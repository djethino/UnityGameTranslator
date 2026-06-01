using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Orchestrates the TTF → SDF atlas pipeline.
    /// Reads a TTF/OTF file, rasterizes all glyphs, generates SDF, packs into atlas,
    /// and produces MsdfAtlasData + RGBA pixel data compatible with CustomFontLoader.
    /// Includes caching to avoid re-rasterizing on subsequent loads.
    /// </summary>
    public static class TtfFontPipeline
    {
        /// <summary>
        /// Default render size in pixels for SDF rasterization.
        /// Higher = better quality but larger atlas. 32px is a good balance.
        /// </summary>
        public const float DefaultRenderSize = 48f;

        /// <summary>
        /// Default SDF spread in pixels. Must match the distanceRange used by TMP shaders.
        /// </summary>
        public const float DefaultDistanceRange = 8f;

        /// <summary>
        /// Process a TTF/OTF file and generate atlas data compatible with CustomFontLoader.
        /// </summary>
        /// <param name="ttfPath">Path to the .ttf or .otf file</param>
        /// <param name="renderSize">Pixel size for rasterization</param>
        /// <param name="distanceRange">SDF spread in pixels</param>
        /// <returns>Pipeline result with atlas data and pixel buffer, or null on failure</returns>
        public static PipelineResult ProcessTtfFont(string ttfPath,
            float renderSize = DefaultRenderSize, float distanceRange = DefaultDistanceRange)
        {
            if (!File.Exists(ttfPath))
            {
                TranslatorCore.LogWarning($"[TtfPipeline] File not found: {ttfPath}");
                return null;
            }

            string fontName = Path.GetFileNameWithoutExtension(ttfPath);
            TranslatorCore.LogInfo($"[TtfPipeline] Processing: {fontName}");

            try
            {
                // Step 1: Parse TTF
                TranslatorCore.LogInfo($"[TtfPipeline] Parsing TTF...");
                var fontData = File.ReadAllBytes(ttfPath);
                var parser = new TtfParser(fontData);

                TranslatorCore.LogInfo($"[TtfPipeline] Font: {parser.Metrics.FontName}, " +
                    $"UPM: {parser.Metrics.UnitsPerEm}, Glyphs: {parser.GlyphCount}");

                // Step 2: Get all codepoints. We process every codepoint the font
                // exposes — no arbitrary cap. Any downstream constraint (atlas
                // texture size, GPU max texture, PNG encoding limit, etc.) is the
                // responsibility of the rasterizer / atlas packer to surface
                // explicitly rather than silently dropping glyphs picked at
                // random by Dictionary iteration order.
                var codepoints = parser.GetSupportedCodepoints();
                TranslatorCore.LogInfo($"[TtfPipeline] Mapped codepoints: {codepoints.Length}");

                int sdfPadding = (int)Math.Ceiling(distanceRange) + 1;

                // Step 3: Rasterize all glyphs
                TranslatorCore.LogInfo($"[TtfPipeline] Rasterizing {codepoints.Length} glyphs at {renderSize}px...");
                var rasterizedGlyphs = new List<RasterizedGlyph>();
                var glyphOutlines = new List<GlyphOutline>(); // Keep for metadata generation
                int rasterizedCount = 0;
                int emptyCount = 0;
                int failCount = 0;

                for (int i = 0; i < codepoints.Length; i++)
                {
                    var outline = parser.GetGlyphOutline(codepoints[i]);
                    if (outline == null)
                    {
                        failCount++;
                        continue;
                    }

                    var rasterized = GlyphRasterizer.Rasterize(outline, parser.Metrics,
                        renderSize, sdfPadding);

                    if (rasterized == null)
                    {
                        failCount++;
                        continue;
                    }

                    if (rasterized.Bitmap == null || rasterized.Width == 0)
                    {
                        // Empty glyph (space, etc.) — keep for metrics
                        emptyCount++;
                        rasterized.Unicode = codepoints[i];
                        rasterizedGlyphs.Add(rasterized);
                        glyphOutlines.Add(outline);
                        continue;
                    }

                    // Step 4: Generate SDF for this glyph
                    var sdfBitmap = SdfGenerator.GenerateSdf(rasterized.Bitmap,
                        rasterized.Width, rasterized.Height, distanceRange);

                    if (sdfBitmap != null)
                    {
                        rasterized.Bitmap = sdfBitmap;
                    }

                    rasterized.Unicode = codepoints[i];
                    rasterizedGlyphs.Add(rasterized);
                    glyphOutlines.Add(outline);
                    rasterizedCount++;

                    // Progress logging for large fonts
                    if (i > 0 && i % 5000 == 0)
                    {
                        TranslatorCore.LogInfo($"[TtfPipeline] Progress: {i}/{codepoints.Length} glyphs...");
                    }
                }

                TranslatorCore.LogInfo($"[TtfPipeline] Rasterized: {rasterizedCount}, " +
                    $"Empty: {emptyCount}, Failed: {failCount}");

                // Step 5: Pack atlas
                TranslatorCore.LogInfo($"[TtfPipeline] Packing atlas...");
                var atlasResult = AtlasPacker.PackAtlas(rasterizedGlyphs);

                TranslatorCore.LogInfo($"[TtfPipeline] Atlas: {atlasResult.Width}x{atlasResult.Height}");

                // Step 6: Generate MsdfAtlasData
                var atlasData = GenerateAtlasData(parser, rasterizedGlyphs, glyphOutlines,
                    atlasResult, renderSize, distanceRange);

                TranslatorCore.LogInfo($"[TtfPipeline] Pipeline complete: {fontName}, " +
                    $"{atlasData.glyphs.Count} glyphs, {atlasResult.Width}x{atlasResult.Height} atlas");

                return new PipelineResult
                {
                    AtlasData = atlasData,
                    RgbaPixels = atlasResult.RgbaData,
                    Width = atlasResult.Width,
                    Height = atlasResult.Height
                };
            }
            catch (NotSupportedException ex)
            {
                TranslatorCore.LogWarning($"[TtfPipeline] {fontName}: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[TtfPipeline] Failed to process {fontName}: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Generate MsdfAtlasData compatible with CustomFontLoader's existing pipeline.
        /// </summary>
        private static CustomFontLoader.MsdfAtlasData GenerateAtlasData(TtfParser parser,
            List<RasterizedGlyph> rasterizedGlyphs, List<GlyphOutline> outlines,
            AtlasResult atlas, float renderSize, float distanceRange)
        {
            var metrics = parser.Metrics;
            float upm = metrics.UnitsPerEm;

            var glyphInfos = new List<CustomFontLoader.GlyphInfo>();

            for (int i = 0; i < rasterizedGlyphs.Count; i++)
            {
                var rg = rasterizedGlyphs[i];
                var outline = i < outlines.Count ? outlines[i] : null;

                var glyphInfo = new CustomFontLoader.GlyphInfo
                {
                    unicode = rg.Unicode,
                    advance = rg.AdvanceWidth / upm
                };

                if (outline != null && !outline.IsEmpty && rg.Width > 0 && rg.Height > 0)
                {
                    // Derive planeBounds from actual bitmap dimensions to guarantee
                    // atlasBounds.width / planeBounds.width = renderSize EXACTLY.
                    float cx = (outline.XMin + outline.XMax) / (2f * upm);
                    float cy = (outline.YMin + outline.YMax) / (2f * upm);
                    float hx = rg.Width / (2f * renderSize);
                    float hy = rg.Height / (2f * renderSize);

                    glyphInfo.planeBounds = new CustomFontLoader.BoundsInfo
                    {
                        left = cx - hx,
                        bottom = cy - hy,
                        right = cx + hx,
                        top = cy + hy
                    };

                    // atlasBounds in pixel coordinates (yOrigin = "bottom")
                    glyphInfo.atlasBounds = new CustomFontLoader.BoundsInfo
                    {
                        left = rg.AtlasX,
                        bottom = atlas.Height - (rg.AtlasY + rg.Height),
                        right = rg.AtlasX + rg.Width,
                        top = atlas.Height - rg.AtlasY
                    };
                }

                glyphInfos.Add(glyphInfo);
            }

            return new CustomFontLoader.MsdfAtlasData
            {
                atlas = new CustomFontLoader.AtlasInfo
                {
                    type = "sdf",
                    distanceRange = distanceRange,
                    size = renderSize,
                    width = atlas.Width,
                    height = atlas.Height,
                    yOrigin = "bottom"
                },
                metrics = new CustomFontLoader.MetricsInfo
                {
                    emSize = upm,
                    lineHeight = (metrics.Ascender - metrics.Descender + metrics.LineGap) / upm,
                    ascender = metrics.Ascender / upm,
                    descender = metrics.Descender / upm,
                    underlineY = metrics.UnderlinePosition / upm,
                    underlineThickness = metrics.UnderlineThickness / upm
                },
                glyphs = glyphInfos,
                kerning = null // Could be added later from kern/GPOS table
            };
        }

        #region Cache

        /// <summary>
        /// Save generated atlas to cache files for faster subsequent loads.
        /// If cacheDir is specified, cache files are written there instead of alongside the TTF
        /// (needed for system fonts where the source directory is read-only).
        /// </summary>
        public static void SaveCache(string ttfPath, PipelineResult result, string cacheDir = null)
        {
            if (result == null) return;

            try
            {
                string dir = cacheDir ?? Path.GetDirectoryName(ttfPath);

                // Create cache directory if it doesn't exist
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string basePath = Path.Combine(dir, Path.GetFileNameWithoutExtension(ttfPath));

                string jsonPath = basePath + ".cache.json";
                string pngPath = basePath + ".cache.png";

                // Save atlas data as JSON
                var json = JsonConvert.SerializeObject(result.AtlasData, Formatting.None);
                File.WriteAllText(jsonPath, json);

                // Save RGBA data as raw file (we can't encode PNG without Unity, so save raw)
                // Store dimensions in a header: [width:4bytes][height:4bytes][rgba data]
                using (var fs = new FileStream(pngPath, FileMode.Create))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(result.Width);
                    bw.Write(result.Height);
                    bw.Write(result.RgbaPixels);
                }

                // Save timestamp file to track TTF modification time
                string stampPath = basePath + ".cache.stamp";
                var ttfLastWrite = File.GetLastWriteTimeUtc(ttfPath);
                File.WriteAllText(stampPath, ttfLastWrite.Ticks.ToString());

                TranslatorCore.LogInfo($"[TtfPipeline] Cache saved: {Path.GetFileName(jsonPath)}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TtfPipeline] Failed to save cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Try to load from cache. Returns null if cache is stale or missing.
        /// If cacheDir is specified, looks there instead of alongside the TTF.
        /// </summary>
        public static PipelineResult TryLoadCache(string ttfPath, string cacheDir = null)
        {
            try
            {
                string dir = cacheDir ?? Path.GetDirectoryName(ttfPath);
                string basePath = Path.Combine(dir, Path.GetFileNameWithoutExtension(ttfPath));

                string jsonPath = basePath + ".cache.json";
                string pngPath = basePath + ".cache.png";
                string stampPath = basePath + ".cache.stamp";

                if (!File.Exists(jsonPath) || !File.Exists(pngPath) || !File.Exists(stampPath))
                    return null;

                // Check if TTF has been modified since cache was created
                var ttfLastWrite = File.GetLastWriteTimeUtc(ttfPath);
                var stampText = File.ReadAllText(stampPath).Trim();
                long stampTicks;
                if (!long.TryParse(stampText, out stampTicks) || ttfLastWrite.Ticks != stampTicks)
                {
                    TranslatorCore.LogInfo($"[TtfPipeline] Cache stale for {Path.GetFileName(ttfPath)}");
                    return null;
                }

                // Load JSON
                var json = File.ReadAllText(jsonPath);
                var atlasData = JsonConvert.DeserializeObject<CustomFontLoader.MsdfAtlasData>(json);
                if (atlasData?.glyphs == null)
                    return null;

                // Load raw RGBA data
                int width, height;
                byte[] rgbaPixels;
                using (var fs = new FileStream(pngPath, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    width = br.ReadInt32();
                    height = br.ReadInt32();
                    rgbaPixels = br.ReadBytes(width * height * 4);
                }

                if (rgbaPixels.Length != width * height * 4)
                    return null;

                TranslatorCore.LogInfo($"[TtfPipeline] Loaded from cache: {Path.GetFileName(ttfPath)}, " +
                    $"{atlasData.glyphs.Count} glyphs, {width}x{height}");

                return new PipelineResult
                {
                    AtlasData = atlasData,
                    RgbaPixels = rgbaPixels,
                    Width = width,
                    Height = height
                };
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TtfPipeline] Cache load failed: {ex.Message}");
                return null;
            }
        }

        #endregion
    }

    /// <summary>
    /// Result from the TTF pipeline.
    /// </summary>
    public class PipelineResult
    {
        public CustomFontLoader.MsdfAtlasData AtlasData;
        public byte[] RgbaPixels;
        public int Width;
        public int Height;
    }
}
