using System;
using System.Threading.Tasks;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI.Components;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Panels
{
    public enum UploadMode
    {
        New,
        Update,
        Branch  // Contributing to existing Main (same UUID, becomes Branch)
    }

    /// <summary>
    /// Upload panel for sharing translations to the server.
    /// Handles new uploads, updates (owner), and branches (non-owner contributing).
    /// For NEW uploads, redirects to UploadSetupPanel for language/game selection first.
    /// </summary>
    public class UploadPanel : TranslatorPanelBase
    {
        public override string Name => "Upload Translation";
        public override int MinWidth => 450;
        public override int MinHeight => 300;
        public override int PanelWidth => 450;
        public override int PanelHeight => 420;

        protected override int MinPanelHeight => 300;

        // UI elements
        private LabelHandle _titleLabel;
        private LabelHandle _gameLabel;
        private LabelHandle _entriesLabel;
        private LabelHandle _modeInfoLabel;
        private StatusLine _status;
        private FieldHandle _notesInput;
        private FieldHandle _resourcesUrlInput;
        private ButtonHandle _backBtn;
        private ButtonHandle _uploadBtn;

        /// <summary>The author's "this is finished". Hidden for a Branch, which inherits.</summary>
        private ToggleHandle _statusToggle;

        /// <summary>
        /// The Main's decision on contributions. A Branch never sees it, and an older server
        /// never answers about it — so every read of it is guarded.
        /// </summary>
        private ToggleHandle _acceptBranchesToggle;

        /// <summary>Says what a Branch inherits, in place of the toggle it may not use.</summary>
        private LabelHandle _statusInherited;
        private Components.HelpZone _helpZone;

        // State
        private bool _isUploading;
        private bool _isChecking;
        private UploadMode _uploadMode;
        // Note: Translation type is now auto-calculated by server from HVASM tags

        // For NEW uploads - selected from UploadSetupPanel
        private string _selectedSourceLanguage;
        private string _selectedTargetLanguage;
        private bool _setupComplete = false;

        /// <summary>
        /// Opened to change what the translation SAYS about itself, not to send it.
        ///
        /// 🔴 **The same window served both acts and announced only one of them.** "Edit details"
        /// opened this screen, which titled itself "Update Translation" and carried the mark for
        /// "both sides end up carrying this" — while the button that opened it carried the mark for
        /// "only your published copy changes". Two marks for one click, and the honest one was on
        /// the button.
        ///
        /// ⚠ Now it is also TRUE: details go through PATCH /details and the file is not sent. The
        /// comment that justified re-sending said the route did not exist; it does.
        /// </summary>
        private bool _detailsOnly;

        /// <summary>Whether this screen is up AND showing the details act rather than the send.</summary>
        public bool IsShowingDetails => Enabled && _detailsOnly;

        /// <summary>Whether this screen is up AND showing the send rather than the details.</summary>
        public bool IsShowingUpload => Enabled && !_detailsOnly;

        public UploadPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            Layout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            // Adaptive card - sizes to content (PanelWidth - 2*PanelPadding)
            var card = Stacks.Card(scrollContent, "UploadCard", PanelWidth - 40);

            // Title
            // ⚠ Both, not Server. What this screen sends is the file from here, so afterwards the
            // published translation and this machine carry the same thing — which is the question
            // the strip answers, rather than "which file does it write".
            _titleLabel = ScopedTitle(card, "TitleLabel", "Upload Translation",
                                      EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: true),
                                      TextPolicy.Dynamic);

            Stacks.Spacer(card, 5);

            // Info section
            var infoBox = Stacks.Section(card, "InfoBox");

            _entriesLabel = Labels.Create(infoBox, "EntriesLabel", "Entries: 0", TextRole.Body,
                                          policy: TextPolicy.Dynamic);

            _gameLabel = Labels.Create(infoBox, "GameLabel", "Game: Unknown", TextRole.Info,
                                       policy: TextPolicy.Excluded);

            // Top-aligned and wrapping, with no fixed height: in Branch mode this says two things
            // — who receives the work, and that players will not be able to download it — and a
            // single-row minHeight would have cut the second line off. Same rule as the quality
            // legend: the label reports the height its wrapped text needs at the width it is
            // given, and the row takes it.
            _modeInfoLabel = Labels.Create(infoBox, "ModeInfoLabel", "", TextRole.Small,
                                           policy: TextPolicy.Excluded, wrap: true, fill: Fill.Stretch,
                                           align: Placement.TopLeft);
            _modeInfoLabel.Italic = true;

            Stacks.Spacer(card, 10);

            // 🔴 **Whether this translation is finished — the author's own word.** The mod posted
            // "in_progress" unconditionally, so it had two effects and both were wrong: nobody
            // could ever declare a translation complete from the game, and republishing from the
            // game silently UNDID a "complete" set on the website.
            //
            // ⚠ Same shape as the site's own screen, deliberately: a Main owner chooses, a Branch
            // inherits its Main's and is told so rather than shown a control that does nothing.
            var statusBox = Stacks.Section(card, "StatusBox");

            _statusToggle = CheckBoxes.Create(statusBox, "StatusToggle", "Mark this translation as complete");
            _helpZone?.Describe(_statusToggle,
                "Your own declaration that this translation is finished. Players see it on the "
                + "listing, and it is what separates a translation you can play with from one still "
                + "being written.");

            _statusInherited = Labels.Create(statusBox, "StatusInherited", "", TextRole.Small,
                                             policy: TextPolicy.Dynamic);

            // 🔴 **Whether anybody may contribute — the Main's other declaration.** Beside the
            // first for the same reason: only a Main can take it, only a Main is shown it, and it
            // is off unless somebody says otherwise. Keeping a translation open to contributions
            // is work nobody agreed to by publishing.
            //
            // ⚠ The reminder says what a contribution IS. Somebody publishing their first
            // translation has no idea, and a checkbox whose subject is unknown gets left alone —
            // which happens to be the safe answer here, but for the wrong reason.
            _acceptBranchesToggle = CheckBoxes.Create(statusBox, "AcceptBranchesToggle", "Let others contribute to this translation");
            _helpZone?.Describe(_acceptBranchesToggle,
                "A contribution is a copy of your work with someone else's changes, sent to you to "
                + "accept or not. Leave this off to work alone — others can still publish their "
                + "own version of it.");

            // Note: Translation type is now auto-calculated by server from HVASM tags
            // (Human/Validated/AI/System/Missing percentages in the file)

            // Notes
            var notesLabel = Labels.Create(card, "NotesLabel", "Notes (optional):", TextRole.Small);

            _notesInput = Fields.Create(card, "NotesInput", "Add any notes about this translation...",
                                       minHeight: UIStyles.MultiLineSmall);
            _helpZone?.Describe(_notesInput,
                "Shown to other players on the website next to your translation");

            // Resources URL
            var urlLabel = Labels.Create(card, "UrlLabel", "Resources URL (optional):", TextRole.Small);

            _resourcesUrlInput = Fields.Create(card, "ResourcesUrlInput", "https://... (link to fonts/images)");
            _helpZone?.Describe(_resourcesUrlInput,
                "Optional public link to the fonts and images pack players need for text to render correctly. Shown to anyone who downloads this translation.");

            var urlHint = Labels.Create(card, "UrlHint",
                "External link to custom fonts or replacement images. Not hosted by us.", TextRole.Hint);

            // Status
            _status = StatusLine.Create(card, "Status");

            // Buttons - in fixed footer (outside scroll)
            var cancelBtn = Buttons.Secondary(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.Clicked += () =>
            {
                // Clear fork context when cancelling
                TranslatorCore.PendingFork = null;
                SetActive(false);
            };

            // Back button - only visible for NEW mode to go back to setup
            _backBtn = Buttons.Secondary(buttonRow, "BackBtn", "← Back");
            _backBtn.Clicked += OnBackToSetup;
            _backBtn.Visible = false; // Hidden by default

            // 🔴 **This is the button that actually publishes.** The main panel's Upload
            // Translation and Edit details only lead here, and both were adorned while the act
            // itself said nothing — the destination announced along the way and dropped at the
            // moment it happens.
            _uploadBtn = Buttons.Primary(buttonRow, "UploadBtn", "Upload",
                                         scope: EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: true),
                                         policy: TextPolicy.Dynamic);
            _uploadBtn.Clicked += () =>
            {
                try
                {
                    TranslatorCore.LogInfo("[UploadPanel] Upload button clicked!");
                    ConfirmThenUpload();
                }
                catch (Exception e)
                {
                    TranslatorCore.LogError($"[UploadPanel] Exception in click handler: {e}");
                }
            };
            DescribeUploadButton("Publish this translation online so others can find and download it for this game");
        }

        /// <summary>
        /// The upload button means different things per mode (New/Update/Branch/Fork);
        /// re-describe it whenever the mode is resolved.
        /// </summary>
        private void DescribeUploadButton(string helpText)
        {
            if (_uploadBtn != null)
                _helpZone?.Describe(_uploadBtn, helpText);
        }

        private void OnBackToSetup()
        {
            // Close this panel and reopen UploadSetupPanel
            SetActive(false);

            // Reopen setup panel - it will pre-populate with detected game
            Intents.SetUpUpload((game, srcLang, tgtLang) =>
            {
                ContinueAfterSetup(game, srcLang, tgtLang);
            });
        }

        public override void SetActive(bool active)
        {
            // Only trigger logic when transitioning from inactive to active
            // (PanelDragger calls SetActive(true) every frame when mouse is in drag/resize area)
            bool wasActive = Enabled;

            // Skip reset if setup was just completed (ContinueAfterSetup sets _setupComplete = true before calling SetActive)
            bool skipReset = active && _setupComplete;

            // ⚠ **An open panel is re-settled when the purpose changes**, and only then. Without
            // it, Upload pressed while Edit details was open left the details screen up under an
            // Upload click. `_purposeStated` is what keeps PanelDragger's per-frame SetActive(true)
            // out of this.
            bool repurposed = active && !skipReset && _purposeStated
                              && _openingForDetails != _detailsOnly;

            base.SetActive(active);

            // Only run on first activation, not repeated SetActive(true) calls
            if (active && ((!wasActive && !skipReset) || repurposed))
            {
                // Reset setup state when opening fresh
                _setupComplete = false;
                _selectedSourceLanguage = null;
                _selectedTargetLanguage = null;

                // What THIS opening is for. Read once and consumed, so every other way in — the
                // Upload button, a Contribute, a Fork — opens the ordinary screen.
                _detailsOnly = _openingForDetails;
                _openingForDetails = false;
                _purposeStated = false;

                CheckUploadMode();
            }
        }

        /// <summary>
        /// Called by UploadSetupPanel when user completes setup for NEW upload.
        /// </summary>
        /// <summary>
        /// Open this screen for the sole purpose of changing the description and the resources
        /// link of an already published translation.
        /// </summary>
        public void OpenForDetails()
        {
            _openingForDetails = true;
            _purposeStated = true;
            SetActive(true);
        }

        /// <summary>
        /// Open this screen to SEND the translation — the ordinary way in, from every button that
        /// publishes, updates, contributes or forks.
        ///
        /// 🔴 **Every way in states its purpose, and that is not tidiness.** This screen serves two
        /// acts now, and it settles which one it is only on a FRESH activation — a panel already on
        /// screen keeps what it was. So pressing Upload while Edit details was open left the window
        /// titled Edit details, marked for the published copy alone, with a Save button that sends
        /// no translation: the button said one thing and the screen did another.
        ///
        /// ⚠ **It cannot be inferred from a bare SetActive(true)**, which is why this exists rather
        /// than a default: PanelDragger calls SetActive(true) on every frame the pointer spends in
        /// the drag area, so "no purpose stated" would re-settle the screen as an upload while
        /// somebody was merely moving it.
        /// </summary>
        public void OpenForUpload()
        {
            _openingForDetails = false;
            _purposeStated = true;
            SetActive(true);
        }

        // ⚠ Pending, not the flag itself: SetActive settles what this opening is FOR, and it runs
        // after this. Set directly, the reset inside SetActive would clear it before anything read
        // it — and worse, the flag would survive into the NEXT opening, so an ordinary Upload would
        // silently become a details edit.
        private bool _openingForDetails;

        // Whether somebody just said what they are opening this for. Only a stated purpose may
        // re-settle a panel that is already up — see OpenForUpload.
        private bool _purposeStated;

        public void ContinueAfterSetup(GameInfo game, string sourceLanguage, string targetLanguage)
        {
            _selectedSourceLanguage = sourceLanguage;
            _selectedTargetLanguage = targetLanguage;
            _setupComplete = true;

            // Update display
            _uploadMode = UploadMode.New;
            RefreshStatusControl();
            _titleLabel.Say("Upload Translation");
            _modeInfoLabel.Show(Tr("Languages:") + $" {sourceLanguage} -> {targetLanguage}");
            _uploadBtn.Label = "Upload";
            _status.Clear();

            // Enable upload button (we're ready to upload after setup)
            _isChecking = false;
            _uploadBtn.Enabled = true;

            // Show back button for NEW mode (user can go back to change game/languages)
            _backBtn.Visible = true;

            RefreshInfo();

            // Show the upload panel
            SetActive(true);
        }

        private async void CheckUploadMode()
        {
            TranslatorCore.LogInfo("[UploadPanel] CheckUploadMode started");
            _isChecking = true;
            _status.Say("Checking...", Tone.Warning);
            _uploadBtn.Enabled = false;

            // Hide back button (only shown for NEW mode after setup)
            _backBtn.Visible = false;

            RefreshInfo();

            try
            {
                // Check UUID to determine mode
                var result = await ApiClient.CheckUuid(TranslatorCore.FileUuid);

                // After await, we may be on a background thread (IL2CPP issue)
                // Use RunOnMainThread for all UI operations
                TranslatorCore.LogInfo($"[UploadPanel] CheckUuid result: Exists={result.Exists}, IsOwner={result.IsOwner}, Success={result.Success}");

                // Handle API errors separately from UUID not existing
                if (!result.Success)
                {
                    var errorMsg = result.Error;
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _status.Show(Tr("Error:") + $" {errorMsg}", Tone.Error);
                        _isChecking = false;
                        _uploadBtn.Enabled = false;
                    });
                    return;
                }

                if (result.Exists)
                {
                    if (result.IsOwner)
                    {
                        // UPDATE mode - update non-UI state first
                        TranslatorCore.LogInfo("[UploadPanel] Mode set to UPDATE");
                        TranslatorCore.ServerState = new ServerTranslationState
                        {
                            Checked = true,
                            // We just published as this account, so the role below is ours.
                            AskedAsAccount = true,
                            Exists = true,
                            IsOwner = true,
                            Role = result.Role,
                            BranchesCount = result.BranchesCount,
                            SiteId = result.ExistingTranslation?.Id,
                            Uploader = TranslatorCore.Config.api_user,
                            Type = result.ExistingTranslation?.Type,
                            Notes = result.ExistingTranslation?.Notes,
                            Hash = result.ExistingTranslation?.FileHash,
                            ResourcesUrl = result.ExistingTranslation?.ResourcesUrl,
                            Status = result.ExistingTranslation?.Status,
                            AcceptsBranches = result.AcceptsBranches,
                            // ⚠ Carried over like AcceptsBranches, or this rebuild WIPED them and
                            // the card lost the notice about a Main gone or closed.
                            MainUsername = result.MainUsername,
                            MainMissing = result.MainMissing,
                            MainAbandoned = result.MainAbandoned,
                            BranchFrozen = result.BranchFrozen
                        };

                        // 🔴 **A branch whose road has ended cannot be updated, only left.** The
                        // socle says so from the walls the server reported — the Main gone, its
                        // account erased, contributions closed since. This announced "Update",
                        // sent the file, and let the server refuse it: the one thing this check
                        // exists to avoid. The sentence is the socle's, the same as the main
                        // panel's button and the Manager.
                        bool ownRowIsABranch = result.Role == LineageRole.Branch;
                        var ownAct = Uploads.ActOf(Publication.Published, ownRowIsABranch,
                                                   result.AcceptsBranches, result.MainMissing,
                                                   result.MainAbandoned, result.BranchFrozen);
                        if (ownAct == UploadAct.Fork)
                        {
                            string ownWall = Uploads.Wall(Publication.Published, ownRowIsABranch,
                                                          result.MainUsername, result.AcceptsBranches,
                                                          result.MainMissing, result.MainAbandoned,
                                                          result.BranchFrozen)
                                             ?? "This contribution can no longer be sent. Fork to carry on.";

                            TranslatorUIManager.RunOnMainThread(() =>
                            {
                                _uploadMode = UploadMode.Update;
                                RefreshStatusControl();
                                _titleLabel.Say("This contribution can no longer be sent");
                                _modeInfoLabel.Show(ownWall);
                                _uploadBtn.Label = Uploads.Verb(UploadAct.Update);
                                DescribeUploadButton("This can no longer be sent as a contribution. Fork instead — it keeps your lines and publishes them under your own name.");
                                _status.Clear();
                                _isChecking = false;
                                _uploadBtn.Enabled = false;
                            });

                            return;
                        }

                        // Capture for closure
                        var siteId = TranslatorCore.ServerState.SiteId;
                        var existingNotes = result.ExistingTranslation?.Notes ?? "";
                        // ⚠ The OWN link, not the effective one. A branch with no link of its own
                        // is shown its Main's everywhere it is displayed — and prefilling this
                        // field with it would post a copy back, pinning the branch to a link it
                        // was only borrowing. Falls back for servers older than the field.
                        var existingUrl = result.ExistingTranslation?.OwnResourcesUrl
                                          ?? result.ExistingTranslation?.ResourcesUrl ?? "";

                        TranslatorUIManager.RunOnMainThread(() =>
                        {
                            _uploadMode = UploadMode.Update;
                            RefreshStatusControl();

                            // ⚠ Two acts, two identities. The details act sends no translation, so
                            // it says so — in its title, in its mark, and on its button.
                            if (_detailsOnly)
                            {
                                _titleLabel.Say("Edit details");
                                AskTitleScope(EditScope.SideAfter(onThisMachine: false,
                                                                  yourPublishedCopy: true));
                                _modeInfoLabel.Say($"Translation #{siteId} — the translation itself is not sent");
                                _uploadBtn.Label = "Save";
                                DescribeUploadButton("Change what your published translation says about itself. The translation is not sent.");
                            }
                            else
                            {
                                _titleLabel.Say("Update Translation");
                                AskTitleScope(EditScope.SideAfter(onThisMachine: true,
                                                                  yourPublishedCopy: true));
                                _modeInfoLabel.Say($"Updating: ID #{siteId}");
                                _uploadBtn.Label = Uploads.Verb(UploadAct.Update);
                                DescribeUploadButton("Replace your published version with your current local file");
                            }

                            // Note: Type is now auto-calculated by server from HVASM tags
                            _notesInput.Text = existingNotes;
                            if (_resourcesUrlInput != null)
                                _resourcesUrlInput.Text = existingUrl;

                            _status.Clear();
                            _isChecking = false;
                            _uploadBtn.Enabled = true;
                            TranslatorCore.LogInfo($"[UploadPanel] UPDATE mode ready");
                        });
                    }
                    else
                    {
                        // BRANCH mode - update non-UI state first
                        TranslatorCore.ServerState = new ServerTranslationState
                        {
                            Checked = true,
                            // We just published as this account, so the role below is ours.
                            AskedAsAccount = true,
                            Exists = true,
                            IsOwner = false,
                            // 🔴 None, not Branch: this account has no row in the lineage yet. One
                            // becomes a Branch by uploading, and this screen is where that is about
                            // to happen. Writing Branch here is what taught five readers that
                            // "Role == Branch" could mean somebody who had never sent anything.
                            Role = LineageRole.None,
                            MainUsername = result.MainUsername,
                            SiteId = result.OriginalTranslation?.Id,
                            Uploader = result.OriginalTranslation?.Uploader,
                            Type = result.OriginalTranslation?.Type,

                            // ⚠ Carried over, or this rebuild WIPED it: the main panel had read the
                            // Main's refusal from sync/state and hidden "Contribute as Branch" on
                            // it; opening this screen put a state without the flag in its place,
                            // and the row came back on the next refresh.
                            AcceptsBranches = result.AcceptsBranches,
                            MainMissing = result.MainMissing,
                            MainAbandoned = result.MainAbandoned
                        };

                        // Capture for closure
                        var uploader = TranslatorCore.ServerState.Uploader ?? "unknown";

                        // 🔴 **The Main may not take contributions at all.** Announcing "Contribute"
                        // and letting the server refuse after the click is the one thing this
                        // check exists to avoid: the work is already done by then, and the answer
                        // arrives as a failure rather than as a choice.
                        //
                        // ⚠ Only when the server SAID so. AcceptsBranches is null on a server that
                        // predates the field, and null means "not asked" — behaving as a refusal
                        // there would put words in an author's mouth. The socle weighs it, so the
                        // wall reads the same here, on the main panel and in the Manager.
                        var act = Uploads.ActOf(Publication.NotYours, false, result.AcceptsBranches,
                                                result.MainMissing, result.MainAbandoned, null);
                        if (act == UploadAct.Fork)
                        {
                            string wall = Uploads.Wall(Publication.NotYours, false, uploader,
                                                       result.AcceptsBranches, result.MainMissing,
                                                       result.MainAbandoned, null)
                                          ?? "This translation cannot take a contribution. Fork to carry on.";

                            TranslatorUIManager.RunOnMainThread(() =>
                            {
                                _uploadMode = UploadMode.Branch;
                                RefreshStatusControl();
                                _titleLabel.Say("This translation cannot take a contribution");
                                _modeInfoLabel.Show(wall);
                                _uploadBtn.Label = Uploads.Verb(UploadAct.Contribute);
                                DescribeUploadButton("This translation does not take contributions. Fork instead — it keeps your lines and publishes them under your own name.");
                                _status.Clear();
                                _isChecking = false;
                                _uploadBtn.Enabled = false;
                            });

                            return;
                        }

                        TranslatorUIManager.RunOnMainThread(() =>
                        {
                            _uploadMode = UploadMode.Branch;
                            RefreshStatusControl();
                            _titleLabel.Say("Contribute as Branch");
                            // What a branch IS, said before sending rather than discovered after.
                            // The panel announced the role and never the visibility: players
                            // cannot download a branch, and someone expecting their work to reach
                            // players has picked the wrong action. "Only its Main can see it"
                            // would be the easy phrasing and it is not true — the game page shows
                            // that the contribution exists, under its author's name; it is the
                            // CONTENT that stays private.
                            _modeInfoLabel.Show(Tr("Contributing to:") + " @" + uploader + "\n"
                                + Tr("Only they can open and merge it. Players cannot download a branch."));
                            _uploadBtn.Label = Uploads.Verb(UploadAct.Contribute);
                            RefreshStatusControl();
                            DescribeUploadButton($"Send your changes to @{uploader} for review — they can merge them into the main translation. To publish a translation players can install, make yours independent instead");
                            // Note: Type is now auto-calculated by server from HVASM tags
                            _status.Clear();
                            _isChecking = false;
                            _uploadBtn.Enabled = true;
                        });
                    }
                }
                else
                {
                    // UUID doesn't exist on server - could be NEW or FORK
                    var pendingFork = TranslatorCore.PendingFork;

                    if (pendingFork != null &&
                        !string.IsNullOrEmpty(pendingFork.SourceLanguage) &&
                        !string.IsNullOrEmpty(pendingFork.TargetLanguage))
                    {
                        // FORK mode - we have context from CreateFork(), skip UploadSetupPanel
                        TranslatorCore.LogInfo($"[UploadPanel] Fork mode: {pendingFork.SourceLanguage} -> {pendingFork.TargetLanguage}");

                        // Capture for closure
                        var forkSourceLang = pendingFork.SourceLanguage;
                        var forkTargetLang = pendingFork.TargetLanguage;

                        TranslatorUIManager.RunOnMainThread(() =>
                        {
                            _uploadMode = UploadMode.New;
                            RefreshStatusControl();
                            _selectedSourceLanguage = forkSourceLang;
                            _selectedTargetLanguage = forkTargetLang;
                            _setupComplete = true;

                            _titleLabel.Say("Upload Fork");
                            _modeInfoLabel.Show(Tr("Languages:") + $" {forkSourceLang} -> {forkTargetLang} " + Tr("(from forked translation)"));
                            _uploadBtn.Label = Uploads.Verb(UploadAct.Upload);
                            DescribeUploadButton("Publish your independent translation — you become its owner on the website");
                            _status.Clear();
                            _isChecking = false;
                            _uploadBtn.Enabled = true;

                            // Don't show back button - fork context is fixed
                            _backBtn.Visible = false;

                            RefreshInfo();
                        });
                    }
                    else
                    {
                        // NEW mode - redirect to UploadSetupPanel for game/language selection
                        TranslatorUIManager.RunOnMainThread(() =>
                        {
                            _uploadMode = UploadMode.New;
                            RefreshStatusControl();
                            _isChecking = false;
                            SetActive(false);

                            Intents.SetUpUpload((game, srcLang, tgtLang) =>
                            {
                                ContinueAfterSetup(game, srcLang, tgtLang);
                            });
                        });
                    }
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Upload] UUID check error: {e.Message}");
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _status.Show(Tr("Error:") + $" {errorMsg}", Tone.Error);
                    _isChecking = false;
                    _uploadBtn.Enabled = true;
                });
            }
        }

        private void RefreshInfo()
        {
            if (_entriesLabel == null) return;

            _entriesLabel.Say($"Entries: {TranslatorCore.TranslationCache.Count}");

            // Same label, same treatment as the main panel and the wizard: the word is
            // translated, the game's name is data and stays as it is. Written raw here, this
            // was the one screen of the three that showed "Game:" in English whatever the
            // player had chosen.
            var gameInfo = TranslatorCore.CurrentGame;
            _gameLabel.Show(gameInfo != null
                ? Tr("Game:") + $" {gameInfo.name}"
                : Tr("Game: Unknown"));
        }

        /// <summary>
        /// One question before publishing a file that translates nothing.
        ///
        /// Capture mode collects the game's own text and leaves it untranslated on purpose, as a
        /// starting point for a human. Published as it stands, it looks like a translation from
        /// the outside and hands the original words back to whoever downloads it — which has
        /// happened, and the next person then built their own work on top of it. The author is
        /// the only one who knows whether that is what they meant, so this asks rather than
        /// refuses.
        ///
        /// Asked once, at the moment of publishing: a permanent warning in the panel would be
        /// read as noise by the many authors who are simply not finished yet.
        /// </summary>
        /// <summary>
        /// Show the status control the way the mode allows.
        ///
        /// ⚠ A Branch is told what it inherits rather than shown a switch that would do nothing:
        /// a dead control is read as a broken one, and the reason it is dead is worth a sentence.
        ///
        /// ⚠ The toggle starts on what the SERVER says, never on a default. Starting it off would
        /// make every republication from the game undo a "complete" set on the website — which is
        /// exactly the bug this replaces.
        /// </summary>
        /// <summary>
        /// Whether this upload lands on a BRANCH — the row being written, not the act being done.
        ///
        /// 🔴 Two different situations, and only one of them was covered. UploadMode.Branch means
        /// "you would become a branch"; an author who already IS one comes back through UPDATE,
        /// with their role in ServerState. Reading only the mode showed an established contributor
        /// a "This translation is finished" switch that the server discards on arrival — a branch
        /// inherits its Main's, always. A control that changes nothing is worse than no control:
        /// it is a promise the product does not keep.
        /// </summary>
        private bool WritingOnABranch =>
            _uploadMode == UploadMode.Branch
            || TranslatorCore.ServerState?.Role == LineageRole.Branch;

        /// <summary>
        /// Whether this upload must say NOTHING about contributions.
        ///
        /// Two cases, and the second is the one that bites. A branch never decides it. And a
        /// translation ALREADY published under this account whose current setting we never learned
        /// — an older site, or a check that did not answer — must not be answered for: the box
        /// reads unticked because nothing filled it, and sending that would close a lineage its
        /// owner had opened, freezing every contributor writing into it.
        ///
        /// ⚠ A first publication is deliberately not in here. Nothing is published, so there is
        /// no setting to lose, the box is the answer, and closed is the default everybody gets.
        /// </summary>
        private bool ContributionsUnknownHere
        {
            get
            {
                if (WritingOnABranch) return true;

                var state = TranslatorCore.ServerState;
                return state != null && state.Exists && state.IsOwner && state.AcceptsBranches == null;
            }
        }

        private void RefreshStatusControl()
        {
            if (_statusToggle == null || _statusInherited == null) return;

            bool branch = WritingOnABranch;

            _statusToggle.Visible = !branch;
            _statusInherited.Visible = branch;

            // The Main's other declaration follows the same rule: shown to a Main, hidden from a
            // contributor. Hidden rather than disabled — a control a branch may never use is not
            // a choice greyed out, it is a question that is not theirs.
            if (_acceptBranchesToggle != null)
            {
                _acceptBranchesToggle.Visible = !branch;
            }

            if (branch)
            {
                _statusInherited.Say("Whether this is finished is the Main's to say — your contribution inherits it.");
                return;
            }

            var published = TranslatorCore.ServerState?.Status;
            _statusToggle.IsOn =
                string.Equals(published, "complete", StringComparison.OrdinalIgnoreCase);

            // ⚠ Only when the server answered. Null means it never said, and forcing the box off
            // there would show "solo work" as this translation's state on the strength of a
            // missing field — then write it back on the next upload.
            if (_acceptBranchesToggle != null && TranslatorCore.ServerState?.AcceptsBranches is bool open)
                _acceptBranchesToggle.IsOn = open;
        }

        private void ConfirmThenUpload()
        {
            var stats = StatusCard.CalculateLocalStats();
            bool captureOnly = stats != null && TranslationQuality.IsCaptureOnly(
                stats.HumanCount, stats.ValidatedCount, stats.SkippedCount, stats.AiCount, stats.CaptureCount);

            if (!captureOnly)
            {
                DoUpload();
                return;
            }

            string message = TranslatorCore.TranslateOwnUIDynamic(
                "This file contains no translation: the " + stats.CaptureCount
                + " lines it captured are the game's own text, waiting to be translated.\n\n"
                + "Published as it is, anyone downloading it gets the original text back.\n\n"
                + "Publish anyway?");

            if (!Intents.CanConfirm())
            {
                // No dialog available: publishing is the author's own request, and swallowing it
                // silently would be worse than asking nothing.
                DoUpload();
                return;
            }

            Intents.Confirm(
                TranslatorCore.TranslateOwnUIDynamic("Nothing translated yet"),
                message,
                TranslatorCore.TranslateOwnUIDynamic("Publish"),
                DoUpload);
        }

        private async void DoUpload()
        {
            TranslatorCore.LogInfo($"[UploadPanel] DoUpload called - isUploading={_isUploading}, isChecking={_isChecking}, mode={_uploadMode}");

            if (_isUploading || _isChecking)
            {
                TranslatorCore.LogWarning("[UploadPanel] DoUpload blocked - already uploading or checking");
                return;
            }

            if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                TranslatorCore.LogWarning("[UploadPanel] DoUpload blocked - no API token");
                _status.Say("Please login first", Tone.Error);
                return;
            }

            // 🔴 **The file must be in the languages this lineage was published with.** Sending it
            // otherwise would push content of one language into a translation declared as another
            // — and the server would take the content while keeping its own language labels, so
            // nothing downstream would ever say what happened. The two ways to get here are a hand
            // edit of translations.json and a backup restored from a time the game was played in
            // another language.
            //
            // ⚠ Only two STATED languages can disagree: a source still reading "auto" is the
            // ordinary state of a translation whose local copy was never written back, and it is
            // resolved from the server, never refused.
            var conflict = TranslationLanguages.PublicationConflict(
                TranslatorCore.FileSourceLanguage, TranslatorCore.FileTargetLanguage,
                TranslatorCore.ServerState?.SourceLanguage, TranslatorCore.ServerState?.TargetLanguage);

            if (conflict != TranslationLanguages.Side.None)
            {
                string why = TranslationLanguages.ExplainConflict(conflict,
                    TranslatorCore.FileSourceLanguage, TranslatorCore.FileTargetLanguage,
                    TranslatorCore.ServerState?.SourceLanguage, TranslatorCore.ServerState?.TargetLanguage);

                TranslatorCore.LogWarning($"[UploadPanel] Refused: {why}");
                _status.Show(why, Tone.Error);
                return;
            }

            _isUploading = true;
            _uploadBtn.Enabled = false;

            // 🔴 **Details go their own way, and send no translation.** Everything below builds and
            // sends the file; this act changes two fields on a row that already exists.
            if (_detailsOnly)
            {
                await SaveDetailsOnly();
                return;
            }

            string actionText = _uploadMode == UploadMode.Update ? "Updating..." :
                               (_uploadMode == UploadMode.Branch ? "Contributing..." : "Uploading...");
            _status.Say(actionText, Tone.Warning);

            // Capture values before async (for use in RunOnMainThread callbacks)
            var uploadMode = _uploadMode;
            string notes = _notesInput.Text;
            string resourcesUrl = _resourcesUrlInput?.Text?.Trim();

            try
            {
                // Determine languages based on mode
                string srcLang, tgtLang;
                if (_uploadMode == UploadMode.New && _setupComplete)
                {
                    // NEW: Use selected languages from UploadSetupPanel
                    srcLang = _selectedSourceLanguage;
                    tgtLang = _selectedTargetLanguage;
                }
                else
                {
                    // UPDATE or FORK: Server will use existing languages (we send these but server ignores)
                    srcLang = TranslatorCore.Config.GetSourceLanguage() ?? "English";
                    tgtLang = TranslatorCore.Config.GetTargetLanguage();
                }

                // Build upload request
                // Note: Type is auto-calculated by server from HVASM tags in the content
                var request = new UploadRequest
                {
                    SteamId = TranslatorCore.CurrentGame?.steam_id,
                    // ⚠ **The name the GAME states, never the folder it sits in.** `name` falls
                    // back to the folder — "HyperEchelon6vYY3", "Forsaken.Frontiers.v1510" — and
                    // publishing under that makes the translation unfindable from any other
                    // install, because no other machine reads that string. See GameInfo.
                    GameName = TranslatorCore.CurrentGame?.product_name
                               ?? TranslatorCore.CurrentGame?.name ?? "Unknown Game",
                    GameCompany = TranslatorCore.CurrentGame?.company_name,
                    SourceLanguage = srcLang,
                    TargetLanguage = tgtLang,
                    // ⚠ Null for a Branch: the server makes it inherit its Main's, and sending a
                    // value would be this client deciding something it has no say in.
                    Status = WritingOnABranch
                        ? null
                        : (_statusToggle != null && _statusToggle.IsOn ? "complete" : "in_progress"),

                    // ⚠ Null on a branch, for the same reason as Status: the decision belongs to
                    // the Main of the lineage, and a contributor sending it would be answering for
                    // somebody else's translation.
                    //
                    // 🔴 Null ALSO on an already-published translation whose current setting we
                    // never learned — an older site, or a check that failed. The box would then
                    // read unticked without anybody having unticked it, and sending that would
                    // CLOSE the lineage and freeze its contributors. Omitted, the server keeps
                    // what it holds. A first publication is not that case: nothing is published,
                    // so the unticked box is the answer, and closed is the default.
                    AcceptsBranches = ContributionsUnknownHere
                        ? (bool?) null
                        : (_acceptBranchesToggle != null && _acceptBranchesToggle.IsOn),
                    Content = BuildTranslationContent(),
                    Notes = notes,
                    ResourcesUrl = string.IsNullOrEmpty(resourcesUrl) ? null : resourcesUrl
                };

                TranslatorCore.LogInfo($"[UploadPanel] Calling ApiClient.UploadTranslation...");
                var result = await ApiClient.UploadTranslation(request);
                TranslatorCore.LogInfo($"[UploadPanel] Upload result: Success={result.Success}, Id={result.TranslationId}, Error={result.Error}");

                // After await, we may be on a background thread (IL2CPP issue)
                // Use RunOnMainThread for all UI operations
                if (result.Success)
                {
                    // Update non-UI state (thread-safe)
                    TranslatorCore.ServerState = new ServerTranslationState
                    {
                        Checked = true,
                        // We just published as this account, so the role below is ours.
                        AskedAsAccount = true,
                        Exists = true,
                        IsOwner = true,
                        Role = result.Role,
                        SiteId = result.TranslationId,
                        Uploader = TranslatorCore.Config.api_user,
                        Hash = result.FileHash,
                        // Type is now auto-calculated by server from HVASM tags
                        Notes = notes,
                        ResourcesUrl = string.IsNullOrEmpty(resourcesUrl) ? null : resourcesUrl,

                        // What was just sent IS what the site now holds — reading it back would
                        // cost a round trip to learn something this client decided a second ago.
                        // ⚠ Null wherever nothing was sent about it, so the same reasoning as
                        // above applies: unknown stays unknown rather than becoming "solo work".
                        AcceptsBranches = ContributionsUnknownHere
                            ? (bool?) null
                            : (_acceptBranchesToggle != null && _acceptBranchesToggle.IsOn)
                    };
                    TranslatorCore.LastSyncedHash = result.FileHash;

                    // 🔴 **Where this file now lives on the site, written into the file itself.**
                    // Both places that recorded it were DOWNLOAD paths, so a translation somebody
                    // wrote and published themselves never carried its id — and that id is the only
                    // thing an anonymous session has: check-uuid needs a token, so signed out the
                    // mod could learn nothing and called a published translation "Never published".
                    // The person it fails for is the one who made it.
                    if (result.TranslationId > 0) TranslatorCore.SourceSiteId = result.TranslationId;

                    // ⚠ The interface font is NOT recorded here any more. Publishing used to write
                    // it into the game's file, which is how a mod setting came to be shared with a
                    // game translation; it now lives in the interface file, which is never
                    // published (see TranslatorCore.SaveModUiCache).

                    // Remember the source language declared at setup. It used to be sent to the
                    // server and forgotten: the user states "this game is written in X" — a fact
                    // about the game, not a preference — and the mod kept guessing it on every
                    // launch, which also left strict_source_language with nothing to enforce.
                    if (!string.IsNullOrEmpty(srcLang)
                        && !string.Equals(TranslatorCore.Config.source_language, srcLang, StringComparison.OrdinalIgnoreCase))
                    {
                        TranslatorCore.Config.source_language = srcLang;
                        TranslatorCore.SaveConfig();
                        TranslatorCore.LogInfo($"[UploadPanel] Source language recorded from upload: {srcLang}");
                    }
                    TranslatorCore.ResetMetadataDirty();

                    // ⚠ The ancestor moves FIRST, then the file is written. SaveAncestorCache is
                    // what makes "published" true — it makes the ancestor equal to what we just
                    // sent — and SaveCache counts the difference against it. Saving first wrote a
                    // file still claiming the changes that had just been published, and nothing
                    // ever rewrote it: in-game it looked synced, on disk it did not.
                    TranslatorCore.SaveAncestorCache();
                    TranslatorCore.SaveCache();
                    TranslatorUIManager.HasPendingUpdate = false;
                    TranslatorUIManager.PendingUpdateInfo = null;
                    TranslatorUIManager.PendingUpdateDirection = UpdateDirection.None;

                    // Clear fork context after successful upload
                    TranslatorCore.PendingFork = null;
                    TranslatorUIManager.NotificationDismissed = false;

                    // Capture for closure
                    var translationId = result.TranslationId;
                    string successMsg = uploadMode == UploadMode.Update ? "Updated" :
                                       (uploadMode == UploadMode.Branch ? "Contributed" : "Uploaded");

                    // Update UI on main thread
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _status.Show(Tr(successMsg + "!") + $" ID: {translationId}", Tone.Success);
                    });

                    await System.Threading.Tasks.Task.Delay(2000);

                    // Close panel and refresh on main thread
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _isUploading = false;
                        _uploadBtn.Enabled = true;
                        SetActive(false);
                        Intents.StateChanged();
                    });
                    return; // Skip finally block UI updates (already done above)
                }
                else
                {
                    var errorMsg = result.Error;
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _status.Show(Tr("Error:") + $" {errorMsg}", Tone.Error);
                        _isUploading = false;
                        _uploadBtn.Enabled = true;
                    });
                    return;
                }
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _status.Show(Tr("Error:") + $" {errorMsg}", Tone.Error);
                    _isUploading = false;
                    _uploadBtn.Enabled = true;
                });
            }
        }

        /// <summary>
        /// Send the description and the resources link, and nothing else.
        ///
        /// ⚠ The site is the only place these live, so there is nothing local to write afterwards
        /// — which is exactly what the window's mark says: the published copy carries the result,
        /// this machine does not.
        /// </summary>
        private async Task SaveDetailsOnly()
        {
            int? siteId = TranslatorCore.ServerState?.SiteId;
            if (siteId == null)
            {
                _isUploading = false;
                _uploadBtn.Enabled = true;
                _status.Show(Tr("This translation is not published, so it has no details to change."),
                             Tone.Error);
                return;
            }

            _status.Say("Saving...", Tone.Warning);

            string notes = _notesInput.Text;
            string resourcesUrl = _resourcesUrlInput?.Text?.Trim();

            var result = await ApiClient.UpdateDetails(siteId.Value, notes, resourcesUrl);

            TranslatorUIManager.RunOnMainThread(() =>
            {
                _isUploading = false;
                _uploadBtn.Enabled = true;

                if (!result.Success)
                {
                    _status.Show(Tr("Error:") + $" {result.Error}", Tone.Error);
                    return;
                }

                _status.Show(Tr("Saved."), Tone.Success);

                // The settings that travel with a translation are unchanged by this, so nothing
                // local is dirty — but the screens read the notes from the server state.
                Intents.StateChanged();
            });
        }

        private string BuildTranslationContent()
        {
            var output = new System.Collections.Generic.Dictionary<string, object>();
            output["_uuid"] = TranslatorCore.FileUuid;

            // What this translation IS, sent with it. The server keeps the languages a lineage was
            // published with and ignores any sent as request fields, so this changes nothing there
            // — it is for whoever DOWNLOADS the file: they get one that states its own languages
            // before any server has answered, instead of borrowing whatever their machine is set
            // to. Excluded from the content hash on both sides, so it cannot move a file_hash.
            if (Languages.IsSettled(TranslatorCore.FileSourceLanguage))
                output["_source_language"] = TranslatorCore.FileSourceLanguage;
            if (Languages.IsSettled(TranslatorCore.FileTargetLanguage))
                output["_target_language"] = TranslatorCore.FileTargetLanguage;

            if (TranslatorCore.CurrentGame != null)
            {
                output["_game"] = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["name"] = TranslatorCore.CurrentGame.name,
                    ["steam_id"] = TranslatorCore.CurrentGame.steam_id
                };
            }

            // Include per-font settings (fallback, scale, enabled, type)
            if (TranslatorCore.FontSettingsMap.Count > 0)
            {
                var fontsObj = new System.Collections.Generic.Dictionary<string, object>();
                foreach (var kvp in TranslatorCore.FontSettingsMap)
                {
                    var fontObj = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["enabled"] = kvp.Value.enabled,
                        ["fallback"] = kvp.Value.fallback,
                        ["type"] = kvp.Value.type
                    };
                    // Effective scale for older mods (they read only this); Phase B decomposition
                    // for newer mods (recompute from live design-scale × deliberate percent).
                    if (System.Math.Abs(kvp.Value.scale - 1.0f) > 0.001f)
                    {
                        fontObj["scale"] = kvp.Value.scale;
                    }
                    if (kvp.Value.scale_auto)
                        fontObj["scale_auto"] = true;
                    if (System.Math.Abs(kvp.Value.size_percent - 1.0f) > 0.001f)
                        fontObj["size_percent"] = kvp.Value.size_percent;
                    fontsObj[kvp.Key] = fontObj;
                }
                output["_fonts"] = fontsObj;
            }

            // Include exclusions
            var exclusions = TranslatorCore.UserExclusions;
            if (exclusions.Count > 0)
            {
                var exclusionsArray = new System.Collections.Generic.List<string>();
                foreach (var pattern in exclusions)
                    exclusionsArray.Add(pattern);
                output["_exclusions"] = exclusionsArray;
            }

            // Settings that travel with the translation. These describe the GAME, not a personal
            // preference: whoever worked out that a game needs the EventSystem left alone, or that
            // its text is typewritten, spares everyone else the same diagnosis. Only non-default
            // values are written, same convention as SaveCache.
            var sharedSettings = new System.Collections.Generic.Dictionary<string, object>();

            if (TranslatorCore.DisableEventSystemOverride)
                sharedSettings["disable_eventsystem_override"] = true;
            if (!TranslatorCore.TypewritingDetection)
                sharedSettings["typewriting_detection"] = false;
            if (!TranslatorCore.ConcatDetection)
                sharedSettings["concat_detection"] = false;

            // ⚠ The mod's interface font used to be published here. It is a MOD setting: it says
            // how our own window renders, not how the game's text does, and sending it filed a
            // local preference inside somebody else's game translation. It lives in
            // modui-translate.json now, which is never uploaded.

            if (sharedSettings.Count > 0)
                output["_settings"] = sharedSettings;

            // Include image replacements
            var imgReplacements = ImageReplacer.SaveToJson();
            if (imgReplacements != null)
                output["_image_replacements"] = imgReplacements;

            // Include variable definitions
            var variables = VariableManager.SaveToJson();
            if (variables != null)
                output["_variables"] = variables;

            // 🔴 The SAME writer the file is saved with, not a copy of it. This was a fourth
            // transcription of {"v","t","i"} — and the one that reaches the server, so a rule
            // corrected in the other three and not here is a rule corrected for nobody.
            var lines = new Newtonsoft.Json.Linq.JObject();
            TranslationFileEntries.WriteInto(lines, TranslatorCore.TranslationCache);
            foreach (var line in lines.Properties()) output[line.Name] = line.Value;

            return Newtonsoft.Json.JsonConvert.SerializeObject(output, Newtonsoft.Json.Formatting.None);
        }
    }
}
