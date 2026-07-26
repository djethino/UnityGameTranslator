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
        public override string Name => "Mod Options";
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
        private SearchableDropdown _interfaceFontDropdown; // mod UI font, shown only when translating the mod UI
        private GameObject _interfaceFontRow;              // container toggled with the checkbox
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
        // Additional hotkeys — all blank by default, for advanced users.
        private HotkeyCapture _hotkeyToggleTranslations;
        private HotkeyCapture _hotkeyToggleAI;
        private HotkeyCapture _hotkeyToggleImages;
        private HotkeyCapture _hotkeyToggleFonts;
        private HotkeyCapture _hotkeyToggleOverlay;
        private HotkeyCapture _hotkeyOpenInspector;
        private HotkeyCapture _hotkeyOpenUpload;
        private HotkeyCapture _hotkeyOpenExclusion;
        private HotkeyCapture _hotkeyOpenTextEditor;
        private HotkeyCapture _hotkeyForceScan;

        // Translation section
        private Toggle _captureKeysOnlyToggle;
        private Toggle _debugLoggingToggle;
        private Toggle _debugAiToggle;
        private Components.HelpZone _helpZone;
        private SearchableDropdown _backendTypeDropdown; // UIStyles.BackendTypeLLM / BackendTypeApi
        private static readonly string[] BackendTypeOptions = { UIStyles.BackendTypeLLM, UIStyles.BackendTypeApi };
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

        // Proxy / Network section (in the Online tab)
        // Mode dropdown is shown to all users. Custom URL/user/pass + bypass toggle
        // are only visible when mode == "Custom" to avoid cluttering the regular case.
        private SearchableDropdown _proxyModeDropdown;
        private GameObject _proxyCustomSection;
        private InputFieldRef _proxyUrlInput;
        private InputFieldRef _proxyUserInput;
        private InputFieldRef _proxyPassInput;
        private Toggle _proxyBypassLocalToggle;
        private static readonly string[] ProxyModeDisplayOptions = { "Default", "System", "None / Direct", "Custom" };

        // Online section
        private Toggle _onlineModeToggle;
        private Toggle _checkUpdatesToggle;
        private Toggle _notifyUpdatesToggle;
        private Toggle _autoDownloadToggle;
        private Toggle _notificationsEnabledToggle;
        private SearchableDropdown _notificationPositionDropdown;
        private Toggle _checkModUpdatesToggle;
        private Toggle _notifyPrereleasesToggle;
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
            public string interface_font;
            public string source_language;
            public string target_language;
            public string settings_hotkey;
            public string toggle_translations_hotkey;
            public string toggle_ai_hotkey;
            public string toggle_images_hotkey;
            public string toggle_fonts_hotkey;
            public string toggle_overlay_hotkey;
            public string open_inspector_hotkey;
            public string open_upload_hotkey;
            public string open_exclusion_mode_hotkey;
            public string open_text_editor_hotkey;
            public string force_scan_hotkey;
            public bool capture_keys_only;
            public bool debug;
            public bool debug_ai;
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
            public bool notify_prereleases;
            public bool disable_eventsystem_override;
            public string proxy_mode;
            public string proxy_url;
            public string proxy_username;
            public string proxy_password;
            public bool proxy_bypass_local;

            public static ConfigSnapshot FromConfig()
            {
                return new ConfigSnapshot
                {
                    enable_translations = TranslatorCore.Config.enable_translations,
                    translate_mod_ui = TranslatorCore.Config.translate_mod_ui,
                    interface_font = TranslatorCore.Config.interface_font,
                    source_language = TranslatorCore.Config.source_language ?? "auto",
                    target_language = TranslatorCore.Config.target_language ?? "auto",
                    settings_hotkey = TranslatorCore.Config.settings_hotkey ?? "F10",
                    toggle_translations_hotkey = TranslatorCore.Config.toggle_translations_hotkey ?? "",
                    toggle_ai_hotkey = TranslatorCore.Config.toggle_ai_hotkey ?? "",
                    toggle_images_hotkey = TranslatorCore.Config.toggle_images_hotkey ?? "",
                    toggle_fonts_hotkey = TranslatorCore.Config.toggle_fonts_hotkey ?? "",
                    toggle_overlay_hotkey = TranslatorCore.Config.toggle_overlay_hotkey ?? "",
                    open_inspector_hotkey = TranslatorCore.Config.open_inspector_hotkey ?? "",
                    open_upload_hotkey = TranslatorCore.Config.open_upload_hotkey ?? "",
                    open_exclusion_mode_hotkey = TranslatorCore.Config.open_exclusion_mode_hotkey ?? "",
                    open_text_editor_hotkey = TranslatorCore.Config.open_text_editor_hotkey ?? "",
                    force_scan_hotkey = TranslatorCore.Config.force_scan_hotkey ?? "",
                    capture_keys_only = TranslatorCore.Config.capture_keys_only,
                    debug = TranslatorCore.Config.debug,
                    debug_ai = TranslatorCore.Config.debug_ai,
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
                    notify_prereleases = TranslatorCore.Config.sync.notify_prereleases,
                    disable_eventsystem_override = TranslatorCore.DisableEventSystemOverride,
                    proxy_mode = TranslatorCore.Config.proxy_mode ?? "default",
                    proxy_url = TranslatorCore.Config.proxy_url ?? "",
                    proxy_username = TranslatorCore.Config.proxy_username ?? "",
                    proxy_password = TranslatorCore.Config.proxy_password ?? "",
                    proxy_bypass_local = TranslatorCore.Config.proxy_bypass_local
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
            _hotkeyToggleTranslations = new HotkeyCapture("");
            _hotkeyToggleAI = new HotkeyCapture("");
            _hotkeyToggleImages = new HotkeyCapture("");
            _hotkeyToggleFonts = new HotkeyCapture("");
            _hotkeyToggleOverlay = new HotkeyCapture("");
            _hotkeyOpenInspector = new HotkeyCapture("");
            _hotkeyOpenUpload = new HotkeyCapture("");
            _hotkeyOpenExclusion = new HotkeyCapture("");
            _hotkeyOpenTextEditor = new HotkeyCapture("");
            _hotkeyForceScan = new HotkeyCapture("");

            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            // Fixed header: tab buttons stay put, only tab content scrolls
            var header = CreateFixedHeader();

            // No big title here — the window title bar already shows "Mod Options" (redundant).

            // Create tab bar — buttons in the fixed header, contents in the scroll area
            _tabBar = new TabBar();
            _tabBar.CreateUI(header, scrollContent);

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

            // Explain what lives behind each tab
            _helpZone?.Describe(_tabBar.GetTabButton("General"),
                "Language, mod UI translation, and general behavior");
            _helpZone?.Describe(_tabBar.GetTabButton("Hotkeys"),
                "Keyboard shortcuts for the mod's panels and tools");
            _helpZone?.Describe(_tabBar.GetTabButton("Translation"),
                "How untranslated texts get translated: your AI, Google or DeepL");
            _helpZone?.Describe(_tabBar.GetTabButton("Online"),
                "Website sync, update notifications, and network settings");

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
            // EXCLUDE from translation: this button's text is code-managed and dynamic
            // ("Apply" / "Close" / "Apply (N)" via UpdateApplyButtonText). Async translation would
            // race with those updates and leave the button stuck / inconsistent with its state.
            RegisterExcluded(_applyBtn.ButtonText);

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
            _helpZone?.Describe(transToggleObj, "Turn the mod's translations on or off. When off, the game shows its original text.");

            UIStyles.CreateSpacer(card, 5);

            // Translate mod UI toggle
            var modUIObj = UIFactory.CreateToggle(card, "TranslateModUIToggle", out _translateModUIToggle, out var modUILabel);
            modUILabel.text = " Translate mod interface";
            modUILabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modUIObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(modUILabel);
            _helpZone?.Describe(modUIObj, "Translate this mod's own buttons and labels into your target language, alongside the game's text.");

            var modUIHint = UIStyles.CreateHint(card, "ModUIHint", "Translate this mod's own buttons and labels");
            RegisterUIText(modUIHint);

            // Interface font — shown directly under the checkbox, only while translating the mod UI.
            // Lets the user pick a font that can render the target script (e.g. CJK) for the mod's own
            // interface. The picker appears immediately when the box is checked; the value applies on Apply.
            _interfaceFontRow = UIStyles.CreateFormRow(card, "InterfaceFontRow", UIStyles.RowHeightNormal, 5);
            var interfaceFontLabel = UIFactory.CreateLabel(_interfaceFontRow, "InterfaceFontLabel", "Interface font:", TextAnchor.MiddleLeft);
            interfaceFontLabel.color = UIStyles.TextSecondary;
            interfaceFontLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(interfaceFontLabel.gameObject, minWidth: 90);
            RegisterUIText(interfaceFontLabel);

            string[] interfaceFontOptions = BuildInterfaceFontOptions();
            string initialInterfaceFont = string.IsNullOrEmpty(TranslatorCore.Config.interface_font)
                ? "(None)" : TranslatorCore.Config.interface_font;
            if (!Array.Exists(interfaceFontOptions, o => o == initialInterfaceFont))
                initialInterfaceFont = "(None)";
            _interfaceFontDropdown = new SearchableDropdown("InterfaceFont", interfaceFontOptions,
                initialInterfaceFont, popupHeight: 250, showSearch: true);
            _interfaceFontDropdown.CategoryProvider = FontManager.GetFontOrigin;
            var interfaceFontObj = _interfaceFontDropdown.CreateUI(_interfaceFontRow,
                (_) => { if (!_isLoadingSettings) UpdateApplyButtonText(); }, width: 260);
            _helpZone?.Describe(interfaceFontObj, "Font for this mod's interface when it is translated. Only fonts usable by the interface are listed; pick one that supports your target language's characters.");

            _interfaceFontRow.SetActive(_translateModUIToggle.isOn);
            UIHelpers.AddToggleListener(_translateModUIToggle, (isOn) =>
            {
                if (_interfaceFontRow != null) _interfaceFontRow.SetActive(isOn);
                if (!_isLoadingSettings) UpdateApplyButtonText();
            });

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
            _helpZone?.Describe(notifEnabledObj, "Show small corner messages for sync, updates, and translation activity. Turn off for clean screenshots.");

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
            _helpZone?.Describe(posDropdownObj, "Which screen corner the notification overlay appears in.");

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
            _helpZone?.Describe(eventSystemObj, "Stop the mod from intercepting UI input. Enable if the game's menus or animations stop working. Needs a game restart.");

            var eventSystemHint = UIStyles.CreateHint(card, "EventSystemHint", "Enable if game's UI animations or menus don't work. Requires game restart.");
            RegisterUIText(eventSystemHint);

            // Debug logging toggles — applied immediately (support: ask a user to tick these
            // to produce logs, no config.json editing needed). Config.debug drives the cached
            // DebugMode (SetRuntimeDebug syncs both); Config.debug_ai is read live.
            var debugObj = UIFactory.CreateToggle(card, "DebugLoggingToggle", out _debugLoggingToggle, out var debugLabel);
            debugLabel.text = " Debug logging";
            debugLabel.color = UIStyles.TextSecondary;
            UIHelpers.AddToggleListener(_debugLoggingToggle, _ => UpdateApplyButtonText());
            UIFactory.SetLayoutElement(debugObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(debugLabel);
            _helpZone?.Describe(debugObj, "Write detailed logs to the mod log file. Turn on when reporting an issue, then share the log. Off by default.");

            var debugAiObj = UIFactory.CreateToggle(card, "DebugAiToggle", out _debugAiToggle, out var debugAiLabel);
            debugAiLabel.text = " Debug AI translation";
            debugAiLabel.color = UIStyles.TextSecondary;
            UIHelpers.AddToggleListener(_debugAiToggle, _ => UpdateApplyButtonText());
            UIFactory.SetLayoutElement(debugAiObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(debugAiLabel);
            _helpZone?.Describe(debugAiObj, "Log every AI request and response (prompts, raw output, placeholder handling). Verbose — use only to diagnose translation quality.");

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

            var sourceLangObj = _sourceLanguageDropdown.CreateUI(_languagesEditableSection, OnSourceLanguageChanged, width: 200);
            _helpZone?.Describe(sourceLangObj, "The language the game's text is written in. Leave on Auto to detect it automatically.");

            UIStyles.CreateSpacer(_languagesEditableSection, 5);

            // Target Language
            var targetLangLabel = UIFactory.CreateLabel(_languagesEditableSection, "TargetLangLabel", "Target Language:", TextAnchor.MiddleLeft);
            targetLangLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(targetLangLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(targetLangLabel);

            var targetLangObj = _targetLanguageDropdown.CreateUI(_languagesEditableSection, width: 200);
            _helpZone?.Describe(targetLangObj, "The language you want the game translated into. Auto uses your system language.");

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
            _helpZone?.Describe(resetBtn.Component.gameObject, "Move all mod windows back to their default positions and sizes.");

            _resetWindowsStatusLabel = UIFactory.CreateLabel(resetRow, "ResetStatus", "", TextAnchor.MiddleLeft);
            _resetWindowsStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_resetWindowsStatusLabel.gameObject, flexibleWidth: 9999);

            // === HELP & FEEDBACK (single compact row) ===
            UIStyles.CreateSpacer(card, 15);

            var helpSectionTitle = UIStyles.CreateSectionTitle(card, "HelpFeedbackLabel", "Help & Feedback");
            RegisterUIText(helpSectionTitle);

            var helpRow = UIStyles.CreateFormRow(card, "HelpFeedbackRow", UIStyles.RowHeightMedium, 5);

            var reportBugBtn = CreateSecondaryButton(helpRow, "ReportBugBtn", "Report a Bug", 110);
            reportBugBtn.OnClick += () => TranslatorCore.OpenUrlSafe("https://github.com/djethino/UnityGameTranslator/issues");
            RegisterUIText(reportBugBtn.ButtonText);
            _helpZone?.Describe(reportBugBtn.Component.gameObject,
                "Something broken? Open a GitHub issue (a free GitHub account is required)");

            var discussionsBtn = CreateSecondaryButton(helpRow, "DiscussionsBtn", "Discussions", 100);
            discussionsBtn.OnClick += () => TranslatorCore.OpenUrlSafe("https://github.com/djethino/UnityGameTranslator/discussions");
            RegisterUIText(discussionsBtn.ButtonText);
            _helpZone?.Describe(discussionsBtn.Component.gameObject,
                "Questions, ideas and feedback — talk with us and other players on GitHub");

            var docsBtn = CreateSecondaryButton(helpRow, "OnlineDocsBtn", "Online Docs", 100);
            docsBtn.OnClick += () => TranslatorCore.OpenUrlSafe($"{ApiClient.WebsiteBaseUrl}/docs");
            RegisterUIText(docsBtn.ButtonText);
            _helpZone?.Describe(docsBtn.Component.gameObject,
                "The full user guide on the website (in your language)");
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
            _helpZone?.Describe(_hotkeyCapture.Root, "The keyboard shortcut that opens and closes this settings panel.");

            UIStyles.CreateSpacer(card, 15);

            // Additional hotkeys (all disabled by default — click X to clear)
            var extraTitle = UIStyles.CreateSectionTitle(card, "ExtraHotkeysLabel", "Additional Hotkeys");
            RegisterUIText(extraTitle);

            var extraHint = UIStyles.CreateHint(card, "ExtraHotkeysHint", "Optional shortcuts. All disabled by default to avoid conflicts with game controls. Click X to clear a hotkey.");
            RegisterUIText(extraHint);

            UIStyles.CreateSpacer(card, 5);

            // --- Toggles (actions that turn things on/off) ---
            CreateHotkeyRow(card, "Toggle translations", "Turn all translations on/off (restores original text)", _hotkeyToggleTranslations,
                "Shortcut to turn all translations on or off, restoring the game's original text.");
            CreateHotkeyRow(card, "Toggle translation backend", "Pause/resume live translation - texts already translated stay translated", _hotkeyToggleAI,
                "Shortcut to pause or resume live translation. Texts already translated stay translated.");
            CreateHotkeyRow(card, "Toggle image replacement", "Debug: show original images instead of replacements", _hotkeyToggleImages,
                "Shortcut to switch between original and replaced images. Mainly for debugging.");
            CreateHotkeyRow(card, "Toggle font replacement", "Debug: show the game's original fonts instead of the mod's replacement fonts", _hotkeyToggleFonts,
                "Shortcut to switch between the game's original fonts and the mod's replacement fonts.");
            CreateHotkeyRow(card, "Toggle notifications", "Show/hide the corner notification overlay (for clean screenshots)", _hotkeyToggleOverlay,
                "Shortcut to show or hide the corner notification overlay, handy for clean screenshots.");

            UIStyles.CreateSpacer(card, 10);

            // --- Quick access (open/close panels) ---
            CreateHotkeyRow(card, "Toggle Inspector", "Open/close the element inspector panel", _hotkeyOpenInspector,
                "Shortcut to open or close the element inspector panel.");
            CreateHotkeyRow(card, "Toggle Upload", "Open/close the translation upload panel", _hotkeyOpenUpload,
                "Shortcut to open or close the translation upload panel.");
            CreateHotkeyRow(card, "Toggle Exclusion mode", "Open/close the inspector in exclusion mode", _hotkeyOpenExclusion,
                "Shortcut to open or close the inspector in exclusion mode.");
            CreateHotkeyRow(card, "Toggle Text editor", "Open/close the in-game text editor (click UI text to edit)", _hotkeyOpenTextEditor,
                "Shortcut to open or close the in-game text editor, where you click UI text to edit it.");

            UIStyles.CreateSpacer(card, 10);

            // --- Utilities ---
            CreateHotkeyRow(card, "Force scene rescan", "Re-scan the current scene (useful after scene glitches)", _hotkeyForceScan,
                "Shortcut to re-scan the current scene, useful after scene glitches.");
        }

        /// <summary>
        /// Creates one row per hotkey: label + hint + HotkeyCapture component.
        /// </summary>
        private void CreateHotkeyRow(GameObject parent, string label, string hint, HotkeyCapture capture, string helpText = null)
        {
            var row = UIFactory.CreateVerticalGroup(parent, $"HotkeyRow_{label}", false, false, true, true, 2);
            UIFactory.SetLayoutElement(row, flexibleWidth: 9999);

            var labelUi = UIFactory.CreateLabel(row, "RowLabel", label, TextAnchor.MiddleLeft);
            labelUi.fontStyle = FontStyle.Bold;
            labelUi.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(labelUi.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(labelUi);

            var hintUi = UIStyles.CreateHint(row, "RowHint", hint);
            RegisterUIText(hintUi);

            capture.CreateUI(row, includeDisplayLabel: false);
            if (!string.IsNullOrEmpty(helpText)) _helpZone?.Describe(capture.Root, helpText);

            UIStyles.CreateSpacer(parent, 6);
        }

        private void CreateTranslationTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "TranslationCard", PanelWidth - 60, stretchVertically: true);

            // Capture keys only section
            var captureSectionTitle = UIStyles.CreateSectionTitle(card, "CaptureLabel", "Manual Mode");
            RegisterUIText(captureSectionTitle);

            var captureObj = UIFactory.CreateToggle(card, "CaptureKeysToggle", out _captureKeysOnlyToggle, out var captureLabel);
            captureLabel.text = " Collect texts without translating them";
            captureLabel.color = UIStyles.TextSecondary;
            UIHelpers.AddToggleListener(_captureKeysOnlyToggle, OnCaptureKeysOnlyChanged);
            UIFactory.SetLayoutElement(captureObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(captureLabel);
            _helpZone?.Describe(captureObj, "Record every text the game shows into your translation file as empty entries, to translate later. No automatic translation happens.");

            var captureHint = UIStyles.CreateHint(card, "CaptureHint", "Every text the game shows is added to your translation file as an empty entry, so you can translate it later (in-game editor or browser)");
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
            _helpZone?.Describe(enableObj, "Automatically translate untranslated texts using the backend below (your AI, Google or DeepL).");

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
                "BackendTypeDropdown", BackendTypeOptions, UIStyles.BackendTypeLLM, popupHeight: 100, showSearch: false);
            var typeObj = _backendTypeDropdown.CreateUI(typeRow, OnBackendTypeChanged);
            UIFactory.SetLayoutElement(typeObj, minWidth: 160, minHeight: UIStyles.InputHeight);
            _helpZone?.Describe(typeObj,
                "AI: your own model (Ollama, LM Studio, ChatGPT...) with full context. Google / DeepL: classic translation services, needs an API key.");

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
            _helpZone?.Describe(_aiUrlInput.Component.gameObject, "Address of your AI server, for example a local Ollama or LM Studio. Default is http://localhost:11434.");

            var testBtn = CreateSecondaryButton(urlRow, "TestBtn", "Test", 60);
            testBtn.OnClick += TestAIConnection;
            RegisterUIText(testBtn.ButtonText);
            _helpZone?.Describe(testBtn.Component.gameObject, "Check that the mod can reach the AI server at the URL above.");

            _aiTestStatusLabel = UIFactory.CreateLabel(_llmSection, "TestStatus", "", TextAnchor.MiddleLeft);
            _aiTestStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_aiTestStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterExcluded(_aiTestStatusLabel);

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
            _helpZone?.Describe(_aiApiKeyInput.Component.gameObject, "API key for your AI server, if it needs one. Leave empty for most local servers.");

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
            _helpZone?.Describe(modelObj, "Which AI model handles the translations. Use Refresh to load the list from your server.");

            var refreshBtn = CreateSecondaryButton(modelRow, "RefreshBtn", "Refresh", 60);
            refreshBtn.OnClick += RefreshModels;
            RegisterUIText(refreshBtn.ButtonText);
            _helpZone?.Describe(refreshBtn.Component.gameObject, "Load the list of available models from your AI server.");

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
            _helpZone?.Describe(_gameContextInput.Component.gameObject, "Optional note about the game (genre, setting, tone) to help the AI pick better wording.");

            var contextHint = UIStyles.CreateHint(_llmSection, "ContextHint", "Helps the AI understand game vocabulary");
            RegisterUIText(contextHint);

            UIStyles.CreateSpacer(_llmSection, 5);

            // Strict source language toggle
            var strictObj = UIFactory.CreateToggle(_llmSection, "StrictSourceToggle", out _strictSourceToggle, out var strictLabel);
            strictLabel.text = " Strict source language detection";
            strictLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(strictObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(strictLabel);
            _helpZone?.Describe(strictObj, "Skip texts that are not in the source language, so foreign or already-translated text is left alone. AI backend only.");

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
            _helpZone?.Describe(providerObj, "Choose the translation service: Google Translate or DeepL. Each needs its own API key.");

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
            _helpZone?.Describe(_googleApiKeyInput.Component.gameObject, "Your Google Cloud API key with the Translation API enabled.");

            var googleTestBtn = CreateSecondaryButton(googleKeyRow, "GoogleTestBtn", "Test", 60);
            googleTestBtn.OnClick += TestGoogleConnection;
            RegisterUIText(googleTestBtn.ButtonText);
            _helpZone?.Describe(googleTestBtn.Component.gameObject, "Send a test request to check that your Google API key works.");

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
            _helpZone?.Describe(_deeplApiKeyInput.Component.gameObject, "Your DeepL API key (Free or Pro).");

            var deeplTestBtn = CreateSecondaryButton(deeplKeyRow, "DeepLTestBtn", "Test", 60);
            deeplTestBtn.OnClick += TestDeepLConnection;
            RegisterUIText(deeplTestBtn.ButtonText);
            _helpZone?.Describe(deeplTestBtn.Component.gameObject, "Send a test request to check that your DeepL API key works.");

            _deeplTestStatusLabel = UIFactory.CreateLabel(_deeplSection, "DeepLTestStatus", "", TextAnchor.MiddleLeft);
            _deeplTestStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_deeplTestStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            var deeplFreeObj = UIFactory.CreateToggle(_deeplSection, "DeepLFreeToggle", out _deeplUseFreeToggle, out var deeplFreeLabel);
            deeplFreeLabel.text = " Use Free API (api-free.deepl.com)";
            deeplFreeLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(deeplFreeObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(deeplFreeLabel);
            _helpZone?.Describe(deeplFreeObj, "Use the DeepL Free endpoint. Turn off if you have a DeepL Pro key.");

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
            _helpZone?.Describe(_rateLimitDelayInput.Component.gameObject, "How long to wait before retrying when the translation service asks the mod to slow down.");

            var rateLimitUnit = UIFactory.CreateLabel(rateLimitRow, "RateLimitUnit", "seconds", TextAnchor.MiddleLeft);
            rateLimitUnit.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(rateLimitUnit.gameObject, flexibleWidth: 9999);
            RegisterUIText(rateLimitUnit);

            var rateLimitHint = UIStyles.CreateHint(_backendTypeSection, "RateLimitHint", "How long to wait before retrying when the translation service asks to slow down");
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
            _helpZone?.Describe(onlineToggleObj,
                "On: the mod contacts our website to find community translations and updates for your games. Off: fully offline, nothing leaves your machine.");

            UIStyles.CreateSpacer(card, 10);

            // Translation sync section
            var syncSectionTitle = UIStyles.CreateSectionTitle(card, "SyncLabel", "Translation Sync");
            RegisterUIText(syncSectionTitle);

            var checkUpdatesObj = UIFactory.CreateToggle(card, "CheckUpdatesToggle", out _checkUpdatesToggle, out var checkLabel);
            checkLabel.text = " Check for translation updates on start";
            checkLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(checkUpdatesObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(checkLabel);
            _helpZone?.Describe(checkUpdatesObj, "Check the website for newer versions of your translations each time the game starts.");

            var notifyObj = UIFactory.CreateToggle(card, "NotifyToggle", out _notifyUpdatesToggle, out var notifyLabel);
            notifyLabel.text = " Notify when translation updates available";
            notifyLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(notifyObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(notifyLabel);
            _helpZone?.Describe(notifyObj, "Show a notification when a newer version of a translation is available to download.");

            var autoDownloadObj = UIFactory.CreateToggle(card, "AutoDownloadToggle", out _autoDownloadToggle, out var autoLabel);
            autoLabel.text = " Auto-download translation updates (no conflicts)";
            autoLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(autoDownloadObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(autoLabel);
            _helpZone?.Describe(autoDownloadObj,
                "Only applies when you have no local changes — otherwise the mod always asks first");

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
            _helpZone?.Describe(modUpdatesObj, "Check for a new version of the mod itself when the game starts.");

            _checkModUpdatesNowBtn = CreateSecondaryButton(modUpdatesRow, "CheckNowBtn", "Check Now", 90);
            _checkModUpdatesNowBtn.OnClick += OnCheckModUpdatesNowClicked;
            RegisterUIText(_checkModUpdatesNowBtn.ButtonText);
            _helpZone?.Describe(_checkModUpdatesNowBtn.Component.gameObject, "Check for a new mod version right now.");

            var prereleaseObj = UIFactory.CreateToggle(card, "PrereleaseToggle", out _notifyPrereleasesToggle, out var prereleaseLabel);
            prereleaseLabel.text = " Also notify about beta releases";
            prereleaseLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(prereleaseObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(prereleaseLabel);
            _helpZone?.Describe(prereleaseObj, "Also get notified about beta (pre-release) mod versions, not just stable ones.");

            var prereleaseHint = UIStyles.CreateHint(card, "PrereleaseHint",
                "Betas are early builds for testing new features. Leave off to only hear about stable releases.");
            RegisterUIText(prereleaseHint);

            _checkModUpdatesStatusLabel = UIFactory.CreateLabel(card, "ModUpdateStatus", "", TextAnchor.MiddleLeft);
            _checkModUpdatesStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_checkModUpdatesStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // === Proxy / Network ===
            // Most users keep "Default". Use "None" to bypass a process-level HTTP
            // proxy injected by the game (DRM / EOS / anti-cheat) when the mod's
            // network calls hang. "System" forces a fresh Windows proxy. "Custom"
            // routes through a user-defined URL with optional credentials.
            UIStyles.CreateSpacer(card, 10);

            var proxySectionTitle = UIStyles.CreateSectionTitle(card, "ProxyLabel", "Network / Proxy");
            RegisterUIText(proxySectionTitle);

            var proxyIntro = UIStyles.CreateHint(card, "ProxyIntro",
                "Use only if the mod's network calls hang (game intercepts HTTP). Keep Default otherwise.");
            RegisterUIText(proxyIntro);

            // Mode dropdown
            var proxyModeRow = UIStyles.CreateFormRow(card, "ProxyModeRow", UIStyles.InputHeight, 5);
            var proxyModeLabel = UIFactory.CreateLabel(proxyModeRow, "ProxyModeLabel", "Mode:", TextAnchor.MiddleLeft);
            proxyModeLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(proxyModeLabel.gameObject, minWidth: 80);
            RegisterUIText(proxyModeLabel);

            _proxyModeDropdown = new SearchableDropdown(
                "ProxyModeDropdown", ProxyModeDisplayOptions, ProxyModeDisplayOptions[0], popupHeight: 150, showSearch: false);
            var proxyModeObj = _proxyModeDropdown.CreateUI(proxyModeRow, OnProxyModeChanged);
            UIFactory.SetLayoutElement(proxyModeObj, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            _helpZone?.Describe(proxyModeObj, "How the mod connects to the internet. Keep Default unless the game blocks the mod's network calls.");

            // Custom-only section (toggled visible by OnProxyModeChanged)
            _proxyCustomSection = UIFactory.CreateVerticalGroup(card, "ProxyCustomSection", false, false, true, true, 3);
            UIFactory.SetLayoutElement(_proxyCustomSection, flexibleWidth: 9999);

            // Custom URL
            var proxyUrlRow = UIStyles.CreateFormRow(_proxyCustomSection, "ProxyUrlRow", UIStyles.InputHeight, 5);
            var proxyUrlLabel = UIFactory.CreateLabel(proxyUrlRow, "ProxyUrlLabel", "URL:", TextAnchor.MiddleLeft);
            proxyUrlLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(proxyUrlLabel.gameObject, minWidth: 80);
            RegisterUIText(proxyUrlLabel);

            _proxyUrlInput = UIFactory.CreateInputField(proxyUrlRow, "ProxyUrl", "http://proxy.example.com:8080");
            UIFactory.SetLayoutElement(_proxyUrlInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_proxyUrlInput.Component.gameObject, UIStyles.InputBackground);
            _helpZone?.Describe(_proxyUrlInput.Component.gameObject, "Address of your proxy server, used only in Custom mode.");

            // Username
            var proxyUserRow = UIStyles.CreateFormRow(_proxyCustomSection, "ProxyUserRow", UIStyles.InputHeight, 5);
            var proxyUserLabel = UIFactory.CreateLabel(proxyUserRow, "ProxyUserLabel", "Username:", TextAnchor.MiddleLeft);
            proxyUserLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(proxyUserLabel.gameObject, minWidth: 80);
            RegisterUIText(proxyUserLabel);

            _proxyUserInput = UIFactory.CreateInputField(proxyUserRow, "ProxyUser", "(optional)");
            UIFactory.SetLayoutElement(_proxyUserInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_proxyUserInput.Component.gameObject, UIStyles.InputBackground);
            _helpZone?.Describe(_proxyUserInput.Component.gameObject, "Proxy username, if your proxy requires sign-in. Optional.");

            // Password
            var proxyPassRow = UIStyles.CreateFormRow(_proxyCustomSection, "ProxyPassRow", UIStyles.InputHeight, 5);
            var proxyPassLabel = UIFactory.CreateLabel(proxyPassRow, "ProxyPassLabel", "Password:", TextAnchor.MiddleLeft);
            proxyPassLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(proxyPassLabel.gameObject, minWidth: 80);
            RegisterUIText(proxyPassLabel);

            _proxyPassInput = UIFactory.CreateInputField(proxyPassRow, "ProxyPass", "(optional)");
            _proxyPassInput.Component.contentType = UnityEngine.UI.InputField.ContentType.Password;
            UIFactory.SetLayoutElement(_proxyPassInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_proxyPassInput.Component.gameObject, UIStyles.InputBackground);
            _helpZone?.Describe(_proxyPassInput.Component.gameObject, "Proxy password, if your proxy requires sign-in. Optional.");

            // Bypass local
            var proxyBypassObj = UIFactory.CreateToggle(_proxyCustomSection, "ProxyBypassToggle",
                out _proxyBypassLocalToggle, out var proxyBypassLabel);
            proxyBypassLabel.text = " Bypass proxy for localhost / private addresses";
            proxyBypassLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(proxyBypassObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(proxyBypassLabel);
            _helpZone?.Describe(proxyBypassObj, "Connect directly to local and private addresses instead of through the proxy.");

            // Hidden by default; OnProxyModeChanged toggles it when the user picks "Custom".
            _proxyCustomSection.SetActive(false);
        }

        private void OnProxyModeChanged(string newDisplay)
        {
            if (_proxyCustomSection != null)
                _proxyCustomSection.SetActive(newDisplay == "Custom");
            if (!_isLoadingSettings) UpdateApplyButtonText();
        }

        private static string ProxyModeDisplayToConfig(string display)
        {
            if (display == "Custom") return "custom";
            if (display == "None / Direct") return "none";
            if (display == "System") return "system";
            return "default";
        }

        private static string ProxyModeConfigToDisplay(string mode)
        {
            switch ((mode ?? "default").Trim().ToLowerInvariant())
            {
                case "custom": return "Custom";
                case "none": return "None / Direct";
                case "system": return "System";
                default: return "Default";
            }
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
            _hotkeyToggleTranslations?.Update();
            _hotkeyToggleAI?.Update();
            _hotkeyToggleImages?.Update();
            _hotkeyToggleFonts?.Update();
            _hotkeyToggleOverlay?.Update();
            _hotkeyOpenInspector?.Update();
            _hotkeyOpenUpload?.Update();
            _hotkeyOpenExclusion?.Update();
            _hotkeyOpenTextEditor?.Update();
            _hotkeyForceScan?.Update();

            // Poll toggle/dropdown state changes to update Apply button text.
            // We cannot use onValueChanged.AddListener on toggles because it fails on IL2CPP
            // (UnityAction delegate conversion issue). Polling is cheap (just bool comparisons)
            // and only runs while the panel is visible.
            if (Enabled)
            {
                UpdateApplyButtonText();
            }
        }

        /// <summary>
        /// Reload the UI from the current config.
        /// Called when the config is modified externally (e.g. via hotkey toggles) so
        /// the Options panel stays in sync without forcing the user to reopen it.
        /// Safe to call even when the panel UI isn't built yet — it's a no-op in that case.
        /// </summary>
        public void RefreshFromConfig()
        {
            // Guard: UI might not be constructed yet (e.g. early mod init)
            if (_enableTranslationsToggle == null) return;

            LoadCurrentSettings();
            _initialSnapshot = ConfigSnapshot.FromConfig();
            UpdateApplyButtonText();
        }

        private void LoadCurrentSettings()
        {
            _isLoadingSettings = true;

            // General
            _enableTranslationsToggle.isOn = TranslatorCore.Config.enable_translations;
            _translateModUIToggle.isOn = TranslatorCore.Config.translate_mod_ui;

            // Interface font: sync the picker visibility with the checkbox on (re)load.
            if (_interfaceFontRow != null)
                _interfaceFontRow.SetActive(TranslatorCore.Config.translate_mod_ui);

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
            _hotkeyToggleTranslations.SetHotkey(TranslatorCore.Config.toggle_translations_hotkey ?? "");
            _hotkeyToggleAI.SetHotkey(TranslatorCore.Config.toggle_ai_hotkey ?? "");
            _hotkeyToggleImages.SetHotkey(TranslatorCore.Config.toggle_images_hotkey ?? "");
            _hotkeyToggleFonts.SetHotkey(TranslatorCore.Config.toggle_fonts_hotkey ?? "");
            _hotkeyToggleOverlay.SetHotkey(TranslatorCore.Config.toggle_overlay_hotkey ?? "");
            _hotkeyOpenInspector.SetHotkey(TranslatorCore.Config.open_inspector_hotkey ?? "");
            _hotkeyOpenUpload.SetHotkey(TranslatorCore.Config.open_upload_hotkey ?? "");
            _hotkeyOpenExclusion.SetHotkey(TranslatorCore.Config.open_exclusion_mode_hotkey ?? "");
            _hotkeyOpenTextEditor.SetHotkey(TranslatorCore.Config.open_text_editor_hotkey ?? "");
            _hotkeyForceScan.SetHotkey(TranslatorCore.Config.force_scan_hotkey ?? "");

            // Online mode (must be loaded BEFORE translation backend — UpdateBackendSections checks online state)
            _onlineModeToggle.isOn = TranslatorCore.Config.online_mode;
            _checkUpdatesToggle.isOn = TranslatorCore.Config.sync.check_update_on_start;
            _notifyUpdatesToggle.isOn = TranslatorCore.Config.sync.notify_updates;
            _autoDownloadToggle.isOn = TranslatorCore.Config.sync.auto_download;
            _checkModUpdatesToggle.isOn = TranslatorCore.Config.sync.check_mod_updates;
            _notifyPrereleasesToggle.isOn = TranslatorCore.Config.sync.notify_prereleases;
            _notificationsEnabledToggle.isOn = TranslatorCore.Config.sync.notifications_enabled;
            _notificationPositionDropdown.SelectedValue = PositionConfigToDisplay(TranslatorCore.Config.sync.notification_position);
            OnOnlineModeChanged(_onlineModeToggle.isOn);

            // Proxy / Network (independent of online mode -- affects every HTTP call)
            _proxyModeDropdown.SelectedValue = ProxyModeConfigToDisplay(TranslatorCore.Config.proxy_mode);
            _proxyUrlInput.Text = TranslatorCore.Config.proxy_url ?? "";
            _proxyUserInput.Text = TranslatorCore.Config.proxy_username ?? "";
            _proxyPassInput.Text = TranslatorCore.Config.proxy_password ?? "";
            _proxyBypassLocalToggle.isOn = TranslatorCore.Config.proxy_bypass_local;
            if (_proxyCustomSection != null)
                _proxyCustomSection.SetActive(_proxyModeDropdown.SelectedValue == "Custom");

            // Translation (Backend + Capture) — after online mode so UpdateBackendSections sees correct online state
            _captureKeysOnlyToggle.isOn = TranslatorCore.Config.capture_keys_only;
            if (_debugLoggingToggle != null) _debugLoggingToggle.isOn = TranslatorCore.Config.debug;
            if (_debugAiToggle != null) _debugAiToggle.isOn = TranslatorCore.Config.debug_ai;
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
            _backendTypeDropdown.SelectedValue = (backend == "google" || backend == "deepl") ? UIStyles.BackendTypeApi : UIStyles.BackendTypeLLM;
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
            _notifyPrereleasesToggle.interactable = enabled;
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

                // And move the LIVE windows back to their defaults right now —
                // clearing the config alone only took effect on the next launch
                TranslatorPanelBase.ResetAllLiveWindows();

                _resetWindowsStatusLabel.text = "Positions reset!";
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

            string type = _backendTypeDropdown?.SelectedValue ?? UIStyles.BackendTypeLLM;
            if (type == UIStyles.BackendTypeLLM) return "llm";

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
            if (!canUseTransApi && _backendTypeDropdown?.SelectedValue == UIStyles.BackendTypeApi)
            {
                _backendTypeDropdown.SelectedValue = UIStyles.BackendTypeLLM;
            }

            string type = _backendTypeDropdown?.SelectedValue ?? UIStyles.BackendTypeLLM;
            bool isLLM = type == UIStyles.BackendTypeLLM;

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

                var result = await GitHubUpdateChecker.CheckForUpdatesAsync(currentVersion, modLoaderType,
                    _notifyPrereleasesToggle != null && _notifyPrereleasesToggle.isOn);

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
                TranslatorCore.LogWarning($"[Options] TestAIConnection threw: {e.GetType().Name}: {errorMsg}");
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

        /// <summary>
        /// Build the interface-font picker options: SYSTEM fonts only. The mod UI font is applied by
        /// rebacking its fontNames to a font family the OS can resolve (FontManager.RebackFontToSystem)
        /// — the IL2CPP-safe way, since a fresh OS-backed Font can't be created there. Game-bundled and
        /// custom (SDF/TMP) fonts aren't OS-installed families, so they can't be rebacked and are omitted.
        /// A system font that covers the target script (e.g. Malgun Gothic for Korean) is what to pick.
        /// </summary>
        private static string[] BuildInterfaceFontOptions()
        {
            var options = new List<string> { "(None)" };

            var sys = FontManager.SystemFonts;
            if (sys != null && sys.Length > 0)
            {
                options.Add("--- System Fonts ---");
                options.AddRange(sys);
            }

            return options.ToArray();
        }

        /// <summary>Normalize an interface-font picker selection to a stored value (null = default UI font).</summary>
        private static string NormalizeInterfaceFont(string selected)
        {
            if (string.IsNullOrEmpty(selected) || selected == "(None)") return null;
            if (selected.StartsWith("--- ")) return null; // separator row, not a real choice
            return selected;
        }

        private void ApplySettings()
        {
            TranslatorCore.LogInfo("[Options] Applying settings...");
            try
            {
                // General
                TranslatorCore.Config.enable_translations = _enableTranslationsToggle.isOn;
                TranslatorCore.Config.translate_mod_ui = _translateModUIToggle.isOn;
                TranslatorCore.Config.interface_font = _interfaceFontDropdown != null
                    ? NormalizeInterfaceFont(_interfaceFontDropdown.SelectedValue)
                    : TranslatorCore.Config.interface_font;

                // Languages
                string selectedSourceLang = _sourceLanguageDropdown.SelectedValue;
                TranslatorCore.Config.source_language = selectedSourceLang == "auto (Detect)" ? "auto" : selectedSourceLang;

                string selectedTargetLang = _targetLanguageDropdown.SelectedValue;
                TranslatorCore.Config.target_language = selectedTargetLang == "auto (System)" ? "auto" : selectedTargetLang;

                // Hotkey
                TranslatorCore.Config.settings_hotkey = _hotkeyCapture.HotkeyString;
                TranslatorCore.Config.toggle_translations_hotkey = _hotkeyToggleTranslations.HotkeyString;
                TranslatorCore.Config.toggle_ai_hotkey = _hotkeyToggleAI.HotkeyString;
                TranslatorCore.Config.toggle_images_hotkey = _hotkeyToggleImages.HotkeyString;
                TranslatorCore.Config.toggle_fonts_hotkey = _hotkeyToggleFonts.HotkeyString;
                TranslatorCore.Config.toggle_overlay_hotkey = _hotkeyToggleOverlay.HotkeyString;
                TranslatorCore.Config.open_inspector_hotkey = _hotkeyOpenInspector.HotkeyString;
                TranslatorCore.Config.open_upload_hotkey = _hotkeyOpenUpload.HotkeyString;
                TranslatorCore.Config.open_exclusion_mode_hotkey = _hotkeyOpenExclusion.HotkeyString;
                TranslatorCore.Config.open_text_editor_hotkey = _hotkeyOpenTextEditor.HotkeyString;
                TranslatorCore.Config.force_scan_hotkey = _hotkeyForceScan.HotkeyString;

                // Translation (Backend + Capture)
                TranslatorCore.Config.capture_keys_only = _captureKeysOnlyToggle.isOn;
                if (_debugLoggingToggle != null) TranslatorCore.SetRuntimeDebug(_debugLoggingToggle.isOn);
                if (_debugAiToggle != null) TranslatorCore.Config.debug_ai = _debugAiToggle.isOn;
                string newBackend = GetSelectedBackendConfig();
                TranslatorCore.Config.translation_backend = newBackend;
                TranslatorCore.Config.enable_ai = (newBackend == "llm"); // Keep enable_ai in sync
                // Capture mode works WITHOUT a backend: the worker must run to
                // store the H+empty entries (it never calls any backend then)
                TranslatorCore.EnsureWorkerRunning();
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
                TranslatorCore.Config.sync.notify_prereleases = _notifyPrereleasesToggle.isOn;
                TranslatorCore.Config.sync.notifications_enabled = _notificationsEnabledToggle.isOn;
                TranslatorCore.Config.sync.notification_position = PositionDisplayToConfig(_notificationPositionDropdown.SelectedValue);

                // Apply notification position change immediately
                TranslatorUIManager.StatusOverlay?.ApplyPositionFromConfig();

                // Advanced settings (per-game, stored in translations.json, requires restart)
                bool eventSystemChanged = TranslatorCore.DisableEventSystemOverride != _disableEventSystemOverrideToggle.isOn;
                TranslatorCore.DisableEventSystemOverride = _disableEventSystemOverrideToggle.isOn;

                // Proxy / Network -- capture old values BEFORE overwriting to detect a change,
                // then rebuild the shared HttpClient AFTER SaveConfig so the next request
                // immediately uses the new proxy.
                string oldProxyMode = (TranslatorCore.Config.proxy_mode ?? "default");
                string oldProxyUrl = TranslatorCore.Config.proxy_url ?? "";
                string oldProxyUser = TranslatorCore.Config.proxy_username ?? "";
                string oldProxyPass = TranslatorCore.Config.proxy_password ?? "";
                bool oldProxyBypass = TranslatorCore.Config.proxy_bypass_local;

                string newProxyMode = ProxyModeDisplayToConfig(_proxyModeDropdown.SelectedValue);
                string newProxyUrl = (_proxyUrlInput.Text ?? "").Trim();
                string newProxyUser = _proxyUserInput.Text ?? "";
                string newProxyPass = _proxyPassInput.Text ?? "";
                bool newProxyBypass = _proxyBypassLocalToggle.isOn;

                TranslatorCore.Config.proxy_mode = newProxyMode;
                TranslatorCore.Config.proxy_url = string.IsNullOrEmpty(newProxyUrl) ? null : newProxyUrl;
                TranslatorCore.Config.proxy_username = string.IsNullOrEmpty(newProxyUser) ? null : newProxyUser;
                TranslatorCore.Config.proxy_password = string.IsNullOrEmpty(newProxyPass) ? null : newProxyPass;
                TranslatorCore.Config.proxy_bypass_local = newProxyBypass;

                bool proxyChanged =
                    oldProxyMode != newProxyMode
                    || oldProxyUrl != newProxyUrl
                    || oldProxyUser != newProxyUser
                    || oldProxyPass != newProxyPass
                    || oldProxyBypass != newProxyBypass;

                TranslatorCore.SaveConfig();

                if (proxyChanged)
                {
                    TranslatorCore.LogInfo("[Options] Proxy configuration changed, rebuilding HttpClient...");
                    TranslatorCore.RebuildHttpClient();
                }

                // Save per-game settings (translations.json) if EventSystem override changed
                if (eventSystemChanged)
                {
                    TranslatorCore.SaveCache();
                    TranslatorCore.LogInfo("[Options] EventSystem override setting changed - game restart required for effect");
                }

                TranslatorCore.LogInfo("[Options] Settings saved successfully");

                // Interface font: (re)apply the mod UI font from the committed config.
                TranslatorUIManager.ApplyInterfaceFont();
                // Mod UI translation: enable → submit our text; disable → restore English.
                if (TranslatorCore.Config.translate_mod_ui)
                    TranslatorUIManager.RefreshOwnUITranslation();
                else
                    TranslatorUIManager.RestoreOwnUIEnglish();

                TranslatorCore.ClearProcessingCaches();

                // Force refresh all text to apply new settings (fonts, translations).
                // reapplyAllScales: discrete Apply — re-derive every component's size from its gated
                // scale so a toggled setting doesn't leave un-retriggered components mis-sized (issue #21).
                TranslatorScanner.ForceRefreshAllText(reapplyAllScales: true);

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
                TranslatorCore.LogError($"[Options] Failed to save settings: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
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
            _hotkeyToggleTranslations.OnHotkeyChanged += _ => UpdateApplyButtonText();
            _hotkeyToggleAI.OnHotkeyChanged += _ => UpdateApplyButtonText();
            _hotkeyToggleImages.OnHotkeyChanged += _ => UpdateApplyButtonText();
            _hotkeyToggleFonts.OnHotkeyChanged += _ => UpdateApplyButtonText();
            _hotkeyToggleOverlay.OnHotkeyChanged += _ => UpdateApplyButtonText();
            _hotkeyOpenInspector.OnHotkeyChanged += _ => UpdateApplyButtonText();
            _hotkeyOpenUpload.OnHotkeyChanged += _ => UpdateApplyButtonText();
            _hotkeyOpenExclusion.OnHotkeyChanged += _ => UpdateApplyButtonText();
            _hotkeyOpenTextEditor.OnHotkeyChanged += _ => UpdateApplyButtonText();
            _hotkeyForceScan.OnHotkeyChanged += _ => UpdateApplyButtonText();
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
            if (NormalizeInterfaceFont(_interfaceFontDropdown?.SelectedValue) != _initialSnapshot.interface_font) count++;

            // Languages
            string currentSource = _sourceLanguageDropdown.SelectedValue;
            string snapshotSource = _initialSnapshot.source_language == "auto" ? "auto (Detect)" : _initialSnapshot.source_language;
            if (currentSource != snapshotSource) count++;

            string currentTarget = _targetLanguageDropdown.SelectedValue;
            string snapshotTarget = _initialSnapshot.target_language == "auto" ? "auto (System)" : _initialSnapshot.target_language;
            if (currentTarget != snapshotTarget) count++;

            // Hotkey
            if (_hotkeyCapture.HotkeyString != _initialSnapshot.settings_hotkey) count++;
            if (_hotkeyToggleTranslations.HotkeyString != _initialSnapshot.toggle_translations_hotkey) count++;
            if (_hotkeyToggleAI.HotkeyString != _initialSnapshot.toggle_ai_hotkey) count++;
            if (_hotkeyToggleImages.HotkeyString != _initialSnapshot.toggle_images_hotkey) count++;
            if (_hotkeyToggleFonts.HotkeyString != _initialSnapshot.toggle_fonts_hotkey) count++;
            if (_hotkeyToggleOverlay.HotkeyString != _initialSnapshot.toggle_overlay_hotkey) count++;
            if (_hotkeyOpenInspector.HotkeyString != _initialSnapshot.open_inspector_hotkey) count++;
            if (_hotkeyOpenUpload.HotkeyString != _initialSnapshot.open_upload_hotkey) count++;
            if (_hotkeyOpenExclusion.HotkeyString != _initialSnapshot.open_exclusion_mode_hotkey) count++;
            if (_hotkeyOpenTextEditor.HotkeyString != _initialSnapshot.open_text_editor_hotkey) count++;
            if (_hotkeyForceScan.HotkeyString != _initialSnapshot.force_scan_hotkey) count++;

            // Translation (Backend + Capture)
            if (_captureKeysOnlyToggle.isOn != _initialSnapshot.capture_keys_only) count++;
            if (_debugLoggingToggle != null && _debugLoggingToggle.isOn != _initialSnapshot.debug) count++;
            if (_debugAiToggle != null && _debugAiToggle.isOn != _initialSnapshot.debug_ai) count++;
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
            if (_notifyPrereleasesToggle.isOn != _initialSnapshot.notify_prereleases) count++;
            if (_notificationsEnabledToggle.isOn != _initialSnapshot.notifications_enabled) count++;
            if (PositionDisplayToConfig(_notificationPositionDropdown.SelectedValue) != _initialSnapshot.notification_position) count++;

            // Advanced (per-game settings)
            if (_disableEventSystemOverrideToggle.isOn != _initialSnapshot.disable_eventsystem_override) count++;

            // Proxy / Network
            if (ProxyModeDisplayToConfig(_proxyModeDropdown.SelectedValue) != _initialSnapshot.proxy_mode) count++;
            if ((_proxyUrlInput.Text ?? "").Trim() != _initialSnapshot.proxy_url) count++;
            if ((_proxyUserInput.Text ?? "") != _initialSnapshot.proxy_username) count++;
            if ((_proxyPassInput.Text ?? "") != _initialSnapshot.proxy_password) count++;
            if (_proxyBypassLocalToggle.isOn != _initialSnapshot.proxy_bypass_local) count++;

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
            string label = changes > 0 ? $"Apply ({changes})" : "Close";
            // Translate at set-time (cache-aware, placeholder-aware) so this code-managed button shows
            // the right state in the current language without racing the async pipeline. English when off.
            _applyBtn.ButtonText.text = TranslatorCore.TranslateOwnUIDynamic(label, _applyBtn.ButtonText);
        }
    }
}
