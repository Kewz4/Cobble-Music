using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CobbleMusicUpdater;

// Hardware sources for the one-time lite check. Every member can be replaced by tests; the real ones only read
// the registry (no admin rights, no WMI) and run a ~1 s single-thread benchmark on its own thread.
internal sealed class PerformanceEnvironment
{
    public Func<string> ReadCpuName { get; init; } = CpuRegistry.ReadName;
    public Func<IReadOnlyList<GpuAdapterInfo>> ReadGpus { get; init; } = GpuRegistry.Read;
    public Func<CancellationToken, CpuProbeResult> MeasureCpu { get; init; } = token => SingleCoreProbe.Run(token);
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumCpuAttempts { get; init; } = 3;

    public static PerformanceEnvironment Default { get; } = new();
}

internal sealed record CpuProbeResult(double Score, double QuantaPerSecond, long ElapsedMilliseconds);

internal static class CpuRegistry
{
    public static string ReadName()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        string name = (key?.GetValue("ProcessorNameString") as string ?? "").Trim();
        return name.Length == 0 ? "unknown processor" : CollapseSpaces(name);
    }

    internal static string CollapseSpaces(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

// Display adapters from the display-class registry key. WMI's AdapterRAM caps at 4 GiB (measured: an 8 GB
// RTX 2070 Super reads 4,293,918,720 there) and needs a NuGet package; this key needs neither.
internal static class GpuRegistry
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static IReadOnlyList<GpuAdapterInfo> Read()
    {
        var adapters = new List<GpuAdapterInfo>();
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (root is null) return adapters;
        foreach (string subKeyName in root.GetSubKeyNames())
        {
            if (subKeyName.Length != 4 || !subKeyName.All(char.IsAsciiDigit)) continue;
            try
            {
                using RegistryKey? adapter = root.OpenSubKey(subKeyName);
                if (adapter is null) continue;
                string name = CpuRegistry.CollapseSpaces((adapter.GetValue("DriverDesc") as string ?? "").Trim());
                if (name.Length == 0 || IsVirtualAdapter(name)) continue;
                long memory = adapter.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long value => value,
                    int value => (uint)value,
                    byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
                    byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
                    _ => adapter.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int value => (uint)value,
                        byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
                        _ => 0L
                    }
                };
                adapters.Add(new GpuAdapterInfo
                {
                    Name = name,
                    DeviceId = (adapter.GetValue("MatchingDeviceId") as string ?? "").Trim(),
                    MemoryBytes = memory
                });
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // The class key also holds a protected "Properties" subkey; unreadable adapters are skipped.
            }
        }
        return adapters
            .GroupBy(adapter => adapter.Name + "\0" + adapter.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    internal static bool IsVirtualAdapter(string name) =>
        name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Microsoft Remote Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Parsec Virtual", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Citrix", StringComparison.OrdinalIgnoreCase);
}

internal enum GpuTier
{
    Unknown,
    Lite,
    Full
}

// Whole-word matching of graphics card names: "NVIDIA GeForce RTX 3050 Laptop GPU" matches "RTX 3050" but
// "RX 6600 XT" does not match the pattern "RX 6600 XT" unless all three words appear in order. '#' in a
// pattern word matches any one digit ("RADEON ###M" = Radeon 610M..890M integrated graphics).
internal static class GpuTierList
{
    public static IReadOnlyList<string> Tokenize(string value)
    {
        string cleaned = value.Replace("(R)", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("®", " ", StringComparison.Ordinal)
            .Replace("™", " ", StringComparison.Ordinal);
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (char character in cleaned)
        {
            if (char.IsAsciiLetterOrDigit(character) || character == '#')
            {
                current.Append(char.ToUpperInvariant(character));
            }
            else if (current.Length != 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length != 0) tokens.Add(current.ToString());
        return tokens;
    }

    public static bool Matches(string adapterName, string pattern)
    {
        IReadOnlyList<string> name = Tokenize(adapterName);
        IReadOnlyList<string> want = Tokenize(pattern);
        if (want.Count == 0 || want.Count > name.Count) return false;
        for (int start = 0; start + want.Count <= name.Count; start++)
        {
            bool all = true;
            for (int offset = 0; offset < want.Count && all; offset++)
                all = WordMatches(name[start + offset], want[offset]);
            if (all) return true;
        }
        return false;
    }

    private static bool WordMatches(string word, string pattern)
    {
        if (word.Length != pattern.Length) return false;
        for (int index = 0; index < word.Length; index++)
        {
            if (pattern[index] == '#' ? !char.IsAsciiDigit(word[index]) : pattern[index] != word[index]) return false;
        }
        return true;
    }

    public static GpuTier Classify(string adapterName, PerformanceDetectionRules rules)
    {
        if (rules.FullGpuPatterns.Any(pattern => Matches(adapterName, pattern))) return GpuTier.Full;
        if (rules.LiteGpuPatterns.Any(pattern => Matches(adapterName, pattern))) return GpuTier.Lite;
        return GpuTier.Unknown;
    }

    // Any full card (or any card we do not know) keeps the PC out of lite on graphics; only when every real
    // adapter is on the lite list does the graphics card vote lite. No adapters read = unknown.
    public static GpuTier ClassifyMachine(IReadOnlyList<GpuAdapterInfo> adapters, PerformanceDetectionRules rules)
    {
        if (adapters.Count == 0) return GpuTier.Unknown;
        List<GpuTier> tiers = adapters.Select(adapter => Classify(adapter.Name, rules)).ToList();
        if (tiers.Contains(GpuTier.Full)) return GpuTier.Full;
        if (tiers.Contains(GpuTier.Unknown)) return GpuTier.Unknown;
        return GpuTier.Lite;
    }
}

// Single-thread processor probe (ported unchanged in its kernels from lite0924/research/cpubench, where it was
// calibrated). Fixed work per quantum, measured time; the score is the FAST TAIL (5th percentile quantum time)
// because interference only ever slows a quantum down. Score 1000 = Kewz's i7-10750H (6500 quanta/s).
internal static class SingleCoreProbe
{
    public const double ReferenceQuantaPerSecond = 6500.0;
    private const int TableBits = 18;
    private const int ChaseLength = 1 << 19;
    private const int HashOpsPerQuantum = 5_000;
    private const int ChaseStepsPerQuantum = 10_000;
    private const int VectorsPerQuantum = 500;

    public static CpuProbeResult Run(CancellationToken cancellationToken, double measureSeconds = 0.8)
    {
        var total = Stopwatch.StartNew();
        int[] table = new int[1 << TableBits];
        int[] chase = BuildCycle(ChaseLength, 0x9E3779B9u);
        float[] vectors = new float[VectorsPerQuantum * 4];
        for (int index = 0; index < vectors.Length; index++) vectors[index] = (index % 97) * 0.013f - 0.5f;
        ulong checksum = 0;
        uint seed = 0x2545F491u;
        int cursor = 0;

        var warmup = Stopwatch.StartNew();
        while (warmup.ElapsedMilliseconds < 150)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checksum += Quantum(table, chase, vectors, ref seed, ref cursor);
        }

        var times = new List<long>(8192);
        long measureEnd = Stopwatch.GetTimestamp() + (long)(measureSeconds * Stopwatch.Frequency);
        long start = Stopwatch.GetTimestamp();
        while (start < measureEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checksum += Quantum(table, chase, vectors, ref seed, ref cursor);
            long end = Stopwatch.GetTimestamp();
            times.Add(end - start);
            start = end;
        }
        GC.KeepAlive(checksum);
        if (times.Count < 20) throw new InvalidOperationException("The processor check could not complete enough work.");
        times.Sort();
        double fastTailSeconds = times[times.Count / 20] / (double)Stopwatch.Frequency;
        double quantaPerSecond = 1.0 / fastTailSeconds;
        return new CpuProbeResult(1000.0 * quantaPerSecond / ReferenceQuantaPerSecond, quantaPerSecond, total.ElapsedMilliseconds);
    }

    private static int[] BuildCycle(int length, uint seed)
    {
        int[] next = new int[length];
        for (int index = 0; index < length; index++) next[index] = index;
        for (int index = length - 1; index > 0; index--)
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            int swap = (int)(seed % (uint)index);
            (next[index], next[swap]) = (next[swap], next[index]);
        }
        return next;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static ulong Quantum(int[] table, int[] chase, float[] vectors, ref uint seed, ref int cursor)
    {
        ulong accumulator = 0;
        int mask = table.Length - 1;
        uint state = seed;
        for (int index = 0; index < HashOpsPerQuantum; index++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            int key = (int)(state & 0x7FFFF) | 1;
            int slot = (int)((uint)(key * -1640531535) >> (32 - TableBits));
            for (int probe = 0; probe < 8; probe++)
            {
                int value = table[slot];
                if (value == 0) { table[slot] = key; break; }
                if (value == key) { accumulator += (ulong)slot; break; }
                slot = (slot + 1) & mask;
            }
            if ((state & 0xFFF) == 0) table[(int)(state >> 20) & mask] = 0;
        }
        seed = state;
        int position = cursor;
        for (int index = 0; index < ChaseStepsPerQuantum; index++) position = chase[position];
        cursor = position;
        accumulator += (ulong)position;
        float m00 = 0.9f, m01 = 0.1f, m02 = -0.2f, m03 = 1f, m10 = -0.1f, m11 = 0.95f, m12 = 0.05f, m13 = 2f,
              m20 = 0.2f, m21 = -0.05f, m22 = 0.97f, m23 = 3f;
        float sum = 0;
        for (int index = 0; index < vectors.Length; index += 4)
        {
            float x = vectors[index], y = vectors[index + 1], z = vectors[index + 2];
            float tx = m00 * x + m01 * y + m02 * z + m03;
            float ty = m10 * x + m11 * y + m12 * z + m13;
            float tz = m20 * x + m21 * y + m22 * z + m23;
            float inverse = 1f / (1f + tx * tx + ty * ty + tz * tz);
            sum += (tx + ty + tz) * inverse;
        }
        accumulator += (ulong)BitConverter.SingleToInt32Bits(sum);
        return accumulator;
    }
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
            text = new UTF8Encoding(false, false).GetString(bytes).TrimStart('\uFEFF');
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
        + "#   mode=auto  the updater picks for this computer (it checks the processor and graphics card once)\r\n"
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

// The once-per-PC hardware record (%LOCALAPPDATA%\CobbleMusicUpdater\performance-detection.json).
internal static class MachinePerformanceStore
{
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
            return record is { SchemaVersion: 1, Gpus: not null, CpuName: not null } ? record : null;
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
            log($"The hardware check result could not be saved ({exception.GetType().Name}); it will run again next launch.");
        }
    }
}
