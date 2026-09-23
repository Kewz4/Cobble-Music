using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using CobbleMusicUpdater;

/// <summary>
/// Lock v2 (updater 1.2.18) tests: sharing-violation-only busy, owner record, stop event, the pure holder classification
/// table, coordinator decisions under an injected clock and injected process facts, verified takeover against real child
/// processes, the launch-fail seam on Blocked runs, and X during Applying rolling back before the card closes.
/// Nothing here terminates a process the suite did not start itself: the launch gate is always a fake.
/// </summary>
internal static class LockV2Tests
{
    private static readonly DateTime T0 = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await TestSharingViolationOnlyIsBusyAsync(Path.Combine(root, "sharing"));
        TestOwnerRecordRoundTrip(Path.Combine(root, "record"));
        TestStopSignal(Path.Combine(root, "stop-signal"));
        TestHolderClassificationTable();
        TestLegacyLivenessAndImageScope();
        await TestCoordinatorDecisionsAsync();
        await TestRealHolderProbeAndVerifiedTerminationAsync(Path.Combine(root, "real-holder"));
        TestLaunchPolicyAndOutcomes(Path.Combine(root, "outcomes"));
        await TestBlockedRunInvokesLaunchGateAsync(Path.Combine(root, "blocked-run"));
        await TestUnknownHolderWithoutJournalLaunchesAsync(Path.Combine(root, "unknown-holder"));
        await TestCancelDuringApplyRollsBackAsync(Path.Combine(root, "cancel-apply"));
        TestForceClosePolicyTable();
        TestStatusCardXDuringApplyWaitsForRollback();
        Console.WriteLine("Lock v2 checks passed: sharing-violation busy, owner record, stop event, holder classification, verified takeover, launch gate, X rollback.");
    }

    // ---- 1/2. Busy only on a sharing violation; a short-lived handle is absorbed by 10 x 250 ms ------------------

    private static async Task TestSharingViolationOnlyIsBusyAsync(string root)
    {
        Directory.CreateDirectory(root);
        Equal(true, OperationLockFile.IsSharingViolation(new IOException("x", unchecked((int)0x80070020))), "0x80070020 is busy");
        Equal(false, OperationLockFile.IsSharingViolation(new IOException("x", unchecked((int)0x80070005))), "0x80070005 is a real error");
        Equal(false, OperationLockFile.IsSharingViolation(new IOException("x", unchecked((int)0x80070003))), "0x80070003 is a real error");

        string lockPath = Path.Combine(root, "update.lock");
        using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            IOException? observed = null;
            try
            {
                using var second = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                observed = exception;
            }
            Equal(true, observed is not null && OperationLockFile.IsSharingViolation(observed), "a real held lock raises a sharing violation");

            int attempts = 0;
            FileStream? busy = await OperationLockFile.TryOpenAsync(
                () => { attempts++; return Open(lockPath); }, 10, TimeSpan.FromMilliseconds(250), Task.Delay, CancellationToken.None);
            Equal<FileStream?>(null, busy, "a lock held throughout stays busy");
            Equal(10, attempts, "busy after exactly 10 attempts");
        }

        foreach (int hresult in new[] { unchecked((int)0x80070005), unchecked((int)0x80070003) })
        {
            int calls = 0;
            await ThrowsAsync<IOException>(() => OperationLockFile.TryOpenAsync(
                () => { calls++; throw new IOException("real", hresult); }, 10, TimeSpan.Zero, Task.Delay, CancellationToken.None));
            Equal(1, calls, $"0x{hresult:X8} propagates on the first attempt, not retried as busy");
        }
        await ThrowsAsync<UnauthorizedAccessException>(() => OperationLockFile.TryOpenAsync(
            () => throw new UnauthorizedAccessException("denied"), 10, TimeSpan.Zero, Task.Delay, CancellationToken.None));

        var transient = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Delay(600).ContinueWith(_ => transient.Dispose(), TaskScheduler.Default);
        var clock = Stopwatch.StartNew();
        using FileStream? acquired = await OperationLockFile.TryOpenAsync(
            () => Open(lockPath), 10, TimeSpan.FromMilliseconds(250), Task.Delay, CancellationToken.None);
        Equal(true, acquired is not null, "a handle held for 600 ms is absorbed by the retry");
        Equal(true, clock.Elapsed >= TimeSpan.FromMilliseconds(500), "the retry actually waited for the transient handle");
    }

    private static FileStream Open(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    // ---- 3. Owner record: atomic write, readable while locked, heartbeat/progress, deleted before the lock is released --

    private static void TestOwnerRecordRoundTrip(string root)
    {
        UpdaterPaths paths = Paths(root, "record0000000001");
        Directory.CreateDirectory(paths.LocalDataDirectory);
        DateTime now = T0;
        var record = new LockOwnerRecord
        {
            SessionId = "session",
            Pid = 4242,
            ProcessStartUtc = T0.AddMinutes(-3).AddTicks(1234567),
            ExePath = Path.Combine(paths.InstallationDirectory, "CobbleMusicUpdater.exe"),
            UpdaterVersion = "1.2.18.0",
            InstanceIdentity = LocalStateStore.NormalizeInstanceIdentity(paths.InstanceDirectory),
            LaunchMode = LockOwnerRecord.LaunchModes.PrismPowerShell,
            ParentPid = 100,
            ParentStartUtc = T0.AddMinutes(-4).AddTicks(7),
            ParentImage = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            PrismPid = 50,
            PrismStartUtc = T0.AddHours(-1).AddTicks(3),
            PrismImage = @"C:\Prism\prismlauncher.exe"
        };
        string recordPath = OperationLockFile.OwnerRecordPath(paths);
        FileStream held = OperationLockFile.Open(paths);
        var ownership = new LockOwnership(held, recordPath, record, () => now, _ => { });
        try
        {
            LockOwnerRecord? read = LockOwnerRecordStore.TryRead(recordPath);
            Equal(true, read is not null, "owner record readable while the lock is held");
            Equal(4242, read!.Pid, "pid round-trips");
            Equal(record.ProcessStartUtc, read.ProcessStartUtc, "start time round-trips to the tick");
            Equal(DateTimeKind.Utc, read.ProcessStartUtc.Kind, "start time stays UTC");
            Equal(record.ParentStartUtc, read.ParentStartUtc, "parent start round-trips");
            Equal(true, read.ParentIdentity!.Matches(new ProcessIdentity(100, record.ParentStartUtc!.Value, record.ParentImage!)), "parent identity round-trips");
            Equal(true, read.PrismIdentity!.Matches(new ProcessIdentity(50, record.PrismStartUtc!.Value, record.PrismImage!)), "Prism identity round-trips");
            Equal(true, read.IsPrismLaunch, "launch mode round-trips");
            Equal(T0, read.HeartbeatUtc, "initial heartbeat");
            Equal("Starting", read.Phase, "initial phase");
            Equal(false, File.Exists(recordPath + ".new"), "atomic write leaves no temporary file");

            IOException? busy = null;
            try
            {
                using FileStream second = OperationLockFile.Open(paths);
            }
            catch (IOException exception)
            {
                busy = exception;
            }
            Equal(true, busy is not null && OperationLockFile.IsSharingViolation(busy), "the lock itself stays exclusive");

            now = T0.AddSeconds(5);
            IProgress<UpdateProgress> tracked = ownership.Track(null);
            tracked.Report(new UpdateProgress(UpdatePhase.Downloading, "Downloading", 450, 1000));
            Equal(T0, LockOwnerRecordStore.TryRead(recordPath)!.HeartbeatUtc, "progress alone does not rewrite the file");
            now = T0.AddSeconds(6);
            ownership.Heartbeat();
            LockOwnerRecord? beat = LockOwnerRecordStore.TryRead(recordPath);
            Equal(T0.AddSeconds(6), beat!.HeartbeatUtc, "heartbeat advances");
            Equal(T0.AddSeconds(5), beat.LastProgressUtc, "progress time comes from the progress callback");
            Equal("Downloading", beat.Phase, "phase follows progress");
            Equal(450L, beat.CompletedBytes, "completed bytes recorded");
            Equal(1000L, beat.TotalBytes, "total bytes recorded");
        }
        finally
        {
            ownership.Dispose();
        }
        Equal(false, File.Exists(recordPath), "record deleted on dispose");
        using (FileStream reopened = OperationLockFile.Open(paths))
        {
            Equal(true, reopened.CanWrite, "lock released on dispose");
        }

        File.WriteAllText(recordPath, "{ not json");
        Equal<LockOwnerRecord?>(null, LockOwnerRecordStore.TryRead(recordPath), "malformed record reads as absent");
        File.WriteAllText(recordPath, "{\"schemaVersion\":2,\"pid\":5}");
        Equal<LockOwnerRecord?>(null, LockOwnerRecordStore.TryRead(recordPath), "unknown schema reads as absent");
        File.Delete(recordPath);
        Equal<LockOwnerRecord?>(null, LockOwnerRecordStore.TryRead(recordPath), "missing record reads as absent");

        LockOwnerRecord self = OperationLockV2.CreateRecord(paths, new CommandLine(paths.InstanceDirectory, paths.MinecraftDirectory, false, false, true));
        Equal(Environment.ProcessId, self.Pid, "own record pid");
        Equal(true, ProcessTree.TryGetIdentity(Environment.ProcessId, out ProcessIdentity me) && me.StartUtc == self.ProcessStartUtc, "own record start time is the OS creation time");
        Equal(LockOwnerRecord.LaunchModes.Manual, self.LaunchMode, "a run outside Prism is recorded as manual");
        Equal(false, self.IsPrismLaunch, "a manual run can never be classified as an orphan");
    }

    // ---- Stop event ------------------------------------------------------------------------------------------------

    private static void TestStopSignal(string root)
    {
        UpdaterPaths paths = Paths(root, "stop" + Guid.NewGuid().ToString("N")[..12]);
        Equal(@"Local\CobbleMusicUpdater.Stop." + Path.GetFileName(paths.LocalDataDirectory).ToLowerInvariant(), UpdaterStopSignal.EventName(paths), "event name uses the lock directory identity hash");
        Equal(false, UpdaterStopSignal.Signal(paths), "no listener: signal reports nobody to stop (legacy holder)");

        // A signal left over from an earlier takeover must not cancel a new, healthy owner.
        using (var stale = new EventWaitHandle(false, EventResetMode.AutoReset, UpdaterStopSignal.EventName(paths)))
        {
            stale.Set();
            using var fired = new ManualResetEventSlim(false);
            using (UpdaterStopSignal.Listen(paths, fired.Set))
            {
                Equal(false, fired.Wait(300), "stale signal is reset before listening");
                Equal(true, UpdaterStopSignal.Signal(paths), "a listener exists");
                Equal(true, fired.Wait(5000), "a real signal reaches the holder");
            }
        }

        using var run = RunControl.Begin();
        using var ownerFired = new ManualResetEventSlim(false);
        using (UpdaterStopSignal.Listen(paths, () => { run.RequestCancel(CancelReason.PeerStop); ownerFired.Set(); }))
        {
            UpdaterStopSignal.Signal(paths);
            Equal(true, ownerFired.Wait(5000), "stop event cancels the run");
        }
        Equal(true, run.Token.IsCancellationRequested, "run token cancelled by the stop event");
        Equal(CancelReason.PeerStop, run.Reason, "cancel reason is a peer stop");
        run.RequestCancel(CancelReason.UserClose);
        Equal(CancelReason.PeerStop, run.Reason, "the first reason wins");
    }

    // ---- 6-11. Pure classification table --------------------------------------------------------------------------

    private static HolderSnapshot Holder(
        HolderSource source = HolderSource.OwnerRecord,
        bool? parentAlive = true,
        bool? prismAlive = true,
        string? phase = "Downloading",
        double? heartbeatAgeSeconds = 0,
        double? progressAgeSeconds = 0,
        double observedSeconds = 0) =>
        new HolderSnapshot(
            source,
            new ProcessIdentity(777, T0.AddHours(-1), @"C:\i\minecraft\cobble-music-updater\CobbleMusicUpdater.exe"),
            parentAlive,
            prismAlive,
            phase,
            heartbeatAgeSeconds is double h ? T0.AddSeconds(-h) : null,
            progressAgeSeconds is double p ? T0.AddSeconds(-p) : null,
            450,
            1000)
        { ObservedSinceUtc = T0.AddSeconds(-observedSeconds) };

    private static void Expect(HolderSnapshot snapshot, bool journal, HolderClass expectedClass, HolderAction expectedAction, string context, double? remainingSeconds = null)
    {
        HolderAssessment assessment = HolderClassifier.Classify(snapshot, journal, T0);
        Equal(expectedClass, assessment.Class, $"{context}: class");
        Equal(expectedAction, assessment.Action, $"{context}: action");
        if (remainingSeconds is double remaining)
        {
            Equal(TimeSpan.FromSeconds(remaining), assessment.Remaining, $"{context}: countdown");
        }
    }

    private static void TestHolderClassificationTable()
    {
        // UNKNOWN_HOLDER: wait 30 s, then give up (the caller blocks only when a journal exists); never TakeOver.
        HolderSnapshot unknown = HolderSnapshot.Unknown() with { ObservedSinceUtc = T0 };
        Expect(unknown, false, HolderClass.UnknownHolder, HolderAction.Wait, "unknown at 0 s", 30);
        Expect(unknown with { ObservedSinceUtc = T0.AddSeconds(-29) }, true, HolderClass.UnknownHolder, HolderAction.Wait, "unknown at 29 s", 1);
        Expect(unknown with { ObservedSinceUtc = T0.AddSeconds(-30) }, false, HolderClass.UnknownHolder, HolderAction.GiveUp, "unknown at 30 s, no journal");
        Expect(unknown with { ObservedSinceUtc = T0.AddSeconds(-30) }, true, HolderClass.UnknownHolder, HolderAction.GiveUp, "unknown at 30 s, journal");

        // ORPHAN_SAFE: parent PowerShell dead, or Prism dead; journal absent.
        Expect(Holder(parentAlive: false), false, HolderClass.OrphanSafe, HolderAction.TakeOver, "parent dead, no journal");
        Expect(Holder(prismAlive: false), false, HolderClass.OrphanSafe, HolderAction.TakeOver, "Prism dead, no journal");
        Expect(Holder(source: HolderSource.LegacyProcess, parentAlive: false, prismAlive: null, phase: null, heartbeatAgeSeconds: null, progressAgeSeconds: null),
            false, HolderClass.OrphanSafe, HolderAction.TakeOver, "legacy holder with dead parent");

        // ORPHAN_APPLYING: journal present. Heartbeat 30/31 s, progress 120/121 s, 5 min window.
        Expect(Holder(parentAlive: false, phase: "Applying"), true, HolderClass.OrphanApplying, HolderAction.Wait, "applying orphan, fresh", 300);
        Expect(Holder(parentAlive: false, phase: "Applying", heartbeatAgeSeconds: 30), true, HolderClass.OrphanApplying, HolderAction.Wait, "applying orphan, heartbeat 30 s");
        Expect(Holder(parentAlive: false, phase: "Applying", heartbeatAgeSeconds: 31), true, HolderClass.OrphanApplying, HolderAction.TakeOver, "applying orphan, heartbeat 31 s");
        Expect(Holder(parentAlive: false, phase: "Applying", progressAgeSeconds: 120), true, HolderClass.OrphanApplying, HolderAction.Wait, "applying orphan, progress 120 s");
        Expect(Holder(parentAlive: false, phase: "Applying", progressAgeSeconds: 121), true, HolderClass.OrphanApplying, HolderAction.TakeOver, "applying orphan, progress 121 s");
        Expect(Holder(parentAlive: false, phase: "Applying", observedSeconds: 299), true, HolderClass.OrphanApplying, HolderAction.Wait, "applying orphan at 4:59", 1);
        Expect(Holder(parentAlive: false, phase: "Applying", observedSeconds: 300), true, HolderClass.OrphanApplying, HolderAction.TakeOver, "applying orphan at 5:00");
        Expect(Holder(source: HolderSource.LegacyProcess, parentAlive: false, prismAlive: null, phase: null, heartbeatAgeSeconds: null, progressAgeSeconds: null, observedSeconds: 120),
            true, HolderClass.OrphanApplying, HolderAction.Wait, "legacy applying orphan only has the 5 min rule", 180);

        // STUCK: not an orphan; heartbeat 29/30/31 s; network no-progress 179/180/181 s.
        Expect(Holder(heartbeatAgeSeconds: 29), false, HolderClass.LiveHealthy, HolderAction.Wait, "heartbeat 29 s");
        Expect(Holder(heartbeatAgeSeconds: 30), false, HolderClass.LiveHealthy, HolderAction.Wait, "heartbeat 30 s");
        Expect(Holder(heartbeatAgeSeconds: 31), false, HolderClass.Stuck, HolderAction.OfferStop, "heartbeat 31 s");
        Expect(Holder(heartbeatAgeSeconds: 31), true, HolderClass.Stuck, HolderAction.OfferStop, "heartbeat 31 s with journal");
        Expect(Holder(progressAgeSeconds: 179), false, HolderClass.LiveHealthy, HolderAction.Wait, "download progress 179 s");
        Expect(Holder(progressAgeSeconds: 180), false, HolderClass.LiveHealthy, HolderAction.Wait, "download progress 180 s");
        Expect(Holder(progressAgeSeconds: 181), false, HolderClass.Stuck, HolderAction.OfferStop, "download progress 181 s");
        Expect(Holder(phase: "Checking", progressAgeSeconds: 181), false, HolderClass.Stuck, HolderAction.OfferStop, "checking counts as a network phase");
        Expect(Holder(phase: "Applying", progressAgeSeconds: 181), true, HolderClass.LiveHealthy, HolderAction.Wait, "the 180 s rule is for network phases only");

        // LIVE_HEALTHY: wait 10 min with a countdown, then only offer the button; never TakeOver.
        Expect(Holder(), false, HolderClass.LiveHealthy, HolderAction.Wait, "live healthy at 0 s", 600);
        Expect(Holder(observedSeconds: 599), false, HolderClass.LiveHealthy, HolderAction.Wait, "live healthy at 9:59", 1);
        Expect(Holder(observedSeconds: 600), false, HolderClass.LiveHealthy, HolderAction.OfferStop, "live healthy at 10:00");
        Expect(Holder(source: HolderSource.LegacyProcess, parentAlive: true, prismAlive: true, phase: null, heartbeatAgeSeconds: null, progressAgeSeconds: null, observedSeconds: 3600),
            true, HolderClass.LiveHealthy, HolderAction.OfferStop, "legacy holder with a live Prism is never auto-killed");
        Expect(Holder(parentAlive: null, prismAlive: null), false, HolderClass.LiveHealthy, HolderAction.Wait, "manual run is never an orphan");

        // Exhaustive guard: no live (non-orphan) or unknown holder is ever taken over automatically.
        foreach (bool journal in new[] { false, true })
        foreach (double heartbeat in new double[] { 0, 30, 31, 3600 })
        foreach (double progress in new double[] { 0, 180, 181, 3600 })
        foreach (double observed in new double[] { 0, 599, 600, 36000 })
        foreach (string phase in new[] { "Downloading", "Applying", "Starting" })
        {
            HolderAssessment live = HolderClassifier.Classify(Holder(phase: phase, heartbeatAgeSeconds: heartbeat, progressAgeSeconds: progress, observedSeconds: observed), journal, T0);
            Equal(false, live.Action == HolderAction.TakeOver, $"live holder never auto-taken over (journal {journal}, hb {heartbeat}, progress {progress}, observed {observed}, {phase})");
            HolderAssessment unknownAssessment = HolderClassifier.Classify(HolderSnapshot.Unknown() with { ObservedSinceUtc = T0.AddSeconds(-observed) }, journal, T0);
            Equal(false, unknownAssessment.Action is HolderAction.TakeOver or HolderAction.OfferStop, "unknown holder is never stopped");
        }

        Equal("0:30", LockCoordinator.FormatCountdown(TimeSpan.FromSeconds(30)), "countdown format");
        Equal("9:41", LockCoordinator.FormatCountdown(TimeSpan.FromSeconds(580.2)), "countdown rounds up");
    }

    // ---- 5/10. Legacy liveness and the instance image scope ------------------------------------------------------

    private static void TestLegacyLivenessAndImageScope()
    {
        var prism = new ProcessIdentity(1, T0, @"C:\Prism\prismlauncher.exe");
        var powershell = new ProcessIdentity(2, T0, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        var terminal = new ProcessIdentity(3, T0, @"C:\Program Files\WindowsApps\WindowsTerminal.exe");
        Equal(((bool?)false, (bool?)null), SystemHolderProbe.LegacyLiveness([]), "no live parent: orphan");
        Equal(((bool?)true, (bool?)false), SystemHolderProbe.LegacyLiveness([powershell]), "pre-launch PowerShell whose Prism is gone: orphan (2026-09-22)");
        Equal(((bool?)true, (bool?)true), SystemHolderProbe.LegacyLiveness([powershell, prism]), "PowerShell under a live Prism: live");
        Equal(((bool?)true, (bool?)null), SystemHolderProbe.LegacyLiveness([powershell, terminal]), "PowerShell from a terminal: a manual run, live");
        Equal(((bool?)true, (bool?)true), SystemHolderProbe.LegacyLiveness([prism]), "legacy direct Prism chain: live");
        Equal(((bool?)true, (bool?)null), SystemHolderProbe.LegacyLiveness([terminal]), "any other live parent: live");

        const string install = @"C:\Games\Inst A\minecraft\cobble-music-updater";
        Equal(true, HolderClassifier.IsHolderImageForInstance(install + @"\CobbleMusicUpdater.exe", install), "this instance's updater");
        Equal(true, HolderClassifier.IsHolderImageForInstance(install.ToUpperInvariant() + @"\cobblemusicupdater.EXE", install + @"\"), "case and trailing separator");
        Equal(false, HolderClassifier.IsHolderImageForInstance(@"C:\Games\Inst B\minecraft\cobble-music-updater\CobbleMusicUpdater.exe", install), "another instance's updater is never ours");
        Equal(false, HolderClassifier.IsHolderImageForInstance(install + @"2\CobbleMusicUpdater.exe", install), "sibling folder with a shared prefix");
        Equal(false, HolderClassifier.IsHolderImageForInstance(install + @"\old\CobbleMusicUpdater.exe", install), "subfolder");
        Equal(false, HolderClassifier.IsHolderImageForInstance(install + @"\cmd.exe", install), "another program in the folder");
        Equal(false, HolderClassifier.IsHolderImageForInstance("", install), "empty image");
    }

    // ---- Coordinator decisions under an injected clock and injected process facts ----------------------------------

    private sealed class FakeLockEnvironment : ILockEnvironment
    {
        private readonly string _lockPath;
        public DateTime Now = T0;
        public Func<FakeLockEnvironment, HolderSnapshot> Probe = _ => HolderSnapshot.Unknown();
        public bool HolderHoldsLock = true;
        public bool Journal;
        public bool ListenerPresent = true;
        public bool CooperativeExit;
        public bool TerminateSucceeds = true;
        public int Signals;
        public readonly List<ProcessIdentity> Terminated = [];
        public readonly List<TimeSpan> Delays = [];
        public Action<FakeLockEnvironment>? OnDelay;

        public FakeLockEnvironment(string lockPath) => _lockPath = lockPath;

        public DateTime UtcNow => Now;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            Now += delay;
            OnDelay?.Invoke(this);
            return Task.CompletedTask;
        }

        public FileStream OpenLock()
        {
            if (HolderHoldsLock)
            {
                throw new IOException("held", unchecked((int)0x80070020));
            }
            return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        public HolderSnapshot ProbeHolder() => Probe(this);

        public bool JournalPresent() => Journal;

        public bool SignalStop()
        {
            Signals++;
            if (ListenerPresent && CooperativeExit)
            {
                HolderHoldsLock = false;
            }
            return ListenerPresent;
        }

        public Task<bool> WaitForExitAsync(ProcessIdentity holder, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (HolderHoldsLock)
            {
                Now += timeout;
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }

        public bool TryTerminate(ProcessIdentity holder)
        {
            if (!TerminateSucceeds)
            {
                return false;
            }
            Terminated.Add(holder);
            HolderHoldsLock = false;
            return true;
        }
    }

    private static HolderSnapshot LiveFacts(FakeLockEnvironment environment, bool? parentAlive = true, bool? prismAlive = true,
        HolderSource source = HolderSource.OwnerRecord, string phase = "Downloading", DateTime? heartbeat = null, DateTime? progress = null) =>
        new(source,
            new ProcessIdentity(4321, T0.AddMinutes(-2), @"C:\i\minecraft\cobble-music-updater\CobbleMusicUpdater.exe"),
            parentAlive,
            prismAlive,
            source == HolderSource.OwnerRecord ? phase : null,
            source == HolderSource.OwnerRecord ? heartbeat ?? environment.Now : null,
            source == HolderSource.OwnerRecord ? progress ?? environment.Now : null,
            450,
            1000);

    private static async Task<(FileStream? Stream, Exception? Failure, CapturedProgress Progress, List<string> Log)> AcquireWithAsync(
        FakeLockEnvironment environment, bool interactive, Action<RunControl>? configure = null)
    {
        using RunControl run = RunControl.Begin();
        run.InteractiveUi = interactive;
        configure?.Invoke(run);
        var progress = new CapturedProgress();
        var log = new List<string>();
        try
        {
            FileStream stream = await new LockCoordinator(environment, run, progress, log.Add).AcquireAsync();
            return (stream, null, progress, log);
        }
        catch (Exception exception)
        {
            return (null, exception, progress, log);
        }
    }

    private static async Task TestCoordinatorDecisionsAsync()
    {
        string lockDirectory = Path.Combine(Path.GetTempPath(), "cobble-lockv2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lockDirectory);
        string lockPath = Path.Combine(lockDirectory, "update.lock");
        try
        {
            // Free lock: taken at once, nothing probed.
            var free = new FakeLockEnvironment(lockPath) { HolderHoldsLock = false };
            var freeResult = await AcquireWithAsync(free, interactive: true);
            Equal(true, freeResult.Stream is not null, "free lock acquired");
            freeResult.Stream!.Dispose();
            Equal(0, free.Delays.Count, "free lock needs no retries");

            // ORPHAN_SAFE 1.2.18 holder: 3 s notice, stop event, cooperative exit, no kill.
            var cooperative = new FakeLockEnvironment(lockPath) { CooperativeExit = true };
            cooperative.Probe = e => LiveFacts(e, parentAlive: false);
            var cooperativeResult = await AcquireWithAsync(cooperative, interactive: true);
            Equal(true, cooperativeResult.Stream is not null, "orphan-safe takeover acquires the lock");
            cooperativeResult.Stream!.Dispose();
            Equal(1, cooperative.Signals, "stop event signalled once");
            Equal(0, cooperative.Terminated.Count, "a holder that stops cooperatively is not killed");
            Equal(10, cooperative.Delays.Count(delay => delay == TimeSpan.FromMilliseconds(250)) + 1, "10 x 250 ms first attempts before identifying the holder");
            Equal(true, cooperative.Delays.Contains(TimeSpan.FromSeconds(3)), "3 s takeover notice");
            Equal(true, cooperativeResult.Progress.Updates.Any(u => u.Message == "Picking up an update from an earlier launch" && u.Detail == "Your download continues where it stopped"), "takeover text");

            // ORPHAN_SAFE legacy holder: no stop event (it cannot listen), verified terminate, reacquire.
            var legacy = new FakeLockEnvironment(lockPath);
            legacy.Probe = e => LiveFacts(e, parentAlive: false, prismAlive: null, source: HolderSource.LegacyProcess);
            var legacyResult = await AcquireWithAsync(legacy, interactive: true);
            Equal(true, legacyResult.Stream is not null, "legacy orphan takeover acquires the lock");
            legacyResult.Stream!.Dispose();
            Equal(0, legacy.Signals, "no stop event for a legacy holder");
            Equal(1, legacy.Terminated.Count, "legacy orphan terminated once");
            Equal(4321, legacy.Terminated[0].Pid, "the classified holder is the one terminated");

            // 1.2.18 holder that ignores the stop event: 15 s wait, then verified terminate.
            var ignoring = new FakeLockEnvironment(lockPath) { CooperativeExit = false };
            ignoring.Probe = e => LiveFacts(e, prismAlive: false);
            DateTime ignoringStart = ignoring.Now;
            var ignoringResult = await AcquireWithAsync(ignoring, interactive: false);
            Equal(true, ignoringResult.Stream is not null, "unresponsive orphan taken over");
            ignoringResult.Stream!.Dispose();
            Equal(1, ignoring.Signals, "stop event tried first");
            Equal(1, ignoring.Terminated.Count, "then terminated");
            Equal(true, ignoring.Now - ignoringStart >= TimeSpan.FromSeconds(15), "waited the 15 s cooperative window first");

            // Re-validation refused (PID reused / image changed): nothing acquired while it is held, nothing else killed.
            var refused = new FakeLockEnvironment(lockPath) { TerminateSucceeds = false, ListenerPresent = false };
            refused.Probe = e => LiveFacts(e, parentAlive: false);
            int refusedPolls = 0;
            refused.OnDelay = e =>
            {
                if (e.Delays[^1] == TimeSpan.FromSeconds(1) && ++refusedPolls == 3)
                {
                    e.HolderHoldsLock = false; // the holder finishes by itself
                }
            };
            var refusedResult = await AcquireWithAsync(refused, interactive: true);
            Equal(true, refusedResult.Stream is not null, "lock taken only after the holder released it");
            refusedResult.Stream!.Dispose();
            Equal(0, refused.Terminated.Count, "a refused re-validation terminates nothing");
            Equal(true, refusedResult.Log.Any(line => line.Contains("Did not stop pid 4321", StringComparison.Ordinal)), "refusal logged");

            // The holder changes between classification and the stop signal (a newer owner took the lock): nothing is
            // signalled or killed, the new holder is classified afresh (live -> wait) and the waiter continues when it ends.
            var swapped = new FakeLockEnvironment(lockPath);
            int swappedProbes = 0;
            swapped.Probe = e => ++swappedProbes == 1
                ? LiveFacts(e, parentAlive: false)
                : LiveFacts(e) with { Holder = new ProcessIdentity(9999, T0, @"C:\i\minecraft\cobble-music-updater\CobbleMusicUpdater.exe") };
            int swappedPolls = 0;
            swapped.OnDelay = e =>
            {
                if (e.Delays[^1] == TimeSpan.FromSeconds(1) && ++swappedPolls == 2)
                {
                    e.HolderHoldsLock = false;
                }
            };
            var swappedResult = await AcquireWithAsync(swapped, interactive: true);
            Equal(true, swappedResult.Stream is not null, "waiter continues after the new holder ends");
            swappedResult.Stream!.Dispose();
            Equal(0, swapped.Signals + swapped.Terminated.Count, "a holder that changed before the takeover is never signalled or killed");
            Equal(true, swappedResult.Log.Any(line => line.Contains("changed before the takeover", StringComparison.Ordinal)), "holder change logged");

            // ORPHAN_APPLYING with a fresh heartbeat and progress: waits the full 5 minutes, then takes over.
            var applying = new FakeLockEnvironment(lockPath) { Journal = true, CooperativeExit = true };
            applying.Probe = e => LiveFacts(e, parentAlive: false, phase: "Applying");
            DateTime applyingStart = applying.Now;
            var applyingResult = await AcquireWithAsync(applying, interactive: true);
            Equal(true, applyingResult.Stream is not null, "applying orphan eventually taken over");
            applyingResult.Stream!.Dispose();
            Equal(true, applying.Now - applyingStart >= TimeSpan.FromMinutes(5), "no takeover before 5 minutes with a fresh heartbeat");
            Equal(true, applyingResult.Progress.Updates.Any(u => u.Detail is not null && u.Detail.StartsWith("Finishing an install from an earlier launch (4:5", StringComparison.Ordinal)), "applying countdown text");

            // LIVE_HEALTHY: waits with a countdown and never kills; continues when the holder releases.
            var healthy = new FakeLockEnvironment(lockPath);
            healthy.Probe = e => LiveFacts(e);
            int healthyPolls = 0;
            healthy.OnDelay = e =>
            {
                if (e.Delays[^1] == TimeSpan.FromSeconds(1) && ++healthyPolls == 5)
                {
                    e.HolderHoldsLock = false;
                }
            };
            var healthyResult = await AcquireWithAsync(healthy, interactive: true);
            Equal(true, healthyResult.Stream is not null, "live holder finishing releases the lock to the waiter");
            healthyResult.Stream!.Dispose();
            Equal(0, healthy.Terminated.Count + healthy.Signals, "live healthy holder never stopped");
            Equal(true, healthyResult.Progress.Updates.Any(u => u.Message == "Another update is already running for this pack"
                && u.Detail is not null && u.Detail.StartsWith("Downloading 45% — waiting for it to finish (9:5", StringComparison.Ordinal)), "waiting text with percent and countdown");

            // LIVE_HEALTHY without a UI: after 10 minutes it gives up (no journal -> launch), still never killed.
            var headless = new FakeLockEnvironment(lockPath);
            headless.Probe = e => LiveFacts(e);
            DateTime headlessStart = headless.Now;
            var headlessResult = await AcquireWithAsync(headless, interactive: false);
            Equal(true, headlessResult.Failure is UpdaterBusyException { JournalPresent: false, HolderClass: HolderClass.LiveHealthy }, "headless live holder: busy without journal");
            Equal(true, headless.Now - headlessStart >= TimeSpan.FromMinutes(10), "gave up only after 10 minutes");
            Equal(0, headless.Terminated.Count + headless.Signals, "headless live holder never stopped");

            // LIVE_HEALTHY with a UI after 10 minutes: offers the button; the player's choice takes over (verified).
            var offered = new FakeLockEnvironment(lockPath) { CooperativeExit = true };
            offered.Probe = e => LiveFacts(e);
            RunControl? offeredRun = null;
            CapturedProgress? offeredProgress = null;
            int offerPolls = 0;
            offered.OnDelay = e =>
            {
                if (offeredProgress?.Updates.LastOrDefault()?.Phase == UpdatePhase.WaitingCanStop && ++offerPolls == 3)
                {
                    offeredRun!.AcceptTakeover();
                }
            };
            using (RunControl run = RunControl.Begin())
            {
                offeredRun = run;
                run.InteractiveUi = true;
                offeredProgress = new CapturedProgress();
                using FileStream offeredStream = await new LockCoordinator(offered, run, offeredProgress, _ => { }).AcquireAsync();
                Equal(true, offered.Now - T0 >= TimeSpan.FromMinutes(10), "button offered only after 10 minutes");
                Equal(true, offeredProgress.Updates.Any(u => u.Phase == UpdatePhase.WaitingCanStop && u.Message == "Another update is still running for this pack"), "offer text");
                Equal(1, offered.Signals, "accepted offer signals the holder");
            }

            // STUCK (heartbeat older than 30 s) without a UI: gives up; journal present -> the caller blocks.
            var stuck = new FakeLockEnvironment(lockPath) { Journal = true };
            stuck.Probe = e => LiveFacts(e, heartbeat: T0.AddSeconds(-31));
            var stuckResult = await AcquireWithAsync(stuck, interactive: false);
            Equal(true, stuckResult.Failure is UpdaterBusyException { JournalPresent: true, HolderClass: HolderClass.Stuck }, "headless stuck holder with journal: blocked busy");
            Equal(0, stuck.Terminated.Count + stuck.Signals, "stuck holder never stopped automatically");

            // UNKNOWN_HOLDER: 30 s of retries, then busy; the journal decides launch vs block; never killed.
            foreach (bool journal in new[] { false, true })
            {
                var unknown = new FakeLockEnvironment(lockPath) { Journal = journal };
                DateTime unknownStart = unknown.Now;
                var unknownResult = await AcquireWithAsync(unknown, interactive: true);
                Equal(true, unknownResult.Failure is UpdaterBusyException busy && busy.JournalPresent == journal, $"unknown holder gives up (journal {journal})");
                Equal(true, unknown.Now - unknownStart >= TimeSpan.FromSeconds(30), "unknown holder waited 30 s");
                Equal(0, unknown.Terminated.Count + unknown.Signals, "unknown holder never stopped");
            }

            // X while waiting: the run token ends the wait.
            var cancelled = new FakeLockEnvironment(lockPath);
            cancelled.Probe = e => LiveFacts(e);
            RunControl? cancelRun = null;
            cancelled.OnDelay = e =>
            {
                if (e.Delays.Count == 15)
                {
                    cancelRun!.RequestCancel(CancelReason.UserClose);
                }
            };
            var cancelResult = await AcquireWithAsync(cancelled, interactive: true, run => cancelRun = run);
            Equal(true, cancelResult.Failure is OperationCanceledException, "X while waiting cancels the wait");
        }
        finally
        {
            Directory.Delete(lockDirectory, recursive: true);
        }
    }

    // ---- 4/5/10/12/13. Real processes: owner-record checks, legacy enumeration, verified termination ----------------

    private static async Task TestRealHolderProbeAndVerifiedTerminationAsync(string root)
    {
        UpdaterPaths paths = Paths(root, "real000000000001");
        Directory.CreateDirectory(paths.InstallationDirectory);
        Directory.CreateDirectory(paths.LocalDataDirectory);
        UpdaterPaths otherInstance = Paths(Path.Combine(root, "other"), "real000000000002");
        Directory.CreateDirectory(otherInstance.InstallationDirectory);
        // A stand-in "updater" of this instance: cmd.exe copied to <install>\CobbleMusicUpdater.exe.
        string fakeUpdater = Path.Combine(paths.InstallationDirectory, "CobbleMusicUpdater.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeUpdater);
        // "/q /k" with a redirected, never-written stdin: cmd blocks reading commands and starts no child process.
        using Process holderProcess = Process.Start(new ProcessStartInfo(fakeUpdater, "/q /k")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        }) ?? throw new InvalidOperationException("Could not start the stand-in holder.");
        try
        {
            Equal(true, ProcessTree.TryGetIdentity(holderProcess.Id, out ProcessIdentity holder), "stand-in holder identity");
            var probe = new SystemHolderProbe(paths);

            // (b) Legacy enumeration: no record; our test runner is its live parent (not Prism, not PowerShell) -> live.
            HolderSnapshot legacy = probe.Probe();
            Equal(HolderSource.LegacyProcess, legacy.Source, "legacy holder found by enumeration of this instance's install folder");
            Equal(true, legacy.Holder!.Matches(holder), "the enumerated holder is the stand-in");
            Equal(false, legacy.IsOrphan, "a legacy holder with a live parent is not an orphan");
            Equal(HolderClass.LiveHealthy, HolderClassifier.Classify(legacy with { ObservedSinceUtc = DateTime.UtcNow }, false, DateTime.UtcNow).Class, "legacy holder with live parent: LIVE_HEALTHY");
            Equal(HolderSource.Unknown, new SystemHolderProbe(otherInstance).Probe().Source, "another instance never sees this holder");

            // (a) Owner record naming the stand-in, launched by a Prism chain that no longer exists -> orphan.
            ProcessIdentity deadParent = new(holder.Pid + 100_001, holder.StartUtc.AddMinutes(-1), @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
            var record = new LockOwnerRecord
            {
                Pid = holder.Pid,
                ProcessStartUtc = holder.StartUtc.AddMilliseconds(400),
                ExePath = holder.ImagePath,
                InstanceIdentity = LocalStateStore.NormalizeInstanceIdentity(paths.InstanceDirectory),
                LaunchMode = LockOwnerRecord.LaunchModes.PrismPowerShell,
                ParentPid = deadParent.Pid,
                ParentStartUtc = deadParent.StartUtc,
                ParentImage = deadParent.ImagePath,
                PrismPid = deadParent.Pid + 1,
                PrismStartUtc = deadParent.StartUtc,
                PrismImage = @"C:\Prism\prismlauncher.exe",
                Phase = "Downloading",
                HeartbeatUtc = DateTime.UtcNow,
                LastProgressUtc = DateTime.UtcNow
            };
            string recordPath = OperationLockFile.OwnerRecordPath(paths);
            LockOwnerRecordStore.Write(recordPath, record);
            HolderSnapshot fromRecord = probe.Probe();
            Equal(HolderSource.OwnerRecord, fromRecord.Source, "record within the ±1 s start tolerance is believed");
            Equal(true, fromRecord.IsOrphan, "record holder whose Prism chain is gone is an orphan");
            Equal("Downloading", fromRecord.Phase, "record phase used");

            // 4. PID reuse: a start time 5 s off rejects the record (falls back to enumeration, never a record-based kill).
            record.ProcessStartUtc = holder.StartUtc.AddSeconds(-5);
            LockOwnerRecordStore.Write(recordPath, record);
            Equal(HolderSource.LegacyProcess, probe.Probe().Source, "PID-reuse record rejected");
            // Another instance's identity in the record is rejected too.
            record.ProcessStartUtc = holder.StartUtc;
            record.InstanceIdentity = "C:\\SOMEWHERE\\ELSE";
            LockOwnerRecordStore.Write(recordPath, record);
            Equal(HolderSource.LegacyProcess, probe.Probe().Source, "record for another instance rejected");
            File.Delete(recordPath);

            // 5/13. Verified termination: another instance's folder, or a swapped PID/start time, never kills.
            Equal(false, SystemLockEnvironment.TryTerminateHolder(holder, otherInstance.InstallationDirectory), "exe outside this instance's folder is never killed");
            Equal(false, SystemLockEnvironment.TryTerminateHolder(holder with { StartUtc = holder.StartUtc.AddSeconds(-5) }, paths.InstallationDirectory), "swapped PID (start time mismatch) is refused");
            Equal(true, ProcessTree.IsAlive(holder), "the holder survives both refusals");

            // 12. Full takeover against the real OS: the stand-in holds update.lock; the real environment kills it and reacquires.
            var environment = new SystemLockEnvironment(paths);
            Equal(true, environment.TryTerminate(holder), "exact identity inside this instance's folder is terminated");
            Equal(true, await environment.WaitForExitAsync(holder, TimeSpan.FromSeconds(10), CancellationToken.None), "terminated holder exits");
            holderProcess.WaitForExit();
            Equal((int)LockTimings.TakeoverTerminationExitCode, holderProcess.ExitCode, "takeover exit code");
        }
        finally
        {
            if (!holderProcess.HasExited)
            {
                holderProcess.Kill(entireProcessTree: true);
            }
        }
    }

    // ---- Launch policy, outcomes and the gate seam ----------------------------------------------------------------

    private sealed class FakeGate(bool result) : IPreLaunchGate
    {
        public readonly List<uint> Calls = [];

        public bool TryFailPreLaunch(uint exitCode, Action<string> log)
        {
            Calls.Add(exitCode);
            return result;
        }
    }

    private static T WithGate<T>(FakeGate gate, Func<T> action)
    {
        IPreLaunchGate previous = LaunchGate.Current;
        LaunchGate.Current = gate;
        try
        {
            return action();
        }
        finally
        {
            LaunchGate.Current = previous;
        }
    }

    private static void TestLaunchPolicyAndOutcomes(string root)
    {
        Equal(false, LaunchPolicy.ShouldFailPreLaunch(RunEnd.Success, false), "success launches");
        Equal(false, LaunchPolicy.ShouldFailPreLaunch(RunEnd.OfflineFallback, false), "offline fallback launches");
        Equal(false, LaunchPolicy.ShouldFailPreLaunch(RunEnd.NetworkFallback, false), "network fallback launches");
        Equal(false, LaunchPolicy.ShouldFailPreLaunch(RunEnd.UserCancelled, false), "X after a clean rollback launches the current pack");
        Equal(false, LaunchPolicy.ShouldFailPreLaunch(RunEnd.BusyWithoutJournal, false), "busy without journal launches");
        Equal(true, LaunchPolicy.ShouldFailPreLaunch(RunEnd.BusyWithoutJournal, true), "busy with journal blocks");
        Equal(true, LaunchPolicy.ShouldFailPreLaunch(RunEnd.UserCancelled, true), "a journal that survived X blocks");
        Equal(true, LaunchPolicy.ShouldFailPreLaunch(RunEnd.PeerStopped, false), "a peer stop blocks (another updater now owns the pack)");
        Equal(true, LaunchPolicy.ShouldFailPreLaunch(RunEnd.Blocked, false), "blocked blocks");

        var prelaunch = new CommandLine("i", "m", PrismPrelaunch: true, CheckOnly: false, NoUi: true);
        var manual = prelaunch with { PrismPrelaunch = false };
        var gate = new FakeGate(result: true);
        var progress = new CapturedProgress();
        Equal(1, WithGate(gate, () => RunOutcomes.Blocked(prelaunch, progress, _ => { }, "status")), "blocked exit code");
        Equal(1, gate.Calls.Count, "blocked run fails the pre-launch once");
        Equal(LaunchGate.BlockedExitCode, gate.Calls[0], "with exit code 3");
        Equal(RunOutcomes.StoppedDetail, progress.Updates[^1].Detail, "card says the launch was stopped only when it was");
        var notStopped = new FakeGate(result: false);
        WithGate(notStopped, () => RunOutcomes.Blocked(prelaunch, progress, _ => { }, "status"));
        Equal(RunOutcomes.NotStoppedDetail, progress.Updates[^1].Detail, "card never claims a stop that did not happen");
        var manualGate = new FakeGate(result: true);
        WithGate(manualGate, () => RunOutcomes.Blocked(manual, progress, _ => { }, "status"));
        Equal(0, manualGate.Calls.Count, "a manual (non pre-launch) run never touches processes");

        var busyGate = new FakeGate(result: true);
        var busyProgress = new CapturedProgress();
        int busyExit = WithGate(busyGate, () => RunOutcomes.Busy(prelaunch, new UpdaterBusyException("busy", false, HolderClass.UnknownHolder), busyProgress, _ => { }));
        Equal(0, busyExit, "busy without journal: exit 0, Minecraft starts with the current pack");
        Equal(0, busyGate.Calls.Count, "busy without journal: no termination");
        Equal(UpdatePhase.Fallback, busyProgress.Updates[^1].Phase, "busy without journal reports a fallback");
        Equal("Couldn’t start the update check", busyProgress.Updates[^1].Message, "busy status text");
        int busyJournalExit = WithGate(busyGate, () => RunOutcomes.Busy(prelaunch, new UpdaterBusyException("busy", true, HolderClass.UnknownHolder), busyProgress, _ => { }));
        Equal(1, busyJournalExit, "busy with journal: exit 1");
        Equal(1, busyGate.Calls.Count, "unknown holder with a journal fails the pre-launch");
        Equal(UpdatePhase.Blocked, busyProgress.Updates[^1].Phase, "busy with journal reports Blocked");

        UpdaterPaths paths = Paths(root, "outcome000000001");
        Directory.CreateDirectory(paths.LocalDataDirectory);
        foreach ((CancelReason reason, bool journal, bool expectGate) in new[]
        {
            (CancelReason.UserClose, false, false),
            (CancelReason.UserClose, true, true),
            (CancelReason.PeerStop, false, true)
        })
        {
            string journalPath = TransactionStore.JournalPath(paths);
            if (journal)
            {
                File.WriteAllText(journalPath, "{}");
            }
            else if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
            using RunControl run = RunControl.Begin();
            run.Paths = paths;
            run.RequestCancel(reason);
            var cancelGate = new FakeGate(result: true);
            int exitCode = WithGate(cancelGate, () => RunOutcomes.Cancelled(prelaunch, run, new CapturedProgress(), _ => { }));
            Equal(1, exitCode, $"cancelled run exit code ({reason}, journal {journal})");
            Equal(expectGate ? 1 : 0, cancelGate.Calls.Count, $"cancelled run gate ({reason}, journal {journal})");
        }
    }

    /// <summary>A run that ends Blocked (unreadable journal -> recovery needs attention) calls the launch-fail seam.</summary>
    private static async Task TestBlockedRunInvokesLaunchGateAsync(string root)
    {
        string instance = Path.Combine(root, "instance");
        string minecraft = Path.Combine(instance, "minecraft");
        Directory.CreateDirectory(minecraft);
        UpdaterPaths paths = LocalStateStore.ResolvePaths(instance, minecraft);
        try
        {
            Directory.CreateDirectory(paths.LocalDataDirectory);
            await File.WriteAllTextAsync(TransactionStore.JournalPath(paths), "{ this journal is unreadable");
            foreach (bool prismPrelaunch in new[] { true, false })
            {
                var gate = new FakeGate(result: true);
                var progress = new CapturedProgress();
                IPreLaunchGate previous = LaunchGate.Current;
                LaunchGate.Current = gate;
                int exitCode;
                try
                {
                    exitCode = await CobbleMusicUpdater.Program.RunUpdaterAsync(
                        new CommandLine(instance, minecraft, prismPrelaunch, CheckOnly: false, NoUi: true), progress);
                }
                finally
                {
                    LaunchGate.Current = previous;
                }
                Equal(1, exitCode, $"recovery failure exit code (PrismPrelaunch={prismPrelaunch})");
                Equal(UpdatePhase.Blocked, progress.Updates[^1].Phase, "recovery failure reports Blocked");
                Equal(prismPrelaunch ? 1 : 0, gate.Calls.Count, $"launch gate invoked only in pre-launch mode (PrismPrelaunch={prismPrelaunch})");
                Equal(false, File.Exists(OperationLockFile.OwnerRecordPath(paths)), "owner record removed when the run ends");
            }
        }
        finally
        {
            if (Directory.Exists(paths.LocalDataDirectory))
            {
                Directory.Delete(paths.LocalDataDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// End to end through RunUpdaterAsync with the real clock: something that is not an updater holds update.lock and no
    /// journal exists, so after 30 s the run launches the current pack (exit 0, Fallback) and never touches a process.
    /// </summary>
    private static async Task TestUnknownHolderWithoutJournalLaunchesAsync(string root)
    {
        string instance = Path.Combine(root, "instance");
        string minecraft = Path.Combine(instance, "minecraft");
        Directory.CreateDirectory(minecraft);
        UpdaterPaths paths = LocalStateStore.ResolvePaths(instance, minecraft);
        try
        {
            using FileStream foreignHandle = OperationLockFile.Open(paths);
            var gate = new FakeGate(result: true);
            var progress = new CapturedProgress();
            IPreLaunchGate previous = LaunchGate.Current;
            LaunchGate.Current = gate;
            var clock = Stopwatch.StartNew();
            int exitCode;
            try
            {
                exitCode = await CobbleMusicUpdater.Program.RunUpdaterAsync(
                    new CommandLine(instance, minecraft, PrismPrelaunch: true, CheckOnly: false, NoUi: true), progress);
            }
            finally
            {
                LaunchGate.Current = previous;
            }
            Equal(0, exitCode, "unknown holder without a journal: exit 0");
            Equal(0, gate.Calls.Count, "unknown holder without a journal: launch not failed");
            Equal(UpdatePhase.Fallback, progress.Updates[^1].Phase, "reports a fallback");
            Equal("Something else is using the updater’s files. Minecraft will start with your current pack.", progress.Updates[^1].Detail, "accurate unknown-holder text");
            Equal(true, progress.Updates.Any(u => u.Phase == UpdatePhase.Waiting && u.Detail is not null && u.Detail.StartsWith("Waiting for the updater's files to be free (0:", StringComparison.Ordinal)), "waiting countdown shown");
            Equal(true, clock.Elapsed >= TimeSpan.FromSeconds(30), "waited the 30 s unknown-holder window");
        }
        finally
        {
            if (Directory.Exists(paths.LocalDataDirectory))
            {
                Directory.Delete(paths.LocalDataDirectory, recursive: true);
            }
        }
    }

    // ---- 17. X during Applying: the run token rolls the transaction back -------------------------------------------

    private static async Task TestCancelDuringApplyRollsBackAsync(string root)
    {
        UpdaterPaths paths = Paths(root, "cancel0000000001");
        Directory.CreateDirectory(paths.MinecraftDirectory);
        Directory.CreateDirectory(paths.InstallationDirectory);
        var oldContent = new Dictionary<string, string> { ["mods/a.jar"] = "old-a", ["mods/b.jar"] = "old-b", ["mods/c.jar"] = "old-c" };
        var newContent = new Dictionary<string, string> { ["mods/a.jar"] = "new-a!", ["mods/b.jar"] = "new-b!", ["mods/c.jar"] = "new-c!" };
        string extract = Path.Combine(root, "extract");
        foreach ((string relative, string content) in oldContent)
        {
            await WriteRelativeAsync(paths.MinecraftDirectory, relative, content);
        }
        foreach ((string relative, string content) in newContent)
        {
            await WriteRelativeAsync(extract, relative, content);
        }
        var previous = new InstalledState
        {
            Version = "1.0.4",
            ManifestSha256 = Hash("old-manifest"),
            ManagedFiles = oldContent.Select(pair => new ManagedFileState { Path = pair.Key, Size = pair.Value.Length, Sha256 = Hash(pair.Value) }).ToList()
        };
        await LocalStateStore.SaveStateAsync(paths, previous, CancellationToken.None);
        var manifest = new UpdateManifest
        {
            SchemaVersion = 1,
            Version = "1.0.5",
            Files = newContent.Select(pair => new ManifestFile { Path = pair.Key, Size = pair.Value.Length, Sha256 = Hash(pair.Value) }).ToList()
        };

        using RunControl run = RunControl.Begin();
        run.Paths = paths;
        run.OwnsLock = true;
        bool sawPendingTransaction = false;
        var cancelOnFirstFile = new CallbackProgress(update =>
        {
            if (update.Phase == UpdatePhase.Applying && update.CurrentItem >= 1 && !run.Token.IsCancellationRequested)
            {
                // What the card's X sees at this moment: a journal on disk -> FinishSafely (rollback, then exit).
                sawPendingTransaction = run.OwnTransactionMayBePending;
                Equal(ForceCloseAction.FinishSafely, ForceClosePolicy.Decide(false, run.OwnTransactionMayBePending, false), "X during Applying finishes safely");
                run.RequestCancel(CancelReason.UserClose);
            }
        });
        var engine = new UpdateEngine(paths, new UpdaterConfiguration { AllowedRoots = ["mods"] }, _ => { }, cancelOnFirstFile);
        Exception? failure = null;
        try
        {
            await engine.ApplyTransactionAsync(manifest, Hash("new-manifest"), extract, previous, signedBase: null, run.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        Equal(true, sawPendingTransaction, "the journal existed while files were being applied");
        Equal(true, failure is OperationCanceledException, "cancellation surfaces as OperationCanceledException after rollback");
        foreach ((string relative, string content) in oldContent)
        {
            Equal(content, await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, relative)), $"{relative} rolled back");
        }
        Equal("1.0.4", LocalStateStore.LoadState(paths).Version, "state still the previous version");
        Equal(false, run.TransactionMayBePending, "journal removed by the rollback");

        var gate = new FakeGate(result: true);
        var progress = new CapturedProgress();
        int exitCode = WithGate(gate, () => RunOutcomes.Cancelled(new CommandLine("i", "m", true, false, true), run, progress, _ => { }));
        Equal(1, exitCode, "X exit code stays 1 as in 1.2.17");
        Equal(0, gate.Calls.Count, "a clean rollback does not fail the launch");
        Equal(UpdatePhase.Fallback, progress.Updates[^1].Phase, "the current pack was kept");
    }

    private static void TestForceClosePolicyTable()
    {
        Equal(ForceCloseAction.ExitNow, ForceClosePolicy.Decide(runFinished: true, transactionMayBePending: true, cancelAlreadyRequested: false), "finished run closes");
        Equal(ForceCloseAction.CancelThenExit, ForceClosePolicy.Decide(false, false, false), "X while checking/downloading cancels then exits");
        Equal(ForceCloseAction.FinishSafely, ForceClosePolicy.Decide(false, true, false), "X during Applying/Recovering rolls back first");
        Equal(ForceCloseAction.ConfirmHardExit, ForceClosePolicy.Decide(false, true, true), "second X during a transaction asks first");
        Equal(ForceCloseAction.ExitNow, ForceClosePolicy.Decide(false, false, true), "second X outside a transaction exits");
        Equal(TimeSpan.FromSeconds(5), ForceClosePolicy.CooperativeExitWait, "5 s cooperative wait");
        Equal("Close (20)", UpdateStatusForm.FailureCloseText(UpdateStatusForm.FailureAutoCloseSeconds), "failure card countdown text");
    }

    /// <summary>
    /// The real status card: X while a journal exists shows "Finishing safely", cancels the run token, and the card closes
    /// only after the run has finished its rollback (never Environment.Exit).
    /// </summary>
    private static void TestStatusCardXDuringApplyWaitsForRollback()
    {
        string root = Path.Combine(Path.GetTempPath(), "cobble-lockv2-card-" + Guid.NewGuid().ToString("N"));
        UpdaterPaths paths = Paths(root, "card000000000001");
        Directory.CreateDirectory(paths.LocalDataDirectory);
        string journalPath = TransactionStore.JournalPath(paths);
        var events = new List<string>();
        string? statusWhileRollingBack = null;
        int formExitCode = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                UpdateStatusForm? form = null;
                form = new UpdateStatusForm(
                    new CommandLine("test-instance", "test-minecraft", PrismPrelaunch: true, CheckOnly: false, NoUi: false),
                    async (_, _) =>
                    {
                        using RunControl run = RunControl.Begin();
                        run.Paths = paths;
                        run.OwnsLock = true;
                        File.WriteAllText(journalPath, "{}"); // "Applying": a transaction is pending
                        form!.BeginInvoke(() => ((Button)form.Controls.Find("forceCloseButton", true)[0]).PerformClick());
                        try
                        {
                            await Task.Delay(Timeout.Infinite, run.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            events.Add("cancelled");
                        }
                        statusWhileRollingBack = (string)form.Invoke(() => form.Controls.Find("statusLabel", true)[0].Text);
                        await Task.Delay(300); // the rollback takes a moment; the card must still be open
                        events.Add(form.IsDisposed ? "closed-early" : "open-during-rollback");
                        File.Delete(journalPath);
                        events.Add("rolled-back");
                        return 1;
                    });
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new System.Drawing.Point(-10_000, -10_000);
                Application.Run(form);
                events.Add("form-closed");
                formExitCode = form.ExitCode;
                form.Dispose();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            if (!thread.Join(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The status card X test did not finish within 30 seconds.");
            }
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            Equal("cancelled|open-during-rollback|rolled-back|form-closed", string.Join('|', events), "X during Applying: cancel, stay open through rollback, then close");
            Equal("Finishing safely — undoing the unfinished install", statusWhileRollingBack, "card says it is finishing safely");
            Equal(1, formExitCode, "exit code 1 after a cancelled run");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private sealed class CapturedProgress : IProgress<UpdateProgress>
    {
        private readonly object _gate = new();
        private readonly List<UpdateProgress> _updates = [];

        public List<UpdateProgress> Updates
        {
            get
            {
                lock (_gate)
                {
                    return [.. _updates];
                }
            }
        }

        public void Report(UpdateProgress value)
        {
            lock (_gate)
            {
                _updates.Add(value);
            }
        }
    }

    private sealed class CallbackProgress(Action<UpdateProgress> callback) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => callback(value);
    }

    private static UpdaterPaths Paths(string root, string localLeaf)
    {
        string instance = Path.Combine(root, "instance");
        string minecraft = Path.Combine(instance, "minecraft");
        string install = Path.Combine(minecraft, "cobble-music-updater");
        return new UpdaterPaths(instance, minecraft, install, Path.Combine(install, "updater.json"), Path.Combine(install, "state.json"), Path.Combine(root, localLeaf));
    }

    private static async Task WriteRelativeAsync(string root, string relative, string content)
    {
        string path = PathSafety.CombineUnder(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
