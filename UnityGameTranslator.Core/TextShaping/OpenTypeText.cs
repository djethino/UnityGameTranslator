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
                sb.Append(shaped);
                copied = i;
            }
            if (sb == null) return text;
            sb.Append(text, copied, text.Length - copied);
            return sb.ToString();
        }

        /// <summary>
        /// What a run is made of: the letters of a shaped script, the joiners and the dotted
        /// circle, and the combining marks of any block (they belong to the letter before them).
        /// A space ends a run: a font's rules never cross one, and it keeps runs short.
        /// </summary>
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
