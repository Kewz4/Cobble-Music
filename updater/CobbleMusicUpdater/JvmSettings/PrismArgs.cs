using System.Runtime.InteropServices;
using System.Text;

namespace CobbleMusicUpdater;

/// <summary>Command-line splitting exactly as Prism and Windows do it.</summary>
internal static class PrismArgs
{
    /// <summary>
    /// Exact port of Prism 10.0.5 <c>Commandline::splitArgs</c> (launcher/Commandline.cpp:47-84), which turns the JvmArgs
    /// setting into JVM arguments: split on ' ' only (never tabs), '"' and '\'' quote, '\' escapes only inside quotes, an
    /// unterminated quote runs to the end, and empty tokens (including "") are dropped.
    /// </summary>
    public static IReadOnlyList<string> Split(string args)
    {
        var argv = new List<string>();
        var current = new StringBuilder();
        bool escape = false;
        char inquotes = '\0';
        foreach (char cchar in args)
        {
            if (escape)
            {
                current.Append(cchar);
                escape = false;
            }
            else if (inquotes != '\0')
            {
                if (cchar == '\\')
                {
                    escape = true;
                }
                else if (cchar == inquotes)
                {
                    inquotes = '\0';
                }
                else
                {
                    current.Append(cchar);
                }
            }
            else
            {
                if (cchar == ' ')
                {
                    if (current.Length > 0)
                    {
                        argv.Add(current.ToString());
                        current.Clear();
                    }
                }
                else if (cchar == '"' || cchar == '\'')
                {
                    inquotes = cchar;
                }
                else
                {
                    current.Append(cchar);
                }
            }
        }
        if (current.Length > 0)
        {
            argv.Add(current.ToString());
        }
        return argv;
    }

    /// <summary>
    /// Splits a Windows command line the way Qt's QCoreApplication::arguments() does on Windows (CommandLineToArgvW), so the
    /// relaunch plan sees the same argv Prism parsed. Returns null when the API fails.
    /// </summary>
    public static string[]? SplitWindowsCommandLine(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return null;
        }
        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var result = new string[count];
            for (int index = 0; index < count; index++)
            {
                result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
            }
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <summary>
    /// Joins arguments into one Windows command-line string that CommandLineToArgvW splits back into exactly the same
    /// arguments (the documented MSVCRT rules: quote when needed, double the backslashes that precede a quote).
    /// </summary>
    public static string JoinWindowsArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteWindowsArgument));

    public static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }
        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(character);
            }
            backslashes = 0;
        }
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
