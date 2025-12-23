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

# Create default config.json
$configJson = @'
{
  "ollama_url": "http://localhost:11434",
  "model": "qwen3:8b",
  "target_language": "auto",
  "source_language": "auto",
  "game_context": "",
  "enable_ollama": false,
  "normalize_numbers": true,
  "preload_model": true,
  "debug_ollama": false
}
'@

# Create zip for each mod loader
Write-Host "`nCreating release zips..." -ForegroundColor Yellow

# BepInEx 5
$bepinex5Dir = "$releasesDir/UnityGameTranslator-BepInEx5-v$Version"
New-Item -ItemType Directory -Path $bepinex5Dir | Out-Null
Copy-Item "UnityGameTranslator-BepInEx5/bin/Release/UnityGameTranslator.dll" $bepinex5Dir
Copy-Item "UnityGameTranslator-BepInEx5/bin/Release/UnityGameTranslator.Core.dll" $bepinex5Dir
Copy-Item "UnityGameTranslator-BepInEx5/bin/Release/Newtonsoft.Json.dll" $bepinex5Dir
$configJson | Out-File -FilePath "$bepinex5Dir/config.json" -Encoding utf8NoBOM
Compress-Archive -Path "$bepinex5Dir/*" -DestinationPath "$releasesDir/UnityGameTranslator-BepInEx5-v$Version.zip"
Write-Host "  Created UnityGameTranslator-BepInEx5-v$Version.zip" -ForegroundColor Gray

# BepInEx 6 Mono
$bepinex6MonoDir = "$releasesDir/UnityGameTranslator-BepInEx6-Mono-v$Version"
New-Item -ItemType Directory -Path $bepinex6MonoDir | Out-Null
Copy-Item "UnityGameTranslator-BepInEx6-Mono/bin/Release/UnityGameTranslator.dll" $bepinex6MonoDir
Copy-Item "UnityGameTranslator-BepInEx6-Mono/bin/Release/UnityGameTranslator.Core.dll" $bepinex6MonoDir
Copy-Item "UnityGameTranslator-BepInEx6-Mono/bin/Release/Newtonsoft.Json.dll" $bepinex6MonoDir
$configJson | Out-File -FilePath "$bepinex6MonoDir/config.json" -Encoding utf8NoBOM
Compress-Archive -Path "$bepinex6MonoDir/*" -DestinationPath "$releasesDir/UnityGameTranslator-BepInEx6-Mono-v$Version.zip"
Write-Host "  Created UnityGameTranslator-BepInEx6-Mono-v$Version.zip" -ForegroundColor Gray

# BepInEx 6 IL2CPP
$bepinex6IL2CPPDir = "$releasesDir/UnityGameTranslator-BepInEx6-IL2CPP-v$Version"
New-Item -ItemType Directory -Path $bepinex6IL2CPPDir | Out-Null
Copy-Item "UnityGameTranslator-BepInEx6-IL2CPP/bin/Release/UnityGameTranslator.dll" $bepinex6IL2CPPDir
Copy-Item "UnityGameTranslator-BepInEx6-IL2CPP/bin/Release/UnityGameTranslator.Core.dll" $bepinex6IL2CPPDir
Copy-Item "UnityGameTranslator-BepInEx6-IL2CPP/bin/Release/Newtonsoft.Json.dll" $bepinex6IL2CPPDir
$configJson | Out-File -FilePath "$bepinex6IL2CPPDir/config.json" -Encoding utf8NoBOM
Compress-Archive -Path "$bepinex6IL2CPPDir/*" -DestinationPath "$releasesDir/UnityGameTranslator-BepInEx6-IL2CPP-v$Version.zip"
Write-Host "  Created UnityGameTranslator-BepInEx6-IL2CPP-v$Version.zip" -ForegroundColor Gray

# MelonLoader
$melonDir = "$releasesDir/UnityGameTranslator-MelonLoader-v$Version"
New-Item -ItemType Directory -Path $melonDir | Out-Null
Copy-Item "UnityGameTranslator-MelonLoader/bin/Release/UnityGameTranslator.dll" $melonDir
Copy-Item "UnityGameTranslator-MelonLoader/bin/Release/UnityGameTranslator.Core.dll" $melonDir
Copy-Item "UnityGameTranslator-MelonLoader/bin/Release/Newtonsoft.Json.dll" $melonDir
$configJson | Out-File -FilePath "$melonDir/config.json" -Encoding utf8NoBOM
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
