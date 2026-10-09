using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The absolute sizes a text's own markup sets — <c>&lt;size=60&gt;</c>, <c>&lt;size=60px&gt;</c>,
    /// <c>&lt;size=+5&gt;</c> — multiplied by the factor the mod scales the component's font by.
    ///
    /// 🔴 **Why it exists** (2026-10-09): the Size setting multiplies a component's font size, and a
    /// word the game had set apart with an absolute size tag kept the game's size — a headline word
    /// left twice as big as the line it sits in once the rest was reduced. A relative size (<c>%</c>,
    /// <c>em</c>) follows the component by itself and is left alone.
    ///
    /// ⚠ **Only when the component's size is changed** (decided 2026-10-09): a factor of 1 leaves the
    /// text as it is — a game text in its own font and size is never rewritten for this.
    ///
    /// PURE by contract — linked into Core.Checks: a string and a factor in, a string out.
    /// </summary>
    internal static class SizeTags
    {
        // An absolute size: digits, optionally signed (a step from the component's size), optionally
        // in px, optionally quoted. A relative one (50%, 1.5em) does not match — nothing follows the
        // digits but the closing of the tag. ⚠ [0-9], not \d: \d takes every script's digits, which
        // the invariant parse below refuses — and markup values are written in ASCII digits.
        private static readonly Regex AbsoluteSize = new Regex(
            @"<size\s*=\s*(?<q>[""']?)(?<sign>[+-]?)(?<n>[0-9]+(?:\.[0-9]+)?)(?<px>px)?\k<q>\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Does this text set any absolute size of its own?</summary>
        public static bool HasAbsolute(string text)
            => !string.IsNullOrEmpty(text) && text.IndexOf("<size", StringComparison.OrdinalIgnoreCase) >= 0
               && AbsoluteSize.IsMatch(text);

        /// <summary>
        /// The text with every absolute size multiplied by <paramref name="factor"/>, the rest as it
        /// was. A factor of 1, or a text with no absolute size, comes back unchanged — the same string.
        /// </summary>
        public static string Scale(string text, float factor)
        {
            if (Math.Abs(factor - 1f) < 0.001f || !HasAbsolute(text)) return text;
            return AbsoluteSize.Replace(text, m =>
            {
                float n = float.Parse(m.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                string q = m.Groups["q"].Value;
                return "<size=" + q + m.Groups["sign"].Value + Format(n * factor) + m.Groups["px"].Value + q + ">";
            });
        }

        /// <summary>Two decimals at most, no trailing zeros, invariant: 42, 42.5, 41.75.</summary>
        private static string Format(float value)
            => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What each component was given with its sizes scaled, so the game reading it back and setting
    /// it again is not scaled a second time.
    ///
    /// 🔴 **Scaled once, from the source, always.** A game re-sets the text it reads off its own
    /// component; that text already carries our 42 where it wrote 60, and scaled again it would be
    /// 29, then 20 — the word shrinking at every refresh. Our own text coming back (as written, or as
    /// shown after the right-to-left presentation) is recognised and re-derived from the source it
    /// was made from; any other text is a new source. A new factor re-derives from the source too.
    ///
    /// ⚠ A text with no absolute size keeps nothing here and comes back as it was — the ordinary case.
    ///
    /// PURE by contract — linked into Core.Checks. Main thread (the text setters).
    /// </summary>
    internal sealed class SizeTagWrites
    {
        private sealed class Entry
        {
            public string Source, Written, Shown;
        }

        private readonly Dictionary<int, Entry> _byId = new Dictionary<int, Entry>();

        /// <summary>The text to give this component for <paramref name="incoming"/> at <paramref name="factor"/>.</summary>
        public string Fit(int id, string incoming, float factor)
        {
            if (incoming == null) return null;
            string source = incoming;
            if (_byId.TryGetValue(id, out var was)
                && (string.Equals(incoming, was.Written, StringComparison.Ordinal)
                    || string.Equals(incoming, was.Shown, StringComparison.Ordinal)))
                source = was.Source;

            string fitted = SizeTags.Scale(source, factor);
            if (string.Equals(fitted, source, StringComparison.Ordinal))
            {
                _byId.Remove(id);
                return source;
            }
            _byId[id] = new Entry { Source = source, Written = fitted, Shown = fitted };
            return fitted;
        }

        /// <summary>
        /// What the component finally shows, when a later step (the right-to-left presentation)
        /// rewrote the fitted text: that form coming back is ours too.
        /// </summary>
        public void NoteShown(int id, string shown)
        {
            if (shown != null && _byId.TryGetValue(id, out var entry)) entry.Shown = shown;
        }

        /// <summary>The component is gone.</summary>
        public void Forget(int id) => _byId.Remove(id);
    }
}
