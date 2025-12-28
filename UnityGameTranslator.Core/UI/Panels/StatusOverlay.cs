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

        // UI elements - Mod update notification
        private GameObject _modUpdateBox;
        private Text _modUpdateLabel;
        private ButtonRef _modUpdateBtn;
        private ButtonRef _modIgnoreBtn;

        // UI elements - Translation sync notification
        private GameObject _syncBox;
        private Text _syncLabel;
        private ButtonRef _syncActionBtn;
        private ButtonRef _syncIgnoreBtn;
        private ButtonRef _syncSettingsBtn;

        // UI elements - Ollama queue status
        private GameObject _ollamaBox;
        private Text _ollamaStatusLabel;
        private Text _ollamaQueueLabel;

        public StatusOverlay(UIBase owner) : base(owner)
        {
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

            var btnRow = UIStyles.CreateFormRow(_modUpdateBox, "ModBtnRow", UIStyles.RowHeightMedium, 5);

            _modUpdateBtn = UIFactory.CreateButton(btnRow, "ModDownloadBtn", "Download");
            UIFactory.SetLayoutElement(_modUpdateBtn.Component.gameObject, minWidth: 80, minHeight: UIStyles.RowHeightNormal);
            _modUpdateBtn.OnClick += OnModUpdateClicked;

            _modIgnoreBtn = UIFactory.CreateButton(btnRow, "ModIgnoreBtn", "Ignore");
            UIFactory.SetLayoutElement(_modIgnoreBtn.Component.gameObject, minWidth: 60, minHeight: UIStyles.RowHeightNormal);
            _modIgnoreBtn.OnClick += OnModIgnoreClicked;

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

            var syncBtnRow = UIStyles.CreateFormRow(_syncBox, "SyncBtnRow", UIStyles.RowHeightMedium, 5);

            _syncActionBtn = UIFactory.CreateButton(syncBtnRow, "SyncActionBtn", "Action");
            UIFactory.SetLayoutElement(_syncActionBtn.Component.gameObject, minWidth: 80, minHeight: UIStyles.RowHeightNormal);
            _syncActionBtn.OnClick += OnSyncActionClicked;

            _syncIgnoreBtn = UIFactory.CreateButton(syncBtnRow, "SyncIgnoreBtn", "Ignore");
            UIFactory.SetLayoutElement(_syncIgnoreBtn.Component.gameObject, minWidth: 60, minHeight: UIStyles.RowHeightNormal);
            _syncIgnoreBtn.OnClick += OnSyncIgnoreClicked;

            _syncSettingsBtn = UIFactory.CreateButton(syncBtnRow, "SyncSettingsBtn", "Settings");
            UIFactory.SetLayoutElement(_syncSettingsBtn.Component.gameObject, minWidth: 70, minHeight: UIStyles.RowHeightNormal);
            _syncSettingsBtn.OnClick += OnSyncSettingsClicked;

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

            _ollamaBox.SetActive(false);
        }

        /// <summary>
        /// Call this periodically to update the overlay visibility and content.
        /// </summary>
        public void RefreshOverlay()
        {
            // 1. Mod update notification
            bool showModUpdate = TranslatorUIManager.HasModUpdate && !TranslatorUIManager.ModUpdateDismissed;
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

            if (showSyncNotification && _syncBox != null)
            {
                _syncBox.SetActive(true);

                // Determine message and button based on direction
                string message;
                string buttonText;
                var direction = TranslatorUIManager.PendingUpdateDirection;

                if (needsMerge)
                {
                    message = "Conflict: Both local and server changed!";
                    buttonText = "Merge";
                }
                else if (hasServerUpdate)
                {
                    message = "Server update available!";
                    buttonText = "Download";
                }
                else if (hasLocalChanges)
                {
                    message = $"You have {TranslatorCore.LocalChangesCount} local changes to upload!";
                    buttonText = "Upload";
                }
                else
                {
                    message = $"{TranslatorCore.LocalChangesCount} local changes";
                    buttonText = "Upload";
                }

                _syncLabel.text = message;
                _syncActionBtn.ButtonText.text = buttonText;
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

            // Adjust panel height based on visible content
            AdjustHeight();
        }

        private void AdjustHeight()
        {
            int height = 10; // padding
            if (_modUpdateBox != null && _modUpdateBox.activeSelf) height += 60;
            if (_syncBox != null && _syncBox.activeSelf) height += 60;
            if (_ollamaBox != null && _ollamaBox.activeSelf) height += 50;

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

            RefreshOverlay();
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

        #endregion
    }
}
