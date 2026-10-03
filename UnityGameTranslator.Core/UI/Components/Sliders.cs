using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>A value on a rail, with the number written beside it.</summary>
    public sealed class SliderHandle : Handle
    {
        internal readonly Slider Slider;
        private readonly GameObject _row;
        private readonly LabelHandle _value;
        private readonly Func<float, string> _format;

        internal SliderHandle(GameObject row, Slider slider, LabelHandle value, Func<float, string> format)
        {
            _row = row;
            Slider = slider;
            _value = value;
            _format = format;
        }

        internal override GameObject Object => _row;

        public float Value
        {
            get => Slider != null ? Slider.value : 0f;
            set
            {
                if (Slider == null) return;
                Slider.value = value;
                _value?.Show(_format(Slider.value));
            }
        }

        public bool Enabled
        {
            get => Slider != null && Slider.interactable;
            set { if (Slider != null) Slider.interactable = value; }
        }
    }

    /// <summary>
    /// A captioned slider showing its value: `CreateOpacitySlider` and the two font sliders
    /// were three copies of this.
    /// </summary>
    public static class Sliders
    {
        /// <param name="format">How the value is written beside the rail — "85%", "1.5×".</param>
        /// <param name="onChanged">Moved by the person. The written value follows on its own.</param>
        public static SliderHandle Labelled(Host parent, string name, string caption,
                                            float min, float max, float initial,
                                            Func<float, string> format, Action<float> onChanged = null,
                                            int captionWidth = 120, bool wholeNumbers = false)
        {
            var row = Stacks.Row(parent, name + "Row");

            var captionLabel = Labels.Create(row, name + "Caption", caption, TextRole.Body,
                                             minHeight: UIStyles.RowHeightNormal);
            UIFactory.SetLayoutElement(captionLabel.Text.gameObject, minWidth: captionWidth, flexibleWidth: 0);

            var obj = UIFactory.CreateSlider(row.Object, name, out Slider slider);
            UIFactory.SetLayoutElement(obj, minHeight: 20, flexibleWidth: 9999);
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;
            slider.value = initial;
            Paint(obj);

            var value = Labels.Create(row, name + "Value", format(initial), TextRole.Body,
                                      policy: TextPolicy.Excluded, wrap: false,
                                      minHeight: UIStyles.RowHeightNormal);
            UIFactory.SetLayoutElement(value.Text.gameObject, minWidth: 50, flexibleWidth: 0);

            var handle = new SliderHandle(row.Object, slider, value, format);
            Live.Add(handle);

            // UIHelpers carries the Il2Cpp delegate conversion; never AddListener from here.
            UIHelpers.AddSliderListener(slider, v =>
            {
                value.Show(format(v));
                onChanged?.Invoke(v);
            });

            return handle;
        }

        /// <summary>Every rail built, for the wheel — swept as its rows are destroyed.</summary>
        private static readonly List<SliderHandle> Live = new List<SliderHandle>();

        /// <summary>The rail holding the wheel, and the scroll area it silenced meanwhile.</summary>
        private static SliderHandle _held;
        private static ScrollRect _silenced;
        private static float _silencedSensitivity;
        private static Vector3 _lastMouse;

        /// <summary>
        /// One turn of the wheel moves a rail by this share of its range — fifty turns end to end,
        /// fine enough for a percentage, quick enough for a size.
        /// </summary>
        private const float TurnsEndToEnd = 50f;

        /// <summary>
        /// The wheel moves the rail under the pointer (user, 2026-09-30: « les curseurs … ne bougent
        /// pas quand on fait la molette »), once per frame from the single tick.
        ///
        /// 🔴 **The rail takes the wheel only when the POINTER was brought onto it.** Rails sit inside
        /// windows that scroll, and a rail that took the wheel whenever it passed under the pointer
        /// would be changed by anybody scrolling past it — the classic trap. So a rail takes it when
        /// the pointer travelled a deliberate distance since the wheel last turned (IntentDistance —
        /// a hand drifting while it scrolls does not), its list is not moving (Scrolling), and the
        /// pointer is over it; it keeps it until the pointer leaves. Otherwise the page keeps the
        /// wheel. Events (the pointer's way, the wheel's turn), never a delay.
        ///
        /// ⚠ **While a rail holds it, the area around it does not scroll too**: its sensitivity is
        /// set to zero and given back when the rail lets go. The wheel of the frame that took the
        /// rail went to the page already (the EventSystem runs before this tick), so the rail only
        /// counts turns from the next frame — one turn never moves both.
        /// </summary>
        public static void PollWheel()
        {
            Vector3 mouse = UniverseLib.Input.InputManager.MousePosition;
            float step0 = (mouse - _lastMouse).magnitude;
            bool moved = step0 > 0.1f;
            _lastMouse = mouse;
            var point = new Vector2(mouse.x, mouse.y);

            // The way travelled since the wheel last turned — see IntentDistance.
            _travel += step0;

            // 🔴 FrameScrollDelta, never MouseScrollDelta: over a menu, UniverseLib resets every
            // axis once it has seen the wheel (against click-through), so by the time this tick
            // runs the plain reading is zero — measured 2026-09-30, a rail holding the wheel for a
            // dozen turns counted none while the page scrolled fine. The value noted before that
            // reset is the one this frame really had.
            float turns = UniverseLib.Input.InputManager.FrameScrollDelta.y;
            if (turns != 0f) _travel = 0f;
            if (_held != null && turns != 0f && Over(_held, point) && _held.Enabled)
            {
                var slider = _held.Slider;
                float step = (slider.maxValue - slider.minValue) / TurnsEndToEnd;
                if (slider.wholeNumbers) step = Math.Max(1f, (float)Math.Round(step));
                // Setting the value raises the rail's own change, exactly as a drag does: the
                // written value and the screen's callback follow (UIHelpers.AddSliderListener).
                _held.Value = Mathf.Clamp(slider.value + turns * step, slider.minValue, slider.maxValue);
            }

            // Who holds the wheel for the next frame.
            if (_held != null && !Over(_held, point)) Release();
            if (_held == null && moved && _travel >= IntentDistance)
            {
                for (int i = Live.Count - 1; i >= 0; i--)
                {
                    var handle = Live[i];
                    if (handle.Slider == null) { Live.RemoveAt(i); continue; }
                    if (!handle.Enabled || !Over(handle, point) || Scrolling(handle)) continue;
                    Take(handle);
                    break;
                }
            }
        }

        /// <summary>The way the pointer travelled since the wheel last turned, in screen pixels.</summary>
        private static float _travel;

        /// <summary>
        /// How far the pointer has to go, with no turn of the wheel meanwhile, before a rail may take
        /// the wheel — a hand reaching for a rail, not a hand drifting while it scrolls (user,
        /// 2026-09-30, a list of fonts with a Size rail on every row « accrochait » the wheel
        /// mid-scroll). Six pixels of a 1080-line screen, scaled to this one: the interface is laid
        /// out for 1080 lines, so a larger screen must not make the gesture shorter.
        /// </summary>
        private static float IntentDistance => 6f * Math.Max(1f, Screen.height / 1080f);

        /// <summary>
        /// Whether the area holding this rail is still moving — the list scrolling under a still
        /// pointer, momentum included. A rail moving under the pointer was not reached for.
        /// </summary>
        private static bool Scrolling(SliderHandle handle)
        {
            var area = handle.Slider.GetComponentInParent<ScrollRect>();
            return area != null && area.velocity.sqrMagnitude > 1f;
        }

        /// <summary>
        /// The pointer is on this rail, and nothing is drawn over it there: an open list's popup
        /// covering the rail belongs to the list, and its wheel scrolls the list, never the rail
        /// behind (user, 2026-10-02: a font size moved while scrolling the font list above it).
        /// </summary>
        private static bool Over(SliderHandle handle, Vector2 screen)
        {
            if (handle.Slider == null || !handle.Slider.gameObject.activeInHierarchy) return false;
            if (SearchableDropdown.PopupCovers(screen)) return false;
            var rect = handle.Slider.GetComponent<RectTransform>();
            var canvas = rect.GetComponentInParent<Canvas>();
            return UIHelpers.ContainsScreenPoint(rect, canvas != null ? canvas.rootCanvas : null, screen);
        }

        private static void Take(SliderHandle handle)
        {
            _held = handle;

            // Said on screen: the handle lights up while the rail holds the wheel, so a page that
            // stopped scrolling under the pointer reads as "the rail has it", not as a fault.
            Paint(handle, UIStyles.SliderHandleHeld);

            _silenced = handle.Slider.GetComponentInParent<ScrollRect>();
            if (_silenced != null)
            {
                _silencedSensitivity = _silenced.scrollSensitivity;
                _silenced.scrollSensitivity = 0f;
            }
        }

        private static void Release()
        {
            if (_silenced != null) _silenced.scrollSensitivity = _silencedSensitivity;
            if (_held != null) Paint(_held, UIStyles.SliderHandleColor);
            _silenced = null;
            _held = null;
        }

        /// <summary>The handle's colour — the rail's own knob, found where UIFactory puts it.</summary>
        private static void Paint(SliderHandle handle, Color colour)
        {
            if (handle.Slider == null) return;
            var knob = handle.Slider.handleRect;
            var image = knob != null ? knob.GetComponent<Image>() : null;
            if (image != null) image.color = colour;
        }

        /// <summary>The product's colours on the rail, the fill and the handle.</summary>
        private static void Paint(GameObject slider)
        {
            Tint(TransformFind.Path(slider.transform, "Background"), UIStyles.SliderBackgroundColor);
            Tint(TransformFind.Path(slider.transform, "Fill Area/Fill"), UIStyles.SliderFillColor);
            Tint(TransformFind.Path(slider.transform, "Handle Slide Area/Handle"), UIStyles.SliderHandleColor);
        }

        private static void Tint(Transform part, Color colour)
        {
            var image = part != null ? part.GetComponent<Image>() : null;
            if (image != null) image.color = colour;
        }
    }
}
