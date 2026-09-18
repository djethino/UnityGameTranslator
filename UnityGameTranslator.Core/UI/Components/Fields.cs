using System;
using UnityEngine.UI;
using UnityEngine;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UniverseLib.UI.Widgets;

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
        /// <param name="scroll">
        /// A multiline field that keeps its height and scrolls inside it, instead of growing with
        /// what it holds: a paragraph pasted into a growing field pushed everything under it off
        /// the screen and gave the whole window a scrollbar instead of the field.
        /// </param>
        /// <param name="readOnly">
        /// Selectable and copyable, never typed into — text the code shows and the person needs to
        /// take characters out of, a placeholder out of a game's line being the case it was built
        /// for (2026-09-19).
        ///
        /// 🔴 **Not <c>interactable = false</c>**, which is the reflex and is wrong: a disabled
        /// field cannot be selected either, so it would take away the one thing this is for.
        ///
        /// ⚠ It is painted as a TROUGH, against the letter of UIStyles' "a field is not a trough"
        /// — deliberately. That rule keeps an INPUT off the recessed colour; this is not an input,
        /// it is a box that receives content, and where it is used it replaces a list, whose look
        /// it has to keep. Painting it like the field below it would invite typing into something
        /// that refuses every key, which is the defect the rule exists to prevent, one level up.
        /// </param>
        public static FieldHandle Create(Host parent, string name, string placeholder = "",
                                         FieldKind kind = FieldKind.Text, int? minHeight = null,
                                         Fill fill = Fill.Stretch, Action<string> onChanged = null,
                                         bool richText = true, int? minWidth = null, bool scroll = false,
                                         bool readOnly = false)
        {
            int height = minHeight ?? (kind == FieldKind.Multiline ? UIStyles.MultiLineMedium : UIStyles.InputHeight);

            InputFieldRef input;
            if (scroll && kind == FieldKind.Multiline)
            {
                // UniverseLib's scrolling input: a viewport, the field as its content, a slider that
                // appears when the text outgrows the box. The box itself is held at its height —
                // the factory hands it a flexible height, which is exactly what must not happen here.
                var box = UIFactory.CreateScrollInputField(parent.Object, name, placeholder ?? "", out var scroller);
                UIFactory.SetLayoutElement(box, minWidth: minWidth, minHeight: height, preferredHeight: height,
                                           flexibleHeight: 0, flexibleWidth: fill == Fill.Stretch ? 9999 : 0);
                UIStyles.SetBackground(box, readOnly ? UIStyles.TroughBackground : UIStyles.InputBackground);
                input = scroller.InputField;
                if (readOnly) input.Component.readOnly = true;

                var scrolling = new FieldHandle(input) { Area = new FieldArea(box, scroller, height) };
                if (!richText && input.Component.textComponent != null)
                    input.Component.textComponent.supportRichText = false;
                if (onChanged != null) scrolling.Changed += onChanged;
                return scrolling;
            }
            else
            {
                input = UIFactory.CreateInputField(parent.Object, name, placeholder ?? "");
                UIFactory.SetLayoutElement(input.Component.gameObject, minWidth: minWidth, minHeight: height,
                                           flexibleWidth: fill == Fill.Stretch ? (int?)9999 : null);
                UIStyles.SetBackground(input.Component.gameObject,
                                       readOnly ? UIStyles.TroughBackground : UIStyles.InputBackground);
                if (readOnly) input.Component.readOnly = true;
            }

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
        /// <summary>
        /// A scrolling field as a shared area: what it holds is its text laid out at its width —
        /// the scroller's own measure of the WHOLE text (InputFieldScroller.MeasureContentHeight),
        /// because the field's own label only ever carries the lines in view — and never less
        /// than the height it was made with.
        ///
        /// 🔴 Measured BY the scroller, never here: building Unity's TextGenerationSettings from
        /// this assembly is a TypeLoadException on IL2CPP ("value type mismatch"), thrown at the
        /// click that opened a line. UniverseLib is compiled for that runtime; this is not.
        /// </summary>
        private sealed class FieldArea : ISharedArea
        {
            private readonly GameObject _box;
            private readonly InputFieldScroller _scroller;
            private readonly int _floor;

            internal FieldArea(GameObject box, InputFieldScroller scroller, int floor)
            {
                _box = box;
                _scroller = scroller;
                _floor = floor;
            }

            public float ContentHeight
            {
                get
                {
                    if (_scroller?.InputField?.Component == null) return _floor;
                    return Math.Max(_floor, _scroller.MeasureContentHeight());
                }
            }

            public bool AtTop => _scroller?.ContentRect == null || _scroller.ContentRect.anchoredPosition.y <= 0.5f;

            public void ToTop()
            {
                var content = _scroller?.ContentRect;
                if (content == null) return;
                content.anchoredPosition = new Vector2(content.anchoredPosition.x, 0f);
            }

            public void SetHeight(int height, bool fill)
            {
                if (_box == null) return;
                UIFactory.SetLayoutElement(_box, minHeight: height, preferredHeight: height,
                                           flexibleHeight: fill ? 9999 : 0, flexibleWidth: 9999);
            }
        }

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
