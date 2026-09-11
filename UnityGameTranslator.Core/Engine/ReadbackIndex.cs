using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using static UnityGameTranslator.Core.TextNormalization;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// "Is this text one of OUR translations coming back?" — the anti-loop device that stops a
    /// string on screen from being learnt as a new source line.
    ///
    /// 🔴 **Why it exists.** The game reads a component back and hands us what it shows. Once we
    /// have written a translation into it, what it shows is ours — and without this index it
    /// looks like fresh source text: queued, translated again (target → target), stored under a
    /// target-language key, published to everybody. Every gate asks here first.
    ///
    /// Four indexes, two per side:
    ///
    /// | | exact (<c>target</c>) | decoration-insensitive (<c>readback</c>) |
    /// |---|---|---|
    /// | what it holds | every translated value, in the key shape (line endings, numbers as slots, trailing whitespace trimmed) | the same values through <see cref="NormalizeForReadbackMatch"/>: tags dropped, numbers and slots collapsed, lowercased |
    /// | what it catches | the value handed back as written | the value re-formatted by the game — a slot re-filled, a colour tag added — which the exact form misses |
    ///
    /// ⚠ **Only a REAL translation is indexed.** A value equal to its key (unchanged output, or a
    /// frame whose only difference is a tag) is the source text itself; indexing it would let the
    /// gate refuse genuine source text — measured on the bench, that single guard took one game
    /// from 42 wrong matches down to zero.
    ///
    /// 🔴 **The game's side and the mod's own interface are SEPARATE, and it took an argument to get
    /// right.** One shared index let a mod-interface translation mark a GAME text as already
    /// translated: the line was never queued, never captured, and simply absent from the file that
    /// gets published — with nothing to say why, since the interface file does not travel with the
    /// translation. A game's register is not this tool's; a menu label is not dialogue. Splitting
    /// them is safe because the read-back each guards against is confined to its own side: a game
    /// reads back what a GAME component displays, and our labels are written by our own code into
    /// our own components.
    ///
    /// ⚠ **Presented strings are a third thing.** The RTL pipeline composes a DISPLAY form (shaped,
    /// mirrored, with glyphs named by our own font assets in the private-use area) out of a logical
    /// string. Two consequences: the display form must be refused like any translation of ours,
    /// and whoever resolves a displayed text back to the cache must recover the LOGICAL truth
    /// first — without that map the in-game editor resolved a shaped display back to a shaped KEY
    /// and offered to save it (found by the user: an Arabic key in the text editor, decision D8).
    /// A typewriter frame or a concat step of such a string comes back as a FRAGMENT, which no
    /// whole-string index recognises — but a fragment holding one of our private codepoints can
    /// be nothing but ours, so the presented strings are kept whole, bounded, and searched.
    ///
    /// ⚠ **State, not impurity**: no Unity, no disk, no clock. The log is injected and left null
    /// in a check that does not read it. Linked by tests/UnityGameTranslator.Core.Checks, where
    /// the SEQUENCES are replayed — a right answer here is only ever right at a moment: after an
    /// index, before a clear, on the side that was asked.
    ///
    /// Moved out of TranslatorCore on 2026-09-11 (step 6t of analyse/plan-prealables-couches.md),
    /// verbatim: same code, same comments, same answers. The public statics of TranslatorCore that
    /// callers name (<c>IsAlreadyTargetText</c>, <c>RegisterPresentedText</c>…) are a façade over
    /// one instance of this.
    /// </summary>
    public sealed class ReadbackIndex
    {
        /// <summary>Where the bounded "not queued, this is ours" lines go. Left null in a check that does not read them.</summary>
        public Action<string> Debug { get; set; }

        private const int SkipLogBudget = 10;
        private int _skipLogCount;

        private sealed class Side
        {
            // Reverse cache: all translated values (to detect already-translated text)
            public readonly ConcurrentDictionary<string, byte> Target = new ConcurrentDictionary<string, byte>();

            // Decoration-insensitive form of the same translated values. The exact reverse cache above
            // misses a whole family: games that build text from templates re-format their slots when they
            // read a component back — {0} becomes 3, or <color=#F4FF58>3</color>. What comes back is OUR
            // translation wearing a decoration we never produced, so it looks like new source text and
            // gets re-translated, drifting on each round trip and polluting the cache with target-language
            // keys. Comparing on a decoration-insensitive form recognises it, synchronously, with no delay.
            // See analyse/readback-substitution-fr-keys-analysis.md.
            public readonly ConcurrentDictionary<string, byte> Readback = new ConcurrentDictionary<string, byte>();

            public void Clear() { Target.Clear(); Readback.Clear(); }
        }

        private readonly Side _game = new Side();
        private readonly Side _ownUi = new Side();
        private Side Of(bool ownUi) => ownUi ? _ownUi : _game;

        // Presented strings that carry private-use codepoints (glyphs named by our font assets),
        // kept whole: a typewriter frame or a concat step of one comes back as a FRAGMENT of the
        // presented string, which no normalized whole-string index can recognise — and a fragment
        // holding one of our private codepoints can be nothing but ours.
        private readonly List<string> _presentedWithPrivate = new List<string>();
        private const int PresentedWithPrivateMax = 4096;

        // Presented (shaped) form → the LOGICAL string it was composed from. The refuse-to-learn
        // set says "this is ours"; only this map can say "and HERE is its truth" — without it the
        // in-game editor resolved a shaped display back to a shaped KEY and offered to save it
        // (found by the user: an Arabic key in the text editor).
        private readonly ConcurrentDictionary<string, string> _presentedToLogical =
            new ConcurrentDictionary<string, string>();

        /// <summary>How many exact translated values one side holds — for the load log.</summary>
        public int TargetCount(bool ownUi) => Of(ownUi).Target.Count;

        /// <summary>How many decoration-insensitive forms one side holds — for the load log.</summary>
        public int ReadbackCount(bool ownUi) => Of(ownUi).Readback.Count;

        /// <summary>
        /// Forget everything the GAME's file taught: both of its indexes, the presented map, and the
        /// log budget. Called when that file is (re)read — or could not be, since an index kept
        /// beside an EMPTY cache goes on answering about a file that could not be read.
        /// </summary>
        public void ClearGame()
        {
            _game.Clear();
            _presentedToLogical.Clear();
            _skipLogCount = 0;
        }

        /// <summary>
        /// Forget what the INTERFACE's file taught, and nothing of the game's. A language set aside
        /// leaves its translations in here otherwise, answering about a file they left.
        /// </summary>
        public void ClearOwnUi()
        {
            _ownUi.Clear();
        }

        /// <summary>Whether the text carries one of the codepoints our font assets name their glyphs by.</summary>
        public static bool HasPrivateGlyphCodepoint(string text)
        {
            foreach (char c in text)
                if (c >= TextShaping.PrivateGlyphs.First && c <= TextShaping.PrivateGlyphs.Last) return true;
            return false;
        }

        /// <summary>A text holding one of our private glyph codepoints, found inside a string we presented.</summary>
        private bool IsFragmentOfPresentedText(string text)
        {
            if (!HasPrivateGlyphCodepoint(text)) return false;
            string probe = text.Trim();
            if (probe.Length == 0) return false;
            lock (_presentedWithPrivate)
            {
                foreach (string presented in _presentedWithPrivate)
                    if (presented.IndexOf(probe, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Register a PRESENTED string — one the RTL pipeline composed for display — as our own
        /// output, together with the logical string it came from. Every gate
        /// (<see cref="IsAlreadyTarget"/>: the scanner, the getters, the setter prefixes)
        /// then refuses to learn from it, and everything that resolves a DISPLAYED text back to
        /// the cache recovers the logical truth first — a shaped form must never be queued to
        /// the AI, cached as a source text, or written to translations.json (decision D8).
        /// </summary>
        public void RegisterPresented(string presented, string logical)
        {
            if (string.IsNullOrEmpty(presented)) return;
            if (HasPrivateGlyphCodepoint(presented))
            {
                lock (_presentedWithPrivate)
                {
                    if (_presentedWithPrivate.Count >= PresentedWithPrivateMax) _presentedWithPrivate.RemoveAt(0);
                    _presentedWithPrivate.Add(presented);
                }
            }
            string n = NormalizeForReadbackMatch(presented);
            if (n == null) return;
            // The GAME's index: the RTL presentation pass runs on the game's components and skips
            // ours outright (RtlPresenter checks IsOwnUI), so nothing shaped here
            // ever belongs to the interface.
            _game.Readback.TryAdd(n, 0);
            if (!string.IsNullOrEmpty(logical) && !string.Equals(presented, logical, StringComparison.Ordinal))
                _presentedToLogical[n] = logical;
        }

        /// <summary>The logical string behind a presented one, or null when the text is not ours.</summary>
        public string PresentedLogical(string displayed)
        {
            if (string.IsNullOrEmpty(displayed) || _presentedToLogical.IsEmpty) return null;
            string n = NormalizeForReadbackMatch(displayed);
            if (n == null) return null;
            return _presentedToLogical.TryGetValue(n, out var logical) ? logical : null;
        }

        /// <summary>
        /// Index a produced translation for decoration-insensitive recognition.
        /// Only entries whose value is a REAL translation are indexed: when the normalized value
        /// equals the normalized key, the "translation" is the source text itself (unchanged output,
        /// or a typewriter frame whose only difference is a tag). Indexing those would let the gate
        /// refuse genuine source text — measured on the bench, that single guard took one game from
        /// 42 wrong matches down to zero.
        /// </summary>
        private void IndexReadback(string key, string value, bool ownUi)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;
            string nv = NormalizeForReadbackMatch(value);
            if (nv == null) return;
            string nk = NormalizeForReadbackMatch(key);
            if (nk != null && string.Equals(nk, nv, StringComparison.Ordinal)) return;
            Of(ownUi).Readback.TryAdd(nv, 0);
        }

        /// <summary>
        /// Record one translated value in its own side's reverse indexes, so the text can be
        /// recognised as OUR output if it comes back — exactly, or wearing a decoration.
        ///
        /// 🔴 **Its own side.** This is not storage, it is the anti-loop device that stops a string
        /// on screen from being learnt as a new source — and it must not reach across: a mod
        /// interface translation marking a GAME text as "already translated" removes that line from
        /// the file that gets published, and the interface file does not travel with it, so nothing
        /// downstream could ever show the cause.
        /// </summary>
        /// <param name="normalizeNumbers">Whether numbers are lifted into slots (the <c>normalize_numbers</c> setting): the exact index holds the KEY shape.</param>
        public void Index(string key, string value, bool ownUi, bool normalizeNumbers)
        {
            if (string.IsNullOrEmpty(value) || key == value) return;

            string normalized = NormalizeLineEndings(value);
            if (normalizeNumbers)
                normalized = ExtractNumbersToPlaceholders(normalized, out _);

            Of(ownUi).Target.TryAdd(normalized.TrimEnd(), 0);
            IndexReadback(key, value, ownUi);
        }

        /// <summary>
        /// Mark a displayed text as one of ours WITHOUT a value to index it from: a stale
        /// translation whose entry is gone from the reloaded cache is kept on screen and must never
        /// be queued. Takes the key shape, trailing whitespace trimmed, as the exact index holds it.
        /// </summary>
        public void MarkTarget(string trimmedNormalized, bool ownUi)
        {
            if (string.IsNullOrEmpty(trimmedNormalized)) return;
            Of(ownUi).Target.TryAdd(trimmedNormalized, 0);
        }

        /// <summary>
        /// The exact half alone: is this key-shaped text one of the values we wrote? Asked by the
        /// scanner to decide whether a component already shows translated text (and so wants the
        /// clone font), where the decoration-insensitive half would say more than it knows.
        /// </summary>
        public bool IsTarget(string trimmedNormalized, bool ownUi)
        {
            if (string.IsNullOrEmpty(trimmedNormalized)) return false;
            return Of(ownUi).Target.ContainsKey(trimmedNormalized);
        }

        /// <summary>
        /// "This text is ALREADY in the target language — do not translate it." The single question
        /// every gate must ask, and the single place that answers it: the exact reverse cache, then
        /// the decoration-insensitive index.
        /// </summary>
        /// <param name="text">The text as shown.</param>
        /// <param name="normalizedTrimmed">The same text in the key shape, trailing whitespace trimmed — what the exact index holds. The caller computes it; this class never touches the game's variables.</param>
        /// <param name="ownUi">
        /// Which side is asking. 🔴 **The two never answer for each other**: a mod-interface
        /// translation that marked a GAME text as already translated would take that line out of
        /// what gets published, invisibly.
        /// </param>
        public bool IsAlreadyTarget(string text, string normalizedTrimmed, bool ownUi)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (normalizedTrimmed != null && Of(ownUi).Target.ContainsKey(normalizedTrimmed)) return true;
            return IsReadback(text, ownUi);
        }

        /// <summary>
        /// True when the text is one of our own translations handed back by the game with a different
        /// decoration — or a fragment of a presented string of ours. Callers must treat it exactly
        /// like an exact reverse-cache hit: leave the text alone. Nothing on screen changes — the
        /// game's own rendering is kept as the developer built it; we simply refuse to learn from it.
        /// </summary>
        public bool IsReadback(string text, bool ownUi)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var index = Of(ownUi).Readback;
            if (index.Count == 0) return IsFragmentOfPresentedText(text);
            string n = NormalizeForReadbackMatch(text);
            if (n == null || !index.ContainsKey(n)) return IsFragmentOfPresentedText(text);

            if (_skipLogCount < SkipLogBudget)
            {
                _skipLogCount++;
                Debug?.Invoke($"[Readback] Not queued, this is our own translation re-decorated by the game: '{(text.Length > 60 ? text.Substring(0, 60) + "..." : text)}'");
            }
            return true;
        }
    }
}
