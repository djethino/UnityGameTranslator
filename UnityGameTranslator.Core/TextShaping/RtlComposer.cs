using System;
using System.Collections.Generic;
using System.Text;
using Topten.RichTextKit;
using Topten.RichTextKit.Utils;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Which markup a text carries — what the composer keeps as structure rather than content: Unity's
    /// and TextMesh Pro's &lt;tags&gt;, or NGUI's [codes] (<see cref="NguiMarkup"/>).
    /// </summary>
    internal enum MarkupSyntax
    {
        AngleTags,
        NguiCodes,
        /// <summary>No markup at all: every character is text (an NGUI label with its encoding off).</summary>
        None,
    }

    /// <summary>How the composed string will be consumed — the two shapes stage D hands out.</summary>
    internal enum RtlOutput
    {
        /// <summary>
        /// Logical order with LTR runs reversed, for engines that expose
        /// <c>isRightToLeftText</c> (TMP, TMProOld — proven on the bench): the engine lays the
        /// string out leftwards and owns wrapping and line order.
        /// </summary>
        RtlFlagged,

        /// <summary>
        /// Fully visual order, for engines with no RTL support (UI.Text, TextMesh, tk2d,
        /// UI Toolkit without ATG): correct on one line; multi-line needs stage D's per-line
        /// work (bench: bio/silk/avia2 — reversed line stacks otherwise).
        /// </summary>
        VisualOrder,
    }

    /// <summary>
    /// Stage C: from a LOGICAL translated string to the string an engine can display — shaping
    /// (stage B, injected), UAX#9 bidi (vendored RichTextKit — decision D2: the full algorithm,
    /// there is only one correct bidi), reordering, bracket mirroring, and the protection of the
    /// things only THIS project knows about: its placeholders and rich-text tags (decision D7 —
    /// the reason this layer is written here and not borrowed).
    ///
    /// Two protections, two mechanics, learnt on the bench (biopb):
    /// - a PLACEHOLDER is visible content: it travels through bidi as one atomic sentinel
    ///   codepoint of class L, and reorders like the word it stands for;
    /// - a TAG is structure, not content: letting it travel as content had the bidi move it and
    ///   the engine rendered the mangled markup literally. Tags are pulled out after shaping
    ///   (they must still break joining, as a tag inside a word is a boundary), the text is
    ///   reordered with its permutation TRACKED, and each matched pair is re-wrapped around the
    ///   final positions of the very glyphs it styled — parse-valid in string order, whatever
    ///   the display direction.
    ///
    /// 🔴 Nothing composed here may ever reach the cache, the file or the server (D8).
    /// ⚠ Main thread only, like the shaper: the Bidi instances below are stateful.
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class RtlComposer
    {
        // Stateful, reused across calls — same lifecycle as the vendored shaper's buffers.
        private static readonly Bidi _bidi = new Bidi();
        private static readonly BidiData _bidiData = new BidiData();
        private static readonly PresentationFormsShaper _shaper = new PresentationFormsShaper();

        // Placeholders become private-use codepoints before shaping and bidi (class L in the
        // UCD — verified in checks), expanded back at the very end. Tags use a SEPARATE sentinel
        // range only through the shaping step, then leave the stream entirely.
        // ⚠ Both ranges sit ABOVE the private codepoints our font assets hand to unmapped
        // glyphs (TtfFontPipeline.PrivateGlyphBase..PrivateGlyphLast = E000..F0FF): those DO
        // travel in displayed text, a sentinel never does, and the two must not overlap.
        internal const int PlaceholderBase = 0xF100;
        private const int TagBase = 0xF500;

        /// <summary>How many tokens of one kind a text can carry: the width of its sentinel range.</summary>
        internal const int SentinelMax = TagBase - PlaceholderBase;

        /// <summary>
        /// How far after its '&lt;' a tag's '&gt;' may stand: TextMesh Pro's own limit. Its parser
        /// copies a tag into <c>m_htmlTag = new char[128]</c> and stops when that is full
        /// (TMP_Text.ValidateHtmlTag, identical in 2.0.1 and 3.0.6), so at most 127 characters sit
        /// between the brackets. A longer "tag" is text to the game, and must be text here too —
        /// read as structure it would be lifted out of a line the game shows as written. The same
        /// bound stops a lone '&lt;' in a sentence from swallowing what follows.
        /// </summary>
        internal const int TagSpan = 128;

        /// <summary>
        /// The markup of the text being composed, for the length of a <see cref="UseMarkup"/> scope —
        /// a setting of the call, like the output shape, held here because every entry (Compose,
        /// ShapeLogicalOnly, HasLtrRunAcrossSpace) reaches the same tokenizer. Main thread only, as the
        /// whole composer is.
        /// </summary>
        private static MarkupSyntax _markup = MarkupSyntax.AngleTags;

        // The placeholder sentinels standing for a <br> in the text being composed (Tokenize):
        // content for the stream, a paragraph separator for the bidi (Resolve).
        private static readonly HashSet<int> _lineBreakSentinels = new HashSet<int>();

        /// <summary>Composes with this markup until the returned scope is disposed.</summary>
        internal static IDisposable UseMarkup(MarkupSyntax markup) => new MarkupScope(markup);

        private sealed class MarkupScope : IDisposable
        {
            private readonly MarkupSyntax _before;
            internal MarkupScope(MarkupSyntax markup) { _before = _markup; _markup = markup; }
            public void Dispose() => _markup = _before;
        }

        /// <summary>The length of the markup token at <paramref name="at"/> under the current syntax, or 0.</summary>
        private static int MarkupLengthAt(string text, int at)
        {
            if (_markup == MarkupSyntax.NguiCodes) return NguiMarkup.LengthAt(text, at);
            if (_markup == MarkupSyntax.None) return 0;
            int end;
            if (text[at] == '<' && at + 1 < text.Length && text[at + 1] != ' ' && text[at + 1] != '<'
                && (end = TagEnd(text, at, stopAtLineBreak: false)) > 0)
                return end + 1 - at;
            return 0;
        }

        private static bool MarkupIsClosing(string tag) => _markup == MarkupSyntax.NguiCodes ? NguiMarkup.IsClosing(tag) : Markup.IsClosing(tag);

        private static string MarkupPairName(string tag) => _markup == MarkupSyntax.NguiCodes ? NguiMarkup.PairName(tag) : Markup.NameOf(tag);

        private sealed class TagInfo
        {
            public string Text;
            public int Anchor;        // index in the TAGLESS stream of the cp that followed it
            public int PairOpen = -1; // for a closing tag: index of its opening TagInfo
            public int Depth;
        }

        /// <summary>
        /// Compose one logical string for display. Call only when
        /// <see cref="RtlText.NeedsPresentation"/> said yes; the paragraph direction is forced
        /// RTL for that reason (a translated Arabic line that happens to START with a
        /// placeholder or a number must not flip the whole paragraph to LTR).
        /// </summary>
        internal static string Compose(string logical, RtlOutput output)
        {
            if (string.IsNullOrEmpty(logical)) return logical;

            // 1-3. Tokens protected, shaped, tags lifted out, UAX#9 run.
            var arr = Resolve(logical, out var placeholders, out var tagInfos, out var levels);

            // 4. Mirror brackets at RTL levels (L4), drop the X9-removed formatting controls —
            //    keeping the ORIGINAL index of every surviving codepoint: the tags get wrapped
            //    back by position, so the permutation must be known, not merely applied.
            var kept = new List<int>(arr.Length);
            var keptLevels = new List<sbyte>(arr.Length);
            var keptOrig = new List<int>(arr.Length);
            for (int i = 0; i < arr.Length; i++)
            {
                if (Bidi.IsRemovedByX9(_bidiData.Types[i])) continue;

                int cp = arr[i];
                if ((levels[i] & 1) == 1)
                    cp = UnicodeInfo.MirrorOf(cp);
                kept.Add(cp);
                keptLevels.Add(levels[i]);
                keptOrig.Add(i);
            }

            // 5. L2 reordering — the visual order.
            Reorder(kept, keptLevels, keptOrig);

            // 6. The flagged form is the visual order reversed whole: the engine will lay it out
            //    right-to-left again, which cancels the reversal for RTL runs and yields
            //    forward-reading LTR runs — the recipe the bench validated (avia3/4, silk9/10).
            if (output == RtlOutput.RtlFlagged)
            {
                kept.Reverse();
                keptOrig.Reverse();
            }

            // 7. Where did every original glyph land?
            var posOf = new int[arr.Length];
            for (int i = 0; i < posOf.Length; i++) posOf[i] = -1;
            for (int i = 0; i < keptOrig.Count; i++) posOf[keptOrig[i]] = i;

            // 8. Re-wrap the tags around the final positions of the glyphs they styled, expand
            //    placeholder sentinels, done.
            return BuildWithTags(kept, keptOrig.Count, posOf, tagInfos, placeholders);
        }

        /// <summary>
        /// Steps 1-3 of <see cref="Compose"/>: our tokens protected, then shaped (both kinds of
        /// sentinel sit in the stream so a token inside a word still breaks joining, like the
        /// measured implementation); the TAG sentinels pulled out of the stream with what each
        /// stood before — structure must not travel as content; UAX#9 on the tagless stream,
        /// paragraph level forced RTL. Returns the tagless codepoints; <paramref name="levels"/>
        /// are the bidi's own buffer, valid until its next run.
        /// </summary>
        private static int[] Resolve(string logical, out List<string> placeholders, out List<TagInfo> tagInfos,
                                     out Slice<sbyte> levels)
        {
            placeholders = new List<string>();
            var tags = new List<string>();
            string sentinelized = Tokenize(logical, placeholders, tags);
            string shaped = _shaper.Shape(sentinelized);

            var cpsAll = ToCodePoints(shaped);
            var cps = new List<int>(cpsAll.Length);
            tagInfos = new List<TagInfo>();
            foreach (int cp in cpsAll)
            {
                int tagIndex = cp - TagBase;
                if (tagIndex >= 0 && tagIndex < tags.Count)
                    tagInfos.Add(new TagInfo { Text = tags[tagIndex], Anchor = cps.Count });
                else
                    cps.Add(cp);
            }
            MatchTagPairs(tagInfos);

            var arr = cps.ToArray();
            // A private glyph codepoint (a positioned mark, a kerned letter — see FontShaping) is
            // class L in the UCD, which would cut a right-to-left run in two around it. For
            // the bidi it is read as a non-spacing mark: it takes the direction of what it
            // follows and travels with it, which is where its glyph belongs — the mark before
            // its base once the run is reversed, exactly the order its offsets were computed for.
            var bidiInput = arr;
            for (int i = 0; i < arr.Length; i++)
                if (PrivateGlyphs.Contains(arr[i]))
                {
                    if (ReferenceEquals(bidiInput, arr)) bidiInput = (int[])arr.Clone();
                    bidiInput[i] = 0x0300;
                }
                else if (_lineBreakSentinels.Contains(arr[i]))
                {
                    // A <br>: the paragraph separator for the bidi, like '\n' (Tokenize).
                    if (ReferenceEquals(bidiInput, arr)) bidiInput = (int[])arr.Clone();
                    bidiInput[i] = '\n';
                }
                else if (i > 0 &&i + 1 < arr.Length && UnicodeInfo.IsNumericJoiner(arr[i])
                         && UnicodeInfo.IsWordNumeric(arr[i - 1]) && UnicodeInfo.IsWordNumeric(arr[i + 1]))
                {
                    // Inside a number (UAX #29, WB11-12): read as a common separator, which rule W4
                    // keeps between two digits of one type. An apostrophe is ON for the bidi, so
                    // 3'000 was two numbers and came out 000'3 at a right-to-left level.
                    if (ReferenceEquals(bidiInput, arr)) bidiInput = (int[])arr.Clone();
                    bidiInput[i] = ',';
                }
            _bidiData.Init(new Slice<int>(bidiInput), 1);
            _bidi.Process(_bidiData);
            levels = _bidi.ResolvedLevels;
            return arr;
        }

        /// <summary>
        /// Whether a left-to-right run of several words sits in this right-to-left text — a space
        /// resolved at an EVEN level, between two left-to-right words ("Schedule I" in Hebrew).
        /// Such a run is what an engine wrapping the flagged form cuts wrongly: it wraps the run
        /// written backwards, so a word of it lands on the wrong line (RtlPresenter, TMP reflow).
        /// </summary>
        internal static bool HasLtrRunAcrossSpace(string logical)
        {
            if (string.IsNullOrEmpty(logical) || logical.IndexOf(' ') < 0) return false;
            var arr = Resolve(logical, out _, out _, out var levels);
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] == ' ' && levels[i] > 0 && (levels[i] & 1) == 0) return true;
            return false;
        }

        /// <summary>
        /// Drop underline and strikethrough tags — exactly &lt;u&gt; &lt;/u&gt; &lt;s&gt; &lt;/s&gt;,
        /// case-insensitive, nothing else (&lt;ul&gt;, &lt;size…&gt; pass untouched). Unity 6's
        /// TextCore DrawUnderlineMesh throws IndexOutOfRange generating the underline of Arabic
        /// text — the '_' glyph resolves against another font asset than the RTL glyphs' fallback
        /// and meshInfo[materialIndex] indexes out of bounds; one mesh routine draws both
        /// features, hence both tags. Returns the SAME instance when there is nothing to drop.
        /// </summary>
        internal static string StripUnderlineTags(string text)
        {
            if (text == null || text.IndexOf('<') < 0) return text;
            StringBuilder sb = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<')
                {
                    int rest = text.Length - i;
                    if (rest >= 3 && text[i + 2] == '>' && IsUnderlineName(text[i + 1]))
                    {
                        if (sb == null) sb = new StringBuilder(text.Length).Append(text, 0, i);
                        i += 2;
                        continue;
                    }
                    if (rest >= 4 && text[i + 1] == '/' && text[i + 3] == '>' && IsUnderlineName(text[i + 2]))
                    {
                        if (sb == null) sb = new StringBuilder(text.Length).Append(text, 0, i);
                        i += 3;
                        continue;
                    }
                }
                sb?.Append(c);
            }
            return sb == null ? text : sb.ToString();
        }

        private static bool IsUnderlineName(char c)
            => c == 'u' || c == 'U' || c == 's' || c == 'S';

        /// <summary>
        /// Shaping and token protection ONLY — logical order in, logical order out. This is what
        /// gets ASSIGNED to a no-flag engine so its own wrapping cuts the paragraph at the
        /// correct logical points; the per-line visual conversion then happens on each cut line
        /// via <see cref="Compose"/>. Tags and placeholders come back verbatim, in place.
        /// </summary>
        internal static string ShapeLogicalOnly(string logical)
        {
            if (string.IsNullOrEmpty(logical)) return logical;
            var placeholders = new List<string>();
            var tags = new List<string>();
            string sentinelized = Tokenize(logical, placeholders, tags);
            string shaped = _shaper.Shape(sentinelized);
            var cps = ToCodePoints(shaped);
            var sb = new StringBuilder(shaped.Length);
            foreach (int cp in cps) AppendExpanded(sb, cp, placeholders, tags);
            return sb.ToString();
        }

        #region Tokenization

        /// <summary>
        /// Swap every protected span for one sentinel codepoint. Placeholders as the socle's grammar
        /// names them (<see cref="Placeholders.LengthAt"/>), and the markup of the text's engine
        /// (<see cref="UseMarkup"/>): rich-text tags <c>&lt;…&gt;</c> under the rule the measured
        /// implementation uses — no space after <c>&lt;</c>, no nested <c>&lt;</c>, closed within
        /// <see cref="TagSpan"/> — or NGUI's codes.
        /// </summary>
        private static string Tokenize(string text, List<string> placeholders, List<string> tags)
        {
            var sb = new StringBuilder(text.Length);
            _lineBreakSentinels.Clear();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                int length;
                if ((length = Placeholders.LengthAt(text, i)) > 0 && placeholders.Count < SentinelMax)
                {
                    sb.Append((char)(PlaceholderBase + placeholders.Count));
                    placeholders.Add(text.Substring(i, length));
                    i += length;
                    continue;
                }
                // 🔴 <br> is a LINE BREAK, not styling: lifted out with the tags, it let the bidi
                // order both lines as one, and a price under its label came out with its first digit
                // on the second line and the rest on the first ("شراء<br>173.28" shown "شراء73.28" /
                // "1", 2026-10-03). Kept in the stream as content, read by the bidi as the paragraph
                // separator '\n' is (Resolve): each line ordered on its own, as TMP and UI Toolkit
                // break them. An engine that shows <br> as text gets it at its reading place.
                if (_markup == MarkupSyntax.AngleTags && placeholders.Count < SentinelMax
                    && (length = MarkupLengthAt(text, i)) > 0 && Markup.NameOf(text.Substring(i, length)) == "br")
                {
                    _lineBreakSentinels.Add(PlaceholderBase + placeholders.Count);
                    sb.Append((char)(PlaceholderBase + placeholders.Count));
                    placeholders.Add(text.Substring(i, length));
                    i += length;
                    continue;
                }
                if (tags.Count < SentinelMax && (length = MarkupLengthAt(text, i)) > 0)
                {
                    sb.Append((char)(TagBase + tags.Count));
                    tags.Add(text.Substring(i, length));
                    i += length;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// The index of the '&gt;' closing the tag opened at <paramref name="open"/>, or -1 — within
        /// <see cref="TagSpan"/>, and a '&lt;' aborts, as TextMesh Pro's parser does. Shared with the
        /// RTL field and the legacy index map, so the three agree on what a tag is.
        /// </summary>
        internal static int TagEnd(string text, int open, bool stopAtLineBreak)
        {
            int limit = Math.Min(text.Length, open + 1 + TagSpan);
            for (int i = open + 1; i < limit; i++)
            {
                if (text[i] == '>') return i;
                if (text[i] == '<' || (stopAtLineBreak && text[i] == '\n')) return -1;
            }
            return -1;
        }

        #endregion

        #region Tag pairing and reinsertion

        /// <summary>Match an opening with its closing by name (&lt;x…&gt; / &lt;/x&gt;, [b] / [/b], a colour / [-]), tracking nesting depth.</summary>
        private static void MatchTagPairs(List<TagInfo> tagInfos)
        {
            var stack = new List<int>();
            for (int i = 0; i < tagInfos.Count; i++)
            {
                string t = tagInfos[i].Text;
                if (MarkupIsClosing(t))
                {
                    // Named by the socle for tags, so a model's answer and the screen pair them alike.
                    string name = MarkupPairName(t);
                    for (int s = stack.Count - 1; s >= 0; s--)
                    {
                        if (MarkupPairName(tagInfos[stack[s]].Text) != name) continue;
                        tagInfos[i].PairOpen = stack[s];
                        tagInfos[i].Depth = tagInfos[stack[s]].Depth = s;
                        stack.RemoveRange(s, stack.Count - s);
                        break;
                    }
                }
                else if (MarkupPairName(t) != null)
                {
                    tagInfos[i].Depth = stack.Count;
                    stack.Add(i);
                }
            }
        }

        private sealed class Insert
        {
            public int Pos;
            public int Order;   // at equal Pos: closings (descending depth) before openings (ascending depth)
            public string Text;
        }

        private static string BuildWithTags(List<int> finalCps, int finalLen, int[] posOf,
                                            List<TagInfo> tagInfos, List<string> placeholders)
        {
            var inserts = new List<Insert>();
            var opened = new HashSet<int>();

            for (int i = 0; i < tagInfos.Count; i++)
            {
                var tag = tagInfos[i];
                if (tag.PairOpen >= 0)
                {
                    // A matched pair: wrap the final span of the glyphs it styled.
                    var open = tagInfos[tag.PairOpen];
                    int min = int.MaxValue, max = int.MinValue;
                    for (int orig = open.Anchor; orig < tag.Anchor; orig++)
                    {
                        if (orig >= posOf.Length || posOf[orig] < 0) continue;
                        if (posOf[orig] < min) min = posOf[orig];
                        if (posOf[orig] > max) max = posOf[orig];
                    }
                    if (min == int.MaxValue)
                    {
                        // Styled nothing that survived — keep the pair adjacent at its anchor.
                        int at = FinalPosForAnchor(open.Anchor, posOf, finalLen);
                        min = at; max = at - 1;
                    }
                    inserts.Add(new Insert { Pos = min, Order = 1000 + open.Depth, Text = open.Text });
                    inserts.Add(new Insert { Pos = max + 1, Order = -open.Depth, Text = tag.Text });
                    opened.Add(tag.PairOpen);
                    opened.Add(i);
                }
            }
            for (int i = 0; i < tagInfos.Count; i++)
            {
                if (opened.Contains(i)) continue;
                // Unpaired (<br>, <sprite=…>, a lone tag): best effort, before the glyph it
                // originally preceded.
                var tag = tagInfos[i];
                inserts.Add(new Insert { Pos = FinalPosForAnchor(tag.Anchor, posOf, finalLen), Order = 500, Text = tag.Text });
            }

            inserts.Sort((a, b) => a.Pos != b.Pos ? a.Pos.CompareTo(b.Pos) : a.Order.CompareTo(b.Order));

            var sb = new StringBuilder(finalLen + 16);
            int insertIdx = 0;
            for (int pos = 0; pos <= finalCps.Count; pos++)
            {
                while (insertIdx < inserts.Count && inserts[insertIdx].Pos == pos)
                    sb.Append(inserts[insertIdx++].Text);
                if (pos < finalCps.Count)
                    AppendExpanded(sb, finalCps[pos], placeholders, null);
            }
            return sb.ToString();
        }

        private static int FinalPosForAnchor(int anchor, int[] posOf, int finalLen)
        {
            for (int orig = anchor; orig < posOf.Length; orig++)
                if (posOf[orig] >= 0) return posOf[orig];
            return finalLen;
        }

        #endregion

        private static int[] ToCodePoints(string s)
        {
            var list = new List<int>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                int cp = char.ConvertToUtf32(s, i);
                if (cp > 0xFFFF) i++;
                list.Add(cp);
            }
            return list.ToArray();
        }

        private static void AppendExpanded(StringBuilder sb, int cp, List<string> placeholders, List<string> tags)
        {
            int p = cp - PlaceholderBase;
            if (p >= 0 && p < placeholders.Count) { sb.Append(placeholders[p]); return; }
            if (tags != null)
            {
                int t = cp - TagBase;
                if (t >= 0 && t < tags.Count) { sb.Append(tags[t]); return; }
            }
            sb.Append(char.ConvertFromUtf32(cp));
        }

        /// <summary>UAX#9 L2 on a resolved-levels sequence, in place — permutation tracked.</summary>
        internal static void Reorder(List<int> cps, List<sbyte> levels, List<int> orig)
        {
            sbyte max = 0;
            sbyte minOdd = sbyte.MaxValue;
            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i] > max) max = levels[i];
                if ((levels[i] & 1) == 1 && levels[i] < minOdd) minOdd = levels[i];
            }
            if (minOdd == sbyte.MaxValue) return;

            for (sbyte level = max; level >= minOdd; level--)
            {
                int i = 0;
                while (i < levels.Count)
                {
                    if (levels[i] < level) { i++; continue; }
                    int start = i;
                    while (i < levels.Count && levels[i] >= level) i++;
                    ReverseRange(cps, levels, orig, start, i - 1);
                }
            }
        }

        private static void ReverseRange(List<int> cps, List<sbyte> levels, List<int> orig, int a, int b)
        {
            while (a < b)
            {
                (cps[a], cps[b]) = (cps[b], cps[a]);
                (levels[a], levels[b]) = (levels[b], levels[a]);
                (orig[a], orig[b]) = (orig[b], orig[a]);
                a++;
                b--;
            }
        }
    }
}
