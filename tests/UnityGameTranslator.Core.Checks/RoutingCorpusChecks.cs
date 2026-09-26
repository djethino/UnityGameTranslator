using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Sequences of writes a game makes, replayed through the routing every engine shares
    /// (Engine/TextRouter) — reveals, texts built in parts, read-backs of our own output, late
    /// translations put back into a page. The cases are `routing/cases.json`.
    ///
    /// 🔴 **A sequence, because every defect of this machine is a right answer at the wrong
    /// moment** (CLAUDE.md, "sequences on real files"). Each piece was checked on its own — the
    /// relations, the gate, the reverse index — and the defects of 2026-09-26 still went through:
    /// a page appended to our shaped Arabic, a late translation that found nothing to go back into.
    ///
    /// ⚠ The host here is a replay, not a game: a component is a box of text, the clock and the
    /// frame are the case's, and right-to-left display is done as RtlPresenter's flagged branch does
    /// it (compose, register the presented form, tell the router). What a real engine adds — a
    /// renderer, fonts, a second write it makes on its own — is out of scope by construction; the
    /// recorded traces (Engine/TextTrace) are what bring those in.
    /// </summary>
    internal static class RoutingCorpusChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string file = Find("tests", "UnityGameTranslator.Core.Checks", "routing", "cases.json");
            check(file != null, "the routing cases are found", "this check reads them; without them, it proves nothing");
            if (file == null) return;

            var cases = (JArray)JObject.Parse(File.ReadAllText(file))["cases"];
            foreach (JObject c in cases)
            {
                string id = (string)c["id"];
                string why = (string)c["why"];
                try
                {
                    string failure = Replay(c);
                    check(failure == null, id, failure == null ? why : failure + "  —  " + why);
                }
                catch (Exception ex)
                {
                    check(false, id, $"the replay threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>Plays one case; null when every expectation held, else what differed first.</summary>
        private static string Replay(JObject c)
        {
            var host = new ReplayHost { RightToLeft = (string)c["present"] == "rtl" };
            // `debugFrame`: prints the router's log for that frame — for reading a case, never left in one.
            if (c["debugFrame"] != null) host.DebugFrame = (int)c["debugFrame"];
            var router = new TextRouter(host);
            host.Router = router;

            if (c["file"] is JObject fileEntries)
                foreach (var kv in fileEntries)
                    host.Add(kv.Key, (string)kv.Value);

            var boxes = new Dictionary<long, ReplayBox>();
            ReplayBox BoxOf(long cid)
            {
                if (!boxes.TryGetValue(cid, out var b)) boxes[cid] = b = new ReplayBox { Id = cid };
                return b;
            }

            int n = 0;
            foreach (JObject step in (JArray)c["steps"])
            {
                n++;
                if (step["f"] != null) host.Frame = (int)step["f"];
                if (step["t"] != null) host.Now = (float)(double)step["t"];
                long cid = step["c"] != null ? (long)step["c"] : 1;
                var box = BoxOf(cid);

                if (step["write"] != null)
                    host.GameWrites(box, (string)step["write"]);
                else if (step["append"] != null)
                    // The game reads what the component holds — our output, in its shown form —
                    // and writes it back with its next part.
                    host.GameWrites(box, box.Shown + (string)step["append"]);
                else if (step["tick"] != null)
                    router.ProcessStabilizedTypewriting();
                else if (step["refresh"] != null)
                    // A refresh sets again what the component holds (the scanner's sweep, an Apply
                    // in the Fonts tab): our own output coming back through the setter.
                    host.GameWrites(box, box.Shown);
                else if (step["sweep"] != null)
                {
                    // The scene sweep: reads the component back, asks the lookup (not the router),
                    // and writes through the setter whatever differs.
                    string swept = router.Translate(box.Shown, box);
                    if (swept != box.Shown) host.GameWrites(box, swept);
                }
                else if (step["arrive"] is JObject arrival)
                    host.Arrive((string)arrival["o"], (string)arrival["tr"]);

                if (step["shows"] != null)
                {
                    string expected = (string)step["shows"];
                    string logical = host.LogicalOf(box.Shown);
                    if (logical != expected)
                        return $"step {n}: shows {Quote(logical)}, expected {Quote(expected)}";
                }
            }

            if (c["queued"] is JArray queued)
            {
                var expected = queued.Select(q => (string)q).ToList();
                var actual = host.Queued.Select(q => q.Text).ToList();
                if (!expected.SequenceEqual(actual))
                    return $"queued [{string.Join(" | ", actual.Select(Quote))}], expected [{string.Join(" | ", expected.Select(Quote))}]";
            }

            return null;
        }

        private static string Quote(string s) => s == null ? "null" : "'" + s.Replace("\n", "\\n") + "'";

        private static string Find(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var segments = new List<string> { dir.FullName };
                segments.AddRange(parts);
                string candidate = Path.Combine(segments.ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
