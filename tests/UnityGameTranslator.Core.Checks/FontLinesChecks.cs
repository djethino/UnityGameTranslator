using System;
using System.IO;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The lines a text is set against, read from the font (TtfParser.MeasureHeights) — never a share
    /// of the ascender. Expected numbers read off the SAME files by fontTools on 2026-10-03 (OS/2,
    /// post, glyph bounds), and the rule they follow measured on Unity's own FontEngine (bench,
    /// FaceInfoProbe): cap and mean lines are the glyphs "H" and "x" where the font has them — Unity
    /// reads only those —, the font's OS/2 heights where it has no such glyph (Unity gives 0 there).
    /// </summary>
    internal static class FontLinesChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // Both glyphs, and an OS/2 cap height that disagrees with "H": the glyph wins, as in Unity (65 at 90 pt = 714).
            Lines(check, "NotoSansDevanagari.ttf", cap: 714, x: 536, strike: 322, underline: -100, thickness: 50);
            // No "H" nor "x": the font's OS/2 heights, where Unity puts 0 and strikes on the baseline.
            Lines(check, "NotoSansHebrew.ttf", cap: 714, x: 536, strike: 322, underline: -100, thickness: 50);
            // CFF outlines; an "x" but no "H": the glyph for one, OS/2 for the other.
            Lines(check, "NotoSansJP-cid-subset.otf", cap: 733, x: 543, strike: 325, underline: -125, thickness: 50);
        }

        private static void Lines(Action<bool, string, string> check, string file, float cap, float x, float strike, float underline, float thickness)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TestData", "Fonts", file);
            if (!File.Exists(path)) { check(false, file + " present", path); return; }
            var m = new TtfParser(File.ReadAllBytes(path)).Metrics;
            check(m.CapHeight == cap && m.XHeight == x, file + ": cap and mean lines from the font",
                  $"cap {m.CapHeight} (want {cap}), x {m.XHeight} (want {x})");
            check(m.StrikeoutPosition == strike, file + ": the font's own strikeout position", $"{m.StrikeoutPosition} (want {strike})");
            check(m.UnderlinePosition == underline && m.UnderlineThickness == thickness, file + ": the post table's underline",
                  $"{m.UnderlinePosition} / {m.UnderlineThickness}");
        }
    }
}
