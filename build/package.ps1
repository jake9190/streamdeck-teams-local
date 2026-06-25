# Builds the plugin and produces distributable artifacts in the output directory, in
# BOTH flavors:
#
#   <uuid>.streamDeckPlugin / .zip                   -> self-contained (no .NET runtime needed)
#   <uuid>-runtime-required.streamDeckPlugin / .zip  -> framework-dependent (smaller; needs the
#                                                       .NET 8 Desktop Runtime installed)
#
# Each flavor is published into a throwaway staging folder, so the tracked package
# folder (manifest.json in particular) is never mutated by the Elgato CLI.
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
$project     = Join-Path $repoRoot "src\MsTeamsLocal.csproj"
$packageDir  = Join-Path $repoRoot "com.local.msteams-local.sdPlugin"
$packageName = Split-Path $packageDir -Leaf            # com.local.msteams-local.sdPlugin
$uuid        = $packageName -replace '\.sdPlugin$', '' # com.local.msteams-local
$outDir      = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repoRoot $OutputDir }

# Clean output dir.
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Install the Elgato CLI once (used to produce the .streamDeckPlugin installer files).
$cliAvailable = $false
if (Get-Command npm -ErrorAction SilentlyContinue) {
    try {
        Write-Host "Installing @elgato/cli..." -ForegroundColor Cyan
        npm install -g "@elgato/cli@latest" | Out-Null
        $cliAvailable = [bool](Get-Command streamdeck -ErrorAction SilentlyContinue)
    } catch {
        Write-Warning "Elgato CLI install failed: $($_.Exception.Message)."
    }
} else {
    Write-Warning "npm not found; skipping .streamDeckPlugin packaging (zip fallback only)."
}

function New-Package {
    param([bool]$SelfContained, [string]$Suffix)

    $kind = if ($SelfContained) { "self-contained" } else { "framework-dependent" }
    Write-Host ""
    Write-Host "=== Packaging $kind ===" -ForegroundColor Cyan

    $staging   = Join-Path $env:TEMP "sdpkg_$([guid]::NewGuid().ToString('N'))"
    $stagedPkg = Join-Path $staging $packageName
    New-Item -ItemType Directory -Force -Path $stagedPkg | Out-Null
    try {
        $scFlag = if ($SelfContained) { "true" } else { "false" }
        dotnet publish $project -c $Configuration -r win-x64 --self-contained $scFlag -o $stagedPkg -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

        # Bring in the manifest + property inspector, then (re)generate icons to match code.
        Copy-Item (Join-Path $packageDir "manifest.json") $stagedPkg -Force
        Copy-Item (Join-Path $packageDir "ui") $stagedPkg -Recurse -Force
        Start-Process -FilePath (Join-Path $stagedPkg "msteams-local.exe") -ArgumentList "--emit-icons" -Wait -NoNewWindow

        # .streamDeckPlugin via the Elgato CLI (renamed to add the flavor suffix).
        if ($cliAvailable) {
            $packOut = Join-Path $staging "out"
            New-Item -ItemType Directory -Force -Path $packOut | Out-Null
            & streamdeck pack $stagedPkg --output $packOut --force
            if ($LASTEXITCODE -eq 0) {
                Move-Item (Join-Path $packOut "$uuid.streamDeckPlugin") `
                          (Join-Path $outDir "$uuid$Suffix.streamDeckPlugin") -Force
            } else {
                Write-Warning "streamdeck pack exited with $LASTEXITCODE for $kind; zip only."
            }
        }

        # Portable zip (always).
        Compress-Archive -Path $stagedPkg -DestinationPath (Join-Path $outDir "$uuid$Suffix.zip") -Force
    }
    finally {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

New-Package -SelfContained $true  -Suffix ""
New-Package -SelfContained $false -Suffix "-runtime-required"

Write-Host ""
Write-Host "Artifacts in $outDir :" -ForegroundColor Green
Get-ChildItem $outDir | ForEach-Object { "  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB) }
if (-not $cliAvailable) {
    Write-Host "(.streamDeckPlugin not produced - install the Elgato CLI: npm i -g @elgato/cli)" -ForegroundColor Yellow
}
