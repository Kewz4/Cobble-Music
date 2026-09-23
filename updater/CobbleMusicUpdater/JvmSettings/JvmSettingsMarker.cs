using System.Text.Json;
using System.Text.Json.Serialization;

namespace CobbleMusicUpdater;

/// <summary>Marker states (section 6). "applied" is fallback F: the edit was made but Prism was not relaunched.</summary>
internal static class JvmMarkerState
{
    public const string None = "none";
    public const string Restarting = "restarting";
    public const string Relaunched = "relaunched";
    public const string Applied = "applied";
    public const string Verified = "verified";
    public const string Failed = "failed";
    public const string AbortedHelper = "aborted-helper";
    public const string AbortedPrismReopened = "aborted-prism-reopened";
    public const string Aborted = "aborted";
    public const string WaitingForPrismExit = "waiting-for-prism-exit";

    /// <summary>States that mean the helper edited instance.cfg, so the next launch must find the settings present.</summary>
    public static bool EditWasMade(string state) => state is Relaunched or Applied;
}

internal sealed class JvmSettingsMarker
{
    public int PolicyVersion { get; set; }
    public string State { get; set; } = JvmMarkerState.None;
    public int Attempts { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public string? Detail { get; set; }
}

/// <summary>The loop guard's verdict for one pre-launch evaluation.</summary>
internal enum JvmLoopGuardVerdict
{
    MayRestart,
    AlreadyRelaunchedStillMissing,
    TooManyAttempts,
    RecentAttempt,
    HelperStillWaiting
}

/// <summary>
/// The marker file (%LOCALAPPDATA%/CobbleMusicUpdater/&lt;instance-hash&gt;/jvm-settings.json) and the section 6 A4 loop
/// guard. The marker only ever limits what the updater does (it can stop a restart, never cause one), so a missing,
/// unreadable or tampered marker is treated as a fresh one, which is still bounded by the other guards.
/// </summary>
internal static class JvmSettingsMarkerStore
{
    public const string FileName = "jvm-settings.json";
    public const int MaximumAttempts = 2;
    public static readonly TimeSpan MinimumAttemptInterval = TimeSpan.FromMinutes(10);

    /// <summary>Fallback F waits at most 12 h for Prism to exit; the marker is trusted as "still waiting" for that long.</summary>
    public static readonly TimeSpan HelperWaitLimit = TimeSpan.FromHours(12) + TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string PathFor(string localDataDirectory) => Path.Combine(localDataDirectory, FileName);

    /// <summary>Loads the marker for the current policy. A marker from another policy version starts over (section 11 test 8).</summary>
    public static JvmSettingsMarker Load(string localDataDirectory, int policyVersion)
    {
        JvmSettingsMarker? marker = null;
        try
        {
            string path = PathFor(localDataDirectory);
            if (File.Exists(path))
            {
                marker = JsonSerializer.Deserialize<JvmSettingsMarker>(File.ReadAllBytes(path), JsonOptions);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            marker = null;
        }
        if (marker is null || marker.PolicyVersion != policyVersion || marker.Attempts < 0)
        {
            return new JvmSettingsMarker { PolicyVersion = policyVersion };
        }
        return marker;
    }

    /// <summary>Writes the marker atomically (temp file in the same folder, flushed, then renamed over the old one).</summary>
    public static void Save(string localDataDirectory, JvmSettingsMarker marker, DateTime utcNow)
    {
        Directory.CreateDirectory(localDataDirectory);
        marker.UpdatedUtc = utcNow;
        string path = PathFor(localDataDirectory);
        string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions));
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Section 6 A4: no restart after a restart that did not stick, after 2 attempts, or within 10 minutes of one.</summary>
    public static JvmLoopGuardVerdict Evaluate(JvmSettingsMarker marker, DateTime utcNow)
    {
        if (JvmMarkerState.EditWasMade(marker.State))
        {
            return JvmLoopGuardVerdict.AlreadyRelaunchedStillMissing;
        }
        // Fallback F: a helper is still waiting for Prism to exit (up to 12 h) and will edit then. A second helper would only
        // lose the per-instance mutex, so no new restart is started while that wait can still be running.
        if (marker.State == JvmMarkerState.WaitingForPrismExit
            && marker.LastAttemptUtc is DateTime waitingSince
            && (utcNow - waitingSince).Duration() < HelperWaitLimit)
        {
            return JvmLoopGuardVerdict.HelperStillWaiting;
        }
        if (marker.Attempts >= MaximumAttempts)
        {
            return JvmLoopGuardVerdict.TooManyAttempts;
        }
        // Absolute distance, so a clock moved back a few minutes cannot open the window early; the attempt cap still bounds
        // any larger clock change.
        if (marker.LastAttemptUtc is DateTime last && (utcNow - last).Duration() < MinimumAttemptInterval)
        {
            return JvmLoopGuardVerdict.RecentAttempt;
        }
        return JvmLoopGuardVerdict.MayRestart;
    }
}
