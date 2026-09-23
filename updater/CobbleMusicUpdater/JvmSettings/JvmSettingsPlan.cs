using System.Text.Json;
using System.Text.Json.Serialization;

namespace CobbleMusicUpdater;

/// <summary>A process identity as stored in plan.json (creation time as UTC ticks so it round-trips exactly).</summary>
internal sealed record JvmPlanProcess(int Pid, long StartUtcTicks, string ImagePath)
{
    public static JvmPlanProcess From(ProcessIdentity identity) =>
        new(identity.Pid, identity.StartUtc.ToUniversalTime().Ticks, identity.ImagePath);

    public ProcessIdentity ToIdentity() => new(Pid, new DateTime(StartUtcTicks, DateTimeKind.Utc), ImagePath);
}

/// <summary>
/// The hand-off from the pre-launch updater to the helper (section 6 A6). The helper never trusts it: every field that
/// decides an action (the Prism exe, the relaunch arguments, the data directory, the desired state) is recomputed from the
/// live, identity-verified Prism process and the compiled policy, and the helper stops when they differ.
/// </summary>
internal sealed class JvmSettingsPlan
{
    public const int CurrentSchema = 1;
    public const string FileName = "jvm-settings-plan.json";
    public const string ReadyFileName = "jvm-settings-plan.ready";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public int Schema { get; set; } = CurrentSchema;
    public int PolicyVersion { get; set; }
    public JvmPlanProcess Prism { get; set; } = null!;
    public JvmPlanProcess? PowerShell { get; set; }
    public JvmPlanProcess Updater { get; set; } = null!;
    public string PrismExePath { get; set; } = string.Empty;
    public List<string> RelaunchArguments { get; set; } = [];
    public string? DataDirEnvironment { get; set; }
    public string DataDirectory { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string InstanceDirectory { get; set; } = string.Empty;
    public string MinecraftDirectory { get; set; } = string.Empty;
    public string InstanceConfigPath { get; set; } = string.Empty;
    public string GlobalConfigPath { get; set; } = string.Empty;
    public string LocalDataDirectory { get; set; } = string.Empty;
    public ulong TotalPhysicalBytes { get; set; }
    public int XmxFloorMiB { get; set; }
    public int XmsMiB { get; set; }
    public List<string> Flags { get; set; } = [];

    public static string PathFor(string localDataDirectory) => Path.Combine(localDataDirectory, FileName);

    public static string ReadyPathFor(string localDataDirectory) => Path.Combine(localDataDirectory, ReadyFileName);

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static JvmSettingsPlan Deserialize(byte[] content) =>
        JsonSerializer.Deserialize<JvmSettingsPlan>(content, JsonOptions)
        ?? throw new InvalidDataException("The JVM settings plan is empty.");

    public void Write(string path)
    {
        string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(Serialize());
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

    /// <summary>True when the plan's copy of the desired state equals the compiled policy for this PC.</summary>
    public bool MatchesDesired(JvmDesiredState desired) =>
        PolicyVersion == desired.PolicyVersion
        && XmxFloorMiB == desired.XmxFloorMiB
        && XmsMiB == desired.XmsMiB
        && Flags.SequenceEqual(desired.Flags.Select(flag => flag.Token), StringComparer.Ordinal);
}

/// <summary>Prism's original command line, as far as the relaunch needs it.</summary>
internal sealed record PrismCommandLine(string? DirArgument, IReadOnlyList<string> KeptArguments);

/// <summary>The relaunch arguments (section 6 A6 and section 11 test 9).</summary>
internal static class PrismRelaunch
{
    private static readonly HashSet<string> DroppedWithValue = new(StringComparer.Ordinal)
    {
        "-l", "--launch", "-s", "--server", "-w", "--world", "-a", "--profile", "-o", "--offline"
    };

    private static readonly string[] DroppedLongWithEquals = ["--launch=", "--server=", "--world=", "--profile=", "--offline="];

    /// <summary>
    /// Parses Prism's argv (argv[0] is the exe). Only the options Prism 10/11 defines for launching are understood:
    /// -d/--dir and --alive are kept verbatim; -l/--launch, -s/--server, -w/--world, -a/--profile and -o/--offline (with
    /// their values, spaced or --long=value) are dropped. Anything else (import URLs, --show, compact short options such
    /// as -dPATH, a missing value, a repeated --dir) returns null: a relaunch we cannot reproduce exactly is not attempted.
    /// </summary>
    public static PrismCommandLine? Parse(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
        {
            return null;
        }
        string? dir = null;
        var kept = new List<string>();
        for (int index = 1; index < argv.Count; index++)
        {
            string argument = argv[index];
            if (argument == "--alive")
            {
                kept.Add(argument);
            }
            else if (argument is "-d" or "--dir")
            {
                if (dir is not null || index + 1 >= argv.Count)
                {
                    return null;
                }
                dir = argv[++index];
                kept.Add(argument);
                kept.Add(dir);
            }
            else if (argument.StartsWith("--dir=", StringComparison.Ordinal))
            {
                if (dir is not null)
                {
                    return null;
                }
                dir = argument["--dir=".Length..];
                kept.Add(argument);
            }
            else if (DroppedWithValue.Contains(argument))
            {
                if (index + 1 >= argv.Count)
                {
                    return null;
                }
                index++;
            }
            else if (DroppedLongWithEquals.Any(prefix => argument.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }
            else
            {
                return null;
            }
        }
        if (dir is not null && (dir.Length == 0 || !Path.IsPathFullyQualified(dir)))
        {
            // Prism resolved a relative --dir against its own start-up directory, which we cannot know.
            return null;
        }
        return new PrismCommandLine(dir, kept);
    }

    /// <summary>
    /// The arguments for the relaunch: the kept original arguments, then --launch &lt;INST_ID&gt;. When the data directory came
    /// from PRISMLAUNCHER_DATA_DIR (no --dir), --dir &lt;that directory&gt; is added: the Explorer relaunch starts Prism with
    /// the user's clean environment, which does not carry that variable, and --dir has the higher precedence anyway.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(PrismCommandLine original, string instanceId, string? dataDirFromEnvironment)
    {
        var arguments = new List<string>(original.KeptArguments);
        if (original.DirArgument is null && !string.IsNullOrEmpty(dataDirFromEnvironment))
        {
            arguments.Add("--dir");
            arguments.Add(dataDirFromEnvironment);
        }
        arguments.Add("--launch");
        arguments.Add(instanceId);
        return arguments;
    }
}
