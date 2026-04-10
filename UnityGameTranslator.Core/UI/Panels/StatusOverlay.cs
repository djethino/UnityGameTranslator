using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Corner notification overlay showing mod updates, sync status, and AI queue.
    /// Displays when no main panels are open.
    /// </summary>
    public class StatusOverlay : TranslatorPanelBase
    {
        public override string Name => "StatusOverlay";
        public override int MinWidth => 350;
        public override int MinHeight => 50;
        public override int PanelWidth => 350;
        public override int PanelHeight => 180;

        // Override anchors - actual position set dynamically via ApplyPositionFromConfig()
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

        // UI elements - AI queue status
        private GameObject _aiBox;
        private Text _aiStatusLabel;
        private Text _aiQueueLabel;

        // UI elements - SSE connection indicator
        private GameObject _connectionBox;
        private Text _connectionLabel;

        // UI elements - Hotkey feedback toast (short-lived visual notification)
        // When the toast is active, the other boxes (mod update, sync, AI queue, connection)
        // are hidden to avoid confusion: the overlay becomes a single-purpose hotkey feedback.
        private GameObject _toastBox;
        private Text _toastLabel;
        private float _toastHideTime = 0f;
        private const float TOAST_DURATION = 1.8f;

        // Toast tone colors (distinct from mod update / sync / AI notifications).
        private static readonly Color ToastOnBg     = new Color(0.13f, 0.35f, 0.18f, 0.95f);  // Dark green
        private static readonly Color ToastOffBg    = new Color(0.40f, 0.15f, 0.15f, 0.95f);  // Dark red
        private static readonly Color ToastInfoBg   = new Color(0.24f, 0.14f, 0.38f, 0.95f);  // Dark purple

        // State - whether main panels are open (affects which boxes are shown)
        private bool _panelsOpenMode = false;

        public StatusOverlay(UIBase owner) : base(owner)
        {
        }

        /// <summary>
        /// Set whether panels are currently open. When true, only AI queue is shown.
        /// </summary>
        public void SetPanelsOpenMode(bool panelsOpen)
        {
            _panelsOpenMode = panelsOpen;
        }

        /// <summary>
        /// Returns true if there's notification content (mod update or sync) to display.
        /// Does NOT include AI queue (which is handled separately).
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
            Rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            ApplyPositionFromConfig();
            EnsureValidPosition();
        }

        /// <summary>
        /// Applies the notification position from config to the overlay anchors/pivot.
        /// Called at init and when the user changes the position setting.
        /// </summary>
        public void ApplyPositionFromConfig()
        {
            if (Rect == null) return;

            string position = TranslatorCore.Config?.sync?.notification_position ?? "top-right";
            float anchorX, anchorY, pivotX, pivotY, posX, posY;

            switch (position)
            {
                case "top-left":
                    anchorX = 0f; anchorY = 1f;
                    pivotX = 0f; pivotY = 1f;
                    posX = 10f; posY = -10f;
                    break;
                case "bottom-right":
                    anchorX = 1f; anchorY = 0f;
                    pivotX = 1f; pivotY = 0f;
                    posX = -10f; posY = 10f;
                    break;
                case "bottom-left":
                    anchorX = 0f; anchorY = 0f;
                    pivotX = 0f; pivotY = 0f;
                    posX = 10f; posY = 10f;
                    break;
                default: // "top-right"
                    anchorX = 1f; anchorY = 1f;
                    pivotX = 1f; pivotY = 1f;
                    posX = -10f; posY = -10f;
                    break;
            }

            Rect.anchorMin = new Vector2(anchorX, anchorY);
            Rect.anchorMax = new Vector2(anchorX, anchorY);
            Rect.pivot = new Vector2(pivotX, pivotY);
            Rect.anchoredPosition = new Vector2(posX, posY);
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

            // AI Queue Status Box
            CreateAIBox();

            // SSE Connection Indicator
            CreateConnectionBox();

            // Hotkey feedback toast
            CreateToastBox();

            // Start hidden and with update
            RefreshOverlay();
        }

        private void CreateToastBox()
        {
            _toastBox = UIFactory.CreateVerticalGroup(ContentRoot, "ToastBox", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_toastBox, minHeight: UIStyles.RowHeightLarge, flexibleWidth: 9999);
            SetBackgroundColor(_toastBox, ToastInfoBg);

            var padding = _toastBox.GetComponent<VerticalLayoutGroup>();
            if (padding != null)
            {
                padding.padding = new RectOffset(12, 12, 8, 8);
            }

            _toastLabel = UIFactory.CreateLabel(_toastBox, "ToastLabel", "", TextAnchor.MiddleCenter);
            _toastLabel.fontStyle = FontStyle.Bold;
            _toastLabel.fontSize = UIStyles.FontSizeSectionTitle;
            _toastLabel.color = Color.white;
            UIFactory.SetLayoutElement(_toastLabel.gameObject, minHeight: UIStyles.RowHeightMedium);

            _toastBox.SetActive(false);
        }

        public enum ToastTone { Info, On, Off }

        /// <summary>
        /// Shows a short-lived toast message (used for hotkey feedback).
        /// While the toast is active, all other overlay boxes are hidden so the
        /// hotkey feedback is unambiguous. The toast auto-hides after TOAST_DURATION.
        /// </summary>
        public void ShowToast(string message, ToastTone tone = ToastTone.Info)
        {
            if (_toastBox == null || _toastLabel == null) return;

            // Force overlay visibility regardless of the user's "notifications_enabled" preference:
            // an explicit hotkey action deserves immediate visual feedback.
            if (!Enabled)
            {
                SetActive(true);
            }

            // Hide all other boxes so the toast is the only thing visible.
            HideNonToastBoxes();

            // Color by tone for fast visual read (green = ON, red = OFF, purple = neutral info).
            Color bg;
            switch (tone)
            {
                case ToastTone.On:  bg = ToastOnBg; break;
                case ToastTone.Off: bg = ToastOffBg; break;
                default:             bg = ToastInfoBg; break;
            }
            SetBackgroundColor(_toastBox, bg);

            _toastLabel.text = message;
            _toastBox.SetActive(true);
            _toastHideTime = Time.realtimeSinceStartup + TOAST_DURATION;
        }

        private void HideNonToastBoxes()
        {
            if (_modUpdateBox != null) _modUpdateBox.SetActive(false);
            if (_syncBox != null) _syncBox.SetActive(false);
            if (_aiBox != null) _aiBox.SetActive(false);
            if (_connectionBox != null) _connectionBox.SetActive(false);
        }

        /// <summary>
        /// Returns true while a hotkey feedback toast is currently shown.
        /// The owning UIManager should skip RefreshOverlay() during this time
        /// so the toast stays visible without being overwritten by the regular boxes.
        /// </summary>
        public bool IsToastActive => _toastBox != null && _toastBox.activeSelf;

        /// <summary>
        /// Called from the UI update loop to expire the toast after its duration.
        /// Once expired, the other boxes are restored by the next RefreshOverlay pass.
        /// </summary>
        public void TickToast()
        {
            if (_toastBox == null || !_toastBox.activeSelf) return;
            if (Time.realtimeSinceStartup >= _toastHideTime)
            {
                _toastBox.SetActive(false);
                // Trigger a refresh so the normal boxes come back immediately.
                RefreshOverlay();
            }
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

        private void CreateAIBox()
        {
            _aiBox = UIFactory.CreateVerticalGroup(ContentRoot, "AIBox", false, false, true, true, 3);
            UIFactory.SetLayoutElement(_aiBox, minHeight: UIStyles.MultiLineSmall, flexibleWidth: 9999);
            SetBackgroundColor(_aiBox, UIStyles.NotificationInfo);

            var padding = _aiBox.GetComponent<VerticalLayoutGroup>();
            if (padding != null)
            {
                padding.padding = new RectOffset(8, 8, 5, 5);
            }

            _aiStatusLabel = UIFactory.CreateLabel(_aiBox, "AIStatusLabel", "Translating...", TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(_aiStatusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            _aiQueueLabel = UIFactory.CreateLabel(_aiBox, "AIQueueLabel", "Queue: 0 pending", TextAnchor.MiddleLeft);
            _aiQueueLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_aiQueueLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            // Exclude dynamic status labels from translation (they contain truncated game text!)
            RegisterExcluded(_aiStatusLabel);
            RegisterExcluded(_aiQueueLabel);

            _aiBox.SetActive(false);
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

            // 3. AI queue status
            bool aiEnabled = TranslatorCore.Config.IsTranslationEnabled;
            int queueCount = TranslatorCore.QueueCount;
            bool isTranslating = TranslatorCore.IsTranslating;
            bool showAI = aiEnabled && (queueCount > 0 || isTranslating);

            return showModUpdate || showSyncNotification || showAI;
        }

        /// <summary>
        /// Call this periodically to update the overlay visibility and content.
        /// </summary>
        public void RefreshOverlay()
        {
            // If a hotkey feedback toast is currently showing, don't touch the other boxes —
            // they were hidden by ShowToast on purpose. They'll come back automatically when
            // TickToast expires the toast and calls RefreshOverlay again.
            if (IsToastActive) return;

            // When panels are open, only show AI queue (mod update & sync are in MainPanel)
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
                        message = "Both local and server changed. Sync needed!";
                    }
                    else
                    {
                        message = $"@{ownerName}'s translation updated. Sync needed!";
                    }
                    actionText = "Sync";
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

            // 3. AI queue status
            bool aiEnabled = TranslatorCore.Config.IsTranslationEnabled;
            int queueCount = TranslatorCore.QueueCount;
            bool isTranslating = TranslatorCore.IsTranslating;
            bool showAI = aiEnabled && (queueCount > 0 || isTranslating);

            if (showAI && _aiBox != null)
            {
                _aiBox.SetActive(true);

                if (isTranslating)
                {
                    string text = TranslatorCore.CurrentText ?? "";
                    if (text.Length > 25) text = text.Substring(0, 25) + "...";
                    _aiStatusLabel.text = $"Translating: {text}";
                    _aiStatusLabel.gameObject.SetActive(true);
                }
                else
                {
                    _aiStatusLabel.gameObject.SetActive(false);
                }

                if (queueCount > 0)
                {
                    _aiQueueLabel.text = $"Queue: {queueCount} pending";
                    _aiQueueLabel.gameObject.SetActive(true);
                }
                else
                {
                    _aiQueueLabel.gameObject.SetActive(false);
                }
            }
            else
            {
                _aiBox?.SetActive(false);
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
            if (_aiBox != null && _aiBox.activeSelf) height += 50;
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
                    // Download remote and start 3-way merge flow
                    ApplyPendingUpdate();
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
