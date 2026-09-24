using System.Security.Cryptography;

namespace CobbleMusicUpdater;

internal static class PathSafety
{
    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("An update path cannot be empty.");
        }

        string normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || Path.IsPathRooted(normalized)
            || normalized.Contains(':'))
        {
            throw new InvalidDataException($"Update path is not relative: {path}");
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
        {
            throw new InvalidDataException($"Unsafe update path: {path}");
        }

        return string.Join('/', segments);
    }

    public static bool IsAllowed(string normalizedRelativePath, IReadOnlyCollection<string> allowedRoots)
    {
        string root = normalizedRelativePath.Split('/', 2)[0];
        return allowedRoots.Contains(root, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsSeedAllowed(string normalizedRelativePath)
    {
        if (string.Equals(normalizedRelativePath, "options.txt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (IsOptionalPlayerMod(normalizedRelativePath))
        {
            return true;
        }
        if (normalizedRelativePath.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase))
        {
            string fileName = normalizedRelativePath["shaderpacks/".Length..];
            return !fileName.Contains('/')
                && fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }
        if (!normalizedRelativePath.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Browser/account state, generated caches/fingerprints, private keys,
        // and editor backups must never be copied into another player's
        // instance, even as create-only defaults.
        return !IsNeverDistributableSeed(normalizedRelativePath);
    }

    public static bool IsSeedTextReplacementAllowed(string normalizedRelativePath) =>
        string.Equals(normalizedRelativePath, "config/iris.properties", StringComparison.OrdinalIgnoreCase)
        || string.Equals(normalizedRelativePath, "options.txt", StringComparison.OrdinalIgnoreCase);

    public static bool IsPlayerSettingMigrationIdAllowed(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 128
            || !IsLowerAlphaNumeric(value[0]))
        {
            return false;
        }
        return value.All(character =>
            IsLowerAlphaNumeric(character) || character is '.' or '_' or '-');
    }

    private static bool IsLowerAlphaNumeric(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsNeverDistributableSeed(string path)
    {
        string[] segments = path.Split('/');
        string name = segments[^1];
        return path.Equals("config/MCBrowser/tabs.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/packed_packs/__version.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/dreamdisplays/config.toml", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/dreamdisplays/config.yml", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/cobbreeding/encryption", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/jade/usernamecache.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/zoomify.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/etf_warnings.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/sodium-fingerprint.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/spark/activity.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/spark/tmp/about.txt", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config/spark/tmp-client/about.txt", StringComparison.OrdinalIgnoreCase)
            || segments.Any(segment => segment.Equals("cache", StringComparison.OrdinalIgnoreCase))
            || IsBackupName(name)
            || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".ds_store", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBackupName(string name)
    {
        if (name.EndsWith('~'))
        {
            return true;
        }
        foreach (string marker in new[] { ".bak", ".old" })
        {
            int markerIndex = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }
            string suffix = name[(markerIndex + marker.Length)..];
            if (suffix.Length == 0
                || suffix.All(char.IsDigit)
                || suffix[0] is '-' or '.' or '_')
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsOptionalPlayerMod(string normalizedRelativePath)
    {
        if (normalizedRelativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
        {
            string fileName = normalizedRelativePath["mods/".Length..];
            return !fileName.Contains('/')
                && fileName.StartsWith("axiom", StringComparison.OrdinalIgnoreCase)
                && (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    // ---- 1.2.22 lite mode: the only files a performance profile may ever edit --------------------------------
    // Compiled in, so a signed manifest can choose keys and values but never widen the set of files. Every file
    // here is a player-owned setting of a non-shader mod. Shaderpack options, Iris settings and the shader
    // profiles are deliberately absent and are also rejected by IsShaderSettingPath (Kewz, 2026-09-23: "dont
    // worry about complementary shaders, we already got the best settings").
    private static readonly Dictionary<string, string> PerformanceSettingTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["options.txt"] = "options",
        ["config/sodium-options.json"] = "json",
        ["config/sodium-extra-options.json"] = "json",
        ["config/voxy-config.json"] = "json",
        ["config/vss-client-config.json"] = "json",
        ["config/astoutline.json"] = "json",
        ["config/asyncparticles/asyncparticles.json"] = "json",
        ["config/gnetum.json"] = "json",
        ["config/xaero/world-map/profiles/cobbleverse.cfg"] = "properties",
        ["config/xaero/minimap/profiles/cobbleverse.cfg"] = "properties"
    };

    public const string PerformanceLedgerPath = "cobble-music-updater/performance-state.json";

    public static string? PerformanceSettingFormat(string normalizedRelativePath) =>
        !IsShaderSettingPath(normalizedRelativePath)
            && PerformanceSettingTargets.TryGetValue(normalizedRelativePath, out string? format) ? format : null;

    public static bool IsShaderSettingPath(string normalizedRelativePath)
    {
        string path = normalizedRelativePath.Replace('\\', '/');
        string name = path.Split('/')[^1];
        return path.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("shader", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("iris", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("oculus", StringComparison.OrdinalIgnoreCase);
    }

    // options.txt keys a performance profile may never edit: the pack list (Packed Packs owns it), keybinds,
    // and anything shader-related.
    public static bool IsPerformanceOptionsKeyAllowed(string key) =>
        !string.IsNullOrEmpty(key)
        && key.Length <= 128
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')
        && !key.StartsWith("key_", StringComparison.Ordinal)
        && !key.Equals("resourcePacks", StringComparison.Ordinal)
        && !key.Equals("incompatibleResourcePacks", StringComparison.Ordinal)
        && !key.Equals("version", StringComparison.Ordinal)
        && !key.Contains("shader", StringComparison.OrdinalIgnoreCase);

    public static bool IsLiteModPath(string normalizedRelativePath)
    {
        if (!normalizedRelativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) return false;
        string fileName = normalizedRelativePath["mods/".Length..];
        return fileName.Length > ".jar".Length
            && !fileName.Contains('/')
            && fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            && !IsOptionalPlayerMod(normalizedRelativePath)
            && !IsNeverDisabledModPath(normalizedRelativePath);
    }

    // Managed mods lite may never switch off, by file name (the mod id is checked too, by LiteModGuard and the
    // publisher). The Subtle Effects join fix must be on every PC: without it a lite PC cannot join the server.
    public static bool IsNeverDisabledModPath(string normalizedRelativePath) =>
        normalizedRelativePath.StartsWith("mods/kewz-subtle-effects-stub", StringComparison.OrdinalIgnoreCase);

    public static bool IsOfficialPackProfilePath(string normalizedRelativePath) =>
        normalizedRelativePath.Equals("config/packed_packs/profiles/resourcepacks/Default.profile.json", StringComparison.OrdinalIgnoreCase)
        || normalizedRelativePath.Equals("config/packed_packs/profiles/resourcepacks/Realistic.profile.json", StringComparison.OrdinalIgnoreCase);

    // Files a performance transaction may create, replace or delete (recovery validates journals against this).
    public static bool IsPerformanceOutcomeAllowed(string normalizedRelativePath)
    {
        if (normalizedRelativePath.Equals(PerformanceLedgerPath, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsOfficialPackProfilePath(normalizedRelativePath)) return true;
        if (PerformanceSettingFormat(normalizedRelativePath) is not null) return true;
        if (normalizedRelativePath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            return IsLiteModPath(normalizedRelativePath[..^".disabled".Length]);
        return IsLiteModPath(normalizedRelativePath);
    }

    public static string CombineUnder(string root, string normalizedRelativePath)
    {
        string rootFullPath = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(rootFullPath, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullPath
            : rootFullPath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path escapes its allowed root: {normalizedRelativePath}");
        }
        return candidate;
    }

    public static bool TryGetRelativePathUnder(string root, string candidate, out string normalizedRelativePath)
    {
        normalizedRelativePath = "";
        try
        {
            string rootFullPath = Path.GetFullPath(root);
            string candidateFullPath = Path.GetFullPath(candidate);
            string relative = Path.GetRelativePath(rootFullPath, candidateFullPath);
            if (relative is "." or "" || Path.IsPathRooted(relative))
            {
                return false;
            }
            normalizedRelativePath = NormalizeRelativePath(relative);
            return string.Equals(CombineUnder(rootFullPath, normalizedRelativePath), candidateFullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public static void AssertNoReparsePointsOnTargetPath(string root, string target)
    {
        if (!TryGetRelativePathUnder(root, target, out string relativePath))
        {
            throw new InvalidDataException($"Target is outside its approved root: {target}");
        }

        string current = Path.GetFullPath(root);
        AssertNotReparsePoint(current);
        string[] segments = relativePath.Split('/');
        for (int index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (Directory.Exists(current))
            {
                AssertNotReparsePoint(current);
            }
        }
        if (File.Exists(target))
        {
            AssertNotReparsePoint(target);
        }
    }

    private static void AssertNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Refusing to follow a junction or symbolic link while updating: {path}");
        }
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        using SHA256 sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsExpectedHash(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}
