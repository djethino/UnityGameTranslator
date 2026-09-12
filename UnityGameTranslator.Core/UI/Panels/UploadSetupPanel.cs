using System;
using System.Collections.Generic;
using System.Linq;
using UniverseLib.UI;
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
        private LabelHandle _gameDisplayLabel;
        private LabelHandle _gameSourceLabel;
        private FieldHandle _gameSearchInput;
        private ButtonHandle _gameSearchBtn;
        private ScrollList _resultsList;
        private LabelHandle _gameSearchStatus;

        // Validation
        private LabelHandle _validationLabel;
        private ButtonHandle _continueBtn;

        // Under the target dropdown, saying why it does not open once the file holds lines
        private LabelHandle _targetSettledHint;

        // Contextual help
        private HelpZone _helpZone;

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
        /// Which tone a search result's button takes. The thresholds are the socle's, the same
        /// lines that decide the ★ and ☆ marks.
        /// </summary>
        private ButtonTone ConfidenceTone(int score)
        {
            if (score >= GameCandidates.BestMatch)
                return ButtonTone.Success; // high confidence
            else if (score >= GameCandidates.LikelyMatch)
                return ButtonTone.Warning; // medium confidence
            else
                return ButtonTone.Secondary; // low confidence
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
            if (_targetSettledHint != null) _targetSettledHint.Visible = targetSettled;

            // Reset search state
            _gameSearchResults = null;
            _resultsList?.Clear();

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
            _sourceDropdown = new SearchableDropdown("Source", languages, "", popupHeight: 250);
            _targetDropdown = new SearchableDropdown("Target", languages, "", popupHeight: 250);

            Layout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            var card = Stacks.Card(scrollContent, "SetupCard", PanelWidth - 40);

            Labels.Create(card, "Title", "New Upload Setup", TextRole.Title);

            Labels.Create(card, "Instructions", "Configure your translation before uploading:", TextRole.Small);

            Stacks.Spacer(card, 10);

            // === GAME SECTION ===
            Labels.Create(card, "GameTitle", "1. Game", TextRole.SectionTitle);

            var gameBox = Stacks.Section(card, "GameBox");

            // Current game display
            var gameRow = Stacks.Row(gameBox, "GameRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);

            _gameDisplayLabel = Labels.Create(gameRow, "GameName", "Unknown", TextRole.Body,
                                              policy: TextPolicy.Dynamic, fill: Fill.Stretch);
            _gameDisplayLabel.Bold = true;

            // ⚠ Right-anchored in the original (TextAnchor.MiddleRight) rather than the role's own
            // left anchor: the vocabulary's `align:` always stretches the label to reach that edge,
            // which here would fight the name label above for the row's flexible space. Left
            // alignment is kept instead — see the migration report.
            _gameSourceLabel = Labels.Create(gameRow, "GameSource", "(auto-detected)", TextRole.Small,
                                             policy: TextPolicy.Excluded, minWidth: 100,
                                             align: Placement.MiddleRight);
            _gameSourceLabel.Italic = true;

            // Game search row
            var searchRow = Stacks.Row(gameBox, "SearchRow", spacing: 5, minHeight: UIStyles.RowHeightLarge);

            _gameSearchInput = Fields.Create(searchRow, "GameSearchInput", "Search for a game...",
                                             minHeight: UIStyles.InputHeight);
            _helpZone?.Describe(_gameSearchInput,
                "Type a game title to find it in the catalog and online databases. Use this if the detected game is wrong or missing.");

            // As tall as the field it sits beside.
            _gameSearchBtn = Buttons.Create(searchRow, "SearchBtn", "Search", ButtonTone.Primary,
                                            ButtonSize.Field, minWidth: 70);
            _gameSearchBtn.Clicked += PerformGameSearch;
            _helpZone?.Describe(_gameSearchBtn,
                "Run the search for the title you typed and list the matching games below.");

            // Search status
            _gameSearchStatus = Labels.Create(gameBox, "SearchStatus", "", TextRole.Small, policy: TextPolicy.Dynamic);

            // Legend for the search result markers
            Labels.Create(gameBox, "ResultsLegend", GameCandidates.Legend, TextRole.Hint);

            // Search results scroll. Padding was 2px on every side by hand; ScrollList's own is
            // 5px — see the migration report.
            _resultsList = ScrollList.Create(gameBox, "ResultsScroll", minHeight: 80, fillHeight: false,
                                             spacing: 2, padding: 2);

            Stacks.Spacer(card, 10);

            // === SOURCE LANGUAGE SECTION ===
            Labels.Create(card, "SourceTitle", "2. Source Language (original game language)", TextRole.SectionTitle);
            var srcHost = _sourceDropdown.CreateUI(card, (lang) => UpdateValidation(), width: 200);
            _helpZone?.Describe(srcHost,
                "The language the game is written in. Pick the original text language, not your translation.");

            Stacks.Spacer(card, 10);

            // === TARGET LANGUAGE SECTION ===
            Labels.Create(card, "TargetTitle", "3. Target Language (your translation)", TextRole.SectionTitle);
            var tgtHost = _targetDropdown.CreateUI(card, (lang) => UpdateValidation(), width: 200);
            _helpZone?.Describe(tgtHost,
                "The language this translation is written in. Settled with its first line — clear the translation to change it.");

            // Why the dropdown above does not open, in the words Options uses for the same lock.
            // Shown only while it is true (ShowForSetup), which on a publishable file is always.
            _targetSettledHint = Labels.Create(card, "TargetSettled",
                "Settled: this file already holds lines in this language.", TextRole.Hint);

            Stacks.Spacer(card, 10);

            // === VALIDATION ===
            _validationLabel = Labels.Create(card, "Validation", "", TextRole.Body, policy: TextPolicy.Dynamic,
                                             centred: true, minHeight: UIStyles.RowHeightLarge);
            _validationLabel.Bold = true;

            // === BUTTONS ===
            var cancelBtn = Buttons.Secondary(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.Clicked += () => SetActive(false);

            _continueBtn = Buttons.Primary(buttonRow, "ContinueBtn", "Continue to Upload");
            _continueBtn.Clicked += OnContinue;
            _helpZone?.Describe(_continueBtn,
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
                _gameDisplayLabel.Show(_selectedGame.name);
                _gameDisplayLabel.Tone = Tone.Success;
                _gameSourceLabel.Show("✓ " + Tr("confirmed"));
                _gameSourceLabel.Tone = Tone.Success;
            }
            else
            {
                // Show detected game but require confirmation
                var detected = TranslatorCore.CurrentGame;
                if (detected != null && !string.IsNullOrEmpty(detected.name))
                {
                    _gameDisplayLabel.Show(detected.name);
                    _gameDisplayLabel.Tone = Tone.Warning;
                    _gameSourceLabel.Show("⚠ " + Tr("confirm below"));
                    _gameSourceLabel.Tone = Tone.Warning;
                }
                else
                {
                    _gameDisplayLabel.Say("No game detected");
                    _gameDisplayLabel.Tone = Tone.Warning;
                    _gameSourceLabel.Show("- " + Tr("please search"));
                    _gameSourceLabel.Tone = Tone.Muted;
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
                _gameSearchStatus.Say("Enter at least 2 characters");
                _gameSearchStatus.Tone = Tone.Warning;
                return;
            }

            _gameSearchBtn.Enabled = false;
            _gameSearchStatus.Say("Searching...");
            _gameSearchStatus.Tone = Tone.Muted;

            // Clear previous results
            _resultsList?.Clear();

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
                        _gameSearchStatus.Say($"Found {games.Count} game(s)");
                        _gameSearchStatus.Tone = Tone.Success;

                        PopulateGameResults();
                    }
                    else if (success)
                    {
                        _gameSearchStatus.Say("No games found");
                        _gameSearchStatus.Tone = Tone.Muted;
                    }
                    else
                    {
                        _gameSearchStatus.Show($"Error: {error}");
                        _gameSearchStatus.Tone = Tone.Error;
                    }

                    _gameSearchBtn.Enabled = true;
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[UploadSetup] Game search error: {errorMsg}");
                    _gameSearchStatus.Show($"Error: {errorMsg}");
                    _gameSearchStatus.Tone = Tone.Error;
                    _gameSearchBtn.Enabled = true;
                });
            }
        }

        private void PopulateGameResults()
        {
            _resultsList?.Clear();

            if (_gameSearchResults == null || _resultsList == null) return;

            // Calculate confidence for each result and sort by confidence (highest first)
            var sortedResults = _gameSearchResults
                .Select(g => new { Game = g, Confidence = CalculateConfidence(g) })
                .OrderByDescending(x => x.Confidence)
                .ToList();

            foreach (var item in sortedResults)
            {
                var game = item.Game;
                int confidence = item.Confidence;

                // Name, source in brackets, mark — the socle's row, the same one the Manager lists.
                var btn = Buttons.Create(_resultsList.Rows, $"Game_{game.Id}",
                                         GameCandidates.Row(game.Name, game.Source, confidence),
                                         tone: ConfidenceTone(confidence), size: ButtonSize.Compact,
                                         fill: Fill.Stretch, policy: TextPolicy.Excluded);

                // Capture game in closure
                var capturedGame = game;
                btn.Clicked += () => OnGameSelected(capturedGame);
            }

            _resultsList.Filled();
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
            _gameSearchStatus.Show("");
            _resultsList?.Clear();

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
                _validationLabel.Say("Please select a game");
                _validationLabel.Tone = Tone.Warning;
                _continueBtn.Enabled = false;
            }
            else if (!hasValidSource)
            {
                _validationLabel.Say("Please select a source language (original game language)");
                _validationLabel.Tone = Tone.Warning;
                _continueBtn.Enabled = false;
            }
            else if (!hasValidTarget)
            {
                _validationLabel.Say("Please select a target language");
                _validationLabel.Tone = Tone.Warning;
                _continueBtn.Enabled = false;
            }
            else if (!differentLangs)
            {
                _validationLabel.Say("Source and target must be different!");
                _validationLabel.Tone = Tone.Error;
                _continueBtn.Enabled = false;
            }
            else
            {
                _validationLabel.Show($"{game.name}: {source} -> {target}");
                _validationLabel.Tone = Tone.Success;
                _continueBtn.Enabled = true;
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
