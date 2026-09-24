using System.Text.Json;

namespace CobbleMusicUpdater;

// Validation of the signed performanceProfiles section (updater 1.2.22). Everything here fails closed: a
// manifest with an unsafe or malformed lite profile is rejected as a whole, exactly like any other invalid
// signed manifest, so a bad list can never half-apply.
internal static class PerformanceProfilePolicy
{
    internal static readonly Version MinimumUpdater = new(1, 2, 22);
    private const int MaximumModEntries = 200;
    private const int MaximumPackEntries = 500;
    private const int MaximumSettingEntries = 200;
    private const int MaximumGpuPatterns = 400;

    public static void Validate(
        UpdateManifest manifest,
        IReadOnlyDictionary<string, ManifestFile> files,
        IReadOnlyDictionary<string, ManifestFile> seedFiles,
        Version requiredUpdater)
    {
        LitePerformanceProfile? lite = manifest.PerformanceProfiles?.Lite;
        if (manifest.PerformanceProfiles is null)
        {
            return;
        }
        if (requiredUpdater < MinimumUpdater)
        {
            throw new InvalidDataException("Signed performance profiles require updater 1.2.22 or newer.");
        }
        if (lite is null)
        {
            return;
        }
        if (!PathSafety.IsPlayerSettingMigrationIdAllowed(lite.RevisionId)
            || lite.Detection is null
            || lite.DisabledMods is null
            || lite.RemovedPackIds is null
            || lite.Settings is null)
        {
            throw new InvalidDataException("Signed lite profile has an invalid revision id or a missing list.");
        }
        ValidateDetection(lite.Detection);

        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lite.DisabledMods.Count > MaximumModEntries)
        {
            throw new InvalidDataException("Signed lite profile lists too many mods.");
        }
        for (int index = 0; index < lite.DisabledMods.Count; index++)
        {
            string normalized = PathSafety.NormalizeRelativePath(lite.DisabledMods[index] ?? "");
            lite.DisabledMods[index] = normalized;
            if (!PathSafety.IsLiteModPath(normalized)
                || !files.TryGetValue(normalized, out ManifestFile? managed)
                || !string.Equals(managed.Path, normalized, StringComparison.Ordinal)
                || !mods.Add(normalized))
            {
                throw new InvalidDataException($"Signed lite profile names a mod that is not a managed top-level jar of this release: {normalized}");
            }
        }

        var packIds = new HashSet<string>(StringComparer.Ordinal);
        if (lite.RemovedPackIds.Count > MaximumPackEntries)
        {
            throw new InvalidDataException("Signed lite profile lists too many resource packs.");
        }
        foreach (string packId in lite.RemovedPackIds)
        {
            if (string.IsNullOrWhiteSpace(packId)
                || packId.Length > 256
                || packId != packId.Trim()
                || packId.Any(char.IsControl)
                || packId is "vanilla" or "fabric"
                || !packIds.Add(packId))
            {
                throw new InvalidDataException($"Signed lite profile has an invalid or duplicate resource pack id: {packId}");
            }
        }

        var settingKeys = new HashSet<string>(StringComparer.Ordinal);
        if (lite.Settings.Count > MaximumSettingEntries)
        {
            throw new InvalidDataException("Signed lite profile lists too many settings.");
        }
        foreach (PerformanceSetting setting in lite.Settings)
        {
            if (setting is null)
            {
                throw new InvalidDataException("Signed lite profile contains an empty setting.");
            }
            setting.Path = PathSafety.NormalizeRelativePath(setting.Path ?? "");
            ValidateSetting(setting, files, seedFiles);
            if (!settingKeys.Add(setting.Path.ToUpperInvariant() + "\0" + setting.Key))
            {
                throw new InvalidDataException($"Signed lite profile sets the same setting twice: {setting.Path} {setting.Key}");
            }
        }
    }

    internal static void ValidateSetting(
        PerformanceSetting setting,
        IReadOnlyDictionary<string, ManifestFile>? files,
        IReadOnlyDictionary<string, ManifestFile>? seedFiles)
    {
        string? allowedFormat = PathSafety.PerformanceSettingFormat(setting.Path);
        if (PathSafety.IsShaderSettingPath(setting.Path))
        {
            throw new InvalidDataException($"Lite never changes shader settings: {setting.Path}");
        }
        if (allowedFormat is null || !string.Equals(allowedFormat, setting.Format, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Signed lite profile setting targets a file or format that is not allow-listed: {setting.Path} ({setting.Format})");
        }
        // Only player-owned create-only defaults: a managed file would be repaired back by convergence, and a
        // file this release does not declare at all is not a pack setting.
        if ((files is not null && files.ContainsKey(setting.Path))
            || (seedFiles is not null && !seedFiles.ContainsKey(setting.Path)))
        {
            throw new InvalidDataException($"Signed lite profile setting must target a player-owned default of this release: {setting.Path}");
        }
        if (!IsSafeValueText(setting.Value))
        {
            throw new InvalidDataException($"Signed lite profile setting has an unsafe value: {setting.Path} {setting.Key}");
        }
        bool validKey = setting.Format switch
        {
            "json" => IsValidJsonKeyPath(setting.Key) && IsJsonScalarLiteral(setting.Value),
            "options" => PathSafety.IsPerformanceOptionsKeyAllowed(setting.Key),
            "properties" => IsValidPropertiesKey(setting.Key) && !setting.Value.StartsWith(' ') && !setting.Value.EndsWith(' '),
            _ => false
        };
        if (!validKey)
        {
            throw new InvalidDataException($"Signed lite profile setting has an invalid key or value: {setting.Path} {setting.Key}");
        }
    }

    private static void ValidateDetection(PerformanceDetectionRules detection)
    {
        if (detection.CpuScoreThreshold is < 1 or > 100_000
            || detection.FullGpuPatterns is null
            || detection.LiteGpuPatterns is null
            || detection.FullGpuPatterns.Count + detection.LiteGpuPatterns.Count > MaximumGpuPatterns)
        {
            throw new InvalidDataException("Signed lite profile has invalid detection rules.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string pattern in detection.FullGpuPatterns.Concat(detection.LiteGpuPatterns))
        {
            if (GpuTierList.Tokenize(pattern ?? "").Count == 0
                || pattern!.Length > 64
                || pattern.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is ' ' or '#' or '-')))
            {
                throw new InvalidDataException($"Signed lite profile has an invalid graphics card pattern: {pattern}");
            }
            if (!seen.Add(string.Join(' ', GpuTierList.Tokenize(pattern))))
            {
                throw new InvalidDataException($"Signed lite profile lists a graphics card pattern twice: {pattern}");
            }
        }
    }

    private static bool IsSafeValueText(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);

    internal static bool IsValidJsonKeyPath(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 256) return false;
        string[] segments = key.Split('.');
        return segments.Length <= 8
            && segments.All(segment => segment.Length > 0
                && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'));
    }

    private static bool IsValidPropertiesKey(string key) =>
        !string.IsNullOrEmpty(key)
        && key.Length <= 128
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')
        && !key.Contains("shader", StringComparison.OrdinalIgnoreCase);

    internal static bool IsJsonScalarLiteral(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is JsonValueKind.String or JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
