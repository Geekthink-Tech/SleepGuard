Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class InjB {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
}
"@
$sw = [System.Diagnostics.Stopwatch]::StartNew()
function Log($m) { "{0,6:N0}ms  {1}" -f $sw.ElapsedMilliseconds, $m }
# 1) absolute cursor jumps (these generate real WM_MOUSEMOVE / raw mouse input)
[InjB]::SetCursorPos(1000, 800) | Out-Null
Start-Sleep -Milliseconds 250
Log "cursor set 1000,800"
foreach ($p in @(@(1200,900),@(1400,1000),@(1600,1100),@(1800,1200),@(1500,950))) {
    [InjB]::SetCursorPos($p[0], $p[1]) | Out-Null
    Start-Sleep -Milliseconds 220
    Log ("cursor set {0},{1}" -f $p[0], $p[1])
}
# 2) small relative moves
foreach ($i in 1..6) {
    [InjB]::mouse_event(0x0001, 15, 10, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
}
Log "relative moves done"
# 3) keys
foreach ($vk in @(0x41,0x42,0x43,0x44)) {
    [InjB]::keybd_event($vk, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [InjB]::keybd_event($vk, 0, 2, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}
Log "keys done"
# 4) clicks + wheel
[InjB]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 100
[InjB]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 200
[InjB]::mouse_event(0x0800, 0, 0, 120, [IntPtr]::Zero)
Log "clicks+wheel done"
