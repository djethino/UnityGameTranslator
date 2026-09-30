using System.Collections.Generic;
using System.Text;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// How a positioned glyph is named as a codepoint in the display string. The font asset
    /// answers: a glyph the cmap maps, at its natural position, is its own codepoint; anything
    /// else — an unmapped glyph, or a glyph shifted by positioning — is a private-use codepoint
    /// the asset hands out (see FontShaping). 0 when it cannot name one (the private range is
    /// exhausted): the run is then shown unshaped rather than half-shaped.
    /// </summary>
    internal interface IGlyphNamer
    {
        int CodepointFor(int glyph, int xOffset, int yOffset, int advanceDelta);
    }

    /// <summary>
    /// A whole display string shaped through the font's OpenType tables, run by run: the
    /// stretches of text the Indic shaper covers are shaped and rewritten as the codepoints
    /// that name their glyphs; everything else — Latin, tags, placeholders, spaces, newlines —
    /// stays exactly as it was. Stage B2 of the pipeline, for components drawn by a font asset
    /// of ours (D8: the result is a presented text, registered as such by the caller).
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class OpenTypeText
    {
        /// <summary>Does this text hold a run the shapers would act on?</summary>
        internal static bool NeedsShaping(string text) => OpenTypeShaping.NeedsShaping(text);

        /// <summary>
        /// Shape every run of <paramref name="text"/>. Returns the same instance when nothing
        /// changed. A run is a maximal stretch of characters of the shaped blocks with the
        /// joiners between them; a space, a tag, a digit or a Latin letter ends it — a font's
        /// rules never cross those, and the tags must survive as text.
        /// </summary>
        internal static string Shape(string text, IShapingFont font, IGlyphNamer namer)
        {
            if (string.IsNullOrEmpty(text) || font == null || namer == null) return text;
            StringBuilder sb = null;
            int copied = 0; // how much of the original is already in sb
            int i = 0;
            while (i < text.Length)
            {
                int cp = CodePointAt(text, i, out int width);
                if (!InRun(cp)) { i += width; continue; }
                int start = i;
                while (i < text.Length && InRun(CodePointAt(text, i, out width))) i += width;
                // A run made only of joiners, spaces or marks has nothing to shape.
                if (!OpenTypeShaping.NeedsShaping(text.Substring(start, i - start))) continue;

                string run = text.Substring(start, i - start);
                string shaped = ShapeRun(run, font, namer);
                if (shaped == null || shaped == run) continue;
                if (sb == null) sb = new StringBuilder(text.Length + 16);
                sb.Append(text, copied, start - copied);
                // A right-to-left run (Hebrew, Adlam…) is shaped in logical order, its marks placed
                // for the other pen direction (ResolveAttachments): the RTL composer reverses it.
                // Named by private codepoints, it no longer SAYS it is right-to-left — they are
                // left-to-right letters to the bidi algorithm, and the line read backwards (probe 3,
                // 2026-10-01). The override marks carry the direction through; the composer applies
                // them and drops them (X9).
                bool rightToLeft = IsRightToLeftRun(run);
                if (rightToLeft) sb.Append(RightToLeftOverride);
                sb.Append(shaped);
                if (rightToLeft) sb.Append(PopDirectionalFormatting);
                copied = i;
            }
            if (sb == null) return text;
            sb.Append(text, copied, text.Length - copied);
            return sb.ToString();
        }

        /// <summary>
        /// One stretch of typed text drawn as one inseparable group of glyphs — a syllable, a
        /// conjunct with its signs: the smallest place a caret can stand around in an edited text
        /// (RtlFieldLayout). <see cref="Glyphs"/> names the glyphs as <see cref="Shape"/> does.
        /// </summary>
        internal struct ShapedUnit
        {
            public int Start, Length;   // UTF-16 range of the typed text
            public string Glyphs;
        }

        /// <summary>
        /// The shaped runs of <paramref name="text"/> cut into units: glyphs grouped so that no
        /// unit draws a character another unit also draws (a pre-base sign drawn before its
        /// consonant stays with it). Text outside the shaped runs is not listed. A run whose glyphs
        /// cannot all be named is left out, as <see cref="Shape"/> leaves it unshaped.
        /// </summary>
        internal static List<ShapedUnit> ShapeUnits(string text, IShapingFont font, IGlyphNamer namer)
        {
            var units = new List<ShapedUnit>();
            if (string.IsNullOrEmpty(text) || font == null || namer == null) return units;
            int i = 0;
            while (i < text.Length)
            {
                int cp = CodePointAt(text, i, out int width);
                if (!InRun(cp)) { i += width; continue; }
                int start = i;
                while (i < text.Length && InRun(CodePointAt(text, i, out width))) i += width;
                string run = text.Substring(start, i - start);
                if (!OpenTypeShaping.NeedsShaping(run)) continue;

                var glyphs = OpenTypeShaping.Shape(run, font);
                var named = new string[glyphs.Count];
                bool ok = glyphs.Count > 0;
                for (int k = 0; k < glyphs.Count && ok; k++)
                {
                    var g = glyphs[k];
                    int code = namer.CodepointFor(g.Glyph, g.XOffset, g.YOffset, g.XAdvance - font.AdvanceWidth(g.Glyph));
                    if (code <= 0) ok = false;
                    else named[k] = char.ConvertFromUtf32(code);
                }
                if (!ok) continue;

                // A unit ends where every glyph after it comes from later text than every glyph
                // before it: the minimum of what follows exceeds the maximum of what precedes.
                var minAfter = new int[glyphs.Count + 1];
                minAfter[glyphs.Count] = run.Length;
                for (int k = glyphs.Count - 1; k >= 0; k--) minAfter[k] = System.Math.Min(minAfter[k + 1], glyphs[k].Cluster);
                int unitFirst = 0, maxBefore = -1;
                for (int k = 0; k < glyphs.Count; k++)
                {
                    maxBefore = System.Math.Max(maxBefore, glyphs[k].Cluster);
                    if (minAfter[k + 1] <= maxBefore) continue;
                    int from = minAfter[unitFirst], to = minAfter[k + 1];
                    var sb = new StringBuilder();
                    for (int u = unitFirst; u <= k; u++) sb.Append(named[u]);
                    // Characters no glyph drew (a joiner) belong to the unit before them.
                    var unit = new ShapedUnit { Start = start + (unitFirst == 0 ? 0 : from), Length = to - (unitFirst == 0 ? 0 : from), Glyphs = sb.ToString() };
                    // A caret never stands inside a user-perceived character either: a unit that
                    // starts on a sign, or on the consonant a virama joins, goes with the one before.
                    int last = units.Count - 1;
                    if (last >= 0 && units[last].Start + units[last].Length == unit.Start && !GraphemeBoundary(text, unit.Start))
                        units[last] = new ShapedUnit { Start = units[last].Start, Length = units[last].Length + unit.Length, Glyphs = units[last].Glyphs + unit.Glyphs };
                    else units.Add(unit);
                    unitFirst = k + 1;
                }
            }
            return units;
        }

        /// <summary>
        /// Whether a user-perceived character may end before <paramref name="i"/> — the parts of
        /// Unicode's extended grapheme clusters (UAX #29) that concern a shaped run: never before
        /// a combining sign (GB9, GB9a) or a joiner (GB9), nor between a virama and the consonant it
        /// joins (GB9c, the Indic conjunct rule of Unicode 15.1). Written here because the
        /// runtimes the mod runs on (.NET Framework, Mono) know only the older combining rule.
        /// </summary>
        internal static bool GraphemeBoundary(string text, int i)
        {
            if (i <= 0 || i >= text.Length) return true;
            int cp = char.ConvertToUtf32(text, char.IsLowSurrogate(text[i]) && i > 0 ? i - 1 : i);
            if (char.IsLowSurrogate(text[i])) return false;
            if (cp == 0x200C || cp == 0x200D) return false;
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark || category == System.Globalization.UnicodeCategory.SpacingCombiningMark
                || category == System.Globalization.UnicodeCategory.EnclosingMark) return false;
            // GB9c: Linker (the viramas of InCB=Linker), then a consonant.
            int prev = text[i - 1];
            bool linker = prev == 0x094D || prev == 0x09CD || prev == 0x0ACD || prev == 0x0B4D || prev == 0x0C4D || prev == 0x0D4D;
            return !(linker && category == System.Globalization.UnicodeCategory.OtherLetter);
        }

        /// <summary>
        /// What a run is made of: the letters of a shaped script, the joiners and the dotted
        /// circle, and the combining marks of any block (they belong to the letter before them).
        /// A space ends a run: a font's rules never cross one, and it keeps runs short.
        /// </summary>
        internal const char RightToLeftOverride = '\u202E', PopDirectionalFormatting = '\u202C';

        /// <summary>A run's direction: its first character of a real script (marks and joiners say nothing).</summary>
        internal static bool IsRightToLeftRun(string run)
        {
            for (int i = 0; i < run.Length;)
            {
                int script = ShapingCommon.ScriptOf(CodePointAt(run, i, out int width));
                i += width;
                if (script == ShapingTables.Script.Inherited || script == ShapingTables.Script.Common || script == ShapingTables.Script.Unknown) continue;
                return ShapingCommon.IsRightToLeft(script);
            }
            return false;
        }

        private static bool InRun(int cp)
        {
            if (cp == 0x200C || cp == 0x200D || cp == 0x25CC) return true;
            if (cp < 0x0300) return false;
            if (cp >= 0x0300 && cp <= 0x036F) return true;
            int script = ShapingCommon.ScriptOf(cp);
            if (script == ShapingTables.Script.Inherited) return true;
            if (script == ShapingTables.Script.Common || script == ShapingTables.Script.Unknown || script == ShapingTables.Script.Latin
                || script == ShapingTables.Script.Arabic || script == ShapingTables.Script.Han || script == ShapingTables.Script.Hiragana
                || script == ShapingTables.Script.Katakana || script == ShapingTables.Script.Hangul || script == ShapingTables.Script.Cyrillic
                || script == ShapingTables.Script.Greek)
                return false;
            return true;
        }

        /// <summary>The code point at <paramref name="i"/> and how many chars it takes (a surrogate pair is one).</summary>
        private static int CodePointAt(string s, int i, out int width)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { width = 2; return char.ConvertToUtf32(c, s[i + 1]); }
            width = 1;
            return c;
        }

        /// <summary>One run: glyphs in, codepoints out; null when a glyph could not be named.</summary>
        private static string ShapeRun(string run, IShapingFont font, IGlyphNamer namer)
        {
            var glyphs = OpenTypeShaping.Shape(run, font);
            var sb = new StringBuilder(glyphs.Count);
            foreach (var g in glyphs)
            {
                int cp = namer.CodepointFor(g.Glyph, g.XOffset, g.YOffset, g.XAdvance - font.AdvanceWidth(g.Glyph));
                if (cp <= 0) return null;
                if (cp > 0xFFFF) sb.Append(char.ConvertFromUtf32(cp));
                else sb.Append((char)cp);
            }
            return sb.ToString();
        }
    }
}
