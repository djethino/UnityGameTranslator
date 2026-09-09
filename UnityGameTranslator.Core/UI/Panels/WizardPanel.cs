using System;
using System.Threading.Tasks;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI.Components;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// First-run wizard panel. Guides user through initial setup.
    /// Steps: Welcome -> OnlineMode -> Hotkey -> LanguageSelection -> TranslationChoice -> AIConfig -> Complete
    ///
    /// Uses CreateScrollablePanelLayout like all other panels.
    /// Each step is a simple container inside scrollContent.
    /// </summary>
    public class WizardPanel : TranslatorPanelBase
    {
        public enum WizardStep
        {
            Welcome,
            OnlineMode,
            Hotkey,
            LanguageSelection,
            TranslationChoice,
            AIConfig,
            Complete
        }

        public override string Name => "Unity Game Translator - Setup";
        public override int MinWidth => 520;
        public override int MinHeight => 400;
        public override int PanelWidth => 520;
        public override int PanelHeight => 500;

        protected override int MinPanelHeight => 400;
        protected override bool PersistWindowPreferences => false;

        // Current step
        private WizardStep _currentStep = WizardStep.Welcome;

        // Scroll content reference
        private Host _scrollContent;

        // Step containers
        private Host _welcomeStep;
        private Host _onlineModeStep;
        private Host _hotkeyStep;
        private Host _languageSelectionStep;
        private Host _translationChoiceStep;
        private Host _aiConfigStep;
        private Host _completeStep;

        // State variables - initialized from config in ConstructPanelContent
        private bool _onlineMode;
        private bool _enableAI;
        private string _translationBackend; // "none", "llm", "google", "deepl"
        private string _aiUrl;
        private string _aiApiKey;
        private string _aiModel;
        private string _gameContext;
        private string _googleApiKey;
        private string _deeplApiKey;
        private bool _deeplUseFree;
        private string _targetLanguage;

        // Language selection
        private SearchableDropdown _targetLanguageDropdown;
        private LabelHandle _detectedLanguageLabel;

        // Hotkey capture (reusable component)
        private HotkeyCapture _hotkeyCapture;
        private LabelHandle _hotkeyDisplayLabel;

        // UI references - Translation config step
        private ToggleHandle _wizardEnableToggle;
        private SearchableDropdown _wizardBackendTypeDropdown;
        private Host _wizardBackendTypeSection;
        private Host _wizardLlmSection;
        private Host _wizardTransApiSection;
        private SearchableDropdown _wizardProviderDropdown;
        private Host _wizardGoogleSection;
        private Host _wizardDeeplSection;
        private FieldHandle _aiUrlInput;
        private FieldHandle _aiApiKeyInput;
        private SearchableDropdown _modelDropdown;
        private FieldHandle _gameContextInput;
        private LabelHandle _aiStatusLabel;
        private FieldHandle _wizardGoogleKeyInput;
        private LabelHandle _wizardGoogleStatusLabel;
        private FieldHandle _wizardDeeplKeyInput;
        private ToggleHandle _wizardDeeplFreeToggle;
        private LabelHandle _wizardDeeplStatusLabel;

        // TranslationChoice step state
        private GameInfo _detectedGame;
        private bool _isDownloading;

        // TranslationChoice UI references
        private LabelHandle _gameLabel;
        private LabelHandle _localTranslationsLabel;
        private LabelHandle _accountStatusLabel;
        private ButtonHandle _loginBtn;
        private TranslationList _translationList;
        private LabelHandle _downloadStatusLabel;
        private LabelHandle _comparisonLabel;
        private ButtonHandle _downloadBtn;
        private ButtonHandle _uploadBtn;
        private ButtonHandle _mergeBtn;
        private Host _actionButtonsRow;
        private LabelHandle _actionButtonsHint;
        private Host _onlineChoiceBox;
        private Host _offlineChoiceBox;
        private Components.HelpZone _helpZone;

        public WizardPanel(UIBase owner) : base(owner)
        {
        }

        public override void Update()
        {
            base.Update();

            // Update hotkey capture component when on hotkey step
            if (_currentStep == WizardStep.Hotkey)
            {
                _hotkeyCapture?.Update();

                // Update display label when hotkey changes
                if (_hotkeyDisplayLabel != null && !_hotkeyCapture.IsCapturing)
                {
                    string newHotkey = _hotkeyCapture.HotkeyString;
                    if (_hotkeyDisplayLabel.Value != newHotkey)
                    {
                        _hotkeyDisplayLabel.Show(newHotkey);
                        _hotkeyDisplayLabel.Tone = Tone.Accent;
                    }
                }
            }
        }

        protected override void ConstructPanelContent()
        {
            // Initialize state from existing config (for re-running wizard)
            _onlineMode = TranslatorCore.Config.online_mode;
            _enableAI = TranslatorCore.Config.enable_ai;
            _translationBackend = TranslatorCore.Config.translation_backend ?? "none";
            _aiUrl = TranslatorCore.Config.ai_url ?? Endpoints.OllamaDefault;
            _aiApiKey = TranslatorCore.Config.ai_api_key ?? "";
            _aiModel = TranslatorCore.Config.ai_model ?? "";
            _gameContext = TranslatorCore.Config.game_context ?? "";
            _googleApiKey = TranslatorCore.Config.google_api_key ?? "";
            _deeplApiKey = TranslatorCore.Config.deepl_api_key ?? "";
            _deeplUseFree = TranslatorCore.Config.deepl_use_free;

            // Initialize target language - auto-detect from system if not set or "auto"
            string configTarget = TranslatorCore.Config.target_language;
            if (string.IsNullOrEmpty(configTarget) || configTarget.ToLower() == "auto")
            {
                _targetLanguage = LanguageHelper.GetSystemLanguageName();
            }
            else
            {
                _targetLanguage = configTarget;
            }

            // Initialize components
            string existingHotkey = TranslatorCore.Config.settings_hotkey ?? "F10";
            _hotkeyCapture = new HotkeyCapture(existingHotkey);
            _translationList = new TranslationList();

            // Use centralized scroll layout - ONE scroll for the entire panel
            Layout(out _scrollContent, out var sharedButtonRow, PanelWidth - 40);

            // Contextual help bar (fixed at the bottom, above the hidden shared row)
            _helpZone = CreateHelpZone(sharedButtonRow, "Hover an element to see what it does");

            // Hide the shared button row - wizard has per-step buttons
            sharedButtonRow.Visible = false;

            // Create all step containers inside scroll content
            CreateWelcomeStep();
            CreateOnlineModeStep();
            CreateHotkeyStep();
            CreateLanguageSelectionStep();
            CreateTranslationChoiceStep();
            CreateAIConfigStep();
            CreateCompleteStep();

            ShowStep(WizardStep.Welcome);
        }

        private void CreateWelcomeStep()
        {
            _welcomeStep = Stacks.Vertical(_scrollContent, "WelcomeStep", spacing: UIStyles.ElementSpacing);

            var card = Stacks.Card(_welcomeStep, "Card", 420);

            // Mod name - never translate
            Labels.Create(card, "Title", "Welcome to Unity Game Translator!", TextRole.Title,
                          policy: TextPolicy.Excluded);

            Labels.Create(card, "Description",
                "This mod automatically translates Unity games using AI.\n\n" +
                "You can either:\n" +
                "• Download community translations from our website\n" +
                "• Generate translations using AI (local or cloud)\n" +
                "• Or both!",
                TextRole.Description, minHeight: UIStyles.MultiLineLarge + 20);

            var buttonRow = Buttons.Row(_welcomeStep);
            var nextBtn = Buttons.Primary(buttonRow, "NextBtn", "Get Started →", minWidth: 160);
            nextBtn.Clicked += () => ShowStep(WizardStep.OnlineMode);
        }

        private void CreateOnlineModeStep()
        {
            _onlineModeStep = Stacks.Vertical(_scrollContent, "OnlineModeStep", spacing: UIStyles.ElementSpacing);

            var card = Stacks.Card(_onlineModeStep, "Card", 420);

            Labels.Create(card, "Title", "Online Mode", TextRole.Title);
            Labels.Create(card, "Description", "Do you want to enable online features?", TextRole.Description);

            Stacks.Spacer(card, 10);

            // Online mode option
            var onlineBox = Stacks.Section(card, "OnlineBox");
            var onlineRow = Stacks.Row(onlineBox, "OnlineRow", minHeight: UIStyles.RowHeightLarge);

            var onlineToggle = CheckBoxes.Bare(onlineRow, "OnlineToggle", initial: _onlineMode);
            onlineToggle.OnChanged((val) => { _onlineMode = val; UpdateOnlineChoiceHighlight(); });

            Labels.Create(onlineRow, "OnlineTextLabel", "Enable Online Mode", TextRole.Body, fill: Fill.Stretch)
                  .Bold = true;

            Labels.Create(onlineBox, "OnlineDesc",
                "• Download community translations\n• Share your translations\n• Check for updates",
                TextRole.Small, tone: Tone.Secondary, minHeight: UIStyles.MultiLineSmall);

            Stacks.Spacer(card, 5);

            // Offline mode option
            var offlineBox = Stacks.Section(card, "OfflineBox");
            var offlineRow = Stacks.Row(offlineBox, "OfflineRow", minHeight: UIStyles.RowHeightLarge);

            var offlineToggle = CheckBoxes.Bare(offlineRow, "OfflineToggle", initial: !_onlineMode);
            offlineToggle.OnChanged((val) =>
            {
                if (val) _onlineMode = false;
                onlineToggle.IsOn = !val;
                UpdateOnlineChoiceHighlight();
            });
            // A second listener on the online toggle, wired only once the offline one exists —
            // same order as the pair used to be built, which is what lets each answer for the
            // other without either needing to exist before the other does.
            onlineToggle.OnChanged((val) => offlineToggle.IsOn = !val);

            Labels.Create(offlineRow, "OfflineTextLabel", "Stay Offline", TextRole.Body, fill: Fill.Stretch)
                  .Bold = true;

            Labels.Create(offlineBox, "OfflineDesc",
                "• Use only local AI\n• No internet connection\n• Full privacy",
                TextRole.Small, tone: Tone.Secondary, minHeight: UIStyles.MultiLineSmall);

            var buttonRow = Buttons.Row(_onlineModeStep);
            var backBtn = Buttons.Secondary(buttonRow, "BackBtn", "← Back");
            backBtn.Clicked += () => ShowStep(WizardStep.Welcome);

            var nextBtn = Buttons.Primary(buttonRow, "NextBtn", "Continue →");
            nextBtn.Clicked += () => ShowStep(WizardStep.Hotkey);

            _onlineChoiceBox = onlineBox;
            _offlineChoiceBox = offlineBox;
            UpdateOnlineChoiceHighlight();

            _helpZone?.Describe(onlineBox,
                "The mod contacts our website to find translations for your games and tell you about updates. Nothing else is sent.");
            _helpZone?.Describe(offlineBox,
                "The mod never talks to our website. You can still translate with a local AI. Changeable anytime in Mod Options.");
        }

        /// <summary>
        /// Highlight the whole selected box so the active choice is visible at a glance
        /// (the 20px checkbox alone is easy to miss).
        /// </summary>
        private void UpdateOnlineChoiceHighlight()
        {
            if (_onlineChoiceBox == null || _offlineChoiceBox == null) return;
            Stacks.Highlight(_onlineChoiceBox, _onlineMode);
            Stacks.Highlight(_offlineChoiceBox, !_onlineMode);
        }

        private void CreateHotkeyStep()
        {
            _hotkeyStep = Stacks.Vertical(_scrollContent, "HotkeyStep", spacing: UIStyles.ElementSpacing);

            var card = Stacks.Card(_hotkeyStep, "Card", 420);

            Labels.Create(card, "Title", "Settings Hotkey", TextRole.Title);
            Labels.Create(card, "Description", "Choose a keyboard shortcut to open the translator menu",
                          TextRole.Description);

            Stacks.Spacer(card, 15);

            // Hotkey capture (reusable component)
            _hotkeyCapture.CreateUI(card, onHotkeyChanged: (hotkey) =>
            {
                if (_hotkeyDisplayLabel != null)
                {
                    _hotkeyDisplayLabel.Show(hotkey);
                    _hotkeyDisplayLabel.Tone = Tone.Accent;
                }
            }, includeDisplayLabel: false);
            _helpZone?.Describe(_hotkeyCapture.Handle,
                "Keyboard shortcut that opens the translator menu in-game. Click to record a new combination; Ctrl, Alt and Shift are supported.");

            Stacks.Spacer(card, 15);

            // Current hotkey display - Contains key names like Ctrl+F10, never translated.
            // ⚠ 18px in the original (SectionTitle+2) — SectionTitle (16) is the nearest role;
            // see the migration report for this and the "Setup Complete!" title's own 2px gap.
            _hotkeyDisplayLabel = Labels.Create(card, "HotkeyLabel", _hotkeyCapture.HotkeyString,
                                                TextRole.SectionTitle, tone: Tone.Accent, centred: true,
                                                policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightXLarge);

            var buttonRow = Buttons.Row(_hotkeyStep);

            var backBtn = Buttons.Secondary(buttonRow, "BackBtn", "← Back");
            backBtn.Clicked += () => ShowStep(WizardStep.OnlineMode);

            var nextBtn = Buttons.Primary(buttonRow, "NextBtn", "Continue →");
            nextBtn.Clicked += () => ShowStep(WizardStep.LanguageSelection);
        }

        private void CreateLanguageSelectionStep()
        {
            _languageSelectionStep = Stacks.Vertical(_scrollContent, "LanguageSelectionStep",
                                                      spacing: UIStyles.ElementSpacing);

            var card = Stacks.Card(_languageSelectionStep, "Card", 420);

            Labels.Create(card, "Title", "Translation Language", TextRole.Title);
            Labels.Create(card, "Description", "Choose the language you want games translated to",
                          TextRole.Description);

            Stacks.Spacer(card, 15);

            // Detected language info
            string systemLang = LanguageHelper.GetSystemLanguageName();
            _detectedLanguageLabel = Labels.Create(card, "DetectedLabel", $"Detected from your system: {systemLang}",
                                                   TextRole.Small, centred: true, policy: TextPolicy.Excluded,
                                                   minHeight: UIStyles.RowHeightNormal);
            _detectedLanguageLabel.Italic = true;

            Stacks.Spacer(card, 10);

            // Target language selector
            var langSection = Stacks.Section(card, "LanguageSection");

            Labels.Create(langSection, "LangLabel", "Translate games to:", TextRole.Small, tone: Tone.Secondary,
                          minHeight: UIStyles.RowHeightSmall);

            _targetLanguageDropdown = new SearchableDropdown(
                "TargetLang",
                LanguageHelper.GetLanguageNames(),
                _targetLanguage,
                popupHeight: 250,
                showSearch: true
            );
            var targetLangHost = _targetLanguageDropdown.CreateUI(langSection, (lang) => _targetLanguage = lang, width: 200);
            _helpZone?.Describe(targetLangHost,
                "Language you want games translated into. The game's original language is detected automatically.");

            Stacks.Spacer(card, 10);

            // Hint about source language
            Labels.Create(card, "HintLabel",
                "The source language (game's original language) is detected automatically.",
                TextRole.Hint, centred: true, minHeight: UIStyles.RowHeightNormal);

            // Navigation buttons
            var buttonRow = Buttons.Row(_languageSelectionStep);

            var backBtn = Buttons.Secondary(buttonRow, "BackBtn", "← Back");
            backBtn.Clicked += () => ShowStep(WizardStep.Hotkey);

            var nextBtn = Buttons.Primary(buttonRow, "NextBtn", "Continue →");
            nextBtn.Clicked += () =>
            {
                if (_onlineMode)
                    ShowStep(WizardStep.TranslationChoice);
                else
                    ShowStep(WizardStep.AIConfig);
            };
        }

        private void CreateTranslationChoiceStep()
        {
            _translationChoiceStep = Stacks.Vertical(_scrollContent, "TranslationChoiceStep",
                                                      spacing: UIStyles.ElementSpacing);

            var card = Stacks.Card(_translationChoiceStep, "Card", 460);

            Labels.Create(card, "Title", "Community Translations", TextRole.Title);

            // Game info section
            var gameSection = Stacks.Section(card, "GameSection");

            _gameLabel = Labels.Create(gameSection, "GameLabel", "Game: Detecting...", TextRole.Body,
                                       policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightNormal);
            _gameLabel.Bold = true;

            _localTranslationsLabel = Labels.Create(gameSection, "LocalLabel", "", TextRole.Small,
                                                     tone: Tone.Secondary, policy: TextPolicy.Excluded,
                                                     minHeight: UIStyles.RowHeightSmall);

            // Account status row
            _accountStatusLabel = Labels.Create(gameSection, "AccountStatus",
                "Optional: connect an account to share your translation later",
                TextRole.Caption, policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);

            _loginBtn = Buttons.Primary(gameSection, "LoginBtn", "Connect Account (optional)", minWidth: 200);
            _loginBtn.Clicked += OnLoginClicked;
            _helpZone?.Describe(_loginBtn,
                "Connect a website account so you can upload and share your translations later. Optional; downloading works without one.");

            Stacks.Spacer(card, 10);

            // Translation list (reusable component)
            // 200, like the main panel: a row of this list is about 130 pixels tall, so at 100
            // the very screen where a newcomer meets the community showed them less than one
            // card and asked them to scroll to see it. Same list, same size, same first
            // impression wherever it appears.
            _translationList.CreateUI(card, 200, onSelectionChanged: (t) =>
            {
                UpdateActionButtons();
            });
            _helpZone?.Describe(_translationList.Handle,
                "Community translations found for this game in your language. Select one to download or merge it.");

            // Comparison info (shows diff between local and selected remote)
            _comparisonLabel = Labels.Create(card, "ComparisonLabel", "", TextRole.Small, centred: true,
                                             policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightNormal);

            // Status label
            _downloadStatusLabel = Labels.Create(card, "DownloadStatus", "", TextRole.Small, centred: true,
                                                 policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);

            // Action buttons row (Download / Upload / Merge)
            _actionButtonsRow = Stacks.Row(card, "ActionBtnsRow", minHeight: UIStyles.RowHeightLarge,
                                           placement: Placement.MiddleCenter);

            _downloadBtn = Buttons.Compact(_actionButtonsRow, "DownloadBtn", "Download", ButtonTone.Primary, minWidth: 100);
            _downloadBtn.Clicked += OnDownloadClicked;
            _helpZone?.Describe(_downloadBtn,
                "Get the selected community translation and use it in your game");

            _uploadBtn = Buttons.Compact(_actionButtonsRow, "UploadBtn", "Upload", ButtonTone.Success, minWidth: 100);
            _uploadBtn.Clicked += OnUploadClicked;
            _helpZone?.Describe(_uploadBtn,
                "Share your local translation on the website");

            _mergeBtn = Buttons.Compact(_actionButtonsRow, "MergeBtn", "Merge", ButtonTone.Warning, minWidth: 100);
            _mergeBtn.Clicked += OnMergeClicked;
            _helpZone?.Describe(_mergeBtn,
                "Combine the community translation with your local texts (nothing is lost)");

            _actionButtonsHint = Labels.Create(card, "ActionBtnsHint",
                "Merge combines the community translation with the texts you already have locally", TextRole.Hint);
            _actionButtonsHint.Visible = false;

            _actionButtonsRow.Visible = false;

            // Navigation buttons
            var buttonRow = Buttons.Row(_translationChoiceStep);

            var backBtn = Buttons.Secondary(buttonRow, "BackBtn", "← Back");
            backBtn.Clicked += () => ShowStep(WizardStep.LanguageSelection);

            var nextBtn = Buttons.Primary(buttonRow, "NextBtn", "Continue →");
            nextBtn.Clicked += () => ShowStep(WizardStep.AIConfig);
        }

        private async void OnTranslationChoiceEnter()
        {
            try
            {
                // Detect game if not already done. Detection is synchronous; only the optional
                // online search below performs an await that can resume on a background thread.
                if (_detectedGame == null)
                {
                    _detectedGame = GameDetector.DetectGame();
                    if (_detectedGame != null)
                    {
                        _gameLabel.Show(Tr("Game:") + $" {_detectedGame.name}");
                        if (_onlineMode && !_translationList.IsSearching)
                        {
                            // Use the selected target language from wizard
                            string targetLang = _targetLanguage;

                            // Capture values for closure
                            var steamId = _detectedGame.steam_id;
                            var gameName = _detectedGame.name;

                            await _translationList.SearchAsync(steamId, gameName, targetLang);
                        }
                    }
                    else
                    {
                        _gameLabel.Say("Game: Unknown");
                    }
                }

                // After the SearchAsync await we may be on a background thread (IL2CPP).
                // ALL UI access from this point on (label writes, UpdateAccountStatus which
                // internally calls RecalculateSize -> coroutine -> LayoutRebuilder) must run
                // on the main thread, otherwise the IL2CPP runtime faults with
                // AccessViolationException inside LayoutRebuilder.ForceRebuildLayoutImmediate.
                // We enqueue even in the no-await branches for simplicity and safety; the
                // 1-frame delay is imperceptible.
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    // Update local translations count
                    int localCount = TranslatorCore.TranslationCache.Count;
                    if (localCount > 0)
                    {
                        _localTranslationsLabel.Say($"You already have {localCount} local translations");
                        var serverState = TranslatorCore.ServerState;
                        if (serverState != null && serverState.Exists && !string.IsNullOrEmpty(serverState.Uploader))
                        {
                            _localTranslationsLabel.Show(_localTranslationsLabel.Value + $" (synced with @{serverState.Uploader})");
                        }
                    }
                    else
                    {
                        _localTranslationsLabel.Show("");
                    }

                    UpdateAccountStatus(); // calls RecalculateSize() internally
                    UpdateActionButtons();
                });
            }
            catch (Exception _e)
            {
                TranslatorCore.LogError($"[OnTranslationChoiceEnter] {_e.GetType().Name}: {_e.Message}\n{_e.StackTrace}");
            }
        }

        /// <summary>
        /// Updates action buttons based on local/remote comparison.
        /// Shows Download, Upload, or Merge depending on the situation.
        /// </summary>
        private void UpdateActionButtons()
        {
            var selected = _translationList?.SelectedTranslation;
            int localCount = TranslatorCore.TranslationCache.Count;
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            string currentUser = TranslatorCore.Config.api_user;

            // Default: hide all
            _downloadBtn.Visible = false;
            _uploadBtn.Visible = false;
            _mergeBtn.Visible = false;
            _comparisonLabel.Show("");
            _actionButtonsRow.Visible = false;
            _actionButtonsHint.Visible = false;

            if (selected == null && localCount == 0)
            {
                // No local, no remote selected
                _comparisonLabel.Say("No translation found for your language");
                _comparisonLabel.Tone = Tone.Muted;
                return;
            }

            _actionButtonsRow.Visible = true;

            // Is the selected translation published under this account — the socle's one test.
            // (The old inline version called .Equals on an Uploader that can be null.)
            bool isOwnRemote = isLoggedIn && People.IsYou(selected?.Uploader, currentUser);

            int remoteCount = selected?.LineCount ?? 0;

            if (selected == null && localCount > 0)
            {
                // Local only, no remote
                if (isLoggedIn)
                {
                    _comparisonLabel.Say($"You have {localCount} local translations (not uploaded yet)");
                    _comparisonLabel.Tone = Tone.Success;
                    _uploadBtn.Visible = true;
                }
                else
                {
                    // Not logged in - hide button row entirely, only show message
                    _actionButtonsRow.Visible = false;
                    _comparisonLabel.Say($"You have {localCount} local translations. Login to upload!");
                    _comparisonLabel.Tone = Tone.Secondary;
                }
                return;
            }

            if (localCount == 0 && selected != null)
            {
                // Remote only, no local
                _comparisonLabel.Show(Tr($"On the server: {remoteCount} lines by") + $" @{selected.Uploader}");
                _comparisonLabel.Tone = Tone.Plain;
                _downloadBtn.Visible = true;
                return;
            }

            // Both local and remote exist - compare
            int diff = localCount - remoteCount;
            string diffText = diff > 0 ? $"+{diff}" : diff.ToString();

            if (isOwnRemote)
            {
                // Same owner - sync scenario
                _comparisonLabel.Show($"Local: {localCount} | Server (yours): {remoteCount} ({diffText})");

                if (localCount > remoteCount)
                {
                    // Local is more complete - suggest upload
                    _comparisonLabel.Tone = Tone.Success;
                    _uploadBtn.Visible = true;
                    _downloadBtn.Visible = true;
                    _mergeBtn.Visible = true;
                }
                else if (localCount < remoteCount)
                {
                    // Remote is more complete - suggest download
                    _comparisonLabel.Tone = Tone.Warning;
                    _downloadBtn.Visible = true;
                    _mergeBtn.Visible = true;
                }
                else
                {
                    // Same count - might still have differences
                    _comparisonLabel.Tone = Tone.Plain;
                    _downloadBtn.Visible = true;
                    _uploadBtn.Visible = true;
                    _mergeBtn.Visible = true;
                }
            }
            else
            {
                // Different owner - download or merge
                _comparisonLabel.Show($"Local: {localCount} | Server (@{selected.Uploader}): {remoteCount}");
                _comparisonLabel.Tone = Tone.Plain;

                _downloadBtn.Visible = true;
                if (localCount > 0)
                {
                    _mergeBtn.Visible = true;
                }
            }

            // Explain Merge whenever the button is offered
            _actionButtonsHint.Visible = _mergeBtn.Visible;
        }

        private void OnUploadClicked()
        {
            // Open upload panel
            SetActive(false);
            TranslatorUIManager.UploadSetupPanel?.ShowForSetup((game, source, target) =>
            {
                TranslatorUIManager.UploadPanel?.OpenForUpload();
            });
        }

        private async void OnMergeClicked()
        {
            try
            {
                var selected = _translationList?.SelectedTranslation;
                if (selected == null) return;

                _downloadStatusLabel.Say("Downloading for merge...");
                _downloadStatusLabel.Tone = Tone.Warning;
                SetButtonsInteractable(false);

                await TranslatorUIManager.DownloadAndMerge(selected, (success, message) =>
                {
                    if (success)
                    {
                        _downloadStatusLabel.Show(message);
                        _downloadStatusLabel.Tone = Tone.Success;
                        // Auto-advance to complete after successful merge
                        TranslatorUIManager.RunDelayed(1.5f, () => ShowStep(WizardStep.Complete));
                    }
                    else
                    {
                        _downloadStatusLabel.Show(message);
                        _downloadStatusLabel.Tone = Tone.Error;
                        SetButtonsInteractable(true);
                    }
                });

                // After await, we may be on a background thread (IL2CPP issue)
                // If MergePanel opened (conflicts), close wizard
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (TranslatorUIManager.MergePanel != null && TranslatorUIManager.MergePanel.Enabled)
                    {
                        SetActive(false);
                    }
                });
            }
            catch (Exception _e)
            {
                TranslatorCore.LogError($"[OnMergeClicked] {_e.GetType().Name}: {_e.Message}\n{_e.StackTrace}");
            }
        }

        private void SetButtonsInteractable(bool interactable)
        {
            _downloadBtn.Enabled = interactable;
            _uploadBtn.Enabled = interactable;
            _mergeBtn.Enabled = interactable;
        }

        public void UpdateAccountStatus()
        {
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);

            if (isLoggedIn)
            {
                string currentUser = TranslatorCore.Config.api_user;
                _accountStatusLabel.Show(Tr("Connected as") + $" @{currentUser}");
                _accountStatusLabel.Italic = true;
                _loginBtn.Visible = false;
            }
            else
            {
                _accountStatusLabel.Say("Optional: connect an account to share your translation later");
                _loginBtn.Visible = true;
            }

            _translationList?.Refresh();

            // Content changed, recalculate panel size
            RecalculateSize();
        }

        private void OnLoginClicked()
        {
            // Sync wizard state to Config so LoginPanel sees correct online_mode
            TranslatorCore.Config.online_mode = _onlineMode;

            TranslatorUIManager.LoginPanel?.SetActive(true);
        }

        private async void OnDownloadClicked()
        {
            try
            {
                var selected = _translationList?.SelectedTranslation;
                if (selected == null || _isDownloading) return;

                _isDownloading = true;
                _downloadStatusLabel.Say("Downloading...");
                _downloadStatusLabel.Tone = Tone.Warning;
                SetButtonsInteractable(false);

                await TranslatorUIManager.DownloadTranslation(selected, (success, message) =>
                {
                    _downloadStatusLabel.Show(message);
                    _downloadStatusLabel.Tone = success ? Tone.Success : Tone.Error;

                    if (success)
                    {
                        // Auto-advance to complete after successful download
                        TranslatorUIManager.RunDelayed(1.5f, () => ShowStep(WizardStep.Complete));
                    }
                    else
                    {
                        _isDownloading = false;
                        SetButtonsInteractable(true);
                    }
                });
            }
            catch (Exception _e)
            {
                TranslatorCore.LogError($"[OnDownloadClicked] {_e.GetType().Name}: {_e.Message}\n{_e.StackTrace}");
                _isDownloading = false;
                SetButtonsInteractable(true);
            }
        }

        private void CreateAIConfigStep()
        {
            _aiConfigStep = Stacks.Vertical(_scrollContent, "AIConfigStep", spacing: UIStyles.ElementSpacing);

            var card = Stacks.Card(_aiConfigStep, "Card", 420);

            Labels.Create(card, "Title", "Auto-Translation", TextRole.Title);
            Labels.Create(card, "Description",
                "How should texts with no translation yet be translated? (can be changed later in Mod Options)",
                TextRole.Description);

            Stacks.Spacer(card, 10);

            // Enable toggle
            var enableSection = Stacks.Section(card, "EnableSection");
            var enableRow = Stacks.Row(enableSection, "EnableRow", minHeight: UIStyles.RowHeightLarge);

            _wizardEnableToggle = CheckBoxes.Bare(enableRow, "EnableToggle", initial: _translationBackend != "none",
                                                  onChanged: OnWizardEnableChanged);
            _helpZone?.Describe(_wizardEnableToggle,
                "Automatically translate texts that have no translation yet. Turn off to use only downloaded community translations.");

            Labels.Create(enableRow, "EnableTextLabel", "Enable auto-translation", TextRole.Body, fill: Fill.Stretch)
                  .Bold = true;

            Stacks.Spacer(card, 10);

            // Backend type section (shown when enabled)
            _wizardBackendTypeSection = Stacks.Vertical(card, "BackendTypeSection", spacing: 5);

            var typeSection = Stacks.Section(_wizardBackendTypeSection, "TypeSection");
            Labels.Create(typeSection, "TypeLabel", "Type:", TextRole.Small, tone: Tone.Secondary,
                          minHeight: UIStyles.RowHeightSmall);

            string[] typeOptions = { UIStyles.BackendTypeLLM, UIStyles.BackendTypeApi };
            bool isTransApi = _translationBackend == "google" || _translationBackend == "deepl";
            _wizardBackendTypeDropdown = new SearchableDropdown("WizardType", typeOptions,
                isTransApi ? UIStyles.BackendTypeApi : UIStyles.BackendTypeLLM, 100, false);
            var typeHost = _wizardBackendTypeDropdown.CreateUI(typeSection, OnWizardTypeChanged, stretch: true);
            _helpZone?.Describe(typeHost,
                "Choose the translation backend: a local or cloud AI model, or a translation API such as Google or DeepL.");

            Stacks.Spacer(_wizardBackendTypeSection, 5);

            // === LLM SECTION ===
            _wizardLlmSection = Stacks.Vertical(_wizardBackendTypeSection, "LLMSection", spacing: 5);

            var urlSection = Stacks.Section(_wizardLlmSection, "UrlSection");
            Labels.Create(urlSection, "UrlLabel", "Server URL:", TextRole.Small, tone: Tone.Secondary,
                          policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);

            var urlRow = Stacks.Row(urlSection, "UrlRow", spacing: 5, minHeight: UIStyles.RowHeightLarge);
            _aiUrlInput = Fields.Create(urlRow, "AIUrl", Endpoints.OllamaDefault, onChanged: (val) => _aiUrl = val);
            _aiUrlInput.Text = _aiUrl;
            _helpZone?.Describe(_aiUrlInput,
                "Address of your OpenAI-compatible AI server (for example Ollama or LM Studio). Defaults to a local server on port 11434.");

            var testBtn = Buttons.Secondary(urlRow, "TestBtn", "Test", minWidth: 70);
            testBtn.Clicked += TestAIConnection;
            _helpZone?.Describe(testBtn,
                "Check that the AI server responds at the given URL. On success the model list is refreshed automatically.");

            _aiStatusLabel = Labels.Create(urlSection, "StatusLabel", "", TextRole.Small, centred: true,
                                           policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightNormal);

            var keySection = Stacks.Section(_wizardLlmSection, "KeySection");
            Labels.Create(keySection, "KeyLabel", "API Key:", TextRole.Small, tone: Tone.Secondary,
                          policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);

            _aiApiKeyInput = Fields.Create(keySection, "AIApiKey", "", FieldKind.Password,
                                          onChanged: (val) => _aiApiKey = val);
            _aiApiKeyInput.Text = _aiApiKey;
            _helpZone?.Describe(_aiApiKeyInput,
                "API key for the AI server. Leave empty for local servers that do not require authentication.");

            Labels.Create(keySection, "KeyHint", "Optional for local servers (Ollama, LM Studio)", TextRole.Hint);

            var modelSection = Stacks.Section(_wizardLlmSection, "ModelSection");
            Labels.Create(modelSection, "ModelLabel", "Model:", TextRole.Small, tone: Tone.Secondary,
                          minHeight: UIStyles.RowHeightSmall);

            var modelRow = Stacks.Row(modelSection, "ModelRow", spacing: 5, minHeight: UIStyles.RowHeightLarge);
            string[] initialModels = !string.IsNullOrEmpty(_aiModel) ? new[] { _aiModel } : new string[0];
            _modelDropdown = new SearchableDropdown("ModelDropdown", initialModels, _aiModel, 200, false);
            var modelHost = _modelDropdown.CreateUI(modelRow, (val) => _aiModel = val, stretch: true);
            _helpZone?.Describe(modelHost,
                "AI model used for translation. Use Refresh to load the models available on the server.");

            var refreshBtn = Buttons.Secondary(modelRow, "RefreshBtn", "Refresh", minWidth: 70);
            refreshBtn.Clicked += RefreshModels;
            _helpZone?.Describe(refreshBtn,
                "Query the server for its list of available models and fill the dropdown above.");

            var contextSection = Stacks.Section(_wizardLlmSection, "ContextSection");
            Labels.Create(contextSection, "ContextLabel", "Game Context (optional):", TextRole.Small,
                          tone: Tone.Secondary, minHeight: UIStyles.RowHeightSmall);

            _gameContextInput = Fields.Create(contextSection, "ContextInput", "e.g., RPG game, fantasy setting",
                                              FieldKind.Multiline, minHeight: UIStyles.MultiLineMedium,
                                              onChanged: (val) => _gameContext = val);
            _gameContextInput.Text = _gameContext;
            _helpZone?.Describe(_gameContextInput,
                "Optional hint about the game (genre, setting, tone) added to AI prompts to improve translation accuracy.");

            // === TRANSLATION API SECTION ===
            _wizardTransApiSection = Stacks.Vertical(_wizardBackendTypeSection, "TransApiSection", spacing: 5);

            var providerSection = Stacks.Section(_wizardTransApiSection, "ProviderSection");
            Labels.Create(providerSection, "ProviderLabel", "Provider:", TextRole.Small, tone: Tone.Secondary,
                          minHeight: UIStyles.RowHeightSmall);

            string[] providerOptions = { "Google Translate", "DeepL" };
            string currentProvider = _translationBackend == "deepl" ? "DeepL" : "Google Translate";
            _wizardProviderDropdown = new SearchableDropdown("WizardProvider", providerOptions, currentProvider, 100, false);
            var providerHost = _wizardProviderDropdown.CreateUI(providerSection, OnWizardProviderChanged, stretch: true);
            _helpZone?.Describe(providerHost,
                "Translation API provider to use, Google Translate or DeepL. Each needs its own API key below.");

            Stacks.Spacer(_wizardTransApiSection, 5);

            // === GOOGLE SECTION ===
            _wizardGoogleSection = Stacks.Vertical(_wizardTransApiSection, "GoogleSection", spacing: 5);

            var googleKeySection = Stacks.Section(_wizardGoogleSection, "GoogleKeySection");
            Labels.Create(googleKeySection, "GoogleKeyLabel", "Google API Key:", TextRole.Small, tone: Tone.Secondary,
                          policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);

            var googleKeyRow = Stacks.Row(googleKeySection, "GoogleKeyRow", spacing: 5, minHeight: UIStyles.RowHeightLarge);
            _wizardGoogleKeyInput = Fields.Create(googleKeyRow, "GoogleKey", "", FieldKind.Password,
                                                  onChanged: (val) => _googleApiKey = val);
            _wizardGoogleKeyInput.Text = _googleApiKey;
            _helpZone?.Describe(_wizardGoogleKeyInput,
                "API key for Google Cloud Translation. Requires a project with the Translation API enabled.");

            var googleTestBtn = Buttons.Secondary(googleKeyRow, "GoogleTestBtn", "Test", minWidth: 70);
            googleTestBtn.Clicked += WizardTestGoogle;
            _helpZone?.Describe(googleTestBtn,
                "Send a test request to verify the Google Translation API key works.");

            _wizardGoogleStatusLabel = Labels.Create(googleKeySection, "GoogleStatus", "", TextRole.Small,
                                                     centred: true, policy: TextPolicy.Excluded,
                                                     minHeight: UIStyles.RowHeightNormal);

            Labels.Create(_wizardGoogleSection, "GoogleHint",
                "Requires Google Cloud API key with Translation API enabled", TextRole.Hint);

            // === DEEPL SECTION ===
            _wizardDeeplSection = Stacks.Vertical(_wizardTransApiSection, "DeepLSection", spacing: 5);

            var deeplKeySection = Stacks.Section(_wizardDeeplSection, "DeepLKeySection");
            Labels.Create(deeplKeySection, "DeepLKeyLabel", "DeepL API Key:", TextRole.Small, tone: Tone.Secondary,
                          policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);

            var deeplKeyRow = Stacks.Row(deeplKeySection, "DeepLKeyRow", spacing: 5, minHeight: UIStyles.RowHeightLarge);
            _wizardDeeplKeyInput = Fields.Create(deeplKeyRow, "DeepLKey", "", FieldKind.Password,
                                                 onChanged: (val) => _deeplApiKey = val);
            _wizardDeeplKeyInput.Text = _deeplApiKey;
            _helpZone?.Describe(_wizardDeeplKeyInput,
                "API key for the DeepL translation API. Free and Pro keys use different endpoints, set below.");

            var deeplTestBtn = Buttons.Secondary(deeplKeyRow, "DeepLTestBtn", "Test", minWidth: 70);
            deeplTestBtn.Clicked += WizardTestDeepL;
            _helpZone?.Describe(deeplTestBtn,
                "Send a test request to verify the DeepL API key and selected plan type work.");

            _wizardDeeplStatusLabel = Labels.Create(deeplKeySection, "DeepLStatus", "", TextRole.Small,
                                                    centred: true, policy: TextPolicy.Excluded,
                                                    minHeight: UIStyles.RowHeightNormal);

            _wizardDeeplFreeToggle = CheckBoxes.Create(_wizardDeeplSection, "DeepLFreeToggle",
                "Use Free API (api-free.deepl.com)", initial: _deeplUseFree,
                onChanged: (val) => _deeplUseFree = val, tone: Tone.Secondary);
            _helpZone?.Describe(_wizardDeeplFreeToggle,
                "Use the free DeepL endpoint (api-free.deepl.com), 500k characters per month. Uncheck for a Pro API key.");

            Labels.Create(_wizardDeeplSection, "DeepLHint", "Free plan: 500k chars/month. Uncheck for Pro API.",
                          TextRole.Hint);

            // Initial visibility
            bool initEnabled = _translationBackend != "none";
            bool initIsTransApi = _translationBackend == "google" || _translationBackend == "deepl";
            _wizardBackendTypeSection.Visible = initEnabled;
            _wizardLlmSection.Visible = initEnabled && !initIsTransApi;
            _wizardTransApiSection.Visible = initEnabled && initIsTransApi;
            _wizardGoogleSection.Visible = _translationBackend == "google";
            _wizardDeeplSection.Visible = _translationBackend == "deepl";

            // Navigation buttons
            var buttonRow = Buttons.Row(_aiConfigStep);

            var backBtn = Buttons.Secondary(buttonRow, "BackBtn", "← Back");
            backBtn.Clicked += () =>
            {
                if (_onlineMode)
                    ShowStep(WizardStep.TranslationChoice);
                else
                    ShowStep(WizardStep.LanguageSelection);
            };

            var finishBtn = Buttons.Primary(buttonRow, "FinishBtn", "Finish Setup →");
            finishBtn.Clicked += () => ShowStep(WizardStep.Complete);
        }

        private void UpdateWizardBackendVisibility()
        {
            bool enabled = _wizardEnableToggle != null && _wizardEnableToggle.IsOn;
            if (_wizardBackendTypeSection != null) _wizardBackendTypeSection.Visible = enabled;

            if (!enabled)
            {
                _translationBackend = "none";
                _enableAI = false;
                return;
            }

            // If not online, force LLM (Translation APIs require internet)
            bool canUseTransApi = _onlineMode;
            _wizardBackendTypeDropdown?.SetInteractable(canUseTransApi);
            if (!canUseTransApi && _wizardBackendTypeDropdown?.SelectedValue == UIStyles.BackendTypeApi)
            {
                _wizardBackendTypeDropdown.SelectedValue = UIStyles.BackendTypeLLM;
            }

            string type = _wizardBackendTypeDropdown?.SelectedValue ?? UIStyles.BackendTypeLLM;
            bool isLLM = type == UIStyles.BackendTypeLLM;

            if (_wizardLlmSection != null) _wizardLlmSection.Visible = isLLM;
            if (_wizardTransApiSection != null) _wizardTransApiSection.Visible = !isLLM;

            if (isLLM)
            {
                _translationBackend = "llm";
                _enableAI = true;
            }
            else
            {
                string provider = _wizardProviderDropdown?.SelectedValue ?? "Google Translate";
                _translationBackend = provider == "DeepL" ? "deepl" : "google";
                _enableAI = false;

                if (_wizardGoogleSection != null) _wizardGoogleSection.Visible = provider == "Google Translate";
                if (_wizardDeeplSection != null) _wizardDeeplSection.Visible = provider == "DeepL";
            }
        }

        private void OnWizardEnableChanged(bool enabled)
        {
            UpdateWizardBackendVisibility();
            RecalculateSize();
        }

        private void OnWizardTypeChanged(string selected)
        {
            UpdateWizardBackendVisibility();
            RecalculateSize();
        }

        private void OnWizardProviderChanged(string selected)
        {
            UpdateWizardBackendVisibility();
            RecalculateSize();
        }

        private async void WizardTestGoogle()
        {
            try
            {
                if (string.IsNullOrEmpty(_googleApiKey))
                {
                    _wizardGoogleStatusLabel.Say("Enter an API key first");
                    _wizardGoogleStatusLabel.Tone = Tone.Warning;
                    return;
                }
                _wizardGoogleStatusLabel.Say("Testing...");
                _wizardGoogleStatusLabel.Tone = Tone.Secondary;

                bool success = await TranslatorCore.TestGoogleConnection(_googleApiKey);
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _wizardGoogleStatusLabel.Show(success ? "Connected!" : "Failed - check API key");
                    _wizardGoogleStatusLabel.Tone = success ? Tone.Success : Tone.Error;
                });
            }
            catch (Exception _e)
            {
                TranslatorCore.LogError($"[WizardTestGoogle] {_e.GetType().Name}: {_e.Message}\n{_e.StackTrace}");
            }
        }

        private async void WizardTestDeepL()
        {
            try
            {
                if (string.IsNullOrEmpty(_deeplApiKey))
                {
                    _wizardDeeplStatusLabel.Say("Enter an API key first");
                    _wizardDeeplStatusLabel.Tone = Tone.Warning;
                    return;
                }
                _wizardDeeplStatusLabel.Say("Testing...");
                _wizardDeeplStatusLabel.Tone = Tone.Secondary;

                bool success = await TranslatorCore.TestDeepLConnection(_deeplApiKey, _deeplUseFree);
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _wizardDeeplStatusLabel.Show(success ? "Connected!" : "Failed - check API key and plan type");
                    _wizardDeeplStatusLabel.Tone = success ? Tone.Success : Tone.Error;
                });
            }
            catch (Exception _e)
            {
                TranslatorCore.LogError($"[WizardTestDeepL] {_e.GetType().Name}: {_e.Message}\n{_e.StackTrace}");
            }
        }

        private void CreateCompleteStep()
        {
            _completeStep = Stacks.Vertical(_scrollContent, "CompleteStep", spacing: UIStyles.ElementSpacing);

            var card = Stacks.Card(_completeStep, "Card", 420);

            // Success title with accent color. ⚠ 22px in the original (Title+2) — the vocabulary's
            // Title role (20) is the nearest match; see the migration report.
            Labels.Create(card, "Title", "Setup Complete!", TextRole.Title, tone: Tone.Success);

            Stacks.Spacer(card, 15);

            Labels.Create(card, "Description",
                "You're all set!\n\n" +
                $"Press {_hotkeyCapture.HotkeyString} to open settings at any time.\n\n" +
                "The translator will automatically detect text in the game\nand translate it to your language.",
                TextRole.Description, policy: TextPolicy.Excluded, minHeight: UIStyles.MultiLineLarge);

            Stacks.Spacer(card, 20);

            // Centered finish button inside card
            var finishBtn = Buttons.Create(card, "FinishBtn", "Start Translating!", ButtonTone.Success, minWidth: 200);
            finishBtn.Clicked += FinishWizard;
            _helpZone?.Describe(finishBtn,
                "Save your setup and close the wizard. The translator starts working in-game right away.");
        }

        private void ShowStep(WizardStep step)
        {
            _currentStep = step;

            // Hide all steps
            _welcomeStep.Visible = false;
            _onlineModeStep.Visible = false;
            _hotkeyStep.Visible = false;
            _languageSelectionStep.Visible = false;
            _translationChoiceStep.Visible = false;
            _aiConfigStep.Visible = false;
            _completeStep.Visible = false;

            // Show current step
            switch (step)
            {
                case WizardStep.Welcome:
                    _welcomeStep.Visible = true;
                    break;
                case WizardStep.OnlineMode:
                    _onlineModeStep.Visible = true;
                    break;
                case WizardStep.Hotkey:
                    _hotkeyStep.Visible = true;
                    break;
                case WizardStep.LanguageSelection:
                    _languageSelectionStep.Visible = true;
                    break;
                case WizardStep.TranslationChoice:
                    _translationChoiceStep.Visible = true;
                    OnTranslationChoiceEnter();
                    break;
                case WizardStep.AIConfig:
                    _aiConfigStep.Visible = true;
                    break;
                case WizardStep.Complete:
                    _completeStep.Visible = true;
                    break;
            }

            // Recalculate panel size for new step content. RecalculateSize() itself waits a
            // frame for the layout to settle before measuring, which is what the wizard's own
            // one-frame-delay coroutine used to do by hand.
            RecalculateSize();
        }

        private async void TestAIConnection()
        {
            if (_aiStatusLabel == null) return;

            _aiStatusLabel.Say("Testing...");
            _aiStatusLabel.Tone = Tone.Warning;

            // Capture values before await
            string url = _aiUrl;
            string apiKey = _aiApiKey;

            try
            {
                bool success = await TranslatorCore.TestAIConnection(url, apiKey);

                // After await, we may be on a background thread (IL2CPP issue)
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success)
                    {
                        _aiStatusLabel.Say("Connection successful!");
                        _aiStatusLabel.Tone = Tone.Success;
                        // Auto-refresh models on successful test
                        RefreshModels();
                    }
                    else
                    {
                        _aiStatusLabel.Say("Connection failed");
                        _aiStatusLabel.Tone = Tone.Error;
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _aiStatusLabel.Show(Tr("Error:") + $" {errorMsg}");
                    _aiStatusLabel.Tone = Tone.Error;
                });
            }
        }

        private async void RefreshModels()
        {
            string url = _aiUrl;
            string apiKey = _aiApiKey;

            try
            {
                string[] models = await TranslatorCore.FetchModels(url, apiKey);

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (models.Length > 0)
                    {
                        _modelDropdown.SetOptions(models);
                        // Keep current selection if still valid
                        if (!string.IsNullOrEmpty(_aiModel) && Array.IndexOf(models, _aiModel) >= 0)
                        {
                            _modelDropdown.SelectedValue = _aiModel;
                        }
                    }
                });
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Wizard] Failed to refresh models: {e.Message}");
            }
        }

        private void FinishWizard()
        {
            // Save all settings
            TranslatorCore.Config.online_mode = _onlineMode;
            TranslatorCore.Config.settings_hotkey = _hotkeyCapture.HotkeyString;
            TranslatorCore.Config.target_language = _targetLanguage;
            TranslatorCore.Config.translation_backend = _translationBackend;
            // Picking a backend here IS asking for it to run — the wizard's own step is titled
            // "Auto-Translation" and choosing "none" is how one declines. Deliberately not
            // "== llm": that older line left Google and DeepL users with the switch off while
            // they translated anyway, which is the confusion this flag now exists to end.
            TranslatorCore.Config.enable_ai = (_translationBackend != "none");
            TranslatorCore.Config.ai_url = _aiUrl;
            TranslatorCore.Config.ai_api_key = !string.IsNullOrEmpty(_aiApiKey) ? _aiApiKey : null;
            TranslatorCore.Config.ai_model = _aiModel;
            TranslatorCore.Config.game_context = _gameContext;
            TranslatorCore.Config.google_api_key = !string.IsNullOrEmpty(_googleApiKey) ? _googleApiKey : null;
            TranslatorCore.Config.deepl_api_key = !string.IsNullOrEmpty(_deeplApiKey) ? _deeplApiKey : null;
            TranslatorCore.Config.deepl_use_free = _deeplUseFree;
            // Opens the latch the whole mod waits on (TranslatorCore.SetupCompleted): until this
            // line the tick loop has been running but deliberately not touching the game — no
            // scanning, no translating, no cache written. Saved BEFORE anything is started, so a
            // crash in between cannot leave the mod acting on a game it was never allowed to.
            TranslatorCore.Config.first_run_completed = true;
            TranslatorCore.SaveConfig();

            // Start translation worker if any backend is enabled
            if (TranslatorCore.Config.IsTranslationEnabled)
            {
                TranslatorCore.EnsureWorkerRunning();
            }

            // Everything on screen right now was met while the latch was shut, so nothing was
            // applied to it. Re-submit it all — the same pipeline the Enable Translations toggle
            // and OptionsPanel.ApplySettings use, for the same reason: text the game will never
            // re-write on its own would otherwise stay untranslated until it next changes.
            TranslatorCore.ClearProcessingCaches();
            TranslatorScanner.ForceRefreshAllText(reapplyAllScales: true);

            // What the startup would have done had the setup already been complete. The wizard is
            // the trigger, not a timer: the answers exist now, so the work happens now.
            TranslatorUIManager.TriggerStartupTasks();

            SetActive(false);
            TranslatorUIManager.ShowMain();
        }

        protected override void OnClosePanelClicked()
        {
            // Don't allow closing wizard with X button during first run
        }
    }
}
