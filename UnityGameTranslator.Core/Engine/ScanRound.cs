namespace UnityGameTranslator.Core.Engine
{
    /// <summary>
    /// Has the sweep been all the way round every kind of text component since it last said so?
    ///
    /// 🔴 **Written after the answer was structurally unreachable for three months** (2026-09-10).
    /// The scanner walks each registered type's component list in batches, a few per frame, and one
    /// flag told it two things: when to look the scene up again, and when to forget which components
    /// it had already read this round. That flag was raised only when EVERY type reached the end of
    /// its list **inside the same call** — and a type that reaches its end puts its own cursor back
    /// to zero before saying so, so the next type in the loop is always at zero when the first one
    /// finishes. With two lists longer than a batch, the flag can never be raised again.
    ///
    /// What that cost, measured on a game with 1 138 TMP and 1 294 UI.Text components: after the
    /// first pass of a scene, **every component was refused for the rest of it** — 90 000 visits per
    /// five seconds, all returning at the first line, and 1 531 components each given exactly one
    /// decision and never a second. New text appearing later (a shop tooltip, a panel built on
    /// demand) was never read at all, and the only way through was the manual rescan, which is
    /// exactly what players had been doing.
    ///
    /// 🔴 **The fix is to stop asking whether they all finish together.** Each type raises its own
    /// latch when it wraps; the round is over when every latch is up. Types finishing one after
    /// another — which is what a per-frame budget makes them do — is the normal case, not a failure.
    ///
    /// ⚠ Pure on purpose: no Unity, no clock, no component. The defect above was a counting rule
    /// and nothing else, and a counting rule can be proved wrong from a console — which is what
    /// <c>ScanRoundChecks</c> now does with the very numbers that game showed.
    /// </summary>
    internal sealed class ScanRound
    {
        private bool[] _done = new bool[0];
        private int _count;

        /// <summary>
        /// How many kinds of component the sweep walks. Growing keeps every latch already raised:
        /// a type registered mid-round joins the round rather than restarting it.
        /// </summary>
        internal void Track(int typeCount)
        {
            if (typeCount < 0) typeCount = 0;
            if (typeCount > _done.Length)
            {
                var grown = new bool[typeCount];
                for (int i = 0; i < _done.Length; i++) grown[i] = _done[i];
                _done = grown;
            }
            _count = typeCount;
        }

        /// <summary>This kind has been walked from end to end since the round began.</summary>
        internal void WentRound(int typeIndex)
        {
            if (typeIndex >= 0 && typeIndex < _done.Length) _done[typeIndex] = true;
        }

        /// <summary>
        /// This kind has nothing to walk. It counts as done — a game with no UI.Text at all must
        /// not hold the round open for ever on its behalf.
        /// </summary>
        internal void NothingToSweep(int typeIndex) => WentRound(typeIndex);

        /// <summary>Every kind has been round. The caller may now forget what it read and look again.</summary>
        internal bool Complete
        {
            get
            {
                for (int i = 0; i < _count; i++)
                    if (!_done[i]) return false;
                return true;
            }
        }

        /// <summary>Begin a new round: every latch back down.</summary>
        internal void Restart()
        {
            for (int i = 0; i < _done.Length; i++) _done[i] = false;
        }
    }
}
