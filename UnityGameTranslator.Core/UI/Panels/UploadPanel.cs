using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;
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
        private Text _titleLabel;
        private Text _gameLabel;
        private Text _entriesLabel;
        private Text _modeInfoLabel;
        private Text _statusLabel;
        private InputFieldRef _notesInput;
        private InputFieldRef _resourcesUrlInput;
        private ButtonRef _backBtn;
        private ButtonRef _uploadBtn;

        /// <summary>The author's "this is finished". Hidden for a Branch, which inherits.</summary>
        private Toggle _statusToggle;

        /// <summary>
        /// The Main's decision on contributions. A Branch never sees it, and an older server
        /// never answers about it — so every read of it is guarded.
        /// </summary>
        private Toggle _acceptBranchesToggle;

        /// <summary>Says what a Branch inherits, in place of the toggle it may not use.</summary>
        private Text _statusInherited;
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

        public UploadPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            // Adaptive card - sizes to content (PanelWidth - 2*PanelPadding)
            var card = CreateAdaptiveCard(scrollContent, "UploadCard", PanelWidth - 40);

            // Title
            // ⚠ Both, not Server. What this screen sends is the file from here, so afterwards the
            // published translation and this machine carry the same thing — which is the question
            // the strip answers, rather than "which file does it write".
            _titleLabel = CreateScopedTitle(card, "TitleLabel", "Upload Translation",
                                            EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: true));
            RegisterExcluded(_titleLabel);

            UIStyles.CreateSpacer(card, 5);

            // Info section
            var infoBox = CreateSection(card, "InfoBox");

            _entriesLabel = UIFactory.CreateLabel(infoBox, "EntriesLabel", "Entries: 0", TextAnchor.MiddleLeft);
            _entriesLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_entriesLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(_entriesLabel);

            _gameLabel = UIFactory.CreateLabel(infoBox, "GameLabel", "Game: Unknown", TextAnchor.MiddleLeft);
            _gameLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_gameLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(_gameLabel);

            // Top-aligned and wrapping, with no fixed height: in Branch mode this says two things
            // — who receives the work, and that players will not be able to download it — and a
            // single-row minHeight would have cut the second line off. Same rule as the quality
            // legend: the label reports the height its wrapped text needs at the width it is
            // given, and the row takes it.
            _modeInfoLabel = UIFactory.CreateLabel(infoBox, "ModeInfoLabel", "", TextAnchor.UpperLeft);
            _modeInfoLabel.fontStyle = FontStyle.Italic;
            _modeInfoLabel.fontSize = UIStyles.FontSizeSmall;
            _modeInfoLabel.color = UIStyles.TextMuted;
            _modeInfoLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            UIFactory.SetLayoutElement(_modeInfoLabel.gameObject, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);
            RegisterExcluded(_modeInfoLabel);

            UIStyles.CreateSpacer(card, 10);

            // 🔴 **Whether this translation is finished — the author's own word.** The mod posted
            // "in_progress" unconditionally, so it had two effects and both were wrong: nobody
            // could ever declare a translation complete from the game, and republishing from the
            // game silently UNDID a "complete" set on the website.
            //
            // ⚠ Same shape as the site's own screen, deliberately: a Main owner chooses, a Branch
            // inherits its Main's and is told so rather than shown a control that does nothing.
            var statusBox = CreateSection(card, "StatusBox");

            var (statusRow, statusToggle) = UIStyles.CreateStyledToggle(
                statusBox, "StatusToggle", "Mark this translation as complete");
            _statusToggle = statusToggle;
            _helpZone?.Describe(statusRow,
                "Your own declaration that this translation is finished. Players see it on the "
                + "listing, and it is what separates a translation you can play with from one still "
                + "being written.");

            _statusInherited = CreateSmallLabel(statusBox, "StatusInherited", "");
            _statusInherited.color = UIStyles.TextMuted;
            RegisterExcluded(_statusInherited);

            // 🔴 **Whether anybody may contribute — the Main's other declaration.** Beside the
            // first for the same reason: only a Main can take it, only a Main is shown it, and it
            // is off unless somebody says otherwise. Keeping a translation open to contributions
            // is work nobody agreed to by publishing.
            //
            // ⚠ The reminder says what a contribution IS. Somebody publishing their first
            // translation has no idea, and a checkbox whose subject is unknown gets left alone —
            // which happens to be the safe answer here, but for the wrong reason.
            var (branchesRow, branchesToggle) = UIStyles.CreateStyledToggle(
                statusBox, "AcceptBranchesToggle", "Let others contribute to this translation");
            _acceptBranchesToggle = branchesToggle;
            _helpZone?.Describe(branchesRow,
                "A contribution is a copy of your work with someone else's changes, sent to you to "
                + "accept or not. Leave this off to work alone — others can still publish their "
                + "own version of it.");

            // Note: Translation type is now auto-calculated by server from HVASM tags
            // (Human/Validated/AI/System/Missing percentages in the file)

            // Notes
            var notesLabel = CreateSmallLabel(card, "NotesLabel", "Notes (optional):");
            RegisterUIText(notesLabel);

            _notesInput = CreateStyledInputField(card, "NotesInput", "Add any notes about this translation...", UIStyles.MultiLineSmall);
            _helpZone?.Describe(_notesInput.Component.gameObject,
                "Shown to other players on the website next to your translation");

            // Resources URL
            var urlLabel = CreateSmallLabel(card, "UrlLabel", "Resources URL (optional):");
            RegisterUIText(urlLabel);

            _resourcesUrlInput = CreateStyledInputField(card, "ResourcesUrlInput", "https://... (link to fonts/images)");
            _helpZone?.Describe(_resourcesUrlInput.Component.gameObject,
                "Optional public link to the fonts and images pack players need for text to render correctly. Shown to anyone who downloads this translation.");

            var urlHint = UIStyles.CreateHint(card, "UrlHint", "External link to custom fonts or replacement images. Not hosted by us.");
            RegisterUIText(urlHint);

            // Status
            _statusLabel = CreateStatusLabel(card, "Status");
            RegisterExcluded(_statusLabel);

            // Buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () =>
            {
                // Clear fork context when cancelling
                TranslatorCore.PendingFork = null;
                SetActive(false);
            };
            RegisterUIText(cancelBtn.ButtonText);

            // Back button - only visible for NEW mode to go back to setup
            _backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            _backBtn.OnClick += OnBackToSetup;
            _backBtn.Component.gameObject.SetActive(false); // Hidden by default
            RegisterUIText(_backBtn.ButtonText);

            _uploadBtn = CreatePrimaryButton(buttonRow, "UploadBtn", "Upload");
            _uploadBtn.OnClick += () =>
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
            RegisterExcluded(_uploadBtn.ButtonText);
            DescribeUploadButton("Publish this translation online so others can find and download it for this game");
        }

        /// <summary>
        /// The upload button means different things per mode (New/Update/Branch/Fork);
        /// re-describe it whenever the mode is resolved.
        /// </summary>
        private void DescribeUploadButton(string helpText)
        {
            if (_uploadBtn != null)
                _helpZone?.Describe(_uploadBtn.Component.gameObject, helpText);
        }

        private void OnBackToSetup()
        {
            // Close this panel and reopen UploadSetupPanel
            SetActive(false);

            // Reopen setup panel - it will pre-populate with detected game
            TranslatorUIManager.UploadSetupPanel.ShowForSetup((game, srcLang, tgtLang) =>
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

            base.SetActive(active);

            // Only run on first activation, not repeated SetActive(true) calls
            if (active && !wasActive && !skipReset)
            {
                // Reset setup state when opening fresh
                _setupComplete = false;
                _selectedSourceLanguage = null;
                _selectedTargetLanguage = null;
                CheckUploadMode();
            }
        }

        /// <summary>
        /// Called by UploadSetupPanel when user completes setup for NEW upload.
        /// </summary>
        public void ContinueAfterSetup(GameInfo game, string sourceLanguage, string targetLanguage)
        {
            _selectedSourceLanguage = sourceLanguage;
            _selectedTargetLanguage = targetLanguage;
            _setupComplete = true;

            // Update display
            _uploadMode = UploadMode.New;
            RefreshStatusControl();
            SetDynamicText(_titleLabel, "Upload Translation");
            _modeInfoLabel.text = Tr("Languages:") + $" {sourceLanguage} -> {targetLanguage}";
            SetDynamicText(_uploadBtn.ButtonText, "Upload");
            _statusLabel.text = "";

            // Enable upload button (we're ready to upload after setup)
            _isChecking = false;
            _uploadBtn.Component.interactable = true;

            // Show back button for NEW mode (user can go back to change game/languages)
            _backBtn.Component.gameObject.SetActive(true);

            RefreshInfo();

            // Show the upload panel
            SetActive(true);
        }

        private async void CheckUploadMode()
        {
            TranslatorCore.LogInfo("[UploadPanel] CheckUploadMode started");
            _isChecking = true;
            SetDynamicText(_statusLabel, "Checking...");
            _statusLabel.color = UIStyles.StatusWarning;
            _uploadBtn.Component.interactable = false;

            // Hide back button (only shown for NEW mode after setup)
            _backBtn.Component.gameObject.SetActive(false);

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
                        _statusLabel.text = Tr("Error:") + $" {errorMsg}";
                        _statusLabel.color = UIStyles.StatusError;
                        _isChecking = false;
                        _uploadBtn.Component.interactable = false;
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
                            AcceptsBranches = result.AcceptsBranches
                        };

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
                            SetDynamicText(_titleLabel, "Update Translation");
                            SetDynamicText(_modeInfoLabel, $"Updating: ID #{siteId}");
                            SetDynamicText(_uploadBtn.ButtonText, "Update");
                            DescribeUploadButton("Replace your published version with your current local file");

                            // Note: Type is now auto-calculated by server from HVASM tags
                            _notesInput.Text = existingNotes;
                            if (_resourcesUrlInput != null)
                                _resourcesUrlInput.Text = existingUrl;

                            _statusLabel.text = "";
                            _isChecking = false;
                            _uploadBtn.Component.interactable = true;
                            TranslatorCore.LogInfo($"[UploadPanel] UPDATE mode ready");
                        });
                    }
                    else
                    {
                        // BRANCH mode - update non-UI state first
                        TranslatorCore.ServerState = new ServerTranslationState
                        {
                            Checked = true,
                            Exists = true,
                            IsOwner = false,
                            Role = TranslationRole.Branch,
                            MainUsername = result.MainUsername,
                            SiteId = result.OriginalTranslation?.Id,
                            Uploader = result.OriginalTranslation?.Uploader,
                            Type = result.OriginalTranslation?.Type
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
                        // there would put words in an author's mouth.
                        if (result.AcceptsBranches == false)
                        {
                            TranslatorUIManager.RunOnMainThread(() =>
                            {
                                _uploadMode = UploadMode.Branch;
                                RefreshStatusControl();
                                SetDynamicText(_titleLabel, "This translation is solo work");
                                _modeInfoLabel.text = "@" + uploader + " "
                                    + Tr("works alone on this one and does not take contributions.")
                                    + System.Environment.NewLine
                                    + Tr("Your lines are safe. Make yours independent to publish them.");
                                SetDynamicText(_uploadBtn.ButtonText, "Contribute");
                                DescribeUploadButton("This translation does not take contributions. Make yours independent instead — it keeps your lines and publishes them under your own name.");
                                _statusLabel.text = "";
                                _isChecking = false;
                                _uploadBtn.Component.interactable = false;
                            });

                            return;
                        }

                        TranslatorUIManager.RunOnMainThread(() =>
                        {
                            _uploadMode = UploadMode.Branch;
                            RefreshStatusControl();
                            SetDynamicText(_titleLabel, "Contribute as Branch");
                            // What a branch IS, said before sending rather than discovered after.
                            // The panel announced the role and never the visibility: players
                            // cannot download a branch, and someone expecting their work to reach
                            // players has picked the wrong action. "Only its Main can see it"
                            // would be the easy phrasing and it is not true — the game page shows
                            // that the contribution exists, under its author's name; it is the
                            // CONTENT that stays private.
                            _modeInfoLabel.text = Tr("Contributing to:") + " @" + uploader + "\n"
                                + Tr("Only they can open and merge it. Players cannot download a branch.");
                            SetDynamicText(_uploadBtn.ButtonText, "Contribute");
                            RefreshStatusControl();
                            DescribeUploadButton($"Send your changes to @{uploader} for review — they can merge them into the main translation. To publish a translation players can install, make yours independent instead");
                            // Note: Type is now auto-calculated by server from HVASM tags
                            _statusLabel.text = "";
                            _isChecking = false;
                            _uploadBtn.Component.interactable = true;
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

                            SetDynamicText(_titleLabel, "Upload Fork");
                            _modeInfoLabel.text = Tr("Languages:") + $" {forkSourceLang} -> {forkTargetLang} " + Tr("(from forked translation)");
                            SetDynamicText(_uploadBtn.ButtonText, "Upload");
                            DescribeUploadButton("Publish your independent translation — you become its owner on the website");
                            _statusLabel.text = "";
                            _isChecking = false;
                            _uploadBtn.Component.interactable = true;

                            // Don't show back button - fork context is fixed
                            _backBtn.Component.gameObject.SetActive(false);

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

                            TranslatorUIManager.UploadSetupPanel.ShowForSetup((game, srcLang, tgtLang) =>
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
                    _statusLabel.text = Tr("Error:") + $" {errorMsg}";
                    _statusLabel.color = UIStyles.StatusError;
                    _isChecking = false;
                    _uploadBtn.Component.interactable = true;
                });
            }
        }

        private void RefreshInfo()
        {
            if (_entriesLabel == null) return;

            SetDynamicText(_entriesLabel, $"Entries: {TranslatorCore.TranslationCache.Count}");

            // Same label, same treatment as the main panel and the wizard: the word is
            // translated, the game's name is data and stays as it is. Written raw here, this
            // was the one screen of the three that showed "Game:" in English whatever the
            // player had chosen.
            var gameInfo = TranslatorCore.CurrentGame;
            _gameLabel.text = gameInfo != null
                ? Tr("Game:") + $" {gameInfo.name}"
                : Tr("Game: Unknown");
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
            || TranslatorCore.ServerState?.Role == TranslationRole.Branch;

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

            _statusToggle.gameObject.SetActive(!branch);
            if (_statusToggle.transform.parent != null)
                _statusToggle.transform.parent.gameObject.SetActive(!branch);

            _statusInherited.gameObject.SetActive(branch);

            // The Main's other declaration follows the same rule: shown to a Main, hidden from a
            // contributor. Hidden rather than disabled — a control a branch may never use is not
            // a choice greyed out, it is a question that is not theirs.
            if (_acceptBranchesToggle != null)
            {
                _acceptBranchesToggle.gameObject.SetActive(!branch);
                if (_acceptBranchesToggle.transform.parent != null)
                    _acceptBranchesToggle.transform.parent.gameObject.SetActive(!branch);
            }

            if (branch)
            {
                _statusInherited.text = TranslatorCore.TranslateOwnUIDynamic(
                    "Whether this is finished is the Main's to say — your contribution inherits it.");
                return;
            }

            var published = TranslatorCore.ServerState?.Status;
            _statusToggle.isOn =
                string.Equals(published, "complete", StringComparison.OrdinalIgnoreCase);

            // ⚠ Only when the server answered. Null means it never said, and forcing the box off
            // there would show "solo work" as this translation's state on the strength of a
            // missing field — then write it back on the next upload.
            if (_acceptBranchesToggle != null && TranslatorCore.ServerState?.AcceptsBranches is bool open)
                _acceptBranchesToggle.isOn = open;
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

            if (TranslatorUIManager.ConfirmationPanel == null)
            {
                // No dialog available: publishing is the author's own request, and swallowing it
                // silently would be worse than asking nothing.
                DoUpload();
                return;
            }

            TranslatorUIManager.ConfirmationPanel.Show(
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
                SetDynamicText(_statusLabel, "Please login first");
                _statusLabel.color = UIStyles.StatusError;
                return;
            }

            _isUploading = true;
            _uploadBtn.Component.interactable = false;

            string actionText = _uploadMode == UploadMode.Update ? "Updating..." :
                               (_uploadMode == UploadMode.Branch ? "Contributing..." : "Uploading...");
            SetDynamicText(_statusLabel, actionText);
            _statusLabel.color = UIStyles.StatusWarning;

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
                    GameName = TranslatorCore.CurrentGame?.name ?? "Unknown Game",
                    SourceLanguage = srcLang,
                    TargetLanguage = tgtLang,
                    // ⚠ Null for a Branch: the server makes it inherit its Main's, and sending a
                    // value would be this client deciding something it has no say in.
                    Status = WritingOnABranch
                        ? null
                        : (_statusToggle != null && _statusToggle.isOn ? "complete" : "in_progress"),

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
                        : (_acceptBranchesToggle != null && _acceptBranchesToggle.isOn),
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
                            : (_acceptBranchesToggle != null && _acceptBranchesToggle.isOn)
                    };
                    TranslatorCore.LastSyncedHash = result.FileHash;
                    // Keep the local file in step with what was just published
                    TranslatorCore.TranslationUIFont = TranslatorCore.EffectiveInterfaceFont;

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
                        _statusLabel.text = Tr(successMsg + "!") + $" ID: {translationId}";
                        _statusLabel.color = UIStyles.StatusSuccess;
                    });

                    await System.Threading.Tasks.Task.Delay(2000);

                    // Close panel and refresh on main thread
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _isUploading = false;
                        _uploadBtn.Component.interactable = true;
                        SetActive(false);
                        TranslatorUIManager.MainPanel?.RefreshUI();
                    });
                    return; // Skip finally block UI updates (already done above)
                }
                else
                {
                    var errorMsg = result.Error;
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _statusLabel.text = Tr("Error:") + $" {errorMsg}";
                        _statusLabel.color = UIStyles.StatusError;
                        _isUploading = false;
                        _uploadBtn.Component.interactable = true;
                    });
                    return;
                }
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _statusLabel.text = Tr("Error:") + $" {errorMsg}";
                    _statusLabel.color = UIStyles.StatusError;
                    _isUploading = false;
                    _uploadBtn.Component.interactable = true;
                });
            }
        }

        private string BuildTranslationContent()
        {
            var output = new System.Collections.Generic.Dictionary<string, object>();
            output["_uuid"] = TranslatorCore.FileUuid;

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

            // Uploading IS publishing, so the font this translated UI needs travels with it. The
            // font FILES come separately, from the author's resources link (fonts/ folder).
            string uiFont = TranslatorCore.EffectiveInterfaceFont;
            if (!string.IsNullOrEmpty(uiFont))
                sharedSettings["ui_font"] = uiFont;

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

            // Use same format as SaveCache: {"v": "value", "t": "tag", "i": index}
            // ("i" omitted when absent — the server validation rejects "i": null)
            foreach (var kv in TranslatorCore.TranslationCache)
            {
                var entryObj = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["v"] = kv.Value.Value,
                    ["t"] = kv.Value.Tag ?? "A"
                };
                if (kv.Value.Index.HasValue)
                {
                    entryObj["i"] = kv.Value.Index.Value;
                }
                output[kv.Key] = entryObj;
            }

            return Newtonsoft.Json.JsonConvert.SerializeObject(output, Newtonsoft.Json.Formatting.None);
        }
    }
}
