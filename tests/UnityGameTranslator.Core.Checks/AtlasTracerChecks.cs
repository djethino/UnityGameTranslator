using System;
using System.Collections.Generic;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A letter traced back from its atlas picture (AtlasTracer) and written into a font from scratch
    /// (DerivedFontWriter.FromOutlines): what Extract gives for a font the game keeps as an atlas only.
    /// The full proof — a real atlas read in a game, its font drawn against the original — is the
    /// bench's (analyse/export-polices-jeu.md).
    /// </summary>
    internal static class AtlasTracerChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // A ring: a 20x20 square with a 8x8 hole, as an SDF (0.5 on the edge, spread 4 px) on a
            // 32x32 grid, its outer edge on whole pixel boundaries.
            const int size = 32, spread = 4;
            var grid = new float[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Pixel centre, y down; distance to the ring's boundary, + inside.
                    double cx = x + 0.5, cy = y + 0.5;
                    double outer = Math.Min(Math.Min(cx - 6, 26 - cx), Math.Min(cy - 6, 26 - cy));
                    double inner = Math.Max(Math.Max(12 - cx, cx - 20), Math.Max(12 - cy, cy - 20));
                    double d = Math.Min(outer, inner);
                    grid[y * size + x] = (float)Math.Max(0, Math.Min(1, 0.5 + d / (2 * spread)));
                }
            var contours = AtlasTracer.Trace(grid, size, size);
            check(contours.Count == 2, "a ring traces as two contours", $"got {contours.Count}");
            if (contours.Count != 2) return;
            double a0 = Area(contours[0]), a1 = Area(contours[1]);
            check(Math.Sign(a0) != Math.Sign(a1), "its hole is wound against its outline", "a font fills a hole only when it turns the other way");
            double outerArea = Math.Max(Math.Abs(a0), Math.Abs(a1)), innerArea = Math.Min(Math.Abs(a0), Math.Abs(a1));
            check(Math.Abs(outerArea - 400) < 4 && Math.Abs(innerArea - 64) < 4,
                "the traced edges sit where the field says 0.5", $"areas {outerArea:F1} (400) and {innerArea:F1} (64)");
            double minX = double.MaxValue, maxX = double.MinValue;
            foreach (var p in Math.Abs(a0) > Math.Abs(a1) ? contours[0] : contours[1]) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); }
            check(Math.Abs(minX - 6) < 0.2 && Math.Abs(maxX - 26) < 0.2,
                "a sample stands for its pixel's centre", $"outline from x {minX:F2} to {maxX:F2} (6 to 26): without the half pixel, every letter would slide by it");
            // Rebuilt finer, a corner is a few points rather than one (the spline turns it over a
            // fraction of a pixel); a straight edge must still not come back as its pixels — a 20 px
            // side traced four times finer is 80 steps before simplification.
            check(contours[0].Count <= 24 && contours[1].Count <= 24, "a straight edge is not cut into its pixels",
                $"{contours[0].Count} and {contours[1].Count} points, at most 6 per corner");
            double strayest = 0;
            foreach (var c in contours)
                foreach (var p in c)
                {
                    double toOuter = Math.Min(Math.Min(Math.Abs(p.X - 6), Math.Abs(p.X - 26)), Math.Min(Math.Abs(p.Y - 6), Math.Abs(p.Y - 26)));
                    double toInner = Math.Min(Math.Min(Math.Abs(p.X - 12), Math.Abs(p.X - 20)), Math.Min(Math.Abs(p.Y - 12), Math.Abs(p.Y - 20)));
                    strayest = Math.Max(strayest, Math.Min(toOuter, toInner));
                }
            check(strayest < 0.25, "every traced point sits on the shape's edge", $"farthest {strayest:F2} px from it");

            // A hairline: a bar 0.8 px tall whose centre runs exactly between two rows of samples, as
            // an SDF with a 10 px spread — the thin strokes of a script font in a small atlas. Both
            // rows read 0.495: straight from sample to sample, the stroke is not there at all; read
            // as the smooth field it was sampled from, it is one stroke from end to end.
            const int hw = 40, hh = 40;
            var hair = new float[hw * hh];
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < hw; x++)
                {
                    double cx = x + 0.5, cy = y + 0.5;
                    double d = Math.Min(0.4 - Math.Abs(cy - 20.0), Math.Min(cx - 5, 35 - cx));
                    hair[y * hw + x] = (float)Math.Max(0, Math.Min(1, 0.5 + d / 20));
                }
            var straight = AtlasTracer.Trace(hair, hw, hh, refine: 1);
            var smooth = AtlasTracer.Trace(hair, hw, hh);
            check(straight.Count == 0, "read straight from sample to sample, a hairline between two rows is lost",
                $"{straight.Count} contour(s) — the case the refinement exists for");
            double hairMin = double.MaxValue, hairMax = double.MinValue;
            if (smooth.Count == 1) foreach (var p in smooth[0]) { hairMin = Math.Min(hairMin, p.X); hairMax = Math.Max(hairMax, p.X); }
            check(smooth.Count == 1 && hairMin < 7 && hairMax > 33,
                "rebuilt as a smooth field, the hairline comes back whole", $"{smooth.Count} contour(s), x from {hairMin:F1} to {hairMax:F1} (5 to 35): thin script fonts exported in pieces");

            // Written into a font from scratch, read back by the mod's own reader.
            var outline = new DerivedFontWriter.Outline { Advance = 600 };
            foreach (var c in contours)
            {
                var points = new List<DerivedFontWriter.QuadPoint>();
                foreach (var p in c) points.Add(new DerivedFontWriter.QuadPoint { X = (int)Math.Round(p.X * 25), Y = (int)Math.Round((size - p.Y) * 25), On = true });
                outline.Contours.Add(points);
            }
            var font = DerivedFontWriter.FromOutlines("UGT Check Traced", 1000, 800, -200,
                new List<DerivedFontWriter.Outline> { outline }, new SortedDictionary<int, int> { ['O'] = 1 });
            var parsed = new TtfParser(font);
            var glyph = parsed.GetGlyphOutline('O');
            check(parsed.GlyphCount == 2 && parsed.HasCodepoint('O') && glyph != null && glyph.Contours.Length == 2,
                "a font written from outlines reads back: its glyph, its character, its two contours", "the file Extract writes is a real font");
            check(glyph != null && Math.Abs(glyph.XMin - 150) <= 2 && Math.Abs(glyph.XMax - 650) <= 2 && glyph.AdvanceWidth == 600 && (int)glyph.LeftSideBearing == glyph.XMin,
                "its box, its advance, and a left side bearing equal to its left edge", $"xMin {glyph?.XMin} xMax {glyph?.XMax} lsb {glyph?.LeftSideBearing} — FreeType places a TrueType outline by its lsb");
        }

        private static double Area(List<AtlasTracer.Point> loop)
        {
            double a = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                var p = loop[i]; var q = loop[(i + 1) % loop.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2;
        }
    }
}
