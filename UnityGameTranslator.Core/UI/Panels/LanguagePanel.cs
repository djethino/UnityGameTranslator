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

        // Language dropdowns (reusable components)
        private SearchableDropdown _sourceDropdown;
        private SearchableDropdown _targetDropdown;

        // Summary display
        private Text _summaryLabel;

        // Contextual help bar
        private Components.HelpZone _helpZone;

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
            _sourceDropdown = new SearchableDropdown("Source", languages, "English", popupHeight: 250, showSearch: true);
            _targetDropdown = new SearchableDropdown("Target", languages, "", popupHeight: 250, showSearch: true);

            // The flag beside each name, as OptionsPanel already does for its own two.
            //
            // ⚠ Every row here IS a language — the list comes straight from the catalogue, with no
            // "auto …" entry to skip — so the row is its own answer. OptionsPanel needs a helper
            // only because its lists begin with a row that stands for no language.
            //
            // 🔴 These two were left out when the flag was taught to SearchableDropdown, so the mod
            // drew flags in Options and none here: the same list of languages, twice, looking like
            // two different controls.
            _sourceDropdown.MarkProvider = row => row;
            _targetDropdown.MarkProvider = row => row;

            // Use scrollable layout for the content
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            var card = CreateAdaptiveCard(scrollContent, "LanguageCard", PanelWidth - 40);

            var title = CreateTitle(card, "Title", "Select Languages");
            RegisterUIText(title);

            UIStyles.CreateSpacer(card, 5);

            // Source language section
            var sourceTitle = UIStyles.CreateSectionTitle(card, "SourceTitle", "Source Language (original game language)");
            RegisterUIText(sourceTitle);
            var srcObj = _sourceDropdown.CreateUI(card, (lang) => UpdateSummary(), width: 200);
            _helpZone?.Describe(srcObj,
                "The game's original language that the mod reads from. Pick the language the game currently displays.");

            UIStyles.CreateSpacer(card, 10);

            // Target language section
            var targetTitle = UIStyles.CreateSectionTitle(card, "TargetTitle", "Target Language (translation language)");
            RegisterUIText(targetTitle);
            var tgtObj = _targetDropdown.CreateUI(card, (lang) => UpdateSummary(), width: 200);
            _helpZone?.Describe(tgtObj,
                "The language you want the game translated into. The mod converts text from the source language to this one.");

            UIStyles.CreateSpacer(card, 10);

            // Summary display - shows language codes, exclude from translation
            _summaryLabel = UIFactory.CreateLabel(card, "Summary", "", TextAnchor.MiddleCenter);
            _summaryLabel.fontSize = UIStyles.FontSizeNormal + 2;
            _summaryLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_summaryLabel.gameObject, minHeight: UIStyles.RowHeightXLarge);
            RegisterExcluded(_summaryLabel); // Contains language names in original form

            UpdateSummary();

            // Buttons - in fixed footer
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);
            RegisterUIText(cancelBtn.ButtonText);

            var confirmBtn = CreatePrimaryButton(buttonRow, "ConfirmBtn", "Confirm");
            UIStyles.SetBackground(confirmBtn.Component.gameObject, UIStyles.ButtonSuccess);
            confirmBtn.OnClick += ConfirmSelection;
            RegisterUIText(confirmBtn.ButtonText);
            _helpZone?.Describe(confirmBtn.Component.gameObject,
                "Confirm the selected source and target languages and continue.");
        }

        private void UpdateSummary()
        {
            if (_summaryLabel == null) return;

            string target = _targetDropdown?.SelectedValue;
            string source = _sourceDropdown?.SelectedValue ?? "English";

            if (!string.IsNullOrEmpty(target))
            {
                _summaryLabel.text = $"{source} → {target}";
                _summaryLabel.color = UIStyles.StatusSuccess;
            }
            else
            {
                SetDynamicText(_summaryLabel, "Select a target language");
                _summaryLabel.color = UIStyles.TextMuted;
            }
        }

        private void ConfirmSelection()
        {
            string target = _targetDropdown?.SelectedValue;

            if (string.IsNullOrEmpty(target))
            {
                SetDynamicText(_summaryLabel, "Please select a target language!");
                _summaryLabel.color = UIStyles.StatusError;
                return;
            }

            _onLanguagesSelected?.Invoke(_sourceDropdown.SelectedValue, target);
            SetActive(false);
        }
    }
}
