using System;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// How the text a component receives relates to the text it held a moment ago.
    ///
    /// Typewriting and procedural-text (concat) detection both rest on one question — *is this the
    /// previous text with more appended?* — which was written out six times across
    /// <see cref="TranslatorPatches"/>. Two of those six carried an extra condition, and because
    /// the shared part had no name, a reader could not tell a deliberate extra condition from a
    /// forgotten one. Naming the questions is the whole point of this file.
    ///
    /// 🔴 **Pure by contract: no Unity, no state, no clock, no logging.** That is what lets
    /// `tests/UnityGameTranslator.Core.Checks` link this FILE (not the assembly) and run these
    /// rules with no game and no runtime — see that project's csproj. Adding a `using UnityEngine`
    /// here breaks its build, which is the intended alarm rather than an accident.
    /// </summary>
    public static class TextRelations
    {
        /// <summary>
        /// The shared question: <paramref name="current"/> is <paramref name="previous"/> with
        /// more text appended.
        ///
        /// ⚠ Says nothing about empty or null inputs, on purpose: the call sites did not all guard
        /// them the same way, so each keeps its own check. Passing null throws here exactly as it
        /// threw before this was extracted.
        ///
        /// 🔴 **Ordinal, and that is load-bearing.** The six call sites used the default
        /// <c>StartsWith(string)</c>, which compares LINGUISTICALLY. Three reasons that was wrong
        /// here, none of them cosmetic:
        ///
        /// 1. **It answers a different question.** A linguistic prefix test ignores characters the
        ///    collation deems irrelevant — soft hyphens, zero-width joiners, some format marks —
        ///    all of which occur in real game text (justification, emoji sequences, Arabic and
        ///    Indic joining). Two texts differing only by those would read as "the same text that
        ///    grew", and a delta would be cut in the wrong place. What is wanted is literally
        ///    "these characters, then more".
        /// 2. **It is not the same test on every runtime.** Unity's Mono, IL2CPP and the .NET that
        ///    runs the checks project do not carry the same collation data — so a culture-sensitive
        ///    rule verified here would not be the rule running in a game. That alone would make
        ///    UnityGameTranslator.Core.Checks a decoration.
        /// 3. **It is far slower**, and this runs on every single set_text of every text component.
        ///
        /// ⚠ Says nothing about empty or null inputs, on purpose: the call sites did not all guard
        /// them the same way, so each keeps its own check. Passing null throws here exactly as it
        /// threw before this was extracted.
        /// </summary>
        public static bool Grows(string previous, string current)
        {
            return current.Length > previous.Length
                   && current.StartsWith(previous, StringComparison.Ordinal);
        }

        /// <summary>Most characters a single typewriter step is assumed to reveal.</summary>
        public const int TypewriterMaxCharsPerStep = 3;

        /// <summary>
        /// Growth that looks like a **typewriter reveal**: a handful of characters at a time.
        /// Used to take a component back OUT of concat mode when the game turns out to be
        /// revealing rather than assembling.
        ///
        /// ⚠ The tests are ordered length → step size → prefix, which is the order the call site
        /// used. Calling <see cref="Grows"/> first would scan the whole prefix before finding out
        /// the step was too big — same answer, needless work on long texts.
        /// </summary>
        public static bool LooksLikeTypewriterGrowth(string previous, string current)
        {
            return current.Length > previous.Length
                   && current.Length - previous.Length <= TypewriterMaxCharsPerStep
                   && current.StartsWith(previous, StringComparison.Ordinal);
        }

        /// <summary>
        /// Growth that looks like **procedural assembly**: the appended part carries something
        /// other than layout whitespace.
        ///
        /// Without that condition, a game that appends a lone newline at start-up (which happens)
        /// gets flagged as building text procedurally and every later write is treated as a delta.
        /// </summary>
        public static bool LooksLikeConcatGrowth(string previous, string current)
        {
            if (!Grows(previous, current)) return false;
            return HasContentFrom(current, previous.Length);
        }

        /// <summary>
        /// True when <paramref name="text"/> holds anything but line breaks, spaces and tabs from
        /// <paramref name="startIndex"/> onwards.
        ///
        /// ⚠ Deliberately NOT <c>char.IsWhiteSpace</c>: the original test listed these four
        /// characters and no others, so a non-breaking space counts as content here. Widening it
        /// would change which components get flagged as procedural.
        /// </summary>
        private static bool HasContentFrom(string text, int startIndex)
        {
            for (int i = startIndex; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch != '\n' && ch != '\r' && ch != ' ' && ch != '\t')
                    return true;
            }
            return false;
        }
    }
}
