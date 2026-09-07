using System;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Reusable confirmation dialog for destructive actions.
    ///
    /// ⚠ The first panel written entirely against the vocabulary (2026-09-07): it holds labels and
    /// buttons, and knows nothing about what draws them. What it looks like is decided by the roles
    /// it names — a Title, a Description, a Primary that turns Danger.
    /// </summary>
    public class ConfirmationPanel : TranslatorPanelBase
    {
        public override string Name => "Confirm";
        public override int MinWidth => 400;
        public override int MinHeight => 150;
        public override int PanelWidth => 400;
        public override int PanelHeight => 200;

        protected override int MinPanelHeight => 150;
        protected override bool PersistWindowPreferences => false;

        private LabelHandle _title;
        private LabelHandle _message;
        private ButtonHandle _confirm;
        private ButtonHandle _cancel;
        private Action _onConfirm;
        private Action _onCancel;

        public ConfirmationPanel(UIBase owner) : base(owner)
        {
        }

        /// <summary>
        /// Shows the confirmation dialog with custom message and callbacks.
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="message">Message to display</param>
        /// <param name="confirmText">Text for confirm button (e.g., "Delete", "Logout")</param>
        /// <param name="onConfirm">Action to execute on confirm</param>
        /// <param name="onCancel">Optional action to execute on cancel</param>
        /// <param name="isDanger">If true, confirm button is styled as danger (red)</param>
        public void Show(
            string title,
            string message,
            string confirmText,
            Action onConfirm,
            Action onCancel = null,
            bool isDanger = true)
        {
            _title.Say(title);
            _message.Say(message);
            _confirm.Label = confirmText;
            _onConfirm = onConfirm;
            _onCancel = onCancel;

            _confirm.Tone = isDanger ? ButtonTone.Danger : ButtonTone.Primary;

            SetActive(true);
        }

        protected override void ConstructPanelContent()
        {
            Layout(out var body, out var footer, PanelWidth - 40);

            var card = Stacks.Card(body, "ConfirmCard", 360);

            // Written by Show, so Dynamic: translated at the moment they are written.
            _title = Labels.Create(card, "Title", "Confirm", TextRole.Title, policy: TextPolicy.Dynamic);

            Stacks.Spacer(card, 10);

            _message = Labels.Create(card, "Message", "", TextRole.Description,
                                     policy: TextPolicy.Dynamic, minHeight: UIStyles.MultiLineSmall);

            _cancel = Buttons.Secondary(footer, "CancelBtn", "Cancel");
            _cancel.Clicked += OnCancelClicked;

            _confirm = Buttons.Primary(footer, "ConfirmBtn", "Confirm", policy: TextPolicy.Dynamic);
            _confirm.Clicked += OnConfirmClicked;
        }

        private void OnConfirmClicked()
        {
            SetActive(false);
            _onConfirm?.Invoke();
        }

        private void OnCancelClicked()
        {
            SetActive(false);
            _onCancel?.Invoke();
        }

        protected override void OnClosePanelClicked()
        {
            SetActive(false);
            _onCancel?.Invoke();
        }
    }
}
