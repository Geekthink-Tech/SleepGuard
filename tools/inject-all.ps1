Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class InjA {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
}
"@
function Key($vk) {
    [InjA]::keybd_event($vk, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [InjA]::keybd_event($vk, 0, 2, [IntPtr]::Zero)
}
# keys
Key 0x41
Start-Sleep -Milliseconds 400
Key 0x42
Start-Sleep -Milliseconds 400
# left mouse button click
[InjA]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 120
[InjA]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 400
# wheel
[InjA]::mouse_event(0x0800, 0, 0, 120, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500
# a short burst of typing
foreach ($vk in @(0x43, 0x44, 0x45)) { Key $vk; Start-Sleep -Milliseconds 500 }
"injected all"
