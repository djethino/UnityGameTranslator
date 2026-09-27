using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// A record of every text the game writes and what the mod does with it — the raw material of
    /// the routing corpus (analyse/banc-routage-texte.md).
    ///
    /// 🔴 **Why it exists.** Reveals, texts built in parts and texts read back from a component are
    /// habits of a GAME's code, and each game has its own: tags that move during a reveal, parts
    /// appended in one frame, a page rewritten whole then appended to. None of that can be
    /// imagined into a test with any confidence; it has to be taken from the game. A trace is a
    /// sequence of dated writes, and the corpus replays it.
    ///
    /// ⚠ Engine-agnostic on purpose: the caller hands the frame and the time, so a Godot or Unreal
    /// adapter records the same thing the same way — which is what lets one corpus check them all.
    ///
    /// ⚠ Off by default, and only ever local: the file holds the game's text, written in this
    /// game's mod folder beside the translation it was recorded against, and sent nowhere.
    ///
    /// One JSON object per line, `k` saying which kind:
    /// <list type="bullet">
    /// <item><c>start</c> — the target language and the translation file copied beside the trace</item>
    /// <item><c>write</c> — the game wrote <c>in</c> on component <c>c</c>; the mod routed it to <c>out</c> (<c>route</c>)</item>
    /// <item><c>shown</c> — presenting turned logical <c>logical</c> into <c>shown</c> on the screen</item>
    /// <item><c>arrive</c> — a translation came back for <c>o</c>, for the components <c>c</c></item>
    /// <item><c>apply</c> — what became of it on one component (<c>ok</c>, <c>skip</c>, <c>reassemble</c>)</item>
    /// <item><c>reveal</c> — the game revealed <c>v</c> characters; the mod turned it into <c>scaled</c></item>
    /// <item><c>size</c> — the mod put a size on a component (<c>held</c>: the font it carried then)</item>
    /// <item><c>font</c> — the mod put font <c>to</c> on a component that carried <c>from</c>; <c>by</c> is the code that did it</item>
    /// </list>
    /// </summary>
    internal static class TextTrace
    {
        private static readonly object _gate = new object();
        private static StreamWriter _writer;
        private static Func<int> _frame;
        private static Func<double> _time;

        /// <summary>Whether a trace is being written. Read before building anything to record.</summary>
        public static bool On { get; private set; }

        /// <summary>The file being written, or null.</summary>
        public static string Path { get; private set; }

        /// <summary>
        /// Starts a trace in <paramref name="folder"/>, with a copy of the translation file as it
        /// stands now — a replay needs what the file held when the game wrote, not what it holds
        /// by the time somebody replays it.
        /// </summary>
        /// <param name="frame">The engine's frame number; -1 when it cannot be asked (off its main thread).</param>
        /// <param name="time">Seconds since the game started, same rule.</param>
        public static void Start(string folder, string translationsPath, string targetLanguage,
                                 Func<int> frame, Func<double> time, Action<string> say)
        {
            lock (_gate)
            {
                if (On) return;

                string stem = "text-trace-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string path = System.IO.Path.Combine(folder, stem + ".jsonl");
                string copy = null;
                if (!string.IsNullOrEmpty(translationsPath) && File.Exists(translationsPath))
                {
                    copy = System.IO.Path.Combine(folder, stem + ".translations.json");
                    File.Copy(translationsPath, copy, overwrite: false);
                }

                _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
                _frame = frame;
                _time = time;
                Path = path;
                On = true;

                Emit(new JObject
                {
                    ["k"] = "start",
                    ["target"] = targetLanguage,
                    ["translations"] = copy == null ? null : System.IO.Path.GetFileName(copy),
                });
                say?.Invoke($"[TextTrace] recording text writes to {path}");
            }
        }

        /// <summary>Stops the trace and closes its file. Nothing happens when none is running.</summary>
        public static void Stop(Action<string> say)
        {
            lock (_gate)
            {
                if (!On) return;
                On = false;
                _writer.Dispose();
                _writer = null;
                say?.Invoke($"[TextTrace] stopped: {Path}");
                Path = null;
            }
        }

        /// <param name="held">
        /// What the component held when the write arrived, read from it — what the game reads
        /// back. A replay that rebuilt it from our own outputs answered right where the game did
        /// not (a layout pass on our translation, 2026-09-26): the component is the witness.
        /// </param>
        public static void Write(long comp, string type, string incoming, string routed, string route, string held)
        {
            if (!On) return;
            Emit(new JObject
            {
                ["k"] = "write", ["c"] = comp, ["type"] = type,
                ["in"] = incoming, ["out"] = routed, ["route"] = route, ["held"] = held,
            });
        }

        public static void Shown(long comp, string logical, string shown)
        {
            if (!On) return;
            Emit(new JObject { ["k"] = "shown", ["c"] = comp, ["logical"] = logical, ["shown"] = shown });
        }

        public static void Arrive(string original, string translation, IEnumerable<long> comps)
        {
            if (!On) return;
            Emit(new JObject { ["k"] = "arrive", ["o"] = original, ["tr"] = translation, ["c"] = new JArray(comps) });
        }

        public static void Apply(long comp, string how, string current)
        {
            if (!On) return;
            Emit(new JObject { ["k"] = "apply", ["c"] = comp, ["how"] = how, ["current"] = current });
        }

        public static void Layout(long comp, string whole, string stack)
        {
            if (!On) return;
            Emit(new JObject { ["k"] = "layout", ["c"] = comp, ["whole"] = whole, ["stack"] = stack });
        }

        public static void Reveal(long comp, int value, int scaled)
        {
            if (!On) return;
            Emit(new JObject { ["k"] = "reveal", ["c"] = comp, ["v"] = value, ["scaled"] = scaled });
        }

        /// <summary>
        /// A size the mod put on a component — its font size, or the bounds its game sizes it
        /// within (TMP auto-size, uGUI best fit). <paramref name="original"/> is what the mod took
        /// for the component's own size, and <paramref name="firstSight"/> says it was read off the
        /// component just now: a size the mod had already scaled, read back as an original, is the
        /// defect a trace of this has to show (a game cloning a template the mod had sized).
        /// </summary>
        /// <param name="held">The font the component carries as the size is put — the size is chosen
        /// for <paramref name="font"/>'s replacement, and a text sized before that font reaches it
        /// shows the replacement's size in the game's own font.</param>
        /// <param name="path">The component's place in the hierarchy, on first sight — what matches a
        /// copy to the template it was made from.</param>
        public static void Size(long comp, string type, string name, string what, string font, string held, float scale,
                                float original, bool firstSight, float before, float after, string path = null)
        {
            if (!On) return;
            var line = new JObject
            {
                ["k"] = "size", ["c"] = comp, ["type"] = type, ["name"] = name, ["what"] = what,
                ["font"] = font, ["held"] = held, ["scale"] = Math.Round(scale, 4), ["orig"] = original,
                ["first"] = firstSight, ["from"] = before, ["to"] = after,
            };
            if (path != null) line["path"] = path;
            Emit(line);
        }

        /// <summary>
        /// A font the mod put on a component. With <c>size</c>, it says in which order a text got
        /// its replacement font and the size chosen for it.
        /// </summary>
        public static void Font(long comp, string type, string name, string from, string to, string by)
        {
            if (!On) return;
            Emit(new JObject
            {
                ["k"] = "font", ["c"] = comp, ["type"] = type, ["name"] = name,
                ["from"] = from, ["to"] = to, ["by"] = by,
            });
        }

        private static void Emit(JObject line)
        {
            lock (_gate)
            {
                if (_writer == null) return;
                line["f"] = _frame();
                line["t"] = Math.Round(_time(), 4);
                _writer.WriteLine(line.ToString(Newtonsoft.Json.Formatting.None));
                // ⚠ Flushed per line: a trace is read after something went wrong, often after the
                // game was killed, and a buffered tail is exactly the part that explains it.
                _writer.Flush();
            }
        }
    }
}
