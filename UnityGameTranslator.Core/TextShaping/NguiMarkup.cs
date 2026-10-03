namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// NGUI's own markup — the bracket codes a UILabel reads when its <c>supportEncoding</c> is on
    /// (NGUIText.ParseSymbol): <c>[b] [i] [u] [s] [c] [t]</c> and their closings, <c>[sub] [sup]</c>
    /// (also <c>=scale</c>) closed by <c>[/sub] [/sup]</c>, <c>[y=scale]</c> by <c>[/y]</c>,
    /// <c>[url=…]</c> by <c>[/url]</c>, a colour <c>[RRGGBB]</c> or <c>[RRGGBBAA]</c> closed by
    /// <c>[-]</c>, and an alpha <c>[AA]</c>. Read as NGUI reads it, without its state: a code is
    /// recognised by its shape only.
    ///
    /// Why it matters: a right-to-left line is put in visual order by the mod, and a code read as
    /// text travelled with the words — brackets mirrored, closings landed before their openings, a
    /// strike drawn across the whole line (bench, real NGUI, 2026-10-03).
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class NguiMarkup
    {
        /// <summary>The length of the NGUI code starting at <paramref name="at"/>, or 0 when there is none.</summary>
        internal static int LengthAt(string text, int at)
        {
            int n = text.Length - at;
            if (n < 3 || text[at] != '[') return 0;
            char c0 = text[at + 1], c1 = text[at + 2];
            if (c1 == ']')
            {
                if (c0 == '-' || IsOneOf(c0, "biusct")) return 3;
            }
            else if (c1 == '=' && (c0 == 'y' || c0 == 'Y'))
            {
                int close = text.IndexOf(']', at + 3);
                if (close > at + 3 && IsNumber(text, at + 3, close)) return close + 1 - at;
            }
            if (n < 4) return 0;
            char c2 = text[at + 3];
            if (c2 == ']')
            {
                if (c0 == '/' && IsOneOf(c1, "biuscty")) return 4;
                if (IsHex(c0) && IsHex(c1)) return 4;   // alpha
            }
            if (n < 5) return 0;
            char c3 = text[at + 4];
            if (Is(c0, 's') && Is(c1, 'u') && (Is(c2, 'b') || Is(c2, 'p')))
            {
                if (c3 == ']') return 5;
                if (c3 == '=')
                {
                    int close = text.IndexOf(']', at + 4);
                    if (close > at + 5 && IsNumber(text, at + 5, close)) return close + 1 - at;
                }
            }
            if (n >= 6 && text[at + 5] == ']' && c0 == '/'
                && ((Is(c1, 's') && Is(c2, 'u') && (Is(c3, 'b') || Is(c3, 'p'))) || (Is(c1, 'u') && Is(c2, 'r') && Is(c3, 'l'))))
                return 6;
            if (c3 == '=' && ((c0 == 'u' && c1 == 'r' && c2 == 'l') || (c0 == 'U' && c1 == 'R' && c2 == 'L')))
            {
                int close = text.IndexOf(']', at + 4);
                return close < 0 ? 0 : close + 1 - at;   // NGUI eats an unclosed one to the end: left as text here
            }
            if (n >= 8 && text[at + 7] == ']' && AllHex(text, at + 1, 6)) return 8;
            if (n >= 10 && text[at + 9] == ']' && AllHex(text, at + 1, 8)) return 10;
            return 0;
        }

        /// <summary>
        /// What pairs an opening code with its closing: "b" for [b] and [/b], "color" for a colour
        /// and [-], "url" for [url=…] and [/url]; null for a code that closes nothing and is closed by
        /// nothing (an alpha).
        /// </summary>
        internal static string PairName(string code)
        {
            if (code.Length == 3 && code[1] == '-') return "color";
            if (code.Length == 10 || (code.Length == 8 && AllHex(code, 1, 6))) return "color";
            if (code.Length == 4 && code[1] != '/' && IsHex(code[1]) && IsHex(code[2])) return null;
            int from = code[1] == '/' ? 2 : 1;
            int end = from;
            while (end < code.Length && code[end] != '=' && code[end] != ']') end++;
            return code.Substring(from, end - from).ToLowerInvariant();
        }

        /// <summary>Whether this code closes a span: [/x] or a colour's [-].</summary>
        internal static bool IsClosing(string code) => code.Length >= 3 && (code[1] == '/' || (code.Length == 3 && code[1] == '-'));

        private static bool Is(char c, char lower) => c == lower || c == char.ToUpperInvariant(lower);
        private static bool IsOneOf(char c, string lowers) => lowers.IndexOf(char.ToLowerInvariant(c)) >= 0;
        private static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static bool AllHex(string text, int from, int count)
        {
            for (int i = from; i < from + count; i++) if (!IsHex(text[i])) return false;
            return true;
        }

        private static bool IsNumber(string text, int from, int to)
        {
            bool digit = false;
            for (int i = from; i < to; i++)
            {
                char c = text[i];
                if (c >= '0' && c <= '9') digit = true;
                else if (c != '.' && c != '-') return false;
            }
            return digit;
        }
    }
}
