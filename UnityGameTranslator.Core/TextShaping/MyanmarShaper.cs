using System.Collections.Generic;
using UnityGameTranslator.Core.Rasterizer;
using L = UnityGameTranslator.Core.Rasterizer.OpenTypeLayout;
using S = UnityGameTranslator.Core.TextShaping.ShapingTables.Syllabic;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Shaping for Myanmar (Burmese, Mon, Shan, Karen…) — Microsoft's mym2 model as HarfBuzz
    /// realises it (hb-ot-shaper-myanmar.cc): syllables cut by a grammar over the Indic-table
    /// categories, the kinzi (ra + asat + halant) and the medials, pre-base vowels and asat
    /// given canonical positions and stably sorted, then the basic features one stage each
    /// inside the syllable, the syllables released, the presentation features, positioning.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class MyanmarShaper
    {
        private const int PosPreC = 3, PosPreM = 2, PosBaseC = 4, PosAfterMain = 5, PosBeforeSub = 7, PosBelowC = 8, PosAfterSub = 9;
        private const int ConsonantSyllable = 0, BrokenCluster = 1, NonMyanmarCluster = 2;

        // One letter per category for the grammar (the machine's alphabet, hb-ot-shaper-myanmar-machine.rl):
        //   C=C IV=V DB=N H=H ZWNJ=Z ZWJ=J SM=S A=A GB=P DOTTEDCIRCLE=O Ra=r CS=c SMPst=T
        //   VAbv=a VBlw=b VPre=p VPst=q As=K MH=h MR=w MW=u MY=i PT=v VS=y ML=l
        private static readonly char[] Letters = BuildLetters();
        private static char[] BuildLetters()
        {
            var a = new char[40];
            for (int i = 0; i < a.Length; i++) a[i] = 'X';
            a[S.C] = 'C'; a[S.V] = 'V'; a[S.N] = 'N'; a[S.H] = 'H'; a[S.ZWNJ] = 'Z'; a[S.ZWJ] = 'J'; a[S.SM] = 'S'; a[S.A] = 'A';
            a[S.PLACEHOLDER] = 'P'; a[S.DOTTEDCIRCLE] = 'O'; a[S.Ra] = 'r'; a[S.CS] = 'c'; a[S.SMPst] = 'T';
            a[S.VAbv] = 'a'; a[S.VBlw] = 'b'; a[S.VPre] = 'p'; a[S.VPst] = 'q'; a[S.As] = 'K'; a[S.MH] = 'h'; a[S.MR] = 'w';
            a[S.MW] = 'u'; a[S.MY] = 'i'; a[S.PT] = 'v'; a[S.VS] = 'y'; a[S.ML] = 'l';
            return a;
        }
        private static char Letter(int category) => category < Letters.Length ? Letters[category] : 'X';

        private const string Jn = "[JZ]";
        private const string Kinzi = "(?:rKH)";
        private const string Sm = "[ST]";
        private const string C = "[Cr]";
        private const string Medial = "(?:i?K?w?(?:(?:uh?l?|hl?|l)K?)?)";
        private const string MainVowel = "(?:(?:py?)*a*b*A*(?:NK?)?)";
        private const string PostVowel = "(?:qh?l?K*a*A*(?:NK?)?)";
        private const string Tone = "(?:" + Sm + "|vA*N?K?)";
        private const string ComplexTail = "(?:K*" + Medial + MainVowel + PostVowel + "*" + Tone + "*" + Jn + "?)";
        private const string SyllableTail = "(?:(?:H(?:" + C + "|V)y?)*(?:H|" + ComplexTail + "))";
        private const string Consonant = "(?:" + Kinzi + "|c)?(?:" + C + "|V|P|O)y?" + SyllableTail;
        private const string Broken = Kinzi + "?y?" + SyllableTail;

        private static readonly ShapingCommon.SyllableRule[] Rules =
        {
            new ShapingCommon.SyllableRule(ConsonantSyllable, Consonant),
            new ShapingCommon.SyllableRule(NonMyanmarCluster, "(?:" + Jn + "|T)"),
            new ShapingCommon.SyllableRule(BrokenCluster, Broken),
        };

        private static readonly string[] Pre = { "locl", "ccmp" };
        private static readonly string[][] BasicStages = { new[] { "rphf" }, new[] { "pref" }, new[] { "blwf" }, new[] { "pstf" } };
        private static readonly string[] Other = { "pres", "abvs", "blws", "psts" };
        private static readonly string[] Gpos = { "abvm", "blwm", "mark", "mkmk", "dist", "kern" };

        private sealed class Plan { public Dictionary<string, int[]> Gsub, Gpos; }
        private static readonly Dictionary<L, Plan> _plans = new Dictionary<L, Plan>();

        private static Plan PlanFor(IShapingFont font)
        {
            var layout = font.Layout;
            if (_plans.TryGetValue(layout, out var plan)) return plan;
            var tags = new[] { "mym2", "mymr", "DFLT" };
            plan = new Plan { Gsub = layout.Gsub?.CollectFeatures(tags, null, out _), Gpos = layout.Gpos?.CollectFeatures(tags, null, out _) };
            _plans[layout] = plan;
            return plan;
        }


        internal static void Shape(List<int> cps, List<int> clusters, IShapingFont font, List<ShapedGlyph> result)
        {
            var plan = PlanFor(font);
            ShapingCommon.Normalize(cps, clusters, font, ShapingCommon.NormalizationMode.DecomposedThenComposedDiacritics);

            var buf = new L.GlyphBuffer();
            for (int i = 0; i < cps.Count; i++)
            {
                var g = ShapingCommon.MakeGlyph(cps[i], clusters[i], font);
                g.Category = ShapingCommon.MyanmarKhmerCategory(cps[i]);
                g.Mask = uint.MaxValue;
                g.Hidden = ShapingCommon.IsDefaultIgnorable(cps[i]);
                buf.Glyphs.Add(g);
            }
            var layout = font.Layout;
            var gsub = layout.Gsub;
            var syllables = ShapingCommon.FindSyllables(ShapingCommon.Categories(buf, Letter), Rules, NonMyanmarCluster, buf);

            if (gsub != null && plan.Gsub != null) ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Pre, null);
            ShapingCommon.RefreshSyllables(buf, syllables);

            // Reorder — dotted circles first, then every consonant or broken syllable.
            if (ShapingCommon.InsertDottedCircles(buf, syllables, BrokenCluster, font, S.DOTTEDCIRCLE, PosBaseC, -1))
                ShapingCommon.RefreshSyllables(buf, syllables);
            foreach (var syl in syllables)
                if (syl.Type == ConsonantSyllable || syl.Type == BrokenCluster)
                    ReorderSyllable(buf, syl.Start, syl.End);

            if (gsub != null && plan.Gsub != null)
            {
                foreach (var stage in BasicStages) ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, stage, null);
                ShapingCommon.ReleaseSyllables(buf);
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Other, null);
                // The common features last, on the whole run.
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, ShapingCommon.CommonGsubFeatures, null);
            }
            else ShapingCommon.ReleaseSyllables(buf);

            // Marks zeroed BEFORE positioning (HarfBuzz's "by GDEF, early").
            ShapingCommon.ResetAdvances(buf, font, zeroMarks: true);
            ShapingCommon.Position(font, buf, plan.Gpos, Gpos);
            ShapingCommon.Emit(buf, result);
        }

        private static bool IsConsonant(L.ShapedGlyph g)
        {
            if (g.Ligated) return false;
            int c = g.Category;
            return c == S.C || c == S.CS || c == S.Ra || c == S.V || c == S.PLACEHOLDER || c == S.DOTTEDCIRCLE;
        }

        /// <summary>hb-ot-shaper-myanmar.cc initial_reordering_consonant_syllable, verbatim in spirit.</summary>
        private static void ReorderSyllable(L.GlyphBuffer buf, int start, int end)
        {
            int b = end;
            bool hasReph = false;
            int limit = start;
            if (start + 3 <= end && buf[start].Category == S.Ra && buf[start + 1].Category == S.As && buf[start + 2].Category == S.H)
            {
                limit += 3;
                b = start;
                hasReph = true;
            }
            if (!hasReph) b = limit;
            for (int i = limit; i < end; i++)
                if (IsConsonant(buf[i])) { b = i; break; }

            int k = start;
            for (; k < start + (hasReph ? 3 : 0); k++) buf[k].Position = PosAfterMain;
            for (; k < b; k++) buf[k].Position = PosPreC;
            if (k < end) { buf[k].Position = PosBaseC; k++; }
            int pos = PosAfterMain;
            for (; k < end; k++)
            {
                var g = buf[k];
                if (g.Category == S.MR) { g.Position = PosPreC; continue; }
                if (g.Category == S.VPre) { g.Position = PosPreM; continue; }
                if (g.Category == S.VS) { g.Position = buf[k - 1].Position; continue; }
                if (pos == PosAfterMain && g.Category == S.VBlw) { pos = PosBelowC; g.Position = pos; continue; }
                if (pos == PosBelowC && g.Category == S.A) { g.Position = PosBeforeSub; continue; }
                if (pos == PosBelowC && g.Category == S.VBlw) { g.Position = pos; continue; }
                if (pos == PosBelowC && g.Category != S.A) { pos = PosAfterSub; g.Position = pos; continue; }
                g.Position = pos;
            }
            ShapingCommon.SortByPosition(buf, start, end);

            // A sequence of left matras is flipped, then each matra's own selectors put back.
            int first = end, last = end;
            for (int i = start; i < end; i++)
                if (buf[i].Position == PosPreM) { if (first == end) first = i; last = i; }
            if (first < last)
            {
                buf.Glyphs.Reverse(first, last - first + 1);
                int i0 = first;
                for (int j = i0; j <= last; j++)
                    if (buf[j].Category == S.VPre)
                    {
                        buf.Glyphs.Reverse(i0, j - i0 + 1);
                        i0 = j + 1;
                    }
            }
        }
    }
}
