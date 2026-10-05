using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A game's picture in the publish list's portrait frame (CoverFit): a cover cropped to fill it,
    /// a wide picture over its blurred copy — the rule the site and the Manager follow
    /// (user, 2026-10-06, analyse/images-des-jeux.md).
    /// </summary>
    internal static class CoverFitChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // A 300×450 Steam capsule in a 32×44 frame: the top and the bottom go, centred.
            CoverFit.CentredRegion(300, 450, 32f / 44f, out int x, out int y, out int w, out int h);
            // 300 / (32/44) = 412.5, rounded to even: 412 rows, 19 off the top.
            check(x == 0 && w == 300 && h == 412 && y == 19, "a cover taller than the frame loses its top and bottom, centred", $"{x},{y} {w}x{h}");

            // A 460×215 header in the same frame: the sides go.
            CoverFit.CentredRegion(460, 215, 32f / 44f, out x, out y, out w, out h);
            check(y == 0 && h == 215 && w == 156 && x == 152, "a wide picture in a portrait frame loses its sides, centred", $"{x},{y} {w}x{h}");

            CoverFit.CentredRegion(300, 450, 0f, out x, out y, out w, out h);
            check(x == 0 && y == 0 && w == 300 && h == 450, "a frame of no size crops nothing", $"{w}x{h}");

            // The shrink: a 4×2 RGB picture, left half black, right half white, into 2×1.
            var raw = new byte[4 * 2 * 3];
            for (int py = 0; py < 2; py++)
                for (int px = 2; px < 4; px++)
                    for (int c = 0; c < 3; c++) raw[(py * 4 + px) * 3 + c] = 255;
            var shrunk = CoverFit.Shrink(raw, 4, 2, CoverFit.Layout.Rgb24, 0, 0, 4, 2, 2, 1);
            check(shrunk != null && shrunk[0] == 0 && shrunk[4] == 255 && shrunk[3] == 255 && shrunk[7] == 255,
                "each output pixel is the average of its own share, opaque without alpha", shrunk == null ? "null" : string.Join(",", shrunk));

            // The order of the channels as the engine left them.
            var argb = new byte[] { 128, 10, 20, 30 };
            var one = CoverFit.Shrink(argb, 1, 1, CoverFit.Layout.Argb32, 0, 0, 1, 1, 1, 1);
            check(one != null && one[0] == 10 && one[1] == 20 && one[2] == 30 && one[3] == 128, "ARGB is read as ARGB", one == null ? "null" : string.Join(",", one));
            var bgra = new byte[] { 30, 20, 10, 128 };
            one = CoverFit.Shrink(bgra, 1, 1, CoverFit.Layout.Bgra32, 0, 0, 1, 1, 1, 1);
            check(one != null && one[0] == 10 && one[2] == 30, "BGRA is read as BGRA", one == null ? "null" : string.Join(",", one));

            // Never past the end of the pixels.
            check(CoverFit.Shrink(new byte[10], 4, 2, CoverFit.Layout.Rgba32, 0, 0, 4, 2, 2, 1) == null,
                "a source shorter than its size gives nothing", "never read past its end");
            check(CoverFit.Shrink(new byte[32], 4, 2, CoverFit.Layout.Rgba32, 2, 0, 4, 2, 2, 1) == null,
                "a region outside the picture gives nothing", "x + width > picture width");

            // More output pixels than the region holds: each still reads one source pixel.
            var tiny = CoverFit.Shrink(new byte[] { 50, 60, 70 }, 1, 1, CoverFit.Layout.Rgb24, 0, 0, 1, 1, 3, 4);
            check(tiny != null && tiny.Length == 48 && tiny[44] == 50, "a region smaller than the output is stretched, not read past", tiny == null ? "null" : tiny.Length.ToString());
        }
    }
}
