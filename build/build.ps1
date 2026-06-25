# Builds the plugin and publishes it into the .sdPlugin package folder,
# then regenerates the icon assets.
#
#   ./build/build.ps1                 # Release build into the package
#   ./build/build.ps1 -Configuration Debug
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot "src\MsTeamsLocal.csproj"
$packageDir = Join-Path $repoRoot "com.local.msteams-local.sdPlugin"

Write-Host "Publishing $project ($Configuration) -> $packageDir" -ForegroundColor Cyan
# Self-contained so end users do NOT need the .NET Desktop Runtime installed.
dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $packageDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $packageDir "msteams-local.exe"
if (-not (Test-Path $exe)) { throw "Expected output not found: $exe" }

Write-Host "Generating icons..." -ForegroundColor Cyan
Start-Process -FilePath $exe -ArgumentList "--emit-icons" -Wait -NoNewWindow

Write-Host "Build complete." -ForegroundColor Green
