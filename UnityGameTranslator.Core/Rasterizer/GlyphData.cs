using System.Collections.Generic;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// A point on a glyph outline contour, with on-curve/off-curve flag.
    /// Coordinates are in font units (unitsPerEm space).
    /// </summary>
    public struct ContourPoint
    {
        public float X;
        public float Y;
        public bool OnCurve;
        public bool IsCubic; // true = cubic Bezier control point (CFF), false = quadratic (TrueType)

        public ContourPoint(float x, float y, bool onCurve, bool isCubic = false)
        {
            X = x;
            Y = y;
            OnCurve = onCurve;
            IsCubic = isCubic;
        }
    }

    /// <summary>
    /// A single contour (closed path) of a glyph outline.
    /// </summary>
    public class GlyphContour
    {
        public ContourPoint[] Points;
    }

    /// <summary>
    /// Complete outline data for a single glyph, extracted from the TTF.
    /// </summary>
    public class GlyphOutline
    {
        public int Unicode;
        public int GlyphIndex;
        public GlyphContour[] Contours;
        public float AdvanceWidth;
        public float LeftSideBearing;
        public int XMin, YMin, XMax, YMax;
        public bool IsEmpty; // true for space-like glyphs (no contours)
    }

    /// <summary>
    /// Font-level metrics extracted from TTF tables.
    /// </summary>
    public class FontMetrics
    {
        public int UnitsPerEm;
        public float Ascender;
        public float Descender; // Negative value
        public float LineGap;
        public float UnderlinePosition;
        public float UnderlineThickness;
        public string FontName;
    }

    /// <summary>
    /// A rasterized glyph bitmap ready for atlas packing.
    /// </summary>
    public class RasterizedGlyph
    {
        public int Unicode;
        public byte[] Bitmap; // Grayscale SDF values, 1 byte per pixel
        public int Width;
        public int Height;
        public float AdvanceWidth; // In font units
        public float BearingX;    // In font units (left side bearing)
        public float BearingY;    // In font units (top bearing = yMax from baseline)

        // Atlas placement (filled by AtlasPacker)
        public int AtlasX;
        public int AtlasY;

        // Index into the AtlasResult list (0 = first/only atlas). Filled by AtlasPacker.
        // Used by multi-atlas mode when a font has too many glyphs to fit one texture.
        public int AtlasIndex;
    }
}
