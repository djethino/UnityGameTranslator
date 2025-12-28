using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Reusable hotkey capture component.
    /// Allows users to set a keyboard shortcut with optional Ctrl/Alt/Shift modifiers.
    /// </summary>
    public class HotkeyCapture
    {
        // UI elements
        private Toggle _ctrlToggle;
        private Toggle _altToggle;
        private Toggle _shiftToggle;
        private ButtonRef _keyButton;
        private Text _displayLabel;

        // State
        private string _key = "F10";
        private bool _ctrl;
        private bool _alt;
        private bool _shift;
        private bool _isCapturing;

        // Callback
        private Action<string> _onHotkeyChanged;

        /// <summary>
        /// Whether currently capturing a key press.
        /// </summary>
        public bool IsCapturing => _isCapturing;

        /// <summary>
        /// Get the full hotkey string (e.g., "Ctrl+Alt+F10").
        /// </summary>
        public string HotkeyString
        {
            get
            {
                string result = "";
                if (_ctrl) result += "Ctrl+";
                if (_alt) result += "Alt+";
                if (_shift) result += "Shift+";
                result += _key;
                return result;
            }
        }

        /// <summary>
        /// Create a new hotkey capture component.
        /// </summary>
        /// <param name="initialHotkey">Initial hotkey string (e.g., "Ctrl+F10")</param>
        public HotkeyCapture(string initialHotkey = "F10")
        {
            ParseHotkey(initialHotkey);
        }

        /// <summary>
        /// Create the UI elements in the given parent.
        /// </summary>
        /// <param name="parent">Parent GameObject to add UI to</param>
        /// <param name="onHotkeyChanged">Callback when hotkey changes</param>
        /// <param name="includeDisplayLabel">Whether to include a display label below</param>
        public void CreateUI(GameObject parent, Action<string> onHotkeyChanged = null, bool includeDisplayLabel = true)
        {
            _onHotkeyChanged = onHotkeyChanged;

            // Modifier toggles in styled container
            var modContainer = UIStyles.CreateModifierContainer(parent, "HotkeyModContainer");

            var ctrlObj = UIFactory.CreateToggle(modContainer, "CtrlToggle", out _ctrlToggle, out var ctrlLabel);
            ctrlLabel.text = "Ctrl";
            ctrlLabel.fontSize = UIStyles.FontSizeNormal;
            _ctrlToggle.isOn = _ctrl;
            _ctrlToggle.onValueChanged.AddListener((val) => { _ctrl = val; NotifyChange(); });
            UIFactory.SetLayoutElement(ctrlObj, minWidth: UIStyles.ModifierKeyWidth);

            var altObj = UIFactory.CreateToggle(modContainer, "AltToggle", out _altToggle, out var altLabel);
            altLabel.text = "Alt";
            altLabel.fontSize = UIStyles.FontSizeNormal;
            _altToggle.isOn = _alt;
            _altToggle.onValueChanged.AddListener((val) => { _alt = val; NotifyChange(); });
            UIFactory.SetLayoutElement(altObj, minWidth: UIStyles.ModifierKeyWidth - 5);

            var shiftObj = UIFactory.CreateToggle(modContainer, "ShiftToggle", out _shiftToggle, out var shiftLabel);
            shiftLabel.text = "Shift";
            shiftLabel.fontSize = UIStyles.FontSizeNormal;
            _shiftToggle.isOn = _shift;
            _shiftToggle.onValueChanged.AddListener((val) => { _shift = val; NotifyChange(); });
            UIFactory.SetLayoutElement(shiftObj, minWidth: UIStyles.ModifierKeyWidth);

            var plusLabel = UIFactory.CreateLabel(modContainer, "PlusLabel", "+", TextAnchor.MiddleCenter);
            plusLabel.fontSize = UIStyles.FontSizeSectionTitle;
            plusLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(plusLabel.gameObject, minWidth: UIStyles.ToggleControlWidth);

            _keyButton = UIFactory.CreateButton(modContainer, "KeyButton", _key);
            UIFactory.SetLayoutElement(_keyButton.Component.gameObject, minWidth: UIStyles.SmallButtonWidth, minHeight: UIStyles.SmallButtonHeight);
            _keyButton.OnClick += StartCapture;

            if (includeDisplayLabel)
            {
                _displayLabel = UIStyles.CreateHint(parent, "HotkeyHint", "Click button and press a key to change");
            }
        }

        /// <summary>
        /// Must be called from Update() to capture key presses.
        /// </summary>
        public void Update()
        {
            if (!_isCapturing) return;

            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                // Skip modifier keys, mouse buttons, and invalid keys
                if (key == KeyCode.None ||
                    key == KeyCode.LeftControl || key == KeyCode.RightControl ||
                    key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
                    key == KeyCode.LeftShift || key == KeyCode.RightShift ||
                    key == KeyCode.LeftCommand || key == KeyCode.RightCommand ||
                    key == KeyCode.LeftWindows || key == KeyCode.RightWindows ||
                    key == KeyCode.AltGr || key == KeyCode.CapsLock ||
                    key == KeyCode.Numlock || key == KeyCode.Menu ||
                    (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6) ||
                    (int)key >= 330) // Skip joystick buttons
                {
                    continue;
                }

                bool keyPressed = false;
                try
                {
                    keyPressed = UniverseLib.Input.InputManager.GetKeyDown(key);
                }
                catch { }

                if (keyPressed)
                {
                    _key = key.ToString();
                    _isCapturing = false;

                    if (_keyButton != null)
                        _keyButton.ButtonText.text = _key;

                    if (_displayLabel != null)
                        _displayLabel.text = "Click button and press a key to change";

                    NotifyChange();
                    return;
                }
            }
        }

        /// <summary>
        /// Set the hotkey from a string (e.g., "Ctrl+Alt+F10").
        /// </summary>
        public void SetHotkey(string hotkeyString)
        {
            ParseHotkey(hotkeyString);
            UpdateUI();
        }

        /// <summary>
        /// Start capturing a key press.
        /// </summary>
        public void StartCapture()
        {
            _isCapturing = true;

            if (_keyButton != null)
                _keyButton.ButtonText.text = "...";

            if (_displayLabel != null)
            {
                _displayLabel.text = "Press any key...";
                _displayLabel.color = UIStyles.StatusWarning;
            }

            // Unfocus the button to allow keyboard capture
            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);
        }

        private void ParseHotkey(string hotkeyString)
        {
            if (string.IsNullOrEmpty(hotkeyString))
            {
                _ctrl = false;
                _alt = false;
                _shift = false;
                _key = "F10";
                return;
            }

            _ctrl = hotkeyString.Contains("Ctrl+");
            _alt = hotkeyString.Contains("Alt+");
            _shift = hotkeyString.Contains("Shift+");
            _key = hotkeyString
                .Replace("Ctrl+", "")
                .Replace("Alt+", "")
                .Replace("Shift+", "");
        }

        private void UpdateUI()
        {
            if (_ctrlToggle != null) _ctrlToggle.isOn = _ctrl;
            if (_altToggle != null) _altToggle.isOn = _alt;
            if (_shiftToggle != null) _shiftToggle.isOn = _shift;
            if (_keyButton != null) _keyButton.ButtonText.text = _key;
        }

        private void NotifyChange()
        {
            _onHotkeyChanged?.Invoke(HotkeyString);
        }
    }
}
