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

        // Track if we've shown the backdrop for this panel
        private bool _backdropShown = false;

        // Use center anchor
        public override Vector2 DefaultAnchorMin => new(0.5f, 0.5f);
        public override Vector2 DefaultAnchorMax => new(0.5f, 0.5f);
        public override bool CanDragAndResize => true;

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

        /// <summary>
        /// Override to properly center the panel with fixed dimensions.
        /// </summary>
        public override void SetDefaultSizeAndPosition()
        {
            // Set anchors to center
            Rect.anchorMin = new Vector2(0.5f, 0.5f);
            Rect.anchorMax = new Vector2(0.5f, 0.5f);

            // Set pivot to center for proper centering
            Rect.pivot = new Vector2(0.5f, 0.5f);

            // Set size explicitly
            Rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);

            // Position at center (0,0 with center anchor = screen center)
            Rect.anchoredPosition = Vector2.zero;

            // Ensure within screen bounds
            EnsureValidPosition();

            if (Dragger != null)
                Dragger.OnEndResize();
        }

        protected override void LateConstructUI()
        {
            // Re-apply sizing after layout is done
            SetDefaultSizeAndPosition();
        }
    }
}
