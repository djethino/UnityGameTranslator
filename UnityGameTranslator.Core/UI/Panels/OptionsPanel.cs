using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;

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
        public override int MinHeight => 580;
        public override int PanelWidth => 500;
        public override int PanelHeight => 580;

        // General section
        private Toggle _enableTranslationsToggle;
        private Text _targetLanguageLabel;
        private int _targetLanguageIndex;
        private string[] _targetLanguageOptions;

        // Hotkey section
        private Text _hotkeyLabel;
        private ButtonRef _hotkeyBtn;
        private Toggle _hotkeyCtrlToggle;
        private Toggle _hotkeyAltToggle;
        private Toggle _hotkeyShiftToggle;
        private string _hotkey;
        private bool _hotkeyCtrl;
        private bool _hotkeyAlt;
        private bool _hotkeyShift;
        private bool _isCapturingHotkey;

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
            InitTargetLanguageOptions();
        }

        private void InitTargetLanguageOptions()
        {
            var langs = LanguageHelper.GetLanguageNames();
            _targetLanguageOptions = new string[langs.Length + 1];
            _targetLanguageOptions[0] = "auto (System)";
            for (int i = 0; i < langs.Length; i++)
            {
                _targetLanguageOptions[i + 1] = langs[i];
            }
        }

        private int GetTargetLanguageIndex(string lang)
        {
            if (string.IsNullOrEmpty(lang) || lang == "auto")
                return 0;
            for (int i = 1; i < _targetLanguageOptions.Length; i++)
            {
                if (_targetLanguageOptions[i] == lang)
                    return i;
            }
            return 0;
        }

        private string GetTargetLanguageFromIndex(int index)
        {
            if (index <= 0 || index >= _targetLanguageOptions.Length)
                return "auto";
            return _targetLanguageOptions[index];
        }

        protected override void ConstructPanelContent()
        {
            UIStyles.ConfigurePanelContent(ContentRoot, true);

            CreateFlexSpacer(ContentRoot, "TopSpacer");

            // Main card with scroll view for options
            var card = CreateCard(ContentRoot, "OptionsCard", 480);

            CreateTitle(card, "Title", "Options");

            UIStyles.CreateSpacer(card, 5);

            // Create scroll view inside card for sections
            var scrollObj = UIFactory.CreateScrollView(card, "OptionsScroll", out var scrollContent, out _);
            UIFactory.SetLayoutElement(scrollObj, flexibleHeight: 9999, flexibleWidth: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(scrollContent, false, false, true, true, 10, 5, 5, 5, 5);
            UIStyles.SetBackground(scrollObj, UIStyles.InputBackground);

            // Sections
            CreateGeneralSection(scrollContent);
            CreateHotkeySection(scrollContent);
            CreateOnlineModeSection(scrollContent);
            CreateOllamaSection(scrollContent);

            CreateFlexSpacer(ContentRoot, "BottomSpacer");

            // Buttons row
            var buttonRow = CreateButtonRow(ContentRoot);

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
            UIFactory.SetLayoutElement(transToggleObj, minHeight: 25);

            UIStyles.CreateSpacer(generalBox, 5);

            // Target Language
            var langRow = UIFactory.CreateHorizontalGroup(generalBox, "LanguageRow", false, false, true, true, 10);
            UIFactory.SetLayoutElement(langRow, minHeight: 30);

            var langLabel = UIFactory.CreateLabel(langRow, "LangLabel", "Target Language:", TextAnchor.MiddleLeft);
            langLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(langLabel.gameObject, minWidth: 120);

            var prevBtn = CreateSecondaryButton(langRow, "PrevLangBtn", "<", 35);
            prevBtn.OnClick += () =>
            {
                _targetLanguageIndex = (_targetLanguageIndex - 1 + _targetLanguageOptions.Length) % _targetLanguageOptions.Length;
                UpdateTargetLanguageDisplay();
            };

            _targetLanguageLabel = UIFactory.CreateLabel(langRow, "TargetLangLabel", "auto (System)", TextAnchor.MiddleCenter);
            _targetLanguageLabel.color = UIStyles.TextAccent;
            _targetLanguageLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_targetLanguageLabel.gameObject, minWidth: 150);

            var nextBtn = CreateSecondaryButton(langRow, "NextLangBtn", ">", 35);
            nextBtn.OnClick += () =>
            {
                _targetLanguageIndex = (_targetLanguageIndex + 1) % _targetLanguageOptions.Length;
                UpdateTargetLanguageDisplay();
            };

            UIStyles.CreateHint(generalBox, "LangHint", "Click arrows to change. First option uses system language.");
        }

        private void CreateHotkeySection(GameObject parent)
        {
            UIStyles.CreateSectionTitle(parent, "HotkeyLabel", "Settings Hotkey");

            var hotkeyBox = CreateSection(parent, "HotkeyBox");

            // Modifier toggles in styled container
            var modContainer = UIStyles.CreateModifierContainer(hotkeyBox, "ModContainer");

            var ctrlObj = UIFactory.CreateToggle(modContainer, "CtrlToggle", out _hotkeyCtrlToggle, out var ctrlLabel);
            ctrlLabel.text = "Ctrl";
            ctrlLabel.fontSize = UIStyles.FontSizeNormal;
            UIFactory.SetLayoutElement(ctrlObj, minWidth: 55);

            var altObj = UIFactory.CreateToggle(modContainer, "AltToggle", out _hotkeyAltToggle, out var altLabel);
            altLabel.text = "Alt";
            altLabel.fontSize = UIStyles.FontSizeNormal;
            UIFactory.SetLayoutElement(altObj, minWidth: 50);

            var shiftObj = UIFactory.CreateToggle(modContainer, "ShiftToggle", out _hotkeyShiftToggle, out var shiftLabel);
            shiftLabel.text = "Shift";
            shiftLabel.fontSize = UIStyles.FontSizeNormal;
            UIFactory.SetLayoutElement(shiftObj, minWidth: 55);

            var plusLabel = UIFactory.CreateLabel(modContainer, "PlusLabel", "+", TextAnchor.MiddleCenter);
            plusLabel.fontSize = 16;
            plusLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(plusLabel.gameObject, minWidth: 25);

            _hotkeyBtn = CreateSecondaryButton(modContainer, "HotkeyBtn", "F10", 80);
            _hotkeyBtn.OnClick += OnHotkeyButtonClicked;

            _hotkeyLabel = UIStyles.CreateHint(hotkeyBox, "HotkeyHint", "Click button and press a key to change");
        }

        private void CreateOnlineModeSection(GameObject parent)
        {
            UIStyles.CreateSectionTitle(parent, "OnlineLabel", "Online Mode");

            var onlineBox = CreateSection(parent, "OnlineBox");

            var onlineToggleObj = UIFactory.CreateToggle(onlineBox, "OnlineModeToggle", out _onlineModeToggle, out var onlineLabel);
            onlineLabel.text = " Enable Online Mode";
            onlineLabel.color = UIStyles.TextPrimary;
            _onlineModeToggle.onValueChanged.AddListener(OnOnlineModeChanged);
            UIFactory.SetLayoutElement(onlineToggleObj, minHeight: 25);

            UIStyles.CreateSpacer(onlineBox, 5);

            var checkUpdatesObj = UIFactory.CreateToggle(onlineBox, "CheckUpdatesToggle", out _checkUpdatesToggle, out var checkLabel);
            checkLabel.text = " Check for translation updates on start";
            checkLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(checkUpdatesObj, minHeight: 22);

            var notifyObj = UIFactory.CreateToggle(onlineBox, "NotifyToggle", out _notifyUpdatesToggle, out var notifyLabel);
            notifyLabel.text = " Notify when updates available";
            notifyLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(notifyObj, minHeight: 22);

            var autoDownloadObj = UIFactory.CreateToggle(onlineBox, "AutoDownloadToggle", out _autoDownloadToggle, out var autoLabel);
            autoLabel.text = " Auto-download updates (no conflicts)";
            autoLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(autoDownloadObj, minHeight: 22);

            var modUpdatesObj = UIFactory.CreateToggle(onlineBox, "ModUpdatesToggle", out _checkModUpdatesToggle, out var modLabel);
            modLabel.text = " Check for mod updates on GitHub";
            modLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(modUpdatesObj, minHeight: 22);
        }

        private void CreateOllamaSection(GameObject parent)
        {
            UIStyles.CreateSectionTitle(parent, "OllamaLabel", "Ollama (Local AI)");

            var ollamaBox = CreateSection(parent, "OllamaBox");

            var enableOllamaObj = UIFactory.CreateToggle(ollamaBox, "EnableOllamaToggle", out _enableOllamaToggle, out var enableLabel);
            enableLabel.text = " Enable Ollama";
            enableLabel.color = UIStyles.TextPrimary;
            _enableOllamaToggle.onValueChanged.AddListener(OnOllamaToggleChanged);
            UIFactory.SetLayoutElement(enableOllamaObj, minHeight: 25);

            UIStyles.CreateSpacer(ollamaBox, 5);

            // URL row
            var urlRow = UIFactory.CreateHorizontalGroup(ollamaBox, "UrlRow", false, false, true, true, 5);
            UIFactory.SetLayoutElement(urlRow, minHeight: UIStyles.InputHeight);

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
            UIFactory.SetLayoutElement(_ollamaTestStatusLabel.gameObject, minHeight: 18);

            // Model row
            var modelRow = UIFactory.CreateHorizontalGroup(ollamaBox, "ModelRow", false, false, true, true, 5);
            UIFactory.SetLayoutElement(modelRow, minHeight: UIStyles.InputHeight);

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
            UIFactory.SetLayoutElement(contextLabel.gameObject, minHeight: 18);

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

            // Capture hotkey when in capture mode
            if (_isCapturingHotkey)
            {
                CaptureHotkeyInput();
            }
        }

        private void CaptureHotkeyInput()
        {
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                // Skip modifier keys and mouse buttons
                if (key == KeyCode.None ||
                    key == KeyCode.LeftControl || key == KeyCode.RightControl ||
                    key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
                    key == KeyCode.LeftShift || key == KeyCode.RightShift ||
                    key == KeyCode.LeftCommand || key == KeyCode.RightCommand ||
                    key == KeyCode.LeftWindows || key == KeyCode.RightWindows ||
                    (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6))
                {
                    continue;
                }

                if (UniverseLib.Input.InputManager.GetKeyDown(key))
                {
                    _hotkey = key.ToString();
                    _isCapturingHotkey = false;
                    _hotkeyBtn.ButtonText.text = _hotkey;
                    _hotkeyLabel.text = "Click button and press a key to change";
                    return;
                }
            }
        }

        private void LoadCurrentSettings()
        {
            // General
            _enableTranslationsToggle.isOn = TranslatorCore.Config.enable_translations;
            _targetLanguageIndex = GetTargetLanguageIndex(TranslatorCore.Config.target_language);
            UpdateTargetLanguageDisplay();

            // Hotkey
            ParseHotkey(TranslatorCore.Config.settings_hotkey);
            _hotkeyCtrlToggle.isOn = _hotkeyCtrl;
            _hotkeyAltToggle.isOn = _hotkeyAlt;
            _hotkeyShiftToggle.isOn = _hotkeyShift;
            _hotkeyBtn.ButtonText.text = _hotkey;

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

        private void ParseHotkey(string hotkeyString)
        {
            if (string.IsNullOrEmpty(hotkeyString))
            {
                _hotkeyCtrl = false;
                _hotkeyAlt = false;
                _hotkeyShift = false;
                _hotkey = "F10";
                return;
            }

            _hotkeyCtrl = hotkeyString.Contains("Ctrl+");
            _hotkeyAlt = hotkeyString.Contains("Alt+");
            _hotkeyShift = hotkeyString.Contains("Shift+");
            _hotkey = hotkeyString
                .Replace("Ctrl+", "")
                .Replace("Alt+", "")
                .Replace("Shift+", "");
        }

        private void UpdateTargetLanguageDisplay()
        {
            if (_targetLanguageLabel != null && _targetLanguageIndex >= 0 && _targetLanguageIndex < _targetLanguageOptions.Length)
            {
                _targetLanguageLabel.text = _targetLanguageOptions[_targetLanguageIndex];
            }
        }

        private void OnHotkeyButtonClicked()
        {
            _isCapturingHotkey = true;
            _hotkeyBtn.ButtonText.text = "Press key...";
            _hotkeyLabel.text = "Waiting for key press...";

            // Unfocus the button to allow keyboard capture
            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);
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
            TranslatorCore.Config.target_language = GetTargetLanguageFromIndex(_targetLanguageIndex);

            // Hotkey - combine with modifiers
            string hotkeyString = "";
            if (_hotkeyCtrlToggle.isOn) hotkeyString += "Ctrl+";
            if (_hotkeyAltToggle.isOn) hotkeyString += "Alt+";
            if (_hotkeyShiftToggle.isOn) hotkeyString += "Shift+";
            hotkeyString += _hotkey;
            TranslatorCore.Config.settings_hotkey = hotkeyString;

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
