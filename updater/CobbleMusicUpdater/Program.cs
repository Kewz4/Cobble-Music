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
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        try
        {
            Report(progress, UpdatePhase.Checking, "Checking for updates…");
            UpdaterPaths paths = LocalStateStore.ResolvePaths(options.InstanceDirectory, options.MinecraftDirectory);
            _diagnosticLogPath = Path.Combine(paths.InstallationDirectory, "updater.log");
            using FileStream updateLock = LocalStateStore.AcquireOperationLock(paths);
            // Recover before opening mutable local configuration. A corrupt
            // configuration must not conceal an interrupted file transaction.
            await TransactionStore.RecoverIfNeededAsync(paths, BuildInfo.SupportedRoots, Log);
            UpdaterConfiguration configuration = LocalStateStore.LoadConfiguration(paths);
            InstalledState installedState = LocalStateStore.LoadState(paths);
            using var releaseClient = new ReleaseClient(TimeSpan.FromSeconds(configuration.NetworkTimeoutSeconds));

            IReadOnlyList<RemoteRelease> releaseChain;
            try
            {
                Log("Checking GitHub Releases...");
                releaseChain = await releaseClient.GetUpdateChainAsync(configuration, installedState, cancellation.Token);
            }
            catch (Exception exception) when (configuration.AllowOfflineLaunch && IsExpectedNetworkFailure(exception))
            {
                Log($"GitHub is unavailable ({exception.GetType().Name}); launching the last known-good pack.");
                Report(progress, UpdatePhase.Fallback, "Couldn’t check for updates — starting Minecraft.");
                return 0;
            }
            catch (Exception exception) when (IsExpectedNetworkFailure(exception))
            {
                // This explicit non-zero return must happen inside the release
                // check. Letting it escape would reach the generic Prism
                // fallback below and accidentally defeat AllowOfflineLaunch.
                Log($"GitHub is unavailable ({exception.GetType().Name}) and offline launch is disabled.");
                Report(progress, UpdatePhase.Blocked, "Couldn’t verify updates — launch is blocked by updater policy.");
                return NetworkFailureExitCode(configuration, exception);
            }

            var engine = new UpdateEngine(paths, configuration, Log, progress);
            try
            {
                await engine.CheckAndUpdateAsync(releaseChain, options.CheckOnly, cancellation.Token);
                return 0;
            }
            catch (TransactionRecoveryException)
            {
                throw;
            }
            catch (Exception exception) when (!configuration.AllowOfflineLaunch && IsExpectedNetworkFailure(exception))
            {
                Log($"Update download failed ({exception.GetType().Name}) and offline launch is disabled.");
                Report(progress, UpdatePhase.Blocked, "Couldn’t verify the update — launch is blocked by updater policy.");
                return NetworkFailureExitCode(configuration, exception);
            }
            catch (Exception exception) when (options.PrismPrelaunch)
            {
                // Bad or unreachable remote data must never partially modify a
                // friend's instance or turn an otherwise playable local pack
                // into a blocked Prism launch. The engine rolls back before
                // this point whenever it has started a transaction.
                Log($"Update was not applied: {exception.Message}");
                Log("Launching the last known-good local pack.");
                Report(progress, UpdatePhase.Fallback, "Update was not applied — starting Minecraft.");
                return 0;
            }
        }
        catch (TransactionRecoveryException exception)
        {
            Log($"Local update recovery needs attention: {exception.Message}");
            Log("Prism launch is blocked so a partially updated modpack cannot run.");
            Report(progress, UpdatePhase.Blocked, "Updater recovery needs attention. Check updater.log.");
            return 1;
        }
        catch (UpdaterBusyException exception)
        {
            Log($"Update check is already in progress: {exception.Message}");
            Log("Prism launch is blocked until that update check finishes.");
            Report(progress, UpdatePhase.Blocked, "Another update check is already running.");
            return 1;
        }
        catch (Exception exception) when (options.PrismPrelaunch)
        {
            Log($"Updater setup issue: {exception.Message}");
            Log("Launching without changing the local pack.");
            Report(progress, UpdatePhase.Fallback, "Updater setup needs attention — starting Minecraft.");
            return 0;
        }
    }

    internal static bool IsExpectedNetworkFailure(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or TimeoutException;

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
            // A diagnostic sink must never block a safe launcher fallback.
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Kewz's Cobblemon Updater");
        Console.WriteLine("  CobbleMusicUpdater.exe --instance-dir <Prism instance> --minecraft-dir <minecraft folder> --prism-prelaunch [--no-ui]");
        Console.WriteLine("  CobbleMusicUpdater.exe --generate-keypair --private-key-file <path> --public-key-file <path>");
        Console.WriteLine("  CobbleMusicUpdater.exe --sign-manifest <manifest.json> --private-key-file <path> --signature-output <manifest.sig>");
        Console.WriteLine("  CobbleMusicUpdater.exe --verify-manifest <manifest.json> --signature-file <manifest.sig>");
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
