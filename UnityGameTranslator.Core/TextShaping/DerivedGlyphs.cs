using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// The glyph namer of a DERIVED font (DerivedFontWriter): every glyph of a shaped run — moved,
    /// spaced differently or left alone — is named by a private-use codepoint of its own, which the
    /// derived font maps to a composite of that glyph carrying the shaper's offset and advance.
    ///
    /// Why even an untouched glyph gets one (probe 1, Unity 6000.0): TMP 3.2+ and UI Toolkit's
    /// TextCore apply the font's GPOS to the glyph ids they meet, so an original glyph left in the
    /// string is positioned a second time. A composite is in no layout table.
    /// (<see cref="Core.ShapingFontAsset"/>, the namer of OUR TMP assets, names an untouched glyph
    /// by its own codepoint: those assets carry no layout table, nothing re-positions them.)
    ///
    /// The names are kept across launches (<see cref="Save"/> / <see cref="Load"/>): a string shaped
    /// yesterday names the same composites today. A name handed out since the last file was written
    /// makes the font <see cref="Dirty"/> — the caller writes a new derived file.
    ///
    /// PURE by contract (no Unity) — linked into Core.Checks. Main thread only, like the shapers.
    /// </summary>
    internal sealed class DerivedGlyphs : IGlyphNamer
    {
        private readonly IShapingFont _font;
        private readonly int _glyphCount;
        private readonly Dictionary<Key, int> _codepoints = new Dictionary<Key, int>();
        private readonly List<DerivedFontWriter.Added> _added = new List<DerivedFontWriter.Added>();
        private readonly HashSet<int> _usedCodepoints = new HashSet<int>();
        private int _next = PrivateGlyphs.First;
        private int _written;

        /// <summary>Said once when the private range runs out: the runs needing more stay unshaped.</summary>
        internal Action<string> OnExhausted;

        private struct Key : IEquatable<Key>
        {
            public int Glyph, DX, DY, AdvanceDelta;
            public bool Equals(Key o) => Glyph == o.Glyph && DX == o.DX && DY == o.DY && AdvanceDelta == o.AdvanceDelta;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => ((Glyph * 397 ^ DX) * 397 ^ DY) * 397 ^ AdvanceDelta;
        }

        internal DerivedGlyphs(IShapingFont font, int glyphCount)
        {
            _font = font;
            _glyphCount = glyphCount;
        }

        /// <summary>The glyphs the derived font must hold, in the order their codepoints were handed out.</summary>
        internal IList<DerivedFontWriter.Added> Added => _added;

        /// <summary>Names handed out since the last derived file — a new file is needed to show them.</summary>
        internal bool Dirty => _added.Count > _written;

        /// <summary>The derived file now holds every name handed out so far.</summary>
        internal void MarkWritten() => _written = _added.Count;

        public int CodepointFor(int glyph, int xOffset, int yOffset, int advanceDelta)
        {
            if (glyph <= 0 || glyph >= _glyphCount) return 0;
            var key = new Key { Glyph = glyph, DX = xOffset, DY = yOffset, AdvanceDelta = advanceDelta };
            if (_codepoints.TryGetValue(key, out int cp)) return cp;

            // Never a codepoint the font maps itself: the derived font keeps every mapping of its source.
            while (_next <= PrivateGlyphs.Last && _font.GlyphIndex(_next) > 0) _next++;
            if (_next > PrivateGlyphs.Last)
            {
                var said = OnExhausted;
                OnExhausted = null;
                said?.Invoke($"no private codepoint left ({PrivateGlyphs.Last - PrivateGlyphs.First + 1} in use) — runs needing a new one stay unshaped");
                return 0;
            }
            cp = _next++;
            _codepoints[key] = cp;
            _usedCodepoints.Add(cp);
            _added.Add(new DerivedFontWriter.Added
            {
                Codepoint = cp,
                Glyph = glyph,
                DX = xOffset,
                DY = yOffset,
                Advance = Math.Max(0, _font.AdvanceWidth(glyph) + advanceDelta),
            });
            return cp;
        }

        /// <summary>One line per name: codepoint, glyph, x offset, y offset, advance delta.</summary>
        internal string Save()
        {
            var sb = new StringBuilder();
            foreach (var kv in _codepoints)
                sb.Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(kv.Key.Glyph.ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(kv.Key.DX.ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(kv.Key.DY.ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(kv.Key.AdvanceDelta.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// The names a previous launch handed out, in codepoint order. A line that cannot be read, or
        /// names a glyph or a codepoint this font cannot take, is left out and counted in the result
        /// (the caller says how many) — the rest still names the same composites as before.
        /// </summary>
        internal int Load(string saved)
        {
            if (string.IsNullOrEmpty(saved)) return 0;
            var rows = new List<int[]>();
            int refused = 0;
            foreach (var line in saved.Split('\n'))
            {
                if (line.Length == 0) continue;
                var parts = line.Split(' ');
                var v = new int[5];
                bool ok = parts.Length == 5;
                for (int i = 0; ok && i < 5; i++) ok = int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]);
                ok = ok && PrivateGlyphs.Contains(v[0]) && v[1] > 0 && v[1] < _glyphCount && _font.GlyphIndex(v[0]) <= 0;
                if (ok) rows.Add(v); else refused++;
            }
            rows.Sort((a, b) => a[0].CompareTo(b[0]));
            foreach (var v in rows)
            {
                var key = new Key { Glyph = v[1], DX = v[2], DY = v[3], AdvanceDelta = v[4] };
                if (_codepoints.ContainsKey(key) || !_usedCodepoints.Add(v[0])) { refused++; continue; }
                _codepoints[key] = v[0];
                _added.Add(new DerivedFontWriter.Added
                {
                    Codepoint = v[0], Glyph = v[1], DX = v[2], DY = v[3],
                    Advance = Math.Max(0, _font.AdvanceWidth(v[1]) + v[4]),
                });
                if (v[0] >= _next) _next = v[0] + 1;
            }
            return refused;
        }
    }
}
