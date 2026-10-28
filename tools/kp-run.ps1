# kp-run.ps1 -- start KeyProbe, inject keys, print the log.
# usage: kp-run.ps1 <mode> [seconds]
param([string]$Mode = 'console', [int]$Seconds = 12)
$ErrorActionPreference = 'Continue'
$dir = 'C:\Users\Administrator\Documents\deepseek-harness\default-workspace\sleep-guard\diagnostics'
$log = Join-Path $dir ("kp-$Mode.log")
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-Process -FilePath (Join-Path $dir 'KeyProbe.exe') `
                   -ArgumentList @($log, $Mode, "$Seconds") `
                   -PassThru -WindowStyle Hidden
"started pid=$($p.Id) mode=$Mode"

Start-Sleep -Seconds 2
if (-not ('KPInject' -as [type])) {
    Add-Type -Namespace KP -Name Inject -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
'@
}
foreach ($vk in 0x51, 0x52, 0x53) {
    [KP.Inject]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    [KP.Inject]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}
[KP.Inject]::SetCursorPos(640, 360) | Out-Null
Start-Sleep -Milliseconds 400
[KP.Inject]::SetCursorPos(1280, 720) | Out-Null

"waiting for probe to finish..."
$p.WaitForExit()
"exit=$($p.ExitCode)"
Get-Content $log -ErrorAction SilentlyContinue
