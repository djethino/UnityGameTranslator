using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Upload setup panel for NEW translations.
    /// Handles game selection/search and language selection before upload.
    /// Uses reusable LanguageSelector components.
    /// </summary>
    public class UploadSetupPanel : TranslatorPanelBase
    {
        public override string Name => "New Upload Setup";
        public override int MinWidth => 500;
        public override int MinHeight => 400;
        public override int PanelWidth => 500;
        public override int PanelHeight => 600;

        protected override int MinPanelHeight => 400;

        // Language selectors (reusable components)
        private LanguageSelector _sourceSelector;
        private LanguageSelector _targetSelector;

        // Game
        private GameInfo _selectedGame = null;
        private List<GameApiInfo> _gameSearchResults = null;

        // Callback
        private Action<GameInfo, string, string> _onSetupComplete;

        // Game UI
        private Text _gameDisplayLabel;
        private Text _gameSourceLabel;
        private InputFieldRef _gameSearchInput;
        private ButtonRef _gameSearchBtn;
        private GameObject _gameResultsContent;
        private Text _gameSearchStatus;

        // Validation
        private Text _validationLabel;
        private ButtonRef _continueBtn;

        public UploadSetupPanel(UIBase owner) : base(owner)
        {
            // Note: Components initialized in ConstructPanelContent() - base constructor calls ConstructUI() first
        }

        /// <summary>
        /// Show the panel for new upload setup.
        /// </summary>
        public void ShowForSetup(Action<GameInfo, string, string> onComplete)
        {
            _onSetupComplete = onComplete;

            // Initialize with current game or null
            _selectedGame = TranslatorCore.CurrentGame;

            // Initialize with system language as target
            string systemLang = LanguageHelper.GetSystemLanguageName();
            _targetSelector.SelectedLanguage = systemLang;

            // Reset search state
            _gameSearchResults = null;

            RefreshGameDisplay();
            UpdateValidation();

            SetActive(true);
        }

        protected override void ConstructPanelContent()
        {
            // Initialize components (must be here, not in constructor - base calls ConstructUI first)
            var languages = LanguageHelper.GetLanguageNames();
            _sourceSelector = new LanguageSelector("Source", languages, "English", 100);
            _targetSelector = new LanguageSelector("Target", languages, "", 120);

            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            var card = CreateAdaptiveCard(scrollContent, "SetupCard", PanelWidth - 40);

            CreateTitle(card, "Title", "New Upload Setup");

            CreateSmallLabel(card, "Instructions", "Configure your translation before uploading:");

            UIStyles.CreateSpacer(card, 10);

            // === GAME SECTION ===
            UIStyles.CreateSectionTitle(card, "GameTitle", "1. Game");

            var gameBox = CreateSection(card, "GameBox");

            // Current game display
            var gameRow = UIStyles.CreateFormRow(gameBox, "GameRow", UIStyles.RowHeightNormal, 5);

            _gameDisplayLabel = UIFactory.CreateLabel(gameRow, "GameName", "Unknown", TextAnchor.MiddleLeft);
            _gameDisplayLabel.fontStyle = FontStyle.Bold;
            _gameDisplayLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_gameDisplayLabel.gameObject, flexibleWidth: 9999);

            _gameSourceLabel = UIFactory.CreateLabel(gameRow, "GameSource", "(auto-detected)", TextAnchor.MiddleRight);
            _gameSourceLabel.fontStyle = FontStyle.Italic;
            _gameSourceLabel.fontSize = UIStyles.FontSizeSmall;
            _gameSourceLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_gameSourceLabel.gameObject, minWidth: 100);

            // Game search row
            var searchRow = UIStyles.CreateFormRow(gameBox, "SearchRow", UIStyles.RowHeightLarge, 5);

            _gameSearchInput = UIFactory.CreateInputField(searchRow, "GameSearchInput", "Search for a game...");
            UIFactory.SetLayoutElement(_gameSearchInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_gameSearchInput.Component.gameObject, UIStyles.InputBackground);

            _gameSearchBtn = UIFactory.CreateButton(searchRow, "SearchBtn", "Search");
            UIFactory.SetLayoutElement(_gameSearchBtn.Component.gameObject, minWidth: 70, minHeight: UIStyles.InputHeight);
            _gameSearchBtn.OnClick += PerformGameSearch;

            // Search status
            _gameSearchStatus = UIFactory.CreateLabel(gameBox, "SearchStatus", "", TextAnchor.MiddleLeft);
            _gameSearchStatus.fontSize = UIStyles.FontSizeSmall;
            _gameSearchStatus.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_gameSearchStatus.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Search results scroll
            var resultsScroll = UIFactory.CreateScrollView(gameBox, "ResultsScroll", out _gameResultsContent, out _);
            UIFactory.SetLayoutElement(resultsScroll, minHeight: 80, flexibleHeight: 0);
            UIStyles.ConfigureScrollViewNoScrollbar(resultsScroll);

            if (_gameResultsContent != null)
            {
                var resultsLayout = _gameResultsContent.GetComponent<VerticalLayoutGroup>()
                    ?? _gameResultsContent.AddComponent<VerticalLayoutGroup>();
                resultsLayout.spacing = 2;
                resultsLayout.childControlHeight = true;
                resultsLayout.childForceExpandHeight = false;
                resultsLayout.padding = new RectOffset(2, 2, 2, 2);

                var resultsFitter = _gameResultsContent.GetComponent<ContentSizeFitter>()
                    ?? _gameResultsContent.AddComponent<ContentSizeFitter>();
                resultsFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            UIStyles.SetBackground(resultsScroll, UIStyles.CardBackground);

            UIStyles.CreateSpacer(card, 10);

            // === SOURCE LANGUAGE SECTION ===
            UIStyles.CreateSectionTitle(card, "SourceTitle", "2. Source Language (original game language)");
            _sourceSelector.CreateUI(card, (lang) => UpdateValidation());

            UIStyles.CreateSpacer(card, 10);

            // === TARGET LANGUAGE SECTION ===
            UIStyles.CreateSectionTitle(card, "TargetTitle", "3. Target Language (your translation)");
            _targetSelector.CreateUI(card, (lang) => UpdateValidation());

            UIStyles.CreateSpacer(card, 10);

            // === VALIDATION ===
            _validationLabel = UIFactory.CreateLabel(card, "Validation", "", TextAnchor.MiddleCenter);
            _validationLabel.fontSize = UIStyles.FontSizeNormal;
            _validationLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_validationLabel.gameObject, minHeight: UIStyles.RowHeightLarge);

            // === BUTTONS ===
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);

            _continueBtn = CreatePrimaryButton(buttonRow, "ContinueBtn", "Continue to Upload");
            UIStyles.SetBackground(_continueBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _continueBtn.OnClick += OnContinue;

            // Initial population
            RefreshGameDisplay();
            UpdateValidation();
        }

        private void RefreshGameDisplay()
        {
            if (_gameDisplayLabel == null) return;

            var game = _selectedGame ?? TranslatorCore.CurrentGame;

            if (game != null && !string.IsNullOrEmpty(game.name))
            {
                _gameDisplayLabel.text = game.name;
                _gameDisplayLabel.color = UIStyles.TextPrimary;

                if (_selectedGame != null)
                {
                    _gameSourceLabel.text = "(selected)";
                }
                else
                {
                    _gameSourceLabel.text = "(auto-detected)";
                }
            }
            else
            {
                _gameDisplayLabel.text = "No game detected";
                _gameDisplayLabel.color = UIStyles.StatusWarning;
                _gameSourceLabel.text = "- please search";
            }

            UpdateValidation();
        }

        private async void PerformGameSearch()
        {
            string query = _gameSearchInput?.Text?.Trim();
            if (string.IsNullOrEmpty(query) || query.Length < 2)
            {
                _gameSearchStatus.text = "Enter at least 2 characters";
                _gameSearchStatus.color = UIStyles.StatusWarning;
                return;
            }

            _gameSearchBtn.Component.interactable = false;
            _gameSearchStatus.text = "Searching...";
            _gameSearchStatus.color = UIStyles.TextMuted;

            // Clear previous results
            ClearGameResults();

            try
            {
                var result = await ApiClient.SearchGamesExternal(query);

                if (result.Success && result.Games != null && result.Games.Count > 0)
                {
                    _gameSearchResults = result.Games;
                    _gameSearchStatus.text = $"Found {result.Games.Count} game(s)";
                    _gameSearchStatus.color = UIStyles.StatusSuccess;

                    PopulateGameResults();
                }
                else if (result.Success)
                {
                    _gameSearchStatus.text = "No games found";
                    _gameSearchStatus.color = UIStyles.TextMuted;
                }
                else
                {
                    _gameSearchStatus.text = $"Error: {result.Error}";
                    _gameSearchStatus.color = UIStyles.StatusError;
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[UploadSetup] Game search error: {e.Message}");
                _gameSearchStatus.text = $"Error: {e.Message}";
                _gameSearchStatus.color = UIStyles.StatusError;
            }
            finally
            {
                _gameSearchBtn.Component.interactable = true;
            }
        }

        private void ClearGameResults()
        {
            if (_gameResultsContent == null) return;

            for (int i = _gameResultsContent.transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_gameResultsContent.transform.GetChild(i).gameObject);
            }
        }

        private void PopulateGameResults()
        {
            ClearGameResults();

            if (_gameSearchResults == null || _gameResultsContent == null) return;

            foreach (var game in _gameSearchResults)
            {
                var btn = UIFactory.CreateButton(_gameResultsContent, $"Game_{game.Id}", game.Name);
                UIFactory.SetLayoutElement(btn.Component.gameObject, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);
                UIStyles.SetBackground(btn.Component.gameObject, UIStyles.ItemBackground);

                // Add source tag if available
                if (!string.IsNullOrEmpty(game.Source))
                {
                    btn.ButtonText.text = $"{game.Name} [{game.Source}]";
                }

                // Capture game in closure
                var capturedGame = game;
                btn.OnClick += () => OnGameSelected(capturedGame);
            }
        }

        private void OnGameSelected(GameApiInfo gameApi)
        {
            _selectedGame = new GameInfo
            {
                name = gameApi.Name,
                steam_id = gameApi.SteamId
            };

            // Clear search
            _gameSearchResults = null;
            _gameSearchInput.Text = "";
            _gameSearchStatus.text = "";
            ClearGameResults();

            RefreshGameDisplay();
        }

        private void UpdateValidation()
        {
            if (_validationLabel == null || _continueBtn == null) return;

            var game = _selectedGame ?? TranslatorCore.CurrentGame;
            bool hasGame = game != null && !string.IsNullOrEmpty(game.name);
            string source = _sourceSelector?.SelectedLanguage;
            string target = _targetSelector?.SelectedLanguage;
            bool hasTarget = !string.IsNullOrEmpty(target);
            bool differentLangs = source != target;

            if (!hasGame)
            {
                _validationLabel.text = "Please select a game";
                _validationLabel.color = UIStyles.StatusWarning;
                _continueBtn.Component.interactable = false;
            }
            else if (!hasTarget)
            {
                _validationLabel.text = "Please select a target language";
                _validationLabel.color = UIStyles.StatusWarning;
                _continueBtn.Component.interactable = false;
            }
            else if (!differentLangs)
            {
                _validationLabel.text = "Source and target must be different!";
                _validationLabel.color = UIStyles.StatusError;
                _continueBtn.Component.interactable = false;
            }
            else
            {
                _validationLabel.text = $"{game.name}: {source} -> {target}";
                _validationLabel.color = UIStyles.StatusSuccess;
                _continueBtn.Component.interactable = true;
            }
        }

        private void OnContinue()
        {
            var game = _selectedGame ?? TranslatorCore.CurrentGame;

            // Update CurrentGame if user selected a different one
            if (_selectedGame != null)
            {
                TranslatorCore.CurrentGame = _selectedGame;
            }

            _onSetupComplete?.Invoke(game, _sourceSelector.SelectedLanguage, _targetSelector.SelectedLanguage);
            SetActive(false);
        }
    }
}
