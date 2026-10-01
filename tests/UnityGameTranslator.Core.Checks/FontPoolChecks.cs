using System;
using System.IO;
using System.Linq;
using UnityGameTranslator.Core.Rasterizer;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The pool of font names (FontPool): the empty font it stands on, read by the mod's own font
    /// parser, and the pool replayed over several launches on a real folder — NO file per name (user,
    /// 2026-10-01: « on ne peut pas créer 1000 fichiers par jeu »), every empty name readable as an
    /// empty font of its own family and of the template's exact length, every name handed out once,
    /// the size doubled after a session that used more than half, never shrunk. That the engine reads
    /// through the mod and draws a filled name is proven on the probe bench, not here
    /// (analyse/ecritures-complexes-etat-reel.md, the pool).
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
            byte[] font = DerivedFontWriter.Placeholder("UGT Pool 0007");
            TtfParser parser = null;
            try { parser = new TtfParser(font); }
            catch (Exception ex) { check(false, "placeholder: read by the mod's font parser", ex.Message); return; }
            check(parser.Metrics?.FontName == "UGT Pool 0007", "placeholder: its family is the slot's", parser.Metrics?.FontName);
            check(parser.GlyphCount == 1, "placeholder: one glyph (.notdef)", parser.GlyphCount.ToString());
            check(!parser.GetSupportedCodepoints().Any(), "placeholder: no character", "");

            // The whole file sums to the magic value once head's adjustment is in (OpenType: 'head').
            uint sum = 0;
            for (int i = 0; i < font.Length; i += 4)
            {
                uint word = 0;
                for (int k = 0; k < 4; k++) word = (word << 8) | (uint)(i + k < font.Length ? font[i + k] : 0);
                sum = unchecked(sum + word);
            }
            check(sum == 0xB1B0AFBA, "placeholder: file checksum", $"0x{sum:X8}");
        }

        private static void Launches(Action<bool, string, string> check, string dir)
        {
            // ── first launch ──
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pool = new FontPool(dir);
            long ms = sw.ElapsedMilliseconds;
            var names = pool.Names().ToList();
            check(pool.Size == FontPool.FirstSize && names.Count == pool.Size && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == pool.Size,
                "launch 1: the first size, every name distinct", $"{names.Count} names, ready in {ms} ms");
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
            check(files.SequenceEqual(new[] { FontPool.StateFile, FontPool.TemplateFile }.OrderBy(n => n)),
                "launch 1: on disk, the template and one line — no file per name", string.Join(", ", files));

            // Every empty name reads as an empty font of its own family, exactly the template's length:
            // the engine sizes and seeks the template, and reads that slot's bytes.
            bool sameLength = true, ownFamily = true, onTemplate = true;
            for (int k = 1; k <= pool.Size; k++)
            {
                string name = pool.NameOf(k);
                var content = pool.ContentOf(name);
                if (content == null || content.Length != pool.TemplateLength) { sameLength = false; continue; }
                if (pool.PathOf(name) != pool.TemplatePath) onTemplate = false;
                if (k == 1 || k == pool.Size || k % 997 == 0)
                    if (new TtfParser(content).Metrics?.FontName != pool.FamilyOf(k)) ownFamily = false;
            }
            check(sameLength && onTemplate, "an empty name opens the template and reads the template's length", "");
            check(ownFamily, "an empty name reads as its own family", pool.FamilyOf(1) + " … " + pool.FamilyOf(pool.Size));
            check(new FileInfo(pool.TemplatePath).Length == pool.TemplateLength, "the template on disk has that length", "");
            check(!pool.Has("ugt-pool-1.ttf") && !pool.Has("other.ttf") && pool.Has(pool.NameOf(pool.Size)) && !pool.Has(pool.NameOf(1).Replace("0001", "9999")),
                "only the pool's own names are the pool's", "");

            var taken = Enumerable.Range(0, 3).Select(_ => pool.Take().Value).ToList();
            check(taken.Select(s => s.Name).Distinct().Count() == 3 && taken[0].Family == pool.FamilyOf(1),
                "launch 1: each name handed out once", string.Join(", ", taken.Select(s => s.Family)));
            string real = Path.Combine(dir, "a-real-font.ttf");
            File.WriteAllBytes(real, new byte[4096]);
            pool.Fill(taken[0], real);
            check(pool.PathOf(taken[0].Name) == real && pool.ContentOf(taken[0].Name) == null,
                "a filled name opens its own file, read as it is", "");
            check(pool.PathOf(taken[1].Name) == pool.TemplatePath, "a name taken but not filled is still the template", "");

            // ── second launch: the record of the last one, the size kept ──
            pool = new FontPool(dir);
            check(pool.UsedLastSession == 3 && pool.Size == FontPool.FirstSize, "launch 2: 3 used last session, same size",
                $"used {pool.UsedLastSession}, size {pool.Size}");
            check(pool.PathOf(pool.NameOf(1)) == pool.TemplatePath, "launch 2: every name empty again", "");

            int half = FontPool.FirstSize / 2 + 1;
            for (int k = 0; k < half; k++) pool.Take();

            // ── third launch: doubled; then a quiet session — never shrunk ──
            pool = new FontPool(dir);
            check(pool.Size == 2 * FontPool.FirstSize && pool.UsedLastSession == half, "launch 3: doubled after a session that used more than half",
                $"size {pool.Size}, used {pool.UsedLastSession}");
            pool = new FontPool(dir);
            check(pool.Size == 2 * FontPool.FirstSize && pool.UsedLastSession == 0, "launch 4: a quiet session keeps the size", $"size {pool.Size}");

            // A pool grown past a power of ten: wider names, the template written again at their length.
            File.WriteAllText(Path.Combine(dir, FontPool.StateFile), "6000 3001");
            pool = new FontPool(dir);
            check(pool.Size == 12000 && pool.FamilyOf(1) == "UGT Pool 00001"
                  && new FileInfo(pool.TemplatePath).Length == pool.TemplateLength
                  && pool.ContentOf(pool.NameOf(12000)).Length == pool.TemplateLength,
                "past 9999 names: wider names, and the template follows their length", pool.FamilyOf(1));

            for (int k = 0; k < pool.Size; k++) pool.Take();
            check(pool.Spent && pool.Take() == null && pool.Taken == pool.Size, "a spent pool hands out nothing", $"taken {pool.Taken}");

            File.WriteAllText(Path.Combine(dir, FontPool.StateFile), "garbage");
            bool refused = false;
            try { new FontPool(dir); } catch (InvalidDataException) { refused = true; }
            check(refused, "an unreadable record is refused, not guessed", "the caller says it and shows no pool");
        }
    }
}
