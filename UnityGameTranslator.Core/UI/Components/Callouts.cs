using UnityEngine;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>The colour of an encart: what it announces.</summary>
    public enum CalloutTone { Success, Warning, Error, Info }

    /// <summary>
    /// An encart: a tinted block with a title, a sentence and a row of buttons — the Manager's
    /// `Callout`, and the five notification boxes of the overlay, the update banner and the
    /// guidance box of the main panel, all built by hand the same way.
    /// </summary>
    public sealed class Callout
    {
        private readonly Host _body;

        private Callout(Host body, LabelHandle title, LabelHandle text, Host actions)
        {
            _body = body;
            Title = title;
            Text = text;
            Actions = actions;
        }

        /// <summary>The bold first line.</summary>
        public LabelHandle Title { get; }

        /// <summary>The sentence under it. Null when none was asked for.</summary>
        public LabelHandle Text { get; }

        /// <summary>Where its buttons go, right-aligned.</summary>
        public Host Actions { get; }

        /// <summary>The block itself.</summary>
        public Host Handle => _body;

        public bool Visible
        {
            get => _body.Visible;
            set => _body.Visible = value;
        }

        /// <param name="policy">Dynamic when the code rewrites the title and text; UiText when they are fixed.</param>
        public static Callout Create(Host parent, string name, CalloutTone tone, string title,
                                     string text = null, TextPolicy policy = TextPolicy.Dynamic)
        {
            var body = Stacks.Vertical(parent, name, spacing: 4, pad: Pad.Of(8, 5),
                                       minHeight: UIStyles.NotificationBoxHeight);
            UIStyles.SetBackground(body.Object, Fill(tone));

            var titleLabel = Labels.Create(body, name + "Title", title, TextRole.Body, policy: policy);
            titleLabel.Bold = true;

            LabelHandle textLabel = null;
            if (text != null)
                textLabel = Labels.Create(body, name + "Text", text, TextRole.Info, policy: policy);

            var actions = Stacks.Horizontal(body, name + "Actions", spacing: 8,
                                            placement: Placement.MiddleRight, minHeight: UIStyles.RowHeightNormal);

            return new Callout(body, titleLabel, textLabel, actions);
        }

        private static Color Fill(CalloutTone tone)
        {
            switch (tone)
            {
                case CalloutTone.Success: return UIStyles.NotificationSuccess;
                case CalloutTone.Warning: return UIStyles.NotificationWarning;
                case CalloutTone.Error: return UIStyles.NotificationError;
                default: return UIStyles.NotificationInfo;
            }
        }

        /// <summary>
        /// A tone-tinted box with none of <see cref="Create"/>'s fixed shape: no title, no forced
        /// bold, no text-then-actions order. For a box whose own parts need individual roles or
        /// policies, or whose actions come before its trailing line rather than after it — the
        /// overlay's sync box builds its five buttons first and an italic hint last.
        /// </summary>
        public static Host Box(Host parent, string name, CalloutTone tone, int spacing = 5,
                               Pad? pad = null, int minHeight = 0)
        {
            var body = Stacks.Vertical(parent, name, spacing: spacing, pad: pad ?? Pad.Of(8, 5),
                                       minHeight: minHeight > 0 ? minHeight : UIStyles.NotificationBoxHeight);
            UIStyles.SetBackground(body.Object, Fill(tone));
            return body;
        }

        /// <summary>
        /// A tone-tinted ROW, for a message and its buttons sharing one line — the mod update
        /// banner's shape, which <see cref="Box"/> cannot give (it stacks vertically).
        /// </summary>
        public static Host HorizontalBox(Host parent, string name, CalloutTone tone, int spacing = 8,
                                         Pad? pad = null, Placement placement = Placement.MiddleLeft,
                                         int minHeight = 0)
        {
            var body = Stacks.Horizontal(parent, name, spacing: spacing, pad: pad ?? Pad.Of(8, 5),
                                         placement: placement,
                                         minHeight: minHeight > 0 ? minHeight : UIStyles.NotificationBoxHeight);
            UIStyles.SetBackground(body.Object, Fill(tone));
            return body;
        }
    }
}
