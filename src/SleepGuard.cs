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
    // even when nothing is audible (网易UU远程 on this machine); counting it would
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
    private static bool audioWasPlaying;    // for start/stop transition logging
    private static bool audioDebug;         // --audio-debug: heartbeat + every sample in the log
    private static long lastHeartbeatMs;
    private static long nextSleepRetryMs;
    private static bool inputSinceSuspend;
    private static IntPtr hibTimer = IntPtr.Zero;

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

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
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token)) return false;

            LUID luid;
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out luid)) return false;

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
        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return string.Empty;

        // The size reported for RIDI_DEVICENAME may or may not include the
        // terminating NUL depending on the driver, so the buffer gets extra
        // slack and is zeroed. An exact-size allocation used to put a 2-byte
        // terminator write past the block, which eventually corrupted the heap
        // and crashed inside VCRUNTIME140_CLR0400.dll.
        const int slack = 16;
        int bytes = ((int)size + slack) * 2;
        IntPtr buf = Marshal.AllocHGlobal(bytes);
        try
        {
            for (int i = 0; i < bytes; i += 2) Marshal.WriteInt16(buf, i, 0);  // cheap, one-time per device
            uint chars = (uint)size + slack;
            uint got = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref chars);
            if (got == 0) return string.Empty;
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

    private static void TurnOffMonitors()
    {
        IntPtr result;
        SendMessageTimeoutW((IntPtr)HWND_BROADCAST, (uint)WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER,
            (IntPtr)2, SMTO_ABORTIFHUNG, 1500, out result);
    }

    // ---- audio / video playback detection --------------------------------
    // The user's rule: while audio/video is actually playing, POSTPONE only the
    // display-off step. Falling asleep / hibernating still happens on schedule
    // ("有音频也照睡"). CoreAudio session enumeration tells us whether anything
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
                // Active state all the time, playing or not (网易UU远程 does this
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
                if (hr == 0 && state == 1) return true;   // AudioSessionStateActive
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
        bool ok = SetSuspendState(true, false, false);
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
        bool ok = SetSuspendState(false, false, false);
        int err = Marshal.GetLastWin32Error();
        long elapsedMs = WallMs() - wallBefore;

        // A driver holding a "system required" power request (powercfg /requests
        // shows it as "[DRIVER] 旧的内核调用程序" / "Legacy Kernel Caller") can make
        // the polite request return TRUE instantly without ever sleeping. Escalate
        // to a forced suspend, which the kernel performs regardless of app/driver
        // vetoes.
        if ((!ok || elapsedMs < 2000) && !inputSinceSuspend)
        {
            Log("polite suspend did not sleep (ok=" + ok + " err=" + err + " after " + elapsedMs +
                "ms) -> retrying with force=true");
            wallBefore = WallMs();
            ok = SetSuspendState(false, true, false);
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
        return true;
    }

    private static void OnTick()
    {
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
                "heartbeat: idle={0:F0}s displayOff={1} lastAudio={2:F0}s ago", idle, displayOff,
                (WallMs() - lastAudioMs) / 1000.0));
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
        }
    }

    // Raw-input device handle -> "is this the phantom HID?" verdict, plus the
    // resolved name for logging. Filled lazily on first event per handle.
    private static readonly System.Collections.Generic.Dictionary<IntPtr, bool> deviceIsPhantom =
        new System.Collections.Generic.Dictionary<IntPtr, bool>();
    private static readonly System.Collections.Generic.Dictionary<IntPtr, string> deviceNames =
        new System.Collections.Generic.Dictionary<IntPtr, string>();

    private static void OnRealInput(IntPtr hDevice)
    {
        lastRealMs = clock.ElapsedMilliseconds;
        inputSinceSuspend = true;
        displayPostponed = false;
        CancelHibTimer();   // the user is here, do not hibernate on them
        if (displayOff)
        {
            string dev;
            if (!deviceNames.TryGetValue(hDevice, out dev)) dev = hDevice.ToString();
            TryLog("real input detected (" + dev + ") -> display considered awake");
            displayOff = false;
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
                RAWINPUTHEADER hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(lParam, typeof(RAWINPUTHEADER));
                // Classify the device once per handle and cache the verdict: the
                // phantom water-cooler HID fires ~3 events/s, so resolving the
                // name on every single event would be wasteful (and it was the
                // path that corrupted the heap).
                bool phantom;
                if (!deviceIsPhantom.TryGetValue(hdr.hDevice, out phantom))
                {
                    string name = GetDeviceName(hdr.hDevice);
                    phantom = IsPhantom(name);
                    deviceIsPhantom[hdr.hDevice] = phantom;
                    if (!deviceNames.ContainsKey(hdr.hDevice)) deviceNames[hdr.hDevice] = name;
                }
                if (!phantom) OnRealInput(hdr.hDevice);
                // Docs: WM_INPUT must reach DefWindowProc or the system never
                // frees the raw-input buffer.
                return DefWindowProcW(hWnd, msg, wParam, lParam);
            }
            case WM_TIMER:
                OnTick();
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

        // Keep a crash on record: an unhandled exception would otherwise leave
        // only a WER entry behind.
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
        {
            TryLog("UNHANDLED EXCEPTION: " + e.ExceptionObject);
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
            string.Join(",", phantomAudioNames) + "] log=" + logPath);
        Log("SeShutdownPrivilege enabled=" + EnableShutdownPrivilege());

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
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // pen / digitizer
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // touch screen
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // precision touchpad
        };

        bool regOk = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        Log("RegisterRawInputDevices ok=" + regOk + (regOk ? "" : " err=" + Marshal.GetLastWin32Error()));

        SetTimer(hwnd, (IntPtr)1, 1000, IntPtr.Zero);

        lastRealMs = clock.ElapsedMilliseconds;
        displayOff = false;
        Log("ready: watching real input; phantom devices are ignored");

        MSG msg;
        int gm;
        while ((gm = GetMessageW(out msg, IntPtr.Zero, 0, 0)) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        if (gm == -1) TryLog("GetMessageW failed, err=" + Marshal.GetLastWin32Error());

        Log("message loop ended; exiting");
        if (instanceMutex != null) instanceMutex.ReleaseMutex();
        return 0;
    }
}
