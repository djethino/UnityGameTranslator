using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Rasterizer
{
    /// <summary>
    /// Parser for CFF (Compact Font Format) outlines found in OpenType .otf fonts.
    /// Handles Type 2 charstrings with cubic Bezier curves.
    /// Only supports CFF version 1 (not CFF2).
    /// </summary>
    public class CffParser
    {
        private byte[] _data;
        private int _cffOffset; // Start of CFF table in the font file
        private int _cffLength;

        // CFF structures
        private int _charstringsCount;
        private int[] _charstringsOffsets; // Offset array for charstrings INDEX
        private int _charstringsDataOffset;

        // Subroutines
        private int[] _gsubrOffsets;
        private int _gsubrDataOffset;
        private int _gsubrCount;
        private int _gsubrBias;

        private int[] _lsubrOffsets;
        private int _lsubrDataOffset;
        private int _lsubrCount;
        private int _lsubrBias;

        // Default width / nominal width from Private DICT
        private float _defaultWidthX;
        private float _nominalWidthX;

        // CID-keyed fonts (CJK .otf, the Top DICT's ROS): each glyph belongs to one Font DICT of the
        // FDArray (FDSelect says which), and each Font DICT has its own Private DICT — its own local
        // subroutines. Null for an ordinary font: one Private DICT for every glyph.
        private LocalSubrs[] _fdSubrs;
        private byte[] _fdOfGlyph;

        private struct LocalSubrs { public int[] Offsets; public int DataOffset; public int Count; public int Bias; }

        public CffParser(byte[] fontData, int cffTableOffset, int cffTableLength)
        {
            _data = fontData;
            _cffOffset = cffTableOffset;
            _cffLength = cffTableLength;
            Parse();
        }

        /// <summary>
        /// Parse a CFF charstring for the given glyph index and return contours.
        /// </summary>
        public GlyphOutline ParseGlyph(int glyphIndex)
        {
            if (glyphIndex < 0 || glyphIndex >= _charstringsCount)
                return null;

            var charstring = GetCharstringData(glyphIndex);
            if (charstring == null || charstring.Length == 0)
                return new GlyphOutline { Contours = new GlyphContour[0], IsEmpty = true };

            // A CID-keyed glyph calls the local subroutines of ITS Font DICT: reading them from the
            // top-level Private DICT drew CJK glyphs from other glyphs' pieces (found by the CFF
            // merge's comparison with FreeType, 2026-09-30).
            if (_fdSubrs != null)
            {
                int fd = _fdOfGlyph != null && glyphIndex < _fdOfGlyph.Length ? _fdOfGlyph[glyphIndex] : 0;
                var subrs = fd < _fdSubrs.Length ? _fdSubrs[fd] : default(LocalSubrs);
                _lsubrOffsets = subrs.Offsets; _lsubrDataOffset = subrs.DataOffset;
                _lsubrCount = subrs.Count; _lsubrBias = subrs.Bias;
            }

            try
            {
                var contours = ExecuteCharstring(charstring);
                var outline = new GlyphOutline
                {
                    GlyphIndex = glyphIndex,
                    Contours = contours.ToArray(),
                    IsEmpty = contours.Count == 0
                };

                // Compute bounding box from contours
                if (!outline.IsEmpty)
                    ComputeBounds(outline);

                return outline;
            }
            // A charstring from a font file this code did not write: a malformed one leaves this
            // glyph blank, and it is said.
            catch (Exception ex)
            {
                Faults.Say("CffParser.ParseGlyph", ex, $"glyph {glyphIndex}");
                return new GlyphOutline { Contours = new GlyphContour[0], IsEmpty = true };
            }
        }

        public int GlyphCount => _charstringsCount;

        #region CFF Parsing

        private void Parse()
        {
            int pos = _cffOffset;
            int end = _cffOffset + _cffLength;

            // CFF Header
            int hdrSize = _data[pos + 2];
            pos = _cffOffset + hdrSize;

            // Name INDEX — skip it
            pos = SkipIndex(pos);
            if (pos < 0 || pos >= end) return;

            // Top DICT INDEX — read first entry
            byte[] topDictData = ReadIndexEntry(pos, 0);
            pos = SkipIndex(pos);
            if (pos < 0 || pos >= end) return;

            // String INDEX — skip it
            pos = SkipIndex(pos);
            if (pos < 0) return;

            // Global Subr INDEX
            if (pos < end)
            {
                ParseSubrIndex(pos, out _gsubrOffsets, out _gsubrDataOffset, out _gsubrCount);
                _gsubrBias = CalcSubrBias(_gsubrCount);
                pos = SkipIndex(pos);
            }

            // Parse Top DICT to find charstrings offset, private dict, etc.
            int charstringsOff = 0;
            int privateDictOff = 0;
            int privateDictSize = 0;
            int fdArrayOff = 0, fdSelectOff = 0;
            if (topDictData != null)
                ParseTopDict(topDictData, ref charstringsOff, ref privateDictOff, ref privateDictSize, ref fdArrayOff, ref fdSelectOff);

            // Charstrings INDEX
            if (charstringsOff > 0)
            {
                int csPos = _cffOffset + charstringsOff;
                if (csPos + 3 < _data.Length)
                {
                    _charstringsCount = ReadUInt16BE(csPos);
                    if (_charstringsCount > 0)
                    {
                        int csOffSize = _data[csPos + 2];
                        _charstringsOffsets = new int[_charstringsCount + 1];
                        for (int i = 0; i <= _charstringsCount; i++)
                            _charstringsOffsets[i] = ReadOffset(csPos + 3 + i * csOffSize, csOffSize);
                        _charstringsDataOffset = csPos + 3 + (_charstringsCount + 1) * csOffSize - 1;
                    }
                }
            }

            if (fdArrayOff > 0) ParseFdArray(fdArrayOff, fdSelectOff);

            // Private DICT → Local Subr INDEX
            _lsubrCount = 0;
            if (privateDictSize > 0 && privateDictOff > 0)
            {
                int privPos = _cffOffset + privateDictOff;
                if (privPos + privateDictSize <= _data.Length)
                {
                    int localSubrOff = ParsePrivateDict(privPos, privateDictSize);
                    if (localSubrOff > 0)
                    {
                        int lsubrPos = privPos + localSubrOff;
                        if (lsubrPos + 3 < _data.Length)
                        {
                            ParseSubrIndex(lsubrPos, out _lsubrOffsets, out _lsubrDataOffset, out _lsubrCount);
                            _lsubrBias = CalcSubrBias(_lsubrCount);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Read the data of a specific entry from a CFF INDEX at the given position.
        /// </summary>
        private byte[] ReadIndexEntry(int indexPos, int entryIdx)
        {
            if (indexPos + 3 >= _data.Length) return null;
            int count = ReadUInt16BE(indexPos);
            if (entryIdx >= count) return null;
            int offSize = _data[indexPos + 2];
            int offsetsStart = indexPos + 3;
            int dataStart = offsetsStart + (count + 1) * offSize;

            int off0 = ReadOffset(offsetsStart + entryIdx * offSize, offSize);
            int off1 = ReadOffset(offsetsStart + (entryIdx + 1) * offSize, offSize);
            int len = off1 - off0;
            if (len <= 0) return null;

            int absStart = dataStart + off0 - 1; // CFF offsets are 1-based
            if (absStart + len > _data.Length) return null;

            var result = new byte[len];
            Array.Copy(_data, absStart, result, 0, len);
            return result;
        }

        private void ParseTopDict(byte[] data, ref int charstringsOff, ref int privateDictOff, ref int privateDictSize,
                                  ref int fdArrayOff, ref int fdSelectOff)
        {
            var stack = new List<float>();
            int pos = 0;
            while (pos < data.Length)
            {
                int b0 = data[pos];
                if (b0 >= 28) // operand
                {
                    stack.Add(ReadDictOperand(data, ref pos));
                }
                else // operator
                {
                    pos++;
                    int op = b0;
                    if (b0 == 12 && pos < data.Length)
                    {
                        op = 0x0C00 | data[pos];
                        pos++;
                    }

                    switch (op)
                    {
                        case 17: // CharStrings
                            if (stack.Count > 0) charstringsOff = (int)stack[stack.Count - 1];
                            break;
                        case 18: // Private (size, offset)
                            if (stack.Count >= 2)
                            {
                                privateDictSize = (int)stack[stack.Count - 2];
                                privateDictOff = (int)stack[stack.Count - 1];
                            }
                            break;
                        case 0x0C24: // FDArray
                            if (stack.Count > 0) fdArrayOff = (int)stack[stack.Count - 1];
                            break;
                        case 0x0C25: // FDSelect
                            if (stack.Count > 0) fdSelectOff = (int)stack[stack.Count - 1];
                            break;
                    }
                    stack.Clear();
                }
            }
        }

        /// <summary>
        /// A CID-keyed font's Font DICTs, each with its Private DICT's local subroutines, and the
        /// Font DICT of every glyph (FDSelect format 0: one byte per glyph; format 3: ranges).
        /// </summary>
        private void ParseFdArray(int fdArrayOff, int fdSelectOff)
        {
            int indexPos = _cffOffset + fdArrayOff;
            if (indexPos + 2 >= _data.Length) return;
            int count = ReadUInt16BE(indexPos);
            _fdSubrs = new LocalSubrs[count];
            for (int fd = 0; fd < count; fd++)
            {
                byte[] dict = ReadIndexEntry(indexPos, fd);
                if (dict == null) continue;
                int cs = 0, privOff = 0, privSize = 0, a = 0, b = 0;
                ParseTopDict(dict, ref cs, ref privOff, ref privSize, ref a, ref b);
                if (privSize <= 0 || privOff <= 0) continue;
                int privPos = _cffOffset + privOff;
                if (privPos + privSize > _data.Length) continue;
                int localSubrOff = ParsePrivateDict(privPos, privSize);
                if (localSubrOff <= 0) continue;
                ParseSubrIndex(privPos + localSubrOff, out var offsets, out int dataOffset, out int subrCount);
                _fdSubrs[fd] = new LocalSubrs { Offsets = offsets, DataOffset = dataOffset, Count = subrCount, Bias = CalcSubrBias(subrCount) };
            }

            if (fdSelectOff <= 0 || _charstringsCount <= 0) return;
            int p = _cffOffset + fdSelectOff;
            if (p >= _data.Length) return;
            _fdOfGlyph = new byte[_charstringsCount];
            int format = _data[p];
            if (format == 0)
            {
                for (int g = 0; g < _charstringsCount && p + 1 + g < _data.Length; g++) _fdOfGlyph[g] = _data[p + 1 + g];
            }
            else if (format == 3)
            {
                int ranges = ReadUInt16BE(p + 1);
                int r = p + 3;
                for (int i = 0; i < ranges; i++, r += 3)
                {
                    int first = ReadUInt16BE(r);
                    byte fd = r + 2 < _data.Length ? _data[r + 2] : (byte)0;
                    int next = ReadUInt16BE(r + 3);   // the next range's first glyph, or the sentinel
                    for (int g = first; g < next && g < _charstringsCount; g++) _fdOfGlyph[g] = fd;
                }
            }
        }

        private int ParsePrivateDict(int pos, int size)
        {
            var stack = new List<float>();
            int end = pos + size;
            int localSubrOff = 0;

            while (pos < end)
            {
                int b0 = _data[pos];
                if (b0 >= 28)
                {
                    stack.Add(ReadDictOperandFromData(ref pos));
                }
                else
                {
                    pos++;
                    int op = b0;
                    if (b0 == 12 && pos < end)
                    {
                        op = 0x0C00 | _data[pos];
                        pos++;
                    }

                    switch (op)
                    {
                        case 19: // Subrs (local subr offset)
                            if (stack.Count > 0) localSubrOff = (int)stack[stack.Count - 1];
                            break;
                        case 20: // defaultWidthX
                            if (stack.Count > 0) _defaultWidthX = stack[stack.Count - 1];
                            break;
                        case 21: // nominalWidthX
                            if (stack.Count > 0) _nominalWidthX = stack[stack.Count - 1];
                            break;
                    }
                    stack.Clear();
                }
            }
            return localSubrOff;
        }

        #endregion

        #region Charstring Execution

        private byte[] GetCharstringData(int glyphIndex)
        {
            int off0 = _charstringsOffsets[glyphIndex];
            int off1 = _charstringsOffsets[glyphIndex + 1];
            int len = off1 - off0;
            if (len <= 0) return null;

            var result = new byte[len];
            Array.Copy(_data, _charstringsDataOffset + off0, result, 0, len);
            return result;
        }

        private byte[] GetGlobalSubr(int index)
        {
            if (index < 0 || index >= _gsubrCount) return null;
            int off0 = _gsubrOffsets[index];
            int off1 = _gsubrOffsets[index + 1];
            int len = off1 - off0;
            if (len <= 0) return null;
            var result = new byte[len];
            Array.Copy(_data, _gsubrDataOffset + off0, result, 0, len);
            return result;
        }

        private byte[] GetLocalSubr(int index)
        {
            if (index < 0 || index >= _lsubrCount) return null;
            int off0 = _lsubrOffsets[index];
            int off1 = _lsubrOffsets[index + 1];
            int len = off1 - off0;
            if (len <= 0) return null;
            var result = new byte[len];
            Array.Copy(_data, _lsubrDataOffset + off0, result, 0, len);
            return result;
        }

        /// <summary>
        /// Execute a Type 2 charstring and produce contours with cubic Bezier points.
        /// </summary>
        private List<GlyphContour> ExecuteCharstring(byte[] cs)
        {
            var contours = new List<GlyphContour>();
            var currentContour = new List<ContourPoint>();
            var stack = new List<float>();
            float x = 0, y = 0;
            bool widthParsed = false;
            int pos = 0;
            int callDepth = 0;
            _currentStemCount = 0;

            ExecuteCharstringInner(cs, ref pos, contours, currentContour, stack,
                ref x, ref y, ref widthParsed, ref callDepth);

            // Close last contour if open
            if (currentContour.Count > 0)
                contours.Add(new GlyphContour { Points = currentContour.ToArray() });

            return contours;
        }

        // Stem count tracked across the charstring (needed for hintmask byte count)
        private int _currentStemCount;

        private void ExecuteCharstringInner(byte[] cs, ref int pos,
            List<GlyphContour> contours, List<ContourPoint> currentContour,
            List<float> stack, ref float x, ref float y, ref bool widthParsed, ref int callDepth)
        {
            if (callDepth > 10) return; // Prevent infinite recursion

            while (pos < cs.Length)
            {
                int b0 = cs[pos];

                if (b0 == 14) // endchar
                {
                    pos++;
                    if (currentContour.Count > 0)
                    {
                        contours.Add(new GlyphContour { Points = currentContour.ToArray() });
                        currentContour.Clear();
                    }
                    return;
                }

                if (b0 >= 32 || b0 == 28) // operand
                {
                    stack.Add(ReadCharstringOperand(cs, ref pos));
                    continue;
                }

                pos++;

                // Handle width (first operand is width if odd number of args before first operator)
                if (!widthParsed)
                {
                    widthParsed = true;
                    // Width is consumed implicitly — we don't need it for outlines
                }

                switch (b0)
                {
                    case 1:  // hstem
                    case 3:  // vstem
                    case 18: // hstemhm
                    case 23: // vstemhm
                        // Each pair of values on the stack defines one stem
                        _currentStemCount += stack.Count / 2;
                        stack.Clear();
                        break;

                    case 19: // hintmask
                    case 20: // cntrmask
                        // Any remaining stack values are implicit vstem hints
                        _currentStemCount += stack.Count / 2;
                        stack.Clear();
                        // Skip ceil(stemCount/8) mask bytes
                        int maskBytes = (_currentStemCount + 7) / 8;
                        if (maskBytes < 1) maskBytes = 1;
                        pos += maskBytes;
                        break;

                    case 21: // rmoveto
                    {
                        if (currentContour.Count > 0)
                        {
                            contours.Add(new GlyphContour { Points = currentContour.ToArray() });
                            currentContour.Clear();
                        }
                        int idx = stack.Count >= 2 ? stack.Count - 2 : 0;
                        if (stack.Count >= 2) { x += stack[idx]; y += stack[idx + 1]; }
                        currentContour.Add(new ContourPoint(x, y, true));
                        stack.Clear();
                        break;
                    }

                    case 22: // hmoveto
                    {
                        if (currentContour.Count > 0)
                        {
                            contours.Add(new GlyphContour { Points = currentContour.ToArray() });
                            currentContour.Clear();
                        }
                        if (stack.Count >= 1) x += stack[stack.Count - 1];
                        currentContour.Add(new ContourPoint(x, y, true));
                        stack.Clear();
                        break;
                    }

                    case 4: // vmoveto
                    {
                        if (currentContour.Count > 0)
                        {
                            contours.Add(new GlyphContour { Points = currentContour.ToArray() });
                            currentContour.Clear();
                        }
                        if (stack.Count >= 1) y += stack[stack.Count - 1];
                        currentContour.Add(new ContourPoint(x, y, true));
                        stack.Clear();
                        break;
                    }

                    case 5: // rlineto
                    {
                        for (int i = 0; i + 1 < stack.Count; i += 2)
                        {
                            x += stack[i]; y += stack[i + 1];
                            currentContour.Add(new ContourPoint(x, y, true));
                        }
                        stack.Clear();
                        break;
                    }

                    case 6: // hlineto
                    {
                        bool horizontal = true;
                        for (int i = 0; i < stack.Count; i++)
                        {
                            if (horizontal) x += stack[i]; else y += stack[i];
                            currentContour.Add(new ContourPoint(x, y, true));
                            horizontal = !horizontal;
                        }
                        stack.Clear();
                        break;
                    }

                    case 7: // vlineto
                    {
                        bool vertical = true;
                        for (int i = 0; i < stack.Count; i++)
                        {
                            if (vertical) y += stack[i]; else x += stack[i];
                            currentContour.Add(new ContourPoint(x, y, true));
                            vertical = !vertical;
                        }
                        stack.Clear();
                        break;
                    }

                    case 8: // rrcurveto
                    {
                        for (int i = 0; i + 5 < stack.Count; i += 6)
                        {
                            float c1x = x + stack[i], c1y = y + stack[i + 1];
                            float c2x = c1x + stack[i + 2], c2y = c1y + stack[i + 3];
                            x = c2x + stack[i + 4]; y = c2y + stack[i + 5];
                            currentContour.Add(new ContourPoint(c1x, c1y, false, true));
                            currentContour.Add(new ContourPoint(c2x, c2y, false, true));
                            currentContour.Add(new ContourPoint(x, y, true));
                        }
                        stack.Clear();
                        break;
                    }

                    case 24: // rcurveline
                    {
                        int i = 0;
                        for (; i + 7 < stack.Count; i += 6)
                        {
                            float c1x = x + stack[i], c1y = y + stack[i + 1];
                            float c2x = c1x + stack[i + 2], c2y = c1y + stack[i + 3];
                            x = c2x + stack[i + 4]; y = c2y + stack[i + 5];
                            currentContour.Add(new ContourPoint(c1x, c1y, false, true));
                            currentContour.Add(new ContourPoint(c2x, c2y, false, true));
                            currentContour.Add(new ContourPoint(x, y, true));
                        }
                        if (i + 1 < stack.Count)
                        {
                            x += stack[i]; y += stack[i + 1];
                            currentContour.Add(new ContourPoint(x, y, true));
                        }
                        stack.Clear();
                        break;
                    }

                    case 25: // rlinecurve
                    {
                        int i = 0;
                        int lineEnd = stack.Count - 6;
                        for (; i + 1 < lineEnd; i += 2)
                        {
                            x += stack[i]; y += stack[i + 1];
                            currentContour.Add(new ContourPoint(x, y, true));
                        }
                        if (i + 5 < stack.Count)
                        {
                            float c1x = x + stack[i], c1y = y + stack[i + 1];
                            float c2x = c1x + stack[i + 2], c2y = c1y + stack[i + 3];
                            x = c2x + stack[i + 4]; y = c2y + stack[i + 5];
                            currentContour.Add(new ContourPoint(c1x, c1y, false, true));
                            currentContour.Add(new ContourPoint(c2x, c2y, false, true));
                            currentContour.Add(new ContourPoint(x, y, true));
                        }
                        stack.Clear();
                        break;
                    }

                    case 26: // vvcurveto
                    {
                        int i = 0;
                        float extraX = 0;
                        if (stack.Count % 4 != 0) { extraX = stack[i]; i++; }
                        for (; i + 3 < stack.Count; i += 4)
                        {
                            float c1x = x + extraX, c1y = y + stack[i];
                            float c2x = c1x + stack[i + 1], c2y = c1y + stack[i + 2];
                            x = c2x; y = c2y + stack[i + 3];
                            currentContour.Add(new ContourPoint(c1x, c1y, false, true));
                            currentContour.Add(new ContourPoint(c2x, c2y, false, true));
                            currentContour.Add(new ContourPoint(x, y, true));
                            extraX = 0;
                        }
                        stack.Clear();
                        break;
                    }

                    case 27: // hhcurveto
                    {
                        int i = 0;
                        float extraY = 0;
                        if (stack.Count % 4 != 0) { extraY = stack[i]; i++; }
                        for (; i + 3 < stack.Count; i += 4)
                        {
                            float c1x = x + stack[i], c1y = y + extraY;
                            float c2x = c1x + stack[i + 1], c2y = c1y + stack[i + 2];
                            x = c2x + stack[i + 3]; y = c2y;
                            currentContour.Add(new ContourPoint(c1x, c1y, false, true));
                            currentContour.Add(new ContourPoint(c2x, c2y, false, true));
                            currentContour.Add(new ContourPoint(x, y, true));
                            extraY = 0;
                        }
                        stack.Clear();
                        break;
                    }

                    case 30: // vhcurveto
                    {
                        int i = 0;
                        bool startV = true;
                        while (i + 3 < stack.Count)
                        {
                            if (startV)
                            {
                                float c1x = x, c1y = y + stack[i];
                                float c2x = c1x + stack[i + 1], c2y = c1y + stack[i + 2];
                                x = c2x + stack[i + 3];
                                y = c2y + (i + 4 == stack.Count - 1 ? stack[i + 4] : 0);
                                currentContour.Add(new ContourPoint(c1x, c1y, false, true));
                                currentContour.Add(new ContourPoint(c2x, c2y, false, true));
                                currentContour.Add(new ContourPoint(x, y, true));
                            }
                            else
                            {
                                float c1x = x + stack[i], c1y = y;
                                float c2x = c1x + stack[i + 1], c2y = c1y + stack[i + 2];
                                y = c2y + stack[i + 3];
                                x = c2x + (i + 4 == stack.Count - 1 ? stack[i + 4] : 0);
                                currentContour.Add(new ContourPoint(c1x, c1y, false, true));
                                currentContour.Add(new ContourPoint(c2x, c2y, false, true));
                                currentContour.Add(new ContourPoint(x, y, true));
                            }
                            i += 4;
                            startV = !startV;
                        }
                        stack.Clear();
                        break;
                    }

                    case 31: // hvcurveto
                    {
                        int i = 0;
                        bool startH = true;
                        while (i + 3 < stack.Count)
                        {
                            if (startH)
                            {
                                float c1x = x + stack[i], c1y = y;
                                float c2x = c1x + stack[i + 1], c2y = c1y + stack[i + 2];
                                y = c2y + stack[i + 3];
                                x = c2x + (i + 4 == stack.Count - 1 ? stack[i + 4] : 0);
                                currentContour.Add(new ContourPoint(c1x, c1y, false, true));
                                currentContour.Add(new ContourPoint(c2x, c2y, false, true));
                                currentContour.Add(new ContourPoint(x, y, true));
                            }
                            else
                            {
                                float c1x = x, c1y = y + stack[i];
                                float c2x = c1x + stack[i + 1], c2y = c1y + stack[i + 2];
                                x = c2x + stack[i + 3];
                                y = c2y + (i + 4 == stack.Count - 1 ? stack[i + 4] : 0);
                                currentContour.Add(new ContourPoint(c1x, c1y, false, true));
                                currentContour.Add(new ContourPoint(c2x, c2y, false, true));
                                currentContour.Add(new ContourPoint(x, y, true));
                            }
                            i += 4;
                            startH = !startH;
                        }
                        stack.Clear();
                        break;
                    }

                    case 10: // callsubr (local)
                    {
                        if (stack.Count > 0)
                        {
                            int subrIdx = (int)stack[stack.Count - 1] + _lsubrBias;
                            stack.RemoveAt(stack.Count - 1);
                            var subr = GetLocalSubr(subrIdx);
                            if (subr != null)
                            {
                                int subrPos = 0;
                                callDepth++;
                                ExecuteCharstringInner(subr, ref subrPos, contours, currentContour,
                                    stack, ref x, ref y, ref widthParsed, ref callDepth);
                                callDepth--;
                            }
                        }
                        break;
                    }

                    case 29: // callgsubr (global)
                    {
                        if (stack.Count > 0)
                        {
                            int subrIdx = (int)stack[stack.Count - 1] + _gsubrBias;
                            stack.RemoveAt(stack.Count - 1);
                            var subr = GetGlobalSubr(subrIdx);
                            if (subr != null)
                            {
                                int subrPos = 0;
                                callDepth++;
                                ExecuteCharstringInner(subr, ref subrPos, contours, currentContour,
                                    stack, ref x, ref y, ref widthParsed, ref callDepth);
                                callDepth--;
                            }
                        }
                        break;
                    }

                    case 11: // return
                        return;

                    case 12: // two-byte operators (flex, etc.)
                    {
                        if (pos >= cs.Length) break;
                        int b1 = cs[pos]; pos++;
                        switch (b1)
                        {
                            case 34: // hflex
                            case 35: // flex
                            case 36: // hflex1
                            case 37: // flex1
                                // Flex is just two curves — consume args and emit curves
                                EmitFlex(b1, stack, currentContour, ref x, ref y);
                                break;
                            default:
                                // Unknown 2-byte op, clear stack
                                break;
                        }
                        stack.Clear();
                        break;
                    }

                    default:
                        // Unknown operator, clear stack
                        stack.Clear();
                        break;
                }
            }
        }

        private void EmitFlex(int flexOp, List<float> stack, List<ContourPoint> contour, ref float x, ref float y)
        {
            // Every flex operator draws two cubic curves (Type 2 charstring spec, §4.1 "flex"); the
            // flex depth argument is a hinting threshold, which outlines ignore.
            if (flexOp == 35 && stack.Count >= 12) // flex: dx1..dy6 + fd
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    int i = pass * 6;
                    float c1x = x + stack[i], c1y = y + stack[i + 1];
                    float c2x = c1x + stack[i + 2], c2y = c1y + stack[i + 3];
                    x = c2x + stack[i + 4]; y = c2y + stack[i + 5];
                    contour.Add(new ContourPoint(c1x, c1y, false, true));
                    contour.Add(new ContourPoint(c2x, c2y, false, true));
                    contour.Add(new ContourPoint(x, y, true));
                }
            }
            else if (flexOp == 34 && stack.Count >= 7) // hflex
            {
                float c1x = x + stack[0], c1y = y;
                float c2x = c1x + stack[1], c2y = c1y + stack[2];
                float c3x = c2x + stack[3]; x = c3x;
                contour.Add(new ContourPoint(c1x, c1y, false, true));
                contour.Add(new ContourPoint(c2x, c2y, false, true));
                contour.Add(new ContourPoint(c3x, c2y, true));

                c1x = c3x + stack[4]; c1y = c2y;
                c2x = c1x + stack[5]; c2y = y;
                x = c2x + stack[6];
                contour.Add(new ContourPoint(c1x, c1y, false, true));
                contour.Add(new ContourPoint(c2x, c2y, false, true));
                contour.Add(new ContourPoint(x, y, true));
            }
            else if (flexOp == 36 && stack.Count >= 9) // hflex1: dx1 dy1 dx2 dy2 dx3 dx4 dx5 dy5 dx6 — ends at the starting height
            {
                // ⚠ Both used to be skipped ("simplified — just consume stack"): the pen stayed where
                // it was and the glyph's next curves were drawn from the wrong place (Adobe Devanagari
                // ल्ख, found by the CFF merge's comparison with FreeType, 2026-09-30).
                float y0 = y;
                float c1x = x + stack[0], c1y = y + stack[1];
                float c2x = c1x + stack[2], c2y = c1y + stack[3];
                float ex = c2x + stack[4];
                contour.Add(new ContourPoint(c1x, c1y, false, true));
                contour.Add(new ContourPoint(c2x, c2y, false, true));
                contour.Add(new ContourPoint(ex, c2y, true));

                float d1x = ex + stack[5], d1y = c2y;
                float d2x = d1x + stack[6], d2y = d1y + stack[7];
                x = d2x + stack[8]; y = y0;
                contour.Add(new ContourPoint(d1x, d1y, false, true));
                contour.Add(new ContourPoint(d2x, d2y, false, true));
                contour.Add(new ContourPoint(x, y, true));
            }
            else if (flexOp == 37 && stack.Count >= 11) // flex1: dx1 dy1 … dx5 dy5 d6 — d6 runs along the longer way
            {
                float x0 = x, y0 = y;
                var p = new float[10];
                float px = x, py = y;
                for (int k = 0; k < 5; k++)
                {
                    px += stack[k * 2]; py += stack[k * 2 + 1];
                    p[k * 2] = px; p[k * 2 + 1] = py;
                }
                float dx = px - x0, dy = py - y0;
                float ex, ey;
                if (Math.Abs(dx) > Math.Abs(dy)) { ex = px + stack[10]; ey = y0; }
                else { ex = x0; ey = py + stack[10]; }
                contour.Add(new ContourPoint(p[0], p[1], false, true));
                contour.Add(new ContourPoint(p[2], p[3], false, true));
                contour.Add(new ContourPoint(p[4], p[5], true));
                contour.Add(new ContourPoint(p[6], p[7], false, true));
                contour.Add(new ContourPoint(p[8], p[9], false, true));
                contour.Add(new ContourPoint(ex, ey, true));
                x = ex; y = ey;
            }
        }

        #endregion

        #region CFF Number/Index Parsing

        private float ReadCharstringOperand(byte[] cs, ref int pos)
        {
            int b0 = cs[pos];
            if (b0 == 28) // 16-bit signed int
            {
                pos++;
                short val = (short)((cs[pos] << 8) | cs[pos + 1]);
                pos += 2;
                return val;
            }
            if (b0 >= 32 && b0 <= 246)
            {
                pos++;
                return b0 - 139;
            }
            if (b0 >= 247 && b0 <= 250)
            {
                int b1 = cs[pos + 1];
                pos += 2;
                return (b0 - 247) * 256 + b1 + 108;
            }
            if (b0 >= 251 && b0 <= 254)
            {
                int b1 = cs[pos + 1];
                pos += 2;
                return -(b0 - 251) * 256 - b1 - 108;
            }
            if (b0 == 255) // 32-bit fixed-point (16.16)
            {
                pos++;
                int val = (cs[pos] << 24) | (cs[pos + 1] << 16) | (cs[pos + 2] << 8) | cs[pos + 3];
                pos += 4;
                return val / 65536f;
            }
            pos++;
            return 0;
        }

        private float ReadDictOperand(byte[] data, ref int pos)
        {
            int b0 = data[pos];
            if (b0 == 28) { pos++; short v = (short)((data[pos] << 8) | data[pos + 1]); pos += 2; return v; }
            if (b0 == 29) { pos++; int v = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]; pos += 4; return v; }
            if (b0 == 30) // real number
            {
                pos++;
                // BCD encoded real — simplified: skip nibbles until 0xf terminator
                while (pos < data.Length)
                {
                    int b = data[pos]; pos++;
                    if ((b & 0x0f) == 0x0f || (b >> 4) == 0x0f) break;
                }
                return 0; // Simplified — real numbers in DICT are rare for our use
            }
            if (b0 >= 32 && b0 <= 246) { pos++; return b0 - 139; }
            if (b0 >= 247 && b0 <= 250) { int b1 = data[pos + 1]; pos += 2; return (b0 - 247) * 256 + b1 + 108; }
            if (b0 >= 251 && b0 <= 254) { int b1 = data[pos + 1]; pos += 2; return -(b0 - 251) * 256 - b1 - 108; }
            pos++;
            return 0;
        }

        private float ReadDictOperandFromData(ref int pos)
        {
            int b0 = _data[pos];
            if (b0 == 28) { pos++; short v = (short)((_data[pos] << 8) | _data[pos + 1]); pos += 2; return v; }
            if (b0 == 29) { pos++; int v = (_data[pos] << 24) | (_data[pos + 1] << 16) | (_data[pos + 2] << 8) | _data[pos + 3]; pos += 4; return v; }
            if (b0 == 30) { pos++; while (pos < _data.Length) { int b = _data[pos]; pos++; if ((b & 0x0f) == 0x0f || (b >> 4) == 0x0f) break; } return 0; }
            if (b0 >= 32 && b0 <= 246) { pos++; return b0 - 139; }
            if (b0 >= 247 && b0 <= 250) { int b1 = _data[pos + 1]; pos += 2; return (b0 - 247) * 256 + b1 + 108; }
            if (b0 >= 251 && b0 <= 254) { int b1 = _data[pos + 1]; pos += 2; return -(b0 - 251) * 256 - b1 - 108; }
            pos++;
            return 0;
        }

        private int SkipIndex(int pos)
        {
            if (pos + 2 >= _data.Length) return -1;
            int count = ReadUInt16BE(pos); pos += 2;
            if (count == 0) return pos;
            if (pos >= _data.Length) return -1;
            int offSize = _data[pos]; pos++;
            if (offSize < 1 || offSize > 4) return -1;
            int offsetsEnd = pos + (count + 1) * offSize;
            if (offsetsEnd > _data.Length) return -1;
            int lastOff = ReadOffset(pos + count * offSize, offSize);
            int dataStart = pos + (count + 1) * offSize;
            return dataStart + lastOff - 1;
        }

        private void ParseSubrIndex(int pos, out int[] offsets, out int dataOffset, out int count)
        {
            offsets = null; dataOffset = 0; count = 0;
            if (pos + 2 >= _data.Length) return;
            count = ReadUInt16BE(pos); pos += 2;
            if (count == 0) return;
            if (pos >= _data.Length) return;
            int offSize = _data[pos]; pos++;
            if (offSize < 1 || offSize > 4) { count = 0; return; }
            if (pos + (count + 1) * offSize > _data.Length) { count = 0; return; }
            offsets = new int[count + 1];
            for (int i = 0; i <= count; i++)
                offsets[i] = ReadOffset(pos + i * offSize, offSize);
            dataOffset = pos + (count + 1) * offSize - 1;
        }

        private int ReadOffset(int pos, int offSize)
        {
            if (pos < 0 || pos + offSize > _data.Length) return 0;
            int val = 0;
            for (int i = 0; i < offSize; i++)
                val = (val << 8) | _data[pos + i];
            return val;
        }

        private int ReadUInt16BE(int pos)
        {
            if (pos < 0 || pos + 1 >= _data.Length) return 0;
            return (_data[pos] << 8) | _data[pos + 1];
        }

        private static int CalcSubrBias(int count)
        {
            if (count < 1240) return 107;
            if (count < 33900) return 1131;
            return 32768;
        }

        #endregion

        #region Bounding Box

        private static void ComputeBounds(GlyphOutline outline)
        {
            // Use ALL points for bounds (conservative overestimate).
            // For cubic Bezier, control points extend beyond the curve but provide
            // a safe bounding box. Using only on-curve points would UNDERESTIMATE
            // and clip the rendered glyph.
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var contour in outline.Contours)
            {
                if (contour.Points == null) continue;
                foreach (var pt in contour.Points)
                {
                    if (pt.X < minX) minX = pt.X;
                    if (pt.Y < minY) minY = pt.Y;
                    if (pt.X > maxX) maxX = pt.X;
                    if (pt.Y > maxY) maxY = pt.Y;
                }
            }

            if (minX != float.MaxValue)
            {
                outline.XMin = (int)Math.Floor(minX);
                outline.YMin = (int)Math.Floor(minY);
                outline.XMax = (int)Math.Ceiling(maxX);
                outline.YMax = (int)Math.Ceiling(maxY);
            }
        }

        #endregion
    }
}
