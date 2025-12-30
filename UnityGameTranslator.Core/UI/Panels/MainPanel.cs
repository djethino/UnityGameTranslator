using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Main settings panel. Shows translation status, account info, sync status, and action buttons.
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

        // UI references - Translation info section
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

            var title = CreateTitle(card, "Title", "Unity Game Translator");
            RegisterExcluded(title); // Mod name - never translate

            UIStyles.CreateSpacer(card, 5);

            // Account Section
            CreateAccountSection(card);

            UIStyles.CreateSpacer(card, 10);

            // Current Translation Info Section
            CreateTranslationInfoSection(card);

            UIStyles.CreateSpacer(card, 10);

            // Community Translations Section (online mode only)
            CreateCommunitySection(card);

            UIStyles.CreateSpacer(card, 10);

            // Actions Section
            CreateActionsSection(card);

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

        private void CreateTranslationInfoSection(GameObject parent)
        {
            var sectionTitle = UIStyles.CreateSectionTitle(parent, "TranslationSectionLabel", "Current Translation");
            RegisterUIText(sectionTitle);

            var infoBox = CreateSection(parent, "TranslationBox");

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

        private void CreateCommunitySection(GameObject parent)
        {
            // Wrap entire section to toggle visibility based on online mode
            _communitySection = UIFactory.CreateVerticalGroup(parent, "CommunitySection", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_communitySection, flexibleWidth: 9999);

            var sectionTitle = UIStyles.CreateSectionTitle(_communitySection, "CommunitySectionLabel", "Community Translations");
            RegisterUIText(sectionTitle);

            var communityBox = CreateSection(_communitySection, "CommunityBox");

            // Game info and search row
            var searchRow = UIStyles.CreateFormRow(communityBox, "SearchRow", UIStyles.RowHeightLarge);

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
            _translationList.CreateUI(communityBox, 100, onSelectionChanged: (t) =>
            {
                if (_downloadBtn != null)
                {
                    _downloadBtn.Component.interactable = t != null;
                }
            });

            UIStyles.CreateSpacer(communityBox, 5);

            // Download button
            var downloadRow = UIStyles.CreateFormRow(communityBox, "DownloadRow", UIStyles.RowHeightLarge, 0);
            var layoutGroup = downloadRow.GetComponent<HorizontalLayoutGroup>();
            if (layoutGroup != null) layoutGroup.childAlignment = TextAnchor.MiddleCenter; // Center the button

            _downloadBtn = CreatePrimaryButton(downloadRow, "DownloadBtn", "Download Selected", 160);
            UIStyles.SetBackground(_downloadBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _downloadBtn.OnClick += OnDownloadCommunityClicked;
            _downloadBtn.Component.interactable = false;
            RegisterUIText(_downloadBtn.ButtonText);
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

        public void RefreshUI()
        {
            RefreshAccountSection();
            RefreshTranslationInfo();
            RefreshCommunitySection();
            RefreshActionsSection();
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
                uploadAction = "Fork Translation";
                uploadHint = $"Create a fork from {state.Uploader}'s translation";
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

            try
            {
                // Call API to init merge preview with local content
                var result = await ApiClient.InitMergePreview(
                    serverState.SiteId.Value,
                    TranslatorCore.TranslationCache
                );

                if (result.Success && !string.IsNullOrEmpty(result.Url))
                {
                    string fullUrl = ApiClient.GetMergePreviewFullUrl(result.Url);
                    TranslatorCore.LogInfo($"[MainPanel] Opening compare page: {fullUrl}");
                    Application.OpenURL(fullUrl);
                }
                else
                {
                    TranslatorCore.LogWarning($"[MainPanel] Failed to init merge preview: {result.Error}");
                    // Could show a toast/notification here
                }
            }
            catch (System.Exception e)
            {
                TranslatorCore.LogWarning($"[MainPanel] Compare error: {e.Message}");
            }
            finally
            {
                // Re-enable button
                if (_compareWithServerBtn != null)
                {
                    _compareWithServerBtn.Component.interactable = true;
                    _compareWithServerBtn.ButtonText.text = "Compare";
                }
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

            // Enable download button if results found
            _downloadBtn.Component.interactable = _translationList.SelectedTranslation != null;
        }

        private async void OnDownloadCommunityClicked()
        {
            var selectedTranslation = _translationList?.SelectedTranslation;
            if (selectedTranslation == null) return;

            // Check if user has local changes that would be lost
            int localChanges = TranslatorCore.LocalChangesCount;
            if (localChanges > 0)
            {
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
