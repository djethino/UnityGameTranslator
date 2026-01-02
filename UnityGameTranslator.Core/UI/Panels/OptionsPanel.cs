using System;
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
        private LanguageSelector _sourceLanguageSelector;
        private LanguageSelector _targetLanguageSelector;
        private string[] _languages;
        private string[] _sourceLanguages;

        // Language section containers for conditional display
        private GameObject _languagesEditableSection;
        private GameObject _languagesLockedSection;
        private Text _lockedSourceLangValue;
        private Text _lockedTargetLangValue;

        // Interface section
        private Text _resetWindowsStatusLabel;

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

            _sourceLanguageSelector = new LanguageSelector("SourceLang", _sourceLanguages, "auto (Detect)", 100);
            _targetLanguageSelector = new LanguageSelector("TargetLang", _languages, "auto (System)", 100);
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
            CreateExclusionsTabContent(exclusionsTab);
            CreateOnlineTabContent(onlineTab);

            // Tab height will be fixed on first display (see SetActive)

            // Buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);
            RegisterUIText(cancelBtn.ButtonText);

            var applyBtn = CreatePrimaryButton(buttonRow, "ApplyBtn", "Apply");
            applyBtn.OnClick += ApplySettings;
            RegisterUIText(applyBtn.ButtonText);
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

            _sourceLanguageSelector.CreateUI(_languagesEditableSection, OnSourceLanguageChanged);

            UIStyles.CreateSpacer(_languagesEditableSection, 5);

            // Target Language
            var targetLangLabel = UIFactory.CreateLabel(_languagesEditableSection, "TargetLangLabel", "Target Language:", TextAnchor.MiddleLeft);
            targetLangLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(targetLangLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(targetLangLabel);

            _targetLanguageSelector.CreateUI(_languagesEditableSection);

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

            // Check if already exists
            if (TranslatorCore.UserExclusions.Contains(pattern))
            {
                _exclusionsStatusLabel.text = "Pattern already exists";
                _exclusionsStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            TranslatorCore.AddExclusion(pattern);
            _manualPatternInput.Text = "";
            _exclusionsStatusLabel.text = "Pattern added";
            _exclusionsStatusLabel.color = UIStyles.StatusSuccess;

            RefreshExclusionsList();
        }

        private void RefreshExclusionsList()
        {
            if (_exclusionsListContainer == null) return;

            // Clear existing items
            foreach (Transform child in _exclusionsListContainer.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            var exclusions = TranslatorCore.UserExclusions;
            TranslatorCore.LogInfo($"[OptionsPanel] UserExclusions count: {exclusions.Count}");

            if (exclusions.Count == 0)
            {
                var emptyLabel = UIFactory.CreateLabel(_exclusionsListContainer, "EmptyLabel", "No exclusions defined", TextAnchor.MiddleCenter);
                emptyLabel.color = UIStyles.TextMuted;
                emptyLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(emptyLabel.gameObject, minHeight: 40, flexibleWidth: 9999);
                return;
            }

            foreach (var pattern in exclusions)
            {
                var row = UIStyles.CreateFormRow(_exclusionsListContainer, $"Row_{pattern.GetHashCode()}", UIStyles.RowHeightNormal, 5);

                var patternLabel = UIFactory.CreateLabel(row, "PatternLabel", pattern, TextAnchor.MiddleLeft);
                patternLabel.color = UIStyles.TextPrimary;
                patternLabel.fontSize = UIStyles.FontSizeSmall;
                UIFactory.SetLayoutElement(patternLabel.gameObject, flexibleWidth: 9999);

                var deleteBtn = CreateSecondaryButton(row, "DeleteBtn", "X", 30);
                var capturedPattern = pattern; // Capture for closure
                deleteBtn.OnClick += () => OnDeleteExclusionClicked(capturedPattern);
            }
        }

        private void OnDeleteExclusionClicked(string pattern)
        {
            if (TranslatorCore.RemoveExclusion(pattern))
            {
                TranslatorCore.SaveCache();
                _exclusionsStatusLabel.text = "Pattern removed";
                _exclusionsStatusLabel.color = UIStyles.StatusSuccess;
                RefreshExclusionsList();
            }
            else
            {
                _exclusionsStatusLabel.text = "Failed to remove pattern";
                _exclusionsStatusLabel.color = UIStyles.StatusError;
            }
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
                _sourceLanguageSelector.SelectedLanguage = "auto (Detect)";
            }
            else
            {
                _sourceLanguageSelector.SelectedLanguage = configSourceLang;
            }

            // Target language
            string configTargetLang = TranslatorCore.Config.target_language;
            if (string.IsNullOrEmpty(configTargetLang) || configTargetLang == "auto")
            {
                _targetLanguageSelector.SelectedLanguage = "auto (System)";
            }
            else
            {
                _targetLanguageSelector.SelectedLanguage = configTargetLang;
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

            // Update strict toggle based on source language
            OnSourceLanguageChanged(_sourceLanguageSelector.SelectedLanguage);

            // Lock languages if translation exists on server
            UpdateLanguagesLocked();

            // Refresh exclusions list
            RefreshExclusionsList();
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

            bool sourceIsAuto = _sourceLanguageSelector.SelectedLanguage == "auto (Detect)";
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
                string selectedSourceLang = _sourceLanguageSelector.SelectedLanguage;
                TranslatorCore.Config.source_language = selectedSourceLang == "auto (Detect)" ? "auto" : selectedSourceLang;

                string selectedTargetLang = _targetLanguageSelector.SelectedLanguage;
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

                TranslatorCore.SaveConfig();
                TranslatorCore.LogInfo("[Options] Settings saved successfully");

                TranslatorCore.ClearProcessingCaches();

                if (_enableOllamaToggle.isOn)
                {
                    TranslatorCore.EnsureWorkerRunning();
                }
                else
                {
                    TranslatorCore.ClearQueue();
                }

                SetActive(false);
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[Options] Failed to save settings: {e.Message}");
                _ollamaTestStatusLabel.text = $"Save failed: {e.Message}";
                _ollamaTestStatusLabel.color = UIStyles.StatusError;
            }
        }
    }
}
