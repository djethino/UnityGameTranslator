using System;
using System.IO;
using System.IO.Compression;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Disk cache for rasterized font atlases: the raw Alpha8 pixels, DEFLATE-compressed.
    ///
    /// Why not PNG, which we also know how to write? Because of what it costs to READ back.
    /// Decoding a cached 8192² PNG goes through UnityEngine's ImageConversion.LoadImage, which
    /// must run on the main thread and was measured at 23.5 SECONDS — the entire startup freeze.
    /// Uploading the same atlas from raw bytes takes 94 ms, and inflating them is plain .NET that
    /// can run off the main thread.
    ///
    /// Compression is NOT optional here: an uncompressed atlas is 67 MB at 8192² and 256 MB at
    /// 16384². An early version of the mod cached raw bytes and reached ~1 GB per font on disk,
    /// which is precisely why PNG was adopted. DEFLATE keeps the file in the same ballpark as the
    /// PNG (a few MB) while leaving the decode in our hands.
    /// </summary>
    internal static class RawAtlasCache
    {
        // "UGTA" — refuse to read anything that is not ours rather than trusting the extension
        private static readonly byte[] Magic = { (byte)'U', (byte)'G', (byte)'T', (byte)'A' };
        private const byte FormatVersion = 1;
        private const byte PixelAlpha8 = 1;

        /// <summary>Cache file for an atlas, alongside the .gen.json (single atlas keeps the bare name).</summary>
        public static string BuildPath(string cacheDir, string fontName, int atlasIndex, int atlasCount)
        {
            string suffix = atlasCount == 1 ? ".gen.bin" : $".gen.atlas{atlasIndex}.bin";
            return Path.Combine(cacheDir, fontName + suffix);
        }

        /// <summary>
        /// Write Alpha8 pixels (Unity bottom-up order, as handed to LoadRawTextureData).
        /// Returns false on any failure — the caller keeps the in-memory texture and simply
        /// re-rasterizes next time, which is far cheaper than a broken cache.
        /// </summary>
        public static bool Write(string path, byte[] alphaPixels, int width, int height)
        {
            if (string.IsNullOrEmpty(path) || alphaPixels == null || width <= 0 || height <= 0)
                return false;

            try
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    file.Write(Magic, 0, Magic.Length);
                    file.WriteByte(FormatVersion);
                    file.WriteByte(PixelAlpha8);
                    WriteInt32(file, width);
                    WriteInt32(file, height);

                    // Streamed, so the compressed copy never exists as one more huge array
                    using (var deflate = new DeflateStream(file, CompressionMode.Compress, leaveOpen: true))
                    {
                        deflate.Write(alphaPixels, 0, alphaPixels.Length);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[RawAtlasCache] Write failed for {Sanitize.Path(path)}: {ex.Message}");
                TryDelete(path); // never leave a truncated file behind, it would be read next launch
                return false;
            }
        }

        /// <summary>
        /// Read Alpha8 pixels back. Pure .NET — safe to call from a background thread, which is
        /// the whole point of this format. Returns null when the file is absent, foreign, from a
        /// newer version, or does not match the expected dimensions.
        /// </summary>
        public static byte[] Read(string path, int expectedWidth, int expectedHeight)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    for (int i = 0; i < Magic.Length; i++)
                    {
                        if (file.ReadByte() != Magic[i])
                        {
                            TranslatorCore.LogWarning($"[RawAtlasCache] {Path.GetFileName(path)} is not a raw atlas cache, ignoring");
                            return null;
                        }
                    }

                    int version = file.ReadByte();
                    int pixelFormat = file.ReadByte();
                    if (version != FormatVersion || pixelFormat != PixelAlpha8)
                    {
                        TranslatorCore.LogInfo($"[RawAtlasCache] {Path.GetFileName(path)} is version {version}/format {pixelFormat}, re-rasterizing instead");
                        return null;
                    }

                    int width = ReadInt32(file);
                    int height = ReadInt32(file);
                    if (width != expectedWidth || height != expectedHeight)
                    {
                        TranslatorCore.LogWarning($"[RawAtlasCache] {Path.GetFileName(path)} is {width}x{height}, expected {expectedWidth}x{expectedHeight} — ignoring");
                        return null;
                    }

                    long expected = (long)width * height;
                    if (expected > int.MaxValue) return null;

                    var pixels = new byte[expected];
                    using (var inflate = new DeflateStream(file, CompressionMode.Decompress))
                    {
                        int read = 0;
                        while (read < pixels.Length)
                        {
                            int n = inflate.Read(pixels, read, pixels.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        if (read != pixels.Length)
                        {
                            TranslatorCore.LogWarning($"[RawAtlasCache] {Path.GetFileName(path)} is truncated ({read}/{pixels.Length} bytes) — ignoring");
                            return null;
                        }
                    }
                    return pixels;
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[RawAtlasCache] Read failed for {Sanitize.Path(path)}: {ex.Message}");
                return null;
            }
        }

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void WriteInt32(Stream s, int value)
        {
            s.WriteByte((byte)value);
            s.WriteByte((byte)(value >> 8));
            s.WriteByte((byte)(value >> 16));
            s.WriteByte((byte)(value >> 24));
        }

        private static int ReadInt32(Stream s)
        {
            return s.ReadByte() | (s.ReadByte() << 8) | (s.ReadByte() << 16) | (s.ReadByte() << 24);
        }
    }
}
