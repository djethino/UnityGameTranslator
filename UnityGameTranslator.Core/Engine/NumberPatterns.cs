using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Matching a sentence that carries concrete numbers against a translation cached with slots.
    ///
    /// 🔴 **What it buys, and it is the whole reason the cache is affordable.** A game showing
    /// "You have 3 apples", then 4, then 17, shows one sentence — not seventeen. Stored once as
    /// "You have [!v*0] apples", every later value has to be recognised as that same line, and the
    /// numbers put back where they were. A pattern that stops matching costs one call per value,
    /// for ever; one that matches too much shows a sentence about something else.
    ///
    /// ⚠ **It works on the translated side too**, which is why the indices travel with it: a
    /// translation is free to reorder the slots — "[!v*1] of [!v*0]" — so capture group i does not
    /// name slot i. The list says which slot each group belongs to.
    ///
    /// 🔴 **Pure by contract**, like its neighbours in Engine/: strings in, an answer out. No
    /// Unity, no state, no clock. Linked by tests/UnityGameTranslator.Core.Checks.
    ///
    /// ⚠ **A port has to describe this in cases, never as an expression.** The regex is a .NET
    /// convenience; a Lua Core has patterns and no alternation, and would build the same answers
    /// another way. What must agree is which texts match a pattern and which numbers come out —
    /// which is what <see cref="Match"/> exists for, and what the cases are written against.
    ///
    /// Moved out of TranslatorCore on 2026-09-08 (step 6 of analyse/plan-prealables-couches.md),
    /// verbatim: same code, same comments, same answers.
    /// </summary>
    public static class NumberPatterns
    {
        // Matches [!v*N] and captures its index
        internal static readonly Regex PlaceholderIndexPattern = new Regex(@"\[!v\*(\d+)\]", RegexOptions.Compiled);

        /// <summary>
        /// Build a regex matching a placeholder pattern against text with concrete numbers.
        /// Each [!v*N] becomes a number-capture group; capture group i+1 corresponds to
        /// placeholderIndices[i]. Works on original keys AND on translated values (which
        /// may reorder the placeholders). Returns null if the pattern has no placeholders.
        /// </summary>
        internal static Regex BuildPatternRegex(string patternText, out List<int> placeholderIndices, bool compiled = false)
        {
            placeholderIndices = new List<int>();
            if (string.IsNullOrEmpty(patternText)) return null;

            var matches = PlaceholderIndexPattern.Matches(patternText);
            if (matches.Count == 0) return null;

            try
            {
                string pattern = Regex.Escape(patternText);
                foreach (Match match in matches)
                {
                    placeholderIndices.Add(int.Parse(match.Groups[1].Value));
                    string placeholder = Regex.Escape(match.Value);
                    // Replace one occurrence at a time so capture group order
                    // follows appearance order even with duplicated indices
                    int idx = pattern.IndexOf(placeholder, StringComparison.Ordinal);
                    if (idx < 0) return null;
                    pattern = pattern.Substring(0, idx) + @"(-?\d+(?:[.,]\d+)?%?)"
                        + pattern.Substring(idx + placeholder.Length);
                }
                return new Regex("^" + pattern + "$", compiled ? RegexOptions.Compiled : RegexOptions.None);
            }
            catch { return null; }
        }

        /// <summary>
        /// Does this text read as that pattern with numbers in its slots — and if so, which number
        /// sits in which slot?
        ///
        /// ⚠ **The form a port is held to.** Everything above hands back a .NET Regex, which is a
        /// convenience of this runtime and not part of the rule; this asks and answers in strings
        /// and numbers only, so the cases mean the same thing in any language. Values are keyed by
        /// SLOT, not by capture order, because a translated pattern is free to reorder them.
        /// </summary>
        public static bool Match(string text, string patternText, out Dictionary<int, string> bySlot)
        {
            bySlot = new Dictionary<int, string>();
            if (string.IsNullOrEmpty(text)) return false;

            var regex = BuildPatternRegex(patternText, out List<int> slots);
            if (regex == null) return false;

            var match = regex.Match(text);
            if (!match.Success) return false;

            for (int i = 0; i < slots.Count && i + 1 < match.Groups.Count; i++)
                bySlot[slots[i]] = match.Groups[i + 1].Value;

            return true;
        }
    }
}
