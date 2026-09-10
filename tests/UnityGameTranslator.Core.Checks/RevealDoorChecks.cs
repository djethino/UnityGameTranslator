using System;
using System.Collections.Generic;
using System.IO;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A reveal in flight is told what the component now shows — through EVERY door, or none.
    ///
    /// 🔴 **The defect, measured on one dialogue visited three times** (2026-09-10): the same
    /// sentence was finalised at 37, then 36, then 35 characters, and each truncation was paid for
    /// at the model, cached as its own line, and applied to a screen that already showed the
    /// translation — with a "translating" notice each time, on a line the player could see was
    /// already done.
    ///
    /// The cause is a rule stated in one place out of eleven. A reveal is only followed through
    /// <c>IsTypewritingInProgress</c>, which a text already known never reaches: the lookup answers
    /// and returns. Eleven of its exits mean "this text is known" — the exact key, the normalised
    /// key, the trimmed key, a number pattern, a concat result — and exactly one told the reveal
    /// what it had seen. The numbers in a sentence are lifted into placeholders, so recognition
    /// arrives BEFORE the last character: the reveal went blind at that point and waited out its
    /// five hundred milliseconds on a fragment.
    ///
    /// ⚠ Lexical, like <see cref="LiveCountChecks"/> and <see cref="ComparisonDoorChecks"/>: the
    /// state machine is welded to a component and a clock. What is checkable without a game is that
    /// the fact is still stated ONCE, ahead of every exit — which is exactly what was missing.
    /// </summary>
    internal static class RevealDoorChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string coreFile = Find("UnityGameTranslator.Core", "TranslatorCore.cs");
            string patchFile = Find("UnityGameTranslator.Core", "TranslatorPatches.cs");

            check(coreFile != null && patchFile != null,
                "the lookup and the reveal are found",
                "this check reads them; without them, it proves nothing");
            if (coreFile == null || patchFile == null) return;

            string lookup = BodyOf(File.ReadAllText(coreFile),
                "private static string TranslateSingleTextWithTracking(string text, object component");
            check(lookup != null,
                "and the lookup is still there under its own name",
                "renamed, the check must say so rather than pass on an empty comparison");
            if (lookup == null) return;

            int told = lookup.IndexOf("TranslatorPatches.NoteTextSeen(", StringComparison.Ordinal);
            check(told >= 0,
                "the lookup tells the reveal what this component shows",
                "🔴 the defect: every exit below means \"already known\", and a reveal told by none of them holds a fragment and sends it");

            check(Occurrences(lookup, "TranslatorPatches.NoteTextSeen(") == 1,
                "and says it exactly once",
                "said per branch, the next branch added forgets — which is how one exit out of eleven ended up carrying the rule");
            if (told < 0) return;

            // Ahead of every exit that can answer. The first `return` of the method body that
            // follows a cache question is what must never come first.
            int firstLookup = FirstIndexOf(lookup, "GetConcatCacheResult(", "store.TryGetValue(", "TryPatternMatch(");
            check(firstLookup >= 0 && told < firstLookup,
                "before the first question that can answer and return",
                "told after a lookup, every text that lookup recognises still leaves the reveal holding what it had");

            string note = BodyOf(File.ReadAllText(patchFile), "public static void NoteTextSeen(long compId, string currentText)");
            check(note != null,
                "and the reveal still answers that call",
                "a caller of a method that no longer holds anything would compile and say nothing");
            if (note == null) return;

            check(note.Contains("TextRelations.Grows(state.TypewritingText, currentText)", StringComparison.Ordinal),
                "it holds only what has GROWN past what it was holding",
                "a text unrelated to the reveal must not become the reveal");

            check(note.Contains("if (state.TypewritingQueued) return;", StringComparison.Ordinal),
                "and never re-holds a text already handed over",
                "cancelling a finalisation already decided is how a line waits for ever");

            // 🔴 The half that makes the top-of-method call safe at all.
            check(!note.Contains("isSame", StringComparison.Ordinal),
                "an identical text is not a reason to wait longer",
                "🔴 restarting the wait on an unchanged text, from a place the sweep reaches several times a second, defers the line for as long as it is on screen — never translated, nothing said");

            NothingIsSentBeforeItSettled(File.ReadAllText(patchFile), check);
            ATemplateIsRefusedAtThreeMoments(File.ReadAllText(coreFile), check);
        }

        /// <summary>
        /// A template the game expands in place is one FACT, asked at the three moments it matters.
        ///
        /// 🔴 **Taking it out of the queue is not enough.** The proof arrives with the expansion, a
        /// few hundred milliseconds after the text was queued, and the worker may have taken it in
        /// between — no reasoning about how long a model takes can rule that out, and a text can be
        /// hundredth in the queue or first. So the withdrawal is the best case; what makes the rule
        /// deterministic is that once the pair has been seen, the text can never be queued, never be
        /// stored, and above all never be written back.
        ///
        /// ⚠ The last one is also what protects a file polluted before the rule existed: the line
        /// stays in it — deleting somebody's translation on a local observation is refused here —
        /// but it stops reaching the screen, so the game can expand its own text again.
        /// </summary>
        private static void ATemplateIsRefusedAtThreeMoments(string core, Action<bool, string, string> check)
        {
            string queueing = BodyOf(core, "public static bool QueueForTranslation(string text, object component = null, bool isOwnUI = false)");
            string storing = BodyOf(core, "public static void AddToCache(string original, string translated, string tag = \"A\")");
            string lookup = BodyOf(core, "private static string TranslateSingleTextWithTracking(string text, object component");

            check(queueing != null && storing != null && lookup != null,
                "the three moments are found",
                "this check reads them; without them, it proves nothing");
            if (queueing == null || storing == null || lookup == null) return;

            check(queueing.Contains("IsExpandedInPlace(", StringComparison.Ordinal),
                "a template is never queued",
                "queued, it costs a call and shows a notice saying a translation is running on a line the player can see is done");

            check(storing.Contains("IsExpandedInPlace(", StringComparison.Ordinal),
                "and its answer is never stored, if one was already in flight",
                "🔴 this is what makes the rule deterministic instead of a race the worker usually loses");

            check(lookup.Contains("IsExpandedInPlace(", StringComparison.Ordinal),
                "and it is never written back, whatever the cache holds",
                "🔴 the only one that protects a file polluted before the rule: written back, the game cannot expand its own text");

            int told = lookup.IndexOf("IsExpandedInPlace(", StringComparison.Ordinal);
            int firstLookup = FirstIndexOf(lookup, "GetConcatCacheResult(", "store.TryGetValue(", "TryPatternMatch(");
            check(told >= 0 && firstLookup >= 0 && told < firstLookup,
                "asked before any lookup can answer",
                "it is a lookup ANSWERING that does the damage, so asking afterwards is asking too late");

            string forget = BodyOf(core, "public static void ForgetTemplateText(string text)");
            check(forget != null && forget.Contains("_expandedInPlace.Add(skeleton)", StringComparison.Ordinal),
                "and one door records it, as a skeleton",
                "🔴 recorded as the text, a refusal covers one state of the expansion and none of the others — which is how the half-resolved form reached the model on the component beside it");

            string ask = BodyOf(core, "internal static bool IsExpandedInPlace(string text)");
            check(ask != null && ask.Contains("!TextRelations.HasUnresolvedTokens(text)", StringComparison.Ordinal),
                "and the finished form is let through",
                "refusing the whole skeleton would leave the line in the game's own language, which is worse than the defect being fixed");

            // 🔴 Being out of sight changes what may be QUEUED, never what may be KNOWN.
            int hidden = lookup.IndexOf("!visComp.gameObject.activeInHierarchy", StringComparison.Ordinal);
            int hiddenEnd = hidden < 0 ? -1 : lookup.IndexOf("catch { }", hidden, StringComparison.Ordinal);
            string hiddenBranch = hidden >= 0 && hiddenEnd > hidden ? lookup.Substring(hidden, hiddenEnd - hidden) : null;
            check(hiddenBranch != null && hiddenBranch.Contains("IsTypewritingInProgress(", StringComparison.Ordinal),
                "a component out of sight still tells the reveal what it shows",
                "🔴 turning back without a word freezes the state on a text the game has already replaced, and the stabiliser then sends THAT — a game filling its tooltips while hidden had its template sent while the next state sat on the same component");
        }

        /// <summary>
        /// Two mechanisms answer "is this text final?" — the stabiliser, which waits, and the
        /// replacement branch, which infers it. They must not contradict each other.
        ///
        /// 🔴 A game whose ability text is a template expanded in place set a component to
        /// <c>*Overclock* ({0}): Add {1} Strength.</c> and 317 ms later to the expanded form. The
        /// template was declared final by the replacement branch while the stabiliser was still
        /// holding it — queued, translated, and stored as <c>*Surcadence* ({[!v*0]})…</c>. Written
        /// back, the game looks for <c>*Overclock*</c> and <c>{0}</c> and finds neither.
        /// </summary>
        private static void NothingIsSentBeforeItSettled(string patches, Action<bool, string, string> check)
        {
            string method = BodyOf(patches, "public static bool IsTypewritingInProgress(long compId, string newText, object component = null)");
            check(method != null,
                "the reveal's own decision is still there under its name",
                "renamed, the check must say so rather than pass on an empty comparison");
            if (method == null) return;

            check(method.Contains("bool settled = elapsed >= TYPEWRITING_STABILIZE_MS;", StringComparison.Ordinal),
                "a replacement asks whether the text it replaces had settled",
                "🔴 without it, a text replaced mid-wait is declared final by a branch while another is still deciding it is not");

            check(method.Contains("if (settled)", StringComparison.Ordinal)
                  && method.IndexOf("ProcessFinalizedText(compId, state.TypewritingText);", StringComparison.Ordinal)
                     > method.IndexOf("if (settled)", StringComparison.Ordinal),
                "and only a settled one is sent",
                "a text replaced within half a second of appearing was read by nobody, and is a template being expanded as often as not");

            check(method.Contains("TYPEWRITING_STABILIZE_MS", StringComparison.Ordinal)
                  && !System.Text.RegularExpressions.Regex.IsMatch(method, @"elapsed\s*[<>]=?\s*\d"),
                "the rule uses the stabiliser's own delay, never a number of its own",
                "a second constant would be a second answer to one question, and the two would drift");
        }

        private static int Occurrences(string text, string needle)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        private static int FirstIndexOf(string text, params string[] needles)
        {
            int best = -1;
            foreach (string needle in needles)
            {
                int i = text.IndexOf(needle, StringComparison.Ordinal);
                if (i >= 0 && (best < 0 || i < best)) best = i;
            }
            return best;
        }

        /// <summary>The body of a method, by counting braces from its signature.</summary>
        private static string BodyOf(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return null;

            int open = text.IndexOf('{', start + signature.Length);
            if (open < 0) return null;

            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(open, i - open + 1);
                }
            }
            return null;
        }

        private static string Find(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var segments = new List<string> { dir.FullName };
                segments.AddRange(parts);
                string candidate = Path.Combine(segments.ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
