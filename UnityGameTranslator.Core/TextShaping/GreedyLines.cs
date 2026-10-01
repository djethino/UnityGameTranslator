using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Cuts a LOGICAL text into lines at its spaces, greedily, against a width and a ruler the
    /// caller brings — the engine's own (TMP's GetPreferredValues for the reflow of right-to-left
    /// text, RtlPresenter). The lines come back in logical order, each without the space it was
    /// cut at, ready to be joined with '\n' and composed as one string.
    ///
    /// ⚠ Never cut inside a rich-text tag (a space in &lt;link="a b"&gt;): the same tag rule as the
    /// composer (<see cref="RtlComposer.TagEnd"/>). An existing '\n' is kept as a cut of its own.
    /// A word wider than the width stays whole on its line: the engine's own wrapping, kept on,
    /// folds it as it would have.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class GreedyLines
    {
        /// <param name="measure">The width a candidate line takes; null when it cannot be told —
        /// the whole cut is then null, and the caller keeps what the engine did.</param>
        internal static List<string> Cut(string logical, float width, Func<string, float?> measure)
        {
            if (logical == null || measure == null || width <= 0f) return null;
            var lines = new List<string>();
            foreach (var paragraph in logical.Split('\n'))
            {
                var spaces = Spaces(paragraph);
                int start = 0, lastFit = -1, s = 0;
                while (s <= spaces.Count)
                {
                    int end = s < spaces.Count ? spaces[s] : paragraph.Length;
                    float? w = measure(paragraph.Substring(start, end - start));
                    if (w == null) return null;
                    if (w.Value <= width || end == paragraph.Length && lastFit < 0)
                    {
                        if (end == paragraph.Length) { lines.Add(paragraph.Substring(start)); break; }
                        lastFit = end;
                        s++;
                        continue;
                    }
                    if (lastFit >= 0)
                    {
                        // Cut at the last space that fitted; this space is tried again on the next line.
                        lines.Add(paragraph.Substring(start, lastFit - start));
                        start = lastFit + 1;
                        lastFit = -1;
                        continue;
                    }
                    // One word wider than the width: alone on its line.
                    lines.Add(paragraph.Substring(start, end - start));
                    start = end + 1;
                    s++;
                }
            }
            return lines;
        }

        /// <summary>The cut points of a paragraph: its spaces, outside tags.</summary>
        private static List<int> Spaces(string paragraph)
        {
            var spaces = new List<int>();
            for (int i = 0; i < paragraph.Length; i++)
            {
                char c = paragraph[i];
                if (c == '<' && i + 1 < paragraph.Length && paragraph[i + 1] != ' ' && paragraph[i + 1] != '<')
                {
                    int end = RtlComposer.TagEnd(paragraph, i, stopAtLineBreak: false);
                    if (end > 0) { i = end; continue; }
                }
                if (c == ' ') spaces.Add(i);
            }
            return spaces;
        }
    }
}
