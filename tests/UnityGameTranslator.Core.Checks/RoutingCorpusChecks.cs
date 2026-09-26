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

            TheGameWritesWhatTheRouterSays(check);
            LinesAreCutOutsideStyledSpans(check);
        }

        /// <summary>
        /// Where a text made of lines is cut (TextRouter.LineParts): at each line break, never
        /// inside a styled span — in the markup of every engine served, whatever the script.
        /// </summary>
        private static void LinesAreCutOutsideStyledSpans(Action<bool, string, string> check)
        {
            void Case(string text, string[] expected, string what, string why)
            {
                var got = TextRouter.LineParts(text);
                bool ok = got.SequenceEqual(expected);
                check(ok, what, ok ? why : $"got [{string.Join(" | ", got.Select(Quote))}]  —  {why}");
            }
            Case("a\nb", new[] { "a\n", "b" }, "lines are cut at their breaks", "each keeps the break that ends it; joined back they are the text");
            Case("<color=red>a\nb</color>\nc", new[] { "<color=red>a\nb</color>\n", "c" },
                "not inside a Unity/TMP span", "a line cut inside a styled span loses its style");
            Case("[b]a\nb[/b]\nc", new[] { "[b]a\nb[/b]\n", "c" },
                "not inside a Godot BBCode span", "the same rule in the markup of another engine");
            Case("<Bold>a\nb</>\nc", new[] { "<Bold>a\nb</>\n", "c" },
                "not inside an Unreal rich-text style closed by </>", "Unreal closes a style without naming it");
            Case("[门派1.1.4]事件一\n[自身1.1.1]事件二", new[] { "[门派1.1.4]事件一\n", "[自身1.1.1]事件二" },
                "a bracketed label never closed is not a span", "an event log's date labels would otherwise glue every line together");
            Case("<sprite=1>a\nb", new[] { "<sprite=1>a\n", "b" },
                "nor a tag never closed", "a sprite or an icon opens nothing");
            Case("مرحبا\nשלום\n你好\nสวัสดี", new[] { "مرحبا\n", "שלום\n", "你好\n", "สวัสดี" },
                "whatever the script", "lines are lines in Arabic, Hebrew, Chinese and Thai alike: only the markup is read, never the text");
        }

        /// <summary>
        /// The one part of a late answer this replay does not play: the scanner's own write. The
        /// replay host writes what <c>Router.Late</c> hands back; the scanner wrote the bare
        /// translation instead, and every line wrapped to the game's width went up on one line,
        /// out of its box, with every case here green (2026-09-26). Lexical, as the scanner is
        /// welded to Unity: the Write branch writes <c>lateText</c>, never <c>translation</c>.
        /// </summary>
        private static void TheGameWritesWhatTheRouterSays(Action<bool, string, string> check)
        {
            string scanner = Find("UnityGameTranslator.Core", "TranslatorScanner.cs");
            check(scanner != null, "the scanner's late write is found", "this check reads it; without it, it proves nothing");
            if (scanner == null) return;

            string text = File.ReadAllText(scanner);
            int start = text.IndexOf("if (late == TextRouter.LateOutcome.Write)", StringComparison.Ordinal);
            int end = start < 0 ? -1 : text.IndexOf("else", start, StringComparison.Ordinal);
            string branch = start >= 0 && end > start ? text.Substring(start, end - start) : null;
            check(branch != null,
                "the scanner still asks the router what a late answer becomes",
                "renamed, the check must say so rather than pass on an empty comparison");
            if (branch == null) return;

            check(branch.Contains("SetText(comp, lateText)", StringComparison.Ordinal)
                  && !branch.Contains("SetText(comp, translation)", StringComparison.Ordinal),
                "and writes what the router answered, not the bare translation",
                "🔴 for a line the game lays out itself, the answer is the translation wrapped to the game's width; writing the bare translation throws that away");
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

                // What the component held before this step, when a recorded case says so: a
                // game that empties a component leaves no other trace of it (TraceToCase).
                if (step["held"] != null) box.Shown = (string)step["held"];

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
                else if (step["resize"] != null)
                {
                    // Size % applied: the font is resized, then — as ReapplyScaleToAllComponents
                    // does — a text the game laid out is laid out again, written through the setter.
                    box.FontSize = (float)step["resize"];
                    string relaid = router.Relayout(box, box.Id, box.Shown);
                    if (relaid != null) host.GameWrites(box, relaid);
                }
                else if (step["clear"] != null)
                    // The game empties the component: an empty text never reaches the router
                    // (the setters let it through untouched), the component simply holds nothing.
                    box.Shown = "";
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
