using System;
using System.Collections.Generic;
using System.Text;
using Topten.RichTextKit;
using Topten.RichTextKit.Utils;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// A right-to-left text somebody is EDITING: what the field shows, and where every typed
    /// character landed on screen — so a caret addressing the typed text can be drawn, a click
    /// can be turned back into a position in it, and the arrow keys can follow the screen.
    ///
    /// 🔴 **The typed text is never changed.** Unity's two input fields store and edit the logical
    /// string and draw it as it is — no shaping, no reordering, on any version (none of them knows
    /// the word "RightToLeft"). Shaping what they STORE is the known way to break the caret and the
    /// clipboard (RTLTMPro lists its own input field among its unsolved issues). So the field keeps
    /// the logical string, only the text its label draws is replaced, and this class is the map
    /// between the two. See analyse/rtl-saisie-et-editeur-mod.md.
    ///
    /// ⚠ **Not <see cref="RtlComposer"/>, on purpose.** The composer serves texts somebody READS:
    /// rich-text tags are pulled out of the stream as structure, a placeholder is one unit. In a
    /// field every character is something the person typed and may put the caret next to: a tag
    /// is text here, so both tags and placeholders stay VISIBLE, as one left-to-right block each,
    /// and nothing is ever dropped from the display.
    ///
    /// UAX#9 as it is meant to be applied to wrapped text: levels resolved per PARAGRAPH (a hard
    /// line break), then each LINE reordered on its own (L2), trailing whitespace of a line put
    /// back at the paragraph level first (L1). The paragraph direction is the first strong
    /// character's (P2/P3), like a browser field with dir="auto".
    ///
    /// PURE by contract (no Unity, no state kept between calls) — linked into Core.Checks.
    /// ⚠ Main thread only: the shaper and the bidi instances are shared buffers.
    /// </summary>
    internal sealed class RtlFieldLayout
    {
        // ── The shared, stateful machinery (same lifecycle as RtlComposer's) ───────────────
        private static readonly Bidi _bidi = new Bidi();
        private static readonly BidiData _bidiData = new BidiData();
        private static readonly PresentationFormsShaper _shaper = new PresentationFormsShaper();

        // Tokens travel through shaping and bidi as one private-use codepoint of class L — the
        // composer's placeholder mechanic, applied here to tags as well (see the summary). Same
        // range as the composer's placeholders: a sentinel never reaches a screen.
        private const int SentinelBase = 0xF100;
        private const int SentinelMax = 0x400;

        /// <summary>Everything decided before any line is known — the text an engine measures.</summary>
        internal sealed class Prepared
        {
            internal string Logical;

            /// <summary>
            /// The shaped text in LOGICAL order, tokens written out — the string an engine must
            /// wrap: the glyphs it will draw, in the order the lines are cut in.
            /// </summary>
            internal string MeasureText;

            internal int[] Cps;                 // shaped codepoints (tokens as sentinels)
            internal List<string> Tokens;
            internal int[] CpOfLogical;         // logical UTF-16 → shaped codepoint
            internal int[] OffsetInCp;          // logical UTF-16 → offset inside a token (0 otherwise)
            internal int[] MeasureStartOfCp;    // shaped codepoint → UTF-16 start in MeasureText
            internal int[] CpOfMeasure;         // MeasureText UTF-16 → shaped codepoint

            /// <summary>The shaped codepoint a MeasureText index belongs to.</summary>
            internal int CpAtMeasure(int measureIndex) =>
                measureIndex <= 0 ? 0
                : measureIndex >= CpOfMeasure.Length ? Cps.Length
                : CpOfMeasure[measureIndex];

            /// <summary>
            /// Lay the text out, cutting soft-wrapped lines at the given MeasureText indices (the
            /// engine's own break points; empty or null for a field on one line).
            /// </summary>
            internal RtlFieldLayout Lay(IList<int> softWrapsInMeasure) =>
                new RtlFieldLayout(this, softWrapsInMeasure);
        }

        /// <summary>
        /// Shape the text and protect its tokens. Null when there is nothing to present (no
        /// strong right-to-left character) or when the shaper's map could not be trusted.
        /// </summary>
        internal static Prepared Prepare(string logical)
        {
            if (string.IsNullOrEmpty(logical) || !RtlText.ContainsStrongRtl(logical)) return null;

            // 1. Tokens → sentinels, remembering which logical span each sentinel stands for.
            var tokens = new List<string>();
            var sb = new StringBuilder(logical.Length);
            var sentinelizedOfLogical = new int[logical.Length];
            var offsetInToken = new int[logical.Length];
            for (int i = 0; i < logical.Length;)
            {
                int end = TokenEnd(logical, i);
                if (end > i && tokens.Count < SentinelMax)
                {
                    for (int k = i; k < end; k++) { sentinelizedOfLogical[k] = sb.Length; offsetInToken[k] = k - i; }
                    sb.Append((char)(SentinelBase + tokens.Count));
                    tokens.Add(logical.Substring(i, end - i));
                    i = end;
                    continue;
                }
                sentinelizedOfLogical[i] = sb.Length;
                sb.Append(logical[i]);
                i++;
            }
            string sentinelized = sb.ToString();

            // 2. Shape, with the map of every character to the glyph that shows it.
            string shaped = _shaper.ShapeWithMap(sentinelized, out int[] shapeMap);
            if (shapeMap == null) return null;

            // 3. Codepoints of the shaped text; shaped UTF-16 → codepoint.
            var cps = new List<int>(shaped.Length);
            var cpOfShaped = new int[shaped.Length + 1];
            for (int i = 0; i < shaped.Length; i++)
            {
                cpOfShaped[i] = cps.Count;
                int cp = char.ConvertToUtf32(shaped, i);
                if (cp > 0xFFFF) { cpOfShaped[i + 1] = cps.Count; i++; }
                cps.Add(cp);
            }
            cpOfShaped[shaped.Length] = cps.Count;

            var prep = new Prepared
            {
                Logical = logical,
                Cps = cps.ToArray(),
                Tokens = tokens,
                CpOfLogical = new int[logical.Length],
                OffsetInCp = offsetInToken,
            };
            for (int i = 0; i < logical.Length; i++)
                prep.CpOfLogical[i] = cpOfShaped[shapeMap[sentinelizedOfLogical[i]]];

            // 4. The measure text: shaped, logical order, tokens written out.
            var measure = new StringBuilder(shaped.Length + 16);
            prep.MeasureStartOfCp = new int[prep.Cps.Length + 1];
            var cpOfMeasure = new List<int>(shaped.Length + 16);
            for (int c = 0; c < prep.Cps.Length; c++)
            {
                prep.MeasureStartOfCp[c] = measure.Length;
                int before = measure.Length;
                AppendCp(measure, prep.Cps[c], tokens);
                for (int k = before; k < measure.Length; k++) cpOfMeasure.Add(c);
            }
            prep.MeasureStartOfCp[prep.Cps.Length] = measure.Length;
            prep.MeasureText = measure.ToString();
            prep.CpOfMeasure = cpOfMeasure.ToArray();
            return prep;
        }

        /// <summary>End of a token starting at <paramref name="i"/>, or <paramref name="i"/> when none — the composer's rule, never across a line break.</summary>
        private static int TokenEnd(string text, int i)
        {
            char c = text[i];
            int close;
            if (c == '[' && i + 1 < text.Length && text[i + 1] == '!' && (close = FindClose(text, i + 2, ']', 32)) > 0)
                return close + 1;
            if (c == '<' && i + 1 < text.Length && text[i + 1] != ' ' && text[i + 1] != '<'
                && (close = FindClose(text, i + 1, '>', 128)) > 0)
                return close + 1;
            return i;
        }

        private static int FindClose(string text, int from, char close, int maxSpan)
        {
            int limit = Math.Min(text.Length, from + maxSpan);
            for (int i = from; i < limit; i++)
            {
                if (text[i] == close) return i;
                if (text[i] == '\n' || (close == '>' && text[i] == '<')) return -1;
            }
            return -1;
        }

        private static void AppendCp(StringBuilder sb, int cp, List<string> tokens)
        {
            int t = cp - SentinelBase;
            if (t >= 0 && t < tokens.Count) { sb.Append(tokens[t]); return; }
            sb.Append(char.ConvertFromUtf32(cp));
        }

        // ── The layout ─────────────────────────────────────────────────────────────────────

        /// <summary>What the label shows: every line in visual order, lines separated by '\n'.</summary>
        internal string Display { get; }

        internal int LogicalLength => _logical.Length;
        internal int LineCount => _lineLogStart.Length;

        private readonly string _logical;
        private readonly int[] _dispOfLogical;   // logical UTF-16 → display UTF-16 of its glyph
        private readonly bool[] _rtlOfLogical;   // resolved level odd
        private readonly int[] _lineOfLogical;
        private readonly int[] _lineLogStart, _lineLogEnd;     // logical range of each line (end: its break, or the text's end)
        private readonly int[] _lineDispStart, _lineDispEnd;   // display range of each line, '\n' excluded
        private readonly bool[] _lineParaRtl;
        private readonly int[] _logicalOfDisplay;              // display UTF-16 → first logical index shown there (-1: a line separator)

        private RtlFieldLayout(Prepared p, IList<int> softWrapsInMeasure)
        {
            _logical = p.Logical;
            int n = p.Cps.Length;

            // Soft wraps as codepoint indices, sorted, strictly inside (0, n).
            var wrapCps = new SortedSet<int>();
            if (softWrapsInMeasure != null)
                foreach (int m in softWrapsInMeasure)
                {
                    int cp = p.CpAtMeasure(m);
                    if (cp > 0 && cp < n) wrapCps.Add(cp);
                }

            // Levels per paragraph (hard break = '\n'); the '\n' itself takes its paragraph's level.
            var levels = new sbyte[n];
            var paraRtlOfCp = new bool[n + 1];
            int paraStart = 0;
            bool lastParaRtl = false;
            for (int i = 0; i <= n; i++)
            {
                if (i < n && p.Cps[i] != '\n') continue;
                int len = i - paraStart;
                bool rtl = lastParaRtl;
                if (len > 0)
                {
                    var slice = new int[len];
                    Array.Copy(p.Cps, paraStart, slice, 0, len);
                    _bidiData.Init(new Slice<int>(slice), 2);   // 2: first strong decides (P2/P3)
                    _bidi.Process(_bidiData);
                    var resolved = _bidi.ResolvedLevels;
                    for (int k = 0; k < len; k++) levels[paraStart + k] = resolved[k];
                    // A paragraph with no strong character keeps the direction of the one before.
                    if (HasStrong(slice)) rtl = (_bidi.ResolvedParagraphEmbeddingLevel & 1) == 1;
                }
                for (int k = paraStart; k <= i && k <= n; k++) paraRtlOfCp[k] = rtl;
                if (i < n) levels[i] = (sbyte)(rtl ? 1 : 0);
                lastParaRtl = rtl;
                paraStart = i + 1;
            }

            // Lines: [start, end) in codepoints, end at a '\n' (excluded) or a soft wrap.
            var lineStarts = new List<int>();
            var lineEnds = new List<int>();
            int ls = 0;
            for (int i = 0; i <= n; i++)
            {
                bool hard = i < n && p.Cps[i] == '\n';
                bool soft = wrapCps.Contains(i);
                if (i < n && !hard && !soft) continue;
                if (soft && !hard)
                {
                    lineStarts.Add(ls); lineEnds.Add(i); ls = i;
                    if (i == n) break;
                    continue;
                }
                lineStarts.Add(ls); lineEnds.Add(i); ls = i + 1;
            }

            int lineCount = lineStarts.Count;
            var dispOfCp = new int[n];
            var display = new StringBuilder(p.MeasureText.Length + lineCount);
            var cpOfDisplay = new List<int>(p.MeasureText.Length + lineCount);
            var lineDispStart = new int[lineCount];
            var lineDispEnd = new int[lineCount];
            var lineParaRtl = new bool[lineCount];

            for (int L = 0; L < lineCount; L++)
            {
                int a = lineStarts[L], b = lineEnds[L];
                lineParaRtl[L] = paraRtlOfCp[Math.Min(a, n)];
                sbyte paraLevel = (sbyte)(lineParaRtl[L] ? 1 : 0);

                var cps = new List<int>(b - a);
                var lv = new List<sbyte>(b - a);
                var orig = new List<int>(b - a);
                for (int i = a; i < b; i++) { cps.Add(p.Cps[i]); lv.Add(levels[i]); orig.Add(i); }

                // L1: whitespace at the end of a line goes back to the paragraph level.
                for (int k = lv.Count - 1; k >= 0 && IsWhitespace(cps[k]); k--) lv[k] = paraLevel;

                // Mirroring at odd levels (L4), then the visual order of this line (L2).
                for (int k = 0; k < cps.Count; k++)
                {
                    if ((lv[k] & 1) == 0) continue;
                    int cp = cps[k];
                    if (UnicodeClasses.PairedBracketType(cp) != PairedBracketType.n)
                    {
                        int opposite = UnicodeClasses.AssociatedBracket(cp);
                        if (opposite != 0) cps[k] = opposite;
                    }
                    else if (RtlComposer.ExtraMirrors.TryGetValue(cp, out int mirrored)) cps[k] = mirrored;
                }
                RtlComposer.Reorder(cps, lv, orig);

                lineDispStart[L] = display.Length;
                for (int k = 0; k < cps.Count; k++)
                {
                    dispOfCp[orig[k]] = display.Length;
                    int before = display.Length;
                    AppendCp(display, cps[k], p.Tokens);
                    for (int u = before; u < display.Length; u++) cpOfDisplay.Add(orig[k]);
                }
                lineDispEnd[L] = display.Length;

                if (L < lineCount - 1)
                {
                    // A hard break maps its '\n' here; a soft one is ours alone.
                    if (b < n && p.Cps[b] == '\n') dispOfCp[b] = display.Length;
                    display.Append('\n');
                    cpOfDisplay.Add(b < n && p.Cps[b] == '\n' ? b : -1);
                }
                else if (b < n && p.Cps[b] == '\n')
                {
                    dispOfCp[b] = display.Length;   // a text ending on a break: an empty last line follows
                }
            }
            Display = display.ToString();

            // Logical-level tables.
            int len2 = _logical.Length;
            _dispOfLogical = new int[len2];
            _rtlOfLogical = new bool[len2];
            _lineOfLogical = new int[len2];
            // The line each codepoint sits on; a hard break belongs to the line it closes.
            var cpLine = new int[n + 1];
            for (int L = 0; L < lineCount; L++)
            {
                for (int i = lineStarts[L]; i < lineEnds[L]; i++) cpLine[i] = L;
                if (lineEnds[L] < n && p.Cps[lineEnds[L]] == '\n') cpLine[lineEnds[L]] = L;
            }
            for (int i = 0; i < len2; i++)
            {
                int cp = p.CpOfLogical[i];
                int d = cp < n ? dispOfCp[cp] : Display.Length;
                bool isToken = p.Cps.Length > cp && cp < n && p.Cps[cp] - SentinelBase >= 0 && p.Cps[cp] - SentinelBase < p.Tokens.Count;
                _dispOfLogical[i] = isToken ? d + p.OffsetInCp[i] : d;
                _rtlOfLogical[i] = cp < n && (levels[cp] & 1) == 1 && !isToken;
                _lineOfLogical[i] = cp < n ? cpLine[cp] : lineCount - 1;
            }

            _logicalOfDisplay = new int[Display.Length];
            for (int d = 0; d < Display.Length; d++) _logicalOfDisplay[d] = -1;
            for (int i = len2 - 1; i >= 0; i--)
            {
                int d = _dispOfLogical[i];
                if (d >= 0 && d < Display.Length) _logicalOfDisplay[d] = i;
            }
            // The low half of a surrogate pair is the same glyph as its high half.
            for (int d = 1; d < Display.Length; d++)
                if (_logicalOfDisplay[d] < 0 && char.IsLowSurrogate(Display[d]) && char.IsHighSurrogate(Display[d - 1]))
                    _logicalOfDisplay[d] = _logicalOfDisplay[d - 1];

            _lineLogStart = new int[lineCount];
            _lineLogEnd = new int[lineCount];
            for (int L = 0; L < lineCount; L++)
            {
                _lineLogStart[L] = LogicalAtCp(p, lineStarts[L], len2);
                _lineLogEnd[L] = LogicalAtCp(p, lineEnds[L], len2);
            }
            _lineDispStart = lineDispStart;
            _lineDispEnd = lineDispEnd;
            _lineParaRtl = lineParaRtl;
        }

        /// <summary>The first logical index whose glyph is codepoint <paramref name="cp"/> or later.</summary>
        private static int LogicalAtCp(Prepared p, int cp, int length)
        {
            for (int i = 0; i < length; i++) if (p.CpOfLogical[i] >= cp) return i;
            return length;
        }

        /// <summary>A strong character by the Unicode classes (L, R, AL) — what P2 looks for.</summary>
        private static bool HasStrong(int[] cps)
        {
            foreach (int cp in cps)
            {
                var d = UnicodeClasses.Directionality(cp);
                if (d == Directionality.L || d == Directionality.R || d == Directionality.AL) return true;
            }
            return false;
        }

        private static bool IsWhitespace(int cp) => cp == ' ' || cp == '\t' || cp == 0x3000 || cp == 0x200B;

        // ── Questions an input field asks ──────────────────────────────────────────────────

        /// <summary>Is the line holding this caret laid out from the right?</summary>
        internal bool LineIsRtl(int line) => line >= 0 && line < _lineParaRtl.Length && _lineParaRtl[line];

        internal int LineDisplayStart(int line) => _lineDispStart[line];
        internal int LineDisplayEnd(int line) => _lineDispEnd[line];

        /// <summary>
        /// The line a caret belongs to. A caret at a soft wrap belongs to the line it starts —
        /// the one where the next typed character appears.
        /// </summary>
        internal int LineOfCaret(int caret)
        {
            caret = Clamp(caret);
            for (int L = LineCount - 1; L >= 0; L--)
                if (caret >= _lineLogStart[L]) return L;
            return 0;
        }

        /// <summary>
        /// Where to draw the caret sitting before logical character <paramref name="caret"/>: the
        /// display index of a glyph and which of its edges. The side is the one the typed text
        /// flows from — before a right-to-left letter is its RIGHT edge. At the end of a line
        /// the character before it anchors instead. <paramref name="displayIndex"/> is -1 on an
        /// empty line: the caller puts the caret at the line's starting side.
        /// </summary>
        internal void CaretAnchor(int caret, out int displayIndex, out bool rightEdge, out int line)
        {
            caret = Clamp(caret);
            line = LineOfCaret(caret);
            bool atLineEnd = caret >= _lineLogEnd[line];

            if (!atLineEnd && caret < _logical.Length && _logical[caret] != '\n')
            {
                displayIndex = _dispOfLogical[caret];
                rightEdge = _rtlOfLogical[caret];
                return;
            }
            if (caret > _lineLogStart[line])
            {
                int before = caret - 1;
                displayIndex = _dispOfLogical[before];
                rightEdge = !_rtlOfLogical[before];
                return;
            }
            displayIndex = -1;
            rightEdge = _lineParaRtl[line];
        }

        /// <summary>
        /// The caret a click lands on, from the glyph under the pointer and which half of it: in a
        /// right-to-left glyph the right half is BEFORE it. A ligature showing several typed
        /// characters is stepped over whole.
        /// </summary>
        internal int CaretFromHit(int displayIndex, bool rightHalf)
        {
            if (displayIndex < 0 || displayIndex >= _logicalOfDisplay.Length) return _logical.Length;
            int first = _logicalOfDisplay[displayIndex];
            if (first < 0)
            {
                // A line separator: the end of the line it closes.
                for (int L = 0; L < LineCount; L++)
                    if (_lineDispEnd[L] == displayIndex) return _lineLogEnd[L];
                return _logical.Length;
            }
            int last = first;
            while (last + 1 < _logical.Length && _dispOfLogical[last + 1] == _dispOfLogical[first]
                   && _logical[last + 1] != '\n') last++;
            bool before = _rtlOfLogical[first] ? rightHalf : !rightHalf;
            return before ? first : last + 1;
        }

        /// <summary>
        /// The caret at one visual end of a line — for a click beyond the text, left or right of it.
        /// </summary>
        internal int CaretAtLineSide(int line, bool rightSide)
        {
            line = Math.Max(0, Math.Min(LineCount - 1, line));
            int best = -1, bestX = 0;
            foreach (int caret in CaretsOf(line))
            {
                int x = BoundaryOf(caret);
                if (best < 0 || (rightSide ? x > bestX : x < bestX)) { best = caret; bestX = x; }
            }
            return best < 0 ? _lineLogStart[line] : best;
        }

        /// <summary>
        /// The arrow keys follow the SCREEN (user's decision, 2026-09-25): the neighbouring caret
        /// position to the left (<paramref name="toRight"/> false) or the right. Past the end of a
        /// line, onto the next line in reading order.
        /// </summary>
        internal int VisualStep(int caret, bool toRight)
        {
            caret = Clamp(caret);
            int line = LineOfCaret(caret);
            int here = BoundaryOf(caret);

            int best = -1, bestX = 0;
            foreach (int candidate in CaretsOf(line))
            {
                int x = BoundaryOf(candidate);
                if (toRight ? x <= here : x >= here) continue;
                bool closer = best < 0 || (toRight ? x < bestX : x > bestX)
                              || (x == bestX && Math.Abs(candidate - caret) < Math.Abs(best - caret));
                if (closer) { best = candidate; bestX = x; }
            }
            if (best >= 0) return best;

            // Off the edge of the line: onward in reading order. Moving toward the side a line
            // ENDS on (left in a right-to-left line) goes to the next line; the other way, back.
            bool forward = toRight != LineIsRtl(line);
            if (forward)
                return line + 1 < LineCount ? LineStartCaret(line + 1) : caret;
            return line > 0 ? _lineLogEnd[line - 1] : caret;
        }

        private int LineStartCaret(int line) => _lineLogStart[line];

        /// <summary>The caret positions that sit on a line, its end included only for a hard end.</summary>
        private IEnumerable<int> CaretsOf(int line)
        {
            int from = _lineLogStart[line];
            int to = _lineLogEnd[line];
            bool softEnd = line + 1 < LineCount && _lineLogStart[line + 1] == to;
            for (int c = from; c <= to; c++)
            {
                if (c == to && softEnd) break;
                // A caret inside a ligature is not a place anybody can see.
                if (c > from && c < _logical.Length && _dispOfLogical[c] == _dispOfLogical[c - 1]
                    && _logical[c] != '\n' && c - 1 >= 0 && _logical[c - 1] != '\n') continue;
                yield return c;
            }
        }

        /// <summary>A caret as a display boundary: the index of the gap it stands in.</summary>
        private int BoundaryOf(int caret)
        {
            CaretAnchor(caret, out int d, out bool right, out int line);
            if (d < 0) return _lineParaRtl[line] ? _lineDispEnd[line] : _lineDispStart[line];
            // A display index is one glyph — a shaped letter, a ligature, one character of a token
            // written out — except a surrogate pair, which spans two.
            if (!right) return d;
            return d + (d < Display.Length && char.IsHighSurrogate(Display[d]) ? 2 : 1);
        }

        private int Clamp(int caret) => caret < 0 ? 0 : caret > _logical.Length ? _logical.Length : caret;

        /// <summary>Display index of the glyph showing logical character <paramref name="i"/>.</summary>
        internal int DisplayOf(int i) => _dispOfLogical[Math.Max(0, Math.Min(_logical.Length - 1, i))];

        /// <summary>Whether logical character <paramref name="i"/> is laid out right-to-left.</summary>
        internal bool IsRtl(int i) => i >= 0 && i < _logical.Length && _rtlOfLogical[i];
    }
}
