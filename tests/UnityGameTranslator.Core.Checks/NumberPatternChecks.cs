using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Reading a sentence that carries concrete numbers as the cached sentence it came from.
    ///
    /// ⚠ **What is at stake, both ways.** A game showing "You have 3 apples", then 4, then 17,
    /// shows ONE sentence. Recognised, it is translated once and every later value costs nothing;
    /// missed, it is a call per number, for ever. Matched too loosely, the player reads a sentence
    /// about something else entirely.
    ///
    /// ⚠ Written against <see cref="NumberPatterns.Match"/> — texts in, slots and values out —
    /// and never against the expression that happens to implement it here. A Lua Core has patterns
    /// and no alternation and would answer the same questions another way; what must agree is the
    /// answers.
    /// </summary>
    internal static class NumberPatternChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            OneSlot(check);
            SeveralSlots(check);
            NotAMatch(check);
            Edges(check);
        }

        private static void OneSlot(Action<bool, string, string> check)
        {
            check(NumberPatterns.Match("You have 3 apples", "You have [!v*0] apples", out var one)
                  && one.Count == 1 && one[0] == "3",
                "a sentence with a number reads as its cached pattern",
                "this is what makes one entry serve every value the game will ever put there");

            check(NumberPatterns.Match("You have 17 apples", "You have [!v*0] apples", out var big)
                  && big[0] == "17",
                "whatever the value",
                "otherwise the second number is a new sentence, and the call is paid again");

            check(NumberPatterns.Match("Cost 3.5 gold", "Cost [!v*0] gold", out var dec) && dec[0] == "3.5",
                "a decimal point stays with its number",
                "splitting it would leave half a value in the slot and half in the prose");

            check(NumberPatterns.Match("Prix 3,5 or", "Prix [!v*0] or", out var comma) && comma[0] == "3,5",
                "and so does a decimal comma",
                "the game formats by locale, and the pattern was captured in one of them");

            check(NumberPatterns.Match("Done 50% now", "Done [!v*0] now", out var pct) && pct[0] == "50%",
                "a percent belongs to its number",
                "left out, the sign lands in the prose and the sentence stops matching itself");

            check(NumberPatterns.Match("Owed -5 gold", "Owed [!v*0] gold", out var neg) && neg[0] == "-5",
                "and so does a minus sign",
                "a debt is a value, not a dash followed by a value");
        }

        private static void SeveralSlots(Action<bool, string, string> check)
        {
            check(NumberPatterns.Match("3 of 12", "[!v*0] of [!v*1]", out var pair)
                  && pair.Count == 2 && pair[0] == "3" && pair[1] == "12",
                "two slots keep their own values",
                "swapping them would show twelve of three");

            // 🔴 The reason the slot numbers travel with the pattern at all: a translation is free
            // to reorder them, so capture order is not slot order.
            check(NumberPatterns.Match("12 sur 3", "[!v*1] sur [!v*0]", out var swapped)
                  && swapped[0] == "3" && swapped[1] == "12",
                "and a translated pattern may reorder them",
                "values are keyed by SLOT, not by the order the groups happen to appear in");

            check(NumberPatterns.Match("5 and 5", "[!v*0] and [!v*0]", out var twice)
                  && twice.Count == 1 && twice[0] == "5",
                "the same slot used twice is one value",
                "a sentence that shows a number twice shows the same number twice");
        }

        private static void NotAMatch(Action<bool, string, string> check)
        {
            check(!NumberPatterns.Match("You have 3 pears", "You have [!v*0] apples", out _),
                "different words are a different sentence",
                "matching them would put a translation about apples on a line about pears");

            // ⚠ Anchored at both ends. Without that, a pattern would match anything containing it,
            // and the longest sentence in the game would answer for every line that starts alike.
            check(!NumberPatterns.Match("You have 3 apples today", "You have [!v*0] apples", out _),
                "and so is the same sentence with more after it",
                "a pattern that matched a prefix would swallow every longer line beginning the same way");

            check(!NumberPatterns.Match("You have many apples", "You have [!v*0] apples", out _),
                "a slot takes a number and nothing else",
                "a word in the slot is a different sentence, not the same one with a value");
        }

        private static void Edges(Action<bool, string, string> check)
        {
            check(!NumberPatterns.Match("You have 3 apples", "You have some apples", out _),
                "a pattern with no slot is not a pattern",
                "it is a plain sentence, and the exact cache already answers for it");

            check(!NumberPatterns.Match("anything", null, out _)
                  && !NumberPatterns.Match("anything", "", out _)
                  && !NumberPatterns.Match(null, "[!v*0]", out _),
                "nothing in, no match out",
                "a missing side is a question with no subject, not a match to be found");

            // ⚠ The pattern text is escaped before the slots are cut into it: a game writing regex
            // characters in its own prose must not turn its sentence into an expression.
            check(NumberPatterns.Match("Ready? 3 (of 4)", "Ready? [!v*0] (of [!v*1])", out var meta)
                  && meta[0] == "3" && meta[1] == "4",
                "a sentence full of expression characters is still just a sentence",
                "'?' and '(' are what a game writes, and unescaped they would rewrite the pattern's meaning");
        }
    }
}
