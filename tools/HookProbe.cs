// HookProbe: minimal WH_KEYBOARD_LL / WH_MOUSE_LL probe.
// usage: HookProbe.exe <logfile> [seconds]
// Purpose: decide whether installing low-level hooks kills the process in this
// environment (security software) or whether the SleepGuard crash is our own code.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

class HookProbe
{
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static string logPath;
    private static long events;
    private static HookProc mouseProc, kbdProc;
    private static IntPtr hMouse, hKbd;

    private static void W(string msg)
    {
        try
        {
            File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
        }
        catch { }
    }

    private static IntPtr Kbd(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                events++;
                int vk = Marshal.ReadInt32(lParam);
                W("KBD event=" + events + " wParam=" + wParam.ToInt32() + " vk=0x" + vk.ToString("X2") + " thread=" + GetCurrentThreadId());
            }
        }
        catch (Exception ex) { W("KBD exception: " + ex.GetType().Name + " " + ex.Message); }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static IntPtr Mse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                events++;
                if (events % 50 == 1) W("MOUSE event=" + events + " wParam=" + wParam.ToInt32());
            }
        }
        catch (Exception ex) { W("MOUSE exception: " + ex.GetType().Name + " " + ex.Message); }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: HookProbe.exe <logfile> [seconds]");
            return 2;
        }
        logPath = args[0];
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;
        W("=== HookProbe start pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " seconds=" + seconds + " ===");

        try
        {
            mouseProc = new HookProc(Mse);
            kbdProc = new HookProc(Kbd);
            hMouse = SetWindowsHookExW(14 /*WH_MOUSE_LL*/, mouseProc, IntPtr.Zero, 0);
            hKbd = SetWindowsHookExW(13 /*WH_KEYBOARD_LL*/, kbdProc, IntPtr.Zero, 0);
            W("hooks installed mouse=" + (hMouse != IntPtr.Zero) + " kbd=" + (hKbd != IntPtr.Zero) + " err=" + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            W("install exception: " + ex);
            return 3;
        }

        var t = new Timer();
        t.Interval = 1000;
        t.Tick += delegate
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  tick events=" + events + "\r\n", Encoding.UTF8); }
            catch { }
            seconds--;
            if (seconds <= 0)
            {
                W("=== HookProbe end events=" + events + " ===");
                Application.Exit();
            }
        };
        t.Start();
        Application.Run();
        return 0;
    }
}
