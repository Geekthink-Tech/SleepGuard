<#
.SYNOPSIS
    Builds the diagnostic probes that ship in tools\ into .\dist\tools.

.DESCRIPTION
    Everything is compiled with the C# compiler that ships with .NET Framework 4.x
    (part of Windows 10/11), so no Visual Studio or .NET SDK is needed.

    All probes are console-subsystem (/target:exe) on purpose: on this machine a
    GUI-subsystem (/target:winexe) process never received any raw input, which is
    exactly the class of bug the probes are meant to expose. One extra copy of
    RawProbe is built as /target:winexe so that difference can be reproduced:
    RawProbe-winexe.exe is expected to report mouse=0 while RawProbe.exe counts
    real events.

.EXAMPLE
    .\tools\build-tools.ps1
    .\tools\build-tools.ps1 -Clean
#>
[CmdletBinding()]
param([switch]$Clean)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'dist\tools'

$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe (.NET Framework 4.x) not found in %WINDIR%\Microsoft.NET.' }

if ($Clean -and (Test-Path $out)) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$consoleTools = @(
    'RawProbe',      # raw input counters (mouse / keyboard / consumer-control), phantom-device aware
    'KeyProbe',      # GetAsyncKeyState polling, per launch context
    'HookProbe',     # WH_MOUSE_LL / WH_KEYBOARD_LL delivery test
    'AllocProbe',    # does AllocConsole() rescue raw input in a winexe process
    'SendMove',      # injects absolute cursor moves (SendInput)
    'AudioSessions', # dumps CoreAudio sessions per endpoint
    'AudioTest',     # loops a system WAV so a real audio session exists
    'PlaySound',     # plays one short WAV
    'SuspendTest'    # prints token privileges, then calls SetSuspendState
)

$failed = @()
foreach ($name in $consoleTools) {
    $src = Join-Path $PSScriptRoot "$name.cs"
    if (-not (Test-Path $src)) { Write-Warning "skip ${name}: source not found"; continue }
    Write-Host "csc $name.cs -> dist\tools\$name.exe" -ForegroundColor Cyan
    & $csc /nologo /optimize+ /target:exe "/out:$(Join-Path $out "$name.exe")" $src | Out-Null
    if ($LASTEXITCODE -ne 0) { $failed += $name }
}

# Same source, GUI subsystem: reproduces "a winexe never sees raw input" on machines
# where that happens. Keep it, it is the cheapest regression test for the v1.1.2 bug.
$rawProbe = Join-Path $PSScriptRoot 'RawProbe.cs'
if (Test-Path $rawProbe) {
    Write-Host 'csc RawProbe.cs -> dist\tools\RawProbe-winexe.exe' -ForegroundColor Cyan
    & $csc /nologo /optimize+ /target:winexe "/out:$(Join-Path $out 'RawProbe-winexe.exe')" $rawProbe | Out-Null
    if ($LASTEXITCODE -ne 0) { $failed += 'RawProbe-winexe' }
}

if ($failed.Count -gt 0) { throw "build failed for: $($failed -join ', ')" }

Get-ChildItem $out -Filter *.exe | ForEach-Object { Write-Host ("  {0,-22} {1,8:N0} bytes" -f $_.Name, $_.Length) }
Write-Host 'Tools build OK.' -ForegroundColor Green
