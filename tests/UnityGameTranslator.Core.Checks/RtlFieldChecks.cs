using System.Collections.Generic;
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
            WhatADoubleClickSelects(check);
            SeveralLines(check);
            ForAnEngineThatMovesGlyphs(check);
        }

        /// <summary>
        /// TMP's input field reads positions back from its label's characterInfo, so its label keeps
        /// the typed order, every character of it told the typed one it stands for, and only the
        /// glyphs are moved afterwards.
        /// </summary>
        private static void ForAnEngineThatMovesGlyphs(Action<bool, string, string> check)
        {
            var prep = RtlFieldLayout.Prepare("السلام لا بأس");
            string padded = prep.LabelFor(out var index, out var length);
            check(padded.Length == "السلام لا بأس".Length && index.SequenceEqual(Enumerable.Range(0, padded.Length)) && length.All(l => l == 1),
                "Arabic: the label is as long as the typed text, each character standing for its own",
                "every drawn character's index is the typed one's — the field's own editing stays right");
            // "السلام": alef, lam, seen, LAM, ALEF, meem — the ligature is at 3, the alef at 4.
            check(padded[3] != 'ل' && padded[4] == RtlFieldLayout.MergedSlot,
                "a lam-alef: the ligature in the lam's slot, a word joiner in the alef's",
                "nothing visible where it merged, and no place for TMP to cut the word: a zero-width space was one");

            // Brackets in a right-to-left field: TMP draws its label's characters as they are, so the
            // label gives "(" mirrored where the uGUI field's display mirrors it (found on the bench:
            // "(שמור)" shown with its brackets backwards, 2026-10-01).
            foreach (var text in new[] { "(שמור) את המשחק", "(احفظ) اللعبة" })
            {
                var bracketed = RtlFieldLayout.Prepare(text);
                var lay = bracketed.Lay(null);
                string tmpLabel = lay.MirroredLabel(bracketed.LabelFor(out var at, out _), at);
                var drawn = new System.Text.StringBuilder();
                foreach (int i in lay.LogicalOnScreen(0))
                    for (int k = 0; k < tmpLabel.Length; k++)
                        if (at[k] == i && tmpLabel[k] != RtlFieldLayout.MergedSlot) drawn.Append(tmpLabel[k]);
                check(drawn.ToString() == lay.Display.Replace(RtlFieldLayout.ZeroWidthSpace.ToString(), ""),
                    "brackets in a right-to-left field: TMP's label, moved into screen order, shows what the uGUI field shows",
                    drawn + " vs " + lay.Display);
            }

            var mixed = RtlFieldLayout.Prepare("abc مرحبا");
            check(mixed.LabelFor(out _, out _).StartsWith("abc "),
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

            // A number typed with its thousands separator stays one number in a field, as in a text
            // (RtlComposer.InsideNumber): the field's layout runs its own bidi pass, and it showed
            // "000'3" after the text's was fixed (NGUI field on the bench, 2026-10-03).
            var grouped = RtlFieldLayout.Prepare("حد 3'000 ذهب").Lay(null);
            check(grouped.Display.Contains("3'000"), "3'000 typed in a right-to-left field stays one number",
                  "shown: " + grouped.Display);

            // A whole line walked with one arrow, as somebody holding it down: every press moves the
            // caret the way the arrow points, and the walk passes every place a caret can stand
            // before it stops at the edge. Mixed lines — numbers, a placeholder, Latin, punctuation —
            // are where a step went the other way (NGUI field on the bench, 2026-10-03).
            foreach (string line in new[] { "הניקוד שלך: {0} נקודות.", "نقاط: {0:0} من 2000", "المستوى 25/50 +2.1",
                                            "زد <sprite=0> بـ 10%", "abc مرحبا def", "حد 3'000 لحاملي الذهب" })
            {
                var walked = RtlFieldLayout.Prepare(line).Lay(null);
                foreach (bool toRight in new[] { true, false })
                {
                    int caret = toRight ? walked.CaretAtLineSide(0, rightSide: false) : walked.CaretAtLineSide(0, rightSide: true);
                    var seen = new HashSet<int> { caret };
                    string wrong = null;
                    for (int press = 0; press <= line.Length + 1; press++)
                    {
                        int next = walked.VisualStep(caret, toRight);
                        if (next == caret) break;
                        int from = walked.BoundaryOf(caret), to = walked.BoundaryOf(next);
                        if (toRight ? to <= from : to >= from) { wrong = $"{caret}→{next} (gap {from}→{to})"; break; }
                        seen.Add(caret = next);
                    }
                    var visible = new HashSet<int>();
                    for (int c = 0; c <= line.Length; c++) visible.Add(walked.BoundaryOf(c));
                    var reached = new HashSet<int>();
                    foreach (int c in seen) reached.Add(walked.BoundaryOf(c));
                    check(wrong == null && reached.SetEquals(visible),
                          (toRight ? "→" : "←") + " walks \"" + line + "\" one gap at a time, end to end",
                          wrong != null ? "a step went the other way: " + wrong : $"reached {reached.Count} of {visible.Count} gaps");
                }
            }
        }

        /// <summary>The word a double-click takes: measured on the typed text (RtlFieldLayout.WordEdge).</summary>
        private static void WhatADoubleClickSelects(Action<bool, string, string> check)
        {
            string Word(string text, int at) =>
                text.Substring(RtlFieldLayout.WordEdge(text, at, false), RtlFieldLayout.WordEdge(text, at, true) - RtlFieldLayout.WordEdge(text, at, false));

            const string sentence = "الآن جاهز ABC من 123 الإصدار";
            check(Word(sentence, 7) == "جاهز", "a double-click on an Arabic word takes that word", Word(sentence, 7));
            check(Word(sentence, 11) == "ABC" && Word(sentence, 18) == "123", "a Latin word and a number in the same line are words of their own",
                  Word(sentence, 11) + " | " + Word(sentence, 18));
            // Vowel signs and joiners inside a word: the engines' own rule (char.IsLetterOrDigit) cut
            // the word at each of them.
            const string voweled = "كَتَبَ الدَّرْسَ";
            check(Word(voweled, 2) == "كَتَبَ" && Word(voweled, 9) == "الدَّرْسَ", "harakat stay inside their word",
                  Word(voweled, 2) + " | " + Word(voweled, 9));
            check(Word("می‌خواهم بروم", 1) == "می‌خواهم", "a zero-width non-joiner stays inside its word (Persian)", Word("می‌خواهم بروم", 1));
            check(Word("שלום, עולם", 4) == "," && Word("שלום, עולם", 1) == "שלום", "punctuation is a class of its own, as the engines have it",
                  Word("שלום, עולם", 4) + " | " + Word("שלום, עולם", 1));
            check(Word("ab\U0001F600\U0001F600 cd", 3) == "\U0001F600\U0001F600", "a character above U+FFFF is never cut in half", "");
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

            string[] softLines = soft.Display.Split('\n');
            check(softLines.Length == 2 && softLines[0].IndexOf(' ') < 0 && softLines[1].IndexOf(' ') < 0,
                "the space a soft wrap broke at is drawn on neither line",
                "kept, it went to the visual start of the right-to-left line and pushed it one space past its box");

            // A tag wider than the line: the engine cuts INSIDE it, as any word too long for its box.
            // Kept whole, the tag stayed on one line and pushed the rest out of the field.
            const string tagged = "أضف <color=#ADD8E6>0.2%</color> من";
            var tagPrep = RtlFieldLayout.Prepare(tagged);
            int tagAt = tagPrep.MeasureText.IndexOf("<color", StringComparison.Ordinal);
            int insideTag = tagAt + 8;                       // "<color=#" | "ADD8E6>"
            var splits = tagPrep.TokenSplitsAt(new[] { insideTag });
            int typedCut = tagged.IndexOf("<color", StringComparison.Ordinal) + 8;
            check(splits.Count == 1 && splits[0] == typedCut, "a wrap inside a tag is found where it was typed",
                  $"typed {(splits.Count > 0 ? splits[0] : -1)}, wanted {typedCut}");
            var whole = tagPrep.Lay(new[] { insideTag });
            var cut = tagPrep.SplitTokensAt(splits).Lay(new[] { insideTag });
            check(cut.LineCount == 2 && cut.LineOfCaret(typedCut) == 1 && cut.LineOfCaret(typedCut - 1) == 0,
                  "a tag cut by the engine is cut there too",
                  $"the cut falls between \"<color=#\" and \"ADD8E6>\" (kept whole: lines {whole.LineOfCaret(typedCut - 1)} and {whole.LineOfCaret(typedCut)} — one line, wider than the box)");
            check(cut.Display.Replace("\n", "").Contains("ADD8E6>") && cut.Display.Contains("<color=#"),
                  "both pieces of the cut tag are drawn as typed", "\"<color=#\" and \"ADD8E6>\" on the screen");

            int endOfFirst = 5;
            check(soft.VisualStep(endOfFirst, toRight: false) == 6,
                "← at the left end of an Arabic line goes to the next line",
                "off the edge of a line, onward in reading order");

            // Up / Down keep the column ON SCREEN. Each glyph one unit wide here: x = the caret's gap
            // from the left of its line. "مرحبا" over "عالم": after "مر" is 3 from the left of the
            // top line; below, 3 from the left is after "ع" — typed position 7.
            Func<int, float> x = c => hard.BoundaryOf(c) - hard.LineDisplayStart(hard.LineOfCaret(c));
            int down = hard.VerticalStep(2, down: true, goalX: x(2), xOf: x);
            check(down == 7 && hard.LineOfCaret(down) == 1, "↓ goes to the line below, in the same column on screen",
                  $"typed {down}, the column of the caret above");
            check(hard.VerticalStep(down, down: false, goalX: x(2), xOf: x) == 2, "↑ brings it back to where it was", "the column is kept between presses");
            check(hard.VerticalStep(2, down: false, goalX: x(2), xOf: x) == -1 && hard.VerticalStep(down, down: true, goalX: x(2), xOf: x) == -1,
                  "no line above the first or below the last: the field decides", "");
        }
    }
}
