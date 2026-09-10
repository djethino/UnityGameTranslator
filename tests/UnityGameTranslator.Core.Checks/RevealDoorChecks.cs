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
