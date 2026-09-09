using System;
using UnityGameTranslator.Core.Engine;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// When the scanner searches the scene for components it does not already hold.
    ///
    /// 🔴 **One rule, one failure mode, and it is this project's recurring one.** Looking is
    /// expensive — one atomic engine call per component type, 0.1 ms in a menu and ~30 ms in a
    /// large city — so discovery was gated on components announcing their arrival. An optimisation
    /// gated on an event can wait for it for ever: a family that never announces itself is never
    /// found, for the rest of the session, while every counter reports healthy work.
    ///
    /// ⚠ **The floor is the case that matters**, and it is the only one that could not be stated
    /// before this rule was a file of its own. The others pin what the floor must not break.
    /// </summary>
    internal static class ScanCadenceChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            const float floor = ScanCadence.LookUpAtLeastEverySeconds;

            check(floor > 0f,
                $"the floor is {floor:F0} s, and it is a real interval",
                "zero would look every cycle, which is the hitch this replaced; a negative one never looks");

            // ── Without the hook, nothing has changed: every cycle looks. ──
            check(ScanCadence.ShouldLookUp(announcementsHooked: false, somethingAppeared: false,
                                           now: 100f, lastLookUpAt: 99.9f),
                "with no arrival hook every cycle searches",
                "that is what the mod did before the hook existed, and a game where the patch fails must not lose discovery");

            // ── With the hook: an announcement is enough, at any moment. ──
            check(ScanCadence.ShouldLookUp(true, somethingAppeared: true, now: 100f, lastLookUpAt: 99.99f),
                "an announcement searches straight away",
                "a panel opening must not wait out the floor to be translated");

            // ── Nothing announced and nothing due: trust what is held. ──
            check(!ScanCadence.ShouldLookUp(true, false, now: 100f, lastLookUpAt: 99f),
                "nothing announced and nothing overdue searches nothing",
                "this is the optimisation itself — searching every cycle was felt as one hitch per second");

            // ── 🔴 The floor. ──
            check(ScanCadence.ShouldLookUp(true, somethingAppeared: false,
                                           now: 100f, lastLookUpAt: 100f - floor),
                "🔴 but a cycle searches once the floor is reached, announcement or not",
                "an optimisation gated on an event must not be able to wait for it for ever: whichever announcement fails to fire, discovery is delayed by the floor and never stopped");

            check(ScanCadence.ShouldLookUp(true, false, now: 1000f, lastLookUpAt: 100f),
                "and long past it, still",
                "the answer must not depend on how far past due it is; a stall of ten minutes is the same defect as one of ten seconds");

            check(!ScanCadence.ShouldLookUp(true, false, now: 100f, lastLookUpAt: 100f - floor + 0.01f),
                "one hundredth of a second short of the floor, it does not",
                "the floor is a boundary and a rule with a soft boundary is a rule nobody can predict");

            // ── The first cycle of all: nothing has ever been looked up. ──
            check(ScanCadence.ShouldLookUp(true, false, now: 0f, lastLookUpAt: float.NegativeInfinity),
                "the first cycle searches, having never searched",
                "a session that starts with no announcement — everything already on screen — must not begin blind");

            // ⚠ A clock that goes backwards is not a scenario here: realtimeSinceStartup only
            // grows. Stated so nobody adds a guard for it and calls that a fix.
            check(!ScanCadence.ShouldLookUp(true, false, now: 100f, lastLookUpAt: 100f),
                "and a cycle that has just searched does not search again",
                "the floor is measured from the last search, so back-to-back cycles cost one lookup, not two");
        }
    }
}
