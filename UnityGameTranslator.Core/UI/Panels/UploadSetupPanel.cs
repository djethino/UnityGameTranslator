using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Common;
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

        // Language dropdowns (reusable components)
        private SearchableDropdown _sourceDropdown;
        private SearchableDropdown _targetDropdown;

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

        // Under the target dropdown, saying why it does not open once the file holds lines
        private Text _targetSettledHint;

        // Contextual help
        private Components.HelpZone _helpZone;

        public UploadSetupPanel(UIBase owner) : base(owner)
        {
            // Note: Components initialized in ConstructPanelContent() - base constructor calls ConstructUI() first
        }

        /// <summary>
        /// How likely a search result is to be the detected game — the socle's rule, shared with
        /// the Manager since it asks the same question before a first publication.
        /// </summary>
        private int CalculateConfidence(GameApiInfo game)
        {
            var currentGame = TranslatorCore.CurrentGame;
            return GameCandidates.Confidence(game.SteamId, game.Name, game.Source,
                                             currentGame?.steam_id, currentGame?.name);
        }

        /// <summary>
        /// Get background color based on confidence score. The thresholds are the socle's, the
        /// same lines that decide the ★ and ☆ marks.
        /// </summary>
        private Color GetConfidenceColor(int score)
        {
            if (score >= GameCandidates.BestMatch)
                return UIStyles.StatusSuccess; // Green - high confidence
            else if (score >= GameCandidates.LikelyMatch)
                return UIStyles.StatusWarning; // Yellow - medium confidence
            else
                return UIStyles.ItemBackground; // Default - low confidence
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

            // Source: use config if not auto, otherwise leave empty for user to select. This is
            // the one language that IS a question here — "auto" means "detect", a working mode,
            // and the source only becomes a value when somebody declares it, which is now.
            if (!string.IsNullOrEmpty(configSource) && configSource.ToLower() != "auto")
            {
                _sourceDropdown.SelectedValue = configSource;
            }

            // 🔴 **The target is not a question: it is what the file IS.** It settled with the
            // first translated line (TranslatorCore.SettleTargetLanguageOnFirstLine) and every
            // line since is written in it — and a file with no line cannot be published at all.
            // This dropdown used to be prefilled AND open: pick another target here and the site
            // stored it while the file went on stating its own, so the next launch raised a
            // language conflict on a translation the person had just published. Same rule as
            // Options (AreLanguagesLocked): shown, and settled.
            //
            // ⚠ Read from the FILE, not the config: the config follows the file, never the other
            // way round ("the file wins", SettleLanguagesFromFile).
            bool targetSettled = Languages.IsSettled(TranslatorCore.FileTargetLanguage);

            if (targetSettled)
            {
                _targetDropdown.SelectedValue = TranslatorCore.FileTargetLanguage;
            }
            else if (!string.IsNullOrEmpty(configTarget) && configTarget.ToLower() != "auto")
            {
                // A file written before it said so: the config is the same answer, one step older.
                _targetDropdown.SelectedValue = configTarget;
            }
            else
            {
                string systemLang = LanguageHelper.GetSystemLanguageName();
                _targetDropdown.SelectedValue = systemLang;
            }

            _targetDropdown.SetInteractable(!targetSettled);
            if (_targetSettledHint != null) _targetSettledHint.gameObject.SetActive(targetSettled);

            // Reset search state
            _gameSearchResults = null;
            ClearGameResults();

            RefreshGameDisplay();
            UpdateValidation();

            SetActive(true);

            // Auto-select detected game: search by steam_id first, fall back to local detection.
            // Server creates the game on upload if it doesn't exist yet.
            var currentGame = TranslatorCore.CurrentGame;
            if (currentGame != null && !string.IsNullOrEmpty(currentGame.name))
            {
                if (!string.IsNullOrEmpty(currentGame.steam_id))
                {
                    // Search server by steam_id to get the canonical name/image if it exists
                    AutoSelectBySteamId(currentGame);
                }
                else if (_gameSearchInput != null)
                {
                    // No steam_id — help user find the game via search
                    _gameSearchInput.Text = currentGame.name;
                    PerformGameSearch();
                }
            }
        }

        protected override void ConstructPanelContent()
        {
            // Initialize components (must be here, not in constructor - base calls ConstructUI first)
            var languages = LanguageHelper.GetLanguageNames();
            // No default for source - must be explicitly selected (required field)
            _sourceDropdown = new SearchableDropdown("Source", languages, "", popupHeight: 250, showSearch: true);
            _targetDropdown = new SearchableDropdown("Target", languages, "", popupHeight: 250, showSearch: true);

            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

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
            RegisterExcluded(_gameSourceLabel);

            // Game search row
            var searchRow = UIStyles.CreateFormRow(gameBox, "SearchRow", UIStyles.RowHeightLarge, 5);

            _gameSearchInput = UIFactory.CreateInputField(searchRow, "GameSearchInput", "Search for a game...");
            UIFactory.SetLayoutElement(_gameSearchInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_gameSearchInput.Component.gameObject, UIStyles.InputBackground);
            _helpZone?.Describe(_gameSearchInput.Component.gameObject,
                "Type a game title to find it in the catalog and online databases. Use this if the detected game is wrong or missing.");

            _gameSearchBtn = UIFactory.CreateButton(searchRow, "SearchBtn", "Search");
            UIFactory.SetLayoutElement(_gameSearchBtn.Component.gameObject, minWidth: 70, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_gameSearchBtn.Component.gameObject, UIStyles.ButtonPrimary);
            _gameSearchBtn.OnClick += PerformGameSearch;
            RegisterUIText(_gameSearchBtn.ButtonText);
            _helpZone?.Describe(_gameSearchBtn.Component.gameObject,
                "Run the search for the title you typed and list the matching games below.");

            // Search status
            _gameSearchStatus = UIFactory.CreateLabel(gameBox, "SearchStatus", "", TextAnchor.MiddleLeft);
            _gameSearchStatus.fontSize = UIStyles.FontSizeSmall;
            _gameSearchStatus.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_gameSearchStatus.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterExcluded(_gameSearchStatus);

            // Legend for the search result markers
            var resultsLegend = UIStyles.CreateHint(gameBox, "ResultsLegend", GameCandidates.Legend);
            RegisterUIText(resultsLegend);

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
                resultsLayout.padding = Compat.MakeRectOffset(2, 2, 2, 2);

                var resultsFitter = _gameResultsContent.GetComponent<ContentSizeFitter>()
                    ?? _gameResultsContent.AddComponent<ContentSizeFitter>();
                resultsFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            UIStyles.SetBackground(resultsScroll, UIStyles.ViewportBackground);  // recessed list area, distinct from the card it sits in

            UIStyles.CreateSpacer(card, 10);

            // === SOURCE LANGUAGE SECTION ===
            var sourceTitle = UIStyles.CreateSectionTitle(card, "SourceTitle", "2. Source Language (original game language)");
            RegisterUIText(sourceTitle);
            var srcObj = _sourceDropdown.CreateUI(card, (lang) => UpdateValidation(), width: 200);
            _helpZone?.Describe(srcObj,
                "The language the game is written in. Pick the original text language, not your translation.");

            UIStyles.CreateSpacer(card, 10);

            // === TARGET LANGUAGE SECTION ===
            var targetTitle = UIStyles.CreateSectionTitle(card, "TargetTitle", "3. Target Language (your translation)");
            RegisterUIText(targetTitle);
            var tgtObj = _targetDropdown.CreateUI(card, (lang) => UpdateValidation(), width: 200);
            _helpZone?.Describe(tgtObj,
                "The language this translation is written in. Settled with its first line — clear the translation to change it.");

            // Why the dropdown above does not open, in the words Options uses for the same lock.
            // Shown only while it is true (ShowForSetup), which on a publishable file is always.
            _targetSettledHint = UIStyles.CreateHint(card, "TargetSettled",
                "Settled: this file already holds lines in this language.");
            RegisterUIText(_targetSettledHint);

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
            _helpZone?.Describe(_continueBtn.Component.gameObject,
                "Confirm the game and languages and move on to the upload step. Enabled once all fields are valid.");

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
                _gameSourceLabel.text = "✓ " + Tr("confirmed");
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
                    _gameSourceLabel.text = "⚠ " + Tr("confirm below");
                    _gameSourceLabel.color = UIStyles.StatusWarning;
                }
                else
                {
                    SetDynamicText(_gameDisplayLabel, "No game detected");
                    _gameDisplayLabel.color = UIStyles.StatusWarning;
                    _gameSourceLabel.text = "- " + Tr("please search");
                    _gameSourceLabel.color = UIStyles.TextMuted;
                }
            }

            UpdateValidation();
        }

        private async void AutoSelectBySteamId(GameInfo detectedGame)
        {
            try
            {
                // Search server by steam_id to get canonical info (name, image)
                var result = await ApiClient.SearchGamesExternal(null, detectedGame.steam_id);

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (result.Success && result.Games != null && result.Games.Count > 0)
                    {
                        // Game exists on server — use the server's canonical info
                        var serverGame = result.Games[0];
                        _selectedGame = new GameInfo
                        {
                            name = serverGame.Name,
                            steam_id = serverGame.SteamId
                        };
                    }
                    else
                    {
                        // Game not on server yet — use local detection.
                        // Server will create it on upload via findOrCreateGame.
                        _selectedGame = detectedGame;
                    }

                    RefreshGameDisplay();
                    UpdateValidation();
                });
            }
            catch
            {
                // Network error — fall back to local detection
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _selectedGame = detectedGame;
                    RefreshGameDisplay();
                    UpdateValidation();
                });
            }
        }

        private async void PerformGameSearch()
        {
            string query = _gameSearchInput?.Text?.Trim();
            if (string.IsNullOrEmpty(query) || query.Length < 2)
            {
                SetDynamicText(_gameSearchStatus, "Enter at least 2 characters");
                _gameSearchStatus.color = UIStyles.StatusWarning;
                return;
            }

            _gameSearchBtn.Component.interactable = false;
            SetDynamicText(_gameSearchStatus, "Searching...");
            _gameSearchStatus.color = UIStyles.TextMuted;

            // Clear previous results
            ClearGameResults();

            try
            {
                var result = await ApiClient.SearchGamesExternal(query);

                // After await, we may be on a background thread (IL2CPP issue)
                var success = result.Success;
                var games = result.Games;
                var error = result.Error;

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success && games != null && games.Count > 0)
                    {
                        _gameSearchResults = games;
                        SetDynamicText(_gameSearchStatus, $"Found {games.Count} game(s)");
                        _gameSearchStatus.color = UIStyles.StatusSuccess;

                        PopulateGameResults();
                    }
                    else if (success)
                    {
                        SetDynamicText(_gameSearchStatus, "No games found");
                        _gameSearchStatus.color = UIStyles.TextMuted;
                    }
                    else
                    {
                        _gameSearchStatus.text = $"Error: {error}";
                        _gameSearchStatus.color = UIStyles.StatusError;
                    }

                    _gameSearchBtn.Component.interactable = true;
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[UploadSetup] Game search error: {errorMsg}");
                    _gameSearchStatus.text = $"Error: {errorMsg}";
                    _gameSearchStatus.color = UIStyles.StatusError;
                    _gameSearchBtn.Component.interactable = true;
                });
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

                // Name, source in brackets, mark — the socle's row, the same one the Manager lists.
                btn.ButtonText.text = GameCandidates.Row(game.Name, game.Source, confidence);

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

            // Ensure language is selected (dropdown values are always from the list)
            string source = _sourceDropdown?.SelectedValue;
            string target = _targetDropdown?.SelectedValue;
            bool hasValidSource = !string.IsNullOrEmpty(source);
            bool hasValidTarget = !string.IsNullOrEmpty(target);
            bool differentLangs = hasValidSource && hasValidTarget && source != target;

            if (!hasGame)
            {
                SetDynamicText(_validationLabel, "Please select a game");
                _validationLabel.color = UIStyles.StatusWarning;
                _continueBtn.Component.interactable = false;
            }
            else if (!hasValidSource)
            {
                SetDynamicText(_validationLabel, "Please select a source language (original game language)");
                _validationLabel.color = UIStyles.StatusWarning;
                _continueBtn.Component.interactable = false;
            }
            else if (!hasValidTarget)
            {
                SetDynamicText(_validationLabel, "Please select a target language");
                _validationLabel.color = UIStyles.StatusWarning;
                _continueBtn.Component.interactable = false;
            }
            else if (!differentLangs)
            {
                SetDynamicText(_validationLabel, "Source and target must be different!");
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

            // ⚠ The declaration carried on every call names the game, so it has to follow when the
            // game changes here — this is the one place it can. Detection had already run when the
            // token was set, so without this a game somebody names by hand would never reach the
            // access it belongs to. The site fills an empty line and never corrects a filled one,
            // so re-declaring is free and cannot relabel anything.
            ApiClient.DeclareGame();

            _onSetupComplete?.Invoke(_selectedGame, _sourceDropdown.SelectedValue, _targetDropdown.SelectedValue);
            SetActive(false);
        }
    }
}
