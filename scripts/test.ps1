#Requires -Version 5.1
<#
.SYNOPSIS
  Run the WhaleGo test suite (four dotnet test projects, run SEQUENTIALLY) and the AstrBot plugin pytest suite.

.DESCRIPTION
  Desktop-interop test hosts must not run concurrently: Platform input tests steal/restore the foreground
  window and GUI STA tests create windows, so parallel test hosts interfere and flake
  (observed: Platform.Tests 110/111 once, green on immediate rerun). Therefore:
    1) dotnet build windows/WhaleGo.sln -c <Configuration>   (skipped with -NoBuild)
    2) for each project in a stable order Contracts -> Platform -> Executor -> Gui:
         dotnet test <project> -c <Configuration> --no-build --nologo
       (failures are collected; remaining projects still run so the summary is complete)
    3) AstrBot plugin pytest via an ephemeral uv env pinned to mcp==1.30.0 (unless -SkipPlugin / uv missing)
  Non-zero exit if any suite fails; per-project result + a failure summary are printed at the end.

.PARAMETER Configuration
  Build configuration (Release default).

.PARAMETER NoBuild
  Skip the build step (tests still pass --no-build, so a prior build is required).

.PARAMETER SkipPlugin
  Skip the AstrBot plugin pytest stage.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -NoBuild -SkipPlugin
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild,

    [switch]$SkipPlugin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $RepoRoot 'windows/WhaleGo.sln'
$failures = New-Object System.Collections.Generic.List[string]
$projects = @(
    [pscustomobject]@{ Name = 'WhaleGo.Contracts.Tests'; Path = 'windows/tests/WhaleGo.Contracts.Tests/WhaleGo.Contracts.Tests.csproj' },
    [pscustomobject]@{ Name = 'WhaleGo.Platform.Tests'; Path = 'windows/tests/WhaleGo.Platform.Tests/WhaleGo.Platform.Tests.csproj' },
    [pscustomobject]@{ Name = 'WhaleGo.Executor.Tests'; Path = 'windows/tests/WhaleGo.Executor.Tests/WhaleGo.Executor.Tests.csproj' },
    [pscustomobject]@{ Name = 'WhaleGo.Gui.Tests'; Path = 'windows/tests/WhaleGo.Gui.Tests/WhaleGo.Gui.Tests.csproj' }
)

if (-not $NoBuild) {
    Write-Host "==> dotnet build $Solution -c $Configuration" -ForegroundColor Cyan
    & dotnet build $Solution -c $Configuration -nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "BUILD FAILED (exit $LASTEXITCODE) - skipping tests." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# Sequential execution is mandatory: Platform input tests and GUI STA construction tests must not run
# in parallel test hosts (foreground/window interference -> flaky failures).
foreach ($project in $projects) {
    $projectPath = Join-Path $RepoRoot $project.Path
    if (-not (Test-Path $projectPath)) {
        $failures.Add($project.Name + ' (project file missing)')
        Write-Host ('FAIL  ' + $project.Name + ' (project file missing)') -ForegroundColor Red
        continue
    }

    Write-Host ('==> dotnet test ' + $project.Name + ' -c ' + $Configuration + ' --no-build') -ForegroundColor Cyan
    & dotnet test $projectPath -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) {
        $failures.Add($project.Name + ' (exit ' + $LASTEXITCODE + ')')
        Write-Host ('FAIL  ' + $project.Name + ' (exit ' + $LASTEXITCODE + ')') -ForegroundColor Red
    } else {
        Write-Host ('PASS  ' + $project.Name) -ForegroundColor Green
    }
}

if (-not $SkipPlugin) {
    $pluginDir = Join-Path $RepoRoot 'astrbot/astrbot_plugin_whalego'
    if (Test-Path $pluginDir) {
        $uv = Get-Command uv -ErrorAction SilentlyContinue
        if (-not $uv) {
            Write-Warning 'uv not found - skipping AstrBot plugin pytest. Install uv to run plugin tests.'
        } else {
            # Ephemeral uv env: works even when the plugin's own .venv exists without dev/test deps,
            # and pins the same mcp version as the AstrBot container (1.30.0).
            Write-Host '==> uv run --no-project --with pytest --with pytest-asyncio --with mcp==1.30.0 python -m pytest -q' -ForegroundColor Cyan
            Push-Location $pluginDir
            try {
                & uv run --no-project --with pytest --with pytest-asyncio --with mcp==1.30.0 python -m pytest -q
                if ($LASTEXITCODE -ne 0) { $failures.Add('plugin pytest (exit ' + $LASTEXITCODE + ')') }
            } finally {
                Pop-Location
            }
        }
    } else {
        Write-Warning 'Plugin directory not found (astrbot/astrbot_plugin_whalego) - skipping plugin pytest.'
    }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host 'TEST FAILED:' -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ('  - ' + $f) -ForegroundColor Red }
    exit 1
}

Write-Host 'TEST OK' -ForegroundColor Green
exit 0
