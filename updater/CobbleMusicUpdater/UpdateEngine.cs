using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CobbleMusicUpdater;

internal sealed class UpdateEngine
{
    private const long DiskReserveBytes = 256L * 1024 * 1024;

    private readonly UpdaterPaths _paths;
    private readonly UpdaterConfiguration _configuration;
    private readonly Action<string> _log;
    private readonly IProgress<UpdateProgress>? _progress;

    public UpdateEngine(
        UpdaterPaths paths,
        UpdaterConfiguration configuration,
        Action<string> log,
        IProgress<UpdateProgress>? progress = null)
    {
        _paths = paths;
        _configuration = configuration;
        _log = log;
        _progress = progress;
    }

    public async Task CheckAndUpdateAsync(
        IReadOnlyList<RemoteRelease> releaseChain,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        await TransactionStore.RecoverIfNeededAsync(_paths, BuildInfo.SupportedRoots, _log);
        if (releaseChain.Count == 0)
        {
            _log("No applicable published update chain; launching the installed pack.");
            Report(UpdatePhase.Complete, "No applicable update is published — starting Minecraft.");
            return;
        }

        Report(UpdatePhase.VerifyingRelease, "Verifying signed update information…");
        foreach (RemoteRelease release in releaseChain)
        {
            ValidateRemoteRelease(release);
        }

        InstalledState state = LocalStateStore.LoadState(_paths);
        RemoteRelease targetRelease = releaseChain[^1];
        UpdateManifest target = targetRelease.Manifest;
        bool alreadyInstalled = IsAlreadyInstalled(state, target, targetRelease.ManifestSha256);
        if (alreadyInstalled
            && !await HasPendingCorrectiveWorkAsync(target, state, cancellationToken))
        {
            _log($"Already on Kewz's Cobblemon {target.Version}.");
            Report(UpdatePhase.Complete, "You’re up to date — starting Minecraft.");
            return;
        }
        if (alreadyInstalled)
        {
            _log($"Kewz's Cobblemon {target.Version} is installed, but signed corrective work is still pending.");
        }
        if (IsDowngradeOrMutation(state, target, targetRelease.ManifestSha256))
        {
            _log($"Ignoring non-newer or mutated release {target.Version}; keeping local {state.Version}.");
            Report(UpdatePhase.Complete, "Keeping your installed Kewz's Cobblemon version.");
            return;
        }

        _log($"Update {target.Version} is available through {releaseChain.Count} verified release step(s).");
        Report(UpdatePhase.UpdateAvailable, $"Update found: Kewz's Cobblemon {target.Version}");
        if (checkOnly)
        {
            Report(UpdatePhase.Complete, $"Kewz's Cobblemon {target.Version} is ready to install.");
            return;
        }

        LocalStateStore.AssertWritable(_paths);
        if (await TryAdoptExistingBaselineAsync(target, targetRelease.ManifestSha256, state, cancellationToken))
        {
            _log($"Adopted the exact existing signed release {target.Version}; no payload download was needed.");
            Report(UpdatePhase.Complete, $"Kewz's Cobblemon {target.Version} is ready — starting Minecraft.");
            return;
        }

        if (await CanRepairAndAdoptExistingTargetAsync(target, state, cancellationToken))
        {
            _log($"The managed {target.Version} files are already exact; applying signed cleanup and player-setting migrations only.");
            var adoptionProgress = new DownloadProgressScope(CalculatePayloadDownloadBytes(target));
            PrepareStagingAndEnsureSufficientDiskSpace(target, targetRelease.ManifestSha256, state);
            await DownloadVerifyAndApplyAsync(
                target,
                targetRelease.ManifestSha256,
                targetRelease.AssetUrls,
                state,
                signedBase: null,
                adoptionProgress,
                cancellationToken,
                adoptExistingPostState: true);
            _log($"Reconciled the existing files with signed release {target.Version}.");
            Report(UpdatePhase.Complete, $"Kewz's Cobblemon {target.Version} installed — starting Minecraft.");
            return;
        }

        UpdateManifest? trustedBase = null;
        string trustedBaseHash = "";
        var downloadProgress = new DownloadProgressScope(CalculateChainDownloadBytes(releaseChain));
        foreach (RemoteRelease release in releaseChain)
        {
            UpdateManifest manifest = release.Manifest;
            if (await TryAdoptExistingBaselineAsync(manifest, release.ManifestSha256, state, cancellationToken))
            {
                downloadProgress.ExcludeSkippedPayload(CalculatePayloadDownloadBytes(manifest));
                state = LocalStateStore.LoadState(_paths);
                trustedBase = manifest;
                trustedBaseHash = release.ManifestSha256;
                _log($"Adopted the exact existing signed baseline {manifest.Version}; no payload download was needed.");
                continue;
            }

            bool sameIdentity = string.Equals(state.Version, manifest.Version, StringComparison.Ordinal)
                && string.Equals(state.ManifestSha256, release.ManifestSha256, StringComparison.OrdinalIgnoreCase);
            if (sameIdentity && StateMatchesManifestAndSizes(state, manifest))
            {
                downloadProgress.ExcludeSkippedPayload(CalculatePayloadDownloadBytes(manifest));
                trustedBase = manifest;
                trustedBaseHash = release.ManifestSha256;
                continue;
            }

            if (sameIdentity && manifest.SchemaVersion == 2)
            {
                throw new InvalidDataException(
                    $"Installed delta {manifest.Version} is incomplete and cannot be repaired from its changed-files-only payload. Install a full signed baseline first.");
            }
            if (IsDowngradeOrMutation(state, manifest, release.ManifestSha256))
            {
                throw new InvalidDataException($"Verified update chain is not strictly newer than installed state {state.Version}.");
            }

            if (manifest.SchemaVersion == 2)
            {
                if (trustedBase is null)
                {
                    throw new InvalidDataException($"Delta {manifest.Version} has no verified signed base in the release chain.");
                }
                await DeltaValidator.ValidateBaseAsync(
                    manifest,
                    trustedBase,
                    trustedBaseHash,
                    state,
                    _paths,
                    _configuration,
                    cancellationToken);
            }

            PrepareStagingAndEnsureSufficientDiskSpace(manifest, release.ManifestSha256, state);
            await DownloadVerifyAndApplyAsync(
                manifest,
                release.ManifestSha256,
                release.AssetUrls,
                state,
                manifest.SchemaVersion == 2 ? trustedBase : null,
                downloadProgress,
                cancellationToken);
            state = LocalStateStore.LoadState(_paths);
            trustedBase = manifest;
            trustedBaseHash = release.ManifestSha256;
            _log($"Applied verified release step {manifest.Version}.");
        }

        _log($"Kewz's Cobblemon {target.Version} installed successfully.");
        Report(UpdatePhase.Complete, $"Kewz's Cobblemon {target.Version} installed — starting Minecraft.");
    }

    internal async Task<bool> TryAdoptExistingBaselineAsync(
        UpdateManifest manifest,
        string manifestHash,
        InstalledState state,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(state.Version)
            || !string.IsNullOrWhiteSpace(state.ManifestSha256)
            || state.ManagedFiles.Count != 0
            || state.OfferedSeedPaths.Count != 0
            || state.AppliedPlayerSettingMigrationIds.Count != 0)
        {
            return false;
        }

        // A signed one-time migration must pass through the transactional
        // path even when its current predicates are already false. That is
        // what durably records the migration as inspected and prevents later
        // player choices from making it applicable again.
        if (manifest.SeedTextReplacements.Any(replacement =>
                !string.IsNullOrEmpty(replacement.MigrationId)))
        {
            return false;
        }

        // Adoption is deliberately all-or-nothing. It is safe only when every
        // signed file already has exact content and there is no pending named
        // cleanup whose omission would leave the instance non-canonical.
        foreach (ManifestFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, file.Path);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (!File.Exists(target)
                || new FileInfo(target).Length != file.Size
                || !PathSafety.IsExpectedHash(await PathSafety.Sha256Async(target, cancellationToken), file.Sha256))
            {
                return false;
            }
        }
        // A blank-state adoption must prove every supplied create-only default
        // was actually initialized. Otherwise adoption would mark a missing
        // seed as offered without ever installing it.
        foreach (string seedPath in manifest.SeedFiles.Select(file => file.Path))
        {
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, seedPath);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (!File.Exists(target))
            {
                return false;
            }
        }
        foreach (SeedTextReplacement replacement in manifest.SeedTextReplacements)
        {
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, replacement.Path);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (await ContainsApplicableSeedTextAsync(target, replacement, cancellationToken))
            {
                return false;
            }
        }
        foreach (string cleanupPath in manifest.DeletePaths
            .Concat(manifest.DeletedFiles.Select(file => file.Path))
            .Concat(manifest.LegacyCleanup.Select(file => file.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, cleanupPath);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (File.Exists(target) || Directory.Exists(target))
            {
                return false;
            }
        }

        var adoptedState = new InstalledState
        {
            Version = manifest.Version,
            ManifestSha256 = manifestHash,
            AppliedAtUtc = DateTimeOffset.UtcNow,
            ManagedFiles = manifest.Files.Select(file => new ManagedFileState
            {
                Path = file.Path,
                Size = file.Size,
                Sha256 = file.Sha256
            }).ToList(),
            OfferedSeedPaths = manifest.SeedFiles
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            AppliedPlayerSettingMigrationIds = []
        };
        await LocalStateStore.SaveStateAsync(_paths, adoptedState, cancellationToken);
        return true;
    }

    internal async Task<bool> CanRepairAndAdoptExistingTargetAsync(
        UpdateManifest manifest,
        InstalledState state,
        CancellationToken cancellationToken)
    {
        foreach (ManifestFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, file.Path);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (!File.Exists(target)
                || new FileInfo(target).Length != file.Size
                || !PathSafety.IsExpectedHash(await PathSafety.Sha256Async(target, cancellationToken), file.Sha256))
            {
                return false;
            }
        }

        foreach (string path in manifest.DeletePaths.Concat(manifest.DeletedFiles.Select(file => file.Path)))
        {
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, path);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (File.Exists(target) || Directory.Exists(target))
            {
                return false;
            }
        }
        return true;
    }

    internal async Task<bool> HasPendingCorrectiveWorkAsync(
        UpdateManifest manifest,
        InstalledState state,
        CancellationToken cancellationToken)
    {
        var offeredSeeds = state.OfferedSeedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var appliedMigrationIds = state.AppliedPlayerSettingMigrationIds.ToHashSet(StringComparer.Ordinal);
        var reofferedSeeds = manifest.ReofferSeedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seedFiles = manifest.SeedFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile seed in manifest.SeedFiles)
        {
            bool shouldExist = reofferedSeeds.Contains(seed.Path) || !offeredSeeds.Contains(seed.Path);
            if (!shouldExist)
            {
                continue;
            }
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, seed.Path);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                return true;
            }
        }
        foreach (SeedTextReplacement replacement in manifest.SeedTextReplacements)
        {
            if (!string.IsNullOrEmpty(replacement.MigrationId))
            {
                if (!appliedMigrationIds.Contains(replacement.MigrationId))
                {
                    return true;
                }
                continue;
            }
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, replacement.Path);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (await ContainsApplicableSeedTextAsync(target, replacement, cancellationToken))
            {
                return true;
            }
        }
        foreach (IGrouping<string, LegacyCleanupFile> identities in manifest.LegacyCleanup.GroupBy(
            file => file.Path,
            StringComparer.OrdinalIgnoreCase))
        {
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, identities.Key);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (!File.Exists(target) || Directory.Exists(target))
            {
                continue;
            }
            var info = new FileInfo(target);
            string actualHash = await PathSafety.Sha256Async(target, cancellationToken);
            if (seedFiles.TryGetValue(identities.Key, out ManifestFile? currentSeed)
                && currentSeed.Size == info.Length
                && PathSafety.IsExpectedHash(actualHash, currentSeed.Sha256))
            {
                // Some ownership transitions intentionally keep identical
                // bytes. Once the signed target state is installed, the exact
                // current seed is not stale merely because an accepted old
                // identity has the same hash.
                continue;
            }
            foreach (LegacyCleanupFile identity in identities.Where(identity => identity.Size == info.Length))
            {
                if (PathSafety.IsExpectedHash(actualHash, identity.Sha256))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void ValidateRemoteRelease(RemoteRelease release)
    {
        UpdateManifest parsed = ManifestParser.VerifyAndParse(release.ManifestBytes, release.SignatureBytes);
        ManifestParser.Validate(parsed, _configuration, release.AssetUrls);
        string actualHash = Convert.ToHexString(SHA256.HashData(release.ManifestBytes)).ToLowerInvariant();
        if (!string.Equals(parsed.ReleaseTag, release.Release.TagName, StringComparison.Ordinal)
            || !string.Equals(parsed.Version, release.Manifest.Version, StringComparison.Ordinal)
            || !string.Equals(actualHash, release.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Verified release-chain metadata changed after selection.");
        }
    }

    private bool IsAlreadyInstalled(InstalledState state, UpdateManifest manifest, string manifestHash) =>
        string.Equals(state.Version, manifest.Version, StringComparison.Ordinal)
        && string.Equals(state.ManifestSha256, manifestHash, StringComparison.OrdinalIgnoreCase)
        && StateMatchesManifestAndSizes(state, manifest);

    internal bool StateMatchesManifestAndSizes(InstalledState state, UpdateManifest manifest)
    {
        if (state.ManagedFiles.Count != manifest.Files.Count)
        {
            return false;
        }
        Dictionary<string, ManagedFileState> recordedFiles;
        try
        {
            recordedFiles = state.ManagedFiles.ToDictionary(file => PathSafety.NormalizeRelativePath(file.Path), StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        foreach (ManifestFile manifestFile in manifest.Files)
        {
            if (!recordedFiles.TryGetValue(manifestFile.Path, out ManagedFileState? recorded)
                || !ManifestParser.SameFile(recorded, manifestFile))
            {
                return false;
            }
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, manifestFile.Path);
            if (!PathSafety.IsOptionalPlayerMod(manifestFile.Path)
                && (!File.Exists(target) || new FileInfo(target).Length != manifestFile.Size))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDowngradeOrMutation(InstalledState state, UpdateManifest manifest, string manifestHash)
    {
        if (!VersionPolicy.TryParseCanonical(state.Version, out Version? current)
            || !VersionPolicy.TryParseCanonical(manifest.Version, out Version? remote))
        {
            return false;
        }
        return remote < current || (remote == current && !string.Equals(state.ManifestSha256, manifestHash, StringComparison.OrdinalIgnoreCase));
    }

    private async Task DownloadVerifyAndApplyAsync(
        UpdateManifest manifest,
        string manifestHash,
        IReadOnlyDictionary<string, Uri> assetUrls,
        InstalledState previousState,
        UpdateManifest? signedBase,
        DownloadProgressScope downloadProgress,
        CancellationToken cancellationToken,
        bool adoptExistingPostState = false)
    {
        string workDirectory = Path.Combine(_paths.LocalDataDirectory, "staging", manifestHash);
        string partsDirectory = Path.Combine(workDirectory, "parts");
        string archivePath = Path.Combine(workDirectory, "payload.zip");
        string extractDirectory = Path.Combine(workDirectory, "extracted");
        if (manifest.Payload is not null)
        {
            Directory.CreateDirectory(partsDirectory);
            if (File.Exists(archivePath))
            {
                _log("Checking the previously assembled update payload...");
                Report(UpdatePhase.Validating, "Checking cached update…");
            }
            bool reusedArchive = await TryReuseVerifiedArchiveAsync(
                archivePath,
                manifest.Payload,
                cancellationToken);
            if (reusedArchive)
            {
                downloadProgress.ExcludeSkippedPayload(CalculatePayloadDownloadBytes(manifest));
                _log("Reusing the previously assembled and verified update payload...");
                // Preparation has already established that this is an
                // updater-owned, non-reparse directory. Reclaim every cached
                // part before extraction; failing closed keeps the disk-space
                // calculation valid instead of silently retaining gigabytes.
                Directory.Delete(partsDirectory, recursive: true);
            }
            else
            {
                using var releaseClient = new ReleaseClient(TimeSpan.FromSeconds(_configuration.NetworkTimeoutSeconds));
                foreach (PayloadPart part in manifest.Payload.Parts)
                {
                    string partPath = Path.Combine(partsDirectory, part.Name);
                    _log($"Downloading {part.Name}...");
                    Report(UpdatePhase.Downloading, "Downloading update", downloadProgress.CompletedBytes, downloadProgress.TotalBytes, networkBytes: downloadProgress.NetworkBytes);
                    long partNetworkBytes = await DownloadVerifiedPartAsync(
                        releaseClient,
                        assetUrls[part.Name],
                        part,
                        partPath,
                        partProgress => Report(downloadProgress.ForPart(partProgress)),
                        cancellationToken);
                    downloadProgress.CompletePart(part.Size, partNetworkBytes);
                }

                _log("Reassembling verified update payload...");
                Report(UpdatePhase.Reassembling, "Reassembling verified update…");
                await CombinePartsAndDeleteAsync(manifest.Payload.Parts, partsDirectory, archivePath, cancellationToken);
                await VerifyFileAsync(archivePath, manifest.Payload.Size, manifest.Payload.Sha256, "update payload", cancellationToken);
            }

            _log("Validating update archive...");
            Report(UpdatePhase.Validating, "Validating update files…");
            if (Directory.Exists(extractDirectory))
            {
                Directory.Delete(extractDirectory, recursive: true);
            }
            Directory.CreateDirectory(extractDirectory);
            await ExtractAndVerifyAsync(
                archivePath,
                extractDirectory,
                ManifestParser.PayloadContents(manifest),
                cancellationToken);
        }
        else
        {
            // A deletion-only schema-v2 release has no payload assets.
            Directory.CreateDirectory(extractDirectory);
        }

        _log("Applying verified update...");
        IReadOnlyCollection<ManifestFile> payloadFiles = ManifestParser.PayloadContents(manifest);
        Report(UpdatePhase.Applying, "Installing verified files", 0, 0, 0, payloadFiles.Count);
        await ApplyTransactionAsync(
            manifest,
            manifestHash,
            extractDirectory,
            previousState,
            signedBase,
            cancellationToken,
            adoptExistingPostState);
        TryDeleteDirectory(workDirectory);
    }

    private async Task<long> DownloadVerifiedPartAsync(
        ReleaseClient client,
        Uri source,
        PayloadPart part,
        string destination,
        Action<PartDownloadProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        long networkBytes = 0;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            long observedDownloadedBytes = File.Exists(destination)
                ? Math.Min(new FileInfo(destination).Length, part.Size)
                : 0L;
            await client.DownloadFileAsync(
                source,
                destination,
                part.Size,
                downloadedBytes =>
                {
                    if (downloadedBytes >= observedDownloadedBytes)
                    {
                        networkBytes = checked(networkBytes + downloadedBytes - observedDownloadedBytes);
                    }
                    observedDownloadedBytes = downloadedBytes;
                    reportProgress?.Invoke(new PartDownloadProgress(downloadedBytes, networkBytes));
                },
                cancellationToken);
            string actualHash = await PathSafety.Sha256Async(destination, cancellationToken);
            if (PathSafety.IsExpectedHash(actualHash, part.Sha256))
            {
                return networkBytes;
            }
            File.Delete(destination);
        }
        throw new InvalidDataException($"Checksum verification failed for {part.Name}.");
    }

    internal static UpdateProgress CreateDownloadProgress(
        long completedBeforePart,
        long totalDownloadBytes,
        long networkBeforePart,
        PartDownloadProgress partProgress) =>
        new(
            UpdatePhase.Downloading,
            "Downloading update",
            checked(completedBeforePart + partProgress.DownloadedBytes),
            totalDownloadBytes,
            NetworkBytes: checked(networkBeforePart + partProgress.NetworkBytes));

    internal static long CalculatePayloadDownloadBytes(UpdateManifest manifest) =>
        manifest.Payload is null
            ? 0L
            : checked(manifest.Payload.Parts.Sum(part => part.Size));

    internal static long CalculateChainDownloadBytes(IEnumerable<RemoteRelease> releaseChain) =>
        checked(releaseChain.Sum(release => CalculatePayloadDownloadBytes(release.Manifest)));

    private static async Task CombinePartsAndDeleteAsync(
        IReadOnlyCollection<PayloadPart> parts,
        string partsDirectory,
        string archivePath,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        foreach (PayloadPart part in parts)
        {
            string partPath = Path.Combine(partsDirectory, part.Name);
            await using (var source = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
            {
                await source.CopyToAsync(destination, 1024 * 1024, cancellationToken);
            }
            // Keeping consumed chunks while also growing the archive doubles
            // peak disk use. A failed combine can safely redownload a chunk.
            File.Delete(partPath);
        }
        await destination.FlushAsync(cancellationToken);
    }

    private static async Task VerifyFileAsync(
        string filePath,
        long expectedSize,
        string expectedHash,
        string item,
        CancellationToken cancellationToken)
    {
        long actualSize = new FileInfo(filePath).Length;
        if (actualSize != expectedSize)
        {
            throw new InvalidDataException($"{item} size mismatch. Expected {expectedSize:N0}, got {actualSize:N0} bytes.");
        }
        string actualHash = await PathSafety.Sha256Async(filePath, cancellationToken);
        if (!PathSafety.IsExpectedHash(actualHash, expectedHash))
        {
            throw new InvalidDataException($"{item} checksum mismatch.");
        }
    }

    internal static async Task ExtractAndVerifyAsync(
        string archivePath,
        string extractDirectory,
        IReadOnlyCollection<ManifestFile> expectedFiles,
        CancellationToken cancellationToken)
    {
        var expected = expectedFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                _ = PathSafety.NormalizeRelativePath(entry.FullName.TrimEnd('/'));
                continue;
            }

            int unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000)
            {
                throw new InvalidDataException("Update archive contains a symbolic link.");
            }

            string relativePath = PathSafety.NormalizeRelativePath(entry.FullName);
            if (!expected.TryGetValue(relativePath, out ManifestFile? expectedFile)
                || !extracted.Add(relativePath)
                || entry.Length != expectedFile.Size)
            {
                throw new InvalidDataException($"Update archive contains an unexpected, duplicate, or size-mismatched file: {entry.FullName}");
            }

            string destination = PathSafety.CombineUnder(extractDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (Stream input = entry.Open())
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            await VerifyFileAsync(destination, expectedFile.Size, expectedFile.Sha256, relativePath, cancellationToken);
        }

        if (extracted.Count != expected.Count)
        {
            throw new InvalidDataException("Update archive is missing one or more signed payload files.");
        }
    }

    internal async Task ApplyTransactionAsync(
        UpdateManifest manifest,
        string manifestHash,
        string extractDirectory,
        InstalledState previousState,
        UpdateManifest? signedBase,
        CancellationToken cancellationToken,
        bool adoptExistingPostState = false)
    {
        if (manifest.SchemaVersion == 2 && signedBase is null && !adoptExistingPostState)
        {
            throw new InvalidDataException("Delta transaction is missing its signed base manifest.");
        }
        string transactionId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
        string backupDirectory = Path.Combine(_paths.LocalDataDirectory, "rollback", transactionId, "files");
        bool hasPreviousIdentity = !string.IsNullOrWhiteSpace(previousState.Version)
            || !string.IsNullOrWhiteSpace(previousState.ManifestSha256)
            || previousState.ManagedFiles.Count != 0;
        var previouslyOfferedSeeds = previousState.OfferedSeedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previouslyAppliedMigrationIds = previousState.AppliedPlayerSettingMigrationIds.ToHashSet(StringComparer.Ordinal);
        var reofferSeedPaths = manifest.ReofferSeedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (signedBase is not null)
        {
            // State written by 1.2.6/1.2.7 has no seed ledger. A signed delta
            // base proves those defaults were already offered under the old
            // create-only policy, so deleted optional/default files stay gone.
            previouslyOfferedSeeds.UnionWith(signedBase.SeedFiles.Select(file => file.Path).Where(PathSafety.IsSeedAllowed));
        }
        var nextOfferedSeeds = previouslyOfferedSeeds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        nextOfferedSeeds.UnionWith(manifest.SeedFiles.Select(file => file.Path).Where(PathSafety.IsSeedAllowed));
        var nextAppliedMigrationIds = previouslyAppliedMigrationIds.ToHashSet(StringComparer.Ordinal);
        nextAppliedMigrationIds.UnionWith(manifest.SeedTextReplacements
            .Select(replacement => replacement.MigrationId)
            .Where(migrationId => !string.IsNullOrEmpty(migrationId)));
        var newState = new InstalledState
        {
            Version = manifest.Version,
            ManifestSha256 = manifestHash,
            AppliedAtUtc = DateTimeOffset.UtcNow,
            ManagedFiles = manifest.Files.Select(file => new ManagedFileState
            {
                Path = file.Path,
                Size = file.Size,
                Sha256 = file.Sha256
            }).ToList(),
            OfferedSeedPaths = nextOfferedSeeds
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            AppliedPlayerSettingMigrationIds = nextAppliedMigrationIds
                .OrderBy(migrationId => migrationId, StringComparer.Ordinal)
                .ToList()
        };
        var journal = new TransactionJournal
        {
            PreviousState = previousState,
            NextState = newState
        };
        await TransactionStore.SaveAsync(_paths, journal, cancellationToken);

        try
        {
            IReadOnlyCollection<ManifestFile> payloadFiles = adoptExistingPostState
                ? Array.Empty<ManifestFile>()
                : ManifestParser.ManagedPayloadContents(manifest);
            int appliedFiles = 0;
            int totalFiles = payloadFiles.Count + manifest.SeedFiles.Count + manifest.DeletedFiles.Count;
            foreach (ManifestFile file in payloadFiles)
            {
                Report(UpdatePhase.Applying, "Installing verified files", 0, 0, appliedFiles, totalFiles);
                string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, file.Path);
                string source = PathSafety.CombineUnder(extractDirectory, file.Path);
                string backup = PathSafety.CombineUnder(backupDirectory, file.Path);
                PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                ManifestFile? expectedOriginal = signedBase is null
                    ? null
                    : await ResolveDeltaMutationOriginalAsync(
                        file.Path,
                        signedBase,
                        manifest.LegacyCleanup,
                        allowExactLegacyRepair: true,
                        cancellationToken,
                        incomingFile: file);

                TransactionOperation operation;
                if (File.Exists(target))
                {
                    operation = await BackupForOperationAsync("replace", target, backup, journal, cancellationToken, expectedOriginal);
                }
                else
                {
                    if (Directory.Exists(target))
                    {
                        throw new InvalidDataException($"Delta file target became a directory immediately before mutation: {file.Path}");
                    }
                    operation = new TransactionOperation
                    {
                        Kind = "create",
                        TargetPath = target,
                        TargetTemporaryPath = TransactionStore.CreateSiblingTemporaryPath(target)
                    };
                    journal.Operations.Add(operation);
                    await TransactionStore.SaveAsync(_paths, journal, cancellationToken);
                }

                // The target was either moved into the journaled rollback
                // area or proved absent. Never overwrite a file that appears
                // in the remaining race window.
                await TransactionStore.MoveOrCopyNewAsync(
                    source,
                    target,
                    operation.TargetTemporaryPath,
                    file.Size,
                    file.Sha256,
                    cancellationToken);
                await VerifyFileAsync(target, file.Size, file.Sha256, file.Path, cancellationToken);
                appliedFiles++;
                Report(UpdatePhase.Applying, "Installing verified files", 0, 0, appliedFiles, totalFiles);
            }

            await ApplySeedTextReplacementsAsync(
                manifest.SeedTextReplacements,
                previouslyAppliedMigrationIds,
                backupDirectory,
                journal,
                cancellationToken);
            await ApplyLegacyCleanupAsync(manifest.LegacyCleanup, backupDirectory, journal, cancellationToken);

            foreach (ManifestFile seedFile in manifest.SeedFiles)
            {
                if (!PathSafety.IsSeedAllowed(seedFile.Path))
                {
                    _log($"Skipping retired historical default: {seedFile.Path}");
                    appliedFiles++;
                    continue;
                }
                Report(UpdatePhase.Applying, "Installing first-run defaults", 0, 0, appliedFiles, totalFiles);
                string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, seedFile.Path);
                string source = PathSafety.CombineUnder(extractDirectory, seedFile.Path);
                PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
                if (File.Exists(target))
                {
                    _log($"Keeping player-owned setting: {seedFile.Path}");
                    appliedFiles++;
                    continue;
                }
                if (Directory.Exists(target))
                {
                    if (!hasPreviousIdentity)
                    {
                        throw new InvalidDataException($"Create-only default target is a directory: {seedFile.Path}");
                    }
                    _log($"Keeping player-owned path: {seedFile.Path}");
                    appliedFiles++;
                    continue;
                }
                bool canInitialize = !hasPreviousIdentity
                    || (manifest.SchemaVersion == 2
                        && (!previouslyOfferedSeeds.Contains(seedFile.Path)
                            || reofferSeedPaths.Contains(seedFile.Path)));
                if (!canInitialize)
                {
                    _log($"Keeping player-owned absence: {seedFile.Path}");
                    appliedFiles++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                journal.SeedFiles.Add(new ManagedFileState
                {
                    Path = seedFile.Path,
                    Size = seedFile.Size,
                    Sha256 = seedFile.Sha256
                });
                var operation = new TransactionOperation
                {
                    Kind = "create",
                    TargetPath = target,
                    TargetTemporaryPath = TransactionStore.CreateSiblingTemporaryPath(target)
                };
                journal.Operations.Add(operation);
                await TransactionStore.SaveAsync(_paths, journal, cancellationToken);
                await TransactionStore.MoveOrCopyNewAsync(
                    source,
                    target,
                    operation.TargetTemporaryPath,
                    seedFile.Size,
                    seedFile.Sha256,
                    cancellationToken);
                await VerifyFileAsync(target, seedFile.Size, seedFile.Sha256, seedFile.Path, cancellationToken);
                _log($"Initialized player-owned setting: {seedFile.Path}");
                appliedFiles++;
            }

            if (manifest.SchemaVersion == 1)
            {
                await ApplySchemaOneDeletesAsync(manifest, previousState, backupDirectory, journal, cancellationToken);
            }
            else if (!adoptExistingPostState)
            {
                foreach (ManifestFile deletedFile in manifest.DeletedFiles)
                {
                    string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, deletedFile.Path);
                    if (signedBase is null)
                    {
                        throw new InvalidDataException("Delta transaction is missing its signed base manifest.");
                    }
                    await ValidateDeltaMutationTargetAsync(deletedFile.Path, signedBase, cancellationToken);
                    string backup = PathSafety.CombineUnder(backupDirectory, deletedFile.Path);
                    PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
                    await BackupForOperationAsync("delete", target, backup, journal, cancellationToken, deletedFile);
                    appliedFiles++;
                    Report(UpdatePhase.Applying, "Installing verified files", 0, 0, appliedFiles, totalFiles);
                }
            }
            if (signedBase is not null)
            {
                await ValidateDeltaPostStateBeforeCommitAsync(manifest, signedBase, cancellationToken);
            }
            else if (adoptExistingPostState)
            {
                await ValidateAdoptedPostStateBeforeCommitAsync(manifest, cancellationToken);
            }

            journal.Phase = "filesApplied";
            await TransactionStore.SaveAsync(_paths, journal, cancellationToken);
            await LocalStateStore.SaveStateAsync(_paths, newState, cancellationToken);
            File.Delete(TransactionStore.JournalPath(_paths));
            TryDeleteDirectory(Path.GetDirectoryName(backupDirectory)!);
        }
        catch
        {
            await TransactionStore.RecoverIfNeededAsync(_paths, BuildInfo.SupportedRoots, _log);
            throw;
        }
    }

    private async Task ApplySchemaOneDeletesAsync(
        UpdateManifest manifest,
        InstalledState previousState,
        string backupDirectory,
        TransactionJournal journal,
        CancellationToken cancellationToken)
    {
        var oldManagedPaths = previousState.ManagedFiles
            .Select(file => PathSafety.NormalizeRelativePath(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newManagedPaths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seedPaths = manifest.SeedFiles.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedDeletes = manifest.DeletePaths
            .Concat(oldManagedPaths.Except(newManagedPaths, StringComparer.OrdinalIgnoreCase))
            .Where(path => !seedPaths.Contains(path) && !PathSafety.IsOptionalPlayerMod(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string requestedDeletePath in requestedDeletes)
        {
            if (!oldManagedPaths.Contains(requestedDeletePath) || newManagedPaths.Contains(requestedDeletePath))
            {
                continue;
            }
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, requestedDeletePath);
            if (!File.Exists(target))
            {
                continue;
            }
            string backup = PathSafety.CombineUnder(backupDirectory, requestedDeletePath);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            await BackupForOperationAsync("delete", target, backup, journal, cancellationToken);
        }

    }

    internal async Task ValidateDeltaMutationTargetAsync(
        string relativePath,
        UpdateManifest signedBase,
        CancellationToken cancellationToken)
    {
        _ = await ResolveDeltaMutationOriginalAsync(
            relativePath,
            signedBase,
            Array.Empty<LegacyCleanupFile>(),
            allowExactLegacyRepair: false,
            cancellationToken);
    }

    private async Task<ManifestFile?> ResolveDeltaMutationOriginalAsync(
        string relativePath,
        UpdateManifest signedBase,
        IReadOnlyCollection<LegacyCleanupFile> legacyCleanup,
        bool allowExactLegacyRepair,
        CancellationToken cancellationToken,
        ManifestFile? incomingFile = null)
    {
        string normalized = PathSafety.NormalizeRelativePath(relativePath);
        string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, normalized);
        PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
        ManifestFile? expectedBase = signedBase.Files.FirstOrDefault(
            file => string.Equals(file.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (expectedBase is null)
        {
            // Older mrpacks already included some files first tracked in 1.0.7.
            // Adopt only the exact signed incoming bytes; unknown copies remain
            // protected. Backup/revalidation and rollback still apply normally.
            if (incomingFile is not null && File.Exists(target))
            {
                await ValidateExactTargetAsync(target, incomingFile,
                    "Pre-existing new delta target differs from signed payload", cancellationToken);
                return incomingFile;
            }
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new InvalidDataException($"New delta target appeared after base validation: {normalized}");
            }
            return null;
        }
        if (!File.Exists(target) || Directory.Exists(target))
        {
            throw new InvalidDataException($"Delta target changed after base validation: {normalized}");
        }
        long actualSize = new FileInfo(target).Length;
        string actualHash = await PathSafety.Sha256Async(target, cancellationToken);
        if (actualSize == expectedBase.Size && PathSafety.IsExpectedHash(actualHash, expectedBase.Sha256))
        {
            return expectedBase;
        }
        if (allowExactLegacyRepair)
        {
            LegacyCleanupFile? repairIdentity = legacyCleanup.FirstOrDefault(identity =>
                string.Equals(identity.Path, normalized, StringComparison.OrdinalIgnoreCase)
                && identity.Size == actualSize
                && PathSafety.IsExpectedHash(actualHash, identity.Sha256));
            if (repairIdentity is not null)
            {
                return new ManifestFile
                {
                    Path = expectedBase.Path,
                    Size = repairIdentity.Size,
                    Sha256 = repairIdentity.Sha256
                };
            }
        }
        throw new InvalidDataException($"Delta target changed after base validation: {normalized}");
    }

    internal async Task ValidateDeltaPostStateBeforeCommitAsync(
        UpdateManifest delta,
        UpdateManifest signedBase,
        CancellationToken cancellationToken)
    {
        var postFiles = delta.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var seedFiles = delta.SeedFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        ILookup<string, LegacyCleanupFile> legacyCleanup = delta.LegacyCleanup.ToLookup(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var reofferSeedPaths = delta.ReofferSeedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile baseFile in signedBase.Files)
        {
            if (!postFiles.TryGetValue(baseFile.Path, out ManifestFile? postFile))
            {
                bool becamePlayerOwned = seedFiles.ContainsKey(baseFile.Path)
                    && reofferSeedPaths.Contains(baseFile.Path)
                    && legacyCleanup[baseFile.Path].Any(transitionIdentity =>
                        transitionIdentity.Size == baseFile.Size
                        && string.Equals(transitionIdentity.Sha256, baseFile.Sha256, StringComparison.OrdinalIgnoreCase));
                if (becamePlayerOwned)
                {
                    continue;
                }
                string deletedTarget = PathSafety.CombineUnder(_paths.MinecraftDirectory, baseFile.Path);
                PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, deletedTarget);
                if (File.Exists(deletedTarget) || Directory.Exists(deletedTarget))
                {
                    throw new InvalidDataException($"Deleted delta target reappeared before commit: {baseFile.Path}");
                }
                continue;
            }

            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, postFile.Path);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            await ValidateExactTargetAsync(
                target,
                postFile,
                ManifestParser.SameFile(baseFile, postFile)
                    ? "Unchanged base file changed before delta commit"
                    : "Installed delta file changed before commit",
                cancellationToken);
        }

        // New files have no base entry, so validate them separately.
        var basePaths = signedBase.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile newFile in delta.Files.Where(file => !basePaths.Contains(file.Path)))
        {
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, newFile.Path);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            await ValidateExactTargetAsync(target, newFile, "Installed delta file changed before commit", cancellationToken);
        }
    }

    private async Task ValidateAdoptedPostStateBeforeCommitAsync(
        UpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (ManifestFile file in manifest.Files)
        {
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, file.Path);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            await ValidateExactTargetAsync(target, file, "Adopted managed file changed before commit", cancellationToken);
        }
        foreach (string path in manifest.DeletePaths.Concat(manifest.DeletedFiles.Select(file => file.Path)))
        {
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, path);
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new InvalidDataException($"Deleted target appeared before adoption commit: {path}");
            }
        }
    }

    private static async Task ValidateExactTargetAsync(
        string target,
        ManifestFile expected,
        string context,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(target)
            || Directory.Exists(target)
            || new FileInfo(target).Length != expected.Size
            || !PathSafety.IsExpectedHash(await PathSafety.Sha256Async(target, cancellationToken), expected.Sha256))
        {
            throw new InvalidDataException($"{context}: {expected.Path}");
        }
    }

    private async Task ApplySeedTextReplacementsAsync(
        IReadOnlyCollection<SeedTextReplacement> replacements,
        IReadOnlySet<string> appliedMigrationIds,
        string backupDirectory,
        TransactionJournal journal,
        CancellationToken cancellationToken)
    {
        foreach (IGrouping<string, SeedTextReplacement> legacyGroup in replacements
            .Where(replacement => string.IsNullOrEmpty(replacement.MigrationId))
            .GroupBy(replacement => replacement.Path, StringComparer.OrdinalIgnoreCase))
        {
            await ApplySeedTextReplacementGroupAsync(
                legacyGroup.ToArray(),
                requireUnambiguousGroup: false,
                backupDirectory,
                journal,
                cancellationToken);
        }
        foreach (IGrouping<string, SeedTextReplacement> migrationGroup in replacements
            .Where(replacement => !string.IsNullOrEmpty(replacement.MigrationId)
                && !appliedMigrationIds.Contains(replacement.MigrationId))
            .GroupBy(replacement => replacement.MigrationId, StringComparer.Ordinal))
        {
            await ApplySeedTextReplacementGroupAsync(
                migrationGroup.ToArray(),
                requireUnambiguousGroup: true,
                backupDirectory,
                journal,
                cancellationToken);
        }
    }

    private async Task ApplySeedTextReplacementGroupAsync(
        IReadOnlyCollection<SeedTextReplacement> replacements,
        bool requireUnambiguousGroup,
        string backupDirectory,
        TransactionJournal journal,
        CancellationToken cancellationToken)
    {
        const long maximumTextSeedBytes = 4L * 1024 * 1024;
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        string[] paths = replacements
            .Select(replacement => replacement.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length != 1)
        {
            throw new InvalidDataException("A player-setting migration unexpectedly spans multiple files.");
        }
        string path = paths[0];
        string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, path);
        PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
        if (!File.Exists(target) || Directory.Exists(target))
        {
            return;
        }
        var targetInfo = new FileInfo(target);
        if (targetInfo.Length > maximumTextSeedBytes)
        {
            _log($"Keeping unusually large player-owned setting: {path}");
            return;
        }

        byte[] originalBytes = await File.ReadAllBytesAsync(target, cancellationToken);
        string originalText;
        try
        {
            originalText = strictUtf8.GetString(originalBytes);
        }
        catch (DecoderFallbackException)
        {
            _log($"Keeping non-UTF-8 player-owned setting: {path}");
            return;
        }

        if (requireUnambiguousGroup)
        {
            bool requiredStateMatches = replacements
                .SelectMany(replacement => replacement.RequiredLines)
                .Distinct(StringComparer.Ordinal)
                .All(requiredLine => FindExactLine(originalText, requiredLine).Count == 1);
            string[] involvedSettingPrefixes = replacements
                .SelectMany(replacement => replacement.RequiredLines
                    .Append(replacement.OldText)
                    .Append(replacement.NewText))
                .Select(GetSettingLinePrefix)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            bool hasAmbiguousSetting = involvedSettingPrefixes.Any(prefix =>
                CountLinesWithPrefix(originalText, prefix) > 1);
            if (!requiredStateMatches || hasAmbiguousSetting)
            {
                _log($"Keeping ambiguous or customized player-owned setting: {path}");
                return;
            }
        }

        string migratedText = originalText;
        int appliedReplacementCount = 0;
        foreach (SeedTextReplacement replacement in replacements)
        {
            if (replacement.RequiredLines.Any(requiredLine =>
                    FindExactLine(originalText, requiredLine).Count != 1))
            {
                continue;
            }
            (int matchCount, int firstMatch) = FindExactLine(migratedText, replacement.OldText);
            if (matchCount == 0)
            {
                continue;
            }
            if (matchCount != 1)
            {
                _log($"Keeping ambiguous player-owned setting with repeated legacy value: {path}");
                return;
            }

            migratedText = migratedText[..firstMatch]
                + replacement.NewText
                + migratedText[(firstMatch + replacement.OldText.Length)..];
            appliedReplacementCount++;
        }
        if (appliedReplacementCount == 0)
        {
            return;
        }
        byte[] migratedBytes = strictUtf8.GetBytes(migratedText);
        string migratedHash = Convert.ToHexString(SHA256.HashData(migratedBytes)).ToLowerInvariant();
        journal.SeedFiles.Add(new ManagedFileState
        {
            Path = path,
            Size = migratedBytes.LongLength,
            Sha256 = migratedHash
        });

        string backup = PathSafety.CombineUnder(backupDirectory, path);
        string originalHash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
        var expectedOriginal = new ManifestFile
        {
            Path = path,
            Size = originalBytes.LongLength,
            Sha256 = originalHash
        };
        TransactionOperation operation = await BackupForOperationAsync(
            "replace",
            target,
            backup,
            journal,
            cancellationToken,
            expectedOriginal);
        await WriteDurableTemporaryAsync(operation.TargetTemporaryPath, migratedBytes, cancellationToken);
        await VerifyFileAsync(
            operation.TargetTemporaryPath,
            migratedBytes.LongLength,
            migratedHash,
            path,
            cancellationToken);
        File.Move(operation.TargetTemporaryPath, target);
        await VerifyFileAsync(target, migratedBytes.LongLength, migratedHash, path, cancellationToken);
        _log($"Migrated {appliedReplacementCount} obsolete player-owned setting value(s): {path}");
    }

    private static async Task<bool> ContainsApplicableSeedTextAsync(
        string target,
        SeedTextReplacement replacement,
        CancellationToken cancellationToken)
    {
        const long maximumTextSeedBytes = 4L * 1024 * 1024;
        if (!File.Exists(target) || Directory.Exists(target) || new FileInfo(target).Length > maximumTextSeedBytes)
        {
            return false;
        }
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(target, cancellationToken);
            string text = new UTF8Encoding(false, true).GetString(bytes);
            return FindExactLine(text, replacement.OldText).Count == 1
                && replacement.RequiredLines.All(requiredLine =>
                    FindExactLine(text, requiredLine).Count == 1);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static (int Count, int Index) FindExactLine(string text, string expectedLine)
    {
        int count = 0;
        int firstIndex = -1;
        int lineStart = 0;
        for (int index = 0; index <= text.Length; index++)
        {
            bool endOfText = index == text.Length;
            if (!endOfText && text[index] is not ('\r' or '\n'))
            {
                continue;
            }
            if (text.AsSpan(lineStart, index - lineStart).SequenceEqual(expectedLine.AsSpan()))
            {
                count++;
                if (firstIndex < 0)
                {
                    firstIndex = lineStart;
                }
            }
            if (!endOfText && text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }
            lineStart = index + 1;
        }
        return (count, firstIndex);
    }

    private static string GetSettingLinePrefix(string line)
    {
        int separator = line.IndexOf(':');
        return separator < 0 ? line : line[..(separator + 1)];
    }

    private static int CountLinesWithPrefix(string text, string prefix)
    {
        int count = 0;
        int lineStart = 0;
        for (int index = 0; index <= text.Length; index++)
        {
            bool endOfText = index == text.Length;
            if (!endOfText && text[index] is not ('\r' or '\n'))
            {
                continue;
            }
            if (text.AsSpan(lineStart, index - lineStart).StartsWith(prefix.AsSpan(), StringComparison.Ordinal))
            {
                count++;
            }
            if (!endOfText && text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }
            lineStart = index + 1;
        }
        return count;
    }

    private static async Task WriteDurableTemporaryAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(content, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private async Task ApplyLegacyCleanupAsync(
        IReadOnlyCollection<LegacyCleanupFile> legacyCleanup,
        string backupDirectory,
        TransactionJournal journal,
        CancellationToken cancellationToken)
    {
        var emptiedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LegacyCleanupFile legacyFile in legacyCleanup)
        {
            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, legacyFile.Path);
            if (!File.Exists(target))
            {
                continue;
            }
            PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
            if (new FileInfo(target).Length != legacyFile.Size
                || !PathSafety.IsExpectedHash(await PathSafety.Sha256Async(target, cancellationToken), legacyFile.Sha256))
            {
                _log($"Keeping changed legacy file: {legacyFile.Path}");
                continue;
            }
            string backup = PathSafety.CombineUnder(backupDirectory, legacyFile.Path);
            await BackupForOperationAsync(
                "delete",
                target,
                backup,
                journal,
                cancellationToken,
                new ManifestFile { Path = legacyFile.Path, Size = legacyFile.Size, Sha256 = legacyFile.Sha256 });
            _log($"Removed verified legacy file: {legacyFile.Path}");
            emptiedParents.Add(Path.GetDirectoryName(target)!);
        }
        foreach (string parent in emptiedParents.OrderByDescending(path => path.Length))
        {
            TryDeleteEmptyParents(parent);
        }
    }

    private void TryDeleteEmptyParents(string startDirectory)
    {
        string minecraftRoot = Path.GetFullPath(_paths.MinecraftDirectory).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string? current = Path.GetFullPath(startDirectory);
        while (!string.IsNullOrWhiteSpace(current)
            && !string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), minecraftRoot, StringComparison.OrdinalIgnoreCase)
            && PathSafety.TryGetRelativePathUnder(minecraftRoot, current, out _))
        {
            try
            {
                PathSafety.AssertNoReparsePointsOnTargetPath(minecraftRoot, Path.Combine(current, ".empty-check"));
                Directory.Delete(current, recursive: false);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
            current = Path.GetDirectoryName(current);
        }
    }

    private async Task<TransactionOperation> BackupForOperationAsync(
        string kind,
        string target,
        string backup,
        TransactionJournal journal,
        CancellationToken cancellationToken,
        ManifestFile? expectedOriginal = null)
    {
        var operation = new TransactionOperation
        {
            Kind = kind,
            TargetPath = target,
            BackupPath = backup,
            OriginalSize = expectedOriginal?.Size ?? new FileInfo(target).Length,
            OriginalSha256 = expectedOriginal?.Sha256 ?? await PathSafety.Sha256Async(target, cancellationToken),
            TargetTemporaryPath = TransactionStore.CreateSiblingTemporaryPath(target),
            BackupTemporaryPath = TransactionStore.CreateSiblingTemporaryPath(backup)
        };
        journal.Operations.Add(operation);
        await TransactionStore.SaveAsync(_paths, journal, cancellationToken);

        // For signed delta/legacy entries the journal can be persisted from
        // signed metadata first, allowing this hash to happen immediately
        // before the target is moved. A concurrent replacement is preserved
        // and recovery blocks instead of guessing which bytes to delete.
        if (expectedOriginal is not null)
        {
            await ValidateExactTargetAsync(
                target,
                expectedOriginal,
                "Target changed immediately before updater mutation",
                cancellationToken);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await TransactionStore.MoveOrCopyNewAsync(
            target,
            backup,
            operation.BackupTemporaryPath,
            operation.OriginalSize,
            operation.OriginalSha256,
            cancellationToken);
        await VerifyFileAsync(backup, operation.OriginalSize, operation.OriginalSha256, "rollback backup", cancellationToken);
        return operation;
    }

    internal StagingPreparation PrepareStagingAndEnsureSufficientDiskSpace(
        UpdateManifest manifest,
        string manifestHash,
        InstalledState previousState)
    {
        try
        {
            ReusableStaging reusable = PrepareReusableStaging(manifest, manifestHash);
            long extractedBytes = checked(ManifestParser.PayloadContents(manifest).Sum(file => file.Size));
            var backupPaths = ManifestParser.PayloadContents(manifest)
                .Select(file => file.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (manifest.SchemaVersion == 2)
            {
                backupPaths.UnionWith(manifest.DeletedFiles.Select(file => file.Path));
            }
            else
            {
                HashSet<string> newPaths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                HashSet<string> seedPaths = manifest.SeedFiles.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                backupPaths.UnionWith(previousState.ManagedFiles
                    .Select(file => PathSafety.NormalizeRelativePath(file.Path))
                    .Where(path => !newPaths.Contains(path)
                        && !seedPaths.Contains(path)
                        && !PathSafety.IsOptionalPlayerMod(path)));
                backupPaths.UnionWith(manifest.DeletePaths);
            }
            backupPaths.UnionWith(manifest.LegacyCleanup.Select(file => file.Path));
            long rollbackBytes = 0;
            foreach (string relativePath in backupPaths)
            {
                string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, relativePath);
                if (File.Exists(target))
                {
                    rollbackBytes = checked(rollbackBytes + new FileInfo(target).Length);
                }
            }
            long largestPart = manifest.Payload?.Parts.Count > 0
                ? manifest.Payload.Parts.Max(part => part.Size)
                : 0L;
            long totalLocalRequired = checked(
                (manifest.Payload?.Size ?? 0L)
                + largestPart
                + extractedBytes
                + rollbackBytes
                + DiskReserveBytes);
            long additionalLocalRequired = Math.Max(
                0L,
                totalLocalRequired - checked(reusable.PartBytes + reusable.ArchiveBytes));
            AssertAvailableSpace(_paths.LocalDataDirectory, additionalLocalRequired, "update staging and rollback");

            string localRoot = Path.GetPathRoot(Path.GetFullPath(_paths.LocalDataDirectory))!;
            string minecraftRoot = Path.GetPathRoot(Path.GetFullPath(_paths.MinecraftDirectory))!;
            long minecraftRequired = 0L;
            if (!string.Equals(localRoot, minecraftRoot, StringComparison.OrdinalIgnoreCase))
            {
                minecraftRequired = checked(extractedBytes + DiskReserveBytes);
                AssertAvailableSpace(_paths.MinecraftDirectory, minecraftRequired, "Minecraft installation");
            }
            return new StagingPreparation(
                reusable.PartBytes,
                reusable.ArchiveBytes,
                totalLocalRequired,
                additionalLocalRequired,
                minecraftRequired);
        }
        catch (OverflowException)
        {
            throw new InvalidDataException("Signed update sizes overflow the updater's disk-space calculation.");
        }
    }

    private ReusableStaging PrepareReusableStaging(UpdateManifest manifest, string manifestHash)
    {
        ManifestParser.ValidateHash(manifestHash, "staging manifest identity");
        Directory.CreateDirectory(_paths.LocalDataDirectory);
        AssertSafeDirectory(_paths.LocalDataDirectory);
        string stagingRoot = Path.Combine(_paths.LocalDataDirectory, "staging");
        if (Directory.Exists(stagingRoot))
        {
            AssertSafeDirectory(stagingRoot);
        }
        else if (File.Exists(stagingRoot))
        {
            AssertNotReparsePoint(stagingRoot);
            File.Delete(stagingRoot);
            Directory.CreateDirectory(stagingRoot);
        }
        else
        {
            Directory.CreateDirectory(stagingRoot);
        }
        string workDirectory = PathSafety.CombineUnder(stagingRoot, manifestHash);
        AssertSafeDirectory(stagingRoot);
        if (Directory.Exists(workDirectory))
        {
            AssertSafeDirectory(workDirectory);
        }
        else if (File.Exists(workDirectory))
        {
            AssertNotReparsePoint(workDirectory);
            File.Delete(workDirectory);
            Directory.CreateDirectory(workDirectory);
        }
        else
        {
            Directory.CreateDirectory(workDirectory);
        }

        string partsDirectory = Path.Combine(workDirectory, "parts");
        long reusableArchiveBytes = 0L;
        foreach (FileSystemInfo entry in new DirectoryInfo(workDirectory).EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.Name, "parts", StringComparison.OrdinalIgnoreCase)
                && entry is DirectoryInfo)
            {
                continue;
            }
            if (manifest.Payload is not null
                && string.Equals(entry.Name, "payload.zip", StringComparison.OrdinalIgnoreCase)
                && entry is FileInfo archive
                && archive.Length == manifest.Payload.Size)
            {
                AssertNotReparsePoint(archive.FullName);
                reusableArchiveBytes = archive.Length;
                continue;
            }
            DeleteUpdaterOwnedEntry(entry);
        }
        if (File.Exists(partsDirectory))
        {
            AssertNotReparsePoint(partsDirectory);
            File.Delete(partsDirectory);
        }
        Directory.CreateDirectory(partsDirectory);
        AssertSafeDirectory(partsDirectory);

        Dictionary<string, PayloadPart> expectedParts = (manifest.Payload?.Parts ?? [])
            .ToDictionary(part => part.Name, StringComparer.OrdinalIgnoreCase);
        long reusableBytes = 0L;
        foreach (FileSystemInfo entry in new DirectoryInfo(partsDirectory).EnumerateFileSystemInfos())
        {
            if (entry is not FileInfo file
                || !expectedParts.TryGetValue(entry.Name, out PayloadPart? expected)
                || file.Length > expected.Size)
            {
                DeleteUpdaterOwnedEntry(entry);
                continue;
            }
            AssertNotReparsePoint(file.FullName);
            reusableBytes = checked(reusableBytes + file.Length);
        }
        return new ReusableStaging(reusableBytes, reusableArchiveBytes);
    }

    internal static async Task<bool> TryReuseVerifiedArchiveAsync(
        string archivePath,
        UpdatePayload payload,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
        {
            return false;
        }
        try
        {
            await VerifyFileAsync(
                archivePath,
                payload.Size,
                payload.Sha256,
                "cached update payload",
                cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            File.Delete(archivePath);
            return false;
        }
    }

    private static void DeleteUpdaterOwnedEntry(FileSystemInfo entry)
    {
        AssertNotReparsePoint(entry.FullName);
        if (entry is DirectoryInfo directory)
        {
            foreach (FileSystemInfo child in directory.EnumerateFileSystemInfos())
            {
                DeleteUpdaterOwnedEntry(child);
            }
            directory.Delete();
        }
        else
        {
            entry.Delete();
        }
    }

    private static void AssertSafeDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Updater staging directory disappeared: {path}");
        }
        AssertNotReparsePoint(path);
    }

    private static void AssertNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Refusing to follow a junction or symbolic link in updater staging: {path}");
        }
    }

    private static void AssertAvailableSpace(string path, long requiredBytes, string purpose)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new IOException($"Could not determine the drive for {purpose}.");
        long available = new DriveInfo(root).AvailableFreeSpace;
        if (available < requiredBytes)
        {
            throw new IOException(
                $"Not enough free disk space for {purpose}. Need about {FormatGiB(requiredBytes)}, but only {FormatGiB(available)} is available.");
        }
    }

    private static string FormatGiB(long bytes) => $"{bytes / (1024d * 1024d * 1024d):0.00} GiB";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Report(
        UpdatePhase phase,
        string message,
        long completedBytes = 0,
        long totalBytes = 0,
        int currentItem = 0,
        int totalItems = 0,
        long networkBytes = 0) =>
        _progress?.Report(new UpdateProgress(phase, message, completedBytes, totalBytes, currentItem, totalItems, networkBytes));

    private void Report(UpdateProgress progress) => _progress?.Report(progress);
}

internal sealed record StagingPreparation(
    long ReusablePartBytes,
    long ReusableArchiveBytes,
    long TotalLocalRequiredBytes,
    long AdditionalLocalRequiredBytes,
    long MinecraftRequiredBytes);

internal readonly record struct ReusableStaging(
    long PartBytes,
    long ArchiveBytes);
