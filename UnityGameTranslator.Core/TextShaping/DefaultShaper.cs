using System.Collections.Generic;
using UnityGameTranslator.Core.Rasterizer;
using L = UnityGameTranslator.Core.Rasterizer.OpenTypeLayout;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Shaping for the scripts with no syllable model — Thai, Lao, Hebrew, and any run whose
    /// letters carry combining marks the font positions by anchor: normalization, the common
    /// substitution features, then positioning with the marks' advances zeroed afterwards.
    /// HarfBuzz's default shaper, with its Thai and Hebrew particulars:
    ///   • Thai and Lao: SARA AM is cut into NIKHAHIT + SARA AA and the nikhahit moved before
    ///     the above-base marks that precede it — what Uniscribe and every engine do, whether
    ///     or not the font has Thai tables;
    ///   • Hebrew: a patah/qamats followed by sheva/hiriq then meteg has its last two marks
    ///     swapped (the order the fonts expect).
    /// The mark reordering by combining class (Thai's below vowels before its tone marks, the
    /// Hebrew and Arabic point orders) lives in the generated classes, not here.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class DefaultShaper
    {
        // HarfBuzz's common and horizontal features (hb-ot-shape.cc), substitution then positioning.
        private static readonly string[] GsubFeatures = { "ccmp", "locl", "rlig", "calt", "clig", "liga", "rclt" };
        private static readonly string[] GposFeatures = { "abvm", "blwm", "mark", "mkmk", "dist", "kern" };

        private sealed class Plan
        {
            public Dictionary<string, int[]> Gsub, Gpos;
        }

        private static readonly Dictionary<L, Dictionary<string, Plan>> _plans = new Dictionary<L, Dictionary<string, Plan>>();

        private static Plan PlanFor(IShapingFont font, string scriptTag)
        {
            var layout = font.Layout;
            if (!_plans.TryGetValue(layout, out var byScript)) _plans[layout] = byScript = new Dictionary<string, Plan>();
            if (byScript.TryGetValue(scriptTag, out var plan)) return plan;
            var tags = new[] { scriptTag, "DFLT", "latn" };
            plan = new Plan
            {
                Gsub = layout.Gsub?.CollectFeatures(tags, null, out _),
                Gpos = layout.Gpos?.CollectFeatures(tags, null, out _),
            };
            byScript[scriptTag] = plan;
            return plan;
        }

        /// <summary>The OpenType script tag of a run, from the script of its first letter.</summary>
        internal static string ScriptTag(int script)
        {
            if (script == ShapingTables.Script.Thai) return "thai";
            if (script == ShapingTables.Script.Lao) return "lao ";
            if (script == ShapingTables.Script.Hebrew) return "hebr";
            if (script == ShapingTables.Script.Cyrillic) return "cyrl";
            if (script == ShapingTables.Script.Greek) return "grek";
            if (script == ShapingTables.Script.Armenian) return "armn";
            if (script == ShapingTables.Script.Georgian) return "geor";
            if (script == ShapingTables.Script.Ethiopic) return "ethi";
            if (script == ShapingTables.Script.Cherokee) return "cher";
            if (script == ShapingTables.Script.Canadian_Aboriginal) return "cans";
            if (script == ShapingTables.Script.Ogham) return "ogam";
            if (script == ShapingTables.Script.Runic) return "runr";
            if (script == ShapingTables.Script.Vai) return "vai ";
            if (script == ShapingTables.Script.Bamum) return "bamu";
            if (script == ShapingTables.Script.New_Tai_Lue) return "talu";
            if (script == ShapingTables.Script.Ol_Chiki) return "olck";
            if (script == ShapingTables.Script.Lisu) return "lisu";
            return "latn";
        }

        /// <summary>Shape one run of a script this shaper covers; the result is appended.</summary>
        internal static void Shape(List<int> cps, List<int> clusters, int script, IShapingFont font, List<ShapedGlyph> result)
        {
            var plan = PlanFor(font, ScriptTag(script));
            bool thaiOrLao = script == ShapingTables.Script.Thai || script == ShapingTables.Script.Lao;
            bool hebrew = script == ShapingTables.Script.Hebrew;

            if (thaiOrLao) CutSaraAm(cps, clusters);
            ShapingCommon.Normalize(cps, clusters, font, ShapingCommon.NormalizationMode.Composed, null, hebrew ? ReorderMarksHebrew : (System.Action<List<int>, int, int>)null);

            bool rtl = ShapingCommon.IsRightToLeft(script);
            var buf = new L.GlyphBuffer();
            for (int i = 0; i < cps.Count; i++)
            {
                var g = ShapingCommon.MakeGlyph(cps[i], clusters[i], font);
                g.Mask = uint.MaxValue;
                g.Hidden = ShapingCommon.IsDefaultIgnorable(cps[i]);
                buf.Glyphs.Add(g);
            }
            // A right-to-left run is shaped in logical order like any other — the lookups are
            // written for it — and only its mark offsets are computed for the other pen
            // direction (see ResolveAttachments): the RTL composer reverses the string later.

            var layout = font.Layout;
            if (layout.Gsub != null && plan.Gsub != null)
                ShapingCommon.ApplyStage(layout, layout.Gsub, buf, plan.Gsub, GsubFeatures, null);

            ShapingCommon.ResetAdvances(buf, font, zeroMarks: false);
            if (layout.Gpos != null && plan.Gpos != null)
                ShapingCommon.ApplyStage(layout, layout.Gpos, buf, plan.Gpos, GposFeatures, null);
            // Marks zeroed after positioning (HarfBuzz's "by GDEF, late"), before the
            // attachments are turned into offsets: the advances between a base and its mark
            // are the bases' only.
            ShapingCommon.ZeroMarkWidths(buf, font);
            buf.ResolveAttachments(rtl);

            ShapingCommon.Emit(buf, result);
        }

        /// <summary>
        /// SARA AM (U+0E33, Lao U+0EB3) → NIKHAHIT + SARA AA, the nikhahit moved back over the
        /// above-base marks before it: ด + ๋ + ำ becomes ด + ํ + ๋ + า.
        /// </summary>
        private static void CutSaraAm(List<int> cps, List<int> clusters)
        {
            for (int i = 0; i < cps.Count; i++)
            {
                int u = cps[i];
                if ((u & ~0x0080) != 0x0E33) continue;
                int nikhahit = u - 0x0E33 + 0x0E4D, saraAa = u - 1, cluster = clusters[i];
                cps[i] = saraAa;
                int start = i;
                while (start > 0 && IsAboveBaseMark(cps[start - 1])) start--;
                cps.Insert(start, nikhahit);
                clusters.Insert(start, cluster);
                i++;
            }
        }

        private static bool IsAboveBaseMark(int u)
        {
            u &= ~0x0080;
            return (u >= 0x0E34 && u <= 0x0E37) || (u >= 0x0E47 && u <= 0x0E4E) || u == 0x0E31 || u == 0x0E3B;
        }

        /// <summary>Hebrew: patah or qamats, sheva or hiriq, then meteg or a below mark → the last two swapped.</summary>
        private static void ReorderMarksHebrew(List<int> cps, int start, int end)
        {
            for (int i = start + 2; i < end; i++)
            {
                int c0 = ShapingCommon.CombiningClass(cps[i - 2]);
                int c1 = ShapingCommon.CombiningClass(cps[i - 1]);
                int c2 = ShapingCommon.CombiningClass(cps[i]);
                if ((c0 == 20 || c0 == 21) && (c1 == 22 || c1 == 23) && (c2 == 25 || c2 == 220))
                {
                    int t = cps[i - 1]; cps[i - 1] = cps[i]; cps[i] = t;
                    break;
                }
            }
        }
    }
}
