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
    ///
    /// ⚠ Described in data since 2026-09-15 (<c>common/spec/screens/settings-choice.json</c>):
    /// the frame is the document's; the rows are built here at show time, one per section, because
    /// their number and their words come from the two files being compared.
    /// </summary>
    public class SettingsChoicePanel : TranslatorPanelBase
    {
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("settings-choice");

        public override string Name => Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

        protected override int MinPanelHeight => Doc.MinHeight;
        protected override bool PersistWindowPreferences => Doc.Persist;
        protected override bool UseBackdrop => Doc.Backdrop;

        private BuiltScreen _screen;

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
            _screen.Say("intro",
                $"These settings differ between your version and {sourceLabel}.\n"
                + $"Tick what you want to replace with the settings from {sourceLabel}. "
                + "Anything left unticked keeps your own setting.");

            BuildSectionRows(decisions);

            // The button is only honest when there is somewhere to go
            _screen.Button("CompareBtn").Visible = onCompare != null;
            _screen.Label("BackupNote").Visible = fileWasBackedUp;

            SetActive(true);
        }

        protected override void ConstructPanelContent()
        {
            Layout(out var body, out var footer, Doc.CardWidth);
            _screen = ScreenBuilder.Build(Doc, body, footer, ActOf);
        }

        private Action ActOf(string act)
        {
            switch (act)
            {
                case "apply": return OnApplyClicked;
                case "compare": return OnCompareClicked;
                case "cancel": return OnCancelClicked;
                default: return null;
            }
        }

        private void BuildSectionRows(List<SettingsSectionPlan> decisions)
        {
            _toggles.Clear();
            var sections = _screen.Host("Sections");
            sections.Clear();

            if (decisions == null) return;

            foreach (var plan in decisions)
            {
                // Ticked by default (the document says so): the downloaded version is the one
                // the player just asked for, and their own settings are recoverable.
                var row = _screen.Instantiate("SectionRow", sections, _ => null);
                _toggles[plan.Section] = row.Toggle("Toggle");
                row.Say("name", $"{plan.DisplayName}  ({plan.OursCount} here / {plan.TheirsCount} downloaded)");
                row.Say("description", plan.Description);
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
