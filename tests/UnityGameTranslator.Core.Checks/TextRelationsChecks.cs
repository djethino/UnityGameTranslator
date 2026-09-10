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
