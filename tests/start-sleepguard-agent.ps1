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

$cmd = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $AgentScript
try {
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
    if ($r.ReturnValue -ne 0) {
        Write-LauncherLog ("WMI Win32_Process.Create failed: {0}" -f $r.ReturnValue)
    } else {
        Write-LauncherLog ("agent started, pid={0}" -f $r.ProcessId)
    }
} catch {
    Write-LauncherLog ("could not start the agent: {0}" -f $_.Exception.Message)
}
