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
        /// <summary>The screen as a document — common/spec/screens/options.json — read once; the base's constructor reads the sizes below through it.</summary>
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("options");

        /// <summary>What the builder made of the document: every piece by name.</summary>
        private BuiltScreen _screen;

        public override string Name => Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

        // Tab system
        private TabBar _tabBar;

        // General section
        private ToggleHandle _enableTranslationsToggle;
        private ToggleHandle _translateModUIToggle;
        private SearchableDropdown _interfaceFontDropdown; // mod UI font, shown only when translating the mod UI
        private Host _interfaceFontRow;                    // container toggled with the checkbox
        private SearchableDropdown _sourceLanguageDropdown;
        private SearchableDropdown _targetLanguageDropdown;

        // Language section containers for conditional display
        private Host _languagesEditableSection;
        private Host _languagesLockedSection;
        private LabelHandle _lockedHeader;
        private LabelHandle _lockedSourceLangValue;
        private LabelHandle _lockedTargetLangValue;
        private Host _lockedSourceMark;
        private Host _lockedTargetMark;

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

        /// <summary>
        /// The screen is options.json; this builds it, gives the pieces the choices and values only
        /// the code knows, and keeps hold of what Apply reads. What the document carries, and why
        /// — a document has no comments:
        /// - five tabs in the fixed header, their contents in the scrolling body;
        /// - General: the interface font row shows only while the mod UI is translated; the
        ///   opacity sliders floor at 40% (uGUI applies the alpha to the text too, lower is not
        ///   translucent but unreadable) and sit with the window reset — what they govern is the
        ///   mod's own windows; the languages come in two blocks, editable and locked, and the
        ///   locked one draws the flag beside the value because a language drawn with its flag in
        ///   one place and as bare text three lines below reads as two different things;
        /// - Adaptation (was "Input", which stopped describing it — freezing touches the clock,
        ///   the EventSystem decides whether the game's interface answers at all; "Compatibility"
        ///   reads as repairing, while these are as often a deliberate choice): each box is an
        ///   INTENTION, and whether it can be honoured is the game's — the code asks the runtime
        ///   per intention and greys out what nobody can serve, with the runtime's own sentence
        ///   in the hidden line under the box; the two mouse boxes are separate because menus
        ///   answer a raycast and the game's own clicks are a read; freezing is its own section,
        ///   off by default, three lines — what it does, why it is off, what is dangerous;
        /// - Hotkeys: the capture controls are the code's, built into hosts, one per shortcut;
        /// - Translation: manual capture first, then the backend, folded advanced settings last —
        ///   a temperature is a real change and every one has a default right for nearly
        ///   everybody; the number fields are Text, not Decimal, because a locale-aware field
        ///   refuses the dot these values are written with (validation happens at Apply);
        /// - Online: the real-time box before the rhythm, because it says what does not follow it;
        ///   the proxy's custom block shows only in Custom mode.
        /// </summary>
        protected override void ConstructPanelContent()
        {
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

            Layout(out var body, out var footer, Doc.Width - 40);
            _helpZone = CreateHelpZone(footer, Doc.Help);
            _screen = ScreenBuilder.Build(Doc, body, footer, ActOf, header: FixedHeader(), help: _helpZone,
                                          layoutChanged: RecalculateSize);

            _tabBar = _screen.Tabs("Tabs");

            // ── General ─────────────────────────────────────────────────────────
            _enableTranslationsToggle = _screen.Toggle("EnableTranslationsToggle");
            _translateModUIToggle = _screen.Toggle("TranslateModUIToggle");
            _interfaceFontRow = _screen.Host("InterfaceFontRow");
            _interfaceFontDropdown = _screen.Dropdown("InterfaceFont");
            _notificationsEnabledToggle = _screen.Toggle("NotifEnabledToggle");
            _notificationPositionDropdown = _screen.Dropdown("NotifPosition");
            _debugLoggingToggle = _screen.Toggle("DebugLoggingToggle");
            _debugAiToggle = _screen.Toggle("DebugAiToggle");
            _languagesEditableSection = _screen.Host("LanguagesEditableSection");
            _sourceLanguageDropdown = _screen.Dropdown("SourceLang");
            _targetLanguageDropdown = _screen.Dropdown("TargetLang");
            _languagesLockedSection = _screen.Host("LanguagesLockedSection");
            _lockedHeader = _screen.Label("LockedHeader");
            _lockedSourceMark = _screen.Host("SourceMark");
            _lockedSourceLangValue = _screen.Label("SourceValue");
            _lockedTargetMark = _screen.Host("TargetMark");
            _lockedTargetLangValue = _screen.Label("TargetValue");
            _opacityFocusedSlider = _screen.Slider("OpacityFocused");
            _opacityUnfocusedSlider = _screen.Slider("OpacityUnfocused");
            _resetWindowsStatusLabel = _screen.Label("ResetStatus");

            // ── Hotkeys: the capture controls, in their hosts ───────────────────
            _hotkeyCapture.CreateUI(_screen.Host("SettingsHotkeyHost"));
            _helpZone.Describe(_hotkeyCapture.Handle, "The keyboard shortcut that opens and closes this settings panel.");
            PlaceHotkey(_hotkeyToggleTranslations, "HkToggleTranslationsHost",
                "Shortcut to turn all translations on or off, restoring the game's original text.");
            PlaceHotkey(_hotkeyToggleAI, "HkToggleAIHost",
                "Shortcut to pause or resume live translation. Texts already translated stay translated.");
            PlaceHotkey(_hotkeyToggleImages, "HkToggleImagesHost",
                "Shortcut to switch between original and replaced images. Mainly for debugging.");
            PlaceHotkey(_hotkeyToggleFonts, "HkToggleFontsHost",
                "Shortcut to switch between the game's original fonts and the mod's replacement fonts.");
            PlaceHotkey(_hotkeyToggleOverlay, "HkToggleOverlayHost",
                "Shortcut to show or hide the corner notification overlay, handy for clean screenshots.");
            PlaceHotkey(_hotkeyOpenInspector, "HkOpenInspectorHost",
                "Shortcut to open or close the element inspector panel.");
            PlaceHotkey(_hotkeyOpenUpload, "HkOpenUploadHost",
                "Shortcut to open or close the translation upload panel.");
            PlaceHotkey(_hotkeyOpenExclusion, "HkOpenExclusionHost",
                "Shortcut to open or close the inspector in exclusion mode.");
            PlaceHotkey(_hotkeyOpenTextEditor, "HkOpenTextEditorHost",
                "Shortcut to open or close the in-game text editor, where you click UI text to edit it.");
            PlaceHotkey(_hotkeyForceScan, "HkForceScanHost",
                "Shortcut to re-scan the current scene, useful after scene glitches.");

            // ── Adaptation: each box greyed out with the runtime's own reason when nothing can serve it ──
            _captureKeyboardToggle = _screen.Toggle("CaptureKeyboard");
            SetUpCaptureToggle(_captureKeyboardToggle, _screen.Label("CaptureKeyboardWhy"), TranslatorCore.InputIntent.Keyboard);
            _captureKeyboardFocusOnlyToggle = _screen.Toggle("CaptureKeyboardFocusOnly");
            _captureGameMenusToggle = _screen.Toggle("CaptureGameMenus");
            SetUpCaptureToggle(_captureGameMenusToggle, _screen.Label("CaptureGameMenusWhy"), TranslatorCore.InputIntent.GameMenus);
            _captureGameClicksToggle = _screen.Toggle("CaptureGameClicks");
            SetUpCaptureToggle(_captureGameClicksToggle, _screen.Label("CaptureGameClicksWhy"), TranslatorCore.InputIntent.GameClicks);
            _captureMouseAxesToggle = _screen.Toggle("CaptureMouseAxes");
            SetUpCaptureToggle(_captureMouseAxesToggle, _screen.Label("CaptureMouseAxesWhy"), TranslatorCore.InputIntent.MouseAxes);
            _pauseGameToggle = _screen.Toggle("PauseGameToggle");
            SetUpPauseToggle();
            _disableEventSystemOverrideToggle = _screen.Toggle("DisableEventSystemToggle");

            // ── Translation ─────────────────────────────────────────────────────
            _captureKeysOnlyToggle = _screen.Toggle("CaptureKeysToggle");
            _enableTranslationBackendToggle = _screen.Toggle("EnableTransBackendToggle");
            _backendTypeSection = _screen.Host("BackendTypeSection");
            _backendTypeDropdown = _screen.Dropdown("BackendTypeDropdown");
            _llmSection = _screen.Host("LLMSection");
            _aiUrlInput = _screen.Field("AIUrl");
            _aiTestStatusLabel = _screen.Label("TestStatus");
            _aiLocalityLabel = _screen.Label("Locality");
            _aiApiKeyInput = _screen.Field("AIApiKey");
            _modelDropdown = _screen.Dropdown("ModelDropdown");
            _gameContextInput = _screen.Field("ContextInput");
            _strictSourceToggle = _screen.Toggle("StrictSourceToggle");
            _aiMaxAttemptsInput = _screen.Field("MaxAttempts");
            _aiTemperatureInput = _screen.Field("Temp");
            _aiTemperatureRepairInput = _screen.Field("TempRepair");
            _aiTemperatureRetranslateInput = _screen.Field("TempRetrans");
            _aiSeedInput = _screen.Field("Seed");
            _aiSeedRepairInput = _screen.Field("SeedRepair");
            _aiSeedRetranslateInput = _screen.Field("SeedRetrans");
            _translationApiSection = _screen.Host("TranslationApiSection");
            _providerDropdown = _screen.Dropdown("ProviderDropdown");
            _googleSection = _screen.Host("GoogleSection");
            _googleApiKeyInput = _screen.Field("GoogleApiKey");
            _googleTestStatusLabel = _screen.Label("GoogleTestStatus");
            _deeplSection = _screen.Host("DeepLSection");
            _deeplApiKeyInput = _screen.Field("DeepLApiKey");
            _deeplTestStatusLabel = _screen.Label("DeepLTestStatus");
            _deeplUseFreeToggle = _screen.Toggle("DeepLFreeToggle");
            _rateLimitDelayInput = _screen.Field("RateLimitDelay");

            // What sending this game's text to that address means follows what is being typed,
            // not what was last applied: somebody pasting a provider's address has to read it
            // before they press Apply, not after.
            _aiUrlInput.Changed += _ => RefreshAiLocality();

            // ── Online ──────────────────────────────────────────────────────────
            _onlineModeToggle = _screen.Toggle("OnlineModeToggle");
            _realtimeOwnToggle = _screen.Toggle("RealtimeOwnToggle");
            _checkFrequencyDropdown = _screen.Dropdown("CheckFrequency");
            _notifyUpdatesToggle = _screen.Toggle("NotifyToggle");
            _autoDownloadToggle = _screen.Toggle("AutoDownloadToggle");
            _settingsDriftRow = _screen.Host("SettingsDriftRow");
            _settingsDriftLabel = _screen.Label("SettingsDriftLabel");
            _restoreSettingsBtn = _screen.Button("RestoreSettingsBtn");
            _checkModUpdatesToggle = _screen.Toggle("ModUpdatesToggle");
            _checkModUpdatesNowBtn = _screen.Button("CheckNowBtn");
            _notifyPrereleasesToggle = _screen.Toggle("PrereleaseToggle");
            _checkModUpdatesStatusLabel = _screen.Label("ModUpdateStatus");
            _proxyModeDropdown = _screen.Dropdown("ProxyModeDropdown");
            _proxyCustomSection = _screen.Host("ProxyCustomSection");
            _proxyUrlInput = _screen.Field("ProxyUrl");
            _proxyUserInput = _screen.Field("ProxyUser");
            _proxyPassInput = _screen.Field("ProxyPass");
            _proxyBypassLocalToggle = _screen.Toggle("ProxyBypassToggle");

            _applyBtn = _screen.Button("ApplyBtn");

            // ── The choices only the code knows ─────────────────────────────────
            // Show the font IN EFFECT — the local override if set, else the one the translation
            // asks for — so the picker reflects what the user actually sees.
            _interfaceFontDropdown.CategoryProvider = FontManager.GetFontOrigin;
            string[] interfaceFontOptions = BuildInterfaceFontOptions();
            string initialInterfaceFont = string.IsNullOrEmpty(TranslatorCore.EffectiveInterfaceFont)
                ? "(None)" : TranslatorCore.EffectiveInterfaceFont;
            if (!Array.Exists(interfaceFontOptions, o => o == initialInterfaceFont))
                initialInterfaceFont = "(None)";
            _interfaceFontDropdown.SetOptions(interfaceFontOptions);
            _interfaceFontDropdown.SelectedValue = initialInterfaceFont;
            _interfaceFontRow.Visible = _translateModUIToggle.IsOn;

            _notificationPositionDropdown.SetOptions(new[] { "Top-Right", "Top-Left", "Bottom-Right", "Bottom-Left" });
            _notificationPositionDropdown.SelectedValue = "Top-Right";

            _backendTypeDropdown.SetOptions(BackendTypeOptions);
            _backendTypeDropdown.SelectedValue = UIStyles.BackendTypeLLM;
            _modelDropdown.SetOptions(new string[0]);
            _providerDropdown.SetOptions(ProviderOptions);
            _providerDropdown.SelectedValue = "Google Translate";

            _checkFrequencyDropdown.SetOptions(UpdateFrequencyDisplayOptions);
            _checkFrequencyDropdown.SelectedValue = FrequencyConfigToDisplay(UpdateCheckFrequency.Hourly);
            _proxyModeDropdown.SetOptions(ProxyModeDisplayOptions);
            _proxyModeDropdown.SelectedValue = ProxyModeDisplayOptions[0];

            _opacityFocusedSlider.Value = TranslatorCore.PanelOpacityFocused;
            _opacityUnfocusedSlider.Value = TranslatorCore.PanelOpacityUnfocused;

            // Setup change listeners for tracking pending changes
            SetupChangeListeners();
            RegisterPendingFields();
        }

        /// <summary>What each verb the document asks for does. A verb with no answer here fails at construction, not at the click.</summary>
        private Action ActOf(string act)
        {
            switch (act)
            {
                // General
                case "translateModUiChanged": return OnTranslateModUiChanged;
                case "interfaceFontChanged": return OnSettingChanged;
                case "notificationsEnabledChanged": return OnNotificationsEnabledChanged;
                case "notifPositionChanged":
                case "debugLoggingChanged":
                case "debugAiChanged":
                case "targetChanged": return UpdateApplyButtonText;
                case "sourceChanged": return OnSourceLanguageChanged;
                case "opacityFocusedChanged":
                case "opacityUnfocusedChanged": return OnSettingChanged;
                case "resetWindows": return OnResetWindowPositionsClicked;
                case "reportBug": return () => TranslatorCore.OpenUrlSafe("https://github.com/djethino/UnityGameTranslator/issues");
                case "discussions": return () => TranslatorCore.OpenUrlSafe("https://github.com/djethino/UnityGameTranslator/discussions");
                case "onlineDocs": return () => TranslatorCore.OpenUrlSafe($"{ApiClient.WebsiteBaseUrl}/docs");
                // Adaptation
                case "captureKeyboardChanged":
                case "captureKeyboardFocusChanged":
                case "captureGameMenusChanged":
                case "captureGameClicksChanged":
                case "captureMouseAxesChanged":
                case "pauseGameChanged":
                case "eventSystemChanged": return OnSettingChanged;
                // Translation
                case "captureKeysChanged": return OnCaptureKeysOnlyChanged;
                case "backendEnabledChanged": return OnEnableTranslationBackendChanged;
                case "backendTypeChanged": return OnBackendTypeChanged;
                case "testAi": return TestAIConnection;
                case "modelChanged": return () => { };
                case "refreshModels": return RefreshModels;
                case "providerChanged": return OnProviderChanged;
                case "testGoogle": return TestGoogleConnection;
                case "testDeepl": return TestDeepLConnection;
                // Online
                case "onlineModeChanged": return OnOnlineModeChanged;
                case "realtimeOwnChanged": return OnSettingChanged;
                case "checkFrequencyChanged": return UpdateApplyButtonText;
                case "restoreSettings": return OnRestoreSettingsClicked;
                case "checkModUpdates": return OnCheckModUpdatesNowClicked;
                case "proxyModeChanged": return OnProxyModeChanged;
                // Footer
                case "cancel": return () => SetActive(false);
                case "apply": return OnApplyClicked;
                default: return null;
            }
        }

        /// <summary>A setting moved: the Apply button counts again — unless the settings are being loaded, which is not a change.</summary>
        private void OnSettingChanged()
        {
            if (!_isLoadingSettings) UpdateApplyButtonText();
        }

        /// <summary>The interface font picker appears the moment the box is ticked; the value applies on Apply.</summary>
        private void OnTranslateModUiChanged()
        {
            if (_interfaceFontRow != null) _interfaceFontRow.Visible = _translateModUIToggle.IsOn;
            OnSettingChanged();
        }

        /// <summary>One shortcut's capture control, in the host the document keeps for it.</summary>
        private void PlaceHotkey(HotkeyCapture capture, string host, string help)
        {
            capture.CreateUI(_screen.Host(host), includeDisplayLabel: false);
            _helpZone.Describe(capture.Handle, help);
        }

        /// <summary>
        /// One capture box, greyed out with the runtime's own explanation when nothing can serve
        /// it. Whether an intention can be honoured is a property of the game, not of the wish,
        /// and only the runtime knows — a hardcoded list of what works would be wrong on some game
        /// and nobody would ever find out. The sentence goes in the line under the box: a box that
        /// is simply grey reads as a bug, or as a setting the player broke themselves.
        /// </summary>
        private void SetUpCaptureToggle(ToggleHandle toggle, LabelHandle why, TranslatorCore.InputIntent intent)
        {
            bool possible = TranslatorCore.CanCaptureInput(intent);
            toggle.Enabled = possible;
            if (possible) return;

            string reason = TranslatorCore.WhyInputCaptureUnavailable(intent);
            why.Show(reason);   // runtime diagnostic text, not UI chrome to translate
            why.Visible = true;
            _helpZone.Describe(toggle, reason);
        }

        /// <summary>
        /// Freezing the game: possible, the two lines saying why it is off and what is dangerous;
        /// impossible (an anti-cheat), the one line naming it, and the box greyed with the same words.
        /// </summary>
        private void SetUpPauseToggle()
        {
            string antiCheat = GamePause.AntiCheat;
            bool pausePossible = string.IsNullOrEmpty(antiCheat);
            _pauseGameToggle.Enabled = pausePossible;
            _screen.Label("PauseWhy").Visible = pausePossible;
            _screen.Label("PauseDanger").Visible = pausePossible;
            if (pausePossible) return;

            string why = $"Unavailable: this game is protected by {antiCheat}, which can treat freezing it as cheating.";
            var blocked = _screen.Label("PauseBlocked");
            blocked.Show(why);   // runtime diagnostic, not UI chrome
            blocked.Visible = true;
            _helpZone.Describe(_pauseGameToggle, why);
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

        private void OnProxyModeChanged()
        {
            string newDisplay = _proxyModeDropdown.SelectedValue;
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

        private void OnSourceLanguageChanged()
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
            OnOnlineModeChanged();

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

                    bool sourceIsAuto = string.IsNullOrEmpty(sourceLang) || sourceLang == "auto";
                    bool targetIsAuto = string.IsNullOrEmpty(targetLang) || targetLang == "auto";

                    _lockedSourceLangValue.Show(sourceIsAuto ? "Auto (Detect)" : sourceLang);
                    _lockedTargetLangValue.Show(targetIsAuto ? "Auto (System)" : targetLang);

                    // ⚠ Rebuilt rather than tinted: a mark is a flag image, and the flag is not the
                    // same one. An "auto" row stands for no language yet and gets none — which is
                    // what LanguageMark answers on a name the catalogue does not know.
                    ShowMark(_lockedSourceMark, sourceIsAuto ? null : sourceLang);
                    ShowMark(_lockedTargetMark, targetIsAuto ? null : targetLang);
                }
            }
        }

        /// <summary>
        /// Puts the flag for <paramref name="languageName"/> in its holder, or empties it.
        ///
        /// ⚠ Through LanguageMark, like every other flag in this mod: one drawing of a language,
        /// wherever it appears. A name the catalogue does not know — and "auto", which names a
        /// behaviour rather than a language — leaves the holder empty rather than guessing.
        /// </summary>
        private static void ShowMark(Host holder, string languageName)
        {
            if (holder == null) return;

            holder.Clear();
            if (string.IsNullOrEmpty(languageName)) return;

            LanguageMark.Create(holder, "Mark", languageName);
        }

        private void OnOnlineModeChanged()
        {
            bool enabled = _onlineModeToggle.IsOn;
            _checkFrequencyDropdown.SetInteractable(enabled);
            _notifyUpdatesToggle.Enabled = enabled;
            _autoDownloadToggle.Enabled = enabled;
            _checkModUpdatesToggle.Enabled = enabled;
            _notifyPrereleasesToggle.Enabled = enabled;
            _checkModUpdatesNowBtn.Enabled = enabled;

            // Translation API availability depends on online mode
            if (!_isLoadingSettings) UpdateBackendSections();
        }

        private void OnNotificationsEnabledChanged()
        {
            _notificationPositionDropdown.SetInteractable(_notificationsEnabledToggle.IsOn);
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

        private void OnCaptureKeysOnlyChanged()
        {
            if (_isLoadingSettings) return;
            UpdateBackendSections();
            RefreshStrictSourceState();
        }

        private void OnEnableTranslationBackendChanged()
        {
            if (_isLoadingSettings) return;
            UpdateBackendSections();
            RefreshStrictSourceState();
            UpdateApplyButtonText();
        }

        private void OnBackendTypeChanged()
        {
            if (_isLoadingSettings) return;
            UpdateBackendSections();
            UpdateApplyButtonText();
        }

        private void OnProviderChanged()
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

                        Intents.StateChanged();
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

                // One spelling of this machine (Endpoints.Canonical) — and written back into the
                // field too: Pending compares the field with the config, so a field left saying
                // "localhost" over a config saying 127.0.0.1 would keep Apply (1) lit for ever.
                string url = Endpoints.Canonical(_aiUrlInput.Text);
                TranslatorCore.Config.ai_url = url;
                if (_aiUrlInput.Text != url) _aiUrlInput.Text = url;
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
                Intents.OverlayPositionChanged();

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
                    Intents.SettingsChanged();

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

    }
}
