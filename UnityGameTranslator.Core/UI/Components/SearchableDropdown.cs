using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UniverseLib.UI.Widgets;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A custom dropdown component with search/filter functionality.
    /// Uses UniverseLib's ButtonRef and InputFieldRef for IL2CPP-safe event handling.
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
        private EllipsisLabel _buttonEllipsis;
        private GameObject _popupRoot;
        private InputFieldRef _searchInputRef;
        private GameObject _listContent;
        private ScrollRect _scrollRect;
        private AutoSliderScrollbar _autoScrollbar;

        // Ellipsis handles of the visible option rows, used to size the popup to its content
        private readonly List<EllipsisLabel> _itemEllipsis = new List<EllipsisLabel>();

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
            // Image stays white; the ColorBlock below tints it. Setting Image.color = DropdownBackground
            // too would render DropdownBackground² (crushed dark) since final = Image.color × normalColor.
            buttonImage.color = Color.white;

            Button button = _buttonObject.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            RuntimeHelper.SetColorBlock(button, UIStyles.DropdownBackground,
                UIStyles.DropdownBackground * 1.2f, UIStyles.DropdownBackground * 0.8f);

            // Use ButtonRef for IL2CPP-safe click handling
            // ButtonRef's constructor calls AddListener inside UniverseLib (compiled with correct platform defines)
            var mainBtnRef = new ButtonRef(button);
            mainBtnRef.OnClick = TogglePopup;

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

            // A long value used to wrap onto a second line that the button then clipped away,
            // leaving only the head visible (e.g. "[Custom]" and nothing else). Keep it on one
            // line and end it with an ellipsis instead; the popup shows entries in full.
            _buttonEllipsis = UIFactory.ConfigureEllipsis(_buttonText);
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
                TranslatorCore.LogDebug($"[SearchableDropdown] TogglePopup IGNORED (debounce), delta={now - _lastToggleTime:F3}s");
                return;
            }
            _lastToggleTime = now;

            TranslatorCore.LogDebug($"[SearchableDropdown] TogglePopup called, _isOpen={_isOpen}");
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
            if (_isOpen) return;
            _isOpen = true;

            CreatePopup();
            RefreshList();
            TranslatorCore.LogDebug($"[SearchableDropdown] Popup opened with {_options?.Length ?? 0} options");
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
            // Get button dimensions — the laid-out width first (a parent layout group may have
            // stretched the dropdown well past its minWidth), falling back to the requested one.
            float buttonWidth = _rootObject.GetComponent<RectTransform>().rect.width;
            if (buttonWidth <= 0f)
            {
                var layoutElem = _rootObject.GetComponent<LayoutElement>();
                buttonWidth = layoutElem != null && layoutElem.minWidth > 0 ? layoutElem.minWidth : 200f;
            }
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

            TranslatorCore.LogDebug($"[SearchableDropdown] Popup created with overrideSorting, size=({buttonWidth}, {popupHeight})");

            // Add background to popup root
            Image bgImage = _popupRoot.AddComponent<Image>();
            bgImage.color = UIStyles.CardBackground;

            float yOffset = 4f;
            float searchHeight = _showSearch ? 25f : 0f;

            // Search input
            if (_showSearch)
            {
                _searchInputRef = UIFactory.CreateInputField(_popupRoot, "SearchInput", "Search...");
                RectTransform searchRect = _searchInputRef.GameObject.GetComponent<RectTransform>();
                searchRect.anchorMin = new Vector2(0, 1);
                searchRect.anchorMax = new Vector2(1, 1);
                searchRect.pivot = new Vector2(0.5f, 1);
                searchRect.anchoredPosition = new Vector2(0, -yOffset);
                searchRect.sizeDelta = new Vector2(-8, searchHeight);

                // Use InputFieldRef.OnValueChanged (IL2CPP-safe: AddListener is inside UniverseLib)
                _searchInputRef.OnValueChanged += OnSearchChanged;

                Image searchBg = _searchInputRef.GameObject.GetComponent<Image>();
                if (searchBg != null)
                    searchBg.color = UIStyles.InputFieldBackground;

                yOffset += searchHeight + 4f;
            }

            // Option list — built with the same ScrollView as every other list in the mod, so it
            // gets the standard auto-hiding scrollbar (the hand-rolled viewport this replaces had
            // no scrollbar at all: long lists could only be reached with the mouse wheel).
            var scrollObj = UIFactory.CreateScrollView(_popupRoot, "OptionsScroll", out _listContent, out _autoScrollbar);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_listContent, true, false, true, true,
                ITEM_SPACING, CONTENT_PADDING, CONTENT_PADDING, CONTENT_PADDING, CONTENT_PADDING);

            // Fill the popup below the search box; anchored so it follows the popup when resized.
            RectTransform scrollTransform = scrollObj.GetComponent<RectTransform>();
            scrollTransform.anchorMin = Vector2.zero;
            scrollTransform.anchorMax = Vector2.one;
            scrollTransform.pivot = new Vector2(0.5f, 0.5f);
            scrollTransform.offsetMin = new Vector2(4f, 4f);
            scrollTransform.offsetMax = new Vector2(-4f, -yOffset);

            UIStyles.ConfigureScrollViewNoScrollbar(scrollObj);

            // The popup draws in a nested Canvas with overrideSorting (so it can appear above the
            // panel). Stencil Masks do not survive that: the grip — a child of the SliderScrollbar's
            // Mask — was culled outright (correct size and colour, but never drawn), while the track
            // still showed because a Mask always draws its own graphic. The grip never leaves its
            // track, so it does not need masking.
            var gripImage = _autoScrollbar?.Slider != null && _autoScrollbar.Slider.handleRect != null
                ? _autoScrollbar.Slider.handleRect.GetComponent<Image>()
                : null;
            if (gripImage != null) gripImage.maskable = false;

            _scrollRect = scrollObj.GetComponent<ScrollRect>();
            if (_scrollRect != null)
            {
                _scrollRect.movementType = ScrollRect.MovementType.Clamped;
                _scrollRect.scrollSensitivity = 20f;
                _scrollRect.inertia = false;
            }

            Canvas.ForceUpdateCanvases();
            TranslatorCore.LogDebug($"[SearchableDropdown] Popup created");
        }

        private void DestroyPopup()
        {
            if (_popupRoot != null)
            {
                UnityEngine.Object.Destroy(_popupRoot);
                _popupRoot = null;
            }

            _searchInputRef = null;
            _listContent = null;
            _scrollRect = null;
            _autoScrollbar = null;
            _itemEllipsis.Clear();
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
            _itemEllipsis.Clear();

            // Clear existing items (manual iteration for IL2CPP compatibility)
            for (int i = _listContent.transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_listContent.transform.GetChild(i).gameObject);
            }

            // Get filter text
            string filter = _searchInputRef != null ? _searchInputRef.Component.text.ToLowerInvariant() : "";

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

            // Size the popup on the FULL list only (open / options changed). Doing it per keystroke
            // while filtering would make the popup jump wider and narrower as the user types.
            if (string.IsNullOrEmpty(filter))
                SizePopupToContent();

            // Force canvas update before scrolling
            Canvas.ForceUpdateCanvases();
            if (_listContent != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_listContent.GetComponent<RectTransform>());

            // Size the scrollbar grip NOW that the rows exist. The scroll view is built before the
            // list is filled, so the grip was sized against an empty content (height 0 → grip
            // collapsed to nothing, which read as "no grip at all").
            _autoScrollbar?.UpdateSliderHandle();

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

        /// <summary>
        /// Grow the popup so its entries are readable in full, capped by the room actually left to
        /// the right of the dropdown inside the canvas (never wider than the screen). Entries still
        /// too long for that cap keep their ellipsis. The popup is never narrower than its button.
        /// </summary>
        private void SizePopupToContent()
        {
            if (_popupRoot == null || _itemEllipsis.Count == 0) return;

            float widestEntry = 0f;
            foreach (var label in _itemEllipsis)
            {
                if (label == null) continue;
                float width = label.PreferredFullWidth;
                if (width > widestEntry) widestEntry = width;
            }
            if (widestEntry <= 0f) return;

            RectTransform popupRect = _popupRoot.GetComponent<RectTransform>();
            float buttonWidth = _rootObject.GetComponent<RectTransform>().rect.width;
            if (buttonWidth <= 0f) buttonWidth = popupRect.sizeDelta.x;

            float wanted = widestEntry + POPUP_CHROME_WIDTH;
            float width2 = Mathf.Clamp(wanted, buttonWidth, GetAvailableWidth(buttonWidth));

            if (!Mathf.Approximately(width2, popupRect.sizeDelta.x))
                popupRect.sizeDelta = new Vector2(width2, popupRect.sizeDelta.y);
        }

        /// <summary>
        /// Horizontal room from the dropdown's left edge to the right edge of the canvas.
        /// Uses TransformPoint/InverseTransformPoint only — RectTransformUtility takes array
        /// parameters that crash on IL2CPP.
        /// </summary>
        private float GetAvailableWidth(float fallbackWidth)
        {
            var canvas = _rootObject.GetComponentInParent<Canvas>();
            var canvasRect = canvas != null ? canvas.rootCanvas.GetComponent<RectTransform>() : null;
            if (canvasRect == null) return fallbackWidth * 2f;

            Vector3 local = canvasRect.InverseTransformPoint(_rootObject.transform.position);
            float leftEdge = local.x - (fallbackWidth / 2f); // rootObject pivot is centred
            float available = canvasRect.rect.xMax - leftEdge - 12f;

            return Mathf.Max(fallbackWidth, available);
        }

        private void ScrollToSelectedItem()
        {
            if (_selectedItemIndex < 0 || _scrollRect == null || _listContent == null)
                return;

            RectTransform contentRect = _listContent.GetComponent<RectTransform>();
            RectTransform viewportRect = _scrollRect.viewport;

            // Height now comes from the layout group + ContentSizeFitter, not a manual sizeDelta
            float contentHeight = contentRect.rect.height;
            float viewportHeight = viewportRect.rect.height;

            // If content fits in viewport, no scroll needed
            if (contentHeight <= viewportHeight)
            {
                _scrollRect.verticalNormalizedPosition = 1f;
                return;
            }

            // Calculate position of selected item (rows are a fixed height, stacked by the layout
            // group under its top padding)
            float itemTop = CONTENT_PADDING + _selectedItemIndex * (ITEM_HEIGHT + ITEM_SPACING);

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

            TranslatorCore.LogDebug($"[SearchableDropdown] Scrolled to item {_selectedItemIndex}, normalizedPos={normalizedPos:F2}");
        }

        private int _itemIndex = 0;
        private const int ITEM_HEIGHT = 25;
        private const int ITEM_SPACING = 2;
        private const int CONTENT_PADDING = 2;

        // Horizontal room an option row needs on top of its text: row text insets (8 + 8),
        // content padding, the scrollbar column and the popup margins.
        private const float ITEM_TEXT_INSETS = 16f;
        private const float POPUP_CHROME_WIDTH = ITEM_TEXT_INSETS + (CONTENT_PADDING * 2) + 28f + 8f;

        private void CreateOptionItem(string option)
        {
            // Create item in content container — height/stacking handled by the layout group
            GameObject itemObj = UIFactory.CreateUIObject($"Option_{option}", _listContent);
            UIFactory.SetLayoutElement(itemObj, minHeight: ITEM_HEIGHT, preferredHeight: ITEM_HEIGHT, flexibleWidth: 9999);
            _itemIndex++;

            Image itemBg = itemObj.AddComponent<Image>();
            bool isSelected = option == _selectedValue;
            // Image stays white; the ColorBlock tints it (final = Image.color × normalColor). Setting
            // Image.color to the same color would render color² — normal items came out near-black.
            itemBg.color = Color.white;

            Button itemButton = itemObj.AddComponent<Button>();
            itemButton.targetGraphic = itemBg;
            RuntimeHelper.SetColorBlock(itemButton,
                isSelected ? UIStyles.ButtonPrimary : UIStyles.DropdownItemNormal,
                UIStyles.DropdownItemHighlight,
                UIStyles.DropdownItemNormal * 0.8f);

            // Use ButtonRef for IL2CPP-safe click handling
            string capturedOption = option;
            var itemBtnRef = new ButtonRef(itemButton);
            itemBtnRef.OnClick = () => SelectOption(capturedOption);

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

            // Rows show the entry in full — the popup widens to its content (SizePopupToContent).
            // The ellipsis only kicks in for entries too long even for the widened popup, so a
            // huge name never spills over the row below it.
            var itemEllipsis = UIFactory.ConfigureEllipsis(itemText, option);
            if (itemEllipsis != null)
                _itemEllipsis.Add(itemEllipsis);
            else
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
            if (_buttonText == null) return;

            string value = string.IsNullOrEmpty(_selectedValue) ? "(none)" : _selectedValue;
            if (_buttonEllipsis != null)
                _buttonEllipsis.FullText = value; // the component owns Text.text (trims to fit)
            else
                _buttonText.text = value;
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
