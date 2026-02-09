using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Corner notification overlay showing mod updates, sync status, and Ollama queue.
    /// Displays when no main panels are open.
    /// </summary>
    public class StatusOverlay : TranslatorPanelBase
    {
        public override string Name => "StatusOverlay";
        public override int MinWidth => 350;
        public override int MinHeight => 50;
        public override int PanelWidth => 350;
        public override int PanelHeight => 180;

        // Override anchors to position in top-right corner
        public override Vector2 DefaultAnchorMin => new(1f, 1f);
        public override Vector2 DefaultAnchorMax => new(1f, 1f);

        // We don't want drag/resize for this overlay
        public override bool CanDragAndResize => false;

        // StatusOverlay should NOT dim the screen
        protected override bool UseBackdrop => false;

        // StatusOverlay has fixed size (not dynamic) and no persistence
        protected override int MinPanelHeight => 50;
        protected override bool UseDynamicSizing => false;
        protected override bool PersistWindowPreferences => false;

        // StatusOverlay uses top-right corner anchors, not center
        protected override bool UsesCenterAnchors => false;

        // UI elements - Mod update notification
        private GameObject _modUpdateBox;
        private Text _modUpdateLabel;
        private ButtonRef _modUpdateBtn;
        private ButtonRef _modIgnoreBtn;

        // UI elements - Translation sync notification
        private GameObject _syncBox;
        private Text _syncLabel;
        private ButtonRef _syncBranchBtn;    // Branch option (contribute, green)
        private ButtonRef _syncForkBtn;      // Fork option (independent, red)
        private ButtonRef _syncActionBtn;    // Generic action (Download/Update/Merge)
        private ButtonRef _syncSettingsBtn;
        private ButtonRef _syncIgnoreBtn;

        // UI elements - Ollama queue status
        private GameObject _ollamaBox;
        private Text _ollamaStatusLabel;
        private Text _ollamaQueueLabel;

        // UI elements - SSE connection indicator
        private GameObject _connectionBox;
        private Text _connectionLabel;

        // State - whether main panels are open (affects which boxes are shown)
        private bool _panelsOpenMode = false;

        public StatusOverlay(UIBase owner) : base(owner)
        {
        }

        /// <summary>
        /// Set whether panels are currently open. When true, only Ollama queue is shown.
        /// </summary>
        public void SetPanelsOpenMode(bool panelsOpen)
        {
            _panelsOpenMode = panelsOpen;
        }

        /// <summary>
        /// Returns true if there's notification content (mod update or sync) to display.
        /// Does NOT include Ollama queue (which is handled separately).
        /// </summary>
        public bool HasNotificationContent()
        {
            // Mod update notification
            bool showModUpdate = TranslatorUIManager.HasModUpdate && !TranslatorUIManager.ModUpdateDismissed;

            // Translation sync notification
            var serverState = TranslatorCore.ServerState;
            bool existsOnServer = serverState != null && serverState.Exists && serverState.SiteId.HasValue;
            bool hasLocalChanges = existsOnServer && TranslatorCore.LocalChangesCount > 0;
            bool hasServerUpdate = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Download;
            bool needsMerge = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Merge;
            bool showSyncNotification = (hasLocalChanges || hasServerUpdate || needsMerge) &&
                !TranslatorUIManager.NotificationDismissed;

            return showModUpdate || showSyncNotification;
        }

        public override void SetDefaultSizeAndPosition()
        {
            // Position in top-right corner with padding
            Rect.anchorMin = new Vector2(1f, 1f);
            Rect.anchorMax = new Vector2(1f, 1f);
            Rect.pivot = new Vector2(1f, 1f);
            Rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            Rect.anchoredPosition = new Vector2(-10, -10); // 10px padding from corner

            EnsureValidPosition();
        }

        protected override void ConstructPanelContent()
        {
            // Remove default title bar for this overlay
            TitleBar?.gameObject.SetActive(false);

            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(ContentRoot, false, false, true, true, 5, 5, 5, 5, 5);

            // Mod Update Notification Box
            CreateModUpdateBox();

            // Translation Sync Notification Box
            CreateSyncBox();

            // Ollama Queue Status Box
            CreateOllamaBox();

            // SSE Connection Indicator
            CreateConnectionBox();

            // Start hidden and with update
            RefreshOverlay();
        }

        private void CreateModUpdateBox()
        {
            _modUpdateBox = UIFactory.CreateVerticalGroup(ContentRoot, "ModUpdateBox", false, false, true, true, 5);
            UIFactory.SetLayoutElement(_modUpdateBox, minHeight: UIStyles.NotificationBoxHeight, flexibleWidth: 9999);
            SetBackgroundColor(_modUpdateBox, UIStyles.NotificationSuccess);

            var padding = _modUpdateBox.GetComponent<VerticalLayoutGroup>();
            if (padding != null)
            {
                padding.padding = new RectOffset(8, 8, 5, 5);
            }

            _modUpdateLabel = UIFactory.CreateLabel(_modUpdateBox, "ModUpdateLabel", "Mod update available: v?.?.?", TextAnchor.MiddleLeft);
            _modUpdateLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_modUpdateLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(_modUpdateLabel);

            var btnRow = UIStyles.CreateFormRow(_modUpdateBox, "ModBtnRow", UIStyles.RowHeightMedium, 5);

            _modUpdateBtn = UIFactory.CreateButton(btnRow, "ModDownloadBtn", "Download");
            UIFactory.SetLayoutElement(_modUpdateBtn.Component.gameObject, minWidth: 80, minHeight: UIStyles.RowHeightNormal);
            _modUpdateBtn.OnClick += OnModUpdateClicked;
            RegisterUIText(_modUpdateBtn.ButtonText);

            _modIgnoreBtn = UIFactory.CreateButton(btnRow, "ModIgnoreBtn", "Ignore");
            UIFactory.SetLayoutElement(_modIgnoreBtn.Component.gameObject, minWidth: 60, minHeight: UIStyles.RowHeightNormal);
            _modIgnoreBtn.OnClick += OnModIgnoreClicked;
            RegisterUIText(_modIgnoreBtn.ButtonText);

            _modUpdateBox.SetActive(false);
        }

        private void CreateSyncBox()
        {
            _syncBox = UIFactory.CreateVerticalGroup(ContentRoot, "SyncBox", false, false, true, true, 5);
            UIFactory.SetLayoutElement(_syncBox, minHeight: UIStyles.NotificationBoxHeight, flexibleWidth: 9999);
            SetBackgroundColor(_syncBox, UIStyles.NotificationWarning);

            var padding = _syncBox.GetComponent<VerticalLayoutGroup>();
            if (padding != null)
            {
                padding.padding = new RectOffset(8, 8, 5, 5);
            }

            _syncLabel = UIFactory.CreateLabel(_syncBox, "SyncLabel", "Sync status", TextAnchor.MiddleLeft);
            _syncLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_syncLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(_syncLabel);

            var syncBtnRow = UIStyles.CreateFormRow(_syncBox, "SyncBtnRow", UIStyles.RowHeightMedium, 3);

            // Branch button (green) - contribute to main, shown for non-owners with local changes
            _syncBranchBtn = UIFactory.CreateButton(syncBtnRow, "SyncBranchBtn", "Branch");
            UIFactory.SetLayoutElement(_syncBranchBtn.Component.gameObject, minWidth: 65, minHeight: UIStyles.RowHeightNormal);
            UIStyles.SetBackground(_syncBranchBtn.Component.gameObject, UIStyles.ButtonSuccess);
            _syncBranchBtn.OnClick += OnSyncBranchClicked;
            RegisterUIText(_syncBranchBtn.ButtonText);

            // Fork button (red) - create independent copy, shown for non-owners with local changes
            _syncForkBtn = UIFactory.CreateButton(syncBtnRow, "SyncForkBtn", "Fork");
            UIFactory.SetLayoutElement(_syncForkBtn.Component.gameObject, minWidth: 55, minHeight: UIStyles.RowHeightNormal);
            UIStyles.SetBackground(_syncForkBtn.Component.gameObject, UIStyles.ButtonDanger);
            _syncForkBtn.OnClick += OnSyncForkClicked;
            RegisterUIText(_syncForkBtn.ButtonText);

            // Generic action button (Download/Update/Merge) - for other scenarios
            _syncActionBtn = UIFactory.CreateButton(syncBtnRow, "SyncActionBtn", "Action");
            UIFactory.SetLayoutElement(_syncActionBtn.Component.gameObject, minWidth: 75, minHeight: UIStyles.RowHeightNormal);
            _syncActionBtn.OnClick += OnSyncActionClicked;
            RegisterUIText(_syncActionBtn.ButtonText);

            // Settings button
            _syncSettingsBtn = UIFactory.CreateButton(syncBtnRow, "SyncSettingsBtn", "Settings");
            UIFactory.SetLayoutElement(_syncSettingsBtn.Component.gameObject, minWidth: 65, minHeight: UIStyles.RowHeightNormal);
            _syncSettingsBtn.OnClick += OnSyncSettingsClicked;
            RegisterUIText(_syncSettingsBtn.ButtonText);

            // Ignore button (last)
            _syncIgnoreBtn = UIFactory.CreateButton(syncBtnRow, "SyncIgnoreBtn", "Ignore");
            UIFactory.SetLayoutElement(_syncIgnoreBtn.Component.gameObject, minWidth: 55, minHeight: UIStyles.RowHeightNormal);
            _syncIgnoreBtn.OnClick += OnSyncIgnoreClicked;
            RegisterUIText(_syncIgnoreBtn.ButtonText);

            _syncBox.SetActive(false);
        }

        private void CreateOllamaBox()
        {
            _ollamaBox = UIFactory.CreateVerticalGroup(ContentRoot, "OllamaBox", false, false, true, true, 3);
            UIFactory.SetLayoutElement(_ollamaBox, minHeight: UIStyles.MultiLineSmall, flexibleWidth: 9999);
            SetBackgroundColor(_ollamaBox, UIStyles.NotificationInfo);

            var padding = _ollamaBox.GetComponent<VerticalLayoutGroup>();
            if (padding != null)
            {
                padding.padding = new RectOffset(8, 8, 5, 5);
            }

            _ollamaStatusLabel = UIFactory.CreateLabel(_ollamaBox, "OllamaStatusLabel", "Translating...", TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(_ollamaStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            _ollamaQueueLabel = UIFactory.CreateLabel(_ollamaBox, "OllamaQueueLabel", "Queue: 0 pending", TextAnchor.MiddleLeft);
            _ollamaQueueLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_ollamaQueueLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            // Exclude dynamic status labels from translation (they contain truncated game text!)
            RegisterExcluded(_ollamaStatusLabel);
            RegisterExcluded(_ollamaQueueLabel);

            _ollamaBox.SetActive(false);
        }

        private void CreateConnectionBox()
        {
            _connectionBox = UIFactory.CreateHorizontalGroup(ContentRoot, "ConnectionBox", false, false, true, true, 5,
                new Vector4(8, 8, 3, 3), Color.clear, TextAnchor.MiddleRight);
            UIFactory.SetLayoutElement(_connectionBox, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

            _connectionLabel = UIFactory.CreateLabel(_connectionBox, "ConnectionLabel", "", TextAnchor.MiddleRight);
            _connectionLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_connectionLabel.gameObject, flexibleWidth: 9999);
            RegisterExcluded(_connectionLabel);

            _connectionBox.SetActive(false);
        }

        /// <summary>
        /// Returns true if the overlay has any content to display.
        /// Used by TranslatorUIManager to decide whether to show the overlay.
        /// </summary>
        public bool HasContentToShow()
        {
            // 1. Mod update notification
            bool showModUpdate = TranslatorUIManager.HasModUpdate && !TranslatorUIManager.ModUpdateDismissed;

            // 2. Translation sync notification
            var serverState = TranslatorCore.ServerState;
            bool existsOnServer = serverState != null && serverState.Exists && serverState.SiteId.HasValue;
            bool hasLocalChanges = existsOnServer && TranslatorCore.LocalChangesCount > 0;
            bool hasServerUpdate = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Download;
            bool needsMerge = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Merge;
            bool showSyncNotification = (hasLocalChanges || hasServerUpdate || needsMerge) &&
                !TranslatorUIManager.NotificationDismissed;

            // 3. Ollama queue status
            bool ollamaEnabled = TranslatorCore.Config.enable_ollama;
            int queueCount = TranslatorCore.QueueCount;
            bool isTranslating = TranslatorCore.IsTranslating;
            bool showOllama = ollamaEnabled && (queueCount > 0 || isTranslating);

            return showModUpdate || showSyncNotification || showOllama;
        }

        /// <summary>
        /// Call this periodically to update the overlay visibility and content.
        /// </summary>
        public void RefreshOverlay()
        {
            // When panels are open, only show Ollama queue (mod update & sync are in MainPanel)
            // When panels are closed, show all notifications

            // 1. Mod update notification (hidden when panels open - shown in MainPanel instead)
            bool showModUpdate = !_panelsOpenMode &&
                                 TranslatorUIManager.HasModUpdate &&
                                 !TranslatorUIManager.ModUpdateDismissed;
            if (showModUpdate && _modUpdateBox != null)
            {
                _modUpdateBox.SetActive(true);
                var info = TranslatorUIManager.ModUpdateInfo;
                _modUpdateLabel.text = $"Mod update available: v{info?.LatestVersion ?? "?"}";

                // Show appropriate button
                bool hasDirectDownload = !string.IsNullOrEmpty(info?.DownloadUrl);
                _modUpdateBtn.ButtonText.text = hasDirectDownload ? "Download" : "View Release";
            }
            else
            {
                _modUpdateBox?.SetActive(false);
            }

            // 2. Translation sync notification (hidden when panels open - shown in MainPanel instead)
            var serverState = TranslatorCore.ServerState;
            bool existsOnServer = serverState != null && serverState.Exists && serverState.SiteId.HasValue;
            bool hasLocalChanges = existsOnServer && TranslatorCore.LocalChangesCount > 0;
            bool hasServerUpdate = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Download;
            bool needsMerge = TranslatorUIManager.HasPendingUpdate &&
                TranslatorUIManager.PendingUpdateDirection == UpdateDirection.Merge;

            bool showSyncNotification = !_panelsOpenMode &&
                                        (hasLocalChanges || hasServerUpdate || needsMerge) &&
                                        !TranslatorUIManager.NotificationDismissed;

            if (showSyncNotification && _syncBox != null)
            {
                _syncBox.SetActive(true);

                // Determine message and button visibility based on context
                string message;
                var direction = TranslatorUIManager.PendingUpdateDirection;

                // Get role for role-specific messages
                bool isBranch = serverState?.Role == TranslationRole.Branch;
                bool isOwner = serverState?.IsOwner == true;

                // Default: hide Branch/Fork buttons, show Action button
                bool showBranchFork = false;
                bool showAction = true;
                string actionText = "Sync";

                // Get owner name for context
                string ownerName = serverState?.Uploader ?? "owner";

                if (needsMerge)
                {
                    if (isOwner)
                    {
                        // Owner: standard merge message
                        message = "Conflict: Both local and server changed!";
                    }
                    else
                    {
                        // Non-owner: after merge, they'll need to choose Branch/Fork
                        message = $"Conflict with @{ownerName}'s update! Merge first.";
                    }
                    actionText = "Merge";
                }
                else if (hasServerUpdate)
                {
                    if (isBranch)
                    {
                        // Branch owner: parent (Main) has been updated
                        message = "Parent translation update available!";
                    }
                    else if (isOwner)
                    {
                        // Main owner: server has update (multi-device scenario)
                        message = "Server update available!";
                    }
                    else
                    {
                        // Non-owner: the Main they downloaded from has been updated
                        message = $"@{ownerName} updated the translation!";
                    }
                    actionText = "Download";
                }
                else if (hasLocalChanges)
                {
                    if (isOwner)
                    {
                        // Owner: show Update button
                        message = $"You have {TranslatorCore.LocalChangesCount} local changes to upload!";
                        actionText = "Update";
                    }
                    else
                    {
                        // Non-owner: show Branch AND Fork options
                        // User must choose to contribute (branch) or go independent (fork)
                        message = $"You have {TranslatorCore.LocalChangesCount} local changes!";
                        showBranchFork = true;
                        showAction = false;
                    }
                }
                else
                {
                    // Fallback for edge case (shouldn't happen with current logic)
                    message = $"{TranslatorCore.LocalChangesCount} local changes";
                    actionText = "Sync";
                }

                _syncLabel.text = message;

                // Show/hide buttons based on context
                _syncBranchBtn?.Component.gameObject.SetActive(showBranchFork);
                _syncForkBtn?.Component.gameObject.SetActive(showBranchFork);
                _syncActionBtn?.Component.gameObject.SetActive(showAction);

                if (showAction)
                {
                    _syncActionBtn.ButtonText.text = actionText;
                }
            }
            else
            {
                _syncBox?.SetActive(false);
            }

            // 3. Ollama queue status
            bool ollamaEnabled = TranslatorCore.Config.enable_ollama;
            int queueCount = TranslatorCore.QueueCount;
            bool isTranslating = TranslatorCore.IsTranslating;
            bool showOllama = ollamaEnabled && (queueCount > 0 || isTranslating);

            if (showOllama && _ollamaBox != null)
            {
                _ollamaBox.SetActive(true);

                if (isTranslating)
                {
                    string text = TranslatorCore.CurrentText ?? "";
                    if (text.Length > 25) text = text.Substring(0, 25) + "...";
                    _ollamaStatusLabel.text = $"Translating: {text}";
                    _ollamaStatusLabel.gameObject.SetActive(true);
                }
                else
                {
                    _ollamaStatusLabel.gameObject.SetActive(false);
                }

                if (queueCount > 0)
                {
                    _ollamaQueueLabel.text = $"Queue: {queueCount} pending";
                    _ollamaQueueLabel.gameObject.SetActive(true);
                }
                else
                {
                    _ollamaQueueLabel.gameObject.SetActive(false);
                }
            }
            else
            {
                _ollamaBox?.SetActive(false);
            }

            // 4. SSE Connection indicator (compact, shown when overlay is visible)
            bool showConnection = false;
            if (TranslatorCore.Config.online_mode && !string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                var connState = TranslatorUIManager.SyncConnectionState;
                switch (connState)
                {
                    case SseConnectionState.Connected:
                        showConnection = true;
                        if (_connectionLabel != null)
                        {
                            _connectionLabel.text = "Connected";
                            _connectionLabel.color = UIStyles.StatusSuccess;
                        }
                        break;
                    case SseConnectionState.Connecting:
                        showConnection = true;
                        if (_connectionLabel != null)
                        {
                            _connectionLabel.text = "Connecting...";
                            _connectionLabel.color = UIStyles.StatusWarning;
                        }
                        break;
                    case SseConnectionState.Reconnecting:
                        showConnection = true;
                        if (_connectionLabel != null)
                        {
                            _connectionLabel.text = "Reconnecting...";
                            _connectionLabel.color = UIStyles.StatusWarning;
                        }
                        break;
                    default:
                        showConnection = false;
                        break;
                }
            }
            _connectionBox?.SetActive(showConnection);

            // Adjust panel height based on visible content
            AdjustHeight();
        }

        private void AdjustHeight()
        {
            int height = 10; // padding
            if (_modUpdateBox != null && _modUpdateBox.activeSelf) height += 60;
            if (_syncBox != null && _syncBox.activeSelf) height += 60;
            if (_ollamaBox != null && _ollamaBox.activeSelf) height += 50;
            if (_connectionBox != null && _connectionBox.activeSelf) height += 20;

            Rect.sizeDelta = new Vector2(PanelWidth, Mathf.Max(50, height));
        }

        #region Button Handlers

        private void OnModUpdateClicked()
        {
            var info = TranslatorUIManager.ModUpdateInfo;
            string url = info?.DownloadUrl ?? info?.ReleaseUrl;
            if (!string.IsNullOrEmpty(url))
            {
                Application.OpenURL(url);
            }
        }

        private void OnModIgnoreClicked()
        {
            TranslatorUIManager.ModUpdateDismissed = true;
            RefreshOverlay();
        }

        private void OnSyncActionClicked()
        {
            var direction = TranslatorUIManager.PendingUpdateDirection;

            switch (direction)
            {
                case UpdateDirection.Upload:
                    TranslatorUIManager.UploadPanel?.SetActive(true);
                    break;

                case UpdateDirection.Download:
                    // Download and apply update
                    ApplyPendingUpdate();
                    break;

                case UpdateDirection.Merge:
                    // Start merge flow
                    TranslatorUIManager.MergePanel?.SetActive(true);
                    break;

                default:
                    // Default to upload if we have local changes
                    if (TranslatorCore.LocalChangesCount > 0)
                    {
                        TranslatorUIManager.UploadPanel?.SetActive(true);
                    }
                    break;
            }
        }

        private async void ApplyPendingUpdate()
        {
            var serverState = TranslatorCore.ServerState;
            if (serverState?.SiteId == null)
            {
                TranslatorCore.LogError("[StatusOverlay] No server translation to download");
                TranslatorUIManager.ShowMain();
                return;
            }

            // Use centralized methods
            if (TranslatorCore.LocalChangesCount > 0)
            {
                // Need merge - use centralized merge flow
                await TranslatorUIManager.DownloadForMerge();
            }
            else
            {
                // Simple download - use centralized download
                await TranslatorUIManager.DownloadUpdate();
            }

            // After await, we may be on a background thread (IL2CPP issue)
            TranslatorUIManager.RunOnMainThread(() =>
            {
                RefreshOverlay();
            });
        }

        private void OnSyncIgnoreClicked()
        {
            TranslatorUIManager.NotificationDismissed = true;
            RefreshOverlay();
        }

        private void OnSyncSettingsClicked()
        {
            TranslatorUIManager.ShowMain();
        }

        /// <summary>
        /// Handler for Branch button - contribute to the main translation.
        /// Opens upload panel which will create a branch.
        /// </summary>
        private void OnSyncBranchClicked()
        {
            // Open upload panel - it will detect we're contributing and handle branch creation
            TranslatorUIManager.UploadPanel?.SetActive(true);
        }

        /// <summary>
        /// Handler for Fork button - create independent copy with new UUID.
        /// Shows warning dialog before proceeding.
        /// </summary>
        private void OnSyncForkClicked()
        {
            var serverState = TranslatorCore.ServerState;
            string ownerName = serverState?.Uploader ?? "the original owner";

            // Show warning dialog before forking (destructive action)
            TranslatorUIManager.ConfirmationPanel?.Show(
                "Create Independent Fork?",
                $"This will create a new independent translation with a new lineage.\n\n" +
                $"You will become the owner of this new translation.\n\n" +
                $"You will no longer be able to contribute to @{ownerName}'s translation.\n\n" +
                "This action cannot be undone.",
                "Fork",
                () =>
                {
                    // Create fork: generate new UUID and reset server state
                    TranslatorCore.CreateFork();

                    // Open upload panel to push the forked translation
                    TranslatorUIManager.UploadPanel?.SetActive(true);

                    RefreshOverlay();
                },
                isDanger: true
            );
        }

        #endregion
    }
}
