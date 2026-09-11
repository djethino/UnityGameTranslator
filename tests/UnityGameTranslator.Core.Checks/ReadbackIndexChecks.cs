using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// "Is this text one of OUR translations coming back?", across the sequence that makes the
    /// answer right or wrong: index, ask, re-decorate, clear, ask again — on the side that was asked.
    ///
    /// 🔴 **The dangerous direction is the silent one.** An index that answers "yes" too widely
    /// refuses genuine source text, and the game stays untranslated with nothing said. One that
    /// answers "no" too narrowly lets our own output be queued as a new source: translated again,
    /// stored under a target-language key, published to everybody. Both are invisible from a
    /// question asked once; both show in a sequence.
    /// </summary>
    internal static class ReadbackIndexChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            Fresh(check);
            Exact(check);
            Decorated(check);
            NotARealTranslation(check);
            TwoSides(check);
            Presented(check);
            Cleared(check);
            LogBudget(check);
        }

        private static void Fresh(Action<bool, string, string> check)
        {
            var index = new ReadbackIndex();
            check(!index.IsAlreadyTarget("Jouer", "Jouer", ownUi: false) && !index.IsReadback("Jouer", ownUi: false),
                "an empty index recognises nothing",
                "before anything is written, every text on screen is the game's");

            check(index.TargetCount(false) == 0 && index.ReadbackCount(false) == 0
                  && index.TargetCount(true) == 0 && index.ReadbackCount(true) == 0,
                "and holds nothing on either side",
                "the load log prints these; a count that starts above zero would be lying about the file");

            check(!index.IsAlreadyTarget("", "", false) && !index.IsAlreadyTarget(null, null, false) && !index.IsReadback(null, false),
                "nothing is never ours",
                "an empty text is not queued anyway; saying it is ours would be a claim about nothing");
        }

        private static void Exact(Action<bool, string, string> check)
        {
            var index = new ReadbackIndex();
            index.Index("Play", "Jouer", ownUi: false, normalizeNumbers: true);

            check(index.IsAlreadyTarget("Jouer", "Jouer", false),
                "a value written is recognised when it comes back as written",
                "this is the loop the index exists to break: our output must not become a source");

            check(!index.IsAlreadyTarget("Play", "Play", false),
                "and its SOURCE is not",
                "the key is the game's text; refusing it would leave that line untranslated for ever");

            index.Index("Level 5", "Niveau 5", ownUi: false, normalizeNumbers: true);
            check(index.IsAlreadyTarget("Niveau 12", "Niveau [!v*0]", false),
                "the exact index holds the KEY shape, numbers as slots",
                "the game re-fills the slot with another value; the gate asks with the shape the file stores");

            var literal = new ReadbackIndex();
            literal.Index("Level 5", "Niveau 5", ownUi: false, normalizeNumbers: false);
            check(literal.IsTarget("Niveau 5", false) && !literal.IsTarget("Niveau [!v*0]", false),
                "with numbers left alone, the exact index holds the value literally",
                "normalize_numbers is a setting; the exact half follows it or it never matches the key shape");

            check(literal.IsAlreadyTarget("Niveau 12", "Niveau 12", false),
                "while the decoration-insensitive half still knows the sentence with another number",
                "that half collapses every number to one token whatever the setting; it is what catches a re-filled slot");

            index.MarkTarget("Encore là", ownUi: false);
            check(index.IsAlreadyTarget("Encore là", "Encore là", false),
                "a text marked without a value is ours from then on",
                "a stale translation whose entry is gone stays on screen and must never be queued");

            check(index.IsTarget("Jouer", false) && !index.IsTarget("<b>Jouer</b>", false) && !index.IsTarget(null, false),
                "the exact half can be asked alone",
                "the scanner decides the clone font on what was WRITTEN, never on a decorated guess");
        }

        private static void Decorated(Action<bool, string, string> check)
        {
            var index = new ReadbackIndex();
            index.Index("You have [!v*0] apples", "Vous avez [!v*0] pommes", ownUi: false, normalizeNumbers: true);

            check(index.IsReadback("<color=#F4FF58>Vous avez 3 pommes</color>", false),
                "our translation re-formatted by the game is still ours",
                "a slot re-filled and a colour tag added is what the exact index misses; this is where target-language keys came from");

            check(index.IsAlreadyTarget("<i>Vous avez 3 pommes</i>", "<i>Vous avez [!v*0] pommes</i>", false),
                "through the gate's single question too",
                "every gate asks IsAlreadyTarget; the decoration-insensitive index is its second half");

            check(!index.IsReadback("Vous avez des pommes", false),
                "a different sentence in the target language is not",
                "the comparison keeps the letters: two texts only collide when they carry the same words");

            check(!index.IsReadback("3", false) && !index.IsReadback("100%", false),
                "a number alone is never recognised this way",
                "short numeric strings all collapse onto each other; matching them would refuse every score on screen");
        }

        private static void NotARealTranslation(Action<bool, string, string> check)
        {
            var index = new ReadbackIndex();
            index.Index("Menu", "Menu", ownUi: false, normalizeNumbers: true);
            check(!index.IsAlreadyTarget("Menu", "Menu", false) && index.TargetCount(false) == 0 && index.ReadbackCount(false) == 0,
                "a value equal to its key is not indexed",
                "it is the source text itself; indexing it would let the gate refuse genuine source text — 42 wrong matches on one game, then zero");

            index.Index("Score [!v*0]", "<b>Score [!v*0]</b>", ownUi: false, normalizeNumbers: true);
            check(index.ReadbackCount(false) == 0,
                "nor is a value that differs from its key only by a tag",
                "a typewriter frame whose only difference is a tag would otherwise mark the plain sentence as ours");

            check(index.TargetCount(false) == 1,
                "though the exact index keeps that decorated form",
                "as written, it IS what we put on screen; only the decoration-insensitive index must refuse it");

            index.Index("Clé", "", ownUi: false, normalizeNumbers: true);
            index.Index(null, null, ownUi: false, normalizeNumbers: true);
            check(index.TargetCount(false) == 1 && index.ReadbackCount(false) == 0,
                "an entry with no value indexes nothing",
                "a capture waiting for a translation has no output of ours to recognise");
        }

        private static void TwoSides(Action<bool, string, string> check)
        {
            var index = new ReadbackIndex();
            index.Index("Apply", "Appliquer", ownUi: true, normalizeNumbers: true);

            check(index.IsAlreadyTarget("Appliquer", "Appliquer", ownUi: true),
                "the interface's translation is recognised on the interface's side",
                "our own labels read back from our own components must not be re-learnt either");

            check(!index.IsAlreadyTarget("Appliquer", "Appliquer", ownUi: false) && !index.IsReadback("Appliquer", ownUi: false),
                "and NOT on the game's",
                "one shared index took a game line out of the published file, invisibly, because a menu label of ours matched it");

            index.Index("Play", "Jouer", ownUi: false, normalizeNumbers: true);
            check(!index.IsAlreadyTarget("Jouer", "Jouer", ownUi: true) && !index.IsReadback("<i>Jouer</i>", ownUi: true),
                "the other way round as well",
                "a game's register is not this tool's; neither side answers for the other");

            check(index.TargetCount(true) == 1 && index.TargetCount(false) == 1,
                "each side counts its own",
                "the load log names both, and a shared count would hide which file taught what");
        }

        private static void Presented(Action<bool, string, string> check)
        {
            var index = new ReadbackIndex();
            string glyphs = new string((char)TextShaping.PrivateGlyphs.First, 1) + new string((char)(TextShaping.PrivateGlyphs.First + 1), 1);
            string presented = glyphs + " shaped display";
            index.RegisterPresented(presented, "logical source");

            check(index.IsReadback(presented, ownUi: false),
                "a presented (shaped) string is ours",
                "a display form must never be queued to the AI, cached as a source, or written to translations.json (D8)");

            check(index.IsReadback(glyphs, ownUi: false),
                "and so is a FRAGMENT of it carrying our private glyphs",
                "a typewriter frame of a shaped string is a fragment no whole-string index recognises; a private codepoint can be nothing but ours");

            check(!index.IsReadback(new string((char)(TextShaping.PrivateGlyphs.First + 7), 1), ownUi: false),
                "but not a private glyph we never presented",
                "a fragment is recognised INSIDE a presented string, not by its alphabet alone");

            check(index.PresentedLogical(presented) == "logical source",
                "the logical truth behind a presented string is recoverable",
                "without this map the in-game editor resolved a shaped display back to a shaped KEY and offered to save it");

            check(index.PresentedLogical("something else") == null && index.PresentedLogical("") == null,
                "and null for a text that is not ours",
                "a caller reads null as 'resolve the displayed text as it is'");

            var same = new ReadbackIndex();
            same.RegisterPresented("plain text", "plain text");
            check(same.PresentedLogical("plain text") == null && same.IsReadback("plain text", false),
                "a presented string equal to its logical form is refused but has no truth to recover",
                "the map only says 'and HERE is its truth' when the two differ");

            check(index.IsReadback(glyphs, ownUi: true),
                "a fragment carrying our private glyphs is ours whichever side asks",
                "the presented strings are one list, not two: a codepoint named by our font assets is ours before it is anybody's");

            check(index.ReadbackCount(false) == 1 && index.ReadbackCount(true) == 0,
                "while the presented string itself is indexed on the game's side only",
                "the RTL pass runs on the game's components and skips ours outright");
        }

        private static void Cleared(Action<bool, string, string> check)
        {
            var index = new ReadbackIndex();
            index.Index("Play", "Jouer", ownUi: false, normalizeNumbers: true);
            index.Index("Apply", "Appliquer", ownUi: true, normalizeNumbers: true);
            string glyph = new string((char)TextShaping.PrivateGlyphs.First, 1);
            index.RegisterPresented(glyph + " shaped", "logical");

            index.ClearGame();
            check(!index.IsAlreadyTarget("Jouer", "Jouer", false) && !index.IsReadback("<b>Jouer</b>", false) && index.PresentedLogical(glyph + " shaped") == null,
                "clearing the game's side forgets its two indexes and the presented map",
                "an index kept beside a reloaded cache goes on answering about a file that is gone");

            check(index.IsAlreadyTarget("Appliquer", "Appliquer", true),
                "and leaves the interface's side alone",
                "the interface file was not re-read; forgetting it would let our own labels be re-learnt");

            index.Index("Play", "Jouer", ownUi: false, normalizeNumbers: true);
            index.ClearOwnUi();
            check(!index.IsAlreadyTarget("Appliquer", "Appliquer", true) && index.IsAlreadyTarget("Jouer", "Jouer", false),
                "clearing the interface's side is the mirror image",
                "a language set aside leaves its labels in here otherwise, answering about a file they left");
        }

        private static void LogBudget(Action<bool, string, string> check)
        {
            var said = new List<string>();
            var index = new ReadbackIndex { Debug = said.Add };
            index.Index("You have [!v*0] apples", "Vous avez [!v*0] pommes", ownUi: false, normalizeNumbers: true);

            for (int i = 0; i < 25; i++) index.IsReadback($"<b>Vous avez {i} pommes</b>", false);
            check(said.Count == 10,
                "the 'not queued, this is ours' line is said ten times, then not again",
                "a game that re-decorates every frame would otherwise write a log nobody can read");

            index.ClearGame();
            index.Index("You have [!v*0] apples", "Vous avez [!v*0] pommes", ownUi: false, normalizeNumbers: true);
            index.IsReadback("<b>Vous avez 1 pommes</b>", false);
            check(said.Count == 11,
                "and a reload of the game's file opens the budget again",
                "a new file is a new session for the log; what it refuses is worth seeing once more");

            var silent = new ReadbackIndex();
            silent.Index("Play", "Jouer", ownUi: false, normalizeNumbers: true);
            check(silent.IsReadback("<b>Jouer</b>", false),
                "with no log wired, the answer is the same",
                "the log is a report, never a condition");
        }
    }
}
