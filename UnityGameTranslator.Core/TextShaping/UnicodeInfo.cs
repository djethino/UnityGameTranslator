using System;
using System.Globalization;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// What a character IS — its general category, whether it is a letter, a digit, a mark, a
    /// space — from the Unicode tables generated into the mod (ShapingTables.g.cs), one version for
    /// all of them.
    ///
    /// 🔴 **Never the game's runtime.** <c>CharUnicodeInfo</c> and <c>char.IsLetter</c> answer from
    /// the tables of the .NET a game ships: Unicode 6 or 8 in an old Unity Mono, a corlib trimmed
    /// to half its size in another — a letter of a script added since is "not assigned" there, a
    /// combining mark a symbol. The same text would be read differently from one game to the next.
    /// Asked here, every game reads it the same, at the Unicode the mod is generated from
    /// (<see cref="ShapingTables.UnicodeVersion"/>). <c>CharacterRangeChecks</c> refuses the
    /// runtime's answers anywhere else.
    ///
    /// PURE by contract — linked into Core.Checks.
    /// </summary>
    internal static class UnicodeInfo
    {
        /// <summary>The general category of a code point (OtherNotAssigned when Unicode assigns none).</summary>
        internal static UnicodeCategory CategoryOf(int cp)
        {
            var runs = ShapingTables.GeneralCategories;
            int lo = 0, hi = runs.Length / 3 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (cp < runs[mid * 3]) hi = mid - 1;
                else if (cp > runs[mid * 3 + 1]) lo = mid + 1;
                else return (UnicodeCategory)runs[mid * 3 + 2];
            }
            return UnicodeCategory.OtherNotAssigned;
        }

        /// <summary>The general category of the code point at <paramref name="i"/>, a surrogate pair read as one.</summary>
        internal static UnicodeCategory CategoryOf(string s, int i) => CategoryOf(CodePointAt(s, i));

        /// <summary>A combining mark: nonspacing, spacing or enclosing (Mn, Mc, Me).</summary>
        internal static bool IsMark(int cp)
        {
            var c = CategoryOf(cp);
            return c == UnicodeCategory.NonSpacingMark || c == UnicodeCategory.SpacingCombiningMark || c == UnicodeCategory.EnclosingMark;
        }

        /// <summary>A letter of any kind (Lu, Ll, Lt, Lm, Lo) — what <c>char.IsLetter</c> means.</summary>
        internal static bool IsLetter(int cp) => CategoryOf(cp) <= UnicodeCategory.OtherLetter;

        /// <summary>A decimal digit of any script (Nd) — what <c>char.IsDigit</c> means.</summary>
        internal static bool IsDigit(int cp) => CategoryOf(cp) == UnicodeCategory.DecimalDigitNumber;

        /// <summary>A letter or a decimal digit.</summary>
        internal static bool IsLetterOrDigit(int cp) => IsLetter(cp) || IsDigit(cp);

        /// <summary>A control character (Cc).</summary>
        internal static bool IsControl(int cp) => CategoryOf(cp) == UnicodeCategory.Control;

        /// <summary>Unicode's White_Space property — tab and line feed included, though they are controls.</summary>
        internal static bool IsWhiteSpace(int cp)
        {
            var ranges = ShapingTables.WhiteSpace;
            for (int i = 0; i < ranges.Length; i += 2)
            {
                if (cp < ranges[i]) return false;
                if (cp <= ranges[i + 1]) return true;
            }
            return false;
        }

        /// <summary>
        /// The character drawn in place of this one at a right-to-left level (rule L4) — Unicode's
        /// Bidi_Mirroring_Glyph: brackets, quotation marks, mathematical symbols; the code point
        /// itself when it has none.
        /// </summary>
        internal static int MirrorOf(int cp)
        {
            var pairs = ShapingTables.BidiMirrors;
            int lo = 0, hi = pairs.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int key = pairs[mid * 2];
                if (cp < key) hi = mid - 1;
                else if (cp > key) lo = mid + 1;
                else return pairs[mid * 2 + 1];
            }
            return cp;
        }

        private static int CodePointAt(string s, int i)
        {
            char c = s[i];
            return char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(c, s[i + 1]) : c;
        }
    }
}
