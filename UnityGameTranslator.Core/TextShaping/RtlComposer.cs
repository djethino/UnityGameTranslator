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
    /// 🔴 Nothing composed here may ever reach the cache, the file or the server (D8): callers
    /// hand the result straight to a component and record it only in the shaped→logical index.
    ///
    /// ⚠ Main thread only, like the shaper: the Bidi instances below are stateful.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks, where the conformance suites and
    /// the bench-validated reference strings hold it in place.
    /// </summary>
    internal static class RtlComposer
    {
        // Stateful, reused across calls — same lifecycle as the vendored shaper's buffers.
        private static readonly Bidi _bidi = new Bidi();
        private static readonly BidiData _bidiData = new BidiData();
        private static readonly PresentationFormsShaper _shaper = new PresentationFormsShaper();

        // Tokens (placeholders, tags) are swapped for private-use codepoints before shaping and
        // bidi, and expanded back at the very end. PUA resolves as class L in the UCD, so a token
        // behaves as one atomic LTR "word" — which is exactly what a placeholder or a tag is.
        private const int SentinelBase = 0xE000;
        private const int SentinelMax = 0x1000;

        // Mirrored by RTL convention though not Bidi_Mirrored in the UCD — guillemets read
        // outward-in in RTL text. Borrowed from RTLTMPro's table; real brackets are NOT here,
        // the UAX#9 paired-bracket data answers those.
        private static readonly Dictionary<int, int> ExtraMirrors = new Dictionary<int, int>
        {
            [0x00AB] = 0x00BB, [0x00BB] = 0x00AB,   // « »
            [0x2039] = 0x203A, [0x203A] = 0x2039,   // ‹ ›
        };

        /// <summary>
        /// Compose one logical string for display. Call only when
        /// <see cref="RtlText.NeedsPresentation"/> said yes; the paragraph direction is forced
        /// RTL for that reason (a translated Arabic line that happens to START with a
        /// placeholder or a number must not flip the whole paragraph to LTR).
        /// </summary>
        internal static string Compose(string logical, RtlOutput output)
        {
            if (string.IsNullOrEmpty(logical)) return logical;

            // 1. Protect what is ours: placeholders and tags become one sentinel each.
            var tokens = new List<string>();
            string sentinelized = Tokenize(logical, tokens);

            // 2. Shape (stage B1). A sentinel between two Arabic letters breaks their joining —
            //    deliberate: a tag or placeholder inside a word IS a boundary (same behaviour as
            //    the upstream implementation this is measured against).
            string shaped = _shaper.Shape(sentinelized);

            // 3. Codepoints. Surrogate pairs collapse into single ints so reordering can never
            //    tear an emoji or an astral character apart.
            var cps = ToCodePoints(shaped);

            // 4. UAX#9. Paragraph level forced RTL (see summary).
            _bidiData.Init(new Slice<int>(cps), 1);
            _bidi.Process(_bidiData);
            var levels = _bidi.ResolvedLevels;

            // 5. Mirror brackets sitting at RTL levels (L4), and drop the explicit formatting
            //    controls X9 removed — they are zero-width and meaningless once resolved.
            var kept = new List<int>(cps.Length);
            var keptLevels = new List<sbyte>(cps.Length);
            for (int i = 0; i < cps.Length; i++)
            {
                var dir = _bidiData.Types[i];
                if (Bidi.IsRemovedByX9(dir)) continue;

                int cp = cps[i];
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
            }

            // 6. L2: from the highest level down to 1, reverse every contiguous run at that
            //    level or higher — the visual order.
            Reorder(kept, keptLevels);

            // 7. The flagged form is the visual order reversed whole: the engine will lay it out
            //    right-to-left again, which cancels the reversal for RTL runs and yields
            //    forward-reading LTR runs — the recipe the bench validated (avia3/4, silk9/10).
            if (output == RtlOutput.RtlFlagged) kept.Reverse();

            // 8. Expand the sentinels back, untouched: their internal order was never exposed to
            //    any reversal because each one travelled as a single codepoint.
            return Detokenize(kept, tokens);
        }

        /// <summary>
        /// Swap every protected span for one sentinel codepoint. Two kinds, both bounded:
        /// placeholders <c>[!…]</c> (the pipeline's own <c>[!v*0]</c>, <c>[!t*1]</c>,
        /// <c>[!STR*2]</c>) and rich-text tags <c>&lt;…&gt;</c> under the same validity rule the
        /// measured implementation uses — no space after <c>&lt;</c>, no nested <c>&lt;</c>.
        /// </summary>
        private static string Tokenize(string text, List<string> tokens)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                int end;
                if (c == '[' && i + 1 < text.Length && text[i + 1] == '!'
                    && (end = FindClose(text, i + 2, ']', 32)) > 0)
                {
                    AddToken(sb, tokens, text.Substring(i, end - i + 1));
                    i = end + 1;
                    continue;
                }
                if (c == '<' && i + 1 < text.Length && text[i + 1] != ' ' && text[i + 1] != '<'
                    && (end = FindClose(text, i + 1, '>', 128)) > 0)
                {
                    AddToken(sb, tokens, text.Substring(i, end - i + 1));
                    i = end + 1;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static void AddToken(StringBuilder sb, List<string> tokens, string token)
        {
            if (tokens.Count >= SentinelMax)
            {
                // Absurd input; better unprotected than wrong sentinels.
                sb.Append(token);
                return;
            }
            sb.Append((char)(SentinelBase + tokens.Count));
            tokens.Add(token);
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

        /// <summary>UAX#9 L2 on a resolved-levels sequence, in place.</summary>
        private static void Reorder(List<int> cps, List<sbyte> levels)
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
                    ReverseRange(cps, levels, start, i - 1);
                }
            }
        }

        private static void ReverseRange(List<int> cps, List<sbyte> levels, int a, int b)
        {
            while (a < b)
            {
                (cps[a], cps[b]) = (cps[b], cps[a]);
                (levels[a], levels[b]) = (levels[b], levels[a]);
                a++;
                b--;
            }
        }

        private static string Detokenize(List<int> cps, List<string> tokens)
        {
            var sb = new StringBuilder(cps.Count);
            for (int i = 0; i < cps.Count; i++)
            {
                int cp = cps[i];
                int tokenIndex = cp - SentinelBase;
                if (tokenIndex >= 0 && tokenIndex < tokens.Count)
                    sb.Append(tokens[tokenIndex]);
                else
                    sb.Append(char.ConvertFromUtf32(cp));
            }
            return sb.ToString();
        }
    }
}
