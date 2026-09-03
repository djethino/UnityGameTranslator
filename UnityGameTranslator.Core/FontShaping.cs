using System;
using System.Collections.Generic;
using System.IO;
using UnityGameTranslator.Core.Rasterizer;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The shaping side of a font asset of ours: the font file's OpenType tables (read once),
    /// the map from glyph index to the codepoint that shows it, and the positioned variants —
    /// a glyph drawn shifted by GPOS is a SECOND entry of the asset on the SAME atlas rectangle
    /// with the shifted bearings, under a private-use codepoint handed out on demand and kept
    /// in the .gen.json so the next launch starts with them. No pixel is ever rasterized for a
    /// variant.
    ///
    /// Only fonts the rasterizer built from a TTF qualify: they carry a glyph index per entry
    /// (pipeline v5) and the file to read GSUB/GPOS from. A user-provided msdf-atlas-gen JSON
    /// has neither, and shaping is simply not offered on it.
    /// </summary>
    internal sealed class ShapingFontAsset : IGlyphNamer
    {
        private readonly CustomFontLoader.CustomFontInfo _info;
        private readonly Dictionary<int, int> _glyphToCodepoint = new Dictionary<int, int>();
        private readonly Dictionary<int, CustomFontLoader.GlyphInfo> _entryByGlyph = new Dictionary<int, CustomFontLoader.GlyphInfo>();
        private readonly Dictionary<long, int> _variants = new Dictionary<long, int>();
        private readonly HashSet<int> _usedPrivate = new HashSet<int>();
        private readonly float _unitsPerEm;
        private int _nextPrivate = TtfFontPipeline.PrivateGlyphBase;
        private bool _exhaustedSaid;

        internal TtfShapingFont Font { get; }
        internal string Name => _info.Name;

        private ShapingFontAsset(CustomFontLoader.CustomFontInfo info, TtfParser parser)
        {
            _info = info;
            Font = new TtfShapingFont(parser);
            _unitsPerEm = parser.Metrics.UnitsPerEm;
            foreach (var g in info.AtlasData.glyphs)
            {
                if (g.glyphIndex <= 0) continue;
                bool isPrivate = g.unicode >= TtfFontPipeline.PrivateGlyphBase && g.unicode <= TtfFontPipeline.PrivateGlyphLast;
                if (isPrivate) _usedPrivate.Add(g.unicode);
                // The natural entry of a glyph: its real codepoint when it has one, else its
                // private one. A variant (advance or bounds shifted) is recognised by being
                // private AND another entry already naming the glyph — recovered into the
                // variant map below.
                if (!_glyphToCodepoint.ContainsKey(g.glyphIndex) || (!isPrivate && IsPrivate(_glyphToCodepoint[g.glyphIndex])))
                {
                    _glyphToCodepoint[g.glyphIndex] = g.unicode;
                    _entryByGlyph[g.glyphIndex] = g;
                }
            }
            foreach (var g in info.AtlasData.glyphs)
            {
                if (g.glyphIndex <= 0 || !IsPrivate(g.unicode) || _glyphToCodepoint[g.glyphIndex] == g.unicode) continue;
                var natural = _entryByGlyph[g.glyphIndex];
                int dx = Units((g.planeBounds?.left ?? 0) - (natural.planeBounds?.left ?? 0));
                int dy = Units((g.planeBounds?.top ?? 0) - (natural.planeBounds?.top ?? 0));
                int da = Units(g.advance - natural.advance);
                _variants[Key(g.glyphIndex, dx, dy, da)] = g.unicode;
            }
            while (_usedPrivate.Contains(_nextPrivate) || parser.HasCodepoint(_nextPrivate)) _nextPrivate++;
        }

        private static bool IsPrivate(int cp) => cp >= TtfFontPipeline.PrivateGlyphBase && cp <= TtfFontPipeline.PrivateGlyphLast;
        private int Units(float em) => (int)Math.Round(em * _unitsPerEm);
        private static long Key(int glyph, int dx, int dy, int da) => ((long)glyph << 42) ^ ((long)(dx & 0x3FFF) << 28) ^ ((long)(dy & 0x3FFF) << 14) ^ (long)(da & 0x3FFF);

        public int CodepointFor(int glyph, int xOffset, int yOffset, int advanceDelta)
        {
            if (!_glyphToCodepoint.TryGetValue(glyph, out int natural)) return 0;
            if (xOffset == 0 && yOffset == 0 && advanceDelta == 0) return natural;
            long key = Key(glyph, xOffset, yOffset, advanceDelta);
            if (_variants.TryGetValue(key, out int cp)) return cp;

            // A new variant: the next free private codepoint, an entry cloned from the natural
            // one with its bearings and advance shifted, into the data, the live asset and the cache.
            while (_nextPrivate <= TtfFontPipeline.PrivateGlyphLast && (_usedPrivate.Contains(_nextPrivate) || Font.GlyphIndex(_nextPrivate) > 0)) _nextPrivate++;
            if (_nextPrivate > TtfFontPipeline.PrivateGlyphLast)
            {
                if (!_exhaustedSaid)
                {
                    _exhaustedSaid = true;
                    TranslatorCore.LogWarning($"[FontShaping] {Name}: no private codepoint left for positioned glyphs — runs needing one stay unshaped");
                }
                return 0;
            }
            var source = _entryByGlyph[glyph];
            var entry = new CustomFontLoader.GlyphInfo
            {
                unicode = _nextPrivate,
                glyphIndex = glyph,
                advance = source.advance + advanceDelta / _unitsPerEm,
                atlasIndex = source.atlasIndex,
                atlasBounds = source.atlasBounds,
                planeBounds = source.planeBounds == null ? null : new CustomFontLoader.BoundsInfo
                {
                    left = source.planeBounds.left + xOffset / _unitsPerEm,
                    right = source.planeBounds.right + xOffset / _unitsPerEm,
                    bottom = source.planeBounds.bottom + yOffset / _unitsPerEm,
                    top = source.planeBounds.top + yOffset / _unitsPerEm,
                },
            };
            cp = _nextPrivate++;
            _usedPrivate.Add(cp);
            _variants[key] = cp;
            _info.AtlasData.glyphs.Add(entry);
            if (!CustomFontLoader.AddGlyphsToLiveAsset(_info, new List<CustomFontLoader.GlyphInfo> { entry }))
                TranslatorCore.LogWarning($"[FontShaping] {Name}: variant U+{cp:X4} (glyph {glyph}, {xOffset},{yOffset},{advanceDelta}) not added to the live asset — it will show as a missing glyph until the next launch");
            CustomFontLoader.SaveGenJson(_info);
            return cp;
        }

        // ───────────────────────────── registry ─────────────────────────────

        private static readonly Dictionary<string, ShapingFontAsset> _byFont = new Dictionary<string, ShapingFontAsset>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The shaping asset behind a component's font settings: the fallback configured for
        /// that game font, when it is a TTF-built font of ours that is loaded and carries
        /// layout tables. Null otherwise — the caller then leaves the text to the codepoint
        /// path (IndicReorderer), which is all a font we do not control can take.
        /// </summary>
        internal static ShapingFontAsset ForSettings(string settingsFontName)
        {
            if (string.IsNullOrEmpty(settingsFontName)) return null;
            if (!TranslatorCore.FontSettingsMap.TryGetValue(settingsFontName, out var settings) || string.IsNullOrEmpty(settings.fallback)) return null;
            if (FontManager.IsGameFontRef(settings.fallback)) return null;
            string name = FontManager.StripFontPrefix(settings.fallback);
            if (_byFont.TryGetValue(name, out var asset)) return asset;
            if (_refused.Contains(name)) return null;
            if (!CustomFontLoader.CustomFonts.TryGetValue(name, out var info) || info == null) return null;
            // Not loaded yet: not refused either — the asset may be built a moment later.
            if (!info.IsLoaded || info.FontAsset == null || info.AtlasData?.glyphs == null) return null;
            if (!info.IsTtf || !File.Exists(info.TtfPath) || info.AtlasData.pipelineVersion < 5)
            {
                _refused.Add(name);
                TranslatorCore.LogInfo($"[FontShaping] {name}: no OpenType shaping — {(info.IsTtf ? "atlas predates pipeline v5 or the TTF is gone" : "not built from a TTF")}");
                return null;
            }
            try
            {
                var parser = new TtfParser(File.ReadAllBytes(info.TtfPath));
                if (parser.Layout.Gsub == null && parser.Layout.Gpos == null)
                {
                    _refused.Add(name);
                    TranslatorCore.LogInfo($"[FontShaping] {name}: no GSUB/GPOS tables — nothing to shape with");
                    return null;
                }
                asset = new ShapingFontAsset(info, parser);
                _byFont[name] = asset;
                TranslatorCore.LogInfo($"[FontShaping] {name}: OpenType shaping ready ({parser.GlyphCount} glyphs, {asset._variants.Count} positioned variants from the cache)");
                return asset;
            }
            catch (Exception ex)
            {
                _refused.Add(name);
                TranslatorCore.LogWarning($"[FontShaping] {name}: cannot read the font for shaping — {ex.Message}");
                return null;
            }
        }

        /// <summary>Forget every asset — the fonts are being reloaded (Apply in the Fonts tab).</summary>
        internal static void InvalidateAll()
        {
            _byFont.Clear();
            _refused.Clear();
        }
    }
}
