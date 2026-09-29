using System;
using System.Linq;
using UnityGameTranslator.Core.Engine;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The tremble detector: a window going back and forth between two layouts is reported once,
    /// and nothing that merely moves — opening, scrolling, dragging — is ever reported.
    /// </summary>
    internal static class OscillationChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            int Reports(float[] frames, Oscillation watch = null)
            {
                watch = watch ?? new Oscillation("y");
                int count = 0;
                foreach (var v in frames) count += watch.Feed(new[] { v }).Count;
                return count;
            }

            float[] Alternating(int n, float a, float b) =>
                Enumerable.Range(0, n).Select(i => i % 2 == 0 ? a : b).ToArray();

            var tremble = new Oscillation("y");
            Oscillation.Tremble? seen = null;
            foreach (var v in Alternating(12, 100f, 128f))
            {
                var found = tremble.Feed(new[] { v });
                if (found.Count > 0) seen = found[0];
            }
            check(Reports(Alternating(12, 100f, 128f)) == 1 && seen is { Channel: "y" } t
                  && Math.Min(t.A, t.B) == 100f && Math.Max(t.A, t.B) == 128f,
                "a value going A, B, A, B every frame is reported once, with both values",
                "the window seen « comme dédoublé »: the line has to say what fights what");

            check(Reports(Enumerable.Range(0, 60).Select(i => i * 3f).ToArray()) == 0,
                "a movement one way is not a tremble", "a scroll, a window growing at opening, a drag");

            check(Reports(new[] { 0f, 40f, 0f, 40f, 0f }) == 0,
                "a few returns are not a tremble", "somebody scrolling back and forth is not a defect");

            check(Reports(Enumerable.Repeat(50f, 30).ToArray()) == 0,
                "a window standing still says nothing", "every frame of every open window goes through here");

            check(Reports(Alternating(12, 100f, 100.3f)) == 0,
                "under half a pixel, two layouts are the same layout", "rounding is not a tremble");

            var again = new Oscillation("y");
            var episodes = Alternating(12, 0f, 10f).Concat(Enumerable.Repeat(10f, 3)).Concat(Alternating(12, 0f, 10f)).ToArray();
            check(Reports(episodes, again) == 2,
                "settling ends an episode, and the next one is reported again",
                "re-armed by the value standing still — an event, never a delay");

            var two = new Oscillation("steady", "shaking");
            int steadyReports = 0, shakingReports = 0;
            for (int i = 0; i < 12; i++)
                foreach (var f in two.Feed(new[] { 5f, i % 2 == 0 ? 0f : 28f }))
                    if (f.Channel == "steady") steadyReports++; else shakingReports++;
            check(steadyReports == 0 && shakingReports == 1,
                "only the value that trembles is named", "the line points at the culprit, not at the window");
        }
    }
}
