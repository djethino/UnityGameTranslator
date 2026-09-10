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
        /// Whether two texts say the same thing, and differ only in how they are dressed.
        ///
        /// 🔴 **Some games reveal a line by MARKUP rather than by building the string.** The whole
        /// sentence is there from the first frame; a tag walks along it, showing what is behind it
        /// and hiding the rest:
        ///
        /// <code>
        /// &lt;i&gt;T&lt;/i&gt;&lt;color=#00000000&gt;he bastards are all in on it together!&lt;/color&gt;
        /// &lt;i&gt;Th&lt;/i&gt;&lt;color=#00000000&gt;e bastards are all in on it together!&lt;/color&gt;
        /// </code>
        ///
        /// The raw text changes every frame and <see cref="Grows"/> is false — the tag MOVED, it
        /// did not grow — so every frame read as a brand new line. Measured on a real game: one
        /// sentence produced **93 requests to the model and 91 cache entries**, and a quarter of
        /// that game's translation file (258 lines of 1050) was five sentences written out
        /// fifty-two times each.
        ///
        /// 🔴 **The markup is REMOVED here, never replaced by a token**, and the difference is the
        /// whole rule: a token moves WITH its tag, so the two frames above stay different and
        /// nothing is gained. Verified by putting the token version in and watching both cases go
        /// red.
        ///
        /// ⚠ **This asks about CONTENT, and nothing else may be asked of it.** Whether a reveal is
        /// finished — whether a translation may be written to the screen — is a question about the
        /// RAW text, because the tag's position is the reveal's own state. Answering that one here
        /// would paint a whole line at once and destroy the animation the game was written to play.
        ///
        /// ⚠ And it must never reach the cache key, which keeps its tokens: stripped, we would know
        /// a line had markup but no longer WHERE to put it back in a translation whose words are in
        /// another order.
        /// </summary>
        public static bool SameContent(string previous, string current)
        {
            if (previous == null || current == null) return false;
            if (previous == current) return true;

            // Nothing to undress: a cheap way out of the common case, since most texts carry no
            // markup at all and this is asked on the hottest path in the mod.
            if (previous.IndexOf('<') < 0 && current.IndexOf('<') < 0) return false;

            return string.Equals(TextNormalization.StripMarkupTags(previous),
                                 TextNormalization.StripMarkupTags(current),
                                 StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether <paramref name="current"/> is <paramref name="previous"/> with the GAME's own
        /// decoration tokens resolved — a template and the line it was expanded into.
        ///
        /// 🔴 **Some games write their ability text as a template with their own token syntax**,
        /// assign it to the visible component, and expand it in place a moment later:
        ///
        /// <code>
        /// *Overclock* ({0}): Add {1} Strength.
        /// &lt;color=#FF78C1&gt;Overclock&lt;/color&gt;&lt;sprite="buff" name=overclock&gt; (&lt;color=#F4FF58&gt;9&lt;/color&gt;): Add &lt;color=#F4FF58&gt;10&lt;/color&gt; Strength.
        /// </code>
        ///
        /// `*word*` is a keyword to style and follow with an icon; `{N}` is a value slot. Every card
        /// game writes descriptions this way, and there is nothing to blame: the only unusual part
        /// is assigning the un-expanded template to the component instead of to a local string.
        ///
        /// 🔴 **Why this cannot be answered by TIME.** Measured on a real game: the template sat
        /// unchanged on screen for 501 ms, so the stabiliser declared it final — correctly, by its
        /// own rule. It was then translated into `*Surcadence* ({[!v*0]})…` and cached. Written back
        /// on the next hover, the game looks for `*Overclock*` and `{0}` to expand and finds
        /// neither, so the player reads the asterisks.
        ///
        /// 🔴 **And why this is NOT a rule about asterisks.** Nothing here judges one text. It
        /// compares TWO texts seen one after the other on the SAME component, exactly as
        /// <see cref="SameContent"/> does for markup — and a false match would mean the second text
        /// IS the first one dressed, which is the case being caught. Real prose (`*sigh*`,
        /// `*whispers*`) is never followed, on its own component, by a version of itself in which
        /// the asterisks have become tags.
        ///
        /// 🔴 **The whole rule in one sentence: the tokens were THERE and are GONE.** The previous
        /// text carried the game's own delimiters, the new one carries none, and markup arrived in
        /// their place. All three, or it is not an expansion:
        ///
        /// <code>
        /// *sigh*  →  &lt;i&gt;*sigh*&lt;/i&gt;      the asterisks survived: prose was italicised, nothing resolved
        /// Add 5 HP → Add &lt;color&gt;7&lt;/color&gt; HP   no token in the first: a value was updated
        /// &lt;b&gt;*Ready*&lt;/b&gt; → &lt;color&gt;Ready&lt;/color&gt;  the first was already dressed: a redecoration
        /// </code>
        ///
        /// ⚠ The first of those was found by its own check case, not by reasoning: without the
        /// "and gone" half, italicising `*sigh*` read as an expansion of it.
        ///
        /// ⚠ Digits are flattened on both sides because `{0}` becomes `9` — the slot and its value
        /// are the same thing seen twice. That is also why the two forms share one cache key.
        /// </summary>
        public static bool SameAfterExpansion(string previous, string current)
        {
            if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current)) return false;

            // The expansion is what PUTS the markup there. A previous text that already carried
            // tags is SameContent's business, and a current one that carries none was not expanded.
            if (current.IndexOf('<') < 0 || previous.IndexOf('<') >= 0) return false;

            // The tokens were there…
            if (previous.IndexOf('*') < 0 && previous.IndexOf('{') < 0) return false;

            // …and they are gone. Asked of the text with its markup removed, so a delimiter living
            // inside a tag's own attributes is not mistaken for one the game left standing.
            string bare = TextNormalization.StripMarkupTags(current);
            if (bare.IndexOf('*') >= 0 || bare.IndexOf('{') >= 0) return false;

            return string.Equals(Flatten(previous), Flatten(current), StringComparison.Ordinal);
        }

        /// <summary>
        /// A text with everything the expansion changes taken out: markup, the game's own token
        /// delimiters, and digits. What is left is the words, which the expansion never touches.
        /// </summary>
        private static string Flatten(string text)
        {
            string stripped = TextNormalization.StripMarkupTags(text);

            var sb = new System.Text.StringBuilder(stripped.Length);
            bool lastWasSpace = false;
            for (int i = 0; i < stripped.Length; i++)
            {
                char c = stripped[i];
                if (c == '*' || c == '{' || c == '}') continue;
                if (c >= '0' && c <= '9') continue;

                // Layout differs on the two sides — a sprite tag leaves none of the space its glyph
                // occupied — so runs of blank become one, and the ends are trimmed below.
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                    sb.Append(' ');
                    continue;
                }
                lastWasSpace = false;
                sb.Append(c);
            }

            return sb.ToString().Trim();
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
