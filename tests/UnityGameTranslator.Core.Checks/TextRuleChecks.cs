using System;
using System.Diagnostics;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What a <c>text:/…/</c> rule from a downloaded file may do to the game's main thread.
    ///
    /// 🔴 The third case is the one that used to freeze a game, and the only way to know the
    /// budget acts is to hand it a pattern that would run for minutes and time how long it
    /// actually took. The first two are what must NOT change: a rule that matched before matches
    /// now, case-insensitively, and an invalid one is refused rather than thrown.
    /// </summary>
    internal static class TextRuleChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            var greeting = TextRule.Compile("bonjour");
            check(greeting != null, "a plain pattern compiles", "the ordinary case");
            check(TextRule.Match(greeting, "Bonjour le monde") == TextRule.Outcome.Matched,
                  "matches without regard to case", "the option every rule has always had");
            check(TextRule.Match(greeting, "Salut") == TextRule.Outcome.NotMatched,
                  "and says so when it does not match", "a no is a no, not a timeout");

            var anchored = TextRule.Compile("^\\d+ / \\d+$");
            check(TextRule.Match(anchored, "12 / 40") == TextRule.Outcome.Matched,
                  "anchors and classes work as in any regex", "nothing about the grammar changed");

            check(TextRule.Compile("(") == null, "an unbalanced pattern is refused, not thrown",
                  "the caller decides what to say about it");
            check(TextRule.Compile("") == null, "an empty pattern is nothing to match", "");
            check(TextRule.Match(null, "text") == TextRule.Outcome.NotMatched,
                  "a refused pattern matches nothing", "and costs nothing");

            // 🔴 The catastrophic case: (a+)+$ against a text it cannot match backtracks
            // exponentially — forty characters would take longer than a game session.
            var bomb = TextRule.Compile("(a+)+$");
            string text = new string('a', 40) + "b";
            var clock = Stopwatch.StartNew();
            var outcome = TextRule.Match(bomb, text);
            clock.Stop();
            check(outcome == TextRule.Outcome.TimedOut,
                  $"a backtracking bomb is stopped: {outcome}", "this is what froze a game");
            check(clock.Elapsed < TextRule.Budget + TimeSpan.FromSeconds(2),
                  $"and stopped on the budget, in {clock.Elapsed.TotalSeconds:0.0} s",
                  "the budget is the cost, never the pattern");

            // The same compiled rule still answers afterwards: a timeout is a verdict on one text.
            check(TextRule.Match(bomb, "aaa") == TextRule.Outcome.Matched,
                  "the rule still works on a text it can decide", "the object is not poisoned");
        }
    }
}
