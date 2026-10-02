using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The line breaker against Unicode's own LineBreakTest.txt, of the version the mod's tables are
    /// generated from (refreshed by tools/generate-bidi-trie): every case, every position. A break
    /// the file marks ÷ must be an opportunity (allowed or mandatory), one it marks × must not.
    /// </summary>
    internal static class LineBreakConformanceChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TestData", "LineBreakTest.txt.gz");
            if (!File.Exists(path))
            {
                check(false, "LineBreakTest.txt.gz present", $"expected at {path}");
                return;
            }

            int cases = 0, failed = 0;
            var firstFailures = new List<string>();
            using (var file = File.OpenRead(path))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gz))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    string line = raw.Split('#')[0].Trim();
                    if (line.Length == 0) continue;

                    // "× 0023 ÷ 0020 ÷": a mark before each code point and one at the end.
                    var cps = new List<int>();
                    var expected = new List<bool>();   // break before code point i, then at the end
                    foreach (var token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (token == "÷") expected.Add(true);
                        else if (token == "×") expected.Add(false);
                        else cps.Add(Convert.ToInt32(token, 16));
                    }
                    var breaks = LineBreaker.Analyse(cps);
                    cases++;
                    bool ok = expected.Count == cps.Count + 1;
                    // Position 0 (sot) is never a break in either; compare 1..n.
                    for (int p = 1; ok && p <= cps.Count; p++)
                        if ((breaks[p] != LineBreaker.Break.None) != expected[p]) ok = false;
                    if (!ok)
                    {
                        failed++;
                        if (firstFailures.Count < 3) firstFailures.Add(raw.Trim());
                    }
                }
            }

            check(failed == 0 && cases > 10000,
                $"Unicode LineBreakTest: {cases} cases",
                failed == 0 ? "every break opportunity agrees with the Unicode Consortium's data"
                            : $"{failed} case(s) diverge — " + string.Join(" || ", firstFailures));
        }
    }
}
