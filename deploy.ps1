#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
    Publishes TankController and deploys it as a systemd service on Raspberry Pi.

.PARAMETER PiHost
    Hostname or IP address of the Raspberry Pi (default: tank-controller).

.PARAMETER PiUser
    SSH username on the Pi (default: bgriggs).

.PARAMETER PiPass
    SSH password. Prefer passing this at runtime rather than storing in source.

.PARAMETER Runtime
    .NET RID. Use linux-arm64 for RPi 4/5 (64-bit OS) or linux-arm for 32-bit OS.

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -PiHost 192.168.1.50 -Runtime linux-arm
#>
[CmdletBinding()]
param(
    [string]$PiHost      = 'tank-controller',
    [string]$PiUser      = '',
    [string]$PiPass      = '',
    [string]$Runtime     = 'linux-arm64',
    [string]$ServiceName = 'tank-controller',
    [string]$DeployDir   = "/home/bgriggs/tank-controller"
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot

# ── 1. Ensure Posh-SSH ────────────────────────────────────────────────────────
if (-not (Get-Module -ListAvailable Posh-SSH)) {
    Write-Host '→ Installing Posh-SSH…' -ForegroundColor Yellow
    Install-Module Posh-SSH -Scope CurrentUser -Force
}
Import-Module Posh-SSH -Force

# ── 2. Publish ────────────────────────────────────────────────────────────────
# The publish output folder is named 'tank-controller' so SCP creates the right
# remote directory: /home/bgriggs/tank-controller/
$PublishDir = Join-Path $ScriptDir 'publish' 'tank-controller'

Write-Host "→ Publishing for $Runtime…" -ForegroundColor Cyan
dotnet publish (Join-Path $ScriptDir 'TankController.csproj') `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# ── 3. Open SSH session ───────────────────────────────────────────────────────
$secPass    = ConvertTo-SecureString $PiPass -AsPlainText -Force
$credential = [pscredential]::new($PiUser, $secPass)

Write-Host "→ Connecting to $PiUser@$PiHost…" -ForegroundColor Cyan
$session = New-SSHSession -ComputerName $PiHost -Credential $credential -AcceptKey -Force

# Helper: run a command on the Pi, optionally with sudo (password piped via stdin)
function Invoke-Pi ([string]$Command, [switch]$Sudo) {
    $cmd = $Sudo ? "echo '$PiPass' | sudo -S bash -c '$Command'" : $Command
    $r   = Invoke-SSHCommand -SSHSession $session -Command $cmd
    if ($r.ExitStatus -ne 0) {
        Write-Warning "  [exit $($r.ExitStatus)] $($r.Error)"
    }
    if ($r.Output) { Write-Verbose ($r.Output -join "`n") }
    return $r
}

try {
    # ── 4. Prepare target directory ──────────────────────────────────────────
    Write-Host '→ Preparing target directory…' -ForegroundColor Cyan

    # Stop any running instance (ignore failure if not yet installed)
    Invoke-Pi "systemctl stop $ServiceName 2>/dev/null; true" -Sudo

    # Create deploy dir and ensure gpio group membership for interactive use
    Invoke-Pi "mkdir -p $DeployDir && chown $PiUser`:$PiUser $DeployDir" -Sudo
    Invoke-Pi "usermod -aG gpio $PiUser" -Sudo

    # ── 5. Upload application ─────────────────────────────────────────────────
    Write-Host '→ Uploading application files…' -ForegroundColor Cyan

    # Set-SCPItem uploads the tank-controller folder into /home/$PiUser/, producing
    # the correct path: /home/bgriggs/tank-controller/
    Set-SCPItem -ComputerName $PiHost -Credential $credential -AcceptKey `
        -Path $PublishDir `
        -Destination "/home/$PiUser"

    # ── 6. Set executable bit ─────────────────────────────────────────────────
    Invoke-Pi "chmod +x $DeployDir/TankController"

    # ── 7. Install systemd unit ───────────────────────────────────────────────
    Write-Host '→ Installing systemd unit…' -ForegroundColor Cyan

    # Upload unit file to /tmp first (no elevated privileges needed), then move
    Set-SCPItem -ComputerName $PiHost -Credential $credential -AcceptKey `
        -Path (Join-Path $ScriptDir 'tank-controller.service') `
        -Destination '/tmp'

    Invoke-Pi "mv /tmp/$ServiceName.service /etc/systemd/system/$ServiceName.service" -Sudo
    Invoke-Pi 'systemctl daemon-reload' -Sudo

    # ── 8. Enable and start ───────────────────────────────────────────────────
    Write-Host '→ Enabling and starting service…' -ForegroundColor Cyan
    Invoke-Pi "systemctl enable $ServiceName"  -Sudo
    Invoke-Pi "systemctl start  $ServiceName"  -Sudo

    # ── 9. Show status ────────────────────────────────────────────────────────
    Start-Sleep -Seconds 2
    $status = Invoke-Pi "systemctl status $ServiceName --no-pager"
    Write-Host ($status.Output -join "`n")

    Write-Host "`n✔ Deployment complete!" -ForegroundColor Green
    Write-Host "  Watch logs : ssh $PiUser@$PiHost 'journalctl -u $ServiceName -f'"
    Write-Host "  Stop       : ssh $PiUser@$PiHost 'sudo systemctl stop $ServiceName'"
    Write-Host "  Restart    : ssh $PiUser@$PiHost 'sudo systemctl restart $ServiceName'"
}
finally {
    Remove-SSHSession -SessionId $session.SessionId | Out-Null
}
