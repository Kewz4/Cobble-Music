using System.Text.Json;

namespace CobbleMusicUpdater;

internal static class ManifestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static UpdateManifest VerifyAndParse(byte[] rawManifest, byte[] rawSignature)
    {
        DetachedSignature? detachedSignature = JsonSerializer.Deserialize<DetachedSignature>(rawSignature, JsonOptions);
        if (detachedSignature is null || !ManifestSecurity.Verify(rawManifest, detachedSignature))
        {
            throw new InvalidDataException("GitHub release manifest signature is invalid. No local files were changed.");
        }

        UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(rawManifest, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidDataException("Signed release manifest is empty.");
        }
        return manifest;
    }

    public static void Validate(UpdateManifest manifest, UpdaterConfiguration configuration, IReadOnlyDictionary<string, Uri> assetUrls)
    {
        if (manifest.SchemaVersion != 1
            || !string.Equals(manifest.ModpackId, configuration.ModpackId, StringComparison.Ordinal)
            || !string.Equals(manifest.Channel, configuration.Channel, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.ReleaseTag)
            || manifest.Payload is null
            || manifest.Payload.Parts is null
            || manifest.Files is null
            || manifest.DeletePaths is null
            || manifest.LegacyCleanup is null
            || manifest.Payload.Parts.Count == 0
            || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("Signed release manifest does not match this updater or has an unsupported schema.");
        }

        if (!Version.TryParse(manifest.Version, out _)
            || !Version.TryParse(manifest.MinimumUpdaterVersion, out Version? requiredUpdater)
            || Version.Parse(BuildInfo.Version) < requiredUpdater)
        {
            throw new InvalidDataException($"Release {manifest.Version} requires updater {manifest.MinimumUpdaterVersion}; this updater is {BuildInfo.Version}.");
        }
        if (!string.Equals(manifest.ReleaseTag, $"modpack-v{manifest.Version}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Signed release manifest has an invalid release tag/version binding.");
        }

        ValidateHash(manifest.Payload.Sha256, "payload");
        if (manifest.Payload.Size <= 0)
        {
            throw new InvalidDataException("Release payload has an invalid size.");
        }

        long totalPartSize = 0;
        var seenParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PayloadPart part in manifest.Payload.Parts)
        {
            if (string.IsNullOrWhiteSpace(part.Name)
                || !string.Equals(part.Name, Path.GetFileName(part.Name), StringComparison.Ordinal)
                || part.Name.Contains('/')
                || part.Name.Contains('\\')
                || part.Name.Contains(':')
                || part.Size <= 0
                || !seenParts.Add(part.Name)
                || !assetUrls.ContainsKey(part.Name))
            {
                throw new InvalidDataException("Release payload references an invalid or unavailable asset part.");
            }
            ValidateHash(part.Sha256, $"payload part {part.Name}");
            try
            {
                totalPartSize = checked(totalPartSize + part.Size);
            }
            catch (OverflowException)
            {
                throw new InvalidDataException("Release payload parts have an invalid total size.");
            }
        }
        if (totalPartSize != manifest.Payload.Size)
        {
            throw new InvalidDataException("Release payload part sizes do not match the signed payload size.");
        }

        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile file in manifest.Files)
        {
            file.Path = PathSafety.NormalizeRelativePath(file.Path);
            if (!PathSafety.IsAllowed(file.Path, configuration.AllowedRoots)
                || file.Size < 0
                || !seenFiles.Add(file.Path))
            {
                throw new InvalidDataException($"Release manifest contains an unsafe or duplicate path: {file.Path}");
            }
            ValidateHash(file.Sha256, $"managed file {file.Path}");
        }

        foreach (string deletePath in manifest.DeletePaths)
        {
            string normalized = PathSafety.NormalizeRelativePath(deletePath);
            if (!PathSafety.IsAllowed(normalized, configuration.AllowedRoots))
            {
                throw new InvalidDataException($"Release manifest contains an unsafe deletion path: {deletePath}");
            }
        }

        var seenLegacyCleanup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LegacyCleanupFile legacyFile in manifest.LegacyCleanup)
        {
            legacyFile.Path = PathSafety.NormalizeRelativePath(legacyFile.Path);
            if (!PathSafety.IsAllowed(legacyFile.Path, configuration.AllowedRoots)
                || legacyFile.Size < 0
                || !seenLegacyCleanup.Add(legacyFile.Path)
                || seenFiles.Contains(legacyFile.Path))
            {
                throw new InvalidDataException($"Release manifest contains an unsafe, duplicate, or overlapping legacy cleanup path: {legacyFile.Path}");
            }
            ValidateHash(legacyFile.Sha256, $"legacy cleanup file {legacyFile.Path}");
        }
    }

    private static void ValidateHash(string hash, string item)
    {
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"Release manifest contains an invalid SHA-256 for {item}.");
        }
    }
}
