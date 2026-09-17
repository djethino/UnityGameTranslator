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
        /// <summary>The screen as a document — common/spec/screens/tools.json — read once; the base's constructor reads the sizes below through it.</summary>
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("tools");

        /// <summary>What the builder made of the document: every piece by name.</summary>
        private BuiltScreen _screen;

        public override string Name => Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

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

        /// <summary>
        /// True while a row is being filled from what is stored. Writing a value into a piece fires
        /// its act exactly as a person would, and the acts say what they did on the status line —
        /// which, at build, would announce every font as just changed.
        /// </summary>
        private bool _fillingRows;

        // Failures — the lines the AI gave up on this session, settled one by one
        private ScrollList _failuresList, _attemptsList;
        private Host _failureEditor, _failExcludeRow;
        private LabelHandle _failKeyLabel, _failElementLabel, _failStatus;
        private FieldHandle _failInput;
        private FailedLine _failure;   // the one open in the editor, or null

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

        /// <summary>
        /// The screen is tools.json; this builds it and keeps hold of what the code writes, fills
        /// or reads. What the document carries, and why — a document has no comments:
        /// - five tabs in the fixed header, their contents in the scrolling body; the Fonts tab
        ///   holds nothing of its own — a second row of tabs (Global, Overrides) sits in a host in
        ///   the header, shown only while Fonts is open, and its contents go INTO the Fonts tab
        ///   (`contentsIn`), so the scrollbar covers the settings and not the switcher above them;
        /// - every list states a preferred height as well as a floor (the base's
        ///   ScrollingListHeightRule): the panel sizes itself to the content's preferred height
        ///   exactly, so a list asking for its minimum leaves it no slack — one pixel over and it
        ///   grows its own scrollbar next to the list's; the "find" lists start hidden and do not
        ///   take spare height;
        /// - the two editor buttons carry the Local scope: both write this machine's file and
        ///   publish nothing;
        /// - the sharpness choices are the code's (`options: code`), bounded by the GPU.
        /// The rows of every list are built by the code from what it lists.
        /// </summary>
        protected override void ConstructPanelContent()
        {
            Layout(out var body, out var footer, Doc.Width - 40);
            _helpZone = CreateHelpZone(footer, Doc.Help);
            _screen = ScreenBuilder.Build(Doc, body, footer, ActOf, header: FixedHeader(), help: _helpZone);

            _tabBar = _screen.Tabs("Tabs");
            _fontsSubTabHost = _screen.Host("FontsSubTabHost");
            _fontsSubTabBar = _screen.Tabs("FontsSubTabs");

            // The sub-tab buttons belong to the chrome, shown only while the Fonts tab is open.
            _tabBar.OnTabChanged += (_, tabName) => _fontsSubTabHost.Visible = tabName == "Fonts";
            _fontsSubTabHost.Visible = _tabBar.SelectedName == "Fonts";

            // Tools
            _browserEditorBtn = _screen.Button("BrowserEditorBtn");
            _browserEditorStatus = _screen.Label("BrowserEditorStatus");
            _typewritingDetectionToggle = _screen.Toggle("TypewritingToggle");
            _concatDetectionToggle = _screen.Toggle("ConcatToggle");

            // Exclusions
            _manualPatternInput = _screen.Field("PatternInput");
            _findByValueInput = _screen.Field("FindValueInput");
            _findResultsList = _screen.List("FindResultsScroll");
            _exclusionsList = _screen.List("ExclusionsScroll");
            _exclusionsStatus = _screen.Label("ExclusionsStatus");

            // Failures
            _failuresList = _screen.List("FailuresScroll");
            _attemptsList = _screen.List("AttemptsScroll");
            _failureEditor = _screen.Host("FailureEditor");
            _failExcludeRow = _screen.Host("FailExcludeRow");
            _failKeyLabel = _screen.Label("FailKey");
            _failElementLabel = _screen.Label("FailElement");
            _failInput = _screen.Field("FailInput");
            _failStatus = _screen.Label("FailStatus");
            // Noted from the worker thread, settled from this one: the event marshals.
            TranslatorCore.Failures.Changed += () => TranslatorUIManager.RunOnMainThread(OnFailuresChanged);
            RefreshFailuresList();

            // Fonts — global
            _enableFontReplacementToggle = _screen.Toggle("EnableFontReplacementToggle");
            _fontAtlasSizeDropdown = _screen.Dropdown("FontSharpness");
            _fontsStatus = _screen.Label("FontsStatus");
            _fontsList = _screen.List("FontsScroll");

            // Fonts — overrides
            _fontOverrideFindInput = _screen.Field("FindOverrideInput");
            _fontOverrideFindResultsList = _screen.List("OverrideFindResults");
            _fontOverrideManualInput = _screen.Field("ManualOverrideInput");
            _overridesCountLabel = _screen.Label("OverridesCount");
            _fontOverridesList = _screen.List("OverridesScroll");
            _fontOverrideStatus = _screen.Label("OverrideStatus");

            // Images
            _enableImageReplacementToggle = _screen.Toggle("EnableImageReplacementToggle");
            _imagesList = _screen.List("ImagesScroll");
            _imagesStatus = _screen.Label("ImagesStatus");

            // Variables
            _scanValueInput = _screen.Field("ScanValueInput");
            _scanResultsList = _screen.List("ScanResultsScroll");
            _variablesList = _screen.List("VarsScroll");
            _variablesStatus = _screen.Label("VarsStatus");

            _applyBtn = _screen.Button("ApplyBtn");

            // Font sharpness = max SDF atlas dimension. Higher = crisper when the translation
            // scales text up, at a VRAM cost. LAYOUT-NEUTRAL (text size unchanged). Options are
            // bounded dynamically by the GPU texture limit; pending until Apply, takes effect on
            // the next font rebuild (auto-detected — no manual .gen deletion).
            int maxTex = FontManager.GetMaxTextureSize();
            var sharpOptions = new List<string> { "Auto" };
            foreach (int s in new[] { 4096, 8192, 16384 })
                if (s <= maxTex) sharpOptions.Add(s.ToString());
            int curBudget = TranslatorCore.Config?.max_font_atlas_size ?? 0;
            string sharpInitial = (curBudget > 0 && sharpOptions.Contains(curBudget.ToString()))
                ? curBudget.ToString() : "Auto";
            _pendingAtlasSize = curBudget;
            _fontAtlasSizeDropdown.SetOptions(sharpOptions.ToArray());
            _fontAtlasSizeDropdown.SelectedValue = sharpInitial;

            // What the config says, written into the boxes.
            _typewritingDetectionToggle.IsOn = TranslatorCore.TypewritingDetection;
            _concatDetectionToggle.IsOn = TranslatorCore.ConcatDetection;
            _enableFontReplacementToggle.IsOn = TranslatorCore.Config.enable_font_replacement;
            _enableImageReplacementToggle.IsOn = TranslatorCore.Config.enable_image_replacement;

            RefreshBrowserEditorUI();
            RefreshFontOverridesList();
            RefreshImageReplacementsList();
            RefreshVariablesList();

            // Clear font highlight when leaving the Fonts tab
            _tabBar.OnTabChanged += (index, name) =>
            {
                if (name != "Fonts")
                {
                    TranslatorScanner.ClearHighlight();
                    ResetHighlightButton();
                }
            };

            RegisterPendingFields();
        }

        /// <summary>What each verb the document asks for does. A verb with no answer here fails at construction, not at the click.</summary>
        private Action ActOf(string act)
        {
            switch (act)
            {
                case "textEditor": return OnStartTextEditorClicked;
                case "browserEditor": return OnBrowserEditorClicked;
                case "typewritingChanged":
                case "concatChanged": return UpdateApplyButtonText;
                case "startInspector": return OnStartInspectorClicked;
                case "addPattern": return OnAddManualPatternClicked;
                case "findByValue": return OnFindByValueClicked;
                case "failSave": return OnFailSaveClicked;
                case "failRetranslate": return OnFailRetranslateClicked;
                case "failSkip": return OnFailSkipClicked;
                case "failExcludeElement": return OnFailExcludeElementClicked;
                case "failExcludePattern": return OnFailExcludePatternClicked;
                case "fontReplacementChanged": return OnEnableFontReplacementChanged;
                case "sharpnessChanged": return OnFontSharpnessChanged;
                // Explicit user request: this is the one place the ranking is allowed to re-rank.
                case "refreshFonts": return () => { InvalidateFontOrder(); RefreshFontsList(); };
                case "overrideInspector": return OnStartFontOverrideInspector;
                case "findOverride": return OnFindForFontOverride;
                case "addOverride": return OnAddManualFontOverride;
                case "imageReplacementChanged": return OnEnableImageReplacementChanged;
                case "imageInspector": return OnStartImageInspectorClicked;
                case "loadAll": return OnLoadAllReplacementsClicked;
                case "scan": return OnScanClicked;
                case "cancel": return () => SetActive(false);
                case "apply": return OnApplyClicked;
                default: return null;
            }
        }

        /// <summary>Pending only — applied (and fonts rebuilt) on Apply, like every other setting.</summary>
        private void OnFontSharpnessChanged()
        {
            string val = _fontAtlasSizeDropdown.SelectedValue;
            _pendingAtlasSize = (val == "Auto" || !int.TryParse(val, out int b)) ? 0 : b;
            UpdateApplyButtonText();
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

        /// <summary>
        /// Open panel and switch directly to the Exclusions tab.
        /// Called when returning from InspectorPanel.
        /// </summary>
        public void OpenOnExclusionsTab()
        {
            SetActive(true);
            _tabBar?.SelectTab("Exclusions");
        }

        public void OpenOnFailuresTab()
        {
            SetActive(true);
            _tabBar?.SelectTab("Failures");
            RefreshFailuresList();
        }

        // ── Failures ──────────────────────────────────────────────────────

        private void OnFailuresChanged()
        {
            RefreshFailuresList();
            if (_failure != null && !TranslatorCore.Failures.Holds(_failure.Key))
            {
                // Settled elsewhere — translated after all, or written from the inspector.
                CloseFailureEditor();
                _failStatus.Say("Line settled");
                _failStatus.Tone = Tone.Success;
            }
        }

        private void RefreshFailuresList()
        {
            if (_failuresList == null) return;
            _failuresList.Clear();
            foreach (var line in TranslatorCore.Failures.All)
            {
                var captured = line;
                var row = _screen.Instantiate("FailureRow", _failuresList.Rows,
                    act => act == "pick" ? (Action)(() => OpenFailure(captured)) : null);
                row.Say("source", OneLine(captured.Source ?? captured.Key, 90));
                row.Say("attempts", Tr($"{captured.Attempts.Count} attempts"));
            }
            _failuresList.Filled();
        }

        /// <summary>Game text on one line, for a list row: line breaks would make the row as tall as the text.</summary>
        private static string OneLine(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string flat = text.Replace("\r", " ").Replace("\n", " ");
            return flat.Length > max ? flat.Substring(0, max) + "…" : flat;
        }

        private void OpenFailure(FailedLine line)
        {
            _failure = line;
            _failKeyLabel.Show(line.Source ?? line.Key);

            // The exclusion buttons need an element; the worker only knows one once the text has
            // been shown in this session, which a line failed at launch may not have been yet.
            bool known = line.Elements.Count > 0;
            _failElementLabel.Show(known
                ? string.Join("\n", line.Elements)
                : Tr("Element not seen yet: it is known once the text shows in-game"));
            _failExcludeRow.Visible = known;

            _attemptsList.Clear();
            foreach (var attempt in line.Attempts)
            {
                var captured = attempt;
                var row = _screen.Instantiate("AttemptRow", _attemptsList.Rows,
                    act => act == "use" ? (Action)(() => { _failInput.Text = captured.Value ?? ""; }) : null);
                row.Say("value", captured.Value ?? "");
                row.Say("errors", string.Join("; ", captured.Errors));
            }
            _attemptsList.Filled();

            _failInput.Text = "";
            _failStatus.Say("");
            _failureEditor.Visible = true;
        }

        private void CloseFailureEditor()
        {
            _failure = null;
            _failureEditor.Visible = false;
        }

        private void OnFailSaveClicked()
        {
            if (_failure == null) return;
            string value = _failInput.Text;
            if (string.IsNullOrEmpty(value))
            {
                _failStatus.Say("Enter a translation first");
                _failStatus.Tone = Tone.Warning;
                return;
            }
            // The same check the inspector's Save makes: a placeholder missing here is exactly
            // what the AI was refused for.
            string broken = TranslatorCore.ValidateEditedPlaceholders(_failure.Key, value);
            if (broken != null)
            {
                _failStatus.Say(broken);
                _failStatus.Tone = Tone.Error;
                return;
            }

            // The write door settles the line (Failures.Remove inside it).
            TranslatorCore.SetTranslationFromEditor(_failure.Key, value, "H");
            CloseFailureEditor();
            _failStatus.Say("Saved as a human translation");
            _failStatus.Tone = Tone.Success;
        }

        private void OnFailRetranslateClicked()
        {
            if (_failure == null) return;
            // The same door as the inspector's Retranslate; it forgets the give-up mark itself. A
            // passing attempt removes the line from this list, a failing one replaces its record.
            if (!TranslatorCore.RemoveTranslationForRetranslate(_failure.Key))
            {
                _failStatus.Say("Translation is switched off — turn it on in Options first");
                _failStatus.Tone = Tone.Warning;
                return;
            }
            _failStatus.Say("Asked again");
            _failStatus.Tone = Tone.Secondary;
        }

        private void OnFailSkipClicked()
        {
            if (_failure == null) return;
            string key = _failure.Key;
            // Kept as the game shows it and no longer asked: the filing of a line the AI declines
            // (Answers.Filing.KeptAsIs), decided here by a person. Until the game changes the
            // text — a new version writes a new key — which is what the exclusion is for.
            TranslatorCore.AddToCache(key, key, "S");
            TranslatorCore.SaveCache();
            TranslatorCore.Failures.Remove(key);
            CloseFailureEditor();
            _failStatus.Say("Skipped: kept as the game shows it");
            _failStatus.Tone = Tone.Success;
        }

        private void OnFailExcludeElementClicked() => ExcludeFailedElements(path => path);

        private void OnFailExcludePatternClicked() => ExcludeFailedElements(path => "**/" + LeafOf(path));

        /// <summary>
        /// Through the one door every exclusion takes on this screen — pending until Apply, listed
        /// in Exclusions with the added mark — never straight into the file as the inspector does:
        /// this window applies, so what it queues is seen and undone like any other change.
        /// </summary>
        private void ExcludeFailedElements(Func<string, string> patternOf)
        {
            if (_failure == null || _failure.Elements.Count == 0) return;
            int added = 0;
            foreach (string path in _failure.Elements)
                if (AddPendingExclusion(patternOf(path), quiet: true)) added++;

            TranslatorCore.Failures.Remove(_failure.Key);
            CloseFailureEditor();
            _failStatus.Say(added > 0 ? "Added to Exclusions: applied on Apply" : "Already in Exclusions");
            _failStatus.Tone = Tone.Secondary;
            RefreshExclusionsList();
            UpdateApplyButtonText();
        }

        private static string LeafOf(string path)
        {
            int at = path.LastIndexOf('/');
            return at >= 0 ? path.Substring(at + 1) : path;
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

            if (!AddPendingExclusion(pattern)) return;

            _manualPatternInput.Text = "";
            _exclusionsStatus.Say("Pattern will be added on Apply");
            _exclusionsStatus.Tone = Tone.Secondary;

            RefreshExclusionsList();
            UpdateApplyButtonText();
        }

        /// <summary>
        /// Queues a pattern for Apply — the one door every addition on this screen takes, whether
        /// typed, picked from a search, or taken from a failed line. False when it is already
        /// there; said on the Exclusions status unless <paramref name="quiet"/>.
        /// </summary>
        private bool AddPendingExclusion(string pattern, bool quiet = false)
        {
            bool alreadyExists = TranslatorCore.UserExclusions.Contains(pattern) ||
                                 _pendingExclusionAdds.Contains(pattern);
            bool wasRemoved = _pendingExclusionRemoves.Contains(pattern);

            if (alreadyExists && !wasRemoved)
            {
                if (!quiet)
                {
                    _exclusionsStatus.Say("Pattern already exists");
                    _exclusionsStatus.Tone = Tone.Warning;
                }
                return false;
            }

            // If it was pending removal, just cancel the removal
            if (wasRemoved) _pendingExclusionRemoves.Remove(pattern);
            else _pendingExclusionAdds.Add(pattern);
            return true;
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
                var capturedPath = kvp.Key;
                var row = _screen.Instantiate("FindResult", _findResultsList.Rows, act => act == "pick" ? (Action)(() =>
                {
                    if (!_pendingExclusionAdds.Contains(capturedPath))
                    {
                        _pendingExclusionAdds.Add(capturedPath);
                        RefreshExclusionsList();
                        UpdateApplyButtonText();
                        _exclusionsStatus.Show(Tr("Added:") + $" {capturedPath}");
                        _exclusionsStatus.Tone = Tone.Success;
                    }
                }) : null);
                // Which framework drew it. A UI Toolkit path is a list of USS classes and reads
                // nothing like a GameObject hierarchy — without this, one of the two looks broken.
                row.Say("path", kvp.Key);
                row.Say("engine", kvp.Value);
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
                var capturedPattern = pattern;
                var row = _screen.Instantiate("ExclusionRow", _exclusionsList.Rows, act =>
                {
                    switch (act)
                    {
                        case "undo": return () =>
                        {
                            _pendingExclusionRemoves.Remove(capturedPattern);
                            RefreshExclusionsList();
                            UpdateApplyButtonText();
                        };
                        case "delete": return () => OnDeleteExclusionClicked(capturedPattern);
                        default: return null;
                    }
                });

                // What this row is waiting for is said by the shared mark (green added, red
                // removed) and, for a removal, in words — the row stays until Apply so the
                // removal can be seen and undone, exactly like a change to any other field.
                row.Say("pattern", pattern);
                row.Label("PatternLabel").Tone = isRemoved ? Tone.Muted : Tone.Plain;

                var state = isPending ? PendingState.Added : isRemoved ? PendingState.Removed : PendingState.None;
                Pending.TrackState(row.Root, () => state, "exclusions");

                row.Label("RemovedLabel").Visible = isRemoved;
                row.Button("UndoBtn").Visible = isRemoved;
                row.Button("DeleteBtn").Visible = !isRemoved;
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
                var capturedPath = kvp.Key;
                var row = _screen.Instantiate("FindResult", _fontOverrideFindResultsList.Rows,
                    act => act == "pick" ? (Action)(() => AddFontOverrideForPath("path:" + capturedPath)) : null);
                row.Say("path", kvp.Key);
                row.Say("engine", kvp.Value);
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
            var row = _screen.Instantiate("RemovedOverrideRow", _fontOverridesList.Rows, act => act == "undo" ? (Action)(() =>
            {
                _removedFontOverrides.Remove(rule);
                RefreshFontOverridesList();
                UpdateApplyButtonText();
            }) : null);
            Pending.TrackState(row.Root, () => PendingState.Removed, "overrides");
            row.Say("match", string.IsNullOrEmpty(rule.match) ? "(empty rule)" : rule.match);
        }

        private void CreateFontOverrideRow(int index, FontOverrideRule rule)
        {
            if (_removedFontOverrides.Contains(rule)) { CreateRemovedFontOverrideRow(index, rule); return; }

            // A rule the panel opened with is compared field by field to what it was; a rule
            // added since is one thing waiting as a whole. The lists stay parallel by index —
            // nothing reorders them, and a removal keeps its slot until Apply.
            FontOverrideRule initial = index < _initialFontOverrides.Count ? _initialFontOverrides[index] : null;
            int capturedIndex = index;

            BuiltScreen row = null;
            row = _screen.Instantiate("OverrideRow", _fontOverridesList.Rows, act =>
            {
                switch (act)
                {
                    case "matchChanged": return () =>
                    {
                        if (_fillingRows || capturedIndex >= _pendingFontOverrides.Count) return;
                        _pendingFontOverrides[capturedIndex].match = row.Field("MatchInput").Text;
                        UpdateApplyButtonText();
                    };
                    // Delete: a rule the panel opened with waits for Apply, marked and undoable;
                    // one added since simply goes, there is nothing on disk to take back.
                    case "delete": return () =>
                    {
                        if (capturedIndex >= _pendingFontOverrides.Count) return;
                        if (initial != null) _removedFontOverrides.Add(rule);
                        else _pendingFontOverrides.RemoveAt(capturedIndex);
                        RefreshFontOverridesList();
                        UpdateApplyButtonText();
                    };
                    case "sizeChanged": return () =>
                    {
                        if (_fillingRows) return;
                        float rounded = (float)Math.Round(row.Slider("OverrideSize").Value * 20) / 20f;
                        if (capturedIndex < _pendingFontOverrides.Count)
                        {
                            _pendingFontOverrides[capturedIndex].size_multiplier = rounded;
                            UpdateApplyButtonText();
                        }
                    };
                    case "rtlChanged": return () =>
                    {
                        if (_fillingRows || capturedIndex >= _pendingFontOverrides.Count) return;
                        string selected = row.Dropdown("OverrideRtl").SelectedValue;
                        _pendingFontOverrides[capturedIndex].rtl_alignment =
                            selected == "Mirror" ? "mirror" : selected == "Keep game's" ? "keep" : null;
                        UpdateApplyButtonText();
                    };
                    default: return null;
                }
            });
            if (initial == null) Pending.TrackState(row.Root, () => PendingState.Added, "overrides");

            _fillingRows = true;
            try
            {
                var matchInput = row.Field("MatchInput");
                matchInput.Text = rule.match ?? "";
                if (initial != null)
                    Pending.Track(matchInput, () => (rule.match ?? "") != (initial.match ?? ""), "overrides");

                // Size multiplier — the mod's other "Size:" slider (the Fonts sub-tab's per-font
                // row) reads label → slider → value, and so does this one.
                var sizeSlider = row.Slider("OverrideSize");
                sizeSlider.Value = rule.size_multiplier > 0.001f ? rule.size_multiplier : 1.0f;
                if (initial != null)
                    Pending.Track(sizeSlider, () => Math.Abs(rule.size_multiplier - initial.size_multiplier) > 0.001f, "overrides");

                // RTL alignment for the matched components (only when this translation involves
                // right-to-left text): inherit the font's setting, or force mirror/keep here — the
                // per-rule refinement the bench demanded (one game, mirroring pane next to
                // one-side-built buttons).
                if (_rtlControlsVisible)
                {
                    row.Host("RtlRow").Visible = true;
                    string initialRtl = string.Equals(rule.rtl_alignment, "mirror", StringComparison.OrdinalIgnoreCase) ? "Mirror"
                                      : string.Equals(rule.rtl_alignment, "keep", StringComparison.OrdinalIgnoreCase) ? "Keep game's"
                                      : "Inherit from font";
                    var rtlDropdown = row.Dropdown("OverrideRtl");
                    rtlDropdown.SetOptions(new[] { "Inherit from font", "Mirror", "Keep game's" });
                    rtlDropdown.SelectedValue = initialRtl;
                    _overrideRtlDropdowns.Add(rtlDropdown);
                    if (initial != null)
                        Pending.Track(rtlDropdown.Handle, () => !string.Equals(rule.rtl_alignment, initial.rtl_alignment, StringComparison.OrdinalIgnoreCase), "overrides");
                }
            }
            finally { _fillingRows = false; }
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

        /// <summary>
        /// One detected font, from its template: name and presence, the Translate box and the
        /// Identify button on the first line; the RTL box when this translation involves
        /// right-to-left text; the fallback picker for fonts that support one; the size slider
        /// with its Auto box for the font types whose design-scale means something.
        /// </summary>
        private void CreateFontRow(FontDisplayInfo fontInfo)
        {
            // Capture values for closure
            string capturedFontName = fontInfo.Name;

            BuiltScreen row = null;
            row = _screen.Instantiate("FontRow", _fontsList.Rows, act =>
            {
                switch (act)
                {
                    case "identify": return () => ToggleFontHighlight(capturedFontName, row.Button("IdentifyBtn"));
                    case "enabledChanged": return () => { if (!_fillingRows) OnFontEnableChanged(capturedFontName, row.Toggle("EnableToggle").IsOn); };
                    case "rtlChanged": return () => { if (!_fillingRows) OnFontRtlAlignChanged(capturedFontName, row.Toggle("RtlMirrorToggle").IsOn); };
                    case "fallbackChanged": return () =>
                    {
                        if (_fillingRows) return;
                        // Markers are display only — what gets stored is the font name
                        string selectedValue = FontManager.StripOptionMarker(row.Dropdown("Fallback").SelectedValue);
                        string fallback = selectedValue == "(None)" ? null : selectedValue;
                        OnFontFallbackChanged(capturedFontName, fallback);
                    };
                    case "scaleChanged": return () => { if (!_fillingRows) OnFontScaleChanged(capturedFontName, (float)Math.Round(row.Slider("Scale").Value, 2)); };
                    case "autoScaleChanged": return () => { if (!_fillingRows) OnFontAutoScaleChanged(capturedFontName, row.Toggle("AutoScale").IsOn); };
                    default: return null;
                }
            });

            _fillingRows = true;
            try
            {
                // Font name and type
                row.Say("font", $"{fontInfo.Name} ({fontInfo.Type})");

                // How present this font is on the screen right now — the figure that tells the user
                // whether a font is worth configuring. -1 means the count couldn't be taken; say
                // nothing rather than show a misleading zero.
                if (fontInfo.SceneCount >= 0)
                {
                    var count = row.Label("SceneCount");
                    row.Say("sceneCount", $"{fontInfo.SceneCount} " + Tr("in scene"));
                    count.Tone = fontInfo.SceneCount > 0 ? Tone.Secondary : Tone.Muted;
                    count.Visible = true;
                }

                // Enable toggle
                var enableToggle = row.Toggle("EnableToggle");
                enableToggle.IsOn = fontInfo.Enabled;
                Pending.Track(enableToggle, () => FontFieldChanged(capturedFontName, (p, i) => p.enabled != i.enabled), "fonts");

                // RTL alignment (only when this translation involves right-to-left text): mirror the
                // component's alignment to follow the reading direction, or keep the game's own —
                // per font and shared with the translation, refinable per rule below.
                if (_rtlControlsVisible)
                {
                    row.Host("RtlRow").Visible = true;
                    var rtlToggle = row.Toggle("RtlMirrorToggle");
                    rtlToggle.IsOn = GetEffectiveFontSettings(capturedFontName).mirrorRtl;
                    Pending.Track(rtlToggle, () => FontFieldChanged(capturedFontName, (p, i) => p.mirrorRtl != i.mirrorRtl), "fonts");
                }

                if (fontInfo.SupportsFallback)
                {
                    row.Host("FallbackRow").Visible = true;
                    FillFallback(row, fontInfo, capturedFontName);
                }
                else
                {
                    // Show hint for non-TMP fonts
                    row.Label("NoFallbackLabel").Visible = true;
                }

                // Size — the DELIBERATE size percent (fit/readability, e.g. a longer cross-script
                // translation vs the HUD). Orthogonal to the auto design-scale: the two combine
                // multiplicatively (Model B). 100% = native. Always active; it does NOT replace the
                // auto design-scale, it applies on top of it.
                var scaleSlider = row.Slider("Scale");
                scaleSlider.Value = Math.Min(2.0f, FontManager.GetFontSizePercent(capturedFontName));
                Pending.Track(scaleSlider, () => FontFieldChanged(capturedFontName, (p, i) => Math.Abs(p.sizePercent - i.sizePercent) > 0.001f), "fonts");

                // Auto design-scale toggle — folds the font's native design-scale into the size as a
                // baseline (so an imported font matches the game's original size), on top of which the
                // slider % still applies. Default ON for freshly detected TMP fonts. HIDDEN for font
                // types where the design-scale has no meaning (UI.Text / non-TMP clone-atlas preserves
                // the game's metrics). Commits on Apply only (UX rule: no immediate application).
                if (FontManager.SupportsDesignScale(fontInfo.Type))
                {
                    var autoToggle = row.Toggle("AutoScale");
                    autoToggle.Visible = true;
                    autoToggle.IsOn = FontManager.GetFontSettings(capturedFontName)?.scale_auto ?? false;
                    Pending.Track(autoToggle, () => FontFieldChanged(capturedFontName, (p, i) => p.scaleAuto != i.scaleAuto), "fonts");
                }
            }
            finally { _fillingRows = false; }
        }

        /// <summary>
        /// The fallback picker of one font: the game's fonts first, then the system's, then the
        /// custom ones — grouped by origin — with the configured fallback found among them.
        /// </summary>
        private void FillFallback(BuiltScreen row, FontDisplayInfo fontInfo, string capturedFontName)
        {
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

            var dropdown = row.Dropdown("Fallback");

            // If no fonts available at all
            if (options.Count <= 1)
            {
                dropdown.Handle.Visible = false;
                row.Label("NoFontsLabel").Visible = true;
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

            dropdown.CategoryProvider = FontManager.GetFontOrigin;
            dropdown.SetOptions(options.ToArray());
            dropdown.SelectedValue = initialValue;

            _fallbackDropdowns.Add(dropdown);
            Pending.Track(dropdown.Handle, () => FontFieldChanged(capturedFontName, (p, i) => p.fallback != i.fallback), "fonts");
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

                var row = _screen.Instantiate("ImageRow", _imagesList.Rows, act => act == "remove" ? (Action)(() =>
                {
                    ImageReplacer.RemoveReplacement(capturedName);
                    TranslatorCore.SaveCache();
                    RefreshImageReplacementsList();
                    _imagesStatus.Show(Tr("Removed:") + $" {capturedName}");
                    _imagesStatus.Tone = Tone.Secondary;
                }) : null);

                row.Say("name", $"{spriteName} ({entry.OriginalWidth}x{entry.OriginalHeight})");

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
                row.Say("status", statusText);
                row.Label("Status").Tone = statusTone;
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
                        // Show the matched value: with partial matches (composed display
                        // strings like "seedA-seedB"), the path alone doesn't tell the
                        // user which piece of the text each candidate holds.
                        string valPreview = (candidate.CurrentValue ?? "").Replace("\r", " ").Replace("\n", " ");
                        if (valPreview.Length > 24) valPreview = valPreview.Substring(0, 24) + "...";
                        string display = $"{candidate.ClassName}.{candidate.FieldPath} = \"{valPreview}\"";
                        if (candidate.IsStatic) display += " (static)";

                        var capturedCandidate = candidate;
                        var row = _screen.Instantiate("ScanCandidate", _scanResultsList.Rows, act => act == "add" ? (Action)(() =>
                        {
                            // Prompt for a name — use the field name as default
                            string varName = capturedCandidate.FieldPath.Split('.').Last();
                            VariableManager.AddVariable(varName, capturedCandidate.ClassName, capturedCandidate.FieldPath);
                            TranslatorCore.SaveCache();
                            RefreshVariablesList();
                            _variablesStatus.Show($"Added: {varName} ({capturedCandidate.ClassName}.{capturedCandidate.FieldPath})");
                            _variablesStatus.Tone = Tone.Success;
                        }) : null);
                        row.Say("label", display);
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

                int capturedId = stableId;
                var row = _screen.Instantiate("VariableRow", _variablesList.Rows, act => act == "remove" ? (Action)(() =>
                {
                    VariableManager.RemoveVariable(capturedId);
                    TranslatorCore.SaveCache();
                    RefreshVariablesList();
                    _variablesStatus.Say("Variable removed");
                    _variablesStatus.Tone = Tone.Secondary;
                }) : null);

                row.Say("name", $"[!STR*{stableId}] {def.Name}");

                string pathStr = $"{def.ClassName}.{def.FieldPath}";
                string valStr = currentVal != null ? $" = \"{currentVal}\"" : " = (not resolved)";
                row.Say("detail", pathStr + valStr);
                row.Label("Detail").Tone = currentVal != null ? Tone.Secondary : Tone.Warning;
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

        private void OnEnableFontReplacementChanged()
        {
            // Just notify the Apply button — actual application happens on Apply.
            UpdateApplyButtonText();
        }

        private void OnEnableImageReplacementChanged()
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
