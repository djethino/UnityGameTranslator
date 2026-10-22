using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What an Apply forgets of the game's sizes (TranslatorPatches.ClearFontSizeCache): only the
    /// working copy of fontSize, whose game value survives in _trueOriginalFontSizes.
    ///
    /// 🔴 **Why it is a check** (2026-10-22): the cache also cleared the auto-size bounds and the older
    /// TMP's sizes, which have no second record — every Apply read the bound the mod had scaled as
    /// the game's and scaled it again (17 → 14.3 → 12 → 10.1 → 8.5 on a menu). Best Fit had been
    /// protected the same way long before; the two beside it were not, and nothing said so.
    /// </summary>
    internal static class SizeMemoryChecks
    {
        /// <summary>What ClearFontSizeCache may forget: a copy whose game value is kept elsewhere.</summary>
        private static readonly HashSet<string> Forgettable = new HashSet<string> { "_originalFontSizes" };

        private static readonly Regex Cleared = new Regex(@"(\w+)\.Clear\(\)", RegexOptions.Compiled);

        public static void Run(Action<bool, string, string> check)
        {
            string core = OwnUiSizeChecks.FindCoreFolder();
            check(core != null, "the Core's sources are found", core ?? "not found from " + AppContext.BaseDirectory);
            if (core == null) return;

            string source = File.ReadAllText(Path.Combine(core, "TranslatorPatches.cs"));
            // The two an Apply goes through: the cache dropped, then every size written again.
            foreach (string name in new[] { "ClearFontSizeCache", "ReapplyAllFontSizes" })
            {
                var method = Regex.Match(source, @"static\s+void\s+" + name + @"\s*\(\s*\)");
                string body = method.Success ? OwnUiSizeChecks.BodyAfter(source, method.Index + method.Length) : null;
                check(body != null, name + " is found", "the Apply path forgets sizes through it");
                if (body == null) continue;

                var refused = Forbidden(body);
                check(refused.Count == 0, name + " forgets no size that is the only record of the game's",
                      refused.Count == 0 ? "only the fontSize working copy" : "cleared: " + string.Join(", ", refused)
                      + " — read back scaled, it is scaled again at every Apply");
            }

            // The check itself can fail: clearing a bound is caught.
            check(Forbidden("{ _originalFontSizes.Clear(); _originalAutoSizeMax.Clear(); }").Count == 1,
                  "clearing an auto-size bound is caught", "the pattern is not decoration");
        }

        private static List<string> Forbidden(string body)
        {
            var refused = new List<string>();
            foreach (Match m in Cleared.Matches(body))
                if (!Forgettable.Contains(m.Groups[1].Value)) refused.Add(m.Groups[1].Value);
            return refused;
        }
    }
}
