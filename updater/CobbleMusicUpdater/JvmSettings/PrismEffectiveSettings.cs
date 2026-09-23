namespace CobbleMusicUpdater;

/// <summary>
/// The memory and JVM-argument settings Prism 10/11 uses for one instance, computed exactly the way Prism does:
/// OverrideSetting::get (settings/OverrideSetting.cpp) returns the global value while the gate is false, and the instance
/// value while it is true, falling back to the global value when the instance key is absent (Setting.cpp:21-32).
/// Keys present in instance.cfg while the gate is false are ignored on purpose: SettingsObject::reload copies global values
/// into instance.cfg (the "bedrock" instance has OverrideMemory=false with a stale MaxMemAlloc=4096).
/// </summary>
internal sealed record PrismEffectiveSettings(
    bool OverrideMemory,
    int MinMemAllocMiB,
    int MaxMemAllocMiB,
    bool OverrideJavaArgs,
    string JvmArgs,
    IReadOnlyList<string> JvmTokens,
    string GlobalJvmArgs)
{
    public const string SupportedConfigVersion = "1.3";
    private const string HeapDumpPathPrefix = "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance";

    /// <summary>The heap Prism passes: -Xmx is max(Min, Max) because Prism swaps the pair when Min is not below Max.</summary>
    public int HeapMiB => Math.Max(MinMemAllocMiB, MaxMemAllocMiB);

    /// <summary>The -Xms Prism passes: min(Min, Max).</summary>
    public int XmsMiB => Math.Min(MinMemAllocMiB, MaxMemAllocMiB);

    /// <summary>
    /// Resolves the effective settings. Throws <see cref="PrismIniUnsupportedException"/> for anything not modelled exactly:
    /// a ConfigVersion other than 1.3 (Prism migrates older files on load), the legacy MinMemoryAlloc/MaxMemoryAlloc synonyms
    /// (Prism's reload rewrites them to the main keys, so their presence means the file changed under Prism), or a
    /// non-integer memory value.
    /// </summary>
    public static PrismEffectiveSettings Resolve(PrismIniDocument instance, PrismIniDocument global, ulong totalPhysicalBytes)
    {
        RequireConfigVersion(instance, "instance.cfg");
        RequireConfigVersion(global, "prismlauncher.cfg");
        foreach (string synonym in new[] { "MinMemoryAlloc", "MaxMemoryAlloc" })
        {
            if (instance.FindGeneral(synonym) is not null || global.FindGeneral(synonym) is not null)
            {
                throw new PrismIniUnsupportedException($"The legacy key {synonym} is present.");
            }
        }

        // Global defaults registered in Application.cpp:746-758.
        int globalMin = ReadInt(global, "MinMemAlloc") ?? 512;
        int globalMax = ReadInt(global, "MaxMemAlloc") ?? SuitableMaxMem(totalPhysicalBytes);
        string globalJvmArgs = global.GetGeneralString("JvmArgs") ?? string.Empty;

        bool overrideMemory = PrismIni.ToBool(instance.GetGeneralString("OverrideMemory"));
        int min = overrideMemory ? ReadInt(instance, "MinMemAlloc") ?? globalMin : globalMin;
        int max = overrideMemory ? ReadInt(instance, "MaxMemAlloc") ?? globalMax : globalMax;

        bool overrideJavaArgs = PrismIni.ToBool(instance.GetGeneralString("OverrideJavaArgs"));
        string jvmArgs = overrideJavaArgs ? instance.GetGeneralString("JvmArgs") ?? globalJvmArgs : globalJvmArgs;

        return new PrismEffectiveSettings(
            overrideMemory, min, max, overrideJavaArgs, jvmArgs, PrismArgs.Split(jvmArgs), globalJvmArgs);
    }

    /// <summary>Port of SysInfo::suitableMaxMem (SysInfo.cpp:54-66), the global MaxMemAlloc default.</summary>
    public static int SuitableMaxMem(ulong totalPhysicalBytes)
    {
        float totalRam = totalPhysicalBytes / (float)(1024 * 1024);
        return totalRam < 4096 * 1.5f ? (int)(totalRam / 1.5f) : 4096;
    }

    /// <summary>
    /// Section 6 A2 cross-check against INST_JAVA_ARGS, which Prism exports as javaArguments().join(' ') of this very launch
    /// (MinecraftInstance.cpp:653). The argument list is: the JvmArgs tokens, then extra arguments Prism adds itself (jar mods,
    /// profile JVM arguments, agents, native library paths; MinecraftInstance.cpp:511-553), then -XX:HeapDumpPath=MojangTricks...,
    /// then -Xms/-Xmx. So the JvmArgs tokens must be a prefix of the joined string (not the whole run before HeapDumpPath),
    /// and the two tokens after the last HeapDumpPath must be exactly the -Xms/-Xmx these settings produce. Any mismatch means
    /// the files changed since Prism's launch-time reload, and the caller takes no action.
    /// </summary>
    public bool MatchesLaunchArguments(string instJavaArgs, out string reason)
    {
        string prefix = string.Join(' ', JvmTokens);
        if (prefix.Length > 0
            && !(instJavaArgs.StartsWith(prefix, StringComparison.Ordinal)
                && (instJavaArgs.Length == prefix.Length || instJavaArgs[prefix.Length] == ' ')))
        {
            reason = "the JvmArgs Prism is launching with differ from the files";
            return false;
        }
        string[] tokens = instJavaArgs[prefix.Length..].Split(' ');
        int heapDump = Array.FindLastIndex(tokens, token => token.StartsWith(HeapDumpPathPrefix, StringComparison.Ordinal));
        if (heapDump < 0 || heapDump + 2 >= tokens.Length)
        {
            reason = "INST_JAVA_ARGS has no Prism memory arguments";
            return false;
        }
        string expectedXms = $"-Xms{XmsMiB}m";
        string expectedXmx = $"-Xmx{HeapMiB}m";
        if (tokens[heapDump + 1] != expectedXms || tokens[heapDump + 2] != expectedXmx)
        {
            reason = $"Prism is launching with {tokens[heapDump + 1]} {tokens[heapDump + 2]}, the files say {expectedXms} {expectedXmx}";
            return false;
        }
        reason = "INST_JAVA_ARGS matches the files";
        return true;
    }

    private static void RequireConfigVersion(PrismIniDocument document, string name)
    {
        string? version = document.GetGeneralString("ConfigVersion");
        if (version != SupportedConfigVersion)
        {
            throw new PrismIniUnsupportedException($"{name} has ConfigVersion {version ?? "(none)"}, not {SupportedConfigVersion}.");
        }
    }

    private static int? ReadInt(PrismIniDocument document, string key)
    {
        string? text = document.GetGeneralString(key);
        if (text is null)
        {
            return null;
        }
        return PrismIni.ToStrictInt(text) ?? throw new PrismIniUnsupportedException($"{key} is not a whole number: {text}");
    }
}

/// <summary>Prism's data directory and instance folder, resolved with the Application.cpp:396-426 rules.</summary>
internal static class PrismDataDir
{
    public const string DataDirEnvironmentVariable = "PRISMLAUNCHER_DATA_DIR";
    public const string GlobalConfigFileName = "prismlauncher.cfg";

    /// <summary>
    /// Data directory precedence: --dir, then PRISMLAUNCHER_DATA_DIR, then &lt;exe dir&gt;/UserData when it exists, then
    /// &lt;exe dir&gt; when portable.txt exists, then %APPDATA%/PrismLauncher. A relative --dir or environment value was
    /// resolved against Prism's own start-up working directory, which we cannot know, so it gives null (no plan).
    /// </summary>
    public static string? Resolve(string prismExePath, string? dirArgument, string? dataDirEnvironment, string appDataDirectory)
    {
        if (!string.IsNullOrEmpty(dirArgument))
        {
            return Path.IsPathFullyQualified(dirArgument) ? Normalize(dirArgument) : null;
        }
        if (!string.IsNullOrEmpty(dataDirEnvironment))
        {
            return Path.IsPathFullyQualified(dataDirEnvironment) ? Normalize(dataDirEnvironment) : null;
        }
        string? exeDirectory = Path.GetDirectoryName(prismExePath);
        if (string.IsNullOrEmpty(exeDirectory))
        {
            return null;
        }
        string userData = Path.Combine(exeDirectory, "UserData");
        if (Directory.Exists(userData))
        {
            return Normalize(userData);
        }
        if (File.Exists(Path.Combine(exeDirectory, "portable.txt")))
        {
            return Normalize(exeDirectory);
        }
        return string.IsNullOrEmpty(appDataDirectory) ? null : Normalize(Path.Combine(appDataDirectory, "PrismLauncher"));
    }

    /// <summary>The InstanceDir setting (default "instances"), made absolute against the data directory.</summary>
    public static string ResolveInstancesDirectory(string dataDirectory, PrismIniDocument global)
    {
        // An empty value is a valid setting: QDir("") is the working directory, which Prism set to the data directory.
        string instanceDir = global.GetGeneralString("InstanceDir") ?? "instances";
        return Normalize(Path.IsPathRooted(instanceDir) ? instanceDir : Path.Combine(dataDirectory, instanceDir));
    }

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool SamePath(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
