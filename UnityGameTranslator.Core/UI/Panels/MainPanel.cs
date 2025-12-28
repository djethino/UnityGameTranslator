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
        private Text _syncStatusLabel;
        private Text _ollamaStatusLabel;

        // UI references - Actions section
        private ButtonRef _uploadBtn;
        private Text _uploadHintLabel;

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

            CreateTitle(card, "Title", "Unity Game Translator");

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

            var closeBtn = CreatePrimaryButton(buttonRow, "CloseBtn", "Close");
            closeBtn.OnClick += () => SetActive(false);

            RefreshUI();
        }

        private void CreateAccountSection(GameObject parent)
        {
            UIStyles.CreateSectionTitle(parent, "AccountSectionLabel", "Account");

            var accountBox = CreateSection(parent, "AccountBox");

            var accountRow = UIStyles.CreateFormRow(accountBox, "AccountRow", UIStyles.RowHeightLarge);

            _accountLabel = UIFactory.CreateLabel(accountRow, "AccountLabel", "Not connected", TextAnchor.MiddleLeft);
            _accountLabel.fontStyle = FontStyle.Italic;
            _accountLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_accountLabel.gameObject, flexibleWidth: 9999);

            _loginLogoutBtn = CreateSecondaryButton(accountRow, "LoginLogoutBtn", "Login", 80);
            _loginLogoutBtn.OnClick += OnLoginLogoutClicked;
        }

        private void CreateTranslationInfoSection(GameObject parent)
        {
            UIStyles.CreateSectionTitle(parent, "TranslationSectionLabel", "Current Translation");

            var infoBox = CreateSection(parent, "TranslationBox");

            _entriesLabel = UIFactory.CreateLabel(infoBox, "EntriesLabel", "Entries: 0", TextAnchor.MiddleLeft);
            _entriesLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_entriesLabel.gameObject, minHeight: UIStyles.RowHeightNormal);

            _targetLabel = UIFactory.CreateLabel(infoBox, "TargetLabel", "Target: auto", TextAnchor.MiddleLeft);
            _targetLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_targetLabel.gameObject, minHeight: UIStyles.RowHeightNormal);

            _sourceLabel = UIFactory.CreateLabel(infoBox, "SourceLabel", "Source: Local", TextAnchor.MiddleLeft);
            _sourceLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_sourceLabel.gameObject, minHeight: UIStyles.RowHeightNormal);

            _syncStatusLabel = UIFactory.CreateLabel(infoBox, "SyncStatusLabel", "", TextAnchor.MiddleLeft);
            _syncStatusLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_syncStatusLabel.gameObject, minHeight: UIStyles.RowHeightNormal);

            _ollamaStatusLabel = CreateSmallLabel(infoBox, "OllamaStatusLabel", "");
        }

        private void CreateActionsSection(GameObject parent)
        {
            UIStyles.CreateSectionTitle(parent, "ActionsSectionLabel", "Actions");

            var actionsBox = CreateSection(parent, "ActionsBox");

            _uploadBtn = CreatePrimaryButton(actionsBox, "UploadBtn", "Upload Translation", 200);
            UIFactory.SetLayoutElement(_uploadBtn.Component.gameObject, flexibleWidth: 9999);
            _uploadBtn.OnClick += OnUploadClicked;

            _uploadHintLabel = UIStyles.CreateHint(actionsBox, "UploadHintLabel", "");
        }

        private void CreateCommunitySection(GameObject parent)
        {
            // Wrap entire section to toggle visibility based on online mode
            _communitySection = UIFactory.CreateVerticalGroup(parent, "CommunitySection", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_communitySection, flexibleWidth: 9999);

            UIStyles.CreateSectionTitle(_communitySection, "CommunitySectionLabel", "Community Translations");

            var communityBox = CreateSection(_communitySection, "CommunityBox");

            // Game info and search row
            var searchRow = UIStyles.CreateFormRow(communityBox, "SearchRow", UIStyles.RowHeightLarge);

            _communityGameLabel = UIFactory.CreateLabel(searchRow, "GameLabel", "Game: Unknown", TextAnchor.MiddleLeft);
            _communityGameLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_communityGameLabel.gameObject, flexibleWidth: 9999);

            _searchBtn = CreateSecondaryButton(searchRow, "SearchBtn", "Search", 80);
            _searchBtn.OnClick += OnSearchCommunityClicked;

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
        }

        public override void SetActive(bool active)
        {
            base.SetActive(active);
            if (active)
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

            // Determine upload action text
            string uploadAction;
            string uploadHint;

            if (existsOnServer && state.IsOwner)
            {
                uploadAction = "Update Translation";
                uploadHint = $"Update your translation #{state.SiteId}";
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
            bool canUpload = isLoggedIn && TranslatorCore.Config.online_mode && TranslatorCore.TranslationCache.Count > 0;
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

        private void OnUploadClicked()
        {
            TranslatorUIManager.UploadPanel?.SetActive(true);
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
