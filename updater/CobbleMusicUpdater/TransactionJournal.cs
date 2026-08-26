using System.Text.Json;

namespace CobbleMusicUpdater;

internal sealed class TransactionJournal
{
    public int SchemaVersion { get; set; } = 1;
    public bool Committed { get; set; }
    public List<TransactionOperation> Operations { get; set; } = [];
}

internal sealed class TransactionOperation
{
    public string Kind { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
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
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions), cancellationToken);
        File.Move(temporary, destination, overwrite: true);
    }

    public static Task RecoverIfNeededAsync(
        UpdaterPaths paths,
        IReadOnlyCollection<string> allowedRoots,
        Action<string> log)
    {
        string path = JournalPath(paths);
        if (!File.Exists(path))
        {
            return Task.CompletedTask;
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
        if (journal is null || journal.SchemaVersion != 1 || journal.Operations is null)
        {
            throw new TransactionRecoveryException("The updater transaction journal has an unsupported format. Prism will not launch until it is repaired.");
        }

        try
        {
            ValidateJournal(paths, allowedRoots, journal);
            if (!journal.Committed)
            {
                log("Recovering an interrupted local update transaction...");
                foreach (TransactionOperation operation in journal.Operations.AsEnumerable().Reverse())
                {
                    Restore(paths, operation, log);
                }
            }
            File.Delete(path);
            TryDeleteRecoveredRollbackDirectories(paths, journal.Operations);
            return Task.CompletedTask;
        }
        catch (TransactionRecoveryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new TransactionRecoveryException("The updater could not safely recover its last transaction. Prism will not launch to avoid a partial modpack.", exception);
        }
    }

    private static void ValidateJournal(UpdaterPaths paths, IReadOnlyCollection<string> allowedRoots, TransactionJournal journal)
    {
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
        }
    }

    private static void Restore(UpdaterPaths paths, TransactionOperation operation, Action<string> log)
    {
        PathSafety.AssertNoReparsePointsOnTargetPath(paths.MinecraftDirectory, operation.TargetPath);
        if (operation.Kind is "replace" or "delete")
        {
            PathSafety.AssertNoReparsePointsOnTargetPath(Path.Combine(paths.LocalDataDirectory, "rollback"), operation.BackupPath);
        }
        switch (operation.Kind)
        {
            case "create":
                if (File.Exists(operation.TargetPath))
                {
                    File.Delete(operation.TargetPath);
                }
                break;
            case "replace":
            case "delete":
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
                break;
            default:
                throw new InvalidDataException($"Unknown updater transaction operation: {operation.Kind}");
        }
        log($"Recovered {Path.GetFileName(operation.TargetPath)}.");
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
}
