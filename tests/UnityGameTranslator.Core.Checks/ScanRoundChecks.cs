using System;
using System.Collections.Generic;
using System.IO;
using UnityGameTranslator.Core.Engine;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// When the sweep has been all the way round — the counting rule that was unreachable.
    ///
    /// 🔴 **The defect this exists for, in one sentence**: the scanner asked whether every kind of
    /// text component finished its list *in the same call*, and a kind that finishes puts its own
    /// cursor back to zero before saying so — so the next kind is always at zero, and with two lists
    /// longer than one batch the answer is never yes. The flag that answer raised did two jobs:
    /// look the scene up again, and forget which components had already been read. Both stopped
    /// after the first pass of a scene, in silence, on every large game.
    ///
    /// ⚠ **The simulation below is the whole point.** A case on <see cref="ScanRound"/> alone would
    /// only prove that latches latch. What went wrong was the SHAPE of the loop around them — one
    /// kind advancing per frame, each resetting on completion — so the check replays that shape with
    /// the numbers the game actually showed (1 138 and 1 294 components, 200 per batch) and demands
    /// that a round end. Against the old rule it does not, which is what makes this able to fail.
    ///
    /// ⚠ And the lexical half, for the same reason as <see cref="LiveCountChecks"/>: the loop itself
    /// lives in the scanner, welded to Unity. What is checkable from here is that it still asks the
    /// round, and still ties forgetting to the round rather than to a single call.
    /// </summary>
    internal static class ScanRoundChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            var round = new ScanRound();
            round.Track(2);

            check(!round.Complete,
                "a fresh round is not complete",
                "starting complete would clear the dedup every frame, so a component would be re-read on every pass");

            round.WentRound(0);
            check(!round.Complete,
                "one kind out of two is not a round",
                "the dedup spans the round: forgetting halfway lets the same component be read twice in one pass");

            round.WentRound(1);
            check(round.Complete,
                "both kinds, each in its own frame, IS a round",
                "🔴 the defect: kinds finishing one after another is what a per-frame budget makes them do, and it was read as failure");

            round.Restart();
            check(!round.Complete,
                "and restarting puts every latch back down",
                "a round that stays complete never spans anything");

            // A kind with nothing in it must not hold the round open: plenty of games carry no
            // UI.Text at all, and one empty list would have frozen the sweep for ever.
            var empty = new ScanRound();
            empty.Track(3);
            empty.WentRound(0);
            empty.NothingToSweep(1);
            empty.WentRound(2);
            check(empty.Complete,
                "a kind with nothing to sweep counts as swept",
                "a game with no UI.Text would hold its round open for ever, which is the same freeze one step along");

            // A kind registered mid-round joins it rather than restarting it: the ones already
            // round stay round, and only the newcomer is waited for.
            var grown = new ScanRound();
            grown.Track(1);
            grown.WentRound(0);
            check(grown.Complete, "one kind, gone round, is a round", "otherwise a single-type game never completes one");
            grown.Track(2);
            check(!grown.Complete,
                "a kind registered mid-round is waited for",
                "counting it as done would let the sweep forget everything before that kind had been read once");
            grown.WentRound(1);
            check(grown.Complete, "and the round ends when it too has been round", "");

            SweepingTwoKindsOneBatchAtATime(check);
            TheScannerStillAsksTheRound(check);
        }

        /// <summary>
        /// Replay the sweep as the scanner runs it: one kind advances per frame, a kind that reaches
        /// the end of its list resets to zero, and the next kind starts from wherever it was.
        ///
        /// 🔴 With the numbers from the game that showed the defect. The old rule — "every kind
        /// finished in THIS frame" — never fires here; the round does, and that difference is the
        /// whole fix.
        /// </summary>
        private static void SweepingTwoKindsOneBatchAtATime(Action<bool, string, string> check)
        {
            const int batch = 200;
            int[] total = { 1138, 1294 };   // TMP_Text, UI.Text — measured, 2026-09-10
            int[] cursor = { 0, 0 };

            var round = new ScanRound();
            round.Track(total.Length);

            int start = 0;
            int roundsFinished = 0;
            bool everyKindFinishedInOneFrame = false;

            for (int frame = 0; frame < 200 && roundsFinished < 2; frame++)
            {
                int finishedThisFrame = 0;
                for (int step = 0; step < total.Length; step++)
                {
                    int index = (start + step) % total.Length;
                    int end = Math.Min(cursor[index] + batch, total[index]);
                    bool wrapped = end >= total[index];
                    cursor[index] = wrapped ? 0 : end;

                    if (!wrapped) { start = index; break; }   // budget spent on an unfinished kind

                    round.WentRound(index);
                    finishedThisFrame++;
                }
                if (finishedThisFrame == total.Length) everyKindFinishedInOneFrame = true;

                if (round.Complete)
                {
                    roundsFinished++;
                    round.Restart();
                    start = 0;
                }
            }

            check(roundsFinished >= 2,
                "sweeping 1 138 and 1 294 components 200 at a time completes rounds, one after another",
                "🔴 this is the defect, in its own numbers: with the old rule the sweep ran for a whole scene and never once said it had been round");

            check(!everyKindFinishedInOneFrame,
                "and no frame ever sees both kinds finish at once",
                "which is exactly why the old condition could not fire: a kind resets its cursor as it finishes, so the next one is always at the start");
        }

        /// <summary>
        /// The loop lives in the scanner, welded to Unity. What is readable from here is that it
        /// still asks the round, and that forgetting what it read is still tied to the round.
        /// </summary>
        private static void TheScannerStillAsksTheRound(Action<bool, string, string> check)
        {
            string file = Find("UnityGameTranslator.Core", "TranslatorScanner.cs");
            check(file != null, "the scanner is found", "this half reads it; without it, it proves nothing");
            if (file == null) return;

            string text = File.ReadAllText(file);

            check(text.Contains("scanCycleComplete = _round.Complete", StringComparison.Ordinal),
                "the sweep decides it has been round from the round, not from one call",
                "the old rule was a local `allDone` raised only when every kind finished inside the same call");

            check(text.Contains("_processedThisCycle.Clear(); _round.Restart();", StringComparison.Ordinal),
                "and what it forgets, it forgets with the round",
                "🔴 the half that actually cost translations: never clearing it refused every component already seen, for the rest of the scene");

            check(text.Contains("_round.NothingToSweep(index)", StringComparison.Ordinal),
                "a kind with nothing in it is counted, not skipped in silence",
                "left uncounted, a game with no UI.Text holds its round open for ever");

            check(text.Contains("if (step > 0 && _scanFrameSw.Elapsed.TotalMilliseconds > budgetMs)", StringComparison.Ordinal),
                "the frame's first kind always gets a turn",
                "a frame whose budget was spent before the sweep started advanced nothing, and a round that can stall is the defect again");
        }

        private static string Find(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var segments = new List<string> { dir.FullName };
                segments.AddRange(parts);
                string candidate = Path.Combine(segments.ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
