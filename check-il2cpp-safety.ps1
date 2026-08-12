#!/usr/bin/env pwsh
<#
    Refuse to build code that compiles on Mono and dies on IL2CPP.

    UnityGameTranslator.Core is compiled ONCE against Mono UniverseLib and then runs on both
    runtimes. Under IL2CPP a UnityAction/UnityAction<T> is an Il2Cpp proxy, not a .NET delegate:
    calling AddListener on a Unity UnityEvent from Core compiles, passes every Mono test, and
    throws MissingMethodException at runtime — inside a panel constructor, which aborts
    CreatePanels() and takes the whole mod UI down with it.

    Nothing else catches this: not the compiler, and not the 8 Mono games in the deploy script.
    Hence a build-time check rather than a note somebody reads afterwards.

    See CLAUDE.md, "NEVER call AddListener on a Unity UnityEvent from UnityGameTranslator.Core",
    and analyse/startup-order-and-setup-gate.md.

    Exits 1 on the first violation. Silent and exit 0 when clean.
#>

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$core = Join-Path $root "UnityGameTranslator.Core"

if (-not (Test-Path $core)) {
    Write-Host "  [IL2CPP check] UnityGameTranslator.Core not found at $core" -ForegroundColor Red
    exit 1
}

# The ONLY file allowed to call AddListener: its raw calls are the Mono-only branches, guarded by
# Adapter.IsIL2CPP, with the IL2CPP path going through reflection + Il2Cpp delegate conversion.
$allowed = "UIHelpers.cs"

$violations = Get-ChildItem -Path $core -Filter *.cs -Recurse |
    Where-Object { $_.Name -ne $allowed } |
    Select-String -Pattern '\.AddListener\s*\(' -CaseSensitive |
    Where-Object { $_.Line -notmatch '^\s*(//|///|\*)' }   # a comment naming the trap is not the trap

if ($violations) {
    Write-Host ""
    Write-Host "  [IL2CPP check] FAILED - AddListener called outside $allowed" -ForegroundColor Red
    foreach ($v in $violations) {
        $rel = $v.Path.Substring($root.Length).TrimStart('\', '/')
        Write-Host ("    {0}:{1}" -f $rel, $v.LineNumber) -ForegroundColor Red
        Write-Host ("      {0}" -f $v.Line.Trim()) -ForegroundColor DarkGray
    }
    Write-Host ""
    Write-Host "  This compiles and works on Mono, then throws MissingMethodException on IL2CPP." -ForegroundColor Yellow
    Write-Host "  InputField -> InputFieldRef.OnValueChanged   |   Button -> ButtonRef.OnClick" -ForegroundColor Yellow
    Write-Host "  Toggle / Slider / EventTrigger -> UIHelpers.Add*Listener" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

exit 0
