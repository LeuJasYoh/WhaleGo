<#
.SYNOPSIS
  Shared helpers for WhaleGo test scripts (check-contract.ps1 / e2e.ps1).

.DESCRIPTION
  Creates isolated temp data dirs + config/secrets files, generates wg_ tokens, starts a headless
  executor on a free port (WHALEGO_DATA_DIR isolation) and waits for /healthz. Nothing here touches
  the real %APPDATA%/%LOCALAPPDATA% WhaleGo data dirs.
#>

Set-StrictMode -Version Latest

function New-WhaleGoFreeTcpPort {
    [CmdletBinding()]
    param()
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

function New-WhaleGoToken {
    [CmdletBinding()]
    param()
    $bytes = New-Object byte[] 32
    # RandomNumberGenerator.Fill() is .NET Core only; Windows PowerShell 5.1 (.NET Framework 4.8) needs a stream.
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $encoded = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $token = 'wg_' + $encoded
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($token))
    } finally {
        $sha.Dispose()
    }
    $sha256Hex = -join ($hash | ForEach-Object { $_.ToString('x2') })
    return [pscustomobject]@{ Token = $token; Sha256 = $sha256Hex }
}

function Get-WhaleGoSha256Hex {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text))
    } finally {
        $sha.Dispose()
    }
    return -join ($hash | ForEach-Object { $_.ToString('x2') })
}

function Invoke-WhaleGoAuditAssertions {
    <#
      Verify durable action-audit metadata for D-1: type/set_value action records must carry
      text_length (>0, Unicode rune count) + text_sha256_prefix (8 lowercase hex), while the raw
      audit file must never contain input plaintext or the token. Prints PASS/FAIL lines and returns
      an object with Passed/Failed counts.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$AuditDir,
        [string]$Token = '',
        [string[]]$KnownPlaintexts = @(),
        [hashtable[]]$ExpectedTextActions = @(),
        [string]$Label = 'audit'
    )
    $passed = 0
    $failed = 0

    function Add-Result([bool]$Ok, [string]$Name, [string]$Detail = '') {
        if ($Ok) {
            $script:__wgAuditPassed++
            Write-Host ("PASS  " + $Name + $(if ($Detail) { "  [" + $Detail + "]" } else { '' }))
        } else {
            $script:__wgAuditFailed++
            Write-Host ("FAIL  " + $Name + $(if ($Detail) { "  [" + $Detail + "]" } else { '' }))
        }
    }
    $script:__wgAuditPassed = 0
    $script:__wgAuditFailed = 0

    Write-Host ''
    Write-Host ("==> audit evidence checks (" + $Label + ")") -ForegroundColor Cyan
    $auditFile = Get-ChildItem -Path (Join-Path $AuditDir 'audit-*.jsonl') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $auditFile) {
        Add-Result $false "audit: audit-*.jsonl exists under $AuditDir"
        return [pscustomobject]@{ Passed = $script:__wgAuditPassed; Failed = $script:__wgAuditFailed }
    }
    Add-Result $true 'audit: audit-*.jsonl exists' $auditFile.Name

    $rawLines = @(Get-Content -Path $auditFile.FullName)
    $records = New-Object System.Collections.Generic.List[object]
    $parseErrors = 0
    foreach ($line in $rawLines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $records.Add(($line | ConvertFrom-Json)) } catch { $parseErrors++ }
    }
    Add-Result ($parseErrors -eq 0 -and $records.Count -gt 0) 'audit: every line is valid JSON' ("lines=" + $records.Count + " parseErrors=" + $parseErrors)

    $actions = @($records | Where-Object { $_.kind -eq 'action' })
    Add-Result ($actions.Count -gt 0) 'audit: action records present' ("actions=" + $actions.Count)

    $leaks = 0
    foreach ($plain in $KnownPlaintexts) {
        if ($plain -and $rawLines -match [regex]::Escape($plain)) { $leaks++ }
    }
    Add-Result ($leaks -eq 0) 'audit: no input plaintext in raw audit file' ("plaintextsChecked=" + $KnownPlaintexts.Count)

    $tokenLeak = $false
    if ($Token) { $tokenLeak = [bool]($rawLines -match [regex]::Escape($Token)) }
    $bearerLeak = [bool]($rawLines -match 'Bearer ')
    Add-Result (-not $tokenLeak -and -not $bearerLeak) 'audit: no token / Authorization material' ''

    $textActions = @($actions | Where-Object { $_.op -eq 'type' -or $_.op -eq 'set_value' })
    $badMeta = New-Object System.Collections.Generic.List[string]
    foreach ($action in $textActions) {
        $length = $action.text_length
        $prefix = [string]$action.text_sha256_prefix
        $lengthOk = ($null -ne $length) -and ([int]$length -gt 0)
        $prefixOk = $prefix -match '^[0-9a-f]{8}$'
        if (-not ($lengthOk -and $prefixOk)) {
            $badMeta.Add(("op=" + $action.op + " text_length=" + $length + " prefix=" + $prefix))
        }
    }
    Add-Result ($textActions.Count -gt 0 -and $badMeta.Count -eq 0) 'audit: every type/set_value action has text_length>0 + 8-hex text_sha256_prefix' ($(if ($badMeta.Count -gt 0) { "bad=[" + ($badMeta -join '; ') + "]" } else { "records=" + $textActions.Count }))

    foreach ($expected in $ExpectedTextActions) {
        $match = @($textActions | Where-Object {
            $_.op -eq $expected.op -and
            $null -ne $_.text_length -and [int]$_.text_length -eq [int]$expected.length -and
            [string]$_.text_sha256_prefix -eq [string]$expected.sha
        })
        $name = "audit: " + $expected.op + " record length=" + $expected.length + " sha_prefix=" + $expected.sha
        Add-Result ($match.Count -gt 0) $name ''
    }

    Write-Host ("AUDIT ASSERTIONS: {0} passed, {1} failed" -f $script:__wgAuditPassed, $script:__wgAuditFailed) -ForegroundColor $(if ($script:__wgAuditFailed -eq 0) { 'Green' } else { 'Red' })
    return [pscustomobject]@{ Passed = $script:__wgAuditPassed; Failed = $script:__wgAuditFailed }
}

function Resolve-WhaleGoRepoPath {
    <# Resolve a repo-relative path to an absolute path (compatible with Windows PowerShell 5.1 / .NET Framework 4.8, where Path.GetFullPath(path, basePath) does not exist). #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$Path = ''
    )
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Write-WhaleGoUtf8File {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8)
}

function New-WhaleGoTestDataDir {
    <#
      Creates an isolated executor data dir:
        <Root>/config.json        full local config (port, token hash, audit/log dirs, allowlist)
        <Root>/data/secrets.json  plaintext token (test format documented by T4)
        <Root>/logs               audit/log directory
      Returns an object with Root, ConfigPath, DataDir, LogDir, Token, Port.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][string]$Sha256,
        [switch]$Readonly,
        [string]$FixtureExe = ''
    )

    $dataDir = Join-Path $Root 'data'
    $logDir = Join-Path $Root 'logs'
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $configPath = Join-Path $Root 'config.json'
    $createdAt = (Get-Date).ToString('o')

    $allowlist = @(
        [ordered]@{ name = 'notepad'; path = 'C:\Windows\System32\notepad.exe' }
    )
    if (-not [string]::IsNullOrWhiteSpace($FixtureExe)) {
        $allowlist += [ordered]@{ name = 'fixture'; path = $FixtureExe }
    }

    $config = [ordered]@{
        listen     = [ordered]@{ address = '127.0.0.1'; port = $Port; healthzRequireToken = $false }
        auth       = [ordered]@{ tokens = @([ordered]@{ name = 'astrbot'; sha256 = $Sha256; createdAt = $createdAt }) }
        policy     = [ordered]@{
            readonly         = [bool]$Readonly
            actRatePerMinute = 120
            controllerWaitMs = 2000
            stateTtlSeconds  = 120
            frameTtlSeconds  = 60
        }
        limits     = [ordered]@{
            maxObserveElements = 300
            maxObserveChars    = 20000
            maxImageWidth      = 1024
            maxImageBytes      = 200000
            toolTimeoutSeconds = 40
        }
        screenshot = [ordered]@{ enabled = $true; mode = 'printwindow'; defaultInclude = $false }
        privacy    = [ordered]@{ maskPasswordFields = $true; sensitiveWindows = @('*password*', '*bank*') }
        audit      = [ordered]@{ enabled = $true; dir = $logDir; requireDurableForActions = $true }
        hotkeys    = [ordered]@{ enabled = $false; emergencyStop = $null; openSettings = $null }
        ui         = [ordered]@{ notificationsLevel = 'important'; theme = 'system' }
        logging    = [ordered]@{ level = 'information'; dir = $logDir }
        launch     = [ordered]@{ allowlist = $allowlist }
    }

    $configJson = $config | ConvertTo-Json -Depth 12
    Write-WhaleGoUtf8File -Path $configPath -Content $configJson

    $secrets = [ordered]@{
        tokens = @([ordered]@{ name = 'astrbot'; token = $Token; createdAt = $createdAt })
    }
    Write-WhaleGoUtf8File -Path (Join-Path $dataDir 'secrets.json') -Content ($secrets | ConvertTo-Json -Depth 5)

    return [pscustomobject]@{
        Root       = $Root
        ConfigPath = $configPath
        DataDir    = $dataDir
        LogDir     = $logDir
        Token      = $Token
        Port       = $Port
    }
}

function Start-WhaleGoHeadlessExecutor {
    <#
      Starts the executor headless with an isolated data dir (WHALEGO_DATA_DIR env, inherited by the
      child process) and a temp config. Returns the Start-Process result.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ExePath,
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][string]$DataDir,
        [Parameter(Mandatory = $true)][string]$LogDir,
        [string]$StdOutName = 'executor.stdout.log',
        [string]$StdErrName = 'executor.stderr.log'
    )

    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $stdout = Join-Path $LogDir $StdOutName
    $stderr = Join-Path $LogDir $StdErrName

    $previous = $env:WHALEGO_DATA_DIR
    $env:WHALEGO_DATA_DIR = $DataDir
    try {
        $p = Start-Process -FilePath $ExePath -ArgumentList @('--headless', '--no-hotkey', '--config', $ConfigPath) -WorkingDirectory (Split-Path -Parent $ExePath) -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    } finally {
        if ($null -eq $previous) { Remove-Item Env:WHALEGO_DATA_DIR -ErrorAction SilentlyContinue } else { $env:WHALEGO_DATA_DIR = $previous }
    }
    $p | Add-Member -NotePropertyName StdOutPath -NotePropertyValue $stdout -Force
    $p | Add-Member -NotePropertyName StdErrPath -NotePropertyValue $stderr -Force
    return $p
}

function Wait-WhaleGoHealth {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [int]$TimeoutSec = 45,
        [System.Diagnostics.Process]$Process = $null
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($Process) {
            try { $Process.Refresh() } catch { }
            if ($Process.HasExited) {
                $tail = ''
                $errPath = $Process.PSObject.Properties['StdErrPath']
                if ($errPath -and (Test-Path $errPath.Value)) {
                    $tail = (Get-Content $errPath.Value -Tail 25 | Out-String)
                }
                throw "Executor exited early (code $($Process.ExitCode)). stderr: $tail"
            }
        }
        try {
            $resp = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 2
            if ($resp.StatusCode -eq 200) { return $resp }
        } catch {
            Start-Sleep -Milliseconds 300
        }
    }
    throw "Timed out after $TimeoutSec s waiting for $Url"
}

function Stop-WhaleGoProcess {
    [CmdletBinding()]
    param([System.Diagnostics.Process]$Process, [string]$Label = 'executor')
    if ($null -eq $Process) { return }
    try { $Process.Refresh() } catch { return }
    if (-not $Process.HasExited) {
        try {
            Stop-Process -Id $Process.Id -Force -ErrorAction Stop
            # Wait for exit so the per-interactive-session single-instance mutex is released
            # before the next headless executor starts.
            [void]$Process.WaitForExit(10000)
            Write-Host "    stopped $Label pid=$($Process.Id)" -ForegroundColor DarkGray
        } catch {
            Write-Warning "could not stop $Label pid=$($Process.Id): $($_.Exception.Message)"
        }
    }
}

function Get-WhaleGoHttpStatus {
    <#
      Returns the HTTP status code for a request that is expected to fail with 400/401/403 (no throw).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [string]$Method = 'Post',
        [string]$Body = '{}',
        [hashtable]$Headers = @{},
        [string]$ContentType = 'application/json'
    )
    $mergedHeaders = @{ 'Accept' = 'application/json, text/event-stream' }
    foreach ($k in $Headers.Keys) { $mergedHeaders[$k] = $Headers[$k] }
    try {
        $resp = Invoke-WebRequest -UseBasicParsing -Uri $Uri -Method $Method -Headers $mergedHeaders -Body $Body -ContentType $ContentType -TimeoutSec 10
        return [int]$resp.StatusCode
    } catch {
        $status = 0
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
        }
        return $status
    }
}

Export-ModuleMember -Function New-WhaleGoFreeTcpPort, New-WhaleGoToken, Write-WhaleGoUtf8File, Resolve-WhaleGoRepoPath, New-WhaleGoTestDataDir, Start-WhaleGoHeadlessExecutor, Wait-WhaleGoHealth, Stop-WhaleGoProcess, Get-WhaleGoHttpStatus, Get-WhaleGoSha256Hex, Invoke-WhaleGoAuditAssertions
