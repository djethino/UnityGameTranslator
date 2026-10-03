using System;
using System.Collections.Generic;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// An underline or strikethrough over the letters it belongs to (DecorationSpans). The strips are
    /// built here the way TMP's DrawUnderlineMesh builds them — start = the run's first letter's LEFT
    /// edge, end = its last letter's RIGHT edge, caps of half the glyph or half the run — so a case
    /// is the geometry TMP hands over, measured on the bench (tmpdeco, 2026-10-03), not the code read back.
    /// </summary>
    internal static class DecorationSpansChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            const float capHalf = 10f;

            // Right to left: the first letter is the rightmost. TMP's strip runs 100 → 60, backwards,
            // over the two letters between the ends only.
            var rtl = Letters(new[] { (100f, 120f), (80f, 100f), (60f, 80f), (40f, 60f) });
            var (xs, ys) = Strip(100f, 60f, capHalf, 1f);
            check(DecorationSpans.IsStrip(xs, ys, 0), "a strip TMP drew backwards is still a strip", "IsStrip false");
            var x = DecorationSpans.Respan(xs, 0, rtl, capHalf);
            check(x != null && x[0] == 40f && x[10] == 120f, "🔴 a right-to-left run: the strip spans its four letters (40 → 120)",
                  x == null ? "left as it was" : $"{x[0]} → {x[10]}");
            check(x != null && x[2] == 50f && x[6] == 110f, "its caps are half the glyph, as TMP draws them", x == null ? "-" : $"caps end {x[2]}, start {x[6]}");

            // Left to right, drawn right by TMP: never touched.
            var ltr = Letters(new[] { (40f, 60f), (60f, 80f), (80f, 100f), (100f, 120f) });
            (xs, ys) = Strip(40f, 120f, capHalf, 1f);
            check(DecorationSpans.Respan(xs, 0, ltr, capHalf) == null, "a strip TMP drew forwards over unmoved letters is left alone", "rewritten");

            // Letters moved after TMP's layout (a field in visual order): the strip follows them.
            var moved = Letters(new[] { (40f, 60f), (60f, 80f) });
            moved[0].Shift = 100f; moved[1].Shift = 100f;
            (xs, ys) = Strip(40f, 80f, capHalf, 1f);
            x = DecorationSpans.Respan(xs, 0, moved, capHalf);
            check(x != null && x[0] == 140f && x[10] == 180f, "letters moved after layout take their strip along", x == null ? "left as it was" : $"{x[0]} → {x[10]}");

            // Ends that are no letter's edges: not this text's strip.
            (xs, ys) = Strip(41f, 119f, capHalf, 1f);
            check(DecorationSpans.Respan(xs, 0, ltr, capHalf) == null, "a strip whose ends are no letter's is not touched", "rewritten");

            // A short run: the caps meet in the middle, never cross (TMP's own rule).
            var shortRun = Letters(new[] { (70f, 78f), (62f, 70f) });
            (xs, ys) = Strip(70f, 70f, capHalf, 1f);
            x = DecorationSpans.Respan(xs, 0, shortRun, capHalf);
            check(x != null && x[2] == 70f && x[6] == 70f, "a run shorter than the glyph: each cap is half the run", x == null ? "left as it was" : $"caps {x[2]} / {x[6]}");

            // The last letter on another line does not close the run.
            var twoLines = Letters(new[] { (100f, 120f), (80f, 100f) });
            twoLines[1].Line = 1;
            (xs, ys) = Strip(100f, 100f, capHalf, 1f);
            check(DecorationSpans.Respan(xs, 0, twoLines, capHalf) == null, "a run is closed on its own line only", "rewritten");

            // Two letters' quads side by side are not a strip.
            var glyphs = new List<float>(); var gy = new List<float>();
            foreach (var (l, r) in new[] { (0f, 10f), (12f, 20f), (22f, 30f) })
            {
                glyphs.AddRange(new[] { l, l, r, r }); gy.AddRange(new[] { 0f, 10f, 10f, 0f });
            }
            check(!DecorationSpans.IsStrip(glyphs, gy, 0), "three letters' quads are not taken for a strip", "IsStrip true");

            // The unused vertex slots past the text are all zero: every equality holds, no height.
            var zeros = new List<float>(new float[12]);
            check(!DecorationSpans.IsStrip(zeros, zeros, 0), "unused zeroed vertices are not a strip", "IsStrip true");
        }

        private static DecorationSpans.Letter[] Letters((float left, float right)[] edges)
        {
            var letters = new DecorationSpans.Letter[edges.Length];
            for (int i = 0; i < edges.Length; i++)
                letters[i] = new DecorationSpans.Letter { Visible = true, Decorated = true, Line = 0, Left = edges[i].left, Right = edges[i].right, Scale = 1f };
            return letters;
        }

        /// <summary>The 12 vertices of TMP's DrawUnderlineMesh for a run from start to end.</summary>
        private static (List<float>, List<float>) Strip(float start, float end, float capHalf, float scale)
        {
            float seg = capHalf * scale;
            if (end - start < 2 * capHalf * scale) seg = (end - start) / 2f;
            float bottom = -5f, top = -3f;
            var xs = new List<float> { start, start, start + seg, start + seg, start + seg, start + seg, end - seg, end - seg, end - seg, end - seg, end, end };
            var ys = new List<float> { bottom, top, top, bottom, bottom, top, top, bottom, bottom, top, top, bottom };
            return (xs, ys);
        }
    }
}
