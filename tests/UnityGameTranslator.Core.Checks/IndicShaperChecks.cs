using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityGameTranslator.Core.Rasterizer;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The Indic shaper against HarfBuzz: every expected line below is HarfBuzz 14.4's output
    /// for the same word and the same font (Noto Sans Devanagari, TestData/Fonts), recorded on
    /// 2026-09-03 with uharfbuzz at 1000 units per em — glyph id, then (advance, x offset,
    /// y offset). What the checks prove is agreement with the reference shaper the fonts were
    /// built against, word by word, glyph by glyph, unit by unit.
    /// </summary>
    internal static class IndicShaperChecks
    {
        private static readonly (string word, string what, string expected)[] Cases =
        {
            ("क्ष",        "akhand conjunct",                              "90(717,0,0)"),
            ("कि",         "pre-base matra reordered before its consonant", "542(259,0,0) 56(768,0,0)"),
            ("किं",        "…with an anusvara: the matra takes a variant",  "557(259,0,0) 56(768,0,0) 759(0,0,0)"),
            ("प्रिय",      "half form, rakar, matra before the cluster",    "543(259,0,0) 309(569,0,0) 81(580,0,0)"),
            ("कर्म",       "reph moved after the base",                     "56(768,0,0) 80(598,0,0) 503(0,0,0)"),
            ("हिन्दी",     "two syllables, half form, ii matra",            "541(259,0,0) 88(531,0,0) 245(309,0,0) 73(531,0,0) 33(259,0,0)"),
            ("अतिरिक्त",   "independent vowel, two i matras, half form",    "5(764,0,0) 542(259,0,0) 71(570,0,0) 539(259,0,0) 82(409,0,0) 232(536,0,0) 71(570,0,0)"),
            ("दिल्ली",     "half la before la",                             "541(259,0,0) 73(531,0,0) 252(451,0,0) 83(678,0,0) 33(259,0,0)"),
            ("कृपया",      "vocalic r below the base (mark positioning)",   "56(768,0,0) 36(0,-221,0) 76(568,0,0) 81(580,0,0) 31(259,0,0)"),
            ("कैं",        "ai matra + anusvara ligated, positioned",       "56(768,0,0) 514(0,-221,0)"),
            ("क्रम",       "rakar: ka + ra conjunct",                       "295(768,0,0) 80(598,0,0)"),
            ("रुपया",      "ra + u ligature",                               "490(598,0,0) 76(568,0,0) 81(580,0,0) 31(259,0,0)"),
            ("विकल्प",     "three syllables",                               "542(259,0,0) 84(556,0,0) 56(768,0,0) 252(429,0,0) 76(568,0,0)"),
            ("ट्रक",       "below-base ra on tta, positioned",              "66(504,0,0) 505(0,-34,0) 56(768,0,0)"),
            ("शक्ति",      "matra inside a word, after a half form",        "85(680,0,0) 552(259,0,0) 232(536,0,0) 71(570,0,0)"),
            ("सर्वोत्तम",  "reph after a post-base matra, tta conjunct",    "87(676,0,0) 84(556,0,0) 31(259,0,0) 511(0,0,0) 439(691,0,0) 80(598,0,0)"),
            ("र्",         "ra + halant alone: no reph",                    "82(409,0,0) 103(0,0,0)"),
        };

        public static void Run(Action<bool, string, string> check)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TestData", "Fonts", "NotoSansDevanagari.ttf");
            if (!File.Exists(path)) { check(false, "check font present", path); return; }
            var font = new TtfShapingFont(new TtfParser(File.ReadAllBytes(path)));

            check(IndicShaper.NeedsShaping("हिन्दी") && !IndicShaper.NeedsShaping("Hello") && !IndicShaper.NeedsShaping("สวัสดี"),
                "NeedsShaping: Devanagari yes, Latin and Thai no", "");

            foreach (var (word, what, expected) in Cases)
            {
                string got;
                try { got = Describe(IndicShaper.Shape(word, font)); }
                catch (Exception ex) { got = "threw " + ex.GetType().Name + ": " + ex.Message; }
                check(got == expected, word + " — " + what, got == expected ? "= HarfBuzz" : "got " + got + " / HarfBuzz " + expected);
            }

            // Latin passes through untouched: one glyph per character, cmap and advance only.
            var latin = IndicShaper.Shape("Ab", font);
            check(latin.Count == 2 && latin[0].Glyph == font.GlyphIndex('A') && latin[1].Glyph == font.GlyphIndex('b')
                  && latin[0].XAdvance == font.AdvanceWidth(latin[0].Glyph),
                "Latin: cmap glyphs, no shaping", Describe(latin));
        }

        private static string Describe(List<IndicShaper.ShapedGlyph> glyphs)
        {
            var sb = new StringBuilder();
            foreach (var g in glyphs)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(g.Glyph).Append('(').Append(g.XAdvance).Append(',').Append(g.XOffset).Append(',').Append(g.YOffset).Append(')');
            }
            return sb.ToString();
        }
    }
}
