using UnityEngine;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A short-lived, single-line banner: white bold text over one of three fixed colours — its
    /// own small palette, distinct from the four notification tones <see cref="Callout"/> reads,
    /// since a toast replaces every other box on screen rather than sitting beside them.
    ///
    /// 🔴 **It arrives and it leaves; it does not blink into being.** A box that appears between two
    /// frames is indistinguishable from one that was always there and that the eye simply had not
    /// noticed — so a second toast with a different message can pass for the first one still up. A
    /// short movement says "this is new" without a word, which is the same job the give at the end
    /// of a scroll does for "that was the end".
    ///
    /// ⚠ **Opacity and SCALE, never position.** This box lives inside a vertical layout group,
    /// which owns its position and its size and rewrites both whenever anything around it changes;
    /// an offset written here would be overwritten on the next layout pass, or fight it. Scale is
    /// the one transform a layout does not touch.
    /// </summary>
    public sealed class Toasts
    {
        /// <summary>How long the box takes to arrive, in seconds.</summary>
        private const float RiseTime = 0.16f;

        /// <summary>And to leave. Shorter: going is acknowledged, not watched.</summary>
        private const float FadeTime = 0.13f;

        /// <summary>
        /// How small it starts. Just under one — the box grows into place rather than zooming in.
        ///
        /// ⚠ A big number here reads as a flourish, and a flourish on a message that may be an
        /// error is the wrong register.
        /// </summary>
        private const float FromScale = 0.94f;

        private readonly Host _box;
        private readonly LabelHandle _label;
        private readonly CanvasGroup _group;

        /// <summary>When the current movement started, on the same clock the overlay expires on.</summary>
        private float _since;

        /// <summary>Which way it is going: +1 arriving, -1 leaving, 0 settled.</summary>
        private int _way;

        private Toasts(Host box, LabelHandle label, CanvasGroup group)
        {
            _box = box;
            _label = label;
            _group = group;
        }

        /// <summary>The box itself.</summary>
        public Host Handle => _box;

        public bool Visible
        {
            get => _box.Visible;
            set => _box.Visible = value;
        }

        public static Toasts Create(Host parent, string name)
        {
            var box = Stacks.Vertical(parent, name, spacing: 0, pad: new Pad(12, 12, 8, 8),
                                      minHeight: UIStyles.RowHeightLarge);

            var label = Labels.Create(box, name + "Label", "", TextRole.SectionTitle,
                                      centred: true, policy: TextPolicy.Excluded,
                                      minHeight: UIStyles.RowHeightMedium);
            // Always white, whatever the tone: only the background carries the on/off/info read.
            label.Text.color = Color.white;

            // ⚠ A stock Unity component, not one of ours: this assembly is built once for Mono and
            // IL2CPP and cannot inject a type of its own — see ButtonStates.
            var group = box.Object.GetComponent<CanvasGroup>();
            if (group == null) group = box.Object.AddComponent<CanvasGroup>();

            box.Visible = false;
            return new Toasts(box, label, group);
        }

        /// <summary>Success and Error read as an ON/OFF flip; anything else as neutral info.</summary>
        public void Show(string message, Tone tone)
        {
            UIStyles.SetBackground(_box.Object, Fill(tone));
            _label.Show(message);
            _box.Visible = true;

            // ⚠ Restarted even when one is already up: a new message replacing another is a new
            // arrival, and reading it as "still the same box" is the confusion this exists to end.
            _way = 1;
            _since = Clock.Now;
            Draw(0f);
        }

        /// <summary>
        /// Starts it leaving. It is still visible until <see cref="Tick"/> has seen it out, which is
        /// what lets the caller keep asking "is a toast up?" and get the honest answer.
        /// </summary>
        public void BeginHide()
        {
            if (!_box.Visible || _way < 0) return;

            _way = -1;
            _since = Clock.Now;
        }

        /// <summary>
        /// Advances the movement. Returns whether the box is still on screen.
        ///
        /// ⚠ Driven by the overlay's own tick rather than a coroutine of its own: one clock for the
        /// thing that expires a toast and the thing that animates it means they cannot disagree
        /// about whether it is still there.
        /// </summary>
        public bool Tick()
        {
            if (!_box.Visible || _way == 0) return _box.Visible;

            var span = _way > 0 ? RiseTime : FadeTime;
            var t = span <= 0f ? 1f : Mathf.Clamp01((Clock.Now - _since) / span);

            Draw(t);

            if (t < 1f) return true;

            if (_way < 0)
            {
                _box.Visible = false;
                _group.alpha = 1f;
                _box.Object.transform.localScale = Vector3.one;
            }

            _way = 0;
            return _box.Visible;
        }

        /// <summary>
        /// One frame of the movement, with <paramref name="t"/> running 0 to 1 through it.
        ///
        /// Eased out on the way in so it decelerates into place, and straight on the way out: a
        /// departure that lingers asks to be watched, and there is nothing left to read.
        /// </summary>
        private void Draw(float t)
        {
            if (_way > 0)
            {
                var eased = 1f - (1f - t) * (1f - t) * (1f - t);
                _group.alpha = eased;
                _box.Object.transform.localScale = Vector3.one * Mathf.Lerp(FromScale, 1f, eased);
            }
            else
            {
                _group.alpha = 1f - t;
                _box.Object.transform.localScale = Vector3.one * Mathf.Lerp(1f, FromScale, t);
            }
        }

        private static Color Fill(Tone tone)
        {
            switch (tone)
            {
                case Tone.Success: return UIStyles.ToastSuccessBg;
                case Tone.Error: return UIStyles.ToastErrorBg;
                default: return UIStyles.ToastInfoBg;
            }
        }
    }
}
