using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SuperBiMcp;

/// <summary>
/// The Windows process/window interop the Desktop-driving surfaces share: engine discovery (the msmdsrv
/// child of a Desktop PID and its listening TCP port), process identity (pid + start time), the
/// AttachThreadInput + Ctrl+S save dance, and the ownership assertion that makes a port provably belong to
/// a Desktop this process launched.
///
/// Every member carries its own <see cref="OperatingSystem.IsWindows"/> guard rather than a class-level
/// [SupportedOSPlatform], because these are called from cross-platform code paths that must still build and
/// run on a CI box: off Windows each one degrades to false/0/null instead of throwing.
///
/// The pure halves - <see cref="AssertPortOwnedByLaunchedPid"/> with injected lookups,
/// <see cref="ResolvePbixExe"/> and the retry wrapper with an injected sleep - are unit-tested offline; the
/// live-process half is deliberately untested here (it needs a real Desktop, which no CI box has).
/// </summary>
internal static class DesktopInterop
{
    internal const string DefaultPbixExe = @"C:\Program Files\Microsoft Power BI Desktop\bin\PBIDesktop.exe";

    /// <summary>The flag wins, then DAXOPS_PBIDESKTOP_EXE, then the stock install path.</summary>
    internal static string ResolvePbixExe(string? overridePath) =>
        overridePath
        ?? Environment.GetEnvironmentVariable("DAXOPS_PBIDESKTOP_EXE")
        ?? DefaultPbixExe;

    /// <summary>Logging sink. stdout is the MCP JSON-RPC channel, so this may only ever write to stderr.</summary>
    internal static Action<string> Log { get; set; } = m => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");

    // =================== process topology (toolhelp) ===================

    /// <summary>PIDs of every running &lt;exeName&gt; whose parent is &lt;parentPid&gt; (toolhelp snapshot walk).</summary>
    internal static IEnumerable<int> FindChildProcessIds(int parentPid, string exeName)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) yield break;
        try
        {
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref pe)) yield break;
            do
            {
                if (pe.th32ParentProcessID == (uint)parentPid &&
                    string.Equals(pe.szExeFile, exeName, StringComparison.OrdinalIgnoreCase))
                    yield return (int)pe.th32ProcessID;
            }
            while (Process32Next(snap, ref pe));
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    /// <summary>The parent pid of <paramref name="pid"/> via a toolhelp snapshot walk (0 if not found).</summary>
    internal static int GetParentPid(int pid)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
        try
        {
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref pe)) return 0;
            do
            {
                if (pe.th32ProcessID == (uint)pid) return (int)pe.th32ParentProcessID;
            }
            while (Process32Next(snap, ref pe));
        }
        finally { CloseHandle(snap); }
        return 0;
    }

    internal static bool PidAlive(int pid)
    {
        if (pid <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    internal static DateTime? PidStartTimeUtc(int pid)
    {
        if (pid <= 0) return null;
        try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    /// <summary>Pid + start time is the only safe process identity: a bare pid can be recycled onto a
    /// stranger between the record and the check. The one-second tolerance absorbs the precision a start
    /// time loses on its way through text storage; a pid cannot be reused inside that window.</summary>
    internal static bool PidAlive(int pid, DateTime expectedStartUtc)
    {
        DateTime? actual = PidStartTimeUtc(pid);
        if (actual is null) return false;
        return Math.Abs((actual.Value - expectedStartUtc).TotalSeconds) < 1.0;
    }

    internal static string? ProcessName(int pid)
    {
        if (pid <= 0) return null;
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    internal static void KillTree(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            try { p.Kill(entireProcessTree: true); }
            catch { try { p.Kill(); } catch { } }
        }
        catch { }
    }

    // =================== ports (GetExtendedTcpTable) ===================

    /// <summary>The pid's listening TCP port (IPv4 first, then IPv6) via GetExtendedTcpTable - the managed
    /// TCP APIs expose listeners without owning PIDs, so this needs the iphlpapi table directly.</summary>
    internal static int FindListeningPort(int pid)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        // row offsets follow MIB_TCPROW_OWNER_PID / MIB_TCP6ROW_OWNER_PID (all-DWORD rows, no padding)
        int p = ScanTcpTable(pid, ipVersion: AF_INET, rowSize: 24, portOffset: 8, pidOffset: 20);
        return p != 0 ? p : ScanTcpTable(pid, ipVersion: AF_INET6, rowSize: 56, portOffset: 20, pidOffset: 52);
    }

    internal static int ScanTcpTable(int pid, int ipVersion, int rowSize, int portOffset, int pidOffset)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        int len = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref len, false, ipVersion, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (len <= 0) return 0;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (GetExtendedTcpTable(buf, ref len, false, ipVersion, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return 0;
            int n = Marshal.ReadInt32(buf);
            for (int i = 0; i < n; i++)
            {
                IntPtr row = buf + 4 + i * rowSize;
                if (Marshal.ReadInt32(row, pidOffset) != pid) continue;
                int portRaw = Marshal.ReadInt32(row, portOffset); // port is network-order in the low word
                return ((portRaw & 0xFF) << 8) | ((portRaw >> 8) & 0xFF);
            }
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>The pid of the msmdsrv.exe listening on <paramref name="port"/> (loopback), or 0.</summary>
    internal static int FindMsmdsrvPidOnPort(int port)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        Process[] engines;
        try { engines = Process.GetProcessesByName("msmdsrv"); } catch { return 0; }
        foreach (var e in engines)
        {
            try { if (FindListeningPort(e.Id) == port) return e.Id; } catch { }
        }
        return 0;
    }

    // =================== ownership assertion ===================

    /// <summary>Proves the msmdsrv on <paramref name="port"/> IS <paramref name="launchedPid"/> or descends
    /// from it, so a pipeline can never bind to a Desktop it did not start. Both lookups are injectable so
    /// the walk is provable with no live engine.</summary>
    internal static void AssertPortOwnedByLaunchedPid(int port, int launchedPid,
        Func<int, int>? msmdsrvPidOnPort = null, Func<int, int>? parentOf = null)
    {
        if (!OperatingSystem.IsWindows()) return;   // nothing to assert off-Windows
        var pidOnPort = msmdsrvPidOnPort ?? FindMsmdsrvPidOnPort;
        var parent = parentOf ?? GetParentPid;
        int enginePid = pidOnPort(port);
        if (enginePid == 0) throw new PortOwnershipException($"port {port} has no live msmdsrv owner.");
        if (enginePid == launchedPid) return;       // PbiEngine launches msmdsrv directly
        for (int hop = 0, cur = enginePid; hop < 4; hop++)
        {
            cur = parent(cur);
            if (cur == 0) break;
            if (cur == launchedPid) return;
        }
        throw new PortOwnershipException(
            $"port {port} is owned by msmdsrv pid {enginePid}, which is not the launched pid {launchedPid} nor a "
          + "descendant of it - refusing to bind to a phantom engine.");
    }

    /// <summary>The port file can parse microseconds before the socket enters the LISTENING table, so a
    /// first miss is expected rather than a failure.</summary>
    internal static void AssertPortOwnedByLaunchedPidWithRetry(int port, int launchedPid,
        int attempts = 20, int delayMs = 250,
        Func<int, int>? msmdsrvPidOnPort = null, Func<int, int>? parentOf = null, Action<int>? sleep = null)
    {
        var nap = sleep ?? Thread.Sleep;
        for (int i = 1; ; i++)
        {
            try
            {
                AssertPortOwnedByLaunchedPid(port, launchedPid, msmdsrvPidOnPort, parentOf);
                return;
            }
            catch (PortOwnershipException) when (i < attempts)
            {
                nap(delayMs);
            }
        }
    }

    // =================== windows / input ===================

    internal static bool HasWindow(Process p)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { p.Refresh(); return p.MainWindowHandle != IntPtr.Zero; } catch { return false; }
    }

    internal static bool TitleMatches(Process p, string wantTitle)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { p.Refresh(); return !string.IsNullOrEmpty(wantTitle) && p.MainWindowTitle.Contains(wantTitle, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>The AttachThreadInput + BringWindowToTop + SetForegroundWindow dance from the ps1 - the
    /// only reliable way to steal the foreground so the Ctrl+S lands in Desktop.</summary>
    internal static bool ForceForeground(IntPtr target)
    {
        if (target == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint myThread = GetCurrentThreadId();
        ShowWindow(target, SW_RESTORE);
        if (fgThread != myThread)
        {
            AttachThreadInput(myThread, fgThread, true);
            BringWindowToTop(target);
            SetForegroundWindow(target);
            AttachThreadInput(myThread, fgThread, false);
        }
        else
        {
            BringWindowToTop(target);
            SetForegroundWindow(target);
        }
        return GetForegroundWindow() == target;
    }

    internal static void SendCtrlS()
    {
        if (!OperatingSystem.IsWindows()) return;
        var inputs = new[]
        {
            KeyInput(VK_CONTROL, down: true),
            KeyInput(VK_S, down: true),
            KeyInput(VK_S, down: false),
            KeyInput(VK_CONTROL, down: false),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Foreground the Desktop window and send Ctrl+S until the file's LastWriteTime bumps.
    /// SendInput is GLOBAL - the keystroke lands in whatever window has focus, not in the window that was
    /// asked for - so every dispatch is gated on proof: the attempt fails (and retries within
    /// <paramref name="saveRetries"/>) unless <see cref="ForceForeground"/> succeeded AND
    /// <see cref="GetForegroundWindow"/> still returns the target immediately before the keys go out.
    /// A blind Ctrl+S into somebody else's document silently saves THEIR unsaved state, which is
    /// unrecoverable, so no proof means no keystroke, never best effort.</summary>
    internal static bool SaveViaCtrlS(Process proc, string pbix, int saveRetries)
    {
        if (!OperatingSystem.IsWindows()) return false;
        IntPtr h = IntPtr.Zero;
        for (int i = 0; i < 30 && h == IntPtr.Zero; i++)
        {
            proc.Refresh();
            h = proc.MainWindowHandle;
            if (h == IntPtr.Zero) Thread.Sleep(500);
        }
        if (h == IntPtr.Zero)
        {
            Log($"save: pid {proc.Id} never exposed a main window - refusing to dispatch a blind Ctrl+S ('{pbix}' not saved).");
            return false;
        }
        return SaveViaCtrlSCore(h, saveRetries,
            ForceForeground, GetForegroundWindow, SendCtrlS,
            () => File.GetLastWriteTimeUtc(pbix), Thread.Sleep);
    }

    /// <summary>The save retry loop with every side effect injected, so the no-blind-keystroke guarantee is
    /// provable without a live Desktop: <paramref name="sendKeys"/> fires only after
    /// <paramref name="forceForeground"/> reported success AND <paramref name="foregroundWindow"/> still
    /// returned <paramref name="target"/> after the settle delay. An attempt that cannot prove focus is
    /// spent, logged, and retried - the keystroke is withheld rather than dispatched into an unknown
    /// window.</summary>
    internal static bool SaveViaCtrlSCore(IntPtr target, int saveRetries,
        Func<IntPtr, bool> forceForeground, Func<IntPtr> foregroundWindow, Action sendKeys,
        Func<DateTime> lastWriteUtc, Action<int> sleep)
    {
        if (target == IntPtr.Zero) return false;   // a zero hwnd can never be proven to hold focus
        DateTime m0 = lastWriteUtc();
        bool saved = false;
        for (int a = 1; a <= saveRetries && !saved; a++)
        {
            bool got = false;
            for (int k = 0; k < 10 && !got; k++)
            {
                got = forceForeground(target);
                if (!got) sleep(400);
            }
            if (!got)
            {
                Log($"save: attempt {a}/{saveRetries} never won the foreground (focus lock or vanished window) - Ctrl+S withheld.");
                continue;
            }
            sleep(700);   // let Desktop settle before the keystroke
            if (foregroundWindow() != target)
            {
                Log($"save: attempt {a}/{saveRetries} lost the foreground during the settle delay - Ctrl+S withheld.");
                continue;
            }
            sendKeys();
            for (int w = 0; w < 30 && !saved; w++)
            {
                sleep(1000);
                if (lastWriteUtc() > m0) saved = true;
            }
        }
        return saved;
    }

    /// <summary>
    /// Persist an ALREADY-OPEN Power BI Desktop document to its .pbix by driving Desktop's own File &gt; Save
    /// (foreground + Ctrl+S), located by the loopback <paramref name="port"/> its private msmdsrv engine
    /// listens on - i.e. the port a live model session is connected to. This is the ONLY way to persist a
    /// data-loaded, M-based model back to disk without losing its data: the bundled headless engine cannot
    /// host such a model (it has no Mashup/M engine), so Desktop must do the save.
    ///
    /// The Desktop is resolved ONLY through the engine's parent identity. There is deliberately no fallback
    /// to GetProcessesByName("PBIDesktop") and no "first Desktop with a window" preference: with several
    /// Desktops open, either would dispatch this Ctrl+S into somebody else's document and save THEIR
    /// unsaved state. A wrong-document save is unrecoverable, so an unproven owner is a failure, not a
    /// best effort.
    /// </summary>
    internal static (bool saveDispatched, int desktopPid, string? error) SaveDesktopHostingPort(
        int port, string pbixPath, int saveRetries = 3)
    {
        if (!OperatingSystem.IsWindows()) return (false, 0, "scripted File>Save only runs on Windows.");
        int enginePid = FindMsmdsrvPidOnPort(port);
        if (enginePid == 0) return (false, 0, $"no msmdsrv is listening on port {port} - the model host is gone.");
        int parent = GetParentPid(enginePid);
        if (parent == 0) return (false, 0, $"cannot resolve the Power BI Desktop that owns port {port} (engine pid {enginePid}).");
        Process? p = TryGetProcessById(parent);
        if (p is null || !string.Equals(ProcessName(parent), "PBIDesktop", StringComparison.OrdinalIgnoreCase))
            return (false, parent, $"port {port} is hosted by pid {enginePid} whose parent {parent} is not PBIDesktop.exe.");
        for (int i = 0; i < 30 && !HasWindow(p); i++) Thread.Sleep(500);   // wait for THIS process, never switch to another
        if (!HasWindow(p)) return (false, parent, $"the Desktop that owns port {port} (pid {parent}) has no window yet.");
        string wantTitle = Path.GetFileNameWithoutExtension(pbixPath ?? "");
        if (wantTitle.Length > 0 && !TitleMatches(p, wantTitle))
            return (false, parent, $"the Desktop that owns port {port} (pid {parent}) has '{p.MainWindowTitle}' open, not '{wantTitle}' - refusing to Ctrl+S the wrong document.");
        bool ok = SaveViaCtrlS(p, pbixPath!, saveRetries);
        return (ok, parent, ok ? null
            : "Ctrl+S was dispatched but the .pbix LastWriteTime did not advance - a Save-As dialog may be open, "
            + "the document had no unsaved change, or the open document's path differs from pbixPath.");
    }

    /// <summary>Close Desktop politely, then force-kill it and any orphan msmdsrv children.</summary>
    internal static void CleanupDesktop(Process? proc)
    {
        if (proc is null) return;
        try { proc.CloseMainWindow(); Thread.Sleep(4000); } catch { }
        try { proc.Refresh(); if (!proc.HasExited) proc.Kill(); } catch { }
        if (OperatingSystem.IsWindows())
            foreach (int child in FindChildProcessIds(proc.Id, "msmdsrv.exe"))
                try { Process.GetProcessById(child).Kill(); } catch { }
        proc.Dispose();   // the orphan sweep above reads proc.Id, so this stays last
    }

    private static Process? TryGetProcessById(int pid)
    {
        try { return Process.GetProcessById(pid); } catch { return null; }
    }

    private static INPUT KeyInput(ushort vk, bool down)
    {
        if (!OperatingSystem.IsWindows()) return default;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0u : KEYEVENTF_KEYUP } },
        };
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int SW_RESTORE = 9;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 2;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_S = 0x53;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
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

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}

internal sealed class PortOwnershipException : Exception
{
    internal PortOwnershipException(string message) : base(message) { }
}
