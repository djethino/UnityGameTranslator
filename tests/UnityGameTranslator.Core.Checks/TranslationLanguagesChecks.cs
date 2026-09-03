using System;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What languages a translation is in, and who gets to say so.
    ///
    /// ⚠ The stake is asymmetric, which is why both directions are pinned here. Resolve the wrong
    /// way and the mod translates towards one language over a file written in another, silently.
    /// Refuse the wrong way and somebody cannot publish their own work — and the commonest state of
    /// all is a source still reading "auto", so a rule that treats unsettled as disagreement would
    /// block the majority of legitimate translations rather than a handful of broken ones.
    /// </summary>
    internal static class TranslationLanguagesChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // ── What counts as an answer ──────────────────────────────────
            check(Languages.IsSettled("French") && Languages.IsSettled("fr"),
                "a name and a code are answers",
                "both forms are stored by different parts of this project");

            check(!Languages.IsSettled("auto") && !Languages.IsSettled("AUTO")
                  && !Languages.IsSettled("") && !Languages.IsSettled(null),
                "'auto', empty and absent are not",
                "auto is a mode — detect, or follow the system — and it must never travel as a language");

            check(Languages.IsSettled("Klingon"),
                "a language the catalogue does not know is still an answer",
                "refusing it would reclassify somebody's translation as undecided");

            // ── When two answers disagree ─────────────────────────────────
            check(Languages.Disagree("Thai", "French"),
                "two different languages disagree",
                "the only case anything may act on");

            check(!Languages.Disagree("French", "fr") && !Languages.Disagree("fr", "French"),
                "a name and its code do not",
                "comparing raw text answers 'different language' for a language against itself");

            check(!Languages.Disagree("auto", "French") && !Languages.Disagree("French", "auto")
                  && !Languages.Disagree(null, "French") && !Languages.Disagree("French", null),
                "unsettled on either side is never a disagreement",
                "a source left at 'auto' is what a version that forgot to write it back leaves — repair it, never refuse it");

            // ── Who answers ───────────────────────────────────────────────
            check(TranslationLanguages.Resolve("English", "Thai", "French") == "English",
                "the server outranks everything",
                "a lineage's languages are frozen at publication and no user may move them");

            check(TranslationLanguages.Resolve(null, "Thai", "French") == "Thai",
                "the file outranks the configuration",
                "the restored-backup case: the file knows what it is, the machine's setting does not");

            check(TranslationLanguages.Resolve(null, null, "French") == "French",
                "the configuration answers when nothing else does",
                "a translation nobody has published and no file has stamped");

            check(TranslationLanguages.Resolve("auto", "auto", "French") == "French",
                "'auto' is not an answer at any rank",
                "letting a mode outrank a stated language is how a source of 'auto' won over the server");

            check(TranslationLanguages.Resolve(null, null, null) == null,
                "nobody has said, and that is reported as such",
                "inventing one here would put a guess into every prompt");

            // ── What may be published ─────────────────────────────────────
            check(TranslationLanguages.PublicationConflict("English", "Thai", "English", "French")
                    == TranslationLanguages.Side.Target,
                "a file in another target may not be published",
                "it would push content of one language into a lineage declared as another");

            check(TranslationLanguages.PublicationConflict("Japanese", "French", "English", "French")
                    == TranslationLanguages.Side.Source,
                "nor one in another source",
                "the source goes into every prompt and decides what strict_source_language retires");

            check(TranslationLanguages.PublicationConflict("auto", "French", "English", "French")
                    == TranslationLanguages.Side.None,
                "a source of 'auto' publishes fine",
                "🔴 the commonest state of all — only an upload from this machine used to write it back");

            check(TranslationLanguages.PublicationConflict(null, null, "English", "French")
                    == TranslationLanguages.Side.None,
                "a file that states nothing publishes fine",
                "every file written before the mod stamped its languages, which is most of them");

            check(TranslationLanguages.PublicationConflict("English", "French", null, null)
                    == TranslationLanguages.Side.None,
                "and so does one the server says nothing about",
                "offline, or never published — there is nothing to disagree with");

            check(TranslationLanguages.PublicationConflict("English", "fr", "English", "French")
                    == TranslationLanguages.Side.None,
                "a code against a name is not a conflict",
                "refusing there would block a translation over a spelling");

            // Both wrong: one thing is named, not two.
            check(TranslationLanguages.PublicationConflict("Japanese", "Thai", "English", "French")
                    == TranslationLanguages.Side.Target,
                "the target is named first when both differ",
                "it is what a player sees, and two problems at once help nobody act on either");

            // ── What the refusal says ─────────────────────────────────────
            string said = TranslationLanguages.ExplainConflict(TranslationLanguages.Side.Target,
                "English", "Thai", "English", "French");

            check(said != null && said.Contains("Thai") && said.Contains("French"),
                "the refusal names both languages",
                "'the languages do not match' leaves somebody to guess which one and what it should be");

            check(said != null && said.Contains("Fork"),
                "and it names the way out",
                "a refusal with no way out is where somebody gives up");

            check(TranslationLanguages.ExplainConflict(TranslationLanguages.Side.None,
                    "English", "French", "English", "French") == null,
                "and says nothing when there is nothing to say",
                "a warning shown on a file that is fine is a warning nobody will read twice");
        }
    }
}
