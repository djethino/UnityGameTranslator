using System;

namespace UnityGameTranslator.Core.Engine
{
    /// <summary>
    /// The units of a kerning pair a font asset keeps — what it should hold, read against what the
    /// font gave.
    ///
    /// A pair (two glyphs, a value for each: x/y placement, x/y advance) comes from the font in font
    /// units; the asset keeps it in the units of its sampling size (× pointSize / unitsPerEM), the
    /// units its glyph metrics are in. TextMesh Pro's and TextCore's AddPairAdjustmentRecords (Unity
    /// 6000.0 to 6000.6, read and measured 2026-10-03) convert the FIRST glyph's x advance and nothing
    /// else: a placement or the second glyph's values stay in font units, unitsPerEM / pointSize times
    /// too large (24 times for a 2048-unit font sampled at 86). Kerning by advance alone (most Latin pairs) is right; a pair that moves a
    /// glyph (placement — the way right-to-left fonts kern, Hebrew and Arabic among them) throws it
    /// half a letter away: letters on top of each other, a gap inside a word.
    ///
    /// Decided per pair from what the asset did to it, never from a version: a value the asset left
    /// EQUAL to the font's while it scaled the first advance was not converted. An engine that
    /// converts everything leaves nothing equal, and nothing is touched. PURE — linked into Core.Checks.
    /// </summary>
    internal static class KerningUnits
    {
        /// <summary>The eight values of a pair: first glyph x/y placement, x/y advance, then the second's.</summary>
        internal const int Count = 8;
        internal const int FirstXAdvance = 2;

        /// <summary>
        /// Did the asset convert this pair's first x advance from font units — the evidence the asset
        /// converts on storing at all? False when the advance is zero (nothing to read) or the scale is
        /// one (the two units are the same).
        /// </summary>
        internal static bool ConvertedAdvance(float scale, float[] fromFont, float[] kept)
        {
            if (scale == 1f || fromFont[FirstXAdvance] == 0f) return false;
            return Near(kept[FirstXAdvance], fromFont[FirstXAdvance] * scale) && !Near(kept[FirstXAdvance], fromFont[FirstXAdvance]);
        }

        /// <summary>
        /// The pair as it should be kept, or null when it is right. Only values left in font units are
        /// changed — those still equal to the font's, non-zero — and only on an asset known to convert
        /// on storing (<paramref name="assetConverts"/>, read on a pair with an advance).
        /// </summary>
        internal static float[] Corrected(float scale, float[] fromFont, float[] kept, bool assetConverts)
        {
            if (!assetConverts || scale == 1f) return null;
            float[] result = null;
            for (int i = 0; i < Count; i++)
            {
                if (i == FirstXAdvance || fromFont[i] == 0f || !Near(kept[i], fromFont[i])) continue;
                if (result == null) result = (float[])kept.Clone();
                result[i] = fromFont[i] * scale;
            }
            return result;
        }

        // Equal as the asset computed it (one float multiplication): a relative tolerance of float
        // precision, not a judgement on sizes.
        private static bool Near(float a, float b) => Math.Abs(a - b) <= 1e-6f * Math.Max(1f, Math.Max(Math.Abs(a), Math.Abs(b)));
    }
}
