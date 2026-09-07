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
        /// <param name="fill">Stretch to push whatever follows it on the same row to the far edge —
        /// a checkbox sharing a row with a button beyond it. Content (the default) keeps its own
        /// width, as every checkbox did before this parameter existed.</param>
        public static ToggleHandle Create(Host parent, string name, string label, bool initial = false,
                                          Action<bool> onChanged = null, TextPolicy policy = TextPolicy.UiText,
                                          Tone tone = Tone.Plain, Fill fill = Fill.Content)
        {
            var row = UIFactory.CreateToggle(parent.Object, name, out Toggle toggle, out Text text);
            text.text = label ?? "";
            text.fontSize = UIStyles.FontSizeNormal;
            text.color = Tones.Colour(tone);
            UIFactory.SetLayoutElement(row, minHeight: UIStyles.ToggleHeight,
                                       flexibleWidth: fill == Fill.Stretch ? (int?)9999 : null);

            // The factory creates it ON; the value is put right before anybody listens.
            toggle.isOn = initial;

            if (policy == TextPolicy.UiText) TranslatorCore.RegisterUIText(text);
            else TranslatorCore.RegisterExcluded(text);

            var handle = new ToggleHandle(row, toggle, new LabelHandle(text, policy));
            if (onChanged != null) handle.OnChanged(onChanged);
            return handle;
        }

        /// <summary>
        /// A box with no words: the words are elsewhere on the row — a name and a description in
        /// a column beside it, a list's line. It keeps the width of a control so the column next
        /// to it starts at the same place on every row. <see cref="ToggleHandle.Text"/> is null.
        /// </summary>
        /// <param name="initial">On or off at creation. Set BEFORE the listener is attached, so creating it fires nothing.</param>
        /// <param name="onChanged">Flipped by the person — never by <see cref="ToggleHandle.IsOn"/> from code.</param>
        public static ToggleHandle Bare(Host parent, string name, bool initial = false, Action<bool> onChanged = null)
        {
            var row = UIFactory.CreateToggle(parent.Object, name, out Toggle toggle, out Text text);
            UIFactory.SetLayoutElement(row, minWidth: UIStyles.ToggleControlWidth);

            // The factory creates it ON; the value is put right before anybody listens.
            toggle.isOn = initial;

            // The factory makes an empty label anyway; excluded, so no pipeline ever writes into it.
            TranslatorCore.RegisterExcluded(text);

            var handle = new ToggleHandle(row, toggle, null);
            if (onChanged != null) handle.OnChanged(onChanged);
            return handle;
        }
    }
}
