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
    /// The Asset Packs card of the Tools tab: a `.ugtpack` from the packs folder or a pasted path,
    /// what it would add, and — on this window's Apply, like everything else here — the act.
    ///
    /// 🔴 **Adding a pack is choosing, not acting.** The rows say what would arrive and what would
    /// replace a file already here (declined unless ticked); the window's single Apply (N) counts
    /// them and writes them, and closing without it forgets them — the rule of every other tab
    /// ("Takes effect on Apply"). Decisions and words are the socle's (AssetPlanner), the same UGT
    /// Manager's Assets tab uses. Design: analyse/manager-onglet-assets.md (root).
    /// </summary>
    public partial class TranslationParametersPanel
    {
        private LabelHandle _packsFolderLabel;
        private ScrollList _packsFolderList;
        private FieldHandle _packPathInput;
        private LabelHandle _packNotes;
        private ScrollList _packOffersList;
        private Host _packSummaryRow;
        private LabelHandle _packSummary;
        private LabelHandle _packsStatus;

        /// <summary>The packs added, in the order the plan was made from — its offers point back here.</summary>
        private readonly List<string> _packSources = new List<string>();

        private AssetPlan _packPlan;

        /// <summary>Offers whose replacement was ticked. Everything else already here stays.</summary>
        private readonly HashSet<string> _packReplace = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>A pack is being read in the background — a second one waits for it (see AddPack).</summary>
        private bool _packReading;

        private void FetchPackPieces()
        {
            _packsFolderLabel = _screen.Label("PacksFolder");
            _packsFolderList = _screen.List("PacksFolderScroll");
            _packPathInput = _screen.Field("PackPathInput");
            _packNotes = _screen.Label("PackNotes");
            _packOffersList = _screen.List("PackOffersScroll");
            _packSummaryRow = _screen.Host("PackSummaryRow");
            _packSummary = _screen.Label("PackSummary");
            _packsStatus = _screen.Label("PacksStatus");
        }

        /// <summary>What Apply writes: every new offer, and the replacements ticked.</summary>
        private List<AssetOffer> AcceptedPacks() =>
            _packPlan == null
                ? new List<AssetOffer>()
                : _packPlan.Offers.Where(o => o.Change == AssetChange.Add
                                              || (o.Change == AssetChange.Replace && _packReplace.Contains(o.Key))).ToList();

        /// <summary>Drops the packs added and not applied — the window closed, or Undo.</summary>
        private void ForgetPacks()
        {
            _packSources.Clear();
            _packPlan = null;
            _packReplace.Clear();
            RefreshPacks();
        }

        /// <summary>The packs folder, where it is and what it holds.</summary>
        private void RefreshPacksFolder()
        {
            if (_packsFolderList == null) return;

            var folder = AssetPackService.PacksFolder;
            _screen.Say("packsFolder", folder == null ? "" : "Packs folder: " + folder);

            _packsFolderList.Clear();
            foreach (var path in AssetPackService.PacksInFolder())
            {
                var captured = path;
                var row = _screen.Instantiate("PackFileRow", _packsFolderList.Rows,
                    act => act == "add" ? (Action)(() => AddPack(captured)) : null);
                row.Say("name", Path.GetFileName(path));
            }

            _packsFolderList.Filled();
        }

        private void OnAddPackPathClicked()
        {
            var path = (_packPathInput.Text ?? "").Trim().Trim('"');

            if (path.Length == 0) return;

            // ⚠ Said here, before reading: a path that is not a pack is a typing slip, not a pack
            // to refuse — and only this extension is ever opened (user, 2026-09-27).
            if (!AssetPacks.IsPack(path))
            {
                Report("Only .ugtpack files can be added here.", Tone.Warning);
                return;
            }

            if (!File.Exists(path))
            {
                Report("No file at this path.", Tone.Warning);
                return;
            }

            _packPathInput.Text = "";
            AddPack(path);
        }

        /// <summary>
        /// Adds a pack to what is held and plans everything held again, together — so a file offered
        /// twice counts once. The reading runs off the main thread: a pack of a few hundred pictures
        /// is read and hashed whole, and the game must not stall for it.
        /// </summary>
        private void AddPack(string path)
        {
            if (_packReading)
            {
                Report("Still reading the previous pack.", Tone.Secondary);
                return;
            }

            _packSources.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _packSources.Add(path);

            var sources = _packSources.ToList();
            var side = AssetPackService.Side();   // on this thread: it copies what the inspector edits here
            _packReading = true;
            Report("Reading " + Path.GetFileName(path) + "...", Tone.Secondary);

            Task.Run(() =>
            {
                AssetPlan plan;
                try { plan = AssetPackService.Plan(sources, side); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
                {
                    plan = AssetPlan.Empty.WithRefused(new RefusedAsset(Path.GetFileName(path), "Could not be read: " + e.Message));
                }

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _packReading = false;
                    _packPlan = plan;
                    Report("", Tone.Secondary);
                    RefreshPacks();
                });
            });
        }

        /// <summary>The held packs' rows, notes and summary — and their marks for Apply (N).</summary>
        private void RefreshPacks()
        {
            if (_packOffersList == null) return;

            Pending.ClearGroup("packs");
            _packOffersList.Clear();

            var plan = _packPlan;
            var shown = plan != null && (plan.Offers.Count > 0 || plan.Refused.Count > 0);

            _packOffersList.Visible = shown;
            _packSummaryRow.Visible = shown;

            var notes = plan == null
                ? new List<string>()
                : plan.MadeFor.Select(AssetPlanner.MadeForText).Concat(plan.OtherLanguages.Select(AssetPlanner.OtherLanguageText)).ToList();
            _screen.Say("packNotes", string.Join("\n", notes));
            _packNotes.Visible = notes.Count > 0;

            if (!shown)
            {
                UpdateApplyButtonText();
                return;
            }

            _screen.Say("packSummary", AssetPlanner.Summary(plan));

            _fillingRows = true;
            try
            {
                foreach (var offer in plan.Offers.OrderBy(o => o.Change == AssetChange.Same).ThenBy(o => o.Kind).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var captured = offer;
                    BuiltScreen row = null;
                    row = _screen.Instantiate("PackOfferRow", _packOffersList.Rows, act => act == "replaceChanged" ? (Action)(() =>
                    {
                        if (_fillingRows) return;
                        if (row.Toggle("ReplaceToggle").IsOn) _packReplace.Add(captured.Key);
                        else _packReplace.Remove(captured.Key);
                        UpdateApplyButtonText();
                    }) : null);

                    row.Say("name", offer.Name);
                    row.Say("from", (offer.Kind == AssetKind.Font ? "Font" : "Image") + " · from " + offer.From);

                    var replace = row.Toggle("ReplaceToggle");
                    replace.Visible = offer.Change == AssetChange.Replace;
                    replace.IsOn = _packReplace.Contains(offer.Key);

                    var state = row.Label("State");
                    state.Visible = offer.Change != AssetChange.Replace;
                    row.Say("state", offer.Change == AssetChange.Add ? "New" : "Already in this game");
                    state.Tone = offer.Change == AssetChange.Add ? Tone.Success : Tone.Muted;

                    // The mark Apply (N) counts: green for what arrives, amber for a ticked replacement.
                    Pending.TrackState(row.Root, () =>
                        captured.Change == AssetChange.Add ? PendingState.Added
                        : captured.Change == AssetChange.Replace && _packReplace.Contains(captured.Key) ? PendingState.Modified
                        : PendingState.None, "packs");
                }

                foreach (var left in plan.Refused)
                {
                    var row = _screen.Instantiate("PackRefusedRow", _packOffersList.Rows, _ => null);
                    row.Say("text", left.Name.Length > 0 ? left.Name + ": " + left.Reason : left.Reason);
                }
            }
            finally
            {
                _fillingRows = false;
            }

            _packOffersList.Filled();
            UpdateApplyButtonText();
        }

        /// <summary>
        /// The act, inside the window's Apply — before the translation is saved, so the image settings
        /// go into the file with everything else. Main thread: pictures become textures.
        /// </summary>
        private void ApplyPacks()
        {
            var accepted = AcceptedPacks();
            if (accepted.Count == 0) return;

            var result = AssetPackService.Apply(_packSources.ToList(), accepted);

            if (!result.Done)
            {
                Report("Nothing was added: " + result.Failure, Tone.Error);
                return;
            }

            if (result.Images > 0 && TranslatorCore.Config.enable_image_replacement) ImageReplacer.ApplyToScene();

            ForgetPacks();
            RefreshPacksFolder();
            Report("Added " + Composition.Amount(result.Fonts + result.Images, "font or image", "fonts and images") + ".", Tone.Success);
        }

        private void Report(string text, Tone tone)
        {
            if (_packsStatus == null) return;
            _screen.Say("packsStatus", text);
            _packsStatus.Tone = tone;
        }
    }
}
