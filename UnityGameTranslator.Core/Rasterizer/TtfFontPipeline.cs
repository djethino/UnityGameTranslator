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
        /// <param name="maxAtlasSize">
        /// Maximum atlas dimension (typically SystemInfo.maxTextureSize at runtime — 16384
        /// on modern PC GPUs). Pipeline never exceeds this size.
        /// </param>
        public static PipelineResult ProcessTtfFont(string ttfPath,
            float renderSize = DefaultRenderSize, float distanceRange = DefaultDistanceRange,
            int maxAtlasSize = 8192)
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

                // Step 5: Pack atlas(es). The packer returns one entry if everything fits,
                // and N entries (multi-atlas) otherwise. Each RasterizedGlyph now carries
                // an AtlasIndex pointing to its atlas in this list.
                TranslatorCore.LogInfo($"[TtfPipeline] Packing atlas (max {maxAtlasSize}x{maxAtlasSize})...");
                var atlasResults = AtlasPacker.PackAtlases(rasterizedGlyphs, padding: 1, maxAtlasSize: maxAtlasSize);

                if (atlasResults.Count == 1)
                    TranslatorCore.LogInfo($"[TtfPipeline] Atlas: {atlasResults[0].Width}x{atlasResults[0].Height} (single)");
                else
                    TranslatorCore.LogInfo($"[TtfPipeline] Multi-atlas: {atlasResults.Count} × {atlasResults[0].Width}x{atlasResults[0].Height}");

                // Glyphs that couldn't fit anywhere (single glyph larger than maxAtlasSize)
                // were tagged with AtlasIndex = -1 by the packer. Drop them explicitly here.
                int droppedCount = 0;
                for (int i = rasterizedGlyphs.Count - 1; i >= 0; i--)
                {
                    if (rasterizedGlyphs[i].AtlasIndex < 0)
                    {
                        droppedCount++;
                        rasterizedGlyphs.RemoveAt(i);
                        if (i < glyphOutlines.Count) glyphOutlines.RemoveAt(i);
                    }
                }
                if (droppedCount > 0)
                {
                    TranslatorCore.LogWarning($"[TtfPipeline] Dropped {droppedCount} glyphs that exceed {maxAtlasSize}x{maxAtlasSize} single-glyph bounds.");
                }

                // Step 6: Generate MsdfAtlasData
                var atlasData = GenerateAtlasData(parser, rasterizedGlyphs, glyphOutlines,
                    atlasResults, renderSize, distanceRange);

                TranslatorCore.LogInfo($"[TtfPipeline] Pipeline complete: {fontName}, " +
                    $"{atlasData.glyphs.Count} glyphs across {atlasResults.Count} atlas(es)");

                // Convert each AtlasResult to an AtlasBuffer for the PipelineResult.
                var buffers = new List<AtlasBuffer>(atlasResults.Count);
                foreach (var ar in atlasResults)
                {
                    buffers.Add(new AtlasBuffer { Rgba = ar.RgbaData, Width = ar.Width, Height = ar.Height });
                }

                return new PipelineResult
                {
                    AtlasData = atlasData,
                    Atlases = buffers
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
        /// Multi-atlas aware: each GlyphInfo carries its atlasIndex and its atlasBounds
        /// are computed against the height of its OWN atlas (since each atlas can in
        /// principle have a different height — they don't currently, but the math is
        /// stable either way).
        /// </summary>
        private static CustomFontLoader.MsdfAtlasData GenerateAtlasData(TtfParser parser,
            List<RasterizedGlyph> rasterizedGlyphs, List<GlyphOutline> outlines,
            List<AtlasResult> atlases, float renderSize, float distanceRange)
        {
            var metrics = parser.Metrics;
            float upm = metrics.UnitsPerEm;

            var glyphInfos = new List<CustomFontLoader.GlyphInfo>();

            for (int i = 0; i < rasterizedGlyphs.Count; i++)
            {
                var rg = rasterizedGlyphs[i];
                var outline = i < outlines.Count ? outlines[i] : null;

                int atlasIdx = rg.AtlasIndex;
                if (atlasIdx < 0 || atlasIdx >= atlases.Count) atlasIdx = 0;
                var atlas = atlases[atlasIdx];

                var glyphInfo = new CustomFontLoader.GlyphInfo
                {
                    unicode = rg.Unicode,
                    advance = rg.AdvanceWidth / upm,
                    atlasIndex = atlasIdx
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

                    // atlasBounds in pixel coordinates (yOrigin = "bottom") of THIS glyph's atlas
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

            // Build atlases[] list and mirror atlases[0] into the legacy `atlas` field
            // so older code paths still see a sensible single-atlas snapshot.
            var atlasInfos = new List<CustomFontLoader.AtlasInfo>(atlases.Count);
            foreach (var a in atlases)
            {
                atlasInfos.Add(new CustomFontLoader.AtlasInfo
                {
                    type = "sdf",
                    distanceRange = distanceRange,
                    size = renderSize,
                    width = a.Width,
                    height = a.Height,
                    yOrigin = "bottom"
                });
            }

            return new CustomFontLoader.MsdfAtlasData
            {
                atlas = atlasInfos[0],
                atlases = atlasInfos,
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

        #region Cache (removed)

        // Historically this region exposed SaveCache / TryLoadCache, which dumped the
        // rasterized RGBA atlas alongside the TTF as raw bytes (no compression). At
        // 16384x16384x4 = 1 GB per font asset, this dominated the mod's on-disk
        // footprint for users with CJK fallbacks. The cache has been replaced with the
        // PNG-compressed .gen.png + .gen.json pair written by CustomFontLoader after
        // the texture round-trip, which is ~14x smaller (50–80 MB) and uses the same
        // codepath as user-provided JSON+PNG fonts at reload time. The migration code
        // in CustomFontLoader.PurgeLegacyRasterCache deletes the orphan .cache.* files.

        #endregion
    }

    /// <summary>
    /// One atlas texture buffer (RGBA bytes + dimensions).
    /// </summary>
    public class AtlasBuffer
    {
        public byte[] Rgba;
        public int Width;
        public int Height;
    }

    /// <summary>
    /// Result from the TTF pipeline. Always has at least one entry in Atlases.
    /// The legacy RgbaPixels/Width/Height properties expose the FIRST atlas for older
    /// call-sites that only knew about one — new code should iterate over Atlases and
    /// rely on each glyph's atlasIndex.
    /// </summary>
    public class PipelineResult
    {
        public CustomFontLoader.MsdfAtlasData AtlasData;
        public List<AtlasBuffer> Atlases;

        // Legacy single-atlas accessors. They mirror Atlases[0] for callers that haven't
        // been migrated yet. Settable so the cache loader can populate them directly when
        // reading a legacy (pre multi-atlas) .cache.png file.
        public byte[] RgbaPixels
        {
            get => (Atlases != null && Atlases.Count > 0) ? Atlases[0].Rgba : null;
            set
            {
                EnsureFirstAtlas();
                Atlases[0].Rgba = value;
            }
        }

        public int Width
        {
            get => (Atlases != null && Atlases.Count > 0) ? Atlases[0].Width : 0;
            set
            {
                EnsureFirstAtlas();
                Atlases[0].Width = value;
            }
        }

        public int Height
        {
            get => (Atlases != null && Atlases.Count > 0) ? Atlases[0].Height : 0;
            set
            {
                EnsureFirstAtlas();
                Atlases[0].Height = value;
            }
        }

        private void EnsureFirstAtlas()
        {
            if (Atlases == null) Atlases = new List<AtlasBuffer>();
            if (Atlases.Count == 0) Atlases.Add(new AtlasBuffer());
        }
    }
}
