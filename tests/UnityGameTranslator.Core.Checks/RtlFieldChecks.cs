using System;
using System.Linq;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A right-to-left text being EDITED (RtlFieldLayout): what the field shows, and the map that
    /// puts the caret, a click and the arrow keys on the right letter.
    ///
    /// ⚠ The display strings are held to the composer's, whose own expected forms come from an
    /// independent implementation (TextShapingChecks) — two routes to one answer, not the code
    /// read back. The caret cases are written from the rule stated in plain words (before a
    /// right-to-left letter is its right edge; the arrows follow the screen), not from the code.
    ///
    /// Nobody on this project can type or read Arabic at the screen (user, 2026-09-25): these
    /// cases are the proof the mechanism is right before anybody looks at a game.
    /// </summary>
    internal static class RtlFieldChecks
    {
        // Same sentences as TextShapingChecks.
        private const string ShortLogical = "مرحبا بكم في عالم الترجمة";
        private const string ShortVisual = "ﺔﻤﺟﺮﺘﻟﺍ ﻢﻟﺎﻋ ﻲﻓ ﻢﻜﺑ ﺎﺒﺣﺮﻣ";
        private const string MixedLogical = "الإصدار 123 من ABC جاهز الآن";
        private const string MixedVisual = "ﻥﻵﺍ ﺰﻫﺎﺟ ABC ﻦﻣ 123 ﺭﺍﺪﺻﻹﺍ";

        public static void Run(Action<bool, string, string> check)
        {
            WhatTheShaperMaps(check);
            WhatTheFieldShows(check);
            WhereTheCaretGoes(check);
            WhereAClickLands(check);
            HowTheArrowsMove(check);
            SeveralLines(check);
            ForAnEngineThatMovesGlyphs(check);
        }

        /// <summary>
        /// TMP's input field reads positions back from its label by index, so its label is given the
        /// shaped text one-for-one with what was typed, and only the glyphs are moved afterwards.
        /// </summary>
        private static void ForAnEngineThatMovesGlyphs(Action<bool, string, string> check)
        {
            var prep = RtlFieldLayout.Prepare("السلام لا بأس");
            string padded = prep.PaddedShaped();
            check(padded.Length == "السلام لا بأس".Length,
                "the padded label is as long as the typed text",
                "every drawn character's index is the typed one's — the field's own editing stays right");
            // "السلام": alef, lam, seen, LAM, ALEF, meem — the ligature is at 3, the alef at 4.
            check(padded[3] != 'ل' && padded[4] == RtlFieldLayout.ZeroWidthSpace,
                "a lam-alef: the ligature in the lam's slot, a zero-width space in the alef's",
                "the glyph where it is drawn, nothing visible where it merged");

            var mixed = RtlFieldLayout.Prepare("abc مرحبا");
            check(mixed.PaddedShaped().StartsWith("abc "),
                "Latin stays as typed",
                "only the right-to-left letters take their shaped forms");

            var layout = mixed.Lay(null);
            var onScreen = layout.LogicalOnScreen(0);
            check(onScreen.SequenceEqual(new[] { 0, 1, 2, 3, 8, 7, 6, 5, 4 }),
                "left to right on screen: abc, the space, then the Arabic word from its end",
                "the order the glyphs are moved into");

            // A break reported by an engine right after a hard line break is not a wrap.
            var hard = RtlFieldLayout.Prepare("مرحبا\nعالم");
            var withStart = hard.LayAtLogical(new[] { 6 });
            check(withStart.LineCount == 2,
                "a line start after a hard break adds no empty line",
                "engines report every line start, hard ones included");
        }

        private static void WhatTheShaperMaps(Action<bool, string, string> check)
        {
            var shaper = new PresentationFormsShaper();

            string plain = shaper.ShapeWithMap("مرحبا", out int[] m1);
            check(plain == shaper.Shape("مرحبا") && m1 != null && m1.SequenceEqual(new[] { 0, 1, 2, 3, 4 }),
                "letters shaped one for one keep their places",
                "the map is the identity where nothing merges, and the text is Shape's");

            string lamAlef = shaper.ShapeWithMap("لا", out int[] m2);
            check(lamAlef.Length == 1 && m2 != null && m2.SequenceEqual(new[] { 0, 0 }),
                "lam + alef: two typed letters, one glyph",
                "both characters point at the ligature — the caret steps over it whole");

            // beh, shadda, fatha — in the order the shaper pairs them (shadda first).
            string shadda = shaper.ShapeWithMap("بَّ", out int[] m3);
            check(m3 != null && m3.Length == 3 && m3[1] == m3[2],
                "shadda + haraka: one combined sign",
                "the two marks point at the one sign that shows them");
        }

        private static void WhatTheFieldShows(Action<bool, string, string> check)
        {
            check(RtlFieldLayout.Prepare("Hello world") == null,
                "no right-to-left letter: nothing to present",
                "a Latin field is left exactly as Unity draws it");

            check(RtlFieldLayout.Prepare(ShortLogical)?.Lay(null)?.Display == ShortVisual,
                "an Arabic line shows in visual order, shaped",
                "the same string the composer (and the independent reference) gives");

            check(RtlFieldLayout.Prepare(MixedLogical)?.Lay(null)?.Display == MixedVisual,
                "digits and Latin inside Arabic read forward",
                "the reference visual form of the mixed sentence");

            var token = RtlFieldLayout.Prepare("مرحبا [!v*1] عالم")?.Lay(null);
            check(token != null && token.Display.Contains("[!v*1]"),
                "a placeholder stays readable, left to right",
                "in a field it is text somebody may put the caret next to, never a reversed bracket soup");

            var tag = RtlFieldLayout.Prepare("<b>مرحبا</b>")?.Lay(null);
            check(tag != null && tag.Display.Contains("<b>") && tag.Display.Contains("</b>"),
                "a tag is shown as typed",
                "a field edits the raw text: its tags are characters, not styling");
        }

        private static void WhereTheCaretGoes(Action<bool, string, string> check)
        {
            var layout = RtlFieldLayout.Prepare(ShortLogical).Lay(null);
            int last = layout.Display.Length - 1;

            layout.CaretAnchor(0, out int d0, out bool right0, out _);
            check(d0 == last && right0,
                "at the start of an Arabic line the caret is on the far right",
                "before the first letter, which is drawn rightmost: its right edge");

            layout.CaretAnchor(ShortLogical.Length, out int dn, out bool rightN, out _);
            check(dn == 0 && !rightN,
                "at the end it is on the far left",
                "after the last letter, drawn leftmost: its left edge");

            // "abc مرحبا": a left-to-right line with an Arabic word at its end.
            var mixed = RtlFieldLayout.Prepare("abc مرحبا").Lay(null);
            mixed.CaretAnchor(9, out int dEnd, out bool rEnd, out _);
            check(dEnd == 4 && !rEnd,
                "the end of \"abc مرحبا\" is right after \"abc \"",
                "the last letter typed is drawn at the left of the Arabic word");
        }

        private static void WhereAClickLands(Action<bool, string, string> check)
        {
            var layout = RtlFieldLayout.Prepare(ShortLogical).Lay(null);
            int last = layout.Display.Length - 1;

            check(layout.CaretFromHit(last, rightHalf: true) == 0,
                "right half of the rightmost letter: before it",
                "in right-to-left text the right half comes first");
            check(layout.CaretFromHit(last, rightHalf: false) == 1,
                "left half of it: after it",
                "the caret goes where the next letter will be typed");

            var lamAlef = RtlFieldLayout.Prepare("لا").Lay(null);
            check(lamAlef.CaretFromHit(0, rightHalf: true) == 0 && lamAlef.CaretFromHit(0, rightHalf: false) == 2,
                "a click on a lam-alef lands before or after BOTH letters",
                "never between two letters drawn as one");
        }

        private static void HowTheArrowsMove(Action<bool, string, string> check)
        {
            var layout = RtlFieldLayout.Prepare(ShortLogical).Lay(null);

            check(layout.VisualStep(0, toRight: false) == 1,
                "← in an Arabic line moves forward in the text",
                "the arrows follow the screen (user, 2026-09-25)");
            check(layout.VisualStep(1, toRight: true) == 0,
                "→ moves back",
                "same rule, other way");
            check(layout.VisualStep(0, toRight: true) == 0,
                "→ at the start of a single line stays",
                "there is nothing further right");

            // "abc مرحبا": from between c and the space, → reaches the gap after the space,
            // which is where the END of the Arabic word is drawn.
            var mixed = RtlFieldLayout.Prepare("abc مرحبا").Lay(null);
            check(mixed.VisualStep(3, toRight: true) == 9,
                "→ from \"abc|\" goes to the visual gap after the space: the text's end",
                "moving by screen position, not by typing order");

            var lamAlef = RtlFieldLayout.Prepare("لا ب").Lay(null);
            check(lamAlef.VisualStep(0, toRight: false) == 2,
                "← over a lam-alef jumps both letters",
                "no stop inside a glyph nobody can see into");
        }

        private static void SeveralLines(Action<bool, string, string> check)
        {
            var hard = RtlFieldLayout.Prepare("مرحبا\nعالم").Lay(null);
            string[] lines = hard.Display.Split('\n');
            check(hard.LineCount == 2 && lines.Length == 2
                  && lines[0] == RtlComposer.Compose("مرحبا", RtlOutput.VisualOrder)
                  && lines[1] == RtlComposer.Compose("عالم", RtlOutput.VisualOrder),
                "a line break keeps two lines, each in visual order",
                "top line first — never the reversed stack of a whole-string reorder");

            hard.CaretAnchor(6, out int d6, out bool r6, out int line6);
            check(line6 == 1 && r6 && d6 == hard.LineDisplayEnd(1) - 1,
                "the caret after the break is at the right of the second line",
                "the start of the next Arabic line");

            // A soft wrap before the second word, as an engine would cut it.
            var prep = RtlFieldLayout.Prepare("مرحبا بكم");
            int wrapAt = prep.MeasureText.IndexOf(' ') + 1;
            var soft = prep.Lay(new[] { wrapAt });
            check(soft.LineCount == 2 && soft.LineOfCaret(6) == 1 && soft.LineOfCaret(5) == 0,
                "a soft wrap cuts where the engine cut",
                "the caret at the wrap belongs to the line where typing continues");

            int endOfFirst = 5;
            check(soft.VisualStep(endOfFirst, toRight: false) == 6,
                "← at the left end of an Arabic line goes to the next line",
                "off the edge of a line, onward in reading order");
        }
    }
}
