<#
.SYNOPSIS
    End-to-end suspend test: idle -> monitor off -> real S3 sleep -> self wake.

.DESCRIPTION
    THIS REALLY SUSPENDS THE MACHINE. Save your work first. It runs with short
    thresholds (default: monitor off after 15 s, S3 after 20 s, wake itself after
    25 s), so the whole cycle takes about two minutes.

    Pass criteria:
      * guard log contains "requesting suspend (S3)"
      * guard log contains "resumed from suspend"
      * Kernel-Power 187 (SetSuspendState called), 42 (entered sleep) and
        131 (firmware S3 timing) appear after the test started
      * no new crash entry for SleepGuard.exe in the Application log

    Note: the wake timer fires late on this machine (about 40 s after the requested
    time), so the wait window is generous.

.PARAMETER Force
    Required - the test suspends the machine.

.PARAMETER WakeSeconds
    Seconds after going to sleep when the guard should wake itself.

.EXAMPLE
    .\tests\run-sleep-test.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [int]$DisplaySeconds = 15,
    [int]$SleepSeconds   = 20,
    [int]$WakeSeconds    = 25
)

. "$PSScriptRoot\_common.ps1"

if (-not $Force) {
    Write-Host 'This test REALLY suspends the machine (S3) and wakes it again.' -ForegroundColor Yellow
    Write-Host 'Save your work, then run:  .\tests\run-sleep-test.ps1 -Force' -ForegroundColor Yellow
    exit 2
}

$log       = Join-Path (Get-SGArtifactDir) 'sleep-test.log'
$guard     = $null
$ok        = $false
$startedAt = Get-Date
$totalWait = $SleepSeconds + $WakeSeconds + 180

$state = Stop-SGProductionGuard
"production guard stopped (taskExisted=$($state.TaskExisted) wasRunning=$($state.WasRunning) taskDisabled=$($state.TaskDisabled))"

try {
    $guard = Start-SGTestGuard -GuardArgs @(
        '--display', "$DisplaySeconds",
        '--sleep',   "$SleepSeconds",
        '--hibernate', '0',
        '--wake',    "$WakeSeconds",
        '--audio-debug'
    ) -LogPath $log
    "test guard pid=$($guard.Pid) log=$log"
    "screen off after ${DisplaySeconds}s, S3 after ${SleepSeconds}s, self wake after ${WakeSeconds}s"
    "=== the machine is going to sleep now - do not touch the keyboard ==="

    $suspend = Wait-SGLogMatch -Path $log -Pattern 'requesting suspend \(S3\)' -TimeoutSec ($SleepSeconds + 60)
    $resume  = $null
    if ($suspend) {
        "suspend requested at $($suspend.Line.Trim())"
        $resume = Wait-SGLogMatch -Path $log -Pattern 'resumed from suspend' -TimeoutSec $totalWait
    }

    if ($suspend -and $resume) {
        "resumed: $($resume.Line.Trim())"
        Start-Sleep -Seconds 2
        $events = Get-SGKernelPowerEvents -SinceMinutes 25 -MaxEvents 30
        $ids    = @($events | ForEach-Object { $_.Id })
        $events | Format-Table -AutoSize | Out-String | Write-Host
        $haveAll = ($ids -contains 187) -and ($ids -contains 42) -and ($ids -contains 131)
        if ($haveAll) {
            $ok = Write-SGResult -Name 'S3 suspend + self wake' -Pass $true -Detail 'Kernel-Power 187/42/131 all present'
        } else {
            $ok = Write-SGResult -Name 'S3 suspend + self wake' -Pass $false -Detail "missing events, saw ids: $($ids -join ',')"
        }
    } else {
        $ok = Write-SGResult -Name 'S3 suspend + self wake' -Pass $false -Detail 'guard never suspended or never resumed'
        '--- guard log tail ---'
        Get-Content $log -Tail 40 -ErrorAction SilentlyContinue | ForEach-Object { "  $_" }
    }

    # crash check: the WM_INPUT bug used to kill the process right after the first event
    try {
        $crashes = Get-WinEvent -FilterHashtable @{
            LogName    = 'Application'
            ProviderName = 'Application Error'
            StartTime  = $startedAt
        } -MaxEvents 10 -ErrorAction Stop | Where-Object { $_.Message -match 'SleepGuard' }
        if ($crashes) {
            $ok = Write-SGResult -Name 'no crash' -Pass $false -Detail "$($crashes.Count) Application Error event(s) for SleepGuard.exe"
        } else {
            Write-SGResult -Name 'no crash' -Pass $true -Detail 'no Application Error for SleepGuard.exe'
        }
    } catch {
        Write-Host '[SKIP] crash check - no Application Error events readable' -ForegroundColor DarkYellow
    }
}
catch {
    $ok = Write-SGResult -Name 'S3 suspend + self wake' -Pass $false -Detail $_.Exception.Message
}
finally {
    Stop-SGTestGuard -Guards @($guard)
    "restore: $(Restore-SGProductionGuard)"
}

if ($ok) { exit 0 } else { exit 1 }
