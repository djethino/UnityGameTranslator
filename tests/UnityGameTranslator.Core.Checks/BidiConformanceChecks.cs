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
            RunCharacterSuite(check);
            RunClassSuite(check);
        }

        /// <summary>
        /// The class-based suite (BidiTest.txt): directionality classes in, expected levels out,
        /// each line run under every paragraph level its bitset requests.
        /// </summary>
        private static void RunClassSuite(Action<bool, string, string> check)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TestData", "BidiTest.txt.gz");
            if (!File.Exists(path))
            {
                check(false, "BidiTest.txt.gz present", $"expected at {path}");
                return;
            }

            var nameMap = new Dictionary<string, Directionality>();
            for (var dir = Directionality.TYPE_MIN; dir <= Directionality.TYPE_MAX; dir++)
                nameMap[dir.ToString()] = dir;

            int cases = 0, failed = 0;
            var bidi = new Bidi();
            sbyte[] expectedLevels = null;

            using (var file = File.OpenRead(path))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gz))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    string line = raw.Split('#')[0].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.StartsWith("@"))
                    {
                        if (line.StartsWith("@Levels:"))
                            expectedLevels = ParseLevels(line.Substring(8));
                        continue;
                    }

                    var parts = line.Split(';');
                    var typeNames = parts[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var types = new Directionality[typeNames.Length];
                    for (int i = 0; i < typeNames.Length; i++)
                        types[i] = nameMap[typeNames[i]];
                    int bitset = Convert.ToInt32(parts[1].Trim(), 16);

                    for (int bit = 1; bit < 8; bit <<= 1)
                    {
                        if ((bitset & bit) == 0) continue;
                        sbyte paragraphLevel = bit == 1 ? (sbyte)2 : bit == 2 ? (sbyte)0 : (sbyte)1;

                        bidi.Process(new Slice<Directionality>(types), Slice<PairedBracketType>.Empty,
                                     Slice<int>.Empty, paragraphLevel, false, null, null, null);

                        cases++;
                        bool ok = bidi.ResolvedLevels.Length == expectedLevels.Length;
                        if (ok)
                        {
                            for (int i = 0; i < expectedLevels.Length; i++)
                            {
                                if (expectedLevels[i] == -1) continue;
                                if (bidi.ResolvedLevels[i] != expectedLevels[i]) { ok = false; break; }
                            }
                        }
                        if (!ok) failed++;
                    }
                }
            }

            check(failed == 0 && cases > 100000,
                $"Unicode BidiTest (classes): {cases} cases",
                failed == 0 ? "every resolved level agrees with the Unicode Consortium's data"
                            : $"{failed} case(s) diverge");
        }

        private static void RunCharacterSuite(Action<bool, string, string> check)
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
            // Tab AND space: BidiTest.txt writes "@Levels:<TAB>x", BidiCharacterTest.txt uses
            // spaces — learnt from a FormatException on the literal string "\tx".
            var parts = field.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new sbyte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                result[i] = parts[i] == "x" ? (sbyte)-1 : sbyte.Parse(parts[i]);
            return result;
        }
    }
}
