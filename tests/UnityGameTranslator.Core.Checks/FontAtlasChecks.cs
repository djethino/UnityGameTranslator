using System;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Every fill of a dynamic font's atlas goes through FontAtlas.Request.
    ///
    /// 🔴 The public Font.RequestCharactersInTexture throws MissingMethodException at every call on
    /// Unity 2023.1+ under IL2CPP (its rebuilt body needs a ReadOnlySpan member the game's runtime
    /// lacks): the atlas never fills and legacy text draws without its glyphs. FontAtlas calls the
    /// native entry there. A direct call added anywhere else compiles, passes every bench below
    /// 2023, and fails in exactly those games — so it is refused here, by reading the sources.
    /// </summary>
    internal static class FontAtlasChecks
    {
        private static readonly Regex DirectCall = new Regex(@"\.RequestCharactersInTexture\s*\(");

        public static void Run(Action<bool, string, string> check)
        {
            string core = FindCoreFolder();
            check(core != null, "the Core sources are found", "the check reads files; without them it proves nothing");
            if (core == null) return;

            check(File.Exists(Path.Combine(core, "FontAtlas.cs")), "FontAtlas.cs exists",
                "the one place an atlas is filled from");

            int files = 0;
            var offenders = new System.Collections.Generic.List<string>();
            foreach (var file in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "FontAtlas.cs") continue;
                files++;
                if (DirectCall.IsMatch(StripComments(File.ReadAllText(file)))) offenders.Add(Path.GetFileName(file));
            }
            check(files > 0, "the Core holds files to read", "a rule with nothing to guard is decoration");
            check(offenders.Count == 0, "no file of the Core fills an atlas itself",
                offenders.Count == 0 ? "FontAtlas.Request is the only door"
                                     : "direct RequestCharactersInTexture in: " + string.Join(", ", offenders) + " — call FontAtlas.Request");
        }

        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", "");
        }

        /// <summary>Up from the binary until the Core folder is found.</summary>
        private static string FindCoreFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "UnityGameTranslator.Core");
                if (Directory.Exists(Path.Combine(candidate, "UI"))) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
