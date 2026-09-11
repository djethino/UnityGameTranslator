using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using static UnityGameTranslator.Core.TextNormalization;

namespace UnityGameTranslator.Core
{
    /// <summary>What a displayed text is, relative to the cache that was replaced.</summary>
    public enum StaleKind
    {
        /// <summary>Not a translation from the old cache. The caller goes on as if this class had not been asked.</summary>
        NotStale,
        /// <summary>A translation from the old cache whose line still exists: show <see cref="StaleVerdict.NewText"/> instead.</summary>
        Refreshed,
        /// <summary>A translation from the old cache whose line is gone: keep what is displayed, and never queue it.</summary>
        Gone,
    }

    /// <summary>The snapshot's answer about one displayed text.</summary>
    public struct StaleVerdict
    {
        public StaleKind Kind;
        /// <summary>The key of the line this text was translated from (<see cref="StaleKind.Refreshed"/> and <see cref="StaleKind.Gone"/>).</summary>
        public string Key;
        /// <summary>The translation to show now, live numbers put back (<see cref="StaleKind.Refreshed"/> only).</summary>
        public string NewText;
        /// <summary>The source text as the game would have shown it, live numbers put back — what to record as the component's original (<see cref="StaleKind.Refreshed"/> only).</summary>
        public string OriginalText;

        internal static readonly StaleVerdict None = new StaleVerdict { Kind = StaleKind.NotStale };
    }

    /// <summary>
    /// The post-reload safety net: a snapshot of the cache being replaced, so that a text still on
    /// screen with an OLD translation can be recognised as one and refreshed from the new cache —
    /// instead of being queued for AI, which would cache the old translated text as a KEY.
    ///
    /// 🔴 **Why the snapshot rather than the restore.** A reload puts every component back to its
    /// original text first (RestoreAllOriginals), but it cannot reach them all: a component the
    /// scanner never tracked, one created and filled between two ticks, one whose original was
    /// never stored. Those go on showing the previous translation, and the next time the game sets
    /// their text the gate sees a target-language sentence it has never met. Without this, that
    /// sentence is a miss — and a miss is queued.
    ///
    /// ⚠ **Two ways to recognise, because a translation may reorder its slots.** The normalized
    /// value ("Niveau [!v*0]") answers for every translation whose placeholders appear in order —
    /// live numbers then map to slots by position. A translation that reordered them
    /// ("[!v*1] sur [!v*0]") cannot be matched that way, so it keeps a regex that captures each
    /// slot by name; the indices travel with it.
    ///
    /// ⚠ **Taken, never cleared.** It stands for the rest of the session: a component missed by
    /// the restore may not be written again for a long time, and there is no moment at which "the
    /// old cache is certainly gone from every screen" can be known. A second reload takes a new
    /// one over it.
    ///
    /// ⚠ **State, not impurity**: no Unity, no disk, no clock. The log is injected. The cache is
    /// handed in — as a snapshot to take, and as the CURRENT map to answer from — so a check can
    /// replay a reload without a game. Linked by tests/UnityGameTranslator.Core.Checks.
    ///
    /// Moved out of TranslatorCore on 2026-09-11 (step 6t of analyse/plan-prealables-couches.md),
    /// verbatim: same code, same comments, same answers. The host effects a verdict calls for —
    /// recording the original, tracking the pair, marking a gone text as ours — stay with the
    /// caller.
    /// </summary>
    public sealed class StaleSnapshot
    {
        /// <summary>Where the two log lines go (one when taken, one per refresh). Left null in a check that does not read them.</summary>
        public Action<string> Debug { get; set; }

        private sealed class PatternEntry
        {
            public Regex Regex;                  // matches the OLD translated form with concrete numbers
            public List<int> PlaceholderIndices; // capture group i+1 -> placeholder index
            public string Key;
        }

        // Snapshot of the cache being replaced by ReloadCache. Texts still displayed
        // with an old translation (components RestoreAllOriginals could not reach)
        // are recognized through it and refreshed from the new cache instead of
        // being queued for AI translation.
        private Dictionary<string, string> _valueToKey;
        private List<PatternEntry> _patterns;

        /// <summary>True until a snapshot has been taken: nothing is stale before the first reload.</summary>
        public bool IsEmpty => _valueToKey == null;

        /// <summary>How many translated values the snapshot holds — for the log.</summary>
        public int ValueCount => _valueToKey?.Count ?? 0;

        /// <summary>How many of them needed a regex because their slots were reordered — for the log.</summary>
        public int ReorderedCount => _patterns?.Count ?? 0;

        /// <summary>
        /// Snapshot the outgoing cache's values before a reload replaces them.
        /// </summary>
        /// <param name="outgoing">The lines as they stand — the caller copies them first, since the worker may be writing.</param>
        /// <param name="normalizeNumbers">Whether numbers are lifted into slots (the <c>normalize_numbers</c> setting).</param>
        public void Take(IEnumerable<KeyValuePair<string, TranslationEntry>> outgoing, bool normalizeNumbers)
        {
            var valueToKey = new Dictionary<string, string>();
            var stalePatterns = new List<PatternEntry>();

            foreach (var kv in outgoing)
            {
                string value = kv.Value?.Value;
                if (string.IsNullOrEmpty(value) || kv.Key == value) continue;

                string normalizedValue = NormalizeLineEndings(value);
                if (normalizeNumbers)
                    normalizedValue = ExtractNumbersToPlaceholders(normalizedValue, out _);
                normalizedValue = normalizedValue.TrimEnd();
                if (!valueToKey.ContainsKey(normalizedValue))
                    valueToKey[normalizedValue] = kv.Key;

                // Values whose placeholders were reordered by the translation can't be
                // matched by the normalized-value lookup — keep a regex for them
                if (value.Contains(PlaceholderPrefix))
                {
                    var regex = NumberPatterns.BuildPatternRegex(value, out var indices);
                    if (regex == null) continue;
                    bool inAppearanceOrder = true;
                    for (int i = 0; i < indices.Count; i++)
                        if (indices[i] != i) { inAppearanceOrder = false; break; }
                    if (!inAppearanceOrder)
                        stalePatterns.Add(new PatternEntry
                        {
                            Regex = regex,
                            PlaceholderIndices = indices,
                            Key = kv.Key
                        });
                }
            }

            _valueToKey = valueToKey;
            _patterns = stalePatterns;
            Debug?.Invoke($"[StaleSnapshot] {valueToKey.Count} values, {stalePatterns.Count} reordered patterns");
        }

        /// <summary>
        /// Recognize a displayed text that is a translation from the pre-reload cache.
        /// </summary>
        /// <param name="text">The text as shown.</param>
        /// <param name="trimmedNormalized">The same text in the key shape, trailing whitespace trimmed — what the caller already computed for the gate.</param>
        /// <param name="current">The cache as it stands NOW, after the reload.</param>
        /// <param name="normalizeNumbers">Whether numbers are lifted into slots.</param>
        public StaleVerdict Resolve(string text, string trimmedNormalized, IDictionary<string, TranslationEntry> current, bool normalizeNumbers)
        {
            var valueToKey = _valueToKey;
            if (valueToKey == null || string.IsNullOrEmpty(text) || trimmedNormalized == null) return StaleVerdict.None;

            string key = null;
            Dictionary<int, string> capturedNumbers = null;

            if (valueToKey.TryGetValue(trimmedNormalized, out key))
            {
                // Normalized-value match: placeholders are in appearance order,
                // so live numbers map to placeholder indices by position
                if (normalizeNumbers)
                {
                    ExtractNumbersToPlaceholders(NormalizeLineEndings(text), out var numbers);
                    if (numbers != null && numbers.Count > 0)
                    {
                        capturedNumbers = new Dictionary<int, string>();
                        for (int i = 0; i < numbers.Count; i++)
                            capturedNumbers[i] = numbers[i];
                    }
                }
            }
            else
            {
                var stalePatterns = _patterns;
                if (stalePatterns == null || stalePatterns.Count == 0) return StaleVerdict.None;

                string lineNormalized = NormalizeLineEndings(text).TrimEnd();
                foreach (var sp in stalePatterns)
                {
                    var m = sp.Regex.Match(lineNormalized);
                    if (!m.Success) continue;
                    key = sp.Key;
                    capturedNumbers = new Dictionary<int, string>();
                    for (int g = 0; g < sp.PlaceholderIndices.Count; g++)
                        capturedNumbers[sp.PlaceholderIndices[g]] = m.Groups[g + 1].Value;
                    break;
                }
                if (key == null) return StaleVerdict.None;
            }

            // The displayed text is a stale translation of `key`
            if (current != null && current.TryGetValue(key, out var entry) && !string.IsNullOrEmpty(entry.Value)
                && entry.Value != key && !entry.IsHumanEmpty && entry.Tag != "S")
            {
                string newText = RestoreNumbersFromPlaceholders(entry.Value, capturedNumbers);
                string originalText = RestoreNumbersFromPlaceholders(key, capturedNumbers);
                Debug?.Invoke($"[StaleRefresh] Refreshed stale translation for key: {key.Substring(0, Math.Min(40, key.Length))}");
                return new StaleVerdict { Kind = StaleKind.Refreshed, Key = key, NewText = newText, OriginalText = originalText };
            }

            // Entry removed from the new cache: keep the displayed text and mark it
            // as translated so it is never queued (returning the input unchanged also
            // keeps the caller from tracking it as a new translation).
            Debug?.Invoke($"[StaleRefresh] Entry gone from new cache, keeping displayed text for key: {key.Substring(0, Math.Min(40, key.Length))}");
            return new StaleVerdict { Kind = StaleKind.Gone, Key = key };
        }
    }
}
