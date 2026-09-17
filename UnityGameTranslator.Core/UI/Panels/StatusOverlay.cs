using System;
using System.Collections.Generic;
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
        /// <summary>The screen as a document — common/spec/screens/overlay.json — read once; the base's constructor reads the sizes below through it.</summary>
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("overlay");

        /// <summary>What the builder made of the document: every piece by name.</summary>
        private BuiltScreen _screen;

        public override string Name => Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

        // Pinned to a corner: never dragged or resized by hand, never centred. The corner itself
        // is a setting, applied in ApplyPositionFromConfig.
        public override bool CanDragAndResize => !Doc.Pinned;
        protected override bool UsesCenterAnchors => !Doc.Pinned;

        protected override bool UseBackdrop => Doc.Backdrop;
        protected override bool PersistWindowPreferences => Doc.Persist;

        // The height is a rule — what the visible boxes add up to, see AdjustHeight — never the
        // base's dynamic sizing.
        protected override bool UseDynamicSizing => false;

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
        private Host _webNotifBox;
        private LabelHandle _webNotifTitle;
        private ButtonHandle _webNotifViewBtn;

        /// <summary>The notifications shown in this corner, of all the site sent — what Dismiss puts away.</summary>
        private readonly List<string> _webNotifShown = new List<string>();

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

        /// <summary>
        /// The screen is overlay.json; this builds it and keeps hold of what the code writes or
        /// shows. What the document carries, and why — a document has no comments:
        /// - one stack, its spacing and padding declared there and READ here (AdjustHeight sizes
        ///   the window from them, never from a private copy that drifts);
        /// - five boxes and a toast, all hidden at first: the mod update (Download before Get
        ///   Manager: whoever came for a zip finds the zip first, the Manager stays beside it,
        ///   Secondary on purpose), the sync notice with its five verbs (Branch, Fork, the action,
        ///   Compare in the place it holds on the main panel, Settings, Ignore last), the website
        ///   notification, the AI queue, the connection line;
        /// - the connection line has no vertical padding: it says one thing on one line, and room
        ///   around it pushes everything else up the screen for nothing. Its dot (U+25CF, the mark
        ///   the status card uses) comes after the words so it sits against the right edge, with
        ///   the width of its own glyph so the words stay flush against it — the dot is the state,
        ///   the words are the courtesy;
        /// - no title bar, no backdrop, nothing remembered: it is a corner, not a window.
        /// </summary>
        protected override void ConstructPanelContent()
        {
            TitleBarHost.Visible = Doc.TitleBar;

            // No scrolling body and no footer here: the document's one stack goes straight into
            // the content, and it is what the window is sized to.
            _screen = ScreenBuilder.Build(Doc, Content, Content, ActOf);

            _stack = _screen.Host("OverlayStack");

            _modUpdateBox = _screen.Host("ModUpdateBox");
            _modUpdateLabel = _screen.Label("ModUpdateLabel");
            _modManagerHint = _screen.Label("ModManagerHint");
            _modUpdateBtn = _screen.Button("ModDownloadBtn");
            _modManagerBtn = _screen.Button("ModManagerBtn");

            _syncBox = _screen.Host("SyncBox");
            _syncLabel = _screen.Label("SyncLabel");
            _syncBranchBtn = _screen.Button("SyncBranchBtn");
            _syncForkBtn = _screen.Button("SyncForkBtn");
            _syncActionBtn = _screen.Button("SyncActionBtn");
            _syncCompareBtn = _screen.Button("SyncCompareBtn");
            _syncHintLabel = _screen.Label("SyncHintLabel");

            _webNotifBox = _screen.Host("WebNotifBox");
            _webNotifTitle = _screen.Label("WebNotifTitle");
            _webNotifViewBtn = _screen.Button("WebNotifViewBtn");

            _aiBox = _screen.Host("AIBox");
            _aiStatusLabel = _screen.Label("AIStatusLabel");
            _aiQueueLabel = _screen.Label("AIQueueLabel");

            _connectionBox = _screen.Host("ConnectionBox");
            _connectionLabel = _screen.Label("ConnectionLabel");
            _connectionDot = _screen.Label("ConnectionDot");

            _toast = _screen.Toast("ToastBox");

            // Start hidden and with update
            RefreshOverlay();
        }

        /// <summary>What each verb the document asks for does. A verb with no answer here fails at construction, not at the click.</summary>
        private Action ActOf(string act)
        {
            switch (act)
            {
                case "modDownload": return OnModUpdateClicked;
                case "modManager": return OnModManagerClicked;
                case "modIgnore": return OnModIgnoreClicked;
                case "syncBranch": return OnSyncBranchClicked;
                case "syncFork": return OnSyncForkClicked;
                case "syncAction": return OnSyncActionClicked;
                case "syncCompare": return OnSyncCompareClicked;
                case "syncSettings": return OnSyncSettingsClicked;
                case "syncIgnore": return OnSyncIgnoreClicked;
                case "webNotifView": return OnWebNotifViewClicked;
                case "webNotifDismiss": return OnWebNotifDismissClicked;
                default: return null;
            }
        }

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

            // A long message wraps: the window takes the toast's height, not whatever it had.
            AdjustHeight();
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

            // ⚠ Asked to leave, then seen out. The box stays visible while it fades, so the refresh
            // that brings the other boxes back waits for it to be actually gone — bringing them
            // back over a fading toast would be two things on screen saying different states.
            if (Clock.Now >= _toastHideTime) _toast.BeginHide();

            if (!_toast.Tick()) RefreshOverlay();
        }

        /// <summary>
        /// Show/hide the website notifications box from the latest poll result.
        /// </summary>
        public void RefreshNotificationsBox()
        {
            if (_webNotifBox == null) return;

            var result = TranslatorUIManager.WebsiteNotifications;

            // 🔴 **Current is current** (the user, 2026-09-17). This corner speaks of the translation
            // the game HOLDS and of nothing else. A contribution sent, closed, merged or orphaned
            // on another lineage — even another lineage of this same game — is that lineage's
            // business: it is read on its row of the community list, or on the site. Announced
            // here it read as a fact about the translation in use, and the reader, Main of the
            // one installed, was told their Main had been removed.
            //
            // ⚠ And what the sync box already states about the installed one is not said a second
            // time by the site's copy: a wall (the card states the fact, Fork is the way out) and
            // contributions waiting for review (the sync box has its Review). What is left for the
            // site to say here is what the game cannot know on its own — lines of the installed
            // contribution merged by its Main — and what is not about a lineage at all.
            _webNotifShown.Clear();
            ModNotificationItem first = null;
            if (result != null)
            {
                foreach (var item in result.Items)
                {
                    if (!ThisCornerSays(item)) continue;
                    if (first == null) first = item;
                    _webNotifShown.Add(item.Id);
                }
            }

            bool show = !TranslatorUIManager.WebsiteNotificationsDismissed && first != null;
            _webNotifBox.Visible = show;
            if (show)
            {
                // Comes from the website, so it may carry line breaks — same one-line rule
                string text = Flatten(first.Text);
                if (_webNotifShown.Count > 1)
                {
                    text += " " + Tr($"(+{_webNotifShown.Count - 1} more)");
                }
                _webNotifTitle.Show(text);
                _webNotifViewBtn.Visible = first.Url != null;
            }

            // A box that appeared or went, a sentence that may wrap: the window is sized again.
            AdjustHeight();
        }

        /// <summary>
        /// Whether a notification of the site belongs in this corner: not about a lineage at all
        /// (an announcement), or about the lineage this game holds and not already stated by the
        /// game itself. A server that predates the lineage field says nothing about it, and
        /// nothing is shown rather than a guess.
        /// </summary>
        private static bool ThisCornerSays(ModNotificationItem item)
        {
            if (item.Type == "announcement") return true;
            if (item.Uuid == null || !TranslatorCore.IsUuidMatch(item.Uuid)) return false;
            // Told by the sync box already: a wall, and contributions waiting for review.
            if (item.IsWall || item.Type == "branch_submitted") return false;
            return true;
        }

        private void OnWebNotifViewClicked()
        {
            var result = TranslatorUIManager.WebsiteNotifications;
            string url = null;
            if (result != null && _webNotifShown.Count > 0)
            {
                foreach (var item in result.Items)
                    if (item.Id == _webNotifShown[0]) { url = item.Url; break; }
            }
            TranslatorCore.OpenUrlSafe(url ?? $"{ApiClient.WebsiteBaseUrl}/notifications");
        }

        /// <summary>Puts away what this corner showed — and only that: the rest was never this game's to read.</summary>
        private void OnWebNotifDismissClicked()
        {
            TranslatorUIManager.MarkWebsiteNotificationsRead(new List<string>(_webNotifShown));
            if (_webNotifBox != null) _webNotifBox.Visible = false;
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

            bool showSyncNotification = !_panelsOpenMode && pending.Any &&
                                        !TranslatorUIManager.NotificationDismissed;

            if (showSyncNotification && _syncBox != null)
            {
                _syncBox.Visible = true;

                // 🔴 **What this corner says, and what its button does, are the socle's** —
                // Notices.Sync, held by the corpus (`notices`), on the standing every other screen
                // reads. This chain used to compose the socle's rules with its own glue, two
                // hundred lines beside a main screen answering the same questions its own way.
                var work = new SyncWork
                {
                    WaitingForAccount = pending.WaitingForAccount,
                    HasLocalChanges = pending.HasLocalChanges,
                    HasMetadataChanges = pending.HasMetadataChanges,
                    HasServerUpdate = pending.HasServerUpdate,
                    NeedsMerge = pending.NeedsMerge,
                    HasMainUpdate = pending.HasMainUpdate,
                    BranchesPendingReview = pending.BranchesPendingReview,
                };
                var standing = StandingFacts.Now(out var local, out var server, out var account);
                var notice = Notices.Sync(work, standing, local, server, account);

                // Translated as it is built. Counts stay inline (the pipeline replaces numbers with
                // placeholders, so every count shares one cache entry); the person named is
                // appended, never sent for translation — in the one form the ecosystem uses.
                string message = Tr(notice.Message);
                if (notice.Mention != null)
                    message += " " + People.MentionOf(notice.Mention, TranslatorCore.Config.api_user);
                _syncLabel.Show(message);

                // Somebody else's lineage is offered a CHOICE — contribute (branch) or go
                // independent (fork) — in place of the one action button.
                bool choosing = notice.Action == SyncAction.ChooseBranchOrFork;
                bool showAction = !choosing;

                // ⚠ Absent rather than greyed, and the two are decided differently on purpose:
                // greyed says "later" — sign in, come back online — while a lineage that refuses
                // contributions is not a "later", it is a road that does not exist. The hint below
                // says which wall it is, in the socle's words.
                if (_syncBranchBtn != null) _syncBranchBtn.Visible = choosing && notice.OffersBranch;
                if (_syncForkBtn != null) _syncForkBtn.Visible = choosing;
                if (_syncActionBtn != null) _syncActionBtn.Visible = showAction;

                // 🔴 **It turns into the way out, it does not go away.** It used to be hidden while
                // a comparison was in flight, on the reasoning that this row is crowded and the way
                // out could live on the main panel. Two things were wrong with that: a control
                // vanishing at the moment it is clicked reads as the click having failed, and the
                // same fact then said two different things on two screens — which is the one thing
                // an ecosystem may not do. Whoever opened it from here closes it from here.
                RefreshCompareButton();

                // The refusal is said before the click, not after the form — the same rule the
                // main screen greys its own Upload on, for each door separately.
                if (choosing)
                {
                    if (_syncBranchBtn != null && notice.OffersBranch) _syncBranchBtn.Enabled = notice.BranchClosed == null;
                    if (_syncForkBtn != null) _syncForkBtn.Enabled = notice.ForkClosed == null;
                }

                if (_syncHintLabel != null)
                {
                    // ⚠ The reason REPLACES the half it is about, and never both: a fork asks for
                    // neither an account nor the network, so it stays open and stays explained —
                    // it is the way on precisely when the other half is shut.
                    string hint = "";
                    if (choosing)
                    {
                        // The wall first when there is one — it is why the Branch button is not
                        // there, and it names somebody, so it is written as it is — then whatever
                        // is left to say about the way on.
                        //
                        // ⚠ The reason is prefixed with the button it is ABOUT. On its own,
                        // "Login required" reads as a statement of fact beside a greyed button —
                        // it never says that signing in is what turns that button back on. The
                        // other half already names itself the same way.
                        string branchHalf = notice.Wall != null
                            ? notice.Wall
                            : notice.BranchClosed != null
                                ? Tr("Branch:") + " " + Tr(notice.BranchClosed)
                                : Tr("Branch: send them for review to") + " "
                                  + People.MentionOf(server.Uploader, TranslatorCore.Config.api_user);

                        hint = branchHalf + " • "
                               + (notice.ForkClosed != null
                                   ? Tr(notice.ForkClosed)
                                   : Tr("Fork: keep them as your own translation"));
                    }

                    _syncHintLabel.Show(hint);
                    _syncHintLabel.Visible = choosing;
                }

                _syncActionIsUpstream = notice.Action == SyncAction.MergeFromMain;
                _syncActionIsReview = notice.Action == SyncAction.Review;

                if (showAction && notice.Verb != null)
                {
                    _syncActionBtn.Label = notice.Verb;
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
                    // 🔴 A counter ONLY while a line is being asked for again. The first try is not
                    // a retry and shows nothing; the Core lowers the count when a line ends and
                    // when the next one starts, so what is read here is always about the line named
                    // beside it — never left over from the one before.
                    int attempt = TranslatorCore.RetryAttempt;
                    string again = attempt > 0 ? $" ({attempt}/{TranslatorCore.RetryTotal})" : "";

                    if (TranslatorCore.CurrentTextIsOwnUI)
                    {
                        _aiStatusLabel.Say(attempt > 0
                            ? $"Retrying the interface{again}..."
                            : "Translating the interface...");
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
                        _aiStatusLabel.Show(attempt > 0
                            ? Tr("Retrying") + $"{again}: {text}"
                            : Tr("Translating:") + $" {text}");
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

            // 🔴 Every box of the stack, the site's notification included: left out of this list,
            // it was drawn without a height of its own, over the buttons of the box above it.
            foreach (var box in new[] { _modUpdateBox, _syncBox, _webNotifBox, _aiBox, _connectionBox, _toast?.Handle })
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
        // 🔴 **Declared ONCE, in the document, and read here** — never written twice. AdjustHeight
        // has to know them to size the window, and an earlier version kept its own copy, which
        // said ten and forgot the five between each pair, so three notifications came out ten
        // pixels short and the last line of one was cut across the middle.
        private static ScreenNode Stack => Doc.Nodes["OverlayStack"];
        private static int StackPadding => 2 * (Stack.Int("pad") ?? 0);   // above the first box and below the last, together
        private static int StackSpacing => Stack.Int("spacing") ?? 0;      // between each pair


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
            if (_webNotifBox != null && _webNotifBox.Visible) height += 60;
            if (_aiBox != null && _aiBox.Visible) height += 50;
            if (_connectionBox != null && _connectionBox.Visible) height += 20;
            if (_toast != null && _toast.Visible) height += 50;
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
                    Intents.OpenUpload();
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
                        Intents.OpenUpload();
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
                Intents.ShowMain();
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
                await TranslatorUIManager.OpenComparison(siteId.Value, toLocal: TranslatorUIManager.ComparisonGoesToLocal);
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
            Intents.ShowMain();
        }

        /// <summary>
        /// Handler for Branch button - contribute to the main translation.
        /// Opens upload panel which will create a branch.
        /// </summary>
        private void OnSyncBranchClicked()
        {
            // Open upload panel - it will detect we're contributing and handle branch creation
            Intents.OpenUpload();
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
