#Requires -Version 5.1
<#
.SYNOPSIS
  Build the WhaleGo solution (Release by default) and print a compact summary.

.DESCRIPTION
  Runs: dotnet build windows/WhaleGo.sln -c <Configuration> [--no-restore]
  Fail-fast: any build error terminates the script with the dotnet exit code.
  Fixtures (windows/tests/WhaleGo.Fixtures.*) are part of the solution; pass -IncludeFixtures
  to build them explicitly too.

.PARAMETER Configuration
  Build configuration: Release (default) or Debug.

.PARAMETER NoRestore
  Pass --no-restore to dotnet build (requires a previous restore).

.PARAMETER IncludeFixtures
  Also build WhaleGo.Fixtures.WinForms and WhaleGo.Fixtures.Elevated explicitly.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Configuration Debug -NoRestore
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$NoRestore,

    [switch]$IncludeFixtures
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $RepoRoot 'windows/WhaleGo.sln'

if (-not (Test-Path $Solution)) {
    Write-Error "Solution not found: $Solution"
    exit 1
}

$buildArgs = @('build', $Solution, '-c', $Configuration, '-nologo')
if ($NoRestore) { $buildArgs += '--no-restore' }

Write-Host "==> dotnet $($buildArgs -join ' ')" -ForegroundColor Cyan
$started = Get-Date
& dotnet @buildArgs
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Host "BUILD FAILED (exit $exitCode)" -ForegroundColor Red
    exit $exitCode
}

if ($IncludeFixtures) {
    foreach ($fixture in @('windows/tests/WhaleGo.Fixtures.WinForms', 'windows/tests/WhaleGo.Fixtures.Elevated')) {
        $fixturePath = Join-Path $RepoRoot $fixture
        if (-not (Test-Path $fixturePath)) { continue }
        Write-Host "==> dotnet build $fixture -c $Configuration" -ForegroundColor Cyan
        & dotnet build $fixturePath -c $Configuration -nologo
        if ($LASTEXITCODE -ne 0) {
            Write-Host "BUILD FAILED: $fixture (exit $LASTEXITCODE)" -ForegroundColor Red
            exit $LASTEXITCODE
        }
    }
}

$elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
Write-Host ""
Write-Host "BUILD OK ($Configuration, $elapsed s)" -ForegroundColor Green
Write-Host "  solution : windows/WhaleGo.sln"
Write-Host "  executor : windows/src/WhaleGo.Executor/bin/$Configuration/net10.0-windows/WhaleGo.Executor.exe"
exit 0
