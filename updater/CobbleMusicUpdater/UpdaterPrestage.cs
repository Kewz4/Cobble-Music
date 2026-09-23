using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CobbleMusicUpdater;

// Updater pre-staging (1.2.18). Friends run the updater through a PINNED bootstrap
// (bootstrap/Bootstrap-CobbleMusicUpdater.ps1) that, whenever the signed stable channel names a
// new updater, downloads ~121 MB with Windows PowerShell 5.1 before any window exists. This file
// lets the running updater fetch the NEXT updater itself (signed updater/channel/next.json), verify
// it exactly like the bootstrap's Test-ExactExecutable, and hand it to a helper process that swaps
// it in after this process exits (UpdaterPrestageSwap.cs). The swap leaves the pinned bootstrap's
// own checks satisfied, so its next run starts the new updater with no download:
//   * Get-CachedChannel verifies installed-updater-channel.json/.sig (the exact signed next bytes)
//     with the pinned 1.2.7 verifier and finds CobbleMusicUpdater.exe matching size + SHA-256;
//   * a stable.json that still names the older version is then "Ignoring replayed updater channel";
//   * once stable.json advances with the same bytes, version, size and hash are equal, so nothing
//     is downloaded and the cache is rewritten with identical bytes.
// Every file decision is made from bytes verified with the compiled Ed25519 key; nothing unsigned
// (URLs, journal contents, local file names) is ever trusted to select an executable.

/// <summary>File layout shared with the pinned bootstrap (its $targetDirectory/$targetExe/$cached* paths) plus our own prestage folder.</summary>
internal sealed record PrestageLayout(string InstallDirectory)
{
    // Mirrors the bootstrap's $VerifierVersion (line 23) and "$targetDirectory\CobbleMusicUpdaterVerifier-$VerifierVersion.exe" (line 36).
    internal const string PinnedVerifierVersion = "1.2.7";
    internal const string HelperPrefix = "swap-helper-";

    public string TargetExe => Path.Combine(InstallDirectory, UpdaterChannelParser.UpdaterAssetName);
    public string CachedChannel => Path.Combine(InstallDirectory, "installed-updater-channel.json");
    public string CachedSignature => Path.Combine(InstallDirectory, "installed-updater-channel.sig");
    public string VerifierExe => Path.Combine(InstallDirectory, $"CobbleMusicUpdaterVerifier-{PinnedVerifierVersion}.exe");
    public string Directory => Path.Combine(InstallDirectory, "prestage");
    public string Journal => Path.Combine(Directory, "swap-journal.json");
    public string RollbackChannel => Path.Combine(Directory, "rollback-installed-updater-channel.json");
    public string RollbackSignature => Path.Combine(Directory, "rollback-installed-updater-channel.sig");
    public string RollbackExe => Path.Combine(Directory, "rollback-CobbleMusicUpdater.exe");

    public string NextChannel(string version) => Path.Combine(Directory, $"next-{version}.json");
    public string NextSignature(string version) => Path.Combine(Directory, $"next-{version}.sig");

    // The staged file name carries the signed hash so a re-signed version with different bytes can never reuse an old download.
    public string StagedExe(UpdaterChannelDescriptor descriptor) =>
        Path.Combine(Directory, $"CobbleMusicUpdater-{descriptor.UpdaterVersion}-{descriptor.Updater!.Sha256[..16]}.exe");

    public string PartialExe(UpdaterChannelDescriptor descriptor) => StagedExe(descriptor) + ".partial";

    public string NewHelperExe() => Path.Combine(Directory, HelperPrefix + Guid.NewGuid().ToString("N") + ".exe");
}

/// <summary>A channel descriptor whose exact bytes passed <see cref="UpdaterChannelParser.VerifyAndParse"/>.</summary>
internal sealed record VerifiedUpdaterChannel(UpdaterChannelDescriptor Descriptor, Version Version, byte[] DescriptorBytes, byte[] SignatureBytes)
{
    public UpdaterChannelAsset Updater => Descriptor.Updater!;
}

internal enum PrestageDecision
{
    Prestage,
    NotNewer,
    SameExecutable
}

internal static class UpdaterPrestage
{
    // The bootstrap's $MaximumChannelBytes / $MaximumSignatureBytes (lines 28-29).
    internal const int MaximumChannelBytes = 16 * 1024;
    internal const int MaximumSignatureBytes = 4 * 1024;

    internal static readonly Uri NextChannelUri =
        new($"https://raw.githubusercontent.com/{BuildInfo.DefaultRepository}/main/updater/channel/next.json");
    internal static readonly Uri NextSignatureUri =
        new($"https://raw.githubusercontent.com/{BuildInfo.DefaultRepository}/main/updater/channel/next.sig");

    /// <summary>Verifies raw descriptor + signature bytes with the compiled key, with the bootstrap's size limits.</summary>
    internal static VerifiedUpdaterChannel Verify(byte[] descriptorBytes, byte[] signatureBytes)
    {
        if (descriptorBytes.Length > MaximumChannelBytes || signatureBytes.Length > MaximumSignatureBytes)
        {
            throw new InvalidDataException("Updater channel metadata exceeds its size limit.");
        }
        UpdaterChannelDescriptor descriptor = UpdaterChannelParser.VerifyAndParse(descriptorBytes, signatureBytes);
        return new VerifiedUpdaterChannel(descriptor, Version.Parse(descriptor.UpdaterVersion), descriptorBytes.ToArray(), signatureBytes.ToArray());
    }

    /// <summary>Reads and verifies a descriptor pair from disk; null when either file is missing, oversized or not validly signed.</summary>
    internal static VerifiedUpdaterChannel? TryReadVerified(string descriptorPath, string signaturePath)
    {
        try
        {
            if (!File.Exists(descriptorPath) || !File.Exists(signaturePath)
                || new FileInfo(descriptorPath).Length > MaximumChannelBytes
                || new FileInfo(signaturePath).Length > MaximumSignatureBytes)
            {
                return null;
            }
            return Verify(File.ReadAllBytes(descriptorPath), File.ReadAllBytes(signaturePath));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Exactly the bootstrap's Test-ExactExecutable (lines 146-175): the file exists, starts with "MZ", has the signed length
    /// and the signed SHA-256 (compared case-insensitively, as its OrdinalIgnoreCase does).
    /// </summary>
    internal static bool IsExactExecutable(string path, long expectedSize, string expectedSha256)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            // A running image can only be opened for reading when the share mode tolerates its loader handle.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 2 || stream.ReadByte() != 0x4d || stream.ReadByte() != 0x5a)
            {
                return false;
            }
            if (stream.Length != expectedSize)
            {
                return false;
            }
            stream.Position = 0;
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsExactExecutable(string path, UpdaterChannelAsset asset) => IsExactExecutable(path, asset.Size, asset.Sha256);

    /// <summary>
    /// Whether <paramref name="next"/> should be pre-staged over the installed channel. The bootstrap trusts the cached channel's
    /// version as "current" (Get-CurrentUpdater lines 280-284), so only a strictly newer signed version can ever be staged; the
    /// running build's own version is a second floor so a developer build never stages something older than itself.
    /// </summary>
    internal static PrestageDecision Decide(VerifiedUpdaterChannel installed, Version ownVersion, VerifiedUpdaterChannel next)
    {
        if (next.Version <= installed.Version || next.Version <= ownVersion)
        {
            return PrestageDecision.NotNewer;
        }
        if (string.Equals(next.Updater.Sha256, installed.Updater.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return PrestageDecision.SameExecutable;
        }
        return PrestageDecision.Prestage;
    }

    /// <summary>
    /// Null when this process may pre-stage, otherwise the reason it may not. Pre-staging is only allowed for the exact launch
    /// shape of the pinned bootstrap: Prism pre-launch, started by Prism's pre-launch PowerShell, running from the bootstrap's
    /// $targetExe = "&lt;instance&gt;\minecraft\cobble-music-updater\CobbleMusicUpdater.exe" (bootstrap lines 31-35), with the bootstrap's
    /// pinned verifier present beside it.
    /// </summary>
    internal static string? GetIneligibilityReason(
        bool prismPrelaunch,
        bool checkOnly,
        string instanceDirectory,
        string installationDirectory,
        string? processPath,
        bool startedByPrismPowerShell,
        bool verifierPresent)
    {
        if (!prismPrelaunch || checkOnly)
        {
            return "not a Prism pre-launch update run";
        }
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return "the updater's own path is unknown";
        }
        string running = Path.GetFullPath(processPath);
        string bootstrapTarget = Path.GetFullPath(Path.Combine(instanceDirectory, "minecraft", "cobble-music-updater", UpdaterChannelParser.UpdaterAssetName));
        string installTarget = Path.GetFullPath(Path.Combine(installationDirectory, UpdaterChannelParser.UpdaterAssetName));
        if (!string.Equals(running, bootstrapTarget, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(running, installTarget, StringComparison.OrdinalIgnoreCase))
        {
            return "the updater is not running from the bootstrap's install path";
        }
        if (!startedByPrismPowerShell)
        {
            return "the updater was not started by the Prism pre-launch bootstrap";
        }
        if (!verifierPresent)
        {
            return "the bootstrap's pinned verifier is not installed";
        }
        return null;
    }

    /// <summary>Writes bytes to a sibling temporary file, flushes it to disk and renames it over <paramref name="path"/> (MoveFileEx REPLACE_EXISTING).</summary>
    internal static void WriteAllBytesAtomically(string path, byte[] bytes)
    {
        string temporary = path + ".prestage-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    internal static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes <paramref name="linkPath"/> another name for <paramref name="existingPath"/> (NTFS hard link, instant, same volume by
    /// construction); falls back to a byte copy on file systems without hard links.
    /// </summary>
    internal static void HardLinkOrCopy(string existingPath, string linkPath)
    {
        TryDelete(linkPath);
        if (!CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
        {
            File.Copy(existingPath, linkPath, overwrite: true);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
}

/// <summary>
/// Brings the bootstrap-visible state (installed-updater-channel.json/.sig + CobbleMusicUpdater.exe) back to a consistent pair
/// after an interrupted swap, then removes prestage leftovers. Runs at the start of every eligible updater run.
/// </summary>
internal static class UpdaterPrestageRecovery
{
    internal sealed record Result(VerifiedUpdaterChannel? Installed, bool HelperActive);

    public static Result Run(PrestageLayout layout, Action<string> log)
    {
        if (UpdaterPrestageSwap.IsJournaledHelperAlive(layout))
        {
            // A swap helper from the previous launch is still working; never touch its files concurrently.
            log("Updater pre-staging: a swap helper is still running; leaving its files alone this launch.");
            return new Result(null, HelperActive: true);
        }

        VerifiedUpdaterChannel? installed = UpdaterPrestage.TryReadVerified(layout.CachedChannel, layout.CachedSignature);
        if (installed is null || !UpdaterPrestage.IsExactExecutable(layout.TargetExe, installed.Updater))
        {
            installed = RepairCache(layout, log);
            if (installed is null)
            {
                log("Updater pre-staging: the installed updater does not match any signed channel on disk; skipping pre-staging.");
                return new Result(null, HelperActive: false);
            }
        }

        Clean(layout, installed, log);
        return new Result(installed, HelperActive: false);
    }

    /// <summary>
    /// When the cache and the executable disagree (a swap died between its atomic steps), rewrite the cache with the signed
    /// descriptor that names the executable actually on disk: the saved rollback descriptor (roll back) or a staged next
    /// descriptor (roll forward). Only bytes that verify with the compiled key and match the exe exactly are ever written.
    /// </summary>
    private static VerifiedUpdaterChannel? RepairCache(PrestageLayout layout, Action<string> log)
    {
        var candidates = new List<(string Json, string Sig)> { (layout.RollbackChannel, layout.RollbackSignature) };
        if (Directory.Exists(layout.Directory))
        {
            foreach (string json in Directory.GetFiles(layout.Directory, "next-*.json").Order(StringComparer.Ordinal))
            {
                candidates.Add((json, Path.ChangeExtension(json, ".sig")));
            }
        }
        foreach ((string json, string sig) in candidates)
        {
            VerifiedUpdaterChannel? candidate = UpdaterPrestage.TryReadVerified(json, sig);
            if (candidate is null || !UpdaterPrestage.IsExactExecutable(layout.TargetExe, candidate.Updater))
            {
                continue;
            }
            UpdaterPrestage.WriteAllBytesAtomically(layout.CachedChannel, candidate.DescriptorBytes);
            UpdaterPrestage.WriteAllBytesAtomically(layout.CachedSignature, candidate.SignatureBytes);
            log($"Updater pre-staging: repaired the installed updater channel to match the executable on disk ({candidate.Version}).");
            return candidate;
        }
        return null;
    }

    private static void Clean(PrestageLayout layout, VerifiedUpdaterChannel installed, Action<string> log)
    {
        // Leftovers of the cache writes (".prestage-<guid>" temporaries) live beside the cache files.
        foreach (string temporary in Directory.GetFiles(layout.InstallDirectory, "installed-updater-channel.*.prestage-*"))
        {
            UpdaterPrestage.TryDelete(temporary);
        }
        if (!Directory.Exists(layout.Directory))
        {
            return;
        }
        UpdaterPrestage.TryDelete(layout.Journal);
        UpdaterPrestage.TryDelete(layout.RollbackChannel);
        UpdaterPrestage.TryDelete(layout.RollbackSignature);
        UpdaterPrestage.TryDelete(layout.RollbackExe);
        int helpersRemoved = 0;
        foreach (string helper in Directory.GetFiles(layout.Directory, PrestageLayout.HelperPrefix + "*.exe"))
        {
            // A helper that is still running cannot be deleted; it is removed on a later run.
            helpersRemoved += UpdaterPrestage.TryDelete(helper) ? 1 : 0;
        }
        foreach (string temporary in Directory.GetFiles(layout.Directory, "*.prestage-*"))
        {
            UpdaterPrestage.TryDelete(temporary);
        }

        // Staged material for a version the installed channel already reached (or passed) is useless; anything newer is kept
        // so an interrupted download resumes.
        foreach (string json in Directory.GetFiles(layout.Directory, "next-*.json"))
        {
            string version = Path.GetFileNameWithoutExtension(json)["next-".Length..];
            if (Version.TryParse(version, out Version? parsed) && parsed > installed.Version)
            {
                continue;
            }
            UpdaterPrestage.TryDelete(json);
            UpdaterPrestage.TryDelete(Path.ChangeExtension(json, ".sig"));
        }
        foreach (string staged in Directory.GetFiles(layout.Directory, "CobbleMusicUpdater-*.exe*"))
        {
            string name = Path.GetFileName(staged);
            string[] parts = name.Split('-');
            if (parts.Length >= 3 && Version.TryParse(parts[1], out Version? parsed) && parsed > installed.Version)
            {
                continue;
            }
            UpdaterPrestage.TryDelete(staged);
        }
        if (helpersRemoved > 0)
        {
            log($"Updater pre-staging: removed {helpersRemoved} finished swap helper file(s).");
        }
    }
}

/// <summary>Result of the background preparation: a verified, staged newer updater, or nothing to do.</summary>
internal sealed record PrestagePreparation(VerifiedUpdaterChannel? Ready, string Summary);

/// <summary>
/// The background half of pre-staging: recovery, reading the installed channel, fetching and verifying next.json, the policy
/// decision, and the verified download. Separated from the process context so tests can drive it with fake HTTP.
/// </summary>
internal static class UpdaterPrestagePreparer
{
    public static async Task<PrestagePreparation> PrepareAsync(
        PrestageLayout layout,
        Version ownVersion,
        HttpClient http,
        PrestageDownloader downloader,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        UpdaterPrestageRecovery.Result recovery = UpdaterPrestageRecovery.Run(layout, log);
        if (recovery.Installed is null)
        {
            return new PrestagePreparation(null, recovery.HelperActive ? "swap helper still running" : "installed updater state not provable");
        }

        byte[]? descriptorBytes = await FetchSmallAsync(http, UpdaterPrestage.NextChannelUri, UpdaterPrestage.MaximumChannelBytes, cancellationToken);
        if (descriptorBytes is null)
        {
            return new PrestagePreparation(null, "no next updater channel is published");
        }
        byte[]? signatureBytes = await FetchSmallAsync(http, UpdaterPrestage.NextSignatureUri, UpdaterPrestage.MaximumSignatureBytes, cancellationToken);
        if (signatureBytes is null)
        {
            return new PrestagePreparation(null, "the next updater channel has no signature");
        }
        VerifiedUpdaterChannel next = UpdaterPrestage.Verify(descriptorBytes, signatureBytes);

        PrestageDecision decision = UpdaterPrestage.Decide(recovery.Installed, ownVersion, next);
        if (decision != PrestageDecision.Prestage)
        {
            return new PrestagePreparation(null, $"next updater {next.Version} is not newer than installed {recovery.Installed.Version} ({decision})");
        }

        Directory.CreateDirectory(layout.Directory);
        string stagedExe = layout.StagedExe(next.Descriptor);
        if (!UpdaterPrestage.IsExactExecutable(stagedExe, next.Updater))
        {
            log($"Updater pre-staging: downloading signed updater {next.Version} ({next.Updater.Size:N0} bytes) for the next launch.");
            Uri asset = new($"https://github.com/{BuildInfo.DefaultRepository}/releases/download/{next.Descriptor.ReleaseTag}/{next.Updater.Name}");
            await downloader.DownloadVerifiedAsync(asset, layout.PartialExe(next.Descriptor), stagedExe, next.Updater.Size, next.Updater.Sha256, cancellationToken);
        }
        // The exact signed bytes travel with the staged exe; the helper and recovery re-verify them before any use.
        WriteIfDifferent(layout.NextChannel(next.Descriptor.UpdaterVersion), next.DescriptorBytes);
        WriteIfDifferent(layout.NextSignature(next.Descriptor.UpdaterVersion), next.SignatureBytes);
        return new PrestagePreparation(next, $"updater {next.Version} is verified and staged");
    }

    private static void WriteIfDifferent(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return;
        }
        UpdaterPrestage.WriteAllBytesAtomically(path, bytes);
    }

    /// <summary>GET a small document; null on 404 (the normal "nothing staged" answer). Oversized bodies are rejected.</summary>
    internal static async Task<byte[]?> FetchSmallAsync(HttpClient http, Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("Next updater channel metadata exceeds its size limit.");
        }
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Next updater channel metadata exceeds its size limit.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

/// <summary>
/// One updater run's pre-staging: started after the update lock and journal recovery, runs in the background while the pack is
/// checked, and at the end of a successful run waits a bounded grace period and then starts the swap helper. Nothing here can
/// change the launch decision: every failure is logged and swallowed.
/// </summary>
internal sealed class UpdaterPrestageSession : IDisposable
{
    // Time we are willing to add to a launch after the pack work is done. An unfinished download resumes on the next launch.
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);

    private readonly PrestageLayout _layout;
    private readonly string _ownExe;
    private readonly ProcessIdentity _self;
    private readonly string _lockFile;
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task<PrestagePreparation?> _work;

    private UpdaterPrestageSession(PrestageLayout layout, string ownExe, ProcessIdentity self, string lockFile, Action<string> log)
    {
        _layout = layout;
        _ownExe = ownExe;
        _self = self;
        _lockFile = lockFile;
        _log = log;
        _http = CreateHttpClient();
        var downloader = new PrestageDownloader(_http, PrestageDownloader.DefaultInactivityTimeout, PrestageDownloader.DefaultRetryDelays, log);
        Version ownVersion = Version.Parse(BuildInfo.Version);
        CancellationToken token = _cancellation.Token;
        _work = Task.Run(async () =>
        {
            try
            {
                return await UpdaterPrestagePreparer.PrepareAsync(layout, ownVersion, _http, downloader, log, token);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                // Cancelled by the grace limit or by Dispose (which also disposes the HttpClient); the .partial resumes next launch.
                return null;
            }
            catch (Exception exception)
            {
                log($"Updater pre-staging did not complete ({exception.GetType().Name}: {exception.Message}); the current updater is unaffected.");
                return null;
            }
        });
    }

    public static UpdaterPrestageSession? TryStart(CommandLine options, UpdaterPaths paths, Action<string> log)
    {
        try
        {
            var layout = new PrestageLayout(paths.InstallationDirectory);
            string? processPath = Environment.ProcessPath;
            PrismLaunchChain? chain = options.PrismPrelaunch ? PrismLaunchChain.TryResolveCurrent() : null;
            string? reason = UpdaterPrestage.GetIneligibilityReason(
                options.PrismPrelaunch,
                options.CheckOnly,
                paths.InstanceDirectory,
                paths.InstallationDirectory,
                processPath,
                chain?.PowerShell is not null,
                File.Exists(layout.VerifierExe));
            if (reason is not null)
            {
                log($"Updater pre-staging skipped: {reason}.");
                return null;
            }
            if (!ProcessTree.TryGetIdentity(Environment.ProcessId, out ProcessIdentity self))
            {
                return null;
            }
            return new UpdaterPrestageSession(layout, processPath!, self, Path.Combine(paths.LocalDataDirectory, "update.lock"), log);
        }
        catch (Exception exception)
        {
            log($"Updater pre-staging skipped ({exception.GetType().Name}: {exception.Message}).");
            return null;
        }
    }

    /// <summary>Called only after a successful update run. Waits at most <see cref="Grace"/>, then starts the swap helper if ready.</summary>
    public Task FinishAsync(IProgress<UpdateProgress>? progress, CancellationToken cancellationToken) =>
        FinishCoreAsync(
            _work,
            Grace,
            _cancellation,
            ready => UpdaterPrestageSwap.StartHelper(_layout, _ownExe, _self, _lockFile, ready, _log),
            progress,
            _log,
            cancellationToken);

    /// <summary>
    /// The body of <see cref="FinishAsync"/>, separated (1.2.18 integration) so the run-token behaviour can be tested with a
    /// fake preparation and a fake helper starter. <paramref name="cancellationToken"/> is the run token (the card's X or a
    /// later launch's stop event): it ends the grace wait at once, and once it is cancelled nothing new is started. That
    /// leaves only states the next run already handles: an unfinished download keeps its resumable .partial (exactly the
    /// grace-expiry path), a finished one stays verified in prestage\ and the next run starts the helper without
    /// downloading again (PrepareAsync skips an exact staged exe). The swap itself only ever runs in the helper.
    /// </summary>
    internal static async Task FinishCoreAsync(
        Task<PrestagePreparation?> work,
        TimeSpan grace,
        CancellationTokenSource workCancellation,
        Action<VerifiedUpdaterChannel> startHelper,
        IProgress<UpdateProgress>? progress,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!work.IsCompleted)
            {
                progress?.Report(new UpdateProgress(UpdatePhase.Checking, "Preparing the next updater version…"));
                // A cancelled Task.Delay completes WhenAny without throwing, so the X never waits out the grace period.
                await Task.WhenAny(work, Task.Delay(grace, cancellationToken));
                if (!cancellationToken.IsCancellationRequested)
                {
                    // The card showed our message; put a neutral final result back before Minecraft starts.
                    progress?.Report(new UpdateProgress(UpdatePhase.Complete, "Ready — starting Minecraft."));
                }
            }
            if (cancellationToken.IsCancellationRequested)
            {
                workCancellation.Cancel();
                log("Updater pre-staging: the run was stopped, so no swap helper was started; pre-staging continues on the next launch.");
                return;
            }
            if (!work.IsCompleted)
            {
                workCancellation.Cancel();
                log("Updater pre-staging: download continues on the next launch.");
                return;
            }
            PrestagePreparation? preparation = await work;
            if (preparation is null)
            {
                return;
            }
            log($"Updater pre-staging: {preparation.Summary}.");
            if (preparation.Ready is not null)
            {
                startHelper(preparation.Ready);
            }
        }
        catch (Exception exception)
        {
            log($"Updater pre-staging could not start its swap helper ({exception.GetType().Name}: {exception.Message}).");
        }
    }

    public void Dispose()
    {
        // Cancelling leaves a resumable .partial; the background task observes the token and ends on its own.
        _cancellation.Cancel();
        _http.Dispose();
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            // Same reason as ReleaseClient: Content-Length/Range must describe the exact signed bytes.
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true
        })
        {
            // Bounds each request up to its headers; body reads have their own inactivity timeout (PrestageDownloader).
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse(BuildInfo.UserAgent));
        http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        return http;
    }
}
