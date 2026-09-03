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
        /// <summary>Does this text hold a run the shaper would act on?</summary>
        internal static bool NeedsShaping(string text) => IndicShaper.NeedsShaping(text);

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
                if (!InRun(text[i])) { i++; continue; }
                int start = i;
                while (i < text.Length && InRun(text[i])) i++;
                // A run made only of joiners has nothing to shape.
                bool hasLetter = false;
                for (int k = start; k < i && !hasLetter; k++) hasLetter = text[k] >= IndicTables.IndicFirst && text[k] <= IndicTables.IndicLast;
                if (!hasLetter) continue;

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

        private static bool InRun(char c)
        {
            return (c >= IndicTables.IndicFirst && c <= IndicTables.IndicLast) || c == '‌' || c == '‍' || c == '◌';
        }

        /// <summary>One run: glyphs in, codepoints out; null when a glyph could not be named.</summary>
        private static string ShapeRun(string run, IShapingFont font, IGlyphNamer namer)
        {
            var glyphs = IndicShaper.Shape(run, font);
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
