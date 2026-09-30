using System;
using System.Collections.Generic;
using System.IO;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The derived font (DerivedFontWriter) against its SOURCE, read by the same parser the mod uses:
    /// every added glyph must be the source glyph's outline moved by exactly the offset asked, advance
    /// by exactly the width asked, and be reached by its codepoint; every mapping of the source must
    /// still hold; the family must change; the variation tables must be gone. The oracle is the source
    /// file, never the writer. Real fonts: Noto Sans Devanagari (variable, TrueType) and Noto Sans Khmer.
    /// The same files are drawn by Unity on the probe bench (banc-unity/, analyse/ecritures-complexes-etat-reel.md).
    /// </summary>
    internal static class DerivedFontChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string fonts = Path.Combine(AppContext.BaseDirectory, "TestData", "Fonts");
            foreach (var file in new[] { "NotoSansDevanagari.ttf", "NotoSansKhmer.ttf" })
            {
                string path = Path.Combine(fonts, file);
                if (!File.Exists(path)) { check(false, $"{file} present", path); continue; }
                One(check, file, File.ReadAllBytes(path));
            }
            Collection(check, File.ReadAllBytes(Path.Combine(fonts, "NotoSansDevanagari.ttf")), File.ReadAllBytes(Path.Combine(fonts, "NotoSansKhmer.ttf")));
            foreach (var pair in new[] { ("NotoSansDevanagari-cff.otf", "NotoSansDevanagari.ttf"), ("NotoSansKhmer-cff.otf", "NotoSansKhmer.ttf") })
            {
                string cff = Path.Combine(fonts, pair.Item1);
                if (!File.Exists(cff)) { check(false, $"{pair.Item1} present", "tools/shaping-oracle/harfbuzz-expectations.py derive"); continue; }
                Cff(check, pair.Item1, File.ReadAllBytes(cff), File.ReadAllBytes(Path.Combine(fonts, pair.Item2)));
            }
            Flex(check, Path.Combine(fonts, "UgtCffFlex.otf"));
        }

        /// <summary>
        /// The flex operators of CFF charstrings, which no converted font contains: one glyph each in
        /// UgtCffFlex.otf (tools/shaping-oracle, derive_cff_flex), read by the mod's CFF reader against
        /// the points fontTools decodes from the same file — written here as it printed them. hflex1 and
        /// flex1 were skipped until 2026-09-30, leaving the pen where it stood.
        /// </summary>
        private static void Flex(Action<bool, string, string> check, string path)
        {
            if (!File.Exists(path)) { check(false, "UgtCffFlex.otf present", "tools/shaping-oracle/harfbuzz-expectations.py derive"); return; }
            var expected = new[]
            {
                ("flex",   new[] { 100, 100, 150, 120, 190, 150, 250, 160, 320, 150, 360, 120, 410, 100, 10, 100 }),
                ("hflex",  new[] { 100, 100, 160, 100, 200, 130, 270, 130, 350, 130, 400, 100, 460, 100, 60, 100 }),
                ("hflex1", new[] { 100, 100, 160, 110, 200, 140, 270, 140, 350, 140, 400, 105, 460, 100, 60, 100 }),
                ("flex1 (horizontal)", new[] { 100, 100, 160, 110, 200, 140, 270, 145, 350, 140, 400, 105, 460, 100, 60, 100 }),
                ("flex1 (vertical)",   new[] { 100, 100, 110, 160, 140, 200, 145, 270, 140, 350, 105, 400, 100, 460, 100, 60 }),
            };
            var font = new TtfParser(File.ReadAllBytes(path));
            for (int g = 1; g <= expected.Length; g++)
            {
                var (name, want) = expected[g - 1];
                var outline = font.OutlineOfAnyGlyph(g);
                var got = new List<int>();
                if (outline?.Contours != null)
                    foreach (var c in outline.Contours)
                        foreach (var p in c.Points) { got.Add((int)Math.Round(p.X)); got.Add((int)Math.Round(p.Y)); }
                // The reader may close the contour on its first point; fontTools does not.
                if (got.Count == want.Length + 2 && got[got.Count - 2] == want[0] && got[got.Count - 1] == want[1]) got.RemoveRange(got.Count - 2, 2);
                check(string.Join(",", got) == string.Join(",", want), $"CFF {name}: drawn as fontTools decodes it",
                      $"got {string.Join(",", got)}");
            }
        }

        /// <summary>
        /// A PostScript-outline font (CFF) has its outlines merged into TrueType ones before the copy is
        /// made. The test font is a CFF copy of a TrueType one, drawn from it with its runs of quadratic
        /// pieces joined into genuine cubics within 1 unit (fontTools'
        /// qu2cu, tools/shaping-oracle), so the TRUETYPE ORIGINAL is the oracle — neither the CFF reader
        /// nor the merge is asked what the shape should be: every merged outline must lie within 3 font
        /// units of the original and the original within 3 of it (qu2cu's 1, the merge's 1, roundings),
        /// every advance and mapping identical. Then the whole copy is made from the CFF file like any other.
        /// </summary>
        private static void Cff(Action<bool, string, string> check, string file, byte[] cff, byte[] original)
        {
            byte[] merged = DerivedFontWriter.WithTrueTypeOutlines(cff, out string refusal);
            check(merged != null, $"{file}: CFF outlines merged", refusal ?? "");
            if (merged == null) return;
            var m = new TtfParser(merged);
            var o = new TtfParser(original);
            check(m.TryGetTable("glyf", out _, out _) && !m.TryGetTable("CFF ", out _, out _), $"{file}: TrueType outlines, no CFF left", "");

            int n = o.GlyphCount, far = 0, advances = 0, mappings = 0;
            double worst = 0; string worstAt = "";
            for (int g = 0; g < n; g++)
            {
                if (m.GetAdvanceWidth(g) != o.GetAdvanceWidth(g)) advances++;
                double d = Hausdorff(o.OutlineOfAnyGlyph(g), m.OutlineOfAnyGlyph(g));
                if (d > worst) { worst = d; worstAt = $"glyph {g}"; }
                if (d > 3.0) far++;
            }
            foreach (int cp in o.GetSupportedCodepoints())
                if (cp != 0 && m.GetGlyphIndex(cp) != o.GetGlyphIndex(cp)) mappings++;
            check(m.GlyphCount == n && far == 0 && advances == 0 && mappings == 0,
                $"{file}: every merged glyph is the original's shape (within 3 units), advance and mapping",
                $"glyphs {m.GlyphCount}/{n}, {far} off, worst {worst:0.00} at {worstAt}, {advances} advances, {mappings} mappings");

            One(check, file + " (merged)", merged);
            byte[] direct = DerivedFontWriter.Write(cff, "UGT Check Cff", new[] { new DerivedFontWriter.Added { Codepoint = 0xE000, Glyph = 1, Advance = 500 } }, out string why);
            check(direct != null && new TtfParser(direct).GetGlyphIndex(0xE000) == n, $"{file}: the copy is written from the CFF file itself", why ?? "");
        }

        /// <summary>
        /// Symmetric distance between two outlines, in font units: each drawn as short segments
        /// (TrueType implied on-curve points, cubic or quadratic curves cut in 16), every vertex of one
        /// measured to the nearest segment of the other. Empty against empty is 0; empty against drawn
        /// is infinite.
        /// </summary>
        private static double Hausdorff(GlyphOutline a, GlyphOutline b)
        {
            var sa = Segments(a); var sb = Segments(b);
            if (sa.Count == 0 || sb.Count == 0) return sa.Count == sb.Count ? 0 : double.PositiveInfinity;
            return Math.Max(Directed(sa, sb), Directed(sb, sa));
        }

        private static double Directed(List<double[]> from, List<double[]> to)
        {
            const double Cell = 32;
            var grid = new Dictionary<long, List<double[]>>();
            long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
            foreach (var s in to)
            {
                int x0 = (int)Math.Floor(Math.Min(s[0], s[2]) / Cell), x1 = (int)Math.Floor(Math.Max(s[0], s[2]) / Cell);
                int y0 = (int)Math.Floor(Math.Min(s[1], s[3]) / Cell), y1 = (int)Math.Floor(Math.Max(s[1], s[3]) / Cell);
                for (int cx = x0; cx <= x1; cx++)
                    for (int cy = y0; cy <= y1; cy++)
                    {
                        if (!grid.TryGetValue(Key(cx, cy), out var list)) grid[Key(cx, cy)] = list = new List<double[]>();
                        list.Add(s);
                    }
            }
            double worst = 0;
            foreach (var s in from)
                foreach (var p in new[] { (s[0], s[1]), ((s[0] + s[2]) / 2, (s[1] + s[3]) / 2) })
                {
                    int cx = (int)Math.Floor(p.Item1 / Cell), cy = (int)Math.Floor(p.Item2 / Cell);
                    double best = double.PositiveInfinity;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                            if (grid.TryGetValue(Key(cx + dx, cy + dy), out var list))
                                foreach (var t in list) best = Math.Min(best, PointToSegment(p.Item1, p.Item2, t));
                    worst = Math.Max(worst, Math.Min(best, Cell));   // beyond a cell: far, and reported as such
                }
            return worst;
        }

        private static double PointToSegment(double px, double py, double[] s)
        {
            double vx = s[2] - s[0], vy = s[3] - s[1];
            double len = vx * vx + vy * vy;
            double t = len == 0 ? 0 : Math.Max(0, Math.Min(1, ((px - s[0]) * vx + (py - s[1]) * vy) / len));
            double dx = s[0] + t * vx - px, dy = s[1] + t * vy - py;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>An outline as straight segments {x0, y0, x1, y1}.</summary>
        private static List<double[]> Segments(GlyphOutline outline)
        {
            var segs = new List<double[]>();
            if (outline?.Contours == null) return segs;
            foreach (var c in outline.Contours)
            {
                var pts = c.Points;
                if (pts == null || pts.Length < 2) continue;
                // Every point once, with TrueType's implied on-curve point between two quadratic
                // off-curve ones; then the ring starts on an on-curve point and closes on it.
                var full = new List<ContourPoint>();
                for (int i = 0; i < pts.Length; i++)
                {
                    var p = pts[i]; var q = pts[(i + 1) % pts.Length];
                    full.Add(p);
                    if (!p.OnCurve && !p.IsCubic && !q.OnCurve && !q.IsCubic)
                        full.Add(new ContourPoint((p.X + q.X) / 2, (p.Y + q.Y) / 2, true));
                }
                int start = full.FindIndex(q => q.OnCurve);
                if (start < 0) continue;
                var ring = new List<ContourPoint>();
                for (int i = 0; i <= full.Count; i++) ring.Add(full[(start + i) % full.Count]);
                var on = ring[0];
                int k = 1;
                while (k < ring.Count)
                {
                    var p = ring[k];
                    var from = on;
                    if (p.OnCurve) { segs.Add(new double[] { from.X, from.Y, p.X, p.Y }); on = p; k++; continue; }
                    if (p.IsCubic && k + 2 < ring.Count)
                    {
                        var c2 = ring[k + 1]; var end = ring[k + 2];
                        Flatten(segs, t => Cubic(from, p, c2, end, t));
                        on = end; k += 3; continue;
                    }
                    if (k + 1 >= ring.Count) break;
                    var to = ring[k + 1];
                    Flatten(segs, t => Quad(from, p, to, t));
                    on = to; k += 2;
                }
            }
            return segs;
        }

        private static void Flatten(List<double[]> segs, Func<double, (double, double)> at)
        {
            var prev = at(0);
            for (int i = 1; i <= 16; i++)
            {
                var p = at(i / 16.0);
                segs.Add(new[] { prev.Item1, prev.Item2, p.Item1, p.Item2 });
                prev = p;
            }
        }

        private static (double, double) Quad(ContourPoint a, ContourPoint b, ContourPoint c, double t)
        {
            double u = 1 - t;
            return (u * u * a.X + 2 * u * t * b.X + t * t * c.X, u * u * a.Y + 2 * u * t * b.Y + t * t * c.Y);
        }

        private static (double, double) Cubic(ContourPoint a, ContourPoint b, ContourPoint c, ContourPoint d, double t)
        {
            double u = 1 - t;
            return (u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
                    u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y);
        }

        /// <summary>
        /// An installed font may be one face of a collection (FontCollection). The collection is built
        /// HERE, from two real fonts, each at its own place with its table offsets moved — so the face
        /// taken out is checked against the font that went in, never against the extractor.
        /// </summary>
        private static void Collection(Action<bool, string, string> check, byte[] a, byte[] b)
        {
            int header = 12 + 2 * 4;
            int atA = header, atB = header + ((a.Length + 3) & ~3);
            var ttc = new byte[atB + b.Length];
            Put32(ttc, 0, 0x74746366); Put32(ttc, 4, 0x00010000); Put32(ttc, 8, 2);
            Put32(ttc, 12, (uint)atA); Put32(ttc, 16, (uint)atB);
            foreach (var (font, at) in new[] { (a, atA), (b, atB) })
            {
                Array.Copy(font, 0, ttc, at, font.Length);
                int tables = font[4] << 8 | font[5];
                for (int t = 0; t < tables; t++)
                {
                    int r = at + 12 + t * 16 + 8;
                    Put32(ttc, r, (uint)((ttc[r] << 24 | ttc[r + 1] << 16 | ttc[r + 2] << 8 | ttc[r + 3]) + at));
                }
            }

            var khmer = new TtfParser(b);
            using (var stream = new MemoryStream(ttc))
            {
                check(FontCollection.FindFace(stream, khmer.Metrics.FontName) == 1, "collection: the face named like the second font is found", khmer.Metrics.FontName);
                check(FontCollection.FindFace(stream, "No Such Font") == -1, "collection: an absent name finds no face", "");
            }
            using (var single = new MemoryStream(b))
                check(FontCollection.FindFace(single, khmer.Metrics.FontName) == -1, "collection: a single font is not searched as a collection", "");
            check(ReferenceEquals(FontCollection.Face(b, 0), b), "collection: a single font is its own face", "");

            var face = new TtfParser(FontCollection.Face(ttc, 1));
            bool same = face.GlyphCount == khmer.GlyphCount && face.Metrics.FontName == khmer.Metrics.FontName;
            int differ = 0;
            for (int g = 1; g < khmer.GlyphCount && same; g++)
                if (!SameOutlineMoved(khmer.GetGlyphOutlineByIndex(g), face.GetGlyphOutlineByIndex(g), 0, 0, out _)) differ++;
            foreach (int cp in khmer.GetSupportedCodepoints())
                if (cp != 0 && face.GetGlyphIndex(cp) != khmer.GetGlyphIndex(cp)) differ++;
            check(same && differ == 0, "collection: the face taken out is the font that went in (names, glyphs, outlines, mappings)",
                $"glyphs {face.GlyphCount}/{khmer.GlyphCount}, {differ} difference(s)");
        }

        private static void Put32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static void One(Action<bool, string, string> check, string file, byte[] bytes)
        {
            var source = new TtfParser(bytes);
            int n = source.GlyphCount;
            var unmapped = source.UnmappedGlyphs();
            int space = source.GetGlyphIndex(0x20);
            int anyMapped = source.GetGlyphIndex(source.GetSupportedCodepoints()[source.GetSupportedCodepoints().Length / 2]);

            // An unmapped glyph unmoved, the same one moved, a mapped glyph moved AND spaced differently,
            // a negative offset, and an empty glyph (the space) with another advance.
            var added = new List<DerivedFontWriter.Added>
            {
                new DerivedFontWriter.Added { Codepoint = 0xE000, Glyph = unmapped[0], DX = 0, DY = 0, Advance = source.GetAdvanceWidth(unmapped[0]) },
                new DerivedFontWriter.Added { Codepoint = 0xE001, Glyph = unmapped[0], DX = 120, DY = -80, Advance = 0 },
                new DerivedFontWriter.Added { Codepoint = 0xE002, Glyph = anyMapped, DX = -300, DY = 250, Advance = source.GetAdvanceWidth(anyMapped) + 57 },
                new DerivedFontWriter.Added { Codepoint = 0xE003, Glyph = space, DX = 0, DY = 0, Advance = 111 },
            };
            byte[] derived = DerivedFontWriter.Write(bytes, "UGT Check Derived", added, out string refusal);
            check(derived != null, $"{file}: derived font written", refusal ?? "");
            if (derived == null) return;
            var d = new TtfParser(derived);

            check(d.GlyphCount == n + added.Count, $"{file}: {added.Count} glyphs added", $"{d.GlyphCount} (source {n})");
            for (int k = 0; k < added.Count; k++)
            {
                var a = added[k];
                int g = d.GetGlyphIndex(a.Codepoint);
                check(g == n + k, $"{file}: U+{a.Codepoint:X4} reaches the added glyph", $"{g}");
                check(d.GetAdvanceWidth(g) == a.Advance, $"{file}: U+{a.Codepoint:X4} advances by {a.Advance}", d.GetAdvanceWidth(g).ToString());
                check(SameOutlineMoved(source.GetGlyphOutlineByIndex(a.Glyph), d.GetGlyphOutlineByIndex(g), a.DX, a.DY, out string why),
                    $"{file}: U+{a.Codepoint:X4} is glyph {a.Glyph} moved by ({a.DX}, {a.DY})", why);
            }

            // Every mapping of the source still holds, and the source's glyphs are untouched.
            int lost = 0, moved = 0;
            string firstLost = "";
            foreach (int cp in source.GetSupportedCodepoints())
            {
                // U+0000 is read from a format 4 subtable and skipped from a format 12 one by the parser
                // (TtfParser.ParseCmapFormat12, `unicode > 0`): a source with only format 4 shows it, the
                // derived font (which has both) does not. Not a character any text draws.
                if (cp == 0) continue;
                if (d.GetGlyphIndex(cp) != source.GetGlyphIndex(cp))
                {
                    if (lost++ == 0) firstLost = $" — first: U+{cp:X4} {source.GetGlyphIndex(cp)} → {d.GetGlyphIndex(cp)}";
                }
                else if (!SameOutlineMoved(source.GetGlyphOutline(cp), d.GetGlyphOutline(cp), 0, 0, out _)) moved++;
            }
            check(lost == 0 && moved == 0, $"{file}: every source mapping and outline kept", $"{lost} remapped, {moved} changed{firstLost}");
            check(d.Metrics.FontName == "UGT Check Derived", $"{file}: another family name", d.Metrics.FontName ?? "none");
            check(!d.TryGetTable("gvar", out _, out _) && !d.TryGetTable("fvar", out _, out _) && !d.TryGetTable("HVAR", out _, out _),
                $"{file}: no variation table left", "");
            check(d.TryGetTable("GSUB", out _, out _) == source.TryGetTable("GSUB", out _, out _)
                  && d.TryGetTable("GPOS", out _, out _) == source.TryGetTable("GPOS", out _, out _),
                $"{file}: layout tables kept", "");

            // Refusals, each said.
            DerivedFontWriter.Write(bytes, "x", new[] { new DerivedFontWriter.Added { Codepoint = source.GetGlyphIndex('A') > 0 ? 'A' : 0x20, Glyph = 1, Advance = 1 } }, out string taken);
            check(taken != null && taken.Contains("already mapped"), $"{file}: a codepoint the font maps is refused", taken ?? "accepted");
            DerivedFontWriter.Write(bytes, "x", new[] { new DerivedFontWriter.Added { Codepoint = 0xF0000, Glyph = 1, Advance = 1 } }, out string astral);
            check(astral != null && astral.Contains("Basic Multilingual Plane"), $"{file}: a codepoint above U+FFFF is refused", astral ?? "accepted");
            DerivedFontWriter.Write(bytes, "x", new[] { new DerivedFontWriter.Added { Codepoint = 0xE000, Glyph = n, Advance = 1 } }, out string noGlyph);
            check(noGlyph != null && noGlyph.Contains("does not exist"), $"{file}: a glyph out of range is refused", noGlyph ?? "accepted");
        }

        /// <summary>Same contours, every point moved by (dx, dy). An empty glyph matches an empty one.</summary>
        private static bool SameOutlineMoved(GlyphOutline a, GlyphOutline b, int dx, int dy, out string why)
        {
            why = "";
            if (a == null || b == null) { why = a == null ? "source outline unreadable" : "derived outline unreadable"; return false; }
            int ca = a.Contours?.Length ?? 0, cb = b.Contours?.Length ?? 0;
            if (ca != cb) { why = $"{ca} contours vs {cb}"; return false; }
            for (int c = 0; c < ca; c++)
            {
                var pa = a.Contours[c].Points; var pb = b.Contours[c].Points;
                if (pa.Length != pb.Length) { why = $"contour {c}: {pa.Length} points vs {pb.Length}"; return false; }
                for (int i = 0; i < pa.Length; i++)
                {
                    if (Math.Abs(pa[i].X + dx - pb[i].X) > 0.001f || Math.Abs(pa[i].Y + dy - pb[i].Y) > 0.001f || pa[i].OnCurve != pb[i].OnCurve)
                    {
                        why = $"contour {c} point {i}: ({pa[i].X},{pa[i].Y}) → ({pb[i].X},{pb[i].Y})";
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
