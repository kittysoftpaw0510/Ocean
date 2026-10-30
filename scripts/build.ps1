<#
.SYNOPSIS
  Builds Ocean and lays out a ready-to-run folder (Ocean.exe + Ocean.Service.exe side by side).

.PARAMETER SelfContained
  Bundle the .NET runtime so target PCs need nothing installed (used by the setup).

.PARAMETER OutDir
  Output folder (default: dist\Ocean).

.EXAMPLE
  .\scripts\build.ps1
  .\scripts\build.ps1 -SelfContained -OutDir dist\setup\Ocean
#>
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release",
    [string]$OutDir
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# --- find a dotnet that has an SDK >= 8 -------------------------------------------------------
# The dotnet on PATH may be a runtime-only install (e.g. C:\Program Files\dotnet), while the SDK lives
# elsewhere (e.g. %USERPROFILE%\.dotnet from dotnet-install.ps1). Try the usual places.
function Find-DotnetSdk {
    $candidates = @(
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
        $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT "dotnet.exe" }),
        (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"),
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
    foreach ($exe in $candidates) {
        $sdks = & $exe --list-sdks 2>$null
        foreach ($line in @($sdks)) {
            if ($line -match '^(\d+)\.' -and [int]$Matches[1] -ge 8) { return $exe }
        }
    }
    return $null
}
$dotnet = Find-DotnetSdk
if (-not $dotnet) {
    throw "No .NET 8 (or newer) SDK found. Install it with:  winget install Microsoft.DotNet.SDK.8   then run this script again."
}
$dotnetDir = Split-Path -Parent $dotnet
$env:DOTNET_ROOT = $dotnetDir                 # child build processes use the same installation
$env:PATH = "$dotnetDir;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"
Write-Host "Using .NET SDK from $dotnetDir" -ForegroundColor DarkGray

$out =if ($OutDir) { if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir } } else { Join-Path $root "dist\Ocean" }
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

$sc = if ($SelfContained) { "true" } else { "false" }
$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", $sc, "-o", $out, "-nologo", "-v", "q", "-p:DebugType=none")

Write-Host "Publishing service..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Ocean.Service\Ocean.Service.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "service publish failed" }

Write-Host "Publishing app..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Ocean.App\Ocean.App.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

# ASP.NET leaves an IIS web.config behind; Ocean does not use IIS.
Remove-Item (Join-Path $out "web.config") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot "install-service.ps1") $out
Copy-Item (Join-Path $PSScriptRoot "uninstall-service.ps1") $out
Copy-Item (Join-Path $root "README.md") $out -ErrorAction SilentlyContinue

$size = [math]::Round((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "Done: $out ($size MB)." -ForegroundColor Green
