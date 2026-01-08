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
    /// </summary>
    public class OptionsPanel : TranslatorPanelBase
    {
        public override string Name => "Options";
        public override int MinWidth => 500;
        public override int MinHeight => 400;
        public override int PanelWidth => 520;
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

        // Translation section (Ollama + Capture)
        private Toggle _captureKeysOnlyToggle;
        private Toggle _enableOllamaToggle;
        private InputFieldRef _ollamaUrlInput;
        private InputFieldRef _modelInput;
        private InputFieldRef _gameContextInput;
        private Toggle _strictSourceToggle;
        private Text _ollamaTestStatusLabel;

        // Online section
        private Toggle _onlineModeToggle;
        private Toggle _checkUpdatesToggle;
        private Toggle _notifyUpdatesToggle;
        private Toggle _autoDownloadToggle;
        private Toggle _checkModUpdatesToggle;
        private ButtonRef _checkModUpdatesNowBtn;
        private Text _checkModUpdatesStatusLabel;

        // Exclusions section
        private GameObject _exclusionsListContainer;
        private InputFieldRef _manualPatternInput;
        private Text _exclusionsStatusLabel;

        // Fonts section
        private GameObject _fontsListContainer;
        private Text _fontsStatusLabel;
        private string[] _systemFonts;
        private List<SearchableDropdown> _fallbackDropdowns = new List<SearchableDropdown>();

        // Pending font changes (fontName -> (enabled, fallback, scale))
        private Dictionary<string, (bool enabled, string fallback, float scale)> _pendingFontSettings = new Dictionary<string, (bool, string, float)>();
        private Dictionary<string, (bool enabled, string fallback, float scale)> _initialFontSettings = new Dictionary<string, (bool, string, float)>();

        // Pending exclusion changes
        private HashSet<string> _pendingExclusionAdds = new HashSet<string>();
        private HashSet<string> _pendingExclusionRemoves = new HashSet<string>();
        private HashSet<string> _initialExclusions = new HashSet<string>();

        // Apply button tracking
        private ButtonRef _applyBtn;
        private ConfigSnapshot _initialSnapshot;

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
            public bool enable_ollama;
            public string ollama_url;
            public string model;
            public string game_context;
            public bool strict_source_language;
            public bool online_mode;
            public bool check_update_on_start;
            public bool notify_updates;
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
                    enable_ollama = TranslatorCore.Config.enable_ollama,
                    ollama_url = TranslatorCore.Config.ollama_url ?? "http://localhost:11434",
                    model = TranslatorCore.Config.model ?? "qwen3:8b",
                    game_context = TranslatorCore.Config.game_context ?? "",
                    strict_source_language = TranslatorCore.Config.strict_source_language,
                    online_mode = TranslatorCore.Config.online_mode,
                    check_update_on_start = TranslatorCore.Config.sync.check_update_on_start,
                    notify_updates = TranslatorCore.Config.sync.notify_updates,
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
            var fontsTab = _tabBar.AddTab("Fonts");
            var exclusionsTab = _tabBar.AddTab("Exclusions");
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
            CreateFontsTabContent(fontsTab);
            CreateExclusionsTabContent(exclusionsTab);
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

            // Ollama section
            var ollamaSectionTitle = UIStyles.CreateSectionTitle(card, "OllamaLabel", "Ollama (Local AI)");
            RegisterExcluded(ollamaSectionTitle);

            var enableOllamaObj = UIFactory.CreateToggle(card, "EnableOllamaToggle", out _enableOllamaToggle, out var enableLabel);
            enableLabel.text = " Enable Ollama";
            enableLabel.color = UIStyles.TextPrimary;
            UIHelpers.AddToggleListener(_enableOllamaToggle, OnOllamaToggleChanged);
            UIFactory.SetLayoutElement(enableOllamaObj, minHeight: UIStyles.RowHeightMedium);
            RegisterExcluded(enableLabel);

            UIStyles.CreateSpacer(card, 5);

            // URL row
            var urlRow = UIStyles.CreateFormRow(card, "UrlRow", UIStyles.InputHeight, 5);

            var urlLabel = UIFactory.CreateLabel(urlRow, "UrlLabel", "URL:", TextAnchor.MiddleLeft);
            urlLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(urlLabel.gameObject, minWidth: 45);
            RegisterExcluded(urlLabel);

            _ollamaUrlInput = UIFactory.CreateInputField(urlRow, "OllamaUrl", "http://localhost:11434");
            UIFactory.SetLayoutElement(_ollamaUrlInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_ollamaUrlInput.Component.gameObject, UIStyles.InputBackground);

            var testBtn = CreateSecondaryButton(urlRow, "TestBtn", "Test", 60);
            testBtn.OnClick += TestOllamaConnection;
            RegisterUIText(testBtn.ButtonText);

            _ollamaTestStatusLabel = UIFactory.CreateLabel(card, "TestStatus", "", TextAnchor.MiddleLeft);
            _ollamaTestStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_ollamaTestStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(_ollamaTestStatusLabel);

            // Model row
            var modelRow = UIStyles.CreateFormRow(card, "ModelRow", UIStyles.InputHeight, 5);

            var modelLabel = UIFactory.CreateLabel(modelRow, "ModelLabel", "Model:", TextAnchor.MiddleLeft);
            modelLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modelLabel.gameObject, minWidth: 50);
            RegisterUIText(modelLabel);

            _modelInput = UIFactory.CreateInputField(modelRow, "ModelInput", "qwen3:8b");
            UIFactory.SetLayoutElement(_modelInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_modelInput.Component.gameObject, UIStyles.InputBackground);

            var modelHint = UIStyles.CreateHint(card, "ModelHint", "Recommended: qwen3:8b");
            RegisterExcluded(modelHint);

            UIStyles.CreateSpacer(card, 5);

            // Game context
            var contextLabel = UIFactory.CreateLabel(card, "ContextLabel", "Game Context (optional):", TextAnchor.MiddleLeft);
            contextLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(contextLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(contextLabel);

            _gameContextInput = UIFactory.CreateInputField(card, "ContextInput", "e.g., RPG game with medieval setting");
            _gameContextInput.Component.lineType = UnityEngine.UI.InputField.LineType.MultiLineNewline;
            UIFactory.SetLayoutElement(_gameContextInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.MultiLineMedium);
            UIStyles.SetBackground(_gameContextInput.Component.gameObject, UIStyles.InputBackground);

            var contextHint = UIStyles.CreateHint(card, "ContextHint", "Helps Ollama understand game vocabulary");
            RegisterExcluded(contextHint);

            UIStyles.CreateSpacer(card, 5);

            // Strict source language toggle
            var strictObj = UIFactory.CreateToggle(card, "StrictSourceToggle", out _strictSourceToggle, out var strictLabel);
            strictLabel.text = " Strict source language detection";
            strictLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(strictObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(strictLabel);

            var strictHint = UIStyles.CreateHint(card, "StrictHint", "Skip texts not matching source language");
            RegisterUIText(strictHint);
        }

        private void CreateFontsTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "FontsCard", PanelWidth - 60, stretchVertically: true);

            // Header and explanation
            var sectionTitle = UIStyles.CreateSectionTitle(card, "FontsLabel", "Font Management");
            RegisterUIText(sectionTitle);

            var explainHint = UIStyles.CreateHint(card, "FontsHint", "Configure translation for detected fonts. Add fallback fonts for non-Latin scripts (Hindi, Arabic, Chinese, etc.). Settings are saved with translations.");
            RegisterUIText(explainHint);

            UIStyles.CreateSpacer(card, 10);

            // Refresh button
            var refreshRow = UIStyles.CreateFormRow(card, "RefreshRow", UIStyles.RowHeightNormal, 5);

            var refreshBtn = CreateSecondaryButton(refreshRow, "RefreshFontsBtn", "Refresh List", 100);
            refreshBtn.OnClick += RefreshFontsList;
            RegisterUIText(refreshBtn.ButtonText);

            _fontsStatusLabel = UIFactory.CreateLabel(refreshRow, "FontsStatus", "", TextAnchor.MiddleLeft);
            _fontsStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_fontsStatusLabel.gameObject, flexibleWidth: 9999);

            UIStyles.CreateSpacer(card, 10);

            // Detected fonts list
            var listLabel = UIFactory.CreateLabel(card, "FontsListLabel", "Detected Fonts:", TextAnchor.MiddleLeft);
            listLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(listLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(listLabel);

            // Scrollable container for fonts
            var scrollObj = UIFactory.CreateScrollView(card, "FontsScroll", out var scrollContent, out var scrollbar);
            UIFactory.SetLayoutElement(scrollObj, minHeight: 180, flexibleHeight: 9999, flexibleWidth: 9999);
            UIStyles.SetBackground(scrollObj, UIStyles.InputBackground);

            _fontsListContainer = scrollContent;
        }

        private void RefreshFontsList()
        {
            if (_fontsListContainer == null) return;

            TranslatorCore.LogInfo($"[OptionsPanel] RefreshFontsList called");

            // Clean up searchable dropdowns first
            foreach (var dropdown in _fallbackDropdowns)
            {
                dropdown.Destroy();
            }
            _fallbackDropdowns.Clear();

            // Clear existing items
            foreach (Transform child in _fontsListContainer.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            var fonts = FontManager.GetDetectedFontsInfo();
            TranslatorCore.LogInfo($"[OptionsPanel] fonts.Count={fonts.Count}");

            if (fonts.Count == 0)
            {
                var emptyLabel = UIFactory.CreateLabel(_fontsListContainer, "EmptyLabel", "No fonts detected yet. Play the game to detect fonts.", TextAnchor.MiddleCenter);
                emptyLabel.color = UIStyles.TextMuted;
                emptyLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(emptyLabel.gameObject, minHeight: 60, flexibleWidth: 9999);
                RegisterUIText(emptyLabel);

                if (_fontsStatusLabel != null)
                {
                    _fontsStatusLabel.text = "0 fonts detected";
                    _fontsStatusLabel.color = UIStyles.TextMuted;
                }
                return;
            }

            // Cache system fonts if not already done
            if (_systemFonts == null)
            {
                _systemFonts = FontManager.SystemFonts;
            }

            foreach (var fontInfo in fonts)
            {
                CreateFontRow(fontInfo);
            }

            if (_fontsStatusLabel != null)
            {
                _fontsStatusLabel.text = $"{fonts.Count} font(s) detected";
                _fontsStatusLabel.color = UIStyles.StatusSuccess;
            }
        }

        private void CreateFontRow(FontDisplayInfo fontInfo)
        {
            TranslatorCore.LogInfo($"[OptionsPanel] CreateFontRow: {fontInfo.Name}, Enabled={fontInfo.Enabled}, SupportsFallback={fontInfo.SupportsFallback}");

            // Main row container with padding
            var row = UIFactory.CreateVerticalGroup(_fontsListContainer, $"FontRow_{fontInfo.Name.GetHashCode()}",
                false, false, true, true, 3, new Vector4(5, 5, 5, 5), UIStyles.CardBackground, TextAnchor.UpperLeft);
            UIFactory.SetLayoutElement(row, minHeight: 55, flexibleWidth: 9999);

            // Header row: font name + type + enable toggle
            var headerRow = UIStyles.CreateFormRow(row, "HeaderRow", UIStyles.RowHeightNormal, 5);

            // Font name and type
            var fontLabel = UIFactory.CreateLabel(headerRow, "FontLabel", $"{fontInfo.Name} ({fontInfo.Type})", TextAnchor.MiddleLeft);
            fontLabel.color = UIStyles.TextPrimary;
            fontLabel.fontSize = UIStyles.FontSizeNormal;
            UIFactory.SetLayoutElement(fontLabel.gameObject, flexibleWidth: 9999);

            // Enable toggle
            var toggleObj = UIFactory.CreateToggle(headerRow, "EnableToggle", out var enableToggle, out var toggleLabel);
            toggleLabel.text = " Translate";
            toggleLabel.color = UIStyles.TextSecondary;
            toggleLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(toggleObj, minWidth: 80);
            enableToggle.isOn = fontInfo.Enabled;

            // Capture values for closure
            string capturedFontName = fontInfo.Name;
            TranslatorCore.LogInfo($"[OptionsPanel] Adding toggle listener for font: {capturedFontName}");
            UIHelpers.AddToggleListener(enableToggle, (isOn) => OnFontEnableChanged(capturedFontName, isOn));

            // Fallback row (only for fonts that support it)
            if (fontInfo.SupportsFallback)
            {
                var fallbackRow = UIStyles.CreateFormRow(row, "FallbackRow", UIStyles.RowHeightNormal, 5);

                var fallbackLabel = UIFactory.CreateLabel(fallbackRow, "FallbackLabel", "Fallback:", TextAnchor.MiddleLeft);
                fallbackLabel.color = UIStyles.TextSecondary;
                fallbackLabel.fontSize = UIStyles.FontSizeSmall;
                UIFactory.SetLayoutElement(fallbackLabel.gameObject, minWidth: 55);

                // Build options array based on font type
                // TMP (alt) fonts can only use fonts already loaded in the game (incompatible type)
                // Standard TMP and Unity fonts can use system fonts (we can create TMP_FontAsset)
                var options = new List<string> { "(None)" };
                string[] availableFonts = null;

                TranslatorCore.LogInfo($"[OptionsPanel] Font '{capturedFontName}' type='{fontInfo.Type}', checking for TMP (alt)...");

                if (fontInfo.Type == "TMP (alt)")
                {
                    // For alternate TMP (TMProOld, etc.), show fonts already loaded in the game
                    availableFonts = TranslatorPatches.GetAlternateTMPFontNames();
                    TranslatorCore.LogInfo($"[OptionsPanel] TMP (alt) font: found {availableFonts.Length} alternate fonts");
                }
                else
                {
                    // For standard TMP and Unity fonts, use system fonts
                    TranslatorCore.LogInfo($"[OptionsPanel] Non-TMP (alt) font, using system fonts");
                    availableFonts = _systemFonts;
                }

                if (availableFonts != null && availableFonts.Length > 0)
                {
                    options.AddRange(availableFonts);
                }

                // Add custom fonts (user-provided SDF fonts from fonts/ folder)
                // Custom fonts only work with TMP fonts (standard or alt), NOT Unity Fonts
                // because they are SDF-based TMP_FontAsset, not regular Font
                bool isTMPFont = fontInfo.Type == "TMP" || fontInfo.Type == "TextMeshPro" || fontInfo.Type == "TMP (alt)";
                string[] customFonts = null;
                if (isTMPFont)
                {
                    customFonts = FontManager.GetCustomFontNames();
                    if (customFonts != null && customFonts.Length > 0)
                    {
                        // Add separator if there are other fonts
                        if (options.Count > 1)
                        {
                            options.Add("--- Custom Fonts ---");
                        }
                        foreach (var customFont in customFonts)
                        {
                            options.Add("[Custom] " + customFont);
                        }
                        TranslatorCore.LogInfo($"[OptionsPanel] Added {customFonts.Length} custom font(s) to dropdown");
                    }
                }

                // If no fonts available at all (no system fonts, no game fonts, no custom fonts)
                if (options.Count <= 1)
                {
                    var noFontsLabel = UIFactory.CreateLabel(fallbackRow, "NoFontsLabel", "(no fonts available)", TextAnchor.MiddleLeft);
                    noFontsLabel.color = UIStyles.TextMuted;
                    noFontsLabel.fontSize = UIStyles.FontSizeSmall;
                    noFontsLabel.fontStyle = FontStyle.Italic;
                    return;
                }

                // Determine initial value
                string initialValue = "(None)";
                if (!string.IsNullOrEmpty(fontInfo.FallbackFont))
                {
                    // Check if the configured fallback is in the available fonts or custom fonts
                    bool foundInList = (availableFonts != null && Array.Exists(availableFonts, f => f == fontInfo.FallbackFont));
                    bool foundInCustom = (customFonts != null && Array.Exists(customFonts, f => "[Custom] " + f == fontInfo.FallbackFont || f == fontInfo.FallbackFont));

                    if (foundInList || foundInCustom)
                    {
                        initialValue = fontInfo.FallbackFont;
                    }
                    else
                    {
                        // Fallback not in current list - could be from different font type
                        // Still show it but it won't work until user changes it
                        initialValue = fontInfo.FallbackFont;
                        options.Add(fontInfo.FallbackFont + " (incompatible)");
                    }
                }

                // Create searchable dropdown with filter
                var dropdown = new SearchableDropdown(
                    $"Fallback_{capturedFontName}",
                    options.ToArray(),
                    initialValue,
                    popupHeight: 250,
                    showSearch: true
                );

                dropdown.CreateUI(fallbackRow, (selectedValue) =>
                {
                    // Handle incompatible marker
                    if (selectedValue != null && selectedValue.EndsWith(" (incompatible)"))
                    {
                        selectedValue = selectedValue.Replace(" (incompatible)", "");
                    }
                    string fallback = selectedValue == "(None)" ? null : selectedValue;
                    OnFontFallbackChanged(capturedFontName, fallback);
                }, width: 200);

                _fallbackDropdowns.Add(dropdown);
            }
            else
            {
                // Show hint for non-TMP fonts
                var noFallbackLabel = UIFactory.CreateLabel(row, "NoFallbackLabel", "Fallback not supported for this font type", TextAnchor.MiddleLeft);
                noFallbackLabel.color = UIStyles.TextMuted;
                noFallbackLabel.fontSize = UIStyles.FontSizeSmall;
                noFallbackLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(noFallbackLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            }

            // Size scale row (for all fonts)
            var scaleRow = UIStyles.CreateFormRow(row, "ScaleRow", UIStyles.RowHeightNormal, 5);

            var scaleLabel = UIFactory.CreateLabel(scaleRow, "ScaleLabel", "Size:", TextAnchor.MiddleLeft);
            scaleLabel.color = UIStyles.TextSecondary;
            scaleLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(scaleLabel.gameObject, minWidth: 55);

            // Scale dropdown - no search needed for small list
            var scaleDropdown = new SearchableDropdown(
                $"Scale_{capturedFontName}",
                _scaleOptions,
                ScaleToString(fontInfo.Scale),
                popupHeight: 200,
                showSearch: false
            );

            scaleDropdown.CreateUI(scaleRow, (selectedValue) =>
            {
                float scale = StringToScale(selectedValue);
                OnFontScaleChanged(capturedFontName, scale);
            }, width: 80);
        }

        // Scale dropdown helpers
        private static readonly string[] _scaleOptions = { "50%", "60%", "70%", "80%", "90%", "100%", "110%", "120%", "130%", "140%", "150%", "175%", "200%" };
        private static readonly float[] _scaleValues = { 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.75f, 2.0f };

        private static string ScaleToString(float scale)
        {
            for (int i = 0; i < _scaleValues.Length; i++)
            {
                if (Math.Abs(_scaleValues[i] - scale) < 0.01f)
                    return _scaleOptions[i];
            }
            return "100%"; // Default to 100%
        }

        private static float StringToScale(string scaleStr)
        {
            for (int i = 0; i < _scaleOptions.Length; i++)
            {
                if (_scaleOptions[i] == scaleStr)
                    return _scaleValues[i];
            }
            return 1.0f; // Default to 100%
        }

        private void OnFontEnableChanged(string fontName, bool enabled)
        {
            TranslatorCore.LogInfo($"[OptionsPanel] OnFontEnableChanged: font={fontName}, enabled={enabled}");

            // Get current pending state or initial state
            string fallback;
            float scale;
            if (_pendingFontSettings.TryGetValue(fontName, out var pending))
            {
                fallback = pending.fallback;
                scale = pending.scale;
                TranslatorCore.LogInfo($"[OptionsPanel] Using pending fallback: {fallback ?? "(null)"}, scale: {scale}");
            }
            else if (_initialFontSettings.TryGetValue(fontName, out var initial))
            {
                fallback = initial.fallback;
                scale = initial.scale;
                TranslatorCore.LogInfo($"[OptionsPanel] Using initial fallback: {fallback ?? "(null)"}, scale: {scale}, initial.enabled={initial.enabled}");
            }
            else
            {
                var settings = FontManager.GetFontSettings(fontName);
                fallback = settings?.fallback;
                scale = settings?.scale ?? 1.0f;
                TranslatorCore.LogInfo($"[OptionsPanel] Using FontManager fallback: {fallback ?? "(null)"}, scale: {scale}");
            }

            // Store pending change
            _pendingFontSettings[fontName] = (enabled, fallback, scale);
            TranslatorCore.LogInfo($"[OptionsPanel] Stored pending: ({enabled}, {fallback ?? "(null)"}, {scale}), total pending fonts: {_pendingFontSettings.Count}");

            if (_fontsStatusLabel != null)
            {
                _fontsStatusLabel.text = enabled ? $"Translation enabled for {fontName}" : $"Translation disabled for {fontName}";
                _fontsStatusLabel.color = UIStyles.TextSecondary;
            }

            UpdateApplyButtonText();
        }

        private void OnFontFallbackChanged(string fontName, string fallbackFont)
        {
            // Get current pending state or initial state
            bool enabled;
            float scale;
            if (_pendingFontSettings.TryGetValue(fontName, out var pending))
            {
                enabled = pending.enabled;
                scale = pending.scale;
            }
            else if (_initialFontSettings.TryGetValue(fontName, out var initial))
            {
                enabled = initial.enabled;
                scale = initial.scale;
            }
            else
            {
                var settings = FontManager.GetFontSettings(fontName);
                enabled = settings?.enabled ?? true;
                scale = settings?.scale ?? 1.0f;
            }

            // Store pending change
            _pendingFontSettings[fontName] = (enabled, fallbackFont, scale);

            if (_fontsStatusLabel != null)
            {
                if (string.IsNullOrEmpty(fallbackFont))
                {
                    _fontsStatusLabel.text = $"Fallback will be removed from {fontName}";
                }
                else
                {
                    _fontsStatusLabel.text = $"Fallback '{fallbackFont}' will be applied to {fontName}";
                }
                _fontsStatusLabel.color = UIStyles.TextSecondary;
            }

            UpdateApplyButtonText();
        }

        private void OnFontScaleChanged(string fontName, float scale)
        {
            TranslatorCore.LogInfo($"[OptionsPanel] OnFontScaleChanged: font={fontName}, scale={scale}");

            // Get current pending state or initial state
            bool enabled;
            string fallback;
            if (_pendingFontSettings.TryGetValue(fontName, out var pending))
            {
                enabled = pending.enabled;
                fallback = pending.fallback;
            }
            else if (_initialFontSettings.TryGetValue(fontName, out var initial))
            {
                enabled = initial.enabled;
                fallback = initial.fallback;
            }
            else
            {
                var settings = FontManager.GetFontSettings(fontName);
                enabled = settings?.enabled ?? true;
                fallback = settings?.fallback;
            }

            // Store pending change
            _pendingFontSettings[fontName] = (enabled, fallback, scale);

            if (_fontsStatusLabel != null)
            {
                int scalePercent = Mathf.RoundToInt(scale * 100f);
                _fontsStatusLabel.text = $"Font size {scalePercent}% will be applied to {fontName}";
                _fontsStatusLabel.color = UIStyles.TextSecondary;
            }

            UpdateApplyButtonText();
        }

        private void CreateExclusionsTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "ExclusionsCard", PanelWidth - 60, stretchVertically: true);

            // Header and explanation
            var sectionTitle = UIStyles.CreateSectionTitle(card, "ExclusionsLabel", "UI Exclusions");
            RegisterUIText(sectionTitle);

            var explainHint = UIStyles.CreateHint(card, "ExclusionsHint", "Exclude UI elements from translation (chat windows, player names, etc.). Exclusions are shared when you upload your translation.");
            RegisterUIText(explainHint);

            UIStyles.CreateSpacer(card, 10);

            // Inspector button
            var inspectorBtn = CreatePrimaryButton(card, "InspectorBtn", "Start Inspector Mode", PanelWidth - 100);
            inspectorBtn.OnClick += OnStartInspectorClicked;
            RegisterUIText(inspectorBtn.ButtonText);

            var inspectorHint = UIStyles.CreateHint(card, "InspectorHint", "Click on UI elements to exclude them");
            RegisterUIText(inspectorHint);

            UIStyles.CreateSpacer(card, 10);

            // Manual add section
            var manualLabel = UIFactory.CreateLabel(card, "ManualLabel", "Add pattern manually:", TextAnchor.MiddleLeft);
            manualLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(manualLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(manualLabel);

            var addRow = UIStyles.CreateFormRow(card, "AddRow", UIStyles.InputHeight, 5);

            _manualPatternInput = UIFactory.CreateInputField(addRow, "PatternInput", "e.g., **/ChatPanel/**");
            UIFactory.SetLayoutElement(_manualPatternInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_manualPatternInput.Component.gameObject, UIStyles.InputBackground);

            var addBtn = CreateSecondaryButton(addRow, "AddBtn", "Add", 60);
            addBtn.OnClick += OnAddManualPatternClicked;
            RegisterUIText(addBtn.ButtonText);

            var patternHint = UIStyles.CreateHint(card, "PatternHint", "Use ** for any depth, * for single level");
            RegisterUIText(patternHint);

            UIStyles.CreateSpacer(card, 10);

            // Current exclusions list
            var listLabel = UIFactory.CreateLabel(card, "ListLabel", "Current Exclusions:", TextAnchor.MiddleLeft);
            listLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(listLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(listLabel);

            // Scrollable container for exclusions
            var scrollObj = UIFactory.CreateScrollView(card, "ExclusionsScroll", out var scrollContent, out var scrollbar);
            UIFactory.SetLayoutElement(scrollObj, minHeight: 120, flexibleHeight: 9999, flexibleWidth: 9999);
            UIStyles.SetBackground(scrollObj, UIStyles.InputBackground);

            _exclusionsListContainer = scrollContent;

            // Status label
            _exclusionsStatusLabel = UIFactory.CreateLabel(card, "ExclusionsStatus", "", TextAnchor.MiddleLeft);
            _exclusionsStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_exclusionsStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
        }

        private void OnStartInspectorClicked()
        {
            // Close options panel and open inspector panel
            SetActive(false);
            TranslatorUIManager.OpenInspectorPanel();
        }

        private void OnAddManualPatternClicked()
        {
            string pattern = _manualPatternInput.Text?.Trim();

            if (string.IsNullOrEmpty(pattern))
            {
                _exclusionsStatusLabel.text = "Enter a pattern first";
                _exclusionsStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            // Check if already exists (in current list or pending adds)
            bool alreadyExists = TranslatorCore.UserExclusions.Contains(pattern) ||
                                 _pendingExclusionAdds.Contains(pattern);
            bool wasRemoved = _pendingExclusionRemoves.Contains(pattern);

            if (alreadyExists && !wasRemoved)
            {
                _exclusionsStatusLabel.text = "Pattern already exists";
                _exclusionsStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            // If it was pending removal, just cancel the removal
            if (wasRemoved)
            {
                _pendingExclusionRemoves.Remove(pattern);
            }
            else
            {
                _pendingExclusionAdds.Add(pattern);
            }

            _manualPatternInput.Text = "";
            _exclusionsStatusLabel.text = "Pattern will be added on Apply";
            _exclusionsStatusLabel.color = UIStyles.TextSecondary;

            RefreshExclusionsList();
            UpdateApplyButtonText();
        }

        private void RefreshExclusionsList()
        {
            if (_exclusionsListContainer == null) return;

            // Clear existing items
            foreach (Transform child in _exclusionsListContainer.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            // Build effective list: current - pending removes + pending adds
            var effectiveExclusions = new List<(string pattern, bool isPending, bool isRemoved)>();

            // Add current exclusions (mark removed ones)
            foreach (var pattern in TranslatorCore.UserExclusions)
            {
                bool isRemoved = _pendingExclusionRemoves.Contains(pattern);
                effectiveExclusions.Add((pattern, false, isRemoved));
            }

            // Add pending additions
            foreach (var pattern in _pendingExclusionAdds)
            {
                effectiveExclusions.Add((pattern, true, false));
            }

            TranslatorCore.LogInfo($"[OptionsPanel] UserExclusions count: {effectiveExclusions.Count}");

            if (effectiveExclusions.Count == 0)
            {
                var emptyLabel = UIFactory.CreateLabel(_exclusionsListContainer, "EmptyLabel", "No exclusions defined", TextAnchor.MiddleCenter);
                emptyLabel.color = UIStyles.TextMuted;
                emptyLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(emptyLabel.gameObject, minHeight: 40, flexibleWidth: 9999);
                return;
            }

            foreach (var (pattern, isPending, isRemoved) in effectiveExclusions)
            {
                var row = UIStyles.CreateFormRow(_exclusionsListContainer, $"Row_{pattern.GetHashCode()}", UIStyles.RowHeightNormal, 5);

                // Show pattern with visual indicator for pending state
                string displayText = pattern;
                if (isPending) displayText = $"+ {pattern}";
                else if (isRemoved) displayText = $"- {pattern}";

                var patternLabel = UIFactory.CreateLabel(row, "PatternLabel", displayText, TextAnchor.MiddleLeft);
                patternLabel.fontSize = UIStyles.FontSizeSmall;
                UIFactory.SetLayoutElement(patternLabel.gameObject, flexibleWidth: 9999);

                // Set color based on state
                if (isPending)
                {
                    patternLabel.color = UIStyles.StatusSuccess;
                }
                else if (isRemoved)
                {
                    patternLabel.color = UIStyles.StatusError;
                    // Note: Unity doesn't support strikethrough, using color only
                }
                else
                {
                    patternLabel.color = UIStyles.TextPrimary;
                }

                var deleteBtn = CreateSecondaryButton(row, "DeleteBtn", isRemoved ? "â†©" : "X", 30);
                var capturedPattern = pattern;
                var capturedIsRemoved = isRemoved;

                if (isRemoved)
                {
                    // Undo removal
                    deleteBtn.OnClick += () =>
                    {
                        _pendingExclusionRemoves.Remove(capturedPattern);
                        RefreshExclusionsList();
                        UpdateApplyButtonText();
                    };
                }
                else
                {
                    deleteBtn.OnClick += () => OnDeleteExclusionClicked(capturedPattern);
                }
            }
        }

        private void OnDeleteExclusionClicked(string pattern)
        {
            // If it was a pending add, just remove from pending
            if (_pendingExclusionAdds.Contains(pattern))
            {
                _pendingExclusionAdds.Remove(pattern);
                _exclusionsStatusLabel.text = "Pending pattern cancelled";
                _exclusionsStatusLabel.color = UIStyles.TextSecondary;
            }
            else
            {
                // Mark for removal on Apply
                _pendingExclusionRemoves.Add(pattern);
                _exclusionsStatusLabel.text = "Pattern will be removed on Apply";
                _exclusionsStatusLabel.color = UIStyles.TextSecondary;
            }

            RefreshExclusionsList();
            UpdateApplyButtonText();
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
            _strictSourceToggle.interactable = !isAuto && _enableOllamaToggle.isOn && !_captureKeysOnlyToggle.isOn;
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
        }

        private void LoadCurrentSettings()
        {
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

            // Translation (Capture + Ollama)
            _captureKeysOnlyToggle.isOn = TranslatorCore.Config.capture_keys_only;
            _enableOllamaToggle.isOn = TranslatorCore.Config.enable_ollama;
            _ollamaUrlInput.Text = TranslatorCore.Config.ollama_url ?? "http://localhost:11434";
            _modelInput.Text = TranslatorCore.Config.model ?? "qwen3:8b";
            _gameContextInput.Text = TranslatorCore.Config.game_context ?? "";
            _strictSourceToggle.isOn = TranslatorCore.Config.strict_source_language;
            _ollamaTestStatusLabel.text = "";
            UpdateOllamaInteractable();

            // Online mode
            _onlineModeToggle.isOn = TranslatorCore.Config.online_mode;
            _checkUpdatesToggle.isOn = TranslatorCore.Config.sync.check_update_on_start;
            _notifyUpdatesToggle.isOn = TranslatorCore.Config.sync.notify_updates;
            _autoDownloadToggle.isOn = TranslatorCore.Config.sync.auto_download;
            _checkModUpdatesToggle.isOn = TranslatorCore.Config.sync.check_mod_updates;
            OnOnlineModeChanged(_onlineModeToggle.isOn);

            // Advanced settings (per-game, stored in translations.json)
            _disableEventSystemOverrideToggle.isOn = TranslatorCore.DisableEventSystemOverride;

            // Update strict toggle based on source language
            OnSourceLanguageChanged(_sourceLanguageDropdown.SelectedValue);

            // Lock languages if translation exists on server
            UpdateLanguagesLocked();

            // Refresh exclusions list
            RefreshExclusionsList();

            // Refresh fonts list
            RefreshFontsList();

            // Capture initial font settings for change tracking
            _initialFontSettings.Clear();
            _pendingFontSettings.Clear();
            TranslatorCore.LogInfo($"[OptionsPanel] Capturing initial font settings...");
            foreach (var fontInfo in FontManager.GetDetectedFontsInfo())
            {
                var settings = FontManager.GetFontSettings(fontInfo.Name);
                var enabled = settings?.enabled ?? true;
                var fallback = settings?.fallback;
                var scale = settings?.scale ?? 1.0f;
                _initialFontSettings[fontInfo.Name] = (enabled, fallback, scale);
                TranslatorCore.LogInfo($"[OptionsPanel] Initial font: {fontInfo.Name} = (enabled={enabled}, fallback={fallback ?? "(null)"}, scale={scale})");
            }
            TranslatorCore.LogInfo($"[OptionsPanel] Total initial fonts: {_initialFontSettings.Count}");

            // Capture initial exclusions for change tracking
            _initialExclusions.Clear();
            _pendingExclusionAdds.Clear();
            _pendingExclusionRemoves.Clear();
            foreach (var pattern in TranslatorCore.UserExclusions)
            {
                _initialExclusions.Add(pattern);
            }

            // Take snapshot for change tracking and update button text
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
            UpdateOllamaInteractable();
        }

        private void OnOllamaToggleChanged(bool enabled)
        {
            UpdateOllamaInteractable();
        }

        private void UpdateOllamaInteractable()
        {
            bool usable = _enableOllamaToggle.isOn && !_captureKeysOnlyToggle.isOn;
            _ollamaUrlInput.Component.interactable = usable;
            _modelInput.Component.interactable = usable;
            _gameContextInput.Component.interactable = usable;

            bool sourceIsAuto = _sourceLanguageDropdown.SelectedValue == "auto (Detect)";
            _strictSourceToggle.interactable = usable && !sourceIsAuto;

            _enableOllamaToggle.interactable = !_captureKeysOnlyToggle.isOn;
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

        private async void TestOllamaConnection()
        {
            _ollamaTestStatusLabel.text = "Testing...";
            _ollamaTestStatusLabel.color = UIStyles.StatusWarning;

            string url = _ollamaUrlInput.Text;

            try
            {
                bool success = await TranslatorCore.TestOllamaConnection(url);

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success)
                    {
                        _ollamaTestStatusLabel.text = "Connection successful!";
                        _ollamaTestStatusLabel.color = UIStyles.StatusSuccess;
                    }
                    else
                    {
                        _ollamaTestStatusLabel.text = "Connection failed";
                        _ollamaTestStatusLabel.color = UIStyles.StatusError;
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _ollamaTestStatusLabel.text = $"Error: {errorMsg}";
                    _ollamaTestStatusLabel.color = UIStyles.StatusError;
                });
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

                // Translation (Capture + Ollama)
                TranslatorCore.Config.capture_keys_only = _captureKeysOnlyToggle.isOn;
                TranslatorCore.Config.enable_ollama = _enableOllamaToggle.isOn;
                TranslatorCore.Config.ollama_url = _ollamaUrlInput.Text;
                TranslatorCore.Config.model = _modelInput.Text;
                TranslatorCore.Config.game_context = _gameContextInput.Text;
                TranslatorCore.Config.strict_source_language = _strictSourceToggle.isOn;

                // Online mode
                TranslatorCore.Config.online_mode = _onlineModeToggle.isOn;
                TranslatorCore.Config.sync.check_update_on_start = _checkUpdatesToggle.isOn;
                TranslatorCore.Config.sync.notify_updates = _notifyUpdatesToggle.isOn;
                TranslatorCore.Config.sync.auto_download = _autoDownloadToggle.isOn;
                TranslatorCore.Config.sync.check_mod_updates = _checkModUpdatesToggle.isOn;

                // Advanced settings (per-game, stored in translations.json, requires restart)
                bool eventSystemChanged = TranslatorCore.DisableEventSystemOverride != _disableEventSystemOverrideToggle.isOn;
                TranslatorCore.DisableEventSystemOverride = _disableEventSystemOverrideToggle.isOn;

                // Apply pending font changes
                foreach (var kvp in _pendingFontSettings)
                {
                    FontManager.UpdateFontSettings(kvp.Key, kvp.Value.enabled, kvp.Value.fallback);
                    FontManager.UpdateFontScale(kvp.Key, kvp.Value.scale);
                }

                // Apply pending exclusion changes
                foreach (var pattern in _pendingExclusionAdds)
                {
                    TranslatorCore.AddExclusion(pattern);
                }
                foreach (var pattern in _pendingExclusionRemoves)
                {
                    TranslatorCore.RemoveExclusion(pattern);
                }

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

                if (_enableOllamaToggle.isOn)
                {
                    TranslatorCore.EnsureWorkerRunning();
                }
                else
                {
                    TranslatorCore.ClearQueue();
                }

                // Update snapshots after apply (no pending changes now)
                _initialSnapshot = ConfigSnapshot.FromConfig();

                // Update initial font settings
                _initialFontSettings.Clear();
                foreach (var fontInfo in FontManager.GetDetectedFontsInfo())
                {
                    var settings = FontManager.GetFontSettings(fontInfo.Name);
                    _initialFontSettings[fontInfo.Name] = (settings?.enabled ?? true, settings?.fallback, settings?.scale ?? 1.0f);
                }
                _pendingFontSettings.Clear();

                // Update initial exclusions
                _initialExclusions.Clear();
                foreach (var pattern in TranslatorCore.UserExclusions)
                {
                    _initialExclusions.Add(pattern);
                }
                _pendingExclusionAdds.Clear();
                _pendingExclusionRemoves.Clear();

                // Refresh lists to show applied state
                RefreshFontsList();
                RefreshExclusionsList();

                UpdateApplyButtonText();
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[Options] Failed to save settings: {e.Message}");
                _ollamaTestStatusLabel.text = $"Save failed: {e.Message}";
                _ollamaTestStatusLabel.color = UIStyles.StatusError;
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
        /// Sets up change listeners on all configurable controls to track pending changes.
        /// </summary>
        private void SetupChangeListeners()
        {
            // Toggles
            _enableTranslationsToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());
            _translateModUIToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());
            _captureKeysOnlyToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());
            _enableOllamaToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());
            _strictSourceToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());
            _onlineModeToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());
            _checkUpdatesToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());
            _notifyUpdatesToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());
            _autoDownloadToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());
            _checkModUpdatesToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());
            _disableEventSystemOverrideToggle.onValueChanged.AddListener(_ => UpdateApplyButtonText());

            // Input fields
            _ollamaUrlInput.OnValueChanged += _ => UpdateApplyButtonText();
            _modelInput.OnValueChanged += _ => UpdateApplyButtonText();
            _gameContextInput.OnValueChanged += _ => UpdateApplyButtonText();

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

            // Translation (Capture + Ollama)
            if (_captureKeysOnlyToggle.isOn != _initialSnapshot.capture_keys_only) count++;
            if (_enableOllamaToggle.isOn != _initialSnapshot.enable_ollama) count++;
            if (_ollamaUrlInput.Text != _initialSnapshot.ollama_url) count++;
            if (_modelInput.Text != _initialSnapshot.model) count++;
            if (_gameContextInput.Text != _initialSnapshot.game_context) count++;
            if (_strictSourceToggle.isOn != _initialSnapshot.strict_source_language) count++;

            // Online
            if (_onlineModeToggle.isOn != _initialSnapshot.online_mode) count++;
            if (_checkUpdatesToggle.isOn != _initialSnapshot.check_update_on_start) count++;
            if (_notifyUpdatesToggle.isOn != _initialSnapshot.notify_updates) count++;
            if (_autoDownloadToggle.isOn != _initialSnapshot.auto_download) count++;
            if (_checkModUpdatesToggle.isOn != _initialSnapshot.check_mod_updates) count++;

            // Advanced (per-game settings)
            if (_disableEventSystemOverrideToggle.isOn != _initialSnapshot.disable_eventsystem_override) count++;

            // Fonts - count fonts that differ from initial
            TranslatorCore.LogInfo($"[OptionsPanel] CountPendingChanges: _pendingFontSettings.Count={_pendingFontSettings.Count}, _initialFontSettings.Count={_initialFontSettings.Count}");
            foreach (var kvp in _pendingFontSettings)
            {
                if (_initialFontSettings.TryGetValue(kvp.Key, out var initial))
                {
                    bool enabledDiff = kvp.Value.enabled != initial.enabled;
                    bool fallbackDiff = kvp.Value.fallback != initial.fallback;
                    bool scaleDiff = Math.Abs(kvp.Value.scale - initial.scale) > 0.01f;
                    TranslatorCore.LogInfo($"[OptionsPanel] Font '{kvp.Key}': pending=({kvp.Value.enabled}, {kvp.Value.fallback ?? "(null)"}, {kvp.Value.scale}), initial=({initial.enabled}, {initial.fallback ?? "(null)"}, {initial.scale}), enabledDiff={enabledDiff}, fallbackDiff={fallbackDiff}, scaleDiff={scaleDiff}");
                    if (enabledDiff || fallbackDiff || scaleDiff)
                    {
                        count++;
                    }
                }
                else
                {
                    // New font not in initial - count as change
                    TranslatorCore.LogInfo($"[OptionsPanel] Font '{kvp.Key}' NOT in initial, counting as change");
                    count++;
                }
            }

            // Exclusions - count adds and removes
            count += _pendingExclusionAdds.Count;
            count += _pendingExclusionRemoves.Count;

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
