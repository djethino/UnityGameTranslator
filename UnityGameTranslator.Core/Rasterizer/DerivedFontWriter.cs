using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Writes the DERIVED font of a TrueType font: the same font, plus one glyph per shaped glyph an
    /// engine must draw — a TrueType composite of the source glyph, shifted by the offset the shaper
    /// gave it and advancing by the width it gave it — each reachable by a private-use codepoint of
    /// the Basic Multilingual Plane. An engine that draws BY CODEPOINT then draws exactly what the
    /// shaper placed (analyse/ecritures-complexes-etat-reel.md, probes 1 and 2).
    ///
    /// Why EVERY glyph of a shaped line is a new glyph, even one the shaper left in place: TMP 3.2+
    /// and UI Toolkit's TextCore apply the font's own GPOS to the glyph ids they meet, so an original
    /// glyph in the string is positioned a second time (probe 1, Unity 6000.0). A glyph added here is
    /// in no layout table: nothing can move it again.
    ///
    /// A CFF (PostScript-outline .otf) font is first merged into TrueType outlines (TrueTypeFromCff):
    /// a composite can only reference a TrueType glyph.
    ///
    /// What changes in the file: cmap (a format 4 and a format 12 subtable, the source's mappings plus
    /// the new ones), glyf + loca (long offsets), hmtx + hhea (one metric per glyph), maxp, name (another
    /// family — the derived file must never pass for the original; notices kept), post (format 3: no
    /// glyph names to keep in step), head (checksum). Dropped: the variation tables (glyf already holds
    /// the default instance — what an engine draws of a variable font), the tables sized by glyph count
    /// we do not rebuild, and the signature the edit invalidates. GSUB/GPOS/GDEF stay: they name the
    /// source's glyph ids, which do not move.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class DerivedFontWriter
    {
        /// <summary>One glyph to add: this codepoint shows the source glyph moved by (DX, DY), advancing by Advance.</summary>
        internal struct Added
        {
            public int Codepoint;
            public int Glyph;
            public int DX, DY;
            public int Advance;
        }

        private static readonly HashSet<string> Dropped = new HashSet<string>(StringComparer.Ordinal)
        {
            "fvar", "gvar", "HVAR", "MVAR", "VVAR", "avar", "STAT", "cvar",   // variations: glyf is the default instance
            "hdmx", "LTSH", "VDMX",                                          // sized by glyph count, not rebuilt
            "DSIG",                                                          // a signature this edit invalidates
        };

        /// <summary>
        /// The derived file, or null with <paramref name="refusal"/> saying why — a font without
        /// TrueType outlines (CFF: a composite cannot reference its glyphs), a codepoint the source
        /// already maps, a codepoint outside the Basic Multilingual Plane, a glyph index out of range.
        /// </summary>
        internal static byte[] Write(byte[] source, string family, IList<Added> added, out string refusal)
        {
            refusal = null;
            // A composite cannot reference a CFF glyph: the outlines are merged into TrueType ones first
            // (user, 2026-09-30), then the copy is made from that as from any TrueType font.
            source = WithTrueTypeOutlines(source, out refusal);
            if (source == null) return null;
            var tables = ReadDirectory(source);
            foreach (var need in new[] { "head", "hhea", "hmtx", "maxp", "cmap" })
                if (!tables.ContainsKey(need)) { refusal = $"the '{need}' table is missing"; return null; }

            var parser = new TtfParser(source);
            int numGlyphs = parser.GlyphCount;
            var cmap = new SortedDictionary<int, int>();
            foreach (int cp in parser.GetSupportedCodepoints())
                cmap[cp] = parser.GetGlyphIndex(cp);

            foreach (var a in added)
            {
                if (a.Codepoint <= 0 || a.Codepoint > 0xFFFF) { refusal = $"U+{a.Codepoint:X} is not a codepoint of the Basic Multilingual Plane"; return null; }
                if (cmap.ContainsKey(a.Codepoint)) { refusal = $"U+{a.Codepoint:X4} is already mapped by the font"; return null; }
                if (a.Glyph < 0 || a.Glyph >= numGlyphs) { refusal = $"glyph {a.Glyph} does not exist (the font has {numGlyphs})"; return null; }
            }

            byte[] head = Slice(source, tables["head"]);
            byte[] hhea = Slice(source, tables["hhea"]);
            byte[] maxp = Slice(source, tables["maxp"]);
            byte[] loca = Slice(source, tables["loca"]);
            byte[] glyf = Slice(source, tables["glyf"]);
            byte[] hmtx = Slice(source, tables["hmtx"]);

            bool longLoca = ReadInt16(head, 50) == 1;
            var offsets = new uint[numGlyphs + 1];
            for (int i = 0; i <= numGlyphs; i++)
                offsets[i] = longLoca ? ReadUInt32(loca, i * 4) : (uint)ReadUInt16(loca, i * 2) * 2;

            // ── glyf + loca: the source's glyphs as they are, then one composite per added glyph ──
            int total = numGlyphs + added.Count;
            if (total > 0xFFFF) { refusal = $"{total} glyphs: more than a TrueType font can hold"; return null; }

            // The source's glyphs as they are (long offsets from now on), then one glyph per added one,
            // in order: glyph numGlyphs + k is added[k].
            var glyfOut = new MemoryStream();
            glyfOut.Write(glyf, 0, (int)offsets[numGlyphs]);
            var locaOut = new MemoryStream();
            for (int i = 0; i < numGlyphs; i++) WriteUInt32(locaOut, offsets[i]);
            var newMetrics = new List<KeyValuePair<int, int>>(added.Count);   // (advance, lsb)
            int sourceDepth = maxp.Length >= 32 ? ReadUInt16(maxp, 30) : 0;  // maxComponentDepth (maxp 1.0)
            int maxDepth = sourceDepth;
            foreach (var a in added)
            {
                WriteUInt32(locaOut, (uint)glyfOut.Length);                    // where this glyph starts
                uint start = offsets[a.Glyph], end = offsets[a.Glyph + 1];
                if (end == start)
                {
                    // An empty source glyph (a space) has nothing to reference: an empty glyph that
                    // only advances.
                    newMetrics.Add(new KeyValuePair<int, int>(a.Advance, 0));
                    continue;
                }
                int xMin = ReadInt16(glyf, (int)start + 2) + a.DX, yMin = ReadInt16(glyf, (int)start + 4) + a.DY;
                int xMax = ReadInt16(glyf, (int)start + 6) + a.DX, yMax = ReadInt16(glyf, (int)start + 8) + a.DY;
                WriteInt16(glyfOut, -1);                                       // numberOfContours: composite
                WriteInt16(glyfOut, xMin); WriteInt16(glyfOut, yMin); WriteInt16(glyfOut, xMax); WriteInt16(glyfOut, yMax);
                WriteUInt16(glyfOut, 0x0001 | 0x0002);                         // ARG_1_AND_2_ARE_WORDS | ARGS_ARE_XY_VALUES
                WriteUInt16(glyfOut, a.Glyph);
                WriteInt16(glyfOut, a.DX);
                WriteInt16(glyfOut, a.DY);
                while (glyfOut.Length % 4 != 0) glyfOut.WriteByte(0);
                newMetrics.Add(new KeyValuePair<int, int>(a.Advance, xMin));
                // One level above its component: a composite of a composite is one deeper than the source's deepest.
                maxDepth = Math.Max(maxDepth, ReadInt16(glyf, (int)start) < 0 ? sourceDepth + 1 : 1);
            }
            WriteUInt32(locaOut, (uint)glyfOut.Length);                        // the end of the last glyph
            byte[] glyfNew = glyfOut.ToArray();

            // ── hmtx + hhea: one (advance, lsb) per glyph ──
            int numH = ReadUInt16(hhea, 34);
            var hmtxOut = new MemoryStream();
            int lastAdvance = 0, maxAdvance = ReadUInt16(hhea, 10);
            for (int i = 0; i < numGlyphs; i++)
            {
                int adv, lsb;
                if (i < numH) { adv = ReadUInt16(hmtx, i * 4); lsb = ReadInt16(hmtx, i * 4 + 2); lastAdvance = adv; }
                else { adv = lastAdvance; lsb = ReadInt16(hmtx, numH * 4 + (i - numH) * 2); }
                WriteUInt16(hmtxOut, adv); WriteInt16(hmtxOut, lsb);
            }
            foreach (var m in newMetrics)
            {
                WriteUInt16(hmtxOut, Math.Max(0, m.Key)); WriteInt16(hmtxOut, m.Value);
                maxAdvance = Math.Max(maxAdvance, m.Key);
            }
            WriteUInt16At(hhea, 34, total);
            WriteUInt16At(hhea, 10, maxAdvance);

            // ── maxp, head, post ──
            WriteUInt16At(maxp, 4, total);
            if (maxp.Length >= 32)
            {
                WriteUInt16At(maxp, 28, Math.Max(ReadUInt16(maxp, 28), 1));    // maxComponentElements
                WriteUInt16At(maxp, 30, maxDepth);
            }
            WriteUInt16At(head, 50, 1);                                        // indexToLocFormat: long
            WriteUInt32At(head, 8, 0);                                         // checkSumAdjustment, set last
            var post = new MemoryStream();
            byte[] oldPost = tables.ContainsKey("post") ? Slice(source, tables["post"]) : new byte[32];
            WriteUInt32(post, 0x00030000);                                     // format 3: no glyph names
            post.Write(oldPost, 4, Math.Min(28, oldPost.Length - 4));
            while (post.Length < 32) post.WriteByte(0);

            // ── cmap: the source's mappings and the added ones ──
            for (int k = 0; k < added.Count; k++) cmap[added[k].Codepoint] = numGlyphs + k;
            byte[] cmapNew = BuildCmap(cmap);

            // ── name: another family, every notice kept ──
            byte[] nameNew = tables.ContainsKey("name") ? RenameFamily(Slice(source, tables["name"]), family) : BuildName(family);

            var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var t in tables)
            {
                if (Dropped.Contains(t.Key)) continue;
                output[t.Key] = Slice(source, t.Value);
            }
            output["glyf"] = glyfNew;
            output["loca"] = locaOut.ToArray();
            output["hmtx"] = hmtxOut.ToArray();
            output["hhea"] = hhea;
            output["maxp"] = maxp;
            output["head"] = head;
            output["post"] = post.ToArray();
            output["cmap"] = cmapNew;
            output["name"] = nameNew;
            if (output.ContainsKey("OS/2")) output["OS/2"] = WidenCharRange(output["OS/2"], cmap);
            return Assemble(output);
        }

        /// <summary>
        /// The smallest valid TrueType font of a family: one empty .notdef, no character — a POOL name
        /// (FontPool) the engine lists at start and that is written with a real font only when one is
        /// needed under it. Proven on the probe bench: listed empty, filled later, drawn (Unity 2018.4,
        /// 2021.3, 6000.6; Mono and IL2CPP paths — analyse/ecritures-complexes-etat-reel.md, the pool).
        /// </summary>
        internal static byte[] Placeholder(string family)
        {
            const int upem = 1000, ascent = 800, descent = -200, advance = 500;
            var head = new byte[54];
            WriteUInt32At(head, 0, 0x00010000);                                // version
            WriteUInt32At(head, 4, 0x00010000);                                // fontRevision
            WriteUInt32At(head, 12, 0x5F0F3CF5);                               // magicNumber
            WriteUInt16At(head, 16, 0x000B);                                   // flags: baseline at 0, lsb at 0, integer ppem
            WriteUInt16At(head, 18, upem);
            WriteUInt16At(head, 46, 8);                                        // lowestRecPPEM
            WriteUInt16At(head, 48, 2);                                        // fontDirectionHint
            WriteUInt16At(head, 50, 1);                                        // indexToLocFormat: long

            var hhea = new byte[36];
            WriteUInt32At(hhea, 0, 0x00010000);
            WriteUInt16At(hhea, 4, ascent);
            WriteUInt16At(hhea, 6, descent & 0xFFFF);
            WriteUInt16At(hhea, 10, advance);                                  // advanceWidthMax
            WriteUInt16At(hhea, 18, 1);                                        // caretSlopeRise
            WriteUInt16At(hhea, 34, 1);                                        // numberOfHMetrics

            var maxp = new byte[32];                                           // version 1.0: TrueType outlines
            WriteUInt32At(maxp, 0, 0x00010000);
            WriteUInt16At(maxp, 4, 1);                                         // numGlyphs
            WriteUInt16At(maxp, 14, 2);                                        // maxZones

            var os2 = new byte[96];                                            // version 4
            WriteUInt16At(os2, 0, 4);
            WriteUInt16At(os2, 2, advance);                                    // xAvgCharWidth
            WriteUInt16At(os2, 4, 400);                                        // usWeightClass: regular
            WriteUInt16At(os2, 6, 5);                                          // usWidthClass: medium
            Encoding.ASCII.GetBytes("UGT ", 0, 4, os2, 58);                    // achVendID
            WriteUInt16At(os2, 62, 0x0040);                                    // fsSelection: REGULAR
            WriteUInt16At(os2, 64, 0xFFFF);                                    // usFirstCharIndex: no character
            WriteUInt16At(os2, 68, ascent);
            WriteUInt16At(os2, 70, descent & 0xFFFF);
            WriteUInt16At(os2, 74, ascent);                                    // usWinAscent
            WriteUInt16At(os2, 76, -descent);                                  // usWinDescent
            WriteUInt32At(os2, 78, 1);                                         // ulCodePageRange1: Latin 1
            WriteUInt16At(os2, 92, 32);                                        // usBreakChar

            var hmtx = new byte[4];
            WriteUInt16At(hmtx, 0, advance);

            var post = new byte[32];
            WriteUInt32At(post, 0, 0x00030000);                                // format 3: no glyph names

            var records = new List<Tuple<int, int, int, int, byte[]>>(OurRecords(family))
            {
                Tuple.Create(3, 1, 0x409, 2, Encoding.BigEndianUnicode.GetBytes("Regular")),
            };

            return Assemble(new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["head"] = head,
                ["hhea"] = hhea,
                ["maxp"] = maxp,
                ["OS/2"] = os2,
                ["hmtx"] = hmtx,
                ["cmap"] = BuildCmap(new SortedDictionary<int, int>()),
                ["loca"] = new byte[8],                                        // .notdef starts and ends at 0: empty
                ["glyf"] = new byte[0],
                ["name"] = BuildNameTable(records),
                ["post"] = post,
            });
        }

        // ─────────────────────────── CFF → TrueType ───────────────────────────

        /// <summary>
        /// The font with TrueType outlines: itself when it has them, its CFF outlines merged otherwise
        /// (null, with the refusal, when it has neither). Done once by a caller that writes the copy
        /// again and again (DerivedFonts): the merge reads every glyph.
        /// </summary>
        internal static byte[] WithTrueTypeOutlines(byte[] source, out string refusal)
        {
            refusal = null;
            var tables = ReadDirectory(source);
            if (tables.ContainsKey("glyf") && tables.ContainsKey("loca")) return source;
            if (tables.ContainsKey("CFF ")) return TrueTypeFromCff(source, tables, out refusal);
            refusal = tables.ContainsKey("CFF2")
                ? "CFF2 outlines (a variable PostScript font) are not read"
                : "no outlines this writer reads (neither TrueType nor CFF)";
            return null;
        }

        /// <summary>
        /// The same font with its CFF outlines turned into TrueType ones — every glyph, same ids, same
        /// advances; each cubic curve approximated by quadratic ones within a tolerance of a thousandth
        /// of the em (the tolerance fontTools' otf2ttf uses). The layout tables name glyph ids, which
        /// do not move. Hints are not carried over: TrueType hinting is instructions, and no engine the
        /// copy serves asks for them.
        /// </summary>
        private static byte[] TrueTypeFromCff(byte[] source, Dictionary<string, Entry> tables, out string refusal)
        {
            refusal = null;
            foreach (var need in new[] { "head", "hhea", "hmtx", "maxp" })
                if (!tables.ContainsKey(need)) { refusal = $"the '{need}' table is missing"; return null; }
            var parser = new TtfParser(source);
            int numGlyphs = parser.GlyphCount;
            byte[] head = Slice(source, tables["head"]);
            byte[] hhea = Slice(source, tables["hhea"]);
            int upem = ReadUInt16(head, 18);
            double tolerance = Math.Max(0.5, upem / 1000.0);

            var glyf = new MemoryStream();
            var loca = new MemoryStream();
            var hmtx = new MemoryStream();
            int maxPoints = 0, maxContours = 0;
            for (int g = 0; g < numGlyphs; g++)
            {
                WriteUInt32(loca, (uint)glyf.Length);
                var contours = new List<List<QuadPoint>>();
                var outline = parser.OutlineOfAnyGlyph(g);
                if (outline != null && !outline.IsEmpty && outline.Contours != null)
                    foreach (var c in outline.Contours)
                    {
                        var q = Quadratic(c.Points, tolerance);
                        if (q.Count >= 2) contours.Add(q);
                    }
                int xMin = 0;
                if (contours.Count > 0)
                {
                    xMin = WriteSimpleGlyph(glyf, contours, out int points);
                    maxPoints = Math.Max(maxPoints, points);
                    maxContours = Math.Max(maxContours, contours.Count);
                }
                // The left side bearing IS the outline's left edge in TrueType: an engine places the
                // outline from it.
                WriteUInt16(hmtx, parser.GetAdvanceWidth(g)); WriteInt16(hmtx, xMin);
            }
            WriteUInt32(loca, (uint)glyf.Length);

            var maxp = new byte[32];
            WriteUInt32At(maxp, 0, 0x00010000);                                // version 1.0: TrueType outlines
            WriteUInt16At(maxp, 4, numGlyphs);
            WriteUInt16At(maxp, 6, maxPoints);
            WriteUInt16At(maxp, 8, maxContours);
            WriteUInt16At(maxp, 14, 2);                                        // maxZones
            WriteUInt16At(hhea, 34, numGlyphs);                                // one metric per glyph
            WriteUInt16At(head, 50, 1);                                        // indexToLocFormat: long
            WriteUInt16At(head, 52, 0);                                        // glyphDataFormat
            WriteUInt32At(head, 8, 0);                                         // checkSumAdjustment, set by Assemble

            var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var t in tables)
            {
                if (t.Key == "CFF " || t.Key == "VORG") continue;               // the PostScript outlines and their vertical origins
                output[t.Key] = Slice(source, t.Value);
            }
            output["glyf"] = glyf.ToArray();
            output["loca"] = loca.ToArray();
            output["hmtx"] = hmtx.ToArray();
            output["hhea"] = hhea;
            output["maxp"] = maxp;
            output["head"] = head;
            return Assemble(output);
        }

        internal struct QuadPoint { public int X, Y; public bool On; }

        /// <summary>
        /// A CFF contour (on-curve points, and pairs of cubic control points before an on-curve one) as
        /// a TrueType one: lines kept, each cubic replaced by quadratic pieces with explicit on-curve
        /// points between them. The closing point that repeats the first is dropped: a TrueType contour
        /// closes by itself.
        /// </summary>
        internal static List<QuadPoint> Quadratic(ContourPoint[] points, double tolerance)
        {
            var output = new List<QuadPoint>();
            if (points == null || points.Length == 0) return output;
            double px = points[0].X, py = points[0].Y;
            Add(output, px, py, true);
            for (int i = 1; i < points.Length; i++)
            {
                var p = points[i];
                if (p.OnCurve) { Add(output, p.X, p.Y, true); px = p.X; py = p.Y; continue; }
                if (p.IsCubic && i + 2 < points.Length && !points[i + 1].OnCurve && points[i + 2].OnCurve)
                {
                    var c2 = points[i + 1]; var end = points[i + 2];
                    CubicToQuadratics(px, py, p.X, p.Y, c2.X, c2.Y, end.X, end.Y, tolerance, output);
                    px = end.X; py = end.Y;
                    i += 2;
                    continue;
                }
                Add(output, p.X, p.Y, false);                                  // already quadratic
            }
            var first = output[0];
            var last = output[output.Count - 1];
            if (output.Count > 1 && last.On && last.X == first.X && last.Y == first.Y) output.RemoveAt(output.Count - 1);
            return output;
        }

        private static void Add(List<QuadPoint> output, double x, double y, bool on)
        {
            var q = new QuadPoint { X = (int)Math.Round(x), Y = (int)Math.Round(y), On = on };
            // Two on-curve points at the same place are one (a zero-length line).
            if (on && output.Count > 0)
            {
                var prev = output[output.Count - 1];
                if (prev.On && prev.X == q.X && prev.Y == q.Y) return;
            }
            output.Add(q);
        }

        /// <summary>
        /// One cubic as the fewest quadratic pieces (1 to 16, equal steps of t) whose distance to it,
        /// sampled along each piece, stays within the tolerance. Each piece's control point is the
        /// classic mid-point estimate (3·(c1 + c2) − (p0 + p3)) / 4 of its sub-cubic.
        /// </summary>
        internal static void CubicToQuadratics(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3,
                                               double tolerance, List<QuadPoint> output)
        {
            const int MaxPieces = 16;
            for (int n = 1; n <= MaxPieces; n++)
            {
                var pieces = new double[n][];
                bool fits = true;
                for (int k = 0; k < n && fits; k++)
                {
                    var s = SubCubic(x0, y0, x1, y1, x2, y2, x3, y3, (double)k / n, (double)(k + 1) / n);
                    double qx = (3 * (s[2] + s[4]) - (s[0] + s[6])) / 4, qy = (3 * (s[3] + s[5]) - (s[1] + s[7])) / 4;
                    pieces[k] = new[] { qx, qy, s[6], s[7] };
                    for (int j = 1; j < 8 && fits; j++)
                    {
                        double t = j / 8.0, u = 1 - t;
                        double cx = u * u * u * s[0] + 3 * u * u * t * s[2] + 3 * u * t * t * s[4] + t * t * t * s[6];
                        double cy = u * u * u * s[1] + 3 * u * u * t * s[3] + 3 * u * t * t * s[5] + t * t * t * s[7];
                        double ax = u * u * s[0] + 2 * u * t * qx + t * t * s[6];
                        double ay = u * u * s[1] + 2 * u * t * qy + t * t * s[7];
                        if ((cx - ax) * (cx - ax) + (cy - ay) * (cy - ay) > tolerance * tolerance) fits = false;
                    }
                }
                if (!fits && n < MaxPieces) continue;
                foreach (var p in pieces) { Add(output, p[0], p[1], false); Add(output, p[2], p[3], true); }
                return;
            }
        }

        /// <summary>The part of a cubic between t0 and t1, as its 8 coordinates (de Casteljau).</summary>
        private static double[] SubCubic(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double t0, double t1)
        {
            double[] P(double t)
            {
                double u = 1 - t;
                return new[] { u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3,
                               u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3 };
            }
            double[] D(double t)
            {
                double u = 1 - t;
                return new[] { 3 * u * u * (x1 - x0) + 6 * u * t * (x2 - x1) + 3 * t * t * (x3 - x2),
                               3 * u * u * (y1 - y0) + 6 * u * t * (y2 - y1) + 3 * t * t * (y3 - y2) };
            }
            // A sub-cubic's control points from the end points and the derivatives, scaled by its span.
            double h = (t1 - t0) / 3;
            var a = P(t0); var b = P(t1); var da = D(t0); var db = D(t1);
            return new[] { a[0], a[1], a[0] + h * da[0], a[1] + h * da[1], b[0] - h * db[0], b[1] - h * db[1], b[0], b[1] };
        }

        /// <summary>A TrueType simple glyph, coordinates as 16-bit deltas. Returns its xMin.</summary>
        private static int WriteSimpleGlyph(MemoryStream glyf, List<List<QuadPoint>> contours, out int points)
        {
            int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
            points = 0;
            foreach (var c in contours)
                foreach (var p in c)
                {
                    xMin = Math.Min(xMin, p.X); yMin = Math.Min(yMin, p.Y);
                    xMax = Math.Max(xMax, p.X); yMax = Math.Max(yMax, p.Y);
                    points++;
                }
            WriteInt16(glyf, contours.Count);
            WriteInt16(glyf, xMin); WriteInt16(glyf, yMin); WriteInt16(glyf, xMax); WriteInt16(glyf, yMax);
            int end = -1;
            foreach (var c in contours) { end += c.Count; WriteUInt16(glyf, end); }
            WriteUInt16(glyf, 0);                                              // no instructions
            foreach (var c in contours) foreach (var p in c) glyf.WriteByte((byte)(p.On ? 1 : 0));
            int prev = 0;
            foreach (var c in contours) foreach (var p in c) { WriteInt16(glyf, p.X - prev); prev = p.X; }
            prev = 0;
            foreach (var c in contours) foreach (var p in c) { WriteInt16(glyf, p.Y - prev); prev = p.Y; }
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
            return xMin;
        }

        // ─────────────────────────────── cmap ───────────────────────────────

        private static byte[] BuildCmap(SortedDictionary<int, int> map)
        {
            byte[] f4 = BuildFormat4(map);
            byte[] f12 = BuildFormat12(map);
            var ms = new MemoryStream();
            WriteUInt16(ms, 0);                                                // version
            WriteUInt16(ms, 2);                                                // subtables
            WriteUInt16(ms, 3); WriteUInt16(ms, 1); WriteUInt32(ms, 4 + 8 * 2);                    // Windows BMP → format 4
            WriteUInt16(ms, 3); WriteUInt16(ms, 10); WriteUInt32(ms, (uint)(4 + 8 * 2 + f4.Length)); // Windows full → format 12
            ms.Write(f4, 0, f4.Length);
            ms.Write(f12, 0, f12.Length);
            return ms.ToArray();
        }

        /// <summary>Format 4: runs of consecutive codepoints mapped to consecutive glyphs, each a segment with idDelta.</summary>
        private static byte[] BuildFormat4(SortedDictionary<int, int> map)
        {
            var starts = new List<int>(); var ends = new List<int>(); var deltas = new List<int>();
            int runStart = -1, runEnd = -1, runGlyph = -1;
            foreach (var kv in map)
            {
                if (kv.Key > 0xFFFE) break;
                if (runStart >= 0 && kv.Key == runEnd + 1 && kv.Value == runGlyph + (kv.Key - runStart)) { runEnd = kv.Key; continue; }
                if (runStart >= 0) { starts.Add(runStart); ends.Add(runEnd); deltas.Add(runGlyph - runStart); }
                runStart = runEnd = kv.Key; runGlyph = kv.Value;
            }
            if (runStart >= 0) { starts.Add(runStart); ends.Add(runEnd); deltas.Add(runGlyph - runStart); }
            starts.Add(0xFFFF); ends.Add(0xFFFF); deltas.Add(1);               // the closing segment

            int segs = starts.Count;
            var ms = new MemoryStream();
            WriteUInt16(ms, 4);
            WriteUInt16(ms, 16 + segs * 8);                                    // length
            WriteUInt16(ms, 0);                                                // language
            WriteUInt16(ms, segs * 2);
            int sr = 2 * (1 << (int)Math.Floor(Math.Log(segs, 2)));
            WriteUInt16(ms, sr);
            WriteUInt16(ms, (int)Math.Floor(Math.Log(segs, 2)));
            WriteUInt16(ms, segs * 2 - sr);
            foreach (int e in ends) WriteUInt16(ms, e);
            WriteUInt16(ms, 0);                                                // reservedPad
            foreach (int s in starts) WriteUInt16(ms, s);
            foreach (int d in deltas) WriteUInt16(ms, d & 0xFFFF);
            foreach (int _ in starts) WriteUInt16(ms, 0);                      // idRangeOffset: always idDelta
            if (ms.Length > 0xFFFF) throw new InvalidDataException("cmap format 4 over 64 KB");
            return ms.ToArray();
        }

        private static byte[] BuildFormat12(SortedDictionary<int, int> map)
        {
            var groups = new List<int[]>();
            int s = -1, e = -1, g = -1;
            foreach (var kv in map)
            {
                if (s >= 0 && kv.Key == e + 1 && kv.Value == g + (kv.Key - s)) { e = kv.Key; continue; }
                if (s >= 0) groups.Add(new[] { s, e, g });
                s = e = kv.Key; g = kv.Value;
            }
            if (s >= 0) groups.Add(new[] { s, e, g });
            var ms = new MemoryStream();
            WriteUInt16(ms, 12); WriteUInt16(ms, 0);
            WriteUInt32(ms, (uint)(16 + groups.Count * 12));
            WriteUInt32(ms, 0);
            WriteUInt32(ms, (uint)groups.Count);
            foreach (var grp in groups) { WriteUInt32(ms, (uint)grp[0]); WriteUInt32(ms, (uint)grp[1]); WriteUInt32(ms, (uint)grp[2]); }
            return ms.ToArray();
        }

        // ─────────────────────────────── name ───────────────────────────────

        private static readonly int[] FamilyIds = { 1, 3, 4, 6, 16, 17, 21, 22 };

        /// <summary>
        /// The source's name records minus those that say which font this is, plus ours for Windows
        /// (3, 1, en-US). Copyright, licence and every other notice are kept word for word (OFL).
        /// </summary>
        private static byte[] RenameFamily(byte[] name, string family)
        {
            int count = ReadUInt16(name, 2), storage = ReadUInt16(name, 4);
            var kept = new List<Tuple<int, int, int, int, byte[]>>();
            for (int i = 0; i < count; i++)
            {
                int r = 6 + i * 12;
                int nameId = ReadUInt16(name, r + 6);
                if (Array.IndexOf(FamilyIds, nameId) >= 0) continue;
                int length = ReadUInt16(name, r + 8), offset = ReadUInt16(name, r + 10);
                var bytes = new byte[length];
                Array.Copy(name, storage + offset, bytes, 0, length);
                kept.Add(Tuple.Create(ReadUInt16(name, r), ReadUInt16(name, r + 2), ReadUInt16(name, r + 4), nameId, bytes));
            }
            foreach (var rec in OurRecords(family)) kept.Add(rec);
            return BuildNameTable(kept);
        }

        private static byte[] BuildName(string family) => BuildNameTable(new List<Tuple<int, int, int, int, byte[]>>(OurRecords(family)));

        private static IEnumerable<Tuple<int, int, int, int, byte[]>> OurRecords(string family)
        {
            var be = Encoding.BigEndianUnicode;
            string ps = family.Replace(" ", "");
            yield return Tuple.Create(3, 1, 0x409, 1, be.GetBytes(family));
            yield return Tuple.Create(3, 1, 0x409, 3, be.GetBytes(ps + "-UGT"));
            yield return Tuple.Create(3, 1, 0x409, 4, be.GetBytes(family));
            yield return Tuple.Create(3, 1, 0x409, 6, be.GetBytes(ps));
        }

        private static byte[] BuildNameTable(List<Tuple<int, int, int, int, byte[]>> records)
        {
            records.Sort((a, b) =>
            {
                int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
                c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
                c = a.Item3.CompareTo(b.Item3); if (c != 0) return c;
                return a.Item4.CompareTo(b.Item4);
            });
            var ms = new MemoryStream();
            WriteUInt16(ms, 0);
            WriteUInt16(ms, records.Count);
            WriteUInt16(ms, 6 + records.Count * 12);
            int offset = 0;
            foreach (var r in records)
            {
                WriteUInt16(ms, r.Item1); WriteUInt16(ms, r.Item2); WriteUInt16(ms, r.Item3); WriteUInt16(ms, r.Item4);
                WriteUInt16(ms, r.Item5.Length); WriteUInt16(ms, offset);
                offset += r.Item5.Length;
            }
            foreach (var r in records) ms.Write(r.Item5, 0, r.Item5.Length);
            return ms.ToArray();
        }

        /// <summary>OS/2 names the first and last BMP character: the private codepoints may widen it.</summary>
        private static byte[] WidenCharRange(byte[] os2, SortedDictionary<int, int> cmap)
        {
            if (os2.Length < 68) return os2;
            int first = 0xFFFF, last = 0;
            foreach (int cp in cmap.Keys)
            {
                if (cp > 0xFFFF) continue;
                first = Math.Min(first, cp); last = Math.Max(last, cp);
            }
            WriteUInt16At(os2, 64, first);
            WriteUInt16At(os2, 66, last);
            return os2;
        }

        // ─────────────────────────────── the file ───────────────────────────────

        private struct Entry { public uint Offset, Length; }

        private static Dictionary<string, Entry> ReadDirectory(byte[] f)
        {
            uint sfnt = ReadUInt32(f, 0);
            if (sfnt == 0x74746366) throw new InvalidDataException("a font collection (.ttc) is not a single font");
            int numTables = ReadUInt16(f, 4);
            var d = new Dictionary<string, Entry>(StringComparer.Ordinal);
            for (int i = 0; i < numTables; i++)
            {
                int r = 12 + i * 16;
                string tag = Encoding.ASCII.GetString(f, r, 4);
                d[tag] = new Entry { Offset = ReadUInt32(f, r + 8), Length = ReadUInt32(f, r + 12) };
            }
            return d;
        }

        private static byte[] Slice(byte[] f, Entry e)
        {
            var b = new byte[e.Length];
            Array.Copy(f, e.Offset, b, 0, e.Length);
            return b;
        }

        /// <summary>The table directory, the tables 4-aligned, every checksum, and head's adjustment.</summary>
        private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
        {
            int n = tables.Count;
            int entrySelector = (int)Math.Floor(Math.Log(n, 2));
            int searchRange = (1 << entrySelector) * 16;
            var ms = new MemoryStream();
            WriteUInt32(ms, 0x00010000);
            WriteUInt16(ms, n); WriteUInt16(ms, searchRange); WriteUInt16(ms, entrySelector); WriteUInt16(ms, n * 16 - searchRange);
            uint offset = (uint)(12 + n * 16);
            var placed = new List<KeyValuePair<string, uint>>();
            foreach (var t in tables)
            {
                ms.Write(Encoding.ASCII.GetBytes(t.Key), 0, 4);
                WriteUInt32(ms, Checksum(t.Value));
                WriteUInt32(ms, offset);
                WriteUInt32(ms, (uint)t.Value.Length);
                placed.Add(new KeyValuePair<string, uint>(t.Key, offset));
                offset += (uint)((t.Value.Length + 3) & ~3);
            }
            foreach (var t in tables)
            {
                ms.Write(t.Value, 0, t.Value.Length);
                while (ms.Length % 4 != 0) ms.WriteByte(0);
            }
            byte[] file = ms.ToArray();
            uint headOffset = 0;
            foreach (var p in placed) if (p.Key == "head") headOffset = p.Value;
            uint adjustment = unchecked(0xB1B0AFBA - Checksum(file));
            WriteUInt32At(file, (int)headOffset + 8, adjustment);
            return file;
        }

        private static uint Checksum(byte[] b)
        {
            uint sum = 0;
            for (int i = 0; i < b.Length; i += 4)
            {
                uint word = 0;
                for (int k = 0; k < 4; k++) word = (word << 8) | (uint)(i + k < b.Length ? b[i + k] : 0);
                sum = unchecked(sum + word);
            }
            return sum;
        }

        // ─────────────────────────────── big-endian ───────────────────────────────

        private static int ReadUInt16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        private static int ReadInt16(byte[] b, int o) => (short)((b[o] << 8) | b[o + 1]);
        private static uint ReadUInt32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        private static void WriteUInt16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void WriteInt16(Stream s, int v) => WriteUInt16(s, v & 0xFFFF);
        private static void WriteUInt32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void WriteUInt16At(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        private static void WriteUInt32At(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    }
}
