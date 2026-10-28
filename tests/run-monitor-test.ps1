<#
.SYNOPSIS
    Checks that the panel really stays dark and that real input brings it back.

.DESCRIPTION
    Regression test for the "the screen came back on immediately" bug: SC_MONITORPOWER
    only blanks the Windows output, so on this machine the phantom HID device
    (VID_1D6B&PID_0122, ~3 reports/s) made the display light up again right after it
    went dark. v1.1.3 blanks the panels through DDC/CI (MCCS VCP 0xD6) instead, which
    is handled by the monitor itself and is immune to input events.

    What it does, using a throw-away guard with a 15 s display timeout and no sleep:
      1. waits for "turning monitors off" in the guard log;
      2. asks the monitor for its DPMS power mode and requires "standby"/"off";
      3. waits a few seconds and requires the panel to still be dark
         (the phantom device must not bring it back);
      4. injects real mouse input and requires the log line
         "monitors woken on real input (ddc)" and power mode "on".

    Nothing is suspended, so this test is safe to run at any time. The production
    guard is stopped for the duration of the test and restored afterwards.

.PARAMETER DisplaySeconds
    Display-off timeout for the test guard. Keep it short: 15 s by default.

.PARAMETER HoldSeconds
    How long the panel must stay dark before real input is injected.

.EXAMPLE
    .\tests\run-monitor-test.ps1
    .\tests\run-monitor-test.ps1 -DisplaySeconds 10 -HoldSeconds 8
#>
[CmdletBinding()]
param(
    [int]$DisplaySeconds = 15,
    [int]$HoldSeconds = 6
)

. "$PSScriptRoot\_common.ps1"

function Get-MonitorPowerTool {
    $candidates = @(
        (Join-Path (Get-SGRoot) 'dist\tools\MonitorPower.exe'),
        (Join-Path (Get-SGRoot) 'tools\MonitorPower.exe'),
        (Join-Path (Get-SGRoot) '..\sleep-guard\tools\MonitorPower.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return (Resolve-Path $c).Path } }
    throw 'MonitorPower.exe not found - build the tools first:  .\tools\build-tools.ps1'
}

function Read-MonitorPower([string]$Tool) {
    # DDC reads fail with ERROR_GRAPHICS_* while the panel changes power state; retry.
    $text = ''
    for ($i = 0; $i -lt 4; $i++) {
        $lines = & $Tool list 2>&1
        $text = ($lines | Out-String).Trim()
        if ($text -notmatch 'unreadable') { break }
        Start-Sleep -Milliseconds 500
    }
    return $text
}

$tool  = Get-MonitorPowerTool
$log   = Join-Path (Get-SGArtifactDir) 'monitor-test.log'
$guard = $null

"monitor power tool: $tool"
$before = Read-MonitorPower $tool
"before: $before"

$state = Stop-SGProductionGuard
"production guard stopped (taskExisted=$($state.TaskExisted) wasRunning=$($state.WasRunning) taskDisabled=$($state.TaskDisabled))"

try {
    $guard = Start-SGTestGuard -GuardArgs @('--display', "$DisplaySeconds", '--sleep', '0', '--hibernate', '0') -LogPath $log
    "test guard pid=$($guard.Pid) log=$log"

    $off = Wait-SGLogMatch -Path $log -Pattern 'turning monitors off' -TimeoutSec ($DisplaySeconds + 25)
    if (-not $off) { throw "guard never turned the monitors off within $($DisplaySeconds + 25)s - see $log" }
    "log: $($off.Line.Trim())"

    $dark = $null
    for ($i = 0; $i -lt 15; $i++) {
        $dark = Read-MonitorPower $tool
        if ($dark -match 'power=(standby|off|suspend)') { break }
        Start-Sleep -Milliseconds 700
    }
    "after display-off: $dark"
    if ($dark -notmatch 'power=(standby|off|suspend)') {
        throw "monitor did not enter DPMS standby (got: $dark). Is DDC/CI enabled in the monitor OSD?"
    }

    "holding the panel dark for $HoldSeconds s (the phantom HID fires ~3 reports/s)..."
    Start-Sleep -Seconds $HoldSeconds
    $stillDark = Read-MonitorPower $tool
    "after ${HoldSeconds}s: $stillDark"
    if ($stillDark -notmatch 'power=(standby|off|suspend)') {
        throw "panel woke up on its own: $stillDark"
    }

    "injecting real mouse input..."
    Send-SGTestInput -MouseOnly

    $woke = Wait-SGLogMatch -Path $log -Pattern 'monitors woken on real input' -TimeoutSec 20
    Start-Sleep -Milliseconds 1500
    # Right after DPMS-on the DDC channel is often busy for a moment and the read
    # fails with ERROR_GRAPHICS_*; that is transient, so poll instead of reading once.
    $after = $null
    for ($i = 0; $i -lt 12; $i++) {
        $after = Read-MonitorPower $tool
        if ($after -match 'power=on') { break }
        Start-Sleep -Milliseconds 800
    }
    "after real input: $after"

    if (-not $woke) { throw "log has no 'monitors woken on real input' line - see $log" }
    if ($after -notmatch 'power=on') { throw "panel did not come back to DPMS on (got: $after)" }

    Write-SGResult -Name 'monitor power (DDC)' -Pass $true -Detail "standby held ${HoldSeconds}s, woke on real input"
} catch {
    Write-SGResult -Name 'monitor power (DDC)' -Pass $false -Detail $_.Exception.Message
} finally {
    if ($guard) { Stop-SGTestGuard }
    # Never leave the user with a dark screen because a test failed halfway.
    try { & $tool on | Out-Null } catch { }
    Restore-SGProductionGuard
    "production guard restore: taskEnabled=$((Get-ScheduledTask -TaskName $script:SGTaskName).State)"
}
