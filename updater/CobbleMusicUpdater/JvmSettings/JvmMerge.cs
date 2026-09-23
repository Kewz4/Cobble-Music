namespace CobbleMusicUpdater;

/// <summary>What the policy would change in one instance, and why. <see cref="IsMissing"/> is the "only if missing" rule.</summary>
internal sealed record JvmMergeResult(
    bool MemoryMissing,
    bool WriteMemory,
    int TargetMaxMiB,
    int TargetMinMiB,
    IReadOnlyList<string> AddedTokens,
    string OldJvmArgs,
    string NewJvmArgs,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings)
{
    public bool IsMissing => MemoryMissing || AddedTokens.Count > 0;

    public bool WriteJvmArgs => AddedTokens.Count > 0;

    /// <summary>The instance.cfg [General] edits, already encoded the way QSettings writes them. Empty when nothing is missing.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> EncodedEdits()
    {
        var edits = new List<KeyValuePair<string, string>>();
        if (!IsMissing)
        {
            return edits;
        }
        if (WriteMemory)
        {
            edits.Add(new("OverrideMemory", PrismIni.EncodeBool(true)));
            edits.Add(new("MinMemAlloc", PrismIni.EncodeInt(TargetMinMiB)));
            edits.Add(new("MaxMemAlloc", PrismIni.EncodeInt(TargetMaxMiB)));
        }
        if (WriteJvmArgs)
        {
            edits.Add(new("OverrideJavaArgs", PrismIni.EncodeBool(true)));
            edits.Add(new("JvmArgs", PrismIni.EncodeString(NewJvmArgs)));
        }
        return edits;
    }
}

/// <summary>The section 8 merge rules. Pure: the same effective settings and desired state always give the same result.</summary>
internal static class JvmMerge
{
    private const long MiBPerGiB = 1024;

    public static JvmMergeResult Compute(PrismEffectiveSettings effective, JvmDesiredState desired, ulong totalPhysicalBytes)
    {
        var reasons = new List<string>();
        var warnings = new List<string>();
        IReadOnlyList<string> tokens = effective.JvmTokens;

        // Memory. Prism appends its own -Xms/-Xmx after JvmArgs and HotSpot uses the last -Xmx, so an -Xmx inside JvmArgs
        // never takes effect (section 1). The restart trigger therefore compares the heap Prism really passes with the floor.
        // The player's -Xmx is honoured as intent: whenever memory is written, MaxMemAlloc is raised to it, never lowered.
        long playerXmx = tokens.Select(JvmPolicy.XmxMiB).Where(value => value.HasValue).Select(value => value!.Value)
            .DefaultIfEmpty(0).Max();
        bool belowMinimum = desired.Tier.IsBelowMinimum;
        int heap = effective.HeapMiB;
        bool memoryMissing = !belowMinimum && heap < desired.XmxFloorMiB;
        int targetMax = belowMinimum ? heap : (int)Math.Min(int.MaxValue, Math.Max(desired.XmxFloorMiB, playerXmx));
        if (memoryMissing)
        {
            reasons.Add($"heap {heap} MiB is below the {desired.Tier.Name} floor of {desired.XmxFloorMiB} MiB");
        }
        if (playerXmx > 0 && playerXmx > heap)
        {
            warnings.Add($"JvmArgs asks for -Xmx {playerXmx} MiB, but Prism's MaxMemAlloc decides the heap ({heap} MiB)");
        }
        long physicalMiB = (long)(totalPhysicalBytes / (1024 * 1024));
        if (Math.Max(targetMax, heap) > physicalMiB - 4 * MiBPerGiB)
        {
            warnings.Add($"the player's heap setting ({Math.Max(targetMax, heap)} MiB) leaves less than 4 GiB of the PC's {physicalMiB} MiB");
        }

        // Flags, key-based so the player's value always wins.
        var presentKeys = new HashSet<string>(tokens.Select(JvmPolicy.XxKey).OfType<string>(), StringComparer.Ordinal);
        string? gcSelector = presentKeys.FirstOrDefault(JvmPolicy.GcSelectorKeys.Contains);
        bool hasGcFileLog = tokens.Any(JvmPolicy.IsGcFileLog);
        var added = new List<string>();
        foreach (JvmPolicyFlag flag in desired.Flags)
        {
            if (flag.IsGcLog)
            {
                if (!hasGcFileLog)
                {
                    added.Add(flag.Token);
                }
                continue;
            }
            if (flag.IsGcGroup && gcSelector is not null)
            {
                continue;
            }
            if (!presentKeys.Contains(flag.Key))
            {
                added.Add(flag.Token);
            }
        }
        if (gcSelector is not null)
        {
            reasons.Add($"the player selects a collector ({gcSelector}), so none of the GC group is added");
        }
        foreach (string key in JvmPolicy.WarnKeys.Where(presentKeys.Contains))
        {
            warnings.Add($"the player's JvmArgs set {key}; it is kept unchanged");
        }
        foreach (string token in added)
        {
            reasons.Add($"missing {token}");
        }

        // The player's text is kept verbatim; ours is appended after one space. Trimming only spaces matters: splitArgs splits
        // on ' ' alone, so a trailing tab belongs to the last token and must not be removed.
        string oldText = effective.JvmArgs;
        string newText = oldText;
        if (added.Count > 0)
        {
            string kept = oldText.TrimEnd(' ');
            newText = kept.Length == 0 ? string.Join(' ', added) : kept + " " + string.Join(' ', added);
            IReadOnlyList<string> expected = [.. PrismArgs.Split(oldText), .. added];
            if (!PrismArgs.Split(newText).SequenceEqual(expected, StringComparer.Ordinal))
            {
                // An unterminated quote or a trailing escape in the player's text would swallow our flags.
                throw new PrismIniUnsupportedException("JvmArgs ends inside a quote, so appending would change the player's arguments.");
            }
        }

        bool isMissing = memoryMissing || added.Count > 0;
        bool writeMemory = isMissing && !belowMinimum && heap < targetMax;
        int targetMin = Math.Min(Math.Max(desired.XmsMiB, effective.XmsMiB), targetMax);
        return new JvmMergeResult(memoryMissing, writeMemory, targetMax, targetMin, added, oldText, newText, reasons, warnings);
    }
}
