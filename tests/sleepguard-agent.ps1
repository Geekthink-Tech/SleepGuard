param(
    [string]$RequestFile = (Join-Path $env:LOCALAPPDATA 'SleepGuard\pause-request.txt'),
    [string]$StateFile   = (Join-Path $env:LOCALAPPDATA 'SleepGuard\pause-state.txt'),
    [string]$LogFile     = (Join-Path $env:LOCALAPPDATA 'SleepGuard\agent.log'),
    [string]$HeartbeatFile = (Join-Path $env:LOCALAPPDATA 'SleepGuard\agent-heartbeat.txt'),
    [int]$PollMs         = 500,
    [string]$GuardName   = 'SleepGuard'
)

# SleepGuard pause agent
# ======================
# Why this process exists
# -----------------------
# Sleeping the display with SC_MONITORPOWER lasts only until the next input event. On this
# machine HiMOS (the water-block screen app) writes a HID consumer-control report about 3x/s
# through USB\VID_1D6B&PID_0122, so Windows treats the desk as "in use" and the display comes
# straight back on. The fix is to pause HiMOS while the screens are dark and resume it on the
# first real input.
#
# Why it cannot live inside SleepGuard.exe: a compiled .NET exe started by Task Scheduler or
# WMI gets a filtered process view here (102 processes visible, Windows PowerShell sees 424;
# HiMOS is missing) and OpenProcess(PROCESS_SUSPEND_RESUME) on a pid handed to it fails with
# err=5 even with SeDebugPrivilege enabled. Windows PowerShell started normally can do both.
#
# So SleepGuard only writes a request file and this agent executes it:
#   pause-request.txt:  "pause"           -> suspend every process named in the second line
#                       "resume"          -> resume whatever this agent suspended
# A third line ("attempt N") is ignored here: SleepGuard rewrites the same pause request when
# the previous one was not confirmed, and the changed content is what makes it act again.
# The agent writes pause-state.txt ("paused:<pids>", "idle", "no-app", "guard-gone") for the
# guard and for the test scripts, agent-heartbeat.txt with a timestamp every few seconds so a
# watchdog can tell a live agent from a dead one, and it resumes the app if the guard
# disappears while the app is still paused (crash safety: otherwise the water-block display
# would stay frozen forever).

$ErrorActionPreference = 'Stop'
$agentId = $PID

# Only one agent may run: two would suspend the same pids twice and the second resume would
# leave the water-block app frozen after the first resume already released it.
$agentMutex = New-Object System.Threading.Mutex($false, 'Global\SleepGuardAgent')
if (-not $agentMutex.WaitOne(0)) {
    try {
        $line = '[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] another agent already running; exiting' -f (Get-Date), $agentId
        $line | Out-File -FilePath $LogFile -Append -Encoding utf8
    } catch { }
    exit 0
}

function Write-AgentLog([string]$message) {
    try {
        $line = '[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}' -f (Get-Date), $agentId, $message
        $line | Out-File -FilePath $LogFile -Append -Encoding utf8
    } catch { }
}

if (-not ('Sg.SgNative' -as [type])) {
    Add-Type -Namespace 'Sg' -Name 'SgNative' -MemberDefinition @'
[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
'@
}

$suspended = New-Object System.Collections.Generic.List[int]

function Suspend-NamedApp([string[]]$names) {
    $count = 0
    foreach ($name in $names) {
        $nm = [string]$name
        $nm = $nm.Trim()
        if (-not $nm) { continue }
        $procs = @(Get-Process -Name $nm -ErrorAction SilentlyContinue)
        foreach ($p in $procs) {
            try {
                if ($suspended.Contains($p.Id)) { continue }   # never suspend the same pid twice
                $rc = [Sg.SgNative]::NtSuspendProcess($p.Handle)
                if ($rc -eq 0) {
                    $suspended.Add($p.Id)
                    $count++
                } else {
                    Write-AgentLog ("suspend {0} pid={1} rc={2}" -f $nm, $p.Id, $rc)
                }
            } catch {
                Write-AgentLog ("suspend {0} pid={1} failed: {2}" -f $nm, $p.Id, $_.Exception.Message)
            } finally {
                $p.Dispose()
            }
        }
    }
    return $count
}

function Resume-SuspendedApp() {
    $count = 0
    foreach ($savedId in $suspended) {
        try {
            $p = Get-Process -Id $savedId -ErrorAction SilentlyContinue
            if ($p) {
                if ([Sg.SgNative]::NtResumeProcess($p.Handle) -eq 0) { $count++ }
                $p.Dispose()
            }
        } catch {
            Write-AgentLog ("resume pid={0} failed: {1}" -f $savedId, $_.Exception.Message)
        }
    }
    $suspended.Clear()
    return $count
}

function Write-State([string]$state) {
    try {
        $parent = Split-Path -Parent $StateFile
        if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
        Set-Content -LiteralPath $StateFile -Value $state -Encoding ascii
    } catch { }
}

function Write-Heartbeat() {
    try {
        $parent = Split-Path -Parent $HeartbeatFile
        if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
        Set-Content -LiteralPath $HeartbeatFile -Value ('{0:o}' -f (Get-Date)) -Encoding ascii
    } catch { }
}

$dir = Split-Path -Parent $RequestFile
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
Write-AgentLog "agent started; request=$RequestFile state=$StateFile poll=${PollMs}ms"
Write-State 'idle'
Write-Heartbeat

# The request is consumed by CONTENT, not by command word: a repeated "pause" that was never
# confirmed must run again, so the guard varies the third line and the signature changes.
$lastSignature = ''
$lastCommand = ''
$guardMissingSince = $null
$lastHeartbeat = Get-Date

while ($true) {
    try {
        $command = ''
        $names = @()
        $why = ''
        $signature = ''
        if (Test-Path -LiteralPath $RequestFile) {
            $raw = ''
            try { $raw = [string](Get-Content -LiteralPath $RequestFile -Raw -Encoding utf8 -ErrorAction SilentlyContinue) } catch { }
            $signature = ($raw -replace "`r`n", "`n").Trim()
            $lines = @($signature -split "`n")
            if ($lines.Count -ge 1) { $command = ([string]$lines[0]).Trim().ToLowerInvariant() }
            if ($lines.Count -ge 2) {
                $why = [string]$lines[1]
                $names = @(([string]$lines[1]) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            }
        }

        if ($command -eq 'pause' -and $signature -ne $lastSignature) {
            if ($names.Count -eq 0) { $names = @('HiMOS') }
            $n = Suspend-NamedApp $names
            if ($n -gt 0) {
                $joined = ($suspended | ForEach-Object { [string]$_ }) -join ','
                Write-State "paused:$joined"
                Write-AgentLog ("paused {0} process(es) of [{1}]: {2}" -f $n, ($names -join ','), $joined)
            } else {
                Write-State 'no-app'
                Write-AgentLog ("pause requested but no process of [{0}] was found" -f ($names -join ','))
            }
            $lastSignature = $signature
            $lastCommand = 'pause'
            $guardMissingSince = $null
        }
        elseif ($command -eq 'resume' -and $signature -ne $lastSignature) {
            $n = Resume-SuspendedApp
            Write-State 'idle'
            Write-AgentLog ("resumed {0} process(es) ({1})" -f $n, $why)
            $lastSignature = $signature
            $lastCommand = 'resume'
            $guardMissingSince = $null
        }

        # Crash safety: if the guard vanished while the app is still suspended, resume it.
        if ($suspended.Count -gt 0) {
            $guard = @(Get-Process -Name $GuardName -ErrorAction SilentlyContinue)
            if ($guard.Count -eq 0) {
                if ($null -eq $guardMissingSince) {
                    $guardMissingSince = Get-Date
                } elseif (((Get-Date) - $guardMissingSince).TotalSeconds -ge 5) {
                    $n = Resume-SuspendedApp
                    Write-State 'guard-gone'
                    Write-AgentLog ("guard is gone; resumed {0} process(es) for safety" -f $n)
                    $guardMissingSince = $null
                    $lastSignature = ''
                    $lastCommand = 'resume'
                }
            } else {
                $guardMissingSince = $null
            }
            foreach ($g in $guard) { $g.Dispose() }
        }
    } catch {
        Write-AgentLog ("loop error: " + $_.Exception.Message)
    }
    if (((Get-Date) - $lastHeartbeat).TotalSeconds -ge 2) {
        Write-Heartbeat
        $lastHeartbeat = Get-Date
    }
    Start-Sleep -Milliseconds $PollMs
}
