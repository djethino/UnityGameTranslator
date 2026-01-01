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
        private Text _ollamaStatusLabel;

        // UI references - Actions section
        private ButtonRef _uploadBtn;
        private Text _uploadHintLabel;
        private ButtonRef _reviewOnWebsiteBtn;
        private ButtonRef _compareWithServerBtn;
        private ButtonRef _forkBtn;

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
        private Text _communityCollapseIcon;
        private GameObject _communityContent;
        private bool _isCommunityExpanded = true;

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

            // Main card - adaptive, sizes to content (PanelWidth - 2*PanelPadding)
            var card = CreateAdaptiveCard(scrollContent, "MainCard", PanelWidth - 40);

            // Mod Update Banner (at very top, visible only when update available)
            CreateModUpdateBanner(card);

            var title = CreateTitle(card, "Title", "Unity Game Translator");
            RegisterExcluded(title); // Mod name - never translate

            UIStyles.CreateSpacer(card, 5);

            // Account Section (always visible, compact)
            CreateAccountSection(card);

            UIStyles.CreateSpacer(card, 10);

            // Login CTA Section (only visible when not logged in)
            CreateLoginCTASection(card);

            // Status Section with StatusCard (visible when logged in + has local)
            CreateStatusSection(card);

            UIStyles.CreateSpacer(card, 5);

            // Legacy Translation Info Section (kept for backward compatibility, will be hidden when StatusCard is shown)
            CreateTranslationInfoSection(card);

            UIStyles.CreateSpacer(card, 10);

            // Community Translations Section (collapsible, online mode only)
            CreateCommunitySection(card);

            UIStyles.CreateSpacer(card, 10);

            // Actions Section (context-dependent)
            CreateActionsSection(card);

            // Contributor Choice Section (GAP 8: 3 guided buttons for ContributorSameUuid state)
            CreateContributorChoiceSection(card);

            // Guidance Section (GAP 9: contextual messages)
            CreateGuidanceSection(card);

            // Bottom buttons - in fixed footer (outside scroll)
            var optionsBtn = CreateSecondaryButton(buttonRow, "OptionsBtn", "Options");
            optionsBtn.OnClick += () => TranslatorUIManager.OptionsPanel?.SetActive(true);
            RegisterUIText(optionsBtn.ButtonText);

            var closeBtn = CreatePrimaryButton(buttonRow, "CloseBtn", "Close");
            closeBtn.OnClick += () => SetActive(false);
            RegisterUIText(closeBtn.ButtonText);

            RefreshUI();
        }

        private void CreateAccountSection(GameObject parent)
        {
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "AccountSectionLabel", "Account");
            RegisterUIText(sectionTitle);

            var accountBox = CreateSection(parent, "AccountBox");

            var accountRow = UIStyles.CreateFormRow(accountBox, "AccountRow", UIStyles.RowHeightLarge);

            _accountLabel = UIFactory.CreateLabel(accountRow, "AccountLabel", "Not connected", TextAnchor.MiddleLeft);
            _accountLabel.fontStyle = FontStyle.Italic;
            _accountLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_accountLabel.gameObject, flexibleWidth: 9999);
            RegisterUIText(_accountLabel);

            _loginLogoutBtn = CreateSecondaryButton(accountRow, "LoginLogoutBtn", "Login", 80);
            _loginLogoutBtn.OnClick += OnLoginLogoutClicked;
            RegisterUIText(_loginLogoutBtn.ButtonText);
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
                padding.padding = new RectOffset(10, 10, 5, 5);
                padding.childAlignment = TextAnchor.MiddleLeft;
            }

            _modUpdateLabel = UIFactory.CreateLabel(_modUpdateBanner, "ModUpdateLabel", "Update available: v?.?.?", TextAnchor.MiddleLeft);
            _modUpdateLabel.fontStyle = FontStyle.Bold;
            _modUpdateLabel.color = Color.white;
            UIFactory.SetLayoutElement(_modUpdateLabel.gameObject, flexibleWidth: 9999);
            RegisterUIText(_modUpdateLabel);

            _modUpdateBtn = UIFactory.CreateButton(_modUpdateBanner, "ModUpdateBtn", "Download");
            UIFactory.SetLayoutElement(_modUpdateBtn.Component.gameObject, minWidth: 90, minHeight: UIStyles.RowHeightNormal);
            _modUpdateBtn.OnClick += OnModUpdateClicked;
            RegisterUIText(_modUpdateBtn.ButtonText);

            // Start hidden
            _modUpdateBanner.SetActive(false);
        }

        private void OnModUpdateClicked()
        {
            var info = TranslatorUIManager.ModUpdateInfo;
            string url = info?.DownloadUrl ?? info?.ReleaseUrl;
            if (!string.IsNullOrEmpty(url))
            {
                Application.OpenURL(url);
            }
        }

        private void CreateLoginCTASection(GameObject parent)
        {
            // Login CTA - prominent call-to-action for not logged in users
            _loginCTASection = UIFactory.CreateVerticalGroup(parent, "LoginCTASection", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_loginCTASection, flexibleWidth: 9999);

            var ctaCard = UIStyles.CreateAdaptiveCard(_loginCTASection, "CTACard", PanelWidth - 60);
            UIStyles.SetBackground(ctaCard, UIStyles.SectionBackground);

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
            RegisterUIText(_entriesLabel);

            _targetLabel = UIFactory.CreateLabel(infoBox, "TargetLabel", "Target: auto", TextAnchor.MiddleLeft);
            _targetLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_targetLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(_targetLabel);

            _sourceLabel = UIFactory.CreateLabel(infoBox, "SourceLabel", "Source: Local", TextAnchor.MiddleLeft);
            _sourceLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_sourceLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(_sourceLabel);

            _roleLabel = UIFactory.CreateLabel(infoBox, "RoleLabel", "", TextAnchor.MiddleLeft);
            _roleLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_roleLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(_roleLabel);

            _syncStatusLabel = UIFactory.CreateLabel(infoBox, "SyncStatusLabel", "", TextAnchor.MiddleLeft);
            _syncStatusLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_syncStatusLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(_syncStatusLabel);

            _ollamaStatusLabel = CreateSmallLabel(infoBox, "OllamaStatusLabel", "");
            RegisterUIText(_ollamaStatusLabel);
        }

        private void CreateActionsSection(GameObject parent)
        {
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "ActionsSectionLabel", "Actions");
            RegisterUIText(sectionTitle);

            var actionsBox = CreateSection(parent, "ActionsBox");

            _uploadBtn = CreatePrimaryButton(actionsBox, "UploadBtn", "Upload Translation", 200);
            UIFactory.SetLayoutElement(_uploadBtn.Component.gameObject, flexibleWidth: 9999);
            _uploadBtn.OnClick += OnUploadClicked;
            RegisterUIText(_uploadBtn.ButtonText);

            _uploadHintLabel = UIStyles.CreateHint(actionsBox, "UploadHintLabel", "");
            RegisterUIText(_uploadHintLabel);

            // Role-specific action buttons row
            var roleActionsRow = UIStyles.CreateFormRow(actionsBox, "RoleActionsRow", UIStyles.RowHeightLarge);
            var rowLayout = roleActionsRow.GetComponent<HorizontalLayoutGroup>();
            if (rowLayout != null) rowLayout.childAlignment = TextAnchor.MiddleCenter;

            // Review on Website button (Main only) - opens page to review branches
            _reviewOnWebsiteBtn = CreateSecondaryButton(roleActionsRow, "ReviewBtn", "Review Branches", 130);
            UIStyles.SetBackground(_reviewOnWebsiteBtn.Component.gameObject, UIStyles.ButtonLink);
            _reviewOnWebsiteBtn.OnClick += OnReviewOnWebsiteClicked;
            RegisterUIText(_reviewOnWebsiteBtn.ButtonText);

            // Compare with Server button (Main and Branch) - opens diff view
            _compareWithServerBtn = CreateSecondaryButton(roleActionsRow, "CompareBtn", "Compare", 100);
            UIStyles.SetBackground(_compareWithServerBtn.Component.gameObject, UIStyles.ButtonSecondary);
            _compareWithServerBtn.OnClick += OnCompareWithServerClicked;
            RegisterUIText(_compareWithServerBtn.ButtonText);

            // Fork button (Branch only) - creates independent fork
            _forkBtn = CreateSecondaryButton(roleActionsRow, "ForkBtn", "Fork", 80);
            UIStyles.SetBackground(_forkBtn.Component.gameObject, UIStyles.ButtonDanger);
            _forkBtn.OnClick += OnForkClicked;
            RegisterUIText(_forkBtn.ButtonText);
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
            RegisterUIText(_downloadLatestBtn.ButtonText);

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

            var forkDesc = UIFactory.CreateLabel(forkRow, "ForkDesc", "Fork into your own translation (new lineage)", TextAnchor.MiddleCenter);
            forkDesc.fontSize = UIStyles.FontSizeSmall;
            forkDesc.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(forkDesc.gameObject, flexibleWidth: 9999, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(forkDesc);
        }

        /// <summary>
        /// Creates the guidance section for contextual messages (GAP 9).
        /// </summary>
        private void CreateGuidanceSection(GameObject parent)
        {
            _guidanceSection = UIFactory.CreateVerticalGroup(parent, "GuidanceSection", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_guidanceSection, flexibleWidth: 9999);

            var guidanceBox = UIStyles.CreateAdaptiveCard(_guidanceSection, "GuidanceBox", PanelWidth - 60);
            UIStyles.SetBackground(guidanceBox, new Color(0.15f, 0.2f, 0.25f, 0.9f));

            _guidanceLabel = UIFactory.CreateLabel(guidanceBox, "GuidanceLabel", "", TextAnchor.MiddleCenter);
            _guidanceLabel.fontSize = UIStyles.FontSizeNormal;
            _guidanceLabel.color = UIStyles.StatusInfo;
            UIFactory.SetLayoutElement(_guidanceLabel.gameObject, flexibleWidth: 9999, minHeight: UIStyles.RowHeightLarge);
            RegisterUIText(_guidanceLabel);
        }

        private void CreateCommunitySection(GameObject parent)
        {
            // Use collapsible section for community translations
            var (container, header, iconLabel, titleLabel, content) = UIStyles.CreateCollapsibleSection(
                parent, "Community", "Community Translations", initiallyExpanded: true);

            _communitySection = container;
            _communityCollapseIcon = iconLabel;
            _communityContent = content;

            RegisterUIText(titleLabel);

            // Wire up header click to toggle collapse (using UIHelpers for IL2CPP compatibility)
            var headerBtn = header.GetComponent<Button>();
            if (headerBtn != null)
            {
                UIHelpers.AddButtonListener(headerBtn, OnCommunityHeaderClicked);
            }

            // Game info and search row
            var searchRow = UIStyles.CreateFormRow(content, "SearchRow", UIStyles.RowHeightLarge);

            _communityGameLabel = UIFactory.CreateLabel(searchRow, "GameLabel", "Game: Unknown", TextAnchor.MiddleLeft);
            _communityGameLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_communityGameLabel.gameObject, flexibleWidth: 9999);
            RegisterUIText(_communityGameLabel);

            _searchBtn = CreateSecondaryButton(searchRow, "SearchBtn", "Search", 80);
            _searchBtn.OnClick += OnSearchCommunityClicked;
            RegisterUIText(_searchBtn.ButtonText);

            // Translation list - ensure initialized
            if (_translationList == null)
            {
                TranslatorCore.LogWarning("[MainPanel] _translationList was null - reinitializing");
                _translationList = new TranslationList();
            }
            _translationList.CreateUI(content, 100, onSelectionChanged: (t) =>
            {
                if (_downloadBtn != null)
                {
                    _downloadBtn.Component.interactable = t != null;
                }
            });

            UIStyles.CreateSpacer(content, 5);

            // Download button
            var downloadRow = UIStyles.CreateFormRow(content, "DownloadRow", UIStyles.RowHeightLarge, 0);
            var layoutGroup = downloadRow.GetComponent<HorizontalLayoutGroup>();
            if (layoutGroup != null) layoutGroup.childAlignment = TextAnchor.MiddleCenter; // Center the button

            _downloadBtn = CreatePrimaryButton(downloadRow, "DownloadBtn", "Download Selected", 160);
            UIStyles.SetBackground(_downloadBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _downloadBtn.OnClick += OnDownloadCommunityClicked;
            _downloadBtn.Component.interactable = false;
            RegisterUIText(_downloadBtn.ButtonText);
        }

        private void OnCommunityHeaderClicked()
        {
            _isCommunityExpanded = !_isCommunityExpanded;
            UIStyles.SetCollapsibleState(_communityCollapseIcon, _communityContent, _isCommunityExpanded);
            RecalculateSize();
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
            }
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
                _modUpdateLabel.text = $"Mod update available: v{info?.LatestVersion ?? "?"}";

                // Show appropriate button text
                bool hasDirectDownload = !string.IsNullOrEmpty(info?.DownloadUrl);
                _modUpdateBtn.ButtonText.text = hasDirectDownload ? "Download" : "View Release";
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
                    if (TranslatorCore.Config.enable_ollama)
                    {
                        message = "Use Ollama to translate captured text, or download a community translation.";
                    }
                    else
                    {
                        message = "Enable Ollama for AI translation, or download a community translation to get started.";
                    }
                    break;

                case LayoutState.ContributorSameUuid:
                    // Same UUID but not owner - show info about parent
                    if (serverState != null)
                    {
                        int localChanges = TranslatorCore.LocalChangesCount;
                        if (localChanges > 0)
                        {
                            message = $"You have {localChanges} changes compared to @{serverState.Uploader}'s translation.";
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
                _guidanceLabel.text = message;
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
                syncStatus = SyncStatusType.Conflict;
            }
            else if (localChanges > 0 || hasServerUpdate)
            {
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

            // Configure card based on layout state
            switch (_currentLayoutState)
            {
                case LayoutState.OwnerMain:
                    _statusCard.ConfigureAsMainOwner(
                        syncStatus,
                        entryCount,
                        targetLang,
                        serverState?.BranchesCount ?? 0);
                    break;

                case LayoutState.OwnerBranch:
                    _statusCard.ConfigureAsBranchOwner(
                        syncStatus,
                        entryCount,
                        targetLang,
                        serverState?.MainUsername ?? serverState?.Uploader);
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
                    break;

                default:
                    _statusCard.ConfigureAsNoLocal();
                    break;
            }
        }

        private void RefreshAccountSection()
        {
            if (_accountLabel == null) return;

            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            string currentUser = TranslatorCore.Config.api_user;

            if (isLoggedIn)
            {
                _accountLabel.text = $"Connected as @{currentUser ?? "Unknown"}";
                _accountLabel.fontStyle = FontStyle.Normal;
                _loginLogoutBtn.ButtonText.text = "Logout";
            }
            else
            {
                _accountLabel.text = "Not connected";
                _accountLabel.fontStyle = FontStyle.Italic;
                _loginLogoutBtn.ButtonText.text = "Login";

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

            TranslatorCore.LogInfo($"[MainPanel] RefreshTranslationInfo: entries={entryCount}, target={targetLang}, serverState={(serverState == null ? "null" : $"checked={serverState.Checked}")}");

            _entriesLabel.text = $"Entries: {entryCount}";
            _targetLabel.text = $"Target: {targetLang}";

            if (existsOnServer)
            {
                _sourceLabel.text = $"Source: {serverState.Uploader ?? "Website"} (#{serverState.SiteId})";

                // Role indicator
                switch (serverState.Role)
                {
                    case TranslationRole.Main:
                        if (serverState.BranchesCount > 0)
                        {
                            _roleLabel.text = $"[MAIN] {serverState.BranchesCount} branch(es) contributing";
                        }
                        else
                        {
                            _roleLabel.text = "[MAIN] You own this translation";
                        }
                        _roleLabel.color = UIStyles.StatusSuccess;
                        break;
                    case TranslationRole.Branch:
                        _roleLabel.text = $"[BRANCH] Contributing to @{serverState.MainUsername ?? serverState.Uploader}";
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
                    _syncStatusLabel.text = $"CONFLICT - Both local ({localChanges}) and server changed!";
                    _syncStatusLabel.color = UIStyles.StatusError;
                }
                else if (localChanges > 0)
                {
                    _syncStatusLabel.text = $"OUT OF SYNC - {localChanges} local changes to upload";
                    _syncStatusLabel.color = UIStyles.StatusWarning;
                }
                else if (hasServerUpdate)
                {
                    int serverLines = TranslatorUIManager.PendingUpdateInfo?.LineCount ?? 0;
                    _syncStatusLabel.text = $"OUT OF SYNC - Server has update ({serverLines} lines)";
                    _syncStatusLabel.color = UIStyles.StatusWarning;
                }
                else
                {
                    _syncStatusLabel.text = "SYNCED with server";
                    _syncStatusLabel.color = UIStyles.StatusSuccess;
                }
            }
            else
            {
                // Not on server - clear role label
                _roleLabel.text = "";

                if (serverState != null && serverState.Checked)
                {
                    _sourceLabel.text = "Source: Local only (not on server)";
                    _syncStatusLabel.text = $"All {entryCount} entries are local";
                    _syncStatusLabel.color = UIStyles.TextMuted;
                }
                else if (!TranslatorCore.Config.online_mode)
                {
                    _sourceLabel.text = "Source: Local (offline mode)";
                    _syncStatusLabel.text = "";
                }
                else if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
                {
                    // Online mode but not logged in - can't check server state
                    _sourceLabel.text = "Source: Local (login to sync)";
                    _syncStatusLabel.text = "";
                }
                else
                {
                    _sourceLabel.text = "Source: Local (checking...)";
                    _syncStatusLabel.text = "";
                }
            }

            // Ollama status
            if (TranslatorCore.Config.enable_ollama)
            {
                int queueCount = TranslatorCore.QueueCount;
                _ollamaStatusLabel.text = queueCount > 0 ? $"Ollama: {queueCount} in queue" : "Ollama: Ready";
            }
            else
            {
                _ollamaStatusLabel.text = "";
            }
        }

        private void RefreshActionsSection()
        {
            if (_uploadBtn == null) return;

            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            var state = TranslatorCore.ServerState;
            bool existsOnServer = state != null && state.Exists && state.SiteId.HasValue;
            bool hasLocalChanges = TranslatorCore.LocalChangesCount > 0;

            // Check if we're in sync (no local changes and hash matches)
            bool isInSync = existsOnServer && state.IsOwner && !hasLocalChanges &&
                           !string.IsNullOrEmpty(state.Hash) &&
                           state.Hash == TranslatorCore.LastSyncedHash;

            // Determine upload action text
            string uploadAction;
            string uploadHint;

            // Check for merge conflict first (highest priority action)
            bool needsMerge = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Merge;

            if (needsMerge)
            {
                // Merge needed - show merge button with clear explanation
                uploadAction = "Merge Translation";
                uploadHint = $"Conflict detected! You have {TranslatorCore.LocalChangesCount} local changes AND server was updated. Click to resolve.";
            }
            else if (isInSync)
            {
                // In sync - no need to show upload button
                uploadAction = "Up to date";
                uploadHint = "Your translation is synchronized with the server";
            }
            else if (existsOnServer && state.IsOwner)
            {
                uploadAction = "Update Translation";
                uploadHint = hasLocalChanges
                    ? $"Update #{state.SiteId} ({TranslatorCore.LocalChangesCount} local changes)"
                    : $"Update your translation #{state.SiteId}";
            }
            else if (existsOnServer && !state.IsOwner)
            {
                uploadAction = "Contribute";
                uploadHint = $"Contribute as a branch to @{state.Uploader}'s translation";
            }
            else
            {
                uploadAction = "Upload Translation";
                uploadHint = "Create a new translation";
            }

            _uploadBtn.ButtonText.text = uploadAction;

            // Enable/disable based on conditions
            // Disable if in sync (nothing to upload) or other conditions not met
            bool canUpload = isLoggedIn && TranslatorCore.Config.online_mode &&
                            TranslatorCore.TranslationCache.Count > 0 && !isInSync;
            _uploadBtn.Component.interactable = canUpload;

            // Update hint
            if (!TranslatorCore.Config.online_mode)
            {
                _uploadHintLabel.text = "Offline mode - upload disabled";
            }
            else if (!isLoggedIn)
            {
                _uploadHintLabel.text = "Login required";
            }
            else if (TranslatorCore.TranslationCache.Count == 0)
            {
                _uploadHintLabel.text = "No translations to upload";
            }
            else
            {
                _uploadHintLabel.text = uploadHint;
            }

            // Role-specific buttons visibility
            if (_reviewOnWebsiteBtn != null && _compareWithServerBtn != null && _forkBtn != null)
            {
                bool isMain = existsOnServer && state.Role == TranslationRole.Main;
                bool isBranch = existsOnServer && state.Role == TranslationRole.Branch;
                bool hasBranches = state != null && state.BranchesCount > 0;

                // Review Branches - only for Main role when there are branches to review
                _reviewOnWebsiteBtn.Component.gameObject.SetActive(isMain && hasBranches);

                // Compare with Server - for Main or Branch when there are local changes
                _compareWithServerBtn.Component.gameObject.SetActive(existsOnServer && hasLocalChanges);
                // Compare button enabled only when logged in
                if (existsOnServer && hasLocalChanges)
                {
                    _compareWithServerBtn.Component.interactable = isLoggedIn;
                }

                // Fork button - only for Branch role
                _forkBtn.Component.gameObject.SetActive(isBranch);

                // Fork button enabled only when logged in
                if (isBranch)
                {
                    _forkBtn.Component.interactable = isLoggedIn;
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
                        TranslatorCore.Config.api_token = null;
                        TranslatorCore.Config.api_user = null;
                        TranslatorCore.Config.api_token_server = null;
                        TranslatorCore.SaveConfig();
                        ApiClient.SetAuthToken(null);
                        RefreshUI();
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
            Application.OpenURL(url);
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
                _downloadLatestBtn.ButtonText.text = "Downloading...";
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
                        _downloadLatestBtn.ButtonText.text = "Download Latest";
                    }
                });
            }
            catch (System.Exception e)
            {
                TranslatorCore.LogWarning($"[MainPanel] Download error: {e.Message}");
                if (_downloadLatestBtn != null)
                {
                    _downloadLatestBtn.Component.interactable = true;
                    _downloadLatestBtn.ButtonText.text = "Download Latest";
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
                $"This will create a new independent translation with a new lineage.\n\n" +
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
                _compareWithServerBtn.ButtonText.text = "Loading...";
            }

            // Capture values for closure
            var siteId = serverState.SiteId.Value;

            try
            {
                // Call API to init merge preview with local content
                var result = await ApiClient.InitMergePreview(
                    siteId,
                    TranslatorCore.TranslationCache
                );

                // After await, we may be on a background thread (IL2CPP issue)
                var success = result.Success;
                var url = result.Url;
                var error = result.Error;

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success && !string.IsNullOrEmpty(url))
                    {
                        string fullUrl = ApiClient.GetMergePreviewFullUrl(url);
                        TranslatorCore.LogInfo($"[MainPanel] Opening compare page: {fullUrl}");
                        Application.OpenURL(fullUrl);
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[MainPanel] Failed to init merge preview: {error}");
                        // Could show a toast/notification here
                    }

                    // Re-enable button
                    if (_compareWithServerBtn != null)
                    {
                        _compareWithServerBtn.Component.interactable = true;
                        _compareWithServerBtn.ButtonText.text = "Compare";
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
                        _compareWithServerBtn.ButtonText.text = "Compare";
                    }
                });
            }
        }

        private void RefreshCommunitySection()
        {
            if (_communitySection == null) return;

            // Hide section if offline mode
            bool showSection = TranslatorCore.Config.online_mode;
            _communitySection.SetActive(showSection);

            if (!showSection) return;

            // Update game label
            var game = TranslatorCore.CurrentGame;
            if (game != null && !string.IsNullOrEmpty(game.name))
            {
                _communityGameLabel.text = $"Game: {game.name}";
                _searchBtn.Component.interactable = true;
            }
            else
            {
                _communityGameLabel.text = "Game: Not detected";
                _searchBtn.Component.interactable = false;
            }

            // Refresh list display (e.g., after login status change)
            _translationList?.Refresh();
        }

        private async void OnSearchCommunityClicked()
        {
            var game = TranslatorCore.CurrentGame;
            if (game == null)
            {
                _translationList.SetStatus("No game detected", UIStyles.StatusWarning);
                return;
            }

            if (_translationList.IsSearching) return;

            string targetLang = TranslatorCore.Config.GetTargetLanguage();
            await _translationList.SearchAsync(game.steam_id, game.name, targetLang);

            // After await, we may be on a background thread (IL2CPP issue)
            TranslatorUIManager.RunOnMainThread(() =>
            {
                // Enable download button if results found
                _downloadBtn.Component.interactable = _translationList.SelectedTranslation != null;
            });
        }

        private async void OnDownloadCommunityClicked()
        {
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
                    $"You are about to download a different translation lineage.\n\n" +
                    $"Your current translation ({localCount} entries) will be replaced with @{selectedTranslation.Uploader}'s translation.\n\n" +
                    "This is a different lineage - you will lose your current translation history.\n\n" +
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
