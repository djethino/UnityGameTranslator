using System;
using System.Collections.Generic;
using UniverseLib.UI;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// This game's translation, as it stood at earlier moments.
    ///
    /// 🔴 **A panel and not a section on the main screen.** The main screen already carries five
    /// sections; a list of a dozen rows with three verbs each would swamp the one thing it exists
    /// to show — what your translation IS right now. The main screen gains a single line naming
    /// how many backups exist and a way in, which is how Merge, Upload and Login already work here.
    ///
    /// ⚠ **Two lists, never one.** They do not live equally long: an automatic backup ages out
    /// on its own, one you took stays until you delete it. Two rows that look alike and do not
    /// survive alike is how people lose things they thought were kept.
    ///
    /// ⚠ Everything the rows say comes from <see cref="Backups"/>, so a backup taken in the game and
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

        private Host _listHost;
        private LabelHandle _nowLabel;
        private ButtonHandle _saveBtn;
        private HelpZone _helpZone;

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
            Layout(out var body, out var footer, PanelWidth - 40);

            _helpZone = CreateHelpZone(footer, "Hover an element to see what it does");

            // 🔴 **What must stay put stays put.** The title, the state you are in and the button
            // that keeps a copy are what somebody reads BEFORE choosing a row — scrolled away by
            // the twelfth entry, they would have to scroll back up to remember where they stand.
            // CreateFixedHeader exists for exactly this and is what the other panels use.
            var header = FixedHeader("BackupsHeader");

            var head = Stacks.Card(header, "BackupsHead", PanelWidth - 40);

            Labels.Create(head, "Title", Backups.ScreenTitle, TextRole.Title);

            // ⚠ Said once, at the top. Somebody looking at a list of their own work deserves to
            // know it goes nowhere before they wonder whether it does.
            Labels.Create(head, "Privacy", Backups.PrivacyNote, TextRole.Caption, fill: Fill.Stretch);

            Stacks.Spacer(head, 6);

            // 🔴 The current state, first. Without it no row can be read: a line count is neither
            // more nor less until you know where you stand today.
            _nowLabel = Labels.Create(head, "Now", "", TextRole.Body, policy: TextPolicy.Excluded,
                                      fill: Fill.Stretch);
            _nowLabel.Bold = true;

            // The rows themselves are the only thing that scrolls, in the panel's own scroll area.
            var card = Stacks.Card(body, "BackupsCard", PanelWidth - 40);

            _listHost = Stacks.Vertical(card, "List", spacing: 6);

            var closeBtn = Buttons.Secondary(footer, "CloseBtn", "Close");
            closeBtn.Clicked += () => SetActive(false);

            Refresh();
        }

        // ── Drawing ───────────────────────────────────────────────────────

        private void Refresh()
        {
            if (_listHost == null) return;

            _listHost.Clear();

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
                  "No backups yet. Take one before you try something, and you can walk back out "
                  + "of whatever you try.",
                  height: 190, saved: true);

            Stacks.Spacer(_listHost, 12);

            Group(Backups.AutomaticHeading, Backups.AutomaticNote, automatic,
                  "Nothing yet. One is taken whenever something replaces your translation.",
                  height: 150);
        }

        /// <summary>
        /// What the translation holds right now — asked in one place.
        ///
        /// ⚠ The header states it, the Backup button is allowed or refused on it, and the restore
        /// question weighs the backup against it. Three readings of one figure: written three times,
        /// the day one of them changes source the screen contradicts itself.
        /// </summary>
        private static int NowLines() => TranslatorCore.TranslationCache?.Count ?? 0;

        private void RefreshHeader(int savedCount)
        {
            if (_nowLabel == null) return;

            _nowLabel.Show(Backups.NowLine(NowLines()));
        }

        /// <summary>Whether another backup may be taken, and why not when it may not.</summary>
        private void RefreshSaveButton()
        {
            if (_saveBtn == null) return;

            // 🔴 **Including "there are no lines yet".** The ceiling was the only refusal asked
            // about, so a game whose translation has not started offered Backup — and taking one
            // produced a row that looks like every other and restores to nothing. The socle decides
            // both, so this panel and the manager's window refuse the same thing in the same words.
            var why = Backups.WhyCannotSave(TranslationBackups.List(), NowLines());
            var can = why == null;

            _saveBtn.Enabled = can;

            // ⚠ Never a control that cannot be pressed without words saying why.
            _helpZone?.Describe(_saveBtn, can
                ? "Backs up the translation as it stands, with the fonts and images it uses."
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
            // Left 6, right 8, top 8, bottom 8 — the padding as it was, named.
            var block = Stacks.Vertical(_listHost, "Group", spacing: 4, pad: new Pad(6, 8, 8, 8),
                                        surface: Surface.Elevated);

            var titleRow = Stacks.Row(block, "Heading", spacing: 8, minHeight: UIStyles.SectionTitleHeight);

            Labels.Create(titleRow, "Text", heading, TextRole.SectionTitle, minWidth: 190);

            // ⚠ Beside the heading, right-aligned: it qualifies the LIST — how full it is, or that
            // it ages out — and on a row it would read as being about that row.
            Labels.Create(titleRow, "Note", note, TextRole.Caption, policy: TextPolicy.Excluded,
                          align: Placement.MiddleRight, fill: Fill.Stretch);

            if (entries.Count == 0)
            {
                Labels.Create(block, "Empty", empty, TextRole.Caption, fill: Fill.Stretch);

                // ⚠ Offered even on an empty list: this is the one control that puts the FIRST
                // copy there, and hiding it until a copy exists would hide it from everybody who
                // has never made one.
                AddSaveButton(block, saved);
                return;
            }

            // 🔴 Its own scroll area, capped. Ten rows in the outer scroll would push the second
            // heading below the fold, and somebody scrolling to reach it loses the first — which
            // is the state the whole screen exists to compare against.
            var list = ScrollList.Create(block, "Rows", minHeight: Math.Min(height, entries.Count * 40 + 8),
                                         preferredHeight: height, fillHeight: false, spacing: 4);

            foreach (var entry in entries) Row(list.Rows, entry);

            AddSaveButton(block, saved);
        }

        /// <summary>
        /// 🔴 **Under the list it fills, not above it.** Every verb in this product sits below the
        /// zone it acts on — the Apply of a settings block, the Apply of a hotkey. Above, it read
        /// as a heading for the list rather than an act upon it, and the eye had to travel back up
        /// to find it.
        /// </summary>
        private void AddSaveButton(Host block, bool saved)
        {
            if (!saved) return;

            // Pushes the button to the right edge, as every action row in this product does.
            var row = Stacks.Row(block, "SaveRow", spacing: 8, minHeight: UIStyles.RowHeightNormal,
                                 placement: Placement.MiddleRight);

            _saveBtn = Buttons.Primary(row, "SaveBtn", "Backup");
            _saveBtn.Clicked += SaveCopy;

            RefreshSaveButton();
        }

        /// <summary>
        /// One copy: what it is, why it exists, and what may be done with it.
        ///
        /// 🔴 **Two lines at most, and the verbs share the first one.** Stacked — facts, then
        /// reason, then a row of buttons — a copy took four lines and each list showed less than
        /// two entries. A list you cannot read two rows of is not a list, it is a keyhole.
        /// </summary>
        private void Row(Host rows, BackupEntry entry)
        {
            // 🔴 **Being renamed, the row becomes two storeys.** A field has no room on a row that
            // already carries the facts and two buttons: it came out about 170 px wide, and wider
            // or narrower depending on how long the name and the date beside it happened to be — so
            // the one control somebody is typing into was the only thing on the screen whose size
            // moved. Laid out downwards, the facts keep their line and the field gets one.
            bool renaming = _renaming == entry.Id;

            var box = renaming
                ? Stacks.Vertical(rows, "Entry", spacing: 4, pad: new Pad(4, 4, 6, 6),
                                  surface: Surface.Item)
                : Stacks.Horizontal(rows, "Entry", spacing: 8, pad: new Pad(4, 4, 6, 6),
                                    surface: Surface.Item);

            var text = Stacks.Vertical(box, "Text", spacing: 1);

            // 🔴 **What identifies stays on the first line; what qualifies goes underneath,
            // small.** Everything on one line grew wider than the row and pushed against the
            // verbs beside it. Split in two, nothing is cut and the row reads the way every other
            // list in this product reads.
            // 🔴 **A NAME identifies; a date only qualifies.** This panel says so itself, in the
            // help beside the Rename button: "Ten dated rows are not a choice. A name is what
            // makes one of them findable." It was written in the smallest role and the dimmest
            // tone, on the second line, under the date — so the one thing somebody scans the list
            // for was the hardest thing on the row to read.
            //
            // ⚠ The grammar of the row does not change, only what fills each slot: what
            // identifies on the first line, what qualifies underneath. A backup nobody named
            // still has the date as its identifier, and keeps it there.
            string named = !string.IsNullOrEmpty(entry.Label) ? entry.Label : null;
            string facts = $"{entry.At:dd MMM HH:mm}   {entry.Lines} lines";

            var details = new List<string>();

            // Named, the date joins the qualifiers. Unnamed, the act that caused it takes their
            // first place — an unnamed saved copy says nothing here, since "Saved by you" is the
            // heading of the very list it sits in, repeated on every row.
            if (named != null) details.Add(facts);
            else if (!entry.IsSaved) details.Add(Backups.Describe(entry.Reason, entry.By));

            if (entry.ByHand > 0) details.Add($"{entry.ByHand} by hand");
            if (entry.WithAssets) details.Add("with fonts and images");

            // ⚠ Excluded either way: a date and a count are not ours to rewrite, and a name is
            // somebody's own words.
            Labels.Create(text, "Facts", named ?? facts, TextRole.Body, policy: TextPolicy.Excluded,
                          fill: Fill.Stretch, minHeight: UIStyles.RowHeightSmall);

            // 🔴 The one restore that cannot be undone with another click, said where the counts
            // are and not in small print underneath.
            if (Backups.IsAnotherLineage(entry.Uuid, TranslatorCore.FileUuid))
            {
                Labels.Create(text, "Foreign", Backups.AnotherLineageNote, TextRole.Caption,
                              tone: Tone.Warning, fill: Fill.Stretch);
            }

            // ⚠ Absent entirely when there is nothing to say, rather than an empty line: a backup
            // taken a second ago, unnamed and with no assets, is one line and no more.
            if (details.Count > 0)
            {
                // ⚠ Excluded from the mod's own translation pass: it carries a name somebody
                // wrote and figures, neither of which is ours to rewrite.
                Labels.Create(text, "Why", string.Join(" · ", details), TextRole.Caption,
                              tone: Tone.Secondary, policy: TextPolicy.Excluded, fill: Fill.Stretch);
            }

            if (renaming)
            {
                RenameRow(box, entry);
                return;
            }

            // ── the verbs, on the same line, at the right edge ──
            var buttons = Stacks.Horizontal(box, "Verbs", spacing: 4, placement: Placement.MiddleRight,
                                            fill: Fill.Content, minHeight: UIStyles.RowHeightSmall);

            var restore = Buttons.Secondary(buttons, "Restore", "Restore");
            restore.Clicked += () => ConfirmRestore(entry);
            _helpZone?.Describe(restore,
                "Puts this backup into the game. What is there now is backed up first, so this "
                + "can be walked back.");

            if (entry.IsSaved)
            {
                var rename = Buttons.Secondary(buttons, "Rename", "Rename");
                rename.Clicked += () => { _renaming = entry.Id; Refresh(); };
                _helpZone?.Describe(rename,
                    "Ten dated rows are not a choice. A name is what makes one of them findable.");

                var delete = Buttons.Secondary(buttons, "Delete", "Delete");
                delete.Clicked += () => ConfirmDelete(entry);
                _helpZone?.Describe(delete,
                    "Deletes this backup and frees a slot. Nothing else is touched.");
            }
            else
            {
                // ⚠ The gesture that closes the loop between the two lists: recognise the one you
                // want before it ages out, and it stops ageing.
                //
                // ⚠ Keeping COPIES, so this row stays here and goes on rotating — and its button
                // can then only fail on a copy already kept. Refused before the click rather than
                // after it, which is the one thing a disabled control is for.
                var all = TranslationBackups.List();
                bool already = Backups.AlreadyKept(all, entry);

                var keep = Buttons.Secondary(buttons, "Keep", "Keep");
                keep.Enabled = !already && Backups.CanSaveAnother(all);
                keep.Clicked += () =>
                {
                    if (!TranslationBackups.Keep(entry.Id))
                    {
                        // ⚠ The slot ceiling alone: this duplicates a backup that already holds
                        // lines, so how many the game holds today has no say in it.
                        TranslatorUIManager.StatusOverlay?.ShowToast(
                            Backups.WhyNoRoom(TranslationBackups.List())
                            ?? "This one could not be kept.", StatusOverlay.ToastTone.Off);
                    }

                    Refresh();
                };
                _helpZone?.Describe(keep, already
                    ? Backups.AlreadyKeptHint
                    : "Copies it into " + Backups.SavedHeading + ", so it stops ageing out. This "
                      + "one stays where it is.");
            }
        }

        /// <summary>
        /// The line the name is typed on — its own, under the facts it names.
        ///
        /// ⚠ **The grammar is the search row of <see cref="UploadSetupPanel"/>**: the field takes the
        /// row, its buttons are as tall as it (<see cref="ButtonSize.Field"/>) and short, and there
        /// is no caption — the row above already says which backup this is, so nothing here repeats
        /// it. It was built instead with the ordinary 130/110-wide buttons and a 22 px field, where
        /// every other field in this product is 32.
        ///
        /// ⚠ A floor on the width as well: the panel can be dragged down to
        /// <see cref="MinWidth"/>, and what must not shrink there is the field, not the two verbs.
        /// </summary>
        private void RenameRow(Host box, BackupEntry entry)
        {
            var row = Stacks.Row(box, "Rename", spacing: 5, minHeight: UIStyles.RowHeightLarge);

            var field = Fields.Create(row, "Label", "What is this one?",
                                      minHeight: UIStyles.InputHeight, minWidth: 160);
            field.Text = entry.Label ?? "";

            // ⚠ ONE act, two ways to reach it — the button and the key run the same lines. Two
            // copies is how one of them comes to lack the other's conditions.
            Action save = () =>
            {
                TranslationBackups.Rename(entry.Id, field.Text);
                _renaming = null;
                Refresh();
            };

            // 🔴 Enter validates, as it does in every program. It used to press Cancel: this input
            // module sends Submit to whatever is SELECTED, and ending the edit moved the selection
            // onto the button beside the field. See UIHelpers.AddSubmitListener.
            field.Submitted(_ => save());

            // ⚠ Cancel, then the verb — the order ConfirmationPanel uses and the order the manager's
            // own naming dialog uses. This row had them the other way round.
            var cancel = Buttons.Create(row, "Cancel", "Cancel", ButtonTone.Secondary,
                                        ButtonSize.Field, minWidth: 70);
            cancel.Clicked += () => { _renaming = null; Refresh(); };

            var ok = Buttons.Create(row, "Ok", "Save", ButtonTone.Primary,
                                    ButtonSize.Field, minWidth: 70);
            ok.Clicked += () => save();
        }

        // ── Acts that replace or remove ───────────────────────────────────

        private void SaveCopy()
        {
            if (TranslationBackups.SaveCopy() == null)
            {
                TranslatorUIManager.StatusOverlay?.ShowToast(
                    Backups.WhyCannotSave(TranslationBackups.List(), NowLines())
                    ?? "It could not be kept.", StatusOverlay.ToastTone.Off);
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
            var body = Backups.ConfirmRestoreBody(
                entry.Lines, NowLines(), entry.At.ToString("dd MMM HH:mm"),
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
                ? $"from {entry.At:dd MMM HH:mm}"
                : $"\"{entry.Label}\"";

            TranslatorUIManager.ConfirmationPanel?.Show(
                Backups.ConfirmDeleteTitle,
                Backups.ConfirmDeleteBody(what, entry.Lines),
                Backups.ConfirmDeleteVerb,
                () => { TranslationBackups.Delete(entry.Id); Refresh(); });
        }
    }
}
