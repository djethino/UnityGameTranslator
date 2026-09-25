using System;

namespace UnityGameTranslator.Core.Engine
{
    /// <summary>
    /// A game's typewriter reveal, counted on the ORIGINAL text, carried over to the translation.
    ///
    /// 🔴 A game that reveals a line character by character sets TMP's <c>maxVisibleCharacters</c>
    /// from a count it took itself — and it takes it from the text it reads back, which the mod
    /// hands it in the original (the getter returns the source, so the game's own logic keeps
    /// working). So the reveal stops at the source's length: "Hello there! What can I do for
    /// you?" (35 characters) showed "Bonjour ! Que puis-je faire pour vo", the first 35 characters
    /// of a longer translation.
    ///
    /// The count is scaled to the translation: the same share of it, so the animation keeps its
    /// pace and the end of the original is the end of the translation. No timer and no rule about a
    /// language — it is a ratio of two lengths.
    ///
    /// ⚠ Only a count that stays within the original can be one taken on the original. A game that
    /// counts on what is on screen goes past it, and from then on its count is left alone
    /// (<see cref="Verdict.CountsOnShown"/>): scaling it would push the reveal ahead of itself.
    /// ⚠ Pure: no Unity, no state — held by RevealScaleChecks.
    /// </summary>
    internal static class RevealScale
    {
        internal enum Verdict
        {
            /// <summary>Nothing to do: the translation is not longer, or the count is "all" or none.</summary>
            Leave,
            /// <summary>Converted to the translation's length.</summary>
            Scaled,
            /// <summary>The game counts on the text shown — leave this component alone from now on.</summary>
            CountsOnShown,
        }

        /// <summary>
        /// The count to give TMP for <paramref name="value"/>, a count the game took on a text of
        /// <paramref name="sourceLength"/> visible characters while <paramref name="shownLength"/>
        /// are shown.
        /// </summary>
        internal static Verdict Convert(int value, int sourceLength, int shownLength, out int scaled)
        {
            scaled = value;
            // 0 hides everything whatever the text; a huge value (TMP's default, a game's "show
            // all") already shows it all. Neither is a count on anything.
            if (value <= 0 || value >= int.MaxValue / 2) return Verdict.Leave;
            if (sourceLength <= 0 || shownLength <= sourceLength) return Verdict.Leave;
            if (value > sourceLength) return Verdict.CountsOnShown;

            // Rounded up, so the last character of the original reveals the last of the
            // translation exactly, and the count never goes backwards as the game's rises.
            scaled = (int)Math.Ceiling(value * (double)shownLength / sourceLength);
            return Verdict.Scaled;
        }
    }
}
