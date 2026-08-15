using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseLib.Runtime;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// The mod's small marks, built as pixels at run time.
    ///
    /// ⚠ **Why not an image file.** A PNG cannot be decoded here: `UnityEngine.ImageConversionModule`
    /// is not among the assemblies this project compiles against, UniverseLib exposes only
    /// `EncodeToPNG` and no decoder, and on IL2CPP the byte array would need converting before the
    /// call anyway. Every route through a file format is closed, so the pixels are computed instead.
    ///
    /// ⚠ **Why that is not a workaround.** These three marks are discs, boxes and capsules — the very
    /// shapes the manager draws as vector paths, from the same numbers in the same 16x16 box. Written
    /// this way the two products cannot drift apart, and nothing has to be embedded, merged by
    /// ILRepack or shipped beside the DLL.
    ///
    /// ⚠ **What this does NOT solve: flags.** A flag is picture data, not geometry, and it will have
    /// to arrive as an embedded raster. That is why the pixels and the drawing are separate below:
    /// <see cref="FromCoverage"/> takes coverage bytes from wherever they come from, and only
    /// <see cref="Draw"/> knows they were computed. A flag atlas plugs into the first without
    /// touching the second.
    ///
    /// 🔴 Every Unity call here goes through <see cref="TextureHelper"/>. `SetPixels32` takes a
    /// managed array, which on IL2CPP is not the array the engine expects — the same class of trap
    /// as AddListener, and UniverseLib has already solved it. Never call the engine directly from
    /// this file.
    /// </summary>
    public static class Icons
    {
        /// <summary>The box every mark is drawn in — the manager's, so the shapes match.</summary>
        private const double Box = 16;

        /// <summary>Pixels per box unit. Two gives a 32x32 mark, ample for a 14pt row.</summary>
        private const int Scale = 2;

        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        /// <summary>
        /// The mark for an identity handed out by <see cref="Common.EditScope.Mark"/>.
        ///
        /// ⚠ Returns null rather than a placeholder for an unknown name. A mark nobody recognises
        /// on a control that means "where this writes" would be worse than none: the caller can see
        /// a missing icon, it cannot see a wrong one.
        /// </summary>
        public static Sprite Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            Sprite cached;
            if (_cache.TryGetValue(id, out cached)) return cached;

            var shape = Draw(id);
            if (shape == null) return null;

            var sprite = FromCoverage(shape, Box * Scale, Box * Scale);
            _cache[id] = sprite;
            return sprite;
        }

        /// <summary>
        /// A language's flag, drawn from the pixels the socle holds.
        ///
        /// 🔴 **Not the same path as a mark, and it cannot be.** <see cref="FromCoverage"/> makes a
        /// WHITE sprite whose alpha carries the shape, exactly so that one texture serves every
        /// colour a mark is ever shown in. A flag carries its own colours, so it needs a texture of
        /// its own — and ⚠ **the Image that shows it must be left white**, or the button's tint
        /// repaints the flag.
        ///
        /// ⚠ Point filtering, deliberately: these are drawn as pixels and a blurred sixteen-pixel
        /// flag is worse than a crisp one, since half of them are only told apart by an edge.
        ///
        /// Returns null for a language whose flag has not been drawn yet — ordinary, and the caller
        /// shows the tag alone.
        /// </summary>
        public static Sprite Flag(string flagId)
        {
            if (string.IsNullOrEmpty(flagId)) return null;

            var key = "flag:" + flagId;

            Sprite cached;
            if (_cache.TryGetValue(key, out cached)) return cached;

            var pixels = Common.Flags.Pixels(flagId);
            if (pixels == null) return null;

            int w = Common.Flags.Width, h = Common.Flags.Height;
            var colours = new Color32[w * h];

            foreach (var pixel in pixels)
            {
                // ⚠ **Unity's textures start at the BOTTOM, the catalogue describes flags from the
                // top.** Forgetting this ships every flag upside down — which for a good half of
                // them (Poland and Indonesia, Netherlands and Croatia) is another country's flag,
                // not an obvious glitch.
                int at = (h - 1 - pixel.Y) * w + pixel.X;

                colours[at] = pixel.Transparent
                    ? new Color32(0, 0, 0, 0)
                    : new Color32((byte)((pixel.Rgb >> 16) & 0xFF),
                                  (byte)((pixel.Rgb >> 8) & 0xFF),
                                  (byte)(pixel.Rgb & 0xFF), 255);
            }

            var texture = TextureHelper.NewTexture2D(w, h, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;

            TextureHelper.SetPixels32Safe(texture, colours);
            texture.Apply(false, false);

            // Same reason as a mark's texture: the cache outlives every panel that asked for it.
            UnityEngine.Object.DontDestroyOnLoad(texture);

            var sprite = TextureHelper.CreateSprite(texture);
            _cache[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// Turns coverage — one byte of "how much of this pixel is inside the shape" — into a white
        /// sprite whose transparency carries the drawing.
        ///
        /// White on purpose: uGUI multiplies a sprite by its Image's colour, so one texture serves
        /// every shade the mark is ever shown in. Baking the colour in would mean one texture per
        /// state, and a mark that could not follow a theme.
        /// </summary>
        private static Sprite FromCoverage(byte[] coverage, double width, double height)
        {
            int w = (int)width, h = (int)height;

            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, 255, 255, coverage[i]);

            var texture = TextureHelper.NewTexture2D(w, h, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;

            TextureHelper.SetPixels32Safe(texture, pixels);
            texture.Apply(false, false);

            // Not destroyed with the panel that first asked for it: the cache outlives every panel,
            // and a texture collected while another still points at it renders as a pink square.
            UnityEngine.Object.DontDestroyOnLoad(texture);

            return TextureHelper.CreateSprite(texture);
        }

        /// <summary>
        /// The three marks, in the manager's coordinates.
        ///
        /// ⚠ These numbers are the ones in `Glyphs.cs`. A mark changed on one side and not the other
        /// is a control that stops being recognisable between two products — which is the only
        /// reason this control exists at all.
        /// </summary>
        private static byte[] Draw(string id)
        {
            int w = (int)(Box * Scale), h = (int)(Box * Scale);
            var coverage = new byte[w * h];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Pixel centres, back in box units. Unity's textures start at the bottom, the
                    // drawings are described from the top — hence the flip, and forgetting it turns
                    // the screen's stand into an aerial.
                    double px = (x + 0.5) / Scale;
                    double py = Box - (y + 0.5) / Scale;

                    double distance;
                    switch (id)
                    {
                        case "cloud": distance = Cloud(px, py); break;
                        case "display": distance = Display(px, py); break;
                        case "link": distance = Link(px, py); break;
                        default: return null;
                    }

                    // One pixel of softening across the edge. Without it a 32-pixel mark shown at
                    // 14 comes out ragged, and three ragged marks in a row read as a rendering
                    // fault rather than as a control.
                    double alpha = 0.5 - distance * Scale;
                    if (alpha < 0) alpha = 0;
                    else if (alpha > 1) alpha = 1;

                    coverage[y * w + x] = (byte)(alpha * 255 + 0.5);
                }
            }

            return coverage;
        }

        // ── The three marks ───────────────────────────────────────────────────────────────────

        /// <summary>Three puffs resting on a bar, their bottoms aligned so the base reads flat.</summary>
        private static double Cloud(double x, double y)
        {
            return Min(Disc(x, y, 5.2, 9.6, 3),
                   Min(Disc(x, y, 9.6, 7.6, 3.8),
                   Min(Disc(x, y, 11.9, 10, 2.6),
                       Box_(x, y, 5.2, 9.6, 11.9, 12.6))));
        }

        /// <summary>A bezel with its screen punched out, on a neck and a foot.</summary>
        private static double Display(double x, double y)
        {
            var bezel = Max(Box_(x, y, 1.6, 2.8, 14.4, 11.2),
                           -Box_(x, y, 3.1, 4.3, 12.9, 9.7));

            return Min(bezel,
                   Min(Box_(x, y, 6.6, 11.2, 9.4, 13),
                       Box_(x, y, 4.6, 13, 11.4, 14.4)));
        }

        /// <summary>Two links of a chain, each a capsule with a capsule taken out of it.</summary>
        private static double Link(double x, double y)
        {
            var left = Max(Capsule(x, y, 3.9, 8, 6.6, 8, 2.6),
                          -Capsule(x, y, 3.9, 8, 6.6, 8, 1.1));

            var right = Max(Capsule(x, y, 9.4, 8, 12.1, 8, 2.6),
                           -Capsule(x, y, 9.4, 8, 12.1, 8, 1.1));

            return Min(left, right);
        }

        // ── Distances ─────────────────────────────────────────────────────────────────────────
        //
        // Signed: negative inside, zero on the edge. Union is the nearer of two, subtraction the
        // farther of one and the other reversed — which is why a hole is written as a negative.

        private static double Min(double a, double b) { return a < b ? a : b; }
        private static double Max(double a, double b) { return a > b ? a : b; }

        private static double Disc(double x, double y, double cx, double cy, double r)
        {
            double dx = x - cx, dy = y - cy;
            return Math.Sqrt(dx * dx + dy * dy) - r;
        }

        /// <summary>Named with a trailing underscore: <c>Box</c> is already the drawing's size.</summary>
        private static double Box_(double x, double y, double x0, double y0, double x1, double y1)
        {
            // Distance to a rectangle, from its centre outward, so a point inside comes back
            // negative rather than zero — the edge has to stay soft on the inside too.
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            double hx = (x1 - x0) / 2, hy = (y1 - y0) / 2;

            double dx = Math.Abs(x - cx) - hx;
            double dy = Math.Abs(y - cy) - hy;

            double outside = Math.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0)
                                     + Math.Max(dy, 0) * Math.Max(dy, 0));

            return outside + Math.Min(Math.Max(dx, dy), 0);
        }

        private static double Capsule(double x, double y, double ax, double ay,
                                      double bx, double by, double r)
        {
            double pax = x - ax, pay = y - ay;
            double bax = bx - ax, bay = by - ay;

            double length = bax * bax + bay * bay;
            double t = length <= 0 ? 0 : (pax * bax + pay * bay) / length;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;

            double dx = pax - bax * t, dy = pay - bay * t;
            return Math.Sqrt(dx * dx + dy * dy) - r;
        }
    }
}
