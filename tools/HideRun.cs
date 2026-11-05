// HideRun.exe - start a command line with no console window at all.
//
// Why: on Windows 11 Windows Terminal is the default console host, so every powershell.exe the
// SleepGuard task / launcher / watchdog started opened a Windows Terminal window, even with
// -WindowStyle Hidden. This helper is a GUI-subsystem exe (no console of its own) that calls
// CreateProcess with CREATE_NO_WINDOW + STARTF_USESHOWWINDOW/SW_HIDE, so the child console process
// never gets a visible window.
//
// The task/launcher/watchdog all run hidden through this helper. When the *parent* is a WMI-created
// process (that is how the guard gets a usable SeShutdownPrivilege on this machine - a Task Scheduler
// token has the privilege present but unadjustable, SetSuspendState -> err=1314), the child inherits
// the WMI token, so the privilege is preserved end to end.
//
// usage:
//   HideRun.exe -w <file.txt|file.cmd>     run the first line of that file and WAIT for it
//   HideRun.exe -w <script.ps1> [args...]  run powershell -File <script> [args] and WAIT
//   HideRun.exe <file.txt|file.cmd>        run the first line of that file as the command line
//   HideRun.exe <script.ps1> [args...]     run powershell -File <script> [args]
//   HideRun.exe <exe> [args...]            run the command line as given
//
// -w (wait) matters when HideRun is the ACTION of a scheduled task: Task Scheduler runs the task
// process in a job object with "kill on job close", so a helper that starts the real script and
// exits immediately makes the child die before PowerShell has even initialised (observed
// 2026-11-05: the launcher/agent scripts never wrote a single log line). Waiting keeps the job alive
// for as long as the mapped script runs.
//
// Exit code: 0 started (or the child's code with -w), otherwise 2 (no arguments), 3 (empty command
// file) or the Win32 error.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class HideRun
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
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
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, int dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const int STARTF_USESTDHANDLES = 0x00000100;

    private const uint INFINITE = 0xFFFFFFFF;

    private const int CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_HIDE = 0;

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "hiderun.log");

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + System.Diagnostics.Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine);
        }
        catch { }
    }

    private static int Main(string[] args)
    {
        try
        {
            Log("start args=" + args.Length + (args.Length > 0 ? " first=" + args[0] : ""));
            if (args.Length == 0) return 2;

            bool wait = false;
            int offset = 0;
            if (string.Equals(args[0], "-w", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "--wait", StringComparison.OrdinalIgnoreCase))
            {
                wait = true;
                offset = 1;
            }
            if (args.Length <= offset) return 2;
            string[] rest = new string[args.Length - offset];
            Array.Copy(args, offset, rest, 0, rest.Length);

            string command = BuildCommand(rest);
            Log("command=[" + command + "]" + (wait ? " (waiting)" : ""));
            if (string.IsNullOrEmpty(command)) return 3;

            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.dwFlags = STARTF_USESHOWWINDOW | STARTF_USESTDHANDLES;
            si.wShowWindow = SW_HIDE;

            // A GUI-subsystem parent has no standard handles. powershell.exe exits with code 1
            // before running a single line when it inherits NULL std handles (that is exactly what
            // happened on 2026-11-05: both launcher scripts "ran" and wrote nothing). Hand the child
            // the NUL device on stdin/stdout/stderr instead.
            IntPtr nul = CreateFileW("NUL", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (nul != new IntPtr(-1) && nul != IntPtr.Zero)
            {
                si.hStdInput = nul;
                si.hStdOutput = nul;
                si.hStdError = nul;
                Log("std handles -> NUL");
            }
            else
            {
                si.dwFlags = STARTF_USESHOWWINDOW;
                Log("could not open NUL (err=" + Marshal.GetLastWin32Error() + "); inheriting handles");
            }

            PROCESS_INFORMATION pi;
            StringBuilder cmd = new StringBuilder(command, command.Length + 1);
            bool ok = CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, true,
                CREATE_NO_WINDOW, IntPtr.Zero, null, ref si, out pi);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                Log("CreateProcess failed err=" + err);
                return err;
            }
            if (nul != new IntPtr(-1) && nul != IntPtr.Zero) CloseHandle(nul);

            Log("started pid=" + pi.dwProcessId);
            CloseHandle(pi.hThread);
            if (wait)
            {
                uint result = WaitForSingleObject(pi.hProcess, INFINITE);
                uint code = 0;
                GetExitCodeProcess(pi.hProcess, out code);
                Log("child pid=" + pi.dwProcessId + " exited code=" + code + " wait=" + result);
                CloseHandle(pi.hProcess);
                return 0;
            }
            CloseHandle(pi.hProcess);
            return 0;
        }
        catch (Exception ex)
        {
            Log("exception: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static string BuildCommand(string[] args)
    {
        string first = args[0];
        if (File.Exists(first))
        {
            string ext = Path.GetExtension(first).ToLowerInvariant();
            if (ext == ".txt" || ext == ".cmd")
            {
                string[] lines = File.ReadAllLines(first);
                if (lines.Length == 0 || lines[0].Trim().Length == 0) return null;
                return lines[0];
            }
            if (ext == ".ps1")
            {
                string powershell = Path.Combine(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell"),
                    "v1.0\\powershell.exe");
                StringBuilder b = new StringBuilder();
                b.Append('"').Append(powershell).Append("\" -NoProfile -ExecutionPolicy Bypass -File \"").Append(first).Append('"');
                for (int i = 1; i < args.Length; i++) b.Append(' ').Append(Quote(args[i]));
                return b.ToString();
            }
        }

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Quote(args[i]));
        }
        return sb.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        return value.IndexOf(' ') >= 0 ? "\"" + value + "\"" : value;
    }
}
