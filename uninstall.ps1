<#
.SYNOPSIS
    Removes the SleepGuard scheduled task and stops the running watchdog.

.PARAMETER Purge
    Also delete %LOCALAPPDATA%\SleepGuard (log/settings) and dist\ (build output).

.EXAMPLE
    .\uninstall.ps1
    .\uninstall.ps1 -Purge
#>
[CmdletBinding()]
param([switch]$Purge)

$ErrorActionPreference = 'Continue'
$task = Get-ScheduledTask -TaskName 'SleepGuard' -ErrorAction SilentlyContinue
if ($task) {
    Stop-ScheduledTask -TaskName 'SleepGuard' -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName 'SleepGuard' -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host 'scheduled task "SleepGuard" removed'
} else {
    Write-Host 'scheduled task "SleepGuard" was not registered'
}

$agentTask = Get-ScheduledTask -TaskName 'SleepGuardAgent' -ErrorAction SilentlyContinue
if ($agentTask) {
    Stop-ScheduledTask -TaskName 'SleepGuardAgent' -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName 'SleepGuardAgent' -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host 'scheduled task "SleepGuardAgent" (pause agent) removed'
}
Get-Process SleepGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host 'running process stopped (if there was one)'

if ($Purge) {
    Remove-Item "$env:LOCALAPPDATA\SleepGuard" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $PSScriptRoot 'dist') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'purged log directory and build output'
}

Write-Host ''
Write-Host 'Done. Windows built-in power settings are untouched.' -ForegroundColor Green
Write-Host 'Note: if a phantom HID device is still present, the monitor will stay on again.'
Write-Host 'Permanent fix: disable the offending device in Device Manager, or turn off the'
Write-Host 'vendor app option that keeps that device alive.'
