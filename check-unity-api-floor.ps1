#!/usr/bin/env pwsh
<#
    Refuse to ship a Core that uses something the oldest supported Unity does not have.

    The mod compiles against a recent Unity, so the compiler only sees today's API. A member added
    later -- or one whose TYPE changed, like InputField.onEndEdit -- compiles, passes every test on a
    recent game, and on an older one makes the method naming it unloadable. v0.13.4 shipped two such
    references: on Unity before 2019.1 the first panel threw and the mod's window stayed empty.

    Runs tests/UnityGameTranslator.UnityApiFloor after the Core is built: every Unity type and member
    the Core names, matched on its full signature against the floor's assemblies (Unity 2018.1, the
    first with a stable .NET 4 runtime). What is absent on purpose -- guarded by its caller -- is
    listed in that folder's allowed.txt, method by method, with its reason.

    The floor's assemblies live in extlibs/UnityFloor/ (not redistributable, so not in git); the
    check says which files it needs when they are missing.

    Exits 1 when something is missing and not allowed, or when an allowance is stale.
#>

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "tests/UnityGameTranslator.UnityApiFloor/UnityGameTranslator.UnityApiFloor.csproj"

Write-Host "  Unity API floor..." -ForegroundColor Gray -NoNewline

# No --nologo: `dotnet run` does not take it and hands it to the program as an argument.
$output = & dotnet run --project $project -c Release 2>&1
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host " FAILED" -ForegroundColor Red
    $output | Where-Object { $_ -notmatch '^\s*allowed ' } | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    exit 1
}

Write-Host " OK" -ForegroundColor Green
exit 0
