<#
    Shared helpers for the SleepGuard test scripts in this folder.

    Dot-source it, do not run it:
        . "$PSScriptRoot\_common.ps1"

    Rules of engagement
    -------------------
    * Only ONE guard can run at a time (single-instance mutex), so every test stops
      the production guard first and restores it when it is done.
    * Test guards are always started through WMI (Win32_Process.Create). A process
      started by Task Scheduler has no usable SeShutdownPrivilege on this machine,
      so SetSuspendState would silently fail and a sleep test would "pass" nothing.
    * Test guards always get an explicit --log so the caller knows where to look.
#>

$ErrorActionPreference = 'Stop'

$script:SGRoot        = Split-Path -Parent $PSScriptRoot
$script:SGTestPids    = New-Object System.Collections.ArrayList
$script:SGWasRunning  = $false
$script:SGTaskName    = 'SleepGuard'

function Get-SGRoot { $script:SGRoot }

function Get-SGArtifactDir {
    $dir = Join-Path $PSScriptRoot 'artifacts'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    return $dir
}

function Get-SGGuardExe {
    $candidates = @(
        (Join-Path $script:SGRoot 'dist\SleepGuard.exe'),
        (Join-Path $script:SGRoot 'SleepGuard.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw 'SleepGuard.exe not found - build it first:  .\build.ps1'
}

function Stop-SGProductionGuard {
    <# Stops the scheduled task (if running) and kills any SleepGuard.exe. #>
    $task = $null
    try { $task = Get-ScheduledTask -TaskName $script:SGTaskName -ErrorAction Stop } catch { }

    $script:SGWasRunning = [bool]($task -and $task.State -eq 'Running')
    if ($script:SGWasRunning) {
        try { Stop-ScheduledTask -TaskName $script:SGTaskName -ErrorAction Stop } catch { }
    }
    Get-Process -Name 'SleepGuard' -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch { }
    }
    Start-Sleep -Milliseconds 800
    $left = @(Get-Process -Name 'SleepGuard' -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) { throw "could not stop the running guard (pid $($left.Id -join ', '))" }

    return [pscustomobject]@{
        TaskExisted  = [bool]$task
        TaskDisabled = [bool]($task -and $task.State -eq 'Disabled')
        WasRunning   = $script:SGWasRunning
    }
}

function Restore-SGProductionGuard {
    <# Brings the production guard back the way Stop-SGProductionGuard left it. #>
    $task = $null
    try { $task = Get-ScheduledTask -TaskName $script:SGTaskName -ErrorAction Stop } catch { }
    if (-not $task)    { return 'no SleepGuard task registered - nothing to restore' }
    if ($task.State -eq 'Disabled') { return 'task is Disabled - left it disabled' }
    if (@(Get-Process -Name 'SleepGuard' -ErrorAction SilentlyContinue).Count -gt 0) {
        return 'a guard process is already running'
    }
    Start-ScheduledTask -TaskName $script:SGTaskName
    Start-Sleep -Seconds 3
    $now = @(Get-Process -Name 'SleepGuard' -ErrorAction SilentlyContinue)
    if ($now.Count -eq 0) {
        # The task only fires at logon; fall back to the launcher so the machine is protected.
        $launcher = Join-Path $script:SGRoot 'launch-sleepguard.ps1'
        if (Test-Path $launcher) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File $launcher
            Start-Sleep -Seconds 3
            $now = @(Get-Process -Name 'SleepGuard' -ErrorAction SilentlyContinue)
        }
    }
    if ($now.Count -gt 0) { return "production guard running (pid $($now.Id -join ', '))" }
    return 'WARNING: production guard is NOT running - check the SleepGuard task'
}

function Start-SGTestGuard {
    <#
        Starts a guard with the given arguments and returns @{ Pid; Log }.
        Always goes through WMI so the child token can actually suspend the machine.
    #>
    param(
        [string[]]$GuardArgs = @(),
        [Parameter(Mandatory)][string]$LogPath
    )
    $exe = Get-SGGuardExe
    if (Test-Path $LogPath) { Remove-Item $LogPath -Force }

    $quoted = foreach ($a in $GuardArgs) {
        if ($a -match '[\s&]') { '"' + $a + '"' } else { $a }
    }
    $cmdline = ('"{0}" --log "{1}"' -f $exe, $LogPath)
    if ($quoted) { $cmdline += ' ' + ($quoted -join ' ') }

    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmdline }
    if ($r.ReturnValue -ne 0) { throw "Win32_Process.Create failed (return $($r.ReturnValue)): $cmdline" }
    [void]$script:SGTestPids.Add([int]$r.ProcessId)

    # Wait for the guard to write its banner; if it dies immediately, say so.
    $banner = Wait-SGLogMatch -Path $LogPath -Pattern 'ready v|config:' -TimeoutSec 10
    if (-not $banner) {
        $alive = @(Get-Process -Id $r.ProcessId -ErrorAction SilentlyContinue).Count -gt 0
        throw "test guard did not come up (pid $($r.ProcessId), alive=$alive). Log:`n$(Get-Content $LogPath -ErrorAction SilentlyContinue -Tail 20 | Out-String)"
    }
    return [pscustomobject]@{ Pid = [int]$r.ProcessId; Log = $LogPath }
}

function Stop-SGTestGuard {
    param([object[]]$Guards = @())
    $pids = @()
    if ($Guards) { $pids = @($Guards | ForEach-Object { $_.Pid }) } else { $pids = @($script:SGTestPids) }
    foreach ($p in $pids) {
        $proc = Get-Process -Id $p -ErrorAction SilentlyContinue
        if ($proc) { try { $proc.Kill() } catch { } }
    }
    Start-Sleep -Milliseconds 500
}

function Wait-SGLogMatch {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Pattern,
        [int]$TimeoutSec = 20
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        if (Test-Path $Path) {
            $m = Select-String -Path $Path -Pattern $Pattern -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($m) { return $m }
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Send-SGTestInput {
    <# Injects cursor movement, a click, a wheel notch and a few key strokes. #>
    param([switch]$KeysOnly, [switch]$MouseOnly)

    if (-not ('SGTestInput' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SGTestInput {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
}
'@
    }

    if (-not $KeysOnly) {
        $cx = [int]([SGTestInput]::GetSystemMetrics(0) / 2)
        $cy = [int]([SGTestInput]::GetSystemMetrics(1) / 2)
        [void][SGTestInput]::SetCursorPos($cx + 40, $cy + 20)
        Start-Sleep -Milliseconds 60
        [void][SGTestInput]::SetCursorPos($cx - 40, $cy - 20)
        Start-Sleep -Milliseconds 60
        [SGTestInput]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # left down
        [SGTestInput]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # left up
        [SGTestInput]::mouse_event(0x0800, 0, 0, 120, [IntPtr]::Zero) # wheel
        Start-Sleep -Milliseconds 80
    }
    if (-not $MouseOnly) {
        foreach ($vk in @(0x41, 0x42, 0x43)) {
            [SGTestInput]::keybd_event([byte]$vk, 0, 0, [IntPtr]::Zero)
            Start-Sleep -Milliseconds 50
            [SGTestInput]::keybd_event([byte]$vk, 0, 2, [IntPtr]::Zero)
            Start-Sleep -Milliseconds 50
        }
    }
}

function Write-SGResult {
    param([string]$Name, [bool]$Pass, [string]$Detail = '')
    $tag = if ($Pass) { 'PASS' } else { 'FAIL' }
    $color = if ($Pass) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1}{2}" -f $tag, $Name, $(if ($Detail) { " - $Detail" } else { '' })) -ForegroundColor $color
    return $Pass
}

function Get-SGKernelPowerEvents {
    <# Recent suspend/resume events, newest first: 42=enter sleep, 107=resume, 187=SetSuspendState called, 131=firmware S3 timing. #>
    param([int]$SinceMinutes = 10, [int]$MaxEvents = 20)
    $since = (Get-Date).AddMinutes(-$SinceMinutes)
    try {
        Get-WinEvent -FilterHashtable @{
            LogName      = 'System'
            ProviderName = 'Microsoft-Windows-Kernel-Power'
            Id           = 42, 107, 187, 131
            StartTime    = $since
        } -MaxEvents $MaxEvents -ErrorAction Stop |
            Select-Object TimeCreated, Id, @{ n = 'Line'; e = { ($_.Message -split "`n")[0] } } |
            Sort-Object TimeCreated
    } catch { }
}
