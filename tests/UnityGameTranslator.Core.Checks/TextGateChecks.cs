using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The lookup ladder a displayed text climbs: exact → normalized → trimmed → pattern, and the
    /// three answers it can get (a translation, "known, show the source", nothing).
    ///
    /// ⚠ **The order is the semantic.** Written against <see cref="TextGate.Lookup"/> with a store
    /// built by hand, so a second Core can be held to the same answers about the same file. Every
    /// case names the rung it proves; a rung that answers out of turn shows here as the wrong
    /// value, not as a slower lookup.
    ///
    /// 🔴 Two of these cases freeze what used to differ between the two copies of this ladder
    /// (an empty entry not tagged H; a pattern after an entry equal to its key). They record the
    /// shape kept — the tracking path's — so that changing it is a decision, never a drift.
    /// </summary>
    internal static class TextGateChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            Exact(check);
            KnownNotMiss(check);
            Normalized(check);
            Trimmed(check);
            Patterns(check);
            OwnUi(check);
            Variables(check);
            Misses(check);
        }

        private static Dictionary<string, TranslationEntry> Store(params (string key, string value, string tag)[] lines)
        {
            var store = new Dictionary<string, TranslationEntry>();
            foreach (var l in lines)
                store[l.key] = new TranslationEntry { Value = l.value, Tag = l.tag };
            return store;
        }

        private static GateLookup Look(string text, Dictionary<string, TranslationEntry> store,
            bool numbers = true, bool ownUi = false, IVariableSubstitution vars = null, Func<string, string> patterns = null)
            => TextGate.Lookup(text, ownUi, store, numbers, vars, patterns);

        private static void Exact(Action<bool, string, string> check)
        {
            var store = Store(("Play", "Jouer", "A"));
            var r = Look("Play", store);
            check(r.Outcome == GateOutcome.Hit && r.Stage == GateStage.Exact && r.Value == "Jouer",
                "the text as shown hits first",
                "most of what a screen shows is already in the file, and this rung costs nothing");

            check(r.NormalizedText == "Play",
                "and the key shape is still reported",
                "a caller reading the reverse index after a miss must not have to recompute it");

            var raw = Store(("Level 5", "Niveau 5", "A"));
            var rawHit = Look("Level 5", raw, numbers: true);
            check(rawHit.Outcome == GateOutcome.Hit && rawHit.Stage == GateStage.Exact && rawHit.Value == "Niveau 5",
                "a key stored with its numbers in still hits, verbatim",
                "an older file wrote raw keys; lifting the numbers out of the text would walk past its own entry");
        }

        private static void KnownNotMiss(Action<bool, string, string> check)
        {
            var store = Store(("Play", "", "H"), ("Quit", "", "A"), ("Skip me", "Skip me", "S"), ("Same", "Same", "A"));

            var capture = Look("Play", store);
            check(capture.Outcome == GateOutcome.Known && capture.Stage == GateStage.Exact,
                "a capture with nothing in it is known, and shows the source",
                "a line waiting for a translation is not a line to queue again");

            var emptyA = Look("Quit", store);
            check(emptyA.Outcome == GateOutcome.Known,
                "so is an empty entry under ANY tag",
                "one copy of the ladder showed an EMPTY string here; a hand-written file can hold a key nobody filled in");

            check(Look("Skip me", store).Outcome == GateOutcome.Known,
                "a skipped line (S) shows the source",
                "S means the source was not in the language expected; it is never translated and never asked again");

            check(Look("Same", store).Outcome == GateOutcome.Known,
                "a value equal to its key is known too",
                "the model said this needs no translation; a miss here would ask it again at every launch");
        }

        private static void Normalized(Action<bool, string, string> check)
        {
            var store = Store(("Score: [!v*0]", "Points : [!v*0]", "A"), ("a\nb", "a\nb traduit", "A"));

            var r = Look("Score: 42", store);
            check(r.Outcome == GateOutcome.Hit && r.Stage == GateStage.Normalized && r.Value == "Points : 42",
                "a sentence with a live number reads as its slotted key, value put back",
                "this is what makes one entry serve every value the game will ever show");

            check(r.NormalizedText == "Score: [!v*0]",
                "the key shape carries the slot",
                "the reverse index is written in that shape, and is consulted in it after a miss");

            var off = Look("Score: 42", store, numbers: false);
            check(off.Outcome == GateOutcome.Miss && off.NormalizedText == "Score: 42",
                "with numbers left alone, the same text is a miss",
                "normalize_numbers is a setting; off, the file is read literally");

            var crlf = Look("a\r\nb", store);
            check(crlf.Outcome == GateOutcome.Hit && crlf.Value == "a\nb traduit",
                "line endings are normalised before the key is tried",
                "the file stores \\n only; a game on another platform shows \\r\\n");
        }

        private static void Trimmed(Action<bool, string, string> check)
        {
            var store = Store(("Play", "Jouer", "A"), ("Wait", "Wait", "A"), ("Gone", "", "H"));

            var r = Look("  Play  ", store);
            check(r.Outcome == GateOutcome.Hit && r.Stage == GateStage.Trimmed && r.Value == "Jouer",
                "padding around a stored key still finds it",
                "a component that pads its text shows the same line as one that does not");

            check(r.NormalizedText == "  Play  ",
                "and the key shape keeps the padding",
                "the trimmed key is a rung, not the shape the miss path is told about");

            check(Look("  Gone  ", store).Outcome == GateOutcome.Known,
                "a padded capture is known, not missed",
                "otherwise the padded form would be queued while the bare one waits");

            // The trimmed rung is tried only when the normalized one found nothing at all.
            var both = Store(("  Play  ", "  Play  ", "A"), ("Play", "Jouer", "A"));
            var sameFirst = Look("  Play  ", both);
            check(sameFirst.Outcome == GateOutcome.Known && sameFirst.Stage == GateStage.Exact,
                "an exact entry equal to its key stops the trimmed rung",
                "the file said this exact text needs nothing; the trimmed line is another entry");
        }

        private static void Patterns(Action<bool, string, string> check)
        {
            Func<string, string> patterns = t => t == "Level 5" ? "Niveau 5" : null;

            var empty = Store();
            var r = Look("Level 5", empty, numbers: false, patterns: patterns);
            check(r.Outcome == GateOutcome.Hit && r.Stage == GateStage.Pattern && r.Value == "Niveau 5",
                "a pattern answers when the keys did not",
                "with numbers not lifted, the cached sentence with slots is the only way this line is recognised");

            int asked = 0;
            Func<string, string> counting = t => { asked++; return null; };
            var found = Store(("Play", "Jouer", "A"));
            Look("Play", found, patterns: counting);
            check(asked == 0,
                "a hit on a key never consults the patterns",
                "patterns are a scan over every slotted sentence; the exact rung exists to avoid it");

            // The exact rung is final either way: an entry equal to its key, found as shown, stops
            // everything. It is the NORMALIZED rung that still lets a pattern speak.
            var exactSame = Store(("Level 5", "Level 5", "A"));
            var stopped = Look("Level 5", exactSame, numbers: false, patterns: counting);
            check(stopped.Outcome == GateOutcome.Known && stopped.Stage == GateStage.Exact && asked == 0,
                "an exact entry equal to its key stops the patterns too",
                "the file said this exact text needs nothing; nothing further is asked");

            Func<string, string> crlfPattern = t => t == "Level\r\n5" ? "Niveau\n5" : null;
            var normalizedSame = Store(("Level\n5", "Level\n5", "A"));
            var afterSame = Look("Level\r\n5", normalizedSame, numbers: false, patterns: crlfPattern);
            check(afterSame.Outcome == GateOutcome.Hit && afterSame.Stage == GateStage.Pattern,
                "a pattern is still tried after a NORMALIZED entry equal to its key",
                "the shape kept from the tracking path; the other copy stopped at the entry — changing this is a decision");

            var known = Store(("Same\nline", "Same\nline", "A"));
            var stillKnown = Look("Same\r\nline", known, patterns: counting);
            check(stillKnown.Outcome == GateOutcome.Known && stillKnown.Stage == GateStage.Normalized,
                "and when no pattern answers, the line stays known",
                "known is not a miss: nothing is queued for a line the file already holds");
        }

        private static void OwnUi(Action<bool, string, string> check)
        {
            var vars = new FakeVariables(("Bob", 3));
            int asked = 0;
            Func<string, string> patterns = t => { asked++; return "from a pattern"; };

            var ui = Store(("Hello Bob", "Bonjour Bob", "M"));
            var r = Look("Hello Bob", ui, ownUi: true, vars: vars, patterns: patterns);
            check(r.Outcome == GateOutcome.Hit && r.Value == "Bonjour Bob",
                "own-UI text is looked up as shown, in the store passed for it",
                "the game's file and the interface's never see each other's entries");

            var miss = Look("Hello Bob, welcome", ui, ownUi: true, vars: vars, patterns: patterns);
            check(miss.Outcome == GateOutcome.Miss && miss.NormalizedText == "Hello Bob, welcome" && asked == 0,
                "own UI: no variable is lifted and no pattern is consulted",
                "a game variable has no meaning on our labels, and a colliding value would eat one; patterns are built from the game's lines");

            var slot = Store(("Apply ([!v*0])", "Appliquer ([!v*0])", "M"));
            var applied = Look("Apply (3)", slot, ownUi: true, vars: vars, patterns: patterns);
            check(applied.Outcome == GateOutcome.Hit && applied.Value == "Appliquer (3)",
                "own UI still lifts its numbers",
                "\"Apply (N)\" is one label, not one per count");
        }

        private static void Variables(Action<bool, string, string> check)
        {
            var vars = new FakeVariables(("Player 7", 0));
            var store = Store(("Hello [!STR*0]", "Bonjour [!STR*0]", "A"), ("[!STR*0] has [!v*0] coins", "[!STR*0] a [!v*0] pièces", "A"));

            var r = Look("Hello Player 7", store, vars: vars);
            check(r.Outcome == GateOutcome.Hit && r.Value == "Bonjour Player 7",
                "a variable's value is lifted out and put back",
                "the sentence is one line however the game names the player");

            check(r.NormalizedText == "Hello [!STR*0]",
                "a variable is lifted BEFORE the numbers",
                "\"Player 7\" carries a digit; lifted after, the 7 would become a number slot and the key would never match");

            var both = Look("Player 7 has 12 coins", store, vars: vars);
            check(both.Outcome == GateOutcome.Hit && both.Value == "Player 7 a 12 pièces" && both.NormalizedText == "[!STR*0] has [!v*0] coins",
                "a number and a variable in one sentence both come back",
                "the order of the two restorations is not what holds this (measured); skipping either one is");

            var none = new FakeVariables();
            var untouched = Look("Hello Player 7", store, vars: none);
            check(untouched.Outcome == GateOutcome.Miss && untouched.NormalizedText == "Hello Player [!v*0]",
                "with no variable defined, nothing is lifted",
                "and the key shape then reads the digit as a number, which is what the file would hold");
        }

        private static void Misses(Action<bool, string, string> check)
        {
            var store = Store(("Play", "Jouer", "A"));

            var r = Look("Never seen", store);
            check(r.Outcome == GateOutcome.Miss && r.Stage == GateStage.None && r.Value == null,
                "a text the file does not hold is a miss, with no value",
                "the caller decides what a miss means: queue, capture, or leave alone");

            check(Look("", store).Outcome == GateOutcome.Known && Look(null, store).Outcome == GateOutcome.Known,
                "nothing is known — not missed",
                "an empty text is never queued, and never was");

            bool threw = false;
            try { TextGate.Lookup("x", false, null, true, null, null); } catch (ArgumentNullException) { threw = true; }
            check(threw,
                "a missing store is refused, not read as empty",
                "a null store would turn every line into a miss and queue the whole screen");
        }

        /// <summary>A game's variables as the gate sees them: values in, placeholders out, and back.</summary>
        private sealed class FakeVariables : IVariableSubstitution
        {
            private readonly List<KeyValuePair<int, string>> _values = new List<KeyValuePair<int, string>>();

            public FakeVariables(params (string value, int id)[] defs)
            {
                foreach (var d in defs) _values.Add(new KeyValuePair<int, string>(d.id, d.value));
            }

            public bool HasVariables => _values.Count > 0;

            public string Extract(string text, out List<KeyValuePair<int, string>> extracted)
            {
                extracted = null;
                string result = text;
                foreach (var v in _values)
                {
                    if (!result.Contains(v.Value)) continue;
                    result = result.Replace(v.Value, $"[!STR*{v.Key}]");
                    (extracted ??= new List<KeyValuePair<int, string>>()).Add(v);
                }
                return result;
            }

            public string Restore(string text, List<KeyValuePair<int, string>> extracted)
            {
                if (extracted == null) return text;
                string result = text;
                foreach (var v in extracted) result = result.Replace($"[!STR*{v.Key}]", v.Value);
                return result;
            }
        }
    }
}
