$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$Cache = Join-Path $ProjectRoot "Library\MaleCNS"
$Venv = Join-Path $Cache "venv"
$Python = Join-Path $Venv "Scripts\python.exe"
$Builder = Join-Path $PSScriptRoot "build_malecns_v1.py"

Write-Host ""
Write-Host "=== FlyMaze / MaleCNS v1.0 Setup ===" -ForegroundColor Cyan
Write-Host "Project: $ProjectRoot"
Write-Host "Cache:   $Cache"
Write-Host ""
Write-Host "This downloads the official MaleCNS v1.0 flat-connectome tables from HHMI Janelia."
Write-Host "Expect roughly 1.2 GB of raw downloads plus generated runtime files."
Write-Host "Everything is stored under Library/MaleCNS and is not committed to Git."
Write-Host ""

New-Item -ItemType Directory -Force -Path $Cache | Out-Null

if (-not (Test-Path $Python)) {
    if (Get-Command py -ErrorAction SilentlyContinue) {
        & py -3 -m venv $Venv
    }
    elseif (Get-Command python -ErrorAction SilentlyContinue) {
        & python -m venv $Venv
    }
    else {
        throw "Python 3 was not found. Install Python 3.11+ and run this setup again."
    }
}

Write-Host "[1/2] Installing Python packages..." -ForegroundColor Yellow
& $Python -m pip install --disable-pip-version-check --upgrade pip
& $Python -m pip install --disable-pip-version-check numpy scipy pandas pyarrow

Write-Host "[2/2] Downloading/building MaleCNS v1.0..." -ForegroundColor Yellow
& $Python $Builder --data $Cache

Write-Host ""
Write-Host "MaleCNS v1.0 is ready." -ForegroundColor Green
Write-Host "Return to Unity and press Play. The MaleCNS bridge will auto-start."
Write-Host ""
