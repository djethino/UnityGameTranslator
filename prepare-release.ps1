#!/usr/bin/env pwsh
# UnityGameTranslator Release Preparation Script
# Usage: ./prepare-release.ps1

$ErrorActionPreference = "Stop"

# Read version from Directory.Build.props (Directory.Build.props has multiple PropertyGroup elements)
[xml]$props = Get-Content "Directory.Build.props"
$Version = ($props.Project.PropertyGroup | Where-Object { $_.Version }).Version

Write-Host "=== UnityGameTranslator Release $Version ===" -ForegroundColor Cyan

# Refuse to ship code that works on Mono and dies on IL2CPP. Cheap, and the only barrier that
# catches it: the compiler cannot, and neither can a Mono-only test round.
& "$PSScriptRoot/check-il2cpp-safety.ps1"
if ($LASTEXITCODE -ne 0) { exit 1 }

# Build UniverseLib first (our fork with custom changes)
Write-Host "`nBuilding UniverseLib..." -ForegroundColor Yellow

$universeLibConfigs = @(
    @{ Name = "Mono"; Config = "Release_Mono" }
    @{ Name = "IL2CPP-BepInEx"; Config = "Release_IL2CPP_Interop_BIE" }
    @{ Name = "IL2CPP-MelonLoader"; Config = "Release_IL2CPP_Interop_ML" }
)

foreach ($ulib in $universeLibConfigs) {
    Write-Host "  Building UniverseLib $($ulib.Name)..." -ForegroundColor Gray -NoNewline
    dotnet build "UniverseLib/src/UniverseLib.sln" -c $ulib.Config --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host " FAILED" -ForegroundColor Red
        exit 1
    }
    Write-Host " OK" -ForegroundColor Green
}

# Update reference DLL for Core compilation
$refSource = "UniverseLib/Release/UniverseLib.Mono/UniverseLib.Mono.dll"
$refDest = "extlibs/UniverseLib/UniverseLib.Mono.dll"
if (Test-Path $refSource) {
    Copy-Item $refSource $refDest -Force
    Write-Host "  Updated extlibs reference DLL" -ForegroundColor DarkGray
}

# Create releases directory
$releasesDir = "releases"
if (Test-Path $releasesDir) {
    Remove-Item -Recurse -Force $releasesDir
}
New-Item -ItemType Directory -Path $releasesDir | Out-Null

# Build all projects
Write-Host "`nBuilding projects..." -ForegroundColor Yellow

$projects = @(
    @{ Name = "BepInEx5"; Path = "UnityGameTranslator-BepInEx5/UnityGameTranslator.BepInEx5.csproj" },
    @{ Name = "BepInEx6-Mono"; Path = "UnityGameTranslator-BepInEx6-Mono/UnityGameTranslator.BepInEx6Mono.csproj" },
    @{ Name = "BepInEx6-IL2CPP"; Path = "UnityGameTranslator-BepInEx6-IL2CPP/UnityGameTranslator.BepInEx6IL2CPP.csproj" },
    @{ Name = "MelonLoader-Mono"; Path = "UnityGameTranslator-MelonLoader-Mono/UnityGameTranslator.MelonLoaderMono.csproj" },
    @{ Name = "MelonLoader-IL2CPP"; Path = "UnityGameTranslator-MelonLoader-IL2CPP/UnityGameTranslator.MelonLoaderIL2CPP.csproj" }
)

foreach ($proj in $projects) {
    Write-Host "  Building $($proj.Name)..." -ForegroundColor Gray
    dotnet build $proj.Path -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  FAILED!" -ForegroundColor Red
        exit 1
    }
}

Write-Host "All builds successful!" -ForegroundColor Green

# NOTE: config.json is NOT included in releases
# The mod creates it on first run with defaults
# This prevents overwriting user settings during updates

# Create zip for each mod loader
Write-Host "`nCreating release zips..." -ForegroundColor Yellow

# Each adapter ships a single ILRepack-merged DLL (Newtonsoft + UniverseLib + Core embedded)
$releasePackages = @(
    @{ Name = "BepInEx5";           Dll = "UnityGameTranslator-BepInEx5/bin/UnityGameTranslator.dll" }
    @{ Name = "BepInEx6-Mono";      Dll = "UnityGameTranslator-BepInEx6-Mono/bin/UnityGameTranslator.dll" }
    @{ Name = "BepInEx6-IL2CPP";    Dll = "UnityGameTranslator-BepInEx6-IL2CPP/bin/UnityGameTranslator.dll" }
    @{ Name = "MelonLoader-Mono";   Dll = "UnityGameTranslator-MelonLoader-Mono/bin/UnityGameTranslator.dll" }
    @{ Name = "MelonLoader-IL2CPP"; Dll = "UnityGameTranslator-MelonLoader-IL2CPP/bin/UnityGameTranslator.dll" }
)

foreach ($pkg in $releasePackages) {
    $stagingDir = "$releasesDir/UnityGameTranslator-$($pkg.Name)-v$Version"
    $zipName = "UnityGameTranslator-$($pkg.Name)-v$Version.zip"
    $zipPath = "$releasesDir/$zipName"
    New-Item -ItemType Directory -Path $stagingDir | Out-Null
    Copy-Item $pkg.Dll $stagingDir
    Compress-Archive -Path "$stagingDir/*" -DestinationPath $zipPath
    Remove-Item -Recurse -Force $stagingDir

    # Per-file SHA256 checksum, sha256sum-compatible format: "<hash>  <filename>" + LF.
    # Lowercase hash and two spaces so users can verify with `sha256sum -c <file>.sha256`.
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
    Set-Content -Path "$zipPath.sha256" -Value "$hash  $zipName`n" -NoNewline -Encoding utf8

    Write-Host "  Created $zipName (+ .sha256)" -ForegroundColor Gray
}

Write-Host "`n=== Release packages ready in ./releases/ ===" -ForegroundColor Green
Get-ChildItem $releasesDir -File | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name)" -ForegroundColor Cyan
}
Write-Host "  (attach the .sha256 files alongside their .zip on the GitHub release)" -ForegroundColor DarkGray
