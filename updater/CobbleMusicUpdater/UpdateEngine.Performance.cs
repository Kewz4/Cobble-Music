using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace CobbleMusicUpdater;

// Updater 1.2.22 lite mode (Kewz, 2026-09-23: weak PCs such as Jim's Ryzen 7 2700 + RTX 3050 get a lighter pack
// that runs better with shaders; lite never touches shader settings).
//
// Order of one launch:
//   1. PreparePerformancePlanAsync  - read performance-mode.txt; if auto, use the once-per-PC hardware record
//                                     (measure it the first time); load this instance's lite ledger.
//   2. normal convergence            - a mod lite switched off counts as installed when <jar>.disabled holds the
//                                     exact signed bytes, so it is never downloaded again.
//   3. ApplyPerformancePlanAsync     - one journaled transaction moves the instance to the wanted mode and records
//                                     every change in cobble-music-updater/performance-state.json; switching back
//                                     to full undoes exactly those changes (renames, saved values, saved bytes).
internal sealed class PerformancePlan
{
    public PerformanceMode Mode { get; init; }
    public LitePerformanceProfile? Lite { get; init; }
    public PerformanceLedger Ledger { get; init; } = new();
    public HashSet<string> LiteMods { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool MayAcceptDisabledCopy(string path) =>
        (Mode == PerformanceMode.Lite && LiteMods.Contains(path))
        || Ledger.Mods.Any(entry => entry.State == PerformanceLedgerStore.Disabled
            && entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
}

internal static class PerformanceLedgerStore
{
    public const string Disabled = "disabled";
    public const string PlayerEnabled = "playerEnabled";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static PerformanceLedger Load(UpdaterPaths paths, Action<string> log)
    {
        string path = PathSafety.CombineUnder(paths.MinecraftDirectory, PathSafety.PerformanceLedgerPath);
        try
        {
            if (!File.Exists(path)) return new PerformanceLedger();
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("the file is unusually large");
            PerformanceLedger? ledger = JsonSerializer.Deserialize<PerformanceLedger>(File.ReadAllBytes(path), JsonOptions);
            if (ledger is null) throw new InvalidDataException("the file is empty");
            return Sanitize(ledger);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or FormatException)
        {
            log($"The lite mode record ({PathSafety.PerformanceLedgerPath}) could not be read ({exception.Message}); lite changes it described cannot be undone automatically.");
            return new PerformanceLedger();
        }
    }

    // A ledger is a record, never an authority: unsafe entries are dropped, and every file operation is still
    // re-checked against exact hashes before anything moves.
    private static PerformanceLedger Sanitize(PerformanceLedger ledger)
    {
        if (ledger.SchemaVersion != 1 || ledger.Mods is null || ledger.Profiles is null || ledger.Settings is null)
            throw new InvalidDataException("the file has an unsupported format");
        var result = new PerformanceLedger();
        foreach (PerformanceModEntry entry in ledger.Mods)
        {
            if (entry is null || !TryNormalize(entry.Path, out string path) || !PathSafety.IsLiteModPath(path)
                || entry.Size < 0 || !IsHash(entry.Sha256) || entry.State is not (Disabled or PlayerEnabled)
                || result.Mods.Any(existing => existing.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            entry.Path = path;
            result.Mods.Add(entry);
        }
        foreach (PerformanceProfileEntry entry in ledger.Profiles)
        {
            if (entry is null || !TryNormalize(entry.Path, out string path) || !PathSafety.IsOfficialPackProfilePath(path)
                || !IsHash(entry.SignedSha256) || entry.ListedIds is null || entry.Removed is null
                || (entry.FilteredSha256.Length != 0 && !IsHash(entry.FilteredSha256))
                || result.Profiles.Any(existing => existing.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            try { _ = Convert.FromBase64String(entry.OriginalBase64); }
            catch (FormatException) { continue; }
            entry.Path = path;
            result.Profiles.Add(entry);
        }
        foreach (PerformanceSettingEntry entry in ledger.Settings)
        {
            if (entry is null || !TryNormalize(entry.Path, out string path)
                || PathSafety.PerformanceSettingFormat(path) != entry.Format
                || string.IsNullOrEmpty(entry.Key) || entry.PreviousValue is null || entry.AppliedValue is null
                || entry.PreviousValue.Any(char.IsControl) || entry.AppliedValue.Any(char.IsControl)
                || result.Settings.Any(existing => existing.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                    && existing.Key == entry.Key)) continue;
            entry.Path = path;
            result.Settings.Add(entry);
        }
        return result;
    }

    private static bool TryNormalize(string value, out string normalized)
    {
        normalized = "";
        try
        {
            normalized = PathSafety.NormalizeRelativePath(value ?? "");
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsHash(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static PerformanceLedger Clone(PerformanceLedger ledger) =>
        JsonSerializer.Deserialize<PerformanceLedger>(JsonSerializer.SerializeToUtf8Bytes(ledger, JsonOptions), JsonOptions)!;

    public static byte[] Serialize(PerformanceLedger ledger) => JsonSerializer.SerializeToUtf8Bytes(ledger, JsonOptions);
}

internal sealed partial class UpdateEngine
{
    private PerformancePlan? _performancePlan;

    internal PerformancePlan? CurrentPerformancePlanForTests => _performancePlan;
    // Tests only: behave like a process that died mid-switch (the journal stays for the next launch to recover).
    internal static bool LeaveJournalOnFailureForTests { get; set; }

    // True when the managed file is a mod lite keeps switched off: <jar> is absent and <jar>.disabled holds the
    // exact signed bytes. Only for a lite-listed mod while lite is wanted, or a mod this instance's ledger says
    // lite switched off (so switching back to full needs a rename, not a download).
    private async Task<bool> IsSatisfiedByDisabledCopyAsync(ManifestFile file, CancellationToken token)
    {
        if (_performancePlan is null || !PathSafety.IsLiteModPath(file.Path) || !_performancePlan.MayAcceptDisabledCopy(file.Path))
            return false;
        string jar = SafeLocal(file.Path);
        if (File.Exists(jar) || Directory.Exists(jar)) return false;
        return await MatchesExactAsync(SafeLocal(file.Path + ".disabled"), file.Size, file.Sha256, token);
    }

    private static async Task<bool> MatchesExactAsync(string path, long size, string sha256, CancellationToken token) =>
        File.Exists(path) && new FileInfo(path).Length == size
        && PathSafety.IsExpectedHash(await PathSafety.Sha256Async(path, token), sha256);

    internal async Task<PerformancePlan> PreparePerformancePlanAsync(UpdateManifest target, bool checkOnly, CancellationToken token)
    {
        PerformanceLedger ledger = PerformanceLedgerStore.Load(_paths, _log);
        LitePerformanceProfile? lite = target.PerformanceProfiles?.Lite;
        if (lite is null)
        {
            if (!ledger.IsEmpty) _log("Performance mode: this release has no lite list, so earlier lite changes are undone (full pack).");
            return new PerformancePlan { Mode = PerformanceMode.Full, Ledger = ledger };
        }

        string modeFile = PerformanceModeFile.PathFor(_paths);
        string requested = PerformanceModeFile.Read(modeFile, _log);
        PerformanceMode mode;
        string status;
        if (requested is "lite" or "full")
        {
            mode = requested == "lite" ? PerformanceMode.Lite : PerformanceMode.Full;
            status = $"the mode line above is set to {requested}, so the automatic check is not used. Picked: {requested}.";
            _log($"Performance mode: {requested.ToUpperInvariant()} (set in cobble-music-updater/{PerformanceModeFile.FileName}; hardware check skipped).");
        }
        else
        {
            (mode, status) = await DecideAutomaticallyAsync(lite.Detection, checkOnly, token);
        }
        if (!checkOnly) PerformanceModeFile.EnsureStatus(modeFile, status, _log);
        return new PerformancePlan
        {
            Mode = mode,
            Lite = mode == PerformanceMode.Lite ? lite : null,
            Ledger = ledger,
            LiteMods = mode == PerformanceMode.Lite
                ? lite.DisabledMods.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task<(PerformanceMode Mode, string Status)> DecideAutomaticallyAsync(PerformanceDetectionRules rules,
        bool checkOnly, CancellationToken token)
    {
        string storePath = MachinePerformanceStore.PathFor(_paths);
        MachinePerformanceRecord? record = MachinePerformanceStore.Load(storePath);
        string cpuName;
        try { cpuName = _performanceEnvironment.ReadCpuName(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            cpuName = "unknown processor";
            _log($"The processor name could not be read ({exception.GetType().Name}).");
        }
        IReadOnlyList<GpuAdapterInfo> gpus;
        string gpuError = "";
        try { gpus = _performanceEnvironment.ReadGpus(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            gpus = [];
            gpuError = $"{exception.GetType().Name}: {exception.Message}";
        }

        bool sameProcessor = record is not null && string.Equals(record.CpuName, cpuName, StringComparison.Ordinal);
        bool needProbe = !checkOnly && (!sameProcessor
            || (record!.CpuScore is null && record.CpuAttempts < _performanceEnvironment.MaximumCpuAttempts));
        if (needProbe)
        {
            Report(UpdatePhase.Validating, "Checking this computer’s speed (only once)…");
            (double? score, double? qps, string error) = await RunCpuProbeAsync(token);
            record = new MachinePerformanceRecord
            {
                MeasuredAtUtc = DateTimeOffset.UtcNow,
                UpdaterVersion = BuildInfo.Version,
                CpuName = cpuName,
                CpuScore = score,
                CpuQuantaPerSecond = qps,
                CpuError = error,
                CpuAttempts = (sameProcessor ? record!.CpuAttempts : 0) + 1,
                Gpus = gpus.ToList(),
                GpuError = gpuError
            };
            MachinePerformanceStore.Save(storePath, record, _log);
        }
        else if (!sameProcessor)
        {
            record = null; // check-only run on an unmeasured PC: no processor vote.
        }
        else if (record!.Gpus.Count != gpus.Count || !record.Gpus.Select(g => g.Name).SequenceEqual(gpus.Select(g => g.Name)))
        {
            if (!checkOnly && gpuError.Length == 0)
            {
                record.Gpus = gpus.ToList();
                record.GpuError = "";
                MachinePerformanceStore.Save(storePath, record, _log);
            }
        }

        double? cpuScore = record?.CpuScore;
        bool cpuLite = cpuScore is not null && cpuScore.Value < rules.CpuScoreThreshold;
        GpuTier gpuTier = GpuTierList.ClassifyMachine(gpus, rules);
        bool gpuLite = gpuTier == GpuTier.Lite;
        PerformanceMode mode = cpuLite || gpuLite ? PerformanceMode.Lite : PerformanceMode.Full;

        string cpuText = cpuScore is null
            ? $"processor \"{cpuName}\" not measured ({(record?.CpuError is { Length: > 0 } e ? e : "no result")})"
            : $"processor \"{cpuName}\" score {cpuScore.Value.ToString("0", CultureInfo.InvariantCulture)} (lite below {rules.CpuScoreThreshold})";
        string gpuText = gpus.Count == 0
            ? $"graphics card not read ({(gpuError.Length > 0 ? gpuError : "no adapter found")})"
            : "graphics " + string.Join(", ", gpus.Select(g => $"\"{g.Name}\" ({TierWords(GpuTierList.Classify(g.Name, rules))})"));
        string because = mode == PerformanceMode.Full
            ? (cpuScore is null && gpuTier != GpuTier.Lite ? "no lite reason could be measured" : "neither is below the lite line")
            : cpuLite && gpuLite ? "processor and graphics card" : cpuLite ? "processor" : "graphics card";
        string measuredAt = record is null ? "not measured yet" : "measured " + record.MeasuredAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _log($"Performance check: {cpuText}; {gpuText}; result {mode.ToString().ToUpperInvariant()} ({because}); {measuredAt}. Override: cobble-music-updater/{PerformanceModeFile.FileName}");

        string cpuStatus = cpuScore is null
            ? "processor not measured"
            : $"processor score {cpuScore.Value.ToString("0", CultureInfo.InvariantCulture)} (lite below {rules.CpuScoreThreshold})";
        string gpuStatus = gpus.Count == 0
            ? "graphics card not read"
            : "graphics card " + string.Join(" + ", gpus.Select(g => $"{g.Name} ({TierWords(GpuTierList.Classify(g.Name, rules))})"));
        return (mode, $"{cpuStatus}, {gpuStatus}. Picked: {mode.ToString().ToLowerInvariant()}.");
    }

    private static string TierWords(GpuTier tier) => tier switch
    {
        GpuTier.Full => "full list",
        GpuTier.Lite => "lite list",
        _ => "not on either list"
    };

    private async Task<(double? Score, double? QuantaPerSecond, string Error)> RunCpuProbeAsync(CancellationToken token)
    {
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<CpuProbeResult> probe = Task.Factory.StartNew(
            () => _performanceEnvironment.MeasureCpu(probeCancellation.Token),
            probeCancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _ = probe.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task finished = await Task.WhenAny(probe, Task.Delay(_performanceEnvironment.ProbeTimeout, delayCancellation.Token));
        delayCancellation.Cancel();
        token.ThrowIfCancellationRequested();
        if (finished != probe)
        {
            probeCancellation.Cancel();
            string timeout = $"the processor check timed out after {_performanceEnvironment.ProbeTimeout.TotalSeconds:0.#} s";
            _log($"Performance check: {timeout}; it does not count toward lite.");
            return (null, null, timeout);
        }
        if (probe.IsFaulted || probe.IsCanceled)
        {
            string failure = probe.IsCanceled ? "the processor check was cancelled"
                : $"the processor check failed ({probe.Exception!.InnerException?.GetType().Name}: {probe.Exception.InnerException?.Message})";
            _log($"Performance check: {failure}; it does not count toward lite.");
            return (null, null, failure);
        }
        CpuProbeResult result = probe.Result;
        if (!double.IsFinite(result.Score) || result.Score <= 0 || result.Score > 1_000_000)
        {
            return (null, null, "the processor check returned an invalid score");
        }
        return (Math.Round(result.Score, 1), Math.Round(result.QuantaPerSecond, 1), "");
    }

    // Never fails the update: an error rolls this one transaction back, is logged, and Minecraft starts with the
    // pack exactly as it was before the switch. Only a failed crash recovery (or cancellation) propagates.
    internal async Task ApplyPerformancePlanAsync(PerformancePlan plan, UpdateManifest target, CancellationToken token)
    {
        try
        {
            await ApplyPerformancePlanCoreAsync(plan, target, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (TransactionRecoveryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or FormatException or ArgumentException)
        {
            _log($"The {plan.Mode.ToString().ToLowerInvariant()} mode switch could not finish ({exception.Message}); every change of this switch was undone and Minecraft starts with the pack as it was. It is tried again next launch.");
        }
    }

    private sealed class PerformanceTransaction
    {
        public TransactionJournal? Journal;
        public string BackupDirectory = "";
        public string StagingDirectory = "";
        public readonly List<string> Changes = [];
    }

    private async Task ApplyPerformancePlanCoreAsync(PerformancePlan plan, UpdateManifest target, CancellationToken token)
    {
        PerformanceLedger next = PerformanceLedgerStore.Clone(plan.Ledger);
        bool ledgerChanged = false;
        var transaction = new PerformanceTransaction();
        try
        {
            ledgerChanged |= await ApplyModsAsync(plan, target, next, transaction, token);
            ledgerChanged |= await ApplyPackProfilesAsync(plan, target, next, transaction, token);
            ledgerChanged |= await ApplySettingsAsync(plan, next, transaction, token);
            if (transaction.Journal is null && !ledgerChanged)
            {
                return;
            }
            string ledgerPath = PathSafety.PerformanceLedgerPath;
            string ledgerTarget = SafeLocal(ledgerPath);
            byte[] ledgerBytes = PerformanceLedgerStore.Serialize(next);
            if (File.Exists(ledgerTarget))
            {
                byte[] current = await File.ReadAllBytesAsync(ledgerTarget, token);
                if (!current.AsSpan().SequenceEqual(ledgerBytes))
                    await ReplacePerformanceBytesAsync(transaction, ledgerPath, current, ledgerBytes, token);
            }
            else
            {
                await CreatePerformanceFileAsync(transaction, ledgerPath, ledgerBytes, token);
            }
            if (transaction.Journal is null) return;
            transaction.Journal.Phase = "filesApplied";
            await TransactionStore.SaveAsync(_paths, transaction.Journal, token);
            File.Delete(TransactionStore.JournalPath(_paths));
            TryDeleteDirectory(Path.GetDirectoryName(transaction.BackupDirectory)!);
            TryDeleteDirectory(transaction.StagingDirectory);
            string summary = transaction.Changes.Count == 0 ? "records updated" : string.Join("; ", transaction.Changes);
            _log($"Performance mode {plan.Mode.ToString().ToUpperInvariant()} applied: {summary}.");
        }
        catch
        {
            if (transaction.Journal is not null && !LeaveJournalOnFailureForTests)
            {
                await TransactionStore.RecoverIfNeededAsync(_paths, BuildInfo.SupportedRoots, _log);
                TryDeleteDirectory(transaction.StagingDirectory);
            }
            throw;
        }
    }

    private async Task<TransactionJournal> BeginPerformanceTransactionAsync(PerformanceTransaction transaction, CancellationToken token)
    {
        if (transaction.Journal is not null) return transaction.Journal;
        LocalStateStore.AssertWritable(_paths);
        InstalledState state = LocalStateStore.LoadState(_paths);
        string transactionId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        transaction.BackupDirectory = Path.Combine(_paths.LocalDataDirectory, "rollback", transactionId, "files");
        transaction.StagingDirectory = Path.Combine(_paths.LocalDataDirectory, "staging", "performance-" + transactionId);
        PathSafety.AssertNoReparsePointsOnTargetPath(_paths.LocalDataDirectory, transaction.StagingDirectory);
        Directory.CreateDirectory(transaction.StagingDirectory);
        // The installed state does not change; recovery rolls the files back while "applying" and keeps them after
        // "filesApplied" (the same state in both snapshots makes the forward check the deciding one).
        transaction.Journal = new TransactionJournal { PreviousState = state, NextState = state };
        await TransactionStore.SaveAsync(_paths, transaction.Journal, token);
        Report(UpdatePhase.Applying, "Switching performance mode…");
        return transaction.Journal;
    }

    private async Task<string> DeletePerformanceFileAsync(PerformanceTransaction transaction, string relative, long size,
        string sha256, CancellationToken token)
    {
        TransactionJournal journal = await BeginPerformanceTransactionAsync(transaction, token);
        string target = SafeLocal(relative);
        string backup = PathSafety.CombineUnder(transaction.BackupDirectory, relative);
        await BackupForOperationAsync("delete", target, backup, journal, token,
            new ManifestFile { Path = relative, Size = size, Sha256 = sha256 });
        return backup;
    }

    private async Task CreatePerformanceFileAsync(PerformanceTransaction transaction, string relative, byte[] content,
        CancellationToken token)
    {
        TransactionJournal journal = await BeginPerformanceTransactionAsync(transaction, token);
        string staged = PathSafety.CombineUnder(transaction.StagingDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await File.WriteAllBytesAsync(staged, content, token);
        await CreatePerformanceFileFromAsync(transaction, journal, relative, staged, content.LongLength,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), token);
    }

    private async Task CreatePerformanceCopyAsync(PerformanceTransaction transaction, string relative, string source,
        long size, string sha256, CancellationToken token)
    {
        TransactionJournal journal = await BeginPerformanceTransactionAsync(transaction, token);
        string staged = PathSafety.CombineUnder(transaction.StagingDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(source, staged, overwrite: false);
        await VerifyFileAsync(staged, size, sha256, relative, token);
        await CreatePerformanceFileFromAsync(transaction, journal, relative, staged, size, sha256, token);
    }

    private async Task CreatePerformanceFileFromAsync(PerformanceTransaction transaction, TransactionJournal journal,
        string relative, string staged, long size, string sha256, CancellationToken token)
    {
        string target = SafeLocal(relative);
        if (File.Exists(target) || Directory.Exists(target))
            throw new InvalidDataException($"A file appeared at {relative} while the performance mode was being switched.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        SetPerformanceOutcome(journal, relative, size, sha256);
        var operation = new TransactionOperation
        {
            Kind = "create",
            TargetPath = target,
            TargetTemporaryPath = TransactionStore.CreateSiblingTemporaryPath(target)
        };
        journal.Operations.Add(operation);
        await TransactionStore.SaveAsync(_paths, journal, token);
        await TransactionStore.MoveOrCopyNewAsync(staged, target, operation.TargetTemporaryPath, size, sha256, token);
        await VerifyFileAsync(target, size, sha256, relative, token);
    }

    private async Task ReplacePerformanceBytesAsync(PerformanceTransaction transaction, string relative, byte[] original,
        byte[] replacement, CancellationToken token)
    {
        TransactionJournal journal = await BeginPerformanceTransactionAsync(transaction, token);
        string target = SafeLocal(relative);
        string backup = PathSafety.CombineUnder(transaction.BackupDirectory, relative);
        string replacementHash = Convert.ToHexString(SHA256.HashData(replacement)).ToLowerInvariant();
        SetPerformanceOutcome(journal, relative, replacement.LongLength, replacementHash);
        TransactionOperation operation = await BackupForOperationAsync("replace", target, backup, journal, token,
            new ManifestFile
            {
                Path = relative,
                Size = original.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant()
            });
        await WriteDurableTemporaryAsync(operation.TargetTemporaryPath, replacement, token);
        await VerifyFileAsync(operation.TargetTemporaryPath, replacement.LongLength, replacementHash, relative, token);
        File.Move(operation.TargetTemporaryPath, target);
        await VerifyFileAsync(target, replacement.LongLength, replacementHash, relative, token);
    }

    private static void SetPerformanceOutcome(TransactionJournal journal, string relative, long size, string sha256)
    {
        journal.PerformanceOutcomes.RemoveAll(outcome => outcome.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
        journal.PerformanceOutcomes.Add(new ManagedFileState { Path = relative, Size = size, Sha256 = sha256 });
    }

    // ---- mods ------------------------------------------------------------------------------------------------

    private async Task<bool> ApplyModsAsync(PerformancePlan plan, UpdateManifest target, PerformanceLedger next,
        PerformanceTransaction transaction, CancellationToken token)
    {
        bool changed = false;
        var files = target.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var wanted = plan.Mode == PerformanceMode.Lite && plan.Lite is not null
            ? plan.Lite.DisabledMods.Where(files.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var turnedOn = new List<string>();
        var turnedOff = new List<string>();

        // Undo: every mod lite switched off that is no longer wanted off.
        foreach (PerformanceModEntry entry in next.Mods.Where(entry => !wanted.Contains(entry.Path)).ToList())
        {
            next.Mods.Remove(entry);
            changed = true;
            if (entry.State != PerformanceLedgerStore.Disabled) continue;
            string disabled = entry.Path + ".disabled";
            string disabledLocal = SafeLocal(disabled);
            bool jarExists = File.Exists(SafeLocal(entry.Path));
            if (files.TryGetValue(entry.Path, out ManifestFile? signed))
            {
                if (!jarExists && await MatchesExactAsync(disabledLocal, signed.Size, signed.Sha256, token))
                {
                    string backup = await DeletePerformanceFileAsync(transaction, disabled, signed.Size, signed.Sha256, token);
                    await CreatePerformanceCopyAsync(transaction, entry.Path, backup, signed.Size, signed.Sha256, token);
                    turnedOn.Add(Path.GetFileName(entry.Path));
                }
                else if (jarExists && await MatchesExactAsync(SafeLocal(entry.Path), signed.Size, signed.Sha256, token)
                    && File.Exists(disabledLocal))
                {
                    // The mod is already back (a newer copy was installed); remove only lite's own old disabled copy.
                    if (await MatchesExactAsync(disabledLocal, entry.Size, entry.Sha256, token))
                        await DeletePerformanceFileAsync(transaction, disabled, entry.Size, entry.Sha256, token);
                    else if (await MatchesExactAsync(disabledLocal, signed.Size, signed.Sha256, token))
                        await DeletePerformanceFileAsync(transaction, disabled, signed.Size, signed.Sha256, token);
                }
                else if (!jarExists)
                {
                    _log($"Performance mode: {Path.GetFileName(entry.Path)} was changed outside the updater; it is left as it is.");
                }
            }
            else if (await MatchesExactAsync(disabledLocal, entry.Size, entry.Sha256, token))
            {
                // The pack retired this mod while lite had it switched off: remove lite's copy too.
                await DeletePerformanceFileAsync(transaction, disabled, entry.Size, entry.Sha256, token);
                transaction.Changes.Add($"removed retired {Path.GetFileName(disabled)}");
            }
        }

        // Apply: switch off every lite-listed mod once.
        foreach (string path in wanted.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            ManifestFile signed = files[path];
            PerformanceModEntry? entry = next.Mods.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            string jarLocal = SafeLocal(path);
            string disabled = path + ".disabled";
            string disabledLocal = SafeLocal(disabled);
            bool jarExists = File.Exists(jarLocal);
            bool disabledExists = File.Exists(disabledLocal);
            if (!jarExists && disabledExists && await MatchesExactAsync(disabledLocal, signed.Size, signed.Sha256, token))
            {
                changed |= SetModEntry(next, entry, signed, PerformanceLedgerStore.Disabled);
                continue;
            }
            if (entry?.State == PerformanceLedgerStore.PlayerEnabled)
            {
                continue;
            }
            if (!jarExists || !await MatchesExactAsync(jarLocal, signed.Size, signed.Sha256, token))
            {
                _log($"Performance mode: {Path.GetFileName(path)} does not match this release, so it is not switched off this time.");
                continue;
            }
            if (entry is { State: PerformanceLedgerStore.Disabled } && !disabledExists
                && PathSafety.IsExpectedHash(entry.Sha256, signed.Sha256) && entry.Size == signed.Size)
            {
                // Lite switched it off before and the player turned it back on by hand: lite leaves it on.
                entry.State = PerformanceLedgerStore.PlayerEnabled;
                changed = true;
                _log($"Performance mode: {Path.GetFileName(path)} was turned back on by hand, so lite leaves it on.");
                continue;
            }
            if (disabledExists)
            {
                if (await MatchesExactAsync(disabledLocal, signed.Size, signed.Sha256, token))
                {
                    await DeletePerformanceFileAsync(transaction, path, signed.Size, signed.Sha256, token);
                    changed |= SetModEntry(next, entry, signed, PerformanceLedgerStore.Disabled);
                    turnedOff.Add(Path.GetFileName(path));
                    continue;
                }
                if (entry is null || !await MatchesExactAsync(disabledLocal, entry.Size, entry.Sha256, token))
                {
                    _log($"Performance mode: {Path.GetFileName(disabled)} already exists with other contents, so {Path.GetFileName(path)} stays on.");
                    continue;
                }
                await DeletePerformanceFileAsync(transaction, disabled, entry.Size, entry.Sha256, token);
            }
            string backup = await DeletePerformanceFileAsync(transaction, path, signed.Size, signed.Sha256, token);
            await CreatePerformanceCopyAsync(transaction, disabled, backup, signed.Size, signed.Sha256, token);
            changed |= SetModEntry(next, entry, signed, PerformanceLedgerStore.Disabled);
            turnedOff.Add(Path.GetFileName(path));
        }
        if (turnedOff.Count > 0) transaction.Changes.Add($"switched off {turnedOff.Count} mods ({string.Join(", ", turnedOff)})");
        if (turnedOn.Count > 0) transaction.Changes.Add($"switched {turnedOn.Count} mods back on ({string.Join(", ", turnedOn)})");
        return changed;
    }

    private static bool SetModEntry(PerformanceLedger ledger, PerformanceModEntry? entry, ManifestFile signed, string state)
    {
        if (entry is not null && entry.State == state && entry.Size == signed.Size
            && PathSafety.IsExpectedHash(entry.Sha256, signed.Sha256))
        {
            return false;
        }
        if (entry is not null) ledger.Mods.Remove(entry);
        ledger.Mods.Add(new PerformanceModEntry { Path = signed.Path, Size = signed.Size, Sha256 = signed.Sha256.ToLowerInvariant(), State = state });
        return true;
    }

    // ---- resource packs (official Packed Packs profiles) -----------------------------------------------------

    private async Task<bool> ApplyPackProfilesAsync(PerformancePlan plan, UpdateManifest target, PerformanceLedger next,
        PerformanceTransaction transaction, CancellationToken token)
    {
        bool changed = false;
        List<string> wanted = plan.Mode == PerformanceMode.Lite && plan.Lite is not null ? plan.Lite.RemovedPackIds : [];
        var profiles = target.Files.Where(file => PathSafety.IsOfficialPackProfilePath(file.Path)).ToList();
        foreach (PerformanceProfileEntry stale in next.Profiles
            .Where(entry => !profiles.Any(file => file.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            next.Profiles.Remove(stale);
            changed = true;
        }
        int removedTotal = 0, restoredProfiles = 0;
        foreach (ManifestFile signed in profiles)
        {
            token.ThrowIfCancellationRequested();
            PerformanceProfileEntry? entry = next.Profiles.FirstOrDefault(item => item.Path.Equals(signed.Path, StringComparison.OrdinalIgnoreCase));
            string local = SafeLocal(signed.Path);
            if (!File.Exists(local) || new FileInfo(local).Length > SettingText.MaximumFileBytes) continue;
            byte[] current = await File.ReadAllBytesAsync(local, token);
            string currentHash = Convert.ToHexString(SHA256.HashData(current)).ToLowerInvariant();
            bool sameRevision = entry is not null && PathSafety.IsExpectedHash(entry.SignedSha256, signed.Sha256);

            if (wanted.Count == 0)
            {
                if (entry is null) continue;
                next.Profiles.Remove(entry);
                changed = true;
                if (!sameRevision || entry.Removed.Count == 0) continue; // a newer signed revision already replaced lite's copy
                byte[]? restored = PathSafety.IsExpectedHash(currentHash, entry.FilteredSha256)
                    ? Convert.FromBase64String(entry.OriginalBase64)
                    : ReinsertPacks(current, entry.Removed, new HashSet<string>(StringComparer.Ordinal));
                if (restored is null)
                {
                    _log($"Performance mode: {Path.GetFileName(signed.Path)} could not be read as a pack profile; its packs were not put back.");
                    continue;
                }
                if (!restored.AsSpan().SequenceEqual(current))
                {
                    await ReplacePerformanceBytesAsync(transaction, signed.Path, current, restored, token);
                    restoredProfiles++;
                }
                continue;
            }

            if (sameRevision && entry!.ListedIds.ToHashSet(StringComparer.Ordinal).SetEquals(wanted)) continue; // applied once
            byte[] original = sameRevision ? Convert.FromBase64String(entry!.OriginalBase64) : current;
            PackProfileText.PackList? originalList = PackProfileText.Parse(original);
            if (originalList is null)
            {
                _log($"Performance mode: {Path.GetFileName(signed.Path)} could not be read as a pack profile; its packs are left as they are.");
                continue;
            }
            var wantedSet = wanted.ToHashSet(StringComparer.Ordinal);
            var removed = originalList.Entries.Select((item, index) => (item.Id, index))
                .Where(item => wantedSet.Contains(item.Id))
                .Select(item => new PerformanceRemovedPack { Id = item.Id, Index = item.index }).ToList();
            byte[]? filtered;
            if (sameRevision && !PathSafety.IsExpectedHash(currentHash, entry!.FilteredSha256))
            {
                // Packed Packs rewrote the file since lite filtered it: work on its current contents.
                filtered = ReinsertPacks(current, entry.Removed.Where(item => !wantedSet.Contains(item.Id)).ToList(), wantedSet);
            }
            else
            {
                filtered = PackProfileText.Write(original, originalList,
                    originalList.Entries.Where(item => !wantedSet.Contains(item.Id)).ToList());
            }
            if (filtered is null)
            {
                _log($"Performance mode: {Path.GetFileName(signed.Path)} could not be read as a pack profile; its packs are left as they are.");
                continue;
            }
            if (!filtered.AsSpan().SequenceEqual(current))
            {
                await ReplacePerformanceBytesAsync(transaction, signed.Path, current, filtered, token);
            }
            removedTotal += removed.Count;
            if (entry is not null) next.Profiles.Remove(entry);
            next.Profiles.Add(new PerformanceProfileEntry
            {
                Path = signed.Path,
                SignedSha256 = signed.Sha256.ToLowerInvariant(),
                OriginalBase64 = Convert.ToBase64String(original),
                FilteredSha256 = Convert.ToHexString(SHA256.HashData(filtered)).ToLowerInvariant(),
                ListedIds = wanted.ToList(),
                Removed = removed
            });
            changed = true;
        }
        if (removedTotal > 0) transaction.Changes.Add($"took {removedTotal} resource pack entries out of the Default/Realistic profiles");
        if (restoredProfiles > 0) transaction.Changes.Add($"put the resource packs back in {restoredProfiles} profile(s)");
        return changed;
    }

    // Puts removed ids back at their original positions (when missing) and takes out the ids in `remove`.
    private static byte[]? ReinsertPacks(byte[] content, IReadOnlyList<PerformanceRemovedPack> restore, IReadOnlySet<string> remove)
    {
        PackProfileText.PackList? list = PackProfileText.Parse(content);
        if (list is null) return null;
        var entries = list.Entries.Where(item => !remove.Contains(item.Id)).ToList();
        foreach (PerformanceRemovedPack pack in restore.OrderBy(pack => pack.Index))
        {
            if (entries.Any(item => item.Id == pack.Id)) continue;
            entries.Insert(Math.Clamp(pack.Index, 0, entries.Count), (pack.Id, PackProfileText.Encode(pack.Id)));
        }
        return PackProfileText.Write(content, list, entries);
    }

    // ---- settings ----------------------------------------------------------------------------------------------

    private async Task<bool> ApplySettingsAsync(PerformancePlan plan, PerformanceLedger next,
        PerformanceTransaction transaction, CancellationToken token)
    {
        bool changed = false;
        List<PerformanceSetting> wanted = plan.Mode == PerformanceMode.Lite && plan.Lite is not null ? plan.Lite.Settings : [];
        int applied = 0, restored = 0;
        var paths = wanted.Select(setting => setting.Path).Concat(next.Settings.Select(entry => entry.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            string local = SafeLocal(path);
            byte[]? original = File.Exists(local) && new FileInfo(local).Length <= SettingText.MaximumFileBytes
                ? await File.ReadAllBytesAsync(local, token) : null;
            byte[]? working = original;
            var entries = next.Settings.Where(entry => entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            var fileWanted = wanted.Where(setting => setting.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            var nextEntries = new List<PerformanceSettingEntry>();
            int fileApplied = 0, fileRestored = 0;

            foreach (PerformanceSettingEntry entry in entries)
            {
                PerformanceSetting? keep = fileWanted.FirstOrDefault(setting => setting.Key == entry.Key && setting.Format == entry.Format);
                if (keep is not null)
                {
                    nextEntries.Add(entry);
                    continue;
                }
                // No longer wanted: put the player's value back, unless they changed it themselves since.
                if (working is null) continue;
                SettingText.ValueSpan? span = SettingText.Find(working, entry.Format, entry.Key);
                if (span is null) continue;
                if (span.Value.Raw == entry.AppliedValue)
                {
                    if (entry.PreviousValue != entry.AppliedValue)
                    {
                        working = SettingText.Replace(working, span.Value, entry.PreviousValue);
                        fileRestored++;
                    }
                }
                else
                {
                    _log($"Performance mode: kept your own value for {entry.Key} in {path}.");
                }
            }

            foreach (PerformanceSetting setting in fileWanted)
            {
                PerformanceSettingEntry? entry = nextEntries.FirstOrDefault(item => item.Key == setting.Key && item.Format == setting.Format);
                if (working is null)
                {
                    if (entry is null) _log($"Performance mode: {path} does not exist yet; {setting.Key} is set once it does.");
                    continue;
                }
                SettingText.ValueSpan? span = SettingText.Find(working, setting.Format, setting.Key);
                if (entry is not null)
                {
                    if (entry.AppliedValue == setting.Value) continue; // applied once; never fight the player
                    if (span is not null && span.Value.Raw == entry.AppliedValue)
                    {
                        working = SettingText.Replace(working, span.Value, setting.Value);
                        fileApplied++;
                    }
                    nextEntries.Remove(entry);
                    nextEntries.Add(new PerformanceSettingEntry
                    {
                        Path = path, Format = setting.Format, Key = setting.Key,
                        PreviousValue = entry.PreviousValue,
                        AppliedValue = span is not null && span.Value.Raw == entry.AppliedValue ? setting.Value : entry.AppliedValue
                    });
                    continue;
                }
                if (span is null)
                {
                    _log($"Performance mode: {setting.Key} was not found exactly once in {path}; it is left as it is.");
                    continue;
                }
                nextEntries.Add(new PerformanceSettingEntry
                {
                    Path = path, Format = setting.Format, Key = setting.Key,
                    PreviousValue = span.Value.Raw, AppliedValue = setting.Value
                });
                if (span.Value.Raw != setting.Value)
                {
                    working = SettingText.Replace(working, span.Value, setting.Value);
                    fileApplied++;
                }
            }

            bool fileChanged = working is not null && original is not null && !working.AsSpan().SequenceEqual(original);
            if (fileChanged && entries.Concat(nextEntries).Any(entry => entry.Format == "json") && !SettingText.IsValidJsonDocument(working!))
            {
                _log($"Performance mode: the edit of {path} would not be valid JSON, so the file is left as it is.");
                continue;
            }
            if (fileChanged)
            {
                await ReplacePerformanceBytesAsync(transaction, path, original!, working!, token);
                applied += fileApplied;
                restored += fileRestored;
            }
            bool ledgerSame = entries.Count == nextEntries.Count && entries.All(entry => nextEntries.Any(item =>
                item.Key == entry.Key && item.Format == entry.Format && item.PreviousValue == entry.PreviousValue
                && item.AppliedValue == entry.AppliedValue));
            if (!ledgerSame)
            {
                next.Settings.RemoveAll(entry => entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                next.Settings.AddRange(nextEntries);
                changed = true;
            }
        }
        if (applied > 0) transaction.Changes.Add($"changed {applied} setting(s)");
        if (restored > 0) transaction.Changes.Add($"put {restored} setting(s) back");
        return changed;
    }
}
