using System.Collections.Generic;
using System.Linq;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>A component, for a replay: an id and what it holds on screen.</summary>
    internal sealed class ReplayBox
    {
        public long Id;
        public string Shown = "";
        public float FontSize = 10f;   // a monospaced font: a character is FontSize / 10 wide
        public bool Hidden;            // out of sight (a tooltip the game fills before showing it)
    }

    /// <summary>
    /// The engine, as far as routing sees one — for the routing corpus (RoutingCorpusChecks) and
    /// for replaying a recorded trace (TraceReplay). Every answer is the plain one a game with this
    /// file would give; nothing is tuned to make a case pass.
    /// </summary>
    internal sealed class ReplayHost : ITextRouterHost, IAdmissionFacts
    {
        public TextRouter Router;

        /// <summary>Present right-to-left text as RtlPresenter's flagged branch does (the corpus).</summary>
        public bool RightToLeft;

        /// <summary>
        /// Whether a write the router asks for (a stabilised reveal found in the file) is routed
        /// again here. Off when replaying a trace: the recording already holds the write that came of it.
        /// </summary>
        public bool WritesThrough = true;

        public readonly List<(string Text, ReplayBox Box)> Queued = new List<(string, ReplayBox)>();

        private readonly Dictionary<string, TranslationEntry> _store = new Dictionary<string, TranslationEntry>();
        private readonly Dictionary<string, TranslationEntry> _ownUi = new Dictionary<string, TranslationEntry>();
        private readonly ReadbackIndex _readback = new ReadbackIndex();
        private readonly StaleSnapshot _stale = new StaleSnapshot();

        public int Frame { get; set; }
        public float Now { get; set; }

        /// <summary>A frame whose router log is printed (`replay … --frame N`); none by default.</summary>
        public int DebugFrame { get; set; } = int.MinValue;
        public bool DebugMode => Frame == DebugFrame;
        public bool TypewritingDetection => true;
        public bool ConcatDetection => true;
        public bool TranslationsActive => true;
        public bool GateOpen => true;
        /// <summary>The <c>normalize_numbers</c> setting (on by default, as in the mod); a case may turn it off.</summary>
        public bool NormalizeNumbers { get; set; } = true;

        public IDictionary<string, TranslationEntry> GameStore => _store;
        public IDictionary<string, TranslationEntry> OwnUiStore => _ownUi;
        public ReadbackIndex Readback => _readback;
        public StaleSnapshot Stale => _stale;
        public IVariableSubstitution Variables => null;
        /// <summary>The game's lines with number slots, through the index the mod uses.</summary>
        private readonly PatternIndex _patterns = new PatternIndex();
        public string MatchPattern(string text) => _patterns.Match(text);
        public bool RefreshVariables() => false;

        /// <summary>Templates proved, in the order the router proved them.</summary>
        public readonly List<string> Withdrawn = new List<string>();

        /// <summary>
        /// Of those, the ones already WAITING in the queue when the proof came. In a game that is
        /// a race the worker may have won — the template sent, its answer refused only at the
        /// store — so the replay, which processes nothing in between, must not let the withdrawal
        /// hide it.
        /// </summary>
        public readonly List<string> TakenBack = new List<string>();

        /// <summary>The mod's own queue: its withdrawals and give-ups, never a copy of them.</summary>
        private readonly TranslationQueue _queue = new TranslationQueue();

        /// <summary>As TranslatorCore.Withdraw, through the same rule.</summary>
        public bool Withdraw(string text, Admission why)
        {
            Withdrawn.Add(text);
            bool waiting = TextAdmission.Withdraw(_queue, text, Router.NormalizeForCacheLookup(text), why);
            if (waiting) TakenBack.Add(text);
            Queued.RemoveAll(q => q.Text == text);
            return waiting;
        }

        // The facts TextAdmission reads — as TranslatorCore's AdmissionFacts reads them.
        public bool IsExpandedInPlace(string text) => Router.IsExpandedInPlace(text);
        public bool IsAlreadyTarget(string text)
            => _readback.IsAlreadyTarget(text, Router.NormalizeForCacheLookup(text).TrimEnd(), ownUi: false);
        public bool IsReadback(string key, bool ownUi) => _readback.IsReadback(key, ownUi);
        public bool WasGivenUp(string text, bool ownUi) => _queue.WasRefused(Router.NormalizeForCacheLookup(text));
        public bool IsWithdrawnHead(string key) => Router.IsWithdrawnHead(key);

        public string SourceOf(string translation, bool ownUi)
        {
            foreach (var kv in ownUi ? _ownUi : _store)
                if (kv.Value.Value == translation) return kv.Key;
            return null;
        }

        public long IdOf(object component) => component is ReplayBox b ? b.Id : -1;
        /// <summary>A box's place is its id: the replay has no hierarchy, and one launch.</summary>
        public string PlaceOf(object component) => component is ReplayBox b ? "box" + b.Id : null;
        public bool IsHidden(object component) => (component as ReplayBox)?.Hidden == true;
        public string GetText(object component) => (component as ReplayBox)?.Shown;
        public void Write(object target, string text) { if (WritesThrough && target is ReplayBox b) GameWrites(b, text); }
        public bool IsGone(object target) => false;
        public object FindTarget(long id) => null;

        private readonly Dictionary<ReplayBox, string> _originals = new Dictionary<ReplayBox, string>();
        public void StoreOriginal(object component, string original)
        {
            if (component is ReplayBox b && !_originals.ContainsKey(b)) _originals[b] = original;
        }
        public string GetOriginal(object component) => component is ReplayBox b && _originals.TryGetValue(b, out var o) ? o : null;
        public void Showing(string text, object component) { }

        public void Queue(string text, object component, bool ownUi)
        {
            // The queue's door: what the text is refused for, by the rule QueueForTranslation
            // applies. The mod's state refusals (switched off, offline) have no replay.
            if (TextAdmission.ForQueue(text, ownUi, this) != Admission.Admitted) return;
            _queue.Submit(text, component, ownUi, out _, out _);
            if (!Queued.Any(q => q.Text == text && ReferenceEquals(q.Box, component)))
                Queued.Add((text, component as ReplayBox));
        }
        public void CountTranslated() { }
        public void CountCacheHit() { }
        public void CountAlreadyTranslated() { }

        public void Log(string message) { }
        public void LogWarning(string message) { }
        public void LogDebug(string message) { if (DebugMode) System.Console.WriteLine("    [router] " + message); }
        public string Describe(object component) => "box";

        /// <summary>The layout passes the router recognised, with the text each one laid out.</summary>
        public readonly List<string> LayoutPasses = new List<string>();
        public void LayoutPassSeen(object component, string fullText) => LayoutPasses.Add(fullText);

        /// <summary>
        /// A monospaced component: a line is as wide as its characters. Enough for a case written
        /// that way; a recorded game measures pixels, which a trace does not carry — there the
        /// router finds the widths contradicting the game's layout and cuts nothing, as it must.
        /// </summary>
        public float? MeasureLine(object component, string line, float? atSize)
            => line.Length * (atSize ?? (component as ReplayBox)?.FontSize ?? 10f) / 10f;

        public float? FontSizeOf(object component) => (component as ReplayBox)?.FontSize;

        /// <summary>An entry of the file, stored and indexed as a load does.</summary>
        public void Add(string source, string translation, string tag = "A")
        {
            string key = Router.NormalizeForCacheLookup(source);
            _store[key] = TranslationEntry.FromValue(translation, tag);
            _readback.Index(key, translation, ownUi: false, normalizeNumbers: NormalizeNumbers);
            // As AddToCache: a line with number slots changes the patterns.
            if (key.Contains(TextNormalization.PlaceholderPrefix)) _patterns.Rebuild(_store);
        }

        /// <summary>An entry exactly as a loaded file keyed it.</summary>
        public void AddEntry(string key, TranslationEntry entry)
        {
            _store[key] = entry;
            _readback.Index(key, entry.Value, ownUi: false, normalizeNumbers: NormalizeNumbers);
            if (key.Contains(TextNormalization.PlaceholderPrefix)) _patterns.Rebuild(_store);
        }

        /// <summary>
        /// A write reaching the component through the setter: routed, then presented — the
        /// order the setter prefix keeps (routing on the logical text, the screen composed last).
        /// Returns what routing made of it.
        /// </summary>
        public string GameWrites(ReplayBox box, string value)
        {
            string routed = value;
            Router.Route(box, box.Id, isOwnUI: false, ref routed);
            box.Shown = Present(box, routed);
            return routed;
        }

        /// <summary>A presented form recorded by a trace: registered as RtlPresenter registers it.</summary>
        public void Presented(ReplayBox box, string logical, string shown)
        {
            _readback.RegisterPresented(shown, logical);
            Router.NotePresented(box.Id, logical, shown);
            box.Shown = shown;
        }

        /// <summary>RtlPresenter's flagged branch, for a right-to-left case; the text as is otherwise.</summary>
        private string Present(ReplayBox box, string value)
        {
            if (!RightToLeft || string.IsNullOrEmpty(value) || !RtlText.NeedsPresentation(value)) return value;
            string flagged = RtlComposer.Compose(value, RtlOutput.RtlFlagged);
            Presented(box, value, flagged);
            return flagged;
        }

        /// <summary>What a person reads: the logical text behind a presented form.</summary>
        public string LogicalOf(string shown) => _readback.PresentedLogical(shown) ?? shown;

        /// <summary>
        /// A translation coming back: stored, then applied to every component it was asked for —
        /// written, rebuilt into its page, or left alone, as the router decides.
        /// </summary>
        public void Arrive(string original, string translation)
        {
            // What AddToCache refuses to store, by the same rule (a template, our own translation
            // read back under another decoration).
            string key = Router.NormalizeForCacheLookup(original);
            var admission = TextAdmission.ForStore(key, false, this);
            if (admission == Admission.Head) Router.ForgetWithdrawnHead(key);
            if (admission != Admission.Admitted) return;
            Add(original, translation);
            foreach (var q in Queued.Where(q => q.Text == original && q.Box != null).ToList())
            {
                var box = q.Box;
                var late = Router.Late(box.Id, box, box.Shown, original, translation, out string toWrite);
                if (late != TextRouter.LateOutcome.Skip) GameWrites(box, toWrite);
            }
        }
    }
}
