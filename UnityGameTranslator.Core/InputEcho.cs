namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Whether a text shown somewhere on screen could be an echo of what the player is typing,
    /// rather than something the game is saying.
    ///
    /// 🔴 **The hard part of input protection lives here.** Catching the box being typed IN is
    /// structural and certain — an InputField ancestor in uGUI, the input element's own name in UI
    /// Toolkit — and needs none of this. What needs it is the ECHO: a header showing the character
    /// name as it is entered, a preview beside a search field. The only thing linking those to the
    /// keyboard is that the strings are equal, and two equal strings at the same instant are
    /// indistinguishable.
    ///
    /// So the question is never "is this typed text" — it cannot be answered — but "could it be",
    /// asked so that the ordinary case answers no:
    ///
    /// 1. **A string this game has already shown us is the game's.** If it is a key we hold, it
    ///    reached us before anyone typed anything.
    /// 2. **Only just after a keystroke.** Typing is a burst; a coincidence is not.
    ///
    /// ⚠ Guard 1 does not endanger the box itself. "Play" typed into a search field would be a
    /// known key and pass this guard — and never reaches it, because the structural check caught it
    /// first, without comparing any content. The two protections are not interchangeable and the
    /// weaker one must not be asked to do the stronger one's job.
    ///
    /// ⚠ What still slips through, stated rather than hidden: a label the game has NEVER shown
    /// before, equal to what was typed less than a second ago. It stays in the source language for
    /// that second. The caller treats this as a transient skip and never as an exclusion, so the
    /// next scan takes it.
    ///
    /// 🔴 Pure by contract — no Unity, no clock, no cache. The caller reads the clock and the
    /// cache; this only decides. That is what lets the checks project link this FILE and run these
    /// rules with no game. A `using UnityEngine` here breaks that build, which is the alarm.
    /// </summary>
    public static class InputEcho
    {
        /// <summary>
        /// How long after a keystroke a matching text elsewhere is still read as an echo.
        ///
        /// ⚠ Measured from the last time the field's content CHANGED, never from the last time it
        /// held focus. Focus alone used to keep this open for as long as a box was selected, which
        /// made every label equal to its content suspect for as long as somebody left it selected.
        /// </summary>
        public const float WindowSeconds = 1f;

        /// <summary>
        /// True when this text could be an echo of the keyboard.
        /// </summary>
        /// <param name="isKnownGameText">
        /// The game has shown us this exact string before — it is content, whoever is typing.
        /// </param>
        /// <param name="secondsSinceTyped">
        /// Since the focused field's content last changed. Negative counts as "not typing": a
        /// caller with no keystroke to report passes a sentinel rather than a real duration.
        /// </param>
        public static bool CouldBeTyping(bool isKnownGameText, float secondsSinceTyped)
        {
            if (isKnownGameText) return false;
            if (secondsSinceTyped < 0f) return false;
            return secondsSinceTyped <= WindowSeconds;
        }
    }
}
