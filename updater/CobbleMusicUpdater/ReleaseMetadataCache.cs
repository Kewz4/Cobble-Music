using System.Security.Cryptography;
using System.Text.Json;

namespace CobbleMusicUpdater;

internal sealed record CachedReleaseMetadata(
    byte[] ManifestBytes,
    byte[] SignatureBytes,
    IReadOnlyList<GitHubAsset> Assets);

// Local copy of each release's signed manifest and signature (design:
// bootstrap-path RC4 fix 1). Without it every launch re-downloads ~53
// manifests (82 MB) and makes one /assets API call per release, which runs
// into GitHub's 60-calls-per-hour anonymous limit.
//
// The cache is a download shortcut, never a source of trust:
// - an entry is used only when the live release listing still names the same
//   release id and tag and the same manifest/signature asset (id, name, size,
//   updated_at); when GitHub's listing carries a sha256 digest, the cached
//   bytes must hash to it;
// - the caller runs the cached bytes through the same Ed25519 verification,
//   parsing, tag binding and validation as freshly downloaded bytes, and
//   evicts and re-downloads an entry that fails.
// GitHub's asset JSON has no ETag field, and reading one would cost the very
// request the cache exists to avoid, so updated_at is the change detector.
internal sealed class ReleaseMetadataCache
{
    private const int EntrySchemaVersion = 1;
    // 8 MiB manifest + 64 KiB signature as base64, plus the asset list.
    private const long MaximumEntryBytes = 16L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _root;

    internal ReleaseMetadataCache(string localDataDirectory)
    {
        _root = Path.GetFullPath(localDataDirectory);
        DirectoryPath = Path.Combine(_root, "cache", "releases");
    }

    internal string DirectoryPath { get; }

    internal string EntryPath(long releaseId) =>
        Path.Combine(DirectoryPath, releaseId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");

    internal CachedReleaseMetadata? TryLoad(
        GitHubRelease release,
        GitHubAsset manifestAsset,
        GitHubAsset signatureAsset,
        Action<string>? log)
    {
        if (!IsCacheable(release, manifestAsset, signatureAsset))
        {
            return null;
        }
        string path = EntryPath(release.Id);
        try
        {
            if (!File.Exists(path) || !IsSafeLocation(path))
            {
                return null;
            }
            if (new FileInfo(path).Length > MaximumEntryBytes)
            {
                Reject(path, release, "entry is oversized", log);
                return null;
            }
            Entry? entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path), JsonOptions);
            string? mismatch = FindMismatch(entry, release, manifestAsset, signatureAsset,
                out byte[] manifestBytes, out byte[] signatureBytes);
            if (mismatch is not null)
            {
                Reject(path, release, mismatch, log);
                return null;
            }
            List<GitHubAsset> assets = (entry!.Assets ?? []).Where(asset => asset is not null).Select(ToAsset).ToList();
            return new CachedReleaseMetadata(manifestBytes, signatureBytes, assets);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or FormatException or NotSupportedException)
        {
            Reject(path, release, $"entry is unreadable ({exception.GetType().Name})", log);
            return null;
        }
    }

    internal void Save(
        GitHubRelease release,
        GitHubAsset manifestAsset,
        GitHubAsset signatureAsset,
        byte[] manifestBytes,
        byte[] signatureBytes,
        IReadOnlyList<GitHubAsset> assets,
        Action<string>? log)
    {
        if (!IsCacheable(release, manifestAsset, signatureAsset))
        {
            return;
        }
        string path = EntryPath(release.Id);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            if (!IsSafeLocation(path))
            {
                return;
            }
            var entry = new Entry
            {
                SchemaVersion = EntrySchemaVersion,
                ReleaseId = release.Id,
                TagName = release.TagName,
                Manifest = FromAsset(manifestAsset),
                Signature = FromAsset(signatureAsset),
                ManifestBase64 = Convert.ToBase64String(manifestBytes),
                SignatureBase64 = Convert.ToBase64String(signatureBytes),
                Assets = assets.Where(asset => asset is not null).Select(FromAsset).ToList()
            };
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A cache that cannot be written only costs a download next launch.
            log?.Invoke($"Could not cache signed metadata for {release.TagName} ({exception.GetType().Name}).");
            TryDelete(temporary);
        }
    }

    internal void Evict(long releaseId) => TryDeleteEntry(EntryPath(releaseId));

    // Drops entries for releases GitHub no longer lists, so a deleted release
    // cannot linger on disk. Only names this class writes are touched.
    internal void Prune(IEnumerable<long> liveReleaseIds, Action<string>? log)
    {
        try
        {
            if (!Directory.Exists(DirectoryPath) || !IsSafeLocation(EntryPath(1)))
            {
                return;
            }
            var live = liveReleaseIds.ToHashSet();
            foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length > 0
                    && name.All(char.IsAsciiDigit)
                    && long.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)
                    && !live.Contains(id))
                {
                    TryDeleteEntry(file);
                    log?.Invoke($"Removed cached metadata for release id {id}, which GitHub no longer lists.");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static bool SameAsset(GitHubAsset left, GitHubAsset right) =>
        left.Id == right.Id
        && left.Size == right.Size
        && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.BrowserDownloadUrl, right.BrowserDownloadUrl, StringComparison.Ordinal)
        && string.Equals(left.UpdatedAt ?? "", right.UpdatedAt ?? "", StringComparison.Ordinal);

    // Without GitHub's asset ids there is no key that notices a replaced
    // asset, so such releases are simply not cached.
    private static bool IsCacheable(GitHubRelease release, GitHubAsset manifestAsset, GitHubAsset signatureAsset) =>
        release.Id > 0 && manifestAsset.Id > 0 && signatureAsset.Id > 0;

    private bool IsSafeLocation(string path)
    {
        try
        {
            PathSafety.AssertNoReparsePointsOnTargetPath(_root, path);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Never read or write cache files through a junction or link, and
            // treat an unreadable location as no cache at all.
            return false;
        }
    }

    private static string? FindMismatch(
        Entry? entry,
        GitHubRelease release,
        GitHubAsset manifestAsset,
        GitHubAsset signatureAsset,
        out byte[] manifestBytes,
        out byte[] signatureBytes)
    {
        manifestBytes = [];
        signatureBytes = [];
        if (entry is null || entry.SchemaVersion != EntrySchemaVersion)
        {
            return "unsupported entry";
        }
        if (entry.ReleaseId != release.Id || !string.Equals(entry.TagName, release.TagName, StringComparison.Ordinal))
        {
            return "release id or tag changed";
        }
        if (entry.Manifest is null || !SameAsset(ToAsset(entry.Manifest), manifestAsset))
        {
            return "manifest asset changed";
        }
        if (entry.Signature is null || !SameAsset(ToAsset(entry.Signature), signatureAsset))
        {
            return "signature asset changed";
        }
        manifestBytes = Convert.FromBase64String(entry.ManifestBase64 ?? "");
        signatureBytes = Convert.FromBase64String(entry.SignatureBase64 ?? "");
        if (manifestBytes.LongLength != manifestAsset.Size || signatureBytes.LongLength != signatureAsset.Size)
        {
            return "cached size differs from the listed asset size";
        }
        if (!MatchesListedDigest(manifestBytes, manifestAsset) || !MatchesListedDigest(signatureBytes, signatureAsset))
        {
            return "cached bytes differ from GitHub's listed digest";
        }
        return null;
    }

    // Only a sha256 digest is understood; any other or absent digest leaves
    // the signature check as the (sufficient) integrity gate.
    private static bool MatchesListedDigest(byte[] bytes, GitHubAsset asset)
    {
        const string prefix = "sha256:";
        if (asset.Digest is not string digest || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string actual = Convert.ToHexString(SHA256.HashData(bytes));
        return string.Equals(actual, digest[prefix.Length..], StringComparison.OrdinalIgnoreCase);
    }

    private void Reject(string path, GitHubRelease release, string reason, Action<string>? log)
    {
        log?.Invoke($"Cached metadata for {release.TagName} was not used ({reason}); downloading it again.");
        TryDeleteEntry(path);
    }

    private void TryDeleteEntry(string path)
    {
        if (IsSafeLocation(path))
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CachedAsset FromAsset(GitHubAsset asset) => new()
    {
        Id = asset.Id,
        Name = asset.Name,
        Size = asset.Size,
        UpdatedAt = asset.UpdatedAt,
        Url = asset.BrowserDownloadUrl
    };

    private static GitHubAsset ToAsset(CachedAsset asset) => new()
    {
        Id = asset.Id,
        Name = asset.Name ?? "",
        Size = asset.Size,
        UpdatedAt = asset.UpdatedAt,
        BrowserDownloadUrl = asset.Url ?? ""
    };

    private sealed class Entry
    {
        public int SchemaVersion { get; set; }
        public long ReleaseId { get; set; }
        public string? TagName { get; set; }
        public CachedAsset? Manifest { get; set; }
        public CachedAsset? Signature { get; set; }
        public string? ManifestBase64 { get; set; }
        public string? SignatureBase64 { get; set; }
        public List<CachedAsset>? Assets { get; set; }
    }

    private sealed class CachedAsset
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public long Size { get; set; }
        public string? UpdatedAt { get; set; }
        public string? Url { get; set; }
    }
}
