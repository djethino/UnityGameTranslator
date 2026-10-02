using System.Collections.Generic;
using S = UnityGameTranslator.Core.TextShaping.ShapingTables.Script;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// The front door of OpenType shaping: a string in, positioned glyphs of a font out. Cuts
    /// the text into runs of one script each (common and inherited characters — spaces,
    /// digits, joiners, combining marks — ride with the run they are in) and hands every run
    /// to the shaper its script calls for, the way HarfBuzz's categorize does:
    ///   • the ten classic Indic scripts → <see cref="IndicShaper"/>;
    ///   • Myanmar → <see cref="MyanmarShaper"/>; Khmer → <see cref="KhmerShaper"/>;
    ///   • the universal-engine scripts (Tibetan, Javanese, Balinese, Mongolian, Adlam, Chakma…
    ///     — HarfBuzz's list, read from its source, every plane), and the joining scripts its
    ///     Arabic shaper takes besides Arabic (Syriac) → <see cref="UseShaper"/>;
    ///   • any other script → <see cref="DefaultShaper"/>, when CLDR says it needs shaping or
    ///     where a letter carries combining marks.
    /// Arabic is not routed here: it goes through the presentation-form path, the only one
    /// that reaches engines whose font we do not control.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class OpenTypeShaping
    {
        private enum Engine { Default, Indic, Myanmar, Khmer, Use, None }

        private static Engine EngineOf(int script)
        {
            if (script == S.Devanagari || script == S.Bengali || script == S.Gurmukhi || script == S.Gujarati || script == S.Oriya
                || script == S.Tamil || script == S.Telugu || script == S.Kannada || script == S.Malayalam || script == S.Sinhala)
                return Engine.Indic;
            if (script == S.Myanmar) return Engine.Myanmar;
            if (script == S.Khmer) return Engine.Khmer;
            // Arabic goes through Unicode's presentation forms — the one path that reaches engines
            // whose font we do not control.
            if (script == S.Arabic) return Engine.None;
            // The other scripts HarfBuzz joins with its Arabic shaper (Syriac) have no presentation
            // forms: their joined letters come from the font's tables, through the universal
            // engine's joining step (their joining types are generated with its own). Sent nowhere
            // until 2026-10-02 — "Arabic and Syriac have their own path", and Syriac had none.
            if (ShapingCommon.IsArabicShaperScript(script) || ShapingCommon.IsUseScript(script)) return Engine.Use;
            return Engine.Default;
        }

        /// <summary>
        /// Does this string hold a character of a script the shapers act on? Judged per code
        /// point (a supplementary-plane letter arrives as a surrogate pair) on its script.
        /// </summary>
        internal static bool NeedsShaping(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int baseCp = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // ASCII holds no combining mark and no letter of a script that needs shaping
                // (Unicode's own layout of its first block): the common case costs one compare.
                if (c < 0x80) { baseCp = c; continue; }
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(c, text[i + 1]); i++; }
                if (IsShapedScript(ShapingCommon.ScriptOf(cp))) return true;
                if (ShapingCommon.IsUnicodeMark(cp))
                {
                    if (MarksPlacedByFont(baseCp)) return true;
                    continue;
                }
                baseCp = cp;
            }
            return false;
        }

        /// <summary>
        /// The scripts whose every run is shaped: the ones with a syllabic engine, the universal
        /// engine's (joining scripts included), and — for the default engine — those CLDR says
        /// cannot be shown without shaping (Thaana, Lao…) and those whose HarfBuzz shaper the
        /// default one transcribes (<see cref="DefaultShaper.HasScriptRules"/>). Never a script
        /// named here: the rest is shaped only where it carries a combining mark
        /// (<see cref="MarksPlacedByFont"/>).
        /// </summary>
        internal static bool IsShapedScript(int script)
        {
            switch (EngineOf(script))
            {
                case Engine.Indic: case Engine.Myanmar: case Engine.Khmer: case Engine.Use: return true;
                case Engine.Default: return ShapingCommon.IsShapingRequired(script) || DefaultShaper.HasScriptRules(script);
                default: return false;
            }
        }

        /// <summary>
        /// A combining mark after this base is placed by the font's tables. 🔴 Measured, not
        /// assumed (bench, 2026-10-02): a game's engine draws a combining mark as a character of
        /// its own, BESIDE the letter — Vietnamese "Nhấn" written decomposed showed "Nhâ˜n",
        /// Russian stress "Нажми́те" showed "Нажми´те", in UI.Text and TextMesh Pro alike. So any
        /// script's letter carrying one is shaped, except Arabic's, whose marks the
        /// presentation-form path handles (<see cref="EngineOf"/>).
        /// </summary>
        internal static bool MarksPlacedByFont(int baseCp)
        {
            if (baseCp < 0) return false;
            int script = ShapingCommon.ScriptOf(baseCp);
            return EngineOf(script) != Engine.None;
        }

        /// <summary>
        /// Shape a string: runs by script, each through its engine. A run of a script no engine
        /// handles is emitted as plain cmap glyphs. Output clusters are indices into the string.
        /// </summary>
        internal static List<ShapedGlyph> Shape(string text, IShapingFont font)
        {
            var result = new List<ShapedGlyph>(text.Length);
            if (string.IsNullOrEmpty(text)) return result;

            // Code points with their source index.
            var cps = new List<int>(text.Length);
            var idx = new List<int>(text.Length);
            for (int i = 0; i < text.Length;)
            {
                int cp = char.ConvertToUtf32(text, i);
                cps.Add(cp); idx.Add(i);
                i += cp > 0xFFFF ? 2 : 1;
            }

            int start = 0;
            while (start < cps.Count)
            {
                // A run: the first real script met, then everything until the next different
                // real script. Common/Inherited characters belong to the run they sit in.
                int script = ScriptForRun(cps, start, out int firstReal);
                int end = firstReal < 0 ? cps.Count : firstReal + 1;
                while (end < cps.Count)
                {
                    int s = ShapingCommon.ScriptOf(cps[end]);
                    if (s != S.Common && s != S.Inherited && s != S.Unknown && s != script) break;
                    end++;
                }
                var runCps = cps.GetRange(start, end - start);
                var runIdx = idx.GetRange(start, end - start);
                switch (script == -1 ? Engine.None : EngineOf(script))
                {
                    case Engine.Indic: IndicShaper.ShapeRun(runCps, runIdx, font, result); break;
                    case Engine.Myanmar: MyanmarShaper.Shape(runCps, runIdx, font, result); break;
                    case Engine.Khmer: KhmerShaper.Shape(runCps, runIdx, font, result); break;
                    case Engine.Use: UseShaper.Shape(runCps, runIdx, script, font, result); break;
                    case Engine.Default: DefaultShaper.Shape(runCps, runIdx, script, font, result); break;
                    default:
                        for (int k = 0; k < runCps.Count; k++)
                        {
                            int glyph = font.GlyphIndex(runCps[k]);
                            result.Add(new ShapedGlyph { Glyph = glyph, Cluster = runIdx[k], XAdvance = font.AdvanceWidth(glyph) });
                        }
                        break;
                }
                start = end;
            }
            return result;
        }

        private static int ScriptForRun(List<int> cps, int start, out int firstReal)
        {
            firstReal = -1;
            for (int i = start; i < cps.Count; i++)
            {
                int s = ShapingCommon.ScriptOf(cps[i]);
                if (s == S.Common || s == S.Inherited || s == S.Unknown) continue;
                firstReal = i;
                return s;
            }
            return -1;
        }
    }
}
