#Requires -Version 5.1
<#
.SYNOPSIS
  Manage the inbound firewall rule 'WhaleGo-MCP' (TCP 8765) limited to Docker/WSL subnets.

.DESCRIPTION
  The WhaleGo executor listens on 0.0.0.0:8765 (configurable) so the AstrBot container can reach it via
  host.docker.internal. This script creates/removes/shows a Windows Defender Firewall inbound rule that
  allows ONLY the local Docker/WSL/Hyper-V subnets to connect. It never opens the port to the LAN.

  -Action add|remove|show (default: show). add/remove require an elevated PowerShell.
  Remote addresses are auto-detected from vEthernet/WSL/Docker/Hyper-V interfaces; use -RemoteAddress
  to override with explicit CIDRs.

.PARAMETER Action
  add = create/refresh rule, remove = delete rule, show = print rule + effective config.

.PARAMETER RuleName
  Firewall rule display name (default: WhaleGo-MCP).

.PARAMETER Port
  TCP port to allow (default: 8765).

.PARAMETER RemoteAddress
  Explicit list of CIDRs (e.g. 172.29.0.0/20). Default: auto-detected Docker/WSL/Hyper-V subnets.

.PARAMETER Profile
  Firewall profile(s) for the rule (default: Any; the rule is still subnet-scoped by RemoteAddress).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/firewall.ps1 -Action show
  # elevated:
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/firewall.ps1 -Action add
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/firewall.ps1 -Action remove
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('add', 'remove', 'show')]
    [string]$Action = 'show',

    [string]$RuleName = 'WhaleGo-MCP',

    [int]$Port = 8765,

    [string[]]$RemoteAddress = @(),

    [string]$Profile = 'Any'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DockerWslSubnets {
    $subnets = New-Object System.Collections.Generic.List[string]
    try {
        $addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Where-Object { $_.InterfaceAlias -match 'vEthernet|WSL|Docker|Default Switch|Hyper-V' -and $_.IPAddress -notmatch '^169\.254\.' }
        foreach ($a in $addresses) {
            $prefix = $a.PrefixLength
            if (-not $prefix) { continue }
            $ip = [System.Net.IPAddress]::Parse($a.IPAddress)
            $bytes = $ip.GetAddressBytes()
            $mask = [uint32]0
            for ($i = 0; $i -lt 32; $i++) {
                if ($i -lt $prefix) { $mask = $mask -bor ([uint32]1 -shl (31 - $i)) }
            }
            $ipUint = ([uint32]$bytes[0] -shl 24) -bor ([uint32]$bytes[1] -shl 16) -bor ([uint32]$bytes[2] -shl 8) -bor [uint32]$bytes[3]
            $network = $ipUint -band $mask
            $n1 = ($network -shr 24) -band 255
            $n2 = ($network -shr 16) -band 255
            $n3 = ($network -shr 8) -band 255
            $n4 = $network -band 255
            $cidr = "$n1.$n2.$n3.$n4/$prefix"
            if (-not $subnets.Contains($cidr)) { $subnets.Add($cidr) }
        }
    } catch {
        Write-Warning "Get-NetIPAddress failed: $($_.Exception.Message)"
    }
    if ($subnets.Count -eq 0) {
        foreach ($fallback in @('172.16.0.0/12', '192.168.65.0/24')) { $subnets.Add($fallback) }
        Write-Warning "No Docker/WSL interface detected - using fallback subnets (172.16.0.0/12, 192.168.65.0/24)."
    }
    return $subnets.ToArray()
}

function Show-Rule {
    $rules = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
    if (-not $rules) {
        Write-Host "Firewall rule '$RuleName' : NOT FOUND" -ForegroundColor Yellow
        return
    }
    foreach ($rule in $rules) {
        $portFilter = $rule | Get-NetFirewallPortFilter
        $addrFilter = $rule | Get-NetFirewallAddressFilter
        Write-Host "Firewall rule '$RuleName'" -ForegroundColor Cyan
        Write-Host "  Enabled        : $($rule.Enabled)"
        Write-Host "  Direction      : $($rule.Direction)"
        Write-Host "  Action         : $($rule.Action)"
        Write-Host "  Profile        : $($rule.Profile)"
        Write-Host "  Protocol/Port  : $($portFilter.Protocol) / $($portFilter.LocalPort)"
        Write-Host "  RemoteAddress  : $($addrFilter.RemoteAddress -join ', ')"
    }
}

if ($RemoteAddress.Count -eq 0) { $RemoteAddress = Get-DockerWslSubnets }
$quotedAddresses = ($RemoteAddress | ForEach-Object { "'" + $_ + "'" }) -join ','
$addCommand = "New-NetFirewallRule -DisplayName '$RuleName' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -RemoteAddress @($quotedAddresses) -Profile $Profile"
$removeCommand = "Remove-NetFirewallRule -DisplayName '$RuleName'"

switch ($Action) {
    'show' {
        Write-Host "==> WhaleGo firewall rule (port $Port)" -ForegroundColor Cyan
        Write-Host "    effective remote subnets: $($RemoteAddress -join ', ')"
        Show-Rule
        exit 0
    }
    'add' {
        if (-not (Test-Administrator)) {
            Write-Error "Firewall changes require an elevated PowerShell. Run as Administrator then: powershell -File scripts/firewall.ps1 -Action add"
            exit 1
        }
        Write-Host "==> add rule '$RuleName' (TCP $Port, remote: $($RemoteAddress -join ', '))" -ForegroundColor Cyan
        Write-Host "    command: $addCommand" -ForegroundColor DarkGray
        if ($PSCmdlet.ShouldProcess($RuleName, 'Create/refresh inbound firewall rule')) {
            $existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
            if ($existing) { Remove-NetFirewallRule -DisplayName $RuleName }
            New-NetFirewallRule -DisplayName $RuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -RemoteAddress $RemoteAddress -Profile $Profile -Description "WhaleGo MCP executor: TCP $Port inbound limited to Docker/WSL subnets (managed by scripts/firewall.ps1)" | Out-Null
        }
        Show-Rule
        exit 0
    }
    'remove' {
        if (-not (Test-Administrator)) {
            Write-Error "Firewall changes require an elevated PowerShell. Run as Administrator then: powershell -File scripts/firewall.ps1 -Action remove"
            exit 1
        }
        Write-Host "==> remove rule '$RuleName'" -ForegroundColor Cyan
        Write-Host "    command: $removeCommand" -ForegroundColor DarkGray
        if ($PSCmdlet.ShouldProcess($RuleName, 'Remove inbound firewall rule')) {
            $existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
            if ($existing) { Remove-NetFirewallRule -DisplayName $RuleName }
        }
        Show-Rule
        exit 0
    }
}
