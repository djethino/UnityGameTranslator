using System;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Pre-base vowel signs put where they are drawn. Every expected string below is written
    /// by hand from the Unicode chart (which sign is drawn left of which cluster), never read
    /// back from the reorderer. Code points are spelt out so a viewer's own shaping cannot hide
    /// what the string holds.
    /// </summary>
    internal static class IndicReorderChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // Devanagari — the bench words (silk1/silk2), misspelt on screen before this stage.
            Expect(check, "विक्ल्प", "िवक्ल्प",
                "विकल्प: i before its consonant", "the short i is stored after व and drawn before it");
            Expect(check, "अतिरिक्त", "अितिरक्त",
                "अतिरिक्त: two i signs, each to its own consonant", "the second one must not cross the first");
            Expect(check, "क्ति", "िक्त",
                "क्ति: i jumps the whole conjunct", "virama binds क and त into one cluster");
            Expect(check, "निकलें", "िनकलें",
                "निकलें: other signs untouched", "e and anusvara are not left signs and stay put");
            Expect(check, "ड़ि", "िड़",
                "ड़ि: the nukta travels with its consonant", "nukta is part of the cluster the sign jumps");

            // Two-part signs: the left part moves, the right part stays where the sign was.
            Expect(check, "কো", "েকা",
                "Bengali কো splits into e + aa", "canonical decomposition, left part first");
            Expect(check, "கொ", "ெகா",
                "Tamil கொ splits into e + aa", "same rule, another script, no code of its own");
            Expect(check, "കോ", "േകാ",
                "Malayalam കോ splits into ee + aa", "");
            Expect(check, "କୈ", "େକୖ",
                "Oriya କୈ splits into e + ai length mark (top)", "a top part stays in place like a right one");
            Expect(check, "කෝ", "ෙකා්",
                "Sinhala කෝ decomposes two levels deep", "ෝ → ො + ් → ෙ + ා + ්");
            Expect(check, "\U00011315\U0001134B", "\U00011347\U00011315\U0001133E",
                "Grantha (supplementary plane) splits too", "surrogate pairs are one code point each");

            // Other scripts, the same rule.
            Expect(check, "ਸਿ", "ਿਸ", "Gurmukhi ਸਿ", "");
            Expect(check, "કિ", "િક", "Gujarati કિ", "");
            Expect(check, "မြေ", "ေြမ",
                "Myanmar မြေ: medial ra, then e outermost", "each left sign goes to the head — the later one ends leftmost");
            Expect(check, "កេ", "េក", "Khmer កេ", "");
            Expect(check, "កោ", "េកា",
                "Khmer កោ splits into e + aa", "the one split the standard describes but does not decompose");
            Expect(check, "ស្តើ", "ើស្ត",
                "Khmer ស្តើ: the sign jumps the coeng cluster", "invisible stacker + consonant are one cluster; no split exists for ើ");

            // What must not move.
            string thai = "เกม";
            check(ReferenceEquals(IndicReorderer.Reorder(thai), thai) && !IndicReorderer.NeedsReordering(thai),
                "Thai เกม untouched", "Thai stores its left vowels in visual order already (Visual_Order_Left)");
            check(!IndicReorderer.NeedsReordering("Hello") && !IndicReorderer.NeedsReordering("مرحبا بكم"),
                "Latin and Arabic never need it", "the range check answers before any table is read");
            check(ReferenceEquals(IndicReorderer.Reorder("Hello"), "Hello"),
                "a text with nothing to move comes back as the same instance", "callers register presented text by reference");

            // Our own output coming back: NOT recognisable from the string — which is why the
            // presenter asks the presented-text registry before reordering, never the reorderer.
            string once = IndicReorderer.Reorder("अतिरिक्त");
            string twice = IndicReorderer.Reorder(once);
            check(once != twice,
                "reordering is NOT idempotent (documented)", "a moved sign follows the previous syllable's consonant like an unmoved one would — the echo must be caught by the registry");

            // Markup is a cluster boundary.
            Expect(check, "<b>वि</b>", "<b>िव</b>",
                "a sign inside a tag pair moves within it", "'<' and '>' are not cluster code points");
            string afterTag = "व</b>ि";
            check(ReferenceEquals(IndicReorderer.Reorder(afterTag), afterTag),
                "a sign separated from its consonant by a tag stays", "documented limit: the tag breaks the cluster");
        }

        private static void Expect(Action<bool, string, string> check, string logical, string expected, string what, string why)
        {
            string got = IndicReorderer.Reorder(logical);
            check(got == expected, what, got == expected ? why : $"got {Hex(got)}, expected {Hex(expected)}");
        }

        private static string Hex(string s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < 128) { sb.Append(s[i]); continue; }
                sb.Append("\\u").Append(((int)s[i]).ToString("X4"));
            }
            return sb.ToString();
        }
    }
}
