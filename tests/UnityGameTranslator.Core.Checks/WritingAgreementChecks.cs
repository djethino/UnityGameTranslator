using System;
using System.Collections.Generic;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// common's script-of-a-letter table (Writing.Tables.g.cs) against the mod's ShapingTables, over
    /// the whole code space.
    ///
    /// 🔴 Both are written by tools/generate-shaping-tables.py in one run from one UCD — "one Unicode
    /// for everything" (CLAUDE.md). This is what notices one of them regenerated without the other:
    /// strict source would then decide a line by a Unicode the mod does not shape with.
    /// </summary>
    internal static class WritingAgreementChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            check(Writing.UnicodeVersion == ShapingTables.UnicodeVersion,
                "the socle's script table is of the mod's Unicode",
                $"{Writing.UnicodeVersion} / {ShapingTables.UnicodeVersion}: regenerate both with tools/generate-shaping-tables.py");

            var codeOf = new Dictionary<int, string>();
            int disagreements = 0;
            string first = null;
            for (int cp = 0; cp < 0x110000; cp++)
            {
                if (cp >= 0xD800 && cp <= 0xDFFF) continue;

                int script = ShapingCommon.ScriptOf(cp);
                bool realScript = script != ShapingTables.Script.Common && script != ShapingTables.Script.Inherited
                                  && script != ShapingTables.Script.Unknown;
                bool modLetter = realScript && UnicodeInfo.IsLetter(cp);
                string code = Writing.ScriptOfLetter(cp);

                bool agrees = (code != null) == modLetter;
                if (agrees && code != null)
                {
                    if (codeOf.TryGetValue(script, out var seen)) agrees = seen == code;
                    else codeOf[script] = code;
                }

                if (!agrees)
                {
                    disagreements++;
                    if (first == null) first = $"U+{cp:X4}";
                }
            }

            check(disagreements == 0,
                "every letter has the same script in the socle and in the mod",
                disagreements == 0 ? "1.1 million code points, one answer each" : $"{disagreements} differ, first {first}");
        }
    }
}
