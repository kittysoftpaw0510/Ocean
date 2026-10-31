<#
.SYNOPSIS
  Fetches ffmpeg into .tools\ffmpeg (once) and prints the path of ffmpeg.exe.
  The installer ships it next to Ocean.Service.exe: it makes the lighter copies of videos for watching.
  Default source: gyan.dev "essentials" release build (GPL). Any ffmpeg.exe placed in .tools\ffmpeg is used as is.
#>
param([string]$Url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root ".tools\ffmpeg"
$exe = Join-Path $dir "ffmpeg.exe"
if (Test-Path $exe) { $exe; return }
New-Item -ItemType Directory -Force $dir | Out-Null
$zip = Join-Path $dir "ffmpeg.zip"
Write-Host "Fetching ffmpeg ($Url)..." -ForegroundColor Cyan
Invoke-WebRequest -UseBasicParsing $Url -OutFile $zip
$tmp = Join-Path $dir "unzip"
Expand-Archive -Path $zip -DestinationPath $tmp -Force
$found = Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
if (-not $found) { throw "ffmpeg.exe not found in the downloaded archive" }
Copy-Item $found.FullName $exe
$lic = Get-ChildItem $tmp -Recurse -Include LICENSE*, COPYING* -File | Select-Object -First 1
if ($lic) { Copy-Item $lic.FullName (Join-Path $dir "LICENSE-ffmpeg.txt") }
Remove-Item $tmp -Recurse -Force
Remove-Item $zip
$exe
