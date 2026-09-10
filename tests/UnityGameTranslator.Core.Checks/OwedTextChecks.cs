using System;
using System.Collections.Generic;
using System.IO;
using UnityGameTranslator.Core.Engine;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A text that was asked for and never answered comes back — because the SCREEN is the store.
    ///
    /// 🔴 **What it used to cost**: the scanner recorded a component as handled the moment its text
    /// had been QUEUED, not when it had been answered. So a translation that failed — a silent
    /// server, a model that never came back — left the next round answering SAME-HASH, and that line
    /// stayed in the game's own language for the rest of the scene. The item itself had left both
    /// queue containers at dequeue, and only a rate limit ever put one back.
    ///
    /// ⚠ **The queue itself is untouched, and it outlives a scene** — emptied only by a cache
    /// reload and by switching translation off, never by a scene change, so a text asked for in one
    /// scene goes on being translated in the next. What follows is the recovery for the one case
    /// where a text leaves the queue WITHOUT an answer, and nothing else.
    ///
    /// 🔴 **And that recovery is not a retry list.** A component still showing an untranslated text
    /// asks for it again by itself, which needs no container of ours. Same lesson as the sweep:
    /// state, not transitions.
    ///
    /// ⚠ Half pure, half lexical, and the split is the honest one: what the queue knows can be
    /// replayed here, while "who asks it, and before what" needs a game — so it is read.
    /// </summary>
    internal static class OwedTextChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            WhatTheQueueKnows(check);
            WhoAsksIt(check);
        }

        private static void WhatTheQueueKnows(Action<bool, string, string> check)
        {
            var queue = new TranslationQueue();
            queue.Submit("Play", null, false, out _, out _);

            check(queue.Holds("Play"),
                "a waiting text is held",
                "the scanner asks this to decide whether the component that shows it is done with");

            check(!queue.Holds("Quit") && !queue.Holds(null) && !queue.Holds(""),
                "and nothing else is",
                "a yes on a text nobody asked for would keep its component alive on the hottest path for ever");

            queue.Submit("Options", null, true, out _, out _);
            check(queue.Holds("Options"),
                "whichever side asked for it",
                "the interface's own labels wait in the same queue under a different origin, and the screen showing one is owed just the same");

            var taken = queue.Take();
            check(taken != null && !queue.Holds(taken.Text),
                "🔴 one already taken is NOT held",
                "it is in the worker's hand and the queue no longer knows it — which is why the caller asks the in-flight text too, and why saying yes here would be a lie");
        }

        /// <summary>
        /// The scanner and the queueing door, read: both live in a game.
        /// </summary>
        private static void WhoAsksIt(Action<bool, string, string> check)
        {
            string scannerFile = Find("UnityGameTranslator.Core", "TranslatorScanner.cs");
            string coreFile = Find("UnityGameTranslator.Core", "TranslatorCore.cs");

            check(scannerFile != null && coreFile != null,
                "the scanner and the core are found",
                "this half reads them; without them, it proves nothing");
            if (scannerFile == null || coreFile == null) return;

            string scanner = File.ReadAllText(scannerFile);
            string core = File.ReadAllText(coreFile);

            string marking = BodyOf(scanner, "private static void ProcessOneComponent(object component, RegisteredTextType type)");
            check(marking != null, "and the per-component pass is still there under its name",
                "renamed, the check must say so rather than pass on an empty comparison");
            if (marking == null) return;

            check(marking.Contains("if (!TranslatorCore.StillOwed(currentText))", StringComparison.Ordinal),
                "a component is written off only when nothing more is coming",
                "🔴 marked at queueing, a text the server never answered is lost for the rest of the scene and nothing says so");

            int guard = marking.IndexOf("StillOwed(currentText)", StringComparison.Ordinal);
            int seen = marking.IndexOf("TranslatorCore.UpdateSeenText(instanceId, currentText)", StringComparison.Ordinal);
            check(guard >= 0 && seen > guard,
                "and BOTH records are behind that question",
                "there are two gates — the seen text and the text hash — and one of them left open is the same loss, one round later");

            string owed = BodyOf(core, "internal static bool StillOwed(string text)");
            check(owed != null && owed.Contains("_queue.Holds(text)", StringComparison.Ordinal)
                  && owed.Contains("_inFlightText", StringComparison.Ordinal),
                "waiting and in flight both count as owed",
                "the queue forgets an item at dequeue, so asking it alone writes off every text being translated right now");

            check(owed != null && owed.Contains("if (_backendSilent) return true;", StringComparison.Ordinal),
                "and so does everything on screen while the server is silent",
                "nothing is being asked then, so nothing may be written off — that is what brings the screen back when it answers again");

            string queueing = BodyOf(core, "public static bool QueueForTranslation(string text, object component = null, bool isOwnUI = false)");
            check(queueing != null
                  && queueing.Contains("_backendSilent && (_queue.Count > 0 || isTranslating)", StringComparison.Ordinal),
                "🔴 while it is silent, exactly one text is let through",
                "refusing all of them is a deadlock — nothing would ever ask again, so nothing would ever answer; letting all of them through is a queue of work nobody will get, one notice each");
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
