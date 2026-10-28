using System;
using System.Runtime.InteropServices;
using System.Threading;

// Sends synthetic relative mouse movement so we can test whether a listener
// sees "real" input. Usage: SendMove.exe <count> <intervalMs>
class SendMove
{
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    static int Main(string[] args)
    {
        int count = args.Length > 0 ? int.Parse(args[0]) : 10;
        int interval = args.Length > 1 ? int.Parse(args[1]) : 150;
        for (int i = 0; i < count; i++)
        {
            var a = new INPUT[1];
            a[0].type = 0; // INPUT_MOUSE
            a[0].mi.dx = (i % 2 == 0) ? 6 : -5;
            a[0].mi.dy = (i % 2 == 0) ? 4 : -3;
            a[0].mi.dwFlags = 0x0001; // MOUSEEVENTF_MOVE
            uint r = SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
            if (r == 0) { Console.WriteLine("SendInput failed err=" + Marshal.GetLastWin32Error()); return 1; }
            Thread.Sleep(interval);
        }
        Console.WriteLine("sent " + count + " moves");
        return 0;
    }
}
