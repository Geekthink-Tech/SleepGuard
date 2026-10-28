Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Inj {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
}
"@
[Inj]::SetCursorPos(400, 300) | Out-Null
Start-Sleep -Milliseconds 200
for ($i = 0; $i -lt 4; $i++) {
    [Inj]::keybd_event(0x41, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [Inj]::keybd_event(0x41, 0, 2, [IntPtr]::Zero)
    [Inj]::SetCursorPos(400 + ($i + 1) * 41, 300 + ($i + 1) * 33) | Out-Null
    Start-Sleep -Milliseconds 400
}
# one wheel notch as well
[Inj]::mouse_event(0x0800, 0, 0, 120, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300
"injected ok"
