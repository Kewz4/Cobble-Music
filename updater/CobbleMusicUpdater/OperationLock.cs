using System.Text.Json;

namespace CobbleMusicUpdater;

/// <summary>Lock v2 constants (lock-lifecycle FINDINGS §7.6).</summary>
internal static class LockTimings
{
    public const int OpenAttempts = 10;
    public static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(250);
    public const int ReacquireAttempts = 10;
    public static readonly TimeSpan ReacquireRetryDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan HeartbeatStale = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ProgressStaleNetwork = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan ProgressStaleApplying = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan HealthyHolderWait = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ApplyingHolderWait = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan UnknownHolderWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CooperativeStopWait = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TakeoverNotice = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    /// <summary>The recorded start time is compared with the live process's creation time within this tolerance.</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);
    /// <summary>Exit code a holder is terminated with during a takeover (distinct from 1 and 3 in logs).</summary>
    public const uint TakeoverTerminationExitCode = 4;
}

/// <summary>Opening <c>update.lock</c>: the same file and FileShare.None as 1.2.17, so 1.2.17 and 1.2.18 still exclude each other.</summary>
internal static class OperationLockFile
{
    /// <summary>ERROR_SHARING_VIOLATION: another process has the file open without sharing.</summary>
    public const int ErrorSharingViolation = 32;

    public static string LockPath(UpdaterPaths paths) => Path.Combine(paths.LocalDataDirectory, "update.lock");

    public static string OwnerRecordPath(UpdaterPaths paths) => LockPath(paths) + ".owner.json";

    /// <summary>
    /// Only a sharing violation means "someone holds the lock". 1.2.17 treated every IOException as busy, which turned
    /// access-denied or path errors into a false "another update is running" on every launch (lock-lifecycle FINDINGS §1 item 3, §4 case 10).
    /// </summary>
    public static bool IsSharingViolation(IOException exception) => (exception.HResult & 0xFFFF) == ErrorSharingViolation;

    public static FileStream Open(UpdaterPaths paths)
    {
        Directory.CreateDirectory(paths.LocalDataDirectory);
        return new FileStream(LockPath(paths), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// Tries <paramref name="attempts"/> opens <paramref name="retryDelay"/> apart and returns null when every one hit a
    /// sharing violation. A transient handle (antivirus, indexer, backup; case 9) is absorbed by the retries. Any other
    /// IOException or UnauthorizedAccessException propagates unchanged: it is a real error, not a busy lock.
    /// </summary>
    public static async Task<FileStream?> TryOpenAsync(
        Func<FileStream> open,
        int attempts,
        TimeSpan retryDelay,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return open();
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (attempt >= attempts)
                {
                    return null;
                }
            }
            await delay(retryDelay, cancellationToken);
        }
    }
}

/// <summary>
/// <c>update.lock.owner.json</c>: who holds the lock, written by the holder while it holds it. Every field is re-checked
/// against the operating system by a waiter, so a stale or forged record can never cause a kill on its own.
/// </summary>
internal sealed class LockOwnerRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string SessionId { get; set; } = "";
    public int Pid { get; set; }
    public DateTime ProcessStartUtc { get; set; }
    public string ExePath { get; set; } = "";
    public string UpdaterVersion { get; set; } = "";
    public string InstanceIdentity { get; set; } = "";
    public int? ParentPid { get; set; }
    public DateTime? ParentStartUtc { get; set; }
    public string? ParentImage { get; set; }
    public int? PrismPid { get; set; }
    public DateTime? PrismStartUtc { get; set; }
    public string? PrismImage { get; set; }
    /// <summary>"prism-powershell" (friend command), "prism-direct" (legacy installer command) or "manual".</summary>
    public string LaunchMode { get; set; } = LaunchModes.Manual;
    public string Phase { get; set; } = "Starting";
    public DateTime HeartbeatUtc { get; set; }
    public DateTime LastProgressUtc { get; set; }
    public long CompletedBytes { get; set; }
    public long TotalBytes { get; set; }
    public int CurrentItem { get; set; }
    public int TotalItems { get; set; }

    public static class LaunchModes
    {
        public const string PrismPowerShell = "prism-powershell";
        public const string PrismDirect = "prism-direct";
        public const string Manual = "manual";
    }

    public bool IsPrismLaunch =>
        LaunchMode is LaunchModes.PrismPowerShell or LaunchModes.PrismDirect;

    public ProcessIdentity? ParentIdentity =>
        ParentPid is int pid && ParentStartUtc is DateTime start && !string.IsNullOrEmpty(ParentImage)
            ? new ProcessIdentity(pid, DateTime.SpecifyKind(start, DateTimeKind.Utc), ParentImage)
            : null;

    public ProcessIdentity? PrismIdentity =>
        PrismPid is int pid && PrismStartUtc is DateTime start && !string.IsNullOrEmpty(PrismImage)
            ? new ProcessIdentity(pid, DateTime.SpecifyKind(start, DateTimeKind.Utc), PrismImage)
            : null;
}

internal static class LockOwnerRecordStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Atomic replace (temp file + move), so a reader sees the old or the new record, never a torn one.</summary>
    public static void Write(string path, LockOwnerRecord record)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        string temporary = path + ".new";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // No flush-to-disk: atomicity comes from the move, and a record torn by a power cut reads as absent (harmless).
            output.Write(content);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Reads the record with full sharing (so the holder's next atomic replace is never blocked by a reader) and returns
    /// null when it is missing, unreadable, malformed or of another schema. Absence is never an error: 1.2.17 writes none.
    /// </summary>
    public static LockOwnerRecord? TryRead(string path)
    {
        try
        {
            byte[] content;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (input.Length is <= 0 or > 64 * 1024)
                {
                    return null;
                }
                content = new byte[input.Length];
                input.ReadExactly(content);
            }
            LockOwnerRecord? record = JsonSerializer.Deserialize<LockOwnerRecord>(content, JsonOptions);
            if (record is null || record.SchemaVersion != LockOwnerRecord.CurrentSchemaVersion || record.Pid <= 0)
            {
                return null;
            }
            record.ProcessStartUtc = DateTime.SpecifyKind(record.ProcessStartUtc, DateTimeKind.Utc);
            record.HeartbeatUtc = DateTime.SpecifyKind(record.HeartbeatUtc, DateTimeKind.Utc);
            record.LastProgressUtc = DateTime.SpecifyKind(record.LastProgressUtc, DateTimeKind.Utc);
            return record;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover record is harmless: every field is re-checked against the OS before it is believed.
        }
    }
}

/// <summary>
/// The held lock plus its owner record: writes the record on creation, refreshes the heartbeat every 2 s from a timer
/// thread, records phase and progress from the run's progress callbacks, listens for the stop event, and on dispose
/// deletes the record BEFORE releasing the lock (so a live record always belongs to the live holder).
/// </summary>
internal sealed class LockOwnership : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _lock;
    private readonly string _recordPath;
    private readonly LockOwnerRecord _record;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _log;
    private System.Threading.Timer? _heartbeat;
    private IDisposable? _stopListener;
    private bool _writeFailureLogged;
    private bool _disposed;

    public LockOwnership(FileStream heldLock, string recordPath, LockOwnerRecord record, Func<DateTime> utcNow, Action<string> log)
    {
        _lock = heldLock;
        _recordPath = recordPath;
        _record = record;
        _utcNow = utcNow;
        _log = log;
        DateTime now = _utcNow();
        _record.HeartbeatUtc = now;
        _record.LastProgressUtc = now;
        WriteRecord();
    }

    public LockOwnerRecord Record
    {
        get
        {
            lock (_gate)
            {
                return _record;
            }
        }
    }

    /// <summary>Starts the 2 s heartbeat timer. Separate from the constructor so tests can drive heartbeats by hand.</summary>
    public void StartHeartbeat(TimeSpan interval)
    {
        lock (_gate)
        {
            if (_disposed || _heartbeat is not null)
            {
                return;
            }
            _heartbeat = new System.Threading.Timer(_ => Heartbeat(), null, interval, interval);
        }
    }

    /// <summary>Registers the stop-event listener; it is disposed with the ownership.</summary>
    public void AttachStopListener(IDisposable listener)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                listener.Dispose();
                return;
            }
            _stopListener = listener;
        }
    }

    public void Heartbeat()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _record.HeartbeatUtc = _utcNow();
            WriteRecord();
        }
    }

    /// <summary>
    /// Every progress callback counts as progress (a stalled download reports none). The record is written on the next
    /// heartbeat, not per callback, so a fast download does not rewrite the file hundreds of times a second.
    /// </summary>
    public void Observe(UpdateProgress update)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _record.Phase = update.Phase.ToString();
            _record.LastProgressUtc = _utcNow();
            _record.CompletedBytes = update.CompletedBytes;
            _record.TotalBytes = update.TotalBytes;
            _record.CurrentItem = update.CurrentItem;
            _record.TotalItems = update.TotalItems;
        }
    }

    /// <summary>Wraps the run's progress sink so the owner record follows it.</summary>
    public IProgress<UpdateProgress> Track(IProgress<UpdateProgress>? inner) => new TrackingProgress(this, inner);

    private void WriteRecord()
    {
        try
        {
            LockOwnerRecordStore.Write(_recordPath, _record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A missed heartbeat write is retried 2 s later; only a 30 s silence makes a waiter call this holder stuck.
            if (!_writeFailureLogged)
            {
                _writeFailureLogged = true;
                _log($"Could not refresh the update lock owner record ({exception.GetType().Name}); will retry.");
            }
        }
    }

    public void Dispose()
    {
        System.Threading.Timer? heartbeat;
        IDisposable? stopListener;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            heartbeat = _heartbeat;
            stopListener = _stopListener;
            _heartbeat = null;
            _stopListener = null;
        }
        if (heartbeat is not null)
        {
            using var stopped = new ManualResetEvent(false);
            heartbeat.Dispose(stopped);
            stopped.WaitOne(TimeSpan.FromSeconds(5));
        }
        stopListener?.Dispose();
        LockOwnerRecordStore.TryDelete(_recordPath);
        LockOwnerRecordStore.TryDelete(_recordPath + ".new");
        _lock.Dispose();
    }

    private sealed class TrackingProgress(LockOwnership owner, IProgress<UpdateProgress>? inner) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value)
        {
            owner.Observe(value);
            inner?.Report(value);
        }
    }
}

/// <summary>
/// The named stop event <c>Local\CobbleMusicUpdater.Stop.&lt;identityHash&gt;</c>. The identity hash is the lock
/// directory's name (the 16-hex instance identity hash, or its mapped legacy hash), so it has exactly the lock's scope.
/// </summary>
internal static class UpdaterStopSignal
{
    public static string EventName(UpdaterPaths paths) =>
        @"Local\CobbleMusicUpdater.Stop." + Path.GetFileName(Path.TrimEndingDirectorySeparator(paths.LocalDataDirectory)).ToLowerInvariant();

    /// <summary>
    /// Holder side: invokes <paramref name="onStop"/> once when a taker signals. The event is auto-reset and is reset
    /// before listening, so a signal left over from an earlier takeover (whose target died before consuming it) can never
    /// cancel this new, healthy owner.
    /// </summary>
    public static IDisposable Listen(UpdaterPaths paths, Action onStop)
    {
        var stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(paths));
        stopEvent.Reset();
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            stopEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    onStop();
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
        return new Listener(stopEvent, registration);
    }

    /// <summary>Taker side: sets the event only if a holder created it (a 1.2.17 holder never does). True when set.</summary>
    public static bool Signal(UpdaterPaths paths)
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(paths), out EventWaitHandle? stopEvent))
        {
            return false;
        }
        using (stopEvent)
        {
            return stopEvent.Set();
        }
    }

    private sealed class Listener(EventWaitHandle stopEvent, RegisteredWaitHandle registration) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            registration.Unregister(null);
            stopEvent.Dispose();
        }
    }
}
