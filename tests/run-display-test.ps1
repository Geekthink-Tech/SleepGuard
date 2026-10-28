<#
.SYNOPSIS
    Checks that the guard really turns the monitors off after the display timeout.

.DESCRIPTION
    Starts a throw-away guard with a very short display timeout
    (--display 15 --sleep 0 --hibernate 0) and requires the log line
    "turning monitors off" within the timeout. The monitor IS switched off for a
    few seconds - that is the point of the test. Injected input at the end wakes it
    again.

    Sleep and hibernate are disabled, so the machine will not suspend here. Use
    run-sleep-test.ps1 for the suspend/resume path.

.PARAMETER TimeoutSeconds
    How long to wait for the display-off line (idle timeout + policy tick + slack).

.EXAMPLE
    .\tests\run-display-test.ps1
#>
[CmdletBinding()]
param([int]$TimeoutSeconds = 45, [int]$DisplaySeconds = 15)

. "$PSScriptRoot\_common.ps1"

$log   = Join-Path (Get-SGArtifactDir) 'display-test.log'
$guard = $null
$ok    = $false

$state = Stop-SGProductionGuard
"production guard stopped (taskExisted=$($state.TaskExisted) wasRunning=$($state.WasRunning) taskDisabled=$($state.TaskDisabled))"

try {
    $guard = Start-SGTestGuard -GuardArgs @('--display', "$DisplaySeconds", '--sleep', '0', '--hibernate', '0', '--audio-debug') -LogPath $log
    "test guard pid=$($guard.Pid) log=$log  (display timeout ${DisplaySeconds}s - screens will go dark)"
    "waiting for idle -> monitor off ..."

    $hit = Wait-SGLogMatch -Path $log -Pattern 'turning monitors off' -TimeoutSec $TimeoutSeconds
    if ($hit) {
        $ok = Write-SGResult -Name 'monitor off after idle' -Pass $true -Detail $hit.Line.Trim()
    } else {
        $ok = Write-SGResult -Name 'monitor off after idle' -Pass $false -Detail "no 'turning monitors off' within ${TimeoutSeconds}s"
        '--- guard log tail ---'
        Get-Content $log -Tail 30 -ErrorAction SilentlyContinue | ForEach-Object { "  $_" }
    }
}
catch {
    $ok = Write-SGResult -Name 'monitor off after idle' -Pass $false -Detail $_.Exception.Message
}
finally {
    Stop-SGTestGuard -Guards @($guard)
    "restore: $(Restore-SGProductionGuard)"
    # wake the monitor again
    Send-SGTestInput -MouseOnly | Out-Null
}

if ($ok) { exit 0 } else { exit 1 }
