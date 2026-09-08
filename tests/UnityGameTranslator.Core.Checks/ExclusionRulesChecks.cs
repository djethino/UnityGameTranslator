using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What somebody's exclusion patterns keep out of the translation, across a whole sequence.
    ///
    /// 🔴 **Why these are sequences and not answers.** Whether a path matches a pattern is settled
    /// elsewhere (<see cref="ExclusionPatterns"/>, checked on its own). What is checked HERE is the
    /// memory: the decision is taken once per target and kept, because it runs on every text write.
    /// Every defect this can carry is a right answer given at the wrong moment — a pattern written
    /// and doing nothing until the game is restarted, or an answer surviving the rule it came from.
    /// A moment only exists in a sequence.
    ///
    /// ⚠ The silent direction is the dangerous one. A rule that refuses too much shows up on
    /// screen at once; a rule that quietly stops refusing sends a chat window to a model and
    /// uploads it to everybody, and nothing anywhere says so.
    /// </summary>
    internal static class ExclusionRulesChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            NothingWritten(check);
            Deciding(check);
            WhenTheRulesMove(check);
            Forgetting(check);
            Writing(check);
        }

        private static void NothingWritten(Action<bool, string, string> check)
        {
            var rules = new ExclusionRules();

            check(!rules.Any && rules.Patterns.Count == 0,
                "nothing written, nothing held",
                "the common case by far, and the one the write path asks about before building a path");

            check(!rules.Decide(1, "Canvas/Chat/Message"),
                "and nothing is excluded",
                "an empty rule set that refused anything would hide text nobody asked to hide");

            check(!rules.TryRecall(1, out _),
                "with nothing remembered either",
                "remembering a refusal never taken would answer for a pattern written a second later");
        }

        private static void Deciding(Action<bool, string, string> check)
        {
            var rules = new ExclusionRules();
            rules.Add("Canvas/Chat/**");

            check(rules.Decide(7, "Canvas/Chat/Message"),
                "a path under a written pattern is excluded",
                "this is the promise: that part of the game does not go to a model");

            check(rules.TryRecall(7, out bool remembered) && remembered,
                "and the answer is kept for that target",
                "deciding again on every text write means walking the hierarchy on every frame");

            check(!rules.Decide(8, "Canvas/Quest/Title"),
                "a path outside them is not",
                "an exclusion that spread would leave the game untranslated with no reason given");

            check(rules.TryRecall(8, out bool no) && !no,
                "and THAT answer is kept too",
                "keeping only the refusals means paying for the path of every ordinary label, for ever");

            check(!rules.Decide(9, null),
                "a target with no path is not excluded",
                "an unnamed target is unknown, and unknown is not a reason to hide its text");
        }

        private static void WhenTheRulesMove(Action<bool, string, string> check)
        {
            // 🔴 The defect this whole class exists for: an answer decided against patterns that
            // no longer exist. It is silent in both directions and survives until the game restarts.
            // ⚠ A pattern FIRST, or nothing is remembered and the case proves nothing: Decide
            // answers no and forgets, with no rules written. That version passed with the rule
            // deliberately broken, which is the definition of decoration.
            var rules = new ExclusionRules();
            rules.Add("Canvas/Quest/**");
            rules.Decide(1, "Canvas/Chat/Message");
            rules.Add("Canvas/Chat/**");

            check(!rules.TryRecall(1, out _),
                "writing a pattern drops what was decided without it",
                "otherwise the pattern does nothing to anything already on screen, until the game restarts");

            var removing = new ExclusionRules();
            removing.Add("Canvas/Chat/**");
            removing.Decide(1, "Canvas/Chat/Message");
            removing.Remove("Canvas/Chat/**");

            check(!removing.TryRecall(1, out _),
                "and so does taking one back",
                "a refusal outliving its rule keeps text hidden that somebody just asked to see again");

            var clearing = new ExclusionRules();
            clearing.Add("Canvas/Chat/**");
            clearing.Decide(1, "Canvas/Chat/Message");
            clearing.Clear();

            check(!clearing.Any && !clearing.TryRecall(1, out _),
                "clearing takes the patterns and the answers together",
                "half a clear is the same defect wearing the word 'clear'");

            var loading = new ExclusionRules();
            loading.Add("Canvas/Chat/**");
            loading.Decide(1, "Canvas/Chat/Message");
            loading.Load(new[] { "Canvas/Quest/**" });

            check(loading.Patterns.Count == 1 && loading.Patterns[0] == "Canvas/Quest/**"
                  && !loading.TryRecall(1, out _),
                "loading a file replaces both",
                "a downloaded translation brings its author's exclusions, and the previous answers are about somebody else's");
        }

        private static void Forgetting(Action<bool, string, string> check)
        {
            var rules = new ExclusionRules();
            rules.Add("Canvas/Chat/**");
            rules.Decide(1, "Canvas/Chat/Message");
            rules.Decide(2, "Canvas/Quest/Title");
            rules.Forget(1);

            check(!rules.TryRecall(1, out _) && rules.TryRecall(2, out _),
                "one target can be forgotten alone",
                "UI Toolkit recycles elements by the hundred, so an entry each would grow for the life of the process");

            check(rules.Any,
                "and forgetting a target is not unwriting a pattern",
                "the memory is an optimisation; the patterns are what somebody asked for");

            rules.ForgetAll();
            check(!rules.TryRecall(2, out _) && rules.Any,
                "a scene change forgets every target and no pattern",
                "instance ids do not survive a scene, and the patterns have nothing to do with scenes");
        }

        private static void Writing(Action<bool, string, string> check)
        {
            var rules = new ExclusionRules();

            check(rules.Add("  Canvas/Chat/**  ") && rules.Patterns[0] == "Canvas/Chat/**",
                "a pattern is written without the spaces around it",
                "a trailing space in a typed pattern would match nothing, and look exactly like one that does");

            check(!rules.Add("Canvas/Chat/**") && rules.Patterns.Count == 1,
                "writing the same pattern twice writes nothing",
                "the answer is what tells the caller to save; saving on a duplicate rewrites the file for nothing");

            check(!rules.Add("") && !rules.Add("   ") && !rules.Add(null),
                "and an empty one is not a pattern",
                "an empty pattern in the file is a line nobody can read and nobody meant");

            check(!rules.Remove("Canvas/Quest/**"),
                "taking back what was never there changes nothing",
                "same reason: it must not report a change, or every failed removal saves the file");
        }
    }
}
