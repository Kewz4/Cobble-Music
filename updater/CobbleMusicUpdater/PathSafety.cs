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
        if (!normalizedRelativePath.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // MCBrowser persists per-user browser state here. It must never be
        // copied into another player's instance, even as a create-only file.
        return !string.Equals(
            normalizedRelativePath,
            "config/MCBrowser/tabs.json",
            StringComparison.OrdinalIgnoreCase);
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
