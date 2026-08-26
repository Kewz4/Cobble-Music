using System.Text.Json.Serialization;

namespace CobbleMusicUpdater;

internal sealed class UpdaterConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string ModpackId { get; set; } = BuildInfo.DefaultModpackId;
    public string Repository { get; set; } = BuildInfo.DefaultRepository;
    public string Channel { get; set; } = "stable";
    public string ManifestAsset { get; set; } = "cobble-music-update.json";
    public string SignatureAsset { get; set; } = "cobble-music-update.sig";
    public int NetworkTimeoutSeconds { get; set; } = 30;
    public bool AllowOfflineLaunch { get; set; } = true;
    public List<string> AllowedRoots { get; set; } =
    [
        "mods",
        "resourcepacks",
        "config",
        "defaultconfigs",
        "kubejs",
        "scripts"
    ];
}

internal sealed class InstalledState
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public DateTimeOffset AppliedAtUtc { get; set; }
    public List<ManagedFileState> ManagedFiles { get; set; } = [];
}

internal sealed class ManagedFileState
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";
}

internal sealed class DetachedSignature
{
    public int SchemaVersion { get; set; } = 1;
    public string Algorithm { get; set; } = "Ed25519";
    public string KeyId { get; set; } = "cobble-music-release-1";
    public string Signature { get; set; } = "";
}

internal sealed class UpdateManifest
{
    public int SchemaVersion { get; set; }
    public string ModpackId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Version { get; set; } = "";
    public string ReleaseTag { get; set; } = "";
    public string MinimumUpdaterVersion { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public UpdatePayload Payload { get; set; } = new();
    public List<ManifestFile> Files { get; set; } = [];
    public List<string> DeletePaths { get; set; } = [];
    public List<LegacyCleanupFile> LegacyCleanup { get; set; } = [];
}

internal sealed class UpdatePayload
{
    public string ArchiveName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public List<PayloadPart> Parts { get; set; } = [];
}

internal sealed class PayloadPart
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed class ManifestFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

// Used only for a reviewed, one-time migration from a known older pack. The
// updater deletes it only when the local file exactly matches this hash.
internal sealed class LegacyCleanupFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed record UpdaterPaths(
    string InstanceDirectory,
    string MinecraftDirectory,
    string InstallationDirectory,
    string ConfigurationPath,
    string StatePath,
    string LocalDataDirectory);

internal sealed record RemoteRelease(
    GitHubRelease Release,
    byte[] ManifestBytes,
    byte[] SignatureBytes,
    IReadOnlyDictionary<string, Uri> AssetUrls);
