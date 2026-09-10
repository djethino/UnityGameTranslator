using System;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The question typewriting and procedural-text detection both rest on, and the two extra
    /// conditions that make them different questions.
    ///
    /// ⚠ These cases are written from the SPECIFICATION — what each rule is supposed to answer —
    /// and not from reading the implementation back. A case derived from the code only proves the
    /// code agrees with itself.
    /// </summary>
    internal static class TextRelationsChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            Growth(check);
            TypewriterSteps(check);
            ConcatDeltas(check);
            SameSentenceDressedDifferently(check);
        }

        /// <summary>
        /// A reveal that walks a tag along a finished line, rather than building the string.
        ///
        /// 🔴 **Measured, not imagined**: one sentence revealed this way cost 93 requests to the
        /// model and 91 cache entries, and a quarter of that game's translation file — 258 lines
        /// of 1050 — was five sentences written out fifty-two times each. Every frame changed the
        /// raw text and <see cref="TextRelations.Grows"/> was false, because the tag MOVED rather
        /// than grew, so every frame read as a brand new line.
        /// </summary>
        private static void SameSentenceDressedDifferently(Action<bool, string, string> check)
        {
            // The two frames that cost 93 calls, verbatim from the game's log.
            Same(check,
                 "<color=#8f8f8f><i>T</i></color><color=#00000000>he bastards are all in on it together!</color>",
                 "<color=#8f8f8f><i>Th</i></color><color=#00000000>e bastards are all in on it together!</color>",
                 true, "🔴 the tag moved one character along a sentence that never changed");

            Same(check,
                 "<color=#8f8f8f><i>The bastards are all in on it together!</i></color>",
                 "<color=#8f8f8f><i>T</i></color><color=#00000000>he bastards are all in on it together!</color>",
                 true, "the reveal starting, from the line already fully written");

            Same(check, "Hello", "Hello", true, "identical is trivially the same sentence");
            Same(check, "<b>Hello</b>", "<i>Hello</i>", true,
                 "a game repainting a label says the same thing; the tags come back from the cache key, not from here");

            // ── What it must NOT swallow ──
            Same(check, "Hello", "Goodbye", false, "two sentences are two sentences");
            Same(check, "Hel", "Hello", false,
                 "🔴 real growth is NOT the same content — it is the case the growth rule exists for, and swallowing it would stop every typewriter being detected at all");
            Same(check, "<b>Hel</b>", "<b>Hello</b>", false,
                 "markup around a text that genuinely grew changes nothing: the content grew");
            Same(check, "You have 3 apples", "You have 5 apples",
                 false, "a number is CONTENT, not decoration — this only ever undresses markup");

            Same(check, null, "Hello", false, "nothing said is not the same as something said");
            Same(check, "Hello", null, false, "and the other way round");

            ATemplateAndItsExpansion(check);
        }

        /// <summary>
        /// A template the game expands in place, and the line it expands into.
        ///
        /// 🔴 The two pairs below are verbatim from a game's log (2026-09-10). The template was
        /// translated and cached; written back, the game could no longer find `*Overclock*` and
        /// `{0}` to expand, and the player read the asterisks.
        /// </summary>
        private static void ATemplateAndItsExpansion(Action<bool, string, string> check)
        {
            Expanded(check,
                "*Tripower*: Gain *Double Strength*.",
                "<color=#73E5AC>Tripower</color><sprite=\"buff\" name=triangle>: Gain <color=#E77531>Double Strength</color><sprite=\"buff\" name=power_rate>.",
                true, "the keyword became a colour and an icon — same words, and the icons leave no word behind");

            Expanded(check,
                "*Overclock* ({0}): Add {1} Strength.",
                "<color=#FF78C1>Overclock</color><sprite=\"buff\" name=overclock> (<color=#F4FF58>9</color>): Add <color=#F4FF58>10</color> Strength.",
                true, "and the value slots became values — the slot and its value are one thing seen twice");

            // 🔴 The states IN BETWEEN, which the first version of this rule let through — and which
            // the game's own file then showed, half-resolved, one line each.
            Expanded(check,
                "*Overclock* ({0}): Add {1} Strength.",
                "*Overclock* (9): Add 10 Strength.",
                true, "🔴 the slots were filled and the keyword was not: a half-resolved state is still not a line anybody reads");

            Expanded(check,
                "When loading {0} Energy of the same point, add {1} Strength.",
                "When loading 2 Energy of the same point, add {1} Strength.",
                true, "and it resolves them one at a time, so one slot down is already a supersession");

            Expanded(check,
                "While Single Stars are the only loaded *Attack Units*, they have *Double Strength*.",
                "While Single Stars are the only loaded *Attack Units*, they have <color=#E77531>Double Strength</color><sprite=\"buff\" name=power_rate>.",
                true, "one keyword expanded and the other not — verbatim from the file, where it became its own line");

            // 🔴 What must NOT match, in the order the mistakes would be made.
            Expanded(check, "Add 5 HP", "Add <color=#F4FF58>7</color> HP",
                     false, "a value simply being updated is not an expansion: nothing in the previous text was a token");

            Expanded(check, "*sigh* I suppose we should go.", "*sigh* I suppose we should go, then.",
                     false, "prose using asterisks stays prose: no markup arrived, so nothing was expanded");

            Expanded(check, "*sigh*", "<i>*sigh*</i>",
                     false, "🔴 the asterisks SURVIVED: prose was italicised, no token was resolved — found by this very case");

            Expanded(check, "*Ready* in {0} turns", "<color=#FF0000>Ready</color> in {0} turns",
                     true, "the keyword resolved and a slot is still open: superseded all the same, and the next state supersedes this one");

            Expanded(check, "<b>*Ready*</b>", "<color=#FF0000>Ready</color>",
                     true, "markup on the previous text does not make it final — the keyword inside it had still to be resolved");

            Expanded(check, "He said *nothing*.", "He said nothing.",
                     true, "⚠ what the rule gives up: prose losing an emphasis reads as a resolution, and is left untranslated in that form. Said out loud, never silently");

            Expanded(check, "*Overclock* ({0}): Add {1} Strength.",
                     "<color=#FF78C1>Overheat</color><sprite=\"buff\" name=overclock> (<color=#F4FF58>9</color>): Add <color=#F4FF58>10</color> Strength.",
                     false, "one word apart is a different line, however alike the shape");

            Expanded(check, "Add Strength.", "<color=#F4FF58>Add Strength.</color>",
                     false, "identical words, but the previous text held no token: this is SameContent, and it must not be answered here");

            Expanded(check, null, "<b>x</b>", false, "nothing expands into something");
            Expanded(check, "*x*", null, false, "and the other way round");
        }

        private static void Expanded(Action<bool, string, string> check, string previous, string current,
                                     bool expected, string why)
        {
            bool actual = TextRelations.SameAfterExpansion(previous, current);
            check(actual == expected, $"SameAfterExpansion({Show(previous)}, …) -> {actual}", why);
        }

        private static void Same(Action<bool, string, string> check, string previous, string current,
                                 bool expected, string why)
        {
            bool actual = TextRelations.SameContent(previous, current);
            check(actual == expected, $"SameContent({Show(previous)}, {Show(current)}) -> {actual}", why);
        }

        /// <summary>The shared brick: same text, plus something at the end.</summary>
        private static void Growth(Action<bool, string, string> check)
        {
            Grows(check, "Hel", "Hello", true, "the previous text with more after it");
            Grows(check, "", "H", true, "growing from nothing still grows");
            Grows(check, "Hello", "Hello", false, "identical is not growing");
            Grows(check, "Hello", "Hell", false, "shorter is not growing");
            Grows(check, "Hello", "Goodbye", false, "a different text is not growing");
            Grows(check, "Hello", "XHello", false, "appended at the FRONT is not growing");

            // The concat path compares against a text that already carries markup, so a prefix
            // must stay a prefix once tags are in play.
            Grows(check, "<b>Hel", "<b>Hello</b>", true, "markup is just characters here");

            // The case Back to the Dawn is expected to produce: same visible text, a marker that
            // moved. Nothing grows, so neither detector fires — this is the documented blind spot,
            // pinned here so a future change to it is a deliberate one.
            Grows(check, "<v>Hel</v>lo", "<v>Hell</v>o", false,
                  "a marker that MOVES does not grow — the blind spot, on purpose");

            // 🔴 These pin the comparison to Ordinal. A linguistic prefix test treats soft hyphens
            // and zero-width joiners as irrelevant, so it would answer TRUE to both of these — the
            // two texts would be read as one that grew, and the delta would be cut in the wrong
            // place. Real game text carries these: justification, emoji, Arabic and Indic joining.
            // ⚠ Written as escapes, never as the characters themselves: a soft hyphen pasted into
            // a source file is invisible in every editor, and the day someone "tidies" the line it
            // vanishes without a trace and the case silently stops testing anything.
            Grows(check, "a\u00ADb", "abcd", false,
                  "a soft hyphen is a character here, not a decoration");
            Grows(check, "Hel\u200Dlo", "Hello world", false,
                  "and so is a zero-width joiner");
        }

        /// <summary>Growth by a few characters: a reveal, not an assembly.</summary>
        private static void TypewriterSteps(Action<bool, string, string> check)
        {
            Typewriter(check, "Hell", "Hello", true, "one character at a time");
            Typewriter(check, "He", "Hello", true, "three characters is still a reveal");
            Typewriter(check, "H", "Hello", false, "four is too many to be one keystroke");
            Typewriter(check, "Hello", "Hello", false, "not moving is not revealing");
            Typewriter(check, "Hello", "Hell", false, "shrinking is not revealing");
            Typewriter(check, "Hello", "Hey", false, "a different text is not revealing");

            // Guards the constant against being widened by accident: at four, this must be false.
            check(TextRelations.TypewriterMaxCharsPerStep == 3,
                  "TypewriterMaxCharsPerStep == 3",
                  "the step size the concat unflag was written against");
        }

        /// <summary>Growth that carries content: an assembly, not a stray line break.</summary>
        private static void ConcatDeltas(Action<bool, string, string> check)
        {
            Concat(check, "Sword", "Sword\n+5 damage", true, "a real second part");
            Concat(check, "Sword", "Sword\n", false, "a lone newline is not procedural text");
            Concat(check, "Sword", "Sword \t\r\n", false, "nor any run of layout whitespace");
            Concat(check, "Sword", "Sword", false, "identical is not growing, so not concat either");
            Concat(check, "Sword", "Shield", false, "a different text is not concat");

            // The four characters are listed one by one in the rule rather than deferred to
            // char.IsWhiteSpace, so a non-breaking space counts as content. Widening it would
            // change which components get flagged.
            Concat(check, "Sword", "Sword\u00A0", true,
                   "a non-breaking space counts as content, unlike a plain one");
        }

        private static void Grows(Action<bool, string, string> check, string previous, string current,
                                  bool expected, string why)
        {
            bool actual = TextRelations.Grows(previous, current);
            check(actual == expected, $"Grows({Show(previous)}, {Show(current)}) -> {actual}", why);
        }

        private static void Typewriter(Action<bool, string, string> check, string previous, string current,
                                       bool expected, string why)
        {
            bool actual = TextRelations.LooksLikeTypewriterGrowth(previous, current);
            check(actual == expected, $"Typewriter({Show(previous)}, {Show(current)}) -> {actual}", why);
        }

        private static void Concat(Action<bool, string, string> check, string previous, string current,
                                   bool expected, string why)
        {
            bool actual = TextRelations.LooksLikeConcatGrowth(previous, current);
            check(actual == expected, $"Concat({Show(previous)}, {Show(current)}) -> {actual}", why);
        }

        /// <summary>Readable in a result line: blanks and line breaks must stay visible.</summary>
        private static string Show(string value)
        {
            if (value == null) return "(null)";
            if (value.Length == 0) return "(empty)";
            // The invisible ones matter most here: a result line showing "ab" for two different
            // strings would make a failure impossible to read.
            return "\"" + value.Replace("\n", "\\n").Replace("\r", "\\r")
                               .Replace("\t", "\\t").Replace("\u00A0", "\\u00A0")
                               .Replace("\u00AD", "\\u00AD").Replace("\u200D", "\\u200D") + "\"";
        }
    }
}
