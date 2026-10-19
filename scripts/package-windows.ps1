[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z.-]*$')]
    [string]$Version = "0.1.3"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot "artifacts"
$packageName = "ChordPort-$Version-win-x64"
$publishDirectory = Join-Path $artifactRoot $packageName
$zipPath = Join-Path $artifactRoot "$packageName.zip"

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $publishDirectory
Remove-Item -Force -ErrorAction SilentlyContinue $zipPath

dotnet publish (Join-Path $repoRoot "src/ChordPort.Api/ChordPort.Api.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $publishDirectory

Copy-Item (Join-Path $PSScriptRoot "Start-ChordPort.cmd") (Join-Path $publishDirectory "Start-ChordPort.cmd") -Force
Compress-Archive -Path $publishDirectory -DestinationPath $zipPath -Force

Write-Host "Portable tester package created: $zipPath"
