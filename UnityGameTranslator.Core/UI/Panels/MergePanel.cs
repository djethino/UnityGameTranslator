using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UniverseLib.UI;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Merge panel for resolving conflicts between local and remote translations.
    /// Every path goes through the tag-aware merge: a merge that loses tags demotes
    /// human and validated work to "AI" (see analyse/sync-paths-audit.md).
    /// </summary>
    public class MergePanel : TranslatorPanelBase
    {
        public override string Name => "Merge Translations";
        public override int MinWidth => 650;
        public override int MinHeight => 400;
        public override int PanelWidth => 650;
        public override int PanelHeight => 500;

        protected override int MinPanelHeight => 400;

        // Conflict list grows with the panel height to show more rows at once.
        protected override bool HasFlexibleContent => true;

        // Merge in progress: values carry their tags, always
        private MergeResultWithTags _pendingMergeWithTags;
        private Dictionary<string, TranslationEntry> _remoteTranslationsWithTags;

        private Dictionary<string, ConflictResolution> _resolutions = new Dictionary<string, ConflictResolution>();
        private string _serverHash;
        private ScrollList _conflictList;
        private LabelHandle _summaryLabel;

        // Button/choice references for dynamic state
        private ButtonHandle _applyBtn;
        private Host _bulkChoiceHost;
        private ChoiceHandle _bulkChoice;
        private ButtonHandle _reviewBtn;
        // Upstream merge (Main -> branch): separate ancestor and separate hash from
        // this translation's own line on the site — see ApplyMerge
        private bool _isUpstreamMerge;
        private Dictionary<string, TranslationEntry> _upstreamContent;
        private string _upstreamHash;
        // Settings travelling with the incoming content, and ours as they stood
        // before it arrived. Null when the caller does not know them, in which
        // case the merge leaves settings alone — as it always did.
        private TranslationSettings _incomingSettings;
        private TranslationSettings _ourSettingsBefore;
        private TranslationSettings _ancestorSettingsBefore;
        private string _settingsSourceLabel;
        private bool _settingsExplicitRequest;
        private HelpZone _helpZone;
        private bool _userMadeChoice = false;
        // True while the review page round trip is in flight (see OpenReviewPage)
        private bool _reviewInFlight;

        /// <summary>
        /// This panel, for its own button callbacks: a lambda that captures `this` is the IL2CPP
        /// trap (a delegate over an Il2Cpp object), so the callbacks reach the panel through a
        /// static instead. There is one merge panel per process.
        /// </summary>
        private static MergePanel _self;

        public MergePanel(UIBase owner) : base(owner)
        {
            _self = this;
        }

        /// <summary>
        /// Set merge data with tags (tag-aware merge).
        /// </summary>
        public void SetMergeDataWithTags(MergeResultWithTags mergeResult, Dictionary<string, TranslationEntry> remoteTranslations, string serverHash = null)
        {
            TranslatorCore.LogInfo($"[MergePanel] SetMergeDataWithTags called - conflicts={mergeResult?.Conflicts?.Count ?? -1}");
            _pendingMergeWithTags = mergeResult;
            _remoteTranslationsWithTags = remoteTranslations;
            _serverHash = serverHash ?? TranslatorCore.ServerState?.Hash;
            _resolutions.Clear();
            _userMadeChoice = false;
            // Cleared here, set by SetUpstreamMerge right after when it applies:
            // a later ordinary merge must never inherit the upstream bookkeeping
            _isUpstreamMerge = false;
            _upstreamContent = null;
            _upstreamHash = null;
            // Same reasoning as the upstream bookkeeping above: settings context
            // belongs to ONE merge, and SetSettingsContext refills it right after
            _incomingSettings = null;
            _ourSettingsBefore = null;
            _ancestorSettingsBefore = null;
            _settingsSourceLabel = null;
            _settingsExplicitRequest = false;
            SetApplyButtonEnabled(false);
            // The bulk choice starts unselected too: RefreshConflictList below rebuilds it fresh,
            // the same state a brand new merge starts in — see RebuildBulkChoice.

            // 🔴 Pre-selected as the socle's own verdict, conflict by conflict — the same first
            // answer as the Manager and the site for the same line (decided 2026-09-07). This
            // forced "take the remote" on every conflict, including a line changed here and
            // deleted there, where the rule keeps the local one; a person who applied without
            // reading each line lost their own correction to a deletion.
            foreach (var conflict in mergeResult.Conflicts)
            {
                _resolutions[conflict.Key] = conflict.Suggested == MergeVerdict.TakeLocal
                    ? ConflictResolution.KeepLocal
                    : ConflictResolution.TakeRemote;
            }

            RefreshConflictList();
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            Layout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover a button to see what it does");

            // Adaptive card for merge conflicts — stretchVertically so the inner conflict list
            // can absorb the extra space when the user enlarges the panel.
            var card = Stacks.Card(scrollContent, "MergeCard", PanelWidth - 40, stretchVertically: true);

            ScopedTitle(card, "Title", "Merge Conflicts", EditSide.Local);

            Stacks.Spacer(card, 5);

            // Explanation
            Labels.Create(card, "Explanation",
                "Both you and the server made changes. Choose which version to keep for each conflict:",
                TextRole.Small, minHeight: UIStyles.RowHeightMedium, fill: Fill.Stretch);

            Stacks.Spacer(card, 3);

            // Summary
            _summaryLabel = Labels.Create(card, "Summary", "Conflicts to resolve:", TextRole.Info,
                                          policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightMedium,
                                          fill: Fill.Stretch);

            // Conflict list scroll view
            // See TranslatorPanelBase.ScrollingListHeightRule: without a preferred height this
            // list is weighed at its minimum when the panel adds up what it needs, so anything
            // below it is never budgeted for.
            _conflictList = ScrollList.Create(card, "ConflictScroll", minHeight: 240, preferredHeight: 240, spacing: 5);

            Stacks.Spacer(card, 10);

            // Bulk action row
            var bulkRow = Stacks.Row(card, "BulkRow", minHeight: UIStyles.RowHeightXLarge, placement: Placement.MiddleCenter);

            _bulkChoiceHost = Stacks.Horizontal(bulkRow, "BulkChoiceHost", fill: Fill.Content);
            RebuildBulkChoice();

            // Apply Merge - starts disabled until user makes a choice
            _applyBtn = Buttons.Primary(bulkRow, "ApplyBtn", "Apply Merge",
                scope: EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false));
            // ⚠ Writes this machine's translation and publishes nothing — the whole merge panel
            // settles a local file. Marked so the three buttons of this row say the same thing.
            _applyBtn.Clicked += () => _self?.ApplyMerge();
            SetApplyButtonEnabled(false);
            _helpZone?.Describe(_applyBtn,
                "Save the merged result: non-conflicting changes from both sides plus your choices above");

            // Bottom buttons - in fixed footer (outside scroll)
            var cancelBtn = Buttons.Secondary(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.Clicked += () => _self?.CancelMerge();
            _helpZone?.Describe(cancelBtn, "Close without changing anything — you can merge later");

            // ⚠ Overwrites the local file with the online one. The most destructive act on this
            // row, and it was the one saying nothing about where it lands.
            var replaceBtn = Buttons.Create(buttonRow, "ReplaceBtn", "Replace with Server", ButtonTone.Danger,
                minWidth: 130, scope: EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false));
            replaceBtn.Clicked += () => _self?.ReplaceWithRemote();
            _helpZone?.Describe(replaceBtn, "Throw away ALL your local changes and take the website's version as-is");

            // Review on Website in the footer (secondary action)
            // ⚠ The same act as the main panel's Review Branches: it rewrites the PUBLISHED Main
            // and never comes back here on its own.
            _reviewBtn = Buttons.Create(buttonRow, "ReviewBtn", "Review on Website", ButtonTone.Link,
                minWidth: 115, scope: EditScope.SideAfter(onThisMachine: false, yourPublishedCopy: true));
            _reviewBtn.Clicked += () => _self?.OpenReviewPage();
            _helpZone?.Describe(_reviewBtn,
                "Open this merge in your browser: bigger screen, search, and line-by-line tools");
        }

        /// <summary>
        /// The two bulk buttons, freshly built and unselected. A <see cref="ChoiceHandle"/> cannot
        /// be told back to "nothing chosen" once one of its options was picked — only recreated —
        /// so a new merge, or a fresh resolution list, gets a new one instead of a reset call.
        /// </summary>
        private void RebuildBulkChoice()
        {
            _bulkChoiceHost.Clear();

            // All button callbacks use the static singleton to avoid IL2CPP 'this' capture issues
            _bulkChoice = Choices.Create(_bulkChoiceHost, "BulkChoice",
                new[] { "Keep My Changes", "Take Server" }, initial: -1, spacing: 10, onChosen: index =>
                {
                    var self = _self;
                    if (self == null) return;
                    if (index == 0) self.UseAllLocal();
                    else self.UseAllRemote();
                });

            _helpZone?.Describe(_bulkChoice.Option(0),
                "Resolve every conflict with YOUR version of the line");
            _helpZone?.Describe(_bulkChoice.Option(1),
                "Resolve every conflict with the website's version of the line");
        }

        private void RefreshConflictList()
        {
            if (_conflictList == null) return;
            if (_pendingMergeWithTags == null) return;

            _conflictList.Clear();
            RebuildBulkChoice();

            var stats = _pendingMergeWithTags.Statistics;
            int conflictCount = _pendingMergeWithTags.Conflicts.Count;

            _summaryLabel.Show(conflictCount > 0
                ? Tr($"{conflictCount} conflict(s) to resolve") + $"  |  {stats.GetSummary()}"
                : Tr("No conflicts! All changes merged automatically.") + $"  |  {stats.GetSummary()}");

            var conflicts = _pendingMergeWithTags.Conflicts;
            for (int i = 0; i < conflicts.Count; i++)
            {
                CreateConflictRowWithTags(conflicts[i]);
            }
        }

        private void CreateConflictRowWithTags(MergeConflictWithTags conflict)
        {
            string localValue = conflict.Local?.Value ?? "(none)";
            string localTag = conflict.Local?.Tag;
            string remoteValue = conflict.Remote?.Value ?? "(none)";
            string remoteTag = conflict.Remote?.Tag;

            CreateConflictRowInternal(conflict.Key, localValue, localTag, remoteValue, remoteTag);
        }

        // ⚠ GetTagDisplayName and GetTagColor lived here and are gone.
        //
        // The first turned a letter into this panel's own word — "H" became "[Human]" here, stayed
        // "H" on the website's tables, and was a green band in the quality bar three inches away:
        // one fact, three vocabularies. The second painted tags with the STATUS colours
        // (StatusSuccess, StatusWarning), which the shared library forbids by a check of its own:
        // "a measurement and a verdict are two registers". Its own comment recorded that they had
        // already drifted once, V having been purple while purple means "kept as is" everywhere.
        //
        // Both are replaced by UIStyles.CreateTagChip, whose colours come from Common.Theme.

        private void CreateConflictRowInternal(string key, string localValue, string localTag, string remoteValue, string remoteTag)
        {
            var rowHost = Stacks.Vertical(_conflictList.Rows, $"Conflict_{key}", spacing: 3,
                                          minHeight: UIStyles.MultiLineMedium);

            // Key label
            var keyLabel = Labels.Create(rowHost, "Key", $"Key: {key}", TextRole.Body,
                                         policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);
            keyLabel.Bold = true;

            // Values row
            var valuesRow = Stacks.Horizontal(rowHost, "Values", spacing: 10, minHeight: UIStyles.CodeDisplayHeight);

            // Local value
            var localGroup = Stacks.Vertical(valuesRow, "Local", spacing: 2);

            // 🔴 The tag as the CHIP the website draws, not as "[AI]" in coloured words.
            //
            // Naming it in prose meant translating the letter into a word — and the words were
            // this panel's own, so H read "Human" here, "H" on the site's tables and a green band
            // in the bar three inches away. The chip is the same square in all three, from the
            // same library. Side and tag also stop competing for one label's colour: the side is
            // told in plain text, the tag by its own mark.
            var localHead = Stacks.Horizontal(localGroup, "LocalHead", spacing: 6, minHeight: UIStyles.RowHeightSmall);

            Labels.Create(localHead, "LocalLabel", "Local:", TextRole.Small, tone: Tone.Secondary);
            if (localTag != null) TagChips.Create(localHead, localTag);

            Labels.Create(localGroup, "LocalValue", localValue, TextRole.Small, tone: Tone.Accent,
                          policy: TextPolicy.Excluded, fill: Fill.Stretch);

            // Remote value
            var remoteGroup = Stacks.Vertical(valuesRow, "Remote", spacing: 2);

            var remoteHead = Stacks.Horizontal(remoteGroup, "RemoteHead", spacing: 6, minHeight: UIStyles.RowHeightSmall);

            Labels.Create(remoteHead, "RemoteLabel", "Server:", TextRole.Small, tone: Tone.Secondary);
            if (remoteTag != null) TagChips.Create(remoteHead, remoteTag);

            Labels.Create(remoteGroup, "RemoteValue", remoteValue, TextRole.Small, tone: Tone.Success,
                          policy: TextPolicy.Excluded, fill: Fill.Stretch);

            // Choice buttons (using ButtonRef instead of Toggle for IL2CPP compatibility)
            var choiceRow = Stacks.Horizontal(rowHost, "Choices", spacing: 10, minHeight: UIStyles.RowHeightMedium);

            bool isLocal = _resolutions.TryGetValue(key, out var res) && res == ConflictResolution.KeepLocal;

            // Capture key by value for closures
            string capturedKey = key;

            Choices.Create(choiceRow, "Choice", new[] { "Use Local", "Use Server" },
                initial: isLocal ? 0 : 1, spacing: 10, minWidth: 100, onChosen: index =>
                {
                    var self = _self;
                    if (self == null) return;
                    self._resolutions[capturedKey] = index == 0 ? ConflictResolution.KeepLocal : ConflictResolution.TakeRemote;
                    self.OnUserMadeChoice();
                });
        }

        internal void UseAllLocal()
        {
            try
            {
                SetAllResolutions(ConflictResolution.KeepLocal);
                HighlightBulkButton(true);
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[MergePanel] UseAllLocal failed: {e}");
            }
        }

        internal void UseAllRemote()
        {
            try
            {
                SetAllResolutions(ConflictResolution.TakeRemote);
                HighlightBulkButton(false);
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[MergePanel] UseAllRemote failed: {e}");
            }
        }

        /// <summary>
        /// Called when user makes any choice (bulk or individual). Enables Apply Merge.
        /// </summary>
        private void OnUserMadeChoice()
        {
            if (!_userMadeChoice)
            {
                _userMadeChoice = true;
                SetApplyButtonEnabled(true);
            }
        }

        private void SetApplyButtonEnabled(bool enabled)
        {
            if (_applyBtn == null) return;
            _applyBtn.Enabled = enabled;
        }

        /// <summary>
        /// Restyle the bulk choice after RefreshConflictList rebuilt it unselected — setting
        /// <see cref="ChoiceHandle.Selected"/> from code restyles without calling back, so this
        /// cannot loop into UseAllLocal/UseAllRemote a second time.
        /// </summary>
        private void HighlightBulkButton(bool isLocal)
        {
            if (_bulkChoice != null) _bulkChoice.Selected = isLocal ? 0 : 1;
            OnUserMadeChoice();
        }

        private void SetAllResolutions(ConflictResolution resolution)
        {
            if (_resolutions == null)
            {
                _resolutions = new Dictionary<string, ConflictResolution>();
            }

            if (_pendingMergeWithTags?.Conflicts == null || _pendingMergeWithTags.Conflicts.Count == 0)
            {
                TranslatorCore.LogError("[MergePanel] No conflicts to resolve");
                return;
            }

            var conflicts = _pendingMergeWithTags.Conflicts;
            var conflictKeys = new List<string>();
            for (int i = 0; i < conflicts.Count; i++)
            {
                conflictKeys.Add(conflicts[i].Key);
            }

            for (int i = 0; i < conflictKeys.Count; i++)
            {
                _resolutions[conflictKeys[i]] = resolution;
            }

            RefreshConflictList();
        }

        /// <summary>
        /// Mark this merge as coming from the UPSTREAM Main rather than from this
        /// translation's own line on the site. The difference matters at apply
        /// time: the two sides have separate ancestors and separate hashes, and
        /// writing one over the other is what would make a branch lose everything
        /// it owns (analyse/main-to-branch-sync.md §2).
        ///
        /// Also unlocks Apply when there is nothing to resolve: the summary itself
        /// is what the player is agreeing to, so an empty conflict list must not
        /// leave the button dead.
        /// </summary>
        internal void SetUpstreamMerge(Dictionary<string, TranslationEntry> mainContent, string mainHash)
        {
            _isUpstreamMerge = true;
            _upstreamContent = mainContent;
            _upstreamHash = mainHash;

            bool nothingToResolve = _pendingMergeWithTags == null || _pendingMergeWithTags.ConflictCount == 0;
            if (nothingToResolve)
            {
                _userMadeChoice = true;
                SetApplyButtonEnabled(true);
            }
        }

        internal void ApplyMerge()
        {
            if (!_userMadeChoice) return;
            if (_pendingMergeWithTags == null) return;

            // Apply resolutions to get final merged result
            ApplyResolutionsWithTags(_pendingMergeWithTags, _resolutions);

            if (_isUpstreamMerge)
            {
                // From the Main: separate ancestor, separate hash
                TranslatorUIManager.ApplyUpstreamMergeWithTags(
                    _pendingMergeWithTags, _upstreamContent, _upstreamHash, _incomingSettings);
            }
            else
            {
                TranslatorUIManager.ApplyMergeWithTags(_pendingMergeWithTags, _serverHash,
                    _remoteTranslationsWithTags, _incomingSettings);
            }

            SetActive(false);

            // A merge resolves LINES. The settings that came with them were
            // dropped in silence until now — including on the Main → branch
            // path, where they are often the whole point of the merge.
            if (_incomingSettings != null)
            {
                TranslatorUIManager.ReconcileSettings(
                    _ourSettingsBefore, _incomingSettings, _ancestorSettingsBefore,
                    incomingAlreadyApplied: false, sourceLabel: _settingsSourceLabel,
                    explicitRequest: _settingsExplicitRequest);
            }
        }

        /// <summary>
        /// Hand the panel the settings travelling with the incoming content, so
        /// that applying the merge can also settle them.
        ///
        /// Call it AFTER SetMergeDataWithTags, which clears this context: a
        /// later merge must never inherit the previous one's settings.
        /// </summary>
        /// <param name="explicitRequest">
        /// The player asked for THIS translation (community list) rather than
        /// merging their own line — see TranslatorUIManager.ReconcileSettings.
        /// </param>
        internal void SetSettingsContext(
            TranslationSettings ours,
            TranslationSettings incoming,
            TranslationSettings ancestor,
            string sourceLabel,
            bool explicitRequest = false)
        {
            _ourSettingsBefore = ours;
            _incomingSettings = incoming;
            _ancestorSettingsBefore = ancestor;
            _settingsSourceLabel = sourceLabel;
            _settingsExplicitRequest = explicitRequest;
        }

        /// <summary>
        /// Apply conflict resolutions to tag-aware merge result
        /// </summary>
        private void ApplyResolutionsWithTags(MergeResultWithTags result, Dictionary<string, ConflictResolution> resolutions)
        {
            var conflictsToRemove = new List<MergeConflictWithTags>();

            foreach (var conflict in result.Conflicts)
            {
                if (resolutions.TryGetValue(conflict.Key, out var resolution))
                {
                    switch (resolution)
                    {
                        case ConflictResolution.KeepLocal:
                            if (conflict.Local != null)
                                result.Merged[conflict.Key] = conflict.Local;
                            else
                                result.Merged.Remove(conflict.Key);
                            break;

                        case ConflictResolution.TakeRemote:
                            if (conflict.Remote != null)
                                result.Merged[conflict.Key] = conflict.Remote;
                            else
                                result.Merged.Remove(conflict.Key);
                            break;

                        case ConflictResolution.KeepBoth:
                            // For "keep both", use local
                            if (conflict.Local != null)
                                result.Merged[conflict.Key] = conflict.Local;
                            break;
                    }

                    conflictsToRemove.Add(conflict);
                    result.Statistics.ResolvedCount++;
                }
            }

            foreach (var conflict in conflictsToRemove)
            {
                result.Conflicts.Remove(conflict);
            }
        }

        internal void ReplaceWithRemote()
        {
            int localChanges = TranslatorCore.LocalChangesCount;
            string message = localChanges > 0
                ? $"This will discard {localChanges} local change(s) and replace with the server version.\n\nThis action cannot be undone."
                : "This will replace your local translations with the server version.\n\nThis action cannot be undone.";

            Intents.Confirm(
                "Replace with Remote",
                message,
                "Replace",
                () =>
                {
                    // Clear pending merge state
                    _pendingMergeWithTags = null;
                    _resolutions.Clear();

                    // Download and apply remote directly (discards local changes)
                    // Use async void method to avoid IL2CPP issues with async lambdas passed as Action
                    PerformReplaceWithRemote();
                },
                isDanger: true
            );
        }

        private async void PerformReplaceWithRemote()
        {
            try
            {
                await TranslatorUIManager.DownloadUpdate();
                TranslatorUIManager.RunOnMainThread(() => SetActive(false));
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[MergePanel] Replace with remote failed: {e.Message}");
            }
        }

        internal void OpenReviewPage()
        {
            // Same trap as the browser editor: the upload takes seconds and the tab
            // opens behind a fullscreen game, so the player clicks again. Each click
            // would send the whole file once more and burn one of the ten merge
            // previews allowed per minute.
            if (_reviewInFlight) return;

            var serverState = TranslatorCore.ServerState;
            if (serverState?.SiteId == null)
            {
                TranslatorCore.LogWarning("[MergePanel] Cannot open review page: no server translation");
                return;
            }

            // Use merge-preview flow: send local content to server, open returned URL
            SetReviewBusy(true);
            PerformOpenReviewPage(serverState.SiteId.Value);
        }

        /// <summary>
        /// Locks the review button while its round trip is in flight. Every exit
        /// path releases it, so a failure can never leave the button dead.
        /// </summary>
        private void SetReviewBusy(bool busy)
        {
            _reviewInFlight = busy;

            if (_reviewBtn == null) return;

            if (busy)
            {
                _reviewBtn.Busy("Opening...");
            }
            else
            {
                _reviewBtn.Enabled = true;
                _reviewBtn.Label = "Review on Website";
            }
        }

        private async void PerformOpenReviewPage(int translationId)
        {
            try
            {
                var result = await ApiClient.InitMergePreview(translationId, TranslatorCore.TranslationCache);

                // After await, we may be on a background thread (IL2CPP)
                var success = result.Success;
                var token = result.Token;
                var relativeUrl = result.Url;
                var error = result.Error;

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success && !string.IsNullOrEmpty(relativeUrl))
                    {
                        string fullUrl = ApiClient.GetMergePreviewFullUrl(relativeUrl);
                        // Debug only: the merge preview URL carries a one-time login token
                        TranslatorCore.LogDebug($"[MergePanel] Opening merge preview: {fullUrl}");
                        TranslatorCore.OpenUrlSafe(fullUrl);

                        // Listen for merge completion via SSE (auto-download result)
                        if (!string.IsNullOrEmpty(token))
                        {
                            TranslatorUIManager.StartMergeCompletionListener(token, translationId);
                        }
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[MergePanel] Failed to init merge preview: {error}");
                        // Without this the failure is silent: no tab opens and the
                        // player has no way to know whether it is still loading
                        Intents.Toast(
                            $"Could not open the review page: {error}",
                            ToastTone.Off);
                    }

                    SetReviewBusy(false);
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorCore.LogError($"[MergePanel] Open review page failed: {errorMsg}");
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    Intents.Toast(
                        $"Could not open the review page: {errorMsg}",
                        ToastTone.Off);
                    SetReviewBusy(false);
                });
            }
        }

        internal void CancelMerge()
        {
            // Clear pending state
            _pendingMergeWithTags = null;
            _resolutions.Clear();

            // Clear pending update flags
            TranslatorUIManager.HasPendingUpdate = false;
            TranslatorUIManager.PendingUpdateInfo = null;
            TranslatorUIManager.PendingUpdateDirection = UpdateDirection.None;

            SetActive(false);
        }
    }
}
