using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Layout states for context-aware UI display.
    /// </summary>
    public enum LayoutState
    {
        NotLogged,           // Show login CTA + community list prominent
        NoLocal,             // Show download prominent
        OwnerMain,           // Status + Update + Review Branches
        OwnerBranch,         // Status + Upload + Fork option
        ContributorSameUuid, // Contribute/Download/Fork choice (3 buttons)
        VisitorDiffUuid      // Download with lineage warning
    }

    /// <summary>
    /// Main settings panel. Shows translation status, account info, sync status, and action buttons.
    /// Context-aware layout adapts to user state.
    /// </summary>
    public class MainPanel : TranslatorPanelBase
    {
        public override string Name => "Unity Game Translator";
        public override int MinWidth => 450;
        public override int MinHeight => 350;
        public override int PanelWidth => 450;
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

        // UI references - Contributor choice section (GAP 8: 3 guided buttons)
        private GameObject _contributorChoiceSection;
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

        // Tab system
        private TabBar _tabBar;
        private const string TAB_MY_TRANSLATION = "My Translation";
        private const string TAB_COMMUNITY = "Community";

        // Current layout state (cached for efficiency)
        private LayoutState _currentLayoutState = LayoutState.NotLogged;

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

            // Contributor Choice Section (GAP 8: 3 guided buttons for ContributorSameUuid state)
            CreateContributorChoiceSection(myTransCard);

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

            _modUpdateBtn = UIFactory.CreateButton(_modUpdateBanner, "ModUpdateBtn", "Download");
            UIFactory.SetLayoutElement(_modUpdateBtn.Component.gameObject, minWidth: 90, minHeight: UIStyles.RowHeightNormal);
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
            // Status section - shows sync status using StatusCard widget
            _statusSection = UIFactory.CreateVerticalGroup(parent, "StatusSection", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_statusSection, flexibleWidth: 9999);

            var sectionTitle = UIStyles.CreateSectionTitle(_statusSection, "StatusSectionLabel", "Current Translation");
            RegisterUIText(sectionTitle);

            // Create StatusCard widget
            _statusCard = new StatusCard();
            _statusCard.CreateUI(_statusSection);
            _helpZone?.Describe(_statusCard.Root,
                "Your translation at a glance: sync state with the website, your role (Main = owner, Branch = contributor), and quality (Human / Validated / AI lines)");

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
            var syncRow = UIStyles.CreateFormRow(actionsBox, "SyncActionsRow", UIStyles.RowHeightLarge, UIStyles.SmallSpacing);

            _uploadBtn = CreatePrimaryButton(syncRow, "UploadBtn", "Upload Translation", 200);
            UIFactory.SetLayoutElement(_uploadBtn.Component.gameObject, flexibleWidth: 9999);
            _uploadBtn.OnClick += OnUploadClicked;
            RegisterExcluded(_uploadBtn.ButtonText);
            _helpZone?.Describe(_uploadBtn.Component.gameObject,
                "Send your local translation to the website so others can use it");

            // Compare with Server — belongs next to the push it qualifies
            _compareWithServerBtn = CreateSecondaryButton(syncRow, "CompareBtn", "Compare", 100);
            UIStyles.SetBackground(_compareWithServerBtn.Component.gameObject, UIStyles.ButtonSecondary);
            _compareWithServerBtn.OnClick += OnCompareWithServerClicked;
            RegisterExcluded(_compareWithServerBtn.ButtonText);
            _helpZone?.Describe(_compareWithServerBtn.Component.gameObject,
                "See the differences between your local file and the version on the website");

            _uploadHintLabel = UIStyles.CreateHint(actionsBox, "UploadHintLabel", "");
            RegisterExcluded(_uploadHintLabel);

            // Role-specific action buttons row
            var roleActionsRow = UIStyles.CreateFormRow(actionsBox, "RoleActionsRow", UIStyles.RowHeightLarge);
            var rowLayout = roleActionsRow.GetComponent<HorizontalLayoutGroup>();
            if (rowLayout != null) rowLayout.childAlignment = TextAnchor.MiddleCenter;

            // Review on Website button (Main only) - opens page to review branches
            _reviewOnWebsiteBtn = CreateSecondaryButton(roleActionsRow, "ReviewBtn", "Review Branches", 130);
            UIStyles.SetBackground(_reviewOnWebsiteBtn.Component.gameObject, UIStyles.ButtonLink);
            _reviewOnWebsiteBtn.OnClick += OnReviewOnWebsiteClicked;
            RegisterUIText(_reviewOnWebsiteBtn.ButtonText);
            _helpZone?.Describe(_reviewOnWebsiteBtn.Component.gameObject,
                "Open the website to accept or reject changes proposed by other players");

            // Edit details (owners) — the description and the resources link were only reachable
            // through the upload screen, which is closed once everything is in sync. Fixing a dead
            // link or rewording a description then had no path at all.
            _editDetailsBtn = CreateSecondaryButton(roleActionsRow, "EditDetailsBtn", "Edit details", 110);
            UIStyles.SetBackground(_editDetailsBtn.Component.gameObject, UIStyles.ButtonSecondary);
            _editDetailsBtn.OnClick += OnEditDetailsClicked;
            RegisterUIText(_editDetailsBtn.ButtonText);
            _helpZone?.Describe(_editDetailsBtn.Component.gameObject,
                "Change the description and the resources link of your published translation, without waiting for new translated lines");

            // Update from Main (Branch only) — the other direction of the exchange.
            // A branch could publish its work but never take in what the Main had
            // published since: it drifted further apart with every update, without
            // anything ever saying so.
            _updateFromMainBtn = CreateSecondaryButton(roleActionsRow, "UpdateFromMainBtn", "Update from Main", 150);
            UIStyles.SetBackground(_updateFromMainBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _updateFromMainBtn.OnClick += OnUpdateFromMainClicked;
            RegisterUIText(_updateFromMainBtn.ButtonText);
            _helpZone?.Describe(_updateFromMainBtn.Component.gameObject,
                "Bring in what the original translation added or corrected since your last update. Your own lines are kept, and you review everything before it applies.");

            // Fork button (Branch only) - creates independent fork
            _forkBtn = CreateSecondaryButton(roleActionsRow, "ForkBtn", "Fork", 80);
            UIStyles.SetBackground(_forkBtn.Component.gameObject, UIStyles.ButtonDanger);
            _forkBtn.OnClick += OnForkClicked;
            RegisterUIText(_forkBtn.ButtonText);
            _helpZone?.Describe(_forkBtn.Component.gameObject,
                "Leave the owner's translation and continue on your own — asks for confirmation first");

            // One-line explanation for whichever role buttons are visible
            _roleActionsHint = UIStyles.CreateHint(actionsBox, "RoleActionsHint", "");
            RegisterExcluded(_roleActionsHint);
        }

        /// <summary>
        /// Creates the contributor choice section with 3 guided buttons (GAP 8).
        /// Shown only for ContributorSameUuid state.
        /// </summary>
        private void CreateContributorChoiceSection(GameObject parent)
        {
            _contributorChoiceSection = UIFactory.CreateVerticalGroup(parent, "ContributorChoiceSection", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_contributorChoiceSection, flexibleWidth: 9999);

            var sectionTitle = UIStyles.CreateSectionTitle(_contributorChoiceSection, "ChoiceSectionLabel", "What would you like to do?");
            RegisterUIText(sectionTitle);

            var choiceBox = CreateSection(_contributorChoiceSection, "ChoiceBox");

            // Button 1: Contribute as Branch
            var branchRow = UIFactory.CreateVerticalGroup(choiceBox, "BranchRow", false, false, true, true, 2);
            UIFactory.SetLayoutElement(branchRow, flexibleWidth: 9999, minHeight: UIStyles.RowHeightLarge + UIStyles.RowHeightNormal);

            _contributeAsBranchBtn = CreatePrimaryButton(branchRow, "ContributeBtn", "Contribute as Branch", 250);
            UIStyles.SetBackground(_contributeAsBranchBtn.Component.gameObject, UIStyles.ButtonSuccess);
            UIFactory.SetLayoutElement(_contributeAsBranchBtn.Component.gameObject, flexibleWidth: 9999);
            _contributeAsBranchBtn.OnClick += OnContributeAsBranchClicked;
            RegisterUIText(_contributeAsBranchBtn.ButtonText);
            _helpZone?.Describe(_contributeAsBranchBtn.Component.gameObject,
                "Your changes are sent to the owner, who can merge them into the main translation");

            var branchDesc = UIFactory.CreateLabel(branchRow, "BranchDesc", "Your changes will help improve the main translation", TextAnchor.MiddleCenter);
            branchDesc.fontSize = UIStyles.FontSizeSmall;
            branchDesc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(branchDesc.gameObject, flexibleWidth: 9999, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(branchDesc);

            UIStyles.CreateSpacer(choiceBox, 8);

            // Button 2: Download Latest
            var downloadRow = UIFactory.CreateVerticalGroup(choiceBox, "DownloadRow", false, false, true, true, 2);
            UIFactory.SetLayoutElement(downloadRow, flexibleWidth: 9999, minHeight: UIStyles.RowHeightLarge + UIStyles.RowHeightNormal);

            _downloadLatestBtn = CreateSecondaryButton(downloadRow, "DownloadLatestBtn", "Download Latest", 250);
            UIStyles.SetBackground(_downloadLatestBtn.Component.gameObject, UIStyles.ButtonPrimary);
            UIFactory.SetLayoutElement(_downloadLatestBtn.Component.gameObject, flexibleWidth: 9999);
            _downloadLatestBtn.OnClick += OnDownloadLatestClicked;
            RegisterExcluded(_downloadLatestBtn.ButtonText);
            _helpZone?.Describe(_downloadLatestBtn.Component.gameObject,
                "Replace your local file with the owner's latest version from the website");

            var downloadDesc = UIFactory.CreateLabel(downloadRow, "DownloadDesc", "Get the owner's latest version (replaces your local)", TextAnchor.MiddleCenter);
            downloadDesc.fontSize = UIStyles.FontSizeSmall;
            downloadDesc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(downloadDesc.gameObject, flexibleWidth: 9999, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(downloadDesc);

            UIStyles.CreateSpacer(choiceBox, 8);

            // Button 3: Create Independent (Fork)
            var forkRow = UIFactory.CreateVerticalGroup(choiceBox, "ForkRow", false, false, true, true, 2);
            UIFactory.SetLayoutElement(forkRow, flexibleWidth: 9999, minHeight: UIStyles.RowHeightLarge + UIStyles.RowHeightNormal);

            _createIndependentBtn = CreateSecondaryButton(forkRow, "CreateIndependentBtn", "Create Independent", 250);
            UIStyles.SetBackground(_createIndependentBtn.Component.gameObject, UIStyles.ButtonDanger);
            UIFactory.SetLayoutElement(_createIndependentBtn.Component.gameObject, flexibleWidth: 9999);
            _createIndependentBtn.OnClick += OnCreateIndependentClicked;
            RegisterUIText(_createIndependentBtn.ButtonText);
            _helpZone?.Describe(_createIndependentBtn.Component.gameObject,
                "Start your own independent translation from your current file — asks for confirmation first");

            var forkDesc = UIFactory.CreateLabel(forkRow, "ForkDesc", "Start your own independent translation, no longer linked to the original", TextAnchor.MiddleCenter);
            forkDesc.fontSize = UIStyles.FontSizeSmall;
            forkDesc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(forkDesc.gameObject, flexibleWidth: 9999, minHeight: UIStyles.RowHeightNormal);
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
            // Community section - now a full tab, no longer collapsible
            _communitySection = UIFactory.CreateVerticalGroup(parent, "CommunitySection", false, false, true, true, 5);
            UIFactory.SetLayoutElement(_communitySection, flexibleWidth: 9999, flexibleHeight: 9999);

            var sectionTitle = UIStyles.CreateSectionTitle(_communitySection, "CommunitySectionLabel", "Community Translations");
            RegisterUIText(sectionTitle);

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
                }
            });

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
            _downloadBtn.Component.interactable = false;
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

                // Reserve the height of the TALLEST tab, like the other tabbed panels do.
                // Without it the panel was sized for whichever tab happened to be open, so
                // switching to Community resized the whole window under the user.
                if (!_tabHeightFixed && _tabBar != null)
                {
                    UniverseLib.RuntimeHelper.StartCoroutine(DelayedFixTabHeight());
                }

                // Auto-search community translations if conditions are met
                TryAutoSearchCommunity();
            }
        }

        private bool _tabHeightFixed;

        private System.Collections.IEnumerator DelayedFixTabHeight()
        {
            // Let Unity settle the layouts before measuring them
            yield return null;
            yield return null;
            yield return null;

            if (_tabBar != null && _tabBar.ContentContainer != null)
            {
                float maxTabHeight = _tabBar.MeasureMaxContentHeight();
                if (maxTabHeight > 0)
                {
                    UIFactory.SetLayoutElement(_tabBar.ContentContainer, minHeight: Mathf.CeilToInt(maxTabHeight));
                    _tabHeightFixed = true;
                    RecalculateSize();
                }
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
                }
            });
        }

        /// <summary>
        /// Detects the current layout state based on login, local translations, and server state.
        /// </summary>
        private LayoutState DetectCurrentState()
        {
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            int localCount = TranslatorCore.TranslationCache.Count;
            var serverState = TranslatorCore.ServerState;
            bool existsOnServer = serverState != null && serverState.Exists && serverState.SiteId.HasValue;

            // Not logged in
            if (!isLoggedIn)
            {
                return LayoutState.NotLogged;
            }

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
                    return LayoutState.ContributorSameUuid;
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
                        return LayoutState.ContributorSameUuid;
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
            }
        }

        /// <summary>
        /// Updates section visibility based on current layout state.
        /// </summary>
        private void RefreshLayoutVisibility()
        {
            // Login CTA - only show when not logged in
            if (_loginCTASection != null)
            {
                _loginCTASection.SetActive(_currentLayoutState == LayoutState.NotLogged);

                // Disable CTA button when offline
                if (_loginCTABtn != null)
                {
                    _loginCTABtn.Component.interactable = TranslatorCore.Config.online_mode;
                }
            }

            // Determine if we should show StatusCard vs legacy TranslationInfo
            bool showStatusCard = _currentLayoutState != LayoutState.NotLogged &&
                                  _currentLayoutState != LayoutState.NoLocal;

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

            // Contributor choice section (GAP 8) - only for ContributorSameUuid state
            if (_contributorChoiceSection != null)
            {
                _contributorChoiceSection.SetActive(_currentLayoutState == LayoutState.ContributorSameUuid);
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
                case LayoutState.NotLogged:
                    if (localCount > 0)
                    {
                        // Has local but not logged - encourage account creation
                        message = "Create an account to sync your translations and contribute to the community.";
                    }
                    break;

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

                case LayoutState.ContributorSameUuid:
                    // Same UUID but not owner - show info about parent
                    if (serverState != null)
                    {
                        int localChanges = TranslatorCore.LocalChangesCount;
                        if (localChanges > 0)
                        {
                            // Count inline (placeholdered), uploader appended as data
                            message = Tr($"You have {localChanges} changes compared to the translation of")
                                      + $" @{serverState.Uploader}";
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

            // Show or hide guidance section based on message
            bool hasMessage = !string.IsNullOrEmpty(message);
            _guidanceSection.SetActive(hasMessage);
            if (hasMessage)
            {
                // The ContributorSameUuid branch already translated (it appends a username);
                // the others are plain sentences translated here.
                _guidanceLabel.text = _currentLayoutState == LayoutState.ContributorSameUuid
                    ? message
                    : TranslatorCore.TranslateOwnUIDynamic(message, _guidanceLabel);
            }
        }

        /// <summary>
        /// Updates the StatusCard with current translation state.
        /// </summary>
        private void RefreshStatusCard()
        {
            if (_statusCard == null) return;

            var serverState = TranslatorCore.ServerState;
            int entryCount = TranslatorCore.TranslationCache.Count;
            string targetLang = TranslatorCore.Config.GetTargetLanguage();
            int localChanges = TranslatorCore.LocalChangesCount;

            // Determine sync status
            SyncStatusType syncStatus;
            bool needsMerge = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Merge;
            bool hasServerUpdate = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Download;

            if (needsMerge)
            {
                syncStatus = SyncStatusType.OutOfSync;
            }
            else if (localChanges > 0 || hasServerUpdate || TranslatorCore.MetadataDirty)
            {
                // Metadata counts: settings edited but not pushed are still something to sync.
                // Leaving it out showed SYNCED next to a button offering an update.
                syncStatus = SyncStatusType.OutOfSync;
            }
            else if (serverState != null && serverState.Exists)
            {
                syncStatus = SyncStatusType.Synced;
            }
            else
            {
                syncStatus = SyncStatusType.LocalOnly;
            }

            // Identity leads the card: which languages, whatever the mode
            _statusCard.SetIdentity(TranslatorCore.Config.GetSourceLanguage(), targetLang);

            // Configure card based on layout state
            switch (_currentLayoutState)
            {
                case LayoutState.OwnerMain:
                    int branches = serverState?.BranchesCount ?? 0;
                    _statusCard.ConfigureAsMainOwner(syncStatus, entryCount, targetLang, branches);
                    // One key action per mode, next to what motivates it. Contributions to review
                    // live on the website, which is where a maintainer accepts or rejects them.
                    if (branches > 0 && _reviewOnWebsiteBtn != null)
                        _statusCard.SetModeAction($"{branches} contribution(s) waiting", "Review", OnReviewOnWebsiteClicked);
                    break;

                case LayoutState.OwnerBranch:
                    _statusCard.ConfigureAsBranchOwner(
                        syncStatus,
                        entryCount,
                        targetLang,
                        serverState?.MainUsername ?? serverState?.Uploader);
                    // A branch owner's question is "what did I change?" — comparing answers it.
                    if (localChanges > 0)
                        _statusCard.SetModeAction($"{localChanges} change(s) not submitted", "Compare", OnCompareWithServerClicked);
                    break;

                case LayoutState.ContributorSameUuid:
                    _statusCard.ConfigureAsContributor(
                        syncStatus,
                        entryCount,
                        targetLang,
                        serverState?.Uploader);
                    break;

                case LayoutState.VisitorDiffUuid:
                    _statusCard.ConfigureAsLocalOnly(entryCount, targetLang);
                    // Nothing shared yet: the one thing worth doing is sharing it.
                    if (entryCount > 0)
                        _statusCard.SetModeAction("Not shared yet", "Upload", OnUploadClicked);
                    break;

                default:
                    _statusCard.ConfigureAsNoLocal();
                    break;
            }

            // What the community made of this translation, and the player's own say. Whatever
            // the mode: an author is entitled to see their count, a player to give one back.
            // Hidden by the card itself when the server reported no vote at all.
            _statusCard.SetVote(
                serverState?.Vote,
                _currentLayoutState == LayoutState.OwnerMain
                    ? TranslationRoleType.Main
                    : TranslationRoleType.None);

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
                    + $" {serverState.Uploader ?? "Website"} (#{serverState.SiteId})";

                // Role indicator
                switch (serverState.Role)
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
                            + $" @{serverState.MainUsername ?? serverState.Uploader}";
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
                    + $" @{state.Uploader}";
            }
            else
            {
                uploadAction = "Upload Translation";
                uploadHint = Tr("Create a new translation");
            }

            SetDynamicText(_uploadBtn.ButtonText, uploadAction);

            // Enable/disable based on conditions
            // Disable if in sync (nothing to upload) or other conditions not met
            bool canUpload = isLoggedIn && TranslatorCore.Config.online_mode &&
                            TranslatorCore.TranslationCache.Count > 0 && !isInSync;
            _uploadBtn.Component.interactable = canUpload;

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

                // Compare with Server - only for owners (Main or Branch) who have uploaded
                // Non-owners can't compare because they don't have a server version to compare against
                bool canCompare = existsOnServer && state.IsOwner && hasLocalChanges;
                _compareWithServerBtn.Component.gameObject.SetActive(canCompare);
                if (canCompare)
                {
                    _compareWithServerBtn.Component.interactable = isLoggedIn;
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

                // Update from Main — a branch only. Shown even when nothing new is
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
                }

                // Explain the visible buttons in plain words
                if (_roleActionsHint != null)
                {
                    string hint = "";
                    if (isBranch && TranslatorUIManager.HasMainUpdate())
                        hint = Tr("The original translation has changed — Update from Main brings it in");
                    else if (isBranch)
                        hint = Tr("Fork = continue on your own, leaving the translation of")
                               + " @" + (state.MainUsername ?? state.Uploader ?? "?");
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

        private void SetUpdateFromMainBusy(bool busy)
        {
            _updateFromMainInFlight = busy;

            if (_updateFromMainBtn?.Component != null)
                _updateFromMainBtn.Component.interactable = !busy;

            if (_updateFromMainBtn?.ButtonText != null)
                SetDynamicText(_updateFromMainBtn.ButtonText, busy ? "Fetching..." : "Update from Main");
        }

        private void OnForkClicked()
        {
            // Show confirmation dialog before forking
            TranslatorUIManager.ConfirmationPanel?.Show(
                "Fork Translation",
                "This will create an independent copy of your translation with a new UUID.\n\n" +
                "You will become the Main owner of this new translation.\n\n" +
                "This action cannot be undone. The link to the original Main will be lost.",
                "Fork",
                () =>
                {
                    // Create fork: generate new UUID and reset server state
                    TranslatorCore.CreateFork();
                    RefreshUI();

                    // Open upload panel to push the forked translation
                    TranslatorUIManager.UploadPanel?.SetActive(true);
                },
                isDanger: true
            );
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
        /// Handler for "Download Latest" button (GAP 8).
        /// Downloads the owner's latest version, replacing local changes.
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
                    "Download Latest Version?",
                    $"This will replace your {localChanges} local change(s) with @{serverState.Uploader}'s latest version.\n\n" +
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

        private async System.Threading.Tasks.Task PerformDownloadLatest(ServerTranslationState serverState)
        {
            // Disable buttons while downloading
            if (_downloadLatestBtn != null)
            {
                _downloadLatestBtn.Component.interactable = false;
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
                        SetDynamicText(_downloadLatestBtn.ButtonText, "Download Latest");
                    }
                });
            }
            catch (System.Exception e)
            {
                TranslatorCore.LogWarning($"[MainPanel] Download error: {e.Message}");
                if (_downloadLatestBtn != null)
                {
                    _downloadLatestBtn.Component.interactable = true;
                    SetDynamicText(_downloadLatestBtn.ButtonText, "Download Latest");
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
            string ownerName = serverState?.Uploader ?? "the original owner";

            // GAP 10: Warning for creating independent fork
            TranslatorUIManager.ConfirmationPanel?.Show(
                "Create Independent Translation?",
                $"This will create a new independent translation, no longer linked to the original.\n\n" +
                $"You will become the Main owner of this new translation.\n\n" +
                $"You will no longer be able to merge changes with @{ownerName}'s translation.\n\n" +
                "This action cannot be undone.",
                "Create Independent",
                () =>
                {
                    // Create fork: generate new UUID and reset server state
                    TranslatorCore.CreateFork();
                    RefreshUI();

                    // Open upload panel to push the new independent translation
                    TranslatorUIManager.UploadPanel?.SetActive(true);
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
                    $"Your current translation ({localCount} entries) will be replaced with @{selectedTranslation.Uploader}'s translation.\n\n" +
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
                    $"You have {localChanges} local change(s) that will be replaced.\n\nDownload '{selectedTranslation.TargetLanguage}' by {selectedTranslation.Uploader}?",
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
            });
        }
    }
}
