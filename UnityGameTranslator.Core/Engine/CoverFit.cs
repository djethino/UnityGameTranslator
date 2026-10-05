using System;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// How a game's picture sits in the portrait frame of the publish list, in plain numbers.
    ///
    /// 🔴 **Pure by contract — no Unity, no state.** The rule is the socle's
    /// (<c>GameCandidates.FillsFrame</c>, the same test the site and the Manager apply): a cover,
    /// taller than wide, fills the frame — cropped at its edges; a wider picture is shown whole over
    /// a blurred copy of itself. This file is the arithmetic of both, so a crop off by a row or a
    /// blur that reads past the end of the pixels fails in <c>CoverFitChecks</c>, not in a game.
    ///
    /// ⚠ The blur is a SHRINK, not a filter: the region is averaged into a few pixels, and the
    /// engine's own bilinear sampling stretches them back over the frame. No shader, no new engine
    /// call — the mod only writes pixels the way it already does (TextureUtils.SetPixels32Safe).
    /// </summary>
    public static class CoverFit
    {
        /// <summary>The order of a decoded picture's bytes, as the engine left them.</summary>
        public enum Layout { Rgb24, Rgba32, Argb32, Bgra32 }

        /// <summary>The bytes per pixel of a layout.</summary>
        public static int BytesPerPixel(Layout layout) => layout == Layout.Rgb24 ? 3 : 4;

        /// <summary>
        /// The centred region of a <paramref name="width"/>×<paramref name="height"/> picture that has
        /// the frame's proportions (<paramref name="frameAspect"/> = width / height) — what a frame
        /// "filled" shows. Whole pixels, never outside the picture.
        /// </summary>
        public static void CentredRegion(int width, int height, float frameAspect,
                                         out int x, out int y, out int regionWidth, out int regionHeight)
        {
            x = 0;
            y = 0;
            regionWidth = Math.Max(width, 0);
            regionHeight = Math.Max(height, 0);
            if (width <= 0 || height <= 0 || frameAspect <= 0f || float.IsNaN(frameAspect) || float.IsInfinity(frameAspect)) return;

            float pictureAspect = (float)width / height;
            if (pictureAspect > frameAspect)
            {
                // Wider than the frame: the sides go.
                regionWidth = Math.Max(1, Math.Min(width, (int)Math.Round(height * frameAspect)));
                x = (width - regionWidth) / 2;
            }
            else
            {
                // Taller than the frame: the top and the bottom go.
                regionHeight = Math.Max(1, Math.Min(height, (int)Math.Round(width / frameAspect)));
                y = (height - regionHeight) / 2;
            }
        }

        /// <summary>
        /// A region of the raw pixels averaged into <paramref name="outWidth"/>×<paramref name="outHeight"/>
        /// RGBA pixels (4 bytes each, rows in the same order as the source) — the blurred copy, before
        /// the engine stretches it. Null when the source is shorter than its size says.
        /// </summary>
        public static byte[] Shrink(byte[] raw, int width, int height, Layout layout,
                                    int x, int y, int regionWidth, int regionHeight,
                                    int outWidth, int outHeight)
        {
            int bpp = BytesPerPixel(layout);
            if (raw == null || width <= 0 || height <= 0 || outWidth <= 0 || outHeight <= 0) return null;
            if ((long)width * height * bpp > raw.Length) return null;
            if (x < 0 || y < 0 || regionWidth <= 0 || regionHeight <= 0 || x + regionWidth > width || y + regionHeight > height) return null;

            // Where R, G, B and A sit in one pixel; -1: no alpha, opaque.
            int r, g, b, a;
            switch (layout)
            {
                case Layout.Rgb24: r = 0; g = 1; b = 2; a = -1; break;
                case Layout.Rgba32: r = 0; g = 1; b = 2; a = 3; break;
                case Layout.Argb32: a = 0; r = 1; g = 2; b = 3; break;
                default: b = 0; g = 1; r = 2; a = 3; break; // Bgra32
            }

            var result = new byte[outWidth * outHeight * 4];
            for (int oy = 0; oy < outHeight; oy++)
            {
                int y0 = y + oy * regionHeight / outHeight;
                int y1 = Math.Max(y0 + 1, y + (oy + 1) * regionHeight / outHeight);

                for (int ox = 0; ox < outWidth; ox++)
                {
                    int x0 = x + ox * regionWidth / outWidth;
                    int x1 = Math.Max(x0 + 1, x + (ox + 1) * regionWidth / outWidth);

                    long sr = 0, sg = 0, sb = 0, sa = 0, n = 0;
                    for (int py = y0; py < y1; py++)
                    {
                        int row = py * width;
                        for (int px = x0; px < x1; px++)
                        {
                            int i = (row + px) * bpp;
                            sr += raw[i + r];
                            sg += raw[i + g];
                            sb += raw[i + b];
                            sa += a < 0 ? 255 : raw[i + a];
                            n++;
                        }
                    }

                    int o = (oy * outWidth + ox) * 4;
                    result[o] = (byte)(sr / n);
                    result[o + 1] = (byte)(sg / n);
                    result[o + 2] = (byte)(sb / n);
                    result[o + 3] = (byte)(sa / n);
                }
            }

            return result;
        }
    }
}
