using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// A letter's outline traced back from its picture in a font atlas — the SDF of a TextMesh Pro or
    /// UI Toolkit asset (0.5 on the edge), or a plain coverage bitmap (TMProOld, tk2d, NGUI): the 0.5
    /// level, followed by marching squares, simplified (Ramer-Douglas-Peucker). For the fonts a game
    /// carries as an atlas only, with no font file to extract (GameFontFiles, Extract).
    ///
    /// Measured before it was written (banc-unity/tools/trace_sdf_proto.py, analyse/export-polices-jeu.md):
    /// straight segments, 0.08 px tolerance, 90 px per em — overlap ≥ 0.99 with the original, worst edge
    /// 0.6 % of the em. Two lessons of the prototype kept here: a sample stands for its pixel's CENTRE
    /// (+0.5), and the writer's left side bearing must be the outline's own left edge.
    ///
    /// ⚠ Pure: no Unity (linked into Core.Checks).
    /// </summary>
    internal static class AtlasTracer
    {
        /// <summary>A point of a traced contour, in grid units (x right, y DOWN, a pixel's centre at +0.5).</summary>
        internal struct Point { public double X, Y; public Point(double x, double y) { X = x; Y = y; } }

        /// <summary>
        /// The closed contours of the level <paramref name="level"/> in a grid of values (row-major,
        /// the first row the TOP one), the inside always on the same side — so holes come out wound
        /// against their outlines. Simplified to <paramref name="tolerance"/> grid units.
        /// </summary>
        internal static List<List<Point>> Trace(float[] grid, int width, int height, double level = 0.5, double tolerance = 0.08)
        {
            if (grid == null || grid.Length < width * height || width <= 0 || height <= 0) return new List<List<Point>>();

            // A value outside the grid is "outside": every contour closes at the border.
            float At(int x, int y) => x < 0 || y < 0 || x >= width || y >= height ? 0f : grid[y * width + x];

            // Each crossing is keyed by its edge — 'h' (horizontal, between x and x+1 on row y) or 'v'
            // (vertical, between y and y+1 on column x) — so the two cells sharing it agree.
            var next = new Dictionary<long, long>();
            var where = new Dictionary<long, Point>();
            for (int y = -1; y < height; y++)
                for (int x = -1; x < width; x++)
                {
                    float v0 = At(x, y), v1 = At(x + 1, y), v2 = At(x + 1, y + 1), v3 = At(x, y + 1);
                    int index = (v0 > level ? 1 : 0) | (v1 > level ? 2 : 0) | (v2 > level ? 4 : 0) | (v3 > level ? 8 : 0);
                    if (index == 0 || index == 15) continue;
                    // The cell's four edges: 0 top (v0-v1), 1 right (v1-v2), 2 bottom (v3-v2), 3 left (v0-v3).
                    foreach (var (a, b) in Pairs(index, (v0 + v1 + v2 + v3) / 4 > level))
                    {
                        long ka = Key(x, y, a), kb = Key(x, y, b);
                        next[ka] = kb;
                        if (!where.ContainsKey(ka)) where[ka] = Cross(x, y, a, v0, v1, v2, v3, level);
                        if (!where.ContainsKey(kb)) where[kb] = Cross(x, y, b, v0, v1, v2, v3, level);
                    }
                }

            var contours = new List<List<Point>>();
            var seen = new HashSet<long>();
            foreach (var start in next.Keys)
            {
                if (seen.Contains(start)) continue;
                var loop = new List<Point>();
                long k = start;
                while (!seen.Contains(k) && next.TryGetValue(k, out long n))
                {
                    seen.Add(k);
                    var p = where[k];
                    // +0.5: a sample stands for its pixel's centre, so the grid's corners sit half a pixel in.
                    loop.Add(new Point(p.X + 0.5, p.Y + 0.5));
                    k = n;
                }
                if (loop.Count > 2)
                {
                    var simple = Simplify(loop, tolerance);
                    if (simple.Count > 2) contours.Add(simple);
                }
            }
            return contours;
        }

        // The crossings of a cell paired so the inside stays on the right (y down); the two saddles
        // decided by the cell's centre value.
        private static IEnumerable<(int, int)> Pairs(int index, bool centreInside)
        {
            switch (index)
            {
                case 1: yield return (3, 0); break;
                case 2: yield return (0, 1); break;
                case 3: yield return (3, 1); break;
                case 4: yield return (1, 2); break;
                case 6: yield return (0, 2); break;
                case 7: yield return (3, 2); break;
                case 8: yield return (2, 3); break;
                case 9: yield return (2, 0); break;
                case 11: yield return (2, 1); break;
                case 12: yield return (1, 3); break;
                case 13: yield return (1, 0); break;
                case 14: yield return (0, 3); break;
                case 5:
                    if (centreInside) { yield return (3, 2); yield return (1, 0); }
                    else { yield return (3, 0); yield return (1, 2); }
                    break;
                case 10:
                    if (centreInside) { yield return (0, 3); yield return (2, 1); }
                    else { yield return (0, 1); yield return (2, 3); }
                    break;
            }
        }

        private static long Key(int x, int y, int edge)
        {
            // x, y from -1: shifted to stay positive; 'h' edges even, 'v' odd.
            long cx = x + 1, cy = y + 1;
            switch (edge)
            {
                case 0: return ((cy << 22 | cx) << 1);                 // h at (x, y)
                case 2: return (((cy + 1) << 22 | cx) << 1);           // h at (x, y + 1)
                case 1: return ((cy << 22 | (cx + 1)) << 1) | 1;       // v at (x + 1, y)
                default: return ((cy << 22 | cx) << 1) | 1;            // v at (x, y)
            }
        }

        private static Point Cross(int x, int y, int edge, float v0, float v1, float v2, float v3, double level)
        {
            Point Lerp(double px, double py, double qx, double qy, double vp, double vq)
            {
                double t = vq != vp ? (level - vp) / (vq - vp) : 0.5;
                return new Point(px + t * (qx - px), py + t * (qy - py));
            }
            switch (edge)
            {
                case 0: return Lerp(x, y, x + 1, y, v0, v1);
                case 1: return Lerp(x + 1, y, x + 1, y + 1, v1, v2);
                case 2: return Lerp(x + 1, y + 1, x, y + 1, v2, v3);
                default: return Lerp(x, y + 1, x, y, v3, v0);
            }
        }

        /// <summary>Ramer-Douglas-Peucker on a closed loop, cut at its two farthest points.</summary>
        internal static List<Point> Simplify(List<Point> loop, double tolerance)
        {
            int far = 0;
            double best = -1;
            for (int i = 0; i < loop.Count; i++)
            {
                double d = Sq(loop[i].X - loop[0].X) + Sq(loop[i].Y - loop[0].Y);
                if (d > best) { best = d; far = i; }
            }
            var first = loop.GetRange(0, far + 1);
            var second = loop.GetRange(far, loop.Count - far);
            second.Add(loop[0]);
            var a = Rdp(first, tolerance);
            var b = Rdp(second, tolerance);
            a.RemoveAt(a.Count - 1);
            b.RemoveAt(b.Count - 1);
            a.AddRange(b);
            return a;
        }

        private static double Sq(double v) => v * v;

        private static List<Point> Rdp(List<Point> p, double tolerance)
        {
            if (p.Count < 3) return new List<Point>(p);
            var keep = new bool[p.Count];
            keep[0] = keep[p.Count - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, p.Count - 1));
            while (stack.Count > 0)
            {
                var (s, e) = stack.Pop();
                double ax = p[s].X, ay = p[s].Y, bx = p[e].X - ax, by = p[e].Y - ay;
                double n = Math.Sqrt(bx * bx + by * by);
                if (n < 1e-9) n = 1e-9;
                int index = -1;
                double worst = 0;
                for (int i = s + 1; i < e; i++)
                {
                    double d = Math.Abs(bx * (p[i].Y - ay) - by * (p[i].X - ax)) / n;
                    if (d > worst) { worst = d; index = i; }
                }
                if (index >= 0 && worst > tolerance)
                {
                    keep[index] = true;
                    stack.Push((s, index));
                    stack.Push((index, e));
                }
            }
            var result = new List<Point>();
            for (int i = 0; i < p.Count; i++) if (keep[i]) result.Add(p[i]);
            return result;
        }
    }
}
