using System;
using System.Collections.Generic;
using System.IO;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// One comparison, and every screen that can open one can also close it — with the same word.
    ///
    /// 🔴 **A comparison outlives the screen that started it.** It holds a token on the site and a
    /// stream in the mod, and the browser tab goes on waiting until one of them says stop. So the
    /// control that opened it must turn into the way out; anything else leaves somebody with a tab
    /// that will sit there until it expires, and a mod still listening for a result nobody will
    /// send.
    ///
    /// 🔴 **Two screens carry that button, and they disagreed twice — in opposite directions.**
    /// The main panel wrote its label in two places, so the click put it straight back on the verb
    /// it had just left. The corner notification HID its button instead of turning it, which reads
    /// as the click having failed and made the same fact say two different things depending on
    /// which screen you were looking at. There is no such thing as "the screen that opened it":
    /// both are about the same comparison.
    ///
    /// ⚠ **Lexical, like <see cref="ReloadChainChecks"/> and <see cref="UploadOfferChecks"/>, and
    /// for the same reason**: opening one needs a server, a browser and a screen, so none of this
    /// replays here. What is checkable without running anything is that each screen still ASKS
    /// whether one is in flight, still offers the way out, and still has exactly ONE author for its
    /// label — which is precisely what went missing each time.
    /// </summary>
    internal static class ComparisonDoorChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // Every screen that puts a Compare button in front of somebody.
            var screens = new (string What, string File)[]
            {
                ("the main panel", "MainPanel.cs"),
                ("the corner notification", "StatusOverlay.cs"),
            };

            check(screens.Length > 1,
                "more than one screen carries the button",
                "with a single screen the rule is trivially true and proves nothing");

            foreach (var screen in screens)
            {
                string path = Find("UnityGameTranslator.Core", "UI", "Panels", screen.File);

                check(path != null,
                    $"{screen.What} is found",
                    "this check reads it; without it, it proves nothing");
                if (path == null) continue;

                string text = File.ReadAllText(path);

                check(text.Contains("TranslatorUIManager.IsComparisonOpen"),
                    $"{screen.What} asks whether one is in flight",
                    "a screen that does not ask cannot tell the offer from the way out");

                check(text.Contains("TranslatorUIManager.EndComparison("),
                    $"{screen.What} offers the way out",
                    "the tab and the token outlive the screen; without this they are only released by expiry");

                check(text.Contains("\"Stop comparison\""),
                    $"{screen.What} says it in the same words",
                    "the same fact reading differently on two screens is the one thing an ecosystem may not do");

                // 🔴 The single-author rule, and it is what actually broke on the main panel: a
                // second place writing the offer's label runs after the verb has changed and puts
                // the button back on the one it just left.
                int authors = Occurrences(text, "\"Compare (");
                check(authors == 1,
                    $"{screen.What} writes the offer's label in exactly one place (found {authors})",
                    "two authors for one label, and the one that runs last knows the least");
            }

            // ── The one place that tells them a comparison has STARTED. ──
            string uiFile = Find("UnityGameTranslator.Core", "UI", "TranslatorUIManager.cs");

            check(uiFile != null,
                "the manager holding both doors is found",
                "this check reads it; without it, it proves nothing");
            if (uiFile == null) return;

            string ui = File.ReadAllText(uiFile);
            string opening = BodyOf(ui, "public static async Task OpenComparison(");
            string ending = BodyOf(ui, "public static void EndComparison(");

            check(opening != null && ending != null,
                "and both doors are still there under their own names",
                "renamed or moved, the check must say so rather than pass on an empty comparison");
            if (opening == null || ending == null) return;

            // The pair, in both directions. Ending already did this; opening did not, and that is
            // how a screen that had not been clicked went on offering a comparison already running.
            var doors = new (string What, string Body)[]
            {
                ("opening one", opening),
                ("ending one", ending),
            };

            foreach (var door in doors)
            {
                check(door.Body.Contains("MainPanel?.RefreshUI()"),
                    $"{door.What} tells the main panel",
                    "otherwise the screen that was not clicked keeps the verb it has just left");

                check(door.Body.Contains("StatusOverlay?.RefreshOverlay()"),
                    $"{door.What} tells the corner notification",
                    "same reason, other screen — and this was the half that was missing");
            }

            // ⚠ A callback per caller is exactly how the two screens came to disagree: each one
            // refreshed its own button and nobody refreshed the other's.
            check(!opening.Contains("onFinished"),
                "and it does not hand the answer back to whoever asked",
                "a caller refreshing its own button is a screen refreshing only itself");
        }

        /// <summary>How many times a literal occurs — overlapping is impossible here, so a plain scan.</summary>
        private static int Occurrences(string text, string needle)
        {
            int count = 0;
            int at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        /// <summary>
        /// The body of a method, by counting braces from its signature.
        ///
        /// ⚠ Returns null rather than guessing when the signature is not found: the caller stops
        /// there, so a rename fails the section instead of quietly checking an empty string.
        /// </summary>
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
