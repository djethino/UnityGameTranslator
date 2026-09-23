using System;
using System.Collections.Generic;
using System.Linq;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The session's failed lines: one per key, in failure order, settled one by one. What used
    /// to be three warnings in the log and a text refused until the next launch.
    /// </summary>
    internal static class FailureLedgerChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            var ledger = new FailureLedger();
            int changes = 0;
            ledger.Changed += () => changes++;

            ledger.Note(Failed("Play [!v*0]", "Play {0}", "Jouer", "token [!v*0] appears 0 time(s) instead of 1"));
            ledger.Note(Failed("Quit", "Quit", "Quitter [!nl]", "token [!nl] appears 1 time(s) instead of 0"));
            check(ledger.Count == 2 && ledger.All[0].Key == "Play [!v*0]",
                  "failures are kept in the order they happened", "the list is worked through top to bottom");
            check(changes == 2, "each note says so", "the screens redraw on the event, not by polling");

            // The same line failing again replaces its record — attempts of the new failure, the
            // place of the old one — and keeps what the host had attached.
            ledger.AttachElements("Play [!v*0]", new[] { "Canvas/Menu/PlayLabel" });
            ledger.Note(Failed("Play [!v*0]", "Play {0}", "Jouer encore", "token [!v*0] appears 0 time(s) instead of 1"));
            check(ledger.Count == 2 && ledger.All[0].Attempts[0].Value == "Jouer encore",
                  "a line failing again is replaced, in place", "two records for one line would be settled twice");
            check(ledger.All[0].Elements.SequenceEqual(new[] { "Canvas/Menu/PlayLabel" }),
                  "and keeps the elements attached to it", "the element is what the exclusion needs, and the worker never knows it");

            int before = changes;
            ledger.AttachElements("Play [!v*0]", new[] { "Canvas/Menu/PlayLabel", "", null });
            check(changes == before, "attaching nothing new is silent", "every scan of the same screen would otherwise redraw the list");
            ledger.AttachElements("Nobody", new[] { "Canvas/X" });
            check(ledger.Count == 2 && changes == before, "an element for an unknown line is dropped", "only a failure has elements worth keeping");

            check(ledger.Remove("Quit") && ledger.Count == 1 && !ledger.Holds("Quit"),
                  "a settled line leaves the list", "saved, skipped, excluded or translated after all: it is no longer to look at");
            check(!ledger.Remove("Quit"), "settling it twice changes nothing", "a stale button must not raise an event");

            var snapshot = ledger.All;
            snapshot.Clear();
            check(ledger.Count == 1, "the list handed out is a copy", "a screen clearing its rows must not empty the ledger");

            ledger.Note(Failed("Menu", "Menu", "Menü [!nl]", "token [!nl] appears 1 time(s) instead of 0"));
            ledger.Note(Failed("Options", "Options", "Optionen [!nl]", "token [!nl] appears 1 time(s) instead of 0"));
            before = changes;
            int settled = ledger.Settle(key => key == "Menu" || key == "Nobody");
            check(settled == 1 && !ledger.Holds("Menu") && ledger.Holds("Options") && changes == before + 1,
                  "settling against the file drops the lines it now holds, in one event", "the reconciliation at load: a key translated since is no failure any more");
            check(ledger.Settle(_ => false) == 0 && changes == before + 1, "settling nothing is silent", "a launch with nothing changed must not rewrite the file");

            ledger.Clear();
            check(ledger.Count == 0, "clear empties it", "a new session starts with nothing to settle");
        }

        private static FailedLine Failed(string key, string source, string value, string error)
            => new FailedLine
            {
                Key = key,
                Source = source,
                Attempts = new List<FailedAttempt> { new FailedAttempt { Value = value, Errors = new List<string> { error } } },
            };
    }
}
