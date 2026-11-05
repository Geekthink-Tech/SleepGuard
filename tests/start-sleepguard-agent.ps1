param(
    [string]$AgentScript   = (Join-Path $PSScriptRoot 'sleepguard-agent.ps1'),
    [string]$LogFile       = (Join-Path $env:LOCALAPPDATA 'SleepGuard\agent-launcher.log'),
    [int]$StaleSeconds     = 45
)

# SleepGuard agent launcher / watchdog
# ====================================
# The pause agent is a PowerShell polling loop that has now died twice on its own (exit code
# 0xC000013A, STATUS_CONTROL_C_EXIT: something closes its hidden console). The scheduled task's
# repetition cannot be relied on to bring it back, because a dead process can leave the task
# instance in the Running state, and IgnoreNew then refuses to start a new one.
#
# So this launcher is idempotent and cheap: if agent-heartbeat.txt is fresher than StaleSeconds,
# the agent is alive and nothing happens. Otherwise the agent is started through WMI, which is
# also how the guard itself is started (a normal PowerShell process, one that can see HiMOS -
# a plain .NET exe on this machine gets a filtered process view).
#
# Callers: the SleepGuardAgent scheduled task (logon + every 10 minutes), and the guard itself
# every 30 s when --pause-app is configured.

$ErrorActionPreference = 'Stop'
$heartbeat = Join-Path $env:LOCALAPPDATA 'SleepGuard\agent-heartbeat.txt'

function Write-LauncherLog([string]$message) {
    try {
        $line = '[{0:yyyy-MM-dd HH:mm:ss.fff}] [launcher {1}] {2}' -f (Get-Date), $PID, $message
        $line | Out-File -FilePath $LogFile -Append -Encoding utf8
    } catch { }
}

$age = -1
if (Test-Path -LiteralPath $heartbeat) {
    try {
        $stamp = [datetime]::Parse((Get-Content -LiteralPath $heartbeat -Raw).Trim(), [Globalization.CultureInfo]::InvariantCulture)
        $age = ((Get-Date) - $stamp).TotalSeconds
    } catch { $age = -1 }
}

if ($age -ge 0 -and $age -lt $StaleSeconds) {
    exit 0   # agent alive; stay quiet so a 10-minute repetition does not spam the log
}

$ageText = if ($age -lt 0) { 'missing' } else { ('{0:F0}s old' -f $age) }
Write-LauncherLog ("agent heartbeat is {0} -> starting the agent via WMI" -f $ageText)

# A zombie agent (process alive, loop dead) still holds the single-instance mutex, so a new
# instance would exit immediately. Clear it first; safe because the heartbeat says the old loop
# stopped working.
try {
    $stale = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like '*sleepguard-agent.ps1*' -and $_.CommandLine -notlike '*start-sleepguard-agent.ps1*' }
    foreach ($p in $stale) {
        Write-LauncherLog ("stopping stale agent pid={0}" -f $p.ProcessId)
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
} catch {
    Write-LauncherLog ("could not clear stale agents: {0}" -f $_.Exception.Message)
}

# Start the agent hidden with HideRun.exe: WMI creates that GUI-subsystem helper and the helper starts
# the agent with CREATE_NO_WINDOW + SW_HIDE, so no console (and no Windows Terminal window) appears.
# A direct powershell launch opened a Windows Terminal window on Windows 11 even with
# -WindowStyle Hidden - that was the "PowerShell keeps popping up" report of 2026-11-05.
# HideRun.exe lives under %LOCALAPPDATA%\SleepGuard: a copy inside the workspace folder starts but
# exits before Main, so the agent never appears (verified 2026-11-05). Fall back to the old path.
$hideRun = Join-Path $env:LOCALAPPDATA 'SleepGuard\HideRun.exe'
if (-not (Test-Path -LiteralPath $hideRun)) { $hideRun = Join-Path (Split-Path -Parent $PSScriptRoot) 'HideRun.exe' }
$cmdFile = Join-Path $env:TEMP 'sleepguard-agent-cmd.txt'
try {
    if (Test-Path -LiteralPath $hideRun) {
        $agentCmd = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $AgentScript + '"'
        [System.IO.File]::WriteAllText($cmdFile, $agentCmd, [System.Text.UTF8Encoding]::new($false))
        $wmi = '"' + $hideRun + '" "' + $cmdFile + '"'
        $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $wmi }
        if ($r.ReturnValue -ne 0) {
            Write-LauncherLog ("WMI -> HideRun failed: {0}" -f $r.ReturnValue)
        } else {
            Write-LauncherLog ("agent start requested (hidden), HideRun pid={0}" -f $r.ProcessId)
        }
    }
    else {
        Write-LauncherLog ("HideRun.exe not found at {0}" -f $hideRun)
    }
} catch {
    Write-LauncherLog ("could not start the agent: {0}" -f $_.Exception.Message)
}
