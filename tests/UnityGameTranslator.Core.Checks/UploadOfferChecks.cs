using System;
using System.Collections.Generic;
using System.IO;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A screen that offers publishing asks the socle WHICH act is available. It never works it
    /// out from the role.
    ///
    /// 🔴 **"Not yours" does not mean "contribute".** A Main marked solo work takes no
    /// contribution; neither does one its author has removed, nor one whose owner erased their
    /// account, nor a branch whose Main has closed since. <c>Uploads.ActOf</c> reads those four
    /// walls and answers Fork instead — and every one of them arrives from the server, so no
    /// screen can know them by looking at the role alone.
    ///
    /// 🔴 **Two screens out of three asked; the third offered Branch anyway.** The corner
    /// notification decided on "not the owner, and something changed locally", so on a lineage
    /// that takes no contributions it named an act the server would refuse — and the line beside
    /// it promised to send the work "for review to @somebody" who had asked for no such thing.
    /// The main panel had paid for the same defect once already, in the other direction.
    ///
    /// ⚠ **Lexical, like its neighbours**: each of these calls needs a server answer and a screen,
    /// so none of it replays here. What is checkable is that the question is still ASKED — which
    /// is exactly what went missing.
    /// </summary>
    internal static class UploadOfferChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // Every screen that puts a publishing act in front of somebody.
            var screens = new (string What, string File, string Why)[]
            {
                ("the main panel", "MainPanel.cs",
                 "its Upload button carries the act's own verb"),
                ("the upload screen", "UploadPanel.cs",
                 "it is where the act is taken, and it re-asks rather than trusting what sent it"),
                ("the corner notification", "StatusOverlay.cs",
                 "this is the one that did not, and it offered Branch on a lineage taking none"),
            };

            foreach (var screen in screens)
            {
                string path = Find("UnityGameTranslator.Core", "UI", "Panels", screen.File);
                check(path != null, $"{screen.What}'s source is found",
                    "this check reads it; without it, it proves nothing");
                if (path == null) return;

                string text = File.ReadAllText(path);
                check(text.Contains("Uploads.ActOf", StringComparison.Ordinal),
                    $"{screen.What} asks the socle which act is available",
                    screen.Why);
            }

            // ⚠ **And the walls are passed IN — read from the CALL, never from the file.**
            //
            // 🔴 The first version of this looked for the four names anywhere in the source, and
            // replacing every argument with `null` did not turn it red: the names were still there
            // a few lines below, in the sentence that explains the wall. A check that cannot fail
            // is decoration, so it reads the argument list.
            string overlay = File.ReadAllText(Find("UnityGameTranslator.Core", "UI", "Panels", "StatusOverlay.cs"));
            string call = Arguments(overlay, "Uploads.ActOf(");

            check(call != null, "the notification's call can be read",
                "the check is anchored on it; renamed, it must say so rather than pass quietly");
            if (call == null) return;

            foreach (string wall in new[] { "AcceptsBranches", "MainMissing", "MainAbandoned", "BranchFrozen" })
            {
                check(call.Contains(wall, StringComparison.Ordinal),
                    $"and hands it {wall}",
                    "each of these turns a contribution into a fork, and only the server knows it — handed a null, the question is asked and the answer thrown away");
            }
        }

        /// <summary>What is between the brackets of a call — the arguments, and nothing else.</summary>
        private static string Arguments(string text, string call)
        {
            int at = text.IndexOf(call, StringComparison.Ordinal);
            if (at < 0) return null;

            int open = at + call.Length - 1;
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0) return text.Substring(open + 1, i - open - 1);
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
