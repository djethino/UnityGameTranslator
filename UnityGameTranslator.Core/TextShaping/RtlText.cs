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
        /// (unshaped) form, of ANY script written right to left. This is the trigger for the
        /// presentation pass.
        /// </summary>
        public static bool ContainsStrongRtl(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 0x80) continue;   // ASCII has no right-to-left character
                // A right-to-left override counts: a shaped RTL run is named by private codepoints
                // that carry no direction of their own, and the override is what says it (OpenTypeText).
                if (c == OpenTypeText.RightToLeftOverride) return true;
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(c, text[++i]);
                if (IsStrongRtl(cp)) return true;
            }
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
                if (IsPresentationForm(text[i])) return true;
            return false;
        }

        /// <summary>
        /// A presentation form: Unicode's blocks of letters already shaped. They exist for Hebrew
        /// and Arabic only — by Unicode's design, no other script has any — so naming those blocks
        /// is a fact about Unicode, not a choice of scripts.
        /// </summary>
        private static bool IsPresentationForm(char c)
        {
            if (c < 'יִ') return false;
            if (c <= 'ﭏ') return true;                    // Hebrew presentation forms
            if (c >= 'ﭐ' && c <= '﷿') return true;   // Arabic presentation forms A
            if (c >= 'ﹰ' && c <= '﻿') return true;   // Arabic presentation forms B
            return false;
        }

        /// <summary>
        /// The one question the pipeline asks per outgoing string: does it need the presentation
        /// pass? Strong RTL present, and not shaped already.
        /// </summary>
        public static bool NeedsPresentation(string text)
            => ContainsStrongRtl(text) && !ContainsPresentationForms(text);

        /// <summary>
        /// A strong right-to-left letter in base form: Unicode's bidi class R or AL, for every
        /// script written right to left (Hebrew, Arabic, Syriac, Thaana, N'Ko, Adlam, Samaritan…).
        /// 🔴 Not a list of blocks: one typed here once said Hebrew and Arabic only, and every
        /// other right-to-left script was shown left to right (2026-10-02). Presentation forms are
        /// deliberately NOT triggers — they mean "already shaped" and are answered by
        /// <see cref="ContainsPresentationForms"/>.
        /// </summary>
        public static bool IsStrongRtl(int cp)
        {
            if (cp < 0x80) return false;
            if (cp <= 0xFFFF && IsPresentationForm((char)cp)) return false;
            var direction = Topten.RichTextKit.UnicodeClasses.Directionality(cp);
            return direction == Topten.RichTextKit.Directionality.R || direction == Topten.RichTextKit.Directionality.AL;
        }
    }
}
