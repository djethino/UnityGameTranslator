using System;
using System.Collections.Generic;
using System.Text;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Puts the pre-base vowel signs of the Brahmic scripts where they are drawn.
    ///
    /// In Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Malayalam, Sinhala, Myanmar,
    /// Khmer and their relatives, some dependent signs are STORED after the consonant cluster
    /// they belong to but WRITTEN to its left (the short i of विकल्प, the e of கெ), and some are
    /// written on both sides at once (Tamil கொ, Bengali কো, Malayalam കോ). A text engine that
    /// applies no OpenType shaping draws the string in storage order, so the sign lands on the
    /// wrong consonant and the word reads as a different word — every such word on the bench
    /// was misspelt (शकिारी for शिकारी). Moving the left part before its cluster in the string
    /// is what the shaping engines do first, and it needs nothing from the font: the sign's
    /// glyph is designed to be drawn where it stands.
    ///
    /// 🔴 ONE rule, and only Unicode data under it — never a script name. Which signs have a
    /// left part, what a cluster is made of, and how a two-part sign splits come from
    /// <see cref="IndicTables"/>, generated from the Unicode Character Database. Thai, Lao and
    /// the Tai scripts store their left vowels in visual order already (Visual_Order_Left) and
    /// are not in the table on purpose.
    ///
    /// A cluster, read backwards from the sign: a base consonant with what attaches to it
    /// (nukta, medials, subjoined consonants, killers, joiners), then, only across a binder
    /// (virama, stacker), the previous base and its attachments, and so on. Two bare consonants
    /// in a row are two syllables — अतिरिक्त carries two i signs, one per consonant, and the
    /// second must not jump the first. A left sign already moved in front of the cluster (a
    /// Myanmar medial ra before its base) is part of it: the next left sign goes in front of
    /// that too, which is how ေ ends up outermost in မြေ.
    ///
    /// ⚠ NOT idempotent, by construction: once moved, a sign follows the PREVIOUS syllable's
    /// consonant and looks exactly like an unmoved sign of that syllable. Our own output coming
    /// back through the setter is recognised by the presented-text registry (what we wrote →
    /// what it came from), never by re-reading the string — the caller asks that first.
    ///
    /// What this does NOT do, and no string transform can: form conjuncts (half forms, reph,
    /// subjoined consonants) — those are glyphs only the font's own tables reach. Without
    /// them the virama stays visible, which is legible; a misplaced vowel is not.
    ///
    /// Pure by contract: no Unity, no state, no clock.
    /// </summary>
    internal static class IndicReorderer
    {
        private static readonly int MinLeftSign = IndicTables.LeftSigns[0];
        private static readonly int MaxLeftSign = IndicTables.LeftSigns[IndicTables.LeftSigns.Length - 1];

        /// <summary>Does this text hold any sign that may have to move? A range check, then a binary search per candidate.</summary>
        internal static bool NeedsReordering(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                int cp = CodePointAt(text, i, out int width);
                if (cp >= MinLeftSign && cp <= MaxLeftSign && IsLeftSign(cp)) return true;
                i += width - 1;
            }
            return false;
        }

        /// <summary>
        /// The text with every left part in front of its cluster. Returns the SAME instance when
        /// nothing had to move — callers compare by reference to know whether to register the
        /// result as presented text.
        /// </summary>
        internal static string Reorder(string text)
        {
            if (!NeedsReordering(text)) return text;

            var cps = new List<int>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                cps.Add(CodePointAt(text, i, out int width));
                i += width - 1;
            }

            bool moved = false;
            for (int i = 0; i < cps.Count; i++)
            {
                int cp = cps[i];
                if (cp < MinLeftSign || cp > MaxLeftSign || !IsLeftSign(cp)) continue;

                int start = ClusterStart(cps, i);
                if (start < 0) continue;   // nothing to jump over — orphaned sign

                int[] parts = SplitOf(cp);
                if (parts == null)
                {
                    cps.RemoveAt(i);
                    cps.Insert(start, cp);
                }
                else
                {
                    // The first part moves; the others take the sign's place, in order.
                    cps.RemoveAt(i);
                    for (int p = parts.Length - 1; p >= 1; p--) cps.Insert(i, parts[p]);
                    cps.Insert(start, parts[0]);
                    i += parts.Length - 1;   // past the parts left in place (the loop's i++ does the +1)
                }
                moved = true;
            }
            if (!moved) return text;

            var sb = new StringBuilder(cps.Count + 4);
            foreach (int cp in cps)
            {
                if (cp > 0xFFFF) sb.Append(char.ConvertFromUtf32(cp));
                else sb.Append((char)cp);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Where the cluster the sign at <paramref name="signIndex"/> belongs to begins, or -1
        /// when no base consonant precedes it.
        /// </summary>
        private static int ClusterStart(List<int> cps, int signIndex)
        {
            int pos = signIndex - 1;
            int start = -1;
            while (true)
            {
                while (pos >= 0 && In(IndicTables.AttachRanges, cps[pos])) pos--;
                if (pos < 0 || !In(IndicTables.BaseRanges, cps[pos])) break;
                start = pos;
                pos--;
                // Across a binder (a joiner may sit between it and this base), the previous base
                // belongs to the same cluster.
                int p = pos;
                while (p >= 0 && In(IndicTables.JoinerRanges, cps[p])) p--;
                if (p < 0 || !In(IndicTables.BinderRanges, cps[p])) break;
                pos = p - 1;
            }
            if (start < 0) return -1;
            // Left signs already standing in front of this cluster are part of it.
            while (start > 0 && IsLeftSign(cps[start - 1])) start--;
            return start;
        }

        private static bool IsLeftSign(int cp) => Array.BinarySearch(IndicTables.LeftSigns, cp) >= 0;

        /// <summary>Binary search over (first, last) range pairs.</summary>
        private static bool In(int[] ranges, int cp)
        {
            int lo = 0, hi = ranges.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int first = ranges[mid * 2], last = ranges[mid * 2 + 1];
                if (cp < first) hi = mid - 1;
                else if (cp > last) lo = mid + 1;
                else return true;
            }
            return false;
        }

        private static int[] SplitOf(int cp)
        {
            var s = IndicTables.Splits;
            for (int i = 0; i < s.Length;)
            {
                int sign = s[i], count = s[i + 1];
                if (sign == cp)
                {
                    var parts = new int[count];
                    Array.Copy(s, i + 2, parts, 0, count);
                    return parts;
                }
                if (sign > cp) return null;   // sorted
                i += 2 + count;
            }
            return null;
        }

        private static int CodePointAt(string s, int i, out int width)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                width = 2;
                return char.ConvertToUtf32(c, s[i + 1]);
            }
            width = 1;
            return c;
        }
    }
}
