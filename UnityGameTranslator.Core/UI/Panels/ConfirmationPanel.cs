using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Reusable confirmation dialog for destructive actions.
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

        private Text _titleLabel;
        private Text _messageLabel;
        private ButtonRef _confirmBtn;
        private ButtonRef _cancelBtn;
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
            _titleLabel.text = title;
            _messageLabel.text = message;
            _confirmBtn.ButtonText.text = confirmText;
            _onConfirm = onConfirm;
            _onCancel = onCancel;

            // Style confirm button
            UIStyles.SetBackground(_confirmBtn.Component.gameObject,
                isDanger ? UIStyles.ButtonDanger : UIStyles.ButtonPrimary);

            SetActive(true);
        }

        protected override void ConstructPanelContent()
        {
            // Use centralized scroll layout like all other panels
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            var card = CreateAdaptiveCard(scrollContent, "ConfirmCard", 360);

            // Title - dynamically set, use UI-specific translation
            _titleLabel = UIFactory.CreateLabel(card, "Title", "Confirm", TextAnchor.MiddleCenter);
            _titleLabel.fontSize = UIStyles.FontSizeTitle;
            _titleLabel.fontStyle = FontStyle.Bold;
            _titleLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_titleLabel.gameObject, minHeight: UIStyles.TitleHeight);
            RegisterUIText(_titleLabel);

            UIStyles.CreateSpacer(card, 10);

            // Message - dynamically set, use UI-specific translation
            _messageLabel = UIFactory.CreateLabel(card, "Message", "", TextAnchor.MiddleCenter);
            _messageLabel.fontSize = UIStyles.FontSizeNormal;
            _messageLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_messageLabel.gameObject, minHeight: UIStyles.MultiLineSmall);
            RegisterUIText(_messageLabel);

            // Buttons in fixed footer
            _cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            _cancelBtn.OnClick += OnCancelClicked;
            RegisterUIText(_cancelBtn.ButtonText);

            _confirmBtn = CreatePrimaryButton(buttonRow, "ConfirmBtn", "Confirm");
            _confirmBtn.OnClick += OnConfirmClicked;
            RegisterUIText(_confirmBtn.ButtonText);
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
