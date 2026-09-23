using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Forms;
using CobbleMusicUpdater;

/// <summary>
/// 1.2.18 jvm track: section 11 unit tests 1-9 of workspace/shared/updater0923/jvm-args/FINDINGS.md, plus the pre-launch
/// coordinator and the helper driven end to end through a fake <see cref="IJvmSettingsSystem"/> (no real Prism, window,
/// COM call or process is touched). Fixtures are byte-exact copies of Kewz's real instance.cfg files (Fixtures/Jvm).
/// </summary>
internal static class JvmSettingsTests
{
    private const ulong MiB = 1024UL * 1024;
    private const ulong GiB = 1024UL * MiB;
    private const string HeapDump = "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump";
    private static readonly string AllFlags = string.Join(' ', JvmPolicy.Flags.Select(flag => flag.Token));

    public static void Run(string root)
    {
        Directory.CreateDirectory(root);
        TestSplitArgs();
        TestWindowsArgumentRoundTrip();
        TestIniRoundTripOnRealFiles();
        TestIniStructureRules();
        TestEscapingTable();
        TestGateLogic();
        TestMergeRules();
        TestTierBoundaries();
        TestLaunchArgumentsCrossCheck();
        TestMarkerStateMachine(Path.Combine(root, "marker"));
        TestRelaunchPlanBuilder();
        TestCfgEditsOnRealFiles(Path.Combine(root, "edits"));
        TestCoordinatorRestartAndVerify(Path.Combine(root, "coordinator"));
        TestCoordinatorGuards(Path.Combine(root, "guards"));
        TestHelperValidationAndCancel(Path.Combine(root, "helper-validate"));
        TestHelperCloseEditRelaunch(Path.Combine(root, "helper-relaunch"));
        TestHelperFallbackAndReopen(Path.Combine(root, "helper-fallback"));
        TestHelperForcedCloseGuards(Path.Combine(root, "helper-force"));
        TestLogsFolderAndStatusTexts(Path.Combine(root, "logs"));
        TestStatusCardClosesOnRestartCode();
        Console.WriteLine("JVM settings checks passed: splitArgs port, QSettings INI round trip and escaping, gates, merge rules, tiers, cross-check, marker loop guard, relaunch plan, atomic cfg edit, coordinator and helper flows.");
    }

    // ---- 1. PrismArgs.Split (Commandline.cpp:47-84) ----------------------------------------------------------------------

    private static void TestSplitArgs()
    {
        AssertTokens("a b  c", ["a", "b", "c"], "spaces separate, runs of spaces collapse");
        AssertTokens("  lead trail  ", ["lead", "trail"], "leading/trailing spaces");
        AssertTokens("\"a b\"", ["a b"], "double quotes group");
        AssertTokens("'a b'", ["a b"], "single quotes group");
        AssertTokens("\"a\\\"b\"", ["a\"b"], "\\\" inside double quotes");
        AssertTokens("'it\\'s'", ["it's"], "\\' inside single quotes");
        AssertTokens("\"it's\"", ["it's"], "the other quote is literal inside quotes");
        AssertTokens("\"x\\ny\"", ["xny"], "backslash escapes any character inside quotes");
        AssertTokens("a\\b c\\", ["a\\b", "c\\"], "backslash is literal outside quotes");
        AssertTokens("", [], "empty string");
        AssertTokens("\"\"", [], "empty quotes give no token");
        AssertTokens("x\"\"y", ["xy"], "empty quotes inside a token");
        AssertTokens("a\tb", ["a\tb"], "tab is not a separator");
        AssertTokens("\"a b", ["a b"], "unterminated quote runs to the end");
        AssertTokens("-Dx=\"a b\" c", ["-Dx=a b", "c"], "quotes in the middle of a token");
        AssertTokens("\"a\"'b'", ["ab"], "adjacent quoted parts join");
        AssertTokens("-Djdk.net.unixdomain.tmpdir=C:/Temp -Xlog:gc:file=logs/gc.log:time,uptime:filecount=3,filesize=10M",
            ["-Djdk.net.unixdomain.tmpdir=C:/Temp", "-Xlog:gc:file=logs/gc.log:time,uptime:filecount=3,filesize=10M"], "Validation JvmArgs");
    }

    private static void AssertTokens(string input, string[] expected, string context)
    {
        IReadOnlyList<string> actual = PrismArgs.Split(input);
        Equal(string.Join("|", expected), string.Join("|", actual), "splitArgs: " + context);
        Equal(expected.Length, actual.Count, "splitArgs count: " + context);
    }

    private static void TestWindowsArgumentRoundTrip()
    {
        string[] arguments = ["--dir", @"C:\Program Files\Prism Launcher\", "a\"b", "", @"back\\slash\""", "--launch", "1.21.1(1)", "Kewz's Cobblemon"];
        string commandLine = @"C:\x\prismlauncher.exe " + PrismArgs.JoinWindowsArguments(arguments);
        string[]? parsed = PrismArgs.SplitWindowsCommandLine(commandLine);
        Equal(true, parsed is not null, "CommandLineToArgvW parses");
        Equal(string.Join("|", arguments), string.Join("|", parsed!.Skip(1)), "Windows quoting round-trips through CommandLineToArgvW");
        Equal("--launch", PrismArgs.QuoteWindowsArgument("--launch"), "plain arguments stay unquoted");
    }

    // ---- 2. PrismIni round trip on the real files --------------------------------------------------------------------------

    private static void TestIniRoundTripOnRealFiles()
    {
        foreach (string name in new[] { "instance-1.21.1.cfg", "instance-1.21.1(1).cfg", "instance-validation.cfg", "instance-bedrock.cfg", "prismlauncher.cfg" })
        {
            byte[] bytes = Fixture(name);
            PrismIniDocument document = PrismIniDocument.Parse(bytes);
            Equal(true, bytes.AsSpan().SequenceEqual(document.Serialize()), $"{name}: parse + serialize is byte-identical");
            Equal("\r\n", document.LineEnding, $"{name}: CRLF detected");
            Equal("1.3", document.GetGeneralString("ConfigVersion"), $"{name}: ConfigVersion");
        }
        PrismIniDocument validation = PrismIniDocument.Parse(Fixture("instance-validation.cfg"));
        Equal("-Djdk.net.unixdomain.tmpdir=C:/Temp -Xlog:gc:file=logs/gc.log:time,uptime:filecount=3,filesize=10M",
            validation.GetGeneralString("JvmArgs"), "Validation quoted JvmArgs decodes");
        PrismIniDocument kewz = PrismIniDocument.Parse(Fixture("instance-1.21.1.cfg"));
        string preLaunch = kewz.GetGeneralString("PreLaunchCommand")!;
        Equal(true, preLaunch.StartsWith("powershell.exe -NoLogo", StringComparison.Ordinal) && preLaunch.EndsWith('\n'),
            "the friend PreLaunchCommand decodes, with its escaped trailing newline");
        Equal(PrismIni.EncodeString(preLaunch), kewz.FindGeneral("PreLaunchCommand")!.RawValue,
            "re-encoding the friend command reproduces Qt's exact line");
        PrismIniDocument other = PrismIniDocument.Parse(Fixture("instance-1.21.1(1).cfg"));
        Equal<PrismIniEntry?>(null, other.FindGeneral("JvmArgs"), "1.21.1(1) has no JvmArgs key");
        Equal(null, other.FindGeneral("mods_Page\\Columns") is null ? null : "found", "[UI] keys are not [General] keys");
    }

    private static void TestIniStructureRules()
    {
        byte[] lf = Encoding.UTF8.GetBytes("[General]\nConfigVersion=1.3\nJvmArgs=x");
        Equal(true, lf.AsSpan().SequenceEqual(PrismIniDocument.Parse(lf).Serialize()), "LF file without a final newline round-trips");
        byte[] bom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("[General]\r\nname=Caf\u00e9\r\n")];
        PrismIniDocument withBom = PrismIniDocument.Parse(bom);
        Equal(true, bom.AsSpan().SequenceEqual(withBom.Serialize()), "BOM is kept");
        Equal("Caf\u00e9", withBom.GetGeneralString("name"), "UTF-8 value decodes");
        Throws<PrismIniUnsupportedException>(() => PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\na=1\nb=2\r\n")));
        Throws<PrismIniUnsupportedException>(() => PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\nJvmArgs=1\r\njvmargs=2\r\n")));
        Throws<PrismIniUnsupportedException>(() => PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\nJvmArgs=\"a\r\nb\"\r\n")));
        Throws<PrismIniUnsupportedException>(() => PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\nJvmArgs=a\\\r\nb\r\n")));
        Throws<PrismIniUnsupportedException>(() => PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\nJvmArgs=a ; note\r\n")));
        Throws<PrismIniUnsupportedException>(() => PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\nnot a setting\r\n")));
        Throws<PrismIniUnsupportedException>(() => PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\na=1\r\n[General]\r\nb=2\r\n")));
        Throws<PrismIniUnsupportedException>(() => PrismIniDocument.Parse([0x5B, 0xC3, 0x28]));
        PrismIniDocument list = PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\nJvmArgs=a,b\r\nX=@ByteArray(abc)\r\nY=@@at\r\n"));
        Throws<PrismIniUnsupportedException>(() => list.GetGeneralString("JvmArgs"));
        Throws<PrismIniUnsupportedException>(() => list.GetGeneralString("X"));
        Equal("@at", list.GetGeneralString("Y"), "@@ decodes to a literal @");
    }

    // ---- 3. Escaping table (qsettings.cpp iniEscapedString) ----------------------------------------------------------------

    private static void TestEscapingTable()
    {
        (string Value, string Encoded)[] table =
        [
            ("plain", "plain"),
            ("a;b", "\"a;b\""),
            ("a,b", "\"a,b\""),
            ("a=b", "\"a=b\""),
            (" lead", "\" lead\""),
            ("trail ", "\"trail \""),
            ("back\\slash", "back\\\\slash"),
            ("q\"uote", "q\\\"uote"),
            ("tab\there", "tab\\there"),
            ("line\nbreak", "line\\nbreak"),
            ("caf\u00e9 \u00fc", "caf\u00e9 \u00fc"),
            ("\u0001a", "\\x1\\x61"),
            ("\u0001z", "\\x1z"),
            ("@argfile", "@@argfile"),
            ("", "")
        ];
        foreach ((string value, string encoded) in table)
        {
            Equal(encoded, PrismIni.EncodeString(value), $"escape {Printable(value)}");
            Equal(value, PrismIni.DecodeValue(encoded).Text, $"decode(escape({Printable(value)}))");
        }
        byte[] written = PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\nname=x\r\n"))
            .WithGeneralValues([new("name", PrismIni.EncodeString("caf\u00e9"))], out _).Serialize();
        Equal("[General]\r\nname=caf\u00c3\u00a9\r\n", Encoding.Latin1.GetString(written), "non-ASCII is written as UTF-8 bytes");
        Equal("true", PrismIni.EncodeBool(true), "bool true");
        Equal("false", PrismIni.EncodeBool(false), "bool false");
        Equal("10000", PrismIni.EncodeInt(10000), "int");
        Equal(false, PrismIni.ToBool("false") || PrismIni.ToBool("FALSE") || PrismIni.ToBool("0") || PrismIni.ToBool(""), "QVariant false strings");
        Equal(true, PrismIni.ToBool("true") && PrismIni.ToBool("yes"), "QVariant true strings");
    }

    private static string Printable(string value) => string.Concat(value.Select(ch => ch < 0x20 ? $"\\x{(int)ch:x}" : ch.ToString()));

    // ---- 4. Gate logic -------------------------------------------------------------------------------------------------------

    private static void TestGateLogic()
    {
        PrismIniDocument global = GlobalWith(("MaxMemAlloc", "6144"), ("MinMemAlloc", "1024"), ("JvmArgs", PrismIni.EncodeString("-Dglobal=1")));
        PrismIniDocument bedrock = PrismIniDocument.Parse(Fixture("instance-bedrock.cfg"));
        Equal("4096", bedrock.GetGeneralString("MaxMemAlloc"), "bedrock carries a stale MaxMemAlloc");
        PrismEffectiveSettings effective = PrismEffectiveSettings.Resolve(bedrock, global, 32 * GiB);
        Equal(false, effective.OverrideMemory, "bedrock OverrideMemory=false");
        Equal(6144, effective.MaxMemAllocMiB, "gate off: the global MaxMemAlloc wins over the stale instance key");
        Equal(1024, effective.MinMemAllocMiB, "gate off: global MinMemAlloc");
        Equal("-Dglobal=1", effective.JvmArgs, "OverrideJavaArgs=false: global JvmArgs");

        PrismEffectiveSettings kewz = PrismEffectiveSettings.Resolve(PrismIniDocument.Parse(Fixture("instance-1.21.1.cfg")), global, 32 * GiB);
        Equal(10096, kewz.MaxMemAllocMiB, "gate on: instance MaxMemAlloc");
        Equal(512, kewz.MinMemAllocMiB, "gate on: instance MinMemAlloc");

        PrismIniDocument gateOnNoKey = PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\nConfigVersion=1.3\r\nOverrideMemory=true\r\nOverrideJavaArgs=true\r\n"));
        PrismEffectiveSettings fallback = PrismEffectiveSettings.Resolve(gateOnNoKey, global, 32 * GiB);
        Equal(6144, fallback.MaxMemAllocMiB, "gate on, key absent: falls back to the global value");
        Equal("-Dglobal=1", fallback.JvmArgs, "gate on, JvmArgs absent: global value");

        PrismIniDocument globalNoMemory = PrismIniDocument.Parse(Encoding.UTF8.GetBytes("[General]\r\nConfigVersion=1.3\r\n"));
        Equal(4096, PrismEffectiveSettings.Resolve(bedrock, globalNoMemory, 32 * GiB).MaxMemAllocMiB, "no global key: suitableMaxMem = 4096");
        Equal(512, PrismEffectiveSettings.Resolve(bedrock, globalNoMemory, 32 * GiB).MinMemAllocMiB, "no global key: MinMemAlloc 512");
        Equal(2730, PrismEffectiveSettings.SuitableMaxMem(4096 * MiB), "suitableMaxMem below 6 GiB is RAM/1.5");
        Throws<PrismIniUnsupportedException>(() => PrismEffectiveSettings.Resolve(bedrock, GlobalWith(("MaxMemoryAlloc", "8192")), 32 * GiB));
        Throws<PrismIniUnsupportedException>(() => PrismEffectiveSettings.Resolve(bedrock, GlobalWith(("ConfigVersion", "1.2")), 32 * GiB));
        Throws<PrismIniUnsupportedException>(() => PrismEffectiveSettings.Resolve(bedrock, GlobalWith(("MaxMemAlloc", "8G")), 32 * GiB));
    }

    // ---- 5. Merge rules ------------------------------------------------------------------------------------------------------

    private static void TestMergeRules()
    {
        JvmDesiredState desired = JvmPolicy.DesiredFor(32 * GiB);

        JvmMergeResult zgc = JvmMerge.Compute(Effective(10000, "-XX:+UseZGC"), desired, 32 * GiB);
        Equal(JvmPolicy.GcLogToken, string.Join(' ', zgc.AddedTokens), "user ZGC: nothing from the GC group, only the gc log");
        Equal("-XX:+UseZGC " + JvmPolicy.GcLogToken, zgc.NewJvmArgs, "user text kept verbatim, ours appended");

        JvmMergeResult dedupOff = JvmMerge.Compute(Effective(10000, "-XX:-UseStringDeduplication"), desired, 32 * GiB);
        Equal(false, dedupOff.AddedTokens.Contains("-XX:+UseStringDeduplication"), "user -XX:-UseStringDeduplication: ours not added");
        Equal(true, dedupOff.NewJvmArgs.StartsWith("-XX:-UseStringDeduplication ", StringComparison.Ordinal), "user dedup flag kept");
        Equal(4, dedupOff.AddedTokens.Count, "the other four flags are still added");

        PrismIniDocument validationCfg = PrismIniDocument.Parse(Fixture("instance-validation.cfg"));
        PrismEffectiveSettings validation = PrismEffectiveSettings.Resolve(validationCfg, PrismIniDocument.Parse(Fixture("prismlauncher.cfg")), 32 * GiB);
        JvmMergeResult validationMerge = JvmMerge.Compute(validation, desired, 32 * GiB);
        Equal(false, validationMerge.AddedTokens.Contains(JvmPolicy.GcLogToken), "Validation -Xlog:gc:file= means no second gc log");
        Equal("-XX:+UseG1GC -XX:MaxGCPauseMillis=50 -XX:G1ReservePercent=15 -XX:+UseStringDeduplication",
            string.Join(' ', validationMerge.AddedTokens), "Validation gets the GC group");
        Equal(false, validationMerge.MemoryMissing, "Validation heap 10000 meets the 32 GB floor");
        Equal(false, validationMerge.WriteMemory, "memory keys untouched when not deficient");

        JvmMergeResult xmx = JvmMerge.Compute(Effective(4096, "-Xmx16G"), desired, 32 * GiB);
        Equal(true, xmx.MemoryMissing, "real heap 4096 is below the floor although JvmArgs says -Xmx16G");
        Equal(true, xmx.WriteMemory, "memory is written");
        Equal(16384, xmx.TargetMaxMiB, "JvmArgs -Xmx16G with MaxMemAlloc 4096 sets MaxMemAlloc 16384");
        Equal(4096, xmx.TargetMinMiB, "Xms 4096");

        JvmMergeResult higher = JvmMerge.Compute(Effective(16384, ""), desired, 32 * GiB);
        Equal(false, higher.MemoryMissing, "a higher player heap is never lowered");
        Equal(true, higher.IsMissing, "flags are still missing");
        Equal(false, higher.WriteMemory, "memory keys untouched");

        PrismEffectiveSettings seeded = PrismEffectiveSettings.Resolve(
            PrismIniDocument.Parse(Fixture("instance-bedrock.cfg")),
            GlobalWith(("MaxMemAlloc", "4096"), ("JvmArgs", PrismIni.EncodeString("-Dglobal=\"a b\" -XX:+AlwaysPreTouch"))), 32 * GiB);
        JvmMergeResult seed = JvmMerge.Compute(seeded, desired, 32 * GiB);
        Equal("-Dglobal=\"a b\" -XX:+AlwaysPreTouch " + AllFlags, seed.NewJvmArgs, "seeded from the global JvmArgs, verbatim");
        Equal(true, seed.EncodedEdits().Any(edit => edit.Key == "OverrideJavaArgs" && edit.Value == "true"), "OverrideJavaArgs=true written");
        Equal(true, seed.Warnings.Any(warning => warning.Contains("AlwaysPreTouch", StringComparison.Ordinal)), "AlwaysPreTouch warning");
        Equal(10000, seed.TargetMaxMiB, "bedrock memory raised to the floor");
        Equal("OverrideMemory,MinMemAlloc,MaxMemAlloc,OverrideJavaArgs,JvmArgs",
            string.Join(',', seed.EncodedEdits().Select(edit => edit.Key)), "exactly the five keys, memory first");

        JvmMergeResult tabbed = JvmMerge.Compute(Effective(10000, "-Da=b\t "), desired, 32 * GiB);
        Equal("-Da=b\t " + AllFlags, tabbed.NewJvmArgs, "only trailing spaces are trimmed (a tab belongs to the token)");
        Throws<PrismIniUnsupportedException>(() => JvmMerge.Compute(Effective(10000, "-Dx=\"open"), desired, 32 * GiB));

        JvmMergeResult complete = JvmMerge.Compute(Effective(10000, AllFlags), desired, 32 * GiB);
        Equal(false, complete.IsMissing, "nothing missing once every key is present");
        Equal(0, complete.EncodedEdits().Count, "no edits when nothing is missing");

        JvmMergeResult small = JvmMerge.Compute(Effective(4096, ""), JvmPolicy.DesiredFor(8 * GiB), 8 * GiB);
        Equal(false, small.MemoryMissing, "below the pack minimum memory is never missing");
        Equal(false, small.WriteMemory, "below the pack minimum memory is never written");

        Equal(true, JvmPolicy.IsGcFileLog("-Xlog:gc*:file=gc.txt"), "gc* to file");
        Equal(true, JvmPolicy.IsGcFileLog("-Xlog:disable"), "-Xlog:disable");
        Equal(true, JvmPolicy.IsGcFileLog("-Xloggc:gc.txt"), "legacy -Xloggc");
        Equal(true, JvmPolicy.IsGcFileLog("-Xlog:gc+heap=debug:gc.txt"), "gc+heap to a bare file name");
        Equal(false, JvmPolicy.IsGcFileLog("-Xlog:gc"), "gc to stdout");
        Equal(false, JvmPolicy.IsGcFileLog("-Xlog:gc:stderr"), "gc to stderr");
        Equal(false, JvmPolicy.IsGcFileLog("-Xlog:safepoint:file=s.txt"), "non-gc tag to file");
        Equal("MaxGCPauseMillis", JvmPolicy.XxKey("-XX:MaxGCPauseMillis=200"), "key of -XX:Name=value");
        Equal("UseG1GC", JvmPolicy.XxKey("-XX:-UseG1GC"), "key of -XX:-Name");
        Equal<long?>(16384, JvmPolicy.XmxMiB("-Xmx16g"), "-Xmx16g");
        Equal<long?>(8192, JvmPolicy.XmxMiB("-Xmx8192M"), "-Xmx8192M");
        Equal<long?>(null, JvmPolicy.XmxMiB("-Xmxfoo"), "bad -Xmx");
    }

    // ---- 6. Tier boundaries --------------------------------------------------------------------------------------------------

    private static void TestTierBoundaries()
    {
        (ulong MiBs, int Max, int Min)[] table =
        [
            (8192, 0, 0), (11263, 0, 0), (11264, 7168, 3584), (16384, 7168, 3584), (19455, 7168, 3584), (19456, 8192, 4096),
            (27647, 8192, 4096), (27648, 10000, 4096), (32768, 10000, 4096), (44031, 10000, 4096), (44032, 12288, 4096), (131072, 12288, 4096)
        ];
        foreach ((ulong mibs, int max, int min) in table)
        {
            JvmHeapTier tier = JvmPolicy.TierFor(mibs * MiB);
            Equal(max, tier.MaxMemAllocMiB, $"tier Max at {mibs} MiB");
            Equal(min, tier.MinMemAllocMiB, $"tier Min at {mibs} MiB");
        }
        Equal(true, JvmPolicy.TierFor(11 * GiB - 1).IsBelowMinimum, "one byte under 11 GiB is below minimum");
        Equal(7168, JvmPolicy.TierFor(11 * GiB).MaxMemAllocMiB, "exactly 11 GiB is the 16 GB class");
        Equal(1, JvmPolicy.PolicyVersion, "policyVersion 1");
    }

    // ---- 7. INST_JAVA_ARGS cross-check ---------------------------------------------------------------------------------------

    private static void TestLaunchArgumentsCrossCheck()
    {
        PrismEffectiveSettings validation = PrismEffectiveSettings.Resolve(
            PrismIniDocument.Parse(Fixture("instance-validation.cfg")), PrismIniDocument.Parse(Fixture("prismlauncher.cfg")), 32 * GiB);
        string real = InstJavaArgs(validation);
        Equal(true, validation.MatchesLaunchArguments(real, out _), "Validation INST_JAVA_ARGS matches");
        Equal(true, validation.MatchesLaunchArguments(InstJavaArgs(validation, "-javaagent:C:/x y/agent.jar"), out _),
            "arguments Prism adds between JvmArgs and HeapDumpPath are allowed");
        Equal(false, validation.MatchesLaunchArguments(real.Replace("-Xmx10000m", "-Xmx4096m", StringComparison.Ordinal), out _),
            "a different -Xmx is a mismatch");
        Equal(false, validation.MatchesLaunchArguments(real.Replace("filecount=3", "filecount=4", StringComparison.Ordinal), out _),
            "different JvmArgs are a mismatch");
        Equal(false, validation.MatchesLaunchArguments("-Xms512m -Xmx10000m", out _), "no HeapDumpPath is a mismatch");
        PrismEffectiveSettings swapped = Effective(4096, "", min: 8192);
        Equal(true, swapped.MatchesLaunchArguments(InstJavaArgs(swapped), out _), "Prism's Min/Max swap is modelled");
        Equal(true, InstJavaArgs(swapped).Contains("-Xms4096m -Xmx8192m", StringComparison.Ordinal), "swap puts the larger value in -Xmx");
    }

    // ---- 8. Marker state machine ---------------------------------------------------------------------------------------------

    private static void TestMarkerStateMachine(string root)
    {
        Directory.CreateDirectory(root);
        DateTime now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        JvmSettingsMarker fresh = JvmSettingsMarkerStore.Load(root, 1);
        Equal(JvmMarkerState.None, fresh.State, "no marker: fresh state");
        Equal(JvmLoopGuardVerdict.MayRestart, JvmSettingsMarkerStore.Evaluate(fresh, now), "fresh marker may restart");

        var restarting = new JvmSettingsMarker { PolicyVersion = 1, State = JvmMarkerState.Restarting, Attempts = 1, LastAttemptUtc = now.AddMinutes(-9) };
        Equal(JvmLoopGuardVerdict.RecentAttempt, JvmSettingsMarkerStore.Evaluate(restarting, now), "within 10 minutes");
        restarting.LastAttemptUtc = now.AddMinutes(-10);
        Equal(JvmLoopGuardVerdict.MayRestart, JvmSettingsMarkerStore.Evaluate(restarting, now), "10 minutes later");
        restarting.LastAttemptUtc = now.AddMinutes(5);
        Equal(JvmLoopGuardVerdict.RecentAttempt, JvmSettingsMarkerStore.Evaluate(restarting, now), "a clock moved back is still recent");
        var twice = new JvmSettingsMarker { PolicyVersion = 1, State = JvmMarkerState.Failed, Attempts = 2, LastAttemptUtc = now.AddDays(-1) };
        Equal(JvmLoopGuardVerdict.TooManyAttempts, JvmSettingsMarkerStore.Evaluate(twice, now), "at most 2 attempts");
        foreach (string edited in new[] { JvmMarkerState.Relaunched, JvmMarkerState.Applied })
        {
            var marker = new JvmSettingsMarker { PolicyVersion = 1, State = edited, Attempts = 1, LastAttemptUtc = now.AddDays(-1) };
            Equal(JvmLoopGuardVerdict.AlreadyRelaunchedStillMissing, JvmSettingsMarkerStore.Evaluate(marker, now), $"{edited} + still missing");
        }
        var waiting = new JvmSettingsMarker { PolicyVersion = 1, State = JvmMarkerState.WaitingForPrismExit, Attempts = 1, LastAttemptUtc = now.AddHours(-11) };
        Equal(JvmLoopGuardVerdict.HelperStillWaiting, JvmSettingsMarkerStore.Evaluate(waiting, now), "fallback F helper still waiting");
        waiting.LastAttemptUtc = now.AddHours(-13);
        Equal(JvmLoopGuardVerdict.MayRestart, JvmSettingsMarkerStore.Evaluate(waiting, now), "fallback F wait over");

        JvmSettingsMarkerStore.Save(root, twice, now);
        JvmSettingsMarker reloaded = JvmSettingsMarkerStore.Load(root, 1);
        Equal(2, reloaded.Attempts, "marker round-trips");
        Equal(now.AddDays(-1), reloaded.LastAttemptUtc!.Value.ToUniversalTime(), "attempt time round-trips");
        JvmSettingsMarker bumped = JvmSettingsMarkerStore.Load(root, 2);
        Equal(0, bumped.Attempts, "a policyVersion bump resets the attempts");
        Equal(JvmLoopGuardVerdict.MayRestart, JvmSettingsMarkerStore.Evaluate(bumped, now), "a policyVersion bump may restart");
        File.WriteAllText(JvmSettingsMarkerStore.PathFor(root), "{ not json");
        Equal(0, JvmSettingsMarkerStore.Load(root, 1).Attempts, "an unreadable marker is a fresh one");
        Equal(false, Directory.EnumerateFiles(root, "*.new-*").Any(), "no temp files left behind");
    }

    // ---- 9. Relaunch plan builder --------------------------------------------------------------------------------------------

    private static void TestRelaunchPlanBuilder()
    {
        const string exe = @"C:\Prism\prismlauncher.exe";
        Equal("--launch|kewz", Relaunch([exe], "kewz"), "plain start");
        Equal("--launch|kewz", Relaunch([exe, "-l", "other"], "kewz"), "-l x dropped");
        Equal("--launch|kewz", Relaunch([exe, "--launch=other"], "kewz"), "--launch=x dropped");
        Equal("--launch|kewz", Relaunch([exe, "--launch", "other", "-s", "mc.example:25565", "-w", "World", "-a", "Steve", "-o", "Alex"], "kewz"),
            "-s/-w/-a/-o and values dropped");
        Equal("--launch|kewz", Relaunch([exe, "--server=a", "--world=b", "--profile=c", "--offline=d"], "kewz"), "--long=value forms dropped");
        Equal(@"--dir|D:\Games\PrismMC|--alive|--launch|kewz", Relaunch([exe, "--dir", @"D:\Games\PrismMC", "--alive"], "kewz"), "--dir and --alive kept");
        Equal(@"-d|D:\Data|--launch|1.21.1(1)", Relaunch([exe, "-d", @"D:\Data"], "1.21.1(1)"), "-d kept verbatim");
        Equal(@"--dir=D:\Data|--launch|kewz", Relaunch([exe, @"--dir=D:\Data"], "kewz"), "--dir=path kept");
        Equal("(no plan)", Relaunch([exe, "--dir", "relative"], "kewz"), "a relative --dir gives no plan");
        Equal("(no plan)", Relaunch([exe, "-d", @".\data"], "kewz"), "a relative -d gives no plan");
        Equal("(no plan)", Relaunch([exe, @"-dD:\Data"], "kewz"), "compact short option gives no plan");
        Equal("(no plan)", Relaunch([exe, "--show", "kewz"], "kewz"), "--show gives no plan");
        Equal("(no plan)", Relaunch([exe, "https://example.invalid/pack.zip"], "kewz"), "an import URL gives no plan");
        Equal("(no plan)", Relaunch([exe, "-l"], "kewz"), "a missing value gives no plan");
        Equal("(no plan)", Relaunch([exe, "-d", @"C:\a", "--dir", @"C:\b"], "kewz"), "a repeated --dir gives no plan");
        PrismCommandLine plain = PrismRelaunch.Parse([exe])!;
        Equal(@"--dir|E:\PrismData|--launch|kewz", string.Join('|', PrismRelaunch.BuildArguments(plain, "kewz", @"E:\PrismData")),
            "a data folder from PRISMLAUNCHER_DATA_DIR becomes --dir for the clean-environment relaunch");
    }

    private static string Relaunch(string[] argv, string id)
    {
        PrismCommandLine? parsed = PrismRelaunch.Parse(argv);
        return parsed is null ? "(no plan)" : string.Join('|', PrismRelaunch.BuildArguments(parsed, id, null));
    }

    // ---- Section 9: atomic edit on the real files -----------------------------------------------------------------------------

    private static void TestCfgEditsOnRealFiles(string root)
    {
        Directory.CreateDirectory(root);
        JvmDesiredState desired = JvmPolicy.DesiredFor(32 * GiB);
        PrismIniDocument global = PrismIniDocument.Parse(Fixture("prismlauncher.cfg"));
        string encodedFlags = "\"" + AllFlags + "\"";

        // 1.21.1: both keys exist; exactly two lines change.
        byte[] kewzBytes = Fixture("instance-1.21.1.cfg");
        byte[] kewzEdited = Edit(kewzBytes, global, desired);
        string expectedKewz = Encoding.UTF8.GetString(kewzBytes)
            .Replace("\r\nOverrideJavaArgs=false\r\n", "\r\nOverrideJavaArgs=true\r\n", StringComparison.Ordinal)
            .Replace("\r\nJvmArgs=\r\n", "\r\nJvmArgs=" + encodedFlags + "\r\n", StringComparison.Ordinal);
        Equal(expectedKewz, Encoding.UTF8.GetString(kewzEdited), "1.21.1: only OverrideJavaArgs and JvmArgs lines change");

        // 1.21.1(1): no JvmArgs key; it is inserted before the blank line that ends [General].
        byte[] otherBytes = Fixture("instance-1.21.1(1).cfg");
        string expectedOther = Encoding.UTF8.GetString(otherBytes)
            .Replace("\r\nOverrideJavaArgs=false\r\n", "\r\nOverrideJavaArgs=true\r\n", StringComparison.Ordinal)
            .Replace("\r\ntotalTimePlayed=16\r\n\r\n[UI]", "\r\ntotalTimePlayed=16\r\nJvmArgs=" + encodedFlags + "\r\n\r\n[UI]", StringComparison.Ordinal);
        Equal(expectedOther, Encoding.UTF8.GetString(Edit(otherBytes, global, desired)), "1.21.1(1): JvmArgs inserted at the end of [General]");

        // bedrock: memory is deficient (global 4096); all five keys are replaced in place.
        byte[] bedrockBytes = Fixture("instance-bedrock.cfg");
        string expectedBedrock = Encoding.UTF8.GetString(bedrockBytes)
            .Replace("\r\nOverrideMemory=false\r\n", "\r\nOverrideMemory=true\r\n", StringComparison.Ordinal)
            .Replace("\r\nMinMemAlloc=512\r\n", "\r\nMinMemAlloc=4096\r\n", StringComparison.Ordinal)
            .Replace("\r\nMaxMemAlloc=4096\r\n", "\r\nMaxMemAlloc=10000\r\n", StringComparison.Ordinal)
            .Replace("\r\nOverrideJavaArgs=false\r\n", "\r\nOverrideJavaArgs=true\r\n", StringComparison.Ordinal)
            .Replace("\r\nJvmArgs=\r\n", "\r\nJvmArgs=" + encodedFlags + "\r\n", StringComparison.Ordinal);
        Equal(expectedBedrock, Encoding.UTF8.GetString(Edit(bedrockBytes, global, desired)), "bedrock: the five keys, nothing else");

        // Validation: the player's quoted JvmArgs stay verbatim; the GC group is appended, no second gc log.
        byte[] validationBytes = Fixture("instance-validation.cfg");
        string expectedValidation = Encoding.UTF8.GetString(validationBytes).Replace(
            "filecount=3,filesize=10M\"\r\n",
            "filecount=3,filesize=10M -XX:+UseG1GC -XX:MaxGCPauseMillis=50 -XX:G1ReservePercent=15 -XX:+UseStringDeduplication\"\r\n",
            StringComparison.Ordinal);
        Equal(expectedValidation, Encoding.UTF8.GetString(Edit(validationBytes, global, desired)), "Validation: GC group appended to the quoted value");

        // Atomic apply with backups, last three kept, and the stale-file guard.
        string config = Path.Combine(root, "instance.cfg");
        File.WriteAllBytes(config, kewzBytes);
        DateTime stamp = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Local);
        for (int index = 0; index < 4; index++)
        {
            File.WriteAllBytes(config, kewzBytes);
            InstanceCfgEditResult result = InstanceCfgEditor.Apply(config, kewzBytes, kewzEdited, () => false, stamp.AddSeconds(index));
            Equal(true, result.Written, $"apply {index} written");
            Equal(true, File.ReadAllBytes(result.BackupPath!).AsSpan().SequenceEqual(kewzBytes), $"apply {index} backup holds the original");
        }
        Equal(true, File.ReadAllBytes(config).AsSpan().SequenceEqual(kewzEdited), "instance.cfg holds the edited bytes");
        string[] backups = Directory.GetFiles(root, "instance.cfg.cobble-music-jvm-*.bak").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
        Equal("instance.cfg.cobble-music-jvm-20260923-100001.bak|instance.cfg.cobble-music-jvm-20260923-100002.bak|instance.cfg.cobble-music-jvm-20260923-100003.bak",
            string.Join('|', backups), "the last three backups are kept");
        Equal(false, Directory.EnumerateFiles(root, "*.tmp").Any(), "no temp file left");

        File.WriteAllBytes(config, kewzBytes);
        Equal(false, InstanceCfgEditor.Apply(config, kewzBytes, kewzEdited, () => true, stamp.AddMinutes(1)).Written, "Prism running: not written");
        Equal(true, File.ReadAllBytes(config).AsSpan().SequenceEqual(kewzBytes), "Prism running: file untouched");
        byte[] changed = [.. kewzBytes, (byte)' '];
        File.WriteAllBytes(config, changed);
        Equal(false, InstanceCfgEditor.Apply(config, kewzBytes, kewzEdited, () => false, stamp.AddMinutes(2)).Written, "file changed since read: not written");
        Equal(true, File.ReadAllBytes(config).AsSpan().SequenceEqual(changed), "file changed since read: untouched");
    }

    private static byte[] Edit(byte[] instanceBytes, PrismIniDocument global, JvmDesiredState desired)
    {
        PrismIniDocument instance = PrismIniDocument.Parse(instanceBytes);
        PrismEffectiveSettings effective = PrismEffectiveSettings.Resolve(instance, global, 32 * GiB);
        JvmMergeResult merge = JvmMerge.Compute(effective, desired, 32 * GiB);
        return InstanceCfgEditor.BuildEditedBytes(instance, global, merge, desired, 32 * GiB);
    }

    // ---- Coordinator (section 6 A) ---------------------------------------------------------------------------------------------

    private static void TestCoordinatorRestartAndVerify(string root)
    {
        Rig rig = Rig.Create(root, "instance-1.21.1(1).cfg", "1.21.1(1)");
        var progress = new RecordingProgress();
        JvmPreLaunchOutcome outcome = new JvmSettingsCoordinator(rig.System, rig.Log.Add, progress).Evaluate(rig.Paths);
        Equal(JvmPreLaunchOutcome.RestartStarted, outcome, "missing flags, all guards pass: restart");
        Equal(1, rig.System.FailPreLaunchCalls, "the launch was stopped once");
        Equal(JvmSettingsText.Restarting, progress.Messages.LastOrDefault(), "card: restarting text");
        JvmSettingsMarker marker = JvmSettingsMarkerStore.Load(rig.LocalData, 1);
        Equal(JvmMarkerState.Restarting, marker.State, "marker restarting");
        Equal(1, marker.Attempts, "attempt counted");
        JvmSettingsPlan plan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(rig.LocalData)));
        Equal("--launch|1.21.1(1)", string.Join('|', plan.RelaunchArguments), "relaunch arguments");
        Equal(rig.PrismExe, plan.PrismExePath, "plan exe is the verified Prism image");
        Equal(rig.Prism.StartUtc, plan.Prism.ToIdentity().StartUtc, "plan keeps Prism's creation time");
        Equal(200, plan.PowerShell!.Pid, "plan names the pre-launch PowerShell");
        Equal(10000, plan.XmxFloorMiB, "plan carries D");

        // The helper takes over (same fake world), edits and relaunches.
        rig.EndPreLaunch();
        JvmHelperOutcome helper = new JvmSettingsHelper(rig.System, rig.Log.Add, null).RunActivePhase(plan);
        Equal(JvmHelperOutcome.Relaunched, helper, "helper relaunched Prism");
        Equal(JvmMarkerState.Relaunched, JvmSettingsMarkerStore.Load(rig.LocalData, 1).State, "marker relaunched");

        // The relaunched Prism runs the pre-launch again: nothing missing, marker verified, no second restart.
        rig.StartNewPrism();
        JvmPreLaunchOutcome second = new JvmSettingsCoordinator(rig.System, rig.Log.Add, null).Evaluate(rig.Paths);
        Equal(JvmPreLaunchOutcome.NothingMissing, second, "after the restart nothing is missing");
        Equal(JvmMarkerState.Verified, JvmSettingsMarkerStore.Load(rig.LocalData, 1).State, "marker verified");
        Equal(1, rig.System.FailPreLaunchCalls, "no second abort");

        // If something reverted the settings after a relaunch, the marker fails and no further restart happens.
        JvmSettingsMarker relaunched = JvmSettingsMarkerStore.Load(rig.LocalData, 1);
        relaunched.State = JvmMarkerState.Relaunched;
        JvmSettingsMarkerStore.Save(rig.LocalData, relaunched, rig.System.UtcNow);
        File.WriteAllBytes(rig.InstanceCfg, Fixture("instance-1.21.1(1).cfg"));
        rig.RefreshJavaArgs();
        var failedProgress = new RecordingProgress();
        Equal(JvmPreLaunchOutcome.LoopGuardStopped, new JvmSettingsCoordinator(rig.System, rig.Log.Add, failedProgress).Evaluate(rig.Paths),
            "relaunched + still missing: stopped");
        Equal(JvmMarkerState.Failed, JvmSettingsMarkerStore.Load(rig.LocalData, 1).State, "marker failed");
        Equal(JvmSettingsText.CouldNotApply, failedProgress.Messages.LastOrDefault(), "card: could-not-apply text");
        Equal(1, rig.System.FailPreLaunchCalls, "still no second abort");
    }

    private static void TestCoordinatorGuards(string root)
    {
        // Another child of Prism (a game from another instance): deferred, no attempt counted.
        Rig game = Rig.Create(Path.Combine(root, "game"), "instance-1.21.1.cfg", "kewz");
        game.System.Children[game.Prism.Pid].Add(new ProcessIdentity(400, game.Prism.StartUtc.AddMinutes(1), @"C:\Java\bin\javaw.exe"));
        var progress = new RecordingProgress();
        Equal(JvmPreLaunchOutcome.Deferred, new JvmSettingsCoordinator(game.System, game.Log.Add, progress).Evaluate(game.Paths), "game running: deferred");
        Equal(JvmSettingsText.Deferred, progress.Messages.LastOrDefault(), "card: deferred text");
        Equal(0, JvmSettingsMarkerStore.Load(game.LocalData, 1).Attempts, "deferral counts no attempt");
        Equal(0, game.System.FailPreLaunchCalls, "deferral never stops the launch");

        Rig small = Rig.Create(Path.Combine(root, "small"), "instance-1.21.1.cfg", "kewz");
        small.System.TotalPhys = 8 * GiB;
        Equal(JvmPreLaunchOutcome.Deferred, new JvmSettingsCoordinator(small.System, small.Log.Add, null).Evaluate(small.Paths), "below pack minimum: deferred");

        Rig reopened = Rig.Create(Path.Combine(root, "second-prism"), "instance-1.21.1.cfg", "kewz");
        reopened.System.ExtraImageProcesses.Add(new ProcessIdentity(150, reopened.Prism.StartUtc, reopened.PrismExe));
        Equal(JvmPreLaunchOutcome.Deferred, new JvmSettingsCoordinator(reopened.System, reopened.Log.Add, null).Evaluate(reopened.Paths), "a second Prism from the same exe: deferred");

        Rig foreignDir = Rig.Create(Path.Combine(root, "instance-dir"), "instance-1.21.1.cfg", "kewz");
        File.WriteAllBytes(foreignDir.GlobalCfg, GlobalWith(("InstanceDir", "elsewhere")).Serialize());
        foreignDir.RefreshJavaArgs();
        Equal(JvmPreLaunchOutcome.Deferred, new JvmSettingsCoordinator(foreignDir.System, foreignDir.Log.Add, null).Evaluate(foreignDir.Paths), "InstanceDir does not hold INST_DIR: deferred");

        // Preconditions: no action at all.
        Rig optOut = Rig.Create(Path.Combine(root, "opt-out"), "instance-1.21.1.cfg", "kewz");
        File.WriteAllText(Path.Combine(optOut.Paths.InstallationDirectory, JvmSettingsCoordinator.OptOutFileName), string.Empty);
        Equal(JvmPreLaunchOutcome.NoAction, new JvmSettingsCoordinator(optOut.System, optOut.Log.Add, null).Evaluate(optOut.Paths), "opt-out file: no action");

        Rig mismatch = Rig.Create(Path.Combine(root, "cross-check"), "instance-1.21.1.cfg", "kewz");
        mismatch.System.Env["INST_JAVA_ARGS"] = mismatch.System.Env["INST_JAVA_ARGS"]!.Replace("-Xmx10096m", "-Xmx4096m", StringComparison.Ordinal);
        Equal(JvmPreLaunchOutcome.NoAction, new JvmSettingsCoordinator(mismatch.System, mismatch.Log.Add, null).Evaluate(mismatch.Paths), "INST_JAVA_ARGS mismatch: no action");
        Equal(false, File.Exists(JvmSettingsMarkerStore.PathFor(mismatch.LocalData)), "no action writes no marker");

        Rig noChain = Rig.Create(Path.Combine(root, "no-chain"), "instance-1.21.1.cfg", "kewz");
        noChain.System.Chain = null;
        Equal(JvmPreLaunchOutcome.NoAction, new JvmSettingsCoordinator(noChain.System, noChain.Log.Add, null).Evaluate(noChain.Paths), "no verified Prism chain: no action");

        Rig oldPrism = Rig.Create(Path.Combine(root, "prism-9"), "instance-1.21.1.cfg", "kewz");
        oldPrism.System.MajorVersion = 9;
        Equal(JvmPreLaunchOutcome.NoAction, new JvmSettingsCoordinator(oldPrism.System, oldPrism.Log.Add, null).Evaluate(oldPrism.Paths), "Prism 9: no action");

        Rig wrongId = Rig.Create(Path.Combine(root, "wrong-id"), "instance-1.21.1.cfg", "kewz");
        wrongId.System.Env["INST_ID"] = "other";
        Equal(JvmPreLaunchOutcome.NoAction, new JvmSettingsCoordinator(wrongId.System, wrongId.Log.Add, null).Evaluate(wrongId.Paths), "INST_ID != folder name: no action");

        Rig relativeDir = Rig.Create(Path.Combine(root, "relative-dir"), "instance-1.21.1.cfg", "kewz");
        relativeDir.System.CommandLines[relativeDir.Prism.Pid] = $"\"{relativeDir.PrismExe}\" --dir data";
        Equal(JvmPreLaunchOutcome.NoAction, new JvmSettingsCoordinator(relativeDir.System, relativeDir.Log.Add, null).Evaluate(relativeDir.Paths), "relative --dir: no action");

        // Loop guard: a recent attempt stops the restart and shows the could-not-apply text.
        Rig recent = Rig.Create(Path.Combine(root, "recent"), "instance-1.21.1.cfg", "kewz");
        JvmSettingsMarkerStore.Save(recent.LocalData, new JvmSettingsMarker
        {
            PolicyVersion = 1, State = JvmMarkerState.Aborted, Attempts = 1, LastAttemptUtc = recent.System.UtcNow.AddMinutes(-3)
        }, recent.System.UtcNow);
        var recentProgress = new RecordingProgress();
        Equal(JvmPreLaunchOutcome.LoopGuardStopped, new JvmSettingsCoordinator(recent.System, recent.Log.Add, recentProgress).Evaluate(recent.Paths), "recent attempt: stopped");
        Equal(JvmSettingsText.CouldNotApply, recentProgress.Messages.LastOrDefault(), "loop guard card text");

        // The helper never confirms: aborted-helper, the launch continues.
        Rig silent = Rig.Create(Path.Combine(root, "silent-helper"), "instance-1.21.1.cfg", "kewz");
        silent.System.HelperWritesReady = false;
        DateTime before = silent.System.UtcNow;
        Equal(JvmPreLaunchOutcome.HelperDidNotStart, new JvmSettingsCoordinator(silent.System, silent.Log.Add, null).Evaluate(silent.Paths), "helper silent: continue");
        Equal(true, silent.System.UtcNow - before >= TimeSpan.FromSeconds(10), "waited the full 10 s");
        Equal(JvmMarkerState.AbortedHelper, JvmSettingsMarkerStore.Load(silent.LocalData, 1).State, "marker aborted-helper");
        Equal(0, silent.System.FailPreLaunchCalls, "no abort without a confirmed helper");

        // The launch cannot be stopped: the marker cancels the helper.
        Rig stuck = Rig.Create(Path.Combine(root, "abort-failed"), "instance-1.21.1.cfg", "kewz");
        stuck.System.FailPreLaunchResult = false;
        Equal(JvmPreLaunchOutcome.AbortFailed, new JvmSettingsCoordinator(stuck.System, stuck.Log.Add, null).Evaluate(stuck.Paths), "abort failed");
        Equal(JvmMarkerState.Aborted, JvmSettingsMarkerStore.Load(stuck.LocalData, 1).State, "marker aborted cancels the helper");

        // Case (b): Prism runs the updater directly; our own process is Prism's only allowed child.
        Rig direct = Rig.Create(Path.Combine(root, "direct"), "instance-1.21.1.cfg", "kewz", withPowerShell: false);
        Equal(JvmPreLaunchOutcome.RestartStarted, new JvmSettingsCoordinator(direct.System, direct.Log.Add, null).Evaluate(direct.Paths), "direct child: restart");
        Equal(true, JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(direct.LocalData))).PowerShell is null, "plan has no PowerShell");

        // PRISMLAUNCHER_DATA_DIR decides the data folder: the relaunch carries it as --dir.
        Rig envDir = Rig.Create(Path.Combine(root, "env-dir"), "instance-1.21.1.cfg", "kewz", portable: false);
        envDir.System.Env[PrismDataDir.DataDirEnvironmentVariable] = envDir.PrismDir;
        Equal(JvmPreLaunchOutcome.RestartStarted, new JvmSettingsCoordinator(envDir.System, envDir.Log.Add, null).Evaluate(envDir.Paths), "env data dir: restart");
        Equal($"--dir|{envDir.PrismDir}|--launch|kewz",
            string.Join('|', JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(envDir.LocalData))).RelaunchArguments), "env data dir becomes --dir");
    }

    // ---- Helper (section 6 B) -----------------------------------------------------------------------------------------------

    private static void TestHelperValidationAndCancel(string root)
    {
        Rig rig = Rig.Create(root, "instance-1.21.1.cfg", "kewz");
        Equal(JvmPreLaunchOutcome.RestartStarted, new JvmSettingsCoordinator(rig.System, rig.Log.Add, null).Evaluate(rig.Paths), "setup restart");
        string planPath = JvmSettingsPlan.PathFor(rig.LocalData);
        JvmSettingsPlan plan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(planPath));
        var helper = new JvmSettingsHelper(rig.System, rig.Log.Add, null);
        Equal<string?>(null, helper.Validate(plan, planPath), "the coordinator's plan validates");

        void Rejects(Action<JvmSettingsPlan> tamper, string context)
        {
            JvmSettingsPlan copy = JvmSettingsPlan.Deserialize(plan.Serialize());
            tamper(copy);
            Equal(true, helper.Validate(copy, planPath) is not null, "rejects " + context);
        }
        Rejects(copy => copy.RelaunchArguments = ["--launch", "other"], "different relaunch arguments");
        Rejects(copy => copy.RelaunchArguments = ["--launch", "kewz", "--server", "evil"], "extra relaunch arguments");
        Rejects(copy => copy.PrismExePath = @"C:\Windows\System32\cmd.exe", "an exe other than the verified Prism");
        Rejects(copy => copy.Prism = copy.Prism with { StartUtcTicks = copy.Prism.StartUtcTicks + 1 }, "a reused Prism PID");
        Rejects(copy => copy.XmxFloorMiB = 1, "a desired state other than the compiled policy");
        Rejects(copy => copy.Flags = [.. copy.Flags, "-XX:+AlwaysPreTouch"], "an extra flag");
        Rejects(copy => copy.Updater = copy.Updater with { ImagePath = @"C:\other\CobbleMusicUpdater.exe" }, "an updater that is not this exe");
        Rejects(copy => copy.InstanceId = "other", "an instance id that is not the folder name");
        Rejects(copy => copy.DataDirectory = root, "a different data folder");
        Rejects(copy => copy.PolicyVersion = 2, "another policy version");
        Equal(true, helper.Validate(plan, Path.Combine(root, "elsewhere", JvmSettingsPlan.FileName)) is not null, "rejects a plan outside the local data folder");

        // The updater cancelled (aborted-helper): the helper changes nothing.
        JvmSettingsMarker marker = JvmSettingsMarkerStore.Load(rig.LocalData, 1);
        marker.State = JvmMarkerState.AbortedHelper;
        JvmSettingsMarkerStore.Save(rig.LocalData, marker, rig.System.UtcNow);
        rig.EndPreLaunch();
        byte[] before = File.ReadAllBytes(rig.InstanceCfg);
        Equal(JvmHelperOutcome.Cancelled, helper.RunActivePhase(plan), "cancelled by the marker");
        Equal(0, rig.System.PostCloseCalls, "Prism was not asked to close");
        Equal(true, before.AsSpan().SequenceEqual(File.ReadAllBytes(rig.InstanceCfg)), "instance.cfg untouched");

        // The updater never exits: stop without touching Prism.
        Rig slow = Rig.Create(Path.Combine(root, "slow"), "instance-1.21.1.cfg", "kewz");
        new JvmSettingsCoordinator(slow.System, slow.Log.Add, null).Evaluate(slow.Paths);
        JvmSettingsPlan slowPlan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(slow.LocalData)));
        Equal(JvmHelperOutcome.Aborted, new JvmSettingsHelper(slow.System, slow.Log.Add, null).RunActivePhase(slowPlan), "updater alive after 60 s: abort");
        Equal(0, slow.System.PostCloseCalls, "Prism untouched when the updater is still running");
    }

    private static void TestHelperCloseEditRelaunch(string root)
    {
        Rig rig = Rig.Create(root, "instance-bedrock.cfg", "bedrock");
        new JvmSettingsCoordinator(rig.System, rig.Log.Add, null).Evaluate(rig.Paths);
        JvmSettingsPlan plan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(rig.LocalData)));
        rig.EndPreLaunch();
        rig.System.CloseRoundsUntilExit = 3;
        // Prism rewrites instance.cfg when it closes (InstanceWindow saveAll): the helper must recompute from that file.
        byte[] rewritten = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Fixture("instance-bedrock.cfg"))
            .Replace("totalTimePlayed=2257", "totalTimePlayed=2300", StringComparison.Ordinal));
        rig.System.OnPrismExit = () => File.WriteAllBytes(rig.InstanceCfg, rewritten);
        var progress = new RecordingProgress();
        JvmHelperOutcome outcome = new JvmSettingsHelper(rig.System, rig.Log.Add, progress).RunActivePhase(plan);
        Equal(JvmHelperOutcome.Relaunched, outcome, "closed gracefully, edited, relaunched");
        Equal(3, rig.System.PostCloseCalls, "WM_CLOSE rounds until Prism exited");
        Equal(0, rig.System.Terminated.Count, "no forced termination after a graceful close");
        Equal(1, rig.System.Relaunches.Count, "one relaunch");
        Equal(rig.PrismExe, rig.System.Relaunches[0].Exe, "relaunch uses the verified exe");
        Equal("--launch|bedrock", string.Join('|', rig.System.Relaunches[0].Arguments), "relaunch arguments");
        Equal(rig.PrismDir, rig.System.Relaunches[0].WorkingDirectory, "relaunch working directory is Prism's folder");
        string edited = File.ReadAllText(rig.InstanceCfg);
        Equal(true, edited.Contains("totalTimePlayed=2300", StringComparison.Ordinal), "Prism's own close-time write is kept");
        Equal(true, edited.Contains("\r\nMaxMemAlloc=10000\r\n", StringComparison.Ordinal) && edited.Contains("\r\nOverrideMemory=true\r\n", StringComparison.Ordinal),
            "memory applied");
        Equal(true, edited.Contains("\r\nJvmArgs=\"" + AllFlags + "\"\r\n", StringComparison.Ordinal), "flags applied");
        Equal(1, Directory.GetFiles(rig.InstanceDir, "instance.cfg.cobble-music-jvm-*.bak").Length, "one backup");
        Equal(true, Directory.Exists(Path.Combine(rig.McDir, "logs")), "logs folder created before the relaunch");
        Equal(JvmSettingsText.Reopening, progress.Messages.LastOrDefault(), "helper card: reopening");
        Equal(true, progress.Messages.Contains(JvmSettingsText.ClosingPrism), "helper card: closing");

        // Relaunch failure is reported, the edit stays.
        Rig broken = Rig.Create(Path.Combine(root, "relaunch-failed"), "instance-bedrock.cfg", "bedrock");
        new JvmSettingsCoordinator(broken.System, broken.Log.Add, null).Evaluate(broken.Paths);
        JvmSettingsPlan brokenPlan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(broken.LocalData)));
        broken.EndPreLaunch();
        broken.System.RelaunchResult = PrismRelaunchMethod.Failed;
        var brokenProgress = new RecordingProgress();
        Equal(JvmHelperOutcome.RelaunchFailed, new JvmSettingsHelper(broken.System, broken.Log.Add, brokenProgress).RunActivePhase(brokenPlan), "relaunch failed");
        Equal(JvmSettingsText.ReopenFailed, brokenProgress.Messages.LastOrDefault(), "card: open Prism yourself");
    }

    private static void TestHelperFallbackAndReopen(string root)
    {
        // A game from another instance appears before Prism closes: fallback F, then the edit after Prism exits, no relaunch.
        Rig rig = Rig.Create(root, "instance-1.21.1.cfg", "kewz");
        new JvmSettingsCoordinator(rig.System, rig.Log.Add, null).Evaluate(rig.Paths);
        JvmSettingsPlan plan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(rig.LocalData)));
        rig.EndPreLaunch();
        rig.System.Children[rig.Prism.Pid].Add(new ProcessIdentity(500, rig.Prism.StartUtc.AddMinutes(2), @"C:\Java\bin\javaw.exe"));
        rig.System.CloseRoundsUntilExit = int.MaxValue;
        var progress = new RecordingProgress();
        var helper = new JvmSettingsHelper(rig.System, rig.Log.Add, progress);
        Equal(JvmHelperOutcome.WaitingForPrismExit, helper.RunActivePhase(plan), "a game is running: fallback F");
        Equal(0, rig.System.PostCloseCalls, "Prism is never closed while a game runs");
        Equal(0, rig.System.Terminated.Count, "never forced while a game runs");
        Equal(JvmSettingsText.NextPrismStart, progress.Messages.LastOrDefault(), "card: next time you start Prism");
        Equal(JvmMarkerState.WaitingForPrismExit, JvmSettingsMarkerStore.Load(rig.LocalData, 1).State, "marker waiting");
        byte[] before = File.ReadAllBytes(rig.InstanceCfg);
        rig.System.ExitOnLongWait = true;
        Equal(JvmHelperOutcome.Applied, new JvmSettingsHelper(rig.System, rig.Log.Add, null).FinishAfterPrismExit(plan), "applied after Prism exited");
        Equal(0, rig.System.Relaunches.Count, "fallback F never relaunches");
        Equal(false, before.AsSpan().SequenceEqual(File.ReadAllBytes(rig.InstanceCfg)), "instance.cfg edited");
        Equal(JvmMarkerState.Applied, JvmSettingsMarkerStore.Load(rig.LocalData, 1).State, "marker applied");

        // The player reopens Prism before the edit: nothing is written.
        Rig reopen = Rig.Create(Path.Combine(root, "reopened"), "instance-1.21.1.cfg", "kewz");
        new JvmSettingsCoordinator(reopen.System, reopen.Log.Add, null).Evaluate(reopen.Paths);
        JvmSettingsPlan reopenPlan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(reopen.LocalData)));
        reopen.EndPreLaunch();
        reopen.System.OnPrismExit = () => reopen.System.ExtraImageProcesses.Add(new ProcessIdentity(777, reopen.System.UtcNow, reopen.PrismExe));
        byte[] untouched = File.ReadAllBytes(reopen.InstanceCfg);
        var reopenProgress = new RecordingProgress();
        Equal(JvmHelperOutcome.AbortedPrismReopened, new JvmSettingsHelper(reopen.System, reopen.Log.Add, reopenProgress).RunActivePhase(reopenPlan), "reopened: stop");
        Equal(true, untouched.AsSpan().SequenceEqual(File.ReadAllBytes(reopen.InstanceCfg)), "reopened: instance.cfg untouched");
        Equal(0, reopen.System.Relaunches.Count, "reopened: no relaunch");
        Equal(JvmMarkerState.AbortedPrismReopened, JvmSettingsMarkerStore.Load(reopen.LocalData, 1).State, "marker aborted-prism-reopened");
        Equal(JvmSettingsText.PrismReopened, reopenProgress.Messages.LastOrDefault(), "card: press Play again");
    }

    private static void TestHelperForcedCloseGuards(string root)
    {
        // Windows never close; no children, no lock: TerminateProcess(prism, 1) after 20 rounds.
        Rig rig = Rig.Create(root, "instance-1.21.1.cfg", "kewz");
        new JvmSettingsCoordinator(rig.System, rig.Log.Add, null).Evaluate(rig.Paths);
        JvmSettingsPlan plan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(rig.LocalData)));
        rig.EndPreLaunch();
        rig.System.CloseRoundsUntilExit = int.MaxValue;
        Equal(JvmHelperOutcome.Relaunched, new JvmSettingsHelper(rig.System, rig.Log.Add, null).RunActivePhase(plan), "forced close then relaunch");
        Equal(JvmSettingsHelper.GracefulCloseRounds, rig.System.PostCloseCalls, "20 graceful rounds first");
        Equal("100:1", string.Join(',', rig.System.Terminated.Select(item => $"{item.Pid}:{item.ExitCode}")), "TerminateProcess(prism, 1)");

        // A settings lock file: never forced.
        Rig locked = Rig.Create(Path.Combine(root, "locked"), "instance-1.21.1.cfg", "kewz");
        new JvmSettingsCoordinator(locked.System, locked.Log.Add, null).Evaluate(locked.Paths);
        JvmSettingsPlan lockedPlan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(locked.LocalData)));
        locked.EndPreLaunch();
        locked.System.CloseRoundsUntilExit = int.MaxValue;
        File.WriteAllText(locked.GlobalCfg + ".lock", "1");
        byte[] before = File.ReadAllBytes(locked.InstanceCfg);
        var progress = new RecordingProgress();
        Equal(JvmHelperOutcome.Aborted, new JvmSettingsHelper(locked.System, locked.Log.Add, progress).RunActivePhase(lockedPlan), "lock present: abort");
        Equal(0, locked.System.Terminated.Count, "never terminated while a .cfg.lock exists");
        Equal(true, before.AsSpan().SequenceEqual(File.ReadAllBytes(locked.InstanceCfg)), "instance.cfg untouched");
        Equal(JvmSettingsText.PressPlayAgain, progress.Messages.LastOrDefault(), "card: press Play again");
        Equal(JvmMarkerState.Aborted, JvmSettingsMarkerStore.Load(locked.LocalData, 1).State, "marker aborted");

        // A child appears during the close rounds: fallback F instead of forcing.
        Rig late = Rig.Create(Path.Combine(root, "late-child"), "instance-1.21.1.cfg", "kewz");
        new JvmSettingsCoordinator(late.System, late.Log.Add, null).Evaluate(late.Paths);
        JvmSettingsPlan latePlan = JvmSettingsPlan.Deserialize(File.ReadAllBytes(JvmSettingsPlan.PathFor(late.LocalData)));
        late.EndPreLaunch();
        late.System.CloseRoundsUntilExit = int.MaxValue;
        late.System.OnPostClose = round =>
        {
            if (round == 2)
            {
                late.System.Children[late.Prism.Pid].Add(new ProcessIdentity(600, late.System.UtcNow, @"C:\Java\bin\javaw.exe"));
            }
        };
        Equal(JvmHelperOutcome.WaitingForPrismExit, new JvmSettingsHelper(late.System, late.Log.Add, null).RunActivePhase(latePlan), "late child: fallback F");
        Equal(0, late.System.Terminated.Count, "late child: never forced");
    }

    private static void TestLogsFolderAndStatusTexts(string root)
    {
        string minecraft = Path.Combine(root, "minecraft");
        JvmSettingsCoordinator.EnsureLogsDirectory(minecraft, _ => { });
        Equal(false, Directory.Exists(minecraft), "a missing game folder is not created");
        Directory.CreateDirectory(minecraft);
        JvmSettingsCoordinator.EnsureLogsDirectory(minecraft, _ => { });
        Equal(true, Directory.Exists(Path.Combine(minecraft, "logs")), "logs folder created");

        foreach (string text in new[] { JvmSettingsText.Restarting, JvmSettingsText.Deferred, JvmSettingsText.CouldNotApply })
        {
            Equal(true, JvmSettingsText.DetailFor(text).Length > 0, "detail line for: " + text);
            Equal(UpdatePhase.MemorySettings, JvmSettingsText.Progress(text).Phase, "memory-settings phase");
        }
        Equal("Prism will restart and launch Kewz's Cobblemon", JvmSettingsText.DetailFor(JvmSettingsText.Restarting), "restart detail");
        Equal(3, JvmSettingsCoordinator.RestartExitCode, "restart exit code 3");

        // The Program.Main wrapper: a failed update is returned unchanged and never evaluated; logs/ is still created.
        string instance = Path.Combine(root, "wrapper-instance");
        string wrapperMinecraft = Path.Combine(instance, "minecraft");
        Directory.CreateDirectory(wrapperMinecraft);
        var options = new CommandLine(instance, wrapperMinecraft, PrismPrelaunch: true, CheckOnly: false, NoUi: true);
        var lines = new List<string>();
        int failed = JvmSettingsCoordinator.RunAfterUpdaterAsync(options, null, (_, _) => Task.FromResult(1), lines.Add).GetAwaiter().GetResult();
        Equal(1, failed, "a failed update keeps its exit code");
        Equal(true, Directory.Exists(Path.Combine(wrapperMinecraft, "logs")), "logs/ created even when the update failed");
        Equal(true, lines.Any(line => line.Contains("not evaluated", StringComparison.Ordinal)), "failed update: not evaluated");
        int notPrism = JvmSettingsCoordinator.RunAfterUpdaterAsync(options with { PrismPrelaunch = false }, null, (_, _) => Task.FromResult(0), lines.Add).GetAwaiter().GetResult();
        Equal(0, notPrism, "outside Prism the wrapper is transparent");
    }

    private static void TestStatusCardClosesOnRestartCode()
    {
        // Exit code 3 must close the card by itself (Prism reads the code only after the process exits).
        Exception? failure = null;
        int exitCode = -1;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new UpdateStatusForm(
                    new CommandLine("test-instance", "test-minecraft", PrismPrelaunch: true, CheckOnly: false, NoUi: false),
                    (_, progress) =>
                    {
                        progress?.Report(JvmSettingsText.Progress(JvmSettingsText.Restarting));
                        return Task.FromResult(JvmSettingsCoordinator.RestartExitCode);
                    });
                Application.Run(form);
                exitCode = form.ExitCode;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException("The status card did not close by itself after exit code 3.");
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        Equal(3, exitCode, "card returns exit code 3");
    }

    // ---- Fixtures and helpers -----------------------------------------------------------------------------------------------

    private static byte[] Fixture(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("JvmFixtures." + name)
            ?? throw new FileNotFoundException("Missing embedded fixture " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static PrismIniDocument GlobalWith(params (string Key, string Encoded)[] values) =>
        PrismIniDocument.Parse(Fixture("prismlauncher.cfg"))
            .WithGeneralValues(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Encoded)).ToList(), out _);

    private static PrismEffectiveSettings Effective(int max, string jvmArgs, int min = 512) =>
        new(true, min, max, true, jvmArgs, PrismArgs.Split(jvmArgs), string.Empty);

    private static string InstJavaArgs(PrismEffectiveSettings effective, string? extra = null)
    {
        var parts = new List<string>(effective.JvmTokens);
        if (extra is not null)
        {
            parts.Add(extra);
        }
        parts.Add(HeapDump);
        parts.Add($"-Xms{Math.Min(effective.MinMemAllocMiB, effective.MaxMemAllocMiB)}m");
        parts.Add($"-Xmx{Math.Max(effective.MinMemAllocMiB, effective.MaxMemAllocMiB)}m");
        parts.Add("-Duser.language=en");
        return string.Join(' ', parts);
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
        }
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class RecordingProgress : IProgress<UpdateProgress>
    {
        public List<string> Messages { get; } = [];

        public void Report(UpdateProgress value) => Messages.Add(value.Message);
    }

    /// <summary>
    /// A temp Prism world: a portable Prism folder (fake prismlauncher.exe, portable.txt, the sanitized global cfg), one
    /// instance with a fixture instance.cfg, a local-data folder, and a fake system whose process tree is
    /// prismlauncher (100) -> powershell (200) -> updater (300).
    /// </summary>
    private sealed class Rig
    {
        public required string PrismDir { get; init; }
        public required string PrismExe { get; init; }
        public required string GlobalCfg { get; init; }
        public required string InstanceDir { get; init; }
        public required string InstanceCfg { get; init; }
        public required string McDir { get; init; }
        public required string LocalData { get; init; }
        public required UpdaterPaths Paths { get; init; }
        public required FakeSystem System { get; init; }
        public ProcessIdentity Prism { get; set; } = null!;
        public ProcessIdentity? PowerShell { get; set; }
        public ProcessIdentity Self { get; set; } = null!;
        public List<string> Log { get; } = [];

        public static Rig Create(string root, string fixture, string instanceId, bool withPowerShell = true, bool portable = true)
        {
            string prismDir = Path.Combine(root, "Prism Launcher");
            string instanceDir = Path.Combine(prismDir, "instances", instanceId);
            string mcDir = Path.Combine(instanceDir, "minecraft");
            string local = Path.Combine(root, "local", "0123456789abcdef");
            Directory.CreateDirectory(mcDir);
            Directory.CreateDirectory(local);
            string exe = Path.Combine(prismDir, "prismlauncher.exe");
            File.WriteAllBytes(exe, []);
            if (portable)
            {
                File.WriteAllText(Path.Combine(prismDir, "portable.txt"), string.Empty);
            }
            string globalCfg = Path.Combine(prismDir, "prismlauncher.cfg");
            File.WriteAllBytes(globalCfg, Fixture("prismlauncher.cfg"));
            string instanceCfg = Path.Combine(instanceDir, "instance.cfg");
            File.WriteAllBytes(instanceCfg, Fixture(fixture));
            string install = Path.Combine(mcDir, "cobble-music-updater");
            Directory.CreateDirectory(install);
            var paths = new UpdaterPaths(instanceDir, mcDir, install, Path.Combine(install, "updater.json"), Path.Combine(install, "state.json"), local);

            DateTime t0 = new(2026, 9, 23, 11, 0, 0, DateTimeKind.Utc);
            var system = new FakeSystem
            {
                Now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc),
                AppData = Path.Combine(root, "AppData", "Roaming"),
                LocalData = local,
                OwnExe = Path.Combine(mcDir, "cobble-music-updater", "CobbleMusicUpdater.exe")
            };
            var rig = new Rig
            {
                PrismDir = prismDir, PrismExe = exe, GlobalCfg = globalCfg, InstanceDir = instanceDir, InstanceCfg = instanceCfg,
                McDir = mcDir, LocalData = local, Paths = paths, System = system
            };
            rig.Prism = new ProcessIdentity(100, t0, exe);
            rig.PowerShell = withPowerShell ? new ProcessIdentity(200, t0.AddMinutes(30), @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe") : null;
            rig.Self = new ProcessIdentity(300, t0.AddMinutes(31), system.OwnExe);
            system.Chain = new PrismLaunchChain(rig.Self, rig.PowerShell, rig.Prism);
            system.Register(rig.Prism);
            system.Register(rig.Self);
            if (rig.PowerShell is not null)
            {
                system.Register(rig.PowerShell);
            }
            system.Children[100] = [rig.PowerShell ?? rig.Self];
            system.CommandLines[100] = $"\"{exe}\"";
            system.Env["INST_DIR"] = instanceDir;
            system.Env["INST_MC_DIR"] = mcDir;
            system.Env["INST_ID"] = instanceId;
            rig.RefreshJavaArgs();
            return rig;
        }

        /// <summary>INST_JAVA_ARGS as Prism would export it for the files as they are now.</summary>
        public void RefreshJavaArgs()
        {
            try
            {
                PrismEffectiveSettings effective = PrismEffectiveSettings.Resolve(
                    PrismIniDocument.Parse(File.ReadAllBytes(InstanceCfg)), PrismIniDocument.Parse(File.ReadAllBytes(GlobalCfg)), System.TotalPhys);
                System.Env["INST_JAVA_ARGS"] = InstJavaArgs(effective);
            }
            catch (PrismIniUnsupportedException)
            {
                System.Env["INST_JAVA_ARGS"] = string.Empty;
            }
        }

        /// <summary>After the abort: the updater and its PowerShell have exited, Prism has no children.</summary>
        public void EndPreLaunch()
        {
            System.Alive.Remove(300);
            System.Alive.Remove(200);
            System.Children[Prism.Pid].Clear();
        }

        /// <summary>The relaunched Prism starts the next pre-launch: new identities, INST_JAVA_ARGS from the edited files.</summary>
        public void StartNewPrism()
        {
            DateTime start = System.Now;
            Prism = new ProcessIdentity(101, start, PrismExe);
            PowerShell = new ProcessIdentity(201, start.AddSeconds(5), PowerShell!.ImagePath);
            Self = new ProcessIdentity(301, start.AddSeconds(6), Self.ImagePath);
            System.Chain = new PrismLaunchChain(Self, PowerShell, Prism);
            System.Register(Prism);
            System.Register(PowerShell);
            System.Register(Self);
            System.Children[101] = [PowerShell];
            System.CommandLines[101] = $"\"{PrismExe}\" --launch {System.Env["INST_ID"]}";
            RefreshJavaArgs();
        }
    }

    private sealed class FakeSystem : IJvmSettingsSystem
    {
        public DateTime Now { get; set; }
        public ulong TotalPhys { get; set; } = 32 * GiB;
        public Dictionary<string, string?> Env { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string AppData { get; set; } = string.Empty;
        public string LocalData { get; set; } = string.Empty;
        public string? OwnExe { get; set; }
        public PrismLaunchChain? Chain { get; set; }
        public HashSet<int> Alive { get; } = [];
        public Dictionary<int, List<ProcessIdentity>> Children { get; } = new();
        public List<ProcessIdentity> ExtraImageProcesses { get; } = [];
        public Dictionary<int, string> CommandLines { get; } = new();
        public Dictionary<int, ProcessIdentity> Identities { get; } = new();
        public int? MajorVersion { get; set; } = 10;
        public bool FailPreLaunchResult { get; set; } = true;
        public int FailPreLaunchCalls { get; private set; }
        public List<(int Pid, uint ExitCode)> Terminated { get; } = [];
        public int PostCloseCalls { get; private set; }
        public int CloseRoundsUntilExit { get; set; } = 1;
        public Action<int>? OnPostClose { get; set; }
        public Action? OnPrismExit { get; set; }
        public bool ExitOnLongWait { get; set; }
        public bool HelperWritesReady { get; set; } = true;
        public PrismRelaunchMethod RelaunchResult { get; set; } = PrismRelaunchMethod.Explorer;
        public List<(string Exe, IReadOnlyList<string> Arguments, string WorkingDirectory, string? DataDir)> Relaunches { get; } = [];

        public DateTime UtcNow => Now;
        public DateTime LocalNow => Now.ToLocalTime();
        public void Sleep(TimeSpan duration) => Now += duration;
        public ulong TotalPhysicalMemoryBytes() => TotalPhys;
        public string? GetEnvironmentVariable(string name) => Env.TryGetValue(name, out string? value) ? value : null;
        public string AppDataDirectory => AppData;
        public string? OwnExecutablePath => OwnExe;
        public string LocalDataDirectoryFor(string instanceDirectory, string minecraftDirectory) => LocalData;
        public PrismLaunchChain? ResolveLaunchChain() => Chain;
        /// <summary>Like the real ProcessTree: a PID is only "this process" when its creation time and image match too.</summary>
        public bool IsAlive(ProcessIdentity process) =>
            Alive.Contains(process.Pid) && (!Identities.TryGetValue(process.Pid, out ProcessIdentity? known) || known.Matches(process));

        public void Register(ProcessIdentity process)
        {
            Identities[process.Pid] = process;
            Alive.Add(process.Pid);
        }

        public bool WaitForExit(ProcessIdentity process, TimeSpan timeout)
        {
            if (!IsAlive(process))
            {
                return true;
            }
            if (ExitOnLongWait && timeout >= TimeSpan.FromHours(1) && process.Pid == Chain?.Prism.Pid)
            {
                Now += TimeSpan.FromMinutes(30);
                Exit(process.Pid);
                return true;
            }
            Now += timeout;
            return false;
        }

        public IReadOnlyList<ProcessIdentity> GetChildren(ProcessIdentity parent) =>
            IsAlive(parent) && Children.TryGetValue(parent.Pid, out List<ProcessIdentity>? children) ? [.. children] : [];

        public IReadOnlyList<ProcessIdentity> FindProcessesByImage(string imagePath)
        {
            var result = new List<ProcessIdentity>();
            if (Chain is not null && Alive.Contains(Chain.Prism.Pid) && string.Equals(Chain.Prism.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(Chain.Prism);
            }
            result.AddRange(ExtraImageProcesses.Where(process => string.Equals(process.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase)));
            return result;
        }

        public string? TryGetCommandLine(ProcessIdentity process) =>
            IsAlive(process) && CommandLines.TryGetValue(process.Pid, out string? line) ? line : null;
        public int? GetFileMajorVersion(string path) => MajorVersion;

        public bool FailPreLaunch(PrismLaunchChain chain, uint exitCode, Action<string> log)
        {
            FailPreLaunchCalls++;
            return FailPreLaunchResult;
        }

        public bool TryTerminate(ProcessIdentity process, uint exitCode)
        {
            Terminated.Add((process.Pid, exitCode));
            Exit(process.Pid);
            return true;
        }

        public int PostCloseToVisibleTopLevelWindows(ProcessIdentity process)
        {
            PostCloseCalls++;
            OnPostClose?.Invoke(PostCloseCalls);
            if (PostCloseCalls >= CloseRoundsUntilExit && (!Children.TryGetValue(process.Pid, out List<ProcessIdentity>? children) || children.Count == 0))
            {
                Exit(process.Pid);
            }
            return 2;
        }

        public ProcessIdentity? StartHelper(string planPath)
        {
            if (HelperWritesReady)
            {
                File.WriteAllText(JvmSettingsPlan.ReadyPathFor(Path.GetDirectoryName(planPath)!), "ready");
            }
            var helper = new ProcessIdentity(900, Now, OwnExe ?? "helper.exe");
            Register(helper);
            return helper;
        }

        public PrismRelaunchMethod Relaunch(string exePath, IReadOnlyList<string> arguments, string workingDirectory, string? dataDirEnvironment, Action<string> log)
        {
            Relaunches.Add((exePath, arguments, workingDirectory, dataDirEnvironment));
            return RelaunchResult;
        }

        private void Exit(int pid)
        {
            if (Alive.Remove(pid) && Chain is not null && pid == Chain.Prism.Pid)
            {
                OnPrismExit?.Invoke();
            }
        }
    }
}
