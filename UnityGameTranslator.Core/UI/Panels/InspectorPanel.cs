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
        /// <summary>The screen as a document — common/spec/screens/inspector.json — read once; the base's constructor reads the sizes below through it.</summary>
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("inspector");

        /// <summary>What the builder made of the document: every piece by name.</summary>
        private BuiltScreen _screen;

        // ⚠ The name is the key the window's position is remembered under, and each mode keeps
        // its own: a rule, not a shape, so it stays here. The document names the ordinary one.
        public override string Name => _currentMode == InspectorMode.BitmapReplace ? "Image Inspector"
            : _currentMode == InspectorMode.FontOverride ? "Font Override Inspector"
            : Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

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
        private ImageHandle _imagePreview;
        private Host _slotStrip;
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
        private static int TextEditListMinHeight => Doc.Nodes["TextEditScroll"].Int("minHeight") ?? 0;
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

        /// <summary>
        /// The screen is inspector.json; this builds it and keeps hold of what the code writes,
        /// shows or enables. What the document carries, and why — a document has no comments:
        /// - the title sits above the card, with the Local scope: everything here writes this
        ///   machine's files (exclusions, images, edited lines) and publishes nothing;
        /// - the card stretches so the text-edit list absorbs the room when the window grows; the
        ///   list states its floor once, and SizeTextEditList revises it once filled — the
        ///   smallest useful box for one line is not the smallest useful box for a dozen;
        /// - three rows of actions, one per mode (exclusion, image, text edit), shown by
        ///   UpdateUIForMode; a fourth, Clear Selection, shared by all;
        /// - the camera list is the picker's, set at show time (`options: code`).
        /// </summary>
        protected override void ConstructPanelContent()
        {
            Layout(out var body, out var footer, Doc.Width - 40);
            _helpZone = CreateHelpZone(footer, Doc.Help);
            _screen = ScreenBuilder.Build(Doc, body, footer, ActOf, help: _helpZone, title: ScopedTitle);

            _titleLabel = _screen.Label("Title");
            _cameraDropdown = _screen.Dropdown("CameraTarget");
            _hoveredPathLabel = _screen.Label("HoverPathValue");
            _selectedPathLabel = _screen.Label("SelectedPathValue");
            _imagePreview = _screen.Picture("ImagePreview");
            _slotStrip = _screen.Host("SlotStrip");
            _spriteInfoLabel = _screen.Label("SpriteInfo");
            _exclusionActionsRow = _screen.Host("ExclusionActionRow");
            _excludeThisBtn = _screen.Button("ExcludeThisBtn");
            _excludePatternBtn = _screen.Button("ExcludePatternBtn");
            _imageActionsRow = _screen.Host("ImageActionRow");
            _exportOriginalBtn = _screen.Button("ExportOriginalBtn");
            _markReplaceBtn = _screen.Button("MarkReplaceBtn");
            _textEditRow = _screen.Host("TextEditRow");
            _textEditCountLabel = _screen.Label("TextEditCount");
            _textEditList = _screen.List("TextEditScroll");
            _cancelBtn = _screen.Button("CancelBtn");
            _statusLabel = _screen.Label("Status");

            // Nothing is hovered or selected yet: the sentences that say so, and the acts closed
            // until something is.
            _hoveredPathLabel.Say("(move cursor over a UI element)");
            _selectedPathLabel.Say("(click to select)");
            _excludeThisBtn.Enabled = false;
            _excludePatternBtn.Enabled = false;
            _exportOriginalBtn.Enabled = false;
            _markReplaceBtn.Enabled = false;
            _cancelBtn.Enabled = false;

            // The picker's list arrives at show time; until then the one choice every mode has.
            _cameraDropdown.SetOptions(new[] { "UI Only" });
            _cameraDropdown.SelectedValue = "UI Only";
        }

        /// <summary>What each verb the document asks for does. A verb with no answer here fails at construction, not at the click.</summary>
        private Action ActOf(string act)
        {
            switch (act)
            {
                case "cameraChanged": return OnCameraSelected;
                case "excludeThis": return OnExcludeThisClicked;
                case "excludePattern": return OnExcludePatternClicked;
                case "exportOriginal": return OnExportOriginalClicked;
                case "markReplace": return OnMarkReplaceClicked;
                case "clearSelection": return OnCancelClicked;
                case "stop": return OnStopClicked;
                default: return null;
            }
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
            _imagePreview.Visible = isImage;
            _spriteInfoLabel.Visible = isImage;
            _textEditRow.Visible = false; // Shown only after clicking a text

            // The picker already rebuilt its camera list in Start(); just mirror it into the dropdown.
            _cameraDropdown.SetOptions(_picker.CameraNames);
            _cameraDropdown.SelectedValue = "UI Only";
        }

        private void OnCameraSelected()
        {
            int index = Array.IndexOf(_picker.CameraNames, _cameraDropdown.SelectedValue);
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
                    // rule, run on this panel's VisibilityChanged (reported on the request, before
                    // UniverseLib's deferred close), so a hotkey close counts too and the Main is
                    // back BEFORE OnStopClicked reopens Translation Tools on top of it.
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
                // The picture first, its name under it: one is recognised, the other is read — and
                // the name is what the replacement rule is keyed on, so it stays.
                //
                // ⚠ The fact "there is no image here" is told ONCE, by the box, which is where the
                // eye already is. It used to be said by this label instead ("No named image on this
                // element."), which left two wordings of one fact three centimetres apart the day a
                // box arrived above it.
                if (target.HasSprite)
                {
                    ShowChosenImage(target.SpriteObject, target.SpriteComponentType,
                                    target.SpriteName, target.SpriteWidth, target.SpriteHeight);
                    BuildSlotStrip(target);
                }
                else
                {
                    // An element can draw a bare shape with no picture at all: there is nothing to
                    // show and nothing to name.
                    _imagePreview.Explain("No image on this element.");
                    _spriteInfoLabel.Show("");
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
            _imagePreview.Clear();
            _slotStrip.Clear();
            _slotStrip.Visible = false;
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

        /// <summary>
        /// Put one of the object's pictures under the eye, and make it the one the verbs act on.
        ///
        /// 🔴 **The picture first, its name under it.** One is recognised at a glance, the other has
        /// to be read — and both are needed: the name is the key the replacement rule is written on,
        /// so it is what says the rule will hold on every object showing this same texture.
        /// </summary>
        private void ShowChosenImage(object image, string componentType, string name, int width, int height)
        {
            _lastSelectedSpriteObj = image;
            _imagePreview.Show(image);
            _spriteInfoLabel.Show($"{componentType}: \"{name}\" ({width}x{height})");
            _spriteInfoLabel.Tone = Tone.Plain;
        }

        /// <summary>
        /// The row of pictures this object carries, when it carries more than one.
        ///
        /// 🔴 **Thumbnails, not a list of slot names.** A picture is chosen by seeing it; nobody
        /// picks between "_MainTex" and "_EmissionMap" by reading them. The slot stays as a caption
        /// under each one, because it answers the question the choice actually asks — which face of
        /// the object is this — and the full name and size of whichever is chosen stay on the line
        /// below, told once.
        ///
        /// ⚠ Hidden outright for the ordinary object with a single picture: a strip of one is a
        /// choice that is not one, and it would push the verbs down for nothing.
        /// </summary>
        private void BuildSlotStrip(PickedTarget target)
        {
            _slotStrip.Clear();
            if (target.Images.Count < 2)
            {
                _slotStrip.Visible = false;
                return;
            }

            foreach (var carried in target.Images)
            {
                var each = carried;                       // captured per thumbnail, not per loop
                var choice = _screen.Instantiate("SlotChoice", _slotStrip,
                    act => act == "pickSlot"
                        ? (Action)(() => ShowChosenImage(each.Image, target.SpriteComponentType,
                                                         each.Name, each.Width, each.Height))
                        : null);
                choice.Picture("SlotImage").Show(each.Image);
                choice.Say("slotName", each.Slot);
            }

            _slotStrip.Visible = true;
        }

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

            // Exporting writes the rule too — the file it saves IS the one the game will load back,
            // so there would be nothing to load it into otherwise.
            //
            // ⚠ **And it SAYS so.** Reported by somebody who could no longer remember whether he had
            // pressed Mark for Replace: the rule had appeared in the list on its own. Since the 3D
            // textures it reaches further still — every object sharing that texture — so a silent
            // one is worse than it was. Told rather than made deliberate: the act is right, only
            // its silence was wrong, and a second click to confirm what the first already implies
            // is a step that teaches nothing.
            bool marked = !ImageReplacer.GetAll().ContainsKey(spriteName);
            if (marked) MarkCurrentForReplace(spriteName);

            var exportedPath = ImageReplacer.ExportOriginal(_lastSelectedSpriteObj, spriteName);
            if (exportedPath != null)
            {
                _statusLabel.Show((marked ? Tr("Exported and marked for replacement:") : Tr("Exported:"))
                                  + $" {System.IO.Path.GetFileName(exportedPath)}");
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
            // One editable line, from the document's template. Its three verbs are answered by
            // handlers written below, once everything they reach exists.
            //
            // 🔴 The tag as a CHIP, not as `[H] ` in front of the key: written into the key's own
            // label it was grey text among grey text — the one thing on the row that carries a
            // colour everyone has already learnt on the site's tables, and it carried none. The key
            // itself renders its markup literally (richText false in the document): what is edited
            // here is the file's exact text, and `<color=…>` has to be seen to be preserved. The
            // rendering is shown separately, right under the field.
            Action save = null, retranslate = null, revert = null;
            var row = _screen.Instantiate("TextEditEntry", _textEditList.Rows, act =>
            {
                switch (act)
                {
                    case "save": return () => save?.Invoke();
                    case "retranslate": return () => retranslate?.Invoke();
                    case "revert": return () => revert?.Invoke();
                    default: return null;
                }
            });

            var tagChip = row.Chip("Tag");
            tagChip.Retag(entry.tag);
            var keyLabel = row.Label("Key");
            row.Say("key", entry.originalKey);

            // Live values of the [!v*N] placeholders, as currently displayed in-game
            if (entry.liveNumbers != null && entry.liveNumbers.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in entry.liveNumbers)
                    parts.Add($"[!v*{kv.Key}] = {kv.Value}");
                row.Say("liveValues", $"Keep placeholders as-is. Current values: {string.Join("   ", parts)}");
                row.Label("LiveValues").Visible = true;
            }

            // Editable translation field — raw text, markup included, exactly as the file holds it
            var input = row.Field("TranslationInput");
            input.Text = entry.text;

            // …and right under it, the same string RENDERED. One shows what you are editing, the
            // other what the game will draw — a colour tag broken while typing shows up here
            // immediately, instead of on a screen you have to go back to.
            var previewLabel = row.Label("Preview");

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

            var saveBtn = row.Button("SaveBtn");
            var retranslateBtn = row.Button("RetranslateBtn");
            var revertBtn = row.Button("RevertBtn");

            rowState.SaveBtn = saveBtn;
            rowState.RetranslateBtn = retranslateBtn;
            rowState.RevertBtn = revertBtn;

            // Both buttons exist before either handler is written: each one has to be able to put
            // the other back in its right state, and a lambda cannot reach a local declared later.
            save = () =>
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

            retranslate = () =>
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

            revert = () =>
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
            //
            // ⚠ **And it is judged on what the field holds, not on whether it was touched**
            // (2026-09-19). A line saved earlier with a placeholder dropped used to say nothing
            // until somebody retyped it, so the one moment it mattered — coming back to check —
            // was the one moment it stayed quiet. The site marks such a row on sight; this is the
            // same rule reaching the same verdict. Save and Revert still key off `changed`: a row
            // nobody edited has nothing to save or put back, broken or not.
            string problem = EditChecks.Problem(row.Key, field);

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
                if (EditChecks.Show(row.PreviewLabel, problem)) return;

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
