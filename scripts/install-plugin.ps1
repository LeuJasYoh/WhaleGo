#Requires -Version 5.1
<#
.SYNOPSIS
  Safely install/update the WhaleGo AstrBot plugin into the AstrBot data dir. NEVER restarts the container.

.DESCRIPTION
  The AstrBot container (default name: AstrBot) bind-mounts C:\Users\23500\data to /AstrBot/data.
  This script copies astrbot/astrbot_plugin_whalego either through the host bind mount (preferred) or
  via "docker cp" (when no host data dir is available). It does not touch container state, does not
  restart/stop/start the container; after installing, RELOAD the plugin in the AstrBot WebUI
  (plugin management -> reload); a full AstrBot restart is a user decision, not this script's.

.PARAMETER Source
  Plugin directory (default: astrbot/astrbot_plugin_whalego in this repo).

.PARAMETER Container
  Docker container name (default: AstrBot), used for -Mode Docker and for mount discovery.

.PARAMETER DataRoot
  Host path bind-mounted to /AstrBot/data (default: C:\Users\23500\data).

.PARAMETER Mode
  Auto (default): host copy when DataRoot exists, else docker cp.
  Copy: force host bind-mount copy. Docker: force docker cp.

.PARAMETER Force
  Delete the target directory before copying (clean update). Without -Force files are merged/overwritten.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-plugin.ps1 -WhatIf
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-plugin.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-plugin.ps1 -Mode Docker -Container AstrBot
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Source = '',
    [string]$Container = 'AstrBot',
    [string]$DataRoot = 'C:\Users\23500\data',
    [ValidateSet('Auto', 'Copy', 'Docker')]
    [string]$Mode = 'Auto',
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'lib/WhaleGo.TestEnv.psm1') -Force
if ([string]::IsNullOrWhiteSpace($Source)) { $Source = Join-Path $RepoRoot 'astrbot/astrbot_plugin_whalego' }
$Source = Resolve-WhaleGoRepoPath -RepoRoot $RepoRoot -Path $Source

if (-not (Test-Path $Source)) {
    Write-Error "Plugin source not found: $Source (expected astrbot/astrbot_plugin_whalego with main.py/metadata.yaml)."
    exit 1
}
if (-not (Test-Path (Join-Path $Source 'main.py'))) {
    Write-Error "Plugin source looks incomplete: missing main.py in $Source"
    exit 1
}
$pluginName = Split-Path -Leaf $Source
if ($pluginName -ne 'astrbot_plugin_whalego') {
    Write-Warning "Source directory is '$pluginName' (expected 'astrbot_plugin_whalego'); the install dir will use the source leaf name."
}

$hostDataUsable = (Test-Path $DataRoot) -and (Test-Path (Join-Path $DataRoot 'plugins'))
if ($Mode -eq 'Auto') {
    if ($hostDataUsable) { $Mode = 'Copy' } else { $Mode = 'Docker' }
}

$targetDir = Join-Path (Join-Path $DataRoot 'plugins') $pluginName
$containerTarget = "/AstrBot/data/plugins/$pluginName"

Write-Host "==> WhaleGo plugin install" -ForegroundColor Cyan
Write-Host "    source    : $Source"
Write-Host "    mode      : $Mode"
Write-Host "    host path : $targetDir"
Write-Host "    container : $containerTarget"

if ($Mode -eq 'Copy') {
    if (-not $hostDataUsable) {
        Write-Error "Host data dir not usable: $DataRoot (missing or no plugins folder). Use -DataRoot <bind-mount-root> or -Mode Docker."
        exit 1
    }
    if ($Force -and (Test-Path $targetDir)) {
        if ($PSCmdlet.ShouldProcess($targetDir, 'Remove existing plugin directory (clean update)')) {
            Remove-Item -Recurse -Force $targetDir
        }
    }
    if ($PSCmdlet.ShouldProcess($targetDir, "Copy plugin from $Source")) {
        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
        # Copy everything except local dev/build artifacts that must not ship.
        $rcArgs = @($Source, $targetDir, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/XD', '.venv', '__pycache__', '.pytest_cache', '.ruff_cache', 'tests')
        & robocopy @rcArgs | Out-Null
        $rc = $LASTEXITCODE
        if ($rc -ge 8) {
            Write-Error "robocopy failed with exit code $rc (<8 means success)."
            exit 1
        }
    }
} else {
    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if (-not $docker) { Write-Error "docker CLI not found but -Mode Docker was selected."; exit 1 }
    $running = (& docker ps --filter "name=^/$Container$" --format '{{.Names}}') 2>$null
    if ($LASTEXITCODE -ne 0) { Write-Error "docker ps failed (is Docker Desktop running?)"; exit 1 }
    if (-not $running) {
        Write-Error "Container '$Container' is not running. This script never starts/stops containers; start it yourself, then re-run."
        exit 1
    }
    if ($Force -and $PSCmdlet.ShouldProcess($containerTarget, "docker exec rm -rf (clean update)")) {
        & docker exec $Container rm -rf $containerTarget
        if ($LASTEXITCODE -ne 0) { Write-Error "docker exec rm failed"; exit 1 }
    }
    if ($PSCmdlet.ShouldProcess($containerTarget, "docker cp plugin")) {
        $srcWithDot = Join-Path $Source '.'
        & docker exec $Container mkdir -p $containerTarget
        if ($LASTEXITCODE -ne 0) { Write-Error "docker exec mkdir failed"; exit 1 }
        & docker cp $srcWithDot ($Container + ':' + $containerTarget)
        if ($LASTEXITCODE -ne 0) { Write-Error "docker cp failed"; exit 1 }
    }
}

Write-Host ""
if ($WhatIfPreference) {
    Write-Host "DRY RUN only (-WhatIf): no files were copied and the container was not touched." -ForegroundColor Yellow
    exit 0
}
Write-Host "PLUGIN INSTALLED (container NOT restarted; only plugin files were written)." -ForegroundColor Green
Write-Host "  host path : $targetDir"
Write-Host "  container : $containerTarget"
Write-Host ""
Write-Host "NEXT STEP (user action):" -ForegroundColor Yellow
Write-Host "  1. Open AstrBot WebUI -> plugin management -> find 'WhaleGo' -> reload."
Write-Host "     If reload does not pick up new modules, restart AstrBot yourself (this script never restarts it)."
Write-Host "  2. In the plugin config set url=http://host.docker.internal:8765/mcp and token=wg_... (from the"
Write-Host "     Windows settings window 'copy plugin config'), allow_screenshot=true, admin_only=true."
Write-Host "  3. Verify in chat: /whalego health"
exit 0