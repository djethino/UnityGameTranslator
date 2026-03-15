using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Minimal PNG encoder in pure C#. Encodes RGBA pixel data to PNG format.
    /// No Unity dependencies — works on all runtimes including IL2CPP where
    /// Texture2D.SetPixels32 and EncodeToPNG are stripped.
    /// </summary>
    public static class PngEncoder
    {
        /// <summary>
        /// Encode RGBA pixel data (top-to-bottom, 4 bytes per pixel) to PNG.
        /// </summary>
        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            using (var ms = new MemoryStream())
            {
                // PNG signature
                ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                // IHDR chunk
                WriteChunk(ms, "IHDR", WriteIHDR(width, height));

                // IDAT chunk(s) — compressed image data
                byte[] compressedData = CompressImageData(rgba, width, height);
                WriteChunk(ms, "IDAT", compressedData);

                // IEND chunk
                WriteChunk(ms, "IEND", new byte[0]);

                return ms.ToArray();
            }
        }

        private static byte[] WriteIHDR(int width, int height)
        {
            var data = new byte[13];
            WriteBE32(data, 0, width);
            WriteBE32(data, 4, height);
            data[8] = 8;  // bit depth
            data[9] = 6;  // color type: RGBA
            data[10] = 0; // compression
            data[11] = 0; // filter
            data[12] = 0; // interlace
            return data;
        }

        private static byte[] CompressImageData(byte[] rgba, int width, int height)
        {
            // Build raw data with filter bytes (filter type 0 = None for each row)
            int rowBytes = width * 4;
            var raw = new byte[(rowBytes + 1) * height];

            for (int y = 0; y < height; y++)
            {
                int rawRowStart = y * (rowBytes + 1);
                raw[rawRowStart] = 0; // Filter: None
                Array.Copy(rgba, y * rowBytes, raw, rawRowStart + 1, rowBytes);
            }

            // Compress with zlib (DeflateStream + zlib header)
            using (var ms = new MemoryStream())
            {
                // zlib header (CM=8, CINFO=7, no dict, FLEVEL=0)
                ms.WriteByte(0x78);
                ms.WriteByte(0x01);

                using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, true))
                {
                    deflate.Write(raw, 0, raw.Length);
                }

                // Adler32 checksum
                uint adler = Adler32(raw);
                var adlerBytes = new byte[4];
                WriteBE32(adlerBytes, 0, (int)adler);
                ms.Write(adlerBytes, 0, 4);

                return ms.ToArray();
            }
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            // Length (4 bytes BE)
            var lenBytes = new byte[4];
            WriteBE32(lenBytes, 0, data.Length);
            stream.Write(lenBytes, 0, 4);

            // Type (4 bytes ASCII)
            var typeBytes = new byte[] {
                (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]
            };
            stream.Write(typeBytes, 0, 4);

            // Data
            if (data.Length > 0)
                stream.Write(data, 0, data.Length);

            // CRC32 (over type + data)
            var crcData = new byte[4 + data.Length];
            Array.Copy(typeBytes, 0, crcData, 0, 4);
            if (data.Length > 0)
                Array.Copy(data, 0, crcData, 4, data.Length);
            uint crc = Crc32(crcData);
            var crcBytes = new byte[4];
            WriteBE32(crcBytes, 0, (int)crc);
            stream.Write(crcBytes, 0, 4);
        }

        private static void WriteBE32(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)((value >> 24) & 0xFF);
            buf[offset + 1] = (byte)((value >> 16) & 0xFF);
            buf[offset + 2] = (byte)((value >> 8) & 0xFF);
            buf[offset + 3] = (byte)(value & 0xFF);
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        // CRC32 lookup table
        private static uint[] _crcTable;

        private static uint Crc32(byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }

            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < data.Length; i++)
                crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }
}
