using System;
using System.IO;
using System.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The traces a mod folder keeps (Engine/TextTrace). The setting survives a restart, so every
    /// launch starts a trace with a copy of the whole translation beside it; without a limit a
    /// folder fills up launch after launch. Played on real files, since what can go wrong is a
    /// NAME: a trace and its copy that do not go together, or a file that was never ours.
    /// </summary>
    internal static class TextTracePruneChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "ugt-text-trace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                TheOldestGoWithTheirCopies(check, Fresh(root));
                WhatIsNotATraceStays(check, Fresh(root));
                FewerThanKeptIsLeftAlone(check, Fresh(root));
                StartingLeavesKeptTraces(check, Fresh(root));
            }
            finally
            {
                TextTrace.Stop(null);
                try { Directory.Delete(root, recursive: true); }
                catch (IOException ex) { Console.WriteLine($"  (temp folder left behind: {ex.Message})"); }
            }
        }

        private static string Fresh(string root)
        {
            string folder = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void Trace(string folder, string moment, bool withCopy = true)
        {
            File.WriteAllText(Path.Combine(folder, "text-trace-" + moment + ".jsonl"), "{}\n");
            if (withCopy) File.WriteAllText(Path.Combine(folder, "text-trace-" + moment + ".translations.json"), "{}");
        }

        private static string[] Names(string folder) =>
            Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        private static void TheOldestGoWithTheirCopies(Action<bool, string, string> check, string folder)
        {
            Trace(folder, "20260101-100000");
            Trace(folder, "20260102-100000", withCopy: false);
            Trace(folder, "20260103-100000");
            Trace(folder, "20260104-100000");
            // A copy whose trace is gone (deleted by hand) is still a trace of ours, and the oldest.
            File.WriteAllText(Path.Combine(folder, "text-trace-20251231-100000.translations.json"), "{}");

            int gone = TextTrace.Prune(folder, 2);

            check(gone == 3,
                "the oldest traces go until the number kept is left",
                "five traces on disk, two kept: three go");

            string[] left = Names(folder);
            check(left.SequenceEqual(new[]
                  {
                      "text-trace-20260103-100000.jsonl", "text-trace-20260103-100000.translations.json",
                      "text-trace-20260104-100000.jsonl", "text-trace-20260104-100000.translations.json",
                  }),
                "the newest stay, each with its translation copy",
                "a trace replays against the copy beside it: one without the other is useless. Left: " + string.Join(", ", left));
        }

        private static void WhatIsNotATraceStays(Action<bool, string, string> check, string folder)
        {
            File.WriteAllText(Path.Combine(folder, "translations.json"), "{}");
            File.WriteAllText(Path.Combine(folder, "text-trace-notes.txt"), "mine");
            File.WriteAllText(Path.Combine(folder, "text-trace-20200101-000000.jsonl.bak"), "mine");
            // Close to ours and older-looking, yet not a name Start writes: each would sort first.
            File.WriteAllText(Path.Combine(folder, "text-trace-#.jsonl"), "mine");
            File.WriteAllText(Path.Combine(folder, "text-trace-2020010-1000000.jsonl"), "mine");
            File.WriteAllText(Path.Combine(folder, "text-trace-20200101-00000.translations.json"), "mine");
            File.WriteAllText(Path.Combine(folder, "TEXT-TRACE-20200101-000000.JSONL"), "mine");
            File.WriteAllText(Path.Combine(folder, "text-trace-20200101-000000.jsonl.txt"), "mine");
            File.WriteAllText(Path.Combine(folder, "my-text-trace-20200101-000000.jsonl"), "mine");
            // A folder by a trace's name is listed by nobody and deleted by nobody.
            // (Another moment than the names above: Windows names are case-blind.)
            Directory.CreateDirectory(Path.Combine(folder, "text-trace-20190101-000000.jsonl"));
            File.WriteAllText(Path.Combine(folder, "text-trace-20190101-000000.jsonl", "inside.txt"), "mine");
            Trace(folder, "20260101-100000");

            int gone = TextTrace.Prune(folder, 0);

            string[] left = Names(folder);
            check(gone == 1 && left.SequenceEqual(new[]
                  {
                      "TEXT-TRACE-20200101-000000.JSONL", "my-text-trace-20200101-000000.jsonl",
                      "text-trace-#.jsonl", "text-trace-2020010-1000000.jsonl",
                      "text-trace-20200101-00000.translations.json",
                      "text-trace-20200101-000000.jsonl.bak", "text-trace-20200101-000000.jsonl.txt",
                      "text-trace-notes.txt", "translations.json",
                  }, StringComparer.Ordinal),
                "only a name the mod writes is ever deleted",
                "prefix, moment as Start spells it, suffix, exact case; anything else in the folder is somebody's. Left: " + string.Join(", ", left));

            check(File.Exists(Path.Combine(folder, "text-trace-20190101-000000.jsonl", "inside.txt")),
                "a folder named like a trace is not entered",
                "the listing is one level, files only");
        }

        private static void FewerThanKeptIsLeftAlone(Action<bool, string, string> check, string folder)
        {
            Trace(folder, "20260101-100000");

            check(TextTrace.Prune(folder, 2) == 0 && Names(folder).Length == 2,
                "fewer traces than kept: nothing goes",
                "the limit is a ceiling, not a target");

            check(TextTrace.Prune(Path.Combine(folder, "missing"), 2) == 0,
                "a folder that does not exist has nothing to prune",
                "the first trace of a game is started before anything was written there");
        }

        private static void StartingLeavesKeptTraces(Action<bool, string, string> check, string folder)
        {
            string translations = Path.Combine(folder, "translations.json");
            File.WriteAllText(translations, "{}");
            for (int day = 1; day <= TextTrace.Kept + 2; day++) Trace(folder, $"202001{day:00}-100000");

            TextTrace.Start(folder, translations, "fr", () => -1, () => -1, null);
            string started = TextTrace.Path;
            TextTrace.Stop(null);

            string[] traces = Directory.GetFiles(folder, "text-trace-*.jsonl");
            check(traces.Length == TextTrace.Kept,
                $"after a start the folder holds {TextTrace.Kept} traces, the new one included",
                $"older ones are cleared when a trace starts, not when it stops — found {traces.Length}");

            check(started != null && File.Exists(started) && File.Exists(Path.ChangeExtension(started, null) + ".translations.json"),
                "the trace just started is among them, with its copy",
                "pruning happens before the new file exists, so it can never take it");
        }
    }
}
