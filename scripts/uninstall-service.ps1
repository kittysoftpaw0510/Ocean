<#
.SYNOPSIS
  Stops and removes the Ocean Windows service and its firewall rules. Run as administrator.
  Settings and data in %ProgramData%\Ocean are kept (delete the folder to start fresh).
#>
$ErrorActionPreference = "Stop"
$name = "OceanService"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Please run this script as administrator."
}
$svc = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne "Stopped") { Stop-Service $name -Force }
    sc.exe delete $name | Out-Null
    Write-Host "OceanService removed." -ForegroundColor Green
} else {
    Write-Host "OceanService is not installed." -ForegroundColor Yellow
}
Remove-NetFirewallRule -DisplayName "Ocean (peer TLS)" -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName "Ocean (discovery)" -ErrorAction SilentlyContinue
