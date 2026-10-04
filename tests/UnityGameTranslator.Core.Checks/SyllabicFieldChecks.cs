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
            // Right to left: units named by private codepoints still read right to left (a Hebrew
            // field showed its line backwards, probe 3, 2026-10-01).
            One(check, Path.Combine(fonts, "NotoSansHebrew.ttf"), "שָׁלוֹם עוֹלָם");
            // Outside the Basic Multilingual Plane, right to left, joined: every character a surrogate pair.
            One(check, Path.Combine(fonts, "NotoSansAdlam.ttf"), "\U0001E900\U0001E923\U0001E924\U0001E922\U0001E925 \U0001E922\U0001E944");
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
            // What a label shows: the shaped text, and — right to left — the composer's visual order.
            string label = OpenTypeText.Shape(text, font, namer);
            if (RtlText.NeedsPresentation(label)) label = RtlComposer.Compose(label, RtlOutput.VisualOrder);
            check(layout.Display == label, $"{name}: the field shows what a label shows", $"field {Codes(layout.Display)} · label {Codes(label)}");

            // Every place the arrow toward the text's end stops (right, or left in a right-to-left text).
            bool rtl = layout.IsRtl(0);
            check(rtl == RtlText.NeedsPresentation(OpenTypeText.Shape(text, font, namer)), $"{name}: the field reads in the script's direction", rtl ? "right to left" : "left to right");
            var stops = new List<int> { 0 };
            int caret = 0;
            for (int guard = 0; guard <= text.Length; guard++)
            {
                int n = layout.VisualStep(caret, toRight: !rtl);
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

            // TMP's label: every character tells the typed one it stands for, in order, and TMP's
            // own editing — read from those (TMP_InputField: Backspace removes characterInfo[caret - 1],
            // Delete characterInfo[caret]) — lands on what was typed, at every unit edge.
            string tmpLabel = prep.LabelFor(out var index, out var length);
            bool ordered = index.Length == tmpLabel.Length && length.Length == tmpLabel.Length;
            for (int k = 1; ordered && k < index.Length; k++) if (index[k] < index[k - 1]) ordered = false;
            check(ordered && index.Length > 0 && index[0] == 0 && index[index.Length - 1] + length[length.Length - 1] == text.Length,
                $"{name}: TMP's label covers the typed text, in order", $"{tmpLabel.Length} label characters for {text.Length} typed");
            bool editsRight = true;
            string why = "";
            foreach (var u in units)
            {
                // Label characters of this unit: those standing for its typed characters.
                int first = Array.FindIndex(index, x => x >= u.Start), last = Array.FindLastIndex(index, x => x < u.Start + u.Length);
                if (first < 0 || last < first) { editsRight = false; why = "unit " + u.Start + " has no label character"; break; }
                string afterBackspace = text.Remove(index[last], length[last]);
                string afterDelete = text.Remove(index[first], length[first]);
                // A character is a code point: a surrogate pair goes whole.
                int end = u.Start + u.Length;
                int lastWidth = end - 2 >= u.Start && char.IsLowSurrogate(text[end - 1]) ? 2 : 1;
                int firstWidth = char.IsHighSurrogate(text[u.Start]) ? 2 : 1;
                if (afterBackspace != text.Remove(end - lastWidth, lastWidth) || afterDelete != text.Remove(u.Start, firstWidth))
                { editsRight = false; why = $"unit {u.Start}+{u.Length}"; break; }
            }
            check(editsRight, $"{name}: Backspace after a unit takes its last typed character, Delete before it its first", why);

            // TMP draws the label's glyphs where the typed characters they stand for come on screen
            // (RtlInputFields.MoveTmpGlyphs): that order must give exactly what the uGUI field shows.
            var onScreen = new System.Text.StringBuilder();
            foreach (int i in layout.LogicalOnScreen(0))
                for (int k = 0; k < tmpLabel.Length; k++)
                    if (index[k] == i && tmpLabel[k] != RtlFieldLayout.MergedSlot) onScreen.Append(tmpLabel[k]);
            string uguiShows = layout.Display.Replace(RtlFieldLayout.ZeroWidthSpace.ToString(), "");
            check(onScreen.ToString() == uguiShows, $"{name}: TMP's glyphs moved into screen order show what the uGUI field shows",
                $"TMP {Codes(onScreen.ToString())} · uGUI {Codes(uguiShows)}");
        }

        private static string Codes(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) sb.Append(((int)c).ToString("X4")).Append(' ');
            return sb.ToString().Trim();
        }
    }
}
