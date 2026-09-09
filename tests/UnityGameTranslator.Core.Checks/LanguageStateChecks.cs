using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Which languages a translation is in, across the launch that decides it.
    ///
    /// 🔴 **Three sources speak at different moments, and that is the whole subject.** The file is
    /// read at launch with no network. The machine's setting is already there. The server answers a
    /// second later, or never. Which value wins is settled in the socle and checked there; what is
    /// checked HERE is when each one speaks and what is written back.
    ///
    /// 🔴 **Every defect this can carry is a right answer taken at the wrong time.** A machine's
    /// setting frozen into a file before the server said otherwise. A target adopted from "auto"
    /// and then following the player's system language onto the next machine, retargeting lines
    /// already written. A restored backup quietly growing a second language inside one file. None
    /// of them is a wrong answer to a question asked once.
    ///
    /// ⚠ The log lines are read here on purpose. They are not diagnostics: they are the only place
    /// a player is told that their setting just changed under them, and why.
    /// </summary>
    internal static class LanguageStateChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            WhatTheFileStates(check);
            WhenTheFileAndTheMachineDisagree(check);
            WhenAutoBecomesAValue(check);
            WhenTheConfigurationAlreadyNamesThem(check);
            WhenTheServerAnswers(check);
            WhenTheyCannotBeReconciled(check);
            WhenTheyAreLocked(check);
        }

        private static LanguageState Fresh(List<string> said = null)
        {
            var state = new LanguageState();
            if (said != null)
            {
                state.Info = said.Add;
                state.Warning = said.Add;
            }
            return state;
        }

        private static void WhatTheFileStates(Action<bool, string, string> check)
        {
            var state = Fresh();

            check(state.FileSource == null && state.FileTarget == null,
                "a file that states nothing states nothing",
                "null means 'this file does not say', never 'this file has no language' — most files predate the stamp");

            state.StateTarget("auto");
            check(state.FileTarget == null,
                "and 'auto' is not a statement",
                "it is a working mode: it resolves differently on the next machine, so writing it down settles nothing");

            state.StateTarget("fr");
            check(state.FileTarget == "fr",
                "a real language is",
                "this is what survives a copy to another machine and a mod that never reads the configuration");

            state.Reset();
            check(state.FileTarget == null && state.Conflict == null,
                "and a new file starts over",
                "a reload may land on a file stating nothing; keeping the last one lets a restored backup inherit languages that are not its own");
        }

        private static void WhenTheFileAndTheMachineDisagree(Action<bool, string, string> check)
        {
            // 🔴 The restored-backup case, and the reason this is settled before the first line is
            // translated: the contradiction is visible the instant the file is read.
            var said = new List<string>();
            var state = Fresh(said);
            state.StateTarget("th");

            var write = state.SettleFromFile("en", "fr", lineCount: 300, everPublished: false);

            check(write.ConfigChanged && write.Target == "th",
                "the file decides and the machine's setting follows it",
                "the alternative writes the next lines in French into a file of Thai ones, and nothing says so");

            check(write.Source == "en",
                "and the side that did not move is carried through untouched",
                "a write-back applied as a pair with one half missing writes a null over the other");

            check(said.Count == 1 && said[0].Contains("th") && said[0].Contains("fr"),
                "with both languages named",
                "somebody's setting just changed under them; 'languages adjusted' would leave them guessing which way");

            // A file that says nothing takes the machine's answer — the only one available.
            var adopting = Fresh();
            var adopted = adopting.SettleFromFile("en", "fr", lineCount: 300, everPublished: false);

            check(adopting.FileTarget == "fr" && adopting.FileChanged && !adopted.ConfigChanged,
                "a file that states nothing adopts the machine's setting",
                "it is the only answer available, and leaving it unstated means the next machine decides again");

            // 🔴 ...but only while nobody has published. Where they have, the server knows.
            var published = Fresh();
            published.SettleFromFile("en", "fr", lineCount: 300, everPublished: true);

            check(published.FileTarget == null && !published.FileChanged,
                "unless this lineage was published, where it waits for the server",
                "guessing first freezes a wrong answer into the file a second before the truth arrives");

            var empty = Fresh();
            empty.SettleFromFile("en", "fr", lineCount: 0, everPublished: false);

            check(empty.FileTarget == null,
                "and a file with no lines states nothing yet",
                "there is nothing to be in a language; the first line is what settles it");
        }

        private static void WhenAutoBecomesAValue(Action<bool, string, string> check)
        {
            var said = new List<string>();
            var state = Fresh(said);

            var write = state.SettleTargetOnFirstLine("en", "auto", "de");

            check(write.ConfigChanged && write.Target == "de" && state.FileTarget == "de",
                "the first line turns 'auto' into a value",
                "left as a mode it aims at whatever machine reads it, and follows the player's system language afterwards");

            check(write.Source == "en",
                "and the source is carried through, not settled",
                "'auto' on the source means DETECT — a working mode with no resolved value to write down");

            check(said.Count == 1 && said[0].Contains("de"),
                "said once, naming the language it settled on",
                "it is a change the player did not ask for and would otherwise never learn about");

            var already = Fresh();
            var untouched = already.SettleTargetOnFirstLine("en", "fr", "de");
            check(!untouched.ConfigChanged && already.FileTarget == null,
                "a target already settled is not re-settled",
                "the machine's current answer must never overwrite a value somebody or the server chose");

            var unresolvable = Fresh();
            var nothing = unresolvable.SettleTargetOnFirstLine("en", "auto", "auto");
            check(!nothing.ConfigChanged,
                "and 'auto' that resolves to nothing settles nothing",
                "writing 'auto' into the file would look like a decision while being the absence of one");
        }

        /// <summary>
        /// A brand-new translation, on a machine whose configuration already names both languages.
        ///
        /// 🔴 **The case nobody covered, and it shipped** (found on a real install, 2026-09-09): a
        /// fresh file whose configuration said English → French carried neither
        /// <c>_source_language</c> nor <c>_target_language</c>, for the whole of its life. Each of
        /// the two rules is right on its own, and between them the file falls through:
        /// <see cref="LanguageState.SettleTargetOnFirstLine"/> has nothing to resolve when the
        /// configuration already names a language, and <see cref="LanguageState.SettleFromFile"/>
        /// refuses while the file has no line — which is what a new translation is.
        ///
        /// ⚠ **So the case is the PAIR, in the order the mod calls it**, and neither call proves
        /// anything alone. That is the whole shape of this file: a right answer at the wrong
        /// moment, or in this case a right answer nobody asked for a second time.
        /// </summary>
        private static void WhenTheConfigurationAlreadyNamesThem(Action<bool, string, string> check)
        {
            // ── A new file: no line, nothing stated, never published. ──
            var fresh = Fresh();

            fresh.SettleTargetOnFirstLine("English", "French", "French");
            check(fresh.FileTarget == null,
                "resolving alone states nothing when there was nothing to resolve",
                "the configuration already named the language, so that rule has no work — this is the half that existed");

            // The line has landed by now, which is what the second rule was waiting for.
            fresh.SettleFromFile("English", "French", lineCount: 1, everPublished: false);

            check(fresh.FileTarget == "French",
                "🔴 and asking again once the line has landed states the target",
                "a file that does not say what language it is works only on the machine that made it: the configuration answers in its place, and nowhere else");
            check(fresh.FileSource == "English",
                "and the source with it",
                "the prompt is built from it; a downloaded copy stating none has the model translating from nothing stated");
            check(fresh.FileChanged,
                "and the file is marked to be written",
                "settled in memory and not on disk is the same as not settled, one launch later");

            // ── The guard that must survive the fix. ──
            var published = Fresh();
            published.SettleTargetOnFirstLine("English", "French", "French");
            published.SettleFromFile("English", "French", lineCount: 1, everPublished: true);

            check(published.FileTarget == null && published.FileSource == null,
                "⚠ but a published lineage still states nothing from this machine",
                "the server keeps the languages a translation was published with; guessing first would freeze a wrong answer before the truth arrives");

            // ── And 'auto' still resolves first, so what is written is the value. ──
            var auto = Fresh();
            var write = auto.SettleTargetOnFirstLine("English", "auto", "German");
            auto.SettleFromFile(write.Source, write.Target, lineCount: 1, everPublished: false);

            check(auto.FileTarget == "German",
                "and 'auto' is written as the value it resolved to, never as the mode",
                "the two calls compose in that order for exactly this reason");

            // 🔴 And that the mod actually MAKES the pair. Everything above is a rule composing
            // correctly; none of it says the engine asks twice, and asking once is what shipped.
            string core = FindCore();
            check(core != null, "TranslatorCore's source is found",
                "this case reads it; without it, it proves nothing");
            if (core == null) return;

            string body = BodyOf(System.IO.File.ReadAllText(core),
                                 "private static void SettleTargetLanguageOnFirstLine()");
            check(body != null, "and the first line's handler is still there under its own name",
                "renamed, the case must say so rather than pass on an empty comparison");
            if (body == null) return;

            check(body.Contains("SettleTargetOnFirstLine(", StringComparison.Ordinal)
                  && body.Contains("SettleLanguagesFromFile()", StringComparison.Ordinal),
                "the first line resolves AND states, in that order",
                "resolving alone is what left a fresh translation never saying what language it is in");
        }

        /// <summary>The body of a method, by counting braces from its signature.</summary>
        private static string BodyOf(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return null;

            int open = text.IndexOf('{', start + signature.Length);
            if (open < 0) return null;

            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(open, i - open + 1);
                }
            }
            return null;
        }

        private static string FindCore()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, "UnityGameTranslator.Core", "TranslatorCore.cs");
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        private static void WhenTheServerAnswers(Action<bool, string, string> check)
        {
            var said = new List<string>();
            var state = Fresh(said);

            var write = state.AlignFromServer("en", "fr", published: true, configSource: "auto", configTarget: "fr");

            check(write.ConfigChanged && write.Source == "en" && write.Target == "fr",
                "the server settles what the machine left at 'auto'",
                "a downloaded translation has no source, so the mod was asking a model to translate without saying from what");

            check(state.FileSource == "en" && state.FileChanged,
                "and the file states it from then on",
                "so it still knows once it is offline, on a machine that never asks the server again");

            // 🔴 Only where the file says nothing. A file that states another language is EVIDENCE.
            var stated = Fresh();
            stated.StateTarget("th");
            stated.AlignFromServer("en", "fr", published: true, configSource: "en", configTarget: "th");

            check(stated.FileTarget == "th",
                "a language the file already states is never overwritten",
                "it is the one piece of evidence that the file is not this lineage's, and tidying it away hides the fact");

            var unpublished = Fresh();
            var quiet = unpublished.AlignFromServer("en", "fr", published: false, configSource: "auto", configTarget: "auto");

            check(!quiet.ConfigChanged && unpublished.FileSource == null,
                "an unpublished lineage is left entirely alone",
                "there is no published truth to align to, and the server said nothing about it");
        }

        private static void WhenTheyCannotBeReconciled(Action<bool, string, string> check)
        {
            var said = new List<string>();
            var state = Fresh(said);
            state.StateTarget("th");
            state.NoteConflict("en", "fr");

            check(state.Conflict != null,
                "a file in one language inside a lineage published in another is a conflict",
                "writing more in the file's language grows what can never be published; writing in the lineage's mixes two into one file");

            int afterFirst = said.Count;
            state.NoteConflict("en", "fr");

            check(said.Count == afterFirst,
                "and it is said once, not on every server answer",
                "this is reached from the sync stream and from every download; an unconditional line repeats for ever");

            check(state.ShouldSayRefusal() && state.ShouldSayRefusal() && state.ShouldSayRefusal()
                  && !state.ShouldSayRefusal(),
                "the refusal itself is said three times and then not again",
                "it is asked from the scanner, so ungated it would be written on every frame");

            state.NoteConflict("en", "th");
            check(state.Conflict == null && state.ShouldSayRefusal(),
                "settling it says so, and the count starts over",
                "'nothing is being translated' has to be revocable, or the mod stays silently stopped after the fix");

            var agreeing = Fresh();
            agreeing.StateTarget("fr");
            agreeing.NoteConflict("en", "fr");
            check(agreeing.Conflict == null,
                "a file that matches its lineage raises nothing",
                "the overwhelmingly common case, and it must cost neither a line nor a refusal");

            var silent = Fresh();
            silent.NoteConflict("en", "fr");
            check(silent.Conflict == null,
                "and a file that states nothing cannot contradict anything",
                "most files predate the stamp; treating 'does not say' as 'says otherwise' would stop them all");
        }

        private static void WhenTheyAreLocked(Action<bool, string, string> check)
        {
            check(!LanguageState.Locked(published: false, lineCount: 0),
                "an empty unpublished translation may still choose",
                "nothing has been written in any language yet, so nothing would be orphaned");

            check(LanguageState.Locked(published: true, lineCount: 0),
                "a published one may not",
                "the server keeps the languages a lineage was published with and ignores any sent with an update");

            check(LanguageState.Locked(published: false, lineCount: 1),
                "and neither may one that already holds a line",
                "retargeting leaves every existing line in a language the game no longer asks for, with the next captures in the new one");
        }
    }
}
