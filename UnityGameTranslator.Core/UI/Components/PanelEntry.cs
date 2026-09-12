using System.Collections.Generic;
using UnityEngine;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// The small movement a panel makes as it opens.
    ///
    /// 🔴 **Because appearing and being there look the same.** A window that is simply switched on
    /// between two frames gives the eye nothing to follow: it was not there, now it is, and whether
    /// it is the one just asked for or one that was already open is a question the reader has to
    /// answer by reading. A short growth into place answers it before any word is read — the same
    /// job the give at the end of a scroll does for "that was the end", and the toast for "this is
    /// new".
    ///
    /// 🔴 **SCALE only, and the alpha is deliberately left alone.** The panel's CanvasGroup already
    /// belongs to something else: TranslatorUIManager writes it every time focus changes, to dim
    /// the windows that are not in front. Two things animating one property is the defect that had
    /// the Manager's tabs stop bouncing the same day this was written — there, Motion's transition
    /// and the scroll edge were both writing RenderTransform. Scale is touched by nothing: not by
    /// the layout, not by the dragger, not by the focus pass.
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
    public static class PanelEntry
    {
        /// <summary>How long the movement lasts, in seconds. Short enough to be felt, not watched.</summary>
        private const float Span = 0.16f;

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
        /// ⚠ **Held at scale zero rather than made inactive.** The layout ignores scale, so the
        /// panel is measured and re-clamped normally while nobody can see it — deactivating it
        /// would stop the very pass we are waiting for. And scale is the one transform nothing else
        /// writes: the CanvasGroup belongs to the focus pass, which dims the windows that are not
        /// in front.
        ///
        /// ⚠ Four frames at 60Hz. Long enough to cover the sizing pass, short enough that opening
        /// still feels immediate — the movement that follows is what the eye reads as the opening.
        /// </summary>
        private const float Settling = 0.066f;

        /// <summary>
        /// How small it starts. Just under one: the panel settles into place rather than zooming.
        ///
        /// ⚠ A larger number reads as a flourish, and these windows carry settings and warnings,
        /// not celebrations.
        /// </summary>
        private const float FromScale = 0.93f;

        private static readonly List<Transform> _playing = new List<Transform>();
        private static readonly List<float> _startedAt = new List<float>();

        /// <summary>
        /// Starts the movement on a panel that has just become visible.
        ///
        /// ⚠ Called only on a real change from hidden to shown. The drag handle calls SetActive(true)
        /// on every frame it is held, so playing this on every call would keep a panel permanently
        /// at 96% while it is being moved — see the note on _reportedVisible in TranslatorPanelBase.
        /// </summary>
        /// <remarks>
        /// ⚠ `internal`, like every engine-typed member the legacy components keep for each other
        /// and for the base: what crosses the frontier in public is a handle, never a GameObject.
        /// `UiBoundaryChecks` rule 2 enforces it, and caught this the first time it was written.
        /// </remarks>
        internal static void Play(GameObject panel)
        {
            if (panel == null) return;

            var transform = panel.transform;
            if (transform == null) return;

            var known = _playing.IndexOf(transform);
            if (known >= 0)
            {
                _startedAt[known] = Clock.Now;
            }
            else
            {
                _playing.Add(transform);
                _startedAt.Add(Clock.Now);
            }

            // Out of sight while it sizes itself — see Settling. Nothing is watching yet, so
            // nothing has to be smooth about this.
            transform.localScale = Vector3.zero;
        }

        /// <summary>
        /// Advances every movement in flight. Called from the UI update loop, beside the toast's own
        /// tick, so one clock drives everything that moves in this interface.
        ///
        /// 🔴 **This became load-bearing the day the panel was held out of sight to settle.** A
        /// panel starts at scale zero and it is THIS that brings it back: stop calling it and a
        /// window opens invisible, with nothing in the log and nothing to see. Two things make that
        /// safe, and both must stay true — the loop this sits in runs whenever any UI is showing,
        /// which a panel becoming visible guarantees; and Clock.Now is realtimeSinceStartup, so a
        /// paused game or a zero timescale does not freeze the count.
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
                    _playing.RemoveAt(i);
                    _startedAt.RemoveAt(i);
                    continue;
                }

                var since = Clock.Now - _startedAt[i];

                // Still sizing itself. Kept out of sight rather than shown mid-rearrangement.
                if (since < Settling)
                {
                    transform.localScale = Vector3.zero;
                    continue;
                }

                var t = Mathf.Clamp01((since - Settling) / Span);

                // Eased out, so it decelerates into place instead of arriving at a stop.
                var eased = 1f - (1f - t) * (1f - t) * (1f - t);
                transform.localScale = Vector3.one * Mathf.Lerp(FromScale, 1f, eased);

                if (t < 1f) continue;

                // ⚠ Put back to exactly one rather than near it: a panel left at 0.999 is a whole
                // subtree re-rasterised for ever at a size nothing asked for.
                transform.localScale = Vector3.one;
                _playing.RemoveAt(i);
                _startedAt.RemoveAt(i);
            }
        }
    }
}
