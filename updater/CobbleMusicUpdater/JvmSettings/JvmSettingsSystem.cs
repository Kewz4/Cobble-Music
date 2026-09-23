using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace CobbleMusicUpdater;

/// <summary>How Prism was started again by the helper.</summary>
internal enum PrismRelaunchMethod
{
    Explorer,
    CreateProcessFallback,
    Failed
}

/// <summary>
/// Everything the JVM-settings coordinator and helper do to real processes, windows, COM and the clock. The decision logic
/// only talks to this interface, so the tests drive every branch with a fake and no real Prism is ever touched.
/// </summary>
internal interface IJvmSettingsSystem
{
    DateTime UtcNow { get; }
    DateTime LocalNow { get; }
    void Sleep(TimeSpan duration);
    ulong TotalPhysicalMemoryBytes();
    string? GetEnvironmentVariable(string name);
    string AppDataDirectory { get; }
    string? OwnExecutablePath { get; }
    string LocalDataDirectoryFor(string instanceDirectory, string minecraftDirectory);
    PrismLaunchChain? ResolveLaunchChain();
    bool IsAlive(ProcessIdentity process);
    bool WaitForExit(ProcessIdentity process, TimeSpan timeout);
    IReadOnlyList<ProcessIdentity> GetChildren(ProcessIdentity parent);
    IReadOnlyList<ProcessIdentity> FindProcessesByImage(string imagePath);
    string? TryGetCommandLine(ProcessIdentity process);
    int? GetFileMajorVersion(string path);
    bool FailPreLaunch(PrismLaunchChain chain, uint exitCode, Action<string> log);
    bool TryTerminate(ProcessIdentity process, uint exitCode);
    int PostCloseToVisibleTopLevelWindows(ProcessIdentity process);
    ProcessIdentity? StartHelper(string planPath);
    PrismRelaunchMethod Relaunch(string exePath, IReadOnlyList<string> arguments, string workingDirectory, string? dataDirEnvironment, Action<string> log);
}

/// <summary>The real Windows implementation (Toolhelp/NT process queries via <see cref="ProcessTree"/>, user32, COM).</summary>
internal sealed class WindowsJvmSettingsSystem : IJvmSettingsSystem
{
    private const uint WmClose = 0x0010;

    public DateTime UtcNow => DateTime.UtcNow;

    public DateTime LocalNow => DateTime.Now;

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);

    public ulong TotalPhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalMemoryStatusEx failed.");
        }
        return status.ullTotalPhys;
    }

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string AppDataDirectory => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string? OwnExecutablePath => Environment.ProcessPath;

    public string LocalDataDirectoryFor(string instanceDirectory, string minecraftDirectory) =>
        LocalStateStore.ResolvePaths(instanceDirectory, minecraftDirectory).LocalDataDirectory;

    public PrismLaunchChain? ResolveLaunchChain() => PrismLaunchChain.TryResolveCurrent();

    public bool IsAlive(ProcessIdentity process) => ProcessTree.IsAlive(process);

    public bool WaitForExit(ProcessIdentity process, TimeSpan timeout) => ProcessTree.WaitForExit(process, timeout);

    public IReadOnlyList<ProcessIdentity> GetChildren(ProcessIdentity parent) => ProcessTree.GetChildren(parent);

    public IReadOnlyList<ProcessIdentity> FindProcessesByImage(string imagePath)
    {
        string exeName = Path.GetFileName(imagePath);
        var result = new List<ProcessIdentity>();
        foreach ((int pid, (_, string name)) in ProcessTree.Snapshot())
        {
            if (string.Equals(name, exeName, StringComparison.OrdinalIgnoreCase)
                && ProcessTree.TryGetIdentity(pid, out ProcessIdentity identity)
                && string.Equals(identity.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(identity);
            }
        }
        return result;
    }

    public string? TryGetCommandLine(ProcessIdentity process) => ProcessTree.TryGetCommandLine(process);

    public int? GetFileMajorVersion(string path)
    {
        try
        {
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileMajorPart : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool FailPreLaunch(PrismLaunchChain chain, uint exitCode, Action<string> log) => chain.TryFailPreLaunch(exitCode, log);

    public bool TryTerminate(ProcessIdentity process, uint exitCode) => ProcessTree.TryTerminate(process, exitCode);

    /// <summary>Posts WM_CLOSE to every visible top-level window owned by the verified process (dialogs included).</summary>
    public int PostCloseToVisibleTopLevelWindows(ProcessIdentity process)
    {
        if (!ProcessTree.IsAlive(process))
        {
            return 0;
        }
        var windows = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == (uint)process.Pid && IsWindowVisible(window))
            {
                windows.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        int posted = 0;
        foreach (IntPtr window in windows)
        {
            if (PostMessageW(window, WmClose, IntPtr.Zero, IntPtr.Zero))
            {
                posted++;
            }
        }
        return posted;
    }

    public ProcessIdentity? StartHelper(string planPath)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return null;
        }
        // Plain CreateProcess with bInheritHandles=FALSE: when Prism runs the updater directly (legacy command), the updater's
        // standard handles are Prism's pre-launch pipes, and the helper must not keep them open after the updater exits.
        // The helper stays in the pre-launch PowerShell's job (no limits, no kill-on-close); that PowerShell is terminated to
        // abort the launch, so nothing waits on the job.
        var commandLine = new StringBuilder(PrismArgs.JoinWindowsArguments([exe, JvmSettingsHelper.CommandLineSwitch, planPath]));
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                Path.GetDirectoryName(exe), ref startup, out ProcessInformation information))
        {
            return null;
        }
        CloseHandle(information.hThread);
        try
        {
            return ProcessTree.TryGetIdentity(information.dwProcessId, out ProcessIdentity identity) ? identity : null;
        }
        finally
        {
            CloseHandle(information.hProcess);
        }
    }

    public PrismRelaunchMethod Relaunch(
        string exePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? dataDirEnvironment,
        Action<string> log)
    {
        string parameters = PrismArgs.JoinWindowsArguments(arguments);
        try
        {
            ExplorerShellExecute(exePath, parameters, workingDirectory);
            return PrismRelaunchMethod.Explorer;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or InvalidOperationException
            or TargetInvocationException or MissingMethodException or UnauthorizedAccessException)
        {
            log($"Explorer could not start Prism ({exception.GetType().Name}: {exception.Message}); using CreateProcess with a fresh user environment.");
        }
        try
        {
            CreateProcessWithUserEnvironment(exePath, parameters, workingDirectory, dataDirEnvironment);
            return PrismRelaunchMethod.CreateProcessFallback;
        }
        catch (Win32Exception exception)
        {
            log($"CreateProcess could not start Prism: {exception.Message}");
            return PrismRelaunchMethod.Failed;
        }
    }

    /// <summary>
    /// Starts a program through the desktop Explorer's IShellDispatch2::ShellExecute (the shell of SWC_DESKTOP, found through
    /// IShellWindows::FindWindowSW). explorer.exe creates the process, so it is outside the pre-launch PowerShell's job and
    /// gets the user's normal environment instead of Prism's pre-launch one.
    /// </summary>
    private static void ExplorerShellExecute(string file, string parameters, string directory)
    {
        Type shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
        object shellWindowsObject = Activator.CreateInstance(shellWindowsType)
            ?? throw new InvalidOperationException("ShellWindows could not be created.");
        object? desktopDispatch = null;
        object? browserObject = null;
        object? viewObject = null;
        object? folderView = null;
        object? shell = null;
        try
        {
            var shellWindows = (IShellWindows)shellWindowsObject;
            object location = 0; // CSIDL_DESKTOP
            object root = null!;
            int hr = shellWindows.FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out _, 1 /* SWFO_NEEDDISPATCH */, out desktopDispatch);
            if (hr != 0 || desktopDispatch is null)
            {
                throw new COMException("The desktop shell window was not found.", hr == 0 ? unchecked((int)0x80004005) : hr);
            }
            Guid topLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
            Guid shellBrowserId = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(((IComServiceProvider)desktopDispatch).QueryService(ref topLevelBrowser, ref shellBrowserId, out browserObject));
            Marshal.ThrowExceptionForHR(((IShellBrowser)browserObject).QueryActiveShellView(out viewObject));
            Guid dispatchId = new("00020400-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(((IShellView)viewObject).GetItemObject(0 /* SVGIO_BACKGROUND */, ref dispatchId, out folderView));
            shell = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null)
                ?? throw new InvalidOperationException("The desktop folder view has no Shell.Application.");
            shell.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, shell,
                [file, parameters, directory, "open", 1 /* SW_SHOWNORMAL */]);
        }
        finally
        {
            foreach (object? comObject in new[] { shell, folderView, viewObject, browserObject, desktopDispatch, shellWindowsObject })
            {
                if (comObject is not null && Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
        }
    }

    /// <summary>
    /// Fallback relaunch: CreateProcess with CreateEnvironmentBlock(user token, inherit FALSE), so Prism does not inherit the
    /// pre-launch INST_* variables, plus PRISMLAUNCHER_DATA_DIR when Prism had it.
    /// </summary>
    private static void CreateProcessWithUserEnvironment(string exePath, string parameters, string workingDirectory, string? dataDirEnvironment)
    {
        const uint TokenQuery = 0x0008;
        const uint TokenDuplicate = 0x0002;
        const uint CreateUnicodeEnvironment = 0x00000400;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out IntPtr token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed.");
        }
        IntPtr block = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out block, token, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");
            }
            var variables = ReadEnvironmentBlock(block);
            if (!string.IsNullOrEmpty(dataDirEnvironment))
            {
                variables.RemoveAll(entry => entry.StartsWith(PrismDataDir.DataDirEnvironmentVariable + "=", StringComparison.OrdinalIgnoreCase));
                variables.Add(PrismDataDir.DataDirEnvironmentVariable + "=" + dataDirEnvironment);
                variables.Sort(StringComparer.OrdinalIgnoreCase);
            }
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables) + "\0\0");
            var commandLine = new StringBuilder(PrismArgs.QuoteWindowsArgument(exePath) + (parameters.Length > 0 ? " " + parameters : string.Empty));
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessW(exePath, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment, environment,
                    workingDirectory, ref startup, out ProcessInformation information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed.");
            }
            CloseHandle(information.hThread);
            CloseHandle(information.hProcess);
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }
            if (block != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(block);
            }
            CloseHandle(token);
        }
    }

    private static List<string> ReadEnvironmentBlock(IntPtr block)
    {
        var variables = new List<string>();
        int offset = 0;
        while (true)
        {
            string entry = Marshal.PtrToStringUni(IntPtr.Add(block, offset)) ?? string.Empty;
            if (entry.Length == 0)
            {
                return variables;
            }
            variables.Add(entry);
            offset += (entry.Length + 1) * 2;
        }
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        // Slots before FindWindowSW are declared only to keep the vtable order; they are never called.
        void Count();
        void Item();
        void NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();

        [PreserveSig]
        int FindWindowSW(
            ref object location,
            ref object root,
            int windowClass,
            out int windowHandle,
            int options,
            [MarshalAs(UnmanagedType.IDispatch)] out object? dispatch);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();

        [PreserveSig]
        int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();

        [PreserveSig]
        int GetItemObject(uint item, ref Guid interfaceId, [MarshalAs(UnmanagedType.IDispatch)] out object result);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
