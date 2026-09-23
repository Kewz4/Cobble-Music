using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CobbleMusicUpdater;

/// <summary>
/// A process as it existed when observed: the PID plus its creation time and image path. Windows reuses PIDs, so a PID alone
/// never identifies a process; every comparison and every termination re-checks the creation time (and the image path).
/// </summary>
internal sealed record ProcessIdentity(int Pid, DateTime StartUtc, string ImagePath)
{
    public string ExeName => Path.GetFileName(ImagePath);

    public bool Matches(ProcessIdentity other) =>
        Pid == other.Pid
        && StartUtc == other.StartUtc
        && string.Equals(ImagePath, other.ImagePath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Read-only Windows process-tree queries (Toolhelp snapshot, GetProcessTimes, QueryFullProcessImageNameW,
/// NtQueryInformationProcess command line) plus a termination that re-validates identity first. No WMI.
/// </summary>
internal static class ProcessTree
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const int ProcessCommandLineInformation = 60;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint StatusBufferOverflow = 0x80000005;
    private const uint StatusBufferTooSmall = 0xC0000023;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>pid -> (parent pid, exe file name) for every process visible to this user at one instant.</summary>
    public static IReadOnlyDictionary<int, (int ParentPid, string ExeName)> Snapshot()
    {
        var result = new Dictionary<int, (int, string)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot failed.");
        }
        try
        {
            var entry = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return result;
            }
            do
            {
                result[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
                entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>();
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    /// <summary>Creation time and full image path of a live process, or false when it is gone or not queryable.</summary>
    public static bool TryGetIdentity(int pid, out ProcessIdentity identity)
    {
        identity = null!;
        if (pid <= 0)
        {
            return false;
        }
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            if (!TryReadIdentity(handle, pid, out identity))
            {
                return false;
            }
            return !HasExited(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>True when the exact process (PID + creation time + image) is still alive.</summary>
    public static bool IsAlive(ProcessIdentity expected) =>
        TryGetIdentity(expected.Pid, out ProcessIdentity current) && current.Matches(expected);

    /// <summary>
    /// The parent chain of <paramref name="pid"/>, nearest parent first. A parent is accepted only when it was created no later
    /// than its child (otherwise the parent PID was reused by an unrelated process and the walk stops).
    /// </summary>
    public static IReadOnlyList<ProcessIdentity> GetAncestors(int pid, int maxDepth = 8)
    {
        var chain = new List<ProcessIdentity>();
        IReadOnlyDictionary<int, (int ParentPid, string ExeName)> snapshot = Snapshot();
        if (!TryGetIdentity(pid, out ProcessIdentity child))
        {
            return chain;
        }
        var seen = new HashSet<int> { pid };
        for (int depth = 0; depth < maxDepth; depth++)
        {
            if (!snapshot.TryGetValue(child.Pid, out (int ParentPid, string ExeName) link)
                || link.ParentPid <= 0
                || !seen.Add(link.ParentPid)
                || !TryGetIdentity(link.ParentPid, out ProcessIdentity parent)
                || parent.StartUtc > child.StartUtc)
            {
                break;
            }
            chain.Add(parent);
            child = parent;
        }
        return chain;
    }

    /// <summary>Live direct children of <paramref name="parent"/> (created after it, so a reused PID is never a child).</summary>
    public static IReadOnlyList<ProcessIdentity> GetChildren(ProcessIdentity parent)
    {
        var children = new List<ProcessIdentity>();
        foreach ((int pid, (int parentPid, _)) in Snapshot())
        {
            if (parentPid != parent.Pid || pid == parent.Pid)
            {
                continue;
            }
            if (TryGetIdentity(pid, out ProcessIdentity child) && child.StartUtc >= parent.StartUtc)
            {
                children.Add(child);
            }
        }
        return children;
    }

    /// <summary>All live descendants of <paramref name="root"/>, breadth first, with the same creation-time guard.</summary>
    public static IReadOnlyList<ProcessIdentity> GetDescendants(ProcessIdentity root)
    {
        IReadOnlyDictionary<int, (int ParentPid, string ExeName)> snapshot = Snapshot();
        var byParent = new Dictionary<int, List<int>>();
        foreach ((int pid, (int parentPid, _)) in snapshot)
        {
            if (!byParent.TryGetValue(parentPid, out List<int>? list))
            {
                byParent[parentPid] = list = new List<int>();
            }
            list.Add(pid);
        }
        var result = new List<ProcessIdentity>();
        var queue = new Queue<ProcessIdentity>();
        var seen = new HashSet<int> { root.Pid };
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            ProcessIdentity current = queue.Dequeue();
            if (!byParent.TryGetValue(current.Pid, out List<int>? childPids))
            {
                continue;
            }
            foreach (int childPid in childPids)
            {
                if (seen.Add(childPid)
                    && TryGetIdentity(childPid, out ProcessIdentity child)
                    && child.StartUtc >= current.StartUtc)
                {
                    result.Add(child);
                    queue.Enqueue(child);
                }
            }
        }
        return result;
    }

    /// <summary>The process's original command line (NtQueryInformationProcess class 60), or null when unavailable.</summary>
    public static string? TryGetCommandLine(ProcessIdentity expected)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)expected.Pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            if (!TryReadIdentity(handle, expected.Pid, out ProcessIdentity current) || !current.Matches(expected))
            {
                return null;
            }
            int size = 2048;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    uint status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, (uint)size, out uint needed);
                    if (status == StatusInfoLengthMismatch || status == StatusBufferOverflow || status == StatusBufferTooSmall)
                    {
                        size = (int)Math.Max(needed, (uint)size * 2);
                        continue;
                    }
                    if (status != 0)
                    {
                        return null;
                    }
                    // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; } followed by the characters.
                    ushort lengthBytes = (ushort)Marshal.ReadInt16(buffer);
                    IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, lengthBytes / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Terminates the process only if it is still exactly <paramref name="expected"/> (same PID, creation time and image).
    /// Returns false (and terminates nothing) on any mismatch or failure.
    /// </summary>
    public static bool TryTerminate(ProcessIdentity expected, uint exitCode)
    {
        IntPtr handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation | Synchronize, false, (uint)expected.Pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            if (!TryReadIdentity(handle, expected.Pid, out ProcessIdentity current) || !current.Matches(expected) || HasExited(handle))
            {
                return false;
            }
            return TerminateProcess(handle, exitCode);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Waits until the exact process has exited (true) or the timeout elapsed (false). A vanished process counts as exited.</summary>
    public static bool WaitForExit(ProcessIdentity expected, TimeSpan timeout)
    {
        IntPtr handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, (uint)expected.Pid);
        if (handle == IntPtr.Zero)
        {
            return true;
        }
        try
        {
            if (!TryReadIdentity(handle, expected.Pid, out ProcessIdentity current) || !current.Matches(expected))
            {
                return true;
            }
            uint milliseconds = (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
            return WaitForSingleObject(handle, milliseconds) == 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryReadIdentity(IntPtr handle, int pid, out ProcessIdentity identity)
    {
        identity = null!;
        if (!GetProcessTimes(handle, out long creation, out _, out _, out _))
        {
            return false;
        }
        var builder = new char[32768];
        uint length = (uint)builder.Length;
        if (!QueryFullProcessImageNameW(handle, 0, builder, ref length))
        {
            return false;
        }
        identity = new ProcessIdentity(pid, DateTime.FromFileTimeUtc(creation), new string(builder, 0, (int)length));
        return true;
    }

    private static bool HasExited(IntPtr handle) => WaitForSingleObject(handle, 0) == 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, uint length, out uint returnLength);
}

/// <summary>
/// The Prism pre-launch chain this updater runs in: (a) prismlauncher.exe -> powershell.exe (friend command + bootstrap, one
/// process) -> this updater, or (b) prismlauncher.exe -> this updater (legacy installer command). Each link is verified with the
/// creation-time guard of <see cref="ProcessTree.GetAncestors"/>.
/// </summary>
internal sealed record PrismLaunchChain(ProcessIdentity Self, ProcessIdentity? PowerShell, ProcessIdentity Prism)
{
    public static PrismLaunchChain? TryResolve(int selfPid)
    {
        if (!ProcessTree.TryGetIdentity(selfPid, out ProcessIdentity self))
        {
            return null;
        }
        IReadOnlyList<ProcessIdentity> ancestors = ProcessTree.GetAncestors(selfPid, maxDepth: 3);
        if (ancestors.Count >= 1 && IsPrism(ancestors[0]))
        {
            return new PrismLaunchChain(self, null, ancestors[0]);
        }
        if (ancestors.Count >= 2 && IsPowerShell(ancestors[0]) && IsPrism(ancestors[1]))
        {
            return new PrismLaunchChain(self, ancestors[0], ancestors[1]);
        }
        return null;
    }

    public static PrismLaunchChain? TryResolveCurrent() => TryResolve(Environment.ProcessId);

    /// <summary>
    /// Makes Prism fail this launch's pre-launch step with <paramref name="exitCode"/>. In chain (a) the verified pre-launch
    /// PowerShell (which would otherwise report success regardless of the updater's exit code) is terminated with that code;
    /// in chain (b) the caller's own exit code already reaches Prism, so nothing is terminated. Returns true when Prism will see
    /// a non-zero pre-launch result.
    /// </summary>
    public bool TryFailPreLaunch(uint exitCode, Action<string> log)
    {
        if (exitCode == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exitCode), "A failing pre-launch needs a non-zero exit code.");
        }
        if (PowerShell is null)
        {
            log($"Prism runs the updater directly; exit code {exitCode} fails the pre-launch step.");
            return true;
        }
        if (!ProcessTree.IsAlive(Prism))
        {
            log("Prism is no longer running; there is no launch to stop.");
            return false;
        }
        bool terminated = ProcessTree.TryTerminate(PowerShell, exitCode);
        log(terminated
            ? $"Stopped the pre-launch PowerShell (pid {PowerShell.Pid}) with exit code {exitCode}, so Prism does not start Minecraft."
            : $"Could not stop the pre-launch PowerShell (pid {PowerShell.Pid}); it changed or already exited.");
        return terminated;
    }

    public static bool IsPrism(ProcessIdentity process) =>
        string.Equals(process.ExeName, "prismlauncher.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsPowerShell(ProcessIdentity process) =>
        string.Equals(process.ExeName, "powershell.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(process.ExeName, "pwsh.exe", StringComparison.OrdinalIgnoreCase);
}
