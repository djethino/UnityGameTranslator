// Apply [module: UnverifiableCode] to bypass Mono runtime IL access checks for
// code merged into our DLL by ILRepack — specifically UniverseLib, which directly
// reads private Unity fields (e.g. EventSystem.m_CurrentInputModule) via IL.
//
// Why module-level (not assembly-level):
//   UniverseLib.Mono.dll has [module: UnverifiableCode] (auto-emitted by csc when
//   compiling unsafe blocks). Pre-ILRepack, the JIT sees this attribute on the
//   calling method's module and skips IL verification + access checks. That's
//   why direct ldfld/stfld on Unity private fields works without
//   InternalsVisibleTo or IgnoresAccessChecksTo.
//
// After ILRepack merges UniverseLib types into our DLL, the calling module
// becomes the merged adapter's module. ILRepack does NOT carry over module-level
// custom attributes from non-primary inputs — so we must declare [module:
// UnverifiableCode] explicitly on the primary adapter.
//
// IgnoresAccessChecksTo (the .NET 5+ attribute) was tried first but is not
// honored by the Mono runtime shipped with Unity 2021.3. UnverifiableCode is.
//
// Companion: IgnoresAccessChecks.cs holds the [assembly: ...] declarations
// (must be in a separate file because [assembly: ...] tokens must syntactically
// precede any type declaration).

// Module-level: skips IL verification + access checks. Honored by Mono runtime
// (Unity 2021.3, BepInEx 5/6 Mono, MelonLoader Mono).
[module: System.Security.UnverifiableCode]

// Assembly-level: explicit access-check bypass per target assembly. Honored by
// .NET 5+ runtime (BepInEx 6 IL2CPP, MelonLoader IL2CPP via Il2CppInterop).
// Defense-in-depth alongside UnverifiableCode.
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("UnityEngine")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("UnityEngine.CoreModule")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("UnityEngine.UI")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("UnityEngine.UIModule")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("UnityEngine.IMGUIModule")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("UnityEngine.TextRenderingModule")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Unity.TextMeshPro")]
