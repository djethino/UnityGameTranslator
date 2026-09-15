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
    ///
    /// ⚠ Its language lists are SearchableDropdown.ForLanguages, like every other language list in
    /// the mod. It used to say "LanguageSelector", a component this panel stopped using and which
    /// nothing constructed any more — it was removed on 2026-09-12 along with the two UIStyles
    /// factories that served only it.
    ///
    /// ⚠ Described in data since 2026-09-15 (<c>common/spec/screens/upload-setup.json</c>): every
    /// piece, its help sentence and the acts are the document's. What stays here is the rules —
    /// which game is shown and in which tone, what the validation line says, whether Continue is
    /// offered, the search and its rows, whether the target is settled.
    /// </summary>
    public class UploadSetupPanel : TranslatorPanelBase
    {
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("upload-setup");

        public override string Name => Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

        protected override int MinPanelHeight => Doc.MinHeight;
        protected override bool PersistWindowPreferences => Doc.Persist;
        protected override bool UseBackdrop => Doc.Backdrop;

        private BuiltScreen _screen;

        // Game
        private GameInfo _selectedGame = null;
        private List<GameApiInfo> _gameSearchResults = null;

        // Callback
        private Action<GameInfo, string, string> _onSetupComplete;

        // Contextual help
        private HelpZone _helpZone;

        private SearchableDropdown SourceDropdown => _screen.Dropdown("Source");
        private SearchableDropdown TargetDropdown => _screen.Dropdown("Target");
        private LabelHandle GameDisplay => _screen.Label("GameName");
        private LabelHandle GameSource => _screen.Label("GameSource");
        private FieldHandle GameSearchInput => _screen.Field("GameSearchInput");
        private ButtonHandle GameSearchBtn => _screen.Button("SearchBtn");
        private ScrollList ResultsList => _screen.List("ResultsScroll");
        private LabelHandle GameSearchStatus => _screen.Label("SearchStatus");
        private LabelHandle Validation => _screen.Label("Validation");
        private ButtonHandle ContinueBtn => _screen.Button("ContinueBtn");

        // Under the target dropdown, saying why it does not open once the file holds lines
        private LabelHandle TargetSettledHint => _screen.Label("TargetSettled");

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
                SourceDropdown.SelectedValue = configSource;
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
                TargetDropdown.SelectedValue = TranslatorCore.FileTargetLanguage;
            }
            else if (!string.IsNullOrEmpty(configTarget) && configTarget.ToLower() != "auto")
            {
                // A file written before it said so: the config is the same answer, one step older.
                TargetDropdown.SelectedValue = configTarget;
            }
            else
            {
                string systemLang = LanguageHelper.GetSystemLanguageName();
                TargetDropdown.SelectedValue = systemLang;
            }

            TargetDropdown.SetInteractable(!targetSettled);
            TargetSettledHint.Visible = targetSettled;

            // Reset search state
            _gameSearchResults = null;
            ResultsList.Clear();

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
                else
                {
                    // No steam_id — help user find the game via search
                    GameSearchInput.Text = currentGame.name;
                    PerformGameSearch();
                }
            }
        }

        protected override void ConstructPanelContent()
        {
            Layout(out var body, out var footer, Doc.CardWidth);

            // Contextual help bar between content and footer — the document's own resting sentence,
            // and the sentence of each piece is the document's too.
            _helpZone = CreateHelpZone(footer, Doc.Help);

            _screen = ScreenBuilder.Build(Doc, body, footer, ActOf, help: _helpZone);

            // The socle's legend for the search result markers — the same words the Manager uses.
            _screen.Say("legend", GameCandidates.Legend);

            // Initial population
            RefreshGameDisplay();
            UpdateValidation();
        }

        private Action ActOf(string act)
        {
            switch (act)
            {
                case "search": return PerformGameSearch;
                case "sourceChanged":
                case "targetChanged": return UpdateValidation;
                case "cancel": return () => SetActive(false);
                case "continue": return OnContinue;
                default: return null;
            }
        }

        private void RefreshGameDisplay()
        {
            if (_screen == null) return;

            if (_selectedGame != null && !string.IsNullOrEmpty(_selectedGame.name))
            {
                // Game confirmed by user selection
                GameDisplay.Show(_selectedGame.name);
                GameDisplay.Tone = Tone.Success;
                GameSource.Show("✓ " + Tr("confirmed"));
                GameSource.Tone = Tone.Success;
            }
            else
            {
                // Show detected game but require confirmation
                var detected = TranslatorCore.CurrentGame;
                if (detected != null && !string.IsNullOrEmpty(detected.name))
                {
                    GameDisplay.Show(detected.name);
                    GameDisplay.Tone = Tone.Warning;
                    GameSource.Show("⚠ " + Tr("confirm below"));
                    GameSource.Tone = Tone.Warning;
                }
                else
                {
                    GameDisplay.Say("No game detected");
                    GameDisplay.Tone = Tone.Warning;
                    GameSource.Show("- " + Tr("please search"));
                    GameSource.Tone = Tone.Muted;
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
            string query = GameSearchInput.Text?.Trim();
            if (string.IsNullOrEmpty(query) || query.Length < 2)
            {
                GameSearchStatus.Say("Enter at least 2 characters");
                GameSearchStatus.Tone = Tone.Warning;
                return;
            }

            GameSearchBtn.Enabled = false;
            GameSearchStatus.Say("Searching...");
            GameSearchStatus.Tone = Tone.Muted;

            // Clear previous results
            ResultsList.Clear();

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
                        GameSearchStatus.Say($"Found {games.Count} game(s)");
                        GameSearchStatus.Tone = Tone.Success;

                        PopulateGameResults();
                    }
                    else if (success)
                    {
                        GameSearchStatus.Say("No games found");
                        GameSearchStatus.Tone = Tone.Muted;
                    }
                    else
                    {
                        GameSearchStatus.Show($"Error: {error}");
                        GameSearchStatus.Tone = Tone.Error;
                    }

                    GameSearchBtn.Enabled = true;
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[UploadSetup] Game search error: {errorMsg}");
                    GameSearchStatus.Show($"Error: {errorMsg}");
                    GameSearchStatus.Tone = Tone.Error;
                    GameSearchBtn.Enabled = true;
                });
            }
        }

        private void PopulateGameResults()
        {
            var list = ResultsList;
            list.Clear();

            if (_gameSearchResults == null) return;

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
                var btn = Buttons.Create(list.Rows, $"Game_{game.Id}",
                                         GameCandidates.Row(game.Name, game.Source, confidence),
                                         tone: ConfidenceTone(confidence), size: ButtonSize.Compact,
                                         fill: Fill.Stretch, policy: TextPolicy.Excluded);

                // Capture game in closure
                var capturedGame = game;
                btn.Clicked += () => OnGameSelected(capturedGame);
            }

            list.Filled();
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
            GameSearchInput.Text = "";
            GameSearchStatus.Show("");
            ResultsList.Clear();

            RefreshGameDisplay();
        }

        private void UpdateValidation()
        {
            if (_screen == null) return;

            // For NEW uploads, game MUST be confirmed by selecting from search results
            // No fallback to auto-detected game
            var game = _selectedGame;
            bool hasGame = game != null && !string.IsNullOrEmpty(game.name);

            // Ensure language is selected (dropdown values are always from the list)
            string source = SourceDropdown.SelectedValue;
            string target = TargetDropdown.SelectedValue;
            bool hasValidSource = !string.IsNullOrEmpty(source);
            bool hasValidTarget = !string.IsNullOrEmpty(target);
            bool differentLangs = hasValidSource && hasValidTarget && source != target;

            if (!hasGame)
            {
                Validation.Say("Please select a game");
                Validation.Tone = Tone.Warning;
                ContinueBtn.Enabled = false;
            }
            else if (!hasValidSource)
            {
                Validation.Say("Please select a source language (original game language)");
                Validation.Tone = Tone.Warning;
                ContinueBtn.Enabled = false;
            }
            else if (!hasValidTarget)
            {
                Validation.Say("Please select a target language");
                Validation.Tone = Tone.Warning;
                ContinueBtn.Enabled = false;
            }
            else if (!differentLangs)
            {
                Validation.Say("Source and target must be different!");
                Validation.Tone = Tone.Error;
                ContinueBtn.Enabled = false;
            }
            else
            {
                Validation.Show($"{game.name}: {source} -> {target}");
                Validation.Tone = Tone.Success;
                ContinueBtn.Enabled = true;
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

            _onSetupComplete?.Invoke(_selectedGame, SourceDropdown.SelectedValue, TargetDropdown.SelectedValue);
            SetActive(false);
        }
    }
}
