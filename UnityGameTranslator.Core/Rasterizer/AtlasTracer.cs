using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// A letter's outline traced back from its picture in a font atlas — the SDF of a TextMesh Pro or
    /// UI Toolkit asset (0.5 on the edge), or a plain coverage bitmap (TMProOld, tk2d, NGUI): the grid
    /// rebuilt finer by a cubic spline (RefineFactor), then its 0.5 level followed by marching squares,
    /// simplified (Ramer-Douglas-Peucker). For the fonts a game
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
        /// How many times finer the grid is rebuilt before it is traced (<see cref="Refine"/>).
        ///
        /// 🔴 **Because a hairline lives BETWEEN the samples** (2026-10-22, thin script fonts of a game
        /// exported cut into pieces). Traced straight from the samples, the field is read as a straight
        /// line from one to the next, which cuts every ridge: a stroke narrower than an atlas pixel
        /// whose centre passes between two samples reads below 0.5 at both, and disappears. Measured
        /// on the bench prototype (banc-unity/tools/trace_sdf_proto.py, a Thin font at 40 px per em):
        /// overlap with the original 0.60 on average, 0.28 at worst, read straight; 0.90 and 0.84
        /// rebuilt by a cubic spline four times finer. An ordinary font gains too (Arial at 90 px:
        /// 0.989 → 0.991). Catmull-Rom was measured beside it and gives less (0.877).
        /// </summary>
        internal const int RefineFactor = 4;

        /// <summary>
        /// The closed contours of the level <paramref name="level"/> in a grid of values (row-major,
        /// the first row the TOP one), the inside always on the same side — so holes come out wound
        /// against their outlines. Simplified to <paramref name="tolerance"/> grid units.
        ///
        /// The grid is read as the smooth field it was sampled from (<see cref="Refine"/>), unless
        /// <paramref name="refine"/> is 1: then straight from sample to sample.
        /// </summary>
        internal static List<List<Point>> Trace(float[] grid, int width, int height, double level = 0.5, double tolerance = 0.08,
                                                int refine = RefineFactor)
        {
            if (grid == null || grid.Length < width * height || width <= 0 || height <= 0) return new List<List<Point>>();
            if (refine <= 1) return March(grid, width, height, level, tolerance);

            // Traced on the finer grid, then brought back: a fine sample j stands at coarse position
            // (j + 0.5) / refine, and March puts a sample's centre at +0.5 — so a fine point divided by
            // the factor is the coarse one. The tolerance is the caller's, in coarse units.
            var fine = Refine(grid, width, height, refine);
            var contours = March(fine, width * refine, height * refine, level, tolerance * refine);
            foreach (var contour in contours)
                for (int i = 0; i < contour.Count; i++)
                    contour[i] = new Point(contour[i].X / refine, contour[i].Y / refine);
            return contours;
        }

        /// <summary>
        /// The grid rebuilt <paramref name="factor"/> times finer by an interpolating cubic B-spline
        /// (Unser's recursive prefilter, mirrored at the borders), clamped to 0..1: the field passes
        /// through every sample and keeps the ridges and corners between them.
        /// </summary>
        internal static float[] Refine(float[] grid, int width, int height, int factor)
        {
            // The spline's coefficients: the prefilter along each row, then along each column.
            var coeff = new double[width * height];
            for (int i = 0; i < coeff.Length; i++) coeff[i] = grid[i];
            var line = new double[Math.Max(width, height)];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) line[x] = coeff[y * width + x];
                Prefilter(line, width);
                for (int x = 0; x < width; x++) coeff[y * width + x] = line[x];
            }
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++) line[y] = coeff[y * width + x];
                Prefilter(line, height);
                for (int y = 0; y < height; y++) coeff[y * width + x] = line[y];
            }

            int fw = width * factor, fh = height * factor;
            // The four weights and first index of each fine column and row, the same for every line.
            var xi = new int[fw]; var xw = new double[fw * 4];
            var yi = new int[fh]; var yw = new double[fh * 4];
            for (int j = 0; j < fw; j++) Weights((j + 0.5) / factor - 0.5, xi, xw, j);
            for (int j = 0; j < fh; j++) Weights((j + 0.5) / factor - 0.5, yi, yw, j);

            var fine = new float[fw * fh];
            for (int fy = 0; fy < fh; fy++)
                for (int fx = 0; fx < fw; fx++)
                {
                    double v = 0;
                    for (int b = 0; b < 4; b++)
                    {
                        int row = Mirror(yi[fy] + b, height) * width;
                        double across = 0;
                        for (int a = 0; a < 4; a++) across += xw[fx * 4 + a] * coeff[row + Mirror(xi[fx] + a, width)];
                        v += yw[fy * 4 + b] * across;
                    }
                    fine[fy * fw + fx] = (float)(v < 0 ? 0 : v > 1 ? 1 : v);
                }
            return fine;
        }

        /// <summary>A cubic B-spline's four weights at <paramref name="u"/> (sample units), from sample floor(u) - 1.</summary>
        private static void Weights(double u, int[] first, double[] weights, int j)
        {
            int i = (int)Math.Floor(u);
            double t = u - i, t2 = t * t, t3 = t2 * t;
            first[j] = i - 1;
            weights[j * 4] = (1 - t) * (1 - t) * (1 - t) / 6;
            weights[j * 4 + 1] = (4 - 6 * t2 + 3 * t3) / 6;
            weights[j * 4 + 2] = (1 + 3 * t + 3 * t2 - 3 * t3) / 6;
            weights[j * 4 + 3] = t3 / 6;
        }

        /// <summary>A sample index folded back into 0..n-1, the border sample not repeated.</summary>
        private static int Mirror(int k, int n)
        {
            if (n == 1) return 0;
            int period = 2 * n - 2;
            k %= period;
            if (k < 0) k += period;
            return k < n ? k : period - k;
        }

        /// <summary>
        /// Samples to cubic B-spline coefficients, in place (Unser, Aldroubi and Eden 1993): one pole,
        /// a causal then an anti-causal pass, the borders mirrored.
        /// </summary>
        private static void Prefilter(double[] c, int n)
        {
            if (n < 2) return;
            double z = Math.Sqrt(3) - 2;      // the cubic spline's pole
            for (int k = 0; k < n; k++) c[k] *= 6;   // its gain, (1 - z)(1 - 1/z)

            // Causal start: the mirrored signal summed until the pole's powers no longer count.
            int horizon = Math.Min(n, 1 + (int)Math.Ceiling(Math.Log(1e-7) / Math.Log(Math.Abs(z))));
            double sum = c[0], zk = z;
            for (int k = 1; k < horizon; k++) { sum += zk * c[k]; zk *= z; }
            c[0] = sum;
            for (int k = 1; k < n; k++) c[k] += z * c[k - 1];

            c[n - 1] = z / (z * z - 1) * (c[n - 1] + z * c[n - 2]);
            for (int k = n - 2; k >= 0; k--) c[k] = z * (c[k + 1] - c[k]);
        }

        /// <summary>Marching squares on the grid as it is: straight from one sample to the next.</summary>
        private static List<List<Point>> March(float[] grid, int width, int height, double level, double tolerance)
        {

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
