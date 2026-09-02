using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core.UI.Components;
using UnityGameTranslator.Common;

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
        private Text _lockedHeader;
        private Text _lockedSourceLangValue;
        private Text _lockedTargetLangValue;

        // Interface section
        private Text _resetWindowsStatusLabel;
        private Toggle _disableEventSystemOverrideToggle;
        private Toggle _captureKeyboardToggle;
        private Toggle _captureKeyboardFocusOnlyToggle;
        private Toggle _captureGameMenusToggle;
        private Toggle _captureGameClicksToggle;
        private Toggle _captureMouseAxesToggle;
        private Toggle _pauseGameToggle;
        private UnityEngine.UI.Slider _opacityFocusedSlider;
        private UnityEngine.UI.Slider _opacityUnfocusedSlider;
        private Text _opacityFocusedValue;
        private Text _opacityUnfocusedValue;

        // Tab sizing

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

        /// <summary>
        /// What has to be said about the address in the URL field — empty for a server on this
        /// machine, which is the case this mod is built around.
        /// </summary>
        private Text _aiLocalityLabel;

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

        // Advanced (AI): how many requests one line may cost, and how the model is asked.
        // Three jobs, three settings each, because they want opposite things — see the config.
        private Text _advancedIconLabel;
        private GameObject _advancedContent;
        private bool _advancedExpanded;
        private InputFieldRef _aiMaxAttemptsInput;
        private InputFieldRef _aiTemperatureInput;
        private InputFieldRef _aiTemperatureRepairInput;
        private InputFieldRef _aiTemperatureRetranslateInput;
        private InputFieldRef _aiSeedInput;
        private InputFieldRef _aiSeedRepairInput;
        private InputFieldRef _aiSeedRetranslateInput;

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
        private SearchableDropdown _checkFrequencyDropdown;

        /// <summary>
        /// Frequency labels, in the same order as UpdateCheckFrequency.All so the two conversions
        /// below stay a simple index lookup.
        ///
        /// ⚠ "Automatic" and "Real-time" are gone from here (2026-08-20) — not removed as features
        /// but moved: they described whether to keep a connection open, which is now its own
        /// checkbox. This list is the RHYTHM, and a rhythm has no "stay connected" in it.
        /// </summary>
        private static readonly string[] UpdateFrequencyDisplayOptions =
        {
            // Reads as a sentence after "Ask the website every:" — except the first two,
            // which are states rather than rhythms
            "Never", "Startup only", "hour", "3 hours", "6 hours"
        };

        private static string FrequencyConfigToDisplay(string value)
        {
            int index = System.Array.IndexOf(UpdateCheckFrequency.All, UpdateCheckFrequency.Normalize(value));
            return UpdateFrequencyDisplayOptions[index];
        }

        private static string FrequencyDisplayToConfig(string display)
        {
            int index = System.Array.IndexOf(UpdateFrequencyDisplayOptions, display);
            return index >= 0 ? UpdateCheckFrequency.All[index] : UpdateCheckFrequency.Hourly;
        }
        private Toggle _realtimeOwnToggle;
        private Toggle _notifyUpdatesToggle;
        private Toggle _autoDownloadToggle;
        private Toggle _notificationsEnabledToggle;
        private SearchableDropdown _notificationPositionDropdown;
        private Toggle _checkModUpdatesToggle;
        private Toggle _notifyPrereleasesToggle;
        private ButtonRef _checkModUpdatesNowBtn;
        private Text _checkModUpdatesStatusLabel;

        // Shown only while the file settings differ from the online version
        private GameObject _settingsDriftRow;
        private Text _settingsDriftLabel;
        private ButtonRef _restoreSettingsBtn;

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
            public bool enable_ai;
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
            public int ai_max_attempts;
            public double ai_temperature;
            public double ai_temperature_repair;
            public double ai_temperature_retranslate;
            public string ai_seed;
            public string ai_seed_repair;
            public string ai_seed_retranslate;
            public bool online_mode;
            public string update_check_frequency;
        public bool realtime_own_translation;
            public bool notify_updates;
            public bool notifications_enabled;
            public string notification_position;
            public bool auto_download;
            public bool check_mod_updates;
            public bool notify_prereleases;
            public bool disable_eventsystem_override;
            public bool capture_keyboard;
            public bool capture_keyboard_focus_only;
            public bool capture_game_menus;
            public bool capture_game_clicks;
            public bool capture_mouse_axes;
            public bool pause_game;
            public float panel_opacity_focused;
            public float panel_opacity_unfocused;
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
                    // Compare against what is in effect, so the Apply counter reacts to the toggle
                    // the same way whether or not the user had already made an explicit choice.
                    translate_mod_ui = TranslatorCore.ShouldTranslateOwnUI,
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
                    enable_ai = TranslatorCore.Config.enable_ai,
                    translation_backend = TranslatorCore.Config.translation_backend ?? "none",
                    ai_url = TranslatorCore.Config.ai_url ?? Endpoints.OllamaDefault,
                    ai_api_key = TranslatorCore.Config.ai_api_key ?? "",
                    ai_model = TranslatorCore.Config.ai_model ?? "",
                    game_context = TranslatorCore.Config.game_context ?? "",
                    strict_source_language = TranslatorCore.Config.strict_source_language,
                    google_api_key = TranslatorCore.Config.google_api_key ?? "",
                    deepl_api_key = TranslatorCore.Config.deepl_api_key ?? "",
                    deepl_use_free = TranslatorCore.Config.deepl_use_free,
                    rate_limit_retry_delay = TranslatorCore.Config.rate_limit_retry_delay,
                    ai_max_attempts = TranslatorCore.Config.ai_max_attempts,
                    ai_temperature = TranslatorCore.Config.ai_temperature,
                    ai_temperature_repair = TranslatorCore.Config.ai_temperature_repair,
                    ai_temperature_retranslate = TranslatorCore.Config.ai_temperature_retranslate,
                    // Compared as the TEXT of an optional number: "unset" and "0" are different
                    // answers here, and a nullable compared through a float would merge them.
                    ai_seed = SeedToText(TranslatorCore.Config.ai_seed),
                    ai_seed_repair = SeedToText(TranslatorCore.Config.ai_seed_repair),
                    ai_seed_retranslate = SeedToText(TranslatorCore.Config.ai_seed_retranslate),
                    online_mode = TranslatorCore.Config.online_mode,
                    update_check_frequency = UpdateCheckFrequency.Normalize(TranslatorCore.Config.sync.update_check_frequency),
            realtime_own_translation = TranslatorCore.Config.sync.realtime_own_translation,
                    notify_updates = TranslatorCore.Config.sync.notify_updates,
                    notifications_enabled = TranslatorCore.Config.sync.notifications_enabled,
                    notification_position = TranslatorCore.Config.sync.notification_position ?? "top-right",
                    auto_download = TranslatorCore.Config.sync.auto_download,
                    check_mod_updates = TranslatorCore.Config.sync.check_mod_updates,
                    notify_prereleases = TranslatorCore.Config.sync.notify_prereleases,
                    disable_eventsystem_override = TranslatorCore.DisableEventSystemOverride,
                    capture_keyboard = TranslatorCore.CaptureKeyboard,
                    capture_keyboard_focus_only = TranslatorCore.CaptureKeyboardFocusOnly,
                    capture_game_menus = TranslatorCore.CaptureGameMenus,
                    capture_game_clicks = TranslatorCore.CaptureGameClicks,
                    capture_mouse_axes = TranslatorCore.CaptureMouseAxes,
                    pause_game = TranslatorCore.PauseGame,
                    panel_opacity_focused = TranslatorCore.PanelOpacityFocused,
                    panel_opacity_unfocused = TranslatorCore.PanelOpacityUnfocused,
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

            // The flag beside each name, the same one the status card and the selector draw.
            // ⚠ The "auto …" rows stand for no language and get none — LanguageMark returns
            // nothing for a name the catalogue does not know, so they simply stay plain text.
            _sourceLanguageDropdown.MarkProvider = LanguageOfRow;
            _targetLanguageDropdown.MarkProvider = LanguageOfRow;
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
            var adaptationTab = _tabBar.AddTab("Adaptation");
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
            _helpZone?.Describe(_tabBar.GetTabButton("Adaptation"),
                "How the mod behaves alongside the game — set it for this game, and for whether you are playing or translating");
            _helpZone?.Describe(_tabBar.GetTabButton("Translation"),
                "How untranslated texts get translated: your AI, Google or DeepL");
            _helpZone?.Describe(_tabBar.GetTabButton("Online"),
                "Website sync, update notifications, and network settings");

            // Build each tab's content
            CreateGeneralTabContent(generalTab);
            CreateHotkeysTabContent(hotkeysTab);
            CreateAdaptationTabContent(adaptationTab);
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
            RegisterPendingFields();
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
            // Show the font IN EFFECT — the local override if set, else the one the translation
            // asks for — so the picker reflects what the user actually sees.
            string initialInterfaceFont = string.IsNullOrEmpty(TranslatorCore.EffectiveInterfaceFont)
                ? "(None)" : TranslatorCore.EffectiveInterfaceFont;
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

            // ⚠ Filled in UpdateLanguagesLocked: there are two reasons the languages are settled,
            // and this said only one of them. "Translation uploaded" on a file nobody has published
            // is simply false, and the reader is then left with a locked control and a wrong
            // explanation — worse than a locked control with none.
            _lockedHeader = UIFactory.CreateLabel(_languagesLockedSection, "LockedHeader", "", TextAnchor.MiddleLeft);
            var lockedHeader = _lockedHeader;
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

            // ── Window opacity ───────────────────────────────────────────────────────────────
            // Here, and not with the input options where it started: what it governs is the mod's
            // own windows — same subject as the reset below — and this is the tab it gets looked
            // for in. Its origin was that the title bar signals focus and this makes that signal
            // felt rather than read, but that is where it came FROM, not what it is ABOUT.
            var opacityTitle = UIStyles.CreateSectionTitle(card, "OpacityLabel", "Window opacity");
            RegisterUIText(opacityTitle);

            CreateOpacitySlider(card, "OpacityFocused", "Focused:",
                "How solid the window you are working in is. Lower it to see the game through the one you are using.",
                TranslatorCore.PanelOpacityFocused, out _opacityFocusedSlider, out _opacityFocusedValue);

            CreateOpacitySlider(card, "OpacityUnfocused", "Others:",
                "How solid the other windows are. Slightly faded by default, so a second window can stay open without hiding the game.",
                TranslatorCore.PanelOpacityUnfocused, out _opacityUnfocusedSlider, out _opacityUnfocusedValue);

            UIStyles.CreateSpacer(card, 10);

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

        /// <summary>
        /// What the mod takes from the game while one of its windows is open.
        /// </summary>
        /// <remarks>
        /// Each box is an INTENTION. Whether it can be honoured is a property of the game, not of
        /// the wish, and only the runtime knows: one game is reached by patching its input calls,
        /// another by taking the Input System's devices, a third by neither. So the screen asks
        /// UniverseLib's InputCapture, per intention, and greys out what nobody can serve — with
        /// the reason it gives, never a sentence written here. A hardcoded list of what works
        /// would be wrong on some game and nobody would ever find out.
        /// </remarks>
        /// <summary>
        /// How the mod conducts itself next to the game.
        /// </summary>
        /// <remarks>
        /// Was called "Input", which had stopped describing it: freezing the game touches its
        /// clock, not its input, and taking its EventSystem decides whether its interface answers
        /// at all. It also sat next to "Hotkeys", so anyone after a keyboard setting had two
        /// plausible tabs and no way to choose.
        ///
        /// ⚠ "Compatibility" was the near miss, and worth recording as one: it reads as REPAIRING
        /// a game that misbehaves, while these are as often a deliberate choice — checking
        /// something now and then while playing does not call for the same behaviour as a long
        /// translating session, on a game where nothing is broken either way. Adaptation covers
        /// both; compatibility only covers the half where something is wrong.
        /// </remarks>
        private void CreateAdaptationTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "InputCard", PanelWidth - 60, stretchVertically: true);

            var sectionTitle = UIStyles.CreateSectionTitle(card, "CaptureLabel", "While a mod window is open");
            RegisterUIText(sectionTitle);

            var intro = UIStyles.CreateHint(card, "CaptureIntro",
                "Stop the game from reacting behind the window. Turn one off if it interferes with this game.");
            RegisterUIText(intro);

            UIStyles.CreateSpacer(card, 5);

            CreateCaptureToggle(card, "CaptureKeyboard", " Take the keyboard",
                "Keys go to this window only. Without it, typing a translation also walks, shoots or opens the game's menus. "
                + "Turn it off if the game stops answering the keyboard the way it should.",
                UniverseLib.Input.InputCapture.CaptureKind.Keyboard, out _captureKeyboardToggle);

            // Sub-option, indented under the keyboard one — and the reason its parent can be on by
            // default: the game keeps its keys until somebody actually types or navigates here.
            var focusRow = UIStyles.CreateFormRow(card, "KeyboardFocusRow", UIStyles.RowHeightNormal, 5);
            UIStyles.CreateSpacer(focusRow, 20);   // indent, so it reads as belonging to the box above
            var focusObj = UIFactory.CreateToggle(focusRow, "CaptureKeyboardFocusOnly",
                out _captureKeyboardFocusOnlyToggle, out var focusLabel);
            focusLabel.text = " Only while the mod's interface has focus";
            focusLabel.color = UIStyles.TextSecondary;
            RegisterUIText(focusLabel);
            UIHelpers.AddToggleListener(_captureKeyboardFocusOnlyToggle, _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); });
            _helpZone?.Describe(focusObj,
                "The game keeps its keyboard until you type in a field or move through this interface with the keyboard. "
                + "Turn it off if what you type does not reach the mod in this game — the keyboard is then taken the whole time a window is open.");

            // ⚠ The two boxes are fixed by opposite moves, and saying so is the point: a plain
            // "turn off if it misbehaves" would leave someone in front of two boxes with no way to
            // tell which one. Parent off = the game gets its keyboard back. Child off = the mod
            // takes it more, not less.
            var focusHint = UIStyles.CreateHint(card, "KeyboardFocusHint",
                "The game keeps its keys until you type or navigate here. If what you type never reaches the mod, turn this one off.");
            RegisterUIText(focusHint);

            UIStyles.CreateSpacer(card, 5);

            // ⚠ These two were ONE box, "Take mouse clicks", and it took away two unrelated
            // things at once: the game's menus answer a RAYCAST, its own clicks are a READ. Giving
            // the menus back therefore also gave the game every click, so clicking beside this
            // window fired a weapon. Separate boxes, separate reasons for greying out.
            CreateCaptureToggle(card, "CaptureGameMenus", " Take clicks from the game's menus",
                "The game's own buttons and menus stop answering the pointer. Clicks inside this window never reach them "
                + "either way — this is about the rest of the screen.",
                UniverseLib.Input.InputCapture.CaptureKind.GameMenus, out _captureGameMenusToggle);

            CreateCaptureToggle(card, "CaptureGameClicks", " Take clicks from the game itself",
                "The game stops reading clicks for what it does on its own — shooting, interacting, dragging. "
                + "Without it, clicking beside this window still acts in the game.",
                UniverseLib.Input.InputCapture.CaptureKind.GameClicks, out _captureGameClicksToggle);

            CreateCaptureToggle(card, "CaptureMouseAxes", " Take mouse movement",
                "Stops the camera turning while you use the window. Mostly matters in first-person games.",
                UniverseLib.Input.InputCapture.CaptureKind.MouseAxes, out _captureMouseAxesToggle);

            // Says what the split is FOR. Three boxes with three descriptions still leave the
            // useful combination to be guessed, and it is the one people actually want.
            var mouseHint = UIStyles.CreateHint(card, "MouseCaptureHint",
                "To hold the view still while the game's own menus keep working: take mouse movement, and leave the two above off.");
            RegisterUIText(mouseHint);

            UIStyles.CreateSpacer(card, 15);

            // ── Freezing the game ────────────────────────────────────────────────────────────
            // Not a capture: the others stop the game RECEIVING, this stops it ADVANCING. Its own
            // section, off by default, and three separate lines — what it does, why it is off, and
            // what is dangerous. The last must not dissolve into the second: it is the only one
            // that can cost somebody their account.
            var pauseTitle = UIStyles.CreateSectionTitle(card, "PauseLabel", "Freezing the game");
            RegisterUIText(pauseTitle);

            var pauseObj = UIFactory.CreateToggle(card, "PauseGameToggle", out _pauseGameToggle, out var pauseLabel);
            pauseLabel.text = " Freeze the game while this window is open";
            UIFactory.SetLayoutElement(pauseObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(pauseLabel);

            string antiCheat = GamePause.AntiCheat;
            bool pausePossible = string.IsNullOrEmpty(antiCheat);
            pauseLabel.color = pausePossible ? UIStyles.TextPrimary : UIStyles.TextMuted;
            _pauseGameToggle.interactable = pausePossible;
            UIHelpers.AddToggleListener(_pauseGameToggle, _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); });

            if (pausePossible)
            {
                _helpZone?.Describe(pauseObj,
                    "The game stops on the current frame; hovering and picking still work.");

                var pauseWhy = UIStyles.CreateHint(card, "PauseWhy",
                    "Off by default: what it does depends on the game. Some ignore it entirely, others cope badly with being frozen. Try it — nothing is changed permanently.");
                RegisterUIText(pauseWhy);

                // ⚠ Never "online" for the GAME: the mod has its own Online mode and a player
                // would read this as a rule about that. "Multiplayer" and "the game's server"
                // can only mean the game.
                var pauseDanger = UIStyles.CreateHint(card, "PauseDanger",
                    "Do not use this in a multiplayer game. The game's server does not stop: your character stays exposed and your session can desynchronise. Some anti-cheat systems also treat this as cheating.");
                pauseDanger.color = UIStyles.StatusWarning;
                RegisterUIText(pauseDanger);
            }
            else
            {
                string why = $"Unavailable: this game is protected by {antiCheat}, which can treat freezing it as cheating.";
                var pauseBlocked = UIStyles.CreateHint(card, "PauseBlocked", why);
                RegisterExcluded(pauseBlocked);   // runtime diagnostic, not UI chrome
                _helpZone?.Describe(pauseObj, why);
            }

            UIStyles.CreateSpacer(card, 15);

            // Moved here from General → Advanced: it belongs with the other three, being the same
            // question asked the other way round — this one HANDS INPUT BACK to the game.
            var advancedTitle = UIStyles.CreateSectionTitle(card, "InputAdvancedLabel", "Advanced");
            RegisterUIText(advancedTitle);

            var eventSystemObj = UIFactory.CreateToggle(card, "DisableEventSystemToggle", out _disableEventSystemOverrideToggle, out var eventSystemLabel);
            eventSystemLabel.text = " Let the game handle its own interface input";
            eventSystemLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(eventSystemObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(eventSystemLabel);
            _helpZone?.Describe(eventSystemObj, "Stop the mod from taking the game's EventSystem. Turn on if the game's own menus stop reacting — losing their hover or their selection cursor — while a mod window is open.");

            var eventSystemHint = UIStyles.CreateHint(card, "EventSystemHint", "Turn on if the game's menus stop reacting while a mod window is open.");
            RegisterUIText(eventSystemHint);
        }

        /// <summary>
        /// One opacity slider, with its live percentage.
        /// </summary>
        /// <remarks>
        /// Floors at 40%: uGUI applies the alpha to the whole subtree, text included, so lower is
        /// not translucent but unreadable — and somebody would blame the mod, not the slider.
        /// </remarks>
        private void CreateOpacitySlider(GameObject card, string name, string label, string help,
            float initial, out UnityEngine.UI.Slider slider, out Text valueLabel)
        {
            var row = UIStyles.CreateFormRow(card, name + "Row", UIStyles.RowHeightMedium, 5);

            var caption = UIFactory.CreateLabel(row, name + "Label", label, TextAnchor.MiddleLeft);
            caption.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(caption.gameObject, minWidth: 70);
            RegisterUIText(caption);

            var sliderObj = UIFactory.CreateSlider(row, name + "Slider", out slider);
            UIFactory.SetLayoutElement(sliderObj, minWidth: 150, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            slider.minValue = 0.4f;
            slider.maxValue = 1f;
            slider.value = initial;
            _helpZone?.Describe(sliderObj, help);

            var shown = UIFactory.CreateLabel(row, name + "Value", $"{initial * 100f:0}%", TextAnchor.MiddleRight);
            shown.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(shown.gameObject, minWidth: 45);
            RegisterExcluded(shown);   // a percentage is not chrome to translate
            valueLabel = shown;

            var capturedLabel = shown;
            UIHelpers.AddSliderListener(slider, val =>
            {
                capturedLabel.text = $"{val * 100f:0}%";
                if (!_isLoadingSettings) UpdateApplyButtonText();
            });
        }

        /// <summary>
        /// One capture box, greyed out with the runtime's own explanation when nothing can serve it.
        /// </summary>
        private void CreateCaptureToggle(GameObject card, string name, string label, string help,
            UniverseLib.Input.InputCapture.CaptureKind kind, out Toggle toggle)
        {
            var obj = UIFactory.CreateToggle(card, name, out toggle, out var text);
            text.text = label;
            UIFactory.SetLayoutElement(obj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(text);

            bool possible = UniverseLib.Input.InputCapture.CanCapture(kind);
            text.color = possible ? UIStyles.TextPrimary : UIStyles.TextMuted;
            toggle.interactable = possible;
            UIHelpers.AddToggleListener(toggle, _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); });

            if (possible)
            {
                _helpZone?.Describe(obj, help);
                return;
            }

            // Say why, in place — a box that is simply grey reads as a bug, or as a setting the
            // player broke themselves. The sentence comes from whichever strategy would have
            // served this, so it names the actual obstacle on THIS game.
            string why = UniverseLib.Input.InputCapture.WhyNot(kind);
            var reason = UIStyles.CreateHint(card, name + "Why", why);
            RegisterExcluded(reason);   // runtime diagnostic text, not UI chrome to translate
            _helpZone?.Describe(obj, why);
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
            _helpZone?.Describe(enableObj, "Automatically translate untranslated texts using the backend below (your AI, Google or DeepL). Turning this off pauses translation and keeps everything below as it is, so you can set it up first and start when you are ready.");

            // Backend type section (stays visible when auto-translation is off — see
            // UpdateBackendSections: configuring is what one does before starting)
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

            _aiUrlInput = UIFactory.CreateInputField(urlRow, "AIUrl", Endpoints.OllamaDefault);
            UIFactory.SetLayoutElement(_aiUrlInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_aiUrlInput.Component.gameObject, UIStyles.InputBackground);
            _helpZone?.Describe(_aiUrlInput.Component.gameObject, "Address of your AI server, for example a local Ollama or LM Studio. Default is " + Endpoints.OllamaDefault + ".");

            var testBtn = CreateSecondaryButton(urlRow, "TestBtn", "Test", 60);
            testBtn.OnClick += TestAIConnection;
            RegisterUIText(testBtn.ButtonText);
            _helpZone?.Describe(testBtn.Component.gameObject, "Check that the mod can reach the AI server at the URL above.");

            _aiTestStatusLabel = UIFactory.CreateLabel(_llmSection, "TestStatus", "", TextAnchor.MiddleLeft);
            _aiTestStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_aiTestStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterExcluded(_aiTestStatusLabel);

            // What sending this game's text to that address actually means. Nothing at all for a
            // server on this machine, which is the ordinary case and the one this mod is built
            // around; privacy for a box on the home network; privacy and a bill for anything else.
            //
            // ⚠ The wording comes from the shared library, not from here. It is a statement about
            // somebody's money and somebody's data, the manager makes it too, and two copies would
            // drift — with the under-warning copy landing in front of whoever needed it most.
            _aiLocalityLabel = UIFactory.CreateLabel(_llmSection, "Locality", "", TextAnchor.UpperLeft);
            _aiLocalityLabel.fontSize = UIStyles.FontSizeSmall;
            _aiLocalityLabel.color = UIStyles.StatusWarning;
            UIFactory.SetLayoutElement(_aiLocalityLabel.gameObject, minHeight: UIStyles.RowHeightSmall,
                                       flexibleHeight: 9999);
            RegisterExcluded(_aiLocalityLabel);

            // Follows what is being typed, not what was last applied: somebody pasting a provider's
            // address has to read this before they press Apply, not after.
            // ⚠ Through InputFieldRef's C# event, NEVER Component.onValueChanged.AddListener: under
            // IL2CPP that UnityEvent takes an Il2Cpp proxy delegate and throws MissingMethodException,
            // which kills panel construction and with it the whole mod UI.
            _aiUrlInput.OnValueChanged += _ => RefreshAiLocality();

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

            CreateAiAdvancedSection(_llmSection);

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
            _advancedContent?.SetActive(false);
            _backendTypeSection.SetActive(false);
            _llmSection.SetActive(false);
            _translationApiSection.SetActive(false);
            _googleSection.SetActive(false);
            _deeplSection.SetActive(false);
        }

        /// <summary>
        /// What the model is asked, and how many times — folded away by default.
        ///
        /// ⚠ Collapsed on purpose, and not out of shyness: a temperature is a real translation
        /// change, and every one of these has a default that is right for nearly everybody. What is
        /// behind this header is for someone who already knows what a seed does; putting it in
        /// front of everyone else would make them think a choice was expected of them.
        /// </summary>
        private void CreateAiAdvancedSection(GameObject parent)
        {
            UIStyles.CreateSpacer(parent, 8);

            var (container, header, icon, title, content) =
                UIStyles.CreateCollapsibleSection(parent, "AiAdvanced", "Advanced", initiallyExpanded: false);
            _advancedIconLabel = icon;
            _advancedContent = content;
            _advancedExpanded = false;
            RegisterUIText(title);

            var headerBtn = header.GetComponent<Button>();
            if (headerBtn != null)
            {
                UIHelpers.AddButtonListener(headerBtn, () =>
                {
                    _advancedExpanded = !_advancedExpanded;
                    UIStyles.SetCollapsibleState(_advancedIconLabel, _advancedContent, _advancedExpanded);
                    // The window measures its content to size itself; a section that just unfolded
                    // is content it has never measured.
                    RecalculateSize();
                });
            }

            var attemptsHint = UIStyles.CreateHint(content, "AttemptsHint",
                "How many requests one line may cost at most — used both to repair a broken placeholder and to retranslate a line you did not like");
            RegisterUIText(attemptsHint);

            _aiMaxAttemptsInput = CreateAdvancedNumberRow(content, "MaxAttempts", "Attempts:", "3",
                "Each attempt is a real request to your AI. 1 means never ask twice. Default 3.");

            UIStyles.CreateSpacer(content, 8);

            var tempHint = UIStyles.CreateHint(content, "TempHint",
                "Temperature: 0 always gives the same answer for the same line, higher wanders further from it");
            RegisterUIText(tempHint);

            _aiTemperatureInput = CreateAdvancedNumberRow(content, "Temp", "Translating:", "0",
                "Ordinary translation. Zero by default so the same line always gets the same translation — the file is cached, shared and merged with other people's.");
            _aiTemperatureRepairInput = CreateAdvancedNumberRow(content, "TempRepair", "Repairing:", "0.3",
                "Used when the answer broke a [!v*0]-style marker and has to be asked again. Just above zero: the same request would return the same broken answer.");
            _aiTemperatureRetranslateInput = CreateAdvancedNumberRow(content, "TempRetrans", "Retranslating:", "0.8",
                "Used by the Retranslate button, when you did not like the translation. High on purpose: same instructions, different wording.");

            UIStyles.CreateSpacer(content, 8);

            var seedHint = UIStyles.CreateHint(content, "SeedHint",
                "Seed: leave empty unless you want the same run twice. Many servers accept it and ignore it");
            RegisterUIText(seedHint);

            _aiSeedInput = CreateAdvancedNumberRow(content, "Seed", "Translating:", "empty",
                "Fixed seed for ordinary translation. Empty sends none.");
            _aiSeedRepairInput = CreateAdvancedNumberRow(content, "SeedRepair", "Repairing:", "empty",
                "Fixed seed when re-asking after a broken marker. Empty sends none.");
            _aiSeedRetranslateInput = CreateAdvancedNumberRow(content, "SeedRetrans", "Retranslating:", "empty",
                "Fixed seed for the Retranslate button. It is offset by the attempt number, so retranslating still varies — a single fixed seed would hand back the answer you just rejected, every time. Empty draws a new one each attempt.");
        }

        /// <summary>One labelled number field of the Advanced block.</summary>
        private InputFieldRef CreateAdvancedNumberRow(GameObject parent, string name, string label,
            string placeholder, string help)
        {
            var row = UIStyles.CreateFormRow(parent, name + "Row", UIStyles.InputHeight, 5);

            var caption = UIFactory.CreateLabel(row, name + "Label", label, TextAnchor.MiddleLeft);
            caption.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(caption.gameObject, minWidth: 110);
            RegisterUIText(caption);

            var input = UIFactory.CreateInputField(row, name + "Input", placeholder);
            UIFactory.SetLayoutElement(input.Component.gameObject, minWidth: 70, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(input.Component.gameObject, UIStyles.InputBackground);
            _helpZone?.Describe(input.Component.gameObject, help);

            // Deliberately NOT ContentType.DecimalNumber: it is locale-aware, so on a machine whose
            // decimal separator is a comma the field refuses the dot these values are written with,
            // and the value is parsed with InvariantCulture on the way out. Validation happens at
            // Apply, where a bad entry falls back to the default instead of being silently eaten.
            return input;
        }

        /// <summary>An optional seed as the text of a field: null becomes empty, never "0".</summary>
        private static string SeedToText(int? seed)
        {
            return seed.HasValue ? seed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        /// <summary>
        /// Read an optional seed back from a field. Anything unreadable is treated as "none" rather
        /// than as zero: zero is a legitimate seed, and guessing it from a typo would quietly pin
        /// every request to it.
        /// </summary>
        private static int? SeedFromText(string text)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(text.Trim())) return null;
            int value;
            return int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value) ? (int?)value : null;
        }

        /// <summary>A temperature from a field, falling back to the default when unreadable.</summary>
        private static double TemperatureFromText(string text, double fallback)
        {
            double value;
            if (!double.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out value))
                return fallback;
            if (value < 0.0) return 0.0;
            return value > 2.0 ? 2.0 : value;
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

            // 🔴 **Before the rhythm, because it says what does not follow it.** The two used to be
            // one list, so choosing "real-time" also put other people's work on that connection: a
            // Main was woken by every contribution anybody sent, and somebody publishing every ten
            // minutes woke each of their contributors just as often. One question each now — what
            // is mine can be immediate, what is other people's has a pace.
            var realtimeObj = UIFactory.CreateToggle(card, "RealtimeOwnToggle",
                                                     out _realtimeOwnToggle, out var realtimeLabel);
            realtimeLabel.text = " Real-time check for your own translation";
            realtimeLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(realtimeObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(realtimeLabel);
            UIHelpers.AddToggleListener(_realtimeOwnToggle,
                                        _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); });
            _helpZone?.Describe(realtimeObj,
                "Keeps a connection open so that what you publish from the website, or from another "
                + "computer, comes back to the game as it happens. Only ever about your own line: "
                + "contributions you receive and the original you contribute to follow the rhythm "
                + "below. Nothing is opened when you have published nothing of your own.");

            var realtimeHint = UIStyles.CreateHint(card, "RealtimeOwnHint",
                "Changes you publish from the website or another machine come back straight away, "
                + "rather than waiting for the next check.");
            RegisterUIText(realtimeHint);

            var freqRow = UIStyles.CreateFormRow(card, "CheckFreqRow", UIStyles.RowHeightMedium, 5);
            // Not just "Check for updates": the word alone left people guessing what
            // was being checked, and for which role
            var freqLabel = UIFactory.CreateLabel(freqRow, "CheckFreqLabel", "Ask the website every:", TextAnchor.MiddleLeft);
            freqLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(freqLabel.gameObject, minWidth: 130);
            RegisterUIText(freqLabel);

            _checkFrequencyDropdown = new SearchableDropdown(
                "CheckFrequency",
                UpdateFrequencyDisplayOptions,
                FrequencyConfigToDisplay(UpdateCheckFrequency.Hourly),
                popupHeight: 150,
                showSearch: false
            );
            var freqDropdownObj = _checkFrequencyDropdown.CreateUI(freqRow, (_) => { UpdateApplyButtonText(); });
            UIFactory.SetLayoutElement(freqDropdownObj, minWidth: 200, minHeight: UIStyles.InputHeight);
            _helpZone?.Describe(freqDropdownObj,
                "How often the mod asks the website what changed: contributions waiting for your "
                + "review if you own a translation, the original translation if you contribute to "
                + "someone else's, and a newer version of the translation you use. Your own line is "
                + "in here too, unless Real-time check is on. Editing in the browser is a separate, "
                + "instant channel and is never affected by this setting.");

            var freqHint = UIStyles.CreateHint(card, "CheckFreqHint",
                "Contributions you received, a Main that moved, a newer version published — and "
                + "your own translation when Real-time check is off.");
            RegisterUIText(freqHint);

            var freqStartupHint = UIStyles.CreateHint(card, "CheckFreqStartupHint",
                "Every option except Never also checks once when the game starts.");
            RegisterUIText(freqStartupHint);

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

            // The way back from a declined replacement — or from local tinkering. Hidden unless
            // the settings actually differ from the online version, so it never suggests undoing
            // something that was not done. Filled by RefreshSettingsDriftRow.
            _settingsDriftRow = UIStyles.CreateFormRow(card, "SettingsDriftRow", UIStyles.RowHeightMedium, 5);

            _settingsDriftLabel = UIFactory.CreateLabel(_settingsDriftRow, "SettingsDriftLabel", "", TextAnchor.MiddleLeft);
            _settingsDriftLabel.fontSize = UIStyles.FontSizeSmall;
            _settingsDriftLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_settingsDriftLabel.gameObject, flexibleWidth: 9999);
            RegisterExcluded(_settingsDriftLabel);

            _restoreSettingsBtn = CreateSecondaryButton(_settingsDriftRow, "RestoreSettingsBtn", "Review…", 100);
            _restoreSettingsBtn.OnClick += OnRestoreSettingsClicked;
            RegisterUIText(_restoreSettingsBtn.ButtonText);
            _helpZone?.Describe(_restoreSettingsBtn.Component.gameObject,
                "Compare your fonts, exclusions and other file settings with the online version, and choose section by section which ones to take back. Nothing changes until you press Apply.");

            _settingsDriftRow.SetActive(false);

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

                // Keeps the window from resizing when the visitor switches tabs
                KeepPanelHeightAcrossTabs(_tabBar);
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

        /// <summary>
        /// Show the way back only when there is somewhere to go back to. Recomputed on every
        /// opening rather than remembered: the settings can drift, and come back, at any time.
        /// </summary>
        private void RefreshSettingsDriftRow()
        {
            if (_settingsDriftRow == null) return;

            var reference = TranslatorCore.GetOnlineSettingsReference();
            bool drifted = reference != null && reference.HasDifferences;
            _settingsDriftRow.SetActive(drifted);

            if (!drifted) return;

            int count = reference.DifferingSections.Count;
            string what = string.Join(", ", reference.DifferingSections
                .Select(SettingsSection.DisplayName).ToArray());

            // Names the sections rather than counting them: "2 sections differ" tells nobody
            // whether their fonts or their exclusions are the ones that moved.
            SetDynamicText(_settingsDriftLabel,
                TranslatorCore.TranslateOwnUIDynamic(count == 1
                    ? $"Your settings differ from {reference.Label}:"
                    : $"Your settings differ from {reference.Label} in {count} sections:") + " " + what);
        }

        private void OnRestoreSettingsClicked()
        {
            TranslatorUIManager.RestoreOnlineSettings();
        }

        private void LoadCurrentSettings()
        {
            _isLoadingSettings = true;

            RefreshSettingsDriftRow();

            // General
            _enableTranslationsToggle.isOn = TranslatorCore.Config.enable_translations;
            // Tri-state: show what is IN EFFECT (the user's choice, or the translation's when they
            // never made one). Ticking the box then records an explicit choice.
            _translateModUIToggle.isOn = TranslatorCore.ShouldTranslateOwnUI;

            // Interface font: sync the picker visibility with the checkbox on (re)load.
            if (_interfaceFontRow != null)
                _interfaceFontRow.SetActive(_translateModUIToggle.isOn);

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
            _checkFrequencyDropdown.SelectedValue = FrequencyConfigToDisplay(TranslatorCore.Config.sync.update_check_frequency);
            _realtimeOwnToggle.isOn = TranslatorCore.Config.sync.realtime_own_translation;
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
            _aiUrlInput.Text = TranslatorCore.Config.ai_url ?? Endpoints.OllamaDefault;
            RefreshAiLocality();
            _aiApiKeyInput.Text = TranslatorCore.Config.ai_api_key ?? "";
            _googleApiKeyInput.Text = TranslatorCore.Config.google_api_key ?? "";
            _deeplApiKeyInput.Text = TranslatorCore.Config.deepl_api_key ?? "";
            _deeplUseFreeToggle.isOn = TranslatorCore.Config.deepl_use_free;
            _rateLimitDelayInput.Text = TranslatorCore.Config.rate_limit_retry_delay.ToString();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // Invariant on the way in as well as out: written back with the machine's culture, a
            // comma-separator locale would store "0,8" and read it as 8 on the next launch.
            if (_aiMaxAttemptsInput != null) _aiMaxAttemptsInput.Text = TranslatorCore.Config.ai_max_attempts.ToString(inv);
            if (_aiTemperatureInput != null) _aiTemperatureInput.Text = TranslatorCore.Config.ai_temperature.ToString(inv);
            if (_aiTemperatureRepairInput != null) _aiTemperatureRepairInput.Text = TranslatorCore.Config.ai_temperature_repair.ToString(inv);
            if (_aiTemperatureRetranslateInput != null) _aiTemperatureRetranslateInput.Text = TranslatorCore.Config.ai_temperature_retranslate.ToString(inv);
            if (_aiSeedInput != null) _aiSeedInput.Text = SeedToText(TranslatorCore.Config.ai_seed);
            if (_aiSeedRepairInput != null) _aiSeedRepairInput.Text = SeedToText(TranslatorCore.Config.ai_seed_repair);
            if (_aiSeedRetranslateInput != null) _aiSeedRetranslateInput.Text = SeedToText(TranslatorCore.Config.ai_seed_retranslate);
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

            // Reads enable_ai, not the backend: a paused setup keeps its backend, so asking the
            // backend would show the switch as on while nothing translates. "none" is still
            // honoured — it is what a community-translations-only setup carries, and there is
            // nothing there to switch on.
            _enableTranslationBackendToggle.isOn =
                TranslatorCore.Config.enable_ai && backend != "none";

            // Done loading — enable listeners and apply section visibility once
            _isLoadingSettings = false;
            UpdateBackendSections();

            // Advanced settings (per-game, stored in translations.json)
            _disableEventSystemOverrideToggle.isOn = TranslatorCore.DisableEventSystemOverride;
            _captureKeyboardToggle.isOn = TranslatorCore.CaptureKeyboard;
            _captureKeyboardFocusOnlyToggle.isOn = TranslatorCore.CaptureKeyboardFocusOnly;
            _captureGameMenusToggle.isOn = TranslatorCore.CaptureGameMenus;
            _captureGameClicksToggle.isOn = TranslatorCore.CaptureGameClicks;
            _captureMouseAxesToggle.isOn = TranslatorCore.CaptureMouseAxes;
            _pauseGameToggle.isOn = TranslatorCore.PauseGame;
            _opacityFocusedSlider.value = TranslatorCore.PanelOpacityFocused;
            _opacityUnfocusedSlider.value = TranslatorCore.PanelOpacityUnfocused;

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

                // ⚠ The reason, and it is not always the same one. A file being written here can
                // still be re-targeted — by clearing it — where a published one never can.
                if (locked && _lockedHeader != null)
                {
                    _lockedHeader.text = TranslatorCore.LanguagesLockedByPublishing
                        ? "Languages are settled: this translation is published."
                        : "Languages are settled: this file already holds lines. Clear the "
                          + "translation to change them.";
                }

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
            _checkFrequencyDropdown.SetInteractable(enabled);
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

                SetDynamicText(_resetWindowsStatusLabel, "Positions reset!");
                _resetWindowsStatusLabel.color = UIStyles.StatusSuccess;

                TranslatorCore.LogInfo("[Options] Window preferences reset");
            }
            catch (Exception e)
            {
                _resetWindowsStatusLabel.text = Tr("Error:") + $" {e.Message}";
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

        /// <summary>
        /// WHICH service the dropdowns are pointing at — never whether it runs.
        ///
        /// ⚠ This used to return "none" when the toggle was off, which meant switching
        /// translation off ERASED the choice of backend from config.json: reopening the screen
        /// showed LLM whatever had been configured, and any credential entered for the other one
        /// was left dangling with nothing pointing at it. Switching something off must not
        /// unconfigure it. The toggle now writes enable_ai, so a paused setup is a complete
        /// setup that simply is not running.
        /// </summary>
        /// <summary>
        /// Puts the caution in step with the address being typed, or clears it.
        ///
        /// ⚠ An empty field says nothing. Somebody who has not typed an address has made no
        /// decision to be cautioned about, and meeting them with a bill notice answers a question
        /// they never asked.
        /// </summary>
        private void RefreshAiLocality()
        {
            if (_aiLocalityLabel == null) return;

            string typed = _aiUrlInput?.Text;
            string caution = string.IsNullOrEmpty(typed) || typed.Trim().Length == 0
                ? null
                : Endpoints.CautionFor(typed.Trim());

            _aiLocalityLabel.text = caution ?? "";
            _aiLocalityLabel.gameObject.SetActive(caution != null);
        }

        private string GetSelectedBackendConfig()
        {
            string type = _backendTypeDropdown?.SelectedValue ?? UIStyles.BackendTypeLLM;
            if (type == UIStyles.BackendTypeLLM) return "llm";

            // Translation API -> check provider
            string provider = _providerDropdown?.SelectedValue ?? "Google Translate";
            return provider == "DeepL" ? "deepl" : "google";
        }

        private void UpdateBackendSections()
        {
            bool captureOnly = _captureKeysOnlyToggle.isOn;

            _enableTranslationBackendToggle.interactable = !captureOnly;

            // ⚠ The backend settings stay VISIBLE and editable while translation is switched off,
            // and that is the point of the switch. Setting up a server, a model or an API key is
            // exactly what one does before starting — hiding the fields until translation is
            // running meant it had to be started, in a game, before it could be configured.
            //
            // Capture-only is different: there is no backend at all in that mode, so there is
            // nothing to configure and the sections go.
            _backendTypeSection?.SetActive(!captureOnly);

            if (captureOnly)
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
                SetDynamicText(_googleTestStatusLabel, "Enter an API key first");
                _googleTestStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            SetDynamicText(_googleTestStatusLabel, "Testing...");
            _googleTestStatusLabel.color = UIStyles.TextSecondary;

            bool success = await TranslatorCore.TestGoogleConnection(apiKey);

            TranslatorUIManager.RunOnMainThread(() =>
            {
                if (success)
                {
                    SetDynamicText(_googleTestStatusLabel, "Connected!");
                    _googleTestStatusLabel.color = UIStyles.StatusSuccess;
                }
                else
                {
                    SetDynamicText(_googleTestStatusLabel, "Failed - check API key");
                    _googleTestStatusLabel.color = UIStyles.StatusError;
                }
            });
        }

        private async void TestDeepLConnection()
        {
            string apiKey = _deeplApiKeyInput?.Text;
            if (string.IsNullOrEmpty(apiKey))
            {
                SetDynamicText(_deeplTestStatusLabel, "Enter an API key first");
                _deeplTestStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            SetDynamicText(_deeplTestStatusLabel, "Testing...");
            _deeplTestStatusLabel.color = UIStyles.TextSecondary;

            bool useFree = _deeplUseFreeToggle.isOn;
            bool success = await TranslatorCore.TestDeepLConnection(apiKey, useFree);

            TranslatorUIManager.RunOnMainThread(() =>
            {
                if (success)
                {
                    SetDynamicText(_deeplTestStatusLabel, "Connected!");
                    _deeplTestStatusLabel.color = UIStyles.StatusSuccess;
                }
                else
                {
                    SetDynamicText(_deeplTestStatusLabel, "Failed - check API key and plan type");
                    _deeplTestStatusLabel.color = UIStyles.StatusError;
                }
            });
        }

        private async void OnCheckModUpdatesNowClicked()
        {
            if (!TranslatorCore.Config.online_mode)
            {
                SetDynamicText(_checkModUpdatesStatusLabel, "Enable online mode first");
                _checkModUpdatesStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            _checkModUpdatesNowBtn.Component.interactable = false;
            SetDynamicText(_checkModUpdatesStatusLabel, "Checking...");
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

                        _checkModUpdatesStatusLabel.text = Tr("Update available:") + $" v{latestVersion}";
                        _checkModUpdatesStatusLabel.color = UIStyles.StatusSuccess;

                        TranslatorUIManager.MainPanel?.RefreshUI();
                    }
                    else if (success)
                    {
                        _checkModUpdatesStatusLabel.text = Tr("Up to date") + $" (v{currentVersion})";
                        _checkModUpdatesStatusLabel.color = UIStyles.StatusSuccess;
                    }
                    else
                    {
                        _checkModUpdatesStatusLabel.text = Tr("Error:") + $" {error}";
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
                    _checkModUpdatesStatusLabel.text = Tr("Error:") + $" {errorMsg}";
                    _checkModUpdatesStatusLabel.color = UIStyles.StatusError;
                    _checkModUpdatesNowBtn.Component.interactable = true;
                });
            }
        }

        private async void TestAIConnection()
        {
            SetDynamicText(_aiTestStatusLabel, "Testing...");
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
                        SetDynamicText(_aiTestStatusLabel, "Connection successful!");
                        _aiTestStatusLabel.color = UIStyles.StatusSuccess;
                        // Auto-refresh models on successful test
                        RefreshModels();
                    }
                    else
                    {
                        SetDynamicText(_aiTestStatusLabel, "Connection failed");
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
                    _aiTestStatusLabel.text = Tr("Error:") + $" {errorMsg}";
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
            if (sys != null)
            {
                // Only offer fonts the OS can actually hand us a file for — an entry the runtime
                // cannot resolve would silently render as the default font instead.
                foreach (string font in sys)
                {
                    if (AssetAvailability.IsSystemFontAvailable(font))
                        options.Add(font);
                }
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
                // Applying records an EXPLICIT choice (tri-state leaves "undecided" for users who
                // never opened this, letting the translation decide for them).
                TranslatorCore.Config.translate_mod_ui = _translateModUIToggle.isOn;
                TranslatorCore.Config.interface_font = _interfaceFontDropdown != null
                    ? NormalizeInterfaceFont(_interfaceFontDropdown.SelectedValue)
                    : TranslatorCore.Config.interface_font;
                TranslatorCore.InvalidateInterfaceFontAvailability();

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
                // The toggle says whether translation runs; the dropdowns say what runs it. Two
                // questions, two keys — so turning it off here and turning it off with the pause
                // hotkey now leave the file in exactly the same state, and neither loses a
                // setting on the way.
                TranslatorCore.Config.enable_ai =
                    _enableTranslationBackendToggle != null && _enableTranslationBackendToggle.isOn;
                // Capture mode works WITHOUT a backend: the worker must run to
                // store the H+empty entries (it never calls any backend then)
                TranslatorCore.EnsureWorkerRunning();
                // Kept before they are overwritten: the model being left behind lives on the
                // address that was in use, which this very screen may also be changing.
                string previousUrl = TranslatorCore.Config.ai_url;
                string previousModel = TranslatorCore.Config.ai_model;

                TranslatorCore.Config.ai_url = _aiUrlInput.Text;
                string apiKeyValue = _aiApiKeyInput.Text;
                TranslatorCore.Config.ai_api_key = !string.IsNullOrEmpty(apiKeyValue) ? apiKeyValue : null;
                TranslatorCore.Config.ai_model = _modelDropdown.SelectedValue ?? "";

                // A model nobody is going to use again should not go on holding the graphics card
                // the game is playing on. Ollama keeps it for five minutes otherwise, and the
                // replacement gets whatever room is left — which is how switching model to gain
                // speed ends up losing it.
                if (!string.IsNullOrEmpty(previousModel)
                    && previousModel != TranslatorCore.Config.ai_model)
                {
                    TranslatorCore.ReleaseModel(previousUrl, previousModel);
                }
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

                // Advanced (AI). Anything unreadable falls back to the default rather than to zero:
                // an empty attempts field must not mean "never ask", and a mistyped temperature must
                // not silently turn every translation deterministic — or the opposite.
                int attempts;
                if (int.TryParse((_aiMaxAttemptsInput?.Text ?? "").Trim(), System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out attempts) && attempts >= 1 && attempts <= 10)
                    TranslatorCore.Config.ai_max_attempts = attempts;
                else
                    TranslatorCore.Config.ai_max_attempts = Placeholders.MaxAttempts;

                TranslatorCore.Config.ai_temperature = TemperatureFromText(_aiTemperatureInput?.Text, 0.0);
                TranslatorCore.Config.ai_temperature_repair = TemperatureFromText(_aiTemperatureRepairInput?.Text, 0.3);
                TranslatorCore.Config.ai_temperature_retranslate = TemperatureFromText(_aiTemperatureRetranslateInput?.Text, 0.8);
                TranslatorCore.Config.ai_seed = SeedFromText(_aiSeedInput?.Text);
                TranslatorCore.Config.ai_seed_repair = SeedFromText(_aiSeedRepairInput?.Text);
                TranslatorCore.Config.ai_seed_retranslate = SeedFromText(_aiSeedRetranslateInput?.Text);

                // Online mode - detect transition for sync stream management
                bool wasOnline = TranslatorCore.Config.online_mode;
                bool nowOnline = _onlineModeToggle.isOn;
                TranslatorCore.Config.online_mode = nowOnline;

                // Applied in place, not "next launch": turning the stream on must open it now, and
                // turning it off must close it now. Same for the rhythm.
                string previousFrequency = UpdateCheckFrequency.Normalize(TranslatorCore.Config.sync.update_check_frequency);
                string newFrequency = FrequencyDisplayToConfig(_checkFrequencyDropdown.SelectedValue);

                bool previousRealtime = TranslatorCore.Config.sync.realtime_own_translation;
                bool newRealtime = _realtimeOwnToggle.isOn;

                bool frequencyChanged = previousFrequency != newFrequency
                                        || previousRealtime != newRealtime;

                TranslatorCore.Config.sync.update_check_frequency = newFrequency;
                TranslatorCore.Config.sync.realtime_own_translation = newRealtime;
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
                // Keep UniverseLib's own copy in step: it consults the flag live (its EventSystem
                // patches read it every time), so with this in place the setting takes effect at
                // once instead of only at the next launch.
                UniverseLib.Config.ConfigManager.Disable_EventSystem_Override = TranslatorCore.DisableEventSystemOverride;

                // Only the EventSystem override lives in the translation: it answers a defect of
                // a particular game and is worth carrying to whoever installs that translation.
                // The capture options and the freeze are preferences and go to config.json, which
                // SaveConfig writes below — they must not ride along when a translation is shared.
                bool perGameChanged = eventSystemChanged;

                // Input capture (per-game too). No restart needed: the capture asks these on every
                // read, so unticking one hands that input back to the game on the next frame —
                // which is what someone turning it off because the game misbehaves needs.
                TranslatorCore.CaptureKeyboard = _captureKeyboardToggle.isOn;
                TranslatorCore.CaptureKeyboardFocusOnly = _captureKeyboardFocusOnlyToggle.isOn;
                TranslatorCore.CaptureGameMenus = _captureGameMenusToggle.isOn;
                TranslatorCore.CaptureGameClicks = _captureGameClicksToggle.isOn;
                TranslatorCore.CaptureMouseAxes = _captureMouseAxesToggle.isOn;
                TranslatorCore.PauseGame = _pauseGameToggle.isOn;
                TranslatorCore.PanelOpacityFocused = _opacityFocusedSlider.value;
                TranslatorCore.PanelOpacityUnfocused = _opacityUnfocusedSlider.value;

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

                // Save per-game settings (translations.json)
                if (perGameChanged)
                {
                    TranslatorCore.SaveCache();
                    TranslatorCore.LogInfo("[Options] EventSystem override setting changed — applied on the next tick, no restart needed");
                }

                TranslatorCore.LogInfo("[Options] Settings saved successfully");

                // Interface font: (re)apply the mod UI font from the committed config.
                TranslatorUIManager.ApplyInterfaceFont();
                // Mod UI translation: enable → submit our text; disable (or missing font) → English.
                if (TranslatorCore.ShouldTranslateOwnUI)
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
                    TranslatorCore.LogInfo("[Options] Online mode enabled, watching for updates...");
                    TranslatorUIManager.StartSyncWatch();
                    if (TranslatorCore.Config.sync.check_mod_updates)
                    {
                        TranslatorUIManager.CheckForModUpdates();
                    }
                }
                else if (nowOnline && frequencyChanged)
                {
                    // Same online state, different rhythm: restart on the new one
                    TranslatorCore.LogInfo($"[Options] Update check frequency changed to {newFrequency}");
                    TranslatorUIManager.StartSyncWatch();
                }
                else if (!nowOnline && wasOnline)
                {
                    // Switched from online to offline - stop sync stream and clear server state
                    TranslatorCore.LogInfo("[Options] Online mode disabled, stopping sync stream...");
                    TranslatorUIManager.StopSyncWatch();

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
                _aiTestStatusLabel.text = Tr("Error:") + $" {e.Message}";
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
            if (_aiMaxAttemptsInput != null) _aiMaxAttemptsInput.OnValueChanged += _ => UpdateApplyButtonText();
            if (_aiTemperatureInput != null) _aiTemperatureInput.OnValueChanged += _ => UpdateApplyButtonText();
            if (_aiTemperatureRepairInput != null) _aiTemperatureRepairInput.OnValueChanged += _ => UpdateApplyButtonText();
            if (_aiTemperatureRetranslateInput != null) _aiTemperatureRetranslateInput.OnValueChanged += _ => UpdateApplyButtonText();
            if (_aiSeedInput != null) _aiSeedInput.OnValueChanged += _ => UpdateApplyButtonText();
            if (_aiSeedRepairInput != null) _aiSeedRepairInput.OnValueChanged += _ => UpdateApplyButtonText();
            if (_aiSeedRetranslateInput != null) _aiSeedRetranslateInput.OnValueChanged += _ => UpdateApplyButtonText();

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
        /// How many settings differ from their initial values — counted AND marked in one pass,
        /// from the list <see cref="RegisterPendingFields"/> built (see PendingMarks).
        /// </summary>
        private int CountPendingChanges()
        {
            if (_initialSnapshot == null) return 0;
            return Pending.Refresh();
        }

        /// <summary>
        /// Every field Apply writes, with the test that says whether it changed — the SAME
        /// readers Apply uses, so what the counter promises and what Apply writes cannot drift
        /// apart. Each test reads the snapshot at call time: the snapshot moves on Apply and on
        /// every opening, the registrations do not.
        /// </summary>
        private void RegisterPendingFields()
        {
            var P = Pending;
            ConfigSnapshot S() => _initialSnapshot;

            // General
            P.Track(_enableTranslationsToggle.gameObject, () => _enableTranslationsToggle.isOn != S().enable_translations);
            P.Track(_translateModUIToggle.gameObject, () => _translateModUIToggle.isOn != S().translate_mod_ui);
            P.Track(_interfaceFontDropdown?.Root, () => NormalizeInterfaceFont(_interfaceFontDropdown?.SelectedValue) != S().interface_font);

            // Languages
            P.Track(_sourceLanguageDropdown.Root, () =>
                _sourceLanguageDropdown.SelectedValue != (S().source_language == "auto" ? "auto (Detect)" : S().source_language));
            P.Track(_targetLanguageDropdown.Root, () =>
                _targetLanguageDropdown.SelectedValue != (S().target_language == "auto" ? "auto (System)" : S().target_language));

            // Hotkeys
            P.Track(_hotkeyCapture.Root, () => _hotkeyCapture.HotkeyString != S().settings_hotkey);
            P.Track(_hotkeyToggleTranslations.Root, () => _hotkeyToggleTranslations.HotkeyString != S().toggle_translations_hotkey);
            P.Track(_hotkeyToggleAI.Root, () => _hotkeyToggleAI.HotkeyString != S().toggle_ai_hotkey);
            P.Track(_hotkeyToggleImages.Root, () => _hotkeyToggleImages.HotkeyString != S().toggle_images_hotkey);
            P.Track(_hotkeyToggleFonts.Root, () => _hotkeyToggleFonts.HotkeyString != S().toggle_fonts_hotkey);
            P.Track(_hotkeyToggleOverlay.Root, () => _hotkeyToggleOverlay.HotkeyString != S().toggle_overlay_hotkey);
            P.Track(_hotkeyOpenInspector.Root, () => _hotkeyOpenInspector.HotkeyString != S().open_inspector_hotkey);
            P.Track(_hotkeyOpenUpload.Root, () => _hotkeyOpenUpload.HotkeyString != S().open_upload_hotkey);
            P.Track(_hotkeyOpenExclusion.Root, () => _hotkeyOpenExclusion.HotkeyString != S().open_exclusion_mode_hotkey);
            P.Track(_hotkeyOpenTextEditor.Root, () => _hotkeyOpenTextEditor.HotkeyString != S().open_text_editor_hotkey);
            P.Track(_hotkeyForceScan.Root, () => _hotkeyForceScan.HotkeyString != S().force_scan_hotkey);

            // Translation (Backend + Capture)
            P.Track(_captureKeysOnlyToggle.gameObject, () => _captureKeysOnlyToggle.isOn != S().capture_keys_only);
            P.Track(_debugLoggingToggle?.gameObject, () => _debugLoggingToggle.isOn != S().debug);
            P.Track(_debugAiToggle?.gameObject, () => _debugAiToggle.isOn != S().debug_ai);
            // The backend is one config value read from two dropdowns: the type owns a change
            // of kind (AI or service), the provider a change of service within the API kind.
            P.Track(_backendTypeDropdown?.Root, () => (S().translation_backend == "llm") != (GetSelectedBackendConfig() == "llm"));
            P.Track(_providerDropdown?.Root, () =>
            {
                string now = GetSelectedBackendConfig();
                return now != "llm" && S().translation_backend != "llm" && now != S().translation_backend;
            });

            // Counted on its own, because the toggle no longer moves the backend: without this
            // line, switching translation off and pressing nothing would leave Apply reading
            // "Close" and the change would be silently dropped on the way out.
            P.Track(_enableTranslationBackendToggle?.gameObject, () =>
                (_enableTranslationBackendToggle != null && _enableTranslationBackendToggle.isOn) != S().enable_ai);
            P.Track(_aiUrlInput.Component.gameObject, () => _aiUrlInput.Text != S().ai_url);
            P.Track(_aiApiKeyInput.Component.gameObject, () => (_aiApiKeyInput.Text ?? "") != S().ai_api_key);
            P.Track(_modelDropdown.Root, () => (_modelDropdown.SelectedValue ?? "") != S().ai_model);
            P.Track(_gameContextInput.Component.gameObject, () => _gameContextInput.Text != S().game_context);
            P.Track(_strictSourceToggle.gameObject, () => _strictSourceToggle.isOn != S().strict_source_language);
            P.Track(_googleApiKeyInput?.Component.gameObject, () => (_googleApiKeyInput?.Text ?? "") != S().google_api_key);
            P.Track(_deeplApiKeyInput?.Component.gameObject, () => (_deeplApiKeyInput?.Text ?? "") != S().deepl_api_key);
            P.Track(_deeplUseFreeToggle.gameObject, () => _deeplUseFreeToggle.isOn != S().deepl_use_free);
            P.Track(_rateLimitDelayInput?.Component.gameObject, () =>
            {
                float parsedDelay;
                float currentDelay = (float.TryParse(_rateLimitDelayInput?.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsedDelay) && parsedDelay >= 0.1f) ? parsedDelay : 3f;
                return Math.Abs(currentDelay - S().rate_limit_retry_delay) > 0.01f;
            });

            // Advanced (AI) — the same readers Apply uses.
            P.Track(_aiMaxAttemptsInput?.Component.gameObject, () =>
            {
                int parsedAttempts;
                int currentAttempts = (int.TryParse((_aiMaxAttemptsInput?.Text ?? "").Trim(), System.Globalization.NumberStyles.Integer,
                                       System.Globalization.CultureInfo.InvariantCulture, out parsedAttempts)
                                       && parsedAttempts >= 1 && parsedAttempts <= 10)
                                      ? parsedAttempts : Placeholders.MaxAttempts;
                return currentAttempts != S().ai_max_attempts;
            });
            P.Track(_aiTemperatureInput?.Component.gameObject, () => Math.Abs(TemperatureFromText(_aiTemperatureInput?.Text, 0.0) - S().ai_temperature) > 0.001);
            P.Track(_aiTemperatureRepairInput?.Component.gameObject, () => Math.Abs(TemperatureFromText(_aiTemperatureRepairInput?.Text, 0.3) - S().ai_temperature_repair) > 0.001);
            P.Track(_aiTemperatureRetranslateInput?.Component.gameObject, () => Math.Abs(TemperatureFromText(_aiTemperatureRetranslateInput?.Text, 0.8) - S().ai_temperature_retranslate) > 0.001);
            P.Track(_aiSeedInput?.Component.gameObject, () => SeedToText(SeedFromText(_aiSeedInput?.Text)) != S().ai_seed);
            P.Track(_aiSeedRepairInput?.Component.gameObject, () => SeedToText(SeedFromText(_aiSeedRepairInput?.Text)) != S().ai_seed_repair);
            P.Track(_aiSeedRetranslateInput?.Component.gameObject, () => SeedToText(SeedFromText(_aiSeedRetranslateInput?.Text)) != S().ai_seed_retranslate);

            // Online
            P.Track(_onlineModeToggle.gameObject, () => _onlineModeToggle.isOn != S().online_mode);
            P.Track(_checkFrequencyDropdown.Root, () => FrequencyDisplayToConfig(_checkFrequencyDropdown.SelectedValue) != S().update_check_frequency);
            P.Track(_realtimeOwnToggle.gameObject, () => _realtimeOwnToggle.isOn != S().realtime_own_translation);
            P.Track(_notifyUpdatesToggle.gameObject, () => _notifyUpdatesToggle.isOn != S().notify_updates);
            P.Track(_autoDownloadToggle.gameObject, () => _autoDownloadToggle.isOn != S().auto_download);
            P.Track(_checkModUpdatesToggle.gameObject, () => _checkModUpdatesToggle.isOn != S().check_mod_updates);
            P.Track(_notifyPrereleasesToggle.gameObject, () => _notifyPrereleasesToggle.isOn != S().notify_prereleases);
            P.Track(_notificationsEnabledToggle.gameObject, () => _notificationsEnabledToggle.isOn != S().notifications_enabled);
            P.Track(_notificationPositionDropdown.Root, () => PositionDisplayToConfig(_notificationPositionDropdown.SelectedValue) != S().notification_position);

            // Advanced (per-game settings)
            P.Track(_disableEventSystemOverrideToggle.gameObject, () => _disableEventSystemOverrideToggle.isOn != S().disable_eventsystem_override);
            P.Track(_captureKeyboardToggle.gameObject, () => _captureKeyboardToggle.isOn != S().capture_keyboard);
            P.Track(_captureKeyboardFocusOnlyToggle.gameObject, () => _captureKeyboardFocusOnlyToggle.isOn != S().capture_keyboard_focus_only);
            P.Track(_captureGameMenusToggle.gameObject, () => _captureGameMenusToggle.isOn != S().capture_game_menus);
            P.Track(_captureGameClicksToggle.gameObject, () => _captureGameClicksToggle.isOn != S().capture_game_clicks);
            P.Track(_captureMouseAxesToggle.gameObject, () => _captureMouseAxesToggle.isOn != S().capture_mouse_axes);
            P.Track(_pauseGameToggle.gameObject, () => _pauseGameToggle.isOn != S().pause_game);
            P.Track(_opacityFocusedSlider.gameObject, () => !Mathf.Approximately(_opacityFocusedSlider.value, S().panel_opacity_focused));
            P.Track(_opacityUnfocusedSlider.gameObject, () => !Mathf.Approximately(_opacityUnfocusedSlider.value, S().panel_opacity_unfocused));

            // Proxy / Network
            P.Track(_proxyModeDropdown.Root, () => ProxyModeDisplayToConfig(_proxyModeDropdown.SelectedValue) != S().proxy_mode);
            P.Track(_proxyUrlInput.Component.gameObject, () => (_proxyUrlInput.Text ?? "").Trim() != S().proxy_url);
            P.Track(_proxyUserInput.Component.gameObject, () => (_proxyUserInput.Text ?? "") != S().proxy_username);
            P.Track(_proxyPassInput.Component.gameObject, () => (_proxyPassInput.Text ?? "") != S().proxy_password);
            P.Track(_proxyBypassLocalToggle.gameObject, () => _proxyBypassLocalToggle.isOn != S().proxy_bypass_local);
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

        /// <summary>
        /// The language a dropdown row stands for, or null when the row is not one.
        ///
        /// The rows are language NAMES, which is what the whole ecosystem keys on — so the row is
        /// its own answer. The exceptions are the "auto …" entries, which name a behaviour rather
        /// than a language; they are handed over unchanged and the mark comes back empty.
        /// </summary>
        private static string LanguageOfRow(string row)
        {
            if (string.IsNullOrEmpty(row)) return null;
            return row.StartsWith("auto", System.StringComparison.OrdinalIgnoreCase) ? null : row;
        }

    }
}
