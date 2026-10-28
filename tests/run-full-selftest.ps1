<#
.SYNOPSIS
    Runs the whole test suite: build, input detection, monitor-off, suspend/resume.

.DESCRIPTION
    Each stage runs in its own PowerShell process and reports PASS/FAIL. The suite
    stops the production guard at the start of every stage and restores it at the
    end, so the machine keeps its normal protection between stages.

    Stages:
      1. build            .\build.ps1  (+ .\tools\build-tools.ps1 unless -SkipTools)
      2. input            injected key/mouse/wheel must reset the idle clock   (safe)
      3. monitor-off      screen really goes dark after the short timeout      (screen flashes)
      4. suspend          real S3 + self wake                                  (MACHINE SLEEPS)

    Stage 4 needs -Force because it suspends the machine.

.EXAMPLE
    .\tests\run-full-selftest.ps1                 # stages 1-3
    .\tests\run-full-selftest.ps1 -Force          # stages 1-4 (machine will sleep)
    .\tests\run-full-selftest.ps1 -Force -SkipScreenOff
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$SkipBuild,
    [switch]$SkipTools,
    [switch]$SkipScreenOff,
    [switch]$SkipSleep
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$psExe     = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path $psExe)) { $psExe = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' }
$results   = New-Object System.Collections.ArrayList

function Invoke-Stage {
    param([string]$Name, [string]$Script, [string[]]$ExtraArgs = @())
    Write-Host ''
    Write-Host "===== $Name =====" -ForegroundColor Cyan
    & $psExe -NoProfile -ExecutionPolicy Bypass -File $Script @ExtraArgs
    $code = $LASTEXITCODE
    [void]$results.Add([pscustomobject]@{
        Stage  = $Name
        Result = if ($code -eq 0) { 'PASS' } elseif ($code -eq 2) { 'SKIP' } else { 'FAIL' }
        Exit   = $code
    })
}

Write-Host "SleepGuard test suite - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Cyan
Write-Host "repo: $root"

function Add-Skip {
    param([string]$Name, [string]$Why)
    [void]$results.Add([pscustomobject]@{ Stage = $Name; Result = 'SKIP'; Exit = 2 })
    Write-Host "[SKIP] $Name - $Why" -ForegroundColor DarkYellow
}

if (-not $SkipBuild) {
    $guardBuild = Join-Path $root 'build.ps1'
    if (Test-Path $guardBuild) {
        Write-Host ''
        Write-Host '===== build =====' -ForegroundColor Cyan
        & $psExe -NoProfile -ExecutionPolicy Bypass -File $guardBuild
        [void]$results.Add([pscustomobject]@{
            Stage  = 'build guard'
            Result = if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }
            Exit   = $LASTEXITCODE
        })
    } else {
        Add-Skip 'build guard' 'no build.ps1 here (installed copy)'
    }

    if (-not $SkipTools) {
        $toolsBuild = Join-Path $root 'tools\build-tools.ps1'
        if (Test-Path $toolsBuild) {
            & $psExe -NoProfile -ExecutionPolicy Bypass -File $toolsBuild
            [void]$results.Add([pscustomobject]@{
                Stage  = 'build tools'
                Result = if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }
                Exit   = $LASTEXITCODE
            })
        } else {
            Add-Skip 'build tools' 'no tools\build-tools.ps1 here'
        }
    }
}

Invoke-Stage -Name 'input detection' -Script (Join-Path $PSScriptRoot 'run-input-test.ps1')

if (-not $SkipScreenOff) {
    Invoke-Stage -Name 'monitor off' -Script (Join-Path $PSScriptRoot 'run-display-test.ps1')
}

if (-not $SkipSleep) {
    if ($Force) {
        Invoke-Stage -Name 'suspend + wake' -Script (Join-Path $PSScriptRoot 'run-sleep-test.ps1') -ExtraArgs @('-Force')
    } else {
        [void]$results.Add([pscustomobject]@{ Stage = 'suspend + wake'; Result = 'SKIP'; Exit = 2 })
        Write-Host '[SKIP] suspend + wake - pass -Force to really sleep the machine' -ForegroundColor DarkYellow
    }
}

Write-Host ''
Write-Host '===== summary =====' -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host

$failed = @($results | Where-Object { $_.Result -eq 'FAIL' })
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) stage(s) FAILED: $($failed.Stage -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host 'all executed stages passed' -ForegroundColor Green
exit 0
