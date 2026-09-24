using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CobbleMusicUpdater;

// Updater 1.2.22 lite mode, round 2: a lite mod list is refused (nothing is switched off) when
//   * a listed jar is a critical mod or a library (compiled id list below, or Mod Menu's "library" badge), or
//   * switching the listed jars off would leave an ENABLED mod (top-level or nested) with a `depends` entry that no
//     enabled jar provides any more, although it was provided with the full pack. `recommends`/`suggests` only warn in
//     Fabric Loader and are not counted.
// The publisher runs the same rule over the release tree before signing (tools/CobbleMusicRelease.Core.psm1); the
// updater re-checks it on the PC against the jars it actually has, once per release and list.
internal static class LiteModGuard
{
    // Mods lite may never switch off. kewz_subtle_stub is the Subtle Effects join fix: a lite PC without it cannot join
    // the server once Subtle Effects is off (lite0924/subtle-stub).
    public static readonly IReadOnlySet<string> CriticalModIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "fabric-api", "fabric", "cobblemon", "sodium", "iris",
        "packed_packs", "kewz_subtle_stub"
    };

    // Mods the server cannot do without unless a stand-in is loaded instead: switching Subtle Effects off while the
    // join fix (kewz_subtle_stub) is not enabled would lock the PC out of the server (round-2 verifiers, FIX 1).
    public static readonly IReadOnlyDictionary<string, string> RequiredWhenOff = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["subtle_effects"] = "kewz_subtle_stub"
    };

    private const int MaximumNestingDepth = 4;
    private const long MaximumNestedJarBytes = 128L * 1024 * 1024;
    private const long MaximumModJsonBytes = 1024 * 1024;

    internal sealed record ModInfo(string Jar, string Id, IReadOnlyList<string> Provides, IReadOnlyList<string> Depends, bool Library, int Depth)
    {
        public IEnumerable<string> Ids => Provides.Prepend(Id);
    }

    // Every fabric.mod.json in a jar and in the jars it nests (Fabric "jars" list), recursively.
    public static List<ModInfo> ReadJar(string path, string label)
    {
        var result = new List<ModInfo>();
        using ZipArchive zip = ZipFile.OpenRead(path);
        Walk(zip, label, 0, result);
        return result;
    }

    private static void Walk(ZipArchive zip, string label, int depth, List<ModInfo> result)
    {
        ZipArchiveEntry? entry = zip.GetEntry("fabric.mod.json");
        if (entry is null) return;
        if (entry.Length > MaximumModJsonBytes) throw new InvalidDataException($"{label}: fabric.mod.json is unusually large");
        byte[] raw;
        using (Stream stream = entry.Open())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            raw = memory.ToArray();
        }
        using JsonDocument document = ParseLenient(raw, label);
        JsonElement root = document.RootElement;
        string id = root.TryGetProperty("id", out JsonElement idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() ?? "" : "";
        if (id.Length == 0) throw new InvalidDataException($"{label}: fabric.mod.json has no mod id");
        var provides = new List<string>();
        if (root.TryGetProperty("provides", out JsonElement providesValue) && providesValue.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in providesValue.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) provides.Add(item.GetString()!);
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out JsonElement providedId)
                    && providedId.ValueKind == JsonValueKind.String) provides.Add(providedId.GetString()!);
            }
        }
        var depends = new List<string>();
        if (root.TryGetProperty("depends", out JsonElement dependsValue))
        {
            if (dependsValue.ValueKind == JsonValueKind.Object)
                depends.AddRange(dependsValue.EnumerateObject().Select(property => property.Name));
            else if (dependsValue.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in dependsValue.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object) depends.AddRange(item.EnumerateObject().Select(property => property.Name));
                    else if (item.ValueKind == JsonValueKind.String) depends.Add(item.GetString()!);
                }
        }
        bool library = root.TryGetProperty("custom", out JsonElement custom) && custom.ValueKind == JsonValueKind.Object
            && custom.TryGetProperty("modmenu", out JsonElement modmenu) && modmenu.ValueKind == JsonValueKind.Object
            && modmenu.TryGetProperty("badges", out JsonElement badges) && badges.ValueKind == JsonValueKind.Array
            && badges.EnumerateArray().Any(badge => badge.ValueKind == JsonValueKind.String && badge.GetString() == "library");
        result.Add(new ModInfo(label, id, provides, depends, library, depth));

        if (!root.TryGetProperty("jars", out JsonElement jars) || jars.ValueKind != JsonValueKind.Array) return;
        if (depth >= MaximumNestingDepth) throw new InvalidDataException($"{label}: jars are nested too deeply");
        foreach (JsonElement jar in jars.EnumerateArray())
        {
            if (jar.ValueKind != JsonValueKind.Object || !jar.TryGetProperty("file", out JsonElement file) || file.ValueKind != JsonValueKind.String)
                continue;
            string name = file.GetString()!;
            ZipArchiveEntry? nested = zip.GetEntry(name)
                ?? throw new InvalidDataException($"{label}: nested jar {name} is listed but missing");
            if (nested.Length > MaximumNestedJarBytes) throw new InvalidDataException($"{label}: nested jar {name} is unusually large");
            using var memory = new MemoryStream();
            using (Stream stream = nested.Open()) stream.CopyTo(memory);
            memory.Position = 0;
            using var inner = new ZipArchive(memory, ZipArchiveMode.Read);
            Walk(inner, label, depth + 1, result);
        }
    }

    // Fabric Loader reads fabric.mod.json leniently; several shipped mods (for example EG Particle Interactions) have raw
    // line breaks inside strings. Control characters inside string literals become spaces; comments and trailing commas
    // are allowed. Nothing outside strings changes.
    internal static JsonDocument ParseLenient(byte[] raw, string label)
    {
        string text = new UTF8Encoding(false, false).GetString(raw).TrimStart('﻿');
        var cleaned = new StringBuilder(text.Length);
        bool inString = false, escaped = false;
        foreach (char character in text)
        {
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                cleaned.Append(character < ' ' ? ' ' : character);
                continue;
            }
            if (character == '"') inString = true;
            cleaned.Append(character);
        }
        try
        {
            return JsonDocument.Parse(cleaned.ToString(), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label}: fabric.mod.json could not be read ({exception.Message})", exception);
        }
    }

    // Returns the reasons the list is refused (empty = the list is safe). `enabled` are the jars that stay loaded,
    // `off` the jars lite would switch off; each is (label, path on disk).
    public static List<string> Check(IReadOnlyList<(string Label, string Path)> enabled, IReadOnlyList<(string Label, string Path)> off)
    {
        var problems = new List<string>();
        var enabledMods = new List<ModInfo>();
        var offMods = new List<ModInfo>();
        foreach ((string label, string path) in enabled)
        {
            try { enabledMods.AddRange(ReadJar(path, label)); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{label} could not be read ({exception.Message}), so its dependencies cannot be checked");
            }
        }
        foreach ((string label, string path) in off)
        {
            List<ModInfo> mods;
            try { mods = ReadJar(path, label); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{label} could not be read ({exception.Message})");
                continue;
            }
            if (mods.Count == 0)
            {
                problems.Add($"{label} is not a Fabric mod");
                continue;
            }
            ModInfo top = mods[0];
            string[] critical = top.Ids.Where(CriticalModIds.Contains).ToArray();
            if (critical.Length > 0) problems.Add($"{label} is a mod lite may never switch off ({string.Join(", ", critical)})");
            if (top.Library) problems.Add($"{label} ({top.Id}) is a library");
            offMods.AddRange(mods);
        }
        var before = enabledMods.Concat(offMods).SelectMany(mod => mod.Ids).ToHashSet(StringComparer.Ordinal);
        var after = enabledMods.SelectMany(mod => mod.Ids).ToHashSet(StringComparer.Ordinal);
        foreach (ModInfo mod in offMods.Where(item => item.Depth == 0))
        {
            foreach (string id in mod.Ids.Distinct(StringComparer.Ordinal))
            {
                if (RequiredWhenOff.TryGetValue(id, out string? standIn) && !after.Contains(standIn))
                    problems.Add($"{mod.Jar} ({id}) may only be switched off while {standIn} is on; without it this PC could not join the server");
            }
        }
        foreach (ModInfo mod in enabledMods)
        {
            foreach (string dependency in mod.Depends.Distinct(StringComparer.Ordinal))
            {
                if (before.Contains(dependency) && !after.Contains(dependency))
                {
                    string owner = string.Join(", ", offMods.Where(item => item.Ids.Contains(dependency, StringComparer.Ordinal))
                        .Select(item => item.Jar).Distinct(StringComparer.Ordinal));
                    problems.Add($"{mod.Jar} ({mod.Id}{(mod.Depth > 0 ? ", nested" : "")}) depends on {dependency}, which only {owner} provides");
                }
            }
        }
        return problems;
    }
}
