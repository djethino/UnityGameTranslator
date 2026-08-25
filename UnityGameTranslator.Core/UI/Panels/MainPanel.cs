using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Layout states for context-aware UI display.
    /// </summary>
    /// <summary>
    /// Layout states for context-aware UI display.
    ///
    /// 🔴 **These describe the TRANSLATION, never the account.** There used to be a `NotLogged`
    /// state, tested before every other, which meant somebody with no account was shown a sign-up
    /// pitch instead of what their translation was doing. Whether to invite somebody to sign in is
    /// a separate question, read from the account where it is needed.
    ///
    /// ⚠ **No state is called "Contributor".** Becoming a Branch IS contributing, so a second word
    /// for it would put two names on one thing across three products — and this state is not a
    /// Branch anyway: it is somebody holding a lineage that is not theirs, who has diverged and
    /// sent nothing. One becomes a Branch by uploading.
    /// </summary>
    public enum LayoutState
    {
        NoLocal,                // Show download prominent
        OwnerMain,              // Status + Update + Review Branches
        OwnerBranch,            // Status + Upload + Fork option
        HoldingAnothersLineage, // Contribute (branch) / Download / Fork — three choices
        VisitorDiffUuid         // Download with lineage warning
    }

    /// <summary>
    /// Main settings panel. Shows translation status, account info, sync status, and action buttons.
    /// Context-aware layout adapts to user state.
    /// </summary>
    public class MainPanel : TranslatorPanelBase
    {
        public override string Name => "Unity Game Translator";

        // ⚠ **The extra 30 is the scrollbar's.** The cards inside are sized from PanelWidth once,
        // at construction; the viewport is NOT, because DynamicScrollbar takes 28 pixels off it the
        // moment the content is long enough to scroll. At 450 the two figures crossed and labels
        // lost their last characters — only on the screens long enough to scroll, which is why it
        // looked intermittent.
        //
        // 🔴 **580, and both raises were measured rather than guessed.**
        //
        // 480 -> 520: two adorned buttons do not fit in 480. The fitter's own trace reports
        // "Upload Translation" at 199 and "Review on Website" at 200 once their scope marks are
        // counted, and the card at that minimum offered 352 — the row fitted by about a pixel on
        // this machine's font and not at all on the next, which is exactly the kind of margin that
        // reads as an intermittent bug.
        //
        // 520 -> 580: the contributions line is one line by design — "59 to review: 24 new (H 21,
        // A 3) · 35 differing (V 17, A 18)" — and it grows with the work: a quality more on either
        // side adds a chip and its count. At 520 it fitted only while both groups held two
        // qualities. A row that is one line ON PURPOSE has to be given the width that keeps it one.
        public override int MinWidth => 580;
        public override int MinHeight => 350;
        public override int PanelWidth => 580;
        public override int PanelHeight => 600;

        protected override int MinPanelHeight => 350;

        // The Community tab embeds a scrollable translation list that benefits from
        // extra room when the user enlarges the window.
        protected override bool HasFlexibleContent => true;

        // UI references - Account section
        private Text _accountLabel;
        private ButtonRef _loginLogoutBtn;

        // UI references - Translation info section (legacy, hidden when StatusCard is shown)
        private GameObject _translationInfoSection;
        private Text _entriesLabel;
        private Text _targetLabel;
        private Text _sourceLabel;
        private Text _roleLabel;
        private Text _syncStatusLabel;
        private Text _aiStatusLabel;

        // UI references - Resources link
        private GameObject _resourcesLinkSection;
        private Text _backupsLabel;
        private Text _resourcesByLabel;
        private Text _resourcesUrlLabel;
        private ButtonRef _resourcesLinkBtn;

        // UI references - Actions section
        private ButtonRef _uploadBtn;
        private Text _uploadHintLabel;
        private ButtonRef _reviewOnWebsiteBtn;
        private ButtonRef _compareWithServerBtn;
        private ButtonRef _editDetailsBtn;
        private ButtonRef _updateFromMainBtn;
        private bool _updateFromMainInFlight;
        private ButtonRef _forkBtn;
        private Text _roleActionsHint;
        private Components.HelpZone _helpZone;

        // UI references - Community Translations section
        private GameObject _communitySection;
        private Text _communityGameLabel;
        private ButtonRef _searchBtn;
        private TranslationList _translationList;
        private ButtonRef _downloadBtn;

        // UI references - Context-aware sections
        private StatusCard _statusCard;
        private GameObject _loginCTASection;
        private ButtonRef _loginCTABtn;
        private GameObject _statusSection;

        // UI references - the three choices when holding another lineage (GAP 8)
        private GameObject _lineageChoiceSection;
        /// <summary>
        /// The two rows of Actions, held so they can be hidden when nothing in them is showing.
        ///
        /// 🔴 **A row keeps its height with every child hidden.** Each is built at RowHeightLarge,
        /// so a state that switches all of their buttons off left two empty bands inside the card —
        /// one above the visible controls and one below. Turning off a button is not turning off
        /// the space it was standing in.
        /// </summary>
        /// <summary>The three rows and their sentences, so each can say why it is closed.</summary>
        private GameObject _branchRow;
        private GameObject _mergeRow;
        private GameObject _downloadRow;
        private ButtonRef _mergeWithMainBtn;
        private Text _mergeDesc;
        private Text _branchDesc;
        private Text _downloadDesc;

        private GameObject _syncActionsRow;
        private GameObject _roleActionsRow;

        private ButtonRef _contributeAsBranchBtn;
        private ButtonRef _downloadLatestBtn;
        private ButtonRef _createIndependentBtn;

        // UI references - Guidance messages (GAP 9)
        private GameObject _guidanceSection;
        private Text _guidanceLabel;

        // UI references - Mod update banner
        private GameObject _modUpdateBanner;
        private Text _modUpdateLabel;
        private ButtonRef _modUpdateBtn;
        private ButtonRef _modManagerBtn;

        // Tab system
        private TabBar _tabBar;
        private const string TAB_MY_TRANSLATION = "My Translation";
        private const string TAB_COMMUNITY = "Community";

        // Current layout state (cached for efficiency)
        private LayoutState _currentLayoutState = LayoutState.NoLocal;

        public MainPanel(UIBase owner) : base(owner)
        {
            // Note: Components initialized in ConstructPanelContent() - base constructor calls ConstructUI() first
        }

        protected override void ConstructPanelContent()
        {
            // Initialize components (must be here, not in constructor - base calls ConstructUI first)
            _translationList = new TranslationList();

            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            // === FIXED HEADER (outside the scroll — only tab content scrolls) ===
            var header = CreateFixedHeader();

            // No big title here — the window title bar already shows the mod name (redundant, wasted height).

            // Account Section (compact, inline)
            CreateAccountSection(header);

            UIStyles.CreateSpacer(header, 5);

            // Mod Update Banner (between account and tabs, visible only when update available)
            CreateModUpdateBanner(header);

            // === TAB BAR (buttons in the fixed header, contents in the scroll area) ===
            _tabBar = new TabBar();
            _tabBar.CreateUI(header, scrollContent);

            // Create tab contents - each tab will create its own card
            var myTranslationTab = _tabBar.AddTab(TAB_MY_TRANSLATION);
            var communityTab = _tabBar.AddTab(TAB_COMMUNITY);

            _helpZone?.Describe(_tabBar.GetTabButton(TAB_MY_TRANSLATION),
                "Your own translation for this game: its sync status, role, and the actions you can take on it.");
            _helpZone?.Describe(_tabBar.GetTabButton(TAB_COMMUNITY),
                "Translations other players shared for this game. Search and download one to use it.");

            // Register tab texts for localization
            foreach (var text in _tabBar.GetTabButtonTexts())
            {
                RegisterUIText(text);
            }

            // === MY TRANSLATION TAB (content in a stretching card) ===
            var myTransCard = CreateAdaptiveCard(myTranslationTab, "MyTranslationCard", PanelWidth - 60, stretchVertically: true);

            // Login CTA Section (only visible when not logged in)
            CreateLoginCTASection(myTransCard);

            // Status Section with StatusCard (visible when logged in + has local)
            CreateStatusSection(myTransCard);

            UIStyles.CreateSpacer(myTransCard, 5);

            // Legacy Translation Info Section (kept for backward compatibility, will be hidden when StatusCard is shown)
            CreateTranslationInfoSection(myTransCard);

            UIStyles.CreateSpacer(myTransCard, 10);

            // Actions Section (context-dependent)
            CreateActionsSection(myTransCard);

            // The three choices offered when holding another lineage (GAP 8: HoldingAnothersLineage state)

            // Guidance Section (GAP 9: contextual messages)
            CreateGuidanceSection(myTransCard);

            // Collapsed glossary for the sharing model vocabulary
            CreateGlossarySection(myTransCard);

            // === COMMUNITY TAB (content in a stretching card) ===
            var communityCard = CreateAdaptiveCard(communityTab, "CommunityCard", PanelWidth - 60, stretchVertically: true);
            CreateCommunitySection(communityCard);

            // Bottom buttons - in fixed footer (outside scroll). These three concern the whole
            // mod and belong to every tab; a tab's own action has no business here — added as a
            // fourth it pushed Close off the edge of the row.
            var transParamsBtn = CreateSecondaryButton(buttonRow, "TransParamsBtn", "Translation Tools");
            transParamsBtn.OnClick += () => TranslatorUIManager.TranslationParamsPanel?.SetActive(true);
            RegisterUIText(transParamsBtn.ButtonText);
            _helpZone?.Describe(transParamsBtn.Component.gameObject,
                "Text editors, exclusions, fonts, images and variables");

            var optionsBtn = CreateSecondaryButton(buttonRow, "OptionsBtn", "Mod Options");
            optionsBtn.OnClick += () => TranslatorUIManager.OptionsPanel?.SetActive(true);
            RegisterUIText(optionsBtn.ButtonText);
            _helpZone?.Describe(optionsBtn.Component.gameObject,
                "General settings: hotkeys, online mode, translation backend");

            var closeBtn = CreatePrimaryButton(buttonRow, "CloseBtn", "Close");
            closeBtn.OnClick += () => SetActive(false);
            RegisterUIText(closeBtn.ButtonText);
            _helpZone?.Describe(closeBtn.Component.gameObject,
                "Close this window. Translation and syncing keep running in the background.");

            RefreshUI();
        }

        /// <summary>
        /// Selects a tab by name. Used by notifications to open specific tabs.
        /// </summary>
        /// <param name="tabName">Tab name (use TAB_MY_TRANSLATION or TAB_COMMUNITY constants)</param>
        public void SelectTab(string tabName)
        {
            _tabBar?.SelectTab(tabName);
        }

        /// <summary>
        /// Opens the Community tab. Convenience method for external callers.
        /// </summary>
        public void OpenCommunityTab()
        {
            SelectTab(TAB_COMMUNITY);
        }

        /// <summary>
        /// Opens the My Translation tab. Convenience method for external callers.
        /// </summary>
        public void OpenMyTranslationTab()
        {
            SelectTab(TAB_MY_TRANSLATION);
        }

        private void CreateAccountSection(GameObject parent)
        {
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "AccountSectionLabel", "Account");
            RegisterUIText(sectionTitle);

            // Grouped in a card (like "Current Translation") so the account block reads as a unit.
            var accountBox = UIStyles.CreateAdaptiveCard(parent, "AccountBox", PanelWidth - 60);

            var accountRow = UIStyles.CreateFormRow(accountBox, "AccountRow", UIStyles.RowHeightLarge);

            _accountLabel = UIFactory.CreateLabel(accountRow, "AccountLabel", "Not connected", TextAnchor.MiddleLeft);
            _accountLabel.fontStyle = FontStyle.Italic;
            _accountLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_accountLabel.gameObject, flexibleWidth: 9999);
            RegisterExcluded(_accountLabel);

            _loginLogoutBtn = CreateSecondaryButton(accountRow, "LoginLogoutBtn", "Login", 80);
            _loginLogoutBtn.OnClick += OnLoginLogoutClicked;
            RegisterExcluded(_loginLogoutBtn.ButtonText);
            _helpZone?.Describe(_loginLogoutBtn.Component.gameObject,
                "An account is only needed to SHARE translations. Downloading and playing work without one.");
        }

        private void CreateModUpdateBanner(GameObject parent)
        {
            // Mod update banner - colored box at top when update available
            _modUpdateBanner = UIFactory.CreateHorizontalGroup(parent, "ModUpdateBanner", false, false, true, true, 8);
            UIFactory.SetLayoutElement(_modUpdateBanner, minHeight: UIStyles.RowHeightLarge, flexibleWidth: 9999);
            UIStyles.SetBackground(_modUpdateBanner, UIStyles.NotificationSuccess);

            var padding = _modUpdateBanner.GetComponent<HorizontalLayoutGroup>();
            if (padding != null)
            {
                padding.padding = Compat.MakeRectOffset(10, 10, 5, 5);
                padding.childAlignment = TextAnchor.MiddleLeft;
            }

            _modUpdateLabel = UIFactory.CreateLabel(_modUpdateBanner, "ModUpdateLabel", "Update available: v?.?.?", TextAnchor.MiddleLeft);
            _modUpdateLabel.fontStyle = FontStyle.Bold;
            _modUpdateLabel.color = Color.white;
            UIFactory.SetLayoutElement(_modUpdateLabel.gameObject, flexibleWidth: 9999);
            RegisterExcluded(_modUpdateLabel);

            // ⚠ Before the download button, and it is the only place on this banner where order is
            // a statement: read left to right, the tool that does the whole job comes first and the
            // manual zip stays available beside it. Neither is taken away.
            _modManagerBtn = UIFactory.CreateButton(_modUpdateBanner, "ModManagerBtn", "Get Manager");
            UIFactory.SetLayoutElement(_modManagerBtn.Component.gameObject, minWidth: 110, minHeight: UIStyles.RowHeightNormal);
            UIStyles.SetBackground(_modManagerBtn.Component.gameObject, UIStyles.ButtonSecondary);
            _modManagerBtn.OnClick += OnModManagerClicked;
            RegisterExcluded(_modManagerBtn.ButtonText);
            _helpZone?.Describe(_modManagerBtn.Component.gameObject,
                "The Manager installs and updates the mod for every game on this machine. "
                + "Opens it when it is already here, otherwise opens the page to get it.");

            _modUpdateBtn = UIFactory.CreateButton(_modUpdateBanner, "ModUpdateBtn", "Download");
            UIFactory.SetLayoutElement(_modUpdateBtn.Component.gameObject, minWidth: 90, minHeight: UIStyles.RowHeightNormal);
            UIStyles.SetBackground(_modUpdateBtn.Component.gameObject, UIStyles.ButtonPrimary);
            _modUpdateBtn.OnClick += OnModUpdateClicked;
            RegisterExcluded(_modUpdateBtn.ButtonText);
            _helpZone?.Describe(_modUpdateBtn.Component.gameObject,
                "Get the newer mod version: downloads it if available, otherwise opens the release page in your browser.");

            // Start hidden
            _modUpdateBanner.SetActive(false);
        }

        private void OnModUpdateClicked()
        {
            var info = TranslatorUIManager.ModUpdateInfo;
            string url = info?.DownloadUrl ?? info?.ReleaseUrl;
            if (!string.IsNullOrEmpty(url))
            {
                TranslatorCore.OpenUrlSafe(url);
            }
        }

        /// <summary>
        /// Opens the Manager, or the page to get it from — ManagerLink decides which, and does it.
        /// </summary>
        private void OnModManagerClicked()
        {
            ManagerLink.Open();

            // Looked at again next time this banner is drawn: somebody who just went to fetch it
            // may come back with it installed, and the button should stop offering what they have.
            ManagerLink.Forget();
        }

        private void OnResourcesLinkClicked()
        {
            var serverState = TranslatorCore.ServerState;
            string url = serverState?.ResourcesUrl;
            if (!string.IsNullOrEmpty(url))
            {
                TranslatorCore.LogInfo($"[MainPanel] Opening external resources: {url}");
                TranslatorCore.OpenUrlSafe(url);
            }
        }

        private void CreateLoginCTASection(GameObject parent)
        {
            // Login CTA - prominent call-to-action for not logged in users
            _loginCTASection = UIFactory.CreateVerticalGroup(parent, "LoginCTASection", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_loginCTASection, flexibleWidth: 9999);

            var ctaCard = UIStyles.CreateAdaptiveCard(_loginCTASection, "CTACard", PanelWidth - 60);
            UIStyles.SetBackground(ctaCard, UIStyles.CardElevated);  // a prominent CTA — must read as a card, not blend into the panel

            var ctaTitle = UIFactory.CreateLabel(ctaCard, "CTATitle", "Login to sync your translations", TextAnchor.MiddleCenter);
            ctaTitle.fontStyle = FontStyle.Bold;
            ctaTitle.fontSize = UIStyles.FontSizeNormal;
            ctaTitle.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(ctaTitle.gameObject, minHeight: UIStyles.RowHeightMedium);
            RegisterUIText(ctaTitle);

            var ctaDesc = UIFactory.CreateLabel(ctaCard, "CTADesc",
                "Sync your work across devices and contribute to community translations.",
                TextAnchor.MiddleCenter);
            ctaDesc.fontSize = UIStyles.FontSizeSmall;
            ctaDesc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(ctaDesc.gameObject, minHeight: UIStyles.RowHeightMedium);
            RegisterUIText(ctaDesc);

            UIStyles.CreateSpacer(ctaCard, 5);

            var ctaBtnRow = UIStyles.CreateFormRow(ctaCard, "CTABtnRow", UIStyles.RowHeightLarge, 0);
            var rowLayout = ctaBtnRow.GetComponent<HorizontalLayoutGroup>();
            if (rowLayout != null) rowLayout.childAlignment = TextAnchor.MiddleCenter;

            _loginCTABtn = CreatePrimaryButton(ctaBtnRow, "CTALoginBtn", "Create Account / Login", 200);
            UIStyles.SetBackground(_loginCTABtn.Component.gameObject, UIStyles.ButtonSuccess);
            _loginCTABtn.OnClick += () => TranslatorUIManager.LoginPanel?.SetActive(true);
            RegisterUIText(_loginCTABtn.ButtonText);
            _helpZone?.Describe(_loginCTABtn.Component.gameObject,
                "An account is only needed to SHARE translations. Downloading and playing work without one.");
        }

        private void CreateStatusSection(GameObject parent)
        {
            // 🔴 **The title goes OUTSIDE the frame, as "Actions" does.** It used to sit inside the
            // section, which carries a background — so one heading was written on the box it names
            // and the other above it, and the two sections of this tab read as different kinds of
            // thing. A heading names what follows; it is not part of it.
            //
            // ⚠ Written exactly the way Actions writes it — CreateSectionTitle straight into the
            // parent, no row of its own. The row existed to give the title the left margin of the
            // content BELOW it, which is a problem that only arises inside the frame.
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "StatusSectionLabel", "Current Translation");
            RegisterUIText(sectionTitle);

            // Status section - shows sync status using StatusCard widget
            _statusSection = UIFactory.CreateVerticalGroup(parent, "StatusSection", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_statusSection, flexibleWidth: 9999);

            // Create StatusCard widget
            _statusCard = new StatusCard();
            _statusCard.CreateUI(_statusSection);
            _helpZone?.Describe(_statusCard.Root,
                "Your translation at a glance: sync state with the website, your role (Main = owner, Branch = contributor), and quality (Human / Validated / AI lines)");

            // 🔴 **One line, and a way in — not a section.** Backups are the HISTORY of the very
            // thing this section shows, so this is where somebody looks for them; but a dozen rows
            // with three verbs each would swamp the card that says what the translation IS. The
            // list lives in its own panel, exactly as Merge, Upload and Login do.
            //
            // ⚠ Not in "Actions" either: that row is about the world — publishing, comparing,
            // arbitrating, forking. What you keep on your own machine is a different subject.
            var backupsRow = UIStyles.CreateFormRow(_statusSection, "BackupsRow",
                                                    UIStyles.RowHeightNormal, 8);

            _backupsLabel = UIFactory.CreateLabel(backupsRow, "BackupsLabel", "", TextAnchor.MiddleLeft);
            _backupsLabel.color = UIStyles.TextSecondary;
            _backupsLabel.fontSize = UIStyles.FontSizeHint;
            UIFactory.SetLayoutElement(_backupsLabel.gameObject, flexibleWidth: 9999);
            RegisterExcluded(_backupsLabel);

            var backupsBtn = CreateSecondaryButton(backupsRow, "BackupsBtn", "Backups…");
            backupsBtn.OnClick += () => TranslatorUIManager.BackupsPanel?.ShowPanel();
            RegisterUIText(backupsBtn.ButtonText);
            _helpZone?.Describe(backupsBtn.Component.gameObject,
                "Your translation as it stood at earlier moments — kept here when something "
                + "replaces it, and whenever you ask.");

            // External Resources section (visible only when ResourcesUrl is set)
            _resourcesLinkSection = UIFactory.CreateVerticalGroup(_statusSection, "ResourcesLinkSection", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_resourcesLinkSection, flexibleWidth: 9999);
            UIStyles.SetBackground(_resourcesLinkSection, UIStyles.CardElevated);
            var rlPadding = _resourcesLinkSection.GetComponent<VerticalLayoutGroup>();
            if (rlPadding != null)
                rlPadding.padding = Compat.MakeRectOffset(12, 12, 10, 10);

            // "External Resources uploaded by @username"
            _resourcesByLabel = UIFactory.CreateLabel(_resourcesLinkSection, "ResourcesByLabel",
                "External Resources", TextAnchor.MiddleLeft);
            _resourcesByLabel.fontStyle = FontStyle.Bold;
            _resourcesByLabel.fontSize = UIStyles.FontSizeSmall;
            _resourcesByLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_resourcesByLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // URL displayed in FULL, never shortened: the user must see where the link leads before
            // opening it. A long URL therefore wraps, so the label has to reserve the height it
            // draws — otherwise its second line ran under the button below.
            _resourcesUrlLabel = UIFactory.CreateLabel(_resourcesLinkSection, "ResourcesUrlLabel",
                "", TextAnchor.UpperLeft);
            _resourcesUrlLabel.fontSize = UIStyles.FontSizeHint;
            _resourcesUrlLabel.color = UIStyles.TextAccent;
            UIFactory.SetLayoutElement(_resourcesUrlLabel.gameObject, minHeight: UIStyles.RowHeightSmall,
                flexibleWidth: 9999);
            UIFactory.ConfigureAutoHeight(_resourcesUrlLabel, UIStyles.SmallSpacing);

            // Open button (centered), kept clear of the URL above it
            var openBtnRow = UIFactory.CreateHorizontalGroup(_resourcesLinkSection, "OpenBtnRow", false, false, true, true, 0);
            UIFactory.SetLayoutElement(openBtnRow, minHeight: UIStyles.RowHeightLarge, flexibleWidth: 9999);
            var openBtnLayout = openBtnRow.GetComponent<HorizontalLayoutGroup>();
            if (openBtnLayout != null)
            {
                openBtnLayout.childAlignment = TextAnchor.MiddleCenter;
                openBtnLayout.padding = Compat.MakeRectOffset(0, 0, UIStyles.SmallSpacing, 0);
            }

            _resourcesLinkBtn = CreateSecondaryButton(openBtnRow, "ResourcesOpenBtn", "Open in Browser", 140);
            // Fill the card width (bounded, no floating/overflowing button) and keep a consistent height.
            UIFactory.SetLayoutElement(_resourcesLinkBtn.Component.gameObject, minWidth: 140, minHeight: UIStyles.ButtonHeight, flexibleWidth: 9999);
            UIStyles.SetBackground(_resourcesLinkBtn.Component.gameObject, UIStyles.ButtonLink);
            _resourcesLinkBtn.OnClick += OnResourcesLinkClicked;
            RegisterUIText(_resourcesLinkBtn.ButtonText);
            _helpZone?.Describe(_resourcesLinkBtn.Component.gameObject,
                "Open the external link the translation's author attached (custom fonts or images). Not hosted by us.");

            // Disclaimer
            var disclaimer = UIFactory.CreateLabel(_resourcesLinkSection, "ResourcesDisclaimer",
                "Third-party content. We are not responsible for external links.",
                TextAnchor.MiddleLeft);
            disclaimer.fontSize = UIStyles.FontSizeHint;
            disclaimer.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(disclaimer.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(disclaimer);

            _resourcesLinkSection.SetActive(false);
        }

        private void CreateTranslationInfoSection(GameObject parent)
        {
            // Wrap in container for visibility control (legacy section, hidden when StatusCard is shown)
            _translationInfoSection = UIFactory.CreateVerticalGroup(parent, "TranslationInfoSection", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_translationInfoSection, flexibleWidth: 9999);

            var sectionTitle = UIStyles.CreateSectionTitle(_translationInfoSection, "TranslationSectionLabel", "Current Translation");
            RegisterUIText(sectionTitle);

            var infoBox = CreateSection(_translationInfoSection, "TranslationBox");

            _entriesLabel = UIFactory.CreateLabel(infoBox, "EntriesLabel", "Entries: 0", TextAnchor.MiddleLeft);
            _entriesLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_entriesLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(_entriesLabel);

            _targetLabel = UIFactory.CreateLabel(infoBox, "TargetLabel", "Target: auto", TextAnchor.MiddleLeft);
            _targetLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_targetLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(_targetLabel);

            _sourceLabel = UIFactory.CreateLabel(infoBox, "SourceLabel", "Source: Local", TextAnchor.MiddleLeft);
            _sourceLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_sourceLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(_sourceLabel);

            _roleLabel = UIFactory.CreateLabel(infoBox, "RoleLabel", "", TextAnchor.MiddleLeft);
            _roleLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_roleLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(_roleLabel);

            _syncStatusLabel = UIFactory.CreateLabel(infoBox, "SyncStatusLabel", "", TextAnchor.MiddleLeft);
            _syncStatusLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_syncStatusLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(_syncStatusLabel);

            _aiStatusLabel = CreateSmallLabel(infoBox, "AIStatusLabel", "");
            RegisterExcluded(_aiStatusLabel);
        }

        private void CreateActionsSection(GameObject parent)
        {
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "ActionsSectionLabel", "Actions");
            RegisterUIText(sectionTitle);

            var actionsBox = CreateSection(parent, "ActionsBox");

            // Pushing your content and inspecting what you are about to push are the same
            // question, so they share a row. Everything else (reviewing others' work, editing the
            // description, forking) is a different subject and lives on the row below.
            // 🔴 **Buttons and their sentences are centred together.** The sentences were left
            // aligned on the reasoning that prose starts where the eye looks for its first word —
            // true on a page, wrong here: each sentence belongs to the button above it, and pulling
            // it to the far left left the block looking pinned to one edge with its captions
            // adrift. Judged on the screen rather than from the rule, which is where it was wrong.
            _syncActionsRow = UIStyles.CreateFormRow(actionsBox, "SyncActionsRow", UIStyles.RowHeightLarge, UIStyles.SmallSpacing);
            var syncRow = _syncActionsRow;
            var syncLayout = syncRow.GetComponent<HorizontalLayoutGroup>();
            if (syncLayout != null) syncLayout.childAlignment = TextAnchor.MiddleCenter;

            // 🔴 **These are FLOORS for a button with no label yet, nothing more.** What a button
            // ends up wide is measured by ButtonLabelFitter from its label plus whatever shares its
            // row — the marks included. An earlier note here said Adorn raised the minimum by what
            // it inserts; that stopped being true when the fitter took the job over, and a figure
            // computed by hand would now be overwritten on the first label change anyway.
            // 🔴 **No flexibleWidth: an action button is as wide as what it says.** This one had it
            // and its neighbours did not, so it alone stretched as the window grew — and, while the
            // measurement below was wrong, it alone looked right, because a button handed the room
            // that is left never has to ask how much it needs. That is what made a fault shared by
            // the whole row read as "these buttons are not built the same way".
            //
            // ⚠ Stretching IS right elsewhere and stays: a lone button under a description
            // (Contribute as Branch, Open in Browser) fills the card because it answers for the
            // whole block. Two actions side by side do not.
            _uploadBtn = CreatePrimaryButton(syncRow, "UploadBtn", "Upload Translation", 150);
            _uploadBtn.OnClick += OnUploadClicked;
            // Publier laisse les deux côtés porteurs du même fichier : c'est Both, pas Server.
            // ⚠ Server voudrait dire « le publié a le résultat, pas cette machine » — ce qui ne
            // peut pas arriver depuis un jeu, puisque le fichier envoyé est celui d'ici.
            ScopeMarks.Adorn(_uploadBtn, EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: true));
            RegisterExcluded(_uploadBtn.ButtonText);
            _helpZone?.Describe(_uploadBtn.Component.gameObject,
                "Send your local translation to the website so others can use it");

            // Compare with Server — belongs next to the push it qualifies
            _compareWithServerBtn = CreateSecondaryButton(syncRow, "CompareBtn", "Compare", 85);
            UIStyles.SetBackground(_compareWithServerBtn.Component.gameObject, UIStyles.ButtonSecondary);
            _compareWithServerBtn.OnClick += OnCompareWithServerClicked;
            // 🔴 **The same word opens this page in both directions, and only the marks say which.**
            // This one is the publishing direction (toLocal: false): what is validated there
            // updates the online version. The Compare in the settings window opens the same screen
            // towards the local file. Nothing else on the button distinguishes them — which is
            // exactly what the scope marks are for, rather than a longer label repeating it.
            ScopeMarks.Adorn(_compareWithServerBtn,
                EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: true));
            RegisterExcluded(_compareWithServerBtn.ButtonText);
            // ⚠ It used to say "See the differences", promising a read. Validating there writes.
            _helpZone?.Describe(_compareWithServerBtn.Component.gameObject,
                "Compare your local file with the published version and choose line by line what to publish");

            // 🔴 **The three lineage choices live HERE, in Actions.** They had a section of their
            // own titled "What would you like to do?", directly under a row already offering
            // "Contribute" — the same act, three inches apart, one of them without the guards. Two
            // headings for one question, and the second one full-width where every other action
            // button on this card is the size of its own label.
            CreateLineageChoices(actionsBox);

            _uploadHintLabel = UIStyles.CreateHint(actionsBox, "UploadHintLabel", "", centred: true);
            RegisterExcluded(_uploadHintLabel);

            // Role-specific action buttons row
            // Centred, like the row above it — see there.
            _roleActionsRow = UIStyles.CreateFormRow(actionsBox, "RoleActionsRow", UIStyles.RowHeightLarge);
            var roleActionsRow = _roleActionsRow;
            var rowLayout = roleActionsRow.GetComponent<HorizontalLayoutGroup>();
            if (rowLayout != null) rowLayout.childAlignment = TextAnchor.MiddleCenter;

            // Review on Website button (Main only) - opens page to review branches
            //
            // ⚠ Carries its count — "Review Branches (3)" — so how many are waiting is read where
            // the decision is taken. RegisterExcluded, not RegisterUIText: the label is written by
            // the code on every refresh, and letting the async pipeline write it too would put two
            // writers on one Text. The pipeline turns the number into a placeholder, so every count
            // shares one cache entry.
            _reviewOnWebsiteBtn = CreateSecondaryButton(roleActionsRow, "ReviewBtn", "Review Branches", 105);
            UIStyles.SetBackground(_reviewOnWebsiteBtn.Component.gameObject, UIStyles.ButtonLink);
            _reviewOnWebsiteBtn.OnClick += OnReviewOnWebsiteClicked;
            // ⚠ Taking in a contribution rewrites the PUBLISHED Main and leaves this machine's file
            // untouched — the one action here whose result never comes back to the game on its own.
            // Marked accordingly: published alone, not both.
            ScopeMarks.Adorn(_reviewOnWebsiteBtn,
                EditScope.SideAfter(onThisMachine: false, yourPublishedCopy: true));
            RegisterExcluded(_reviewOnWebsiteBtn.ButtonText);
            _helpZone?.Describe(_reviewOnWebsiteBtn.Component.gameObject,
                "Open the website to accept or reject changes proposed by other players");

            // Edit details (owners) — the description and the resources link were only reachable
            // through the upload screen, which is closed once everything is in sync. Fixing a dead
            // link or rewording a description then had no path at all.
            _editDetailsBtn = CreateSecondaryButton(roleActionsRow, "EditDetailsBtn", "Edit details", 90);
            UIStyles.SetBackground(_editDetailsBtn.Component.gameObject, UIStyles.ButtonSecondary);
            _editDetailsBtn.OnClick += OnEditDetailsClicked;
            // ⚠ Opens a panel in the game, where a second confirmation actually sends — the mark
            // says where this ends up, not that it happens on click. Same as Start Text Editor,
            // which is adorned for the file it will eventually write.
            ScopeMarks.Adorn(_editDetailsBtn,
                EditScope.SideAfter(onThisMachine: false, yourPublishedCopy: true));
            RegisterUIText(_editDetailsBtn.ButtonText);
            _helpZone?.Describe(_editDetailsBtn.Component.gameObject,
                "Change the description and the resources link of your published translation, without waiting for new translated lines");

            // Merge with Main (Branch only) — the other direction of the exchange.
            // A branch could publish its work but never take in what the Main had
            // published since: it drifted further apart with every update, without
            // anything ever saying so.
            _updateFromMainBtn = CreateSecondaryButton(roleActionsRow, "UpdateFromMainBtn", "Merge with Main", 120);
            UIStyles.SetBackground(_updateFromMainBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _updateFromMainBtn.OnClick += OnUpdateFromMainClicked;
            // ⚠ Brings the Main INTO this machine's file and publishes nothing — the opposite side
            // from its two neighbours on this row. It sat between two adorned buttons saying
            // nothing, which is the one arrangement that makes a mark look decorative.
            ScopeMarks.Adorn(_updateFromMainBtn,
                EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false));
            RegisterUIText(_updateFromMainBtn.ButtonText);
            _helpZone?.Describe(_updateFromMainBtn.Component.gameObject,
                "Bring in what the original translation added or corrected since your last update. Your own lines are kept, and you review everything before it applies.");

            // Fork button (Branch only) - creates independent fork
            _forkBtn = CreateSecondaryButton(roleActionsRow, "ForkBtn", "Fork", 80);
            UIStyles.SetBackground(_forkBtn.Component.gameObject, UIStyles.ButtonDanger);
            // 🔴 **The same handler as "Create Independent" below, because it is the same act.**
            // There were two, and only one of them ever got a correction: this one still said "You
            // will become the Main owner" — which forking does not do, it sends nothing — and
            // opened the upload screen for people who could not use it.
            _forkBtn.OnClick += OnCreateIndependentClicked;
            // Forker crée une lignée à soi sur le site, à partir du fichier d'ici : après, les deux
            // portent la même chose.
            ScopeMarks.Adorn(_forkBtn, EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: true));
            RegisterUIText(_forkBtn.ButtonText);
            _helpZone?.Describe(_forkBtn.Component.gameObject,
                "Leave the owner's translation and continue on your own — asks for confirmation first");

            // One-line explanation for whichever role buttons are visible
            _roleActionsHint = UIStyles.CreateHint(actionsBox, "RoleActionsHint", "", centred: true);
            RegisterExcluded(_roleActionsHint);
        }

        /// <summary>
        /// The three answers open to somebody holding a lineage that is not theirs, inside Actions.
        ///
        /// 🔴 **No heading of their own.** They had one — "What would you like to do?" — directly
        /// under a row already offering "Contribute", which is the first of these three. One
        /// question asked twice, and the copy with the heading was the one without the guards.
        ///
        /// ⚠ **Each button is the size of its label, and centred**, like every other action on this
        /// card. They stretched the full width, which made them read as a different kind of control
        /// on a different screen. Their sentences stay under them: prose starts at the left margin,
        /// buttons sit in the middle — the rule the sync row above already states at length.
        /// </summary>
        /// <summary>
        /// Switches a row off when nothing inside it is showing, and back on when something is.
        ///
        /// ⚠ Both directions, always. Hiding only would leave the row gone for the rest of the
        /// session the first time a state emptied it — including the states that fill it again.
        ///
        /// ⚠ Manual loop rather than LINQ over the children: this runs on every panel refresh, and
        /// `foreach` over a Transform does not work on IL2CPP.
        /// </summary>
        private static void HideIfEmpty(GameObject row)
        {
            if (row == null) return;

            bool anything = false;
            for (int i = 0; i < row.transform.childCount; i++)
            {
                if (row.transform.GetChild(i).gameObject.activeSelf) { anything = true; break; }
            }

            if (row.activeSelf != anything) row.SetActive(anything);
        }

        private void CreateLineageChoices(GameObject parent)
        {
            _lineageChoiceSection = UIFactory.CreateVerticalGroup(parent, "LineageChoiceSection",
                                                                  false, false, true, true,
                                                                  UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_lineageChoiceSection, flexibleWidth: 9999);

            // Contribute as Branch
            _branchRow = UIStyles.CreateFormRow(_lineageChoiceSection, "BranchRow",
                                                   UIStyles.RowHeightLarge, UIStyles.SmallSpacing);
            var branchRow = _branchRow;
            var branchLayout = branchRow.GetComponent<HorizontalLayoutGroup>();
            if (branchLayout != null) branchLayout.childAlignment = TextAnchor.MiddleCenter;

            _contributeAsBranchBtn = CreatePrimaryButton(branchRow, "ContributeBtn", "Contribute as Branch", 180);
            UIStyles.SetBackground(_contributeAsBranchBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _contributeAsBranchBtn.OnClick += OnContributeAsBranchClicked;
            // La branche créée porte le fichier d'ici — les deux côtés en step.
            ScopeMarks.Adorn(_contributeAsBranchBtn,
                             EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: true));
            RegisterUIText(_contributeAsBranchBtn.ButtonText);
            _helpZone?.Describe(_contributeAsBranchBtn.Component.gameObject,
                "Your changes are sent to the owner, who can merge them into the main translation");

            _branchDesc = UIStyles.CreateHint(_lineageChoiceSection, "BranchDesc",
                "Your changes will help improve the main translation", centred: true);
            var branchDesc = _branchDesc;
            RegisterUIText(branchDesc);

            // Merge with Main — the safe way to take in what the Main added.
            //
            // 🔴 **Above Take, and that order is the Manager's.** Its two buttons sit the same way
            // round, with the same reasoning written beside them: "Merge, above, keeps both sides;
            // this one does not pretend to." The safe act is met first; the one that drops work is
            // read second, next to the sentence saying what it drops.
            //
            // ⚠ **Same handler as the Branch's button**, never a second copy of the act: the two
            // are never on screen at once (a Branch has its own row) and one guard helper drives
            // both, so they cannot drift the way the two fork buttons did.
            _mergeRow = UIStyles.CreateFormRow(_lineageChoiceSection, "MergeRow",
                                               UIStyles.RowHeightLarge, UIStyles.SmallSpacing);
            var mergeLayout = _mergeRow.GetComponent<HorizontalLayoutGroup>();
            if (mergeLayout != null) mergeLayout.childAlignment = TextAnchor.MiddleCenter;

            _mergeWithMainBtn = CreateSecondaryButton(_mergeRow, "MergeWithMainBtn", "Merge with Main", 150);
            UIStyles.SetBackground(_mergeWithMainBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _mergeWithMainBtn.OnClick += OnUpdateFromMainClicked;
            // Brings the Main INTO this machine's file and publishes nothing.
            ScopeMarks.Adorn(_mergeWithMainBtn,
                             EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false));
            RegisterUIText(_mergeWithMainBtn.ButtonText);
            _helpZone?.Describe(_mergeWithMainBtn.Component.gameObject,
                "Bring in what the Main added or corrected. Your own lines are kept, and you review everything before it applies.");

            _mergeDesc = UIStyles.CreateHint(_lineageChoiceSection, "MergeDesc",
                "Take in what the Main added — your own lines are kept", centred: true);
            RegisterUIText(_mergeDesc);

            // Take Main's version
            _downloadRow = UIStyles.CreateFormRow(_lineageChoiceSection, "DownloadRow",
                                                     UIStyles.RowHeightLarge, UIStyles.SmallSpacing);
            var downloadRow = _downloadRow;
            var downloadLayout = downloadRow.GetComponent<HorizontalLayoutGroup>();
            if (downloadLayout != null) downloadLayout.childAlignment = TextAnchor.MiddleCenter;

            _downloadLatestBtn = CreateSecondaryButton(downloadRow, "DownloadLatestBtn", "Take Main's version", 150);
            UIStyles.SetBackground(_downloadLatestBtn.Component.gameObject, UIStyles.ButtonPrimary);
            _downloadLatestBtn.OnClick += OnDownloadLatestClicked;
            // ⚠ Le côté DÉPEND du rôle, il est donc corrigé à chaque rafraîchissement par
            // SetDownloadLatestState. Construit au plus prudent.
            ScopeMarks.Adorn(_downloadLatestBtn,
                             EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false));
            RegisterExcluded(_downloadLatestBtn.ButtonText);
            _helpZone?.Describe(_downloadLatestBtn.Component.gameObject,
                "Replace your local file with the owner's latest version from the website");

            _downloadDesc = UIStyles.CreateHint(_lineageChoiceSection, "DownloadDesc",
                "Get the owner's latest version (replaces your local)", centred: true);
            var downloadDesc = _downloadDesc;
            RegisterUIText(downloadDesc);

            // Create Independent (Fork)
            var forkRow = UIStyles.CreateFormRow(_lineageChoiceSection, "ForkRow",
                                                 UIStyles.RowHeightLarge, UIStyles.SmallSpacing);
            var forkLayout = forkRow.GetComponent<HorizontalLayoutGroup>();
            if (forkLayout != null) forkLayout.childAlignment = TextAnchor.MiddleCenter;

            _createIndependentBtn = CreateSecondaryButton(forkRow, "CreateIndependentBtn", "Create Independent", 170);
            // ⚠ **Not red.** Red is what this product uses for something wrong or refused, and
            // making a copy of a translation is neither — it is the third of three legitimate
            // answers. It also sat as the loudest thing on the card while being the least common
            // choice, with white text on a bright fill nobody could read comfortably.
            UIStyles.SetBackground(_createIndependentBtn.Component.gameObject, UIStyles.ButtonSecondary);
            _createIndependentBtn.OnClick += OnCreateIndependentClicked;
            // Une lignée neuve, faite du fichier d'ici — les deux côtés en step.
            ScopeMarks.Adorn(_createIndependentBtn,
                             EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: true));
            RegisterUIText(_createIndependentBtn.ButtonText);
            _helpZone?.Describe(_createIndependentBtn.Component.gameObject,
                "Start your own translation from the file in this game — asks for confirmation first");

            // ⚠ **Says what it starts FROM.** "Start your own independent translation" left the
            // reader to guess whether it began from nothing or from the lines they have: the
            // difference between losing an afternoon's work and keeping it.
            var forkDesc = UIStyles.CreateHint(_lineageChoiceSection, "ForkDesc",
                "A copy of this translation as it is now, yours. It keeps the credit to its author, and stops following their updates",
                centred: true);
            RegisterUIText(forkDesc);
        }

        /// <summary>
        /// Collapsed glossary explaining the sharing model vocabulary
        /// (Main / Branch / Fork and the H/V/A quality tags) for first-time users.
        /// </summary>
        private void CreateGlossarySection(GameObject parent)
        {
            var (container, header, iconLabel, titleLabel, content) =
                UIStyles.CreateCollapsibleSection(parent, "Glossary", "What do Main, Branch and Fork mean?", initiallyExpanded: false);
            RegisterUIText(titleLabel);
            RegisterExcluded(iconLabel);
            _helpZone?.Describe(header,
                "Expand a short glossary of the sharing terms Main, Branch and Fork and the line quality tags.");

            var headerBtn = header.GetComponent<Button>();
            bool expanded = false;
            UIHelpers.AddButtonListener(headerBtn, () =>
            {
                expanded = !expanded;
                UIStyles.SetCollapsibleState(iconLabel, content, expanded);
                RecalculateSize();
            });

            var glossaryText = UIFactory.CreateLabel(content, "GlossaryText",
                "• Main — the reference translation, owned by its creator and public on the website.\n" +
                "• Branch — your improvements to someone else's Main; they are sent to the owner for review.\n" +
                "• Fork — your own independent translation: you become the owner and it is no longer linked to the original.\n\n" +
                "Line quality tags: H = written by a human, V = AI line validated by a human, A = raw AI.",
                TextAnchor.UpperLeft);
            glossaryText.fontSize = UIStyles.FontSizeSmall;
            glossaryText.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(glossaryText.gameObject, flexibleWidth: 9999, minHeight: UIStyles.MultiLineLarge);
            RegisterUIText(glossaryText);
        }

        /// <summary>
        /// Creates the guidance section for contextual messages (GAP 9).
        /// </summary>
        private void CreateGuidanceSection(GameObject parent)
        {
            _guidanceSection = UIFactory.CreateVerticalGroup(parent, "GuidanceSection", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_guidanceSection, flexibleWidth: 9999);

            var guidanceBox = UIStyles.CreateAdaptiveCard(_guidanceSection, "GuidanceBox", PanelWidth - 60);
            UIStyles.SetBackground(guidanceBox, UIStyles.CardElevated);

            _guidanceLabel = UIFactory.CreateLabel(guidanceBox, "GuidanceLabel", "", TextAnchor.MiddleCenter);
            _guidanceLabel.fontSize = UIStyles.FontSizeNormal;
            _guidanceLabel.color = UIStyles.StatusInfo;
            UIFactory.SetLayoutElement(_guidanceLabel.gameObject, flexibleWidth: 9999, minHeight: UIStyles.RowHeightLarge);
            RegisterExcluded(_guidanceLabel);
        }

        private void CreateCommunitySection(GameObject parent)
        {
            // The heading names the frame, so it sits outside it — same as "Current Translation"
            // and "Actions". See CreateStatusSection for why.
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "CommunitySectionLabel", "Community Translations");
            RegisterUIText(sectionTitle);

            // Community section - now a full tab, no longer collapsible
            _communitySection = UIFactory.CreateVerticalGroup(parent, "CommunitySection", false, false, true, true, 5);
            UIFactory.SetLayoutElement(_communitySection, flexibleWidth: 9999, flexibleHeight: 9999);

            // Game info and search row
            var searchRow = UIStyles.CreateFormRow(_communitySection, "SearchRow", UIStyles.RowHeightLarge);

            _communityGameLabel = UIFactory.CreateLabel(searchRow, "GameLabel", "Game: Unknown", TextAnchor.MiddleLeft);
            _communityGameLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_communityGameLabel.gameObject, flexibleWidth: 9999);
            RegisterExcluded(_communityGameLabel);

            _searchBtn = CreateSecondaryButton(searchRow, "SearchBtn", "Search", 80);
            _searchBtn.OnClick += OnSearchCommunityClicked;
            RegisterUIText(_searchBtn.ButtonText);
            _helpZone?.Describe(_searchBtn.Component.gameObject,
                "Search the translations other players shared for this game");

            // Translation list - ensure initialized (larger height for dedicated tab)
            if (_translationList == null)
            {
                TranslatorCore.LogWarning("[MainPanel] _translationList was null - reinitializing");
                _translationList = new TranslationList();
            }
            _translationList.CreateUI(_communitySection, 200, onSelectionChanged: (t) =>
            {
                if (_downloadBtn != null)
                {
                    _downloadBtn.Component.interactable = t != null;
                    SetCommunityDownloadState(t != null);
                }
            }, help: _helpZone);

            UIStyles.CreateSpacer(_communitySection, 5);

            // The tab's OWN action bar, under its own list. It belongs here and not in the
            // panel footer: that row carries what applies to the whole mod on every tab, and a
            // fourth button pushed Close off its edge. Staying visible is the list's business —
            // the list above takes the spare height and this row keeps its own, which is how
            // every other list-and-action tab in the mod is built.
            var downloadRow = UIStyles.CreateFormRow(_communitySection, "DownloadRow", UIStyles.RowHeightLarge, 0);
            var layoutGroup = downloadRow.GetComponent<HorizontalLayoutGroup>();
            if (layoutGroup != null) layoutGroup.childAlignment = TextAnchor.MiddleCenter;

            _downloadBtn = CreatePrimaryButton(downloadRow, "DownloadBtn", "Download Selected", 160);
            UIStyles.SetBackground(_downloadBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _downloadBtn.OnClick += OnDownloadCommunityClicked;
            // ⚠ Même chose : prendre une traduction de la communauté, c'est presque toujours prendre
            // celle de quelqu'un d'autre — donc Local. RetargetDownloadButtons corrige le seul cas
            // où c'est la nôtre.
            ScopeMarks.Adorn(_downloadBtn,
                             EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false));
            _downloadBtn.Component.interactable = false;
            SetCommunityDownloadState(false);
            RegisterUIText(_downloadBtn.ButtonText);
            _helpZone?.Describe(_downloadBtn.Component.gameObject,
                "Use the selected translation in your game — the mod asks before replacing anything you changed");
        }

        public override void SetActive(bool active)
        {
            // Only refresh when transitioning from inactive to active
            // (PanelDragger calls SetActive(true) every frame when mouse is in drag/resize area)
            bool wasActive = Enabled;
            base.SetActive(active);
            if (active && !wasActive)
            {
                RefreshUI();

                // 🔴 **Opening this panel IS the moment to ask about the lineage.** What other
                // people did — a contribution arriving, a Main deleted or gone silent — follows the
                // rhythm the player chose, which can be six hours; and a stream deliberately does
                // not carry it. So the one screen where those facts are read asks for them itself.
                //
                // ⚠ It matters most where a wrong answer costs the most: a contributor whose Main
                // has been deleted must be told here, not when they finally try to publish. Cheap
                // when nothing changed — the site answers from a cache keyed on the files' hashes.
                TranslatorUIManager.RefreshLineageNow();

                // Reserve the height of the TALLEST tab, like the other tabbed panels do.
                // Without it the panel was sized for whichever tab happened to be open, so
                // switching to Community resized the whole window under the user.
                KeepPanelHeightAcrossTabs(_tabBar);

                // Auto-search community translations if conditions are met
                TryAutoSearchCommunity();
            }
        }

        /// <summary>
        /// Automatically search for community translations if:
        /// - Online mode is enabled
        /// - Game is detected
        /// - List is empty (no previous search results)
        /// - Not already searching
        /// </summary>
        private void TryAutoSearchCommunity()
        {
            if (!TranslatorCore.Config.online_mode) return;
            if (_translationList == null) return;
            if (_translationList.IsSearching) return;
            if (_translationList.Count > 0) return; // Already has results

            var game = TranslatorCore.CurrentGame;
            if (game == null || string.IsNullOrEmpty(game.name)) return;

            // Trigger search automatically
            string targetLang = TranslatorCore.Config.GetTargetLanguage();
            SearchCommunityAsync(game.steam_id, game.name, targetLang);
        }

        /// <summary>
        /// Perform community search (shared between auto-search and button click).
        /// </summary>
        private async void SearchCommunityAsync(string steamId, string gameName, string targetLang)
        {
            await _translationList.SearchAsync(steamId, gameName, targetLang);

            // After await, we may be on a background thread (IL2CPP issue)
            TranslatorUIManager.RunOnMainThread(() =>
            {
                // Enable download button if results found
                if (_downloadBtn != null)
                {
                    _downloadBtn.Component.interactable = _translationList?.SelectedTranslation != null;
                    SetCommunityDownloadState(_translationList?.SelectedTranslation != null);
                }
            });
        }

        /// <summary>
        /// Detects the current layout state based on login, local translations, and server state.
        /// </summary>
        private LayoutState DetectCurrentState()
        {
            int localCount = TranslatorCore.TranslationCache.Count;
            var serverState = TranslatorCore.ServerState;
            bool existsOnServer = serverState != null && serverState.Exists && serverState.SiteId.HasValue;

            // 🔴 **Having an account is NOT a layout state**, and testing it first here is what made
            // the panel useless to the person who most needed it. Somebody with no account can
            // download a community translation and go on adding lines: they diverge exactly like a
            // branch would, while being neither branch nor fork because they never published. The
            // mod already DETECTS that the main moved — StartSyncWatch has a public branch for
            // precisely this case, see analyse/false-branch-role-after-download.md — and this line
            // then hid the answer behind a "create an account" pitch.
            //
            // ⚠ The account decides which ACTIONS are offered, never what somebody is allowed to
            // KNOW. Merging and taking the main's version again write nothing but the local file.

            // No local translation
            if (localCount == 0)
            {
                return LayoutState.NoLocal;
            }

            // Has local translation - check server state
            if (existsOnServer)
            {
                if (serverState.IsOwner)
                {
                    // User owns this translation
                    return serverState.Role == TranslationRole.Main
                        ? LayoutState.OwnerMain
                        : LayoutState.OwnerBranch;
                }
                else
                {
                    // User doesn't own - check if same UUID (same lineage)
                    // ServerState.Exists means the UUID exists on server
                    // We're working with the same UUID but not the owner
                    return LayoutState.HoldingAnothersLineage;
                }
            }
            else
            {
                // Not on server but has local - check if UUID exists but owned by someone else
                if (serverState != null && serverState.Checked)
                {
                    if (serverState.Exists && !serverState.IsOwner)
                    {
                        // UUID exists on server but we don't own it
                        return LayoutState.HoldingAnothersLineage;
                    }
                }
                // Local only - treat as potential new upload or visitor
                return LayoutState.VisitorDiffUuid;
            }
        }

        public void RefreshUI()
        {
            // Whoever set update checks to "Never" still needs a panel that knows
            // its role, its site id and whether an upload is possible — those come
            // from the same sync state. Fetched on demand here, so the choice
            // silences notifications without blinding the interface.
            TranslatorUIManager.EnsureServerStateKnown();

            // Detect and cache current state
            _currentLayoutState = DetectCurrentState();

            // Refresh all sections
            RefreshModUpdateBanner();
            RefreshAccountSection();
            RefreshTranslationInfo();
            RefreshCommunitySection();
            RefreshActionsSection();
            RefreshLayoutVisibility();
        }

        private void RefreshModUpdateBanner()
        {
            if (_modUpdateBanner == null) return;

            bool showBanner = TranslatorUIManager.HasModUpdate && !TranslatorUIManager.ModUpdateDismissed;
            _modUpdateBanner.SetActive(showBanner);

            if (showBanner)
            {
                var info = TranslatorUIManager.ModUpdateInfo;
                // Version number appended: it is data, and it changes with every release
                _modUpdateLabel.text = Tr("Mod update available:")
                    + $" v{info?.LatestVersion ?? "?"}";

                // Show appropriate button text
                bool hasDirectDownload = !string.IsNullOrEmpty(info?.DownloadUrl);
                SetDynamicText(_modUpdateBtn.ButtonText, hasDirectDownload ? "Download" : "View Release");

                // The verb follows what pressing it will do: open what is already on this machine,
                // or go and fetch it.
                SetDynamicText(_modManagerBtn.ButtonText,
                    ManagerLink.IsOnThisMachine ? "Open Manager" : "Get Manager");
            }
        }

        /// <summary>
        /// Updates section visibility based on current layout state.
        /// </summary>
        private void RefreshLayoutVisibility()
        {
            // ⚠ Read from the account directly, no longer from the layout state. The two were the
            // same test and are two different questions: whether to invite somebody to sign in, and
            // what their translation is doing. Tying them meant an invitation REPLACED the answer.
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);

            if (_loginCTASection != null)
            {
                _loginCTASection.SetActive(!isLoggedIn);

                // Disable CTA button when offline
                if (_loginCTABtn != null)
                {
                    _loginCTABtn.Component.interactable = TranslatorCore.Config.online_mode;
                }
            }

            // The card describes a translation, so it appears whenever there is one — with or
            // without a name attached to it.
            bool showStatusCard = _currentLayoutState != LayoutState.NoLocal;

            // Status section with StatusCard - show when logged in and has local content
            if (_statusSection != null)
            {
                _statusSection.SetActive(showStatusCard);
                if (showStatusCard)
                {
                    RefreshStatusCard();
                }
            }

            // Legacy TranslationInfo section - hide when StatusCard is shown
            if (_translationInfoSection != null)
            {
                _translationInfoSection.SetActive(!showStatusCard);
            }

            // The three choices offered to somebody holding a lineage that is not theirs.
            if (_lineageChoiceSection != null)
            {
                bool choosing = _currentLayoutState == LayoutState.HoldingAnothersLineage;
                _lineageChoiceSection.SetActive(choosing);

                // 🔴 **The upload button steps aside for them.** In this state it reads
                // "Contribute" and does the same thing as the first of the three below it — two
                // doors to one act, three inches apart, and the hint under it ("Login required")
                // answered for a button nobody should have been looking at.
                if (_uploadBtn != null) _uploadBtn.Component.gameObject.SetActive(!choosing);
                if (_uploadHintLabel != null) _uploadHintLabel.gameObject.SetActive(!choosing);

                // ⚠ **And the rows they were standing in.** Hiding every button in a row leaves the
                // row, which keeps its RowHeightLarge and shows as an empty band inside the card.
                HideIfEmpty(_syncActionsRow);
                HideIfEmpty(_roleActionsRow);

                // ⚠ Two of the three need a name, one does not. Taking the main's version again
                // writes only the local file, so it stays live without an account — that is the
                // whole point of showing this section to somebody signed out. Contributing and
                // forking put something on the site under a name, so they wait for one.
                // ⚠ Tint alongside interactable, every time. Unity greys a button's own image and
                // leaves its children alone, so the marks and the label keep whatever colour they
                // were built with — which left an accent-purple mark on a dead button.
                // 🔴 **Each of the three answers its own question, and two of them were answering
                // none.** Signed in was the whole test, so "Contribute as Branch" invited somebody
                // to send a file identical to the Main — a contribution holding nothing — and
                // "Take Main's version" offered to fetch a version that had not moved. Same guards as
                // the Actions row, which had them right all along: this section is the same acts,
                // laid out as a choice.
                bool canReachServer = isLoggedIn && TranslatorCore.Config.online_mode;
                bool haveSomethingToOffer = TranslatorCore.LocalChangesCount > 0
                                            || TranslatorCore.MetadataDirty;

                // 🔴 **Not shown at all when the Main refuses contributions.** That is its owner's
                // declaration, and the server enforces it — an upload into a lineage that does not
                // take branches is refused outright. Offering the act anyway is a door onto a no
                // somebody else already said.
                //
                // ⚠ Only on a stated refusal. AcceptsBranches is null on a server too old to send
                // it, and unknown is not "no": hiding the button there would take the act away over
                // a question nobody answered.
                bool refusesBranches = TranslatorCore.ServerState?.AcceptsBranches == false;
                if (_branchRow != null) _branchRow.SetActive(!refusesBranches);
                if (_branchDesc != null) _branchDesc.gameObject.SetActive(!refusesBranches);

                if (_contributeAsBranchBtn != null && !refusesBranches)
                {
                    bool canContribute = canReachServer && haveSomethingToOffer;
                    _contributeAsBranchBtn.Component.interactable = canContribute;
                    ScopeMarks.Tint(_contributeAsBranchBtn, canContribute);

                    // ⚠ **The sentence says why it is closed, or what it does.** Hiding the Actions
                    // row took its "Login required" with it, so a greyed button was left with a
                    // sentence describing an act nobody could take and no reason anywhere.
                    if (_branchDesc != null)
                    {
                        SetDynamicText(_branchDesc,
                            !TranslatorCore.Config.online_mode ? "Offline mode — nothing is sent"
                            : !isLoggedIn ? "Login required"
                            : !haveSomethingToOffer
                                ? "Nothing to send yet: this file matches the main translation"
                                : "Your changes will help improve the main translation");
                    }
                }

                // 🔴 **The safe way to take the Main in, and it needs no account either.** It reads
                // a public file and writes local ones — nothing is sent. Offered on the same
                // condition as the row below it, because it answers the same question: something
                // upstream is worth taking. What separates them is what happens to YOUR lines.
                //
                // ⚠ Kept in step with the Branch's own Merge with Main by sharing its handler AND
                // its busy state, never by repeating the rule here.
                bool upstreamWorthTaking = TranslatorUIManager.HasPendingUpdate
                    && TranslatorUIManager.PendingUpdateDirection != UpdateDirection.Upload;

                if (_mergeWithMainBtn != null && !_updateFromMainInFlight)
                {
                    _mergeWithMainBtn.Component.interactable = upstreamWorthTaking;
                    ScopeMarks.Tint(_mergeWithMainBtn, upstreamWorthTaking);

                    if (_mergeDesc != null)
                    {
                        SetDynamicText(_mergeDesc, upstreamWorthTaking
                            ? "Take in what the Main added — your own lines are kept"
                            : "Nothing new in the Main to take in");
                    }
                }

                // ⚠ Only when the published one actually moved. It writes the local file, so it
                // needs no account — but fetching a version identical to the one already here is
                // an act with no effect, and a button that promises one is worse than none.
                if (_downloadLatestBtn != null)
                {
                    bool serverMoved = upstreamWorthTaking;

                    _downloadLatestBtn.Component.interactable = serverMoved;
                    SetDownloadLatestState(serverMoved);

                    if (_downloadDesc != null)
                    {
                        SetDynamicText(_downloadDesc, serverMoved
                            ? "Replaces this file with the Main's — your own lines are dropped"
                            : "You already have the Main's version");
                    }
                }

                // 🔴 **No account, no network, no divergence — forking asks for none of them.**
                // CreateFork is local from end to end: a new uuid, the server state cleared, the
                // origin recorded. Nothing is sent. Publishing is a separate act the owner of a
                // fork takes when they want to, which is exactly what distinguishes it from a
                // branch — a branch does not exist until it is sent, and its Main learns of it then.
                //
                // Gating it on being signed in made this file's identity depend on an account,
                // which nothing about it does.
                if (_createIndependentBtn != null)
                {
                    _createIndependentBtn.Component.interactable = true;
                    ScopeMarks.Tint(_createIndependentBtn, true);
                }
            }

            // Guidance section (GAP 9) - show contextual messages
            RefreshGuidanceSection();

            // Recalculate panel size after visibility changes
            RecalculateSize();
        }

        /// <summary>
        /// Refreshes the guidance section with contextual messages (GAP 9).
        /// </summary>
        private void RefreshGuidanceSection()
        {
            if (_guidanceSection == null || _guidanceLabel == null) return;

            string message = null;
            var serverState = TranslatorCore.ServerState;
            int localCount = TranslatorCore.TranslationCache.Count;

            switch (_currentLayoutState)
            {
                case LayoutState.NoLocal:
                    // No local translation - guide user
                    if (TranslatorCore.Config.IsTranslationEnabled)
                    {
                        message = "Auto-translation active. Captured text will be translated, or download a community translation.";
                    }
                    else
                    {
                        message = "Enable AI translation, or download a community translation to get started.";
                    }
                    break;

                case LayoutState.HoldingAnothersLineage:
                    // Same UUID but not owner - show info about parent
                    if (serverState != null)
                    {
                        int localChanges = TranslatorCore.LocalChangesCount;
                        if (localChanges > 0)
                        {
                            // Count inline (placeholdered), uploader appended as data
                            message = Tr($"You have {localChanges} changes compared to the translation of")
                                      + " " + People.MentionOf(serverState.Uploader,
                                                                  TranslatorCore.Config.api_user);
                        }
                    }
                    break;

                case LayoutState.VisitorDiffUuid:
                    // Different UUID - local only
                    if (serverState != null && serverState.Checked && !serverState.Exists)
                    {
                        message = "Your translation is local only. Upload it to share with the community!";
                    }
                    break;
            }

            // ⚠ The invitation to sign up is appended, never substituted. It used to BE the message
            // for anybody without an account, which is how somebody holding a diverged community
            // translation was told to create an account instead of being told they had diverged.
            if (message is null && string.IsNullOrEmpty(TranslatorCore.Config.api_token)
                && localCount > 0)
            {
                message = "Create an account to publish your translation or contribute to the community.";
            }

            // Show or hide guidance section based on message
            bool hasMessage = !string.IsNullOrEmpty(message);
            _guidanceSection.SetActive(hasMessage);
            if (hasMessage)
            {
                // The HoldingAnothersLineage branch already translated (it appends a username);
                // the others are plain sentences translated here.
                _guidanceLabel.text = _currentLayoutState == LayoutState.HoldingAnothersLineage
                    ? message
                    : TranslatorCore.TranslateOwnUIDynamic(message, _guidanceLabel);
            }
        }

        /// <summary>
        /// Updates the StatusCard with current translation state.
        /// </summary>
        /// <summary>
        /// How much history this translation has, in one line.
        ///
        /// ⚠ Both figures, always — the two families do not live equally long, and a single total
        /// would hide that half of it ages out on its own. Said even at zero: an empty line is how
        /// somebody learns the feature exists before they need it, which is the only moment worth
        /// learning it.
        /// </summary>
        private void RefreshBackupsLine()
        {
            if (_backupsLabel == null) return;

            var saved = 0;
            var automatic = 0;

            foreach (var entry in TranslationBackups.List())
            {
                if (entry.IsSaved) saved++;
                else automatic++;
            }

            _backupsLabel.text = saved == 0 && automatic == 0
                ? "Backups: none yet"
                : $"Backups: {saved} of your own, {automatic} automatic";
        }

        private void RefreshStatusCard()
        {
            if (_statusCard == null) return;

            RefreshBackupsLine();

            var serverState = TranslatorCore.ServerState;
            int entryCount = TranslatorCore.TranslationCache.Count;
            string targetLang = TranslatorCore.Config.GetTargetLanguage();
            int localChanges = TranslatorCore.LocalChangesCount;

            // Where this translation stands, on the four questions the socle keeps apart.
            Standing standing;
            bool needsMerge = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Merge;
            bool hasServerUpdate = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Download;

            // ⚠ **The direction is kept now.** "OutOfSync" said only that the two differed; which
            // side had moved decides whether the answer is to take an update or to publish, and
            // the reader had to work it out from the buttons.
            SyncDirection? sync;
            if (serverState == null || !serverState.Exists) sync = null;
            else if (needsMerge) sync = SyncDirection.Merge;
            else if (hasServerUpdate && localChanges > 0) sync = SyncDirection.Merge;
            else if (hasServerUpdate) sync = SyncDirection.Download;

            // Metadata counts: settings edited but not pushed are still something to send.
            // Leaving it out showed "up to date" next to a button offering an update.
            else if (localChanges > 0 || TranslatorCore.MetadataDirty) sync = SyncDirection.Upload;
            else sync = SyncDirection.InSync;

            standing = new Standing
            {
                // ⚠ `yours` decides between two sentences that describe opposite acts: publishing
                // your own updates it, publishing into somebody else's lineage contributes to it.
                // Without it the card said "Published" over a community translation the player had
                // merely downloaded, and offered to update a file that is not theirs to update.
                Publication = Publications.Of(hereOnDisk: entryCount > 0,
                                              onTheSite: serverState != null && serverState.Exists,
                                              yours: serverState != null && serverState.Exists
                                                  ? serverState.IsOwner
                                                  : (bool?)null),
                Sync = sync,

                // ⚠ From the point of view of the game itself, which holds its own credential: the
                // question the manager asks — is this somebody else's game — cannot arise here.
                Account = string.IsNullOrEmpty(TranslatorCore.Config.api_token)
                    ? AccountStanding.Anonymous
                    : AccountStanding.Ours,

                Role = _currentLayoutState == LayoutState.OwnerMain ? LineageRole.Main
                     : _currentLayoutState == LayoutState.OwnerBranch ? LineageRole.Branch
                     : LineageRole.None,

                // ⚠ What is actually WAITING, not how many people contribute. Falls back to the raw
                // count only when the site could not answer: unknown is not zero, and showing
                // nothing there would tell a Main their contributions are settled when nobody knows.
                BranchesWaiting = _currentLayoutState == LayoutState.OwnerMain
                    ? (serverState?.BranchesWithWork ?? serverState?.BranchesCount)
                    : null,

                LinesAvailable = _currentLayoutState == LayoutState.OwnerMain
                    ? serverState?.LinesAvailable
                    : null,

                // ⚠ Whoever leads the lineage, and ONLY when it is not this account — the same two
                // fields the card below already reads to say "Based on the translation of @x".
                // Null when it is ours: there is then nobody else to name.
                MainOwner = serverState != null && !serverState.IsOwner
                    ? serverState.MainUsername ?? serverState.Uploader
                    : null,
            };

            // Identity leads the card: which languages, whatever the mode
            _statusCard.SetIdentity(TranslatorCore.Config.GetSourceLanguage(), targetLang);

            // 🔴 **The card describes, it does not act.** Every action on this translation lives in
            // "Actions" below, and there only — that row is where the conditions are (signed in,
            // online, anything to send) and where a refusal is explained. Three buttons stood here
            // between 2026-07-26 and 2026-08-19, each duplicating one of them a few rows higher
            // WITHOUT its guards: Upload opened the upload screen while signed out, on a call that
            // could only fail. What each mode has to SAY is set by the ConfigureAs* below; what it
            // lets you DO is not this component's business.
            switch (_currentLayoutState)
            {
                case LayoutState.OwnerMain:
                    // ⚠ Both axes travel together. How many rows need a decision is not how many are
                    // worth taking, and what they are made of is what decides whether opening the
                    // review is worth it — none of which a single total can say.
                    _statusCard.ConfigureAsMainOwner(standing, entryCount, targetLang,
                                                     standing.BranchesWaiting ?? 0,
                                                     serverState?.LinesAvailable,
                                                     serverState?.LinesToReview,
                                                     serverState?.LinesNew ?? default(TagTally),
                                                     serverState?.LinesDiffering ?? default(TagTally));
                    break;

                case LayoutState.OwnerBranch:
                    _statusCard.ConfigureAsBranchOwner(
                        standing,
                        entryCount,
                        targetLang,
                        serverState?.MainUsername ?? serverState?.Uploader,
                        localChanges);
                    break;

                case LayoutState.HoldingAnothersLineage:
                    _statusCard.ConfigureAsHoldingAnothersLineage(
                        standing,
                        entryCount,
                        targetLang,
                        serverState?.Uploader);
                    break;

                case LayoutState.VisitorDiffUuid:
                    _statusCard.ConfigureAsLocalOnly(entryCount, targetLang);
                    break;

                default:
                    // ⚠ Only NoLocal reaches here, and RefreshLayoutVisibility has already hidden
                    // the card for it: a card describing a file says nothing when there is no file.
                    // It used to be configured anyway, with counts of zero and a language of "None".
                    break;
            }

            // What the community made of this translation, and the player's own say. Whatever
            // the mode: an author is entitled to see their count, a player to give one back.
            // Hidden by the card itself when the server reported no vote at all.
            _statusCard.SetVote(
                serverState?.Vote,
                _currentLayoutState == LayoutState.OwnerMain
                    ? LineageRole.Main
                    : LineageRole.None);

            // Show/hide external resources link
            if (_resourcesLinkSection != null)
            {
                string resourcesUrl = serverState?.ResourcesUrl;
                bool hasResources = !string.IsNullOrEmpty(resourcesUrl);
                _resourcesLinkSection.SetActive(hasResources);
                if (hasResources)
                {
                    string uploader = serverState?.Uploader;
                    string by = !string.IsNullOrEmpty(uploader)
                        ? $"External Resources uploaded by @{uploader}"
                        : "External Resources";

                    // Say whether the link still has something for this user. The fonts and images a
                    // translation names never travel with it, so a missing one means the link is
                    // worth following — and it explains boxes or untranslated art in the game.
                    var missing = AssetAvailability.GetMissingResources();
                    if (missing.Any)
                    {
                        _resourcesByLabel.text = $"{by} — {DescribeMissing(missing)} missing";
                        _resourcesByLabel.color = UIStyles.StatusWarning;
                        UIStyles.SetBackground(_resourcesLinkSection, UIStyles.CardElevated);
                    }
                    else
                    {
                        _resourcesByLabel.text = by;
                        _resourcesByLabel.color = UIStyles.TextPrimary;
                        UIStyles.SetBackground(_resourcesLinkSection, UIStyles.CardBackground);
                    }

                    _resourcesUrlLabel.text = resourcesUrl;
                }
            }
        }

        /// <summary>"2 fonts, 3 images" — only the kinds actually missing, singular where it fits.</summary>
        private static string DescribeMissing(AssetAvailability.MissingResources missing)
        {
            string fonts = missing.Fonts > 0 ? $"{missing.Fonts} font{(missing.Fonts > 1 ? "s" : "")}" : null;
            string images = missing.Images > 0 ? $"{missing.Images} image{(missing.Images > 1 ? "s" : "")}" : null;

            if (fonts != null && images != null) return $"{fonts}, {images}";
            return fonts ?? images ?? "";
        }

        private void RefreshAccountSection()
        {
            if (_accountLabel == null) return;

            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            string currentUser = TranslatorCore.Config.api_user;

            if (isLoggedIn)
            {
                // Username concatenated, never sent for translation (one cache entry per user otherwise)
                _accountLabel.text = Tr("Connected as")
                    + $" @{currentUser ?? "Unknown"}";
                _accountLabel.fontStyle = FontStyle.Normal;
                SetDynamicText(_loginLogoutBtn.ButtonText, "Logout");
            }
            else
            {
                SetDynamicText(_accountLabel, "Not connected");
                _accountLabel.fontStyle = FontStyle.Italic;
                SetDynamicText(_loginLogoutBtn.ButtonText, "Login");

                // Disable login if offline mode
                _loginLogoutBtn.Component.interactable = TranslatorCore.Config.online_mode;
            }
        }

        private void RefreshTranslationInfo()
        {
            if (_entriesLabel == null)
            {
                TranslatorCore.LogWarning("[MainPanel] RefreshTranslationInfo: _entriesLabel is null!");
                return;
            }

            int entryCount = TranslatorCore.TranslationCache.Count;
            string targetLang = TranslatorCore.Config.GetTargetLanguage();
            var serverState = TranslatorCore.ServerState;
            bool existsOnServer = serverState != null && serverState.Exists && serverState.SiteId.HasValue;

            TranslatorCore.LogDebug($"[MainPanel] RefreshTranslationInfo: entries={entryCount}, target={targetLang}, serverState={(serverState == null ? "null" : $"checked={serverState.Checked}")}");

            // Counts stay inside the string: the pipeline turns numbers into placeholders, so every
            // count shares one cache entry. Languages, usernames and ids are concatenated instead.
            SetDynamicText(_entriesLabel, $"Entries: {entryCount}");
            _targetLabel.text = Tr("Target:") + $" {targetLang}";

            if (existsOnServer)
            {
                _sourceLabel.text = Tr("Source:")
                    + $" {People.MentionOf(serverState.Uploader, TranslatorCore.Config.api_user)}"
                    + $" (#{serverState.SiteId})";

                // Role indicator.
                //
                // ⚠ Read through IsOwner, exactly as DetectCurrentState() does a few lines above —
                // a role only means something about a translation we actually hold on the server.
                // Reading Role on its own is what let this line announce "[BRANCH] Your changes are
                // reviewed by @X" to a player who had merely downloaded @X's file, while the status
                // card, which does consult IsOwner, correctly offered them the Branch/Fork choice.
                // Two blocks of the same panel contradicting each other on the same state.
                // See analyse/false-branch-role-after-download.md.
                switch (serverState.IsOwner ? serverState.Role : TranslationRole.None)
                {
                    case TranslationRole.Main:
                        if (serverState.BranchesCount > 0)
                        {
                            SetDynamicText(_roleLabel, $"[MAIN] {serverState.BranchesCount} contribution(s) from other players");
                        }
                        else
                        {
                            SetDynamicText(_roleLabel, "[MAIN] You own this translation");
                        }
                        _roleLabel.color = UIStyles.StatusSuccess;
                        break;
                    case TranslationRole.Branch:
                        _roleLabel.text = "[BRANCH] "
                            + Tr("Your changes are reviewed by")
                            + " " + People.MentionOf(serverState.MainUsername ?? serverState.Uploader,
                                                        TranslatorCore.Config.api_user);
                        _roleLabel.color = UIStyles.StatusWarning;
                        break;
                    default:
                        _roleLabel.text = "";
                        break;
                }

                // Sync status
                int localChanges = TranslatorCore.LocalChangesCount;
                bool hasServerUpdate = TranslatorUIManager.HasPendingUpdate &&
                    TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Download;
                bool needsMerge = TranslatorUIManager.HasPendingUpdate &&
                    TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Merge;

                if (needsMerge)
                {
                    SetDynamicText(_syncStatusLabel, $"SYNC NEEDED - Both local ({localChanges}) and server changed");
                    _syncStatusLabel.color = UIStyles.StatusWarning;
                }
                else if (localChanges > 0)
                {
                    SetDynamicText(_syncStatusLabel, $"OUT OF SYNC - {localChanges} local changes to upload");
                    _syncStatusLabel.color = UIStyles.StatusWarning;
                }
                else if (hasServerUpdate)
                {
                    int serverLines = TranslatorUIManager.PendingUpdateInfo?.LineCount ?? 0;
                    SetDynamicText(_syncStatusLabel, $"OUT OF SYNC - Server has update ({serverLines} lines)");
                    _syncStatusLabel.color = UIStyles.StatusWarning;
                }
                else
                {
                    SetDynamicText(_syncStatusLabel, "SYNCED with server");
                    _syncStatusLabel.color = UIStyles.StatusSuccess;
                }
            }
            else
            {
                // Not on server - clear role label
                _roleLabel.text = "";

                if (serverState != null && serverState.Checked)
                {
                    SetDynamicText(_sourceLabel, "Source: Local only (not on server)");
                    SetDynamicText(_syncStatusLabel, $"All {entryCount} entries are local");
                    _syncStatusLabel.color = UIStyles.TextMuted;
                }
                else if (!TranslatorCore.Config.online_mode)
                {
                    SetDynamicText(_sourceLabel, "Source: Local (offline mode)");
                    _syncStatusLabel.text = "";
                }
                else if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
                {
                    // Online mode but not logged in - can't check server state
                    SetDynamicText(_sourceLabel, "Source: Local (login to sync)");
                    _syncStatusLabel.text = "";
                }
                else
                {
                    SetDynamicText(_sourceLabel, "Source: Local (checking...)");
                    _syncStatusLabel.text = "";
                }
            }

            // AI status
            if (TranslatorCore.Config.IsTranslationEnabled)
            {
                int queueCount = TranslatorCore.QueueCount;
                // Backend name is a brand, kept out of the translated part
                string backendLabel = TranslatorCore.Config.translation_backend == "llm" ? "AI" :
                    TranslatorCore.Config.translation_backend == "google" ? "Google" : "DeepL";
                _aiStatusLabel.text = $"{backendLabel}: " + (queueCount > 0
                    ? Tr($"{queueCount} in queue")
                    : Tr("Ready"));
            }
            else
            {
                _aiStatusLabel.text = "";
            }
        }

        private void RefreshActionsSection()
        {
            if (_uploadBtn == null) return;

            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            var state = TranslatorCore.ServerState;
            bool existsOnServer = state != null && state.Exists && state.SiteId.HasValue;
            bool hasLocalChanges = TranslatorCore.LocalChangesCount > 0;

            // Settings that travel with the translation — replacement fonts, images, exclusions,
            // variables, the interface font — can change without a single new translated line.
            // Sync used to be judged on line count alone, so those edits had no way out: the
            // upload button stayed disabled on "Up to date" and the work stayed on the machine.
            // That also blocked editing the resources link, which is entered on the upload screen.
            bool hasMetadataChanges = TranslatorCore.MetadataDirty;

            // In sync means nothing left to push: neither lines NOR settings
            bool isInSync = existsOnServer && state.IsOwner && !hasLocalChanges && !hasMetadataChanges &&
                           !string.IsNullOrEmpty(state.Hash) &&
                           state.Hash == TranslatorCore.LastSyncedHash;

            // Determine upload action text
            string uploadAction;
            string uploadHint;

            // Check for merge conflict first (highest priority action)
            bool needsMerge = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Merge;

            // Hints are translated as they are built: counts and ids stay inline (the pipeline
            // placeholders numbers), usernames are appended so they never reach the translator.
            if (needsMerge)
            {
                // Merge needed - show sync button with clear explanation
                uploadAction = "Sync Translation";
                uploadHint = Tr($"Both local ({TranslatorCore.LocalChangesCount} changes) and server were updated. Click to sync.");
            }
            else if (isInSync)
            {
                // In sync - no need to show upload button
                uploadAction = "Up to date";
                uploadHint = Tr("Your translation is synchronized with the server");
            }
            else if (existsOnServer && state.IsOwner)
            {
                uploadAction = "Update Translation";
                // Say WHICH kind of change is pending, otherwise an update offered after a mere
                // font or exclusion edit looks like the mod lost track of what was synced.
                if (hasLocalChanges)
                    uploadHint = Tr($"Update #{state.SiteId} ({TranslatorCore.LocalChangesCount} local changes)");
                else if (hasMetadataChanges)
                    uploadHint = Tr($"Update #{state.SiteId} — settings changed (fonts, images, exclusions)");
                else
                    uploadHint = Tr($"Update your translation #{state.SiteId}");
            }
            else if (existsOnServer && !state.IsOwner)
            {
                uploadAction = "Contribute";
                uploadHint = Tr("Contribute as a branch to")
                    + " " + People.MentionOf(state.Uploader, TranslatorCore.Config.api_user);
            }
            else
            {
                uploadAction = "Upload Translation";
                uploadHint = Tr("Create a new translation");
            }

            SetDynamicText(_uploadBtn.ButtonText, uploadAction);

            // Enable/disable based on conditions
            // Disable if in sync (nothing to upload) or other conditions not met
            // 🔴 **A fork that has not been touched holds somebody else's file, line for line.**
            // Publishing it puts a second identical entry on the site under a new name — the two
            // then compete for the same readers, and the work is one person's. CreateFork counts
            // every entry as a local change (from the new lineage's point of view nothing has ever
            // been published, which is true), so nothing else here could tell the difference.
            //
            // ⚠ This is a CONTENT condition, and the only one: a fork publishes whenever it wants,
            // signed in or not, the same day or a year later. What it may not do is publish a copy.
            //
            // ⚠ **Only while it has never been published.** The marker travels inside the file, so
            // whoever downloads a fork carries it too — and they are in a lineage, where "nothing
            // to contribute" is the question and other rules answer it. Reading it there would put
            // a sentence about publishing a copy in front of somebody offering a correction.
            bool stillTheCopy = !existsOnServer && TranslatorCore.ForkIsStillTheCopy;

            bool canUpload = isLoggedIn && TranslatorCore.Config.online_mode &&
                            TranslatorCore.TranslationCache.Count > 0 && !isInSync && !stillTheCopy;
            _uploadBtn.Component.interactable = canUpload;
            ScopeMarks.Tint(_uploadBtn, canUpload);

            // Update hint
            if (!TranslatorCore.Config.online_mode)
            {
                SetDynamicText(_uploadHintLabel, "Offline mode - upload disabled");
            }
            else if (!isLoggedIn)
            {
                SetDynamicText(_uploadHintLabel, "Login required");
            }
            else if (TranslatorCore.TranslationCache.Count == 0)
            {
                SetDynamicText(_uploadHintLabel, "No translations to upload");
            }
            else if (stillTheCopy)
            {
                // The fact, then the way out. Naming the author would need a lookup the mod does
                // not have after a fork — the lineage is gone — and the sentence works without it.
                SetDynamicText(_uploadHintLabel,
                    "This copy is unchanged. Translate or correct a line to publish it as yours.");
            }
            else
            {
                _uploadHintLabel.text = uploadHint; // already translated above
            }

            // Role-specific buttons visibility
            if (_reviewOnWebsiteBtn != null && _compareWithServerBtn != null && _forkBtn != null)
            {
                bool isMain = existsOnServer && state.Role == TranslationRole.Main;
                bool isBranch = existsOnServer && state.Role == TranslationRole.Branch;
                bool hasBranches = state != null && state.BranchesCount > 0;

                // Review Branches - only for Main role when there are branches to review
                _reviewOnWebsiteBtn.Component.gameObject.SetActive(isMain && hasBranches);
                if (isMain && hasBranches)
                {
                    // 🔴 The count is what is WAITING — not been through, and holding something —
                    // never how many people contribute. A number that includes work already
                    // arbitrated never falls to zero, and a number that never falls to zero stops
                    // being read, which hides the times there IS something to do.
                    //
                    // ⚠ The button stays whenever contributions exist: somebody may want to look
                    // at what they refused, or at who is contributing. It simply carries no number
                    // when nothing is waiting — the convention this project uses everywhere.
                    int? waiting = state.BranchesWithWork;

                    SetDynamicText(_reviewOnWebsiteBtn.ButtonText,
                                   waiting.HasValue
                                       ? (waiting.Value > 0
                                           ? $"Review Branches ({waiting.Value})"
                                           : "Review Branches")
                                       // An older site could not say: the raw count is all there
                                       // is, and it is better than a silence that reads as zero.
                                       : $"Review Branches ({state.BranchesCount})");
                }

                // Compare with Server - only for owners (Main or Branch) who have uploaded
                // Non-owners can't compare because they don't have a server version to compare against
                bool canCompare = existsOnServer && state.IsOwner && hasLocalChanges;
                _compareWithServerBtn.Component.gameObject.SetActive(canCompare);
                if (canCompare)
                {
                    _compareWithServerBtn.Component.interactable = isLoggedIn;
                    // How many lines the comparison is about, on the button that opens it.
                    SetDynamicText(_compareWithServerBtn.ButtonText,
                                   $"Compare ({TranslatorCore.LocalChangesCount})");
                }

                // Edit details — for owners of a published translation, whatever the sync state.
                // That is the point: it exists precisely for when there is nothing else to push.
                bool canEditDetails = existsOnServer && state.IsOwner;
                if (_editDetailsBtn != null)
                {
                    _editDetailsBtn.Component.gameObject.SetActive(canEditDetails);
                    if (canEditDetails)
                        _editDetailsBtn.Component.interactable = isLoggedIn && TranslatorCore.Config.online_mode;
                }

                // Merge with Main — a branch only. Shown even when nothing new is
                // known upstream: the very first merge is what teaches the mod where
                // the Main stood, so it must be reachable before any notice exists.
                if (_updateFromMainBtn != null)
                {
                    bool canUpdateFromMain = isBranch && state.MainSiteId.HasValue;
                    _updateFromMainBtn.Component.gameObject.SetActive(canUpdateFromMain);
                    if (canUpdateFromMain && !_updateFromMainInFlight)
                    {
                        _updateFromMainBtn.Component.interactable = isLoggedIn && TranslatorCore.Config.online_mode;
                    }
                }

                // Fork button - only for Branch role
                _forkBtn.Component.gameObject.SetActive(isBranch);

                // Fork button enabled only when logged in
                if (isBranch)
                {
                    _forkBtn.Component.interactable = isLoggedIn;
                    ScopeMarks.Tint(_forkBtn, isLoggedIn);
                }

                // Explain the visible buttons in plain words
                if (_roleActionsHint != null)
                {
                    string hint = "";
                    if (isBranch && TranslatorUIManager.HasMainUpdate())
                        hint = Tr("The original translation has changed — Merge with Main brings it in");
                    else if (isBranch)
                        hint = Tr("Fork = continue on your own, leaving the translation of")
                               + " " + People.MentionOf(state.MainUsername ?? state.Uploader,
                                                           TranslatorCore.Config.api_user);
                    else if (isMain && hasBranches)
                        hint = Tr("Review Branches opens the website to accept or reject contributions");
                    else if (canCompare)
                        hint = Tr("Compare shows your changes against the website version");
                    _roleActionsHint.text = hint;
                    _roleActionsHint.gameObject.SetActive(!string.IsNullOrEmpty(hint));
                }
            }
        }

        private void OnLoginLogoutClicked()
        {
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);

            if (isLoggedIn)
            {
                // Show confirmation dialog before logout
                TranslatorUIManager.ConfirmationPanel?.Show(
                    "Logout",
                    "Are you sure you want to disconnect?\nYou'll need to re-authenticate to sync translations.",
                    "Logout",
                    () =>
                    {
                        TranslatorCore.ClearApiSession();

                        // Refresh all UI components
                        RefreshUI();
                        TranslatorUIManager.StatusOverlay?.RefreshOverlay();
                        TranslatorUIManager.NotificationDismissed = false; // Reset dismissals
                    },
                    isDanger: true
                );
            }
            else
            {
                // Show login panel
                TranslatorUIManager.LoginPanel?.SetActive(true);
            }
        }

        private async void OnUploadClicked()
        {
            if (!TranslatorCore.Config.online_mode) return;

            // Check if merge is needed - open MergePanel instead of UploadPanel
            if (TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Merge)
            {
                // Start merge flow - download remote and show merge panel
                await TranslatorUIManager.DownloadForMerge();
            }
            else
            {
                TranslatorUIManager.UploadPanel?.SetActive(true);
            }
        }

        /// <summary>
        /// Open the upload screen for the sole purpose of editing what describes the translation:
        /// its notes and its resources link. Both are prefilled from the published version, so the
        /// user edits rather than retypes.
        ///
        /// Deliberately reuses the upload path instead of a dedicated call: publishing metadata
        /// alone would need a server route that does not exist. The cost is that the file is sent
        /// again unchanged — acceptable for an action taken once in a while, and it keeps the
        /// server as the single source of truth for what a published translation contains.
        /// </summary>
        private void OnEditDetailsClicked()
        {
            if (!TranslatorCore.Config.online_mode) return;
            TranslatorUIManager.UploadPanel?.SetActive(true);
        }

        private void OnReviewOnWebsiteClicked()
        {
            // Open the merge review page on the website (Main only)
            string uuid = TranslatorCore.FileUuid;
            if (string.IsNullOrEmpty(uuid))
            {
                TranslatorCore.LogWarning("[MainPanel] Cannot open review page: no UUID");
                return;
            }

            string url = ApiClient.GetMergeReviewUrl(uuid);
            TranslatorCore.LogInfo($"[MainPanel] Opening review page: {url}");
            TranslatorCore.OpenUrlSafe(url);
        }

        /// <summary>
        /// Pull the Main into this branch. Nothing is written here: the merge is
        /// prepared and shown, and only the player's confirmation applies it —
        /// content coming from someone else never enters a translation unattended
        /// (analyse/main-to-branch-sync.md §5.2).
        /// </summary>
        private async void OnUpdateFromMainClicked()
        {
            if (_updateFromMainInFlight) return;

            SetUpdateFromMainBusy(true);

            try
            {
                await TranslatorUIManager.MergeFromMain();
            }
            catch (System.Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                    TranslatorCore.LogWarning($"[MainMerge] Failed: {errorMsg}"));
            }
            finally
            {
                TranslatorUIManager.RunOnMainThread(() => SetUpdateFromMainBusy(false));
            }
        }

        /// <summary>
        /// One act, one busy state — whichever of the two buttons carries it.
        ///
        /// ⚠ The Branch has its own row and the lineage block has another; they are never on
        /// screen together, but they share this method and OnUpdateFromMainClicked so that the
        /// pair cannot drift the way the two fork buttons did.
        /// </summary>
        private void SetUpdateFromMainBusy(bool busy)
        {
            _updateFromMainInFlight = busy;

            foreach (var button in new[] { _updateFromMainBtn, _mergeWithMainBtn })
            {
                if (button?.Component == null) continue;

                button.Component.interactable = !busy;
                ScopeMarks.Tint(button, !busy);

                if (button.ButtonText != null)
                    SetDynamicText(button.ButtonText, busy ? "Fetching..." : "Merge with Main");
            }
        }

        /// <summary>
        /// Handler for "Contribute as Branch" button (GAP 8).
        /// Opens the upload panel to contribute changes as a branch.
        /// </summary>
        private void OnContributeAsBranchClicked()
        {
            // Open upload panel - it will detect that we're contributing to an existing translation
            TranslatorUIManager.UploadPanel?.SetActive(true);
        }

        /// <summary>
        /// Handler for "Take Main's version". Its safe sibling is Merge with Main, one row above.
        /// Replaces the local file with the Main's, dropping whatever was not published.
        /// </summary>
        private async void OnDownloadLatestClicked()
        {
            var serverState = TranslatorCore.ServerState;
            if (serverState == null || !serverState.SiteId.HasValue)
            {
                TranslatorCore.LogWarning("[MainPanel] Cannot download: no server translation");
                return;
            }

            int localChanges = TranslatorCore.LocalChangesCount;

            // GAP 10: Warning for replacing local changes
            if (localChanges > 0)
            {
                TranslatorUIManager.ConfirmationPanel?.Show(
                    "Take the Main's version?",
                    $"This will replace your {localChanges} local change(s) with the latest version from "
                    + $"{People.MentionOf(serverState.Uploader, TranslatorCore.Config.api_user)}.\n\n" +
                    "Your local changes will be lost. This cannot be undone.",
                    "Replace",
                    async () => await PerformDownloadLatest(serverState),
                    isDanger: true
                );
            }
            else
            {
                await PerformDownloadLatest(serverState);
            }
        }

        /// <summary>
        /// Colour the "Take Main's version" marks for the state it is really in.
        ///
        /// 🔴 **The side of a download depends on WHOSE translation it is**, which is why this
        /// exists instead of a constant written when the button was built. Taking the latest of a
        /// lineage you lead leaves your published copy and this machine carrying the same thing —
        /// Both. Taking somebody else's leaves nothing of yours moved: it is Local, and saying
        /// Both there tells a Main owner they are in step at the moment they stop being.
        ///
        /// ⚠ Retargeting and tinting in ONE method on purpose. Done separately, a caller that
        /// tints without retargeting repaints the previous role's answer — which is the same class
        /// of bug as the side that used to be passed to both Adorn and Tint.
        /// </summary>
        private void SetDownloadLatestState(bool interactable)
        {
            if (_downloadLatestBtn == null) return;

            var state = TranslatorCore.ServerState;
            ScopeMarks.Retarget(_downloadLatestBtn,
                EditScope.SideAfter(onThisMachine: true,
                                    yourPublishedCopy: state != null && state.IsOwner));
            ScopeMarks.Tint(_downloadLatestBtn, interactable);
        }

        /// <summary>
        /// Same, for taking a translation from the community list.
        ///
        /// ⚠ Almost always somebody else's, hence Local almost always. The exception is real
        /// though: one's own published translation is listed there like any other.
        /// </summary>
        private void SetCommunityDownloadState(bool interactable)
        {
            if (_downloadBtn == null) return;

            ScopeMarks.Retarget(_downloadBtn,
                EditScope.SideAfter(onThisMachine: true,
                                    yourPublishedCopy: PublishedByUs(_translationList?.SelectedTranslation)));
            ScopeMarks.Tint(_downloadBtn, interactable);
        }

        /// <summary>
        /// Was this published under the account signed in here?
        ///
        /// ⚠ The uploader's name, NOT the lineage id: a Branch carries the Main's uuid and is
        /// somebody else's line. Matching on the uuid would call a Main owner's translation ours
        /// while we hold a branch of it — the exact confusion the vocabulary exists to prevent.
        /// </summary>
        private static bool PublishedByUs(TranslationInfo translation)
        {
            var us = TranslatorCore.Config.api_user;

            return translation != null
                && !string.IsNullOrEmpty(us)
                && string.Equals(translation.Uploader, us, System.StringComparison.OrdinalIgnoreCase);
        }

        private async System.Threading.Tasks.Task PerformDownloadLatest(ServerTranslationState serverState)
        {
            // Disable buttons while downloading
            if (_downloadLatestBtn != null)
            {
                _downloadLatestBtn.Component.interactable = false;
                SetDownloadLatestState(false);
                SetDynamicText(_downloadLatestBtn.ButtonText, "Downloading...");
            }

            try
            {
                // Create a TranslationInfo from ServerState to use the existing download flow
                var translationInfo = new TranslationInfo
                {
                    Id = serverState.SiteId.Value,
                    Uploader = serverState.Uploader,
                    TargetLanguage = TranslatorCore.Config.GetTargetLanguage(),
                    FileUuid = TranslatorCore.FileUuid
                };

                await TranslatorUIManager.DownloadTranslation(translationInfo, (success, message) =>
                {
                    if (success)
                    {
                        TranslatorCore.LogInfo("[MainPanel] Downloaded latest version successfully");
                        RefreshUI();
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[MainPanel] Download failed: {message}");
                    }

                    // Re-enable button
                    if (_downloadLatestBtn != null)
                    {
                        _downloadLatestBtn.Component.interactable = true;
                        SetDownloadLatestState(true);
                        SetDynamicText(_downloadLatestBtn.ButtonText, "Take Main's version");
                    }
                });
            }
            catch (System.Exception e)
            {
                TranslatorCore.LogWarning($"[MainPanel] Download error: {e.Message}");
                if (_downloadLatestBtn != null)
                {
                    _downloadLatestBtn.Component.interactable = true;
                    SetDownloadLatestState(true);
                    SetDynamicText(_downloadLatestBtn.ButtonText, "Take Main's version");
                }
            }
        }

        /// <summary>
        /// Handler for "Create Independent" button (GAP 8).
        /// Creates a fork with new UUID, making the user the Main owner of a new lineage.
        /// </summary>
        private void OnCreateIndependentClicked()
        {
            var serverState = TranslatorCore.ServerState;

            // 🔴 **The person being left, not the person signed in.** Uploader is the row this file
            // matches — which IS the Main's owner for somebody holding another's lineage, and is
            // ONESELF for a branch author, whose own row is their branch. Reading it alone made the
            // Fork button on that screen say "a copy of @you's translation".
            string ownerName = !string.IsNullOrEmpty(serverState?.MainUsername)
                ? serverState.MainUsername
                : (serverState?.Uploader ?? "the original owner");

            // ⚠ **It said "You will become the Main owner", which presumed the publishing.** Forking
            // sends nothing: it is this file leaving a lineage, here, now. Somebody with no account
            // may do it, and publish the day they have one — or never. What IS given up happens
            // immediately, so that is what this says.
            TranslatorUIManager.ConfirmationPanel?.Show(
                "Make an independent copy?",
                "A copy of @" + ownerName + "'s translation, starting from the file in this game as "
                + "it is now. It becomes yours."
                + "\n\nNothing is sent to the site. Publish it when you want to, or never."
                + "\n\nYou will no longer be told when @" + ownerName + "'s version changes, and "
                + "you can no longer merge with it. That part cannot be undone.",
                "Create Independent",
                () =>
                {
                    // Create fork: generate new UUID and reset server state
                    TranslatorCore.CreateFork();
                    RefreshUI();

                    // 🔴 **Offered only when there is something of one's own to publish.** A fork
                    // made from a file one has not touched is, line for line, somebody else's
                    // work: sending it puts a second identical entry on the site under a new name,
                    // and that is not sharing. It is the CONTENT that decides, never the account —
                    // a fork publishes whenever it wants to, signed in or not, now or in a year.
                    //
                    // ⚠ The card behind carries the publish action either way, so nothing is taken
                    // away: this only decides whether the screen opens by itself.
                    if (!TranslatorCore.ForkIsStillTheCopy)
                    {
                        TranslatorUIManager.UploadPanel?.SetActive(true);
                    }
                },
                isDanger: true
            );
        }

        private async void OnCompareWithServerClicked()
        {
            // Compare local changes with server version (Main or Branch)
            var serverState = TranslatorCore.ServerState;
            if (serverState?.SiteId == null)
            {
                TranslatorCore.LogWarning("[MainPanel] Cannot compare: no server translation");
                return;
            }

            // Disable button while loading
            if (_compareWithServerBtn != null)
            {
                _compareWithServerBtn.Component.interactable = false;
                SetDynamicText(_compareWithServerBtn.ButtonText, "Loading...");
            }

            // Capture values for closure
            var siteId = serverState.SiteId.Value;

            try
            {
                // Publishing comparison: this is our own translation, and validating it there
                // updates the online version. Shared with the settings dialog's Compare, which
                // opens the same page in the other direction.
                await TranslatorUIManager.OpenComparison(siteId, toLocal: false, onFinished: () =>
                {
                    if (_compareWithServerBtn != null)
                    {
                        _compareWithServerBtn.Component.interactable = true;
                        SetDynamicText(_compareWithServerBtn.ButtonText, "Compare");
                    }
                });
            }
            catch (System.Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[MainPanel] Compare error: {errorMsg}");

                    // Re-enable button
                    if (_compareWithServerBtn != null)
                    {
                        _compareWithServerBtn.Component.interactable = true;
                        SetDynamicText(_compareWithServerBtn.ButtonText, "Compare");
                    }
                });
            }
        }

        private void RefreshCommunitySection()
        {
            if (_communitySection == null) return;

            // Check online mode
            bool isOnline = TranslatorCore.Config.online_mode;

            // Update game label
            var game = TranslatorCore.CurrentGame;
            if (!isOnline)
            {
                SetDynamicText(_communityGameLabel, "Offline mode - enable Online Mode in Mod Options");
                _communityGameLabel.color = UIStyles.StatusWarning;
                _searchBtn.Component.interactable = false;

                // Clear previous search results - can't download in offline mode
                _translationList?.Clear();
                return;
            }
            else if (game != null && !string.IsNullOrEmpty(game.name))
            {
                // Game name is data — never translated
                _communityGameLabel.text = Tr("Game:")
                    + $" {game.name}";
                _communityGameLabel.color = UIStyles.TextSecondary;
                _searchBtn.Component.interactable = true;
            }
            else
            {
                SetDynamicText(_communityGameLabel, "Game: Not detected");
                _communityGameLabel.color = UIStyles.TextSecondary;
                _searchBtn.Component.interactable = false;
            }

            // Refresh list display (e.g., after login status change)
            _translationList?.Refresh();
        }

        private void OnSearchCommunityClicked()
        {
            if (!TranslatorCore.Config.online_mode) return;

            var game = TranslatorCore.CurrentGame;
            if (game == null)
            {
                _translationList.SetStatus("No game detected", UIStyles.StatusWarning);
                return;
            }

            if (_translationList.IsSearching) return;

            string targetLang = TranslatorCore.Config.GetTargetLanguage();
            SearchCommunityAsync(game.steam_id, game.name, targetLang);
        }

        private async void OnDownloadCommunityClicked()
        {
            if (!TranslatorCore.Config.online_mode) return;

            var selectedTranslation = _translationList?.SelectedTranslation;
            if (selectedTranslation == null) return;

            int localChanges = TranslatorCore.LocalChangesCount;
            int localCount = TranslatorCore.TranslationCache.Count;

            // GAP 10: Check if downloading a different lineage (different UUID)
            bool isDifferentLineage = !string.IsNullOrEmpty(TranslatorCore.FileUuid) &&
                                      !string.IsNullOrEmpty(selectedTranslation.FileUuid) &&
                                      selectedTranslation.FileUuid != TranslatorCore.FileUuid;

            if (isDifferentLineage && localCount > 0)
            {
                // WARNING: Different lineage - this is a major change
                TranslatorUIManager.ConfirmationPanel?.Show(
                    "Switch to Different Translation?",
                    $"This translation is not related to yours — it is a separate translation, not an update.\n\n" +
                    $"Your current translation ({localCount} entries) will be replaced with the translation from "
                    + $"{People.MentionOf(selectedTranslation.Uploader, TranslatorCore.Config.api_user)}.\n\n" +
                    "You will lose your current translation and its history.\n\n" +
                    "This cannot be undone.",
                    "Switch Translation",
                    async () => await PerformDownload(selectedTranslation),
                    isDanger: true
                );
            }
            else if (localChanges > 0)
            {
                // WARNING: Local changes will be lost
                TranslatorUIManager.ConfirmationPanel?.Show(
                    "Replace Local Translation?",
                    $"You have {localChanges} local change(s) that will be replaced.\n\nDownload "
                    + $"'{selectedTranslation.TargetLanguage}' by "
                    + $"{People.MentionOf(selectedTranslation.Uploader, TranslatorCore.Config.api_user)}?",
                    "Replace",
                    async () => await PerformDownload(selectedTranslation),
                    isDanger: true
                );
            }
            else
            {
                await PerformDownload(selectedTranslation);
            }
        }

        private async System.Threading.Tasks.Task PerformDownload(TranslationInfo translation)
        {
            _downloadBtn.Component.interactable = false;
            SetCommunityDownloadState(false);
            _translationList.SetStatus("Downloading...", UIStyles.StatusWarning);

            await TranslatorUIManager.DownloadTranslation(translation, (success, message) =>
            {
                if (success)
                {
                    int count = TranslatorCore.TranslationCache.Count;
                    _translationList.SetStatus($"Downloaded {count} entries!", UIStyles.StatusSuccess);
                    RefreshUI();
                }
                else
                {
                    _translationList.SetStatus($"Error: {message}", UIStyles.StatusError);
                }

                _downloadBtn.Component.interactable = _translationList?.SelectedTranslation != null;
                SetCommunityDownloadState(_translationList?.SelectedTranslation != null);
            });
        }
    }
}
