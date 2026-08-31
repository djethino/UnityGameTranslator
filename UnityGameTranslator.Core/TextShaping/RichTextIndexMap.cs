using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// The index map that lets stage D slice a RICH-TEXT string at the line breaks Unity's
    /// TextGenerator reports. The generator's <c>UILineInfo.startCharIdx</c> counts characters of
    /// the TAG-STRIPPED text; our slices must cut the RAW string — this map is the bridge.
    ///
    /// Why it can be exact: the legacy rich-text tag set is CLOSED — <c>b</c>, <c>i</c>,
    /// <c>size</c>, <c>color</c>, <c>material</c> (paired) and <c>quad</c> (standalone, renders
    /// exactly ONE glyph). Anything else between angle brackets is rendered literally and counts
    /// as ordinary characters. TMP has an open, evolving set (no map possible); this file is for
    /// the legacy generator only (UI.Text).
    ///
    /// ⚠ The map is a CLAIM about what the native parser strips, and the caller must hold the
    /// proof: compare the generator's <c>characterCount</c> against <see cref="StrippedLength"/>
    /// (±1 for the trailing terminator glyph) before trusting any index — a mismatch means the
    /// running Unity parses differently, and the caller falls back to the whole-string form
    /// exactly as before this map existed.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class RichTextIndexMap
    {
        // The paired tags. Opening forms: <b>, <i>, <size=…>, <color=…>, <material=…>.
        private static readonly string[] BareTags = { "b", "i" };
        private static readonly string[] ValueTags = { "size", "color", "material" };

        /// <summary>
        /// Map from TAG-STRIPPED character index to RAW string index. Length is
        /// strippedLength + 1: the final entry maps "end of stripped text" to raw.Length, so a
        /// slice is always <c>raw[map[s] .. map[e]]</c> with no special case for the last line.
        /// A stripped index produced by a &lt;quad&gt; maps to the raw index of its '&lt;' — a
        /// line starting on the quad glyph starts on its whole tag.
        /// Returns null when the text contains no recognized tag (identity — use the raw indices
        /// directly, and skip the characterCount cross-check against strippedLength).
        /// </summary>
        internal static int[] Build(string raw, out int strippedLength)
        {
            List<int> map = null;   // allocated on the first recognized tag only
            int i = 0;
            int stripped = 0;
            while (i < raw.Length)
            {
                if (raw[i] == '<' && TryParseTag(raw, i, out int end, out bool isQuad))
                {
                    if (map == null)
                    {
                        map = new List<int>(raw.Length);
                        for (int k = 0; k < stripped; k++) map.Add(k);   // identity so far
                    }
                    if (isQuad) { map.Add(i); stripped++; }   // one rendered glyph, no raw char
                    i = end + 1;
                    continue;
                }
                if (map != null) map.Add(i);
                stripped++;
                // A surrogate pair is TWO chars to the generator too (it counts UTF-16 units),
                // so no pairing logic here: every char unit maps individually.
                i++;
            }
            strippedLength = stripped;
            if (map == null) return null;
            map.Add(raw.Length);
            return map.ToArray();
        }

        /// <summary>
        /// True when raw[at] opens a tag the legacy parser strips; end = index of its '&gt;'.
        /// Matching is case-insensitive — the native parser accepts either case, and a wrong
        /// guess here is caught by the caller's characterCount cross-check, never rendered.
        /// </summary>
        private static bool TryParseTag(string raw, int at, out int end, out bool isQuad)
        {
            end = -1; isQuad = false;
            int i = at + 1;
            if (i >= raw.Length) return false;
            bool closing = raw[i] == '/';
            if (closing) i++;

            int nameStart = i;
            while (i < raw.Length && char.IsLetter(raw[i])) i++;
            int nameLen = i - nameStart;
            if (nameLen == 0 || i >= raw.Length) return false;

            if (!closing && nameLen == 4 && string.Compare(raw, nameStart, "quad", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                // <quad …> — standalone, one glyph. Attributes (or nothing) up to '>'.
                if (raw[i] != ' ' && raw[i] != '=' && raw[i] != '>' && raw[i] != '/') return false;
                int close = FindTagEnd(raw, i);
                if (close < 0) return false;
                end = close; isQuad = true;
                return true;
            }

            foreach (string tag in BareTags)
            {
                if (nameLen != tag.Length || string.Compare(raw, nameStart, tag, 0, nameLen, StringComparison.OrdinalIgnoreCase) != 0)
                    continue;
                // <b> / </b>: nothing between name and '>'.
                if (raw[i] != '>') return false;
                end = i;
                return true;
            }
            foreach (string tag in ValueTags)
            {
                if (nameLen != tag.Length || string.Compare(raw, nameStart, tag, 0, nameLen, StringComparison.OrdinalIgnoreCase) != 0)
                    continue;
                if (closing)
                {
                    // </size>: nothing between name and '>'.
                    if (raw[i] != '>') return false;
                    end = i;
                    return true;
                }
                // <size=…>: the '=' is what separates the tag from a literal '<size>' rendered as text.
                if (raw[i] != '=') return false;
                int close = FindTagEnd(raw, i + 1);
                if (close < 0) return false;
                end = close;
                return true;
            }
            return false;
        }

        /// <summary>Index of the closing '&gt;', or -1 — a '&lt;' aborts (the parser restarts there).</summary>
        private static int FindTagEnd(string raw, int from)
        {
            // Same bound the composer's tokenizer uses: a runaway '<' must not swallow the text.
            int limit = Math.Min(raw.Length, from + 128);
            for (int i = from; i < limit; i++)
            {
                if (raw[i] == '>') return i;
                if (raw[i] == '<') return -1;
            }
            return -1;
        }
    }
}
