$ErrorActionPreference = 'Continue'
$log = if ($PSScriptRoot) { Join-Path $PSScriptRoot 'quiet-probe-log.txt' } else { Join-Path (Get-Location).Path 'quiet-probe-log.txt' }
$code = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class QuietProbe
{
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;

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
    private static Dictionary<string, int> s_ev = new Dictionary<string, int>();
    private static List<string> s_first = new List<string>();
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
            try { n = GetRawInputDeviceInfoW(hDev, RIDI_DEVICENAME, buf, ref size) > 0 ? (Marshal.PtrToStringUni(buf) ?? "(空)") : "(读取失败)"; }
            finally { Marshal.FreeHGlobal(buf); }
        }
        s_names[k] = n;
        return n;
    }

    public static void Run(string logPath, int seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lines = new List<string>();
        int errHookM = 0, errHookK = 0, errRaw = 0;
        s_mHook = delegate (int code, IntPtr w, IntPtr l) { if (code >= 0) System.Threading.Interlocked.Increment(ref s_llM); return CallNextHookEx(IntPtr.Zero, code, w, l); };
        s_kHook = delegate (int code, IntPtr w, IntPtr l) { if (code >= 0) System.Threading.Interlocked.Increment(ref s_llK); return CallNextHookEx(IntPtr.Zero, code, w, l); };
        s_hMH = SetWindowsHookExW(WH_MOUSE_LL, s_mHook, GetModuleHandleW(null), 0);
        if (s_hMH == IntPtr.Zero) errHookM = Marshal.GetLastWin32Error();
        s_hKH = SetWindowsHookExW(WH_KEYBOARD_LL, s_kHook, GetModuleHandleW(null), 0);
        if (s_hKH == IntPtr.Zero) errHookK = Marshal.GetLastWin32Error();
        s_proc = delegate (IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            if (uMsg == WM_INPUT)
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
                            string nm = NameOf(hDev);
                            string typeName = type == 0 ? "鼠标" : (type == 1 ? "键盘" : "HID其他");
                            string key = typeName + " | " + nm;
                            int c; s_ev.TryGetValue(key, out c); s_ev[key] = c + 1;
                            if (s_first.Count < 40)
                            {
                                int off = Marshal.SizeOf(typeof(RAWINPUTHEADER));
                                string detail = "";
                                if (type == 0 && size >= off + 20) detail = string.Format(" dx={0} dy={1}", Marshal.ReadInt32(buf, off + 12), Marshal.ReadInt32(buf, off + 16));
                                if (type == 1 && size >= off + 16) detail = string.Format(" vkey=0x{0:X2}", Marshal.ReadInt16(buf, off + 6));
                                s_first.Add(string.Format("{0:HH:mm:ss.fff} {1}{2}", DateTime.Now, key, detail));
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, uMsg, wParam, lParam);
        };
        IntPtr hw = CreateWindowExW(0, "STATIC", "QuietWnd", 0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hw != IntPtr.Zero)
        {
            SetWindowLongPtrW(hw, -4, Marshal.GetFunctionPointerForDelegate(s_proc));
            var rid = new RAWINPUTDEVICE[7];
            rid[0] = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = hw };      // mouse
            rid[1] = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = RIDEV_INPUTSINK, hwndTarget = hw };      // keyboard
            rid[2] = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 5, dwFlags = RIDEV_INPUTSINK, hwndTarget = hw };      // gamepad
            rid[3] = new RAWINPUTDEVICE { usUsagePage = 0x0C, usUsage = 1, dwFlags = RIDEV_INPUTSINK, hwndTarget = hw };   // consumer control
            rid[4] = new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = hw };   // pen
            rid[5] = new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 4, dwFlags = RIDEV_INPUTSINK, hwndTarget = hw };   // touch screen
            rid[6] = new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 5, dwFlags = RIDEV_INPUTSINK, hwndTarget = hw };   // precision touchpad
            if (!RegisterRawInputDevices(rid, 7, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) errRaw = Marshal.GetLastWin32Error();
        }
        else { errRaw = Marshal.GetLastWin32Error(); }

        var idleSeries = new List<string>();
        double nextSample = 1;
        MSG msg;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
            if (sw.Elapsed.TotalSeconds >= nextSample)
            {
                nextSample += 1;
                POINT p; GetCursorPos(out p);
                LASTINPUTINFO li = new LASTINPUTINFO(); li.cbSize = (uint)Marshal.SizeOf(li); GetLastInputInfo(ref li);
                double idle = (GetTickCount() - li.dwTime) / 1000.0;
                idleSeries.Add(string.Format("{0:HH:mm:ss} idle={1:F1}s cur={2},{3}", DateTime.Now, idle, p.X, p.Y));
            }
            System.Threading.Thread.Sleep(5);
        }
        if (s_hMH != IntPtr.Zero) UnhookWindowsHookEx(s_hMH);
        if (s_hKH != IntPtr.Zero) UnhookWindowsHookEx(s_hKH);
        if (hw != IntPtr.Zero) DestroyWindow(hw);

        lines.Add(string.Format("窗口创建={0} 钩子鼠标={1}(err={2}) 钩子键盘={3}(err={4}) 原始输入注册错误={5}",
            hw != IntPtr.Zero, s_hMH != IntPtr.Zero, errHookM, s_hKH != IntPtr.Zero, errHookK, errRaw));
        lines.Add("低级钩子计数: 鼠标=" + s_llM + " 键盘=" + s_llK);
        lines.Add("---- 每秒 idle ----");
        lines.AddRange(idleSeries);
        lines.Add("---- 原始输入按设备统计 ----");
        if (s_ev.Count == 0) lines.Add("(无任何原始输入事件)");
        foreach (var kv in s_ev) lines.Add(string.Format("{0} 事件={1}", kv.Key, kv.Value));
        lines.Add("---- 前40条事件明细 ----");
        lines.AddRange(s_first);
        File.WriteAllLines(logPath, lines.ToArray(), new UTF8Encoding(true));
        foreach (var l in lines) Console.WriteLine(l);
    }
}
'@
Add-Type -TypeDefinition $code -Language CSharp
[QuietProbe]::Run($log, 25)
