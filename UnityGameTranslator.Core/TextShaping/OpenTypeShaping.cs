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
    ///   • the universal-engine scripts (Tibetan, Javanese, Balinese, Mongolian…) → <see cref="UseShaper"/>;
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
            if (IsUseScript(script)) return Engine.Use;
            return Engine.Default;
        }

        private static bool IsUseScript(int script)
        {
            return script == S.Tibetan || script == S.Mongolian || script == S.Buhid || script == S.Hanunoo || script == S.Tagalog
                || script == S.Tagbanwa || script == S.Limbu || script == S.Tai_Le || script == S.Buginese || script == S.Syloti_Nagri
                || script == S.Tifinagh || script == S.Balinese || script == S.Nko || script == S.Phags_Pa || script == S.Cham
                || script == S.Kayah_Li || script == S.Lepcha || script == S.Rejang || script == S.Saurashtra || script == S.Sundanese
                || script == S.Javanese || script == S.Meetei_Mayek || script == S.Tai_Tham || script == S.Tai_Viet || script == S.Batak
                || script == S.Mandaic;
        }

        /// <summary>Does this string hold a character of a script the shapers act on?</summary>
        internal static bool NeedsShaping(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 0x0590) continue;
                if (c >= 0x0590 && c <= 0x05FF) return true;                       // Hebrew
                if (c >= IndicTables.IndicFirst && c <= 0x0FFF) return true;       // Indic, Thai, Lao, Tibetan
                if (c >= 0x1000 && c <= 0x109F) return true;                       // Myanmar
                if (c >= 0x1700 && c <= 0x1AAF) return true;                       // Philippine, Khmer, Mongolian, Limbu, Tai Le, Buginese, Tai Tham
                if (c >= 0x1B00 && c <= 0x1C4F) return true;                       // Balinese, Sundanese, Batak, Lepcha
                if (c >= 0xA800 && c <= 0xAAFF) return true;                       // Syloti, Phags-pa, Saurashtra, Kayah Li, Rejang, Javanese, Myanmar ext, Cham, Tai Viet, Meetei
                if (c >= 0xABC0 && c <= 0xABFF) return true;                       // Meetei Mayek
                if (c >= 0x07C0 && c <= 0x085F) return true;                       // NKo, Mandaic
                if (c >= 0x2D30 && c <= 0x2D7F) return true;                       // Tifinagh
            }
            return false;
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
