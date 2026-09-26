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

        /// <summary>A component: an id and what it holds — the logical text, and the form on screen.</summary>
        private sealed class Box
        {
            public long Id;
            public string Shown = "";
        }

        /// <summary>Plays one case; null when every expectation held, else what differed first.</summary>
        private static string Replay(JObject c)
        {
            var host = new ReplayHost { RightToLeft = (string)c["present"] == "rtl" };
            var router = new TextRouter(host);
            host.Router = router;

            if (c["file"] is JObject fileEntries)
                foreach (var kv in fileEntries)
                    host.Add(kv.Key, (string)kv.Value);

            var boxes = new Dictionary<long, Box>();
            Box BoxOf(long cid)
            {
                if (!boxes.TryGetValue(cid, out var b)) boxes[cid] = b = new Box { Id = cid };
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
                else if (step["arrive"] is JObject arrival)
                    host.Arrive((string)arrival["o"], (string)arrival["tr"], boxes.Values);

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

        /// <summary>
        /// The engine, as far as routing sees one. Every answer is the plain one a game with this
        /// file would give; nothing is tuned to make a case pass.
        /// </summary>
        private sealed class ReplayHost : ITextRouterHost
        {
            public TextRouter Router;
            public bool RightToLeft;
            public readonly List<(string Text, Box Box)> Queued = new List<(string, Box)>();

            private readonly Dictionary<string, TranslationEntry> _store = new Dictionary<string, TranslationEntry>();
            private readonly Dictionary<string, TranslationEntry> _ownUi = new Dictionary<string, TranslationEntry>();
            private readonly ReadbackIndex _readback = new ReadbackIndex();
            private readonly StaleSnapshot _stale = new StaleSnapshot();

            public int Frame { get; set; }
            public float Now { get; set; }

            public bool DebugMode => false;
            public bool TypewritingDetection => true;
            public bool ConcatDetection => true;
            public bool TranslationsActive => true;
            public bool GateOpen => true;
            public bool NormalizeNumbers => true;

            public IDictionary<string, TranslationEntry> GameStore => _store;
            public IDictionary<string, TranslationEntry> OwnUiStore => _ownUi;
            public ReadbackIndex Readback => _readback;
            public StaleSnapshot Stale => _stale;
            public IVariableSubstitution Variables => null;
            public string MatchPattern(string text) => null;
            public bool RefreshVariables() => false;
            public bool IsExpandedInPlace(string text) => false;
            public void ForgetTemplate(string text) { }

            public string SourceOf(string translation, bool ownUi)
            {
                foreach (var kv in ownUi ? _ownUi : _store)
                    if (kv.Value.Value == translation) return kv.Key;
                return null;
            }

            public long IdOf(object component) => component is Box b ? b.Id : -1;
            public bool IsHidden(object component) => false;
            public string GetText(object component) => (component as Box)?.Shown;
            public void Write(object target, string text) { if (target is Box b) GameWrites(b, text); }
            public bool IsGone(object target) => false;
            public object FindTarget(long id) => null;

            private readonly Dictionary<Box, string> _originals = new Dictionary<Box, string>();
            public void StoreOriginal(object component, string original)
            {
                if (component is Box b && !_originals.ContainsKey(b)) _originals[b] = original;
            }
            public string GetOriginal(object component) => component is Box b && _originals.TryGetValue(b, out var o) ? o : null;
            public void Showing(string text, object component) { }

            public void Queue(string text, object component, bool ownUi)
            {
                if (!Queued.Any(q => q.Text == text && ReferenceEquals(q.Box, component)))
                    Queued.Add((text, component as Box));
            }
            public void CountTranslated() { }
            public void CountCacheHit() { }
            public void CountAlreadyTranslated() { }

            public void Log(string message) { }
            public void LogWarning(string message) { }
            public void LogDebug(string message) { }
            public string Describe(object component) => "box";

            /// <summary>An entry of the file, stored and indexed as a load does.</summary>
            public void Add(string source, string translation)
            {
                string key = Router.NormalizeForCacheLookup(source);
                _store[key] = TranslationEntry.FromValue(translation);
                _readback.Index(key, translation, ownUi: false, normalizeNumbers: true);
            }

            /// <summary>
            /// A write reaching the component through the setter: routed, then presented — the
            /// order the setter prefix keeps (routing on the logical text, the screen composed last).
            /// </summary>
            public void GameWrites(Box box, string value)
            {
                string routed = value;
                Router.Route(box, box.Id, isOwnUI: false, ref routed);
                box.Shown = Present(box, routed);
            }

            /// <summary>RtlPresenter's flagged branch, for a right-to-left case; the text as is otherwise.</summary>
            private string Present(Box box, string value)
            {
                if (!RightToLeft || string.IsNullOrEmpty(value) || !RtlText.NeedsPresentation(value)) return value;
                string flagged = RtlComposer.Compose(value, RtlOutput.RtlFlagged);
                _readback.RegisterPresented(flagged, value);
                Router.NotePresented(box.Id, value, flagged);
                return flagged;
            }

            /// <summary>What a person reads: the logical text behind a presented form.</summary>
            public string LogicalOf(string shown) => _readback.PresentedLogical(shown) ?? shown;

            /// <summary>
            /// A translation coming back: stored, then applied to every component it was asked for —
            /// written, rebuilt into its page, or left alone, as the router decides.
            /// </summary>
            public void Arrive(string original, string translation, IEnumerable<Box> all)
            {
                Add(original, translation);
                foreach (var q in Queued.Where(q => q.Text == original && q.Box != null).ToList())
                {
                    var box = q.Box;
                    var late = Router.Late(box.Id, box, box.Shown, original, translation, out string toWrite);
                    if (late != TextRouter.LateOutcome.Skip) GameWrites(box, toWrite);
                }
            }
        }

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
