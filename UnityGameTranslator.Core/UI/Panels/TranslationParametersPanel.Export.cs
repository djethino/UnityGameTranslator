using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// The Export card of the Tools tab: this game's fonts and images in one `.ugtpack`, written into
    /// packs/exported/ under a dated name (user, 2026-09-28).
    ///
    /// 🔴 **The same card as UGT Manager's Export** — same words, same order, same pack: what goes, the
    /// manifest and the zip are the socle's (AssetPackWriter). ⚠ Unlike the window's settings, Export
    /// acts at once: it writes a new file and changes nothing in the game, so there is nothing for
    /// Apply to confirm — like Open folder beside it. What it reads is what the game applies: changes
    /// waiting for Apply are not in it.
    /// Design: analyse/manager-onglet-assets.md § « Export d'un .ugtpack depuis le MOD ».
    /// </summary>
    public partial class TranslationParametersPanel
    {
        private Host _exportSystemRow;
        private ToggleHandle _exportSystemToggle;
        private LabelHandle _exportNotes;
        private ButtonHandle _exportBtn;
        private Host _exportDoneRow;
        private LabelHandle _exportStatus;

        /// <summary>The System fonts the translation uses — read off the main thread, null until then.</summary>
        private List<SystemFontChoice> _exportSystemFonts;

        private ExportPlan _exportPlanWithout;
        private ExportPlan _exportPlanWith;

        /// <summary>
        /// Whether the System fonts go — ⚠ never remembered beyond this window's life: sharing fonts under
        /// somebody else's licence is decided each time (the Manager's rule).
        /// </summary>
        private bool _exportIncludeSystem;

        private bool _exportBusy;
        private string _lastExport;

        private void FetchExportPieces()
        {
            _exportSystemRow = _screen.Host("ExportSystemFontsRow");
            _exportSystemToggle = _screen.Toggle("ExportSystemFontsToggle");
            _exportNotes = _screen.Label("ExportNotes");
            _exportBtn = _screen.Button("ExportBtn");
            _exportDoneRow = _screen.Host("ExportDoneRow");
            _exportStatus = _screen.Label("ExportStatus");

            // The socle's words, read where the decision to share is taken — true of every file in a pack.
            _screen.Say("exportShareNotice", AssetPacks.ShareNotice);
        }

        /// <summary>
        /// Reads what an export would carry — the fonts folder, the images, the System fonts on this
        /// computer — off the main thread, then says it. Called when the Tools tab is shown and after Apply.
        /// </summary>
        private void RefreshExport()
        {
            if (_exportBtn == null || _exportBusy) return;

            var source = AssetPackService.ExportSide();   // on this thread: the inspector edits it here
            _exportBtn.Enabled = false;
            _screen.Say("exportSummary", "Reading...");

            Task.Run(() =>
            {
                List<SystemFontChoice> system;
                ExportPlan without, with;
                try
                {
                    system = AssetPackService.SystemFonts(source);
                    without = AssetPackService.PlanExport(source, new SystemFontChoice[0]);
                    with = AssetPackService.PlanExport(source, system);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    TranslatorUIManager.RunOnMainThread(() => _screen.Say("exportSummary", "Could not read: " + e.Message));
                    return;
                }

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _exportSystemFonts = system;
                    _exportPlanWithout = without;
                    _exportPlanWith = with;
                    ShowExport();
                });
            });
        }

        /// <summary>The card from what was read: the System fonts line, the reasons, the count, the button.</summary>
        private void ShowExport()
        {
            var includable = _exportSystemFonts.Where(c => c.Includable).ToList();

            // The option exists only when the translation uses a System font that can go.
            _exportSystemRow.Visible = includable.Count > 0;
            _fillingRows = true;
            try { _exportSystemToggle.IsOn = _exportIncludeSystem; }
            finally { _fillingRows = false; }
            _screen.Say("exportSystemFonts", includable.Count > 0
                ? "(" + includable.Count + "): " + string.Join(", ", includable.Select(c => c.Reference))
                : "");

            // One line per reason, the reason said once (the Manager's wording).
            var reasons = _exportSystemFonts.Where(c => !c.Includable).GroupBy(c => c.Why)
                .Select(g => "System fonts not included: " + string.Join(", ", g.Select(c => c.Reference)) + " (" + g.Key + ").")
                .ToList();
            _screen.Say("exportNotes", string.Join("\n", reasons));
            _exportNotes.Visible = reasons.Count > 0;

            var plan = _exportIncludeSystem && includable.Count > 0 ? _exportPlanWith : _exportPlanWithout;
            _screen.Say("exportSummary", plan.IsEmpty
                ? "Nothing to export"
                : Composition.Amount(plan.Fonts, "font", "fonts") + ", " + Composition.Amount(plan.ImageCount, "image", "images"));

            _exportBtn.Enabled = !plan.IsEmpty;
        }

        private void OnExportSystemFontsChanged()
        {
            if (_fillingRows || _exportSystemFonts == null) return;
            _exportIncludeSystem = _exportSystemToggle.IsOn;
            ShowExport();
        }

        /// <summary>Writes the pack off the main thread, then says where it went.</summary>
        private void OnExportClicked()
        {
            if (_exportBusy || _exportSystemFonts == null) return;

            var source = AssetPackService.ExportSide();
            var system = _exportIncludeSystem ? _exportSystemFonts : new List<SystemFontChoice>();

            _exportBusy = true;
            _exportBtn.Enabled = false;
            _screen.Say("exportSummary", "Exporting...");

            Task.Run(() =>
            {
                string written = null, failure = null;
                try { written = AssetPackService.Export(source, system); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException
                                          || e is InvalidDataException || e is InvalidOperationException)
                {
                    failure = e.Message;
                }

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _exportBusy = false;
                    _exportDoneRow.Visible = true;

                    if (written != null)
                    {
                        _lastExport = written;
                        _screen.Say("exportStatus", "Exported: " + Path.GetFileName(written));
                        _exportStatus.Tone = Tone.Success;
                    }
                    else
                    {
                        _screen.Say("exportStatus", "Nothing was exported: " + failure);
                        _exportStatus.Tone = Tone.Error;
                    }

                    _screen.Button("ExportShowBtn").Visible = written != null;
                    ShowExport();
                });
            });
        }

        /// <summary>The folder the pack was saved in.</summary>
        private void OnShowExportClicked()
        {
            var folder = _lastExport != null ? Path.GetDirectoryName(_lastExport) : AssetPackService.ExportFolder;
            if (!TranslatorCore.OpenFolderSafe(folder))
            {
                _screen.Say("exportStatus", "Could not open the folder: " + folder);
                _exportStatus.Tone = Tone.Warning;
            }
        }
    }
}
