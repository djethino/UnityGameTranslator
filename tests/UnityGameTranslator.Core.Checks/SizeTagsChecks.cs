using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A text's own absolute sizes following the factor the mod scales its font by
    /// (<see cref="SizeTags"/>), and the memory that keeps a text read back by the game from being
    /// scaled twice (<see cref="SizeTagWrites"/>) — the second is a sequence, so it is replayed as one.
    /// </summary>
    internal static class SizeTagsChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            Scaling(check);
            Sequences(check);
        }

        private static void Scaling(Action<bool, string, string> check)
        {
            const string word = "SEARCHING THE ESTATE CAN BE <size=60>EXHAUSTING";
            check(SizeTags.Scale(word, 0.7f) == "SEARCHING THE ESTATE CAN BE <size=42>EXHAUSTING",
                "an absolute size follows the factor", "the headline word shrinks with the line it sits in");

            check(ReferenceEquals(SizeTags.Scale(word, 1f), word),
                "a factor of 1 gives back the very same text", "a game text in its own size is never rewritten");

            const string relative = "<size=150%>BIG</size> <size=1.5em>ALSO</size>";
            check(ReferenceEquals(SizeTags.Scale(relative, 0.5f), relative),
                "a relative size is left alone", "a percentage or an em already follows the component");

            check(SizeTags.Scale("<size=+10>up</size><size=-4>down</size>", 0.5f) == "<size=+5>up</size><size=-2>down</size>",
                "a step from the component's size is scaled as a step", "+10 at half size is +5, not 5");

            check(SizeTags.Scale("<SIZE=\"40px\">a</size>", 1.5f) == "<size=\"60px\">a</size>",
                "the unit and the quotes are kept", "the tag stays what the game wrote, only its number moves");

            check(SizeTags.Scale("<size=33>a", 0.7f) == "<size=23.1>a",
                "a fraction is written with a dot, two decimals at most", "invariant: a comma would be a broken tag");

            check(!SizeTags.HasAbsolute("no size here") && !SizeTags.HasAbsolute("<size=٦٠>a"),
                "digits of another script are not read as a size", "markup is written in ASCII digits; parsing the others would throw");
        }

        private static void Sequences(Action<bool, string, string> check)
        {
            var writes = new SizeTagWrites();
            const string source = "CAN BE <size=60>EXHAUSTING";

            string first = writes.Fit(1, source, 0.7f);
            check(first == "CAN BE <size=42>EXHAUSTING", "the first write is scaled", "60 at 70% is 42");

            string echoed = writes.Fit(1, first, 0.7f);
            check(echoed == first,
                "🔴 our own text coming back is not scaled again", "re-scaled it would be 29, then 20 — shrinking at every refresh");

            string resized = writes.Fit(1, first, 0.5f);
            check(resized == "CAN BE <size=30>EXHAUSTING",
                "a new factor re-derives from the source", "30 is 60 at 50%, not 42 at 50%");

            string restored = writes.Fit(1, resized, 1f);
            check(restored == source, "back to 100%, the game's own sizes return", "nothing of ours stays when the size is the game's");

            check(writes.Fit(1, "OTHER <size=20>TEXT", 0.5f) == "OTHER <size=10>TEXT",
                "a text the game wrote itself is a new source", "only what we wrote is recognised as ours");

            // The shown form (after right-to-left presentation) coming back is ours too.
            var shownWrites = new SizeTagWrites();
            string fitted = shownWrites.Fit(2, "<size=40>A", 0.5f);
            shownWrites.NoteShown(2, "SHOWN<size=20>");
            check(shownWrites.Fit(2, "SHOWN<size=20>", 0.5f) == fitted,
                "the presented form coming back is re-derived from its source", "the presentation's own output must not be scaled again");

            var plain = new SizeTagWrites();
            const string none = "nothing to scale";
            check(ReferenceEquals(plain.Fit(3, none, 0.5f), none),
                "a text with no absolute size comes back as it was", "the ordinary case keeps nothing and rewrites nothing");
        }
    }
}
