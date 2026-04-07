using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Options/configuration panel with all settings organized in tabs.
    /// Fonts, Exclusions, and Images tabs have been moved to TranslationParametersPanel.
    /// </summary>
    public class OptionsPanel : TranslatorPanelBase
    {
        public override string Name => "Options";
        public override int MinWidth => 580;
        public override int MinHeight => 400;
        public override int PanelWidth => 600;
        public override int PanelHeight => 520;

        protected override int MinPanelHeight => 400;

        // Tab system
        private TabBar _tabBar;

        // General section
        private Toggle _enableTranslationsToggle;
        private Toggle _translateModUIToggle;
        private SearchableDropdown _sourceLanguageDropdown;
        private SearchableDropdown _targetLanguageDropdown;
        private string[] _languages;
        private string[] _sourceLanguages;

        // Language section containers for conditional display
        private GameObject _languagesEditableSection;
        private GameObject _languagesLockedSection;
        private Text _lockedSourceLangValue;
        private Text _lockedTargetLangValue;

        // Interface section
        private Text _resetWindowsStatusLabel;
        private Toggle _disableEventSystemOverrideToggle;

        // Tab sizing
        private bool _tabHeightFixed = false;

        // Hotkey section
        private HotkeyCapture _hotkeyCapture;

        // Translation section
        private Toggle _captureKeysOnlyToggle;
        private SearchableDropdown _backendTypeDropdown; // "LLM (AI)" / "Translation API"
        private static readonly string[] BackendTypeOptions = { "LLM (AI)", "Translation API" };
        private Toggle _enableTranslationBackendToggle;
        private GameObject _backendTypeSection;

        // LLM section
        private GameObject _llmSection;
        private InputFieldRef _aiUrlInput;
        private InputFieldRef _aiApiKeyInput;
        private SearchableDropdown _modelDropdown;
        private InputFieldRef _gameContextInput;
        private Toggle _strictSourceToggle;
        private Text _aiTestStatusLabel;

        // Translation API section (contains provider dropdown + Google/DeepL sub-sections)
        private GameObject _translationApiSection;
        private SearchableDropdown _providerDropdown;
        private static readonly string[] ProviderOptions = { "Google Translate", "DeepL" };

        // Google section
        private GameObject _googleSection;
        private InputFieldRef _googleApiKeyInput;
        private Text _googleTestStatusLabel;

        // DeepL section
        private GameObject _deeplSection;
        private InputFieldRef _deeplApiKeyInput;
        private Toggle _deeplUseFreeToggle;
        private Text _deeplTestStatusLabel;

        // Rate limit
        private InputFieldRef _rateLimitDelayInput;

        // Online section
        private Toggle _onlineModeToggle;
        private Toggle _checkUpdatesToggle;
        private Toggle _notifyUpdatesToggle;
        private Toggle _autoDownloadToggle;
        private Toggle _notificationsEnabledToggle;
        private SearchableDropdown _notificationPositionDropdown;
        private Toggle _checkModUpdatesToggle;
        private ButtonRef _checkModUpdatesNowBtn;
        private Text _checkModUpdatesStatusLabel;

        // Apply button tracking
        private ButtonRef _applyBtn;
        private ConfigSnapshot _initialSnapshot;
        private bool _isLoadingSettings;

        /// <summary>
        /// Snapshot of config values taken when panel opens.
        /// Used to detect changes and update Apply button text.
        /// </summary>
        private class ConfigSnapshot
        {
            public bool enable_translations;
            public bool translate_mod_ui;
            public string source_language;
            public string target_language;
            public string settings_hotkey;
            public bool capture_keys_only;
            public string translation_backend;
            public string ai_url;
            public string ai_api_key;
            public string ai_model;
            public string game_context;
            public bool strict_source_language;
            public string google_api_key;
            public string deepl_api_key;
            public bool deepl_use_free;
            public float rate_limit_retry_delay;
            public bool online_mode;
            public bool check_update_on_start;
            public bool notify_updates;
            public bool notifications_enabled;
            public string notification_position;
            public bool auto_download;
            public bool check_mod_updates;
            public bool disable_eventsystem_override;

            public static ConfigSnapshot FromConfig()
            {
                return new ConfigSnapshot
                {
                    enable_translations = TranslatorCore.Config.enable_translations,
                    translate_mod_ui = TranslatorCore.Config.translate_mod_ui,
                    source_language = TranslatorCore.Config.source_language ?? "auto",
                    target_language = TranslatorCore.Config.target_language ?? "auto",
                    settings_hotkey = TranslatorCore.Config.settings_hotkey ?? "F10",
                    capture_keys_only = TranslatorCore.Config.capture_keys_only,
                    translation_backend = TranslatorCore.Config.translation_backend ?? "none",
                    ai_url = TranslatorCore.Config.ai_url ?? "http://localhost:11434",
                    ai_api_key = TranslatorCore.Config.ai_api_key ?? "",
                    ai_model = TranslatorCore.Config.ai_model ?? "",
                    game_context = TranslatorCore.Config.game_context ?? "",
                    strict_source_language = TranslatorCore.Config.strict_source_language,
                    google_api_key = TranslatorCore.Config.google_api_key ?? "",
                    deepl_api_key = TranslatorCore.Config.deepl_api_key ?? "",
                    deepl_use_free = TranslatorCore.Config.deepl_use_free,
                    rate_limit_retry_delay = TranslatorCore.Config.rate_limit_retry_delay,
                    online_mode = TranslatorCore.Config.online_mode,
                    check_update_on_start = TranslatorCore.Config.sync.check_update_on_start,
                    notify_updates = TranslatorCore.Config.sync.notify_updates,
                    notifications_enabled = TranslatorCore.Config.sync.notifications_enabled,
                    notification_position = TranslatorCore.Config.sync.notification_position ?? "top-right",
                    auto_download = TranslatorCore.Config.sync.auto_download,
                    check_mod_updates = TranslatorCore.Config.sync.check_mod_updates,
                    disable_eventsystem_override = TranslatorCore.DisableEventSystemOverride
                };
            }
        }

        public OptionsPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Initialize language arrays
            var langs = LanguageHelper.GetLanguageNames();

            _sourceLanguages = new string[langs.Length + 1];
            _sourceLanguages[0] = "auto (Detect)";
            for (int i = 0; i < langs.Length; i++)
            {
                _sourceLanguages[i + 1] = langs[i];
            }

            _languages = new string[langs.Length + 1];
            _languages[0] = "auto (System)";
            for (int i = 0; i < langs.Length; i++)
            {
                _languages[i + 1] = langs[i];
            }

            _sourceLanguageDropdown = new SearchableDropdown("SourceLang", _sourceLanguages, "auto (Detect)", popupHeight: 250, showSearch: true);
            _targetLanguageDropdown = new SearchableDropdown("TargetLang", _languages, "auto (System)", popupHeight: 250, showSearch: true);
            _hotkeyCapture = new HotkeyCapture("F10");

            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Title
            var title = CreateTitle(scrollContent, "Title", "Options");
            RegisterUIText(title);

            UIStyles.CreateSpacer(scrollContent, 5);

            // Create tab bar
            _tabBar = new TabBar();
            _tabBar.CreateUI(scrollContent);

            // Register tab button texts for localization
            // (done after adding tabs)

            // Create tab contents
            var generalTab = _tabBar.AddTab("General");
            var hotkeysTab = _tabBar.AddTab("Hotkeys");
            var translationTab = _tabBar.AddTab("Translation");
            var onlineTab = _tabBar.AddTab("Online");

            // Register tab texts for localization
            foreach (var text in _tabBar.GetTabButtonTexts())
            {
                RegisterUIText(text);
            }

            // Build each tab's content
            CreateGeneralTabContent(generalTab);
            CreateHotkeysTabContent(hotkeysTab);
            CreateTranslationTabContent(translationTab);
            CreateOnlineTabContent(onlineTab);

            // Tab height will be fixed on first display (see SetActive)

            // Buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);
            RegisterUIText(cancelBtn.ButtonText);

            _applyBtn = CreatePrimaryButton(buttonRow, "ApplyBtn", "Apply");
            _applyBtn.OnClick += OnApplyClicked;
            RegisterUIText(_applyBtn.ButtonText);

            // Setup change listeners for tracking pending changes
            SetupChangeListeners();
        }

        private void CreateGeneralTabContent(GameObject parent)
        {
            // stretchVertically: true = card expands to fill tab space, gray only as border
            var card = CreateAdaptiveCard(parent, "GeneralCard", PanelWidth - 60, stretchVertically: true);

            // Enable Translations toggle
            var transToggleObj = UIFactory.CreateToggle(card, "EnableTranslationsToggle", out _enableTranslationsToggle, out var transLabel);
            transLabel.text = " Enable Translations";
            transLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(transToggleObj, minHeight: UIStyles.RowHeightMedium);
            RegisterUIText(transLabel);

            UIStyles.CreateSpacer(card, 5);

            // Translate mod UI toggle
            var modUIObj = UIFactory.CreateToggle(card, "TranslateModUIToggle", out _translateModUIToggle, out var modUILabel);
            modUILabel.text = " Translate mod interface";
            modUILabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modUIObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(modUILabel);

            var modUIHint = UIStyles.CreateHint(card, "ModUIHint", "Translate this mod's own buttons and labels");
            RegisterUIText(modUIHint);

            UIStyles.CreateSpacer(card, 10);

            // === NOTIFICATION OVERLAY SECTION ===
            var notifSectionTitle = UIStyles.CreateSectionTitle(card, "NotificationsLabel", "Notification Overlay");
            RegisterUIText(notifSectionTitle);

            var notifEnabledObj = UIFactory.CreateToggle(card, "NotifEnabledToggle", out _notificationsEnabledToggle, out var notifEnabledLabel);
            notifEnabledLabel.text = " Show notification overlay";
            notifEnabledLabel.color = UIStyles.TextSecondary;
            UIHelpers.AddToggleListener(_notificationsEnabledToggle, OnNotificationsEnabledChanged);
            UIFactory.SetLayoutElement(notifEnabledObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(notifEnabledLabel);

            var posRow = UIStyles.CreateFormRow(card, "NotifPosRow", UIStyles.RowHeightMedium, 5);
            var posLabel = UIFactory.CreateLabel(posRow, "NotifPosLabel", "Position:", TextAnchor.MiddleLeft);
            posLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(posLabel.gameObject, minWidth: 60);
            RegisterUIText(posLabel);

            _notificationPositionDropdown = new SearchableDropdown(
                "NotifPosition",
                new[] { "Top-Right", "Top-Left", "Bottom-Right", "Bottom-Left" },
                "Top-Right",
                popupHeight: 150,
                showSearch: false
            );
            var posDropdownObj = _notificationPositionDropdown.CreateUI(posRow, (_) => { UpdateApplyButtonText(); });
            UIFactory.SetLayoutElement(posDropdownObj, minWidth: 140, minHeight: UIStyles.InputHeight);

            UIStyles.CreateSpacer(card, 10);

            // === ADVANCED SECTION ===
            var advancedSectionTitle = UIStyles.CreateSectionTitle(card, "AdvancedLabel", "Advanced");
            RegisterUIText(advancedSectionTitle);

            // Disable EventSystem Override toggle (per-game setting stored in translations.json)
            var eventSystemObj = UIFactory.CreateToggle(card, "DisableEventSystemToggle", out _disableEventSystemOverrideToggle, out var eventSystemLabel);
            eventSystemLabel.text = " Disable UI input interception";
            eventSystemLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(eventSystemObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(eventSystemLabel);

            var eventSystemHint = UIStyles.CreateHint(card, "EventSystemHint", "Enable if game's UI animations or menus don't work. Requires game restart.");
            RegisterUIText(eventSystemHint);

            UIStyles.CreateSpacer(card, 10);

            // === EDITABLE LANGUAGES SECTION ===
            _languagesEditableSection = UIFactory.CreateVerticalGroup(card, "LanguagesEditableSection", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_languagesEditableSection, flexibleWidth: 9999);

            var langSectionTitle = UIStyles.CreateSectionTitle(_languagesEditableSection, "LangLabel", "Languages");
            RegisterUIText(langSectionTitle);

            // Source Language
            var sourceLangLabel = UIFactory.CreateLabel(_languagesEditableSection, "SourceLangLabel", "Source Language:", TextAnchor.MiddleLeft);
            sourceLangLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(sourceLangLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(sourceLangLabel);

            _sourceLanguageDropdown.CreateUI(_languagesEditableSection, OnSourceLanguageChanged, width: 200);

            UIStyles.CreateSpacer(_languagesEditableSection, 5);

            // Target Language
            var targetLangLabel = UIFactory.CreateLabel(_languagesEditableSection, "TargetLangLabel", "Target Language:", TextAnchor.MiddleLeft);
            targetLangLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(targetLangLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(targetLangLabel);

            _targetLanguageDropdown.CreateUI(_languagesEditableSection, width: 200);

            // === LOCKED LANGUAGES SECTION ===
            _languagesLockedSection = UIFactory.CreateVerticalGroup(card, "LanguagesLockedSection", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_languagesLockedSection, flexibleWidth: 9999);

            var lockedHeader = UIFactory.CreateLabel(_languagesLockedSection, "LockedHeader", "Languages (locked - translation uploaded):", TextAnchor.MiddleLeft);
            lockedHeader.color = UIStyles.StatusWarning;
            lockedHeader.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(lockedHeader.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(lockedHeader);

            var sourceRow = UIStyles.CreateFormRow(_languagesLockedSection, "SourceRow", UIStyles.RowHeightNormal, 5);
            var sourceLabel = UIFactory.CreateLabel(sourceRow, "SourceLabel", "Source:", TextAnchor.MiddleLeft);
            sourceLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(sourceLabel.gameObject, minWidth: 60);
            RegisterUIText(sourceLabel);

            _lockedSourceLangValue = UIFactory.CreateLabel(sourceRow, "SourceValue", "-", TextAnchor.MiddleLeft);
            _lockedSourceLangValue.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_lockedSourceLangValue.gameObject, flexibleWidth: 9999);

            var targetRow = UIStyles.CreateFormRow(_languagesLockedSection, "TargetRow", UIStyles.RowHeightNormal, 5);
            var targetLabel2 = UIFactory.CreateLabel(targetRow, "TargetLabel", "Target:", TextAnchor.MiddleLeft);
            targetLabel2.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(targetLabel2.gameObject, minWidth: 60);
            RegisterUIText(targetLabel2);

            _lockedTargetLangValue = UIFactory.CreateLabel(targetRow, "TargetValue", "-", TextAnchor.MiddleLeft);
            _lockedTargetLangValue.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_lockedTargetLangValue.gameObject, flexibleWidth: 9999);

            _languagesLockedSection.SetActive(false);

            // === INTERFACE SECTION ===
            UIStyles.CreateSpacer(card, 15);

            var interfaceSectionTitle = UIStyles.CreateSectionTitle(card, "InterfaceLabel", "Interface");
            RegisterUIText(interfaceSectionTitle);

            var resetRow = UIStyles.CreateFormRow(card, "ResetRow", UIStyles.RowHeightNormal, 5);

            var resetBtn = CreateSecondaryButton(resetRow, "ResetWindowsBtn", "Reset Window Positions", 160);
            resetBtn.OnClick += OnResetWindowPositionsClicked;
            RegisterUIText(resetBtn.ButtonText);

            _resetWindowsStatusLabel = UIFactory.CreateLabel(resetRow, "ResetStatus", "", TextAnchor.MiddleLeft);
            _resetWindowsStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_resetWindowsStatusLabel.gameObject, flexibleWidth: 9999);
        }

        private void CreateHotkeysTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "HotkeysCard", PanelWidth - 60, stretchVertically: true);

            var sectionTitle = UIStyles.CreateSectionTitle(card, "SettingsHotkeyLabel", "Settings Panel");
            RegisterUIText(sectionTitle);

            var hint = UIStyles.CreateHint(card, "HotkeyHint", "Press the key combination to open/close the settings panel");
            RegisterUIText(hint);

            UIStyles.CreateSpacer(card, 5);

            _hotkeyCapture.CreateUI(card);

            UIStyles.CreateSpacer(card, 15);

            // Placeholder for future hotkeys
            var futureLabel = UIFactory.CreateLabel(card, "FutureLabel", "More hotkeys coming soon...", TextAnchor.MiddleCenter);
            futureLabel.color = UIStyles.TextMuted;
            futureLabel.fontSize = UIStyles.FontSizeSmall;
            futureLabel.fontStyle = FontStyle.Italic;
            UIFactory.SetLayoutElement(futureLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(futureLabel);
        }

        private void CreateTranslationTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "TranslationCard", PanelWidth - 60, stretchVertically: true);

            // Capture keys only section
            var captureSectionTitle = UIStyles.CreateSectionTitle(card, "CaptureLabel", "Manual Mode");
            RegisterUIText(captureSectionTitle);

            var captureObj = UIFactory.CreateToggle(card, "CaptureKeysToggle", out _captureKeysOnlyToggle, out var captureLabel);
            captureLabel.text = " Capture keys only (no translation)";
            captureLabel.color = UIStyles.TextSecondary;
            UIHelpers.AddToggleListener(_captureKeysOnlyToggle, OnCaptureKeysOnlyChanged);
            UIFactory.SetLayoutElement(captureObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(captureLabel);

            var captureHint = UIStyles.CreateHint(card, "CaptureHint", "Saves texts without translating - for manual translation");
            RegisterUIText(captureHint);

            UIStyles.CreateSpacer(card, 15);

            // === AUTO-TRANSLATION ===
            var backendSectionTitle = UIStyles.CreateSectionTitle(card, "BackendLabel", "Auto-Translation");
            RegisterUIText(backendSectionTitle);

            // Enable toggle
            var enableObj = UIFactory.CreateToggle(card, "EnableTransBackendToggle", out _enableTranslationBackendToggle, out var enableLabel);
            enableLabel.text = " Enable auto-translation";
            enableLabel.color = UIStyles.TextPrimary;
            UIHelpers.AddToggleListener(_enableTranslationBackendToggle, OnEnableTranslationBackendChanged);
            UIFactory.SetLayoutElement(enableObj, minHeight: UIStyles.RowHeightMedium);
            RegisterUIText(enableLabel);

            // Backend type section (shown when enabled)
            _backendTypeSection = UIFactory.CreateVerticalGroup(card, "BackendTypeSection", false, false, true, true, 5);
            UIFactory.SetLayoutElement(_backendTypeSection, flexibleWidth: 9999);

            // Backend type dropdown: LLM (AI) / Translation API
            var typeRow = UIStyles.CreateFormRow(_backendTypeSection, "TypeRow", UIStyles.RowHeightMedium, 5);
            var typeLabel = UIFactory.CreateLabel(typeRow, "TypeLabel", "Type:", TextAnchor.MiddleLeft);
            typeLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(typeLabel.gameObject, minWidth: 40);
            RegisterUIText(typeLabel);

            _backendTypeDropdown = new SearchableDropdown(
                "BackendTypeDropdown", BackendTypeOptions, "LLM (AI)", popupHeight: 100, showSearch: false);
            var typeObj = _backendTypeDropdown.CreateUI(typeRow, OnBackendTypeChanged);
            UIFactory.SetLayoutElement(typeObj, minWidth: 160, minHeight: UIStyles.InputHeight);

            UIStyles.CreateSpacer(_backendTypeSection, 5);

            // === LLM SECTION ===
            _llmSection = UIFactory.CreateVerticalGroup(_backendTypeSection, "LLMSection", false, false, true, true, 3);
            UIFactory.SetLayoutElement(_llmSection, flexibleWidth: 9999);

            // URL row
            var urlRow = UIStyles.CreateFormRow(_llmSection, "UrlRow", UIStyles.InputHeight, 5);
            var urlLabel = UIFactory.CreateLabel(urlRow, "UrlLabel", "URL:", TextAnchor.MiddleLeft);
            urlLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(urlLabel.gameObject, minWidth: 45);
            RegisterExcluded(urlLabel);

            _aiUrlInput = UIFactory.CreateInputField(urlRow, "AIUrl", "http://localhost:11434");
            UIFactory.SetLayoutElement(_aiUrlInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_aiUrlInput.Component.gameObject, UIStyles.InputBackground);

            var testBtn = CreateSecondaryButton(urlRow, "TestBtn", "Test", 60);
            testBtn.OnClick += TestAIConnection;
            RegisterUIText(testBtn.ButtonText);

            _aiTestStatusLabel = UIFactory.CreateLabel(_llmSection, "TestStatus", "", TextAnchor.MiddleLeft);
            _aiTestStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_aiTestStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(_aiTestStatusLabel);

            // API Key row
            var keyRow = UIStyles.CreateFormRow(_llmSection, "KeyRow", UIStyles.InputHeight, 5);
            var keyLabel = UIFactory.CreateLabel(keyRow, "KeyLabel", "API Key:", TextAnchor.MiddleLeft);
            keyLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(keyLabel.gameObject, minWidth: 55);
            RegisterExcluded(keyLabel);

            _aiApiKeyInput = UIFactory.CreateInputField(keyRow, "AIApiKey", "");
            _aiApiKeyInput.Component.contentType = UnityEngine.UI.InputField.ContentType.Password;
            UIFactory.SetLayoutElement(_aiApiKeyInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_aiApiKeyInput.Component.gameObject, UIStyles.InputBackground);

            var keyHint = UIStyles.CreateHint(_llmSection, "KeyHint", "Optional for local servers (Ollama, LM Studio)");
            RegisterUIText(keyHint);

            // Model row
            var modelRow = UIStyles.CreateFormRow(_llmSection, "ModelRow", UIStyles.InputHeight, 5);
            var modelLabel = UIFactory.CreateLabel(modelRow, "ModelLabel", "Model:", TextAnchor.MiddleLeft);
            modelLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modelLabel.gameObject, minWidth: 50);
            RegisterUIText(modelLabel);

            _modelDropdown = new SearchableDropdown("ModelDropdown", new string[0], null, 200, false);
            var modelObj = _modelDropdown.CreateUI(modelRow, (val) => { });
            UIFactory.SetLayoutElement(modelObj, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);

            var refreshBtn = CreateSecondaryButton(modelRow, "RefreshBtn", "Refresh", 60);
            refreshBtn.OnClick += RefreshModels;
            RegisterUIText(refreshBtn.ButtonText);

            var modelHint = UIStyles.CreateHint(_llmSection, "ModelHint", "Select a model from your server");
            RegisterUIText(modelHint);

            UIStyles.CreateSpacer(_llmSection, 5);

            // Game context
            var contextLabel = UIFactory.CreateLabel(_llmSection, "ContextLabel", "Game Context (optional):", TextAnchor.MiddleLeft);
            contextLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(contextLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(contextLabel);

            _gameContextInput = UIFactory.CreateInputField(_llmSection, "ContextInput", "e.g., RPG game with medieval setting");
            _gameContextInput.Component.lineType = UnityEngine.UI.InputField.LineType.MultiLineNewline;
            UIFactory.SetLayoutElement(_gameContextInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.MultiLineMedium);
            UIStyles.SetBackground(_gameContextInput.Component.gameObject, UIStyles.InputBackground);

            var contextHint = UIStyles.CreateHint(_llmSection, "ContextHint", "Helps the AI understand game vocabulary");
            RegisterUIText(contextHint);

            UIStyles.CreateSpacer(_llmSection, 5);

            // Strict source language toggle
            var strictObj = UIFactory.CreateToggle(_llmSection, "StrictSourceToggle", out _strictSourceToggle, out var strictLabel);
            strictLabel.text = " Strict source language detection";
            strictLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(strictObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(strictLabel);

            var strictHint = UIStyles.CreateHint(_llmSection, "StrictHint", "Skip texts not matching source language (LLM only)");
            RegisterUIText(strictHint);

            // === TRANSLATION API SECTION (contains provider dropdown + sub-sections) ===
            _translationApiSection = UIFactory.CreateVerticalGroup(_backendTypeSection, "TranslationApiSection", false, false, true, true, 3);
            UIFactory.SetLayoutElement(_translationApiSection, flexibleWidth: 9999);

            // Provider dropdown
            var providerRow = UIStyles.CreateFormRow(_translationApiSection, "ProviderRow", UIStyles.RowHeightMedium, 5);
            var providerLabel = UIFactory.CreateLabel(providerRow, "ProviderLabel", "Provider:", TextAnchor.MiddleLeft);
            providerLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(providerLabel.gameObject, minWidth: 55);
            RegisterUIText(providerLabel);

            _providerDropdown = new SearchableDropdown(
                "ProviderDropdown", ProviderOptions, "Google Translate", popupHeight: 100, showSearch: false);
            var providerObj = _providerDropdown.CreateUI(providerRow, OnProviderChanged);
            UIFactory.SetLayoutElement(providerObj, minWidth: 160, minHeight: UIStyles.InputHeight);

            UIStyles.CreateSpacer(_translationApiSection, 5);

            // === GOOGLE SECTION ===
            _googleSection = UIFactory.CreateVerticalGroup(_translationApiSection, "GoogleSection", false, false, true, true, 3);
            UIFactory.SetLayoutElement(_googleSection, flexibleWidth: 9999);

            var googleKeyRow = UIStyles.CreateFormRow(_googleSection, "GoogleKeyRow", UIStyles.InputHeight, 5);
            var googleKeyLabel = UIFactory.CreateLabel(googleKeyRow, "GoogleKeyLabel", "API Key:", TextAnchor.MiddleLeft);
            googleKeyLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(googleKeyLabel.gameObject, minWidth: 55);
            RegisterExcluded(googleKeyLabel);

            _googleApiKeyInput = UIFactory.CreateInputField(googleKeyRow, "GoogleApiKey", "");
            _googleApiKeyInput.Component.contentType = UnityEngine.UI.InputField.ContentType.Password;
            UIFactory.SetLayoutElement(_googleApiKeyInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_googleApiKeyInput.Component.gameObject, UIStyles.InputBackground);

            var googleTestBtn = CreateSecondaryButton(googleKeyRow, "GoogleTestBtn", "Test", 60);
            googleTestBtn.OnClick += TestGoogleConnection;
            RegisterUIText(googleTestBtn.ButtonText);

            _googleTestStatusLabel = UIFactory.CreateLabel(_googleSection, "GoogleTestStatus", "", TextAnchor.MiddleLeft);
            _googleTestStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_googleTestStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            var googleHint = UIStyles.CreateHint(_googleSection, "GoogleHint", "Requires a Google Cloud API key with Translation API enabled");
            RegisterUIText(googleHint);

            // === DEEPL SECTION ===
            _deeplSection = UIFactory.CreateVerticalGroup(_translationApiSection, "DeepLSection", false, false, true, true, 3);
            UIFactory.SetLayoutElement(_deeplSection, flexibleWidth: 9999);

            var deeplKeyRow = UIStyles.CreateFormRow(_deeplSection, "DeepLKeyRow", UIStyles.InputHeight, 5);
            var deeplKeyLabel = UIFactory.CreateLabel(deeplKeyRow, "DeepLKeyLabel", "API Key:", TextAnchor.MiddleLeft);
            deeplKeyLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(deeplKeyLabel.gameObject, minWidth: 55);
            RegisterExcluded(deeplKeyLabel);

            _deeplApiKeyInput = UIFactory.CreateInputField(deeplKeyRow, "DeepLApiKey", "");
            _deeplApiKeyInput.Component.contentType = UnityEngine.UI.InputField.ContentType.Password;
            UIFactory.SetLayoutElement(_deeplApiKeyInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_deeplApiKeyInput.Component.gameObject, UIStyles.InputBackground);

            var deeplTestBtn = CreateSecondaryButton(deeplKeyRow, "DeepLTestBtn", "Test", 60);
            deeplTestBtn.OnClick += TestDeepLConnection;
            RegisterUIText(deeplTestBtn.ButtonText);

            _deeplTestStatusLabel = UIFactory.CreateLabel(_deeplSection, "DeepLTestStatus", "", TextAnchor.MiddleLeft);
            _deeplTestStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_deeplTestStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            var deeplFreeObj = UIFactory.CreateToggle(_deeplSection, "DeepLFreeToggle", out _deeplUseFreeToggle, out var deeplFreeLabel);
            deeplFreeLabel.text = " Use Free API (api-free.deepl.com)";
            deeplFreeLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(deeplFreeObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(deeplFreeLabel);

            var deeplHint = UIStyles.CreateHint(_deeplSection, "DeepLHint", "Uncheck for Pro API (api.deepl.com). Free plan: 500k chars/month");
            RegisterUIText(deeplHint);

            // Rate limit retry delay (shared across all backends)
            UIStyles.CreateSpacer(_backendTypeSection, 10);
            var rateLimitRow = UIStyles.CreateFormRow(_backendTypeSection, "RateLimitRow", UIStyles.InputHeight, 5);
            var rateLimitLabel = UIFactory.CreateLabel(rateLimitRow, "RateLimitLabel", "Rate limit retry:", TextAnchor.MiddleLeft);
            rateLimitLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(rateLimitLabel.gameObject, minWidth: 110);
            RegisterUIText(rateLimitLabel);

            _rateLimitDelayInput = UIFactory.CreateInputField(rateLimitRow, "RateLimitDelay", "3");
            _rateLimitDelayInput.Component.contentType = UnityEngine.UI.InputField.ContentType.DecimalNumber;
            UIFactory.SetLayoutElement(_rateLimitDelayInput.Component.gameObject, minWidth: 50, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_rateLimitDelayInput.Component.gameObject, UIStyles.InputBackground);

            var rateLimitUnit = UIFactory.CreateLabel(rateLimitRow, "RateLimitUnit", "seconds", TextAnchor.MiddleLeft);
            rateLimitUnit.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(rateLimitUnit.gameObject, flexibleWidth: 9999);
            RegisterUIText(rateLimitUnit);

            var rateLimitHint = UIStyles.CreateHint(_backendTypeSection, "RateLimitHint", "Delay before retrying after a rate limit error (HTTP 429)");
            RegisterUIText(rateLimitHint);

            // Initial visibility - all hidden until UpdateBackendSections
            _backendTypeSection.SetActive(false);
            _llmSection.SetActive(false);
            _translationApiSection.SetActive(false);
            _googleSection.SetActive(false);
            _deeplSection.SetActive(false);
        }

        private void CreateOnlineTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "OnlineCard", PanelWidth - 60, stretchVertically: true);

            var onlineToggleObj = UIFactory.CreateToggle(card, "OnlineModeToggle", out _onlineModeToggle, out var onlineLabel);
            onlineLabel.text = " Enable Online Mode";
            onlineLabel.color = UIStyles.TextPrimary;
            UIHelpers.AddToggleListener(_onlineModeToggle, OnOnlineModeChanged);
            UIFactory.SetLayoutElement(onlineToggleObj, minHeight: UIStyles.RowHeightMedium);
            RegisterUIText(onlineLabel);

            UIStyles.CreateSpacer(card, 10);

            // Translation sync section
            var syncSectionTitle = UIStyles.CreateSectionTitle(card, "SyncLabel", "Translation Sync");
            RegisterUIText(syncSectionTitle);

            var checkUpdatesObj = UIFactory.CreateToggle(card, "CheckUpdatesToggle", out _checkUpdatesToggle, out var checkLabel);
            checkLabel.text = " Check for translation updates on start";
            checkLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(checkUpdatesObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(checkLabel);

            var notifyObj = UIFactory.CreateToggle(card, "NotifyToggle", out _notifyUpdatesToggle, out var notifyLabel);
            notifyLabel.text = " Notify when translation updates available";
            notifyLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(notifyObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(notifyLabel);

            var autoDownloadObj = UIFactory.CreateToggle(card, "AutoDownloadToggle", out _autoDownloadToggle, out var autoLabel);
            autoLabel.text = " Auto-download translation updates (no conflicts)";
            autoLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(autoDownloadObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(autoLabel);

            UIStyles.CreateSpacer(card, 10);

            // Mod updates section
            var modSectionTitle = UIStyles.CreateSectionTitle(card, "ModUpdatesLabel", "Mod Updates");
            RegisterUIText(modSectionTitle);

            var modUpdatesRow = UIStyles.CreateFormRow(card, "ModUpdatesRow", UIStyles.RowHeightNormal, 5);

            var modUpdatesObj = UIFactory.CreateToggle(modUpdatesRow, "ModUpdatesToggle", out _checkModUpdatesToggle, out var modLabel);
            modLabel.text = " Check on startup";
            modLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modUpdatesObj, flexibleWidth: 9999);
            RegisterUIText(modLabel);

            _checkModUpdatesNowBtn = CreateSecondaryButton(modUpdatesRow, "CheckNowBtn", "Check Now", 90);
            _checkModUpdatesNowBtn.OnClick += OnCheckModUpdatesNowClicked;
            RegisterUIText(_checkModUpdatesNowBtn.ButtonText);

            _checkModUpdatesStatusLabel = UIFactory.CreateLabel(card, "ModUpdateStatus", "", TextAnchor.MiddleLeft);
            _checkModUpdatesStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_checkModUpdatesStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
        }

        private void OnSourceLanguageChanged(string newSource)
        {
            bool isAuto = newSource == "auto (Detect)";
            _strictSourceToggle.interactable = !isAuto && GetSelectedBackendConfig() == "llm" &&
                _enableTranslationBackendToggle.isOn && !_captureKeysOnlyToggle.isOn;
            if (isAuto && _strictSourceToggle.isOn)
            {
                _strictSourceToggle.isOn = false;
            }
            UpdateApplyButtonText();
        }

        public override void SetActive(bool active)
        {
            bool wasActive = Enabled;
            base.SetActive(active);
            if (active && !wasActive)
            {
                LoadCurrentSettings();

                // Fix tab height on first display (layouts need to be calculated first)
                if (!_tabHeightFixed && _tabBar != null)
                {
                    UniverseLib.RuntimeHelper.StartCoroutine(DelayedFixTabHeight());
                }
            }
        }

        private System.Collections.IEnumerator DelayedFixTabHeight()
        {
            // Wait a few frames for Unity to calculate layouts
            yield return null;
            yield return null;
            yield return null;

            if (_tabBar != null && _tabBar.ContentContainer != null)
            {
                float maxTabHeight = _tabBar.MeasureMaxContentHeight();
                if (maxTabHeight > 0)
                {
                    UIFactory.SetLayoutElement(_tabBar.ContentContainer, minHeight: Mathf.CeilToInt(maxTabHeight));
                    _tabHeightFixed = true;

                    // Recalculate panel size with the new fixed height
                    RecalculateSize();
                }
            }
        }

        public override void Update()
        {
            base.Update();
            _hotkeyCapture?.Update();

            // Poll toggle/dropdown state changes to update Apply button text.
            // We cannot use onValueChanged.AddListener on toggles because it fails on IL2CPP
            // (UnityAction delegate conversion issue). Polling is cheap (just bool comparisons)
            // and only runs while the panel is visible.
            if (Enabled)
            {
                UpdateApplyButtonText();
            }
        }

        private void LoadCurrentSettings()
        {
            _isLoadingSettings = true;

            // General
            _enableTranslationsToggle.isOn = TranslatorCore.Config.enable_translations;
            _translateModUIToggle.isOn = TranslatorCore.Config.translate_mod_ui;

            // Source language
            string configSourceLang = TranslatorCore.Config.source_language;
            if (string.IsNullOrEmpty(configSourceLang) || configSourceLang == "auto")
            {
                _sourceLanguageDropdown.SelectedValue = "auto (Detect)";
            }
            else
            {
                _sourceLanguageDropdown.SelectedValue = configSourceLang;
            }

            // Target language
            string configTargetLang = TranslatorCore.Config.target_language;
            if (string.IsNullOrEmpty(configTargetLang) || configTargetLang == "auto")
            {
                _targetLanguageDropdown.SelectedValue = "auto (System)";
            }
            else
            {
                _targetLanguageDropdown.SelectedValue = configTargetLang;
            }

            // Hotkey
            _hotkeyCapture.SetHotkey(TranslatorCore.Config.settings_hotkey ?? "F10");

            // Online mode (must be loaded BEFORE translation backend — UpdateBackendSections checks online state)
            _onlineModeToggle.isOn = TranslatorCore.Config.online_mode;
            _checkUpdatesToggle.isOn = TranslatorCore.Config.sync.check_update_on_start;
            _notifyUpdatesToggle.isOn = TranslatorCore.Config.sync.notify_updates;
            _autoDownloadToggle.isOn = TranslatorCore.Config.sync.auto_download;
            _checkModUpdatesToggle.isOn = TranslatorCore.Config.sync.check_mod_updates;
            _notificationsEnabledToggle.isOn = TranslatorCore.Config.sync.notifications_enabled;
            _notificationPositionDropdown.SelectedValue = PositionConfigToDisplay(TranslatorCore.Config.sync.notification_position);
            OnOnlineModeChanged(_onlineModeToggle.isOn);

            // Translation (Backend + Capture) — after online mode so UpdateBackendSections sees correct online state
            _captureKeysOnlyToggle.isOn = TranslatorCore.Config.capture_keys_only;
            _aiUrlInput.Text = TranslatorCore.Config.ai_url ?? "http://localhost:11434";
            _aiApiKeyInput.Text = TranslatorCore.Config.ai_api_key ?? "";
            _googleApiKeyInput.Text = TranslatorCore.Config.google_api_key ?? "";
            _deeplApiKeyInput.Text = TranslatorCore.Config.deepl_api_key ?? "";
            _deeplUseFreeToggle.isOn = TranslatorCore.Config.deepl_use_free;
            _rateLimitDelayInput.Text = TranslatorCore.Config.rate_limit_retry_delay.ToString();
            string currentModel = TranslatorCore.Config.ai_model ?? "";
            if (!string.IsNullOrEmpty(currentModel))
            {
                _modelDropdown.SetOptions(new[] { currentModel });
                _modelDropdown.SelectedValue = currentModel;
            }
            _gameContextInput.Text = TranslatorCore.Config.game_context ?? "";
            _strictSourceToggle.isOn = TranslatorCore.Config.strict_source_language;
            _aiTestStatusLabel.text = "";
            // Set dropdowns BEFORE the enable toggle (which triggers UpdateBackendSections)
            string backend = TranslatorCore.Config.translation_backend ?? "none";
            _backendTypeDropdown.SelectedValue = (backend == "google" || backend == "deepl") ? "Translation API" : "LLM (AI)";
            _providerDropdown.SelectedValue = backend == "deepl" ? "DeepL" : "Google Translate";
            _enableTranslationBackendToggle.isOn = (backend != "none");

            // Done loading — enable listeners and apply section visibility once
            _isLoadingSettings = false;
            UpdateBackendSections();

            // Advanced settings (per-game, stored in translations.json)
            _disableEventSystemOverrideToggle.isOn = TranslatorCore.DisableEventSystemOverride;

            // Update strict toggle based on source language
            OnSourceLanguageChanged(_sourceLanguageDropdown.SelectedValue);

            // Lock languages if translation exists on server
            UpdateLanguagesLocked();

            // CRITICAL: Always create snapshot, even if some UI refreshes above failed.
            // Without this, CountPendingChanges() returns 0 and Apply button stays "Close".
            _initialSnapshot = ConfigSnapshot.FromConfig();
            UpdateApplyButtonText();
        }

        private void UpdateLanguagesLocked()
        {
            bool locked = TranslatorCore.AreLanguagesLocked;

            if (_languagesEditableSection != null)
            {
                _languagesEditableSection.SetActive(!locked);
            }

            if (_languagesLockedSection != null)
            {
                _languagesLockedSection.SetActive(locked);

                if (locked && _lockedSourceLangValue != null && _lockedTargetLangValue != null)
                {
                    string sourceLang = TranslatorCore.Config.source_language;
                    string targetLang = TranslatorCore.Config.target_language;

                    _lockedSourceLangValue.text = string.IsNullOrEmpty(sourceLang) || sourceLang == "auto"
                        ? "Auto (Detect)"
                        : sourceLang;

                    _lockedTargetLangValue.text = string.IsNullOrEmpty(targetLang) || targetLang == "auto"
                        ? "Auto (System)"
                        : targetLang;
                }
            }
        }

        private void OnOnlineModeChanged(bool enabled)
        {
            _checkUpdatesToggle.interactable = enabled;
            _notifyUpdatesToggle.interactable = enabled;
            _autoDownloadToggle.interactable = enabled;
            _checkModUpdatesToggle.interactable = enabled;
            _checkModUpdatesNowBtn.Component.interactable = enabled;

            // Translation API availability depends on online mode
            if (!_isLoadingSettings) UpdateBackendSections();
        }

        private void OnNotificationsEnabledChanged(bool enabled)
        {
            _notificationPositionDropdown.SetInteractable(enabled);
            UpdateApplyButtonText();
        }

        private static string PositionConfigToDisplay(string config)
        {
            switch (config)
            {
                case "top-left": return "Top-Left";
                case "bottom-right": return "Bottom-Right";
                case "bottom-left": return "Bottom-Left";
                default: return "Top-Right";
            }
        }

        private static string PositionDisplayToConfig(string display)
        {
            switch (display)
            {
                case "Top-Left": return "top-left";
                case "Bottom-Right": return "bottom-right";
                case "Bottom-Left": return "bottom-left";
                default: return "top-right";
            }
        }

        private void OnResetWindowPositionsClicked()
        {
            try
            {
                // Clear all window preferences
                TranslatorCore.Config.window_preferences.panels.Clear();
                TranslatorCore.Config.window_preferences.screenWidth = 0;
                TranslatorCore.Config.window_preferences.screenHeight = 0;
                TranslatorCore.SaveConfig();

                _resetWindowsStatusLabel.text = "Positions reset! Reopen panels.";
                _resetWindowsStatusLabel.color = UIStyles.StatusSuccess;

                TranslatorCore.LogInfo("[Options] Window preferences reset");
            }
            catch (Exception e)
            {
                _resetWindowsStatusLabel.text = $"Error: {e.Message}";
                _resetWindowsStatusLabel.color = UIStyles.StatusError;
            }
        }

        private void OnCaptureKeysOnlyChanged(bool captureOnly)
        {
            if (_isLoadingSettings) return;
            UpdateBackendSections();
        }

        private void OnEnableTranslationBackendChanged(bool enabled)
        {
            if (_isLoadingSettings) return;
            UpdateBackendSections();
            UpdateApplyButtonText();
        }

        private void OnBackendTypeChanged(string selectedType)
        {
            if (_isLoadingSettings) return;
            UpdateBackendSections();
            UpdateApplyButtonText();
        }

        private void OnProviderChanged(string selectedProvider)
        {
            if (_isLoadingSettings) return;
            UpdateBackendSections();
            UpdateApplyButtonText();
        }

        private string GetSelectedBackendConfig()
        {
            if (_enableTranslationBackendToggle == null || !_enableTranslationBackendToggle.isOn)
                return "none";

            string type = _backendTypeDropdown?.SelectedValue ?? "LLM (AI)";
            if (type == "LLM (AI)") return "llm";

            // Translation API -> check provider
            string provider = _providerDropdown?.SelectedValue ?? "Google Translate";
            return provider == "DeepL" ? "deepl" : "google";
        }

        private void UpdateBackendSections()
        {
            bool captureOnly = _captureKeysOnlyToggle.isOn;
            bool enabled = _enableTranslationBackendToggle != null && _enableTranslationBackendToggle.isOn;

            _enableTranslationBackendToggle.interactable = !captureOnly;
            _backendTypeSection?.SetActive(!captureOnly && enabled);

            if (!enabled || captureOnly)
            {
                _llmSection?.SetActive(false);
                _translationApiSection?.SetActive(false);
                return;
            }

            // Translation APIs require online mode
            bool canUseTransApi = _onlineModeToggle != null && _onlineModeToggle.isOn;
            _backendTypeDropdown?.SetInteractable(canUseTransApi);
            if (!canUseTransApi && _backendTypeDropdown?.SelectedValue == "Translation API")
            {
                _backendTypeDropdown.SelectedValue = "LLM (AI)";
            }

            string type = _backendTypeDropdown?.SelectedValue ?? "LLM (AI)";
            bool isLLM = type == "LLM (AI)";

            _llmSection?.SetActive(isLLM);
            _translationApiSection?.SetActive(!isLLM);

            if (isLLM)
            {
                bool sourceIsAuto = _sourceLanguageDropdown.SelectedValue == "auto (Detect)";
                _strictSourceToggle.interactable = !sourceIsAuto;
            }

            if (!isLLM)
            {
                string provider = _providerDropdown?.SelectedValue ?? "Google Translate";
                _googleSection?.SetActive(provider == "Google Translate");
                _deeplSection?.SetActive(provider == "DeepL");
            }
        }

        private async void TestGoogleConnection()
        {
            string apiKey = _googleApiKeyInput?.Text;
            if (string.IsNullOrEmpty(apiKey))
            {
                _googleTestStatusLabel.text = "Enter an API key first";
                _googleTestStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            _googleTestStatusLabel.text = "Testing...";
            _googleTestStatusLabel.color = UIStyles.TextSecondary;

            bool success = await TranslatorCore.TestGoogleConnection(apiKey);

            TranslatorUIManager.RunOnMainThread(() =>
            {
                if (success)
                {
                    _googleTestStatusLabel.text = "Connected!";
                    _googleTestStatusLabel.color = UIStyles.StatusSuccess;
                }
                else
                {
                    _googleTestStatusLabel.text = "Failed - check API key";
                    _googleTestStatusLabel.color = UIStyles.StatusError;
                }
            });
        }

        private async void TestDeepLConnection()
        {
            string apiKey = _deeplApiKeyInput?.Text;
            if (string.IsNullOrEmpty(apiKey))
            {
                _deeplTestStatusLabel.text = "Enter an API key first";
                _deeplTestStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            _deeplTestStatusLabel.text = "Testing...";
            _deeplTestStatusLabel.color = UIStyles.TextSecondary;

            bool useFree = _deeplUseFreeToggle.isOn;
            bool success = await TranslatorCore.TestDeepLConnection(apiKey, useFree);

            TranslatorUIManager.RunOnMainThread(() =>
            {
                if (success)
                {
                    _deeplTestStatusLabel.text = "Connected!";
                    _deeplTestStatusLabel.color = UIStyles.StatusSuccess;
                }
                else
                {
                    _deeplTestStatusLabel.text = "Failed - check API key and plan type";
                    _deeplTestStatusLabel.color = UIStyles.StatusError;
                }
            });
        }

        private async void OnCheckModUpdatesNowClicked()
        {
            if (!TranslatorCore.Config.online_mode)
            {
                _checkModUpdatesStatusLabel.text = "Enable online mode first";
                _checkModUpdatesStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            _checkModUpdatesNowBtn.Component.interactable = false;
            _checkModUpdatesStatusLabel.text = "Checking...";
            _checkModUpdatesStatusLabel.color = UIStyles.TextSecondary;

            try
            {
                string currentVersion = PluginInfo.Version;
                string modLoaderType = TranslatorCore.Adapter?.ModLoaderType ?? "Unknown";

                var result = await GitHubUpdateChecker.CheckForUpdatesAsync(currentVersion, modLoaderType);

                var success = result.Success;
                var hasUpdate = result.HasUpdate;
                var latestVersion = result.LatestVersion;
                var error = result.Error;

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success && hasUpdate)
                    {
                        TranslatorUIManager.HasModUpdate = true;
                        TranslatorUIManager.ModUpdateInfo = result;
                        TranslatorUIManager.ModUpdateDismissed = false;

                        _checkModUpdatesStatusLabel.text = $"Update available: v{latestVersion}";
                        _checkModUpdatesStatusLabel.color = UIStyles.StatusSuccess;

                        TranslatorUIManager.MainPanel?.RefreshUI();
                    }
                    else if (success)
                    {
                        _checkModUpdatesStatusLabel.text = $"Up to date (v{currentVersion})";
                        _checkModUpdatesStatusLabel.color = UIStyles.StatusSuccess;
                    }
                    else
                    {
                        _checkModUpdatesStatusLabel.text = $"Error: {error}";
                        _checkModUpdatesStatusLabel.color = UIStyles.StatusError;
                    }

                    _checkModUpdatesNowBtn.Component.interactable = true;
                });
            }
            catch (System.Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _checkModUpdatesStatusLabel.text = $"Error: {errorMsg}";
                    _checkModUpdatesStatusLabel.color = UIStyles.StatusError;
                    _checkModUpdatesNowBtn.Component.interactable = true;
                });
            }
        }

        private async void TestAIConnection()
        {
            _aiTestStatusLabel.text = "Testing...";
            _aiTestStatusLabel.color = UIStyles.StatusWarning;

            string url = _aiUrlInput.Text;
            string apiKey = _aiApiKeyInput.Text;

            try
            {
                bool success = await TranslatorCore.TestAIConnection(url, apiKey);

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success)
                    {
                        _aiTestStatusLabel.text = "Connection successful!";
                        _aiTestStatusLabel.color = UIStyles.StatusSuccess;
                        // Auto-refresh models on successful test
                        RefreshModels();
                    }
                    else
                    {
                        _aiTestStatusLabel.text = "Connection failed";
                        _aiTestStatusLabel.color = UIStyles.StatusError;
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _aiTestStatusLabel.text = $"Error: {errorMsg}";
                    _aiTestStatusLabel.color = UIStyles.StatusError;
                });
            }
        }

        private async void RefreshModels()
        {
            string url = _aiUrlInput.Text;
            string apiKey = _aiApiKeyInput.Text;

            try
            {
                string[] models = await TranslatorCore.FetchModels(url, apiKey);

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (models.Length > 0)
                    {
                        string currentSelection = _modelDropdown.SelectedValue;
                        _modelDropdown.SetOptions(models);
                        // Keep current selection if still valid
                        if (!string.IsNullOrEmpty(currentSelection) && Array.IndexOf(models, currentSelection) >= 0)
                        {
                            _modelDropdown.SelectedValue = currentSelection;
                        }
                    }
                });
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Options] Failed to refresh models: {e.Message}");
            }
        }

        private void ApplySettings()
        {
            TranslatorCore.LogInfo("[Options] Applying settings...");
            try
            {
                // General
                TranslatorCore.Config.enable_translations = _enableTranslationsToggle.isOn;
                TranslatorCore.Config.translate_mod_ui = _translateModUIToggle.isOn;

                // Languages
                string selectedSourceLang = _sourceLanguageDropdown.SelectedValue;
                TranslatorCore.Config.source_language = selectedSourceLang == "auto (Detect)" ? "auto" : selectedSourceLang;

                string selectedTargetLang = _targetLanguageDropdown.SelectedValue;
                TranslatorCore.Config.target_language = selectedTargetLang == "auto (System)" ? "auto" : selectedTargetLang;

                // Hotkey
                TranslatorCore.Config.settings_hotkey = _hotkeyCapture.HotkeyString;

                // Translation (Backend + Capture)
                TranslatorCore.Config.capture_keys_only = _captureKeysOnlyToggle.isOn;
                string newBackend = GetSelectedBackendConfig();
                TranslatorCore.Config.translation_backend = newBackend;
                TranslatorCore.Config.enable_ai = (newBackend == "llm"); // Keep enable_ai in sync
                TranslatorCore.Config.ai_url = _aiUrlInput.Text;
                string apiKeyValue = _aiApiKeyInput.Text;
                TranslatorCore.Config.ai_api_key = !string.IsNullOrEmpty(apiKeyValue) ? apiKeyValue : null;
                TranslatorCore.Config.ai_model = _modelDropdown.SelectedValue ?? "";
                TranslatorCore.Config.game_context = _gameContextInput.Text;
                TranslatorCore.Config.strict_source_language = _strictSourceToggle.isOn;
                string googleKey = _googleApiKeyInput?.Text;
                TranslatorCore.Config.google_api_key = !string.IsNullOrEmpty(googleKey) ? googleKey : null;
                string deeplKey = _deeplApiKeyInput?.Text;
                TranslatorCore.Config.deepl_api_key = !string.IsNullOrEmpty(deeplKey) ? deeplKey : null;
                TranslatorCore.Config.deepl_use_free = _deeplUseFreeToggle.isOn;
                float rateLimitDelay;
                if (float.TryParse(_rateLimitDelayInput?.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rateLimitDelay) && rateLimitDelay >= 0.1f)
                    TranslatorCore.Config.rate_limit_retry_delay = rateLimitDelay;
                else
                    TranslatorCore.Config.rate_limit_retry_delay = 3f;

                // Online mode - detect transition for sync stream management
                bool wasOnline = TranslatorCore.Config.online_mode;
                bool nowOnline = _onlineModeToggle.isOn;
                TranslatorCore.Config.online_mode = nowOnline;
                TranslatorCore.Config.sync.check_update_on_start = _checkUpdatesToggle.isOn;
                TranslatorCore.Config.sync.notify_updates = _notifyUpdatesToggle.isOn;
                TranslatorCore.Config.sync.auto_download = _autoDownloadToggle.isOn;
                TranslatorCore.Config.sync.check_mod_updates = _checkModUpdatesToggle.isOn;
                TranslatorCore.Config.sync.notifications_enabled = _notificationsEnabledToggle.isOn;
                TranslatorCore.Config.sync.notification_position = PositionDisplayToConfig(_notificationPositionDropdown.SelectedValue);

                // Apply notification position change immediately
                TranslatorUIManager.StatusOverlay?.ApplyPositionFromConfig();

                // Advanced settings (per-game, stored in translations.json, requires restart)
                bool eventSystemChanged = TranslatorCore.DisableEventSystemOverride != _disableEventSystemOverrideToggle.isOn;
                TranslatorCore.DisableEventSystemOverride = _disableEventSystemOverrideToggle.isOn;

                TranslatorCore.SaveConfig();

                // Save per-game settings (translations.json) if EventSystem override changed
                if (eventSystemChanged)
                {
                    TranslatorCore.SaveCache();
                    TranslatorCore.LogInfo("[Options] EventSystem override setting changed - game restart required for effect");
                }

                TranslatorCore.LogInfo("[Options] Settings saved successfully");

                TranslatorCore.ClearProcessingCaches();

                // Force refresh all text to apply new settings (fonts, translations)
                TranslatorScanner.ForceRefreshAllText();

                if (TranslatorCore.Config.IsTranslationEnabled)
                {
                    TranslatorCore.EnsureWorkerRunning();
                }
                else
                {
                    TranslatorCore.ClearQueue();
                }

                // Handle online mode transition
                if (nowOnline && !wasOnline)
                {
                    // Switched from offline to online - start sync stream and check for updates
                    TranslatorCore.LogInfo("[Options] Online mode enabled, starting sync stream...");
                    TranslatorUIManager.StartSyncStream();
                    if (TranslatorCore.Config.sync.check_mod_updates)
                    {
                        TranslatorUIManager.CheckForModUpdates();
                    }
                }
                else if (!nowOnline && wasOnline)
                {
                    // Switched from online to offline - stop sync stream and clear server state
                    TranslatorCore.LogInfo("[Options] Online mode disabled, stopping sync stream...");
                    TranslatorUIManager.StopSyncStream();

                    // Reset server state - we're offline, server info is no longer relevant
                    TranslatorCore.ServerState = null;

                    // Reset pending update notifications
                    TranslatorUIManager.HasPendingUpdate = false;
                    TranslatorUIManager.NotificationDismissed = false;
                }

                // Always refresh UI after online mode change (or any settings change)
                if (nowOnline != wasOnline)
                {
                    TranslatorUIManager.MainPanel?.RefreshUI();
                    TranslatorUIManager.StatusOverlay?.RefreshOverlay();
                }

                // Update snapshots after apply (no pending changes now)
                _initialSnapshot = ConfigSnapshot.FromConfig();

                UpdateApplyButtonText();
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[Options] Failed to save settings: {e.Message}");
                _aiTestStatusLabel.text = $"Error: {e.Message}";
                _aiTestStatusLabel.color = UIStyles.StatusError;
            }
        }

        /// <summary>
        /// Called when Apply button is clicked. Applies settings if there are changes,
        /// or closes the panel if there are no pending changes.
        /// </summary>
        private void OnApplyClicked()
        {
            int changes = CountPendingChanges();
            if (changes > 0)
            {
                ApplySettings();
            }
            else
            {
                // No changes - just close
                SetActive(false);
            }
        }

        /// <summary>
        /// Sets up change listeners on configurable controls to track pending changes.
        /// Note: Toggle listeners are NOT set here because onValueChanged.AddListener
        /// fails on IL2CPP (UnityAction delegate conversion issue). Instead, toggle
        /// state changes are detected via polling in Update().
        /// </summary>
        private void SetupChangeListeners()
        {
            // Input fields (InputFieldRef.OnValueChanged is a C# event, IL2CPP-safe)
            _aiUrlInput.OnValueChanged += _ => UpdateApplyButtonText();
            _aiApiKeyInput.OnValueChanged += _ => UpdateApplyButtonText();
            _gameContextInput.OnValueChanged += _ => UpdateApplyButtonText();
            _googleApiKeyInput.OnValueChanged += _ => UpdateApplyButtonText();
            _deeplApiKeyInput.OnValueChanged += _ => UpdateApplyButtonText();
            _rateLimitDelayInput.OnValueChanged += _ => UpdateApplyButtonText();

            // Language dropdowns - hook into their change events
            _sourceLanguageDropdown.OnSelectionChanged += _ => UpdateApplyButtonText();
            _targetLanguageDropdown.OnSelectionChanged += _ => UpdateApplyButtonText();

            // Hotkey capture
            _hotkeyCapture.OnHotkeyChanged += _ => UpdateApplyButtonText();
        }

        /// <summary>
        /// Counts how many settings differ from their initial values.
        /// </summary>
        private int CountPendingChanges()
        {
            if (_initialSnapshot == null) return 0;

            int count = 0;

            // General
            if (_enableTranslationsToggle.isOn != _initialSnapshot.enable_translations) count++;
            if (_translateModUIToggle.isOn != _initialSnapshot.translate_mod_ui) count++;

            // Languages
            string currentSource = _sourceLanguageDropdown.SelectedValue;
            string snapshotSource = _initialSnapshot.source_language == "auto" ? "auto (Detect)" : _initialSnapshot.source_language;
            if (currentSource != snapshotSource) count++;

            string currentTarget = _targetLanguageDropdown.SelectedValue;
            string snapshotTarget = _initialSnapshot.target_language == "auto" ? "auto (System)" : _initialSnapshot.target_language;
            if (currentTarget != snapshotTarget) count++;

            // Hotkey
            if (_hotkeyCapture.HotkeyString != _initialSnapshot.settings_hotkey) count++;

            // Translation (Backend + Capture)
            if (_captureKeysOnlyToggle.isOn != _initialSnapshot.capture_keys_only) count++;
            if (GetSelectedBackendConfig() != _initialSnapshot.translation_backend) count++;
            if (_aiUrlInput.Text != _initialSnapshot.ai_url) count++;
            if ((_aiApiKeyInput.Text ?? "") != _initialSnapshot.ai_api_key) count++;
            if ((_modelDropdown.SelectedValue ?? "") != _initialSnapshot.ai_model) count++;
            if (_gameContextInput.Text != _initialSnapshot.game_context) count++;
            if (_strictSourceToggle.isOn != _initialSnapshot.strict_source_language) count++;
            if ((_googleApiKeyInput?.Text ?? "") != _initialSnapshot.google_api_key) count++;
            if ((_deeplApiKeyInput?.Text ?? "") != _initialSnapshot.deepl_api_key) count++;
            if (_deeplUseFreeToggle.isOn != _initialSnapshot.deepl_use_free) count++;
            float parsedDelay;
            float currentDelay = (float.TryParse(_rateLimitDelayInput?.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsedDelay) && parsedDelay >= 0.1f) ? parsedDelay : 3f;
            if (Math.Abs(currentDelay - _initialSnapshot.rate_limit_retry_delay) > 0.01f) count++;

            // Online
            if (_onlineModeToggle.isOn != _initialSnapshot.online_mode) count++;
            if (_checkUpdatesToggle.isOn != _initialSnapshot.check_update_on_start) count++;
            if (_notifyUpdatesToggle.isOn != _initialSnapshot.notify_updates) count++;
            if (_autoDownloadToggle.isOn != _initialSnapshot.auto_download) count++;
            if (_checkModUpdatesToggle.isOn != _initialSnapshot.check_mod_updates) count++;
            if (_notificationsEnabledToggle.isOn != _initialSnapshot.notifications_enabled) count++;
            if (PositionDisplayToConfig(_notificationPositionDropdown.SelectedValue) != _initialSnapshot.notification_position) count++;

            // Advanced (per-game settings)
            if (_disableEventSystemOverrideToggle.isOn != _initialSnapshot.disable_eventsystem_override) count++;

            return count;
        }

        /// <summary>
        /// Updates the Apply button text based on pending changes count.
        /// Shows "Apply (x)" when there are changes, "Close" when there are none.
        /// </summary>
        private void UpdateApplyButtonText()
        {
            if (_applyBtn == null) return;

            int changes = CountPendingChanges();
            if (changes > 0)
            {
                _applyBtn.ButtonText.text = $"Apply ({changes})";
            }
            else
            {
                _applyBtn.ButtonText.text = "Close";
            }
        }
    }
}
