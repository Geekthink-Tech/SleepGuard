param(
    [int]$Display = 300,
    [int]$Sleep = 600,
    [int]$Hibernate = 1800,
    [string]$Phantom = 'VID_1D6B&PID_0122',
    [string]$PauseApp = ''
)

# SleepGuard launcher
#
# Why this indirection exists
# ---------------------------
# A process started DIRECTLY by Task Scheduler receives a token where
# SeShutdownPrivilege is present but disabled, and enabling it from inside the
# process silently fails (AdjustTokenPrivileges reports success, the token still
# shows attribute 0x0, and SetSuspendState then fails with 1314
# ERROR_PRIVILEGE_NOT_HELD). Measured on this machine:
#
#   task  -> SleepGuard.exe : SeShutdownPrivilege=0x0, SetSuspendState -> ret=0 err=1314
#   WMI   -> SleepGuard.exe : SeShutdownPrivilege=0x2, SetSuspendState -> ret=1 (machine slept)
#
# So the scheduled task only runs this launcher, and the launcher asks WMI to
# create the real guard process. That child gets the privilege ENABLED and can
# therefore suspend / hibernate.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$launchLog = Join-Path $here 'launcher.log'
$guardLog = Join-Path $env:LOCALAPPDATA 'SleepGuard\sleep-guard.log'

function Write-LaunchLog([string]$message) {
    try {
        ('[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date), $message) |
            Out-File -FilePath $launchLog -Append -Encoding utf8
    } catch { }
}

# Watch this file in log order; it tells you whether the launch itself worked.
$exe = Join-Path $here 'dist\SleepGuard.exe'
if (-not (Test-Path $exe)) { $exe = Join-Path $here 'SleepGuard.exe' }
if (-not (Test-Path $exe)) {
    Write-LaunchLog "SleepGuard.exe not found next to $here (or in dist\)"
    exit 1
}

$existing = Get-CimInstance Win32_Process -Filter "Name='SleepGuard.exe'" -ErrorAction SilentlyContinue
if ($existing) {
    $pid0 = ($existing | Select-Object -First 1).ProcessId
    Write-LaunchLog "guard already running pid=$pid0 - not starting a second instance"
    exit 0
}

# Keep the pause agent alive. Nobody restarts a stopped script otherwise: if the agent is
# missing when the screens go dark, HiMOS keeps writing HID reports and the monitors light up
# again. Missing agent is not fatal - the guard simply blanks the screens the old way.
try {
    $agentPattern = 'sleepguard-agent\.ps1'
    $agents = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -match $agentPattern })
    if ($agents.Count -eq 0) {
        Start-ScheduledTask -TaskName 'SleepGuardAgent' -ErrorAction Stop
        Write-LaunchLog "started SleepGuardAgent (no agent process was running)"
    } else {
        Write-LaunchLog ("pause agent already running pid={0}" -f $agents[0].ProcessId)
    }
}
catch {
    Write-LaunchLog "could not start SleepGuardAgent: $($_.Exception.Message)"
}

$cmdLine = '"' + $exe + '" --display ' + $Display + ' --sleep ' + $Sleep + ' --hibernate ' + $Hibernate +
           ' --phantom "' + $Phantom + '" --log "' + $guardLog + '"'

# Optional: name the process that must be paused while the screens are dark.
#
# HiMOS (the water-block screen app) writes a HID consumer-control report about 3 times a
# second through USB\VID_1D6B&PID_0122. Windows counts that as user input, so a monitor blanked
# with SC_MONITORPOWER comes back on immediately. The guard cannot suspend it directly (a .NET
# exe started by Task Scheduler/WMI gets a filtered process view on this machine and
# OpenProcess fails with err=5), so it only writes a request file and the "SleepGuardAgent"
# scheduled task performs the suspend/resume - see tests\sleepguard-agent.ps1.
if ($PauseApp) {
    $cmdLine += ' --pause-app "' + $PauseApp + '"'
}
try {
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmdLine }
    Write-LaunchLog "WMI create return=$($r.ReturnValue) pid=$($r.ProcessId) args=[$cmdLine]"
    if ($r.ReturnValue -ne 0) { exit 1 }
}
catch {
    Write-LaunchLog "WMI create failed: $($_.Exception.Message)"
    exit 1
}
