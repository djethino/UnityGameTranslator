namespace UnityGameTranslator.Core.Engine
{
    /// <summary>
    /// When the scanner searches the scene for text components it does not already hold.
    ///
    /// 🔴 **The whole subject is that looking is expensive and not looking is unbounded.** One
    /// atomic engine call per component type, measured at 0.1 ms in a menu and ~30 ms in a large
    /// city — so the version that looked every cycle was felt as a hitch per second, and the one
    /// that replaced it looks only when a component announces its arrival through
    /// <c>Graphic.OnEnable</c>.
    ///
    /// 🔴 **An optimisation gated on an event must not be able to wait for it for ever.** That
    /// second version could: a family that never announces itself is never found, for the rest of
    /// the session, with every counter reporting healthy work — text still flowing through the
    /// gate, the scanner still busy, and nothing new ever discovered. This project has paid for
    /// that shape often enough to name it: reconcile from the real state, never from transitions
    /// alone.
    ///
    /// ⚠ **A floor, not a diagnosis** (2026-09-09). A session was observed with no lookup for its
    /// last thirty seconds while the player was meeting new screens; WHICH announcement failed to
    /// fire was never established — a subclass overriding OnEnable without calling base, a family
    /// that does not descend from Graphic, a pooled object reused without re-enabling. It does not
    /// need to be. After this none of them can stop discovery, only delay it by the interval.
    ///
    /// ⚠ Pure by contract: no Unity, no state, no clock of its own — the caller hands the time in.
    /// That is what lets the whole rule be replayed in the check corpus rather than argued about.
    /// </summary>
    public static class ScanCadence
    {
        /// <summary>
        /// How long discovery may go without looking, whatever the announcements say.
        ///
        /// ⚠ The cost is the avoided call divided by this interval: a few milliseconds per second
        /// in the worst scene measured, against a hitch per second before. And it turns a stall
        /// that lasted a session into one that lasts ten seconds.
        /// </summary>
        public const float LookUpAtLeastEverySeconds = 10f;

        /// <summary>
        /// Whether this refresh cycle searches the scene, or trusts the components it already holds.
        /// </summary>
        /// <param name="announcementsHooked">The arrival hook is in place. Without it every cycle looks, as it always did.</param>
        /// <param name="somethingAppeared">A component announced itself since the last cycle.</param>
        /// <param name="now">Seconds since the game started.</param>
        /// <param name="lastLookUpAt">When a cycle last searched, on the same clock.</param>
        public static bool ShouldLookUp(bool announcementsHooked, bool somethingAppeared,
                                        float now, float lastLookUpAt)
        {
            if (!announcementsHooked) return true;
            if (somethingAppeared) return true;

            return now - lastLookUpAt >= LookUpAtLeastEverySeconds;
        }
    }
}
