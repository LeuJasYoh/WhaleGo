#Requires -Version 5.1
<#
.SYNOPSIS
  Start the WhaleGo Windows executor (manual start; nothing auto-starts it).

.DESCRIPTION
  Starts windows/src/WhaleGo.Executor/bin/<Configuration>/net10.0-windows/WhaleGo.Executor.exe.
  Background-friendly: the process starts detached, the script prints PID/logs/health URL and returns.
  Default: GUI mode (tray + WPF settings window). Pass -Headless for CI/E2E headless mode.
  The script never kills an already running instance (the single-instance mutex makes a second start exit 2).

.PARAMETER Configuration
  Build configuration containing the executable (Release default).

.PARAMETER Headless
  Pass --headless (no tray/settings window; required for CI/E2E).

.PARAMETER Port
  Override listen.port via the WHALEGO_LISTEN__PORT config env var (D14 env override convention).
  Use -Config instead if the executor build does not support that override yet.

.PARAMETER Config
  Path to config.json (passed as --config <path>). Default: the executor generates/uses its data dir config.

.PARAMETER NoHotkey
  Pass --no-hotkey (do not register global hotkeys).

.PARAMETER WaitReadySec
  When -Headless, wait up to N seconds for /healthz to return 200 (default 0 = do not wait).

.PARAMETER LogDir
  Directory for redirected stdout/stderr (default .tmp/run-executor).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-executor.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-executor.ps1 -Headless -Port 8765 -NoHotkey -WaitReadySec 30
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Headless,

    [int]$Port = 0,

    [string]$Config = '',

    [switch]$NoHotkey,

    [int]$WaitReadySec = 0,

    [string]$LogDir = ''
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

if ([string]::IsNullOrWhiteSpace($LogDir)) { $LogDir = Join-Path $RepoRoot '.tmp/run-executor' }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$exeArgs = @()
if ($Headless) { $exeArgs += '--headless' }
if ($NoHotkey) { $exeArgs += '--no-hotkey' }
if ($Port -gt 0) { $exeArgs += @('--port', [string]$Port) }
if (-not [string]::IsNullOrWhiteSpace($Config)) {
    $configPath = Resolve-WhaleGoRepoPath -RepoRoot $RepoRoot -Path $Config
    $exeArgs += @('--config', $configPath)
}

$stdout = Join-Path $LogDir 'executor.stdout.log'
$stderr = Join-Path $LogDir 'executor.stderr.log'

# Start the exe directly (no cmd.exe wrapper): the printed PID is the executor PID, so callers can stop it.
$startInfo = @{
    FilePath               = $exe
    ArgumentList           = $exeArgs
    WorkingDirectory       = $RepoRoot
    RedirectStandardOutput = $stdout
    RedirectStandardError  = $stderr
    WindowStyle            = 'Hidden'
    PassThru               = $true
}

Write-Host "==> starting WhaleGo.Executor.exe $(if ($Headless) { '(--headless)' } else { '(GUI)' })" -ForegroundColor Cyan
$process = Start-Process @startInfo
Start-Sleep -Milliseconds 300
$process.Refresh()

Write-Host "    pid      : $($process.Id)$(if ($process.HasExited) { ' (EXITED code=' + $process.ExitCode + ')' })"
Write-Host "    stdout   : $stdout"
Write-Host "    stderr   : $stderr"
if ($Port -gt 0) {
    Write-Host "    healthz  : http://127.0.0.1:$Port/healthz"
} else {
    Write-Host "    healthz  : http://127.0.0.1:8765/healthz (default; see config listen.port)"
}

if ($Headless -and $WaitReadySec -gt 0) {
    $healthUrl = if ($Port -gt 0) { "http://127.0.0.1:$Port/healthz" } else { 'http://127.0.0.1:8765/healthz' }
    $deadline = (Get-Date).AddSeconds($WaitReadySec)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            Write-Host "FAILED: executor exited early with code $($process.ExitCode)" -ForegroundColor Red
            if (Test-Path $stderr) { Get-Content $stderr -Tail 30 | ForEach-Object { Write-Host "  $_" } }
            exit 1
        }
        try {
            $resp = Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -TimeoutSec 2
            if ($resp.StatusCode -eq 200) { $ready = $true; break }
        } catch {
            Start-Sleep -Milliseconds 300
        }
    }
    if (-not $ready) {
        Write-Host "FAILED: /healthz not ready within $WaitReadySec s" -ForegroundColor Red
        exit 1
    }
    Write-Host "    /healthz : OK" -ForegroundColor Green
}

Write-Host "Executor start requested (manual start; no auto-start)." -ForegroundColor Green
exit 0