using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CobbleMusicUpdater;

internal sealed class ReleaseClient : IDisposable
{
    private const int MaxReleaseIndexBytes = 4 * 1024 * 1024;
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private const int MaxSignatureBytes = 64 * 1024;
    private const int MaxReleasePages = 5;

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ReleaseClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        _http = new HttpClient(handler)
        {
            Timeout = timeout
        };
        _http.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse(BuildInfo.UserAgent));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<IReadOnlyList<RemoteRelease>> GetUpdateChainAsync(
        UpdaterConfiguration configuration,
        InstalledState installedState,
        CancellationToken cancellationToken)
    {
        List<GitHubRelease> releases = await GetPublishedModpackReleasesAsync(configuration, cancellationToken);
        if (releases.Count == 0)
        {
            return [];
        }

        // Manifests carry the delta base hash, so a chain cannot be selected
        // safely from tags alone. Verify every candidate first, with bounded
        // parallelism to avoid serial release latency or API bursts.
        using var throttle = new SemaphoreSlim(4);
        Task<RemoteRelease>[] tasks = releases.Select(async release =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                return await DownloadAndVerifyReleaseAsync(release, configuration, cancellationToken);
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();
        RemoteRelease[] verified = await Task.WhenAll(tasks);
        return BuildSequentialChain(verified, installedState);
    }

    internal static IReadOnlyList<RemoteRelease> BuildSequentialChain(
        IEnumerable<RemoteRelease> verifiedReleases,
        InstalledState installedState)
    {
        List<RemoteRelease> releases = verifiedReleases
            .OrderBy(release => Version.Parse(release.Manifest.Version))
            .ToList();
        if (releases.GroupBy(release => release.Manifest.Version, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("Published modpack releases contain duplicate semantic versions.");
        }

        bool hasInstalledVersion = Version.TryParse(installedState.Version, out Version? installedVersion);
        var candidates = new List<List<RemoteRelease>>();

        // An installed delta base is usable only when its exact signed manifest
        // is still published and verified. The anchor is included so the
        // engine can compare state metadata to the signed full file set before
        // it accepts any subsequent delta.
        if (hasInstalledVersion)
        {
            RemoteRelease? anchor = releases.FirstOrDefault(release =>
                string.Equals(release.Manifest.Version, installedState.Version, StringComparison.Ordinal)
                && string.Equals(release.ManifestSha256, installedState.ManifestSha256, StringComparison.OrdinalIgnoreCase));
            if (anchor is not null)
            {
                var anchoredPath = new List<RemoteRelease> { anchor };
                anchoredPath.AddRange(BestDeltaPath(anchor, releases));
                candidates.Add(anchoredPath);
            }
        }

        // A schema-1 release is a complete signed baseline. It may safely
        // bootstrap a fresh install or replace an older installed version,
        // after which schema-2 releases are followed by exact base hashes.
        foreach (RemoteRelease baseline in releases.Where(release =>
            release.Manifest.SchemaVersion == 1
            && (!hasInstalledVersion || Version.Parse(release.Manifest.Version) > installedVersion)))
        {
            var path = new List<RemoteRelease> { baseline };
            path.AddRange(BestDeltaPath(baseline, releases));
            candidates.Add(path);
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        return candidates
            .OrderByDescending(path => Version.Parse(path[^1].Manifest.Version))
            .ThenBy(path => path.Sum(release => release.Manifest.Payload?.Size ?? 0L))
            .ThenBy(path => path.Count)
            .First();
    }

    private static IReadOnlyList<RemoteRelease> BestDeltaPath(
        RemoteRelease current,
        IReadOnlyCollection<RemoteRelease> releases)
    {
        List<List<RemoteRelease>> paths = releases
            .Where(candidate => candidate.Manifest.SchemaVersion == 2
                && candidate.Manifest.Base is not null
                && string.Equals(candidate.Manifest.Base.Version, current.Manifest.Version, StringComparison.Ordinal)
                && string.Equals(candidate.Manifest.Base.ManifestSha256, current.ManifestSha256, StringComparison.OrdinalIgnoreCase)
                && Version.Parse(candidate.Manifest.Version) > Version.Parse(current.Manifest.Version))
            .Select(candidate =>
            {
                var path = new List<RemoteRelease> { candidate };
                path.AddRange(BestDeltaPath(candidate, releases));
                return path;
            })
            .ToList();

        if (paths.Count == 0)
        {
            return [];
        }
        return paths
            .OrderByDescending(path => Version.Parse(path[^1].Manifest.Version))
            .ThenBy(path => path.Sum(release => release.Manifest.Payload?.Size ?? 0L))
            .ThenBy(path => path.Count)
            .First();
    }

    public async Task DownloadFileAsync(
        Uri source,
        string destination,
        long expectedSize,
        Action<long>? reportDownloadedBytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        long existingSize = File.Exists(destination) ? new FileInfo(destination).Length : 0L;
        if (existingSize > expectedSize)
        {
            File.Delete(destination);
            existingSize = 0L;
        }
        if (existingSize == expectedSize)
        {
            reportDownloadedBytes?.Invoke(expectedSize);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (existingSize > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingSize, null);
        }
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        bool append = existingSize > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            existingSize = 0L;
        }

        reportDownloadedBytes?.Invoke(existingSize);

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            useAsync: true);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            long downloaded = existingSize;
            var reportTimer = Stopwatch.StartNew();
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded = checked(downloaded + read);
                if (reportTimer.ElapsedMilliseconds >= 250)
                {
                    reportDownloadedBytes?.Invoke(downloaded);
                    reportTimer.Restart();
                }
            }
            reportDownloadedBytes?.Invoke(downloaded);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        await output.FlushAsync(cancellationToken);

        long finalSize = new FileInfo(destination).Length;
        if (finalSize != expectedSize)
        {
            throw new InvalidDataException($"Downloaded size mismatch for {Path.GetFileName(destination)}. Expected {expectedSize:N0}, got {finalSize:N0} bytes.");
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<List<GitHubRelease>> GetPublishedModpackReleasesAsync(
        UpdaterConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var result = new List<GitHubRelease>();
        for (int page = 1; page <= MaxReleasePages; page++)
        {
            string endpoint = $"https://api.github.com/repos/{configuration.Repository}/releases?per_page=100&page={page}";
            using HttpResponseMessage response = await _http.GetAsync(endpoint, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }
            response.EnsureSuccessStatusCode();

            byte[] releaseIndex = await ReadBoundedBytesAsync(response.Content, MaxReleaseIndexBytes, cancellationToken);
            List<GitHubRelease>? pageReleases = JsonSerializer.Deserialize<List<GitHubRelease>>(releaseIndex, _jsonOptions);
            if (pageReleases is null)
            {
                throw new InvalidDataException("GitHub returned an invalid release index.");
            }
            result.AddRange(pageReleases.Where(candidate => !candidate.Draft
                && !candidate.Prerelease
                && candidate.TagName.StartsWith("modpack-v", StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(candidate.TagName["modpack-v".Length..], out _)
                && candidate.Assets.Any(asset => string.Equals(asset.Name, configuration.ManifestAsset, StringComparison.Ordinal))
                && candidate.Assets.Any(asset => string.Equals(asset.Name, configuration.SignatureAsset, StringComparison.Ordinal))));
            if (pageReleases.Count < 100)
            {
                break;
            }
            if (page == MaxReleasePages)
            {
                throw new InvalidDataException("GitHub has too many releases for the updater's bounded release-chain scan.");
            }
        }
        return result;
    }

    private async Task<RemoteRelease> DownloadAndVerifyReleaseAsync(
        GitHubRelease release,
        UpdaterConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (release.Assets.GroupBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
        {
            throw new InvalidDataException($"GitHub release {release.TagName} contains duplicate asset names.");
        }
        var assets = release.Assets.ToDictionary(asset => asset.Name, StringComparer.OrdinalIgnoreCase);
        if (!assets.TryGetValue(configuration.ManifestAsset, out GitHubAsset? manifestAsset)
            || !assets.TryGetValue(configuration.SignatureAsset, out GitHubAsset? signatureAsset))
        {
            throw new InvalidDataException($"GitHub release {release.TagName} is missing signed manifest assets.");
        }

        byte[] manifestBytes = await DownloadBytesAsync(ValidatedAssetUri(manifestAsset), MaxManifestBytes, cancellationToken);
        byte[] signatureBytes = await DownloadBytesAsync(ValidatedAssetUri(signatureAsset), MaxSignatureBytes, cancellationToken);
        var urls = assets.ToDictionary(pair => pair.Key, pair => ValidatedAssetUri(pair.Value), StringComparer.OrdinalIgnoreCase);
        UpdateManifest manifest = ManifestParser.VerifyAndParse(manifestBytes, signatureBytes);
        ManifestParser.Validate(manifest, configuration, urls);
        if (!string.Equals(manifest.ReleaseTag, release.TagName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Signed manifest release tag does not match the GitHub release that carried it.");
        }
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        return new RemoteRelease(release, manifestBytes, signatureBytes, urls, manifest, manifestHash);
    }

    private static Uri ValidatedAssetUri(GitHubAsset asset)
    {
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidDataException($"GitHub release contains an invalid HTTPS asset URL for {asset.Name}.");
        }
        return uri;
    }

    private async Task<byte[]> DownloadBytesAsync(Uri source, int maximumBytes, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedBytesAsync(response.Content, maximumBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException($"GitHub response exceeds the {maximumBytes:N0}-byte updater safety limit.");
        }

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"GitHub response exceeds the {maximumBytes:N0}-byte updater safety limit.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
