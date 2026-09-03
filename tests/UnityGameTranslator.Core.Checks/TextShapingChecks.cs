using System;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Stage A (what a string contains) and stage B1 (Arabic presentation-form shaping).
    ///
    /// ⚠ The expected shaped strings were produced by an INDEPENDENT implementation
    /// (python arabic_reshaper 3.x + python-bidi, 2026-08-28, see
    /// analyse/issue-24-rtl-second-look.md §7.3 bis) — not by reading our code back. Agreement
    /// between two unrelated shapers on fixed sentences is the strongest check available without
    /// a font on screen; the same strings were then SEEN rendering correctly on the bench
    /// (avia3, silk8-10), which ties these constants to reality.
    /// </summary>
    internal static class TextShapingChecks
    {
        // "مرحبا بكم في عالم الترجمة" — logical order, and its shaped form in logical order
        // (pure Arabic: no LTR run, so the shaped-logical form IS the bench's TmpMode string).
        private const string ShortLogical = "مرحبا بكم في عالم الترجمة";
        private const string ShortShaped = "ﻣﺮﺣﺒﺎ ﺑﻜﻢ ﻓﻲ ﻋﺎﻟﻢ ﺍﻟﺘﺮﺟﻤﺔ";

        // "الإصدار 123 من ABC جاهز الآن" — digits and Latin must come out untouched from B1
        // (their reversal for RTL display is stage C's job, not shaping's). Carries both lam-alef
        // ligature cases: lam + alef-hamza-below (ﻹ) and lam + alef-madda (ﻵ).
        private const string MixedLogical = "الإصدار 123 من ABC جاهز الآن";
        private const string MixedShaped = "ﺍﻹﺻﺪﺍﺭ 123 ﻣﻦ ABC ﺟﺎﻫﺰ ﺍﻵﻥ";

        // The long bench paragraph — sentence punctuation must stay where it stands in logical
        // order.
        private const string LongLogical = "مرحبا بكم في عالم الترجمة. هذه فقرة طويلة كتبت لاختبار الانتقال التلقائي إلى السطر التالي وترتيب الأسطر عند العرض من اليمين إلى اليسار داخل اللعبة.";
        private const string LongShaped = "ﻣﺮﺣﺒﺎ ﺑﻜﻢ ﻓﻲ ﻋﺎﻟﻢ ﺍﻟﺘﺮﺟﻤﺔ. ﻫﺬﻩ ﻓﻘﺮﺓ ﻃﻮﻳﻠﺔ ﻛﺘﺒﺖ ﻻﺧﺘﺒﺎﺭ ﺍﻻﻧﺘﻘﺎﻝ ﺍﻟﺘﻠﻘﺎﺋﻲ ﺇﻟﻰ ﺍﻟﺴﻄﺮ ﺍﻟﺘﺎﻟﻲ ﻭﺗﺮﺗﻴﺐ ﺍﻷﺳﻄﺮ ﻋﻨﺪ ﺍﻟﻌﺮﺽ ﻣﻦ ﺍﻟﻴﻤﻴﻦ ﺇﻟﻰ ﺍﻟﻴﺴﺎﺭ ﺩﺍﺧﻞ ﺍﻟﻠﻌﺒﺔ.";

        // The bench's other two reference forms (same provenance): fully visual order, and the
        // isRightToLeftText form (visual reversed whole — LTR runs read forward again).
        private const string ShortVisual = "ﺔﻤﺟﺮﺘﻟﺍ ﻢﻟﺎﻋ ﻲﻓ ﻢﻜﺑ ﺎﺒﺣﺮﻣ";
        private const string MixedVisual = "ﻥﻵﺍ ﺰﻫﺎﺟ ABC ﻦﻣ 123 ﺭﺍﺪﺻﻹﺍ";
        private const string MixedFlagged = "ﺍﻹﺻﺪﺍﺭ 321 ﻣﻦ CBA ﺟﺎﻫﺰ ﺍﻵﻥ";

        public static void Run(Action<bool, string, string> check)
        {
            WhatAStringContains(check);
            WhatShapingProduces(check);
            WhatComposingProduces(check);
            WhatUnderlineStrippingDoes(check);
        }

        /// <summary>
        /// The DrawUnderlineMesh crash guard (Timberborn bench, 2026-09-02): underline and
        /// strikethrough tags — those two exactly, nothing that merely starts with the same
        /// letter — are dropped from RTL text bound for the UI Toolkit standard generator.
        /// </summary>
        private static void WhatUnderlineStrippingDoes(Action<bool, string, string> check)
        {
            check(RtlComposer.StripUnderlineTags("<u>مرحبا</u>") == "مرحبا",
                "an underline pair is dropped, its content kept",
                "the crash guard: Unity's underline mesh dies on Arabic, the text must not");
            check(RtlComposer.StripUnderlineTags("<S>a</S>") == "a",
                "strikethrough too, either case",
                "one TextCore routine draws both features — same crash, same guard");
            check(RtlComposer.StripUnderlineTags("<u><color=red>نص</color></u>") == "<color=red>نص</color>",
                "other tags pass through untouched",
                "only the two dangerous tags leave; color/size/b/i are the composer's business");
            string ul = "<ul>x</ul>";
            check(ReferenceEquals(RtlComposer.StripUnderlineTags(ul), ul),
                "<ul> is not <u> — same first letter, different tag",
                "and the untouched case returns the SAME instance, so the caller can tell");
            string cmp = "a < b";
            check(ReferenceEquals(RtlComposer.StripUnderlineTags(cmp), cmp),
                "a bare '<' is left alone",
                "comparison text is not markup");
        }

        private static void WhatAStringContains(Action<bool, string, string> check)
        {
            check(RtlText.IsStrongRtl('م'), "Arabic letter is strong RTL",
                "meem, U+0645, base block");
            check(RtlText.IsStrongRtl('ש'), "Hebrew letter is strong RTL",
                "shin, U+05E9");
            check(!RtlText.IsStrongRtl('A'), "Latin letter is not strong RTL",
                "the fast path must reject everything below U+0590");
            check(!RtlText.IsStrongRtl('ﻣ'), "a presentation form is NOT a strong trigger",
                "U+FEE3 means 'already shaped' — that question has its own answer");

            check(RtlText.NeedsPresentation(ShortLogical), "logical Arabic needs the pass",
                "this is the string the pipeline must transform");
            check(!RtlText.NeedsPresentation(ShortShaped), "shaped Arabic never re-enters",
                "the never-shape-twice guard: a game with its own RTL support hands us this");
            check(!RtlText.NeedsPresentation("Hello 123"), "plain LTR is left alone",
                "zero cost on the overwhelming majority of texts");
            check(RtlText.NeedsPresentation("abc مرحبا def"), "mixed text still needs the pass",
                "one strong RTL letter is enough — runs are cut later");
            check(!RtlText.NeedsPresentation(""), "empty is left alone", "no crash, no work");

            check(RtlText.PrefersFarsiForms("فارسی"), "Farsi yeh detected from content",
                "U+06CC present — the one letter the vendored fixer must be told about");
            check(!RtlText.PrefersFarsiForms(ShortLogical), "Arabic yeh does not read as Farsi",
                "U+064A only");
        }

        private static void WhatShapingProduces(Action<bool, string, string> check)
        {
            var shaper = new PresentationFormsShaper();

            check(shaper.Shape(ShortLogical) == ShortShaped,
                "pure Arabic sentence shapes to the reference",
                "agreement with arabic_reshaper, seen rendering on the bench");

            check(shaper.Shape(MixedLogical) == MixedShaped,
                "digits and Latin come out untouched, lam-alef ligated",
                "B1 shapes letters only — reordering LTR runs is stage C");

            check(shaper.Shape(LongLogical) == LongShaped,
                "long paragraph shapes to the reference",
                "sentence periods stay at their logical positions");

            check(shaper.Shape(ShortShaped) == ShortShaped,
                "an already-shaped string passes through unchanged",
                "presentation forms are not in the joining tables — belt to the guard's braces");

            check(shaper.Shape("שלום עולם") == "שלום עולם",
                "Hebrew passes through unchanged",
                "no joining in Hebrew — its work is all in stages C and D");

            check(shaper.Shape("Hello") == "Hello",
                "plain LTR passes through unchanged",
                "the shaper itself must be harmless, not only gated");
        }

        /// <summary>
        /// Stage C end to end: shaping + UAX#9 + reordering + mirroring + token protection.
        /// The flagged references are what python-bidi's visual order gives once reversed — a
        /// second independent implementation of UAX#9 agreeing character for character.
        /// </summary>
        private static void WhatComposingProduces(Action<bool, string, string> check)
        {
            check(Topten.RichTextKit.UnicodeClasses.Directionality(0xF100) == Topten.RichTextKit.Directionality.L
                  && Topten.RichTextKit.UnicodeClasses.Directionality(0xF8FF) == Topten.RichTextKit.Directionality.L,
                "a PUA sentinel resolves as class L (F100..F8FF)",
                "the whole token-protection scheme rests on this UCD fact");

            // A private glyph codepoint inside a Hebrew word (a positioned mark from FontShaping)
            // rides with the letter before it: the run stays one run, and reversal puts the
            // mark before its base — the order its offsets were computed for.
            string hebrewWithVariant = "אב";
            string composedVariant = RtlComposer.Compose(hebrewWithVariant, RtlOutput.VisualOrder);
            check(composedVariant.IndexOf("בא", System.StringComparison.Ordinal) >= 0,
                "a private glyph codepoint keeps an RTL run whole", Escape(composedVariant));

            check(RtlComposer.Compose(ShortLogical, RtlOutput.RtlFlagged) == ShortShaped,
                "pure Arabic, flagged form == shaped logical",
                "no LTR run to move: composing must add nothing");

            check(RtlComposer.Compose(ShortLogical, RtlOutput.VisualOrder) == ShortVisual,
                "pure Arabic, visual form matches python-bidi",
                "two unrelated UAX#9 implementations agree");

            check(RtlComposer.Compose(MixedLogical, RtlOutput.VisualOrder) == MixedVisual,
                "mixed sentence, visual form matches python-bidi",
                "digits and Latin hold their forward order inside the reversal");

            check(RtlComposer.Compose(MixedLogical, RtlOutput.RtlFlagged) == MixedFlagged,
                "mixed sentence, flagged form matches the bench string",
                "the exact string avia4/silk9 rendered correctly in game");

            string withPlaceholder = RtlComposer.Compose("مرحبا [!v*0] بكم", RtlOutput.RtlFlagged);
            check(withPlaceholder.Contains("[!v*0]"),
                "a placeholder survives composing, verbatim",
                "decision D7: this layer exists because it knows our placeholders");

            // Tags are STRUCTURE: whatever the display direction does to the text, the markup
            // must stay parse-valid in string order — opening first, wrapping the same glyphs.
            // The bench (biopb) showed the sentinel approach mangling exactly this.
            foreach (var mode in new[] { RtlOutput.RtlFlagged, RtlOutput.VisualOrder })
            {
                string wrapped = RtlComposer.Compose("<color=red>مرحبا بكم</color>", mode);
                check(wrapped.StartsWith("<color=red>") && wrapped.EndsWith("</color>"),
                    $"a whole-string tag pair stays wrapping it all ({mode})",
                    "opening first, closing last, in STRING order");

                string mid = RtlComposer.Compose("مرحبا <b>بكم</b> في عالم", mode);
                int o = mid.IndexOf("<b>", StringComparison.Ordinal);
                int c = mid.IndexOf("</b>", StringComparison.Ordinal);
                check(o >= 0 && c > o,
                    $"a mid-sentence pair stays ordered and adjacent to its word ({mode})",
                    "the pair re-wraps the final positions of the glyphs it styled");

                string two = RtlComposer.Compose("<b>مرحبا</b> <i>بكم</i>", mode);
                check(two.IndexOf("<b>", StringComparison.Ordinal) < two.IndexOf("</b>", StringComparison.Ordinal)
                      && two.IndexOf("<i>", StringComparison.Ordinal) < two.IndexOf("</i>", StringComparison.Ordinal),
                    $"two sibling pairs each stay valid ({mode})",
                    "adjacent L-classed tags used to swap during reordering");

                string lone = RtlComposer.Compose("مرحبا <sprite=3> بكم", mode);
                check(lone.Contains("<sprite=3>"),
                    $"an unpaired tag survives verbatim ({mode})",
                    "sprite/br tags have no closing half to pair with");
            }

            check(RtlComposer.Compose("مرحبا (بكم)", RtlOutput.VisualOrder).Contains(")"),
                "brackets mirror at RTL levels",
                "UAX#9 L4 via the trie's paired-bracket data");

            check(RtlComposer.ShapeLogicalOnly(MixedLogical) == MixedShaped,
                "shape-only output equals the shaper's reference",
                "the measuring form for no-flag engines: shaped, logical, nothing moved");

            string measuring = RtlComposer.ShapeLogicalOnly("مرحبا [!v*0] بكم");
            check(measuring.Contains("[!v*0]"),
                "shape-only keeps placeholders verbatim",
                "pass 2 slices this string; a mangled token would slice wrong");
        }
    
        private static string Escape(string text)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in text) sb.Append(c < 128 ? c.ToString() : $"<{(int)c:X4}>");
            return sb.ToString();
        }
}
}
