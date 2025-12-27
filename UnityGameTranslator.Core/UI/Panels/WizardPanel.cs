using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core.UI;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// First-run wizard panel. Guides user through initial setup.
    /// Steps: Welcome -> OnlineMode -> Hotkey -> TranslationChoice -> OllamaConfig -> Complete
    /// </summary>
    public class WizardPanel : TranslatorPanelBase
    {
        public enum WizardStep
        {
            Welcome,
            OnlineMode,
            Hotkey,
            TranslationChoice,
            OllamaConfig,
            Complete
        }

        public override string Name => "Unity Game Translator - Setup";
        public override int MinWidth => 520;
        public override int MinHeight => 500;
        public override int PanelWidth => 520;
        public override int PanelHeight => 500;

        // Current step
        private WizardStep _currentStep = WizardStep.Welcome;

        // Step containers
        private GameObject _welcomeStep;
        private GameObject _onlineModeStep;
        private GameObject _hotkeyStep;
        private GameObject _translationChoiceStep;
        private GameObject _ollamaConfigStep;
        private GameObject _completeStep;

        // State variables
        private bool _onlineMode = true;
        private string _hotkey = "F10";
        private bool _hotkeyCtrl = false;
        private bool _hotkeyAlt = false;
        private bool _hotkeyShift = false;
        private bool _enableOllama = false;
        private string _ollamaUrl = "http://localhost:11434";

        // UI references
        private Text _hotkeyLabel;
        private InputFieldRef _ollamaUrlInput;
        private Text _ollamaStatusLabel;

        // Hotkey capture state
        private bool _isCapturingHotkey;
        private ButtonRef _setHotkeyBtn;

        // TranslationChoice step state
        private GameInfo _detectedGame;
        private bool _isSearchingTranslations;
        private string _searchStatus = "";
        private List<TranslationInfo> _availableTranslations = new List<TranslationInfo>();
        private TranslationInfo _selectedTranslation;
        private bool _isDownloading;
        private string _downloadStatus = "";

        // TranslationChoice UI references
        private Text _gameLabel;
        private Text _localTranslationsLabel;
        private Text _accountStatusLabel;
        private ButtonRef _loginBtn;
        private Text _searchStatusLabel;
        private GameObject _translationListContent;
        private Text _downloadStatusLabel;
        private ButtonRef _downloadBtn;

        public WizardPanel(UIBase owner) : base(owner)
        {
        }

        public override void Update()
        {
            base.Update();

            // Capture hotkey when in capture mode - try multiple input methods
            if (_isCapturingHotkey && _currentStep == WizardStep.Hotkey)
            {
                CaptureHotkeyInput();
            }
        }

        private void CaptureHotkeyInput()
        {
            // Try to detect any key press using multiple methods
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                // Skip modifier keys, mouse buttons, and invalid keys
                if (key == KeyCode.None ||
                    key == KeyCode.LeftControl || key == KeyCode.RightControl ||
                    key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
                    key == KeyCode.LeftShift || key == KeyCode.RightShift ||
                    key == KeyCode.LeftCommand || key == KeyCode.RightCommand ||
                    key == KeyCode.LeftWindows || key == KeyCode.RightWindows ||
                    key == KeyCode.AltGr || key == KeyCode.CapsLock ||
                    key == KeyCode.Numlock || key == KeyCode.Menu ||
                    (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6) ||
                    (int)key >= 330) // Skip joystick buttons
                {
                    continue;
                }

                // Use UniverseLib InputManager
                bool keyPressed = false;
                try
                {
                    keyPressed = UniverseLib.Input.InputManager.GetKeyDown(key);
                }
                catch { }

                if (keyPressed)
                {
                    _hotkey = key.ToString();
                    _isCapturingHotkey = false;
                    UpdateHotkeyDisplay();

                    if (_setHotkeyBtn != null)
                        _setHotkeyBtn.ButtonText.text = "Change";

                    TranslatorCore.LogInfo($"[Wizard] Captured hotkey: {_hotkey}");
                    return;
                }
            }
        }

        protected override void ConstructPanelContent()
        {
            // Use centralized styling for panel content
            UIStyles.ConfigurePanelContent(ContentRoot, false);

            // Create all step containers
            CreateWelcomeStep();
            CreateOnlineModeStep();
            CreateHotkeyStep();
            CreateTranslationChoiceStep();
            CreateOllamaConfigStep();
            CreateCompleteStep();

            ShowStep(WizardStep.Welcome);
        }

        private void CreateWelcomeStep()
        {
            _welcomeStep = UIFactory.CreateVerticalGroup(ContentRoot, "WelcomeStep", true, false, true, true, 0);
            UIFactory.SetLayoutElement(_welcomeStep, flexibleWidth: 9999, flexibleHeight: 9999);
            var stepLayout = _welcomeStep.GetComponent<VerticalLayoutGroup>();
            if (stepLayout != null) stepLayout.childAlignment = TextAnchor.MiddleCenter;

            CreateFlexSpacer(_welcomeStep, "TopSpacer");

            var card = CreateCard(_welcomeStep, "Card", 280);

            CreateTitle(card, "Title", "Welcome to Unity Game Translator!");

            var desc = UIFactory.CreateLabel(card, "Description",
                "This mod automatically translates Unity games using AI.\n\n" +
                "You can either:\n" +
                "• Download community translations from our website\n" +
                "• Generate translations locally using Ollama AI\n" +
                "• Or both!",
                TextAnchor.MiddleCenter);
            desc.fontSize = UIStyles.FontSizeNormal;
            desc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(desc.gameObject, minHeight: 140);

            CreateFlexSpacer(_welcomeStep, "BottomSpacer");

            var buttonRow = CreateButtonRow(_welcomeStep);
            var nextBtn = CreatePrimaryButton(buttonRow, "NextBtn", "Get Started →", 160);
            nextBtn.OnClick += () => ShowStep(WizardStep.OnlineMode);
        }

        private void CreateOnlineModeStep()
        {
            _onlineModeStep = UIFactory.CreateVerticalGroup(ContentRoot, "OnlineModeStep", true, false, true, true, 0);
            UIFactory.SetLayoutElement(_onlineModeStep, flexibleWidth: 9999, flexibleHeight: 9999);
            var stepLayout = _onlineModeStep.GetComponent<VerticalLayoutGroup>();
            if (stepLayout != null) stepLayout.childAlignment = TextAnchor.MiddleCenter;

            CreateFlexSpacer(_onlineModeStep, "TopSpacer");

            var card = CreateCard(_onlineModeStep, "Card", 320);

            CreateTitle(card, "Title", "Online Mode");
            CreateDescription(card, "Description", "Do you want to enable online features?");

            UIStyles.CreateSpacer(card, 10);

            // Online mode option
            var onlineBox = CreateSection(card, "OnlineBox", 90);
            var onlineRow = UIFactory.CreateHorizontalGroup(onlineBox, "OnlineRow", false, false, true, true, 10);
            UIFactory.SetLayoutElement(onlineRow, minHeight: 28);

            var onlineToggleObj = UIFactory.CreateToggle(onlineRow, "OnlineToggle", out var onlineToggle, out var onlineLabel);
            onlineToggle.isOn = _onlineMode;
            onlineLabel.text = "";
            onlineToggle.onValueChanged.AddListener((val) => _onlineMode = val);
            UIFactory.SetLayoutElement(onlineToggleObj, minWidth: 25);

            var onlineTextLabel = UIFactory.CreateLabel(onlineRow, "OnlineTextLabel", "Enable Online Mode", TextAnchor.MiddleLeft);
            onlineTextLabel.fontStyle = FontStyle.Bold;
            onlineTextLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(onlineTextLabel.gameObject, flexibleWidth: 9999);

            var onlineDesc = UIFactory.CreateLabel(onlineBox, "OnlineDesc",
                "• Download community translations\n• Share your translations\n• Check for updates",
                TextAnchor.MiddleLeft);
            onlineDesc.fontSize = UIStyles.FontSizeSmall;
            onlineDesc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(onlineDesc.gameObject, minHeight: 45);

            UIStyles.CreateSpacer(card, 5);

            // Offline mode option
            var offlineBox = CreateSection(card, "OfflineBox", 90);
            var offlineRow = UIFactory.CreateHorizontalGroup(offlineBox, "OfflineRow", false, false, true, true, 10);
            UIFactory.SetLayoutElement(offlineRow, minHeight: 28);

            var offlineToggleObj = UIFactory.CreateToggle(offlineRow, "OfflineToggle", out var offlineToggle, out var offlineLabel);
            offlineToggle.isOn = !_onlineMode;
            offlineLabel.text = "";
            offlineToggle.onValueChanged.AddListener((val) => { if (val) _onlineMode = false; onlineToggle.isOn = !val; });
            onlineToggle.onValueChanged.AddListener((val) => offlineToggle.isOn = !val);
            UIFactory.SetLayoutElement(offlineToggleObj, minWidth: 25);

            var offlineTextLabel = UIFactory.CreateLabel(offlineRow, "OfflineTextLabel", "Stay Offline", TextAnchor.MiddleLeft);
            offlineTextLabel.fontStyle = FontStyle.Bold;
            offlineTextLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(offlineTextLabel.gameObject, flexibleWidth: 9999);

            var offlineDesc = UIFactory.CreateLabel(offlineBox, "OfflineDesc",
                "• Use only local Ollama AI\n• No internet connection\n• Full privacy",
                TextAnchor.MiddleLeft);
            offlineDesc.fontSize = UIStyles.FontSizeSmall;
            offlineDesc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(offlineDesc.gameObject, minHeight: 45);

            CreateFlexSpacer(_onlineModeStep, "BottomSpacer");

            var buttonRow = CreateButtonRow(_onlineModeStep);
            var backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            backBtn.OnClick += () => ShowStep(WizardStep.Welcome);

            var nextBtn = CreatePrimaryButton(buttonRow, "NextBtn", "Continue →");
            nextBtn.OnClick += () => ShowStep(WizardStep.Hotkey);
        }

        private void CreateHotkeyStep()
        {
            // Create step container - forceExpandWidth=true so children fill the width
            _hotkeyStep = UIFactory.CreateVerticalGroup(ContentRoot, "HotkeyStep", true, false, true, true, 0);
            UIFactory.SetLayoutElement(_hotkeyStep, flexibleWidth: 9999, flexibleHeight: 9999);
            var stepLayout = _hotkeyStep.GetComponent<VerticalLayoutGroup>();
            if (stepLayout != null)
            {
                stepLayout.childAlignment = TextAnchor.MiddleCenter;
            }

            // Top spacer for vertical centering
            CreateFlexSpacer(_hotkeyStep, "TopSpacer");

            // Main content card using centralized styling
            var card = CreateCard(_hotkeyStep, "Card", 220);

            // Title
            CreateTitle(card, "Title", "Settings Hotkey");

            // Description
            CreateDescription(card, "Description", "Choose a keyboard shortcut to open the translator menu");

            // Spacer
            UIStyles.CreateSpacer(card, 15);

            // Modifier toggles in a styled container
            var modContainer = UIStyles.CreateModifierContainer(card, "ModContainer");

            var ctrlObj = UIFactory.CreateToggle(modContainer, "CtrlToggle", out var ctrlToggle, out var ctrlLabel);
            ctrlLabel.text = "Ctrl";
            ctrlLabel.fontSize = UIStyles.FontSizeNormal;
            ctrlToggle.isOn = _hotkeyCtrl;
            ctrlToggle.onValueChanged.AddListener((val) => { _hotkeyCtrl = val; UpdateHotkeyDisplay(); });
            UIFactory.SetLayoutElement(ctrlObj, minWidth: 55);

            var altObj = UIFactory.CreateToggle(modContainer, "AltToggle", out var altToggle, out var altLabel);
            altLabel.text = "Alt";
            altLabel.fontSize = UIStyles.FontSizeNormal;
            altToggle.isOn = _hotkeyAlt;
            altToggle.onValueChanged.AddListener((val) => { _hotkeyAlt = val; UpdateHotkeyDisplay(); });
            UIFactory.SetLayoutElement(altObj, minWidth: 50);

            var shiftObj = UIFactory.CreateToggle(modContainer, "ShiftToggle", out var shiftToggle, out var shiftLabel);
            shiftLabel.text = "Shift";
            shiftLabel.fontSize = UIStyles.FontSizeNormal;
            shiftToggle.isOn = _hotkeyShift;
            shiftToggle.onValueChanged.AddListener((val) => { _hotkeyShift = val; UpdateHotkeyDisplay(); });
            UIFactory.SetLayoutElement(shiftObj, minWidth: 55);

            var plusLabel = UIFactory.CreateLabel(modContainer, "PlusLabel", "+", TextAnchor.MiddleCenter);
            plusLabel.fontSize = 16;
            plusLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(plusLabel.gameObject, minWidth: 25);

            _setHotkeyBtn = UIFactory.CreateButton(modContainer, "SetHotkeyBtn", _hotkey ?? "F10");
            UIFactory.SetLayoutElement(_setHotkeyBtn.Component.gameObject, minWidth: 80, minHeight: UIStyles.SmallButtonHeight);
            _setHotkeyBtn.OnClick += OnSetHotkeyClicked;

            // Spacer
            UIStyles.CreateSpacer(card, 15);

            // Current hotkey display
            _hotkeyLabel = UIFactory.CreateLabel(card, "HotkeyLabel", GetHotkeyDisplayString(), TextAnchor.MiddleCenter);
            _hotkeyLabel.fontSize = 18;
            _hotkeyLabel.fontStyle = FontStyle.Bold;
            _hotkeyLabel.color = UIStyles.TextAccent;
            UIFactory.SetLayoutElement(_hotkeyLabel.gameObject, minHeight: 35);

            // Bottom spacer for vertical centering
            CreateFlexSpacer(_hotkeyStep, "BottomSpacer");

            // Navigation buttons at bottom using centralized styling
            var buttonRow = CreateButtonRow(_hotkeyStep);

            var backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            backBtn.OnClick += () => ShowStep(WizardStep.OnlineMode);

            var nextBtn = CreatePrimaryButton(buttonRow, "NextBtn", "Continue →");
            nextBtn.OnClick += () =>
            {
                if (_onlineMode)
                    ShowStep(WizardStep.TranslationChoice);
                else
                    ShowStep(WizardStep.OllamaConfig);
            };
        }

        private void CreateTranslationChoiceStep()
        {
            _translationChoiceStep = UIFactory.CreateVerticalGroup(ContentRoot, "TranslationChoiceStep", true, false, true, true, 0);
            UIFactory.SetLayoutElement(_translationChoiceStep, flexibleWidth: 9999, flexibleHeight: 9999);
            var stepLayout = _translationChoiceStep.GetComponent<VerticalLayoutGroup>();
            if (stepLayout != null) stepLayout.childAlignment = TextAnchor.MiddleCenter;

            CreateFlexSpacer(_translationChoiceStep, "TopSpacer");

            // Main card for this step - larger to accommodate scroll view
            var card = CreateCard(_translationChoiceStep, "Card", 350);

            CreateTitle(card, "Title", "Community Translations");

            // Game info section
            var gameSection = CreateSection(card, "GameSection");

            _gameLabel = UIFactory.CreateLabel(gameSection, "GameLabel", "Game: Detecting...", TextAnchor.MiddleLeft);
            _gameLabel.fontStyle = FontStyle.Bold;
            _gameLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_gameLabel.gameObject, minHeight: 22);

            _localTranslationsLabel = UIFactory.CreateLabel(gameSection, "LocalLabel", "", TextAnchor.MiddleLeft);
            _localTranslationsLabel.fontSize = UIStyles.FontSizeSmall;
            _localTranslationsLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_localTranslationsLabel.gameObject, minHeight: 18);

            // Account status row
            var accountRow = UIFactory.CreateHorizontalGroup(gameSection, "AccountRow", false, false, true, true, 10);
            UIFactory.SetLayoutElement(accountRow, minHeight: 25);

            _accountStatusLabel = UIFactory.CreateLabel(accountRow, "AccountStatus", "Want to sync your translations?", TextAnchor.MiddleLeft);
            _accountStatusLabel.fontSize = UIStyles.FontSizeHint;
            _accountStatusLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_accountStatusLabel.gameObject, flexibleWidth: 9999);

            _loginBtn = UIFactory.CreateButton(accountRow, "LoginBtn", "Connect (optional)");
            UIFactory.SetLayoutElement(_loginBtn.Component.gameObject, minWidth: 130, minHeight: 22);
            _loginBtn.OnClick += OnLoginClicked;

            UIStyles.CreateSpacer(card, 10);

            // Search status
            _searchStatusLabel = UIFactory.CreateLabel(card, "SearchStatus", "", TextAnchor.MiddleLeft);
            _searchStatusLabel.fontSize = UIStyles.FontSizeSmall;
            _searchStatusLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_searchStatusLabel.gameObject, minHeight: 20);

            // Translation list (scroll view)
            var scrollObj = UIFactory.CreateScrollView(card, "TranslationScroll", out _translationListContent, out _);
            UIFactory.SetLayoutElement(scrollObj, minHeight: 100, flexibleHeight: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_translationListContent, false, false, true, true, 5, 5, 5, 5, 5);
            UIStyles.SetBackground(scrollObj, UIStyles.InputBackground);

            // Download status
            _downloadStatusLabel = UIFactory.CreateLabel(card, "DownloadStatus", "", TextAnchor.MiddleCenter);
            _downloadStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_downloadStatusLabel.gameObject, minHeight: 20);

            // Download button
            _downloadBtn = CreatePrimaryButton(card, "DownloadBtn", "Download Selected", 200);
            _downloadBtn.OnClick += OnDownloadClicked;
            _downloadBtn.Component.gameObject.SetActive(false);

            CreateFlexSpacer(_translationChoiceStep, "BottomSpacer");

            // Navigation buttons
            var buttonRow = CreateButtonRow(_translationChoiceStep);

            var backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            backBtn.OnClick += () => ShowStep(WizardStep.Hotkey);

            var ollamaBtn = CreateSecondaryButton(buttonRow, "OllamaBtn", "Configure Ollama");
            ollamaBtn.OnClick += () => ShowStep(WizardStep.OllamaConfig);

            var skipBtn = CreatePrimaryButton(buttonRow, "SkipBtn", "Skip →");
            skipBtn.OnClick += () => ShowStep(WizardStep.Complete);
        }

        private void OnTranslationChoiceEnter()
        {
            // Detect game if not already done
            if (_detectedGame == null)
            {
                _detectedGame = GameDetector.DetectGame();
                if (_detectedGame != null)
                {
                    _gameLabel.text = $"Game: {_detectedGame.name}";
                    if (_onlineMode && !_isSearchingTranslations)
                    {
                        SearchForTranslations();
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
                    _localTranslationsLabel.text += $" (from {serverState.Uploader})";
                }
            }
            else
            {
                _localTranslationsLabel.text = "";
            }

            // Update account status
            UpdateAccountStatus();
        }

        /// <summary>
        /// Updates the account status display. Call after login/logout.
        /// </summary>
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
        }

        private async void SearchForTranslations()
        {
            if (_isSearchingTranslations) return;
            if (!_onlineMode) return;

            _isSearchingTranslations = true;
            _searchStatusLabel.text = "Searching online...";
            _searchStatusLabel.color = Color.yellow;
            _availableTranslations.Clear();
            _selectedTranslation = null;
            ClearTranslationList();

            try
            {
                string targetLang = LanguageHelper.GetSystemLanguageName();
                TranslationSearchResult result = null;

                // Try Steam ID first
                if (!string.IsNullOrEmpty(_detectedGame?.steam_id))
                {
                    result = await ApiClient.SearchBysteamId(_detectedGame.steam_id, targetLang);
                }

                // Fallback to game name
                if ((result == null || !result.Success || result.Count == 0) && !string.IsNullOrEmpty(_detectedGame?.name))
                {
                    result = await ApiClient.SearchByGameName(_detectedGame.name, targetLang);
                }

                if (result != null && result.Success)
                {
                    _availableTranslations = result.Translations ?? new List<TranslationInfo>();
                    if (_availableTranslations.Count == 0)
                    {
                        _searchStatusLabel.text = "No translations found for your language";
                        _searchStatusLabel.color = Color.gray;
                    }
                    else
                    {
                        _searchStatusLabel.text = $"Found {_availableTranslations.Count} translation(s):";
                        _searchStatusLabel.color = Color.white;
                        PopulateTranslationList();
                        _downloadBtn.Component.gameObject.SetActive(true);
                    }
                }
                else
                {
                    _searchStatusLabel.text = result?.Error ?? "Search failed";
                    _searchStatusLabel.color = Color.red;
                }
            }
            catch (Exception e)
            {
                _searchStatusLabel.text = $"Error: {e.Message}";
                _searchStatusLabel.color = Color.red;
                TranslatorCore.LogWarning($"[Wizard] Search error: {e.Message}");
            }
            finally
            {
                _isSearchingTranslations = false;
            }
        }

        private void ClearTranslationList()
        {
            if (_translationListContent == null) return;

            foreach (Transform child in _translationListContent.transform)
            {
                GameObject.Destroy(child.gameObject);
            }
        }

        private void PopulateTranslationList()
        {
            ClearTranslationList();

            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            string currentUser = TranslatorCore.Config.api_user;

            int displayCount = Math.Min(5, _availableTranslations.Count);
            for (int i = 0; i < displayCount; i++)
            {
                var t = _availableTranslations[i];
                CreateTranslationListItem(t, isLoggedIn, currentUser);
            }

            // Auto-select first if none selected
            if (_selectedTranslation == null && _availableTranslations.Count > 0)
            {
                _selectedTranslation = _availableTranslations[0];
            }
        }

        private void CreateTranslationListItem(TranslationInfo translation, bool isLoggedIn, string currentUser)
        {
            var itemRow = UIFactory.CreateHorizontalGroup(_translationListContent, $"Item_{translation.Id}", false, false, true, true, 10);
            UIFactory.SetLayoutElement(itemRow, minHeight: 50, flexibleWidth: 9999);
            SetBackgroundColor(itemRow, new Color(0.15f, 0.15f, 0.15f, 0.8f));

            // Selection toggle
            var toggleObj = UIFactory.CreateToggle(itemRow, "SelectToggle", out var toggle, out var _);
            toggle.isOn = _selectedTranslation == translation;
            toggle.onValueChanged.AddListener((val) =>
            {
                if (val)
                {
                    _selectedTranslation = translation;
                    RefreshTranslationListSelection();
                }
            });
            UIFactory.SetLayoutElement(toggleObj, minWidth: 25);

            // Info column
            var infoCol = UIFactory.CreateVerticalGroup(itemRow, "InfoCol", false, false, true, true, 2);
            UIFactory.SetLayoutElement(infoCol, flexibleWidth: 9999);

            // Title row
            string label = $"{translation.TargetLanguage} by {translation.Uploader}";
            bool isOwnTranslation = isLoggedIn && !string.IsNullOrEmpty(currentUser) &&
                translation.Uploader.Equals(currentUser, StringComparison.OrdinalIgnoreCase);
            if (isOwnTranslation) label += " (you)";

            var titleLabel = UIFactory.CreateLabel(infoCol, "Title", label, TextAnchor.MiddleLeft);
            titleLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(titleLabel.gameObject, minHeight: 20);

            // Details row
            var detailsLabel = UIFactory.CreateLabel(infoCol, "Details",
                $"{translation.LineCount} lines | +{translation.VoteCount} votes | {translation.Type}",
                TextAnchor.MiddleLeft);
            detailsLabel.fontSize = 11;
            detailsLabel.color = Color.gray;
            UIFactory.SetLayoutElement(detailsLabel.gameObject, minHeight: 18);
        }

        private void RefreshTranslationListSelection()
        {
            // Update all toggles to reflect current selection
            if (_translationListContent == null) return;

            foreach (Transform child in _translationListContent.transform)
            {
                var toggle = child.GetComponentInChildren<Toggle>();
                if (toggle != null)
                {
                    string itemName = child.name;
                    int id = 0;
                    if (itemName.StartsWith("Item_") && int.TryParse(itemName.Substring(5), out id))
                    {
                        toggle.isOn = _selectedTranslation != null && _selectedTranslation.Id == id;
                    }
                }
            }
        }

        private void OnLoginClicked()
        {
            TranslatorUIManager.LoginPanel?.SetActive(true);
        }

        private async void OnDownloadClicked()
        {
            if (_selectedTranslation == null || _isDownloading) return;

            _isDownloading = true;
            _downloadStatusLabel.text = "Downloading...";
            _downloadStatusLabel.color = Color.yellow;
            _downloadBtn.Component.interactable = false;

            try
            {
                var result = await ApiClient.Download(_selectedTranslation.Id);

                if (result.Success && !string.IsNullOrEmpty(result.Content))
                {
                    // Save to translations.json
                    System.IO.File.WriteAllText(TranslatorCore.CachePath, result.Content);
                    TranslatorCore.ReloadCache();

                    // Update server state
                    string currentUser = TranslatorCore.Config.api_user;
                    bool isOwner = !string.IsNullOrEmpty(currentUser) &&
                        _selectedTranslation.Uploader.Equals(currentUser, StringComparison.OrdinalIgnoreCase);

                    TranslatorCore.ServerState = new ServerTranslationState
                    {
                        Checked = true,
                        Exists = true,
                        IsOwner = isOwner,
                        SiteId = _selectedTranslation.Id,
                        Uploader = _selectedTranslation.Uploader,
                        Hash = result.FileHash ?? _selectedTranslation.FileHash,
                        Type = _selectedTranslation.Type,
                        Notes = _selectedTranslation.Notes
                    };

                    TranslatorCore.SaveAncestorCache();

                    _downloadStatusLabel.text = "Downloaded successfully!";
                    _downloadStatusLabel.color = Color.green;
                    TranslatorCore.LogInfo($"[Wizard] Downloaded translation from {_selectedTranslation.Uploader}");

                    await Task.Delay(1500);
                    ShowStep(WizardStep.Complete);
                }
                else
                {
                    _downloadStatusLabel.text = $"Download failed: {result.Error}";
                    _downloadStatusLabel.color = Color.red;
                }
            }
            catch (Exception e)
            {
                _downloadStatusLabel.text = $"Error: {e.Message}";
                _downloadStatusLabel.color = Color.red;
                TranslatorCore.LogWarning($"[Wizard] Download error: {e.Message}");
            }
            finally
            {
                _isDownloading = false;
                _downloadBtn.Component.interactable = true;
            }
        }

        private void CreateOllamaConfigStep()
        {
            _ollamaConfigStep = UIFactory.CreateVerticalGroup(ContentRoot, "OllamaConfigStep", true, false, true, true, 0);
            UIFactory.SetLayoutElement(_ollamaConfigStep, flexibleWidth: 9999, flexibleHeight: 9999);
            var stepLayout = _ollamaConfigStep.GetComponent<VerticalLayoutGroup>();
            if (stepLayout != null) stepLayout.childAlignment = TextAnchor.MiddleCenter;

            CreateFlexSpacer(_ollamaConfigStep, "TopSpacer");

            var card = CreateCard(_ollamaConfigStep, "Card", 300);

            CreateTitle(card, "Title", "Ollama Configuration");
            CreateDescription(card, "Description", "Configure local AI for offline translation");

            UIStyles.CreateSpacer(card, 10);

            // Enable toggle section
            var enableSection = CreateSection(card, "EnableSection");
            var enableRow = UIFactory.CreateHorizontalGroup(enableSection, "EnableRow", false, false, true, true, 10);
            UIFactory.SetLayoutElement(enableRow, minHeight: 28);

            var enableObj = UIFactory.CreateToggle(enableRow, "EnableToggle", out var enableToggle, out var enableLabel);
            enableToggle.isOn = _enableOllama;
            enableLabel.text = "";
            enableToggle.onValueChanged.AddListener((val) => _enableOllama = val);
            UIFactory.SetLayoutElement(enableObj, minWidth: 25);

            var enableTextLabel = UIFactory.CreateLabel(enableRow, "EnableTextLabel", "Enable Ollama (local AI)", TextAnchor.MiddleLeft);
            enableTextLabel.fontStyle = FontStyle.Bold;
            enableTextLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(enableTextLabel.gameObject, flexibleWidth: 9999);

            UIStyles.CreateSpacer(card, 10);

            // URL input section
            var urlSection = CreateSection(card, "UrlSection");

            var urlLabel = UIFactory.CreateLabel(urlSection, "UrlLabel", "Ollama URL:", TextAnchor.MiddleLeft);
            urlLabel.color = UIStyles.TextSecondary;
            urlLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(urlLabel.gameObject, minHeight: 18);

            var urlRow = UIFactory.CreateHorizontalGroup(urlSection, "UrlRow", false, false, true, true, 5);
            UIFactory.SetLayoutElement(urlRow, minHeight: 32);

            _ollamaUrlInput = UIFactory.CreateInputField(urlRow, "OllamaUrl", "http://localhost:11434");
            _ollamaUrlInput.Text = _ollamaUrl;
            _ollamaUrlInput.OnValueChanged += (val) => _ollamaUrl = val;
            UIFactory.SetLayoutElement(_ollamaUrlInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_ollamaUrlInput.Component.gameObject, UIStyles.InputBackground);

            var testBtn = CreateSecondaryButton(urlRow, "TestBtn", "Test", 70);
            testBtn.OnClick += TestOllamaConnection;

            _ollamaStatusLabel = UIFactory.CreateLabel(urlSection, "StatusLabel", "", TextAnchor.MiddleCenter);
            _ollamaStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_ollamaStatusLabel.gameObject, minHeight: 22);

            UIStyles.CreateSpacer(card, 10);

            // Info hint
            var infoHint = UIStyles.CreateHint(card, "InfoHint",
                "Recommended model: qwen3:8b\nInstall: ollama pull qwen3:8b\nRequires ~6-8 GB VRAM");
            UIFactory.SetLayoutElement(infoHint.gameObject, minHeight: 50);

            CreateFlexSpacer(_ollamaConfigStep, "BottomSpacer");

            var buttonRow = CreateButtonRow(_ollamaConfigStep);

            var backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            backBtn.OnClick += () =>
            {
                if (_onlineMode)
                    ShowStep(WizardStep.TranslationChoice);
                else
                    ShowStep(WizardStep.Hotkey);
            };

            var finishBtn = CreatePrimaryButton(buttonRow, "FinishBtn", "Finish Setup →");
            finishBtn.OnClick += () => ShowStep(WizardStep.Complete);
        }

        private void CreateCompleteStep()
        {
            _completeStep = UIFactory.CreateVerticalGroup(ContentRoot, "CompleteStep", true, false, true, true, 0);
            UIFactory.SetLayoutElement(_completeStep, flexibleWidth: 9999, flexibleHeight: 9999);
            var stepLayout = _completeStep.GetComponent<VerticalLayoutGroup>();
            if (stepLayout != null) stepLayout.childAlignment = TextAnchor.MiddleCenter;

            CreateFlexSpacer(_completeStep, "TopSpacer");

            var card = CreateCard(_completeStep, "Card", 280);

            // Success title with accent color
            var title = UIFactory.CreateLabel(card, "Title", "Setup Complete!", TextAnchor.MiddleCenter);
            title.fontSize = UIStyles.FontSizeTitle + 2;
            title.fontStyle = FontStyle.Bold;
            title.color = UIStyles.StatusSuccess;
            UIFactory.SetLayoutElement(title.gameObject, minHeight: UIStyles.TitleHeight);

            UIStyles.CreateSpacer(card, 15);

            var desc = UIFactory.CreateLabel(card, "Description",
                "You're all set!\n\n" +
                $"Press {GetHotkeyDisplayString()} to open settings at any time.\n\n" +
                "The translator will automatically detect text in the game\nand translate it to your language.",
                TextAnchor.MiddleCenter);
            desc.fontSize = UIStyles.FontSizeNormal;
            desc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(desc.gameObject, minHeight: 120);

            UIStyles.CreateSpacer(card, 20);

            // Centered finish button inside card
            var finishBtn = CreatePrimaryButton(card, "FinishBtn", "Start Translating!", 200);
            finishBtn.OnClick += FinishWizard;
            // Style with success color
            UIStyles.SetBackground(finishBtn.Component.gameObject, UIStyles.ButtonSuccess);

            CreateFlexSpacer(_completeStep, "BottomSpacer");
        }

        private void ShowStep(WizardStep step)
        {
            _currentStep = step;

            _welcomeStep?.SetActive(false);
            _onlineModeStep?.SetActive(false);
            _hotkeyStep?.SetActive(false);
            _translationChoiceStep?.SetActive(false);
            _ollamaConfigStep?.SetActive(false);
            _completeStep?.SetActive(false);

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
        }

        private string GetHotkeyDisplayString()
        {
            string result = "";
            if (_hotkeyCtrl) result += "Ctrl+";
            if (_hotkeyAlt) result += "Alt+";
            if (_hotkeyShift) result += "Shift+";
            result += _hotkey;
            return result;
        }

        private void UpdateHotkeyDisplay()
        {
            if (_hotkeyLabel != null)
            {
                _hotkeyLabel.text = GetHotkeyDisplayString();
                _hotkeyLabel.color = UIStyles.TextAccent;
            }
            if (_setHotkeyBtn != null)
                _setHotkeyBtn.ButtonText.text = _hotkey ?? "F10";
        }

        private void OnSetHotkeyClicked()
        {
            _isCapturingHotkey = true;
            if (_hotkeyLabel != null)
            {
                _hotkeyLabel.text = "Press any key...";
                _hotkeyLabel.color = UIStyles.StatusWarning;
            }
            if (_setHotkeyBtn != null)
                _setHotkeyBtn.ButtonText.text = "...";

            // Unfocus the button to allow keyboard capture
            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);

            TranslatorCore.LogInfo("[Wizard] Hotkey capture started - waiting for key press");
        }

        private async void TestOllamaConnection()
        {
            if (_ollamaStatusLabel == null) return;

            _ollamaStatusLabel.text = "Testing...";
            _ollamaStatusLabel.color = Color.yellow;

            try
            {
                bool success = await TranslatorCore.TestOllamaConnection(_ollamaUrl);
                if (success)
                {
                    _ollamaStatusLabel.text = "Connection successful!";
                    _ollamaStatusLabel.color = Color.green;
                }
                else
                {
                    _ollamaStatusLabel.text = "Connection failed";
                    _ollamaStatusLabel.color = Color.red;
                }
            }
            catch (Exception e)
            {
                _ollamaStatusLabel.text = $"Error: {e.Message}";
                _ollamaStatusLabel.color = Color.red;
            }
        }

        private void FinishWizard()
        {
            TranslatorCore.Config.online_mode = _onlineMode;
            TranslatorCore.Config.settings_hotkey = GetHotkeyDisplayString();
            TranslatorCore.Config.enable_ollama = _enableOllama;
            TranslatorCore.Config.ollama_url = _ollamaUrl;
            TranslatorCore.Config.first_run_completed = true;
            TranslatorCore.SaveConfig();

            SetActive(false);
            TranslatorUIManager.ShowMain();
        }

        protected override void OnClosePanelClicked()
        {
            // Don't allow closing wizard with X button during first run
            // User must complete the wizard
        }
    }
}
