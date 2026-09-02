using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Common;
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

        // Explicit transparent - use this instead of Color.clear when you want transparent
        // WITHOUT inheriting automatic padding from UniverseLib defaults
        public static readonly Color Transparent = new Color(0f, 0f, 0f, 0.001f);

        /// <summary>
        /// A shared colour, as Unity wants it. The library carries no alpha (see Common.Theme), so
        /// opacity is named here, where the reason for it lives.
        /// </summary>
        private static Color Of(Rgb c, float alpha = 1f)
        {
            return new Color(c.Rf, c.Gf, c.Bf, alpha);
        }

        /// <summary>
        /// <paramref name="over"/> laid on <paramref name="under"/>, at the given strength.
        ///
        /// The arithmetic is <see cref="Rgb.Over"/>, in the shared library, so that the Manager
        /// reaches the same shade rather than one that is nearly it — a tinted state is as much a
        /// shared decision as the colour it is made from.
        /// </summary>
        private static Color Blend(Rgb over, Rgb under, float strength)
        {
            return Of(over.Over(under, strength));
        }

        // ── The product's palette, from the shared library ────────────────────────────────────
        //
        // ⚠ NOT written here. `Common.Theme` holds the colours for the mod AND the Manager, because
        // the two are C# and a palette is exactly the kind of rule they must not contradict — the
        // same reason Languages and Quality live there. Its values were read out of the running
        // website's CSS custom properties, and its check project refuses a drift.
        //
        // What stays local is what is local: the alpha values. This interface floats over a running
        // game, so a panel is nearly opaque where a web page needs nothing of the sort — that is a
        // decision about THIS product, and the shared library carries no alpha at all.
        //
        // Naming stays as it was: these names are used in some five hundred places across the
        // panels, and renaming them to match the library would be a rewrite that changes no pixel.
        public static readonly Color PanelBackground = Of(Theme.SurfaceBase, 0.98f);
        public static readonly Color CardBackground = Of(Theme.SurfaceCard, 0.96f);
        public static readonly Color SectionBackground = Transparent;                               // Transparent (no auto-padding)
        public static readonly Color InputBackground = Of(Theme.SurfaceRaised);

        /// <summary>
        /// The trough a list sits in: a scroll viewport, a tab strip. `SurfaceDeep`.
        ///
        /// 🔴 **Not <see cref="InputBackground"/>, which is what every list used.** That is the
        /// same value as <see cref="ItemBackground"/>, so a row was painted the exact colour of the
        /// thing it sits on: one card in a list was invisible, and several read as one block. The
        /// ramp exists precisely to prevent this — `Theme.SurfaceDeep` is documented as "a
        /// viewport, a tab strip, the trough of a list" — and the wrong rung was picked.
        ///
        /// ⚠ A field is not a trough. An input sits ON a card and stays `SurfaceRaised`; a list
        /// RECESSES into it. Sharing one key for both is what made the mistake invisible.
        /// </summary>
        public static readonly Color TroughBackground = Of(Theme.SurfaceDeep);

        // Edges. The site draws a 1px line around every card (`border-gray-700`, 242 times) and
        // every field (`border-gray-600`); the mod drew none at all, which is half of why the same
        // card read as a different object here.
        public static readonly Color BorderSubtle = Of(Theme.BorderSubtle);
        public static readonly Color BorderStrong = Of(Theme.BorderStrong);

        // Text colors
        public static readonly Color TextPrimary = Of(Theme.TextPrimary);
        public static readonly Color TextSecondary = Of(Theme.TextSecondary);
        public static readonly Color TextMuted = Of(Theme.TextMuted);
        public static readonly Color TextAccent = Of(Theme.AccentSoft);

        /// <summary>
        /// The chosen one of the three scope marks.
        ///
        /// ⚠ NOT <see cref="TextAccent"/>, and the socle explains at length why: the marks sit
        /// inside buttons, including accent-filled ones, where a light purple scored less against
        /// the fill than the two DIMMED marks did.
        /// </summary>
        public static readonly Color MarkLit = Of(Theme.MarkLit);

        // Buttons. FOUR purples, as the site has: 600 fills, 500 edges and highlights, 400 writes,
        // 700 presses. One purple for all four was most of what made the mod read as another
        // product — the accent was flat where the site's has depth.
        public static readonly Color ButtonPrimary = Of(Theme.Accent);
        public static readonly Color ButtonSecondary = Of(Theme.SurfaceRaised);
        public static readonly Color ButtonSuccess = Of(Theme.Accent);                             // the main CTA is an accent, not a green
        public static readonly Color ButtonWarning = Of(Theme.StatusWarning);
        public static readonly Color ButtonDanger = Of(Theme.StatusError);
        /// <summary>
        /// A button that leads somewhere else — the website. purple-900.
        ///
        /// 🔴 **It was AccentSoft, and AccentSoft is a colour for WRITING, not for filling.** The
        /// library says so on the field itself ("carries text and links on a dark surface"), and
        /// this file uses it correctly nineteen lines above, as <see cref="TextAccent"/>. Filled
        /// with it, the button became a pale purple carrying pale content:
        ///
        /// | on the old fill (purple-400) | contrast |
        /// |---|---|
        /// | the label | 2.54 |
        /// | a dimmed scope mark | **1.07** — 1.00 is invisible |
        /// | the lit scope mark | 2.24 |
        ///
        /// On purple-900 those become 9.98, 4.22 and 8.80. Measured, not judged — the same method
        /// the library used when it took AccentSoft away from the lit mark for the same reason.
        ///
        /// ⚠ Dark rather than simply different: every other control in these rows is dark, so the
        /// marks and the label are legible here for the same reason they are legible there. The
        /// purple is what keeps "this leaves the game" distinct from a plain secondary button.
        /// </summary>
        public static readonly Color ButtonLink = Of(Theme.AccentDim);
        public static readonly Color ButtonHover = Of(Theme.AccentEdge);
        public static readonly Color ButtonDisabled = new Color(0.20f, 0.22f, 0.27f, 1f);          // Dim slate for disabled controls (visible, never black)

        /// <summary>
        /// What a dead button's label and marks are painted with.
        ///
        /// 🔴 **The state is carried by the TEXT, because the fill cannot carry it.** Measured: the
        /// disabled fill scores 1.14 against the live one — the two are the same colour to the eye
        /// — and darkening it does not help, because it then scores 1.07 against the card behind
        /// and the button dissolves into it. On a dark theme there is no room between "same as the
        /// live button" and "invisible".
        ///
        /// ⚠ 3.24 against that fill: weaker than TextMuted's 4.50, which still read as ordinary
        /// prose, and well clear of the 2.4 where it stops being readable. A disabled control has
        /// to stay legible — somebody is reading it to find out why they cannot press it.
        ///
        /// ⚠ Mod-local on purpose. TextMuted is the shared palette the website renders too, and it
        /// means "a quiet fact", not "a dead control".
        /// </summary>
        public static readonly Color ButtonLabelDead = new Color(0.494f, 0.529f, 0.596f, 1f);      // #7E8798

        // Status colors — the shared 400 ramp, which is what the site uses on dark surfaces.
        public static readonly Color StatusSuccess = Of(Theme.StatusSuccess);
        public static readonly Color StatusWarning = Of(Theme.StatusWarning);
        public static readonly Color StatusError = Of(Theme.StatusError);
        public static readonly Color StatusInfo = Of(Theme.StatusInfo);
        public static readonly Color StatusNeutral = Of(Theme.StatusNeutral);
        // Kept as is on purpose (tag S): dealt with, not pending — hence its own colour rather
        // than the grey.
        public static readonly Color StatusKept = Of(Theme.QualityKept);
        // The mod's own interface (tag M): a provenance, not a degree of translation, so it takes
        // the one colour nothing else uses. It has no band in the quality bar on any side —
        // see QualityBar.CountTags.
        public static readonly Color StatusModUi = Of(Theme.TagModUi);

        // ── Waiting for Apply ─────────────────────────────────────────────────────────────────
        //
        // The diff convention, on the status ramp: added = green, changed = amber, removed = red.
        // Drawn by Components.PendingMarks and nowhere else — a screen that needs to say "this
        // is waiting for Apply" registers the element there rather than colouring it itself, so
        // the same fact reads the same way on every screen with an Apply button.
        public const float PendingBarWidth = 3f;

        public static Color PendingBar(Components.PendingState state)
        {
            switch (state)
            {
                case Components.PendingState.Added: return StatusSuccess;
                case Components.PendingState.Modified: return StatusWarning;
                case Components.PendingState.Removed: return StatusError;
                default: return Transparent;
            }
        }

        /// <summary>The faint wash behind a whole row that is added or removed — the bar alone reads as one field.</summary>
        public static Color PendingTint(Components.PendingState state)
        {
            var c = PendingBar(state);
            return new Color(c.r, c.g, c.b, 0.12f);
        }

        // ── What a translation is MADE OF ─────────────────────────────────────────────────────
        //
        // ⚠ Its own five keys, and not the status colours above, which is the whole point.
        //
        // The bar reused StatusSuccess/StatusInfo/StatusWarning, so the AI share came out AMBER
        // here and orange on the website and in the Manager — three implementations that quote
        // each other in their comments, disagreeing on three bands out of five. The cause was
        // structural: "it went well" and "this line came from an AI" are two different registers,
        // and one colour cannot serve both without drifting the next time either is adjusted.
        //
        // Same order as the site and the Manager: settled first, still-to-do last.
        public static readonly Color QualityHuman = Of(Theme.QualityHuman);
        public static readonly Color QualityValidated = Of(Theme.QualityValidated);
        public static readonly Color QualityAi = Of(Theme.QualityAi);
        public static readonly Color QualityKept = Of(Theme.QualityKept);
        public static readonly Color QualityCapture = Of(Theme.QualityCapture);

        /// <summary>
        /// The letter on its coloured square, the way the website draws it.
        ///
        /// 🔴 **The same five letters are named in three products and only one of them drew them.**
        /// Here they were `[H] some key` — the tag as plain grey text between brackets, indistinct
        /// from the key beside it, and carrying none of the colour a reader has already learnt on
        /// the site's tables. The colours come from <see cref="Theme"/>, so changing how a tag
        /// looks is one edit in the shared library rather than a hunt through three code bases.
        ///
        /// ⚠ The chip ramp (600) and not the band ramp (500): six pixels of white type need the
        /// darker one behind them. The library holds both and says why.
        /// </summary>
        /// <summary>
        /// The colour a dimmed scope mark takes on a given fill — the socle's rule, in Unity's type.
        ///
        /// ⚠ The decision is <see cref="Theme.MarkDim"/> and stays there: the Manager draws the same
        /// three marks on its own buttons, and "which grey a dimmed mark takes" is exactly the kind
        /// of answer the two products must not reach separately.
        /// </summary>
        public static Color MarkDimOn(Color fill)
        {
            return Of(Theme.MarkDim(ToRgb(fill)));
        }

        /// <summary>Unity's colour back into the library's, for a rule that needs to weigh it.</summary>
        private static Rgb ToRgb(Color c)
        {
            return new Rgb((byte)Mathf.Round(Mathf.Clamp01(c.r) * 255f),
                           (byte)Mathf.Round(Mathf.Clamp01(c.g) * 255f),
                           (byte)Mathf.Round(Mathf.Clamp01(c.b) * 255f));
        }

        public static Color TagChip(string tag)
        {
            return Of(Theme.ChipBackground(string.IsNullOrEmpty(tag) ? null : tag.ToUpperInvariant()));
        }

        /// <summary>
        /// Build one: the letter on its square, sized to sit on a row of small text.
        /// </summary>
        /// <param name="letter">The label, handed back so the caller can retag the row later.</param>
        public static GameObject CreateTagChip(GameObject parent, string tag, out Text letter)
        {
            GameObject chip = UIFactory.CreateUIObject("TagChip", parent);

            // 🔴 **The square has to EXIST before anything can paint it.** SetBackground writes into
            // an Image and never adds one — every other coloured surface in this product is built by
            // a factory that fits one (see QualityBar's segments, the same two lines). This one was
            // a bare object, so the colour went nowhere and five letters shipped as grey type on
            // nothing: the one thing the chips were introduced to stop.
            //
            // ⚠ Never a raycast target: a mark that swallows a click puts a dead spot exactly where
            // the eye is drawn. Same rule as the scope marks.
            Image square = chip.AddComponent<Image>();
            square.raycastTarget = false;

            UIFactory.SetLayoutElement(chip, minWidth: TagChipWidth, minHeight: TagChipHeight,
                                       preferredWidth: TagChipWidth, preferredHeight: TagChipHeight,
                                       flexibleWidth: 0, flexibleHeight: 0);

            letter = UIFactory.CreateLabel(chip, "Letter", TagLetterOf(tag), TextAnchor.MiddleCenter,
                                           supportRichText: false);
            // The website's chip is 12px bold; the hint size is this product's nearest, and a chip
            // that used the row's own size would be a word rather than a mark.
            letter.fontSize = FontSizeHint;
            letter.fontStyle = FontStyle.Bold;
            letter.color = Of(Theme.ChipLetter);

            RectTransform rect = letter.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            SetTagChip(chip, letter, tag);
            return chip;
        }

        /// <summary>Retag one that already exists — colour and letter together, never one alone.</summary>
        public static void SetTagChip(GameObject chip, Text letter, string tag)
        {
            if (chip != null) SetBackground(chip, TagChip(tag), UIFactory.Shapes.Small);
            if (letter != null) letter.text = TagLetterOf(tag);
        }

        /// <summary>
        /// ⚠ An em dash for a line that has no tag yet, and the capture grey behind it: a blank
        /// square would read as a colour nobody can name.
        /// </summary>
        private static string TagLetterOf(string tag)
        {
            return string.IsNullOrEmpty(tag) ? "—" : tag.ToUpperInvariant();
        }

        /// <summary>Wide enough for one bold letter with the website's padding on each side.</summary>
        public const int TagChipWidth = 20;
        public const int TagChipHeight = 18;

        // Item/List backgrounds
        public static readonly Color ItemBackground = Of(Theme.SurfaceRaised);
        public static readonly Color ItemBackgroundHover = Of(Theme.SurfaceHover);
        // A selected row is the deep purple laid over the card rather than the accent itself: text
        // has to stay readable on it, which purple-600 does not allow. Computed in the library, so
        // the Manager's selected row is the same shade and not merely a similar one.
        public static readonly Color ItemBackgroundSelected = Of(Theme.RowSelected);
        public static readonly Color ItemBackgroundLineage = Of(Theme.RowRelated);

        // Callouts: the hue laid over the surface, never a flat saturated block. Straight from the
        // library, so the same notice is the same colour in the window and in the game.
        public static readonly Color NotificationSuccess = Of(Theme.CalloutSuccess, 0.95f);
        public static readonly Color NotificationWarning = Of(Theme.CalloutWarning, 0.95f);
        public static readonly Color NotificationInfo = Of(Theme.CalloutInfo, 0.95f);

        // Toasts sit in a corner OVER THE GAME rather than inside a panel, so they are tinted
        // harder than a callout — they have to be read at a glance against scenery nobody chose.
        public static readonly Color ToastSuccessBg = Blend(Theme.StatusSuccess, Theme.SurfaceDeep, 0.30f);
        public static readonly Color ToastErrorBg = Blend(Theme.StatusError, Theme.SurfaceDeep, 0.30f);
        public static readonly Color ToastInfoBg = Blend(Theme.Accent, Theme.SurfaceDeep, 0.30f);

        // Elevated surface (a card that must sit clearly above another card, e.g. guidance box).
        // The site's answer to the same need is gray-700 on gray-800.
        public static readonly Color CardElevated = Of(Theme.SurfaceRaised, 0.96f);

        // In-game element highlight overlays (Inspector) — semi-transparent info-blue
        public static readonly Color GameHighlightHover = new Color(0.24f, 0.55f, 0.85f, 0.28f);
        public static readonly Color GameHighlightSelected = new Color(0.20f, 0.50f, 0.78f, 0.40f);

        // Tab bar. The active tab wears the colour of the content it opens — that is what makes the
        // two read as one object rather than as a button sitting above a box.
        public static readonly Color TabBarBackground = Of(Theme.SurfaceDeep);
        public static readonly Color TabActiveBackground = Of(Theme.SurfaceCard);                  // = the card it opens
        public static readonly Color TabInactiveBackground = Blend(Theme.SurfaceCard, Theme.SurfaceDeep, 0.45f);
        public static readonly Color TabHoverBackground = Blend(Theme.SurfaceCard, Theme.SurfaceDeep, 0.75f);
        public static readonly Color TabContentBackground = Of(Theme.SurfaceDeep);

        // Scroll view viewport background (replaces UniverseLib's gray default)
        public static readonly Color ViewportBackground = Of(Theme.SurfaceDeep);

        // Dropdown colors (for SearchableDropdown component)
        public static readonly Color DropdownBackground = Of(Theme.SurfaceRaised);                 // = InputBackground
        public static readonly Color DropdownItemNormal = Of(Theme.SurfaceCard);
        public static readonly Color DropdownItemHighlight = Of(Theme.Accent, 0.55f);
        public static readonly Color InputFieldBackground = Of(Theme.SurfaceRaised);               // = InputBackground

        // Toggle/checkbox (wired into UniverseLib.Colors at init so the plugin controls them)
        public static readonly Color CheckboxUnchecked = new Color(0.42f, 0.47f, 0.58f, 1f);        // Light slate — a small box needs more contrast than a large button to read on dark bg
        public static readonly Color CheckboxCheckmark = ButtonPrimary;                              // Purple check when on
        public static readonly Color CheckboxBorder = new Color(0.62f, 0.67f, 0.78f, 0.9f);          // Light edge → box reads on any bg

        // UniverseLib theme extras (no dedicated UGT use yet — kept here so the whole UniverseLib
        // palette is driven from ONE place; see TranslatorUIManager.Initialize theme sync)
        public static readonly Color SliderBackgroundColor = Of(Theme.SurfaceCard);
        public static readonly Color SliderFillColor = Of(Theme.Accent, 0.85f);
        public static readonly Color SliderHandleColor = Of(Theme.SurfaceHover);
        public static readonly Color InputBorderColor = BorderStrong;                                // = the site's field edge
        public static readonly Color AccentPressed = Of(Theme.AccentDeep);
        public static readonly Color ButtonPressed = Of(Theme.SurfaceDeep);

        /// <summary>
        /// A colour as `RRGGBB`, for a rich-text `&lt;color=#…&gt;` tag.
        ///
        /// 🔴 **Do not reach for `ColorUtility.ToHtmlStringRGB`.** Some games strip it, and
        /// Il2CppInterop cannot always rebuild what was stripped: the call then throws
        /// `NotSupportedException: Method unstripping failed` — inside a panel constructor, which
        /// aborts CreatePanels and leaves the first-run wizard and an oversized main panel on
        /// screen over an already-configured game.
        ///
        /// Same family as TextureUtils versus UniverseLib's TextureHelper, and as the
        /// GetComponentInChildren overload: **naming a Unity API is a bet that this game kept it.**
        /// Arithmetic on three floats is not a bet. It lives here because this is where anyone
        /// building a colour goes looking — in QualityBar it would not have stopped the next person.
        /// </summary>
        public static string Hex(Color color)
        {
            return Channel(color.r) + Channel(color.g) + Channel(color.b);
        }

        private static string Channel(float value)
        {
            if (value < 0f) value = 0f;
            if (value > 1f) value = 1f;
            int b = (int)(value * 255f + 0.5f);
            return b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
        }

        #endregion

        #region Dimensions

        // Padding & Margins
        public static readonly int PanelPadding = 15;
        public static readonly int CardPadding = 20;
        public static readonly int SectionPadding = 12;
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

        // Backend type dropdown labels, shared by WizardPanel and OptionsPanel
        // (plain words instead of "LLM"/"API" jargon; compared by value in both panels)
        public const string BackendTypeLLM = "AI (local or cloud)";
        public const string BackendTypeApi = "Google / DeepL";

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
            return Compat.MakeRect(
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
        /// <summary>
        /// Make a layout row blend into the surface it sits on.
        ///
        /// CreateHorizontalGroup/CreateVerticalGroup paint every row with
        /// <c>Colors.DefaultLayoutBackground</c> and add its padding, which is what we want for a
        /// standalone block — but rows INSIDE a card then read as separate stacked boxes instead of
        /// one continuous surface. Passing Color.clear at creation does not help: the factory
        /// treats (0,0,0,0) as "no colour given" and applies the default anyway.
        /// </summary>
        public static void ClearRowBackground(GameObject row, bool clearPadding = true)
        {
            if (row == null) return;

            var image = row.GetComponent<Image>();
            if (image != null) image.color = Color.clear;

            if (!clearPadding) return;
            var layout = row.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (layout != null) layout.padding = Compat.MakeRectOffset(0, 0, 0, 0);
        }

        /// <summary>
        /// Paint a surface, and round it while we are here.
        ///
        /// The site rounds everything it fills — `rounded-lg` appears 367 times in its templates —
        /// so a coloured surface being round is the rule, not the exception, and stating it once
        /// here beats repeating it at ninety-odd call sites and forgetting it at the ninety-first.
        ///
        /// A transparent surface is left alone: there is nothing to round, and giving a sprite to
        /// an invisible Image only makes uGUI change texture for nothing.
        ///
        /// Pass <paramref name="shape"/> to say otherwise — <c>UIFactory.Shapes.Small</c> for a
        /// dense row, <c>CardTop</c> for something crowning a card, or a shape of your own.
        /// </summary>
        public static void SetBackground(GameObject obj, Color color, Sprite shape)
        {
            SetBackground(obj, color);

            var image = obj != null ? obj.GetComponent<Image>() : null;
            if (image != null) UIFactory.SetShape(image, shape);
        }

        public static void SetBackground(GameObject obj, Color color)
        {
            // Interactive controls (Button/Toggle/Selectable) keep Image.color = white and tint via
            // their ColorBlock.normalColor. Writing Image.color alone renders color × normalColor
            // (UniverseLib's default normalColor dims/crushes it). So on a Selectable, drive the
            // ColorBlock (normal + hover + pressed) and keep the Image white → renders at FULL color.
            var selectable = obj.GetComponent<Selectable>();
            var image = obj.GetComponent<Image>();
            if (selectable != null)
            {
                if (image != null)
                {
                    image.color = Color.white;
                    // A control is painted through its ColorBlock, so the alpha test below would
                    // read white-opaque whatever the real colour is. It is a control: it is round.
                    UIFactory.SetShape(image, UIFactory.Shapes.Control);
                }
                var cb = selectable.colors;
                cb.normalColor = color;
                cb.highlightedColor = new Color(
                    Mathf.Min(color.r * 1.15f, 1f), Mathf.Min(color.g * 1.15f, 1f), Mathf.Min(color.b * 1.15f, 1f), color.a);
                cb.pressedColor = new Color(color.r * 0.8f, color.g * 0.8f, color.b * 0.8f, color.a);
                cb.selectedColor = color;
                // Themed disabled state (dim slate, fully opaque) so a disabled button reads as a clear
                // "greyed out" control instead of Unity's translucent light-gray default, which looked
                // like a black smudge over the dark panel (e.g. Upload Translation when not uploadable).
                cb.disabledColor = ButtonDisabled;
                cb.colorMultiplier = 1f;

                // 🔴 **The ColorBlock above dresses the BACKGROUND and nothing else.** Unity tints
                // a Selectable's targetGraphic and leaves every child alone, so the label kept its
                // full-strength white on a control nobody could press — a disabled button was told
                // apart from a live one by a shade of grey behind it, and got confused with an
                // ordinary button on another surface. The label is followed from here, which is the
                // same act that gives this control its colours in the first place.
                ButtonStates.Watch(selectable);
                selectable.colors = cb;
                return;
            }
            if (image != null)
            {
                image.color = color;

                // Anything actually painted gets the card radius. Below that alpha the surface is
                // there to hold a layout together, not to be seen — see the overload above.
                if (color.a > 0.02f) UIFactory.SetShape(image, UIFactory.Shapes.Card);
            }
        }

        /// <summary>
        /// Configures a UniverseLib scroll view to auto-hide scrollbar and expand viewport.
        /// Uses UniverseLib's built-in DynamicScrollbar component.
        /// Also applies our navy viewport background color.
        /// </summary>
        public static void ConfigureScrollViewNoScrollbar(GameObject scrollObj)
        {
            if (scrollObj == null) return;

            // Use UniverseLib's built-in auto-hide scrollbar
            UIFactory.ConfigureAutoHideScrollbar(scrollObj);

            // Override UniverseLib's gray viewport background with our navy color
            ApplyViewportBackground(scrollObj);
        }

        /// <summary>
        /// Applies our navy viewport background color to a scroll view.
        /// Overrides UniverseLib's default gray color.
        /// </summary>
        public static void ApplyViewportBackground(GameObject scrollObj)
        {
            if (scrollObj == null) return;

            var viewport = scrollObj.transform.Find("Viewport");
            if (viewport != null)
            {
                var image = viewport.GetComponent<Image>();
                if (image != null)
                {
                    image.color = ViewportBackground;
                }
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
            UIFactory.AddBorder(card, BorderSubtle);

            var layout = card.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = Compat.MakeRectOffset(CardPadding, CardPadding, CardPadding, CardPadding);
                layout.childAlignment = TextAnchor.MiddleCenter;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
            }

            return card;
        }

        /// <summary>
        /// Creates a styled section box (smaller than card, for grouping related options).
        /// Clean design without borders - content flows naturally like website.
        /// </summary>
        public static GameObject CreateSection(GameObject parent, string name, int minHeight = 0, bool showTopBorder = false)
        {
            // Container for optional top border + content
            var container = UIFactory.CreateVerticalGroup(parent, name + "_Container", false, false, true, true, 0);
            UIFactory.SetLayoutElement(container, flexibleWidth: 9999);

            // Optional subtle top border (disabled by default for cleaner look)
            if (showTopBorder)
            {
                var topBorder = UIFactory.CreateUIObject(name + "_TopBorder", container);
                UIFactory.SetLayoutElement(topBorder, minHeight: 1, flexibleWidth: 9999);
                var borderImage = topBorder.AddComponent<Image>();
                borderImage.color = new Color(0.20f, 0.22f, 0.26f, 0.3f);  // Very subtle line
            }

            var section = UIFactory.CreateVerticalGroup(container, name, false, false, true, true, SmallSpacing);

            if (minHeight > 0)
                UIFactory.SetLayoutElement(section, minHeight: minHeight, flexibleWidth: 9999);
            else
                UIFactory.SetLayoutElement(section, flexibleWidth: 9999);

            SetBackground(section, SectionBackground);

            var layout = section.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = Compat.MakeRectOffset(SectionPadding, SectionPadding, SectionPadding, SectionPadding);
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
        /// <param name="centred">
        /// Left by default, because a hint is prose and prose starts where the eye looks for its
        /// first word.
        ///
        /// ⚠ Centred where the hint sits UNDER a centred button and belongs to it. Actions reads
        /// as a column of buttons each with its sentence; left-aligning those sentences pulled them
        /// away from the control they describe and left the block looking pinned to one edge. The
        /// rule above still holds for a hint under a form row, which is most of them.
        ///
        /// 🔴 **A bool, not a TextAnchor, and that is not a style choice.** That enum lives in
        /// UnityEngine.TextRenderingModule, which the IL2CPP adapter's assembly rewriter cannot
        /// resolve: naming it in a PUBLIC signature writes a metadata reference that using it
        /// inside a method body never does, and the whole IL2CPP build fails at the rewrite step
        /// with nothing pointing at this file.
        /// </param>
        public static Text CreateHint(GameObject parent, string name, string text,
                                      bool centred = false)
        {
            var label = UIFactory.CreateLabel(parent, name, text,
                                              centred ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft);
            label.fontSize = FontSizeHint;
            label.fontStyle = FontStyle.Italic;
            label.color = TextMuted;
            // 🔴 **Centring a label centres its text INSIDE the label, and a label is only as wide
            // as what it says.** In a vertical group that does not force its children to expand,
            // that leaves a short sentence sitting at the left edge with its own text neatly
            // centred inside it — no visible difference at all, while a long one wraps to the full
            // width and does look centred. Which is exactly how a card ends up with one centred
            // sentence at the bottom and every shorter one still pinned left.
            //
            // ⚠ Null, not 0, when it is not centred: SetLayoutElement's parameters are nullable and
            // null means "leave it alone". Writing 0 would turn a field nobody had set into an
            // override, on every hint in the mod, to fix a case that is not this one.
            UIFactory.SetLayoutElement(label.gameObject, minHeight: 18,
                                       flexibleWidth: centred ? (int?)9999 : null);
            return label;
        }

        /// <summary>
        /// Creates a primary styled button
        /// </summary>
        public static ButtonRef CreatePrimaryButton(GameObject parent, string name, string text, int minWidth = 130)
        {
            var btn = UIFactory.CreateButton(parent, name, text);
            UIFactory.SetLayoutElement(btn.Component.gameObject, minWidth: minWidth, minHeight: ButtonHeight);
            SetBackground(btn.Component.gameObject, ButtonPrimary);   // smart: sets the full ColorBlock (normal/hover/pressed/disabled)
            return btn;
        }

        /// <summary>
        /// Creates a secondary styled button
        /// </summary>
        public static ButtonRef CreateSecondaryButton(GameObject parent, string name, string text, int minWidth = 110)
        {
            var btn = UIFactory.CreateButton(parent, name, text);
            UIFactory.SetLayoutElement(btn.Component.gameObject, minWidth: minWidth, minHeight: ButtonHeight);
            SetBackground(btn.Component.gameObject, ButtonSecondary); // smart: full ColorBlock (normal/hover/pressed/disabled), renders at full value (no ×0.25 crush)
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
                layout.padding = Compat.MakeRectOffset(0, 0, 5, 5);
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
            // Blue-gray background matching website input fields rgb(54,65,83)
            SetBackground(container, new Color(0.212f, 0.255f, 0.325f, 0.9f));

            var layout = container.GetComponent<HorizontalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = Compat.MakeRectOffset(20, 20, 10, 10);
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
            scrollLayout.padding = Compat.MakeRectOffset(0, 0, 0, 0);
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
        /// <param name="scrollContent">Parent container</param>
        /// <param name="name">Card name</param>
        /// <param name="width">Card width</param>
        /// <param name="stretchVertically">If true, card expands to fill available vertical space (for tab content)</param>
        public static GameObject CreateAdaptiveCard(GameObject scrollContent, string name, int width = 420, bool stretchVertically = false)
        {
            // Create a horizontal wrapper to position the card (transparent - no automatic padding)
            var wrapper = UIFactory.CreateHorizontalGroup(scrollContent, name + "_Wrapper", false, false, true, true, 0,
                default, Transparent);

            if (stretchVertically)
            {
                // For tab content: wrapper and card expand to fill space
                UIFactory.SetLayoutElement(wrapper, flexibleWidth: 9999, flexibleHeight: 9999);
            }
            else
            {
                UIFactory.SetLayoutElement(wrapper, flexibleWidth: 9999);
            }

            var wrapperLayout = wrapper.GetComponent<HorizontalLayoutGroup>();
            if (wrapperLayout != null)
            {
                wrapperLayout.childAlignment = stretchVertically ? TextAnchor.UpperCenter : TextAnchor.MiddleCenter;
                wrapperLayout.childForceExpandWidth = false;
                wrapperLayout.childForceExpandHeight = stretchVertically; // Expand card when stretching
            }

            // Create the actual card inside the wrapper
            var card = UIFactory.CreateVerticalGroup(wrapper, name, false, false, true, true, ElementSpacing);

            if (stretchVertically)
            {
                UIFactory.SetLayoutElement(card, minWidth: width, preferredWidth: width, flexibleHeight: 9999);
            }
            else
            {
                UIFactory.SetLayoutElement(card, minWidth: width, preferredWidth: width);
            }

            SetBackground(card, CardBackground);
            // The site's card is `bg-gray-800 rounded-lg p-6 border border-gray-700`, and that
            // border is 242 of its elements. Without it a card floats instead of sitting.
            UIFactory.AddBorder(card, BorderSubtle);

            var layout = card.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = Compat.MakeRectOffset(CardPadding, CardPadding, CardPadding, CardPadding);
                layout.childAlignment = stretchVertically ? TextAnchor.UpperCenter : TextAnchor.MiddleCenter;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false; // Content inside card doesn't stretch
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
                layout.padding = Compat.MakeRectOffset(10, 10, 5, 5); // Left, Right, Top, Bottom padding
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
            // The small radius, not the card's: these rows are stacked and dense, and a row is not
            // a card. Same distinction the site makes between a card and a list line.
            SetBackground(item, selected ? ItemBackgroundSelected : ItemBackground, UIFactory.Shapes.Small);

            // A chosen row is tinted AND edged, as it is in the Manager and on the site. The tint
            // alone has to be strong enough to be unmistakable, which means burying the text under
            // it; an accent edge says the same thing at the same glance and costs no contrast.
            if (selected) UIFactory.AddBorder(item, ButtonHover, UIFactory.Shapes.BorderSmall);

            var layout = item.GetComponent<HorizontalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = Compat.MakeRectOffset(10, 10, 5, 5);
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
        public static (GameObject container, InputFieldRef searchInput, GameObject listContent, Text selectedLabel, GameObject selectedMark)
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

            // Holds the flag of whatever is selected. Rebuilt by LanguageSelector when the choice
            // changes — a mark left over from the previous one would name a language nobody picked.
            var selectedMark = UIFactory.CreateUIObject(name + "SelectedMark", selectedRow);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(selectedMark, false, false, true, true,
                                                            4, 0, 0, 0, 0, TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(selectedMark, minHeight: RowHeightSmall,
                                       flexibleWidth: 0, flexibleHeight: 0);

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
            // ⚠ The seventh trough, missed when the other six were fixed because it is built
            // in here rather than at a call site: InputBackground is the same value as
            // ItemBackground, so every row was the colour of the list it sits in.
            SetBackground(scrollObj, TroughBackground);
            ConfigureScrollViewNoScrollbar(scrollObj);

            return (container, searchInput, listContent, selectedLabel, selectedMark);
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

            // Clear existing items (iterate backwards for safe destruction)
            for (int i = listContent.transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(listContent.transform.GetChild(i).gameObject);
            }

            string filter = searchFilter?.ToLower() ?? "";

            foreach (var lang in languages)
            {
                if (!string.IsNullOrEmpty(filter) && !lang.ToLower().Contains(filter))
                    continue;

                bool isSelected = lang == selectedLanguage;
                var item = CreateListItem(listContent, $"Lang_{lang}", RowHeightMedium, isSelected);

                // Flag then name, in one control. ⚠ The row is a horizontal group already, so the
                // mark drops in where the label used to be; the socle suppresses the tag chip
                // because the name is right there, which is what it exists to replace.
                var mark = Components.LanguageMark.Create(
                    item, "Mark", lang, withName: true,
                    nameColour: isSelected ? TextPrimary : TextSecondary);

                if (mark != null)
                {
                    UIFactory.SetLayoutElement(mark, flexibleWidth: 9999);
                }
                else
                {
                    // A language the catalogue does not mark still has to be pickable.
                    var label = UIFactory.CreateLabel(item, "Label", lang, TextAnchor.MiddleLeft);
                    label.color = isSelected ? TextPrimary : TextSecondary;
                    label.fontSize = FontSizeNormal;
                    UIFactory.SetLayoutElement(label.gameObject, flexibleWidth: 9999);
                }

                // Make clickable (use helper for IL2CPP compatibility)
                var btn = item.AddComponent<Button>();
                var langCapture = lang; // Capture for closure
                UIHelpers.AddButtonListener(btn, () => onSelect?.Invoke(langCapture));

                // Add hover effect (works on both Mono and IL2CPP via UniverseLib)
                if (!isSelected)
                {
                    AddHoverEffect(item, ItemBackground, ItemBackgroundHover);
                }
            }
        }

        /// <summary>
        /// Adds hover effect to an item. Works on both Mono and IL2CPP.
        /// Uses UniverseLib's built-in HoverEffect component with IPointerEnterHandler/IPointerExitHandler.
        /// </summary>
        public static void AddHoverEffect(GameObject item, Color normalColor, Color hoverColor)
        {
            // Use UniverseLib's IL2CPP-compatible hover effect
            UIFactory.AddHoverEffect(item, normalColor, hoverColor);
        }

        /// <summary>
        /// Creates a collapsible section with clickable header and content container.
        /// Use SetCollapsibleState to toggle visibility.
        /// </summary>
        /// <param name="parent">Parent container</param>
        /// <param name="name">Base name for UI elements</param>
        /// <param name="title">Section title text</param>
        /// <param name="initiallyExpanded">Whether the section starts expanded</param>
        /// <returns>Tuple with (container, header, iconLabel, titleLabel, content)</returns>
        public static (GameObject container, GameObject header, Text iconLabel, Text titleLabel, GameObject content)
            CreateCollapsibleSection(GameObject parent, string name, string title, bool initiallyExpanded = true)
        {
            // Main container
            var container = UIFactory.CreateVerticalGroup(parent, name + "Section", false, false, true, true, 0);
            UIFactory.SetLayoutElement(container, flexibleWidth: 9999);

            // Clickable header row
            var header = UIFactory.CreateHorizontalGroup(container, name + "Header", false, false, true, true, SmallSpacing);
            UIFactory.SetLayoutElement(header, minHeight: RowHeightMedium, flexibleWidth: 9999);
            SetBackground(header, SectionBackground);

            var headerLayout = header.GetComponent<HorizontalLayoutGroup>();
            if (headerLayout != null)
            {
                headerLayout.padding = Compat.MakeRectOffset(10, 10, 5, 5);
                headerLayout.childAlignment = TextAnchor.MiddleLeft;
            }

            // Collapse/expand icon
            var iconLabel = UIFactory.CreateLabel(header, name + "Icon", initiallyExpanded ? "▼" : "►", TextAnchor.MiddleCenter);
            iconLabel.color = TextSecondary;
            iconLabel.fontSize = FontSizeSmall;
            UIFactory.SetLayoutElement(iconLabel.gameObject, minWidth: 20);

            // Title label
            var titleLabel = UIFactory.CreateLabel(header, name + "Title", title, TextAnchor.MiddleLeft);
            titleLabel.color = TextSecondary;
            titleLabel.fontStyle = FontStyle.Bold;
            titleLabel.fontSize = FontSizeNormal;
            // Overflow, not the Unity default Truncate: in a height-constrained header row a bold 14px
            // line can exceed the row height and Truncate culls the WHOLE line → the title vanished on
            // games whose default UI font has taller line metrics. minHeight also reserves the row height.
            titleLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            titleLabel.verticalOverflow = VerticalWrapMode.Overflow;
            UIFactory.SetLayoutElement(titleLabel.gameObject, minHeight: RowHeightMedium, flexibleWidth: 9999);

            // Make header clickable (button will be added by caller to wire up toggle)
            var headerBtn = header.AddComponent<Button>();
            headerBtn.targetGraphic = header.GetComponent<Image>();

            // Content container
            var content = UIFactory.CreateVerticalGroup(container, name + "Content", false, false, true, true, SmallSpacing);
            UIFactory.SetLayoutElement(content, flexibleWidth: 9999);
            content.SetActive(initiallyExpanded);

            var contentLayout = content.GetComponent<VerticalLayoutGroup>();
            if (contentLayout != null)
            {
                contentLayout.padding = Compat.MakeRectOffset(10, 10, 10, 10);
            }

            return (container, header, iconLabel, titleLabel, content);
        }

        /// <summary>
        /// Updates the visual state of a collapsible section.
        /// </summary>
        public static void SetCollapsibleState(Text iconLabel, GameObject content, bool expanded)
        {
            if (iconLabel != null) iconLabel.text = expanded ? "▼" : "►";
            if (content != null) content.SetActive(expanded);
        }

        #endregion
    }
}
