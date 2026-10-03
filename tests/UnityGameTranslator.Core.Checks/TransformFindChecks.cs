using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Every child looked up by name goes through TransformFind.Path (the Core) or
    /// UnityHelpers.FindChild (UniverseLib, pointed at it).
    ///
    /// 🔴 Transform.Find throws MissingMethodException at every call on Unity 2023.1+ under IL2CPP in a
    /// game that never calls it (the interop rebuilds its stripped body around a ReadOnlySpan member
    /// the game's runtime lacks): the mod's window is never built. A direct call compiles and passes
    /// every game that kept the method — so it is refused here, by reading the sources. Caught: a
    /// lookup written on a member or variable named transform / Transform; another receiver's name
    /// escapes this reading.
    /// </summary>
    internal static class TransformFindChecks
    {
        private static readonly Regex DirectCall = new Regex(@"\b[tT]ransform\s*\.\s*Find\s*\(");

        public static void Run(Action<bool, string, string> check)
        {
            string core = FindFolder("UnityGameTranslator.Core", "UI");
            string universe = core == null ? null : Path.Combine(Path.GetDirectoryName(core), "UniverseLib", "src");
            check(core != null && Directory.Exists(universe), "the Core and UniverseLib sources are found",
                "the check reads files; without them it proves nothing");
            if (core == null || !Directory.Exists(universe)) return;

            check(File.Exists(Path.Combine(core, "TransformFind.cs")), "TransformFind.cs exists", "the one place a child is found from");

            int files = 0;
            var offenders = new List<string>();
            foreach (var root in new[] { core, universe })
                foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    if (name == "TransformFind.cs" || name == "UnityHelpers.cs") continue;   // the door, and its default
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                    files++;
                    if (DirectCall.IsMatch(StripComments(File.ReadAllText(file)))) offenders.Add(name);
                }
            check(files > 0, "the sources hold files to read", "a rule with nothing to guard is decoration");
            check(offenders.Count == 0, "no file looks a child up with Transform.Find itself",
                offenders.Count == 0 ? "TransformFind.Path / UnityHelpers.FindChild are the only doors"
                                     : "direct Transform.Find in: " + string.Join(", ", offenders) + " — call TransformFind.Path (Core) or UnityHelpers.FindChild (UniverseLib)");
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
