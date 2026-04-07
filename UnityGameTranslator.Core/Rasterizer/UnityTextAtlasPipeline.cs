using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Builds a bitmap font atlas from a TTF file for Unity legacy UI.Text.
    /// Produces CharacterInfo-compatible data that can be applied to a Font
    /// created via new Font() (dynamic=false, bitmap mode).
    ///
    /// In bitmap mode, Unity does NOT call RequestCharactersInTexture internally,
    /// the atlas is never auto-regenerated, and there's no FreeType sharing.
    /// This eliminates all atlas corruption issues from Instantiate clones.
    /// </summary>
    public static class UnityTextAtlasPipeline
    {
        /// <summary>
        /// Result of the pipeline — everything needed to create a bitmap font.
        /// </summary>
        public class Result
        {
            public byte[] RgbaData;
            public int AtlasWidth, AtlasHeight;
            public int CharCount;
            public int[] Indices;
            public int[] Advances;
            public float[] UvL, UvR, UvT, UvB;
            public int[] MinXs, MaxXs, MinYs, MaxYs;
            public int[] GlyphWs, GlyphHs;
            public HashSet<char> RenderedChars;
        }

        /// <summary>
        /// Build a bitmap atlas for the given chars from a TTF file.
        /// </summary>
        /// <param name="ttfPath">Path to the replacement font TTF</param>
        /// <param name="chars">Set of chars to rasterize</param>
        /// <param name="renderSize">Pixel size for rasterization</param>
        /// <returns>Atlas result or null on failure</returns>
        public static Result BuildAtlas(string ttfPath, HashSet<char> chars, float renderSize = 32f)
        {
            if (string.IsNullOrEmpty(ttfPath) || chars == null || chars.Count == 0)
                return null;

            try
            {
                var fontData = System.IO.File.ReadAllBytes(ttfPath);
                var parser = new TtfParser(fontData);
                var metrics = parser.Metrics;

                // Rasterize each char that the TTF supports
                int padding = 1;
                var glyphs = new List<RasterizedGlyph>();
                var renderedChars = new HashSet<char>();

                foreach (char c in chars)
                {
                    if (!parser.HasCodepoint(c))
                        continue;

                    var outline = parser.GetGlyphOutline(c);
                    if (outline == null)
                        continue;

                    var rasterized = GlyphRasterizer.Rasterize(outline, metrics, renderSize, padding);
                    if (rasterized != null)
                    {
                        glyphs.Add(rasterized);
                        renderedChars.Add(c);
                    }
                }

                // Add space manually if not rasterized (no contours but has advance)
                if (!renderedChars.Contains(' ') && parser.HasCodepoint(' '))
                {
                    var spaceOutline = parser.GetGlyphOutline(' ');
                    if (spaceOutline != null)
                    {
                        glyphs.Add(new RasterizedGlyph
                        {
                            Unicode = 32,
                            Bitmap = null,
                            Width = 0,
                            Height = 0,
                            AdvanceWidth = spaceOutline.AdvanceWidth,
                            BearingX = 0,
                            BearingY = 0
                        });
                        renderedChars.Add(' ');
                    }
                }

                if (glyphs.Count == 0)
                    return null;

                // Pack atlas (BitmapAlpha mode: RGB=white, A=coverage)
                var atlas = PackAtlasBitmapAlpha(glyphs, padding);

                // Build CharacterInfo arrays
                float scale = renderSize / metrics.UnitsPerEm;
                int count = glyphs.Count;

                var result = new Result
                {
                    RgbaData = atlas.RgbaData,
                    AtlasWidth = atlas.Width,
                    AtlasHeight = atlas.Height,
                    CharCount = count,
                    Indices = new int[count],
                    Advances = new int[count],
                    UvL = new float[count],
                    UvR = new float[count],
                    UvT = new float[count],
                    UvB = new float[count],
                    MinXs = new int[count],
                    MaxXs = new int[count],
                    MinYs = new int[count],
                    MaxYs = new int[count],
                    GlyphWs = new int[count],
                    GlyphHs = new int[count],
                    RenderedChars = renderedChars
                };

                for (int i = 0; i < count; i++)
                {
                    var g = glyphs[i];
                    result.Indices[i] = g.Unicode;
                    result.Advances[i] = (int)Math.Round(g.AdvanceWidth * scale);

                    if (g.Width > 0 && g.Height > 0)
                    {
                        // UV coordinates (normalized 0-1)
                        // No texture flip — atlas is fed directly to SetPixels32.
                        // Atlas top (AtlasY=0) is at texture pixel[0] = Unity UV Y=0 (bottom).
                        // To render glyphs right-side-up, uvT < uvB (inverted Y sampling):
                        // - uvT maps to the TOP of the rendered quad = top of original glyph = AtlasY
                        // - uvB maps to the BOTTOM of the quad = bottom of glyph = AtlasY+Height
                        result.UvL[i] = (float)g.AtlasX / atlas.Width;
                        result.UvR[i] = (float)(g.AtlasX + g.Width) / atlas.Width;
                        result.UvT[i] = (float)g.AtlasY / atlas.Height;
                        result.UvB[i] = (float)(g.AtlasY + g.Height) / atlas.Height;

                        // Glyph bounds in pixels (for text layout positioning)
                        int innerW = g.Width - padding * 2;
                        int innerH = g.Height - padding * 2;
                        result.MinXs[i] = (int)Math.Floor(g.BearingX * scale);
                        result.MaxXs[i] = result.MinXs[i] + innerW;
                        result.MinYs[i] = (int)Math.Floor(g.BearingY * scale) - innerH;
                        result.MaxYs[i] = (int)Math.Floor(g.BearingY * scale);
                        result.GlyphWs[i] = innerW;
                        result.GlyphHs[i] = innerH;
                    }
                }

                TranslatorCore.LogInfo($"[UnityTextAtlas] Built atlas {atlas.Width}x{atlas.Height} with {count} glyphs from '{System.IO.Path.GetFileName(ttfPath)}'");
                return result;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[UnityTextAtlas] BuildAtlas failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pack glyphs into RGBA atlas with BitmapAlpha format (RGB=255, A=coverage).
        /// Unity's font shader uses alpha as glyph coverage.
        /// </summary>
        private static AtlasResult PackAtlasBitmapAlpha(List<RasterizedGlyph> glyphs, int padding)
        {
            // Use the existing AtlasPacker for layout
            var atlas = AtlasPacker.PackAtlas(glyphs, padding);

            // Convert to BitmapAlpha format: RGB=white, A=coverage
            var rgba = new byte[atlas.Width * atlas.Height * 4];

            // Init to transparent white
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = 255;     // R
                rgba[i + 1] = 255; // G
                rgba[i + 2] = 255; // B
                rgba[i + 3] = 0;   // A = transparent
            }

            // Blit glyphs
            foreach (var glyph in glyphs)
            {
                if (glyph.Bitmap == null || glyph.Width == 0 || glyph.Height == 0)
                    continue;

                for (int gy = 0; gy < glyph.Height; gy++)
                {
                    for (int gx = 0; gx < glyph.Width; gx++)
                    {
                        int atlasX = glyph.AtlasX + gx;
                        int atlasY = glyph.AtlasY + gy;

                        if (atlasX >= atlas.Width || atlasY >= atlas.Height)
                            continue;

                        int atlasIdx = (atlasY * atlas.Width + atlasX) * 4;
                        byte value = glyph.Bitmap[gy * glyph.Width + gx];

                        rgba[atlasIdx] = 255;      // R = white
                        rgba[atlasIdx + 1] = 255;  // G = white
                        rgba[atlasIdx + 2] = 255;  // B = white
                        rgba[atlasIdx + 3] = value; // A = coverage
                    }
                }
            }

            return new AtlasResult { RgbaData = rgba, Width = atlas.Width, Height = atlas.Height };
        }
    }
}
