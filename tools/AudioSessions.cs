// AudioSessions.exe - diagnostic: what does CoreAudio think is playing right now?
// Prints every session on the default playback endpoint (state + owning process)
// and the endpoint peak level, so a "postponed display-off" decision can be checked.
using System;
using System.Runtime.InteropServices;

internal static class Program
{
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

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-F08625A6C5F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out int channelCount);
        [PreserveSig] int GetChannelsPeakValues(int channelCount, IntPtr peakValues);
        [PreserveSig] int QueryHardwareSupport(out int mask);
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

    [ComImport, Guid("BF448BEF-5048-4FC8-914F-2314662DE6E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
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
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    private static string ProcessName(uint pid)
    {
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return "?"; }
    }

    private static int Main()
    {
        CoInitializeEx(IntPtr.Zero, 0);
        IMMDeviceEnumerator devEnum = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice dev;
        if (devEnum.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev == null)
        {
            Console.WriteLine("no default playback endpoint");
            return 1;
        }
        string id;
        dev.GetId(out id);
        Console.WriteLine("default endpoint: " + id);

        Guid meterIid = typeof(IAudioMeterInformation).GUID;
        object meterObj;
        float peak = -1f;
        if (dev.Activate(ref meterIid, 0, IntPtr.Zero, out meterObj) == 0 && meterObj != null)
            peak = ((IAudioMeterInformation)meterObj).GetPeakValue(out peak);
        Console.WriteLine("endpoint peak: " + peak.ToString("0.0000"));

        Guid iid = typeof(IAudioSessionManager2).GUID;
        object mgrObj;
        if (dev.Activate(ref iid, 0, IntPtr.Zero, out mgrObj) != 0 || mgrObj == null)
        {
            Console.WriteLine("IAudioSessionManager2 activate failed");
            return 1;
        }
        IAudioSessionEnumerator e;
        IAudioSessionManager2 mgr = (IAudioSessionManager2)mgrObj;
        if (mgr.GetSessionEnumerator(out e) != 0 || e == null)
        {
            Console.WriteLine("GetSessionEnumerator failed");
            return 1;
        }
        int count;
        e.GetCount(out count);
        Console.WriteLine("sessions: " + count);
        int active = 0;
        for (int i = 0; i < count; i++)
        {
            object o;
            if (e.GetSession(i, out o) != 0 || o == null) continue;
            IAudioSessionControl2 c = o as IAudioSessionControl2;
            if (c == null) continue;
            int state;
            uint pid = 0;
            c.GetState(out state);
            try { c.GetProcessId(out pid); } catch { }
            string stateName = state == 0 ? "Inactive" : state == 1 ? "Active" : "Expired";
            if (state == 1) active++;
            Console.WriteLine(string.Format("  [{0}] pid={1,-6} {2,-10} {3}",
                i, pid, stateName, ProcessName(pid)));
        }
        Console.WriteLine("active sessions: " + active);
        Console.WriteLine("verdict (SleepGuard logic): " + (active > 0 ? "AUDIO PLAYING -> postpone display off" : "silent -> allow display off"));
        return 0;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
}
