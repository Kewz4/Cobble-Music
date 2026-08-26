using System.Globalization;

namespace CobbleMusicUpdater;

internal readonly record struct TransferMetrics(
    double BytesPerSecond,
    TimeSpan? EstimatedTimeRemaining)
{
    public bool HasEstimate => BytesPerSecond > 0 && EstimatedTimeRemaining is not null;
}

internal sealed class TransferMetricsTracker
{
    private readonly long _timestampFrequency;
    private bool _started;
    private long _lastSampleAt;
    private long _lastNetworkBytes;
    private double _smoothedBytesPerSecond;

    internal TransferMetricsTracker(long timestampFrequency)
    {
        if (timestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        }
        _timestampFrequency = timestampFrequency;
    }

    internal void Reset()
    {
        _started = false;
        _lastSampleAt = 0;
        _lastNetworkBytes = 0;
        _smoothedBytesPerSecond = 0D;
    }

    internal TransferMetrics Observe(UpdateProgress progress, long timestamp)
    {
        if (progress.Phase != UpdatePhase.Downloading || progress.TotalBytes <= 0)
        {
            Reset();
            return default;
        }

        if (!_started || progress.NetworkBytes < _lastNetworkBytes || timestamp < _lastSampleAt)
        {
            _started = true;
            _lastSampleAt = timestamp;
            _lastNetworkBytes = progress.NetworkBytes;
            _smoothedBytesPerSecond = 0D;
            return default;
        }

        double elapsedSeconds = (timestamp - _lastSampleAt) / (double)_timestampFrequency;
        if (elapsedSeconds >= 0.25D)
        {
            long transferredBytes = progress.NetworkBytes - _lastNetworkBytes;
            double instantaneousSpeed = transferredBytes / elapsedSeconds;
            if (_smoothedBytesPerSecond <= 0D)
            {
                _smoothedBytesPerSecond = instantaneousSpeed;
            }
            else
            {
                // A three-second exponential window keeps the readout stable while
                // still responding quickly to a real connection-speed change.
                double alpha = 1D - Math.Exp(-elapsedSeconds / 3D);
                _smoothedBytesPerSecond += alpha * (instantaneousSpeed - _smoothedBytesPerSecond);
            }
            _lastSampleAt = timestamp;
            _lastNetworkBytes = progress.NetworkBytes;
        }

        if (_smoothedBytesPerSecond <= 0D)
        {
            return default;
        }

        long remainingBytes = Math.Max(0L, progress.TotalBytes - progress.CompletedBytes);
        double remainingSeconds = remainingBytes / _smoothedBytesPerSecond;
        TimeSpan eta = TimeSpan.FromSeconds(Math.Min(remainingSeconds, TimeSpan.MaxValue.TotalSeconds));
        return new TransferMetrics(_smoothedBytesPerSecond, eta);
    }
}

internal static class TransferMetricsFormatter
{
    internal static string FormatDownloadDetail(UpdateProgress progress, TransferMetrics metrics)
    {
        long totalBytes = Math.Max(0L, progress.TotalBytes);
        long completedBytes = Math.Clamp(progress.CompletedBytes, 0L, totalBytes);
        string transferred = $"{FormatBytes(completedBytes)} / {FormatBytes(totalBytes)}";
        if (!metrics.HasEstimate)
        {
            return $"{transferred} • Calculating speed and ETA…";
        }

        return $"{transferred} • {FormatSpeed(metrics.BytesPerSecond)} • ETA {FormatDuration(metrics.EstimatedTimeRemaining!.Value)}";
    }

    internal static string FormatBytes(long bytes)
    {
        double value = Math.Max(0L, bytes);
        if (value >= 1024D * 1024D * 1024D)
        {
            return (value / (1024D * 1024D * 1024D)).ToString("0.00", CultureInfo.InvariantCulture) + " GiB";
        }
        if (value >= 1024D * 1024D)
        {
            return (value / (1024D * 1024D)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
        }
        if (value >= 1024D)
        {
            return (value / 1024D).ToString("0.0", CultureInfo.InvariantCulture) + " KiB";
        }
        return ((long)value).ToString(CultureInfo.InvariantCulture) + " B";
    }

    internal static string FormatSpeed(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0)
        {
            return "0 B/s";
        }
        return FormatBytes((long)Math.Round(bytesPerSecond)) + "/s";
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        double seconds = Math.Max(0D, duration.TotalSeconds);
        if (seconds < 1D)
        {
            return "<1s";
        }

        long roundedSeconds = (long)Math.Ceiling(seconds);
        if (roundedSeconds < 60)
        {
            return $"{roundedSeconds}s";
        }
        if (roundedSeconds < 3600)
        {
            return $"{roundedSeconds / 60}m {roundedSeconds % 60:00}s";
        }
        return $"{roundedSeconds / 3600}h {(roundedSeconds % 3600) / 60:00}m";
    }
}
