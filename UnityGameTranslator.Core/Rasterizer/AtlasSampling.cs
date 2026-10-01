using System;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// The sampling size a font is drawn at in its SDF atlas, from its glyph count and the atlas
    /// budget. Pure — held by AtlasSamplingChecks; used by the pipeline when drawing and by the
    /// cache check when deciding whether a cached atlas still matches the budget.
    /// </summary>
    public static class AtlasSampling
    {
        /// <summary>The size nothing goes under: what a huge charset (CJK) is drawn at.</summary>
        public const float FloorSize = 48f;

        /// <summary>
        /// The automatic choice ("Auto", max_font_atlas_size 0): every font gets at least what a
        /// 4096 atlas allows, and a font with many glyphs may use up to an 8192 atlas to reach
        /// <see cref="AutoLargeFontSize"/>. The atlas goes to the GPU as Alpha8 (one byte a
        /// pixel): 8192² is 64 MB. The rule this replaced reasoned on ARGB32 (4096² = 64 MB) and
        /// so kept large fonts at 48 px/em, where the SDF thins strokes (devanagari ink 0.969 of
        /// the reference against 0.984 for Unity's own asset — analyse/ecritures-complexes-etat-reel.md ⑤).
        /// The player can still choose 4096 to spend less (Translation Parameters).
        /// </summary>
        private const int AutoCompactBudget = 4096;
        private const int AutoLargeBudget = 8192;

        /// <summary>
        /// How high "Auto" lifts a large font: Unity's own default sampling (90) rounded to a
        /// step of the ladder. Measured: at 90 px/em our SDF loses as much ink as Unity's (1.2 %),
        /// at 48 px/em 2 %. Higher would only spend memory on fonts already as sharp as Unity's.
        /// </summary>
        private const float AutoLargeFontSize = 96f;

        /// <summary>
        /// Pick the largest sampling size whose estimated single atlas fits the budget.
        /// Small charsets (Latin ≈ a few hundred glyphs) get 96-128px/em — crisp at the
        /// large on-screen sizes where the historical 48px looked jagged; huge charsets
        /// (CJK) fall back to the compact default.
        /// </summary>
        public static float ChooseRenderSize(int glyphCount, int maxAtlasSize, int atlasBudget = 0)
        {
            // A budget the player chose (config max_font_atlas_size): the largest size it fits.
            // Always capped by the hardware texture limit.
            if (atlasBudget > 0) return LargestFitting(glyphCount, Math.Min(atlasBudget, maxAtlasSize));
            float compact = LargestFitting(glyphCount, Math.Min(AutoCompactBudget, maxAtlasSize));
            float large = Math.Min(LargestFitting(glyphCount, Math.Min(AutoLargeBudget, maxAtlasSize)), AutoLargeFontSize);
            return Math.Max(compact, large);
        }

        private static float LargestFitting(int glyphCount, int budget)
        {
            float side = (float)Math.Ceiling(Math.Sqrt(Math.Max(1, glyphCount)));

            // Largest first: pick the highest sampling size whose atlas fits the budget. The
            // highest sizes need a raised budget — they let our atlas match (or exceed) the game
            // font's own sampling size (a test game's own font is sampled at 408 px/em), which is
            // what keeps replacement text from looking softer than the original at large sizes.
            foreach (float size in new[] { 512f, 384f, 256f, 192f, 128f, 96f, 64f })
            {
                // Cell estimate: glyph body (~1.3 em worst case) + SDF padding on both
                // sides + inter-cell spacing (2× distanceRange, see the packing step: covers
                // the SDF spread AND a drop-shadow offset that reaches into a neighbour cell).
                float range = (float)Math.Round(size / 6f);
                float cell = size * 1.3f + 2f * (range + 1f) + 2f * range;
                if (side * cell <= budget)
                    return size;
            }
            return FloorSize;
        }
    }
}
