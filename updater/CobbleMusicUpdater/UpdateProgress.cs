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
    int TotalItems = 0);
