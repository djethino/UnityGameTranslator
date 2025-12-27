using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Main settings panel. Shows translation status, account info, sync status, and action buttons.
    /// </summary>
    public class MainPanel : TranslatorPanelBase
    {
        public override string Name => "Unity Game Translator";
        public override int MinWidth => 450;
        public override int MinHeight => 460;
        public override int PanelWidth => 450;
        public override int PanelHeight => 460;

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

        public MainPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, 450);

            // Main card - adaptive, sizes to content
            var card = CreateAdaptiveCard(scrollContent, "MainCard", 450);

            CreateTitle(card, "Title", "Unity Game Translator");

            UIStyles.CreateSpacer(card, 5);

            // Account Section
            CreateAccountSection(card);

            UIStyles.CreateSpacer(card, 10);

            // Current Translation Info Section
            CreateTranslationInfoSection(card);

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

            var accountRow = UIFactory.CreateHorizontalGroup(accountBox, "AccountRow", false, false, true, true, 10);
            UIFactory.SetLayoutElement(accountRow, minHeight: 30, flexibleWidth: 9999);

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
            UIFactory.SetLayoutElement(_entriesLabel.gameObject, minHeight: 22);

            _targetLabel = UIFactory.CreateLabel(infoBox, "TargetLabel", "Target: auto", TextAnchor.MiddleLeft);
            _targetLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_targetLabel.gameObject, minHeight: 22);

            _sourceLabel = UIFactory.CreateLabel(infoBox, "SourceLabel", "Source: Local", TextAnchor.MiddleLeft);
            _sourceLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_sourceLabel.gameObject, minHeight: 22);

            _syncStatusLabel = UIFactory.CreateLabel(infoBox, "SyncStatusLabel", "", TextAnchor.MiddleLeft);
            _syncStatusLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_syncStatusLabel.gameObject, minHeight: 22);

            _ollamaStatusLabel = UIFactory.CreateLabel(infoBox, "OllamaStatusLabel", "", TextAnchor.MiddleLeft);
            _ollamaStatusLabel.fontSize = UIStyles.FontSizeSmall;
            _ollamaStatusLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_ollamaStatusLabel.gameObject, minHeight: 20);
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
            if (_entriesLabel == null) return;

            int entryCount = TranslatorCore.TranslationCache.Count;
            string targetLang = TranslatorCore.Config.GetTargetLanguage();
            var serverState = TranslatorCore.ServerState;
            bool existsOnServer = serverState != null && serverState.Exists && serverState.SiteId.HasValue;

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
                // Logout
                TranslatorCore.Config.api_token = null;
                TranslatorCore.Config.api_user = null;
                TranslatorCore.SaveConfig();
                ApiClient.SetAuthToken(null);
                RefreshUI();
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
    }
}
