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
        if (ledger.ModCheck is { Length: 67 or 72 } check
            && (check.StartsWith("ok:", StringComparison.Ordinal) || check.StartsWith("refused:", StringComparison.Ordinal))
            && IsHash(check[(check.IndexOf(':') + 1)..]))
        {
            result.ModCheck = check;
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
            try
            {
                (mode, status) = await DecideAutomaticallyAsync(lite.Detection, checkOnly, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Round-2 verifier FIX 2: an unexpected hardware name (for example ill-formed UTF-16) must never stop the
                // launch. Nothing is saved, so it is decided again next launch.
                mode = PerformanceMode.Full;
                status = "the hardware check failed on this launch, so the full pack is used. Picked: full.";
                _log($"Performance check: failed ({exception.GetType().Name}: {exception.Message}); result FULL for this launch, nothing saved.");
            }
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

    // Table lookup, no measurement (round 2). Re-decided automatically only when the hardware fingerprint (processor
    // name + real graphics cards), the built-in table's version or the signed lines change; otherwise the stored pick
    // is kept. A launch where the hardware could not be read keeps the stored pick and saves nothing.
    private Task<(PerformanceMode Mode, string Status)> DecideAutomaticallyAsync(PerformanceDetectionRules rules,
        bool checkOnly, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var readErrors = new List<string>();
        string cpuName;
        try { cpuName = _performanceEnvironment.ReadCpuName() ?? ""; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            cpuName = "";
            readErrors.Add($"processor name ({exception.GetType().Name}: {exception.Message})");
        }
        IReadOnlyList<GpuAdapterInfo> gpus;
        try { gpus = _performanceEnvironment.ReadGpus() ?? []; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            gpus = [];
            readErrors.Add($"graphics cards ({exception.GetType().Name}: {exception.Message})");
        }
        HardwareDb db;
        try { db = _performanceEnvironment.Database(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log($"Performance check: the built-in hardware list could not be loaded ({exception.GetType().Name}: {exception.Message}); result FULL.");
            return Task.FromResult((PerformanceMode.Full, "the built-in hardware list could not be read. Picked: full."));
        }

        HardwareDecision decision = HardwareVerdict.Decide(cpuName, gpus, db, rules);
        string storePath = MachinePerformanceStore.PathFor(_paths);
        MachinePerformanceRecord? stored = MachinePerformanceStore.Load(storePath);
        bool sameInputs = stored is not null
            && string.Equals(stored.Fingerprint, decision.Fingerprint, StringComparison.Ordinal)
            && string.Equals(stored.DatabaseVersion, decision.DatabaseVersion, StringComparison.Ordinal)
            && stored.CpuSingleThreadBelow == rules.CpuSingleThreadBelow
            && stored.GpuScoreBelow == rules.GpuScoreBelow;
        PerformanceMode mode;
        string decided;
        if (sameInputs)
        {
            mode = stored!.Verdict == "lite" ? PerformanceMode.Lite : PerformanceMode.Full;
            decided = "kept from " + MachinePerformanceStore.FormatDate(stored.DecidedAtUtc) + " (same hardware, table and lines)";
            if (mode != decision.Mode) decided += $"; note: a fresh lookup reads {decision.Mode.ToString().ToUpperInvariant()}";
        }
        else if (readErrors.Count > 0 && stored is not null)
        {
            mode = stored.Verdict == "lite" ? PerformanceMode.Lite : PerformanceMode.Full;
            decided = $"could not read the {string.Join(" and the ", readErrors)}, so the pick from {MachinePerformanceStore.FormatDate(stored.DecidedAtUtc)} ({mode.ToString().ToUpperInvariant()}) is kept";
        }
        else
        {
            mode = decision.Mode;
            string why = stored is null ? "first decision on this computer"
                : !string.Equals(stored.Fingerprint, decision.Fingerprint, StringComparison.Ordinal) ? "the hardware changed"
                : !string.Equals(stored.DatabaseVersion, decision.DatabaseVersion, StringComparison.Ordinal) ? "the built-in table changed"
                : "the lines in this release changed";
            decided = "decided now (" + why + ")";
            if (readErrors.Count > 0) decided += $"; could not read the {string.Join(" and the ", readErrors)}, so nothing is saved and it is decided again next launch";
            else if (!checkOnly)
            {
                MachinePerformanceStore.Save(storePath, ToRecord(decision), _log);
            }
        }
        _log(HardwareVerdict.LogLine(decision, decided));
        return Task.FromResult((mode, HardwareVerdict.StatusText(decision, mode)));
    }

    private static MachinePerformanceRecord ToRecord(HardwareDecision decision) => new()
    {
        DecidedAtUtc = DateTimeOffset.UtcNow,
        UpdaterVersion = BuildInfo.Version,
        Fingerprint = decision.Fingerprint,
        DatabaseVersion = decision.DatabaseVersion,
        CpuSingleThreadBelow = decision.Rules.CpuSingleThreadBelow,
        GpuScoreBelow = decision.Rules.GpuScoreBelow,
        Verdict = decision.Mode == PerformanceMode.Lite ? "lite" : "full",
        Reason = decision.Reason,
        CpuName = decision.CpuName,
        CpuKey = decision.Cpu.Key ?? "",
        CpuScore = decision.Cpu.Score,
        Gpus = decision.Adapters.Select(item => new GpuAdapterInfo
        {
            Name = item.Adapter.Name,
            DeviceId = item.Adapter.DeviceId,
            HardwareId = item.Adapter.HardwareId,
            MemoryBytes = item.Adapter.MemoryBytes,
            RegistryKey = item.Adapter.RegistryKey,
            Present = item.Adapter.Present,
            TableKey = item.Match.Key ?? "",
            Score = item.Match.Score,
            Note = item.Real ? item.Match.Rule : "not counted: " + item.NotRealReason
        }).ToList()
    };

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
        if (wanted.Count > 0)
        {
            (bool allowed, bool checkChanged) = CheckLiteModList(target, wanted, next, token);
            changed |= checkChanged;
            if (!allowed) wanted.Clear(); // refused: every mod stays (or is switched back) on; packs and settings still apply
        }
        else if (next.ModCheck.Length != 0)
        {
            next.ModCheck = "";
            changed = true;
        }
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

    // The dependency rule (LiteModGuard) over the jars this PC actually has: every top-level mods/*.jar stays enabled
    // except the listed ones, which are read from <jar> or their lite copy <jar>.disabled. Runs once per release and
    // list (the result is kept in the lite record); a refusal is logged and switches no mod off.
    private (bool Allowed, bool Changed) CheckLiteModList(UpdateManifest target, HashSet<string> wanted, PerformanceLedger next,
        CancellationToken token)
    {
        string identity = string.Join('\n', target.Files.Where(file => PathSafety.IsLiteModPath(file.Path) || PathSafety.IsNeverDisabledModPath(file.Path))
                .Select(file => file.Path.ToLowerInvariant() + ":" + file.Sha256.ToLowerInvariant()).Order(StringComparer.Ordinal))
            + "\n--\n" + string.Join('\n', wanted.Select(path => path.ToLowerInvariant()).Order(StringComparer.Ordinal));
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        if (next.ModCheck == "ok:" + key) return (true, false);
        if (next.ModCheck == "refused:" + key) return (false, false);
        token.ThrowIfCancellationRequested();
        string modsDirectory = SafeLocal("mods");
        var enabled = new List<(string Label, string Path)>();
        var off = new List<(string Label, string Path)>();
        var problems = new List<string>();
        if (Directory.Exists(modsDirectory))
        {
            foreach (string jar in Directory.GetFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
            {
                string relative = "mods/" + Path.GetFileName(jar);
                if (!wanted.Contains(relative)) enabled.Add((Path.GetFileName(jar), jar));
            }
        }
        foreach (string path in wanted.Order(StringComparer.OrdinalIgnoreCase))
        {
            string jar = SafeLocal(path);
            string source = File.Exists(jar) ? jar : jar + ".disabled";
            if (File.Exists(source)) off.Add((Path.GetFileName(path), source));
            else problems.Add($"{Path.GetFileName(path)} is not on this computer, so its dependencies cannot be checked");
        }
        problems.AddRange(LiteModGuard.Check(enabled, off));
        bool allowed = problems.Count == 0;
        next.ModCheck = (allowed ? "ok:" : "refused:") + key;
        if (allowed)
        {
            _log($"Performance mode: the lite mod list passed the dependency check ({off.Count} mods off, {enabled.Count} mods stay on).");
        }
        else
        {
            _log("Performance mode: the lite mod list is refused, so no mod is switched off: " + string.Join("; ", problems) + ".");
        }
        return (allowed, true);
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

            bool signedCopyBack = sameRevision && entry!.Removed.Count > 0
                && PathSafety.IsExpectedHash(currentHash, Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(entry.OriginalBase64))).ToLowerInvariant());
            if (sameRevision && !signedCopyBack && entry!.ListedIds.ToHashSet(StringComparer.Ordinal).SetEquals(wanted)) continue; // applied once
            if (signedCopyBack)
            {
                // The unfiltered copy is back (the player deleted the profile and convergence restored the signed
                // revision): it is filtered again. A profile Packed Packs rewrote (other bytes) is never refiltered.
                _log($"Performance mode: {Path.GetFileName(signed.Path)} came back unfiltered, so the lite packs are taken out again.");
            }
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
            if (sameRevision && !signedCopyBack && !PathSafety.IsExpectedHash(currentHash, entry!.FilteredSha256))
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
