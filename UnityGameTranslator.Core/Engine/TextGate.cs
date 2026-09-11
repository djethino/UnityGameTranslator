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
    /// ⚠ What happens AFTER a miss — the reverse index, the stale snapshot, visibility, a reveal
    /// in flight, the queue — is not here yet: it reads state that still lives in TranslatorCore
    /// (step 6t of analyse/plan-prealables-couches.md). The order of those rungs is written where
    /// they run.
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

            // Line endings first: keys are stored with \n only.
            string lineNormalized = NormalizeLineEndings(text);

            // Variables BEFORE numbers — never on our own GUI.
            string afterVars = lineNormalized;
            List<KeyValuePair<int, string>> extractedVars = null;
            if (!isOwnUI && variables != null && variables.HasVariables)
                afterVars = variables.Extract(lineNormalized, out extractedVars);

            // Then numbers into slots, when the setting says so.
            string normalizedText = afterVars;
            List<string> extractedNumbers = null;
            if (normalizeNumbers)
                normalizedText = ExtractNumbersToPlaceholders(afterVars, out extractedNumbers);

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

        /// <summary>Numbers back first, then variables — the reverse of the extraction.</summary>
        private static string Restore(string value, List<string> numbers, List<KeyValuePair<int, string>> vars, IVariableSubstitution variables)
        {
            string result = (numbers != null && numbers.Count > 0)
                ? RestoreNumbersFromPlaceholders(value, numbers)
                : value;
            return (variables != null) ? variables.Restore(result, vars) : result;
        }
    }
}
