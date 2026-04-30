# Build a Velopack installer locally on Windows.
#
# Usage:
#   ./scripts/build-installer.ps1 -Version 0.1.0
#
# Output: ./releases/OpenApparatusStudio-win-Setup.exe (and update .nupkg)

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Rid = 'win-x64',

    [string]$Channel = 'win'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path "$PSScriptRoot/.."
Set-Location $repoRoot

$project = 'src/OpenApparatus.Studio/OpenApparatus.Studio.csproj'
$packId = 'OpenApparatusStudio'
$packTitle = 'OpenApparatus Studio'
$packAuthors = 'OpenApparatus'

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host 'Installing Velopack CLI (vpk)...'
    dotnet tool install -g vpk
}

Write-Host "Publishing $Rid (self-contained)..."
dotnet publish $project `
    -c Release `
    -r $Rid `
    --self-contained true `
    -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Packing v$Version..."
vpk pack `
    --packId $packId `
    --packTitle $packTitle `
    --packAuthors $packAuthors `
    --packVersion $Version `
    --packDir publish `
    --mainExe OpenApparatus.Studio.exe `
    --channel $Channel `
    --outputDir releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host "`nDone. Artifacts in ./releases:"
Get-ChildItem releases | Format-Table Name, Length
