using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Reusable language selector component with search functionality.
    /// Creates a searchable dropdown list for selecting a language.
    /// </summary>
    public class LanguageSelector
    {
        // UI elements
        private InputFieldRef _searchInput;
        private GameObject _listContent;
        private Text _selectedLabel;

        // State
        private string[] _languages;
        private string _selectedLanguage;
        private Action<string> _onLanguageChanged;

        // Configuration
        private readonly string _name;
        private readonly int _listHeight;

        /// <summary>
        /// Current selected language.
        /// </summary>
        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (_selectedLanguage != value)
                {
                    _selectedLanguage = value;
                    UpdateSelectedLabel();
                    Refresh();
                }
            }
        }

        /// <summary>
        /// Create a new language selector.
        /// </summary>
        /// <param name="name">Unique name for UI elements</param>
        /// <param name="languages">Array of language names to choose from</param>
        /// <param name="initialSelection">Initially selected language</param>
        /// <param name="listHeight">Height of the scrollable list</param>
        public LanguageSelector(string name, string[] languages, string initialSelection = "English", int listHeight = 100)
        {
            _name = name;
            _languages = languages;
            _selectedLanguage = initialSelection;
            _listHeight = listHeight;
        }

        /// <summary>
        /// Create the UI elements in the given parent.
        /// </summary>
        /// <param name="parent">Parent GameObject to add UI to</param>
        /// <param name="onLanguageChanged">Callback when selection changes</param>
        public void CreateUI(GameObject parent, Action<string> onLanguageChanged = null)
        {
            _onLanguageChanged = onLanguageChanged;

            var (_, searchInput, listContent, selectedLabel) = UIStyles.CreateLanguageSelector(parent, _name, _listHeight);
            _searchInput = searchInput;
            _listContent = listContent;
            _selectedLabel = selectedLabel;

            // Set initial state
            _selectedLabel.text = _selectedLanguage;
            _searchInput.OnValueChanged += (val) => Refresh();

            // Initial population
            Refresh();
        }

        /// <summary>
        /// Refresh the language list based on current search filter.
        /// </summary>
        public void Refresh()
        {
            if (_listContent == null) return;

            UIStyles.PopulateLanguageList(
                _listContent,
                _languages,
                _searchInput?.Text ?? "",
                _selectedLanguage,
                OnLanguageSelected
            );
        }

        /// <summary>
        /// Set the available languages.
        /// </summary>
        public void SetLanguages(string[] languages)
        {
            _languages = languages;
            Refresh();
        }

        /// <summary>
        /// Clear the search input.
        /// </summary>
        public void ClearSearch()
        {
            if (_searchInput != null)
            {
                _searchInput.Text = "";
            }
        }

        private void OnLanguageSelected(string language)
        {
            _selectedLanguage = language;
            UpdateSelectedLabel();
            Refresh();
            _onLanguageChanged?.Invoke(language);
        }

        private void UpdateSelectedLabel()
        {
            if (_selectedLabel != null)
            {
                _selectedLabel.text = _selectedLanguage;
            }
        }
    }
}
