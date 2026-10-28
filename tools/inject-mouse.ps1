Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class InjM {
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
}
"@
for ($i = 0; $i -lt 12; $i++) {
    [InjM]::mouse_event(0x0001, 40, 22, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}
"mouse injected"
