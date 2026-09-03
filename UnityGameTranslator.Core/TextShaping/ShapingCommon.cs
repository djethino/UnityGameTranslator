using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityGameTranslator.Core.Rasterizer;
using L = UnityGameTranslator.Core.Rasterizer.OpenTypeLayout;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// The private-use codepoints our font assets hand to glyphs no codepoint maps to and to
    /// positioned variants (TtfFontPipeline, FontShaping). Named here, on the pure side, so
    /// the composer and the shapers can recognise them without the rasterizer.
    /// </summary>
    internal static class PrivateGlyphs
    {
        internal const int First = 0xE000;
        internal const int Last = 0xF0FF;
        internal static bool Contains(int cp) => cp >= First && cp <= Last;
    }

    /// <summary>One positioned glyph of a shaping result, in font units.</summary>
    internal struct ShapedGlyph
    {
        public int Glyph;
        public int Cluster;      // index of the first code unit of its syllable in the input
        public int XAdvance, XOffset, YOffset;
    }

    /// <summary>
    /// What every OpenType shaper of this project shares, the way HarfBuzz's core is shared by
    /// its shapers: Unicode lookups over the generated tables, the three-round normalization
    /// (decompose, reorder marks, recompose), stage application in lookup-index order with
    /// merged masks, mark-width zeroing, dotted circles, the joining forms of cursive scripts,
    /// and the output. A shaper (Indic, Myanmar, Khmer, universal, default) owns only its
    /// syllable model, its reordering and its feature plan.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class ShapingCommon
    {
        // ───────────────────────────── Unicode lookups ─────────────────────────────

        /// <summary>HarfBuzz-modified canonical combining class; 0 for a non-mark.</summary>
        internal static int CombiningClass(int cp)
        {
            var t = ShapingTables.CombiningClasses;
            int lo = 0, hi = t.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int key = t[mid * 2];
                if (cp < key) hi = mid - 1;
                else if (cp > key) lo = mid + 1;
                else return t[mid * 2 + 1];
            }
            return 0;
        }

        /// <summary>First-level canonical decomposition; false when the code point has none.</summary>
        internal static bool TryDecompose(int cp, out int a, out int b, out bool composes)
        {
            var t = ShapingTables.Decompositions;
            int lo = 0, hi = t.Length / 4 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int key = t[mid * 4];
                if (cp < key) hi = mid - 1;
                else if (cp > key) lo = mid + 1;
                else { a = t[mid * 4 + 1]; b = t[mid * 4 + 2]; composes = t[mid * 4 + 3] != 0; return true; }
            }
            a = b = 0; composes = false;
            return false;
        }

        private static Dictionary<long, int> _compositions;

        /// <summary>The canonical composition of a pair, 0 when there is none (the inverse of the table, exclusions left out).</summary>
        internal static int Compose(int a, int b)
        {
            if (_compositions == null)
            {
                var map = new Dictionary<long, int>();
                var t = ShapingTables.Decompositions;
                for (int i = 0; i + 3 < t.Length; i += 4)
                    if (t[i + 3] != 0) map[((long)t[i + 1] << 32) | (uint)t[i + 2]] = t[i];
                _compositions = map;
            }
            return _compositions.TryGetValue(((long)a << 32) | (uint)b, out int ab) ? ab : 0;
        }

        private static int RunValue(int[] runs, int cp, int missing)
        {
            int lo = 0, hi = runs.Length / 3 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int a = runs[mid * 3], b = runs[mid * 3 + 1];
                if (cp < a) hi = mid - 1;
                else if (cp > b) lo = mid + 1;
                else return runs[mid * 3 + 2];
            }
            return missing;
        }

        private static bool InRanges(int[] ranges, int cp)
        {
            int lo = 0, hi = ranges.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (cp < ranges[mid * 2]) hi = mid - 1;
                else if (cp > ranges[mid * 2 + 1]) lo = mid + 1;
                else return true;
            }
            return false;
        }

        internal static bool IsDefaultIgnorable(int cp) => InRanges(ShapingTables.DefaultIgnorable, cp);
        internal static int UseCategory(int cp) => RunValue(ShapingTables.UseCategories, cp, ShapingTables.Use.O);
        internal static int JoiningType(int cp) => RunValue(ShapingTables.JoiningTypes, cp, 0);
        internal static int ScriptOf(int cp) => RunValue(ShapingTables.Scripts, cp, ShapingTables.Script.Unknown);

        /// <summary>HarfBuzz's Indic-table category for the Myanmar and Khmer blocks; X elsewhere.</summary>
        internal static int MyanmarKhmerCategory(int cp)
        {
            var t = ShapingTables.MyanmarKhmerCategories;
            int lo = 0, hi = t.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int key = t[mid * 2];
                if (cp < key) hi = mid - 1;
                else if (cp > key) lo = mid + 1;
                else return t[mid * 2 + 1];
            }
            return ShapingTables.Syllabic.X;
        }

        internal static bool IsUnicodeMark(int cp)
        {
            var cat = cp <= 0xFFFF ? CharUnicodeInfo.GetUnicodeCategory((char)cp) : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark || cat == UnicodeCategory.EnclosingMark;
        }

        // ───────────────────────────── normalization ─────────────────────────────

        internal enum NormalizationMode
        {
            /// <summary>Keep what the font has, decompose only what it lacks, recompose (HarfBuzz's default).</summary>
            Composed,
            /// <summary>Decompose everything the font can show decomposed, recompose only non-mark pairs (the syllabic shapers).</summary>
            DecomposedThenComposedDiacritics,
        }

        internal delegate bool DecomposeOverride(int ab, out int a, out int b);

        /// <summary>
        /// HarfBuzz's normalization, three rounds over code points with their source indices:
        /// decompose (when the font can show the parts), reorder marks by modified combining
        /// class inside each run of marks, recompose when the font has the composite and the
        /// mode allows. <paramref name="decompose"/> lets a shaper cut a sign the standard
        /// does not; <paramref name="reorderMarks"/> runs on each mark run after the sort.
        /// </summary>
        internal static void Normalize(List<int> cps, List<int> clusters, IShapingFont font, NormalizationMode mode,
                                       DecomposeOverride decompose = null, Action<List<int>, int, int> reorderMarks = null)
        {
            // Round 1: decompose.
            var outCps = new List<int>(cps.Count + 8);
            var outClusters = new List<int>(cps.Count + 8);
            bool shortCircuit = mode == NormalizationMode.Composed;
            for (int i = 0; i < cps.Count; i++)
            {
                int cp = cps[i];
                // HarfBuzz's decompose_current_character: keep what the font has when short-
                // circuiting; else the decomposition the font can show; else the character itself.
                if (shortCircuit && font.GlyphIndex(cp) > 0) { outCps.Add(cp); outClusters.Add(clusters[i]); continue; }
                if (Decompose(cp, font, decompose, shortCircuit, outCps, outClusters, clusters[i], 0) == 0)
                {
                    outCps.Add(cp); outClusters.Add(clusters[i]);
                }
            }
            cps.Clear(); cps.AddRange(outCps);
            clusters.Clear(); clusters.AddRange(outClusters);

            // Round 2: reorder marks by combining class, stable, per run of non-zero classes.
            for (int i = 0; i < cps.Count;)
            {
                if (CombiningClass(cps[i]) == 0) { i++; continue; }
                int end = i + 1;
                while (end < cps.Count && CombiningClass(cps[end]) != 0) end++;
                if (end - i <= 32)
                {
                    for (int x = i + 1; x < end; x++)
                    {
                        int cp = cps[x], cl = clusters[x], key = CombiningClass(cp);
                        int y = x - 1;
                        while (y >= i && CombiningClass(cps[y]) > key) { cps[y + 1] = cps[y]; clusters[y + 1] = clusters[y]; y--; }
                        cps[y + 1] = cp; clusters[y + 1] = cl;
                    }
                    reorderMarks?.Invoke(cps, i, end);
                }
                i = end;
            }

            // Round 3: recompose.
            int starter = 0;
            for (int i = 1; i < cps.Count; i++)
            {
                int cp = cps[i];
                if (IsUnicodeMark(cp))
                {
                    bool adjacent = starter == i - 1 || CombiningClass(cps[i - 1]) < CombiningClass(cp);
                    int a = cps[starter];
                    if (adjacent && !(mode == NormalizationMode.DecomposedThenComposedDiacritics && IsUnicodeMark(a)))
                    {
                        int ab = Compose(a, cp);
                        if (ab != 0 && font.GlyphIndex(ab) > 0)
                        {
                            cps[starter] = ab;
                            cps.RemoveAt(i); clusters.RemoveAt(i);
                            i--;
                            continue;
                        }
                    }
                }
                if (CombiningClass(cps[i]) == 0) starter = i;
            }
        }

        /// <summary>HarfBuzz's decompose(): the count of characters written, 0 when nothing could be.</summary>
        private static int Decompose(int ab, IShapingFont font, DecomposeOverride decompose, bool shortest,
                                     List<int> outCps, List<int> outClusters, int cluster, int depth)
        {
            if (depth > 8) return 0;
            int a, b;
            bool has = decompose != null ? decompose(ab, out a, out b) : TryDecompose(ab, out a, out b, out _);
            if (!has || (b != 0 && font.GlyphIndex(b) <= 0)) return 0;
            bool hasA = font.GlyphIndex(a) > 0;
            if (shortest && hasA)
            {
                outCps.Add(a); outClusters.Add(cluster);
                if (b != 0) { outCps.Add(b); outClusters.Add(cluster); }
                return b != 0 ? 2 : 1;
            }
            int ret = Decompose(a, font, decompose, shortest, outCps, outClusters, cluster, depth + 1);
            if (ret > 0)
            {
                if (b != 0) { outCps.Add(b); outClusters.Add(cluster); }
                return ret + (b != 0 ? 1 : 0);
            }
            if (!hasA) return 0;
            outCps.Add(a); outClusters.Add(cluster);
            if (b != 0) { outCps.Add(b); outClusters.Add(cluster); }
            return b != 0 ? 2 : 1;
        }

        // ───────────────────────────── buffer, stages, output ─────────────────────────────

        /// <summary>
        /// The substitution features every syllabic shaper applies AFTER its own, unconstrained
        /// by syllables — HarfBuzz's common and horizontal feature lists, collected after the
        /// shaper's and so landing in its last GSUB stage. A font that finishes its conjuncts
        /// in 'rlig' or 'clig' (Balinese, Khmer) is served here, once the subjoined forms exist.
        /// ⚠ Not 'ccmp' and 'locl': every shaper enables those itself, early, and a feature
        /// named twice runs ONCE at its earliest stage (HarfBuzz merges the entries) — run
        /// again here, a font's 'ccmp' undid a conjunct it had just formed (Tibetan ཀྵ).
        /// </summary>
        internal static readonly string[] CommonGsubFeatures = { "rlig", "calt", "clig", "liga", "rclt" };

        /// <summary>Scripts written right to left among the ones routed to a shaper here.</summary>
        internal static bool IsRightToLeft(int script)
        {
            return script == ShapingTables.Script.Hebrew || script == ShapingTables.Script.Arabic || script == ShapingTables.Script.Syriac
                || script == ShapingTables.Script.Nko || script == ShapingTables.Script.Mandaic || script == ShapingTables.Script.Thaana
                || script == ShapingTables.Script.Samaritan;
        }

        /// <summary>The category string of the buffer as it is now (after a stage merged or split glyphs).</summary>
        internal static string Categories(L.GlyphBuffer buf, Func<int, char> letterOf)
        {
            var sb = new System.Text.StringBuilder(buf.Count);
            for (int i = 0; i < buf.Count; i++) sb.Append(letterOf(buf[i].Category));
            return sb.ToString();
        }

        internal static L.ShapedGlyph MakeGlyph(int cp, int cluster, IShapingFont font)
        {
            int glyph = font.GlyphIndex(cp);
            return new L.ShapedGlyph
            {
                Glyph = glyph,
                Cluster = cluster,
                XAdvance = font.AdvanceWidth(glyph),
                UnicodeMark = IsUnicodeMark(cp),
                Mask = 0,
            };
        }

        /// <summary>
        /// One stage: the lookups of every listed feature, each once, in lookup-index order,
        /// with the masks of the features naming it merged — the way OpenType engines run a
        /// stage. <paramref name="maskOf"/> gives a feature's mask (all-ones for a global one).
        /// </summary>
        /// <summary>A sink for the buffer after every stage (the checks' tracing); null in the mod.</summary>
        internal static Action<string> Trace;

        internal static void ApplyStage(L layout, L.LayoutTable table, L.GlyphBuffer buf, Dictionary<string, int[]> features,
                                        string[] stage, Func<string, uint> maskOf)
        {
            if (table == null || features == null) return;
            if (Trace != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (string f in stage) if (features.ContainsKey(f)) sb.Append(f).Append(' ');
                Trace($"stage [{sb.ToString().Trim()}] before: {Dump(buf)}");
            }
            var masks = new SortedDictionary<int, uint>();
            foreach (string feature in stage)
            {
                if (!features.TryGetValue(feature, out var lookups)) continue;
                uint mask = maskOf != null ? maskOf(feature) : uint.MaxValue;
                foreach (int li in lookups)
                    masks[li] = masks.TryGetValue(li, out uint m) ? (m | mask) : mask;
            }
            foreach (var kv in masks)
            {
                bool changed = layout.ApplyLookup(table, kv.Key, buf, kv.Value);
                if (Trace != null && changed) Trace($"  lookup {kv.Key} mask {kv.Value:X}: {Dump(buf)}");
            }
        }

        internal static string Dump(L.GlyphBuffer buf)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < buf.Count; i++)
            {
                var g = buf[i];
                sb.Append(g.Glyph).Append("/c").Append(g.Category).Append("/s").Append(g.Syllable).Append("/m").Append(g.Mask.ToString("X")).Append(' ');
            }
            return sb.ToString();
        }

        /// <summary>Advances from the font again (a substituted glyph has its own), marks zeroed when asked.</summary>
        internal static void ResetAdvances(L.GlyphBuffer buf, IShapingFont font, bool zeroMarks)
        {
            var layout = font.Layout;
            for (int i = 0; i < buf.Count; i++)
            {
                buf[i].XAdvance = font.AdvanceWidth(buf[i].Glyph);
                if (zeroMarks && IsMarkGlyph(layout, buf[i])) buf[i].XAdvance = 0;
            }
        }

        internal static void ZeroMarkWidths(L.GlyphBuffer buf, IShapingFont font)
        {
            var layout = font.Layout;
            for (int i = 0; i < buf.Count; i++)
                if (IsMarkGlyph(layout, buf[i])) buf[i].XAdvance = 0;
        }

        private static bool IsMarkGlyph(L layout, L.ShapedGlyph g)
        {
            return layout.HasGlyphClasses ? layout.GlyphClass(g.Glyph) == L.ClassMark : g.UnicodeMark;
        }

        /// <summary>Positioning: every listed feature of the plan, once each, then attachments resolved.</summary>
        internal static void Position(IShapingFont font, L.GlyphBuffer buf, Dictionary<string, int[]> gpos, string[] features, bool rightToLeft = false)
        {
            var layout = font.Layout;
            if (layout.Gpos != null && gpos != null) ApplyStage(layout, layout.Gpos, buf, gpos, features, null);
            buf.ResolveAttachments(rightToLeft);
        }

        /// <summary>The buffer as output, the joiners and other default-ignorables left out.</summary>
        internal static void Emit(L.GlyphBuffer buf, List<ShapedGlyph> result)
        {
            for (int i = 0; i < buf.Count; i++)
            {
                var g = buf[i];
                if (g.Hidden) continue;
                result.Add(new ShapedGlyph { Glyph = g.Glyph, Cluster = g.Cluster, XAdvance = g.XAdvance, XOffset = g.XOffset, YOffset = g.YOffset });
            }
        }

        // ───────────────────────────── syllables ─────────────────────────────

        /// <summary>A grammar rule: a syllable type and the regex (anchored with \G) that matches it over a category string.</summary>
        internal sealed class SyllableRule
        {
            public readonly int Type;
            public readonly Regex Regex;
            public SyllableRule(int type, string pattern) { Type = type; Regex = new Regex("\\G" + pattern, RegexOptions.Compiled); }
        }

        internal sealed class Syllable
        {
            public int Start, End, Type;
        }

        /// <summary>
        /// Cut a category string into syllables: at each position the longest rule wins (a
        /// scanner's longest-match), anything unmatched is a one-character syllable of
        /// <paramref name="otherType"/>. Every glyph gets its syllable number.
        /// </summary>
        internal static List<Syllable> FindSyllables(string categories, SyllableRule[] rules, int otherType, L.GlyphBuffer buf)
        {
            var syllables = new List<Syllable>();
            int p = 0, number = 0;
            while (p < categories.Length)
            {
                int bestLen = 0, bestType = otherType;
                foreach (var rule in rules)
                {
                    var m = rule.Regex.Match(categories, p);
                    if (m.Success && m.Length > bestLen) { bestLen = m.Length; bestType = rule.Type; }
                }
                if (bestLen == 0) { bestLen = 1; bestType = otherType; }
                number++;
                for (int k = p; k < p + bestLen; k++) buf[k].Syllable = number;
                syllables.Add(new Syllable { Start = p, End = p + bestLen, Type = bestType });
                p += bestLen;
            }
            return syllables;
        }

        /// <summary>
        /// A dotted circle (U+25CC) into every syllable of <paramref name="brokenType"/>, after
        /// any leading glyph of <paramref name="rephaCategory"/> — what every shaper draws for
        /// marks with nothing to carry them. Returns true when the buffer changed; callers then
        /// refresh their syllable bounds.
        /// </summary>
        internal static bool InsertDottedCircles(L.GlyphBuffer buf, List<Syllable> syllables, int brokenType, IShapingFont font,
                                                 int dottedCircleCategory, int dottedCirclePosition, int rephaCategory)
        {
            int glyph = font.GlyphIndex(0x25CC);
            if (glyph <= 0) return false;
            bool changed = false;
            for (int s = syllables.Count - 1; s >= 0; s--)
            {
                var syl = syllables[s];
                if (syl.Type != brokenType) continue;
                int at = syl.Start;
                while (rephaCategory >= 0 && at < syl.End && buf[at].Category == rephaCategory) at++;
                var g = new L.ShapedGlyph
                {
                    Glyph = glyph, Cluster = buf[syl.Start].Cluster, Category = dottedCircleCategory, Position = dottedCirclePosition,
                    XAdvance = font.AdvanceWidth(glyph), Mask = buf[syl.Start].Mask, Syllable = buf[syl.Start].Syllable,
                };
                buf.Glyphs.Insert(at, g);
                syl.End++;
                for (int k = s + 1; k < syllables.Count; k++) { syllables[k].Start++; syllables[k].End++; }
                changed = true;
            }
            return changed;
        }

        /// <summary>Syllable bounds re-read from the buffer after substitutions merged or split glyphs.</summary>
        internal static void RefreshSyllables(L.GlyphBuffer buf, List<Syllable> syllables)
        {
            int i = 0, s = 0;
            while (i < buf.Count && s < syllables.Count)
            {
                int number = buf[i].Syllable;
                int end = i + 1;
                while (end < buf.Count && buf[end].Syllable == number) end++;
                syllables[s].Start = i; syllables[s].End = end;
                i = end; s++;
            }
            while (s < syllables.Count) { syllables[s].Start = syllables[s].End = buf.Count; s++; }
        }

        internal static void ReleaseSyllables(L.GlyphBuffer buf)
        {
            for (int i = 0; i < buf.Count; i++) buf[i].Syllable = 0;
        }

        internal static void Move(L.GlyphBuffer buf, int from, int to)
        {
            if (from == to) return;
            var g = buf.Glyphs[from];
            buf.Glyphs.RemoveAt(from);
            buf.Glyphs.Insert(to, g);
        }

        /// <summary>Stable sort of a slice by position (insertion sort, as HarfBuzz's bsort).</summary>
        internal static void SortByPosition(L.GlyphBuffer buf, int start, int end)
        {
            for (int i = start + 1; i < end; i++)
            {
                var g = buf.Glyphs[i];
                int j = i - 1;
                while (j >= start && buf.Glyphs[j].Position > g.Position) { buf.Glyphs[j + 1] = buf.Glyphs[j]; j--; }
                buf.Glyphs[j + 1] = g;
            }
        }

        // ───────────────────────────── cursive joining ─────────────────────────────

        internal const int JoinU = 0, JoinC = 1, JoinD = 2, JoinL = 3, JoinR = 4, JoinT = 5;
        internal const int FormIsol = 0, FormInit = 1, FormMedi = 2, FormFina = 3, FormNone = -1;

        /// <summary>
        /// Arabic-style joining forms for a run of joining types (the Unicode algorithm as every
        /// engine runs it): what each letter's form is, from the neighbours that can join it.
        /// Transparent characters are skipped; non-joining ones break the chain.
        /// </summary>
        internal static int[] JoiningForms(int[] types)
        {
            var forms = new int[types.Length];
            for (int i = 0; i < types.Length; i++) forms[i] = FormNone;
            int prev = -1; // index of the previous joining letter
            for (int i = 0; i < types.Length; i++)
            {
                int t = types[i];
                if (t == JoinT) continue;
                if (t == JoinU) { prev = -1; continue; }
                bool joinsLeft = t == JoinD || t == JoinL || t == JoinC;   // this letter joins the one after it
                bool joinsRight = t == JoinD || t == JoinR || t == JoinC;  // this letter joins the one before it
                bool prevJoins = prev >= 0 && (types[prev] == JoinD || types[prev] == JoinL || types[prev] == JoinC);
                bool joined = joinsRight && prevJoins;
                if (joined && prev >= 0)
                    forms[prev] = forms[prev] == FormFina ? FormMedi : forms[prev] == FormIsol ? FormInit : forms[prev];
                forms[i] = joined ? FormFina : FormIsol;
                prev = joinsLeft ? i : -1;
            }
            return forms;
        }
    }
}
