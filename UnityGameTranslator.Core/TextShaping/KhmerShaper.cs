using System.Collections.Generic;
using UnityGameTranslator.Core.Rasterizer;
using L = UnityGameTranslator.Core.Rasterizer.OpenTypeLayout;
using S = UnityGameTranslator.Core.TextShaping.ShapingTables.Syllabic;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Shaping for Khmer — Microsoft's khmr model as HarfBuzz realises it
    /// (hb-ot-shaper-khmer.cc): syllables by grammar, the coeng + ro pair moved before the base
    /// and marked for 'pref' (what follows it for 'cfar'), the left vowel piece moved to the
    /// front, the basic features in one stage inside the syllable, then the presentation ones.
    /// Split vowels without a Unicode decomposition are cut into their left piece and the sign.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class KhmerShaper
    {
        private const int ConsonantSyllable = 0, BrokenCluster = 1, NonKhmerCluster = 2;

        // The machine's alphabet (hb-ot-shaper-khmer-machine.rl): C=C V=V H=H ZWNJ=Z ZWJ=J PLACEHOLDER=P
        // DOTTEDCIRCLE=O Ra=r VAbv=a VBlw=b VPre=p VPst=q Robatic=R Xgroup=X Ygroup=Y.
        private static readonly char[] Letters = BuildLetters();
        private static char[] BuildLetters()
        {
            var a = new char[40];
            for (int i = 0; i < a.Length; i++) a[i] = 'x';
            a[S.C] = 'C'; a[S.V] = 'V'; a[S.H] = 'H'; a[S.ZWNJ] = 'Z'; a[S.ZWJ] = 'J'; a[S.PLACEHOLDER] = 'P'; a[S.DOTTEDCIRCLE] = 'O';
            a[S.Ra] = 'r'; a[S.VAbv] = 'a'; a[S.VBlw] = 'b'; a[S.VPre] = 'p'; a[S.VPst] = 'q'; a[S.Robatic] = 'R'; a[S.Xgroup] = 'X'; a[S.Ygroup] = 'Y';
            return a;
        }
        private static char Letter(int category) => category < Letters.Length ? Letters[category] : 'x';

        // hb-ot-shaper-khmer-machine.rl over those letters.
        private const string C = "[CrV]";
        private const string Cn = "(?:" + C + "(?:[JZ]?R)?)";
        private const string Joiner = "[JZ]";
        private const string XGroup = "(?:" + Joiner + "*X)*";
        private const string YGroup = "Y*";
        private const string MatraGroup = "(?:p?" + XGroup + "b?" + XGroup + "(?:" + Joiner + "?a)?" + XGroup + "q?)";
        private const string SyllableTail = "(?:" + XGroup + MatraGroup + XGroup + "(?:H" + C + ")?" + YGroup + ")";
        private const string Broken = "(?:R?(?:H" + Cn + ")*(?:H|" + SyllableTail + "))";
        private const string Consonant = "(?:" + Cn + "|P|O)" + Broken;

        private static readonly ShapingCommon.SyllableRule[] Rules =
        {
            new ShapingCommon.SyllableRule(ConsonantSyllable, Consonant),
            new ShapingCommon.SyllableRule(BrokenCluster, Broken),
        };

        private const uint MaskPref = 1u << 0, MaskBlwf = 1u << 1, MaskAbvf = 1u << 2, MaskPstf = 1u << 3, MaskCfar = 1u << 4, MaskGlobal = 1u << 7;
        private static readonly string[] CommonFeatures = { "rlig", "calt", "clig", "rclt" };
        private static readonly string[] Pre = { "locl", "ccmp" };
        private static readonly string[] Basic = { "pref", "blwf", "abvf", "pstf", "cfar" };
        private static readonly string[] Other = { "pres", "abvs", "blws", "psts" };
        private static readonly string[] Gpos = { "abvm", "blwm", "mark", "mkmk", "dist", "kern" };

        private static uint MaskOf(string feature)
        {
            switch (feature)
            {
                case "pref": return MaskPref;
                case "blwf": return MaskBlwf;
                case "abvf": return MaskAbvf;
                case "pstf": return MaskPstf;
                case "cfar": return MaskCfar;
                default: return uint.MaxValue;
            }
        }

        private sealed class Plan { public Dictionary<string, int[]> Gsub, Gpos; }
        private static readonly Dictionary<L, Plan> _plans = new Dictionary<L, Plan>();

        private static Plan PlanFor(IShapingFont font)
        {
            var layout = font.Layout;
            if (_plans.TryGetValue(layout, out var plan)) return plan;
            var tags = new[] { "khmr", "DFLT" };
            plan = new Plan { Gsub = layout.Gsub?.CollectFeatures(tags, null, out _), Gpos = layout.Gpos?.CollectFeatures(tags, null, out _) };
            _plans[layout] = plan;
            return plan;
        }

        private static bool DecomposeKhmer(int ab, out int a, out int b)
        {
            switch (ab)
            {
                case 0x17BE: case 0x17BF: case 0x17C0: case 0x17C4: case 0x17C5:
                    a = 0x17C1; b = ab; return true;
            }
            return ShapingCommon.TryDecompose(ab, out a, out b, out _);
        }

        internal static void Shape(List<int> cps, List<int> clusters, IShapingFont font, List<ShapedGlyph> result)
        {
            var plan = PlanFor(font);
            ShapingCommon.Normalize(cps, clusters, font, ShapingCommon.NormalizationMode.DecomposedThenComposedDiacritics, DecomposeKhmer);

            var buf = new L.GlyphBuffer();
            for (int i = 0; i < cps.Count; i++)
            {
                var g = ShapingCommon.MakeGlyph(cps[i], clusters[i], font);
                g.Category = ShapingCommon.MyanmarKhmerCategory(cps[i]);
                g.Mask = MaskGlobal;
                g.Hidden = ShapingCommon.IsDefaultIgnorable(cps[i]);
                buf.Glyphs.Add(g);
            }
            var layout = font.Layout;
            var gsub = layout.Gsub;
            var syllables = ShapingCommon.FindSyllables(ShapingCommon.Categories(buf, Letter), Rules, NonKhmerCluster, buf);

            if (gsub != null && plan.Gsub != null) ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Pre, null);
            ShapingCommon.RefreshSyllables(buf, syllables);

            if (ShapingCommon.InsertDottedCircles(buf, syllables, BrokenCluster, font, S.DOTTEDCIRCLE, 0, -1))
                ShapingCommon.RefreshSyllables(buf, syllables);
            foreach (var syl in syllables)
                if (syl.Type == ConsonantSyllable || syl.Type == BrokenCluster)
                    ReorderSyllable(buf, syl.Start, syl.End, plan);

            if (gsub != null && plan.Gsub != null)
            {
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Basic, MaskOf);
                ShapingCommon.ReleaseSyllables(buf);
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Other, null);
                // The common features last, on the whole run — 'clig' kept and 'liga' left out,
                // as the Khmer specification asks.
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, CommonFeatures, null);
            }
            else ShapingCommon.ReleaseSyllables(buf);

            ShapingCommon.ResetAdvances(buf, font, zeroMarks: false);
            ShapingCommon.Position(font, buf, plan.Gpos, Gpos);
            ShapingCommon.Emit(buf, result);
        }

        /// <summary>hb-ot-shaper-khmer.cc reorder_consonant_syllable.</summary>
        private static void ReorderSyllable(L.GlyphBuffer buf, int start, int end, Plan plan)
        {
            for (int i = start + 1; i < end; i++) buf[i].Mask |= MaskBlwf | MaskAbvf | MaskPstf;
            bool hasCfar = plan.Gsub != null && plan.Gsub.ContainsKey("cfar");
            int coengs = 0;
            for (int i = start + 1; i < end; i++)
            {
                if (buf[i].Category == S.H && coengs <= 2 && i + 1 < end)
                {
                    coengs++;
                    if (buf[i + 1].Category == S.Ra)
                    {
                        buf[i].Mask |= MaskPref;
                        buf[i + 1].Mask |= MaskPref;
                        // The coeng + ro pair to the start of the syllable.
                        var t0 = buf.Glyphs[i]; var t1 = buf.Glyphs[i + 1];
                        buf.Glyphs.RemoveAt(i + 1); buf.Glyphs.RemoveAt(i);
                        buf.Glyphs.Insert(start, t1); buf.Glyphs.Insert(start, t0);
                        if (hasCfar)
                            for (int j = i + 2; j < end; j++) buf[j].Mask |= MaskCfar;
                        coengs = 2;
                    }
                }
                else if (buf[i].Category == S.VPre)
                {
                    ShapingCommon.Move(buf, i, start);
                }
            }
        }
    }
}
