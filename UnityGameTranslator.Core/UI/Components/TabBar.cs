using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Reusable tab bar component.
    /// Creates a row of tab buttons with associated content panels.
    /// </summary>
    public class TabBar
    {
        /// <summary>
        /// Information about a single tab.
        /// </summary>
        private class TabInfo
        {
            public string Name;
            public ButtonRef Button;
            public GameObject Content;
            public Text ButtonText;
        }

        // UI elements
        private GameObject _tabRow;
        private GameObject _contentContainer;

        // State
        private readonly List<TabInfo> _tabs = new List<TabInfo>();
        private int _selectedIndex = -1;

        // Callbacks
        private Action<int, string> _onTabChanged;

        /// <summary>
        /// Currently selected tab index (-1 if none).
        /// </summary>
        public int SelectedIndex => _selectedIndex;

        /// <summary>
        /// Currently selected tab name (null if none).
        /// </summary>
        public string SelectedName => _selectedIndex >= 0 && _selectedIndex < _tabs.Count
            ? _tabs[_selectedIndex].Name
            : null;

        /// <summary>
        /// Number of tabs.
        /// </summary>
        public int TabCount => _tabs.Count;

        /// <summary>
        /// Event fired when tab selection changes.
        /// Parameters: (tabIndex, tabName)
        /// </summary>
        public event Action<int, string> OnTabChanged
        {
            add => _onTabChanged += value;
            remove => _onTabChanged -= value;
        }

        /// <summary>
        /// Create the tab bar UI in the given parent.
        /// Call AddTab() after this to add tabs.
        /// </summary>
        /// <param name="parent">Parent GameObject</param>
        /// <param name="tabRowHeight">Height of the tab button row</param>
        public void CreateUI(GameObject parent, int tabRowHeight = 32)
        {
            // Tab button row
            _tabRow = UIFactory.CreateHorizontalGroup(parent, "TabRow", false, false, true, true, 2,
                new Vector4(0, 0, 0, 0), UIStyles.TabBarBackground, TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(_tabRow, minHeight: tabRowHeight, flexibleWidth: 9999);

            // Content container - holds all tab contents, only one visible at a time
            // forceHeight: false so children align to top instead of stretching
            _contentContainer = UIFactory.CreateVerticalGroup(parent, "TabContent", true, false, true, true, 0,
                new Vector4(0, 0, 0, 0), Color.clear, TextAnchor.UpperLeft);
            UIFactory.SetLayoutElement(_contentContainer, flexibleWidth: 9999, flexibleHeight: 9999);
        }

        /// <summary>
        /// Add a new tab with the given name.
        /// Returns the content GameObject where you can add UI elements.
        /// </summary>
        /// <param name="name">Tab name (displayed on button)</param>
        /// <returns>Content GameObject for this tab</returns>
        public GameObject AddTab(string name)
        {
            int tabIndex = _tabs.Count;

            // Create tab button
            var btn = UIFactory.CreateButton(_tabRow, $"Tab_{name}", name, UIStyles.TabInactiveBackground);
            UIFactory.SetLayoutElement(btn.Component.gameObject, minWidth: 80, minHeight: 28, flexibleWidth: 1);

            // Style the button text
            btn.ButtonText.fontSize = UIStyles.FontSizeNormal;
            btn.ButtonText.color = UIStyles.TextSecondary;

            // Create content panel (hidden by default)
            // TabContentBackground with no padding (thin border effect)
            var content = UIFactory.CreateVerticalGroup(_contentContainer, $"TabContent_{name}",
                true, true, true, true, 0, Vector4.zero, UIStyles.TabContentBackground, TextAnchor.UpperLeft);
            UIFactory.SetLayoutElement(content, flexibleWidth: 9999, flexibleHeight: 9999);
            content.SetActive(false);

            // Store tab info
            var tabInfo = new TabInfo
            {
                Name = name,
                Button = btn,
                Content = content,
                ButtonText = btn.ButtonText
            };
            _tabs.Add(tabInfo);

            // Click handler - capture index for closure
            int capturedIndex = tabIndex;
            btn.OnClick += () => SelectTab(capturedIndex);

            // Auto-select first tab
            if (_tabs.Count == 1)
            {
                SelectTab(0);
            }

            return content;
        }

        /// <summary>
        /// Select a tab by index.
        /// </summary>
        public void SelectTab(int index)
        {
            if (index < 0 || index >= _tabs.Count)
                return;

            if (_selectedIndex == index)
                return;

            // Deselect previous tab
            if (_selectedIndex >= 0 && _selectedIndex < _tabs.Count)
            {
                var prevTab = _tabs[_selectedIndex];
                prevTab.Content.SetActive(false);
                StyleTabButton(prevTab, false);
            }

            // Select new tab
            _selectedIndex = index;
            var newTab = _tabs[index];
            newTab.Content.SetActive(true);
            StyleTabButton(newTab, true);

            // Fire event
            _onTabChanged?.Invoke(index, newTab.Name);
        }

        /// <summary>
        /// Select a tab by name.
        /// </summary>
        public void SelectTab(string name)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].Name == name)
                {
                    SelectTab(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Get the content GameObject for a tab by index.
        /// </summary>
        public GameObject GetTabContent(int index)
        {
            if (index < 0 || index >= _tabs.Count)
                return null;
            return _tabs[index].Content;
        }

        /// <summary>
        /// Get the content GameObject for a tab by name.
        /// </summary>
        public GameObject GetTabContent(string name)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].Name == name)
                    return _tabs[i].Content;
            }
            return null;
        }

        /// <summary>
        /// Update tab button text (for localization).
        /// </summary>
        public void SetTabName(int index, string newName)
        {
            if (index < 0 || index >= _tabs.Count)
                return;

            _tabs[index].Name = newName;
            _tabs[index].ButtonText.text = newName;
        }

        /// <summary>
        /// Style a tab button as active or inactive.
        /// </summary>
        private void StyleTabButton(TabInfo tab, bool isActive)
        {
            if (tab.Button?.Component == null)
                return;

            var colors = tab.Button.Component.colors;

            if (isActive)
            {
                colors.normalColor = UIStyles.TabActiveBackground;
                colors.highlightedColor = UIStyles.TabActiveBackground;
                colors.pressedColor = UIStyles.TabActiveBackground;
                tab.ButtonText.color = UIStyles.TextPrimary;
                tab.ButtonText.fontStyle = FontStyle.Bold;
            }
            else
            {
                colors.normalColor = UIStyles.TabInactiveBackground;
                colors.highlightedColor = UIStyles.TabHoverBackground;
                colors.pressedColor = UIStyles.TabActiveBackground;
                tab.ButtonText.color = UIStyles.TextSecondary;
                tab.ButtonText.fontStyle = FontStyle.Normal;
            }

            tab.Button.Component.colors = colors;
        }

        /// <summary>
        /// Get all tab button Text components for localization registration.
        /// </summary>
        public List<Text> GetTabButtonTexts()
        {
            var texts = new List<Text>();
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].ButtonText != null)
                    texts.Add(_tabs[i].ButtonText);
            }
            return texts;
        }

        /// <summary>
        /// Measures the maximum content height across all tabs.
        /// Activates all tabs simultaneously to ensure proper layout calculation,
        /// then restores original selection.
        /// </summary>
        /// <returns>Maximum preferred height of all tab contents</returns>
        public float MeasureMaxContentHeight()
        {
            if (_tabs.Count == 0)
                return 0;

            int originalIndex = _selectedIndex;
            float maxHeight = 0;

            // Step 1: Activate ALL tab contents simultaneously
            for (int i = 0; i < _tabs.Count; i++)
            {
                _tabs[i].Content.SetActive(true);
            }

            // Step 2: Force layout rebuild on the container (parent of all tabs)
            if (_contentContainer != null)
            {
                var containerRect = _contentContainer.GetComponent<RectTransform>();
                if (containerRect != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(containerRect);
                }
            }

            // Step 3: Measure each tab content
            for (int i = 0; i < _tabs.Count; i++)
            {
                var tab = _tabs[i];
                var rect = tab.Content.GetComponent<RectTransform>();
                if (rect != null)
                {
                    // Force rebuild on this specific content
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rect);

                    // Try preferred height first
                    float height = LayoutUtility.GetPreferredHeight(rect);

                    // Fallback: measure children manually if preferred returns 0
                    if (height <= 0)
                    {
                        height = MeasureChildrenHeight(tab.Content.transform);
                    }

                    if (height > maxHeight)
                        maxHeight = height;
                }
            }

            // Step 4: Restore original state - deactivate non-selected tabs
            for (int i = 0; i < _tabs.Count; i++)
            {
                _tabs[i].Content.SetActive(i == originalIndex);
            }

            return maxHeight;
        }

        /// <summary>
        /// Manually measures children height as fallback.
        /// </summary>
        private float MeasureChildrenHeight(Transform parent)
        {
            float totalHeight = 0;
            int childCount = parent.childCount;

            for (int i = 0; i < childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy)
                    continue;

                var layoutElement = child.GetComponent<LayoutElement>();
                var rectTransform = child.GetComponent<RectTransform>();

                if (layoutElement != null && layoutElement.minHeight > 0)
                {
                    totalHeight += layoutElement.minHeight;
                }
                else if (rectTransform != null)
                {
                    float preferred = LayoutUtility.GetPreferredHeight(rectTransform);
                    if (preferred > 0)
                    {
                        totalHeight += preferred;
                    }
                    else
                    {
                        totalHeight += rectTransform.rect.height;
                    }
                }
            }

            // Add spacing from layout group if present
            var layoutGroup = parent.GetComponent<VerticalLayoutGroup>();
            if (layoutGroup != null && childCount > 1)
            {
                totalHeight += layoutGroup.spacing * (childCount - 1);
                totalHeight += layoutGroup.padding.top + layoutGroup.padding.bottom;
            }

            return totalHeight;
        }

        /// <summary>
        /// Gets the content container that holds all tab contents.
        /// Useful for setting a fixed height based on max tab content.
        /// </summary>
        public GameObject ContentContainer => _contentContainer;
    }
}
