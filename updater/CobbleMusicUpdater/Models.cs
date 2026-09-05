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
        "shaderpacks",
        "datapacks",
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
    // Records which create-only defaults have already been offered. The files
    // themselves remain player-owned and are never integrity checked.
    public List<string> OfferedSeedPaths { get; set; } = [];
    // Records narrowly scoped, signed migrations of player-owned settings.
    // Once recorded, that migration is never evaluated again, so a player's
    // later keybind/video/sound choices remain fully player-owned.
    public List<string> AppliedPlayerSettingMigrationIds { get; set; } = [];
}

internal sealed class ManagedFileState
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

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
    internal HashSet<string> VerifiedRetiredSeedIdentities { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> VerifiedPlayerOwnedIdentities { get; } = new(StringComparer.Ordinal);
    public int SchemaVersion { get; set; }
    public string ModpackId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Version { get; set; } = "";
    public string ReleaseTag { get; set; } = "";
    public string MinimumUpdaterVersion { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public ManifestBase? Base { get; set; }
    public UpdatePayload? Payload { get; set; }
    // Schema 1 payloads contain every entry in Files. Schema 2 payloads contain
    // only these changed/new entries while Files remains the authoritative
    // complete post-update state.
    public List<ManifestFile> PayloadFiles { get; set; } = [];
    // Schema 2 removals carry the exact metadata from the signed base state.
    public List<ManifestFile> DeletedFiles { get; set; } = [];
    // Signed create-only defaults are carried in the payload but are never
    // recorded as managed state. Existing player files always win.
    public List<ManifestFile> SeedFiles { get; set; } = [];
    // A corrective delta may explicitly offer selected create-only defaults
    // again when they are missing. Existing files still always win, and the
    // repair applies only while installing this signed release.
    public List<string> ReofferSeedPaths { get; set; } = [];
    // Signed, narrowly scoped literal replacements for player-owned text
    // settings. They are applied only when the exact old text is present;
    // every other byte and every unrelated setting remains player-owned.
    public List<SeedTextReplacement> SeedTextReplacements { get; set; } = [];
    public List<ManifestFile> Files { get; set; } = [];
    public List<string> DeletePaths { get; set; } = [];
    public List<LegacyCleanupFile> LegacyCleanup { get; set; } = [];
}

internal sealed class ManifestBase
{
    public string Version { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
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

internal sealed class SeedTextReplacement
{
    public string Path { get; set; } = "";
    public string OldText { get; set; } = "";
    public string NewText { get; set; } = "";
    // Empty only for the pre-1.2.11 Iris selector migration. New migrations
    // of player-owned settings must carry a stable ID that is committed to the
    // local one-time migration ledger with the surrounding transaction.
    public string MigrationId { get; set; } = "";
    // Every required line must occur exactly once before this replacement is
    // eligible. This keeps corrective migrations conditional on the precise
    // legacy state they were designed to repair.
    public List<string> RequiredLines { get; set; } = [];
}

// Used only for a reviewed, one-time migration from a known older pack. The
// updater deletes it only when the local file exactly matches this hash, or
// accepts it as an exact pre-state when the same signed delta replaces that
// managed path with a carried payload file.
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
    IReadOnlyDictionary<string, Uri> AssetUrls,
    UpdateManifest Manifest,
    string ManifestSha256);
