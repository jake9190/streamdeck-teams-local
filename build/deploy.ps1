# Builds the plugin, installs it into the Stream Deck plugins folder, and restarts
# Stream Deck so the new build is loaded.
#
#   ./build/deploy.ps1
#   ./build/deploy.ps1 -StreamDeckPluginsDir "D:\Custom\Plugins"
#   ./build/deploy.ps1 -Configuration Debug
#
# Stream Deck locks the running plugin executable, so this stops Stream Deck (and the
# plugin) before copying, then relaunches it.
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$StreamDeckPluginsDir = (Join-Path $env:APPDATA "Elgato\StreamDeck\Plugins")
)

$ErrorActionPreference = "Stop"

$repoRoot    = Split-Path -Parent $PSScriptRoot
$packageDir  = Join-Path $repoRoot "com.local.msteams-local.sdPlugin"
$packageName = Split-Path $packageDir -Leaf
$targetDir   = Join-Path $StreamDeckPluginsDir $packageName

# 1. Stop Stream Deck (capture its path so we can relaunch the same install).
$sd = Get-Process -Name "StreamDeck" -ErrorAction SilentlyContinue | Select-Object -First 1
$sdPath = if ($sd) { $sd.Path } else { Join-Path ${env:ProgramFiles} "Elgato\StreamDeck\StreamDeck.exe" }
if ($sd) {
    Write-Host "Stopping Stream Deck..." -ForegroundColor Cyan
    Get-Process -Name "StreamDeck" -ErrorAction SilentlyContinue | Stop-Process -Force
}
Get-Process -Name "msteams-local" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# 2. Build + publish into the package folder.
& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration

# 3. Mirror the package into the Stream Deck plugins folder.
Write-Host "Deploying to $targetDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
robocopy $packageDir $targetDir /MIR /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

# 4. Relaunch Stream Deck.
if (Test-Path $sdPath) {
    Write-Host "Starting Stream Deck..." -ForegroundColor Cyan
    Start-Process -FilePath $sdPath
} else {
    Write-Warning "Stream Deck executable not found at $sdPath; start it manually."
}

Write-Host "Deploy complete." -ForegroundColor Green
