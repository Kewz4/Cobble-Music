using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CobbleMusicUpdater;

// Updater 1.2.22 lite mode, round 2 (Kewz, 2026-09-24: "build an entire list of cpus/gpus with score").
// The lite decision no longer measures anything at run time: it looks the processor and graphics cards up in two
// embedded tables (PassMark single-thread rating for processors, PassMark G3D Mark for graphics cards; built by
// workspace/shared/lite0924/hwdb/build_hwdb.py). Names are matched through ONE normaliser, "hwdb-norm-1", applied
// identically to the PassMark names at build time and to the Windows strings here, followed by an EXACT lookup with no
// fuzzy fallback. This file is a step-for-step port of hwdb/hwdb_norm.py; the test vectors in
// CobbleMusicUpdater.Tests/Fixtures/Hwdb pin it to the Python reference.
internal enum HardwareMatchStatus
{
    Unknown,
    Known,
    Ignore
}

internal sealed record HardwareMatch(HardwareMatchStatus Status, string? Key, int? Score, string Rule, bool Ambiguous)
{
    public static HardwareMatch Unknown(string? key, string rule) => new(HardwareMatchStatus.Unknown, key, null, rule, false);
    public static HardwareMatch Ignore(string rule) => new(HardwareMatchStatus.Ignore, null, null, rule, false);
}

internal static class HardwareNames
{
    public const string NormaliserId = "hwdb-norm-1";

    // PCI vendors whose adapters are graphics hardware: NVIDIA, AMD (graphics), AMD (1022, the processor vendor id some
    // AMD integrated parts carry) and Intel. The Python reference allows 10de/1002/8086; 1022 is the updater's own
    // addition (task rule, 2026-09-24) and behaves exactly like 1002.
    public static readonly IReadOnlySet<string> GraphicsVendors = new HashSet<string>(StringComparer.Ordinal) { "10de", "1002", "1022", "8086" };

    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private static readonly Regex TradeMarks = new(@"\((?:r|tm|c)\)|[®™©]", Options | RegexOptions.IgnoreCase);
    private static readonly Regex Whitespace = new(@"\s+", Options);

    private static readonly (Regex Pattern, string Replacement)[] CpuSteps =
    [
        (new Regex(@"\b\d{1,2}(?:st|nd|rd|th) gen\b", Options), " "),
        (new Regex(@"@\s*[\d.]+\s*[gm]hz", Options), " "),
        (new Regex(@"\s(?:with|w/)\s+radeon\b.*$", Options), " "),
        (new Regex(@"\s+radeon r\d+\b.*$", Options), " "),
        (new Regex(@",?\s*\d+\s+compute cores.*$", Options), " "),
        (new Regex(@"\b(?:dual|triple|quad|six|eight|ten|twelve|sixteen|\d+)[- ]cores?\b", Options), " "),
        (new Regex(@"\b(?:intel|amd|cpu|processor|apu)\b", Options), " "),
        (new Regex(@"(?<=\s)0(?=\s|$)", Options), " "),
        (new Regex(@"\s-\s*qualcomm.*$", Options), " ")
    ];

    private static readonly Regex Nehalem = new(@"^core (i[3579]) (\d{3}[a-z]*)$", Options);

    private static readonly (Regex Pattern, string Replacement)[] GpuSteps =
    [
        (new Regex(@"\bradeont\b", Options), "radeon"),
        (new Regex(@"\bwith max-q design\b", Options), " max-q "),
        (new Regex(@"\blaptop gpu\b", Options), " laptop "),
        (new Regex(@"\(mobile\)", Options), " mobile "),
        (new Regex(@"\((\d+)\s*gb\)", Options), " ${1}gb "),
        (new Regex(@"\b(\d+)\s+gb\b", Options), "${1}gb"),
        (new Regex(@"\b(?:nvidia|amd|ati|intel)\b", Options), " "),
        (new Regex(@"\b(?:graphics|series|gpu|family|adapter)\b", Options), " ")
    ];

    internal static readonly Regex MemoryToken = new(@"(?:^| )(\d+)gb(?= |$)", Options);
    private static readonly Regex AmdIntegrated = new(@"^radeon(?: rx)?(?: vega(?: \d+)?| \d{3}m|)$", Options);
    private static readonly Regex PciId = new(@"ven_([0-9a-f]{4}).*?dev_([0-9a-f]{4})", Options);
    internal static readonly string[] MobileTokens = ["laptop", "mobile", "max-q"];

    // Adapters that are not graphics hardware (virtual, remote, indirect, USB display), matched on the RAW DriverDesc,
    // case-insensitive substring. Same list as VIRTUAL_ADAPTER_FRAGMENTS in hwdb_norm.py.
    internal static readonly string[] VirtualAdapterFragments =
    [
        "microsoft basic", "basicdisplay", "basic render", "remote display", "remotefx", "hyper-v",
        "virtual", "vmware", "virtualbox", "parallels", "citrix", "idd", "indirect display", "spacedesk",
        "displaylink", "parsec", "splashtop", "anydesk", "anyviewer", "oray", "sunlogin", "teamviewer",
        "luminon", "duet", "mirage", "mirror driver", "usb display", "usb device", "meta ", "oculus",
        "streaming", "sudomaker", "gameviewer", "rdp", "qxl", "red hat", "cirrus",
        "usb", "miracast", "mirror", "video hook", "framebuffer", "turzx", "cxdisplay"
    ];

    private static string Base(string? value)
    {
        string text = (value ?? "").Normalize(NormalizationForm.FormKC);
        text = TradeMarks.Replace(text, "");
        return text.ToLowerInvariant();
    }

    internal static string Squash(string value) => Whitespace.Replace(value, " ").Trim();

    internal static bool IsAmdIntegratedKey(string key) => AmdIntegrated.IsMatch(key);

    public static string CpuKey(string? name)
    {
        string text = " " + Base(name) + " ";
        foreach ((Regex pattern, string replacement) in CpuSteps) text = pattern.Replace(text, replacement);
        text = Squash(text);
        return Nehalem.Replace(text, "core $1-$2");
    }

    public static string GpuKey(string? name)
    {
        string text = " " + Base(name) + " ";
        foreach ((Regex pattern, string replacement) in GpuSteps) text = pattern.Replace(text, replacement);
        text = Squash(text);
        return text.Length != 0 ? text : Squash(Base(name));
    }

    public static bool IsVirtualAdapter(string? description)
    {
        string lower = (description ?? "").ToLowerInvariant();
        return VirtualAdapterFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal));
    }

    // ("10de", "1e91") from "pci\ven_10de&dev_1e91&subsys_...", else (null, null). Like the reference, it does not
    // require the "pci\" prefix; callers that need real PCI hardware check the prefix themselves.
    public static (string? Vendor, string? Device) ParsePciId(string? deviceId)
    {
        Match match = PciId.Match((deviceId ?? "").ToLowerInvariant());
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : (null, null);
    }
}

internal sealed class HardwareDb
{
    private readonly Dictionary<string, int> _cpuScores;
    private readonly HashSet<string> _cpuAmbiguous;
    private readonly Dictionary<string, int> _gpuScores;
    // base key -> [(memory GiB, key)] in document order, for "base <N>gb" rows (the reference's _variants).
    private readonly Dictionary<string, List<(int Gib, string Key)>> _gpuVariants;
    private readonly int? _derivedRadeon;
    private readonly HashSet<string> _nvidiaMobileDeviceIds;

    public string Version { get; }
    public int CpuCount => _cpuScores.Count;
    public int GpuCount => _gpuScores.Count;

    private static readonly Lazy<HardwareDb> EmbeddedDb = new(() => Load(
        ReadResource("HardwareDb.cpu_scores.json"), ReadResource("HardwareDb.gpu_scores.json")));

    public static HardwareDb Embedded => EmbeddedDb.Value;

    private HardwareDb(Dictionary<string, int> cpuScores, HashSet<string> cpuAmbiguous, Dictionary<string, int> gpuScores,
        Dictionary<string, List<(int, string)>> gpuVariants, int? derivedRadeon, HashSet<string> nvidiaMobile, string version)
    {
        _cpuScores = cpuScores;
        _cpuAmbiguous = cpuAmbiguous;
        _gpuScores = gpuScores;
        _gpuVariants = gpuVariants;
        _derivedRadeon = derivedRadeon;
        _nvidiaMobileDeviceIds = nvidiaMobile;
        Version = version;
    }

    private static byte[] ReadResource(string name)
    {
        using Stream stream = typeof(HardwareDb).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"The built-in hardware list {name} is missing from the updater.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static HardwareDb Load(byte[] cpuJson, byte[] gpuJson)
    {
        using JsonDocument cpu = JsonDocument.Parse(cpuJson);
        using JsonDocument gpu = JsonDocument.Parse(gpuJson);
        CheckHeader(cpu.RootElement, "cpu");
        CheckHeader(gpu.RootElement, "gpu");
        var cpuScores = ReadScores(cpu.RootElement, out _);
        var cpuAmbiguous = ReadStrings(cpu.RootElement, "ambiguous");
        var gpuScores = ReadScores(gpu.RootElement, out List<string> gpuOrder);
        var variants = new Dictionary<string, List<(int, string)>>(StringComparer.Ordinal);
        var variantKey = new Regex(@"^(.+) (\d+)gb$", RegexOptions.CultureInvariant);
        foreach (string key in gpuOrder)
        {
            Match match = variantKey.Match(key);
            if (!match.Success) continue;
            string baseKey = match.Groups[1].Value;
            if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int gib)) continue;
            if (!variants.TryGetValue(baseKey, out List<(int, string)>? list)) variants[baseKey] = list = [];
            list.Add((gib, key));
        }
        int? derivedRadeon = gpu.RootElement.TryGetProperty("derived", out JsonElement derived)
            && derived.TryGetProperty("radeon", out JsonElement radeon) && radeon.TryGetInt32(out int value) ? value : null;
        var mobile = ReadStrings(gpu.RootElement, "nvidiaMobileDeviceIds");
        byte[] hash = SHA256.HashData([.. cpuJson, .. gpuJson]);
        string fetched = cpu.RootElement.TryGetProperty("fetched", out JsonElement date) ? date.GetString() ?? "" : "";
        string version = $"{HardwareNames.NormaliserId}/{fetched}/{Convert.ToHexString(hash, 0, 6).ToLowerInvariant()}";
        return new HardwareDb(cpuScores, cpuAmbiguous, gpuScores, variants, derivedRadeon,
            mobile.Select(id => id.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal), version);
    }

    private static void CheckHeader(JsonElement root, string kind)
    {
        if (!root.TryGetProperty("schema", out JsonElement schema) || schema.GetInt32() != 1
            || root.GetProperty("kind").GetString() != kind
            || root.GetProperty("normaliser").GetString() != HardwareNames.NormaliserId)
        {
            throw new InvalidDataException($"The built-in {kind} list is not a {HardwareNames.NormaliserId} schema 1 table.");
        }
    }

    private static Dictionary<string, int> ReadScores(JsonElement root, out List<string> order)
    {
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        order = [];
        foreach (JsonProperty property in root.GetProperty("scores").EnumerateObject())
        {
            scores[property.Name] = property.Value.GetInt32();
            order.Add(property.Name);
        }
        return scores;
    }

    private static HashSet<string> ReadStrings(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement array)
            ? array.EnumerateArray().Select(item => item.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    public HardwareMatch ResolveCpu(string? name)
    {
        string key = HardwareNames.CpuKey(name);
        return _cpuScores.TryGetValue(key, out int score)
            ? new HardwareMatch(HardwareMatchStatus.Known, key, score, "exact", _cpuAmbiguous.Contains(key))
            : HardwareMatch.Unknown(key, "no-entry");
    }

    // One display adapter -> a table entry. First rule that applies wins (NORMALISER.md, "GPU resolution").
    public HardwareMatch ResolveGpu(string? description, string? matchingDeviceId, long? memoryBytes, string? cpuName)
    {
        (string? vendor, string? device) = HardwareNames.ParsePciId(matchingDeviceId);
        bool hasDeviceId = !string.IsNullOrEmpty(matchingDeviceId);
        if (HardwareNames.IsVirtualAdapter(description)
            || (hasDeviceId && vendor is not null && !HardwareNames.GraphicsVendors.Contains(vendor)))
        {
            return HardwareMatch.Ignore("virtual-or-non-gpu-vendor");
        }
        if (hasDeviceId && vendor is null) return HardwareMatch.Ignore("non-pci-device");
        string key = HardwareNames.GpuKey(description);
        if (key.Length == 0) return HardwareMatch.Unknown(null, "empty-key");

        HardwareMatch Hit(string hitKey, string rule, bool ambiguous = false) =>
            new(HardwareMatchStatus.Known, hitKey, _gpuScores[hitKey], rule, ambiguous);

        // 1. AMD integrated graphics: PassMark files them under the paired processor.
        if (HardwareNames.IsAmdIntegratedKey(key) && (vendor is null or "1002" or "1022"))
        {
            string? cpuKey = cpuName is null ? null : HardwareNames.CpuKey(cpuName);
            if (!string.IsNullOrEmpty(cpuKey) && _gpuScores.ContainsKey(key + "@" + cpuKey)) return Hit(key + "@" + cpuKey, "amd-igpu-by-cpu");
            if (_gpuScores.ContainsKey(key)) return Hit(key, "exact");
            if (key == "radeon" && _derivedRadeon is int generic)
                return new HardwareMatch(HardwareMatchStatus.Known, "radeon", generic, "amd-igpu-generic-max", true);
        }

        // 2. NVIDIA laptop chip reported under the desktop name (Kewz's RTX 2070 Super on device 1E91).
        bool mobileChip = vendor == "10de" && device is not null && _nvidiaMobileDeviceIds.Contains(device);
        if (mobileChip && !key.Split(' ').Any(token => HardwareNames.MobileTokens.Contains(token)))
        {
            string? lowest = null;
            foreach (string token in HardwareNames.MobileTokens)
            {
                string candidate = key + " " + token;
                if (_gpuScores.TryGetValue(candidate, out int score) && (lowest is null || score < _gpuScores[lowest])) lowest = candidate;
            }
            if (lowest is not null) return Hit(lowest, "nvidia-mobile-by-device-id", true);
            if (_gpuScores.ContainsKey(key)) return Hit(key, "nvidia-mobile-no-laptop-entry-desktop-score", true);
        }

        // 3. Exact; a size-split card is refined by the adapter memory.
        if (_gpuScores.ContainsKey(key))
        {
            if (memoryBytes is > 0 && !HardwareNames.MemoryToken.IsMatch(key)
                && _gpuVariants.TryGetValue(key, out List<(int Gib, string Key)>? sized))
            {
                int gib = RoundGib(memoryBytes.Value);
                foreach ((int size, string sizedKey) in sized)
                    if (size == gib) return Hit(sizedKey, "memory-variant");
            }
            return Hit(key, "exact");
        }

        // 4. Memory-size variants (PassMark only has "RTX 3050 6GB" and "RTX 3050 8GB", never a plain "RTX 3050").
        Match memoryToken = HardwareNames.MemoryToken.Match(key);
        string baseKey = memoryToken.Success ? HardwareNames.Squash(HardwareNames.MemoryToken.Replace(key, " ")) : key;
        if (memoryToken.Success)
        {
            if (_gpuScores.ContainsKey(baseKey)) return Hit(baseKey, "memory-token-dropped");
        }
        else if (_gpuVariants.TryGetValue(baseKey, out List<(int Gib, string Key)>? variants) && variants.Count > 0)
        {
            if (memoryBytes is > 0)
            {
                int gib = RoundGib(memoryBytes.Value);
                foreach ((int size, string sizedKey) in variants)
                    if (size == gib) return Hit(sizedKey, "memory-variant");
            }
            string lowest = variants[0].Key;
            foreach ((_, string sizedKey) in variants)
                if (_gpuScores[sizedKey] < _gpuScores[lowest]) lowest = sizedKey;
            return Hit(lowest, "memory-variant-lower", true);
        }
        return HardwareMatch.Unknown(key, "no-entry");
    }

    // Python round(): half to even, which is also Math.Round's default.
    private static int RoundGib(long bytes) => (int)Math.Round(bytes / (double)(1L << 30));
}
