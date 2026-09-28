using System;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// How a replaced font's shadow and outline are made to look like the game's — the arithmetic,
    /// pure (no Unity, no state), held by <c>DrawnWidthsChecks</c> with cases from real games.
    ///
    /// 🔴 **What must be equal is what is DRAWN, relative to the glyph**: an effect width w is drawn
    /// as w × ratio × _GradientScale atlas pixels, and an atlas pixel is 1/pointSize of the glyph's
    /// em. TMP computes the ratio (_ScaleRatioC for the underlay, _ScaleRatioA for the outline) from
    /// the widths themselves, differently on a spread of 21 and one of 64 — so the widths are SOLVED
    /// for, not multiplied by a fixed factor.
    ///
    /// ⚠ Two fixed factors were tried and each broke a game the other fixed (2026-07 Frog, 2026-09-25
    /// Beacon Pines, reconciled 2026-09-28): the spread ratio alone (right on Frog by chance) and the
    /// spread ratio × pointSize/scale (the scale counted twice). Design:
    /// analyse/font-rendering-target-size.md § « RÈGLE EN VIGUEUR depuis le 2026-09-28 ».
    /// </summary>
    public static class DrawnWidths
    {
        /// <summary>
        /// Atlas pixels in one em of the glyph as drawn. An atlas sampled at P points holds an em in
        /// P pixels, and TMP draws that em at fontSize × scale: relative to the drawn glyph a pixel
        /// is 1/P whatever the scale. 🔴 Not P / scale — the mod already matches the replaced text's
        /// size to the scale (design-scale), and dividing by it counts it twice.
        /// </summary>
        public static float PointsPerEm(float pointSize, float faceScale) =>
            pointSize > 0f ? pointSize : float.NaN;

        /// <summary>Our atlas's points per em over the game's — 1 when either is unknown (the conversion
        /// then stays the spread ratio alone, and the caller logs it).</summary>
        public static float EmRatio(float pointsGame, float pointsOurs) =>
            float.IsNaN(pointsGame) || float.IsNaN(pointsOurs) || pointsGame <= 0f || pointsOurs <= 0f
                ? 1f
                : pointsOurs / pointsGame;

        /// <summary>
        /// What factor × ratioOurs must equal so that ours is drawn like the game's:
        /// (spread_game / spread_ours) × emRatio × ratio_game.
        /// </summary>
        public static float Target(float gradGame, float gradOurs, float emRatio, float ratioGame)
        {
            float gradRatio = gradOurs > 0.0001f && !float.IsNaN(gradGame) ? gradGame / gradOurs : 1f;
            return gradRatio * emRatio * (ratioGame > 0f ? ratioGame : 1f);
        }

        /// <summary>
        /// The factor on the game's widths such that factor × ratio(factor) = target, where
        /// <paramref name="ratioAt"/> applies a factor and returns the ratio TMP computes for it.
        ///
        /// ⚠ The best factor found, not the last tried: a ratio that shrinks as fast as the widths
        /// grow cannot reach every target, and rounds chasing it would end anywhere. The caller applies
        /// the returned factor once more, since ratioAt was last called with another.
        /// </summary>
        public static float Solve(float target, Func<float, float> ratioAt, int rounds = 8)
        {
            float factor = target, best = target, bestGap = float.MaxValue, previous = float.NaN;
            float tolerance = 0.0001f * Math.Max(1f, target);

            for (int round = 0; round < rounds; round++)
            {
                float ratio = ratioAt(factor);
                if (!(ratio > 0f)) break;

                float drawn = factor * ratio;
                float gap = Math.Abs(drawn - target);

                // Only a real improvement counts: once the spread is full, larger widths draw the
                // same, and the smallest that does is the one kept.
                if (gap < bestGap - tolerance) { bestGap = gap; best = factor; }
                if (gap <= tolerance) break;

                // The drawn width no longer moves: the ratio shrinks as fast as the widths grow.
                if (!float.IsNaN(previous) && Math.Abs(drawn - previous) <= tolerance) break;
                previous = drawn;

                float next = target / ratio;
                if (float.IsNaN(next) || float.IsInfinity(next)) break;
                factor = next;
            }

            return best;
        }
    }
}
