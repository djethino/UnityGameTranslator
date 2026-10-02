using System;
using System.Collections.Generic;
using System.IO;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// A font COLLECTION (.ttc: several faces sharing tables in one file) opened face by face. An
    /// installed font the translation names may live in one — the system's own font for a whole
    /// family of scripts often does — and everything here reads a single font: a face is taken out
    /// as a standalone font of its own, same tables, same bytes, nothing re-encoded. WHICH face
    /// carries a name is the socle's answer (<see cref="UnityGameTranslator.Common.FontFileNames"/>),
    /// shared with the Manager's export.
    ///
    /// PURE by contract — linked into Core.Checks.
    /// </summary>
    internal static class FontCollection
    {
        private const uint Ttcf = 0x74746366;   // 'ttcf'

        internal static bool IsCollection(byte[] file) => file != null && file.Length >= 12 && ReadUInt32(file, 0) == Ttcf;

        internal static int FaceCount(byte[] file) => IsCollection(file) ? (int)ReadUInt32(file, 8) : 1;

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
