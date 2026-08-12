#!/usr/bin/env pwsh
<#
    Refuse to build code that compiles on Mono and dies on IL2CPP.

    UnityGameTranslator.Core is compiled ONCE against Mono UnityEngine/UniverseLib and then runs on
    both runtimes. Several Mono APIs simply do not exist under IL2CPP: the call compiles, passes
    every Mono test, and throws MissingMethodException at runtime — where nothing but the game
    itself can report it.

    Nothing else catches this: not the compiler, and not the Mono games in the deploy script.
    Hence a build-time check rather than a note somebody reads afterwards.

    See CLAUDE.md and analyse/input-capture-and-priority.md.

    Exits 1 on the first rule with violations. Silent and exit 0 when clean.
#>

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$core = Join-Path $root "UnityGameTranslator.Core"

if (-not (Test-Path $core)) {
    Write-Host "  [IL2CPP check] UnityGameTranslator.Core not found at $core" -ForegroundColor Red
    exit 1
}

$rules = @(
    @{
        Name    = "AddListener on a Unity UnityEvent"
        # Under IL2CPP a UnityAction/UnityAction<T> is an Il2Cpp proxy, not a .NET delegate. The
        # throw lands inside a panel constructor, aborting CreatePanels() and taking the whole mod
        # UI down with it.
        Pattern = '\.AddListener\s*\('
        # Its raw calls are the Mono-only branches, guarded by Adapter.IsIL2CPP, with the IL2CPP
        # path going through reflection + Il2Cpp delegate conversion.
        Allowed = @("UIHelpers.cs")
        Advice  = @(
            "InputField -> InputFieldRef.OnValueChanged   |   Button -> ButtonRef.OnClick",
            "Toggle / Slider / EventTrigger -> UIHelpers.Add*Listener"
        )
    },
    @{
        Name    = "non-generic GetComponent(Type)"
        # GameObject.GetComponent(System.Type) does not exist under IL2CPP. A direct open paren is
        # exactly what tells the two apart: the generic form always reads GetComponent<T>(.
        # Found the hard way — UIHelpers.GetComponentSafe<T> used it while its own summary claimed
        # it was "IL2CPP compatible", throwing every frame and, because it sat upstream of the
        # cursor handover, costing the player their mouse pointer.
        Pattern = '\.GetComponents?(InChildren|InParent)?\s*\('
        # The one legitimate home: TypeHelper.GetComponentByType, whose type is a runtime value and
        # so CANNOT be generic. It carries the reflection fallback, and says so once when even that
        # fails. Everyone else goes through it.
        Allowed = @("TypeHelper.cs")
        Advice  = @(
            "Type known at compile time -> GetComponent<T>() (used all over the Core, proven on IL2CPP)",
            "Type only known at runtime -> follow ImageReplacer's reflection fallback"
        )
    }
)

foreach ($rule in $rules) {
    $violations = Get-ChildItem -Path $core -Filter *.cs -Recurse |
        Where-Object { $rule.Allowed -notcontains $_.Name } |
        Select-String -Pattern $rule.Pattern -CaseSensitive |
        Where-Object { $_.Line -notmatch '^\s*(//|///|\*)' }   # a comment naming the trap is not the trap

    if ($violations) {
        Write-Host ""
        Write-Host ("  [IL2CPP check] FAILED - {0}, outside {1}" -f $rule.Name, ($rule.Allowed -join ", ")) -ForegroundColor Red
        foreach ($v in $violations) {
            $rel = $v.Path.Substring($root.Length).TrimStart('\', '/')
            Write-Host ("    {0}:{1}" -f $rel, $v.LineNumber) -ForegroundColor Red
            Write-Host ("      {0}" -f $v.Line.Trim()) -ForegroundColor DarkGray
        }
        Write-Host ""
        Write-Host "  This compiles and works on Mono, then throws MissingMethodException on IL2CPP." -ForegroundColor Yellow
        foreach ($line in $rule.Advice) {
            Write-Host ("  {0}" -f $line) -ForegroundColor Yellow
        }
        Write-Host ""
        exit 1
    }
}

exit 0
