using System.Collections.Generic;
using UnityGameTranslator.Core.Rasterizer;
using L = UnityGameTranslator.Core.Rasterizer.OpenTypeLayout;
using U = UnityGameTranslator.Core.TextShaping.ShapingTables.Use;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// The Universal Shaping Engine — Microsoft's data-driven model for every script without a
    /// dedicated one (Tibetan, Javanese, Balinese, Tai Tham, Cham, Mongolian, N'Ko…), as
    /// HarfBuzz realises it (hb-ot-shaper-use.cc): clusters cut by the USE grammar over
    /// categories derived from Unicode's own properties, the repha and pre-base pieces
    /// reordered, the feature groups of the specification in order, joining forms for the
    /// cursive scripts, positioning with mark advances zeroed first.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class UseShaper
    {
        private const int ViramaTerminated = 0, SakotTerminated = 1, Standard = 2, NumberJoinerTerminated = 3, Numeral = 4,
            Symbol = 5, Hieroglyph = 6, Broken = 7, NonCluster = 8;

        // One letter per USE category value (0..56); unused values are '?'.
        private static readonly string Letters = BuildLetters();
        private static string BuildLetters()
        {
            var a = new char[64];
            for (int i = 0; i < a.Length; i++) a[i] = '?';
            a[U.O] = 'O'; a[U.B] = 'B'; a[U.N] = 'N'; a[U.GB] = 'G'; a[U.CGJ] = 'C'; a[U.SUB] = 'S'; a[U.H] = 'H'; a[U.HN] = 'n';
            a[U.ZWNJ] = 'Z'; a[U.WJ] = 'W'; a[U.R] = 'R'; a[U.CS] = 'c'; a[U.IS] = 'I'; a[U.Sk] = 'k'; a[U.G] = 'g'; a[U.J] = 'j';
            a[U.SB] = 'b'; a[U.SE] = 'e'; a[U.HVM] = 'h'; a[U.HM] = 'm'; a[U.HR] = 'r'; a[U.RK] = 'K';
            a[U.FAbv] = 'A'; a[U.FBlw] = 'D'; a[U.FPst] = 'E'; a[U.MAbv] = 'F'; a[U.MBlw] = 'J'; a[U.MPst] = 'L'; a[U.MPre] = 'M';
            a[U.CMAbv] = 'P'; a[U.CMBlw] = 'Q'; a[U.VAbv] = 'T'; a[U.VBlw] = 'U'; a[U.VPst] = 'V'; a[U.VPre] = 'X';
            a[U.VMAbv] = 'a'; a[U.VMBlw] = 'd'; a[U.VMPst] = 'f'; a[U.VMPre] = 'i'; a[U.SMAbv] = 'l'; a[U.SMBlw] = 'o';
            a[U.FMAbv] = 'p'; a[U.FMBlw] = 'q'; a[U.FMPst] = 's';
            return new string(a);
        }
        private static char Letter(int category) => category < Letters.Length ? Letters[category] : '?';

        // hb-ot-shaper-use-machine.rl over those letters.
        private const string Hh = "[HhIk]";
        private const string ConsonantModifiers = "(?:P*Q*(?:(?:" + Hh + "B|S)P*Q*)*)";
        private const string MedialConsonants = "(?:M?F?J?L?)";
        private const string DependentVowels = "(?:X*T*U*V*|H)";
        private const string VowelModifiers = "(?:h?i*a*d*f*)";
        private const string FinalConsonants = "(?:A*D*E*)";
        private const string FinalModifiers = "(?:p*q*|s?)";
        private const string ComplexStart = "(?:[Rc]?[BG])";
        private const string ComplexMiddle = "(?:" + ConsonantModifiers + MedialConsonants + DependentVowels + VowelModifiers + "(?:kB)*)";
        private const string ComplexTail = "(?:" + ComplexMiddle + FinalConsonants + FinalModifiers + ")";
        private const string NumberJoinerTail = "(?:(?:nN)*n)";
        private const string NumeralTail = "(?:(?:nN)+)";
        private const string SymbolTail = "(?:l+o*|o+)";
        private const string ViramaTail = "(?:" + ConsonantModifiers + "[IK])";
        private const string SakotTail = "(?:" + ComplexMiddle + "k)";
        private const string Tail = "(?:" + ComplexTail + "|" + SakotTail + "|" + SymbolTail + "|" + ViramaTail + ")";

        private static readonly ShapingCommon.SyllableRule[] Rules =
        {
            new ShapingCommon.SyllableRule(ViramaTerminated, ComplexStart + ViramaTail + "Z?"),
            new ShapingCommon.SyllableRule(SakotTerminated, ComplexStart + SakotTail + "Z?"),
            new ShapingCommon.SyllableRule(Standard, ComplexStart + ComplexTail + "Z?"),
            new ShapingCommon.SyllableRule(NumberJoinerTerminated, "N" + NumberJoinerTail + "Z?"),
            new ShapingCommon.SyllableRule(Numeral, "N" + NumeralTail + "?Z?"),
            new ShapingCommon.SyllableRule(Symbol, "[OGb]" + Tail + "?Z?"),
            new ShapingCommon.SyllableRule(Hieroglyph, "b*gr?m?e*(?:jb*(?:gr?m?e*)?)*Z?"),
            new ShapingCommon.SyllableRule(NonCluster, "s"),
            new ShapingCommon.SyllableRule(Broken, "R?(?:" + Tail + "|" + NumberJoinerTail + "|" + NumeralTail + ")Z?"),
        };

        private const uint MaskRphf = 1u << 0, MaskIsol = 1u << 1, MaskInit = 1u << 2, MaskMedi = 1u << 3, MaskFina = 1u << 4, MaskGlobal = 1u << 7;
        private static readonly string[] Pre = { "locl", "ccmp", "nukt", "akhn" };
        private static readonly string[] Rphf = { "rphf" };
        private static readonly string[] Pref = { "pref" };
        private static readonly string[] Basic = { "rkrf", "abvf", "blwf", "half", "pstf", "vatu", "cjct" };
        private static readonly string[] Topographical = { "isol", "init", "medi", "fina" };
        private static readonly string[] Other = { "abvs", "blws", "haln", "pres", "psts" };
        private static readonly string[] Gpos = { "abvm", "blwm", "mark", "mkmk", "dist", "kern" };

        private static uint MaskOf(string feature)
        {
            switch (feature)
            {
                case "rphf": return MaskRphf;
                case "isol": return MaskIsol;
                case "init": return MaskInit;
                case "medi": return MaskMedi;
                case "fina": return MaskFina;
                default: return uint.MaxValue;
            }
        }

        private sealed class Plan { public Dictionary<string, int[]> Gsub, Gpos; public bool Joins; }
        private static readonly Dictionary<L, Dictionary<string, Plan>> _plans = new Dictionary<L, Dictionary<string, Plan>>();

        private static Plan PlanFor(IShapingFont font, string tag)
        {
            var layout = font.Layout;
            if (!_plans.TryGetValue(layout, out var byScript)) _plans[layout] = byScript = new Dictionary<string, Plan>();
            if (byScript.TryGetValue(tag, out var plan)) return plan;
            var tags = new[] { tag, "DFLT" };
            plan = new Plan { Gsub = layout.Gsub?.CollectFeatures(tags, null, out _), Gpos = layout.Gpos?.CollectFeatures(tags, null, out _) };
            byScript[tag] = plan;
            return plan;
        }

        /// <summary>OpenType script tags of the USE scripts this shaper meets (BMP).</summary>
        internal static string ScriptTag(int script)
        {
            if (script == ShapingTables.Script.Tibetan) return "tibt";
            if (script == ShapingTables.Script.Mongolian) return "mong";
            if (script == ShapingTables.Script.Javanese) return "java";
            if (script == ShapingTables.Script.Balinese) return "bali";
            if (script == ShapingTables.Script.Sundanese) return "sund";
            if (script == ShapingTables.Script.Tai_Tham) return "lana";
            if (script == ShapingTables.Script.Tai_Viet) return "tavt";
            if (script == ShapingTables.Script.Cham) return "cham";
            if (script == ShapingTables.Script.Meetei_Mayek) return "mtei";
            if (script == ShapingTables.Script.Buginese) return "bugi";
            if (script == ShapingTables.Script.Batak) return "batk";
            if (script == ShapingTables.Script.Lepcha) return "lepc";
            if (script == ShapingTables.Script.Limbu) return "limb";
            if (script == ShapingTables.Script.Tai_Le) return "tale";
            if (script == ShapingTables.Script.Syloti_Nagri) return "sylo";
            if (script == ShapingTables.Script.Phags_Pa) return "phag";
            if (script == ShapingTables.Script.Saurashtra) return "saur";
            if (script == ShapingTables.Script.Kayah_Li) return "kali";
            if (script == ShapingTables.Script.Rejang) return "rjng";
            if (script == ShapingTables.Script.Nko) return "nko ";
            if (script == ShapingTables.Script.Tifinagh) return "tfng";
            if (script == ShapingTables.Script.Mandaic) return "mand";
            if (script == ShapingTables.Script.Tagalog) return "tglg";
            if (script == ShapingTables.Script.Hanunoo) return "hano";
            if (script == ShapingTables.Script.Buhid) return "buhd";
            if (script == ShapingTables.Script.Tagbanwa) return "tagb";
            return "DFLT";
        }

        internal static void Shape(List<int> cps, List<int> clusters, int script, IShapingFont font, List<ShapedGlyph> result)
        {
            var plan = PlanFor(font, ScriptTag(script));
            ShapingCommon.Normalize(cps, clusters, font, ShapingCommon.NormalizationMode.DecomposedThenComposedDiacritics);

            bool rtl = ShapingCommon.IsRightToLeft(script);
            var buf = new L.GlyphBuffer();
            var joining = new int[cps.Count];
            bool anyJoining = false;
            for (int i = 0; i < cps.Count; i++)
            {
                var g = ShapingCommon.MakeGlyph(cps[i], clusters[i], font);
                g.Category = ShapingCommon.UseCategory(cps[i]);
                g.Mask = MaskGlobal;
                g.Hidden = ShapingCommon.IsDefaultIgnorable(cps[i]);
                buf.Glyphs.Add(g);
                joining[i] = ShapingCommon.JoiningType(cps[i]);
                anyJoining |= joining[i] != ShapingCommon.JoinU && joining[i] != ShapingCommon.JoinT;
            }
            var layout = font.Layout;
            var gsub = layout.Gsub;
            var syllables = ShapingCommon.FindSyllables(ShapingCommon.Categories(buf, Letter), Rules, NonCluster, buf);

            // The repha mask: the first glyph (a logical repha) or the first three of every cluster.
            foreach (var syl in syllables)
            {
                int limit = buf[syl.Start].Category == U.R ? 1 : System.Math.Min(3, syl.End - syl.Start);
                for (int i = syl.Start; i < syl.Start + limit; i++) buf[i].Mask |= MaskRphf;
            }
            // Joining forms for the cursive scripts, per letter.
            if (anyJoining)
            {
                var forms = ShapingCommon.JoiningForms(joining);
                for (int i = 0; i < buf.Count; i++)
                {
                    switch (forms[i])
                    {
                        case ShapingCommon.FormIsol: buf[i].Mask |= MaskIsol; break;
                        case ShapingCommon.FormInit: buf[i].Mask |= MaskInit; break;
                        case ShapingCommon.FormMedi: buf[i].Mask |= MaskMedi; break;
                        case ShapingCommon.FormFina: buf[i].Mask |= MaskFina; break;
                    }
                }
            }

            if (gsub != null && plan.Gsub != null)
            {
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Pre, null);
                ClearSubstituted(buf);
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Rphf, MaskOf);
                ShapingCommon.RefreshSyllables(buf, syllables);
                foreach (var syl in syllables)
                    for (int i = syl.Start; i < syl.End && (buf[i].Mask & MaskRphf) != 0; i++)
                        if (buf[i].Substituted) { buf[i].Category = U.R; break; }
                ClearSubstituted(buf);
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Pref, null);
                ShapingCommon.RefreshSyllables(buf, syllables);
                foreach (var syl in syllables)
                    for (int i = syl.Start; i < syl.End; i++)
                        if (buf[i].Substituted) { buf[i].Category = U.VPre; break; }
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Basic, null);
                ShapingCommon.RefreshSyllables(buf, syllables);
            }

            if (ShapingCommon.InsertDottedCircles(buf, syllables, Broken, font, U.B, 0, U.R))
                ShapingCommon.RefreshSyllables(buf, syllables);
            foreach (var syl in syllables)
                if (syl.Type == ViramaTerminated || syl.Type == SakotTerminated || syl.Type == Standard || syl.Type == Symbol || syl.Type == Broken)
                    ReorderSyllable(buf, syl.Start, syl.End);
            ShapingCommon.ReleaseSyllables(buf);

            if (gsub != null && plan.Gsub != null)
            {
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Topographical, MaskOf);
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, Other, null);
                // The common features last, on the whole run.
                ShapingCommon.ApplyStage(layout, gsub, buf, plan.Gsub, ShapingCommon.CommonGsubFeatures, null);
            }

            ShapingCommon.ResetAdvances(buf, font, zeroMarks: true);
            ShapingCommon.Position(font, buf, plan.Gpos, Gpos, rtl);
            ShapingCommon.Emit(buf, result);
        }

        private static void ClearSubstituted(L.GlyphBuffer buf)
        {
            for (int i = 0; i < buf.Count; i++) { buf[i].Substituted = false; buf[i].Ligated = false; buf[i].Multiplied = false; }
        }

        private static bool IsHalant(L.ShapedGlyph g)
        {
            return (g.Category == U.H || g.Category == U.HVM || g.Category == U.IS) && !g.Ligated;
        }

        private static bool IsPostBase(int category)
        {
            switch (category)
            {
                case U.FBlw: case U.FPst: case U.FMAbv: case U.FMBlw: case U.FMPst: case U.MAbv: case U.MBlw: case U.MPst: case U.MPre:
                case U.VAbv: case U.VBlw: case U.VPst: case U.VPre: case U.VMAbv: case U.VMBlw: case U.VMPst: case U.VMPre:
                    return true;
                default: return false;
            }
        }

        /// <summary>hb-ot-shaper-use.cc reorder_syllable_use.</summary>
        private static void ReorderSyllable(L.GlyphBuffer buf, int start, int end)
        {
            // A repha moves towards the end, before the first post-base glyph.
            if (buf[start].Category == U.R && end - start > 1)
            {
                for (int i = start + 1; i < end; i++)
                {
                    bool postBase = IsPostBase(buf[i].Category) || IsHalant(buf[i]);
                    if (postBase || i == end - 1)
                    {
                        if (postBase) i--;
                        ShapingCommon.Move(buf, start, i);
                        break;
                    }
                }
            }
            // Pre-base pieces move back: to just after the last halant, else to the start.
            int j = start;
            for (int i = start; i < end; i++)
            {
                var g = buf[i];
                if (IsHalant(g)) { j = i + 1; }
                else if ((g.Category == U.VPre || g.Category == U.VMPre) && g.MultipleIndex == 0 && j < i)
                {
                    ShapingCommon.Move(buf, i, j);
                }
            }
        }
    }
}
