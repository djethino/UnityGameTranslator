using System;
using UnityGameTranslator.Common;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Corner notification overlay showing mod updates, sync status, and AI queue.
    /// Displays when no main panels are open.
    ///
    /// Every line here must stay ONE line tall: the boxes are stacked, so a label that grows to
    /// two lines shifts everything under it and the overlay visibly jumps. Game text shown here is
    /// therefore always passed through <see cref="Flatten"/> first.
    /// </summary>
    public class StatusOverlay : TranslatorPanelBase
    {
        public override string Name => "StatusOverlay";
        public override int MinWidth => 350;
        public override int MinHeight => 50;
        public override int PanelWidth => 350;
        public override int PanelHeight => 180;

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
        private Host _modUpdateBox;
        private LabelHandle _modUpdateLabel;
        private LabelHandle _modManagerHint;
        private ButtonHandle _modUpdateBtn;
        private ButtonHandle _modManagerBtn;

        // UI elements - Translation sync notification
        private Host _stack;
        private Host _syncBox;
        private LabelHandle _syncLabel;
        private LabelHandle _syncHintLabel;
        private ButtonHandle _syncBranchBtn;    // Branch option (contribute, green)
        private ButtonHandle _syncForkBtn;      // Fork option (independent, red)
        private ButtonHandle _syncActionBtn;    // Generic action (Download/Update/Merge)
        private ButtonHandle _syncCompareBtn;   // Look before pushing (owners with changes)

        // UI elements - Website notifications relay
        private Callout _webNotif;

        // UI elements - AI queue status
        private Host _aiBox;
        private LabelHandle _aiStatusLabel;
        private LabelHandle _aiQueueLabel;

        // UI elements - SSE connection indicator
        private Host _connectionBox;
        private LabelHandle _connectionLabel;
        private LabelHandle _connectionDot;

        // UI elements - Hotkey feedback toast (short-lived visual notification)
        // When the toast is active, the other boxes (mod update, sync, AI queue, connection)
        // are hidden to avoid confusion: the overlay becomes a single-purpose hotkey feedback.
        private Toasts _toast;
        private float _toastHideTime = 0f;
        private const float TOAST_DURATION = 1.8f;

        // State - whether main panels are open (affects which boxes are shown)
        private bool _panelsOpenMode = false;

        // Set while rendering: the visible action pulls from the Main, not from
        // this translation's own published version
        private bool _syncActionIsUpstream = false;
        private bool _syncActionIsReview = false;

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
        /// What the sync state means for the player, right now.
        ///
        /// The same four questions were answered by three copies of the same
        /// dozen lines; they drifted apart the moment one of them learned about
        /// pending settings. One place, one answer.
        /// </summary>
        private struct PendingSyncWork
        {
            public ServerTranslationState ServerState;
            /// <summary>Lines captured or edited in game, not sent yet.</summary>
            public bool HasLocalChanges;
            /// <summary>Fonts, images, exclusions, variables — shipped with the translation.</summary>
            public bool HasMetadataChanges;
            /// <summary>A newer version waits on the site and nothing local conflicts.</summary>
            public bool HasServerUpdate;
            /// <summary>Both sides moved: this one always speaks up.</summary>
            public bool NeedsMerge;

            /// <summary>
            /// Branch only: the Main it derives from has published something since
            /// the last merge from it. A different source entirely from the three
            /// above, which all concern this translation's own line on the site.
            /// </summary>
            public bool HasMainUpdate;

            /// <summary>Main only: branches never reviewed, or changed since.</summary>
            public int BranchesPendingReview;

            /// <summary>
            /// Signed in, and the server has not yet said what this account is to this lineage.
            ///
            /// 🔴 **Every message below is written in the second person, so it cannot be built
            /// before we know who "you" are.** The public endpoint answers about a translation and
            /// never about a person: it fills the state with "not the owner", because that is all
            /// an anonymous caller can be told. Read while the account's own check was still in
            /// flight, that offered the OWNER of the translation the two buttons meant for a
            /// stranger — Branch and Fork — for the second or two it took to answer.
            ///
            /// ⚠ **Waiting is the whole fix, and it costs nothing.** Nothing here degrades: a
            /// notification that appears a second later is a notification that is right. Showing
            /// it early and correcting it is how somebody clicks Fork on their own translation.
            /// </summary>
            public bool WaitingForAccount;

            /// <summary>Anything at all worth showing.</summary>
            public bool Any => !WaitingForAccount
                               && (HasLocalChanges || HasMetadataChanges || HasServerUpdate
                                   || NeedsMerge || HasMainUpdate || BranchesPendingReview > 0);

            public static PendingSyncWork Current()
            {
                var serverState = TranslatorCore.ServerState;
                bool existsOnServer = serverState != null && serverState.Exists && serverState.SiteId.HasValue;
                var direction = TranslatorUIManager.PendingUpdateDirection;

                // "Notify me about updates" only ever governs the case it names: a
                // newer version waiting, which costs nothing to learn about later.
                // A merge is work at risk and a local change is the player's own
                // unsent work — silencing those would hide, not calm.
                bool notifyUpdates = TranslatorCore.Config.sync.notify_updates;

                // ⚠ Not "have we asked" but "have we asked AS US" — the two differ for exactly as
                // long as this matters. See ServerTranslationState.AskedAsAccount.
                bool signedIn = TranslatorCore.Config.online_mode
                                && !string.IsNullOrEmpty(TranslatorCore.Config.api_token);

                return new PendingSyncWork
                {
                    ServerState = serverState,
                    WaitingForAccount = signedIn && !(serverState != null && serverState.AskedAsAccount),
                    HasLocalChanges = existsOnServer && TranslatorCore.LocalChangesCount > 0,
                    HasMetadataChanges = existsOnServer && TranslatorCore.MetadataDirty,
                    HasServerUpdate = notifyUpdates && TranslatorUIManager.HasPendingUpdate &&
                        direction == UpdateDirection.Download,
                    NeedsMerge = TranslatorUIManager.HasPendingUpdate &&
                        direction == UpdateDirection.Merge,
                    // Same switch as a server update: it is an update, just from
                    // upstream. Nothing is at risk if it waits.
                    HasMainUpdate = notifyUpdates && TranslatorUIManager.HasMainUpdate(),
                    BranchesPendingReview = notifyUpdates && serverState != null
                        ? serverState.BranchesPendingReview
                        : 0,
                };
            }
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
            var pending = PendingSyncWork.Current();
            bool showSyncNotification = pending.Any && !TranslatorUIManager.NotificationDismissed;

            return showModUpdate || showSyncNotification;
        }

        public override void SetDefaultSizeAndPosition()
        {
            Overlays.SetSize(Window, PanelWidth, PanelHeight);
            ApplyPositionFromConfig();
            EnsureValidPosition();
        }

        /// <summary>
        /// Applies the notification position from config to the overlay anchors/pivot.
        /// Called at init and when the user changes the position setting.
        /// </summary>
        public void ApplyPositionFromConfig()
        {
            string position = TranslatorCore.Config?.sync?.notification_position ?? "top-right";
            Overlays.PinToCorner(Window, position);
        }

        protected override void ConstructPanelContent()
        {
            // Remove default title bar for this overlay
            TitleBarHost.Visible = false;

            // ⚠ Kept: it is the only thing that knows how tall the overlay has to be, spacing
            // and padding included. See AdjustHeight.
            _stack = Stacks.Vertical(Content, "OverlayStack",
                                     spacing: StackSpacing, pad: Pad.All(StackPadding / 2));
            var stack = _stack;

            // Mod Update Notification Box
            CreateModUpdateBox(stack);

            // Translation Sync Notification Box
            CreateSyncBox(stack);

            // AI Queue Status Box
            CreateAIBox(stack);

            // SSE Connection Indicator
            CreateConnectionBox(stack);

            // Hotkey feedback toast
            CreateToastBox(stack);

            // Start hidden and with update
            RefreshOverlay();
        }

        private void CreateToastBox(Host stack)
        {
            _toast = Toasts.Create(stack, "ToastBox");
        }

        public enum ToastTone { Info, On, Off }

        /// <summary>
        /// Shows a short-lived toast message (used for hotkey feedback).
        /// While the toast is active, all other overlay boxes are hidden so the
        /// hotkey feedback is unambiguous. The toast auto-hides after TOAST_DURATION.
        /// </summary>
        /// <summary>
        /// Collapse any run of whitespace — line breaks included — into single spaces, so a piece
        /// of game text can be shown on one line whatever it contains.
        /// </summary>
        private static string Flatten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new System.Text.StringBuilder(text.Length);
            bool inWhitespace = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    inWhitespace = true;
                    continue;
                }
                if (inWhitespace && sb.Length > 0) sb.Append(' ');
                inWhitespace = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        public void ShowToast(string message, ToastTone tone = ToastTone.Info)
        {
            if (_toast == null) return;

            // Force overlay visibility regardless of the user's "notifications_enabled" preference:
            // an explicit hotkey action deserves immediate visual feedback.
            if (!Enabled)
            {
                SetActive(true);
            }

            // Hide all other boxes so the toast is the only thing visible.
            HideNonToastBoxes();

            // Colour by tone for fast visual read (green = ON, red = OFF, purple = neutral info).
            Tone paletteTone = tone == ToastTone.On ? Tone.Success
                              : tone == ToastTone.Off ? Tone.Error
                              : Tone.Info;
            _toast.Show(message, paletteTone);

            _toastHideTime = Clock.Now + TOAST_DURATION;
        }

        private void HideNonToastBoxes()
        {
            if (_modUpdateBox != null) _modUpdateBox.Visible = false;
            if (_syncBox != null) _syncBox.Visible = false;
            if (_aiBox != null) _aiBox.Visible = false;
            if (_connectionBox != null) _connectionBox.Visible = false;
        }

        /// <summary>
        /// Returns true while a hotkey feedback toast is currently shown.
        /// The owning UIManager should skip RefreshOverlay() during this time
        /// so the toast stays visible without being overwritten by the regular boxes.
        /// </summary>
        public bool IsToastActive => _toast != null && _toast.Visible;

        /// <summary>
        /// Called from the UI update loop to expire the toast after its duration.
        /// Once expired, the other boxes are restored by the next RefreshOverlay pass.
        /// </summary>
        public void TickToast()
        {
            if (_toast == null || !_toast.Visible) return;
            if (Clock.Now >= _toastHideTime)
            {
                _toast.Visible = false;
                // Trigger a refresh so the normal boxes come back immediately.
                RefreshOverlay();
            }
        }

        private void CreateModUpdateBox(Host stack)
        {
            _modUpdateBox = Callout.Box(stack, "ModUpdateBox", CalloutTone.Success,
                                        pad: new Pad(8, 8, 5, 5));

            _modUpdateLabel = Labels.Create(_modUpdateBox, "ModUpdateLabel",
                                            "Mod update available: v?.?.?", TextRole.Body,
                                            policy: TextPolicy.Excluded);
            _modUpdateLabel.Bold = true;

            // ⚠ Only shown when the Manager has to be fetched. "Open Manager" is a verb that
            // explains itself; "Get Manager" is an offer, and an offer with no reason beside it is
            // one nobody takes.
            _modManagerHint = Labels.Create(_modUpdateBox, "ModManagerHint",
                "Or let the Manager keep it up to date", TextRole.Small);

            var btnRow = Stacks.Row(_modUpdateBox, "ModBtnRow", spacing: 5);

            _modUpdateBtn = Buttons.Compact(btnRow, "ModDownloadBtn", "Download", ButtonTone.Primary,
                                            minWidth: 80, policy: TextPolicy.Excluded);
            _modUpdateBtn.Clicked += OnModUpdateClicked;

            // The other way to update, beside the manual one rather than in place of it. Secondary
            // on purpose: whoever came here to grab a zip should still find the zip first.
            _modManagerBtn = Buttons.Compact(btnRow, "ModManagerBtn", "Get Manager", ButtonTone.Secondary,
                                             minWidth: 100, policy: TextPolicy.Excluded);
            _modManagerBtn.Clicked += OnModManagerClicked;

            var modIgnoreBtn = Buttons.Compact(btnRow, "ModIgnoreBtn", "Ignore", ButtonTone.Secondary,
                                               minWidth: 60);
            modIgnoreBtn.Clicked += OnModIgnoreClicked;

            _modUpdateBox.Visible = false;
        }

        private void CreateSyncBox(Host stack)
        {
            _syncBox = Callout.Box(stack, "SyncBox", CalloutTone.Warning, pad: new Pad(8, 8, 5, 5));

            _syncLabel = Labels.Create(_syncBox, "SyncLabel", "Sync status", TextRole.Body,
                                       policy: TextPolicy.Excluded);
            _syncLabel.Bold = true;

            var syncBtnRow = Stacks.Row(_syncBox, "SyncBtnRow", spacing: 3);

            // Branch button (green) - contribute to main, shown for non-owners with local changes
            _syncBranchBtn = Buttons.Compact(syncBtnRow, "SyncBranchBtn", "Branch", ButtonTone.Success,
                                             minWidth: 65);
            _syncBranchBtn.Clicked += OnSyncBranchClicked;

            // Fork button (red) - create independent copy, shown for non-owners with local changes
            _syncForkBtn = Buttons.Compact(syncBtnRow, "SyncForkBtn", "Fork", ButtonTone.Danger,
                                           minWidth: 55);
            _syncForkBtn.Clicked += OnSyncForkClicked;

            // Generic action button (Download/Update/Merge) - for other scenarios
            _syncActionBtn = Buttons.Compact(syncBtnRow, "SyncActionBtn", "Action", ButtonTone.Primary,
                                             minWidth: 75, policy: TextPolicy.Excluded);
            _syncActionBtn.Clicked += OnSyncActionClicked;

            // Compare — between the action and Settings, the place it holds on the main panel.
            //
            // ⚠ It was on that panel only, so somebody who never opens a panel — which is the
            // whole point of this corner — could push everything or nothing, and never look first.
            // Same door, same condition: see TranslatorUIManager.CanCompareWithServer.
            _syncCompareBtn = Buttons.Compact(syncBtnRow, "SyncCompareBtn", "Compare",
                                              ButtonTone.Secondary, minWidth: 75,
                                              policy: TextPolicy.Excluded);
            _syncCompareBtn.Clicked += OnSyncCompareClicked;

            // Settings button
            var syncSettingsBtn = Buttons.Compact(syncBtnRow, "SyncSettingsBtn", "Settings",
                                                  ButtonTone.Secondary, minWidth: 65);
            syncSettingsBtn.Clicked += OnSyncSettingsClicked;

            // Ignore button (last)
            var syncIgnoreBtn = Buttons.Compact(syncBtnRow, "SyncIgnoreBtn", "Ignore",
                                                ButtonTone.Secondary, minWidth: 55);
            syncIgnoreBtn.Clicked += OnSyncIgnoreClicked;

            // Plain-words explanation of the Branch/Fork choice (only shown with those buttons)
            _syncHintLabel = Labels.Create(_syncBox, "SyncHintLabel", "", TextRole.Hint,
                                           policy: TextPolicy.Excluded);

            _syncBox.Visible = false;

            CreateWebNotifBox(stack);
        }

        /// <summary>
        /// Website notifications relay: contributions to review, announcements.
        /// Discreet corner box with the first notification's summary.
        /// </summary>
        private void CreateWebNotifBox(Host stack)
        {
            _webNotif = Callout.Create(stack, "WebNotifBox", CalloutTone.Info, "",
                                       policy: TextPolicy.Excluded);

            var viewBtn = Buttons.Compact(_webNotif.Actions, "WebNotifViewBtn", "View",
                                          ButtonTone.Primary, minWidth: 65);
            viewBtn.Clicked += OnWebNotifViewClicked;

            var dismissBtn = Buttons.Compact(_webNotif.Actions, "WebNotifDismissBtn", "Dismiss",
                                             ButtonTone.Secondary, minWidth: 70);
            dismissBtn.Clicked += OnWebNotifDismissClicked;

            _webNotif.Visible = false;
        }

        /// <summary>
        /// Show/hide the website notifications box from the latest poll result.
        /// </summary>
        public void RefreshNotificationsBox()
        {
            if (_webNotif == null) return;

            var result = TranslatorUIManager.WebsiteNotifications;
            bool show = !TranslatorUIManager.WebsiteNotificationsDismissed &&
                        result != null && result.Unread > 0 && result.Items.Count > 0;

            _webNotif.Visible = show;
            if (show)
            {
                // Comes from the website, so it may carry line breaks — same one-line rule
                string text = Flatten(result.Items[0].Text);
                if (result.Unread > 1)
                {
                    text += " " + Tr($"(+{result.Unread - 1} more)");
                }
                _webNotif.Title.Show(text);
            }
        }

        private void OnWebNotifViewClicked()
        {
            var result = TranslatorUIManager.WebsiteNotifications;
            string url = result?.Items.Count > 0 ? result.Items[0].Url : null;
            TranslatorCore.OpenUrlSafe(url ?? $"{ApiClient.WebsiteBaseUrl}/notifications");
        }

        private void OnWebNotifDismissClicked()
        {
            TranslatorUIManager.MarkWebsiteNotificationsRead();
            if (_webNotif != null) _webNotif.Visible = false;
        }

        private void CreateAIBox(Host stack)
        {
            _aiBox = Callout.Box(stack, "AIBox", CalloutTone.Info, spacing: 3,
                                 pad: new Pad(8, 8, 5, 5), minHeight: UIStyles.MultiLineSmall);

            _aiStatusLabel = Labels.Create(_aiBox, "AIStatusLabel", "Translating...", TextRole.Body,
                                           policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);

            _aiQueueLabel = Labels.Create(_aiBox, "AIQueueLabel", "Queue: 0 pending", TextRole.Small,
                                          policy: TextPolicy.Excluded);
            // Exclude dynamic status labels from translation (they contain truncated game text!)
            // (their creation above already registers them Excluded)

            _aiBox.Visible = false;
        }

        /// <summary>
        /// Where the link to the website stands: one line, and a dot.
        ///
        /// ⚠ **No vertical padding, on purpose.** This is the one box in the stack that says a
        /// single thing in a single line — the others carry a headline, a hint and buttons — so
        /// room around it is room that pushes everything else up the screen for nothing. The line
        /// height is the box height.
        ///
        /// ⚠ **The dot is the state; the words are the courtesy.** Colour is read before text, and
        /// at a glance from across a game it may be all that is read — so the dot carries green,
        /// amber and red on its own, and would still be understood with the words removed. It is
        /// the mark <see cref="StatusCard"/> already uses for the same job.
        /// </summary>
        private void CreateConnectionBox(Host stack)
        {
            _connectionBox = Stacks.Horizontal(stack, "ConnectionBox", spacing: 5,
                                               pad: new Pad(8, 8, 0, 0), placement: Placement.MiddleRight,
                                               minHeight: UIStyles.RowHeightSmall);

            _connectionLabel = Labels.Create(_connectionBox, "ConnectionLabel", "", TextRole.Small,
                                             policy: TextPolicy.Excluded, align: Placement.MiddleRight);

            // After the words, so it sits against the right edge the box is aligned to.
            //
            // 🔴 **minWidth is what keeps the words on the right**, and it is not a size choice.
            // Labels.Create stretches any label aligned right that has no width of its own — one
            // does the right thing, TWO share the room between them, and "Connected" lands in the
            // middle of the box. Giving the dot the width of its own glyph leaves the label the
            // only stretched thing in the row, so the words stay flush against it.
            _connectionDot = Labels.Create(_connectionBox, "ConnectionDot", StatusDot, TextRole.Small,
                                           policy: TextPolicy.Excluded, align: Placement.MiddleRight,
                                           minWidth: 12, wrap: false);

            _connectionBox.Visible = false;
        }

        /// <summary>
        /// The mark the dot is drawn with — U+25CF, the same one the status card uses.
        ///
        /// ⚠ A character rather than an image: it inherits the tone colours the rest of the
        /// interface is built on, costs no texture, and cannot be lost by an atlas that failed to
        /// warm on a runtime that strips things.
        /// </summary>
        private const string StatusDot = "●";

        /// <summary>
        /// Returns true if the overlay has any content to display.
        /// Used by TranslatorUIManager to decide whether to show the overlay.
        /// </summary>
        public bool HasContentToShow()
        {
            // 1. Mod update notification
            bool showModUpdate = TranslatorUIManager.HasModUpdate && !TranslatorUIManager.ModUpdateDismissed;

            // 2. Translation sync notification
            var pending = PendingSyncWork.Current();
            bool showSyncNotification = pending.Any && !TranslatorUIManager.NotificationDismissed;

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
                _modUpdateBox.Visible = true;
                var info = TranslatorUIManager.ModUpdateInfo;
                string kind = info?.IsPrerelease == true ? " " + Tr("(beta)")
                    : info?.IsMajorUpdate == true ? " " + Tr("(major)") : "";
                // Version number is data, appended after the translated part
                _modUpdateLabel.Show(Tr("Mod update available:") + $" v{info?.LatestVersion ?? "?"}{kind}");

                // Show appropriate button
                bool hasDirectDownload = !string.IsNullOrEmpty(info?.DownloadUrl);
                _modUpdateBtn.Label = hasDirectDownload ? "Download" : "View Release";

                // The verb follows what pressing it will actually do — open the Manager already on
                // this machine, or go and fetch it. Naming the wrong one is how a button that
                // launches a program reads as a download, and the other way round.
                bool managerHere = ManagerLink.IsOnThisMachine;
                _modManagerBtn.Label = managerHere ? "Open Manager" : "Get Manager";
                if (_modManagerHint != null) _modManagerHint.Visible = !managerHere;
            }
            else
            {
                if (_modUpdateBox != null) _modUpdateBox.Visible = false;
            }

            // 2. Translation sync notification (hidden when panels open - shown in MainPanel instead)
            var pending = PendingSyncWork.Current();
            var serverState = pending.ServerState;
            bool hasLocalChanges = pending.HasLocalChanges;
            bool hasMetadataChanges = pending.HasMetadataChanges;
            bool hasServerUpdate = pending.HasServerUpdate;
            bool needsMerge = pending.NeedsMerge;

            bool showSyncNotification = !_panelsOpenMode && pending.Any &&
                                        !TranslatorUIManager.NotificationDismissed;

            if (showSyncNotification && _syncBox != null)
            {
                _syncBox.Visible = true;

                // Determine message and button visibility based on context
                string message;
                var direction = TranslatorUIManager.PendingUpdateDirection;

                // Get role for role-specific messages
                bool isBranch = serverState?.Role == LineageRole.Branch;
                bool isOwner = serverState?.IsOwner == true;

                // 🔴 **Whether a contribution can be made at all is the socle's to say**, from the
                // walls the server reported: a Main marked solo work takes none, and neither does
                // one whose author has removed it or erased their account. Offered anyway, the
                // button names an act the server will refuse — and the hint beside it promised to
                // send the lines "for review to @somebody" who had asked for no such thing.
                //
                // ⚠ The main panel and the upload screen already ask this; this corner did not,
                // which is the same defect the panel's own comment records having paid for once.
                bool existsOnServer = serverState != null && serverState.Exists
                                      && serverState.SiteId.HasValue;
                var publication = Publications.Of(hereOnDisk: TranslatorCore.TranslationCache.Count > 0,
                                                  onTheSite: existsOnServer,
                                                  yours: existsOnServer ? serverState.IsOwner : (bool?)null);
                bool onABranch = existsOnServer && serverState.IsOwner
                                 && serverState.Role == LineageRole.Branch;
                var offered = Uploads.ActOf(publication, onABranch, serverState?.AcceptsBranches,
                                            serverState?.MainMissing, serverState?.MainAbandoned,
                                            serverState?.BranchFrozen);

                // A lineage that takes no contribution leaves one way on, and it is the one the
                // socle names: forking.
                bool canBranch = offered == UploadAct.Contribute;

                // Default: hide Branch/Fork buttons, show Action button
                bool showBranchFork = false;
                bool showAction = true;
                string actionText = "Sync";
                // The action button reads PendingUpdateDirection, which only ever
                // describes THIS translation's own line. An upstream merge is a
                // different exchange, so it is flagged here rather than smuggled
                // into that enum — the two sources must not converge again.
                bool actionIsUpstream = false;
                bool actionIsReview = false;

                // Get owner name for context
                // One form for the ecosystem, and it marks your own name — see People.Mention.
                string ownerName = People.MentionOf(serverState?.Uploader,
                                                    TranslatorCore.Config.api_user);

                // Translated as each message is built. Counts stay inline (the pipeline replaces
                // numbers with placeholders, so every count shares one cache entry); the uploader
                // name is appended, never sent for translation.
                if (needsMerge)
                {
                    message = isOwner
                        ? Tr("Both local and server changed. Sync needed!")
                        : Tr("Sync needed — translation updated by") + $" @{ownerName}";
                    actionText = "Sync";
                }
                else if (hasServerUpdate)
                {
                    if (isOwner)
                    {
                        // Owner, Main or branch alike: it is THEIR OWN published
                        // version that moved — another machine, or the site editor.
                        // This used to claim "Parent translation update available!"
                        // for a branch, which named the wrong source entirely: the
                        // hash compared here has never been the Main's.
                        message = Tr("Server update available!");
                    }
                    else
                    {
                        // Non-owner: the Main they downloaded from has been updated
                        message = Tr("Translation updated by") + $" @{ownerName}";
                    }
                    actionText = "Download";
                }
                else if (pending.HasMainUpdate)
                {
                    // Genuinely upstream this time, and a different exchange: it is
                    // merged into the branch, never downloaded over it
                    message = Tr("The original translation has been updated by") + $" @{ownerName}";
                    actionText = "Update";
                    actionIsUpstream = true;
                }
                else if (hasLocalChanges)
                {
                    if (isOwner)
                    {
                        // Owner: show Update button
                        message = Tr($"You have {TranslatorCore.LocalChangesCount} local changes to upload!");
                        actionText = "Update";
                    }
                    else
                    {
                        // Non-owner: show Branch AND Fork options
                        // User must choose to contribute (branch) or go independent (fork)
                        message = Tr($"You changed {TranslatorCore.LocalChangesCount} line(s). Share them?");
                        showBranchFork = true;
                        showAction = false;
                    }
                }
                else if (hasMetadataChanges)
                {
                    // No new lines, but settings that travel with the translation were edited
                    if (isOwner)
                    {
                        message = Tr("Translation settings changed — not uploaded yet");
                        actionText = "Update";
                    }
                    else
                    {
                        message = Tr("You changed translation settings. Share them?");
                        showBranchFork = true;
                        showAction = false;
                    }
                }
                else if (pending.BranchesPendingReview > 0)
                {
                    // Last, and rightly so: nothing degrades while it waits. But a
                    // contribution nobody ever hears about is a contributor lost —
                    // and the branch COUNT never moved when someone pushed more
                    // work to a branch already counted.
                    message = Tr($"{pending.BranchesPendingReview} contribution(s) waiting for your review");
                    actionText = "Review";
                    actionIsReview = true;
                }
                else
                {
                    // Fallback for edge case (shouldn't happen with current logic)
                    message = Tr($"{TranslatorCore.LocalChangesCount} local changes");
                    actionText = "Sync";
                }

                _syncLabel.Show(message);

                // Show/hide buttons based on context
                // ⚠ Absent rather than greyed, and the two are decided differently on purpose:
                // greyed says "later" — sign in, come back online — while a lineage that refuses
                // contributions is not a "later", it is a road that does not exist. The hint below
                // says which wall it is, in the socle's words.
                if (_syncBranchBtn != null) _syncBranchBtn.Visible = showBranchFork && canBranch;
                if (_syncForkBtn != null) _syncForkBtn.Visible = showBranchFork;
                if (_syncActionBtn != null) _syncActionBtn.Visible = showAction;

                // 🔴 **It turns into the way out, it does not go away.** It used to be hidden while
                // a comparison was in flight, on the reasoning that this row is crowded and the way
                // out could live on the main panel. Two things were wrong with that: a control
                // vanishing at the moment it is clicked reads as the click having failed, and the
                // same fact then said two different things on two screens — which is the one thing
                // an ecosystem may not do. Whoever opened it from here closes it from here.
                RefreshCompareButton();

                // 🔴 **The refusal is said before the click, not after the form.** This button
                // opened the upload window whatever the state of the account, so somebody with no
                // account filled it in and learnt there that they needed one — while the main
                // panel, three inches away in another screen, greys its own Upload and says why.
                // One rule for both: Uploads.ClosedReason.
                string branchClosed = null;
                string forkClosed = null;
                if (showBranchFork)
                {
                    bool online = TranslatorCore.Config.online_mode;
                    bool signedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
                    int lines = TranslatorCore.TranslationCache.Count;
                    bool untouchedCopy = serverState == null || !serverState.Exists
                                         ? TranslatorCore.ForkIsStillTheCopy
                                         : false;

                    branchClosed = Uploads.ClosedReason(UploadAct.Contribute, lines, untouchedCopy,
                                                        online, signedIn, inSync: false);
                    forkClosed = Uploads.ClosedReason(UploadAct.Fork, lines, untouchedCopy,
                                                      online, signedIn, inSync: false);

                    if (_syncBranchBtn != null && canBranch) _syncBranchBtn.Enabled = branchClosed == null;
                    if (_syncForkBtn != null) _syncForkBtn.Enabled = forkClosed == null;
                }

                if (_syncHintLabel != null)
                {
                    // ⚠ The reason REPLACES the half it is about, and never both: a fork asks for
                    // neither an account nor the network, so it stays open and stays explained —
                    // it is the way on precisely when the other half is shut.
                    string hint = "";
                    if (showBranchFork)
                    {
                        // The wall first when there is one — it is why the Branch button is not
                        // there — then whatever is left to say about the way on.
                        string wall = canBranch
                            ? null
                            : Uploads.Wall(publication, onABranch,
                                           serverState?.MainUsername ?? serverState?.Uploader,
                                           serverState?.AcceptsBranches, serverState?.MainMissing,
                                           serverState?.MainAbandoned, serverState?.BranchFrozen);

                        // ⚠ The reason is prefixed with the button it is ABOUT. On its own,
                        // "Login required" reads as a statement of fact beside a greyed button —
                        // it never says that signing in is what turns that button back on. The
                        // other half already names itself the same way.
                        string branchHalf = wall != null
                            ? Tr(wall)
                            : branchClosed != null
                                ? Tr("Branch:") + " " + Tr(branchClosed)
                                : Tr("Branch: send them for review to") + $" @{ownerName}";

                        hint = branchHalf + " • "
                               + (forkClosed != null
                                   ? Tr(forkClosed)
                                   : Tr("Fork: start your own independent translation"));
                    }

                    _syncHintLabel.Show(hint);
                    _syncHintLabel.Visible = showBranchFork;
                }

                _syncActionIsUpstream = actionIsUpstream;
                _syncActionIsReview = actionIsReview;

                if (showAction)
                {
                    _syncActionBtn.Label = actionText;
                }
            }
            else
            {
                if (_syncBox != null) _syncBox.Visible = false;
            }

            // 3. AI queue status
            bool aiEnabled = TranslatorCore.Config.IsTranslationEnabled;
            int queueCount = TranslatorCore.QueueCount;
            bool isTranslating = TranslatorCore.IsTranslating;
            bool showAI = aiEnabled && (queueCount > 0 || isTranslating);

            if (showAI && _aiBox != null)
            {
                _aiBox.Visible = true;

                if (isTranslating)
                {
                    // The excerpt is GAME text being translated — data, never sent for translation.
                    // Flattened first: game strings often carry line breaks, and a single one made
                    // this label two lines tall, pushing the queue line below it. The overlay then
                    // jumped on every such text and settled back on the next one.
                    // No excerpt for our own interface: quoting our own labels here reads as the
                    // mod translating itself ("Translating: Translating:").
                    if (TranslatorCore.CurrentTextIsOwnUI)
                    {
                        _aiStatusLabel.Say("Translating the interface...");
                    }
                    else
                    {
                        // ⚠ Markup out FIRST. A game line arrives as
                        // `…建议您<b><color=#FF0000>` and the tags were shown as they stood — they
                        // say nothing to a reader and they ate most of the twenty-five characters
                        // this excerpt is allowed, so the notification quoted punctuation instead
                        // of words. Stripping is the socle's, shared with the translation path.
                        string text = Flatten(TextNormalization.StripMarkupTags(TranslatorCore.CurrentText));
                        if (text.Length > 25) text = text.Substring(0, 25) + "…";
                        _aiStatusLabel.Show(Tr("Translating:") + $" {text}");
                    }
                    _aiStatusLabel.Visible = true;
                }
                else
                {
                    _aiStatusLabel.Visible = false;
                }

                if (queueCount > 0)
                {
                    _aiQueueLabel.Say($"Queue: {queueCount} pending");
                    _aiQueueLabel.Visible = true;
                }
                else
                {
                    _aiQueueLabel.Visible = false;
                }
            }
            else
            {
                if (_aiBox != null) _aiBox.Visible = false;
            }

            // 4. Where the link to the website stands.
            //
            // ⚠ **A link that DROPPED is shown too, in red**, where the box used to simply vanish.
            // Disappearing reads as "there was never anything here", which is the one thing it does
            // not mean: the mod is signed in, it was listening, and it no longer is. Only somebody
            // who never had a stream sees nothing — and for them there is genuinely nothing to say.
            bool showConnection = false;
            if (TranslatorCore.Config.online_mode && !string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                string say = null;
                Tone tone = Tone.Muted;

                switch (TranslatorUIManager.SyncConnectionState)
                {
                    case SseConnectionState.Connected:
                        say = "Connected"; tone = Tone.Success; break;
                    case SseConnectionState.Connecting:
                        say = "Connecting..."; tone = Tone.Warning; break;
                    case SseConnectionState.Reconnecting:
                        say = "Reconnecting..."; tone = Tone.Warning; break;
                    // ⚠ **What it says moves, because something is happening.** A link that
                    // dropped and is waiting for its next try used to read "Reconnecting…" and
                    // never change — for hours, if the network stayed down — so a dead stream and
                    // a slow one looked identical. The countdown is the difference, and it is the
                    // one thing that says the mod has not given up.
                    default:
                        if (TranslatorUIManager.SyncStreamWanted)
                        {
                            int wait = TranslatorUIManager.SyncRetryInSeconds;
                            say = wait > 0 ? $"Retrying in {wait}s" : "Disconnected";
                            tone = Tone.Error;
                        }
                        break;
                }

                showConnection = say != null;
                if (showConnection)
                {
                    if (_connectionLabel != null) { _connectionLabel.Say(say); _connectionLabel.Tone = tone; }
                    if (_connectionDot != null) _connectionDot.Tone = tone;
                }
            }
            if (_connectionBox != null) _connectionBox.Visible = showConnection;

            // Adjust panel height based on visible content
            AdjustHeight();
        }

        /// <summary>
        /// Size the overlay to what it is actually showing.
        ///
        /// 🔴 **It used to add a fixed count of pixels per visible box** — 60, 60, 50, 20 — and had
        /// done so since the overlay was written (2025-12-27). That held while every box was a
        /// headline and a row of buttons, all the same height. It stopped holding the day one of
        /// them gained a line of explanation long enough to wrap: the box wants about ninety, is
        /// given sixty, and the difference is what a reader sees — a line cut across the middle,
        /// with the next notification drawn over what is left of it.
        ///
        /// 🔴 **The STACK is asked, not the boxes one by one**, and that is the difference between
        /// a fix and an answer. Adding up the boxes still leaves the caller keeping a private copy
        /// of the layout: the five pixels between each pair and the five around the whole — which
        /// the first version of this fix got wrong, by ten pixels, with three notifications up. The
        /// stack holds its children, its spacing and its padding; asked once, it cannot drift, and a
        /// sixth box added later needs nothing here.
        ///
        /// ⚠ The old numbers survive as a floor, and only for the frame the engine has not laid out
        /// yet — it answers 0 then, and an overlay one frame tall is the jump this exists to avoid.
        /// </summary>
        private void AdjustHeight()
        {
            // 🔴 **The boxes, not the stack — and the stack cannot answer, by construction.** It is
            // stretched to the window it is inside, so its own height IS the window's: asking it
            // how tall the window should be is asking the window. Measured on a real install
            // (2026-09-08): empty it answered 10, its padding; with one box visible it answered 0,
            // and the caller fell back on the numbers this was written to replace.
            //
            // ⚠ A box CAN answer, because the stack sizes it from its content. What it wants is
            // read first; when the layout has not been calculated yet that comes back as 0, and
            // then what it currently IS is read instead — see Host.WantedHeight.
            // ⚠ **The parent first, once.** Widths travel down; heights are read back up. Asking a
            // box before its width exists is what made one ask for 1541 pixels on the frame it
            // appeared, and the overlay open at the height of the screen before settling twice.
            _stack?.SettleLayout();

            int height = StackPadding;
            int shown = 0;

            foreach (var box in new[] { _modUpdateBox, _syncBox, _aiBox, _connectionBox })
            {
                if (box == null || !box.Visible) continue;

                float boxHeight = box.WantedHeight;
                if (boxHeight <= 0f) continue;

                if (shown > 0) height += StackSpacing;
                height += (int)Math.Ceiling(boxHeight);
                shown++;
            }

            // Nothing could be measured at all — a first frame, before any layout pass.
            if (shown == 0) height = FloorHeight();

            Overlays.SetSize(Window, PanelWidth, Math.Max(50, height));
        }

        // What the stack costs around its boxes, and between them.
        //
        // 🔴 **Declared here and READ by the stack**, rather than written twice. AdjustHeight has
        // to know them to size the window, and the first attempt kept its own copy — which said
        // ten and forgot the five between each pair, so three notifications came out ten pixels
        // short and the last line of one was cut across the middle.
        private const int StackPadding = 10;   // above the first box and below the last, together
        private const int StackSpacing = 5;    // between each pair


        /// <summary>
        /// What the overlay was worth before anything could be measured — the numbers it used from
        /// 2025 until this was written.
        ///
        /// ⚠ Reached on the first frame only, and deliberately not tuned: its job is to be roughly
        /// right for one frame, not to be a second layout nobody remembers to update.
        /// </summary>
        private int FloorHeight()
        {
            int height = 10;
            if (_modUpdateBox != null && _modUpdateBox.Visible) height += 60;
            if (_syncBox != null && _syncBox.Visible) height += 60;
            if (_aiBox != null && _aiBox.Visible) height += 50;
            if (_connectionBox != null && _connectionBox.Visible) height += 20;
            return height;
        }

        #region Button Handlers

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

            // Looked at again on the next refresh: somebody who just went to fetch it may come back
            // with it installed, and the button should stop offering what they now have.
            ManagerLink.Forget();
        }

        private void OnModIgnoreClicked()
        {
            TranslatorUIManager.ModUpdateDismissed = true;
            RefreshOverlay();
        }

        private void OnSyncActionClicked()
        {
            if (_syncActionIsReview)
            {
                // Reviewing contributions happens on the website: bigger screen,
                // line-by-line tools, and the Main owner decides there
                string uuid = TranslatorCore.FileUuid;
                if (!string.IsNullOrEmpty(uuid))
                {
                    TranslatorCore.OpenUrlSafe(ApiClient.GetMergeReviewUrl(uuid));
                }
                return;
            }

            if (_syncActionIsUpstream)
            {
                // Opens the merge panel with a summary; nothing is written until
                // the player confirms there
                _ = TranslatorUIManager.MergeFromMain();
                return;
            }

            var direction = TranslatorUIManager.PendingUpdateDirection;

            switch (direction)
            {
                case UpdateDirection.Upload:
                    TranslatorUIManager.UploadPanel?.OpenForUpload();
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
                    // Nothing pending from the server: this is the owner-side "Update" —
                    // either new lines to push, or only the settings that travel with the
                    // translation (fonts, images, exclusions, variables). Metadata-only
                    // changes leave LocalChangesCount at 0, so gating on it alone made the
                    // button inert for the very case its own notification announces.
                    if (TranslatorCore.LocalChangesCount > 0 || TranslatorCore.MetadataDirty)
                    {
                        TranslatorUIManager.UploadPanel?.OpenForUpload();
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

            // Use centralized methods. MetadataDirty counts as "we have something local":
            // DownloadUpdate overwrites translations.json wholesale and reloads, which drops
            // locally edited settings (fonts, images, exclusions, variables), while the merge
            // flow only rewrites the translation entries and leaves them intact. The direction
            // was already decided with metadata in mind (TranslatorUIManager, hasLocalChanges);
            // deciding again on LocalChangesCount alone contradicted it.
            if (TranslatorCore.LocalChangesCount > 0 || TranslatorCore.MetadataDirty)
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

        /// <summary>
        /// Open the comparison page for our own published version.
        ///
        /// ⚠ The same call the main panel makes, in the same direction: this is OUR translation, so
        /// validating there updates the online one. Nothing is decided here — the page is.
        /// </summary>
        private async void OnSyncCompareClicked()
        {
            // The button says Stop, so it stops — the same door the main panel and the reload path
            // use, which ends the token on the site so the browser tab is told rather than left
            // hanging until it expires.
            if (TranslatorUIManager.IsComparisonOpen)
            {
                TranslatorUIManager.EndComparison("stopped from the notification");
                return;
            }

            var siteId = TranslatorCore.ServerState?.SiteId;
            if (siteId == null) return;

            _syncCompareBtn?.Busy("Loading...");

            try
            {
                // ⚠ Nothing is passed back for the label: by the time the browser is up the verb
                // has changed, and OpenComparison tells both screens itself.
                await TranslatorUIManager.OpenComparison(siteId.Value, toLocal: false);
            }
            catch (System.Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[StatusOverlay] Compare error: {errorMsg}");
                    RefreshCompareButton();
                });
            }
        }

        /// <summary>
        /// What the Compare button says here — the only place that writes it, as on the main panel
        /// and for the same reason: two authors for one label, and the one that ran last knew the
        /// least.
        /// </summary>
        private void RefreshCompareButton()
        {
            if (_syncCompareBtn == null) return;

            bool comparing = TranslatorUIManager.IsComparisonOpen;
            bool canCompare = TranslatorUIManager.CanCompareWithServer;

            _syncCompareBtn.Visible = canCompare || comparing;

            if (comparing)
            {
                // ⚠ Named, not "Stop": this row carries six controls that could all be stopped.
                _syncCompareBtn.Enabled = true;
                _syncCompareBtn.Label = "Stop comparison";
            }
            else if (canCompare)
            {
                _syncCompareBtn.Enabled = true;
                // The same label as on the main panel: how many lines the comparison is about.
                _syncCompareBtn.Label = $"Compare ({TranslatorCore.LocalChangesCount})";
            }
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
            TranslatorUIManager.UploadPanel?.OpenForUpload();
        }

        /// <summary>
        /// Fork button: the same door as the main panel's, <see cref="TranslatorUIManager.OfferFork"/>.
        /// This overlay carried its own copy of the confirmation, and it was the stale one — it
        /// presumed the publishing and opened the upload screen over an untouched copy.
        /// </summary>
        private void OnSyncForkClicked()
        {
            TranslatorUIManager.OfferFork(RefreshOverlay);
        }

        #endregion
    }
}
