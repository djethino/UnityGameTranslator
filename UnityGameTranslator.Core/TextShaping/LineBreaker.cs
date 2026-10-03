using System.Collections.Generic;
using System.Globalization;
using C = UnityGameTranslator.Core.TextShaping.ShapingTables.LineBreak;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Where a line may break — Unicode's Line Breaking Algorithm (UAX #14, the revision for the
    /// Unicode of ShapingTables), rule by rule in its order: LB1 to LB31, the default tailoring.
    ///
    /// 🔴 A transcription of the standard, checked against Unicode's own LineBreakTest.txt of the same
    /// version (LineBreakConformanceChecks), never a list of scripts: the line breaking it replaced
    /// knew spaces, Chinese and Japanese, and nothing else (2026-10-02). Its data — the Line_Break
    /// class of every code point, East Asian Width, Extended_Pictographic, the general category — is
    /// generated from the UCD with every other table.
    ///
    /// PURE by contract — linked into Core.Checks.
    /// </summary>
    internal static class LineBreaker
    {
        internal enum Break : byte { None, Allowed, Mandatory }

        /// <summary>
        /// The break at every position of <paramref name="cps"/>: element i is the break BEFORE code
        /// point i (element 0, the start of text, is None — LB2), element n the end (Mandatory — LB3).
        /// </summary>
        internal static Break[] Analyse(IList<int> cps)
        {
            int n = cps.Count;
            var breaks = new Break[n + 1];
            if (n == 0) return breaks;
            var raw = new int[n];
            for (int i = 0; i < n; i++) raw[i] = Resolved(cps[i]);

            // LB9 and LB10: X (CM | ZWJ)* is X; a CM or ZWJ with no such X is AL (with A's properties).
            var eff = new int[n];
            var baseOf = new int[n];
            var asLetterA = new bool[n];
            var absorbed = new bool[n];
            for (int i = 0; i < n; i++)
            {
                eff[i] = raw[i]; baseOf[i] = i;
                if (raw[i] != C.CM && raw[i] != C.ZWJ) continue;
                if (i > 0 && !IsLb9Excluded(eff[i - 1]))
                {
                    absorbed[i] = true; eff[i] = eff[i - 1]; baseOf[i] = baseOf[i - 1];
                }
                else { eff[i] = C.AL; asLetterA[i] = true; }
            }

            var ctx = new Context(cps, eff, baseOf, asLetterA, absorbed);
            for (int p = 1; p < n; p++) breaks[p] = At(ctx, raw, p);
            breaks[n] = Break.Mandatory;
            return breaks;
        }

        /// <summary>The same, over a string: (UTF-16 index, kind) of every break opportunity after its start.</summary>
        internal static List<KeyValuePair<int, Break>> Opportunities(string text)
        {
            var result = new List<KeyValuePair<int, Break>>();
            if (string.IsNullOrEmpty(text)) return result;
            var cps = new List<int>(text.Length);
            var starts = new List<int>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                starts.Add(i);
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    cps.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                    i++;
                }
                else cps.Add(text[i]);
            }
            var breaks = Analyse(cps);
            for (int p = 1; p <= cps.Count; p++)
                if (breaks[p] != Break.None) result.Add(new KeyValuePair<int, Break>(p < cps.Count ? starts[p] : text.Length, breaks[p]));
            return result;
        }

        /// <summary>
        /// Unicode's Line_Break SA — "complex context dependent": a script written without spaces
        /// between words, whose line breaks only a dictionary finds (Thai, Lao, Myanmar, Khmer…).
        /// </summary>
        internal static bool NeedsDictionary(int cp) => ClassOf(cp) == C.SA;

        // LB1: AI, SG, XX → AL; SA → CM when Mn or Mc, AL otherwise; CJ → NS.
        private static int Resolved(int cp)
        {
            int c = ClassOf(cp);
            if (c == C.AI || c == C.SG || c == C.XX) return C.AL;
            if (c == C.SA)
            {
                var g = UnicodeInfo.CategoryOf(cp);
                return g == UnicodeCategory.NonSpacingMark || g == UnicodeCategory.SpacingCombiningMark ? C.CM : C.AL;
            }
            if (c == C.CJ) return C.NS;
            return c;
        }

        private static bool IsLb9Excluded(int c) => c == C.BK || c == C.CR || c == C.LF || c == C.NL || c == C.SP || c == C.ZW;

        private sealed class Context
        {
            public readonly IList<int> Cps;
            public readonly int[] Eff, BaseOf;
            public readonly bool[] AsLetterA, Absorbed;
            public readonly int N;
            public Context(IList<int> cps, int[] eff, int[] baseOf, bool[] asLetterA, bool[] absorbed)
            { Cps = cps; Eff = eff; BaseOf = baseOf; AsLetterA = asLetterA; Absorbed = absorbed; N = cps.Count; }

            /// <summary>The item before the one at <paramref name="i"/>, -1 at the start (sot).</summary>
            public int Prev(int i) => BaseOf[i] - 1;
            /// <summary>The item after the one at <paramref name="i"/>, N at the end (eot).</summary>
            public int Next(int i) { int j = i + 1; while (j < N && Absorbed[j]) j++; return j; }
            public int Class(int i) => i < 0 || i >= N ? -1 : Eff[i];
            public UnicodeCategory Gc(int i) => AsLetterA[BaseOf[i]] ? UnicodeCategory.UppercaseLetter : UnicodeInfo.CategoryOf(Cps[BaseOf[i]]);
            public bool EastAsian(int i) => !AsLetterA[BaseOf[i]] && InRanges(ShapingTables.EastAsianWide, Cps[BaseOf[i]]);
            public bool Pictographic(int i) => !AsLetterA[BaseOf[i]] && InRanges(ShapingTables.ExtendedPictographic, Cps[BaseOf[i]]);
            public bool DottedCircle(int i) => i >= 0 && i < N && Cps[BaseOf[i]] == 0x25CC;
            /// <summary>Back from <paramref name="i"/> over SP items.</summary>
            public int SkipSpacesBack(int i) { while (i >= 0 && Eff[i] == C.SP) i = Prev(i); return i; }
        }

        private static bool Is(int c, params int[] set) { foreach (int s in set) if (c == s) return true; return false; }

        private static Break At(Context x, int[] raw, int p)
        {
            int a = p - 1, b = p;

            // LB4 – LB8a, on the characters themselves.
            if (raw[a] == C.BK) return Break.Mandatory;
            if (raw[a] == C.CR && raw[b] == C.LF) return Break.None;
            if (raw[a] == C.CR || raw[a] == C.LF || raw[a] == C.NL) return Break.Mandatory;
            if (Is(raw[b], C.BK, C.CR, C.LF, C.NL)) return Break.None;
            if (raw[b] == C.SP || raw[b] == C.ZW) return Break.None;
            int z = a;
            while (z >= 0 && raw[z] == C.SP) z--;
            if (z >= 0 && raw[z] == C.ZW) return Break.Allowed;
            if (raw[a] == C.ZWJ) return Break.None;
            // LB9: inside X (CM | ZWJ)*.
            if (x.Absorbed[b]) return Break.None;

            // From here, items: L the one before the position, R the one after it.
            int L = a, R = b;
            int l = x.Class(L), r = x.Class(R);

            // LB11, LB12, LB12a
            if (r == C.WJ || l == C.WJ) return Break.None;
            if (l == C.GL) return Break.None;
            if (r == C.GL && !Is(l, C.SP, C.HY, C.HH)) return Break.None;
            // LB13
            if (Is(r, C.CL, C.CP, C.EX, C.SY)) return Break.None;
            // LB14
            int s = x.SkipSpacesBack(L);
            if (x.Class(s) == C.OP) return Break.None;
            // LB15a
            if (x.Class(s) == C.QU && x.Gc(s) == UnicodeCategory.InitialQuotePunctuation)
            {
                int before = x.Prev(s);
                if (before < 0 || Is(x.Class(before), C.BK, C.CR, C.LF, C.NL, C.OP, C.QU, C.GL, C.SP, C.ZW)) return Break.None;
            }
            // LB15b
            if (r == C.QU && x.Gc(R) == UnicodeCategory.FinalQuotePunctuation)
            {
                int after = x.Next(R);
                if (after >= x.N || Is(x.Class(after), C.SP, C.GL, C.WJ, C.CL, C.QU, C.CP, C.EX, C.IS, C.SY,
                                       C.BK, C.CR, C.LF, C.NL, C.ZW)) return Break.None;
            }
            // LB15c, LB15d
            if (l == C.SP && r == C.IS && x.Class(x.Next(R)) == C.NU) return Break.Allowed;
            if (r == C.IS) return Break.None;
            // LB16, LB17
            if (Is(x.Class(s), C.CL, C.CP) && r == C.NS) return Break.None;
            if (x.Class(s) == C.B2 && r == C.B2) return Break.None;
            // LB18
            if (l == C.SP) return Break.Allowed;
            // LB19
            if (r == C.QU && x.Gc(R) != UnicodeCategory.InitialQuotePunctuation) return Break.None;
            if (l == C.QU && x.Gc(L) != UnicodeCategory.FinalQuotePunctuation) return Break.None;
            // LB19a
            if (r == C.QU && !x.EastAsian(L)) return Break.None;
            if (r == C.QU) { int after = x.Next(R); if (after >= x.N || !x.EastAsian(after)) return Break.None; }
            if (l == C.QU && !x.EastAsian(R)) return Break.None;
            if (l == C.QU) { int before = x.Prev(L); if (before < 0 || !x.EastAsian(before)) return Break.None; }
            // LB20
            if (r == C.CB || l == C.CB) return Break.Allowed;
            // LB20a
            if (Is(l, C.HY, C.HH) && Is(r, C.AL, C.HL))
            {
                int before = x.Prev(L);
                if (before < 0 || Is(x.Class(before), C.BK, C.CR, C.LF, C.NL, C.SP, C.ZW, C.CB, C.GL)) return Break.None;
            }
            // LB21
            if (Is(r, C.BA, C.HH, C.HY, C.NS)) return Break.None;
            if (l == C.BB) return Break.None;
            // LB21a, LB21b, LB22
            if (Is(l, C.HY, C.HH) && x.Class(x.Prev(L)) == C.HL && r != C.HL) return Break.None;
            if (l == C.SY && r == C.HL) return Break.None;
            if (r == C.IN) return Break.None;
            // LB23, LB23a, LB24
            if (Is(l, C.AL, C.HL) && r == C.NU) return Break.None;
            if (l == C.NU && Is(r, C.AL, C.HL)) return Break.None;
            if (l == C.PR && Is(r, C.ID, C.EB, C.EM)) return Break.None;
            if (Is(l, C.ID, C.EB, C.EM) && r == C.PO) return Break.None;
            if (Is(l, C.PR, C.PO) && Is(r, C.AL, C.HL)) return Break.None;
            if (Is(l, C.AL, C.HL) && Is(r, C.PR, C.PO)) return Break.None;
            // LB25
            if (Is(r, C.PO, C.PR))
            {
                int k = Is(l, C.CL, C.CP) ? x.Prev(L) : L;
                while (k >= 0 && Is(x.Class(k), C.SY, C.IS)) k = x.Prev(k);
                if (x.Class(k) == C.NU && (Is(l, C.CL, C.CP) || Is(l, C.NU, C.SY, C.IS))) return Break.None;
            }
            if (Is(l, C.PO, C.PR) && r == C.OP)
            {
                int n1 = x.Next(R);
                if (x.Class(n1) == C.NU) return Break.None;
                if (x.Class(n1) == C.IS && x.Class(x.Next(n1)) == C.NU) return Break.None;
            }
            if (Is(l, C.PO, C.PR, C.HY, C.IS) && r == C.NU) return Break.None;
            if (r == C.NU && Is(l, C.NU, C.SY, C.IS))
            {
                int k = L;
                while (k >= 0 && Is(x.Class(k), C.SY, C.IS)) k = x.Prev(k);
                if (x.Class(k) == C.NU) return Break.None;
            }
            // LB26, LB27
            if (l == C.JL && Is(r, C.JL, C.JV, C.H2, C.H3)) return Break.None;
            if (Is(l, C.JV, C.H2) && Is(r, C.JV, C.JT)) return Break.None;
            if (Is(l, C.JT, C.H3) && r == C.JT) return Break.None;
            if (Is(l, C.JL, C.JV, C.JT, C.H2, C.H3) && r == C.PO) return Break.None;
            if (l == C.PR && Is(r, C.JL, C.JV, C.JT, C.H2, C.H3)) return Break.None;
            // LB28
            if (Is(l, C.AL, C.HL) && Is(r, C.AL, C.HL)) return Break.None;
            // LB28a
            bool kl = Is(l, C.AK, C.AS) || x.DottedCircle(L), kr = Is(r, C.AK, C.AS) || x.DottedCircle(R);
            if (l == C.AP && kr) return Break.None;
            if (kl && Is(r, C.VF, C.VI)) return Break.None;
            if (l == C.VI && (r == C.AK || x.DottedCircle(R)))
            {
                int before = x.Prev(L);
                if (before >= 0 && (Is(x.Class(before), C.AK, C.AS) || x.DottedCircle(before))) return Break.None;
            }
            if (kl && kr && x.Class(x.Next(R)) == C.VF) return Break.None;
            // LB29
            if (l == C.IS && Is(r, C.AL, C.HL)) return Break.None;
            // LB30
            if (Is(l, C.AL, C.HL, C.NU) && r == C.OP && !x.EastAsian(R)) return Break.None;
            if (l == C.CP && !x.EastAsian(L) && Is(r, C.AL, C.HL, C.NU)) return Break.None;
            // LB30a
            if (l == C.RI && r == C.RI)
            {
                int count = 0, k = L;
                while (k >= 0 && x.Class(k) == C.RI) { count++; k = x.Prev(k); }
                if ((count & 1) == 1) return Break.None;
            }
            // LB30b
            if (r == C.EM && (l == C.EB || (x.Pictographic(L) && x.Gc(L) == UnicodeCategory.OtherNotAssigned))) return Break.None;
            // LB31
            return Break.Allowed;
        }

        private static int ClassOf(int cp)
        {
            var runs = ShapingTables.LineBreakClasses;
            int lo = 0, hi = runs.Length / 3 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (cp < runs[mid * 3]) hi = mid - 1;
                else if (cp > runs[mid * 3 + 1]) lo = mid + 1;
                else return runs[mid * 3 + 2];
            }
            return C.XX;
        }

        private static bool InRanges(int[] ranges, int cp) => UnicodeInfo.InRanges(ranges, cp);
    }
}
