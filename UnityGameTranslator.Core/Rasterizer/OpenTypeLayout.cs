using System;
using System.Collections.Generic;
using System.Text;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// The OpenType layout tables of a font — GDEF, GSUB, GPOS — and the generic machinery that
    /// applies their lookups to a run of glyphs. This is the layer every OpenType shaper stands
    /// on: it knows what a lookup DOES (replace, ligate, attach a mark to an anchor), never
    /// WHEN a script wants it applied — the feature order, the syllable model and the masks are
    /// the shaper's business (TextShaping/OpenTypeShaper).
    ///
    /// Read from the raw font bytes, big-endian, on demand; nothing here allocates per glyph at
    /// application time beyond the buffer itself. Subtable kinds that no script this mod shapes
    /// through this path needs are parsed as "unsupported" and never apply: cursive attachment
    /// (Arabic goes through presentation forms, not through here) and reverse chaining.
    ///
    /// PURE by contract (no Unity, no state beyond the parsed tables) — linked into Core.Checks,
    /// where it is exercised against a real font (Noto Sans Devanagari) with expectations read
    /// off the font by an independent tool.
    ///
    /// References: OpenType 1.9 — chapters "OpenType Layout Common Table Formats", "GDEF",
    /// "GSUB", "GPOS". Section names below quote the specification's table names.
    /// </summary>
    public sealed class OpenTypeLayout
    {
        // Lookup flags (LookupFlag bit enumeration).
        public const int FlagRightToLeft = 0x0001;
        public const int FlagIgnoreBaseGlyphs = 0x0002;
        public const int FlagIgnoreLigatures = 0x0004;
        public const int FlagIgnoreMarks = 0x0008;
        public const int FlagUseMarkFilteringSet = 0x0010;
        public const int FlagMarkAttachmentTypeMask = 0xFF00;

        // GDEF glyph classes.
        public const int ClassBase = 1;
        public const int ClassLigature = 2;
        public const int ClassMark = 3;
        public const int ClassComponent = 4;

        private readonly byte[] _d;

        /// <summary>GDEF glyph classes, or null when the font has none (the shaper then classes by Unicode).</summary>
        private readonly ClassDef _glyphClasses;
        private readonly ClassDef _markAttachClasses;
        private readonly Coverage[] _markGlyphSets;

        public bool HasGlyphClasses => _glyphClasses != null;

        /// <summary>The substitution table, or null when the font has no GSUB.</summary>
        public LayoutTable Gsub { get; }
        /// <summary>The positioning table, or null when the font has no GPOS.</summary>
        public LayoutTable Gpos { get; }

        /// <summary>
        /// Read the three tables. <paramref name="tables"/> answers a table tag with its offset
        /// and length in <paramref name="data"/>, or false when the font has no such table.
        /// </summary>
        public OpenTypeLayout(byte[] data, TryGetTableDelegate tables)
        {
            _d = data ?? throw new ArgumentNullException(nameof(data));
            if (tables == null) throw new ArgumentNullException(nameof(tables));

            if (tables("GDEF", out uint gdef, out _) && gdef > 0)
            {
                int major = U16(gdef), minor = U16(gdef + 2);
                int glyphClassDef = U16(gdef + 4);
                int markAttachClassDef = U16(gdef + 10);
                if (glyphClassDef != 0) _glyphClasses = ClassDef.Read(_d, gdef + (uint)glyphClassDef);
                if (markAttachClassDef != 0) _markAttachClasses = ClassDef.Read(_d, gdef + (uint)markAttachClassDef);
                if (major == 1 && minor >= 2)
                {
                    int markGlyphSetsDef = U16(gdef + 12);
                    if (markGlyphSetsDef != 0)
                    {
                        uint sets = gdef + (uint)markGlyphSetsDef;
                        int count = U16(sets + 2);
                        _markGlyphSets = new Coverage[count];
                        for (int i = 0; i < count; i++)
                            _markGlyphSets[i] = Coverage.Read(_d, sets + U32(sets + 4 + (uint)(i * 4)));
                    }
                }
            }
            if (tables("GSUB", out uint gsub, out _) && gsub > 0) Gsub = new LayoutTable(this, gsub, isGpos: false);
            if (tables("GPOS", out uint gpos, out _) && gpos > 0) Gpos = new LayoutTable(this, gpos, isGpos: true);
        }

        public delegate bool TryGetTableDelegate(string tag, out uint offset, out uint length);

        /// <summary>GDEF class of a glyph: 0 when unclassed or when the font has no GDEF.</summary>
        public int GlyphClass(int glyph) => _glyphClasses?.Class(glyph) ?? 0;
        public int MarkAttachClass(int glyph) => _markAttachClasses?.Class(glyph) ?? 0;

        // ───────────────────────────── binary reading ─────────────────────────────

        private int U16(uint o) => o + 1 < _d.Length ? (_d[o] << 8) | _d[o + 1] : 0;
        private short S16(uint o) => (short)U16(o);
        private uint U32(uint o) => o + 3 < _d.Length ? ((uint)_d[o] << 24) | ((uint)_d[o + 1] << 16) | ((uint)_d[o + 2] << 8) | _d[o + 3] : 0u;
        private string Tag(uint o) => o + 3 < _d.Length ? Encoding.ASCII.GetString(_d, (int)o, 4) : "????";

        // ───────────────────────────── common formats ─────────────────────────────

        /// <summary>Coverage table: which glyphs a subtable concerns, each with an index.</summary>
        public sealed class Coverage
        {
            private readonly int[] _glyphs;            // format 1: sorted glyph ids, index = position
            private readonly int[] _starts, _ends, _startIndices; // format 2: ranges

            private Coverage(int[] glyphs) { _glyphs = glyphs; }
            private Coverage(int[] starts, int[] ends, int[] startIndices) { _starts = starts; _ends = ends; _startIndices = startIndices; }

            public static Coverage Read(byte[] d, uint o)
            {
                int format = R16(d, o);
                if (format == 1)
                {
                    int count = R16(d, o + 2);
                    var glyphs = new int[count];
                    for (int i = 0; i < count; i++) glyphs[i] = R16(d, o + 4 + (uint)(i * 2));
                    return new Coverage(glyphs);
                }
                if (format == 2)
                {
                    int count = R16(d, o + 2);
                    var starts = new int[count]; var ends = new int[count]; var idx = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        uint r = o + 4 + (uint)(i * 6);
                        starts[i] = R16(d, r); ends[i] = R16(d, r + 2); idx[i] = R16(d, r + 4);
                    }
                    return new Coverage(starts, ends, idx);
                }
                return new Coverage(new int[0]);
            }

            /// <summary>Coverage index of a glyph, or -1 when it is not covered.</summary>
            public int Index(int glyph)
            {
                if (_glyphs != null)
                {
                    int i = Array.BinarySearch(_glyphs, glyph);
                    return i >= 0 ? i : -1;
                }
                int lo = 0, hi = _starts.Length - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (glyph < _starts[mid]) hi = mid - 1;
                    else if (glyph > _ends[mid]) lo = mid + 1;
                    else return _startIndices[mid] + (glyph - _starts[mid]);
                }
                return -1;
            }

            public bool Contains(int glyph) => Index(glyph) >= 0;

            /// <summary>Every covered glyph, in coverage-index order. Diagnostics and checks only.</summary>
            public IEnumerable<int> Glyphs()
            {
                if (_glyphs != null) { foreach (int g in _glyphs) yield return g; yield break; }
                for (int r = 0; r < _starts.Length; r++)
                    for (int g = _starts[r]; g <= _ends[r]; g++) yield return g;
            }
        }

        /// <summary>Class definition table: a class number per glyph, 0 when unlisted.</summary>
        public sealed class ClassDef
        {
            private readonly int _startGlyph; private readonly int[] _classes;      // format 1
            private readonly int[] _starts, _ends, _values;                          // format 2

            private ClassDef(int start, int[] classes) { _startGlyph = start; _classes = classes; }
            private ClassDef(int[] starts, int[] ends, int[] values) { _starts = starts; _ends = ends; _values = values; }

            public static ClassDef Read(byte[] d, uint o)
            {
                int format = R16(d, o);
                if (format == 1)
                {
                    int start = R16(d, o + 2), count = R16(d, o + 4);
                    var classes = new int[count];
                    for (int i = 0; i < count; i++) classes[i] = R16(d, o + 6 + (uint)(i * 2));
                    return new ClassDef(start, classes);
                }
                if (format == 2)
                {
                    int count = R16(d, o + 2);
                    var starts = new int[count]; var ends = new int[count]; var values = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        uint r = o + 4 + (uint)(i * 6);
                        starts[i] = R16(d, r); ends[i] = R16(d, r + 2); values[i] = R16(d, r + 4);
                    }
                    return new ClassDef(starts, ends, values);
                }
                return new ClassDef(0, new int[0]);
            }

            public int Class(int glyph)
            {
                if (_classes != null)
                {
                    int i = glyph - _startGlyph;
                    return i >= 0 && i < _classes.Length ? _classes[i] : 0;
                }
                int lo = 0, hi = _starts.Length - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (glyph < _starts[mid]) hi = mid - 1;
                    else if (glyph > _ends[mid]) lo = mid + 1;
                    else return _values[mid];
                }
                return 0;
            }
        }

        private static int R16(byte[] d, uint o) => o + 1 < d.Length ? (d[o] << 8) | d[o + 1] : 0;

        /// <summary>An anchor point in font units (formats 1, 2, 3 all start with x, y).</summary>
        public struct Anchor
        {
            public short X, Y;
            public bool IsSet;
            public static Anchor Read(byte[] d, uint o) => o == 0 ? default(Anchor)
                : new Anchor { X = (short)R16(d, o + 2), Y = (short)R16(d, o + 4), IsSet = true };
        }

        /// <summary>A GPOS value record: the placement and advance deltas a lookup adds.</summary>
        public struct ValueRecord
        {
            public short XPlacement, YPlacement, XAdvance, YAdvance;
            public bool IsEmpty => XPlacement == 0 && YPlacement == 0 && XAdvance == 0 && YAdvance == 0;

            public static int Size(int format)
            {
                int n = 0;
                for (int bit = 0; bit < 8; bit++) if ((format & (1 << bit)) != 0) n++;
                return n * 2;
            }

            public static ValueRecord Read(byte[] d, uint o, int format)
            {
                var v = new ValueRecord();
                uint p = o;
                if ((format & 0x01) != 0) { v.XPlacement = (short)R16(d, p); p += 2; }
                if ((format & 0x02) != 0) { v.YPlacement = (short)R16(d, p); p += 2; }
                if ((format & 0x04) != 0) { v.XAdvance = (short)R16(d, p); p += 2; }
                if ((format & 0x08) != 0) { v.YAdvance = (short)R16(d, p); p += 2; }
                // 0x10–0x80: device / variation index tables — the default instance is what our
                // rasterizer renders, so they are skipped on purpose.
                return v;
            }
        }

        // ───────────────────────────── scripts, features, lookups ─────────────────────────────

        public sealed class FeatureRecord
        {
            public string Tag;
            public int[] LookupIndices;
        }

        public sealed class LangSys
        {
            public string Tag;                 // "dflt" for the default language system
            public int RequiredFeature = -1;   // index into the feature list, -1 when none
            public int[] FeatureIndices;
        }

        public sealed class Script
        {
            public string Tag;
            public LangSys Default;
            public LangSys[] Languages;
        }

        public sealed class Lookup
        {
            public int Type;
            public int Flag;
            public int MarkFilteringSet = -1;
            public Subtable[] Subtables;
        }

        /// <summary>One GSUB or GPOS table: its scripts, features and lookups.</summary>
        public sealed class LayoutTable
        {
            private readonly OpenTypeLayout _owner;
            private readonly byte[] _d;
            public readonly bool IsGpos;
            public Script[] Scripts { get; }
            public FeatureRecord[] Features { get; }
            public Lookup[] Lookups { get; }

            internal LayoutTable(OpenTypeLayout owner, uint o, bool isGpos)
            {
                _owner = owner; _d = owner._d; IsGpos = isGpos;
                uint scriptList = o + (uint)owner.U16(o + 4);
                uint featureList = o + (uint)owner.U16(o + 6);
                uint lookupList = o + (uint)owner.U16(o + 8);

                // ScriptList
                int scriptCount = owner.U16(scriptList);
                Scripts = new Script[scriptCount];
                for (int i = 0; i < scriptCount; i++)
                {
                    uint rec = scriptList + 2 + (uint)(i * 6);
                    uint script = scriptList + (uint)owner.U16(rec + 4);
                    var s = new Script { Tag = owner.Tag(rec) };
                    int defaultLangSys = owner.U16(script);
                    if (defaultLangSys != 0) s.Default = ReadLangSys(script + (uint)defaultLangSys, "dflt");
                    int langCount = owner.U16(script + 2);
                    s.Languages = new LangSys[langCount];
                    for (int l = 0; l < langCount; l++)
                    {
                        uint lrec = script + 4 + (uint)(l * 6);
                        s.Languages[l] = ReadLangSys(script + (uint)owner.U16(lrec + 4), owner.Tag(lrec));
                    }
                    Scripts[i] = s;
                }

                // FeatureList
                int featureCount = owner.U16(featureList);
                Features = new FeatureRecord[featureCount];
                for (int i = 0; i < featureCount; i++)
                {
                    uint rec = featureList + 2 + (uint)(i * 6);
                    uint feature = featureList + (uint)owner.U16(rec + 4);
                    int lookupCount = owner.U16(feature + 2);
                    var indices = new int[lookupCount];
                    for (int l = 0; l < lookupCount; l++) indices[l] = owner.U16(feature + 4 + (uint)(l * 2));
                    Features[i] = new FeatureRecord { Tag = owner.Tag(rec), LookupIndices = indices };
                }

                // LookupList
                int lookupTotal = owner.U16(lookupList);
                Lookups = new Lookup[lookupTotal];
                for (int i = 0; i < lookupTotal; i++)
                {
                    uint lookup = lookupList + (uint)owner.U16(lookupList + 2 + (uint)(i * 2));
                    var lk = new Lookup { Type = owner.U16(lookup), Flag = owner.U16(lookup + 2) };
                    int subCount = owner.U16(lookup + 4);
                    if ((lk.Flag & FlagUseMarkFilteringSet) != 0)
                        lk.MarkFilteringSet = owner.U16(lookup + 6 + (uint)(subCount * 2));
                    lk.Subtables = new Subtable[subCount];
                    for (int s = 0; s < subCount; s++)
                    {
                        uint sub = lookup + (uint)owner.U16(lookup + 6 + (uint)(s * 2));
                        lk.Subtables[s] = owner.ReadSubtable(sub, lk.Type, isGpos);
                    }
                    Lookups[i] = lk;
                }
            }

            private LangSys ReadLangSys(uint o, string tag)
            {
                var ls = new LangSys { Tag = tag };
                int required = _owner.U16(o + 2);
                ls.RequiredFeature = required == 0xFFFF ? -1 : required;
                int count = _owner.U16(o + 4);
                ls.FeatureIndices = new int[count];
                for (int i = 0; i < count; i++) ls.FeatureIndices[i] = _owner.U16(o + 6 + (uint)(i * 2));
                return ls;
            }

            /// <summary>
            /// The features a script offers, as tag → lookup indices (sorted, duplicates merged).
            /// The first of <paramref name="scriptTags"/> the font carries wins; its default
            /// language system is used unless <paramref name="langTag"/> names another it has.
            /// Null when the font carries none of the scripts.
            /// </summary>
            public Dictionary<string, int[]> CollectFeatures(string[] scriptTags, string langTag, out string scriptUsed)
            {
                scriptUsed = null;
                foreach (string tag in scriptTags)
                {
                    foreach (var s in Scripts)
                    {
                        if (s.Tag != tag) continue;
                        LangSys ls = s.Default;
                        if (langTag != null)
                            foreach (var l in s.Languages) if (l.Tag == langTag) { ls = l; break; }
                        if (ls == null) continue;
                        scriptUsed = tag;
                        var result = new Dictionary<string, List<int>>();
                        void Add(int featureIndex)
                        {
                            if (featureIndex < 0 || featureIndex >= Features.Length) return;
                            var f = Features[featureIndex];
                            if (!result.TryGetValue(f.Tag, out var list)) result[f.Tag] = list = new List<int>();
                            foreach (int li in f.LookupIndices) if (!list.Contains(li)) list.Add(li);
                        }
                        Add(ls.RequiredFeature);
                        foreach (int fi in ls.FeatureIndices) Add(fi);
                        var final = new Dictionary<string, int[]>();
                        foreach (var kv in result) { kv.Value.Sort(); final[kv.Key] = kv.Value.ToArray(); }
                        return final;
                    }
                }
                return null;
            }
        }

        // ───────────────────────────── subtables ─────────────────────────────

        public abstract class Subtable
        {
            /// <summary>Lookup type as written in the font (after extension unwrapping).</summary>
            public int Type;
        }

        /// <summary>A subtable kind this engine does not apply (cursive, reverse chain, unknown formats).</summary>
        public sealed class UnsupportedSubtable : Subtable { public string Why; }

        public sealed class SingleSubst : Subtable
        {
            public Coverage Coverage; public int Delta; public int[] Substitutes; // one of the two
            public int Substitute(int glyph)
            {
                int i = Coverage.Index(glyph);
                if (i < 0) return -1;
                if (Substitutes != null) return i < Substitutes.Length ? Substitutes[i] : -1;
                return (glyph + Delta) & 0xFFFF;
            }
        }

        public sealed class MultipleSubst : Subtable { public Coverage Coverage; public int[][] Sequences; }
        public sealed class AlternateSubst : Subtable { public Coverage Coverage; public int[][] Alternates; }

        public sealed class Ligature { public int Glyph; public int[] Components; /* after the first */ }
        public sealed class LigatureSubst : Subtable { public Coverage Coverage; public Ligature[][] Sets; }

        public sealed class SequenceLookup { public int SequenceIndex; public int LookupIndex; }

        /// <summary>
        /// One rule of a (chained) contextual subtable, whatever its format: the input sequence
        /// after its first item, the backtrack (nearest first) and lookahead, each as glyph ids
        /// (format 1), class numbers (format 2) or coverage tables (format 3).
        /// </summary>
        public sealed class ContextRule
        {
            public int[] Backtrack, Input, Lookahead;              // formats 1 and 2 (Input excludes the first glyph)
            public Coverage[] BacktrackCov, InputCov, LookaheadCov; // format 3 (InputCov includes the first)
            public SequenceLookup[] Lookups;
        }

        public sealed class ContextSubst : Subtable
        {
            public int Format;
            public Coverage Coverage;                              // formats 1, 2 — the first input glyph
            public ClassDef BacktrackClasses, InputClasses, LookaheadClasses; // format 2 (chained: three; plain: Input only)
            public ContextRule[][] RuleSets;                       // formats 1, 2: by coverage index / class
            public ContextRule Rule3;                              // format 3: one rule
            public bool Chained;
        }

        public sealed class SinglePos : Subtable
        {
            public Coverage Coverage; public ValueRecord[] Values; // one entry (format 1) or one per covered glyph
            public bool TryGet(int glyph, out ValueRecord v)
            {
                int i = Coverage.Index(glyph);
                if (i < 0) { v = default(ValueRecord); return false; }
                v = Values.Length == 1 ? Values[0] : (i < Values.Length ? Values[i] : default(ValueRecord));
                return true;
            }
        }

        public sealed class PairValue { public int SecondGlyph; public ValueRecord First, Second; }
        public sealed class PairPos : Subtable
        {
            public int Format;
            public Coverage Coverage;
            public PairValue[][] PairSets;                 // format 1, by coverage index, sorted by SecondGlyph
            public ClassDef Class1, Class2; public ValueRecord[,] First, Second; public int Class1Count, Class2Count; // format 2
        }

        public sealed class MarkRecord { public int Class; public Anchor Anchor; }
        public sealed class MarkBasePos : Subtable
        {
            public Coverage MarkCoverage, BaseCoverage; public int ClassCount;
            public MarkRecord[] Marks;           // by mark coverage index
            public Anchor[][] BaseAnchors;       // [base coverage index][mark class]
        }
        public sealed class MarkLigPos : Subtable
        {
            public Coverage MarkCoverage, LigatureCoverage; public int ClassCount;
            public MarkRecord[] Marks;
            public Anchor[][][] LigatureAnchors; // [ligature coverage index][component][mark class]
        }
        public sealed class MarkMarkPos : Subtable
        {
            public Coverage Mark1Coverage, Mark2Coverage; public int ClassCount;
            public MarkRecord[] Marks1;
            public Anchor[][] Mark2Anchors;      // [mark2 coverage index][mark class]
        }

        private Subtable ReadSubtable(uint o, int type, bool isGpos)
        {
            // Extension (GSUB 7 / GPOS 9): the real subtable sits behind a 32-bit offset.
            if ((!isGpos && type == 7) || (isGpos && type == 9))
            {
                int realType = U16(o + 2);
                uint target = o + U32(o + 4);
                return ReadSubtable(target, realType, isGpos);
            }
            Subtable st = isGpos ? ReadGposSubtable(o, type) : ReadGsubSubtable(o, type);
            st.Type = type;
            return st;
        }

        private Subtable ReadGsubSubtable(uint o, int type)
        {
            int format = U16(o);
            switch (type)
            {
                case 1:
                {
                    var st = new SingleSubst { Coverage = Coverage.Read(_d, o + (uint)U16(o + 2)) };
                    if (format == 1) st.Delta = S16(o + 4);
                    else
                    {
                        int count = U16(o + 4);
                        st.Substitutes = new int[count];
                        for (int i = 0; i < count; i++) st.Substitutes[i] = U16(o + 6 + (uint)(i * 2));
                    }
                    return st;
                }
                case 2:
                case 3:
                {
                    var cov = Coverage.Read(_d, o + (uint)U16(o + 2));
                    int count = U16(o + 4);
                    var seqs = new int[count][];
                    for (int i = 0; i < count; i++)
                    {
                        uint seq = o + (uint)U16(o + 6 + (uint)(i * 2));
                        int n = U16(seq);
                        seqs[i] = new int[n];
                        for (int k = 0; k < n; k++) seqs[i][k] = U16(seq + 2 + (uint)(k * 2));
                    }
                    if (type == 2) return new MultipleSubst { Coverage = cov, Sequences = seqs };
                    return new AlternateSubst { Coverage = cov, Alternates = seqs };
                }
                case 4:
                {
                    var st = new LigatureSubst { Coverage = Coverage.Read(_d, o + (uint)U16(o + 2)) };
                    int setCount = U16(o + 4);
                    st.Sets = new Ligature[setCount][];
                    for (int i = 0; i < setCount; i++)
                    {
                        uint set = o + (uint)U16(o + 6 + (uint)(i * 2));
                        int ligCount = U16(set);
                        var ligs = new Ligature[ligCount];
                        for (int l = 0; l < ligCount; l++)
                        {
                            uint lig = set + (uint)U16(set + 2 + (uint)(l * 2));
                            int compCount = U16(lig + 2);
                            var comps = new int[Math.Max(0, compCount - 1)];
                            for (int c = 0; c < comps.Length; c++) comps[c] = U16(lig + 4 + (uint)(c * 2));
                            ligs[l] = new Ligature { Glyph = U16(lig), Components = comps };
                        }
                        st.Sets[i] = ligs;
                    }
                    return st;
                }
                case 5: return ReadContext(o, chained: false);
                case 6: return ReadContext(o, chained: true);
                default: return new UnsupportedSubtable { Why = "GSUB lookup type " + type };
            }
        }

        private Subtable ReadGposSubtable(uint o, int type)
        {
            int format = U16(o);
            switch (type)
            {
                case 1:
                {
                    var st = new SinglePos { Coverage = Coverage.Read(_d, o + (uint)U16(o + 2)) };
                    int vf = U16(o + 4);
                    if (format == 1) st.Values = new[] { ValueRecord.Read(_d, o + 6, vf) };
                    else
                    {
                        int count = U16(o + 6);
                        st.Values = new ValueRecord[count];
                        int size = ValueRecord.Size(vf);
                        for (int i = 0; i < count; i++) st.Values[i] = ValueRecord.Read(_d, o + 8 + (uint)(i * size), vf);
                    }
                    return st;
                }
                case 2:
                {
                    var st = new PairPos { Format = format, Coverage = Coverage.Read(_d, o + (uint)U16(o + 2)) };
                    int vf1 = U16(o + 4), vf2 = U16(o + 6);
                    int size1 = ValueRecord.Size(vf1), size2 = ValueRecord.Size(vf2);
                    if (format == 1)
                    {
                        int setCount = U16(o + 8);
                        st.PairSets = new PairValue[setCount][];
                        for (int i = 0; i < setCount; i++)
                        {
                            uint set = o + (uint)U16(o + 10 + (uint)(i * 2));
                            int n = U16(set);
                            var pairs = new PairValue[n];
                            uint p = set + 2;
                            for (int k = 0; k < n; k++)
                            {
                                pairs[k] = new PairValue
                                {
                                    SecondGlyph = U16(p),
                                    First = ValueRecord.Read(_d, p + 2, vf1),
                                    Second = ValueRecord.Read(_d, p + 2 + (uint)size1, vf2)
                                };
                                p += (uint)(2 + size1 + size2);
                            }
                            st.PairSets[i] = pairs;
                        }
                    }
                    else if (format == 2)
                    {
                        st.Class1 = ClassDef.Read(_d, o + (uint)U16(o + 8));
                        st.Class2 = ClassDef.Read(_d, o + (uint)U16(o + 10));
                        st.Class1Count = U16(o + 12); st.Class2Count = U16(o + 14);
                        st.First = new ValueRecord[st.Class1Count, st.Class2Count];
                        st.Second = new ValueRecord[st.Class1Count, st.Class2Count];
                        uint p = o + 16;
                        for (int c1 = 0; c1 < st.Class1Count; c1++)
                            for (int c2 = 0; c2 < st.Class2Count; c2++)
                            {
                                st.First[c1, c2] = ValueRecord.Read(_d, p, vf1);
                                st.Second[c1, c2] = ValueRecord.Read(_d, p + (uint)size1, vf2);
                                p += (uint)(size1 + size2);
                            }
                    }
                    else return new UnsupportedSubtable { Why = "PairPos format " + format };
                    return st;
                }
                case 4:
                {
                    var st = new MarkBasePos
                    {
                        MarkCoverage = Coverage.Read(_d, o + (uint)U16(o + 2)),
                        BaseCoverage = Coverage.Read(_d, o + (uint)U16(o + 4)),
                        ClassCount = U16(o + 6)
                    };
                    st.Marks = ReadMarkArray(o + (uint)U16(o + 8));
                    uint baseArray = o + (uint)U16(o + 10);
                    int baseCount = U16(baseArray);
                    st.BaseAnchors = new Anchor[baseCount][];
                    for (int b = 0; b < baseCount; b++)
                    {
                        st.BaseAnchors[b] = new Anchor[st.ClassCount];
                        for (int c = 0; c < st.ClassCount; c++)
                        {
                            int off = U16(baseArray + 2 + (uint)((b * st.ClassCount + c) * 2));
                            st.BaseAnchors[b][c] = off == 0 ? default(Anchor) : Anchor.Read(_d, baseArray + (uint)off);
                        }
                    }
                    return st;
                }
                case 5:
                {
                    var st = new MarkLigPos
                    {
                        MarkCoverage = Coverage.Read(_d, o + (uint)U16(o + 2)),
                        LigatureCoverage = Coverage.Read(_d, o + (uint)U16(o + 4)),
                        ClassCount = U16(o + 6)
                    };
                    st.Marks = ReadMarkArray(o + (uint)U16(o + 8));
                    uint ligArray = o + (uint)U16(o + 10);
                    int ligCount = U16(ligArray);
                    st.LigatureAnchors = new Anchor[ligCount][][];
                    for (int l = 0; l < ligCount; l++)
                    {
                        uint attach = ligArray + (uint)U16(ligArray + 2 + (uint)(l * 2));
                        int compCount = U16(attach);
                        var comps = new Anchor[compCount][];
                        for (int c = 0; c < compCount; c++)
                        {
                            comps[c] = new Anchor[st.ClassCount];
                            for (int k = 0; k < st.ClassCount; k++)
                            {
                                int off = U16(attach + 2 + (uint)((c * st.ClassCount + k) * 2));
                                comps[c][k] = off == 0 ? default(Anchor) : Anchor.Read(_d, attach + (uint)off);
                            }
                        }
                        st.LigatureAnchors[l] = comps;
                    }
                    return st;
                }
                case 6:
                {
                    var st = new MarkMarkPos
                    {
                        Mark1Coverage = Coverage.Read(_d, o + (uint)U16(o + 2)),
                        Mark2Coverage = Coverage.Read(_d, o + (uint)U16(o + 4)),
                        ClassCount = U16(o + 6)
                    };
                    st.Marks1 = ReadMarkArray(o + (uint)U16(o + 8));
                    uint mark2Array = o + (uint)U16(o + 10);
                    int count = U16(mark2Array);
                    st.Mark2Anchors = new Anchor[count][];
                    for (int m = 0; m < count; m++)
                    {
                        st.Mark2Anchors[m] = new Anchor[st.ClassCount];
                        for (int c = 0; c < st.ClassCount; c++)
                        {
                            int off = U16(mark2Array + 2 + (uint)((m * st.ClassCount + c) * 2));
                            st.Mark2Anchors[m][c] = off == 0 ? default(Anchor) : Anchor.Read(_d, mark2Array + (uint)off);
                        }
                    }
                    return st;
                }
                case 7: return ReadContext(o, chained: false);
                case 8: return ReadContext(o, chained: true);
                default: return new UnsupportedSubtable { Why = "GPOS lookup type " + type };
            }
        }

        private MarkRecord[] ReadMarkArray(uint o)
        {
            int count = U16(o);
            var marks = new MarkRecord[count];
            for (int i = 0; i < count; i++)
            {
                uint rec = o + 2 + (uint)(i * 4);
                int anchorOff = U16(rec + 2);
                marks[i] = new MarkRecord { Class = U16(rec), Anchor = anchorOff == 0 ? default(Anchor) : Anchor.Read(_d, o + (uint)anchorOff) };
            }
            return marks;
        }

        private SequenceLookup[] ReadSequenceLookups(uint o, int count)
        {
            var records = new SequenceLookup[count];
            for (int i = 0; i < count; i++)
                records[i] = new SequenceLookup { SequenceIndex = U16(o + (uint)(i * 4)), LookupIndex = U16(o + (uint)(i * 4 + 2)) };
            return records;
        }

        private int[] ReadU16Array(uint o, int count)
        {
            var arr = new int[count];
            for (int i = 0; i < count; i++) arr[i] = U16(o + (uint)(i * 2));
            return arr;
        }

        private Coverage[] ReadCoverages(uint baseOffset, uint o, int count)
        {
            var arr = new Coverage[count];
            for (int i = 0; i < count; i++) arr[i] = Coverage.Read(_d, baseOffset + (uint)U16(o + (uint)(i * 2)));
            return arr;
        }

        /// <summary>Sequence context (GSUB 5 / GPOS 7) and chained (GSUB 6 / GPOS 8), formats 1, 2, 3.</summary>
        private Subtable ReadContext(uint o, bool chained)
        {
            int format = U16(o);
            var st = new ContextSubst { Format = format, Chained = chained };
            if (format == 1 || format == 2)
            {
                st.Coverage = Coverage.Read(_d, o + (uint)U16(o + 2));
                uint p = o + 4;
                if (format == 2)
                {
                    if (chained)
                    {
                        st.BacktrackClasses = ClassDef.Read(_d, o + (uint)U16(p));
                        st.InputClasses = ClassDef.Read(_d, o + (uint)U16(p + 2));
                        st.LookaheadClasses = ClassDef.Read(_d, o + (uint)U16(p + 4));
                        p += 6;
                    }
                    else
                    {
                        st.InputClasses = ClassDef.Read(_d, o + (uint)U16(p));
                        p += 2;
                    }
                }
                int setCount = U16(p);
                st.RuleSets = new ContextRule[setCount][];
                for (int s = 0; s < setCount; s++)
                {
                    int setOff = U16(p + 2 + (uint)(s * 2));
                    if (setOff == 0) { st.RuleSets[s] = new ContextRule[0]; continue; }
                    uint set = o + (uint)setOff;
                    int ruleCount = U16(set);
                    var rules = new ContextRule[ruleCount];
                    for (int r = 0; r < ruleCount; r++)
                    {
                        uint rule = set + (uint)U16(set + 2 + (uint)(r * 2));
                        var cr = new ContextRule();
                        uint q = rule;
                        if (chained)
                        {
                            int backCount = U16(q); q += 2;
                            cr.Backtrack = ReadU16Array(q, backCount); q += (uint)(backCount * 2);
                            int inputCount = U16(q); q += 2;
                            cr.Input = ReadU16Array(q, Math.Max(0, inputCount - 1)); q += (uint)(Math.Max(0, inputCount - 1) * 2);
                            int aheadCount = U16(q); q += 2;
                            cr.Lookahead = ReadU16Array(q, aheadCount); q += (uint)(aheadCount * 2);
                            int lookupCount = U16(q); q += 2;
                            cr.Lookups = ReadSequenceLookups(q, lookupCount);
                        }
                        else
                        {
                            int inputCount = U16(q); int lookupCount = U16(q + 2); q += 4;
                            cr.Input = ReadU16Array(q, Math.Max(0, inputCount - 1)); q += (uint)(Math.Max(0, inputCount - 1) * 2);
                            cr.Backtrack = new int[0]; cr.Lookahead = new int[0];
                            cr.Lookups = ReadSequenceLookups(q, lookupCount);
                        }
                        rules[r] = cr;
                    }
                    st.RuleSets[s] = rules;
                }
                return st;
            }
            if (format == 3)
            {
                var cr = new ContextRule();
                uint q = o + 2;
                if (chained)
                {
                    int backCount = U16(q); q += 2;
                    cr.BacktrackCov = ReadCoverages(o, q, backCount); q += (uint)(backCount * 2);
                    int inputCount = U16(q); q += 2;
                    cr.InputCov = ReadCoverages(o, q, inputCount); q += (uint)(inputCount * 2);
                    int aheadCount = U16(q); q += 2;
                    cr.LookaheadCov = ReadCoverages(o, q, aheadCount); q += (uint)(aheadCount * 2);
                    int lookupCount = U16(q); q += 2;
                    cr.Lookups = ReadSequenceLookups(q, lookupCount);
                }
                else
                {
                    int inputCount = U16(q); int lookupCount = U16(q + 2); q += 4;
                    cr.InputCov = ReadCoverages(o, q, inputCount); q += (uint)(inputCount * 2);
                    cr.BacktrackCov = new Coverage[0]; cr.LookaheadCov = new Coverage[0];
                    cr.Lookups = ReadSequenceLookups(q, lookupCount);
                }
                st.Rule3 = cr;
                return st;
            }
            return new UnsupportedSubtable { Why = (chained ? "chained " : "") + "context format " + format };
        }

        // ───────────────────────────── the glyph buffer ─────────────────────────────

        /// <summary>One glyph in a run being shaped, with what positioning has decided for it.</summary>
        public sealed class ShapedGlyph
        {
            public int Glyph;
            /// <summary>Index of the codepoint this glyph came from (the first one, for a ligature).</summary>
            public int Cluster;
            /// <summary>Feature masks the shaper set — a lookup applies only where its mask bit is set.</summary>
            public uint Mask = uint.MaxValue;
            /// <summary>True for a combining mark when the font has no GDEF (Unicode category, set by the shaper).</summary>
            public bool UnicodeMark;

            public int XOffset, YOffset;      // placement, font units
            public int XAdvance;              // advance, font units — starts at the font's, GPOS adjusts it
            public int AttachedTo = -1;       // index of the glyph this mark is anchored to, -1 when free
            public int AttachX, AttachY;      // anchor difference (base anchor − mark anchor), resolved at the end

            public int LigatureId;            // 0 = not from a ligature
            public int LigatureComponent;     // 1-based component a mark followed inside a ligature; 0 = none

            /// <summary>
            /// Syllable number the shaper assigned (0 = none). Matching never crosses from one
            /// syllable into another: a ligature or a context stops at the boundary, as it does
            /// in every OpenType shaper, so a font's rules only see the cluster they were written for.
            /// </summary>
            public int Syllable;

            /// <summary>What substitution did to this glyph — the shaper's reordering reads these.</summary>
            public bool Substituted, Ligated, Multiplied;

            /// <summary>Two slots for the shaper's own classification (category, position).</summary>
            public int Category, Position;

            public ShapedGlyph Clone() => (ShapedGlyph)MemberwiseClone();
        }

        /// <summary>The run: a list the lookups rewrite in place.</summary>
        public sealed class GlyphBuffer
        {
            public readonly List<ShapedGlyph> Glyphs = new List<ShapedGlyph>();
            private int _nextLigatureId;
            public int Count => Glyphs.Count;
            public ShapedGlyph this[int i] => Glyphs[i];
            internal int NewLigatureId() => ++_nextLigatureId;

            /// <summary>
            /// Turn every attachment into final offsets: a mark sits at its base's origin plus the
            /// anchor difference, so the advances between the two are subtracted from its
            /// placement and the base's own placement is inherited. Call once, after GPOS.
            /// </summary>
            public void ResolveAttachments()
            {
                for (int i = 0; i < Glyphs.Count; i++)
                {
                    var g = Glyphs[i];
                    if (g.AttachedTo < 0) continue;
                    // Follow the chain base-ward first so a mark on a mark inherits a resolved parent.
                    Resolve(i, 0);
                }
            }

            private void Resolve(int i, int depth)
            {
                var g = Glyphs[i];
                int j = g.AttachedTo;
                if (j < 0 || j >= Glyphs.Count || depth > 8) return;
                var parent = Glyphs[j];
                if (parent.AttachedTo >= 0) Resolve(j, depth + 1);
                int x = g.AttachX + parent.XOffset;
                int y = g.AttachY + parent.YOffset;
                if (j < i) for (int k = j; k < i; k++) x -= Glyphs[k].XAdvance;
                else for (int k = i; k < j; k++) x += Glyphs[k].XAdvance;
                g.XOffset = x;
                g.YOffset = y;
                g.AttachedTo = -1;
            }
        }

        // ───────────────────────────── application ─────────────────────────────

        private int ClassOf(ShapedGlyph g)
        {
            if (_glyphClasses != null) return _glyphClasses.Class(g.Glyph);
            return g.UnicodeMark ? ClassMark : ClassBase;
        }

        /// <summary>Whether a lookup's flags say to skip this glyph when matching and attaching.</summary>
        private bool IsIgnored(Lookup lookup, ShapedGlyph g)
        {
            int cls = ClassOf(g);
            int flag = lookup.Flag;
            if ((flag & FlagIgnoreBaseGlyphs) != 0 && cls == ClassBase) return true;
            if ((flag & FlagIgnoreLigatures) != 0 && cls == ClassLigature) return true;
            if (cls == ClassMark)
            {
                if ((flag & FlagIgnoreMarks) != 0) return true;
                if ((flag & FlagUseMarkFilteringSet) != 0)
                {
                    if (_markGlyphSets == null || lookup.MarkFilteringSet < 0 || lookup.MarkFilteringSet >= _markGlyphSets.Length) return true;
                    return !_markGlyphSets[lookup.MarkFilteringSet].Contains(g.Glyph);
                }
                int attachType = (flag & FlagMarkAttachmentTypeMask) >> 8;
                if (attachType != 0 && MarkAttachClass(g.Glyph) != attachType) return true;
            }
            return false;
        }

        private static bool SameSyllable(ShapedGlyph a, ShapedGlyph b) => a.Syllable == 0 || b.Syllable == 0 || a.Syllable == b.Syllable;

        private int Next(Lookup lookup, GlyphBuffer buf, int from, uint mask)
        {
            var origin = buf[from];
            for (int i = from + 1; i < buf.Count; i++)
            {
                var g = buf[i];
                if (!SameSyllable(origin, g)) return -1;
                if (IsIgnored(lookup, g)) continue;
                return i;
            }
            return -1;
        }

        private int Prev(Lookup lookup, GlyphBuffer buf, int from)
        {
            var origin = buf[from];
            for (int i = from - 1; i >= 0; i--)
            {
                if (!SameSyllable(origin, buf[i])) return -1;
                if (IsIgnored(lookup, buf[i])) continue;
                return i;
            }
            return -1;
        }

        /// <summary>
        /// Would any of these lookups rewrite exactly this glyph sequence? Flags are ignored;
        /// a contextual rule counts when its input is the whole sequence — and, with
        /// <paramref name="zeroContext"/>, only if it requires nothing before or after
        /// (HarfBuzz's would_apply). The question a shaper asks a font before reordering
        /// ("does this consonant have a below-base form?"), not an application.
        /// </summary>
        public bool WouldSubstitute(LayoutTable table, int[] lookupIndices, int[] glyphs, bool zeroContext = true)
        {
            if (table == null || lookupIndices == null || glyphs == null || glyphs.Length == 0) return false;
            foreach (int li in lookupIndices)
            {
                if (li < 0 || li >= table.Lookups.Length) continue;
                foreach (var st in table.Lookups[li].Subtables)
                    if (WouldApply(st, glyphs, zeroContext)) return true;
            }
            return false;
        }

        private static bool WouldApply(Subtable st, int[] glyphs, bool zeroContext)
        {
            switch (st)
            {
                case SingleSubst s: return glyphs.Length == 1 && s.Coverage.Contains(glyphs[0]);
                case MultipleSubst m: return glyphs.Length == 1 && m.Coverage.Contains(glyphs[0]);
                case AlternateSubst a: return glyphs.Length == 1 && a.Coverage.Contains(glyphs[0]);
                case LigatureSubst l:
                {
                    if (glyphs.Length < 2) return false;
                    int ci = l.Coverage.Index(glyphs[0]);
                    if (ci < 0 || ci >= l.Sets.Length) return false;
                    foreach (var lig in l.Sets[ci])
                    {
                        if (lig.Components.Length != glyphs.Length - 1) continue;
                        bool same = true;
                        for (int k = 0; k < lig.Components.Length && same; k++) same = lig.Components[k] == glyphs[k + 1];
                        if (same) return true;
                    }
                    return false;
                }
                case ContextSubst c:
                {
                    if (c.Format == 3)
                    {
                        var r = c.Rule3;
                        if (zeroContext && (r.BacktrackCov.Length != 0 || r.LookaheadCov.Length != 0)) return false;
                        if (r.InputCov.Length != glyphs.Length) return false;
                        for (int k = 0; k < glyphs.Length; k++) if (!r.InputCov[k].Contains(glyphs[k])) return false;
                        return true;
                    }
                    int first = c.Coverage.Index(glyphs[0]);
                    if (first < 0) return false;
                    int set = c.Format == 1 ? first : c.InputClasses.Class(glyphs[0]);
                    if (set < 0 || set >= c.RuleSets.Length) return false;
                    foreach (var rule in c.RuleSets[set])
                    {
                        if (zeroContext && (rule.Backtrack.Length != 0 || rule.Lookahead.Length != 0)) continue;
                        if (rule.Input.Length != glyphs.Length - 1) continue;
                        bool same = true;
                        for (int k = 0; k < rule.Input.Length && same; k++)
                            same = c.Format == 1 ? rule.Input[k] == glyphs[k + 1] : c.InputClasses.Class(glyphs[k + 1]) == rule.Input[k];
                        if (same) return true;
                    }
                    return false;
                }
                default: return false;
            }
        }

        /// <summary>
        /// Apply one lookup over the whole buffer, left to right, wherever the glyph's mask
        /// carries <paramref name="mask"/>. Returns true when anything changed.
        /// </summary>
        public bool ApplyLookup(LayoutTable table, int lookupIndex, GlyphBuffer buf, uint mask = uint.MaxValue)
        {
            if (table == null || lookupIndex < 0 || lookupIndex >= table.Lookups.Length) return false;
            var lookup = table.Lookups[lookupIndex];
            bool any = false;
            for (int i = 0; i < buf.Count; i++)
            {
                var g = buf[i];
                if ((g.Mask & mask) == 0 || IsIgnored(lookup, g)) continue;
                int advance = ApplyAt(table, lookup, buf, i, mask, 0);
                if (advance > 0) { any = true; i += advance - 1; }
            }
            return any;
        }

        /// <summary>
        /// Try the lookup's subtables at one position. Returns how many positions the cursor
        /// should move forward when one applied (≥ 1), 0 when none did.
        /// </summary>
        private int ApplyAt(LayoutTable table, Lookup lookup, GlyphBuffer buf, int i, uint mask, int depth)
        {
            if (depth > 6) return 0; // nested contexts calling each other — the spec forbids cycles, fonts do not always
            foreach (var st in lookup.Subtables)
            {
                int r = table.IsGpos ? ApplyGpos(table, lookup, st, buf, i, mask, depth) : ApplyGsub(table, lookup, st, buf, i, mask, depth);
                if (r > 0) return r;
            }
            return 0;
        }

        private int ApplyGsub(LayoutTable table, Lookup lookup, Subtable st, GlyphBuffer buf, int i, uint mask, int depth)
        {
            var g = buf[i];
            switch (st)
            {
                case SingleSubst s:
                {
                    int sub = s.Substitute(g.Glyph);
                    if (sub < 0) return 0;
                    g.Glyph = sub;
                    g.Substituted = true;
                    return 1;
                }
                case MultipleSubst m:
                {
                    int ci = m.Coverage.Index(g.Glyph);
                    if (ci < 0 || ci >= m.Sequences.Length) return 0;
                    var seq = m.Sequences[ci];
                    if (seq.Length == 0) { buf.Glyphs.RemoveAt(i); return 1; }
                    g.Glyph = seq[0];
                    g.Substituted = true;
                    g.Multiplied = seq.Length > 1;
                    for (int k = 1; k < seq.Length; k++)
                    {
                        var copy = g.Clone();
                        copy.Glyph = seq[k];
                        buf.Glyphs.Insert(i + k, copy);
                    }
                    return seq.Length;
                }
                case AlternateSubst a:
                {
                    int ci = a.Coverage.Index(g.Glyph);
                    if (ci < 0 || ci >= a.Alternates.Length || a.Alternates[ci].Length == 0) return 0;
                    g.Glyph = a.Alternates[ci][0];
                    g.Substituted = true;
                    return 1;
                }
                case LigatureSubst l:
                {
                    int ci = l.Coverage.Index(g.Glyph);
                    if (ci < 0 || ci >= l.Sets.Length) return 0;
                    foreach (var lig in l.Sets[ci])
                    {
                        // Match the components on the following non-ignored glyphs.
                        var matched = new List<int>(lig.Components.Length);
                        int pos = i;
                        bool ok = true;
                        foreach (int comp in lig.Components)
                        {
                            pos = Next(lookup, buf, pos, mask);
                            if (pos < 0 || buf[pos].Glyph != comp || (buf[pos].Mask & mask) == 0) { ok = false; break; }
                            matched.Add(pos);
                        }
                        if (!ok) continue;
                        // Replace: the first glyph becomes the ligature; skipped glyphs (marks) between
                        // components stay, remembering which component they followed; components go.
                        int ligId = buf.NewLigatureId();
                        g.Glyph = lig.Glyph;
                        g.LigatureId = ligId;
                        g.LigatureComponent = 0;
                        g.Substituted = true;
                        g.Ligated = true;
                        g.Multiplied = false;
                        int component = 1;
                        int last = matched.Count > 0 ? matched[matched.Count - 1] : i;
                        for (int k = i + 1; k <= last; k++)
                        {
                            if (matched.Contains(k)) { component++; continue; }
                            buf[k].LigatureId = ligId;
                            buf[k].LigatureComponent = component;
                        }
                        for (int m = matched.Count - 1; m >= 0; m--) buf.Glyphs.RemoveAt(matched[m]);
                        return 1;
                    }
                    return 0;
                }
                case ContextSubst c:
                    return ApplyContext(table, lookup, c, buf, i, mask, depth);
                default:
                    return 0;
            }
        }

        private int ApplyGpos(LayoutTable table, Lookup lookup, Subtable st, GlyphBuffer buf, int i, uint mask, int depth)
        {
            var g = buf[i];
            switch (st)
            {
                case SinglePos s:
                {
                    if (!s.TryGet(g.Glyph, out var v)) return 0;
                    Add(g, v);
                    return 1;
                }
                case PairPos p:
                {
                    int ci = p.Coverage.Index(g.Glyph);
                    if (ci < 0) return 0;
                    int j = Next(lookup, buf, i, mask);
                    if (j < 0) return 0;
                    var second = buf[j];
                    if (p.Format == 1)
                    {
                        if (ci >= p.PairSets.Length) return 0;
                        foreach (var pv in p.PairSets[ci])
                        {
                            if (pv.SecondGlyph != second.Glyph) continue;
                            Add(g, pv.First); Add(second, pv.Second);
                            return pv.Second.IsEmpty ? 1 : j - i + 1;
                        }
                        return 0;
                    }
                    int c1 = p.Class1.Class(g.Glyph), c2 = p.Class2.Class(second.Glyph);
                    if (c1 >= p.Class1Count || c2 >= p.Class2Count) return 0;
                    var v1 = p.First[c1, c2]; var v2 = p.Second[c1, c2];
                    if (v1.IsEmpty && v2.IsEmpty) return 0;
                    Add(g, v1); Add(second, v2);
                    return v2.IsEmpty ? 1 : j - i + 1;
                }
                case MarkBasePos mb:
                {
                    int mi = mb.MarkCoverage.Index(g.Glyph);
                    if (mi < 0 || mi >= mb.Marks.Length) return 0;
                    // The base: the nearest preceding glyph that is not a mark, skipping what the
                    // flags say to skip. A mark attaches to the base of its own cluster only.
                    int j = i - 1;
                    while (j >= 0 && SameSyllable(g, buf[j]) && (ClassOf(buf[j]) == ClassMark || IsIgnored(lookup, buf[j]))) j--;
                    if (j < 0 || !SameSyllable(g, buf[j])) return 0;
                    int bi = mb.BaseCoverage.Index(buf[j].Glyph);
                    if (bi < 0 || bi >= mb.BaseAnchors.Length) return 0;
                    var mark = mb.Marks[mi];
                    if (mark.Class >= mb.ClassCount) return 0;
                    var baseAnchor = mb.BaseAnchors[bi][mark.Class];
                    if (!baseAnchor.IsSet || !mark.Anchor.IsSet) return 0;
                    Attach(g, j, baseAnchor, mark.Anchor);
                    return 1;
                }
                case MarkLigPos ml:
                {
                    int mi = ml.MarkCoverage.Index(g.Glyph);
                    if (mi < 0 || mi >= ml.Marks.Length) return 0;
                    int j = i - 1;
                    while (j >= 0 && SameSyllable(g, buf[j]) && (ClassOf(buf[j]) == ClassMark || IsIgnored(lookup, buf[j]))) j--;
                    if (j < 0 || !SameSyllable(g, buf[j])) return 0;
                    int li = ml.LigatureCoverage.Index(buf[j].Glyph);
                    if (li < 0 || li >= ml.LigatureAnchors.Length) return 0;
                    var comps = ml.LigatureAnchors[li];
                    if (comps.Length == 0) return 0;
                    // Which component the mark followed: recorded by the ligature substitution;
                    // a mark that was not inside the ligature hangs on its last component.
                    int comp = comps.Length - 1;
                    if (g.LigatureId != 0 && g.LigatureId == buf[j].LigatureId && g.LigatureComponent > 0)
                        comp = Math.Min(g.LigatureComponent - 1, comps.Length - 1);
                    var mark = ml.Marks[mi];
                    if (mark.Class >= ml.ClassCount) return 0;
                    var anchor = comps[comp][mark.Class];
                    if (!anchor.IsSet || !mark.Anchor.IsSet) return 0;
                    Attach(g, j, anchor, mark.Anchor);
                    return 1;
                }
                case MarkMarkPos mm:
                {
                    int mi = mm.Mark1Coverage.Index(g.Glyph);
                    if (mi < 0 || mi >= mm.Marks1.Length) return 0;
                    // The previous glyph, skipping only what mark filtering says to skip: it must
                    // itself be a mark, and belong to the same ligature component as this one.
                    int j = i - 1;
                    while (j >= 0 && SameSyllable(g, buf[j]) && ClassOf(buf[j]) == ClassMark && IsIgnored(lookup, buf[j])) j--;
                    if (j < 0 || !SameSyllable(g, buf[j]) || ClassOf(buf[j]) != ClassMark) return 0;
                    var m2 = buf[j];
                    bool sameComponent = g.LigatureId == m2.LigatureId && g.LigatureComponent == m2.LigatureComponent;
                    if (!sameComponent && !(g.LigatureId == 0 && m2.LigatureId == 0)) return 0;
                    int m2i = mm.Mark2Coverage.Index(m2.Glyph);
                    if (m2i < 0 || m2i >= mm.Mark2Anchors.Length) return 0;
                    var mark = mm.Marks1[mi];
                    if (mark.Class >= mm.ClassCount) return 0;
                    var anchor = mm.Mark2Anchors[m2i][mark.Class];
                    if (!anchor.IsSet || !mark.Anchor.IsSet) return 0;
                    Attach(g, j, anchor, mark.Anchor);
                    return 1;
                }
                case ContextSubst c:
                    return ApplyContext(table, lookup, c, buf, i, mask, depth);
                default:
                    return 0;
            }
        }

        private static void Add(ShapedGlyph g, ValueRecord v)
        {
            g.XOffset += v.XPlacement;
            g.YOffset += v.YPlacement;
            g.XAdvance += v.XAdvance;
        }

        private static void Attach(ShapedGlyph mark, int baseIndex, Anchor baseAnchor, Anchor markAnchor)
        {
            mark.AttachedTo = baseIndex;
            mark.AttachX = baseAnchor.X - markAnchor.X;
            mark.AttachY = baseAnchor.Y - markAnchor.Y;
        }

        /// <summary>
        /// Contextual matching: the input sequence from position i (first item = the glyph at i),
        /// the backtrack before it (nearest first) and the lookahead after it, every item on a
        /// non-ignored glyph; then the nested lookups on the matched input positions.
        /// </summary>
        private int ApplyContext(LayoutTable table, Lookup lookup, ContextSubst c, GlyphBuffer buf, int i, uint mask, int depth)
        {
            var g = buf[i];
            if (c.Format == 3)
            {
                var r = c.Rule3;
                if (r.InputCov.Length == 0 || !r.InputCov[0].Contains(g.Glyph)) return 0;
                var positions = MatchRule(lookup, buf, i, mask, r, c, coverage: true);
                if (positions == null) return 0;
                return RunNested(table, buf, positions, r.Lookups, depth);
            }
            int ci = c.Coverage.Index(g.Glyph);
            if (ci < 0) return 0;
            int setIndex = c.Format == 1 ? ci : c.InputClasses.Class(g.Glyph);
            if (setIndex < 0 || setIndex >= c.RuleSets.Length) return 0;
            foreach (var rule in c.RuleSets[setIndex])
            {
                var positions = MatchRule(lookup, buf, i, mask, rule, c, coverage: false);
                if (positions == null) continue;
                return RunNested(table, buf, positions, rule.Lookups, depth);
            }
            return 0;
        }

        /// <summary>Matched input positions (index 0 = i), or null.</summary>
        private List<int> MatchRule(Lookup lookup, GlyphBuffer buf, int i, uint mask, ContextRule r, ContextSubst c, bool coverage)
        {
            int inputCount = coverage ? r.InputCov.Length : r.Input.Length + 1;
            var positions = new List<int>(inputCount) { i };
            int pos = i;
            for (int k = 1; k < inputCount; k++)
            {
                pos = Next(lookup, buf, pos, mask);
                if (pos < 0 || (buf[pos].Mask & mask) == 0) return null;
                int glyph = buf[pos].Glyph;
                bool ok = coverage ? r.InputCov[k].Contains(glyph)
                    : c.Format == 1 ? glyph == r.Input[k - 1]
                    : c.InputClasses.Class(glyph) == r.Input[k - 1];
                if (!ok) return null;
                positions.Add(pos);
            }
            int backCount = coverage ? r.BacktrackCov.Length : r.Backtrack.Length;
            pos = i;
            for (int k = 0; k < backCount; k++)
            {
                pos = Prev(lookup, buf, pos);
                if (pos < 0) return null;
                int glyph = buf[pos].Glyph;
                bool ok = coverage ? r.BacktrackCov[k].Contains(glyph)
                    : c.Format == 1 ? glyph == r.Backtrack[k]
                    : c.BacktrackClasses.Class(glyph) == r.Backtrack[k];
                if (!ok) return null;
            }
            int aheadCount = coverage ? r.LookaheadCov.Length : r.Lookahead.Length;
            pos = positions[positions.Count - 1];
            for (int k = 0; k < aheadCount; k++)
            {
                pos = Next(lookup, buf, pos, mask);
                if (pos < 0) return null;
                int glyph = buf[pos].Glyph;
                bool ok = coverage ? r.LookaheadCov[k].Contains(glyph)
                    : c.Format == 1 ? glyph == r.Lookahead[k]
                    : c.LookaheadClasses.Class(glyph) == r.Lookahead[k];
                if (!ok) return null;
            }
            return positions;
        }

        private int RunNested(LayoutTable table, GlyphBuffer buf, List<int> positions, SequenceLookup[] records, int depth)
        {
            int end = positions[positions.Count - 1];
            foreach (var rec in records)
            {
                if (rec.SequenceIndex >= positions.Count) continue;
                if (rec.LookupIndex < 0 || rec.LookupIndex >= table.Lookups.Length) continue;
                int at = positions[rec.SequenceIndex];
                if (at < 0 || at >= buf.Count) continue;
                int before = buf.Count;
                var nested = table.Lookups[rec.LookupIndex];
                if (IsIgnored(nested, buf[at])) continue;
                if (ApplyAt(table, nested, buf, at, uint.MaxValue, depth + 1) == 0) continue;
                int delta = buf.Count - before;
                if (delta != 0)
                {
                    // Positions after the change shift with the buffer; the one changed stays.
                    for (int k = 0; k < positions.Count; k++) if (positions[k] > at) positions[k] += delta;
                    end += delta;
                }
            }
            return Math.Max(1, end - positions[0] + 1);
        }
    }
}
