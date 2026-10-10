using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// A font the game keeps as an ATLAS only (a static TextMesh Pro asset, a UI Toolkit FontAsset —
    /// no font file in its data): each letter's picture read from the atlas, its outline traced back
    /// (AtlasTracer) and written into a TrueType font (DerivedFontWriter.FromOutlines). What Extract
    /// gives for such a font (GameFontFiles; analyse/export-polices-jeu.md, step 6).
    ///
    /// Two halves, so no frame waits: <see cref="Capture"/> on the main thread (the asset's tables, the
    /// atlas read back — Unity), <see cref="Write"/> on a worker (tracing, writing — pure).
    ///
    /// ⚠ TextCore assets only (TextMesh Pro 1.4+, UI Toolkit): the old TextMesh Pro (TMProOld) keeps its
    /// letters in another table — refused, said, until it is read too.
    /// </summary>
    internal static class AtlasFontExport
    {
        internal sealed class Letter
        {
            public int Unicode;
            public float[] Grid;          // the letter's picture with its margin, rows from the TOP, 0..1
            public int Width, Height;
            public double OriginX, OriginY; // the grid's top-left corner, in sampling pixels, y up from the baseline
            public double Advance;        // sampling pixels
        }

        internal sealed class Captured
        {
            public string Family;
            public double PointSize;      // pixels per em the atlas was drawn at
            public double Ascent, Descent;
            public readonly List<Letter> Letters = new List<Letter>();
        }

        private const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>
        /// Main thread: the letters of a TextCore font asset as pictures; null with
        /// <paramref name="refusal"/> when the asset cannot be read that way.
        /// </summary>
        internal static Captured Capture(object asset, out string refusal)
        {
            refusal = null;
            if (asset == null) { refusal = "no such font asset is loaded"; return null; }
            var type = asset.GetType();
            var shape = FontManager.SdfShape.Of(asset);
            var characters = type.GetProperty("characterTable", Pub)?.GetValue(asset, null);
            if (shape == null || characters == null)
            {
                // The old TextMesh Pro (TMProOld): its letters in m_glyphInfoList, its sizes in m_fontInfo.
                var old = CaptureOld(asset, type, out refusal);
                if (old != null || refusal != null) return old;
                refusal = "its letters are not in a table this mod reads";
                return null;
            }
            object face = type.GetProperty("faceInfo", Pub)?.GetValue(asset, null);
            var captured = new Captured
            {
                Family = Text(face, "familyName") ?? TypeHelper.FontNameOf(asset) ?? "Game font",
                PointSize = shape.Sampling,
                Ascent = Number(face, "ascentLine"),
                Descent = Number(face, "descentLine"),
            };
            if (captured.PointSize <= 0) { refusal = "its atlas names no drawing size"; return null; }

            var atlases = new Dictionary<int, byte[]>();
            var sizes = new Dictionary<int, Vector2Int>();
            var textures = type.GetProperty("atlasTextures", Pub)?.GetValue(asset, null);
            int padding = shape.Padding;
            foreach (var character in Items(characters))
            {
                if (character == null) continue;
                int unicode = (int)Number(character, "unicode");
                var glyph = Get(character, "glyph");
                if (glyph == null || unicode <= 0) continue;
                var metrics = Get(glyph, "metrics");
                var rect = Get(glyph, "glyphRect");
                int atlasIndex = (int)Number(glyph, "atlasIndex");
                var letter = new Letter { Unicode = unicode, Advance = Number(metrics, "horizontalAdvance") };
                int rw = (int)Number(rect, "width"), rh = (int)Number(rect, "height");
                if (rw > 0 && rh > 0)
                {
                    if (!atlases.TryGetValue(atlasIndex, out var raw))
                    {
                        raw = ReadBack(ItemAt(textures, atlasIndex) ?? type.GetProperty("atlasTexture", Pub)?.GetValue(asset, null), out var size);
                        atlases[atlasIndex] = raw;
                        sizes[atlasIndex] = size;
                    }
                    if (raw == null) { refusal = "its atlas cannot be read back"; return null; }
                    var tex = sizes[atlasIndex];
                    int x0 = (int)Number(rect, "x") - padding, y0 = (int)Number(rect, "y") - padding;
                    int w = rw + 2 * padding, h = rh + 2 * padding;
                    var grid = new float[w * h];
                    for (int gy = 0; gy < h; gy++)
                    {
                        int ty = y0 + h - 1 - gy;                                 // texture rows go up, the grid's down
                        if (ty < 0 || ty >= tex.y) continue;
                        for (int gx = 0; gx < w; gx++)
                        {
                            int tx = x0 + gx;
                            if (tx < 0 || tx >= tex.x) continue;
                            grid[gy * w + gx] = raw[(ty * tex.x + tx) * 4 + 3] / 255f;
                        }
                    }
                    letter.Grid = grid;
                    letter.Width = w;
                    letter.Height = h;
                    // The picture starts on the whole pixel at or before the letter's left edge, and at
                    // or above its top — as FreeType places a bitmap (bitmap_left, bitmap_top): with the
                    // fractional bearings, an unhinted atlas came back a pixel off (bench, 2026-10-10).
                    letter.OriginX = Math.Floor(Number(metrics, "horizontalBearingX")) - padding;
                    letter.OriginY = Math.Ceiling(Number(metrics, "horizontalBearingY")) + padding;
                }
                captured.Letters.Add(letter);
            }
            if (captured.Letters.Count == 0) { refusal = "it holds no letter"; return null; }
            return captured;
        }

        /// <summary>
        /// The old TextMesh Pro's asset: public fields, a glyph's y counted from the atlas's TOP (as the
        /// mod writes them itself, CustomFontLoader.CreateGlyph), xOffset / yOffset its left and top
        /// bearings, its box without the padding. Null and no refusal when the asset is not one.
        /// </summary>
        private static Captured CaptureOld(object asset, Type type, out string refusal)
        {
            refusal = null;
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            object Member(object o, Type t, string name) =>
                o == null ? null : (t.GetField(name, any)?.GetValue(o) ?? t.GetProperty(name, any)?.GetValue(o, null));
            var glyphs = Member(asset, type, "m_glyphInfoList");
            var info = Member(asset, type, "m_fontInfo") ?? Member(asset, type, "fontInfo");
            if (glyphs == null || info == null) return null;
            double Num(object o, string name) { var v = Member(o, o.GetType(), name); return v == null ? 0 : Convert.ToDouble(v); }

            var captured = new Captured
            {
                Family = Member(info, info.GetType(), "Name") as string ?? TypeHelper.FontNameOf(asset) ?? "Game font",
                PointSize = Num(info, "PointSize"),
                Ascent = Num(info, "Ascender"),
                Descent = Num(info, "Descender"),
            };
            if (captured.PointSize <= 0) { refusal = "its atlas names no drawing size"; return null; }
            int padding = (int)Math.Round(Num(info, "Padding"));
            var raw = ReadBack(Member(asset, type, "atlas"), out var tex);
            if (raw == null) { refusal = "its atlas cannot be read back"; return null; }

            // The list can lack the space, which TMProOld then answers from the dictionary it builds when
            // it reads its asset (as KeepOldSpecialGlyphs reads it): without it, a traced font drew its
            // spaces with .notdef's half-em (Silksong's perpetua_tmpro, 2026-10-10).
            var all = new List<object>(Items(glyphs));
            var listed = new HashSet<int>();
            foreach (var g in all) if (g != null) listed.Add((int)Num(g, "id"));
            var dictionary = type.GetProperty("characterDictionary", Pub)?.GetValue(asset, null);
            var tryGet = dictionary?.GetType().GetMethod("TryGetValue");
            if (tryGet != null)
                foreach (int special in new[] { 0x20, 0xA0 })
                {
                    if (listed.Contains(special)) continue;
                    var args = new object[] { special, null };
                    if ((bool)tryGet.Invoke(dictionary, args) && args[1] != null) all.Add(args[1]);
                }

            foreach (var glyph in all)
            {
                if (glyph == null) continue;
                int unicode = (int)Num(glyph, "id");
                if (unicode <= 0) continue;
                var letter = new Letter { Unicode = unicode, Advance = Num(glyph, "xAdvance") };
                int gw = (int)Math.Round(Num(glyph, "width")), gh = (int)Math.Round(Num(glyph, "height"));
                if (gw > 0 && gh > 0)
                {
                    int x0 = (int)Math.Round(Num(glyph, "x")) - padding, top = (int)Math.Round(Num(glyph, "y")) - padding;
                    int w = gw + 2 * padding, h = gh + 2 * padding;
                    var grid = new float[w * h];
                    for (int gy = 0; gy < h; gy++)
                    {
                        int ty = tex.y - 1 - (top + gy);                          // rows counted from the top
                        if (ty < 0 || ty >= tex.y) continue;
                        for (int gx = 0; gx < w; gx++)
                        {
                            int tx = x0 + gx;
                            if (tx < 0 || tx >= tex.x) continue;
                            grid[gy * w + gx] = raw[(ty * tex.x + tx) * 4 + 3] / 255f;
                        }
                    }
                    letter.Grid = grid;
                    letter.Width = w;
                    letter.Height = h;
                    letter.OriginX = Math.Floor(Num(glyph, "xOffset")) - padding;
                    letter.OriginY = Math.Ceiling(Num(glyph, "yOffset")) + padding;
                }
                captured.Letters.Add(letter);
            }
            if (captured.Letters.Count == 0) { refusal = "it holds no letter"; return null; }
            return captured;
        }

        /// <summary>Worker: the traced font, 1000 units per em.</summary>
        internal static byte[] Write(Captured captured)
        {
            const int upem = 1000;
            double s = upem / captured.PointSize;
            var outlines = new List<DerivedFontWriter.Outline>();
            var cmap = new SortedDictionary<int, int>();
            foreach (var letter in captured.Letters)
            {
                if (cmap.ContainsKey(letter.Unicode)) continue;
                var outline = new DerivedFontWriter.Outline { Advance = (int)Math.Round(letter.Advance * s) };
                if (letter.Grid != null)
                    foreach (var contour in AtlasTracer.Trace(letter.Grid, letter.Width, letter.Height))
                    {
                        var points = new List<DerivedFontWriter.QuadPoint>(contour.Count);
                        foreach (var p in contour)
                            points.Add(new DerivedFontWriter.QuadPoint
                            {
                                X = (int)Math.Round((letter.OriginX + p.X) * s),
                                Y = (int)Math.Round((letter.OriginY - p.Y) * s),
                                On = true,
                            });
                        outline.Contours.Add(points);
                    }
                outlines.Add(outline);
                cmap[letter.Unicode] = outlines.Count;
            }
            int ascent = (int)Math.Round(captured.Ascent * s), descent = (int)Math.Round(captured.Descent * s);
            if (ascent <= 0) ascent = 800;
            if (descent >= 0) descent = -200;
            return DerivedFontWriter.FromOutlines(captured.Family, upem, ascent, descent, outlines, cmap);
        }

        // ── reflection over TextCore's objects (Mono and IL2CPP) ──────────────────────────────

        private static byte[] ReadBack(object texture, out Vector2Int size)
        {
            size = default(Vector2Int);
            var tex2D = TypeHelper.Il2CppCast(texture, typeof(Texture2D)) as Texture2D ?? texture as Texture2D;
            if (tex2D == null) return null;
            Texture2D copy = null;
            try
            {
                copy = TextureUtils.MakeReadableCopy(tex2D);
                var raw = copy != null ? TextureUtils.GetRawTextureDataSafe(copy) : null;
                if (raw == null || raw.Length < copy.width * copy.height * 4) return null;
                size = new Vector2Int(copy.width, copy.height);
                return raw;
            }
            finally { if (copy != null) UnityEngine.Object.Destroy(copy); }
        }

        private static object Get(object o, string name) => o?.GetType().GetProperty(name, Pub)?.GetValue(o, null);

        private static double Number(object o, string name)
        {
            var v = Get(o, name);
            return v == null ? 0 : Convert.ToDouble(v);
        }

        private static string Text(object o, string name) => Get(o, name) as string ?? Get(o, name)?.ToString();

        /// <summary>The elements of a list (a managed List, or IL2CPP's), by Count and its indexer.</summary>
        private static IEnumerable<object> Items(object list)
        {
            if (list == null) yield break;
            var type = list.GetType();
            var count = type.GetProperty("Count", Pub) ?? type.GetProperty("Length", Pub);
            var item = type.GetProperty("Item", Pub);
            if (count == null || item == null) yield break;
            int n = Convert.ToInt32(count.GetValue(list, null));
            for (int i = 0; i < n; i++) yield return item.GetValue(list, new object[] { i });
        }

        private static object ItemAt(object list, int index)
        {
            int i = 0;
            foreach (var o in Items(list)) { if (i == index) return o; i++; }
            return null;
        }
    }
}
