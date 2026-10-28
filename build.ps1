<#
.SYNOPSIS
    Builds SleepGuard.exe into .\dist.

.DESCRIPTION
    Uses the C# compiler that ships with .NET Framework 4.x (part of Windows 10/11),
    so no Visual Studio / .NET SDK installation is required.

    The build target is CONSOLE (/target:exe) on purpose. On the reference machine a
    GUI-subsystem build (/target:winexe) never received a single raw input event, and
    AllocConsole() at runtime did not help either - see docs\troubleshooting.md.
    SleepGuard hides its own console window at start-up, so the user never sees it.

    Diagnostic probes (RawProbe, KeyProbe, AudioSessions, ...) are built separately:
        .\tools\build-tools.ps1

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Clean
#>
[CmdletBinding()]
param([switch]$Clean)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'dist'

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe (.NET Framework 4.x) not found in %WINDIR%\Microsoft.NET. Is .NET Framework installed?' }

if ($Clean -and (Test-Path $out)) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$src = Join-Path $root 'src\SleepGuard.cs'
if (-not (Test-Path $src)) { throw "source not found: $src" }
$target = Join-Path $out 'SleepGuard.exe'

# A running guard locks its own image; stop it before rebuilding.
Get-Process -Name 'SleepGuard' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Warning "stopping running SleepGuard.exe (pid $($_.Id)) so the file can be replaced"
    try { $_.Kill() } catch { }
}
Start-Sleep -Milliseconds 400

Write-Host 'csc src\SleepGuard.cs -> dist\SleepGuard.exe (/target:exe)' -ForegroundColor Cyan
& $csc /nologo /optimize+ /target:exe "/out:$target" /r:System.dll $src
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

$file = Get-Item $target
Write-Host ("  {0,-16} {1,8:N0} bytes  ({2})" -f $file.Name, $file.Length, $file.LastWriteTime) -ForegroundColor Green
Write-Host 'Build OK. Next: .\install.ps1 (elevated PowerShell) or run dist\SleepGuard.exe manually.' -ForegroundColor Green
