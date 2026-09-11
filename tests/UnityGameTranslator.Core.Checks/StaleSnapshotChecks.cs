using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A text still on screen with the OLD translation after a reload: recognised, refreshed from
    /// the new cache, or kept — and never queued as a new source line.
    ///
    /// 🔴 **What is at stake.** A reload restores what it can reach; what it cannot goes on
    /// showing the previous translation, and the next time the game sets that text the gate
    /// meets a target-language sentence it has never seen. Without this net, that sentence is a
    /// miss, a miss is queued, and the old translation becomes a KEY in the file — translated
    /// again, published to everybody.
    ///
    /// ⚠ Replayed as a sequence: take, replace the cache, ask. A right answer here is right only
    /// after the snapshot was taken and against the cache that stands NOW.
    /// </summary>
    internal static class StaleSnapshotChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            BeforeAnyReload(check);
            Refreshed(check);
            Gone(check);
            NotStale(check);
            WithNumbers(check);
            Reordered(check);
            TakenAgain(check);
        }

        private static Dictionary<string, TranslationEntry> Cache(params (string key, string value, string tag)[] lines)
        {
            var cache = new Dictionary<string, TranslationEntry>();
            foreach (var l in lines) cache[l.key] = new TranslationEntry { Value = l.value, Tag = l.tag };
            return cache;
        }

        private static void BeforeAnyReload(Action<bool, string, string> check)
        {
            var snapshot = new StaleSnapshot();
            check(snapshot.IsEmpty && snapshot.ValueCount == 0 && snapshot.ReorderedCount == 0,
                "nothing is stale before the first reload",
                "there is no old cache yet; every text on screen belongs to the one that is loaded");

            var verdict = snapshot.Resolve("Jouer", "Jouer", Cache(("Play", "Jouer", "A")), normalizeNumbers: true);
            check(verdict.Kind == StaleKind.NotStale,
                "and asking answers 'not stale'",
                "the caller then goes on as if this class had not been asked");
        }

        private static void Refreshed(Action<bool, string, string> check)
        {
            var said = new List<string>();
            var snapshot = new StaleSnapshot { Debug = said.Add };
            snapshot.Take(Cache(("Play", "Jouer", "A"), ("Quit", "Quitter", "A")), normalizeNumbers: true);
            check(!snapshot.IsEmpty && snapshot.ValueCount == 2 && said.Count == 1 && said[0].Contains("2 values"),
                "a snapshot holds every translated value of the outgoing cache",
                "the log names how many, once, so a reload that snapshotted nothing can be seen");

            var current = Cache(("Play", "Jouer !", "H"), ("Quit", "Quitter", "A"));
            var verdict = snapshot.Resolve("Jouer", "Jouer", current, normalizeNumbers: true);
            check(verdict.Kind == StaleKind.Refreshed && verdict.NewText == "Jouer !" && verdict.OriginalText == "Play" && verdict.Key == "Play",
                "the old translation is recognised and the new one handed back, with its source",
                "the component shows what the reloaded file says, and its original is recorded so it can be restored");

            var unchanged = snapshot.Resolve("Quitter", "Quitter", current, normalizeNumbers: true);
            check(unchanged.Kind == StaleKind.Refreshed && unchanged.NewText == "Quitter",
                "a line the reload did not change is refreshed to itself",
                "still a stale translation, still never a miss — the answer is the same text, not 'queue it'");
        }

        private static void Gone(Action<bool, string, string> check)
        {
            var snapshot = new StaleSnapshot();
            snapshot.Take(Cache(("Play", "Jouer", "A"), ("Skip", "Sauter", "A"), ("Wait", "Attendre", "A"), ("Same", "Pareil", "A")), normalizeNumbers: true);

            var current = Cache(("Skip", "Sauter", "S"), ("Wait", "", "H"), ("Same", "Same", "A"));
            check(snapshot.Resolve("Jouer", "Jouer", current, true).Kind == StaleKind.Gone,
                "a line removed by the reload: keep what is shown, mark it ours",
                "the text stays on screen — nothing better is known — and must never become a new key");

            check(snapshot.Resolve("Sauter", "Sauter", current, true).Kind == StaleKind.Gone,
                "so is a line now skipped (S)",
                "a skipped line has no translation to show; the old one stays, unqueued");

            check(snapshot.Resolve("Attendre", "Attendre", current, true).Kind == StaleKind.Gone,
                "and one now empty",
                "a capture waiting for a translation has nothing to refresh with");

            var same = snapshot.Resolve("Pareil", "Pareil", current, true);
            check(same.Kind == StaleKind.Gone && same.Key == "Same",
                "and one now equal to its key",
                "'no translation needed' is not a text to put on screen over the old one");

            check(snapshot.Resolve("Jouer", "Jouer", null, true).Kind == StaleKind.Gone,
                "with no current cache at all, everything stale is gone",
                "a reload that failed leaves an empty cache; the old texts stay and stay unqueued");
        }

        private static void NotStale(Action<bool, string, string> check)
        {
            var snapshot = new StaleSnapshot();
            snapshot.Take(Cache(("Play", "Jouer", "A"), ("Menu", "Menu", "A"), ("Empty", "", "H")), normalizeNumbers: true);

            check(snapshot.Resolve("Play", "Play", Cache(("Play", "Jouer", "A")), true).Kind == StaleKind.NotStale,
                "a source text is not stale",
                "the game's own text goes through the gate as usual; only OUR old output is caught here");

            check(snapshot.Resolve("Menu", "Menu", Cache(("Menu", "Menu", "A")), true).Kind == StaleKind.NotStale && snapshot.ValueCount == 1,
                "a value equal to its key was never snapshotted",
                "it is the source text itself; catching it would refresh a line the game owns");

            check(snapshot.Resolve("Never seen", "Never seen", Cache(), true).Kind == StaleKind.NotStale,
                "a text the old cache never produced is not stale",
                "a genuine new line must reach the queue; the net catches ours, not everything");

            check(snapshot.Resolve("", "", Cache(), true).Kind == StaleKind.NotStale && snapshot.Resolve(null, null, Cache(), true).Kind == StaleKind.NotStale,
                "nothing is not stale",
                "an empty text has no old translation to be");
        }

        private static void WithNumbers(Action<bool, string, string> check)
        {
            var snapshot = new StaleSnapshot();
            snapshot.Take(Cache(("Level [!v*0]", "Niveau [!v*0]", "A")), normalizeNumbers: true);

            var current = Cache(("Level [!v*0]", "Étage [!v*0]", "H"));
            var verdict = snapshot.Resolve("Niveau 7", "Niveau [!v*0]", current, normalizeNumbers: true);
            check(verdict.Kind == StaleKind.Refreshed && verdict.NewText == "Étage 7" && verdict.OriginalText == "Level 7",
                "live numbers are carried from the old translation into the new one, and into the source",
                "one entry serves every value; the refreshed text and the recorded original both show the value on screen");

            var trailing = snapshot.Resolve("Niveau 7\n", "Niveau [!v*0]", current, normalizeNumbers: true);
            check(trailing.Kind == StaleKind.Refreshed && trailing.NewText == "Étage 7",
                "trailing whitespace on screen does not hide the match",
                "TMP often strips it when displaying; the snapshot stores its values trimmed for that reason");

            var literal = new StaleSnapshot();
            literal.Take(Cache(("Level 5", "Niveau 5", "A")), normalizeNumbers: false);
            var lit = literal.Resolve("Niveau 5", "Niveau 5", Cache(("Level 5", "Étage 5", "A")), normalizeNumbers: false);
            check(lit.Kind == StaleKind.Refreshed && lit.NewText == "Étage 5" && lit.OriginalText == "Level 5",
                "with numbers left alone, the value is matched literally",
                "normalize_numbers is a setting; off, the snapshot holds what the file holds");
        }

        private static void Reordered(Action<bool, string, string> check)
        {
            var snapshot = new StaleSnapshot();
            snapshot.Take(Cache(("[!v*0] of [!v*1]", "[!v*1] sur [!v*0]", "A"), ("Level [!v*0]", "Niveau [!v*0]", "A")), normalizeNumbers: true);
            check(snapshot.ReorderedCount == 1,
                "only a translation that reordered its slots keeps a regex",
                "the normalized value answers for the others; a regex per line would be a scan per text");

            var current = Cache(("[!v*0] of [!v*1]", "[!v*1] / [!v*0]", "H"));
            var verdict = snapshot.Resolve("12 sur 3", "[!v*0] sur [!v*1]", current, normalizeNumbers: true);
            check(verdict.Kind == StaleKind.Refreshed && verdict.NewText == "12 / 3" && verdict.OriginalText == "3 of 12",
                "a reordered translation is recognised by its regex, each slot by name",
                "by position, 12 would land in slot 0 and the refreshed text would swap the two values");

            check(snapshot.Resolve("12 contre 3", "[!v*0] contre [!v*1]", current, true).Kind == StaleKind.NotStale,
                "and a sentence the regex does not match is not stale",
                "the regex is anchored to the old translation's words; another sentence is another line");
        }

        private static void TakenAgain(Action<bool, string, string> check)
        {
            var snapshot = new StaleSnapshot();
            snapshot.Take(Cache(("Play", "Jouer", "A")), normalizeNumbers: true);
            snapshot.Take(Cache(("Quit", "Quitter", "A")), normalizeNumbers: true);

            check(snapshot.Resolve("Quitter", "Quitter", Cache(("Quit", "Quitter", "A")), true).Kind == StaleKind.Refreshed
                  && snapshot.Resolve("Jouer", "Jouer", Cache(("Play", "Jouer", "A")), true).Kind == StaleKind.NotStale,
                "a second reload takes a new snapshot over the first",
                "the net stands for the cache that was JUST replaced; an older one's texts were restored two reloads ago");

            check(snapshot.ValueCount == 1,
                "and holds only the latest outgoing cache",
                "keeping both would let a text from two files ago be 'refreshed' from a line it never came from");
        }
    }
}
