using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Translation parameters panel with Exclusions, Fonts, Images, and Variables tabs.
    /// Extracted from OptionsPanel to keep options focused on general settings.
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

        // Tab sizing

        // Behavior section
        private Toggle _typewritingDetectionToggle;
        private Toggle _concatDetectionToggle;

        // Exclusions section
        private GameObject _exclusionsListContainer;
        private InputFieldRef _manualPatternInput;
        private Text _exclusionsStatusLabel;

        // Find by value
        private InputFieldRef _findByValueInput;
        private GameObject _findResultsContainer;
        private GameObject _findResultsScrollObj;

        // Pending exclusion changes
        private HashSet<string> _pendingExclusionAdds = new HashSet<string>();
        private HashSet<string> _pendingExclusionRemoves = new HashSet<string>();
        private HashSet<string> _initialExclusions = new HashSet<string>();

        // Fonts section
        private GameObject _fontsListContainer;
        private Text _fontsStatusLabel;
        private Toggle _enableFontReplacementToggle;
        private string[] _systemFonts;
        private List<SearchableDropdown> _fallbackDropdowns = new List<SearchableDropdown>();
        private SearchableDropdown _fontAtlasSizeDropdown;
        private int _pendingAtlasSize;  // font sharpness selection, applied on Apply

        // Pending font changes (fontName -> (enabled, fallback, sizePercent, scaleAuto)).
        // sizePercent = the deliberate size slider (orthogonal to the auto design-scale baseline);
        // scaleAuto = whether the design-scale is folded in. The two combine multiplicatively.
        private Dictionary<string, (bool enabled, string fallback, float sizePercent, bool scaleAuto)> _pendingFontSettings = new Dictionary<string, (bool, string, float, bool)>();
        private Dictionary<string, (bool enabled, string fallback, float sizePercent, bool scaleAuto)> _initialFontSettings = new Dictionary<string, (bool, string, float, bool)>();

        // Images section
        private GameObject _imagesListContainer;
        private Text _imagesStatusLabel;
        private Toggle _enableImageReplacementToggle;

        // Variables section
        private GameObject _variablesListContainer;
        private Text _variablesStatusLabel;
        private InputFieldRef _scanValueInput;
        private GameObject _scanResultsContainer;
        private bool _isScanning;

        // Apply button tracking
        private ButtonRef _applyBtn;

        // Tools tab — browser editor (live edit session)
        private ButtonRef _browserEditorBtn;
        private Text _browserEditorStatus;
        // True while a start or stop round trip is in flight (see OnBrowserEditorClicked)
        private bool _browserEditorBusy;

        // Font highlight tracking
        private string _highlightedFontName = null;
        private ButtonRef _highlightedButton = null;

        public TranslationParametersPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            // Fixed header: tab buttons stay put, only tab content scrolls
            var header = CreateFixedHeader();

            // No big title here — the window title bar already shows "Translation Tools" (redundant).

            // Create tab bar — buttons in the fixed header, contents in the scroll area
            _tabBar = new TabBar();
            _tabBar.CreateUI(header, scrollContent);

            // Create tab contents
            var behaviorTab = _tabBar.AddTab("Tools");
            var exclusionsTab = _tabBar.AddTab("Exclusions");
            var fontsTab = _tabBar.AddTab("Fonts");
            var imagesTab = _tabBar.AddTab("Images");
            var variablesTab = _tabBar.AddTab("Variables");

            // Register tab texts for localization
            foreach (var text in _tabBar.GetTabButtonTexts())
            {
                RegisterUIText(text);
            }

            // Explain what lives behind each tab
            _helpZone?.Describe(_tabBar.GetTabButton("Tools"),
                "Text editors (in-game and browser) and text detection settings");
            _helpZone?.Describe(_tabBar.GetTabButton("Exclusions"),
                "Prevent specific texts or UI elements from being translated");
            _helpZone?.Describe(_tabBar.GetTabButton("Fonts"),
                "Replace the game's fonts when they can't display your language's characters");
            _helpZone?.Describe(_tabBar.GetTabButton("Images"),
                "Replace images that contain baked-in text");
            _helpZone?.Describe(_tabBar.GetTabButton("Variables"),
                "Protect dynamic values (numbers, names) inside translated texts");

            // Host for the Fonts sub-tab buttons: they belong to the chrome, not to the content,
            // so they sit in the fixed header like the main tabs instead of scrolling away with
            // the settings they switch between. Shown only while the Fonts tab is open.
            _fontsSubTabHost = UIFactory.CreateVerticalGroup(header, "FontsSubTabHost",
                false, false, true, true, 0, default, Color.clear);
            UIFactory.SetLayoutElement(_fontsSubTabHost, flexibleWidth: 9999, flexibleHeight: 0);
            _tabBar.OnTabChanged += (_, tabName) => _fontsSubTabHost.SetActive(tabName == "Fonts");
            _fontsSubTabHost.SetActive(_tabBar.SelectedName == "Fonts");

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
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);
            RegisterUIText(cancelBtn.ButtonText);

            _applyBtn = CreatePrimaryButton(buttonRow, "ApplyBtn", "Apply");
            _applyBtn.OnClick += OnApplyClicked;
            // EXCLUDE: code-managed dynamic text ("Apply"/"Close"/"Apply (N)") — async translation
            // would race with UpdateApplyButtonText and break the button. Static labels stay translatable.
            RegisterExcluded(_applyBtn.ButtonText);
        }

        #region Tools Tab (formerly Behavior)

        private void CreateBehaviorTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "ToolsCard", PanelWidth - 60, stretchVertically: true);

            // Text Editor section
            var editorTitle = UIStyles.CreateSectionTitle(card, "TextEditorLabel", "Text Editor");
            RegisterUIText(editorTitle);

            var editorHint = UIStyles.CreateHint(card, "TextEditorHint",
                "Click on any text in-game to edit its translation or retranslate it with AI.");
            RegisterUIText(editorHint);

            var editorBtn = CreatePrimaryButton(card, "TextEditorBtn", "Start Text Editor", PanelWidth - 100);
            editorBtn.OnClick += OnStartTextEditorClicked;
            RegisterUIText(editorBtn.ButtonText);
            _helpZone?.Describe(editorBtn.Component.gameObject,
                "Pick any text on screen to fix its translation without leaving the game");

            UIStyles.CreateSpacer(card, 15);

            // Browser Editor section (live edit session on the website, no account needed)
            var browserEditorTitle = UIStyles.CreateSectionTitle(card, "BrowserEditorLabel", "Browser Editor");
            RegisterUIText(browserEditorTitle);

            var browserEditorHint = UIStyles.CreateHint(card, "BrowserEditorHint",
                "Edit your translation file comfortably in your browser while playing — no account needed, nothing is published. Each save is applied in-game automatically.");
            RegisterUIText(browserEditorHint);

            _browserEditorBtn = CreatePrimaryButton(card, "BrowserEditorBtn", "Edit in browser", PanelWidth - 100);
            _browserEditorBtn.OnClick += OnBrowserEditorClicked;
            RegisterExcluded(_browserEditorBtn.ButtonText);
            _helpZone?.Describe(_browserEditorBtn.Component.gameObject,
                "Open your translation in a browser editor: search, filters, and every save applied in-game live");

            _browserEditorStatus = UIStyles.CreateHint(card, "BrowserEditorStatus", "");
            RegisterExcluded(_browserEditorStatus);
            RefreshBrowserEditorUI();

            UIStyles.CreateSpacer(card, 15);

            // Detection section
            var sectionTitle = UIStyles.CreateSectionTitle(card, "DetectionLabel", "Detection");
            RegisterUIText(sectionTitle);

            var detectionHint = UIStyles.CreateHint(card, "DetectionHint",
                "Control how the mod detects special text patterns. Disable if causing issues with your game.");
            RegisterUIText(detectionHint);

            // Typewriting detection toggle
            var twObj = UIFactory.CreateToggle(card, "TypewritingToggle", out _typewritingDetectionToggle, out var twLabel);
            twLabel.text = " Typewriting detection";
            twLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(twObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(twLabel);
            _helpZone?.Describe(twObj,
                "Detects text that appears letter by letter, like dialogues, and waits for it to settle before translating. Disable if it causes issues.");

            var twHint = UIStyles.CreateHint(card, "TypewritingHint",
                "Text that appears letter by letter (dialogues, cutscenes). Waits for the text to stabilize before translating.");
            RegisterUIText(twHint);

            // Concat detection toggle
            var concatObj = UIFactory.CreateToggle(card, "ConcatToggle", out _concatDetectionToggle, out var concatLabel);
            concatLabel.text = " Procedural text detection";
            concatLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(concatObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(concatLabel);
            _helpZone?.Describe(concatObj,
                "Detects text assembled in parts, like tooltips or item stats, and translates each part for better cache reuse. Disable if it causes issues.");

            var concatHint = UIStyles.CreateHint(card, "ConcatHint",
                "Text built in multiple steps (tooltips, item stats). Translates each part separately for better cache reuse.");
            RegisterUIText(concatHint);

            // Init values
            _typewritingDetectionToggle.isOn = TranslatorCore.TypewritingDetection;
            _concatDetectionToggle.isOn = TranslatorCore.ConcatDetection;

            // Listeners for Apply button
            UIHelpers.AddToggleListener(_typewritingDetectionToggle, (val) => UpdateApplyButtonText());
            UIHelpers.AddToggleListener(_concatDetectionToggle, (val) => UpdateApplyButtonText());
        }

        private void OnStartTextEditorClicked()
        {
            SetActive(false);
            TranslatorUIManager.OpenInspectorPanel(InspectorMode.TextEdit);
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

            StartBrowserEditorSession();
        }

        /// <summary>
        /// Called by TranslatorUIManager when the session ends outside a
        /// direct click (browser closed past grace, ended from the page,
        /// expired server-side, or the Stop button round trip).
        /// </summary>
        public void OnEditSessionEnded(string reason)
        {
            SetBrowserEditorStatus(reason, UIStyles.TextMuted);
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
                UIStyles.StatusSuccess);
            SetBrowserEditorBusy(false);
        }

        private async void StartBrowserEditorSession()
        {
            // Says what is actually slow (the upload) and where the tab will show
            // up, so the wait is understood instead of read as a dead button
            SetBrowserEditorBusy(true, "Opening...");
            SetBrowserEditorStatus(
                "Sending your translation file... your browser opens when it is ready (the tab may appear behind the game).",
                UIStyles.TextSecondary);

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
                        SetBrowserEditorStatus($"Failed to start: {error ?? "unknown error"}", UIStyles.StatusError);
                        SetBrowserEditorBusy(false);
                        return;
                    }

                    string fullUrl = ApiClient.GetMergePreviewFullUrl(url);
                    TranslatorCore.OpenUrlSafe(fullUrl);

                    TranslatorUIManager.StartEditSessionListener(modKey);
                    SetBrowserEditorStatus("Session active — edit in your browser, each save is applied in-game.", UIStyles.StatusSuccess);
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
                    SetBrowserEditorStatus($"Failed to start: {errorMsg}", UIStyles.StatusError);
                    SetBrowserEditorBusy(false);
                });
            }
        }

        private void SetBrowserEditorStatus(string message, Color color)
        {
            if (_browserEditorStatus == null) return;
            _browserEditorStatus.text = message;
            _browserEditorStatus.color = color;
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

            if (_browserEditorBtn?.Component != null)
                _browserEditorBtn.Component.interactable = !busy;

            if (busy)
            {
                if (busyLabel != null && _browserEditorBtn?.ButtonText != null)
                    SetDynamicText(_browserEditorBtn.ButtonText, busyLabel);
                return;
            }

            RefreshBrowserEditorUI();
        }

        private void RefreshBrowserEditorUI()
        {
            if (_browserEditorBtn?.ButtonText != null)
            {
                SetDynamicText(_browserEditorBtn.ButtonText, TranslatorUIManager.IsEditSessionActive
                    ? "Stop browser session"
                    : "Edit in browser");
            }
        }

        #endregion

        #region Exclusions Tab

        private void CreateExclusionsTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "ExclusionsCard", PanelWidth - 60, stretchVertically: true);

            // Header and explanation
            var sectionTitle = UIStyles.CreateSectionTitle(card, "ExclusionsLabel", "UI Exclusions");
            RegisterUIText(sectionTitle);

            var explainHint = UIStyles.CreateHint(card, "ExclusionsHint", "Exclude UI elements from translation (chat windows, player names, etc.). Exclusions are shared when you upload your translation.");
            RegisterUIText(explainHint);

            UIStyles.CreateSpacer(card, 10);

            // Inspector button
            var inspectorBtn = CreatePrimaryButton(card, "InspectorBtn", "Start Inspector Mode", PanelWidth - 100);
            inspectorBtn.OnClick += OnStartInspectorClicked;
            RegisterUIText(inspectorBtn.ButtonText);
            _helpZone?.Describe(inspectorBtn.Component.gameObject,
                "Closes this panel so you can click UI elements in-game to exclude them from translation.");

            var inspectorHint = UIStyles.CreateHint(card, "InspectorHint", "Click on UI elements to exclude them");
            RegisterUIText(inspectorHint);

            UIStyles.CreateSpacer(card, 10);

            // Manual add section
            var manualLabel = UIFactory.CreateLabel(card, "ManualLabel", "Add pattern manually:", TextAnchor.MiddleLeft);
            manualLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(manualLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(manualLabel);

            var addRow = UIStyles.CreateFormRow(card, "AddRow", UIStyles.InputHeight, 5);

            _manualPatternInput = UIFactory.CreateInputField(addRow, "PatternInput", "e.g., **/ChatPanel/**");
            UIFactory.SetLayoutElement(_manualPatternInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_manualPatternInput.Component.gameObject, UIStyles.InputBackground);
            _helpZone?.Describe(_manualPatternInput.Component.gameObject,
                "Type a hierarchy path pattern to exclude. Use ** for any depth and * for a single level.");

            var addBtn = CreateSecondaryButton(addRow, "AddBtn", "Add", 60);
            addBtn.OnClick += OnAddManualPatternClicked;
            RegisterUIText(addBtn.ButtonText);
            _helpZone?.Describe(addBtn.Component.gameObject,
                "Adds the typed pattern to the exclusion list. Takes effect on Apply.");

            var patternHint = UIStyles.CreateHint(card, "PatternHint", "Use ** for any depth, * for single level");
            RegisterUIText(patternHint);

            UIStyles.CreateSpacer(card, 10);

            // Find by value section
            var findLabel = UIFactory.CreateLabel(card, "FindLabel", "Find by text content:", TextAnchor.MiddleLeft);
            findLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(findLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(findLabel);

            var findRow = UIStyles.CreateFormRow(card, "FindRow", UIStyles.InputHeight, 5);

            _findByValueInput = UIFactory.CreateInputField(findRow, "FindValueInput", "Enter text visible in-game...");
            UIFactory.SetLayoutElement(_findByValueInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_findByValueInput.Component.gameObject, UIStyles.InputBackground);
            _helpZone?.Describe(_findByValueInput.Component.gameObject,
                "Type text visible in-game to locate the UI element that shows it, then exclude it.");

            var findBtn = CreateSecondaryButton(findRow, "FindBtn", "Find", 60);
            findBtn.OnClick += OnFindByValueClicked;
            RegisterUIText(findBtn.ButtonText);
            _helpZone?.Describe(findBtn.Component.gameObject,
                "Searches the scene for UI components displaying the entered text.");

            var findHint = UIStyles.CreateHint(card, "FindHint", "Find which UI component displays this text, then exclude it");
            RegisterUIText(findHint);

            // Find results (hidden until search)
            var findResultsScroll = UIFactory.CreateScrollView(card, "FindResultsScroll", out var findResultsContent, out _);
            UIFactory.SetLayoutElement(findResultsScroll, minHeight: 0, preferredHeight: 80, flexibleHeight: 0);
            _findResultsContainer = findResultsContent;
            _findResultsScrollObj = findResultsScroll;
            findResultsScroll.SetActive(false);

            UIStyles.CreateSpacer(card, 10);

            // Current exclusions list
            var listLabel = UIFactory.CreateLabel(card, "ListLabel", "Current Exclusions:", TextAnchor.MiddleLeft);
            listLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(listLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(listLabel);

            // Scrollable container for exclusions
            var scrollObj = UIFactory.CreateScrollView(card, "ExclusionsScroll", out var scrollContent, out var scrollbar);
            UIFactory.SetLayoutElement(scrollObj, minHeight: 200, preferredHeight: 200, flexibleHeight: 9999, flexibleWidth: 9999);
            UIStyles.SetBackground(scrollObj, UIStyles.InputBackground);
            UIFactory.ConfigureAutoHideScrollbar(scrollObj);

            _exclusionsListContainer = scrollContent;

            // Status label
            _exclusionsStatusLabel = UIFactory.CreateLabel(card, "ExclusionsStatus", "", TextAnchor.MiddleLeft);
            _exclusionsStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_exclusionsStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
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
            if (UIRoot != null) UIRoot.transform.SetAsLastSibling();
        }

        public void OpenOnFontOverridesTab()
        {
            SetActive(true);
            _tabBar?.SelectTab("Fonts");
            _fontsSubTabBar?.SelectTab("Overrides");
            RefreshFontOverridesList();
            if (UIRoot != null) UIRoot.transform.SetAsLastSibling();
        }

        private void OnStartInspectorClicked()
        {
            // Close panel and open inspector panel (exclusion mode)
            SetActive(false);
            TranslatorUIManager.OpenInspectorPanel();
        }

        private void OnStartImageInspectorClicked()
        {
            // Close panel and open inspector panel (image replacement mode)
            SetActive(false);
            TranslatorUIManager.OpenInspectorPanel(InspectorMode.BitmapReplace);
        }

        private void OnAddManualPatternClicked()
        {
            string pattern = _manualPatternInput.Text?.Trim();

            if (string.IsNullOrEmpty(pattern))
            {
                SetDynamicText(_exclusionsStatusLabel, "Enter a pattern first");
                _exclusionsStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            // Check if already exists (in current list or pending adds)
            bool alreadyExists = TranslatorCore.UserExclusions.Contains(pattern) ||
                                 _pendingExclusionAdds.Contains(pattern);
            bool wasRemoved = _pendingExclusionRemoves.Contains(pattern);

            if (alreadyExists && !wasRemoved)
            {
                SetDynamicText(_exclusionsStatusLabel, "Pattern already exists");
                _exclusionsStatusLabel.color = UIStyles.StatusWarning;
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
            SetDynamicText(_exclusionsStatusLabel, "Pattern will be added on Apply");
            _exclusionsStatusLabel.color = UIStyles.TextSecondary;

            RefreshExclusionsList();
            UpdateApplyButtonText();
        }

        private void OnFindByValueClicked()
        {
            string searchValue = _findByValueInput?.Text?.Trim();
            if (string.IsNullOrEmpty(searchValue))
            {
                SetDynamicText(_exclusionsStatusLabel, "Enter text to search for");
                _exclusionsStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            // Show results container
            _findResultsScrollObj.SetActive(true);

            // Clear previous results
            int childCount = _findResultsContainer.transform.childCount;
            for (int i = childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_findResultsContainer.transform.GetChild(i).gameObject);

            // Scan all text components from the scanner cache
            var found = new List<KeyValuePair<string, string>>();
            var seenPaths = new HashSet<string>();

            try
            {
                // Search ALL text components in the scene (not just scanner cache)
                // This finds hidden/buffer components that the scanner doesn't track
                var textTypes = new List<Type>();
                if (TypeHelper.UI_TextType != null) textTypes.Add(TypeHelper.UI_TextType);
                if (TypeHelper.TMP_TextType != null) textTypes.Add(TypeHelper.TMP_TextType);

                foreach (var textType in textTypes)
                {
                    var allComponents = TypeHelper.FindAllObjectsOfType(textType);
                    if (allComponents == null) continue;

                    foreach (var obj in allComponents)
                    {
                        if (obj == null) continue;
                        try
                        {
                            string text = TypeHelper.GetText(obj);
                            if (string.IsNullOrEmpty(text)) continue;
                            if (!text.Contains(searchValue)) continue;

                            Component comp = obj as Component;
                            if (comp == null)
                                comp = TypeHelper.Il2CppCast(obj, typeof(Component)) as Component;
                            if (comp == null || comp.gameObject == null) continue;

                            // Skip our own mod UI
                            if (TranslatorCore.ShouldSkipTranslation(comp)) continue;

                            string path = TranslatorCore.GetGameObjectPath(comp.gameObject);
                            if (seenPaths.Contains(path)) continue;
                            seenPaths.Add(path);

                            string snippet = text.Length > 50 ? text.Substring(0, 50) + "..." : text;
                            found.Add(new KeyValuePair<string, string>(path, snippet));
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (found.Count == 0)
            {
                var emptyLabel = UIFactory.CreateLabel(_findResultsContainer, "NoResults",
                    "No UI component found with this text.", TextAnchor.MiddleCenter);
                emptyLabel.color = UIStyles.TextMuted;
                emptyLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(emptyLabel.gameObject, minHeight: 30, flexibleWidth: 9999);

                SetDynamicText(_exclusionsStatusLabel, "No results");
                _exclusionsStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            foreach (var kvp in found)
            {
                var row = UIFactory.CreateUIObject("FindResult", _findResultsContainer);
                UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(row, false, false, true, true, 5, 2, 2, 2, 2);
                UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

                var label = UIFactory.CreateLabel(row, "Path", kvp.Key, TextAnchor.MiddleLeft);
                label.fontSize = UIStyles.FontSizeSmall;
                UIFactory.SetLayoutElement(label.gameObject, flexibleWidth: 9999);

                var capturedPath = kvp.Key;
                var excludeBtn = CreateSecondaryButton(row, "Exclude", "+");
                UIFactory.SetLayoutElement(excludeBtn.Component.gameObject, minWidth: 30);
                excludeBtn.OnClick += () =>
                {
                    if (!_pendingExclusionAdds.Contains(capturedPath))
                    {
                        _pendingExclusionAdds.Add(capturedPath);
                        RefreshExclusionsList();
                        UpdateApplyButtonText();
                        _exclusionsStatusLabel.text = Tr("Added:") + $" {capturedPath}";
                        _exclusionsStatusLabel.color = UIStyles.StatusSuccess;
                    }
                };
            }

            SetDynamicText(_exclusionsStatusLabel, $"Found {found.Count} component(s)");
            _exclusionsStatusLabel.color = UIStyles.StatusSuccess;
        }

        private void RefreshExclusionsList()
        {
            if (_exclusionsListContainer == null) return;

            // Clear existing items (manual iteration for IL2CPP compatibility)
            for (int i = _exclusionsListContainer.transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_exclusionsListContainer.transform.GetChild(i).gameObject);
            }

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

            if (effectiveExclusions.Count == 0)
            {
                var emptyLabel = UIFactory.CreateLabel(_exclusionsListContainer, "EmptyLabel", "No exclusions defined", TextAnchor.MiddleCenter);
                emptyLabel.color = UIStyles.TextMuted;
                emptyLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(emptyLabel.gameObject, minHeight: 40, flexibleWidth: 9999);
                return;
            }

            foreach (var (pattern, isPending, isRemoved) in effectiveExclusions)
            {
                var row = UIStyles.CreateFormRow(_exclusionsListContainer, $"Row_{pattern.GetHashCode()}", UIStyles.RowHeightNormal, 5);

                // Show pattern with visual indicator for pending state
                string displayText = pattern;
                if (isPending) displayText = $"+ {pattern}";
                else if (isRemoved) displayText = $"- {pattern}";

                var patternLabel = UIFactory.CreateLabel(row, "PatternLabel", displayText, TextAnchor.MiddleLeft);
                patternLabel.fontSize = UIStyles.FontSizeSmall;
                UIFactory.SetLayoutElement(patternLabel.gameObject, flexibleWidth: 9999);

                // Set color based on state
                if (isPending)
                {
                    patternLabel.color = UIStyles.StatusSuccess;
                }
                else if (isRemoved)
                {
                    patternLabel.color = UIStyles.StatusError;
                }
                else
                {
                    patternLabel.color = UIStyles.TextPrimary;
                }

                var deleteBtn = CreateSecondaryButton(row, "DeleteBtn", isRemoved ? "\u21a9" : "X", 30);
                var capturedPattern = pattern;
                var capturedIsRemoved = isRemoved;

                if (isRemoved)
                {
                    // Undo removal
                    deleteBtn.OnClick += () =>
                    {
                        _pendingExclusionRemoves.Remove(capturedPattern);
                        RefreshExclusionsList();
                        UpdateApplyButtonText();
                    };
                }
                else
                {
                    deleteBtn.OnClick += () => OnDeleteExclusionClicked(capturedPattern);
                }
            }
        }

        private void OnDeleteExclusionClicked(string pattern)
        {
            // If it was a pending add, just remove from pending
            if (_pendingExclusionAdds.Contains(pattern))
            {
                _pendingExclusionAdds.Remove(pattern);
                SetDynamicText(_exclusionsStatusLabel, "Pending pattern cancelled");
                _exclusionsStatusLabel.color = UIStyles.TextSecondary;
            }
            else
            {
                // Mark for removal on Apply
                _pendingExclusionRemoves.Add(pattern);
                SetDynamicText(_exclusionsStatusLabel, "Pattern will be removed on Apply");
                _exclusionsStatusLabel.color = UIStyles.TextSecondary;
            }

            RefreshExclusionsList();
            UpdateApplyButtonText();
        }

        #endregion

        #region Fonts Tab

        // Font overrides UI
        private GameObject _fontOverridesListContainer;
        private TabBar _fontsSubTabBar;
        private GameObject _fontsSubTabHost;

        private void CreateFontsTabContent(GameObject parent)
        {
            // Sub-tab buttons in the fixed header, their contents in the scrolling tab body:
            // the scrollbar then covers the settings only, not the switcher above them.
            _fontsSubTabBar = new TabBar();
            _fontsSubTabBar.CreateUI(_fontsSubTabHost, parent, tabRowHeight: 26); // Compact height for sub-tabs

            var globalTab = _fontsSubTabBar.AddTab("Global");
            var overridesTab = _fontsSubTabBar.AddTab("Overrides");

            foreach (var text in _fontsSubTabBar.GetTabButtonTexts())
                RegisterUIText(text);

            _helpZone?.Describe(_fontsSubTabBar.GetTabButton("Global"),
                "Global font settings for every detected font, including fallbacks and sharpness.");
            _helpZone?.Describe(_fontsSubTabBar.GetTabButton("Overrides"),
                "Per-element rules that override the font size for specific UI elements.");

            CreateFontsGlobalSubTab(globalTab);
            CreateFontsOverridesSubTab(overridesTab);
        }

        private void CreateFontsGlobalSubTab(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "FontsCard", PanelWidth - 60, stretchVertically: true);

            // Header and explanation
            var sectionTitle = UIStyles.CreateSectionTitle(card, "FontsLabel", "Font Management");
            RegisterUIText(sectionTitle);

            var explainHint = UIStyles.CreateHint(card, "FontsHint", "Configure translation for detected fonts. Add fallback fonts for non-Latin scripts (Hindi, Arabic, Chinese, etc.). Settings are saved with translations.");
            RegisterUIText(explainHint);

            UIStyles.CreateSpacer(card, 5);

            // Debug toggle: globally disable font replacement (for translators).
            var fontEnableObj = UIFactory.CreateToggle(card, "EnableFontReplacementToggle", out _enableFontReplacementToggle, out var fontEnableLabel);
            fontEnableLabel.text = " Enable font replacement (uncheck to debug with original fonts)";
            fontEnableLabel.color = UIStyles.TextSecondary;
            UIHelpers.AddToggleListener(_enableFontReplacementToggle, OnEnableFontReplacementChanged);
            UIFactory.SetLayoutElement(fontEnableObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(fontEnableLabel);
            _helpZone?.Describe(fontEnableObj,
                "Replaces game fonts so your language's characters display correctly. Uncheck to debug with the original fonts.");

            // Font sharpness = max SDF atlas dimension. Higher = crisper when the translation
            // scales text up, at a VRAM cost. LAYOUT-NEUTRAL (text size unchanged). Options are
            // bounded dynamically by the GPU texture limit; applied immediately (SaveConfig),
            // takes effect on the next font rebuild (auto-detected — no manual .gen deletion).
            var sharpRow = UIStyles.CreateFormRow(card, "FontSharpnessRow", UIStyles.RowHeightMedium, 5);
            var sharpLabel = UIFactory.CreateLabel(sharpRow, "FontSharpnessLabel", "Font sharpness:", TextAnchor.MiddleLeft);
            sharpLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(sharpLabel.gameObject, minWidth: 100);
            RegisterUIText(sharpLabel);

            int maxTex = 8192;
            try { int sys = UnityEngine.SystemInfo.maxTextureSize; if (sys >= 512) maxTex = sys; } catch { }
            var sharpOptions = new List<string> { "Auto" };
            foreach (int s in new[] { 4096, 8192, 16384 })
                if (s <= maxTex) sharpOptions.Add(s.ToString());
            int curBudget = TranslatorCore.Config?.max_font_atlas_size ?? 0;
            string sharpInitial = (curBudget > 0 && sharpOptions.Contains(curBudget.ToString()))
                ? curBudget.ToString() : "Auto";

            _pendingAtlasSize = curBudget;
            _fontAtlasSizeDropdown = new SearchableDropdown("FontSharpness", sharpOptions.ToArray(),
                sharpInitial, popupHeight: 150, showSearch: false);
            var sharpDropdownObj = _fontAtlasSizeDropdown.CreateUI(sharpRow, (val) =>
            {
                // Pending only — applied (and fonts rebuilt) on Apply, like every other setting.
                _pendingAtlasSize = (val == "Auto" || !int.TryParse(val, out int b)) ? 0 : b;
                UpdateApplyButtonText();
            }, width: 140);
            UIFactory.SetLayoutElement(sharpDropdownObj, minWidth: 140, minHeight: UIStyles.InputHeight);
            _helpZone?.Describe(sharpDropdownObj, "How finely replacement fonts are rendered. Higher = crisper when the translation scales text up, but uses more video memory. 'Auto' is a safe default. Text size is unchanged. Takes effect on the next font rebuild.");

            UIStyles.CreateSpacer(card, 10);

            // Refresh button
            var refreshRow = UIStyles.CreateFormRow(card, "RefreshRow", UIStyles.RowHeightNormal, 5);

            var refreshBtn = CreateSecondaryButton(refreshRow, "RefreshFontsBtn", "Refresh List", 100);
            // Explicit user request: this is the one place the ranking is allowed to re-rank.
            refreshBtn.OnClick += () => { InvalidateFontOrder(); RefreshFontsList(); };
            RegisterUIText(refreshBtn.ButtonText);
            _helpZone?.Describe(refreshBtn.Component.gameObject,
                "Rescans the game for fonts currently in use and updates the list below.");

            _fontsStatusLabel = UIFactory.CreateLabel(refreshRow, "FontsStatus", "", TextAnchor.MiddleLeft);
            _fontsStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_fontsStatusLabel.gameObject, flexibleWidth: 9999);

            UIStyles.CreateSpacer(card, 10);

            // Detected fonts list
            var listLabel = UIFactory.CreateLabel(card, "FontsListLabel", "Detected Fonts:", TextAnchor.MiddleLeft);
            listLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(listLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(listLabel);

            // Scrollable container for fonts
            var scrollObj = UIFactory.CreateScrollView(card, "FontsScroll", out var scrollContent, out var scrollbar);
            // Three heights, and all three matter (measured in-game, see [SizeDiag] logs):
            //  - preferred = a COMFORTABLE height, not the minimum. The panel sizes itself to the
            //    content's preferred height exactly, so a list asking for its minimum leaves the
            //    panel no slack at all: one pixel over and it grows its own scrollbar next to the
            //    list's. Asking for more gives the panel room it can take back under pressure.
            //  - min = how far the list may be squeezed on a small screen before the panel scrolls.
            //  - flexible = it soaks up any spare height when the window is enlarged.
            // Without the preferred height the tab would instead claim the WHOLE list's height,
            // which overflows the screen for the same double-scrollbar result.
            UIFactory.SetLayoutElement(scrollObj, minHeight: 180, preferredHeight: 180,
                flexibleHeight: 9999, flexibleWidth: 9999);
            UIStyles.SetBackground(scrollObj, UIStyles.InputBackground);
            UIFactory.ConfigureAutoHideScrollbar(scrollObj);

            _fontsListContainer = scrollContent;
        }

        private void CreateFontsOverridesSubTab(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "OverridesCard", PanelWidth - 60, stretchVertically: true);

            var sectionTitle = UIStyles.CreateSectionTitle(card, "OverridesLabel", "Font Overrides");
            RegisterUIText(sectionTitle);

            var explainHint = UIStyles.CreateHint(card, "OverridesHint",
                "Override font size for specific UI elements. Use inspector, search, or manual pattern.");
            RegisterUIText(explainHint);

            UIStyles.CreateSpacer(card, 5);

            // Inspector button — click on element to add override
            var inspectorBtn = CreatePrimaryButton(card, "FontOverrideInspectorBtn", "Inspect Element", PanelWidth - 100);
            inspectorBtn.OnClick += OnStartFontOverrideInspector;
            RegisterUIText(inspectorBtn.ButtonText);
            _helpZone?.Describe(inspectorBtn.Component.gameObject,
                "Closes this panel so you can click a UI element in-game to create a font override for it.");

            var inspectorHint = UIStyles.CreateHint(card, "InspectorHint", "Click on a UI element to create an override for it");
            RegisterUIText(inspectorHint);

            UIStyles.CreateSpacer(card, 5);

            // Find by content
            var findLabel = UIFactory.CreateLabel(card, "FindLabel", "Find by text content:", TextAnchor.MiddleLeft);
            findLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(findLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(findLabel);

            var findRow = UIStyles.CreateFormRow(card, "FindOverrideRow", UIStyles.InputHeight, 5);

            _fontOverrideFindInput = UIFactory.CreateInputField(findRow, "FindOverrideInput", "Enter text visible in-game...");
            UIFactory.SetLayoutElement(_fontOverrideFindInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_fontOverrideFindInput.Component.gameObject, UIStyles.InputBackground);
            _helpZone?.Describe(_fontOverrideFindInput.Component.gameObject,
                "Type text visible in-game to locate the UI element that shows it, then create an override.");

            var findBtn = CreateSecondaryButton(findRow, "FindOverrideBtn", "Find", 60);
            findBtn.OnClick += OnFindForFontOverride;
            RegisterUIText(findBtn.ButtonText);
            _helpZone?.Describe(findBtn.Component.gameObject,
                "Searches the scene for UI components displaying the entered text.");

            // Find results (hidden until search)
            var findResultsScroll = UIFactory.CreateScrollView(card, "OverrideFindResults", out var findResultsContent, out _);
            UIFactory.SetLayoutElement(findResultsScroll, minHeight: 0, preferredHeight: 80, flexibleHeight: 0);
            _fontOverrideFindResultsContainer = findResultsContent;
            _fontOverrideFindResultsScroll = findResultsScroll;
            findResultsScroll.SetActive(false);

            UIStyles.CreateSpacer(card, 5);

            // Manual add
            var manualLabel = UIFactory.CreateLabel(card, "ManualLabel", "Add pattern manually:", TextAnchor.MiddleLeft);
            manualLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(manualLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(manualLabel);

            var addRow = UIStyles.CreateFormRow(card, "AddOverrideRow", UIStyles.InputHeight, 5);
            _fontOverrideManualInput = UIFactory.CreateInputField(addRow, "ManualOverrideInput", "path:**/TablePanel/**");
            UIFactory.SetLayoutElement(_fontOverrideManualInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.InputHeight);
            UIStyles.SetBackground(_fontOverrideManualInput.Component.gameObject, UIStyles.InputBackground);
            _helpZone?.Describe(_fontOverrideManualInput.Component.gameObject,
                "Type a rule to match elements. Prefix with path:, font:, or text: to match by hierarchy, font name, or content.");

            var addBtn = CreateSecondaryButton(addRow, "AddOverrideBtn", "Add", 60);
            addBtn.OnClick += OnAddManualFontOverride;
            RegisterUIText(addBtn.ButtonText);
            _helpZone?.Describe(addBtn.Component.gameObject,
                "Adds the typed pattern as a new override rule. Takes effect on Apply.");

            var patternHint = UIStyles.CreateHint(card, "PatternHint", "Prefixes: path: (hierarchy), font: (name), text: (content)");
            RegisterUIText(patternHint);

            UIStyles.CreateSpacer(card, 5);

            // Count label
            _overridesCountLabel = UIFactory.CreateLabel(card, "OverridesCount", "", TextAnchor.MiddleLeft);
            _overridesCountLabel.fontSize = UIStyles.FontSizeSmall;
            _overridesCountLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_overridesCountLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Scrollable list of overrides
            var scrollObj = UIFactory.CreateScrollView(card, "OverridesScroll", out var scrollContent, out var scrollbar);
            UIFactory.SetLayoutElement(scrollObj, minHeight: 200, preferredHeight: 200, flexibleHeight: 9999, flexibleWidth: 9999);
            UIStyles.SetBackground(scrollObj, UIStyles.InputBackground);
            UIFactory.ConfigureAutoHideScrollbar(scrollObj);

            _fontOverridesListContainer = scrollContent;

            // Status label
            _fontOverrideStatusLabel = UIFactory.CreateLabel(card, "OverrideStatus", "", TextAnchor.MiddleLeft);
            _fontOverrideStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_fontOverrideStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            RefreshFontOverridesList();
        }

        // Font override UI fields
        private Text _overridesCountLabel;
        private Text _fontOverrideStatusLabel;
        private InputFieldRef _fontOverrideFindInput;
        private InputFieldRef _fontOverrideManualInput;
        private GameObject _fontOverrideFindResultsContainer;
        private GameObject _fontOverrideFindResultsScroll;

        // Pending font overrides (local copy, applied on Apply button)
        private List<FontOverrideRule> _pendingFontOverrides = new List<FontOverrideRule>();
        private List<FontOverrideRule> _initialFontOverrides = new List<FontOverrideRule>();

        private void InitPendingFontOverrides()
        {
            _pendingFontOverrides.Clear();
            _initialFontOverrides.Clear();
            foreach (var rule in TranslatorCore.FontOverrides)
            {
                _pendingFontOverrides.Add(CloneRule(rule));
                _initialFontOverrides.Add(CloneRule(rule));
            }
        }

        private static FontOverrideRule CloneRule(FontOverrideRule r)
        {
            return new FontOverrideRule
            {
                match = r.match,
                replacement = r.replacement,
                size_multiplier = r.size_multiplier,
                enabled = r.enabled,
                comment = r.comment
            };
        }

        private void OnStartFontOverrideInspector()
        {
            SetActive(false);
            TranslatorUIManager.OpenInspectorPanel(InspectorMode.FontOverride);
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
            if (UIRoot != null) UIRoot.transform.SetAsLastSibling();
        }

        private void OnAddManualFontOverride()
        {
            string pattern = _fontOverrideManualInput?.Text?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                SetDynamicText(_fontOverrideStatusLabel, "Enter a pattern first");
                _fontOverrideStatusLabel.color = UIStyles.StatusWarning;
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
                SetDynamicText(_fontOverrideStatusLabel, "Enter text to search for");
                _fontOverrideStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            _fontOverrideFindResultsScroll.SetActive(true);

            // Clear previous results
            for (int i = _fontOverrideFindResultsContainer.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_fontOverrideFindResultsContainer.transform.GetChild(i).gameObject);

            // Search text components (same logic as exclusions find)
            var found = new List<KeyValuePair<string, string>>();
            var seenPaths = new HashSet<string>();

            try
            {
                var textTypes = new List<Type>();
                if (TypeHelper.UI_TextType != null) textTypes.Add(TypeHelper.UI_TextType);
                if (TypeHelper.TMP_TextType != null) textTypes.Add(TypeHelper.TMP_TextType);

                foreach (var textType in textTypes)
                {
                    var allComponents = TypeHelper.FindAllObjectsOfType(textType);
                    if (allComponents == null) continue;

                    foreach (var obj in allComponents)
                    {
                        if (obj == null) continue;
                        try
                        {
                            string text = TypeHelper.GetText(obj);
                            if (string.IsNullOrEmpty(text)) continue;
                            if (text.IndexOf(searchValue, StringComparison.OrdinalIgnoreCase) < 0) continue;

                            Component comp = obj as Component;
                            if (comp == null)
                                comp = TypeHelper.Il2CppCast(obj, typeof(Component)) as Component;
                            if (comp == null || comp.gameObject == null) continue;
                            if (TranslatorCore.ShouldSkipTranslation(comp)) continue;

                            string path = TranslatorCore.GetGameObjectPath(comp.gameObject);
                            if (seenPaths.Contains(path)) continue;
                            seenPaths.Add(path);

                            string snippet = text.Length > 50 ? text.Substring(0, 50) + "..." : text;
                            found.Add(new KeyValuePair<string, string>(path, snippet));
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (found.Count == 0)
            {
                var emptyLabel = UIFactory.CreateLabel(_fontOverrideFindResultsContainer, "NoResults",
                    "No UI component found with this text.", TextAnchor.MiddleCenter);
                emptyLabel.color = UIStyles.TextMuted;
                emptyLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(emptyLabel.gameObject, minHeight: 30, flexibleWidth: 9999);
                SetDynamicText(_fontOverrideStatusLabel, "No results");
                _fontOverrideStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            foreach (var kvp in found)
            {
                var row = UIFactory.CreateUIObject("FindResult", _fontOverrideFindResultsContainer);
                UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(row, false, false, true, true, 5, 2, 2, 2, 2);
                UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

                var label = UIFactory.CreateLabel(row, "Path", kvp.Key, TextAnchor.MiddleLeft);
                label.fontSize = UIStyles.FontSizeSmall;
                UIFactory.SetLayoutElement(label.gameObject, flexibleWidth: 9999);

                var capturedPath = kvp.Key;
                var addPathBtn = CreateSecondaryButton(row, "AddOverride", "+");
                UIFactory.SetLayoutElement(addPathBtn.Component.gameObject, minWidth: 30);
                addPathBtn.OnClick += () => AddFontOverrideForPath("path:" + capturedPath);
            }

            SetDynamicText(_fontOverrideStatusLabel, $"Found {found.Count} component(s)");
            _fontOverrideStatusLabel.color = UIStyles.StatusSuccess;
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
            if (_fontOverrideStatusLabel != null)
            {
                _fontOverrideStatusLabel.text = $"Added: {match} (Apply to save)";
                _fontOverrideStatusLabel.color = UIStyles.StatusSuccess;
            }
        }

        private void RefreshFontOverridesList()
        {
            if (_fontOverridesListContainer == null) return;

            // Clear existing rows
            for (int i = _fontOverridesListContainer.transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_fontOverridesListContainer.transform.GetChild(i).gameObject);
            }

            if (_overridesCountLabel != null)
            {
                _overridesCountLabel.text = _pendingFontOverrides.Count > 0
                    ? $"{_pendingFontOverrides.Count} rule(s)"
                    : "No rules defined";
            }

            for (int i = 0; i < _pendingFontOverrides.Count; i++)
            {
                CreateFontOverrideRow(i, _pendingFontOverrides[i]);
            }
        }

        private void CreateFontOverrideRow(int index, FontOverrideRule rule)
        {
            var row = UIFactory.CreateVerticalGroup(_fontOverridesListContainer, $"Override_{index}",
                false, false, true, true, 3);
            UIFactory.SetLayoutElement(row, minHeight: UIStyles.MultiLineMedium, flexibleWidth: 9999);
            UIStyles.SetBackground(row, UIStyles.CardBackground);

            // Row 1: Match pattern (editable) + enabled toggle + delete button
            var topRow = UIFactory.CreateHorizontalGroup(row, "TopRow", false, false, true, true, 5);
            UIFactory.SetLayoutElement(topRow, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);

            var matchLabel = UIFactory.CreateLabel(topRow, "MatchLabel", "Match:", TextAnchor.MiddleLeft);
            matchLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(matchLabel.gameObject, minWidth: 45);

            var matchInput = UIFactory.CreateInputField(topRow, "MatchInput", "path:*Pattern*");
            UIFactory.SetLayoutElement(matchInput.Component.gameObject, flexibleWidth: 9999, minHeight: UIStyles.RowHeightNormal);
            matchInput.Text = rule.match ?? "";

            int capturedIndex = index;

            matchInput.OnValueChanged += (val) =>
            {
                if (capturedIndex < _pendingFontOverrides.Count)
                {
                    _pendingFontOverrides[capturedIndex].match = val;
                    UpdateApplyButtonText();
                }
            };

            // Delete button
            var deleteBtn = UIFactory.CreateButton(topRow, "DeleteBtn", "X");
            UIFactory.SetLayoutElement(deleteBtn.Component.gameObject, minWidth: 28, minHeight: UIStyles.RowHeightNormal);
            UIStyles.SetBackground(deleteBtn.Component.gameObject, UIStyles.ButtonDanger);
            deleteBtn.OnClick += () =>
            {
                if (capturedIndex < _pendingFontOverrides.Count)
                {
                    _pendingFontOverrides.RemoveAt(capturedIndex);
                    RefreshFontOverridesList();
                    UpdateApplyButtonText();
                }
            };

            // Row 2: Size multiplier slider + comment
            var bottomRow = UIFactory.CreateHorizontalGroup(row, "BottomRow", false, false, true, true, 5);
            UIFactory.SetLayoutElement(bottomRow, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);

            var sizeLabel = UIFactory.CreateLabel(bottomRow, "SizeLabel", "Size:", TextAnchor.MiddleLeft);
            sizeLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(sizeLabel.gameObject, minWidth: 35);

            var sizeValueLabel = UIFactory.CreateLabel(bottomRow, "SizeValue",
                rule.size_multiplier > 0.001f ? $"{(int)(rule.size_multiplier * 100)}%" : "default",
                TextAnchor.MiddleCenter);
            sizeValueLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(sizeValueLabel.gameObject, minWidth: 45);

            var sizeSliderObj = UIFactory.CreateSlider(bottomRow, "SizeSlider", out var sizeSlider);
            UIFactory.SetLayoutElement(sizeSliderObj, flexibleWidth: 9999, minHeight: UIStyles.RowHeightNormal);
            sizeSlider.minValue = 0f;
            sizeSlider.maxValue = 3f;
            sizeSlider.value = rule.size_multiplier > 0.001f ? rule.size_multiplier : 1.0f;

            UIHelpers.AddSliderListener(sizeSlider, (val) =>
            {
                // Round to nearest 5%
                float rounded = (float)Math.Round(val * 20) / 20f;
                sizeValueLabel.text = $"{(int)(rounded * 100)}%";

                if (capturedIndex < _pendingFontOverrides.Count)
                {
                    _pendingFontOverrides[capturedIndex].size_multiplier = rounded;
                    UpdateApplyButtonText();
                }
            });
        }

        private void RefreshFontsList()
        {
            if (_fontsListContainer == null) return;

            TranslatorCore.LogInfo($"[TranslationParametersPanel] RefreshFontsList called");

            // Clean up searchable dropdowns first
            foreach (var dropdown in _fallbackDropdowns)
            {
                dropdown.Destroy();
            }
            _fallbackDropdowns.Clear();

            // Clear existing items (manual iteration for IL2CPP compatibility)
            for (int i = _fontsListContainer.transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_fontsListContainer.transform.GetChild(i).gameObject);
            }

            var fonts = FontManager.GetDetectedFontsInfo();
            fonts = ApplyStableFontOrder(fonts);

            if (fonts.Count == 0)
            {
                var emptyLabel = UIFactory.CreateLabel(_fontsListContainer, "EmptyLabel", "No fonts detected yet. Play the game to detect fonts.", TextAnchor.MiddleCenter);
                emptyLabel.color = UIStyles.TextMuted;
                emptyLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(emptyLabel.gameObject, minHeight: 60, flexibleWidth: 9999);
                RegisterUIText(emptyLabel);

                if (_fontsStatusLabel != null)
                {
                    SetDynamicText(_fontsStatusLabel, "0 fonts detected");
                    _fontsStatusLabel.color = UIStyles.TextMuted;
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

            if (_fontsStatusLabel != null)
            {
                SetDynamicText(_fontsStatusLabel, $"{fonts.Count} font(s) detected");
                _fontsStatusLabel.color = UIStyles.StatusSuccess;
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
            var row = UIFactory.CreateVerticalGroup(_fontsListContainer, $"FontRow_{fontInfo.Name.GetHashCode()}",
                false, false, true, true, 3, new Vector4(5, 5, 5, 5), UIStyles.CardBackground, TextAnchor.UpperLeft);
            UIFactory.SetLayoutElement(row, minHeight: 55, flexibleWidth: 9999);

            // Header row: font name + type + enable toggle
            var headerRow = UIStyles.CreateFormRow(row, "HeaderRow", UIStyles.RowHeightNormal, 5);

            // Capture values for closure
            string capturedFontName = fontInfo.Name;

            // Font name and type
            var fontLabel = UIFactory.CreateLabel(headerRow, "FontLabel", $"{fontInfo.Name} ({fontInfo.Type})", TextAnchor.MiddleLeft);
            fontLabel.color = UIStyles.TextPrimary;
            fontLabel.fontSize = UIStyles.FontSizeNormal;
            UIFactory.SetLayoutElement(fontLabel.gameObject, flexibleWidth: 9999);

            // How present this font is on the screen right now — the figure that tells the user
            // whether a font is worth configuring. -1 means the count couldn't be taken; say
            // nothing rather than show a misleading zero.
            if (fontInfo.SceneCount >= 0)
            {
                var countLabel = UIFactory.CreateLabel(headerRow, "SceneCount",
                    $"{fontInfo.SceneCount} " + Tr("in scene"), TextAnchor.MiddleRight);
                countLabel.color = fontInfo.SceneCount > 0 ? UIStyles.TextSecondary : UIStyles.TextMuted;
                countLabel.fontSize = UIStyles.FontSizeSmall;
                UIFactory.SetLayoutElement(countLabel.gameObject, minWidth: 70);
                RegisterExcluded(countLabel);
            }

            // Identify button: highlight in-game texts using this font. This button swaps its color at
            // runtime (?/slate ↔ X/accent) via Image.color, so create it with a WHITE ColorBlock
            // normalColor and let Image.color drive the visible color directly (otherwise rendered =
            // Image.color × 0.25 default, which crushed the slate to near-black).
            var identifyBtn = UIFactory.CreateButton(headerRow, "IdentifyBtn", "?", Color.white);
            UIFactory.SetLayoutElement(identifyBtn.GameObject, minWidth: 28, minHeight: 22);
            identifyBtn.ButtonText.fontSize = UIStyles.FontSizeSmall;
            identifyBtn.ButtonText.color = UIStyles.TextSecondary;
            var identifyBg = identifyBtn.GameObject.GetComponent<Image>();
            if (identifyBg != null)
            {
                identifyBg.color = UIStyles.ButtonSecondary;
            }
            identifyBtn.OnClick += () => ToggleFontHighlight(capturedFontName, identifyBtn);

            // Enable toggle
            var toggleObj = UIFactory.CreateToggle(headerRow, "EnableToggle", out var enableToggle, out var toggleLabel);
            toggleLabel.text = " Translate";
            toggleLabel.color = UIStyles.TextSecondary;
            toggleLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(toggleObj, minWidth: 80);
            enableToggle.isOn = fontInfo.Enabled;

            UIHelpers.AddToggleListener(enableToggle, (isOn) => OnFontEnableChanged(capturedFontName, isOn));

            // Fallback row (only for fonts that support it)
            if (fontInfo.SupportsFallback)
            {
                var fallbackRow = UIStyles.CreateFormRow(row, "FallbackRow", UIStyles.RowHeightNormal, 5);

                var fallbackLabel = UIFactory.CreateLabel(fallbackRow, "FallbackLabel", "Fallback:", TextAnchor.MiddleLeft);
                fallbackLabel.color = UIStyles.TextSecondary;
                fallbackLabel.fontSize = UIStyles.FontSizeSmall;
                UIFactory.SetLayoutElement(fallbackLabel.gameObject, minWidth: 55);

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
                string[] customFonts = null;
                {
                    customFonts = FontManager.GetCustomFontNames();
                    if (customFonts != null && customFonts.Length > 0)
                    {
                        if (options.Count > 1)
                            options.Add("--- Custom Fonts ---");
                        foreach (var customFont in customFonts)
                            options.Add("[Custom] " + customFont);
                    }
                }

                // If no fonts available at all
                if (options.Count <= 1)
                {
                    var noFontsLabel = UIFactory.CreateLabel(fallbackRow, "NoFontsLabel", "(no fonts available)", TextAnchor.MiddleLeft);
                    noFontsLabel.color = UIStyles.TextMuted;
                    noFontsLabel.fontSize = UIStyles.FontSizeSmall;
                    noFontsLabel.fontStyle = FontStyle.Italic;
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
                    popupHeight: 250,
                    showSearch: true
                );
                dropdown.CategoryProvider = FontManager.GetFontOrigin;

                dropdown.CreateUI(fallbackRow, (selectedValue) =>
                {
                    // Markers are display only — what gets stored is the font name
                    selectedValue = FontManager.StripOptionMarker(selectedValue);
                    string fallback = selectedValue == "(None)" ? null : selectedValue;
                    OnFontFallbackChanged(capturedFontName, fallback);
                }, width: 350);

                _fallbackDropdowns.Add(dropdown);
            }
            else
            {
                // Show hint for non-TMP fonts
                var noFallbackLabel = UIFactory.CreateLabel(row, "NoFallbackLabel", "Fallback not supported for this font type", TextAnchor.MiddleLeft);
                noFallbackLabel.color = UIStyles.TextMuted;
                noFallbackLabel.fontSize = UIStyles.FontSizeSmall;
                noFallbackLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(noFallbackLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            }

            // Size row (for all fonts) — the DELIBERATE size percent (fit/readability, e.g. a longer
            // cross-script translation vs the HUD). Orthogonal to the auto design-scale: the two
            // combine multiplicatively (Model B). 100% = native.
            var scaleRow = UIStyles.CreateFormRow(row, "ScaleRow", UIStyles.RowHeightNormal, 5);

            var scaleLabel = UIFactory.CreateLabel(scaleRow, "ScaleLabel", "Size:", TextAnchor.MiddleLeft);
            scaleLabel.color = UIStyles.TextSecondary;
            scaleLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(scaleLabel.gameObject, minWidth: 55);

            // Size slider (1% to 200%) = the deliberate percent. Always active; it does NOT replace
            // the auto design-scale, it applies on top of it.
            var sliderObj = UIFactory.CreateSlider(scaleRow, $"ScaleSlider_{capturedFontName}", out UnityEngine.UI.Slider scaleSlider);
            UIFactory.SetLayoutElement(sliderObj, minWidth: 120, flexibleWidth: 1, minHeight: 20);
            scaleSlider.minValue = 0.01f;
            scaleSlider.maxValue = 2.0f;
            scaleSlider.wholeNumbers = false;
            float sizePercent = FontManager.GetFontSizePercent(capturedFontName);
            scaleSlider.value = Math.Min(scaleSlider.maxValue, sizePercent);

            var scaleValueLabel = UIFactory.CreateLabel(scaleRow, $"ScaleValue_{capturedFontName}",
                $"{(int)(sizePercent * 100)}%", TextAnchor.MiddleCenter);
            scaleValueLabel.fontSize = UIStyles.FontSizeSmall;
            scaleValueLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(scaleValueLabel.gameObject, minWidth: 40);

            var capturedScaleLabel = scaleValueLabel;
            UIHelpers.AddSliderListener(scaleSlider, (float val) =>
            {
                // Round to nearest 1%
                float rounded = (float)Math.Round(val, 2);
                capturedScaleLabel.text = $"{(int)(rounded * 100)}%";
                OnFontScaleChanged(capturedFontName, rounded);
            });

            // Auto design-scale toggle — folds the font's native design-scale into the size as a
            // baseline (so an imported font matches the game's original size), on top of which the
            // slider % still applies. Default ON for freshly detected TMP fonts. HIDDEN for font
            // types where the design-scale has no meaning (UI.Text / non-TMP clone-atlas preserves
            // the game's metrics). Commits on Apply only (UX rule: no immediate application).
            if (FontManager.SupportsDesignScale(fontInfo.Type))
            {
                bool fontScaleAuto = FontManager.GetFontSettings(capturedFontName)?.scale_auto ?? false;
                var autoToggleObj = UIFactory.CreateToggle(scaleRow, $"AutoScale_{capturedFontName}", out UnityEngine.UI.Toggle autoScaleToggle, out var autoScaleLabel);
                autoScaleToggle.isOn = fontScaleAuto;
                autoScaleLabel.text = " Auto";
                autoScaleLabel.color = UIStyles.TextSecondary;
                autoScaleLabel.fontSize = UIStyles.FontSizeSmall;
                UIFactory.SetLayoutElement(autoToggleObj, minWidth: 70);
                UIHelpers.AddToggleListener(autoScaleToggle, (isOn) => OnFontAutoScaleChanged(capturedFontName, isOn));
            }
        }

        // Scale dropdown helpers
        private static readonly string[] _scaleOptions = { "50%", "60%", "70%", "80%", "90%", "100%", "110%", "120%", "130%", "140%", "150%", "175%", "200%" };
        private static readonly float[] _scaleValues = { 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.75f, 2.0f };

        private static string ScaleToString(float scale)
        {
            for (int i = 0; i < _scaleValues.Length; i++)
            {
                if (Math.Abs(_scaleValues[i] - scale) < 0.01f)
                    return _scaleOptions[i];
            }
            return "100%";
        }

        private static float StringToScale(string scaleStr)
        {
            for (int i = 0; i < _scaleOptions.Length; i++)
            {
                if (_scaleOptions[i] == scaleStr)
                    return _scaleValues[i];
            }
            return 1.0f;
        }

        /// <summary>
        /// Toggle font highlight: click to show, click again (or click another) to clear.
        /// </summary>
        private void ToggleFontHighlight(string fontName, ButtonRef button)
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
                button.ButtonText.text = "X";
                button.ButtonText.color = UIStyles.TextPrimary;
                var bg = button.GameObject.GetComponent<Image>();
                if (bg != null) bg.color = UIStyles.TextAccent;
            }
        }

        private void ResetHighlightButton()
        {
            if (_highlightedButton != null)
            {
                _highlightedButton.ButtonText.text = "?";
                _highlightedButton.ButtonText.color = UIStyles.TextSecondary;
                var bg = _highlightedButton.GameObject.GetComponent<Image>();
                if (bg != null) bg.color = UIStyles.ButtonSecondary;
            }
            _highlightedFontName = null;
            _highlightedButton = null;
        }

        /// <summary>
        /// Resolve a font's current settings for change-tracking: pending edit if any, else the
        /// captured initial, else the live font state. Single source so each handler mutates one
        /// field and preserves the others (enabled, fallback, sizePercent, scaleAuto).
        /// </summary>
        private (bool enabled, string fallback, float sizePercent, bool scaleAuto) GetEffectiveFontSettings(string fontName)
        {
            if (_pendingFontSettings.TryGetValue(fontName, out var pending))
                return pending;
            if (_initialFontSettings.TryGetValue(fontName, out var initial))
                return initial;
            var settings = FontManager.GetFontSettings(fontName);
            return (settings?.enabled ?? true, settings?.fallback,
                    FontManager.GetFontSizePercent(fontName),  // deliberate percent (not the effective)
                    settings?.scale_auto ?? false);
        }

        private void OnFontEnableChanged(string fontName, bool enabled)
        {
            var cur = GetEffectiveFontSettings(fontName);
            _pendingFontSettings[fontName] = (enabled, cur.fallback, cur.sizePercent, cur.scaleAuto);

            if (_fontsStatusLabel != null)
            {
                _fontsStatusLabel.text = enabled ? $"Translation enabled for {fontName}" : $"Translation disabled for {fontName}";
                _fontsStatusLabel.color = UIStyles.TextSecondary;
            }

            UpdateApplyButtonText();
        }

        private void OnFontFallbackChanged(string fontName, string fallbackFont)
        {
            var cur = GetEffectiveFontSettings(fontName);
            _pendingFontSettings[fontName] = (cur.enabled, fallbackFont, cur.sizePercent, cur.scaleAuto);

            if (_fontsStatusLabel != null)
            {
                if (string.IsNullOrEmpty(fallbackFont))
                {
                    _fontsStatusLabel.text = $"Fallback will be removed from {fontName}";
                }
                else
                {
                    _fontsStatusLabel.text = $"Fallback '{fallbackFont}' will be applied to {fontName}";
                }
                _fontsStatusLabel.color = UIStyles.TextSecondary;
            }

            UpdateApplyButtonText();
        }

        private void OnFontScaleChanged(string fontName, float sizePercent)
        {
            var cur = GetEffectiveFontSettings(fontName);
            // The slider is the deliberate size percent — orthogonal to the auto design-scale, so
            // scaleAuto is preserved (Model B: the two combine).
            _pendingFontSettings[fontName] = (cur.enabled, cur.fallback, sizePercent, cur.scaleAuto);

            if (_fontsStatusLabel != null)
            {
                int percent = Mathf.RoundToInt(sizePercent * 100f);
                _fontsStatusLabel.text = $"Font size {percent}% will be applied to {fontName}";
                _fontsStatusLabel.color = UIStyles.TextSecondary;
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
            _pendingFontSettings[fontName] = (cur.enabled, cur.fallback, cur.sizePercent, scaleAuto);

            if (_fontsStatusLabel != null)
            {
                _fontsStatusLabel.text = scaleAuto
                    ? $"Auto native size enabled for {fontName}"
                    : $"Auto native size disabled for {fontName}";
                _fontsStatusLabel.color = UIStyles.TextSecondary;
            }

            UpdateApplyButtonText();
        }

        #endregion

        #region Images Tab

        private void CreateImagesTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "ImagesCard", PanelWidth - 60, stretchVertically: true);

            // Section title
            var sectionTitle = UIStyles.CreateSectionTitle(card, "ImagesLabel", "Bitmap Replacements");
            RegisterUIText(sectionTitle);

            var explainHint = UIStyles.CreateHint(card, "ImagesHint",
                "Replace images that contain text (bitmap text) with translated versions. " +
                "Use the inspector to select images, export originals as templates, " +
                "then import your translated PNG files.");
            RegisterUIText(explainHint);

            UIStyles.CreateSpacer(card, 5);

            // Debug toggle: globally disable image replacement (for translators).
            var imgEnableObj = UIFactory.CreateToggle(card, "EnableImageReplacementToggle", out _enableImageReplacementToggle, out var imgEnableLabel);
            imgEnableLabel.text = " Enable image replacement (uncheck to debug with original images)";
            imgEnableLabel.color = UIStyles.TextSecondary;
            UIHelpers.AddToggleListener(_enableImageReplacementToggle, OnEnableImageReplacementChanged);
            UIFactory.SetLayoutElement(imgEnableObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(imgEnableLabel);
            _helpZone?.Describe(imgEnableObj,
                "Swaps images that contain baked-in text for your translated versions. Uncheck to debug with the original images.");

            UIStyles.CreateSpacer(card, 5);

            // Start Image Inspector button
            var inspectorBtn = CreatePrimaryButton(card, "ImageInspectorBtn", "Start Image Inspector", PanelWidth - 100);
            inspectorBtn.OnClick += OnStartImageInspectorClicked;
            RegisterUIText(inspectorBtn.ButtonText);
            _helpZone?.Describe(inspectorBtn.Component.gameObject,
                "Closes this panel so you can click images in-game to mark them for replacement.");

            var inspectorHint = UIStyles.CreateHint(card, "ImageInspectorHint",
                "Click on images in the game to mark them for replacement");
            RegisterUIText(inspectorHint);

            UIStyles.CreateSpacer(card, 8);

            // Current replacements list
            var listLabel = UIFactory.CreateLabel(card, "ListLabel", "Current Replacements:", TextAnchor.MiddleLeft);
            listLabel.fontSize = UIStyles.FontSizeSmall;
            listLabel.fontStyle = FontStyle.Bold;
            RegisterUIText(listLabel);

            var scrollObj = UIFactory.CreateScrollView(card, "ImagesScroll", out var scrollContent, out var scrollbar);
            UIFactory.SetLayoutElement(scrollObj, minHeight: 200, preferredHeight: 200, flexibleHeight: 9999);
            _imagesListContainer = scrollContent;

            // Apply All button
            UIStyles.CreateSpacer(card, 5);

            var applyRow = UIStyles.CreateFormRow(card, "ApplyRow", UIStyles.ButtonHeight, 5);
            var applyAllBtn = CreatePrimaryButton(applyRow, "ApplyAllBtn", "Load All Replacements");
            applyAllBtn.OnClick += OnLoadAllReplacementsClicked;
            UIFactory.SetLayoutElement(applyAllBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(applyAllBtn.ButtonText);
            _helpZone?.Describe(applyAllBtn.Component.gameObject,
                "Reloads your edited PNG files from disk and applies them in-game. Use after editing the exported templates.");

            // Status label
            _imagesStatusLabel = UIFactory.CreateLabel(card, "ImagesStatus", "", TextAnchor.MiddleLeft);
            _imagesStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_imagesStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Initial populate
            RefreshImageReplacementsList();
        }

        private void RefreshImageReplacementsList()
        {
            if (_imagesListContainer == null) return;

            // Clear existing items (backwards for IL2CPP safety)
            int childCount = _imagesListContainer.transform.childCount;
            for (int i = childCount - 1; i >= 0; i--)
            {
                var child = _imagesListContainer.transform.GetChild(i);
                UnityEngine.Object.Destroy(child.gameObject);
            }

            var replacements = ImageReplacer.GetAll();
            if (replacements.Count == 0)
            {
                var emptyLabel = UIFactory.CreateLabel(_imagesListContainer, "Empty",
                    "No images marked for replacement yet.\nUse the Image Inspector to select images.",
                    TextAnchor.MiddleCenter);
                emptyLabel.color = UIStyles.TextMuted;
                emptyLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(emptyLabel.gameObject, minHeight: 60, flexibleWidth: 9999);
                return;
            }

            foreach (var kvp in replacements)
            {
                var entry = kvp.Value;
                var spriteName = entry.SpriteName;
                bool isLoaded = ImageReplacer.IsReplacementLoaded(spriteName);
                bool fileExists = ImageReplacer.HasReplacementFile(spriteName);
                var capturedName = spriteName;

                // Row container
                var row = UIFactory.CreateUIObject("Row_" + spriteName, _imagesListContainer);
                UIFactory.SetLayoutGroup<UnityEngine.UI.HorizontalLayoutGroup>(row, false, false, true, true, 5, 2, 2, 2, 2);
                UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);

                // Info
                var infoObj = UIFactory.CreateUIObject("Info", row);
                UIFactory.SetLayoutGroup<UnityEngine.UI.VerticalLayoutGroup>(infoObj, false, false, true, true, 0);
                UIFactory.SetLayoutElement(infoObj, flexibleWidth: 9999);

                var nameLabel = UIFactory.CreateLabel(infoObj, "Name",
                    $"{spriteName} ({entry.OriginalWidth}x{entry.OriginalHeight})",
                    TextAnchor.MiddleLeft);
                nameLabel.fontSize = UIStyles.FontSizeSmall;
                nameLabel.fontStyle = FontStyle.Bold;
                UIFactory.SetLayoutElement(nameLabel.gameObject, flexibleWidth: 9999);

                // Status
                string statusText;
                Color statusColor;
                if (isLoaded)
                {
                    statusText = "Replacement active";
                    statusColor = UIStyles.StatusSuccess;
                }
                else if (fileExists)
                {
                    statusText = $"File ready: {entry.File} (click Load All)";
                    statusColor = UIStyles.StatusWarning;
                }
                else
                {
                    statusText = "Edit the exported PNG, then Load All";
                    statusColor = UIStyles.TextMuted;
                }

                var statusLabel = UIFactory.CreateLabel(infoObj, "Status", statusText, TextAnchor.MiddleLeft);
                statusLabel.fontSize = UIStyles.FontSizeSmall - 1;
                statusLabel.color = statusColor;
                UIFactory.SetLayoutElement(statusLabel.gameObject, flexibleWidth: 9999);

                // Remove button
                var removeBtn = CreateSecondaryButton(row, "Remove_" + spriteName, "X");
                UIFactory.SetLayoutElement(removeBtn.Component.gameObject, minWidth: 30, minHeight: UIStyles.RowHeightNormal);
                removeBtn.OnClick += () =>
                {
                    ImageReplacer.RemoveReplacement(capturedName);
                    TranslatorCore.SaveCache();
                    RefreshImageReplacementsList();
                    _imagesStatusLabel.text = Tr("Removed:") + $" {capturedName}";
                    _imagesStatusLabel.color = UIStyles.TextSecondary;
                };
            }
        }

        private void OnLoadAllReplacementsClicked()
        {
            // Force reload all (user may have edited PNGs on disk)
            int loaded = ImageReplacer.LoadAllReplacements(forceReload: true);
            int applied = ImageReplacer.ApplyToScene();
            RefreshImageReplacementsList();

            if (loaded > 0)
            {
                SetDynamicText(_imagesStatusLabel, $"Loaded {loaded} replacement(s)");
                _imagesStatusLabel.color = UIStyles.StatusSuccess;
            }
            else
            {
                SetDynamicText(_imagesStatusLabel, "No new replacements to load");
                _imagesStatusLabel.color = UIStyles.TextMuted;
            }
        }

        #endregion

        #region Variables Tab

        private void CreateVariablesTabContent(GameObject parent)
        {
            var card = CreateAdaptiveCard(parent, "VariablesCard", PanelWidth - 60, stretchVertically: true);

            // Section title
            var sectionTitle = UIStyles.CreateSectionTitle(card, "VarsLabel", "Game Variables");
            RegisterUIText(sectionTitle);

            var explainHint = UIStyles.CreateHint(card, "VarsHint",
                "Capture dynamic game values (player name, clan name, etc.) so translations can be reused regardless of the actual value. " +
                "Variables are replaced with placeholders before translation.");
            RegisterUIText(explainHint);

            UIStyles.CreateSpacer(card, 8);

            // Capture section
            var captureTitle = UIStyles.CreateSectionTitle(card, "CaptureLabel", "Capture Variable");
            RegisterUIText(captureTitle);

            var captureHint = UIStyles.CreateHint(card, "CaptureHint",
                "Enter the current value of a game variable (e.g. your character name) to find it in memory.");
            RegisterUIText(captureHint);

            var captureRow = UIStyles.CreateFormRow(card, "CaptureRow", UIStyles.RowHeightNormal, 5);

            _scanValueInput = UIFactory.CreateInputField(captureRow, "ScanValueInput", "Enter value to search...");
            UIFactory.SetLayoutElement(_scanValueInput.UIRoot, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);
            _helpZone?.Describe(_scanValueInput.Component.gameObject,
                "Type the current value of a game variable, like your character name, to find it in memory.");

            var scanBtn = CreatePrimaryButton(captureRow, "ScanBtn", "Scan");
            scanBtn.OnClick += OnScanClicked;
            UIFactory.SetLayoutElement(scanBtn.Component.gameObject, minWidth: 70);
            RegisterUIText(scanBtn.ButtonText);
            _helpZone?.Describe(scanBtn.Component.gameObject,
                "Searches game memory for the entered value so it can be turned into a reusable placeholder.");

            UIStyles.CreateSpacer(card, 5);

            // Scan results (hidden until scan)
            var resultsScroll = UIFactory.CreateScrollView(card, "ScanResultsScroll", out var resultsContent, out _);
            UIFactory.SetLayoutElement(resultsScroll, minHeight: 80, preferredHeight: 100, flexibleHeight: 0);
            _scanResultsContainer = resultsContent;
            resultsScroll.SetActive(false);

            UIStyles.CreateSpacer(card, 8);

            // Current variables list
            var listLabel = UIFactory.CreateLabel(card, "ListLabel", "Defined Variables:", TextAnchor.MiddleLeft);
            listLabel.fontSize = UIStyles.FontSizeSmall;
            listLabel.fontStyle = FontStyle.Bold;
            RegisterUIText(listLabel);

            var scrollObj = UIFactory.CreateScrollView(card, "VarsScroll", out var scrollContent, out _);
            UIFactory.SetLayoutElement(scrollObj, minHeight: 200, preferredHeight: 200, flexibleHeight: 9999);
            _variablesListContainer = scrollContent;

            // Status label
            UIStyles.CreateSpacer(card, 5);
            _variablesStatusLabel = UIFactory.CreateLabel(card, "VarsStatus", "", TextAnchor.MiddleLeft);
            _variablesStatusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_variablesStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Initial populate
            RefreshVariablesList();
        }

        private void OnScanClicked()
        {
            if (_isScanning) return;

            string value = _scanValueInput?.Text?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                SetDynamicText(_variablesStatusLabel, "Enter a value to search for");
                _variablesStatusLabel.color = UIStyles.StatusWarning;
                return;
            }

            _isScanning = true;
            SetDynamicText(_variablesStatusLabel, "Scanning...");
            _variablesStatusLabel.color = UIStyles.TextSecondary;

            // Show results container
            _scanResultsContainer.transform.parent.parent.gameObject.SetActive(true);

            // Clear previous results
            int childCount = _scanResultsContainer.transform.childCount;
            for (int i = childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_scanResultsContainer.transform.GetChild(i).gameObject);

            try
            {
                var candidates = VariableManager.ScanForValue(value);

                if (candidates.Count == 0)
                {
                    var emptyLabel = UIFactory.CreateLabel(_scanResultsContainer, "NoResults",
                        "No matching fields found in game memory.", TextAnchor.MiddleCenter);
                    emptyLabel.color = UIStyles.TextMuted;
                    emptyLabel.fontStyle = FontStyle.Italic;
                    UIFactory.SetLayoutElement(emptyLabel.gameObject, minHeight: 30, flexibleWidth: 9999);

                    SetDynamicText(_variablesStatusLabel, "No results found");
                    _variablesStatusLabel.color = UIStyles.StatusWarning;
                }
                else
                {
                    foreach (var candidate in candidates)
                    {
                        var row = UIFactory.CreateUIObject("Candidate", _scanResultsContainer);
                        UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(row, false, false, true, true, 5, 2, 2, 2, 2);
                        UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

                        // Show the matched value: with partial matches (composed display
                        // strings like "seedA-seedB"), the path alone doesn't tell the
                        // user which piece of the text each candidate holds.
                        string valPreview = (candidate.CurrentValue ?? "").Replace("\r", " ").Replace("\n", " ");
                        if (valPreview.Length > 24) valPreview = valPreview.Substring(0, 24) + "...";
                        string display = $"{candidate.ClassName}.{candidate.FieldPath} = \"{valPreview}\"";
                        if (candidate.IsStatic) display += " (static)";

                        var label = UIFactory.CreateLabel(row, "Label", display, TextAnchor.MiddleLeft);
                        label.fontSize = UIStyles.FontSizeSmall;
                        UIFactory.SetLayoutElement(label.gameObject, flexibleWidth: 9999);

                        var capturedCandidate = candidate;
                        var addBtn = CreateSecondaryButton(row, "Add", "+");
                        UIFactory.SetLayoutElement(addBtn.Component.gameObject, minWidth: 30);
                        addBtn.OnClick += () =>
                        {
                            // Prompt for a name — use the field name as default
                            string varName = capturedCandidate.FieldPath.Split('.').Last();
                            VariableManager.AddVariable(varName, capturedCandidate.ClassName, capturedCandidate.FieldPath);
                            TranslatorCore.SaveCache();
                            RefreshVariablesList();
                            _variablesStatusLabel.text = $"Added: {varName} ({capturedCandidate.ClassName}.{capturedCandidate.FieldPath})";
                            _variablesStatusLabel.color = UIStyles.StatusSuccess;
                        };
                    }

                    SetDynamicText(_variablesStatusLabel, $"Found {candidates.Count} candidate(s). Click + to add.");
                    _variablesStatusLabel.color = UIStyles.StatusSuccess;
                }
            }
            catch (Exception ex)
            {
                _variablesStatusLabel.text = $"Scan error: {ex.Message}";
                _variablesStatusLabel.color = UIStyles.StatusError;
            }

            _isScanning = false;
        }

        private void RefreshVariablesList()
        {
            if (_variablesListContainer == null) return;

            int childCount = _variablesListContainer.transform.childCount;
            for (int i = childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_variablesListContainer.transform.GetChild(i).gameObject);

            var definitions = VariableManager.Definitions;
            if (definitions.Count == 0)
            {
                var emptyLabel = UIFactory.CreateLabel(_variablesListContainer, "Empty",
                    "No variables defined.\nUse Capture to find game variables.",
                    TextAnchor.MiddleCenter);
                emptyLabel.color = UIStyles.TextMuted;
                emptyLabel.fontStyle = FontStyle.Italic;
                UIFactory.SetLayoutElement(emptyLabel.gameObject, minHeight: 40, flexibleWidth: 9999);
                return;
            }

            // Refresh values
            VariableManager.RefreshValues();

            for (int i = 0; i < definitions.Count; i++)
            {
                var def = definitions[i];
                int stableId = def.Id;
                string currentVal = VariableManager.GetValue(stableId);

                var row = UIFactory.CreateUIObject("Var_" + stableId, _variablesListContainer);
                UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(row, false, false, true, true, 5, 2, 2, 2, 2);
                UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);

                // Info
                var infoObj = UIFactory.CreateUIObject("Info", row);
                UIFactory.SetLayoutGroup<VerticalLayoutGroup>(infoObj, false, false, true, true, 0);
                UIFactory.SetLayoutElement(infoObj, flexibleWidth: 9999);

                var nameLabel = UIFactory.CreateLabel(infoObj, "Name",
                    $"[!STR*{stableId}] {def.Name}", TextAnchor.MiddleLeft);
                nameLabel.fontSize = UIStyles.FontSizeSmall;
                nameLabel.fontStyle = FontStyle.Bold;
                UIFactory.SetLayoutElement(nameLabel.gameObject, flexibleWidth: 9999);

                string pathStr = $"{def.ClassName}.{def.FieldPath}";
                string valStr = currentVal != null ? $" = \"{currentVal}\"" : " = (not resolved)";
                var detailLabel = UIFactory.CreateLabel(infoObj, "Detail",
                    pathStr + valStr, TextAnchor.MiddleLeft);
                detailLabel.fontSize = UIStyles.FontSizeSmall - 1;
                detailLabel.color = currentVal != null ? UIStyles.TextSecondary : UIStyles.StatusWarning;
                UIFactory.SetLayoutElement(detailLabel.gameObject, flexibleWidth: 9999);

                // Remove button
                int capturedId = stableId;
                var removeBtn = CreateSecondaryButton(row, "Remove_" + stableId, "X");
                UIFactory.SetLayoutElement(removeBtn.Component.gameObject, minWidth: 30, minHeight: UIStyles.RowHeightNormal);
                removeBtn.OnClick += () =>
                {
                    VariableManager.RemoveVariable(capturedId);
                    TranslatorCore.SaveCache();
                    RefreshVariablesList();
                    SetDynamicText(_variablesStatusLabel, "Variable removed");
                    _variablesStatusLabel.color = UIStyles.TextSecondary;
                };
            }
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
        public void RefreshFromConfig()
        {
            if (_enableFontReplacementToggle != null)
                _enableFontReplacementToggle.isOn = TranslatorCore.Config.enable_font_replacement;
            if (_enableImageReplacementToggle != null)
                _enableImageReplacementToggle.isOn = TranslatorCore.Config.enable_image_replacement;

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
                _enableFontReplacementToggle.isOn = TranslatorCore.Config.enable_font_replacement;
            if (_enableImageReplacementToggle != null)
                _enableImageReplacementToggle.isOn = TranslatorCore.Config.enable_image_replacement;
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
                    _initialFontSettings[fontInfo.Name] = (enabled, fallback, sizePercent, scaleAuto);
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
                    TranslatorCore.SetFontOverrides(new List<FontOverrideRule>(_pendingFontOverrides));
                }

                // Apply behavior settings
                if (_typewritingDetectionToggle != null)
                    TranslatorCore.TypewritingDetection = _typewritingDetectionToggle.isOn;
                if (_concatDetectionToggle != null)
                    TranslatorCore.ConcatDetection = _concatDetectionToggle.isOn;

                // Apply debug toggles — only act when the value actually changed, to avoid
                // unnecessarily restoring/applying when the user didn't touch these.
                if (_enableFontReplacementToggle != null)
                {
                    bool fontEnabled = _enableFontReplacementToggle.isOn;
                    if (fontEnabled != TranslatorCore.Config.enable_font_replacement)
                    {
                        TranslatorCore.Config.enable_font_replacement = fontEnabled;
                        if (!fontEnabled)
                            FontManager.RestoreAllOriginalFonts();
                    }
                }

                if (_enableImageReplacementToggle != null)
                {
                    bool imgEnabled = _enableImageReplacementToggle.isOn;
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
                    _initialFontSettings[fontInfo.Name] = (settings?.enabled ?? true, settings?.fallback, FontManager.GetFontSizePercent(fontInfo.Name), settings?.scale_auto ?? false);
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
        private int CountPendingChanges()
        {
            int count = 0;

            // Fonts - count fonts that differ from initial
            foreach (var kvp in _pendingFontSettings)
            {
                if (_initialFontSettings.TryGetValue(kvp.Key, out var initial))
                {
                    bool enabledDiff = kvp.Value.enabled != initial.enabled;
                    bool fallbackDiff = kvp.Value.fallback != initial.fallback;
                    bool sizeDiff = Math.Abs(kvp.Value.sizePercent - initial.sizePercent) > 0.001f;
                    bool autoDiff = kvp.Value.scaleAuto != initial.scaleAuto;
                    if (enabledDiff || fallbackDiff || sizeDiff || autoDiff)
                    {
                        count++;
                    }
                }
                else
                {
                    // New font not in initial - count as change
                    count++;
                }
            }

            // Exclusions - count adds and removes
            count += _pendingExclusionAdds.Count;
            count += _pendingExclusionRemoves.Count;

            // Font overrides
            if (HasFontOverrideChanges()) count++;

            // Behavior settings
            if (_typewritingDetectionToggle != null && _typewritingDetectionToggle.isOn != TranslatorCore.TypewritingDetection) count++;
            if (_concatDetectionToggle != null && _concatDetectionToggle.isOn != TranslatorCore.ConcatDetection) count++;

            // Debug toggles (persisted in config.json)
            if (_enableFontReplacementToggle != null && _enableFontReplacementToggle.isOn != TranslatorCore.Config.enable_font_replacement) count++;
            if (_enableImageReplacementToggle != null && _enableImageReplacementToggle.isOn != TranslatorCore.Config.enable_image_replacement) count++;

            // Font sharpness (max atlas budget)
            if (_fontAtlasSizeDropdown != null && _pendingAtlasSize != TranslatorCore.Config.max_font_atlas_size) count++;

            return count;
        }

        /// <summary>
        /// Updates the Apply button text based on pending changes count.
        /// Shows "Apply (x)" when there are changes, "Close" when there are none.
        /// </summary>
        private bool HasFontOverrideChanges()
        {
            if (_pendingFontOverrides.Count != _initialFontOverrides.Count)
                return true;

            for (int i = 0; i < _pendingFontOverrides.Count; i++)
            {
                var p = _pendingFontOverrides[i];
                var o = _initialFontOverrides[i];
                if (p.match != o.match ||
                    p.replacement != o.replacement ||
                    Math.Abs(p.size_multiplier - o.size_multiplier) > 0.001f ||
                    p.enabled != o.enabled)
                    return true;
            }
            return false;
        }

        private void UpdateApplyButtonText()
        {
            if (_applyBtn == null) return;

            int changes = CountPendingChanges();
            string label = changes > 0 ? $"Apply ({changes})" : "Close";
            // Translate at set-time (cache/placeholder-aware) — no race with the async pipeline.
            _applyBtn.ButtonText.text = TranslatorCore.TranslateOwnUIDynamic(label, _applyBtn.ButtonText);
        }

        #endregion
    }
}
