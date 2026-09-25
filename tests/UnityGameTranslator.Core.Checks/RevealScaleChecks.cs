using System;
using UnityGameTranslator.Core.Engine;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A typewriter reveal counted on the original, carried over to the translation. The numbers
    /// are the ones seen in a game: "Hello there! What can I do for you?" (35 characters) cut the
    /// French at its own 35th character.
    /// </summary>
    internal static class RevealScaleChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            const int source = 35, shown = 51;

            var v = RevealScale.Convert(source, source, shown, out int end);
            check(v == RevealScale.Verdict.Scaled && end == shown,
                "the end of the original reveals the whole translation",
                "the defect itself: the reveal stopped at the source's length, mid-word");

            int previous = 0;
            bool rising = true;
            for (int i = 1; i <= source; i++)
            {
                RevealScale.Convert(i, source, shown, out int step);
                if (step < previous || step > shown) rising = false;
                previous = step;
            }
            check(rising, "the count only rises, and never past the translation",
                "a reveal that went backwards would flicker; one past the end is harmless but wrong");

            check(RevealScale.Convert(source + 1, source, shown, out _) == RevealScale.Verdict.CountsOnShown,
                "a count past the original means the game counts on the text shown",
                "scaling it again would reveal the translation ahead of the game's own pace");

            check(RevealScale.Convert(20, 40, 30, out int shorter) == RevealScale.Verdict.Leave && shorter == 20,
                "a shorter translation is left alone",
                "the original's count already covers all of it; scaling down would cut it");

            check(RevealScale.Convert(0, source, shown, out int none) == RevealScale.Verdict.Leave && none == 0
                  && RevealScale.Convert(int.MaxValue, source, shown, out int all) == RevealScale.Verdict.Leave && all == int.MaxValue,
                "0 and \"show everything\" are not counts",
                "a hidden text must stay hidden, and TMP's default must stay the default");
        }
    }
}
