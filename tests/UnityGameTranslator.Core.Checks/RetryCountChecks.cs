using System;
using System.Collections.Generic;
using System.IO;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A retry counter that belongs to the line beside it, and to no other.
    ///
    /// 🔴 **The whole rule is about when it is NOT there.** A first try is not a retry, so it shows
    /// nothing; and a count is lowered both when a line finishes and when the next one starts, so
    /// what a reader sees can never be left over from the text before. A stale "2/3" beside a line
    /// that was answered first time says the model is struggling when it is not — and it is the
    /// kind of thing that comes back the day somebody adds an exit to the worker loop.
    ///
    /// ⚠ Lexical, like <see cref="LiveCountChecks"/>: the counter is read by a panel and written by
    /// a worker thread mid-request. What is checkable without a game is that all three moments are
    /// still wired — and all three are needed, none of them says anything alone.
    /// </summary>
    internal static class RetryCountChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string coreFile = Find("UnityGameTranslator.Core", "TranslatorCore.cs");
            string overlayFile = Find("UnityGameTranslator.Core", "UI", "Panels", "StatusOverlay.cs");

            check(coreFile != null && overlayFile != null,
                "the worker and the notice are found",
                "this check reads them; without them, it proves nothing");
            if (coreFile == null || overlayFile == null) return;

            string core = File.ReadAllText(coreFile);
            string overlay = File.ReadAllText(overlayFile);

            string note = BodyOf(core, "private static void NoteAttempt(int attempt, int total)");
            check(note != null && note.Contains("attempt == 0 ? 0 :", StringComparison.Ordinal),
                "the first try counts as no retry",
                "a \"1/3\" on every line says the model is struggling on all of them, which is what the counter exists to distinguish");

            check(Occurrences(core, "NoteAttempt(0, 0)") >= 2,
                "and the count is lowered twice: when a line ends, and when the next one starts",
                "🔴 either alone leaves a window — one exit added to the worker loop, or one line that never reaches its end, and the next text wears the previous one's count");

            // The repair loop is the socle's LineTranslation since 2026-09-23. That it tells its
            // OnAttempt BEFORE each request is held there (LineTranslationChecks); what the mod
            // must still do is hand it this counter.
            check(core.Contains("OnAttempt = NoteAttempt", StringComparison.Ordinal),
                "the repair loop is told which attempt is running",
                "without it the counter never rises and the whole thing is decoration");

            check(overlay.Contains("TranslatorCore.RetryAttempt", StringComparison.Ordinal)
                  && overlay.Contains("attempt > 0", StringComparison.Ordinal),
                "the notice shows it only while a line is being asked for again",
                "shown always, it is noise; shown from a value nobody lowered, it is wrong");
        }

        private static int Occurrences(string text, string needle)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
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
