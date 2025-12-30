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
            // Register panel root EARLY for hierarchy-based own UI detection
            // This must happen before any child components are created
            EnsurePanelRootRegistered();
            return UIStyles.CreateScrollablePanelLayout(ContentRoot, out scrollContent, out buttonRow, cardWidth);
        }

        /// <summary>
        /// Ensures this panel's root is registered for hierarchy-based own UI detection.
        /// Call this before creating any child components if not using CreateScrollablePanelLayout.
        /// </summary>
        protected void EnsurePanelRootRegistered()
        {
            if (UIRoot != null && !_panelRootRegistered)
            {
                TranslatorCore.RegisterPanelRoot(UIRoot);
                _panelRootRegistered = true;
            }
        }

        private bool _panelRootRegistered = false;

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

        #region Own UI Registration Helpers

        /// <summary>
        /// Registers a Text component as excluded from translation.
        /// Use for: mod title, language codes, config values, technical labels.
        /// </summary>
        protected void RegisterExcluded(Text text)
        {
            TranslatorCore.RegisterExcluded(text);
        }

        /// <summary>
        /// Registers a Text component for UI-specific translation.
        /// Use for: buttons, labels, descriptions that should be translated with the UI prompt.
        /// </summary>
        protected void RegisterUIText(Text text)
        {
            TranslatorCore.RegisterUIText(text);
        }

        /// <summary>
        /// Registers a TMPro text component as excluded from translation.
        /// Use for: mod title, language codes, config values, technical labels.
        /// </summary>
        protected void RegisterExcluded(TMPro.TMP_Text text)
        {
            TranslatorCore.RegisterExcluded(text);
        }

        /// <summary>
        /// Registers a TMPro text component for UI-specific translation.
        /// Use for: buttons, labels, descriptions that should be translated with the UI prompt.
        /// </summary>
        protected void RegisterUIText(TMPro.TMP_Text text)
        {
            TranslatorCore.RegisterUIText(text);
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

        /// <summary>
        /// Whether this panel uses center anchors for positioning.
        /// Override to false for panels like StatusOverlay that use corner anchors.
        /// </summary>
        protected virtual bool UsesCenterAnchors => true;

        // Track if we've shown the backdrop for this panel
        private bool _backdropShown = false;

        // Track previous position/size for change detection
        private Vector2 _lastSavedPosition;
        private Vector2 _lastSavedSize;
        private bool _hasLastSavedValues;

        // Track if initial sizing is complete (don't save during construction)
        private bool _initialSizingComplete;

        // Track if we need to do sizing on first show (deferred from init to when panel is visible)
        private bool _needsFirstShowSizing = true;

        // Track the dynamically calculated size (to preserve across SetDefaultSizeAndPosition calls)
        private Vector2 _dynamicSize;
        private bool _hasDynamicSize;

        // Content measurement cache
        private float _measuredContentHeight;
        private bool _contentMeasured;

        // Flag to ignore OnPanelResized during programmatic resizes
        private bool _isProgrammaticResize;

        /// <summary>
        /// Updates the dragger's resize cache without triggering the user resize save.
        /// Use this for programmatic size changes.
        /// </summary>
        private void UpdateDraggerCache()
        {
            if (Dragger != null)
            {
                _isProgrammaticResize = true;
                Dragger.OnEndResize();
            }
        }

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

        /// <summary>
        /// Override ConstructUI to use construction mode.
        /// This ensures all text created during panel construction is skipped from translation,
        /// preventing race conditions where texts are queued before we can register them.
        /// </summary>
        public override void ConstructUI()
        {
            // Enter construction mode BEFORE any UI is created
            // This makes ShouldSkipTranslation return true for all components during construction
            TranslatorCore.EnterConstructionMode();

            try
            {
                // Call base which creates title bar, content root, etc.
                base.ConstructUI();

                // Title bar and close button text are created by base.ConstructUI()
                // Register them as excluded so they're never translated even after construction mode ends
                // Note: We iterate manually to avoid IL2CPP issues with generic GetComponentsInChildren<T>
                if (TitleBar != null)
                {
                    ExcludeAllTextComponents(TitleBar.transform);
                }
            }
            finally
            {
                // Always exit construction mode, even if an exception occurs
                TranslatorCore.ExitConstructionMode();
            }
        }

        /// <summary>
        /// Recursively excludes all Text components from translation.
        /// Uses manual iteration to avoid IL2CPP issues with generic GetComponentsInChildren.
        /// </summary>
        private void ExcludeAllTextComponents(Transform parent)
        {
            if (parent == null) return;

            // Check this object for Text component
            var text = parent.GetComponent<UnityEngine.UI.Text>();
            if (text != null)
                TranslatorCore.RegisterExcluded(text);

            // Recursively check all children
            for (int i = 0; i < parent.childCount; i++)
            {
                ExcludeAllTextComponents(parent.GetChild(i));
            }
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

            // Dynamic sizing on FIRST SHOW - this is when Unity's layout is actually calculated
            if (active && _needsFirstShowSizing && UseDynamicSizing)
            {
                _needsFirstShowSizing = false;
                UniverseLib.RuntimeHelper.StartCoroutine(DelayedFirstShowSizing());
            }
        }

        /// <summary>
        /// Calculates and applies optimal size on first show, when Unity's layout is properly calculated.
        /// </summary>
        private System.Collections.IEnumerator DelayedFirstShowSizing()
        {
            // Wait for Unity's layout system to fully calculate (panel is now active)
            yield return null;
            yield return null;

            // Force layout rebuild now that panel is visible
            if (ContentRoot != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRoot.GetComponent<RectTransform>());
            }

            yield return null;

            // Now measure and apply optimal size
            _contentMeasured = false;
            CalculateAndApplyOptimalSize();
            EnsureValidPosition();

            // Update tracking values
            _lastSavedPosition = Rect.anchoredPosition;
            _lastSavedSize = new Vector2(Rect.rect.width, Rect.rect.height);
            _hasLastSavedValues = true;
            _initialSizingComplete = true;

            UpdateDraggerCache();
        }

        #region Dynamic Sizing

        /// <summary>
        /// Override to properly center the panel.
        /// Preserves dynamically calculated size if already set.
        /// </summary>
        public override void SetDefaultSizeAndPosition()
        {
            // Set anchors to center (for center-anchored panels)
            if (UsesCenterAnchors)
            {
                Rect.anchorMin = new Vector2(0.5f, 0.5f);
                Rect.anchorMax = new Vector2(0.5f, 0.5f);
                Rect.pivot = new Vector2(0.5f, 0.5f);
            }

            // Use dynamically calculated size if available, otherwise use declared size
            var screenDim = new Vector2(Screen.width, Screen.height);
            if (_hasDynamicSize)
            {
                // Preserve dynamic size (already calculated)
                Rect.sizeDelta = _dynamicSize;
            }
            else
            {
                // Initial size before dynamic sizing runs
                Rect.sizeDelta = new Vector2(
                    Mathf.Min(PanelWidth, UIStyles.CalculateMaxPanelWidth(screenDim.x)),
                    Mathf.Min(PanelHeight, UIStyles.CalculateMaxPanelHeight(screenDim.y))
                );
            }

            if (UsesCenterAnchors)
            {
                Rect.anchoredPosition = Vector2.zero;
            }

            EnsureValidPosition();

            UpdateDraggerCache();
        }

        protected override void LateConstructUI()
        {
            // Ensure panel root is registered (backup in case CreateScrollablePanelLayout wasn't used)
            EnsurePanelRootRegistered();

            // Apply anchors based on panel type
            if (UsesCenterAnchors)
            {
                // Center-anchored panels (most panels)
                Rect.anchorMin = new Vector2(0.5f, 0.5f);
                Rect.anchorMax = new Vector2(0.5f, 0.5f);
                Rect.pivot = new Vector2(0.5f, 0.5f);
            }
            // For non-center panels (e.g., StatusOverlay), keep the anchors set in SetDefaultSizeAndPosition()

            // Hook into dragger events for persistence
            if (Dragger != null)
            {
                Dragger.OnFinishResize += OnPanelResized;
                if (PersistWindowPreferences)
                {
                    Dragger.OnFinishDrag += OnPanelDragged;
                }
            }

            // For non-center-anchored panels, skip preference loading (they have fixed positions)
            if (!UsesCenterAnchors)
            {
                _needsFirstShowSizing = false; // Non-centered panels don't use dynamic sizing
                _initialSizingComplete = true;
                return;
            }

            // Try to load saved preferences (position only - size will be calculated on first show)
            WindowPreference pref = null;
            var prefs = TranslatorCore.Config.window_preferences;
            bool hasValidPreference = PersistWindowPreferences &&
                prefs.panels.TryGetValue(Name, out pref) &&
                pref.width > 0 && pref.height > 0 && prefs.screenWidth > 0;

            if (hasValidPreference)
            {
                // Apply saved position (scaled if resolution changed)
                var screenDim = new Vector2(Screen.width, Screen.height);
                float widthRatio = screenDim.x / prefs.screenWidth;
                float heightRatio = screenDim.y / prefs.screenHeight;
                bool resolutionChanged = Math.Abs(widthRatio - 1) > 0.1f || Math.Abs(heightRatio - 1) > 0.1f;

                // Check if saved position would be out of bounds with new resolution
                float newX = resolutionChanged ? pref.x * widthRatio : pref.x;
                float newY = resolutionChanged ? pref.y * heightRatio : pref.y;
                float halfWidth = (resolutionChanged ? PanelWidth : pref.width) / 2f;
                float halfHeight = (resolutionChanged ? PanelHeight : pref.height) / 2f;

                // Calculate screen bounds (panel is center-anchored, so position is relative to center)
                bool positionOutOfBounds = Math.Abs(newX) + halfWidth > screenDim.x / 2f ||
                                           Math.Abs(newY) + halfHeight > screenDim.y / 2f;

                if (positionOutOfBounds)
                {
                    // Invalidate saved preference - window would be outside screen
                    Rect.anchoredPosition = Vector2.zero;
                    pref.userResized = false;
                }
                else
                {
                    Rect.anchoredPosition = new Vector2(newX, newY);
                }

                // If user manually resized AND resolution didn't change, use saved size and skip dynamic sizing
                if (pref.userResized && !resolutionChanged && !positionOutOfBounds)
                {
                    Rect.sizeDelta = new Vector2(pref.width, pref.height);
                    _needsFirstShowSizing = false; // User already resized, don't override
                    _initialSizingComplete = true;
                }
                // Otherwise, keep _needsFirstShowSizing = true to calculate on first show
            }
            else
            {
                // No preference - center position, size will be calculated on first show
                Rect.anchoredPosition = Vector2.zero;
            }

            // Initialize tracking values
            _lastSavedPosition = Rect.anchoredPosition;
            _lastSavedSize = new Vector2(Rect.rect.width, Rect.rect.height);
            _hasLastSavedValues = true;

            // For non-dynamic sizing panels, set the fixed size now
            if (!UseDynamicSizing)
            {
                Rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
                _needsFirstShowSizing = false;
                _initialSizingComplete = true;
                EnsureValidPosition();
                UpdateDraggerCache();
            }
        }

        /// <summary>
        /// Called when user finishes resizing the panel.
        /// Max/min are now enforced during resize by UniverseLib via MaxHeight property.
        /// This just invalidates content cache and saves preferences if size actually changed.
        /// </summary>
        private void OnPanelResized()
        {
            // Invalidate content measurement cache since size changed
            _contentMeasured = false;

            // Ignore programmatic resizes (dynamic sizing, etc.)
            if (_isProgrammaticResize)
            {
                _isProgrammaticResize = false;
                return;
            }

            // Don't save during initial construction - only after user interaction
            if (!_initialSizingComplete) return;

            // Save preference (user manually resized) - only if persistence enabled and size actually changed
            if (PersistWindowPreferences && HasSizeChanged())
            {
                SaveWindowPreference(userResized: true);
            }
        }

        /// <summary>
        /// Called when user finishes dragging the panel.
        /// Saves position preference only if position actually changed.
        /// </summary>
        private void OnPanelDragged()
        {
            // Don't save during initial construction - only after user interaction
            if (!_initialSizingComplete) return;

            if (HasPositionChanged())
            {
                SaveWindowPreference(userResized: false);
            }
        }

        /// <summary>
        /// Checks if the panel position has changed since last save.
        /// </summary>
        private bool HasPositionChanged()
        {
            if (!_hasLastSavedValues) return true;
            const float tolerance = 1f;
            return Math.Abs(Rect.anchoredPosition.x - _lastSavedPosition.x) > tolerance ||
                   Math.Abs(Rect.anchoredPosition.y - _lastSavedPosition.y) > tolerance;
        }

        /// <summary>
        /// Checks if the panel size has changed since last save.
        /// Uses Rect.rect (actual rendered size) because UniverseLib resizes via anchors, not sizeDelta.
        /// </summary>
        private bool HasSizeChanged()
        {
            if (!_hasLastSavedValues) return true;
            const float tolerance = 1f;
            // Use rect.width/height instead of sizeDelta because UniverseLib changes anchors when resizing
            return Math.Abs(Rect.rect.width - _lastSavedSize.x) > tolerance ||
                   Math.Abs(Rect.rect.height - _lastSavedSize.y) > tolerance;
        }

        /// <summary>
        /// Measures the preferred content height using layout system.
        /// Recursively measures child elements to handle nested layouts.
        /// </summary>
        protected float MeasureContentHeight()
        {
            if (ContentRoot == null)
            {
                return PanelHeight;
            }

            // Force complete layout rebuild first
            LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRoot.GetComponent<RectTransform>());

            float contentHeight = 0;

            // Find the scrollContent (has ContentSizeFitter) - this is where our cards are
            var sizeFitter = ContentRoot.GetComponentInChildren<ContentSizeFitter>();
            if (sizeFitter != null)
            {
                var scrollContent = sizeFitter.gameObject;
                var scrollContentRect = scrollContent.GetComponent<RectTransform>();
                LayoutRebuilder.ForceRebuildLayoutImmediate(scrollContentRect);

                // Method 1: Use Unity's preferred height (most accurate when layout is calculated)
                float unityPreferredHeight = LayoutUtility.GetPreferredHeight(scrollContentRect);

                // Method 2: Measure recursively (fallback when Unity returns 0)
                float childrenHeight = MeasureChildrenRecursive(scrollContent.transform);

                // Add spacing between direct children (from VerticalLayoutGroup)
                var layoutGroup = scrollContent.GetComponent<VerticalLayoutGroup>();
                if (layoutGroup != null)
                {
                    // Use helper for IL2CPP compatibility (foreach on Transform doesn't work in IL2CPP)
                    int activeChildren = UIHelpers.CountActiveChildren(scrollContent.transform);
                    if (activeChildren > 1)
                    {
                        childrenHeight += layoutGroup.spacing * (activeChildren - 1);
                    }
                    // Add padding
                    childrenHeight += layoutGroup.padding.top + layoutGroup.padding.bottom;
                }

                // Use the MAXIMUM of both methods - Unity's calculation is more accurate when available
                contentHeight = Mathf.Max(unityPreferredHeight, childrenHeight);
            }
            else
            {
                // Fallback: measure ContentRoot directly (for panels without scroll)
                contentHeight = LayoutUtility.GetPreferredHeight(ContentRoot.GetComponent<RectTransform>());
                if (contentHeight <= 0)
                {
                    contentHeight = MeasureChildrenRecursive(ContentRoot.transform);
                }
            }

            // Measure chrome dynamically instead of hardcoding
            float chromeHeight = MeasureChromeHeight();

            _measuredContentHeight = contentHeight + chromeHeight;
            _contentMeasured = true;

            return _measuredContentHeight;
        }

        /// <summary>
        /// Measures the chrome height (non-scrollable parts: title bar, button row, padding, spacing).
        /// Dynamically finds and measures actual UI elements instead of using hardcoded values.
        /// </summary>
        private float MeasureChromeHeight()
        {
            if (ContentRoot == null)
            {
                return UIStyles.PanelPadding * 2;
            }

            float chromeHeight = 0;

            // Measure ContentRoot's layout group padding (if any)
            var contentVlg = ContentRoot.GetComponent<VerticalLayoutGroup>();
            if (contentVlg != null)
            {
                chromeHeight += contentVlg.padding.top + contentVlg.padding.bottom;
            }
            else
            {
                // Fallback to UIStyles constant
                chromeHeight += UIStyles.PanelPadding * 2;
            }

            // Find and measure title bar
            var titleBar = ContentRoot.transform.Find("TitleBar");
            if (titleBar != null && titleBar.gameObject.activeSelf)
            {
                var titleRect = titleBar.GetComponent<RectTransform>();
                if (titleRect != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(titleRect);
                    float titleHeight = LayoutUtility.GetPreferredHeight(titleRect);
                    if (titleHeight <= 0)
                    {
                        var titleLayout = titleBar.GetComponent<LayoutElement>();
                        titleHeight = titleLayout != null ? titleLayout.minHeight : titleRect.rect.height;
                    }
                    chromeHeight += titleHeight;
                }
            }

            // Find and measure button row (can be "ButtonRow" or "FooterButtons")
            Transform buttonRow = ContentRoot.transform.Find("ButtonRow") ?? ContentRoot.transform.Find("FooterButtons");
            if (buttonRow != null && buttonRow.gameObject.activeSelf)
            {
                var buttonRect = buttonRow.GetComponent<RectTransform>();
                if (buttonRect != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(buttonRect);
                    float buttonHeight = LayoutUtility.GetPreferredHeight(buttonRect);
                    if (buttonHeight <= 0)
                    {
                        var buttonLayout = buttonRow.GetComponent<LayoutElement>();
                        buttonHeight = buttonLayout != null ? buttonLayout.minHeight : buttonRect.rect.height;
                    }
                    chromeHeight += buttonHeight;
                }
            }

            // Add spacing between elements
            if (contentVlg != null)
            {
                // Count visible direct children to calculate spacing
                // Use helper for IL2CPP compatibility (foreach on Transform doesn't work in IL2CPP)
                int visibleChildren = UIHelpers.CountActiveChildren(ContentRoot.transform);
                if (visibleChildren > 1)
                {
                    chromeHeight += contentVlg.spacing * (visibleChildren - 1);
                }
            }
            else
            {
                // Fallback: assume 2 gaps with default spacing
                chromeHeight += UIStyles.ElementSpacing * 2;
            }

            return chromeHeight;
        }

        /// <summary>
        /// Recursively measures children heights, going up to maxDepth levels deep.
        /// Uses manual iteration for IL2CPP compatibility (foreach on Transform doesn't work in IL2CPP).
        /// </summary>
        private float MeasureChildrenRecursive(Transform parent, int depth = 0, int maxDepth = 10)
        {
            if (depth > maxDepth) return 0;

            float totalHeight = 0;
            int childCount = 0;

            // Manual iteration for IL2CPP compatibility
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (!child.gameObject.activeSelf) continue;

                var childRect = child.GetComponent<RectTransform>();
                if (childRect == null) continue;

                LayoutRebuilder.ForceRebuildLayoutImmediate(childRect);

                // Try to get preferred height first
                float childHeight = LayoutUtility.GetPreferredHeight(childRect);

                // If preferred is 0, check LayoutElement.minHeight
                if (childHeight <= 0)
                {
                    var layoutElement = child.GetComponent<LayoutElement>();
                    if (layoutElement != null && layoutElement.minHeight > 0)
                    {
                        childHeight = layoutElement.minHeight;
                    }
                }

                // If still 0, try rect height
                if (childHeight <= 0)
                {
                    childHeight = childRect.rect.height;
                }

                // If still 0 and has children, measure children recursively
                if (childHeight <= 0 && child.childCount > 0)
                {
                    childHeight = MeasureChildrenRecursive(child, depth + 1, maxDepth);

                    // Add layout group padding/spacing if present
                    var vlg = child.GetComponent<VerticalLayoutGroup>();
                    if (vlg != null)
                    {
                        childHeight += vlg.padding.top + vlg.padding.bottom;
                    }
                }

                if (childHeight > 0)
                {
                    totalHeight += childHeight;
                    childCount++;
                }
            }

            // Add spacing between children
            var parentVlg = parent.GetComponent<VerticalLayoutGroup>();
            if (parentVlg != null && childCount > 1)
            {
                totalHeight += parentVlg.spacing * (childCount - 1);
            }

            return totalHeight;
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
                UpdateDraggerCache();
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

            // Store and apply dynamic size
            _dynamicSize = new Vector2(optimalWidth, optimalHeight);
            _hasDynamicSize = true;
            Rect.sizeDelta = _dynamicSize;

            // Update resize cache so cursor appears correctly
            UpdateDraggerCache();
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
            var prefs = TranslatorCore.Config.window_preferences;

            // Get or create preference
            if (!prefs.panels.TryGetValue(Name, out var pref))
            {
                pref = new WindowPreference();
            }

            // Update per-panel values
            // Use Rect.rect for size because UniverseLib resizes via anchors, not sizeDelta
            pref.x = Rect.anchoredPosition.x;
            pref.y = Rect.anchoredPosition.y;
            pref.width = Rect.rect.width;
            pref.height = Rect.rect.height;

            // Only set userResized to true if explicitly requested (don't reset to false)
            if (userResized)
            {
                pref.userResized = true;
            }

            prefs.panels[Name] = pref;

            // Update global screen dimensions
            prefs.screenWidth = Mathf.RoundToInt(screenDim.x);
            prefs.screenHeight = Mathf.RoundToInt(screenDim.y);

            // Update tracking values
            _lastSavedPosition = Rect.anchoredPosition;
            _lastSavedSize = new Vector2(Rect.rect.width, Rect.rect.height);
            _hasLastSavedValues = true;

            // Save config (debounced by TranslatorCore)
            TranslatorCore.SaveConfig();
        }

        #endregion
    }
}
