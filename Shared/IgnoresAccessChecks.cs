// Compile-time and runtime visibility bypass for Unity internal/private members.
//
// Why this exists:
//   UniverseLib accesses private/internal Unity fields directly via IL
//   (e.g. EventSystem.m_CurrentInputModule). When UniverseLib was shipped as
//   a separate assembly named "UniverseLib.Mono", the runtime allowed those
//   accesses (the assembly was loaded with a skip-verification context by
//   BepInEx/MelonLoader's loader).
//
//   After ILRepack merges UniverseLib types into UnityGameTranslator.dll,
//   the calling-assembly identity changes from "UniverseLib.Mono" to
//   "UnityGameTranslator". The runtime then enforces visibility checks
//   against UnityGameTranslator and throws FieldAccessException because
//   UnityEngine.UI does not whitelist us via [InternalsVisibleTo].
//
//   IgnoresAccessChecksToAttribute tells the runtime to skip those checks
//   when this assembly accesses members of the named target. The runtime
//   matches the attribute by full name (System.Runtime.CompilerServices.
//   IgnoresAccessChecksToAttribute), so we can declare the type ourselves
//   without depending on a system library that ships it.
//
//   The attribute must live on the merged DLL's primary (non-internalized)
//   assembly, which is why we include this file in each adapter project.
//
// Companion: IgnoresAccessChecksApplications.cs holds the [assembly: ...]
//   declarations (must be in a separate file because [assembly:] tokens
//   must syntactically precede any type declaration in the file they live in).

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName)
        {
            AssemblyName = assemblyName;
        }

        public string AssemblyName { get; }
    }
}
