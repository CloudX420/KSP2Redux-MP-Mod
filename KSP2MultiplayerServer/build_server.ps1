#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes the KSP2 Multiplayer Redux dedicated server (framework-dependent, win-x64) to .\dist
  and zips it for distribution.

.PARAMETER Runtime
  .NET runtime identifier. Default win-x64. Use linux-x64 to host on Linux.

.EXAMPLE
  .\build_server.ps1
  .\build_server.ps1 -Runtime linux-x64
#>
param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
$distDir    = Join-Path $projectDir "dist"
$zipFile    = Join-Path $projectDir "KSP2MultiplayerServer_Release.zip"

Write-Host "1. Publishing server ($Runtime)..." -ForegroundColor Cyan
Push-Location $projectDir
try {
    dotnet publish KSP2MultiplayerServer.csproj -c Release -r $Runtime --self-contained false -o $distDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }

Write-Host "2. Zipping release..." -ForegroundColor Cyan
if (Test-Path $zipFile) { Remove-Item $zipFile -Force }
Compress-Archive -Path (Join-Path $distDir "*") -DestinationPath $zipFile
Write-Host " -> $zipFile" -ForegroundColor Green
Write-Host "Done." -ForegroundColor Yellow
