using System;
using System.Collections.Generic;
using UniverseLib.UI;
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

        private LabelHandle _introLabel;
        private LabelHandle _backupLabel;
        private Host _sectionsHost;
        private ButtonHandle _applyBtn;
        private ButtonHandle _compareBtn;
        private ButtonHandle _cancelBtn;

        // Section name -> its toggle. Ticked means "replace mine with theirs".
        private readonly Dictionary<string, ToggleHandle> _toggles = new Dictionary<string, ToggleHandle>();

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
            _introLabel.Say(
                $"These settings differ between your version and {sourceLabel}.\n"
                + $"Tick what you want to replace with the settings from {sourceLabel}. "
                + "Anything left unticked keeps your own setting.");

            BuildSectionRows(decisions);

            // The button is only honest when there is somewhere to go
            _compareBtn.Visible = onCompare != null;
            _backupLabel.Visible = fileWasBackedUp;

            SetActive(true);
        }

        protected override void ConstructPanelContent()
        {
            Layout(out var body, out var footer, PanelWidth - 40);

            var card = Stacks.Card(body, "SettingsChoiceCard", PanelWidth - 60);

            Labels.Create(card, "Title", "Settings differ", TextRole.Title, centred: false);

            // Written by Show, so Dynamic; a paragraph that wraps, so it grows to what it draws.
            _introLabel = Labels.Create(card, "Intro", "", TextRole.Small, tone: Tone.Secondary,
                                        policy: TextPolicy.Dynamic, fill: Fill.Stretch,
                                        minHeight: UIStyles.MultiLineSmall, autoHeight: true);

            Stacks.Spacer(card, 8);

            // One row per section, rebuilt on every Show
            _sectionsHost = Stacks.Vertical(card, "Sections", spacing: UIStyles.SmallSpacing);

            Stacks.Spacer(card, 8);

            _backupLabel = Labels.Create(card, "BackupNote",
                "Your current file is backed up before anything is replaced.", TextRole.Hint);
            _backupLabel.Italic = false;

            _cancelBtn = Buttons.Secondary(footer, "CancelBtn", "Keep mine");
            _cancelBtn.Clicked += OnCancelClicked;

            // 🔴 **The same word as the main panel's Compare, opening the same page the OTHER way
            // round.** This one is `toLocal: true` — what is validated there comes back into the
            // file on this machine and publishes nothing. Two buttons that read identically and
            // write to opposite sides: the marks are the only thing separating them, which is
            // precisely their job (see name-things-in-ui: the scope tells where it writes, the
            // label carries the verb).
            _compareBtn = Buttons.Secondary(footer, "CompareBtn", "Compare",
                scope: EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false));
            _compareBtn.Clicked += OnCompareClicked;

            _applyBtn = Buttons.Primary(footer, "ApplyBtn", "Apply");
            _applyBtn.Clicked += OnApplyClicked;
        }

        private void BuildSectionRows(List<SettingsSectionPlan> decisions)
        {
            _toggles.Clear();
            if (_sectionsHost == null) return;

            _sectionsHost.Clear();

            if (decisions == null) return;

            foreach (var plan in decisions)
            {
                var row = Stacks.Horizontal(_sectionsHost, $"Row_{plan.Section}", spacing: 8,
                                            pad: Pad.Of(10, 6), placement: Placement.MiddleLeft,
                                            surface: Surface.Elevated, minHeight: UIStyles.RowHeightLarge);

                // Ticked by default: the downloaded version is the one the
                // player just asked for, and their own settings are recoverable
                _toggles[plan.Section] = CheckBoxes.Bare(row, $"Toggle_{plan.Section}", initial: true);

                var infoCol = Stacks.Vertical(row, "Info", spacing: 2);

                var nameLabel = Labels.Create(infoCol, "Name",
                    $"{plan.DisplayName}  ({plan.OursCount} here / {plan.TheirsCount} downloaded)",
                    TextRole.Body, policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);
                nameLabel.Bold = true;

                var descLabel = Labels.Create(infoCol, "Desc", plan.Description, TextRole.Hint,
                                              policy: TextPolicy.Excluded);
                descLabel.Italic = false;
            }
        }

        private List<string> TickedSections()
        {
            var chosen = new List<string>();
            foreach (var kvp in _toggles)
            {
                if (kvp.Value != null && kvp.Value.IsOn)
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
