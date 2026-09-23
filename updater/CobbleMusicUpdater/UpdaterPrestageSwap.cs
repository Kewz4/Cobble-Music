using System.Diagnostics;
using System.Text.Json;

namespace CobbleMusicUpdater;

internal enum PrestageSwapStep
{
    RollbackPrepared,
    ChannelWritten,
    SignatureWritten,
    ExecutableReplaced
}

/// <summary>
/// Swaps a verified pre-staged updater in for the installed one, in a separate helper process.
///
/// Why a helper: the running updater cannot rename its own executable. Measured on .NET 10 single-file builds (plain and
/// compressed): after File.Move(self, aside) every later assembly load in that process throws FileNotFoundException, because
/// the runtime reopens the bundle by path. So the updater only downloads and verifies, then starts a hard link of its own
/// executable (prestage\swap-helper-*.exe) that waits for it to exit and performs the swap.
///
/// Order (each step is one atomic rename over its target, MoveFileEx REPLACE_EXISTING, after a flushed temporary write):
///   0. keep rollback material: the exact installed descriptor/signature bytes and a hard link to the installed exe;
///   1. installed-updater-channel.json := next.json bytes;
///   2. installed-updater-channel.sig  := next.sig bytes;
///   3. CobbleMusicUpdater.exe         := the staged exe (the old file lives on through the helper's and the rollback's links).
/// The cache is written before the exe on purpose. If the process dies between steps, the bootstrap sees a cache that is
/// invalid or names the new exe while the old exe is installed; online, its stable-channel branch then re-caches stable.json
/// for the old exe (which still matches) and runs it with NO download, and the next updater run retries the swap. The exe-first
/// order would instead make it re-download the old exe. Offline, both orders reach the bootstrap's pinned-verifier fallback
/// (lines 330-341) for that one launch; that is the only residual, it needs a crash inside a window of two renames, and
/// UpdaterPrestageRecovery repairs the pair on the next run of any 1.2.18+ updater.
/// </summary>
internal static class UpdaterPrestageSwap
{
    internal const string HelperSwitch = "--complete-updater-prestage";
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Performs steps 0-3 with full re-verification and in-process rollback on any failure.</summary>
    internal static void Execute(PrestageLayout layout, string nextVersion, Action<string> log, Action<PrestageSwapStep>? afterStep = null)
    {
        VerifiedUpdaterChannel installed = UpdaterPrestage.TryReadVerified(layout.CachedChannel, layout.CachedSignature)
            ?? throw new InvalidDataException("The installed updater channel is missing or not validly signed.");
        if (!UpdaterPrestage.IsExactExecutable(layout.TargetExe, installed.Updater))
        {
            throw new InvalidDataException("The installed updater executable does not match its signed channel.");
        }
        VerifiedUpdaterChannel next = UpdaterPrestage.TryReadVerified(layout.NextChannel(nextVersion), layout.NextSignature(nextVersion))
            ?? throw new InvalidDataException($"The staged updater channel {nextVersion} is missing or not validly signed.");
        if (!string.Equals(next.Descriptor.UpdaterVersion, nextVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The staged updater channel names a different version than requested.");
        }
        if (next.Version <= installed.Version)
        {
            // Another helper (or the bootstrap) already moved past this version: nothing to do, and never a downgrade.
            log($"Staged updater {next.Version} is not newer than installed {installed.Version}; nothing to swap.");
            return;
        }
        string staged = layout.StagedExe(next.Descriptor);
        if (!UpdaterPrestage.IsExactExecutable(staged, next.Updater))
        {
            throw new InvalidDataException("The staged updater executable does not match its signed channel.");
        }

        UpdaterPrestage.WriteAllBytesAtomically(layout.RollbackChannel, installed.DescriptorBytes);
        UpdaterPrestage.WriteAllBytesAtomically(layout.RollbackSignature, installed.SignatureBytes);
        UpdaterPrestage.HardLinkOrCopy(layout.TargetExe, layout.RollbackExe);
        if (!UpdaterPrestage.IsExactExecutable(layout.RollbackExe, installed.Updater))
        {
            throw new InvalidDataException("Could not preserve the installed updater for rollback.");
        }
        afterStep?.Invoke(PrestageSwapStep.RollbackPrepared);

        try
        {
            UpdaterPrestage.WriteAllBytesAtomically(layout.CachedChannel, next.DescriptorBytes);
            afterStep?.Invoke(PrestageSwapStep.ChannelWritten);
            UpdaterPrestage.WriteAllBytesAtomically(layout.CachedSignature, next.SignatureBytes);
            afterStep?.Invoke(PrestageSwapStep.SignatureWritten);
            File.Move(staged, layout.TargetExe, overwrite: true);
            afterStep?.Invoke(PrestageSwapStep.ExecutableReplaced);

            // Re-read exactly what the bootstrap will read.
            VerifiedUpdaterChannel? now = UpdaterPrestage.TryReadVerified(layout.CachedChannel, layout.CachedSignature);
            if (now is null
                || !now.DescriptorBytes.AsSpan().SequenceEqual(next.DescriptorBytes)
                || !UpdaterPrestage.IsExactExecutable(layout.TargetExe, next.Updater))
            {
                throw new InvalidDataException("The swapped updater failed verification.");
            }
        }
        catch (Exception exception)
        {
            log($"Updater swap to {next.Version} failed ({exception.GetType().Name}: {exception.Message}); rolling back to {installed.Version}.");
            Rollback(layout, installed, next, staged, log);
            throw;
        }

        UpdaterPrestage.TryDelete(layout.RollbackChannel);
        UpdaterPrestage.TryDelete(layout.RollbackSignature);
        UpdaterPrestage.TryDelete(layout.RollbackExe);
        log($"Installed pre-staged updater {next.Version}; the next Prism launch starts it without a bootstrap download.");
    }

    private static void Rollback(PrestageLayout layout, VerifiedUpdaterChannel installed, VerifiedUpdaterChannel next, string staged, Action<string> log)
    {
        try
        {
            // Executable first, so an interruption here leaves the cheap state (old exe + new cache: the bootstrap re-caches
            // stable for the old exe without downloading).
            if (!UpdaterPrestage.IsExactExecutable(layout.TargetExe, installed.Updater))
            {
                if (!File.Exists(staged) && UpdaterPrestage.IsExactExecutable(layout.TargetExe, next.Updater))
                {
                    // Keep the verified download for the retry instead of discarding it.
                    UpdaterPrestage.HardLinkOrCopy(layout.TargetExe, staged);
                }
                File.Move(layout.RollbackExe, layout.TargetExe, overwrite: true);
            }
            if (!HasBytes(layout.CachedChannel, installed.DescriptorBytes))
            {
                UpdaterPrestage.WriteAllBytesAtomically(layout.CachedChannel, installed.DescriptorBytes);
            }
            if (!HasBytes(layout.CachedSignature, installed.SignatureBytes))
            {
                UpdaterPrestage.WriteAllBytesAtomically(layout.CachedSignature, installed.SignatureBytes);
            }
            log($"Rolled back to updater {installed.Version}.");
        }
        catch (Exception exception)
        {
            // Left for UpdaterPrestageRecovery, which re-pairs the cache with whichever signed exe is on disk.
            log($"Rollback did not complete ({exception.GetType().Name}: {exception.Message}); the next updater run repairs it.");
        }
    }

    private static bool HasBytes(string path, byte[] expected) =>
        File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);

    /// <summary>Starts a hard link of the running updater as the swap helper. The helper waits for this process to exit.</summary>
    internal static void StartHelper(PrestageLayout layout, string ownExe, ProcessIdentity self, string lockFile, VerifiedUpdaterChannel next, Action<string> log)
    {
        Directory.CreateDirectory(layout.Directory);
        string helper = layout.NewHelperExe();
        UpdaterPrestage.HardLinkOrCopy(ownExe, helper);
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = layout.Directory
        };
        start.ArgumentList.Add(HelperSwitch);
        start.ArgumentList.Add("--next-version");
        start.ArgumentList.Add(next.Descriptor.UpdaterVersion);
        start.ArgumentList.Add("--parent-pid");
        start.ArgumentList.Add(self.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--parent-start-ticks");
        start.ArgumentList.Add(self.StartUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--lock-file");
        start.ArgumentList.Add(lockFile);
        using Process? process = Process.Start(start);
        log(process is null
            ? "Updater pre-staging: the swap helper did not start; retrying next launch."
            : $"Updater pre-staging: swap helper started (pid {process.Id}); it installs updater {next.Version} after this updater exits.");
    }

    /// <summary>Entry point of the helper process (dispatched from Program.Main before normal argument parsing).</summary>
    internal static int RunHelper(string[] args)
    {
        string? own = Environment.ProcessPath;
        string? prestageDirectory = own is null ? null : Path.GetDirectoryName(own);
        string? installDirectory = prestageDirectory is null ? null : Path.GetDirectoryName(prestageDirectory);
        // The helper only ever acts on the install folder it physically lives in, never on a path from its arguments.
        if (own is null || installDirectory is null
            || !string.Equals(Path.GetFileName(prestageDirectory), "prestage", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(own).StartsWith(PrestageLayout.HelperPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Kewz's Cobblemon Updater: the swap helper must run from its prestage folder.");
            return 64;
        }
        var layout = new PrestageLayout(installDirectory);
        void Log(string message) => AppendLog(layout, message);

        if (!TryParseHelperArguments(args, out string nextVersion, out int parentPid, out long parentStartTicks, out string lockFile))
        {
            Log("Swap helper received invalid arguments; nothing was changed.");
            return 64;
        }
        if (!ProcessTree.TryGetIdentity(Environment.ProcessId, out ProcessIdentity self))
        {
            return 65;
        }
        WriteJournal(layout, self, nextVersion);
        try
        {
            if (ProcessTree.TryGetIdentity(parentPid, out ProcessIdentity parent)
                && parent.StartUtc.Ticks == parentStartTicks
                && !ProcessTree.WaitForExit(parent, ParentExitTimeout))
            {
                Log("Swap helper: the updater that started it is still running; swap postponed to a later launch.");
                return 2;
            }
            using FileStream? updateLock = TryAcquireLock(lockFile);
            if (updateLock is null)
            {
                Log("Swap helper: another updater holds the update lock; swap postponed to a later launch.");
                return 3;
            }
            Execute(layout, nextVersion, Log);
            return 0;
        }
        catch (Exception exception)
        {
            Log($"Swap helper stopped: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            UpdaterPrestage.TryDelete(layout.Journal);
        }
    }

    internal static bool TryParseHelperArguments(string[] args, out string nextVersion, out int parentPid, out long parentStartTicks, out string lockFile)
    {
        nextVersion = "";
        parentPid = 0;
        parentStartTicks = 0;
        lockFile = "";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == HelperSwitch)
            {
                continue;
            }
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
            index++;
        }
        if (values.Count != 4
            || !values.TryGetValue("--next-version", out string? version)
            || !values.TryGetValue("--parent-pid", out string? pid)
            || !values.TryGetValue("--parent-start-ticks", out string? ticks)
            || !values.TryGetValue("--lock-file", out string? lockPath)
            || !Version.TryParse(version, out Version? parsed) || parsed.Build < 0 || parsed.Revision >= 0 || parsed.ToString(3) != version
            || !int.TryParse(pid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parentPid)
            || !long.TryParse(ticks, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parentStartTicks)
            || !string.Equals(Path.GetFileName(lockPath), "update.lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        nextVersion = version;
        lockFile = lockPath;
        return true;
    }

    private static FileStream? TryAcquireLock(string lockFile)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // Same open as LocalStateStore.AcquireOperationLock: exclusive while held, so no updater runs mid-swap.
                return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (stopwatch.Elapsed < LockTimeout)
            {
                Thread.Sleep(250);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private sealed record Journal(int HelperPid, long HelperStartUtcTicks, string NextVersion);

    private static void WriteJournal(PrestageLayout layout, ProcessIdentity self, string nextVersion)
    {
        Directory.CreateDirectory(layout.Directory);
        UpdaterPrestage.WriteAllBytesAtomically(layout.Journal, JsonSerializer.SerializeToUtf8Bytes(new Journal(self.Pid, self.StartUtc.Ticks, nextVersion)));
    }

    /// <summary>
    /// True when the journal names a live swap helper (same PID and creation time, image in the prestage folder). The journal is
    /// unsigned, so it is only ever used to stay hands-off, never to choose a file.
    /// </summary>
    internal static bool IsJournaledHelperAlive(PrestageLayout layout)
    {
        try
        {
            if (!File.Exists(layout.Journal))
            {
                return false;
            }
            Journal? journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(layout.Journal));
            return journal is not null
                && journal.HelperPid != Environment.ProcessId
                && ProcessTree.TryGetIdentity(journal.HelperPid, out ProcessIdentity helper)
                && helper.StartUtc.Ticks == journal.HelperStartUtcTicks
                && helper.ExeName.StartsWith(PrestageLayout.HelperPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void AppendLog(PrestageLayout layout, string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(layout.InstallDirectory, "updater.log"),
                $"{DateTimeOffset.Now:O} Kewz's Cobblemon Updater: [swap helper] {message}{Environment.NewLine}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Diagnostics must never change what the helper does.
        }
    }
}
