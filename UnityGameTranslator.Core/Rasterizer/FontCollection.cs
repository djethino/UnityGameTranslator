using System;
using System.Collections.Generic;
using System.IO;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// A font COLLECTION (.ttc: several faces sharing tables in one file) opened face by face. An
    /// installed font the translation names may live in one — the system's own font for a whole
    /// family of scripts often does — and everything here reads a single font: a face is taken out
    /// as a standalone font of its own, same tables, same bytes, nothing re-encoded.
    ///
    /// PURE by contract — linked into Core.Checks.
    /// </summary>
    internal static class FontCollection
    {
        private const uint Ttcf = 0x74746366;   // 'ttcf'

        internal static bool IsCollection(byte[] file) => file != null && file.Length >= 12 && ReadUInt32(file, 0) == Ttcf;

        internal static int FaceCount(byte[] file) => IsCollection(file) ? (int)ReadUInt32(file, 8) : 1;

        /// <summary>
        /// The index of the face of a collection that carries <paramref name="name"/>, -1 when none
        /// or when the file is not a collection. See the other overload for which face wins.
        /// </summary>
        internal static int FindFace(Stream file, string name) => FindFace(file, name, out _);

        /// <summary>The worst <see cref="FindFace(Stream, string, out int)"/> rank: a typographic family only.</summary>
        internal const int WorstRank = 3;

        /// <summary>
        /// The face of a collection that best carries <paramref name="name"/>, with how well
        /// (<paramref name="rank"/>, 0 best), so a caller searching several collections keeps the
        /// best face of all of them rather than the first file listed. Reads the directories and
        /// name tables only: a system's collections weigh tens of megabytes, and they are searched
        /// while the game starts.
        ///
        /// 0 — the face's full name (id 4) IS the name ("Yu Gothic UI Semibold");
        /// 1 — its family (id 1) is, and it is the Regular of that family;
        /// 2 — its family is, in another style: the Bold of a family carries the same family name,
        ///     and its file is listed before the Regular's ("Yu Gothic" gave the Bold, 2026-10-01);
        /// 3 — only its typographic family (id 16), shared by every weight ("Nirmala UI" names the
        ///     Semilight face too).
        /// </summary>
        internal static int FindFace(Stream file, string name, out int rank)
        {
            rank = int.MaxValue;
            var header = ReadAt(file, 0, 12);
            if (header == null || ReadUInt32(header, 0) != Ttcf) return -1;
            int count = (int)ReadUInt32(header, 8);
            var offsets = ReadAt(file, 12, count * 4);
            if (offsets == null) return -1;
            int best = -1;
            for (int i = 0; i < count && rank > 0; i++)
            {
                var table = NameTable(file, ReadUInt32(offsets, i * 4));
                if (table == null) continue;
                int r = Has(table, 4, name) ? 0
                      : Has(table, 1, name) ? (IsRegular(table) ? 1 : 2)
                      : Has(table, 16, name) ? WorstRank
                      : int.MaxValue;
                if (r < rank) { rank = r; best = i; }
            }
            return best;
        }

        private static bool Has(byte[] table, int id, string name) =>
            NamesOf(table, id).Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Its subfamily (id 2) is the plain style, in the words fonts use for it.</summary>
        private static bool IsRegular(byte[] table) =>
            NamesOf(table, 2).Exists(n => n.Equals("Regular", StringComparison.OrdinalIgnoreCase)
                                       || n.Equals("Normal", StringComparison.OrdinalIgnoreCase)
                                       || n.Equals("Book", StringComparison.OrdinalIgnoreCase)
                                       || n.Equals("Roman", StringComparison.OrdinalIgnoreCase));

        private static byte[] NameTable(Stream file, long dir)
        {
            var sfnt = ReadAt(file, dir, 12);
            if (sfnt == null) return null;
            int numTables = ReadUInt16(sfnt, 4);
            var records = ReadAt(file, dir + 12, numTables * 16);
            if (records == null) return null;
            for (int t = 0; t < numTables; t++)
            {
                int r = t * 16;
                if (records[r] == 'n' && records[r + 1] == 'a' && records[r + 2] == 'm' && records[r + 3] == 'e')
                    return ReadAt(file, ReadUInt32(records, r + 8), (int)ReadUInt32(records, r + 12));
            }
            return null;
        }

        /// <summary>The family and full names of a name table (ids 1 and 4, or the typographic family 16), every platform.</summary>
        internal static List<string> Names(byte[] table, bool typographic)
            => typographic ? NamesOf(table, 16) : NamesOf(table, 1, 4);

        /// <summary>The strings a name table holds under <paramref name="ids"/>, every platform.</summary>
        private static List<string> NamesOf(byte[] table, params int[] ids)
        {
            var names = new List<string>();
            if (table.Length < 6) return names;
            int count = ReadUInt16(table, 2), strings = ReadUInt16(table, 4);
            for (int i = 0; i < count; i++)
            {
                int r = 6 + i * 12;
                if (r + 12 > table.Length) break;
                int platform = ReadUInt16(table, r), id = ReadUInt16(table, r + 6);
                int length = ReadUInt16(table, r + 8), at = strings + ReadUInt16(table, r + 10);
                if (Array.IndexOf(ids, id) < 0) continue;
                if (at + length > table.Length) continue;
                string n = platform == 3 || platform == 0 ? System.Text.Encoding.BigEndianUnicode.GetString(table, at, length)
                         : platform == 1 ? System.Text.Encoding.ASCII.GetString(table, at, length)
                         : null;
                if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
            }
            return names;
        }

        private static byte[] ReadAt(Stream s, long offset, int length)
        {
            if (length < 0 || offset < 0 || offset + length > s.Length) return null;
            s.Position = offset;
            var b = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = s.Read(b, read, length - read);
                if (n <= 0) return null;
                read += n;
            }
            return b;
        }

        /// <summary>
        /// Face <paramref name="index"/> as a standalone font: its table directory, then each table
        /// copied as is (4-byte aligned, offsets rewritten). A single font asked for face 0 comes
        /// back as it is.
        /// </summary>
        internal static byte[] Face(byte[] file, int index)
        {
            if (!IsCollection(file))
            {
                if (index != 0) throw new ArgumentOutOfRangeException(nameof(index), "a single font has one face");
                return file;
            }
            int count = (int)ReadUInt32(file, 8);
            if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index), $"the collection has {count} face(s)");
            int dir = (int)ReadUInt32(file, 12 + index * 4);
            if (dir < 0 || dir + 12 > file.Length) throw new InvalidDataException("face directory out of the file");

            int numTables = ReadUInt16(file, dir + 4);
            if (dir + 12 + numTables * 16 > file.Length) throw new InvalidDataException("table directory out of the file");

            var records = new List<(byte[] Tag, uint Checksum, uint Offset, uint Length)>(numTables);
            for (int t = 0; t < numTables; t++)
            {
                int r = dir + 12 + t * 16;
                var tag = new byte[4];
                Array.Copy(file, r, tag, 0, 4);
                uint offset = ReadUInt32(file, r + 8), length = ReadUInt32(file, r + 12);
                if (offset + (long)length > file.Length) throw new InvalidDataException("table out of the file");
                records.Add((tag, ReadUInt32(file, r + 4), offset, length));
            }

            int headerSize = 12 + numTables * 16;
            long total = headerSize;
            foreach (var rec in records) total += (rec.Length + 3) & ~3u;
            var output = new byte[total];

            // The directory header as the face had it (search ranges depend on the table count only).
            Array.Copy(file, dir, output, 0, 12);
            uint at = (uint)headerSize;
            for (int t = 0; t < numTables; t++)
            {
                var rec = records[t];
                int r = 12 + t * 16;
                Array.Copy(rec.Tag, 0, output, r, 4);
                WriteUInt32(output, r + 4, rec.Checksum);
                WriteUInt32(output, r + 8, at);
                WriteUInt32(output, r + 12, rec.Length);
                Array.Copy(file, rec.Offset, output, at, rec.Length);
                at += (rec.Length + 3) & ~3u;
            }
            return output;
        }

        private static uint ReadUInt32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        private static int ReadUInt16(byte[] b, int o) => b[o] << 8 | b[o + 1];

        private static void WriteUInt32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
    }
}
