using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace CobbleMusicUpdater;

/// <summary>
/// A Qt-free stand-in for prismlauncher.exe 10.0.5, for the jvm-args integration tests (FINDINGS section 11, integration
/// test 1 and the guard tests). It reproduces only what the updater observes:
/// - it is named prismlauncher.exe with FileVersion 10.0.5 and runs from a portable folder (portable.txt, prismlauncher.cfg,
///   instances/&lt;id&gt;/instance.cfg);
/// - with --launch &lt;id&gt; it reads instance.cfg + prismlauncher.cfg at launch start, exports INST_NAME, INST_ID, INST_DIR,
///   INST_MC_DIR and INST_JAVA_ARGS exactly as MinecraftInstance::getVariables does, substitutes $VARS in PreLaunchCommand,
///   splits it like QProcess::splitCommand and runs it with redirected pipes and CREATE_NO_WINDOW (like QProcess);
/// - it logs "Process exited with code N." and "Pre-Launch command failed with code N." / "...ran successfully." to
///   fake-prism.log in its folder, then "would start Minecraft with INST_JAVA_ARGS=..." on success;
/// - it shows one visible top-level window; closing it while the pre-launch runs only hides it (Prism keeps running while an
///   instance launch is pending, Application.cpp:1626-1629), otherwise the process exits;
/// - --spawn-dummy-game &lt;seconds&gt; starts a child process that stands in for a running javaw (the "game running" guard).
/// Never shipped; never pointed at a real Prism data folder.
/// </summary>
internal static class FakePrismProgram
{
    private static string _logPath = "fake-prism.log";

    [STAThread]
    public static int Main(string[] args)
    {
        string exeDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string dataDirectory = exeDirectory;
        string? launchId = null;
        int dummyGameSeconds = 0;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-d" or "--dir" when index + 1 < args.Length:
                    dataDirectory = Path.GetFullPath(args[++index]);
                    break;
                case "-l" or "--launch" when index + 1 < args.Length:
                    launchId = args[++index];
                    break;
                case "--spawn-dummy-game" when index + 1 < args.Length:
                    dummyGameSeconds = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--alive":
                    break;
                default:
                    Log($"unknown argument {args[index]}");
                    return 2;
            }
        }
        _logPath = Path.Combine(dataDirectory, "fake-prism.log");
        Log($"started: {Environment.CommandLine}");

        ApplicationConfiguration.Initialize();
        using var form = new Form { Text = "Fake Prism Launcher", Width = 420, Height = 160 };
        var status = new Label { Dock = DockStyle.Fill, Text = "Fake Prism Launcher (jvm-args integration test)" };
        form.Controls.Add(status);
        bool launchPending = false;
        form.FormClosing += (_, eventArgs) =>
        {
            Log("WM_CLOSE received on the main window.");
            if (launchPending)
            {
                eventArgs.Cancel = true;
                form.Hide();
                Log("a launch is pending, so the window was hidden and the process keeps running.");
            }
        };
        form.Shown += async (_, _) =>
        {
            if (dummyGameSeconds > 0)
            {
                using Process? game = Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n {dummyGameSeconds} 127.0.0.1 > nul")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                Log($"dummy game child started (pid {game?.Id}).");
            }
            if (launchId is null)
            {
                return;
            }
            launchPending = true;
            int exitCode = await Task.Run(() => RunPreLaunch(dataDirectory, launchId));
            launchPending = false;
            if (!form.Visible)
            {
                Log("the launch ended while the window was closed; exiting.");
                form.Close();
            }
            status.Text = exitCode == 0 ? "Pre-launch succeeded." : $"Pre-launch failed with code {exitCode}.";
        };
        Application.Run(form);
        Log("exiting.");
        return 0;
    }

    private static int RunPreLaunch(string dataDirectory, string instanceId)
    {
        try
        {
            string instanceDirectory = Path.Combine(dataDirectory, "instances", instanceId);
            string minecraftDirectory = Path.Combine(instanceDirectory, "minecraft");
            // Launch-time reload (LaunchController.cpp:392): read the files now, like Prism.
            PrismIniDocument instance = PrismIniDocument.Parse(File.ReadAllBytes(Path.Combine(instanceDirectory, "instance.cfg")));
            PrismIniDocument global = PrismIniDocument.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "prismlauncher.cfg")));
            PrismEffectiveSettings effective = PrismEffectiveSettings.Resolve(instance, global, (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
            var javaArguments = new List<string>(effective.JvmTokens)
            {
                "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump",
                $"-Xms{effective.XmsMiB}m",
                $"-Xmx{effective.HeapMiB}m",
                "-Duser.language=en"
            };
            var variables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["INST_NAME"] = instance.GetGeneralString("name") ?? instanceId,
                ["INST_ID"] = instanceId,
                ["INST_DIR"] = instanceDirectory,
                ["INST_MC_DIR"] = minecraftDirectory,
                ["INST_JAVA"] = instance.GetGeneralString("JavaPath") ?? string.Empty,
                ["INST_JAVA_ARGS"] = string.Join(' ', javaArguments),
                ["NO_COLOR"] = "1"
            };
            string? command = PrismIni.ToBool(instance.GetGeneralString("OverrideCommands"))
                ? instance.GetGeneralString("PreLaunchCommand")
                : global.GetGeneralString("PreLaunchCommand");
            if (string.IsNullOrWhiteSpace(command))
            {
                Log($"no pre-launch command; would start Minecraft with INST_JAVA_ARGS={variables["INST_JAVA_ARGS"]}");
                return 0;
            }
            string expanded = ExpandVariables(command, variables);
            Log($"Running Pre-Launch command: {expanded}");
            List<string> argv = SplitCommand(expanded);
            var start = new ProcessStartInfo(argv[0])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in argv.Skip(1))
            {
                start.ArgumentList.Add(argument);
            }
            foreach ((string key, string value) in variables)
            {
                start.Environment[key] = value;
            }
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("the pre-launch command did not start");
            process.OutputDataReceived += (_, line) => { if (line.Data is not null) { Log("pre-launch stdout: " + line.Data); } };
            process.ErrorDataReceived += (_, line) => { if (line.Data is not null) { Log("pre-launch stderr: " + line.Data); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            Log($"Process exited with code {process.ExitCode}.");
            if (process.ExitCode != 0)
            {
                Log($"Pre-Launch command failed with code {process.ExitCode}.");
                return process.ExitCode;
            }
            Log("Pre-Launch command ran successfully.");
            Log($"would start Minecraft with INST_JAVA_ARGS={variables["INST_JAVA_ARGS"]}");
            return 0;
        }
        catch (Exception exception)
        {
            Log($"launch failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    /// <summary>$VAR and ${VAR} expansion for the INST_* variables (Prism's expandVariables, simplified to known names).</summary>
    private static string ExpandVariables(string command, IReadOnlyDictionary<string, string> variables)
    {
        foreach ((string key, string value) in variables.OrderByDescending(pair => pair.Key.Length))
        {
            command = command.Replace("${" + key + "}", value, StringComparison.Ordinal).Replace("$" + key, value, StringComparison.Ordinal);
        }
        return command;
    }

    /// <summary>Port of QProcess::splitCommand: '"' quotes, three consecutive quotes are one literal quote.</summary>
    private static List<string> SplitCommand(string command)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        int quoteCount = 0;
        bool inQuote = false;
        foreach (char ch in command)
        {
            if (ch == '"')
            {
                ++quoteCount;
                if (quoteCount == 3)
                {
                    quoteCount = 0;
                    current.Append(ch);
                }
                continue;
            }
            if (quoteCount > 0)
            {
                if (quoteCount == 1)
                {
                    inQuote = !inQuote;
                }
                quoteCount = 0;
            }
            if (!inQuote && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }
        return args;
    }

    private static void Log(string message)
    {
        string line = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}{Environment.NewLine}";
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.AppendAllText(_logPath, line, new UTF8Encoding(false));
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
