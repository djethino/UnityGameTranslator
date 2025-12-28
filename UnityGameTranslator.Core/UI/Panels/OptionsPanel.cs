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
        private LanguageSelector _languageSelector;
        private string[] _languages;

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
        private Text _ollamaTestStatusLabel;

        public OptionsPanel(UIBase owner) : base(owner)
        {
            // Note: Components initialized in ConstructPanelContent() - base constructor calls ConstructUI() first
        }

        protected override void ConstructPanelContent()
        {
            // Initialize components (must be here, not in constructor - base calls ConstructUI first)
            var langs = LanguageHelper.GetLanguageNames();
            _languages = new string[langs.Length + 1];
            _languages[0] = "auto (System)";
            for (int i = 0; i < langs.Length; i++)
            {
                _languages[i + 1] = langs[i];
            }
            _languageSelector = new LanguageSelector("TargetLang", _languages, "auto (System)", 100);
            _hotkeyCapture = new HotkeyCapture("F10");

            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Adaptive card for options - sizes to content
            var card = CreateAdaptiveCard(scrollContent, "OptionsCard", PanelWidth - 40);

            CreateTitle(card, "Title", "Options");

            UIStyles.CreateSpacer(card, 5);

            // Sections (card is inside scroll view from CreateScrollablePanelLayout)
            CreateGeneralSection(card);
            CreateHotkeySection(card);
            CreateOnlineModeSection(card);
            CreateOllamaSection(card);

            // Buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);

            var saveBtn = CreatePrimaryButton(buttonRow, "SaveBtn", "Save");
            saveBtn.OnClick += SaveSettings;
        }

        private void CreateGeneralSection(GameObject parent)
        {
            UIStyles.CreateSectionTitle(parent, "GeneralLabel", "General");

            var generalBox = CreateSection(parent, "GeneralBox");

            // Enable Translations toggle
            var transToggleObj = UIFactory.CreateToggle(generalBox, "EnableTranslationsToggle", out _enableTranslationsToggle, out var transLabel);
            transLabel.text = " Enable Translations";
            transLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(transToggleObj, minHeight: UIStyles.RowHeightMedium);

            UIStyles.CreateSpacer(generalBox, 5);

            // Target Language label
            var langLabel = UIFactory.CreateLabel(generalBox, "LangLabel", "Target Language:", TextAnchor.MiddleLeft);
            langLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(langLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Language selector (reusable component)
            _languageSelector.CreateUI(generalBox);
        }

        private void CreateHotkeySection(GameObject parent)
        {
            UIStyles.CreateSectionTitle(parent, "HotkeyLabel", "Settings Hotkey");

            var hotkeyBox = CreateSection(parent, "HotkeyBox");

            // Hotkey capture (reusable component)
            _hotkeyCapture.CreateUI(hotkeyBox);
        }

        private void CreateOnlineModeSection(GameObject parent)
        {
            UIStyles.CreateSectionTitle(parent, "OnlineLabel", "Online Mode");

            var onlineBox = CreateSection(parent, "OnlineBox");

            var onlineToggleObj = UIFactory.CreateToggle(onlineBox, "OnlineModeToggle", out _onlineModeToggle, out var onlineLabel);
            onlineLabel.text = " Enable Online Mode";
            onlineLabel.color = UIStyles.TextPrimary;
            _onlineModeToggle.onValueChanged.AddListener(OnOnlineModeChanged);
            UIFactory.SetLayoutElement(onlineToggleObj, minHeight: UIStyles.RowHeightMedium);

            UIStyles.CreateSpacer(onlineBox, 5);

            var checkUpdatesObj = UIFactory.CreateToggle(onlineBox, "CheckUpdatesToggle", out _checkUpdatesToggle, out var checkLabel);
            checkLabel.text = " Check for translation updates on start";
            checkLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(checkUpdatesObj, minHeight: UIStyles.RowHeightNormal);

            var notifyObj = UIFactory.CreateToggle(onlineBox, "NotifyToggle", out _notifyUpdatesToggle, out var notifyLabel);
            notifyLabel.text = " Notify when updates available";
            notifyLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(notifyObj, minHeight: UIStyles.RowHeightNormal);

            var autoDownloadObj = UIFactory.CreateToggle(onlineBox, "AutoDownloadToggle", out _autoDownloadToggle, out var autoLabel);
            autoLabel.text = " Auto-download updates (no conflicts)";
            autoLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(autoDownloadObj, minHeight: UIStyles.RowHeightNormal);

            var modUpdatesObj = UIFactory.CreateToggle(onlineBox, "ModUpdatesToggle", out _checkModUpdatesToggle, out var modLabel);
            modLabel.text = " Check for mod updates on GitHub";
            modLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modUpdatesObj, minHeight: UIStyles.RowHeightNormal);
        }

        private void CreateOllamaSection(GameObject parent)
        {
            UIStyles.CreateSectionTitle(parent, "OllamaLabel", "Ollama (Local AI)");

            var ollamaBox = CreateSection(parent, "OllamaBox");

            var enableOllamaObj = UIFactory.CreateToggle(ollamaBox, "EnableOllamaToggle", out _enableOllamaToggle, out var enableLabel);
            enableLabel.text = " Enable Ollama";
            enableLabel.color = UIStyles.TextPrimary;
            _enableOllamaToggle.onValueChanged.AddListener(OnOllamaToggleChanged);
            UIFactory.SetLayoutElement(enableOllamaObj, minHeight: UIStyles.RowHeightMedium);

            UIStyles.CreateSpacer(ollamaBox, 5);

            // URL row
            var urlRow = UIStyles.CreateFormRow(ollamaBox, "UrlRow", UIStyles.InputHeight, 5);

            var urlLabel = UIFactory.CreateLabel(urlRow, "UrlLabel", "URL:", TextAnchor.MiddleLeft);
            urlLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(urlLabel.gameObject, minWidth: 45);

            _ollamaUrlInput = UIFactory.CreateInputField(urlRow, "OllamaUrl", "http://localhost:11434");
            UIFactory.SetLayoutElement(_ollamaUrlInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_ollamaUrlInput.Component.gameObject, UIStyles.InputBackground);

            var testBtn = CreateSecondaryButton(urlRow, "TestBtn", "Test", 60);
            testBtn.OnClick += TestOllamaConnection;

            _ollamaTestStatusLabel = UIFactory.CreateLabel(ollamaBox, "TestStatus", "", TextAnchor.MiddleLeft);
            _ollamaTestStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_ollamaTestStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Model row
            var modelRow = UIStyles.CreateFormRow(ollamaBox, "ModelRow", UIStyles.InputHeight, 5);

            var modelLabel = UIFactory.CreateLabel(modelRow, "ModelLabel", "Model:", TextAnchor.MiddleLeft);
            modelLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modelLabel.gameObject, minWidth: 50);

            _modelInput = UIFactory.CreateInputField(modelRow, "ModelInput", "qwen3:8b");
            UIFactory.SetLayoutElement(_modelInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_modelInput.Component.gameObject, UIStyles.InputBackground);

            UIStyles.CreateHint(ollamaBox, "ModelHint", "Recommended: qwen3:8b");

            UIStyles.CreateSpacer(ollamaBox, 5);

            // Game context
            var contextLabel = UIFactory.CreateLabel(ollamaBox, "ContextLabel", "Game Context (optional):", TextAnchor.MiddleLeft);
            contextLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(contextLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            _gameContextInput = UIFactory.CreateInputField(ollamaBox, "ContextInput", "");
            UIFactory.SetLayoutElement(_gameContextInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_gameContextInput.Component.gameObject, UIStyles.InputBackground);
        }

        public override void SetActive(bool active)
        {
            base.SetActive(active);
            if (active)
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

            // Load target language via component
            string configLang = TranslatorCore.Config.target_language;
            if (string.IsNullOrEmpty(configLang) || configLang == "auto")
            {
                _languageSelector.SelectedLanguage = "auto (System)";
            }
            else
            {
                _languageSelector.SelectedLanguage = configLang;
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
            _ollamaTestStatusLabel.text = "";
            OnOllamaToggleChanged(_enableOllamaToggle.isOn);
        }

        private void OnOnlineModeChanged(bool enabled)
        {
            _checkUpdatesToggle.interactable = enabled;
            _notifyUpdatesToggle.interactable = enabled;
            _autoDownloadToggle.interactable = enabled;
            _checkModUpdatesToggle.interactable = enabled;
        }

        private void OnOllamaToggleChanged(bool enabled)
        {
            _ollamaUrlInput.Component.interactable = enabled;
            _modelInput.Component.interactable = enabled;
            _gameContextInput.Component.interactable = enabled;
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
            // General
            TranslatorCore.Config.enable_translations = _enableTranslationsToggle.isOn;

            // Save target language (convert "auto (System)" back to "auto")
            string selectedLang = _languageSelector.SelectedLanguage;
            if (selectedLang == "auto (System)")
            {
                TranslatorCore.Config.target_language = "auto";
            }
            else
            {
                TranslatorCore.Config.target_language = selectedLang;
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

            TranslatorCore.SaveConfig();

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
    }
}
