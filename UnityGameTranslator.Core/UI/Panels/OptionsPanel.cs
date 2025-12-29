using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Options/configuration panel with all settings.
    /// Matches old IMGUI OptionsWindow functionality.
    /// </summary>
    public class OptionsPanel : TranslatorPanelBase
    {
        public override string Name => "Options";
        public override int MinWidth => 500;
        public override int MinHeight => 400;
        public override int PanelWidth => 500;
        public override int PanelHeight => 580;

        protected override int MinPanelHeight => 400;

        // General section
        private Toggle _enableTranslationsToggle;
        private Toggle _captureKeysOnlyToggle;
        private Toggle _translateModUIToggle;
        private LanguageSelector _sourceLanguageSelector;
        private LanguageSelector _targetLanguageSelector;
        private string[] _languages;
        private string[] _sourceLanguages; // Includes "auto (Detect)"
        private GameObject _languagesLockedHint; // Shown when languages can't be changed

        // Hotkey section (reusable component)
        private HotkeyCapture _hotkeyCapture;

        // Online section
        private Toggle _onlineModeToggle;
        private Toggle _checkUpdatesToggle;
        private Toggle _notifyUpdatesToggle;
        private Toggle _autoDownloadToggle;
        private Toggle _checkModUpdatesToggle;

        // Ollama section
        private Toggle _enableOllamaToggle;
        private InputFieldRef _ollamaUrlInput;
        private InputFieldRef _modelInput;
        private InputFieldRef _gameContextInput;
        private Toggle _strictSourceToggle;
        private Text _ollamaTestStatusLabel;

        public OptionsPanel(UIBase owner) : base(owner)
        {
            // Note: Components initialized in ConstructPanelContent() - base constructor calls ConstructUI() first
        }

        protected override void ConstructPanelContent()
        {
            // Initialize components (must be here, not in constructor - base calls ConstructUI first)
            var langs = LanguageHelper.GetLanguageNames();

            // Source languages: "auto (Detect)" + all languages
            _sourceLanguages = new string[langs.Length + 1];
            _sourceLanguages[0] = "auto (Detect)";
            for (int i = 0; i < langs.Length; i++)
            {
                _sourceLanguages[i + 1] = langs[i];
            }

            // Target languages: "auto (System)" + all languages
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

            // Adaptive card for options - sizes to content
            var card = CreateAdaptiveCard(scrollContent, "OptionsCard", PanelWidth - 40);

            var title = CreateTitle(card, "Title", "Options");
            RegisterUIText(title);

            UIStyles.CreateSpacer(card, 5);

            // Sections (card is inside scroll view from CreateScrollablePanelLayout)
            CreateGeneralSection(card);
            CreateHotkeySection(card);
            CreateOnlineModeSection(card);
            CreateOllamaSection(card);

            // Buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);
            RegisterUIText(cancelBtn.ButtonText);

            var saveBtn = CreatePrimaryButton(buttonRow, "SaveBtn", "Save");
            saveBtn.OnClick += SaveSettings;
            RegisterUIText(saveBtn.ButtonText);
        }

        private void CreateGeneralSection(GameObject parent)
        {
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "GeneralLabel", "General");
            RegisterUIText(sectionTitle);

            var generalBox = CreateSection(parent, "GeneralBox");

            // Enable Translations toggle
            var transToggleObj = UIFactory.CreateToggle(generalBox, "EnableTranslationsToggle", out _enableTranslationsToggle, out var transLabel);
            transLabel.text = " Enable Translations";
            transLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(transToggleObj, minHeight: UIStyles.RowHeightMedium);
            RegisterUIText(transLabel);

            // Capture keys only toggle
            var captureObj = UIFactory.CreateToggle(generalBox, "CaptureKeysToggle", out _captureKeysOnlyToggle, out var captureLabel);
            captureLabel.text = " Capture keys only (no translation)";
            captureLabel.color = UIStyles.TextSecondary;
            _captureKeysOnlyToggle.onValueChanged.AddListener(OnCaptureKeysOnlyChanged);
            UIFactory.SetLayoutElement(captureObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(captureLabel);

            var captureHint = UIStyles.CreateHint(generalBox, "CaptureHint", "For manual translation: saves texts without translating");
            RegisterUIText(captureHint);

            UIStyles.CreateSpacer(generalBox, 5);

            // Translate mod UI toggle
            var modUIObj = UIFactory.CreateToggle(generalBox, "TranslateModUIToggle", out _translateModUIToggle, out var modUILabel);
            modUILabel.text = " Translate mod interface";
            modUILabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modUIObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(modUILabel);

            var modUIHint = UIStyles.CreateHint(generalBox, "ModUIHint", "Translate this mod's own buttons and labels");
            RegisterUIText(modUIHint);

            UIStyles.CreateSpacer(generalBox, 5);

            // Source Language label
            var sourceLangLabel = UIFactory.CreateLabel(generalBox, "SourceLangLabel", "Source Language:", TextAnchor.MiddleLeft);
            sourceLangLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(sourceLangLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(sourceLangLabel);

            // Source Language selector
            _sourceLanguageSelector.CreateUI(generalBox, OnSourceLanguageChanged);

            UIStyles.CreateSpacer(generalBox, 5);

            // Target Language label
            var targetLangLabel = UIFactory.CreateLabel(generalBox, "TargetLangLabel", "Target Language:", TextAnchor.MiddleLeft);
            targetLangLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(targetLangLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(targetLangLabel);

            // Target Language selector
            _targetLanguageSelector.CreateUI(generalBox);

            // Languages locked hint (hidden by default, shown when languages can't be changed)
            var lockedHint = UIStyles.CreateHint(generalBox, "LanguagesLockedHint", "Languages locked: translation already uploaded");
            lockedHint.color = UIStyles.StatusWarning;
            _languagesLockedHint = lockedHint.gameObject;
            _languagesLockedHint.SetActive(false);
        }

        private void OnSourceLanguageChanged(string newSource)
        {
            // Strict source language can only be enabled when source is NOT auto
            bool isAuto = newSource == "auto (Detect)";
            _strictSourceToggle.interactable = !isAuto && _enableOllamaToggle.isOn && !_captureKeysOnlyToggle.isOn;
            if (isAuto && _strictSourceToggle.isOn)
            {
                _strictSourceToggle.isOn = false;
            }
        }

        private void CreateHotkeySection(GameObject parent)
        {
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "HotkeyLabel", "Settings Hotkey");
            RegisterUIText(sectionTitle);

            var hotkeyBox = CreateSection(parent, "HotkeyBox");

            // Hotkey capture (reusable component)
            _hotkeyCapture.CreateUI(hotkeyBox);
        }

        private void CreateOnlineModeSection(GameObject parent)
        {
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "OnlineLabel", "Online Mode");
            RegisterUIText(sectionTitle);

            var onlineBox = CreateSection(parent, "OnlineBox");

            var onlineToggleObj = UIFactory.CreateToggle(onlineBox, "OnlineModeToggle", out _onlineModeToggle, out var onlineLabel);
            onlineLabel.text = " Enable Online Mode";
            onlineLabel.color = UIStyles.TextPrimary;
            _onlineModeToggle.onValueChanged.AddListener(OnOnlineModeChanged);
            UIFactory.SetLayoutElement(onlineToggleObj, minHeight: UIStyles.RowHeightMedium);
            RegisterUIText(onlineLabel);

            UIStyles.CreateSpacer(onlineBox, 5);

            // Translation sync options header
            var translationSyncLabel = UIFactory.CreateLabel(onlineBox, "TranslationSyncLabel", "Translation Sync:", TextAnchor.MiddleLeft);
            translationSyncLabel.color = UIStyles.TextMuted;
            translationSyncLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(translationSyncLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(translationSyncLabel);

            var checkUpdatesObj = UIFactory.CreateToggle(onlineBox, "CheckUpdatesToggle", out _checkUpdatesToggle, out var checkLabel);
            checkLabel.text = " Check for translation updates on start";
            checkLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(checkUpdatesObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(checkLabel);

            var notifyObj = UIFactory.CreateToggle(onlineBox, "NotifyToggle", out _notifyUpdatesToggle, out var notifyLabel);
            notifyLabel.text = " Notify when translation updates available";
            notifyLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(notifyObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(notifyLabel);

            var autoDownloadObj = UIFactory.CreateToggle(onlineBox, "AutoDownloadToggle", out _autoDownloadToggle, out var autoLabel);
            autoLabel.text = " Auto-download translation updates (no conflicts)";
            autoLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(autoDownloadObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(autoLabel);

            UIStyles.CreateSpacer(onlineBox, 5);

            // Mod updates header
            var modSyncLabel = UIFactory.CreateLabel(onlineBox, "ModSyncLabel", "Mod Updates:", TextAnchor.MiddleLeft);
            modSyncLabel.color = UIStyles.TextMuted;
            modSyncLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(modSyncLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(modSyncLabel);

            var modUpdatesObj = UIFactory.CreateToggle(onlineBox, "ModUpdatesToggle", out _checkModUpdatesToggle, out var modLabel);
            modLabel.text = " Check for mod updates on GitHub";
            modLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modUpdatesObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(modLabel);
        }

        private void CreateOllamaSection(GameObject parent)
        {
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "OllamaLabel", "Ollama (Local AI)");
            RegisterExcluded(sectionTitle); // "Ollama" is a brand name

            var ollamaBox = CreateSection(parent, "OllamaBox");

            var enableOllamaObj = UIFactory.CreateToggle(ollamaBox, "EnableOllamaToggle", out _enableOllamaToggle, out var enableLabel);
            enableLabel.text = " Enable Ollama";
            enableLabel.color = UIStyles.TextPrimary;
            _enableOllamaToggle.onValueChanged.AddListener(OnOllamaToggleChanged);
            UIFactory.SetLayoutElement(enableOllamaObj, minHeight: UIStyles.RowHeightMedium);
            RegisterExcluded(enableLabel); // "Ollama" is a brand name

            UIStyles.CreateSpacer(ollamaBox, 5);

            // URL row
            var urlRow = UIStyles.CreateFormRow(ollamaBox, "UrlRow", UIStyles.InputHeight, 5);

            var urlLabel = UIFactory.CreateLabel(urlRow, "UrlLabel", "URL:", TextAnchor.MiddleLeft);
            urlLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(urlLabel.gameObject, minWidth: 45);
            RegisterExcluded(urlLabel); // Technical term

            _ollamaUrlInput = UIFactory.CreateInputField(urlRow, "OllamaUrl", "http://localhost:11434");
            UIFactory.SetLayoutElement(_ollamaUrlInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_ollamaUrlInput.Component.gameObject, UIStyles.InputBackground);

            var testBtn = CreateSecondaryButton(urlRow, "TestBtn", "Test", 60);
            testBtn.OnClick += TestOllamaConnection;
            RegisterUIText(testBtn.ButtonText);

            _ollamaTestStatusLabel = UIFactory.CreateLabel(ollamaBox, "TestStatus", "", TextAnchor.MiddleLeft);
            _ollamaTestStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_ollamaTestStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(_ollamaTestStatusLabel);

            // Model row
            var modelRow = UIStyles.CreateFormRow(ollamaBox, "ModelRow", UIStyles.InputHeight, 5);

            var modelLabel = UIFactory.CreateLabel(modelRow, "ModelLabel", "Model:", TextAnchor.MiddleLeft);
            modelLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modelLabel.gameObject, minWidth: 50);
            RegisterUIText(modelLabel);

            _modelInput = UIFactory.CreateInputField(modelRow, "ModelInput", "qwen3:8b");
            UIFactory.SetLayoutElement(_modelInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_modelInput.Component.gameObject, UIStyles.InputBackground);

            var modelHint = UIStyles.CreateHint(ollamaBox, "ModelHint", "Recommended: qwen3:8b");
            RegisterExcluded(modelHint); // Technical model name

            UIStyles.CreateSpacer(ollamaBox, 5);

            // Game context
            var contextLabel = UIFactory.CreateLabel(ollamaBox, "ContextLabel", "Game Context (optional):", TextAnchor.MiddleLeft);
            contextLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(contextLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(contextLabel);

            _gameContextInput = UIFactory.CreateInputField(ollamaBox, "ContextInput", "e.g., RPG game with medieval setting");
            _gameContextInput.Component.lineType = UnityEngine.UI.InputField.LineType.MultiLineNewline;
            UIFactory.SetLayoutElement(_gameContextInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.MultiLineMedium);
            UIStyles.SetBackground(_gameContextInput.Component.gameObject, UIStyles.InputBackground);

            var contextHint = UIStyles.CreateHint(ollamaBox, "ContextHint", "Helps Ollama understand game vocabulary");
            RegisterExcluded(contextHint); // Contains "Ollama"

            UIStyles.CreateSpacer(ollamaBox, 5);

            // Strict source language toggle
            var strictObj = UIFactory.CreateToggle(ollamaBox, "StrictSourceToggle", out _strictSourceToggle, out var strictLabel);
            strictLabel.text = " Strict source language detection";
            strictLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(strictObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(strictLabel);

            var strictHint = UIStyles.CreateHint(ollamaBox, "StrictHint", "Skip texts not matching source language");
            RegisterUIText(strictHint);
        }

        public override void SetActive(bool active)
        {
            // Only load settings when transitioning from inactive to active
            // (PanelDragger calls SetActive(true) every frame when mouse is in drag/resize area)
            bool wasActive = Enabled;
            base.SetActive(active);
            if (active && !wasActive)
            {
                LoadCurrentSettings();
            }
        }

        public override void Update()
        {
            base.Update();

            // Update hotkey capture component
            _hotkeyCapture?.Update();
        }

        private void LoadCurrentSettings()
        {
            // General
            _enableTranslationsToggle.isOn = TranslatorCore.Config.enable_translations;
            _captureKeysOnlyToggle.isOn = TranslatorCore.Config.capture_keys_only;
            _translateModUIToggle.isOn = TranslatorCore.Config.translate_mod_ui;

            // Load source language via component
            string configSourceLang = TranslatorCore.Config.source_language;
            if (string.IsNullOrEmpty(configSourceLang) || configSourceLang == "auto")
            {
                _sourceLanguageSelector.SelectedLanguage = "auto (Detect)";
            }
            else
            {
                _sourceLanguageSelector.SelectedLanguage = configSourceLang;
            }

            // Load target language via component
            string configTargetLang = TranslatorCore.Config.target_language;
            if (string.IsNullOrEmpty(configTargetLang) || configTargetLang == "auto")
            {
                _targetLanguageSelector.SelectedLanguage = "auto (System)";
            }
            else
            {
                _targetLanguageSelector.SelectedLanguage = configTargetLang;
            }

            // Load hotkey via component
            _hotkeyCapture.SetHotkey(TranslatorCore.Config.settings_hotkey ?? "F10");

            // Online mode
            _onlineModeToggle.isOn = TranslatorCore.Config.online_mode;
            _checkUpdatesToggle.isOn = TranslatorCore.Config.sync.check_update_on_start;
            _notifyUpdatesToggle.isOn = TranslatorCore.Config.sync.notify_updates;
            _autoDownloadToggle.isOn = TranslatorCore.Config.sync.auto_download;
            _checkModUpdatesToggle.isOn = TranslatorCore.Config.sync.check_mod_updates;
            OnOnlineModeChanged(_onlineModeToggle.isOn);

            // Ollama
            _enableOllamaToggle.isOn = TranslatorCore.Config.enable_ollama;
            _ollamaUrlInput.Text = TranslatorCore.Config.ollama_url ?? "http://localhost:11434";
            _modelInput.Text = TranslatorCore.Config.model ?? "qwen3:8b";
            _gameContextInput.Text = TranslatorCore.Config.game_context ?? "";
            _strictSourceToggle.isOn = TranslatorCore.Config.strict_source_language;
            _ollamaTestStatusLabel.text = "";
            UpdateOllamaInteractable();

            // Update strict toggle based on source language
            OnSourceLanguageChanged(_sourceLanguageSelector.SelectedLanguage);

            // Lock languages if translation exists on server
            UpdateLanguagesLocked();
        }

        private void UpdateLanguagesLocked()
        {
            bool locked = TranslatorCore.AreLanguagesLocked;

            _sourceLanguageSelector.SetInteractable(!locked);
            _targetLanguageSelector.SetInteractable(!locked);

            if (_languagesLockedHint != null)
            {
                _languagesLockedHint.SetActive(locked);
            }
        }

        private void OnOnlineModeChanged(bool enabled)
        {
            _checkUpdatesToggle.interactable = enabled;
            _notifyUpdatesToggle.interactable = enabled;
            _autoDownloadToggle.interactable = enabled;
            _checkModUpdatesToggle.interactable = enabled;
        }

        private void OnCaptureKeysOnlyChanged(bool captureOnly)
        {
            // When capture keys only is enabled, Ollama section should be disabled
            UpdateOllamaInteractable();
        }

        private void OnOllamaToggleChanged(bool enabled)
        {
            UpdateOllamaInteractable();
        }

        private void UpdateOllamaInteractable()
        {
            // Ollama is usable only if enabled AND capture_keys_only is OFF
            bool usable = _enableOllamaToggle.isOn && !_captureKeysOnlyToggle.isOn;
            _ollamaUrlInput.Component.interactable = usable;
            _modelInput.Component.interactable = usable;
            _gameContextInput.Component.interactable = usable;

            // Strict source toggle: only when usable AND source is NOT auto
            bool sourceIsAuto = _sourceLanguageSelector.SelectedLanguage == "auto (Detect)";
            _strictSourceToggle.interactable = usable && !sourceIsAuto;

            // The enable toggle itself is disabled when capture mode is on
            _enableOllamaToggle.interactable = !_captureKeysOnlyToggle.isOn;
        }

        private async void TestOllamaConnection()
        {
            _ollamaTestStatusLabel.text = "Testing...";
            _ollamaTestStatusLabel.color = UIStyles.StatusWarning;

            try
            {
                bool success = await TranslatorCore.TestOllamaConnection(_ollamaUrlInput.Text);
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
            }
            catch (Exception e)
            {
                _ollamaTestStatusLabel.text = $"Error: {e.Message}";
                _ollamaTestStatusLabel.color = UIStyles.StatusError;
            }
        }

        private void SaveSettings()
        {
            TranslatorCore.LogInfo($"[Options] SaveSettings called - enable_translations toggle is: {_enableTranslationsToggle.isOn}");
            try
            {
                // General
                TranslatorCore.Config.enable_translations = _enableTranslationsToggle.isOn;
                TranslatorCore.Config.capture_keys_only = _captureKeysOnlyToggle.isOn;
                TranslatorCore.Config.translate_mod_ui = _translateModUIToggle.isOn;

                // Save source language (convert "auto (Detect)" back to "auto")
                string selectedSourceLang = _sourceLanguageSelector.SelectedLanguage;
                if (selectedSourceLang == "auto (Detect)")
                {
                    TranslatorCore.Config.source_language = "auto";
                }
                else
                {
                    TranslatorCore.Config.source_language = selectedSourceLang;
                }

                // Save target language (convert "auto (System)" back to "auto")
                string selectedTargetLang = _targetLanguageSelector.SelectedLanguage;
                if (selectedTargetLang == "auto (System)")
                {
                    TranslatorCore.Config.target_language = "auto";
                }
                else
                {
                    TranslatorCore.Config.target_language = selectedTargetLang;
                }

                // Save hotkey from component
                TranslatorCore.Config.settings_hotkey = _hotkeyCapture.HotkeyString;

                // Online mode
                TranslatorCore.Config.online_mode = _onlineModeToggle.isOn;
                TranslatorCore.Config.sync.check_update_on_start = _checkUpdatesToggle.isOn;
                TranslatorCore.Config.sync.notify_updates = _notifyUpdatesToggle.isOn;
                TranslatorCore.Config.sync.auto_download = _autoDownloadToggle.isOn;
                TranslatorCore.Config.sync.check_mod_updates = _checkModUpdatesToggle.isOn;

                // Ollama
                TranslatorCore.Config.enable_ollama = _enableOllamaToggle.isOn;
                TranslatorCore.Config.ollama_url = _ollamaUrlInput.Text;
                TranslatorCore.Config.model = _modelInput.Text;
                TranslatorCore.Config.game_context = _gameContextInput.Text;
                TranslatorCore.Config.strict_source_language = _strictSourceToggle.isOn;

                TranslatorCore.SaveConfig();
                TranslatorCore.LogInfo("[Options] Settings saved successfully");

                // Start Ollama worker if just enabled
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
