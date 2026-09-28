using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A replaced font's shadow and outline drawn like the game's (<see cref="DrawnWidths"/>).
    ///
    /// 🔴 The numbers are the ones two games logged, each validated on screen by the user
    /// (2026-09-28): Frog Detective (spread 21→64, a font at scale 1.7) and Beacon Pines (spread 10→8,
    /// Arabic). Two fixed factors were tried before and each broke the game the other fixed — these
    /// cases are what makes the next change say so here, instead of in a game nobody relaunched.
    ///
    /// ⚠ TMP's own ratio formula is NOT copied here: it differs between TMP versions (some count the
    /// font's weight), and a copy slightly wrong would pass a wrong solver. The ratio is modelled by
    /// shapes the solver must handle — constant, shrinking, saturating — and by the values games logged.
    /// </summary>
    internal static class DrawnWidthsChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            bool Near(float a, float b, float tolerance = 0.002f) => Math.Abs(a - b) <= tolerance;

            // ── The size of an atlas pixel ───────────────────────────────────────────────────
            check(DrawnWidths.PointsPerEm(236f, 1.7f) == 236f,
                "the pixels of an em are the point size, whatever the font's scale",
                "dividing by the scale counts it twice: the design-scale already matches the text to it (Frog's shadow 2.8× too far)");

            check(DrawnWidths.EmRatio(float.NaN, 384f) == 1f && DrawnWidths.EmRatio(236f, 0f) == 1f,
                "an unknown size leaves the spread ratio alone", "a guess would move every effect by it");

            // ── Frog Detective: "FrogDetectiveNeat shadow" ───────────────────────────────────
            float frogEm = DrawnWidths.EmRatio(DrawnWidths.PointsPerEm(236f, 1.7f), DrawnWidths.PointsPerEm(384f, 1f));
            float frogTarget = DrawnWidths.Target(21f, 64f, frogEm, 0.469f);
            float frog = DrawnWidths.Solve(frogTarget, _ => 0.800f);
            check(Near(frogEm, 1.627f) && Near(frog, 0.313f),
                "Frog's shadow: ×0.313 — the 0.328 validated by hand in July, now reached by the arithmetic",
                "TMP shrank the game's shadow (ratio 0.469) more than ours (0.800); the fixed factors missed it");

            // ── Beacon Pines: PaytoneOne, Arabic ─────────────────────────────────────────────
            float paytoneEm = 0.345f;
            float underlay = DrawnWidths.Solve(DrawnWidths.Target(10f, 8f, paytoneEm, 0.257f), _ => 0.711f);
            float outline = DrawnWidths.Solve(DrawnWidths.Target(10f, 8f, paytoneEm, 0.900f), _ => 0.875f);
            check(Near(underlay, 0.156f) && Near(outline, 0.444f),
                "Beacon Pines' PaytoneOne: shadow ×0.156, outline ×0.444 — as logged and seen right in Arabic",
                "the outline 2.9 times too wide erased a whole menu; the shadow was 2.8 times too heavy");

            // ── The solver, on the shapes a ratio takes ──────────────────────────────────────
            check(Near(DrawnWidths.Solve(0.4f, _ => 1f), 0.4f),
                "without ratios (RATIOS_OFF), the factor is the target", "the plain unit conversion");

            // TMP's shape: constant until the effect fills the spread, then shrinking with it.
            Func<float, float> shrinking = f => (63f / 64f) / Math.Max(1f, f * 2.2f);
            float reachable = DrawnWidths.Solve(0.25f, shrinking);
            check(Near(reachable * shrinking(reachable), 0.25f, 0.0005f),
                "a reachable target is drawn exactly: factor × the ratio TMP gives for it",
                "what is equal to the game is what is drawn, not the numbers written");

            // Saturated: the drawn width can never exceed (G-1)/(G·S) whatever the factor.
            float unreachable = DrawnWidths.Solve(2f, shrinking);
            float drawn = unreachable * shrinking(unreachable);
            check(!float.IsNaN(unreachable) && !float.IsInfinity(unreachable) && unreachable < 100f
                  && drawn >= (63f / 64f) / 2.2f - 0.001f,
                "an unreachable target gives the widest the spread allows, and stops there",
                "chasing it would grow the widths round after round and end anywhere");
        }
    }
}
