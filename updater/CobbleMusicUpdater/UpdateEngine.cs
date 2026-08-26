using System.IO.Compression;
using System.Security.Cryptography;

namespace CobbleMusicUpdater;

internal sealed class UpdateEngine
{
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

    public async Task CheckAndUpdateAsync(RemoteRelease? remoteRelease, bool checkOnly, CancellationToken cancellationToken)
    {
        await TransactionStore.RecoverIfNeededAsync(_paths, BuildInfo.SupportedRoots, _log);
        if (remoteRelease is null)
        {
            _log("No published update release yet; launching the installed pack.");
            Report(UpdatePhase.Complete, "No update is published yet — starting Minecraft.");
            return;
        }

        Report(UpdatePhase.VerifyingRelease, "Verifying signed update information…");
        UpdateManifest manifest = ManifestParser.VerifyAndParse(remoteRelease.ManifestBytes, remoteRelease.SignatureBytes);
        ManifestParser.Validate(manifest, _configuration, remoteRelease.AssetUrls);
        if (!string.Equals(manifest.ReleaseTag, remoteRelease.Release.TagName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Signed manifest release tag does not match the GitHub release that carried it.");
        }
        InstalledState state = LocalStateStore.LoadState(_paths);
        string manifestHash = Convert.ToHexString(SHA256.HashData(remoteRelease.ManifestBytes)).ToLowerInvariant();

        if (IsAlreadyInstalled(state, manifest, manifestHash))
        {
            _log($"Already on Kewz's Cobblemon {manifest.Version}.");
            Report(UpdatePhase.Complete, "You’re up to date — starting Minecraft.");
            return;
        }
        if (IsDowngradeOrMutation(state, manifest, manifestHash))
        {
            _log($"Ignoring non-newer or mutated release {manifest.Version}; keeping local {state.Version}.");
            Report(UpdatePhase.Complete, "Keeping your installed Kewz's Cobblemon version.");
            return;
        }

        _log($"Update {manifest.Version} is available.");
        Report(UpdatePhase.UpdateAvailable, $"Update found: Kewz's Cobblemon {manifest.Version}");
        if (checkOnly)
        {
            Report(UpdatePhase.Complete, $"Kewz's Cobblemon {manifest.Version} is ready to install.");
            return;
        }

        LocalStateStore.AssertWritable(_paths);
        await DownloadVerifyAndApplyAsync(manifest, manifestHash, remoteRelease.AssetUrls, state, cancellationToken);
        _log($"Kewz's Cobblemon {manifest.Version} installed successfully.");
        Report(UpdatePhase.Complete, $"Kewz's Cobblemon {manifest.Version} installed — starting Minecraft.");
    }

    private bool IsAlreadyInstalled(InstalledState state, UpdateManifest manifest, string manifestHash)
    {
        if (!string.Equals(state.Version, manifest.Version, StringComparison.Ordinal)
            || !string.Equals(state.ManifestSha256, manifestHash, StringComparison.OrdinalIgnoreCase)
            || state.ManagedFiles.Count != manifest.Files.Count)
        {
            return false;
        }

        var recordedFiles = state.ManagedFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile manifestFile in manifest.Files)
        {
            if (!recordedFiles.TryGetValue(manifestFile.Path, out ManagedFileState? recorded)
                || recorded.Size != manifestFile.Size
                || !string.Equals(recorded.Sha256, manifestFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, manifestFile.Path);
            if (!File.Exists(target) || new FileInfo(target).Length != manifestFile.Size)
            {
                _log($"Repairing incomplete local Kewz's Cobblemon {manifest.Version} files.");
                return false;
            }
        }
        return true;
    }

    private static bool IsDowngradeOrMutation(InstalledState state, UpdateManifest manifest, string manifestHash)
    {
        if (!Version.TryParse(state.Version, out Version? current) || !Version.TryParse(manifest.Version, out Version? remote))
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
        CancellationToken cancellationToken)
    {
        string workDirectory = Path.Combine(_paths.LocalDataDirectory, "staging", manifestHash);
        string partsDirectory = Path.Combine(workDirectory, "parts");
        string archivePath = Path.Combine(workDirectory, "payload.zip");
        string extractDirectory = Path.Combine(workDirectory, "extracted");
        Directory.CreateDirectory(partsDirectory);

        using var releaseClient = new ReleaseClient(TimeSpan.FromSeconds(_configuration.NetworkTimeoutSeconds));
        long totalDownloadBytes = checked(manifest.Payload.Parts.Sum(part => part.Size));
        long completedDownloadBytes = 0;
        foreach (PayloadPart part in manifest.Payload.Parts)
        {
            string partPath = Path.Combine(partsDirectory, part.Name);
            _log($"Downloading {part.Name}...");
            long completedBeforePart = completedDownloadBytes;
            Report(UpdatePhase.Downloading, "Downloading update", completedBeforePart, totalDownloadBytes);
            await DownloadVerifiedPartAsync(
                releaseClient,
                assetUrls[part.Name],
                part,
                partPath,
                downloaded => Report(UpdatePhase.Downloading, "Downloading update", completedBeforePart + downloaded, totalDownloadBytes),
                cancellationToken);
            completedDownloadBytes = checked(completedDownloadBytes + part.Size);
        }

        _log("Reassembling verified update payload...");
        Report(UpdatePhase.Reassembling, "Reassembling verified update…");
        await CombinePartsAsync(manifest.Payload.Parts, partsDirectory, archivePath, cancellationToken);
        await VerifyFileAsync(archivePath, manifest.Payload.Size, manifest.Payload.Sha256, "update payload", cancellationToken);

        _log("Validating update archive...");
        Report(UpdatePhase.Validating, "Validating update files…");
        if (Directory.Exists(extractDirectory))
        {
            Directory.Delete(extractDirectory, recursive: true);
        }
        Directory.CreateDirectory(extractDirectory);
        await ExtractAndVerifyAsync(archivePath, extractDirectory, manifest.Files, cancellationToken);

        _log("Applying verified update...");
        Report(UpdatePhase.Applying, "Installing verified files", 0, 0, 0, manifest.Files.Count);
        await ApplyTransactionAsync(manifest, manifestHash, extractDirectory, previousState, cancellationToken);
        TryDeleteDirectory(workDirectory);
    }

    private async Task DownloadVerifiedPartAsync(
        ReleaseClient client,
        Uri source,
        PayloadPart part,
        string destination,
        Action<long>? reportDownloadedBytes,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            await client.DownloadFileAsync(source, destination, part.Size, reportDownloadedBytes, cancellationToken);
            string actualHash = await PathSafety.Sha256Async(destination, cancellationToken);
            if (PathSafety.IsExpectedHash(actualHash, part.Sha256))
            {
                return;
            }
            File.Delete(destination);
        }
        throw new InvalidDataException($"Checksum verification failed for {part.Name}.");
    }

    private static async Task CombinePartsAsync(
        IReadOnlyCollection<PayloadPart> parts,
        string partsDirectory,
        string archivePath,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        foreach (PayloadPart part in parts)
        {
            await using var source = new FileStream(Path.Combine(partsDirectory, part.Name), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
            await source.CopyToAsync(destination, 1024 * 1024, cancellationToken);
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

    private async Task ExtractAndVerifyAsync(
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
                continue;
            }

            // Reject Unix symbolic-link ZIP entries rather than relying on a
            // platform-specific extraction behavior.
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
            throw new InvalidDataException("Update archive is missing one or more signed files.");
        }
    }

    private async Task ApplyTransactionAsync(
        UpdateManifest manifest,
        string manifestHash,
        string extractDirectory,
        InstalledState previousState,
        CancellationToken cancellationToken)
    {
        string transactionId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
        string backupDirectory = Path.Combine(_paths.LocalDataDirectory, "rollback", transactionId, "files");
        var journal = new TransactionJournal();
        try
        {
            int appliedFiles = 0;
            int totalFiles = manifest.Files.Count;
            foreach (ManifestFile file in manifest.Files)
            {
                Report(UpdatePhase.Applying, "Installing verified files", 0, 0, appliedFiles, totalFiles);
                string target = PathSafety.CombineUnder(_paths.MinecraftDirectory, file.Path);
                string source = PathSafety.CombineUnder(extractDirectory, file.Path);
                string backup = PathSafety.CombineUnder(backupDirectory, file.Path);
                PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                {
                    journal.Operations.Add(new TransactionOperation { Kind = "replace", TargetPath = target, BackupPath = backup });
                    await TransactionStore.SaveAsync(_paths, journal, cancellationToken);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    TransactionStore.MoveOrCopy(target, backup);
                }
                else
                {
                    journal.Operations.Add(new TransactionOperation { Kind = "create", TargetPath = target });
                    await TransactionStore.SaveAsync(_paths, journal, cancellationToken);
                }

                TransactionStore.MoveOrCopy(source, target);
                await VerifyFileAsync(target, file.Size, file.Sha256, file.Path, cancellationToken);
                appliedFiles++;
                Report(UpdatePhase.Applying, "Installing verified files", 0, 0, appliedFiles, totalFiles);
            }

            var oldManagedPaths = previousState.ManagedFiles
                .Select(file => PathSafety.NormalizeRelativePath(file.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newManagedPaths = manifest.Files
                .Select(file => file.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A file is removable only when this updater installed it in an
            // earlier verified release. That lets a canonical release clean
            // out obsolete mods/resource packs automatically, while never
            // sweeping up unrelated player files which were not updater-owned.
            var requestedDeletes = manifest.DeletePaths
                .Select(PathSafety.NormalizeRelativePath)
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
                journal.Operations.Add(new TransactionOperation { Kind = "delete", TargetPath = target, BackupPath = backup });
                await TransactionStore.SaveAsync(_paths, journal, cancellationToken);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                TransactionStore.MoveOrCopy(target, backup);
            }

            // First-run migration is intentionally narrower than an ordinary
            // delete: only named legacy files whose exact expected hash is
            // still present can be removed. Unknown or player-modified files
            // are left alone rather than risking a broad folder cleanup.
            foreach (LegacyCleanupFile legacyFile in manifest.LegacyCleanup)
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
                journal.Operations.Add(new TransactionOperation { Kind = "delete", TargetPath = target, BackupPath = backup });
                await TransactionStore.SaveAsync(_paths, journal, cancellationToken);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                TransactionStore.MoveOrCopy(target, backup);
                _log($"Removed verified legacy file: {legacyFile.Path}");
            }

            journal.Committed = true;
            await TransactionStore.SaveAsync(_paths, journal, cancellationToken);
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A failed cleanup never compromises an installed or rolled back pack.
        }
        catch (UnauthorizedAccessException)
        {
            // The next updater run can safely clean its own staging directory.
        }
    }

    private void Report(
        UpdatePhase phase,
        string message,
        long completedBytes = 0,
        long totalBytes = 0,
        int currentItem = 0,
        int totalItems = 0) =>
        _progress?.Report(new UpdateProgress(phase, message, completedBytes, totalBytes, currentItem, totalItems));
}
