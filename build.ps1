<#
.SYNOPSIS
    Builds SleepGuard.exe and AudioTest.exe into .\dist.

.DESCRIPTION
    Uses the C# compiler that ships with .NET Framework 4.x (part of Windows 10/11),
    so no Visual Studio / .NET SDK installation is required.

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

function Build-Target([string]$source, [string]$exe) {
    $src = Join-Path $root $source
    if (-not (Test-Path $src)) { throw "source not found: $src" }
    Write-Host "csc $source -> dist\$exe" -ForegroundColor Cyan
    & $csc /nologo /optimize+ /target:winexe "/out:$(Join-Path $out $exe)" /r:System.dll $src
    if ($LASTEXITCODE -ne 0) { throw "build failed for $source (exit $LASTEXITCODE)" }
}

Build-Target 'src\SleepGuard.cs' 'SleepGuard.exe'
Build-Target 'src\AudioTest.cs'  'AudioTest.exe'

Get-ChildItem $out -File | ForEach-Object { Write-Host ("  {0,-16} {1,8:N0} bytes" -f $_.Name, $_.Length) }
Write-Host 'Build OK. Next: .\install.ps1 (elevated PowerShell) or run dist\SleepGuard.exe manually.' -ForegroundColor Green
