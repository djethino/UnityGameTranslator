using System;
using System.Collections.Generic;
using UnityEngine;

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
    /// 🔴 **Every Unity call here goes through <see cref="Compat"/> and <see cref="TextureUtils"/> —
    /// this mod's own, never UniverseLib's `TextureHelper`.** `SetPixels32` takes a managed array,
    /// which on IL2CPP is not the array the engine expects, and `Sprite.Create` has overloads a game
    /// may not carry: the same class of trap as AddListener. This file used `TextureHelper` for both
    /// and was the ONLY place in the mod that did — every other texture the mod makes (the rounded
    /// corners in <see cref="UIShapes"/>, replacement images, glyph atlases) already went through
    /// `TextureUtils`, which resolves the overload the game actually has instead of naming one.
    ///
    /// ⚠ **What that cost, and why the symptom pointed the wrong way.** On some IL2CPP games the
    /// marks killed the process outright — no exception, no log line, the game simply gone. It read
    /// as "textures do not work here", and it was not: on those very games <see cref="UIShapes"/>
    /// builds a 256x256 atlas and its sprites through `TextureUtils` — before any panel exists, so a
    /// window with rounded corners is proof both calls went through. Only the two helper calls below
    /// differed. So when a texture misbehaves on IL2CPP, compare against the paths that already work
    /// in this mod before concluding anything about the runtime.
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

            var texture = Compat.MakeTexture2D(w, h, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;

            TextureUtils.SetPixels32Safe(texture, colours);
            texture.Apply(false, false);

            // Same reason as a mark's texture: the cache outlives every panel that asked for it.
            UnityEngine.Object.DontDestroyOnLoad(texture);

            var sprite = MakeSprite(texture);
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

            var texture = Compat.MakeTexture2D(w, h, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;

            TextureUtils.SetPixels32Safe(texture, pixels);
            texture.Apply(false, false);

            // Not destroyed with the panel that first asked for it: the cache outlives every panel,
            // and a texture collected while another still points at it renders as a pink square.
            UnityEngine.Object.DontDestroyOnLoad(texture);

            return MakeSprite(texture);
        }

        /// <summary>
        /// One whole texture, as a sprite the interface can show.
        ///
        /// ⚠ Written out rather than calling UniverseLib: <see cref="TextureUtils.CreateSpriteSafe"/>
        /// picks whichever `Sprite.Create` overload the running game carries, which is the difference
        /// between a mark and a dead process on IL2CPP (see the class remarks).
        ///
        /// 100 pixels per unit, no border, centre pivot — the numbers <see cref="UIShapes"/> uses, so
        /// a 32-texel mark is 32 pixels on screen. No border because a mark is never nine-sliced: it
        /// is scaled as a whole, and a border would pin its edges while stretching the middle.
        /// </summary>
        private static Sprite MakeSprite(Texture2D texture)
        {
            var sprite = TextureUtils.CreateSpriteSafe(
                texture, new Vector2(0.5f, 0.5f), 100f, Vector4.zero) as Sprite;

            if (sprite == null)
                TranslatorCore.LogWarning("[Icons] Sprite.Create returned nothing — that mark stays blank");

            return sprite;
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
                        // The pointer drawn while the game hides the system cursor (SoftwareCursor):
                        // the arrow, and the same arrow one stroke wider for its dark rim. Mod-only —
                        // the manager has the system's own cursor and no use for these.
                        case "cursor": distance = Arrow(px, py); break;
                        case "cursor-rim": distance = Arrow(px, py) - CursorRim; break;
                        // The one window's section bar (analyse/fenetre-unique-sections.md). Mod-only:
                        // the manager has no such bar.
                        case "section-translation": distance = SpeechBubble(px, py); break;
                        case "section-tools": distance = Wrench(px, py); break;
                        case "section-options": distance = Gear(px, py); break;
                        case "chevron-left": distance = Chevron(px, py, false); break;
                        case "chevron-right": distance = Chevron(px, py, true); break;
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

        // ── The pointer ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Where the pointer's tip sits in the box, top-left origin: the hotspot. The cursor is
        /// placed by this point, so the tip is exactly where the mouse is.
        /// </summary>
        public const double CursorTipX = 1.2, CursorTipY = 1.2;

        /// <summary>The dark rim's width around the arrow, in box units (0.9 is just under two texels).</summary>
        private const double CursorRim = 0.9;

        /// <summary>The box the pointer marks are drawn in, in texels — what SoftwareCursor sizes them by.</summary>
        public const int CursorTexels = (int)(Box * Scale);

        // The system arrow's outline: tip, down the left edge, the notch, the tail and back up.
        private static readonly double[] ArrowPoints =
        {
            CursorTipX, CursorTipY,
            1.2, 13.4,
            4.3, 10.6,
            6.5, 15.0,
            8.4, 14.1,
            6.3, 9.8,
            10.2, 9.8,
        };

        /// <summary>The system's arrow, as a polygon.</summary>
        private static double Arrow(double x, double y) => Polygon(x, y, ArrowPoints);

        /// <summary>
        /// Distance to a closed polygon given as x,y pairs: to its nearest edge, negative inside
        /// (crossing count) — the same convention as the other shapes.
        /// </summary>
        private static double Polygon(double x, double y, double[] points)
        {
            int n = points.Length / 2;
            double nearest = double.MaxValue;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double ax = points[2 * i], ay = points[2 * i + 1];
                double bx = points[2 * j], by = points[2 * j + 1];

                double ex = bx - ax, ey = by - ay;
                double length = ex * ex + ey * ey;
                double t = length <= 0 ? 0 : ((x - ax) * ex + (y - ay) * ey) / length;
                if (t < 0) t = 0;
                else if (t > 1) t = 1;
                double dx = x - (ax + ex * t), dy = y - (ay + ey * t);
                double d = dx * dx + dy * dy;
                if (d < nearest) nearest = d;

                if ((ay > y) != (by > y) && x < ax + (bx - ax) * (y - ay) / (by - ay))
                    inside = !inside;
            }
            double distance = Math.Sqrt(nearest);
            return inside ? -distance : distance;
        }

        // ── The section bar ───────────────────────────────────────────────────────────────────

        /// <summary>A speech bubble with two lines of text in it: what this game's translation says.</summary>
        private static double SpeechBubble(double x, double y)
        {
            var body = Max(Box_(x, y, 1.6, 2.4, 14.4, 11.4), -Box_(x, y, 3.0, 3.8, 13.0, 10.0));
            var tail = Polygon(x, y, new[] { 4.2, 11.0, 4.2, 14.6, 8.0, 11.0 });
            var lines = Min(Box_(x, y, 4.6, 5.4, 11.4, 6.4), Box_(x, y, 4.6, 7.4, 9.6, 8.4));
            return Min(Min(body, tail), lines);
        }

        /// <summary>A wrench: a handle, and an open ring at its head.</summary>
        private static double Wrench(double x, double y)
        {
            var handle = Capsule(x, y, 6.4, 9.6, 13.0, 13.0, 1.3);
            var ring = Max(Disc(x, y, 5.0, 5.0, 4.0), -Disc(x, y, 5.0, 5.0, 1.8));
            // The jaw's opening, cut toward the top-left.
            var jaw = Capsule(x, y, 3.0, 3.0, 0.4, 0.4, 1.6);
            return Min(handle, Max(ring, -jaw));
        }

        /// <summary>A gear: a ring with eight teeth, its hub open.</summary>
        private static double Gear(double x, double y)
        {
            double cx = 8, cy = 8;
            var wheel = Disc(x, y, cx, cy, 4.6);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                double c = Math.Cos(a), s = Math.Sin(a);
                wheel = Min(wheel, Capsule(x, y, cx + c * 4.2, cy + s * 4.2, cx + c * 6.2, cy + s * 6.2, 1.2));
            }
            return Max(wheel, -Disc(x, y, cx, cy, 2.0));
        }

        /// <summary>A chevron, pointing left or right: the section bar folding or unfolding.</summary>
        private static double Chevron(double x, double y, bool right)
        {
            double tip = right ? 10.4 : 5.6, back = right ? 5.6 : 10.4;
            return Min(Capsule(x, y, back, 3.4, tip, 8.0, 1.1), Capsule(x, y, tip, 8.0, back, 12.6, 1.1));
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
