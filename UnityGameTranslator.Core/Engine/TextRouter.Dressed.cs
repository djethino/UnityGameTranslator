using System;
using System.Collections.Generic;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    public sealed partial class TextRouter
    {
        // === A KNOWN SENTENCE UNCOVERED BY MARKUP ===
        //
        // Some games write a finished sentence and uncover it by moving the opening of one tag
        // along it (`Can y<color=#00000000>ou help?</color>`), ending on the sentence with an empty
        // tag after it. TextRelations.SameContent already reads those frames as ONE sentence when
        // it is new. When it is KNOWN, every frame used to miss: the player read the source being
        // uncovered, and the frame the reveal held when the last one was recognised was sent —
        // a fragment paid for at every showing (measured 2026-09-27: `power<color…>.</color>`, then
        // `powe<color…>r.</color>`), and a second key for a sentence the file held bare.
        //
        // Now a frame of a known sentence shows its translation, with the game's own tag placed
        // after the same share of visible characters (Markup.RevealUnder): the translation is
        // uncovered at the game's pace. No language and no tag is interpreted — the tag is copied.
        //
        // ⚠ **Only where this component is seen doing it.** A text whose last word is coloured is
        // a text; one whose markup moved over the same content since the frame before is a
        // reveal. The proof is the component's own previous frame, or the text it was last shown
        // a translation for — as for the heads of a reveal (TextRouter.Heads), decided on what the
        // component does, never on the text.
        //
        // ⚠ Only a span that runs to the END: a reveal. A span in the middle (a highlighted word
        // moving) points at a WORD, and a share of characters would light another one in the
        // translation — left alone until a game shows it.

        /// <summary>
        /// This component has just shown the same content with its markup elsewhere: the frame
        /// before in a reveal it is following, or the source it was last shown a translation for.
        /// Asked BEFORE <see cref="NoteTextSeen"/>, which moves the first of the two onto this text.
        /// </summary>
        private bool MarkupMovedHere(long compId, string text)
        {
            var state = PeekState(compId);
            if (state == null) return false;
            string before = state.Mode == TextMode.Typewriter ? state.TypewritingText : null;
            if (before != null && before != text && TextRelations.SameContent(before, text)) return true;
            string shown = state.ReadBackSource;
            return shown != null && shown != text && TextRelations.SameContent(shown, text);
        }

        /// <summary>
        /// The translation of a known sentence, uncovered as far as this frame uncovers its source;
        /// null when the frame does not end on a span, or its sentence is not known. The sentence
        /// is looked up bare, then as the frame a reveal ends on (the sentence and the empty tag):
        /// either may be the file's key.
        /// </summary>
        private string KnownUnderSpan(string text, bool isOwnUI, IDictionary<string, TranslationEntry> store)
        {
            if (!Markup.TrailingSpan(text, out int openStart, out int openLength, out int closeStart)) return null;
            string open = text.Substring(openStart, openLength);
            string close = text.Substring(closeStart);
            string uncovered = text.Substring(0, openStart);
            string whole = uncovered + text.Substring(openStart + openLength, closeStart - openStart - openLength);

            string translation = TranslationOf(whole, isOwnUI, store);
            if (translation == null)
            {
                translation = TranslationOf(whole + open + close, isOwnUI, store);
                if (translation != null && translation.EndsWith(open + close, StringComparison.Ordinal))
                    translation = translation.Substring(0, translation.Length - open.Length - close.Length);
            }
            if (translation == null) return null;

            return Markup.RevealUnder(translation, open, close, Markup.Strip(uncovered).Length, Markup.Strip(whole).Length);
        }

        /// <summary>The ladder's answer for a text, when it has a translation — nothing else.</summary>
        private string TranslationOf(string text, bool isOwnUI, IDictionary<string, TranslationEntry> store)
        {
            var look = TextGate.Lookup(text, isOwnUI, store, _host.NormalizeNumbers, _host.Variables, _host.MatchPattern);
            return look.Outcome == GateOutcome.Hit ? look.Value : null;
        }
    }
}
