using System;
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

            // UIHelpers carries the Il2Cpp delegate conversion; never AddListener from here.
            UIHelpers.AddSliderListener(slider, v =>
            {
                value.Show(format(v));
                onChanged?.Invoke(v);
            });

            return handle;
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
