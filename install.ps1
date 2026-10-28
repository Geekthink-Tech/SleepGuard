<#
.SYNOPSIS
    Installs SleepGuard: builds it if needed, registers the "SleepGuard" logon task, starts it.

.DESCRIPTION
    The task runs in your interactive session with the highest privileges (needed for
    SeShutdownPrivilege to suspend/hibernate), has no execution time limit, is started at logon
    and restarted up to 3 times if it crashes.

.PARAMETER Display
    Seconds of real idle before the monitor is turned off. 0 = never. Default 300.

.PARAMETER Sleep
    Seconds of real idle before suspending to RAM (S3). 0 = never. Default 600.

.PARAMETER Hibernate
    Seconds of TOTAL idle before hibernating (hibernate happens as sleep -> wake -> hibernate).
    Must be greater than -Sleep. 0 = never. Default 1800.

.PARAMETER Phantom
    Comma separated device-name substrings whose raw input must be ignored (case-insensitive).
    Use tools\quiet-probe.ps1 to find them. Default 'VID_1D6B&PID_0122'.

.PARAMETER SkipBuild
    Fail instead of building when dist\SleepGuard.exe is missing.

.EXAMPLE
    .\install.ps1 -Display 300 -Sleep 600 -Hibernate 1800

.EXAMPLE
    .\install.ps1 -Display 900 -Sleep 0 -Hibernate 0 -Phantom 'VID_1D6B&PID_0122,VID_1234&PID_5678'
#>
[CmdletBinding()]
param(
    [int]$Display    = 300,
    [int]$Sleep      = 600,
    [int]$Hibernate  = 1800,
    [string]$Phantom = 'VID_1D6B&PID_0122',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an ELEVATED PowerShell window (the task needs SeShutdownPrivilege to suspend the machine).'
}
if ($Display -lt 0 -or $Sleep -lt 0 -or $Hibernate -lt 0) { throw 'All timeouts must be >= 0 seconds.' }
if ($Hibernate -gt 0 -and $Hibernate -le $Sleep) { throw '-Hibernate must be greater than -Sleep (or 0).' }

$exe = Join-Path $root 'dist\SleepGuard.exe'
if (-not (Test-Path $exe)) {
    if ($SkipBuild) { throw "not built: $exe (run .\build.ps1 first)" }
    & (Join-Path $root 'build.ps1')
    if (-not (Test-Path $exe)) { throw 'build did not produce dist\SleepGuard.exe' }
}

$argument = "--display $Display --sleep $Sleep --hibernate $Hibernate --phantom `"$Phantom`""

Get-Process SleepGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Unregister-ScheduledTask -TaskName 'SleepGuard' -Confirm:$false -ErrorAction SilentlyContinue

# The task starts the LAUNCHER, not SleepGuard.exe directly. A process started by
# Task Scheduler gets a token whose SeShutdownPrivilege cannot be enabled from
# inside (AdjustTokenPrivileges "succeeds", the attr stays 0x0, SetSuspendState
# then fails with 1314), so the guard would never be able to sleep the machine.
# launch-sleepguard.ps1 asks WMI to create the guard instead: that child has the
# privilege enabled and suspend works. See README "Task Scheduler steals the
# shutdown privilege".
$launcher = Join-Path $root 'launch-sleepguard.ps1'
$user      = "$env:USERDOMAIN\$env:USERNAME"
$action    = New-ScheduledTaskAction -Execute 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
                -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcher`" -Display $Display -Sleep $Sleep -Hibernate $Hibernate -Phantom `"$Phantom`"" `
                -WorkingDirectory $root
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet `
                -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                -ExecutionTimeLimit ([TimeSpan]::Zero) `
                -MultipleInstances IgnoreNew -StartWhenAvailable `
                -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName 'SleepGuard' -Action $action -Trigger $trigger -Principal $principal `
    -Settings $settings -Force `
    -Description 'Phantom-input aware idle watchdog: monitor off / suspend / hibernate on a real-input clock (launched via WMI so the shutdown privilege is usable).' | Out-Null

Start-ScheduledTask -TaskName 'SleepGuard'
Start-Sleep -Seconds 2

$state = (Get-ScheduledTask -TaskName 'SleepGuard').State
$count = (Get-Process SleepGuard -ErrorAction SilentlyContinue | Measure-Object).Count

Write-Host ''
Write-Host "SleepGuard installed." -ForegroundColor Green
Write-Host "  task     : $state"
Write-Host "  process  : $count"
Write-Host "  argument : $argument"
Write-Host "  exe      : $exe"
Write-Host "  log      : $env:LOCALAPPDATA\SleepGuard\sleep-guard.log"
Write-Host ''
Write-Host 'Verify with:' -ForegroundColor Cyan
Write-Host "  Get-Content `"$env:LOCALAPPDATA\SleepGuard\sleep-guard.log`" -Tail 20"
Write-Host 'Uninstall with: .\uninstall.ps1'
