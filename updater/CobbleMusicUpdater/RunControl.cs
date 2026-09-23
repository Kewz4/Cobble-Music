namespace CobbleMusicUpdater;

/// <summary>Why the run-level token was cancelled.</summary>
internal enum CancelReason
{
    None,
    /// <summary>The player pressed the card's X.</summary>
    UserClose,
    /// <summary>A later launch of this instance proved this run orphaned (or the player chose "Stop it and continue")
    /// and signalled <c>Local\CobbleMusicUpdater.Stop.&lt;identityHash&gt;</c>.</summary>
    PeerStop
}

/// <summary>
/// Lock v2: the ONE run-level cancellation source of an updater run. <see cref="Program.RunUpdaterAsync"/> creates it and
/// passes <see cref="Token"/> to the release check and the engine; the X button and the stop event cancel it. The engine's
/// transaction catch (UpdateEngine.ApplyTransactionAsync) turns a cancellation during Applying into a journal rollback,
/// so cancelling never leaves a half-applied pack behind.
/// </summary>
internal sealed class RunControl : IDisposable
{
    private static RunControl? s_current;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _takeoverAccepted;
    private bool _disposed;

    /// <summary>
    /// The run of this process (the latest one started). It deliberately stays readable after the run ends so the status
    /// card can still see why it ended (for example a peer stop, which closes the card at once).
    /// </summary>
    public static RunControl? Current => Volatile.Read(ref s_current);

    public static RunControl Begin()
    {
        var run = new RunControl();
        Volatile.Write(ref s_current, run);
        return run;
    }

    public CancellationToken Token => _cancellation.Token;

    public CancelReason Reason { get; private set; }

    /// <summary>Set once paths are resolved; lets the X button see whether a transaction journal exists.</summary>
    public UpdaterPaths? Paths { get; set; }

    /// <summary>The loaded configuration's offline policy (null until updater.json has been read).</summary>
    public bool? AllowOfflineLaunch { get; set; }

    /// <summary>The run's diagnostic log sink (Program.Log once the run has started).</summary>
    public Action<string> Log { get; set; } = _ => { };

    /// <summary>True when a status card can show the "Stop it and continue" choice to a person.</summary>
    public bool InteractiveUi { get; set; }

    /// <summary>Completes when <see cref="Program.RunUpdaterAsync"/> has returned (lock released, outcome decided).</summary>
    public Task Completion => _completion.Task;

    public bool IsFinished => _completion.Task.IsCompleted;

    /// <summary>
    /// transaction.json exists exactly while instance files may be half-applied (it is written before the first mutation
    /// and deleted after state.json commits; recovery deletes it after the rollback), so it is the precise
    /// "Applying or Recovering" signal for the X button.
    /// </summary>
    public bool TransactionMayBePending
    {
        get
        {
            UpdaterPaths? paths = Paths;
            try
            {
                return paths is not null && File.Exists(TransactionStore.JournalPath(paths));
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>True once this run holds update.lock (set by OperationLockV2).</summary>
    public bool OwnsLock { get; set; }

    /// <summary>
    /// A journal exists AND it is this run's own (it holds the lock). While merely waiting, a journal belongs to the other
    /// updater: the X then just stops waiting, and the launch decision (LaunchPolicy) still sees the journal.
    /// </summary>
    public bool OwnTransactionMayBePending => OwnsLock && TransactionMayBePending;

    public bool TakeoverAccepted => _takeoverAccepted;

    /// <summary>The player chose "Stop it and continue" on the status card.</summary>
    public void AcceptTakeover() => _takeoverAccepted = true;

    public void ClearTakeoverAcceptance() => _takeoverAccepted = false;

    /// <summary>Cancels the run. The first reason wins, so a later X cannot relabel a peer stop (or the reverse).</summary>
    public void RequestCancel(CancelReason reason)
    {
        if (reason == CancelReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (Reason == CancelReason.None)
            {
                Reason = reason;
            }
            _cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _completion.TrySetResult();
            _cancellation.Dispose();
        }
    }
}

/// <summary>What the card's X does, decided from facts only (the form executes it).</summary>
internal enum ForceCloseAction
{
    /// <summary>The run is over (or nothing can be pending): close now.</summary>
    ExitNow,
    /// <summary>Checking/downloading/waiting: cancel the run token, wait up to 5 s for it to stop, then exit.</summary>
    CancelThenExit,
    /// <summary>A transaction may be half-applied: cancel so the engine rolls back, show "Finishing safely", exit when done.</summary>
    FinishSafely,
    /// <summary>Second X while a transaction is still pending: warn, and only exit on an explicit yes.</summary>
    ConfirmHardExit
}

internal static class ForceClosePolicy
{
    /// <summary>Lock-lifecycle §7.5: the cooperative-cancel wait outside Applying.</summary>
    public static readonly TimeSpan CooperativeExitWait = TimeSpan.FromSeconds(5);

    public static ForceCloseAction Decide(bool runFinished, bool transactionMayBePending, bool cancelAlreadyRequested)
    {
        if (runFinished)
        {
            return ForceCloseAction.ExitNow;
        }
        if (cancelAlreadyRequested)
        {
            // A second X outside a transaction is harmless (downloads resume by Range); inside one it needs a warning.
            return transactionMayBePending ? ForceCloseAction.ConfirmHardExit : ForceCloseAction.ExitNow;
        }
        return transactionMayBePending ? ForceCloseAction.FinishSafely : ForceCloseAction.CancelThenExit;
    }
}

/// <summary>
/// Seam for "make Prism fail this pre-launch step". Production uses <see cref="PrismPreLaunchGate"/>; tests replace
/// <see cref="LaunchGate.Current"/> so no real process is ever terminated by the test suite.
/// </summary>
internal interface IPreLaunchGate
{
    bool TryFailPreLaunch(uint exitCode, Action<string> log);
}

internal sealed class PrismPreLaunchGate : IPreLaunchGate
{
    public bool TryFailPreLaunch(uint exitCode, Action<string> log)
    {
        // Resolved at the moment of failing, not at start: if Prism or the pre-launch PowerShell already ended there is
        // no launch left to stop, and the creation-time guard in GetAncestors rejects a reused PID.
        PrismLaunchChain? chain = PrismLaunchChain.TryResolveCurrent();
        if (chain is null)
        {
            log("This run is not a verified Prism pre-launch step (or Prism already ended it); there is no launch to stop.");
            return false;
        }
        return chain.TryFailPreLaunch(exitCode, log);
    }
}

internal static class LaunchGate
{
    /// <summary>
    /// The exit code the pre-launch PowerShell is terminated with. Distinct from the updater's own exit code 1 so Prism's
    /// "Pre-Launch command failed with code 3" identifies an updater-stopped launch in a friend's log.
    /// </summary>
    public const uint BlockedExitCode = 3;

    public static IPreLaunchGate Current { get; set; } = new PrismPreLaunchGate();

    /// <summary>
    /// The pinned bootstrap ignores the updater's exit code, so a Blocked run must end its own verified pre-launch
    /// PowerShell to keep Prism from starting Minecraft on a half-applied pack. Only runs in --prism-prelaunch mode.
    /// Returns true when Prism will see a failed pre-launch.
    /// </summary>
    public static bool FailPreLaunch(bool prismPrelaunch, Action<string> log)
    {
        if (!prismPrelaunch)
        {
            return false;
        }
        try
        {
            return Current.TryFailPreLaunch(BlockedExitCode, log);
        }
        catch (Exception exception)
        {
            log($"Could not stop this Prism launch ({exception.GetType().Name}: {exception.Message}).");
            return false;
        }
    }
}

/// <summary>How a run ended, for the launch decision.</summary>
internal enum RunEnd
{
    Success,
    /// <summary>Release check unavailable and offline launch allowed (nothing was mutated).</summary>
    OfflineFallback,
    /// <summary>A network failure after the check, offline launch allowed, and no transaction journal left behind.</summary>
    NetworkFallback,
    /// <summary>The player closed the card (X); the engine rolled any transaction back.</summary>
    UserCancelled,
    /// <summary>A later launch is taking this update over.</summary>
    PeerStopped,
    /// <summary>The lock stayed busy (or unopenable) and no transaction journal exists.</summary>
    BusyWithoutJournal,
    /// <summary>Recovery needs attention, integrity/apply failure, offline-disabled policy, or a busy lock with a journal.</summary>
    Blocked
}

internal static class LaunchPolicy
{
    /// <summary>
    /// True when this launch must not reach Minecraft. A journal still present after any non-success end means files may be
    /// half-applied, whatever the cause. A peer stop means another updater now owns the pack and is about to rewrite it.
    /// </summary>
    public static bool ShouldFailPreLaunch(RunEnd end, bool journalPresent) => end switch
    {
        RunEnd.Success => false,
        RunEnd.Blocked or RunEnd.PeerStopped => true,
        _ => journalPresent
    };
}

/// <summary>Lock v2 run endings shared by Program.RunUpdaterAsync (kept here so the shared Program.cs edit stays small).</summary>
internal static class RunOutcomes
{
    public const string StoppedDetail = "This launch was stopped so Minecraft can’t start on an unfinished pack";
    public const string NotStoppedDetail = "Don’t play until this is fixed — see updater.log";

    /// <summary>
    /// Blocked: make Prism fail the pre-launch step (the pinned bootstrap would otherwise continue on any exit code),
    /// report the reason, and return the updater's own failure code.
    /// </summary>
    public static int Blocked(
        CommandLine options,
        IProgress<UpdateProgress>? progress,
        Action<string> log,
        string status,
        int exitCode = 1)
    {
        bool stopped = LaunchGate.FailPreLaunch(options.PrismPrelaunch, log);
        progress?.Report(new UpdateProgress(UpdatePhase.Blocked, status) { Detail = stopped ? StoppedDetail : NotStoppedDetail });
        return exitCode == 0 ? 1 : exitCode;
    }

    /// <summary>The lock could not be taken: block only if an install journal says files may be half-applied.</summary>
    public static int Busy(CommandLine options, UpdaterBusyException exception, IProgress<UpdateProgress>? progress, Action<string> log)
    {
        if (LaunchPolicy.ShouldFailPreLaunch(RunEnd.BusyWithoutJournal, exception.JournalPresent))
        {
            log("An unfinished install is pending, so this launch is stopped until an updater can finish or undo it.");
            return Blocked(options, progress, log, "Couldn’t finish an earlier install. Check updater.log.");
        }
        log("No install is pending; Minecraft starts with the current pack.");
        progress?.Report(new UpdateProgress(UpdatePhase.Fallback, "Couldn’t start the update check")
        {
            Detail = "Something else is using the updater’s files. Minecraft will start with your current pack."
        });
        return 0;
    }

    /// <summary>
    /// The run token was cancelled. The engine has already rolled back any transaction (its catch runs recovery), so a
    /// player's X launches the current pack like 1.2.17 did; a peer stop, or a journal that survived, blocks this launch.
    /// </summary>
    public static int Cancelled(CommandLine options, RunControl run, IProgress<UpdateProgress>? progress, Action<string> log)
    {
        RunEnd end = run.Reason == CancelReason.PeerStop ? RunEnd.PeerStopped : RunEnd.UserCancelled;
        bool journalPresent = run.TransactionMayBePending;
        log(end == RunEnd.PeerStopped
            ? "Stopped so a later launch can take this update over."
            : "Closed by the player before the update finished; any unfinished install was undone.");
        if (LaunchPolicy.ShouldFailPreLaunch(end, journalPresent))
        {
            return Blocked(options, progress, log, end == RunEnd.PeerStopped
                ? "A newer launch took over this update."
                : "Couldn’t finish undoing the install. Check updater.log.");
        }
        progress?.Report(new UpdateProgress(UpdatePhase.Fallback, "Update stopped — your current pack was kept."));
        return 1;
    }
}
