using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// `dotnet run -- tocase &lt;text-trace-….jsonl&gt; &lt;component id&gt; [first frame] [last frame]` —
    /// one component's sequence, recorded in a game, written out as a routing case
    /// (`routing/cases.json`) that expects what the mod did while recording.
    ///
    /// 🔴 **Only for a sequence the mod got RIGHT**, seen on screen: the case freezes the
    /// recorded answers, so a wrong recording would freeze a defect. What it adds that a
    /// hand-written case cannot: the game's real sequence — its frames, its timing, its markup,
    /// the component it empties — which nobody would think to write.
    ///
    /// ⚠ What it writes, and why:
    ///  - every write, with the text the component HELD before it (`held`, when the trace has
    ///    it — a game that empties a component leaves no other trace of it) and what the mod
    ///    made of it (`shows`);
    ///  - the stabiliser run once per frame (`tick`), as the game's tick does;
    ///  - each translation that came back for this component (`arrive`), moved to the start of
    ///    its frame: the model's answer is stored by the worker before the main thread's writes
    ///    of that frame, while the trace records the arrival after them;
    ///  - `queued`: the texts that came back, in order — what the recording sent.
    ///  The `id` and `why` are left for a person to write: a case says what it protects.
    ///
    /// ⚠ Traces hold the game's own text: the case carries a few of its lines, as the corpus
    /// already does from captures; it is reviewed before being added.
    /// </summary>
    internal static class TraceToCase
    {
        public static int Run(string tracePath, long comp, int firstFrame, int lastFrame)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var events = new List<JObject>();
            foreach (string line in File.ReadLines(tracePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var o = JObject.Parse(line);
                string k = (string)o["k"];
                int f = o["f"] != null ? (int)o["f"] : -1;
                if (f < firstFrame || f > lastFrame) continue;
                bool mine = k == "write" && (long)o["c"] == comp
                            || k == "arrive" && o["c"] is JArray cs && cs.Any(c => (long)c == comp);
                if (mine) events.Add(o);
            }
            if (events.Count == 0) { Console.Error.WriteLine("nothing recorded for that component in that range"); return 1; }

            // Answers move to the start of their frame (see above).
            var arrivalsByFrame = events.Where(e => (string)e["k"] == "arrive")
                                        .GroupBy(e => (int)e["f"]).ToDictionary(g => g.Key, g => g.ToList());

            var steps = new JArray();
            var queued = new JArray();
            int lastSeen = int.MinValue;
            foreach (var e in events)
            {
                if ((string)e["k"] != "write") continue;
                int f = (int)e["f"];
                double t = Math.Round((double)e["t"], 3);
                if (f != lastSeen)
                {
                    if (lastSeen != int.MinValue) steps.Add(new JObject { ["f"] = f, ["t"] = t, ["tick"] = true });
                    if (arrivalsByFrame.TryGetValue(f, out var answers))
                    {
                        foreach (var a in answers)
                        {
                            steps.Add(new JObject { ["f"] = f, ["t"] = t, ["arrive"] = new JObject { ["o"] = a["o"], ["tr"] = a["tr"] } });
                            queued.Add(a["o"]);
                        }
                        arrivalsByFrame.Remove(f);
                    }
                    lastSeen = f;
                }
                var step = new JObject { ["f"] = f, ["t"] = t };
                if (e["held"] != null) step["held"] = e["held"];
                step["write"] = e["in"];
                step["shows"] = e["out"];
                steps.Add(step);
            }
            // Answers after the last write: still part of what was sent.
            foreach (var answers in arrivalsByFrame.Values)
                foreach (var a in answers)
                {
                    steps.Add(new JObject { ["f"] = (int)a["f"], ["t"] = Math.Round((double)a["t"], 3), ["arrive"] = new JObject { ["o"] = a["o"], ["tr"] = a["tr"] } });
                    queued.Add(a["o"]);
                }

            var c = new JObject
            {
                ["id"] = "TO WRITE",
                ["why"] = "TO WRITE — what this sequence protects",
                ["file"] = new JObject(),
                ["steps"] = steps,
                ["queued"] = queued,
            };
            Console.WriteLine(c.ToString(Formatting.Indented));
            return 0;
        }
    }
}
