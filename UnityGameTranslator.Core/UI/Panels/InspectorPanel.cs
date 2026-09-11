using System;
using System.Collections.Generic;
using UniverseLib.UI;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.UI;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Inspector mode: determines behavior for exclusion or bitmap replacement.
    /// </summary>
    public enum InspectorMode
    {
        Exclusion,
        BitmapReplace,
        FontOverride,
        TextEdit
    }

    /// <summary>
    /// Inspector panel for visually selecting UI elements.
    /// Dual mode: Exclusion (select text to exclude) or BitmapReplace (select images to replace).
    /// DevTools-style: hover preview with highlight overlay, click to select.
    ///
    /// ⚠ Migrated to the UI vocabulary 2026-09-08 (see
    /// analyse/inventaire-couches/brief-migration-panneau.md): everything that actually touches
    /// Unity — the reflection-based IL2CPP-safe raycast, the highlight overlay, camera picking —
    /// moved to <see cref="InspectorPicker"/>. This class keeps only what a panel is for: buttons,
    /// labels, the text-edit list, and the per-mode decisions.
    /// </summary>
    public class InspectorPanel : TranslatorPanelBase
    {
        public override string Name => _currentMode == InspectorMode.BitmapReplace ? "Image Inspector"
            : _currentMode == InspectorMode.FontOverride ? "Font Override Inspector"
            : "Element Inspector";
        public override int MinWidth => 420;
        public override int MinHeight => 360;
        public override int PanelWidth => 480;
        public override int PanelHeight => 420;

        protected override int MinPanelHeight => 360;

        // TextEdit mode contains a scrollable list of child texts that benefits from extra
        // vertical room when the user enlarges the panel.
        protected override bool HasFlexibleContent => true;

        // The in-game half of the inspector — see its own class summary.
        private readonly InspectorPicker _picker;

        // Mode
        private InspectorMode _currentMode = InspectorMode.Exclusion;

        // UI elements — shared
        private LabelHandle _hoveredPathLabel;
        private LabelHandle _selectedPathLabel;
        private LabelHandle _statusLabel;
        private ButtonHandle _cancelBtn;
        private LabelHandle _titleLabel;
        private Components.HelpZone _helpZone;

        // UI elements — Exclusion mode
        private ButtonHandle _excludeThisBtn;
        private ButtonHandle _excludePatternBtn;
        private Host _exclusionActionsRow;

        // UI elements — BitmapReplace mode
        private ButtonHandle _exportOriginalBtn;
        private ButtonHandle _markReplaceBtn;
        private Host _imageActionsRow;
        private LabelHandle _spriteInfoLabel;

        // UI elements — TextEdit mode
        private Host _textEditRow;
        private ScrollList _textEditList;
        private LabelHandle _textEditCountLabel;

        /// <summary>
        /// The list's floor, and its ceiling once filled. One text needs a small box; a busy screen
        /// hands this panel a dozen at once, and 260px of them was the complaint. The ceiling is
        /// not a limit on the list — it scrolls — but on how much window it may claim by itself;
        /// past that the panel is still resizable by hand, up to the screen.
        /// </summary>
        private const int TextEditListMinHeight = 260;
        private const int TextEditListMaxHeight = 560;

        /// <summary>Rough height of one edit row: labels + field + preview + buttons + spacing.</summary>
        private const int TextEditRowHeight = 120;

        /// <summary>
        /// Rows waiting for an AI retranslation. The answer arrives seconds later, on the worker
        /// thread, long after the click — without this the row would stay on "Queued for AI..."
        /// forever, which is exactly what "the button does nothing" looked like.
        /// A list, not a dictionary: the same text can be shown by several components at once.
        /// </summary>
        private readonly List<TextEditRowState> _pendingRetranslateRows = new List<TextEditRowState>();

        /// <summary>
        /// One editable line of the in-game text editor, whole. Its handlers, the answer that
        /// comes back from another thread seconds later, and the button/preview refresh all read
        /// from this — passing the pieces around one by one is how one of them gets forgotten.
        /// </summary>
        private sealed class TextEditRowState
        {
            public string Key;
            public object Component;
            // Live values of the [!v*N] placeholders as displayed when the row was built: what is
            // stored and edited carries placeholders, what the game draws carries numbers.
            public Dictionary<int, string> LiveNumbers;

            public FieldHandle Input;
            public LabelHandle KeyLabel;
            /// <summary>
            /// The tag's coloured square and the letter on it — what the website has always drawn
            /// and this panel wrote as `[H] ` in front of the key, in the same grey as the key.
            /// Kept on the row because four different gestures rewrite it: saving, retranslating,
            /// reverting, and the periodic refresh.
            /// </summary>
            public TagChipHandle TagChip;
            public LabelHandle PreviewLabel;
            public ButtonHandle SaveBtn;
            public ButtonHandle RetranslateBtn;
            public ButtonHandle RevertBtn;

            /// <summary>
            /// What the AI last proposed for this line, or null. Kept so that saving it untouched
            /// can be filed as "A" — the machine wrote it — instead of claiming a human did.
            /// </summary>
            public string AiProposal;
        }

        // Camera selection — the dropdown lives here (it is UI); which camera is picked lives in
        // the picker, which is what actually needs a Camera reference.
        private Components.SearchableDropdown _cameraDropdown;

        // State kept about the current selection, for the buttons below to act on. The Unity side
        // of a selection (the GameObject/element it came from) never leaves the picker.
        private string _lastSelectedPath = "";
        private string _lastSelectedName = "";
        private object _lastSelectedSpriteObj = null;

        public InspectorPanel(UIBase owner) : base(owner)
        {
            _picker = new InspectorPicker();
            _picker.Hovered += OnHovered;
            _picker.Picked += OnPicked;

            // Panels are built once for the life of the process (CreatePanels), so this
            // subscription needs no matching removal — and must not be made per row, which would
            // pile up one handler per click on a static event.
            TranslatorCore.OnRetranslateFinished += OnRetranslateFinished;
        }

        protected override void ConstructPanelContent()
        {
            Layout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            // Title
            _titleLabel = ScopedTitle(scrollContent, "Title", "Element Inspector", EditSide.Local,
                                      policy: TextPolicy.Dynamic);

            Stacks.Spacer(scrollContent, 5);

            // Main card
            var card = Stacks.Card(scrollContent, "InspectorCard", PanelWidth - 60, stretchVertically: true);

            // Instructions
            Labels.Create(card, "InstructionsLabel", "Instructions", TextRole.SectionTitle);
            Labels.Create(card, "InstructionsHint", "Hover over any UI element to preview it. Click to select.",
                         TextRole.Hint);

            Stacks.Spacer(card, 8);

            // --- Camera selection ---
            Labels.Create(card, "CameraLabel", "Target", TextRole.SectionTitle);

            _cameraDropdown = new Components.SearchableDropdown("CameraTarget",
                new[] { "UI Only" }, "UI Only", popupHeight: 150, showSearch: false);
            var cameraHost = _cameraDropdown.CreateUI(card, OnCameraSelected, PanelWidth - 80, stretch: true);
            _helpZone?.Describe(cameraHost,
                "'UI Only' picks on-screen interface text. Choose a camera to pick objects in the game world instead.");

            Stacks.Spacer(card, 8);

            // --- Hovered Element section ---
            Labels.Create(card, "HoverSectionLabel", "Hovered", TextRole.SectionTitle);

            var hoverBox = Stacks.Section(card, "HoverBox");

            _hoveredPathLabel = Labels.Create(hoverBox, "HoverPathValue", "(move cursor over a UI element)",
                TextRole.Small, tone: Tone.Muted, policy: TextPolicy.Dynamic, fill: Fill.Stretch,
                minHeight: UIStyles.RowHeightNormal);
            _hoveredPathLabel.Italic = true;

            Stacks.Spacer(card, 8);

            // --- Selected Element section ---
            Labels.Create(card, "SelectedSectionLabel", "Selected", TextRole.SectionTitle);

            var selectedBox = Stacks.Section(card, "SelectedBox");

            _selectedPathLabel = Labels.Create(selectedBox, "SelectedPathValue", "(click to select)",
                TextRole.Small, tone: Tone.Muted, policy: TextPolicy.Dynamic, fill: Fill.Stretch,
                minHeight: UIStyles.RowHeightNormal);
            _selectedPathLabel.Italic = true;

            Stacks.Spacer(card, 8);

            // --- Sprite info (BitmapReplace mode only) ---
            _spriteInfoLabel = Labels.Create(card, "SpriteInfo", "", TextRole.Small, tone: Tone.Secondary,
                policy: TextPolicy.Dynamic, fill: Fill.Stretch, minHeight: UIStyles.RowHeightSmall);
            _spriteInfoLabel.Visible = false;

            Stacks.Spacer(card, 4);

            // --- Action buttons ---
            Labels.Create(card, "ActionsLabel", "Actions", TextRole.SectionTitle);

            // Exclusion mode actions
            _exclusionActionsRow = Stacks.Row(card, "ExclusionActionRow", spacing: 5, minHeight: UIStyles.ButtonHeight);

            _excludeThisBtn = Buttons.Create(_exclusionActionsRow, "ExcludeThisBtn", "Exclude This Element",
                ButtonTone.Primary, fill: Fill.Stretch);
            _excludeThisBtn.Enabled = false;
            _excludeThisBtn.Clicked += OnExcludeThisClicked;
            _helpZone?.Describe(_excludeThisBtn, "Never translate this exact element (only this one)");

            _excludePatternBtn = Buttons.Create(_exclusionActionsRow, "ExcludePatternBtn", "Exclude Pattern",
                ButtonTone.Secondary, fill: Fill.Stretch);
            _excludePatternBtn.Enabled = false;
            _excludePatternBtn.Clicked += OnExcludePatternClicked;
            _helpZone?.Describe(_excludePatternBtn,
                "Never translate ANY element with this name, anywhere in the game (e.g. every chat line)");

            // BitmapReplace mode actions
            _imageActionsRow = Stacks.Row(card, "ImageActionRow", spacing: 5, minHeight: UIStyles.ButtonHeight);

            _exportOriginalBtn = Buttons.Create(_imageActionsRow, "ExportOriginalBtn", "Export Original",
                ButtonTone.Primary, fill: Fill.Stretch);
            _exportOriginalBtn.Enabled = false;
            _exportOriginalBtn.Clicked += OnExportOriginalClicked;
            _helpZone?.Describe(_exportOriginalBtn, "Save the game's current image to disk as a template you can edit");

            _markReplaceBtn = Buttons.Create(_imageActionsRow, "MarkReplaceBtn", "Mark for Replace",
                ButtonTone.Secondary, fill: Fill.Stretch);
            _markReplaceBtn.Enabled = false;
            _markReplaceBtn.Clicked += OnMarkReplaceClicked;
            _helpZone?.Describe(_markReplaceBtn,
                "Register this image for replacement: drop your edited version in the images folder and it swaps in-game");

            _imageActionsRow.Visible = false; // Hidden by default (exclusion mode)

            // TextEdit mode — scrollable list of child texts
            _textEditRow = Stacks.Vertical(card, "TextEditRow", spacing: 4, fillHeight: true);

            _textEditCountLabel = Labels.Create(_textEditRow, "TextEditCount", "", TextRole.Small,
                tone: Tone.Secondary, policy: TextPolicy.Dynamic, minHeight: UIStyles.RowHeightSmall);

            // See TranslatorPanelBase.ScrollingListHeightRule. Both numbers are revised once the
            // list is filled (SizeTextEditList): the smallest useful box for one line is not the
            // smallest useful box for a dozen, and this panel is regularly handed a dozen.
            _textEditList = ScrollList.Create(_textEditRow, "TextEditScroll",
                minHeight: TextEditListMinHeight, preferredHeight: TextEditListMinHeight);

            _textEditRow.Visible = false; // Hidden by default

            // Shared clear selection button
            var actionRow2 = Stacks.Row(card, "ActionRow2", spacing: 5, minHeight: UIStyles.ButtonHeight);

            _cancelBtn = Buttons.Create(actionRow2, "CancelBtn", "Clear Selection", ButtonTone.Secondary,
                fill: Fill.Stretch);
            _cancelBtn.Enabled = false;
            _cancelBtn.Clicked += OnCancelClicked;
            _helpZone?.Describe(_cancelBtn,
                "Deselect the current element and keep inspecting. Nothing is changed.");

            // Status label
            Stacks.Spacer(card, 5);
            _statusLabel = Labels.Create(card, "Status", "", TextRole.Small, tone: Tone.Plain,
                policy: TextPolicy.Dynamic, minHeight: UIStyles.RowHeightSmall);

            // Footer button (fixed at bottom)
            var stopBtn = Buttons.Primary(buttonRow, "StopBtn", "Stop Inspecting");
            stopBtn.Clicked += OnStopClicked;
            _helpZone?.Describe(stopBtn, "Leave inspect mode and close this window. Element picking stops.");
        }

        /// <summary>
        /// Set the inspector mode. Must be called before SetActive(true).
        /// </summary>
        public void SetMode(InspectorMode mode)
        {
            _currentMode = mode;
        }

        private void UpdateUIForMode()
        {
            bool isImage = _currentMode == InspectorMode.BitmapReplace;
            bool isFontOverride = _currentMode == InspectorMode.FontOverride;
            bool isTextEdit = _currentMode == InspectorMode.TextEdit;

            // Update title
            if (_titleLabel != null)
                _titleLabel.Say(isImage ? "Image Inspector"
                    : isFontOverride ? "Font Override — Click on an element"
                    : isTextEdit ? "Text Editor — Click on text to edit"
                    : "Element Inspector");

            // Toggle action button visibility per mode
            _exclusionActionsRow.Visible = !isImage && !isFontOverride && !isTextEdit;
            _imageActionsRow.Visible = isImage;
            _spriteInfoLabel.Visible = isImage;
            _textEditRow.Visible = false; // Shown only after clicking a text

            // The picker already rebuilt its camera list in Start(); just mirror it into the dropdown.
            _cameraDropdown.SetOptions(_picker.CameraNames);
            _cameraDropdown.SelectedValue = "UI Only";
        }

        private void OnCameraSelected(string value)
        {
            int index = Array.IndexOf(_picker.CameraNames, value);
            _picker.SelectCamera(index);

            ClearSelection();
            ClearHover();
        }

        public override void SetActive(bool active)
        {
            bool wasActive = Enabled;
            base.SetActive(active);

            if (active)
            {
                if (!wasActive)
                {
                    _picker.Start(_currentMode, Rect);
                    ClearSelection();
                    ClearHover();
                    _statusLabel.Show("");
                    UpdateUIForMode();
                    // The Main is hidden while inspecting and put back after — the ScreenRouter's
                    // rule, run on this panel's VisibilityChanged, so a hotkey close counts too.
                }
            }
            else
            {
                _picker.Stop();
            }
        }

        public override void Update()
        {
            base.Update();

            if (!Enabled) return;
            _picker.Tick();
        }

        /// <summary>The hovered path changed (or was cleared: <paramref name="path"/> is empty).</summary>
        private void OnHovered(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                _hoveredPathLabel.Say("(move cursor over a UI element)");
                _hoveredPathLabel.Tone = Tone.Muted;
            }
            else
            {
                _hoveredPathLabel.Show(path);
                _hoveredPathLabel.Tone = Tone.Secondary;
            }
            _hoveredPathLabel.Italic = true;
        }

        /// <summary>
        /// Something was picked. Every mode's own reaction lives here — the picker only reports
        /// WHAT was found, never what to do about it.
        ///
        /// ⚠ A UI Toolkit pick never enabled the exclude/export/mark buttons in the original inline
        /// handlers either — <c>SelectUIToolkitAt</c> touched only the path labels and (in
        /// BitmapReplace mode) the sprite line, never a button's <c>interactable</c>. Preserved
        /// here as <c>isCanvasOrWorld</c> rather than silently fixed: see the migration report.
        /// </summary>
        private void OnPicked(PickedTarget target)
        {
            _lastSelectedPath = target.Path;
            _lastSelectedName = target.Name;
            _lastSelectedSpriteObj = target.SpriteObject;

            _selectedPathLabel.Show(target.Path);
            _selectedPathLabel.Tone = Tone.Plain;
            _selectedPathLabel.Italic = false;
            _cancelBtn.Enabled = true;

            bool isCanvasOrWorld = target.Engine != "UI Toolkit";

            if (_currentMode == InspectorMode.TextEdit)
            {
                ShowTextEditUI(target.Path);
            }
            else if (_currentMode == InspectorMode.FontOverride)
            {
                // Font override mode: add override for parent path with /** to cover siblings
                // e.g. "Canvas/Panel/Table/Text" → "path:Canvas/Panel/Table/**"
                string overridePath = target.Path;
                int lastSlash = overridePath.LastIndexOf('/');
                if (lastSlash > 0)
                    overridePath = overridePath.Substring(0, lastSlash) + "/**";
                // Close inspector FIRST (the router puts the Main back if it was open)
                SetActive(false);
                // THEN open the parameters window (SetAsLastSibling puts it on top)
                Intents.AddFontOverride("path:" + overridePath);
                return;
            }
            else if (_currentMode == InspectorMode.BitmapReplace)
            {
                if (!target.HasSprite && target.Engine == "UI Toolkit")
                {
                    // Said rather than left blank: an element can draw a bare texture, or a shape
                    // with no picture at all, and neither has a name to match a replacement to.
                    _spriteInfoLabel.Show("No named image on this element.");
                    _spriteInfoLabel.Tone = Tone.Muted;
                }
                else
                {
                    _spriteInfoLabel.Show(
                        $"{target.SpriteComponentType}: \"{target.SpriteName}\" ({target.SpriteWidth}x{target.SpriteHeight})");
                    _spriteInfoLabel.Tone = Tone.Plain;
                }

                if (isCanvasOrWorld)
                {
                    _exportOriginalBtn.Enabled = target.HasSprite;
                    _markReplaceBtn.Enabled = target.HasSprite;
                }
            }
            else
            {
                if (isCanvasOrWorld)
                {
                    _excludeThisBtn.Enabled = true;
                    _excludePatternBtn.Enabled = true;
                }
            }

            if (isCanvasOrWorld)
            {
                _statusLabel.Say("Element selected");
                _statusLabel.Tone = Tone.Success;
            }
        }

        private void ClearHover()
        {
            _hoveredPathLabel.Say("(move cursor over a UI element)");
            _hoveredPathLabel.Tone = Tone.Muted;
            _hoveredPathLabel.Italic = true;
            _picker.ClearHover();
        }

        private void ClearSelection()
        {
            _lastSelectedPath = "";
            _lastSelectedName = "";
            _lastSelectedSpriteObj = null;
            _selectedPathLabel.Say("(click to select)");
            _selectedPathLabel.Tone = Tone.Muted;
            _selectedPathLabel.Italic = true;
            _excludeThisBtn.Enabled = false;
            _excludePatternBtn.Enabled = false;
            _exportOriginalBtn.Enabled = false;
            _markReplaceBtn.Enabled = false;
            _cancelBtn.Enabled = false;
            _spriteInfoLabel.Show("");
            _picker.ClearSelection();

            // Clear TextEdit UI
            _pendingRetranslateRows.Clear();
            _textEditRow.Visible = false;
            _textEditList.Clear();
        }

        private void OnExcludeThisClicked()
        {
            if (string.IsNullOrEmpty(_lastSelectedPath)) return;

            TranslatorCore.AddExclusion(_lastSelectedPath);

            _statusLabel.Say("Excluded!");
            _statusLabel.Tone = Tone.Success;

            ClearSelection();
        }

        private void OnExcludePatternClicked()
        {
            if (string.IsNullOrEmpty(_lastSelectedPath) || string.IsNullOrEmpty(_lastSelectedName)) return;

            string pattern = "**/" + _lastSelectedName;

            TranslatorCore.AddExclusion(pattern);
            TranslatorCore.SaveCache();

            _statusLabel.Show(Tr("Excluded:") + $" {pattern}");
            _statusLabel.Tone = Tone.Success;

            ClearSelection();
        }

        private void OnCancelClicked()
        {
            ClearSelection();
            _statusLabel.Show("");
        }

        #region BitmapReplace Actions

        private void OnExportOriginalClicked()
        {
            if (_lastSelectedSpriteObj == null) return;

            var spriteName = ImageReplacer.GetSpriteName(_lastSelectedSpriteObj);
            if (string.IsNullOrEmpty(spriteName))
            {
                _statusLabel.Say("Cannot export: sprite has no name");
                _statusLabel.Tone = Tone.Error;
                return;
            }

            // If not already marked, mark it first
            if (!ImageReplacer.GetAll().ContainsKey(spriteName))
            {
                MarkCurrentForReplace(spriteName);
            }

            var exportedPath = ImageReplacer.ExportOriginal(_lastSelectedSpriteObj, spriteName);
            if (exportedPath != null)
            {
                _statusLabel.Show($"Exported: {System.IO.Path.GetFileName(exportedPath)}");
                _statusLabel.Tone = Tone.Success;
                TranslatorCore.SaveCache();
            }
            else
            {
                _statusLabel.Say("Export failed (check log)");
                _statusLabel.Tone = Tone.Error;
            }
        }

        private void OnMarkReplaceClicked()
        {
            if (_lastSelectedSpriteObj == null) return;

            var spriteName = ImageReplacer.GetSpriteName(_lastSelectedSpriteObj);
            if (string.IsNullOrEmpty(spriteName))
            {
                _statusLabel.Say("Cannot mark: sprite has no name");
                _statusLabel.Tone = Tone.Error;
                return;
            }

            MarkCurrentForReplace(spriteName);

            _statusLabel.Show(Tr("Marked:") + $" {spriteName}");
            _statusLabel.Tone = Tone.Success;

            TranslatorCore.SaveCache();
            ClearSelection();
        }

        private void MarkCurrentForReplace(string spriteName)
        {
            ImageReplacer.AddReplacementFromSprite(_lastSelectedSpriteObj, _lastSelectedPath, spriteName);
        }

        #endregion

        #region TextEdit Mode

        /// <summary>
        /// Show what can be edited under a path.
        ///
        /// ⚠ The path is all it needs — it took a GameObject too and never read it, which is what
        /// made the editor look tied to uGUI. It is not: FindTextComponentsAtPath matches on the
        /// path string, so a UI Toolkit element works the same way.
        /// </summary>
        private void ShowTextEditUI(string path)
        {
            if (_textEditRow == null || _textEditList == null) return;

            var textEntries = new List<(object component, string text, string originalKey, string tag, string childPath, Dictionary<int, string> liveNumbers)>();

            // Try the clicked path, then walk up the hierarchy until we find text components
            string searchPath = path;
            for (int attempt = 0; attempt < 3 && textEntries.Count == 0; attempt++)
            {
                FindTextComponentsAtPath(searchPath, textEntries);

                if (textEntries.Count == 0)
                {
                    // Go up one level
                    int lastSlash = searchPath.LastIndexOf('/');
                    if (lastSlash <= 0) break;
                    searchPath = searchPath.Substring(0, lastSlash);
                }
            }

            if (textEntries.Count == 0)
            {
                _statusLabel.Say("No text components found");
                _statusLabel.Tone = Tone.Warning;
                return;
            }

            // Clear previous entries — and with them any retranslation still expected for a row
            // that is about to be destroyed
            _pendingRetranslateRows.Clear();
            _textEditList.Clear();

            // Show the edit UI
            _textEditRow.Visible = true;
            _textEditCountLabel.Say($"{textEntries.Count} text(s) found:");

            // Create an editable row for each text
            for (int i = 0; i < textEntries.Count; i++)
            {
                CreateTextEditRow(textEntries[i]);
            }

            SizeTextEditList(textEntries.Count);

            _statusLabel.Say("Edit translations and click Save");
            _statusLabel.Tone = Tone.Secondary;
        }

        /// <summary>
        /// Ask the window for the height this many rows actually need, and let it grow.
        ///
        /// ⚠ The panel measures its content to size itself, and a scrolling list is weighed at its
        /// PREFERRED height, not at what it holds — so a list left at its floor keeps the window at
        /// the size of one or two rows however many were found. Revising the floor is what makes
        /// the measurement tell the truth. A size the user picked themselves still wins:
        /// RecalculateSize leaves it alone.
        /// </summary>
        private void SizeTextEditList(int rowCount)
        {
            if (_textEditList == null) return;

            int wanted = Math.Max(TextEditListMinHeight, Math.Min(TextEditListMaxHeight, rowCount * TextEditRowHeight));
            _textEditList.SetHeight(wanted);

            RecalculateSize();
        }

        /// <summary>
        /// Find all text components whose path matches or is a child of the given path.
        /// Uses FindAllObjectsOfType (IL2CPP-safe).
        /// </summary>
        private void FindTextComponentsAtPath(string pathPrefix,
            List<(object component, string text, string originalKey, string tag, string childPath, Dictionary<int, string> liveNumbers)> results)
        {
            // 🔴 One enumeration for every framework. This used to name UI.Text and TMP_Text here,
            // so the in-game editor could only ever edit those two — on an NGUI, tk2d, TMProOld,
            // TextMesh or UI Toolkit game it opened on an empty list, whatever had been picked.
            // See analyse/text-targets-audit.md.
            foreach (var target in TextTargets.All())
            {
                try
                {
                    string compPath = target.Path;
                    if (compPath != pathPrefix && !compPath.StartsWith(pathPrefix + "/"))
                        continue;

                    // Resolve to the cache entry with pipeline normalization, so texts
                    // with dynamic numbers map to their [!v*N] pattern key
                    var resolution = TranslatorCore.ResolveDisplayedText(target.Text);
                    if (resolution == null) continue;

                    string tag = resolution.Entry?.Tag ?? "—";
                    // Prefill: current translation with placeholders, or the normalized
                    // source text when no entry exists yet
                    string translation = resolution.Entry?.Value ?? resolution.Key;

                    results.Add((target.Owner, translation, resolution.Key, tag, compPath,
                                 resolution.CapturedNumbers));
                }
                catch { }
            }
        }

        private void CreateTextEditRow((object component, string text, string originalKey, string tag, string childPath, Dictionary<int, string> liveNumbers) entry)
        {
            var row = Stacks.Vertical(_textEditList.Rows, "TextEditEntry", spacing: 3, surface: Surface.Card);

            // Original key: full text, word-wrapped (translating needs the whole source).
            //
            // ⚠ richText: false, and this is the whole point of the row. Left on, the label RENDERS
            // `<color=#FF0000>` instead of showing it, so a decorated line appeared coloured with
            // its markup invisible, and there was no way to see what had to be preserved while
            // editing. What is edited here is the file's exact text; that is what has to be on
            // screen. The rendering is shown separately below.
            // 🔴 The tag as a CHIP, not as `[H] ` in front of the key.
            //
            // Written into the key's own label, it was grey text among grey text — the one thing
            // on the row that carries a colour everyone has already learnt on the site's tables,
            // and it carried none. It cannot be rich text either: this label deliberately renders
            // markup literally (see above), so `<color=…>` would show as characters.
            //
            // Hence a row: the chip, then the key. The colours come from the shared library, so
            // changing how a tag looks is one edit there rather than three across the products.
            var keyRow = Stacks.Horizontal(row, "KeyRow", spacing: 6, placement: Placement.TopLeft,
                                           minHeight: UIStyles.RowHeightSmall);

            var tagChip = TagChips.Create(keyRow, entry.tag);

            var keyLabel = Labels.Create(keyRow, "Key", entry.originalKey, TextRole.Small,
                policy: TextPolicy.Excluded, richText: false, fill: Fill.Stretch,
                minHeight: UIStyles.RowHeightSmall);

            // Live values of the [!v*N] placeholders, as currently displayed in-game
            if (entry.liveNumbers != null && entry.liveNumbers.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in entry.liveNumbers)
                    parts.Add($"[!v*{kv.Key}] = {kv.Value}");
                Labels.Create(row, "LiveValues",
                    $"Keep placeholders as-is. Current values: {string.Join("   ", parts)}", TextRole.Caption,
                    tone: Tone.Accent, policy: TextPolicy.Excluded, richText: false, fill: Fill.Stretch,
                    minHeight: UIStyles.RowHeightSmall);
            }

            // Editable translation field — raw text, markup included, exactly as the file holds it
            var input = Fields.Create(row, "TranslationInput", "Enter translation...",
                FieldKind.Multiline, minHeight: 40, richText: false);
            input.Text = entry.text;

            // …and right under it, the same string RENDERED. One shows what you are editing, the
            // other what the game will draw — a colour tag broken while typing shows up here
            // immediately, instead of on a screen you have to go back to.
            var previewLabel = Labels.Create(row, "Preview", "", TextRole.Small, tone: Tone.Secondary,
                policy: TextPolicy.Excluded, fill: Fill.Stretch, minHeight: UIStyles.RowHeightSmall);

            // Buttons row
            var btnRow = Stacks.Horizontal(row, "BtnRow", spacing: 4, minHeight: UIStyles.RowHeightNormal);

            // Everything this row needs, in one place: its handlers, the answer that arrives
            // seconds later on another thread, and the button-state refresh all work from it.
            // Passing the pieces around separately is how one of them ends up forgotten.
            var rowState = new TextEditRowState
            {
                Key = entry.originalKey,
                Component = entry.component,
                LiveNumbers = entry.liveNumbers,
                Input = input,
                KeyLabel = keyLabel,
                TagChip = tagChip,
                PreviewLabel = previewLabel
            };
            string capturedKey = entry.originalKey;
            object capturedComponent = entry.component;
            var capturedNumbers = entry.liveNumbers;

            // ⚠ policy: Excluded on all three — none of these three labels was ever registered for
            // translation in the original either (no RegisterUIText call reached them), unlike
            // every other button in this panel. Preserved as-is; see the migration report.
            var saveBtn = Buttons.Compact(btnRow, "SaveBtn", "Save (H)", ButtonTone.Success,
                minWidth: 80, policy: TextPolicy.Excluded);

            var retranslateBtn = Buttons.Compact(btnRow, "RetranslateBtn", "Retranslate (AI)", ButtonTone.Primary,
                minWidth: 110, policy: TextPolicy.Excluded);

            var revertBtn = Buttons.Compact(btnRow, "RevertBtn", "Revert", ButtonTone.Secondary,
                minWidth: 70, policy: TextPolicy.Excluded);
            _helpZone?.Describe(revertBtn,
                "Put the field back to what the translation file holds, discarding what you typed or what the AI proposed.");

            rowState.SaveBtn = saveBtn;
            rowState.RetranslateBtn = retranslateBtn;
            rowState.RevertBtn = revertBtn;

            // Both buttons exist before either handler is written: each one has to be able to put
            // the other back in its right state, and a lambda cannot reach a local declared later.
            saveBtn.Clicked += () =>
            {
                string newValue = input.Text;
                if (string.IsNullOrEmpty(newValue)) return;
                // Saving an unchanged field is not a no-op: it would stamp the line "H", turning a
                // machine translation into a human one nobody wrote. The button is greyed for it,
                // and refuses anyway — a greyed button is a hint, not a guarantee.
                if (!HasUnsavedEdit(capturedKey, newValue)) return;

                // ⚠ A belt. The button is already grey while a placeholder is broken and the line
                // under the field says which one, so this is unreachable through the interface —
                // and it stays, because a greyed button is a hint and not a guarantee. It says
                // nothing on screen: the row said it while the text was being typed.
                if (TranslatorCore.ValidateEditedPlaceholders(capturedKey, newValue) != null) return;

                // ⚠ The same belt, for the same reason: a key in presentation forms is the RTL
                // pipeline's display output read back, and saving it would file a key no source
                // text can ever match (D8). Known from the key alone, so the row says it and greys
                // Save before anything is typed; the write door in TranslatorCore refuses it too.
                if (TextShaping.RtlText.ContainsPresentationForms(capturedKey)) return;

                // Who wrote what is being saved. Accepting an AI proposal untouched files it as A:
                // stamping H would claim a review nobody performed, and that tag drives the
                // quality score, the A → V gesture and what the community sees.
                string tag = rowState.AiProposal != null
                             && string.Equals(newValue, rowState.AiProposal, StringComparison.Ordinal)
                    ? "A" : "H";

                TranslatorCore.SetTranslationFromEditor(capturedKey, newValue, tag);

                // Apply immediately to the component, with the live numbers re-injected
                try
                {
                    // ⚠ TextTargets, not TypeHelper: the latter is the uGUI answer and does nothing
                    // for a UI Toolkit element — the edit would be written to the file and never
                    // appear, which reads as the save having failed.
                    TextTargets.Write(capturedComponent,
                        TextNormalization.RestoreNumbersFromPlaceholders(newValue, capturedNumbers));
                }
                catch { }

                _statusLabel.Say(tag == "A" ? "AI translation applied" : "Saved!");
                _statusLabel.Tone = Tone.Success;
                rowState.TagChip.Retag(tag);
                RefreshRow(rowState);
            };

            retranslateBtn.Clicked += () =>
            {
                if (TranslatorCore.Config == null || !TranslatorCore.Config.IsTranslationEnabled)
                {
                    _statusLabel.Say("Translation is switched off — turn it on in Options first");
                    _statusLabel.Tone = Tone.Warning;
                    return;
                }

                // No confirmation here any more, and its absence is deliberate: a retranslation
                // now only PROPOSES. A hand-written line is replaced by the Save click, never by
                // this one — asking twice for something that has not happened yet trains people to
                // dismiss the question. The browser still asks, because there it does write.
                StartRetranslate(rowState);
            };

            revertBtn.Clicked += () =>
            {
                // Back to what the file holds — the AI's proposal and anything typed both go.
                input.Text = TranslatorCore.GetTranslationValue(capturedKey) ?? capturedKey;
                rowState.AiProposal = null;
                rowState.TagChip.Retag(TranslatorCore.GetTranslationTag(capturedKey));
                _statusLabel.Say("Back to the saved translation");
                _statusLabel.Tone = Tone.Secondary;
                RefreshRow(rowState);
            };

            // FieldHandle.Changed, never a raw InputField event — see UIHelpers.
            input.Changed += _ => RefreshRow(rowState);
            RefreshRow(rowState);
        }

        /// <summary>
        /// True when the field holds something the file does not. The comparison is against the
        /// stored value read NOW, so a line the AI or the browser changed under an untouched field
        /// settles back to "nothing to save" on its own.
        /// </summary>
        private static bool HasUnsavedEdit(string key, string fieldText)
        {
            if (string.IsNullOrEmpty(fieldText)) return false;
            // No entry yet: the field was prefilled with the source text, so saving it verbatim
            // would file the source as its own translation — still nothing worth saving.
            string stored = TranslatorCore.GetTranslationValue(key) ?? key;
            return !string.Equals(fieldText, stored, StringComparison.Ordinal);
        }

        /// <summary>
        /// Grey out what would do nothing — Save while the field matches the file or holds a
        /// broken placeholder, Retranslate while an answer for that line is already on its way —
        /// and keep the line under the field in step with what is being typed.
        /// </summary>
        private void RefreshRow(TextEditRowState row)
        {
            if (row?.Input == null) return;

            string field = row.Input.Text ?? "";
            bool changed = HasUnsavedEdit(row.Key, row.Input.Text);

            // 🔴 **Said while it is being typed, not after the click.** A placeholder dropped or
            // duplicated cannot be saved — so the button goes grey the moment it happens and the
            // line under the field says which token and how many times. Making somebody press a
            // button to be told a refusal we already knew is the shape this project forbids: what
            // is known before the click is said before the click.
            //
            // ⚠ The first of the two is known from the KEY alone, before a character is typed: a
            // row whose key is the RTL pipeline's own display output can never be saved, whatever
            // is put in the field. It said so after the click, on a status line at the other end of
            // the panel; it now says so on the row, from the moment the row exists.
            string problem = TextShaping.RtlText.ContainsPresentationForms(row.Key)
                ? "this row's key is display-shaped text, not a source text — nothing typed here can be saved"
                : changed ? TranslatorCore.ValidateEditedPlaceholders(row.Key, field) : null;

            if (row.SaveBtn != null) row.SaveBtn.Enabled = changed && problem == null;

            // ⚠ Revert stays live on ANY change, broken text included: undoing is exactly what
            // somebody wants when they have just broken something, and greying it here would leave
            // them stuck with a field they cannot save and cannot put back.
            if (row.RevertBtn != null) row.RevertBtn.Enabled = changed;

            if (row.RetranslateBtn != null)
                row.RetranslateBtn.Enabled = !_pendingRetranslateRows.Contains(row);

            if (row.PreviewLabel != null)
            {
                // One line under the field, two things it can say — and the problem wins, because
                // there is nothing useful to preview about a line the game would break on. A row of
                // its own for each would cost height per entry in a list that routinely holds a
                // dozen, and this panel was reported as too short.
                if (problem != null)
                {
                    row.PreviewLabel.Visible = true;
                    row.PreviewLabel.Tone = Tone.Error;
                    row.PreviewLabel.Show(problem);
                    return;
                }

                row.PreviewLabel.Tone = Tone.Secondary;

                // Shown only when there is markup to interpret. On a plain line the preview would
                // repeat the field word for word.
                bool worthShowing = field.IndexOf('<') >= 0 && field.IndexOf('>') >= 0;
                row.PreviewLabel.Visible = worthShowing;
                if (worthShowing)
                {
                    // The only place in this row where markup is meant to be interpreted. Numbers
                    // are put back too, so this is the line as the game would draw it right now.
                    row.PreviewLabel.Show(TextNormalization.RestoreNumbersFromPlaceholders(field, row.LiveNumbers));
                }
            }
        }

        /// <summary>
        /// Ask the AI for another translation of this line. It PROPOSES: nothing is written, the
        /// answer lands in the field and waits for Save — which is why there is no confirmation
        /// step and nothing to lose if it goes wrong.
        /// </summary>
        private void StartRetranslate(TextEditRowState row)
        {
            if (row == null) return;
            if (!_pendingRetranslateRows.Contains(row))
                _pendingRetranslateRows.Add(row);

            if (!TranslatorCore.RemoveTranslationForRetranslate(row.Key, storeResult: false))
            {
                _pendingRetranslateRows.Remove(row);
                _statusLabel.Say("Could not ask the AI — check the backend in Options");
                _statusLabel.Tone = Tone.Error;
                row.TagChip.Retag(TranslatorCore.GetTranslationTag(row.Key));
                RefreshRow(row);
                return;
            }

            _statusLabel.Say("Asking the AI for another translation...");
            _statusLabel.Tone = Tone.Accent;
            // ⚠ The chip keeps the tag the line still HAS while the AI is asked. It used to read
            // `[AI...]`, which announced a provenance the line had not been given yet — and if the
            // request failed, that was simply false. The status line above says what is happening.
            RefreshRow(row);
        }

        /// <summary>
        /// A retranslation ended. Raised on the WORKER thread — everything below touches Unity
        /// objects, so it hops to the main thread first.
        /// </summary>
        private void OnRetranslateFinished(string key, string value, TranslatorCore.RetranslateOutcome outcome)
        {
            TranslatorUIManager.RunOnMainThread(() => ApplyRetranslateResult(key, value, outcome));
        }

        private void ApplyRetranslateResult(string key, string value, TranslatorCore.RetranslateOutcome outcome)
        {
            var rows = _pendingRetranslateRows.FindAll(r => r.Key == key);
            if (rows.Count == 0)
            {
                // The rows were destroyed while the AI was answering — another element was clicked,
                // or the selection was cleared. The proposal has nowhere to land and, being a
                // proposal, was never written anywhere: it is simply lost. Said out loud rather
                // than dropped in silence; nothing is damaged, but "I asked and got nothing" must
                // have an explanation somewhere.
                TranslatorCore.LogInfo("[Retranslate] Answer arrived after its row was gone — proposal discarded");
                return;
            }
            _pendingRetranslateRows.RemoveAll(r => r.Key == key);

            bool proposed = outcome == TranslatorCore.RetranslateOutcome.Replaced && value != null;

            foreach (var row in rows)
            {
                // The row may have been destroyed since (another element was clicked)
                if (row.Input == null || row.KeyLabel == null) continue;

                if (proposed)
                {
                    // Into the FIELD, not into the file and not onto the game screen. Remembered
                    // so that accepting it untouched can be filed as A rather than as human work.
                    row.AiProposal = value;
                    row.Input.Text = value;
                }

                row.TagChip.Retag(TranslatorCore.GetTranslationTag(key));
                RefreshRow(row);
            }

            switch (outcome)
            {
                case TranslatorCore.RetranslateOutcome.Replaced:
                    _statusLabel.Say("New translation proposed — Save to keep it, Revert to drop it");
                    _statusLabel.Tone = Tone.Success;
                    break;
                case TranslatorCore.RetranslateOutcome.Unchanged:
                    _statusLabel.Say("The AI gave the same translation again — nothing changed");
                    _statusLabel.Tone = Tone.Warning;
                    break;
                default:
                    _statusLabel.Say("The AI returned nothing — the line is untouched");
                    _statusLabel.Tone = Tone.Error;
                    break;
            }
        }

        #endregion

        private void OnStopClicked()
        {
            SetActive(false);

            // Back to the parameters window, on the tab this mode came from
            Intents.OpenTranslationParameters(
                _currentMode == InspectorMode.BitmapReplace ? ParametersTab.Images
                : _currentMode == InspectorMode.FontOverride ? ParametersTab.FontOverrides
                : _currentMode == InspectorMode.TextEdit ? ParametersTab.Tools
                : ParametersTab.Exclusions);
        }
    }
}
