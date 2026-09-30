using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityGameTranslator.Core.Rasterizer;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A text of a script that needs shaping being EDITED (RtlFieldLayout with shaped units): the
    /// field shows exactly what a label shows (OpenTypeText.Shape, itself held to HarfBuzz), and the
    /// caret only ever stands between two units — never inside a syllable drawn as one — on the
    /// boundaries of Unicode's extended grapheme clusters (.NET's StringInfo, an independent
    /// reading of UAX #29). Real fonts: Noto Sans Devanagari, Bengali, Khmer.
    /// </summary>
    internal static class SyllabicFieldChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string fonts = Path.Combine(AppContext.BaseDirectory, "TestData", "Fonts");
            One(check, Path.Combine(fonts, "NotoSansDevanagari.ttf"), "किताब क्षमा हिन्दी");
            One(check, Path.Combine(fonts, "NotoSansBengali.ttf"), "কোথায় বাংলা");
            One(check, Path.Combine(fonts, "NotoSansKhmer.ttf"), "ភាសាខ្មែរ ស្រី");
        }

        private static void One(Action<bool, string, string> check, string fontPath, string text)
        {
            string name = Path.GetFileNameWithoutExtension(fontPath);
            var parser = new TtfParser(File.ReadAllBytes(fontPath));
            var font = new TtfShapingFont(parser);
            var namer = new DerivedGlyphs(font, parser.GlyphCount);

            var units = OpenTypeText.ShapeUnits(text, font, namer);
            int covered = 0, next = -1; bool contiguous = true;
            foreach (var u in units)
            {
                if (next >= 0 && u.Start != next && text.Substring(next, u.Start - next).Trim().Length > 0) contiguous = false;
                next = u.Start + u.Length;
                covered += u.Length;
            }
            check(units.Count > 0 && contiguous, $"{name}: the shaped runs are cut into contiguous units", $"{units.Count} units");

            var prep = RtlFieldLayout.Prepare(text, t => OpenTypeText.ShapeUnits(t, font, namer));
            check(prep != null, $"{name}: a field with shaped text is presented", "");
            if (prep == null) return;
            var layout = prep.Lay(null);
            string label = OpenTypeText.Shape(text, font, namer);
            check(layout.Display == label, $"{name}: the field shows what a label shows", $"field {Codes(layout.Display)} · label {Codes(label)}");

            // Every place the right arrow stops, from the start to the end.
            var stops = new List<int> { 0 };
            int caret = 0;
            for (int guard = 0; guard <= text.Length; guard++)
            {
                int n = layout.VisualStep(caret, toRight: true);
                if (n == caret) break;
                stops.Add(n);
                caret = n;
            }
            var unitStarts = new HashSet<int>();
            foreach (var u in units) { unitStarts.Add(u.Start); unitStarts.Add(u.Start + u.Length); }
            bool insideUnit = false;
            foreach (int stop in stops)
                foreach (var u in units)
                    if (stop > u.Start && stop < u.Start + u.Length) insideUnit = true;
            check(!insideUnit && stops[stops.Count - 1] == text.Length, $"{name}: the arrows never stop inside a unit, and reach the end",
                string.Join(",", stops));

            var graphemes = new HashSet<int>(StringInfo.ParseCombiningCharacters(text)) { text.Length };
            bool offGrapheme = false;
            foreach (int stop in stops) if (!graphemes.Contains(stop)) offGrapheme = true;
            check(!offGrapheme, $"{name}: every caret stop is a grapheme cluster boundary", string.Join(",", stops));

            // A click anywhere on a unit's glyphs lands before or after the unit.
            bool clickInside = false;
            for (int d = 0; d < layout.Display.Length; d++)
                foreach (bool right in new[] { false, true })
                {
                    int c = layout.CaretFromHit(d, right);
                    foreach (var u in units) if (c > u.Start && c < u.Start + u.Length) clickInside = true;
                }
            check(!clickInside, $"{name}: a click on any glyph of a unit lands at its edge", "");

            // The unit's width on screen: all its glyphs.
            bool lengths = true;
            foreach (var u in units)
                for (int i = u.Start; i < u.Start + u.Length; i++)
                    if (layout.DisplayLengthOf(i) != u.Glyphs.Length) lengths = false;
            check(lengths, $"{name}: every typed character of a unit is drawn by all its glyphs", "");

            // TMP's one-for-one label: exactly as long as the typed text, or refused.
            string padded = prep.PaddedShaped();
            check(padded == null ? !prep.Paddable : padded.Length == text.Length,
                $"{name}: TMP's label keeps one character per typed one, or says it cannot", padded == null ? "not paddable" : padded.Length + " vs " + text.Length);
        }

        private static string Codes(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) sb.Append(((int)c).ToString("X4")).Append(' ');
            return sb.ToString().Trim();
        }
    }
}
