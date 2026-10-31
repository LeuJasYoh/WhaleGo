#Requires -Version 5.1
<#
.SYNOPSIS
  Run the executor self-check (--self-check --json) and exit with its code.

.DESCRIPTION
  Runs: WhaleGo.Executor.exe --self-check --json [--config <path>]
  Prints the JSON report (pretty-printed when possible) and exits with the executor's exit code.
  Useful for after-install verification and for docs/evidence.

.PARAMETER Configuration
  Build configuration containing the executable (Release default).

.PARAMETER Config
  Optional config path (passed as --config <path>).

.PARAMETER Raw
  Print the raw JSON exactly as produced (do not pretty-print).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/self-check.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Config = '',

    [switch]$Raw
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'lib/WhaleGo.TestEnv.psm1') -Force
$exe = Join-Path $RepoRoot "windows/src/WhaleGo.Executor/bin/$Configuration/net10.0-windows/WhaleGo.Executor.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Executor not found: $exe. Build it first: powershell -File scripts/build.ps1"
    exit 1
}

$exeArgs = @('--self-check', '--json')
if (-not [string]::IsNullOrWhiteSpace($Config)) {
    $exeArgs += @('--config', (Resolve-WhaleGoRepoPath -RepoRoot $RepoRoot -Path $Config))
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("whalego-selfcheck-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$stdout = Join-Path $tmp 'self-check.out.json'
$stderr = Join-Path $tmp 'self-check.err.log'
try {
    Write-Host "==> WhaleGo.Executor.exe --self-check --json" -ForegroundColor Cyan
    $p = Start-Process -FilePath $exe -ArgumentList $exeArgs -WorkingDirectory $RepoRoot -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $json = if (Test-Path $stdout) { Get-Content $stdout -Raw } else { '' }
    if ([string]::IsNullOrWhiteSpace($json)) {
        Write-Host "SELF-CHECK produced no JSON (stderr follows):" -ForegroundColor Red
        if (Test-Path $stderr) { Get-Content $stderr -Tail 50 }
        exit $(if ($p.ExitCode -ne 0) { $p.ExitCode } else { 1 })
    }

    if (-not $Raw) {
        try { $json = ($json | ConvertFrom-Json | ConvertTo-Json -Depth 32) } catch { }
    }
    Write-Output $json
    Write-Host "SELF-CHECK exit=$($p.ExitCode)" -ForegroundColor $(if ($p.ExitCode -eq 0) { 'Green' } else { 'Red' })
    exit $p.ExitCode
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}