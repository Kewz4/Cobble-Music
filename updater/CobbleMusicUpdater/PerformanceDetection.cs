using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CobbleMusicUpdater;

// Hardware sources for the lite decision. Every member can be replaced by tests; the real ones only READ the registry
// (and ask Windows which display devices are present). Nothing is measured: round 1's processor benchmark was dropped
// because it mostly timed memory latency and depended on what else was running (lite0924/build/VERIFY.md).
internal sealed class PerformanceEnvironment
{
    public Func<string> ReadCpuName { get; init; } = CpuRegistry.ReadName;
    public Func<IReadOnlyList<GpuAdapterInfo>> ReadGpus { get; init; } = GpuRegistry.Read;
    public Func<HardwareDb> Database { get; init; } = () => HardwareDb.Embedded;

    public static PerformanceEnvironment Default { get; } = new();
}

internal static class CpuRegistry
{
    // HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0 ProcessorNameString, exactly as Windows stores it apart from
    // the outer spaces (Intel pads some names on the left). The normaliser collapses inner spaces itself.
    public static string ReadName()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string ?? "").Trim();
    }
}

// Display adapters from the display-class registry key (no admin rights, no WMI: WMI's AdapterRAM caps at 4 GiB).
// The class key also keeps entries for cards that were removed, so each entry is matched to a PRESENT display device
// through SetupAPI (its driver key "{4d36e968-...}\NNNN"); an entry with no present device is marked Present = false.
internal static class GpuRegistry
{
    internal const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\" + DisplayClassGuid;

    public static IReadOnlyList<GpuAdapterInfo> Read()
    {
        Dictionary<string, string>? present = PresentDisplayDevices.TryRead();
        var adapters = new List<GpuAdapterInfo>();
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (root is null) return adapters;
        foreach (string subKeyName in root.GetSubKeyNames().Order(StringComparer.Ordinal))
        {
            if (subKeyName.Length != 4 || !subKeyName.All(char.IsAsciiDigit)) continue;
            try
            {
                using RegistryKey? adapter = root.OpenSubKey(subKeyName);
                if (adapter is null) continue;
                string name = (adapter.GetValue("DriverDesc") as string ?? "").Trim();
                if (name.Length == 0) continue;
                adapters.Add(new GpuAdapterInfo
                {
                    Name = name,
                    DeviceId = (adapter.GetValue("MatchingDeviceId") as string ?? "").Trim(),
                    HardwareId = present is not null && present.TryGetValue(subKeyName, out string? hardwareId) ? hardwareId : "",
                    MemoryBytes = ReadMemory(adapter),
                    RegistryKey = subKeyName,
                    Present = present is null ? null : present.ContainsKey(subKeyName)
                });
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // The class key also holds a protected "Properties" subkey; unreadable adapters are skipped.
            }
        }
        return adapters;
    }

    private static long ReadMemory(RegistryKey adapter) =>
        adapter.GetValue("HardwareInformation.qwMemorySize") switch
        {
            long value => value,
            int value => (uint)value,
            byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
            byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
            _ => adapter.GetValue("HardwareInformation.MemorySize") switch
            {
                int value => (uint)value,
                long value => value,
                byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
                _ => 0L
            }
        };
}

// SetupAPI: the display devices Windows reports as PRESENT, keyed by their class subkey ("0001"), with the first hardware
// id ("PCI\VEN_10DE&DEV_1E91&..."). Returns null when the query itself fails (then presence is unknown, and every class
// entry is used as before). Read-only.
internal static class PresentDisplayDevices
{
    private const uint DigcfPresent = 0x2;
    private const uint SpdrpHardwareId = 0x1;
    private const uint SpdrpDriver = 0x9;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevinfoData data, uint property,
        out uint registryType, byte[]? buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    public static Dictionary<string, string>? TryRead()
    {
        try
        {
            Guid display = new(GpuRegistry.DisplayClassGuid);
            IntPtr set = SetupDiGetClassDevsW(ref display, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
            if (set == InvalidHandle || set == IntPtr.Zero) return null;
            try
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (uint index = 0; index < 64; index++)
                {
                    var data = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
                    if (!SetupDiEnumDeviceInfo(set, index, ref data)) break;
                    string driver = ReadString(set, ref data, SpdrpDriver);
                    int slash = driver.LastIndexOf('\\');
                    if (slash < 0 || !driver[..slash].Equals(GpuRegistry.DisplayClassGuid, StringComparison.OrdinalIgnoreCase)) continue;
                    result[driver[(slash + 1)..]] = ReadString(set, ref data, SpdrpHardwareId);
                }
                return result;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return null;
        }
    }

    // REG_SZ or the first string of a REG_MULTI_SZ.
    private static string ReadString(IntPtr set, ref SpDevinfoData data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out uint required);
        if (required == 0 || required > 64 * 1024) return "";
        byte[] buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, required, out _)) return "";
        string text = Encoding.Unicode.GetString(buffer);
        int end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }
}

internal sealed record AdapterVerdict(GpuAdapterInfo Adapter, bool Real, string NotRealReason, string PciVendor, string PciDevice, HardwareMatch Match);

internal sealed record HardwareDecision(
    PerformanceMode Mode,
    string CpuName,
    HardwareMatch Cpu,
    IReadOnlyList<AdapterVerdict> Adapters,
    AdapterVerdict? BestGpu,
    bool GpuVotes,
    bool CpuLite,
    bool GpuLite,
    string Reason,
    string Fingerprint,
    PerformanceDetectionRules Rules,
    string DatabaseVersion);

// The whole lite decision as one pure function of (processor name, display adapters, table, lines):
//   * processor: LITE vote when its table score is below cpuSingleThreadBelow; not in the table = no vote;
//   * graphics: only REAL adapters count (present, a PCI hardware id from NVIDIA 10DE, AMD 1002/1022 or Intel 8086,
//     not a Basic Display / remote / virtual / indirect / USB adapter). The BEST real adapter's score is compared with
//     gpuScoreBelow. If any real adapter is not in the table, graphics do not vote (the unknown card could be the best);
//   * LITE when either vote says lite; nothing known = FULL.
internal static class HardwareVerdict
{
    public static HardwareDecision Decide(string cpuName, IReadOnlyList<GpuAdapterInfo> adapters, HardwareDb db, PerformanceDetectionRules rules)
    {
        HardwareMatch cpu = cpuName.Length == 0 ? HardwareMatch.Unknown(null, "not-read") : db.ResolveCpu(cpuName);
        var verdicts = adapters.Select(adapter => Classify(adapter, db, cpuName)).ToList();
        var real = verdicts.Where(item => item.Real).ToList();
        AdapterVerdict? best = real.Where(item => item.Match.Status == HardwareMatchStatus.Known)
            .OrderByDescending(item => item.Match.Score).ThenBy(item => item.Adapter.RegistryKey, StringComparer.Ordinal).FirstOrDefault();
        bool gpuVotes = real.Count > 0 && real.All(item => item.Match.Status == HardwareMatchStatus.Known);
        bool cpuLite = cpu.Status == HardwareMatchStatus.Known && cpu.Score < rules.CpuSingleThreadBelow;
        bool gpuLite = gpuVotes && best is not null && best.Match.Score < rules.GpuScoreBelow;
        PerformanceMode mode = cpuLite || gpuLite ? PerformanceMode.Lite : PerformanceMode.Full;
        string reason = mode == PerformanceMode.Lite
            ? cpuLite && gpuLite ? "processor and graphics card are below their lines" : cpuLite ? "processor is below its line" : "graphics card is below its line"
            : cpu.Status != HardwareMatchStatus.Known && !gpuVotes ? "nothing could be looked up"
            : "neither is below its line";
        string fingerprint = "cpu=" + cpuName + "|gpu=" + string.Join(";", real
            .Select(item => $"{item.Adapter.Name}@{item.PciVendor}:{item.PciDevice}@{RoundGib(item.Adapter.MemoryBytes)}g")
            .Order(StringComparer.Ordinal));
        return new HardwareDecision(mode, cpuName, cpu, verdicts, best, gpuVotes, cpuLite, gpuLite, reason, fingerprint, rules, db.Version);
    }

    private static AdapterVerdict Classify(GpuAdapterInfo adapter, HardwareDb db, string cpuName)
    {
        string pciId = IsPci(adapter.DeviceId) ? adapter.DeviceId : IsPci(adapter.HardwareId) ? adapter.HardwareId : "";
        (string? vendor, string? device) = HardwareNames.ParsePciId(pciId);
        AdapterVerdict NotReal(string why) =>
            new(adapter, false, why, vendor ?? "", device ?? "", HardwareMatch.Ignore(why));
        if (adapter.Present == false) return NotReal("not present (left over from removed hardware)");
        if (HardwareNames.IsVirtualAdapter(adapter.Name)) return NotReal("virtual, remote or indirect display adapter");
        if (vendor is null || device is null) return NotReal("no PCI hardware id");
        if (!HardwareNames.GraphicsVendors.Contains(vendor)) return NotReal($"PCI vendor {vendor} is not NVIDIA, AMD or Intel");
        HardwareMatch match = db.ResolveGpu(adapter.Name, pciId, adapter.MemoryBytes > 0 ? adapter.MemoryBytes : null, cpuName);
        if (match.Status == HardwareMatchStatus.Ignore) return NotReal(match.Rule);
        return new AdapterVerdict(adapter, true, "", vendor, device, match);
    }

    private static bool IsPci(string? id) => id is not null && id.StartsWith(@"pci\", StringComparison.OrdinalIgnoreCase);

    internal static int RoundGib(long bytes) => bytes <= 0 ? 0 : (int)Math.Round(bytes / (double)(1L << 30));

    // One plain log line: every name, the table key it matched, the score, the lines and the verdict.
    public static string LogLine(HardwareDecision decision, string decided)
    {
        var text = new StringBuilder("Performance check: processor \"").Append(decision.CpuName).Append("\" ");
        text.Append(decision.Cpu.Status == HardwareMatchStatus.Known
            ? $"= \"{decision.Cpu.Key}\" {decision.Cpu.Score}{(decision.Cpu.Ambiguous ? " (ambiguous key)" : "")}"
            : $"not in the table (key \"{decision.Cpu.Key}\")");
        text.Append($" (lite below {decision.Rules.CpuSingleThreadBelow}); graphics ");
        var real = decision.Adapters.Where(item => item.Real).ToList();
        text.Append(real.Count == 0 ? "none real" : string.Join(", ", real.Select(item =>
            $"\"{item.Adapter.Name}\" [{item.PciVendor}:{item.PciDevice}, {RoundGib(item.Adapter.MemoryBytes)} GiB] "
            + (item.Match.Status == HardwareMatchStatus.Known
                ? $"= \"{item.Match.Key}\" {item.Match.Score} ({item.Match.Rule}{(item.Match.Ambiguous ? ", ambiguous" : "")})"
                : $"not in the table (key \"{item.Match.Key}\")"))));
        text.Append(decision.BestGpu is null ? "; no known graphics card" : $"; best {decision.BestGpu.Match.Score}");
        text.Append($" (lite below {decision.Rules.GpuScoreBelow}){(decision.GpuVotes || real.Count == 0 ? "" : "; graphics do not vote: a real card is not in the table")}");
        var ignored = decision.Adapters.Where(item => !item.Real).ToList();
        if (ignored.Count > 0)
            text.Append("; ignored ").Append(string.Join(", ", ignored.Select(item => $"\"{item.Adapter.Name}\" ({item.NotRealReason})")));
        text.Append($"; table {decision.DatabaseVersion}; result {decision.Mode.ToString().ToUpperInvariant()} ({decision.Reason}); {decided}.");
        text.Append(" Override: cobble-music-updater/").Append(PerformanceModeFile.FileName);
        return text.ToString();
    }

    // The same facts for performance-mode.txt, in plain words (one line, after "# This computer:").
    public static string StatusText(HardwareDecision decision, PerformanceMode picked)
    {
        string processor = decision.Cpu.Status == HardwareMatchStatus.Known
            ? $"processor {Plain(decision.CpuName)}, speed score {decision.Cpu.Score} (lite below {decision.Rules.CpuSingleThreadBelow})"
            : decision.CpuName.Length == 0 ? "processor could not be read"
            : $"processor {Plain(decision.CpuName)} is not in the updater's list, so it does not count";
        var real = decision.Adapters.Where(item => item.Real).ToList();
        string graphics;
        if (real.Count == 0) graphics = "no graphics card could be read";
        else if (!decision.GpuVotes)
            graphics = "graphics card " + string.Join(" + ", real.Where(item => item.Match.Status != HardwareMatchStatus.Known).Select(item => Plain(item.Adapter.Name)))
                + " is not in the updater's list, so graphics do not count";
        else
            graphics = $"best graphics card {Plain(decision.BestGpu!.Adapter.Name)}, speed score {decision.BestGpu.Match.Score}"
                + (decision.BestGpu.Match.Rule == "nvidia-mobile-by-device-id" ? " as a laptop chip" : "")
                + $" (lite below {decision.Rules.GpuScoreBelow})";
        return $"{processor}; {graphics}. Picked: {picked.ToString().ToLowerInvariant()}.";
    }

    private static string Plain(string name) =>
        HardwareNames.Squash(name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("®", "", StringComparison.Ordinal).Replace("™", "", StringComparison.Ordinal));
}

// The one plain file a player edits to force a mode: <minecraft>/cobble-music-updater/performance-mode.txt.
internal static class PerformanceModeFile
{
    public const string FileName = "performance-mode.txt";
    private const string StatusPrefix = "# This computer:";

    public static string PathFor(UpdaterPaths paths) => Path.Combine(paths.InstallationDirectory, FileName);

    // Returns "auto", "lite" or "full". Anything unreadable or unknown counts as auto (and is logged).
    public static string Read(string path, Action<string> log)
    {
        if (!File.Exists(path)) return "auto";
        string text;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > 64 * 1024)
            {
                log("performance-mode.txt is unusually large; using mode=auto.");
                return "auto";
            }
            text = new UTF8Encoding(false, false).GetString(bytes).TrimStart('﻿');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"performance-mode.txt could not be read ({exception.GetType().Name}); using mode=auto.");
            return "auto";
        }
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int separator = line.IndexOfAny(['=', ':']);
            if (separator < 0 || !line[..separator].Trim().Equals("mode", StringComparison.OrdinalIgnoreCase)) continue;
            string value = line[(separator + 1)..].Trim().ToLowerInvariant();
            if (value is "auto" or "lite" or "full") return value;
            log($"performance-mode.txt has mode={value}, which is not auto, lite or full; using mode=auto.");
            return "auto";
        }
        return "auto";
    }

    public static string Template(string status) =>
        "# Kewz's Cobblemon - performance mode\r\n"
        + "# Change the mode line and start the game again:\r\n"
        + "#   mode=auto  the updater picks for this computer from its built-in list of processor and graphics card speeds\r\n"
        + "#   mode=lite  lighter pack: fewer visual extras, runs better with shaders\r\n"
        + "#   mode=full  the whole pack\r\n"
        + "mode=auto\r\n"
        + "#\r\n"
        + StatusPrefix + " " + status + "\r\n";

    // Creates the file when it is missing, or refreshes only its status line. The player's mode line and
    // every other line stay byte-for-byte as they are. Never throws: this file is a convenience.
    public static void EnsureStatus(string path, string status, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                WriteAtomic(path, Encoding.UTF8.GetBytes(Template(status)));
                return;
            }
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > 64 * 1024) return;
            string text = new UTF8Encoding(false, false).GetString(bytes);
            string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string wanted = StatusPrefix + " " + status;
            string[] lines = text.Split('\n');
            int index = Array.FindIndex(lines, line => line.TrimEnd('\r').StartsWith(StatusPrefix, StringComparison.Ordinal));
            string updated;
            if (index >= 0)
            {
                if (lines[index].TrimEnd('\r') == wanted) return;
                lines[index] = wanted + (lines[index].EndsWith('\r') ? "\r" : "");
                updated = string.Join('\n', lines);
            }
            else
            {
                updated = text + (text.Length == 0 || text.EndsWith('\n') ? "" : newline) + wanted + newline;
            }
            WriteAtomic(path, Encoding.UTF8.GetBytes(updated));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"performance-mode.txt could not be updated ({exception.GetType().Name}); the mode is unchanged.");
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

// The machine-wide (per Windows user) record of the last automatic decision
// (%LOCALAPPDATA%\CobbleMusicUpdater\performance-detection.json). The verdict is re-decided only when the hardware
// fingerprint, the built-in table's version or the signed lines change.
internal static class MachinePerformanceStore
{
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string PathFor(UpdaterPaths paths) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(paths.LocalDataDirectory).TrimEnd(Path.DirectorySeparatorChar))!,
            "performance-detection.json");

    public static MachinePerformanceRecord? Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return null;
            MachinePerformanceRecord? record = JsonSerializer.Deserialize<MachinePerformanceRecord>(File.ReadAllBytes(path), JsonOptions);
            return record is { SchemaVersion: SchemaVersion, Fingerprint: not null, DatabaseVersion: not null, Verdict: "lite" or "full" }
                ? record : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string path, MachinePerformanceRecord record, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"The hardware decision could not be saved ({exception.GetType().Name}); it is decided again next launch.");
        }
    }

    public static string FormatDate(DateTimeOffset value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
