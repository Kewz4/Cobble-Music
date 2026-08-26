using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CobbleMusicUpdater;

internal sealed class ReleaseClient : IDisposable
{
    private const int MaxReleaseIndexBytes = 4 * 1024 * 1024;
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private const int MaxSignatureBytes = 64 * 1024;

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

    public async Task<RemoteRelease?> GetLatestAsync(UpdaterConfiguration configuration, CancellationToken cancellationToken)
    {
        // Do not use GitHub's /releases/latest endpoint here. A normal source
        // or updater-binary release could otherwise mask the most recent
        // modpack payload. `modpack-v*` is a deliberately reserved tag
        // namespace and we choose the highest stable semantic version within it.
        string endpoint = $"https://api.github.com/repos/{configuration.Repository}/releases?per_page=100";
        using HttpResponseMessage response = await _http.GetAsync(endpoint, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        byte[] releaseIndex = await ReadBoundedBytesAsync(response.Content, MaxReleaseIndexBytes, cancellationToken);
        List<GitHubRelease>? releases = JsonSerializer.Deserialize<List<GitHubRelease>>(releaseIndex, _jsonOptions);
        GitHubRelease? release = releases?
            .Where(candidate => !candidate.Draft
                && !candidate.Prerelease
                && candidate.TagName.StartsWith("modpack-v", StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(candidate.TagName["modpack-v".Length..], out _)
                && candidate.Assets.Any(asset => string.Equals(asset.Name, configuration.ManifestAsset, StringComparison.Ordinal))
                && candidate.Assets.Any(asset => string.Equals(asset.Name, configuration.SignatureAsset, StringComparison.Ordinal)))
            .OrderByDescending(candidate => Version.Parse(candidate.TagName["modpack-v".Length..]))
            .FirstOrDefault();
        if (release is null)
        {
            return null;
        }

        var assets = release.Assets.ToDictionary(asset => asset.Name, StringComparer.OrdinalIgnoreCase);
        if (!assets.TryGetValue(configuration.ManifestAsset, out GitHubAsset? manifestAsset)
            || !assets.TryGetValue(configuration.SignatureAsset, out GitHubAsset? signatureAsset))
        {
            throw new InvalidDataException("Latest GitHub release is missing the signed Kewz's Cobblemon manifest assets.");
        }

        byte[] manifest = await DownloadBytesAsync(new Uri(manifestAsset.BrowserDownloadUrl), MaxManifestBytes, cancellationToken);
        byte[] signature = await DownloadBytesAsync(new Uri(signatureAsset.BrowserDownloadUrl), MaxSignatureBytes, cancellationToken);
        var urls = assets.ToDictionary(pair => pair.Key, pair => new Uri(pair.Value.BrowserDownloadUrl), StringComparer.OrdinalIgnoreCase);
        return new RemoteRelease(release, manifest, signature, urls);
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
