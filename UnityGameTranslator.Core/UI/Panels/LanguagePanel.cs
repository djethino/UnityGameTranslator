using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Language selection panel for choosing source and target languages.
    /// </summary>
    public class LanguagePanel : TranslatorPanelBase
    {
        public override string Name => "Select Languages";
        public override int MinWidth => 500;
        public override int MinHeight => 500;
        public override int PanelWidth => 500;
        public override int PanelHeight => 500;

        // Initialize languages early as ConstructPanelContent() is called during base constructor
        private string[] _languages = LanguageHelper.GetLanguageNames();
        private string _selectedSourceLanguage = "English";
        private string _selectedTargetLanguage = "";
        private InputFieldRef _searchInput;
        private GameObject _languageListContent;
        private Text _selectedLabel;
        private Action<string, string> _onLanguagesSelected;

        public LanguagePanel(UIBase owner) : base(owner)
        {
        }

        public void ShowForSelection(Action<string, string> onSelected)
        {
            _onLanguagesSelected = onSelected;
            RefreshLanguageList();
            SetActive(true);
        }

        protected override void ConstructPanelContent()
        {
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(ContentRoot, false, false, true, true, 10, 15, 15, 15, 15);

            // Source language selection
            var sourceLabel = UIFactory.CreateLabel(ContentRoot, "SourceLabel", "Source Language (original game language):", TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(sourceLabel.gameObject, minHeight: 20);

            var sourceDropdown = CreateLanguageDropdown(ContentRoot, "SourceDropdown", _selectedSourceLanguage, (lang) =>
            {
                _selectedSourceLanguage = lang;
                UpdateSelectedLabel();
            });

            // Target language selection
            var targetLabel = UIFactory.CreateLabel(ContentRoot, "TargetLabel", "Target Language (translation language):", TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(targetLabel.gameObject, minHeight: 20);

            // Search filter
            _searchInput = UIFactory.CreateInputField(ContentRoot, "SearchInput", "Search languages...");
            UIFactory.SetLayoutElement(_searchInput.Component.gameObject, minHeight: 30, flexibleWidth: 9999);
            _searchInput.OnValueChanged += (val) => RefreshLanguageList();

            // Language list scroll view
            var scrollObj = UIFactory.CreateScrollView(ContentRoot, "LanguageScroll", out _languageListContent, out _);
            UIFactory.SetLayoutElement(scrollObj, flexibleHeight: 9999, flexibleWidth: 9999, minHeight: 200);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_languageListContent, false, false, true, true, 2, 5, 5, 5, 5);

            // Selected languages display
            _selectedLabel = UIFactory.CreateLabel(ContentRoot, "SelectedLabel", "", TextAnchor.MiddleCenter);
            _selectedLabel.fontSize = 14;
            _selectedLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_selectedLabel.gameObject, minHeight: 30);

            UpdateSelectedLabel();

            // Buttons
            var buttonRow = UIFactory.CreateHorizontalGroup(ContentRoot, "ButtonRow", false, false, true, true, 10);
            UIFactory.SetLayoutElement(buttonRow, minHeight: 40);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(buttonRow, false, false, true, true, 10, childAlignment: TextAnchor.MiddleCenter);

            var confirmBtn = UIFactory.CreateButton(buttonRow, "ConfirmBtn", "Confirm");
            UIFactory.SetLayoutElement(confirmBtn.Component.gameObject, minWidth: 100, minHeight: 35);
            confirmBtn.OnClick += ConfirmSelection;

            var cancelBtn = UIFactory.CreateButton(buttonRow, "CancelBtn", "Cancel");
            UIFactory.SetLayoutElement(cancelBtn.Component.gameObject, minWidth: 100, minHeight: 35);
            cancelBtn.OnClick += () => SetActive(false);

            RefreshLanguageList();
        }

        private GameObject CreateLanguageDropdown(GameObject parent, string name, string defaultValue, Action<string> onChanged)
        {
            var row = UIFactory.CreateHorizontalGroup(parent, $"{name}Row", false, false, true, true, 5);
            UIFactory.SetLayoutElement(row, minHeight: 30);

            // For simplicity, use buttons to cycle through languages
            // In a full implementation, you'd use a proper dropdown
            var prevBtn = UIFactory.CreateButton(row, "PrevBtn", "<");
            UIFactory.SetLayoutElement(prevBtn.Component.gameObject, minWidth: 30, minHeight: 25);

            var valueLabel = UIFactory.CreateLabel(row, "Value", defaultValue, TextAnchor.MiddleCenter);
            UIFactory.SetLayoutElement(valueLabel.gameObject, minWidth: 150, minHeight: 25);

            var nextBtn = UIFactory.CreateButton(row, "NextBtn", ">");
            UIFactory.SetLayoutElement(nextBtn.Component.gameObject, minWidth: 30, minHeight: 25);

            int currentIndex = Array.IndexOf(_languages, defaultValue);
            if (currentIndex < 0) currentIndex = 0;

            prevBtn.OnClick += () =>
            {
                currentIndex = (currentIndex - 1 + _languages.Length) % _languages.Length;
                valueLabel.text = _languages[currentIndex];
                onChanged?.Invoke(_languages[currentIndex]);
            };

            nextBtn.OnClick += () =>
            {
                currentIndex = (currentIndex + 1) % _languages.Length;
                valueLabel.text = _languages[currentIndex];
                onChanged?.Invoke(_languages[currentIndex]);
            };

            return row;
        }

        private void RefreshLanguageList()
        {
            if (_languageListContent == null) return;

            // Clear existing items
            foreach (Transform child in _languageListContent.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            string search = _searchInput?.Text?.ToLower() ?? "";

            foreach (var lang in _languages)
            {
                if (!string.IsNullOrEmpty(search) && !lang.ToLower().Contains(search))
                    continue;

                CreateLanguageRow(lang);
            }
        }

        private void CreateLanguageRow(string language)
        {
            var row = UIFactory.CreateHorizontalGroup(_languageListContent, $"Lang_{language}", false, false, true, true, 5);
            UIFactory.SetLayoutElement(row, minHeight: 30, flexibleWidth: 9999);

            var btn = UIFactory.CreateButton(row, "SelectBtn", language);
            UIFactory.SetLayoutElement(btn.Component.gameObject, flexibleWidth: 9999, minHeight: 28);

            // Highlight if selected
            if (language == _selectedTargetLanguage)
            {
                btn.Component.GetComponent<Image>().color = new Color(0.3f, 0.5f, 0.3f);
            }

            btn.OnClick += () =>
            {
                _selectedTargetLanguage = language;
                UpdateSelectedLabel();
                RefreshLanguageList();
            };
        }

        private void UpdateSelectedLabel()
        {
            if (_selectedLabel == null) return;

            if (!string.IsNullOrEmpty(_selectedTargetLanguage))
            {
                _selectedLabel.text = $"{_selectedSourceLanguage} → {_selectedTargetLanguage}";
                _selectedLabel.color = Color.green;
            }
            else
            {
                _selectedLabel.text = "Select a target language";
                _selectedLabel.color = Color.gray;
            }
        }

        private void ConfirmSelection()
        {
            if (string.IsNullOrEmpty(_selectedTargetLanguage))
            {
                _selectedLabel.text = "Please select a target language!";
                _selectedLabel.color = Color.red;
                return;
            }

            _onLanguagesSelected?.Invoke(_selectedSourceLanguage, _selectedTargetLanguage);
            SetActive(false);
        }
    }
}
