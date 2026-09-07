using UniverseLib.UI;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A button: a verb, a tone, a size, and the scope marks when it writes somewhere.
    ///
    /// One factory where there were three (primary, secondary, and the raw `UIFactory.CreateButton`
    /// plus two lines of sizing and one of colour, twenty-four times for the compact ones), and
    /// the "busy" pattern — disabled under "Fetching…" — written by hand in four panels.
    /// </summary>
    public static class Buttons
    {
        /// <summary>
        /// Create a button.
        /// </summary>
        /// <param name="scope">Which copy this button writes to; the three marks are laid inside it.</param>
        /// <param name="minWidth">Null takes the tone's own: wider for a primary.</param>
        /// <param name="policy">The verb is UiText unless the code rewrites it (a count, a state).</param>
        public static ButtonHandle Create(Host parent, string name, string text,
                                          ButtonTone tone = ButtonTone.Secondary,
                                          ButtonSize size = ButtonSize.Normal,
                                          int? minWidth = null, Fill fill = Fill.Content,
                                          EditSide? scope = null, TextPolicy policy = TextPolicy.UiText)
        {
            var btn = UIFactory.CreateButton(parent.Object, name, text ?? "");

            int height = size == ButtonSize.Compact ? UIStyles.RowHeightNormal
                       : size == ButtonSize.Field ? UIStyles.InputHeight
                       : UIStyles.ButtonHeight;
            int width = minWidth ?? (size != ButtonSize.Normal ? UIStyles.SmallButtonWidth
                                     : tone == ButtonTone.Primary ? 130 : 110);

            UIFactory.SetLayoutElement(btn.Component.gameObject, minWidth: width, minHeight: height,
                                       flexibleWidth: fill == Fill.Stretch ? (int?)9999 : null);

            // The full ColorBlock — normal, hover, pressed, disabled — and the label followed from
            // there through ButtonStates.
            UIStyles.SetBackground(btn.Component.gameObject, Tones.ButtonFill(tone));

            if (btn.ButtonText != null)
            {
                btn.ButtonText.fontSize = size == ButtonSize.Compact ? UIStyles.FontSizeSmall : UIStyles.FontSizeNormal;
                if (policy == TextPolicy.UiText) TranslatorCore.RegisterUIText(btn.ButtonText);
                else TranslatorCore.RegisterExcluded(btn.ButtonText);
            }

            if (scope.HasValue) ScopeMarks.Adorn(btn, scope.Value);

            return new ButtonHandle(btn);
        }

        /// <summary>The one button that does what the screen is for.</summary>
        public static ButtonHandle Primary(Host parent, string name, string text, int? minWidth = null,
                                           EditSide? scope = null, TextPolicy policy = TextPolicy.UiText)
        {
            return Create(parent, name, text, ButtonTone.Primary, minWidth: minWidth, scope: scope, policy: policy);
        }

        /// <summary>Any other button.</summary>
        public static ButtonHandle Secondary(Host parent, string name, string text, int? minWidth = null,
                                             EditSide? scope = null, TextPolicy policy = TextPolicy.UiText)
        {
            return Create(parent, name, text, ButtonTone.Secondary, minWidth: minWidth, scope: scope, policy: policy);
        }

        /// <summary>A button that fits a dense row — a list's verbs, a notification's answer.</summary>
        public static ButtonHandle Compact(Host parent, string name, string text,
                                           ButtonTone tone = ButtonTone.Secondary, int? minWidth = null,
                                           TextPolicy policy = TextPolicy.UiText)
        {
            return Create(parent, name, text, tone, ButtonSize.Compact, minWidth, policy: policy);
        }

        /// <summary>The row the footer buttons sit in, centred.</summary>
        public static Host Row(Host parent, string name = "ButtonRow")
        {
            return new Host(UIStyles.CreateButtonRow(parent.Object, name));
        }
    }
}
