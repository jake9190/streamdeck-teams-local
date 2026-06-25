# Builds the plugin and produces distributable artifacts in the output directory:
#   - <uuid>.streamDeckPlugin  (via the Elgato CLI, the supported installer format)
#   - <uuid>.zip               (always produced, as a portable fallback)
#
#   ./build/package.ps1
#   ./build/package.ps1 -OutputDir dist -Configuration Release
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"

$repoRoot    = Split-Path -Parent $PSScriptRoot
$packageDir  = Join-Path $repoRoot "com.local.msteams-local.sdPlugin"
$packageName = Split-Path $packageDir -Leaf            # com.local.msteams-local.sdPlugin
$uuid        = $packageName -replace '\.sdPlugin$', '' # com.local.msteams-local
$outDir      = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repoRoot $OutputDir }

# 1. Build + publish + emit icons.
& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration

# 2. Clean output dir.
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 3. Stage a throwaway copy (excluding logs) so the CLI never mutates the tracked tree.
$staging   = Join-Path $env:TEMP "sdpkg_$([guid]::NewGuid().ToString('N'))"
$stagedPkg = Join-Path $staging $packageName
New-Item -ItemType Directory -Force -Path $stagedPkg | Out-Null
try {
    robocopy $packageDir $stagedPkg /MIR /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

    # 4. Preferred: package with the Elgato Stream Deck CLI (operates on the staged copy).
    $packed = $false
    if (Get-Command npm -ErrorAction SilentlyContinue) {
        try {
            Write-Host "Installing @elgato/cli..." -ForegroundColor Cyan
            npm install -g "@elgato/cli@latest" | Out-Null
            if (Get-Command streamdeck -ErrorAction SilentlyContinue) {
                Write-Host "Packing with the Elgato CLI..." -ForegroundColor Cyan
                & streamdeck pack $stagedPkg --output $outDir --force
                if ($LASTEXITCODE -eq 0) { $packed = $true }
                else { Write-Warning "streamdeck pack exited with $LASTEXITCODE; using zip fallback." }
            }
        } catch {
            Write-Warning "Elgato CLI packaging failed: $($_.Exception.Message). Using zip fallback."
        }
    } else {
        Write-Warning "npm not found; skipping .streamDeckPlugin packaging (zip fallback only)."
    }

    # 5. Always produce a portable zip of the package folder.
    Compress-Archive -Path $stagedPkg -DestinationPath (Join-Path $outDir "$uuid.zip") -Force
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Artifacts in $outDir :" -ForegroundColor Green
Get-ChildItem $outDir | ForEach-Object { Write-Host "  $($_.Name)" }
if (-not $packed) {
    Write-Host "(.streamDeckPlugin not produced - install the Elgato CLI: npm i -g @elgato/cli)" -ForegroundColor Yellow
}
