using System;
using System.Linq;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A long text cut into pieces a label can draw: none over the mesh's limit, cut at a line
    /// break wherever one falls, and nothing lost in the cutting.
    /// </summary>
    internal static class TextChunksChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            check(TextChunks.LabelCharacters == 16250,
                  "one label draws 16 250 characters at most", "65 000 vertices in a canvas mesh, four per character — the figure is derived, not chosen");

            check(TextChunks.Split("short").SequenceEqual(new[] { "short" }),
                  "a short text is one piece", "nothing to cut");
            check(TextChunks.Split("").SequenceEqual(new[] { "" }) && TextChunks.Split(null).SequenceEqual(new[] { "" }),
                  "an empty text is one empty piece", "a caller always has a row to write into");

            string lines = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}"));   // 69 characters, 9 breaks
            var atBreaks = TextChunks.Split(lines, 20);
            check(atBreaks.All(p => p.Length <= 20), "no piece is over the limit", $"got {string.Join("|", atBreaks.Select(p => p.Length))}");
            check(atBreaks.All(p => p.EndsWith("\n", StringComparison.Ordinal) || ReferenceEquals(p, atBreaks[atBreaks.Count - 1])),
                  "every piece but the last ends on a line break", "a line is never split across two labels when a break is within reach");
            check(string.Concat(atBreaks) == lines, "the pieces put back together are the text", "nothing is lost or doubled in the cutting");

            string wall = new string('x', 45);
            var hard = TextChunks.Split(wall, 20);
            check(hard.Count == 3 && hard[0].Length == 20 && hard[2].Length == 5 && string.Concat(hard) == wall,
                  "a line with no break is cut at the limit", "a wall of text still has to be drawn");

            string big = string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"Fixed a bug where {i} happened."));
            var pieces = TextChunks.Split(big);
            check(pieces.Count >= 2 && pieces.All(p => p.Length <= TextChunks.LabelCharacters) && string.Concat(pieces) == big,
                  "a proposal of tens of thousands of characters becomes several labels, whole", "the case that showed a blank trough under a scrollbar");
        }
    }
}
