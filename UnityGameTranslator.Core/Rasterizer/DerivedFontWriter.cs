using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Writes the DERIVED font of a TrueType font: the same font, plus one glyph per shaped glyph an
    /// engine must draw — a TrueType composite of the source glyph, shifted by the offset the shaper
    /// gave it and advancing by the width it gave it — each reachable by a private-use codepoint of
    /// the Basic Multilingual Plane. An engine that draws BY CODEPOINT then draws exactly what the
    /// shaper placed (analyse/ecritures-complexes-etat-reel.md, probes 1 and 2).
    ///
    /// Why EVERY glyph of a shaped line is a new glyph, even one the shaper left in place: TMP 3.2+
    /// and UI Toolkit's TextCore apply the font's own GPOS to the glyph ids they meet, so an original
    /// glyph in the string is positioned a second time (probe 1, Unity 6000.0). A glyph added here is
    /// in no layout table: nothing can move it again.
    ///
    /// What changes in the file: cmap (a format 4 and a format 12 subtable, the source's mappings plus
    /// the new ones), glyf + loca (long offsets), hmtx + hhea (one metric per glyph), maxp, name (another
    /// family — the derived file must never pass for the original; notices kept), post (format 3: no
    /// glyph names to keep in step), head (checksum). Dropped: the variation tables (glyf already holds
    /// the default instance — what an engine draws of a variable font), the tables sized by glyph count
    /// we do not rebuild, and the signature the edit invalidates. GSUB/GPOS/GDEF stay: they name the
    /// source's glyph ids, which do not move.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks.
    /// </summary>
    internal static class DerivedFontWriter
    {
        /// <summary>One glyph to add: this codepoint shows the source glyph moved by (DX, DY), advancing by Advance.</summary>
        internal struct Added
        {
            public int Codepoint;
            public int Glyph;
            public int DX, DY;
            public int Advance;
        }

        private static readonly HashSet<string> Dropped = new HashSet<string>(StringComparer.Ordinal)
        {
            "fvar", "gvar", "HVAR", "MVAR", "VVAR", "avar", "STAT", "cvar",   // variations: glyf is the default instance
            "hdmx", "LTSH", "VDMX",                                          // sized by glyph count, not rebuilt
            "DSIG",                                                          // a signature this edit invalidates
        };

        /// <summary>
        /// The derived file, or null with <paramref name="refusal"/> saying why — a font without
        /// TrueType outlines (CFF: a composite cannot reference its glyphs), a codepoint the source
        /// already maps, a codepoint outside the Basic Multilingual Plane, a glyph index out of range.
        /// </summary>
        internal static byte[] Write(byte[] source, string family, IList<Added> added, out string refusal)
        {
            refusal = null;
            var tables = ReadDirectory(source);
            if (!tables.ContainsKey("glyf") || !tables.ContainsKey("loca"))
            {
                refusal = "no TrueType outlines (CFF): a composite glyph cannot reference its glyphs";
                return null;
            }
            foreach (var need in new[] { "head", "hhea", "hmtx", "maxp", "cmap" })
                if (!tables.ContainsKey(need)) { refusal = $"the '{need}' table is missing"; return null; }

            var parser = new TtfParser(source);
            int numGlyphs = parser.GlyphCount;
            var cmap = new SortedDictionary<int, int>();
            foreach (int cp in parser.GetSupportedCodepoints())
                cmap[cp] = parser.GetGlyphIndex(cp);

            foreach (var a in added)
            {
                if (a.Codepoint <= 0 || a.Codepoint > 0xFFFF) { refusal = $"U+{a.Codepoint:X} is not a codepoint of the Basic Multilingual Plane"; return null; }
                if (cmap.ContainsKey(a.Codepoint)) { refusal = $"U+{a.Codepoint:X4} is already mapped by the font"; return null; }
                if (a.Glyph < 0 || a.Glyph >= numGlyphs) { refusal = $"glyph {a.Glyph} does not exist (the font has {numGlyphs})"; return null; }
            }

            byte[] head = Slice(source, tables["head"]);
            byte[] hhea = Slice(source, tables["hhea"]);
            byte[] maxp = Slice(source, tables["maxp"]);
            byte[] loca = Slice(source, tables["loca"]);
            byte[] glyf = Slice(source, tables["glyf"]);
            byte[] hmtx = Slice(source, tables["hmtx"]);

            bool longLoca = ReadInt16(head, 50) == 1;
            var offsets = new uint[numGlyphs + 1];
            for (int i = 0; i <= numGlyphs; i++)
                offsets[i] = longLoca ? ReadUInt32(loca, i * 4) : (uint)ReadUInt16(loca, i * 2) * 2;

            // ── glyf + loca: the source's glyphs as they are, then one composite per added glyph ──
            int total = numGlyphs + added.Count;
            if (total > 0xFFFF) { refusal = $"{total} glyphs: more than a TrueType font can hold"; return null; }

            // The source's glyphs as they are (long offsets from now on), then one glyph per added one,
            // in order: glyph numGlyphs + k is added[k].
            var glyfOut = new MemoryStream();
            glyfOut.Write(glyf, 0, (int)offsets[numGlyphs]);
            var locaOut = new MemoryStream();
            for (int i = 0; i < numGlyphs; i++) WriteUInt32(locaOut, offsets[i]);
            var newMetrics = new List<KeyValuePair<int, int>>(added.Count);   // (advance, lsb)
            int sourceDepth = maxp.Length >= 32 ? ReadUInt16(maxp, 30) : 0;  // maxComponentDepth (maxp 1.0)
            int maxDepth = sourceDepth;
            foreach (var a in added)
            {
                WriteUInt32(locaOut, (uint)glyfOut.Length);                    // where this glyph starts
                uint start = offsets[a.Glyph], end = offsets[a.Glyph + 1];
                if (end == start)
                {
                    // An empty source glyph (a space) has nothing to reference: an empty glyph that
                    // only advances.
                    newMetrics.Add(new KeyValuePair<int, int>(a.Advance, 0));
                    continue;
                }
                int xMin = ReadInt16(glyf, (int)start + 2) + a.DX, yMin = ReadInt16(glyf, (int)start + 4) + a.DY;
                int xMax = ReadInt16(glyf, (int)start + 6) + a.DX, yMax = ReadInt16(glyf, (int)start + 8) + a.DY;
                WriteInt16(glyfOut, -1);                                       // numberOfContours: composite
                WriteInt16(glyfOut, xMin); WriteInt16(glyfOut, yMin); WriteInt16(glyfOut, xMax); WriteInt16(glyfOut, yMax);
                WriteUInt16(glyfOut, 0x0001 | 0x0002);                         // ARG_1_AND_2_ARE_WORDS | ARGS_ARE_XY_VALUES
                WriteUInt16(glyfOut, a.Glyph);
                WriteInt16(glyfOut, a.DX);
                WriteInt16(glyfOut, a.DY);
                while (glyfOut.Length % 4 != 0) glyfOut.WriteByte(0);
                newMetrics.Add(new KeyValuePair<int, int>(a.Advance, xMin));
                // One level above its component: a composite of a composite is one deeper than the source's deepest.
                maxDepth = Math.Max(maxDepth, ReadInt16(glyf, (int)start) < 0 ? sourceDepth + 1 : 1);
            }
            WriteUInt32(locaOut, (uint)glyfOut.Length);                        // the end of the last glyph
            byte[] glyfNew = glyfOut.ToArray();

            // ── hmtx + hhea: one (advance, lsb) per glyph ──
            int numH = ReadUInt16(hhea, 34);
            var hmtxOut = new MemoryStream();
            int lastAdvance = 0, maxAdvance = ReadUInt16(hhea, 10);
            for (int i = 0; i < numGlyphs; i++)
            {
                int adv, lsb;
                if (i < numH) { adv = ReadUInt16(hmtx, i * 4); lsb = ReadInt16(hmtx, i * 4 + 2); lastAdvance = adv; }
                else { adv = lastAdvance; lsb = ReadInt16(hmtx, numH * 4 + (i - numH) * 2); }
                WriteUInt16(hmtxOut, adv); WriteInt16(hmtxOut, lsb);
            }
            foreach (var m in newMetrics)
            {
                WriteUInt16(hmtxOut, Math.Max(0, m.Key)); WriteInt16(hmtxOut, m.Value);
                maxAdvance = Math.Max(maxAdvance, m.Key);
            }
            WriteUInt16At(hhea, 34, total);
            WriteUInt16At(hhea, 10, maxAdvance);

            // ── maxp, head, post ──
            WriteUInt16At(maxp, 4, total);
            if (maxp.Length >= 32)
            {
                WriteUInt16At(maxp, 28, Math.Max(ReadUInt16(maxp, 28), 1));    // maxComponentElements
                WriteUInt16At(maxp, 30, maxDepth);
            }
            WriteUInt16At(head, 50, 1);                                        // indexToLocFormat: long
            WriteUInt32At(head, 8, 0);                                         // checkSumAdjustment, set last
            var post = new MemoryStream();
            byte[] oldPost = tables.ContainsKey("post") ? Slice(source, tables["post"]) : new byte[32];
            WriteUInt32(post, 0x00030000);                                     // format 3: no glyph names
            post.Write(oldPost, 4, Math.Min(28, oldPost.Length - 4));
            while (post.Length < 32) post.WriteByte(0);

            // ── cmap: the source's mappings and the added ones ──
            for (int k = 0; k < added.Count; k++) cmap[added[k].Codepoint] = numGlyphs + k;
            byte[] cmapNew = BuildCmap(cmap);

            // ── name: another family, every notice kept ──
            byte[] nameNew = tables.ContainsKey("name") ? RenameFamily(Slice(source, tables["name"]), family) : BuildName(family);

            var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var t in tables)
            {
                if (Dropped.Contains(t.Key)) continue;
                output[t.Key] = Slice(source, t.Value);
            }
            output["glyf"] = glyfNew;
            output["loca"] = locaOut.ToArray();
            output["hmtx"] = hmtxOut.ToArray();
            output["hhea"] = hhea;
            output["maxp"] = maxp;
            output["head"] = head;
            output["post"] = post.ToArray();
            output["cmap"] = cmapNew;
            output["name"] = nameNew;
            if (output.ContainsKey("OS/2")) output["OS/2"] = WidenCharRange(output["OS/2"], cmap);
            return Assemble(output);
        }

        // ─────────────────────────────── cmap ───────────────────────────────

        private static byte[] BuildCmap(SortedDictionary<int, int> map)
        {
            byte[] f4 = BuildFormat4(map);
            byte[] f12 = BuildFormat12(map);
            var ms = new MemoryStream();
            WriteUInt16(ms, 0);                                                // version
            WriteUInt16(ms, 2);                                                // subtables
            WriteUInt16(ms, 3); WriteUInt16(ms, 1); WriteUInt32(ms, 4 + 8 * 2);                    // Windows BMP → format 4
            WriteUInt16(ms, 3); WriteUInt16(ms, 10); WriteUInt32(ms, (uint)(4 + 8 * 2 + f4.Length)); // Windows full → format 12
            ms.Write(f4, 0, f4.Length);
            ms.Write(f12, 0, f12.Length);
            return ms.ToArray();
        }

        /// <summary>Format 4: runs of consecutive codepoints mapped to consecutive glyphs, each a segment with idDelta.</summary>
        private static byte[] BuildFormat4(SortedDictionary<int, int> map)
        {
            var starts = new List<int>(); var ends = new List<int>(); var deltas = new List<int>();
            int runStart = -1, runEnd = -1, runGlyph = -1;
            foreach (var kv in map)
            {
                if (kv.Key > 0xFFFE) break;
                if (runStart >= 0 && kv.Key == runEnd + 1 && kv.Value == runGlyph + (kv.Key - runStart)) { runEnd = kv.Key; continue; }
                if (runStart >= 0) { starts.Add(runStart); ends.Add(runEnd); deltas.Add(runGlyph - runStart); }
                runStart = runEnd = kv.Key; runGlyph = kv.Value;
            }
            if (runStart >= 0) { starts.Add(runStart); ends.Add(runEnd); deltas.Add(runGlyph - runStart); }
            starts.Add(0xFFFF); ends.Add(0xFFFF); deltas.Add(1);               // the closing segment

            int segs = starts.Count;
            var ms = new MemoryStream();
            WriteUInt16(ms, 4);
            WriteUInt16(ms, 16 + segs * 8);                                    // length
            WriteUInt16(ms, 0);                                                // language
            WriteUInt16(ms, segs * 2);
            int sr = 2 * (1 << (int)Math.Floor(Math.Log(segs, 2)));
            WriteUInt16(ms, sr);
            WriteUInt16(ms, (int)Math.Floor(Math.Log(segs, 2)));
            WriteUInt16(ms, segs * 2 - sr);
            foreach (int e in ends) WriteUInt16(ms, e);
            WriteUInt16(ms, 0);                                                // reservedPad
            foreach (int s in starts) WriteUInt16(ms, s);
            foreach (int d in deltas) WriteUInt16(ms, d & 0xFFFF);
            foreach (int _ in starts) WriteUInt16(ms, 0);                      // idRangeOffset: always idDelta
            if (ms.Length > 0xFFFF) throw new InvalidDataException("cmap format 4 over 64 KB");
            return ms.ToArray();
        }

        private static byte[] BuildFormat12(SortedDictionary<int, int> map)
        {
            var groups = new List<int[]>();
            int s = -1, e = -1, g = -1;
            foreach (var kv in map)
            {
                if (s >= 0 && kv.Key == e + 1 && kv.Value == g + (kv.Key - s)) { e = kv.Key; continue; }
                if (s >= 0) groups.Add(new[] { s, e, g });
                s = e = kv.Key; g = kv.Value;
            }
            if (s >= 0) groups.Add(new[] { s, e, g });
            var ms = new MemoryStream();
            WriteUInt16(ms, 12); WriteUInt16(ms, 0);
            WriteUInt32(ms, (uint)(16 + groups.Count * 12));
            WriteUInt32(ms, 0);
            WriteUInt32(ms, (uint)groups.Count);
            foreach (var grp in groups) { WriteUInt32(ms, (uint)grp[0]); WriteUInt32(ms, (uint)grp[1]); WriteUInt32(ms, (uint)grp[2]); }
            return ms.ToArray();
        }

        // ─────────────────────────────── name ───────────────────────────────

        private static readonly int[] FamilyIds = { 1, 3, 4, 6, 16, 17, 21, 22 };

        /// <summary>
        /// The source's name records minus those that say which font this is, plus ours for Windows
        /// (3, 1, en-US). Copyright, licence and every other notice are kept word for word (OFL).
        /// </summary>
        private static byte[] RenameFamily(byte[] name, string family)
        {
            int count = ReadUInt16(name, 2), storage = ReadUInt16(name, 4);
            var kept = new List<Tuple<int, int, int, int, byte[]>>();
            for (int i = 0; i < count; i++)
            {
                int r = 6 + i * 12;
                int nameId = ReadUInt16(name, r + 6);
                if (Array.IndexOf(FamilyIds, nameId) >= 0) continue;
                int length = ReadUInt16(name, r + 8), offset = ReadUInt16(name, r + 10);
                var bytes = new byte[length];
                Array.Copy(name, storage + offset, bytes, 0, length);
                kept.Add(Tuple.Create(ReadUInt16(name, r), ReadUInt16(name, r + 2), ReadUInt16(name, r + 4), nameId, bytes));
            }
            foreach (var rec in OurRecords(family)) kept.Add(rec);
            return BuildNameTable(kept);
        }

        private static byte[] BuildName(string family) => BuildNameTable(new List<Tuple<int, int, int, int, byte[]>>(OurRecords(family)));

        private static IEnumerable<Tuple<int, int, int, int, byte[]>> OurRecords(string family)
        {
            var be = Encoding.BigEndianUnicode;
            string ps = family.Replace(" ", "");
            yield return Tuple.Create(3, 1, 0x409, 1, be.GetBytes(family));
            yield return Tuple.Create(3, 1, 0x409, 3, be.GetBytes(ps + "-UGT"));
            yield return Tuple.Create(3, 1, 0x409, 4, be.GetBytes(family));
            yield return Tuple.Create(3, 1, 0x409, 6, be.GetBytes(ps));
        }

        private static byte[] BuildNameTable(List<Tuple<int, int, int, int, byte[]>> records)
        {
            records.Sort((a, b) =>
            {
                int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
                c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
                c = a.Item3.CompareTo(b.Item3); if (c != 0) return c;
                return a.Item4.CompareTo(b.Item4);
            });
            var ms = new MemoryStream();
            WriteUInt16(ms, 0);
            WriteUInt16(ms, records.Count);
            WriteUInt16(ms, 6 + records.Count * 12);
            int offset = 0;
            foreach (var r in records)
            {
                WriteUInt16(ms, r.Item1); WriteUInt16(ms, r.Item2); WriteUInt16(ms, r.Item3); WriteUInt16(ms, r.Item4);
                WriteUInt16(ms, r.Item5.Length); WriteUInt16(ms, offset);
                offset += r.Item5.Length;
            }
            foreach (var r in records) ms.Write(r.Item5, 0, r.Item5.Length);
            return ms.ToArray();
        }

        /// <summary>OS/2 names the first and last BMP character: the private codepoints may widen it.</summary>
        private static byte[] WidenCharRange(byte[] os2, SortedDictionary<int, int> cmap)
        {
            if (os2.Length < 68) return os2;
            int first = 0xFFFF, last = 0;
            foreach (int cp in cmap.Keys)
            {
                if (cp > 0xFFFF) continue;
                first = Math.Min(first, cp); last = Math.Max(last, cp);
            }
            WriteUInt16At(os2, 64, first);
            WriteUInt16At(os2, 66, last);
            return os2;
        }

        // ─────────────────────────────── the file ───────────────────────────────

        private struct Entry { public uint Offset, Length; }

        private static Dictionary<string, Entry> ReadDirectory(byte[] f)
        {
            uint sfnt = ReadUInt32(f, 0);
            if (sfnt == 0x74746366) throw new InvalidDataException("a font collection (.ttc) is not a single font");
            int numTables = ReadUInt16(f, 4);
            var d = new Dictionary<string, Entry>(StringComparer.Ordinal);
            for (int i = 0; i < numTables; i++)
            {
                int r = 12 + i * 16;
                string tag = Encoding.ASCII.GetString(f, r, 4);
                d[tag] = new Entry { Offset = ReadUInt32(f, r + 8), Length = ReadUInt32(f, r + 12) };
            }
            return d;
        }

        private static byte[] Slice(byte[] f, Entry e)
        {
            var b = new byte[e.Length];
            Array.Copy(f, e.Offset, b, 0, e.Length);
            return b;
        }

        /// <summary>The table directory, the tables 4-aligned, every checksum, and head's adjustment.</summary>
        private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
        {
            int n = tables.Count;
            int entrySelector = (int)Math.Floor(Math.Log(n, 2));
            int searchRange = (1 << entrySelector) * 16;
            var ms = new MemoryStream();
            WriteUInt32(ms, 0x00010000);
            WriteUInt16(ms, n); WriteUInt16(ms, searchRange); WriteUInt16(ms, entrySelector); WriteUInt16(ms, n * 16 - searchRange);
            uint offset = (uint)(12 + n * 16);
            var placed = new List<KeyValuePair<string, uint>>();
            foreach (var t in tables)
            {
                ms.Write(Encoding.ASCII.GetBytes(t.Key), 0, 4);
                WriteUInt32(ms, Checksum(t.Value));
                WriteUInt32(ms, offset);
                WriteUInt32(ms, (uint)t.Value.Length);
                placed.Add(new KeyValuePair<string, uint>(t.Key, offset));
                offset += (uint)((t.Value.Length + 3) & ~3);
            }
            foreach (var t in tables)
            {
                ms.Write(t.Value, 0, t.Value.Length);
                while (ms.Length % 4 != 0) ms.WriteByte(0);
            }
            byte[] file = ms.ToArray();
            uint headOffset = 0;
            foreach (var p in placed) if (p.Key == "head") headOffset = p.Value;
            uint adjustment = unchecked(0xB1B0AFBA - Checksum(file));
            WriteUInt32At(file, (int)headOffset + 8, adjustment);
            return file;
        }

        private static uint Checksum(byte[] b)
        {
            uint sum = 0;
            for (int i = 0; i < b.Length; i += 4)
            {
                uint word = 0;
                for (int k = 0; k < 4; k++) word = (word << 8) | (uint)(i + k < b.Length ? b[i + k] : 0);
                sum = unchecked(sum + word);
            }
            return sum;
        }

        // ─────────────────────────────── big-endian ───────────────────────────────

        private static int ReadUInt16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        private static int ReadInt16(byte[] b, int o) => (short)((b[o] << 8) | b[o + 1]);
        private static uint ReadUInt32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        private static void WriteUInt16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void WriteInt16(Stream s, int v) => WriteUInt16(s, v & 0xFFFF);
        private static void WriteUInt32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void WriteUInt16At(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        private static void WriteUInt32At(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    }
}
