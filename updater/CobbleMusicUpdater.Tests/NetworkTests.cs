using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CobbleMusicUpdater;

// 1.2.18 network track: idle timeouts, retries, the release-check budget,
// the verified release-metadata cache, off-path skipping and rate-limit
// diagnostics. Every GitHub and CDN response comes from an in-process fake.
internal static partial class Program
{
    private const string NetworkRepository = "owner/repository";

    private static async Task TestNetworkResilienceAsync(string root)
    {
        TestRetryDelayPolicy();
        await TestStalledPayloadBodyRetriesAndResumesAsync(Path.Combine(root, "stalled"));
        await TestRetryStatusPolicyAsync(Path.Combine(root, "status"));
        await TestReleaseCheckBudgetAsync(Path.Combine(root, "budget"));
        await TestVerifiedReleaseMetadataCacheAsync(Path.Combine(root, "cache"));
        await TestUnreachableReleaseSkippingAsync(Path.Combine(root, "skip"));
        await TestRateLimitDiagnosticsAsync(Path.Combine(root, "rate-limit"));
        Console.WriteLine("Network resilience checks passed: idle timeouts, retries, check budget, verified metadata cache, off-path skipping, rate-limit diagnostics.");
    }

    private static NetworkPolicy FastNetworkPolicy() => new()
    {
        MetadataIdleTimeout = TimeSpan.FromMilliseconds(250),
        PayloadIdleTimeout = TimeSpan.FromMilliseconds(250),
        ReleaseCheckBudget = TimeSpan.FromSeconds(20),
        MaxAttempts = 3,
        Backoff = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)],
        JitterFraction = 0,
        MaxRetryAfter = TimeSpan.FromSeconds(2)
    };

    private static void TestRetryDelayPolicy()
    {
        NetworkPolicy policy = NetworkPolicy.Default;
        Equal(TimeSpan.FromSeconds(30), policy.MetadataIdleTimeout, "metadata idle timeout");
        Equal(TimeSpan.FromSeconds(60), policy.PayloadIdleTimeout, "payload idle timeout");
        Equal(TimeSpan.FromSeconds(90), policy.ReleaseCheckBudget, "release check budget");
        Equal(3, policy.MaxAttempts, "three attempts per request");
        var transient = new IOException("reset");
        Equal(TimeSpan.FromSeconds(2), ResilientIo.DelayBefore(policy, 1, transient, () => 0D), "first backoff without jitter");
        Equal(TimeSpan.FromSeconds(3), ResilientIo.DelayBefore(policy, 1, transient, () => 1D), "first backoff with full jitter");
        Equal(TimeSpan.FromSeconds(5), ResilientIo.DelayBefore(policy, 2, transient, () => 0D), "second backoff");
        Equal(TimeSpan.FromSeconds(5), ResilientIo.DelayBefore(policy, 7, transient, () => 0D), "last backoff repeats");
        var limited = new GitHubHttpException("429", HttpStatusCode.TooManyRequests, null, null, TimeSpan.FromSeconds(30));
        Equal(TimeSpan.FromSeconds(30), ResilientIo.DelayBefore(policy, 1, limited, () => 1D), "Retry-After longer than backoff wins");

        Equal(true, ResilientIo.IsTransient(new IOException("x"), NoCancellation), "IOException retried");
        Equal(true, ResilientIo.IsTransient(new TimeoutException("x"), NoCancellation), "timeout retried");
        Equal(true, ResilientIo.IsTransient(new HttpRequestException("dns"), NoCancellation), "transport failure retried");
        Equal(true, ResilientIo.IsTransient(new TaskCanceledException("header timeout"), NoCancellation), "HttpClient.Timeout retried");
        Equal(true, ResilientIo.IsTransient(limited, NoCancellation), "429 retried");
        Equal(true, ResilientIo.IsTransient(new GitHubHttpException("503", HttpStatusCode.ServiceUnavailable, null, null, null), NoCancellation), "5xx retried");
        Equal(false, ResilientIo.IsTransient(new GitHubHttpException("404", HttpStatusCode.NotFound, null, null, null), NoCancellation), "404 not retried");
        Equal(false, ResilientIo.IsTransient(new GitHubHttpException("403", HttpStatusCode.Forbidden, 0, null, null), NoCancellation), "403 not retried");
        Equal(false, ResilientIo.IsTransient(new InvalidDataException("bad signature"), NoCancellation), "integrity failure not retried");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Equal(false, ResilientIo.IsTransient(new IOException("x"), cancelled.Token), "nothing retried after the caller cancelled");
    }

    private static async Task TestStalledPayloadBodyRetriesAndResumesAsync(string root)
    {
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "payload.part001");
        var ranges = new List<long?>();
        var handler = new StubHttpHandler(request =>
        {
            long? from = request.Headers.Range?.Ranges.Single().From;
            lock (ranges)
            {
                ranges.Add(from);
            }
            if (from is null)
            {
                // Headers arrive, three bytes arrive, then the body stalls.
                var content = new StreamContent(new StallingStream("abc"u8.ToArray()));
                content.Headers.ContentLength = 6;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("def"u8.ToArray())
            };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return partial;
        });
        var logs = new List<string>();
        var notices = new List<string>();
        var timer = Stopwatch.StartNew();
        using (var client = new ReleaseClient(handler, TimeSpan.FromSeconds(5))
        {
            Policy = FastNetworkPolicy(),
            DiagnosticLog = logs.Add,
            RetryNotice = notices.Add
        })
        {
            await client.DownloadFileAsync(new Uri("https://example.invalid/payload.part001"), destination, 6, null, NoCancellation);
        }
        Equal("abcdef", await File.ReadAllTextAsync(destination), "stalled body resumed to the exact signed bytes");
        Equal(2, ranges.Count, "one retry after the stall");
        Equal(null, ranges[0], "first attempt starts at byte zero");
        Equal(3L, ranges[1], "retry resumes from the kept partial with Range");
        Equal(true, timer.Elapsed < TimeSpan.FromSeconds(10), "stall detected by the idle timeout, not a hang");
        Equal(true, logs.Any(line => line.Contains("No data was received", StringComparison.Ordinal) && line.Contains("retrying", StringComparison.Ordinal)), "stall retry logged");
        Equal("Connection stalled — retrying…", notices.Single(), "stall retry shown on the card");

        // The idle timer measures silence, not total time: a body that trickles
        // one byte every 100 ms for 1.2 s never trips a 250 ms idle limit.
        File.Delete(destination);
        int requests = 0;
        var trickle = new StubHttpHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            var content = new StreamContent(new TrickleStream("abcdefghijkl"u8.ToArray(), TimeSpan.FromMilliseconds(100)));
            content.Headers.ContentLength = 12;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using (var client = new ReleaseClient(trickle, TimeSpan.FromSeconds(5)) { Policy = FastNetworkPolicy() })
        {
            await client.DownloadFileAsync(new Uri("https://example.invalid/slow"), destination, 12, null, NoCancellation);
        }
        Equal(1, requests, "a slow but moving body is never retried");
        Equal("abcdefghijkl", await File.ReadAllTextAsync(destination), "trickled body complete");
    }

    private static async Task TestRetryStatusPolicyAsync(string root)
    {
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "asset.part");

        // 429 with Retry-After: the wait honours the header, not the 10 ms backoff.
        int rateLimited = 0;
        var retryAfter = new StubHttpHandler(_ =>
        {
            if (Interlocked.Increment(ref rateLimited) == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(700));
                return limited;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("abcdef"u8.ToArray()) };
        });
        var logs = new List<string>();
        var timer = Stopwatch.StartNew();
        using (var client = new ReleaseClient(retryAfter, TimeSpan.FromSeconds(5)) { Policy = FastNetworkPolicy(), DiagnosticLog = logs.Add })
        {
            await client.DownloadFileAsync(new Uri("https://example.invalid/asset"), destination, 6, null, NoCancellation);
        }
        Equal(2, rateLimited, "429 retried once");
        Equal(true, timer.Elapsed >= TimeSpan.FromMilliseconds(650), "Retry-After honoured");
        Equal("abcdef", await File.ReadAllTextAsync(destination), "download completes after 429");
        Equal(true, logs.Any(line => line.Contains("HTTP 429", StringComparison.Ordinal) && line.Contains("Retry-After 0.7 s", StringComparison.Ordinal)), "429 status and Retry-After logged");

        // A Retry-After beyond the cap is not waited out.
        File.Delete(destination);
        int longWait = 0;
        var tooLong = new StubHttpHandler(_ =>
        {
            Interlocked.Increment(ref longWait);
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return limited;
        });
        using (var client = new ReleaseClient(tooLong, TimeSpan.FromSeconds(5)) { Policy = FastNetworkPolicy() })
        {
            await ThrowsAsync<GitHubHttpException>(() => client.DownloadFileAsync(
                new Uri("https://example.invalid/asset"), destination, 6, null, NoCancellation));
        }
        Equal(1, longWait, "ten-minute Retry-After fails at once instead of hanging the launch");

        // 5xx: three attempts, then the failure surfaces.
        int unavailable = 0;
        var serverError = new StubHttpHandler(_ =>
        {
            Interlocked.Increment(ref unavailable);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using (var client = new ReleaseClient(serverError, TimeSpan.FromSeconds(5)) { Policy = FastNetworkPolicy() })
        {
            await ThrowsAsync<HttpRequestException>(() => client.DownloadFileAsync(
                new Uri("https://example.invalid/asset"), destination, 6, null, NoCancellation));
        }
        Equal(3, unavailable, "5xx attempted exactly three times");

        // 4xx other than 429 and protocol violations are not retried.
        int missing = 0;
        var notFound = new StubHttpHandler(_ =>
        {
            Interlocked.Increment(ref missing);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using (var client = new ReleaseClient(notFound, TimeSpan.FromSeconds(5)) { Policy = FastNetworkPolicy() })
        {
            await ThrowsAsync<GitHubHttpException>(() => client.DownloadFileAsync(
                new Uri("https://example.invalid/asset"), destination, 6, null, NoCancellation));
        }
        Equal(1, missing, "404 not retried");

        await File.WriteAllTextAsync(destination, "abc");
        int badRange = 0;
        var invalidRange = new StubHttpHandler(_ =>
        {
            Interlocked.Increment(ref badRange);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent("def"u8.ToArray()) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 4, 6);
            return response;
        });
        using (var client = new ReleaseClient(invalidRange, TimeSpan.FromSeconds(5)) { Policy = FastNetworkPolicy() })
        {
            await ThrowsAsync<InvalidDataException>(() => client.DownloadFileAsync(
                new Uri("https://example.invalid/asset"), destination, 6, null, NoCancellation));
        }
        Equal(1, badRange, "protocol violation not retried");
        Equal(false, File.Exists(destination), "protocol violation still deletes the partial");
    }

    private static async Task TestReleaseCheckBudgetAsync(string root)
    {
        // The release index stalls; the idle limit (30 s) is far away, so only
        // the check budget can end it.
        var stalledIndex = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream("[{"u8.ToArray()))
        });
        UpdaterConfiguration configuration = NetworkConfiguration();
        NetworkPolicy policy = FastNetworkPolicy() with
        {
            MetadataIdleTimeout = TimeSpan.FromSeconds(30),
            ReleaseCheckBudget = TimeSpan.FromMilliseconds(400)
        };
        var timer = Stopwatch.StartNew();
        Exception? failure = null;
        using (var client = new ReleaseClient(stalledIndex, TimeSpan.FromSeconds(30)) { Policy = policy })
        {
            try
            {
                await client.GetUpdateChainAsync(configuration, new InstalledState(), NoCancellation);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        Equal(true, failure is TimeoutException, $"budget expiry is a TimeoutException (got {failure?.GetType().Name})");
        Equal(true, timer.Elapsed < TimeSpan.FromSeconds(10), "budget ends the check promptly");
        Equal(true, CobbleMusicUpdater.Program.IsExpectedNetworkFailure(failure!), "budget expiry takes the offline-fallback branch");
        Equal(0, CobbleMusicUpdater.Program.NetworkFailureExitCode(configuration, failure!), "offline fallback launches the local pack");
        Equal("Couldn’t check for updates — starting Minecraft.", ReleaseCheckDiagnostics.FallbackCardMessage(failure!), "budget expiry keeps the generic card text");

        // A caller cancellation is not converted into the fallback timeout.
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using (var client = new ReleaseClient(stalledIndex, TimeSpan.FromSeconds(30)) { Policy = policy with { ReleaseCheckBudget = TimeSpan.FromSeconds(30) } })
        {
            await ThrowsAsync<OperationCanceledException>(() => client.GetUpdateChainAsync(configuration, new InstalledState(), caller.Token));
        }

        // Budget expiry while only an off-path release is still pending: that
        // release is skipped and the check succeeds with the rest.
        using var signer = new ConvergenceTestSigner();
        List<NetworkFixtureRelease> releases = await CreateNetworkFixtureReleasesAsync(root, signer, "1.0.1", "1.0.2", "1.0.3");
        var github = new FakeGitHub(releases)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/1.0.1/cobble-music-update.json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream("{"u8.ToArray())) }
                : null
        };
        using (var client = github.CreateClient(policy with { ReleaseCheckBudget = TimeSpan.FromMilliseconds(1500) }, cacheDirectory: null, logs: null))
        {
            IReadOnlyList<RemoteRelease> chain = await client.GetUpdateChainAsync(configuration, InstalledWithLedger("1.0.2"), NoCancellation);
            Equal("1.0.3", chain[^1].Manifest.Version, "check completes without the stalled off-path release");
            Equal("modpack-v1.0.1", client.SkippedReleaseTags.Single(), "stalled off-path release skipped at budget expiry");
        }
    }

    private static async Task TestVerifiedReleaseMetadataCacheAsync(string root)
    {
        using var signer = new ConvergenceTestSigner();
        List<NetworkFixtureRelease> releases = await CreateNetworkFixtureReleasesAsync(root, signer, "1.0.1", "1.0.2");
        var github = new FakeGitHub(releases);
        UpdaterConfiguration configuration = NetworkConfiguration();
        string local = Path.Combine(root, "local-data");
        Directory.CreateDirectory(local);
        var cache = new ReleaseMetadataCache(local);
        var logs = new List<string>();

        async Task<ReleaseClient> RunAsync()
        {
            github.ResetCounts();
            ReleaseClient client = github.CreateClient(FastNetworkPolicy(), local, logs);
            await client.GetUpdateChainAsync(configuration, new InstalledState(), NoCancellation);
            return client;
        }

        using (ReleaseClient cold = await RunAsync())
        {
            Equal(1, github.Count("list"), "cold check: one release-list API call");
            Equal(0, github.Count("assets"), "nested assets avoid per-release /assets calls");
            Equal(2, github.Count("manifest"), "cold check downloads each manifest once");
            Equal(2, github.Count("signature"), "cold check downloads each signature once");
            Equal(2, cold.VerifiedReleases.Count, "cold check verifies both releases");
        }
        Equal(2, Directory.GetFiles(cache.DirectoryPath, "*.json").Length, "one cache entry per release");
        Equal(true, logs.Any(line => line.Contains("GitHub API release list page 1: HTTP 200 (rate limit remaining 57", StringComparison.Ordinal)), "API status and rate limit logged");

        using (ReleaseClient warm = await RunAsync())
        {
            Equal(1, github.Count("list"), "steady state: exactly one API call");
            Equal(0, github.Count("assets"), "steady state: no /assets calls");
            Equal(0, github.Count("manifest"), "steady state: zero manifest downloads");
            Equal(0, github.Count("signature"), "steady state: zero signature downloads");
            Equal(2, warm.VerifiedReleases.Count, "cached releases verified");
            foreach (NetworkFixtureRelease release in releases)
            {
                RemoteRelease verified = warm.VerifiedReleases.Single(candidate => candidate.Release.Id == release.Id);
                Equal(release.Signed.ManifestSha256, verified.ManifestSha256, $"cached {release.Version} has the exact signed identity");
                Equal(release.PartUrl, verified.AssetUrls["payload.part001"].ToString(), $"cached {release.Version} binds its part URL");
            }
        }

        // Tampered cached manifest bytes (same size, so only the signature can
        // notice) are rejected, evicted and fetched again.
        NetworkFixtureRelease first = releases[0];
        MutateCacheEntry(cache, first.Id, entry =>
        {
            byte[] manifest = Convert.FromBase64String(entry["manifestBase64"]!.GetValue<string>());
            manifest[manifest.Length / 2] ^= 0x01;
            entry["manifestBase64"] = Convert.ToBase64String(manifest);
        });
        logs.Clear();
        using (ReleaseClient tampered = await RunAsync())
        {
            Equal(1, github.Count("manifest"), "only the tampered release is downloaded again");
            Equal(1, github.Count("signature"), "its signature is downloaded again with it");
            Equal(first.Signed.ManifestSha256, tampered.VerifiedReleases.Single(candidate => candidate.Release.Id == first.Id).ManifestSha256, "refetched manifest is the genuine one");
        }
        Equal(true, logs.Any(line => line.Contains($"Cached metadata for {first.Tag} failed verification", StringComparison.Ordinal)), "tampered cache logged");
        using (await RunAsync())
        {
            Equal(0, github.Count("manifest"), "rewritten entry is used again");
        }

        // A tampered signature and an unreadable entry are rejected the same way.
        MutateCacheEntry(cache, first.Id, entry =>
        {
            byte[] signature = Convert.FromBase64String(entry["signatureBase64"]!.GetValue<string>());
            signature[signature.Length / 2] ^= 0x01;
            entry["signatureBase64"] = Convert.ToBase64String(signature);
        });
        using (await RunAsync())
        {
            Equal(1, github.Count("manifest"), "tampered cached signature forces a download");
        }
        await File.WriteAllTextAsync(cache.EntryPath(first.Id), "not json");
        using (await RunAsync())
        {
            Equal(1, github.Count("manifest"), "unreadable cache entry forces a download");
        }

        // A replaced asset (new updated_at in the live listing) invalidates the entry.
        releases[1].ManifestUpdatedAt = "2026-09-24T00:00:00Z";
        using (await RunAsync())
        {
            Equal(1, github.Count("manifest"), "replaced manifest asset is downloaded again");
        }

        // A listed sha256 digest that the cached bytes do not match rejects the entry.
        releases[1].ManifestDigest = "sha256:" + new string('0', 64);
        using (await RunAsync())
        {
            Equal(1, github.Count("manifest"), "digest mismatch rejects the cached bytes");
        }
        releases[1].ManifestDigest = "sha256:" + HashBytes(releases[1].Signed.ManifestBytes);
        using (await RunAsync())
        {
            Equal(0, github.Count("manifest"), "matching listed digest accepts the cached bytes");
        }

        // A release GitHub no longer lists loses its entry.
        github.Hidden.Add(first.Id);
        using (await RunAsync())
        {
            Equal(false, File.Exists(cache.EntryPath(first.Id)), "entry of an unlisted release pruned");
            Equal(true, File.Exists(cache.EntryPath(releases[1].Id)), "entry of a listed release kept");
        }
        github.Hidden.Clear();

        // Nested assets without the payload part (a truncated nested list): the
        // first check falls back to /assets once per release, later checks use
        // the asset list saved with the verified entry.
        string truncatedLocal = Path.Combine(root, "local-truncated");
        Directory.CreateDirectory(truncatedLocal);
        github.NestedWithoutParts = true;
        github.ResetCounts();
        using (ReleaseClient client = github.CreateClient(FastNetworkPolicy(), truncatedLocal, null))
        {
            await client.GetUpdateChainAsync(configuration, new InstalledState(), NoCancellation);
            Equal(2, github.Count("assets"), "missing nested part falls back to /assets");
            Equal(2, client.VerifiedReleases.Count, "parts bound from the complete asset list");
        }
        github.ResetCounts();
        using (ReleaseClient client = github.CreateClient(FastNetworkPolicy(), truncatedLocal, null))
        {
            await client.GetUpdateChainAsync(configuration, new InstalledState(), NoCancellation);
            Equal(1, github.Count("list"), "truncated nested list, warm cache: one API call");
            Equal(0, github.Count("assets"), "saved asset list stands in for /assets");
            Equal(0, github.Count("manifest"), "truncated nested list, warm cache: zero downloads");
            Equal(releases[0].PartUrl, client.VerifiedReleases.Single(candidate => candidate.Release.Id == releases[0].Id).AssetUrls["payload.part001"].ToString(), "saved asset list binds the same part URL");
        }
        github.NestedWithoutParts = false;
    }

    private static async Task TestUnreachableReleaseSkippingAsync(string root)
    {
        using var signer = new ConvergenceTestSigner();
        List<NetworkFixtureRelease> releases = await CreateNetworkFixtureReleasesAsync(root, signer, "1.0.1", "1.0.2", "1.0.3");
        UpdaterConfiguration configuration = NetworkConfiguration();
        Version newest = new(1, 0, 3);
        Equal(true, ReleaseClient.IsOnUpdatePath(new Version(1, 0, 3), newest, InstalledWithLedger("1.0.2")), "newest release is on the path");
        Equal(true, ReleaseClient.IsOnUpdatePath(new Version(1, 0, 2), newest, InstalledWithLedger("1.0.2")), "installed anchor is on the path");
        Equal(false, ReleaseClient.IsOnUpdatePath(new Version(1, 0, 1), newest, InstalledWithLedger("1.0.2")), "older release is off the path");
        Equal(true, ReleaseClient.IsOnUpdatePath(new Version(1, 0, 1), newest, new InstalledState()), "fresh install: every release is on the path");
        Equal(true, ReleaseClient.IsOnUpdatePath(new Version(1, 0, 1), newest, new InstalledState { Version = "1.0.2" }), "legacy state without a seed ledger: older releases are needed");
        Equal(true, ReleaseClient.IsOnUpdatePath(new Version(1, 0, 3), newest, InstalledWithLedger("1.0.9")), "newest release stays on the path even below the installed version");

        int oldManifestRequests = 0;
        HttpResponseMessage? FailOld(HttpRequestMessage request)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/1.0.1/cobble-music-update.json", StringComparison.Ordinal))
            {
                return null;
            }
            Interlocked.Increment(ref oldManifestRequests);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        var github = new FakeGitHub(releases) { Override = FailOld };
        var logs = new List<string>();
        using (ReleaseClient client = github.CreateClient(FastNetworkPolicy(), null, logs))
        {
            IReadOnlyList<RemoteRelease> chain = await client.GetUpdateChainAsync(configuration, InstalledWithLedger("1.0.2"), NoCancellation);
            Equal(2, client.VerifiedReleases.Count, "reachable releases verified");
            Equal("modpack-v1.0.1", client.SkippedReleaseTags.Single(), "unreachable off-path release skipped");
            Equal("1.0.3", chain[^1].Manifest.Version, "chain still reaches the newest release");
        }
        Equal(3, oldManifestRequests, "the skipped release was tried three times first");
        Equal(true, logs.Any(line => line.StartsWith("Skipped unreachable release modpack-v1.0.1", StringComparison.Ordinal) && line.Contains("HTTP 503", StringComparison.Ordinal)), "skip logged with its HTTP status");

        using (ReleaseClient client = github.CreateClient(FastNetworkPolicy(), null, null))
        {
            await ThrowsAsync<HttpRequestException>(() => client.GetUpdateChainAsync(configuration, new InstalledState(), NoCancellation));
        }
        using (ReleaseClient client = github.CreateClient(FastNetworkPolicy(), null, null))
        {
            await ThrowsAsync<HttpRequestException>(() => client.GetUpdateChainAsync(configuration, new InstalledState { Version = "1.0.2" }, NoCancellation));
        }

        github.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/1.0.3/cobble-music-update.json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : null;
        using (ReleaseClient client = github.CreateClient(FastNetworkPolicy(), null, null))
        {
            await ThrowsAsync<HttpRequestException>(() => client.GetUpdateChainAsync(configuration, InstalledWithLedger("1.0.2"), NoCancellation));
        }

        // An integrity failure is never skipped, even for an old release.
        byte[] forged = releases[0].Signed.ManifestBytes.ToArray();
        forged[forged.Length / 2] ^= 0x01;
        github.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/1.0.1/cobble-music-update.json", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(forged) }
            : null;
        using (ReleaseClient client = github.CreateClient(FastNetworkPolicy(), null, null))
        {
            await ThrowsAsync<InvalidDataException>(() => client.GetUpdateChainAsync(configuration, InstalledWithLedger("1.0.2"), NoCancellation));
        }
    }

    private static async Task TestRateLimitDiagnosticsAsync(string root)
    {
        Directory.CreateDirectory(root);
        UpdaterConfiguration configuration = NetworkConfiguration();
        DateTimeOffset reset = new(2026, 9, 23, 18, 30, 0, TimeSpan.Zero);
        int listRequests = 0;
        var exhausted = new StubHttpHandler(_ =>
        {
            Interlocked.Increment(ref listRequests);
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "60");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        });
        Exception? failure = null;
        var logs = new List<string>();
        using (var client = new ReleaseClient(exhausted, TimeSpan.FromSeconds(5)) { Policy = FastNetworkPolicy(), DiagnosticLog = logs.Add })
        {
            try
            {
                await client.GetUpdateChainAsync(configuration, new InstalledState(), NoCancellation);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        Equal(1, listRequests, "exhausted primary rate limit is not retried");
        var http = failure as GitHubHttpException;
        Equal(true, http is { IsRateLimited: true, StatusCode: HttpStatusCode.Forbidden, RateLimitRemaining: 0 }, "403 with Remaining 0 is a rate limit");
        Equal(reset, http!.RateLimitReset, "reset time parsed");
        Equal(true, CobbleMusicUpdater.Program.IsExpectedNetworkFailure(failure!), "rate limit still takes the offline-fallback branch");
        string card = ReleaseCheckDiagnostics.FallbackCardMessage(failure!);
        Equal($"GitHub is limiting update checks from this network until {reset.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture)} — starting your current pack.", card, "specific card text for a 403 rate limit");
        string described = ReleaseCheckDiagnostics.Describe(failure!);
        Equal(true, described.Contains("HTTP 403", StringComparison.Ordinal) && described.Contains("rate limit remaining 0", StringComparison.Ordinal), $"log names status and remaining quota: {described}");
        Equal(true, logs.Any(line => line.Contains("HTTP 403 (rate limit remaining 0, resets", StringComparison.Ordinal)), "API response logged with rate-limit headers");

        int tooMany = 0;
        var secondary = new StubHttpHandler(_ =>
        {
            Interlocked.Increment(ref tooMany);
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        failure = null;
        using (var client = new ReleaseClient(secondary, TimeSpan.FromSeconds(5)) { Policy = FastNetworkPolicy() })
        {
            try
            {
                await client.GetUpdateChainAsync(configuration, new InstalledState(), NoCancellation);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        Equal(3, tooMany, "429 on the release list is retried three times");
        Equal("GitHub is limiting update checks from this network right now — starting your current pack.", ReleaseCheckDiagnostics.FallbackCardMessage(failure!), "specific card text for a 429");

        var forbidden = new GitHubHttpException("x", HttpStatusCode.Forbidden, 42, null, null);
        Equal(false, forbidden.IsRateLimited, "403 with quota left is not a rate limit");
        Equal("Couldn’t check for updates — starting Minecraft.", ReleaseCheckDiagnostics.FallbackCardMessage(forbidden), "plain 403 keeps the generic card text");
        Equal("Couldn’t check for updates — starting Minecraft.", ReleaseCheckDiagnostics.FallbackCardMessage(new HttpRequestException("dns")), "network error keeps the generic card text");
        Equal("HttpRequestException", ReleaseCheckDiagnostics.Describe(new HttpRequestException("dns")), "other failures keep the type-name log text");
    }

    private static UpdaterConfiguration NetworkConfiguration()
    {
        UpdaterConfiguration configuration = Configuration();
        configuration.Repository = NetworkRepository;
        return configuration;
    }

    private static InstalledState InstalledWithLedger(string version) => new()
    {
        Version = version,
        ManifestSha256 = new string('a', 64),
        OfferedSeedPaths = ["config/example.txt"]
    };

    private static void MutateCacheEntry(ReleaseMetadataCache cache, long releaseId, Action<JsonObject> mutate)
    {
        string path = cache.EntryPath(releaseId);
        JsonObject entry = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        mutate(entry);
        File.WriteAllText(path, entry.ToJsonString());
    }

    private static async Task<List<NetworkFixtureRelease>> CreateNetworkFixtureReleasesAsync(
        string root,
        ConvergenceTestSigner signer,
        params string[] versions)
    {
        UpdaterPaths paths = Paths(Path.Combine(root, "fixture"));
        var releases = new List<NetworkFixtureRelease>();
        long id = 5000;
        foreach (string version in versions)
        {
            byte[] jar = ConvergenceFabricJar("network_fixture", version);
            RemoteRelease signed = await CreateSignedConvergenceReleaseAsync(paths,
                new UpdateManifest { SchemaVersion = 1, Version = version, Files = [ConvergenceFile("mods/network.jar", jar)] },
                new() { ["mods/network.jar"] = jar },
                signer);
            releases.Add(new NetworkFixtureRelease(++id, version, signed));
        }
        return releases;
    }

    private sealed class NetworkFixtureRelease(long id, string version, RemoteRelease signed)
    {
        public long Id { get; } = id;
        public string Version { get; } = version;
        public string Tag => "modpack-v" + Version;
        public RemoteRelease Signed { get; } = signed;
        public string ManifestUpdatedAt { get; set; } = "2026-09-01T00:00:00Z";
        public string? ManifestDigest { get; set; }
        public string BaseUrl => $"https://example.invalid/net/{Version}/";
        public string PartUrl => BaseUrl + "payload.part001";

        public List<GitHubAsset> Assets(bool includePart)
        {
            var assets = new List<GitHubAsset>
            {
                new()
                {
                    Id = Id * 10 + 1, Name = "cobble-music-update.json", Size = Signed.ManifestBytes.LongLength,
                    UpdatedAt = ManifestUpdatedAt, Digest = ManifestDigest, BrowserDownloadUrl = BaseUrl + "cobble-music-update.json"
                },
                new()
                {
                    Id = Id * 10 + 2, Name = "cobble-music-update.sig", Size = Signed.SignatureBytes.LongLength,
                    UpdatedAt = "2026-09-01T00:00:00Z", BrowserDownloadUrl = BaseUrl + "cobble-music-update.sig"
                }
            };
            if (includePart)
            {
                assets.Add(new()
                {
                    Id = Id * 10 + 3, Name = "payload.part001", Size = Signed.Manifest.Payload!.Parts[0].Size,
                    UpdatedAt = "2026-09-01T00:00:00Z", BrowserDownloadUrl = PartUrl
                });
            }
            return assets;
        }
    }

    // In-process stand-in for api.github.com and the release-asset CDN.
    private sealed class FakeGitHub(List<NetworkFixtureRelease> releases)
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
        public bool NestedWithoutParts { get; set; }
        public HashSet<long> Hidden { get; } = [];

        public int Count(string kind)
        {
            lock (_counts)
            {
                return _counts.GetValueOrDefault(kind);
            }
        }

        public void ResetCounts()
        {
            lock (_counts)
            {
                _counts.Clear();
            }
        }

        public ReleaseClient CreateClient(NetworkPolicy policy, string? cacheDirectory, List<string>? logs)
        {
            var client = new ReleaseClient(new StubHttpHandler(Handle), TimeSpan.FromSeconds(5)) { Policy = policy };
            if (cacheDirectory is not null)
            {
                client.MetadataCache = new ReleaseMetadataCache(cacheDirectory);
            }
            if (logs is not null)
            {
                client.DiagnosticLog = line =>
                {
                    lock (logs)
                    {
                        logs.Add(line);
                    }
                };
            }
            return client;
        }

        private void Hit(string kind)
        {
            lock (_counts)
            {
                _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
            }
        }

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            Uri uri = request.RequestUri!;
            string path = uri.AbsolutePath;
            if (uri.Host == "api.github.com" && path == $"/repos/{NetworkRepository}/releases")
            {
                Hit("list");
                HttpResponseMessage list = JsonResponse(releases
                    .Where(release => !Hidden.Contains(release.Id))
                    .Select(release => new GitHubRelease { Id = release.Id, TagName = release.Tag, Assets = release.Assets(!NestedWithoutParts) })
                    .ToList());
                list.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "57");
                list.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1790000000");
                return list;
            }
            if (uri.Host == "api.github.com" && path.EndsWith("/assets", StringComparison.Ordinal))
            {
                Hit("assets");
                long id = long.Parse(path.Split('/')[^2], System.Globalization.CultureInfo.InvariantCulture);
                return JsonResponse(releases.Single(release => release.Id == id).Assets(includePart: true));
            }
            if (Override?.Invoke(request) is HttpResponseMessage overridden)
            {
                Hit(path.EndsWith(".sig", StringComparison.Ordinal) ? "signature" : "manifest");
                return overridden;
            }
            NetworkFixtureRelease? owner = releases.SingleOrDefault(release => uri.ToString().StartsWith(release.BaseUrl, StringComparison.Ordinal));
            if (owner is not null && path.EndsWith("/cobble-music-update.json", StringComparison.Ordinal))
            {
                Hit("manifest");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(owner.Signed.ManifestBytes) };
            }
            if (owner is not null && path.EndsWith("/cobble-music-update.sig", StringComparison.Ordinal))
            {
                Hit("signature");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(owner.Signed.SignatureBytes) };
            }
            throw new InvalidOperationException($"Unexpected test URL: {uri}");
        }
    }

    // Returns its prefix, then blocks until the read is cancelled.
    private sealed class StallingStream(byte[] prefix) : Stream
    {
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                int count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Async reads only.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Returns one byte per read, each after a fixed pause.
    private sealed class TrickleStream(byte[] content, TimeSpan pause) : Stream
    {
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= content.Length || buffer.Length == 0)
            {
                return 0;
            }
            await Task.Delay(pause, cancellationToken);
            buffer.Span[0] = content[_position++];
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Async reads only.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
