using System.Collections.Generic;
using UnityEngine;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// The small movement anything makes as it appears — a whole panel, or one box inside one.
    ///
    /// 🔴 **Because appearing and being there look the same.** Something simply switched on between
    /// two frames gives the eye nothing to follow: it was not there, now it is, and whether it is
    /// what was just asked for or something that was already up is a question the reader has to
    /// answer by reading. A short growth into place answers it before any word is read — the same
    /// job the give at the end of a scroll does for "that was the end", and the toast for "this is
    /// new".
    ///
    /// 🔴 **Two profiles, one mechanism, and every appearance in this mod goes through it.** A
    /// panel is a window opening; a box is a line of an answer arriving. They want the same gesture
    /// at different sizes, not two gestures — so the difference is two numbers, and the movement,
    /// the clock and the registry are shared. Reported as "everything is too raw": a program where
    /// blocks blink in reads as unfinished however good each screen is.
    ///
    /// ⚠ **Where a block appearance is triggered from is the one place that can see them all**:
    /// `Handle.Visible`, which every piece of the vocabulary sets. One line there covers the boxes
    /// of the overlay, the cards, the rows, and anything written afterwards — the alternative was a
    /// call at each of the hundreds of sites that show something, which is a rule that gets
    /// forgotten the day after it is written.
    ///
    /// 🔴 **SCALE only, and the alpha is deliberately left alone.** The panel's CanvasGroup already
    /// belongs to something else: TranslatorUIManager writes it every time focus changes, to dim
    /// the windows that are not in front. Two things animating one property is the defect that had
    /// the Manager's tabs stop bouncing the same day this was written — there, Motion's transition
    /// and the scroll edge were both writing RenderTransform. Scale is touched by nothing: not by
    /// the layout, not by the dragger, not by the focus pass. When a panel has to be out of sight
    /// while it sizes itself, this says so (<see cref="HeldOutOfSight"/>) and the focus pass writes
    /// the zero — one writer, one fact.
    ///
    /// ⚠ **A registry ticked once a frame, not a MonoBehaviour** — the shape ButtonStates explains
    /// at length: this assembly is built ONCE for Mono and IL2CPP, so a type injected into the
    /// runtime would work on one and fail on the other.
    ///
    /// ⚠ **Opening only.** A close takes effect at once, because the panel really does go away —
    /// the router is told, a click that asked for it is finishing, and holding a window on screen
    /// after it has been closed would make "is it open?" answerable two ways. A departure nobody
    /// watches costs nothing; an arrival nobody notices costs a reading.
    /// </summary>
    public static class Appearances
    {
        /// <summary>How long a panel's movement lasts. Short enough to be felt, not watched.</summary>
        private const float PanelSpan = 0.16f;

        /// <summary>
        /// And a box's, which is shorter.
        ///
        /// ⚠ A block is small and there may be several at once — a refreshed overlay shows three.
        /// The same duration as a window would read as the screen labouring; briefer reads as the
        /// content settling.
        /// </summary>
        private const float BlockSpan = 0.11f;

        /// <summary>
        /// How long the panel is held out of sight before it grows, in seconds.
        ///
        /// 🔴 **This is the fix for the shudder at opening, and it is not a delay for taste.** A
        /// panel cannot be measured while it is hidden — it has no size — so the sizing pass runs
        /// with the window ALREADY ON SCREEN: two frames, a forced layout rebuild, then the real
        /// size and the position clamp. Everything in that sequence is correct and every step of it
        /// is visible, which is exactly what "a little glitch, a sort of tremble at opening" is. It
        /// became noticeable with the router, which hides and shows panels on every navigation
        /// rather than leaving them up.
        ///
        /// ⚠ **Held out of sight rather than made inactive.** The layout runs on a hidden window,
        /// so the panel is measured and re-clamped normally while nobody can see it — deactivating
        /// it would stop the very pass we are waiting for.
        ///
        /// 🔴 **Out of sight means alpha zero, NEVER scale zero** (2026-09-15). It was held at scale
        /// zero, on the reasoning that the layout ignores scale. The layout does; a ScrollRect does
        /// not: it works its content's bounds out through the viewport's world-to-local matrix,
        /// which is singular at scale zero, and every scroll area of the window then pushed its
        /// content by thousands of pixels a frame until the clamp caught it at the far edge. That
        /// is what "les listes s'ouvrent scrollées en bas" was — on the one screen whose lists are
        /// built and sized during these very frames — and three fixes aimed at the lists could not
        /// touch it. The alpha belongs to the focus pass (TranslatorUIManager dims the windows not
        /// in front), so this does not write it: it answers <see cref="HeldOutOfSight"/>, and the
        /// focus pass — the one writer — writes zero while that holds. The scale sits at
        /// <see cref="PanelFrom"/> meanwhile, where the movement will start from.
        ///
        /// ⚠ Four frames at 60Hz. Long enough to cover the sizing pass, short enough that opening
        /// still feels immediate — the movement that follows is what the eye reads as the opening.
        /// </summary>
        private const float Settling = 0.066f;

        /// <summary>
        /// How small a panel starts. Just under one: it settles into place rather than zooming.
        ///
        /// ⚠ A larger number reads as a flourish, and these windows carry settings and warnings,
        /// not celebrations.
        /// </summary>
        private const float PanelFrom = 0.93f;

        /// <summary>
        /// And a box, which starts closer to its size.
        ///
        /// ⚠ Deliberately slight. A block sits INSIDE something already on screen, so a movement
        /// big enough to notice on its own would read as the panel around it jumping.
        /// </summary>
        private const float BlockFrom = 0.97f;

        private static readonly List<Transform> _playing = new List<Transform>();
        private static readonly List<float> _startedAt = new List<float>();
        private static readonly List<bool> _isPanel = new List<bool>();

        /// <summary>
        /// Starts a panel opening: held out of sight while it sizes itself, then grown in.
        ///
        /// ⚠ Called only on a real change from hidden to shown. The drag handle calls SetActive(true)
        /// on every frame it is held, so playing this on every call would keep a panel permanently
        /// under size while it is being moved — see the note on _reportedVisible in
        /// TranslatorPanelBase.
        /// </summary>
        /// <remarks>
        /// ⚠ `internal`, like every engine-typed member the legacy components keep for each other
        /// and for the base: what crosses the frontier in public is a handle, never a GameObject.
        /// `UiBoundaryChecks` rule 2 enforces it, and caught this the first time it was written.
        /// </remarks>
        internal static void Panel(GameObject panel) => Start(panel, true);

        /// <summary>
        /// Starts a box arriving inside something already on screen.
        ///
        /// ⚠ No settling wait: a block is laid out by its parent, which is already measured. The
        /// wait exists for a window that has to find its own size first.
        /// </summary>
        internal static void Block(GameObject box) => Start(box, false);

        private static void Start(GameObject target, bool panel)
        {
            if (target == null) return;

            var transform = target.transform;
            if (transform == null) return;

            var known = _playing.IndexOf(transform);
            if (known >= 0)
            {
                _startedAt[known] = Clock.Now;
                _isPanel[known] = panel;
            }
            else
            {
                _playing.Add(transform);
                _startedAt.Add(Clock.Now);
                _isPanel.Add(panel);
            }

            // Both start where their movement will start from. A panel is out of sight meanwhile —
            // through the focus pass, see Settling — a block has nothing to wait for.
            transform.localScale = Vector3.one * (panel ? PanelFrom : BlockFrom);
        }

        /// <summary>
        /// Whether this panel is still sizing itself and must not be seen yet — the fact the focus
        /// pass turns into an alpha of zero. See <see cref="Settling"/> for why it is asked rather
        /// than done here.
        /// </summary>
        internal static bool HeldOutOfSight(GameObject panel)
        {
            if (panel == null) return false;

            var known = _playing.IndexOf(panel.transform);
            if (known < 0 || !_isPanel[known]) return false;

            return Clock.Now - _startedAt[known] < Settling;
        }

        /// <summary>
        /// Advances every movement in flight. Called from the UI update loop, beside the toast's own
        /// tick, so one clock drives everything that moves in this interface.
        ///
        /// 🔴 **This became load-bearing the day the panel was held out of sight to settle.** A
        /// panel starts under size and it is THIS that brings it to one — and the focus pass keeps
        /// it at alpha zero for as long as <see cref="HeldOutOfSight"/> says so, which is a count on
        /// the same clock. Two things make that safe, and both must stay true — the loop this sits
        /// in runs whenever any UI is showing, which a panel becoming visible guarantees; and
        /// Clock.Now is realtimeSinceStartup, so a paused game or a zero timescale does not freeze
        /// the count.
        /// </summary>
        public static void Tick()
        {
            for (int i = _playing.Count - 1; i >= 0; i--)
            {
                var transform = _playing[i];

                // The panel was destroyed, or the game tore its canvas down. Unity's fake null
                // means this has to be asked rather than assumed.
                if (transform == null)
                {
                    Forget(i);
                    continue;
                }

                bool panel = _isPanel[i];
                var since = Clock.Now - _startedAt[i];
                var wait = panel ? Settling : 0f;

                // A panel is still sizing itself: kept out of sight by the focus pass rather than
                // shown mid-rearrangement, and held where its movement will start from.
                if (since < wait)
                {
                    transform.localScale = Vector3.one * PanelFrom;
                    continue;
                }

                var t = Mathf.Clamp01((since - wait) / (panel ? PanelSpan : BlockSpan));

                // Eased out, so it decelerates into place instead of arriving at a stop.
                var eased = 1f - (1f - t) * (1f - t) * (1f - t);
                var from = panel ? PanelFrom : BlockFrom;
                transform.localScale = Vector3.one * Mathf.Lerp(from, 1f, eased);

                if (t < 1f) continue;

                // ⚠ Put back to exactly one rather than near it: anything left at 0.999 is a whole
                // subtree re-rasterised for ever at a size nothing asked for.
                transform.localScale = Vector3.one;
                Forget(i);
            }
        }

        private static void Forget(int i)
        {
            _playing.RemoveAt(i);
            _startedAt.RemoveAt(i);
            _isPanel.RemoveAt(i);
        }
    }
}
