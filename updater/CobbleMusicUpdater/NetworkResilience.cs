using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;

namespace CobbleMusicUpdater;

// Network limits for 1.2.18 (design: updater0923 bootstrap-path RC3/RC4 and
// lock-lifecycle section 6). HttpClient.Timeout only bounds the wait for
// response headers when ResponseHeadersRead is used, so every body read gets
// its own inactivity timeout, transient failures are retried a bounded number
// of times, and the release check as a whole has a wall-clock budget.
internal sealed record NetworkPolicy
{
    internal static NetworkPolicy Default { get; } = new();

    // Longest silence tolerated between two successful body reads.
    internal TimeSpan MetadataIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan PayloadIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    // Release index + asset lists + manifests together. On expiry the check
    // fails with TimeoutException, which Program already maps to the offline
    // fallback (nothing has been mutated at that point).
    internal TimeSpan ReleaseCheckBudget { get; init; } = TimeSpan.FromSeconds(90);

    // Total attempts per request, including the first one.
    internal int MaxAttempts { get; init; } = 3;

    // Base wait before attempt 2, 3, ...; the last value repeats if needed.
    internal IReadOnlyList<TimeSpan> Backoff { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    // Each wait is stretched by a random 0..JitterFraction share so several
    // friends behind one IP do not retry in lock step.
    internal double JitterFraction { get; init; } = 0.5;

    // A Retry-After longer than this is not waited out: failing now lets the
    // launch continue (offline fallback) instead of hanging Prism.
    internal TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);
}

// An unsuccessful HTTP status, with the GitHub rate-limit headers that
// explain it. Deriving from HttpRequestException keeps every existing
// "expected network failure" classification unchanged.
internal sealed class GitHubHttpException : HttpRequestException
{
    internal GitHubHttpException(
        string message,
        HttpStatusCode statusCode,
        int? rateLimitRemaining,
        DateTimeOffset? rateLimitReset,
        TimeSpan? retryAfter)
        : base(message, null, statusCode)
    {
        RateLimitRemaining = rateLimitRemaining;
        RateLimitReset = rateLimitReset;
        RetryAfter = retryAfter;
    }

    internal int? RateLimitRemaining { get; }
    internal DateTimeOffset? RateLimitReset { get; }
    internal TimeSpan? RetryAfter { get; }

    // 429 is always a rate limit. GitHub signals its primary limit as 403 with
    // X-RateLimit-Remaining: 0 and its secondary limit as 403 with Retry-After;
    // any other 403 is a real permission problem.
    internal bool IsRateLimited =>
        StatusCode == HttpStatusCode.TooManyRequests
        || (StatusCode == HttpStatusCode.Forbidden && (RateLimitRemaining == 0 || RetryAfter is not null));
}

// A body that ended before its signed size without a transport error. The
// partial file is kept so the next attempt resumes it with a Range request.
internal sealed class TruncatedDownloadException : IOException
{
    internal TruncatedDownloadException(string message) : base(message) { }
}

internal static class HttpResponses
{
    internal static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response, what);
        }
    }

    internal static GitHubHttpException Failure(HttpResponseMessage response, string what)
    {
        int? remaining = ReadRateLimitRemaining(response);
        DateTimeOffset? reset = ReadRateLimitReset(response);
        TimeSpan? retryAfter = ReadRetryAfter(response);
        string message = $"{what} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd()
            + DescribeRateLimit(remaining, reset, retryAfter) + ".";
        return new GitHubHttpException(message, response.StatusCode, remaining, reset, retryAfter);
    }

    internal static string DescribeStatus(HttpResponseMessage response) =>
        $"HTTP {(int)response.StatusCode}"
        + DescribeRateLimit(ReadRateLimitRemaining(response), ReadRateLimitReset(response), ReadRetryAfter(response));

    private static string DescribeRateLimit(int? remaining, DateTimeOffset? reset, TimeSpan? retryAfter)
    {
        var parts = new List<string>();
        if (remaining is int left)
        {
            parts.Add($"rate limit remaining {left}");
        }
        if (reset is DateTimeOffset when)
        {
            parts.Add($"resets {when.ToLocalTime():HH:mm:ss}");
        }
        if (retryAfter is TimeSpan wait)
        {
            parts.Add($"Retry-After {wait.TotalSeconds:0.#} s");
        }
        return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
    }

    internal static int? ReadRateLimitRemaining(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int remaining)
            ? remaining
            : null;

    internal static DateTimeOffset? ReadRateLimitReset(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? values)
        && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
        && seconds > 0
        && seconds < 253402300800L
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        if (header?.Date is DateTimeOffset date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }
}

internal static class ResilientIo
{
    // Reads once with an inactivity limit. The caller creates `idle` once per
    // response, linked to its own token; CancelAfter is re-armed before every
    // read, so the limit measures silence, never total transfer time.
    internal static async ValueTask<int> ReadAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationTokenSource idle,
        TimeSpan idleTimeout,
        CancellationToken outer)
    {
        idle.CancelAfter(idleTimeout);
        try
        {
            return await input.ReadAsync(buffer, idle.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException
            && idle.IsCancellationRequested
            && !outer.IsCancellationRequested)
        {
            // Some transports surface the cancellation as an IOException; both
            // mean the idle timer fired, not that the caller gave up.
            throw new TimeoutException(
                $"No data was received for {idleTimeout.TotalSeconds:0.###} seconds.",
                exception);
        }
    }

    // True for failures that another attempt may cure. Integrity and protocol
    // failures (InvalidDataException) are never retried, and neither is
    // anything once the caller's own token (or the check budget) has fired.
    internal static bool IsTransient(Exception exception, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return false;
        }
        return exception switch
        {
            GitHubHttpException { StatusCode: HttpStatusCode code } => code == HttpStatusCode.TooManyRequests || (int)code >= 500,
            GitHubHttpException => false,
            HttpRequestException => true,
            TimeoutException => true,
            // HttpClient.Timeout while waiting for headers.
            OperationCanceledException => true,
            IOException => true,
            _ => false
        };
    }

    internal static TimeSpan DelayBefore(NetworkPolicy policy, int failedAttempt, Exception exception, Func<double> random)
    {
        IReadOnlyList<TimeSpan> backoff = policy.Backoff;
        TimeSpan baseDelay = backoff.Count == 0
            ? TimeSpan.Zero
            : backoff[Math.Min(failedAttempt - 1, backoff.Count - 1)];
        TimeSpan delay = baseDelay * (1D + policy.JitterFraction * random());
        if (exception is GitHubHttpException { RetryAfter: TimeSpan retryAfter } && retryAfter > delay)
        {
            delay = retryAfter;
        }
        return delay;
    }

    internal static async Task<T> WithRetriesAsync<T>(
        NetworkPolicy policy,
        string what,
        Func<CancellationToken, Task<T>> operation,
        Action<string>? log,
        Action<string>? notice,
        Func<TimeSpan?>? remainingBudget,
        CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(token);
            }
            catch (Exception exception) when (attempt < policy.MaxAttempts && IsTransient(exception, token))
            {
                TimeSpan delay = DelayBefore(policy, attempt, exception, Random.Shared.NextDouble);
                TimeSpan? left = remainingBudget?.Invoke();
                if (delay > policy.MaxRetryAfter || (left is TimeSpan budget && delay >= budget))
                {
                    log?.Invoke($"{what} failed ({Describe(exception)}); not retrying because the required wait of {delay.TotalSeconds:0.#} s exceeds the remaining allowance.");
                    ExceptionDispatchInfo.Capture(exception).Throw();
                }
                log?.Invoke($"{what} failed ({Describe(exception)}); retrying in {delay.TotalSeconds:0.#} s (attempt {attempt + 1} of {policy.MaxAttempts}).");
                notice?.Invoke(NoticeFor(exception));
                await Task.Delay(delay, token);
            }
        }
    }

    internal static string NoticeFor(Exception exception) => exception switch
    {
        GitHubHttpException { IsRateLimited: true } => "GitHub is busy — retrying…",
        GitHubHttpException => "GitHub had a problem — retrying…",
        TimeoutException or OperationCanceledException => "Connection stalled — retrying…",
        _ => "Connection interrupted — retrying…"
    };

    internal static string Describe(Exception exception) =>
        exception is GitHubHttpException or TimeoutException or TruncatedDownloadException
            ? exception.Message
            : $"{exception.GetType().Name}: {exception.Message}";
}

// Log and card text for a release check that ended in the offline fallback
// or the "blocked" policy. Only the text changes; the launch decision stays
// in Program.
internal static class ReleaseCheckDiagnostics
{
    internal static GitHubHttpException? FindHttpFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is GitHubHttpException http)
            {
                return http;
            }
        }
        return null;
    }

    internal static string Describe(Exception exception)
    {
        if (FindHttpFailure(exception) is GitHubHttpException http)
        {
            return http.Message;
        }
        return exception is TimeoutException
            ? $"{nameof(TimeoutException)}: {exception.Message}"
            : exception.GetType().Name;
    }

    internal static string FallbackCardMessage(Exception exception) =>
        FindHttpFailure(exception) is { IsRateLimited: true } http
            ? http.RateLimitReset is DateTimeOffset reset
                ? $"GitHub is limiting update checks from this network until {reset.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)} — starting your current pack."
                : "GitHub is limiting update checks from this network right now — starting your current pack."
            : "Couldn’t check for updates — starting Minecraft.";
}

internal static class Deadline
{
    internal static long After(TimeSpan span) =>
        Stopwatch.GetTimestamp() + (long)(span.TotalSeconds * Stopwatch.Frequency);

    internal static TimeSpan Remaining(long deadline) =>
        TimeSpan.FromSeconds(Math.Max(0L, deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
}
