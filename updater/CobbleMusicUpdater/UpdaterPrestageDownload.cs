using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace CobbleMusicUpdater;

/// <summary>
/// Resumable, time-bounded download of one signed updater executable. Unlike the pinned bootstrap's Invoke-WebRequest (whose
/// body reads can stall for 300 s) every body read has an inactivity timeout, transient failures are retried with backoff and
/// resume from the partial file, and the result is accepted only when it is exactly the signed size, SHA-256 and a PE ("MZ")
/// file - the same test as the bootstrap's Test-ExactExecutable.
/// </summary>
internal sealed class PrestageDownloader
{
    internal static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromSeconds(30);
    internal static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10)];

    private readonly HttpClient _http;
    private readonly TimeSpan _inactivityTimeout;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly Action<string> _log;

    public PrestageDownloader(HttpClient http, TimeSpan inactivityTimeout, IReadOnlyList<TimeSpan> retryDelays, Action<string> log)
    {
        _http = http;
        _inactivityTimeout = inactivityTimeout;
        _retryDelays = retryDelays;
        _log = log;
    }

    /// <summary>Downloads into <paramref name="partialPath"/> and moves it to <paramref name="finalPath"/> only after exact verification.</summary>
    public async Task DownloadVerifiedAsync(
        Uri source,
        string partialPath,
        string finalPath,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(source, partialPath, expectedSize, cancellationToken);
                if (!UpdaterPrestage.IsExactExecutable(partialPath, expectedSize, expectedSha256))
                {
                    throw new PrestageIntegrityException("Downloaded updater does not match the signed channel size, SHA-256, or executable format.");
                }
                File.Move(partialPath, finalPath, overwrite: true);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (exception is PrestageIntegrityException)
                {
                    // Bytes that broke the protocol or the hash must never be resumed from.
                    UpdaterPrestage.TryDelete(partialPath);
                }
                if (!IsRetryable(exception) || attempt >= _retryDelays.Count)
                {
                    throw;
                }
                _log($"Updater pre-staging: download attempt {attempt + 1} failed ({exception.GetType().Name}: {exception.Message}); retrying.");
                await Task.Delay(_retryDelays[attempt], cancellationToken);
            }
        }
    }

    internal static bool IsRetryable(Exception exception) => exception switch
    {
        PrestageIntegrityException => true,
        TimeoutException => true,
        IOException => true,
        // HttpClient.Timeout surfaces as TaskCanceledException when the caller's token was not cancelled.
        TaskCanceledException => true,
        HttpRequestException http => http.StatusCode is null
            || http.StatusCode == HttpStatusCode.RequestTimeout
            || http.StatusCode == HttpStatusCode.TooManyRequests
            || (int)http.StatusCode >= 500,
        _ => false
    };

    private async Task DownloadOnceAsync(Uri source, string partialPath, long expectedSize, CancellationToken cancellationToken)
    {
        long existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0L;
        if (existing > expectedSize)
        {
            File.Delete(partialPath);
            existing = 0L;
        }
        if (existing == expectedSize)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Updater download returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
        bool append = AcceptResponse(response, existing, expectedSize);

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(partialPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        long written = append ? existing : 0L;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            while (true)
            {
                int read;
                // A fresh deadline per read: a slow but moving download is fine, a stalled one is not.
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    readTimeout.CancelAfter(_inactivityTimeout);
                    try
                    {
                        read = await input.ReadAsync(buffer.AsMemory(), readTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No updater bytes arrived for {_inactivityTimeout.TotalSeconds:N0} seconds.");
                    }
                }
                if (read == 0)
                {
                    break;
                }
                if (written > expectedSize - read)
                {
                    throw new PrestageIntegrityException("Updater download exceeded its signed size.");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
            }
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (written != expectedSize)
        {
            // Connection closed early: keep the partial so the retry resumes from here.
            throw new IOException($"Updater download ended at {written:N0} of {expectedSize:N0} bytes.");
        }
    }

    /// <summary>The same resume rules as ReleaseClient.ValidateDownloadResponse: an exact Content-Range for 206, full length for 200.</summary>
    private static bool AcceptResponse(HttpResponseMessage response, long existing, long expectedSize)
    {
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (existing == 0
                || range is null
                || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                || range.From != existing
                || range.To != expectedSize - 1
                || range.Length != expectedSize
                || (response.Content.Headers.ContentLength is long partialLength && partialLength != expectedSize - existing))
            {
                throw new PrestageIntegrityException("Resume Content-Range does not match the signed size and local offset.");
            }
            return true;
        }
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new PrestageIntegrityException($"Unexpected successful HTTP status {(int)response.StatusCode}.");
        }
        if (response.Content.Headers.ContentLength is long fullLength && fullLength != expectedSize)
        {
            throw new PrestageIntegrityException("Content-Length does not match the signed updater size.");
        }
        // A server that ignores Range answers 200: restart from byte zero (FileMode.Create truncates).
        return false;
    }
}

internal sealed class PrestageIntegrityException(string message) : Exception(message);
