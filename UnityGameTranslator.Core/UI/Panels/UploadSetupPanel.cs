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

        /// <summary>
        /// The list answer the selected game came from, as a publication sends it (`game_pick`) —
        /// null when the game was taken as detected (no answer of the site named it).
        /// </summary>
        private GameChoice _selectedPick = null;
        private List<GameApiInfo> _gameSearchResults = null;

        // Callback
        private Action<GameInfo, GameChoice, string, string, bool> _onSetupComplete;

        /// <summary>
        /// 🔴 **Two uses, one screen** (user, 2026-10-05: Change opens "le même écran qu'à la
        /// publication"). Opened by Change, only the game is asked, and the answer is kept as the
        /// game confirmed in this game (`game_choice`); before a first publication, the languages
        /// too, and the answers go to the upload.
        /// </summary>
        private bool _gameOnly;
        private Action<GameChoice> _onGameChosen;

        private Host LanguagesSection => _screen.Host("LanguagesSection");

        // The covers on screen — dropped with the list they belong to (ForgetCovers): shown while
        // the list is, never kept (user, 2026-10-04: "un moyen d'identification éphémère"). The
        // counter drops a cover that arrives for an older list.
        private readonly List<ImageHandle> _covers = new List<ImageHandle>();
        private int _coversAsked;

        // What the site said about the picked game being for adults only — null until it answered
        // about THIS game (analyse/adult-declaration-at-publish.md). The counter drops an answer
        // that comes back after another game was picked.
        private GameAdultRating _adultRating;
        private int _adultAsked;

        private ToggleHandle AdultBox => _screen.Toggle("AdultBox");
        private LabelHandle AdultNote => _screen.Label("AdultNote");

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

        // Under each dropdown, saying why it does not open once the file states that language
        private LabelHandle SourceSettledHint => _screen.Label("SourceSettled");
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
        public void ShowForSetup(Action<GameInfo, GameChoice, string, string, bool> onComplete)
        {
            _onSetupComplete = onComplete;
            _onGameChosen = null;
            SetMode(gameOnly: false);

            // For NEW uploads, game MUST be confirmed by user. The previous selection is cleared
            // below, once the screen is laid out (SelectGame(null)).

            // Pre-select languages from Options if already configured (not "auto")
            string configSource = TranslatorCore.Config.source_language;
            string configTarget = TranslatorCore.Config.target_language;

            // 🔴 **The source is a question only until the file states it** (2026-09-15). With
            // strict source detection on and a source set, the file carries its source language
            // from its first line — every line it holds was written against it, and changing it
            // here would send a file whose lines say otherwise. Options already shows it settled;
            // this screen went on asking. Same reading as the target below: from the FILE.
            //
            // Otherwise — "auto" means "detect", a working mode — the source only becomes a value
            // when somebody declares it, which is now. And one named in Options while the file
            // held lines is settled already (TranslationLanguages.SourceLocked, 2026-10-04): the
            // file's or, before it is written back, the configuration's — EffectiveSourceLanguage.
            bool sourceSettled = TranslatorCore.SourceLanguageLocked;

            if (sourceSettled)
            {
                SourceDropdown.SelectedValue = TranslatorCore.EffectiveSourceLanguage;
            }
            else if (!string.IsNullOrEmpty(configSource) && configSource.ToLower() != "auto")
            {
                SourceDropdown.SelectedValue = configSource;
            }

            SourceDropdown.SetInteractable(!sourceSettled);
            SourceSettledHint.Visible = sourceSettled;

            // 🔴 **The target is not a question: it is what the file IS.** It settled with the
            // first translated line (TranslatorCore.SettleTargetLanguageOnFirstLine) and every
            // line since is written in it — and a file with no line cannot be published at all.
            // This dropdown used to be prefilled AND open: pick another target here and the site
            // stored it while the file went on stating its own, so the next launch raised a
            // language conflict on a translation the person had just published. Same rule as
            // Options (TranslatorCore.TargetLanguageLocked): shown, and settled.
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
            ForgetCovers();
            ResultsList.Clear();

            OpenOnTheGame();
        }

        /// <summary>
        /// Change: which game this is, and nothing else. The answer is kept as the game confirmed
        /// in this game (`game_choice`) and handed to the caller; only an answer of the site's list
        /// is a choice — a game nobody can identify is none.
        /// </summary>
        public void ShowForGame(Action<GameChoice> onChosen)
        {
            _onSetupComplete = null;
            _onGameChosen = onChosen;
            SetMode(gameOnly: true);

            _gameSearchResults = null;
            ForgetCovers();
            ResultsList.Clear();

            OpenOnTheGame();
        }

        /// <summary>The words and the parts of each use; the game block is the same in both.</summary>
        private void SetMode(bool gameOnly)
        {
            _gameOnly = gameOnly;
            _screen.Say("title", gameOnly ? "Game" : "New Upload Setup");
            _screen.Say("instructions", gameOnly
                ? "Used to publish this game's translation and to find translations shared for it."
                : "Configure your translation before uploading:");
            _screen.Say("continue", gameOnly ? "Select" : "Continue to Upload");
            LanguagesSection.Visible = !gameOnly;
        }

        /// <summary>
        /// Opens on the game already confirmed in this game, shown as it is; otherwise on the
        /// detected one, looked up on the site.
        /// </summary>
        private void OpenOnTheGame()
        {
            SelectGame(null, null);
            UpdateValidation();

            SetActive(true);

            if (TranslatorCore.ConfirmedGame is GameChoice confirmed)
            {
                SelectGame(new GameInfo
                {
                    name = confirmed.Name,
                    steam_id = confirmed.Source == "steam" ? confirmed.Id : null,
                }, confirmed);
                return;
            }

            // Auto-select detected game: search by steam_id first, fall back to local detection.
            // The site decides at upload, and refuses a game nothing identifies — said before the
            // click from its answer (UpdateValidation).
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

            // Enter in the search box searches, the same act as the button beside it. Subscribed
            // here — once, at construction — because GameSearchInput is a property that re-reads
            // the handle, so wiring it anywhere that runs twice would stack handlers.
            GameSearchInput.Submitted(_ => PerformGameSearch());

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
                case "cancel": return () => { ForgetCovers(); SetActive(false); };
                case "continue": return OnContinue;
                default: return null;
            }
        }

        /// <summary>
        /// The one way the picked game changes: the display, and the question about adult content,
        /// follow it. Several paths pick a game (auto-detection, a search result, a reset); none of
        /// them may leave the box answering about the previous one.
        /// </summary>
        private void SelectGame(GameInfo game, GameChoice pick)
        {
            _selectedGame = game;
            _selectedPick = pick;
            RefreshGameDisplay();
            AskAboutAdultContent();
        }

        /// <summary>
        /// Ask the site about the game just picked. Called every time the choice changes — and the
        /// box stays hidden until the answer is about THIS game.
        /// </summary>
        private async void AskAboutAdultContent()
        {
            int asked = ++_adultAsked;
            _adultRating = null;
            RefreshAdultBox();

            var game = _selectedGame;
            if (game == null || string.IsNullOrEmpty(game.name)) return;

            // With the answer taken, as the upload will send it: the site resolves it the same way
            // — and says when nothing identifies the game.
            var rating = await ApiClient.CheckGameAdult(game.steam_id, game.PublishName(), _selectedPick?.AsPick());

            TranslatorUIManager.RunOnMainThread(() =>
            {
                if (asked != _adultAsked) return; // another game was picked meanwhile
                _adultRating = rating.Success ? rating : null;
                RefreshAdultBox();
                UpdateValidation();
            });
        }

        /// <summary>
        /// The "Adults only" box under the game, from what the site answered.
        ///
        /// 🔴 **The person is told when the game is classified, and asked only when nobody can
        /// tell** (the user, 2026-09-23). Classified by a store, a moderator or its first
        /// translation's author: shown ticked and locked, with who says so — the one question here
        /// is already answered. Not classified, and this upload adds the game: the box is open, with
        /// what ticking it does. Any other case — the game is already on the site, or the site did
        /// not answer — shows nothing: the first publisher had the say, and a guess is not an answer.
        /// </summary>
        private void RefreshAdultBox()
        {
            if (_screen == null) return;

            var rating = _adultRating;
            // Asked of a publication only: choosing which game this is declares nothing.
            bool shown = !_gameOnly && rating != null && AdultMarks.Shown(rating.Adult, rating.Declarable);

            AdultBox.Visible = shown;
            AdultNote.Visible = shown;
            if (!shown) return;

            // The box has no act: the code only reads it at Continue, so writing it wakes nothing.
            bool open = AdultMarks.Open(rating.Adult, rating.Declarable);
            AdultBox.IsOn = rating.Adult;
            AdultBox.Enabled = open;

            // The socle's sentences, the Manager's too — one fact, one wording.
            AdultNote.Say(open ? AdultMarks.WhatItDoes : AdultMarks.Source(rating.Source));
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
                        SelectGame(new GameInfo
                        {
                            name = serverGame.Name,
                            steam_id = serverGame.SteamId
                        }, ChoiceOf(serverGame));
                    }
                    else if (!_gameOnly)
                    {
                        // Nothing answers to that id on the site: taken as detected, and the site
                        // decides at upload (it refuses a game nothing identifies — said before the
                        // click from its answer). Change takes only an answer of the list.
                        SelectGame(detectedGame, null);
                    }

                    UpdateValidation();
                });
            }
            catch (Exception ex)
            {
                // Network error — fall back to local detection, and say why
                TranslatorCore.LogWarning($"[UploadSetup] Game lookup on the site failed, using local detection: {ex.Message}");
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (!_gameOnly) SelectGame(detectedGame, null);
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
            ForgetCovers();
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
            ForgetCovers();
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
                // The tone says how sure the match is: a rule, so it is set here.
                var capturedGame = game;
                var row = _screen.Instantiate("GameHit", list.Rows, act => act == "pick" ? (Action)(() => OnGameSelected(capturedGame)) : null);
                var btn = row.Button("GameBtn");
                btn.Label = GameCandidates.Row(game.Name, game.Source, confidence);
                btn.Tone = ConfidenceTone(confidence);

                // What tells it apart from a game of the same title, and its cover (2026-10-05):
                // the person can check before picking.
                var facts = row.Label("GameFacts");
                facts.Show(game.Facts ?? "");
                facts.Visible = !string.IsNullOrEmpty(game.Facts);
                LoadCover(row.Picture("Cover"), game.ImageUrl, _coversAsked);
            }

            list.Filled(anotherSubject: true);
        }

        private void OnGameSelected(GameApiInfo gameApi)
        {
            // Clear search
            _gameSearchResults = null;
            GameSearchInput.Text = "";
            GameSearchStatus.Show("");
            ForgetCovers();
            ResultsList.Clear();

            SelectGame(new GameInfo
            {
                name = gameApi.Name,
                steam_id = gameApi.SteamId
            }, ChoiceOf(gameApi));
        }

        /// <summary>
        /// A row's cover, fetched from where the site says it is (ApiClient.FetchCover: HTTPS,
        /// never with the account's token) and decoded by its box — a game that cannot decode it
        /// leaves the empty frame.
        /// </summary>
        private async void LoadCover(ImageHandle picture, string url, int asked)
        {
            if (string.IsNullOrEmpty(url)) return;

            var bytes = await ApiClient.FetchCover(url);

            TranslatorUIManager.RunOnMainThread(() =>
            {
                if (asked != _coversAsked || bytes == null) return; // another list since

                // The box decodes it and owns what it made (ImageHandle.ShowEncoded).
                if (picture.ShowEncoded(bytes)) _covers.Add(picture);
            });
        }

        /// <summary>The covers of the list being left: each box lets go of what it made.</summary>
        private void ForgetCovers()
        {
            _coversAsked++;
            foreach (var cover in _covers) cover.Clear();
            _covers.Clear();
        }

        /// <summary>A list answer as a choice (common GameCandidates.PickOf), or null when it carries no usable id.</summary>
        private static GameChoice ChoiceOf(GameApiInfo game)
        {
            var pick = GameCandidates.PickOf(game.Source, game.Id, game.SteamId);
            return pick == null ? null : new GameChoice(pick.Source, pick.Id, game.Name);
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

            if (!hasGame || (_gameOnly && _selectedPick == null))
            {
                Validation.Say("Please select a game");
                Validation.Tone = Tone.Warning;
                ContinueBtn.Enabled = false;
            }
            else if (_adultRating?.Identified == false)
            {
                // The site would refuse it (game_not_found): said before the click, in its words.
                Validation.Say(GameChoices.NotIdentified);
                Validation.Tone = Tone.Warning;
                ContinueBtn.Enabled = false;
            }
            else if (_gameOnly)
            {
                Validation.Show(game.name);
                Validation.Tone = Tone.Success;
                ContinueBtn.Enabled = true;
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

            // 🔴 **The detection is never written over** (analyse/identite-des-jeux-parcours.md): it
            // used to be, here, so the game picked replaced what the game's own files say and every
            // later upload sent the pick as if it had been read on disk. The choice now travels
            // apart — kept as the game confirmed (Change), or handed to the upload (`game_pick`).
            ForgetCovers();

            if (_gameOnly)
            {
                if (_selectedPick == null) return;
                TranslatorCore.ConfirmGame(_selectedPick);
                _onGameChosen?.Invoke(_selectedPick);
                SetActive(false);
                return;
            }

            // Only where the site offered the box, and only when ticked. A classified game shows it
            // ticked but locked — the stores already said it, nothing to declare.
            bool adultDeclared = _adultRating != null && AdultMarks.Open(_adultRating.Adult, _adultRating.Declarable)
                                 && AdultBox.IsOn;

            _onSetupComplete?.Invoke(_selectedGame, _selectedPick, SourceDropdown.SelectedValue, TargetDropdown.SelectedValue, adultDeclared);
            SetActive(false);
        }
    }
}
