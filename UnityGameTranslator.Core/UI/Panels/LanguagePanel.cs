using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Language selection panel for choosing source and target languages.
    /// Uses reusable LanguageSelector components.
    /// </summary>
    public class LanguagePanel : TranslatorPanelBase
    {
        public override string Name => "Select Languages";
        public override int MinWidth => 500;
        public override int MinHeight => 300;
        public override int PanelWidth => 500;
        public override int PanelHeight => 550;

        protected override int MinPanelHeight => 300;

        // Language selectors (reusable components)
        private LanguageSelector _sourceSelector;
        private LanguageSelector _targetSelector;

        // Summary display
        private Text _summaryLabel;

        // Callback
        private Action<string, string> _onLanguagesSelected;

        public LanguagePanel(UIBase owner) : base(owner)
        {
            // Note: Components initialized in ConstructPanelContent() - base constructor calls ConstructUI() first
        }

        public void ShowForSelection(Action<string, string> onSelected)
        {
            _onLanguagesSelected = onSelected;
            UpdateSummary();
            SetActive(true);
        }

        protected override void ConstructPanelContent()
        {
            // Initialize components (must be here, not in constructor - base calls ConstructUI first)
            var languages = LanguageHelper.GetLanguageNames();
            _sourceSelector = new LanguageSelector("Source", languages, "English", 100);
            _targetSelector = new LanguageSelector("Target", languages, "", 120);

            // Use scrollable layout for the content
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            var card = CreateAdaptiveCard(scrollContent, "LanguageCard", PanelWidth - 40);

            CreateTitle(card, "Title", "Select Languages");

            UIStyles.CreateSpacer(card, 5);

            // Source language section
            UIStyles.CreateSectionTitle(card, "SourceTitle", "Source Language (original game language)");
            _sourceSelector.CreateUI(card, (lang) => UpdateSummary());

            UIStyles.CreateSpacer(card, 10);

            // Target language section
            UIStyles.CreateSectionTitle(card, "TargetTitle", "Target Language (translation language)");
            _targetSelector.CreateUI(card, (lang) => UpdateSummary());

            UIStyles.CreateSpacer(card, 10);

            // Summary display
            _summaryLabel = UIFactory.CreateLabel(card, "Summary", "", TextAnchor.MiddleCenter);
            _summaryLabel.fontSize = UIStyles.FontSizeNormal + 2;
            _summaryLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_summaryLabel.gameObject, minHeight: UIStyles.RowHeightXLarge);

            UpdateSummary();

            // Buttons - in fixed footer
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);

            var confirmBtn = CreatePrimaryButton(buttonRow, "ConfirmBtn", "Confirm");
            UIStyles.SetBackground(confirmBtn.Component.gameObject, UIStyles.ButtonSuccess);
            confirmBtn.OnClick += ConfirmSelection;
        }

        private void UpdateSummary()
        {
            if (_summaryLabel == null) return;

            string target = _targetSelector?.SelectedLanguage;
            string source = _sourceSelector?.SelectedLanguage ?? "English";

            if (!string.IsNullOrEmpty(target))
            {
                _summaryLabel.text = $"{source} → {target}";
                _summaryLabel.color = UIStyles.StatusSuccess;
            }
            else
            {
                _summaryLabel.text = "Select a target language";
                _summaryLabel.color = UIStyles.TextMuted;
            }
        }

        private void ConfirmSelection()
        {
            string target = _targetSelector?.SelectedLanguage;

            if (string.IsNullOrEmpty(target))
            {
                _summaryLabel.text = "Please select a target language!";
                _summaryLabel.color = UIStyles.StatusError;
                return;
            }

            _onLanguagesSelected?.Invoke(_sourceSelector.SelectedLanguage, target);
            SetActive(false);
        }
    }
}
