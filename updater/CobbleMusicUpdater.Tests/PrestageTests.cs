using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CobbleMusicUpdater;
using Org.BouncyCastle.Crypto.Parameters;

/// <summary>
/// Updater pre-staging (UpdaterPrestage*.cs): descriptor verification, the newer/equal/older policy, launch-context eligibility,
/// the verified resumable download, the swap with rollback at every step, crash recovery from every torn state, and cleanup.
/// The bootstrap-level proof (the REAL pinned bootstrap functions run against these files) is tests/Test-UpdaterPrestageBootstrap.ps1,
/// which drives <see cref="PrestageFixtureCli"/>.
/// </summary>
internal static class PrestageTests
{
    private const string OldVersion = "1.2.30";
    private const string NewVersion = "1.2.31";

    public static async Task RunAllAsync(string root)
    {
        Directory.CreateDirectory(root);
        using PrestageTestKey key = PrestageTestKey.Install();
        TestDescriptorVerification(key);
        TestDecisions(key);
        TestEligibility(Path.Combine(root, "eligibility"));
        TestExactExecutable(Path.Combine(root, "exact"));
        TestHelperArguments();
        await TestDownloadsAsync(Path.Combine(root, "downloads"));
        TestSwapAndRollbackAtEachStep(key, Path.Combine(root, "swap"));
        TestRecoveryFromEachTornState(key, Path.Combine(root, "recovery"));
        TestRecoveryLeavesLiveHelperAlone(key, Path.Combine(root, "live-helper"));
        TestCleanup(key, Path.Combine(root, "cleanup"));
        await TestPrepareEndToEndAsync(key, Path.Combine(root, "prepare"));
        Console.WriteLine("Updater pre-staging checks passed: signed next channel, newer/equal/older policy, bootstrap-only eligibility, verified resumable download, swap rollback at every step, torn-state recovery, and cleanup.");
    }

    // ---------------------------------------------------------------- descriptor + policy

    private static void TestDescriptorVerification(PrestageTestKey key)
    {
        (byte[] json, byte[] sig, _) = key.Channel(NewVersion, FakeExe("verify", 17));
        VerifiedUpdaterChannel verified = UpdaterPrestage.Verify(json, sig);
        Check(verified.Version == Version.Parse(NewVersion), "verified next channel version");
        Check(verified.DescriptorBytes.AsSpan().SequenceEqual(json), "verified channel keeps the exact signed bytes");

        byte[] tampered = json.ToArray();
        tampered[^3] ^= 1;
        Throws<InvalidDataException>(() => UpdaterPrestage.Verify(tampered, sig), "tampered next.json");
        // next.json must be a *stable* descriptor: the pinned 1.2.7 verifier (same Validate) rejects any other channel name.
        (byte[] nextNamed, byte[] nextNamedSig, _) = key.Channel(NewVersion, FakeExe("verify", 17), channel: "next");
        Throws<InvalidDataException>(() => UpdaterPrestage.Verify(nextNamed, nextNamedSig), "descriptor naming channel 'next'");
        byte[] oversized = Encoding.UTF8.GetBytes(new string(' ', UpdaterPrestage.MaximumChannelBytes + 1));
        Throws<InvalidDataException>(() => UpdaterPrestage.Verify(oversized, key.Sign(oversized)), "oversized next.json");
        byte[] foreignSignature = JsonSerializer.SerializeToUtf8Bytes(new DetachedSignature
        {
            KeyId = "unknown-key",
            Signature = Convert.ToBase64String(new byte[64])
        }, PrestageTestKey.SignatureJson);
        Throws<InvalidDataException>(() => UpdaterPrestage.Verify(json, foreignSignature), "unknown signing key");
        Check(UpdaterPrestage.TryReadVerified(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid()), "missing.sig") is null, "missing cache reads as null");
    }

    private static void TestDecisions(PrestageTestKey key)
    {
        byte[] installedExe = FakeExe("decide-installed", 3);
        VerifiedUpdaterChannel installed = key.Verified("1.2.18", installedExe);
        Version own = Version.Parse("1.2.18");
        Check(UpdaterPrestage.Decide(installed, own, key.Verified("1.2.19", FakeExe("decide-new", 4))) == PrestageDecision.Prestage, "newer version is staged");
        Check(UpdaterPrestage.Decide(installed, own, key.Verified("1.2.18", installedExe)) == PrestageDecision.NotNewer, "equal version is not staged");
        Check(UpdaterPrestage.Decide(installed, own, key.Verified("1.2.18", FakeExe("decide-other", 5))) == PrestageDecision.NotNewer, "equal version with other bytes is not staged");
        Check(UpdaterPrestage.Decide(installed, own, key.Verified("1.2.17", FakeExe("decide-old", 6))) == PrestageDecision.NotNewer, "older version is not staged");
        Check(UpdaterPrestage.Decide(installed, own, key.Verified("1.2.19", installedExe)) == PrestageDecision.SameExecutable, "newer version naming the same exe is not staged");
        Check(UpdaterPrestage.Decide(installed, Version.Parse("1.2.20"), key.Verified("1.2.19", FakeExe("decide-new", 4))) == PrestageDecision.NotNewer, "never stage below the running build's own version");
    }

    private static void TestEligibility(string root)
    {
        string instance = Path.Combine(root, "Instance With Spaces");
        string install = Path.Combine(instance, "minecraft", "cobble-music-updater");
        string exe = Path.Combine(install, "CobbleMusicUpdater.exe");
        string? Reason(bool prelaunch = true, bool checkOnly = false, string? process = null, string? installation = null, bool chain = true, bool verifier = true) =>
            UpdaterPrestage.GetIneligibilityReason(prelaunch, checkOnly, instance, installation ?? install, process ?? exe, chain, verifier);

        Check(Reason() is null, "bootstrap launch shape is eligible");
        Check(Reason(process: exe.ToUpperInvariant()) is null, "path comparison is case-insensitive like Windows");
        Check(Reason(prelaunch: false) is not null, "non-prelaunch run is not eligible");
        Check(Reason(checkOnly: true) is not null, "check-only run is not eligible");
        Check(Reason(process: Path.Combine(install, "prestage", "swap-helper-x.exe")) is not null, "helper path is not eligible");
        Check(Reason(process: Path.Combine(root, "elsewhere", "CobbleMusicUpdater.exe")) is not null, "exe outside the bootstrap target is not eligible");
        string otherInstall = Path.Combine(instance, "other-minecraft", "cobble-music-updater");
        Check(Reason(process: Path.Combine(otherInstall, "CobbleMusicUpdater.exe"), installation: otherInstall) is not null, "minecraft folder other than <instance>\\minecraft is not eligible");
        Check(Reason(chain: false) is not null, "a run not started by Prism's pre-launch PowerShell is not eligible");
        Check(Reason(verifier: false) is not null, "a run without the bootstrap's pinned verifier is not eligible");
        Check(UpdaterPrestage.GetIneligibilityReason(true, false, instance, install, null, true, true) is not null, "unknown process path is not eligible");
    }

    private static void TestExactExecutable(string root)
    {
        Directory.CreateDirectory(root);
        byte[] bytes = FakeExe("exact", 9);
        string path = Path.Combine(root, "a.exe");
        File.WriteAllBytes(path, bytes);
        string sha = Sha(bytes);
        Check(UpdaterPrestage.IsExactExecutable(path, bytes.Length, sha), "exact executable");
        Check(UpdaterPrestage.IsExactExecutable(path, bytes.Length, sha.ToUpperInvariant()), "hash comparison ignores case like the bootstrap");
        Check(!UpdaterPrestage.IsExactExecutable(path, bytes.Length + 1, sha), "size mismatch");
        Check(!UpdaterPrestage.IsExactExecutable(path, bytes.Length, Sha(FakeExe("exact-other", 9))), "hash mismatch");
        Check(!UpdaterPrestage.IsExactExecutable(Path.Combine(root, "missing.exe"), bytes.Length, sha), "missing file");
        byte[] notPe = bytes.ToArray();
        notPe[0] = (byte)'X';
        File.WriteAllBytes(path, notPe);
        Check(!UpdaterPrestage.IsExactExecutable(path, notPe.Length, Sha(notPe)), "non-MZ file with its own exact hash");
    }

    private static void TestHelperArguments()
    {
        string[] Args(params string[] rest) => [UpdaterPrestageSwap.HelperSwitch, .. rest];
        Check(UpdaterPrestageSwap.TryParseHelperArguments(
            Args("--next-version", "1.2.19", "--parent-pid", "42", "--parent-start-ticks", "638000000000000000", "--lock-file", @"C:\x\update.lock"),
            out string version, out int pid, out long ticks, out string lockFile)
            && version == "1.2.19" && pid == 42 && ticks == 638000000000000000 && lockFile == @"C:\x\update.lock", "valid helper arguments");
        Check(!UpdaterPrestageSwap.TryParseHelperArguments(Args("--next-version", "1.2", "--parent-pid", "42", "--parent-start-ticks", "1", "--lock-file", "update.lock"), out _, out _, out _, out _), "non-canonical version rejected");
        Check(!UpdaterPrestageSwap.TryParseHelperArguments(Args("--next-version", "1.2.19", "--parent-pid", "42", "--parent-start-ticks", "1", "--lock-file", "other.lock"), out _, out _, out _, out _), "unexpected lock file rejected");
        Check(!UpdaterPrestageSwap.TryParseHelperArguments(Args("--next-version", "1.2.19", "--parent-pid", "42", "--parent-start-ticks", "1"), out _, out _, out _, out _), "missing argument rejected");
        Check(!UpdaterPrestageSwap.TryParseHelperArguments(Args("--next-version", "1.2.19", "--next-version", "1.2.20", "--parent-pid", "42", "--parent-start-ticks", "1", "--lock-file", "update.lock"), out _, out _, out _, out _), "duplicate argument rejected");
        Check(!UpdaterPrestageSwap.TryParseHelperArguments(Args("--next-version", "1.2.19", "--parent-pid", "-4", "--parent-start-ticks", "1", "--lock-file", "update.lock"), out _, out _, out _, out _), "signed pid rejected");
    }

    // ---------------------------------------------------------------- download

    private static async Task TestDownloadsAsync(string root)
    {
        Directory.CreateDirectory(root);
        byte[] exe = FakeExe("download", 123_457);
        string sha = Sha(exe);
        var source = new Uri("https://github.com/Kewz4/Cobble-Music/releases/download/updater-v1.2.31/CobbleMusicUpdater.exe");
        TimeSpan[] fastRetries = [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)];

        async Task<(ScriptedHandler Handler, Exception? Failure, string Partial, string Final)> Run(string name, Func<HttpRequestMessage, int, HttpResponseMessage> respond, byte[]? existingPartial = null, TimeSpan? inactivity = null)
        {
            string partial = Path.Combine(root, name + ".partial");
            string final = Path.Combine(root, name + ".exe");
            if (existingPartial is not null)
            {
                File.WriteAllBytes(partial, existingPartial);
            }
            var handler = new ScriptedHandler(respond);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var downloader = new PrestageDownloader(http, inactivity ?? TimeSpan.FromSeconds(30), fastRetries, _ => { });
            Exception? failure = null;
            try
            {
                await downloader.DownloadVerifiedAsync(source, partial, final, exe.Length, sha, CancellationToken.None);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            return (handler, failure, partial, final);
        }

        var full = await Run("full", (_, _) => Full(exe));
        Check(full.Failure is null && Sha(File.ReadAllBytes(full.Final)) == sha && !File.Exists(full.Partial), "full download verified and moved into place");
        Check(full.Handler.Requests.Single().RequestUri == source, "download uses the signed release URL");

        var resumed = await Run("resume", (request, _) => Partial(exe, request.Headers.Range!.Ranges.Single().From!.Value), exe[..400_000]);
        Check(resumed.Failure is null && Sha(File.ReadAllBytes(resumed.Final)) == sha, "resumed download verified");
        Check(resumed.Handler.Requests.Single().Headers.Range?.Ranges.Single().From == 400_000, "resume requests the missing tail only");

        var rangeIgnored = await Run("range-ignored", (_, _) => Full(exe), exe[..1000]);
        Check(rangeIgnored.Failure is null && Sha(File.ReadAllBytes(rangeIgnored.Final)) == sha, "a server ignoring Range restarts from zero");

        var stalled = await Run("stall", (request, call) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(exe[..1000])) }
            : Partial(exe, request.Headers.Range!.Ranges.Single().From!.Value), inactivity: TimeSpan.FromMilliseconds(300));
        Check(stalled.Failure is null && Sha(File.ReadAllBytes(stalled.Final)) == sha, "a stalled body times out and the retry resumes");
        Check(stalled.Handler.Requests.Count == 2 && stalled.Handler.Requests[1].Headers.Range?.Ranges.Single().From == 1000, "stall retry resumes at the bytes already written");

        var serverError = await Run("server-error", (_, call) => call == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Full(exe));
        Check(serverError.Failure is null && serverError.Handler.Requests.Count == 2, "5xx is retried");

        var notFound = await Run("not-found", (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        Check(notFound.Failure is HttpRequestException && notFound.Handler.Requests.Count == 1 && !File.Exists(notFound.Final), "404 is not retried and stages nothing");

        byte[] wrong = FakeExe("download-wrong", 123_457);
        var wrongBytes = await Run("wrong-hash", (_, _) => Full(wrong));
        Check(wrongBytes.Failure is PrestageIntegrityException && wrongBytes.Handler.Requests.Count == fastRetries.Length + 1, "wrong bytes are retried then rejected");
        Check(!File.Exists(wrongBytes.Final) && !File.Exists(wrongBytes.Partial), "rejected bytes are neither staged nor kept for resume");

        byte[] longer = [.. exe, .. new byte[10]];
        var oversized = await Run("oversized", (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(longer)) });
        Check(oversized.Failure is PrestageIntegrityException && !File.Exists(oversized.Final) && !File.Exists(oversized.Partial), "a body longer than the signed size is rejected");

        var badRange = await Run("bad-range", (request, _) =>
        {
            HttpResponseMessage response = Partial(exe, request.Headers.Range?.Ranges.Single().From ?? 0);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, exe.Length - 1, exe.Length);
            return response;
        }, exe[..5000]);
        Check(badRange.Failure is PrestageIntegrityException && !File.Exists(badRange.Final), "a mismatched Content-Range is rejected");

        // Cancellation (the grace limit or process shutdown) keeps the partial for the next launch.
        string cancelPartial = Path.Combine(root, "cancel.partial");
        using (var cancellation = new CancellationTokenSource())
        {
            var handler = new ScriptedHandler((_, _) =>
            {
                cancellation.CancelAfter(200);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(exe[..2000])) };
            });
            using var http = new HttpClient(handler);
            var downloader = new PrestageDownloader(http, TimeSpan.FromSeconds(30), fastRetries, _ => { });
            Exception? failure = null;
            try
            {
                await downloader.DownloadVerifiedAsync(source, cancelPartial, Path.Combine(root, "cancel.exe"), exe.Length, sha, cancellation.Token);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            Check(failure is OperationCanceledException && handler.Requests.Count == 1, "cancellation is not retried");
            Check(File.Exists(cancelPartial) && new FileInfo(cancelPartial).Length == 2000, "cancellation keeps the partial for resume");
        }
    }

    // ---------------------------------------------------------------- swap

    private static void TestSwapAndRollbackAtEachStep(PrestageTestKey key, string root)
    {
        {
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "success"));
            UpdaterPrestageSwap.Execute(fixture.Layout, NewVersion, _ => { });
            fixture.AssertInstalled(fixture.New, "successful swap");
            Check(!File.Exists(fixture.Layout.StagedExe(fixture.New.Channel.Descriptor)), "staged exe moved into place");
            Check(!File.Exists(fixture.Layout.RollbackExe) && !File.Exists(fixture.Layout.RollbackChannel), "rollback material removed after commit");
            UpdaterPrestageSwap.Execute(fixture.Layout, NewVersion, _ => { });
            fixture.AssertInstalled(fixture.New, "repeating a finished swap is a no-op");
        }

        foreach (PrestageSwapStep step in Enum.GetValues<PrestageSwapStep>())
        {
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "fail-" + step));
            Throws<InvalidOperationException>(() => UpdaterPrestageSwap.Execute(fixture.Layout, NewVersion, _ => { }, current =>
            {
                if (current == step)
                {
                    throw new InvalidOperationException("injected failure after " + step);
                }
            }), "failure after " + step);
            fixture.AssertInstalled(fixture.Old, "rollback after failure at " + step);
            Check(UpdaterPrestage.IsExactExecutable(fixture.Layout.StagedExe(fixture.New.Channel.Descriptor), fixture.New.Channel.Updater), "verified download survives rollback at " + step);
            UpdaterPrestageSwap.Execute(fixture.Layout, NewVersion, _ => { });
            fixture.AssertInstalled(fixture.New, "retry after rollback at " + step);
        }

        {
            // A new updater launched by the next Play holds CobbleMusicUpdater.exe without delete sharing: the exe rename fails.
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "target-in-use"));
            using (new FileStream(fixture.Layout.TargetExe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Windows reports the refused rename as ERROR_ACCESS_DENIED (UnauthorizedAccessException) or a sharing violation (IOException).
                Exception? failure = null;
                try
                {
                    UpdaterPrestageSwap.Execute(fixture.Layout, NewVersion, _ => { });
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                Check(failure is UnauthorizedAccessException or IOException, "target in use refuses the exe rename: " + failure?.GetType().Name);
            }
            fixture.AssertInstalled(fixture.Old, "rollback when the installed exe is in use");
        }

        {
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "tampered-staged"));
            byte[] staged = File.ReadAllBytes(fixture.Layout.StagedExe(fixture.New.Channel.Descriptor));
            staged[^1] ^= 1;
            File.WriteAllBytes(fixture.Layout.StagedExe(fixture.New.Channel.Descriptor), staged);
            Throws<InvalidDataException>(() => UpdaterPrestageSwap.Execute(fixture.Layout, NewVersion, _ => { }), "tampered staged exe");
            fixture.AssertInstalled(fixture.Old, "tampered staged exe changes nothing");
        }

        {
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "unsigned-next"));
            File.WriteAllBytes(fixture.Layout.NextSignature(NewVersion), key.Sign(Encoding.UTF8.GetBytes("other")));
            Throws<InvalidDataException>(() => UpdaterPrestageSwap.Execute(fixture.Layout, NewVersion, _ => { }), "next.sig not matching next.json");
            fixture.AssertInstalled(fixture.Old, "unsigned next changes nothing");
        }

        {
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "installed-mismatch"));
            File.WriteAllBytes(fixture.Layout.TargetExe, FakeExe("somebody-else", 11));
            Throws<InvalidDataException>(() => UpdaterPrestageSwap.Execute(fixture.Layout, NewVersion, _ => { }), "installed exe not matching its cache");
            Check(File.ReadAllBytes(fixture.Layout.CachedChannel).AsSpan().SequenceEqual(fixture.Old.Json), "mismatch leaves the cache alone");
        }
    }

    // ---------------------------------------------------------------- recovery + cleanup

    private static void TestRecoveryFromEachTornState(PrestageTestKey key, string root)
    {
        // Each case is the on-disk state a process death leaves right after the named step (UpdaterPrestageSwap.Execute order).
        {
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "after-rollback-prepared"));
            fixture.PrepareRollback();
            Check(UpdaterPrestageRecovery.Run(fixture.Layout, _ => { }).Installed?.Version == Version.Parse(OldVersion), "recovery after rollback prepared");
            fixture.AssertInstalled(fixture.Old, "state after rollback prepared");
            Check(!File.Exists(fixture.Layout.RollbackExe), "rollback exe cleaned");
            Check(File.Exists(fixture.Layout.StagedExe(fixture.New.Channel.Descriptor)), "newer staged exe kept for the retry");
        }
        {
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "after-channel"));
            fixture.PrepareRollback();
            File.WriteAllBytes(fixture.Layout.CachedChannel, fixture.New.Json);
            Check(UpdaterPrestageRecovery.Run(fixture.Layout, _ => { }).Installed?.Version == Version.Parse(OldVersion), "recovery after channel written");
            fixture.AssertInstalled(fixture.Old, "torn cache rolled back to the installed exe");
        }
        {
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "after-signature"));
            fixture.PrepareRollback();
            File.WriteAllBytes(fixture.Layout.CachedChannel, fixture.New.Json);
            File.WriteAllBytes(fixture.Layout.CachedSignature, fixture.New.Sig);
            Check(UpdaterPrestageRecovery.Run(fixture.Layout, _ => { }).Installed?.Version == Version.Parse(OldVersion), "recovery after signature written");
            fixture.AssertInstalled(fixture.Old, "new cache with old exe rolled back");
        }
        {
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "after-exe"));
            fixture.PrepareRollback();
            File.WriteAllBytes(fixture.Layout.CachedChannel, fixture.New.Json);
            File.WriteAllBytes(fixture.Layout.CachedSignature, fixture.New.Sig);
            File.Move(fixture.Layout.StagedExe(fixture.New.Channel.Descriptor), fixture.Layout.TargetExe, overwrite: true);
            Check(UpdaterPrestageRecovery.Run(fixture.Layout, _ => { }).Installed?.Version == Version.Parse(NewVersion), "recovery after exe replaced");
            fixture.AssertInstalled(fixture.New, "completed swap kept");
            Check(!File.Exists(fixture.Layout.RollbackExe) && !File.Exists(fixture.Layout.NextChannel(NewVersion)), "rollback and reached next material cleaned");
        }
        {
            // Exe new but cache old (e.g. the bootstrap re-cached stable afterwards): roll the cache forward from the staged next.
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "exe-ahead"));
            File.Copy(fixture.Layout.StagedExe(fixture.New.Channel.Descriptor), fixture.Layout.TargetExe, overwrite: true);
            Check(UpdaterPrestageRecovery.Run(fixture.Layout, _ => { }).Installed?.Version == Version.Parse(NewVersion), "recovery rolls the cache forward");
            fixture.AssertInstalled(fixture.New, "cache rolled forward to the exe on disk");
        }
        {
            // Nothing signed names the exe on disk: recovery refuses to invent a pairing and changes nothing.
            Fixture fixture = Fixture.Create(key, Path.Combine(root, "unprovable"));
            byte[] foreign = FakeExe("foreign", 13);
            File.WriteAllBytes(fixture.Layout.TargetExe, foreign);
            Check(UpdaterPrestageRecovery.Run(fixture.Layout, _ => { }).Installed is null, "unprovable state is not repaired");
            Check(File.ReadAllBytes(fixture.Layout.CachedChannel).AsSpan().SequenceEqual(fixture.Old.Json)
                && File.ReadAllBytes(fixture.Layout.TargetExe).AsSpan().SequenceEqual(foreign), "unprovable state left untouched");
        }
    }

    private static void TestRecoveryLeavesLiveHelperAlone(PrestageTestKey key, string root)
    {
        Fixture fixture = Fixture.Create(key, Path.Combine(root, "state"));
        fixture.PrepareRollback();
        File.WriteAllBytes(fixture.Layout.CachedChannel, fixture.New.Json);
        // Any long-running image named like a helper stands in for a helper that is mid-swap.
        string helperImage = Path.Combine(fixture.Layout.Directory, PrestageLayout.HelperPrefix + "live.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), helperImage);
        using Process helper = Process.Start(new ProcessStartInfo(helperImage, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            Check(ProcessTree.TryGetIdentity(helper.Id, out ProcessIdentity identity), "live helper identity");
            File.WriteAllText(fixture.Layout.Journal, JsonSerializer.Serialize(new { HelperPid = identity.Pid, HelperStartUtcTicks = identity.StartUtc.Ticks, NextVersion = NewVersion }));
            Check(UpdaterPrestageSwap.IsJournaledHelperAlive(fixture.Layout), "journaled helper detected");
            UpdaterPrestageRecovery.Result result = UpdaterPrestageRecovery.Run(fixture.Layout, _ => { });
            Check(result.HelperActive && result.Installed is null, "recovery defers to a live helper");
            Check(File.ReadAllBytes(fixture.Layout.CachedChannel).AsSpan().SequenceEqual(fixture.New.Json) && File.Exists(fixture.Layout.RollbackExe), "live helper's files untouched");
        }
        finally
        {
            helper.Kill();
            helper.WaitForExit();
        }
        Check(!UpdaterPrestageSwap.IsJournaledHelperAlive(fixture.Layout), "exited helper is not alive");
        Check(UpdaterPrestageRecovery.Run(fixture.Layout, _ => { }).Installed?.Version == Version.Parse(OldVersion), "recovery proceeds once the helper is gone");
        Check(!File.Exists(fixture.Layout.Journal) && !File.Exists(helperImage), "journal and finished helper removed");
    }

    private static void TestCleanup(PrestageTestKey key, string root)
    {
        Fixture fixture = Fixture.Create(key, Path.Combine(root, "state"));
        string layoutDirectory = fixture.Layout.Directory;
        (byte[] olderJson, byte[] olderSig, byte[] olderExe) = key.Channel("1.2.29", FakeExe("older", 21));
        VerifiedUpdaterChannel older = UpdaterPrestage.Verify(olderJson, olderSig);
        File.WriteAllBytes(fixture.Layout.NextChannel("1.2.29"), olderJson);
        File.WriteAllBytes(fixture.Layout.NextSignature("1.2.29"), olderSig);
        File.WriteAllBytes(fixture.Layout.StagedExe(older.Descriptor), olderExe);
        File.WriteAllBytes(fixture.Layout.PartialExe(older.Descriptor), olderExe[..100]);
        File.WriteAllBytes(fixture.Layout.PartialExe(fixture.New.Channel.Descriptor), [1, 2, 3]);
        string finishedHelper = Path.Combine(layoutDirectory, PrestageLayout.HelperPrefix + "done.exe");
        File.WriteAllBytes(finishedHelper, olderExe);
        string cacheTemporary = fixture.Layout.CachedChannel + ".prestage-0123";
        File.WriteAllBytes(cacheTemporary, [1]);
        fixture.PrepareRollback();

        Check(UpdaterPrestageRecovery.Run(fixture.Layout, _ => { }).Installed?.Version == Version.Parse(OldVersion), "cleanup run");
        foreach (string removed in new[]
        {
            fixture.Layout.NextChannel("1.2.29"), fixture.Layout.NextSignature("1.2.29"), fixture.Layout.StagedExe(older.Descriptor),
            fixture.Layout.PartialExe(older.Descriptor), finishedHelper, cacheTemporary, fixture.Layout.RollbackExe,
            fixture.Layout.RollbackChannel, fixture.Layout.RollbackSignature
        })
        {
            Check(!File.Exists(removed), "cleanup removed " + Path.GetFileName(removed));
        }
        foreach (string kept in new[]
        {
            fixture.Layout.NextChannel(NewVersion), fixture.Layout.NextSignature(NewVersion),
            fixture.Layout.StagedExe(fixture.New.Channel.Descriptor), fixture.Layout.PartialExe(fixture.New.Channel.Descriptor)
        })
        {
            Check(File.Exists(kept), "cleanup kept newer " + Path.GetFileName(kept));
        }
        fixture.AssertInstalled(fixture.Old, "cleanup never touches the installed pair");
    }

    // ---------------------------------------------------------------- end to end (fake network)

    private static async Task TestPrepareEndToEndAsync(PrestageTestKey key, string root)
    {
        Fixture fixture = Fixture.Create(key, Path.Combine(root, "state"), stageNext: false);
        var assetUri = new Uri($"https://github.com/Kewz4/Cobble-Music/releases/download/updater-v{NewVersion}/CobbleMusicUpdater.exe");
        HttpResponseMessage Serve(HttpRequestMessage request, byte[] json, byte[] sig)
        {
            if (request.RequestUri == UpdaterPrestage.NextChannelUri) return Full(json);
            if (request.RequestUri == UpdaterPrestage.NextSignatureUri) return Full(sig);
            if (request.RequestUri == assetUri) return Full(fixture.New.Exe);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        async Task<(PrestagePreparation? Result, Exception? Failure, ScriptedHandler Handler)> Prepare(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        {
            var handler = new ScriptedHandler(respond);
            using var http = new HttpClient(handler);
            var downloader = new PrestageDownloader(http, TimeSpan.FromSeconds(30), [TimeSpan.FromMilliseconds(5)], _ => { });
            try
            {
                return (await UpdaterPrestagePreparer.PrepareAsync(fixture.Layout, Version.Parse(OldVersion), http, downloader, _ => { }, CancellationToken.None), null, handler);
            }
            catch (Exception exception)
            {
                return (null, exception, handler);
            }
        }

        var none = await Prepare((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        Check(none.Result is { Ready: null } && none.Handler.Requests.Count == 1, "no next.json published: nothing staged, one request");

        (byte[] foreignJson, _, _) = key.Channel(NewVersion, fixture.New.Exe);
        byte[] forged = JsonSerializer.SerializeToUtf8Bytes(new DetachedSignature { KeyId = "unknown-key", Signature = Convert.ToBase64String(new byte[64]) }, PrestageTestKey.SignatureJson);
        var unsigned = await Prepare((request, _) => Serve(request, foreignJson, forged));
        Check(unsigned.Failure is InvalidDataException && !unsigned.Handler.Requests.Any(r => r.RequestUri == assetUri), "unsigned next.json downloads nothing");

        (byte[] olderJson, byte[] olderSig, _) = key.Channel("1.2.29", FakeExe("older-e2e", 2));
        var older = await Prepare((request, _) => Serve(request, olderJson, olderSig));
        Check(older.Result is { Ready: null } && !older.Handler.Requests.Any(r => r.RequestUri == assetUri), "older next.json downloads nothing");

        var staged = await Prepare((request, _) => Serve(request, fixture.New.Json, fixture.New.Sig));
        Check(staged.Result?.Ready?.Version == Version.Parse(NewVersion), "newer next.json is staged");
        Check(UpdaterPrestage.IsExactExecutable(fixture.Layout.StagedExe(fixture.New.Channel.Descriptor), fixture.New.Channel.Updater), "staged exe verified");
        Check(File.ReadAllBytes(fixture.Layout.NextChannel(NewVersion)).AsSpan().SequenceEqual(fixture.New.Json)
            && File.ReadAllBytes(fixture.Layout.NextSignature(NewVersion)).AsSpan().SequenceEqual(fixture.New.Sig), "exact signed next bytes kept beside the staged exe");
        fixture.AssertInstalled(fixture.Old, "preparing never touches the installed pair");

        var again = await Prepare((request, _) => Serve(request, fixture.New.Json, fixture.New.Sig));
        Check(again.Result?.Ready is not null && !again.Handler.Requests.Any(r => r.RequestUri == assetUri), "an already staged exe is not downloaded twice");

        UpdaterPrestageSwap.Execute(fixture.Layout, NewVersion, _ => { });
        fixture.AssertInstalled(fixture.New, "swap after end-to-end preparation");
        var after = await Prepare((request, _) => Serve(request, fixture.New.Json, fixture.New.Sig));
        Check(after.Result is { Ready: null } && !Directory.GetFiles(fixture.Layout.Directory, "next-*").Any(), "once installed, next is not newer and its files are cleaned");
    }

    // ---------------------------------------------------------------- fixtures

    internal sealed record SignedExe(byte[] Exe, byte[] Json, byte[] Sig, VerifiedUpdaterChannel Channel);

    internal sealed class Fixture
    {
        public required PrestageLayout Layout { get; init; }
        public required SignedExe Old { get; init; }
        public required SignedExe New { get; init; }

        public static Fixture Create(PrestageTestKey key, string installDirectory, bool stageNext = true, string oldVersion = OldVersion, string newVersion = NewVersion)
        {
            Directory.CreateDirectory(installDirectory);
            var layout = new PrestageLayout(installDirectory);
            SignedExe old = key.Signed(oldVersion, FakeExe("old-" + oldVersion, 1));
            SignedExe next = key.Signed(newVersion, FakeExe("new-" + newVersion, 2));
            File.WriteAllBytes(layout.TargetExe, old.Exe);
            File.WriteAllBytes(layout.CachedChannel, old.Json);
            File.WriteAllBytes(layout.CachedSignature, old.Sig);
            Directory.CreateDirectory(layout.Directory);
            if (stageNext)
            {
                File.WriteAllBytes(layout.NextChannel(newVersion), next.Json);
                File.WriteAllBytes(layout.NextSignature(newVersion), next.Sig);
                File.WriteAllBytes(layout.StagedExe(next.Channel.Descriptor), next.Exe);
            }
            return new Fixture { Layout = layout, Old = old, New = next };
        }

        /// <summary>Step 0 of the swap, exactly as Execute performs it.</summary>
        public void PrepareRollback()
        {
            File.WriteAllBytes(Layout.RollbackChannel, Old.Json);
            File.WriteAllBytes(Layout.RollbackSignature, Old.Sig);
            UpdaterPrestage.HardLinkOrCopy(Layout.TargetExe, Layout.RollbackExe);
        }

        /// <summary>The bootstrap-visible pair is exactly <paramref name="expected"/>: cache bytes and the exe the cache names.</summary>
        public void AssertInstalled(SignedExe expected, string context)
        {
            Check(File.ReadAllBytes(Layout.CachedChannel).AsSpan().SequenceEqual(expected.Json), context + ": cache json");
            Check(File.ReadAllBytes(Layout.CachedSignature).AsSpan().SequenceEqual(expected.Sig), context + ": cache sig");
            Check(File.ReadAllBytes(Layout.TargetExe).AsSpan().SequenceEqual(expected.Exe), context + ": exe");
        }
    }

    internal static byte[] FakeExe(string label, int extraBytes)
    {
        // At least the channel's 1 MiB minimum, "MZ" first like any PE file, deterministic content per label.
        var bytes = new byte[(int)UpdaterChannelParser.MinimumUpdaterBytes + extraBytes];
        byte[] block = SHA256.HashData(Encoding.UTF8.GetBytes(label));
        for (int offset = 0; offset < bytes.Length; offset += block.Length)
        {
            block = SHA256.HashData(block);
            block.AsSpan(0, Math.Min(block.Length, bytes.Length - offset)).CopyTo(bytes.AsSpan(offset));
        }
        bytes[0] = 0x4d;
        bytes[1] = 0x5a;
        return bytes;
    }

    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpResponseMessage Full(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Partial(byte[] body, long from)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[(int)from..]) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
        return response;
    }

    private static void Check(bool condition, string context)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Pre-staging check failed: " + context);
        }
    }

    private static void Throws<TException>(Action action, string context) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Pre-staging check failed: {context} threw {exception.GetType().Name} instead of {typeof(TException).Name}: {exception.Message}");
        }
        throw new InvalidOperationException($"Pre-staging check failed: {context} did not throw {typeof(TException).Name}.");
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request, Requests.Count));
        }
    }

    /// <summary>Returns some bytes, then never completes a read until cancelled (a stalled CDN connection).</summary>
    private sealed class StallingStream(byte[] prefix) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                int count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class NonSeekableStream(byte[] body) : Stream
    {
        private readonly MemoryStream _inner = new(body);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// A fixed, test-only Ed25519 key installed into this TEST process's key ring by reflection (the same technique as
/// ConvergenceTestSigner). Production binaries never contain it; the real-key path is covered by the real signed 1.2.7 and
/// current stable descriptors in tests/Test-UpdaterPrestageBootstrap.ps1.
/// </summary>
internal sealed class PrestageTestKey : IDisposable
{
    internal const string KeyId = "prestage-fixture-test";
    internal static readonly JsonSerializerOptions SignatureJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly byte[] Seed = SHA256.HashData(Encoding.UTF8.GetBytes("cobble-music-prestage-fixture-key-v1"));
    private readonly IDictionary<string, byte[]> _keys;
    private readonly bool _added;

    private PrestageTestKey()
    {
        _keys = (IDictionary<string, byte[]>)(typeof(TrustedKeyRing).GetField("Keys",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Could not install the pre-staging fixture public key."));
        _added = _keys.TryAdd(KeyId, new Ed25519PrivateKeyParameters(Seed, 0).GeneratePublicKey().GetEncoded());
    }

    public static PrestageTestKey Install() => new();

    public byte[] Sign(byte[] bytes) => JsonSerializer.SerializeToUtf8Bytes(new DetachedSignature
    {
        KeyId = KeyId,
        Signature = Convert.ToBase64String(ManifestSecurity.Sign(bytes, Seed))
    }, SignatureJson);

    /// <summary>A descriptor in the exact shape the publisher writes (Get-UpdaterChannelText: compressed JSON plus "\n").</summary>
    public (byte[] Json, byte[] Sig, byte[] Exe) Channel(string version, byte[] exe, string channel = "stable")
    {
        string text = "{\"schemaVersion\":1,\"productId\":\"cobble-music-updater\",\"repository\":\"Kewz4/Cobble-Music\","
            + $"\"channel\":\"{channel}\",\"updaterVersion\":\"{version}\",\"releaseTag\":\"updater-v{version}\","
            + $"\"updater\":{{\"name\":\"CobbleMusicUpdater.exe\",\"size\":{exe.Length},\"sha256\":\"{PrestageTests.Sha(exe)}\"}}}}\n";
        byte[] json = Encoding.UTF8.GetBytes(text);
        return (json, Sign(json), exe);
    }

    public PrestageTests.SignedExe Signed(string version, byte[] exe)
    {
        (byte[] json, byte[] sig, _) = Channel(version, exe);
        return new PrestageTests.SignedExe(exe, json, sig, UpdaterPrestage.Verify(json, sig));
    }

    public VerifiedUpdaterChannel Verified(string version, byte[] exe) => Signed(version, exe).Channel;

    public void Dispose()
    {
        if (_added)
        {
            _keys.Remove(KeyId);
        }
    }
}

/// <summary>
/// Command-line fixtures for tests/Test-UpdaterPrestageBootstrap.ps1:
///   --verify-updater-channel ...          the production verifier CLI (CobbleMusicUpdater.Program.Main, byte-identical logic to the
///                                         pinned 1.2.7 verifier) with the fixture key added, used as the bootstrap's $verifierExe;
///   --prestage-fixture make &lt;dir&gt; &lt;old&gt; &lt;new&gt;             signed fake old/new updaters;
///   --prestage-fixture swap &lt;install&gt; &lt;version&gt; &lt;crashStep|none&gt; [--real-key]
///                                         the real swap; a crash step kills this process right after that step (no rollback runs);
///   --prestage-fixture recover &lt;install&gt; [--real-key]
///   --prestage-tests &lt;dir&gt;                                the unit tests above only.
/// </summary>
internal static class PrestageFixtureCli
{
    public static bool Handles(string[] args) =>
        args.Length > 0 && args[0] is "--verify-updater-channel" or "--prestage-fixture" or "--prestage-tests";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args[0] == "--verify-updater-channel")
            {
                using PrestageTestKey key = PrestageTestKey.Install();
                return CobbleMusicUpdater.Program.Main(args);
            }
            if (args[0] == "--prestage-tests")
            {
                try
                {
                    await PrestageTests.RunAllAsync(args[1]);
                }
                finally
                {
                    Directory.Delete(args[1], recursive: true);
                }
                return 0;
            }
            bool realKey = args.Contains("--real-key", StringComparer.Ordinal);
            using PrestageTestKey? fixtureKey = realKey ? null : PrestageTestKey.Install();
            switch (args[1])
            {
                case "make":
                {
                    string directory = args[2];
                    Directory.CreateDirectory(directory);
                    foreach ((string name, string version) in new[] { ("old", args[3]), ("new", args[4]) })
                    {
                        (byte[] json, byte[] sig, byte[] exe) = fixtureKey!.Channel(version, PrestageTests.FakeExe(name + "-" + version, name == "old" ? 1 : 2));
                        File.WriteAllBytes(Path.Combine(directory, name + ".exe"), exe);
                        File.WriteAllBytes(Path.Combine(directory, name + ".json"), json);
                        File.WriteAllBytes(Path.Combine(directory, name + ".sig"), sig);
                    }
                    return 0;
                }
                case "swap":
                {
                    var layout = new PrestageLayout(args[2]);
                    string crashStep = args[4];
                    UpdaterPrestageSwap.Execute(layout, args[3], Console.WriteLine, step =>
                    {
                        if (string.Equals(step.ToString(), crashStep, StringComparison.Ordinal))
                        {
                            Console.Out.Flush();
                            // TerminateProcess: no catch, finally or rollback runs, exactly like power loss or End Task.
                            Process.GetCurrentProcess().Kill();
                        }
                    });
                    return 0;
                }
                case "recover":
                {
                    UpdaterPrestageRecovery.Result result = UpdaterPrestageRecovery.Run(new PrestageLayout(args[2]), Console.WriteLine);
                    Console.WriteLine($"installed={result.Installed?.Version.ToString() ?? "none"} helperActive={result.HelperActive}");
                    return result.Installed is null ? 3 : 0;
                }
                default:
                    Console.Error.WriteLine("Unknown pre-staging fixture command.");
                    return 64;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
