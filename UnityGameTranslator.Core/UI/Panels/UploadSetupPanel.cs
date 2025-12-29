using System;
using System.Collections.Generic;
using System.Linq;
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
        /// Calculate confidence score for a game search result.
        /// Higher score = more likely to be the correct game.
        /// </summary>
        private int CalculateConfidence(GameApiInfo game)
        {
            int score = 0;
            var currentGame = TranslatorCore.CurrentGame;

            // steam_id match with detected game: +50 points
            if (!string.IsNullOrEmpty(game.SteamId) &&
                currentGame != null &&
                !string.IsNullOrEmpty(currentGame.steam_id) &&
                game.SteamId == currentGame.steam_id)
            {
                score += 50;
            }

            // source == "local" (has translations): +30 points
            if (game.Source == "local")
            {
                score += 30;
            }
            // source == "steam": +20 points
            else if (game.Source == "steam")
            {
                score += 20;
            }

            // Name matching
            if (currentGame != null && !string.IsNullOrEmpty(currentGame.name))
            {
                string detectedName = currentGame.name.ToLowerInvariant();
                string resultName = game.Name?.ToLowerInvariant() ?? "";

                // Exact name match: +20 points
                if (detectedName == resultName)
                {
                    score += 20;
                }
                // Partial name match (contains): +5 points
                else if (resultName.Contains(detectedName) || detectedName.Contains(resultName))
                {
                    score += 5;
                }
            }

            return score;
        }

        /// <summary>
        /// Get background color based on confidence score.
        /// </summary>
        private Color GetConfidenceColor(int score)
        {
            if (score >= 50)
                return UIStyles.StatusSuccess; // Green - high confidence
            else if (score >= 20)
                return UIStyles.StatusWarning; // Yellow - medium confidence
            else
                return UIStyles.ItemBackground; // Default - low confidence
        }

        /// <summary>
        /// Get user-friendly display name for source.
        /// </summary>
        private string GetSourceDisplayName(string source)
        {
            switch (source?.ToLowerInvariant())
            {
                case "local": return "catalog"; // Already in our database with translations
                case "steam": return "steam";
                case "igdb": return "igdb";
                case "rawg": return "rawg";
                default: return source ?? "";
            }
        }

        /// <summary>
        /// Show the panel for new upload setup.
        /// </summary>
        public void ShowForSetup(Action<GameInfo, string, string> onComplete)
        {
            _onSetupComplete = onComplete;

            // For NEW uploads, game MUST be confirmed by user
            // Clear any previous selection - user must select from search results
            _selectedGame = null;

            // Pre-select languages from Options if already configured (not "auto")
            string configSource = TranslatorCore.Config.source_language;
            string configTarget = TranslatorCore.Config.target_language;

            // Source: use config if not auto, otherwise leave empty for user to select
            if (!string.IsNullOrEmpty(configSource) && configSource.ToLower() != "auto")
            {
                _sourceSelector.SelectedLanguage = configSource;
            }

            // Target: use config if not auto, otherwise fall back to system language
            if (!string.IsNullOrEmpty(configTarget) && configTarget.ToLower() != "auto")
            {
                _targetSelector.SelectedLanguage = configTarget;
            }
            else
            {
                string systemLang = LanguageHelper.GetSystemLanguageName();
                _targetSelector.SelectedLanguage = systemLang;
            }

            // Reset search state
            _gameSearchResults = null;
            ClearGameResults();

            RefreshGameDisplay();
            UpdateValidation();

            SetActive(true);

            // Pre-search with detected game name to help user confirm the correct game
            var currentGame = TranslatorCore.CurrentGame;
            if (currentGame != null && !string.IsNullOrEmpty(currentGame.name) && _gameSearchInput != null)
            {
                _gameSearchInput.Text = currentGame.name;
                // Trigger search automatically
                PerformGameSearch();
            }
        }

        protected override void ConstructPanelContent()
        {
            // Initialize components (must be here, not in constructor - base calls ConstructUI first)
            var languages = LanguageHelper.GetLanguageNames();
            // No default for source - must be explicitly selected (required field)
            _sourceSelector = new LanguageSelector("Source", languages, "", 100);
            _targetSelector = new LanguageSelector("Target", languages, "", 120);

            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            var card = CreateAdaptiveCard(scrollContent, "SetupCard", PanelWidth - 40);

            var title = CreateTitle(card, "Title", "New Upload Setup");
            RegisterUIText(title);

            var instructions = CreateSmallLabel(card, "Instructions", "Configure your translation before uploading:");
            RegisterUIText(instructions);

            UIStyles.CreateSpacer(card, 10);

            // === GAME SECTION ===
            var gameTitle = UIStyles.CreateSectionTitle(card, "GameTitle", "1. Game");
            RegisterUIText(gameTitle);

            var gameBox = CreateSection(card, "GameBox");

            // Current game display
            var gameRow = UIStyles.CreateFormRow(gameBox, "GameRow", UIStyles.RowHeightNormal, 5);

            _gameDisplayLabel = UIFactory.CreateLabel(gameRow, "GameName", "Unknown", TextAnchor.MiddleLeft);
            _gameDisplayLabel.fontStyle = FontStyle.Bold;
            _gameDisplayLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_gameDisplayLabel.gameObject, flexibleWidth: 9999);
            RegisterExcluded(_gameDisplayLabel); // Game names should not be translated

            _gameSourceLabel = UIFactory.CreateLabel(gameRow, "GameSource", "(auto-detected)", TextAnchor.MiddleRight);
            _gameSourceLabel.fontStyle = FontStyle.Italic;
            _gameSourceLabel.fontSize = UIStyles.FontSizeSmall;
            _gameSourceLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_gameSourceLabel.gameObject, minWidth: 100);
            RegisterUIText(_gameSourceLabel);

            // Game search row
            var searchRow = UIStyles.CreateFormRow(gameBox, "SearchRow", UIStyles.RowHeightLarge, 5);

            _gameSearchInput = UIFactory.CreateInputField(searchRow, "GameSearchInput", "Search for a game...");
            UIFactory.SetLayoutElement(_gameSearchInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_gameSearchInput.Component.gameObject, UIStyles.InputBackground);

            _gameSearchBtn = UIFactory.CreateButton(searchRow, "SearchBtn", "Search");
            UIFactory.SetLayoutElement(_gameSearchBtn.Component.gameObject, minWidth: 70, minHeight: UIStyles.InputHeight);
            _gameSearchBtn.OnClick += PerformGameSearch;
            RegisterUIText(_gameSearchBtn.ButtonText);

            // Search status
            _gameSearchStatus = UIFactory.CreateLabel(gameBox, "SearchStatus", "", TextAnchor.MiddleLeft);
            _gameSearchStatus.fontSize = UIStyles.FontSizeSmall;
            _gameSearchStatus.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_gameSearchStatus.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(_gameSearchStatus);

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
            var sourceTitle = UIStyles.CreateSectionTitle(card, "SourceTitle", "2. Source Language (original game language)");
            RegisterUIText(sourceTitle);
            _sourceSelector.CreateUI(card, (lang) => UpdateValidation());

            UIStyles.CreateSpacer(card, 10);

            // === TARGET LANGUAGE SECTION ===
            var targetTitle = UIStyles.CreateSectionTitle(card, "TargetTitle", "3. Target Language (your translation)");
            RegisterUIText(targetTitle);
            _targetSelector.CreateUI(card, (lang) => UpdateValidation());

            UIStyles.CreateSpacer(card, 10);

            // === VALIDATION ===
            _validationLabel = UIFactory.CreateLabel(card, "Validation", "", TextAnchor.MiddleCenter);
            _validationLabel.fontSize = UIStyles.FontSizeNormal;
            _validationLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_validationLabel.gameObject, minHeight: UIStyles.RowHeightLarge);
            RegisterExcluded(_validationLabel); // Contains game/language names

            // === BUTTONS ===
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);
            RegisterUIText(cancelBtn.ButtonText);

            _continueBtn = CreatePrimaryButton(buttonRow, "ContinueBtn", "Continue to Upload");
            UIStyles.SetBackground(_continueBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _continueBtn.OnClick += OnContinue;
            RegisterUIText(_continueBtn.ButtonText);

            // Initial population
            RefreshGameDisplay();
            UpdateValidation();
        }

        private void RefreshGameDisplay()
        {
            if (_gameDisplayLabel == null) return;

            if (_selectedGame != null && !string.IsNullOrEmpty(_selectedGame.name))
            {
                // Game confirmed by user selection
                _gameDisplayLabel.text = _selectedGame.name;
                _gameDisplayLabel.color = UIStyles.StatusSuccess;
                _gameSourceLabel.text = "✓ confirmed";
                _gameSourceLabel.color = UIStyles.StatusSuccess;
            }
            else
            {
                // Show detected game but require confirmation
                var detected = TranslatorCore.CurrentGame;
                if (detected != null && !string.IsNullOrEmpty(detected.name))
                {
                    _gameDisplayLabel.text = detected.name;
                    _gameDisplayLabel.color = UIStyles.StatusWarning;
                    _gameSourceLabel.text = "⚠ confirm below";
                    _gameSourceLabel.color = UIStyles.StatusWarning;
                }
                else
                {
                    _gameDisplayLabel.text = "No game detected";
                    _gameDisplayLabel.color = UIStyles.StatusWarning;
                    _gameSourceLabel.text = "- please search";
                    _gameSourceLabel.color = UIStyles.TextMuted;
                }
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

            // Calculate confidence for each result and sort by confidence (highest first)
            var sortedResults = _gameSearchResults
                .Select(g => new { Game = g, Confidence = CalculateConfidence(g) })
                .OrderByDescending(x => x.Confidence)
                .ToList();

            foreach (var item in sortedResults)
            {
                var game = item.Game;
                int confidence = item.Confidence;

                var btn = UIFactory.CreateButton(_gameResultsContent, $"Game_{game.Id}", game.Name);
                UIFactory.SetLayoutElement(btn.Component.gameObject, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);

                // Use confidence-based background color
                Color bgColor = GetConfidenceColor(confidence);
                UIStyles.SetBackground(btn.Component.gameObject, bgColor);

                // Build display text with source and confidence indicator
                string displayText = game.Name;
                if (!string.IsNullOrEmpty(game.Source))
                {
                    string sourceDisplay = GetSourceDisplayName(game.Source);
                    displayText += $" [{sourceDisplay}]";
                }

                // Add confidence indicator
                if (confidence >= 50)
                    displayText += " ★"; // High confidence
                else if (confidence >= 20)
                    displayText += " ☆"; // Medium confidence

                btn.ButtonText.text = displayText;

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

            // For NEW uploads, game MUST be confirmed by selecting from search results
            // No fallback to auto-detected game
            var game = _selectedGame;
            bool hasGame = game != null && !string.IsNullOrEmpty(game.name);

            // Use IsValidSelection() to ensure language is from the list, not just non-empty
            bool hasValidSource = _sourceSelector?.IsValidSelection() ?? false;
            bool hasValidTarget = _targetSelector?.IsValidSelection() ?? false;

            string source = _sourceSelector?.SelectedLanguage;
            string target = _targetSelector?.SelectedLanguage;
            bool differentLangs = hasValidSource && hasValidTarget && source != target;

            if (!hasGame)
            {
                _validationLabel.text = "Please select a game";
                _validationLabel.color = UIStyles.StatusWarning;
                _continueBtn.Component.interactable = false;
            }
            else if (!hasValidSource)
            {
                _validationLabel.text = "Please select a source language (original game language)";
                _validationLabel.color = UIStyles.StatusWarning;
                _continueBtn.Component.interactable = false;
            }
            else if (!hasValidTarget)
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
            // For NEW uploads, game MUST be confirmed via _selectedGame
            if (_selectedGame == null)
            {
                TranslatorCore.LogWarning("[UploadSetup] OnContinue called without selected game");
                return;
            }

            // Update CurrentGame with user's confirmed selection
            TranslatorCore.CurrentGame = _selectedGame;

            _onSetupComplete?.Invoke(_selectedGame, _sourceSelector.SelectedLanguage, _targetSelector.SelectedLanguage);
            SetActive(false);
        }
    }
}
