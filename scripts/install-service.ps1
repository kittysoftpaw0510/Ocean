<#
.SYNOPSIS
  Installs the Ocean background service as a Windows service (auto-start, shares even when nobody is signed in).
  Run as administrator from the folder that contains Ocean.Service.exe.

  Notes
  - The service runs as LocalSystem and reads the shared folders directly; mapped network drives are per-user
    and not visible to it (use UNC paths or local folders).
  - Settings and data live in %ProgramData%\Ocean.
#>
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "Ocean.Service.exe"
if (-not (Test-Path $exe)) { throw "Ocean.Service.exe not found next to this script." }
$name = "OceanService"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Please run this script as administrator."
}

# Stop any background instance started by the app so the ports are free.
Get-Process -Name "Ocean.Service" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service already exists; updating binary path." -ForegroundColor Yellow
    if ($existing.Status -ne "Stopped") { Stop-Service $name -Force }
    sc.exe config $name binPath= "`"$exe`"" | Out-Null
} else {
    New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName "Ocean LAN Video Sharing" `
        -Description "Shares and streams videos with other Ocean nodes on the local network." -StartupType Automatic | Out-Null
}
sc.exe failure $name reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# Allow the peer HTTPS port, the discovery port and the multicast port through Windows Firewall (private/domain profiles).
$peerPort = 38471; $discPort = 38472; $castPort = 38473
Remove-NetFirewallRule -DisplayName "Ocean (peer TLS)" -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName "Ocean (discovery)" -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName "Ocean (multicast)" -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "Ocean (peer TLS)" -Direction Inbound -Protocol TCP -LocalPort $peerPort -Action Allow -Profile Private,Domain | Out-Null
New-NetFirewallRule -DisplayName "Ocean (discovery)" -Direction Inbound -Protocol UDP -LocalPort $discPort -Action Allow -Profile Private,Domain | Out-Null
New-NetFirewallRule -DisplayName "Ocean (multicast)" -Direction Inbound -Protocol UDP -LocalPort $castPort -Action Allow -Profile Private,Domain | Out-Null

Start-Service $name
Write-Host "OceanService installed and started. Open Ocean.exe to manage shares." -ForegroundColor Green
