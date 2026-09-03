using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Minimal TrueType font parser. Reads .ttf/.otf files and extracts
    /// glyph outlines, metrics, and unicode mapping.
    /// Only supports TrueType outlines (glyf table), not CFF/PostScript.
    /// </summary>
    public class TtfParser
    {
        public FontMetrics Metrics { get; private set; }
        public int GlyphCount { get; private set; }

        private byte[] _data;
        private bool _isCff; // true if this is a CFF/OpenType font

        // Table directory
        private Dictionary<string, TableRecord> _tables = new Dictionary<string, TableRecord>();

        // Parsed data
        private int _unitsPerEm;
        private int _indexToLocFormat; // 0 = short, 1 = long
        private int _numGlyphs;
        private int _numberOfHMetrics;
        private ushort[] _advanceWidths;
        private short[] _leftSideBearings;
        private uint[] _glyphOffsets; // from loca table (TrueType only)
        private Dictionary<int, int> _unicodeToGlyph = new Dictionary<int, int>(); // unicode → glyph index

        // Glyph outline cache (avoid re-parsing compound glyphs)
        private Dictionary<int, GlyphOutline> _glyphCache = new Dictionary<int, GlyphOutline>();

        // Table offsets
        private uint _glyfOffset;

        // CFF parser (for OpenType CFF fonts)
        private CffParser _cffParser;

        private struct TableRecord
        {
            public uint Offset;
            public uint Length;
        }

        public TtfParser(byte[] fontData)
        {
            _data = fontData ?? throw new ArgumentNullException(nameof(fontData));
            Parse();
        }

        /// <summary>
        /// Get glyph outline for a specific unicode codepoint.
        /// Returns null if the codepoint is not mapped.
        /// </summary>
        public GlyphOutline GetGlyphOutline(int unicode)
        {
            int glyphIndex;
            if (!_unicodeToGlyph.TryGetValue(unicode, out glyphIndex))
                return null;

            if (glyphIndex <= 0 || glyphIndex >= _numGlyphs)
                return null;

            var outline = GetGlyphByIndex(glyphIndex);
            if (outline != null)
                outline.Unicode = unicode;
            return outline;
        }

        /// <summary>
        /// Get all mapped unicode codepoints in this font.
        /// </summary>
        public int[] GetSupportedCodepoints()
        {
            var result = new int[_unicodeToGlyph.Count];
            int i = 0;
            foreach (var kv in _unicodeToGlyph)
            {
                result[i++] = kv.Key;
            }
            return result;
        }

        /// <summary>
        /// Check if a specific codepoint is supported.
        /// </summary>
        public bool HasCodepoint(int unicode)
        {
            return _unicodeToGlyph.ContainsKey(unicode);
        }

        /// <summary>
        /// A glyph by its index in the font, whether or not a codepoint maps to it. This is the
        /// door to the glyphs OpenType shaping produces — conjuncts, half forms, ligatures —
        /// which have no codepoint of their own: rasterized by index, they can be mapped to a
        /// private-use codepoint and shown by an engine that only knows codepoints.
        /// </summary>
        public GlyphOutline GetGlyphOutlineByIndex(int glyphIndex)
        {
            if (glyphIndex <= 0 || glyphIndex >= _numGlyphs) return null;
            return GetGlyphByIndex(glyphIndex);
        }

        /// <summary>
        /// The first glyph no codepoint maps to and that draws something — the probe glyph of
        /// the private-use experiment (analyse/issue-24-rtl-arabic.md §5 ter). -1 when every
        /// glyph of the font is reachable through the cmap.
        /// </summary>
        public int FirstUnmappedDrawableGlyph()
        {
            var mapped = new HashSet<int>(_unicodeToGlyph.Values);
            for (int index = 1; index < _numGlyphs; index++)
            {
                if (mapped.Contains(index)) continue;
                var outline = GetGlyphByIndex(index);
                if (outline != null && !outline.IsEmpty && outline.Contours != null && outline.Contours.Length > 0)
                    return index;
            }
            return -1;
        }

        /// <summary>Glyph index a codepoint maps to, or 0 (the .notdef glyph) when it maps to none.</summary>
        public int GetGlyphIndex(int unicode)
        {
            return _unicodeToGlyph.TryGetValue(unicode, out int glyphIndex) ? glyphIndex : 0;
        }

        /// <summary>Horizontal advance of a glyph by index, in font units (hmtx).</summary>
        public int GetAdvanceWidth(int glyphIndex)
        {
            return _advanceWidths != null && glyphIndex >= 0 && glyphIndex < _advanceWidths.Length ? _advanceWidths[glyphIndex] : 0;
        }

        /// <summary>Offset and length of a table by tag, false when the font has no such table.</summary>
        public bool TryGetTable(string tag, out uint offset, out uint length)
        {
            if (_tables.TryGetValue(tag, out var rec)) { offset = rec.Offset; length = rec.Length; return true; }
            offset = 0; length = 0;
            return false;
        }

        private OpenTypeLayout _layout;

        /// <summary>
        /// The font's OpenType layout tables (GDEF/GSUB/GPOS), read on first use. Never null:
        /// a font without them answers with empty tables, so a shaper can ask and get nothing.
        /// </summary>
        public OpenTypeLayout Layout => _layout ?? (_layout = new OpenTypeLayout(_data, TryGetTable));

        #region Main Parse

        private void Parse()
        {
            if (_data.Length < 12)
                throw new InvalidDataException("Font file too small");

            // Read offset table
            uint sfVersion = ReadUInt32(0);
            // 0x00010000 = TrueType, 0x4F54544F = 'OTTO' (CFF OpenType)
            _isCff = (sfVersion == 0x4F54544F);

            int numTables = ReadUInt16(4);

            // Read table directory
            int offset = 12;
            for (int i = 0; i < numTables; i++)
            {
                string tag = ReadTag(offset);
                _tables[tag] = new TableRecord
                {
                    Offset = ReadUInt32((uint)(offset + 8)),
                    Length = ReadUInt32((uint)(offset + 12))
                };
                offset += 16;
            }

            // Parse required tables in order
            ParseHead();
            ParseMaxp();
            ParseCmap();
            ParseHhea();
            ParseHmtx();

            if (_isCff)
            {
                // CFF font: parse CFF table for outlines
                if (!_tables.ContainsKey("CFF "))
                    throw new InvalidDataException("Missing required 'CFF ' table");
                var cffTable = _tables["CFF "];
                _cffParser = new CffParser(_data, (int)cffTable.Offset, (int)cffTable.Length);
            }
            else
            {
                // TrueType font: parse loca + glyf tables
                ParseLoca();
                if (!_tables.ContainsKey("glyf"))
                    throw new InvalidDataException("Missing required 'glyf' table");
                _glyfOffset = _tables["glyf"].Offset;
            }

            // Parse optional tables
            ParsePost();
            ParseName();

            // Build metrics
            Metrics = new FontMetrics
            {
                UnitsPerEm = _unitsPerEm,
                Ascender = ReadInt16AtTable("hhea", 4),
                Descender = ReadInt16AtTable("hhea", 6),
                LineGap = ReadInt16AtTable("hhea", 8),
                UnderlinePosition = Metrics?.UnderlinePosition ?? 0,
                UnderlineThickness = Metrics?.UnderlineThickness ?? 0,
                FontName = Metrics?.FontName ?? "Unknown"
            };

            GlyphCount = _numGlyphs;
        }

        #endregion

        #region Table Parsers

        private void ParseHead()
        {
            if (!_tables.ContainsKey("head"))
                throw new InvalidDataException("Missing required 'head' table");

            uint off = _tables["head"].Offset;
            _unitsPerEm = ReadUInt16(off + 18);
            _indexToLocFormat = ReadInt16(off + 50);

            if (_unitsPerEm == 0)
                throw new InvalidDataException("Invalid unitsPerEm: 0");
        }

        private void ParseMaxp()
        {
            if (!_tables.ContainsKey("maxp"))
                throw new InvalidDataException("Missing required 'maxp' table");

            uint off = _tables["maxp"].Offset;
            _numGlyphs = ReadUInt16(off + 4);
        }

        private void ParseCmap()
        {
            if (!_tables.ContainsKey("cmap"))
                throw new InvalidDataException("Missing required 'cmap' table");

            uint cmapOff = _tables["cmap"].Offset;
            int numSubtables = ReadUInt16(cmapOff + 2);

            // Find best subtable: prefer format 12 (full Unicode), then format 4 (BMP)
            uint format12Offset = 0;
            uint format4Offset = 0;

            for (int i = 0; i < numSubtables; i++)
            {
                uint recordOff = cmapOff + 4 + (uint)(i * 8);
                int platformID = ReadUInt16(recordOff);
                int encodingID = ReadUInt16(recordOff + 2);
                uint subtableOffset = cmapOff + ReadUInt32(recordOff + 4);

                int format = ReadUInt16(subtableOffset);

                // Platform 3 (Windows), Encoding 10 (UCS-4) = full Unicode
                if (platformID == 3 && encodingID == 10 && format == 12)
                    format12Offset = subtableOffset;
                // Platform 3 (Windows), Encoding 1 (BMP) = basic Unicode
                else if (platformID == 3 && encodingID == 1 && format == 4)
                    format4Offset = subtableOffset;
                // Platform 0 (Unicode), any encoding
                else if (platformID == 0)
                {
                    if (format == 12 && format12Offset == 0)
                        format12Offset = subtableOffset;
                    else if (format == 4 && format4Offset == 0)
                        format4Offset = subtableOffset;
                }
            }

            if (format12Offset != 0)
                ParseCmapFormat12(format12Offset);
            else if (format4Offset != 0)
                ParseCmapFormat4(format4Offset);
            else
                throw new InvalidDataException("No supported cmap subtable found (need format 4 or 12)");
        }

        private void ParseCmapFormat4(uint offset)
        {
            int segCount = ReadUInt16(offset + 6) / 2;
            uint endCodesOff = offset + 14;
            uint startCodesOff = endCodesOff + (uint)(segCount * 2) + 2; // +2 for reservedPad
            uint idDeltaOff = startCodesOff + (uint)(segCount * 2);
            uint idRangeOffsetOff = idDeltaOff + (uint)(segCount * 2);

            for (int i = 0; i < segCount; i++)
            {
                int endCode = ReadUInt16(endCodesOff + (uint)(i * 2));
                int startCode = ReadUInt16(startCodesOff + (uint)(i * 2));
                int idDelta = ReadInt16(idDeltaOff + (uint)(i * 2));
                int idRangeOffset = ReadUInt16(idRangeOffsetOff + (uint)(i * 2));

                if (startCode == 0xFFFF)
                    break;

                for (int c = startCode; c <= endCode; c++)
                {
                    int glyphIndex;
                    if (idRangeOffset == 0)
                    {
                        glyphIndex = (c + idDelta) & 0xFFFF;
                    }
                    else
                    {
                        uint glyphIdOff = idRangeOffsetOff + (uint)(i * 2) +
                            (uint)idRangeOffset + (uint)((c - startCode) * 2);
                        glyphIndex = ReadUInt16(glyphIdOff);
                        if (glyphIndex != 0)
                            glyphIndex = (glyphIndex + idDelta) & 0xFFFF;
                    }

                    if (glyphIndex != 0 && !_unicodeToGlyph.ContainsKey(c))
                        _unicodeToGlyph[c] = glyphIndex;
                }
            }
        }

        private void ParseCmapFormat12(uint offset)
        {
            uint numGroups = ReadUInt32(offset + 12);
            uint groupOff = offset + 16;

            for (uint g = 0; g < numGroups; g++)
            {
                uint startCharCode = ReadUInt32(groupOff);
                uint endCharCode = ReadUInt32(groupOff + 4);
                uint startGlyphID = ReadUInt32(groupOff + 8);

                for (uint c = startCharCode; c <= endCharCode; c++)
                {
                    int glyphIndex = (int)(startGlyphID + (c - startCharCode));
                    int unicode = (int)c;
                    if (glyphIndex != 0 && unicode > 0 && !_unicodeToGlyph.ContainsKey(unicode))
                        _unicodeToGlyph[unicode] = glyphIndex;
                }

                groupOff += 12;
            }
        }

        private void ParseHhea()
        {
            if (!_tables.ContainsKey("hhea"))
                throw new InvalidDataException("Missing required 'hhea' table");

            uint off = _tables["hhea"].Offset;
            _numberOfHMetrics = ReadUInt16(off + 34);
        }

        private void ParseHmtx()
        {
            if (!_tables.ContainsKey("hmtx"))
                throw new InvalidDataException("Missing required 'hmtx' table");

            uint off = _tables["hmtx"].Offset;
            _advanceWidths = new ushort[_numGlyphs];
            _leftSideBearings = new short[_numGlyphs];

            // Read longHorMetric entries
            for (int i = 0; i < _numberOfHMetrics; i++)
            {
                _advanceWidths[i] = ReadUInt16(off + (uint)(i * 4));
                _leftSideBearings[i] = ReadInt16(off + (uint)(i * 4 + 2));
            }

            // Remaining glyphs share last advance width
            ushort lastAdvance = _numberOfHMetrics > 0 ? _advanceWidths[_numberOfHMetrics - 1] : (ushort)0;
            uint lsbOff = off + (uint)(_numberOfHMetrics * 4);
            for (int i = _numberOfHMetrics; i < _numGlyphs; i++)
            {
                _advanceWidths[i] = lastAdvance;
                _leftSideBearings[i] = ReadInt16(lsbOff + (uint)((i - _numberOfHMetrics) * 2));
            }
        }

        private void ParseLoca()
        {
            if (!_tables.ContainsKey("loca"))
                throw new InvalidDataException("Missing required 'loca' table");

            uint off = _tables["loca"].Offset;
            _glyphOffsets = new uint[_numGlyphs + 1]; // +1 to detect empty glyphs

            if (_indexToLocFormat == 0)
            {
                // Short format: uint16 offsets, multiply by 2
                for (int i = 0; i <= _numGlyphs; i++)
                    _glyphOffsets[i] = (uint)(ReadUInt16(off + (uint)(i * 2)) * 2);
            }
            else
            {
                // Long format: uint32 offsets
                for (int i = 0; i <= _numGlyphs; i++)
                    _glyphOffsets[i] = ReadUInt32(off + (uint)(i * 4));
            }
        }

        private void ParsePost()
        {
            TableRecord rec;
            if (!_tables.TryGetValue("post", out rec))
            {
                if (Metrics == null)
                    Metrics = new FontMetrics();
                Metrics.UnderlinePosition = -_unitsPerEm * 0.1f;
                Metrics.UnderlineThickness = _unitsPerEm * 0.05f;
                return;
            }

            if (Metrics == null)
                Metrics = new FontMetrics();

            // underlinePosition at offset 8 (Fixed 16.16)
            Metrics.UnderlinePosition = ReadInt16(rec.Offset + 8);
            // underlineThickness at offset 10 (Fixed 16.16)
            Metrics.UnderlineThickness = ReadInt16(rec.Offset + 10);

            if (Metrics.UnderlineThickness == 0)
                Metrics.UnderlineThickness = _unitsPerEm * 0.05f;
        }

        private void ParseName()
        {
            TableRecord rec;
            if (!_tables.TryGetValue("name", out rec))
                return;

            if (Metrics == null)
                Metrics = new FontMetrics();

            uint off = rec.Offset;
            int count = ReadUInt16(off + 2);
            uint stringOffset = off + ReadUInt16(off + 4);

            // Look for nameID 4 (Full Name) or nameID 1 (Family Name)
            string familyName = null;
            string fullName = null;

            for (int i = 0; i < count; i++)
            {
                uint nameRecOff = off + 6 + (uint)(i * 12);
                int platformID = ReadUInt16(nameRecOff);
                int encodingID = ReadUInt16(nameRecOff + 2);
                int nameID = ReadUInt16(nameRecOff + 6);
                int length = ReadUInt16(nameRecOff + 8);
                int nameOffset = ReadUInt16(nameRecOff + 10);

                if (nameID != 1 && nameID != 4)
                    continue;

                string name = null;
                uint strOff = stringOffset + (uint)nameOffset;

                if (platformID == 3 || platformID == 0) // Windows or Unicode: UTF-16BE
                {
                    name = ReadUtf16BE(strOff, length);
                }
                else if (platformID == 1) // Mac: ASCII-ish
                {
                    name = ReadAscii(strOff, length);
                }

                if (!string.IsNullOrEmpty(name))
                {
                    if (nameID == 1 && familyName == null)
                        familyName = name;
                    else if (nameID == 4 && fullName == null)
                        fullName = name;
                }
            }

            Metrics.FontName = fullName ?? familyName ?? "Unknown";
        }

        #endregion

        #region Glyph Parsing

        private GlyphOutline GetGlyphByIndex(int glyphIndex)
        {
            GlyphOutline cached;
            if (_glyphCache.TryGetValue(glyphIndex, out cached))
                return cached;

            GlyphOutline outline;
            if (_isCff && _cffParser != null)
            {
                outline = _cffParser.ParseGlyph(glyphIndex);
                if (outline != null)
                {
                    outline.AdvanceWidth = glyphIndex < _advanceWidths.Length ? _advanceWidths[glyphIndex] : 0;
                    outline.LeftSideBearing = glyphIndex < _leftSideBearings.Length ? _leftSideBearings[glyphIndex] : 0;
                }
            }
            else
            {
                outline = ParseGlyph(glyphIndex, 0);
            }

            _glyphCache[glyphIndex] = outline;
            return outline;
        }

        private GlyphOutline ParseGlyph(int glyphIndex, int recursionDepth)
        {
            if (recursionDepth > 32)
                return null; // Prevent infinite recursion

            if (glyphIndex < 0 || glyphIndex >= _numGlyphs)
                return null;

            uint glyphOff = _glyfOffset + _glyphOffsets[glyphIndex];
            uint nextGlyphOff = _glyfOffset + _glyphOffsets[glyphIndex + 1];

            // Empty glyph (e.g. space)
            if (glyphOff == nextGlyphOff)
            {
                return new GlyphOutline
                {
                    GlyphIndex = glyphIndex,
                    AdvanceWidth = glyphIndex < _advanceWidths.Length ? _advanceWidths[glyphIndex] : 0,
                    LeftSideBearing = glyphIndex < _leftSideBearings.Length ? _leftSideBearings[glyphIndex] : 0,
                    Contours = new GlyphContour[0],
                    IsEmpty = true
                };
            }

            int numberOfContours = ReadInt16(glyphOff);
            int xMin = ReadInt16(glyphOff + 2);
            int yMin = ReadInt16(glyphOff + 4);
            int xMax = ReadInt16(glyphOff + 6);
            int yMax = ReadInt16(glyphOff + 8);

            GlyphOutline outline;
            if (numberOfContours >= 0)
                outline = ParseSimpleGlyph(glyphOff + 10, numberOfContours);
            else
                outline = ParseCompoundGlyph(glyphOff + 10, recursionDepth);

            if (outline == null)
                return null;

            outline.GlyphIndex = glyphIndex;
            outline.XMin = xMin;
            outline.YMin = yMin;
            outline.XMax = xMax;
            outline.YMax = yMax;
            outline.AdvanceWidth = glyphIndex < _advanceWidths.Length ? _advanceWidths[glyphIndex] : 0;
            outline.LeftSideBearing = glyphIndex < _leftSideBearings.Length ? _leftSideBearings[glyphIndex] : 0;
            outline.IsEmpty = outline.Contours == null || outline.Contours.Length == 0;

            return outline;
        }

        private GlyphOutline ParseSimpleGlyph(uint offset, int numberOfContours)
        {
            if (numberOfContours == 0)
                return new GlyphOutline { Contours = new GlyphContour[0] };

            // Read end points of contours
            var endPts = new int[numberOfContours];
            for (int i = 0; i < numberOfContours; i++)
            {
                endPts[i] = ReadUInt16(offset);
                offset += 2;
            }

            int totalPoints = endPts[numberOfContours - 1] + 1;

            // Skip instructions
            int instructionLength = ReadUInt16(offset);
            offset += 2 + (uint)instructionLength;

            // Read flags
            var flags = new byte[totalPoints];
            for (int i = 0; i < totalPoints; )
            {
                byte flag = _data[offset++];
                flags[i++] = flag;

                // Repeat flag
                if ((flag & 0x08) != 0)
                {
                    int repeatCount = _data[offset++];
                    for (int r = 0; r < repeatCount && i < totalPoints; r++)
                        flags[i++] = flag;
                }
            }

            // Read X coordinates (delta-encoded)
            var xCoords = new short[totalPoints];
            short xVal = 0;
            for (int i = 0; i < totalPoints; i++)
            {
                byte flag = flags[i];
                if ((flag & 0x02) != 0) // x-Short
                {
                    byte delta = _data[offset++];
                    xVal += (short)((flag & 0x10) != 0 ? delta : -delta);
                }
                else if ((flag & 0x10) == 0) // Not short and not same: 2-byte delta
                {
                    xVal += ReadInt16(offset);
                    offset += 2;
                }
                // else: same as previous (delta = 0)
                xCoords[i] = xVal;
            }

            // Read Y coordinates (delta-encoded)
            var yCoords = new short[totalPoints];
            short yVal = 0;
            for (int i = 0; i < totalPoints; i++)
            {
                byte flag = flags[i];
                if ((flag & 0x04) != 0) // y-Short
                {
                    byte delta = _data[offset++];
                    yVal += (short)((flag & 0x20) != 0 ? delta : -delta);
                }
                else if ((flag & 0x20) == 0) // Not short and not same
                {
                    yVal += ReadInt16(offset);
                    offset += 2;
                }
                yCoords[i] = yVal;
            }

            // Build contours
            var contours = new GlyphContour[numberOfContours];
            int pointIdx = 0;
            for (int c = 0; c < numberOfContours; c++)
            {
                int endPt = endPts[c];
                int contourLength = endPt - pointIdx + 1;
                var points = new ContourPoint[contourLength];
                for (int p = 0; p < contourLength; p++)
                {
                    int idx = pointIdx + p;
                    points[p] = new ContourPoint(xCoords[idx], yCoords[idx], (flags[idx] & 0x01) != 0);
                }
                contours[c] = new GlyphContour { Points = points };
                pointIdx = endPt + 1;
            }

            return new GlyphOutline { Contours = contours };
        }

        private GlyphOutline ParseCompoundGlyph(uint offset, int recursionDepth)
        {
            var allContours = new List<GlyphContour>();
            bool hasMore = true;

            while (hasMore)
            {
                ushort compFlags = ReadUInt16(offset);
                offset += 2;
                int compGlyphIndex = ReadUInt16(offset);
                offset += 2;

                // Read transform arguments
                float arg1, arg2;
                if ((compFlags & 0x0001) != 0) // ARG_1_AND_2_ARE_WORDS
                {
                    if ((compFlags & 0x0002) != 0) // ARGS_ARE_XY_VALUES
                    {
                        arg1 = ReadInt16(offset);
                        arg2 = ReadInt16(offset + 2);
                    }
                    else
                    {
                        arg1 = ReadUInt16(offset);
                        arg2 = ReadUInt16(offset + 2);
                    }
                    offset += 4;
                }
                else
                {
                    if ((compFlags & 0x0002) != 0)
                    {
                        arg1 = (sbyte)_data[offset];
                        arg2 = (sbyte)_data[offset + 1];
                    }
                    else
                    {
                        arg1 = _data[offset];
                        arg2 = _data[offset + 1];
                    }
                    offset += 2;
                }

                // Read transform matrix
                float a = 1, b = 0, c = 0, d = 1;
                if ((compFlags & 0x0008) != 0) // WE_HAVE_A_SCALE
                {
                    a = d = ReadF2Dot14(offset);
                    offset += 2;
                }
                else if ((compFlags & 0x0040) != 0) // WE_HAVE_AN_X_AND_Y_SCALE
                {
                    a = ReadF2Dot14(offset);
                    d = ReadF2Dot14(offset + 2);
                    offset += 4;
                }
                else if ((compFlags & 0x0080) != 0) // WE_HAVE_A_TWO_BY_TWO
                {
                    a = ReadF2Dot14(offset);
                    b = ReadF2Dot14(offset + 2);
                    c = ReadF2Dot14(offset + 4);
                    d = ReadF2Dot14(offset + 6);
                    offset += 8;
                }

                // Get component glyph
                var component = ParseGlyph(compGlyphIndex, recursionDepth + 1);
                if (component != null && component.Contours != null)
                {
                    // Apply transform to all points
                    float tx = (compFlags & 0x0002) != 0 ? arg1 : 0;
                    float ty = (compFlags & 0x0002) != 0 ? arg2 : 0;

                    foreach (var contour in component.Contours)
                    {
                        var transformed = new ContourPoint[contour.Points.Length];
                        for (int p = 0; p < contour.Points.Length; p++)
                        {
                            float ox = contour.Points[p].X;
                            float oy = contour.Points[p].Y;
                            transformed[p] = new ContourPoint(
                                a * ox + c * oy + tx,
                                b * ox + d * oy + ty,
                                contour.Points[p].OnCurve
                            );
                        }
                        allContours.Add(new GlyphContour { Points = transformed });
                    }
                }

                hasMore = (compFlags & 0x0020) != 0; // MORE_COMPONENTS
            }

            return new GlyphOutline { Contours = allContours.ToArray() };
        }

        #endregion

        #region Binary Reading Helpers (Big-Endian)

        private ushort ReadUInt16(uint offset)
        {
            if (offset + 1 >= _data.Length) return 0;
            return (ushort)((_data[offset] << 8) | _data[offset + 1]);
        }

        private short ReadInt16(uint offset)
        {
            return (short)ReadUInt16(offset);
        }

        private uint ReadUInt32(uint offset)
        {
            if (offset + 3 >= _data.Length) return 0;
            return ((uint)_data[offset] << 24) | ((uint)_data[offset + 1] << 16) |
                   ((uint)_data[offset + 2] << 8) | _data[offset + 3];
        }

        private int ReadUInt16(int offset)
        {
            return ReadUInt16((uint)offset);
        }

        private short ReadInt16(int offset)
        {
            return ReadInt16((uint)offset);
        }

        private float ReadF2Dot14(uint offset)
        {
            return ReadInt16(offset) / 16384f;
        }

        private string ReadTag(int offset)
        {
            if (offset + 3 >= _data.Length) return "????";
            return Encoding.ASCII.GetString(_data, offset, 4);
        }

        private string ReadUtf16BE(uint offset, int byteLength)
        {
            if (offset + byteLength > _data.Length) return null;
            var chars = new char[byteLength / 2];
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)((_data[offset + i * 2] << 8) | _data[offset + i * 2 + 1]);
            }
            return new string(chars);
        }

        private string ReadAscii(uint offset, int length)
        {
            if (offset + length > _data.Length) return null;
            return Encoding.ASCII.GetString(_data, (int)offset, length);
        }

        private short ReadInt16AtTable(string tableName, int offsetInTable)
        {
            TableRecord rec;
            if (!_tables.TryGetValue(tableName, out rec))
                return 0;
            return ReadInt16(rec.Offset + (uint)offsetInTable);
        }

        #endregion
    }
}
