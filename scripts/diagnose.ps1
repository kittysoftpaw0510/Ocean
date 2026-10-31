<#
.SYNOPSIS
  Collects what's needed to diagnose Ocean on this PC into one text file (read-only: changes nothing).

.DESCRIPTION
  Run it in PowerShell on the PC with the problem:
      powershell -ExecutionPolicy Bypass -File .\diagnose.ps1
  It writes ocean-diagnostics-<PC name>.txt to the Desktop. Send that file back.
  Contains: Ocean versions, service state and restart history, crashes reported by Windows, which program
  owns Ocean's ports, ports Windows reserves, network profile, Ocean firewall rules, and the end of
  Ocean's logs. No videos, passwords or shared secrets are included.
#>
param([string]$OutFile)
$ErrorActionPreference = 'Continue'
$out = if ($OutFile) { $OutFile } else { Join-Path ([Environment]::GetFolderPath('Desktop')) "ocean-diagnostics-$env:COMPUTERNAME.txt" }
$data = Join-Path $env:ProgramData 'Ocean'
$since = (Get-Date).AddDays(-2)
$sb = New-Object System.Text.StringBuilder
function Section($title) { [void]$sb.AppendLine(); [void]$sb.AppendLine("===== $title =====") }
function Add($text) { [void]$sb.AppendLine(($text | Out-String).TrimEnd()) }
function Try-Add($title, [scriptblock]$block) { Section $title; try { Add (& $block) } catch { Add "  (failed: $($_.Exception.Message))" } }

Add "Ocean diagnostics  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  PC=$env:COMPUTERNAME  user=$env:USERNAME"
Add "Windows $([Environment]::OSVersion.Version)"

Try-Add 'Installed Ocean' {
    foreach ($exe in 'Ocean.exe', 'Ocean.Service.exe') {
        $p = Join-Path $env:ProgramFiles "Ocean\$exe"
        if (Test-Path $p) { $i = Get-Item $p; "{0}  {1}  {2}" -f $exe, $i.VersionInfo.ProductVersion, $i.LastWriteTime } else { "$exe  not found" }
    }
}
Try-Add 'Service' {
    Get-Service OceanService | Format-List Name, Status, StartType | Out-String
    sc.exe qc OceanService | Out-String
    sc.exe qfailure OceanService | Out-String
}
Try-Add 'Ocean processes' {
    Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'Ocean*' } |
        ForEach-Object { "{0,-6} {1,-18} session={2} started={3} {4}" -f $_.ProcessId, $_.Name, $_.SessionId, $_.CreationDate, $_.ExecutablePath }
}
Try-Add 'Who listens on Ocean ports (TCP 38470 local, 38471 peers; UDP 38472 discovery)' {
    Get-NetTCPConnection -LocalPort 38470, 38471 -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { "TCP {0}:{1}  pid={2} ({3})" -f $_.LocalAddress, $_.LocalPort, $_.OwningProcess, (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName }
    Get-NetUDPEndpoint -LocalPort 38472 -ErrorAction SilentlyContinue |
        ForEach-Object { "UDP {0}:{1}  pid={2} ({3})" -f $_.LocalAddress, $_.LocalPort, $_.OwningProcess, (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName }
    $c = New-Object Net.Sockets.TcpClient
    try { $c.Connect('127.0.0.1', 38470); "connect 127.0.0.1:38470: OK" } catch { "connect 127.0.0.1:38470: FAILED ($($_.Exception.InnerException.Message))" } finally { $c.Dispose() }
}
Try-Add 'Ports Windows reserves (Hyper-V / WSL / Docker can block Ocean ports)' {
    netsh int ipv4 show excludedportrange protocol=tcp | Out-String
    netsh int ipv4 show excludedportrange protocol=udp | Out-String
}
Try-Add 'Network profile' { Get-NetConnectionProfile | Format-Table InterfaceAlias, Name, NetworkCategory, IPv4Connectivity -AutoSize | Out-String }
Try-Add 'Firewall rules mentioning Ocean' {
    Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*Ocean*' } | ForEach-Object {
        $app = ($_ | Get-NetFirewallApplicationFilter).Program
        "{0} | {1} | {2} | enabled={3} | profile={4} | {5}" -f $_.DisplayName, $_.Direction, $_.Action, $_.Enabled, $_.Profile, $app
    }
}
Try-Add 'Security software' {
    Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue | ForEach-Object { $_.displayName }
    Get-CimInstance -Namespace root/SecurityCenter2 -ClassName FirewallProduct -ErrorAction SilentlyContinue | ForEach-Object { "firewall: " + $_.displayName }
}
Try-Add 'Service start/stop history (last 2 days)' {
    Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Ocean' } | Select-Object -First 60 |
        ForEach-Object { "{0:MM-dd HH:mm:ss} [{1}] {2}" -f $_.TimeCreated, $_.Id, ($_.Message -replace '\s+', ' ') }
}
Try-Add 'Crashes reported by Windows (last 2 days)' {
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error', 'Windows Error Reporting', 'Application Hang' -and $_.Message -match 'Ocean\.(Service\.)?exe' } |
        Select-Object -First 10 |
        ForEach-Object { "----- {0:MM-dd HH:mm:ss} {1} [{2}]`n{3}" -f $_.TimeCreated, $_.ProviderName, $_.Id, $_.Message }
}
Try-Add 'Ocean data folder' { Get-ChildItem $data -ErrorAction SilentlyContinue | Format-Table Name, Length, LastWriteTime -AutoSize | Out-String }
Try-Add 'Service log (end)' {
    Get-ChildItem (Join-Path $data 'logs\service-*.log') -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 2 |
        ForEach-Object { "--- $($_.Name)"; Get-Content $_.FullName -Tail 300 }
}
Try-Add 'App errors (end)' { Get-Content (Join-Path $data 'logs\app-errors.log') -Tail 80 -ErrorAction SilentlyContinue }

$sb.ToString() | Set-Content -Encoding UTF8 $out
Write-Host "Saved: $out" -ForegroundColor Green
Write-Host "Please send this file back."
