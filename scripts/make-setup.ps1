<#
.SYNOPSIS
  Produces dist\Ocean-Setup-<version>.exe: a single installer with the .NET runtime bundled,
  so target PCs need nothing else.

  Steps: (1) find Inno Setup's ISCC.exe (installed copy, or fetch the Tools.InnoSetup NuGet package into .tools\),
         (2) publish a self-contained build into dist\setup\Ocean, (3) compile installer\Ocean.iss.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\make-setup.ps1
#>
param([string]$InnoVersion = "6.4.3")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# --- version from Directory.Build.props --------------------------------------------------------
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { $version = "1.4.6" }

# --- artwork -------------------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $root "assets\ocean.ico"))) {
    & (Join-Path $PSScriptRoot "make-icon.ps1") | Out-Null
}

# --- Inno Setup compiler -----------------------------------------------------------------------
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    (Join-Path $root ".tools\innosetup-$InnoVersion\tools\ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Host "Fetching Inno Setup $InnoVersion compiler (NuGet: Tools.InnoSetup)..." -ForegroundColor Cyan
    $toolDir = Join-Path $root ".tools\innosetup-$InnoVersion"
    New-Item -ItemType Directory -Force $toolDir | Out-Null
    $zip = Join-Path $toolDir "pkg.zip"
    Invoke-WebRequest -UseBasicParsing "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$InnoVersion/tools.innosetup.$InnoVersion.nupkg" -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $toolDir -Force
    Remove-Item $zip
    $iscc = Join-Path $toolDir "tools\ISCC.exe"
    if (-not (Test-Path $iscc)) { throw "ISCC.exe not found in the Inno Setup package." }
}

# --- self-contained build ----------------------------------------------------------------------
$stage = Join-Path $root "dist\setup\Ocean"
& (Join-Path $PSScriptRoot "build.ps1") -SelfContained -OutDir $stage

# --- compile -------------------------------------------------------------------------------------
Write-Host "Compiling installer..." -ForegroundColor Cyan
& $iscc /Qp "/DAppVersion=$version" "/DSourceDir=$stage" (Join-Path $root "installer\Ocean.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed ($LASTEXITCODE)" }

$setup = Join-Path $root "dist\Ocean-Setup-$version.exe"
$mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
# checksum next to it: upload both to the GitHub release (tag vX.Y.Z); the auto-updater verifies the download
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Value "$sha  Ocean-Setup-$version.exe" -Encoding ascii
Write-Host "Setup ready: $setup ($mb MB), checksum in $setup.sha256" -ForegroundColor Green
Write-Host "Publish: git tag v$version; create a GitHub release for it with both files attached." -ForegroundColor Cyan
