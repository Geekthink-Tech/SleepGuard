// SleepGuard - "phantom-input aware" idle watchdog for Windows.
//
// Purpose: some USB HID devices (e.g. a water-block LCD screen driven by HiMOS)
// send input reports several times per second. Windows counts those as user
// activity, so GetLastInputInfo() never reaches the display-off / sleep
// timeouts and the monitor stays on forever.
//
// SleepGuard keeps its own "real input" clock: it only counts mouse, keyboard,
// pen, touch and precision-touchpad raw input that does NOT come from the
// configured phantom device(s). On that clock it turns the monitor off and
// suspends the machine.
//
// CLI:
//   --display <sec>   monitor off after N seconds of real idle   (default 300, 0 = off)
//   --sleep   <sec>   suspend after N seconds of real idle        (default 600, 0 = off)
//   --phantom <s1,s2> device-name substrings to ignore            (default VID_1D6B&PID_0122)
//   --wake    <sec>   SELF-TEST ONLY: arm a resume timer before suspending
//   --log     <path>  log file path
//   --pause-app <a,b> suspend these processes while the screens are off (HiMOS) and
//                     resume them on real input, crash or exit
//   --ddc             OPT-IN: also put the panels into DDC/CI DPMS standby.
//                     WARNING: on some monitors (DisplayPort) DPMS off drops the
//                     link, Windows then loses the display and nothing can wake it
//                     again - the power button is needed. Off by default.
//   --once            log the resolved configuration and keep running (default)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class SleepGuard
{
    // ---- Win32 constants -------------------------------------------------
    private const int WM_INPUT = 0x00FF;
    private const int WM_TIMER = 0x0113;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int SC_MONITORPOWER = 0xF170;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_APMRESUME = 0x0007;

    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_DEVNOTIFY = 0x00002000;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RID_INPUT = 0x10000003;

    private const int HWND_BROADCAST = 0xFFFF;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    // ---- configuration ---------------------------------------------------
    private static int displaySec = 300;
    private static int sleepSec = 600;
    private static int hibernateSec = 0;     // 0 = never hibernate on our own
    private static int wakeSec = 0;          // self-test only: wake from sleep after N s
    private static int hibWakeSec = 0;       // self-test only: wake from hibernate after N s
    private static string[] phantomNames = new string[] { "VID_1D6B&PID_0122" };

    // Audio-session display names that must not count as "media is playing".
    // Remote-desktop / streaming tools keep a render session Active all the time
    // even when nothing is audible (NetEase UU Remote on this machine); counting it would
    // keep the display on forever.
    private static string[] phantomAudioNames = new string[] { "网易UU远程", "UU远程" };

    private static string logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SleepGuard", "sleep-guard.log");

    private static readonly object logLock = new object();
    private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    private static long lastRealMs;
    private static bool displayOff;
    private static bool displayPostponed;   // audio/video is playing -> display-off deferred
    private static long lastAudioMs;        // wall ms of the last tick that saw real audio
    private const int AudioGraceMs = 10000; // keep the display on this long after the last sound
    private const int DisplayRetryMs = 45000; // re-assert monitor-off at most this often while idle
    private static bool audioWasPlaying;    // for start/stop transition logging
    private static bool audioDebug;         // --audio-debug: heartbeat + every sample in the log
    private static long lastHeartbeatMs;
    private static long nextSleepRetryMs;
    private static long nextDisplayRetryMs;  // re-send monitor-off if a phantom input turned it back on
    private static long rawEvents;           // diagnostics: how many WM_INPUT messages we received
    private static bool inputSinceSuspend;
    private static IntPtr hibTimer = IntPtr.Zero;

    // DDC/CI panel power is opt-in: SC_MONITORPOWER alone is always safe, while a DDC
    // DPMS off can power the panel down so hard that the DisplayPort link drops and
    // Windows can no longer send the matching "on" (the user has to press the button).
    private static bool ddcEnabled;          // --ddc enables DDC/CI panel power control

    // --pause-app a,b: while the screens are off, suspend these processes.
    //
    // Why: on this machine HiMOS (the water-block display app) writes a HID
    // consumer-control report about 3x/s through USB\VID_1D6B&PID_0122. Windows sees
    // that as user activity, so a SC_MONITORPOWER blank comes back on immediately and
    // the OS idle timer never ages. Pausing the writer is what makes a plain OS blank
    // stay dark: no DDC, no physical power-off, and both monitors blank at once.
    private static string[] pauseApps = new string[0];
    private static bool appsPaused;
    private static bool testPause;           // --test-pause: exercise pause/resume once at startup
    private static string pauseRequestPath = ""; // --pause-request: where the agent watches
    private static string lastPauseRequest = ""; // last request written (skip duplicates)
    private static bool ddcUnavailable;      // no monitor accepted a DDC write on this box
    private static bool panelDark;           // we asked the panels to go into DPMS standby

    // Real-input detection. Raw input and low-level hooks proved unreliable for
    // this process (installed fine, never fired - even in the interactive
    // session), so the guard polls instead: any cursor movement or any pressed
    // key means "the user is here". Both calls are cheap and cannot be spoofed
    // by the water-cooler HID that only emits HID consumer-control reports.
    private static long lastInputLogMs;
    private static bool cursorKnown;
    private static int lastCursorX;
    private static int lastCursorY;
    private static long pollTicks;           // diagnostics: 100 ms poll timer fired this often
    private static long keyHits;             // diagnostics: GetAsyncKeyState saw a pressed key/button
    private static long inputMsgCount;          // diagnostics: WM_INPUT messages seen
    private static long realInputEvents;       // diagnostics: raw events that were NOT phantom
    private static long fastTicks;           // diagnostics: 20 ms fast-timer ticks

    private static WndProcDelegate wndProcKeepAlive;
    private static Mutex instanceMutex;   // rooted: keeps the singleton mutex alive

    // ---- P/Invoke --------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // CharSet MUST be Unicode here: the class name is a string field inside the
    // struct, and RegisterClassExW reads it as UTF-16. Without it the marshaller
    // hands over ANSI bytes, a garbage class gets registered and
    // CreateWindowExW then fails with ERROR_CANNOT_FIND_WND_CLASS (1407).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // v1.1.2: the low-level hook plumbing was removed. On the reference machine the
    // hooks installed fine but never fired inside the guard, while raw input does work
    // once the guard is built as a console-subsystem binary - see docs\troubleshooting.md.
    // Dead code left behind only invited "why does nothing happen?" debugging sessions.

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex, StringBuilder pvInfo, uint nLength, out uint lpnLengthNeeded);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("powrprof.dll", SetLastError = true)]
    // The Win32 prototype is BOOLEAN SetSuspendState(BOOLEAN, BOOLEAN, BOOLEAN):
    // one-byte values. Declaring the return type as `bool` makes the CLR read the
    // whole 32-bit EAX, whose upper bytes are undefined -> "ok=True" even when the
    // call failed with ERROR_PRIVILEGE_NOT_HELD (1314). Marshal as byte instead.
    private static extern byte SetSuspendState(byte hibernate, byte forceCritical, byte disableWakeEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateWaitableTimerW(IntPtr attributes, bool manualReset, string name);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint coInit);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr arg, bool resume);

    // ---- helpers ---------------------------------------------------------
    private static void Log(string message)
    {
        string line = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss.fff}  {1}",
            DateTime.Now, message);
        try
        {
            lock (logLock)
            {
                string dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                FileInfo fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    string old = logPath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(logPath, old);
                }
                File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(true));
            }
        }
        catch { }
    }

    private static bool EnableShutdownPrivilege()
    {
        return EnablePrivilege("SeShutdownPrivilege");
    }

    // SeDebugPrivilege is what lets us OpenProcess(PROCESS_SUSPEND_RESUME) on the phantom-input
    // writer. Without it the guard gets err=5 (ACCESS_DENIED) even though it runs elevated as the
    // same user, because the target processes are protected by the vendor driver.
    private static bool EnableDebugPrivilege()
    {
        return EnablePrivilege("SeDebugPrivilege");
    }

    private static bool EnablePrivilege(string name)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token)) return false;

            LUID luid;
            if (!LookupPrivilegeValueW(null, name, out luid)) return false;

            TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
            tp.PrivilegeCount = 1;
            tp.Privileges.Luid = luid;
            tp.Privileges.Attributes = SE_PRIVILEGE_ENABLED;

            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static string GetDeviceName(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero) return "<synthetic>";
        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return string.Empty;

        // pcbSize for RIDI_DEVICENAME is a character count. Allocate the buffer
        // with slack, zero it, and pass the character capacity -- an exact-size
        // allocation here was the original heap-corruption bug.
        IntPtr buf = Marshal.AllocHGlobal((int)size * 2 + 16);
        try
        {
            for (int i = 0; i < (int)size * 2 + 16; i++) Marshal.WriteByte(buf, i, 0);
            uint chars = size + 4;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref chars);
            string s = Marshal.PtrToStringUni(buf);
            return s == null ? string.Empty : s;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool IsPhantom(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return false;
        for (int i = 0; i < phantomNames.Length; i++)
        {
            if (deviceName.IndexOf(phantomNames[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // SC_MONITORPOWER only asks Windows to blank its output. Any input event then
    // lights the panel right back up - and this machine has a HID device that fires
    // ~3 reports per second (the HiMOS water-cooler screen), so the OS-level blank
    // was undone almost instantly: the screen went black and came back on a moment
    // later, over and over. A DDC/CI DPMS-off (MCCS VCP 0xD6) is handled by the
    // monitor itself and is immune to input events, so the panel really stays dark
    // until a real user comes back.
    private const byte VCP_POWER = 0xD6;   // 1 = on, 2 = standby, 3 = suspend, 4 = off
    private const uint DPMS_ON = 1;
    private const uint DPMS_OFF = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITOR_RECT { public int left, top, right, bottom; }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref MONITOR_RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr hPhysicalMonitor, byte code, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] monitors);

    private static void TurnOffMonitors()
    {
        IntPtr result;
        SendMessageTimeoutW((IntPtr)HWND_BROADCAST, (uint)WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER,
            (IntPtr)2, SMTO_ABORTIFHUNG, 1500, out result);
        QueueMonitorPower(false);
    }

    // Called when real input arrives: the OS brings its own blank back, but a panel
    // that is in DDC standby has to be asked explicitly.
    private static void WakeMonitors()
    {
        panelDark = false;
        IntPtr result;
        SendMessageTimeoutW((IntPtr)HWND_BROADCAST, (uint)WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER,
            (IntPtr)(-1), SMTO_ABORTIFHUNG, 1500, out result);
        QueueMonitorPower(true);
        TryLog("monitors woken on real input (ddc)");
    }

    private static void QueueMonitorPower(bool on)
    {
        if (!ddcEnabled || ddcUnavailable) return;
        if (!on) panelDark = true;
        ThreadPool.QueueUserWorkItem(delegate { ApplyMonitorPower(on); });
    }

    // Runs off the message loop: a DDC write costs tens of milliseconds per monitor.
    private static void ApplyMonitorPower(bool on)
    {
        if (!ddcEnabled || ddcUnavailable) return;
        try
        {
            int touched = 0, failed = 0;
            MonitorEnumProc cb = delegate(IntPtr hMon, IntPtr hdc, ref MONITOR_RECT rc, IntPtr data)
            {
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0) return true;
                PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, arr)) return true;
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (SetVCPFeature(arr[i].hPhysicalMonitor, VCP_POWER, on ? DPMS_ON : DPMS_OFF)) touched++;
                        else failed++;
                    }
                }
                finally { DestroyPhysicalMonitors(count, arr); }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            if (touched == 0)
            {
                if (failed > 0) ddcUnavailable = true;
                TryLog("ddc: no monitor accepted power=" + (on ? "on" : "off") + " (failed=" + failed + ")");
            }
        }
        catch (Exception ex) { TryLog("ddc error: " + ex.Message); }
    }

    // ---- audio / video playback detection --------------------------------
    // The user's rule: while audio/video is actually playing, POSTPONE only the
    // display-off step. Falling asleep / hibernating still happens on schedule
    // ("play audio and it must still sleep"). CoreAudio session enumeration tells us whether anything
    // currently renders audio on the default playback endpoint.
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, [MarshalAs(UnmanagedType.Interface)] out object session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
    }

    private static IMMDeviceEnumerator audioDeviceEnum;
    private static IMMDevice audioDefaultDevice;

    private static void ResetAudioCache()
    {
        audioDefaultDevice = null;
        audioDeviceEnum = null;
    }

    // Called once per tick; remembers when audio was last actually playing.
    private static void SampleAudio()
    {
        try
        {
            bool playing = IsAudioPlaying();
            if (playing)
            {
                lastAudioMs = WallMs();
                if (!audioWasPlaying)
                {
                    audioWasPlaying = true;
                    Log("audio/video playback started -> display-off will be postponed");
                }
            }
            else if (audioWasPlaying)
            {
                audioWasPlaying = false;
                Log("audio/video playback stopped");
            }
            if (audioDebug) Log("audio-debug: playing=" + playing);
        }
        catch (Exception ex)
        {
            if (audioDebug) TryLog("audio-debug: sample failed: " + ex.Message);
        }
    }

    private static bool IsAudioPlaying()
    {
        try
        {
            if (audioDeviceEnum == null)
                audioDeviceEnum = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            if (audioDefaultDevice == null)
            {
                IMMDevice dev;
                if (audioDeviceEnum.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev == null) return false;
                audioDefaultDevice = dev;
            }

            Guid iid = typeof(IAudioSessionManager2).GUID;
            object mgrObj;
            if (audioDefaultDevice.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out mgrObj) != 0 || mgrObj == null)
            {
                ResetAudioCache();
                return false;
            }

            IAudioSessionManager2 mgr = (IAudioSessionManager2)mgrObj;
            IAudioSessionEnumerator sessions;
            if (mgr.GetSessionEnumerator(out sessions) != 0 || sessions == null)
            {
                ResetAudioCache();
                return false;
            }

            int count;
            if (sessions.GetCount(out count) != 0) count = 0;
            for (int i = 0; i < count; i++)
            {
                object o;
                if (sessions.GetSession(i, out o) != 0 || o == null) continue;
                IAudioSessionControl ctl = o as IAudioSessionControl;
                if (ctl == null) continue;
                int state;
                int hr = ctl.GetState(out state);
                string name = "";
                try { ctl.GetDisplayName(out name); } catch { name = ""; }
                if (name == null) name = "";

                // Remote-desktop / streaming tools keep a render session in the
                // Active state all the time, playing or not (UU Remote does this
                // on this machine). Such sessions are ignored, otherwise the
                // display could never turn off.
                if (IsPhantomAudio(name))
                {
                    if (audioDebug) TryLog("audio-debug: session[" + i + "] state=" + state +
                        " name=" + name + " -> ignored (phantom)");
                    continue;
                }

                if (audioDebug) TryLog("audio-debug: session[" + i + "] hr=" + hr + " state=" + state +
                    " name=" + name);
                // A session with no display name is a leftover/zombie session on
                // this machine's virtual endpoint (observed stuck in Active with
                // an empty name for hours). Only a named session counts as real
                // media playback, otherwise the display would never turn off.
                if (hr == 0 && state == 1 && name.Length > 0) return true;
            }
            return false;
        }
        catch
        {
            ResetAudioCache();
            return false;
        }
    }

    private static bool IsPhantomAudio(string displayName)
    {
        for (int i = 0; i < phantomAudioNames.Length; i++)
        {
            if (displayName.IndexOf(phantomAudioNames[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // Wall clock in ms. Stopwatch/QPC time can stand still across S3/S4, wall
    // time does not, so resume detection must use wall time.
    private static long WallMs()
    {
        return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    }

    private static IntPtr ArmResumeTimer(int seconds)
    {
        IntPtr t = CreateWaitableTimerW(IntPtr.Zero, false, null);
        if (t == IntPtr.Zero) return IntPtr.Zero;
        long due = -10000000L * seconds; // relative, 100ns units
        if (!SetWaitableTimer(t, ref due, 0, IntPtr.Zero, IntPtr.Zero, true))
        {
            CloseHandle(t);
            return IntPtr.Zero;
        }
        return t;
    }

    private static void CancelTimer(ref IntPtr t)
    {
        if (t == IntPtr.Zero) return;
        long cancel = 0;
        SetWaitableTimer(t, ref cancel, 0, IntPtr.Zero, IntPtr.Zero, false);
        CloseHandle(t);
        t = IntPtr.Zero;
    }

    private static void CancelHibTimer() { CancelTimer(ref hibTimer); }

    private static void HibernateNow(string why)
    {
        Log("requesting HIBERNATE (" + why + ")");

        IntPtr autoWake = IntPtr.Zero;
        if (hibWakeSec > 0)
        {
            // self-test only: make sure the machine comes back on its own.
            autoWake = ArmResumeTimer(hibWakeSec);
            Log("selftest: hibernate auto-resume armed=" + (autoWake != IntPtr.Zero) +
                " in " + hibWakeSec + "s");
        }

        long wallBefore = WallMs();
        bool ok = SetSuspendState(1, 0, 0) != 0;
        int err = Marshal.GetLastWin32Error();
        long elapsedMs = WallMs() - wallBefore;

        if (autoWake != IntPtr.Zero) CancelTimer(ref autoWake);

        if (!ok)
        {
            Log("hibernate call FAILED, win32 err=" + err + "; retry in 60s");
            nextSleepRetryMs = clock.ElapsedMilliseconds + 60000;
            return;
        }
        if (elapsedMs < 2000)
        {
            Log("hibernate request returned after " + elapsedMs +
                "ms without hibernating (blocked by a power request?) -> retry in 60s");
            nextSleepRetryMs = clock.ElapsedMilliseconds + 60000;
            return;
        }

        Log("resumed from hibernation after " + (elapsedMs / 1000) + "s; idle clock reset");
        lastRealMs = clock.ElapsedMilliseconds;
        displayOff = false;
        inputSinceSuspend = false;
        nextSleepRetryMs = 0;
    }

    // Returns true when the machine really slept (and the resume was handled), or
    // hibernated; false when the request was refused so the caller can still turn
    // the display off instead of doing nothing.
    private static bool RequestSleep(double idle)
    {
        Log(string.Format(CultureInfo.InvariantCulture,
            "IDLE {0:F0}s >= sleep threshold {1}s -> requesting suspend (session S3)", idle, sleepSec));

        bool hibernateAfterSleep = hibernateSec > 0 && hibernateSec > sleepSec;
        int transitionSec = hibernateAfterSleep ? (hibernateSec - sleepSec) : 0;

        inputSinceSuspend = false;
        if (hibernateAfterSleep)
        {
            // wake ourselves after the remaining idle time and hibernate then
            hibTimer = ArmResumeTimer(transitionSec);
            Log("hibernate-after-sleep armed=" + (hibTimer != IntPtr.Zero) +
                ": will wake in " + transitionSec + "s to hibernate (total idle " + hibernateSec + "s)");
        }

        IntPtr testWake = IntPtr.Zero;
        if (wakeSec > 0)
        {
            testWake = ArmResumeTimer(wakeSec);
            Log("selftest: sleep wake timer armed=" + (testWake != IntPtr.Zero) + " in " + wakeSec + "s");
        }

        long wallBefore = WallMs();
        bool ok = SetSuspendState(0, 0, 0) != 0;
        int err = Marshal.GetLastWin32Error();
        long elapsedMs = WallMs() - wallBefore;

        // A driver holding a "system required" power request (powercfg /requests
    // shows it as "[DRIVER] Legacy Kernel Caller") can make
        // the polite request return TRUE instantly without ever sleeping. Escalate
        // to a forced suspend, which the kernel performs regardless of app/driver
        // vetoes.
        if ((!ok || elapsedMs < 2000) && !inputSinceSuspend)
        {
            Log("polite suspend did not sleep (ok=" + ok + " err=" + err + " after " + elapsedMs +
                "ms) -> retrying with force=true");
            wallBefore = WallMs();
            ok = SetSuspendState(0, 1, 0) != 0;
            err = Marshal.GetLastWin32Error();
            elapsedMs = WallMs() - wallBefore;
        }

        if (testWake != IntPtr.Zero) CancelTimer(ref testWake);

        if (!ok)
        {
            CancelHibTimer();
            Log("suspend call FAILED, win32 err=" + err + "; retry in 60s");
            nextSleepRetryMs = clock.ElapsedMilliseconds + 60000;
            return false;
        }
        if (elapsedMs < 2000)
        {
            // SetSuspendState can return TRUE without the machine ever sleeping
            // (e.g. Windows Update or another service holds a power request).
            CancelHibTimer();
            Log("suspend request returned after " + elapsedMs +
                "ms without sleeping (blocked by a power request?) -> retry in 30s");
            nextSleepRetryMs = clock.ElapsedMilliseconds + 30000;
            return false;
        }

        // We really slept and are back.
        if (hibernateAfterSleep)
        {
            if (elapsedMs >= transitionSec * 1000L - 5000L)
            {
                CancelHibTimer();
                Log("slept " + (elapsedMs / 1000) + "s, hibernate transition time reached" +
                    (inputSinceSuspend ? " (input seen)" : ""));
                HibernateNow("after " + (elapsedMs / 1000) + "s of sleep");
                return true;
            }
            CancelHibTimer();
            Log("woken after " + (elapsedMs / 1000) + "s of sleep before the hibernate transition; " +
                "hibernate cancelled" + (inputSinceSuspend ? " (real input)" : " (no input seen)"));
        }
        else
        {
            Log("resumed from suspend after " + (elapsedMs / 1000) + "s; idle clock reset");
        }

        lastRealMs = clock.ElapsedMilliseconds;
        displayOff = false;
        if (panelDark) WakeMonitors();
        ResumeIdleApps("real input");   // DDC standby survives an S3 cycle
        return true;
    }

    private static uint OsIdleMs()
    {
        LASTINPUTINFO li = new LASTINPUTINFO();
        li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref li)) return 0xFFFFFFFFu;
        return unchecked((uint)Environment.TickCount - li.dwTime);
    }

    private static void OnTick()
    {
        // Real input is detected by polling (see PollCursor/PollKeyboard); raw
        // input and low-level hooks never fire in this process.
        long now = clock.ElapsedMilliseconds;
        double idle = (now - lastRealMs) / 1000.0;

        // Sample audio every second: a 1-second notification "ding" must not
        // postpone the display for the whole idle period, so the display branch
        // only looks at how recently real audio was playing.
        if (displaySec > 0) SampleAudio();

        if (audioDebug && WallMs() - lastHeartbeatMs >= 30000)
        {
            lastHeartbeatMs = WallMs();
            Log(string.Format(CultureInfo.InvariantCulture,
                "heartbeat: idle={0:F0}s displayOff={1} lastAudio={2:F0}s ago rawEvents={3} osIdle={4}ms polls={5} keyHits={6} k58=0x{7:X4}", idle, displayOff,
                (WallMs() - lastAudioMs) / 1000.0, rawEvents, OsIdleMs(), pollTicks, keyHits, (short)GetAsyncKeyState(0x58)));
        }

        // Direct hibernate while still awake (also covers the case where the sleep
        // request was refused and the machine kept running).
        if (hibernateSec > 0 && idle >= hibernateSec && now >= nextSleepRetryMs)
        {
            HibernateNow(string.Format(CultureInfo.InvariantCulture,
                "IDLE {0:F0}s >= hibernate threshold {1}s", idle, hibernateSec));
            return;
        }

        if (sleepSec > 0 && idle >= sleepSec && now >= nextSleepRetryMs)
        {
            // If the machine refuses to sleep (another power request is holding it
            // awake), fall through and at least turn the display off.
            if (RequestSleep(idle)) return;
        }

        if (displaySec > 0 && idle >= displaySec && !displayOff)
        {
            // Exception requested by the user: keep the screen awake while
            // audio/video is playing, but do NOT postpone sleep/hibernate.
            if (WallMs() - lastAudioMs < AudioGraceMs)
            {
                if (!displayPostponed)
                {
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "IDLE {0:F0}s >= display threshold {1}s, but audio/video is playing -> display-off postponed (sleep/hibernate still on schedule)",
                        idle, displaySec));
                    displayPostponed = true;
                }
                return;
            }

            Log(string.Format(CultureInfo.InvariantCulture,
                "IDLE {0:F0}s >= display threshold {1}s -> turning monitors off", idle, displaySec));
            TurnOffMonitors();
            displayOff = true;
            displayPostponed = false;
            nextDisplayRetryMs = now + DisplayRetryMs;
            PauseIdleApps();   // stop the phantom HID writer so the blank stays dark
        }
        else if (displaySec > 0 && idle >= displaySec && displayOff && now >= nextDisplayRetryMs &&
                 WallMs() - lastAudioMs >= AudioGraceMs)
        {
            // The phantom HID can bring the panel back up even though nobody is
            // here. Keep insisting (bounded interval) until real input arrives.
            Log(string.Format(CultureInfo.InvariantCulture,
                "IDLE {0:F0}s and still no real input -> turning monitors off again", idle));
            TurnOffMonitors();
            nextDisplayRetryMs = now + DisplayRetryMs;
        }
    }

    private static string PauseRequestPath()
    {
        if (!string.IsNullOrEmpty(pauseRequestPath)) return pauseRequestPath;
        string dir = Path.GetDirectoryName(logPath);
        if (string.IsNullOrEmpty(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleepGuard");
        return Path.Combine(dir, "pause-request.txt");
    }

    // Write only on a state change, otherwise the 20 ms input tick would rewrite the same
    // request dozens of times per second and spam both logs.
    private static bool WritePauseRequest(string text)
    {
        if (text == lastPauseRequest) return false;
        try
        {
            string p = PauseRequestPath();
            string dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(p, text);
            lastPauseRequest = text;
            return true;
        }
        catch (Exception ex) { TryLog("pause-app: request write failed: " + ex.Message); return false; }
    }

    // Handing the pause/resume to an out-of-process agent
    // ---------------------------------------------------
    // The guard cannot suspend the phantom-input writer itself on this machine: a .NET exe
    // started by Task Scheduler / WMI gets a filtered process view here (102 processes
    // visible while Windows PowerShell in the same session sees 424, so HiMOS is simply not
    // in the list) and OpenProcess(PROCESS_SUSPEND_RESUME) on a pid it is handed returns
    // err=5 even with SeDebugPrivilege enabled. Windows PowerShell in a normal session can do
    // both, so the guard only writes a request file and tests\sleepguard-agent.ps1 performs
    // the actual suspend/resume. Protocol (UTF-8, one word per line):
    //     pause            -> agent suspends the names listed in pause-apps.txt
    //     resume           -> agent resumes whatever it suspended
    private static void PauseIdleApps()
    {
        if (pauseApps.Length == 0 || appsPaused) return;
        appsPaused = true;
        string list = string.Join(",", pauseApps);
        if (WritePauseRequest("pause" + Environment.NewLine + list + Environment.NewLine))
            Log("pause-app: requested pause of [" + list + "] -> " + PauseRequestPath());
    }

    private static void ResumeIdleApps(string why)
    {
        if (!appsPaused) return;
        appsPaused = false;
        if (WritePauseRequest("resume" + Environment.NewLine + "(" + why + ")" + Environment.NewLine))
            Log("pause-app: requested resume (" + why + ") -> " + PauseRequestPath());
    }

    // Raw-input device handle -> "is this the phantom HID?" verdict, plus the
    // resolved name for logging. Filled lazily on first event per handle.
    private static readonly System.Collections.Generic.Dictionary<IntPtr, bool> deviceIsPhantom =
        new System.Collections.Generic.Dictionary<IntPtr, bool>();
    private static readonly System.Collections.Generic.Dictionary<IntPtr, string> deviceNames =
        new System.Collections.Generic.Dictionary<IntPtr, string>();

    private static void OnRealInput(IntPtr hDevice, string source)
    {
        long now = clock.ElapsedMilliseconds;
        lastRealMs = now;
        inputSinceSuspend = true;
        displayPostponed = false;
        CancelHibTimer();   // the user is here, do not hibernate on them
        // Do not log every event: a held key would write the log 10 times a
        // second. One line per minute (or whenever the display was off) is enough.
        if (displayOff || lastInputLogMs == 0 || now - lastInputLogMs > 60000)
        {
            lastInputLogMs = now;
            TryLog("real input detected (" + source + ") -> idle clock reset, display considered awake");
            displayOff = false;
        }

        // A panel in DDC standby does not come back on its own for a key press or a
        // wheel click: the OS only lights the display for input it turns into
        // activity, and we blanked the monitor behind its back.
        if (panelDark) WakeMonitors();
        ResumeIdleApps("real input");
    }

    // Cursor movement is a real user action; the phantom HID never moves the
    // pointer (it only emits HID consumer-control reports).
    private static void PollCursor()
    {
        POINT pt;
        if (!GetCursorPos(out pt)) return;
        if (!cursorKnown)
        {
            cursorKnown = true;
            lastCursorX = pt.X;
            lastCursorY = pt.Y;
            return;
        }
        if (pt.X != lastCursorX || pt.Y != lastCursorY)
        {
            lastCursorX = pt.X;
            lastCursorY = pt.Y;
            OnRealInput(IntPtr.Zero, "cursor " + pt.X + "," + pt.Y);
        }
    }

    // Cheap keyboard scan: the guard's hooks never fire, so a pressed key is
    // detected by sampling GetAsyncKeyState. Runs every 100 ms (the poll timer),
    // which is faster than any human key press, so no keystroke is missed.
    private static void PollKeyboard()
    {
        pollTicks++;
        for (int vk = 1; vk < 255; vk++)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                keyHits++;
                OnRealInput(IntPtr.Zero, "key 0x" + vk.ToString("X2"));
                return;
            }
        }
    }

    private static void TryLog(string message)
    {
        try { Log(message); } catch { }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Any exception escaping a native callback would kill the process, so
        // everything is contained here.
        try
        {
            return WndProcCore(hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            TryLog("WndProc exception: " + ex);
            return IntPtr.Zero;
        }
    }

    private static IntPtr WndProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
            {
                rawEvents++;
                inputMsgCount++;
                if (rawEvents == 1) TryLog("raw input: first WM_INPUT received");
                if (rawEvents == 1) TryLog("raw input: first WM_INPUT received");
                // ROOT CAUSE OF THE CRASHES (fixed in v1.1.1): lParam of WM_INPUT is
                // an HRAWINPUT handle, NOT a pointer to RAWINPUTHEADER. Marshalling it
                // directly dereferenced invalid memory and killed the process with
                // 0xC0000005 inside VCRUNTIME140_CLR0400.dll -- so a monitor could be
                // left blanked with no process alive to restore it. Copy the payload
                // out with GetRawInputData first, exactly like the RawProbe diagnostic.
                uint rawSize = 0;
                GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref rawSize, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                if (rawSize == 0 || rawSize > 65536)
                {
                    TryLog("  EARLY-RETURN bad size=" + rawSize);
                    return DefWindowProcW(hWnd, msg, wParam, lParam);
                }
                IntPtr rawBuf = Marshal.AllocHGlobal((int)rawSize);
                try
                {
                    uint rawGot = rawSize;
                    uint copied = GetRawInputData(lParam, RID_INPUT, rawBuf, ref rawGot, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                    if (copied != rawGot)
                    {
                        TryLog("  EARLY-RETURN copied=" + copied + " got=" + rawGot);
                        return DefWindowProcW(hWnd, msg, wParam, lParam);
                    }
                    RAWINPUTHEADER hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(rawBuf, typeof(RAWINPUTHEADER));
                    // Classify the device once per handle and cache the verdict: the
                    // phantom water-cooler HID fires ~3 events/s, so resolving the
                    // name on every single event would be wasteful.
                    bool phantom;
                    string name;
                    if (!deviceIsPhantom.TryGetValue(hdr.hDevice, out phantom))
                    {
                        name = GetDeviceName(hdr.hDevice);
                        phantom = IsPhantom(name);
                        deviceIsPhantom[hdr.hDevice] = phantom;
                        deviceNames[hdr.hDevice] = name;
                        TryLog("raw device seen: type=" + hdr.dwType + " phantom=" + phantom + " name=" + name);
                    }
                    else
                    {
                        if (!deviceNames.TryGetValue(hdr.hDevice, out name) || name == null) name = hdr.hDevice.ToString();
                    }
                    if (!phantom)
                    {
                        realInputEvents++;
                        if (realInputEvents <= 8)
                            TryLog("real raw input #" + realInputEvents + " type=" + hdr.dwType + " name=" + name);
                        string kind = hdr.dwType == 0 ? "mouse" : (hdr.dwType == 1 ? "keyboard" : "hid");
                        OnRealInput(hdr.hDevice, kind + " " + name);
                    }
                }
                finally { Marshal.FreeHGlobal(rawBuf); }
                // Docs: WM_INPUT must reach DefWindowProc or the system never
                // frees the raw-input buffer.
                return DefWindowProcW(hWnd, msg, wParam, lParam);
            }
            case WM_TIMER:
                if (wParam == (IntPtr)2)
                {
                    // 20 ms fast tick: cursor polling, which is always available,
                    // plus the GetAsyncKeyState safety net every 25th tick (500 ms).
                    PollCursor();
                    if (++fastTicks % 25 == 0) PollKeyboard();
                }
                else OnTick();                                              // 1 s: policy tick
                return IntPtr.Zero;
            case WM_POWERBROADCAST:
                if ((int)wParam == PBT_APMRESUMEAUTOMATIC || (int)wParam == PBT_APMRESUME)
                {
                    lastRealMs = clock.ElapsedMilliseconds;
                    displayOff = false;
                    Log("power notification: system resumed -> idle clock reset");
                }
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static bool ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string v = (i + 1 < args.Length) ? args[i + 1] : null;
            switch (a)
            {
                case "--display":
                    if (!int.TryParse(v, out displaySec)) { Log("bad --display value: " + v); return false; }
                    i++; break;
                case "--sleep":
                    if (!int.TryParse(v, out sleepSec)) { Log("bad --sleep value: " + v); return false; }
                    i++; break;
                case "--hibernate":
                    if (!int.TryParse(v, out hibernateSec)) { Log("bad --hibernate value: " + v); return false; }
                    i++; break;
                case "--hib-wake":
                    if (!int.TryParse(v, out hibWakeSec)) { Log("bad --hib-wake value: " + v); return false; }
                    i++; break;
                case "--wake":
                    if (!int.TryParse(v, out wakeSec)) { Log("bad --wake value: " + v); return false; }
                    i++; break;
                case "--phantom":
                    if (v == null) return false;
                    List<string> list = new List<string>();
                    foreach (string p in v.Split(','))
                    {
                        string t = p.Trim();
                        if (t.Length > 0) list.Add(t);
                    }
                    if (list.Count > 0) phantomNames = list.ToArray();
                    i++; break;
                case "--phantom-audio":
                    if (v == null) return false;
                    List<string> alist = new List<string>();
                    foreach (string p in v.Split(','))
                    {
                        string t = p.Trim();
                        if (t.Length > 0) alist.Add(t);
                    }
                    if (alist.Count > 0) phantomAudioNames = alist.ToArray();
                    i++; break;
                case "--log":
                    if (v == null) return false;
                    logPath = v; i++; break;
                case "--audio-debug":
                    audioDebug = true; break;
                case "--ddc":
                    ddcEnabled = true; break;
                case "--pause-app":
                    if (v == null) return false;
                    {
                        List<string> plist = new List<string>();
                        foreach (string p in v.Split(','))
                        {
                            string t = p.Trim();
                            if (t.Length > 0) plist.Add(t);
                        }
                        pauseApps = plist.ToArray();
                    }
                    i++; break;
                case "--pause-request":
                    if (v == null) return false;
                    pauseRequestPath = v; i++; break;
                case "--test-pause":
                    testPause = true;
                    break;
                default:
                    Log("unknown argument ignored: " + a);
                    break;
            }
        }
        return true;
    }

    private static int Main(string[] args)
    {
        // CoreAudio COM (used to detect audio/video playback) needs an initialized
        // apartment on this thread. Ignore the failure code: S_FALSE/RPC_E_CHANGED_MODE
        // both mean COM is already usable and MTA is fine for MMDeviceEnumerator.
        CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);

        // This binary is built with /target:exe on purpose. Measured on this machine:
        // the identical probe code receives mouse+keyboard WM_INPUT when built as a
        // console app and receives NOTHING when built /target:winexe (AllocConsole at
        // runtime does not help either). Since raw input is the only reliable input
        // channel here, we ship the console-subsystem flavour and hide its window.
        try
        {
            IntPtr conWnd = GetConsoleWindow();
            if (conWnd != IntPtr.Zero) ShowWindow(conWnd, 0 /* SW_HIDE */);
            Log("console window hidden=" + (conWnd != IntPtr.Zero));
        }
        catch { }

        // Keep a crash on record: an unhandled exception would otherwise leave
        // only a WER entry behind.
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
        {
            TryLog("UNHANDLED EXCEPTION: " + e.ExceptionObject);
            ApplyMonitorPower(true);   // never leave the panels dark behind a dead guard
            ResumeIdleApps("crash");   // and never leave the water-block app frozen
        };

        bool isNew;
        instanceMutex = new Mutex(true, "Local\\SleepGuard.Instance", out isNew);
        if (!isNew)
        {
            Log("another SleepGuard instance is already running; exiting");
            return 0;
        }

        if (!ParseArgs(args))
        {
            Log("invalid arguments; exiting");
            return 2;
        }

        Log("==================== SleepGuard start ====================");
        Log("config: display=" + displaySec + "s sleep=" + sleepSec + "s hibernate=" + hibernateSec +
            "s wake=" + wakeSec + "s hib-wake=" + hibWakeSec + "s phantom=[" +
            string.Join(",", phantomNames) + "] phantom-audio=[" +
            string.Join(",", phantomAudioNames) + "] pause-app=[" +
            string.Join(",", pauseApps) + "] log=" + logPath);
        Log("SeShutdownPrivilege enabled=" + EnableShutdownPrivilege());
        Log("SeDebugPrivilege enabled=" + EnableDebugPrivilege());
        Log("monitor power control: ddc=" + ddcEnabled);
        if (ddcEnabled) ApplyMonitorPower(true);   // a previous run may have died with the panels dark

        string className = "SleepGuardWindowClass";
        wndProcKeepAlive = new WndProcDelegate(WndProc);

        WNDCLASSEX wc = new WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.style = 0;
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcKeepAlive);
        wc.hInstance = GetModuleHandleW(null);
        wc.lpszClassName = className;

        if (RegisterClassExW(ref wc) == 0)
        {
            Log("RegisterClassExW failed, err=" + Marshal.GetLastWin32Error());
            return 3;
        }

        IntPtr hwnd = CreateWindowExW(0, className, "SleepGuard", 0x80000000u,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Log("CreateWindowExW failed, err=" + Marshal.GetLastWin32Error());
            return 4;
        }

        RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // mouse
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // keyboard
            // v1.1.2: consumer control is deliberately NOT registered. The water-cooler
            // HID lives in that usage page and its 1057-byte reports drown the queue;
            // not registering it keeps the phantom completely invisible to us.
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // pen / digitizer
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // touch screen
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // precision touchpad
        };

        bool regOk = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        Log("RegisterRawInputDevices ok=" + regOk + (regOk ? "" : " err=" + Marshal.GetLastWin32Error()));

        // Diagnostics: what does the OS think is registered, and on which desktop
        // is this window? Raw input in the background (RIDEV_INPUTSINK) is
        // desktop-scoped, so a process whose window ends up on a different window
        // station silently receives nothing.
        try
        {
            IntPtr sta = GetProcessWindowStation();
            StringBuilder staName = new StringBuilder(256);
            uint need;
            GetUserObjectInformationW(sta, 2, staName, 512, out need);
            IntPtr desk = GetThreadDesktop(GetCurrentThreadId());
            StringBuilder deskName = new StringBuilder(256);
            uint need2;
            GetUserObjectInformationW(desk, 2, deskName, 512, out need2);
            uint sid;
            ProcessIdToSessionId((uint)System.Diagnostics.Process.GetCurrentProcess().Id, out sid);
            Log("window station=" + staName + " desktop=" + deskName + " session=" + sid +
                " hwnd=0x" + hwnd.ToInt64().ToString("X"));

            uint count = 0;
            GetRegisteredRawInputDevices(null, ref count, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            if (count > 0 && count < 64)
            {
                RAWINPUTDEVICE[] reg = new RAWINPUTDEVICE[count];
                uint count2 = count;
                uint got = GetRegisteredRawInputDevices(reg, ref count2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                StringBuilder sb = new StringBuilder("registered raw devices got=" + got + " err=" + Marshal.GetLastWin32Error() + ":");
                for (int k = 0; k < got && k < reg.Length; k++)
                    sb.Append(" " + reg[k].usUsagePage.ToString("X2") + "/" + reg[k].usUsage.ToString("X2") +
                              "@0x" + reg[k].hwndTarget.ToInt64().ToString("X"));
                Log(sb.ToString());
            }
            else
            {
                Log("GetRegisteredRawInputDevices count=" + count + " err=" + Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex) { TryLog("registration probe failed: " + ex.Message); }

        // v1.1.0: low-level hooks are NOT installed any more. On this machine they
        // either never fired or delivered one synthetic event and then took the
        // process down (exit 0xC0000005) -- which is exactly how the screen got
        // blanked with nobody left alive to turn it back on. Raw input, registered
        // above, is the primary input source; cursor polling stays as a fallback
        // because GetCursorPos always works.
        Log("low-level hooks: disabled by design (v1.1.2); raw input + cursor polling only");

        SetTimer(hwnd, (IntPtr)1, 1000, IntPtr.Zero);   // policy tick
        SetTimer(hwnd, (IntPtr)2, 20, IntPtr.Zero);     // real-input poll (keyboard)

        lastRealMs = clock.ElapsedMilliseconds;
        lastAudioMs = WallMs();
        displayOff = false;
        ResumeIdleApps("startup, previous guard died while paused");
        Log("ready v1.2.0 (console-subsystem build): raw input (mouse/keyboard/consumer/pen/touch) + cursor polling; hooks disabled; phantom devices ignored; ddc=" + ddcEnabled);

        if (testPause)
        {
            Log("test-pause: pausing [" + string.Join(",", pauseApps) + "] for 2s");
            PauseIdleApps();
            System.Threading.Thread.Sleep(2000);
            ResumeIdleApps("test-pause");
            Log("test-pause: done");
        }

        MSG msg;
        int gm;
        while ((gm = GetMessageW(out msg, IntPtr.Zero, 0, 0)) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        if (gm == -1) TryLog("GetMessageW failed, err=" + Marshal.GetLastWin32Error());

        Log("message loop ended; exiting");
        ApplyMonitorPower(true);   // never leave the panels dark behind a dead guard
        ResumeIdleApps("exit");
        if (instanceMutex != null) instanceMutex.ReleaseMutex();
        return 0;
    }
}
