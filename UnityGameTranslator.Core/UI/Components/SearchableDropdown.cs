using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A custom dropdown component with search/filter functionality.
    /// </summary>
    public class SearchableDropdown
    {
        // Configuration
        private readonly string _name;
        private readonly int _popupHeight;
        private readonly bool _showSearch;

        // State
        private string[] _options;
        private string _selectedValue;
        private bool _isOpen;
        private Action<string> _onValueChanged;

        // UI Elements
        private GameObject _rootObject;
        private GameObject _buttonObject;
        private Text _buttonText;
        private GameObject _popupRoot;
        private InputField _searchInput;
        private GameObject _listContent;
        private ScrollRect _scrollRect;

        // Track selected item index for scroll positioning
        private int _selectedItemIndex = -1;

        // Prevent double-click issues
        private float _lastToggleTime = 0f;

        /// <summary>
        /// Current selected value.
        /// </summary>
        public string SelectedValue
        {
            get => _selectedValue;
            set
            {
                if (_selectedValue != value)
                {
                    _selectedValue = value;
                    UpdateButtonText();
                }
            }
        }

        /// <summary>
        /// Event fired when selection changes.
        /// </summary>
        public event Action<string> OnSelectionChanged;

        /// <summary>
        /// Create a new searchable dropdown.
        /// </summary>
        /// <param name="name">Unique name for UI elements</param>
        /// <param name="options">Array of options to choose from</param>
        /// <param name="initialValue">Initially selected value (null for first option or empty)</param>
        /// <param name="popupHeight">Height of the popup list</param>
        /// <param name="showSearch">Whether to show the search input</param>
        public SearchableDropdown(string name, string[] options, string initialValue = null, int popupHeight = 200, bool showSearch = true)
        {
            _name = name;
            _options = options ?? new string[0];
            _selectedValue = initialValue ?? (_options.Length > 0 ? _options[0] : "");
            _popupHeight = popupHeight;
            _showSearch = showSearch;
        }

        /// <summary>
        /// Create the dropdown UI in the given parent.
        /// </summary>
        /// <param name="parent">Parent GameObject</param>
        /// <param name="onValueChanged">Callback when selection changes</param>
        /// <param name="width">Width of the dropdown button</param>
        /// <returns>The root GameObject of the dropdown</returns>
        public GameObject CreateUI(GameObject parent, Action<string> onValueChanged = null, int width = 200)
        {
            _onValueChanged = onValueChanged;

            // Root container
            _rootObject = UIFactory.CreateUIObject($"SearchableDropdown_{_name}", parent);
            UIFactory.SetLayoutElement(_rootObject, minWidth: width, minHeight: 25, preferredWidth: width, preferredHeight: 25);

            // Main button
            _buttonObject = UIFactory.CreateUIObject("Button", _rootObject);
            RectTransform buttonRect = _buttonObject.GetComponent<RectTransform>();
            buttonRect.anchorMin = Vector2.zero;
            buttonRect.anchorMax = Vector2.one;
            buttonRect.sizeDelta = Vector2.zero;

            Image buttonImage = _buttonObject.AddComponent<Image>();
            buttonImage.type = Image.Type.Sliced;
            buttonImage.color = UIStyles.DropdownBackground;

            Button button = _buttonObject.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            RuntimeHelper.SetColorBlock(button, UIStyles.DropdownBackground,
                UIStyles.DropdownBackground * 1.2f, UIStyles.DropdownBackground * 0.8f);
            button.onClick.AddListener(TogglePopup);

            // Button text
            GameObject textObj = UIFactory.CreateUIObject("Text", _buttonObject);
            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0, 0);
            textRect.anchorMax = new Vector2(1, 1);
            textRect.offsetMin = new Vector2(8, 2);
            textRect.offsetMax = new Vector2(-25, -2);

            _buttonText = textObj.AddComponent<Text>();
            _buttonText.font = UniversalUI.DefaultFont;
            _buttonText.fontSize = 14;
            _buttonText.alignment = TextAnchor.MiddleLeft;
            _buttonText.color = UIStyles.TextPrimary;

            // Register BEFORE setting text (the patch intercepts text assignment)
            TranslatorCore.RegisterExcluded(_buttonText);
            UpdateButtonText();

            // Arrow
            GameObject arrowObj = UIFactory.CreateUIObject("Arrow", _buttonObject);
            RectTransform arrowRect = arrowObj.GetComponent<RectTransform>();
            arrowRect.anchorMin = new Vector2(1, 0.5f);
            arrowRect.anchorMax = new Vector2(1, 0.5f);
            arrowRect.sizeDelta = new Vector2(20, 20);
            arrowRect.anchoredPosition = new Vector2(-12, 0);

            Text arrowText = arrowObj.AddComponent<Text>();
            arrowText.font = UniversalUI.DefaultFont;
            arrowText.fontSize = 12;
            arrowText.alignment = TextAnchor.MiddleCenter;
            arrowText.color = UIStyles.TextSecondary;

            // Register BEFORE setting text (the patch intercepts text assignment)
            TranslatorCore.RegisterExcluded(arrowText);
            arrowText.text = "\u25BC";

            return _rootObject;
        }

        /// <summary>
        /// Update the available options.
        /// </summary>
        public void SetOptions(string[] options)
        {
            _options = options ?? new string[0];
            if (_isOpen)
            {
                RefreshList();
            }
        }

        /// <summary>
        /// Toggle the popup open/closed.
        /// </summary>
        public void TogglePopup()
        {
            // Prevent rapid toggle (debounce)
            float now = Time.realtimeSinceStartup;
            if (now - _lastToggleTime < 0.15f)
            {
                TranslatorCore.LogInfo($"[SearchableDropdown] TogglePopup IGNORED (debounce), delta={now - _lastToggleTime:F3}s");
                return;
            }
            _lastToggleTime = now;

            TranslatorCore.LogInfo($"[SearchableDropdown] TogglePopup called, _isOpen={_isOpen}");
            if (_isOpen)
                ClosePopup();
            else
                OpenPopup();
        }

        /// <summary>
        /// Open the popup.
        /// </summary>
        public void OpenPopup()
        {
            TranslatorCore.LogInfo($"[SearchableDropdown] OpenPopup called");
            if (_isOpen) return;
            _isOpen = true;

            CreatePopup();
            RefreshList();
            TranslatorCore.LogInfo($"[SearchableDropdown] Popup created with {_options?.Length ?? 0} options");
        }

        /// <summary>
        /// Close the popup.
        /// </summary>
        public void ClosePopup()
        {
            if (!_isOpen) return;
            _isOpen = false;

            DestroyPopup();
        }

        private void CreatePopup()
        {
            // Get button dimensions
            float buttonWidth = 200f;
            var layoutElem = _rootObject.GetComponent<LayoutElement>();
            if (layoutElem != null && layoutElem.minWidth > 0)
                buttonWidth = layoutElem.minWidth;
            float popupHeight = _popupHeight + (_showSearch ? 35 : 10);

            // Create popup as child of the button
            _popupRoot = UIFactory.CreateUIObject($"SearchableDropdown_Popup_{_name}", _rootObject);

            // Add Canvas with overrideSorting to render above other panel elements
            Canvas popupCanvas = _popupRoot.AddComponent<Canvas>();
            popupCanvas.overrideSorting = true;
            popupCanvas.sortingOrder = 32000; // Above panel content but reasonable
            _popupRoot.AddComponent<GraphicRaycaster>();

            // Position below the button
            RectTransform popupRect = _popupRoot.GetComponent<RectTransform>();
            popupRect.anchorMin = new Vector2(0f, 0f);
            popupRect.anchorMax = new Vector2(0f, 0f);
            popupRect.pivot = new Vector2(0f, 1f); // Top-left pivot
            popupRect.anchoredPosition = Vector2.zero; // At button's bottom-left
            popupRect.sizeDelta = new Vector2(buttonWidth, popupHeight);

            TranslatorCore.LogInfo($"[SearchableDropdown] Popup created with overrideSorting, size=({buttonWidth}, {popupHeight})");

            // Add background to popup root
            Image bgImage = _popupRoot.AddComponent<Image>();
            bgImage.color = UIStyles.CardBackground;

            float yOffset = 4f;
            float searchHeight = _showSearch ? 25f : 0f;
            float scrollHeight = _popupHeight - (_showSearch ? 35 : 10);

            // Search input
            if (_showSearch)
            {
                var searchFieldRef = UIFactory.CreateInputField(_popupRoot, "SearchInput", "Search...");
                RectTransform searchRect = searchFieldRef.GameObject.GetComponent<RectTransform>();
                searchRect.anchorMin = new Vector2(0, 1);
                searchRect.anchorMax = new Vector2(1, 1);
                searchRect.pivot = new Vector2(0.5f, 1);
                searchRect.anchoredPosition = new Vector2(0, -yOffset);
                searchRect.sizeDelta = new Vector2(-8, searchHeight);

                _searchInput = searchFieldRef.Component;
                _searchInput.onValueChanged.AddListener(OnSearchChanged);

                Image searchBg = searchFieldRef.GameObject.GetComponent<Image>();
                if (searchBg != null)
                    searchBg.color = UIStyles.InputFieldBackground;

                yOffset += searchHeight + 4f;
            }

            // Viewport with RectMask2D
            GameObject viewport = UIFactory.CreateUIObject("Viewport", _popupRoot);
            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin = new Vector2(0, 1);
            viewportRect.anchorMax = new Vector2(1, 1);
            viewportRect.pivot = new Vector2(0.5f, 1);
            viewportRect.anchoredPosition = new Vector2(0, -yOffset);
            viewportRect.sizeDelta = new Vector2(-8, scrollHeight);

            Image viewportBg = viewport.AddComponent<Image>();
            viewportBg.color = new Color(0.1f, 0.1f, 0.12f);
            viewport.AddComponent<RectMask2D>();

            // Content container
            _listContent = UIFactory.CreateUIObject("Content", viewport);
            RectTransform contentRect = _listContent.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0, 0);

            // ScrollRect
            _scrollRect = viewport.AddComponent<ScrollRect>();
            _scrollRect.content = contentRect;
            _scrollRect.viewport = viewportRect;
            _scrollRect.horizontal = false;
            _scrollRect.vertical = true;
            _scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _scrollRect.scrollSensitivity = 30f;
            _scrollRect.inertia = false;

            Canvas.ForceUpdateCanvases();
            TranslatorCore.LogInfo($"[SearchableDropdown] Popup created");
        }

        private void DestroyPopup()
        {
            if (_popupRoot != null)
            {
                UnityEngine.Object.Destroy(_popupRoot);
                _popupRoot = null;
            }

            _searchInput = null;
            _listContent = null;
            _scrollRect = null;
        }

        private void OnSearchChanged(string searchText)
        {
            RefreshList();
        }

        private void RefreshList()
        {
            if (_listContent == null) return;

            // Reset item index for positioning
            _itemIndex = 0;
            _selectedItemIndex = -1;

            // Clear existing items from content
            foreach (Transform child in _listContent.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            // Get filter text
            string filter = _searchInput != null ? _searchInput.text.ToLowerInvariant() : "";

            // Create filtered list
            int currentFilteredIndex = 0;
            foreach (string option in _options)
            {
                if (!string.IsNullOrEmpty(filter) && !option.ToLowerInvariant().Contains(filter))
                    continue;

                // Track selected item's index in the filtered list
                if (option == _selectedValue)
                    _selectedItemIndex = currentFilteredIndex;

                CreateOptionItem(option);
                currentFilteredIndex++;
            }

            // Force canvas update before scrolling
            Canvas.ForceUpdateCanvases();

            // Scroll to selected item (only on initial open, not when filtering)
            if (_scrollRect != null && string.IsNullOrEmpty(filter))
            {
                ScrollToSelectedItem();
            }
            else if (_scrollRect != null)
            {
                // When filtering, scroll to top
                _scrollRect.verticalNormalizedPosition = 1f;
            }
        }

        private void ScrollToSelectedItem()
        {
            if (_selectedItemIndex < 0 || _scrollRect == null || _listContent == null)
                return;

            RectTransform contentRect = _listContent.GetComponent<RectTransform>();
            RectTransform viewportRect = _scrollRect.viewport;

            float contentHeight = contentRect.sizeDelta.y;
            float viewportHeight = viewportRect.rect.height;

            // If content fits in viewport, no scroll needed
            if (contentHeight <= viewportHeight)
            {
                _scrollRect.verticalNormalizedPosition = 1f;
                return;
            }

            // Calculate position of selected item
            float itemTop = _selectedItemIndex * (ITEM_HEIGHT + ITEM_SPACING);
            float itemBottom = itemTop + ITEM_HEIGHT;

            // Calculate scroll range
            float scrollableHeight = contentHeight - viewportHeight;

            // We want the selected item to be visible, preferably centered
            // Target: place item in the middle of viewport
            float targetScrollY = itemTop - (viewportHeight / 2) + (ITEM_HEIGHT / 2);

            // Clamp to valid range
            targetScrollY = Mathf.Clamp(targetScrollY, 0, scrollableHeight);

            // Convert to normalized position (1 = top, 0 = bottom)
            float normalizedPos = 1f - (targetScrollY / scrollableHeight);
            _scrollRect.verticalNormalizedPosition = normalizedPos;

            TranslatorCore.LogInfo($"[SearchableDropdown] Scrolled to item {_selectedItemIndex}, normalizedPos={normalizedPos:F2}");
        }

        private int _itemIndex = 0;
        private const float ITEM_HEIGHT = 25f;
        private const float ITEM_SPACING = 2f;

        private void CreateOptionItem(string option)
        {
            // Create item in content container
            GameObject itemObj = UIFactory.CreateUIObject($"Option_{option}", _listContent);

            // Position manually based on index
            float yPos = _itemIndex * (ITEM_HEIGHT + ITEM_SPACING);
            RectTransform itemRect = itemObj.GetComponent<RectTransform>();
            itemRect.anchorMin = new Vector2(0, 1);
            itemRect.anchorMax = new Vector2(1, 1);
            itemRect.pivot = new Vector2(0.5f, 1);
            itemRect.anchoredPosition = new Vector2(0, -yPos);
            itemRect.sizeDelta = new Vector2(0, ITEM_HEIGHT);
            _itemIndex++;

            // Update content height
            RectTransform contentRect = _listContent.GetComponent<RectTransform>();
            contentRect.sizeDelta = new Vector2(0, _itemIndex * (ITEM_HEIGHT + ITEM_SPACING));

            Image itemBg = itemObj.AddComponent<Image>();
            bool isSelected = option == _selectedValue;
            itemBg.color = isSelected ? UIStyles.ButtonPrimary : UIStyles.DropdownItemNormal;

            Button itemButton = itemObj.AddComponent<Button>();
            itemButton.targetGraphic = itemBg;
            RuntimeHelper.SetColorBlock(itemButton,
                isSelected ? UIStyles.ButtonPrimary : UIStyles.DropdownItemNormal,
                UIStyles.DropdownItemHighlight,
                UIStyles.DropdownItemNormal * 0.8f);

            string capturedOption = option;
            itemButton.onClick.AddListener(() => SelectOption(capturedOption));

            // Option text
            Text itemText = UIFactory.CreateLabel(itemObj, "Text", "", TextAnchor.MiddleLeft,
                isSelected ? Color.white : UIStyles.TextPrimary, fontSize: 13);

            RectTransform textRect = itemText.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8, 2);
            textRect.offsetMax = new Vector2(-8, -2);

            // Register BEFORE setting text to exclude from translation
            TranslatorCore.RegisterExcluded(itemText);
            itemText.text = option;
        }

        private void SelectOption(string option)
        {
            _selectedValue = option;
            UpdateButtonText();
            ClosePopup();

            _onValueChanged?.Invoke(option);
            OnSelectionChanged?.Invoke(option);
        }

        private void UpdateButtonText()
        {
            if (_buttonText != null)
            {
                _buttonText.text = string.IsNullOrEmpty(_selectedValue) ? "(none)" : _selectedValue;
            }
        }

        /// <summary>
        /// Enable or disable the dropdown.
        /// </summary>
        public void SetInteractable(bool interactable)
        {
            if (_buttonObject != null)
            {
                Button btn = _buttonObject.GetComponent<Button>();
                if (btn != null)
                    btn.interactable = interactable;
            }
        }

        /// <summary>
        /// Destroy the dropdown UI.
        /// </summary>
        public void Destroy()
        {
            ClosePopup();
            if (_rootObject != null)
            {
                UnityEngine.Object.Destroy(_rootObject);
                _rootObject = null;
            }
        }
    }
}
