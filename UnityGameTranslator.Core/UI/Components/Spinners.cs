using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// The ASymptOmatik mark, turning while something is being waited for.
    ///
    /// 🔴 **One mark, one meaning — it says "we are waiting", and nothing else.** That is why it is
    /// not on the About tab as decoration: a mark that turns while nothing happens teaches the eye
    /// to ignore it, and then it says nothing on the day it matters. The Manager's gear has said
    /// exactly this since the day somebody clicked Refresh four times for want of it.
    ///
    /// 🔴 **Placed so it CANNOT break a layout.** It is an absolutely-positioned child of whatever
    /// it marks — anchored to that thing's left edge, its own fixed size — so no row is widened, no
    /// text is pushed and nothing below moves when it appears or goes. A mark that reflowed the
    /// line it belongs to would move the very sentence somebody is reading.
    ///
    /// ⚠ **A registry ticked from the one tick, not a component**: this assembly is compiled once
    /// for Mono and IL2CPP and cannot inject a MonoBehaviour of its own — the same reason
    /// <see cref="ButtonStates"/> is a registry. Dead marks are dropped as they are met.
    /// </summary>
    public static class Spinners
    {
        /// <summary>One full turn every two and a half seconds: movement, never agitation.</summary>
        private const float DegreesPerSecond = 144f;

        private static readonly List<Spinner> _live = new List<Spinner>();

        /// <summary>
        /// Puts a mark on the left edge of <paramref name="piece"/>, hidden until it is turned on.
        ///
        /// ⚠ A PIECE, never a GameObject: what a component takes is the vocabulary's own handle,
        /// which is the frontier UiBoundaryChecks holds — the engine stops here.
        /// </summary>
        /// <param name="size">
        /// The mark's side, in pixels. Small enough to sit in the padding beside a line of text:
        /// a card keeps about twelve, and the mark has to fit in it without touching the words.
        /// </param>
        internal static Spinner Mark(Handle piece, int size = 12)
        {
            GameObject host = piece?.Object;
            if (host == null) return null;

            var obj = UIFactory.CreateUIObject("WaitMark", host);
            var image = obj.AddComponent<Image>();
            image.raycastTarget = false;
            image.preserveAspect = true;
            image.color = UIStyles.TextMuted;

            var sprite = Branding.Sprite(Branding.Gear) as Sprite;
            if (sprite == null)
            {
                // No picture on this game: no mark at all rather than an empty square. The
                // sentence beside it already says what is happening.
                Object.Destroy(obj);
                return null;
            }

            image.sprite = sprite;

            var rect = obj.GetComponent<RectTransform>();
            if (rect != null)
            {
                // 🔴 **Just OUTSIDE the line's left edge, in the card's own padding** — not inside
                // it. Inside, it overlapped the words: a label's box spans the row and its text
                // starts at that same left edge as soon as the sentence is long or left-aligned,
                // so the mark and the first letters shared a pixel. Reported that way.
                //
                // ⚠ Outside the box and still inside the card, so nothing is pushed, nothing is
                // reflowed, and no text can reach it — while a scroll view's mask, which cuts at
                // the card and not at the line, still keeps it visible.
                rect.anchorMin = new Vector2(0f, 0.5f);
                rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(size, size);
                rect.anchoredPosition = new Vector2(-(size * 0.5f + 3f), 0f);
            }

            obj.SetActive(false);

            var spinner = new Spinner(obj, rect);
            _live.Add(spinner);
            return spinner;
        }

        /// <summary>
        /// Turns every mark that is on. Called from the one tick — see TranslatorUIManager.
        ///
        /// ⚠ Unscaled time: a game paused, or running in slow motion, is exactly when somebody is
        /// waiting on us, and a mark that stops turning then reads as the mod having died.
        /// </summary>
        public static void Tick()
        {
            if (_live.Count == 0) return;

            float step = DegreesPerSecond * Time.unscaledDeltaTime;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var spinner = _live[i];
                if (spinner == null || spinner.Gone)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                spinner.Turn(step);
            }
        }
    }

    /// <summary>One mark: switched on while its wait lasts, turned by the tick.</summary>
    public sealed class Spinner
    {
        private readonly GameObject _object;
        private readonly RectTransform _rect;
        private float _angle;

        internal Spinner(GameObject obj, RectTransform rect) { _object = obj; _rect = rect; }

        /// <summary>True once the screen holding it is gone, so the registry may forget it.</summary>
        internal bool Gone => _object == null;

        /// <summary>Whether the mark is on. Off is the ordinary state: nothing is being waited for.</summary>
        public bool Turning
        {
            get => _object != null && _object.activeSelf;
            set
            {
                if (_object == null) return;
                _object.SetActive(value);
                if (!value) return;

                // Starts where it stopped: a mark that jumps back to noon at every wait reads as
                // two waits rather than one continuing.
                Apply();
            }
        }

        internal void Turn(float degrees)
        {
            if (!Turning) return;
            _angle -= degrees;
            if (_angle <= -360f) _angle += 360f;
            Apply();
        }

        private void Apply()
        {
            if (_rect != null) _rect.localRotation = Quaternion.Euler(0f, 0f, _angle);
        }
    }
}
