# Télécharge libmpv-2.dll (build shinchiro/mpv-winbuild-cmake) à la racine du projet.
# Usage : powershell -ExecutionPolicy Bypass -File tools\get-libmpv.ps1 [-Tag 20261021]
param([string]$Tag = "20261021", [string]$Asset = "mpv-dev-x86_64-20261021-git-e471496c56.7z")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$sevenZip = "C:\Program Files\7-Zip\7z.exe"
if (-not (Test-Path $sevenZip)) { throw "7-Zip est requis : $sevenZip introuvable." }

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("libmpv-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
    $url = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$Tag/$Asset"
    Write-Host "Téléchargement de $url"
    Invoke-WebRequest $url -OutFile (Join-Path $tmp "mpv-dev.7z") -UseBasicParsing
    & $sevenZip e (Join-Path $tmp "mpv-dev.7z") libmpv-2.dll "-o$tmp" -y | Out-Null
    Copy-Item (Join-Path $tmp "libmpv-2.dll") (Join-Path $root "libmpv-2.dll") -Force
    Write-Host "libmpv-2.dll copiée dans $root"
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
