using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityGameTranslator.Core.Rasterizer;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The whole chain a codepoint-drawing engine goes through, against HarfBuzz: every word of the
    /// oracle files (TestData/Shaping, written by HarfBuzz 14.4 from TestData/Fonts) is shaped by the
    /// mod (OpenTypeText), named by the derived-font namer (DerivedGlyphs), the derived font is written
    /// (DerivedFontWriter) and read back, and the resulting string is DRAWN the way UI.Text draws: each
    /// codepoint's glyph at the pen, the pen moved by that glyph's advance in the derived font. What is
    /// drawn — which source glyph, where — must be HarfBuzz's result, glyph for glyph and unit for unit.
    /// The Unity side of the same claim is the probe bench (analyse/ecritures-complexes-etat-reel.md).
    /// Right-to-left files are left to the RTL composer's own checks (it orders the runs).
    /// </summary>
    internal static class DerivedPipelineChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string fontsDir = Path.Combine(AppContext.BaseDirectory, "TestData", "Fonts");
            string shapingDir = Path.Combine(AppContext.BaseDirectory, "TestData", "Shaping");
            if (!Directory.Exists(shapingDir)) { check(false, "TestData/Shaping present", shapingDir); return; }
            var files = Directory.GetFiles(shapingDir, "*.txt");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                string header = lines.Length > 0 ? lines[0] : "";
                if (header.Contains("rtl=1")) continue;
                string fontName = null;
                foreach (string token in header.TrimStart('#').Split(' '))
                    if (token.StartsWith("font=")) fontName = token.Substring(5);
                string script = Path.GetFileNameWithoutExtension(file);
                if (fontName == null || !File.Exists(Path.Combine(fontsDir, fontName))) { check(false, script + ": font present", fontName ?? header); continue; }
                One(check, script, File.ReadAllBytes(Path.Combine(fontsDir, fontName)), lines);
            }
        }

        private static void One(Action<bool, string, string> check, string script, byte[] bytes, string[] lines)
        {
            var parser = new TtfParser(bytes);
            var font = new TtfShapingFont(parser);
            var namer = new DerivedGlyphs(font, parser.GlyphCount);

            var words = new List<KeyValuePair<string, string>>();
            var shaped = new List<string>();
            foreach (string line in lines)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                words.Add(new KeyValuePair<string, string>(line.Substring(0, tab), line.Substring(tab + 1)));
                shaped.Add(OpenTypeText.Shape(line.Substring(0, tab), font, namer));
            }

            byte[] derivedBytes = DerivedFontWriter.Write(bytes, "UGT Check " + script, namer.Added, out string refusal);
            if (derivedBytes == null) { check(false, script + ": derived font written", refusal); return; }
            var derived = new TtfParser(derivedBytes);
            var byCodepoint = new Dictionary<int, DerivedFontWriter.Added>();
            foreach (var a in namer.Added) byCodepoint[a.Codepoint] = a;

            var failures = new List<string>();
            for (int w = 0; w < words.Count; w++)
            {
                string drawn = Draw(shaped[w], parser, derived, byCodepoint);
                string expected = Positions(words[w].Value);
                if (drawn != expected) failures.Add($"{words[w].Key}: drawn {drawn} / HarfBuzz {expected}");
            }
            check(failures.Count == 0,
                $"{script}: {words.Count} words drawn from the derived font = HarfBuzz ({namer.Added.Count} private glyphs)",
                failures.Count == 0 ? "" : failures.Count + " differ — " + string.Join(" | ", failures.GetRange(0, Math.Min(2, failures.Count))));

            // Across launches: the names saved today are the names read tomorrow, so a string shaped
            // yesterday draws the same composites; a new file is needed only for a name handed out since.
            var again = new DerivedGlyphs(font, parser.GlyphCount);
            int refused = again.Load(namer.Save());
            int differ = 0;
            for (int w = 0; w < words.Count; w++)
                if (OpenTypeText.Shape(words[w].Key, font, again) != shaped[w]) differ++;
            bool dirtyAfterLoad = again.Dirty;
            again.MarkWritten();
            bool cleanAfterWrite = !again.Dirty;
            OpenTypeText.Shape(words[0].Key, font, again);
            bool stillClean = !again.Dirty;
            check(refused == 0 && differ == 0 && again.Added.Count == namer.Added.Count && dirtyAfterLoad && cleanAfterWrite && stillClean,
                $"{script}: the names survive a relaunch; a known word needs no new file",
                $"{refused} refused, {differ} words named otherwise, dirty after load {dirtyAfterLoad}, after write {!cleanAfterWrite}, after a known word {!stillClean}");
        }

        /// <summary>
        /// Draw a string as UI.Text does: each codepoint's glyph at the pen, the pen moved by its
        /// advance — read from the DERIVED font. A private codepoint's glyph is a composite: what it
        /// draws is its source glyph moved by the composite's offset. Result: "source(x,y) …" and the pen.
        /// </summary>
        private static string Draw(string s, TtfParser source, TtfParser derived, Dictionary<int, DerivedFontWriter.Added> byCodepoint)
        {
            var sb = new StringBuilder();
            int pen = 0;
            for (int i = 0; i < s.Length; i++)
            {
                int cp = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? char.ConvertToUtf32(s[i], s[++i]) : s[i];
                int g = derived.GetGlyphIndex(cp);
                int sourceGlyph = g, dx = 0, dy = 0;
                if (byCodepoint.TryGetValue(cp, out var a))
                {
                    // The composite must draw exactly its source glyph moved by the named offset.
                    sourceGlyph = a.Glyph; dx = a.DX; dy = a.DY;
                    if (!SameOutlineMoved(source.GetGlyphOutlineByIndex(a.Glyph), derived.GetGlyphOutlineByIndex(g), dx, dy))
                        return $"U+{cp:X4} does not draw glyph {a.Glyph} moved by ({dx},{dy})";
                }
                sb.Append(sourceGlyph).Append('(').Append(pen + dx).Append(',').Append(dy).Append(") ");
                pen += derived.GetAdvanceWidth(g);
            }
            return sb.Append("pen ").Append(pen).ToString();
        }

        /// <summary>HarfBuzz's "glyph(advance,xOffset,yOffset) …" as the same drawn positions.</summary>
        private static string Positions(string expected)
        {
            var sb = new StringBuilder();
            int pen = 0;
            foreach (string item in expected.Split(' '))
            {
                if (item.Length == 0) continue;
                int open = item.IndexOf('(');
                var nums = item.Substring(open + 1, item.Length - open - 2).Split(',');
                int glyph = int.Parse(item.Substring(0, open)), adv = int.Parse(nums[0]), xo = int.Parse(nums[1]), yo = int.Parse(nums[2]);
                sb.Append(glyph).Append('(').Append(pen + xo).Append(',').Append(yo).Append(") ");
                pen += adv;
            }
            return sb.Append("pen ").Append(pen).ToString();
        }

        private static bool SameOutlineMoved(GlyphOutline a, GlyphOutline b, int dx, int dy)
        {
            if (a == null || b == null) return false;
            int ca = a.Contours?.Length ?? 0, cb = b.Contours?.Length ?? 0;
            if (ca != cb) return false;
            for (int c = 0; c < ca; c++)
            {
                var pa = a.Contours[c].Points; var pb = b.Contours[c].Points;
                if (pa.Length != pb.Length) return false;
                for (int i = 0; i < pa.Length; i++)
                    if (Math.Abs(pa[i].X + dx - pb[i].X) > 0.001f || Math.Abs(pa[i].Y + dy - pb[i].Y) > 0.001f) return false;
            }
            return true;
        }
    }
}
