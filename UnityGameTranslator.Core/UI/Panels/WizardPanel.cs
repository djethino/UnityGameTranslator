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
    /// Steps: Welcome -> OnlineMode -> Hotkey -> LanguageSelection -> TranslationChoice -> OllamaConfig -> Complete
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
            OllamaConfig,
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
        private GameObject _ollamaConfigStep;
        private GameObject _completeStep;

        // State variables - initialized from config in ConstructPanelContent
        private bool _onlineMode;
        private bool _enableOllama;
        private string _ollamaUrl;
        private string _model;
        private string _gameContext;
        private string _targetLanguage;

        // Language selection
        private LanguageSelector _targetLanguageSelector;
        private Text _detectedLanguageLabel;

        // Hotkey capture (reusable component)
        private HotkeyCapture _hotkeyCapture;
        private Text _hotkeyDisplayLabel;

        // UI references
        private InputFieldRef _ollamaUrlInput;
        private InputFieldRef _modelInput;
        private InputFieldRef _gameContextInput;
        private Text _ollamaStatusLabel;

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
            _enableOllama = TranslatorCore.Config.enable_ollama;
            _ollamaUrl = TranslatorCore.Config.ollama_url ?? "http://localhost:11434";
            _model = TranslatorCore.Config.model ?? "qwen3:8b";
            _gameContext = TranslatorCore.Config.game_context ?? "";

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
            CreateOllamaConfigStep();
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
                "• Generate translations locally using Ollama AI\n" +
                "• Or both!",
                TextAnchor.MiddleCenter);
            desc.fontSize = UIStyles.FontSizeNormal;
            desc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(desc.gameObject, minHeight: UIStyles.MultiLineLarge + 20);
            RegisterExcluded(desc); // Contains "Ollama" brand name

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
                "• Use only local Ollama AI\n• No internet connection\n• Full privacy",
                TextAnchor.MiddleLeft);
            offlineDescLabel.fontSize = UIStyles.FontSizeSmall;
            offlineDescLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(offlineDescLabel.gameObject, minHeight: UIStyles.MultiLineSmall);
            RegisterExcluded(offlineDescLabel); // Contains "Ollama" brand name

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

            _targetLanguageSelector = new LanguageSelector(
                "TargetLang",
                LanguageHelper.GetLanguageNames(),
                _targetLanguage,
                100
            );
            _targetLanguageSelector.CreateUI(langSection, (lang) => _targetLanguage = lang);

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
                    ShowStep(WizardStep.OllamaConfig);
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
            var accountRow = UIStyles.CreateFormRow(gameSection, "AccountRow", UIStyles.RowHeightMedium);

            _accountStatusLabel = UIFactory.CreateLabel(accountRow, "AccountStatus", "Want to sync your translations?", TextAnchor.MiddleLeft);
            _accountStatusLabel.fontSize = UIStyles.FontSizeHint;
            _accountStatusLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_accountStatusLabel.gameObject, flexibleWidth: 9999);
            RegisterExcluded(_accountStatusLabel); // Contains username

            _loginBtn = UIFactory.CreateButton(accountRow, "LoginBtn", "Connect (optional)");
            UIFactory.SetLayoutElement(_loginBtn.Component.gameObject, minWidth: 130, minHeight: UIStyles.RowHeightNormal);
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
            nextBtn.OnClick += () => ShowStep(WizardStep.OllamaConfig);
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

        private void CreateOllamaConfigStep()
        {
            _ollamaConfigStep = UIFactory.CreateVerticalGroup(_scrollContent, "OllamaConfigStep", false, false, true, true, UIStyles.ElementSpacing);
            UIFactory.SetLayoutElement(_ollamaConfigStep, flexibleWidth: 9999);

            var card = CreateAdaptiveCard(_ollamaConfigStep, "Card", 420);

            var ollamaTitle = CreateTitle(card, "Title", "Ollama Configuration");
            RegisterExcluded(ollamaTitle); // Contains "Ollama" brand name
            var ollamaDesc = CreateDescription(card, "Description", "Configure local AI for offline translation");
            RegisterUIText(ollamaDesc);

            UIStyles.CreateSpacer(card, 10);

            // Enable toggle section
            var enableSection = CreateSection(card, "EnableSection");
            var enableRow = UIStyles.CreateFormRow(enableSection, "EnableRow", UIStyles.RowHeightLarge);

            var enableObj = UIFactory.CreateToggle(enableRow, "EnableToggle", out var enableToggle, out var enableLabel);
            enableToggle.isOn = _enableOllama;
            enableLabel.text = "";
            UIHelpers.AddToggleListener(enableToggle, (val) => _enableOllama = val);
            UIFactory.SetLayoutElement(enableObj, minWidth: UIStyles.ToggleControlWidth);

            var enableTextLabel = UIFactory.CreateLabel(enableRow, "EnableTextLabel", "Enable Ollama (local AI)", TextAnchor.MiddleLeft);
            enableTextLabel.fontStyle = FontStyle.Bold;
            enableTextLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(enableTextLabel.gameObject, flexibleWidth: 9999);
            RegisterExcluded(enableTextLabel); // Contains "Ollama" brand name

            UIStyles.CreateSpacer(card, 10);

            // URL input section
            var urlSection = CreateSection(card, "UrlSection");

            var urlLabel = UIFactory.CreateLabel(urlSection, "UrlLabel", "Ollama URL:", TextAnchor.MiddleLeft);
            urlLabel.color = UIStyles.TextSecondary;
            urlLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(urlLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterExcluded(urlLabel); // Contains "Ollama" and "URL" - technical terms

            var urlRow = UIStyles.CreateFormRow(urlSection, "UrlRow", UIStyles.RowHeightLarge, 5);

            _ollamaUrlInput = UIFactory.CreateInputField(urlRow, "OllamaUrl", "http://localhost:11434");
            _ollamaUrlInput.Text = _ollamaUrl;
            _ollamaUrlInput.OnValueChanged += (val) => _ollamaUrl = val;
            UIFactory.SetLayoutElement(_ollamaUrlInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_ollamaUrlInput.Component.gameObject, UIStyles.InputBackground);

            var testBtn = CreateSecondaryButton(urlRow, "TestBtn", "Test", 70);
            testBtn.OnClick += TestOllamaConnection;
            RegisterUIText(testBtn.ButtonText);

            _ollamaStatusLabel = UIFactory.CreateLabel(urlSection, "StatusLabel", "", TextAnchor.MiddleCenter);
            _ollamaStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_ollamaStatusLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(_ollamaStatusLabel);

            UIStyles.CreateSpacer(card, 10);

            // Model input section
            var modelSection = CreateSection(card, "ModelSection");

            var modelLabel = UIFactory.CreateLabel(modelSection, "ModelLabel", "Model:", TextAnchor.MiddleLeft);
            modelLabel.color = UIStyles.TextSecondary;
            modelLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(modelLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(modelLabel);

            _modelInput = UIFactory.CreateInputField(modelSection, "ModelInput", "qwen3:8b");
            _modelInput.Text = _model;
            _modelInput.OnValueChanged += (val) => _model = val;
            UIFactory.SetLayoutElement(_modelInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_modelInput.Component.gameObject, UIStyles.InputBackground);

            var modelHint = UIStyles.CreateHint(modelSection, "ModelHint", "Recommended: qwen3:8b • Install: ollama pull qwen3:8b");
            RegisterExcluded(modelHint); // Contains technical model name and command

            UIStyles.CreateSpacer(card, 10);

            // Game context section
            var contextSection = CreateSection(card, "ContextSection");

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

            var contextHint = UIStyles.CreateHint(contextSection, "ContextHint", "Helps the AI understand game-specific terms");
            RegisterUIText(contextHint);

            // Navigation buttons
            var buttonRow = CreateButtonRow(_ollamaConfigStep);

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
            _ollamaConfigStep?.SetActive(false);
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
                case WizardStep.OllamaConfig:
                    _ollamaConfigStep?.SetActive(true);
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

        private async void TestOllamaConnection()
        {
            if (_ollamaStatusLabel == null) return;

            _ollamaStatusLabel.text = "Testing...";
            _ollamaStatusLabel.color = UIStyles.StatusWarning;

            // Capture URL before await
            string url = _ollamaUrl;

            try
            {
                bool success = await TranslatorCore.TestOllamaConnection(url);

                // After await, we may be on a background thread (IL2CPP issue)
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success)
                    {
                        _ollamaStatusLabel.text = "Connection successful!";
                        _ollamaStatusLabel.color = UIStyles.StatusSuccess;
                    }
                    else
                    {
                        _ollamaStatusLabel.text = "Connection failed";
                        _ollamaStatusLabel.color = UIStyles.StatusError;
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _ollamaStatusLabel.text = $"Error: {errorMsg}";
                    _ollamaStatusLabel.color = UIStyles.StatusError;
                });
            }
        }

        private void FinishWizard()
        {
            // Save all settings
            TranslatorCore.Config.online_mode = _onlineMode;
            TranslatorCore.Config.settings_hotkey = _hotkeyCapture.HotkeyString;
            TranslatorCore.Config.target_language = _targetLanguage;
            TranslatorCore.Config.enable_ollama = _enableOllama;
            TranslatorCore.Config.ollama_url = _ollamaUrl;
            TranslatorCore.Config.model = _model;
            TranslatorCore.Config.game_context = _gameContext;
            TranslatorCore.Config.first_run_completed = true;
            TranslatorCore.SaveConfig();

            // Start Ollama worker if enabled
            if (_enableOllama)
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
