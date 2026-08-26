using System.IO.Compression;
using System.Security.Cryptography;

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
        if (IsAlreadyInstalled(state, target, targetRelease.ManifestSha256))
        {
            _log($"Already on Kewz's Cobblemon {target.Version}.");
            Report(UpdatePhase.Complete, "You’re up to date — starting Minecraft.");
            return;
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
        if (manifest.SchemaVersion != 1
            || !string.IsNullOrWhiteSpace(state.Version)
            || !string.IsNullOrWhiteSpace(state.ManifestSha256)
            || state.ManagedFiles.Count != 0)
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
        foreach (string cleanupPath in manifest.DeletePaths
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
            }).ToList()
        };
        await LocalStateStore.SaveStateAsync(_paths, adoptedState, cancellationToken);
        return true;
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

    private bool StateMatchesManifestAndSizes(InstalledState state, UpdateManifest manifest)
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
            if (!File.Exists(target) || new FileInfo(target).Length != manifestFile.Size)
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
        CancellationToken cancellationToken)
    {
        string workDirectory = Path.Combine(_paths.LocalDataDirectory, "staging", manifestHash);
        string partsDirectory = Path.Combine(workDirectory, "parts");
        string archivePath = Path.Combine(workDirectory, "payload.zip");
        string extractDirectory = Path.Combine(workDirectory, "extracted");
        if (manifest.Payload is not null)
        {
            Directory.CreateDirectory(partsDirectory);
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
        await ApplyTransactionAsync(manifest, manifestHash, extractDirectory, previousState, signedBase, cancellationToken);
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

    private static async Task ExtractAndVerifyAsync(
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
            await using Stream input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
            await output.FlushAsync(cancellationToken);
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
        CancellationToken cancellationToken)
    {
        if (manifest.SchemaVersion == 2 && signedBase is null)
        {
            throw new InvalidDataException("Delta transaction is missing its signed base manifest.");
        }
        string transactionId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
        string backupDirectory = Path.Combine(_paths.LocalDataDirectory, "rollback", transactionId, "files");
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
            }).ToList()
        };
        var journal = new TransactionJournal
        {
            PreviousState = previousState,
            NextState = newState
        };
        await TransactionStore.SaveAsync(_paths, journal, cancellationToken);

        try
        {
            IReadOnlyCollection<ManifestFile> payloadFiles = ManifestParser.PayloadContents(manifest);
            Dictionary<string, ManifestFile>? signedBaseFiles = signedBase?.Files.ToDictionary(
                file => file.Path,
                StringComparer.OrdinalIgnoreCase);
            int appliedFiles = 0;
            int totalFiles = payloadFiles.Count + manifest.DeletedFiles.Count;
            foreach (ManifestFile file in payloadFiles)
            {
                Report(UpdatePhase.Applying, "Installing verified files", 0, 0, appliedFiles, totalFiles);
                string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, file.Path);
                string source = PathSafety.CombineUnder(extractDirectory, file.Path);
                string backup = PathSafety.CombineUnder(backupDirectory, file.Path);
                PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (signedBase is not null)
                {
                    await ValidateDeltaMutationTargetAsync(file.Path, signedBase, cancellationToken);
                }

                TransactionOperation operation;
                if (File.Exists(target))
                {
                    ManifestFile? expectedOriginal = null;
                    signedBaseFiles?.TryGetValue(file.Path, out expectedOriginal);
                    if (signedBaseFiles is not null && expectedOriginal is null)
                    {
                        throw new InvalidDataException($"New delta target appeared immediately before mutation: {file.Path}");
                    }
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

            if (manifest.SchemaVersion == 1)
            {
                await ApplySchemaOneDeletesAsync(manifest, previousState, backupDirectory, journal, cancellationToken);
            }
            else
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
            await ApplyLegacyCleanupAsync(manifest.LegacyCleanup, backupDirectory, journal, cancellationToken);

            if (signedBase is not null)
            {
                await ValidateDeltaPostStateBeforeCommitAsync(manifest, signedBase, cancellationToken);
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
        var requestedDeletes = manifest.DeletePaths
            .Concat(oldManagedPaths.Except(newManagedPaths, StringComparer.OrdinalIgnoreCase))
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
        string normalized = PathSafety.NormalizeRelativePath(relativePath);
        string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, normalized);
        PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
        ManifestFile? expectedBase = signedBase.Files.FirstOrDefault(
            file => string.Equals(file.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (expectedBase is null)
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new InvalidDataException($"New delta target appeared after base validation: {normalized}");
            }
            return;
        }
        await ValidateExactTargetAsync(target, expectedBase, "Delta target changed after base validation", cancellationToken);
    }

    internal async Task ValidateDeltaPostStateBeforeCommitAsync(
        UpdateManifest delta,
        UpdateManifest signedBase,
        CancellationToken cancellationToken)
    {
        var postFiles = delta.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile baseFile in signedBase.Files)
        {
            if (!postFiles.TryGetValue(baseFile.Path, out ManifestFile? postFile))
            {
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

    private async Task ApplyLegacyCleanupAsync(
        IReadOnlyCollection<LegacyCleanupFile> legacyCleanup,
        string backupDirectory,
        TransactionJournal journal,
        CancellationToken cancellationToken)
    {
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
            long reusablePartBytes = PrepareReusableStaging(manifest, manifestHash);
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
                backupPaths.UnionWith(previousState.ManagedFiles
                    .Select(file => PathSafety.NormalizeRelativePath(file.Path))
                    .Where(path => !newPaths.Contains(path)));
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
            long additionalLocalRequired = Math.Max(0L, totalLocalRequired - reusablePartBytes);
            AssertAvailableSpace(_paths.LocalDataDirectory, additionalLocalRequired, "update staging and rollback");

            string localRoot = Path.GetPathRoot(Path.GetFullPath(_paths.LocalDataDirectory))!;
            string minecraftRoot = Path.GetPathRoot(Path.GetFullPath(_paths.MinecraftDirectory))!;
            long minecraftRequired = 0L;
            if (!string.Equals(localRoot, minecraftRoot, StringComparison.OrdinalIgnoreCase))
            {
                minecraftRequired = checked(extractedBytes + DiskReserveBytes);
                AssertAvailableSpace(_paths.MinecraftDirectory, minecraftRequired, "Minecraft installation");
            }
            return new StagingPreparation(reusablePartBytes, totalLocalRequired, additionalLocalRequired, minecraftRequired);
        }
        catch (OverflowException)
        {
            throw new InvalidDataException("Signed update sizes overflow the updater's disk-space calculation.");
        }
    }

    private long PrepareReusableStaging(UpdateManifest manifest, string manifestHash)
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
        foreach (FileSystemInfo entry in new DirectoryInfo(workDirectory).EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.Name, "parts", StringComparison.OrdinalIgnoreCase)
                && entry is DirectoryInfo)
            {
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
        return reusableBytes;
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
    long TotalLocalRequiredBytes,
    long AdditionalLocalRequiredBytes,
    long MinecraftRequiredBytes);
