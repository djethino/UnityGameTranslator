using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// `dotnet run -- replay &lt;text-trace-….jsonl&gt; [max-diffs]` — plays a trace recorded in a game
    /// (Engine/TextTrace) through the router, write by write, and says where it answers differently
    /// from what the mod did while recording.
    ///
    /// 🔴 **Why**: the router was moved out of the engine; a recorded game is the one witness of
    /// what the code did BEFORE, on real sequences nobody would have thought to write as cases. A
    /// difference is either a deliberate fix or a regression, and the list says which lines to read.
    ///
    /// ⚠ What a trace cannot carry is left out on purpose, and each is a known source of
    /// differences, not a defect of the replay: a translation that came back while no component
    /// was waiting for it (no `arrive` line), the game's variables and number patterns, and our own
    /// writes, which are in the trace as the writes they became.
    ///
    /// ⚠ Traces hold the game's own text: they are read from where they were recorded, never
    /// copied into this repository.
    /// </summary>
    internal static class TraceReplay
    {
        public static int Run(string tracePath, int maxDiffs, int debugFrame)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var host = new ReplayHost { RightToLeft = false, WritesThrough = false, DebugFrame = debugFrame };
            var router = new TextRouter(host);
            host.Router = router;

            var boxes = new Dictionary<long, ReplayBox>();
            ReplayBox BoxOf(long cid)
            {
                if (!boxes.TryGetValue(cid, out var b)) boxes[cid] = b = new ReplayBox { Id = cid };
                return b;
            }

            int writes = 0, diffs = 0, shown = 0;
            // For each line sent for translation, the write that sent it and what the component
            // held just before — what to read when a word goes out alone.
            var causes = new Dictionary<string, string>();
            int lastFrame = int.MinValue;
            string folder = Path.GetDirectoryName(Path.GetFullPath(tracePath));

            foreach (string line in File.ReadLines(tracePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var o = JObject.Parse(line);
                string k = (string)o["k"];

                if (k == "start")
                {
                    string copy = (string)o["translations"];
                    if (copy != null)
                    {
                        var file = JObject.Parse(File.ReadAllText(Path.Combine(folder, copy)));
                        foreach (var kv in TranslationFileEntries.ReadAll(file).Entries)
                            host.AddEntry(kv.Key, kv.Value);
                        Console.WriteLine($"file: {host.GameStore.Count} entries from {copy}");
                    }
                    continue;
                }

                int f = o["f"] != null ? (int)o["f"] : lastFrame;
                host.Frame = f;
                if (o["t"] != null) host.Now = (float)(double)o["t"];

                // The stabiliser runs once a frame in the game.
                if (f != lastFrame) { router.ProcessStabilizedTypewriting(); lastFrame = f; }

                switch (k)
                {
                    case "write":
                    {
                        writes++;
                        var box = BoxOf((long)o["c"]);
                        string recorded = (string)o["out"];
                        int queuedBefore = host.Queued.Count;
                        string previous = box.Shown;
                        string replayed = host.GameWrites(box, (string)o["in"]);
                        for (int i = queuedBefore; i < host.Queued.Count; i++)
                            causes[host.Queued[i].Text] = $"f={f} after [{Clip(previous)}] came [{Clip((string)o["in"])}]";
                        if (replayed != recorded)
                        {
                            diffs++;
                            if (diffs <= maxDiffs)
                            {
                                Console.WriteLine($"--- diff #{diffs} at f={f} t={(double)o["t"]:F2} comp={box.Id}");
                                Console.WriteLine($"  in       [{Clip((string)o["in"])}]");
                                Console.WriteLine($"  recorded [{Clip(recorded)}]");
                                Console.WriteLine($"  replayed [{Clip(replayed)}]");
                            }
                        }
                        break;
                    }
                    case "shown":
                        shown++;
                        host.Presented(BoxOf((long)o["c"]), (string)o["logical"], (string)o["shown"]);
                        break;
                    case "arrive":
                        host.Add((string)o["o"], (string)o["tr"]);
                        break;
                }
            }

            // A word sent alone: one or two words ending with the space that separated it from
            // the next — the mark of a layout pass cut into pieces.
            bool IsWordPart(string t) => t.EndsWith(" ") && t.Trim().Split(' ').Length <= 2
                                         && System.Text.RegularExpressions.Regex.IsMatch(t, "[A-Za-z]");
            int words = 0;
            foreach (var q in host.Queued) if (IsWordPart(q.Text)) words++;
            Console.WriteLine($"{host.Queued.Count} line(s) sent for translation, {words} of them words sent alone");
            int listed = 0;
            foreach (var q in host.Queued)
                if (IsWordPart(q.Text) && listed++ < 60)
                {
                    Console.WriteLine($"  sent alone: [{Clip(q.Text)}] comp={q.Box?.Id}");
                    if (causes.TryGetValue(q.Text, out string cause)) Console.WriteLine($"    {cause}");
                }
            Console.WriteLine($"{host.LayoutPasses.Count} layout pass(es) recognised");
            foreach (string whole in host.LayoutPasses.GetRange(0, Math.Min(5, host.LayoutPasses.Count)))
                Console.WriteLine($"  laid out: [{Clip(whole)}]");
            Console.WriteLine($"{writes} writes replayed, {shown} presented forms, {diffs} answered differently");
            return diffs == 0 ? 0 : 1;
        }

        private static string Clip(string s)
        {
            if (s == null) return "null";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            return s.Length <= 160 ? s : s.Substring(0, 80) + " … " + s.Substring(s.Length - 70);
        }
    }
}
