#!/usr/bin/env pwsh
# UnityGameTranslator Release Preparation Script
# Usage: ./prepare-release.ps1

$ErrorActionPreference = "Stop"

# Read version from Directory.Build.props
[xml]$props = Get-Content "Directory.Build.props"
$Version = $props.Project.PropertyGroup.Version

Write-Host "=== UnityGameTranslator Release $Version ===" -ForegroundColor Cyan

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
    @{ Name = "MelonLoader"; Path = "UnityGameTranslator-MelonLoader/UnityGameTranslator.MelonLoader.csproj" }
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

# BepInEx 5
$bepinex5Dir = "$releasesDir/UnityGameTranslator-BepInEx5-v$Version"
New-Item -ItemType Directory -Path $bepinex5Dir | Out-Null
Copy-Item "UnityGameTranslator-BepInEx5/bin/UnityGameTranslator.dll" $bepinex5Dir
Copy-Item "UnityGameTranslator-BepInEx5/bin/UnityGameTranslator.Core.dll" $bepinex5Dir
Copy-Item "UnityGameTranslator-BepInEx5/bin/Newtonsoft.Json.dll" $bepinex5Dir
# UniverseLib for uGUI
Copy-Item "UniverseLib/Release/NuGet_Mono/lib/net35/UniverseLib.Mono.dll" $bepinex5Dir
Compress-Archive -Path "$bepinex5Dir/*" -DestinationPath "$releasesDir/UnityGameTranslator-BepInEx5-v$Version.zip"
Write-Host "  Created UnityGameTranslator-BepInEx5-v$Version.zip" -ForegroundColor Gray

# BepInEx 6 Mono
$bepinex6MonoDir = "$releasesDir/UnityGameTranslator-BepInEx6-Mono-v$Version"
New-Item -ItemType Directory -Path $bepinex6MonoDir | Out-Null
Copy-Item "UnityGameTranslator-BepInEx6-Mono/bin/UnityGameTranslator.dll" $bepinex6MonoDir
Copy-Item "UnityGameTranslator-BepInEx6-Mono/bin/UnityGameTranslator.Core.dll" $bepinex6MonoDir
Copy-Item "UnityGameTranslator-BepInEx6-Mono/bin/Newtonsoft.Json.dll" $bepinex6MonoDir
# UniverseLib for uGUI
Copy-Item "UniverseLib/Release/UniverseLib.Mono/UniverseLib.Mono.dll" $bepinex6MonoDir
Compress-Archive -Path "$bepinex6MonoDir/*" -DestinationPath "$releasesDir/UnityGameTranslator-BepInEx6-Mono-v$Version.zip"
Write-Host "  Created UnityGameTranslator-BepInEx6-Mono-v$Version.zip" -ForegroundColor Gray

# BepInEx 6 IL2CPP
$bepinex6IL2CPPDir = "$releasesDir/UnityGameTranslator-BepInEx6-IL2CPP-v$Version"
New-Item -ItemType Directory -Path $bepinex6IL2CPPDir | Out-Null
Copy-Item "UnityGameTranslator-BepInEx6-IL2CPP/bin/UnityGameTranslator.dll" $bepinex6IL2CPPDir
Copy-Item "UnityGameTranslator-BepInEx6-IL2CPP/bin/UnityGameTranslator.Core.dll" $bepinex6IL2CPPDir
Copy-Item "UnityGameTranslator-BepInEx6-IL2CPP/bin/Newtonsoft.Json.dll" $bepinex6IL2CPPDir
# UniverseLib IL2CPP variant for BepInEx
Copy-Item "UniverseLib/Release/NuGet_IL2CPP_Interop/lib/net6.0/UniverseLib.BIE.IL2CPP.Interop.dll" $bepinex6IL2CPPDir
Compress-Archive -Path "$bepinex6IL2CPPDir/*" -DestinationPath "$releasesDir/UnityGameTranslator-BepInEx6-IL2CPP-v$Version.zip"
Write-Host "  Created UnityGameTranslator-BepInEx6-IL2CPP-v$Version.zip" -ForegroundColor Gray

# MelonLoader (includes both Mono and IL2CPP variants)
$melonDir = "$releasesDir/UnityGameTranslator-MelonLoader-v$Version"
New-Item -ItemType Directory -Path $melonDir | Out-Null
Copy-Item "UnityGameTranslator-MelonLoader/bin/UnityGameTranslator.dll" $melonDir
Copy-Item "UnityGameTranslator-MelonLoader/bin/UnityGameTranslator.Core.dll" $melonDir
Copy-Item "UnityGameTranslator-MelonLoader/bin/Newtonsoft.Json.dll" $melonDir
# UniverseLib - both variants, mod detects and loads correct one
Copy-Item "UniverseLib/Release/UniverseLib.Mono/UniverseLib.Mono.dll" $melonDir
Copy-Item "UniverseLib/Release/NuGet_IL2CPP_Interop/lib/net6.0/UniverseLib.ML.IL2CPP.Interop.dll" $melonDir
Compress-Archive -Path "$melonDir/*" -DestinationPath "$releasesDir/UnityGameTranslator-MelonLoader-v$Version.zip"
Write-Host "  Created UnityGameTranslator-MelonLoader-v$Version.zip" -ForegroundColor Gray

# Cleanup temp directories
Remove-Item -Recurse -Force $bepinex5Dir
Remove-Item -Recurse -Force $bepinex6MonoDir
Remove-Item -Recurse -Force $bepinex6IL2CPPDir
Remove-Item -Recurse -Force $melonDir

Write-Host "`n=== Release packages ready in ./releases/ ===" -ForegroundColor Green
Get-ChildItem $releasesDir -Filter "*.zip" | ForEach-Object {
    Write-Host "  $($_.Name)" -ForegroundColor Cyan
}
