using System;
using System.IO;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The OpenType layout reader against a real font: Noto Sans Devanagari (OFL, in
    /// TestData/Fonts). Every expected number below was read off the SAME file by fontTools on
    /// 2026-09-03 — glyph ids, lookup indices, anchor coordinates — never by the code under
    /// check. What is checked is the whole chain: tables parsed, features collected for the
    /// dev2 script, lookups applied on a glyph buffer, marks attached and resolved.
    /// </summary>
    internal static class OpenTypeLayoutChecks
    {
        // Glyph ids (fontTools getGlyphOrder / getBestCmap).
        private const int Ka = 56, Virama = 103, Ssa = 86, Ra = 82, Rra = 230, IMatra = 32, Anusvara = 100;
        private const int KaViramaSsa = 90;   // uni0915094D0937 — the akhand conjunct क्ष
        private const int RaVirama = 503;     // uni0930094D — the reph
        private const int KaHalf = 232;       // uni0915094D — half form
        private const int IMatraForRa = 539;  // uni093F.01 — the i-matra chosen before र
        private const int IMatraForKa = 542;  // uni093F.04 — the one chosen before क

        public static void Run(Action<bool, string, string> check)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TestData", "Fonts", "NotoSansDevanagari.ttf");
            if (!File.Exists(path))
            {
                check(false, "check font present", path);
                return;
            }
            var parser = new TtfParser(File.ReadAllBytes(path));
            var layout = parser.Layout;

            // ── tables ──
            check(parser.GlyphCount == 1090, "glyph count", parser.GlyphCount.ToString());
            check(parser.GetGlyphIndex(0x0915) == Ka && parser.GetGlyphIndex(0x094D) == Virama && parser.GetGlyphIndex(0x0937) == Ssa,
                "cmap: क ् ष", $"{parser.GetGlyphIndex(0x0915)} {parser.GetGlyphIndex(0x094D)} {parser.GetGlyphIndex(0x0937)}");
            check(parser.GetAdvanceWidth(Ka) == 768, "hmtx: advance of क", parser.GetAdvanceWidth(Ka).ToString());
            check(layout.Gsub != null && layout.Gsub.Lookups.Length == 224, "GSUB: 224 lookups", (layout.Gsub?.Lookups.Length ?? 0).ToString());
            check(layout.Gpos != null && layout.Gpos.Lookups.Length == 33, "GPOS: 33 lookups", (layout.Gpos?.Lookups.Length ?? 0).ToString());
            check(layout.HasGlyphClasses && layout.GlyphClass(Ka) == OpenTypeLayout.ClassBase && layout.GlyphClass(Anusvara) == OpenTypeLayout.ClassMark,
                "GDEF: क is a base, ं a mark", $"{layout.GlyphClass(Ka)} {layout.GlyphClass(Anusvara)}");

            // ── features of the dev2 script ──
            var gsub = layout.Gsub.CollectFeatures(new[] { "dev2", "deva", "DFLT" }, null, out string script);
            check(script == "dev2" && gsub != null, "dev2 script found in GSUB", script ?? "none");
            if (gsub == null) return;
            check(gsub.TryGetValue("akhn", out var akhn) && akhn.Length == 2 && akhn[0] == 102 && akhn[1] == 103,
                "akhn → lookups 102, 103", akhn == null ? "missing" : string.Join(",", akhn));
            check(gsub.TryGetValue("rphf", out var rphf) && rphf.Length == 1 && rphf[0] == 104, "rphf → lookup 104", rphf == null ? "missing" : string.Join(",", rphf));
            check(gsub.TryGetValue("half", out var half) && half.Length == 4 && half[0] == 114, "half → 4 lookups from 114", half == null ? "missing" : string.Join(",", half));
            check(gsub.TryGetValue("pres", out var pres) && Array.IndexOf(pres, 46) >= 0, "pres holds lookup 46", pres == null ? "missing" : string.Join(",", pres));
            var gpos = layout.Gpos.CollectFeatures(new[] { "dev2", "deva", "DFLT" }, null, out _);
            int[] abvm = null;
            bool abvmOk = gpos != null && gpos.TryGetValue("abvm", out abvm) && abvm.Length == 8 && abvm[0] == 11 && abvm[7] == 18;
            check(abvmOk, "abvm → lookups 11–18", abvm == null ? "missing" : string.Join(",", abvm));
            check(gpos != null && gpos.ContainsKey("blwm") && gpos.ContainsKey("mark") && gpos.ContainsKey("mkmk") && gpos.ContainsKey("dist"),
                "GPOS dev2 has blwm, mark, mkmk, dist", "");

            // ── ligature: क + ् + ष → क्ष through akhn ──
            var buf = Buffer(Ka, Virama, Ssa);
            bool applied = layout.ApplyLookup(layout.Gsub, 102, buf);
            check(applied && buf.Count == 1 && buf[0].Glyph == KaViramaSsa && buf[0].Cluster == 0,
                "akhn ligates क्ष into glyph 90", Describe(buf));

            // ── reph: र + ् → glyph 503 through rphf ──
            buf = Buffer(Ra, Virama);
            layout.ApplyLookup(layout.Gsub, 104, buf);
            check(buf.Count == 1 && buf[0].Glyph == RaVirama, "rphf ligates र् into the reph (503)", Describe(buf));

            // ── half form: क + ् → 232 through half; and the same lookup leaves क alone ──
            buf = Buffer(Ka, Virama);
            layout.ApplyLookup(layout.Gsub, 114, buf);
            check(buf.Count == 1 && buf[0].Glyph == KaHalf, "half ligates क् into the half form (232)", Describe(buf));
            buf = Buffer(Ka, Ka);
            check(!layout.ApplyLookup(layout.Gsub, 114, buf) && buf.Count == 2, "half does nothing without a virama", Describe(buf));

            // ── chained context (format 3): ि before र becomes the wider variant ──
            buf = Buffer(IMatra, Ra);
            layout.ApplyLookup(layout.Gsub, 46, buf);
            check(buf.Count == 2 && buf[0].Glyph == IMatraForRa && buf[1].Glyph == Ra, "pres: ि before र → uni093F.01 (539)", Describe(buf));
            buf = Buffer(IMatra, Rra);
            layout.ApplyLookup(layout.Gsub, 46, buf);
            check(buf[0].Glyph == IMatraForRa, "…and before ऱ too (lookahead coverage)", Describe(buf));
            // 17 subtables in that lookup, one per consonant width; the first that matches wins —
            // before क that is subtable 3, whose nested lookup 50 picks uni093F.04.
            buf = Buffer(IMatra, Ka);
            layout.ApplyLookup(layout.Gsub, 46, buf);
            check(buf[0].Glyph == IMatraForKa, "…and before क the narrower uni093F.04 (542)", Describe(buf));

            // ── mark-to-base: ं on क ──
            buf = Buffer(Ka, Anusvara);
            bool attached = layout.ApplyLookup(layout.Gpos, 14, buf);
            check(attached && buf[1].AttachedTo == 0 && buf[1].AttachX == 547 && buf[1].AttachY == 0,
                "abvm lookup 14 anchors ं on क (377 − (−170), 622 − 622)", $"attached={buf[1].AttachedTo} dx={buf[1].AttachX} dy={buf[1].AttachY}");
            buf.ResolveAttachments();
            check(buf[1].XOffset == 547 - 768 && buf[1].YOffset == 0 && buf[1].AttachedTo == -1,
                "resolved: mark offset = anchor difference − base advance", $"x={buf[1].XOffset} y={buf[1].YOffset}");

            // ── the whole dev2 GSUB feature list applied in order on क्ष: still one glyph ──
            buf = Buffer(Ka, Virama, Ssa);
            foreach (string tag in new[] { "nukt", "akhn", "rphf", "blwf", "half", "pstf", "vatu", "cjct", "pres", "abvs", "blws", "psts", "haln" })
                if (gsub.TryGetValue(tag, out var lookups))
                    foreach (int li in lookups) layout.ApplyLookup(layout.Gsub, li, buf);
            check(buf.Count == 1 && buf[0].Glyph == KaViramaSsa, "all dev2 GSUB features on क्ष: one conjunct, unchanged after", Describe(buf));

            // ── a font-less layout answers empty, never throws ──
            var none = new OpenTypeLayout(new byte[16], (string tag, out uint o, out uint l) => { o = 0; l = 0; return false; });
            check(none.Gsub == null && none.Gpos == null && !none.HasGlyphClasses && none.GlyphClass(5) == 0,
                "no tables → empty layout", "");
        }

        private static OpenTypeLayout.GlyphBuffer Buffer(params int[] glyphs)
        {
            var buf = new OpenTypeLayout.GlyphBuffer();
            for (int i = 0; i < glyphs.Length; i++)
                buf.Glyphs.Add(new OpenTypeLayout.ShapedGlyph { Glyph = glyphs[i], Cluster = i, XAdvance = 768 });
            return buf;
        }

        private static string Describe(OpenTypeLayout.GlyphBuffer buf)
        {
            var parts = new string[buf.Count];
            for (int i = 0; i < buf.Count; i++) parts[i] = buf[i].Glyph + "@" + buf[i].Cluster;
            return "[" + string.Join(" ", parts) + "]";
        }
    }
}
