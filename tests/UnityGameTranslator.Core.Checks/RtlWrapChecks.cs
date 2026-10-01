using System;
using System.Collections.Generic;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A right-to-left text wrapped by TMP from its flagged form: a left-to-right run of several
    /// words crossing a line end had its words swapped between the lines ("…של Schedule I?" shown
    /// "…של I" / "?Schedule", 2026-10-01). The cure is ours to check without Unity: which texts
    /// carry such a run, where the lines are cut (in logical order, with a ruler given), and that
    /// composing the cut lines as ONE flagged string keeps each line's words on their line.
    /// </summary>
    internal static class RtlWrapChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            Detection(check);
            Cutting(check);
            Composing(check);
        }

        private static void Detection(Action<bool, string, string> check)
        {
            check(RtlComposer.HasLtrRunAcrossSpace("מה אתה רוצה לראות בעדכון הבא של Schedule I?"),
                "a Latin name of two words in Hebrew is a run across a space", "the case seen in game");
            check(!RtlComposer.HasLtrRunAcrossSpace("מה אתה רוצה לראות בעדכון הבא של Schedule?"),
                "one Latin word is not", "its letters stay together whatever the cut");
            check(!RtlComposer.HasLtrRunAcrossSpace("שלום עולם ומלואו"),
                "Hebrew alone is not", "spaces at the paragraph's own level");
            check(RtlComposer.HasLtrRunAcrossSpace("مرحبا بكم في Unity Game Translator اليوم"),
                "Arabic around three Latin words is", "same rule, any right-to-left script");
        }

        // One unit per character: the ruler is the caller's, its unit does not matter.
        private static float? Chars(string s) => s.Length;

        private static void Cutting(Action<bool, string, string> check)
        {
            var lines = GreedyLines.Cut("a bb ccc dddd", 6f, Chars);
            check(lines != null && string.Join("|", lines) == "a bb|ccc|dddd",
                "cut at the last space that fits, in logical order", lines == null ? "null" : string.Join("|", lines));

            lines = GreedyLines.Cut("short\nand longer words", 9f, Chars);
            check(lines != null && string.Join("|", lines) == "short|and|longer|words",
                "an existing break is kept as a cut of its own", lines == null ? "null" : string.Join("|", lines));

            lines = GreedyLines.Cut("x <link=\"a b\">yy</link> z", 4f, Chars);
            bool tagWhole = lines != null && lines.Exists(l => l.Contains("<link=\"a b\">"));
            check(tagWhole, "never cut inside a tag", lines == null ? "null" : string.Join("|", lines));

            lines = GreedyLines.Cut("tiny enormousword end", 6f, Chars);
            check(lines != null && string.Join("|", lines) == "tiny|enormousword|end",
                "a word wider than the width stays whole on its line", lines == null ? "null" : string.Join("|", lines));

            check(GreedyLines.Cut("a b", 5f, _ => null) == null,
                "no ruler, no cut", "the engine's own lines are kept");
        }

        private static void Composing(Action<bool, string, string> check)
        {
            // The two lines as they must read: "…של Schedule" then "I?".
            string composed = RtlComposer.Compose("מה אתה רוצה לראות בעדכון הבא של Schedule\nI?", RtlOutput.RtlFlagged);
            var lines = composed.Split('\n');
            bool kept = lines.Length == 2
                        && lines[0].Contains("eludehcS") && lines[0].Contains("מה") && !lines[0].Contains("I")
                        && lines[1].Contains("I") && !lines[1].Contains("eludehcS");
            check(kept, "each cut line keeps its own words once composed flagged", string.Join(" / ", lines));
        }
    }
}
