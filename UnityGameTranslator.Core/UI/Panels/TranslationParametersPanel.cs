using System;
using System.Collections.Generic;
using System.Linq;
using UniverseLib.UI;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Translation parameters panel with Exclusions, Fonts, Images, and Variables tabs.
    /// Extracted from OptionsPanel to keep options focused on general settings.
    ///
    /// ⚠ Migrated to the UI vocabulary 2026-09-08 (see
    /// analyse/inventaire-couches/brief-migration-panneau.md): the panel now holds handles
    /// (Host, LabelHandle, ButtonHandle, FieldHandle, ToggleHandle, SliderHandle) and talks to
    /// the Stacks/Labels/Buttons/Fields/CheckBoxes/Sliders/ScrollList/Callout factories — it
    /// names nothing of Unity or UniverseLib. See the migration report for what moved and why.
    /// </summary>
    public class TranslationParametersPanel : TranslatorPanelBase
    {
        public override string Name => "Translation Tools";
        public override int MinWidth => 580;
        public override int MinHeight => 400;
        public override int PanelWidth => 600;
        public override int PanelHeight => 520;

        protected override int MinPanelHeight => 400;

        // Every tab here holds a scrollable list (exclusions, fonts, overrides, images, vars)
        // that should grow when the user enlarges the panel.
        protected override bool HasFlexibleContent => true;

        // Tab system
        private TabBar _tabBar;
        private Components.HelpZone _helpZone;

        // Behavior section
        private ToggleHandle _typewritingDetectionToggle;
        private ToggleHandle _concatDetectionToggle;

        // Exclusions section
        private ScrollList _exclusionsList;
        private FieldHandle _manualPatternInput;
        private LabelHandle _exclusionsStatus;

        // Find by value
        private FieldHandle _findByValueInput;
        private ScrollList _findResultsList;

        // Pending exclusion changes
        private HashSet<string> _pendingExclusionAdds = new HashSet<string>();
        private HashSet<string> _pendingExclusionRemoves = new HashSet<string>();
        private HashSet<string> _initialExclusions = new HashSet<string>();

        // Fonts section
        private ScrollList _fontsList;
        private LabelHandle _fontsStatus;
        private ToggleHandle _enableFontReplacementToggle;
        private string[] _systemFonts;
        private List<SearchableDropdown> _fallbackDropdowns = new List<SearchableDropdown>();
        // Same lifecycle rule as the fallback dropdowns: a SearchableDropdown owns a popup
        // outside the row, so destroying the row is not enough.
        private List<SearchableDropdown> _overrideRtlDropdowns = new List<SearchableDropdown>();
        private SearchableDropdown _fontAtlasSizeDropdown;
        private int _pendingAtlasSize;  // font sharpness selection, applied on Apply

        // Pending font changes (fontName -> (enabled, fallback, sizePercent, scaleAuto, mirrorRtl)).
        // sizePercent = the deliberate size slider (orthogonal to the auto design-scale baseline);
        // scaleAuto = whether the design-scale is folded in. The two combine multiplicatively.
        private Dictionary<string, (bool enabled, string fallback, float sizePercent, bool scaleAuto, bool mirrorRtl)> _pendingFontSettings = new Dictionary<string, (bool, string, float, bool, bool)>();
        private Dictionary<string, (bool enabled, string fallback, float sizePercent, bool scaleAuto, bool mirrorRtl)> _initialFontSettings = new Dictionary<string, (bool, string, float, bool, bool)>();

        // Whether the RTL controls (per-font mirror toggle, per-rule alignment) are shown at all:
        // only when this game's translation involves right-to-left text in either direction —
        // they are noise for everyone else (user-arbitrated). Recomputed at each list refresh.
        private bool _rtlControlsVisible;

        // Images section
        private ScrollList _imagesList;
        private LabelHandle _imagesStatus;
        private ToggleHandle _enableImageReplacementToggle;

        // Variables section
        private ScrollList _variablesList;
        private LabelHandle _variablesStatus;
        private FieldHandle _scanValueInput;
        private ScrollList _scanResultsList;
        private bool _isScanning;

        // Apply button tracking
        private ButtonHandle _applyBtn;

        // Tools tab — browser editor (live edit session)
        private ButtonHandle _browserEditorBtn;
        private LabelHandle _browserEditorStatus;
        // True while a start or stop round trip is in flight (see OnBrowserEditorClicked)
        private bool _browserEditorBusy;

        // Font highlight tracking
        private string _highlightedFontName = null;
        private ButtonHandle _highlightedButton = null;

        public TranslationParametersPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            Layout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            // Fixed header: tab buttons stay put, only tab content scrolls
            var header = FixedHeader();

            // No big title here — the window title bar already shows "Translation Tools" (redundant).

            // Create tab bar — buttons in the fixed header, contents in the scroll area
            _tabBar = new TabBar();
            _tabBar.CreateUI(header, scrollContent);

            // Create tab contents
            var behaviorTab = _tabBar.Tab("Tools");
            var exclusionsTab = _tabBar.Tab("Exclusions");
            var fontsTab = _tabBar.Tab("Fonts");
            var imagesTab = _tabBar.Tab("Images");
            var variablesTab = _tabBar.Tab("Variables");

            // The TabBar registers its own tab labels for localization now — nothing left to do here.

            // Explain what lives behind each tab
            _helpZone?.Describe(_tabBar.Button("Tools"),
                "Text editors (in-game and browser) and text detection settings");
            _helpZone?.Describe(_tabBar.Button("Exclusions"),
                "Prevent specific texts or UI elements from being translated");
            _helpZone?.Describe(_tabBar.Button("Fonts"),
                "Replace the game's fonts when they can't display your language's characters");
            _helpZone?.Describe(_tabBar.Button("Images"),
                "Replace images that contain baked-in text");
            _helpZone?.Describe(_tabBar.Button("Variables"),
                "Protect dynamic values (numbers, names) inside translated texts");

            // Host for the Fonts sub-tab buttons: they belong to the chrome, not to the content,
            // so they sit in the fixed header like the main tabs instead of scrolling away with
            // the settings they switch between. Shown only while the Fonts tab is open.
            _fontsSubTabHost = Stacks.Vertical(header, "FontsSubTabHost");
            _tabBar.OnTabChanged += (_, tabName) => _fontsSubTabHost.Visible = tabName == "Fonts";
            _fontsSubTabHost.Visible = _tabBar.SelectedName == "Fonts";

            // Build each tab's content
            CreateBehaviorTabContent(behaviorTab);
            CreateExclusionsTabContent(exclusionsTab);
            CreateFontsTabContent(fontsTab);
            CreateImagesTabContent(imagesTab);
            CreateVariablesTabContent(variablesTab);

            // Clear font highlight when leaving the Fonts tab
            _tabBar.OnTabChanged += (index, name) =>
            {
                if (name != "Fonts")
                {
                    TranslatorScanner.ClearHighlight();
                    ResetHighlightButton();
                }
            };

            // Buttons - in fixed footer (outside scroll)
            var cancelBtn = Buttons.Secondary(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.Clicked += () => SetActive(false);

            _applyBtn = Buttons.Primary(buttonRow, "ApplyBtn", "Apply", policy: TextPolicy.Dynamic);
            _applyBtn.Clicked += OnApplyClicked;
            // Dynamic: code-managed text ("Apply"/"Close"/"Apply (N)") — async translation would
            // race with UpdateApplyButtonText and break the button. Static labels stay UiText.

            RegisterPendingFields();
        }

        /// <summary>
        /// The fields built once with the panel, registered once (see PendingMarks and the
        /// panel base). The lists — fonts, exclusions, rules — register their rows each time
        /// they are rebuilt, under a group of their own.
        /// </summary>
        private void RegisterPendingFields()
        {
            Pending.Track(_typewritingDetectionToggle, () => _typewritingDetectionToggle.IsOn != TranslatorCore.TypewritingDetection);
            Pending.Track(_concatDetectionToggle, () => _concatDetectionToggle.IsOn != TranslatorCore.ConcatDetection);
            Pending.Track(_enableFontReplacementToggle, () => _enableFontReplacementToggle.IsOn != TranslatorCore.Config.enable_font_replacement);
            Pending.Track(_enableImageReplacementToggle, () => _enableImageReplacementToggle.IsOn != TranslatorCore.Config.enable_image_replacement);
            Pending.Track(_fontAtlasSizeDropdown?.Handle, () => _pendingAtlasSize != TranslatorCore.Config.max_font_atlas_size);
        }

        #region Tools Tab (formerly Behavior)

        private void CreateBehaviorTabContent(Host parent)
        {
            var card = Stacks.Card(parent, "ToolsCard", PanelWidth - 60, stretchVertically: true);

            // Text Editor section
            Labels.Create(card, "TextEditorLabel", "Text Editor", TextRole.SectionTitle);

            Labels.Create(card, "TextEditorHint",
                "Click on any text in-game to edit its translation or retranslate it with AI.", TextRole.Hint);

            var editorBtn = Buttons.Primary(card, "TextEditorBtn", "Start Text Editor", PanelWidth - 100,
                scope: EditSide.Local);
            editorBtn.Clicked += OnStartTextEditorClicked;
            // L'éditeur en jeu écrit le fichier d'ici, comme l'éditeur navigateur juste dessous.
            _helpZone?.Describe(editorBtn,
                "Pick any text on screen to fix its translation without leaving the game");

            Stacks.Spacer(card, 15);

            // Browser Editor section (live edit session on the website, no account needed)
            Labels.Create(card, "BrowserEditorLabel", "Browser Editor", TextRole.SectionTitle);

            Labels.Create(card, "BrowserEditorHint",
                "Edit your translation file comfortably in your browser while playing — no account needed, nothing is published. Each save is applied in-game automatically.",
                TextRole.Hint);

            _browserEditorBtn = Buttons.Primary(card, "BrowserEditorBtn", "Edit in browser", PanelWidth - 100,
                scope: EditSide.Local, policy: TextPolicy.Dynamic);
            _browserEditorBtn.Clicked += OnBrowserEditorClicked;
            // Éditer dans le navigateur ne change que le fichier d'ici — rien n'est publié.
            _helpZone?.Describe(_browserEditorBtn,
                "Open your translation in a browser editor: search, filters, and every save applied in-game live");

            _browserEditorStatus = Labels.Create(card, "BrowserEditorStatus", "", TextRole.Hint,
                policy: TextPolicy.Excluded);
            RefreshBrowserEditorUI();

            Stacks.Spacer(card, 15);

            // Detection section
            Labels.Create(card, "DetectionLabel", "Detection", TextRole.SectionTitle);

            Labels.Create(card, "DetectionHint",
                "Control how the mod detects special text patterns. Disable if causing issues with your game.",
                TextRole.Hint);

            // Typewriting detection toggle
            _typewritingDetectionToggle = CheckBoxes.Create(card, "TypewritingToggle", "Typewriting detection",
                TranslatorCore.TypewritingDetection, _ => UpdateApplyButtonText());
            _helpZone?.Describe(_typewritingDetectionToggle,
                "Detects text that appears letter by letter, like dialogues, and waits for it to settle before translating. Disable if it causes issues.");

            Labels.Create(card, "TypewritingHint",
                "Text that appears letter by letter (dialogues, cutscenes). Waits for the text to stabilize before translating.",
                TextRole.Hint);

            // Concat detection toggle
            _concatDetectionToggle = CheckBoxes.Create(card, "ConcatToggle", "Procedural text detection",
                TranslatorCore.ConcatDetection, _ => UpdateApplyButtonText());
            _helpZone?.Describe(_concatDetectionToggle,
                "Detects text assembled in parts, like tooltips or item stats, and translates each part for better cache reuse. Disable if it causes issues.");

            Labels.Create(card, "ConcatHint",
                "Text built in multiple steps (tooltips, item stats). Translates each part separately for better cache reuse.",
                TextRole.Hint);
        }

        private void OnStartTextEditorClicked()
        {
            SetActive(false);
            Intents.OpenInspector(InspectorMode.TextEdit);
        }

        private void OnBrowserEditorClicked()
        {
            // Repeat clicks are the normal reaction here, not user error: the init
            // uploads the whole translation file (megabytes on a large game) and the
            // browser usually opens BEHIND a fullscreen window, so nothing seems to
            // happen for several seconds. Every extra click used to create one more
            // server-side session, one more tab, and burn one of the six init calls
            // allowed per minute — the last ones failing with a raw 429.
            if (_browserEditorBusy) return;

            if (TranslatorUIManager.IsEditSessionActive)
            {
                // Ends server-side too (deletes the session file) and
                // calls back OnEditSessionEnded to refresh this UI
                SetBrowserEditorBusy(true, "Stopping...");
                TranslatorUIManager.EndEditSessionFromMod("Session stopped.");
                return;
            }

            BeginBrowserEditorSession();
        }

        /// <summary>
        /// Called by TranslatorUIManager when the session ends outside a
        /// direct click (browser closed past grace, ended from the page,
        /// expired server-side, or the Stop button round trip).
        /// </summary>
        public void OnEditSessionEnded(string reason)
        {
            SetBrowserEditorStatus(reason, Tone.Muted);
            // Also the end of the "Stopping..." round trip when the user clicked Stop
            SetBrowserEditorBusy(false);
        }

        /// <summary>
        /// Called by TranslatorUIManager when a session left open by a previous
        /// run is picked back up at startup. The button must say "Stop", not
        /// "Edit in browser": the session is live and the page is likely still
        /// open — offering to start a second one would strand the first.
        /// </summary>
        public void OnEditSessionResumed()
        {
            SetBrowserEditorStatus(
                "Session resumed — your browser tab is connected again.",
                Tone.Success);
            SetBrowserEditorBusy(false);
        }

        /// <summary>
        /// Clicked "Edit in browser": make sure nobody else is already editing this translation,
        /// then open the session.
        ///
        /// ⚠ The question comes BEFORE anything is uploaded. Asking after would mean the file has
        /// already travelled and a second session already exists on the site — which is the state
        /// this exists to prevent.
        /// </summary>
        private async void BeginBrowserEditorSession()
        {
            SetBrowserEditorBusy(true, "Checking...");

            var blocking = await TranslatorUIManager.FindBlockingEditSession();

            TranslatorUIManager.RunOnMainThread(() =>
            {
                if (blocking == null)
                {
                    StartBrowserEditorSession();
                    return;
                }

                if (!Intents.CanConfirm())
                {
                    // No way to ask is not a licence to decide: the safe answer is to do nothing
                    // and say why, rather than erase a session somebody may be typing in.
                    SetBrowserEditorStatus(blocking.Question, Tone.Warning);
                    SetBrowserEditorBusy(false);
                    return;
                }

                Intents.Confirm(
                    "Already being edited",
                    blocking.Question,
                    blocking.ModKey != null ? "End it and open mine" : "Open mine anyway",
                    () => TakeOverAndStart(blocking.ModKey),
                    () => SetBrowserEditorBusy(false),
                    isDanger: true);
            });
        }

        private async void TakeOverAndStart(string modKey)
        {
            // Null when the other session belongs to another account of this computer: we cannot
            // end it, and the player has chosen to open theirs regardless.
            if (modKey != null)
                await TranslatorUIManager.TakeOverEditSession(modKey);

            TranslatorUIManager.RunOnMainThread(StartBrowserEditorSession);
        }

        private async void StartBrowserEditorSession()
        {
            // Says what is actually slow (the upload) and where the tab will show
            // up, so the wait is understood instead of read as a dead button
            SetBrowserEditorBusy(true, "Opening...");
            SetBrowserEditorStatus(
                "Sending your translation file... your browser opens when it is ready (the tab may appear behind the game).",
                Tone.Secondary);

            try
            {
                // Close whatever a previous run left behind first. Starting a second
                // session while the first is still alive server-side would strand it
                // until it expires — and those slots are shared by every user.
                await TranslatorUIManager.DiscardPersistedEditSession();

                // Flush in-memory changes so the browser edits the current state
                TranslatorCore.SaveCache();

                var result = await ApiClient.InitEditSession();

                // Capture values before RunOnMainThread (IL2CPP safety)
                var success = result.Success;
                var error = result.Error;
                var modKey = result.ModKey;
                var url = result.Url;

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (!success || string.IsNullOrEmpty(modKey) || string.IsNullOrEmpty(url))
                    {
                        SetBrowserEditorStatus($"Failed to start: {error ?? "unknown error"}", Tone.Error);
                        SetBrowserEditorBusy(false);
                        return;
                    }

                    string fullUrl = ApiClient.GetMergePreviewFullUrl(url);
                    TranslatorCore.OpenUrlSafe(fullUrl);

                    TranslatorUIManager.StartEditSessionListener(modKey);
                    SetBrowserEditorStatus("Session active — edit in your browser, each save is applied in-game.", Tone.Success);
                    SetBrowserEditorBusy(false);
                });
            }
            catch (Exception e)
            {
                // Never leave the button stuck: an exception here (network, disk)
                // would otherwise lock the only way to open the editor
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    SetBrowserEditorStatus($"Failed to start: {errorMsg}", Tone.Error);
                    SetBrowserEditorBusy(false);
                });
            }
        }

        private void SetBrowserEditorStatus(string message, Tone tone)
        {
            if (_browserEditorStatus == null) return;
            _browserEditorStatus.Show(message);
            _browserEditorStatus.Tone = tone;
        }

        /// <summary>
        /// Locks the button for the whole duration of a start or stop round trip.
        /// Releasing it always goes through here, so the button can never stay
        /// dead after a failure.
        /// </summary>
        /// <param name="busyLabel">Label shown while locked; ignored when releasing.</param>
        private void SetBrowserEditorBusy(bool busy, string busyLabel = null)
        {
            _browserEditorBusy = busy;

            if (busy)
            {
                if (busyLabel != null) _browserEditorBtn?.Busy(busyLabel);
                else if (_browserEditorBtn != null) _browserEditorBtn.Enabled = false;
                return;
            }

            if (_browserEditorBtn != null) _browserEditorBtn.Enabled = true;
            RefreshBrowserEditorUI();
        }

        private void RefreshBrowserEditorUI()
        {
            if (_browserEditorBtn != null)
            {
                _browserEditorBtn.Label = TranslatorUIManager.IsEditSessionActive
                    ? "Stop browser session"
                    : "Edit in browser";
            }
        }

        #endregion

        #region Exclusions Tab

        private void CreateExclusionsTabContent(Host parent)
        {
            var card = Stacks.Card(parent, "ExclusionsCard", PanelWidth - 60, stretchVertically: true);

            // Header and explanation
            Labels.Create(card, "ExclusionsLabel", "UI Exclusions", TextRole.SectionTitle);

            Labels.Create(card, "ExclusionsHint",
                "Exclude UI elements from translation (chat windows, player names, etc.). Exclusions are shared when you upload your translation.",
                TextRole.Hint);

            Stacks.Spacer(card, 10);

            // Inspector button
            var inspectorBtn = Buttons.Primary(card, "InspectorBtn", "Start Inspector Mode", PanelWidth - 100);
            inspectorBtn.Clicked += OnStartInspectorClicked;
            _helpZone?.Describe(inspectorBtn,
                "Closes this panel so you can click UI elements in-game to exclude them from translation.");

            Labels.Create(card, "InspectorHint", "Click on UI elements to exclude them", TextRole.Hint);

            Stacks.Spacer(card, 10);

            // Manual add section
            Labels.Create(card, "ManualLabel", "Add pattern manually:", TextRole.Small);

            var addRow = Stacks.Row(card, "AddRow", spacing: 5, minHeight: UIStyles.InputHeight);

            _manualPatternInput = Fields.Create(addRow, "PatternInput", "e.g., **/ChatPanel/**");
            _helpZone?.Describe(_manualPatternInput,
                "Type a hierarchy path pattern to exclude. Use ** for any depth and * for a single level.");

            var addBtn = Buttons.Secondary(addRow, "AddBtn", "Add", 60);
            addBtn.Clicked += OnAddManualPatternClicked;
            _helpZone?.Describe(addBtn,
                "Adds the typed pattern to the exclusion list. Takes effect on Apply.");

            Labels.Create(card, "PatternHint", "Use ** for any depth, * for single level", TextRole.Hint);

            Stacks.Spacer(card, 10);

            // Find by value section
            Labels.Create(card, "FindLabel", "Find by text content:", TextRole.Small);

            var findRow = Stacks.Row(card, "FindRow", spacing: 5, minHeight: UIStyles.InputHeight);

            _findByValueInput = Fields.Create(findRow, "FindValueInput", "Enter text visible in-game...");
            _helpZone?.Describe(_findByValueInput,
                "Type text visible in-game to locate the UI element that shows it, then exclude it.");

            var findBtn = Buttons.Secondary(findRow, "FindBtn", "Find", 60);
            findBtn.Clicked += OnFindByValueClicked;
            _helpZone?.Describe(findBtn,
                "Searches the scene for UI components displaying the entered text.");

            Labels.Create(card, "FindHint", "Find which UI component displays this text, then exclude it", TextRole.Hint);

            // Find results (hidden until search)
            _findResultsList = ScrollList.Create(card, "FindResultsScroll", minHeight: 0, preferredHeight: 80,
                fillHeight: false, emptyText: "No UI component found with this text.");
            _findResultsList.Visible = false;

            Stacks.Spacer(card, 10);

            // Current exclusions list
            Labels.Create(card, "ListLabel", "Current Exclusions:", TextRole.Small);

            // Scrollable container for exclusions
            _exclusionsList = ScrollList.Create(card, "ExclusionsScroll", minHeight: 200, preferredHeight: 200,
                emptyText: "No exclusions defined");

            // Status label
            _exclusionsStatus = Labels.Create(card, "ExclusionsStatus", "", TextRole.Small, policy: TextPolicy.Dynamic);
        }

        /// <summary>
        /// Open panel and switch directly to the Exclusions tab.
        /// Called when returning from InspectorPanel.
        /// </summary>
        public void OpenOnExclusionsTab()
        {
            SetActive(true);
            _tabBar?.SelectTab("Exclusions");
        }

        public void OpenOnBitmapReplaceTab()
        {
            SetActive(true);
            _tabBar?.SelectTab("Images");
            RefreshImageReplacementsList();
        }

        public void OpenOnToolsTab()
        {
            SetActive(true);
            _tabBar?.SelectTab("Tools");
            Window.ToBack();
        }

        public void OpenOnFontOverridesTab()
        {
            SetActive(true);
            _tabBar?.SelectTab("Fonts");
            _fontsSubTabBar?.SelectTab("Overrides");
            RefreshFontOverridesList();
            Window.ToBack();
        }

        private void OnStartInspectorClicked()
        {
            // Close panel and open inspector panel (exclusion mode)
            SetActive(false);
            Intents.OpenInspector();
        }

        private void OnStartImageInspectorClicked()
        {
            // Close panel and open inspector panel (image replacement mode)
            SetActive(false);
            Intents.OpenInspector(InspectorMode.BitmapReplace);
        }

        private void OnAddManualPatternClicked()
        {
            string pattern = _manualPatternInput.Text?.Trim();

            if (string.IsNullOrEmpty(pattern))
            {
                _exclusionsStatus.Say("Enter a pattern first");
                _exclusionsStatus.Tone = Tone.Warning;
                return;
            }

            // Check if already exists (in current list or pending adds)
            bool alreadyExists = TranslatorCore.UserExclusions.Contains(pattern) ||
                                 _pendingExclusionAdds.Contains(pattern);
            bool wasRemoved = _pendingExclusionRemoves.Contains(pattern);

            if (alreadyExists && !wasRemoved)
            {
                _exclusionsStatus.Say("Pattern already exists");
                _exclusionsStatus.Tone = Tone.Warning;
                return;
            }

            // If it was pending removal, just cancel the removal
            if (wasRemoved)
            {
                _pendingExclusionRemoves.Remove(pattern);
            }
            else
            {
                _pendingExclusionAdds.Add(pattern);
            }

            _manualPatternInput.Text = "";
            _exclusionsStatus.Say("Pattern will be added on Apply");
            _exclusionsStatus.Tone = Tone.Secondary;

            RefreshExclusionsList();
            UpdateApplyButtonText();
        }

        private void OnFindByValueClicked()
        {
            string searchValue = _findByValueInput?.Text?.Trim();
            if (string.IsNullOrEmpty(searchValue))
            {
                _exclusionsStatus.Say("Enter text to search for");
                _exclusionsStatus.Tone = Tone.Warning;
                return;
            }

            // Show results container
            _findResultsList.Visible = true;

            // Clear previous results
            _findResultsList.Clear();

            // 🔴 One enumeration for every framework, not a list of type names written here.
            // This used to name UI.Text and TMP_Text in its own code, so a player on an NGUI, tk2d,
            // TMProOld, TextMesh or UI Toolkit game could see their text translated and never find
            // it from this screen — while the scanner knew about it all along.
            // See analyse/text-targets-audit.md.
            var found = new List<KeyValuePair<string, string>>();
            var seenPaths = new HashSet<string>();

            foreach (var target in TextTargets.All(text => text.Contains(searchValue)))
            {
                string path = target.Path;
                if (string.IsNullOrEmpty(path) || !seenPaths.Add(path)) continue;

                // The framework, not a snippet of the text: the text is what was searched for, so
                // showing it back says nothing. What the reader cannot guess is why one path looks
                // like a GameObject hierarchy and the next like a list of USS classes.
                found.Add(new KeyValuePair<string, string>(path, target.Engine));
            }

            if (found.Count == 0)
            {
                _exclusionsStatus.Say("No results");
                _exclusionsStatus.Tone = Tone.Warning;
                return;
            }

            foreach (var kvp in found)
            {
                var row = Stacks.Horizontal(_findResultsList.Rows, "FindResult", spacing: 5, pad: Pad.All(2),
                    minHeight: UIStyles.RowHeightSmall);

                Labels.Create(row, "Path", kvp.Key, TextRole.Small, fill: Fill.Stretch);

                // Which framework drew it. A UI Toolkit path is a list of USS classes and reads
                // nothing like a GameObject hierarchy — without this, one of the two looks broken.
                Labels.Create(row, "Engine", kvp.Value, TextRole.Caption, policy: TextPolicy.Excluded,
                    wrap: false, minWidth: 60, align: Placement.MiddleRight);

                var capturedPath = kvp.Key;
                var excludeBtn = Buttons.Secondary(row, "Exclude", "+", 30);
                excludeBtn.Clicked += () =>
                {
                    if (!_pendingExclusionAdds.Contains(capturedPath))
                    {
                        _pendingExclusionAdds.Add(capturedPath);
                        RefreshExclusionsList();
                        UpdateApplyButtonText();
                        _exclusionsStatus.Show(Tr("Added:") + $" {capturedPath}");
                        _exclusionsStatus.Tone = Tone.Success;
                    }
                };
            }

            _findResultsList.Filled();
            _exclusionsStatus.Say($"Found {found.Count} component(s)");
            _exclusionsStatus.Tone = Tone.Success;
        }

        private void RefreshExclusionsList()
        {
            if (_exclusionsList == null) return;
            Pending.ClearGroup("exclusions");
            _exclusionsList.Clear();

            // Build effective list: current - pending removes + pending adds
            var effectiveExclusions = new List<(string pattern, bool isPending, bool isRemoved)>();

            // Add current exclusions (mark removed ones)
            foreach (var pattern in TranslatorCore.UserExclusions)
            {
                bool isRemoved = _pendingExclusionRemoves.Contains(pattern);
                effectiveExclusions.Add((pattern, false, isRemoved));
            }

            // Add pending additions
            foreach (var pattern in _pendingExclusionAdds)
            {
                effectiveExclusions.Add((pattern, true, false));
            }

            if (effectiveExclusions.Count == 0) return;

            foreach (var (pattern, isPending, isRemoved) in effectiveExclusions)
            {
                var row = Stacks.Row(_exclusionsList.Rows, $"Row_{pattern.GetHashCode()}", spacing: 5,
                    minHeight: UIStyles.RowHeightNormal);

                // What this row is waiting for is said by the shared mark (green added, red
                // removed) and, for a removal, in words — the row stays until Apply so the
                // removal can be seen and undone, exactly like a change to any other field.
                Labels.Create(row, "PatternLabel", pattern, TextRole.Small,
                    tone: isRemoved ? Tone.Muted : Tone.Plain, policy: TextPolicy.Excluded, fill: Fill.Stretch);

                var state = isPending ? PendingState.Added : isRemoved ? PendingState.Removed : PendingState.None;
                Pending.TrackState(row, () => state, "exclusions");

                var capturedPattern = pattern;
                if (isRemoved)
                {
                    Labels.Create(row, "RemovedLabel", "Removed on Apply", TextRole.Small, tone: Tone.Error,
                        minWidth: 110, align: Placement.MiddleRight);

                    var undoBtn = Buttons.Secondary(row, "UndoBtn", "Undo", 50);
                    undoBtn.Clicked += () =>
                    {
                        _pendingExclusionRemoves.Remove(capturedPattern);
                        RefreshExclusionsList();
                        UpdateApplyButtonText();
                    };
                }
                else
                {
                    var deleteBtn = Buttons.Secondary(row, "DeleteBtn", "X", 30);
                    deleteBtn.Clicked += () => OnDeleteExclusionClicked(capturedPattern);
                }
            }

            _exclusionsList.Filled();
        }

        private void OnDeleteExclusionClicked(string pattern)
        {
            // If it was a pending add, just remove from pending
            if (_pendingExclusionAdds.Contains(pattern))
            {
                _pendingExclusionAdds.Remove(pattern);
                _exclusionsStatus.Say("Pending pattern cancelled");
                _exclusionsStatus.Tone = Tone.Secondary;
            }
            else
            {
                // Mark for removal on Apply
                _pendingExclusionRemoves.Add(pattern);
                _exclusionsStatus.Say("Pattern will be removed on Apply");
                _exclusionsStatus.Tone = Tone.Secondary;
            }

            RefreshExclusionsList();
            UpdateApplyButtonText();
        }

        #endregion

        #region Fonts Tab

        // Font overrides UI
        private ScrollList _fontOverridesList;
        private TabBar _fontsSubTabBar;
        private Host _fontsSubTabHost;

        private void CreateFontsTabContent(Host parent)
        {
            // Sub-tab buttons in the fixed header, their contents in the scrolling tab body:
            // the scrollbar then covers the settings only, not the switcher above them.
            _fontsSubTabBar = new TabBar();
            _fontsSubTabBar.CreateUI(_fontsSubTabHost, parent, tabRowHeight: 26); // Compact height for sub-tabs

            var globalTab = _fontsSubTabBar.Tab("Global");
            var overridesTab = _fontsSubTabBar.Tab("Overrides");

            _helpZone?.Describe(_fontsSubTabBar.Button("Global"),
                "Global font settings for every detected font, including fallbacks and sharpness.");
            _helpZone?.Describe(_fontsSubTabBar.Button("Overrides"),
                "Per-element rules that override the font size for specific UI elements.");

            CreateFontsGlobalSubTab(globalTab);
            CreateFontsOverridesSubTab(overridesTab);
        }

        private void CreateFontsGlobalSubTab(Host parent)
        {
            var card = Stacks.Card(parent, "FontsCard", PanelWidth - 60, stretchVertically: true);

            // Header and explanation
            Labels.Create(card, "FontsLabel", "Font Management", TextRole.SectionTitle);

            Labels.Create(card, "FontsHint",
                "Configure translation for detected fonts. Add fallback fonts for non-Latin scripts (Hindi, Arabic, Chinese, etc.). Settings are saved with translations.",
                TextRole.Hint);

            Stacks.Spacer(card, 5);

            // Debug toggle: globally disable font replacement (for translators).
            _enableFontReplacementToggle = CheckBoxes.Create(card, "EnableFontReplacementToggle",
                "Enable font replacement (uncheck to debug with original fonts)",
                TranslatorCore.Config.enable_font_replacement, OnEnableFontReplacementChanged);
            _helpZone?.Describe(_enableFontReplacementToggle,
                "Replaces game fonts so your language's characters display correctly. Uncheck to debug with the original fonts.");

            // Font sharpness = max SDF atlas dimension. Higher = crisper when the translation
            // scales text up, at a VRAM cost. LAYOUT-NEUTRAL (text size unchanged). Options are
            // bounded dynamically by the GPU texture limit; applied immediately (SaveConfig),
            // takes effect on the next font rebuild (auto-detected — no manual .gen deletion).
            var sharpRow = Stacks.Row(card, "FontSharpnessRow", spacing: 5, minHeight: UIStyles.RowHeightMedium);
            Labels.Create(sharpRow, "FontSharpnessLabel", "Font sharpness:", TextRole.Small, minWidth: 100);

            int maxTex = FontManager.GetMaxTextureSize();
            var sharpOptions = new List<string> { "Auto" };
            foreach (int s in new[] { 4096, 8192, 16384 })
                if (s <= maxTex) sharpOptions.Add(s.ToString());
            int curBudget = TranslatorCore.Config?.max_font_atlas_size ?? 0;
            string sharpInitial = (curBudget > 0 && sharpOptions.Contains(curBudget.ToString()))
                ? curBudget.ToString() : "Auto";

            _pendingAtlasSize = curBudget;
            _fontAtlasSizeDropdown = new SearchableDropdown("FontSharpness", sharpOptions.ToArray(),
                sharpInitial, popupHeight: 150);
            var sharpHost = _fontAtlasSizeDropdown.CreateUI(sharpRow, (val) =>
            {
                // Pending only — applied (and fonts rebuilt) on Apply, like every other setting.
                _pendingAtlasSize = (val == "Auto" || !int.TryParse(val, out int b)) ? 0 : b;
                UpdateApplyButtonText();
            }, width: 140);
            _helpZone?.Describe(sharpHost, "How finely replacement fonts are rendered. Higher = crisper when the translation scales text up, but uses more video memory. 'Auto' is a safe default. Text size is unchanged. Takes effect on the next font rebuild.");

            Stacks.Spacer(card, 10);

            // Refresh button
            var refreshRow = Stacks.Row(card, "RefreshRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);

            var refreshBtn = Buttons.Secondary(refreshRow, "RefreshFontsBtn", "Refresh List", 100);
            // Explicit user request: this is the one place the ranking is allowed to re-rank.
            refreshBtn.Clicked += () => { InvalidateFontOrder(); RefreshFontsList(); };
            _helpZone?.Describe(refreshBtn,
                "Rescans the game for fonts currently in use and updates the list below.");

            _fontsStatus = Labels.Create(refreshRow, "FontsStatus", "", TextRole.Small,
                policy: TextPolicy.Dynamic, fill: Fill.Stretch);

            Stacks.Spacer(card, 10);

            // Detected fonts list
            Labels.Create(card, "FontsListLabel", "Detected Fonts:", TextRole.Small);

            // Scrollable container for fonts
            //  - preferred = a COMFORTABLE height, not the minimum. The panel sizes itself to the
            //    content's preferred height exactly, so a list asking for its minimum leaves the
            //    panel no slack at all: one pixel over and it grows its own scrollbar next to the
            //    list's. Asking for more gives the panel room it can take back under pressure.
            //  - min = how far the list may be squeezed on a small screen before the panel scrolls.
            //  - flexible (fillHeight) = it soaks up any spare height when the window is enlarged.
            // Without the preferred height the tab would instead claim the WHOLE list's height,
            // which overflows the screen for the same double-scrollbar result.
            _fontsList = ScrollList.Create(card, "FontsScroll", minHeight: 180, preferredHeight: 180,
                emptyText: "No fonts detected yet. Play the game to detect fonts.");
        }

        private void CreateFontsOverridesSubTab(Host parent)
        {
            var card = Stacks.Card(parent, "OverridesCard", PanelWidth - 60, stretchVertically: true);

            Labels.Create(card, "OverridesLabel", "Font Overrides", TextRole.SectionTitle);

            Labels.Create(card, "OverridesHint",
                "Override font size for specific UI elements. Use inspector, search, or manual pattern.", TextRole.Hint);

            Stacks.Spacer(card, 5);

            // Inspector button — click on element to add override
            var inspectorBtn = Buttons.Primary(card, "FontOverrideInspectorBtn", "Inspect Element", PanelWidth - 100);
            inspectorBtn.Clicked += OnStartFontOverrideInspector;
            _helpZone?.Describe(inspectorBtn,
                "Closes this panel so you can click a UI element in-game to create a font override for it.");

            Labels.Create(card, "InspectorHint", "Click on a UI element to create an override for it", TextRole.Hint);

            Stacks.Spacer(card, 5);

            // Find by content
            Labels.Create(card, "FindLabel", "Find by text content:", TextRole.Small);

            var findRow = Stacks.Row(card, "FindOverrideRow", spacing: 5, minHeight: UIStyles.InputHeight);

            _fontOverrideFindInput = Fields.Create(findRow, "FindOverrideInput", "Enter text visible in-game...");
            _helpZone?.Describe(_fontOverrideFindInput,
                "Type text visible in-game to locate the UI element that shows it, then create an override.");

            var findBtn = Buttons.Secondary(findRow, "FindOverrideBtn", "Find", 60);
            findBtn.Clicked += OnFindForFontOverride;
            _helpZone?.Describe(findBtn,
                "Searches the scene for UI components displaying the entered text.");

            // Find results (hidden until search)
            _fontOverrideFindResultsList = ScrollList.Create(card, "OverrideFindResults", minHeight: 0,
                preferredHeight: 80, fillHeight: false, emptyText: "No UI component found with this text.");
            _fontOverrideFindResultsList.Visible = false;

            Stacks.Spacer(card, 5);

            // Manual add
            Labels.Create(card, "ManualLabel", "Add pattern manually:", TextRole.Small);

            var addRow = Stacks.Row(card, "AddOverrideRow", spacing: 5, minHeight: UIStyles.InputHeight);
            _fontOverrideManualInput = Fields.Create(addRow, "ManualOverrideInput", "path:**/TablePanel/**");
            _helpZone?.Describe(_fontOverrideManualInput,
                "Type a rule to match elements. Prefix with path:, font:, or text: to match by hierarchy, font name, or content.");

            var addBtn = Buttons.Secondary(addRow, "AddOverrideBtn", "Add", 60);
            addBtn.Clicked += OnAddManualFontOverride;
            _helpZone?.Describe(addBtn,
                "Adds the typed pattern as a new override rule. Takes effect on Apply.");

            Labels.Create(card, "PatternHint", "Prefixes: path: (hierarchy), font: (name), text: (content)", TextRole.Hint);

            Stacks.Spacer(card, 5);

            // Count label
            _overridesCountLabel = Labels.Create(card, "OverridesCount", "", TextRole.Small, tone: Tone.Muted,
                policy: TextPolicy.Excluded);

            // Scrollable list of overrides
            _fontOverridesList = ScrollList.Create(card, "OverridesScroll", minHeight: 200, preferredHeight: 200);

            // Status label
            _fontOverrideStatus = Labels.Create(card, "OverrideStatus", "", TextRole.Small, policy: TextPolicy.Dynamic);

            RefreshFontOverridesList();
        }

        // Font override UI fields
        private LabelHandle _overridesCountLabel;
        private LabelHandle _fontOverrideStatus;
        private FieldHandle _fontOverrideFindInput;
        private FieldHandle _fontOverrideManualInput;
        private ScrollList _fontOverrideFindResultsList;

        // Pending font overrides (local copy, applied on Apply button)
        private List<FontOverrideRule> _pendingFontOverrides = new List<FontOverrideRule>();
        private List<FontOverrideRule> _initialFontOverrides = new List<FontOverrideRule>();
        // Rules that go away on Apply. Kept in the pending list (and on screen, marked) until
        // then, so the removal can be seen and undone; Apply writes the list without them.
        private readonly HashSet<FontOverrideRule> _removedFontOverrides = new HashSet<FontOverrideRule>();

        /// <summary>The rules Apply will keep: everything pending, minus what is marked for removal.</summary>
        private List<FontOverrideRule> KeptFontOverrides()
        {
            var kept = new List<FontOverrideRule>(_pendingFontOverrides.Count);
            foreach (var rule in _pendingFontOverrides)
                if (!_removedFontOverrides.Contains(rule)) kept.Add(rule);
            return kept;
        }

        private void InitPendingFontOverrides()
        {
            _pendingFontOverrides.Clear();
            _initialFontOverrides.Clear();
            _removedFontOverrides.Clear();
            foreach (var rule in TranslatorCore.FontOverrides)
            {
                _pendingFontOverrides.Add(CloneRule(rule));
                _initialFontOverrides.Add(CloneRule(rule));
            }
        }

        // ⚠ Every field of the rule, and HasFontOverrideChanges compares every one of them: a
        // field left out here is dropped from every saved rule at the next Apply, and left out
        // there is a change the Apply button never sees. rtl_alignment was both — "Keep game's"
        // chosen in the dropdown, no Apply offered, and the rule's value lost on any other Apply.
        private static FontOverrideRule CloneRule(FontOverrideRule r)
        {
            return new FontOverrideRule
            {
                match = r.match,
                replacement = r.replacement,
                size_multiplier = r.size_multiplier,
                enabled = r.enabled,
                comment = r.comment,
                rtl_alignment = r.rtl_alignment,
            };
        }

        private void OnStartFontOverrideInspector()
        {
            SetActive(false);
            Intents.OpenInspector(InspectorMode.FontOverride);
        }

        /// <summary>
        /// Called from InspectorPanel when a font override target is selected.
        /// </summary>
        public void AddFontOverrideFromInspector(string path)
        {
            // SetActive FIRST so LoadCurrentState initializes _pendingFontOverrides
            SetActive(true);
            _tabBar?.SelectTab("Fonts");
            _fontsSubTabBar?.SelectTab("Overrides");
            // THEN add the new rule (on top of initialized state)
            AddFontOverrideForPath(path);
            // Ensure we're in front of MainPanel (which may have been restored)
            Window.ToBack();
        }

        private void OnAddManualFontOverride()
        {
            string pattern = _fontOverrideManualInput?.Text?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                _fontOverrideStatus.Say("Enter a pattern first");
                _fontOverrideStatus.Tone = Tone.Warning;
                return;
            }

            AddFontOverrideForPath(pattern);
            _fontOverrideManualInput.Text = "";
        }

        private void OnFindForFontOverride()
        {
            string searchValue = _fontOverrideFindInput?.Text?.Trim();
            if (string.IsNullOrEmpty(searchValue))
            {
                _fontOverrideStatus.Say("Enter text to search for");
                _fontOverrideStatus.Tone = Tone.Warning;
                return;
            }

            _fontOverrideFindResultsList.Visible = true;
            _fontOverrideFindResultsList.Clear();

            // 🔴 Aligned on the Exclusions tab's Find: TextTargets.All() instead of the old
            // TypeHelper-based uGUI/TMP enumeration, which found nothing on a game drawn by any
            // other framework (NGUI, tk2d, TMProOld, TextMesh, UI Toolkit). The case-insensitive
            // match stays — that is this Find's own trait, not something to align away.
            var found = new List<KeyValuePair<string, string>>();
            var seenPaths = new HashSet<string>();

            foreach (var target in TextTargets.All(text => text.IndexOf(searchValue, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                string path = target.Path;
                if (string.IsNullOrEmpty(path) || !seenPaths.Add(path)) continue;
                found.Add(new KeyValuePair<string, string>(path, target.Engine));
            }

            if (found.Count == 0)
            {
                _fontOverrideStatus.Say("No results");
                _fontOverrideStatus.Tone = Tone.Warning;
                return;
            }

            foreach (var kvp in found)
            {
                var row = Stacks.Horizontal(_fontOverrideFindResultsList.Rows, "FindResult", spacing: 5,
                    pad: Pad.All(2), minHeight: UIStyles.RowHeightSmall);

                Labels.Create(row, "Path", kvp.Key, TextRole.Small, fill: Fill.Stretch);
                Labels.Create(row, "Engine", kvp.Value, TextRole.Caption, policy: TextPolicy.Excluded,
                    wrap: false, minWidth: 60, align: Placement.MiddleRight);

                var capturedPath = kvp.Key;
                var addPathBtn = Buttons.Secondary(row, "AddOverride", "+", 30);
                addPathBtn.Clicked += () => AddFontOverrideForPath("path:" + capturedPath);
            }

            _fontOverrideFindResultsList.Filled();
            _fontOverrideStatus.Say($"Found {found.Count} component(s)");
            _fontOverrideStatus.Tone = Tone.Success;
        }

        private void AddFontOverrideForPath(string match)
        {
            var rule = new FontOverrideRule
            {
                match = match,
                size_multiplier = 1.0f,
                enabled = true
            };
            _pendingFontOverrides.Add(rule);
            RefreshFontOverridesList();
            UpdateApplyButtonText();
            if (_fontOverrideStatus != null)
            {
                _fontOverrideStatus.Show($"Added: {match} (Apply to save)");
                _fontOverrideStatus.Tone = Tone.Success;
            }
        }

        private void RefreshFontOverridesList()
        {
            if (_fontOverridesList == null) return;

            _rtlControlsVisible = TranslatorCore.TranslationTouchesRtl();

            foreach (var dropdown in _overrideRtlDropdowns)
                dropdown.Destroy();
            _overrideRtlDropdowns.Clear();

            _fontOverridesList.Clear();
            Pending.ClearGroup("overrides");

            if (_overridesCountLabel != null)
            {
                int kept = _pendingFontOverrides.Count - _removedFontOverrides.Count;
                _overridesCountLabel.Show(kept > 0 ? $"{kept} rule(s)" : "No rules defined");
            }

            for (int i = 0; i < _pendingFontOverrides.Count; i++)
            {
                CreateFontOverrideRow(i, _pendingFontOverrides[i]);
            }

            if (_pendingFontOverrides.Count > 0) _fontOverridesList.Filled();
        }

        /// <summary>
        /// A rule that goes away on Apply: its card stays, reduced to what identifies it, marked
        /// and worded as waiting, with the one action that still makes sense.
        /// </summary>
        private void CreateRemovedFontOverrideRow(int index, FontOverrideRule rule)
        {
            var row = Stacks.Horizontal(_fontOverridesList.Rows, $"Override_{index}", spacing: 5,
                pad: new Pad(5, 5, 8, 8), surface: Surface.Card, minHeight: UIStyles.RowHeightNormal);
            Pending.TrackState(row, () => PendingState.Removed, "overrides");

            Labels.Create(row, "MatchLabel", string.IsNullOrEmpty(rule.match) ? "(empty rule)" : rule.match,
                TextRole.Small, tone: Tone.Muted, policy: TextPolicy.Excluded, fill: Fill.Stretch);

            Labels.Create(row, "RemovedLabel", "Removed on Apply", TextRole.Small, tone: Tone.Error,
                minWidth: 110, align: Placement.MiddleRight);

            var undoBtn = Buttons.Secondary(row, "UndoBtn", "Undo", 50);
            undoBtn.Clicked += () =>
            {
                _removedFontOverrides.Remove(rule);
                RefreshFontOverridesList();
                UpdateApplyButtonText();
            };
        }

        private void CreateFontOverrideRow(int index, FontOverrideRule rule)
        {
            if (_removedFontOverrides.Contains(rule)) { CreateRemovedFontOverrideRow(index, rule); return; }

            var row = Stacks.Vertical(_fontOverridesList.Rows, $"Override_{index}", spacing: 3,
                surface: Surface.Card, minHeight: UIStyles.MultiLineMedium);

            // A rule the panel opened with is compared field by field to what it was; a rule
            // added since is one thing waiting as a whole. The lists stay parallel by index —
            // nothing reorders them, and a removal keeps its slot until Apply.
            FontOverrideRule initial = index < _initialFontOverrides.Count ? _initialFontOverrides[index] : null;
            if (initial == null) Pending.TrackState(row, () => PendingState.Added, "overrides");

            // Row 1: Match pattern (editable) + delete button
            var topRow = Stacks.Row(row, "TopRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);

            Labels.Create(topRow, "MatchLabel", "Match:", TextRole.Small, policy: TextPolicy.Excluded, minWidth: 45);

            var matchInput = Fields.Create(topRow, "MatchInput", "path:*Pattern*");
            matchInput.Text = rule.match ?? "";

            int capturedIndex = index;

            matchInput.Changed += (val) =>
            {
                if (capturedIndex < _pendingFontOverrides.Count)
                {
                    _pendingFontOverrides[capturedIndex].match = val;
                    UpdateApplyButtonText();
                }
            };
            if (initial != null)
                Pending.Track(matchInput, () => (rule.match ?? "") != (initial.match ?? ""), "overrides");

            // Delete button: a rule the panel opened with waits for Apply, marked and undoable;
            // one added since simply goes, there is nothing on disk to take back.
            var deleteBtn = Buttons.Compact(topRow, "DeleteBtn", "X", ButtonTone.Danger, minWidth: 28,
                policy: TextPolicy.Excluded);
            deleteBtn.Clicked += () =>
            {
                if (capturedIndex >= _pendingFontOverrides.Count) return;
                if (initial != null) _removedFontOverrides.Add(rule);
                else _pendingFontOverrides.RemoveAt(capturedIndex);
                RefreshFontOverridesList();
                UpdateApplyButtonText();
            };

            // Row 2: Size multiplier slider — the mod's other "Size:" slider (the Fonts sub-tab's
            // per-font row) reads label → slider → value; this one used to read label → value →
            // slider. The vocabulary's slider factory offers one order, so this row now reads the
            // same way as its sibling — a harmless reordering, not a behaviour change.
            float initialSlider = rule.size_multiplier > 0.001f ? rule.size_multiplier : 1.0f;
            var sizeSlider = Sliders.Labelled(row, $"OverrideSize_{index}", "Size:", 0f, 3f, initialSlider,
                v =>
                {
                    float rounded = (float)Math.Round(v * 20) / 20f;
                    return rounded > 0.001f ? $"{(int)(rounded * 100)}%" : "default";
                },
                v =>
                {
                    float rounded = (float)Math.Round(v * 20) / 20f;
                    if (capturedIndex < _pendingFontOverrides.Count)
                    {
                        _pendingFontOverrides[capturedIndex].size_multiplier = rounded;
                        UpdateApplyButtonText();
                    }
                },
                captionWidth: 35);
            if (initial != null)
                Pending.Track(sizeSlider, () => Math.Abs(rule.size_multiplier - initial.size_multiplier) > 0.001f, "overrides");

            // RTL alignment for the matched components (only when this translation involves
            // right-to-left text): inherit the font's setting, or force mirror/keep here — the
            // per-rule refinement the bench demanded (one game, mirroring pane next to
            // one-side-built buttons).
            if (_rtlControlsVisible)
            {
                var rtlRow = Stacks.Row(row, "RtlRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);

                Labels.Create(rtlRow, "RtlLabel", "RTL alignment:", TextRole.Small, policy: TextPolicy.Excluded, minWidth: 95);

                string initialRtl = string.Equals(rule.rtl_alignment, "mirror", StringComparison.OrdinalIgnoreCase) ? "Mirror"
                                  : string.Equals(rule.rtl_alignment, "keep", StringComparison.OrdinalIgnoreCase) ? "Keep game's"
                                  : "Inherit from font";
                var rtlDropdown = new SearchableDropdown($"OverrideRtl_{index}",
                    new[] { "Inherit from font", "Mirror", "Keep game's" }, initialRtl);
                var rtlHost = rtlDropdown.CreateUI(rtlRow, (selected) =>
                {
                    if (capturedIndex >= _pendingFontOverrides.Count) return;
                    _pendingFontOverrides[capturedIndex].rtl_alignment =
                        selected == "Mirror" ? "mirror" : selected == "Keep game's" ? "keep" : null;
                    UpdateApplyButtonText();
                }, 150);
                _overrideRtlDropdowns.Add(rtlDropdown);
                if (initial != null)
                    Pending.Track(rtlHost, () => !string.Equals(rule.rtl_alignment, initial.rtl_alignment, StringComparison.OrdinalIgnoreCase), "overrides");
            }
        }

        private void RefreshFontsList()
        {
            if (_fontsList == null) return;

            _rtlControlsVisible = TranslatorCore.TranslationTouchesRtl();
            TranslatorCore.LogInfo($"[TranslationParametersPanel] RefreshFontsList called");
            Pending.ClearGroup("fonts");

            // Clean up searchable dropdowns first
            foreach (var dropdown in _fallbackDropdowns)
            {
                dropdown.Destroy();
            }
            _fallbackDropdowns.Clear();

            _fontsList.Clear();

            var fonts = FontManager.GetDetectedFontsInfo();
            fonts = ApplyStableFontOrder(fonts);

            if (fonts.Count == 0)
            {
                if (_fontsStatus != null)
                {
                    _fontsStatus.Say("0 fonts detected");
                    _fontsStatus.Tone = Tone.Muted;
                }
                return;
            }

            // Cache system fonts if not already done
            if (_systemFonts == null)
            {
                _systemFonts = FontManager.SystemFonts;
            }

            foreach (var fontInfo in fonts)
            {
                CreateFontRow(fontInfo);
            }

            _fontsList.Filled();

            if (_fontsStatus != null)
            {
                _fontsStatus.Say($"{fonts.Count} font(s) detected");
                _fontsStatus.Tone = Tone.Success;
            }
        }

        // Row order held for the whole editing session. Rebuilt only when the user opens the panel
        // or asks for a refresh — never behind their back.
        private List<string> _fontRowOrder;

        /// <summary>Forget the frozen order so the next list build re-ranks from scratch.</summary>
        private void InvalidateFontOrder() => _fontRowOrder = null;

        /// <summary>
        /// Keep the rows where the user last saw them. The ranking (configured first, then presence
        /// in the scene) is a good STARTING order, but it must not move under the user's hands:
        /// ApplySettings rebuilds this list, and the scene keeps changing while the panel is open,
        /// so re-ranking there moved the very row that had just been edited. Fonts appearing since
        /// the order was taken are appended, keeping their relative ranking.
        /// </summary>
        private List<FontDisplayInfo> ApplyStableFontOrder(List<FontDisplayInfo> fonts)
        {
            if (fonts == null || fonts.Count == 0) return fonts;

            if (_fontRowOrder == null)
            {
                _fontRowOrder = new List<string>(fonts.Count);
                foreach (var f in fonts) _fontRowOrder.Add(f.Name);
                return fonts;
            }

            var byName = new Dictionary<string, FontDisplayInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fonts) byName[f.Name] = f;

            var ordered = new List<FontDisplayInfo>(fonts.Count);
            foreach (var name in _fontRowOrder)
            {
                if (byName.TryGetValue(name, out var f))
                {
                    ordered.Add(f);
                    byName.Remove(name);
                }
            }
            // Newly detected fonts: appended in ranking order, and joining the frozen order so they
            // stay put from now on.
            foreach (var f in fonts)
            {
                if (!byName.ContainsKey(f.Name)) continue;
                ordered.Add(f);
                _fontRowOrder.Add(f.Name);
            }
            return ordered;
        }

        /// <summary>
        /// The picker entry matching <paramref name="wanted"/>, ignoring display-only markers on
        /// either side, or null. Marker-insensitive because the same font is offered as
        /// "[Game] X" or "[Game] X (not loaded)" depending on what the game currently holds in
        /// memory — a stored fallback must select its entry in both cases.
        /// </summary>
        private static string FindOption(List<string> options, string wanted)
        {
            if (options == null || string.IsNullOrEmpty(wanted)) return null;

            string target = FontManager.StripOptionMarker(wanted);
            foreach (var option in options)
            {
                if (string.Equals(FontManager.StripOptionMarker(option), target, StringComparison.Ordinal))
                    return option;
            }
            return null;
        }

        private void CreateFontRow(FontDisplayInfo fontInfo)
        {
            // Main row container with padding
            var row = Stacks.Vertical(_fontsList.Rows, $"FontRow_{fontInfo.Name.GetHashCode()}", spacing: 3,
                pad: Pad.All(5), surface: Surface.Card, minHeight: 55);

            // Header row: font name + type + enable toggle
            var headerRow = Stacks.Row(row, "HeaderRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);

            // Capture values for closure
            string capturedFontName = fontInfo.Name;

            // Font name and type
            Labels.Create(headerRow, "FontLabel", $"{fontInfo.Name} ({fontInfo.Type})", TextRole.Body,
                tone: Tone.Plain, policy: TextPolicy.Excluded, fill: Fill.Stretch);

            // How present this font is on the screen right now — the figure that tells the user
            // whether a font is worth configuring. -1 means the count couldn't be taken; say
            // nothing rather than show a misleading zero.
            if (fontInfo.SceneCount >= 0)
            {
                Labels.Create(headerRow, "SceneCount", $"{fontInfo.SceneCount} " + Tr("in scene"), TextRole.Small,
                    tone: fontInfo.SceneCount > 0 ? Tone.Secondary : Tone.Muted, policy: TextPolicy.Excluded,
                    minWidth: 70);
            }

            // Identify button: highlight in-game texts using this font. Its tone swaps between
            // Secondary and Primary to say whether it is the one currently highlighted — the
            // vocabulary's tones stand in for the bespoke slate/accent fill the raw button used.
            var identifyBtn = Buttons.Compact(headerRow, "IdentifyBtn", "?", ButtonTone.Secondary, minWidth: 28,
                policy: TextPolicy.Excluded);
            identifyBtn.Clicked += () => ToggleFontHighlight(capturedFontName, identifyBtn);

            // Enable toggle
            var enableToggle = CheckBoxes.Create(headerRow, "EnableToggle", "Translate", fontInfo.Enabled,
                (isOn) => OnFontEnableChanged(capturedFontName, isOn));
            Pending.Track(enableToggle, () => FontFieldChanged(capturedFontName, (p, i) => p.enabled != i.enabled), "fonts");

            // RTL alignment (only when this translation involves right-to-left text): mirror the
            // component's alignment to follow the reading direction, or keep the game's own —
            // per font and shared with the translation, refinable per rule below.
            if (_rtlControlsVisible)
            {
                var rtlRow = Stacks.Row(row, "RtlRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);
                var rtlToggle = CheckBoxes.Create(rtlRow, "RtlMirrorToggle", "Mirror alignment (RTL)",
                    GetEffectiveFontSettings(capturedFontName).mirrorRtl,
                    (isOn) => OnFontRtlAlignChanged(capturedFontName, isOn));
                Pending.Track(rtlToggle, () => FontFieldChanged(capturedFontName, (p, i) => p.mirrorRtl != i.mirrorRtl), "fonts");
                _helpZone?.Describe(rtlToggle,
                    "Right-to-left text flips left-aligned components to right-aligned, following the reading direction. Turn off to keep the game's own alignment when its layout was built around one side.");
            }

            // Fallback row (only for fonts that support it)
            if (fontInfo.SupportsFallback)
            {
                var fallbackRow = Stacks.Row(row, "FallbackRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);

                Labels.Create(fallbackRow, "FallbackLabel", "Fallback:", TextRole.Small, policy: TextPolicy.Excluded, minWidth: 55);

                // Build options array based on font type
                var options = new List<string> { "(None)" };
                string[] availableFonts = null;
                bool isTMPFont = fontInfo.Type == "TMP" || fontInfo.Type == "TextMeshPro" || fontInfo.Type == "TMP (alt)";

                if (fontInfo.Type == "TMP (alt)")
                {
                    // For alternate TMP (TMProOld, etc.), show game fonts + system fonts
                    var altFonts = TranslatorPatches.GetAlternateTMPFontNames();
                    if (altFonts != null && altFonts.Length > 0)
                    {
                        options.Add("--- Game Fonts ---");
                        foreach (var af in altFonts)
                            options.Add("[Game] " + af);
                    }

                    if (_systemFonts != null && _systemFonts.Length > 0)
                    {
                        options.Add("--- System Fonts ---");
                        availableFonts = _systemFonts;
                    }
                }
                else if (isTMPFont)
                {
                    var gameFonts = FontManager.GetGameFontNames();
                    var knownFonts = FontManager.GetKnownUnloadedFontNames(tmpFamily: true);
                    if (gameFonts.Length > 0 || knownFonts.Length > 0)
                    {
                        options.Add("--- Game Fonts ---");
                        foreach (var gf in gameFonts)
                            options.Add("[Game] " + gf);
                        // Known from the translation but not in memory right now — see
                        // FontManager.GetKnownUnloadedFontNames. Without them, a font used as a
                        // fallback in a past session could not be picked again.
                        foreach (var kf in knownFonts)
                            options.Add("[Game] " + kf + FontManager.UnloadedMarker);
                    }

                    if (_systemFonts != null && _systemFonts.Length > 0)
                    {
                        options.Add("--- System Fonts ---");
                        availableFonts = _systemFonts;
                    }
                }
                else
                {
                    // Unity Font: game fonts first (with [Game] prefix), then system fonts
                    var gameUnityFonts = FontManager.GetGameUnityFontNames();
                    var knownFonts = FontManager.GetKnownUnloadedFontNames(tmpFamily: false);
                    if (gameUnityFonts.Length > 0 || knownFonts.Length > 0)
                    {
                        options.Add("--- Game Fonts ---");
                        foreach (var gf in gameUnityFonts)
                            options.Add("[Game] " + gf);
                        foreach (var kf in knownFonts)
                            options.Add("[Game] " + kf + FontManager.UnloadedMarker);
                    }
                    availableFonts = _systemFonts;
                }

                if (availableFonts != null && availableFonts.Length > 0)
                {
                    if (options.Count > 1)
                        options.Add("--- System Fonts ---");
                    options.AddRange(availableFonts);
                }

                // Add custom fonts (user-provided fonts from fonts/ folder)
                string[] customFonts = FontManager.GetCustomFontNames();
                if (customFonts != null && customFonts.Length > 0)
                {
                    if (options.Count > 1)
                        options.Add("--- Custom Fonts ---");
                    foreach (var customFont in customFonts)
                        options.Add("[Custom] " + customFont);
                }

                // If no fonts available at all
                if (options.Count <= 1)
                {
                    Labels.Create(fallbackRow, "NoFontsLabel", "(no fonts available)", TextRole.Small,
                        tone: Tone.Muted, policy: TextPolicy.Excluded);
                    return;
                }

                // Determine initial value.
                // Matched against the options AS DISPLAYED but marker-insensitive: the same font
                // shows as "[Game] X" or "[Game] X (not loaded)" depending on what the game has
                // loaded right now, and a configured fallback must find its entry either way.
                // The selected value must BE one of the options, otherwise the list opens with
                // nothing highlighted and the user cannot see what is currently set.
                string initialValue = "(None)";
                if (!string.IsNullOrEmpty(fontInfo.FallbackFont))
                {
                    string match = FindOption(options, fontInfo.FallbackFont)
                        // Migration: old JSON might have a game font name without [Game] prefix
                        ?? FindOption(options, "[Game] " + fontInfo.FallbackFont);

                    if (match == null)
                    {
                        match = fontInfo.FallbackFont + FontManager.IncompatibleMarker;
                        options.Add(match);
                    }

                    initialValue = match;
                }

                // Create searchable dropdown with filter
                var dropdown = new SearchableDropdown(
                    $"Fallback_{capturedFontName}",
                    options.ToArray(),
                    initialValue,
                    popupHeight: 250
                );
                dropdown.CategoryProvider = FontManager.GetFontOrigin;

                var dropdownHost = dropdown.CreateUI(fallbackRow, (selectedValue) =>
                {
                    // Markers are display only — what gets stored is the font name
                    selectedValue = FontManager.StripOptionMarker(selectedValue);
                    string fallback = selectedValue == "(None)" ? null : selectedValue;
                    OnFontFallbackChanged(capturedFontName, fallback);
                }, width: 350);

                _fallbackDropdowns.Add(dropdown);
                Pending.Track(dropdownHost, () => FontFieldChanged(capturedFontName, (p, i) => p.fallback != i.fallback), "fonts");
            }
            else
            {
                // Show hint for non-TMP fonts
                Labels.Create(row, "NoFallbackLabel", "Fallback not supported for this font type", TextRole.Small,
                    tone: Tone.Muted, policy: TextPolicy.Excluded);
            }

            // Size row (for all fonts) — the DELIBERATE size percent (fit/readability, e.g. a longer
            // cross-script translation vs the HUD). Orthogonal to the auto design-scale: the two
            // combine multiplicatively (Model B). 100% = native.
            var scaleRow = Stacks.Row(row, "ScaleRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);

            // Size slider (1% to 200%) = the deliberate percent. Always active; it does NOT replace
            // the auto design-scale, it applies on top of it.
            float sizePercent = FontManager.GetFontSizePercent(capturedFontName);
            var scaleSlider = Sliders.Labelled(scaleRow, $"Scale_{capturedFontName}", "Size:", 0.01f, 2.0f,
                Math.Min(2.0f, sizePercent),
                v => $"{(int)(Math.Round(v, 2) * 100)}%",
                v => OnFontScaleChanged(capturedFontName, (float)Math.Round(v, 2)),
                captionWidth: 55);
            Pending.Track(scaleSlider, () => FontFieldChanged(capturedFontName, (p, i) => Math.Abs(p.sizePercent - i.sizePercent) > 0.001f), "fonts");

            // Auto design-scale toggle — folds the font's native design-scale into the size as a
            // baseline (so an imported font matches the game's original size), on top of which the
            // slider % still applies. Default ON for freshly detected TMP fonts. HIDDEN for font
            // types where the design-scale has no meaning (UI.Text / non-TMP clone-atlas preserves
            // the game's metrics). Commits on Apply only (UX rule: no immediate application).
            if (FontManager.SupportsDesignScale(fontInfo.Type))
            {
                bool fontScaleAuto = FontManager.GetFontSettings(capturedFontName)?.scale_auto ?? false;
                var autoToggle = CheckBoxes.Create(scaleRow, $"AutoScale_{capturedFontName}", "Auto", fontScaleAuto,
                    (isOn) => OnFontAutoScaleChanged(capturedFontName, isOn));
                Pending.Track(autoToggle, () => FontFieldChanged(capturedFontName, (p, i) => p.scaleAuto != i.scaleAuto), "fonts");
            }
        }

        /// <summary>
        /// Whether one setting of a font differs from what the panel opened with. A font that
        /// appeared after the opening (Refresh List) has no snapshot: what is stored for it now
        /// stands in, which is what "as it was" means for that font.
        /// </summary>
        private bool FontFieldChanged(string fontName,
            Func<(bool enabled, string fallback, float sizePercent, bool scaleAuto, bool mirrorRtl),
                 (bool enabled, string fallback, float sizePercent, bool scaleAuto, bool mirrorRtl), bool> differs)
        {
            if (!_pendingFontSettings.TryGetValue(fontName, out var pending)) return false;
            if (!_initialFontSettings.TryGetValue(fontName, out var initial))
            {
                var settings = FontManager.GetFontSettings(fontName);
                initial = (settings?.enabled ?? true, settings?.fallback, FontManager.GetFontSizePercent(fontName),
                           settings?.scale_auto ?? false,
                           !string.Equals(settings?.rtl_alignment, "keep", StringComparison.OrdinalIgnoreCase));
            }
            return differs(pending, initial);
        }

        /// <summary>
        /// Toggle font highlight: click to show, click again (or click another) to clear.
        /// </summary>
        private void ToggleFontHighlight(string fontName, ButtonHandle button)
        {
            if (_highlightedFontName == fontName)
            {
                // Same font clicked again - clear highlight
                TranslatorScanner.ClearHighlight();
                ResetHighlightButton();
            }
            else
            {
                // Different font or first click - highlight this one
                TranslatorScanner.ClearHighlight();
                ResetHighlightButton();

                TranslatorScanner.HighlightFont(fontName);
                _highlightedFontName = fontName;
                _highlightedButton = button;

                // Visual feedback: active state on button
                button.Text.Show("X");
                button.Text.Tone = Tone.Plain;
                button.Tone = ButtonTone.Primary;
            }
        }

        private void ResetHighlightButton()
        {
            if (_highlightedButton != null)
            {
                _highlightedButton.Text.Show("?");
                _highlightedButton.Text.Tone = Tone.Secondary;
                _highlightedButton.Tone = ButtonTone.Secondary;
            }
            _highlightedFontName = null;
            _highlightedButton = null;
        }

        /// <summary>
        /// Resolve a font's current settings for change-tracking: pending edit if any, else the
        /// captured initial, else the live font state. Single source so each handler mutates one
        /// field and preserves the others (enabled, fallback, sizePercent, scaleAuto).
        /// </summary>
        private (bool enabled, string fallback, float sizePercent, bool scaleAuto, bool mirrorRtl) GetEffectiveFontSettings(string fontName)
        {
            if (_pendingFontSettings.TryGetValue(fontName, out var pending))
                return pending;
            if (_initialFontSettings.TryGetValue(fontName, out var initial))
                return initial;
            var settings = FontManager.GetFontSettings(fontName);
            return (settings?.enabled ?? true, settings?.fallback,
                    FontManager.GetFontSizePercent(fontName),  // deliberate percent (not the effective)
                    settings?.scale_auto ?? false,
                    !string.Equals(settings?.rtl_alignment, "keep", StringComparison.OrdinalIgnoreCase));
        }

        private void OnFontRtlAlignChanged(string fontName, bool mirror)
        {
            var cur = GetEffectiveFontSettings(fontName);
            _pendingFontSettings[fontName] = (cur.enabled, cur.fallback, cur.sizePercent, cur.scaleAuto, mirror);
            UpdateApplyButtonText();
        }

        private void OnFontEnableChanged(string fontName, bool enabled)
        {
            var cur = GetEffectiveFontSettings(fontName);
            _pendingFontSettings[fontName] = (enabled, cur.fallback, cur.sizePercent, cur.scaleAuto, cur.mirrorRtl);

            if (_fontsStatus != null)
            {
                _fontsStatus.Show(enabled ? $"Translation enabled for {fontName}" : $"Translation disabled for {fontName}");
                _fontsStatus.Tone = Tone.Secondary;
            }

            UpdateApplyButtonText();
        }

        private void OnFontFallbackChanged(string fontName, string fallbackFont)
        {
            var cur = GetEffectiveFontSettings(fontName);
            _pendingFontSettings[fontName] = (cur.enabled, fallbackFont, cur.sizePercent, cur.scaleAuto, cur.mirrorRtl);

            if (_fontsStatus != null)
            {
                if (string.IsNullOrEmpty(fallbackFont))
                {
                    _fontsStatus.Show($"Fallback will be removed from {fontName}");
                }
                else
                {
                    _fontsStatus.Show($"Fallback '{fallbackFont}' will be applied to {fontName}");
                }
                _fontsStatus.Tone = Tone.Secondary;
            }

            UpdateApplyButtonText();
        }

        private void OnFontScaleChanged(string fontName, float sizePercent)
        {
            var cur = GetEffectiveFontSettings(fontName);
            // The slider is the deliberate size percent — orthogonal to the auto design-scale, so
            // scaleAuto is preserved (Model B: the two combine).
            _pendingFontSettings[fontName] = (cur.enabled, cur.fallback, sizePercent, cur.scaleAuto, cur.mirrorRtl);

            if (_fontsStatus != null)
            {
                int percent = (int)Math.Round(sizePercent * 100f);
                _fontsStatus.Show($"Font size {percent}% will be applied to {fontName}");
                _fontsStatus.Tone = Tone.Secondary;
            }

            UpdateApplyButtonText();
        }

        /// <summary>
        /// Toggle the auto design-scale for a font. Orthogonal to the size slider: it only folds the
        /// design-scale baseline in/out; the deliberate percent is preserved. Applied on Apply only.
        /// </summary>
        private void OnFontAutoScaleChanged(string fontName, bool scaleAuto)
        {
            var cur = GetEffectiveFontSettings(fontName);
            _pendingFontSettings[fontName] = (cur.enabled, cur.fallback, cur.sizePercent, scaleAuto, cur.mirrorRtl);

            if (_fontsStatus != null)
            {
                _fontsStatus.Show(scaleAuto
                    ? $"Auto native size enabled for {fontName}"
                    : $"Auto native size disabled for {fontName}");
                _fontsStatus.Tone = Tone.Secondary;
            }

            UpdateApplyButtonText();
        }

        #endregion

        #region Images Tab

        private void CreateImagesTabContent(Host parent)
        {
            var card = Stacks.Card(parent, "ImagesCard", PanelWidth - 60, stretchVertically: true);

            // Section title
            Labels.Create(card, "ImagesLabel", "Bitmap Replacements", TextRole.SectionTitle);

            Labels.Create(card, "ImagesHint",
                "Replace images that contain text (bitmap text) with translated versions. " +
                "Use the inspector to select images, export originals as templates, " +
                "then import your translated PNG files.", TextRole.Hint);

            Stacks.Spacer(card, 5);

            // Debug toggle: globally disable image replacement (for translators).
            _enableImageReplacementToggle = CheckBoxes.Create(card, "EnableImageReplacementToggle",
                "Enable image replacement (uncheck to debug with original images)",
                TranslatorCore.Config.enable_image_replacement, OnEnableImageReplacementChanged);
            _helpZone?.Describe(_enableImageReplacementToggle,
                "Swaps images that contain baked-in text for your translated versions. Uncheck to debug with the original images.");

            Stacks.Spacer(card, 5);

            // Start Image Inspector button
            var inspectorBtn = Buttons.Primary(card, "ImageInspectorBtn", "Start Image Inspector", PanelWidth - 100);
            inspectorBtn.Clicked += OnStartImageInspectorClicked;
            _helpZone?.Describe(inspectorBtn,
                "Closes this panel so you can click images in-game to mark them for replacement.");

            Labels.Create(card, "ImageInspectorHint", "Click on images in the game to mark them for replacement", TextRole.Hint);

            Stacks.Spacer(card, 8);

            // Current replacements list
            Labels.Create(card, "ListLabel", "Current Replacements:", TextRole.Small).Bold = true;

            _imagesList = ScrollList.Create(card, "ImagesScroll", minHeight: 200, preferredHeight: 200,
                emptyText: "No images marked for replacement yet.\nUse the Image Inspector to select images.");

            // Apply All button
            Stacks.Spacer(card, 5);

            var applyRow = Stacks.Row(card, "ApplyRow", spacing: 5, minHeight: UIStyles.ButtonHeight);
            var applyAllBtn = Buttons.Create(applyRow, "ApplyAllBtn", "Load All Replacements",
                ButtonTone.Primary, fill: Fill.Stretch);
            applyAllBtn.Clicked += OnLoadAllReplacementsClicked;
            _helpZone?.Describe(applyAllBtn,
                "Reloads your edited PNG files from disk and applies them in-game. Use after editing the exported templates.");

            // Status label
            _imagesStatus = Labels.Create(card, "ImagesStatus", "", TextRole.Small, policy: TextPolicy.Dynamic);

            // Initial populate
            RefreshImageReplacementsList();
        }

        private void RefreshImageReplacementsList()
        {
            if (_imagesList == null) return;
            _imagesList.Clear();

            var replacements = ImageReplacer.GetAll();
            if (replacements.Count == 0) return;

            foreach (var kvp in replacements)
            {
                var entry = kvp.Value;
                var spriteName = entry.SpriteName;
                bool isLoaded = ImageReplacer.IsReplacementLoaded(spriteName);
                bool fileExists = ImageReplacer.HasReplacementFile(spriteName);
                var capturedName = spriteName;

                // Row container
                var row = Stacks.Horizontal(_imagesList.Rows, "Row_" + spriteName, spacing: 5, pad: Pad.All(2),
                    minHeight: UIStyles.RowHeightNormal);

                // Info
                var infoCol = Stacks.Vertical(row, "Info", fill: Fill.Stretch);

                Labels.Create(infoCol, "Name", $"{spriteName} ({entry.OriginalWidth}x{entry.OriginalHeight})",
                    TextRole.Small, policy: TextPolicy.Excluded, fill: Fill.Stretch).Bold = true;

                // Status
                string statusText; Tone statusTone;
                if (isLoaded)
                {
                    statusText = "Replacement active";
                    statusTone = Tone.Success;
                }
                else if (fileExists)
                {
                    statusText = $"File ready: {entry.File} (click Load All)";
                    statusTone = Tone.Warning;
                }
                else
                {
                    statusText = "Edit the exported PNG, then Load All";
                    statusTone = Tone.Muted;
                }

                Labels.Create(infoCol, "Status", statusText, TextRole.Caption, tone: statusTone,
                    policy: TextPolicy.Excluded, fill: Fill.Stretch);

                // Remove button
                var removeBtn = Buttons.Secondary(row, "Remove_" + spriteName, "X", 30);
                removeBtn.Clicked += () =>
                {
                    ImageReplacer.RemoveReplacement(capturedName);
                    TranslatorCore.SaveCache();
                    RefreshImageReplacementsList();
                    _imagesStatus.Show(Tr("Removed:") + $" {capturedName}");
                    _imagesStatus.Tone = Tone.Secondary;
                };
            }

            _imagesList.Filled();
        }

        private void OnLoadAllReplacementsClicked()
        {
            // Force reload all (user may have edited PNGs on disk)
            int loaded = ImageReplacer.LoadAllReplacements(forceReload: true);
            int applied = ImageReplacer.ApplyToScene();
            RefreshImageReplacementsList();

            if (loaded > 0)
            {
                _imagesStatus.Say($"Loaded {loaded} replacement(s)");
                _imagesStatus.Tone = Tone.Success;
            }
            else
            {
                _imagesStatus.Say("No new replacements to load");
                _imagesStatus.Tone = Tone.Muted;
            }
        }

        #endregion

        #region Variables Tab

        private void CreateVariablesTabContent(Host parent)
        {
            var card = Stacks.Card(parent, "VariablesCard", PanelWidth - 60, stretchVertically: true);

            // Section title
            Labels.Create(card, "VarsLabel", "Game Variables", TextRole.SectionTitle);

            Labels.Create(card, "VarsHint",
                "Capture dynamic game values (player name, clan name, etc.) so translations can be reused regardless of the actual value. " +
                "Variables are replaced with placeholders before translation.", TextRole.Hint);

            Stacks.Spacer(card, 8);

            // Capture section
            Labels.Create(card, "CaptureLabel", "Capture Variable", TextRole.SectionTitle);

            Labels.Create(card, "CaptureHint",
                "Enter the current value of a game variable (e.g. your character name) to find it in memory.", TextRole.Hint);

            var captureRow = Stacks.Row(card, "CaptureRow", spacing: 5, minHeight: UIStyles.RowHeightNormal);

            _scanValueInput = Fields.Create(captureRow, "ScanValueInput", "Enter value to search...");
            _helpZone?.Describe(_scanValueInput,
                "Type the current value of a game variable, like your character name, to find it in memory.");

            var scanBtn = Buttons.Primary(captureRow, "ScanBtn", "Scan", 70);
            scanBtn.Clicked += OnScanClicked;
            _helpZone?.Describe(scanBtn,
                "Searches game memory for the entered value so it can be turned into a reusable placeholder.");

            Stacks.Spacer(card, 5);

            // Scan results (hidden until scan)
            _scanResultsList = ScrollList.Create(card, "ScanResultsScroll", minHeight: 80, preferredHeight: 100,
                fillHeight: false, emptyText: "No matching fields found in game memory.");
            _scanResultsList.Visible = false;

            Stacks.Spacer(card, 8);

            // Current variables list
            Labels.Create(card, "ListLabel", "Defined Variables:", TextRole.Small).Bold = true;

            _variablesList = ScrollList.Create(card, "VarsScroll", minHeight: 200, preferredHeight: 200);

            // Status label
            Stacks.Spacer(card, 5);
            _variablesStatus = Labels.Create(card, "VarsStatus", "", TextRole.Small, policy: TextPolicy.Dynamic);

            // Initial populate
            RefreshVariablesList();
        }

        private void OnScanClicked()
        {
            if (_isScanning) return;

            string value = _scanValueInput?.Text?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                _variablesStatus.Say("Enter a value to search for");
                _variablesStatus.Tone = Tone.Warning;
                return;
            }

            _isScanning = true;
            _variablesStatus.Say("Scanning...");
            _variablesStatus.Tone = Tone.Secondary;

            // Show results container
            _scanResultsList.Visible = true;

            // Clear previous results
            _scanResultsList.Clear();

            try
            {
                var candidates = VariableManager.ScanForValue(value);

                if (candidates.Count == 0)
                {
                    _variablesStatus.Say("No results found");
                    _variablesStatus.Tone = Tone.Warning;
                }
                else
                {
                    foreach (var candidate in candidates)
                    {
                        var row = Stacks.Horizontal(_scanResultsList.Rows, "Candidate", spacing: 5, pad: Pad.All(2),
                            minHeight: UIStyles.RowHeightSmall);

                        // Show the matched value: with partial matches (composed display
                        // strings like "seedA-seedB"), the path alone doesn't tell the
                        // user which piece of the text each candidate holds.
                        string valPreview = (candidate.CurrentValue ?? "").Replace("\r", " ").Replace("\n", " ");
                        if (valPreview.Length > 24) valPreview = valPreview.Substring(0, 24) + "...";
                        string display = $"{candidate.ClassName}.{candidate.FieldPath} = \"{valPreview}\"";
                        if (candidate.IsStatic) display += " (static)";

                        Labels.Create(row, "Label", display, TextRole.Small, policy: TextPolicy.Excluded, fill: Fill.Stretch);

                        var capturedCandidate = candidate;
                        var addBtn = Buttons.Secondary(row, "Add", "+", 30);
                        addBtn.Clicked += () =>
                        {
                            // Prompt for a name — use the field name as default
                            string varName = capturedCandidate.FieldPath.Split('.').Last();
                            VariableManager.AddVariable(varName, capturedCandidate.ClassName, capturedCandidate.FieldPath);
                            TranslatorCore.SaveCache();
                            RefreshVariablesList();
                            _variablesStatus.Show($"Added: {varName} ({capturedCandidate.ClassName}.{capturedCandidate.FieldPath})");
                            _variablesStatus.Tone = Tone.Success;
                        };
                    }

                    _scanResultsList.Filled();

                    _variablesStatus.Say($"Found {candidates.Count} candidate(s). Click + to add.");
                    _variablesStatus.Tone = Tone.Success;
                }
            }
            catch (Exception ex)
            {
                _variablesStatus.Show($"Scan error: {ex.Message}");
                _variablesStatus.Tone = Tone.Error;
            }

            _isScanning = false;
        }

        private void RefreshVariablesList()
        {
            if (_variablesList == null) return;
            _variablesList.Clear();

            var definitions = VariableManager.Definitions;
            if (definitions.Count == 0) return;

            // Refresh values
            VariableManager.RefreshValues();

            for (int i = 0; i < definitions.Count; i++)
            {
                var def = definitions[i];
                int stableId = def.Id;
                string currentVal = VariableManager.GetValue(stableId);

                var row = Stacks.Horizontal(_variablesList.Rows, "Var_" + stableId, spacing: 5, pad: Pad.All(2),
                    minHeight: UIStyles.RowHeightNormal);

                // Info
                var infoCol = Stacks.Vertical(row, "Info", fill: Fill.Stretch);

                Labels.Create(infoCol, "Name", $"[!STR*{stableId}] {def.Name}", TextRole.Small,
                    policy: TextPolicy.Excluded, fill: Fill.Stretch).Bold = true;

                string pathStr = $"{def.ClassName}.{def.FieldPath}";
                string valStr = currentVal != null ? $" = \"{currentVal}\"" : " = (not resolved)";
                Labels.Create(infoCol, "Detail", pathStr + valStr, TextRole.Caption,
                    tone: currentVal != null ? Tone.Secondary : Tone.Warning, policy: TextPolicy.Excluded, fill: Fill.Stretch);

                // Remove button
                int capturedId = stableId;
                var removeBtn = Buttons.Secondary(row, "Remove_" + stableId, "X", 30);
                removeBtn.Clicked += () =>
                {
                    VariableManager.RemoveVariable(capturedId);
                    TranslatorCore.SaveCache();
                    RefreshVariablesList();
                    _variablesStatus.Say("Variable removed");
                    _variablesStatus.Tone = Tone.Secondary;
                };
            }

            _variablesList.Filled();
        }

        #endregion

        #region Panel Lifecycle

        public override void SetActive(bool active)
        {
            bool wasActive = Enabled;
            base.SetActive(active);
            if (active && !wasActive)
            {
                LoadCurrentState();

                // Keeps the window from resizing when the visitor switches tabs — both rows of
                // them, since the font settings carry their own
                KeepPanelHeightAcrossTabs(_tabBar);
                KeepPanelHeightAcrossTabs(_fontsSubTabBar);
            }
            else if (!active && wasActive)
            {
                // Clear font highlight when closing the panel
                TranslatorScanner.ClearHighlight();
                ResetHighlightButton();
            }
        }

        public override void Update()
        {
            base.Update();

            // Poll state changes to update Apply button text (IL2CPP-safe, no AddListener)
            if (Enabled)
            {
                UpdateApplyButtonText();
            }
        }

        /// <summary>
        /// Reloads UI state from current config (debug toggles mainly).
        /// Called externally when a hotkey flips the config flag, so the panel stays in sync.
        /// Also resets the dirty-tracking snapshot (the hotkey already saved the config).
        /// </summary>
        /// <summary>
        /// Rebuild everything this panel shows, because the translation under it has been replaced.
        ///
        /// 🔴 **Each list is otherwise built when ITS tab is opened, and never again.** So a
        /// download, a merge or a backup put back left whatever was on screen describing a file
        /// that is no longer there — and the lists are the fonts, the font rules, the exclusions,
        /// the images and the variables, which is to say everything a translation carries besides
        /// its lines.
        ///
        /// ⚠ Seen on a real install (2026-09-08): a Chinese→English translation restored over a
        /// Chinese→French one. The game was right — the log says the image was taken back off the
        /// scene — and the Images tab went on listing it, because nobody had told the panel.
        /// </summary>
        public void RefreshFromTranslation()
        {
            RefreshFromConfig();
            RefreshFontsList();
            RefreshFontOverridesList();
            RefreshExclusionsList();
            RefreshImageReplacementsList();
            RefreshVariablesList();
        }

        public void RefreshFromConfig()
        {
            if (_enableFontReplacementToggle != null)
                _enableFontReplacementToggle.IsOn = TranslatorCore.Config.enable_font_replacement;
            if (_enableImageReplacementToggle != null)
                _enableImageReplacementToggle.IsOn = TranslatorCore.Config.enable_image_replacement;

            UpdateApplyButtonText();
        }

        private void OnEnableFontReplacementChanged(bool enabled)
        {
            // Just notify the Apply button — actual application happens on Apply.
            UpdateApplyButtonText();
        }

        private void OnEnableImageReplacementChanged(bool enabled)
        {
            UpdateApplyButtonText();
        }

        private void LoadCurrentState()
        {
            // Debug toggles
            if (_enableFontReplacementToggle != null)
                _enableFontReplacementToggle.IsOn = TranslatorCore.Config.enable_font_replacement;
            if (_enableImageReplacementToggle != null)
                _enableImageReplacementToggle.IsOn = TranslatorCore.Config.enable_image_replacement;
            _pendingAtlasSize = TranslatorCore.Config.max_font_atlas_size;

            // Refresh UI lists
            try { RefreshExclusionsList(); }
            catch (Exception ex) { TranslatorCore.LogWarning($"[TranslationParametersPanel] RefreshExclusionsList failed: {ex.Message}"); }

            // Opening the panel is the other moment a fresh ranking is expected.
            InvalidateFontOrder();
            try { RefreshFontsList(); }
            catch (Exception ex) { TranslatorCore.LogWarning($"[TranslationParametersPanel] RefreshFontsList failed: {ex.Message}"); }

            // Capture initial font settings for change tracking
            _initialFontSettings.Clear();
            _pendingFontSettings.Clear();
            try
            {
                foreach (var fontInfo in FontManager.GetDetectedFontsInfo())
                {
                    var settings = FontManager.GetFontSettings(fontInfo.Name);
                    var enabled = settings?.enabled ?? true;
                    var fallback = settings?.fallback;
                    var sizePercent = FontManager.GetFontSizePercent(fontInfo.Name);  // deliberate percent
                    var scaleAuto = settings?.scale_auto ?? false;
                    var mirrorRtl = !string.Equals(settings?.rtl_alignment, "keep", StringComparison.OrdinalIgnoreCase);
                    _initialFontSettings[fontInfo.Name] = (enabled, fallback, sizePercent, scaleAuto, mirrorRtl);
                }
            }
            catch (Exception ex) { TranslatorCore.LogWarning($"[TranslationParametersPanel] Font settings capture failed: {ex.Message}"); }

            // Capture initial exclusions for change tracking
            _initialExclusions.Clear();
            _pendingExclusionAdds.Clear();
            _pendingExclusionRemoves.Clear();
            foreach (var pattern in TranslatorCore.UserExclusions)
            {
                _initialExclusions.Add(pattern);
            }

            // Capture initial font overrides for change tracking
            InitPendingFontOverrides();
            try { RefreshFontOverridesList(); }
            catch (Exception ex) { TranslatorCore.LogWarning($"[TranslationParametersPanel] RefreshFontOverridesList failed: {ex.Message}"); }

            UpdateApplyButtonText();
        }

        #endregion

        #region Apply / Change Tracking

        private void OnApplyClicked()
        {
            int changes = CountPendingChanges();
            if (changes > 0)
            {
                ApplySettings();
            }
            else
            {
                // No changes - just close
                SetActive(false);
            }
        }

        private void ApplySettings()
        {
            TranslatorCore.LogInfo("[TranslationParametersPanel] Applying settings...");
            try
            {
                // Apply pending font changes
                if (_pendingFontSettings.Count > 0)
                    TranslatorCore.SetMetadataDirty();
                foreach (var kvp in _pendingFontSettings)
                {
                    string fontName = kvp.Key;
                    var pending = kvp.Value;

                    // enabled/fallback always applied (does not touch size_percent/scale_auto).
                    FontManager.UpdateFontSettings(fontName, pending.enabled, pending.fallback);

                    // Auto design-scale toggle (orthogonal to the deliberate percent). SetFontAutoScale
                    // recompensates the shadow in place (no revert/re-scan).
                    bool initialAuto = _initialFontSettings.TryGetValue(fontName, out var init0) && init0.scaleAuto;
                    if (pending.scaleAuto != initialAuto)
                        FontManager.SetFontAutoScale(fontName, pending.scaleAuto);

                    // Deliberate size percent — pushed only when the slider actually moved (editing
                    // only fallback/enabled must not touch the size). UpdateFontScale also recompensates
                    // the shadow in place.
                    if (Math.Abs(pending.sizePercent - FontManager.GetFontSizePercent(fontName)) > 0.001f)
                        FontManager.UpdateFontScale(fontName, pending.sizePercent);

                    // RTL alignment choice — pushed only when it moved; null is the default
                    // (mirror), "keep" the deliberate opt-out, both shared with the translation.
                    bool initialMirror = !_initialFontSettings.TryGetValue(fontName, out var init1)
                                         || init1.mirrorRtl;
                    if (pending.mirrorRtl != initialMirror)
                        FontManager.SetFontRtlAlignment(fontName, pending.mirrorRtl ? null : "keep");
                }

                // Apply pending exclusion changes
                foreach (var pattern in _pendingExclusionAdds)
                {
                    TranslatorCore.AddExclusion(pattern);
                }
                foreach (var pattern in _pendingExclusionRemoves)
                {
                    TranslatorCore.RemoveExclusion(pattern);
                }

                // Apply font overrides
                bool fontOverridesChanged = HasFontOverrideChanges();
                if (fontOverridesChanged)
                {
                    TranslatorCore.SetFontOverrides(KeptFontOverrides());
                }

                // Apply behavior settings
                if (_typewritingDetectionToggle != null)
                    TranslatorCore.TypewritingDetection = _typewritingDetectionToggle.IsOn;
                if (_concatDetectionToggle != null)
                    TranslatorCore.ConcatDetection = _concatDetectionToggle.IsOn;

                // Apply debug toggles — only act when the value actually changed, to avoid
                // unnecessarily restoring/applying when the user didn't touch these.
                if (_enableFontReplacementToggle != null)
                {
                    bool fontEnabled = _enableFontReplacementToggle.IsOn;
                    if (fontEnabled != TranslatorCore.Config.enable_font_replacement)
                    {
                        TranslatorCore.Config.enable_font_replacement = fontEnabled;
                        if (!fontEnabled)
                            FontManager.RestoreAllOriginalFonts();
                    }
                }

                if (_enableImageReplacementToggle != null)
                {
                    bool imgEnabled = _enableImageReplacementToggle.IsOn;
                    if (imgEnabled != TranslatorCore.Config.enable_image_replacement)
                    {
                        TranslatorCore.Config.enable_image_replacement = imgEnabled;
                        if (imgEnabled)
                            ImageReplacer.ApplyToScene();
                        else
                            ImageReplacer.RestoreAllOriginalImages();
                    }
                }

                // Apply font render quality (sharpness). If it changed, rebuild replacement fonts
                // at the new resolution: revert components + drop derived assets + invalidate the
                // source atlas — the ForceRefreshAllText below then re-applies, which re-rasterizes
                // via the .gen budget check. Reuses the proven toggle-off/on paths to stay clear of
                // the fragile font-application timing (issue #21).
                if (_fontAtlasSizeDropdown != null && _pendingAtlasSize != TranslatorCore.Config.max_font_atlas_size)
                {
                    TranslatorCore.Config.max_font_atlas_size = _pendingAtlasSize;
                    try
                    {
                        FontManager.RestoreAllOriginalFonts();
                        FontManager.ClearCreatedFontAssets();
                        CustomFontLoader.InvalidateLoadedFonts();
                    }
                    catch (Exception ex)
                    {
                        TranslatorCore.LogWarning($"[TranslationParametersPanel] Font sharpness rebuild failed: {ex.Message}");
                    }
                }

                TranslatorCore.SaveConfig();
                TranslatorCore.SaveCache(); // saves _settings to translations.json

                // Force refresh all text to apply new settings (fonts, translations, overrides)
                // This re-triggers ProcessTextPatchPrefix for all components, which:
                // - Re-evaluates font override patterns (ApplyTemporaryScale)
                // - Re-applies font scale via ApplyFontScale (uses per-component overrides)
                // reapplyAllScales: discrete Apply — re-derive size for components the game doesn't
                // re-trigger so a changed scale/enable/override lands everywhere (issue #21).
                TranslatorScanner.ForceRefreshAllText(reapplyAllScales: true);

                // Update initial font settings
                _initialFontSettings.Clear();
                foreach (var fontInfo in FontManager.GetDetectedFontsInfo())
                {
                    var settings = FontManager.GetFontSettings(fontInfo.Name);
                    _initialFontSettings[fontInfo.Name] = (settings?.enabled ?? true, settings?.fallback, FontManager.GetFontSizePercent(fontInfo.Name), settings?.scale_auto ?? false,
                        !string.Equals(settings?.rtl_alignment, "keep", StringComparison.OrdinalIgnoreCase));
                }
                _pendingFontSettings.Clear();

                // Update initial exclusions
                _initialExclusions.Clear();
                foreach (var pattern in TranslatorCore.UserExclusions)
                {
                    _initialExclusions.Add(pattern);
                }
                _pendingExclusionAdds.Clear();
                _pendingExclusionRemoves.Clear();

                // Update initial font overrides
                InitPendingFontOverrides();

                // Refresh lists to show applied state
                RefreshFontsList();
                RefreshExclusionsList();
                RefreshFontOverridesList();

                UpdateApplyButtonText();

                TranslatorCore.LogInfo("[TranslationParametersPanel] Settings applied successfully");
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[TranslationParametersPanel] Failed to apply settings: {e.Message}");
            }
        }

        /// <summary>
        /// Counts how many settings differ from their initial values.
        /// </summary>
        /// <summary>
        /// How many things Apply will act on — counted AND marked in one pass, from what the
        /// fields and the list rows registered (see PendingMarks).
        /// </summary>
        private int CountPendingChanges() => Pending.Refresh();

        /// <summary>
        /// Updates the Apply button text based on pending changes count.
        /// Shows "Apply (x)" when there are changes, "Close" when there are none.
        /// </summary>
        private bool HasFontOverrideChanges()
        {
            if (_removedFontOverrides.Count > 0)
                return true;
            if (_pendingFontOverrides.Count != _initialFontOverrides.Count)
                return true;

            for (int i = 0; i < _pendingFontOverrides.Count; i++)
            {
                var p = _pendingFontOverrides[i];
                var o = _initialFontOverrides[i];
                if (p.match != o.match ||
                    p.replacement != o.replacement ||
                    Math.Abs(p.size_multiplier - o.size_multiplier) > 0.001f ||
                    p.enabled != o.enabled ||
                    !string.Equals(p.rtl_alignment, o.rtl_alignment, StringComparison.OrdinalIgnoreCase) ||
                    p.comment != o.comment)
                    return true;
            }
            return false;
        }

        private void UpdateApplyButtonText()
        {
            if (_applyBtn == null) return;

            int changes = CountPendingChanges();
            // Translated at set-time (cache/placeholder-aware) — no race with the async pipeline.
            _applyBtn.Label = changes > 0 ? $"Apply ({changes})" : "Close";
        }

        #endregion
    }
}
