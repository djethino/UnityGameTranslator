using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UniverseLib.UI.Panels;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Base class for all translator panels.
    /// Provides consistent sizing and centering behavior similar to the old IMGUI windows.
    /// Uses UIStyles for centralized theming.
    /// </summary>
    public abstract class TranslatorPanelBase : PanelBase
    {
        #region Style Helpers (delegates to UIStyles)

        /// <summary>
        /// Helper to set background color on a UI group/box.
        /// </summary>
        protected static void SetBackgroundColor(GameObject obj, Color color)
        {
            UIStyles.SetBackground(obj, color);
        }

        /// <summary>
        /// Creates a styled card container with proper padding
        /// </summary>
        protected GameObject CreateCard(GameObject parent, string name, int minHeight = 0)
        {
            return UIStyles.CreateCard(parent, name, minHeight);
        }

        /// <summary>
        /// Creates a styled section box
        /// </summary>
        protected GameObject CreateSection(GameObject parent, string name, int minHeight = 0)
        {
            return UIStyles.CreateSection(parent, name, minHeight);
        }

        /// <summary>
        /// Creates a flexible spacer for vertical centering
        /// </summary>
        protected GameObject CreateFlexSpacer(GameObject parent, string name = "Spacer")
        {
            return UIStyles.CreateFlexSpacer(parent, name);
        }

        /// <summary>
        /// Creates a styled title label
        /// </summary>
        protected Text CreateTitle(GameObject parent, string name, string text)
        {
            return UIStyles.CreateTitle(parent, name, text);
        }

        /// <summary>
        /// Creates a styled description label
        /// </summary>
        protected Text CreateDescription(GameObject parent, string name, string text)
        {
            return UIStyles.CreateDescription(parent, name, text);
        }

        /// <summary>
        /// Creates a navigation button row
        /// </summary>
        protected GameObject CreateButtonRow(GameObject parent, string name = "ButtonRow")
        {
            return UIStyles.CreateButtonRow(parent, name);
        }

        /// <summary>
        /// Creates a primary styled button
        /// </summary>
        protected ButtonRef CreatePrimaryButton(GameObject parent, string name, string text, int minWidth = 130)
        {
            return UIStyles.CreatePrimaryButton(parent, name, text, minWidth);
        }

        /// <summary>
        /// Creates a secondary styled button
        /// </summary>
        protected ButtonRef CreateSecondaryButton(GameObject parent, string name, string text, int minWidth = 110)
        {
            return UIStyles.CreateSecondaryButton(parent, name, text, minWidth);
        }

        /// <summary>
        /// Creates a scrollable panel layout with fixed footer buttons.
        /// This is the recommended way to build panel content - content scrolls if needed,
        /// while buttons stay fixed at the bottom.
        /// </summary>
        protected GameObject CreateScrollablePanelLayout(out GameObject scrollContent, out GameObject buttonRow, int cardWidth = 420)
        {
            return UIStyles.CreateScrollablePanelLayout(ContentRoot, out scrollContent, out buttonRow, cardWidth);
        }

        /// <summary>
        /// Creates an adaptive card that sizes to its content (no fixed minHeight).
        /// Use inside scrollContent from CreateScrollablePanelLayout.
        /// </summary>
        protected GameObject CreateAdaptiveCard(GameObject parent, string name, int width = 420)
        {
            return UIStyles.CreateAdaptiveCard(parent, name, width);
        }

        /// <summary>
        /// Creates a styled info label (secondary color, normal font size).
        /// </summary>
        protected Text CreateInfoLabel(GameObject parent, string name, string text)
        {
            return UIStyles.CreateInfoLabel(parent, name, text);
        }

        /// <summary>
        /// Creates a small styled label (muted color, small font).
        /// </summary>
        protected Text CreateSmallLabel(GameObject parent, string name, string text)
        {
            return UIStyles.CreateSmallLabel(parent, name, text);
        }

        /// <summary>
        /// Creates a centered status label for displaying status messages.
        /// </summary>
        protected Text CreateStatusLabel(GameObject parent, string name)
        {
            return UIStyles.CreateStatusLabel(parent, name);
        }

        /// <summary>
        /// Creates a styled input field with proper background and sizing.
        /// </summary>
        protected InputFieldRef CreateStyledInputField(
            GameObject parent, string name, string placeholder, int minHeight = 0)
        {
            return UIStyles.CreateStyledInputField(parent, name, placeholder, minHeight);
        }

        /// <summary>
        /// Creates a list item row with consistent styling.
        /// </summary>
        protected GameObject CreateListItem(GameObject parent, string name, int minHeight = 0)
        {
            return UIStyles.CreateListItem(parent, name, minHeight);
        }

        #endregion

        /// <summary>
        /// Desired width of the panel in pixels.
        /// </summary>
        public abstract int PanelWidth { get; }

        /// <summary>
        /// Desired height of the panel in pixels.
        /// </summary>
        public abstract int PanelHeight { get; }

        /// <summary>
        /// Whether this panel should show the backdrop when active.
        /// Override to false for panels like StatusOverlay that shouldn't dim the screen.
        /// </summary>
        protected virtual bool UseBackdrop => true;

        /// <summary>
        /// Minimum panel height for resize constraints.
        /// Override to set a different minimum per panel.
        /// </summary>
        protected virtual int MinPanelHeight => MinHeight;

        /// <summary>
        /// Whether this panel should use dynamic content-based sizing.
        /// Override to false for fixed-size panels like StatusOverlay.
        /// </summary>
        protected virtual bool UseDynamicSizing => true;

        /// <summary>
        /// Whether this panel should persist window preferences (position, size).
        /// Override to false for temporary panels like dialogs or wizards.
        /// </summary>
        protected virtual bool PersistWindowPreferences => true;

        // Track if we've shown the backdrop for this panel
        private bool _backdropShown = false;

        // Content measurement cache
        private float _measuredContentHeight;
        private bool _contentMeasured;

        // Use center anchor
        public override Vector2 DefaultAnchorMin => new(0.5f, 0.5f);
        public override Vector2 DefaultAnchorMax => new(0.5f, 0.5f);
        public override bool CanDragAndResize => true;

        /// <summary>
        /// Maximum height for resize - based on content height.
        /// This is checked by UniverseLib during resize to prevent extending beyond content.
        /// </summary>
        public override int MaxHeight
        {
            get
            {
                if (!UseDynamicSizing) return int.MaxValue;
                if (!_contentMeasured) MeasureContentHeight();
                return Mathf.RoundToInt(_measuredContentHeight);
            }
        }

        protected TranslatorPanelBase(UIBase owner) : base(owner)
        {
        }

        public override void SetActive(bool active)
        {
            // Handle backdrop
            if (UseBackdrop)
            {
                if (active && !_backdropShown)
                {
                    UIStyles.ShowBackdrop(Owner);
                    _backdropShown = true;
                }
                else if (!active && _backdropShown)
                {
                    UIStyles.HideBackdrop();
                    _backdropShown = false;
                }
            }

            base.SetActive(active);
        }

        #region Dynamic Sizing

        /// <summary>
        /// Override to properly center the panel with dynamic or fixed dimensions.
        /// </summary>
        public override void SetDefaultSizeAndPosition()
        {
            // Set anchors to center
            Rect.anchorMin = new Vector2(0.5f, 0.5f);
            Rect.anchorMax = new Vector2(0.5f, 0.5f);

            // Set pivot to center for proper centering
            Rect.pivot = new Vector2(0.5f, 0.5f);

            // Try to load saved preference first
            if (!LoadWindowPreference())
            {
                // No saved preference - calculate optimal size
                if (UseDynamicSizing)
                {
                    CalculateAndApplyOptimalSize();
                }
                else
                {
                    // Fixed size panels just use their declared dimensions
                    Rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
                }

                // Position at center (0,0 with center anchor = screen center)
                Rect.anchoredPosition = Vector2.zero;
            }

            // Ensure within screen bounds
            EnsureValidPosition();

            if (Dragger != null)
                Dragger.OnEndResize();
        }

        protected override void LateConstructUI()
        {
            // Re-apply sizing after layout is done
            SetDefaultSizeAndPosition();

            // Hook into dragger events for resize constraints (always) and persistence (if enabled)
            if (Dragger != null)
            {
                Dragger.OnFinishResize += OnPanelResized;
                if (PersistWindowPreferences)
                {
                    Dragger.OnFinishDrag += OnPanelDragged;
                }
            }
        }

        /// <summary>
        /// Called when user finishes resizing the panel.
        /// Max/min are now enforced during resize by UniverseLib via MaxHeight property.
        /// This just invalidates content cache and saves preferences.
        /// </summary>
        private void OnPanelResized()
        {
            // Invalidate content measurement cache since size changed
            _contentMeasured = false;

            // Save preference (user manually resized) - only if persistence enabled
            if (PersistWindowPreferences)
            {
                SaveWindowPreference(userResized: true);
            }
        }

        /// <summary>
        /// Called when user finishes dragging the panel.
        /// Saves position preference.
        /// </summary>
        private void OnPanelDragged()
        {
            SaveWindowPreference(userResized: false);
        }

        /// <summary>
        /// Measures the preferred content height using layout system.
        /// Finds the scrollContent (with ContentSizeFitter) to measure actual content,
        /// not the ScrollView which has flexibleHeight.
        /// </summary>
        protected float MeasureContentHeight()
        {
            if (ContentRoot == null) return PanelHeight;

            // Force layout rebuild to get accurate measurements
            LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRoot.GetComponent<RectTransform>());

            float preferredHeight = 0;

            // Find the scrollContent (has ContentSizeFitter) to measure actual content size
            // The scroll view itself has flexibleHeight:9999 which would give wrong measurement
            var sizeFitter = ContentRoot.GetComponentInChildren<ContentSizeFitter>();
            if (sizeFitter != null)
            {
                var sizeFitterRect = sizeFitter.GetComponent<RectTransform>();
                LayoutRebuilder.ForceRebuildLayoutImmediate(sizeFitterRect);
                preferredHeight = LayoutUtility.GetPreferredHeight(sizeFitterRect);
            }
            else
            {
                // Fallback: measure ContentRoot directly (for panels without scroll)
                preferredHeight = LayoutUtility.GetPreferredHeight(ContentRoot.GetComponent<RectTransform>());
            }

            // Add chrome: title bar (~25px) + button row (~45px) + padding
            var chromeHeight = 25 + 45 + UIStyles.PanelPadding * 2;

            _measuredContentHeight = preferredHeight + chromeHeight;
            _contentMeasured = true;

            return _measuredContentHeight;
        }

        /// <summary>
        /// Gets the current maximum content height (what the panel could expand to).
        /// Used to enforce resize limits.
        /// </summary>
        protected float GetMaxContentHeight()
        {
            if (!_contentMeasured)
                MeasureContentHeight();
            return _measuredContentHeight;
        }

        /// <summary>
        /// Call this when panel content changes dynamically to recalculate size.
        /// Waits one frame for layout to update before measuring.
        /// </summary>
        protected void RecalculateSize()
        {
            if (!UseDynamicSizing) return;
            UniverseLib.RuntimeHelper.StartCoroutine(DelayedRecalculateSize());
        }

        private System.Collections.IEnumerator DelayedRecalculateSize()
        {
            // Wait one frame for layout to update after content changes
            yield return null;
            _contentMeasured = false; // Force re-measurement
            CalculateAndApplyOptimalSize();
        }

        /// <summary>
        /// Calculates and applies optimal panel size based on content and screen.
        /// </summary>
        protected void CalculateAndApplyOptimalSize()
        {
            if (!UseDynamicSizing)
            {
                Rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
                Dragger?.OnEndResize(); // Update resize cache
                return;
            }

            var screenDim = new Vector2(Screen.width, Screen.height);

            // Measure content
            float contentHeight = MeasureContentHeight();

            // Calculate optimal height: min(content, screen-bounded)
            int optimalHeight = UIStyles.CalculateOptimalPanelHeight(
                contentHeight,
                screenDim.y,
                MinPanelHeight
            );

            // Width stays at declared PanelWidth (or screen-bounded if too wide)
            int optimalWidth = Mathf.Min(PanelWidth, UIStyles.CalculateMaxPanelWidth(screenDim.x));

            Rect.sizeDelta = new Vector2(optimalWidth, optimalHeight);

            // Update resize cache so cursor appears correctly
            Dragger?.OnEndResize();
        }

        #endregion

        #region Window Preference Persistence

        /// <summary>
        /// Saves current window position and size to config.
        /// </summary>
        protected void SaveWindowPreference(bool userResized)
        {
            if (!PersistWindowPreferences) return;

            var screenDim = new Vector2(Screen.width, Screen.height);

            // Get or create preference
            if (!TranslatorCore.Config.window_preferences.panels.TryGetValue(Name, out var pref))
            {
                pref = new WindowPreference();
            }

            // Update with current values
            pref.x = Rect.anchoredPosition.x;
            pref.y = Rect.anchoredPosition.y;
            pref.width = Rect.sizeDelta.x;
            pref.height = Rect.sizeDelta.y;
            pref.screenWidth = Mathf.RoundToInt(screenDim.x);
            pref.screenHeight = Mathf.RoundToInt(screenDim.y);

            // Only set userResized to true if explicitly requested (don't reset to false)
            if (userResized)
            {
                pref.userResized = true;
            }

            TranslatorCore.Config.window_preferences.panels[Name] = pref;

            // Save config (debounced by TranslatorCore)
            TranslatorCore.SaveConfig();
        }

        /// <summary>
        /// Loads and applies saved window preference if available.
        /// Returns true if preference was applied, false if using defaults.
        /// </summary>
        protected bool LoadWindowPreference()
        {
            if (!PersistWindowPreferences) return false;

            if (!TranslatorCore.Config.window_preferences.panels.TryGetValue(Name, out var pref))
                return false;

            // Validate saved preference has reasonable values
            if (pref.width <= 0 || pref.height <= 0 || pref.screenWidth <= 0 || pref.screenHeight <= 0)
                return false;

            var screenDim = new Vector2(Screen.width, Screen.height);

            // Handle resolution changes gracefully
            float widthRatio = screenDim.x / pref.screenWidth;
            float heightRatio = screenDim.y / pref.screenHeight;
            bool resolutionChanged = Math.Abs(widthRatio - 1) > 0.1f || Math.Abs(heightRatio - 1) > 0.1f;

            if (resolutionChanged)
            {
                // Scale position proportionally
                Rect.anchoredPosition = new Vector2(
                    pref.x * widthRatio,
                    pref.y * heightRatio
                );

                // Recalculate optimal size for new resolution
                if (UseDynamicSizing)
                {
                    CalculateAndApplyOptimalSize();
                }
                else
                {
                    Rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
                }
            }
            else
            {
                // Apply saved dimensions directly
                Rect.anchoredPosition = new Vector2(pref.x, pref.y);
                Rect.sizeDelta = new Vector2(pref.width, pref.height);
            }

            return true;
        }

        #endregion
    }
}
