using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core.UI.Components;

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
        public static readonly Color ButtonWarning = new Color(0.75f, 0.55f, 0.2f);
        public static readonly Color ButtonDanger = new Color(0.7f, 0.25f, 0.25f);
        public static readonly Color ButtonHover = new Color(0.35f, 0.35f, 0.4f);

        // Status colors
        public static readonly Color StatusSuccess = new Color(0.3f, 0.85f, 0.4f);
        public static readonly Color StatusWarning = new Color(1f, 0.8f, 0.3f);
        public static readonly Color StatusError = new Color(1f, 0.35f, 0.35f);
        public static readonly Color StatusInfo = new Color(0.4f, 0.75f, 1f);

        // Item/List backgrounds (for lists, entries, selectable items)
        public static readonly Color ItemBackground = new Color(0.15f, 0.15f, 0.15f, 0.8f);
        public static readonly Color ItemBackgroundHover = new Color(0.2f, 0.2f, 0.22f, 0.85f);
        public static readonly Color ItemBackgroundSelected = new Color(0.2f, 0.35f, 0.5f, 0.9f);

        // Notification box colors (for status overlays, alerts)
        public static readonly Color NotificationSuccess = new Color(0.15f, 0.35f, 0.15f, 0.95f);
        public static readonly Color NotificationWarning = new Color(0.35f, 0.25f, 0.1f, 0.95f);
        public static readonly Color NotificationInfo = new Color(0.1f, 0.1f, 0.1f, 0.9f);

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

        // Row heights (standardized heights for list items, form rows, etc.)
        public static readonly int RowHeightSmall = 18;     // hints, small labels
        public static readonly int RowHeightNormal = 22;    // standard labels, info rows
        public static readonly int RowHeightMedium = 25;    // toggles, buttons in rows
        public static readonly int RowHeightLarge = 30;     // input rows, account rows
        public static readonly int RowHeightXLarge = 35;    // special emphasis rows

        // Multi-line content heights
        public static readonly int MultiLineSmall = 45;     // 2-3 lines of text
        public static readonly int MultiLineMedium = 80;    // descriptions, paragraphs
        public static readonly int MultiLineLarge = 120;    // large text blocks

        // Control widths
        public static readonly int ToggleControlWidth = 25;
        public static readonly int ModifierKeyWidth = 55;
        public static readonly int SmallButtonWidth = 80;

        // Code/special display
        public static readonly int CodeDisplayFontSize = 28;
        public static readonly int CodeDisplayHeight = 50;

        // Notification boxes (StatusOverlay)
        public static readonly int NotificationBoxHeight = 55;

        // Screen margins for dynamic sizing
        public static readonly int ScreenMarginTop = 40;
        public static readonly int ScreenMarginBottom = 40;
        public static readonly int ScreenMarginHorizontal = 30;
        public static readonly int MinimumPanelHeight = 150;

        #endregion

        #region Dynamic Sizing Helpers

        /// <summary>
        /// Calculates the maximum panel height based on screen dimensions.
        /// Respects top and bottom margins.
        /// </summary>
        public static int CalculateMaxPanelHeight(float screenHeight)
        {
            return Mathf.Max(MinimumPanelHeight, Mathf.FloorToInt(screenHeight - ScreenMarginTop - ScreenMarginBottom));
        }

        /// <summary>
        /// Calculates the maximum panel width based on screen dimensions.
        /// Respects horizontal margins.
        /// </summary>
        public static int CalculateMaxPanelWidth(float screenWidth)
        {
            return Mathf.Max(200, Mathf.FloorToInt(screenWidth - ScreenMarginHorizontal * 2));
        }

        /// <summary>
        /// Calculates optimal panel height: min(contentHeight, maxScreenHeight).
        /// Never larger than content (no empty space).
        /// </summary>
        public static int CalculateOptimalPanelHeight(float contentHeight, float screenHeight, int minHeight)
        {
            int maxHeight = CalculateMaxPanelHeight(screenHeight);
            // Never larger than content (no void), never smaller than min, never larger than screen allows
            return Mathf.Clamp(Mathf.CeilToInt(contentHeight), minHeight, maxHeight);
        }

        /// <summary>
        /// Gets the safe area for panel placement (accounting for margins).
        /// </summary>
        public static Rect GetScreenSafeArea(Vector2 screenDimensions)
        {
            return new Rect(
                ScreenMarginHorizontal,
                ScreenMarginBottom,
                screenDimensions.x - ScreenMarginHorizontal * 2,
                screenDimensions.y - ScreenMarginTop - ScreenMarginBottom
            );
        }

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
        /// Configures a UniverseLib scroll view to auto-hide scrollbar and expand viewport.
        /// Adds a DynamicScrollbarHider component that monitors content size and toggles scrollbar visibility.
        /// </summary>
        public static void ConfigureScrollViewNoScrollbar(GameObject scrollObj)
        {
            if (scrollObj == null) return;

            // Add our dynamic scrollbar hider component
            var hider = scrollObj.GetComponent<DynamicScrollbarHider>();
            if (hider == null)
            {
                hider = scrollObj.AddComponent<DynamicScrollbarHider>();
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
                layout.childAlignment = TextAnchor.UpperLeft;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
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

            // Hide UniverseLib's fixed 28px scrollbar zone and extend viewport
            ConfigureScrollViewNoScrollbar(scrollObj);

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

        #region High-Level Helpers

        /// <summary>
        /// Creates a styled info label (secondary color, normal font size).
        /// Use for descriptions and informational text.
        /// </summary>
        public static Text CreateInfoLabel(GameObject parent, string name, string text)
        {
            var label = UIFactory.CreateLabel(parent, name, text, TextAnchor.MiddleLeft);
            label.color = TextSecondary;
            label.fontSize = FontSizeNormal;
            UIFactory.SetLayoutElement(label.gameObject, minHeight: RowHeightNormal);
            return label;
        }

        /// <summary>
        /// Creates a small styled label (muted color, small font).
        /// Use for hints, captions, and secondary information.
        /// </summary>
        public static Text CreateSmallLabel(GameObject parent, string name, string text)
        {
            var label = UIFactory.CreateLabel(parent, name, text, TextAnchor.MiddleLeft);
            label.color = TextMuted;
            label.fontSize = FontSizeSmall;
            UIFactory.SetLayoutElement(label.gameObject, minHeight: RowHeightSmall);
            return label;
        }

        /// <summary>
        /// Creates a centered status label for displaying status messages.
        /// </summary>
        public static Text CreateStatusLabel(GameObject parent, string name)
        {
            var label = UIFactory.CreateLabel(parent, name, "", TextAnchor.MiddleCenter);
            label.fontSize = FontSizeNormal;
            UIFactory.SetLayoutElement(label.gameObject, minHeight: RowHeightMedium);
            return label;
        }

        /// <summary>
        /// Creates a styled input field with proper background and sizing.
        /// </summary>
        public static InputFieldRef CreateStyledInputField(
            GameObject parent, string name, string placeholder, int minHeight = 0)
        {
            var input = UIFactory.CreateInputField(parent, name, placeholder);
            UIFactory.SetLayoutElement(input.Component.gameObject,
                flexibleWidth: 9999,
                minHeight: minHeight > 0 ? minHeight : InputHeight);
            SetBackground(input.Component.gameObject, InputBackground);
            return input;
        }

        /// <summary>
        /// Creates a horizontal row for form elements (toggles, labels, inputs).
        /// Items are vertically centered within the row with proper padding.
        /// </summary>
        public static GameObject CreateFormRow(GameObject parent, string name, int minHeight = 0, int spacing = 10)
        {
            var row = UIFactory.CreateHorizontalGroup(parent, name, false, false, true, true, spacing);
            UIFactory.SetLayoutElement(row, minHeight: minHeight > 0 ? minHeight : RowHeightMedium, flexibleWidth: 9999);

            var layout = row.GetComponent<HorizontalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = new RectOffset(10, 10, 5, 5); // Left, Right, Top, Bottom padding
                layout.childAlignment = TextAnchor.MiddleLeft;
                layout.childForceExpandWidth = false;
                layout.childForceExpandHeight = false;
            }

            return row;
        }

        /// <summary>
        /// Creates a list item row with consistent styling and hover support.
        /// Use for entries in scrollable lists.
        /// </summary>
        public static GameObject CreateListItem(GameObject parent, string name, int minHeight = 0, bool selected = false)
        {
            var item = UIFactory.CreateHorizontalGroup(parent, name, false, false, true, true, SmallSpacing);
            UIFactory.SetLayoutElement(item,
                minHeight: minHeight > 0 ? minHeight : RowHeightMedium,
                flexibleWidth: 9999);
            SetBackground(item, selected ? ItemBackgroundSelected : ItemBackground);

            var layout = item.GetComponent<HorizontalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = new RectOffset(10, 10, 5, 5);
                layout.childAlignment = TextAnchor.MiddleLeft;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = false; // Don't expand children - they have their own widths
                layout.childForceExpandHeight = false;
            }

            return item;
        }

        /// <summary>
        /// Creates an inline language selector with search and scrollable list.
        /// </summary>
        /// <param name="parent">Parent container</param>
        /// <param name="name">Base name for UI elements</param>
        /// <param name="languages">Array of language names</param>
        /// <param name="listHeight">Height of the scrollable list</param>
        /// <returns>Tuple with (container, searchInput, listContent, selectedLabel)</returns>
        public static (GameObject container, InputFieldRef searchInput, GameObject listContent, Text selectedLabel)
            CreateLanguageSelector(GameObject parent, string name, int listHeight = 120)
        {
            var container = UIFactory.CreateVerticalGroup(parent, name + "Container", false, false, true, true, SmallSpacing);
            UIFactory.SetLayoutElement(container, flexibleWidth: 9999);

            // Selected language display
            var selectedRow = UIFactory.CreateHorizontalGroup(container, name + "SelectedRow", false, false, true, true, SmallSpacing);
            UIFactory.SetLayoutElement(selectedRow, minHeight: RowHeightMedium);

            var selectedLabelPrefix = UIFactory.CreateLabel(selectedRow, name + "Prefix", "Selected: ", TextAnchor.MiddleLeft);
            selectedLabelPrefix.color = TextSecondary;
            selectedLabelPrefix.fontSize = FontSizeSmall;
            UIFactory.SetLayoutElement(selectedLabelPrefix.gameObject, minWidth: 60);

            var selectedLabel = UIFactory.CreateLabel(selectedRow, name + "Selected", "", TextAnchor.MiddleLeft);
            selectedLabel.color = TextAccent;
            selectedLabel.fontStyle = FontStyle.Bold;
            selectedLabel.fontSize = FontSizeNormal;
            UIFactory.SetLayoutElement(selectedLabel.gameObject, flexibleWidth: 9999);

            // Search input
            var searchInput = CreateStyledInputField(container, name + "Search", "Search languages...", RowHeightLarge);

            // Scrollable list
            var scrollObj = UIFactory.CreateScrollView(container, name + "Scroll", out var listContent, out _);
            UIFactory.SetLayoutElement(scrollObj, minHeight: listHeight, flexibleWidth: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(listContent, false, false, true, true, 2, 5, 5, 5, 5);
            SetBackground(scrollObj, InputBackground);
            ConfigureScrollViewNoScrollbar(scrollObj);

            return (container, searchInput, listContent, selectedLabel);
        }

        /// <summary>
        /// Populates a language list with clickable items.
        /// Call this to refresh the list when search changes or selection changes.
        /// </summary>
        /// <param name="listContent">The list content from CreateLanguageSelector</param>
        /// <param name="languages">All available languages</param>
        /// <param name="searchFilter">Current search text (empty = show all)</param>
        /// <param name="selectedLanguage">Currently selected language</param>
        /// <param name="onSelect">Callback when a language is clicked</param>
        public static void PopulateLanguageList(
            GameObject listContent,
            string[] languages,
            string searchFilter,
            string selectedLanguage,
            System.Action<string> onSelect)
        {
            if (listContent == null) return;

            // Clear existing items
            foreach (Transform child in listContent.transform)
            {
                Object.Destroy(child.gameObject);
            }

            string filter = searchFilter?.ToLower() ?? "";

            foreach (var lang in languages)
            {
                if (!string.IsNullOrEmpty(filter) && !lang.ToLower().Contains(filter))
                    continue;

                bool isSelected = lang == selectedLanguage;
                var item = CreateListItem(listContent, $"Lang_{lang}", RowHeightMedium, isSelected);

                var label = UIFactory.CreateLabel(item, "Label", lang, TextAnchor.MiddleLeft);
                label.color = isSelected ? TextPrimary : TextSecondary;
                label.fontSize = FontSizeNormal;
                UIFactory.SetLayoutElement(label.gameObject, flexibleWidth: 9999);

                // Make clickable
                var btn = item.AddComponent<Button>();
                var langCapture = lang; // Capture for closure
                btn.onClick.AddListener(() => onSelect?.Invoke(langCapture));

                // Add hover effect
                var hoverHandler = item.AddComponent<LanguageItemHoverHandler>();
                hoverHandler.Initialize(item, isSelected);
            }
        }

        #endregion
    }

    /// <summary>
    /// Simple hover handler for language list items.
    /// </summary>
    public class LanguageItemHoverHandler : MonoBehaviour,
        UnityEngine.EventSystems.IPointerEnterHandler,
        UnityEngine.EventSystems.IPointerExitHandler
    {
        private GameObject _item;
        private bool _isSelected;
        private Image _image;

        public void Initialize(GameObject item, bool isSelected)
        {
            _item = item;
            _isSelected = isSelected;
            _image = item.GetComponent<Image>();
        }

        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (_image != null && !_isSelected)
            {
                _image.color = UIStyles.ItemBackgroundHover;
            }
        }

        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (_image != null && !_isSelected)
            {
                _image.color = UIStyles.ItemBackground;
            }
        }
    }
}
