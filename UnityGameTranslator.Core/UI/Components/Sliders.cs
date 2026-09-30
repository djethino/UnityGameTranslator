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
        /// 🔴 **The rail takes the wheel only when the POINTER moved onto it.** Rails sit inside
        /// windows that scroll, and a rail that took the wheel whenever it passed under the pointer
        /// would be changed by anybody scrolling past it — the classic trap. So: the pointer moved
        /// and is over a rail → that rail holds the wheel, until the pointer leaves it; the page
        /// scrolled a rail under a still pointer → the page keeps the wheel. An event (the pointer
        /// moving), never a delay.
        ///
        /// ⚠ **While a rail holds it, the area around it does not scroll too**: its sensitivity is
        /// set to zero and given back when the rail lets go. The wheel of the frame that took the
        /// rail went to the page already (the EventSystem runs before this tick), so the rail only
        /// counts turns from the next frame — one turn never moves both.
        /// </summary>
        public static void PollWheel()
        {
            Vector3 mouse = UniverseLib.Input.InputManager.MousePosition;
            bool moved = (mouse - _lastMouse).sqrMagnitude > 0.01f;
            _lastMouse = mouse;
            var point = new Vector2(mouse.x, mouse.y);

            // Turns counted for the rail that held the wheel since the last frame.
            float turns = UniverseLib.Input.InputManager.MouseScrollDelta.y;
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
            if (_held == null && moved)
            {
                for (int i = Live.Count - 1; i >= 0; i--)
                {
                    var handle = Live[i];
                    if (handle.Slider == null) { Live.RemoveAt(i); continue; }
                    if (!handle.Enabled || !Over(handle, point)) continue;
                    Take(handle);
                    break;
                }
            }
        }

        private static bool Over(SliderHandle handle, Vector2 screen)
        {
            if (handle.Slider == null || !handle.Slider.gameObject.activeInHierarchy) return false;
            var rect = handle.Slider.GetComponent<RectTransform>();
            var canvas = rect.GetComponentInParent<Canvas>();
            return UIHelpers.ContainsScreenPoint(rect, canvas != null ? canvas.rootCanvas : null, screen);
        }

        private static void Take(SliderHandle handle)
        {
            _held = handle;
            _silenced = handle.Slider.GetComponentInParent<ScrollRect>();
            if (_silenced == null) return;
            _silencedSensitivity = _silenced.scrollSensitivity;
            _silenced.scrollSensitivity = 0f;
        }

        private static void Release()
        {
            if (_silenced != null) _silenced.scrollSensitivity = _silencedSensitivity;
            _silenced = null;
            _held = null;
        }

        /// <summary>The product's colours on the rail, the fill and the handle.</summary>
        private static void Paint(GameObject slider)
        {
            Tint(slider.transform.Find("Background"), UIStyles.SliderBackgroundColor);
            Tint(slider.transform.Find("Fill Area/Fill"), UIStyles.SliderFillColor);
            Tint(slider.transform.Find("Handle Slide Area/Handle"), UIStyles.SliderHandleColor);
        }

        private static void Tint(Transform part, Color colour)
        {
            var image = part != null ? part.GetComponent<Image>() : null;
            if (image != null) image.color = colour;
        }
    }
}
