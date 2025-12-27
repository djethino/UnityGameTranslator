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
    /// </summary>
    public class MergePanel : TranslatorPanelBase
    {
        public override string Name => "Merge Translations";
        public override int MinWidth => 650;
        public override int MinHeight => 500;
        public override int PanelWidth => 650;
        public override int PanelHeight => 500;

        private MergeResult _pendingMerge;
        private Dictionary<string, ConflictResolution> _resolutions = new Dictionary<string, ConflictResolution>();
        private Dictionary<string, string> _remoteTranslations;
        private string _serverHash;
        private GameObject _conflictListContent;
        private Text _summaryLabel;

        public MergePanel(UIBase owner) : base(owner)
        {
        }

        public void SetMergeData(MergeResult mergeResult, Dictionary<string, string> remoteTranslations, string serverHash = null)
        {
            _pendingMerge = mergeResult;
            _remoteTranslations = remoteTranslations;
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
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, 600);

            // Adaptive card for merge conflicts - sizes to content
            var card = CreateAdaptiveCard(scrollContent, "MergeCard", 600);

            CreateTitle(card, "Title", "Merge Conflicts");

            UIStyles.CreateSpacer(card, 5);

            // Summary
            _summaryLabel = UIFactory.CreateLabel(card, "Summary", "Conflicts to resolve:", TextAnchor.MiddleLeft);
            _summaryLabel.fontSize = UIStyles.FontSizeNormal;
            _summaryLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_summaryLabel.gameObject, minHeight: 25);

            // Conflict list scroll view
            var scrollObj = UIFactory.CreateScrollView(card, "ConflictScroll", out _conflictListContent, out _);
            UIFactory.SetLayoutElement(scrollObj, flexibleHeight: 9999, flexibleWidth: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_conflictListContent, false, false, true, true, 5, 5, 5, 5, 5);
            UIStyles.SetBackground(scrollObj, UIStyles.InputBackground);

            UIStyles.CreateSpacer(card, 10);

            // Bulk action row
            var bulkRow = UIFactory.CreateHorizontalGroup(card, "BulkRow", false, false, true, true, 10);
            UIFactory.SetLayoutElement(bulkRow, minHeight: 35);
            var bulkLayout = bulkRow.GetComponent<HorizontalLayoutGroup>();
            if (bulkLayout != null) bulkLayout.childAlignment = TextAnchor.MiddleCenter;

            var useAllLocalBtn = CreateSecondaryButton(bulkRow, "UseAllLocalBtn", "Use All Local", 110);
            useAllLocalBtn.OnClick += UseAllLocal;

            var useAllRemoteBtn = CreateSecondaryButton(bulkRow, "UseAllRemoteBtn", "Use All Remote", 115);
            useAllRemoteBtn.OnClick += UseAllRemote;

            // Bottom buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += CancelMerge;

            var replaceBtn = CreateSecondaryButton(buttonRow, "ReplaceBtn", "Replace with Remote", 155);
            UIStyles.SetBackground(replaceBtn.Component.gameObject, UIStyles.ButtonDanger);
            replaceBtn.OnClick += ReplaceWithRemote;

            var applyBtn = CreatePrimaryButton(buttonRow, "ApplyBtn", "Apply Merge");
            UIStyles.SetBackground(applyBtn.Component.gameObject, UIStyles.ButtonSuccess);
            applyBtn.OnClick += ApplyMerge;
        }

        private void RefreshConflictList()
        {
            if (_conflictListContent == null || _pendingMerge == null) return;

            // Clear existing items
            foreach (Transform child in _conflictListContent.transform)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            int count = _pendingMerge.Conflicts.Count;
            _summaryLabel.text = $"{count} conflict(s) to resolve:";

            foreach (var conflict in _pendingMerge.Conflicts)
            {
                CreateConflictRow(conflict);
            }
        }

        private void CreateConflictRow(MergeConflict conflict)
        {
            var row = UIFactory.CreateVerticalGroup(_conflictListContent, $"Conflict_{conflict.Key}", false, false, true, true, 3);
            UIFactory.SetLayoutElement(row, minHeight: 80, flexibleWidth: 9999);

            // Key label
            var keyLabel = UIFactory.CreateLabel(row, "Key", $"Key: {conflict.Key}", TextAnchor.MiddleLeft);
            keyLabel.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(keyLabel.gameObject, minHeight: 20);

            // Values row
            var valuesRow = UIFactory.CreateHorizontalGroup(row, "Values", false, false, true, true, 10);
            UIFactory.SetLayoutElement(valuesRow, minHeight: 50);

            // Local value
            var localGroup = UIFactory.CreateVerticalGroup(valuesRow, "Local", false, false, true, true, 2);
            UIFactory.SetLayoutElement(localGroup, flexibleWidth: 9999);
            var localLabel = UIFactory.CreateLabel(localGroup, "LocalLabel", "Local:", TextAnchor.MiddleLeft);
            localLabel.fontSize = 12;
            var localValue = UIFactory.CreateLabel(localGroup, "LocalValue", conflict.LocalValue ?? "(none)", TextAnchor.MiddleLeft);
            localValue.fontSize = 12;
            localValue.color = UIStyles.TextAccent;

            // Remote value
            var remoteGroup = UIFactory.CreateVerticalGroup(valuesRow, "Remote", false, false, true, true, 2);
            UIFactory.SetLayoutElement(remoteGroup, flexibleWidth: 9999);
            var remoteLabel = UIFactory.CreateLabel(remoteGroup, "RemoteLabel", "Remote:", TextAnchor.MiddleLeft);
            remoteLabel.fontSize = 12;
            var remoteValue = UIFactory.CreateLabel(remoteGroup, "RemoteValue", conflict.RemoteValue ?? "(none)", TextAnchor.MiddleLeft);
            remoteValue.fontSize = 12;
            remoteValue.color = UIStyles.StatusSuccess;

            // Choice toggles
            var choiceRow = UIFactory.CreateHorizontalGroup(row, "Choices", false, false, true, true, 20);
            UIFactory.SetLayoutElement(choiceRow, minHeight: 25);

            var key = conflict.Key;
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
            foreach (var conflict in _pendingMerge.Conflicts)
            {
                _resolutions[conflict.Key] = ConflictResolution.KeepLocal;
            }
            RefreshConflictList();
        }

        private void UseAllRemote()
        {
            foreach (var conflict in _pendingMerge.Conflicts)
            {
                _resolutions[conflict.Key] = ConflictResolution.TakeRemote;
            }
            RefreshConflictList();
        }

        private void ApplyMerge()
        {
            if (_pendingMerge == null) return;

            // Apply resolutions to get final merged result
            TranslationMerger.ApplyResolutions(_pendingMerge, _resolutions);

            // Use TranslatorUIManager.ApplyMerge to handle hash updates
            // Pass remoteTranslations so it's saved as ancestor for correct LocalChangesCount
            TranslatorUIManager.ApplyMerge(_pendingMerge, _serverHash, _remoteTranslations);

            SetActive(false);
        }

        private async void ReplaceWithRemote()
        {
            // Clear pending merge state
            _pendingMerge = null;
            _resolutions.Clear();

            // Download and apply remote directly (discards local changes)
            await TranslatorUIManager.DownloadUpdate();

            SetActive(false);
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
