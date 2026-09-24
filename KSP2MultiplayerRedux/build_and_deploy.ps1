#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the KSP2 Multiplayer Redux client mod, assembles a release folder, optionally deploys it
  into your local KSP2 install, and zips it for distribution.

.PARAMETER Ksp2Dir
  Path to your KSP2 install. Defaults to $env:KSP2DIR, then the standard Steam location.

.PARAMETER NoDeploy
  Build and zip only; do not copy into the game's mods folder.

.EXAMPLE
  .\build_and_deploy.ps1
  .\build_and_deploy.ps1 -Ksp2Dir "D:\Games\Kerbal Space Program 2"
  .\build_and_deploy.ps1 -NoDeploy
#>
param(
    [string]$Ksp2Dir = $(if ($env:KSP2DIR) { $env:KSP2DIR } else { "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2" }),
    [switch]$NoDeploy
)

$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
$distDir    = Join-Path $projectDir "dist\KSP2MultiplayerRedux"
$zipFile    = Join-Path $projectDir "KSP2MultiplayerRedux_Release.zip"
$modsDir    = Join-Path $Ksp2Dir "mods"

Write-Host "1. Building mod (Release)..." -ForegroundColor Cyan
Push-Location $projectDir
try {
    dotnet build KSP2MultiplayerRedux.csproj -c Release -p:KSP2DIR="$Ksp2Dir"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }

Write-Host "2. Assembling release folder..." -ForegroundColor Cyan
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -Path $distDir -ItemType Directory -Force | Out-Null

# LiteNetLib is merged INTO the mod DLL at build time (see ILRepack.targets), so only the
# single DLL plus the manifest and UI assets are shipped.
Copy-Item (Join-Path $projectDir "bin\Release\netstandard2.1\KSP2MultiplayerRedux.dll") -Destination $distDir -Force
Copy-Item (Join-Path $projectDir "swinfo.json") -Destination $distDir -Force

$assetsDir = Join-Path $distDir "assets\ui"
New-Item -Path $assetsDir -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $projectDir "Assets\KSP2MultiplayerRedux\UI\multiplayer_menu.uxml") -Destination $assetsDir -Force

if (-not $NoDeploy) {
    Write-Host "3. Deploying to local KSP2 install..." -ForegroundColor Cyan
    if (-not (Test-Path $modsDir)) {
        Write-Warning "KSP2 mods folder not found at '$modsDir'. Skipping deploy (use -Ksp2Dir or set KSP2DIR)."
    } else {
        if (Get-Process -Name "KSP2_x64" -ErrorAction SilentlyContinue) {
            throw "KSP2 is running. Close the game before deploying (the DLL is locked)."
        }
        $target = Join-Path $modsDir "KSP2MultiplayerRedux"
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Copy-Item -Path $distDir -Destination $modsDir -Recurse -Force
        Write-Host " -> Deployed to $target" -ForegroundColor Green
    }
} else {
    Write-Host "3. Deploy skipped (-NoDeploy)." -ForegroundColor DarkGray
}

Write-Host "4. Zipping release..." -ForegroundColor Cyan
if (Test-Path $zipFile) { Remove-Item $zipFile -Force }
Compress-Archive -Path $distDir -DestinationPath $zipFile
Write-Host " -> $zipFile" -ForegroundColor Green

Write-Host "Done." -ForegroundColor Yellow
