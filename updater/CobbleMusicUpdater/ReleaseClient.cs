using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CobbleMusicUpdater;

internal sealed class ReleaseClient : IDisposable
{
    private const int MaxReleaseIndexBytes = 4 * 1024 * 1024;
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private const int MaxSignatureBytes = 64 * 1024;
    private const int MaxReleasePages = 5;
    private const int MaxAssetPagesPerRelease = 10;

    private readonly HttpClient _http;
    private readonly object _logGate = new();
    // Stopwatch deadline of the running release check; 0 outside a check.
    private long _checkDeadline;
    internal IReadOnlyList<RemoteRelease> VerifiedReleases { get; private set; } = [];
    // Tags of releases that could not be reached and were provably not needed.
    internal IReadOnlyList<string> SkippedReleaseTags { get; private set; } = [];
    internal NetworkPolicy Policy { get; set; } = NetworkPolicy.Default;
    internal Action<string>? DiagnosticLog { get; set; }
    // Short player-facing text shown while a request is being retried.
    internal Action<string>? RetryNotice { get; set; }
    internal ReleaseMetadataCache? MetadataCache { get; set; }
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

    // Wires the release check to the updater log, the card and the verified
    // metadata cache of this instance (1.2.18 network track).
    internal void AttachReleaseCheck(UpdaterPaths paths, Action<string> log, Action<string> retryNotice)
    {
        DiagnosticLog = log;
        RetryNotice = retryNotice;
        MetadataCache = new ReleaseMetadataCache(paths.LocalDataDirectory);
    }

    public async Task<IReadOnlyList<RemoteRelease>> GetUpdateChainAsync(
        UpdaterConfiguration configuration,
        InstalledState installedState,
        CancellationToken cancellationToken)
    {
        // One wall-clock budget covers the release index, asset lists and
        // manifests. Its expiry surfaces as TimeoutException, which Program
        // maps to the existing offline fallback; a cancellation requested by
        // the caller still surfaces as OperationCanceledException.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Policy.ReleaseCheckBudget);
        Volatile.Write(ref _checkDeadline, Deadline.After(Policy.ReleaseCheckBudget));
        try
        {
            List<GitHubRelease> releases = await ListModpackReleasesAsync(configuration, budget.Token);
            if (releases.Count == 0)
            {
                return [];
            }

            // Manifests carry the delta base hash, so a chain cannot be selected
            // safely from tags alone. Verify every candidate first, with bounded
            // parallelism to avoid serial release latency or API bursts.
            using var throttle = new SemaphoreSlim(4);
            Task<ReleaseOutcome>[] tasks = releases
                .Select(release => VerifyReleaseOutcomeAsync(release, configuration, throttle, budget.Token, cancellationToken))
                .ToArray();
            ReleaseOutcome[] outcomes = await Task.WhenAll(tasks);
            List<RemoteRelease> verified = SelectReachableReleases(outcomes, installedState, budget.IsCancellationRequested);
            VerifiedReleases = verified;
            MetadataCache?.Prune(releases.Select(release => release.Id), Log);
            return BuildSequentialChain(verified, installedState);
        }
        catch (OperationCanceledException exception) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw BudgetExpired(exception);
        }
        finally
        {
            Volatile.Write(ref _checkDeadline, 0L);
        }
    }

    private TimeoutException BudgetExpired(Exception inner) =>
        new($"The update check did not finish within {Policy.ReleaseCheckBudget.TotalSeconds:0.###} seconds.", inner);

    private TimeSpan? RemainingCheckBudget()
    {
        long deadline = Volatile.Read(ref _checkDeadline);
        return deadline == 0 ? null : Deadline.Remaining(deadline);
    }

    internal sealed record ReleaseOutcome(GitHubRelease Release, RemoteRelease? Remote, Exception? Failure);

    private async Task<ReleaseOutcome> VerifyReleaseOutcomeAsync(
        GitHubRelease release,
        UpdaterConfiguration configuration,
        SemaphoreSlim throttle,
        CancellationToken budgetToken,
        CancellationToken callerToken)
    {
        bool entered = false;
        try
        {
            await throttle.WaitAsync(budgetToken);
            entered = true;
            return new ReleaseOutcome(release, await VerifyReleaseAsync(release, configuration, budgetToken), null);
        }
        catch (Exception exception) when (!callerToken.IsCancellationRequested && IsUnreachable(exception))
        {
            // Decided after every release has finished: see SelectReachableReleases.
            return new ReleaseOutcome(release, null, exception);
        }
        finally
        {
            if (entered)
            {
                throttle.Release();
            }
        }
    }

    // Only transport-level failures make a release "unreachable". A bad
    // signature, an invalid manifest or a protocol violation is never skipped
    // (InvalidDataException is not an IOException).
    private static bool IsUnreachable(Exception exception) =>
        exception is HttpRequestException or TimeoutException or OperationCanceledException or IOException;

    private List<RemoteRelease> SelectReachableReleases(
        IReadOnlyList<ReleaseOutcome> outcomes,
        InstalledState installedState,
        bool budgetExpired)
    {
        Version newest = outcomes.Max(outcome => TagVersion(outcome.Release))!;
        List<ReleaseOutcome> failures = outcomes
            .Where(outcome => outcome.Failure is not null)
            .OrderByDescending(outcome => TagVersion(outcome.Release))
            .ToList();
        List<ReleaseOutcome> needed = failures
            .Where(outcome => IsOnUpdatePath(TagVersion(outcome.Release), newest, installedState))
            .ToList();
        if (needed.Count > 0)
        {
            foreach (ReleaseOutcome outcome in needed)
            {
                Log($"Release {outcome.Release.TagName} could not be reached and may be needed for this update: {ResilientIo.Describe(outcome.Failure!)}");
            }
            // A rate limit explains the failure best, so it decides the card text.
            ReleaseOutcome decisive = needed.FirstOrDefault(outcome =>
                ReleaseCheckDiagnostics.FindHttpFailure(outcome.Failure!) is { IsRateLimited: true }) ?? needed[0];
            if (decisive.Failure is OperationCanceledException && budgetExpired)
            {
                throw BudgetExpired(decisive.Failure);
            }
            ExceptionDispatchInfo.Capture(decisive.Failure!).Throw();
        }
        foreach (ReleaseOutcome outcome in failures)
        {
            Log($"Skipped unreachable release {outcome.Release.TagName}: it is older than the installed {installedState.Version} and not needed to reach {newest} ({ResilientIo.Describe(outcome.Failure!)}).");
        }
        SkippedReleaseTags = failures.Select(outcome => outcome.Release.TagName).ToList();
        return outcomes.Where(outcome => outcome.Remote is not null).Select(outcome => outcome.Remote!).ToList();
    }

    // Whether a release that could not be reached might matter to this
    // update. Deliberately conservative: only a release older than a known
    // installed version is skippable, because
    // - the newest release is the convergence target,
    // - the installed release anchors the delta chain, and every newer release
    //   can be crossed by it or be the only signed source of a needed file,
    // - with no installed version (fresh install) any release may be a source,
    // - an installed state without an offered-seed ledger is back-filled by
    //   UpdateEngine from every release up to the installed one.
    internal static bool IsOnUpdatePath(Version releaseVersion, Version newestPublished, InstalledState installedState)
    {
        if (releaseVersion >= newestPublished)
        {
            return true;
        }
        if (!VersionPolicy.TryParseCanonical(installedState.Version, out Version? installed))
        {
            return true;
        }
        if (installedState.OfferedSeedPaths.Count == 0)
        {
            return true;
        }
        return releaseVersion >= installed;
    }

    private static Version TagVersion(GitHubRelease release) =>
        VersionPolicy.TryParseCanonical(release.TagName["modpack-v".Length..], out Version? version)
            ? version!
            : throw new InvalidDataException($"GitHub release {release.TagName} has no canonical version.");

    private void Log(string message)
    {
        // Releases are verified four at a time; the log sink appends to a file.
        lock (_logGate)
        {
            DiagnosticLog?.Invoke(message);
        }
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

    // Payload parts: up to Policy.MaxAttempts attempts for transport errors,
    // stalls (no byte for Policy.PayloadIdleTimeout), 5xx and 429. The partial
    // file survives a transient failure, so each retry resumes with a Range
    // request that ValidateDownloadResponse checks exactly as before. There
    // is deliberately no total wall-clock cap on a download that is moving.
    public Task DownloadFileAsync(
        Uri source,
        string destination,
        long expectedSize,
        Action<long>? reportDownloadedBytes,
        CancellationToken cancellationToken) =>
        ResilientIo.WithRetriesAsync(
            Policy,
            $"Download of {Path.GetFileName(destination)}",
            async attemptToken =>
            {
                await DownloadFileOnceAsync(source, destination, expectedSize, reportDownloadedBytes, attemptToken);
                return true;
            },
            Log,
            RetryNotice,
            remainingBudget: null,
            cancellationToken);

    private async Task DownloadFileOnceAsync(
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
            HttpResponses.EnsureSuccess(response, $"Download of {Path.GetFileName(destination)}");

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
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                long downloaded = existingSize;
                var reportTimer = Stopwatch.StartNew();
                int read;
                while ((read = await ResilientIo.ReadAsync(input, buffer.AsMemory(), idle, Policy.PayloadIdleTimeout, cancellationToken)) > 0)
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
            if (finalSize < expectedSize)
            {
                // The body ended early without a transport error. Keep the
                // partial; the retry resumes it from this offset.
                throw new TruncatedDownloadException($"Download of {Path.GetFileName(destination)} ended early at {finalSize:N0} of {expectedSize:N0} bytes.");
            }
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

    internal async Task<List<GitHubRelease>> GetPublishedModpackReleasesAsync(
        UpdaterConfiguration configuration,
        CancellationToken cancellationToken)
    {
        List<GitHubRelease> result = await ListModpackReleasesAsync(configuration, cancellationToken);
        using var throttle = new SemaphoreSlim(4);
        Task[] assetTasks = result.Select(async release =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                if (!NestedAssetsCarrySignedMetadata(release, configuration))
                {
                    await LoadCompleteAssetListAsync(configuration, release, cancellationToken);
                }
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();
        await Task.WhenAll(assetTasks);
        return result.Where(release => CarriesSignedMetadata(release, configuration)).ToList();
    }

    // The release index pages only (normally one API call). Each release keeps
    // the nested asset list GitHub embeds in this response.
    private async Task<List<GitHubRelease>> ListModpackReleasesAsync(
        UpdaterConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var result = new List<GitHubRelease>();
        for (int page = 1; page <= MaxReleasePages; page++)
        {
            string endpoint = $"https://api.github.com/repos/{configuration.Repository}/releases?per_page=100&page={page}";
            byte[]? releaseIndex = await GetApiBytesAsync(endpoint, $"release list page {page}", notFoundMeansEmpty: true, cancellationToken);
            if (releaseIndex is null)
            {
                return [];
            }
            List<GitHubRelease>? pageReleases = JsonSerializer.Deserialize<List<GitHubRelease>>(releaseIndex, _jsonOptions);
            if (pageReleases is null || pageReleases.Any(candidate => candidate is null))
            {
                throw new InvalidDataException("GitHub returned an invalid release index.");
            }
            result.AddRange(pageReleases.Where(candidate => !candidate.Draft
                && !candidate.Prerelease
                && candidate.TagName.StartsWith("modpack-v", StringComparison.OrdinalIgnoreCase)
                && VersionPolicy.TryParseCanonical(candidate.TagName["modpack-v".Length..], out _)));
            if (pageReleases.Count < 100)
            {
                break;
            }
            if (page == MaxReleasePages)
            {
                throw new InvalidDataException("GitHub has too many releases for the updater's bounded release-chain scan.");
            }
        }
        if (result.Any(release => release.Id <= 0))
        {
            throw new InvalidDataException("GitHub returned a modpack release without a valid release ID.");
        }
        foreach (GitHubRelease release in result)
        {
            release.Assets ??= [];
            release.AssetListIsComplete = false;
        }
        return result;
    }

    // The nested `assets` of a list-releases response come from the same
    // API over the same TLS channel as /releases/{id}/assets, so they carry
    // the same (unsigned) trust. What matters for security is unchanged:
    // manifest and signature bytes are Ed25519-verified, and every signed
    // payload part must bind to an asset of exactly its signed size before
    // its bytes are SHA-256 checked. The only risk of the nested copy is that
    // it could be incomplete; that shows up as a missing manifest, signature
    // or part, and only then is the paginated endpoint called. This saves one
    // API call per release per launch (bootstrap-path RC4).
    private static bool NestedAssetsCarrySignedMetadata(GitHubRelease release, UpdaterConfiguration configuration) =>
        release.Assets.All(asset => asset is not null)
        && release.Assets.GroupBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).All(group => group.Count() == 1)
        && CarriesSignedMetadata(release, configuration);

    private static bool CarriesSignedMetadata(GitHubRelease release, UpdaterConfiguration configuration) =>
        release.Assets.Any(asset => asset is not null && string.Equals(asset.Name, configuration.ManifestAsset, StringComparison.Ordinal))
        && release.Assets.Any(asset => asset is not null && string.Equals(asset.Name, configuration.SignatureAsset, StringComparison.Ordinal));

    private async Task LoadCompleteAssetListAsync(
        UpdaterConfiguration configuration,
        GitHubRelease release,
        CancellationToken cancellationToken)
    {
        release.Assets = await GetAllReleaseAssetsAsync(configuration, release.Id, cancellationToken);
        release.AssetListIsComplete = true;
    }

    internal async Task<List<GitHubAsset>> GetAllReleaseAssetsAsync(
        UpdaterConfiguration configuration,
        long releaseId,
        CancellationToken cancellationToken)
    {
        if (releaseId <= 0)
        {
            throw new InvalidDataException("GitHub release asset lookup requires a valid release ID.");
        }
        var assets = new List<GitHubAsset>();
        for (int page = 1; page <= MaxAssetPagesPerRelease; page++)
        {
            string endpoint = $"https://api.github.com/repos/{configuration.Repository}/releases/{releaseId}/assets?per_page=100&page={page}";
            byte[] assetIndex = (await GetApiBytesAsync(endpoint, $"asset list of release {releaseId} page {page}", notFoundMeansEmpty: false, cancellationToken))!;
            List<GitHubAsset>? pageAssets = JsonSerializer.Deserialize<List<GitHubAsset>>(assetIndex, _jsonOptions);
            if (pageAssets is null || pageAssets.Count > 100 || pageAssets.Any(asset => asset is null))
            {
                throw new InvalidDataException($"GitHub returned an invalid asset index for release {releaseId}.");
            }
            assets.AddRange(pageAssets);
            if (pageAssets.Count < 100)
            {
                return assets;
            }
            if (page == MaxAssetPagesPerRelease)
            {
                throw new InvalidDataException($"GitHub release {releaseId} exceeds the updater's bounded asset scan.");
            }
        }
        throw new InvalidDataException($"GitHub release {releaseId} asset scan did not terminate safely.");
    }

    // One api.github.com GET with retries. Every response's status and rate
    // limit headers are logged, so a 403 can be told apart from a network
    // error in a friend's updater.log.
    private Task<byte[]?> GetApiBytesAsync(
        string endpoint,
        string what,
        bool notFoundMeansEmpty,
        CancellationToken cancellationToken) =>
        ResilientIo.WithRetriesAsync<byte[]?>(
            Policy,
            $"GitHub API {what}",
            async attemptToken =>
            {
                using HttpResponseMessage response = await _http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, attemptToken);
                Log($"GitHub API {what}: {HttpResponses.DescribeStatus(response)}.");
                if (notFoundMeansEmpty && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                HttpResponses.EnsureSuccess(response, $"GitHub API {what}");
                return await ReadBoundedBytesAsync(response.Content, MaxReleaseIndexBytes, Policy.MetadataIdleTimeout, attemptToken);
            },
            Log,
            RetryNotice,
            RemainingCheckBudget,
            cancellationToken);

    // Returns null when the release carries no signed manifest/signature pair
    // (not an updater release), exactly as the former list filter did.
    private async Task<RemoteRelease?> VerifyReleaseAsync(
        GitHubRelease release,
        UpdaterConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!release.AssetListIsComplete && !NestedAssetsCarrySignedMetadata(release, configuration))
        {
            await LoadCompleteAssetListAsync(configuration, release, cancellationToken);
        }
        if (!CarriesSignedMetadata(release, configuration))
        {
            return null;
        }
        return await DownloadAndVerifyReleaseAsync(release, configuration, cancellationToken);
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

        byte[]? manifestBytes = null;
        byte[]? signatureBytes = null;
        UpdateManifest? manifest = null;
        CachedReleaseMetadata? cached = MetadataCache?.TryLoad(release, manifestAsset, signatureAsset, Log);
        if (cached is not null)
        {
            try
            {
                // Cached bytes get the full verification that downloaded
                // bytes get; the cache is never trusted by itself.
                manifest = VerifyReleaseMetadata(cached.ManifestBytes, cached.SignatureBytes, release);
                manifestBytes = cached.ManifestBytes;
                signatureBytes = cached.SignatureBytes;
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                Log($"Cached metadata for {release.TagName} failed verification ({exception.Message}); downloading it again.");
                MetadataCache!.Evict(release.Id);
                cached = null;
            }
        }
        if (manifest is null)
        {
            manifestBytes = await DownloadBytesAsync(
                ValidatedAssetUri(manifestAsset),
                MaxManifestBytes,
                manifestAsset.Size,
                $"Download of {release.TagName} {manifestAsset.Name}",
                cancellationToken);
            signatureBytes = await DownloadBytesAsync(
                ValidatedAssetUri(signatureAsset),
                MaxSignatureBytes,
                signatureAsset.Size,
                $"Download of {release.TagName} {signatureAsset.Name}",
                cancellationToken);
            manifest = VerifyReleaseMetadata(manifestBytes, signatureBytes, release);
        }

        (IReadOnlyDictionary<string, Uri> urls, bool assetListChanged) = await BindPartsAsync(
            release, configuration, manifest, manifestAsset, signatureAsset, cached, cancellationToken);
        ManifestParser.Validate(manifest, configuration, urls);
        if (cached is null || assetListChanged)
        {
            MetadataCache?.Save(release, manifestAsset, signatureAsset, manifestBytes!, signatureBytes!, release.Assets, Log);
        }
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes!)).ToLowerInvariant();
        return new RemoteRelease(release, manifestBytes!, signatureBytes!, urls, manifest, manifestHash);
    }

    private static UpdateManifest VerifyReleaseMetadata(byte[] manifestBytes, byte[] signatureBytes, GitHubRelease release)
    {
        UpdateManifest manifest = ManifestParser.VerifyAndParse(manifestBytes, signatureBytes);
        if (!string.Equals(manifest.ReleaseTag, release.TagName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Signed manifest release tag does not match the GitHub release that carried it.");
        }
        return manifest;
    }

    // Binds signed payload parts to download URLs. Nested listing first; if it
    // lacks a part, the asset list saved with a cache entry whose manifest and
    // signature assets match the live listing; then the paginated endpoint.
    // Every candidate goes through the same BindSignedPartAssets check.
    private async Task<(IReadOnlyDictionary<string, Uri> Urls, bool AssetListChanged)> BindPartsAsync(
        GitHubRelease release,
        UpdaterConfiguration configuration,
        UpdateManifest manifest,
        GitHubAsset manifestAsset,
        GitHubAsset signatureAsset,
        CachedReleaseMetadata? cached,
        CancellationToken cancellationToken)
    {
        try
        {
            return (BindSignedPartAssets(manifest, AssetDictionary(release.Assets)), false);
        }
        catch (InvalidDataException) when (!release.AssetListIsComplete)
        {
        }

        if (cached is not null && CachedAssetsAgree(cached.Assets, release.Assets, manifestAsset, signatureAsset))
        {
            try
            {
                return (BindSignedPartAssets(manifest, AssetDictionary(cached.Assets)), false);
            }
            catch (InvalidDataException)
            {
            }
        }

        await LoadCompleteAssetListAsync(configuration, release, cancellationToken);
        if (release.Assets.GroupBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
        {
            throw new InvalidDataException($"GitHub release {release.TagName} contains duplicate asset names.");
        }
        Dictionary<string, GitHubAsset> complete = AssetDictionary(release.Assets);
        if (!complete.TryGetValue(configuration.ManifestAsset, out GitHubAsset? listedManifest)
            || !complete.TryGetValue(configuration.SignatureAsset, out GitHubAsset? listedSignature)
            || !ReleaseMetadataCache.SameAsset(listedManifest, manifestAsset)
            || !ReleaseMetadataCache.SameAsset(listedSignature, signatureAsset))
        {
            throw new InvalidDataException($"GitHub release {release.TagName} changed its signed manifest assets during the update check.");
        }
        return (BindSignedPartAssets(manifest, complete), true);
    }

    private static Dictionary<string, GitHubAsset> AssetDictionary(IEnumerable<GitHubAsset> assets)
    {
        var result = new Dictionary<string, GitHubAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (GitHubAsset asset in assets)
        {
            if (asset is not null && !result.TryAdd(asset.Name, asset))
            {
                throw new InvalidDataException("GitHub release contains duplicate asset names.");
            }
        }
        return result;
    }

    // A saved asset list is only a stand-in for the paginated endpoint while
    // it still describes the live release: same manifest and signature asset,
    // and identical entries for every asset the live listing shows.
    private static bool CachedAssetsAgree(
        IReadOnlyList<GitHubAsset> cachedAssets,
        IReadOnlyList<GitHubAsset> liveAssets,
        GitHubAsset manifestAsset,
        GitHubAsset signatureAsset)
    {
        Dictionary<string, GitHubAsset> cachedByName;
        try
        {
            cachedByName = AssetDictionary(cachedAssets);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        return cachedByName.TryGetValue(manifestAsset.Name, out GitHubAsset? cachedManifest)
            && ReleaseMetadataCache.SameAsset(cachedManifest, manifestAsset)
            && cachedByName.TryGetValue(signatureAsset.Name, out GitHubAsset? cachedSignature)
            && ReleaseMetadataCache.SameAsset(cachedSignature, signatureAsset)
            && liveAssets.All(live => live is not null
                && cachedByName.TryGetValue(live.Name, out GitHubAsset? saved)
                && ReleaseMetadataCache.SameAsset(saved, live));
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

    private Task<byte[]> DownloadBytesAsync(
        Uri source,
        int maximumBytes,
        long expectedBytes,
        string what,
        CancellationToken cancellationToken) =>
        ResilientIo.WithRetriesAsync(
            Policy,
            what,
            async attemptToken =>
            {
                using HttpResponseMessage response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, attemptToken);
                HttpResponses.EnsureSuccess(response, what);
                byte[] bytes = await ReadBoundedBytesAsync(response.Content, maximumBytes, Policy.MetadataIdleTimeout, attemptToken);
                if (bytes.LongLength != expectedBytes)
                {
                    throw new InvalidDataException($"GitHub asset size differs from its release metadata. Expected {expectedBytes:N0}, got {bytes.LongLength:N0} bytes.");
                }
                return bytes;
            },
            Log,
            RetryNotice,
            RemainingCheckBudget,
            cancellationToken);

    private static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        int maximumBytes,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException($"GitHub response exceeds the {maximumBytes:N0}-byte updater safety limit.");
        }

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await ResilientIo.ReadAsync(input, buffer, idle, idleTimeout, cancellationToken)) > 0)
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
