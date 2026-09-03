using System;
using System.Collections.Generic;
using System.Text;
using Topten.RichTextKit;
using Topten.RichTextKit.Utils;

namespace UnityGameTranslator.Core.TextShaping
{
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
        private const int PlaceholderBase = 0xF100;
        private const int TagBase = 0xF500;
        private const int SentinelMax = 0x400;

        // Mirrored by RTL convention though not Bidi_Mirrored in the UCD — guillemets read
        // outward-in in RTL text. Borrowed from RTLTMPro's table; real brackets are NOT here,
        // the UAX#9 paired-bracket data answers those.
        private static readonly Dictionary<int, int> ExtraMirrors = new Dictionary<int, int>
        {
            [0x00AB] = 0x00BB, [0x00BB] = 0x00AB,   // « »
            [0x2039] = 0x203A, [0x203A] = 0x2039,   // ‹ ›
        };

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

            // 1. Protect our tokens, then shape. Both kinds of sentinel sit in the stream here so
            //    a token inside a word still breaks joining, like the measured implementation.
            var placeholders = new List<string>();
            var tags = new List<string>();
            string sentinelized = Tokenize(logical, placeholders, tags);
            string shaped = _shaper.Shape(sentinelized);

            // 2. Codepoints; pull the TAG sentinels out of the stream, remembering what each one
            //    stood before. Structure must not travel as content.
            var cpsAll = ToCodePoints(shaped);
            var cps = new List<int>(cpsAll.Length);
            var tagInfos = new List<TagInfo>();
            foreach (int cp in cpsAll)
            {
                int tagIndex = cp - TagBase;
                if (tagIndex >= 0 && tagIndex < tags.Count)
                    tagInfos.Add(new TagInfo { Text = tags[tagIndex], Anchor = cps.Count });
                else
                    cps.Add(cp);
            }
            MatchTagPairs(tagInfos);

            // 3. UAX#9 on the tagless stream. Paragraph level forced RTL (see summary).
            var arr = cps.ToArray();
            _bidiData.Init(new Slice<int>(arr), 1);
            _bidi.Process(_bidiData);
            var levels = _bidi.ResolvedLevels;

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
                {
                    if (UnicodeClasses.PairedBracketType(cp) != PairedBracketType.n)
                    {
                        int opposite = UnicodeClasses.AssociatedBracket(cp);
                        if (opposite != 0) cp = opposite;
                    }
                    else if (ExtraMirrors.TryGetValue(cp, out int mirrored))
                    {
                        cp = mirrored;
                    }
                }
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
        /// Swap every protected span for one sentinel codepoint. Placeholders <c>[!…]</c> and
        /// rich-text tags <c>&lt;…&gt;</c> under the same validity rule the measured
        /// implementation uses — no space after <c>&lt;</c>, no nested <c>&lt;</c>. Both bounded.
        /// </summary>
        private static string Tokenize(string text, List<string> placeholders, List<string> tags)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                int end;
                if (c == '[' && i + 1 < text.Length && text[i + 1] == '!'
                    && (end = FindClose(text, i + 2, ']', 32)) > 0
                    && placeholders.Count < SentinelMax)
                {
                    sb.Append((char)(PlaceholderBase + placeholders.Count));
                    placeholders.Add(text.Substring(i, end - i + 1));
                    i = end + 1;
                    continue;
                }
                if (c == '<' && i + 1 < text.Length && text[i + 1] != ' ' && text[i + 1] != '<'
                    && (end = FindClose(text, i + 1, '>', 128)) > 0
                    && tags.Count < SentinelMax)
                {
                    sb.Append((char)(TagBase + tags.Count));
                    tags.Add(text.Substring(i, end - i + 1));
                    i = end + 1;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>The index of <paramref name="close"/>, or -1 — bounded, and a '&lt;' aborts a tag scan.</summary>
        private static int FindClose(string text, int from, char close, int maxSpan)
        {
            int limit = Math.Min(text.Length, from + maxSpan);
            for (int i = from; i < limit; i++)
            {
                if (text[i] == close) return i;
                if (close == '>' && text[i] == '<') return -1;
            }
            return -1;
        }

        #endregion

        #region Tag pairing and reinsertion

        /// <summary>Match &lt;x…&gt; with &lt;/x&gt; by name, tracking nesting depth.</summary>
        private static void MatchTagPairs(List<TagInfo> tagInfos)
        {
            var stack = new List<int>();
            for (int i = 0; i < tagInfos.Count; i++)
            {
                string t = tagInfos[i].Text;
                if (t.Length > 2 && t[1] == '/')
                {
                    string name = TagName(t, 2);
                    for (int s = stack.Count - 1; s >= 0; s--)
                    {
                        if (TagName(tagInfos[stack[s]].Text, 1) != name) continue;
                        tagInfos[i].PairOpen = stack[s];
                        tagInfos[i].Depth = tagInfos[stack[s]].Depth = s;
                        stack.RemoveRange(s, stack.Count - s);
                        break;
                    }
                }
                else
                {
                    tagInfos[i].Depth = stack.Count;
                    stack.Add(i);
                }
            }
        }

        private static string TagName(string tag, int from)
        {
            int end = from;
            while (end < tag.Length && char.IsLetterOrDigit(tag[end])) end++;
            return tag.Substring(from, end - from).ToLowerInvariant();
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
        private static void Reorder(List<int> cps, List<sbyte> levels, List<int> orig)
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
