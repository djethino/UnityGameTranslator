using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core.UI;
using UnityGameTranslator.Core.UI.Components;

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
        private GameObject _scrollContent;

        // Step containers
        private GameObject _welcomeStep;
        private GameObject _onlineModeStep;
        private GameObject _hotkeyStep;
        private GameObject _languageSelectionStep;
        private GameObject _translationChoiceStep;
        private GameObject _aiConfigStep;
        private GameObject _completeStep;

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
        private Text _detectedLanguageLabel;

        // Hotkey capture (reusable component)
        private HotkeyCapture _hotkeyCapture;
        private Text _hotkeyDisplayLabel;

        // UI references - Translation config step
        private Toggle _wizardEnableToggle;
        private SearchableDropdown _wizardBackendTypeDropdown;
        private GameObject _wizardBackendTypeSection;
        private GameObject _wizardLlmSection;
        private GameObject _wizardTransApiSection;
        private SearchableDropdown _wizardProviderDropdown;
        private GameObject _wizardGoogleSection;
        private GameObject _wizardDeeplSection;
        private InputFieldRef _aiUrlInput;
        private InputFieldRef _aiApiKeyInput;
        private SearchableDropdown _modelDropdown;
        private InputFieldRef _gameContextInput;
        private Text _aiStatusLabel;
        private InputFieldRef _wizardGoogleKeyInput;
        private Text _wizardGoogleStatusLabel;
        private InputFieldRef _wizardDeeplKeyInput;
        private Toggle _wizardDeeplFreeToggle;
        private Text _wizardDeeplStatusLabel;

        // TranslationChoice step state
        private GameInfo _detectedGame;
        private bool _isDownloading;

        // TranslationChoice UI references
        private Text _gameLabel;
        private Text _localTranslationsLabel;
        private Text _accountStatusLabel;
        private ButtonRef _loginBtn;
        private TranslationList _translationList;
        private Text _downloadStatusLabel;
        private Text _comparisonLabel;
        private ButtonRef _downloadBtn;
        private ButtonRef _uploadBtn;
        private ButtonRef _mergeBtn;
        private GameObject _actionButtonsRow;

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
                    if (_hotkeyDisplayLabel.text != newHotkey)
                    {
                        _hotkeyDisplayLabel.text = newHotkey;
                        _hotkeyDisplayLabel.color = UIStyles.TextAccent;
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
            _aiUrl = TranslatorCore.Config.ai_url ?? "http://localhost:11434";
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
            CreateScrollablePanelLayout(out _scrollContent, out var sharedButtonRow, PanelWidth - 40);

            // Hide the shared button row - wizard has per-step buttons
            sharedButtonRow.SetActive(false);

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
            _welcomeStep = UIFactory.CreateVerticalGroup(_scrollContent, "WelcomeStep", false, false, true, true, UIStyles.ElementSpacing);
            UIFactory.SetLayoutElement(_welcomeStep, flexibleWidth: 9999);

            var card = CreateAdaptiveCard(_welcomeStep, "Card", 420);

            var title = CreateTitle(card, "Title", "Welcome to Unity Game Translator!");
            RegisterExcluded(title); // Mod name - never translate

            var desc = UIFactory.CreateLabel(card, "Description",
                "This mod automatically translates Unity games using AI.\n\n" +
                "You can either:\n" +
                "• Download community translations from our website\n" +
                "• Generate translations using AI (local or cloud)\n" +
                "• Or both!",
                TextAnchor.MiddleCenter);
            desc.fontSize = UIStyles.FontSizeNormal;
            desc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(desc.gameObject, minHeight: UIStyles.MultiLineLarge + 20);
            RegisterUIText(desc);

            var buttonRow = CreateButtonRow(_welcomeStep);
            var nextBtn = CreatePrimaryButton(buttonRow, "NextBtn", "Get Started →", 160);
            nextBtn.OnClick += () => ShowStep(WizardStep.OnlineMode);
            RegisterUIText(nextBtn.ButtonText);
        }

        private void CreateOnlineModeStep()
        {
            _onlineModeStep = UIFactory.CreateVerticalGroup(_scrollContent, "OnlineModeStep", false, false, true, true, UIStyles.ElementSpacing);
            UIFactory.SetLayoutElement(_onlineModeStep, flexibleWidth: 9999);

            var card = CreateAdaptiveCard(_onlineModeStep, "Card", 420);

            var onlineModeTitle = CreateTitle(card, "Title", "Online Mode");
            RegisterUIText(onlineModeTitle);
            var onlineModeDesc = CreateDescription(card, "Description", "Do you want to enable online features?");
            RegisterUIText(onlineModeDesc);

            UIStyles.CreateSpacer(card, 10);

            // Online mode option
            var onlineBox = CreateSection(card, "OnlineBox");
            var onlineRow = UIStyles.CreateFormRow(onlineBox, "OnlineRow", UIStyles.RowHeightLarge);

            var onlineToggleObj = UIFactory.CreateToggle(onlineRow, "OnlineToggle", out var onlineToggle, out var onlineLabel);
            onlineToggle.isOn = _onlineMode;
            onlineLabel.text = "";
            UIHelpers.AddToggleListener(onlineToggle, (val) => _onlineMode = val);
            UIFactory.SetLayoutElement(onlineToggleObj, minWidth: UIStyles.ToggleControlWidth);

            var onlineTextLabel = UIFactory.CreateLabel(onlineRow, "OnlineTextLabel", "Enable Online Mode", TextAnchor.MiddleLeft);
            onlineTextLabel.fontStyle = FontStyle.Bold;
            onlineTextLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(onlineTextLabel.gameObject, flexibleWidth: 9999);
            RegisterUIText(onlineTextLabel);

            var onlineDescLabel = UIFactory.CreateLabel(onlineBox, "OnlineDesc",
                "• Download community translations\n• Share your translations\n• Check for updates",
                TextAnchor.MiddleLeft);
            onlineDescLabel.fontSize = UIStyles.FontSizeSmall;
            onlineDescLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(onlineDescLabel.gameObject, minHeight: UIStyles.MultiLineSmall);
            RegisterUIText(onlineDescLabel);

            UIStyles.CreateSpacer(card, 5);

            // Offline mode option
            var offlineBox = CreateSection(card, "OfflineBox");
            var offlineRow = UIStyles.CreateFormRow(offlineBox, "OfflineRow", UIStyles.RowHeightLarge);

            var offlineToggleObj = UIFactory.CreateToggle(offlineRow, "OfflineToggle", out var offlineToggle, out var offlineLabel);
            offlineToggle.isOn = !_onlineMode;
            offlineLabel.text = "";
            UIHelpers.AddToggleListener(offlineToggle, (val) => { if (val) _onlineMode = false; onlineToggle.isOn = !val; });
            UIHelpers.AddToggleListener(onlineToggle, (val) => offlineToggle.isOn = !val);
            UIFactory.SetLayoutElement(offlineToggleObj, minWidth: UIStyles.ToggleControlWidth);

            var offlineTextLabel = UIFactory.CreateLabel(offlineRow, "OfflineTextLabel", "Stay Offline", TextAnchor.MiddleLeft);
            offlineTextLabel.fontStyle = FontStyle.Bold;
            offlineTextLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(offlineTextLabel.gameObject, flexibleWidth: 9999);
            RegisterUIText(offlineTextLabel);

            var offlineDescLabel = UIFactory.CreateLabel(offlineBox, "OfflineDesc",
                "• Use only local AI\n• No internet connection\n• Full privacy",
                TextAnchor.MiddleLeft);
            offlineDescLabel.fontSize = UIStyles.FontSizeSmall;
            offlineDescLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(offlineDescLabel.gameObject, minHeight: UIStyles.MultiLineSmall);
            RegisterUIText(offlineDescLabel);

            var buttonRow = CreateButtonRow(_onlineModeStep);
            var backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            backBtn.OnClick += () => ShowStep(WizardStep.Welcome);
            RegisterUIText(backBtn.ButtonText);

            var nextBtn = CreatePrimaryButton(buttonRow, "NextBtn", "Continue →");
            nextBtn.OnClick += () => ShowStep(WizardStep.Hotkey);
            RegisterUIText(nextBtn.ButtonText);
        }

        private void CreateHotkeyStep()
        {
            _hotkeyStep = UIFactory.CreateVerticalGroup(_scrollContent, "HotkeyStep", false, false, true, true, UIStyles.ElementSpacing);
            UIFactory.SetLayoutElement(_hotkeyStep, flexibleWidth: 9999);

            var card = CreateAdaptiveCard(_hotkeyStep, "Card", 420);

            var hotkeyTitle = CreateTitle(card, "Title", "Settings Hotkey");
            RegisterUIText(hotkeyTitle);
            var hotkeyDesc = CreateDescription(card, "Description", "Choose a keyboard shortcut to open the translator menu");
            RegisterUIText(hotkeyDesc);

            UIStyles.CreateSpacer(card, 15);

            // Hotkey capture (reusable component)
            _hotkeyCapture.CreateUI(card, onHotkeyChanged: (hotkey) =>
            {
                if (_hotkeyDisplayLabel != null)
                {
                    _hotkeyDisplayLabel.text = hotkey;
                    _hotkeyDisplayLabel.color = UIStyles.TextAccent;
                }
            }, includeDisplayLabel: false);

            UIStyles.CreateSpacer(card, 15);

            // Current hotkey display - Contains key names like Ctrl+F10
            _hotkeyDisplayLabel = UIFactory.CreateLabel(card, "HotkeyLabel", _hotkeyCapture.HotkeyString, TextAnchor.MiddleCenter);
            _hotkeyDisplayLabel.fontSize = UIStyles.FontSizeSectionTitle + 2;
            _hotkeyDisplayLabel.fontStyle = FontStyle.Bold;
            _hotkeyDisplayLabel.color = UIStyles.TextAccent;
            UIFactory.SetLayoutElement(_hotkeyDisplayLabel.gameObject, minHeight: UIStyles.RowHeightXLarge);
            RegisterExcluded(_hotkeyDisplayLabel); // Keyboard key names should not be translated

            var buttonRow = CreateButtonRow(_hotkeyStep);

            var backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            backBtn.OnClick += () => ShowStep(WizardStep.OnlineMode);
            RegisterUIText(backBtn.ButtonText);

            var nextBtn = CreatePrimaryButton(buttonRow, "NextBtn", "Continue →");
            nextBtn.OnClick += () => ShowStep(WizardStep.LanguageSelection);
            RegisterUIText(nextBtn.ButtonText);
        }

        private void CreateLanguageSelectionStep()
        {
            _languageSelectionStep = UIFactory.CreateVerticalGroup(_scrollContent, "LanguageSelectionStep", false, false, true, true, UIStyles.ElementSpacing);
            UIFactory.SetLayoutElement(_languageSelectionStep, flexibleWidth: 9999);

            var card = CreateAdaptiveCard(_languageSelectionStep, "Card", 420);

            var title = CreateTitle(card, "Title", "Translation Language");
            RegisterUIText(title);
            var desc = CreateDescription(card, "Description", "Choose the language you want games translated to");
            RegisterUIText(desc);

            UIStyles.CreateSpacer(card, 15);

            // Detected language info
            string systemLang = LanguageHelper.GetSystemLanguageName();
            _detectedLanguageLabel = UIFactory.CreateLabel(card, "DetectedLabel",
                $"Detected from your system: {systemLang}",
                TextAnchor.MiddleCenter);
            _detectedLanguageLabel.fontSize = UIStyles.FontSizeSmall;
            _detectedLanguageLabel.color = UIStyles.TextMuted;
            _detectedLanguageLabel.fontStyle = FontStyle.Italic;
            UIFactory.SetLayoutElement(_detectedLanguageLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(_detectedLanguageLabel); // Contains language name

            UIStyles.CreateSpacer(card, 10);

            // Target language selector
            var langSection = CreateSection(card, "LanguageSection");

            var langLabel = UIFactory.CreateLabel(langSection, "LangLabel", "Translate games to:", TextAnchor.MiddleLeft);
            langLabel.color = UIStyles.TextSecondary;
            langLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(langLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(langLabel);

            _targetLanguageDropdown = new SearchableDropdown(
                "TargetLang",
                LanguageHelper.GetLanguageNames(),
                _targetLanguage,
                popupHeight: 250,
                showSearch: true
            );
            _targetLanguageDropdown.CreateUI(langSection, (lang) => _targetLanguage = lang, width: 200);

            UIStyles.CreateSpacer(card, 10);

            // Hint about source language
            var hintLabel = UIFactory.CreateLabel(card, "HintLabel",
                "The source language (game's original language) is detected automatically.",
                TextAnchor.MiddleCenter);
            hintLabel.fontSize = UIStyles.FontSizeHint;
            hintLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(hintLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(hintLabel);

            // Navigation buttons
            var buttonRow = CreateButtonRow(_languageSelectionStep);

            var backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            backBtn.OnClick += () => ShowStep(WizardStep.Hotkey);
            RegisterUIText(backBtn.ButtonText);

            var nextBtn = CreatePrimaryButton(buttonRow, "NextBtn", "Continue →");
            nextBtn.OnClick += () =>
            {
                if (_onlineMode)
                    ShowStep(WizardStep.TranslationChoice);
                else
                    ShowStep(WizardStep.AIConfig);
            };
            RegisterUIText(nextBtn.ButtonText);
        }

        private void CreateTranslationChoiceStep()
        {
            _translationChoiceStep = UIFactory.CreateVerticalGroup(_scrollContent, "TranslationChoiceStep", false, false, true, true, UIStyles.ElementSpacing);
            UIFactory.SetLayoutElement(_translationChoiceStep, flexibleWidth: 9999);

            var card = CreateAdaptiveCard(_translationChoiceStep, "Card", 460);

            var translationTitle = CreateTitle(card, "Title", "Community Translations");
            RegisterUIText(translationTitle);

            // Game info section
            var gameSection = CreateSection(card, "GameSection");

            _gameLabel = UIFactory.CreateLabel(gameSection, "GameLabel", "Game: Detecting...", TextAnchor.MiddleLeft);
            _gameLabel.fontStyle = FontStyle.Bold;
            _gameLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_gameLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(_gameLabel); // Contains game name

            _localTranslationsLabel = UIFactory.CreateLabel(gameSection, "LocalLabel", "", TextAnchor.MiddleLeft);
            _localTranslationsLabel.fontSize = UIStyles.FontSizeSmall;
            _localTranslationsLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_localTranslationsLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterExcluded(_localTranslationsLabel); // Dynamic content with numbers/usernames

            // Account status row
            _accountStatusLabel = UIFactory.CreateLabel(gameSection, "AccountStatus", "Want to sync your translations?", TextAnchor.MiddleLeft);
            _accountStatusLabel.fontSize = UIStyles.FontSizeHint;
            _accountStatusLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_accountStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterExcluded(_accountStatusLabel); // Contains username

            _loginBtn = CreatePrimaryButton(gameSection, "LoginBtn", "Connect Account (optional)", 200);
            _loginBtn.OnClick += OnLoginClicked;
            RegisterUIText(_loginBtn.ButtonText);

            UIStyles.CreateSpacer(card, 10);

            // Translation list (reusable component)
            _translationList.CreateUI(card, 100, onSelectionChanged: (t) =>
            {
                UpdateActionButtons();
            });

            // Comparison info (shows diff between local and selected remote)
            _comparisonLabel = UIFactory.CreateLabel(card, "ComparisonLabel", "", TextAnchor.MiddleCenter);
            _comparisonLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_comparisonLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(_comparisonLabel); // Contains numbers/usernames

            // Status label
            _downloadStatusLabel = UIFactory.CreateLabel(card, "DownloadStatus", "", TextAnchor.MiddleCenter);
            _downloadStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_downloadStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(_downloadStatusLabel);

            // Action buttons row (Download / Upload / Merge)
            _actionButtonsRow = UIStyles.CreateFormRow(card, "ActionBtnsRow", UIStyles.RowHeightLarge);
            var actionLayout = _actionButtonsRow.GetComponent<HorizontalLayoutGroup>();
            if (actionLayout != null) actionLayout.childAlignment = TextAnchor.MiddleCenter; // Override to center buttons

            _downloadBtn = UIFactory.CreateButton(_actionButtonsRow, "DownloadBtn", "Download");
            UIFactory.SetLayoutElement(_downloadBtn.Component.gameObject, minWidth: 100, minHeight: UIStyles.RowHeightNormal);
            UIStyles.SetBackground(_downloadBtn.Component.gameObject, UIStyles.ButtonPrimary);
            _downloadBtn.OnClick += OnDownloadClicked;
            RegisterUIText(_downloadBtn.ButtonText);

            _uploadBtn = UIFactory.CreateButton(_actionButtonsRow, "UploadBtn", "Upload");
            UIFactory.SetLayoutElement(_uploadBtn.Component.gameObject, minWidth: 100, minHeight: UIStyles.RowHeightNormal);
            UIStyles.SetBackground(_uploadBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _uploadBtn.OnClick += OnUploadClicked;
            RegisterUIText(_uploadBtn.ButtonText);

            _mergeBtn = UIFactory.CreateButton(_actionButtonsRow, "MergeBtn", "Merge");
            UIFactory.SetLayoutElement(_mergeBtn.Component.gameObject, minWidth: 100, minHeight: UIStyles.RowHeightNormal);
            UIStyles.SetBackground(_mergeBtn.Component.gameObject, UIStyles.ButtonWarning);
            _mergeBtn.OnClick += OnMergeClicked;
            RegisterUIText(_mergeBtn.ButtonText);

            _actionButtonsRow.SetActive(false);

            // Navigation buttons
            var buttonRow = CreateButtonRow(_translationChoiceStep);

            var backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            backBtn.OnClick += () => ShowStep(WizardStep.LanguageSelection);
            RegisterUIText(backBtn.ButtonText);

            var nextBtn = CreatePrimaryButton(buttonRow, "NextBtn", "Continue →");
            nextBtn.OnClick += () => ShowStep(WizardStep.AIConfig);
            RegisterUIText(nextBtn.ButtonText);
        }

        private async void OnTranslationChoiceEnter()
        {
            // Detect game if not already done
            if (_detectedGame == null)
            {
                _detectedGame = GameDetector.DetectGame();
                if (_detectedGame != null)
                {
                    _gameLabel.text = $"Game: {_detectedGame.name}";
                    if (_onlineMode && !_translationList.IsSearching)
                    {
                        // Use the selected target language from wizard
                        string targetLang = _targetLanguage;

                        // Capture values for closure
                        var steamId = _detectedGame.steam_id;
                        var gameName = _detectedGame.name;

                        await _translationList.SearchAsync(steamId, gameName, targetLang);

                        // After await, we may be on a background thread (IL2CPP issue)
                        TranslatorUIManager.RunOnMainThread(() =>
                        {
                            UpdateActionButtons();

                            // Translations loaded, recalculate panel size
                            RecalculateSize();
                        });
                    }
                }
                else
                {
                    _gameLabel.text = "Game: Unknown";
                }
            }

            // Update local translations count
            int localCount = TranslatorCore.TranslationCache.Count;
            if (localCount > 0)
            {
                _localTranslationsLabel.text = $"You already have {localCount} local translations";
                var serverState = TranslatorCore.ServerState;
                if (serverState != null && serverState.Exists && !string.IsNullOrEmpty(serverState.Uploader))
                {
                    _localTranslationsLabel.text += $" (synced with @{serverState.Uploader})";
                }
            }
            else
            {
                _localTranslationsLabel.text = "";
            }

            UpdateAccountStatus();
            UpdateActionButtons();
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
            _downloadBtn.Component.gameObject.SetActive(false);
            _uploadBtn.Component.gameObject.SetActive(false);
            _mergeBtn.Component.gameObject.SetActive(false);
            _comparisonLabel.text = "";
            _actionButtonsRow.SetActive(false);

            if (selected == null && localCount == 0)
            {
                // No local, no remote selected
                _comparisonLabel.text = "No translation found for your language";
                _comparisonLabel.color = UIStyles.TextMuted;
                return;
            }

            _actionButtonsRow.SetActive(true);

            // Check if selected remote is owned by current user
            bool isOwnRemote = isLoggedIn && selected != null &&
                !string.IsNullOrEmpty(currentUser) &&
                selected.Uploader.Equals(currentUser, StringComparison.OrdinalIgnoreCase);

            int remoteCount = selected?.LineCount ?? 0;

            if (selected == null && localCount > 0)
            {
                // Local only, no remote
                if (isLoggedIn)
                {
                    _comparisonLabel.text = $"You have {localCount} local translations (not uploaded yet)";
                    _comparisonLabel.color = UIStyles.StatusSuccess;
                    _uploadBtn.Component.gameObject.SetActive(true);
                }
                else
                {
                    // Not logged in - hide button row entirely, only show message
                    _actionButtonsRow.SetActive(false);
                    _comparisonLabel.text = $"You have {localCount} local translations. Login to upload!";
                    _comparisonLabel.color = UIStyles.TextSecondary;
                }
                return;
            }

            if (localCount == 0 && selected != null)
            {
                // Remote only, no local
                _comparisonLabel.text = $"Remote: {remoteCount} lines by @{selected.Uploader}";
                _comparisonLabel.color = UIStyles.TextPrimary;
                _downloadBtn.Component.gameObject.SetActive(true);
                return;
            }

            // Both local and remote exist - compare
            int diff = localCount - remoteCount;
            string diffText = diff > 0 ? $"+{diff}" : diff.ToString();

            if (isOwnRemote)
            {
                // Same owner - sync scenario
                _comparisonLabel.text = $"Local: {localCount} | Remote (yours): {remoteCount} ({diffText})";

                if (localCount > remoteCount)
                {
                    // Local is more complete - suggest upload
                    _comparisonLabel.color = UIStyles.StatusSuccess;
                    _uploadBtn.Component.gameObject.SetActive(true);
                    _downloadBtn.Component.gameObject.SetActive(true);
                    _mergeBtn.Component.gameObject.SetActive(true);
                }
                else if (localCount < remoteCount)
                {
                    // Remote is more complete - suggest download
                    _comparisonLabel.color = UIStyles.StatusWarning;
                    _downloadBtn.Component.gameObject.SetActive(true);
                    _mergeBtn.Component.gameObject.SetActive(true);
                }
                else
                {
                    // Same count - might still have differences
                    _comparisonLabel.color = UIStyles.TextPrimary;
                    _downloadBtn.Component.gameObject.SetActive(true);
                    _uploadBtn.Component.gameObject.SetActive(true);
                    _mergeBtn.Component.gameObject.SetActive(true);
                }
            }
            else
            {
                // Different owner - download or merge
                _comparisonLabel.text = $"Local: {localCount} | Remote (@{selected.Uploader}): {remoteCount}";
                _comparisonLabel.color = UIStyles.TextPrimary;

                _downloadBtn.Component.gameObject.SetActive(true);
                if (localCount > 0)
                {
                    _mergeBtn.Component.gameObject.SetActive(true);
                }
            }
        }

        private void OnUploadClicked()
        {
            // Open upload panel
            SetActive(false);
            TranslatorUIManager.UploadSetupPanel?.ShowForSetup((game, source, target) =>
            {
                TranslatorUIManager.UploadPanel?.SetActive(true);
            });
        }

        private async void OnMergeClicked()
        {
            var selected = _translationList?.SelectedTranslation;
            if (selected == null) return;

            _downloadStatusLabel.text = "Downloading for merge...";
            _downloadStatusLabel.color = UIStyles.StatusWarning;
            SetButtonsInteractable(false);

            await TranslatorUIManager.DownloadAndMerge(selected, (success, message) =>
            {
                if (success)
                {
                    _downloadStatusLabel.text = message;
                    _downloadStatusLabel.color = UIStyles.StatusSuccess;
                    // Auto-advance to complete after successful merge
                    UniverseLib.RuntimeHelper.StartCoroutine(DelayedShowComplete());
                }
                else
                {
                    _downloadStatusLabel.text = message;
                    _downloadStatusLabel.color = UIStyles.StatusError;
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

        private void SetButtonsInteractable(bool interactable)
        {
            _downloadBtn.Component.interactable = interactable;
            _uploadBtn.Component.interactable = interactable;
            _mergeBtn.Component.interactable = interactable;
        }

        private System.Collections.IEnumerator DelayedShowComplete()
        {
            yield return new WaitForSeconds(1.5f);
            ShowStep(WizardStep.Complete);
        }

        public void UpdateAccountStatus()
        {
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);

            if (isLoggedIn)
            {
                string currentUser = TranslatorCore.Config.api_user;
                _accountStatusLabel.text = $"Connected as @{currentUser}";
                _accountStatusLabel.fontStyle = FontStyle.Italic;
                _loginBtn.Component.gameObject.SetActive(false);
            }
            else
            {
                _accountStatusLabel.text = "Want to sync your translations?";
                _loginBtn.Component.gameObject.SetActive(true);
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
            var selected = _translationList?.SelectedTranslation;
            if (selected == null || _isDownloading) return;

            _isDownloading = true;
            _downloadStatusLabel.text = "Downloading...";
            _downloadStatusLabel.color = UIStyles.StatusWarning;
            SetButtonsInteractable(false);

            await TranslatorUIManager.DownloadTranslation(selected, (success, message) =>
            {
                _downloadStatusLabel.text = message;
                _downloadStatusLabel.color = success ? UIStyles.StatusSuccess : UIStyles.StatusError;

                if (success)
                {
                    // Auto-advance to complete after successful download
                    UniverseLib.RuntimeHelper.StartCoroutine(DelayedShowComplete());
                }
                else
                {
                    _isDownloading = false;
                    SetButtonsInteractable(true);
                }
            });
        }

        private void CreateAIConfigStep()
        {
            _aiConfigStep = UIFactory.CreateVerticalGroup(_scrollContent, "AIConfigStep", false, false, true, true, UIStyles.ElementSpacing);
            UIFactory.SetLayoutElement(_aiConfigStep, flexibleWidth: 9999);

            var card = CreateAdaptiveCard(_aiConfigStep, "Card", 420);

            var aiTitle = CreateTitle(card, "Title", "Auto-Translation");
            RegisterUIText(aiTitle);
            var aiDesc = CreateDescription(card, "Description", "Choose a translation backend (can be changed later in Settings)");
            RegisterUIText(aiDesc);

            UIStyles.CreateSpacer(card, 10);

            // Enable toggle
            var enableSection = CreateSection(card, "EnableSection");
            var enableRow = UIStyles.CreateFormRow(enableSection, "EnableRow", UIStyles.RowHeightLarge);

            var enableObj = UIFactory.CreateToggle(enableRow, "EnableToggle", out _wizardEnableToggle, out var enableLabel);
            _wizardEnableToggle.isOn = (_translationBackend != "none");
            enableLabel.text = "";
            UIHelpers.AddToggleListener(_wizardEnableToggle, OnWizardEnableChanged);
            UIFactory.SetLayoutElement(enableObj, minWidth: UIStyles.ToggleControlWidth);

            var enableTextLabel = UIFactory.CreateLabel(enableRow, "EnableTextLabel", "Enable auto-translation", TextAnchor.MiddleLeft);
            enableTextLabel.fontStyle = FontStyle.Bold;
            enableTextLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(enableTextLabel.gameObject, flexibleWidth: 9999);
            RegisterUIText(enableTextLabel);

            UIStyles.CreateSpacer(card, 10);

            // Backend type section (shown when enabled)
            _wizardBackendTypeSection = UIFactory.CreateVerticalGroup(card, "BackendTypeSection", false, false, true, true, 5);
            UIFactory.SetLayoutElement(_wizardBackendTypeSection, flexibleWidth: 9999);

            var typeSection = CreateSection(_wizardBackendTypeSection, "TypeSection");
            var typeLabel = UIFactory.CreateLabel(typeSection, "TypeLabel", "Type:", TextAnchor.MiddleLeft);
            typeLabel.color = UIStyles.TextSecondary;
            typeLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(typeLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(typeLabel);

            string[] typeOptions = { "LLM (AI)", "Translation API" };
            bool isTransApi = _translationBackend == "google" || _translationBackend == "deepl";
            _wizardBackendTypeDropdown = new SearchableDropdown("WizardType", typeOptions,
                isTransApi ? "Translation API" : "LLM (AI)", 100, false);
            var typeObj = _wizardBackendTypeDropdown.CreateUI(typeSection, OnWizardTypeChanged);
            UIFactory.SetLayoutElement(typeObj, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);

            UIStyles.CreateSpacer(_wizardBackendTypeSection, 5);

            // === LLM SECTION ===
            _wizardLlmSection = UIFactory.CreateVerticalGroup(_wizardBackendTypeSection, "LLMSection", false, false, true, true, 5);
            UIFactory.SetLayoutElement(_wizardLlmSection, flexibleWidth: 9999);

            var urlSection = CreateSection(_wizardLlmSection, "UrlSection");
            var urlLabel = UIFactory.CreateLabel(urlSection, "UrlLabel", "Server URL:", TextAnchor.MiddleLeft);
            urlLabel.color = UIStyles.TextSecondary;
            urlLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(urlLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterExcluded(urlLabel);

            var urlRow = UIStyles.CreateFormRow(urlSection, "UrlRow", UIStyles.RowHeightLarge, 5);
            _aiUrlInput = UIFactory.CreateInputField(urlRow, "AIUrl", "http://localhost:11434");
            _aiUrlInput.Text = _aiUrl;
            _aiUrlInput.OnValueChanged += (val) => _aiUrl = val;
            UIFactory.SetLayoutElement(_aiUrlInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_aiUrlInput.Component.gameObject, UIStyles.InputBackground);

            var testBtn = CreateSecondaryButton(urlRow, "TestBtn", "Test", 70);
            testBtn.OnClick += TestAIConnection;
            RegisterUIText(testBtn.ButtonText);

            _aiStatusLabel = UIFactory.CreateLabel(urlSection, "StatusLabel", "", TextAnchor.MiddleCenter);
            _aiStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_aiStatusLabel.gameObject, minHeight: UIStyles.RowHeightNormal);

            var keySection = CreateSection(_wizardLlmSection, "KeySection");
            var keyLabel = UIFactory.CreateLabel(keySection, "KeyLabel", "API Key:", TextAnchor.MiddleLeft);
            keyLabel.color = UIStyles.TextSecondary;
            keyLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(keyLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterExcluded(keyLabel);

            _aiApiKeyInput = UIFactory.CreateInputField(keySection, "AIApiKey", "");
            _aiApiKeyInput.Text = _aiApiKey;
            _aiApiKeyInput.Component.contentType = UnityEngine.UI.InputField.ContentType.Password;
            _aiApiKeyInput.OnValueChanged += (val) => _aiApiKey = val;
            UIFactory.SetLayoutElement(_aiApiKeyInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_aiApiKeyInput.Component.gameObject, UIStyles.InputBackground);

            var keyHint = UIStyles.CreateHint(keySection, "KeyHint", "Optional for local servers (Ollama, LM Studio)");
            RegisterUIText(keyHint);

            var modelSection = CreateSection(_wizardLlmSection, "ModelSection");
            var modelLabel = UIFactory.CreateLabel(modelSection, "ModelLabel", "Model:", TextAnchor.MiddleLeft);
            modelLabel.color = UIStyles.TextSecondary;
            modelLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(modelLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(modelLabel);

            var modelRow = UIStyles.CreateFormRow(modelSection, "ModelRow", UIStyles.RowHeightLarge, 5);
            string[] initialModels = !string.IsNullOrEmpty(_aiModel) ? new[] { _aiModel } : new string[0];
            _modelDropdown = new SearchableDropdown("ModelDropdown", initialModels, _aiModel, 200, false);
            var modelObj = _modelDropdown.CreateUI(modelRow, (val) => _aiModel = val);
            UIFactory.SetLayoutElement(modelObj, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);

            var refreshBtn = CreateSecondaryButton(modelRow, "RefreshBtn", "Refresh", 70);
            refreshBtn.OnClick += RefreshModels;
            RegisterUIText(refreshBtn.ButtonText);

            var contextSection = CreateSection(_wizardLlmSection, "ContextSection");
            var contextLabel = UIFactory.CreateLabel(contextSection, "ContextLabel", "Game Context (optional):", TextAnchor.MiddleLeft);
            contextLabel.color = UIStyles.TextSecondary;
            contextLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(contextLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(contextLabel);

            _gameContextInput = UIFactory.CreateInputField(contextSection, "ContextInput", "e.g., RPG game, fantasy setting");
            _gameContextInput.Component.lineType = UnityEngine.UI.InputField.LineType.MultiLineNewline;
            _gameContextInput.Text = _gameContext;
            _gameContextInput.OnValueChanged += (val) => _gameContext = val;
            UIFactory.SetLayoutElement(_gameContextInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.MultiLineMedium);
            UIStyles.SetBackground(_gameContextInput.Component.gameObject, UIStyles.InputBackground);

            // === TRANSLATION API SECTION ===
            _wizardTransApiSection = UIFactory.CreateVerticalGroup(_wizardBackendTypeSection, "TransApiSection", false, false, true, true, 5);
            UIFactory.SetLayoutElement(_wizardTransApiSection, flexibleWidth: 9999);

            var providerSection = CreateSection(_wizardTransApiSection, "ProviderSection");
            var providerLabel = UIFactory.CreateLabel(providerSection, "ProviderLabel", "Provider:", TextAnchor.MiddleLeft);
            providerLabel.color = UIStyles.TextSecondary;
            providerLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(providerLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(providerLabel);

            string[] providerOptions = { "Google Translate", "DeepL" };
            string currentProvider = _translationBackend == "deepl" ? "DeepL" : "Google Translate";
            _wizardProviderDropdown = new SearchableDropdown("WizardProvider", providerOptions, currentProvider, 100, false);
            var providerObj = _wizardProviderDropdown.CreateUI(providerSection, OnWizardProviderChanged);
            UIFactory.SetLayoutElement(providerObj, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);

            UIStyles.CreateSpacer(_wizardTransApiSection, 5);

            // === GOOGLE SECTION ===
            _wizardGoogleSection = UIFactory.CreateVerticalGroup(_wizardTransApiSection, "GoogleSection", false, false, true, true, 5);
            UIFactory.SetLayoutElement(_wizardGoogleSection, flexibleWidth: 9999);

            var googleKeySection = CreateSection(_wizardGoogleSection, "GoogleKeySection");
            var googleKeyLabel = UIFactory.CreateLabel(googleKeySection, "GoogleKeyLabel", "Google API Key:", TextAnchor.MiddleLeft);
            googleKeyLabel.color = UIStyles.TextSecondary;
            googleKeyLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(googleKeyLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterExcluded(googleKeyLabel);

            var googleKeyRow = UIStyles.CreateFormRow(googleKeySection, "GoogleKeyRow", UIStyles.RowHeightLarge, 5);
            _wizardGoogleKeyInput = UIFactory.CreateInputField(googleKeyRow, "GoogleKey", "");
            _wizardGoogleKeyInput.Text = _googleApiKey;
            _wizardGoogleKeyInput.Component.contentType = UnityEngine.UI.InputField.ContentType.Password;
            _wizardGoogleKeyInput.OnValueChanged += (val) => _googleApiKey = val;
            UIFactory.SetLayoutElement(_wizardGoogleKeyInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_wizardGoogleKeyInput.Component.gameObject, UIStyles.InputBackground);

            var googleTestBtn = CreateSecondaryButton(googleKeyRow, "GoogleTestBtn", "Test", 70);
            googleTestBtn.OnClick += WizardTestGoogle;
            RegisterUIText(googleTestBtn.ButtonText);

            _wizardGoogleStatusLabel = UIFactory.CreateLabel(googleKeySection, "GoogleStatus", "", TextAnchor.MiddleCenter);
            _wizardGoogleStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_wizardGoogleStatusLabel.gameObject, minHeight: UIStyles.RowHeightNormal);

            var googleHint = UIStyles.CreateHint(_wizardGoogleSection, "GoogleHint", "Requires Google Cloud API key with Translation API enabled");
            RegisterUIText(googleHint);

            // === DEEPL SECTION ===
            _wizardDeeplSection = UIFactory.CreateVerticalGroup(_wizardTransApiSection, "DeepLSection", false, false, true, true, 5);
            UIFactory.SetLayoutElement(_wizardDeeplSection, flexibleWidth: 9999);

            var deeplKeySection = CreateSection(_wizardDeeplSection, "DeepLKeySection");
            var deeplKeyLabel = UIFactory.CreateLabel(deeplKeySection, "DeepLKeyLabel", "DeepL API Key:", TextAnchor.MiddleLeft);
            deeplKeyLabel.color = UIStyles.TextSecondary;
            deeplKeyLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(deeplKeyLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterExcluded(deeplKeyLabel);

            var deeplKeyRow = UIStyles.CreateFormRow(deeplKeySection, "DeepLKeyRow", UIStyles.RowHeightLarge, 5);
            _wizardDeeplKeyInput = UIFactory.CreateInputField(deeplKeyRow, "DeepLKey", "");
            _wizardDeeplKeyInput.Text = _deeplApiKey;
            _wizardDeeplKeyInput.Component.contentType = UnityEngine.UI.InputField.ContentType.Password;
            _wizardDeeplKeyInput.OnValueChanged += (val) => _deeplApiKey = val;
            UIFactory.SetLayoutElement(_wizardDeeplKeyInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_wizardDeeplKeyInput.Component.gameObject, UIStyles.InputBackground);

            var deeplTestBtn = CreateSecondaryButton(deeplKeyRow, "DeepLTestBtn", "Test", 70);
            deeplTestBtn.OnClick += WizardTestDeepL;
            RegisterUIText(deeplTestBtn.ButtonText);

            _wizardDeeplStatusLabel = UIFactory.CreateLabel(deeplKeySection, "DeepLStatus", "", TextAnchor.MiddleCenter);
            _wizardDeeplStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_wizardDeeplStatusLabel.gameObject, minHeight: UIStyles.RowHeightNormal);

            var deeplFreeObj = UIFactory.CreateToggle(_wizardDeeplSection, "DeepLFreeToggle", out _wizardDeeplFreeToggle, out var deeplFreeLabel);
            _wizardDeeplFreeToggle.isOn = _deeplUseFree;
            deeplFreeLabel.text = " Use Free API (api-free.deepl.com)";
            deeplFreeLabel.color = UIStyles.TextSecondary;
            UIHelpers.AddToggleListener(_wizardDeeplFreeToggle, (val) => _deeplUseFree = val);
            UIFactory.SetLayoutElement(deeplFreeObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(deeplFreeLabel);

            var deeplHint = UIStyles.CreateHint(_wizardDeeplSection, "DeepLHint", "Free plan: 500k chars/month. Uncheck for Pro API.");
            RegisterUIText(deeplHint);

            // Initial visibility
            bool initEnabled = _translationBackend != "none";
            bool initIsTransApi = _translationBackend == "google" || _translationBackend == "deepl";
            _wizardBackendTypeSection.SetActive(initEnabled);
            _wizardLlmSection.SetActive(initEnabled && !initIsTransApi);
            _wizardTransApiSection.SetActive(initEnabled && initIsTransApi);
            _wizardGoogleSection.SetActive(_translationBackend == "google");
            _wizardDeeplSection.SetActive(_translationBackend == "deepl");

            // Navigation buttons
            var buttonRow = CreateButtonRow(_aiConfigStep);

            var backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            backBtn.OnClick += () =>
            {
                if (_onlineMode)
                    ShowStep(WizardStep.TranslationChoice);
                else
                    ShowStep(WizardStep.LanguageSelection);
            };
            RegisterUIText(backBtn.ButtonText);

            var finishBtn = CreatePrimaryButton(buttonRow, "FinishBtn", "Finish Setup →");
            finishBtn.OnClick += () => ShowStep(WizardStep.Complete);
            RegisterUIText(finishBtn.ButtonText);
        }

        private void UpdateWizardBackendVisibility()
        {
            bool enabled = _wizardEnableToggle != null && _wizardEnableToggle.isOn;
            _wizardBackendTypeSection?.SetActive(enabled);

            if (!enabled)
            {
                _translationBackend = "none";
                _enableAI = false;
                return;
            }

            // If not online, force LLM (Translation APIs require internet)
            bool canUseTransApi = _onlineMode;
            _wizardBackendTypeDropdown?.SetInteractable(canUseTransApi);
            if (!canUseTransApi && _wizardBackendTypeDropdown?.SelectedValue == "Translation API")
            {
                _wizardBackendTypeDropdown.SelectedValue = "LLM (AI)";
            }

            string type = _wizardBackendTypeDropdown?.SelectedValue ?? "LLM (AI)";
            bool isLLM = type == "LLM (AI)";

            _wizardLlmSection?.SetActive(isLLM);
            _wizardTransApiSection?.SetActive(!isLLM);

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

                _wizardGoogleSection?.SetActive(provider == "Google Translate");
                _wizardDeeplSection?.SetActive(provider == "DeepL");
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
            if (string.IsNullOrEmpty(_googleApiKey))
            {
                _wizardGoogleStatusLabel.text = "Enter an API key first";
                _wizardGoogleStatusLabel.color = UIStyles.StatusWarning;
                return;
            }
            _wizardGoogleStatusLabel.text = "Testing...";
            _wizardGoogleStatusLabel.color = UIStyles.TextSecondary;

            bool success = await TranslatorCore.TestGoogleConnection(_googleApiKey);
            TranslatorUIManager.RunOnMainThread(() =>
            {
                _wizardGoogleStatusLabel.text = success ? "Connected!" : "Failed - check API key";
                _wizardGoogleStatusLabel.color = success ? UIStyles.StatusSuccess : UIStyles.StatusError;
            });
        }

        private async void WizardTestDeepL()
        {
            if (string.IsNullOrEmpty(_deeplApiKey))
            {
                _wizardDeeplStatusLabel.text = "Enter an API key first";
                _wizardDeeplStatusLabel.color = UIStyles.StatusWarning;
                return;
            }
            _wizardDeeplStatusLabel.text = "Testing...";
            _wizardDeeplStatusLabel.color = UIStyles.TextSecondary;

            bool success = await TranslatorCore.TestDeepLConnection(_deeplApiKey, _deeplUseFree);
            TranslatorUIManager.RunOnMainThread(() =>
            {
                _wizardDeeplStatusLabel.text = success ? "Connected!" : "Failed - check API key and plan type";
                _wizardDeeplStatusLabel.color = success ? UIStyles.StatusSuccess : UIStyles.StatusError;
            });
        }

        private void CreateCompleteStep()
        {
            _completeStep = UIFactory.CreateVerticalGroup(_scrollContent, "CompleteStep", false, false, true, true, UIStyles.ElementSpacing);
            UIFactory.SetLayoutElement(_completeStep, flexibleWidth: 9999);

            var card = CreateAdaptiveCard(_completeStep, "Card", 420);

            // Success title with accent color
            var title = UIFactory.CreateLabel(card, "Title", "Setup Complete!", TextAnchor.MiddleCenter);
            title.fontSize = UIStyles.FontSizeTitle + 2;
            title.fontStyle = FontStyle.Bold;
            title.color = UIStyles.StatusSuccess;
            UIFactory.SetLayoutElement(title.gameObject, minHeight: UIStyles.TitleHeight);
            RegisterUIText(title);

            UIStyles.CreateSpacer(card, 15);

            var desc = UIFactory.CreateLabel(card, "Description",
                "You're all set!\n\n" +
                $"Press {_hotkeyCapture.HotkeyString} to open settings at any time.\n\n" +
                "The translator will automatically detect text in the game\nand translate it to your language.",
                TextAnchor.MiddleCenter);
            desc.fontSize = UIStyles.FontSizeNormal;
            desc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(desc.gameObject, minHeight: UIStyles.MultiLineLarge);
            RegisterExcluded(desc); // Contains hotkey string

            UIStyles.CreateSpacer(card, 20);

            // Centered finish button inside card
            var finishBtn = CreatePrimaryButton(card, "FinishBtn", "Start Translating!", 200);
            finishBtn.OnClick += FinishWizard;
            UIStyles.SetBackground(finishBtn.Component.gameObject, UIStyles.ButtonSuccess);
            RegisterUIText(finishBtn.ButtonText);
        }

        private void ShowStep(WizardStep step)
        {
            _currentStep = step;

            // Hide all steps
            _welcomeStep?.SetActive(false);
            _onlineModeStep?.SetActive(false);
            _hotkeyStep?.SetActive(false);
            _languageSelectionStep?.SetActive(false);
            _translationChoiceStep?.SetActive(false);
            _aiConfigStep?.SetActive(false);
            _completeStep?.SetActive(false);

            // Show current step
            switch (step)
            {
                case WizardStep.Welcome:
                    _welcomeStep?.SetActive(true);
                    break;
                case WizardStep.OnlineMode:
                    _onlineModeStep?.SetActive(true);
                    break;
                case WizardStep.Hotkey:
                    _hotkeyStep?.SetActive(true);
                    break;
                case WizardStep.LanguageSelection:
                    _languageSelectionStep?.SetActive(true);
                    break;
                case WizardStep.TranslationChoice:
                    _translationChoiceStep?.SetActive(true);
                    OnTranslationChoiceEnter();
                    break;
                case WizardStep.AIConfig:
                    _aiConfigStep?.SetActive(true);
                    break;
                case WizardStep.Complete:
                    _completeStep?.SetActive(true);
                    break;
            }

            // Recalculate panel size for new step content
            // Delay to let layout update after SetActive changes
            UniverseLib.RuntimeHelper.StartCoroutine(DelayedResize());
        }

        private System.Collections.IEnumerator DelayedResize()
        {
            // Wait one frame for layout to update
            yield return null;
            CalculateAndApplyOptimalSize();
        }

        private async void TestAIConnection()
        {
            if (_aiStatusLabel == null) return;

            _aiStatusLabel.text = "Testing...";
            _aiStatusLabel.color = UIStyles.StatusWarning;

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
                        _aiStatusLabel.text = "Connection successful!";
                        _aiStatusLabel.color = UIStyles.StatusSuccess;
                        // Auto-refresh models on successful test
                        RefreshModels();
                    }
                    else
                    {
                        _aiStatusLabel.text = "Connection failed";
                        _aiStatusLabel.color = UIStyles.StatusError;
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _aiStatusLabel.text = $"Error: {errorMsg}";
                    _aiStatusLabel.color = UIStyles.StatusError;
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
            TranslatorCore.Config.enable_ai = (_translationBackend == "llm");
            TranslatorCore.Config.ai_url = _aiUrl;
            TranslatorCore.Config.ai_api_key = !string.IsNullOrEmpty(_aiApiKey) ? _aiApiKey : null;
            TranslatorCore.Config.ai_model = _aiModel;
            TranslatorCore.Config.game_context = _gameContext;
            TranslatorCore.Config.google_api_key = !string.IsNullOrEmpty(_googleApiKey) ? _googleApiKey : null;
            TranslatorCore.Config.deepl_api_key = !string.IsNullOrEmpty(_deeplApiKey) ? _deeplApiKey : null;
            TranslatorCore.Config.deepl_use_free = _deeplUseFree;
            TranslatorCore.Config.first_run_completed = true;
            TranslatorCore.SaveConfig();

            // Start translation worker if any backend is enabled
            if (TranslatorCore.Config.IsTranslationEnabled)
            {
                TranslatorCore.EnsureWorkerRunning();
            }

            SetActive(false);
            TranslatorUIManager.ShowMain();
        }

        protected override void OnClosePanelClicked()
        {
            // Don't allow closing wizard with X button during first run
        }
    }
}
