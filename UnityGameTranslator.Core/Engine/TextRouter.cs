using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Everything the router asks of the engine it runs in — and nothing else. A Unity adapter,
    /// a Godot one and the corpus's replay each answer these, and the decisions stay the same.
    ///
    /// 🔴 **The component is opaque.** It travels as the object the engine hands over; the router
    /// only ever gives it back to the host (to read, write, track). Its id comes from
    /// <see cref="IdOf"/>, which answers -1 for anything the engine does not follow per component —
    /// on Unity, whatever is not a Component (a UI Toolkit element is routed under its own id by
    /// its caller, but read-back and reveals were never followed for it through the lookup).
    /// </summary>
    public interface ITextRouterHost
    {
        // --- clock (family ② of the waits in CLAUDE.md: the absence of an event is the signal) ---
        /// <summary>The engine's frame number: two writes in the same one are an assembly in parts.</summary>
        int Frame { get; }
        /// <summary>Seconds, monotonic: how long a revealing text has stood still.</summary>
        float Now { get; }

        // --- settings ---
        bool DebugMode { get; }
        bool TypewritingDetection { get; }
        bool ConcatDetection { get; }
        /// <summary>Translations on AND set up: the bottleneck every lookup goes through.</summary>
        bool TranslationsActive { get; }
        /// <summary>Translation on, or capture-only on — the two reasons a miss is worth anything.</summary>
        bool GateOpen { get; }
        bool NormalizeNumbers { get; }

        // --- what is known ---
        IDictionary<string, TranslationEntry> GameStore { get; }
        IDictionary<string, TranslationEntry> OwnUiStore { get; }
        ReadbackIndex Readback { get; }
        StaleSnapshot Stale { get; }
        IVariableSubstitution Variables { get; }
        /// <summary>The game's cached sentences with slots: the filled translation, or null.</summary>
        string MatchPattern(string text);
        /// <summary>Re-resolve the game's variables before a never-seen text is queued; true when it ran (throttled by the host).</summary>
        bool RefreshVariables();
        /// <summary>A template the game expands in place: never written back.</summary>
        bool IsExpandedInPlace(string text);
        /// <summary>The source of one of our translations, from the store of its side.</summary>
        string SourceOf(string translation, bool ownUi);
        /// <summary>A template taken back from the queue once its expansion proved what it was.</summary>
        void ForgetTemplate(string text);

        // --- components ---
        /// <summary>The id the router follows this component under, or -1.</summary>
        long IdOf(object component);
        /// <summary>Out of sight (Unity: not active in the hierarchy). False for what the engine cannot tell.</summary>
        bool IsHidden(object component);
        string GetText(object component);
        /// <summary>Put a text back on a target, the way a translation is delivered.</summary>
        void Write(object target, string text);
        /// <summary>Whether a remembered target has been destroyed since.</summary>
        bool IsGone(object target);
        /// <summary>Find a target by id where the router kept none (the setters' own table, weak elements).</summary>
        object FindTarget(long id);
        /// <summary>What the component showed before we touched it, for the runtime switch to restore.</summary>
        void StoreOriginal(object component, string original);
        string GetOriginal(object component);
        /// <summary>A translated text about to be shown: the host makes sure its glyphs can be drawn.</summary>
        void Showing(string text, object component);

        // --- effects ---
        void Queue(string text, object component, bool ownUi);
        void CountTranslated();
        void CountCacheHit();
        void CountAlreadyTranslated();

        // --- logs ---
        void Log(string message);
        void LogWarning(string message);
        void LogDebug(string message);
        /// <summary>A short description of the component for a debug line (type and name).</summary>
        string Describe(object component);

        /// <summary>
        /// The game has started laying out a text it just wrote whole: in the same frame it writes
        /// it again from its first word, word by word, breaking lines where it chooses. Said once
        /// per pass start; the host decides what to find out (which of the game's methods does it).
        /// </summary>
        void LayoutPassSeen(object component, string fullText);

        /// <summary>
        /// The width of one line of text as this component draws it, in whatever unit the engine
        /// measures (only compared with other widths of the same component); null when the
        /// engine cannot say. Read from the game's own layouts, then used to wrap a translation
        /// that arrived after the game laid out its source (TextRouter.Fit). <paramref name="atSize"/>:
        /// as it would be drawn at that font size — the size the game laid out at, once the font
        /// has been resized since; null for the size it draws at now.
        /// </summary>
        float? MeasureLine(object component, string line, float? atSize);

        /// <summary>The font size this component draws at now; null when it has none to give.</summary>
        float? FontSizeOf(object component);
    }

    /// <summary>What the caller must do once the text has been routed.</summary>
    public enum RouteOutcome
    {
        /// <summary>Routed, possibly translated — carry on with the font work.</summary>
        Translated,
        /// <summary>Leave this setter alone entirely.</summary>
        Stop,
        /// <summary>Nothing to translate, but the component still needs its scale re-asserted.</summary>
        StopButRescale,
    }

    /// <summary>
    /// Decides what happens to each text a game writes: the lookup, a reveal held until it
    /// settles, a text built in parts, one read back from a component and appended to, a late
    /// translation put back into a page — the habits of a GAME's code, the same on every engine.
    ///
    /// 🔴 **Why this is its own class, with no engine in it** (2026-09-26, analyse/banc-routage-texte.md).
    /// It lived in TranslatorPatches, soldered to Unity, where nothing could replay it: the pieces
    /// were checked (TextRelations, TextGate, ReadbackIndex) but never the SEQUENCE, and every
    /// defect here is a right answer at the wrong moment. Out of the engine, a recorded or written
    /// sequence of writes replays through the very same decisions — and Godot and Unreal get
    /// them without rediscovering each trap.
    ///
    /// ⚠ Moved, not rewritten: every branch, its order and its comments come from the code that
    /// was paid for in games. The engine's questions go through <see cref="ITextRouterHost"/>.
    ///
    /// ⚠ Main thread only, like the code it came from: one instance per game, no locks.
    /// </summary>
    public sealed partial class TextRouter
    {
        private readonly ITextRouterHost _host;

        public TextRouter(ITextRouterHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        // === CONCATENATION DETECTION ===
        // Games build text procedurally: text = "title"; then text = "title\nattr"; etc.
        // Each set_text contains the FULL text so far (pure source language, no FR/CN mix).
        // We detect this by tracking the raw (pre-translation) text per component.
        // When new text starts with the previous raw text → it's growing → extract delta.
        // Each delta is translated separately (cache-friendly: same parts across items).
        //
        // Concat vs Typewriting:
        // - Concat: 2+ set_text calls in the SAME frame → translate deltas immediately
        // - TW: 1 set_text per frame, text grows → defer for 500ms stabilization
        // Detection: track set_text count per frame per component.

        /// <summary>
        /// Which of the three ways a component's text is being followed.
        ///
        /// 🔴 **One field rather than two booleans**, because "the game assembles this" and "a
        /// reveal is in flight" are mutually exclusive — and that used to be kept true BY HAND at
        /// four separate places: the concat detector cancelled the reveal, the reveal detector
        /// refused to run on a concat component, the stabilizer dropped concat ids, and the
        /// input-mirror path cleared both. A state that cannot be spelled wrongly needs none of it.
        /// </summary>
        private enum TextMode
        {
            /// <summary>Nothing special: translate what arrives.</summary>
            Normal = 0,
            /// <summary>A reveal is in flight — hold the text back until it settles.</summary>
            Typewriter,
            /// <summary>The game builds this text in parts — translate each part.</summary>
            Concat,
        }

        /// <summary>
        /// Everything followed per text component, in one record.
        ///
        /// 🔴 **Nine dictionaries keyed by the same instance id used to hold this, under FIVE
        /// different cleanup policies.** Adding a tenth meant knowing which of three clear methods
        /// it belonged in, and getting it wrong was invisible: the read-back map ended up cleaned
        /// by nothing at all and kept two strings per component for the whole session, the
        /// typewriting work list kept ids whose state was gone, and the best-fit reference was
        /// never dropped. One record has ONE lifetime, so a field added later cannot be forgotten.
        ///
        /// ⚠ **Deliberately NOT in here**: `_concatAssembledCache` and `_concatTranslatedValues`,
        /// which are keyed by TEXT and not by component. They answer "have I already seen this
        /// string", a question that outlives any one component.
        /// </summary>
        private sealed class ComponentTextState
        {
            public TextMode Mode;

            // The component this state is about, recorded by whoever first tracked it — the
            // setter prefix or the scanner. A stabilized typewriter text is queued by id and
            // delivered to THIS; it used to be looked up in the prefixes' own table, which the
            // scanner never fills, so a component the scanner met first (its text set before
            // any setter ran, or the scan reaching it before the game did) could be translated
            // and never updated (bench 2026-09-03: a whole menu stuck in English).
            public object Target;

            // --- What the game last wrote, and what we last showed ---
            public string LastRaw;          // pre-translation, for delta computation
            public string LastTranslated;   // for display: translatedBase + translatedDelta

            // --- Procedural text (concat) ---
            public List<string> Deltas;     // ordered parts, for re-assembly when translations arrive

            // --- Frame tracking that feeds the concat decision ---
            // -1 and not 0: the frame number really is 0 on the first frame, and a fresh record must
            // read as "not seen this frame" there too, exactly as an absent dictionary entry did.
            public int LastFrame = -1;
            public int FrameCallCount;

            // --- Read-back: what we translated, so an append can be reconstructed ---
            public string ReadBackSource;
            public string ReadBackTranslated;
            // --- What the component actually holds when presenting changed our text ---
            // (shaped right-to-left, word breaks, reordered signs): that is what a game reads back
            // and appends to. PresentedFrom is the logical text it stands for, before any stage.
            public string Presented;
            public string PresentedFrom;

            // --- A layout pass in flight (the game placing its own line breaks) ---
            public string LayoutWhole;      // the text being laid out, breaks set aside
            public string LayoutHeld;       // that text as the component held it when the pass began
            public int LayoutFrame = -1;    // the frame it runs in: a pass never spans two
            public bool LayoutOurs;         // laying out our translation, or the game's own text
            public int WriteFrame = -1;     // the frame of the last write, whatever became of it
            public string LastLayout;       // the last text the game laid out here, as it left it
            public float? LastLayoutSize;   // ...at that font size: what its widths were measured at
            public string LastLayoutSource; // ...the game's own text it laid out (null when it was ours)
            // What the component shows as a layout — the game's, or ours wrapped like it — and at
            // what size: a resize re-lays it out (TextRouter.Relayout).
            public string ShownWhole;       // the text laid out, without breaks
            public string ShownLaidOut;     // ...as the component shows it
            public float? ShownSize;
            public bool ShownIsOurs;        // a translation (else the game's own text)
            public bool LayoutReachedFull;  // the pass in flight has reached the whole length
            public int LayoutSteps;         // steps of the pass in flight
            public int LayoutOffset;        // where the line being built starts in LayoutWhole
            public string LayoutLastStep;   // the step before, breaks set aside
            public List<int> LayoutStarts = new List<int>();  // where each line began (TextRouter.Spread)
            public SpreadLayout SpreadOf;   // the spread layout this component shows a line of
            public int SpreadIndex = -1;    // ...which line
            public bool AssemblyMissing;    // the assembly shown still has a part waiting for an answer

            // --- Typewriting ---
            public string TypewritingText;
            public float TypewritingSince;
            public bool TypewritingQueued;  // already handed over; do not hand it over twice
            public bool TypewritingGrew;    // the held text grew (or its markup walked) — a reveal, not a text written whole
        }

        private readonly Dictionary<long, ComponentTextState> _componentState =
            new Dictionary<long, ComponentTextState>();

        /// <summary>
        /// Components with typewriting in flight — an INDEX over <see cref="_componentState"/>, not
        /// a second source of truth.
        ///
        /// ⚠ Kept as its own set on purpose: the stabilizer runs every frame, and walking every
        /// known component to find the two that are mid-reveal would turn a constant cost into one
        /// proportional to the whole scene. Any id in here whose mode is no longer Typewriter gets
        /// dropped by the stabilizer, so it cannot drift into a source of truth.
        /// </summary>
        private readonly HashSet<long> _typewritingPending = new HashSet<long>();

        // Runtime cache for concat-assembled texts (not saved to JSON).
        // Key = full raw source text, Value = full assembled translated text.
        // Prevents re-queuing of assembled texts on scanner refresh.
        private readonly Dictionary<string, string> _concatAssembledCache = new Dictionary<string, string>();
        // Fast lookup for translated values (to skip target-language text that comes back)
        private readonly HashSet<string> _concatTranslatedValues = new HashSet<string>();
        // The keys of _concatAssembledCache that are a page's SOURCE, in the game's own language —
        // as opposed to our shown form with a part appended, which is a key too but never a text
        // the game writes on its own. Only a source says what a redrawn page stands for.
        private readonly HashSet<string> _assembledSources = new HashSet<string>();
        // The game's own texts as its layout pass left them (line breaks placed by the game): the
        // whole was held or sent as one line, and its redraws are left alone rather than sent again
        // as lines of their own. Keyed by text, like the two above: it outlives any one component.
        // Each layout keyed to the whole it lays out, so a translation of that whole can take its
        // place (TextRouter.Fit).
        private readonly Dictionary<string, string> _layoutResults = new Dictionary<string, string>();
        private int _layoutHeldSaid;   // [LAYOUT-HELD] lines said this session (bounded)
        private int _layoutFitSaid;    // [LAYOUT-FIT] lines said this session (bounded)

        /// <summary>The record for this component, created on first need.</summary>
        private ComponentTextState StateFor(long compId)
        {
            ComponentTextState state;
            if (!_componentState.TryGetValue(compId, out state))
            {
                state = new ComponentTextState();
                _componentState[compId] = state;
            }
            return state;
        }

        /// <summary>The record for this component, or null. For readers that must not create one.</summary>
        private ComponentTextState PeekState(long compId)
        {
            if (compId == -1) return null;
            ComponentTextState state;
            return _componentState.TryGetValue(compId, out state) ? state : null;
        }

        // === LIFETIME ===

        /// <summary>Everything, at once (scene change).</summary>
        public void Clear()
        {
            _componentState.Clear();
            _typewritingPending.Clear();
            _concatAssembledCache.Clear();
            _concatTranslatedValues.Clear();
            _assembledSources.Clear();
            _layoutResults.Clear();
            _spreadLines.Clear();
        }

        /// <summary>
        /// Drop everything followed for an id whose target no longer exists: a destroyed component,
        /// or a UI Toolkit element that has been collected.
        /// </summary>
        public void Forget(long id)
        {
            _componentState.Remove(id);
            _typewritingPending.Remove(id);
        }

        /// <summary>
        /// Clear the last-translated text so a refresh re-processes every component fully
        /// (including the font scale). Call when font overrides change.
        /// </summary>
        public void ClearLastTranslated()
        {
            // Only that one field: the rest of a component's record (concat mode, deltas,
            // typewriting) is not what this method is about, and dropping it here would silently
            // reset the detection every time a font override changes.
            foreach (var state in _componentState.Values)
                state.LastTranslated = null;
        }

        /// <summary>
        /// The player's own typing echoed on this component: whatever was being followed on it is
        /// dropped. Char-by-char typing has the exact signature of a typewriting effect, and a
        /// one-frame-late mirror must not leave a stale prefix behind for the stabilizer to queue.
        /// </summary>
        public void ForgetTyped(long compId)
        {
            var typedState = PeekState(compId);
            if (typedState == null) return;
            ForgetTypewriting(typedState);
            _typewritingPending.Remove(compId);
            LeaveConcat(typedState);
            typedState.Deltas = null;
            typedState.LastRaw = null;
        }
    }
}
