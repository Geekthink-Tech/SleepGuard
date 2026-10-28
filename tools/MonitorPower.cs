using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// DDC/CI monitor power control (VCP 0xD6 = DPMS power mode).
//   1 = on, 2 = standby, 3 = suspend, 4 = off (soft), 5 = off (hard)
// Usage:
//   MonitorPower.exe list                  show monitors and their current power mode
//   MonitorPower.exe get                   same as list
//   MonitorPower.exe off [--hard]          put every monitor into DPMS off
//   MonitorPower.exe on                    bring every monitor back to DPMS on
//   MonitorPower.exe watch <seconds>       poll power mode every second and log changes
internal static class MonitorPower
{
    private const byte VCP_POWER = 0xD6;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, out uint pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    private static List<PHYSICAL_MONITOR> Monitors()
    {
        var found = new List<PHYSICAL_MONITOR>();
        MonitorEnumProc cb = delegate(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data)
        {
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0) return true;
            var arr = new PHYSICAL_MONITOR[count];
            if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
            {
                for (int i = 0; i < count; i++) found.Add(arr[i]);
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        return found;
    }

    private static string PowerName(uint v)
    {
        switch (v)
        {
            case 1: return "on (DPMS on)";
            case 2: return "standby";
            case 3: return "suspend";
            case 4: return "off (soft off)";
            case 5: return "off (hard off)";
            default: return "unknown/" + v;
        }
    }

    private static int GetPower(IntPtr h, out uint value)
    {
        uint cur = 0, max = 0, type = 0;
        value = 0;
        if (!GetVCPFeatureAndVCPFeatureReply(h, VCP_POWER, out type, out cur, out max)) return Marshal.GetLastWin32Error();
        value = cur;
        return 0;
    }

    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var list = Monitors();
        if (list.Count == 0)
        {
            Console.Error.WriteLine("no physical monitors found (DDC/CI unsupported?)");
            return 1;
        }

        if (cmd == "list" || cmd == "get")
        {
            foreach (var m in list)
            {
                uint v;
                int err = GetPower(m.hPhysicalMonitor, out v);
                Console.WriteLine("{0,-40} power={1}", m.szPhysicalMonitorDescription,
                    err == 0 ? PowerName(v) : ("unreadable (err " + err + ")"));
            }
            return 0;
        }

        if (cmd == "off" || cmd == "on")
        {
            uint target = cmd == "on" ? 1u : (args.Length > 1 && args[1] == "--hard" ? 5u : 4u);
            int failures = 0;
            foreach (var m in list)
            {
                if (!SetVCPFeature(m.hPhysicalMonitor, VCP_POWER, target))
                {
                    failures++;
                    Console.Error.WriteLine("SetVCPFeature failed on {0}: err {1}", m.szPhysicalMonitorDescription, Marshal.GetLastWin32Error());
                }
                else
                {
                    Console.WriteLine("{0,-40} -> requested {1}", m.szPhysicalMonitorDescription, PowerName(target));
                }
            }
            return failures == 0 ? 0 : 2;
        }

        if (cmd == "watch")
        {
            int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;
            var last = new Dictionary<IntPtr, uint>();
            for (int t = 0; t < seconds; t++)
            {
                foreach (var m in list)
                {
                    uint v;
                    if (GetPower(m.hPhysicalMonitor, out v) != 0) continue;
                    uint prev;
                    if (!last.TryGetValue(m.hPhysicalMonitor, out prev) || prev != v)
                    {
                        Console.WriteLine("{0} {1,-40} {2}", DateTime.Now.ToString("HH:mm:ss.fff"), m.szPhysicalMonitorDescription, PowerName(v));
                        last[m.hPhysicalMonitor] = v;
                    }
                }
                System.Threading.Thread.Sleep(1000);
            }
            return 0;
        }

        Console.Error.WriteLine("usage: MonitorPower.exe list|get|on|off [--hard]|watch <seconds>");
        return 1;
    }
}
