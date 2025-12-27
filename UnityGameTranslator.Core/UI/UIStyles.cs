using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// Centralized styling system for the translator UI.
    /// Similar to CSS, this provides consistent theming across all panels.
    /// </summary>
    public static class UIStyles
    {
        #region Backdrop System

        private static GameObject _backdrop;
        private static int _backdropRefCount = 0;

        /// <summary>
        /// Shows the backdrop (darkened background). Reference counted - multiple panels can request it.
        /// </summary>
        public static void ShowBackdrop(UIBase owner)
        {
            if (_backdrop == null && owner != null)
            {
                CreateBackdrop(owner);
            }

            _backdropRefCount++;
            if (_backdrop != null)
            {
                _backdrop.SetActive(true);
            }
        }

        /// <summary>
        /// Hides the backdrop. Only actually hides when all references are released.
        /// </summary>
        public static void HideBackdrop()
        {
            _backdropRefCount--;
            if (_backdropRefCount <= 0)
            {
                _backdropRefCount = 0;
                if (_backdrop != null)
                {
                    _backdrop.SetActive(false);
                }
            }
        }

        private static void CreateBackdrop(UIBase owner)
        {
            // Create backdrop as a child of the UI canvas
            _backdrop = new GameObject("TranslatorBackdrop");
            _backdrop.transform.SetParent(owner.RootObject.transform.parent, false);

            // Add RectTransform to fill screen
            var rect = _backdrop.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;

            // Add semi-transparent black image
            var image = _backdrop.AddComponent<Image>();
            image.color = BackdropColor;
            image.raycastTarget = true; // Block clicks behind

            // Make sure it's behind other UI elements but still visible
            _backdrop.transform.SetAsFirstSibling();

            // Start hidden
            _backdrop.SetActive(false);
        }

        #endregion

        #region Colors

        // Backdrop (screen dimming when panel is open)
        public static readonly Color BackdropColor = new Color(0f, 0f, 0f, 0.6f);

        // Background colors
        public static readonly Color PanelBackground = new Color(0.12f, 0.12f, 0.14f, 0.95f);
        public static readonly Color CardBackground = new Color(0.16f, 0.16f, 0.19f, 0.98f);
        public static readonly Color SectionBackground = new Color(0.12f, 0.12f, 0.14f, 0.85f);
        public static readonly Color InputBackground = new Color(0.08f, 0.08f, 0.10f, 0.9f);

        // Text colors
        public static readonly Color TextPrimary = new Color(0.92f, 0.92f, 0.95f);
        public static readonly Color TextSecondary = new Color(0.7f, 0.7f, 0.75f);
        public static readonly Color TextMuted = new Color(0.5f, 0.5f, 0.55f);
        public static readonly Color TextAccent = new Color(0.4f, 0.85f, 0.95f);

        // Button colors
        public static readonly Color ButtonPrimary = new Color(0.22f, 0.52f, 0.72f);
        public static readonly Color ButtonSecondary = new Color(0.28f, 0.28f, 0.32f);
        public static readonly Color ButtonSuccess = new Color(0.2f, 0.6f, 0.35f);
        public static readonly Color ButtonDanger = new Color(0.7f, 0.25f, 0.25f);
        public static readonly Color ButtonHover = new Color(0.35f, 0.35f, 0.4f);

        // Status colors
        public static readonly Color StatusSuccess = new Color(0.3f, 0.85f, 0.4f);
        public static readonly Color StatusWarning = new Color(1f, 0.8f, 0.3f);
        public static readonly Color StatusError = new Color(1f, 0.35f, 0.35f);
        public static readonly Color StatusInfo = new Color(0.4f, 0.75f, 1f);

        #endregion

        #region Dimensions

        // Padding & Margins
        public static readonly int PanelPadding = 20;
        public static readonly int CardPadding = 25;
        public static readonly int SectionPadding = 15;
        public static readonly int ElementSpacing = 10;
        public static readonly int SmallSpacing = 5;

        // Component heights
        public static readonly int TitleHeight = 40;
        public static readonly int SectionTitleHeight = 25;
        public static readonly int ButtonHeight = 38;
        public static readonly int SmallButtonHeight = 30;
        public static readonly int InputHeight = 32;
        public static readonly int LabelHeight = 24;
        public static readonly int ToggleHeight = 28;

        // Font sizes
        public static readonly int FontSizeTitle = 20;
        public static readonly int FontSizeSectionTitle = 16;
        public static readonly int FontSizeNormal = 14;
        public static readonly int FontSizeSmall = 12;
        public static readonly int FontSizeHint = 11;

        #endregion

        #region Helper Methods

        /// <summary>
        /// Sets background color on a UI element (Image component)
        /// </summary>
        public static void SetBackground(GameObject obj, Color color)
        {
            var image = obj.GetComponent<Image>();
            if (image != null)
            {
                image.color = color;
            }
        }

        /// <summary>
        /// Creates a styled card container with proper padding and centering.
        /// Cards are the main content containers.
        /// </summary>
        public static GameObject CreateCard(GameObject parent, string name, int minHeight = 0, int width = 420)
        {
            // Create a horizontal wrapper to center the card
            var wrapper = UIFactory.CreateHorizontalGroup(parent, name + "_Wrapper", false, false, true, true, 0);
            UIFactory.SetLayoutElement(wrapper, flexibleWidth: 9999, flexibleHeight: 0);
            var wrapperLayout = wrapper.GetComponent<HorizontalLayoutGroup>();
            if (wrapperLayout != null)
            {
                wrapperLayout.childAlignment = TextAnchor.MiddleCenter;
                wrapperLayout.childForceExpandWidth = false;
                wrapperLayout.childForceExpandHeight = false;
            }

            // Create the actual card inside the wrapper
            var card = UIFactory.CreateVerticalGroup(wrapper, name, false, false, true, true, ElementSpacing);

            // Fixed width, not flexible - this allows centering
            if (minHeight > 0)
                UIFactory.SetLayoutElement(card, minWidth: width, preferredWidth: width, minHeight: minHeight);
            else
                UIFactory.SetLayoutElement(card, minWidth: width, preferredWidth: width);

            SetBackground(card, CardBackground);

            // Add subtle border effect with slightly darker outline
            var outline = card.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color(0.08f, 0.08f, 0.1f, 0.8f);
            outline.effectDistance = new Vector2(1, -1);

            var layout = card.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = new RectOffset(CardPadding, CardPadding, CardPadding, CardPadding);
                layout.childAlignment = TextAnchor.MiddleCenter;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
            }

            return card;
        }

        /// <summary>
        /// Creates a styled section box (smaller than card, for grouping related options)
        /// </summary>
        public static GameObject CreateSection(GameObject parent, string name, int minHeight = 0)
        {
            var section = UIFactory.CreateVerticalGroup(parent, name, false, false, true, true, SmallSpacing);

            if (minHeight > 0)
                UIFactory.SetLayoutElement(section, minHeight: minHeight, flexibleWidth: 9999);
            else
                UIFactory.SetLayoutElement(section, flexibleWidth: 9999);

            SetBackground(section, SectionBackground);

            var layout = section.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = new RectOffset(SectionPadding, SectionPadding, SectionPadding, SectionPadding);
            }

            return section;
        }

        /// <summary>
        /// Creates a flexible spacer for vertical centering
        /// </summary>
        public static GameObject CreateFlexSpacer(GameObject parent, string name = "Spacer")
        {
            var spacer = UIFactory.CreateUIObject(name, parent);
            UIFactory.SetLayoutElement(spacer, flexibleHeight: 9999);
            return spacer;
        }

        /// <summary>
        /// Creates a fixed-height spacer
        /// </summary>
        public static GameObject CreateSpacer(GameObject parent, int height, string name = "Spacer")
        {
            var spacer = UIFactory.CreateUIObject(name, parent);
            UIFactory.SetLayoutElement(spacer, minHeight: height);
            return spacer;
        }

        /// <summary>
        /// Creates a styled title label
        /// </summary>
        public static Text CreateTitle(GameObject parent, string name, string text)
        {
            var label = UIFactory.CreateLabel(parent, name, text, TextAnchor.MiddleCenter);
            label.fontSize = FontSizeTitle;
            label.fontStyle = FontStyle.Bold;
            label.color = TextPrimary;
            UIFactory.SetLayoutElement(label.gameObject, minHeight: TitleHeight);
            return label;
        }

        /// <summary>
        /// Creates a styled section title
        /// </summary>
        public static Text CreateSectionTitle(GameObject parent, string name, string text)
        {
            var label = UIFactory.CreateLabel(parent, name, text, TextAnchor.MiddleLeft);
            label.fontSize = FontSizeSectionTitle;
            label.fontStyle = FontStyle.Bold;
            label.color = TextPrimary;
            UIFactory.SetLayoutElement(label.gameObject, minHeight: SectionTitleHeight);
            return label;
        }

        /// <summary>
        /// Creates a styled description label
        /// </summary>
        public static Text CreateDescription(GameObject parent, string name, string text)
        {
            var label = UIFactory.CreateLabel(parent, name, text, TextAnchor.MiddleCenter);
            label.fontSize = FontSizeNormal;
            label.color = TextSecondary;
            UIFactory.SetLayoutElement(label.gameObject, minHeight: LabelHeight);
            return label;
        }

        /// <summary>
        /// Creates a styled hint/caption label
        /// </summary>
        public static Text CreateHint(GameObject parent, string name, string text)
        {
            var label = UIFactory.CreateLabel(parent, name, text, TextAnchor.MiddleLeft);
            label.fontSize = FontSizeHint;
            label.fontStyle = FontStyle.Italic;
            label.color = TextMuted;
            UIFactory.SetLayoutElement(label.gameObject, minHeight: 18);
            return label;
        }

        /// <summary>
        /// Creates a primary styled button
        /// </summary>
        public static ButtonRef CreatePrimaryButton(GameObject parent, string name, string text, int minWidth = 130)
        {
            var btn = UIFactory.CreateButton(parent, name, text);
            UIFactory.SetLayoutElement(btn.Component.gameObject, minWidth: minWidth, minHeight: ButtonHeight);
            SetBackground(btn.Component.gameObject, ButtonPrimary);
            return btn;
        }

        /// <summary>
        /// Creates a secondary styled button
        /// </summary>
        public static ButtonRef CreateSecondaryButton(GameObject parent, string name, string text, int minWidth = 110)
        {
            var btn = UIFactory.CreateButton(parent, name, text);
            UIFactory.SetLayoutElement(btn.Component.gameObject, minWidth: minWidth, minHeight: ButtonHeight);
            SetBackground(btn.Component.gameObject, ButtonSecondary);
            return btn;
        }

        /// <summary>
        /// Creates a navigation button row with proper centering
        /// </summary>
        public static GameObject CreateButtonRow(GameObject parent, string name = "ButtonRow")
        {
            var row = UIFactory.CreateHorizontalGroup(parent, name, false, false, true, true, ElementSpacing * 2);
            UIFactory.SetLayoutElement(row, minHeight: ButtonHeight + 16, flexibleWidth: 9999);

            var layout = row.GetComponent<HorizontalLayoutGroup>();
            if (layout != null)
            {
                layout.childAlignment = TextAnchor.MiddleCenter;
                layout.childForceExpandWidth = false;
                layout.childForceExpandHeight = false;
                layout.padding = new RectOffset(0, 0, 5, 5);
            }

            return row;
        }

        /// <summary>
        /// Creates a horizontal row for form elements
        /// </summary>
        public static GameObject CreateFormRow(GameObject parent, string name, int minHeight = 0)
        {
            var row = UIFactory.CreateHorizontalGroup(parent, name, false, false, true, true, ElementSpacing);
            UIFactory.SetLayoutElement(row, minHeight: minHeight > 0 ? minHeight : InputHeight, flexibleWidth: 9999);
            return row;
        }

        /// <summary>
        /// Creates a styled modifier container (for hotkey modifiers, etc.)
        /// </summary>
        public static GameObject CreateModifierContainer(GameObject parent, string name)
        {
            var container = UIFactory.CreateHorizontalGroup(parent, name, false, false, true, true, 15);
            UIFactory.SetLayoutElement(container, minHeight: 50);
            // Darker background for better contrast with toggles
            SetBackground(container, new Color(0.06f, 0.06f, 0.08f, 0.9f));

            var layout = container.GetComponent<HorizontalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = new RectOffset(20, 20, 10, 10);
                layout.childAlignment = TextAnchor.MiddleCenter;
            }

            return container;
        }

        /// <summary>
        /// Creates a styled toggle with label
        /// </summary>
        public static (GameObject obj, Toggle toggle) CreateStyledToggle(GameObject parent, string name, string labelText)
        {
            var toggleObj = UIFactory.CreateToggle(parent, name, out var toggle, out var label);
            label.text = labelText;
            label.fontSize = FontSizeNormal;
            UIFactory.SetLayoutElement(toggleObj, minHeight: ToggleHeight);
            return (toggleObj, toggle);
        }

        /// <summary>
        /// Configures the ContentRoot of a panel with proper padding for centered content
        /// </summary>
        public static void ConfigurePanelContent(GameObject contentRoot, bool centerContent = false)
        {
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(contentRoot, false, false, true, true,
                ElementSpacing, PanelPadding, PanelPadding, PanelPadding, PanelPadding);

            if (centerContent)
            {
                var layout = contentRoot.GetComponent<VerticalLayoutGroup>();
                if (layout != null)
                {
                    layout.childAlignment = TextAnchor.MiddleCenter;
                }
            }
        }

        /// <summary>
        /// Creates a vertically centered content layout with spacers
        /// </summary>
        public static (GameObject topSpacer, GameObject bottomSpacer) CreateVerticalCenterLayout(
            GameObject parent, out GameObject contentContainer, string containerName = "CenteredContent")
        {
            // Create top spacer
            var topSpacer = CreateFlexSpacer(parent, "TopSpacer");

            // Create content container
            contentContainer = UIFactory.CreateVerticalGroup(parent, containerName, false, false, true, true, ElementSpacing);
            UIFactory.SetLayoutElement(contentContainer, preferredWidth: 420);
            var layout = contentContainer.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.childAlignment = TextAnchor.MiddleCenter;
            }

            // Create bottom spacer
            var bottomSpacer = CreateFlexSpacer(parent, "BottomSpacer");

            return (topSpacer, bottomSpacer);
        }

        /// <summary>
        /// Creates a scrollable panel layout with fixed footer buttons.
        /// The content area scrolls if needed, while buttons stay fixed at the bottom.
        /// This is the recommended way to build panel content.
        /// </summary>
        /// <param name="contentRoot">The panel's ContentRoot</param>
        /// <param name="scrollContent">Output: Container for your scrollable content (cards, sections, etc.)</param>
        /// <param name="buttonRow">Output: Container for footer buttons (Cancel, Save, etc.)</param>
        /// <param name="cardWidth">Width of cards created inside scrollContent</param>
        /// <param name="centerContent">Whether to center content vertically when it fits</param>
        /// <returns>The scroll view GameObject for additional configuration if needed</returns>
        public static GameObject CreateScrollablePanelLayout(
            GameObject contentRoot,
            out GameObject scrollContent,
            out GameObject buttonRow,
            int cardWidth = 420,
            bool centerContent = true)
        {
            // Configure the content root
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(contentRoot, false, false, true, true,
                ElementSpacing, PanelPadding, PanelPadding, PanelPadding, PanelPadding);

            // Create scroll view that takes all available space
            var scrollObj = UIFactory.CreateScrollView(contentRoot, "PanelScroll", out scrollContent, out _);
            UIFactory.SetLayoutElement(scrollObj, flexibleHeight: 9999, flexibleWidth: 9999);

            // Configure scroll rect to auto-hide scrollbar when content fits
            var scrollRect = scrollObj.GetComponent<ScrollRect>();
            if (scrollRect != null)
            {
                scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
                scrollRect.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            }

            // Configure scroll content layout
            var scrollLayout = scrollContent.GetComponent<VerticalLayoutGroup>();
            if (scrollLayout == null)
            {
                scrollLayout = scrollContent.AddComponent<VerticalLayoutGroup>();
            }
            scrollLayout.spacing = ElementSpacing;
            scrollLayout.padding = new RectOffset(0, 0, 0, 0);
            scrollLayout.childAlignment = centerContent ? TextAnchor.MiddleCenter : TextAnchor.UpperCenter;
            scrollLayout.childControlWidth = true;
            scrollLayout.childControlHeight = true;
            scrollLayout.childForceExpandWidth = true;
            scrollLayout.childForceExpandHeight = false;

            // Add content size fitter so scroll content adapts to children
            var sizeFitter = scrollContent.GetComponent<ContentSizeFitter>();
            if (sizeFitter == null)
            {
                sizeFitter = scrollContent.AddComponent<ContentSizeFitter>();
            }
            sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            sizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            // Make scroll view background transparent (content provides its own background)
            SetBackground(scrollObj, Color.clear);

            // Create fixed button row at bottom (outside scroll)
            buttonRow = CreateButtonRow(contentRoot, "FooterButtons");

            return scrollObj;
        }

        /// <summary>
        /// Creates a card inside a scrollable panel layout.
        /// Unlike CreateCard, this version doesn't need minHeight - it adapts to content.
        /// </summary>
        public static GameObject CreateAdaptiveCard(GameObject scrollContent, string name, int width = 420)
        {
            // Create a horizontal wrapper to center the card
            var wrapper = UIFactory.CreateHorizontalGroup(scrollContent, name + "_Wrapper", false, false, true, true, 0);
            UIFactory.SetLayoutElement(wrapper, flexibleWidth: 9999);
            var wrapperLayout = wrapper.GetComponent<HorizontalLayoutGroup>();
            if (wrapperLayout != null)
            {
                wrapperLayout.childAlignment = TextAnchor.MiddleCenter;
                wrapperLayout.childForceExpandWidth = false;
                wrapperLayout.childForceExpandHeight = false;
            }

            // Create the actual card inside the wrapper - NO minHeight, adapts to content
            var card = UIFactory.CreateVerticalGroup(wrapper, name, false, false, true, true, ElementSpacing);
            UIFactory.SetLayoutElement(card, minWidth: width, preferredWidth: width);

            SetBackground(card, CardBackground);

            // Add subtle border effect
            var outline = card.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color(0.08f, 0.08f, 0.1f, 0.8f);
            outline.effectDistance = new Vector2(1, -1);

            var layout = card.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = new RectOffset(CardPadding, CardPadding, CardPadding, CardPadding);
                layout.childAlignment = TextAnchor.MiddleCenter;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
            }

            return card;
        }

        #endregion
    }
}
