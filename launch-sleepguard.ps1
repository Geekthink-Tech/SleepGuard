param(
    [int]$Display = 300,
    [int]$Sleep = 600,
    [int]$Hibernate = 1800,
    [string]$Phantom = 'VID_1D6B&PID_0122'
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

$cmdLine = '"' + $exe + '" --display ' + $Display + ' --sleep ' + $Sleep + ' --hibernate ' + $Hibernate +
           ' --phantom "' + $Phantom + '" --log "' + $guardLog + '"'
try {
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmdLine }
    Write-LaunchLog "WMI create return=$($r.ReturnValue) pid=$($r.ProcessId) args=[$cmdLine]"
    if ($r.ReturnValue -ne 0) { exit 1 }
}
catch {
    Write-LaunchLog "WMI create failed: $($_.Exception.Message)"
    exit 1
}
