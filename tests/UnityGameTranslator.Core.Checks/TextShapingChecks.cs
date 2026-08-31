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

        public static void Run(Action<bool, string, string> check)
        {
            WhatAStringContains(check);
            WhatShapingProduces(check);
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
    }
}
