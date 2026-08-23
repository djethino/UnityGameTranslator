using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
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
        private GameObject _conflictListContent;
        private Text _summaryLabel;

        // Button references for dynamic state
        private ButtonRef _applyBtn;
        private ButtonRef _keepMineBtn;
        private ButtonRef _takeServerBtn;
        private ButtonRef _reviewBtn;
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
        private Components.HelpZone _helpZone;
        private bool _userMadeChoice = false;
        // True while the review page round trip is in flight (see OpenReviewPage)
        private bool _reviewInFlight;

        public MergePanel(UIBase owner) : base(owner)
        {
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
            ResetBulkButtonStyles();

            // Initialize resolutions to use remote by default
            foreach (var conflict in mergeResult.Conflicts)
            {
                _resolutions[conflict.Key] = ConflictResolution.TakeRemote;
            }

            RefreshConflictList();
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover a button to see what it does");

            // Adaptive card for merge conflicts — stretchVertically so the inner conflict list
            // can absorb the extra space when the user enlarges the panel.
            var card = CreateAdaptiveCard(scrollContent, "MergeCard", PanelWidth - 40, stretchVertically: true);

            var title = CreateScopedTitle(card, "Title", "Merge Conflicts", EditSide.Local);
            RegisterUIText(title);

            UIStyles.CreateSpacer(card, 5);

            // Explanation
            var explanationLabel = UIFactory.CreateLabel(card, "Explanation",
                "Both you and the server made changes. Choose which version to keep for each conflict:",
                TextAnchor.MiddleLeft);
            explanationLabel.fontSize = UIStyles.FontSizeSmall;
            explanationLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(explanationLabel.gameObject, minHeight: UIStyles.RowHeightMedium);
            RegisterUIText(explanationLabel);

            UIStyles.CreateSpacer(card, 3);

            // Summary
            _summaryLabel = UIFactory.CreateLabel(card, "Summary", "Conflicts to resolve:", TextAnchor.MiddleLeft);
            _summaryLabel.fontSize = UIStyles.FontSizeNormal;
            _summaryLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_summaryLabel.gameObject, minHeight: UIStyles.RowHeightMedium);
            RegisterExcluded(_summaryLabel);

            // Conflict list scroll view
            var scrollObj = UIFactory.CreateScrollView(card, "ConflictScroll", out _conflictListContent, out _);
            // See TranslatorPanelBase.ScrollingListHeightRule: without a preferred height this
            // list is weighed at its minimum when the panel adds up what it needs, so anything
            // below it is never budgeted for.
            UIFactory.SetLayoutElement(scrollObj, minHeight: 240, preferredHeight: 240,
                flexibleHeight: 9999, flexibleWidth: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_conflictListContent, false, false, true, true, 5, 5, 5, 5, 5);
            UIStyles.SetBackground(scrollObj, UIStyles.TroughBackground);
            UIStyles.ConfigureScrollViewNoScrollbar(scrollObj);

            UIStyles.CreateSpacer(card, 10);

            // Bulk action row
            var bulkRow = UIStyles.CreateFormRow(card, "BulkRow", UIStyles.RowHeightXLarge);
            var bulkLayout = bulkRow.GetComponent<HorizontalLayoutGroup>();
            if (bulkLayout != null) bulkLayout.childAlignment = TextAnchor.MiddleCenter;

            // All button callbacks use the static singleton to avoid IL2CPP 'this' capture issues
            _keepMineBtn = CreateSecondaryButton(bulkRow, "UseAllLocalBtn", "Keep My Changes", 120);
            _keepMineBtn.OnClick += () => TranslatorUIManager.MergePanel?.UseAllLocal();
            RegisterUIText(_keepMineBtn.ButtonText);
            _helpZone?.Describe(_keepMineBtn.Component.gameObject,
                "Resolve every conflict with YOUR version of the line");

            _takeServerBtn = CreateSecondaryButton(bulkRow, "UseAllRemoteBtn", "Take Server", 100);
            _takeServerBtn.OnClick += () => TranslatorUIManager.MergePanel?.UseAllRemote();
            RegisterUIText(_takeServerBtn.ButtonText);
            _helpZone?.Describe(_takeServerBtn.Component.gameObject,
                "Resolve every conflict with the website's version of the line");

            // Apply Merge - starts disabled until user makes a choice
            _applyBtn = CreatePrimaryButton(bulkRow, "ApplyBtn", "Apply Merge");
            // ⚠ Writes this machine's translation and publishes nothing — the whole merge panel
            // settles a local file. Marked so the three buttons of this row say the same thing.
            ScopeMarks.Adorn(_applyBtn, EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false));
            _applyBtn.OnClick += () => TranslatorUIManager.MergePanel?.ApplyMerge();
            RegisterUIText(_applyBtn.ButtonText);
            SetApplyButtonEnabled(false);
            _helpZone?.Describe(_applyBtn.Component.gameObject,
                "Save the merged result: non-conflicting changes from both sides plus your choices above");

            // Bottom buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => TranslatorUIManager.MergePanel?.CancelMerge();
            RegisterUIText(cancelBtn.ButtonText);
            _helpZone?.Describe(cancelBtn.Component.gameObject,
                "Close without changing anything — you can merge later");

            var replaceBtn = CreateSecondaryButton(buttonRow, "ReplaceBtn", "Replace with Server", 130);
            UIStyles.SetBackground(replaceBtn.Component.gameObject, UIStyles.ButtonDanger);
            // ⚠ Overwrites the local file with the online one. The most destructive act on this
            // row, and it was the one saying nothing about where it lands.
            ScopeMarks.Adorn(replaceBtn, EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false));
            replaceBtn.OnClick += () => TranslatorUIManager.MergePanel?.ReplaceWithRemote();
            RegisterUIText(replaceBtn.ButtonText);
            _helpZone?.Describe(replaceBtn.Component.gameObject,
                "Throw away ALL your local changes and take the website's version as-is");

            // Review on Website in the footer (secondary action)
            _reviewBtn = CreateSecondaryButton(buttonRow, "ReviewBtn", "Review on Website", 115);
            var reviewBtn = _reviewBtn;
            UIStyles.SetBackground(reviewBtn.Component.gameObject, UIStyles.ButtonLink);
            // ⚠ The same act as the main panel's Review Branches: it rewrites the PUBLISHED Main
            // and never comes back here on its own.
            ScopeMarks.Adorn(reviewBtn, EditScope.SideAfter(onThisMachine: false, yourPublishedCopy: true));
            reviewBtn.OnClick += () => TranslatorUIManager.MergePanel?.OpenReviewPage();
            RegisterUIText(reviewBtn.ButtonText);
            _helpZone?.Describe(reviewBtn.Component.gameObject,
                "Open this merge in your browser: bigger screen, search, and line-by-line tools");
        }

        private void RefreshConflictList()
        {
            if (_conflictListContent == null) return;
            if (_pendingMergeWithTags == null) return;

            // Clear existing items (manual iteration for IL2CPP compatibility)
            for (int i = _conflictListContent.transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_conflictListContent.transform.GetChild(i).gameObject);
            }

            var stats = _pendingMergeWithTags.Statistics;
            int conflictCount = _pendingMergeWithTags.Conflicts.Count;

            _summaryLabel.text = conflictCount > 0
                ? Tr($"{conflictCount} conflict(s) to resolve") + $"  |  {stats.GetSummary()}"
                : Tr("No conflicts! All changes merged automatically.") + $"  |  {stats.GetSummary()}";

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
            var row = UIFactory.CreateVerticalGroup(_conflictListContent, $"Conflict_{key}", false, false, true, true, 3);
            UIFactory.SetLayoutElement(row, minHeight: UIStyles.MultiLineMedium, flexibleWidth: 9999);

            // Key label
            var keyLabel = UIFactory.CreateLabel(row, "Key", $"Key: {key}", TextAnchor.MiddleLeft);
            keyLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(keyLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Values row
            var valuesRow = UIFactory.CreateHorizontalGroup(row, "Values", false, false, true, true, 10);
            UIFactory.SetLayoutElement(valuesRow, minHeight: UIStyles.CodeDisplayHeight);

            // Local value
            var localGroup = UIFactory.CreateVerticalGroup(valuesRow, "Local", false, false, true, true, 2);
            UIFactory.SetLayoutElement(localGroup, flexibleWidth: 9999);

            // 🔴 The tag as the CHIP the website draws, not as "[AI]" in coloured words.
            //
            // Naming it in prose meant translating the letter into a word — and the words were
            // this panel's own, so H read "Human" here, "H" on the site's tables and a green band
            // in the bar three inches away. The chip is the same square in all three, from the
            // same library. Side and tag also stop competing for one label's colour: the side is
            // told in plain text, the tag by its own mark.
            var localHead = UIFactory.CreateHorizontalGroup(localGroup, "LocalHead", false, false, true, true, 6,
                                                            default, default, TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(localHead, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

            var localLbl = UIFactory.CreateLabel(localHead, "LocalLabel", "Local:", TextAnchor.MiddleLeft);
            localLbl.fontSize = UIStyles.FontSizeSmall;
            localLbl.color = UIStyles.TextSecondary;
            if (localTag != null) UIStyles.CreateTagChip(localHead, localTag, out _);

            var localValueLbl = UIFactory.CreateLabel(localGroup, "LocalValue", localValue, TextAnchor.MiddleLeft);
            localValueLbl.fontSize = UIStyles.FontSizeSmall;
            localValueLbl.color = UIStyles.TextAccent;

            // Remote value
            var remoteGroup = UIFactory.CreateVerticalGroup(valuesRow, "Remote", false, false, true, true, 2);
            UIFactory.SetLayoutElement(remoteGroup, flexibleWidth: 9999);

            var remoteHead = UIFactory.CreateHorizontalGroup(remoteGroup, "RemoteHead", false, false, true, true, 6,
                                                             default, default, TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(remoteHead, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

            var remoteLbl = UIFactory.CreateLabel(remoteHead, "RemoteLabel", "Server:", TextAnchor.MiddleLeft);
            remoteLbl.fontSize = UIStyles.FontSizeSmall;
            remoteLbl.color = UIStyles.TextSecondary;
            if (remoteTag != null) UIStyles.CreateTagChip(remoteHead, remoteTag, out _);

            var remoteValueLbl = UIFactory.CreateLabel(remoteGroup, "RemoteValue", remoteValue, TextAnchor.MiddleLeft);
            remoteValueLbl.fontSize = UIStyles.FontSizeSmall;
            remoteValueLbl.color = UIStyles.StatusSuccess;

            // Choice buttons (using ButtonRef instead of Toggle for IL2CPP compatibility)
            var choiceRow = UIFactory.CreateHorizontalGroup(row, "Choices", false, false, true, true, 10);
            UIFactory.SetLayoutElement(choiceRow, minHeight: UIStyles.RowHeightMedium);

            bool isLocal = _resolutions.TryGetValue(key, out var res) && res == ConflictResolution.KeepLocal;

            var localBtn = UIFactory.CreateButton(choiceRow, "UseLocalBtn", "Use Local");
            UIFactory.SetLayoutElement(localBtn.Component.gameObject, minWidth: 100, minHeight: UIStyles.RowHeightNormal);

            var remoteBtn = UIFactory.CreateButton(choiceRow, "UseRemoteBtn", "Use Server");
            UIFactory.SetLayoutElement(remoteBtn.Component.gameObject, minWidth: 100, minHeight: UIStyles.RowHeightNormal);

            // Style the active button
            UpdateChoiceButtonStyles(localBtn, remoteBtn, isLocal);

            // Capture key by value for closures
            string capturedKey = key;

            localBtn.OnClick += () =>
            {
                var self = TranslatorUIManager.MergePanel;
                if (self == null) return;
                self._resolutions[capturedKey] = ConflictResolution.KeepLocal;
                self.UpdateChoiceButtonStyles(localBtn, remoteBtn, true);
                self.OnUserMadeChoice();
            };

            remoteBtn.OnClick += () =>
            {
                var self = TranslatorUIManager.MergePanel;
                if (self == null) return;
                self._resolutions[capturedKey] = ConflictResolution.TakeRemote;
                self.UpdateChoiceButtonStyles(localBtn, remoteBtn, false);
                self.OnUserMadeChoice();
            };
        }

        /// <summary>
        /// Update visual styling for choice buttons to show which is selected.
        /// </summary>
        private void UpdateChoiceButtonStyles(ButtonRef localBtn, ButtonRef remoteBtn, bool isLocalSelected)
        {
            if (isLocalSelected)
            {
                UIStyles.SetBackground(localBtn.Component.gameObject, UIStyles.TextAccent);
                localBtn.ButtonText.fontStyle = FontStyle.Bold;
                UIStyles.SetBackground(remoteBtn.Component.gameObject, UIStyles.InputBackground);
                remoteBtn.ButtonText.fontStyle = FontStyle.Normal;
            }
            else
            {
                UIStyles.SetBackground(localBtn.Component.gameObject, UIStyles.InputBackground);
                localBtn.ButtonText.fontStyle = FontStyle.Normal;
                UIStyles.SetBackground(remoteBtn.Component.gameObject, UIStyles.StatusSuccess);
                remoteBtn.ButtonText.fontStyle = FontStyle.Bold;
            }
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
            if (enabled)
            {
                UIStyles.SetBackground(_applyBtn.Component.gameObject, UIStyles.ButtonSuccess);
                _applyBtn.ButtonText.color = Color.white;
            }
            else
            {
                UIStyles.SetBackground(_applyBtn.Component.gameObject, UIStyles.InputBackground);
                _applyBtn.ButtonText.color = UIStyles.TextMuted;
            }
        }

        private void ResetBulkButtonStyles()
        {
            if (_keepMineBtn != null)
            {
                UIStyles.SetBackground(_keepMineBtn.Component.gameObject, UIStyles.ButtonSecondary);
                _keepMineBtn.ButtonText.fontStyle = FontStyle.Normal;
            }
            if (_takeServerBtn != null)
            {
                UIStyles.SetBackground(_takeServerBtn.Component.gameObject, UIStyles.ButtonSecondary);
                _takeServerBtn.ButtonText.fontStyle = FontStyle.Normal;
            }
        }

        private void HighlightBulkButton(bool isLocal)
        {
            if (_keepMineBtn != null)
            {
                UIStyles.SetBackground(_keepMineBtn.Component.gameObject,
                    isLocal ? UIStyles.TextAccent : UIStyles.ButtonSecondary);
                _keepMineBtn.ButtonText.fontStyle = isLocal ? FontStyle.Bold : FontStyle.Normal;
            }
            if (_takeServerBtn != null)
            {
                UIStyles.SetBackground(_takeServerBtn.Component.gameObject,
                    !isLocal ? UIStyles.StatusSuccess : UIStyles.ButtonSecondary);
                _takeServerBtn.ButtonText.fontStyle = !isLocal ? FontStyle.Bold : FontStyle.Normal;
            }
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

            TranslatorUIManager.ConfirmationPanel?.Show(
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

            if (_reviewBtn?.Component != null)
                _reviewBtn.Component.interactable = !busy;

            if (_reviewBtn?.ButtonText != null)
                SetDynamicText(_reviewBtn.ButtonText, busy ? "Opening..." : "Review on Website");
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
                        TranslatorUIManager.StatusOverlay?.ShowToast(
                            $"Could not open the review page: {error}",
                            Panels.StatusOverlay.ToastTone.Off);
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
                    TranslatorUIManager.StatusOverlay?.ShowToast(
                        $"Could not open the review page: {errorMsg}",
                        Panels.StatusOverlay.ToastTone.Off);
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
