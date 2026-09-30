using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// The give at the end of a scroll: push past the first or the last line and the content leans a
    /// few pixels, then settles back — the same gesture as the site (rubber.js) and the Manager
    /// (ScrollBounce), with the same physics (<see cref="EdgeGive"/>, in common).
    ///
    /// 🔴 **The FRAME moves, not the content** (analyse/mod-micro-animations.md). A clamped
    /// ScrollRect puts its content back inside its bounds in LateUpdate, after this tick and before
    /// the frame is drawn: a lean written on the content is undone before anybody sees it. The
    /// bounds are the content against the viewport, so the two are moved TOGETHER and the content
    /// never leaves them:
    ///   - top edge: the viewport shrinks from the top; the content, pinned to its top, goes down
    ///     and a band of the trough shows above it;
    ///   - bottom edge: the viewport shrinks from the bottom and the content goes up by as much —
    ///     still inside, since the viewport lost exactly that.
    /// No row is laid out again: only two vertical offsets change.
    ///
    /// ⚠ `Elastic` was tried on 2026-09-12 and removed within the hour: its resistance applies to
    /// dragging only, the wheel pushes unbounded, and the spring fought the wheel in jumps.
    ///
    /// ⚠ **Polled from the single tick, never a component** — the same assembly runs on Mono and
    /// IL2CPP (see ButtonStates). Every scroll area of the mod registers here, through
    /// UIStyles.GiveScrollAnEdge, so none can be forgotten.
    /// </summary>
    public static class ScrollGive
    {
        private sealed class Area
        {
            internal ScrollRect Scroll;
            internal RectTransform Viewport;
            internal RectTransform Content;
            internal readonly EdgeGive Give = new EdgeGive();

            /// <summary>What is currently written: the top shrink and the bottom shrink, in UI units.</summary>
            internal float Top, Bottom;

            internal bool Drawn => Top != 0f || Bottom != 0f;
        }

        private static readonly List<Area> Areas = new List<Area>();

        /// <summary>Registers a scroll area — called once for each, by UIStyles.GiveScrollAnEdge.</summary>
        internal static void Watch(GameObject scrollObj)
        {
            var scroll = scrollObj != null ? scrollObj.GetComponent<ScrollRect>() : null;
            if (scroll == null || scroll.viewport == null || scroll.content == null) return;

            for (int i = 0; i < Areas.Count; i++)
                if (Areas[i].Scroll == scroll) return;

            Areas.Add(new Area { Scroll = scroll, Viewport = scroll.viewport, Content = scroll.content });
        }

        /// <summary>
        /// Once a frame: the wheel pushing past an end opens that end, and every open end moves on.
        ///
        /// ⚠ The wheel is <c>FrameScrollDelta</c>, as for the rails: over a menu UniverseLib resets
        /// the axes once it has seen the wheel, and this tick runs after that.
        /// ⚠ Only the deepest area under the pointer takes the push — the one Unity scrolls, since a
        /// scroll event stops at the first ScrollRect it meets — and never one a rail has silenced
        /// (Sliders.PollWheel sets its sensitivity to zero while the rail holds the wheel).
        /// </summary>
        public static void Tick()
        {
            float wheel = UniverseLib.Input.InputManager.FrameScrollDelta.y;
            if (wheel != 0f)
            {
                Vector3 mouse = UniverseLib.Input.InputManager.MousePosition;
                var pushed = Under(new Vector2(mouse.x, mouse.y));
                if (pushed != null && AtEnd(pushed, wheel)) pushed.Give.Push(wheel);
            }

            float dt = Time.unscaledDeltaTime;
            for (int i = Areas.Count - 1; i >= 0; i--)
            {
                var area = Areas[i];
                if (area.Scroll == null || area.Viewport == null || area.Content == null)
                {
                    Areas.RemoveAt(i);
                    continue;
                }

                // Gone from the screen with its end open — a window closed, a list folded: handed
                // back at once, so it is whole the next time it shows.
                if (!area.Scroll.gameObject.activeInHierarchy)
                {
                    if (area.Drawn || !area.Give.AtRest)
                    {
                        area.Give.Release();
                        Draw(area, 0f);
                    }
                    continue;
                }

                if (area.Give.AtRest && !area.Drawn) continue;

                area.Give.Advance(dt);
                Draw(area, (float)area.Give.Offset);
            }
        }

        /// <summary>The deepest scroll area under the pointer that can scroll and has not been silenced.</summary>
        private static Area Under(Vector2 screen)
        {
            Area best = null;
            int bestDepth = -1;
            for (int i = 0; i < Areas.Count; i++)
            {
                var area = Areas[i];
                if (area.Scroll == null || area.Viewport == null || area.Content == null) continue;
                if (!area.Scroll.gameObject.activeInHierarchy || area.Scroll.scrollSensitivity <= 0f) continue;
                if (Reach(area) <= 0.5f) continue;   // everything fits: no end to reach

                var canvas = area.Viewport.GetComponentInParent<Canvas>();
                if (!UIHelpers.ContainsScreenPoint(area.Viewport, canvas != null ? canvas.rootCanvas : null, screen)) continue;

                int depth = Depth(area.Viewport);
                if (depth > bestDepth) { bestDepth = depth; best = area; }
            }
            return best;
        }

        /// <summary>How far the content can travel in the viewport as it stands (the give included).</summary>
        private static float Reach(Area area) => area.Content.rect.height - area.Viewport.rect.height;

        /// <summary>Whether the wheel pushes against an end: up at the first line, down at the last.</summary>
        private static bool AtEnd(Area area, float wheel)
        {
            float y = area.Content.anchoredPosition.y;
            return wheel > 0f ? y <= 0.5f : y >= Reach(area) - 0.5f;
        }

        /// <summary>
        /// Writes the lean — positive down (the top end), negative up (the bottom end) — as a change
        /// from what is already written, so the viewport's own width and whatever else sits in its
        /// offsets are left as they are.
        /// </summary>
        private static void Draw(Area area, float lean)
        {
            float top = lean > 0f ? lean : 0f;
            float bottom = lean < 0f ? -lean : 0f;

            if (top != area.Top)
            {
                var max = area.Viewport.offsetMax;
                area.Viewport.offsetMax = new Vector2(max.x, max.y - (top - area.Top));
                area.Top = top;
            }

            if (bottom != area.Bottom)
            {
                float change = bottom - area.Bottom;
                var min = area.Viewport.offsetMin;
                area.Viewport.offsetMin = new Vector2(min.x, min.y + change);
                var at = area.Content.anchoredPosition;
                area.Content.anchoredPosition = new Vector2(at.x, at.y + change);
                area.Bottom = bottom;
            }
        }

        private static int Depth(Transform t)
        {
            int depth = 0;
            for (var p = t; p != null; p = p.parent) depth++;
            return depth;
        }
    }
}
