using System.Security.Cryptography;
using System.Text.Json;

namespace CobbleMusicUpdater;

internal sealed class TransactionJournal
{
    public int SchemaVersion { get; set; } = 2;
    // Schema 1 compatibility. New journals use Phase and never mark committed
    // before state.json is durably replaced.
    public bool Committed { get; set; }
    public string Phase { get; set; } = "applying";
    public InstalledState? PreviousState { get; set; }
    public InstalledState? NextState { get; set; }
    public List<TransactionOperation> Operations { get; set; } = [];
}

internal sealed class TransactionOperation
{
    public string Kind { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public long OriginalSize { get; set; } = -1;
    public string OriginalSha256 { get; set; } = "";
}

internal sealed class TransactionRecoveryException : IOException
{
    public TransactionRecoveryException(string message) : base(message) { }
    public TransactionRecoveryException(string message, Exception innerException) : base(message, innerException) { }
}

internal static class TransactionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string JournalPath(UpdaterPaths paths) => Path.Combine(paths.LocalDataDirectory, "transaction.json");

    public static async Task SaveAsync(UpdaterPaths paths, TransactionJournal journal, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.LocalDataDirectory);
        string destination = JournalPath(paths);
        string temporary = destination + ".new";
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        await DurableWriteAsync(temporary, content, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
    }

    public static async Task RecoverIfNeededAsync(
        UpdaterPaths paths,
        IReadOnlyCollection<string> allowedRoots,
        Action<string> log)
    {
        string path = JournalPath(paths);
        if (!File.Exists(path))
        {
            return;
        }

        TransactionJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new TransactionRecoveryException("The updater transaction journal is unreadable. Prism will not launch until it is repaired.", exception);
        }
        if (journal is null || journal.SchemaVersion is not (1 or 2) || journal.Operations is null)
        {
            throw new TransactionRecoveryException("The updater transaction journal has an unsupported format. Prism will not launch until it is repaired.");
        }

        try
        {
            ValidateJournal(paths, allowedRoots, journal);
            bool rollback;
            if (journal.SchemaVersion == 1)
            {
                rollback = !journal.Committed;
            }
            else if (string.Equals(journal.Phase, "applying", StringComparison.Ordinal))
            {
                rollback = true;
            }
            else
            {
                InstalledState actualState = LocalStateStore.LoadState(paths);
                if (StateEquivalent(actualState, journal.NextState!))
                {
                    // state.json is the durable commit point. A crash after its
                    // atomic replacement may complete forward only if every
                    // transaction-affected outcome is still exact.
                    await ValidateCommittedOutcomesAsync(paths, journal, CancellationToken.None);
                    rollback = false;
                }
                else if (StateEquivalent(actualState, journal.PreviousState!))
                {
                    // A crash before state.json replacement rolls files back.
                    rollback = true;
                }
                else
                {
                    throw new TransactionRecoveryException("Updater state is neither the old nor new transaction state; automatic recovery is unsafe.");
                }
            }

            if (rollback)
            {
                log("Recovering an interrupted local update transaction...");
                foreach (TransactionOperation operation in journal.Operations.AsEnumerable().Reverse())
                {
                    Restore(paths, operation, journal, log);
                }
                if (journal.SchemaVersion == 2)
                {
                    await LocalStateStore.SaveStateAsync(paths, journal.PreviousState!, CancellationToken.None);
                }
            }
            File.Delete(path);
            TryDeleteRecoveredRollbackDirectories(paths, journal.Operations);
        }
        catch (TransactionRecoveryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException)
        {
            throw new TransactionRecoveryException("The updater could not safely recover its last transaction. Prism will not launch to avoid a partial modpack.", exception);
        }
    }

    private static async Task ValidateCommittedOutcomesAsync(
        UpdaterPaths paths,
        TransactionJournal journal,
        CancellationToken cancellationToken)
    {
        var nextFiles = journal.NextState!.ManagedFiles.ToDictionary(
            file => file.Path,
            StringComparer.OrdinalIgnoreCase);
        string rollbackRoot = Path.Combine(paths.LocalDataDirectory, "rollback");
        foreach (TransactionOperation operation in journal.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.AssertNoReparsePointsOnTargetPath(paths.MinecraftDirectory, operation.TargetPath);
            if (!string.IsNullOrWhiteSpace(operation.BackupPath) && Directory.Exists(operation.BackupPath))
            {
                throw new TransactionRecoveryException($"Committed rollback backup path became a directory: {operation.BackupPath}");
            }
            if (!string.IsNullOrWhiteSpace(operation.BackupPath) && File.Exists(operation.BackupPath))
            {
                PathSafety.AssertNoReparsePointsOnTargetPath(rollbackRoot, operation.BackupPath);
            }

            if (operation.Kind == "delete")
            {
                if (File.Exists(operation.TargetPath) || Directory.Exists(operation.TargetPath))
                {
                    throw new TransactionRecoveryException($"Committed deletion outcome is no longer absent: {operation.TargetPath}");
                }
                continue;
            }

            if (!PathSafety.TryGetRelativePathUnder(paths.MinecraftDirectory, operation.TargetPath, out string relative)
                || !nextFiles.TryGetValue(relative, out ManagedFileState? expected)
                || !File.Exists(operation.TargetPath)
                || Directory.Exists(operation.TargetPath)
                || new FileInfo(operation.TargetPath).Length != expected.Size
                || !PathSafety.IsExpectedHash(
                    await PathSafety.Sha256Async(operation.TargetPath, cancellationToken),
                    expected.Sha256))
            {
                throw new TransactionRecoveryException($"Committed file outcome does not match next state: {operation.TargetPath}");
            }
        }
    }

    private static void ValidateJournal(
        UpdaterPaths paths,
        IReadOnlyCollection<string> allowedRoots,
        TransactionJournal journal)
    {
        if (journal.SchemaVersion == 2)
        {
            if (journal.Phase is not ("applying" or "filesApplied")
                || journal.PreviousState is null
                || journal.NextState is null)
            {
                throw new TransactionRecoveryException("The updater transaction journal has an invalid schema-2 phase or state snapshot.");
            }
            ValidateJournalState(journal.PreviousState, allowedRoots);
            ValidateJournalState(journal.NextState, allowedRoots);
        }

        string rollbackRoot = Path.Combine(paths.LocalDataDirectory, "rollback");
        foreach (TransactionOperation operation in journal.Operations)
        {
            if (operation is null
                || operation.Kind is not ("create" or "replace" or "delete")
                || string.IsNullOrWhiteSpace(operation.TargetPath)
                || !PathSafety.TryGetRelativePathUnder(paths.MinecraftDirectory, operation.TargetPath, out string targetRelative)
                || !PathSafety.IsAllowed(targetRelative, allowedRoots))
            {
                throw new TransactionRecoveryException("The updater transaction journal contains an unsafe target path.");
            }

            bool needsBackup = operation.Kind is "replace" or "delete";
            if (needsBackup && (string.IsNullOrWhiteSpace(operation.BackupPath)
                || !PathSafety.TryGetRelativePathUnder(rollbackRoot, operation.BackupPath, out _)))
            {
                throw new TransactionRecoveryException("The updater transaction journal contains an unsafe backup path.");
            }
            if (!needsBackup && !string.IsNullOrWhiteSpace(operation.BackupPath))
            {
                throw new TransactionRecoveryException("The updater transaction journal contains an invalid create operation.");
            }
            if (journal.SchemaVersion == 2 && needsBackup
                && (operation.OriginalSize < 0
                    || string.IsNullOrWhiteSpace(operation.OriginalSha256)
                    || operation.OriginalSha256.Length != 64
                    || operation.OriginalSha256.Any(character => !Uri.IsHexDigit(character))))
            {
                throw new TransactionRecoveryException("The updater transaction journal is missing original-file recovery metadata.");
            }
        }
    }

    private static void ValidateJournalState(InstalledState state, IReadOnlyCollection<string> allowedRoots)
    {
        if (state.SchemaVersion != 1 || state.ManagedFiles is null)
        {
            throw new TransactionRecoveryException("The updater transaction journal contains an invalid state snapshot.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedFileState file in state.ManagedFiles)
        {
            if (file is null)
            {
                throw new TransactionRecoveryException("The updater transaction journal contains an empty state entry.");
            }
            string path = PathSafety.NormalizeRelativePath(file.Path);
            if (!PathSafety.IsAllowed(path, allowedRoots)
                || file.Size < 0
                || string.IsNullOrWhiteSpace(file.Sha256)
                || file.Sha256.Length != 64
                || file.Sha256.Any(character => !Uri.IsHexDigit(character))
                || !paths.Add(path))
            {
                throw new TransactionRecoveryException("The updater transaction journal contains an unsafe state snapshot.");
            }
            file.Path = path;
        }
    }

    private static bool StateEquivalent(InstalledState left, InstalledState right)
    {
        if (!string.Equals(left.Version, right.Version, StringComparison.Ordinal)
            || !string.Equals(left.ManifestSha256, right.ManifestSha256, StringComparison.OrdinalIgnoreCase)
            || left.ManagedFiles.Count != right.ManagedFiles.Count)
        {
            return false;
        }
        var rightFiles = right.ManagedFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        return left.ManagedFiles.All(file => rightFiles.TryGetValue(file.Path, out ManagedFileState? expected)
            && file.Size == expected.Size
            && string.Equals(file.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static void Restore(UpdaterPaths paths, TransactionOperation operation, TransactionJournal journal, Action<string> log)
    {
        PathSafety.AssertNoReparsePointsOnTargetPath(paths.MinecraftDirectory, operation.TargetPath);
        if ((operation.Kind is "replace" or "delete") && File.Exists(operation.BackupPath))
        {
            PathSafety.AssertNoReparsePointsOnTargetPath(Path.Combine(paths.LocalDataDirectory, "rollback"), operation.BackupPath);
        }
        switch (operation.Kind)
        {
            case "create":
                if (journal.SchemaVersion == 2)
                {
                    RestoreSchemaTwoCreate(paths, operation, journal);
                }
                else if (File.Exists(operation.TargetPath))
                {
                    File.Delete(operation.TargetPath);
                }
                break;
            case "replace":
            case "delete":
                if (journal.SchemaVersion == 2)
                {
                    RestoreSchemaTwoBackup(paths, operation, journal);
                }
                else
                {
                    if (!File.Exists(operation.BackupPath))
                    {
                        throw new TransactionRecoveryException($"Rollback backup is missing for {operation.TargetPath}.");
                    }
                    if (File.Exists(operation.TargetPath))
                    {
                        File.Delete(operation.TargetPath);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(operation.TargetPath)!);
                    MoveOrCopy(operation.BackupPath, operation.TargetPath);
                }
                break;
            default:
                throw new InvalidDataException($"Unknown updater transaction operation: {operation.Kind}");
        }
        log($"Recovered {Path.GetFileName(operation.TargetPath)}.");
    }

    private static void RestoreSchemaTwoCreate(
        UpdaterPaths paths,
        TransactionOperation operation,
        TransactionJournal journal)
    {
        if (!File.Exists(operation.TargetPath) && !Directory.Exists(operation.TargetPath))
        {
            return;
        }
        if (!TryGetNextFile(paths, operation, journal, out ManagedFileState? next)
            || !File.Exists(operation.TargetPath)
            || !MatchesExpected(operation.TargetPath, next!))
        {
            throw new TransactionRecoveryException($"Created target has an unknown concurrent outcome: {operation.TargetPath}");
        }
        File.Delete(operation.TargetPath);
    }

    private static void RestoreSchemaTwoBackup(
        UpdaterPaths paths,
        TransactionOperation operation,
        TransactionJournal journal)
    {
        bool targetExists = File.Exists(operation.TargetPath);
        if (!targetExists && Directory.Exists(operation.TargetPath))
        {
            throw new TransactionRecoveryException($"Rollback target became a directory: {operation.TargetPath}");
        }
        bool backupExists = File.Exists(operation.BackupPath);
        if (targetExists && MatchesOriginal(operation.TargetPath, operation))
        {
            // Either mutation had not started, or an earlier recovery already
            // restored the original. A partial cross-volume backup is stale.
            if (backupExists)
            {
                File.Delete(operation.BackupPath);
            }
            return;
        }
        if (targetExists)
        {
            bool isUpdaterReplacement = operation.Kind == "replace"
                && TryGetNextFile(paths, operation, journal, out ManagedFileState? next)
                && MatchesExpected(operation.TargetPath, next!);
            if (!isUpdaterReplacement)
            {
                throw new TransactionRecoveryException($"Rollback target has an unknown concurrent outcome: {operation.TargetPath}");
            }
        }
        if (!backupExists || !MatchesOriginal(operation.BackupPath, operation))
        {
            throw new TransactionRecoveryException($"A complete rollback backup is missing for {operation.TargetPath}.");
        }
        if (targetExists)
        {
            File.Delete(operation.TargetPath);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(operation.TargetPath)!);
        MoveOrCopy(operation.BackupPath, operation.TargetPath);
    }

    private static bool TryGetNextFile(
        UpdaterPaths paths,
        TransactionOperation operation,
        TransactionJournal journal,
        out ManagedFileState? expected)
    {
        expected = null;
        if (!PathSafety.TryGetRelativePathUnder(paths.MinecraftDirectory, operation.TargetPath, out string relative))
        {
            return false;
        }
        expected = journal.NextState!.ManagedFiles.FirstOrDefault(
            file => string.Equals(file.Path, relative, StringComparison.OrdinalIgnoreCase));
        return expected is not null;
    }

    private static bool MatchesExpected(string path, ManagedFileState expected)
    {
        if (new FileInfo(path).Length != expected.Size)
        {
            return false;
        }
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesOriginal(string path, TransactionOperation operation)
    {
        var info = new FileInfo(path);
        if (info.Length != operation.OriginalSize)
        {
            return false;
        }
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return string.Equals(hash, operation.OriginalSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteRecoveredRollbackDirectories(UpdaterPaths paths, IEnumerable<TransactionOperation> operations)
    {
        string rollbackRoot = Path.Combine(paths.LocalDataDirectory, "rollback");
        foreach (string transactionId in operations
            .Where(operation => !string.IsNullOrWhiteSpace(operation.BackupPath))
            .Select(operation => PathSafety.TryGetRelativePathUnder(rollbackRoot, operation.BackupPath, out string relative)
                ? relative.Split('/')[0]
                : "")
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string transactionDirectory = Path.Combine(rollbackRoot, transactionId);
                if (Directory.Exists(transactionDirectory))
                {
                    Directory.Delete(transactionDirectory, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void MoveOrCopy(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (IOException) when (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, destination, overwrite: true);
            File.Delete(source);
        }
    }

    public static void MoveOrCopyNew(string source, string destination)
    {
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(source, destination);
            return;
        }
        File.Copy(source, destination, overwrite: false);
        File.Delete(source);
    }

    private static async Task DurableWriteAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(content, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }
}
