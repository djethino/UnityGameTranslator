#!/usr/bin/env pwsh
# UnityGameTranslator Release Preparation Script
# Usage: ./prepare-release.ps1

$ErrorActionPreference = "Stop"

# Read version from Directory.Build.props (Directory.Build.props has multiple PropertyGroup elements)
[xml]$props = Get-Content "Directory.Build.props"
$Version = ($props.Project.PropertyGroup | Where-Object { $_.Version }).Version

Write-Host "=== UnityGameTranslator Release $Version ===" -ForegroundColor Cyan

# 🔴 **Never ship a build that talks to a development site.**
#
# The three URLs are compiled into the DLL (PluginInfo.g.cs), so a release made while they point at
# a local address produces a mod that reaches nothing on a player's machine — and says so only in a
# log nobody reads. The mistake is easy: pointing the build at Herd is a normal thing to do while
# testing, and putting the file back afterwards is a thing to remember.
#
# ⚠ It refuses DEVELOPMENT addresses, not "addresses that are not ours". Self-hosting is a
# supported case: somebody building this mod for their own instance must be able to, and their
# domain is none of this script's business.
function Assert-NotADevelopmentUrl {
    param([string] $Name, [string] $Url)

    if ([string]::IsNullOrWhiteSpace($Url)) {
        Write-Host "  $Name is empty" -ForegroundColor Red
        exit 1
    }

    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) {
        Write-Host "  $Name is not an absolute URL: $Url" -ForegroundColor Red
        exit 1
    }

    # ⚠ NOT $host: PowerShell reserves it for the shell itself, and assigning to it throws
    # "Cannot overwrite variable Host because it is read-only" — a failure that reads as a script
    # bug rather than the check doing its job.
    $urlHost = $uri.Host
    $isLocal = $uri.IsLoopback `
        -or $urlHost -eq 'localhost' `
        -or $urlHost -like '*.test' `
        -or $urlHost -like '*.local' `
        -or $urlHost -like '*.localhost' `
        -or $uri.Scheme -ne 'https'

    if ($isLocal) {
        Write-Host "  $Name points at a development address: $Url" -ForegroundColor Red
        Write-Host "  Put Directory.Build.props back before releasing." -ForegroundColor Yellow
        Write-Host "  (To build against a local site, use ./build-and-deploy.ps1 -Local," -ForegroundColor DarkGray
        Write-Host "   which passes the addresses per build and leaves this file alone.)" -ForegroundColor DarkGray
        exit 1
    }
}

Write-Host "`nChecking the addresses this build will carry..." -ForegroundColor Yellow
foreach ($urlName in @('ApiBaseUrl', 'WebsiteBaseUrl', 'SseBaseUrl')) {
    $value = ($props.Project.PropertyGroup | Where-Object { $_.$urlName }).$urlName
    Assert-NotADevelopmentUrl -Name $urlName -Url $value
    Write-Host "  $urlName -> $value" -ForegroundColor DarkGray
}

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
