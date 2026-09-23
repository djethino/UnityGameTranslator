using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The failed lines beside the translation, across a launch: written when they fail, read
    /// back before the queue asks anything, gone when the last is settled. Replayed on a real
    /// folder — the moment a file exists or does not is the whole point.
    /// </summary>
    internal static class FailureStoreChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string folder = Path.Combine(Path.GetTempPath(), "ugt-failures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string translation = Path.Combine(folder, "translations.json");
                var store = new FailureStore(translation);
                check(store.Path == translation + ".failures", "the file sits beside the translation, by its name", "a companion the uninstall sweep and the eye both find");

                var empty = new FailureLedger();
                check(store.Load(empty) == 0 && empty.Count == 0, "no file reads as nothing", "a first launch has nothing to skip");

                var ledger = new FailureLedger();
                ledger.Note(new FailedLine
                {
                    Key = "Version [!v*0]",
                    Source = "Version {0}",
                    Attempts = new List<FailedAttempt>
                    {
                        new FailedAttempt { Value = "Version", Errors = new List<string> { "token [!v*0] appears 0 time(s) instead of 1" } },
                        new FailedAttempt { Value = "Version [!v*0] [!v*0]", Errors = new List<string> { "token [!v*0] appears 2 time(s) instead of 1" } },
                    },
                });
                ledger.AttachElements("Version [!v*0]", new[] { "Canvas/Notes/Body" });
                ledger.Note(new FailedLine { Key = "Chat", Source = "Chat", Attempts = new List<FailedAttempt> { new FailedAttempt { Value = "Tchat [!nl]", Errors = new List<string> { "token [!nl] appears 1 time(s) instead of 0" } } } });
                store.Save(ledger);
                check(File.Exists(store.Path) && !File.Exists(store.Path + AtomicFile.TempSuffix), "a ledger with lines is written, whole", "the same rule as the translation: a crash mid-write must not leave half a file");

                // The next launch: read back, every detail that settling needs.
                var back = new FailureLedger();
                int read = store.Load(back);
                var first = back.All.FirstOrDefault(l => l.Key == "Version [!v*0]");
                check(read == 2 && back.Count == 2 && first != null,
                      "read back, both lines", "what was refused yesterday is refused today without asking");
                check(first != null && first.Source == "Version {0}" && first.Attempts.Count == 2
                      && first.Attempts[1].Value == "Version [!v*0] [!v*0]"
                      && first.Attempts[1].Errors.SequenceEqual(new[] { "token [!v*0] appears 2 time(s) instead of 1" })
                      && first.Elements.SequenceEqual(new[] { "Canvas/Notes/Body" }),
                      "with the proposals, their errors and the elements", "the tab settles a line from these, so none of them may be lost in the file");
                check(back.All[0].Key == "Version [!v*0]" && back.All[1].Key == "Chat", "in the order they failed", "the list is worked through top to bottom, launch after launch");

                // Settled against what the file now holds: a key translated since — downloaded,
                // restored, written by hand — leaves the record.
                var translated = new HashSet<string> { "Chat" };
                int settled = back.Settle(key => translated.Contains(key));
                check(settled == 1 && back.Count == 1 && !back.Holds("Chat"), "a line the file now translates is settled at load", "a download or a restored backup may have brought the line; asking would be a lie and skipping it a loss");

                store.Save(back);
                var again = new FailureLedger();
                check(store.Load(again) == 1 && again.Holds("Version [!v*0]"), "the reconciled ledger is what the next launch reads", "otherwise the settled line comes back at every launch");

                again.Clear();
                store.Save(again);
                check(!File.Exists(store.Path), "an empty ledger deletes the file", "nothing left to skip: a folder listing shows no failures, because there are none");

                File.WriteAllText(store.Path, "{ not json");
                bool refused = false;
                try { store.Load(new FailureLedger()); } catch (Exception) { refused = true; }
                check(refused, "a file that is not ours is refused, not read as empty", "the caller logs it and goes on; silently reading nothing would drop every record without a word");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { /* a temp folder left behind proves nothing */ }
            }
        }
    }
}
