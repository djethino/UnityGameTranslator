using System.Collections.Generic;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>Who answered, which decides one step of putting the text back together.</summary>
    public enum AnswerFrom
    {
        /// <summary>
        /// A language model. It was given instructions and may have answered with more than the
        /// translation — a preamble, quotation marks, a thinking block.
        /// </summary>
        Model,

        /// <summary>
        /// A translation service. It takes no instructions and returns a translation and nothing
        /// else, so there is no chatter to remove — and removing some would be removing text.
        /// </summary>
        TranslationApi,
    }

    /// <summary>
    /// A text taken apart for sending, and everything needed to put it back together.
    ///
    /// ⚠ The parts are held rather than re-derived: the tags are keyed by position in this list,
    /// and the whitespace was deliberately cut off — neither can be found again in the answer.
    /// </summary>
    public struct PreparedText
    {
        /// <summary>What a backend is actually given.</summary>
        public string ToSend;

        /// <summary>The markup that was lifted out, in the order its placeholders are numbered.</summary>
        public List<string> Tags;

        /// <summary>Visual padding held back, put on again at the very end.</summary>
        public string Leading;

        /// <inheritdoc cref="Leading"/>
        public string Trailing;

        /// <summary>
        /// True when nothing translatable is left. Nobody is asked.
        ///
        /// ⚠ The two paths disagreed here: one returned early, the other sent the empty text to a
        /// model. Neither looks reachable today — whitespace-only text is turned back at the queue
        /// door — but two copies differing is exactly what this file exists to end.
        /// </summary>
        public bool NothingToSend;
    }

    /// <summary>
    /// What every backend does to a text before sending it, and to the answer before believing it.
    ///
    /// 🔴 **Written once because it was written twice.** The model path and the translation-API
    /// path each carried their own copy of these two dozen lines, identical apart from one step.
    /// Two copies of the same rule is one copy that will be fixed and one that will not — and this
    /// is the rule a SECOND engine's Core has to reproduce exactly, so the count would have gone
    /// from two to two per engine.
    ///
    /// 🔴 **The ORDER is the rule, both ways, and neither is arbitrary.**
    ///
    /// Going out: line breaks become tokens FIRST, then markup, then the padding is cut. The trim
    /// has to be last — once a trailing newline is a `[!nl]` token it is no longer whitespace, so
    /// it survives as something the answer must give back rather than being quietly shaved off.
    ///
    /// Coming back: markup, then line breaks, then the model's chatter, then the padding. The
    /// padding is last because removing chatter also trims, so putting the padding on any earlier
    /// means putting it on and taking it straight off again.
    ///
    /// ⚠ **Pure by contract**: strings in, strings out. What to send it to, how many times to ask,
    /// and what to do with a refusal all stay with the caller.
    ///
    /// Cut out of TranslatorCore on 2026-09-08 (step 6 of analyse/plan-prealables-couches.md).
    /// </summary>
    public static class Backends
    {
        /// <summary>The token a line break becomes while a text is away being translated.</summary>
        public const string LineBreak = "[!nl]";

        /// <summary>
        /// Take a text apart for sending: structure into tokens, padding held back.
        ///
        /// ⚠ Numbers are NOT done here. They are lifted much earlier, before the cache is even
        /// consulted, because the text with its numbers replaced IS the cache key — one entry
        /// serving every value the game puts in it (see <see cref="NumberPatterns"/>).
        /// </summary>
        public static PreparedText Prepare(string text)
        {
            var prepared = new PreparedText { Leading = "", Trailing = "", Tags = new List<string>() };

            if (string.IsNullOrEmpty(text))
            {
                prepared.ToSend = text;
                prepared.NothingToSend = true;
                return prepared;
            }

            // 1. Line breaks → [!nl], so the answer has to give the shape back rather than
            //    reflowing it. Before the trim, deliberately — see the note on order above.
            string work = text.Replace("\n", LineBreak);

            // 2. Markup tags (<color=…>, </b>, …) → [!t*N]. The model never sees markup it could
            //    translate, reorder or invent.
            work = TextNormalization.ExtractMarkupTags(work, out List<string> tags);
            prepared.Tags = tags ?? new List<string>();

            // 3. Visual padding held back. A model asked to translate "  Play  " answers about the
            //    spaces as often as not, and the game laid them out for a reason.
            string trimmed = work.TrimStart();
            if (trimmed.Length < work.Length)
            {
                prepared.Leading = work.Substring(0, work.Length - trimmed.Length);
                work = trimmed;
            }
            trimmed = work.TrimEnd();
            if (trimmed.Length < work.Length)
            {
                prepared.Trailing = work.Substring(trimmed.Length);
                work = trimmed;
            }

            prepared.ToSend = work;
            prepared.NothingToSend = string.IsNullOrWhiteSpace(work);
            return prepared;
        }

        /// <summary>
        /// Put the answer back together: markup, line breaks, chatter, padding — in that order.
        ///
        /// ⚠ An empty answer is handed straight back. There is nothing to restore into it, and
        /// dressing an empty string in the original's padding would produce a translation made
        /// entirely of spaces.
        /// </summary>
        public static string Restore(PreparedText prepared, string answer, AnswerFrom from)
        {
            if (string.IsNullOrEmpty(answer)) return answer;

            // 1. [!t*N] → the original markup, exactly as it was.
            string result = TextNormalization.RestoreMarkupTags(answer, prepared.Tags);

            // 2. [!nl] → real line breaks.
            result = result.Replace(LineBreak, "\n");

            // 3. What a model wrapped around its answer — never applied to a translation service,
            //    which returns a translation and nothing else, so anything removed would be text.
            if (from == AnswerFrom.Model) result = Answers.Clean(result);

            // 4. The padding, LAST: step 3 trims, so any earlier is put on and taken off again.
            if (prepared.Leading.Length > 0 || prepared.Trailing.Length > 0)
                result = prepared.Leading + result + prepared.Trailing;

            return result;
        }
    }
}
