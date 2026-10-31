#Requires -Version 5.1
<#
.SYNOPSIS
  WhaleGo end-to-end acceptance: two real headless executors (normal + policy.readonly) on isolated temp
  data dirs, driven by the real mcp==1.30.0 Python client (scripts/e2e/mcp_e2e.py).

.DESCRIPTION
  Starts:
    - normal executor on a free port with a temp config (screenshot enabled, fixture in the launch allowlist);
    - readonly executor on a second free port with policy.readonly=true.
  Then runs scripts/e2e/mcp_e2e.py via "uv run --no-project --with mcp==1.30.0 python ...", which drives
  the real MCP Streamable HTTP endpoint and asserts the DESIGN section 11 oracle cases:
    /healthz 200; /mcp no token 401; 7 tools; go_health protocol_version=1 + accepting_actions;
    go_apps/go_windows; go_launch allowlist fixture; go_observe tree+PNG; no automatic image;
    go_act set_value/type/click + readback oracles; STALE_STATE after acting with an old state_id;
    go_stop -> SESSION_STOPPED; observe still works after stop; READONLY_MODE; CONTROLLER_BUSY (2 sessions).
  Prints PASS/FAIL per case and exits non-zero if any case failed or a setup step failed.
  Always cleans up: executor processes, fixture processes started by this run (via .tmp/e2e marker), temp dirs.

.PARAMETER Configuration
  Build configuration containing the executor (Release default).

.PARAMETER ExecutorPath
  Override the executor exe path.

.PARAMETER FixturePath
  Override the WinForms fixture exe path.

.PARAMETER ContractsPath
  Override contracts/whalego.tools.v1.json path.

.PARAMETER Port
  Normal executor port (default: auto free port). -ReadonlyPort likewise for the readonly executor.

.PARAMETER HealthTimeoutSec
  Seconds to wait for each executor's /healthz (default 60).

.PARAMETER PythonTimeoutSec
  Hard timeout for the E2E driver (default 600).

.PARAMETER KeepTemp
  Keep temp configs/logs for debugging (paths are printed).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/e2e.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/e2e.ps1 -KeepTemp
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$ExecutorPath = '',

    [string]$FixturePath = '',

    [string]$ContractsPath = '',

    [int]$Port = 0,

    [int]$ReadonlyPort = 0,

    [int]$HealthTimeoutSec = 60,

    [int]$PythonTimeoutSec = 600,

    [switch]$KeepTemp
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
$fixture = if ([string]::IsNullOrWhiteSpace($FixturePath)) {
    Join-Path $RepoRoot "windows/tests/WhaleGo.Fixtures.WinForms/bin/$Configuration/net10.0-windows/WhaleGo.Fixtures.WinForms.exe"
} else {
    Resolve-WhaleGoRepoPath -RepoRoot $RepoRoot -Path $FixturePath
}
$contracts = if ([string]::IsNullOrWhiteSpace($ContractsPath)) {
    Join-Path $RepoRoot 'contracts/whalego.tools.v1.json'
} else {
    Resolve-WhaleGoRepoPath -RepoRoot $RepoRoot -Path $ContractsPath
}
if (-not (Test-Path $exe)) {
    Write-Error "Executor not found: $exe. Build it first: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1"
    exit 1
}
if (-not (Test-Path $fixture)) {
    Write-Error "WinForms fixture not found: $fixture. Build it first: dotnet build windows/tests/WhaleGo.Fixtures.WinForms -c $Configuration"
    exit 1
}
if (-not (Test-Path $contracts)) {
    Write-Error "Contract file not found: $contracts"
    exit 1
}
$uv = Get-Command uv -ErrorAction SilentlyContinue
if (-not $uv) {
    Write-Error "uv not found. Install uv (https://docs.astral.sh/uv/); E2E runs: uv run --no-project --with mcp==1.30.0 python scripts/e2e/mcp_e2e.py"
    exit 1
}
$driver = Join-Path $PSScriptRoot 'e2e/mcp_e2e.py'
if (-not (Test-Path $driver)) {
    Write-Error "E2E driver missing: $driver"
    exit 1
}

if ($Port -le 0) { $Port = New-WhaleGoFreeTcpPort }
if ($ReadonlyPort -le 0) { $ReadonlyPort = New-WhaleGoFreeTcpPort }
while ($ReadonlyPort -eq $Port) { $ReadonlyPort = New-WhaleGoFreeTcpPort }

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("whalego-e2e-" + [guid]::NewGuid().ToString('N'))
$normalRoot = Join-Path $tempRoot 'normal'
$readonlyRoot = Join-Path $tempRoot 'readonly'
New-Item -ItemType Directory -Force -Path $normalRoot, $readonlyRoot | Out-Null

$normalTokenInfo = New-WhaleGoToken
$readonlyTokenInfo = New-WhaleGoToken
$normalEnv = New-WhaleGoTestDataDir -Root $normalRoot -Port $Port -Token $normalTokenInfo.Token -Sha256 $normalTokenInfo.Sha256 -FixtureExe $fixture
$readonlyEnv = New-WhaleGoTestDataDir -Root $readonlyRoot -Port $ReadonlyPort -Token $readonlyTokenInfo.Token -Sha256 $readonlyTokenInfo.Sha256 -FixtureExe $fixture -Readonly

$normalProcess = $null
$readonlyProcess = $null
$normalPid = 0
$readonlyPid = 0
$setupFailures = New-Object System.Collections.Generic.List[string]
$exitCode = 2

Write-Host "== WhaleGo E2E ==" -ForegroundColor Cyan
Write-Host "  repo       : $RepoRoot"
Write-Host "  executor   : $exe"
Write-Host "  fixture    : $fixture"
Write-Host "  contracts  : $contracts"
Write-Host "  temp       : $tempRoot"
Write-Host "  ports      : normal=$Port readonly=$ReadonlyPort"

function Invoke-WhaleGoE2EPhase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$PyArgs
    )
    $outFile = Join-Path $tempRoot ($Label + '.stdout.log')
    $errFile = Join-Path $tempRoot ($Label + '.stderr.log')
    Write-Host ""
    Write-Host "==> uv run --no-project --with mcp==1.30.0 python scripts/e2e/mcp_e2e.py ($Label)" -ForegroundColor Cyan
    $uvProcess = Start-Process -FilePath $uv.Source -ArgumentList $PyArgs -WorkingDirectory $RepoRoot -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile -WindowStyle Hidden
    $finished = $uvProcess.WaitForExit($PythonTimeoutSec * 1000)
    if (-not $finished) {
        Write-Host "    E2E driver timed out after $PythonTimeoutSec s - killing pid $($uvProcess.Id)" -ForegroundColor Red
        Stop-Process -Id $uvProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $outFile) { Get-Content $outFile | ForEach-Object { Write-Host $_ } }
    if ((Test-Path $errFile) -and ((Get-Item $errFile).Length -gt 0)) {
        Write-Host "---- uv stderr ($Label) ----" -ForegroundColor DarkYellow
        Get-Content $errFile | ForEach-Object { Write-Host $_ }
    }
    if (-not $finished) { return 1 }
    if ($uvProcess.ExitCode -ne 0) { return 1 }
    return 0
}

try {
    Write-Host ""
    Write-Host "==> starting normal executor (port $Port)" -ForegroundColor Cyan
    $normalProcess = Start-WhaleGoHeadlessExecutor -ExePath $exe -ConfigPath $normalEnv.ConfigPath -DataDir $normalEnv.DataDir -LogDir $normalEnv.LogDir
    $normalPid = $normalProcess.Id
    Write-Host "    pid=$normalPid"
    Wait-WhaleGoHealth -Url "http://127.0.0.1:$Port/healthz" -TimeoutSec $HealthTimeoutSec -Process $normalProcess | Out-Null
    Write-Host "    /healthz OK" -ForegroundColor Green

    $normalArgs = @(
        'run', '--no-project', '--with', 'mcp==1.30.0', 'python', $driver,
        '--url', "http://127.0.0.1:$Port/mcp",
        '--token', $normalEnv.Token,
        '--fixture', $fixture,
        '--contracts', $contracts
    )
    $normalExit = Invoke-WhaleGoE2EPhase -Label 'e2e-normal' -PyArgs $normalArgs

    # D-1: action audit must carry input length + SHA-256 prefix (no plaintext). The E2E driver writes:
    #   set_value 'e2e-set-value' (14) / type 'e2e-type-42' (11) / set_value 500-char A*100+'read-after-100'+B*386.
    Start-Sleep -Milliseconds 500
    $longText = ('A' * 100) + 'read-after-100' + ('B' * 386)
    $expectedTextActions = @(
        @{ op = 'type'; length = 11; sha = (Get-WhaleGoSha256Hex -Text 'e2e-type-42').Substring(0, 8) },
        @{ op = 'set_value'; length = 13; sha = (Get-WhaleGoSha256Hex -Text 'e2e-set-value').Substring(0, 8) },
        @{ op = 'set_value'; length = 500; sha = (Get-WhaleGoSha256Hex -Text $longText).Substring(0, 8) }
    )
    $auditResult = Invoke-WhaleGoAuditAssertions -AuditDir $normalEnv.LogDir -Token $normalEnv.Token -KnownPlaintexts @('e2e-type-42', 'e2e-set-value', 'read-after-100') -ExpectedTextActions $expectedTextActions -Label 'normal executor'
    if ($auditResult.Failed -gt 0) { $exitCode = 1 }

    # Headless executors share a per-interactive-session single-instance mutex, so the readonly
    # executor must run strictly after the normal one has exited (sequential phases, not parallel).
    Stop-WhaleGoProcess -Process $normalProcess -Label 'normal executor'
    $normalProcess = $null
    Start-Sleep -Milliseconds 500

    Write-Host ""
    Write-Host "==> starting readonly executor (port $ReadonlyPort)" -ForegroundColor Cyan
    try {
        $readonlyProcess = Start-WhaleGoHeadlessExecutor -ExePath $exe -ConfigPath $readonlyEnv.ConfigPath -DataDir $readonlyEnv.DataDir -LogDir $readonlyEnv.LogDir -StdOutName 'executor.ro.stdout.log' -StdErrName 'executor.ro.stderr.log'
        $readonlyPid = $readonlyProcess.Id
        Write-Host "    pid=$readonlyPid"
        Wait-WhaleGoHealth -Url "http://127.0.0.1:$ReadonlyPort/healthz" -TimeoutSec $HealthTimeoutSec -Process $readonlyProcess | Out-Null
        Write-Host "    /healthz OK" -ForegroundColor Green

        $readonlyArgs = @(
            'run', '--no-project', '--with', 'mcp==1.30.0', 'python', $driver,
            '--readonly-only',
            '--readonly-url', "http://127.0.0.1:$ReadonlyPort/mcp",
            '--readonly-token', $readonlyEnv.Token,
            '--fixture', $fixture,
            '--contracts', $contracts
        )
        $readonlyExit = Invoke-WhaleGoE2EPhase -Label 'e2e-readonly' -PyArgs $readonlyArgs
        if ($readonlyExit -ne 0) { $exitCode = 1 }
    } catch {
        $setupFailures.Add("readonly executor phase failed: $($_.Exception.Message)")
        Write-Host "    READONLY EXECUTOR FAILED: $($_.Exception.Message)" -ForegroundColor Red
        if (Test-Path (Join-Path $readonlyEnv.LogDir 'executor.ro.stderr.log')) {
            Get-Content (Join-Path $readonlyEnv.LogDir 'executor.ro.stderr.log') -Tail 20 | ForEach-Object { Write-Host "      $_" }
        }
        $readonlyProcess = $null
    }

    # Aggregate phase results (evidence-friendly totals).
    $totalPassed = 0; $totalFailed = 0; $totalSkipped = 0
    foreach ($phaseLog in @((Join-Path $tempRoot 'e2e-normal.stdout.log'), (Join-Path $tempRoot 'e2e-readonly.stdout.log'))) {
        if (-not (Test-Path $phaseLog)) { continue }
        $resultLine = (Get-Content $phaseLog | Select-String -Pattern '^RESULT: (\d+) passed, (\d+) failed, (\d+) skipped').Matches
        if ($resultLine -and $resultLine.Count -gt 0) {
            $totalPassed += [int]$resultLine[0].Groups[1].Value
            $totalFailed += [int]$resultLine[0].Groups[2].Value
            $totalSkipped += [int]$resultLine[0].Groups[3].Value
        }
    }
    Write-Host ""
    Write-Host ("TOTAL: {0} passed, {1} failed, {2} skipped" -f $totalPassed, $totalFailed, $totalSkipped) -ForegroundColor Cyan
    Write-Host ("phase-exits: normal={0} readonly={1}" -f ($normalExit -join ','), ($readonlyExit -join ',') ) -ForegroundColor DarkGray

    # Final verdict is derived from parsed phase results (robust against function return-shape surprises).
    if ($totalFailed -gt 0 -or $totalPassed -eq 0) { $exitCode = 1 } else { $exitCode = 0 }
} catch {
    Write-Host "E2E SETUP ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $setupFailures.Add($_.Exception.Message)
    $exitCode = 2
} finally {
    Stop-WhaleGoProcess -Process $normalProcess -Label 'normal executor'
    Stop-WhaleGoProcess -Process $readonlyProcess -Label 'readonly executor'
    # Kill fixture processes started by this run (executor-launched children and temp-dir markers).
    try {
        $executorPids = @()
        if ($normalPid -gt 0) { $executorPids += $normalPid }
        if ($readonlyPid -gt 0) { $executorPids += $readonlyPid }
        $fixtureProcesses = Get-CimInstance Win32_Process -Filter "Name = 'WhaleGo.Fixtures.WinForms.exe'" -ErrorAction SilentlyContinue |
            Where-Object {
                ($executorPids.Count -gt 0 -and $executorPids -contains $_.ParentProcessId) -or
                ($_.CommandLine -and $_.CommandLine.Contains($tempRoot))
            }
        foreach ($fp in $fixtureProcesses) {
            Stop-Process -Id $fp.ProcessId -Force -ErrorAction SilentlyContinue
        }
    } catch { }
    foreach ($failure in $setupFailures) { Write-Host "SETUP FAILURE: $failure" -ForegroundColor Red }
    if ($setupFailures.Count -gt 0 -and $exitCode -eq 0) { $exitCode = 1 }
    if ($KeepTemp) {
        Write-Host "temp kept: $tempRoot" -ForegroundColor Yellow
    } else {
        Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue
    }
}

Write-Host ""
if ($exitCode -eq 0) {
    Write-Host "E2E PASSED" -ForegroundColor Green
} else {
    Write-Host "E2E FAILED (exit $exitCode)" -ForegroundColor Red
}
exit $exitCode