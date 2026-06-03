using System;
using System.IO;
using System.IO.Compression;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Minimal PNG writer for the grayscale (Alpha8) atlases we produce. Bypasses
    /// <c>UnityEngine.ImageConversion.EncodeToPNG</c> which is broken on Il2CppInterop
    /// + Unity 6 (the IL2CPP wrapper for <c>Il2CppArrayBase&lt;byte&gt;</c> can't be
    /// instantiated at unmarshal time, so the call throws "Instances of abstract
    /// classes cannot be created" before any bytes come back).
    ///
    /// We only need the single-channel-8-bit path, which is the simplest PNG variant —
    /// no palette, no filters beyond None, no interlacing. Output is fully spec-conformant
    /// and decodes in every image viewer / Unity LoadImage we've tested.
    ///
    /// References:
    ///   - PNG 1.2 spec (https://www.w3.org/TR/PNG/) — sections 4 (Chunks), 12 (IDAT)
    ///   - RFC 1950 (zlib container) and RFC 1951 (DEFLATE)
    /// </summary>
    public static class PngEncoder
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// Encode an Alpha8 atlas (one byte per pixel) as a grayscale PNG.
        ///
        /// <paramref name="topDown"/>: when true (default), <c>pixels[row * width + col]</c>
        /// is interpreted as the pixel at (col, row) with row 0 at the top — matching
        /// PNG's native scan order. When false, the buffer is treated as Unity-style
        /// bottom-up (row 0 = bottom) and the writer walks rows in reverse to produce
        /// a correctly oriented PNG. This lets us hand it the same byte array we just
        /// fed to <c>Texture2D.LoadRawTextureData</c> (which is bottom-up by Unity
        /// convention) without an in-place flip.
        /// </summary>
        public static byte[] EncodeAlpha8(byte[] pixels, int width, int height, bool topDown = true)
        {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (width <= 0 || height <= 0) throw new ArgumentException("invalid dimensions");
            if (pixels.Length != width * height)
                throw new ArgumentException($"pixel buffer length {pixels.Length} does not match {width}×{height} = {width * height}");

            using (var ms = new MemoryStream())
            {
                ms.Write(PngSignature, 0, PngSignature.Length);
                WriteIhdr(ms, width, height);
                WriteIdat(ms, pixels, width, height, topDown);
                WriteIend(ms);
                return ms.ToArray();
            }
        }

        private static void WriteIhdr(Stream s, int width, int height)
        {
            // IHDR data: width(4) height(4) bitDepth(1) colorType(1)
            //           compression(1)=0 filter(1)=0 interlace(1)=0
            //
            // colorType=4 = grayscale + alpha (2 samples per pixel). We use this
            // instead of colorType=0 (grayscale only) so that Unity's LoadImage
            // correctly routes the SDF value into the texture's alpha channel — TMP
            // reads `_MainTex.a` at sample time. On a colorType=0 PNG, LoadImage on
            // an Alpha8 (or RGBA32) target leaves alpha=255 and puts the grayscale
            // sample in the red channel, which makes every glyph render as a solid
            // white rectangle of the rect bounds ("inside SDF everywhere").
            //
            // We pay 2 bytes/pixel raw (512 MB for a 16384² atlas) instead of 1, but
            // the SDF compresses to almost identical disk size because grayscale==alpha
            // duplicates perfectly and deflate handles the redundancy in stride.
            var data = new byte[13];
            WriteUInt32BE(data, 0, (uint)width);
            WriteUInt32BE(data, 4, (uint)height);
            data[8] = 8;  // bit depth: 8 bits per channel
            data[9] = 4;  // color type 4 = grayscale + alpha
            data[10] = 0; // compression: deflate (only option in spec)
            data[11] = 0; // filter: adaptive (only option in spec)
            data[12] = 0; // interlace: none
            WriteChunk(s, "IHDR", data, 0, data.Length);
        }

        private static void WriteIdat(Stream s, byte[] pixels, int width, int height, bool topDown)
        {
            // colorType=4 (grayscale + alpha), bit depth 8 → 2 bytes per pixel.
            // Each scanline: 1 filter byte ("None" = 0) + width * 2 sample bytes.
            // We duplicate the SDF byte into both samples: grayscale=value, alpha=value.
            // PNG viewers show the grayscale as the visible image, and Unity's LoadImage
            // routes the alpha sample into the texture's .a channel for TMP's SDF shader.
            //
            // We stream into the DeflateStream row-by-row and only hold ONE row of raw
            // bytes at a time (~32 KB for a 16384-wide atlas). Building the full raw
            // payload up-front would otherwise add 537 MB of Mono LOH pressure during
            // the encode — a measurable +500 MB Task Manager spike the user has seen.
            // The compressed payload is also kept off the LOH for as long as we can:
            // we compute Adler32 incrementally and only collect the final byte[] at
            // chunk-write time.
            using (var payload = new MemoryStream())
            {
                // zlib header: deflate, 32 KB window, default compression level. Same
                // 0x78 0x9C combo as before so any decoder still accepts the stream.
                payload.WriteByte(0x78);
                payload.WriteByte(0x9C);

                uint adlerA = 1;
                uint adlerB = 0;
                const uint MOD_ADLER = 65521;

                int rowBytes = 1 + width * 2;
                byte[] rowBuf = new byte[rowBytes];

                using (var deflate = new DeflateStream(payload, CompressionMode.Compress, leaveOpen: true))
                {
                    for (int row = 0; row < height; row++)
                    {
                        rowBuf[0] = 0; // None filter
                        int srcRow = topDown ? row : (height - 1 - row);
                        int srcBase = srcRow * width;
                        int dst = 1;
                        for (int col = 0; col < width; col++)
                        {
                            byte v = pixels[srcBase + col];
                            rowBuf[dst++] = v; // grayscale sample
                            rowBuf[dst++] = v; // alpha sample
                        }
                        deflate.Write(rowBuf, 0, rowBytes);

                        // Update Adler32 over the same bytes we just compressed. Doing this
                        // inline avoids touching the (large) raw payload a second time.
                        for (int i = 0; i < rowBytes; i++)
                        {
                            adlerA = (adlerA + rowBuf[i]) % MOD_ADLER;
                            adlerB = (adlerB + adlerA) % MOD_ADLER;
                        }
                    }
                }

                // zlib trailer: Adler32 over the uncompressed data.
                uint adler = (adlerB << 16) | adlerA;
                WriteUInt32BE(payload, adler);

                // Write IDAT chunk (length | type | data | crc) from the compressed payload.
                byte[] data = payload.ToArray();
                WriteChunk(s, "IDAT", data, 0, data.Length);
            }
        }

        private static void WriteIend(Stream s)
        {
            // IEND is empty (no data)
            WriteChunk(s, "IEND", null, 0, 0);
        }

        private static void WriteChunk(Stream s, string type, byte[] data, int offset, int length)
        {
            // Each PNG chunk: length(4 BE) | type(4 ASCII) | data(length) | crc32(4 BE)
            // CRC is computed over type + data.
            if (type.Length != 4) throw new ArgumentException("type must be 4 chars");
            var lenBuf = new byte[4];
            WriteUInt32BE(lenBuf, 0, (uint)length);
            s.Write(lenBuf, 0, 4);

            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            s.Write(typeBytes, 0, 4);

            if (length > 0)
                s.Write(data, offset, length);

            // CRC over type + data
            uint crc = Crc32Init();
            crc = Crc32Update(crc, typeBytes, 0, 4);
            if (length > 0) crc = Crc32Update(crc, data, offset, length);
            crc = Crc32Finalize(crc);
            WriteUInt32BE(s, crc);
        }

        private static void WriteUInt32BE(byte[] buf, int offset, uint value)
        {
            buf[offset]     = (byte)((value >> 24) & 0xff);
            buf[offset + 1] = (byte)((value >> 16) & 0xff);
            buf[offset + 2] = (byte)((value >>  8) & 0xff);
            buf[offset + 3] = (byte)(value & 0xff);
        }

        private static void WriteUInt32BE(Stream s, uint value)
        {
            s.WriteByte((byte)((value >> 24) & 0xff));
            s.WriteByte((byte)((value >> 16) & 0xff));
            s.WriteByte((byte)((value >>  8) & 0xff));
            s.WriteByte((byte)(value & 0xff));
        }

        // === CRC32 (PNG spec section 15) ===========================================

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32Init() => 0xFFFFFFFFu;

        private static uint Crc32Update(uint crc, byte[] buf, int offset, int length)
        {
            for (int i = 0; i < length; i++)
                crc = CrcTable[(crc ^ buf[offset + i]) & 0xff] ^ (crc >> 8);
            return crc;
        }

        private static uint Crc32Finalize(uint crc) => crc ^ 0xFFFFFFFFu;
    }
}
