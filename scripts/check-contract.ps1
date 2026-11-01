#Requires -Version 5.1
<#
.SYNOPSIS
  CI contract gate: start the built executor headless on an isolated temp config/port, fetch MCP
  tools/list with a real mcp==1.30.0 Python client, and diff it against
  contracts/whalego.tools.v1.json (single source of truth).

.DESCRIPTION
  Asserts (see scripts/check-contract/contract_check.py):
    - 7 exact tool names;
    - descriptions equal;
    - inputSchema structurally equal after stripping x-internal properties (order-insensitive);
    - no session_id / request_id / scope in model-facing schemas;
    - contract_version == 1.3 / protocol_version == 1 (static, from the contract file);
    - optional -CheckHealth: go_health.protocol_version == 1 + accepting_actions present.
  Also sanity-checks that POST /mcp without a token is rejected with HTTP 401.

  Isolation: temp data dir via WHALEGO_DATA_DIR, temp config via --config, free TCP port. The real
  %APPDATA%/%LOCALAPPDATA% WhaleGo data is never touched.

  Requirements: built executor (scripts/build.ps1), uv (uv run --with mcp==1.30.0), network or a warm
  uv cache for the mcp package.

.PARAMETER Configuration
  Build configuration containing the executor (Release default).

.PARAMETER ExecutorPath
  Override the executor exe path.

.PARAMETER ContractsPath
  Override the contract JSON path (default contracts/whalego.tools.v1.json).

.PARAMETER Port
  Fixed port (default: auto-select a free loopback port).

.PARAMETER TimeoutSec
  Seconds to wait for /healthz (default 60).

.PARAMETER KeepTemp
  Keep the temp dir/process logs on exit (for debugging failures).

.PARAMETER CheckHealth
  Also assert go_health.protocol_version == 1 via a real tool call.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-contract.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-contract.ps1 -KeepTemp -CheckHealth
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$ExecutorPath = '',

    [string]$ContractsPath = '',

    [int]$Port = 0,

    [int]$TimeoutSec = 60,

    [switch]$KeepTemp,

    [switch]$CheckHealth
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'lib/WhaleGo.TestEnv.psm1') -Force

$exe = if ([string]::IsNullOrWhiteSpace($ExecutorPath)) {
    Join-Path $RepoRoot "windows/src/WhaleGo.Executor/bin/$Configuration/net10.0-windows/WhaleGo.Executor.exe"
} else {
    Resolve-WhaleGoRepoPath -RepoRoot $RepoRoot -Path $ExecutorPath
}
if (-not (Test-Path $exe)) {
    Write-Error "Executor not found: $exe. Build it first: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1"
    exit 1
}

$contracts = if ([string]::IsNullOrWhiteSpace($ContractsPath)) {
    Join-Path $RepoRoot 'contracts/whalego.tools.v1.json'
} else {
    Resolve-WhaleGoRepoPath -RepoRoot $RepoRoot -Path $ContractsPath
}
if (-not (Test-Path $contracts)) {
    Write-Error "Contract file not found: $contracts"
    exit 1
}

$uv = Get-Command uv -ErrorAction SilentlyContinue
if (-not $uv) {
    Write-Error "uv not found. Install uv (https://docs.astral.sh/uv/); the contract gate runs: uv run --no-project --with mcp==1.30.0 python scripts/check-contract/contract_check.py"
    exit 1
}
$pythonHelper = Join-Path $PSScriptRoot 'check-contract/contract_check.py'
if (-not (Test-Path $pythonHelper)) {
    Write-Error "Python helper missing: $pythonHelper"
    exit 1
}

if ($Port -le 0) { $Port = New-WhaleGoFreeTcpPort }
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("whalego-contract-" + [guid]::NewGuid().ToString('N'))
$tokenInfo = New-WhaleGoToken
$envObj = New-WhaleGoTestDataDir -Root $tempRoot -Port $Port -Token $tokenInfo.Token -Sha256 $tokenInfo.Sha256
$process = $null
$exitCode = 2

Write-Host "== WhaleGo contract check ==" -ForegroundColor Cyan
Write-Host "  repo      : $RepoRoot"
Write-Host "  executor  : $exe"
Write-Host "  contract  : $contracts"
Write-Host "  temp      : $tempRoot"
Write-Host "  port      : $Port"

try {
    $process = Start-WhaleGoHeadlessExecutor -ExePath $exe -ConfigPath $envObj.ConfigPath -DataDir $envObj.DataDir -LogDir $envObj.LogDir
    Write-Host "  pid       : $($process.Id)"
    Wait-WhaleGoHealth -Url "http://127.0.0.1:$Port/healthz" -TimeoutSec $TimeoutSec -Process $process | Out-Null
    Write-Host "  /healthz  : OK" -ForegroundColor Green

    $initializeBody = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"contract-check","version":"1.0"}}}'
    $unauthStatus = Get-WhaleGoHttpStatus -Uri "http://127.0.0.1:$Port/mcp" -Method Post -Body $initializeBody
    if ($unauthStatus -eq 401) {
        Write-Host "  /mcp no token : 401 (expected)" -ForegroundColor Green
    } else {
        Write-Warning "/mcp without token returned HTTP $unauthStatus (expected 401)"
    }

    $pyArgs = @(
        'run', '--no-project', '--with', 'mcp==1.30.0', 'python', $pythonHelper,
        '--url', "http://127.0.0.1:$Port/mcp",
        '--token', $envObj.Token,
        '--contracts', $contracts
    )
    if ($CheckHealth) { $pyArgs += '--check-health' }

    Write-Host ""
    & uv @pyArgs
    $exitCode = $LASTEXITCODE
} catch {
    Write-Host "CONTRACT CHECK ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 2
} finally {
    Stop-WhaleGoProcess -Process $process -Label 'executor'
    if ($exitCode -ne 0) {
        if (Test-Path $envObj.LogDir) {
            foreach ($logName in @('executor.stderr.log', 'executor.stdout.log')) {
                $logPath = Join-Path $envObj.LogDir $logName
                if ((Test-Path $logPath) -and ((Get-Item $logPath).Length -gt 0)) {
                    Write-Host "---- $logName (tail) ----" -ForegroundColor DarkYellow
                    Get-Content $logPath -Tail 30
                }
            }
        }
    }
    if ($KeepTemp) {
        Write-Host "temp kept: $tempRoot" -ForegroundColor Yellow
    } else {
        Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue
    }
}

Write-Host ""
if ($exitCode -eq 0) {
    Write-Host "CONTRACT CHECK PASSED" -ForegroundColor Green
} else {
    Write-Host "CONTRACT CHECK FAILED (exit $exitCode)" -ForegroundColor Red
}
exit $exitCode