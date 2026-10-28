<#
.SYNOPSIS
    Checks the pause-agent protocol end to end: guard request file -> agent suspends HiMOS ->
    guard resume request -> agent resumes HiMOS.

.DESCRIPTION
    The monitor goes dark with SC_MONITORPOWER, but HiMOS keeps writing a HID consumer-control
    report about 3 times per second, so Windows considers the desk in use and the panel lights up
    again. The guard therefore pauses HiMOS while the screens are dark and resumes it on the first
    real input. It cannot suspend the process itself (a guard started by Task Scheduler/WMI gets a
    filtered process view on the reference machine), so it writes a request file that the
    "SleepGuardAgent" logon task executes.

    This test uses --test-pause, which performs one pause and one resume 2 seconds later at
    startup, so it never touches the display or the power state.

.PARAMETER PauseApp
    Process name(s) the guard should ask the agent to pause. Default HiMOS.

.EXAMPLE
    .\tests\run-pause-test.ps1
#>
[CmdletBinding()]
param(
    [string]$PauseApp = 'HiMOS'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$result = [ordered]@{
    Test       = 'pause-agent'
    StartedAt  = (Get-Date)
    Steps      = @()
    Pass       = $false
}

function Add-Step([string]$name, [bool]$ok, [string]$detail) {
    $result.Steps += [pscustomobject]@{ Step = $name; Ok = $ok; Detail = $detail }
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) -ForegroundColor $colour
}

$agentLog = Join-Path $env:LOCALAPPDATA 'SleepGuard\agent.log'
$request  = Join-Path $env:LOCALAPPDATA 'SleepGuard\pause-request.txt'
$artefact = Join-Path (Get-SGArtifactDir) 'pause-test.log'

if (-not (Test-Path $agentLog)) { throw "agent log not found: $agentLog (is the SleepGuardAgent task registered?)" }

# 0. the agent has to be alive before the guard can ask for anything
$agentPattern = '-File\s+"[^"]*sleepguard-agent\.ps1"'
$agents = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -match $agentPattern })
if ($agents.Count -eq 0) {
    Write-Host '  agent is not running, starting the SleepGuardAgent task...' -ForegroundColor Yellow
    Start-ScheduledTask -TaskName 'SleepGuardAgent'
    Start-Sleep -Seconds 3
    $agents = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -match $agentPattern })
}
Add-Step 'agent running' ($agents.Count -gt 0) ("pids: " + (($agents | ForEach-Object { $_.ProcessId }) -join ','))

$logBefore = @(Get-Content $agentLog -ErrorAction SilentlyContinue).Count

Stop-SGProductionGuard
$guard = $null
try {
    $guard = Start-SGTestGuard -LogPath $artefact -GuardArgs @(
        '--display', '0',
        '--sleep', '0',
        '--hibernate', '0',
        '--pause-app', $PauseApp,
        '--pause-request', $request,
        '--test-pause'
    )

    # 1. the guard must write a pause request
    $paused = Wait-SGLogMatch -Path $artefact -Pattern 'requested pause of' -TimeoutSec 15
    Add-Step 'guard wrote pause request' ([bool]$paused) ([string]$paused)

    # 2. the agent must have suspended the app
    Start-Sleep -Seconds 3
    $newLines = @(Get-Content $agentLog -Encoding UTF8 | Select-Object -Skip $logBefore)
    $pausedLine = $newLines | Where-Object { $_ -match 'paused (\d+) process\(es\)' } | Select-Object -First 1
    $n = 0
    if ($pausedLine -and $pausedLine -match 'paused (\d+) process\(es\)') { $n = [int]$Matches[1] }
    Add-Step 'agent suspended the app' ($n -gt 0) ([string]$pausedLine)

    # 3. the agent must have resumed it after the guard's resume request
    $resumedLine = $newLines | Where-Object { $_ -match 'resumed (\d+) process\(es\)' } | Select-Object -First 1
    $rn = 0
    if ($resumedLine -and $resumedLine -match 'resumed (\d+) process\(es\)') { $rn = [int]$Matches[1] }
    Add-Step 'agent resumed the app' ($rn -gt 0) ([string]$resumedLine)

    # 4. nothing may be left suspended
    $left = @(Get-Process -Name $PauseApp -ErrorAction SilentlyContinue)
    Add-Step 'app still running' ($left.Count -gt 0) ("$PauseApp processes: " + $left.Count)
}
finally {
    if ($guard) { Stop-SGTestGuard -Pid $guard.Pid }
    Restore-SGProductionGuard
    try { Remove-Item $request -Force -ErrorAction SilentlyContinue } catch { }
}

$result.Pass = -not ($result.Steps | Where-Object { -not $_.Ok })
Write-SGResult -Name 'pause-agent' -Pass $result.Pass -Detail ("{0} step(s), log: {1}" -f $result.Steps.Count, $artefact)
if (-not $result.Pass) { exit 1 }
