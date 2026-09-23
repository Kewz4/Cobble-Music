namespace CobbleMusicUpdater;

internal sealed partial class UpdateEngine
{
    // 1.2.18 net track: payload downloads retry by themselves (ReleaseClient).
    // Each retry is written to updater.log and shown on the card. The notice
    // is reported without byte totals so the card shows its text instead of
    // the percentage; the next progress report restores the percentage.
    private void AttachDownloadDiagnostics(ReleaseClient client)
    {
        client.DiagnosticLog = _log;
        client.RetryNotice = message => Report(UpdatePhase.Downloading, message);
    }
}
