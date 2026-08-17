using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// This game's translation, as it stood at earlier moments.
    ///
    /// 🔴 **A panel and not a section on the main screen.** The main screen already carries five
    /// sections; a list of a dozen rows with three verbs each would swamp the one thing it exists
    /// to show — what your translation IS right now. The main screen gains a single line naming
    /// how many copies exist and a way in, which is how Merge, Upload and Login already work here.
    ///
    /// ⚠ **Two lists, never one.** They do not live equally long: an automatic copy ages out on
    /// its own, a saved one stays until somebody removes it. Two rows that look alike and do not
    /// survive alike is how people lose things they thought were kept.
    ///
    /// ⚠ Everything the rows say comes from <see cref="Backups"/>, so a copy taken in the game and
    /// read in the Manager reads identically. What differs is only the drawing.
    /// </summary>
    public class BackupsPanel : TranslatorPanelBase
    {
        public override string Name => "Backups";
        public override int MinWidth => 560;
        public override int MinHeight => 340;
        public override int PanelWidth => 640;
        public override int PanelHeight => 620;

        protected override int MinPanelHeight => 340;

        private GameObject _listHost;
        private Text _nowLabel;
        private ButtonRef _saveBtn;
        private Components.HelpZone _helpZone;

        /// <summary>Which row is being renamed, or null. One at a time, in place.</summary>
        private string _renaming;

        public BackupsPanel(UIBase owner) : base(owner) { }

        public void ShowPanel()
        {
            Refresh();
            SetActive(true);
        }

        protected override void ConstructPanelContent()
        {
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            // 🔴 **What must stay put stays put.** The title, the state you are in and the button
            // that keeps a copy are what somebody reads BEFORE choosing a row — scrolled away by
            // the twelfth entry, they would have to scroll back up to remember where they stand.
            // CreateFixedHeader exists for exactly this and is what the other panels use.
            var header = CreateFixedHeader("BackupsHeader");

            var head = CreateAdaptiveCard(header, "BackupsHead", PanelWidth - 40);

            var title = CreateTitle(head, "Title", "Backups");
            RegisterUIText(title);

            // ⚠ Said once, at the top. Somebody looking at a list of their own work deserves to
            // know it goes nowhere before they wonder whether it does.
            var privacy = UIFactory.CreateLabel(head, "Privacy", Backups.PrivacyNote,
                                                TextAnchor.MiddleLeft);
            privacy.color = UIStyles.TextMuted;
            privacy.fontSize = UIStyles.FontSizeHint;
            UIFactory.SetLayoutElement(privacy.gameObject, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 9999);
            RegisterUIText(privacy);

            UIStyles.CreateSpacer(head, 6);

            // 🔴 The current state, first. Without it no row can be read: a line count is neither
            // more nor less until you know where you stand today.
            _nowLabel = UIFactory.CreateLabel(head, "Now", "", TextAnchor.MiddleLeft);
            _nowLabel.fontStyle = FontStyle.Bold;
            _nowLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_nowLabel.gameObject, minHeight: UIStyles.RowHeightNormal,
                                       flexibleWidth: 9999);
            RegisterExcluded(_nowLabel);

            // The rows themselves are the only thing that scrolls, in the panel's own scroll area.
            var card = CreateAdaptiveCard(scrollContent, "BackupsCard", PanelWidth - 40);

            _listHost = UIFactory.CreateVerticalGroup(card, "List", false, false, true, true, 6);
            UIFactory.SetLayoutElement(_listHost, flexibleWidth: 9999);

            var closeBtn = CreateSecondaryButton(buttonRow, "CloseBtn", "Close");
            closeBtn.OnClick += () => SetActive(false);
            RegisterUIText(closeBtn.ButtonText);

            Refresh();
        }

        // ── Drawing ───────────────────────────────────────────────────────

        private void Refresh()
        {
            if (_listHost == null) return;

            UIHelpers.DestroyChildren(_listHost);

            var entries = TranslationBackups.List();
            var saved = new List<BackupEntry>();
            var automatic = new List<BackupEntry>();

            foreach (var entry in entries)
            {
                if (entry.IsSaved) saved.Add(entry);
                else automatic.Add(entry);
            }

            RefreshHeader(saved.Count);

            // 🔴 **Two lists, and they must LOOK like two.** Both groups used to be rows in one
            // scrolling column with a bold line between them: the headings drifted away with the
            // rows, nothing said which heading a row belonged to, and by the fifth entry the
            // screen was one undifferentiated list. Each group now owns a titled block with its
            // own scroll area, so its heading is always above its own rows and never above
            // somebody else's.
            Group(Backups.SavedHeading, $"{saved.Count} of {Backups.SavedKept}", saved,
                  "Nothing kept yet. Keep one before you try something, and you can walk back out "
                  + "of whatever you try.",
                  height: 190, saved: true);

            UIStyles.CreateSpacer(_listHost, 12);

            Group(Backups.AutomaticHeading, Backups.AutomaticNote, automatic,
                  "Nothing yet. One is kept whenever something replaces your translation.",
                  height: 150);
        }

        private void RefreshHeader(int savedCount)
        {
            if (_nowLabel == null) return;

            var lines = TranslatorCore.TranslationCache?.Count ?? 0;
            _nowLabel.text = $"Now: {lines} lines";
        }

        /// <summary>Whether another copy may be taken, and why not when it may not.</summary>
        private void RefreshSaveButton()
        {
            if (_saveBtn?.Component == null) return;

            var why = Backups.WhyCannotSave(TranslationBackups.List());
            var can = why == null;

            _saveBtn.Component.interactable = can;

            // ⚠ Never a control that cannot be pressed without words saying why.
            _helpZone?.Describe(_saveBtn.Component.gameObject, can
                ? "Keeps the translation as it stands, with the fonts and images it uses."
                : why);
        }

        /// <summary>
        /// One titled block: its heading, and its own rows under it.
        ///
        /// ⚠ The heading uses the panel's shared section title — the same size, weight and colour
        /// every other section of this product wears. Hand-rolling a bold label made it the same
        /// weight as the rows beneath it, which is how a heading stops reading as one.
        /// </summary>
        private void Group(string heading, string note, List<BackupEntry> entries, string empty,
                           int height, bool saved = false)
        {
            var block = UIFactory.CreateVerticalGroup(_listHost, "Group", false, false, true, true,
                                                      4, new Vector4(8, 8, 6, 8));
            UIStyles.SetBackground(block, UIStyles.CardElevated);
            UIFactory.SetLayoutElement(block, flexibleWidth: 9999);

            var titleRow = UIStyles.CreateFormRow(block, "Heading", UIStyles.SectionTitleHeight, 8);

            var title = UIStyles.CreateSectionTitle(titleRow, "Text", heading);
            UIFactory.SetLayoutElement(title.gameObject, minWidth: 190,
                                       minHeight: UIStyles.SectionTitleHeight);
            RegisterUIText(title);

            // ⚠ Beside the heading, right-aligned: it qualifies the LIST — how full it is, or that
            // it ages out — and on a row it would read as being about that row.
            var hint = UIFactory.CreateLabel(titleRow, "Note", note, TextAnchor.MiddleRight);
            hint.color = UIStyles.TextMuted;
            hint.fontSize = UIStyles.FontSizeHint;
            UIFactory.SetLayoutElement(hint.gameObject, flexibleWidth: 9999);
            RegisterExcluded(hint);

            if (entries.Count == 0)
            {
                var none = UIFactory.CreateLabel(block, "Empty", empty, TextAnchor.MiddleLeft);
                none.color = UIStyles.TextMuted;
                none.fontSize = UIStyles.FontSizeHint;
                UIFactory.SetLayoutElement(none.gameObject, minHeight: UIStyles.RowHeightSmall,
                                           flexibleWidth: 9999);
                RegisterUIText(none);

                // ⚠ Offered even on an empty list: this is the one control that puts the FIRST
                // copy there, and hiding it until a copy exists would hide it from everybody who
                // has never made one.
                AddSaveButton(block, saved);
                return;
            }

            // 🔴 Its own scroll area, capped. Ten rows in the outer scroll would push the second
            // heading below the fold, and somebody scrolling to reach it loses the first — which
            // is the state the whole screen exists to compare against.
            var scroll = UIFactory.CreateScrollView(block, "Rows", out var rows, out _);
            UIFactory.SetLayoutElement(scroll, minHeight: Math.Min(height, entries.Count * 40 + 8),
                                       preferredHeight: height, flexibleWidth: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(rows, false, false, true, true, 4,
                                                          4, 4, 4, 4);
            UIStyles.SetBackground(scroll, UIStyles.TroughBackground);
            UIStyles.ConfigureScrollViewNoScrollbar(scroll);

            var host = _listHost;
            _listHost = rows;

            foreach (var entry in entries) Row(entry);

            _listHost = host;

            AddSaveButton(block, saved);
        }

        /// <summary>
        /// 🔴 **Under the list it fills, not above it.** Every verb in this product sits below the
        /// zone it acts on — the Apply of a settings block, the Apply of a hotkey. Above, it read
        /// as a heading for the list rather than an act upon it, and the eye had to travel back up
        /// to find it.
        /// </summary>
        private void AddSaveButton(GameObject block, bool saved)
        {
            if (!saved) return;

            var row = UIStyles.CreateFormRow(block, "SaveRow", UIStyles.RowHeightNormal, 8);

            // Pushes the button to the right edge, as every action row in this product does.
            var filler = UIFactory.CreateUIObject("Filler", row);
            UIFactory.SetLayoutElement(filler, flexibleWidth: 9999, minHeight: 1);

            _saveBtn = CreatePrimaryButton(row, "SaveBtn", "Save a copy");
            _saveBtn.OnClick += SaveCopy;
            RegisterUIText(_saveBtn.ButtonText);

            RefreshSaveButton();
        }

        /// <summary>
        /// One copy: what it is, why it exists, and what may be done with it.
        ///
        /// 🔴 **Two lines at most, and the verbs share the first one.** Stacked — facts, then
        /// reason, then a row of buttons — a copy took four lines and each list showed less than
        /// two entries. A list you cannot read two rows of is not a list, it is a keyhole.
        /// </summary>
        private void Row(BackupEntry entry)
        {
            var box = UIFactory.CreateUIObject("Entry", _listHost);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(box, false, false, true, true, 8,
                                                            6, 6, 4, 4, TextAnchor.MiddleLeft);
            UIStyles.SetBackground(box, UIStyles.ItemBackground);
            UIFactory.SetLayoutElement(box, flexibleWidth: 9999);

            var text = UIFactory.CreateVerticalGroup(box, "Text", false, false, true, true, 1);
            UIFactory.SetLayoutElement(text, flexibleWidth: 9999);

            // 🔴 **What identifies stays on the first line; what qualifies goes underneath,
            // small.** Everything on one line grew wider than the row and pushed against the
            // verbs beside it. Split in two, nothing is cut and the row reads the way every other
            // list in this product reads.
            var facts = $"{entry.At:dd MMM HH:mm}   {entry.Lines} lines";

            var details = new List<string>();

            // The name somebody gave it, or the act that caused it — first, because it is what
            // the eye looks for. An unnamed saved copy says nothing here: "Saved by you" would be
            // the heading of the very list it sits in, repeated on every row.
            if (!string.IsNullOrEmpty(entry.Label)) details.Add("\"" + entry.Label + "\"");
            else if (!entry.IsSaved) details.Add(Backups.Describe(entry.Reason, entry.By));

            if (entry.ByHand > 0) details.Add($"{entry.ByHand} by hand");
            if (entry.WithAssets) details.Add("with fonts and images");

            var factsLabel = UIFactory.CreateLabel(text, "Facts", facts, TextAnchor.MiddleLeft);
            factsLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(factsLabel.gameObject, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 9999);
            RegisterExcluded(factsLabel);

            // 🔴 The one restore that cannot be undone with another click, said where the counts
            // are and not in small print underneath.
            if (Backups.IsAnotherLineage(entry.Uuid, TranslatorCore.FileUuid))
            {
                var warn = UIFactory.CreateLabel(text, "Foreign", Backups.AnotherLineageNote,
                                                 TextAnchor.MiddleLeft);
                warn.color = UIStyles.StatusWarning;
                warn.fontSize = UIStyles.FontSizeHint;
                UIFactory.SetLayoutElement(warn.gameObject, minHeight: UIStyles.RowHeightSmall,
                                           flexibleWidth: 9999);
                RegisterUIText(warn);
            }

            // ⚠ Absent entirely when there is nothing to say, rather than an empty line: a copy
            // taken a second ago, unnamed and with no assets, is one line and no more.
            if (details.Count > 0)
            {
                var subtitleLabel = UIFactory.CreateLabel(text, "Why", string.Join(" · ", details),
                                                          TextAnchor.MiddleLeft);
                subtitleLabel.color = UIStyles.TextSecondary;
                subtitleLabel.fontSize = UIStyles.FontSizeHint;
                UIFactory.SetLayoutElement(subtitleLabel.gameObject,
                                           minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

                // ⚠ Excluded from the mod's own translation pass: it carries a name somebody
                // wrote and figures, neither of which is ours to rewrite.
                RegisterExcluded(subtitleLabel);
            }

            if (_renaming == entry.Id)
            {
                RenameRow(box, entry);
                return;
            }

            // ── the verbs, on the same line, at the right edge ──
            var buttons = UIFactory.CreateUIObject("Verbs", box);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(buttons, false, false, true, true, 4,
                                                            0, 0, 0, 0, TextAnchor.MiddleRight);
            UIFactory.SetLayoutElement(buttons, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 0);

            var restore = CreateSecondaryButton(buttons, "Restore", "Restore");
            restore.OnClick += () => ConfirmRestore(entry);
            RegisterUIText(restore.ButtonText);
            _helpZone?.Describe(restore.Component.gameObject,
                "Puts this one back in the game. What you have now is kept first, so this can be "
                + "walked back.");

            if (entry.IsSaved)
            {
                var rename = CreateSecondaryButton(buttons, "Rename", "Rename");
                rename.OnClick += () => { _renaming = entry.Id; Refresh(); };
                RegisterUIText(rename.ButtonText);
                _helpZone?.Describe(rename.Component.gameObject,
                    "Ten dated rows are not a choice. A name is what makes one of them findable.");

                var delete = CreateSecondaryButton(buttons, "Delete", "Delete");
                delete.OnClick += () => ConfirmDelete(entry);
                RegisterUIText(delete.ButtonText);
                _helpZone?.Describe(delete.Component.gameObject,
                    "Removes this copy and frees a slot. Nothing else is touched.");
            }
            else
            {
                // ⚠ The gesture that closes the loop between the two lists: recognise the one you
                // want before it ages out, and it stops ageing.
                var keep = CreateSecondaryButton(buttons, "Keep", "Keep");
                keep.OnClick += () =>
                {
                    if (!TranslationBackups.Keep(entry.Id))
                    {
                        TranslatorUIManager.StatusOverlay?.ShowToast(
                            Backups.WhyCannotSave(TranslationBackups.List())
                            ?? "This one could not be kept.", StatusOverlay.ToastTone.Off);
                    }

                    Refresh();
                };
                RegisterUIText(keep.ButtonText);
                _helpZone?.Describe(keep.Component.gameObject,
                    "Moves it in with the ones you saved, so it stops ageing out.");
            }
        }

        private void RenameRow(GameObject box, BackupEntry entry)
        {
            var row = UIStyles.CreateFormRow(box, "Rename", UIStyles.RowHeightNormal, 6);

            var field = UIFactory.CreateInputField(row, "Label", "What is this one?");
            UIFactory.SetLayoutElement(field.GameObject, minHeight: UIStyles.RowHeightNormal,
                                       flexibleWidth: 9999);
            field.Text = entry.Label ?? "";

            var ok = CreatePrimaryButton(row, "Ok", "Save");
            ok.OnClick += () =>
            {
                TranslationBackups.Rename(entry.Id, field.Text);
                _renaming = null;
                Refresh();
            };
            RegisterUIText(ok.ButtonText);

            var cancel = CreateSecondaryButton(row, "Cancel", "Cancel");
            cancel.OnClick += () => { _renaming = null; Refresh(); };
            RegisterUIText(cancel.ButtonText);
        }

        // ── Acts that replace or remove ───────────────────────────────────

        private void SaveCopy()
        {
            if (TranslationBackups.SaveCopy() == null)
            {
                TranslatorUIManager.StatusOverlay?.ShowToast(
                    Backups.WhyCannotSave(TranslationBackups.List()) ?? "Could not keep a copy.",
                    StatusOverlay.ToastTone.Off);
            }

            Refresh();
        }

        /// <summary>
        /// ⚠ Always confirmed, and the sentence names what is at stake rather than asking "are you
        /// sure": this replaces the translation the game is running.
        ///
        /// ⚠ **The words come from `Backups`, not from here.** They were written twice — once in
        /// this panel, once nowhere at all, since the manager asked nothing — and two screens onto
        /// one folder must not differ about what an act costs. Written twice they drift, and the
        /// drift is invisible: nobody has both dialogs open at once to notice.
        /// </summary>
        private void ConfirmRestore(BackupEntry entry)
        {
            var now = TranslatorCore.TranslationCache?.Count ?? 0;

            var body = Backups.ConfirmRestoreBody(
                entry.Lines, now, entry.At.ToString("dd MMM HH:mm"),
                Backups.IsAnotherLineage(entry.Uuid, TranslatorCore.FileUuid));

            TranslatorUIManager.ConfirmationPanel?.Show(
                Backups.ConfirmRestoreTitle, body, Backups.ConfirmRestoreVerb,
                () =>
                {
                    if (!TranslationBackups.Restore(entry.Id))
                        TranslatorUIManager.StatusOverlay?.ShowToast("It could not be put back.",
                                                                      StatusOverlay.ToastTone.Off);

                    Refresh();
                });
        }

        private void ConfirmDelete(BackupEntry entry)
        {
            var what = string.IsNullOrEmpty(entry.Label)
                ? $"the copy from {entry.At:dd MMM HH:mm}"
                : $"\"{entry.Label}\"";

            TranslatorUIManager.ConfirmationPanel?.Show(
                Backups.ConfirmDeleteTitle,
                Backups.ConfirmDeleteBody(what, entry.Lines),
                Backups.ConfirmDeleteVerb,
                () => { TranslationBackups.Delete(entry.Id); Refresh(); });
        }
    }
}
