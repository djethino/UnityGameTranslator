using System;
using System.Collections.Generic;
using System.Linq;
using UniverseLib.UI;
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
        private ToggleHandle _enableTranslationsToggle;
        private ToggleHandle _translateModUIToggle;
        private SearchableDropdown _interfaceFontDropdown; // mod UI font, shown only when translating the mod UI
        private Host _interfaceFontRow;                    // container toggled with the checkbox
        private SearchableDropdown _sourceLanguageDropdown;
        private SearchableDropdown _targetLanguageDropdown;
        private string[] _languages;
        private string[] _sourceLanguages;

        // Language section containers for conditional display
        private Host _languagesEditableSection;
        private Host _languagesLockedSection;
        private LabelHandle _lockedHeader;
        private LabelHandle _lockedSourceLangValue;
        private LabelHandle _lockedTargetLangValue;

        // Interface section
        private LabelHandle _resetWindowsStatusLabel;
        private ToggleHandle _disableEventSystemOverrideToggle;
        private ToggleHandle _captureKeyboardToggle;
        private ToggleHandle _captureKeyboardFocusOnlyToggle;
        private ToggleHandle _captureGameMenusToggle;
        private ToggleHandle _captureGameClicksToggle;
        private ToggleHandle _captureMouseAxesToggle;
        private ToggleHandle _pauseGameToggle;
        private SliderHandle _opacityFocusedSlider;
        private SliderHandle _opacityUnfocusedSlider;

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
        private ToggleHandle _captureKeysOnlyToggle;
        private ToggleHandle _debugLoggingToggle;
        private ToggleHandle _debugAiToggle;
        private Components.HelpZone _helpZone;
        private SearchableDropdown _backendTypeDropdown; // UIStyles.BackendTypeLLM / BackendTypeApi
        private static readonly string[] BackendTypeOptions = { UIStyles.BackendTypeLLM, UIStyles.BackendTypeApi };
        private ToggleHandle _enableTranslationBackendToggle;
        private Host _backendTypeSection;

        // LLM section
        private Host _llmSection;
        private FieldHandle _aiUrlInput;
        private FieldHandle _aiApiKeyInput;
        private SearchableDropdown _modelDropdown;
        private FieldHandle _gameContextInput;
        private ToggleHandle _strictSourceToggle;
        private LabelHandle _aiTestStatusLabel;

        /// <summary>
        /// What has to be said about the address in the URL field — empty for a server on this
        /// machine, which is the case this mod is built around.
        /// </summary>
        private LabelHandle _aiLocalityLabel;

        // Translation API section (contains provider dropdown + Google/DeepL sub-sections)
        private Host _translationApiSection;
        private SearchableDropdown _providerDropdown;
        private static readonly string[] ProviderOptions = { "Google Translate", "DeepL" };

        // Google section
        private Host _googleSection;
        private FieldHandle _googleApiKeyInput;
        private LabelHandle _googleTestStatusLabel;

        // DeepL section
        private Host _deeplSection;
        private FieldHandle _deeplApiKeyInput;
        private ToggleHandle _deeplUseFreeToggle;
        private LabelHandle _deeplTestStatusLabel;

        // Rate limit
        private FieldHandle _rateLimitDelayInput;

        // Advanced (AI): how many requests one line may cost, and how the model is asked.
        // Three jobs, three settings each, because they want opposite things — see the config.
        private FieldHandle _aiMaxAttemptsInput;
        private FieldHandle _aiTemperatureInput;
        private FieldHandle _aiTemperatureRepairInput;
        private FieldHandle _aiTemperatureRetranslateInput;
        private FieldHandle _aiSeedInput;
        private FieldHandle _aiSeedRepairInput;
        private FieldHandle _aiSeedRetranslateInput;

        // Proxy / Network section (in the Online tab)
        // Mode dropdown is shown to all users. Custom URL/user/pass + bypass toggle
        // are only visible when mode == "Custom" to avoid cluttering the regular case.
        private SearchableDropdown _proxyModeDropdown;
        private Host _proxyCustomSection;
        private FieldHandle _proxyUrlInput;
        private FieldHandle _proxyUserInput;
        private FieldHandle _proxyPassInput;
        private ToggleHandle _proxyBypassLocalToggle;
        private static readonly string[] ProxyModeDisplayOptions = { "Default", "System", "None / Direct", "Custom" };

        // Online section
        private ToggleHandle _onlineModeToggle;
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
        private ToggleHandle _realtimeOwnToggle;
        private ToggleHandle _notifyUpdatesToggle;
        private ToggleHandle _autoDownloadToggle;
        private ToggleHandle _notificationsEnabledToggle;
        private SearchableDropdown _notificationPositionDropdown;
        private ToggleHandle _checkModUpdatesToggle;
        private ToggleHandle _notifyPrereleasesToggle;
        private ButtonHandle _checkModUpdatesNowBtn;
        private LabelHandle _checkModUpdatesStatusLabel;

        // Shown only while the file settings differ from the online version
        private Host _settingsDriftRow;
        private LabelHandle _settingsDriftLabel;
        private ButtonHandle _restoreSettingsBtn;

        // Apply button tracking
        private ButtonHandle _applyBtn;
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
            Layout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            // Fixed header: tab buttons stay put, only tab content scrolls
            var header = FixedHeader();

            // No big title here — the window title bar already shows "Mod Options" (redundant).

            // Create tab bar — buttons in the fixed header, contents in the scroll area
            _tabBar = new TabBar();
            _tabBar.CreateUI(header, scrollContent);

            // Create tab contents. TabBar registers each tab button's own label itself
            // (idempotent RegisterUIText) — nothing left to do here for that.
            var generalTab = _tabBar.Tab("General");
            var hotkeysTab = _tabBar.Tab("Hotkeys");
            var adaptationTab = _tabBar.Tab("Adaptation");
            var translationTab = _tabBar.Tab("Translation");
            var onlineTab = _tabBar.Tab("Online");

            // Explain what lives behind each tab
            _helpZone?.Describe(_tabBar.Button("General"),
                "Language, mod UI translation, and general behavior");
            _helpZone?.Describe(_tabBar.Button("Hotkeys"),
                "Keyboard shortcuts for the mod's panels and tools");
            _helpZone?.Describe(_tabBar.Button("Adaptation"),
                "How the mod behaves alongside the game — set it for this game, and for whether you are playing or translating");
            _helpZone?.Describe(_tabBar.Button("Translation"),
                "How untranslated texts get translated: your AI, Google or DeepL");
            _helpZone?.Describe(_tabBar.Button("Online"),
                "Website sync, update notifications, and network settings");

            // Build each tab's content
            CreateGeneralTabContent(generalTab);
            CreateHotkeysTabContent(hotkeysTab);
            CreateAdaptationTabContent(adaptationTab);
            CreateTranslationTabContent(translationTab);
            CreateOnlineTabContent(onlineTab);

            // Tab height will be fixed on first display (see SetActive)

            // Buttons - in fixed footer (outside scroll)
            var cancelBtn = Buttons.Secondary(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.Clicked += () => SetActive(false);

            _applyBtn = Buttons.Primary(buttonRow, "ApplyBtn", "Apply", policy: TextPolicy.Excluded);
            _applyBtn.Clicked += OnApplyClicked;
            // EXCLUDE from translation: this button's text is code-managed and dynamic
            // ("Apply" / "Close" / "Apply (N)" via UpdateApplyButtonText). Async translation would
            // race with those updates and leave the button stuck / inconsistent with its state.

            // Setup change listeners for tracking pending changes
            SetupChangeListeners();
            RegisterPendingFields();
        }

        private void CreateGeneralTabContent(Host parent)
        {
            // stretchVertically: true = card expands to fill tab space, gray only as border
            var card = Stacks.Card(parent, "GeneralCard", PanelWidth - 60, stretchVertically: true);

            // Enable Translations toggle
            _enableTranslationsToggle = CheckBoxes.Create(card, "EnableTranslationsToggle", " Enable Translations");
            _helpZone?.Describe(_enableTranslationsToggle, "Turn the mod's translations on or off. When off, the game shows its original text.");

            Stacks.Spacer(card, 5);

            // Translate mod UI toggle
            _translateModUIToggle = CheckBoxes.Create(card, "TranslateModUIToggle", " Translate mod interface",
                tone: Tone.Secondary,
                onChanged: isOn =>
                {
                    if (_interfaceFontRow != null) _interfaceFontRow.Visible = isOn;
                    if (!_isLoadingSettings) UpdateApplyButtonText();
                });
            _helpZone?.Describe(_translateModUIToggle, "Translate this mod's own buttons and labels into your target language, alongside the game's text.");

            Labels.Create(card, "ModUIHint", "Translate this mod's own buttons and labels", TextRole.Hint);

            // Interface font — shown directly under the checkbox, only while translating the mod UI.
            // Lets the user pick a font that can render the target script (e.g. CJK) for the mod's own
            // interface. The picker appears immediately when the box is checked; the value applies on Apply.
            _interfaceFontRow = Stacks.Row(card, "InterfaceFontRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);
            Labels.Create(_interfaceFontRow, "InterfaceFontLabel", "Interface font:", TextRole.Info, minWidth: 90);

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
            var interfaceFontHost = _interfaceFontDropdown.CreateUI(_interfaceFontRow,
                (_) => { if (!_isLoadingSettings) UpdateApplyButtonText(); }, width: 260);
            _helpZone?.Describe(interfaceFontHost, "Font for this mod's interface when it is translated. Only fonts usable by the interface are listed; pick one that supports your target language's characters.");

            _interfaceFontRow.Visible = _translateModUIToggle.IsOn;

            Stacks.Spacer(card, 10);

            // === NOTIFICATION OVERLAY SECTION ===
            Labels.Create(card, "NotificationsLabel", "Notification Overlay", TextRole.SectionTitle);

            _notificationsEnabledToggle = CheckBoxes.Create(card, "NotifEnabledToggle", " Show notification overlay",
                tone: Tone.Secondary, onChanged: OnNotificationsEnabledChanged);
            _helpZone?.Describe(_notificationsEnabledToggle, "Show small corner messages for sync, updates, and translation activity. Turn off for clean screenshots.");

            var posRow = Stacks.Row(card, "NotifPosRow", spacing: 5, minHeight: UIStyles.RowHeightMedium);
            Labels.Create(posRow, "NotifPosLabel", "Position:", TextRole.Info, minWidth: 60);

            _notificationPositionDropdown = new SearchableDropdown(
                "NotifPosition",
                new[] { "Top-Right", "Top-Left", "Bottom-Right", "Bottom-Left" },
                "Top-Right",
                popupHeight: 150,
                showSearch: false
            );
            var posDropdownHost = _notificationPositionDropdown.CreateUI(posRow, (_) => { UpdateApplyButtonText(); }, width: 140,
                                                                        minHeight: UIStyles.InputHeight);
            _helpZone?.Describe(posDropdownHost, "Which screen corner the notification overlay appears in.");

            Stacks.Spacer(card, 10);

            // === ADVANCED SECTION ===
            Labels.Create(card, "AdvancedLabel", "Advanced", TextRole.SectionTitle);

            // Debug logging toggles — applied immediately (support: ask a user to tick these
            // to produce logs, no config.json editing needed). Config.debug drives the cached
            // DebugMode (SetRuntimeDebug syncs both); Config.debug_ai is read live.
            _debugLoggingToggle = CheckBoxes.Create(card, "DebugLoggingToggle", " Debug logging",
                tone: Tone.Secondary, onChanged: _ => UpdateApplyButtonText());
            _helpZone?.Describe(_debugLoggingToggle, "Write detailed logs to the mod log file. Turn on when reporting an issue, then share the log. Off by default.");

            _debugAiToggle = CheckBoxes.Create(card, "DebugAiToggle", " Debug AI translation",
                tone: Tone.Secondary, onChanged: _ => UpdateApplyButtonText());
            _helpZone?.Describe(_debugAiToggle, "Log every AI request and response (prompts, raw output, placeholder handling). Verbose — use only to diagnose translation quality.");

            Stacks.Spacer(card, 10);

            // === EDITABLE LANGUAGES SECTION ===
            _languagesEditableSection = Stacks.Vertical(card, "LanguagesEditableSection");

            Labels.Create(_languagesEditableSection, "LangLabel", "Languages", TextRole.SectionTitle);

            // Source Language
            Labels.Create(_languagesEditableSection, "SourceLangLabel", "Source Language:", TextRole.Info,
                minHeight: UIStyles.RowHeightSmall);

            var sourceLangHost = _sourceLanguageDropdown.CreateUI(_languagesEditableSection, OnSourceLanguageChanged, width: 200);
            _helpZone?.Describe(sourceLangHost, "The language the game's text is written in. Leave on Auto to detect it automatically.");

            Stacks.Spacer(_languagesEditableSection, 5);

            // Target Language
            Labels.Create(_languagesEditableSection, "TargetLangLabel", "Target Language:", TextRole.Info,
                minHeight: UIStyles.RowHeightSmall);

            var targetLangHost = _targetLanguageDropdown.CreateUI(_languagesEditableSection, width: 200);
            _helpZone?.Describe(targetLangHost, "The language you want the game translated into. Auto uses your system language.");

            // === LOCKED LANGUAGES SECTION ===
            _languagesLockedSection = Stacks.Vertical(card, "LanguagesLockedSection");

            // ⚠ Filled in UpdateLanguagesLocked: there are two reasons the languages are settled,
            // and this said only one of them. "Translation uploaded" on a file nobody has published
            // is simply false, and the reader is then left with a locked control and a wrong
            // explanation — worse than a locked control with none.
            _lockedHeader = Labels.Create(_languagesLockedSection, "LockedHeader", "", TextRole.Small,
                                          tone: Tone.Warning, policy: TextPolicy.Dynamic);

            var sourceRow = Stacks.Row(_languagesLockedSection, "SourceRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);
            Labels.Create(sourceRow, "SourceLabel", "Source:", TextRole.Info, minWidth: 60);

            _lockedSourceLangValue = Labels.Create(sourceRow, "SourceValue", "-", TextRole.Body,
                                                   policy: TextPolicy.Dynamic, fill: Fill.Stretch);

            var targetRow = Stacks.Row(_languagesLockedSection, "TargetRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);
            Labels.Create(targetRow, "TargetLabel", "Target:", TextRole.Info, minWidth: 60);

            _lockedTargetLangValue = Labels.Create(targetRow, "TargetValue", "-", TextRole.Body,
                                                   policy: TextPolicy.Dynamic, fill: Fill.Stretch);

            _languagesLockedSection.Visible = false;

            // === INTERFACE SECTION ===
            Stacks.Spacer(card, 15);

            Labels.Create(card, "InterfaceLabel", "Interface", TextRole.SectionTitle);

            // ── Window opacity ───────────────────────────────────────────────────────────────
            // Here, and not with the input options where it started: what it governs is the mod's
            // own windows — same subject as the reset below — and this is the tab it gets looked
            // for in. Its origin was that the title bar signals focus and this makes that signal
            // felt rather than read, but that is where it came FROM, not what it is ABOUT.
            Labels.Create(card, "OpacityLabel", "Window opacity", TextRole.SectionTitle);

            // ⚠ Floors at 40%: uGUI applies the alpha to the whole subtree, text included, so lower
            // is not translucent but unreadable — and somebody would blame the mod, not the slider.
            _opacityFocusedSlider = Sliders.Labelled(card, "OpacityFocused", "Focused:", 0.4f, 1f,
                TranslatorCore.PanelOpacityFocused, v => $"{v * 100f:0}%",
                _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); }, captionWidth: 70);
            _helpZone?.Describe(_opacityFocusedSlider,
                "How solid the window you are working in is. Lower it to see the game through the one you are using.");

            _opacityUnfocusedSlider = Sliders.Labelled(card, "OpacityUnfocused", "Others:", 0.4f, 1f,
                TranslatorCore.PanelOpacityUnfocused, v => $"{v * 100f:0}%",
                _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); }, captionWidth: 70);
            _helpZone?.Describe(_opacityUnfocusedSlider,
                "How solid the other windows are. Slightly faded by default, so a second window can stay open without hiding the game.");

            Stacks.Spacer(card, 10);

            var resetRow = Stacks.Row(card, "ResetRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);

            var resetBtn = Buttons.Secondary(resetRow, "ResetWindowsBtn", "Reset Window Positions", 160);
            resetBtn.Clicked += OnResetWindowPositionsClicked;
            _helpZone?.Describe(resetBtn, "Move all mod windows back to their default positions and sizes.");

            _resetWindowsStatusLabel = Labels.Create(resetRow, "ResetStatus", "", TextRole.Small,
                                                      policy: TextPolicy.Dynamic, fill: Fill.Stretch);

            // === HELP & FEEDBACK (single compact row) ===
            Stacks.Spacer(card, 15);

            Labels.Create(card, "HelpFeedbackLabel", "Help & Feedback", TextRole.SectionTitle);

            var helpRow = Stacks.Row(card, "HelpFeedbackRow", spacing: 5, minHeight: UIStyles.RowHeightMedium);

            var reportBugBtn = Buttons.Secondary(helpRow, "ReportBugBtn", "Report a Bug", 110);
            reportBugBtn.Clicked += () => TranslatorCore.OpenUrlSafe("https://github.com/djethino/UnityGameTranslator/issues");
            _helpZone?.Describe(reportBugBtn,
                "Something broken? Open a GitHub issue (a free GitHub account is required)");

            var discussionsBtn = Buttons.Secondary(helpRow, "DiscussionsBtn", "Discussions", 100);
            discussionsBtn.Clicked += () => TranslatorCore.OpenUrlSafe("https://github.com/djethino/UnityGameTranslator/discussions");
            _helpZone?.Describe(discussionsBtn,
                "Questions, ideas and feedback — talk with us and other players on GitHub");

            var docsBtn = Buttons.Secondary(helpRow, "OnlineDocsBtn", "Online Docs", 100);
            docsBtn.Clicked += () => TranslatorCore.OpenUrlSafe($"{ApiClient.WebsiteBaseUrl}/docs");
            _helpZone?.Describe(docsBtn,
                "The full user guide on the website (in your language)");
        }

        /// <summary>
        /// What the mod takes from the game while one of its windows is open.
        /// </summary>
        /// <remarks>
        /// Each box is an INTENTION. Whether it can be honoured is a property of the game, not of
        /// the wish, and only the runtime knows: one game is reached by patching its input calls,
        /// another by taking the Input System's devices, a third by neither. So the screen asks
        /// the runtime, per intention, and greys out what nobody can serve — with the reason it
        /// gives, never a sentence written here. A hardcoded list of what works would be wrong on
        /// some game and nobody would ever find out.
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
        private void CreateAdaptationTabContent(Host parent)
        {
            var card = Stacks.Card(parent, "InputCard", PanelWidth - 60, stretchVertically: true);

            Labels.Create(card, "CaptureLabel", "While a mod window is open", TextRole.SectionTitle);

            Labels.Create(card, "CaptureIntro",
                "Stop the game from reacting behind the window. Turn one off if it interferes with this game.",
                TextRole.Hint);

            Stacks.Spacer(card, 5);

            _captureKeyboardToggle = CreateCaptureToggle(card, "CaptureKeyboard", " Take the keyboard",
                "Keys go to this window only. Without it, typing a translation also walks, shoots or opens the game's menus. "
                + "Turn it off if the game stops answering the keyboard the way it should.",
                TranslatorCore.InputIntent.Keyboard);

            // Sub-option, indented under the keyboard one — and the reason its parent can be on by
            // default: the game keeps its keys until somebody actually types or navigates here.
            var focusRow = Stacks.Row(card, "KeyboardFocusRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);
            Stacks.Spacer(focusRow, 20);   // indent, so it reads as belonging to the box above
            _captureKeyboardFocusOnlyToggle = CheckBoxes.Create(focusRow, "CaptureKeyboardFocusOnly",
                " Only while the mod's interface has focus", tone: Tone.Secondary,
                onChanged: _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); });
            _helpZone?.Describe(_captureKeyboardFocusOnlyToggle,
                "The game keeps its keyboard until you type in a field or move through this interface with the keyboard. "
                + "Turn it off if what you type does not reach the mod in this game — the keyboard is then taken the whole time a window is open.");

            // ⚠ The two boxes are fixed by opposite moves, and saying so is the point: a plain
            // "turn off if it misbehaves" would leave someone in front of two boxes with no way to
            // tell which one. Parent off = the game gets its keyboard back. Child off = the mod
            // takes it more, not less.
            Labels.Create(card, "KeyboardFocusHint",
                "The game keeps its keys until you type or navigate here. If what you type never reaches the mod, turn this one off.",
                TextRole.Hint);

            Stacks.Spacer(card, 5);

            // ⚠ These two were ONE box, "Take mouse clicks", and it took away two unrelated
            // things at once: the game's menus answer a RAYCAST, its own clicks are a READ. Giving
            // the menus back therefore also gave the game every click, so clicking beside this
            // window fired a weapon. Separate boxes, separate reasons for greying out.
            _captureGameMenusToggle = CreateCaptureToggle(card, "CaptureGameMenus", " Take clicks from the game's menus",
                "The game's own buttons and menus stop answering the pointer. Clicks inside this window never reach them "
                + "either way — this is about the rest of the screen.",
                TranslatorCore.InputIntent.GameMenus);

            _captureGameClicksToggle = CreateCaptureToggle(card, "CaptureGameClicks", " Take clicks from the game itself",
                "The game stops reading clicks for what it does on its own — shooting, interacting, dragging. "
                + "Without it, clicking beside this window still acts in the game.",
                TranslatorCore.InputIntent.GameClicks);

            _captureMouseAxesToggle = CreateCaptureToggle(card, "CaptureMouseAxes", " Take mouse movement",
                "Stops the camera turning while you use the window. Mostly matters in first-person games.",
                TranslatorCore.InputIntent.MouseAxes);

            // Says what the split is FOR. Three boxes with three descriptions still leave the
            // useful combination to be guessed, and it is the one people actually want.
            Labels.Create(card, "MouseCaptureHint",
                "To hold the view still while the game's own menus keep working: take mouse movement, and leave the two above off.",
                TextRole.Hint);

            Stacks.Spacer(card, 15);

            // ── Freezing the game ────────────────────────────────────────────────────────────
            // Not a capture: the others stop the game RECEIVING, this stops it ADVANCING. Its own
            // section, off by default, and three separate lines — what it does, why it is off, and
            // what is dangerous. The last must not dissolve into the second: it is the only one
            // that can cost somebody their account.
            Labels.Create(card, "PauseLabel", "Freezing the game", TextRole.SectionTitle);

            _pauseGameToggle = CheckBoxes.Create(card, "PauseGameToggle", " Freeze the game while this window is open",
                onChanged: _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); });

            string antiCheat = GamePause.AntiCheat;
            bool pausePossible = string.IsNullOrEmpty(antiCheat);
            _pauseGameToggle.Enabled = pausePossible;

            if (pausePossible)
            {
                _helpZone?.Describe(_pauseGameToggle,
                    "The game stops on the current frame; hovering and picking still work.");

                Labels.Create(card, "PauseWhy",
                    "Off by default: what it does depends on the game. Some ignore it entirely, others cope badly with being frozen. Try it — nothing is changed permanently.",
                    TextRole.Hint);

                // ⚠ Never "online" for the GAME: the mod has its own Online mode and a player
                // would read this as a rule about that. "Multiplayer" and "the game's server"
                // can only mean the game.
                Labels.Create(card, "PauseDanger",
                    "Do not use this in a multiplayer game. The game's server does not stop: your character stays exposed and your session can desynchronise. Some anti-cheat systems also treat this as cheating.",
                    TextRole.Hint, tone: Tone.Warning);
            }
            else
            {
                string why = $"Unavailable: this game is protected by {antiCheat}, which can treat freezing it as cheating.";
                Labels.Create(card, "PauseBlocked", why, TextRole.Hint, policy: TextPolicy.Excluded);   // runtime diagnostic, not UI chrome
                _helpZone?.Describe(_pauseGameToggle, why);
            }

            Stacks.Spacer(card, 15);

            // Moved here from General → Advanced: it belongs with the other three, being the same
            // question asked the other way round — this one HANDS INPUT BACK to the game.
            Labels.Create(card, "InputAdvancedLabel", "Advanced", TextRole.SectionTitle);

            _disableEventSystemOverrideToggle = CheckBoxes.Create(card, "DisableEventSystemToggle",
                " Let the game handle its own interface input", tone: Tone.Secondary,
                onChanged: _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); });
            _helpZone?.Describe(_disableEventSystemOverrideToggle,
                "Stop the mod from taking the game's EventSystem. Turn on if the game's own menus stop reacting — losing their hover or their selection cursor — while a mod window is open.");

            Labels.Create(card, "EventSystemHint",
                "Turn on if the game's menus stop reacting while a mod window is open.", TextRole.Hint);
        }

        /// <summary>
        /// One capture box, greyed out with the runtime's own explanation when nothing can serve it.
        /// </summary>
        private ToggleHandle CreateCaptureToggle(Host card, string name, string label, string help,
            TranslatorCore.InputIntent intent)
        {
            var toggle = CheckBoxes.Create(card, name, label,
                onChanged: _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); });

            bool possible = TranslatorCore.CanCaptureInput(intent);
            toggle.Enabled = possible;

            if (possible)
            {
                _helpZone?.Describe(toggle, help);
                return toggle;
            }

            // Say why, in place — a box that is simply grey reads as a bug, or as a setting the
            // player broke themselves. The sentence comes from whichever strategy would have
            // served this, so it names the actual obstacle on THIS game.
            string why = TranslatorCore.WhyInputCaptureUnavailable(intent);
            Labels.Create(card, name + "Why", why, TextRole.Hint, policy: TextPolicy.Excluded);   // runtime diagnostic text, not UI chrome to translate
            _helpZone?.Describe(toggle, why);
            return toggle;
        }

        private void CreateHotkeysTabContent(Host parent)
        {
            var card = Stacks.Card(parent, "HotkeysCard", PanelWidth - 60, stretchVertically: true);

            Labels.Create(card, "SettingsHotkeyLabel", "Settings Panel", TextRole.SectionTitle);

            Labels.Create(card, "HotkeyHint", "Press the key combination to open/close the settings panel", TextRole.Hint);

            Stacks.Spacer(card, 5);

            _hotkeyCapture.CreateUI(card);
            _helpZone?.Describe(_hotkeyCapture.Handle, "The keyboard shortcut that opens and closes this settings panel.");

            Stacks.Spacer(card, 15);

            // Additional hotkeys (all disabled by default — click X to clear)
            Labels.Create(card, "ExtraHotkeysLabel", "Additional Hotkeys", TextRole.SectionTitle);

            Labels.Create(card, "ExtraHotkeysHint", "Optional shortcuts. All disabled by default to avoid conflicts with game controls. Click X to clear a hotkey.", TextRole.Hint);

            Stacks.Spacer(card, 5);

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

            Stacks.Spacer(card, 10);

            // --- Quick access (open/close panels) ---
            CreateHotkeyRow(card, "Toggle Inspector", "Open/close the element inspector panel", _hotkeyOpenInspector,
                "Shortcut to open or close the element inspector panel.");
            CreateHotkeyRow(card, "Toggle Upload", "Open/close the translation upload panel", _hotkeyOpenUpload,
                "Shortcut to open or close the translation upload panel.");
            CreateHotkeyRow(card, "Toggle Exclusion mode", "Open/close the inspector in exclusion mode", _hotkeyOpenExclusion,
                "Shortcut to open or close the inspector in exclusion mode.");
            CreateHotkeyRow(card, "Toggle Text editor", "Open/close the in-game text editor (click UI text to edit)", _hotkeyOpenTextEditor,
                "Shortcut to open or close the in-game text editor, where you click UI text to edit it.");

            Stacks.Spacer(card, 10);

            // --- Utilities ---
            CreateHotkeyRow(card, "Force scene rescan", "Re-scan the current scene (useful after scene glitches)", _hotkeyForceScan,
                "Shortcut to re-scan the current scene, useful after scene glitches.");
        }

        /// <summary>
        /// Creates one row per hotkey: label + hint + HotkeyCapture component.
        /// </summary>
        private void CreateHotkeyRow(Host parent, string label, string hint, HotkeyCapture capture, string helpText = null)
        {
            var row = Stacks.Vertical(parent, $"HotkeyRow_{label}", spacing: 2);

            var labelHandle = Labels.Create(row, "RowLabel", label, TextRole.Body, minHeight: UIStyles.RowHeightSmall);
            labelHandle.Bold = true;

            Labels.Create(row, "RowHint", hint, TextRole.Hint);

            capture.CreateUI(row, includeDisplayLabel: false);
            if (!string.IsNullOrEmpty(helpText)) _helpZone?.Describe(capture.Handle, helpText);

            Stacks.Spacer(parent, 6);
        }

        private void CreateTranslationTabContent(Host parent)
        {
            var card = Stacks.Card(parent, "TranslationCard", PanelWidth - 60, stretchVertically: true);

            // Capture keys only section
            Labels.Create(card, "CaptureLabel", "Manual Mode", TextRole.SectionTitle);

            _captureKeysOnlyToggle = CheckBoxes.Create(card, "CaptureKeysToggle", " Collect texts without translating them",
                tone: Tone.Secondary, onChanged: OnCaptureKeysOnlyChanged);
            _helpZone?.Describe(_captureKeysOnlyToggle, "Record every text the game shows into your translation file as empty entries, to translate later. No automatic translation happens.");

            Labels.Create(card, "CaptureHint", "Every text the game shows is added to your translation file as an empty entry, so you can translate it later (in-game editor or browser)", TextRole.Hint);

            Stacks.Spacer(card, 15);

            // === AUTO-TRANSLATION ===
            Labels.Create(card, "BackendLabel", "Auto-Translation", TextRole.SectionTitle);

            // Enable toggle
            _enableTranslationBackendToggle = CheckBoxes.Create(card, "EnableTransBackendToggle", " Enable auto-translation",
                onChanged: OnEnableTranslationBackendChanged);
            _helpZone?.Describe(_enableTranslationBackendToggle, "Automatically translate untranslated texts using the backend below (your AI, Google or DeepL). Turning this off pauses translation and keeps everything below as it is, so you can set it up first and start when you are ready.");

            // Backend type section (stays visible when auto-translation is off — see
            // UpdateBackendSections: configuring is what one does before starting)
            _backendTypeSection = Stacks.Vertical(card, "BackendTypeSection", spacing: 5);

            // Backend type dropdown: LLM (AI) / Translation API
            var typeRow = Stacks.Row(_backendTypeSection, "TypeRow", spacing: 5, minHeight: UIStyles.RowHeightMedium);
            Labels.Create(typeRow, "TypeLabel", "Type:", TextRole.Info, minWidth: 40);

            _backendTypeDropdown = new SearchableDropdown(
                "BackendTypeDropdown", BackendTypeOptions, UIStyles.BackendTypeLLM, popupHeight: 100, showSearch: false);
            var typeHost = _backendTypeDropdown.CreateUI(typeRow, OnBackendTypeChanged, width: 160,
                                                         minHeight: UIStyles.InputHeight);
            _helpZone?.Describe(typeHost,
                "AI: your own model (Ollama, LM Studio, ChatGPT...) with full context. Google / DeepL: classic translation services, needs an API key.");

            Stacks.Spacer(_backendTypeSection, 5);

            // === LLM SECTION ===
            _llmSection = Stacks.Vertical(_backendTypeSection, "LLMSection", spacing: 3);

            // URL row
            var urlRow = Stacks.Row(_llmSection, "UrlRow", spacing: 5, minHeight: UIStyles.InputHeight);
            Labels.Create(urlRow, "UrlLabel", "URL:", TextRole.Info, minWidth: 45, policy: TextPolicy.Excluded);

            _aiUrlInput = Fields.Create(urlRow, "AIUrl", Endpoints.OllamaDefault);
            _helpZone?.Describe(_aiUrlInput, "Address of your AI server, for example a local Ollama or LM Studio. Default is " + Endpoints.OllamaDefault + ".");

            var testBtn = Buttons.Secondary(urlRow, "TestBtn", "Test", 60);
            testBtn.Clicked += TestAIConnection;
            _helpZone?.Describe(testBtn, "Check that the mod can reach the AI server at the URL above.");

            _aiTestStatusLabel = Labels.Create(_llmSection, "TestStatus", "", TextRole.Small, policy: TextPolicy.Excluded);

            // What sending this game's text to that address actually means. Nothing at all for a
            // server on this machine, which is the ordinary case and the one this mod is built
            // around; privacy for a box on the home network; privacy and a bill for anything else.
            //
            // ⚠ The wording comes from the shared library, not from here. It is a statement about
            // somebody's money and somebody's data, the manager makes it too, and two copies would
            // drift — with the under-warning copy landing in front of whoever needed it most.
            _aiLocalityLabel = Labels.Create(_llmSection, "Locality", "", TextRole.Small, tone: Tone.Warning,
                policy: TextPolicy.Excluded, align: Placement.TopLeft, fill: Fill.Stretch, autoHeight: true);

            // Follows what is being typed, not what was last applied: somebody pasting a provider's
            // address has to read this before they press Apply, not after.
            _aiUrlInput.Changed += _ => RefreshAiLocality();

            // API Key row
            var keyRow = Stacks.Row(_llmSection, "KeyRow", spacing: 5, minHeight: UIStyles.InputHeight);
            Labels.Create(keyRow, "KeyLabel", "API Key:", TextRole.Info, minWidth: 55, policy: TextPolicy.Excluded);

            _aiApiKeyInput = Fields.Create(keyRow, "AIApiKey", "", FieldKind.Password);
            _helpZone?.Describe(_aiApiKeyInput, "API key for your AI server, if it needs one. Leave empty for most local servers.");

            Labels.Create(_llmSection, "KeyHint", "Optional for local servers (Ollama, LM Studio)", TextRole.Hint);

            // Model row
            var modelRow = Stacks.Row(_llmSection, "ModelRow", spacing: 5, minHeight: UIStyles.InputHeight);
            Labels.Create(modelRow, "ModelLabel", "Model:", TextRole.Info, minWidth: 50);

            _modelDropdown = new SearchableDropdown("ModelDropdown", new string[0], null, 200, false);
            var modelHost = _modelDropdown.CreateUI(modelRow, (val) => { }, width: 200, stretch: true);
            _helpZone?.Describe(modelHost, "Which AI model handles the translations. Use Refresh to load the list from your server.");

            var refreshBtn = Buttons.Secondary(modelRow, "RefreshBtn", "Refresh", 60);
            refreshBtn.Clicked += RefreshModels;
            _helpZone?.Describe(refreshBtn, "Load the list of available models from your AI server.");

            Labels.Create(_llmSection, "ModelHint", "Select a model from your server", TextRole.Hint);

            Stacks.Spacer(_llmSection, 5);

            // Game context
            Labels.Create(_llmSection, "ContextLabel", "Game Context (optional):", TextRole.Info,
                minHeight: UIStyles.RowHeightSmall);

            _gameContextInput = Fields.Create(_llmSection, "ContextInput", "e.g., RPG game with medieval setting", FieldKind.Multiline);
            _helpZone?.Describe(_gameContextInput, "Optional note about the game (genre, setting, tone) to help the AI pick better wording.");

            Labels.Create(_llmSection, "ContextHint", "Helps the AI understand game vocabulary", TextRole.Hint);

            Stacks.Spacer(_llmSection, 5);

            // Strict source language toggle
            _strictSourceToggle = CheckBoxes.Create(_llmSection, "StrictSourceToggle", " Strict source language detection",
                tone: Tone.Secondary);
            _helpZone?.Describe(_strictSourceToggle, "Skip texts that are not in the source language, so foreign or already-translated text is left alone. AI backend only.");

            Labels.Create(_llmSection, "StrictHint", "Skip texts not matching source language (LLM only)", TextRole.Hint);

            CreateAiAdvancedSection(_llmSection);

            // === TRANSLATION API SECTION (contains provider dropdown + sub-sections) ===
            _translationApiSection = Stacks.Vertical(_backendTypeSection, "TranslationApiSection", spacing: 3);

            // Provider dropdown
            var providerRow = Stacks.Row(_translationApiSection, "ProviderRow", spacing: 5, minHeight: UIStyles.RowHeightMedium);
            Labels.Create(providerRow, "ProviderLabel", "Provider:", TextRole.Info, minWidth: 55);

            _providerDropdown = new SearchableDropdown(
                "ProviderDropdown", ProviderOptions, "Google Translate", popupHeight: 100, showSearch: false);
            var providerHost = _providerDropdown.CreateUI(providerRow, OnProviderChanged, width: 160,
                                                          minHeight: UIStyles.InputHeight);
            _helpZone?.Describe(providerHost, "Choose the translation service: Google Translate or DeepL. Each needs its own API key.");

            Stacks.Spacer(_translationApiSection, 5);

            // === GOOGLE SECTION ===
            _googleSection = Stacks.Vertical(_translationApiSection, "GoogleSection", spacing: 3);

            var googleKeyRow = Stacks.Row(_googleSection, "GoogleKeyRow", spacing: 5, minHeight: UIStyles.InputHeight);
            Labels.Create(googleKeyRow, "GoogleKeyLabel", "API Key:", TextRole.Info, minWidth: 55, policy: TextPolicy.Excluded);

            _googleApiKeyInput = Fields.Create(googleKeyRow, "GoogleApiKey", "", FieldKind.Password);
            _helpZone?.Describe(_googleApiKeyInput, "Your Google Cloud API key with the Translation API enabled.");

            var googleTestBtn = Buttons.Secondary(googleKeyRow, "GoogleTestBtn", "Test", 60);
            googleTestBtn.Clicked += TestGoogleConnection;
            _helpZone?.Describe(googleTestBtn, "Send a test request to check that your Google API key works.");

            _googleTestStatusLabel = Labels.Create(_googleSection, "GoogleTestStatus", "", TextRole.Small, policy: TextPolicy.Dynamic);

            Labels.Create(_googleSection, "GoogleHint", "Requires a Google Cloud API key with Translation API enabled", TextRole.Hint);

            // === DEEPL SECTION ===
            _deeplSection = Stacks.Vertical(_translationApiSection, "DeepLSection", spacing: 3);

            var deeplKeyRow = Stacks.Row(_deeplSection, "DeepLKeyRow", spacing: 5, minHeight: UIStyles.InputHeight);
            Labels.Create(deeplKeyRow, "DeepLKeyLabel", "API Key:", TextRole.Info, minWidth: 55, policy: TextPolicy.Excluded);

            _deeplApiKeyInput = Fields.Create(deeplKeyRow, "DeepLApiKey", "", FieldKind.Password);
            _helpZone?.Describe(_deeplApiKeyInput, "Your DeepL API key (Free or Pro).");

            var deeplTestBtn = Buttons.Secondary(deeplKeyRow, "DeepLTestBtn", "Test", 60);
            deeplTestBtn.Clicked += TestDeepLConnection;
            _helpZone?.Describe(deeplTestBtn, "Send a test request to check that your DeepL API key works.");

            _deeplTestStatusLabel = Labels.Create(_deeplSection, "DeepLTestStatus", "", TextRole.Small, policy: TextPolicy.Dynamic);

            _deeplUseFreeToggle = CheckBoxes.Create(_deeplSection, "DeepLFreeToggle", " Use Free API (api-free.deepl.com)",
                tone: Tone.Secondary);
            _helpZone?.Describe(_deeplUseFreeToggle, "Use the DeepL Free endpoint. Turn off if you have a DeepL Pro key.");

            Labels.Create(_deeplSection, "DeepLHint", "Uncheck for Pro API (api.deepl.com). Free plan: 500k chars/month", TextRole.Hint);

            // Rate limit retry delay (shared across all backends)
            Stacks.Spacer(_backendTypeSection, 10);
            var rateLimitRow = Stacks.Row(_backendTypeSection, "RateLimitRow", spacing: 5, minHeight: UIStyles.InputHeight);
            Labels.Create(rateLimitRow, "RateLimitLabel", "Rate limit retry:", TextRole.Info, minWidth: 110);

            _rateLimitDelayInput = Fields.Create(rateLimitRow, "RateLimitDelay", "3", FieldKind.Decimal,
                minWidth: 50, fill: Fill.Content);
            _helpZone?.Describe(_rateLimitDelayInput, "How long to wait before retrying when the translation service asks the mod to slow down.");

            Labels.Create(rateLimitRow, "RateLimitUnit", "seconds", TextRole.Info, tone: Tone.Muted, fill: Fill.Stretch);

            Labels.Create(_backendTypeSection, "RateLimitHint", "How long to wait before retrying when the translation service asks to slow down", TextRole.Hint);

            // Initial visibility - all hidden until UpdateBackendSections
            _backendTypeSection.Visible = false;
            _llmSection.Visible = false;
            _translationApiSection.Visible = false;
            _googleSection.Visible = false;
            _deeplSection.Visible = false;
        }

        /// <summary>
        /// What the model is asked, and how many times — folded away by default.
        ///
        /// ⚠ Collapsed on purpose, and not out of shyness: a temperature is a real translation
        /// change, and every one of these has a default that is right for nearly everybody. What is
        /// behind this header is for someone who already knows what a seed does; putting it in
        /// front of everyone else would make them think a choice was expected of them.
        /// </summary>
        private void CreateAiAdvancedSection(Host parent)
        {
            Stacks.Spacer(parent, 8);

            var advanced = Collapsible.Create(parent, "AiAdvanced", "Advanced", expanded: false,
                // The window measures its content to size itself; a section that just unfolded
                // is content it has never measured.
                onToggled: _ => RecalculateSize());
            var content = advanced.Body;

            Labels.Create(content, "AttemptsHint",
                "How many requests one line may cost at most — used both to repair a broken placeholder and to retranslate a line you did not like",
                TextRole.Hint);

            _aiMaxAttemptsInput = CreateAdvancedNumberRow(content, "MaxAttempts", "Attempts:", "3",
                "Each attempt is a real request to your AI. 1 means never ask twice. Default 3.");

            Stacks.Spacer(content, 8);

            Labels.Create(content, "TempHint",
                "Temperature: 0 always gives the same answer for the same line, higher wanders further from it",
                TextRole.Hint);

            _aiTemperatureInput = CreateAdvancedNumberRow(content, "Temp", "Translating:", "0",
                "Ordinary translation. Zero by default so the same line always gets the same translation — the file is cached, shared and merged with other people's.");
            _aiTemperatureRepairInput = CreateAdvancedNumberRow(content, "TempRepair", "Repairing:", "0.3",
                "Used when the answer broke a [!v*0]-style marker and has to be asked again. Just above zero: the same request would return the same broken answer.");
            _aiTemperatureRetranslateInput = CreateAdvancedNumberRow(content, "TempRetrans", "Retranslating:", "0.8",
                "Used by the Retranslate button, when you did not like the translation. High on purpose: same instructions, different wording.");

            Stacks.Spacer(content, 8);

            Labels.Create(content, "SeedHint",
                "Seed: leave empty unless you want the same run twice. Many servers accept it and ignore it",
                TextRole.Hint);

            _aiSeedInput = CreateAdvancedNumberRow(content, "Seed", "Translating:", "empty",
                "Fixed seed for ordinary translation. Empty sends none.");
            _aiSeedRepairInput = CreateAdvancedNumberRow(content, "SeedRepair", "Repairing:", "empty",
                "Fixed seed when re-asking after a broken marker. Empty sends none.");
            _aiSeedRetranslateInput = CreateAdvancedNumberRow(content, "SeedRetrans", "Retranslating:", "empty",
                "Fixed seed for the Retranslate button. It is offset by the attempt number, so retranslating still varies — a single fixed seed would hand back the answer you just rejected, every time. Empty draws a new one each attempt.");
        }

        /// <summary>One labelled number field of the Advanced block.</summary>
        private FieldHandle CreateAdvancedNumberRow(Host parent, string name, string label,
            string placeholder, string help)
        {
            // Deliberately FieldKind.Text, not Decimal: ContentType.DecimalNumber is locale-aware,
            // so on a machine whose decimal separator is a comma the field refuses the dot these
            // values are written with. Validation happens at Apply, where a bad entry falls back to
            // the default instead of being silently eaten (see TemperatureFromText).
            var field = Fields.Captioned(parent, name, label, placeholder, FieldKind.Text,
                captionWidth: 110, fieldMinWidth: 70, fieldFill: Fill.Content);
            _helpZone?.Describe(field, help);
            return field;
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

        private void CreateOnlineTabContent(Host parent)
        {
            var card = Stacks.Card(parent, "OnlineCard", PanelWidth - 60, stretchVertically: true);

            _onlineModeToggle = CheckBoxes.Create(card, "OnlineModeToggle", " Enable Online Mode",
                onChanged: OnOnlineModeChanged);
            _helpZone?.Describe(_onlineModeToggle,
                "On: the mod contacts our website to find community translations and updates for your games. Off: fully offline, nothing leaves your machine.");

            Stacks.Spacer(card, 10);

            // Translation sync section
            Labels.Create(card, "SyncLabel", "Translation Sync", TextRole.SectionTitle);

            // 🔴 **Before the rhythm, because it says what does not follow it.** The two used to be
            // one list, so choosing "real-time" also put other people's work on that connection: a
            // Main was woken by every contribution anybody sent, and somebody publishing every ten
            // minutes woke each of their contributors just as often. One question each now — what
            // is mine can be immediate, what is other people's has a pace.
            _realtimeOwnToggle = CheckBoxes.Create(card, "RealtimeOwnToggle", " Real-time check for your own translation",
                tone: Tone.Secondary, onChanged: _ => { if (!_isLoadingSettings) UpdateApplyButtonText(); });
            _helpZone?.Describe(_realtimeOwnToggle,
                "Keeps a connection open so that what you publish from the website, or from another "
                + "computer, comes back to the game as it happens. Only ever about your own line: "
                + "contributions you receive and the original you contribute to follow the rhythm "
                + "below. Nothing is opened when you have published nothing of your own.");

            Labels.Create(card, "RealtimeOwnHint",
                "Changes you publish from the website or another machine come back straight away, "
                + "rather than waiting for the next check.", TextRole.Hint);

            var freqRow = Stacks.Row(card, "CheckFreqRow", spacing: 5, minHeight: UIStyles.RowHeightMedium);
            // Not just "Check for updates": the word alone left people guessing what
            // was being checked, and for which role
            Labels.Create(freqRow, "CheckFreqLabel", "Ask the website every:", TextRole.Info, minWidth: 130);

            _checkFrequencyDropdown = new SearchableDropdown(
                "CheckFrequency",
                UpdateFrequencyDisplayOptions,
                FrequencyConfigToDisplay(UpdateCheckFrequency.Hourly),
                popupHeight: 150,
                showSearch: false
            );
            var freqHost = _checkFrequencyDropdown.CreateUI(freqRow, (_) => { UpdateApplyButtonText(); }, width: 200,
                                                            minHeight: UIStyles.InputHeight);
            _helpZone?.Describe(freqHost,
                "How often the mod asks the website what changed: contributions waiting for your "
                + "review if you own a translation, the original translation if you contribute to "
                + "someone else's, and a newer version of the translation you use. Your own line is "
                + "in here too, unless Real-time check is on. Editing in the browser is a separate, "
                + "instant channel and is never affected by this setting.");

            Labels.Create(card, "CheckFreqHint",
                "Contributions you received, a Main that moved, a newer version published — and "
                + "your own translation when Real-time check is off.", TextRole.Hint);

            Labels.Create(card, "CheckFreqStartupHint",
                "Every option except Never also checks once when the game starts.", TextRole.Hint);

            _notifyUpdatesToggle = CheckBoxes.Create(card, "NotifyToggle", " Notify when translation updates available",
                tone: Tone.Secondary);
            _helpZone?.Describe(_notifyUpdatesToggle, "Show a notification when a newer version of a translation is available to download.");

            _autoDownloadToggle = CheckBoxes.Create(card, "AutoDownloadToggle", " Auto-download translation updates (no conflicts)",
                tone: Tone.Secondary);
            _helpZone?.Describe(_autoDownloadToggle,
                "Only applies when you have no local changes — otherwise the mod always asks first");

            // The way back from a declined replacement — or from local tinkering. Hidden unless
            // the settings actually differ from the online version, so it never suggests undoing
            // something that was not done. Filled by RefreshSettingsDriftRow.
            _settingsDriftRow = Stacks.Row(card, "SettingsDriftRow", spacing: 5, minHeight: UIStyles.RowHeightMedium);

            _settingsDriftLabel = Labels.Create(_settingsDriftRow, "SettingsDriftLabel", "", TextRole.Small,
                                                tone: Tone.Secondary, policy: TextPolicy.Excluded, fill: Fill.Stretch);

            _restoreSettingsBtn = Buttons.Secondary(_settingsDriftRow, "RestoreSettingsBtn", "Review…", 100);
            _restoreSettingsBtn.Clicked += OnRestoreSettingsClicked;
            _helpZone?.Describe(_restoreSettingsBtn,
                "Compare your fonts, exclusions and other file settings with the online version, and choose section by section which ones to take back. Nothing changes until you press Apply.");

            _settingsDriftRow.Visible = false;

            Stacks.Spacer(card, 10);

            // Mod updates section
            Labels.Create(card, "ModUpdatesLabel", "Mod Updates", TextRole.SectionTitle);

            var modUpdatesRow = Stacks.Row(card, "ModUpdatesRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);

            _checkModUpdatesToggle = CheckBoxes.Create(modUpdatesRow, "ModUpdatesToggle", " Check on startup",
                tone: Tone.Secondary, fill: Fill.Stretch);
            _helpZone?.Describe(_checkModUpdatesToggle, "Check for a new version of the mod itself when the game starts.");

            _checkModUpdatesNowBtn = Buttons.Secondary(modUpdatesRow, "CheckNowBtn", "Check Now", 90);
            _checkModUpdatesNowBtn.Clicked += OnCheckModUpdatesNowClicked;
            _helpZone?.Describe(_checkModUpdatesNowBtn, "Check for a new mod version right now.");

            _notifyPrereleasesToggle = CheckBoxes.Create(card, "PrereleaseToggle", " Also notify about beta releases",
                tone: Tone.Secondary);
            _helpZone?.Describe(_notifyPrereleasesToggle, "Also get notified about beta (pre-release) mod versions, not just stable ones.");

            Labels.Create(card, "PrereleaseHint",
                "Betas are early builds for testing new features. Leave off to only hear about stable releases.", TextRole.Hint);

            _checkModUpdatesStatusLabel = Labels.Create(card, "ModUpdateStatus", "", TextRole.Small, policy: TextPolicy.Dynamic);

            // === Proxy / Network ===
            // Most users keep "Default". Use "None" to bypass a process-level HTTP
            // proxy injected by the game (DRM / EOS / anti-cheat) when the mod's
            // network calls hang. "System" forces a fresh Windows proxy. "Custom"
            // routes through a user-defined URL with optional credentials.
            Stacks.Spacer(card, 10);

            Labels.Create(card, "ProxyLabel", "Network / Proxy", TextRole.SectionTitle);

            Labels.Create(card, "ProxyIntro",
                "Use only if the mod's network calls hang (game intercepts HTTP). Keep Default otherwise.", TextRole.Hint);

            // Mode dropdown
            var proxyModeRow = Stacks.Row(card, "ProxyModeRow", spacing: 5, minHeight: UIStyles.InputHeight);
            Labels.Create(proxyModeRow, "ProxyModeLabel", "Mode:", TextRole.Info, minWidth: 80);

            _proxyModeDropdown = new SearchableDropdown(
                "ProxyModeDropdown", ProxyModeDisplayOptions, ProxyModeDisplayOptions[0], popupHeight: 150, showSearch: false);
            var proxyModeHost = _proxyModeDropdown.CreateUI(proxyModeRow, OnProxyModeChanged, width: 200, stretch: true);
            _helpZone?.Describe(proxyModeHost, "How the mod connects to the internet. Keep Default unless the game blocks the mod's network calls.");

            // Custom-only section (toggled visible by OnProxyModeChanged)
            _proxyCustomSection = Stacks.Vertical(card, "ProxyCustomSection", spacing: 3);

            // Custom URL
            var proxyUrlRow = Stacks.Row(_proxyCustomSection, "ProxyUrlRow", spacing: 5, minHeight: UIStyles.InputHeight);
            Labels.Create(proxyUrlRow, "ProxyUrlLabel", "URL:", TextRole.Info, minWidth: 80);

            _proxyUrlInput = Fields.Create(proxyUrlRow, "ProxyUrl", "http://proxy.example.com:8080");
            _helpZone?.Describe(_proxyUrlInput, "Address of your proxy server, used only in Custom mode.");

            // Username
            var proxyUserRow = Stacks.Row(_proxyCustomSection, "ProxyUserRow", spacing: 5, minHeight: UIStyles.InputHeight);
            Labels.Create(proxyUserRow, "ProxyUserLabel", "Username:", TextRole.Info, minWidth: 80);

            _proxyUserInput = Fields.Create(proxyUserRow, "ProxyUser", "(optional)");
            _helpZone?.Describe(_proxyUserInput, "Proxy username, if your proxy requires sign-in. Optional.");

            // Password
            var proxyPassRow = Stacks.Row(_proxyCustomSection, "ProxyPassRow", spacing: 5, minHeight: UIStyles.InputHeight);
            Labels.Create(proxyPassRow, "ProxyPassLabel", "Password:", TextRole.Info, minWidth: 80);

            _proxyPassInput = Fields.Create(proxyPassRow, "ProxyPass", "(optional)", FieldKind.Password);
            _helpZone?.Describe(_proxyPassInput, "Proxy password, if your proxy requires sign-in. Optional.");

            // Bypass local
            _proxyBypassLocalToggle = CheckBoxes.Create(_proxyCustomSection, "ProxyBypassToggle",
                " Bypass proxy for localhost / private addresses", tone: Tone.Secondary);
            _helpZone?.Describe(_proxyBypassLocalToggle, "Connect directly to local and private addresses instead of through the proxy.");

            // Hidden by default; OnProxyModeChanged toggles it when the user picks "Custom".
            _proxyCustomSection.Visible = false;
        }

        private void OnProxyModeChanged(string newDisplay)
        {
            if (_proxyCustomSection != null)
                _proxyCustomSection.Visible = newDisplay == "Custom";
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

        /// <summary>
        /// **Strict source language** may only be ticked when the source is stated (not auto), the
        /// backend is the AI one, translation is switched on, and manual capture is not active —
        /// one formula, called from every trigger that can move one of those four conditions.
        ///
        /// 🔴 Two callers used to carry two different, incomplete copies of this rule
        /// (OnSourceLanguageChanged had the full four; UpdateBackendSections tested only the
        /// source), and two more triggers — the auto-translation toggle and the manual-capture
        /// toggle — moved a condition without refreshing either copy. So switching one of those
        /// off could leave the checkbox interactable when it should not be, or the reverse.
        /// Refreshed from all four triggers now, from this one formula.
        /// </summary>
        private void RefreshStrictSourceState()
        {
            if (_strictSourceToggle == null || _sourceLanguageDropdown == null) return;

            bool isAuto = _sourceLanguageDropdown.SelectedValue == "auto (Detect)";
            bool isLLM = GetSelectedBackendConfig() == "llm";
            bool backendOn = _enableTranslationBackendToggle != null && _enableTranslationBackendToggle.IsOn;
            bool captureOnly = _captureKeysOnlyToggle != null && _captureKeysOnlyToggle.IsOn;

            _strictSourceToggle.Enabled = !isAuto && isLLM && backendOn && !captureOnly;

            if (isAuto && _strictSourceToggle.IsOn)
                _strictSourceToggle.IsOn = false;
        }

        private void OnSourceLanguageChanged(string newSource)
        {
            RefreshStrictSourceState();
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
            _settingsDriftRow.Visible = drifted;

            if (!drifted) return;

            int count = reference.DifferingSections.Count;
            string what = string.Join(", ", reference.DifferingSections
                .Select(SettingsSections.Name).ToArray());

            // Names the sections rather than counting them: "2 sections differ" tells nobody
            // whether their fonts or their exclusions are the ones that moved.
            _settingsDriftLabel.Say(
                Tr(count == 1
                    ? $"Your settings differ from {reference.Label}:"
                    : $"Your settings differ from {reference.Label} in {count} sections:")
                + " " + what);
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
            _enableTranslationsToggle.IsOn = TranslatorCore.Config.enable_translations;
            // Tri-state: show what is IN EFFECT (the user's choice, or the translation's when they
            // never made one). Ticking the box then records an explicit choice.
            _translateModUIToggle.IsOn = TranslatorCore.ShouldTranslateOwnUI;

            // Interface font: sync the picker visibility with the checkbox on (re)load.
            if (_interfaceFontRow != null)
                _interfaceFontRow.Visible = _translateModUIToggle.IsOn;

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
            _onlineModeToggle.IsOn = TranslatorCore.Config.online_mode;
            _checkFrequencyDropdown.SelectedValue = FrequencyConfigToDisplay(TranslatorCore.Config.sync.update_check_frequency);
            _realtimeOwnToggle.IsOn = TranslatorCore.Config.sync.realtime_own_translation;
            _notifyUpdatesToggle.IsOn = TranslatorCore.Config.sync.notify_updates;
            _autoDownloadToggle.IsOn = TranslatorCore.Config.sync.auto_download;
            _checkModUpdatesToggle.IsOn = TranslatorCore.Config.sync.check_mod_updates;
            _notifyPrereleasesToggle.IsOn = TranslatorCore.Config.sync.notify_prereleases;
            _notificationsEnabledToggle.IsOn = TranslatorCore.Config.sync.notifications_enabled;
            _notificationPositionDropdown.SelectedValue = PositionConfigToDisplay(TranslatorCore.Config.sync.notification_position);
            OnOnlineModeChanged(_onlineModeToggle.IsOn);

            // Proxy / Network (independent of online mode -- affects every HTTP call)
            _proxyModeDropdown.SelectedValue = ProxyModeConfigToDisplay(TranslatorCore.Config.proxy_mode);
            _proxyUrlInput.Text = TranslatorCore.Config.proxy_url ?? "";
            _proxyUserInput.Text = TranslatorCore.Config.proxy_username ?? "";
            _proxyPassInput.Text = TranslatorCore.Config.proxy_password ?? "";
            _proxyBypassLocalToggle.IsOn = TranslatorCore.Config.proxy_bypass_local;
            if (_proxyCustomSection != null)
                _proxyCustomSection.Visible = _proxyModeDropdown.SelectedValue == "Custom";

            // Translation (Backend + Capture) — after online mode so UpdateBackendSections sees correct online state
            _captureKeysOnlyToggle.IsOn = TranslatorCore.Config.capture_keys_only;
            if (_debugLoggingToggle != null) _debugLoggingToggle.IsOn = TranslatorCore.Config.debug;
            if (_debugAiToggle != null) _debugAiToggle.IsOn = TranslatorCore.Config.debug_ai;
            _aiUrlInput.Text = TranslatorCore.Config.ai_url ?? Endpoints.OllamaDefault;
            RefreshAiLocality();
            _aiApiKeyInput.Text = TranslatorCore.Config.ai_api_key ?? "";
            _googleApiKeyInput.Text = TranslatorCore.Config.google_api_key ?? "";
            _deeplApiKeyInput.Text = TranslatorCore.Config.deepl_api_key ?? "";
            _deeplUseFreeToggle.IsOn = TranslatorCore.Config.deepl_use_free;
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
            _strictSourceToggle.IsOn = TranslatorCore.Config.strict_source_language;
            _aiTestStatusLabel.Show("");
            // Set dropdowns BEFORE the enable toggle (which triggers UpdateBackendSections)
            string backend = TranslatorCore.Config.translation_backend ?? "none";
            _backendTypeDropdown.SelectedValue = (backend == "google" || backend == "deepl") ? UIStyles.BackendTypeApi : UIStyles.BackendTypeLLM;
            _providerDropdown.SelectedValue = backend == "deepl" ? "DeepL" : "Google Translate";

            // Reads enable_ai, not the backend: a paused setup keeps its backend, so asking the
            // backend would show the switch as on while nothing translates. "none" is still
            // honoured — it is what a community-translations-only setup carries, and there is
            // nothing there to switch on.
            _enableTranslationBackendToggle.IsOn =
                TranslatorCore.Config.enable_ai && backend != "none";

            // Done loading — enable listeners and apply section visibility once
            _isLoadingSettings = false;
            UpdateBackendSections();

            // Advanced settings (per-game, stored in translations.json)
            _disableEventSystemOverrideToggle.IsOn = TranslatorCore.DisableEventSystemOverride;
            _captureKeyboardToggle.IsOn = TranslatorCore.CaptureKeyboard;
            _captureKeyboardFocusOnlyToggle.IsOn = TranslatorCore.CaptureKeyboardFocusOnly;
            _captureGameMenusToggle.IsOn = TranslatorCore.CaptureGameMenus;
            _captureGameClicksToggle.IsOn = TranslatorCore.CaptureGameClicks;
            _captureMouseAxesToggle.IsOn = TranslatorCore.CaptureMouseAxes;
            _pauseGameToggle.IsOn = TranslatorCore.PauseGame;
            _opacityFocusedSlider.Value = TranslatorCore.PanelOpacityFocused;
            _opacityUnfocusedSlider.Value = TranslatorCore.PanelOpacityUnfocused;

            // Update strict toggle based on source language
            RefreshStrictSourceState();

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
                _languagesEditableSection.Visible = !locked;
            }

            if (_languagesLockedSection != null)
            {
                _languagesLockedSection.Visible = locked;

                // ⚠ The reason, and it is not always the same one. A file being written here can
                // still be re-targeted — by clearing it — where a published one never can.
                if (locked && _lockedHeader != null)
                {
                    _lockedHeader.Say(TranslatorCore.LanguagesLockedByPublishing
                        ? "Languages are settled: this translation is published."
                        : "Languages are settled: this file already holds lines. Clear the "
                          + "translation to change them.");
                }

                if (locked && _lockedSourceLangValue != null && _lockedTargetLangValue != null)
                {
                    string sourceLang = TranslatorCore.Config.source_language;
                    string targetLang = TranslatorCore.Config.target_language;

                    _lockedSourceLangValue.Show(string.IsNullOrEmpty(sourceLang) || sourceLang == "auto"
                        ? "Auto (Detect)"
                        : sourceLang);

                    _lockedTargetLangValue.Show(string.IsNullOrEmpty(targetLang) || targetLang == "auto"
                        ? "Auto (System)"
                        : targetLang);
                }
            }
        }

        private void OnOnlineModeChanged(bool enabled)
        {
            _checkFrequencyDropdown.SetInteractable(enabled);
            _notifyUpdatesToggle.Enabled = enabled;
            _autoDownloadToggle.Enabled = enabled;
            _checkModUpdatesToggle.Enabled = enabled;
            _notifyPrereleasesToggle.Enabled = enabled;
            _checkModUpdatesNowBtn.Enabled = enabled;

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

                _resetWindowsStatusLabel.Say("Positions reset!");
                _resetWindowsStatusLabel.Tone = Tone.Success;

                TranslatorCore.LogInfo("[Options] Window preferences reset");
            }
            catch (Exception e)
            {
                _resetWindowsStatusLabel.Show(Tr("Error:") + $" {e.Message}");
                _resetWindowsStatusLabel.Tone = Tone.Error;
            }
        }

        private void OnCaptureKeysOnlyChanged(bool captureOnly)
        {
            if (_isLoadingSettings) return;
            UpdateBackendSections();
            RefreshStrictSourceState();
        }

        private void OnEnableTranslationBackendChanged(bool enabled)
        {
            if (_isLoadingSettings) return;
            UpdateBackendSections();
            RefreshStrictSourceState();
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

            _aiLocalityLabel.Show(caution ?? "");
            _aiLocalityLabel.Visible = caution != null;
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
            bool captureOnly = _captureKeysOnlyToggle.IsOn;

            _enableTranslationBackendToggle.Enabled = !captureOnly;

            // ⚠ The backend settings stay VISIBLE and editable while translation is switched off,
            // and that is the point of the switch. Setting up a server, a model or an API key is
            // exactly what one does before starting — hiding the fields until translation is
            // running meant it had to be started, in a game, before it could be configured.
            //
            // Capture-only is different: there is no backend at all in that mode, so there is
            // nothing to configure and the sections go.
            if (_backendTypeSection != null) _backendTypeSection.Visible = !captureOnly;

            if (captureOnly)
            {
                if (_llmSection != null) _llmSection.Visible = false;
                if (_translationApiSection != null) _translationApiSection.Visible = false;
                return;
            }

            // Translation APIs require online mode
            bool canUseTransApi = _onlineModeToggle != null && _onlineModeToggle.IsOn;
            _backendTypeDropdown?.SetInteractable(canUseTransApi);
            if (!canUseTransApi && _backendTypeDropdown?.SelectedValue == UIStyles.BackendTypeApi)
            {
                _backendTypeDropdown.SelectedValue = UIStyles.BackendTypeLLM;
            }

            string type = _backendTypeDropdown?.SelectedValue ?? UIStyles.BackendTypeLLM;
            bool isLLM = type == UIStyles.BackendTypeLLM;

            if (_llmSection != null) _llmSection.Visible = isLLM;
            if (_translationApiSection != null) _translationApiSection.Visible = !isLLM;

            RefreshStrictSourceState();

            if (!isLLM)
            {
                string provider = _providerDropdown?.SelectedValue ?? "Google Translate";
                if (_googleSection != null) _googleSection.Visible = provider == "Google Translate";
                if (_deeplSection != null) _deeplSection.Visible = provider == "DeepL";
            }
        }

        private async void TestGoogleConnection()
        {
            string apiKey = _googleApiKeyInput?.Text;
            if (string.IsNullOrEmpty(apiKey))
            {
                _googleTestStatusLabel.Say("Enter an API key first");
                _googleTestStatusLabel.Tone = Tone.Warning;
                return;
            }

            _googleTestStatusLabel.Say("Testing...");
            _googleTestStatusLabel.Tone = Tone.Secondary;

            bool success = await TranslatorCore.TestGoogleConnection(apiKey);

            TranslatorUIManager.RunOnMainThread(() =>
            {
                if (success)
                {
                    _googleTestStatusLabel.Say("Connected!");
                    _googleTestStatusLabel.Tone = Tone.Success;
                }
                else
                {
                    _googleTestStatusLabel.Say("Failed - check API key");
                    _googleTestStatusLabel.Tone = Tone.Error;
                }
            });
        }

        private async void TestDeepLConnection()
        {
            string apiKey = _deeplApiKeyInput?.Text;
            if (string.IsNullOrEmpty(apiKey))
            {
                _deeplTestStatusLabel.Say("Enter an API key first");
                _deeplTestStatusLabel.Tone = Tone.Warning;
                return;
            }

            _deeplTestStatusLabel.Say("Testing...");
            _deeplTestStatusLabel.Tone = Tone.Secondary;

            bool useFree = _deeplUseFreeToggle.IsOn;
            bool success = await TranslatorCore.TestDeepLConnection(apiKey, useFree);

            TranslatorUIManager.RunOnMainThread(() =>
            {
                if (success)
                {
                    _deeplTestStatusLabel.Say("Connected!");
                    _deeplTestStatusLabel.Tone = Tone.Success;
                }
                else
                {
                    _deeplTestStatusLabel.Say("Failed - check API key and plan type");
                    _deeplTestStatusLabel.Tone = Tone.Error;
                }
            });
        }

        private async void OnCheckModUpdatesNowClicked()
        {
            if (!TranslatorCore.Config.online_mode)
            {
                _checkModUpdatesStatusLabel.Say("Enable online mode first");
                _checkModUpdatesStatusLabel.Tone = Tone.Warning;
                return;
            }

            _checkModUpdatesNowBtn.Enabled = false;
            _checkModUpdatesStatusLabel.Say("Checking...");
            _checkModUpdatesStatusLabel.Tone = Tone.Secondary;

            try
            {
                string currentVersion = PluginInfo.Version;
                string modLoaderType = TranslatorCore.Adapter?.ModLoaderType ?? "Unknown";

                var result = await GitHubUpdateChecker.CheckForUpdatesAsync(currentVersion, modLoaderType,
                    _notifyPrereleasesToggle != null && _notifyPrereleasesToggle.IsOn);

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

                        _checkModUpdatesStatusLabel.Show(Tr("Update available:") + $" v{latestVersion}");
                        _checkModUpdatesStatusLabel.Tone = Tone.Success;

                        TranslatorUIManager.MainPanel?.RefreshUI();
                    }
                    else if (success)
                    {
                        _checkModUpdatesStatusLabel.Show(Tr("Up to date") + $" (v{currentVersion})");
                        _checkModUpdatesStatusLabel.Tone = Tone.Success;
                    }
                    else
                    {
                        _checkModUpdatesStatusLabel.Show(Tr("Error:") + $" {error}");
                        _checkModUpdatesStatusLabel.Tone = Tone.Error;
                    }

                    _checkModUpdatesNowBtn.Enabled = true;
                });
            }
            catch (System.Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _checkModUpdatesStatusLabel.Show(Tr("Error:") + $" {errorMsg}");
                    _checkModUpdatesStatusLabel.Tone = Tone.Error;
                    _checkModUpdatesNowBtn.Enabled = true;
                });
            }
        }

        private async void TestAIConnection()
        {
            _aiTestStatusLabel.Say("Testing...");
            _aiTestStatusLabel.Tone = Tone.Warning;

            string url = _aiUrlInput.Text;
            string apiKey = _aiApiKeyInput.Text;

            try
            {
                bool success = await TranslatorCore.TestAIConnection(url, apiKey);

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success)
                    {
                        _aiTestStatusLabel.Say("Connection successful!");
                        _aiTestStatusLabel.Tone = Tone.Success;
                        // Auto-refresh models on successful test
                        RefreshModels();
                    }
                    else
                    {
                        _aiTestStatusLabel.Say("Connection failed");
                        _aiTestStatusLabel.Tone = Tone.Error;
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorCore.LogWarning($"[Options] TestAIConnection threw: {e.GetType().Name}: {errorMsg}");
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _aiTestStatusLabel.Show(Tr("Error:") + $" {errorMsg}");
                    _aiTestStatusLabel.Tone = Tone.Error;
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
                TranslatorCore.Config.enable_translations = _enableTranslationsToggle.IsOn;
                // Applying records an EXPLICIT choice (tri-state leaves "undecided" for users who
                // never opened this, letting the translation decide for them).
                TranslatorCore.Config.translate_mod_ui = _translateModUIToggle.IsOn;
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
                TranslatorCore.Config.capture_keys_only = _captureKeysOnlyToggle.IsOn;
                if (_debugLoggingToggle != null) TranslatorCore.SetRuntimeDebug(_debugLoggingToggle.IsOn);
                if (_debugAiToggle != null) TranslatorCore.Config.debug_ai = _debugAiToggle.IsOn;
                string newBackend = GetSelectedBackendConfig();
                TranslatorCore.Config.translation_backend = newBackend;
                // The toggle says whether translation runs; the dropdowns say what runs it. Two
                // questions, two keys — so turning it off here and turning it off with the pause
                // hotkey now leave the file in exactly the same state, and neither loses a
                // setting on the way.
                TranslatorCore.Config.enable_ai =
                    _enableTranslationBackendToggle != null && _enableTranslationBackendToggle.IsOn;
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
                TranslatorCore.Config.strict_source_language = _strictSourceToggle.IsOn;
                string googleKey = _googleApiKeyInput?.Text;
                TranslatorCore.Config.google_api_key = !string.IsNullOrEmpty(googleKey) ? googleKey : null;
                string deeplKey = _deeplApiKeyInput?.Text;
                TranslatorCore.Config.deepl_api_key = !string.IsNullOrEmpty(deeplKey) ? deeplKey : null;
                TranslatorCore.Config.deepl_use_free = _deeplUseFreeToggle.IsOn;
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
                bool nowOnline = _onlineModeToggle.IsOn;
                TranslatorCore.Config.online_mode = nowOnline;

                // Applied in place, not "next launch": turning the stream on must open it now, and
                // turning it off must close it now. Same for the rhythm.
                string previousFrequency = UpdateCheckFrequency.Normalize(TranslatorCore.Config.sync.update_check_frequency);
                string newFrequency = FrequencyDisplayToConfig(_checkFrequencyDropdown.SelectedValue);

                bool previousRealtime = TranslatorCore.Config.sync.realtime_own_translation;
                bool newRealtime = _realtimeOwnToggle.IsOn;

                bool frequencyChanged = previousFrequency != newFrequency
                                        || previousRealtime != newRealtime;

                TranslatorCore.Config.sync.update_check_frequency = newFrequency;
                TranslatorCore.Config.sync.realtime_own_translation = newRealtime;
                TranslatorCore.Config.sync.notify_updates = _notifyUpdatesToggle.IsOn;
                TranslatorCore.Config.sync.auto_download = _autoDownloadToggle.IsOn;
                TranslatorCore.Config.sync.check_mod_updates = _checkModUpdatesToggle.IsOn;
                TranslatorCore.Config.sync.notify_prereleases = _notifyPrereleasesToggle.IsOn;
                TranslatorCore.Config.sync.notifications_enabled = _notificationsEnabledToggle.IsOn;
                TranslatorCore.Config.sync.notification_position = PositionDisplayToConfig(_notificationPositionDropdown.SelectedValue);

                // Apply notification position change immediately
                TranslatorUIManager.StatusOverlay?.ApplyPositionFromConfig();

                // Advanced settings (per-game, stored in translations.json, requires restart)
                bool eventSystemChanged = TranslatorCore.DisableEventSystemOverride != _disableEventSystemOverrideToggle.IsOn;
                TranslatorCore.DisableEventSystemOverride = _disableEventSystemOverrideToggle.IsOn;
                // Keep UniverseLib's own copy in step: it consults the flag live (its EventSystem
                // patches read it every time), so with this in place the setting takes effect at
                // once instead of only at the next launch.
                TranslatorCore.SyncEventSystemOverrideLive();

                // Only the EventSystem override lives in the translation: it answers a defect of
                // a particular game and is worth carrying to whoever installs that translation.
                // The capture options and the freeze are preferences and go to config.json, which
                // SaveConfig writes below — they must not ride along when a translation is shared.
                bool perGameChanged = eventSystemChanged;

                // Input capture (per-game too). No restart needed: the capture asks these on every
                // read, so unticking one hands that input back to the game on the next frame —
                // which is what someone turning it off because the game misbehaves needs.
                TranslatorCore.CaptureKeyboard = _captureKeyboardToggle.IsOn;
                TranslatorCore.CaptureKeyboardFocusOnly = _captureKeyboardFocusOnlyToggle.IsOn;
                TranslatorCore.CaptureGameMenus = _captureGameMenusToggle.IsOn;
                TranslatorCore.CaptureGameClicks = _captureGameClicksToggle.IsOn;
                TranslatorCore.CaptureMouseAxes = _captureMouseAxesToggle.IsOn;
                TranslatorCore.PauseGame = _pauseGameToggle.IsOn;
                TranslatorCore.PanelOpacityFocused = _opacityFocusedSlider.Value;
                TranslatorCore.PanelOpacityUnfocused = _opacityUnfocusedSlider.Value;

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
                bool newProxyBypass = _proxyBypassLocalToggle.IsOn;

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
                _aiTestStatusLabel.Show(Tr("Error:") + $" {e.Message}");
                _aiTestStatusLabel.Tone = Tone.Error;
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
            // Input fields (FieldHandle.Changed is a C# event, IL2CPP-safe)
            _aiUrlInput.Changed += _ => UpdateApplyButtonText();
            _aiApiKeyInput.Changed += _ => UpdateApplyButtonText();
            _gameContextInput.Changed += _ => UpdateApplyButtonText();
            _googleApiKeyInput.Changed += _ => UpdateApplyButtonText();
            _deeplApiKeyInput.Changed += _ => UpdateApplyButtonText();
            _rateLimitDelayInput.Changed += _ => UpdateApplyButtonText();
            if (_aiMaxAttemptsInput != null) _aiMaxAttemptsInput.Changed += _ => UpdateApplyButtonText();
            if (_aiTemperatureInput != null) _aiTemperatureInput.Changed += _ => UpdateApplyButtonText();
            if (_aiTemperatureRepairInput != null) _aiTemperatureRepairInput.Changed += _ => UpdateApplyButtonText();
            if (_aiTemperatureRetranslateInput != null) _aiTemperatureRetranslateInput.Changed += _ => UpdateApplyButtonText();
            if (_aiSeedInput != null) _aiSeedInput.Changed += _ => UpdateApplyButtonText();
            if (_aiSeedRepairInput != null) _aiSeedRepairInput.Changed += _ => UpdateApplyButtonText();
            if (_aiSeedRetranslateInput != null) _aiSeedRetranslateInput.Changed += _ => UpdateApplyButtonText();

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
            P.Track(_enableTranslationsToggle, () => _enableTranslationsToggle.IsOn != S().enable_translations);
            P.Track(_translateModUIToggle, () => _translateModUIToggle.IsOn != S().translate_mod_ui);
            P.Track(_interfaceFontDropdown?.Handle, () => NormalizeInterfaceFont(_interfaceFontDropdown?.SelectedValue) != S().interface_font);

            // Languages
            P.Track(_sourceLanguageDropdown.Handle, () =>
                _sourceLanguageDropdown.SelectedValue != (S().source_language == "auto" ? "auto (Detect)" : S().source_language));
            P.Track(_targetLanguageDropdown.Handle, () =>
                _targetLanguageDropdown.SelectedValue != (S().target_language == "auto" ? "auto (System)" : S().target_language));

            // Hotkeys
            P.Track(_hotkeyCapture.Handle, () => _hotkeyCapture.HotkeyString != S().settings_hotkey);
            P.Track(_hotkeyToggleTranslations.Handle, () => _hotkeyToggleTranslations.HotkeyString != S().toggle_translations_hotkey);
            P.Track(_hotkeyToggleAI.Handle, () => _hotkeyToggleAI.HotkeyString != S().toggle_ai_hotkey);
            P.Track(_hotkeyToggleImages.Handle, () => _hotkeyToggleImages.HotkeyString != S().toggle_images_hotkey);
            P.Track(_hotkeyToggleFonts.Handle, () => _hotkeyToggleFonts.HotkeyString != S().toggle_fonts_hotkey);
            P.Track(_hotkeyToggleOverlay.Handle, () => _hotkeyToggleOverlay.HotkeyString != S().toggle_overlay_hotkey);
            P.Track(_hotkeyOpenInspector.Handle, () => _hotkeyOpenInspector.HotkeyString != S().open_inspector_hotkey);
            P.Track(_hotkeyOpenUpload.Handle, () => _hotkeyOpenUpload.HotkeyString != S().open_upload_hotkey);
            P.Track(_hotkeyOpenExclusion.Handle, () => _hotkeyOpenExclusion.HotkeyString != S().open_exclusion_mode_hotkey);
            P.Track(_hotkeyOpenTextEditor.Handle, () => _hotkeyOpenTextEditor.HotkeyString != S().open_text_editor_hotkey);
            P.Track(_hotkeyForceScan.Handle, () => _hotkeyForceScan.HotkeyString != S().force_scan_hotkey);

            // Translation (Backend + Capture)
            P.Track(_captureKeysOnlyToggle, () => _captureKeysOnlyToggle.IsOn != S().capture_keys_only);
            P.Track(_debugLoggingToggle, () => _debugLoggingToggle.IsOn != S().debug);
            P.Track(_debugAiToggle, () => _debugAiToggle.IsOn != S().debug_ai);
            // The backend is one config value read from two dropdowns: the type owns a change
            // of kind (AI or service), the provider a change of service within the API kind.
            P.Track(_backendTypeDropdown?.Handle, () => (S().translation_backend == "llm") != (GetSelectedBackendConfig() == "llm"));
            P.Track(_providerDropdown?.Handle, () =>
            {
                string now = GetSelectedBackendConfig();
                return now != "llm" && S().translation_backend != "llm" && now != S().translation_backend;
            });

            // Counted on its own, because the toggle no longer moves the backend: without this
            // line, switching translation off and pressing nothing would leave Apply reading
            // "Close" and the change would be silently dropped on the way out.
            P.Track(_enableTranslationBackendToggle, () =>
                (_enableTranslationBackendToggle != null && _enableTranslationBackendToggle.IsOn) != S().enable_ai);
            P.Track(_aiUrlInput, () => _aiUrlInput.Text != S().ai_url);
            P.Track(_aiApiKeyInput, () => (_aiApiKeyInput.Text ?? "") != S().ai_api_key);
            P.Track(_modelDropdown.Handle, () => (_modelDropdown.SelectedValue ?? "") != S().ai_model);
            P.Track(_gameContextInput, () => _gameContextInput.Text != S().game_context);
            P.Track(_strictSourceToggle, () => _strictSourceToggle.IsOn != S().strict_source_language);
            P.Track(_googleApiKeyInput, () => (_googleApiKeyInput?.Text ?? "") != S().google_api_key);
            P.Track(_deeplApiKeyInput, () => (_deeplApiKeyInput?.Text ?? "") != S().deepl_api_key);
            P.Track(_deeplUseFreeToggle, () => _deeplUseFreeToggle.IsOn != S().deepl_use_free);
            P.Track(_rateLimitDelayInput, () =>
            {
                float parsedDelay;
                float currentDelay = (float.TryParse(_rateLimitDelayInput?.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsedDelay) && parsedDelay >= 0.1f) ? parsedDelay : 3f;
                return Math.Abs(currentDelay - S().rate_limit_retry_delay) > 0.01f;
            });

            // Advanced (AI) — the same readers Apply uses.
            P.Track(_aiMaxAttemptsInput, () =>
            {
                int parsedAttempts;
                int currentAttempts = (int.TryParse((_aiMaxAttemptsInput?.Text ?? "").Trim(), System.Globalization.NumberStyles.Integer,
                                       System.Globalization.CultureInfo.InvariantCulture, out parsedAttempts)
                                       && parsedAttempts >= 1 && parsedAttempts <= 10)
                                      ? parsedAttempts : Placeholders.MaxAttempts;
                return currentAttempts != S().ai_max_attempts;
            });
            P.Track(_aiTemperatureInput, () => Math.Abs(TemperatureFromText(_aiTemperatureInput?.Text, 0.0) - S().ai_temperature) > 0.001);
            P.Track(_aiTemperatureRepairInput, () => Math.Abs(TemperatureFromText(_aiTemperatureRepairInput?.Text, 0.3) - S().ai_temperature_repair) > 0.001);
            P.Track(_aiTemperatureRetranslateInput, () => Math.Abs(TemperatureFromText(_aiTemperatureRetranslateInput?.Text, 0.8) - S().ai_temperature_retranslate) > 0.001);
            P.Track(_aiSeedInput, () => SeedToText(SeedFromText(_aiSeedInput?.Text)) != S().ai_seed);
            P.Track(_aiSeedRepairInput, () => SeedToText(SeedFromText(_aiSeedRepairInput?.Text)) != S().ai_seed_repair);
            P.Track(_aiSeedRetranslateInput, () => SeedToText(SeedFromText(_aiSeedRetranslateInput?.Text)) != S().ai_seed_retranslate);

            // Online
            P.Track(_onlineModeToggle, () => _onlineModeToggle.IsOn != S().online_mode);
            P.Track(_checkFrequencyDropdown.Handle, () => FrequencyDisplayToConfig(_checkFrequencyDropdown.SelectedValue) != S().update_check_frequency);
            P.Track(_realtimeOwnToggle, () => _realtimeOwnToggle.IsOn != S().realtime_own_translation);
            P.Track(_notifyUpdatesToggle, () => _notifyUpdatesToggle.IsOn != S().notify_updates);
            P.Track(_autoDownloadToggle, () => _autoDownloadToggle.IsOn != S().auto_download);
            P.Track(_checkModUpdatesToggle, () => _checkModUpdatesToggle.IsOn != S().check_mod_updates);
            P.Track(_notifyPrereleasesToggle, () => _notifyPrereleasesToggle.IsOn != S().notify_prereleases);
            P.Track(_notificationsEnabledToggle, () => _notificationsEnabledToggle.IsOn != S().notifications_enabled);
            P.Track(_notificationPositionDropdown.Handle, () => PositionDisplayToConfig(_notificationPositionDropdown.SelectedValue) != S().notification_position);

            // Advanced (per-game settings)
            P.Track(_disableEventSystemOverrideToggle, () => _disableEventSystemOverrideToggle.IsOn != S().disable_eventsystem_override);
            P.Track(_captureKeyboardToggle, () => _captureKeyboardToggle.IsOn != S().capture_keyboard);
            P.Track(_captureKeyboardFocusOnlyToggle, () => _captureKeyboardFocusOnlyToggle.IsOn != S().capture_keyboard_focus_only);
            P.Track(_captureGameMenusToggle, () => _captureGameMenusToggle.IsOn != S().capture_game_menus);
            P.Track(_captureGameClicksToggle, () => _captureGameClicksToggle.IsOn != S().capture_game_clicks);
            P.Track(_captureMouseAxesToggle, () => _captureMouseAxesToggle.IsOn != S().capture_mouse_axes);
            P.Track(_pauseGameToggle, () => _pauseGameToggle.IsOn != S().pause_game);
            P.Track(_opacityFocusedSlider, () => Math.Abs(_opacityFocusedSlider.Value - S().panel_opacity_focused) > 0.001f);
            P.Track(_opacityUnfocusedSlider, () => Math.Abs(_opacityUnfocusedSlider.Value - S().panel_opacity_unfocused) > 0.001f);

            // Proxy / Network
            P.Track(_proxyModeDropdown.Handle, () => ProxyModeDisplayToConfig(_proxyModeDropdown.SelectedValue) != S().proxy_mode);
            P.Track(_proxyUrlInput, () => (_proxyUrlInput.Text ?? "").Trim() != S().proxy_url);
            P.Track(_proxyUserInput, () => (_proxyUserInput.Text ?? "") != S().proxy_username);
            P.Track(_proxyPassInput, () => (_proxyPassInput.Text ?? "") != S().proxy_password);
            P.Track(_proxyBypassLocalToggle, () => _proxyBypassLocalToggle.IsOn != S().proxy_bypass_local);
        }

        /// <summary>
        /// Updates the Apply button text based on pending changes count.
        /// Shows "Apply (x)" when there are changes, "Close" when there are none.
        /// </summary>
        private void UpdateApplyButtonText()
        {
            if (_applyBtn == null) return;

            int changes = CountPendingChanges();
            // Translated at set-time (cache-aware, placeholder-aware) so this code-managed button
            // shows the right state in the current language without racing the async pipeline.
            _applyBtn.Label = changes > 0 ? $"Apply ({changes})" : "Close";
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
