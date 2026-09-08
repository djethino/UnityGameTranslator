using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Which font rule applies to a label, and what survives being decided.
    ///
    /// 🔴 **Three things live here that no single question can see.** The ORDER (first match wins,
    /// so the file's order is a decision its author made), the MEMORY (one answer per target, kept,
    /// because it is asked on every text write), and the two per-session VERDICTS — a pattern that
    /// will not compile, and one that will not finish.
    ///
    /// ⚠ **The verdicts are the reason a downloaded translation cannot freeze a game.** A
    /// <c>text:/…/</c> pattern arrives in somebody else's file and runs on the main thread against
    /// every label of every frame. One built to backtrack did exactly that. So it is announced,
    /// once, naming the rule, and left out for the rest of the run — and "once" is half the rule:
    /// a warning on every frame is its own defect.
    ///
    /// ⚠ **Never written back to the file.** <c>enabled</c> belongs to whoever wrote the rule; this
    /// is a verdict on one run, and quietly disabling somebody's rule in their own translation
    /// would be a decision nobody took.
    /// </summary>
    internal static class FontRulesChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            WhichRuleWins(check);
            HowAPatternIsRead(check);
            WhenAPatternMisbehaves(check);
            TheMemory(check);
        }

        private static FontOverrideRule Rule(string match, string replacement = "Noto", bool enabled = true)
        {
            return new FontOverrideRule { match = match, replacement = replacement, enabled = enabled };
        }

        private static FontRules With(params FontOverrideRule[] rules)
        {
            var holder = new FontRules();
            holder.Load(rules);
            return holder;
        }

        private static void WhichRuleWins(Action<bool, string, string> check)
        {
            var first = Rule("path:Canvas/**", "First");
            var second = Rule("path:Canvas/Quest/**", "Second");
            var rules = With(first, second);

            check(ReferenceEquals(rules.Find(1, "Canvas/Quest/Title", "Arial", "Go"), first),
                "the first rule that matches wins",
                "the order in the file is a decision its author made, and a later rule must not quietly take over");

            check(With(second, first).Find(1, "Canvas/Quest/Title", "Arial", "Go").replacement == "Second",
                "and reordering them changes the answer",
                "otherwise 'first match wins' is a sentence in a comment rather than a behaviour");

            check(With(Rule("path:Canvas/**", "Off", enabled: false), second)
                      .Find(1, "Canvas/Quest/Title", "Arial", "Go").replacement == "Second",
                "a rule switched off is passed over, not obeyed",
                "somebody turned it off; leaving it first would make the switch do nothing at all");

            check(With(Rule("path:Canvas/Shop/**")).Find(1, "Canvas/Quest/Title", "Arial", "Go") == null,
                "nothing matching is an answer of its own",
                "the label keeps the font the game gave it, which is what no rule means");

            check(With(Rule(null), Rule(""), second).Find(1, "Canvas/Quest/Title", "Arial", "Go").replacement == "Second",
                "a rule with no pattern matches nothing",
                "an empty pattern that matched everything would repaint a whole game from a blank field");
        }

        private static void HowAPatternIsRead(Action<bool, string, string> check)
        {
            check(With(Rule("path:Canvas/**")).Find(1, "Canvas/Quest/Title", "Arial", "Go") != null
                  && With(Rule("path:Canvas/**")).Find(1, null, "Canvas/Quest", "Canvas/Quest") == null,
                "path: looks at the hierarchy and nowhere else",
                "a font named like a path, or a label quoting one, is not where somebody pointed");

            check(With(Rule("font:Arial")).Find(1, "Canvas/Quest/Title", "ARIAL", "Go") != null,
                "font: names a font, whatever its capitals",
                "the same font is spelt differently by two games, and nobody types it twice to find out");

            check(With(Rule("font:Arial")).Find(1, "Canvas/Quest/Title", "Arial Black", "Go") == null,
                "and names it whole",
                "matching a prefix would take every font whose name begins alike");

            check(With(Rule("text:gold")).Find(1, "Canvas/Quest/Title", "Arial", "You found GOLD") != null,
                "text: looks inside the label, whatever its capitals",
                "it exists for a word that appears mid-sentence, so it cannot be an equality");

            check(With(Rule("text:/^[0-9]+$/")).Find(1, "Canvas/Q", "Arial", "1234") != null
                  && With(Rule("text:/^[0-9]+$/")).Find(1, "Canvas/Q", "Arial", "12a4") == null,
                "and a pattern between slashes is an expression",
                "counters and clocks are matched by shape, which no substring can express");

            // ⚠ No prefix at all: the form somebody types first. Path, then substring — and the
            // order matters, since a label can quote a path.
            var bare = With(Rule("Canvas/Quest/**"));
            check(bare.Find(1, "Canvas/Quest/Title", "Arial", "Go") != null,
                "a pattern with no prefix tries the path first",
                "it is the form written by hand, and a hierarchy is what somebody is usually pointing at");

            check(With(Rule("gold")).Find(1, "Canvas/Quest/Title", "Arial", "You found gold") != null,
                "then the label",
                "so a plain word still works without anybody learning a prefix");
        }

        private static void WhenAPatternMisbehaves(Action<bool, string, string> check)
        {
            // 🔴 An expression that cannot compile. Silence here is what it used to be, and a rule
            // that does nothing while looking active is indistinguishable from a rule that matches
            // nothing.
            var said = new List<string>();
            var broken = Rule("text:/([a-z/");
            var rules = With(broken);
            rules.Warn = said.Add;

            check(rules.Find(1, "Canvas/Q", "Arial", "hello") == null && said.Count == 1
                  && said[0].Contains("([a-z"),
                "an expression that will not compile is announced, naming the rule",
                "it used to be swallowed whole, so a rule doing nothing looked exactly like a rule matching nothing");

            rules.ForgetAll();
            rules.Find(2, "Canvas/Q", "Arial", "hello");
            rules.Find(3, "Canvas/Q", "Arial", "world");

            check(said.Count == 1,
                "and announced ONCE, not on every label",
                "this is asked on every text write of every frame; a line each would drown the log it belongs to");

            check(broken.enabled,
                "the rule is left out for the run and NOT switched off in the file",
                "enabled belongs to whoever wrote the rule; disabling it in their own translation is a decision nobody took");

            // A rule that is fine still works while a broken one sits beside it.
            var mixed = With(Rule("text:/([a-z/"), Rule("path:Canvas/**", "Good"));
            mixed.Warn = _ => { };
            check(mixed.Find(1, "Canvas/Q", "Arial", "hello").replacement == "Good",
                "and its neighbours go on working",
                "one bad pattern in a downloaded file must not cost the rest of somebody's work");

            check(With(Rule("text:/([a-z/")).Find(1, "Canvas/Q", "Arial", "hello") == null,
                "with nobody listening, it still refuses",
                "the log is a courtesy; the refusal is the rule, and a check that passes no sink must not change it");

            // 🔴 The one that actually froze a game: an expression that compiles fine and then
            // backtracks for longer than a session, on the main thread, against every label of
            // every frame. Costs a real second here, once — the budget IS the point, and a case
            // that faked the timeout would prove nothing about the reaction to it.
            var timing = new List<string>();
            var bomb = Rule("text:/(a+)+$/");
            var slow = With(bomb, Rule("path:Canvas/**", "Good"));
            slow.Warn = timing.Add;
            string cannotMatch = new string('a', 40) + "b";

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var verdict = slow.Find(1, "Canvas/Q", "Arial", cannotMatch);
            clock.Stop();

            check(verdict != null && verdict.replacement == "Good"
                  && clock.Elapsed < TextRule.Budget + TimeSpan.FromSeconds(2),
                $"a rule that will not finish is dropped on the budget, in {clock.Elapsed.TotalSeconds:0.0} s",
                "unbounded, this ran on the main thread against every label of every frame and the game stopped");

            check(timing.Count == 1 && timing[0].Contains("switched off"),
                "and says so once, naming the rule and where to fix it",
                "a pattern arriving in somebody else's translation is not something a player can guess at");

            var after = slow.Find(2, "Canvas/Q", "Arial", cannotMatch);
            check(after != null && after.replacement == "Good" && timing.Count == 1,
                "the next label does not pay it again",
                "paying the budget per label is the freeze, slower — the verdict has to hold for the run");
        }

        private static void TheMemory(Action<bool, string, string> check)
        {
            var rules = With(Rule("path:Canvas/Quest/**"));

            var answer = rules.Find(1, "Canvas/Quest/Title", "Arial", "Go");
            check(ReferenceEquals(rules.Find(1, "somewhere/else/entirely", "Other", "Other"), answer),
                "a target is decided once and kept",
                "the path, the font and the text are re-read on every write; deciding again each time is the cost this avoids");

            rules.Find(2, "Canvas/Shop/Price", "Arial", "10");
            rules.Forget(2);
            check(rules.Find(2, "Canvas/Quest/Title", "Arial", "Go") != null,
                "forgetting one target decides it again",
                "UI Toolkit recycles elements, so an entry each would grow for the life of the process");

            rules.Load(new[] { Rule("path:Canvas/Shop/**") });
            check(rules.Find(1, "Canvas/Quest/Title", "Arial", "Go") == null,
                "loading rules drops every answer decided under the old ones",
                "a rule taken back must stop repainting what it was already applied to, without restarting the game");

            rules.ForgetAll();
            check(rules.Any && rules.Rules.Count == 1,
                "and a scene change forgets answers, never rules",
                "instance ids do not survive a scene; what somebody wrote does");

            var empty = new FontRules();
            check(!empty.Any && empty.Find(1, "Canvas/Q", "Arial", "Go") == null,
                "no rules, no answer",
                "the common case by far, and the one the write path asks about before building a path");

            empty.Load(null);
            check(!empty.Any,
                "and loading nothing is loading no rules",
                "a file with no font section is a file with no font rules, not a file to refuse");
        }
    }
}
