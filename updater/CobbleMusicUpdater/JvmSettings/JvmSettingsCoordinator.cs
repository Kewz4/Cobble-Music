namespace CobbleMusicUpdater;

/// <summary>A read of instance.cfg + prismlauncher.cfg and the merge the policy would apply to it.</summary>
internal sealed record JvmSettingsInspection(
    byte[] InstanceBytes,
    PrismIniDocument Instance,
    PrismIniDocument Global,
    PrismEffectiveSettings Effective,
    JvmDesiredState Desired,
    JvmMergeResult Merge)
{
    /// <summary>Reads both files as they are now. Throws <see cref="PrismIniUnsupportedException"/> for unmodelled content.</summary>
    public static JvmSettingsInspection Read(string instanceConfigPath, string globalConfigPath, ulong totalPhysicalBytes)
    {
        byte[] instanceBytes = File.ReadAllBytes(instanceConfigPath);
        PrismIniDocument instance = PrismIniDocument.Parse(instanceBytes);
        PrismIniDocument global = PrismIniDocument.Parse(File.ReadAllBytes(globalConfigPath));
        PrismEffectiveSettings effective = PrismEffectiveSettings.Resolve(instance, global, totalPhysicalBytes);
        JvmDesiredState desired = JvmPolicy.DesiredFor(totalPhysicalBytes);
        return new JvmSettingsInspection(instanceBytes, instance, global, effective, desired,
            JvmMerge.Compute(effective, desired, totalPhysicalBytes));
    }
}

/// <summary>The outcome of the pre-launch evaluation, for logging and tests.</summary>
internal enum JvmPreLaunchOutcome
{
    NoAction,
    NothingMissing,
    LoopGuardStopped,
    Deferred,
    HelperDidNotStart,
    AbortFailed,
    RestartStarted
}

/// <summary>
/// Section 6 A: runs in the pre-launch updater after the update finished (success or offline fallback) and the update lock
/// was released. Every failed precondition means "no action" and the launch continues; only when something is missing,
/// the loop guard allows it and every restart guard passes does it hand over to the helper and stop this launch.
/// </summary>
internal sealed class JvmSettingsCoordinator
{
    /// <summary>The pre-launch exit code that makes Prism fail the step ("Pre-Launch command failed with code 3.").</summary>
    public const int RestartExitCode = 3;
    public const string OptOutFileName = "jvm-settings.optout";
    public static readonly TimeSpan HelperReadyTimeout = TimeSpan.FromSeconds(10);

    private readonly IJvmSettingsSystem _system;
    private readonly Action<string> _log;
    private readonly IProgress<UpdateProgress>? _progress;

    public JvmSettingsCoordinator(IJvmSettingsSystem system, Action<string> log, IProgress<UpdateProgress>? progress)
    {
        _system = system;
        _log = message => log("Memory settings: " + message);
        _progress = progress;
    }

    /// <summary>
    /// The Program.Main hook: runs the normal updater, then (for a Prism pre-launch run) always creates INST_MC_DIR/logs and,
    /// when the update succeeded or fell back offline (exit 0), evaluates the memory settings. Returns the updater's exit
    /// code, or <see cref="RestartExitCode"/> when this launch was stopped so the helper can restart Prism.
    /// </summary>
    public static async Task<int> RunAfterUpdaterAsync(
        CommandLine options,
        IProgress<UpdateProgress>? progress,
        Func<CommandLine, IProgress<UpdateProgress>?, Task<int>> runUpdater,
        Action<string> log)
    {
        int exitCode = await runUpdater(options, progress);
        if (!options.PrismPrelaunch || options.CheckOnly || !OperatingSystem.IsWindows())
        {
            return exitCode;
        }
        var system = new WindowsJvmSettingsSystem();
        EnsureLogsDirectory(system.GetEnvironmentVariable("INST_MC_DIR") ?? options.MinecraftDirectory, log);
        if (exitCode != 0)
        {
            log("Memory settings: not evaluated because the update did not finish.");
            return exitCode;
        }
        try
        {
            UpdaterPaths paths = LocalStateStore.ResolvePaths(options.InstanceDirectory, options.MinecraftDirectory);
            JvmPreLaunchOutcome outcome = new JvmSettingsCoordinator(system, log, progress).Evaluate(paths);
            return outcome == JvmPreLaunchOutcome.RestartStarted ? RestartExitCode : exitCode;
        }
        catch (Exception exception)
        {
            // The feature must never change the launch decision by failing.
            log($"Memory settings: evaluation failed ({exception.GetType().Name}: {exception.Message}); continuing the launch.");
            return exitCode;
        }
    }

    /// <summary>
    /// The JVM log flag (-Xlog ... file=logs/gc.log) is fatal when logs/ is missing, so the game folder's logs/ is created on
    /// every run, whatever else happens. Only an existing game folder is used; nothing else is created.
    /// </summary>
    public static void EnsureLogsDirectory(string minecraftDirectory, Action<string> log)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(minecraftDirectory) && Directory.Exists(minecraftDirectory))
            {
                Directory.CreateDirectory(Path.Combine(minecraftDirectory, "logs"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"Memory settings: could not create the logs folder: {exception.Message}");
        }
    }

    public JvmPreLaunchOutcome Evaluate(UpdaterPaths paths)
    {
        // A1. Preconditions.
        string? instDir = _system.GetEnvironmentVariable("INST_DIR");
        string? instMcDir = _system.GetEnvironmentVariable("INST_MC_DIR");
        string? instId = _system.GetEnvironmentVariable("INST_ID");
        string? instJavaArgs = _system.GetEnvironmentVariable("INST_JAVA_ARGS");
        if (string.IsNullOrEmpty(instDir) || string.IsNullOrEmpty(instMcDir) || string.IsNullOrEmpty(instId) || instJavaArgs is null)
        {
            return NoAction("Prism did not pass INST_DIR, INST_MC_DIR, INST_ID and INST_JAVA_ARGS");
        }
        if (!PrismDataDir.SamePath(instDir, paths.InstanceDirectory)
            || instId != Path.GetFileName(PrismDataDir.Normalize(instDir)))
        {
            return NoAction("INST_DIR/INST_ID do not match this updater's instance");
        }
        if (File.Exists(Path.Combine(paths.InstallationDirectory, OptOutFileName)))
        {
            return NoAction($"the player opted out ({OptOutFileName} exists)");
        }
        PrismLaunchChain? chain = _system.ResolveLaunchChain();
        if (chain is null)
        {
            return NoAction("the parent chain is not a verified Prism pre-launch (prismlauncher -> [powershell ->] updater)");
        }
        int? prismMajor = _system.GetFileMajorVersion(chain.Prism.ImagePath);
        if (prismMajor is not (10 or 11))
        {
            return NoAction($"Prism major version {prismMajor?.ToString() ?? "unknown"} is not 10 or 11");
        }

        // A2. Effective state, read exactly as Prism does, and the INST_JAVA_ARGS cross-check.
        string[]? argv = _system.TryGetCommandLine(chain.Prism) is string commandLine ? PrismArgs.SplitWindowsCommandLine(commandLine) : null;
        PrismCommandLine? prismCommandLine = argv is null ? null : PrismRelaunch.Parse(argv);
        if (prismCommandLine is null)
        {
            return NoAction("Prism's command line could not be read or has options a relaunch cannot reproduce");
        }
        string? dataDirEnvironment = _system.GetEnvironmentVariable(PrismDataDir.DataDirEnvironmentVariable);
        string? dataDir = PrismDataDir.Resolve(chain.Prism.ImagePath, prismCommandLine.DirArgument, dataDirEnvironment, _system.AppDataDirectory);
        if (dataDir is null)
        {
            return NoAction("Prism's data folder could not be determined (relative --dir or PRISMLAUNCHER_DATA_DIR)");
        }
        string globalConfig = Path.Combine(dataDir, PrismDataDir.GlobalConfigFileName);
        string instanceConfig = Path.Combine(paths.InstanceDirectory, "instance.cfg");
        if (!File.Exists(globalConfig) || !File.Exists(instanceConfig))
        {
            return NoAction($"{(File.Exists(instanceConfig) ? globalConfig : instanceConfig)} does not exist");
        }
        ulong totalPhysical = _system.TotalPhysicalMemoryBytes();
        JvmSettingsInspection inspection;
        try
        {
            inspection = JvmSettingsInspection.Read(instanceConfig, globalConfig, totalPhysical);
        }
        catch (PrismIniUnsupportedException exception)
        {
            return NoAction("the settings files hold something this reader does not model: " + exception.Message);
        }
        if (!inspection.Effective.MatchesLaunchArguments(instJavaArgs, out string crossCheck))
        {
            return NoAction("cross-check failed: " + crossCheck);
        }

        // A3. Only if missing.
        JvmMergeResult merge = inspection.Merge;
        JvmDesiredState desired = inspection.Desired;
        foreach (string warning in merge.Warnings)
        {
            _log("warning: " + warning);
        }
        JvmSettingsMarker marker = JvmSettingsMarkerStore.Load(paths.LocalDataDirectory, JvmPolicy.PolicyVersion);
        if (!merge.IsMissing)
        {
            if (JvmMarkerState.EditWasMade(marker.State))
            {
                SaveMarker(paths, marker, JvmMarkerState.Verified, "the restart delivered the settings");
            }
            _log($"nothing missing (heap {inspection.Effective.HeapMiB} MiB, {desired.Tier.Name}); no action.");
            return JvmPreLaunchOutcome.NothingMissing;
        }
        _log("missing: " + string.Join("; ", merge.Reasons));

        // A4. Loop guard.
        JvmLoopGuardVerdict verdict = JvmSettingsMarkerStore.Evaluate(marker, _system.UtcNow);
        if (verdict != JvmLoopGuardVerdict.MayRestart)
        {
            if (verdict == JvmLoopGuardVerdict.AlreadyRelaunchedStillMissing)
            {
                SaveMarker(paths, marker, JvmMarkerState.Failed, "still missing after the restart: " + string.Join("; ", merge.Reasons));
            }
            _log($"loop guard ({verdict}, state {marker.State}, attempts {marker.Attempts}); no restart, continuing the launch.");
            Report(JvmSettingsText.CouldNotApply);
            return JvmPreLaunchOutcome.LoopGuardStopped;
        }

        // A5. Restart guards: any failure defers without counting an attempt.
        ProcessIdentity ourChild = chain.PowerShell ?? chain.Self;
        IReadOnlyList<ProcessIdentity> otherChildren = _system.GetChildren(chain.Prism).Where(child => !child.Matches(ourChild)).ToList();
        if (otherChildren.Count > 0)
        {
            return Defer($"Prism is also running {string.Join(", ", otherChildren.Select(child => $"{child.ExeName} (pid {child.Pid})"))}");
        }
        if (desired.Tier.IsBelowMinimum)
        {
            return Defer($"this PC has {totalPhysical / (1024 * 1024)} MiB of RAM, below the pack minimum");
        }
        if (!CanWrite(paths.InstanceDirectory))
        {
            return Defer("the instance folder is not writable");
        }
        if (!File.Exists(chain.Prism.ImagePath))
        {
            return Defer("prismlauncher.exe is not readable");
        }
        string instancesDir = PrismDataDir.ResolveInstancesDirectory(dataDir, inspection.Global);
        if (!PrismDataDir.SamePath(instancesDir, Path.GetDirectoryName(PrismDataDir.Normalize(paths.InstanceDirectory))!))
        {
            return Defer($"Prism's instance folder {instancesDir} does not contain this instance, so --launch would not find it");
        }
        if (_system.FindProcessesByImage(chain.Prism.ImagePath).Any(process => !process.Matches(chain.Prism)))
        {
            return Defer("another prismlauncher.exe from the same folder is running");
        }
        bool dataDirFromEnvironment = prismCommandLine.DirArgument is null && !string.IsNullOrEmpty(dataDirEnvironment);
        IReadOnlyList<string> relaunchArguments = PrismRelaunch.BuildArguments(prismCommandLine, instId, dataDirFromEnvironment ? dataDir : null);

        // A6. Count the attempt, then write the plan.
        marker.Attempts++;
        marker.LastAttemptUtc = _system.UtcNow;
        SaveMarker(paths, marker, JvmMarkerState.Restarting, string.Join("; ", merge.Reasons));
        var plan = new JvmSettingsPlan
        {
            PolicyVersion = desired.PolicyVersion,
            Prism = JvmPlanProcess.From(chain.Prism),
            PowerShell = chain.PowerShell is null ? null : JvmPlanProcess.From(chain.PowerShell),
            Updater = JvmPlanProcess.From(chain.Self),
            PrismExePath = chain.Prism.ImagePath,
            RelaunchArguments = [.. relaunchArguments],
            DataDirEnvironment = string.IsNullOrEmpty(dataDirEnvironment) ? null : dataDirEnvironment,
            DataDirectory = dataDir,
            InstanceId = instId,
            InstanceDirectory = paths.InstanceDirectory,
            MinecraftDirectory = PrismDataDir.Normalize(instMcDir),
            InstanceConfigPath = instanceConfig,
            GlobalConfigPath = globalConfig,
            LocalDataDirectory = paths.LocalDataDirectory,
            TotalPhysicalBytes = totalPhysical,
            XmxFloorMiB = desired.XmxFloorMiB,
            XmsMiB = desired.XmsMiB,
            Flags = [.. desired.Flags.Select(flag => flag.Token)]
        };
        string planPath = JvmSettingsPlan.PathFor(paths.LocalDataDirectory);
        string readyPath = JvmSettingsPlan.ReadyPathFor(paths.LocalDataDirectory);
        if (File.Exists(readyPath))
        {
            File.Delete(readyPath);
        }
        plan.Write(planPath);

        // A7. Start the helper and wait for it to confirm the plan.
        ProcessIdentity? helper = _system.StartHelper(planPath);
        if (helper is null || !WaitForReady(readyPath, helper))
        {
            SaveMarker(paths, marker, JvmMarkerState.AbortedHelper, "the helper did not confirm the plan within 10 s");
            _log("the helper did not confirm the plan; continuing the launch.");
            Report(JvmSettingsText.Deferred);
            return JvmPreLaunchOutcome.HelperDidNotStart;
        }

        // A8. Stop this launch. Nothing was claimed yet: ClaimAccount runs after the pre-launch step.
        if (!_system.FailPreLaunch(chain, RestartExitCode, _log))
        {
            // The helper re-reads the marker before closing Prism, so this state cancels it.
            SaveMarker(paths, marker, JvmMarkerState.Aborted, "the current launch could not be stopped");
            Report(JvmSettingsText.CouldNotApply);
            return JvmPreLaunchOutcome.AbortFailed;
        }

        // A9.
        _log($"restarting Prism to apply: {string.Join("; ", merge.Reasons)}.");
        Report(JvmSettingsText.Restarting);
        return JvmPreLaunchOutcome.RestartStarted;
    }

    private bool WaitForReady(string readyPath, ProcessIdentity helper)
    {
        DateTime deadline = _system.UtcNow + HelperReadyTimeout;
        while (_system.UtcNow < deadline)
        {
            if (File.Exists(readyPath))
            {
                return true;
            }
            if (!_system.IsAlive(helper))
            {
                return File.Exists(readyPath);
            }
            _system.Sleep(TimeSpan.FromMilliseconds(100));
        }
        return File.Exists(readyPath);
    }

    private JvmPreLaunchOutcome NoAction(string reason)
    {
        _log($"no action: {reason}.");
        return JvmPreLaunchOutcome.NoAction;
    }

    private JvmPreLaunchOutcome Defer(string reason)
    {
        _log($"deferred (no attempt counted): {reason}.");
        Report(JvmSettingsText.Deferred);
        return JvmPreLaunchOutcome.Deferred;
    }

    private void SaveMarker(UpdaterPaths paths, JvmSettingsMarker marker, string state, string detail)
    {
        marker.State = state;
        marker.Detail = detail;
        JvmSettingsMarkerStore.Save(paths.LocalDataDirectory, marker, _system.UtcNow);
    }

    private void Report(string message) => _progress?.Report(JvmSettingsText.Progress(message));

    private static bool CanWrite(string directory)
    {
        string probe = Path.Combine(directory, ".cobble-music-jvm-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
