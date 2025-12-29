using System;
using System.Collections.Generic;
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
            _useTagAwareMerge = true;
            _pendingMergeWithTags = mergeResult;
            _remoteTranslationsWithTags = remoteTranslations;
            _pendingMerge = null;
            _remoteTranslations = null;
            _serverHash = serverHash ?? TranslatorCore.ServerState?.Hash;
            _resolutions.Clear();

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
            if (bulkLayout != null) bulkLayout.childAlignment = TextAnchor.MiddleCenter; // Center the buttons

            var useAllLocalBtn = CreateSecondaryButton(bulkRow, "UseAllLocalBtn", "Use All Local", 110);
            useAllLocalBtn.OnClick += UseAllLocal;
            RegisterUIText(useAllLocalBtn.ButtonText);

            var useAllRemoteBtn = CreateSecondaryButton(bulkRow, "UseAllRemoteBtn", "Use All Remote", 115);
            useAllRemoteBtn.OnClick += UseAllRemote;
            RegisterUIText(useAllRemoteBtn.ButtonText);

            // Review on website button (Main only - useful for comparing branches)
            var reviewBtn = CreateSecondaryButton(bulkRow, "ReviewBtn", "Review on Website", 140);
            UIStyles.SetBackground(reviewBtn.Component.gameObject, UIStyles.ButtonLink);
            reviewBtn.OnClick += OpenReviewPage;
            RegisterUIText(reviewBtn.ButtonText);

            // Bottom buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += CancelMerge;
            RegisterUIText(cancelBtn.ButtonText);

            var replaceBtn = CreateSecondaryButton(buttonRow, "ReplaceBtn", "Replace with Remote", 155);
            UIStyles.SetBackground(replaceBtn.Component.gameObject, UIStyles.ButtonDanger);
            replaceBtn.OnClick += ReplaceWithRemote;
            RegisterUIText(replaceBtn.ButtonText);

            var applyBtn = CreatePrimaryButton(buttonRow, "ApplyBtn", "Apply Merge");
            UIStyles.SetBackground(applyBtn.Component.gameObject, UIStyles.ButtonSuccess);
            applyBtn.OnClick += ApplyMerge;
            RegisterUIText(applyBtn.ButtonText);
        }

        private void RefreshConflictList()
        {
            if (_conflictListContent == null) return;
            if (!_useTagAwareMerge && _pendingMerge == null) return;
            if (_useTagAwareMerge && _pendingMergeWithTags == null) return;

            // Clear existing items
            foreach (Transform child in _conflictListContent.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            if (_useTagAwareMerge)
            {
                int count = _pendingMergeWithTags.Conflicts.Count;
                _summaryLabel.text = $"{count} conflict(s) to resolve:";

                foreach (var conflict in _pendingMergeWithTags.Conflicts)
                {
                    CreateConflictRowWithTags(conflict);
                }
            }
            else
            {
                int count = _pendingMerge.Conflicts.Count;
                _summaryLabel.text = $"{count} conflict(s) to resolve:";

                foreach (var conflict in _pendingMerge.Conflicts)
                {
                    CreateConflictRow(conflict);
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

            // Choice toggles
            var choiceRow = UIFactory.CreateHorizontalGroup(row, "Choices", false, false, true, true, 20);
            UIFactory.SetLayoutElement(choiceRow, minHeight: UIStyles.RowHeightMedium);

            var localToggleObj = UIFactory.CreateToggle(choiceRow, "LocalToggle", out var localToggle, out var localToggleLabel);
            localToggleLabel.text = "Use Local";
            localToggle.isOn = _resolutions.TryGetValue(key, out var res) && res == ConflictResolution.KeepLocal;
            UIFactory.SetLayoutElement(localToggleObj, minWidth: 100);

            var remoteToggleObj = UIFactory.CreateToggle(choiceRow, "RemoteToggle", out var remoteToggle, out var remoteToggleLabel);
            remoteToggleLabel.text = "Use Remote";
            remoteToggle.isOn = !localToggle.isOn;
            UIFactory.SetLayoutElement(remoteToggleObj, minWidth: 100);

            localToggle.onValueChanged.AddListener((val) =>
            {
                if (val)
                {
                    remoteToggle.isOn = false;
                    _resolutions[key] = ConflictResolution.KeepLocal;
                }
            });

            remoteToggle.onValueChanged.AddListener((val) =>
            {
                if (val)
                {
                    localToggle.isOn = false;
                    _resolutions[key] = ConflictResolution.TakeRemote;
                }
            });
        }

        private void UseAllLocal()
        {
            if (_useTagAwareMerge)
            {
                foreach (var conflict in _pendingMergeWithTags.Conflicts)
                {
                    _resolutions[conflict.Key] = ConflictResolution.KeepLocal;
                }
            }
            else
            {
                foreach (var conflict in _pendingMerge.Conflicts)
                {
                    _resolutions[conflict.Key] = ConflictResolution.KeepLocal;
                }
            }
            RefreshConflictList();
        }

        private void UseAllRemote()
        {
            if (_useTagAwareMerge)
            {
                foreach (var conflict in _pendingMergeWithTags.Conflicts)
                {
                    _resolutions[conflict.Key] = ConflictResolution.TakeRemote;
                }
            }
            else
            {
                foreach (var conflict in _pendingMerge.Conflicts)
                {
                    _resolutions[conflict.Key] = ConflictResolution.TakeRemote;
                }
            }
            RefreshConflictList();
        }

        private void ApplyMerge()
        {
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

        private void ReplaceWithRemote()
        {
            int localChanges = TranslatorCore.LocalChangesCount;
            string message = localChanges > 0
                ? $"This will discard {localChanges} local change(s) and replace with the server version.\n\nThis action cannot be undone."
                : "This will replace your local translations with the server version.\n\nThis action cannot be undone.";

            TranslatorUIManager.ConfirmationPanel?.Show(
                "Replace with Remote",
                message,
                "Replace",
                async () =>
                {
                    // Clear pending merge state
                    _pendingMerge = null;
                    _resolutions.Clear();

                    // Download and apply remote directly (discards local changes)
                    await TranslatorUIManager.DownloadUpdate();

                    SetActive(false);
                },
                isDanger: true
            );
        }

        private void OpenReviewPage()
        {
            // Open the website merge review page for this translation
            string uuid = TranslatorCore.FileUuid;
            if (string.IsNullOrEmpty(uuid))
            {
                TranslatorCore.LogWarning("[MergePanel] Cannot open review page: no UUID");
                return;
            }

            string url = ApiClient.GetMergeReviewUrl(uuid);
            TranslatorCore.LogInfo($"[MergePanel] Opening review page: {url}");
            Application.OpenURL(url);
        }

        private void CancelMerge()
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
