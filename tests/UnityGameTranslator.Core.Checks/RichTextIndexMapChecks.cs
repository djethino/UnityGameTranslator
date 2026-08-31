using System;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The bridge between the legacy TextGenerator's line indices (which count the TAG-STRIPPED
    /// text) and the raw rich-text string stage D must slice. The expected values below are
    /// counted by hand from Unity's documented closed tag set — b, i, size, color, material,
    /// quad — never by running the map and reading its answer back.
    /// </summary>
    internal static class RichTextIndexMapChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            int len;

            check(RichTextIndexMap.Build("مرحبا بكم", out len) == null && len == 9,
                "no tags → identity (null map)",
                "the common case must cost nothing: no allocation, raw indices used directly");

            check(RichTextIndexMap.Build("a < b, x<y", out len) == null && len == 10,
                "a bare '<' is not a tag",
                "comparison text must never be mistaken for markup");

            check(RichTextIndexMap.Build("<foo>x</foo>", out len) == null && len == 12,
                "unknown tags render literally",
                "the legacy parser strips ONLY its closed set — <foo> is ordinary text");

            var map = RichTextIndexMap.Build("<b>x</b>", out len);
            check(map != null && len == 1 && map[0] == 3 && map[1] == 8,
                "<b>x</b> strips to one char, mapped to the raw 'x'",
                "stripped index 0 → raw 3; the end sentinel → raw length");

            map = RichTextIndexMap.Build("<size=20>ab</size>", out len);
            check(map != null && len == 2 && map[0] == 9 && map[1] == 10 && map[2] == 18,
                "value tags (<size=20>) strip like bare ones",
                "the '=' is what separates a tag from a literal '<size>'");

            RichTextIndexMap.Build("<size>x</size>", out len);
            check(len == 7,
                "<size> without '=' is literal, </size> still strips",
                "asymmetric on purpose: the parser recognizes each tag on its own, no pairing");

            map = RichTextIndexMap.Build("<B>x</B>", out len);
            check(map != null && len == 1,
                "tag matching is case-insensitive",
                "a wrong guess here is caught by the caller's characterCount cross-check anyway");

            map = RichTextIndexMap.Build("<quad material=1 size=20>", out len);
            check(map != null && len == 1 && map[0] == 0 && map[1] == 25,
                "<quad …> is one rendered glyph anchored at its '<'",
                "a line starting on the quad glyph must start on its whole tag");

            RichTextIndexMap.Build("<size=1<b>x", out len);
            check(len == 8,
                "a '<' inside a tag aborts it — the parser restarts there",
                "'<size=1' renders literally (7 chars), then <b> strips, then 'x'");

            // The real job: slicing Arabic at the generator's stripped indices. Raw layout:
            // 15 tag chars, 5 Arabic chars, 8 closing-tag chars, then " ABC".
            string raw = "<color=#ff0000>مرحبا</color> ABC";
            map = RichTextIndexMap.Build(raw, out len);
            check(map != null && len == 9,
                "mixed Arabic + tags: stripped length counts glyphs only",
                "5 Arabic + space + ABC = 9 — what the generator will report (±terminator)");
            check(map != null && map[0] == 15 && map[5] == 28 && map[9] == raw.Length,
                "every stripped index lands on its raw glyph",
                "م at raw 15, the space at raw 28, the end sentinel at raw length");
            check(map != null && raw.Substring(map[0], map[6] - map[0]) == "مرحبا</color> ",
                "a slice between stripped indices carries the tags it crossed",
                "the closing tag rides inside the first line's slice — Compose re-anchors it");
        }
    }
}
