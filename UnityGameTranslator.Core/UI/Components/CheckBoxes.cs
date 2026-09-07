using System;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A box with words beside it.
    ///
    /// The six lines every panel wrote around `UIFactory.CreateToggle` — the label's text, its
    /// colour, the row's height, the registration, the listener, the help — thirty-two times.
    /// </summary>
    public static class CheckBoxes
    {
        /// <param name="initial">On or off at creation. Set BEFORE the listener is attached, so creating it fires nothing.</param>
        /// <param name="onChanged">Flipped by the person — never by <see cref="ToggleHandle.IsOn"/> from code.</param>
        public static ToggleHandle Create(Host parent, string name, string label, bool initial = false,
                                          Action<bool> onChanged = null, TextPolicy policy = TextPolicy.UiText,
                                          Tone tone = Tone.Plain)
        {
            var row = UIFactory.CreateToggle(parent.Object, name, out Toggle toggle, out Text text);
            text.text = label ?? "";
            text.fontSize = UIStyles.FontSizeNormal;
            text.color = Tones.Colour(tone);
            UIFactory.SetLayoutElement(row, minHeight: UIStyles.ToggleHeight);

            // The factory creates it ON; the value is put right before anybody listens.
            toggle.isOn = initial;

            if (policy == TextPolicy.UiText) TranslatorCore.RegisterUIText(text);
            else TranslatorCore.RegisterExcluded(text);

            var handle = new ToggleHandle(row, toggle, new LabelHandle(text, policy));
            if (onChanged != null) handle.OnChanged(onChanged);
            return handle;
        }
    }
}
