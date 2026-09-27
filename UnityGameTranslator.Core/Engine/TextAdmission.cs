using UnityGameTranslator.Common;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core
{
    /// <summary>What a text is refused for, at the queue's door or at the store.</summary>
    public enum Admission
    {
        Admitted,
        /// <summary>Right-to-left text already shaped for display: our own output read back, or a game shipping its own shaping.</summary>
        PresentationForms,
        /// <summary>Nothing but digits, symbols and placeholders: the same in every language.</summary>
        NumericOrSymbol,
        /// <summary>A template the game expands in place (TextRouter.Templates).</summary>
        Template,
        /// <summary>Longer than any backend accepts.</summary>
        TooLong,
        /// <summary>Already a translation of ours, read back from the screen.</summary>
        AlreadyTarget,
        /// <summary>Given up this session: asking again would only repeat a known failure.</summary>
        GivenUp,
    }

    /// <summary>The facts the admission reads, from whichever engine and file are running.</summary>
    public interface IAdmissionFacts
    {
        /// <summary>A template the game expands in place (TextRouter.IsExpandedInPlace).</summary>
        bool IsExpandedInPlace(string text);
        /// <summary>This text is already in the target language: one of our translations, however decorated.</summary>
        bool IsAlreadyTarget(string text);
        /// <summary>This key reads as one of our translations, re-decorated (ReadbackIndex.IsReadback).</summary>
        bool IsReadback(string key, bool ownUi);
        /// <summary>The queue gave this text up this session.</summary>
        bool WasGivenUp(string text, bool ownUi);
    }

    /// <summary>
    /// Whether a text may become a request to the backend, or an answer may become an entry of the
    /// file — the refusals that depend on the TEXT alone, in the order they are asked.
    ///
    /// 🔴 **One rule for every engine and for the replay.** These lived in TranslatorCore, mixed
    /// with the refusals that depend on the mod's state (switched off, offline, the server silent),
    /// so the routing corpus could not see them and its replay host copied the two it needed. The
    /// state stays with the caller; the text is decided here.
    ///
    /// ⚠ It decides, it never logs: saying why a text was refused (once per text, for the long
    /// ones) is the caller's, which knows its log.
    /// </summary>
    public static class TextAdmission
    {
        /// <summary>At the queue's door: may this text be sent?</summary>
        public static Admission ForQueue(string text, bool ownUi, IAdmissionFacts facts)
        {
            // 🔴 Presentation forms never enter the queue — so they can never become a cache KEY.
            // Two ways such text reaches a gate: our own composed output read back during the
            // short window where a cache reload emptied the presented→logical table, and a game
            // that ships its own RTL support (RTLTMPro hands the base setter shaped strings). The
            // first is ours and must be dropped; the second is a real source this project cannot
            // translate yet (unshaping is ambiguous — issue #24 scope, §6.4-4).
            if (RtlText.ContainsPresentationForms(text)) return Admission.PresentationForms;

            if (TextNormalization.IsNumericOrSymbol(text)) return Admission.NumericOrSymbol;

            // A template the game expands in place: nothing queued at all — no line in the notice
            // that says a translation is running, and no call.
            if (facts.IsExpandedInPlace(text)) return Admission.Template;

            // Longer than any backend will accept. Refused here rather than deeper down, where the
            // refusal used to be stored as an entry tagged "S": the source text twice in a file
            // that is uploaded and shown, under the tag that means "a human kept this as it is".
            if (text.Length > Limits.AiTextLength) return Admission.TooLong;

            // The last line of defence against our own translation read back and sent again: the
            // single door, because guarding the callers one by one kept leaving another route open.
            // Own UI is exempt — its labels are source text we produce, never a read-back.
            if (!ownUi && facts.IsAlreadyTarget(text)) return Admission.AlreadyTarget;

            // Given up this session: what reopens it is a person asking again, never another hover.
            if (facts.WasGivenUp(text, ownUi)) return Admission.GivenUp;

            return Admission.Admitted;
        }

        /// <summary>At the store: may this answer become an entry?</summary>
        public static Admission ForStore(string key, bool ownUi, IAdmissionFacts facts)
        {
            // A template the game expands in place. The proof arrives with the expansion, usually
            // before the model answers — but nothing guarantees it, so the answer is refused HERE
            // as well: that is what makes the rule deterministic rather than a race.
            if (!ownUi && facts.IsExpandedInPlace(key)) return Admission.Template;

            // The last stop before an entry exists: every route that creates one passes here. A key
            // we recognise as our own translation wearing another decoration never becomes one.
            if (facts.IsReadback(key, ownUi)) return Admission.AlreadyTarget;

            return Admission.Admitted;
        }

        /// <summary>
        /// A template proved: taken back out of the queue if it is still waiting, and given up for
        /// the session. True when it was waiting — in a game, a race the worker may have won.
        /// </summary>
        public static bool WithdrawTemplate(TranslationQueue queue, string text, string key)
        {
            bool withdrawn = queue.Withdraw(text) || queue.Withdraw(key);
            queue.NoteRefused(key);
            return withdrawn;
        }
    }
}
