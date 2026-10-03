using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// An underline or a strikethrough over the letters it belongs to, wherever they ended up.
    ///
    /// TMP draws each run of &lt;u&gt; or &lt;s&gt; as one 12-vertex strip (three quads: a cap, a
    /// stretched middle, a cap) from the LEFT edge of the run's first letter to the RIGHT edge of its
    /// last — in reading order. With its right-to-left setting the first letter is the rightmost, so
    /// the strip runs backwards and covers only the letters between the two ends: measured without
    /// the mod on TMP 1.4 and 3.0 (Unity 2018.4 and 2021.3, Mono and IL2CPP, bench tmpdeco,
    /// 2026-10-03), and the same code is in Unity 6's TMP. Letters moved after TMP's layout (an input
    /// field put in visual order, RtlInputFields) leave the strip where they were, too.
    ///
    /// Pure: the caller reads TMP's geometry and writes back what this decides. A strip is
    /// recognised by its own shape (the equalities TMP's DrawUnderlineMesh writes) and by its ends,
    /// which are EXACT copies of its letters' edges; one TMP drew right is never touched.
    /// </summary>
    internal static class DecorationSpans
    {
        /// <summary>A letter as TMP laid it out: its edges, as TMP wrote them, and how far it was moved since.</summary>
        internal struct Letter
        {
            public bool Visible, Decorated;
            public int Line;
            public float Left, Right, Shift, Scale;
        }

        /// <summary>
        /// Whether the 12 vertices from <paramref name="at"/> are a decoration strip: the middle
        /// quad starts where the first cap ends and the last cap starts where the middle ends
        /// (v4 = v3, v5 = v2, v8 = v7, v9 = v6), each quad flat-bottomed and flat-topped.
        /// </summary>
        internal static bool IsStrip(IList<float> xs, IList<float> ys, int at)
        {
            if (at < 0 || at + 12 > xs.Count || at + 12 > ys.Count) return false;
            bool Same(int a, int b) => xs[at + a] == xs[at + b] && ys[at + a] == ys[at + b];
            // A height of its own: the unused, zeroed slots past the text match every equality.
            return ys[at + 0] != ys[at + 1]
                   && Same(4, 3) && Same(5, 2) && Same(8, 7) && Same(9, 6)
                   && ys[at + 0] == ys[at + 3] && ys[at + 1] == ys[at + 2]
                   && ys[at + 10] == ys[at + 9] && ys[at + 11] == ys[at + 8]
                   && xs[at + 0] == xs[at + 1] && xs[at + 10] == xs[at + 11];
        }

        /// <summary>
        /// The new x of the 12 vertices of the strip at <paramref name="at"/>, or null when it is
        /// right as it is or not one of these letters'. <paramref name="capHalf"/>: half the width of
        /// the glyph TMP draws the strip with, unscaled — the caps are that times the run's largest
        /// letter scale, never more than half the run (TMP's own DrawUnderlineMesh).
        /// </summary>
        internal static float[] Respan(IList<float> xs, int at, IList<Letter> letters, float capHalf)
        {
            float from = xs[at], to = xs[at + 10];
            int first = -1, last = -1;
            for (int k = 0; k < letters.Count && first < 0; k++)
                if (letters[k].Decorated && letters[k].Left == from) first = k;
            if (first < 0) return null;
            for (int k = first; k < letters.Count; k++)
                if (letters[k].Decorated && letters[k].Line == letters[first].Line && letters[k].Right == to) { last = k; break; }
            if (last < 0) return null;

            float start = float.MaxValue, end = float.MinValue, scale = 0f;
            for (int k = first; k <= last; k++)
            {
                var l = letters[k];
                if (!l.Visible || !l.Decorated || l.Line != letters[first].Line) continue;
                start = Math.Min(start, l.Left + l.Shift);
                end = Math.Max(end, l.Right + l.Shift);
                scale = Math.Max(scale, l.Scale);
            }
            // Right when it already spans its letters, whichever way it was drawn (two right-to-left
            // letters gave a strip of no width at all: not "forwards", and wrong).
            if (start == float.MaxValue || (start == from && end == to)) return null;

            float cap = Math.Min(capHalf * scale, (end - start) / 2f);
            if (cap < 0f) cap = 0f;
            var x = new float[12];
            x[0] = x[1] = start;
            x[2] = x[3] = x[4] = x[5] = start + cap;
            x[6] = x[7] = x[8] = x[9] = end - cap;
            x[10] = x[11] = end;
            return x;
        }
    }
}
