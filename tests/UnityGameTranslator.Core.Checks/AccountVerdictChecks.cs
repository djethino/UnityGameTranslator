using System;
using System.Collections.Generic;
using System.IO;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Anything that decides what this account IS to a lineage must also say whether it asked.
    ///
    /// 🔴 **"We asked the server" and "we asked AS US" are two different facts.** The public
    /// endpoint answers about a translation and never about a person, so it fills the state with
    /// <c>IsOwner = false, Role = None</c> — all an anonymous caller can be told. Read by somebody
    /// signed in whose own check was still in flight, that reads as "this is not yours": the sync
    /// notification offered the OWNER of the translation the two buttons meant for a stranger,
    /// Branch and Fork, until the account check landed a second or two later.
    ///
    /// ⚠ **It cannot be told from the shape** — "not owner, role none" is also the honest, final
    /// answer for somebody using another person's translation. Only who was asked separates them.
    ///
    /// ⚠ **A lexical check, and the rule is narrow on purpose**: a state that says nothing about
    /// ownership (a download, a merge, a failed call) is right to say nothing, and several do so
    /// deliberately. What must never happen is a state that ANSWERS the ownership question without
    /// saying on whose behalf.
    /// </summary>
    internal static class AccountVerdictChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            var files = new (string What, string Path)[]
            {
                ("the sync watch", Find("UnityGameTranslator.Core", "UI", "TranslatorUIManager.cs")),
                ("the upload screen", Find("UnityGameTranslator.Core", "UI", "Panels", "UploadPanel.cs")),
                // The stream's `state` reader moved out of the sync watch on 2026-09-11
                // (ApiReaders.ReadSyncState, held by spec/sse-events); it builds the state the
                // watch used to build inline, so it answers to the same rule.
                ("the readers", Find("UnityGameTranslator.Core", "Engine", "ApiReaders.cs")),
            };

            foreach (var file in files)
            {
                check(file.Path != null, $"{file.What}'s source is found",
                    "this check reads it; without it, it proves nothing");
                if (file.Path == null) return;
            }

            int deciding = 0;
            var silent = new List<string>();

            foreach (var file in files)
            {
                string text = File.ReadAllText(file.Path);
                int at = 0;
                while (true)
                {
                    at = text.IndexOf("new ServerTranslationState", at, StringComparison.Ordinal);
                    if (at < 0) break;

                    string block = Initializer(text, at);
                    at += "new ServerTranslationState".Length;
                    if (block == null) continue;   // built empty, then filled elsewhere

                    bool answersOwnership = block.Contains("IsOwner", StringComparison.Ordinal)
                                            || block.Contains("Role =", StringComparison.Ordinal);
                    if (!answersOwnership) continue;

                    deciding++;
                    if (!block.Contains("AskedAsAccount", StringComparison.Ordinal))
                        silent.Add(file.What);
                }
            }

            check(deciding >= 4,
                $"{deciding} state(s) answer the ownership question",
                "finding none or one would mean the search missed them, and an empty comparison always passes");

            check(silent.Count == 0,
                silent.Count == 0
                    ? "and every one of them says whether it asked as this account"
                    : "ANSWERS OWNERSHIP WITHOUT SAYING WHO ASKED: " + string.Join(", ", silent),
                "an anonymous answer read as an account's offered the owner of a translation the Branch and Fork buttons meant for a stranger");

            // The public check does not BUILD a state — it mutates whichever one is there, so it
            // can inherit a claim that was true of an account answer and is not true of its own.
            string manager = File.ReadAllText(files[0].Path);
            int publicCheck = manager.IndexOf("state.IsOwner = false;", StringComparison.Ordinal);
            check(publicCheck > 0 && manager.LastIndexOf("state.AskedAsAccount = false;", publicCheck, StringComparison.Ordinal) > 0,
                "and the public check takes the claim back off the state it reuses",
                "it writes over an account answer with what an anonymous caller is told; leaving the claim standing is worse than never setting it");
        }

        /// <summary>The `{ … }` that follows, or null when the construction has none.</summary>
        private static string Initializer(string text, int at)
        {
            int i = at + "new ServerTranslationState".Length;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\r' || text[i] == '\n' || text[i] == '\t')) i++;
            if (i >= text.Length || text[i] != '{') return null;

            int depth = 0;
            for (int j = i; j < text.Length; j++)
            {
                if (text[j] == '{') depth++;
                else if (text[j] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(i, j - i + 1);
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
