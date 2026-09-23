namespace CobbleMusicUpdater;

internal enum JvmHelperOutcome
{
    Relaunched,
    RelaunchFailed,
    WaitingForPrismExit,
    Applied,
    Cancelled,
    Aborted,
    AbortedPrismReopened
}

/// <summary>
/// Section 6 B: the helper mode (<c>--apply-jvm-settings &lt;plan.json&gt;</c>) of the same exe. The bootstrap never passes
/// this switch; only the pre-launch updater starts it. It closes Prism, edits instance.cfg while no prismlauncher.exe runs
/// (the only moment an edit cannot be overwritten, FINDINGS section 2) and starts Prism again with --launch.
/// </summary>
internal sealed class JvmSettingsHelper
{
    public const string CommandLineSwitch = "--apply-jvm-settings";
    public static readonly TimeSpan UpdaterExitTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PowerShellExitTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ChildrenGoneTimeout = TimeSpan.FromSeconds(15);
    public const int GracefulCloseRounds = 20;
    public const int LockWaitRounds = 5;
    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FallbackWaitLimit = TimeSpan.FromHours(12);

    private readonly IJvmSettingsSystem _system;
    private readonly Action<string> _log;
    private readonly IProgress<UpdateProgress>? _progress;

    public JvmSettingsHelper(IJvmSettingsSystem system, Action<string> log, IProgress<UpdateProgress>? progress)
    {
        _system = system;
        _log = message => log("Memory settings helper: " + message);
        _progress = progress;
    }

    /// <summary>Program.Main entry for <see cref="CommandLineSwitch"/>. Always returns without throwing.</summary>
    public static int RunFromCommandLine(string[] args)
    {
        if (args.Length != 2 || args[0] != CommandLineSwitch || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine($"Usage: CobbleMusicUpdater.exe {CommandLineSwitch} <plan.json>");
            return 2;
        }
        string planPath = Path.GetFullPath(args[1]);
        string planDirectory = Path.GetDirectoryName(planPath)!;
        // Until the plan is validated, log next to the plan (the updater's own local-data folder), never to a path the plan names.
        var log = new JvmHelperLog(Path.Combine(planDirectory, "jvm-settings-helper.log"));
        try
        {
            JvmSettingsPlan plan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(planPath));
            using var mutex = new Mutex(initiallyOwned: true, $"Local\\CobbleMusicUpdater.JvmSettings.{Path.GetFileName(planDirectory)}", out bool created);
            if (!created)
            {
                log.Write("Memory settings helper: another helper is already running for this instance; exiting.");
                return 0;
            }
            try
            {
                var system = new WindowsJvmSettingsSystem();
                var validator = new JvmSettingsHelper(system, log.Write, progress: null);
                string? invalid = validator.Validate(plan, planPath);
                if (invalid is not null)
                {
                    log.Write($"Memory settings helper: the plan was rejected ({invalid}); nothing was changed.");
                    return 2;
                }
                log.Retarget(Path.Combine(plan.MinecraftDirectory, "cobble-music-updater", "updater.log"));
                File.WriteAllText(JvmSettingsPlan.ReadyPathFor(plan.LocalDataDirectory), plan.Prism.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));

                JvmHelperOutcome outcome = JvmHelperOutcome.Aborted;
                var options = new CommandLine(plan.InstanceDirectory, plan.MinecraftDirectory, PrismPrelaunch: true, CheckOnly: false, NoUi: false);
                UpdateStatusForm.Run(options, (_, progress) =>
                {
                    outcome = new JvmSettingsHelper(system, log.Write, progress).RunActivePhase(plan);
                    return Task.FromResult(outcome is JvmHelperOutcome.Relaunched or JvmHelperOutcome.WaitingForPrismExit
                        or JvmHelperOutcome.Cancelled ? 0 : 1);
                });
                if (outcome == JvmHelperOutcome.WaitingForPrismExit)
                {
                    // Fallback F continues without a window: the card already told the player what will happen.
                    new JvmSettingsHelper(system, log.Write, progress: null).FinishAfterPrismExit(plan);
                }
                return 0;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception exception)
        {
            log.Write($"Memory settings helper: failed ({exception.GetType().Name}: {exception.Message}).");
            return 1;
        }
    }

    /// <summary>
    /// B1: the plan is a hand-off, not an authority. Returns null when every field that decides an action matches what the
    /// helper recomputes from the live, identity-checked Prism process and the compiled policy; otherwise the reason.
    /// </summary>
    public string? Validate(JvmSettingsPlan plan, string planPath)
    {
        if (plan.Schema != JvmSettingsPlan.CurrentSchema || plan.PolicyVersion != JvmPolicy.PolicyVersion)
        {
            return "unknown plan schema or policy version";
        }
        if (plan.Prism is null || plan.Updater is null)
        {
            return "the plan names no Prism or updater process";
        }
        if (!PrismDataDir.SamePath(Path.GetDirectoryName(planPath)!, plan.LocalDataDirectory))
        {
            return "the plan is not in this instance's local data folder";
        }
        try
        {
            if (!PrismDataDir.SamePath(_system.LocalDataDirectoryFor(plan.InstanceDirectory, plan.MinecraftDirectory), plan.LocalDataDirectory))
            {
                return "the local data folder does not belong to the plan's instance";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TransactionRecoveryException or ArgumentException)
        {
            return "the plan's instance folders are not valid: " + exception.Message;
        }
        if (!plan.MatchesDesired(JvmPolicy.DesiredFor(_system.TotalPhysicalMemoryBytes())))
        {
            return "the plan's desired settings differ from the compiled policy for this PC";
        }
        ProcessIdentity prism = plan.Prism.ToIdentity();
        if (!PrismLaunchChain.IsPrism(prism) || !string.Equals(plan.PrismExePath, prism.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            return "the plan's Prism process is not prismlauncher.exe";
        }
        if (!_system.IsAlive(prism))
        {
            return "the plan's Prism process is not running";
        }
        if (_system.OwnExecutablePath is not string ownExe
            || !string.Equals(plan.Updater.ImagePath, ownExe, StringComparison.OrdinalIgnoreCase))
        {
            return "the plan's updater is not this executable";
        }
        if (plan.PowerShell is not null && !PrismLaunchChain.IsPowerShell(plan.PowerShell.ToIdentity()))
        {
            return "the plan's pre-launch process is not PowerShell";
        }
        if (plan.InstanceId != Path.GetFileName(PrismDataDir.Normalize(plan.InstanceDirectory))
            || !PrismDataDir.SamePath(plan.InstanceConfigPath, Path.Combine(plan.InstanceDirectory, "instance.cfg"))
            || !PrismDataDir.SamePath(Path.GetDirectoryName(PrismDataDir.Normalize(plan.MinecraftDirectory))!, plan.InstanceDirectory))
        {
            return "the plan's instance paths are inconsistent";
        }
        string[]? argv = _system.TryGetCommandLine(prism) is string commandLine ? PrismArgs.SplitWindowsCommandLine(commandLine) : null;
        PrismCommandLine? parsed = argv is null ? null : PrismRelaunch.Parse(argv);
        if (parsed is null)
        {
            return "Prism's command line cannot be reproduced";
        }
        string? dataDirEnvironment = _system.GetEnvironmentVariable(PrismDataDir.DataDirEnvironmentVariable);
        if (string.IsNullOrEmpty(dataDirEnvironment))
        {
            dataDirEnvironment = null;
        }
        if (dataDirEnvironment != plan.DataDirEnvironment)
        {
            return "PRISMLAUNCHER_DATA_DIR differs from the plan";
        }
        string? dataDir = PrismDataDir.Resolve(prism.ImagePath, parsed.DirArgument, dataDirEnvironment, _system.AppDataDirectory);
        if (dataDir is null || !PrismDataDir.SamePath(dataDir, plan.DataDirectory)
            || !PrismDataDir.SamePath(plan.GlobalConfigPath, Path.Combine(dataDir, PrismDataDir.GlobalConfigFileName)))
        {
            return "Prism's data folder differs from the plan";
        }
        bool fromEnvironment = parsed.DirArgument is null && dataDirEnvironment is not null;
        IReadOnlyList<string> arguments = PrismRelaunch.BuildArguments(parsed, plan.InstanceId, fromEnvironment ? dataDir : null);
        if (!arguments.SequenceEqual(plan.RelaunchArguments, StringComparer.Ordinal))
        {
            return "the relaunch arguments differ from Prism's command line";
        }
        try
        {
            PrismIniDocument global = PrismIniDocument.Parse(File.ReadAllBytes(plan.GlobalConfigPath));
            if (!PrismDataDir.SamePath(PrismDataDir.ResolveInstancesDirectory(dataDir, global),
                    Path.GetDirectoryName(PrismDataDir.Normalize(plan.InstanceDirectory))!))
            {
                return "Prism's instance folder does not contain the plan's instance";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PrismIniUnsupportedException)
        {
            return "prismlauncher.cfg could not be read: " + exception.Message;
        }
        return null;
    }

    /// <summary>B2-B10. Returns <see cref="JvmHelperOutcome.WaitingForPrismExit"/> for fallback F.</summary>
    public JvmHelperOutcome RunActivePhase(JvmSettingsPlan plan)
    {
        ProcessIdentity prism = plan.Prism.ToIdentity();

        // B2. The relaunched Prism's pre-launch must not meet a live update.lock, and the launch must really be over.
        if (!_system.WaitForExit(plan.Updater.ToIdentity(), UpdaterExitTimeout))
        {
            return Abort(plan, "the pre-launch updater did not exit within 60 s");
        }
        if (plan.PowerShell is not null && !_system.WaitForExit(plan.PowerShell.ToIdentity(), PowerShellExitTimeout))
        {
            return Abort(plan, "the pre-launch PowerShell did not exit within 10 s");
        }
        JvmSettingsMarker marker = JvmSettingsMarkerStore.Load(plan.LocalDataDirectory, plan.PolicyVersion);
        if (marker.State != JvmMarkerState.Restarting)
        {
            // The updater cancelled (the helper was too slow, or the launch could not be stopped).
            _log($"the updater cancelled this restart (marker state {marker.State}); nothing was changed.");
            return JvmHelperOutcome.Cancelled;
        }
        Report(JvmSettingsText.ClosingPrism);

        // B3. Wait for Prism to have no children (the failed pre-launch is gone).
        DateTime deadline = _system.UtcNow + ChildrenGoneTimeout;
        IReadOnlyList<ProcessIdentity> children = [];
        while (_system.IsAlive(prism))
        {
            children = _system.GetChildren(prism);
            if (children.Count == 0 || _system.UtcNow >= deadline)
            {
                break;
            }
            _system.Sleep(TimeSpan.FromMilliseconds(500));
        }
        if (_system.IsAlive(prism) && children.Count > 0)
        {
            return EnterFallback(plan, children);
        }

        // B4. Graceful close: WM_CLOSE rounds to every visible top-level window (dialogs first get closed in earlier rounds).
        for (int round = 0; round < GracefulCloseRounds && _system.IsAlive(prism); round++)
        {
            children = _system.GetChildren(prism);
            if (children.Count > 0)
            {
                return EnterFallback(plan, children);
            }
            int posted = _system.PostCloseToVisibleTopLevelWindows(prism);
            _log($"close round {round + 1}: WM_CLOSE posted to {posted} Prism window(s).");
            if (_system.WaitForExit(prism, TimeSpan.FromSeconds(1)))
            {
                break;
            }
        }

        // B5. Forced close only when no game or other child runs and Prism is not in the middle of writing a settings file.
        if (_system.IsAlive(prism))
        {
            children = _system.GetChildren(prism);
            if (children.Count > 0)
            {
                return EnterFallback(plan, children);
            }
            int lockRound = 0;
            while (SettingsLockExists(plan) && lockRound++ < LockWaitRounds)
            {
                _system.Sleep(TimeSpan.FromSeconds(1));
            }
            if (SettingsLockExists(plan))
            {
                return Abort(plan, "Prism is still writing a settings file (.cfg.lock), so it was not forced to close");
            }
            if (_system.GetChildren(prism).Count > 0)
            {
                return EnterFallback(plan, _system.GetChildren(prism));
            }
            _log("Prism did not close within 20 s; terminating it (no children, no settings lock).");
            _system.TryTerminate(prism, 1);
            if (!_system.WaitForExit(prism, ForcedExitTimeout))
            {
                return Abort(plan, "Prism did not exit");
            }
        }

        // B6-B8.
        if (!FinishEdit(plan, out bool edited, out string detail))
        {
            return JvmHelperOutcome.AbortedPrismReopened;
        }

        // B9-B10. The marker is written before the relaunch so the relaunched pre-launch always sees it.
        SaveMarker(plan, marker, edited ? JvmMarkerState.Relaunched : JvmMarkerState.Failed, detail);
        Report(JvmSettingsText.Reopening);
        PrismRelaunchMethod method = _system.Relaunch(plan.PrismExePath, plan.RelaunchArguments,
            Path.GetDirectoryName(plan.PrismExePath)!, plan.DataDirEnvironment, _log);
        _log($"relaunch of Prism with {PrismArgs.JoinWindowsArguments(plan.RelaunchArguments)}: {method}.");
        if (method == PrismRelaunchMethod.Failed)
        {
            Report(JvmSettingsText.ReopenFailed);
            return JvmHelperOutcome.RelaunchFailed;
        }
        return JvmHelperOutcome.Relaunched;
    }

    /// <summary>Fallback F, second half: wait (at most 12 h) for Prism to exit, then B6-B8 without a relaunch.</summary>
    public JvmHelperOutcome FinishAfterPrismExit(JvmSettingsPlan plan)
    {
        ProcessIdentity prism = plan.Prism.ToIdentity();
        if (!_system.WaitForExit(prism, FallbackWaitLimit))
        {
            return Abort(plan, "Prism was still running after 12 h", report: false);
        }
        JvmSettingsMarker marker = JvmSettingsMarkerStore.Load(plan.LocalDataDirectory, plan.PolicyVersion);
        if (!FinishEdit(plan, out bool edited, out string detail))
        {
            return JvmHelperOutcome.AbortedPrismReopened;
        }
        SaveMarker(plan, marker, edited ? JvmMarkerState.Applied : JvmMarkerState.Failed, detail);
        return JvmHelperOutcome.Applied;
    }

    /// <summary>B6 (Prism not reopened), B7 (edit from the file as it is now), B8 (logs folder). False when Prism was reopened.</summary>
    private bool FinishEdit(JvmSettingsPlan plan, out bool edited, out string detail)
    {
        _system.Sleep(TimeSpan.FromSeconds(1));
        if (PrismIsRunning(plan))
        {
            JvmSettingsMarker marker = JvmSettingsMarkerStore.Load(plan.LocalDataDirectory, plan.PolicyVersion);
            SaveMarker(plan, marker, JvmMarkerState.AbortedPrismReopened, "Prism was started again before the edit");
            _log("Prism was started again before the edit; nothing was changed.");
            Report(JvmSettingsText.PrismReopened);
            edited = false;
            detail = "Prism reopened";
            return false;
        }
        (edited, detail) = EditInstanceConfig(plan);
        _log(detail);
        JvmSettingsCoordinator.EnsureLogsDirectory(plan.MinecraftDirectory, _log);
        return true;
    }

    /// <summary>B7: recomputes the merge from the files as Prism left them on exit and applies the section 9 edit.</summary>
    private (bool Edited, string Detail) EditInstanceConfig(JvmSettingsPlan plan)
    {
        try
        {
            ulong totalPhysical = _system.TotalPhysicalMemoryBytes();
            JvmSettingsInspection inspection = JvmSettingsInspection.Read(plan.InstanceConfigPath, plan.GlobalConfigPath, totalPhysical);
            if (!inspection.Merge.IsMissing)
            {
                return (true, "instance.cfg already has the recommended settings; no edit needed.");
            }
            byte[] updated = InstanceCfgEditor.BuildEditedBytes(inspection.Instance, inspection.Global, inspection.Merge,
                inspection.Desired, totalPhysical);
            InstanceCfgEditResult result = InstanceCfgEditor.Apply(plan.InstanceConfigPath, inspection.InstanceBytes, updated,
                () => PrismIsRunning(plan), _system.LocalNow);
            return (result.Written, $"{result.Detail} ({string.Join("; ", inspection.Merge.Reasons)}; backup {result.BackupPath}).");
        }
        catch (Exception exception) when (exception is PrismIniUnsupportedException or InvalidDataException or IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            return (false, $"instance.cfg was not changed: {exception.Message}");
        }
    }

    private JvmHelperOutcome EnterFallback(JvmSettingsPlan plan, IReadOnlyList<ProcessIdentity> children)
    {
        _log($"Prism is running {string.Join(", ", children.Select(child => $"{child.ExeName} (pid {child.Pid})"))}; " +
            "it is not closed. The settings will be written after Prism exits, without a relaunch.");
        JvmSettingsMarker marker = JvmSettingsMarkerStore.Load(plan.LocalDataDirectory, plan.PolicyVersion);
        SaveMarker(plan, marker, JvmMarkerState.WaitingForPrismExit, "waiting for Prism to exit");
        Report(JvmSettingsText.NextPrismStart);
        return JvmHelperOutcome.WaitingForPrismExit;
    }

    private JvmHelperOutcome Abort(JvmSettingsPlan plan, string reason, bool report = true)
    {
        _log($"stopped: {reason}; instance.cfg was not changed.");
        JvmSettingsMarker marker = JvmSettingsMarkerStore.Load(plan.LocalDataDirectory, plan.PolicyVersion);
        SaveMarker(plan, marker, JvmMarkerState.Aborted, reason);
        if (report)
        {
            Report(JvmSettingsText.PressPlayAgain);
        }
        return JvmHelperOutcome.Aborted;
    }

    private bool PrismIsRunning(JvmSettingsPlan plan) => _system.FindProcessesByImage(plan.PrismExePath).Count > 0;

    /// <summary>The QLockFile QSettings holds while it writes a file (qsettings.cpp:1358-1376): "&lt;file&gt;.lock".</summary>
    private static bool SettingsLockExists(JvmSettingsPlan plan) =>
        File.Exists(plan.InstanceConfigPath + ".lock") || File.Exists(plan.GlobalConfigPath + ".lock");

    private void SaveMarker(JvmSettingsPlan plan, JvmSettingsMarker marker, string state, string detail)
    {
        marker.State = state;
        marker.Detail = detail;
        JvmSettingsMarkerStore.Save(plan.LocalDataDirectory, marker, _system.UtcNow);
    }

    private void Report(string message) => _progress?.Report(JvmSettingsText.Progress(message));
}

/// <summary>The helper's log: the same line format as updater.log, append-only, never throwing.</summary>
internal sealed class JvmHelperLog
{
    private string _path;

    public JvmHelperLog(string path)
    {
        _path = path;
    }

    public void Retarget(string path) => _path = path;

    public void Write(string message)
    {
        string line = $"Kewz's Cobblemon Updater: {message}";
        Console.WriteLine(line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A diagnostic sink must not change what the helper does.
        }
    }
}
