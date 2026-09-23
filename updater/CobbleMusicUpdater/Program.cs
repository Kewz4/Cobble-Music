using System.Text.Json;

namespace CobbleMusicUpdater;

internal static class Program
{
    private const long MaximumDiagnosticLogBytes = 1024 * 1024;
    private static string? _diagnosticLogPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--generate-keypair", StringComparer.Ordinal))
            {
                return GenerateKeyPair(args);
            }
            if (args.Contains("--sign-manifest", StringComparer.Ordinal))
            {
                return SignManifest(args);
            }
            if (args.Contains("--verify-manifest", StringComparer.Ordinal))
            {
                return VerifyManifest(args);
            }
            if (args.Contains("--verify-updater-channel", StringComparer.Ordinal))
            {
                return VerifyUpdaterChannel(args);
            }
            if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
            {
                PrintUsage();
                return 0;
            }

            CommandLine options = CommandLine.Parse(args);
            if (options.PrismPrelaunch && !options.NoUi)
            {
                return UpdateStatusForm.Run(options, RunUpdaterAsync);
            }
            return RunUpdaterAsync(options, progress: null).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Kewz's Cobblemon Updater: {exception.Message}");
            return 1;
        }
    }

    internal static async Task<int> RunUpdaterAsync(CommandLine options, IProgress<UpdateProgress>? progress)
    {
        _diagnosticLogPath = null;
        // [lock-v2] The ONE run-level cancellation source: the X button and the stop event cancel it.
        using RunControl run = RunControl.Begin();
        run.Log = Log;
        run.InteractiveUi = options.PrismPrelaunch && !options.NoUi;
        // [/lock-v2]
        try
        {
            Report(progress, UpdatePhase.Checking, "Checking for updates…");
            UpdaterPaths paths = LocalStateStore.ResolvePaths(options.InstanceDirectory, options.MinecraftDirectory);
            _diagnosticLogPath = Path.Combine(paths.InstallationDirectory, "updater.log");
            run.Paths = paths; // [lock-v2]
            Log($"Updater executable version: {typeof(Program).Assembly.GetName().Version}");
            Log($"Resolved instance root: {paths.InstanceDirectory}");
            Log($"Resolved Minecraft directory: {paths.MinecraftDirectory}");
            // [lock-v2] Waits for, takes over from, or gives up on another holder; writes the owner record.
            using LockOwnership updateLock = await OperationLockV2.AcquireAsync(paths, options, run, progress, Log);
            progress = updateLock.Track(progress);
            // [/lock-v2]
            // Recover before opening mutable local configuration. A corrupt
            // configuration must not conceal an interrupted file transaction.
            await TransactionStore.RecoverIfNeededAsync(paths, BuildInfo.SupportedRoots, Log);
            UpdaterConfiguration configuration = LocalStateStore.LoadConfiguration(paths);
            run.AllowOfflineLaunch = configuration.AllowOfflineLaunch; // [lock-v2]
            InstalledState installedState = LocalStateStore.LoadState(paths);
            using var releaseClient = new ReleaseClient(TimeSpan.FromSeconds(configuration.NetworkTimeoutSeconds));
            // 1.2.18 net track: log + retry notice + verified metadata cache.
            releaseClient.AttachReleaseCheck(paths, Log, message => Report(progress, UpdatePhase.Checking, message));

            IReadOnlyList<RemoteRelease> releaseChain;
            try
            {
                Log("Checking GitHub Releases...");
                releaseChain = await releaseClient.GetUpdateChainAsync(configuration, installedState, run.Token);
            }
            catch (Exception exception) when (!run.Token.IsCancellationRequested && configuration.AllowOfflineLaunch && IsExpectedNetworkFailure(exception))
            {
                // Offline success is permitted only during the initial release check.
                // 1.2.18 net track: HTTP status/rate-limit detail in the log and on the card.
                Log($"Initial release check is unavailable ({ReleaseCheckDiagnostics.Describe(exception)}); offline launch is enabled, so starting the local pack without verifying updates.");
                Report(progress, UpdatePhase.Fallback, ReleaseCheckDiagnostics.FallbackCardMessage(exception));
                return 0;
            }
            catch (Exception exception) when (!run.Token.IsCancellationRequested && IsExpectedNetworkFailure(exception))
            {
                Log($"GitHub is unavailable ({exception.GetType().Name}) and offline launch is disabled.");
                // [lock-v2] Make the policy's "blocked" true under the pinned bootstrap.
                return RunOutcomes.Blocked(options, progress, Log, "Couldn’t verify updates — launch is blocked by updater policy.",
                    NetworkFailureExitCode(configuration, exception));
            }

            var engine = new UpdateEngine(paths, configuration, Log, progress, releaseClient.VerifiedReleases);
            await engine.CheckAndUpdateAsync(releaseChain, options.CheckOnly, run.Token);
            return 0;
        }
        // [lock-v2] X button or a peer's stop event. First, so no network filter mistakes the resulting
        // TaskCanceledException for an outage and reports an offline fallback.
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            return RunOutcomes.Cancelled(options, run, progress, Log);
        }
        catch (TransactionRecoveryException exception)
        {
            Log($"Local update recovery needs attention: {exception.Message}");
            Log("Stopping this Prism launch so a partially updated modpack cannot run."); // [lock-v2]
            return RunOutcomes.Blocked(options, progress, Log, "Updater recovery needs attention. Check updater.log.");
        }
        catch (UpdaterBusyException exception)
        {
            Log($"Update check is already in progress: {exception.Message}");
            return RunOutcomes.Busy(options, exception, progress, Log); // [lock-v2]
        }
        catch (Exception exception)
        {
            Log($"Updater setup, update, or integrity check failed: {exception.Message}");
            // [lock-v2] A cancelled run can surface as any exception once a callee wraps the cancellation; it is still
            // a cancellation (a peer stop must still stop this launch).
            if (run.Token.IsCancellationRequested)
            {
                return RunOutcomes.Cancelled(options, run, progress, Log);
            }
            // A network failure after the check is harmless when nothing is half-applied and offline launch is allowed.
            if (run.AllowOfflineLaunch == true && IsExpectedNetworkFailure(exception) && !run.TransactionMayBePending)
            {
                Log("It was a network failure, offline launch is enabled and no install is pending; starting the current pack.");
                Report(progress, UpdatePhase.Fallback, "Couldn’t finish the update — starting Minecraft.");
                return 0;
            }
            Log("Update did not complete; client parity has not been verified. Stopping this Prism launch.");
            return RunOutcomes.Blocked(options, progress, Log, "Update failed — pack not updated. Check updater.log before playing.");
            // [/lock-v2]
        }
    }

    // [integration 1.2.18] Lock v2 makes Blocked really stop the launch, so every transport failure the net track can
    // still raise after its retries must count as a network failure here (offline fallback during the check; the
    // no-journal network fallback after it), or a flaky connection would stop a launch that has nothing half-applied.
    // Measured on .NET 10: a body cut short with Content-Length gives HttpIOException (ResponseEnded); a connection
    // reset mid-body gives a plain IOException whose InnerException is a SocketException; a body that ends cleanly
    // below its signed size gives the net track's TruncatedDownloadException. Any other IOException (disk full, a
    // locked file) is NOT a network failure. A cancelled run never reaches these filters (Program filters on run.Token).
    internal static bool IsExpectedNetworkFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or TimeoutException
            or System.Net.Http.HttpIOException or TruncatedDownloadException
        || (exception is IOException && HasSocketCause(exception));

    private static bool HasSocketCause(Exception exception)
    {
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is System.Net.Sockets.SocketException)
            {
                return true;
            }
        }
        return false;
    }
    // [/integration 1.2.18]

    internal static int NetworkFailureExitCode(UpdaterConfiguration configuration, Exception exception)
    {
        if (!IsExpectedNetworkFailure(exception))
        {
            throw new ArgumentException("Failure is not an expected network outage.", nameof(exception));
        }
        return configuration.AllowOfflineLaunch ? 0 : 1;
    }

    private static void Report(
        IProgress<UpdateProgress>? progress,
        UpdatePhase phase,
        string message,
        long completedBytes = 0,
        long totalBytes = 0,
        int currentItem = 0,
        int totalItems = 0) =>
        progress?.Report(new UpdateProgress(phase, message, completedBytes, totalBytes, currentItem, totalItems));

    private static int GenerateKeyPair(string[] args)
    {
        string privateOutput = RequiredValue(args, "--private-key-file");
        string publicOutput = RequiredValue(args, "--public-key-file");
        if (File.Exists(privateOutput) || File.Exists(publicOutput))
        {
            throw new IOException("Refusing to overwrite an existing signing key file.");
        }

        (byte[] privateSeed, byte[] publicKey) = ManifestSecurity.GenerateKeyPair();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privateOutput))!);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publicOutput))!);
            File.WriteAllText(privateOutput, Convert.ToBase64String(privateSeed) + Environment.NewLine);
            File.WriteAllText(publicOutput, Convert.ToBase64String(publicKey) + Environment.NewLine);
            Console.WriteLine($"Kewz's Cobblemon Updater: private signing seed written to {Path.GetFullPath(privateOutput)}");
            Console.WriteLine($"Kewz's Cobblemon Updater: public verification key written to {Path.GetFullPath(publicOutput)}");
            Console.WriteLine("Kewz's Cobblemon Updater: keep the private seed out of Git, Claude, and all shared folders.");
            return 0;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(privateSeed);
        }
    }

    private static int SignManifest(string[] args)
    {
        string manifestPath = RequiredValue(args, "--sign-manifest");
        string privateKeyPath = RequiredValue(args, "--private-key-file");
        string signatureOutput = RequiredValue(args, "--signature-output");
        if (File.Exists(signatureOutput))
        {
            throw new IOException("Refusing to overwrite an existing manifest signature.");
        }

        byte[] manifest = File.ReadAllBytes(manifestPath);
        byte[] seed = Convert.FromBase64String(File.ReadAllText(privateKeyPath).Trim());
        try
        {
            byte[] signature = ManifestSecurity.Sign(manifest, seed);
            var detached = new DetachedSignature
            {
                KeyId = TrustedKeyRing.CurrentKeyId,
                Signature = Convert.ToBase64String(signature)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(signatureOutput))!);
            File.WriteAllBytes(signatureOutput, JsonSerializer.SerializeToUtf8Bytes(detached, JsonOptions));
            Console.WriteLine($"Kewz's Cobblemon Updater: signed manifest written to {Path.GetFullPath(signatureOutput)}");
            return 0;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static int VerifyManifest(string[] args)
    {
        string manifestPath = RequiredValue(args, "--verify-manifest");
        string signaturePath = RequiredValue(args, "--signature-file");
        UpdateManifest manifest = ManifestParser.VerifyAndParse(File.ReadAllBytes(manifestPath), File.ReadAllBytes(signaturePath));
        Console.WriteLine($"Kewz's Cobblemon Updater: verified signed manifest for {manifest.ModpackId} {manifest.Version}.");
        return 0;
    }

    private static int VerifyUpdaterChannel(string[] args)
    {
        string descriptorPath = RequiredValue(args, "--verify-updater-channel");
        string signaturePath = RequiredValue(args, "--signature-file");
        string verifiedOutputPath = RequiredValue(args, "--verified-output");
        if (File.Exists(verifiedOutputPath))
        {
            throw new IOException("Refusing to overwrite an existing verified channel output.");
        }

        UpdaterChannelDescriptor descriptor = UpdaterChannelParser.VerifyAndParse(
            File.ReadAllBytes(descriptorPath),
            File.ReadAllBytes(signaturePath));
        byte[] canonical = UpdaterChannelParser.SerializeCanonical(descriptor);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(verifiedOutputPath))!);
        using (var output = new FileStream(verifiedOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(canonical);
            output.Flush(flushToDisk: true);
        }
        Console.WriteLine($"Kewz's Cobblemon Updater: verified signed updater channel {descriptor.UpdaterVersion}.");
        return 0;
    }

    private static string RequiredValue(string[] args, string key)
    {
        int index = Array.FindIndex(args, argument => string.Equals(argument, key, StringComparison.Ordinal));
        if (index < 0 || index == args.Length - 1 || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"Missing required value for {key}.");
        }
        return args[index + 1];
    }

    private static void Log(string message)
    {
        string line = $"Kewz's Cobblemon Updater: {message}";
        Console.WriteLine(line);
        if (string.IsNullOrWhiteSpace(_diagnosticLogPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_diagnosticLogPath)!);
            if (File.Exists(_diagnosticLogPath)
                && new FileInfo(_diagnosticLogPath).Length >= MaximumDiagnosticLogBytes)
            {
                File.Move(_diagnosticLogPath, _diagnosticLogPath + ".previous", overwrite: true);
            }
            File.AppendAllText(
                _diagnosticLogPath,
                $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // A diagnostic sink must not change the updater's launch decision.
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Kewz's Cobblemon Updater");
        Console.WriteLine("  CobbleMusicUpdater.exe --instance-dir <Prism instance> --minecraft-dir <minecraft folder> --prism-prelaunch [--no-ui]");
        Console.WriteLine("  CobbleMusicUpdater.exe --generate-keypair --private-key-file <path> --public-key-file <path>");
        Console.WriteLine("  CobbleMusicUpdater.exe --sign-manifest <manifest.json> --private-key-file <path> --signature-output <manifest.sig>");
        Console.WriteLine("  CobbleMusicUpdater.exe --verify-manifest <manifest.json> --signature-file <manifest.sig>");
        Console.WriteLine("  CobbleMusicUpdater.exe --verify-updater-channel <channel.json> --signature-file <channel.sig> --verified-output <validated.json>");
    }
}

internal sealed record CommandLine(
    string InstanceDirectory,
    string MinecraftDirectory,
    bool PrismPrelaunch,
    bool CheckOnly,
    bool NoUi)
{
    public static CommandLine Parse(string[] args)
    {
        string? instanceDirectory = null;
        string? minecraftDirectory = null;
        bool prismPrelaunch = false;
        bool checkOnly = false;
        bool noUi = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--instance-dir":
                    instanceDirectory = ReadValue(args, ref index, "--instance-dir");
                    break;
                case "--minecraft-dir":
                    minecraftDirectory = ReadValue(args, ref index, "--minecraft-dir");
                    break;
                case "--prism-prelaunch":
                    prismPrelaunch = true;
                    break;
                case "--check-only":
                    checkOnly = true;
                    break;
                case "--no-ui":
                    noUi = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(instanceDirectory) || string.IsNullOrWhiteSpace(minecraftDirectory))
        {
            throw new ArgumentException("--instance-dir and --minecraft-dir are required.");
        }
        return new CommandLine(instanceDirectory, minecraftDirectory, prismPrelaunch, checkOnly, noUi);
    }

    private static string ReadValue(string[] args, ref int index, string key)
    {
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Missing value for {key}.");
        }
        return args[index];
    }
}
