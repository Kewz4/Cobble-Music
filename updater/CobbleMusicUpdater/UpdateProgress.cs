namespace CobbleMusicUpdater;

internal enum UpdatePhase
{
    Checking,
    VerifyingRelease,
    UpdateAvailable,
    Downloading,
    Reassembling,
    Validating,
    Applying,
    Complete,
    Fallback,
    Blocked
}

internal sealed record UpdateProgress(
    UpdatePhase Phase,
    string Message,
    long CompletedBytes = 0,
    long TotalBytes = 0,
    int CurrentItem = 0,
    int TotalItems = 0,
    long NetworkBytes = 0);

internal readonly record struct PartDownloadProgress(
    long DownloadedBytes,
    long NetworkBytes);

internal sealed class DownloadProgressScope
{
    internal DownloadProgressScope(long totalBytes)
    {
        if (totalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        }
        TotalBytes = totalBytes;
    }

    internal long CompletedBytes { get; private set; }
    internal long TotalBytes { get; private set; }
    internal long NetworkBytes { get; private set; }

    internal void ExcludeSkippedPayload(long bytes)
    {
        if (bytes < 0 || bytes > TotalBytes - CompletedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }
        TotalBytes -= bytes;
    }

    internal UpdateProgress ForPart(PartDownloadProgress partProgress) =>
        UpdateEngine.CreateDownloadProgress(
            CompletedBytes,
            TotalBytes,
            NetworkBytes,
            partProgress);

    internal void CompletePart(long partSize, long partNetworkBytes)
    {
        if (partSize < 0 || partSize > TotalBytes - CompletedBytes || partNetworkBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partSize));
        }
        CompletedBytes = checked(CompletedBytes + partSize);
        NetworkBytes = checked(NetworkBytes + partNetworkBytes);
    }
}
