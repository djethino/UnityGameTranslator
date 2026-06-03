using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Packs rasterized SDF glyph bitmaps into a single power-of-2 texture atlas
    /// using shelf (row-based) packing. Sorts glyphs by height for optimal packing.
    /// </summary>
    public static class AtlasPacker
    {
        /// <summary>
        /// Pack rasterized glyphs into an RGBA atlas.
        /// Sets AtlasX/AtlasY on each glyph.
        /// Returns RGBA pixel data (4 bytes/pixel: R=G=B=255, A=SDF value).
        /// </summary>
        /// <param name="glyphs">Glyphs with SDF bitmaps to pack</param>
        /// <param name="padding">Pixels between glyphs to prevent bleeding</param>
        /// <param name="maxAtlasSize">
        /// Maximum power-of-2 atlas dimension. Should be SystemInfo.maxTextureSize at runtime
        /// (typically 16384 on modern PC GPUs). The packer never exceeds this limit.
        /// </param>
        public static AtlasResult PackAtlas(List<RasterizedGlyph> glyphs, int padding = 1, int maxAtlasSize = 8192)
        {
            // Backward-compatible single-atlas wrapper. Returns the first atlas only;
            // any overflow glyphs keep AtlasIndex > 0 but are silently dropped from
            // the returned buffer. Callers that need full coverage must use PackAtlases.
            var all = PackAtlases(glyphs, padding, maxAtlasSize);
            return all[0];
        }

        /// <summary>
        /// Pack glyphs into one or more RGBA atlases, never exceeding maxAtlasSize on any
        /// dimension. Returns at least one AtlasResult. Each glyph receives AtlasX/AtlasY/
        /// AtlasIndex pointing to its atlas in the returned list.
        ///
        /// Strategy:
        ///  1. Try a single power-of-2 atlas that fits ALL glyphs (smallest possible) —
        ///     this preserves the legacy single-atlas behaviour for small fonts.
        ///  2. If no single atlas at maxAtlasSize fits everything, fall back to a sequence
        ///     of (maxAtlasSize × maxAtlasSize) atlases, packing glyphs greedily until each
        ///     one is full, then opening a new one.
        ///
        /// Glyphs are always sorted by height descending before packing (shelf-pack optimum).
        /// </summary>
        public static List<AtlasResult> PackAtlases(List<RasterizedGlyph> glyphs, int padding = 1, int maxAtlasSize = 8192)
        {
            var results = new List<AtlasResult>();

            if (glyphs == null || glyphs.Count == 0)
            {
                results.Add(new AtlasResult { RgbaData = new byte[4], Width = 1, Height = 1 });
                return results;
            }

            // Filter out empty glyphs (spaces etc.) and pre-clear placement state in case
            // the same glyph list is reused across runs.
            var packable = new List<RasterizedGlyph>();
            foreach (var g in glyphs)
            {
                if (g.Bitmap != null && g.Width > 0 && g.Height > 0)
                {
                    g.AtlasX = 0;
                    g.AtlasY = 0;
                    g.AtlasIndex = 0;
                    packable.Add(g);
                }
            }

            if (packable.Count == 0)
            {
                results.Add(new AtlasResult { RgbaData = new byte[4], Width = 1, Height = 1 });
                return results;
            }

            // Sort by height descending (better shelf packing)
            packable.Sort((a, b) => b.Height.CompareTo(a.Height));

            // === Step 1: try single-atlas at the smallest size that fits everything ===
            int singleW, singleH;
            FindAtlasSize(packable, padding, maxAtlasSize, out singleW, out singleH);
            if (TryShelfPack(packable, singleW, singleH, padding))
            {
                ShelfPack(packable, singleW, singleH, padding);
                foreach (var g in packable) g.AtlasIndex = 0;
                results.Add(BlitAtlas(packable, singleW, singleH));
                return results;
            }

            // === Step 2: multi-atlas at maxAtlasSize × maxAtlasSize ===
            // Each pass: place as many of the remaining glyphs as the current empty atlas can
            // hold (still in height-desc order so shelf packing stays near-optimal), then move
            // the rest to the next atlas.
            int atlasW = NextPowerOf2(Math.Max(128, maxAtlasSize));
            int atlasH = atlasW;
            int atlasIndex = 0;
            int placed = 0;

            while (placed < packable.Count)
            {
                int fitted = ShelfPackPartial(packable, placed, atlasW, atlasH, padding);
                if (fitted == 0)
                {
                    // A single glyph is larger than the maximum atlas dimension. We can't
                    // place it anywhere; drop it (and any following) so we don't infinite-loop.
                    // Leave it with AtlasIndex = -1 so the pipeline can skip it cleanly.
                    for (int i = placed; i < packable.Count; i++)
                        packable[i].AtlasIndex = -1;
                    break;
                }

                // Tag everything just placed with this atlas's index, then blit.
                for (int i = placed; i < placed + fitted; i++)
                    packable[i].AtlasIndex = atlasIndex;

                results.Add(BlitAtlas(packable, atlasW, atlasH, placed, fitted));

                placed += fitted;
                atlasIndex++;
            }

            return results;
        }

        /// <summary>
        /// Allocate an RGBA buffer, init it to opaque-black (SDF=0 outside), and blit each
        /// glyph's bitmap into it using its AtlasX/AtlasY coordinates.
        /// rangeStart/rangeCount let the caller restrict the blit to a sub-slice of the list
        /// (used by multi-atlas mode where each atlas only owns a contiguous slice).
        /// </summary>
        private static AtlasResult BlitAtlas(List<RasterizedGlyph> glyphs, int atlasW, int atlasH,
            int rangeStart = 0, int rangeCount = -1)
        {
            var rgba = new byte[atlasW * atlasH * 4];

            // SDF in grayscale RGB with A=255 (same format as msdf-atlas-gen).
            // ConvertSdfTextureForTMP will copy R to Alpha for TMP shader.
            // Init to black (SDF=0 = far outside) with A=255
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = 0;
                rgba[i + 1] = 0;
                rgba[i + 2] = 0;
                rgba[i + 3] = 255;
            }

            int end = rangeCount < 0 ? glyphs.Count : Math.Min(glyphs.Count, rangeStart + rangeCount);
            for (int gi = rangeStart; gi < end; gi++)
            {
                var glyph = glyphs[gi];
                for (int gy = 0; gy < glyph.Height; gy++)
                {
                    for (int gx = 0; gx < glyph.Width; gx++)
                    {
                        int atlasX = glyph.AtlasX + gx;
                        int atlasY = glyph.AtlasY + gy;

                        if (atlasX >= atlasW || atlasY >= atlasH)
                            continue;

                        int atlasIdx = (atlasY * atlasW + atlasX) * 4;
                        byte sdfValue = glyph.Bitmap[gy * glyph.Width + gx];

                        rgba[atlasIdx] = sdfValue;
                        rgba[atlasIdx + 1] = sdfValue;
                        rgba[atlasIdx + 2] = sdfValue;
                        rgba[atlasIdx + 3] = 255;
                    }
                }
            }

            return new AtlasResult { RgbaData = rgba, Width = atlasW, Height = atlasH };
        }

        /// <summary>
        /// Place glyphs from index `startIndex` onward into an atlas of size (atlasW, atlasH).
        /// Stops as soon as a glyph doesn't fit (it stays unplaced) and returns the number of
        /// glyphs that WERE placed. Modifies AtlasX/AtlasY on placed glyphs; leaves unplaced
        /// glyphs untouched (their AtlasIndex is set by the caller for the next atlas).
        /// </summary>
        private static int ShelfPackPartial(List<RasterizedGlyph> glyphs, int startIndex,
            int atlasW, int atlasH, int padding)
        {
            int shelfX = 0;
            int shelfY = 0;
            int shelfHeight = 0;
            int placed = 0;

            for (int i = startIndex; i < glyphs.Count; i++)
            {
                var g = glyphs[i];
                int gw = g.Width + padding;
                int gh = g.Height + padding;

                if (shelfX + gw > atlasW)
                {
                    shelfX = 0;
                    shelfY += shelfHeight;
                    shelfHeight = 0;
                }

                if (shelfY + gh > atlasH)
                    break; // Atlas is full, the rest goes to the next atlas

                g.AtlasX = shelfX;
                g.AtlasY = shelfY;

                shelfX += gw;
                if (gh > shelfHeight)
                    shelfHeight = gh;

                placed++;
            }

            return placed;
        }

        /// <summary>
        /// Find the smallest power-of-2 atlas size that fits all glyphs.
        /// Never exceeds maxAtlasSize on either dimension.
        /// </summary>
        private static void FindAtlasSize(List<RasterizedGlyph> glyphs, int padding, int maxAtlasSize,
            out int width, out int height)
        {
            // Estimate total area needed
            long totalArea = 0;
            int maxGlyphW = 0;
            int maxGlyphH = 0;

            foreach (var g in glyphs)
            {
                int pw = g.Width + padding;
                int ph = g.Height + padding;
                totalArea += pw * ph;
                if (pw > maxGlyphW) maxGlyphW = pw;
                if (ph > maxGlyphH) maxGlyphH = ph;
            }

            // Start with the smallest power-of-2 that could fit the area
            // and is at least as wide/tall as the largest glyph
            int minDim = Math.Max(maxGlyphW, maxGlyphH);
            int startSize = NextPowerOf2(Math.Max(minDim, (int)Math.Sqrt(totalArea)));

            // Build power-of-2 size ladder up to maxAtlasSize
            int cap = NextPowerOf2(Math.Max(128, maxAtlasSize));
            var sizes = new List<int>();
            for (int s = 128; s <= cap; s *= 2)
                sizes.Add(s);

            foreach (int size in sizes)
            {
                if (size < startSize) continue;

                // Try square first
                if (TryShelfPack(glyphs, size, size, padding))
                {
                    width = size;
                    height = size;
                    return;
                }

                // Try wider (only if doubling stays under the cap)
                if (size * 2 <= cap && TryShelfPack(glyphs, size * 2, size, padding))
                {
                    width = size * 2;
                    height = size;
                    return;
                }
            }

            // Fallback: maximum size. Caller is expected to detect overflow via
            // glyphs.Any(g => g.AtlasY + g.Height > height) and recover (multi-atlas
            // or explicit drop with logging) — silent skip would produce out-of-bounds
            // atlasBounds and wrap-around UV sampling.
            width = cap;
            height = cap;
        }

        /// <summary>
        /// Test if glyphs fit in the given atlas dimensions using shelf packing.
        /// Does not modify glyph positions.
        /// </summary>
        private static bool TryShelfPack(List<RasterizedGlyph> glyphs, int atlasW, int atlasH, int padding)
        {
            int shelfX = 0;
            int shelfY = 0;
            int shelfHeight = 0;

            foreach (var g in glyphs)
            {
                int gw = g.Width + padding;
                int gh = g.Height + padding;

                if (shelfX + gw > atlasW)
                {
                    // Start new shelf
                    shelfX = 0;
                    shelfY += shelfHeight;
                    shelfHeight = 0;
                }

                if (shelfY + gh > atlasH)
                    return false; // Doesn't fit

                shelfX += gw;
                if (gh > shelfHeight)
                    shelfHeight = gh;
            }

            return true;
        }

        /// <summary>
        /// Place glyphs using shelf packing. Sets AtlasX/AtlasY on each glyph.
        /// </summary>
        private static void ShelfPack(List<RasterizedGlyph> glyphs, int atlasW, int atlasH, int padding)
        {
            int shelfX = 0;
            int shelfY = 0;
            int shelfHeight = 0;

            foreach (var g in glyphs)
            {
                int gw = g.Width + padding;
                int gh = g.Height + padding;

                if (shelfX + gw > atlasW)
                {
                    shelfX = 0;
                    shelfY += shelfHeight;
                    shelfHeight = 0;
                }

                g.AtlasX = shelfX;
                g.AtlasY = shelfY;

                shelfX += gw;
                if (gh > shelfHeight)
                    shelfHeight = gh;
            }
        }

        private static int NextPowerOf2(int v)
        {
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v++;
            return Math.Max(v, 1);
        }
    }

    /// <summary>
    /// Result of atlas packing.
    /// </summary>
    public class AtlasResult
    {
        public byte[] RgbaData;
        public int Width;
        public int Height;
    }
}
