using System;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// What the caller must write back once the languages have been settled.
    ///
    /// ⚠ Returned rather than written, because the configuration object belongs to whoever owns
    /// the file: this decides, the caller saves. It is also what makes the whole thing replayable
    /// — a check reads the answer instead of watching for a disk write that never happens.
    /// </summary>
    public struct LanguageWriteBack
    {
        /// <summary>What <c>config.source_language</c> must become. Only meaningful when <see cref="ConfigChanged"/>.</summary>
        public string Source;

        /// <inheritdoc cref="Source"/>
        public string Target;

        /// <summary>True when the configuration moved and has to be saved.</summary>
        public bool ConfigChanged;
    }

    /// <summary>
    /// Which languages this translation is in — as the file states them, as the machine is set, and
    /// as the server published them — and what happens when the three disagree.
    ///
    /// 🔴 **Three sources, one answer, and the order is the whole rule.** The server outranks the
    /// file, the file outranks the configuration. The pure part of that (which value wins, and
    /// whether two values genuinely disagree) lives in the socle — <see cref="TranslationLanguages"/>
    /// and <see cref="Languages"/> — shared with the site and the Manager. What lives HERE is the
    /// sequence: when each source speaks, what is written back, and what is said about it.
    ///
    /// 🔴 **Why the sequence is the interesting part.** The file is read at launch with no network;
    /// the server answers a second later. Everything that can go wrong is a matter of moment — a
    /// machine's setting frozen into a file before the server said otherwise, a target adopted from
    /// "auto" and then following the player's system language on the next machine, a restored
    /// backup quietly mixing two languages into one file. None of these is a wrong ANSWER to a
    /// question asked once; each is a right answer taken at the wrong time.
    ///
    /// ⚠ **Pure by contract.** No Unity, no disk, no clock, no configuration object: values in, an
    /// answer and a write-back out, with the two log sinks injected so a check can read what a
    /// player would have been told — and how often, since the refusal is deliberately said three
    /// times and then not again.
    ///
    /// Cut out of TranslatorCore on 2026-09-08 (step 6 of analyse/plan-prealables-couches.md).
    /// </summary>
    public sealed class LanguageState
    {
        /// <summary>How many times one refusal is said before it stops being said.</summary>
        private const int RefusalsSaid = 3;

        private int _refusalsSaid;

        /// <summary>Where the ordinary lines go. Left null in a check that does not read them.</summary>
        public Action<string> Info { get; set; }

        /// <inheritdoc cref="Info"/>
        public Action<string> Warning { get; set; }

        /// <summary>
        /// The languages <c>translations.json</c> states for itself, or null when it states none.
        ///
        /// 🔴 **The file, not the preference.** <c>config.json</c> says what somebody wants; these
        /// say what this translation IS. They are what survives a backup restore, a copy to another
        /// machine, and a mod that never looks at the configuration.
        ///
        /// ⚠ Null on every file written before the mod stamped them, which is most of them today.
        /// Null means "this file does not say", never "this file has no language".
        /// </summary>
        public string FileSource { get; private set; }

        /// <inheritdoc cref="FileSource"/>
        public string FileTarget { get; private set; }

        /// <summary>
        /// True when the file's own languages moved and the translation has to be written back.
        ///
        /// ⚠ Never cleared here: whoever writes the file clears it, because only they know whether
        /// the write happened.
        /// </summary>
        public bool FileChanged { get; private set; }

        /// <summary>
        /// What this translation says it is, against what its lineage was published as. Null while
        /// the two agree — or while either side has not said.
        ///
        /// 🔴 **Set, nothing new is translated.** The file's lines are in one language and the
        /// lineage is declared in another: writing more in the file's language grows something that
        /// can never be published, and writing in the lineage's mixes two languages in one file.
        /// There is no safe third answer, so the mod stops producing and says so — the lines
        /// already in the file keep being applied, and the game stays playable and translated.
        ///
        /// ⚠ It takes a deliberate act to get here: restoring a backup from a time the game was
        /// played in another language, or editing translations.json by hand. The way out is the
        /// same act undone, or Fork — a new lineage, free to say what it likes.
        /// </summary>
        public string Conflict { get; private set; }

        /// <summary>Everything this translation said about itself, forgotten. A new file, or none.</summary>
        public void Reset()
        {
            FileSource = null;
            FileTarget = null;
            Conflict = null;
            FileChanged = false;
            _refusalsSaid = 0;
        }

        /// <summary>What the file states, as it is read. Only settled values are taken.</summary>
        public void StateSource(string language)
        {
            if (Languages.IsSettled(language)) FileSource = language;
        }

        /// <inheritdoc cref="StateSource"/>
        public void StateTarget(string language)
        {
            if (Languages.IsSettled(language)) FileTarget = language;
        }

        /// <summary>The file has been written; what it states and what is held agree again.</summary>
        public void FileWritten()
        {
            FileChanged = false;
        }

        /// <summary>The languages in force, resolved from the server, then the file, then the configuration.</summary>
        public string EffectiveSource(string serverSource, string configSource)
        {
            return TranslationLanguages.Resolve(serverSource, FileSource, configSource);
        }

        /// <inheritdoc cref="EffectiveSource"/>
        public string EffectiveTarget(string serverTarget, string configTarget)
        {
            return TranslationLanguages.Resolve(serverTarget, FileTarget, configTarget);
        }

        /// <summary>
        /// Whether the languages may still be changed, and why not when they may not.
        ///
        /// Two reasons, and the second was missing for a long time:
        ///
        /// · **published** — the server keeps the languages a translation was published with and
        ///   ignores any sent with an update, so nothing local could move them anyway;
        ///
        /// · 🔴 **this file already holds lines.** A target language is not a preference, it is
        ///   what the file IS: retargeting a file that already carries lines leaves every one of
        ///   them written in a language the game is no longer asking for, and the next captures
        ///   arrive in the new one. One file, two languages, and nothing said so.
        /// </summary>
        public static bool Locked(bool published, int lineCount)
        {
            return published || lineCount > 0;
        }

        /// <summary>
        /// Reconcile what the file states with what the machine is set to, at load, with no network.
        ///
        /// 🔴 **The file wins, and it settles this before a single line is translated.** That is
        /// the whole answer to "will a restored backup mix two languages": the contradiction is
        /// visible the instant the file is read — a file stating Thai beside a machine set to
        /// French — and it needs no server to be SEEN, only to be arbitrated. Waiting for one would
        /// have cost every launch a delay, and refusing to translate meanwhile would lose the lines
        /// that pass once and never come back: a toast, a line of dialogue, a typewriter reveal.
        ///
        /// ⚠ The server still outranks the file (see <see cref="AlignFromServer"/>), and it answers
        /// a second later. It cannot contradict this quietly: a published lineage that disagrees
        /// raises <see cref="Conflict"/> instead of overwriting anything.
        ///
        /// ⚠ A file that states nothing adopts the machine's setting — the only answer available —
        /// but ONLY when this lineage was never published. Where it was, the server knows and will
        /// say; guessing first would freeze a wrong answer into the file before the truth arrives.
        /// </summary>
        public LanguageWriteBack SettleFromFile(string configSource, string configTarget,
                                                int lineCount, bool everPublished)
        {
            var write = new LanguageWriteBack { Source = configSource, Target = configTarget };
            if (lineCount == 0) return write;

            if (Languages.Disagree(FileTarget, configTarget))
            {
                Warning?.Invoke($"[Languages] This translation is in {FileTarget} and this "
                                + $"game was set to {configTarget}. The translation decides: "
                                + "the setting follows it.");
                write.Target = FileTarget;
                write.ConfigChanged = true;
            }
            else if (!Languages.IsSettled(FileTarget)
                     && !everPublished
                     && Languages.IsSettled(configTarget))
            {
                FileTarget = configTarget;
                FileChanged = true;
                Info?.Invoke($"[Languages] This translation now states its target: '{FileTarget}'"
                             + " — taken from this machine's setting, the only answer available for a"
                             + " file written before it said so.");
            }

            // The source, same ladder. ⚠ Never invented: "auto" here means "detect", which is a
            // working mode and not an answer, so an unset source stays unset until somebody
            // declares one at upload or the server states it.
            if (Languages.Disagree(FileSource, configSource))
            {
                Warning?.Invoke($"[Languages] This translation is written from {FileSource} "
                                + $"and this game was set to {configSource}. The translation "
                                + "decides: the setting follows it.");
                write.Source = FileSource;
                write.ConfigChanged = true;
            }
            else if (!Languages.IsSettled(FileSource)
                     && !everPublished
                     && Languages.IsSettled(configSource))
            {
                FileSource = configSource;
                FileChanged = true;
            }

            return write;
        }

        /// <summary>
        /// Settle the target language the moment this translation acquires its first line.
        ///
        /// 🔴 **"auto" resolves at every read, which is not the same as being settled.** A file
        /// whose configuration still says "auto" aims at whatever language the machine is set to,
        /// so the same file targets French here and German on the next machine — and follows the
        /// player's system language if they ever change it, retargeting lines already written.
        /// A target settles when the first line is written; from then on it is a value, not a mode.
        ///
        /// ⚠ The SOURCE is deliberately not settled here. "auto" there means "detect", which is a
        /// working mode with no resolved value to write; it settles when the person declares one at
        /// upload, or when the server states it (see <see cref="AlignFromServer"/>).
        /// </summary>
        /// <param name="configSource">Carried through untouched; see the note on the pair.</param>
        /// <param name="configTarget">What the configuration holds — "auto" is what brings us here.</param>
        /// <param name="resolvedTarget">What that resolves to right now, on this machine.</param>
        public LanguageWriteBack SettleTargetOnFirstLine(string configSource, string configTarget,
                                                         string resolvedTarget)
        {
            // ⚠ Both sides carried, always, even though only the target can move here. The
            // write-back is applied as a pair, so a half-filled one writes a null over the source.
            var write = new LanguageWriteBack { Source = configSource, Target = configTarget };
            if (Languages.IsSettled(configTarget)) return write;
            if (!Languages.IsSettled(resolvedTarget)) return write;

            write.Target = resolvedTarget;
            write.ConfigChanged = true;

            FileTarget = resolvedTarget;
            Info?.Invoke($"[Languages] Target settled as '{resolvedTarget}' with this translation's "
                         + "first line — it no longer follows the system language.");
            return write;
        }

        /// <summary>
        /// Write back what the server settled, so the three places stop drifting apart.
        ///
        /// 🔴 **The server makes it true, and nothing here may argue.** A lineage's languages are
        /// frozen at publication and the server ignores any sent with an update; a local value that
        /// differs is not an opinion, it is a copy that was never brought up to date. The commonest
        /// case by far is a source left at "auto" — written back only by an upload made from THIS
        /// machine, so every translation somebody downloaded still has none, and the mod has been
        /// asking the model to translate without saying from what.
        ///
        /// ⚠ Called wherever the server answers about this lineage — the sync stream, a download,
        /// an upload — and silent when it has not.
        /// </summary>
        public LanguageWriteBack AlignFromServer(string serverSource, string serverTarget, bool published,
                                                 string configSource, string configTarget)
        {
            var write = new LanguageWriteBack { Source = configSource, Target = configTarget };
            if (!published) return write;

            if (Languages.IsSettled(serverSource)
                && !string.Equals(configSource, serverSource, StringComparison.OrdinalIgnoreCase))
            {
                Info?.Invoke($"[Languages] Source: '{configSource ?? "(unset)"}' → "
                             + $"'{serverSource}', as this translation is published.");
                write.Source = serverSource;
                write.ConfigChanged = true;
            }

            if (Languages.IsSettled(serverTarget)
                && !string.Equals(configTarget, serverTarget, StringComparison.OrdinalIgnoreCase))
            {
                Info?.Invoke($"[Languages] Target: '{configTarget ?? "(unset)"}' → "
                             + $"'{serverTarget}', as this translation is published.");
                write.Target = serverTarget;
                write.ConfigChanged = true;
            }

            // The file says the same thing from now on, so it still knows once it is offline.
            //
            // 🔴 **Only when it says nothing yet.** Overwriting a stated language with the
            // server's would make the file claim to be in a language its LINES are not — and it
            // would erase the one piece of evidence that says so, leaving the upload refusal with
            // nothing to detect. A file that states another language is not this lineage's; that is
            // a fact to surface, not to tidy away.
            if (Languages.IsSettled(serverSource) && !Languages.IsSettled(FileSource))
            {
                FileSource = serverSource;
                FileChanged = true;
            }
            if (Languages.IsSettled(serverTarget) && !Languages.IsSettled(FileTarget))
            {
                FileTarget = serverTarget;
                FileChanged = true;
            }

            NoteConflict(serverSource, serverTarget);
            return write;
        }

        /// <summary>
        /// Compare what the file states with what the lineage was published as, and say so once.
        ///
        /// ⚠ Says nothing when the verdict has not moved: this is reached from the sync stream and
        /// from every server answer, so an unconditional line would repeat for ever.
        /// </summary>
        public void NoteConflict(string serverSource, string serverTarget)
        {
            var side = TranslationLanguages.PublicationConflict(
                FileSource, FileTarget, serverSource, serverTarget);

            string explained = TranslationLanguages.ExplainConflict(side,
                FileSource, FileTarget, serverSource, serverTarget);

            if (explained == Conflict) return;

            Conflict = explained;
            _refusalsSaid = 0;

            if (explained != null)
            {
                Warning?.Invoke($"[Languages] {explained} Nothing new will be translated until "
                                + "this is settled; what the file already holds is still applied.");
            }
            else
            {
                Info?.Invoke("[Languages] This translation matches the lineage it belongs to again.");
            }
        }

        /// <summary>
        /// Whether the refusal is worth saying again.
        ///
        /// ⚠ Three times, then silence. This is asked from the scanner, so an ungated line would be
        /// written on every frame — and a log that repeats one line for ever is a log nobody reads
        /// the rest of. The count starts over whenever the verdict itself moves.
        /// </summary>
        public bool ShouldSayRefusal()
        {
            return _refusalsSaid++ < RefusalsSaid;
        }
    }
}
