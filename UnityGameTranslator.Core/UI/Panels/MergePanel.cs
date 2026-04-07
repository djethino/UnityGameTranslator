using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Merge panel for resolving conflicts between local and remote translations.
    /// Supports both legacy (string) and new (TranslationEntry with tags) merge results.
    /// </summary>
    public class MergePanel : TranslatorPanelBase
    {
        public override string Name => "Merge Translations";
        public override int MinWidth => 650;
        public override int MinHeight => 400;
        public override int PanelWidth => 650;
        public override int PanelHeight => 500;

        protected override int MinPanelHeight => 400;

        // Legacy merge (string-based)
        private MergeResult _pendingMerge;
        private Dictionary<string, string> _remoteTranslations;

        // Tag-aware merge (TranslationEntry-based)
        private MergeResultWithTags _pendingMergeWithTags;
        private Dictionary<string, TranslationEntry> _remoteTranslationsWithTags;
        private bool _useTagAwareMerge = false;

        private Dictionary<string, ConflictResolution> _resolutions = new Dictionary<string, ConflictResolution>();
        private string _serverHash;
        private GameObject _conflictListContent;
        private Text _summaryLabel;

        // Button references for dynamic state
        private ButtonRef _applyBtn;
        private ButtonRef _keepMineBtn;
        private ButtonRef _takeServerBtn;
        private bool _userMadeChoice = false;

        public MergePanel(UIBase owner) : base(owner)
        {
        }

        /// <summary>
        /// Set merge data (legacy string-based merge).
        /// </summary>
        public void SetMergeData(MergeResult mergeResult, Dictionary<string, string> remoteTranslations, string serverHash = null)
        {
            _useTagAwareMerge = false;
            _pendingMerge = mergeResult;
            _remoteTranslations = remoteTranslations;
            _pendingMergeWithTags = null;
            _remoteTranslationsWithTags = null;
            _serverHash = serverHash ?? TranslatorCore.ServerState?.Hash;
            _resolutions.Clear();
            _userMadeChoice = false;
            SetApplyButtonEnabled(false);
            ResetBulkButtonStyles();

            // Initialize resolutions to use remote by default
            foreach (var conflict in mergeResult.Conflicts)
            {
                _resolutions[conflict.Key] = ConflictResolution.TakeRemote;
            }

            RefreshConflictList();
        }

        /// <summary>
        /// Set merge data with tags (tag-aware merge).
        /// </summary>
        public void SetMergeDataWithTags(MergeResultWithTags mergeResult, Dictionary<string, TranslationEntry> remoteTranslations, string serverHash = null)
        {
            TranslatorCore.LogInfo($"[MergePanel] SetMergeDataWithTags called - conflicts={mergeResult?.Conflicts?.Count ?? -1}");
            _useTagAwareMerge = true;
            _pendingMergeWithTags = mergeResult;
            _remoteTranslationsWithTags = remoteTranslations;
            _pendingMerge = null;
            _remoteTranslations = null;
            _serverHash = serverHash ?? TranslatorCore.ServerState?.Hash;
            _resolutions.Clear();
            _userMadeChoice = false;
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

            // Adaptive card for merge conflicts - sizes to content (PanelWidth - 2*PanelPadding)
            var card = CreateAdaptiveCard(scrollContent, "MergeCard", PanelWidth - 40);

            var title = CreateTitle(card, "Title", "Merge Conflicts");
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
            RegisterUIText(_summaryLabel);

            // Conflict list scroll view
            var scrollObj = UIFactory.CreateScrollView(card, "ConflictScroll", out _conflictListContent, out _);
            UIFactory.SetLayoutElement(scrollObj, flexibleHeight: 9999, flexibleWidth: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_conflictListContent, false, false, true, true, 5, 5, 5, 5, 5);
            UIStyles.SetBackground(scrollObj, UIStyles.InputBackground);
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

            _takeServerBtn = CreateSecondaryButton(bulkRow, "UseAllRemoteBtn", "Take Server", 100);
            _takeServerBtn.OnClick += () => TranslatorUIManager.MergePanel?.UseAllRemote();
            RegisterUIText(_takeServerBtn.ButtonText);

            // Apply Merge - starts disabled until user makes a choice
            _applyBtn = CreatePrimaryButton(bulkRow, "ApplyBtn", "Apply Merge");
            _applyBtn.OnClick += () => TranslatorUIManager.MergePanel?.ApplyMerge();
            RegisterUIText(_applyBtn.ButtonText);
            SetApplyButtonEnabled(false);

            // Bottom buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => TranslatorUIManager.MergePanel?.CancelMerge();
            RegisterUIText(cancelBtn.ButtonText);

            var replaceBtn = CreateSecondaryButton(buttonRow, "ReplaceBtn", "Replace with Remote", 155);
            UIStyles.SetBackground(replaceBtn.Component.gameObject, UIStyles.ButtonDanger);
            replaceBtn.OnClick += () => TranslatorUIManager.MergePanel?.ReplaceWithRemote();
            RegisterUIText(replaceBtn.ButtonText);

            // Review on Website in the footer (secondary action)
            var reviewBtn = CreateSecondaryButton(buttonRow, "ReviewBtn", "Review on Website", 140);
            UIStyles.SetBackground(reviewBtn.Component.gameObject, UIStyles.ButtonLink);
            reviewBtn.OnClick += () => TranslatorUIManager.MergePanel?.OpenReviewPage();
            RegisterUIText(reviewBtn.ButtonText);
        }

        private void RefreshConflictList()
        {
            if (_conflictListContent == null) return;
            if (!_useTagAwareMerge && _pendingMerge == null) return;
            if (_useTagAwareMerge && _pendingMergeWithTags == null) return;

            // Clear existing items (manual iteration for IL2CPP compatibility)
            for (int i = _conflictListContent.transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_conflictListContent.transform.GetChild(i).gameObject);
            }

            if (_useTagAwareMerge)
            {
                var stats = _pendingMergeWithTags.Statistics;
                int conflictCount = _pendingMergeWithTags.Conflicts.Count;

                if (conflictCount > 0)
                {
                    _summaryLabel.text = $"{conflictCount} conflict(s) to resolve  |  {stats.GetSummary()}";
                }
                else
                {
                    _summaryLabel.text = $"No conflicts! All changes merged automatically.  |  {stats.GetSummary()}";
                }

                var conflicts = _pendingMergeWithTags.Conflicts;
                for (int i = 0; i < conflicts.Count; i++)
                {
                    CreateConflictRowWithTags(conflicts[i]);
                }
            }
            else
            {
                var stats = _pendingMerge.Statistics;
                int conflictCount = _pendingMerge.Conflicts.Count;

                if (conflictCount > 0)
                {
                    _summaryLabel.text = $"{conflictCount} conflict(s) to resolve  |  {stats.GetSummary()}";
                }
                else
                {
                    _summaryLabel.text = $"No conflicts! All changes merged automatically.  |  {stats.GetSummary()}";
                }

                var conflicts = _pendingMerge.Conflicts;
                for (int i = 0; i < conflicts.Count; i++)
                {
                    CreateConflictRow(conflicts[i]);
                }
            }
        }

        private void CreateConflictRow(MergeConflict conflict)
        {
            CreateConflictRowInternal(conflict.Key, conflict.LocalValue ?? "(none)", null, conflict.RemoteValue ?? "(none)", null);
        }

        private void CreateConflictRowWithTags(MergeConflictWithTags conflict)
        {
            string localValue = conflict.Local?.Value ?? "(none)";
            string localTag = conflict.Local?.Tag;
            string remoteValue = conflict.Remote?.Value ?? "(none)";
            string remoteTag = conflict.Remote?.Tag;

            CreateConflictRowInternal(conflict.Key, localValue, localTag, remoteValue, remoteTag);
        }

        private string GetTagDisplayName(string tag)
        {
            switch (tag)
            {
                case "A": return "[AI]";
                case "H": return "[Human]";
                case "V": return "[Validated]";
                default: return "";
            }
        }

        private Color GetTagColor(string tag)
        {
            switch (tag)
            {
                case "A": return UIStyles.StatusWarning;  // Yellow for AI
                case "H": return UIStyles.StatusSuccess;  // Green for Human
                case "V": return UIStyles.TextAccent;     // Blue for Validated
                default: return UIStyles.TextMuted;
            }
        }

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

            // Local label with tag if available
            string localLabelText = localTag != null ? $"Local {GetTagDisplayName(localTag)}:" : "Local:";
            var localLbl = UIFactory.CreateLabel(localGroup, "LocalLabel", localLabelText, TextAnchor.MiddleLeft);
            localLbl.fontSize = UIStyles.FontSizeSmall;
            if (localTag != null) localLbl.color = GetTagColor(localTag);

            var localValueLbl = UIFactory.CreateLabel(localGroup, "LocalValue", localValue, TextAnchor.MiddleLeft);
            localValueLbl.fontSize = UIStyles.FontSizeSmall;
            localValueLbl.color = UIStyles.TextAccent;

            // Remote value
            var remoteGroup = UIFactory.CreateVerticalGroup(valuesRow, "Remote", false, false, true, true, 2);
            UIFactory.SetLayoutElement(remoteGroup, flexibleWidth: 9999);

            // Remote label with tag if available
            string remoteLabelText = remoteTag != null ? $"Remote {GetTagDisplayName(remoteTag)}:" : "Remote:";
            var remoteLbl = UIFactory.CreateLabel(remoteGroup, "RemoteLabel", remoteLabelText, TextAnchor.MiddleLeft);
            remoteLbl.fontSize = UIStyles.FontSizeSmall;
            if (remoteTag != null) remoteLbl.color = GetTagColor(remoteTag);

            var remoteValueLbl = UIFactory.CreateLabel(remoteGroup, "RemoteValue", remoteValue, TextAnchor.MiddleLeft);
            remoteValueLbl.fontSize = UIStyles.FontSizeSmall;
            remoteValueLbl.color = UIStyles.StatusSuccess;

            // Choice buttons (using ButtonRef instead of Toggle for IL2CPP compatibility)
            var choiceRow = UIFactory.CreateHorizontalGroup(row, "Choices", false, false, true, true, 10);
            UIFactory.SetLayoutElement(choiceRow, minHeight: UIStyles.RowHeightMedium);

            bool isLocal = _resolutions.TryGetValue(key, out var res) && res == ConflictResolution.KeepLocal;

            var localBtn = UIFactory.CreateButton(choiceRow, "UseLocalBtn", "Use Local");
            UIFactory.SetLayoutElement(localBtn.Component.gameObject, minWidth: 100, minHeight: UIStyles.RowHeightNormal);

            var remoteBtn = UIFactory.CreateButton(choiceRow, "UseRemoteBtn", "Use Remote");
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
                UIStyles.SetBackground(_keepMineBtn.Component.gameObject, UIStyles.CardBackground);
                _keepMineBtn.ButtonText.fontStyle = FontStyle.Normal;
            }
            if (_takeServerBtn != null)
            {
                UIStyles.SetBackground(_takeServerBtn.Component.gameObject, UIStyles.CardBackground);
                _takeServerBtn.ButtonText.fontStyle = FontStyle.Normal;
            }
        }

        private void HighlightBulkButton(bool isLocal)
        {
            if (_keepMineBtn != null)
            {
                UIStyles.SetBackground(_keepMineBtn.Component.gameObject,
                    isLocal ? UIStyles.TextAccent : UIStyles.CardBackground);
                _keepMineBtn.ButtonText.fontStyle = isLocal ? FontStyle.Bold : FontStyle.Normal;
            }
            if (_takeServerBtn != null)
            {
                UIStyles.SetBackground(_takeServerBtn.Component.gameObject,
                    !isLocal ? UIStyles.StatusSuccess : UIStyles.CardBackground);
                _takeServerBtn.ButtonText.fontStyle = !isLocal ? FontStyle.Bold : FontStyle.Normal;
            }
            OnUserMadeChoice();
        }

        private void SetAllResolutions(ConflictResolution resolution)
        {
            TranslatorCore.LogInfo($"[MergePanel] SetAllResolutions({resolution}) - _useTagAwareMerge={_useTagAwareMerge}, " +
                $"_pendingMergeWithTags null={_pendingMergeWithTags == null}, _pendingMerge null={_pendingMerge == null}, " +
                $"_resolutions null={_resolutions == null}, _resolutions count={_resolutions?.Count ?? -1}");

            if (_resolutions == null)
            {
                _resolutions = new Dictionary<string, ConflictResolution>();
            }

            // Determine conflicts source - check both since _useTagAwareMerge might not be reliable on IL2CPP
            List<string> conflictKeys = new List<string>();

            if (_pendingMergeWithTags != null && _pendingMergeWithTags.Conflicts != null && _pendingMergeWithTags.Conflicts.Count > 0)
            {
                var conflicts = _pendingMergeWithTags.Conflicts;
                for (int i = 0; i < conflicts.Count; i++)
                {
                    conflictKeys.Add(conflicts[i].Key);
                }
                TranslatorCore.LogInfo($"[MergePanel] Using tag-aware conflicts: {conflictKeys.Count} keys");
            }
            else if (_pendingMerge != null && _pendingMerge.Conflicts != null && _pendingMerge.Conflicts.Count > 0)
            {
                var conflicts = _pendingMerge.Conflicts;
                for (int i = 0; i < conflicts.Count; i++)
                {
                    conflictKeys.Add(conflicts[i].Key);
                }
                TranslatorCore.LogInfo($"[MergePanel] Using legacy conflicts: {conflictKeys.Count} keys");
            }
            else
            {
                TranslatorCore.LogError("[MergePanel] No conflicts found in either merge result");
                return;
            }

            for (int i = 0; i < conflictKeys.Count; i++)
            {
                _resolutions[conflictKeys[i]] = resolution;
            }

            RefreshConflictList();
        }

        internal void ApplyMerge()
        {
            if (!_userMadeChoice) return;

            if (_useTagAwareMerge)
            {
                if (_pendingMergeWithTags == null) return;

                // Apply resolutions to get final merged result
                ApplyResolutionsWithTags(_pendingMergeWithTags, _resolutions);

                // Use TranslatorUIManager.ApplyMergeWithTags to preserve tags
                TranslatorUIManager.ApplyMergeWithTags(_pendingMergeWithTags, _serverHash, _remoteTranslationsWithTags);
            }
            else
            {
                if (_pendingMerge == null) return;

                // Apply resolutions to get final merged result
                TranslationMerger.ApplyResolutions(_pendingMerge, _resolutions);

                // Use TranslatorUIManager.ApplyMerge to handle hash updates
                TranslatorUIManager.ApplyMerge(_pendingMerge, _serverHash, _remoteTranslations);
            }

            SetActive(false);
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
                    _pendingMerge = null;
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
            var serverState = TranslatorCore.ServerState;
            if (serverState?.SiteId == null)
            {
                TranslatorCore.LogWarning("[MergePanel] Cannot open review page: no server translation");
                return;
            }

            // Use merge-preview flow: send local content to server, open returned URL
            PerformOpenReviewPage(serverState.SiteId.Value);
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
                        TranslatorCore.LogInfo($"[MergePanel] Opening merge preview: {fullUrl}");
                        Application.OpenURL(fullUrl);

                        // Listen for merge completion via SSE (auto-download result)
                        if (!string.IsNullOrEmpty(token))
                        {
                            TranslatorUIManager.StartMergeCompletionListener(token, translationId);
                        }
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[MergePanel] Failed to init merge preview: {error}");
                    }
                });
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[MergePanel] Open review page failed: {e.Message}");
            }
        }

        internal void CancelMerge()
        {
            // Clear pending state
            _pendingMerge = null;
            _resolutions.Clear();

            // Clear pending update flags
            TranslatorUIManager.HasPendingUpdate = false;
            TranslatorUIManager.PendingUpdateInfo = null;
            TranslatorUIManager.PendingUpdateDirection = UpdateDirection.None;

            SetActive(false);
        }
    }
}
