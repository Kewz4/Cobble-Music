namespace CobbleMusicUpdater;

/// <summary>A heap tier chosen from physical RAM. <see cref="IsBelowMinimum"/> tiers never change memory settings.</summary>
internal sealed record JvmHeapTier(string Name, int MaxMemAllocMiB, int MinMemAllocMiB)
{
    public bool IsBelowMinimum => MaxMemAllocMiB == 0;
}

/// <summary>One JVM argument the policy adds when its key is absent from the player's effective JvmArgs.</summary>
internal sealed record JvmPolicyFlag(string Token, string Key, bool IsGcGroup, bool IsGcLog);

/// <summary>The desired state D for one PC: the heap floor and Xms from the tier, plus the flag list.</summary>
internal sealed record JvmDesiredState(int PolicyVersion, JvmHeapTier Tier, IReadOnlyList<JvmPolicyFlag> Flags)
{
    public int XmxFloorMiB => Tier.MaxMemAllocMiB;
    public int XmsMiB => Tier.MinMemAllocMiB;
}

/// <summary>
/// Policy version 1, compiled into the exe (jvm-args FINDINGS section 7). Nothing here is read from remote or local data:
/// changing the policy means shipping a new signed updater, and bumping <see cref="PolicyVersion"/> resets the loop guard.
/// </summary>
internal static class JvmPolicy
{
    public const int PolicyVersion = 1;
    private const ulong GiB = 1024UL * 1024 * 1024;

    /// <summary>The GC log output. Relative to the game's working directory (INST_MC_DIR), which must contain logs/.</summary>
    public const string GcLogToken = "-Xlog:gc*,stringdedup*=info:file=logs/gc.log:time,uptime,level,tags:filecount=5,filesize=20M";

    // The GC group is suppressed as a whole when the player already selects any collector (section 8): the G1 tuning flags
    // mean nothing on another collector, and a second selector is fatal at JVM start. UseStringDeduplication is kept in the
    // group on purpose: it is harmless on G1/ZGC/Shenandoah, but not every collector a player might pick (Epsilon) supports it,
    // so the conservative reading of "none of our GC group" includes it.
    public static readonly IReadOnlyList<JvmPolicyFlag> Flags =
    [
        new("-XX:+UseG1GC", "UseG1GC", IsGcGroup: true, IsGcLog: false),
        new("-XX:MaxGCPauseMillis=50", "MaxGCPauseMillis", IsGcGroup: true, IsGcLog: false),
        new("-XX:G1ReservePercent=15", "G1ReservePercent", IsGcGroup: true, IsGcLog: false),
        new("-XX:+UseStringDeduplication", "UseStringDeduplication", IsGcGroup: true, IsGcLog: false),
        new(GcLogToken, "Xlog:gc-file", IsGcGroup: false, IsGcLog: true)
    ];

    /// <summary>
    /// Collector selectors. The spec lists the six current ones; the removed/legacy selectors (CMS, ParallelOld) and the ZGC
    /// mode flag are included as well because treating them as "a GC is chosen" can only make the merge more conservative.
    /// </summary>
    public static readonly IReadOnlySet<string> GcSelectorKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "UseG1GC", "UseZGC", "UseShenandoahGC", "UseParallelGC", "UseSerialGC", "UseEpsilonGC",
        "UseConcMarkSweepGC", "UseParallelOldGC", "UseGenerationalZGC"
    };

    /// <summary>Player flags we never touch but warn about (section 8).</summary>
    public static readonly IReadOnlySet<string> WarnKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "AlwaysPreTouch", "DisableExplicitGC"
    };

    /// <summary>
    /// Heap tier by GlobalMemoryStatusEx.ullTotalPhys (the same source Prism uses). Boundaries are whole GiB and inclusive at
    /// the lower edge: 11 GiB - 1 byte is below minimum, 11 GiB exactly is the 16 GB class.
    /// </summary>
    public static JvmHeapTier TierFor(ulong totalPhysicalBytes)
    {
        if (totalPhysicalBytes < 11 * GiB)
        {
            return new JvmHeapTier("below pack minimum", 0, 0);
        }
        if (totalPhysicalBytes < 19 * GiB)
        {
            return new JvmHeapTier("16 GB class", 7168, 3584);
        }
        if (totalPhysicalBytes < 27 * GiB)
        {
            return new JvmHeapTier("24 GB class", 8192, 4096);
        }
        if (totalPhysicalBytes < 43 * GiB)
        {
            return new JvmHeapTier("32 GB class", 10000, 4096);
        }
        return new JvmHeapTier("48 GB and up", 12288, 4096);
    }

    public static JvmDesiredState DesiredFor(ulong totalPhysicalBytes) =>
        new(PolicyVersion, TierFor(totalPhysicalBytes), Flags);

    /// <summary>
    /// The key a JVM argument sets, for key-based presence: -XX:[+-]Name and -XX:Name=value give "Name". Any other token has no
    /// -XX key (null).
    /// </summary>
    public static string? XxKey(string token)
    {
        if (!token.StartsWith("-XX:", StringComparison.Ordinal) || token.Length <= 4)
        {
            return null;
        }
        string body = token[4..];
        if (body[0] == '+' || body[0] == '-')
        {
            body = body[1..];
        }
        int equals = body.IndexOf('=');
        string name = equals >= 0 ? body[..equals] : body;
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// True when the player's argument already logs GC to a file (or disables unified logging), so ours must not be added: a
    /// second -Xlog writing the same logs/gc.log would fight over the file. Covers -Xlog:disable, the legacy -Xloggc:file, and
    /// -Xlog:what:output where what names a gc selector (or "all") and the output is anything but stdout/stderr.
    /// </summary>
    public static bool IsGcFileLog(string token)
    {
        if (token.StartsWith("-Xloggc:", StringComparison.Ordinal))
        {
            return true;
        }
        if (!token.StartsWith("-Xlog:", StringComparison.Ordinal))
        {
            return false;
        }
        string body = token["-Xlog:".Length..];
        if (body == "disable")
        {
            return true;
        }
        string[] parts = body.Split(':');
        string what = parts[0];
        string output = parts.Length > 1 ? parts[1] : string.Empty;
        bool selectsGc = what.Length == 0
            || what.Split(',').Any(selector =>
            {
                string tags = selector.Split('=')[0].TrimEnd('*');
                return tags == "all" || tags.Split('+').Contains("gc", StringComparer.Ordinal);
            });
        bool toFile = output.Length > 0
            && output is not ("stdout" or "stderr" or "#0" or "#1");
        return selectsGc && toFile;
    }

    /// <summary>
    /// The heap an -Xmx token asks for, in MiB, or null for any other token. Sizes follow HotSpot: no suffix is bytes, k/m/g/t
    /// (either case) scale by 1024. A value that is not a whole number of MiB is rounded down.
    /// </summary>
    public static long? XmxMiB(string token)
    {
        if (!token.StartsWith("-Xmx", StringComparison.Ordinal) || token.Length <= 4)
        {
            return null;
        }
        string size = token[4..];
        long multiplier = 1;
        char suffix = char.ToLowerInvariant(size[^1]);
        if (suffix is 'k' or 'm' or 'g' or 't')
        {
            multiplier = suffix switch { 'k' => 1024L, 'm' => 1024L * 1024, 'g' => 1024L * 1024 * 1024, _ => 1024L * 1024 * 1024 * 1024 };
            size = size[..^1];
        }
        if (size.Length == 0 || size.Length > 12 || !size.All(char.IsAsciiDigit))
        {
            return null;
        }
        return long.Parse(size, System.Globalization.CultureInfo.InvariantCulture) * multiplier / (1024L * 1024);
    }
}
