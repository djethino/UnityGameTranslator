using System;
using UniverseLib.UI;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Layout states for context-aware UI display.
    /// </summary>
    /// <summary>
    /// Layout states for context-aware UI display.
    ///
    /// 🔴 **These describe the TRANSLATION, never the account.** There used to be a `NotLogged`
    /// state, tested before every other, which meant somebody with no account was shown a sign-up
    /// pitch instead of what their translation was doing. Whether to invite somebody to sign in is
    /// a separate question, read from the account where it is needed.
    ///
    /// ⚠ **No state is called "Contributor".** Becoming a Branch IS contributing, so a second word
    /// for it would put two names on one thing across three products — and this state is not a
    /// Branch anyway: it is somebody holding a lineage that is not theirs, who has diverged and
    /// sent nothing. One becomes a Branch by uploading.
    /// </summary>
    /// <summary>
    /// Main settings panel. Shows translation status, account info, sync status, and action buttons.
    /// Context-aware layout adapts to user state.
    /// </summary>
    public class MainPanel : TranslatorPanelBase
    {
        /// <summary>The screen as a document — common/spec/screens/main.json — read once; the base's constructor reads the sizes below through it.</summary>
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("main");

        /// <summary>What the builder made of the document: every piece by name.</summary>
        private BuiltScreen _screen;

        public override string Name => Doc.Name;

        // ⚠ **The extra 30 is the scrollbar's.** The cards inside are sized from the document's
        // cardWidth once, at construction; the viewport is NOT, because DynamicScrollbar takes 28
        // pixels off it the moment the content is long enough to scroll. At 450 the two figures
        // crossed and labels lost their last characters — only on the screens long enough to
        // scroll, which is why it looked intermittent.
        //
        // 🔴 **580, and both raises were measured rather than guessed.** The figure is main.json's;
        // the reasons are here because a document carries no comment.
        //
        // 480 -> 520: two adorned buttons do not fit in 480. The fitter's own trace reports
        // "Upload Translation" at 199 and "Review on Website" at 200 once their scope marks are
        // counted, and the card at that minimum offered 352 — the row fitted by about a pixel on
        // this machine's font and not at all on the next, which is exactly the kind of margin that
        // reads as an intermittent bug.
        //
        // 520 -> 580: the contributions line is one line by design — "59 to review: 24 new (H 21,
        // A 3) · 35 differing (V 17, A 18)" — and it grows with the work: a quality more on either
        // side adds a chip and its count. At 520 it fitted only while both groups held two
        // qualities. A row that is one line ON PURPOSE has to be given the width that keeps it one.
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

        // The Community tab embeds a scrollable translation list that benefits from
        // extra room when the user enlarges the window.
        protected override bool HasFlexibleContent => true;

        // UI references - Account section
        private LabelHandle _accountLabel;
        private ButtonHandle _loginLogoutBtn;

        // UI references - Translation info section (legacy, hidden when StatusCard is shown)
        private Host _translationInfoSection;
        private LabelHandle _entriesLabel;
        private LabelHandle _targetLabel;
        private LabelHandle _sourceLabel;
        private LabelHandle _roleLabel;
        private LabelHandle _syncStatusLabel;
        private LabelHandle _aiStatusLabel;

        // UI references - Resources link
        private Host _resourcesLinkSection;
        private LabelHandle _backupsLabel;
        private LabelHandle _resourcesByLabel;
        private LabelHandle _resourcesUrlLabel;
        private ButtonHandle _resourcesLinkBtn;

        // UI references - Actions section
        private ButtonHandle _uploadBtn;
        private LabelHandle _uploadHintLabel;
        private ButtonHandle _reviewOnWebsiteBtn;
        private ButtonHandle _compareWithServerBtn;
        private ButtonHandle _transParamsBtn;
        private ButtonHandle _optionsBtn;
        private ButtonHandle _backupsBtn;
        private ButtonHandle _editDetailsBtn;
        private ButtonHandle _updateFromMainBtn;
        private bool _updateFromMainInFlight;
        private ButtonHandle _forkBtn;
        private LabelHandle _roleActionsHint;
        private Components.HelpZone _helpZone;

        // UI references - Community Translations section
        private Host _communitySection;
        private LabelHandle _communityGameLabel;
        private ButtonHandle _searchBtn;
        private TranslationList _translationList;
        private ButtonHandle _downloadBtn;

        // UI references - Context-aware sections
        private StatusCard _statusCard;
        private Host _loginCTASection;
        private ButtonHandle _loginCTABtn;
        private Host _statusSection;

        /// <summary>
        /// The way into this translation's history.
        ///
        /// 🔴 **Held because it outlives the card above it.** It sits in the status section, which
        /// used to be hidden whole when there is no translation — so the backups taken by whatever
        /// removed that translation became unreachable from the game, and the only way back was a
        /// file manager. It now decides its own fate: see <see cref="RefreshBackupsLine"/>.
        /// </summary>
        private Host _backupsRow;

        // UI references - the three choices when holding another lineage (GAP 8)
        private Host _lineageChoiceSection;
        /// <summary>
        /// The two rows of Actions, held so they can be hidden when nothing in them is showing.
        ///
        /// 🔴 **A row keeps its height with every child hidden.** Each is built at RowHeightLarge,
        /// so a state that switches all of their buttons off left two empty bands inside the card —
        /// one above the visible controls and one below. Turning off a button is not turning off
        /// the space it was standing in.
        /// </summary>
        /// <summary>The three rows and their sentences, so each can say why it is closed.</summary>
        private Host _branchRow;
        private Host _mergeRow;
        private Host _downloadRow;
        private Host _forkRow;
        private LabelHandle _forkDesc;
        private ButtonHandle _mergeWithMainBtn;
        private LabelHandle _mergeDesc;
        private LabelHandle _branchDesc;
        private LabelHandle _downloadDesc;

        private Host _syncActionsRow;
        private Host _roleActionsRow;

        private ButtonHandle _contributeAsBranchBtn;
        private ButtonHandle _downloadLatestBtn;
        private ButtonHandle _createIndependentBtn;

        // UI references - Guidance messages (GAP 9)
        private Host _guidanceSection;
        private LabelHandle _guidanceLabel;

        // UI references - Mod update banner
        private Host _modUpdateBanner;
        private LabelHandle _modUpdateLabel;
        private ButtonHandle _modUpdateBtn;
        private ButtonHandle _modManagerBtn;

        // Tab system
        private TabBar _tabBar;
        // The words on the two tabs are the document's; the code names the pieces.
        private static string TAB_MY_TRANSLATION => Doc.Nodes["MyTranslationTab"].Text;
        private static string TAB_COMMUNITY => Doc.Nodes["CommunityTab"].Text;

        /// <summary>
        /// Where this translation stands, as last read from the facts — the socle's four questions,
        /// answered once per redraw and read by every section.
        ///
        /// 🔴 **One reading, not two** (2026-09-16). A layout state used to be derived from the
        /// server state here, and a Standing rebuilt from the same state PLUS that layout state in
        /// the card — and the two disagreed: the Standing never carried MainMissing, so the chip for
        /// a vanished Main never appeared in a game. `Standings.From` fills every field or none.
        /// </summary>
        private Standing _standing;

        /// <summary>The three fact sheets the standing was read from — what the button is judged on.</summary>
        private LocalFacts _local;
        private ServerFacts _server;
        private AccountFacts _account;

        public MainPanel(UIBase owner) : base(owner)
        {
            // Note: Components initialized in ConstructPanelContent() - base constructor calls ConstructUI() first
        }

        /// <summary>
        /// The screen is main.json; this builds it and keeps hold of what the code writes or shows.
        ///
        /// What the document carries, and why — a document has no comments, so the reasons the
        /// layout was argued into its shape stay here:
        /// - a section's title sits OUTSIDE the frame it names ("Current Translation", "Actions",
        ///   "Community Translations"): a heading names what follows, it is not part of it;
        /// - the three lineage choices live inside Actions with no heading of their own: they had
        ///   one, directly under a row already offering "Contribute" — one question asked twice,
        ///   and the copy with the heading was the one without the guards;
        /// - every action button is the size of its label and centred with its sentence; the one
        ///   that stretches (Open in Browser) answers for its whole block;
        /// - Merge with Main sits above Take Main's version, the Manager's order: the safe act is
        ///   met first, the one that drops work is read second, next to the sentence saying so;
        /// - Create Independent is not red: red is for something wrong or refused, and a copy of a
        ///   translation is the third of three legitimate answers;
        /// - on the update banner, Get Manager comes before Download: read left to right, the tool
        ///   that does the whole job comes first and the manual zip stays beside it;
        /// - the Community tab's Download is under its own list, not in the footer: that row
        ///   carries what applies to the whole mod on every tab, and a fourth button pushed Close
        ///   off its edge;
        /// - Backups is one line and a way in, in the status section: backups are the history of
        ///   the very thing that section shows, and the list lives in its own panel;
        /// - the footer's three buttons concern the whole mod and belong to every tab.
        ///
        /// ⚠ Two components with a document of their own — the status card and the community
        /// list, parts under common/spec/screens/parts — are built here into the two hosts the
        /// document leaves empty.
        /// </summary>
        protected override void ConstructPanelContent()
        {
            _translationList = new TranslationList();

            // The panel's chrome: the scrolling body, the fixed footer, the help bar between them,
            // the fixed header. What goes in them is the document's.
            Layout(out var body, out var footer, Doc.Width - 40);
            _helpZone = CreateHelpZone(footer, Doc.Help);
            _screen = ScreenBuilder.Build(Doc, body, footer, ActOf, header: FixedHeader(), help: _helpZone,
                                          layoutChanged: RecalculateSize);

            _tabBar = _screen.Tabs("Tabs");

            _accountLabel = _screen.Label("AccountLabel");
            _loginLogoutBtn = _screen.Button("LoginLogoutBtn");

            _modUpdateBanner = _screen.Host("ModUpdateBanner");
            _modUpdateLabel = _screen.Label("ModUpdateLabel");
            _modManagerBtn = _screen.Button("ModManagerBtn");
            _modUpdateBtn = _screen.Button("ModUpdateBtn");

            _loginCTASection = _screen.Host("LoginCTASection");
            _loginCTABtn = _screen.Button("CTALoginBtn");

            _statusSection = _screen.Host("StatusSection");
            _backupsRow = _screen.Host("BackupsRow");
            _backupsLabel = _screen.Label("BackupsLabel");
            _backupsBtn = _screen.Button("BackupsBtn");
            _resourcesLinkSection = _screen.Host("ResourcesLinkSection");
            _resourcesByLabel = _screen.Label("ResourcesByLabel");
            _resourcesUrlLabel = _screen.Label("ResourcesUrlLabel");
            _resourcesLinkBtn = _screen.Button("ResourcesOpenBtn");

            _translationInfoSection = _screen.Host("TranslationInfoSection");
            _entriesLabel = _screen.Label("EntriesLabel");
            _targetLabel = _screen.Label("TargetLabel");
            _sourceLabel = _screen.Label("SourceLabel");
            _roleLabel = _screen.Label("RoleLabel");
            _syncStatusLabel = _screen.Label("SyncStatusLabel");
            _aiStatusLabel = _screen.Label("AIStatusLabel");

            _syncActionsRow = _screen.Host("SyncActionsRow");
            _uploadBtn = _screen.Button("UploadBtn");
            _compareWithServerBtn = _screen.Button("CompareBtn");
            _lineageChoiceSection = _screen.Host("LineageChoiceSection");
            _branchRow = _screen.Host("BranchRow");
            _contributeAsBranchBtn = _screen.Button("ContributeBtn");
            _branchDesc = _screen.Label("BranchDesc");
            _mergeRow = _screen.Host("MergeRow");
            _mergeWithMainBtn = _screen.Button("MergeWithMainBtn");
            _mergeDesc = _screen.Label("MergeDesc");
            _downloadRow = _screen.Host("DownloadRow");
            _forkRow = _screen.Host("ForkRow");
            _forkDesc = _screen.Label("ForkDesc");
            _downloadLatestBtn = _screen.Button("DownloadLatestBtn");
            _downloadDesc = _screen.Label("DownloadDesc");
            _createIndependentBtn = _screen.Button("CreateIndependentBtn");
            _uploadHintLabel = _screen.Label("UploadHintLabel");
            _roleActionsRow = _screen.Host("RoleActionsRow");
            _reviewOnWebsiteBtn = _screen.Button("ReviewBtn");
            _editDetailsBtn = _screen.Button("EditDetailsBtn");
            _updateFromMainBtn = _screen.Button("UpdateFromMainBtn");
            _forkBtn = _screen.Button("ForkBtn");
            _roleActionsHint = _screen.Label("RoleActionsHint");

            _guidanceSection = _screen.Host("GuidanceSection");
            _guidanceLabel = _screen.Label("GuidanceLabel");

            _communitySection = _screen.Host("CommunitySection");
            _communityGameLabel = _screen.Label("GameLabel");
            _searchBtn = _screen.Button("SearchBtn");
            _downloadBtn = _screen.Button("DownloadBtn");

            _transParamsBtn = _screen.Button("TransParamsBtn");
            _optionsBtn = _screen.Button("OptionsBtn");

            // The status card: what the translation IS, in the host at the top of its section.
            _statusCard = new StatusCard();
            _statusCard.CreateUI(_screen.Host("StatusCardHost"));
            _helpZone.Describe(_statusCard.Handle,
                "Your translation at a glance: sync state with the website, your role (Main = owner, Branch = contributor), and quality (Human / Validated / AI lines)");

            // The community list, in the host between the search row and its own action row —
            // the list takes the spare height and that row keeps its own.
            _translationList.CreateUI(_screen.Host("TranslationListHost"), onSelectionChanged: (t) =>
            {
                if (_downloadBtn != null)
                {
                    _downloadBtn.Enabled = t != null;
                    SetCommunityDownloadState(t != null);
                }
            }, help: _helpZone);
            _downloadBtn.Enabled = false;
            SetCommunityDownloadState(false);

            RefreshUI();
        }

        /// <summary>What each verb the document asks for does. A verb with no answer here fails at construction, not at the click.</summary>
        private Action ActOf(string act)
        {
            switch (act)
            {
                case "loginLogout": return OnLoginLogoutClicked;
                case "modManager": return OnModManagerClicked;
                case "modUpdate": return OnModUpdateClicked;
                case "ctaLogin": return () => Intents.OpenLogin();
                case "backups": return () =>
                {
                    if (Intents.IsOpen(ScreenId.Backups)) Intents.Close(ScreenId.Backups);
                    else Intents.OpenBackups();
                };
                case "resourcesOpen": return OnResourcesLinkClicked;
                case "upload": return OnUploadClicked;
                case "compare": return OnCompareWithServerClicked;
                case "contribute": return OnContributeAsBranchClicked;
                // ⚠ One act behind two buttons, never two copies of it: a Branch's "Merge with
                // Main" and the lineage choice's are never on screen at once, and one guard helper
                // drives both, so they cannot drift the way two fork buttons once did.
                case "mergeWithMain":
                case "updateFromMain": return OnUpdateFromMainClicked;
                case "downloadLatest": return OnDownloadLatestClicked;
                // Same thing for the two ways into a fork.
                case "createIndependent":
                case "fork": return OnCreateIndependentClicked;
                case "review": return OnReviewOnWebsiteClicked;
                case "editDetails": return OnEditDetailsClicked;
                case "search": return OnSearchCommunityClicked;
                case "download": return OnDownloadCommunityClicked;
                // The footer's three concern the whole mod. ⚠ Kept as fields: a button that opens
                // a window has to be told, afterwards, that the window is there — and that it is
                // gone again. See RefreshOpenerStates.
                case "transParams": return () => Intents.Toggle(ScreenId.TranslationParameters);
                case "options": return () => Intents.Toggle(ScreenId.Options);
                case "close": return () => SetActive(false);
                default: return null;
            }
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

            // Looked at again next time this banner is drawn: somebody who just went to fetch it
            // may come back with it installed, and the button should stop offering what they have.
            ManagerLink.Forget();
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

        public override void SetActive(bool active)
        {
            // Only refresh when transitioning from inactive to active
            // (PanelDragger calls SetActive(true) every frame when mouse is in drag/resize area)
            bool wasActive = Enabled;
            base.SetActive(active);
            if (active && !wasActive)
            {
                RefreshUI();

                // 🔴 **Opening this panel IS the moment to ask about the lineage.** What other
                // people did — a contribution arriving, a Main deleted or gone silent — follows the
                // rhythm the player chose, which can be six hours; and a stream deliberately does
                // not carry it. So the one screen where those facts are read asks for them itself.
                //
                // ⚠ It matters most where a wrong answer costs the most: a contributor whose Main
                // has been deleted must be told here, not when they finally try to publish. Cheap
                // when nothing changed — the site answers from a cache keyed on the files' hashes.
                TranslatorUIManager.RefreshLineageNow();

                // Reserve the height of the TALLEST tab, like the other tabbed panels do.
                // Without it the panel was sized for whichever tab happened to be open, so
                // switching to Community resized the whole window under the user.
                KeepPanelHeightAcrossTabs(_tabBar);

                // Auto-search community translations if conditions are met
                TryAutoSearchCommunity();
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
                    _downloadBtn.Enabled = _translationList?.SelectedTranslation != null;
                    SetCommunityDownloadState(_translationList?.SelectedTranslation != null);
                }
            });
        }

        /// <summary>
        /// Where this translation stands, read off the facts the engine holds — the socle composes
        /// it, StandingFacts gathers what it asks for, and the sheets are kept beside it for the
        /// button that is judged on them.
        /// </summary>
        private void ReadFacts()
        {
            _standing = StandingFacts.Now(out _local, out _server, out _account);
        }

        /// <summary>How many lines the screen last said the translation holds.</summary>
        private int _shownEntries = -1;

        /// <summary>
        /// A translation grows on its own — the worker adds a line every time the game shows a new
        /// text — and nothing announces it: the screen said "Entries: 0" and greyed Upload until it
        /// was closed and opened again. So the count is followed, and a change redraws the screen
        /// as any other event does. Reconciled from the state rather than wired to the writer,
        /// which has several and would need every one of them to remember.
        /// </summary>
        public override void FollowFacts()
        {
            int entries = TranslatorCore.TranslationCache?.Count ?? 0;
            if (entries == _shownEntries) return;

            // Recorded before the redraw, so a redraw that cannot write the count yet does not
            // become a redraw on every tick.
            _shownEntries = entries;

            // ⚠ Redrawn from the facts already here, without asking the server again: a line
            // arrives every few seconds while a translation is being made, and a check that has
            // not answered yet would otherwise be asked for again on each of them.
            RedrawFromFacts();
        }

        public void RefreshUI()
        {
            // Whoever set update checks to "Never" still needs a panel that knows
            // its role, its site id and whether an upload is possible — those come
            // from the same sync state. Fetched on demand here, so the choice
            // silences notifications without blinding the interface.
            TranslatorUIManager.EnsureServerStateKnown();

            // The community rows say what each lineage is to the account; a new account, new rows.
            _translationList?.Refresh();

            RedrawFromFacts();
        }

        /// <summary>Every section, from what the engine holds right now.</summary>
        private void RedrawFromFacts()
        {
            // Where the translation stands, read once for every section below.
            ReadFacts();

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
            _modUpdateBanner.Visible = showBanner;

            if (showBanner)
            {
                var info = TranslatorUIManager.ModUpdateInfo;
                // Version number appended: it is data, and it changes with every release
                _modUpdateLabel.Show(Tr("Mod update available:") + $" v{info?.LatestVersion ?? "?"}");

                // Show appropriate button text
                bool hasDirectDownload = !string.IsNullOrEmpty(info?.DownloadUrl);
                _modUpdateBtn.Label = hasDirectDownload ? "Download" : "View Release";

                // The verb follows what pressing it will do: open what is already on this machine,
                // or go and fetch it.
                _modManagerBtn.Label = ManagerLink.IsOnThisMachine ? "Open Manager" : "Get Manager";
            }
        }

        /// <summary>
        /// Updates section visibility based on current layout state.
        /// </summary>
        private void RefreshLayoutVisibility()
        {
            // ⚠ Read from the account directly, no longer from the layout state. The two were the
            // same test and are two different questions: whether to invite somebody to sign in, and
            // what their translation is doing. Tying them meant an invitation REPLACED the answer.
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);

            if (_loginCTASection != null)
            {
                _loginCTASection.Visible = !isLoggedIn;

                // Disable CTA button when offline
                if (_loginCTABtn != null)
                {
                    _loginCTABtn.Enabled = TranslatorCore.Config.online_mode;
                }
            }

            // The card describes a translation, so it appears whenever there is one — with or
            // without a name attached to it.
            bool showStatusCard = _standing.Publication != Publication.NotDownloaded;

            // 🔴 **The card goes, the section stays.** Hiding the whole section took the way into
            // Backups with it — and "there is no translation" is exactly the state in which somebody
            // needs it, since removing one is what took the last backup. So the card hides itself,
            // the external-resources block follows it (it describes that same translation), and the
            // backups line decides on its own in RefreshBackupsLine.
            //
            // ⚠ The manager already did this and said why — see its TranslationWorkbench: "the way
            // back survives having nothing to work on". The mod was the one product left without it.
            if (_statusCard != null)
            {
                _statusCard.SetVisible(showStatusCard);
            }

            if (!showStatusCard && _resourcesLinkSection != null)
            {
                _resourcesLinkSection.Visible = false;
            }

            if (showStatusCard)
            {
                RefreshStatusCard();
            }
            else
            {
                // RefreshStatusCard would have done it; with no card there is nobody else to ask.
                RefreshBackupsLine();
            }

            // Legacy TranslationInfo section - hide when StatusCard is shown
            if (_translationInfoSection != null)
            {
                _translationInfoSection.Visible = !showStatusCard;
            }

            // The three choices offered to somebody holding a lineage that is not theirs.
            if (_lineageChoiceSection != null)
            {
                bool choosing = _standing.Publication == Publication.NotYours;

                // 🔴 **An owner whose published copy moved elsewhere has one thing to do here:
                // take it in.** The section used to be the "not yours" choice only, so a Main
                // published from another machine left this one with a closed Upload and a way in
                // that lived in the corner notification alone. The Download row is that way in,
                // for the owner too; the three other rows stay the stranger's.
                bool ownerBehind = _standing.Publication == Publication.Published
                                   && _standing.Sync == SyncDirection.Download;
                _lineageChoiceSection.Visible = choosing || ownerBehind;
                if (_mergeRow != null) _mergeRow.Visible = choosing;
                if (_mergeDesc != null) _mergeDesc.Visible = choosing;
                if (_forkRow != null) _forkRow.Visible = choosing;
                if (_forkDesc != null) _forkDesc.Visible = choosing;

                // 🔴 **The upload button steps aside for them.** In this state it reads
                // "Contribute" and does the same thing as the first of the three below it — two
                // doors to one act, three inches apart, and the hint under it ("Login required")
                // answered for a button nobody should have been looking at.
                if (_uploadBtn != null) _uploadBtn.Visible = !choosing;
                if (_uploadHintLabel != null) _uploadHintLabel.Visible = !choosing;

                // ⚠ **And the rows they were standing in.** Hiding every button in a row leaves the
                // row, which keeps its RowHeightLarge and shows as an empty band inside the card.
                _syncActionsRow?.HideIfEmpty();
                _roleActionsRow?.HideIfEmpty();

                // ⚠ Two of the three need a name, one does not. Taking the main's version again
                // writes only the local file, so it stays live without an account — that is the
                // whole point of showing this section to somebody signed out. Contributing and
                // forking put something on the site under a name, so they wait for one.
                // ⚠ Tint alongside interactable, every time. Unity greys a button's own image and
                // leaves its children alone, so the marks and the label keep whatever colour they
                // were built with — which left an accent-purple mark on a dead button.
                // 🔴 **Each of the three answers its own question, and two of them were answering
                // none.** Signed in was the whole test, so "Contribute as Branch" invited somebody
                // to send a file identical to the Main — a contribution holding nothing — and
                // "Take Main's version" offered to fetch a version that had not moved. Same guards as
                // the Actions row, which had them right all along: this section is the same acts,
                // laid out as a choice.
                bool canReachServer = isLoggedIn && TranslatorCore.Config.online_mode;
                bool haveSomethingToOffer = TranslatorCore.LocalChangesCount > 0
                                            || TranslatorCore.MetadataDirty;

                // 🔴 **Not shown at all when the Main refuses contributions.** That is its owner's
                // declaration, and the server enforces it — an upload into a lineage that does not
                // take branches is refused outright. Offering the act anyway is a door onto a no
                // somebody else already said.
                //
                // ⚠ Only on a stated refusal. AcceptsBranches is null on a server too old to send
                // it, and unknown is not "no": hiding the button there would take the act away over
                // a question nobody answered.
                bool refusesBranches = TranslatorCore.ServerState?.AcceptsBranches == false;
                if (_branchRow != null) _branchRow.Visible = choosing && !refusesBranches;
                if (_branchDesc != null) _branchDesc.Visible = choosing && !refusesBranches;

                if (_contributeAsBranchBtn != null && !refusesBranches)
                {
                    bool canContribute = canReachServer && haveSomethingToOffer;
                    _contributeAsBranchBtn.Enabled = canContribute;

                    // ⚠ **The sentence says why it is closed, or what it does.** Hiding the Actions
                    // row took its "Login required" with it, so a greyed button was left with a
                    // sentence describing an act nobody could take and no reason anywhere.
                    if (_branchDesc != null)
                    {
                        _branchDesc.Say(
                            !TranslatorCore.Config.online_mode ? "Offline mode — nothing is sent"
                            : !isLoggedIn ? "Login required"
                            : !haveSomethingToOffer
                                ? "Nothing to send yet: this file matches the main translation"
                                : "Your changes will help improve the main translation");
                    }
                }

                // 🔴 **The safe way to take the Main in, and it needs no account either.** It reads
                // a public file and writes local ones — nothing is sent. Offered on the same
                // condition as the row below it, because it answers the same question: something
                // upstream is worth taking. What separates them is what happens to YOUR lines.
                //
                // ⚠ Kept in step with the Branch's own Merge with Main by sharing its handler AND
                // its busy state, never by repeating the rule here.
                // The socle's verdict, the same one the card shows: the published version moved
                // (Download), or both did (Merge). Reading the manager's summary here was one
                // more derivation of the same fact.
                var upstream = _standing.Sync;
                bool upstreamWorthTaking = upstream == SyncDirection.Download
                                           || upstream == SyncDirection.Merge;

                if (_mergeWithMainBtn != null && !_updateFromMainInFlight)
                {
                    _mergeWithMainBtn.Enabled = upstreamWorthTaking;

                    if (_mergeDesc != null)
                    {
                        _mergeDesc.Say(upstreamWorthTaking
                            ? "Take in what the Main added — your own lines are kept"
                            : "Nothing new in the Main to take in");
                    }
                }

                // ⚠ Only when the published one actually moved. It writes the local file, so it
                // needs no account — but fetching a version identical to the one already here is
                // an act with no effect, and a button that promises one is worse than none.
                if (_downloadLatestBtn != null)
                {
                    bool serverMoved = upstreamWorthTaking;

                    _downloadLatestBtn.Enabled = serverMoved;
                    SetDownloadLatestState(serverMoved);

                    // The owner's own copy is "the site's", not "the Main's" — they ARE the Main.
                    _downloadLatestBtn.Label = ownerBehind ? "Download latest" : "Take Main's version";
                    if (_downloadDesc != null)
                    {
                        _downloadDesc.Say(ownerBehind
                            ? "Replaces this file with the copy on the site. Compare shows what changed first."
                            : serverMoved
                                ? "Replaces this file with the Main's — your own lines are dropped"
                                : "You already have the Main's version");
                    }
                }

                // 🔴 **No account, no network, no divergence — forking asks for none of them.**
                // CreateFork is local from end to end: a new uuid, the server state cleared, the
                // origin recorded. Nothing is sent. Publishing is a separate act the owner of a
                // fork takes when they want to, which is exactly what distinguishes it from a
                // branch — a branch does not exist until it is sent, and its Main learns of it then.
                //
                // Gating it on being signed in made this file's identity depend on an account,
                // which nothing about it does.
                if (_createIndependentBtn != null)
                {
                    _createIndependentBtn.Enabled = true;
                }
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

            switch (_standing.Publication)
            {
                case Publication.NotDownloaded:
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

                case Publication.NotYours:
                    // Somebody else's lineage - show info about parent
                    if (serverState != null)
                    {
                        int localChanges = TranslatorCore.LocalChangesCount;
                        if (localChanges > 0)
                        {
                            // Count inline (placeholdered), uploader appended as data
                            message = Tr($"You have {localChanges} changes compared to the translation of")
                                      + " " + People.MentionOf(serverState.Uploader,
                                                                  TranslatorCore.Config.api_user);
                        }
                    }
                    break;

                case Publication.NeverPublished:
                    // Local only — said once the server has confirmed it knows nothing of it
                    if (serverState != null && serverState.Checked && !serverState.Exists)
                    {
                        message = "Your translation is local only. Upload it to share with the community!";
                    }
                    break;
            }

            // ⚠ The invitation to sign up is appended, never substituted. It used to BE the message
            // for anybody without an account, which is how somebody holding a diverged community
            // translation was told to create an account instead of being told they had diverged.
            if (message is null && string.IsNullOrEmpty(TranslatorCore.Config.api_token)
                && localCount > 0)
            {
                message = "Create an account to publish your translation or contribute to the community.";
            }

            // Show or hide guidance section based on message
            bool hasMessage = !string.IsNullOrEmpty(message);
            _guidanceSection.Visible = hasMessage;
            if (hasMessage)
            {
                // The NotYours branch already translated (it appends a username); the others are
                // plain sentences translated here.
                if (_standing.Publication == Publication.NotYours)
                    _guidanceLabel.Show(message);
                else
                    _guidanceLabel.Say(message);
            }
        }

        /// <summary>
        /// Updates the StatusCard with current translation state.
        /// </summary>
        /// <summary>
        /// How much history this translation has, in one line.
        ///
        /// ⚠ Both figures, always — the two families do not live equally long, and a single total
        /// would hide that half of it ages out on its own. Said even at zero: an empty line is how
        /// somebody learns the feature exists before they need it, which is the only moment worth
        /// learning it.
        /// </summary>
        private void RefreshBackupsLine()
        {
            if (_backupsLabel == null) return;

            var saved = 0;
            var automatic = 0;

            foreach (var entry in TranslationBackups.List())
            {
                if (entry.IsSaved) saved++;
                else automatic++;
            }

            // 🔴 **Shown whenever there is a translation OR a backup of one.** Those are the two
            // reasons to come here — keeping a copy before a risky move, and walking back out of one
            // — and the second survives the translation being gone. With neither, the game has no
            // history and nothing to make one from, so the row says nothing at all.
            if (_backupsRow != null)
            {
                _backupsRow.Visible = _standing.Publication != Publication.NotDownloaded
                                      || saved + automatic > 0;
            }

            _backupsLabel.Show(saved == 0 && automatic == 0
                ? "Backups: none yet"
                : $"Backups: {saved} of your own, {automatic} automatic");
        }

        /// <summary>
        /// What the card was last told the translation holds. -1 until it has been told anything.
        /// </summary>
        private int _shownLineCount = -1;

        /// <summary>
        /// Follow the translation growing under the card.
        ///
        /// 🔴 **Lines arrive from a thread that knows nothing about screens.** The worker stores a
        /// captured or translated line and moves on; no event reaches any panel. So the card showed
        /// whatever the count happened to be when something ELSE refreshed it, and stayed there —
        /// reported from a game where the panel read 98 while the file held 145, and the Manager,
        /// which reads the file, read 145 too.
        ///
        /// ⚠ **Ticked rather than pushed**, deliberately: pushing would mean the worker calling
        /// into the interface for every line, on the wrong thread, hundreds of times a second while
        /// capture runs. Asking costs one integer compare per tick and answers nothing the rest of
        /// the time.
        ///
        /// ⚠ The whole card is refreshed, not the count alone: the same lines move
        /// <c>LocalChangesCount</c>, and a card saying "145 lines" beside "98 unpublished changes"
        /// would be a second way of being wrong.
        /// </summary>
        internal void RefreshCountIfChanged()
        {
            if (!Enabled || _statusCard == null) return;

            if (TranslatorCore.TranslationCache.Count == _shownLineCount) return;

            RefreshStatusCard();
        }

        private void RefreshStatusCard()
        {
            if (_statusCard == null) return;

            RefreshBackupsLine();

            var serverState = TranslatorCore.ServerState;
            int entryCount = TranslatorCore.TranslationCache.Count;
            string targetLang = TranslatorCore.Config.GetTargetLanguage();
            int localChanges = TranslatorCore.LocalChangesCount;

            // Recorded here rather than by the caller, so a refresh from ANY door leaves the tick
            // with the truth — see RefreshCountIfChanged.
            _shownLineCount = entryCount;

            // Where this translation stands, on the four questions the socle keeps apart — read
            // once per redraw (ReadFacts), never rebuilt here from the same facts.
            var standing = _standing;
            // Identity leads the card: which languages, whatever the mode
            _statusCard.SetIdentity(TranslatorCore.Config.GetSourceLanguage(), targetLang);

            // 🔴 **The card describes, it does not act.** Every action on this translation lives in
            // "Actions" below, and there only — that row is where the conditions are (signed in,
            // online, anything to send) and where a refusal is explained. Three buttons stood here
            // between 2026-07-26 and 2026-08-19, each duplicating one of them a few rows higher
            // WITHOUT its guards: Upload opened the upload screen while signed out, on a call that
            // could only fail. What each mode has to SAY is set by the ConfigureAs* below; what it
            // lets you DO is not this component's business.
            if (Standings.LeadsTheLineage(standing))
            {
                // ⚠ Both axes travel together. How many rows need a decision is not how many are
                // worth taking, and what they are made of is what decides whether opening the
                // review is worth it — none of which a single total can say.
                _statusCard.ConfigureAsMainOwner(standing, entryCount, targetLang,
                                                 standing.BranchesWaiting ?? 0,
                                                 standing.LinesAvailable,
                                                 serverState?.LinesToReview,
                                                 serverState?.LinesNew ?? default(TagTally),
                                                 serverState?.LinesDiffering ?? default(TagTally));
            }
            else if (Standings.OnABranch(standing))
            {
                _statusCard.ConfigureAsBranchOwner(standing, entryCount, targetLang, localChanges);
            }
            else if (standing.Publication == Publication.NotYours)
            {
                _statusCard.ConfigureAsHoldingAnothersLineage(standing, entryCount, targetLang);
            }
            else if (standing.Publication == Publication.NeverPublished)
            {
                _statusCard.ConfigureAsLocalOnly(standing, entryCount, targetLang);
            }
            // ⚠ NotDownloaded never reaches here: RefreshLayoutVisibility has already hidden the
            // card for it — a card describing a file says nothing when there is no file. It used
            // to be configured anyway, with counts of zero and a language of "None".

            // What the community made of this translation, and the player's own say. Whatever
            // the mode: an author is entitled to see their count, a player to give one back.
            // Hidden by the card itself when the server reported no vote at all.
            _statusCard.SetVote(
                serverState?.Vote,
                Standings.LeadsTheLineage(standing)
                    ? LineageRole.Main
                    : LineageRole.None);

            // Show/hide external resources link
            if (_resourcesLinkSection != null)
            {
                string resourcesUrl = serverState?.ResourcesUrl;
                bool hasResources = !string.IsNullOrEmpty(resourcesUrl);
                _resourcesLinkSection.Visible = hasResources;
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
                        _resourcesByLabel.Show($"{by} — {DescribeMissing(missing)} missing");
                        _resourcesByLabel.Tone = Tone.Warning;
                        Stacks.Retint(_resourcesLinkSection, Surface.Elevated);
                    }
                    else
                    {
                        _resourcesByLabel.Show(by);
                        _resourcesByLabel.Tone = Tone.Plain;
                        Stacks.Retint(_resourcesLinkSection, Surface.Card);
                    }

                    _resourcesUrlLabel.Show(resourcesUrl);
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
                //
                // 🔴 The code is what makes this access findable on the account's "Linked devices"
                // page. That page names every line "#QKADJN" and offers to rename the machine it
                // belongs to — while the code appeared in no program, so it asked somebody to name
                // a machine nothing let them identify. Reported from production on 2026-08-27.
                //
                // ⚠ Concatenated for the same reason as the name: it must never reach the
                // translator, which would return a different string for each account and fill the
                // cache with one entry per person.
                //
                // ⚠ Absent until the site answers, and absent for good when it cannot be reached.
                // Nothing here waits, and a code that might be wrong is worse than none.
                _accountLabel.Show(Tr("Connected as")
                    + $" @{currentUser ?? "Unknown"}"
                    + (string.IsNullOrEmpty(ApiClient.AccessCode) ? "" : $" · #{ApiClient.AccessCode}"));
                _accountLabel.Italic = false;
                _loginLogoutBtn.Label = "Logout";
            }
            else
            {
                _accountLabel.Say("Not connected");
                _accountLabel.Italic = true;
                _loginLogoutBtn.Label = "Login";

                // Disable login if offline mode
                _loginLogoutBtn.Enabled = TranslatorCore.Config.online_mode;
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
            _shownEntries = entryCount;
            _entriesLabel.Say($"Entries: {entryCount}");
            _targetLabel.Show(Tr("Target:") + $" {targetLang}");

            if (existsOnServer)
            {
                _sourceLabel.Show(Tr("Source:")
                    + $" {People.MentionOf(serverState.Uploader, TranslatorCore.Config.api_user)}"
                    + $" (#{serverState.SiteId})");

                // Role indicator.
                //
                // ⚠ Read through IsOwner, exactly as DetectCurrentState() does a few lines above —
                // a role only means something about a translation we actually hold on the server.
                // Reading Role on its own is what let this line announce "[BRANCH] Your changes are
                // reviewed by @X" to a player who had merely downloaded @X's file, while the status
                // card, which does consult IsOwner, correctly offered them the Branch/Fork choice.
                // Two blocks of the same panel contradicting each other on the same state.
                // See analyse/false-branch-role-after-download.md.
                switch (serverState.IsOwner ? serverState.Role : LineageRole.None)
                {
                    case LineageRole.Main:
                        if (serverState.BranchesCount > 0)
                        {
                            _roleLabel.Say($"[MAIN] {serverState.BranchesCount} contribution(s) from other players");
                        }
                        else
                        {
                            _roleLabel.Say("[MAIN] You own this translation");
                        }
                        _roleLabel.Tone = Tone.Success;
                        break;
                    case LineageRole.Branch:
                        _roleLabel.Show("[BRANCH] "
                            + Tr("Your changes are reviewed by")
                            + " " + People.MentionOf(serverState.MainUsername ?? serverState.Uploader,
                                                        TranslatorCore.Config.api_user));
                        _roleLabel.Tone = Tone.Warning;
                        break;
                    default:
                        _roleLabel.Show("");
                        break;
                }

                // Sync status — the socle's verdict, the same one the card and the Actions row show
                int localChanges = TranslatorCore.LocalChangesCount;
                var sync = _standing.Sync;

                if (sync == SyncDirection.Merge)
                {
                    _syncStatusLabel.Say($"SYNC NEEDED - Both local ({localChanges}) and server changed");
                    _syncStatusLabel.Tone = Tone.Warning;
                }
                else if (sync == SyncDirection.Upload)
                {
                    _syncStatusLabel.Say($"OUT OF SYNC - {localChanges} local changes to upload");
                    _syncStatusLabel.Tone = Tone.Warning;
                }
                else if (sync == SyncDirection.Download)
                {
                    int serverLines = TranslatorUIManager.PendingUpdateInfo?.LineCount ?? 0;
                    _syncStatusLabel.Say($"OUT OF SYNC - Server has update ({serverLines} lines)");
                    _syncStatusLabel.Tone = Tone.Warning;
                }
                else
                {
                    _syncStatusLabel.Say("SYNCED with server");
                    _syncStatusLabel.Tone = Tone.Success;
                }
            }
            else
            {
                // Not on server - clear role label
                _roleLabel.Show("");

                if (serverState != null && serverState.Checked)
                {
                    _sourceLabel.Say("Source: Local only (not on server)");
                    _syncStatusLabel.Say($"All {entryCount} entries are local");
                    _syncStatusLabel.Tone = Tone.Muted;
                }
                else if (!TranslatorCore.Config.online_mode)
                {
                    _sourceLabel.Say("Source: Local (offline mode)");
                    _syncStatusLabel.Show("");
                }
                else if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
                {
                    // Online mode but not logged in - can't check server state
                    _sourceLabel.Say("Source: Local (login to sync)");
                    _syncStatusLabel.Show("");
                }
                else
                {
                    _sourceLabel.Say("Source: Local (checking...)");
                    _syncStatusLabel.Show("");
                }
            }

            // AI status
            if (TranslatorCore.Config.IsTranslationEnabled)
            {
                int queueCount = TranslatorCore.QueueCount;
                // Backend name is a brand, kept out of the translated part
                string backendLabel = TranslatorCore.Config.translation_backend == "llm" ? "AI" :
                    TranslatorCore.Config.translation_backend == "google" ? "Google" : "DeepL";
                _aiStatusLabel.Show($"{backendLabel}: " + (queueCount > 0
                    ? Tr($"{queueCount} in queue")
                    : Tr("Ready")));
            }
            else
            {
                _aiStatusLabel.Show("");
            }
        }

        private void RefreshActionsSection()
        {
            if (_uploadBtn == null) return;

            bool isLoggedIn = _account.SignedIn;
            var state = TranslatorCore.ServerState;
            bool existsOnServer = state != null && state.Exists && state.SiteId.HasValue;

            // 🔴 **What the button DOES, the word on it, the line under it and why it is closed are
            // the socle's to say** — Uploads.Button, held by the corpus (`uploads`), from the
            // standing read for this redraw and the facts it was read from. This row used to
            // compose them itself, and so did the upload window and the corner notification:
            // three glues over one rule, each with its own idea of when "Sync" replaces the verb.
            var button = Uploads.Button(_standing, _local, _server, _account);
            _uploadAct = button.Act;
            _uploadBtn.Label = button.Verb;
            _uploadBtn.Enabled = button.Enabled;

            // Why the button is closed, said under it — a control that cannot act must say why
            // right there — or what it does while it is open.
            if (button.Closed != null)
            {
                _uploadHintLabel.Say(button.Closed);
            }
            else
            {
                // Translated as it is built when it may be: counts and ids stay inline (the
                // pipeline placeholders numbers), a username is appended so it never reaches the
                // translator, and a wall that already names somebody is written as it is.
                string hint = button.HintIsTranslatable ? Tr(button.Hint) : button.Hint;
                if (button.Mention != null)
                    hint += " " + People.MentionOf(button.Mention, TranslatorCore.Config.api_user);
                _uploadHintLabel.Show(hint);
            }

            // Role-specific buttons visibility
            if (_reviewOnWebsiteBtn != null && _compareWithServerBtn != null && _forkBtn != null)
            {
                bool isMain = existsOnServer && state.Role == LineageRole.Main;
                bool isBranch = existsOnServer && state.Role == LineageRole.Branch;
                bool hasBranches = state != null && state.BranchesCount > 0;

                // Review Branches - only for Main role when there are branches to review
                _reviewOnWebsiteBtn.Visible = isMain && hasBranches;
                if (isMain && hasBranches)
                {
                    // 🔴 The count is what is WAITING — not been through, and holding something —
                    // never how many people contribute. A number that includes work already
                    // arbitrated never falls to zero, and a number that never falls to zero stops
                    // being read, which hides the times there IS something to do.
                    //
                    // ⚠ The button stays whenever contributions exist: somebody may want to look
                    // at what they refused, or at who is contributing. It simply carries no number
                    // when nothing is waiting — the convention this project uses everywhere.
                    int? waiting = state.BranchesWithWork;

                    _reviewOnWebsiteBtn.Label = waiting.HasValue
                        ? (waiting.Value > 0
                            ? $"Review Branches ({waiting.Value})"
                            : "Review Branches")
                        // An older site could not say: the raw count is all there
                        // is, and it is better than a silence that reads as zero.
                        : $"Review Branches ({state.BranchesCount})";
                }

                // Compare with Server - only for owners (Main or Branch) who have uploaded
                // Non-owners can't compare because they don't have a server version to compare against
                // ⚠ From the manager, because the sync notification offers the same button and
                // the two must refuse in the same cases. See TranslatorUIManager.CanCompareWithServer.
                RefreshCompareButton(isLoggedIn);

                // Edit details — for owners of a published translation, whatever the sync state.
                // That is the point: it exists precisely for when there is nothing else to push.
                bool canEditDetails = existsOnServer && state.IsOwner;
                if (_editDetailsBtn != null)
                {
                    _editDetailsBtn.Visible = canEditDetails;
                    if (canEditDetails)
                        _editDetailsBtn.Enabled = isLoggedIn && TranslatorCore.Config.online_mode;
                }

                // Merge with Main — a branch only. Shown even when nothing new is
                // known upstream: the very first merge is what teaches the mod where
                // the Main stood, so it must be reachable before any notice exists.
                if (_updateFromMainBtn != null)
                {
                    bool canUpdateFromMain = isBranch && state.MainSiteId.HasValue;
                    _updateFromMainBtn.Visible = canUpdateFromMain;
                    if (canUpdateFromMain && !_updateFromMainInFlight)
                    {
                        _updateFromMainBtn.Enabled = isLoggedIn && TranslatorCore.Config.online_mode;
                    }
                }

                // Fork button - only for Branch role
                _forkBtn.Visible = isBranch;

                // ⚠ Never gated on an account: forking is local from end to end (a new lineage on
                // this machine, nothing sent), and it is publishing that needs a name. This asked
                // to be signed in while "Create Independent" three inches away did not — the same
                // act, two doors, two rules (decided 2026-09-07).
                if (isBranch)
                {
                    _forkBtn.Enabled = true;
                }

                // Explain the visible buttons in plain words
                if (_roleActionsHint != null)
                {
                    string hint = "";
                    if (isBranch && TranslatorUIManager.HasMainUpdate())
                        hint = Tr("The original translation has changed — Merge with Main brings it in");
                    else if (isBranch)
                        hint = Tr("Fork = continue on your own, leaving the translation of")
                               + " " + People.MentionOf(state.MainUsername ?? state.Uploader,
                                                           TranslatorCore.Config.api_user);
                    else if (isMain && hasBranches)
                        hint = Tr("Review Branches opens the website to accept or reject contributions");
                    else if (TranslatorUIManager.CanCompareWithServer)
                        hint = Tr("Compare shows your changes against the website version");
                    _roleActionsHint.Show(hint);
                    _roleActionsHint.Visible = !string.IsNullOrEmpty(hint);
                }
            }
        }

        private void OnLoginLogoutClicked()
        {
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);

            // ⚠ Only when this button is the one showing the sign-in screen. Signing OUT is not a
            // window and has nothing to put away.
            if (!isLoggedIn && Intents.IsOpen(ScreenId.Login))
            {
                Intents.Close(ScreenId.Login);
                return;
            }

            if (isLoggedIn)
            {
                // Show confirmation dialog before logout
                Intents.Confirm(
                    "Logout",
                    "Are you sure you want to disconnect?\nYou'll need to re-authenticate to sync translations.",
                    "Logout",
                    () =>
                    {
                        // ⚠ SignOut, not ClearApiSession: forgetting the token here leaves it alive
                        // on the account, and nothing on the site can tell a forgotten access from
                        // a quiet one. The screen fills up with lines nobody can identify.
                        TranslatorCore.SignOut(revoked =>
                        {
                            if (revoked) return;

                            // Said, not swallowed. The access is still live on the account and the
                            // only way to cut it now is from the site.
                            TranslatorUIManager.RunOnMainThread(() =>
                                Intents.Toast(
                                    "Signed out in the game. The site could not be reached: remove this device from Linked devices on the site.",
                                    ToastTone.Off));
                        });

                        // Every screen that shows who is signed in re-reads it
                        Intents.AccountChanged();
                        TranslatorUIManager.NotificationDismissed = false; // Reset dismissals
                    },
                    isDanger: true
                );
            }
            else
            {
                // Show login panel
                Intents.OpenLogin();
            }
        }

        /// <summary>
        /// What the Actions-row upload button would do, as the socle read it on the last refresh.
        /// Null when there is nothing on this machine to send.
        /// </summary>
        private UploadAct? _uploadAct;

        private async void OnUploadClicked()
        {
            // ⚠ Closes only the SEND. On a window showing the details act, this re-purposes it —
            // pressing Upload must never read as "close the details I was editing".
            if (_uploadAct != UploadAct.Fork && Intents.IsShowingUpload())
            {
                Intents.Close(ScreenId.Upload);
                return;
            }

            // The button says Fork, so it forks — the same door as the Fork button of the role
            // row and as "Create Independent". Local from end to end: neither the network nor an
            // account is asked for here.
            if (_uploadAct == UploadAct.Fork)
            {
                OnCreateIndependentClicked();
                return;
            }

            if (!TranslatorCore.Config.online_mode) return;

            // Both sides moved: settle them first, on the same verdict the button was labelled from
            // — read again now, since a line edited since the last redraw changes it.
            ReadFacts();
            if (_standing.Sync == SyncDirection.Merge)
            {
                await TranslatorUIManager.DownloadForMerge();
            }
            else
            {
                Intents.OpenUpload();
            }
        }

        /// <summary>
        /// Open the upload screen for the sole purpose of editing what describes the translation:
        /// its notes and its resources link. Both are prefilled from the published version, so the
        /// user edits rather than retypes.
        ///
        /// ⚠ **It shares the SCREEN, not the act.** The upload window already holds both fields,
        /// prefilled from the published version, so a second panel would be the same form twice —
        /// but it is opened for this purpose, says so in its title and its mark, and sends the two
        /// fields through PATCH /details rather than the whole translation.
        ///
        /// 🔴 The comment that used to live here said a metadata route did not exist and that the
        /// file was therefore re-sent unchanged. The route exists. Re-sending was not merely
        /// wasteful: it made this button and Upload the same act, so the window could not say which
        /// one had been asked for, and the two carried contradictory marks for where the result
        /// lands.
        /// </summary>
        private void OnEditDetailsClicked()
        {
            if (!TranslatorCore.Config.online_mode) return;

            // ⚠ Closes only what IT opened. On a window showing the send, this re-purposes it —
            // pressing "Edit details" must never read as "close the upload I was filling in".
            if (Intents.IsShowingDetails()) { Intents.Close(ScreenId.Upload); return; }

            Intents.OpenDetails();
        }

        /// <summary>
        /// Show a window, or put it away if this button is the one that put it there.
        ///
        /// 🔴 **A button that opens a window and then does nothing reads as broken.** Pressing
        /// "Translation Tools" a second time changed nothing at all — the window was already up,
        /// possibly behind another one — so the only honest readings were "it is broken" or "I
        /// missed". The button is lit while its window is up, and it is the way back out.
        /// </summary>
        /// <summary>
        /// Tell every opener whether the thing it opens is on screen.
        ///
        /// ⚠ **Reconciled from the state, every frame, rather than flipped on the click.** A window
        /// closes by its own X, by a hotkey, or because something else closed it — none of which
        /// passes through the button that opened it. This project has paid for the transition
        /// approach more than once; see TickNewlyOpenedPanels a few lines from here.
        ///
        /// ⚠ Cheap by construction: the setter returns immediately when the value has not changed,
        /// so this is a handful of bool comparisons per frame and a write only when something moved.
        /// </summary>
        public void RefreshOpenerStates()
        {
            if (_transParamsBtn != null)
                _transParamsBtn.Showing = Intents.IsOpen(ScreenId.TranslationParameters);
            if (_optionsBtn != null)
                _optionsBtn.Showing = Intents.IsOpen(ScreenId.Options);
            if (_backupsBtn != null)
                _backupsBtn.Showing = Intents.IsOpen(ScreenId.Backups);
            if (_loginLogoutBtn != null)
                _loginLogoutBtn.Showing = Intents.IsOpen(ScreenId.Login);
            if (_loginCTABtn != null)
                _loginCTABtn.Showing = Intents.IsOpen(ScreenId.Login);

            // ⚠ The two acts of one window, so each button follows ITS act — never merely "the
            // window is up", which would light both and say the wrong thing about one of them.
            if (_uploadBtn != null) _uploadBtn.Showing = Intents.IsShowingUpload();
            if (_editDetailsBtn != null) _editDetailsBtn.Showing = Intents.IsShowingDetails();

            // ⚠ The comparison lives in a BROWSER, so nothing in this window is told when it opens,
            // ends, or is closed from the page. Asked here for the same reason as the rest.
            RefreshCompareButton(!string.IsNullOrEmpty(TranslatorCore.Config.api_token));
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

        /// <summary>
        /// One act, one busy state — whichever of the two buttons carries it.
        ///
        /// ⚠ The Branch has its own row and the lineage block has another; they are never on
        /// screen together, but they share this method and OnUpdateFromMainClicked so that the
        /// pair cannot drift the way the two fork buttons did.
        /// </summary>
        private void SetUpdateFromMainBusy(bool busy)
        {
            _updateFromMainInFlight = busy;

            foreach (var button in new[] { _updateFromMainBtn, _mergeWithMainBtn })
            {
                if (button == null) continue;

                if (busy)
                {
                    button.Busy("Fetching...");
                }
                else
                {
                    button.Enabled = true;
                    button.Label = "Merge with Main";
                }
            }
        }

        /// <summary>
        /// Handler for "Contribute as Branch" button (GAP 8).
        /// Opens the upload panel to contribute changes as a branch.
        /// </summary>
        private void OnContributeAsBranchClicked()
        {
            // Open upload panel - it will detect that we're contributing to an existing translation
            Intents.OpenUpload();
        }

        /// <summary>
        /// Handler for "Take Main's version". Its safe sibling is Merge with Main, one row above.
        /// Replaces the local file with the Main's, dropping whatever was not published.
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
                Intents.Confirm(
                    "Take the Main's version?",
                    $"This will replace your {localChanges} local change(s) with the latest version from "
                    + $"{People.MentionOf(serverState.Uploader, TranslatorCore.Config.api_user)}.\n\n" +
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

        /// <summary>
        /// Colour the "Take Main's version" marks for the state it is really in.
        ///
        /// 🔴 **The side of a download depends on WHOSE translation it is**, which is why this
        /// exists instead of a constant written when the button was built. Taking the latest of a
        /// lineage you lead leaves your published copy and this machine carrying the same thing —
        /// Both. Taking somebody else's leaves nothing of yours moved: it is Local, and saying
        /// Both there tells a Main owner they are in step at the moment they stop being.
        ///
        /// ⚠ Retargeting and tinting in ONE method on purpose. Done separately, a caller that
        /// tints without retargeting repaints the previous role's answer — which is the same class
        /// of bug as the side that used to be passed to both Adorn and Tint.
        /// </summary>
        private void SetDownloadLatestState(bool interactable)
        {
            if (_downloadLatestBtn == null) return;

            var state = TranslatorCore.ServerState;
            _downloadLatestBtn.Retarget(
                EditScope.SideAfter(onThisMachine: true,
                                    yourPublishedCopy: state != null && state.IsOwner));
            _downloadLatestBtn.Enabled = interactable;
        }

        /// <summary>
        /// Same, for taking a translation from the community list.
        ///
        /// ⚠ Almost always somebody else's, hence Local almost always. The exception is real
        /// though: one's own published translation is listed there like any other.
        /// </summary>
        private void SetCommunityDownloadState(bool interactable)
        {
            if (_downloadBtn == null) return;

            _downloadBtn.Retarget(
                EditScope.SideAfter(onThisMachine: true,
                                    yourPublishedCopy: PublishedByUs(_translationList?.SelectedTranslation)));
            _downloadBtn.Enabled = interactable;
        }

        /// <summary>
        /// Was this published under the account signed in here?
        ///
        /// ⚠ The uploader's name, NOT the lineage id: a Branch carries the Main's uuid and is
        /// somebody else's line. Matching on the uuid would call a Main owner's translation ours
        /// while we hold a branch of it — the exact confusion the vocabulary exists to prevent.
        /// </summary>
        private static bool PublishedByUs(TranslationInfo translation)
        {
            // The one test for "is this name mine", the socle's — three screens had their own.
            return People.IsYou(translation?.Uploader, TranslatorCore.Config.api_user);
        }

        private async System.Threading.Tasks.Task PerformDownloadLatest(ServerTranslationState serverState)
        {
            // Disable buttons while downloading
            if (_downloadLatestBtn != null)
            {
                _downloadLatestBtn.Busy("Downloading...");
                SetDownloadLatestState(false);
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
                        _downloadLatestBtn.Label = "Take Main's version";
                        SetDownloadLatestState(true);
                    }
                });
            }
            catch (System.Exception e)
            {
                TranslatorCore.LogWarning($"[MainPanel] Download error: {e.Message}");
                if (_downloadLatestBtn != null)
                {
                    _downloadLatestBtn.Label = "Take Main's version";
                    SetDownloadLatestState(true);
                }
            }
        }

        /// <summary>
        /// "Create Independent" and "Fork" both lead here: the one door to a fork is
        /// <see cref="TranslatorUIManager.OfferFork"/>, which carries the confirmation, the act and
        /// the rule about opening the upload screen. Two copies of that text drifted apart once.
        /// </summary>
        private void OnCreateIndependentClicked()
        {
            TranslatorUIManager.OfferFork(RefreshUI);
        }

        /// <summary>
        /// What the Compare button says and whether it may act.
        ///
        /// 🔴 **One button, two verbs — the shape "Edit in browser" already uses.** A comparison
        /// opened from here lives in a browser tab, and the game had no way to let go of it:
        /// closing the tab leaves the token alive until it expires, and the mod goes on waiting for
        /// a result nobody is going to send. The way out belongs on the control that opened it.
        ///
        /// 🔴 **Written HERE and nowhere else, because the click used to write it too.** The
        /// browser having been opened, the callback restored the label with a literal "Compare" —
        /// so the button went straight past the verb it should have taken, and stayed on the wrong
        /// one until something happened to refresh the whole panel. Two authors for one label, and
        /// the one that ran last knew the least.
        /// </summary>
        private void RefreshCompareButton(bool isLoggedIn)
        {
            if (_compareWithServerBtn == null) return;

            bool comparing = TranslatorUIManager.IsComparisonOpen;
            bool canCompare = TranslatorUIManager.CanCompareWithServer;

            _compareWithServerBtn.Visible = canCompare || comparing;

            if (comparing)
            {
                // ⚠ Named, not "Stop": three buttons on this row could be stopped.
                _compareWithServerBtn.Enabled = true;
                _compareWithServerBtn.Label = "Stop comparison";
            }
            else if (canCompare)
            {
                _compareWithServerBtn.Enabled = isLoggedIn;
                // How many lines the comparison is about, on the button that opens it — the lines
                // changed here. When only the site moved, what it is about is on the site, and a
                // count this machine cannot know is not written as zero.
                int changes = TranslatorCore.LocalChangesCount;
                _compareWithServerBtn.Label = changes > 0 ? $"Compare ({changes})" : "Compare";
            }
        }

        private async void OnCompareWithServerClicked()
        {
            // The button says Stop, so it stops — the same door the reload path uses, which ends
            // the token on the site so the browser tab is told rather than left hanging.
            if (TranslatorUIManager.IsComparisonOpen)
            {
                TranslatorUIManager.EndComparison("stopped from the game");
                return;
            }

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
                _compareWithServerBtn.Busy("Loading...");
            }

            // Capture values for closure
            var siteId = serverState.SiteId.Value;

            try
            {
                // Publishing comparison: this is our own translation, and validating it there
                // updates the online version. Shared with the settings dialog's Compare, which
                // opens the same page in the other direction.
                // ⚠ Nothing is passed back for the label: by the time the browser is up, the
                // comparison is in flight and the verb has changed. OpenComparison refreshes this
                // screen AND the corner notification, which carries the same button.
                await TranslatorUIManager.OpenComparison(siteId, toLocal: TranslatorUIManager.ComparisonGoesToLocal);
            }
            catch (System.Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[MainPanel] Compare error: {errorMsg}");
                    RefreshCompareButton(isLoggedIn: true);
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
                _communityGameLabel.Say("Offline mode - enable Online Mode in Mod Options");
                _communityGameLabel.Tone = Tone.Warning;
                _searchBtn.Enabled = false;

                // Clear previous search results - can't download in offline mode
                _translationList?.Clear();
                return;
            }
            else if (game != null && !string.IsNullOrEmpty(game.name))
            {
                // Game name is data — never translated
                _communityGameLabel.Show(Tr("Game:") + $" {game.name}");
                _communityGameLabel.Tone = Tone.Secondary;
                _searchBtn.Enabled = true;
            }
            else
            {
                _communityGameLabel.Say("Game: Not detected");
                _communityGameLabel.Tone = Tone.Secondary;
                _searchBtn.Enabled = false;
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
                _translationList.SetStatus("No game detected", Tone.Warning);
                return;
            }

            if (_translationList.IsSearching) return;

            string targetLang = TranslatorCore.Config.GetTargetLanguage();
            SearchCommunityAsync(game.steam_id, game.name, targetLang);
        }

        private void OnDownloadCommunityClicked()
        {
            if (!TranslatorCore.Config.online_mode) return;

            var selectedTranslation = _translationList?.SelectedTranslation;
            if (selectedTranslation == null) return;

            // The one door for taking a translation over the local file, shared with the wizard:
            // it asks what must be asked (another lineage, unpublished changes) and hands over.
            TranslatorUIManager.OfferDownload(selectedTranslation,
                () => _ = PerformDownload(selectedTranslation));
        }

        private async System.Threading.Tasks.Task PerformDownload(TranslationInfo translation)
        {
            _downloadBtn.Enabled = false;
            SetCommunityDownloadState(false);
            _translationList.SetStatus("Downloading...", Tone.Warning);

            await TranslatorUIManager.DownloadTranslation(translation, (success, message) =>
            {
                if (success)
                {
                    int count = TranslatorCore.TranslationCache.Count;
                    _translationList.SetStatus($"Downloaded {count} entries!", Tone.Success);
                    RefreshUI();
                }
                else
                {
                    _translationList.SetStatus($"Error: {message}", Tone.Error);
                }

                _downloadBtn.Enabled = _translationList?.SelectedTranslation != null;
                SetCommunityDownloadState(_translationList?.SelectedTranslation != null);
            });
        }
    }
}
