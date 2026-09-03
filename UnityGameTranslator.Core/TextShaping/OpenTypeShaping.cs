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
    ///     — HarfBuzz's list, generated, every plane) → <see cref="UseShaper"/>;
    ///   • Thai, Lao, Hebrew, and any other script → <see cref="DefaultShaper"/>.
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
            if (script == S.Arabic || script == S.Syriac) return Engine.None;
            if (ShapingCommon.IsUseScript(script)) return Engine.Use;
            return Engine.Default;
        }

        /// <summary>
        /// Does this string hold a character of a script the shapers act on? Judged per code
        /// point (a supplementary-plane letter arrives as a surrogate pair) on its script.
        /// </summary>
        internal static bool NeedsShaping(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 0x0590) continue;
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(c, text[i + 1]); i++; }
                if (IsShapedScript(ShapingCommon.ScriptOf(cp))) return true;
            }
            return false;
        }

        /// <summary>
        /// The scripts a run is shaped for: the ones with a syllabic engine, the universal
        /// engine's, and the three the default engine is asked for — Hebrew, Thai and Lao,
        /// whose marks a font positions and whose vowels it substitutes. Every other script
        /// is left as written: Latin, Cyrillic, Greek, CJK and Arabic have their own paths.
        /// </summary>
        private static bool IsShapedScript(int script)
        {
            switch (EngineOf(script))
            {
                case Engine.Indic: case Engine.Myanmar: case Engine.Khmer: case Engine.Use: return true;
                case Engine.Default: return script == S.Hebrew || script == S.Thai || script == S.Lao;
                default: return false;
            }
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
