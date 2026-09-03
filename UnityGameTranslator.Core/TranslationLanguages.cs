using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// What languages a translation is in — and who gets to say so.
    ///
    /// 🔴 **A translation identified by a uuid HAS a source and a target.** They settle the moment
    /// its first line is written, before any publication, and publishing freezes them for good: the
    /// server keeps the languages a lineage was published with and ignores any sent with an update
    /// (`TranslationService::resolveLanguages`), so no user can move them.
    ///
    /// 🔴 **The language is a property of the TRANSLATION, not of the machine.** `config.json` says
    /// what somebody wants; `translations.json` IS an English→Thai translation. Reading the first as
    /// if it were the second is what let the mod announce "English → French" over a Thai file, and
    /// what let a language change leave one file holding two languages with nothing said.
    ///
    /// ⚠ Pure by contract: no Unity, no state, no clock. See tests/UnityGameTranslator.Core.Checks.
    /// </summary>
    public static class TranslationLanguages
    {
        /// <summary>
        /// Which language answer wins, from the three places that can give one.
        ///
        ///  1. **the server**, whenever it has answered about this lineage. The only durable
        ///     authority — and the only one a mod too old to know about the file's own stamp cannot
        ///     erase, which is what makes it the right rank while the fleet is mixed;
        ///  2. **the file**, when the server has not answered — never published, or offline. This is
        ///     the restored-backup case: the file knows what it is, the configuration does not;
        ///  3. **the configuration**, when neither does.
        ///
        /// ⚠ **"auto" is not an answer at any rank.** A source still reading "auto" beside a server
        /// that states one is what a version that forgot to write it back leaves behind: common,
        /// entirely legitimate, and repaired by taking the server's — never by refusing anything.
        /// </summary>
        /// <returns>The settled language, or null when nobody has said.</returns>
        public static string Resolve(string fromServer, string fromFile, string fromConfig)
        {
            if (Languages.IsSettled(fromServer)) return fromServer;
            if (Languages.IsSettled(fromFile)) return fromFile;
            return Languages.IsSettled(fromConfig) ? fromConfig : null;
        }

        /// <summary>Which side of a pair a disagreement is on. Null when there is none.</summary>
        public enum Side
        {
            None,
            Source,
            Target,
        }

        /// <summary>
        /// Whether the file in hand may be published into the lineage the server holds.
        ///
        /// 🔴 **A published translation's language never changes, so a file claiming another one is
        /// not an update of it — it is a different translation.** Editing translations.json by hand,
        /// or restoring a backup from a time the game was played in another language, produces
        /// exactly that, and publishing it would push content of one language into a lineage
        /// declared as another. The way out is a Fork, which is a new lineage and may say what it
        /// likes.
        ///
        /// ⚠ **Only two STATED languages can disagree.** Unsettled on either side is the ordinary
        /// state of a translation whose local source was never written back; it resolves, it never
        /// blocks. Refusing there would turn the commonest legitimate case into a dead end — which
        /// is the one outcome this rule must not have.
        ///
        /// ⚠ The target is reported first when both differ: it is what a player sees, and naming
        /// two problems at once in a refusal helps nobody act on either.
        /// </summary>
        public static Side PublicationConflict(string fileSource, string fileTarget,
                                               string serverSource, string serverTarget)
        {
            if (Languages.Disagree(fileTarget, serverTarget)) return Side.Target;
            if (Languages.Disagree(fileSource, serverSource)) return Side.Source;
            return Side.None;
        }

        /// <summary>
        /// What to tell somebody whose file disagrees with what was published.
        ///
        /// 🔴 **It names both values.** "The languages do not match" leaves the person to work out
        /// which one and what it should be, in a file they may never have opened; the two names are
        /// the whole of the diagnosis and they cost eleven words. It also names what to do — a
        /// refusal with no way out is where somebody gives up.
        ///
        /// ⚠ In plain international English, like everything else the mod writes: this is read by
        /// people whose fourth language it is. See .claude/rules/name-things-in-ui.md.
        ///
        /// ⚠ Here rather than in the socle only because nothing else says it yet. The day the
        /// manager refuses the same thing, this sentence moves there — one fact, one wording.
        /// </summary>
        /// <returns>Null when there is nothing to say.</returns>
        public static string ExplainConflict(Side side, string fileSource, string fileTarget,
                                             string serverSource, string serverTarget)
        {
            if (side == Side.None) return null;

            string published = Show(serverSource) + " to " + Show(serverTarget);
            string here = Show(fileSource) + " to " + Show(fileTarget);

            return $"This translation is published as {published}. The file here says {here}. "
                 + "A published translation keeps the languages it was published with, so this "
                 + "cannot be sent as an update. Use Fork to publish it as a translation of its own.";
        }

        private static string Show(string language) =>
            Languages.IsSettled(language) ? language : "unknown";
    }
}
