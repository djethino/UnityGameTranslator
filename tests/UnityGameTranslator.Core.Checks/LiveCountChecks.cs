using System;
using System.Collections.Generic;
using System.IO;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A screen that shows a number the engine changes has to be told, and nothing tells it.
    ///
    /// 🔴 **The worker knows nothing about screens, and that is right.** It stores a captured or
    /// translated line and moves on; it must not call into the interface, on the wrong thread,
    /// hundreds of times a second while a capture pass runs. The consequence is that the card
    /// showed whatever the count was when something ELSE happened to refresh it, and stayed there:
    /// reported from a real game where the panel read 98 while the file held 145 — and the Manager,
    /// which reads the file rather than the card, read 145 too.
    ///
    /// ⚠ **The answer is a tick that ASKS**, in the shape the strips beside it already use: the
    /// loop asks every panel, the panel answers with one integer compare and returns. Both halves
    /// are needed and neither says anything alone — a tick calling nothing, or a method nobody
    /// calls, are the same silence.
    ///
    /// ⚠ Lexical, like <see cref="ReloadChainChecks"/> and for the same reason: this needs a game,
    /// a screen and a worker thread to happen. What is checkable without running anything is that
    /// the question is still ASKED — which is exactly what was missing.
    /// </summary>
    internal static class LiveCountChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string uiFile = Find("UnityGameTranslator.Core", "UI", "TranslatorUIManager.cs");
            string panelFile = Find("UnityGameTranslator.Core", "UI", "Panels", "MainPanel.cs");

            check(uiFile != null && panelFile != null,
                "the tick and the panel are found",
                "this check reads them; without them, it proves nothing");
            if (uiFile == null || panelFile == null) return;

            string tick = BodyOf(File.ReadAllText(uiFile), "private static IEnumerator MainTickLoop()");
            string panel = File.ReadAllText(panelFile);

            check(tick != null,
                "and the tick loop is still there under its own name",
                "renamed, the check must say so rather than pass on an empty comparison");
            if (tick == null) return;

            check(tick.Contains("MainPanel?.RefreshCountIfChanged()", StringComparison.Ordinal),
                "the tick asks the main panel whether the count moved",
                "nothing else can: the worker stores a line and moves on, and must not call into a screen from its own thread");

            string ask = BodyOf(panel, "internal void RefreshCountIfChanged()");
            check(ask != null,
                "and the panel still answers that question",
                "a tick calling a method that no longer exists would not compile; one calling a method that no longer refreshes would say nothing");
            if (ask == null) return;

            check(ask.Contains("_shownLineCount", StringComparison.Ordinal),
                "against what it last showed",
                "asking every tick without comparing is a full card rebuild per frame, which is the cost the tick was written to avoid");

            check(ask.Contains("RefreshStatusCard()", StringComparison.Ordinal),
                "and it refreshes the whole card, not the number alone",
                "the same lines move the unpublished-changes count: a card saying 145 lines beside 98 unpublished changes is a second way of being wrong");

            // 🔴 And the recording lives with the drawing, so a refresh through ANY door leaves the
            // tick with the truth. Recorded by the caller instead, one door forgets and the card
            // freezes again — the exact defect, one level down.
            string draw = BodyOf(panel, "private void RefreshStatusCard()");
            check(draw != null && draw.Contains("_shownLineCount = entryCount", StringComparison.Ordinal),
                "the card records what it drew, where it draws it",
                "left to the callers, the one that forgets freezes the card while every other door works");
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
