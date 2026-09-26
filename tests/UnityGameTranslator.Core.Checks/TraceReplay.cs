using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public static int Run(string tracePath, int maxDiffs, int debugFrame, int sweepEvery)
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

            int writes = 0, diffs = 0, shown = 0, heldDiffs = 0;
            // For each line sent for translation, the write that sent it and what the component
            // held just before — what to read when a word goes out alone.
            var causes = new Dictionary<string, string>();
            int lastFrame = int.MinValue;
            string folder = Path.GetDirectoryName(Path.GetFullPath(tracePath));

            // 🔴 Answers go in at the START of the frame they were recorded in: the worker stores
            // an answer before the main thread's writes of that frame, while the trace records the
            // arrival after them. Replayed in trace order, the game read back a translation the
            // replay did not have yet, and the router saw its own output as new text.
            var answers = new SortedDictionary<int, List<(string o, string tr)>>();
            foreach (string line in File.ReadLines(tracePath))
            {
                if (line.IndexOf("\"k\":\"arrive\"", StringComparison.Ordinal) < 0) continue;
                var a = JObject.Parse(line);
                int af = (int)a["f"];
                if (!answers.TryGetValue(af, out var list)) answers[af] = list = new List<(string, string)>();
                list.Add(((string)a["o"], (string)a["tr"]));
            }
            void AnswersUpTo(int frame)
            {
                while (answers.Count > 0)
                {
                    var first = answers.Keys.First();
                    if (first > frame) break;
                    // Stored only: what the mod then wrote with it is in the trace as the writes
                    // it became, and replaying those is replaying the application.
                    foreach (var (o2, tr2) in answers[first]) host.Add(o2, tr2);
                    answers.Remove(first);
                }
            }

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
                if (f != lastFrame)
                {
                    AnswersUpTo(f);
                    int before = host.Queued.Count;
                    router.ProcessStabilizedTypewriting();
                    for (int i = before; i < host.Queued.Count; i++)
                        causes[host.Queued[i].Text] = $"f={f} the stabiliser sent what it had held";

                    // `--sweep N`: the scene sweep, every N frames — it reads each component back
                    // and asks the lookup directly, as TranslatorScanner does. A trace does not
                    // record it (it is not a game write), and it reaches paths the setter never does.
                    if (sweepEvery > 0 && f / sweepEvery != lastFrame / sweepEvery)
                    {
                        int beforeSweep = host.Queued.Count;
                        foreach (var box in boxes.Values)
                        {
                            if (string.IsNullOrEmpty(box.Shown)) continue;
                            string swept = router.Translate(box.Shown, box);
                            if (swept != box.Shown) host.GameWrites(box, swept);
                        }
                        for (int i = beforeSweep; i < host.Queued.Count; i++)
                            causes[host.Queued[i].Text] = $"f={f} the sweep sent what a component showed";
                    }
                    lastFrame = f;
                }

                switch (k)
                {
                    case "write":
                    {
                        writes++;
                        var box = BoxOf((long)o["c"]);
                        string recorded = (string)o["out"];
                        // What the component really held, when the trace says (recorded since
                        // 2026-09-26): the game's truth wins over what the replay rebuilt, and each
                        // disagreement is counted — it is a place where the router's picture of
                        // the screen is wrong.
                        string held = (string)o["held"];
                        // ⚠ A component the replay has not seen written yet holds whatever the game
                        // put there before recording began: not a disagreement.
                        if (o["held"] != null && held != box.Shown && !string.IsNullOrEmpty(box.Shown))
                        {
                            heldDiffs++;
                            if (heldDiffs <= 5)
                                Console.WriteLine($"--- held #{heldDiffs} at f={f} comp={box.Id}: replay [{Clip(box.Shown)}] game [{Clip(held)}]");
                        }
                        if (o["held"] != null) box.Shown = held;
                        int queuedBefore = host.Queued.Count;
                        string previous = box.Shown;
                        string replayed = host.GameWrites(box, (string)o["in"]);
                        if (f == debugFrame)
                            Console.WriteLine($"  WRITE comp={box.Id} [{Clip((string)o["in"])}] -> [{Clip(replayed)}]");
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
                    // "arrive": stored at the start of its frame (AnswersUpTo, above).
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
                if ((IsWordPart(q.Text) || host.Queued.Count <= 60) && listed++ < 60)
                {
                    Console.WriteLine($"  {(IsWordPart(q.Text) ? "sent alone" : "sent")}: [{Clip(q.Text)}] comp={q.Box?.Id}");
                    if (causes.TryGetValue(q.Text, out string cause)) Console.WriteLine($"    {cause}");
                    else Console.WriteLine("    (sent by the stabiliser, from a text held since)");
                }
            Console.WriteLine($"{host.LayoutPasses.Count} layout pass(es) recognised");
            foreach (string whole in host.LayoutPasses.GetRange(0, Math.Min(5, host.LayoutPasses.Count)))
                Console.WriteLine($"  laid out: [{Clip(whole)}]");
            Console.WriteLine($"{writes} writes replayed, {shown} presented forms, {diffs} answered differently, {heldDiffs} held a text the replay did not expect");
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
