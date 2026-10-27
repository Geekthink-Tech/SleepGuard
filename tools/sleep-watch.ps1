$ErrorActionPreference = 'Stop'
$log = if ($PSScriptRoot) { Join-Path $PSScriptRoot 'sleep-monitor-log.txt' } else { Join-Path (Get-Location).Path 'sleep-monitor-log.txt' }
$code = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class Mon
{
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const uint RIDI_DEVICENAME = 0x20000007;

    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static HookProc s_mHook;
    private static HookProc s_kHook;
    private static WndProcDelegate s_proc;
    private static IntPtr s_hMH;
    private static IntPtr s_hKH;
    private static int s_llM;
    private static int s_llK;
    private static int s_rawM;
    private static int s_rawK;
    private static string s_lastDev = "";
    private static Dictionary<long, string> s_names = new Dictionary<long, string>();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int index, IntPtr val);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int idHook, HookProc fn, IntPtr inst, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hHook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hHook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint num, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")] private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG m, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO li);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);

    private static string NameOf(IntPtr hDev)
    {
        long k = hDev.ToInt64();
        string cached;
        if (s_names.TryGetValue(k, out cached)) return cached;
        uint size = 0;
        GetRawInputDeviceInfoW(hDev, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        string n;
        if (size == 0) { n = "(未知)"; }
        else
        {
            IntPtr buf = Marshal.AllocHGlobal((int)size * 2);
            try
            {
                n = GetRawInputDeviceInfoW(hDev, RIDI_DEVICENAME, buf, ref size) > 0 ? (Marshal.PtrToStringUni(buf) ?? "(空)") : "(读取失败)";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        s_names[k] = n;
        return n;
    }

    public static void Run(string logPath, int seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        s_mHook = delegate (int code, IntPtr w, IntPtr l) { if (code >= 0) System.Threading.Interlocked.Increment(ref s_llM); return CallNextHookEx(IntPtr.Zero, code, w, l); };
        s_kHook = delegate (int code, IntPtr w, IntPtr l) { if (code >= 0) System.Threading.Interlocked.Increment(ref s_llK); return CallNextHookEx(IntPtr.Zero, code, w, l); };
        s_hMH = SetWindowsHookExW(WH_MOUSE_LL, s_mHook, GetModuleHandleW(null), 0);
        s_hKH = SetWindowsHookExW(WH_KEYBOARD_LL, s_kHook, GetModuleHandleW(null), 0);
        s_proc = delegate (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT)
            {
                uint size = 0;
                uint hdr = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
                GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, hdr);
                if (size > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (GetRawInputData(lParam, RID_INPUT, buf, ref size, hdr) == size)
                        {
                            int type = Marshal.ReadInt32(buf, 0);
                            IntPtr hDev = Marshal.ReadIntPtr(buf, 8);
                            if (type == 0) s_rawM++; else s_rawK++;
                            s_lastDev = NameOf(hDev);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        };
        IntPtr hw = CreateWindowExW(0, "STATIC", "MonWnd", 0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hw != IntPtr.Zero)
        {
            SetWindowLongPtrW(hw, -4, Marshal.GetFunctionPointerForDelegate(s_proc));
            var rid = new RAWINPUTDEVICE[2];
            rid[0] = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = hw };
            rid[1] = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = RIDEV_INPUTSINK, hwndTarget = hw };
            RegisterRawInputDevices(rid, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        int lastRawM = 0, lastRawK = 0, lastLlM = 0, lastLlK = 0;
        double lastSample = -100;
        using (var w = new StreamWriter(logPath, false, new UTF8Encoding(true)))
        {
            w.WriteLine("START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  (采样间隔5秒, 总时长 " + seconds + " 秒)");
            w.Flush();
            MSG msg;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                double t = sw.Elapsed.TotalSeconds;
                if (t - lastSample >= 5)
                {
                    lastSample = t;
                    POINT p;
                    GetCursorPos(out p);
                    LASTINPUTINFO li = new LASTINPUTINFO();
                    li.cbSize = (uint)Marshal.SizeOf(li);
                    GetLastInputInfo(ref li);
                    double idle = (GetTickCount() - li.dwTime) / 1000.0;
                    string line = string.Format("{0:HH:mm:ss} idle={1,7:F1}s cur={2},{3} | raw鼠标+{4} raw键盘+{5} | 低级钩子鼠标+{6} 键盘+{7} | last={8}",
                        DateTime.Now, idle, p.X, p.Y, s_rawM - lastRawM, s_rawK - lastRawK, s_llM - lastLlM, s_llK - lastLlK, s_lastDev);
                    lastRawM = s_rawM; lastRawK = s_rawK; lastLlM = s_llM; lastLlK = s_llK;
                    w.WriteLine(line);
                    w.Flush();
                }
                System.Threading.Thread.Sleep(5);
            }
            w.WriteLine("END " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            w.Flush();
        }
        if (s_hMH != IntPtr.Zero) UnhookWindowsHookEx(s_hMH);
        if (s_hKH != IntPtr.Zero) UnhookWindowsHookEx(s_hKH);
        if (hw != IntPtr.Zero) DestroyWindow(hw);
        var ridRemove = new RAWINPUTDEVICE[2];
        ridRemove[0] = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 1, hwndTarget = IntPtr.Zero };
        ridRemove[1] = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = 1, hwndTarget = IntPtr.Zero };
        RegisterRawInputDevices(ridRemove, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
    }
}
'@
Add-Type -TypeDefinition $code -Language CSharp
[Mon]::Run($log, 900)
