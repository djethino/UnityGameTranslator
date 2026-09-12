using System;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Language selection panel for choosing source and target languages.
    /// Uses reusable SearchableDropdown components.
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
        private LabelHandle _summary;

        // Contextual help bar
        private HelpZone _helpZone;

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
            _sourceDropdown = new SearchableDropdown("Source", languages, "English", popupHeight: 250);
            _targetDropdown = new SearchableDropdown("Target", languages, "", popupHeight: 250);

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

            Layout(out var body, out var footer, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(footer, "Hover an element to see what it does");

            var card = Stacks.Card(body, "LanguageCard", PanelWidth - 40);

            Labels.Create(card, "Title", "Select Languages", TextRole.Title);

            Stacks.Spacer(card, 5);

            // Source language section
            Labels.Create(card, "SourceTitle", "Source Language (original game language)", TextRole.SectionTitle);
            var source = _sourceDropdown.CreateUI(card, lang => UpdateSummary(), width: 200);
            _helpZone?.Describe(source,
                "The game's original language that the mod reads from. Pick the language the game currently displays.");

            Stacks.Spacer(card, 10);

            // Target language section
            Labels.Create(card, "TargetTitle", "Target Language (translation language)", TextRole.SectionTitle);
            var target = _targetDropdown.CreateUI(card, lang => UpdateSummary(), width: 200);
            _helpZone?.Describe(target,
                "The language you want the game translated into. The mod converts text from the source language to this one.");

            Stacks.Spacer(card, 10);

            // Summary: language names in their original form, written by the code — never translated
            _summary = Labels.Create(card, "Summary", "", TextRole.SectionTitle, centred: true,
                                     policy: TextPolicy.Dynamic, minHeight: UIStyles.RowHeightXLarge);

            UpdateSummary();

            // Buttons - in fixed footer
            var cancel = Buttons.Secondary(footer, "CancelBtn", "Cancel");
            cancel.Clicked += () => SetActive(false);

            var confirm = Buttons.Create(footer, "ConfirmBtn", "Confirm", ButtonTone.Success, minWidth: 130);
            confirm.Clicked += ConfirmSelection;
            _helpZone?.Describe(confirm, "Confirm the selected source and target languages and continue.");
        }

        private void UpdateSummary()
        {
            if (_summary == null) return;

            string target = _targetDropdown?.SelectedValue;
            string source = _sourceDropdown?.SelectedValue ?? "English";

            if (!string.IsNullOrEmpty(target))
            {
                _summary.Show($"{source} → {target}");
                _summary.Tone = Tone.Success;
            }
            else
            {
                _summary.Say("Select a target language");
                _summary.Tone = Tone.Muted;
            }
        }

        private void ConfirmSelection()
        {
            string target = _targetDropdown?.SelectedValue;

            if (string.IsNullOrEmpty(target))
            {
                _summary.Say("Please select a target language!");
                _summary.Tone = Tone.Error;
                return;
            }

            _onLanguagesSelected?.Invoke(_sourceDropdown.SelectedValue, target);
            SetActive(false);
        }
    }
}
