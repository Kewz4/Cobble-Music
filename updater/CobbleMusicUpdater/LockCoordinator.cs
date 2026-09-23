namespace CobbleMusicUpdater;

/// <summary>Everything the lock coordinator touches outside itself, so its decisions can be tested with a fake clock and fake processes.</summary>
internal interface ILockEnvironment
{
    DateTime UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    /// <summary>One attempt to open update.lock (FileShare.None). Throws IOException on failure.</summary>
    FileStream OpenLock();
    HolderSnapshot ProbeHolder();
    bool JournalPresent();
    /// <summary>Sets the stop event; false when no holder listens (a legacy holder).</summary>
    bool SignalStop();
    Task<bool> WaitForExitAsync(ProcessIdentity holder, TimeSpan timeout, CancellationToken cancellationToken);
    /// <summary>Terminates the holder only if it is still exactly this process AND this instance's updater exe.</summary>
    bool TryTerminate(ProcessIdentity holder);
}

internal sealed class SystemLockEnvironment(UpdaterPaths paths) : ILockEnvironment
{
    private readonly SystemHolderProbe _probe = new(paths);

    public DateTime UtcNow => DateTime.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    public FileStream OpenLock() => OperationLockFile.Open(paths);

    public HolderSnapshot ProbeHolder() => _probe.Probe();

    public bool JournalPresent() => File.Exists(TransactionStore.JournalPath(paths));

    public bool SignalStop() => UpdaterStopSignal.Signal(paths);

    public Task<bool> WaitForExitAsync(ProcessIdentity holder, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.Run(() => ProcessTree.WaitForExit(holder, timeout), cancellationToken);

    public bool TryTerminate(ProcessIdentity holder) => TryTerminateHolder(holder, paths.InstallationDirectory);

    /// <summary>Step 3 of the takeover: re-check the image belongs to this instance, then ProcessTree re-checks pid + start + image.</summary>
    internal static bool TryTerminateHolder(ProcessIdentity holder, string installationDirectory) =>
        HolderClassifier.IsHolderImageForInstance(holder.ImagePath, installationDirectory)
        && ProcessTree.TryTerminate(holder, LockTimings.TakeoverTerminationExitCode);
}

/// <summary>
/// Lock v2 acquisition (FINDINGS §7.2-7.4): take update.lock, or identify and classify its holder and then wait, offer
/// "Stop it and continue", take over a proven orphan, or give up. It returns only while owning the lock, or throws
/// <see cref="UpdaterBusyException"/> whose <see cref="UpdaterBusyException.JournalPresent"/> says whether the launch must
/// be blocked. It never kills an unidentified holder, a live healthy holder, or a process outside this instance.
/// </summary>
internal sealed class LockCoordinator(
    ILockEnvironment environment,
    RunControl run,
    IProgress<UpdateProgress>? progress,
    Action<string> log)
{
    public async Task<FileStream> AcquireAsync()
    {
        CancellationToken token = run.Token;
        FileStream? held = await OperationLockFile.TryOpenAsync(
            environment.OpenLock, LockTimings.OpenAttempts, LockTimings.OpenRetryDelay, environment.DelayAsync, token);
        if (held is not null)
        {
            return held;
        }

        log("update.lock is held by another process after 10 attempts; identifying the holder.");
        string? holderKey = null;
        DateTime observedSince = environment.UtcNow;
        (HolderClass, HolderAction)? lastDecision = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            HolderSnapshot facts = environment.ProbeHolder();
            DateTime now = environment.UtcNow;
            if (!string.Equals(facts.Key, holderKey, StringComparison.Ordinal))
            {
                holderKey = facts.Key;
                observedSince = now;
                lastDecision = null;
                run.ClearTakeoverAcceptance();
                log($"Lock holder: {Describe(facts)}.");
            }
            HolderSnapshot snapshot = facts with { ObservedSinceUtc = observedSince };
            bool journalPresent = environment.JournalPresent();
            HolderAssessment assessment = HolderClassifier.Classify(snapshot, journalPresent, now);
            if (lastDecision != (assessment.Class, assessment.Action))
            {
                lastDecision = (assessment.Class, assessment.Action);
                log($"Lock holder classified {assessment.Class} -> {assessment.Action}: {assessment.Reason} (journal {(journalPresent ? "present" : "absent")}).");
            }

            switch (assessment.Action)
            {
                case HolderAction.TakeOver:
                    if (assessment.Class == HolderClass.OrphanSafe)
                    {
                        Report(UpdatePhase.Waiting, "Picking up an update from an earlier launch", "Your download continues where it stopped");
                        await environment.DelayAsync(LockTimings.TakeoverNotice, token);
                    }
                    FileStream? takenOver = await TakeOverAsync(snapshot, assessment);
                    if (takenOver is not null)
                    {
                        return takenOver;
                    }
                    break;
                case HolderAction.OfferStop:
                    if (!run.InteractiveUi)
                    {
                        // Nobody can answer the offer (--no-ui diagnostics), and it is never taken automatically.
                        throw GiveUp(journalPresent, assessment);
                    }
                    if (run.TakeoverAccepted)
                    {
                        run.ClearTakeoverAcceptance();
                        log("The player chose \"Stop it and continue\".");
                        FileStream? stopped = await TakeOverAsync(snapshot, assessment);
                        if (stopped is not null)
                        {
                            return stopped;
                        }
                        break;
                    }
                    ReportOffer(assessment);
                    break;
                case HolderAction.GiveUp:
                    throw GiveUp(journalPresent, assessment);
                default:
                    ReportWaiting(snapshot, assessment);
                    break;
            }

            await environment.DelayAsync(LockTimings.PollInterval, token);
            FileStream? released = await OperationLockFile.TryOpenAsync(
                environment.OpenLock, 1, TimeSpan.Zero, environment.DelayAsync, token);
            if (released is not null)
            {
                log("The other update released the lock; continuing.");
                return released;
            }
        }
    }

    /// <summary>
    /// FINDINGS §7.3: signal the stop event (a 1.2.18 holder cancels; an apply rolls back) and wait up to 15 s; if it is
    /// still there, re-validate and terminate it and wait up to 10 s; then reacquire (10 x 500 ms). Recovery of any journal
    /// it left runs right after acquisition in Program.RunUpdaterAsync, so there are never two writers.
    /// </summary>
    private async Task<FileStream?> TakeOverAsync(HolderSnapshot snapshot, HolderAssessment assessment)
    {
        CancellationToken token = run.Token;
        ProcessIdentity holder = snapshot.Holder
            ?? throw new InvalidOperationException("A takeover needs an identified holder.");
        log($"Taking over from {Describe(snapshot)} ({assessment.Class}: {assessment.Reason}).");
        Report(UpdatePhase.Waiting, "Picking up an update from an earlier launch", "Your download continues where it stopped");

        bool exited = false;
        // The stop event is per instance, not per holder: re-check that the classified holder still holds the record
        // right before signalling, so a signal meant for a dead holder cannot reach a newer, healthy owner.
        if (!string.Equals(environment.ProbeHolder().Key, snapshot.Key, StringComparison.Ordinal))
        {
            log("The lock holder changed before the takeover; classifying it again.");
            return null;
        }
        // A legacy (1.2.17) holder has no stop listener, so waiting 15 s for it would only delay the takeover.
        if (snapshot.Source == HolderSource.OwnerRecord && environment.SignalStop())
        {
            log("Asked the other updater to stop; waiting up to 15 s for it to finish safely.");
            exited = await environment.WaitForExitAsync(holder, LockTimings.CooperativeStopWait, token);
        }
        if (!exited)
        {
            if (!environment.TryTerminate(holder))
            {
                log($"Did not stop pid {holder.Pid}: it is no longer exactly the process that was classified (or not this instance's updater).");
                return null;
            }
            log($"Stopped the other updater (pid {holder.Pid}).");
            if (!await environment.WaitForExitAsync(holder, LockTimings.KillWait, token))
            {
                log($"pid {holder.Pid} has not exited 10 s after termination.");
            }
        }

        FileStream? held = await OperationLockFile.TryOpenAsync(
            environment.OpenLock, LockTimings.ReacquireAttempts, LockTimings.ReacquireRetryDelay, environment.DelayAsync, token);
        if (held is null)
        {
            log("The lock is still held after the takeover; checking the holder again.");
        }
        return held;
    }

    private UpdaterBusyException GiveUp(bool journalPresent, HolderAssessment assessment)
    {
        log($"Stopped waiting for update.lock ({assessment.Class}: {assessment.Reason}); an install journal is {(journalPresent ? "present" : "absent")}.");
        return new UpdaterBusyException(
            "Another process kept this instance's update lock.",
            journalPresent,
            assessment.Class);
    }

    private void ReportWaiting(HolderSnapshot snapshot, HolderAssessment assessment)
    {
        string countdown = FormatCountdown(assessment.Remaining);
        string detail = assessment.Class switch
        {
            HolderClass.OrphanApplying => $"Finishing an install from an earlier launch ({countdown})",
            HolderClass.UnknownHolder => $"Waiting for the updater's files to be free ({countdown})",
            _ when string.Equals(snapshot.Phase, nameof(UpdatePhase.Downloading), StringComparison.Ordinal) && snapshot.TotalBytes > 0 =>
                $"Downloading {Math.Clamp((int)Math.Round(snapshot.CompletedBytes * 100d / snapshot.TotalBytes), 0, 100)}% — waiting for it to finish ({countdown})",
            _ => $"Waiting for it to finish ({countdown})"
        };
        Report(UpdatePhase.Waiting, "Another update is already running for this pack", detail);
    }

    private void ReportOffer(HolderAssessment assessment)
    {
        if (assessment.Class == HolderClass.Stuck)
        {
            Report(UpdatePhase.WaitingCanStop, "The other update has stopped responding", "Stop it and continue, or keep waiting");
        }
        else
        {
            Report(UpdatePhase.WaitingCanStop, "Another update is still running for this pack", "Stop it and continue, or keep waiting");
        }
    }

    private void Report(UpdatePhase phase, string message, string detail) =>
        progress?.Report(new UpdateProgress(phase, message) { Detail = detail });

    internal static string FormatCountdown(TimeSpan remaining)
    {
        int seconds = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private static string Describe(HolderSnapshot snapshot) => snapshot.Holder is null
        ? "unidentified"
        : $"{snapshot.Source} pid {snapshot.Holder.Pid} started {snapshot.Holder.StartUtc:O} ({snapshot.Holder.ImagePath}), "
          + $"parent alive {Tri(snapshot.ParentAlive)}, Prism alive {Tri(snapshot.PrismAlive)}, phase {snapshot.Phase ?? "unknown"}, "
          + $"heartbeat {snapshot.HeartbeatUtc?.ToString("O") ?? "none"}, progress {snapshot.LastProgressUtc?.ToString("O") ?? "none"}";

    private static string Tri(bool? value) => value switch { true => "yes", false => "no", null => "n/a" };
}

/// <summary>Lock v2 entry point used by Program.RunUpdaterAsync.</summary>
internal static class OperationLockV2
{
    /// <summary>
    /// Acquires update.lock (waiting, taking over or giving up as classified), then writes the owner record, starts the
    /// heartbeat and listens for the stop event (which cancels <paramref name="run"/> as a peer stop).
    /// </summary>
    public static async Task<LockOwnership> AcquireAsync(
        UpdaterPaths paths,
        CommandLine options,
        RunControl run,
        IProgress<UpdateProgress>? progress,
        Action<string> log)
    {
        FileStream held;
        try
        {
            held = await new LockCoordinator(new SystemLockEnvironment(paths), run, progress, log).AcquireAsync();
        }
        catch (Exception exception) when (exception is (IOException or UnauthorizedAccessException) and not UpdaterBusyException)
        {
            // Not a sharing violation: the lock file itself cannot be opened (access denied, bad path, disk error).
            // Nothing was mutated by this run; whether the launch may continue depends only on a pending journal.
            bool journalPresent = File.Exists(TransactionStore.JournalPath(paths));
            log($"Could not open update.lock ({exception.GetType().Name}: {exception.Message}).");
            throw new UpdaterBusyException("The updater's lock file could not be opened.", journalPresent, HolderClass.UnknownHolder, exception);
        }
        var ownership = new LockOwnership(held, OperationLockFile.OwnerRecordPath(paths), CreateRecord(paths, options), () => DateTime.UtcNow, log);
        try
        {
            ownership.AttachStopListener(UpdaterStopSignal.Listen(paths, () =>
            {
                log("A later launch asked this update to stop (it is taking the update over).");
                run.RequestCancel(CancelReason.PeerStop);
            }));
            ownership.StartHeartbeat(LockTimings.HeartbeatInterval);
            run.OwnsLock = true;
            return ownership;
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    internal static LockOwnerRecord CreateRecord(UpdaterPaths paths, CommandLine options)
    {
        ProcessIdentity? self = ProcessTree.TryGetIdentity(Environment.ProcessId, out ProcessIdentity me) ? me : null;
        var record = new LockOwnerRecord
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Pid = Environment.ProcessId,
            ProcessStartUtc = self?.StartUtc ?? DateTime.UtcNow,
            ExePath = self?.ImagePath ?? Environment.ProcessPath ?? "",
            UpdaterVersion = typeof(OperationLockV2).Assembly.GetName().Version?.ToString() ?? "",
            InstanceIdentity = LocalStateStore.NormalizeInstanceIdentity(paths.InstanceDirectory)
        };
        PrismLaunchChain? chain = options.PrismPrelaunch ? PrismLaunchChain.TryResolveCurrent() : null;
        if (chain is not null)
        {
            ProcessIdentity parent = chain.PowerShell ?? chain.Prism;
            record.LaunchMode = chain.PowerShell is null
                ? LockOwnerRecord.LaunchModes.PrismDirect
                : LockOwnerRecord.LaunchModes.PrismPowerShell;
            record.ParentPid = parent.Pid;
            record.ParentStartUtc = parent.StartUtc;
            record.ParentImage = parent.ImagePath;
            record.PrismPid = chain.Prism.Pid;
            record.PrismStartUtc = chain.Prism.StartUtc;
            record.PrismImage = chain.Prism.ImagePath;
        }
        else if (ProcessTree.GetAncestors(Environment.ProcessId, maxDepth: 1) is [ProcessIdentity manualParent])
        {
            // Informational only: a manual run is never classified as an orphan.
            record.ParentPid = manualParent.Pid;
            record.ParentStartUtc = manualParent.StartUtc;
            record.ParentImage = manualParent.ImagePath;
        }
        return record;
    }
}
