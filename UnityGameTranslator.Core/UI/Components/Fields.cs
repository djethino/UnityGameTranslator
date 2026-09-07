using System;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>What a field accepts.</summary>
    public enum FieldKind { Text, Password, Integer, Decimal, Multiline }

    /// <summary>
    /// A text field, alone or behind a caption — the row a form is made of.
    ///
    /// Twenty-four times a panel built `CreateFormRow + CreateLabel(caption, minWidth) +
    /// UIFactory.CreateInputField + contentType + SetLayoutElement + SetBackground`. Here the
    /// kind is a word and the caption is a parameter.
    /// </summary>
    public static class Fields
    {
        /// <summary>A field on its own.</summary>
        /// <param name="richText">
        /// False renders &lt;color=…&gt; and friends literally instead of interpreting them — for a
        /// field editing text a player or a file could have written, where seeing the markup IS the
        /// point (InspectorPanel's text editor). True (the default) is uGUI's own default.
        /// </param>
        /// <param name="minWidth">
        /// A floor on width, for a short numeric field that should not stretch to the row — combine
        /// with <see cref="Fill.Content"/>. Null leaves the width to <paramref name="fill"/> alone,
        /// as before this parameter existed.
        /// </param>
        public static FieldHandle Create(Host parent, string name, string placeholder = "",
                                         FieldKind kind = FieldKind.Text, int? minHeight = null,
                                         Fill fill = Fill.Stretch, Action<string> onChanged = null,
                                         bool richText = true, int? minWidth = null)
        {
            var input = UIFactory.CreateInputField(parent.Object, name, placeholder ?? "");

            int height = minHeight ?? (kind == FieldKind.Multiline ? UIStyles.MultiLineMedium : UIStyles.InputHeight);
            UIFactory.SetLayoutElement(input.Component.gameObject, minWidth: minWidth, minHeight: height,
                                       flexibleWidth: fill == Fill.Stretch ? (int?)9999 : null);
            UIStyles.SetBackground(input.Component.gameObject, UIStyles.InputBackground);

            switch (kind)
            {
                case FieldKind.Password: input.Component.contentType = InputField.ContentType.Password; break;
                case FieldKind.Integer: input.Component.contentType = InputField.ContentType.IntegerNumber; break;
                case FieldKind.Decimal: input.Component.contentType = InputField.ContentType.DecimalNumber; break;
                case FieldKind.Multiline: input.Component.lineType = InputField.LineType.MultiLineNewline; break;
            }

            if (!richText && input.Component.textComponent != null)
                input.Component.textComponent.supportRichText = false;

            var handle = new FieldHandle(input);
            if (onChanged != null) handle.Changed += onChanged;
            return handle;
        }

        /// <summary>
        /// A caption and a field on one row: "Server  [__________]". The caption keeps a fixed
        /// width so the fields of a form line up.
        /// </summary>
        /// <param name="fieldMinWidth">Forwarded to <see cref="Create"/> — a floor for a short field
        /// beside its caption (an attempt count, a temperature), instead of filling the row.</param>
        /// <param name="fieldFill">Forwarded to <see cref="Create"/>.</param>
        public static FieldHandle Captioned(Host parent, string name, string caption, out LabelHandle captionLabel,
                                            string placeholder = "", FieldKind kind = FieldKind.Text,
                                            int captionWidth = 120, Action<string> onChanged = null,
                                            int? fieldMinWidth = null, Fill fieldFill = Fill.Stretch)
        {
            var row = Stacks.Row(parent, name + "Row");
            captionLabel = Labels.Create(row, name + "Caption", caption, TextRole.Body,
                                         minHeight: UIStyles.RowHeightNormal);
            UIFactory.SetLayoutElement(captionLabel.Text.gameObject, minWidth: captionWidth, flexibleWidth: 0);

            return Create(row, name, placeholder, kind, onChanged: onChanged, minWidth: fieldMinWidth, fill: fieldFill);
        }

        /// <summary>Same, for callers that do not need the caption back.</summary>
        public static FieldHandle Captioned(Host parent, string name, string caption, string placeholder = "",
                                            FieldKind kind = FieldKind.Text, int captionWidth = 120,
                                            Action<string> onChanged = null, int? fieldMinWidth = null,
                                            Fill fieldFill = Fill.Stretch)
        {
            return Captioned(parent, name, caption, out _, placeholder, kind, captionWidth, onChanged,
                             fieldMinWidth, fieldFill);
        }
    }
}
