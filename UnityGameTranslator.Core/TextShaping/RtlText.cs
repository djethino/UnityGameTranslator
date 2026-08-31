using System;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Stage A of the shaping pipeline: what does this STRING contain — never what language the
    /// user configured. Content decides, because the target language can be "auto", because a
    /// game already written in Arabic must be recognized when translating OUT of it, and because
    /// the project rule forbids language-specific logic (a SCRIPT detected in content is not a
    /// language): see analyse/issue-24-rtl-second-look.md §7.1 and the 06/08 analysis §3.5.
    ///
    /// ⚠ Every range is written as \uXXXX on purpose: literal RTL characters inside comparisons
    /// are unreadable in any editor (the bidi algorithm reorders the source line itself) and
    /// unverifiable in review.
    ///
    /// PURE by contract — no Unity, no state, no clock — so it is linked into
    /// tests/UnityGameTranslator.Core.Checks like TextRelations is.
    /// </summary>
    public static class RtlText
    {
        /// <summary>
        /// True when the string carries at least one strong right-to-left letter in base
        /// (unshaped) form: Hebrew, Arabic and its extensions. This is the trigger for the
        /// presentation pass.
        /// </summary>
        public static bool ContainsStrongRtl(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
                if (IsStrongRtl(text[i])) return true;
            return false;
        }

        /// <summary>
        /// True when the string already carries presentation forms — Arabic FB50–FDFF / FE70–FEFF
        /// or Hebrew FB1D–FB4F.
        ///
        /// 🔴 The "never shape twice" guard: a game that embeds its own RTL support (RTLTMPro
        /// overrides `text` and hands the BASE setter an already-shaped string — verified in its
        /// source) delivers such text to our hooks. Shaping it again would destroy it, and
        /// treating it as source text would send presentation forms to a translation backend.
        /// </summary>
        public static bool ContainsPresentationForms(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 'יִ') continue;
                if (c <= 'ﭏ') return true;                      // Hebrew presentation forms
                if (c >= 'ﭐ' && c <= '﷿') return true;     // Arabic presentation forms A
                if (c >= 'ﹰ' && c <= '﻿') return true;     // Arabic presentation forms B
            }
            return false;
        }

        /// <summary>
        /// The one question the pipeline asks per outgoing string: does it need the presentation
        /// pass? Strong RTL present, and not shaped already.
        /// </summary>
        public static bool NeedsPresentation(string text)
            => ContainsStrongRtl(text) && !ContainsPresentationForms(text);

        /// <summary>
        /// A strong RTL letter in base form. Blocks, not languages: Hebrew 0590–05FF, Arabic
        /// 0600–06FF, Arabic Supplement 0750–077F, Arabic Extended-B 0870–089F and Extended-A
        /// 08A0–08FF. Presentation forms are deliberately NOT triggers here — they mean "already
        /// shaped" and are answered by <see cref="ContainsPresentationForms"/>.
        /// </summary>
        public static bool IsStrongRtl(char c)
        {
            if (c < '֐') return false;                          // fast path: Latin & co.
            if (c <= '׿') return true;                          // Hebrew
            if (c >= '؀' && c <= 'ۿ') return true;         // Arabic
            if (c >= 'ݐ' && c <= 'ݿ') return true;         // Arabic Supplement
            if (c >= 'ࡰ' && c <= 'ࣿ') return true;         // Arabic Extended-B + A
            return false;
        }

        /// <summary>
        /// True when the string contains the Farsi yeh (U+06CC) — the one letter whose base form
        /// differs between Arabic and Persian spelling. The vendored GlyphFixer normalizes yeh
        /// one way or the other and must be told which; deciding FROM CONTENT keeps this free of
        /// any configured language. ⚠ Open question flagged in PresentationFormsShaper: a text
        /// genuinely mixing both yehs gets one of them normalized.
        /// </summary>
        public static bool PrefersFarsiForms(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == 'ی') return true;
            return false;
        }
    }
}
