using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Every engine call that hands a string to native code goes through its door: TransformFind.Path
    /// and EngineStrings in the Core, UnityHelpers.FindChild / NewGameObject in UniverseLib (pointed at
    /// them).
    ///
    /// 🔴 On Unity 2023.1+ under IL2CPP such a call throws MissingMethodException at every call in a
    /// game that never makes it itself (the interop rebuilds its stripped body around a ReadOnlySpan
    /// member the game's runtime lacks): with Transform.Find, the mod's window was never built. A
    /// direct call compiles and passes every game that kept the method — so it is refused here, by
    /// reading the sources. Caught: a lookup written on a member or variable named transform /
    /// Transform, a GameObject made with a name, GameObject.Find, Application.OpenURL, the clipboard.
    /// Not caught: Object.name set on an object (the same text sets plain data's names too) — those go
    /// through EngineStrings.SetName by review.
    /// </summary>
    internal static class TransformFindChecks
    {
        private static readonly (Regex Call, string Door)[] DirectCalls =
        {
            (new Regex(@"\b[tT]ransform\s*\.\s*Find\s*\("), "TransformFind.Path / UnityHelpers.FindChild"),
            (new Regex(@"new\s+GameObject\s*\(\s*[^)\s]"), "EngineStrings.NewGameObject / UnityHelpers.NewGameObject"),
            (new Regex(@"\bGameObject\s*\.\s*Find\s*\("), "EngineStrings.FindGameObject"),
            (new Regex(@"\bApplication\s*\.\s*OpenURL\s*\("), "EngineStrings.OpenUrl"),
            (new Regex(@"\bsystemCopyBuffer\s*="), "EngineStrings.SetClipboard"),
            // An ARRAY to native code, same family: the pointer overload, the array pinned (2026-10-03).
            (new Regex(@"\.\s*LoadRawTextureData\s*\("), "TextureUtils.LoadRawTextureDataSafe"),
        };

        // Files allowed to name the engine call: the doors and their defaults, and one UniverseLib line
        // that runs for a single other game only (EventSystemHelper, VRChat).
        private static readonly HashSet<string> Doors = new HashSet<string> { "TransformFind.cs", "EngineStrings.cs", "UnityHelpers.cs", "EventSystemHelper.cs" };

        public static void Run(Action<bool, string, string> check)
        {
            string core = FindFolder("UnityGameTranslator.Core", "UI");
            string universe = core == null ? null : Path.Combine(Path.GetDirectoryName(core), "UniverseLib", "src");
            check(core != null && Directory.Exists(universe), "the Core and UniverseLib sources are found",
                "the check reads files; without them it proves nothing");
            if (core == null || !Directory.Exists(universe)) return;

            check(File.Exists(Path.Combine(core, "TransformFind.cs")) && File.Exists(Path.Combine(core, "EngineStrings.cs")),
                "TransformFind.cs and EngineStrings.cs exist", "the doors");

            int files = 0;
            var offenders = new List<string>();
            foreach (var root in new[] { core, universe })
                foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    if (Doors.Contains(name)) continue;
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                    files++;
                    string source = StripComments(File.ReadAllText(file));
                    foreach (var rule in DirectCalls)
                        if (rule.Call.IsMatch(source)) offenders.Add(name + " (" + rule.Door + ")");
                }
            check(files > 0, "the sources hold files to read", "a rule with nothing to guard is decoration");
            check(offenders.Count == 0, "no file hands a string to the engine itself",
                offenders.Count == 0 ? "TransformFind / EngineStrings and UniverseLib's UnityHelpers hooks are the only doors"
                                     : "direct call in: " + string.Join(", ", offenders));
        }

        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", "");
        }

        /// <summary>Up from the binary until the folder is found.</summary>
        private static string FindFolder(string name, string marker)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, name);
                if (Directory.Exists(Path.Combine(candidate, marker))) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
