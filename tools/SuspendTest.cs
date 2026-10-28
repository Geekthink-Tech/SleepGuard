using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Diagnostic: does SetSuspendState actually suspend from a scheduled-task context?
// Logs token privilege state, return value (marshalled as BOOLEAN/U1), last error,
// and how long the caller was away (wall clock), so a real suspend is provable.
internal static class SuspendTest
{
    private static string logPath = null;

    private static void L(string m)
    {
        try
        {
            File.AppendAllText(logPath,
                string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}\r\n", DateTime.Now, m),
                new UTF8Encoding(true));
        }
        catch { }
    }

    private static long WallMs() { return DateTime.UtcNow.Ticks / 10000L; }

    // BOOLEAN return: marshal as U1, otherwise the upper bytes of the register are garbage.
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern byte SetSuspendState(byte hibernate, byte forceCritical, byte disableWakeEvent);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr hProcess, uint desiredAccess, ref IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string systemName, string name, ref long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll, ref TokPriv1Luid newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimer(IntPtr attr, bool manualReset, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr handle, ref long dueTime, int period,
        IntPtr routine, IntPtr param, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelWaitableTimer(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TokPriv1Luid
    {
        public int Count;
        public long Luid;
        public int Attr;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int infoClass, IntPtr info, int len, out int returnLen);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeName(string systemName, ref long luid, StringBuilder name, ref int nameLen);

    private static string PrivilegeName(long luid)
    {
        StringBuilder sb = new StringBuilder(256);
        int len = sb.Capacity;
        if (LookupPrivilegeName(null, ref luid, sb, ref len)) return sb.ToString();
        return "?(err=" + Marshal.GetLastWin32Error() + ")";
    }

    private static void DumpToken(string tag)
    {
        IntPtr h = IntPtr.Zero;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, ref h))
        {
            L(tag + ": OpenProcessToken failed err=" + Marshal.GetLastWin32Error());
            return;
        }
        // TokenElevation = 20
        int ret = 0;
        IntPtr elevBuf = Marshal.AllocHGlobal(4);
        if (GetTokenInformation(h, 20, elevBuf, 4, out ret))
            L(tag + ": elevated=" + Marshal.ReadInt32(elevBuf));
        else
            L(tag + ": TokenElevation failed err=" + Marshal.GetLastWin32Error());
        Marshal.FreeHGlobal(elevBuf);

        // TokenPrivileges = 3
        int len = 0;
        GetTokenInformation(h, 3, IntPtr.Zero, 0, out len);
        IntPtr buf = Marshal.AllocHGlobal(len);
        if (GetTokenInformation(h, 3, buf, len, out len))
        {
            int count = Marshal.ReadInt32(buf);
            L(tag + ": privileges=" + count);
            for (int i = 0; i < count; i++)
            {
                IntPtr p = (IntPtr)(buf.ToInt64() + 4 + i * 12);
                long luid = Marshal.ReadInt64(p);
                int attr = Marshal.ReadInt32((IntPtr)(p.ToInt64() + 8));
                string name = PrivilegeName(luid);
                if (name.IndexOf("Shutdown", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Tcb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("SystemEnvironment", StringComparison.OrdinalIgnoreCase) >= 0)
                    L(tag + ":   " + name + " attr=0x" + attr.ToString("X"));
            }
        }
        else
        {
            L(tag + ": TokenPrivileges failed err=" + Marshal.GetLastWin32Error());
        }
        Marshal.FreeHGlobal(buf);
        CloseHandle(h);
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const int SePrivilegeEnabled = 0x00000002;

    private static bool EnablePriv(string name)
    {
        IntPtr h = IntPtr.Zero;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, ref h))
        {
            L("OpenProcessToken failed err=" + Marshal.GetLastWin32Error());
            return false;
        }
        long luid = 0;
        if (!LookupPrivilegeValue(null, name, ref luid))
        {
            L("LookupPrivilegeValue failed err=" + Marshal.GetLastWin32Error());
            return false;
        }
        TokPriv1Luid tp = new TokPriv1Luid();
        tp.Count = 1;
        tp.Luid = luid;
        tp.Attr = SePrivilegeEnabled;
        bool ok = AdjustTokenPrivileges(h, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        CloseHandle(h);
        L(name + " enable=" + ok + " err=" + err);
        return ok && err == 0;
    }

    private static int Main(string[] args)
    {
        logPath = args.Length > 0 && args[0].Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "suspend-test.log");

        L("start pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
        DumpToken("before");
        L("SeShutdownPrivilege before: enable attempt...");
        bool priv = EnablePriv("SeShutdownPrivilege");
        L("priv ok=" + priv);
        DumpToken("after");

        // Mimic the guard: arm a long "resume" timer first, exactly as Hibernate-after-sleep does.
        IntPtr hibTimer = CreateWaitableTimer(IntPtr.Zero, false, null);
        long hibDue = -1200L * 10000000L;
        bool hibArmed = hibTimer != IntPtr.Zero && SetWaitableTimer(hibTimer, ref hibDue, 0, IntPtr.Zero, IntPtr.Zero, true);
        L("hib timer armed=" + hibArmed + " err=" + Marshal.GetLastWin32Error());

        int wakeSec = 15;
        if (args.Length > 1) { int.TryParse(args[1], out wakeSec); if (wakeSec <= 0) wakeSec = 15; }

        // Self-wake so the test cannot strand the machine.
        IntPtr wakeTimer = CreateWaitableTimer(IntPtr.Zero, false, null);
        long wakeDue = -(long)wakeSec * 10000000L;
        bool wakeArmed = wakeTimer != IntPtr.Zero && SetWaitableTimer(wakeTimer, ref wakeDue, 0, IntPtr.Zero, IntPtr.Zero, true);
        L("wake timer " + wakeSec + "s armed=" + wakeArmed + " err=" + Marshal.GetLastWin32Error());

        long w0 = WallMs();
        L("calling SetSuspendState(0,0,0) ...");
        byte ret = SetSuspendState(0, 0, 0);
        int err2 = Marshal.GetLastWin32Error();
        long e1 = WallMs();
        L("returned ret=" + ret + " err=" + err2 + " apiElapsedMs=" + (e1 - w0));

        Thread.Sleep(6000);
        long e2 = WallMs();
        long total = e2 - w0;
        L("6s later: wallMs=" + total + " stayedSuspendedMs=" + (total - 6000) + " -> actuallySlept=" + (total > 20000));

        if (wakeTimer != IntPtr.Zero) { CancelWaitableTimer(wakeTimer); CloseHandle(wakeTimer); }
        if (hibTimer != IntPtr.Zero) { CancelWaitableTimer(hibTimer); CloseHandle(hibTimer); }
        L("done");
        return 0;
    }
}
