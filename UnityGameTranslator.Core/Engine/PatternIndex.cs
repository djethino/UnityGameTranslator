using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The game's cached sentences with number slots — "Gold: [!v*0]" — and the texts they answer:
    /// "Gold: 12" gets the translation with 12 in its slot. The pattern rung of TextGate, which
    /// matters when numbers are not lifted into slots before the lookup (normalize_numbers off);
    /// with them lifted, the normalized key already finds the line.
    ///
    /// 🔴 **Moved out of TranslatorCore (2026-09-27)** so the routing corpus answers through the
    /// same index a game does; before, its replay said "no pattern" to everything.
    ///
    /// ⚠ Built on the worker's thread (every new pattern translation) and read on the main one:
    /// the list is replaced whole, never edited, so a reader sees the old one or the new one.
    /// </summary>
    public sealed class PatternIndex
    {
        private sealed class Entry
        {
            public string Original;       // the source line with its slots
            public string Translated;     // its translation, slots possibly reordered
            public Regex MatchRegex;      // matches the source with numbers in the slots
            public List<int> Slots;       // capture group i+1 → slot index
        }

        private volatile List<Entry> _entries = new List<Entry>();

        // Each pattern key's regex (null when the key holds no number), with its slot order.
        private Dictionary<string, KeyValuePair<Regex, List<int>>> _regexes = new Dictionary<string, KeyValuePair<Regex, List<int>>>();
        private readonly object _buildGate = new object();

        // Texts that matched no pattern — asked again only when the patterns change.
        private readonly HashSet<string> _misses = new HashSet<string>();
        private readonly object _missesGate = new object();

        public int Count => _entries.Count;

        /// <summary>
        /// The patterns, from the game's lines as they stand (the caller copies them under its own
        /// lock: the worker writes to them).
        ///
        /// 🔴 **The regexes are kept from one build to the next**, keyed by the pattern they match
        /// (the key alone decides the regex; the translation only fills it). A compiled regex pays
        /// its compilation the first time it RUNS — and that first run was the main thread's next
        /// miss. Rebuilding every one of them on each new pattern translation froze the game for
        /// about a second per answer, with 300 patterns (seen 2026-09-26). So an old regex is
        /// reused, and a new one is run once HERE, on whichever thread builds — the worker, in the
        /// case that matters — before the main thread can see it.
        ///
        /// 🔴 **And the misses are forgotten**: a new pattern is the one event that can make a text
        /// match that did not. Kept, a text met before its pattern arrived went on missing it and
        /// was sent to the model on its own — one call more, and a translation free to differ from
        /// the pattern's.
        /// </summary>
        public void Rebuild(IEnumerable<KeyValuePair<string, TranslationEntry>> lines)
        {
            var entries = new List<Entry>();
            lock (_buildGate)
            {
                var kept = new Dictionary<string, KeyValuePair<Regex, List<int>>>();
                foreach (var kv in lines)
                {
                    string translated = kv.Value?.Value;
                    // Key equal to its value: no translation to fill.
                    if (translated == null || kv.Key == translated) continue;

                    if (!kept.TryGetValue(kv.Key, out var built) && !_regexes.TryGetValue(kv.Key, out built))
                    {
                        var regex = NumberPatterns.BuildPatternRegex(kv.Key, out var slots, compiled: true);
                        built = new KeyValuePair<Regex, List<int>>(regex, slots);
                        // Run on a text it MATCHES — the key with a number in each slot. A text too
                        // short to match stops before the matching code, which would then still be
                        // compiled on the main thread.
                        regex?.IsMatch(NumberPatterns.PlaceholderIndexPattern.Replace(kv.Key, "0"));
                    }
                    kept[kv.Key] = built;
                    if (built.Key == null) continue;

                    entries.Add(new Entry { Original = kv.Key, Translated = translated, MatchRegex = built.Key, Slots = built.Value });
                }
                // Only what the file still holds: a removed key takes its regex with it.
                _regexes = kept;
            }

            _entries = entries;
            lock (_missesGate) { _misses.Clear(); }
        }

        /// <summary>The translation of <paramref name="text"/> through a pattern, numbers in their slots; null when none matches.</summary>
        public string Match(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            lock (_missesGate) { if (_misses.Contains(text)) return null; }

            var entries = _entries;
            foreach (var entry in entries)
            {
                var match = entry.MatchRegex.Match(text);
                if (!match.Success) continue;

                string result = entry.Translated;
                for (int i = 0; i < entry.Slots.Count && i + 1 < match.Groups.Count; i++)
                    result = result.Replace(TextNormalization.PlaceholderPrefix + entry.Slots[i] + TextNormalization.PlaceholderSuffix, match.Groups[i + 1].Value);
                return result;
            }

            // Only while the patterns stay as they are: Rebuild forgets it.
            if (entries.Count > 0)
                lock (_missesGate) { _misses.Add(text); }
            return null;
        }

        /// <summary>
        /// A text SHOWN translated, whose numbers the translation moved: the pattern whose translated
        /// form it reads as, and the number in each slot. False when none does.
        /// </summary>
        public bool MatchTranslated(string shown, out string original, Dictionary<int, string> numbersBySlot)
        {
            original = null;
            foreach (var entry in _entries)
            {
                var reverse = NumberPatterns.BuildPatternRegex(entry.Translated, out var slots);
                if (reverse == null) continue;
                var match = reverse.Match(shown);
                if (!match.Success) continue;

                numbersBySlot.Clear();
                for (int g = 0; g < slots.Count; g++)
                    numbersBySlot[slots[g]] = match.Groups[g + 1].Value;
                original = entry.Original;
                return true;
            }
            return false;
        }

        /// <summary>Settings changed what a text should become: every miss is asked again.</summary>
        public void ForgetMisses()
        {
            lock (_missesGate) { _misses.Clear(); }
        }
    }
}
