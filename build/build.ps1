# Builds the plugin and publishes it into the .sdPlugin package folder,
# then regenerates the icon assets.
#
#   ./build/build.ps1                        # self-contained Release build (no runtime needed)
#   ./build/build.ps1 -SelfContained:$false  # framework-dependent (smaller; needs .NET runtime)
#   ./build/build.ps1 -Configuration Debug
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [bool]$SelfContained = $true
)

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot "src\MsTeamsLocal.csproj"
$packageDir = Join-Path $repoRoot "com.local.msteams-local.sdPlugin"

$scFlag = if ($SelfContained) { "true" } else { "false" }
$kind   = if ($SelfContained) { "self-contained (no runtime needed)" } else { "framework-dependent (.NET runtime required)" }
Write-Host "Publishing $project ($Configuration, $kind) -> $packageDir" -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r win-x64 --self-contained $scFlag -o $packageDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $packageDir "msteams-local.exe"
if (-not (Test-Path $exe)) { throw "Expected output not found: $exe" }

Write-Host "Generating icons..." -ForegroundColor Cyan
Start-Process -FilePath $exe -ArgumentList "--emit-icons" -Wait -NoNewWindow

Write-Host "Build complete." -ForegroundColor Green
