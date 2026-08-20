using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Asks which settings sections to replace with the incoming ones.
    ///
    /// Modelled on a browser's "clear browsing data" dialog: one tick per
    /// section, everything visible at once, one button to go. It only ever
    /// lists sections BOTH sides changed since the last common state — a
    /// section only the other side touched is taken silently, one only we
    /// touched is kept silently (see SettingsSyncPlan). So the usual case is
    /// no dialog at all, and the rare one is a short list.
    ///
    /// The panel decides nothing: it collects ticks and hands them back.
    /// </summary>
    public class SettingsChoicePanel : TranslatorPanelBase
    {
        public override string Name => "Settings";
        public override int MinWidth => 480;
        public override int MinHeight => 220;
        public override int PanelWidth => 560;
        public override int PanelHeight => 420;

        protected override int MinPanelHeight => 220;
        protected override bool PersistWindowPreferences => false;

        private Text _titleLabel;
        private Text _introLabel;
        private Text _backupLabel;
        private GameObject _sectionsHost;
        private ButtonRef _applyBtn;
        private ButtonRef _compareBtn;
        private ButtonRef _cancelBtn;

        // Section name -> its toggle. Ticked means "replace mine with theirs".
        private readonly Dictionary<string, Toggle> _toggles = new Dictionary<string, Toggle>();

        private Action<List<string>> _onApply;
        private Action _onCompare;
        private Action _onCancel;

        public SettingsChoicePanel(UIBase owner) : base(owner)
        {
        }

        /// <summary>
        /// Show the sections that need arbitration.
        /// </summary>
        /// <param name="decisions">Sections where both sides moved (SettingsSyncPlan.Decisions)</param>
        /// <param name="sourceLabel">Where the incoming settings come from, in the player's words</param>
        /// <param name="onApply">Receives the sections to replace — possibly empty, which means "keep everything of mine"</param>
        /// <param name="onCompare">Optional: open a side-by-side comparison. The button is hidden when null.</param>
        /// <param name="onCancel">Closing without applying anything</param>
        /// <param name="fileWasBackedUp">
        /// Whether a backup of the file was actually taken before this. Only the download paths
        /// take one; saying so on a path that did not would be a promise the mod cannot keep.
        /// </param>
        public void Show(
            List<SettingsSectionPlan> decisions,
            string sourceLabel,
            Action<List<string>> onApply,
            Action onCompare = null,
            Action onCancel = null,
            bool fileWasBackedUp = true)
        {
            _onApply = onApply;
            _onCompare = onCompare;
            _onCancel = onCancel;

            // Says only what is true on EVERY path that opens this panel. It used to claim both
            // sides had changed, which is right for a conflict but wrong for a download the
            // player asked for (where any difference is submitted) and wrong again when they
            // deliberately come to take the online settings back.
            SetDynamicText(_introLabel,
                $"These settings differ between your version and {sourceLabel}.\n"
                + $"Tick what you want to replace with the settings from {sourceLabel}. "
                + "Anything left unticked keeps your own setting.");

            BuildSectionRows(decisions);

            // The button is only honest when there is somewhere to go
            _compareBtn?.Component?.gameObject?.SetActive(onCompare != null);
            _backupLabel?.gameObject?.SetActive(fileWasBackedUp);

            SetActive(true);
        }

        protected override void ConstructPanelContent()
        {
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            var card = CreateAdaptiveCard(scrollContent, "SettingsChoiceCard", PanelWidth - 60);

            _titleLabel = UIFactory.CreateLabel(card, "Title", "Settings differ", TextAnchor.MiddleLeft);
            _titleLabel.fontSize = UIStyles.FontSizeTitle;
            _titleLabel.fontStyle = FontStyle.Bold;
            _titleLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_titleLabel.gameObject, minHeight: UIStyles.TitleHeight);
            RegisterUIText(_titleLabel);

            _introLabel = UIFactory.CreateLabel(card, "Intro", "", TextAnchor.UpperLeft);
            _introLabel.fontSize = UIStyles.FontSizeSmall;
            _introLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_introLabel.gameObject, minHeight: UIStyles.MultiLineSmall, flexibleWidth: 9999);
            UIFactory.ConfigureAutoHeight(_introLabel, UIStyles.SmallSpacing);
            RegisterExcluded(_introLabel);

            UIStyles.CreateSpacer(card, 8);

            // One row per section, rebuilt on every Show
            _sectionsHost = UIFactory.CreateVerticalGroup(card, "Sections", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_sectionsHost, flexibleWidth: 9999);

            UIStyles.CreateSpacer(card, 8);

            _backupLabel = UIFactory.CreateLabel(card, "BackupNote",
                "Your current file is backed up before anything is replaced.", TextAnchor.MiddleLeft);
            _backupLabel.fontSize = UIStyles.FontSizeHint;
            _backupLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_backupLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(_backupLabel);

            _cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Keep mine");
            _cancelBtn.OnClick += OnCancelClicked;
            RegisterUIText(_cancelBtn.ButtonText);

            _compareBtn = CreateSecondaryButton(buttonRow, "CompareBtn", "Compare");
            _compareBtn.OnClick += OnCompareClicked;
            // 🔴 **The same word as the main panel's Compare, opening the same page the OTHER way
            // round.** This one is `toLocal: true` — what is validated there comes back into the
            // file on this machine and publishes nothing. Two buttons that read identically and
            // write to opposite sides: the marks are the only thing separating them, which is
            // precisely their job (see name-things-in-ui: the scope tells where it writes, the
            // label carries the verb).
            ScopeMarks.Adorn(_compareBtn,
                EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false));
            RegisterUIText(_compareBtn.ButtonText);

            _applyBtn = CreatePrimaryButton(buttonRow, "ApplyBtn", "Apply");
            _applyBtn.OnClick += OnApplyClicked;
            RegisterUIText(_applyBtn.ButtonText);
        }

        private void BuildSectionRows(List<SettingsSectionPlan> decisions)
        {
            _toggles.Clear();
            if (_sectionsHost == null) return;

            for (int i = _sectionsHost.transform.childCount - 1; i >= 0; i--)
            {
                GameObject.Destroy(_sectionsHost.transform.GetChild(i).gameObject);
            }

            if (decisions == null) return;

            foreach (var plan in decisions)
            {
                var row = UIFactory.CreateHorizontalGroup(_sectionsHost, $"Row_{plan.Section}",
                    false, false, true, true, 8);
                UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightLarge, flexibleWidth: 9999);
                UIStyles.SetBackground(row, UIStyles.CardElevated);
                var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
                if (rowLayout != null)
                {
                    rowLayout.padding = Compat.MakeRectOffset(10, 10, 6, 6);
                    rowLayout.childAlignment = TextAnchor.MiddleLeft;
                }

                var toggleObj = UIFactory.CreateToggle(row, $"Toggle_{plan.Section}", out var toggle, out var _);
                // Ticked by default: the downloaded version is the one the
                // player just asked for, and their own settings are recoverable
                toggle.isOn = true;
                UIFactory.SetLayoutElement(toggleObj, minWidth: UIStyles.ToggleControlWidth);
                _toggles[plan.Section] = toggle;

                var infoCol = UIFactory.CreateVerticalGroup(row, "Info", false, false, true, true, 2);
                UIFactory.SetLayoutElement(infoCol, flexibleWidth: 9999);

                var nameLabel = UIFactory.CreateLabel(infoCol, "Name",
                    $"{plan.DisplayName}  ({plan.OursCount} here / {plan.TheirsCount} downloaded)",
                    TextAnchor.MiddleLeft);
                nameLabel.fontStyle = FontStyle.Bold;
                nameLabel.fontSize = UIStyles.FontSizeNormal;
                nameLabel.color = UIStyles.TextPrimary;
                UIFactory.SetLayoutElement(nameLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
                RegisterExcluded(nameLabel);

                var descLabel = UIFactory.CreateLabel(infoCol, "Desc", plan.Description, TextAnchor.MiddleLeft);
                descLabel.fontSize = UIStyles.FontSizeHint;
                descLabel.color = UIStyles.TextMuted;
                UIFactory.SetLayoutElement(descLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
                RegisterExcluded(descLabel);
            }
        }

        private List<string> TickedSections()
        {
            var chosen = new List<string>();
            foreach (var kvp in _toggles)
            {
                if (kvp.Value != null && kvp.Value.isOn)
                {
                    chosen.Add(kvp.Key);
                }
            }

            return chosen;
        }

        private void OnApplyClicked()
        {
            var chosen = TickedSections();
            SetActive(false);
            _onApply?.Invoke(chosen);
        }

        private void OnCompareClicked()
        {
            // Stays open behind the comparison: the player still has to decide
            _onCompare?.Invoke();
        }

        private void OnCancelClicked()
        {
            SetActive(false);
            _onCancel?.Invoke();
        }

        protected override void OnClosePanelClicked()
        {
            // Closing the window is not "replace nothing and forget it": the
            // caller may have content waiting to be written, so it is told
            SetActive(false);
            _onCancel?.Invoke();
        }
    }
}
