using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Topten.RichTextKit;
using Topten.RichTextKit.Utils;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Runs the vendored UAX#9 against Unicode's own conformance suite —
    /// BidiCharacterTest.txt, every case: code points in, expected resolved paragraph level and
    /// per-character levels out. This is what makes "the full algorithm, borrowed" (decision D2)
    /// verifiable rather than trusted: the data comes from the Unicode Consortium, not from the
    /// code under test, and it rode along when the files were vendored (pinned, gzipped in
    /// TestData/ — see TextShaping/RichTextKit/VENDORED.md).
    ///
    /// ⚠ ~90 000 cases; they are summarized as one check line, with the failure count when any.
    /// </summary>
    internal static class BidiConformanceChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TestData", "BidiCharacterTest.txt.gz");
            if (!File.Exists(path))
            {
                check(false, "BidiCharacterTest.txt.gz present", $"expected at {path}");
                return;
            }

            int cases = 0, failed = 0;
            var bidi = new Bidi();
            var bidiData = new BidiData();

            using (var file = File.OpenRead(path))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gz))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    string line = raw.Split('#')[0].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var fields = line.Split(';');
                    var cps = ParseHexList(fields[0]);
                    sbyte paragraphLevel = sbyte.Parse(fields[1]);
                    sbyte expectedParagraphLevel = sbyte.Parse(fields[2]);
                    var expectedLevels = ParseLevels(fields[3]);

                    bidiData.Init(new Slice<int>(cps), paragraphLevel);
                    bidi.Process(bidiData);

                    cases++;
                    bool ok = bidi.ResolvedParagraphEmbeddingLevel == expectedParagraphLevel;
                    if (ok)
                    {
                        var levels = bidi.ResolvedLevels;
                        for (int i = 0; i < expectedLevels.Length; i++)
                        {
                            if (expectedLevels[i] == -1) continue;   // 'x': level irrelevant
                            if (levels[i] != expectedLevels[i]) { ok = false; break; }
                        }
                    }
                    if (!ok) failed++;
                }
            }

            check(failed == 0 && cases > 50000,
                $"Unicode BidiCharacterTest: {cases} cases",
                failed == 0 ? "every resolved level agrees with the Unicode Consortium's data"
                            : $"{failed} case(s) diverge");
        }

        private static int[] ParseHexList(string field)
        {
            var parts = field.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                result[i] = Convert.ToInt32(parts[i], 16);
            return result;
        }

        private static sbyte[] ParseLevels(string field)
        {
            var parts = field.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new sbyte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                result[i] = parts[i] == "x" ? (sbyte)-1 : sbyte.Parse(parts[i]);
            return result;
        }
    }
}
