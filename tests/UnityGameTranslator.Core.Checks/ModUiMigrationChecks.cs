using System;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What becomes of the mod's interface lines a game translation is still carrying.
    ///
    /// ⚠ The stake, both ways. Keep too much and an interface line written by a stranger comes back
    /// — somebody else choosing what "Apply" and "Keep mine" say on a mod that handles tokens and
    /// file replacement. Keep too little and a pass of the translator is thrown away silently.
    ///
    /// ⚠ And the second rule is quieter still: it decides whether a file whose only difference with
    /// the published copy is WHERE the mod's labels are kept reads as "in sync" or as work waiting
    /// to be shared. Getting it wrong shows a count somebody holding another person's Main can
    /// never clear — and no exception is thrown either way.
    /// </summary>
    internal static class ModUiMigrationChecks
    {
        /// <summary>Named arguments once, so each case below reads as its own sentence.</summary>
        private static ModUiMigration.Verdict Decide(bool inAncestor, bool alreadyHeld, bool isEmpty,
                                                     string lineLanguage, string interfaceLanguage)
            => ModUiMigration.Decide(inAncestor, alreadyHeld, isEmpty, lineLanguage, interfaceLanguage);

        public static void Run(Action<bool, string, string> check)
        {
            // ── Which stranded lines survive ──────────────────────────────
            // Nothing established on the receiving side: the ordinary first migration.
            check(Decide(false, false, false, null, null) == ModUiMigration.Verdict.Move,
                "a line this machine wrote is kept",
                "it is a pass of the translator, and nothing else holds it");

            check(Decide(true, false, false, null, null) == ModUiMigration.Verdict.Drop,
                "a line the published copy carries is dropped",
                "it arrived with somebody else's translation — that is the deception this closes");

            check(Decide(false, true, false, null, null) == ModUiMigration.Verdict.Drop,
                "the interface file wins over a leftover",
                "what it holds is the current language's work; this is what an old file still had");

            check(Decide(false, false, true, null, null) == ModUiMigration.Verdict.Drop,
                "an empty line is dropped",
                "there is no work in it, and the interface file has no editor to fill one in");

            // Any one reason is enough: they are not weighed against each other.
            check(Decide(true, true, true, null, null) == ModUiMigration.Verdict.Drop,
                "reasons to drop do not cancel out",
                "a rule that needed all three would keep a stranger's line whenever one was absent");

            // ── And it must be in the right language ──────────────────────
            // 🔴 The case that shipped broken: a translations.json restored from a Thai-era backup
            // put 28 Thai labels into an interface file stamped French, because nothing asked.
            check(Decide(false, false, false, "Thai", "French") == ModUiMigration.Verdict.Drop,
                "a line of another language is dropped",
                "an interface translated into one language is noise in another — this shipped broken");

            check(Decide(false, false, false, "French", "French") == ModUiMigration.Verdict.Move,
                "a line of the same language is kept",
                "the ordinary case, and refusing it would throw away a pass of the translator");

            check(Decide(false, false, false, "fr", "French") == ModUiMigration.Verdict.Move,
                "a code and a name are the same language",
                "comparing them as text answers 'different language' for a language against itself");

            check(Decide(false, false, false, null, "French") == ModUiMigration.Verdict.Drop
                  && Decide(false, false, false, "auto", "French") == ModUiMigration.Verdict.Drop,
                "an unknown language may not join a file that has one",
                "refusing costs a pass of the translator; accepting mixes an interface for good");

            check(Decide(false, false, false, "Thai", null) == ModUiMigration.Verdict.Move
                  && Decide(false, false, false, null, "auto") == ModUiMigration.Verdict.Move,
                "an interface file that says nothing takes the line",
                "there is nothing to contradict, and the line is what settles the file's language");

            // ── What the hash must still count ────────────────────────────
            check(ModUiMigration.StillCountsAsPublished(ModUi.Tag, presentLocally: false),
                "an interface line published and no longer held still counts",
                "the file_hash is a contract; what moved is where we keep our own labels");

            check(!ModUiMigration.StillCountsAsPublished(ModUi.Tag, presentLocally: true),
                "one still held is not counted twice",
                "it is already in the lines being hashed");

            check(!ModUiMigration.StillCountsAsPublished("H", presentLocally: false)
                  && !ModUiMigration.StillCountsAsPublished("A", presentLocally: false)
                  && !ModUiMigration.StillCountsAsPublished("V", presentLocally: false)
                  && !ModUiMigration.StillCountsAsPublished("S", presentLocally: false),
                "a missing GAME line is never added back",
                "that is a deletion somebody made, and hiding it would offer to restore it");

            check(!ModUiMigration.StillCountsAsPublished(null, presentLocally: false),
                "a line with no tag at all is a game line",
                "files written before tags existed hold bare strings; they are the game's");
        }
    }
}
