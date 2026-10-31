#Requires -Version 5.1
<#
.SYNOPSIS
  Capture Light/Dark screenshots of the WhaleGo WPF settings window (GUI evidence, P1 optional).

.DESCRIPTION
  STATUS: documented stub (T7 GUI not implemented yet at the time this script was written).
  When WhaleGo.Gui is ready this script will:
    1. start the executor in GUI mode (or launch the settings window via the ShowSettings event),
    2. switch ui.theme to light, capture the settings window PNG,
    3. switch ui.theme to dark, capture the settings window PNG,
    4. write docs/evidence/gui-theme-light.png / gui-theme-dark.png (+ a short md note).
  Capture uses PrintWindow of the settings window (no third-party screenshot tooling). The window is a
  WPF window; use the GUI process's own HWND looked up by title "WhaleGo 设置".

  As long as the GUI is not implemented, this script prints the plan, checks prerequisites and exits 0
  with status "SKIPPED" so CI does not fail on a not-yet-existing P1 evidence item.

.PARAMETER OutputDir
  Where to write the PNGs (default docs/evidence).

.PARAMETER Configuration
  Build configuration (Release default).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/gui-theme-shots.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir = '',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $RepoRoot 'docs/evidence' }

$guiDll = Join-Path $RepoRoot "windows/src/WhaleGo.Gui/bin/$Configuration/net10.0-windows/WhaleGo.Gui.dll"
$exe = Join-Path $RepoRoot "windows/src/WhaleGo.Executor/bin/$Configuration/net10.0-windows/WhaleGo.Executor.exe"

Write-Host "== WhaleGo GUI theme screenshots (P1, optional) ==" -ForegroundColor Cyan
Write-Host "  output dir : $OutputDir"
Write-Host "  gui dll    : $guiDll"
Write-Host "  executor   : $exe"

if (-not (Test-Path $exe)) {
    Write-Host "SKIPPED: executor not built (run scripts/build.ps1 first)." -ForegroundColor Yellow
    exit 0
}
# The WPF settings window + IWindowTheme hot-switch is T7 scope. Until its public GuiShell is functional
# (non-stub), theme screenshot automation cannot be reliable, so we leave this documented stub.
$guiShell = Join-Path $RepoRoot 'windows/src/WhaleGo.Gui/GuiShell.cs'
$isStub = $false
if (Test-Path $guiShell) {
    $content = Get-Content $guiShell -Raw
    if ($content -match 'SCAFFOLD') { $isStub = $true }
}
if ($isStub) {
    Write-Host "SKIPPED: WhaleGo.Gui is still a scaffold (SCAFFOLD marker in GuiShell.cs)." -ForegroundColor Yellow
    Write-Host "  Plan (implement when T7 lands):" -ForegroundColor Yellow
    Write-Host "    1. Start executor GUI mode; open settings window (tray menu / ShowSettings)."
    Write-Host "    2. Set ui.theme=light via the settings window; capture HWND 'WhaleGo 设置' with PrintWindow -> $OutputDir/gui-theme-light.png"
    Write-Host "    3. Set ui.theme=dark; capture -> $OutputDir/gui-theme-dark.png"
    Write-Host "    4. Append a short evidence note to docs/evidence/implementation-e2e.md."
    exit 0
}

# Implementation placeholder (deliberately not silently pretending to pass):
Write-Error "gui-theme-shots.ps1: GuiShell is no longer a scaffold, but the capture implementation has not been written yet. Implement PrintWindow-based capture here (see docs/GUI.md 10.7/12.2) and rerun."
exit 1
