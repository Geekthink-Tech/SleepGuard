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

.PARAMETER PauseApp
    Comma separated process names that write phantom input (HiMOS on this machine) and must be
    paused while the screens are dark, then resumed on the first real input. The suspend is
    performed by the separate "SleepGuardAgent" logon task (tests\sleepguard-agent.ps1) because
    a guard started by Task Scheduler/WMI cannot see or open those processes on this machine.
    Pass '' to disable. Default 'HiMOS'.

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
    [string]$PauseApp = 'HiMOS',
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
if ($PauseApp) { $argument += " --pause-app `"$PauseApp`"" }

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
                -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcher`" -Display $Display -Sleep $Sleep -Hibernate $Hibernate -Phantom `"$Phantom`" -PauseApp `"$PauseApp`"" `
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

# The pause agent is a second logon task. It runs in the same interactive session but as a
# normal PowerShell process, which on this machine can see and suspend processes that the guard
# cannot (the guard saw 102 processes, Windows PowerShell 424, and HiMOS was simply absent).
# The task runs the LAUNCHER, not the agent: the launcher exits immediately when the agent
# heartbeat is fresh, so the 10-minute repetition doubles as a watchdog for a dead agent.
if ($PauseApp) {
    $agent     = Join-Path $root 'tests\start-sleepguard-agent.ps1'
    $agentAct  = New-ScheduledTaskAction -Execute 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
                    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$agent`"" `
                    -WorkingDirectory $root
    $agentSet  = New-ScheduledTaskSettingsSet `
                    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -ExecutionTimeLimit ([TimeSpan]::Zero) `
                    -MultipleInstances IgnoreNew -StartWhenAvailable `
                    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    # The agent is a plain polling loop with no restart of its own: it died on 2026-10-30 after
    # surviving a 44 h hibernation, and because nothing brought it back the screens stopped
    # staying dark. A repetition trigger makes Task Scheduler restart it (IgnoreNew keeps one
    # instance) so the death of the agent cannot silently disable the whole pause feature.
    $agentTrigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $agentTrigger.Repetition = (New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
                                    -RepetitionInterval (New-TimeSpan -Minutes 10) `
                                    -RepetitionDuration ([TimeSpan]::MaxValue)).Repetition
    Register-ScheduledTask -TaskName 'SleepGuardAgent' -Action $agentAct -Trigger $agentTrigger `
        -Principal $principal -Settings $agentSet -Force `
        -Description 'SleepGuard pause agent: suspends the phantom-input writer while the screens are dark and resumes it on real input.' | Out-Null
    Start-ScheduledTask -TaskName 'SleepGuardAgent'
    Start-Sleep -Seconds 1
}
else {
    Unregister-ScheduledTask -TaskName 'SleepGuardAgent' -Confirm:$false -ErrorAction SilentlyContinue
}

Start-ScheduledTask -TaskName 'SleepGuard'
Start-Sleep -Seconds 2

$state = (Get-ScheduledTask -TaskName 'SleepGuard').State
$count = (Get-Process SleepGuard -ErrorAction SilentlyContinue | Measure-Object).Count

Write-Host ''
Write-Host "SleepGuard installed." -ForegroundColor Green
Write-Host "  task     : $state"
Write-Host "  process  : $count"
Write-Host "  argument : $argument"
Write-Host "  agent    : $(if ($PauseApp) { (Get-ScheduledTask -TaskName 'SleepGuardAgent').State } else { 'disabled' })"
Write-Host "  exe      : $exe"
Write-Host "  log      : $env:LOCALAPPDATA\SleepGuard\sleep-guard.log"
Write-Host ''
Write-Host 'Verify with:' -ForegroundColor Cyan
Write-Host "  Get-Content `"$env:LOCALAPPDATA\SleepGuard\sleep-guard.log`" -Tail 20"
Write-Host 'Uninstall with: .\uninstall.ps1'
