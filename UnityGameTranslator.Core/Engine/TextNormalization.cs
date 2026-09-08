using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// What a text looks like once the decoration is set aside: line endings made one, numbers and
    /// markup lifted out into placeholders and put back, and the two questions "does this carry a
    /// word at all" and "is this the same in every language".
    ///
    /// 🔴 **Pure by contract, and that is the whole reason this file exists.** No Unity, no state,
    /// no clock, no disk: a string in, an answer out. It is linked by
    /// tests/UnityGameTranslator.Core.Checks, which cannot reference the Core — that would drag a
    /// game engine into a console app — so the day somebody adds a `using UnityEngine` here the
    /// checks project stops compiling. That is the alarm, not an accident.
    ///
    /// ⚠ **The placeholder spellings are a contract, not a detail.** `[!v*N]` for a number and
    /// `[!t*N]` for a markup tag travel to a model and come back; the socle refuses a reply that
    /// broke them (Common.Placeholders), the manager scores models against the same rule, and a
    /// translation file on disk holds them. Changing a spelling is a migration.
    ///
    /// ⚠ **Two questions about letters live side by side, and they are not the same question.**
    /// <see cref="IsNumericOrSymbol"/> asks of a whole text "is there nothing to translate here",
    /// at the four doors into translation; <see cref="IsWordCategory"/> asks of one code point "is
    /// this part of a word", to decide whether a text can be recognised when the game hands it
    /// back. Both now read Unicode categories rather than lists of scripts, and they differ in
    /// exactly ONE place, on purpose: a private-use codepoint is one of our own shaped glyphs, so
    /// the readback form counts it as a letter (that is how it recognises our output) and the door
    /// does not (so our own output is never sent to a model).
    ///
    /// Moved out of TranslatorCore on 2026-09-08 (step 6 of analyse/plan-prealables-couches.md),
    /// verbatim: same code, same comments, same answers.
    /// </summary>
    public static class TextNormalization
    {
        // A text qualifies once it carries a real word: at least two letters, at least two of them
        // adjacent. Deliberately NOT a letter COUNT — one ideograph is one letter, so a threshold
        // tuned on the alphabet protected a Latin sentence while leaving its Chinese equivalent
        // exposed. Measured across the bench: identical results, minus the Latin assumption.
        internal const int ReadbackMinLetters = 2;
        internal const int ReadbackMinLetterRun = 2;

        /// <summary>
        /// Decoration-insensitive form of a text: rich-text tags dropped, every number and every one
        /// of our placeholders collapsed to '#', brace/bracket/emphasis decoration removed, whitespace
        /// collapsed, lowercased. Letters are preserved untouched, which is what makes the comparison
        /// safe: two texts only collide when they carry the same words.
        /// Returns null when the result holds fewer than ReadbackMinLetters letters — short numeric or
        /// symbolic strings ("3", "x2", "100%") all collapse onto each other and must never be matched
        /// this way.
        /// Hand-rolled rather than regex: this runs on the set_text path, thousands of calls per second.
        /// </summary>
        internal static string NormalizeForReadbackMatch(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var sb = new System.Text.StringBuilder(text.Length);
            int letters = 0;
            int run = 0, longestRun = 0;
            bool lastWasSpace = true;   // leading whitespace is dropped

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // Rich-text tag: <color=#...>, </color>, <b>, <size=..>…
                if (c == '<')
                {
                    int close = text.IndexOf('>', i + 1);
                    if (close > i && close - i <= 64)
                    {
                        char next = text[i + 1];
                        if (next == '/' || char.IsLetter(next)) { i = close; continue; }
                    }
                }

                // Our own placeholders ([!v*0], [!t*1], [!STR*2]) and any literal number the game
                // re-injected in their place — one and the same slot, so one and the same token.
                if (c == '[' && i + 2 < text.Length && text[i + 1] == '!')
                {
                    int close = text.IndexOf(']', i + 2);
                    if (close > i && close - i <= 16)
                    {
                        sb.Append('#'); lastWasSpace = false; run = 0; i = close; continue;
                    }
                }
                if (char.IsDigit(c))
                {
                    while (i + 1 < text.Length)
                    {
                        char nx = text[i + 1];
                        if (char.IsDigit(nx)) { i++; continue; }
                        // A separator belongs to the number only when a digit follows it: "3,5" is
                        // one number, "= 3, les points" is a number then punctuation. Swallowing that
                        // comma made the SAME sentence normalise differently depending on whether the
                        // slot still held our placeholder or the value the game had re-injected — so
                        // the guards upstream never recognised what the storage guard did.
                        if ((nx == '.' || nx == ',') && i + 2 < text.Length && char.IsDigit(text[i + 2])) { i += 2; continue; }
                        break;
                    }
                    if (i + 1 < text.Length && text[i + 1] == '%') i++;
                    sb.Append('#'); lastWasSpace = false; run = 0; continue;
                }

                // Decoration the game adds or removes around the same words
                if (c == '{' || c == '}' || c == '[' || c == ']' || c == '*') continue;

                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                    run = 0;
                    continue;
                }

                // A word is letters AND what rides on them: a vowel sign or a tone mark (Mn/Mc —
                // IsLetter says no, so कि, से, บ้าน counted one letter and every short Indic or
                // Thai text fell below the threshold, never indexed, never recognised), and a
                // private-use codepoint (Co) naming a shaped glyph — a conjunct IS letters.
                // Without those, a shaped word coming back through the setter was "not ours",
                // went to the AI, and its own translation entered the cache as a Hindi→Hindi key.
                // A letter outside the basic plane arrives as a surrogate pair: judged as one
                // code point (a lone surrogate is no letter), copied as its two halves.
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (IsWordCharacter(text, i)) { letters++; run++; if (run > longestRun) longestRun = run; }
                    else run = 0;
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                    lastWasSpace = false;
                    continue;
                }
                if (IsWordCharacter(c))
                {
                    letters++;
                    run++;
                    if (run > longestRun) longestRun = run;
                }
                else run = 0;
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }

            if (letters < ReadbackMinLetters || longestRun < ReadbackMinLetterRun) return null;
            // Trailing space, if any
            if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
            return sb.Length == 0 ? null : sb.ToString();
        }

        internal static bool IsWordCharacter(char c) => IsWordCategory(System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c));

        /// <summary>The same question for the code point at <paramref name="i"/>, a surrogate pair read as one.</summary>
        internal static bool IsWordCharacter(string s, int i) => IsWordCategory(System.Globalization.CharUnicodeInfo.GetUnicodeCategory(s, i));

        internal static bool IsWordCategory(System.Globalization.UnicodeCategory category)
        {
            switch (category)
            {
                case System.Globalization.UnicodeCategory.UppercaseLetter:
                case System.Globalization.UnicodeCategory.LowercaseLetter:
                case System.Globalization.UnicodeCategory.TitlecaseLetter:
                case System.Globalization.UnicodeCategory.ModifierLetter:
                case System.Globalization.UnicodeCategory.OtherLetter:
                case System.Globalization.UnicodeCategory.NonSpacingMark:
                case System.Globalization.UnicodeCategory.SpacingCombiningMark:
                case System.Globalization.UnicodeCategory.PrivateUse:
                    return true;
                default:
                    return false;
            }
        }

        // Placeholder format for extracted numbers: [!v*0], [!v*1], etc.
        // Exotic format to avoid collision with game text (e.g. [v0] used by some games).
        internal const string PlaceholderPrefix = "[!v*";
        internal const string PlaceholderSuffix = "]";

        internal static readonly Regex NumberPattern = new Regex(
            @"(?<!\[!v\*)(-?\d+(?:[.,]\d+)?%?)",
            RegexOptions.Compiled);

        // Matches any XML/HTML-like tag: <tag>, </tag>, <tag attr="val">, <tag/>, etc.
        internal static readonly Regex MarkupTagPattern = new Regex(
            @"<[^>]+>",
            RegexOptions.Compiled);

        internal const string TagPlaceholderPrefix = "[!t*";
        internal const string TagPlaceholderSuffix = "]";

        /// <summary>
        /// Remove all markup tags from a text — for comparisons against raw values
        /// (e.g. input mirrors: games wrap the typed value in color tags).
        /// </summary>
        public static string StripMarkupTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return MarkupTagPattern.Replace(text, "");
        }

        /// <summary>
        /// Extract markup tags from text, replacing them with [!t*N] placeholders.
        /// Returns the processed text and the list of extracted tags.
        /// </summary>
        public static string ExtractMarkupTags(string text, out List<string> extractedTags)
        {
            extractedTags = new List<string>();
            if (string.IsNullOrEmpty(text))
                return text;

            var matches = MarkupTagPattern.Matches(text);
            if (matches.Count == 0)
                return text;

            var result = new StringBuilder(text.Length);
            int lastIndex = 0;

            foreach (Match match in matches)
            {
                // Append text before this tag
                result.Append(text, lastIndex, match.Index - lastIndex);
                // Replace tag with placeholder
                int tagIndex = extractedTags.Count;
                extractedTags.Add(match.Value);
                result.Append(TagPlaceholderPrefix).Append(tagIndex).Append(TagPlaceholderSuffix);
                lastIndex = match.Index + match.Length;
            }

            // Append remaining text after last tag
            result.Append(text, lastIndex, text.Length - lastIndex);
            return result.ToString();
        }

        /// <summary>
        /// Restore [!t*N] placeholders back to their original markup tags.
        /// </summary>
        public static string RestoreMarkupTags(string text, List<string> tags)
        {
            if (string.IsNullOrEmpty(text) || tags == null || tags.Count == 0)
                return text;

            string result = text;
            for (int i = 0; i < tags.Count; i++)
            {
                result = result.Replace($"{TagPlaceholderPrefix}{i}{TagPlaceholderSuffix}", tags[i]);
            }
            return result;
        }

        /// <summary>
        /// Normalize line endings to Unix format (\n).
        /// Converts \r\n (Windows) and \r (old Mac) to \n.
        /// This ensures consistent keys across platforms.
        /// </summary>
        public static string NormalizeLineEndings(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Order is important: first \r\n, then \r
            // Otherwise \r\n would become \n\n
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        public static string ExtractNumbersToPlaceholders(string text, out List<string> extractedNumbers)
        {
            extractedNumbers = new List<string>();

            if (string.IsNullOrEmpty(text))
                return text;

            var matches = NumberPattern.Matches(text);
            if (matches.Count == 0)
                return text;

            var numbersWithIndex = new List<Tuple<string, int, int>>();
            foreach (Match match in matches)
            {
                if (!IsPartOfHexColor(text, match.Index, match.Length)
                    && !IsInsidePlaceholder(text, match.Index))
                {
                    numbersWithIndex.Add(Tuple.Create(match.Value, match.Index, match.Length));
                }
            }

            if (numbersWithIndex.Count == 0)
                return text;

            foreach (var num in numbersWithIndex)
            {
                extractedNumbers.Add(num.Item1);
            }

            var result = new StringBuilder(text);
            for (int i = numbersWithIndex.Count - 1; i >= 0; i--)
            {
                var num = numbersWithIndex[i];
                result.Remove(num.Item2, num.Item3);
                result.Insert(num.Item2, $"{PlaceholderPrefix}{i}{PlaceholderSuffix}");
            }

            return result.ToString();
        }

        /// <summary>
        /// Replace [!v*N] placeholders with live numbers captured by ResolveDisplayedText.
        ///
        /// ⚠ The same rule as the overload below, taking what its caller happens to hold: a
        /// dictionary keyed by slot rather than a list in slot order. It stayed behind in
        /// TranslatorCore for one build and that was enough to break the cut — a method of the
        /// enclosing class hides an imported one entirely, so every call resolved to the wrong
        /// overload and the compiler said so seven times.
        /// </summary>
        public static string RestoreNumbersFromPlaceholders(string text, IDictionary<int, string> numbersByIndex)
        {
            if (string.IsNullOrEmpty(text) || numbersByIndex == null || numbersByIndex.Count == 0)
                return text;

            string result = text;
            foreach (var kv in numbersByIndex)
                result = result.Replace($"{PlaceholderPrefix}{kv.Key}{PlaceholderSuffix}", kv.Value);
            return result;
        }

        public static string RestoreNumbersFromPlaceholders(string text, List<string> numbers)
        {
            if (string.IsNullOrEmpty(text) || numbers == null || numbers.Count == 0)
                return text;

            string result = text;
            for (int i = 0; i < numbers.Count; i++)
            {
                result = result.Replace($"{PlaceholderPrefix}{i}{PlaceholderSuffix}", numbers[i]);
            }
            return result;
        }

        internal static bool IsPartOfHexColor(string text, int index, int length)
        {
            for (int i = index - 1; i >= 0 && i >= index - 8; i--)
            {
                char c = text[i];
                if (c == '#')
                    return true;
                if (!IsHexChar(c))
                    break;
            }
            return false;
        }

        internal static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        /// <summary>
        /// Check if a number at the given index is inside a [!v*N] placeholder.
        /// </summary>
        internal static bool IsInsidePlaceholder(string text, int index)
        {
            // Look backwards for "[!v*" or "[!STR*" patterns
            // Protects numbers inside [!v*0], [!STR*0], [!t*0] from being extracted
            for (int i = index - 1; i >= Math.Max(0, index - 8); i--)
            {
                // Check for [!v* (4 chars)
                if (i >= 3 && text[i] == '*' && text[i - 1] == 'v' && text[i - 2] == '!' && text[i - 3] == '[')
                {
                    for (int j = index; j < Math.Min(text.Length, index + 4); j++)
                    {
                        if (text[j] == ']') return true;
                        if (!char.IsDigit(text[j])) break;
                    }
                }
                // Check for [!STR* (6 chars)
                if (i >= 5 && text[i] == '*' && text[i - 1] == 'R' && text[i - 2] == 'T' && text[i - 3] == 'S'
                    && text[i - 4] == '!' && text[i - 5] == '[')
                {
                    for (int j = index; j < Math.Min(text.Length, index + 4); j++)
                    {
                        if (text[j] == ']') return true;
                        if (!char.IsDigit(text[j])) break;
                    }
                }
                // Check for [!t* (4 chars)
                if (i >= 3 && text[i] == '*' && text[i - 1] == 't' && text[i - 2] == '!' && text[i - 3] == '[')
                {
                    for (int j = index; j < Math.Min(text.Length, index + 4); j++)
                    {
                        if (text[j] == ']') return true;
                        if (!char.IsDigit(text[j])) break;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Check if a normalized text is a "natural identity" — contains only digits,
        /// punctuation, whitespace, placeholders and rich text tags. Such text is the
        /// same in any language and an identity translation (key==value) is expected,
        /// not an AI failure.
        /// </summary>
        internal static bool IsNaturalIdentity(string normalizedText)
        {
            if (string.IsNullOrEmpty(normalizedText)) return true;

            // Strip placeholders [!v*N] [!STR*N] and rich text tags <...>
            // then check if remaining text has any letters
            var stripped = new System.Text.StringBuilder(normalizedText.Length);
            for (int i = 0; i < normalizedText.Length; i++)
            {
                char c = normalizedText[i];
                if (c == '[' && i + 1 < normalizedText.Length && normalizedText[i + 1] == '!')
                {
                    int end = normalizedText.IndexOf(']', i);
                    if (end > i) { i = end; continue; }
                }
                if (c == '<')
                {
                    int end = normalizedText.IndexOf('>', i);
                    if (end > i) { i = end; continue; }
                }
                stripped.Append(c);
            }

            return IsNumericOrSymbol(stripped.ToString());
        }

        /// <summary>
        /// Does this text carry no word at all — only digits, punctuation, symbols and spacing —
        /// so that it reads the same in every language and there is nothing to ask a model?
        ///
        /// 🔴 **Asked the other way round on purpose, and this is the whole rule.** What is
        /// enumerated below is what is CERTAINLY not a letter; anything else counts as one. The
        /// two mistakes are not worth the same: answering "there are letters" about "123" spends
        /// one call, which is visible and recoverable, while answering "no letters" about a real
        /// sentence leaves it untranslated for ever, in silence, with nothing to read anywhere. So
        /// the unknown case has to fall on the side of translating.
        ///
        /// 🔴 **It used to enumerate the letters instead, and by SCRIPT** — char.IsLetter, then
        /// CJK, hangul, kana, Cyrillic, Arabic, Devanagari and Thai as seven blocks written out by
        /// hand, with everything unlisted falling into "no letters". The seven were a belt against
        /// a suspicion — char.IsLetter failing for CJK on some IL2CPP runtime — that is documented
        /// nowhere and was never measured.
        ///
        /// ⚠ **On a healthy runtime the two shapes agree about every script**, measured on
        /// 2026-09-08: Hebrew, Greek, Armenian, Georgian, Bengali, Tamil, Kannada, Malayalam, Lao,
        /// Khmer, Burmese and Ethiopic all read as words under both. The belt was never load-
        /// bearing there. What it WAS is incomplete — it covered four scripts and left eighteen
        /// out, so on the runtime it was written for it would have protected Devanagari and not
        /// Bengali, Thai and not Lao, for no reason anybody wrote down. And a list of scripts is
        /// the one thing this codebase forbids itself: no logic specific to a language or a
        /// writing system.
        ///
        /// 🔴 **The answer that did change is the unclassifiable one.** A codepoint the runtime
        /// cannot place used to read as "nothing to translate"; it now reads as a letter, because
        /// OtherNotAssigned is deliberately absent from the list below. A stripped or broken table
        /// therefore makes this translate too much rather than go quiet — which is the whole point
        /// of asking the question this way round.
        ///
        /// ⚠ **PrivateUse is listed, so our own shaped glyphs are not letters here.** That keeps
        /// what the seven ranges did by accident: a word rendered entirely in our ligatures is not
        /// sent to a model, because it is already a translation of ours.
        /// <see cref="IsWordCategory"/> makes the opposite call for the same codepoints, and that
        /// is the one difference between the two — its job is to RECOGNISE our own output coming
        /// back through the game.
        /// </summary>
        public static bool IsNumericOrSymbol(string text)
        {
            string trimmed = text.Trim();

            for (int i = 0; i < trimmed.Length; i++)
            {
                // By code point: a letter outside the basic plane arrives as a surrogate pair, and
                // judging its halves separately would read an emoji and an astral letter alike.
                var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(trimmed, i);
                if (char.IsHighSurrogate(trimmed[i]) && i + 1 < trimmed.Length
                    && char.IsLowSurrogate(trimmed[i + 1])) i++;

                if (!IsCertainlyNotALetter(category)) return false;
            }

            return true;
        }

        /// <summary>
        /// The closed list: numbers, punctuation, symbols, separators, and the four kinds of
        /// non-character. Everything absent from it — every letter category, every mark, and
        /// anything the runtime could not classify — counts as a letter.
        /// </summary>
        internal static bool IsCertainlyNotALetter(System.Globalization.UnicodeCategory category)
        {
            switch (category)
            {
                case System.Globalization.UnicodeCategory.DecimalDigitNumber:
                case System.Globalization.UnicodeCategory.LetterNumber:
                case System.Globalization.UnicodeCategory.OtherNumber:

                case System.Globalization.UnicodeCategory.ConnectorPunctuation:
                case System.Globalization.UnicodeCategory.DashPunctuation:
                case System.Globalization.UnicodeCategory.OpenPunctuation:
                case System.Globalization.UnicodeCategory.ClosePunctuation:
                case System.Globalization.UnicodeCategory.InitialQuotePunctuation:
                case System.Globalization.UnicodeCategory.FinalQuotePunctuation:
                case System.Globalization.UnicodeCategory.OtherPunctuation:

                case System.Globalization.UnicodeCategory.MathSymbol:
                case System.Globalization.UnicodeCategory.CurrencySymbol:
                case System.Globalization.UnicodeCategory.ModifierSymbol:
                case System.Globalization.UnicodeCategory.OtherSymbol:

                case System.Globalization.UnicodeCategory.SpaceSeparator:
                case System.Globalization.UnicodeCategory.LineSeparator:
                case System.Globalization.UnicodeCategory.ParagraphSeparator:

                case System.Globalization.UnicodeCategory.Control:
                case System.Globalization.UnicodeCategory.Format:
                case System.Globalization.UnicodeCategory.Surrogate:

                // ⚠ Ours, not a script — see the note on the method above.
                case System.Globalization.UnicodeCategory.PrivateUse:
                    return true;

                // ⚠ OtherNotAssigned is NOT here, and its absence is the safety net: a runtime
                // that cannot classify a character makes this translate, never go quiet.
                default:
                    return false;
            }
        }
    }
}
