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
