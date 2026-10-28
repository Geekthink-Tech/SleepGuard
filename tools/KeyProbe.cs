// KeyProbe: does GetAsyncKeyState work from this kind of process?
// usage: KeyProbe.exe <logfile> [console|window] [seconds]
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class KeyProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)]
    static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint elapse, IntPtr proc);
    [DllImport("user32.dll")]
    static extern bool GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string name);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint msg; public IntPtr wParam, lParam; public uint time; public int x, y; }

    static string logPath;
    static readonly object gate = new object();
    static readonly StringBuilder sb = new StringBuilder();
    static readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
    static bool cursorKnown;
    static int lastX, lastY;

    static void Emit(string line)
    {
        lock (gate)
        {
            sb.AppendLine(sw.ElapsedMilliseconds + "ms " + line);
            try { System.IO.File.WriteAllText(logPath, sb.ToString()); } catch { }
        }
    }

    static void Poll()
    {
        for (int vk = 1; vk < 255; vk++)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) { Emit("KEY 0x" + vk.ToString("X2")); break; }
        }
        POINT p;
        if (GetCursorPos(out p))
        {
            if (!cursorKnown) { cursorKnown = true; lastX = p.X; lastY = p.Y; }
            else if (p.X != lastX || p.Y != lastY)
            {
                lastX = p.X; lastY = p.Y;
                Emit("CURSOR " + p.X + "," + p.Y);
            }
        }
    }

    [ThreadStatic] static IntPtr win;
    static void WinThread()
    {
        IntPtr inst = GetModuleHandleW(null);
        win = CreateWindowExW(0, "STATIC", "kp", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        Emit("window=0x" + win.ToString("X") + " err=" + Marshal.GetLastWin32Error());
        SetTimer(win, (UIntPtr)1, 20, IntPtr.Zero);
        MSG m;
        while (GetMessageW(out m, IntPtr.Zero, 0, 0))
        {
            if (m.msg == 0x0113) Poll();
            DispatchMessageW(ref m);
        }
    }

    static void Main(string[] args)
    {
        logPath = args.Length > 0 ? args[0] : "keyprobe.log";
        string mode = args.Length > 1 ? args[1] : "console";
        int seconds = args.Length > 2 ? int.Parse(args[2]) : 20;
        Emit("mode=" + mode + " session=" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        if (mode == "window")
        {
            var t = new Thread(WinThread);
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
        while (DateTime.UtcNow < deadline)
        {
            if (mode != "window") Poll();
            Thread.Sleep(20);
        }
        Emit("done");
    }
}
