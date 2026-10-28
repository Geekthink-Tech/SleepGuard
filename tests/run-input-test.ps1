<#
.SYNOPSIS
    Checks that injected keyboard / mouse / wheel input reaches the guard.

.DESCRIPTION
    Starts a throw-away guard with every action disabled
    (--display 0 --sleep 0 --hibernate 0), injects one click, one wheel notch and a
    couple of keystrokes, and requires the guard log to contain
    "real input detected" within the timeout.

    Nothing is blanked and the machine is never suspended, so this test is safe to
    run at any time. It stops the production guard for the duration of the test and
    restores it afterwards.

.PARAMETER TimeoutSeconds
    How long to wait for the guard to react to the injected input.

.EXAMPLE
    .\tests\run-input-test.ps1
#>
[CmdletBinding()]
param([int]$TimeoutSeconds = 10)

. "$PSScriptRoot\_common.ps1"

$log   = Join-Path (Get-SGArtifactDir) 'input-test.log'
$guard = $null
$hit   = $null
$ok    = $false

$state = Stop-SGProductionGuard
"production guard stopped (taskExisted=$($state.TaskExisted) wasRunning=$($state.WasRunning) taskDisabled=$($state.TaskDisabled))"

try {
    $guard = Start-SGTestGuard -GuardArgs @('--display', '0', '--sleep', '0', '--hibernate', '0', '--audio-debug') -LogPath $log
    "test guard pid=$($guard.Pid) log=$log"
    Start-Sleep -Seconds 1
    "injecting input..."

    Send-SGTestInput

    $hit = Wait-SGLogMatch -Path $log -Pattern 'real input detected|real raw input #' -TimeoutSec $TimeoutSeconds
    if ($hit) {
        $ok = Write-SGResult -Name 'input detection' -Pass $true -Detail $hit.Line.Trim()
    } else {
        $ok = Write-SGResult -Name 'input detection' -Pass $false -Detail "no 'real input detected' within ${TimeoutSeconds}s"
        '--- guard log tail ---'
        Get-Content $log -Tail 30 -ErrorAction SilentlyContinue | ForEach-Object { "  $_" }
    }
}
catch {
    $ok = Write-SGResult -Name 'input detection' -Pass $false -Detail $_.Exception.Message
}
finally {
    Stop-SGTestGuard -Guards @($guard)
    "restore: $(Restore-SGProductionGuard)"
}

if ($ok) { exit 0 } else { exit 1 }
