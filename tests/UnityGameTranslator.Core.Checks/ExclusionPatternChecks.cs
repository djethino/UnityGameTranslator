using System;
using System.Diagnostics;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Whether a path in the game's hierarchy is covered by a pattern somebody wrote.
    ///
    /// ⚠ **What a pattern means is a promise.** Somebody wrote it to keep a proper noun, a brand or
    /// a line they translate by hand away from a model — or, through the `path:` prefix of a font
    /// rule, to give one part of a screen another face. A pattern that quietly stops matching sends
    /// that text off anyway, and nothing on screen says so. It had no case of its own until this
    /// file, which is why the edges below are written out rather than assumed.
    ///
    /// ⚠ Written from the rule as stated — "** any depth, * one level, an exact path covers its
    /// children" — not read back from the matcher.
    /// </summary>
    internal static class ExclusionPatternChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            ExactPaths(check);
            Wildcards(check);
            Edges(check);
            Cost(check);
        }

        private static void ExactPaths(Action<bool, string, string> check)
        {
            check(ExclusionPatterns.Matches("Canvas/Panel", "Canvas/Panel"),
                "a path matches itself", "the plainest thing anybody writes");

            // 🔴 An exact path covers what hangs under it, and this is the rule people rely on
            // without being told: somebody excludes a panel, not a list of its labels.
            check(ExclusionPatterns.Matches("Canvas/Panel/Text", "Canvas/Panel")
                  && ExclusionPatterns.Matches("Canvas/Panel/Row/Text", "Canvas/Panel"),
                "and everything under it, at any depth",
                "excluding a panel one label at a time is not something anybody would finish");

            // ⚠ The boundary that makes the rule safe: a sibling whose name merely STARTS with the
            // pattern is not under it. Without the '/' test, excluding "Canvas/Panel" would silently
            // take "Canvas/PanelOfShame" with it.
            check(!ExclusionPatterns.Matches("Canvas/PanelOfShame", "Canvas/Panel"),
                "but a sibling that merely starts the same way is not under it",
                "a prefix test alone would swallow every neighbour whose name begins with the pattern");

            check(ExclusionPatterns.Matches("CANVAS/panel", "canvas/PANEL"),
                "case is not part of the promise",
                "a path is written by a developer and read by a player; neither should have to match capitals");

            check(!ExclusionPatterns.Matches("Canvas", "Canvas/Panel"),
                "and a parent is not covered by excluding its child",
                "the rule goes down, never up");
        }

        private static void Wildcards(Action<bool, string, string> check)
        {
            check(ExclusionPatterns.Matches("Canvas/Chat/Line", "Canvas/Chat/**")
                  && ExclusionPatterns.Matches("Canvas/Chat/Box/Line", "Canvas/Chat/**"),
                "** covers any depth below it", "the pattern somebody writes for a whole subtree");

            // ⚠ Zero segments too: "Canvas/Chat/**" covers "Canvas/Chat" itself. Otherwise the
            // container of an excluded subtree would be the one thing still translated in it.
            check(ExclusionPatterns.Matches("Canvas/Chat", "Canvas/Chat/**"),
                "including zero of them",
                "otherwise the one label left translated is the container of everything excluded");

            check(ExclusionPatterns.Matches("Canvas/HUD/PlayerName", "**/PlayerName")
                  && ExclusionPatterns.Matches("PlayerName", "**/PlayerName"),
                "and ** at the front matches at any depth, including none",
                "the pattern for a name that appears in several screens");

            check(ExclusionPatterns.Matches("Canvas/HUD/Name", "Canvas/*/Name"),
                "* stands for exactly one level", "which is what makes it different from **");

            check(!ExclusionPatterns.Matches("Canvas/HUD/Row/Name", "Canvas/*/Name"),
                "and only one",
                "a * that quietly spanned two levels would make ** pointless and every pattern wider than written");

            check(ExclusionPatterns.Matches("Canvas/ChatWindow/Line", "Canvas/Chat*/**"),
                "a * inside a name matches the rest of it",
                "Chat* for ChatWindow and ChatLog, which is how somebody names a family of panels");

            check(!ExclusionPatterns.Matches("Canvas/Inventory/Line", "Canvas/Chat*/**"),
                "and not another name", "the same case, from the side that must not match");

            // ⚠ A pattern with a wildcard does NOT get the implicit-children rule: it is spelled
            // out with ** when that is wanted. Two rules at once would make "Canvas/*" mean the
            // whole tree, which is not what anybody writing a single star expects.
            check(!ExclusionPatterns.Matches("Canvas/HUD/Name", "Canvas/*"),
                "a wildcard pattern covers what it says and no more",
                "one star meaning 'this level' and 'everything under it' at once would leave nothing for **");
        }

        private static void Edges(Action<bool, string, string> check)
        {
            check(!ExclusionPatterns.Matches("Canvas/Panel", null)
                  && !ExclusionPatterns.Matches("Canvas/Panel", ""),
                "nothing written excludes nothing",
                "an empty pattern reaching this would otherwise exclude the whole game in silence");

            check(!ExclusionPatterns.Matches("", "Canvas/Panel"),
                "and a path nobody could build matches no pattern",
                "a component with no hierarchy is not a match, it is a question with no subject");

            check(ExclusionPatterns.Matches("", "**"),
                "except **, which is everything and says so",
                "somebody who writes it has asked for exactly that");
        }

        /// <summary>
        /// ⚠ **Measured, not assumed.** Each ** tries every remaining position, so several of them
        /// on a deep path multiply. This project has already had a text rule freeze a game by
        /// backtracking (TextRuleChecks), and this matcher runs behind a cache on every text write.
        /// The case exists to notice the day somebody makes it worse — patterns come from a person,
        /// so a pathological one is possible even if it is not likely.
        /// </summary>
        private static void Cost(Action<bool, string, string> check)
        {
            string deep = string.Join("/", new string[24]).Replace("/", "seg/").TrimEnd('/');
            const string nasty = "**/**/**/**/**/**/nowhere";

            var clock = Stopwatch.StartNew();
            bool matched = ExclusionPatterns.Matches(deep, nasty);
            clock.Stop();

            check(!matched, "a pattern that cannot match says so", "the answer first, the cost second");

            check(clock.ElapsedMilliseconds < 250,
                $"and six ** over a 24-deep path costs {clock.ElapsedMilliseconds} ms, not seconds",
                "this runs behind a cache on the write path; a pattern nobody can afford would freeze the game that loaded it");
        }
    }
}
