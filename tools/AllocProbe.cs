using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Diagnose whether raw-input delivery works in different launch contexts.
// Usage: RawProbe.exe <logfile> <seconds>
class RawProbe
{
    const int WM_INPUT = 0x00FF;
    const int WM_TIMER = 0x0113;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x10000003;
    const uint RIDI_DEVICENAME = 0x20000007;
    const int UOI_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUT { public RAWINPUTHEADER header; }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(IntPtr h, int idx, StringBuilder buf, int len, out int needed);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint ms, IntPtr f);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string n);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO l);
    [DllImport("kernel32.dll")] static extern void GetSystemTimeAsFileTime(out long ft);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static string LogPath;
    static DateTime T0 = DateTime.UtcNow;
    static long Ms() { return (long)(DateTime.UtcNow - T0).TotalMilliseconds; }
    static object Lock = new object();

    static void Log(string s)
    {
        try { lock (Lock) File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n", new UTF8Encoding(true)); }
        catch { }
    }

    static void ObjName(IntPtr h, string tag)
    {
        try
        {
            int need;
            var sb = new StringBuilder(512);
            bool ok = GetUserObjectInformationW(h, UOI_NAME, sb, 512, out need);
            Log(tag + "=" + (ok ? sb.ToString() : ("<fail err=" + Marshal.GetLastWin32Error() + ">")));
        }
        catch (Exception e) { Log(tag + "=<ex " + e.Message + ">"); }
    }

    static Dictionary<IntPtr, string> names = new Dictionary<IntPtr, string>();

    static string GetDeviceName(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero) return "<synthetic/no-device>";
        string n;
        if (names.TryGetValue(hDevice, out n)) return n;
        try
        {
            uint size = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0) { names[hDevice] = "<unknown>"; return "<unknown>"; }
            IntPtr buf = Marshal.AllocHGlobal((int)size * 2 + 8);
            try
            {
                for (int i = 0; i < (int)size * 2 + 8; i++) Marshal.WriteByte(buf, i, 0);
                uint got = size + 4;
                GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buf, ref got);
                n = Marshal.PtrToStringUni(buf) ?? "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { n = "<ex>"; }
        names[hDevice] = n;
        return n;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint cmd, IntPtr data, ref uint size);

    static int mouseCount, kbCount, hidCount;
    static long lastInputMs;

    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_INPUT)
            {
                uint size = 0;
                GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                if (size > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        uint got = size;
                        if (GetRawInputData(lParam, RID_INPUT, buf, ref got, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER))) == got)
                        {
                            var hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                            string dev = GetDeviceName(hdr.hDevice);
                            if (hdr.dwType == 0) { mouseCount++; if (mouseCount <= 3) Log("raw MOUSE from " + dev); }
                            else if (hdr.dwType == 1) { kbCount++; if (kbCount <= 3) Log("raw KEYBOARD from " + dev); }
                            else { hidCount++; if (hidCount <= 3) Log("raw HID from " + dev); }
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                return DefWindowProcW(hWnd, msg, wParam, lParam);
            }
            if (msg == WM_TIMER)
            {
                var li = new LASTINPUTINFO(); li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                GetLastInputInfo(ref li);
                uint now = (uint)Environment.TickCount;
                uint idleOs = now - li.dwTime;
                Log("tick t=" + (Ms() / 1000) + "s mouse=" + mouseCount + " kb=" + kbCount + " hid=" + hidCount + " osIdle=" + idleOs + "ms");
                return IntPtr.Zero;
            }
        }
        catch (Exception e) { Log("WndProc EX " + e.GetType().Name + ": " + e.Message); }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    static int Main(string[] args)
    {
        LogPath = args.Length > 0 ? args[0] : "AllocProbe.log";
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 15;
        Log("=== AllocProbe start pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " ===");
        try { bool ac = AllocConsole(); IntPtr cw = GetConsoleWindow(); if (cw != IntPtr.Zero) ShowWindow(cw, 0); Log("allocConsole=" + ac + " consoleWnd=" + cw); } catch (Exception ex) { Log("alloc ex " + ex.Message); }
        ObjName(GetProcessWindowStation(), "winSta");
        try { Log("desktop=<skip>"); } catch { }
        var wc = new WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(new WndProcDelegate(WndProc));
        wc.hInstance = GetModuleHandleW(null);
        wc.lpszClassName = "AllocProbeWindowClass";
        ushort atom = RegisterClassExW(ref wc);
        Log("RegisterClassEx atom=" + atom + " err=" + Marshal.GetLastWin32Error());
        IntPtr hwnd = CreateWindowExW(0, "AllocProbeWindowClass", "AllocProbe", 0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Log("hwnd=" + hwnd + " err=" + Marshal.GetLastWin32Error());
        if (hwnd == IntPtr.Zero) return 2;
        string mode = args.Length > 2 ? args[2] : "probe";
        RAWINPUTDEVICE[] devs;
        if (mode == "guard")
        {
            devs = new RAWINPUTDEVICE[5];
            devs[0] = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd };
            devs[1] = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd };
            devs[2] = new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd };
            devs[3] = new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd };
            devs[4] = new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd };
        }
        else
        {
            devs = new RAWINPUTDEVICE[3];
            devs[0] = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd };
            devs[1] = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd };
            devs[2] = new RAWINPUTDEVICE { usUsagePage = 0x0C, usUsage = 1, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd };
        }
        Log("mode=" + mode + " count=" + devs.Length);
        bool ok = RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        Log("RegisterRawInputDevices ok=" + ok + " err=" + Marshal.GetLastWin32Error());
        SetTimer(hwnd, IntPtr.Zero, 2000, IntPtr.Zero);
        MSG m;
        int gm;
        long deadline = Ms() + seconds * 1000L;
        while ((gm = GetMessageW(out m, IntPtr.Zero, 0, 0)) > 0)
        {
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
            if (Ms() >= deadline) break;
        }
        Log("=== done mouse=" + mouseCount + " kb=" + kbCount + " hid=" + hidCount + " ===");
        return 0;
    }
}
