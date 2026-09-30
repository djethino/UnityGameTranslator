using System;
using System.IO;
using System.Linq;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The pool of font names (FontPool): the empty font it is made of, read by the mod's own font
    /// parser, and the pool replayed on real files over three launches — every slot handed out once,
    /// a used slot emptied again at the next launch, the pool doubled after a session that used more
    /// than half, never shrunk. That an empty font listed at start and filled later is DRAWN is proven
    /// on the probe bench, not here (analyse/ecritures-complexes-etat-reel.md, the pool).
    /// </summary>
    internal static class FontPoolChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            Placeholder(check);

            string dir = Path.Combine(Path.GetTempPath(), "ugt-fontpool-" + Guid.NewGuid().ToString("N"));
            try { Launches(check, dir); }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        private static void Placeholder(Action<bool, string, string> check)
        {
            byte[] font = DerivedFontWriter.Placeholder("UGT Pool 7");
            TtfParser parser = null;
            try { parser = new TtfParser(font); }
            catch (Exception ex) { check(false, "placeholder: read by the mod's font parser", ex.Message); return; }
            check(parser.Metrics?.FontName == "UGT Pool 7", "placeholder: its family is the slot's", parser.Metrics?.FontName);
            check(parser.GlyphCount == 1, "placeholder: one glyph (.notdef)", parser.GlyphCount.ToString());
            check(!parser.GetSupportedCodepoints().Any(), "placeholder: no character", string.Join(",", parser.GetSupportedCodepoints().Take(5)));

            // The whole file sums to the magic value once head's adjustment is in (OpenType: 'head').
            uint sum = 0;
            for (int i = 0; i < font.Length; i += 4)
            {
                uint word = 0;
                for (int k = 0; k < 4; k++) word = (word << 8) | (uint)(i + k < font.Length ? font[i + k] : 0);
                sum = unchecked(sum + word);
            }
            check(sum == 0xB1B0AFBA, "placeholder: file checksum", $"0x{sum:X8}");
            check(!font.SequenceEqual(DerivedFontWriter.Placeholder("UGT Pool 8")), "placeholder: two slots are two families", "");
        }

        private static void Launches(Action<bool, string, string> check, string dir)
        {
            // ── first launch: a pool of the first size, every slot empty ──
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pool = new FontPool(dir);
            long firstMs = sw.ElapsedMilliseconds;
            check(pool.Size == FontPool.FirstSize && pool.UsedLastSession == 0, "launch 1: the first size, nothing used before",
                $"size {pool.Size}, used {pool.UsedLastSession}, written in {firstMs} ms");
            check(pool.Files.All(File.Exists), "launch 1: every slot is a file", "");

            var taken = Enumerable.Range(0, 3).Select(_ => pool.Take().Value).ToList();
            check(taken.Select(s => s.Family).Distinct().Count() == 3 && taken.Select(s => s.File).Distinct().Count() == 3,
                "launch 1: each slot handed out once", string.Join(", ", taken.Select(s => s.Family)));
            check(taken[0].Family == FontPool.FamilyOf(1) && taken[0].File == FontPool.FileOf(dir, 1),
                "launch 1: a slot's file and family go together", taken[0].Family);
            foreach (var slot in taken) File.WriteAllBytes(slot.File, new byte[4096]);   // what a caller writes: a real font

            // ── second launch: the used slots are empty again, the size kept ──
            sw.Restart();
            pool = new FontPool(dir);
            check(pool.UsedLastSession == 3 && pool.Size == FontPool.FirstSize, "launch 2: 3 used last session, same size",
                $"used {pool.UsedLastSession}, size {pool.Size}, read in {sw.ElapsedMilliseconds} ms");
            check(File.ReadAllBytes(FontPool.FileOf(dir, 1)).SequenceEqual(DerivedFontWriter.Placeholder(FontPool.FamilyOf(1))),
                "launch 2: a used slot is empty again", "");

            // More than half used: the next launch doubles the pool.
            int half = FontPool.FirstSize / 2 + 1;
            for (int k = 0; k < half; k++) File.WriteAllBytes(pool.Take().Value.File, new byte[4096]);

            // ── third launch: doubled; then a quiet session — never shrunk ──
            pool = new FontPool(dir);
            check(pool.Size == 2 * FontPool.FirstSize && pool.UsedLastSession == half, "launch 3: doubled after a session that used more than half",
                $"size {pool.Size}, used {pool.UsedLastSession}");
            pool = new FontPool(dir);
            check(pool.Size == 2 * FontPool.FirstSize && pool.UsedLastSession == 0, "launch 4: a quiet session keeps the size",
                $"size {pool.Size}, used {pool.UsedLastSession}");

            // Spent: every slot handed out, then none.
            for (int k = 0; k < pool.Size; k++) pool.Take();
            check(pool.Spent && pool.Take() == null && pool.Taken == pool.Size, "a spent pool hands out nothing", $"taken {pool.Taken}");
        }
    }
}
