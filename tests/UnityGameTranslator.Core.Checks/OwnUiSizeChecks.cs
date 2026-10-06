using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The mod's own interface keeps its size whatever the game's fonts are set to (issue #32):
    /// every place in TranslatorPatches that resizes a text to a font's Size % must leave ours alone.
    ///
    /// 🔴 **Why it is a check**: six such places existed and five had the guard; the sixth, the
    /// UI.Text size setter, relied on a narrower test that only knew the texts registered as ours.
    /// A game whose font was the engine's Arial — the one the mod's interface is drawn in — set to
    /// 60% shrank every label of ours built after its panel. A guard missing from one path looks like
    /// nothing until a player meets it.
    /// </summary>
    internal static class OwnUiSizeChecks
    {
        // The methods that put a font's scale on a text: the size setters' prefixes and the
        // functions that re-apply a scale.
        private static readonly Regex Resizer = new Regex(
            @"(?:public|private|internal)\s+static\s+\w+\s+(\w*SetFontSize_Prefix|Apply\w*FontScale)\s*\(",
            RegexOptions.Compiled);

        public static void Run(Action<bool, string, string> check)
        {
            string core = FindCoreFolder();
            check(core != null, "the Core's sources are found", core ?? "not found from " + AppContext.BaseDirectory);
            if (core == null) return;

            string source = File.ReadAllText(Path.Combine(core, "TranslatorPatches.cs"));
            var missing = new List<string>();
            int seen = 0;

            foreach (Match m in Resizer.Matches(source))
            {
                seen++;
                string body = BodyAfter(source, m.Index + m.Length);
                if (body == null || !body.Contains("IsOwnUIText(")) missing.Add(m.Groups[1].Value);
            }

            check(seen >= 4, "the size setters and scale functions are found", seen + " found");
            check(missing.Count == 0, "every one of them leaves the mod's own interface at its size (issue #32)",
                  missing.Count == 0 ? "all guarded" : "no IsOwnUIText in: " + string.Join(", ", missing));

            // The check itself can fail: a resizer without the guard is caught.
            string broken = "private static void ApplyFakeFontScale(object instance) { float s = 1; }";
            var found = Resizer.Match(broken);
            check(found.Success && !BodyAfter(broken, found.Index + found.Length).Contains("IsOwnUIText("),
                  "a resizer without the guard is caught", "the pattern is not decoration");
        }

        /// <summary>The method's body, from its opening brace to the matching closing one.</summary>
        private static string BodyAfter(string source, int from)
        {
            int open = source.IndexOf('{', from);
            if (open < 0) return null;
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            return null;
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
