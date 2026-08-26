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
        : this(CreateDefaultHandler(), timeout)
    {
    }

    internal ReleaseClient(HttpMessageHandler handler, TimeSpan timeout)
    {
        _http = new HttpClient(handler)
        {
            Timeout = timeout
        };
        _http.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse(BuildInfo.UserAgent));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler
    {
        // Release assets are signed and hashed as raw bytes. Transparent
        // decompression would make Content-Length/Range describe different
        // bytes than the updater writes and verifies.
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = true
    };

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

        bool hasInstalledVersion = VersionPolicy.TryParseCanonical(installedState.Version, out Version? installedVersion);
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

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            if (existingSize > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingSize, null);
            }
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            bool append = ValidateDownloadResponse(response, existingSize, expectedSize);
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
                    if (downloaded > expectedSize - read)
                    {
                        throw new AssetProtocolException("Asset stream exceeded its signed size.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;
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
        catch (AssetProtocolException exception)
        {
            TryDeletePartial(destination);
            throw new InvalidDataException($"Rejected invalid asset response for {Path.GetFileName(destination)}: {exception.Message}", exception);
        }
    }

    private static bool ValidateDownloadResponse(HttpResponseMessage response, long existingSize, long expectedSize)
    {
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (existingSize == 0)
            {
                throw new AssetProtocolException("Server sent an unsolicited partial response.");
            }
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            long expectedRemaining = expectedSize - existingSize;
            if (range is null
                || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                || range.From != existingSize
                || range.To != expectedSize - 1
                || range.Length != expectedSize
                || (response.Content.Headers.ContentLength is long partialLength && partialLength != expectedRemaining))
            {
                throw new AssetProtocolException("Resume Content-Range does not match the signed asset size and local offset.");
            }
            return true;
        }
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new AssetProtocolException($"Unexpected successful HTTP status {(int)response.StatusCode}.");
        }
        if (response.Content.Headers.ContentLength is long fullLength && fullLength != expectedSize)
        {
            throw new AssetProtocolException("Full-response Content-Length does not match the signed asset size.");
        }
        // A server may ignore Range and return 200; FileMode.Create safely
        // truncates the old partial and restarts from byte zero.
        return false;
    }

    private static void TryDeletePartial(string path)
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
                && VersionPolicy.TryParseCanonical(candidate.TagName["modpack-v".Length..], out _)
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

        ValidateBoundedAssetMetadata(manifestAsset, MaxManifestBytes, "manifest");
        ValidateBoundedAssetMetadata(signatureAsset, MaxSignatureBytes, "signature");
        byte[] manifestBytes = await DownloadBytesAsync(
            ValidatedAssetUri(manifestAsset),
            MaxManifestBytes,
            manifestAsset.Size,
            cancellationToken);
        byte[] signatureBytes = await DownloadBytesAsync(
            ValidatedAssetUri(signatureAsset),
            MaxSignatureBytes,
            signatureAsset.Size,
            cancellationToken);
        UpdateManifest manifest = ManifestParser.VerifyAndParse(manifestBytes, signatureBytes);
        IReadOnlyDictionary<string, Uri> urls = BindSignedPartAssets(manifest, assets);
        ManifestParser.Validate(manifest, configuration, urls);
        if (!string.Equals(manifest.ReleaseTag, release.TagName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Signed manifest release tag does not match the GitHub release that carried it.");
        }
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        return new RemoteRelease(release, manifestBytes, signatureBytes, urls, manifest, manifestHash);
    }

    internal static IReadOnlyDictionary<string, Uri> BindSignedPartAssets(
        UpdateManifest manifest,
        IReadOnlyDictionary<string, GitHubAsset> assets)
    {
        var urls = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (PayloadPart part in manifest.Payload?.Parts ?? [])
        {
            if (part is null)
            {
                throw new InvalidDataException("Signed payload contains an empty part entry.");
            }
            if (!assets.TryGetValue(part.Name, out GitHubAsset? asset)
                || asset.Size != part.Size)
            {
                throw new InvalidDataException($"GitHub asset metadata does not match signed payload part {part.Name}.");
            }
            urls.Add(part.Name, ValidatedAssetUri(asset));
        }
        return urls;
    }

    internal static void ValidateBoundedAssetMetadata(GitHubAsset asset, int maximumBytes, string kind)
    {
        if (asset.Size <= 0 || asset.Size > maximumBytes)
        {
            throw new InvalidDataException($"GitHub {kind} asset has an invalid declared size.");
        }
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

    private async Task<byte[]> DownloadBytesAsync(
        Uri source,
        int maximumBytes,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await ReadBoundedBytesAsync(response.Content, maximumBytes, cancellationToken);
        if (bytes.LongLength != expectedBytes)
        {
            throw new InvalidDataException($"GitHub asset size differs from its release metadata. Expected {expectedBytes:N0}, got {bytes.LongLength:N0} bytes.");
        }
        return bytes;
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

    private sealed class AssetProtocolException : IOException
    {
        public AssetProtocolException(string message) : base(message) { }
    }
}
