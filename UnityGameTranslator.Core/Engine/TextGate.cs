using System;
using System.Collections.Generic;
using static UnityGameTranslator.Core.TextNormalization;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The substitution of a game's variables ([!STR*N]) as the gate needs it: lift the current
    /// values out of a text before it is looked up, put them back into what was found.
    ///
    /// ⚠ The gate never RESOLVES a variable — that can touch the engine and belongs to the host's
    /// main thread. It only asks for the two string transformations, and passes the extraction
    /// straight back for the restoration, untouched.
    /// </summary>
    public interface IVariableSubstitution
    {
        /// <summary>False when no variable is defined: the gate then skips both calls.</summary>
        bool HasVariables { get; }

        /// <summary>Replace every current value with its placeholder; <paramref name="extracted"/> is null when nothing was found.</summary>
        string Extract(string text, out List<KeyValuePair<int, string>> extracted);

        /// <summary>Put the values back where the placeholders are; a null <paramref name="extracted"/> returns the text unchanged.</summary>
        string Restore(string text, List<KeyValuePair<int, string>> extracted);
    }

    /// <summary>What the gate found for a text.</summary>
    public enum GateOutcome
    {
        /// <summary>A translation to show. <see cref="GateLookup.Value"/> is the text to display.</summary>
        Hit,
        /// <summary>The text is known and the source is what to show: an entry with nothing in it, a skipped line, or a line equal to its own key.</summary>
        Known,
        /// <summary>Nothing answered. The caller decides what a miss means for it — queue, capture, or leave alone.</summary>
        Miss,
    }

    /// <summary>Which rung of the ladder answered. <see cref="None"/> on a miss.</summary>
    public enum GateStage { None, Exact, Normalized, Trimmed, Pattern }

    /// <summary>
    /// The gate's answer, with the one thing every caller needs after a miss: the text in the
    /// shape the file stores its keys in, which is what the reverse index and the stale snapshot
    /// are consulted with.
    /// </summary>
    public struct GateLookup
    {
        public GateOutcome Outcome;
        public GateStage Stage;
        /// <summary>The text to display on a <see cref="GateOutcome.Hit"/>; null otherwise.</summary>
        public string Value;
        /// <summary>
        /// Line endings normalised, variables then numbers lifted out — the key shape. Equal to the
        /// text itself when nothing applied. Always set, even on an exact hit.
        /// </summary>
        public string NormalizedText;

        internal static GateLookup Found(GateStage stage, string value, string normalized)
            => new GateLookup { Outcome = GateOutcome.Hit, Stage = stage, Value = value, NormalizedText = normalized };
        internal static GateLookup KnownAt(GateStage stage, string normalized)
            => new GateLookup { Outcome = GateOutcome.Known, Stage = stage, Value = null, NormalizedText = normalized };
        internal static GateLookup Missed(string normalized)
            => new GateLookup { Outcome = GateOutcome.Miss, Stage = GateStage.None, Value = null, NormalizedText = normalized };
    }

    /// <summary>
    /// What the miss path asks of the host, in the order it asks. Each question is about the
    /// component the text was set on, handed through as the opaque object every caller already
    /// holds — so one static host serves every call and a miss allocates nothing here.
    ///
    /// 🔴 **Asked DURING the descent, never collected before it.** <see cref="IsRevealInProgress"/>
    /// has effects — it is the one door through which a reveal is followed, and it records the
    /// text it is shown — and it must be asked only once the rungs above have said "not known".
    /// Asking it earlier, to hand the answer in as a fact, would change the moment a reveal
    /// learns what its component shows. That is why this is an interface and not a struct of facts.
    /// </summary>
    public interface ITextGateHost
    {
        /// <summary>The component is out of sight AND a reveal is in flight on it — an accumulator filling a hidden tooltip. False for anything that is not a component.</summary>
        bool IsHiddenWhileRevealing(object component);

        /// <summary>Typewriting detection: the text is growing char by char on this component. Has effects (the reveal is told). False for anything that is not a component.</summary>
        bool IsRevealInProgress(object component, string text);

        /// <summary>Re-resolve the game's variables right before a never-seen text is queued; true when a refresh actually ran, so the whole lookup is retried once (the host throttles, so the retry cannot loop).</summary>
        bool RefreshVariables();
    }

    /// <summary>What becomes of a text the ladder did not find.</summary>
    public enum MissKind
    {
        /// <summary>The gate is shut (translation off and not capturing) or the text is empty: nothing happens.</summary>
        Closed,
        /// <summary>One of our own translations coming back: left alone, never learnt.</summary>
        AlreadyTarget,
        /// <summary>A label of the mod's own interface the file does not hold: shown as is, never queued from here (the anti-loop guard; a code-owned label submits itself).</summary>
        OwnUiUnknown,
        /// <summary>An old translation still on screen after a reload, whose line still exists: show <see cref="MissVerdict.NewText"/>.</summary>
        Refreshed,
        /// <summary>An old translation still on screen after a reload, whose line is gone: keep it, and mark it ours.</summary>
        Gone,
        /// <summary>Out of sight while a reveal is in flight: held back, the reveal told.</summary>
        HeldHidden,
        /// <summary>A reveal in progress: held back until the text settles.</summary>
        HeldRevealing,
        /// <summary>A concat delta: deltas are queued individually, the whole text is not.</summary>
        NotQueued,
        /// <summary>The variables were refreshed: climb the whole ladder again, once.</summary>
        Retry,
        /// <summary>A line nobody has: queue it.</summary>
        Queue,
    }

    /// <summary>The miss path's answer.</summary>
    public struct MissVerdict
    {
        public MissKind Kind;
        /// <summary>The refreshed translation to show (<see cref="MissKind.Refreshed"/> only).</summary>
        public string NewText;
        /// <summary>The source of that translation, live numbers put back — the component's original (<see cref="MissKind.Refreshed"/> only).</summary>
        public string OriginalText;
        /// <summary>The key shape the indexes were asked with, trailing whitespace trimmed — what <see cref="MissKind.Gone"/> marks as ours.</summary>
        public string TrimmedNormalized;

        internal static MissVerdict Of(MissKind kind, string trimmedNormalized)
            => new MissVerdict { Kind = kind, TrimmedNormalized = trimmedNormalized };
    }

    /// <summary>
    /// The lookup ladder every displayed text climbs before anything else happens to it: is this
    /// line already in the file, and in which shape?
    ///
    /// 🔴 **The order of the rungs IS the semantic**, and a second Core has to climb them in the
    /// same order or it answers differently about the same file:
    ///
    /// | rung | key tried | what it catches |
    /// |---|---|---|
    /// | exact | the text as shown | a hit with no allocation — most of what a screen shows, most of the time |
    /// | normalized | line endings → variables → numbers | the sentence the game fills with a value or a name |
    /// | trimmed | the normalized key, whitespace trimmed | a component that pads what the file stored bare |
    /// | pattern | the game's own cached sentences with slots | what the normalized key misses when numbers are not lifted |
    ///
    /// ⚠ **Three answers, not two.** A line can be found and still show the SOURCE: an entry with
    /// nothing in it (a capture waiting for a translation, or a key nobody filled in — whatever
    /// tag it wears), a skipped line (<c>S</c>), or a value equal to its key. That is
    /// <see cref="GateOutcome.Known"/>, and it is not a miss: a known line is never queued.
    ///
    /// ⚠ **Variables are lifted out BEFORE the numbers**, because a variable's value may itself
    /// carry digits ("Player 7"): lifted after, that 7 would already be a number slot and the key
    /// would never match. Restoration runs the other way round, numbers then variables — a
    /// convention kept from the original code, not a constraint: a number slot and a variable's
    /// value cannot be mistaken for each other on the way back (measured on 2026-09-11 by
    /// inverting it: nothing changed).
    ///
    /// 🔴 **The mod's own interface and the game's text never see each other's rules.** Own-UI
    /// text is looked up in the store the caller passes for it, with no variable extraction (a
    /// game's variable has no meaning on our labels, and a colliding value would eat one) and no
    /// pattern (patterns are built from the game's lines — our own placeholders, "Apply ([!v*0])",
    /// already resolve on the normalized rung).
    ///
    /// ⚠ **One ladder, where there were two.** Until 2026-09-11 the localization fallback
    /// (<c>TranslateSingleText</c>) and the tracking path each carried their own copy, and they had
    /// drifted: one showed an EMPTY string for an empty entry that was not tagged <c>H</c> (the
    /// other, deliberately, shows the source whatever the tag); one let a pattern answer after an
    /// entry equal to its key, the other did not. Both now climb this ladder, in the shape the
    /// tracking path had — the one every text on a component went through.
    ///
    /// 🔴 **Pure by contract**, like its neighbours in Engine/: a text and a store in, an answer
    /// out. No Unity, no clock, no state of its own — the store, the variables and the patterns
    /// are the caller's, handed in. Linked by tests/UnityGameTranslator.Core.Checks.
    ///
    /// ⚠ What happens AFTER a miss is the second half, <see cref="ResolveMiss"/>, and its order is
    /// held by cases just the same:
    ///
    /// | rung | question | answer |
    /// |---|---|---|
    /// | gate | is translation on, or capture? | <see cref="MissKind.Closed"/> otherwise — nothing below is asked |
    /// | reverse index | is this one of OUR translations coming back? | <see cref="MissKind.AlreadyTarget"/> |
    /// | own UI | is it a label of ours the file does not hold? | <see cref="MissKind.OwnUiUnknown"/> — the stale snapshot is the GAME's, and must not be asked about our labels |
    /// | stale snapshot | is it an old translation still on screen? | <see cref="MissKind.Refreshed"/> / <see cref="MissKind.Gone"/> |
    /// | visibility | hidden while a reveal is in flight? | <see cref="MissKind.HeldHidden"/> — the reveal is told first |
    /// | reveal | growing char by char? | <see cref="MissKind.HeldRevealing"/> |
    /// | concat | a delta queued on its own? | <see cref="MissKind.NotQueued"/> |
    /// | variables | did a refresh just run? | <see cref="MissKind.Retry"/> — the whole ladder again, once |
    /// | queue | | <see cref="MissKind.Queue"/> |
    /// </summary>
    public static class TextGate
    {
        /// <summary>
        /// Climb the ladder for one text.
        /// </summary>
        /// <param name="text">The text as the component shows it. Null or empty is <see cref="GateOutcome.Known"/>: nothing to look up, nothing to queue.</param>
        /// <param name="isOwnUI">True for the mod's own labels — no variables, no patterns.</param>
        /// <param name="store">The file to look in: the game's, or the interface's. The caller chooses, once.</param>
        /// <param name="normalizeNumbers">Whether numbers are lifted into [!v*N] slots (the <c>normalize_numbers</c> setting).</param>
        /// <param name="variables">The game's variables; may be null when there are none.</param>
        /// <param name="patterns">The game's cached sentences with slots: the text in, the filled translation or null. May be null.</param>
        public static GateLookup Lookup(
            string text,
            bool isOwnUI,
            IDictionary<string, TranslationEntry> store,
            bool normalizeNumbers,
            IVariableSubstitution variables,
            Func<string, string> patterns)
        {
            if (string.IsNullOrEmpty(text))
                return GateLookup.KnownAt(GateStage.None, text);
            if (store == null) throw new ArgumentNullException(nameof(store));

            // Fast path: the text as shown, before any normalisation — no allocation on a hit.
            if (store.TryGetValue(text, out var exactEntry))
            {
                // An entry with nothing in it is a line waiting for a translation, whatever tag it
                // wears: the game's captures say so with H, and a hand-written interface file can
                // hold a key somebody has not filled in yet. Both show the source text.
                if (exactEntry.IsEmpty || exactEntry.Tag == "S")
                    return GateLookup.KnownAt(GateStage.Exact, text);
                if (exactEntry.Value != text)
                    return GateLookup.Found(GateStage.Exact, exactEntry.Value, text);
                // key == value: no translation needed
                return GateLookup.KnownAt(GateStage.Exact, text);
            }

            string normalizedText = KeyShape(text, isOwnUI, variables, normalizeNumbers,
                out List<KeyValuePair<int, string>> extractedVars, out List<string> extractedNumbers);

            // A rung that found the line equal to its own key: known, but the pattern rung below
            // is still tried before saying so (see the class remarks: the shape the tracking path had).
            GateStage knownAt = GateStage.None;

            if (store.TryGetValue(normalizedText, out var cachedEntry))
            {
                if (cachedEntry.IsEmpty || cachedEntry.Tag == "S")
                    return GateLookup.KnownAt(GateStage.Normalized, normalizedText);
                if (cachedEntry.Value != normalizedText)
                    return GateLookup.Found(GateStage.Normalized,
                        Restore(cachedEntry.Value, extractedNumbers, extractedVars, variables), normalizedText);
                knownAt = GateStage.Normalized;
            }

            // The trimmed key, only when the normalized one found nothing at all.
            if (knownAt == GateStage.None)
            {
                string trimmed = normalizedText.Trim();
                if (trimmed != normalizedText && store.TryGetValue(trimmed, out var cachedTrimmedEntry))
                {
                    if (cachedTrimmedEntry.IsEmpty || cachedTrimmedEntry.Tag == "S")
                        return GateLookup.KnownAt(GateStage.Trimmed, normalizedText);
                    if (cachedTrimmedEntry.Value != trimmed)
                        return GateLookup.Found(GateStage.Trimmed,
                            Restore(cachedTrimmedEntry.Value, extractedNumbers, extractedVars, variables), normalizedText);
                    knownAt = GateStage.Trimmed;
                }
            }

            // The GAME's patterns only, on the text as shown.
            if (!isOwnUI && patterns != null)
            {
                string patternResult = patterns(text);
                if (patternResult != null)
                    return GateLookup.Found(GateStage.Pattern, patternResult, normalizedText);
            }

            if (knownAt != GateStage.None)
                return GateLookup.KnownAt(knownAt, normalizedText);

            return GateLookup.Missed(normalizedText);
        }

        /// <summary>
        /// What becomes of a text the ladder did not find. See the class remarks for the rungs and
        /// their order; what each verdict DOES (counters, tracking, the queue itself) is the caller's.
        /// </summary>
        /// <param name="text">The text as shown.</param>
        /// <param name="isOwnUI">True for the mod's own labels.</param>
        /// <param name="normalizedText">The key shape <see cref="Lookup"/> reported; the indexes are asked with it, trailing whitespace trimmed.</param>
        /// <param name="gateOpen">Translation on, or capture-only on — the two reasons a miss is worth anything.</param>
        /// <param name="skipTypewriting">Do not ask the reveal (a concat delta: immediate by design).</param>
        /// <param name="skipQueueing">Do not queue (a concat component: its deltas are queued one by one).</param>
        /// <param name="readback">Our own translations coming back.</param>
        /// <param name="stale">The post-reload safety net.</param>
        /// <param name="current">The GAME's cache as it stands, for the stale snapshot to refresh from.</param>
        /// <param name="normalizeNumbers">Whether numbers are lifted into slots.</param>
        /// <param name="host">The three questions only the host can answer; may be null when there is no component and no variables (a check, or a text with no home).</param>
        /// <param name="component">The component the text was set on, opaque, handed to the host's questions.</param>
        public static MissVerdict ResolveMiss(
            string text,
            bool isOwnUI,
            string normalizedText,
            bool gateOpen,
            bool skipTypewriting,
            bool skipQueueing,
            ReadbackIndex readback,
            StaleSnapshot stale,
            IDictionary<string, TranslationEntry> current,
            bool normalizeNumbers,
            ITextGateHost host,
            object component)
        {
            if (readback == null) throw new ArgumentNullException(nameof(readback));
            if (stale == null) throw new ArgumentNullException(nameof(stale));

            string trimmedNormalized = (normalizedText ?? text ?? "").TrimEnd();

            if (!gateOpen || string.IsNullOrEmpty(text))
                return MissVerdict.Of(MissKind.Closed, trimmedNormalized);

            // Check reverse cache with NORMALIZED text (translations are stored normalized + trimmed)
            // TrimEnd because TMP often strips trailing whitespace/newlines when displaying.
            // ⚠ Its OWN side's index: a mod-interface translation answering here about a game
            // text would take that line out of the file that gets published, invisibly.
            if (readback.IsAlreadyTarget(text, trimmedNormalized, isOwnUI))
                return MissVerdict.Of(MissKind.AlreadyTarget, trimmedNormalized);

            // Own UI text that's already translated (displayed result) — don't re-queue.
            // The mod UI shows translated text; re-queueing it creates an infinite loop.
            //
            // ⚠ Before the stale-translation check below, which reasons about the GAME's cache
            // as it stood before a reload: nothing of ours is in that snapshot, so asking it
            // about one of our labels can only ever answer wrongly.
            if (isOwnUI)
                return MissVerdict.Of(MissKind.OwnUiUnknown, trimmedNormalized);

            // Text may be a translation from the pre-reload cache still displayed
            // (component missed by RestoreAllOriginals) — refresh it, never queue it
            var old = stale.Resolve(text, trimmedNormalized, current, normalizeNumbers);
            if (old.Kind == StaleKind.Refreshed)
                return new MissVerdict { Kind = MissKind.Refreshed, NewText = old.NewText, OriginalText = old.OriginalText, TrimmedNormalized = trimmedNormalized };
            if (old.Kind == StaleKind.Gone)
                return MissVerdict.Of(MissKind.Gone, trimmedNormalized);

            if (host != null)
            {
                // Skip invisible components ONLY if they're also in typewriting state
                // (likely an accumulator: hidden component with growing text).
                // Inactive components with STABLE text (tab panels, menus) are allowed
                // through so they get translated before the user opens them.
                if (host.IsHiddenWhileRevealing(component))
                {
                    // 🔴 Tell the reveal what this component now shows before turning back, and
                    // through the same door as everywhere else. Returning without a word froze the
                    // state on a text the game had already replaced, and the stabiliser then
                    // finalised THAT one. ⚠ Being out of sight changes what may be QUEUED, never
                    // what may be KNOWN. The answer is dropped on purpose: this branch has already
                    // decided to turn back.
                    if (!skipTypewriting)
                        host.IsRevealInProgress(component, text);
                    return MissVerdict.Of(MissKind.HeldHidden, trimmedNormalized);
                }

                // Typewriting detection: skip queuing if text is growing char by char
                // on the same component. Only for cache MISSES — cache hits are returned above.
                // This prevents partial typewriting text from being sent to AI.
                // Skip for concat deltas (they should be queued immediately, not deferred).
                if (!skipTypewriting && host.IsRevealInProgress(component, text))
                    return MissVerdict.Of(MissKind.HeldRevealing, trimmedNormalized);
            }

            // concat component — deltas are queued individually, skip full text queue
            if (skipQueueing)
                return MissVerdict.Of(MissKind.NotQueued, trimmedNormalized);

            // A never-seen text may miss only because variable values went stale (game
            // assigned a new seed/name this frame). Refresh and retry the whole lookup once —
            // the host throttles the refresh to once per frame, so the retry cannot loop.
            if (host != null && host.RefreshVariables())
                return MissVerdict.Of(MissKind.Retry, trimmedNormalized);

            return MissVerdict.Of(MissKind.Queue, trimmedNormalized);
        }

        /// <summary>
        /// The shape a key is stored in: line endings normalised, then the game's variables lifted
        /// out (never on our own GUI), then the numbers into slots when the setting says so.
        ///
        /// 🔴 **One implementation.** The gate, the worker and the reverse-index probe each wrote
        /// this out until 2026-09-11; three copies of an order are three places for it to drift.
        /// </summary>
        public static string KeyShape(string text, bool isOwnUI, IVariableSubstitution variables, bool normalizeNumbers,
            out List<KeyValuePair<int, string>> extractedVars, out List<string> extractedNumbers)
        {
            extractedVars = null;
            extractedNumbers = null;
            if (string.IsNullOrEmpty(text)) return text;

            // Line endings first: keys are stored with \n only.
            string lineNormalized = NormalizeLineEndings(text);

            // Variables BEFORE numbers — never on our own GUI.
            string afterVars = lineNormalized;
            if (!isOwnUI && variables != null && variables.HasVariables)
                afterVars = variables.Extract(lineNormalized, out extractedVars);

            // Then numbers into slots, when the setting says so.
            string normalizedText = afterVars;
            if (normalizeNumbers)
                normalizedText = ExtractNumbersToPlaceholders(afterVars, out extractedNumbers);

            return normalizedText;
        }

        /// <summary>Numbers back first, then variables — the reverse of the extraction.</summary>
        public static string RestoreSlots(string value, List<string> numbers, List<KeyValuePair<int, string>> vars, IVariableSubstitution variables)
            => Restore(value, numbers, vars, variables);

        private static string Restore(string value, List<string> numbers, List<KeyValuePair<int, string>> vars, IVariableSubstitution variables)
        {
            string result = (numbers != null && numbers.Count > 0)
                ? RestoreNumbersFromPlaceholders(value, numbers)
                : value;
            return (variables != null) ? variables.Restore(result, vars) : result;
        }
    }
}
