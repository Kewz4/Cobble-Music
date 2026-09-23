namespace CobbleMusicUpdater;

internal enum HolderSource
{
    /// <summary>No identifiable updater of this instance holds the lock.</summary>
    Unknown,
    /// <summary>A 1.2.18+ holder whose owner record was re-checked against the OS.</summary>
    OwnerRecord,
    /// <summary>A holder found by process enumeration (a 1.2.17 or older updater writes no record).</summary>
    LegacyProcess
}

internal enum HolderClass
{
    OrphanSafe,
    OrphanApplying,
    Stuck,
    LiveHealthy,
    UnknownHolder
}

internal enum HolderAction
{
    /// <summary>Verified takeover (stop event, wait, re-validate, terminate, reacquire).</summary>
    TakeOver,
    /// <summary>Keep polling every second; <see cref="HolderAssessment.Remaining"/> is the countdown.</summary>
    Wait,
    /// <summary>Offer "Stop it and continue" to the player; never automatic.</summary>
    OfferStop,
    /// <summary>Stop waiting: launch the current pack when no journal exists, otherwise block this launch.</summary>
    GiveUp
}

/// <summary>
/// Everything a waiter knows about the process holding <c>update.lock</c>, gathered by <see cref="IHolderProbe"/>.
/// Liveness facts are three-valued: null means "not a Prism launch / not recorded", which never makes an orphan.
/// </summary>
internal sealed record HolderSnapshot(
    HolderSource Source,
    ProcessIdentity? Holder,
    bool? ParentAlive,
    bool? PrismAlive,
    string? Phase,
    DateTime? HeartbeatUtc,
    DateTime? LastProgressUtc,
    long CompletedBytes = 0,
    long TotalBytes = 0)
{
    /// <summary>When this waiter first saw this holder (the 30 s / 5 min / 10 min windows count from here).</summary>
    public DateTime ObservedSinceUtc { get; init; }

    /// <summary>
    /// A holder is an orphan when the Prism launch that started it is gone: its pre-launch parent or its Prism died.
    /// Every long-lived holder in FINDINGS §4 has this property; a manual or live run never does.
    /// </summary>
    public bool IsOrphan => Source != HolderSource.Unknown && (ParentAlive == false || PrismAlive == false);

    /// <summary>Stable key of the holder, so a different holder restarts the wait windows.</summary>
    public string Key => Holder is null ? "unknown" : $"{Holder.Pid}@{Holder.StartUtc.Ticks}";

    public static HolderSnapshot Unknown() => new(HolderSource.Unknown, null, null, null, null, null, null);
}

internal sealed record HolderAssessment(HolderClass Class, HolderAction Action, TimeSpan Remaining, string Reason);

internal static class HolderClassifier
{
    /// <summary>Phases in which the holder is talking to the network (the 180 s no-progress rule applies only there).</summary>
    public static bool IsNetworkPhase(string? phase) => phase is
        nameof(UpdatePhase.Checking)
        or nameof(UpdatePhase.VerifyingRelease)
        or nameof(UpdatePhase.UpdateAvailable)
        or nameof(UpdatePhase.Downloading);

    /// <summary>
    /// Pure classification (FINDINGS §7.2). "Older than" is strict: a heartbeat exactly 30 s old is still fresh, 31 s is
    /// stale; the same for 120 s and 180 s of progress. The wait windows (30 s, 5 min, 10 min) expire when the full span
    /// has passed (&gt;=).
    /// </summary>
    public static HolderAssessment Classify(HolderSnapshot snapshot, bool journalPresent, DateTime now)
    {
        TimeSpan observed = now - snapshot.ObservedSinceUtc;
        if (observed < TimeSpan.Zero)
        {
            observed = TimeSpan.Zero;
        }

        if (snapshot.Source == HolderSource.Unknown)
        {
            // Never kill what cannot be identified. Wait out transient handles, then launch only if nothing is half-applied.
            return observed < LockTimings.UnknownHolderWait
                ? new(HolderClass.UnknownHolder, HolderAction.Wait, LockTimings.UnknownHolderWait - observed, "no updater of this instance holds the lock")
                : new(HolderClass.UnknownHolder, HolderAction.GiveUp, TimeSpan.Zero, "the lock stayed held by something that is not this instance's updater");
        }

        TimeSpan? heartbeatAge = snapshot.HeartbeatUtc is DateTime heartbeat ? now - heartbeat : null;
        TimeSpan? progressAge = snapshot.LastProgressUtc is DateTime progressed ? now - progressed : null;
        bool heartbeatStale = heartbeatAge > LockTimings.HeartbeatStale;

        if (snapshot.IsOrphan)
        {
            if (!journalPresent)
            {
                return new(HolderClass.OrphanSafe, HolderAction.TakeOver, TimeSpan.Zero, "its Prism launch is gone and no install is in progress");
            }
            if (heartbeatStale)
            {
                return new(HolderClass.OrphanApplying, HolderAction.TakeOver, TimeSpan.Zero, "its Prism launch is gone and its heartbeat stopped");
            }
            if (progressAge > LockTimings.ProgressStaleApplying)
            {
                return new(HolderClass.OrphanApplying, HolderAction.TakeOver, TimeSpan.Zero, "its Prism launch is gone and its install stopped making progress");
            }
            if (observed >= LockTimings.ApplyingHolderWait)
            {
                return new(HolderClass.OrphanApplying, HolderAction.TakeOver, TimeSpan.Zero, "its Prism launch is gone and it did not finish installing within 5 minutes");
            }
            return new(HolderClass.OrphanApplying, HolderAction.Wait, LockTimings.ApplyingHolderWait - observed, "its Prism launch is gone but it is installing files");
        }

        if (heartbeatStale)
        {
            return new(HolderClass.Stuck, HolderAction.OfferStop, TimeSpan.Zero, "its heartbeat stopped");
        }
        if (IsNetworkPhase(snapshot.Phase) && progressAge > LockTimings.ProgressStaleNetwork)
        {
            return new(HolderClass.Stuck, HolderAction.OfferStop, TimeSpan.Zero, "its download made no progress for 3 minutes");
        }
        return observed < LockTimings.HealthyHolderWait
            ? new(HolderClass.LiveHealthy, HolderAction.Wait, LockTimings.HealthyHolderWait - observed, "a live launch is updating this pack")
            : new(HolderClass.LiveHealthy, HolderAction.OfferStop, TimeSpan.Zero, "a live launch is still updating this pack after 10 minutes");
    }

    /// <summary>
    /// True only for this instance's own updater exe: <c>&lt;minecraft&gt;\cobble-music-updater\CobbleMusicUpdater.exe</c>.
    /// Required before a holder is believed and again before it is terminated, so an updater of another instance (or any
    /// other program) is never killed.
    /// </summary>
    public static bool IsHolderImageForInstance(string imagePath, string installationDirectory)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(installationDirectory))
        {
            return false;
        }
        try
        {
            string image = Path.GetFullPath(imagePath);
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory));
            return string.Equals(Path.GetFileName(image), "CobbleMusicUpdater.exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(image), directory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>Gathers holder facts from the owner record and the OS.</summary>
internal interface IHolderProbe
{
    HolderSnapshot Probe();
}

/// <summary>
/// Step 1 of FINDINGS §7.2: (a) the owner record, believed only when the pid is alive with a matching start time (±1 s),
/// its image is this instance's updater exe and the instance identity matches; (b) otherwise enumeration of
/// <c>CobbleMusicUpdater.exe</c> processes running from this instance's install directory (legacy holders); (c) unknown.
/// </summary>
internal sealed class SystemHolderProbe(UpdaterPaths paths) : IHolderProbe
{
    public HolderSnapshot Probe()
    {
        LockOwnerRecord? record = LockOwnerRecordStore.TryRead(OperationLockFile.OwnerRecordPath(paths));
        if (record is not null && TryFromRecord(record, out HolderSnapshot? fromRecord))
        {
            return fromRecord;
        }
        return TryFromEnumeration(out HolderSnapshot? legacy) ? legacy : HolderSnapshot.Unknown();
    }

    private bool TryFromRecord(LockOwnerRecord record, out HolderSnapshot snapshot)
    {
        snapshot = null!;
        if (record.Pid == Environment.ProcessId
            || !string.Equals(record.InstanceIdentity, LocalStateStore.NormalizeInstanceIdentity(paths.InstanceDirectory), StringComparison.Ordinal)
            || !ProcessTree.TryGetIdentity(record.Pid, out ProcessIdentity live)
            || (live.StartUtc - record.ProcessStartUtc).Duration() > LockTimings.StartTimeTolerance
            || !HolderClassifier.IsHolderImageForInstance(live.ImagePath, paths.InstallationDirectory))
        {
            return false;
        }
        bool? parentAlive = null;
        bool? prismAlive = null;
        if (record.IsPrismLaunch)
        {
            // Recorded identities are checked exactly (pid + creation time + image), so a reused PID reads as dead.
            parentAlive = record.ParentIdentity is ProcessIdentity parent ? ProcessTree.IsAlive(parent) : false;
            prismAlive = record.PrismIdentity is ProcessIdentity prism ? ProcessTree.IsAlive(prism) : false;
        }
        snapshot = new HolderSnapshot(
            HolderSource.OwnerRecord,
            live,
            parentAlive,
            prismAlive,
            record.Phase,
            record.HeartbeatUtc,
            record.LastProgressUtc,
            record.CompletedBytes,
            record.TotalBytes);
        return true;
    }

    private bool TryFromEnumeration(out HolderSnapshot snapshot)
    {
        snapshot = null!;
        ProcessIdentity? holder = null;
        foreach ((int pid, (_, string exeName)) in ProcessTree.Snapshot())
        {
            if (pid == Environment.ProcessId
                || !string.Equals(exeName, "CobbleMusicUpdater.exe", StringComparison.OrdinalIgnoreCase)
                || !ProcessTree.TryGetIdentity(pid, out ProcessIdentity candidate)
                || !HolderClassifier.IsHolderImageForInstance(candidate.ImagePath, paths.InstallationDirectory))
            {
                continue;
            }
            // The holder took the lock before any later updater started, so the earliest-started candidate is the holder.
            // Choosing a later waiter by mistake only errs towards "live" (its parent is alive), never towards a kill.
            if (holder is null || candidate.StartUtc < holder.StartUtc)
            {
                holder = candidate;
            }
        }
        if (holder is null)
        {
            return false;
        }
        (bool? parentAlive, bool? prismAlive) = LegacyLiveness(ProcessTree.GetAncestors(holder.Pid, maxDepth: 2));
        snapshot = new HolderSnapshot(HolderSource.LegacyProcess, holder, parentAlive, prismAlive, null, null, null);
        return true;
    }

    /// <summary>
    /// Liveness of a legacy holder from its verified live ancestors (dead or reused parents are cut off by GetAncestors'
    /// creation-time guard). No live parent: orphan. A pre-launch PowerShell whose own parent is gone: orphan too - that is
    /// the proven 2026-09-22 case (Prism closed while the bootstrap was still downloading; the bootstrap then started the
    /// updater with no Prism waiting). Any other live parent (Prism, a terminal): a live run, never killed automatically.
    /// </summary>
    internal static (bool? ParentAlive, bool? PrismAlive) LegacyLiveness(IReadOnlyList<ProcessIdentity> ancestors)
    {
        if (ancestors.Count == 0)
        {
            return (false, null);
        }
        if (PrismLaunchChain.IsPrism(ancestors[0]))
        {
            return (true, true);
        }
        if (PrismLaunchChain.IsPowerShell(ancestors[0]))
        {
            if (ancestors.Count == 1)
            {
                return (true, false);
            }
            return (true, PrismLaunchChain.IsPrism(ancestors[1]) ? true : null);
        }
        return (true, null);
    }
}
