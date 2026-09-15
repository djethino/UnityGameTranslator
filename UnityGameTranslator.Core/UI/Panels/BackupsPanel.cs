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
    ///
    /// ⚠ Described in data since 2026-09-15 (<c>common/spec/screens/backups.json</c>): the frame —
    /// the fixed header, the card, the help bar, the Close verb — is the document's. What stays
    /// here is what a document never carries: the two blocks, built from the backups folder, and
    /// the rule that divides the body's height between their lists.
    /// </summary>
    public class BackupsPanel : TranslatorPanelBase
    {
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("backups");

        public override string Name => Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

        protected override bool PersistWindowPreferences => Doc.Persist;
        protected override bool UseBackdrop => Doc.Backdrop;

        /// <summary>
        /// 🔴 **What the two lists cost at their floors, MEASURED — not a figure written by hand.**
        /// It was 340, which is less than this screen has ever been able to draw: shrunk to it, the
        /// top list showed a heading over nothing and the bottom one barely one row. Then it was
        /// worked out from declared parts, and a declared chrome is a few pixels off whenever a
        /// sentence wraps — so at the smallest size the lists overflowed the body and the window
        /// grew a scrollbar around its own content. Now it is what the laid-out screen comes to
        /// with both lists at <see cref="ListRooms.LeastRows"/> rows, read in <see cref="BodySized"/>,
        /// and zero until then: nothing has a floor before it has been laid out.
        /// </summary>
        public override int MinHeight => _floor;

        /// <summary>
        /// 🔴 **And never taller than everything shown.** Left free to reach the screen's height,
        /// the window went on growing past the last row of both lists and showed a band of nothing
        /// under them — "agrandir la fenêtre crée du vide". The ceiling is the same measure as the
        /// floor with every list at its whole content; before it is known, the base's own answer.
        /// </summary>
        public override int MaxHeight => _ceiling > 0 ? Math.Min(_ceiling, TallestOnThisScreen) : base.MaxHeight;

        /// <summary>
        /// What the panel opens at: the body with every list showing everything it holds.
        ///
        /// 🔴 **Without this the window opened at its floor.** The lists are built at two rows and
        /// only given their real height once the body has been laid out — so the first measure,
        /// which decides the opening size, saw two rows apiece and sized the window to that:
        /// "le panneau s'ouvre trop petit". This tells the measure what the lists could show, and
        /// the base then opens the window at that, or at what the screen allows.
        /// </summary>
        protected override float ContentHeightFloor
        {
            get
            {
                if (_slices.Count == 0) return 0f;

                var content = BodyContentHeight;
                if (content <= 0f) return 0f;

                double given = 0, whole = 0;
                for (var i = 0; i < _slices.Count; i++)
                {
                    given += _slices[i].Given;
                    whole += _slices[i].List.ContentHeight;
                }

                return (float)(content - given + whole);
            }
        }

        /// <summary>
        /// The window's floor and ceiling as last measured — both lists at their least rows, both
        /// lists showing everything — window chrome included. Zero until the body has been laid
        /// out once.
        /// </summary>
        private int _floor, _ceiling;

        /// <summary>
        /// One row, provisionally — what a list asks for between being built and being measured.
        ///
        /// ⚠ Provisional and nothing more: the first <see cref="BodySized"/> replaces it with what
        /// the rows actually come to, laid out. A row here is one, two or four lines tall depending
        /// on what it has to say, so no declared figure can stand in for the measure — it either
        /// cut a list short or handed it a band of empty trough, and both were reported.
        /// </summary>
        private const int RowSpace = 56;

        /// <summary>Room between the trough's edge and its rows, top and bottom.</summary>
        private const int ListPad = 8;

        // 🔴 **No redraw on resize, and that was mine to learn twice.** Rebuilding both lists while
        // the window is being dragged tears down the scroll areas UniverseLib's auto-hiding
        // scrollbar is holding on to: the bar vanished and left its lane painted black. The
        // Manager had the same idea and it was worse there — rewriting the grid's rows from a
        // layout event is a loop, and it took the window off the screen.
        //
        // ⚠ What a resize does is pose HEIGHTS, in BodySized, from the room that was measured.

        /// <summary>
        /// One list and what it was last given — kept because the height is not settled when the
        /// list is built: it is settled once the body has a measured size, and again every time
        /// that size changes.
        /// </summary>
        private sealed class Slice
        {
            public ScrollList List;

            /// <summary>How many rows it holds — what the floor is worked out from.</summary>
            public int Rows;

            /// <summary>What it was last given — what the measured chrome is worked out against.</summary>
            public int Given;
        }

        private readonly List<Slice> _slices = new List<Slice>();

        /// <summary>Whether the lists have been given their height since the last redraw.</summary>
        private bool _shared;

        private BuiltScreen _screen;
        private ButtonHandle _saveBtn;
        private HelpZone _helpZone;

        /// <summary>Which row is being renamed, or null. One at a time, in place.</summary>
        private string _renaming;

        private Host ListHost => _screen.Host("List");

        public BackupsPanel(UIBase owner) : base(owner) { }

        public void ShowPanel()
        {
            Refresh();
            SetActive(true);

            // ⚠ The content may have changed while the window was hidden — an automatic backup is
            // taken whenever something replaces the translation — and a window opened on more rows
            // than it was last sized for should open on them, not on last time's height.
            RecalculateSize();
        }

        protected override void ConstructPanelContent()
        {
            Layout(out var body, out var footer, Doc.CardWidth);

            if (Doc.Help != null) _helpZone = CreateHelpZone(footer, Doc.Help);

            // 🔴 **What must stay put stays put.** The title, the state you are in and the privacy
            // note are what somebody reads BEFORE choosing a row — scrolled away by the twelfth
            // entry, they would have to scroll back up to remember where they stand. The document
            // says so by putting them in its header; this is where a header goes.
            _screen = ScreenBuilder.Build(Doc, body, footer, ActOf, header: FixedHeader("BackupsHeader"), help: _helpZone);

            // The socle's words, shared with the Manager's window — written, not copied into the
            // document, so the two screens cannot drift apart.
            _screen.Say("title", Backups.ScreenTitle);
            _screen.Say("privacy", Backups.PrivacyNote);

            Refresh();
        }

        private Action ActOf(string act)
        {
            switch (act)
            {
                case "close": return () => SetActive(false);
                default: return null;
            }
        }

        // ── Drawing ───────────────────────────────────────────────────────

        /// <summary>
        /// Draw the screen again from the backups folder, and give the window the size the new
        /// content asks for — after an act that changed what the lists hold.
        /// </summary>
        private void Redraw()
        {
            Refresh();
            RecalculateSize();
        }

        private void Refresh()
        {
            if (_screen == null) return;

            ListHost.Clear();

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
            //
            // 🔴 **Nothing here works out any heights, and that is the fix.** Three shapes were
            // tried and each patched the one before: a figure written in the panel, then a share of
            // the room in proportion to the rows, then that share computed from an ESTIMATE of how
            // much room there was. The last is why a stretched window showed a gap under one list
            // while the other was still incomplete — the numbers were settled once, at draw time,
            // from a guess, inside a layout system that knows the real sizes and re-runs on every
            // resize. The heights are posed in BodySized, from a body that has been MEASURED, and
            // posed again whenever that body moves.
            _slices.Clear();
            _shared = false;

            Group(Backups.SavedHeading, $"{saved.Count} of {Backups.SavedKept}", saved,
                  "No backups yet. Take one before you try something, and you can walk back out "
                  + "of whatever you try.",
                  saved: true);

            Stacks.Spacer(ListHost, 12);

            Group(Backups.AutomaticHeading, Backups.AutomaticNote, automatic,
                  "Nothing yet. One is taken whenever something replaces your translation.");

            // ⚠ Straight away when the body already has a height — a redraw after a backup or a
            // restore is not a first show, and waiting for a resize would leave both lists at two
            // rows in a window that has room for ten.
            BodySized();
        }

        /// <summary>
        /// The body has a measured size: divide it between the lists, and put the window back
        /// between its floor and its ceiling if it is outside them.
        ///
        /// 🔴 **The room is measured, never guessed.** That distinction is the whole difference
        /// between this and the arithmetic it replaced: the old one took an ESTIMATE of the
        /// available room at draw time and settled the heights once, so a stretched window left a
        /// gap under one list while the other was still scrolling. This is asked again every time
        /// the body's size changes, with the size the layout actually gave it.
        ///
        /// 🔴 **And so is what each list HOLDS.** A row is one, two or four lines tall depending
        /// on what it has to say; a declared row height either cut a list short or handed it a
        /// band of empty trough. Each list is asked what its rows come to, laid out at this width.
        /// </summary>
        protected override void BodySized()
        {
            if (_screen == null) return;

            // ⚠ Nothing can be measured on a hidden hierarchy: the layout does not run there, and
            // every row answers zero. A redraw while hidden waits for the show, which asks again.
            if (UIRoot == null || !UIRoot.activeInHierarchy) return;

            var body = BodyHeight;
            if (body <= 1f) return;

            // 🔴 **The chrome is MEASURED, and nothing about it is written down here.** Everything
            // the body carries that is not a list — two headings, the verb under one of them, the
            // padding of the card, the gap between the blocks, a whole block reduced to a sentence
            // when its list is empty — is the difference between what the content asks for and what
            // the lists were last given. Declaring those figures instead is what left a band of
            // empty card under the lists when it overshot, and a scrollbar around the whole screen
            // when it fell short. The same for what the window carries around its body: title bar,
            // header, help bar, footer — the window's height less the body's.
            double given = 0;
            for (var i = 0; i < _slices.Count; i++) given += _slices[i].Given;

            var chrome = BodyContentHeight - given;
            var around = Rect.rect.height - body;

            var rooms = new List<ListRoom>();
            double least = 0, whole = 0;
            for (var i = 0; i < _slices.Count; i++)
            {
                var content = _slices[i].List.ContentHeight;
                var room = ListRooms.Of(content, _slices[i].Rows, content / _slices[i].Rows);
                rooms.Add(room);
                least += room.Least;
                whole += room.Whole;
            }

            _floor = (int)Math.Ceiling(around + chrome + least);
            _ceiling = (int)Math.Ceiling(around + chrome + whole);

            var heights = ListRooms.Share(rooms, body - chrome);

            for (var i = 0; i < _slices.Count; i++)
            {
                // 🔴 **A list nobody has scrolled stays at its first row; one somebody is reading
                // is left alone.** Read before the height is posed, and put back after: a rebuilt
                // list is put back whatever it showed, since it is not the same list.
                var atTop = _slices[i].List.AtTop;

                _slices[i].Given = (int)Math.Max(0, heights[i]);
                _slices[i].List.SetHeight(_slices[i].Given, fill: false);

                if (!_shared || atTop) _slices[i].List.ToTop();
            }

            _shared = true;

            // ⚠ A window restored from a preference, or one whose content changed while it was
            // hidden, can be under the floor or over the ceiling that were just measured. Under,
            // it wraps a scrollbar around its own content; over, it shows a band of nothing. It is
            // put back between the two, and this is asked again with the body that gives.
            // ⚠ The ceiling is bounded by the screen, like every height this panel takes: a
            // window taller than the screen shows everything and can be read to the bottom by
            // nobody — it was dragged there once, saved, and opened half off the screen.
            var height = Rect.rect.height;
            if (height < _floor - 0.5f) ResizeTo(_floor);
            else if (height > MaxHeight + 0.5f) ResizeTo(MaxHeight);
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
            _screen.Say("now", Backups.NowLine(NowLines()));
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
                           bool saved = false)
        {
            // Left 6, right 8, top 8, bottom 8 — the padding as it was, named.
            // ⚠ Pinned, and its list is given a settled height: a flexible block hands spare room to
            // a list that has nothing to put there, and a flexible list in a pinned block is pinned
            // anyway. Both were learnt the hard way, one after the other.
            var block = Stacks.Vertical(ListHost, "Group", spacing: 4, pad: new Pad(6, 8, 8, 8),
                                        surface: Surface.Elevated,
                                        fillHeight: false);

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

                // ⚠ Nothing is recorded for it. A block with no rows is a sentence, not a scroll
                // area: it takes its own height, and BodySized counts it as chrome without being
                // told — it is part of what the content asks for and not part of what the lists
                // were given.
                return;
            }

            // 🔴 Its own scroll area. Ten rows in the outer scroll would push the second heading
            // below the fold, and somebody scrolling to reach it loses the first — which is the
            // state the whole screen exists to compare against.
            //
            // ⚠ It opens at a PROVISIONAL floor and takes no flexible share, so the sum of what
            // this body asks for stays under the body itself and the panel never grows a scrollbar
            // of its own. The real height is posed in BodySized, from the measured room and the
            // measured rows.
            var floor = (int)ListRooms.For(entries.Count, RowSpace, ListPad).Least;

            var list = ScrollList.Create(block, "Rows",
                                         minHeight: floor, preferredHeight: floor,
                                         fillHeight: false, spacing: 4);

            _slices.Add(new Slice { List = list, Rows = entries.Count, Given = floor });

            foreach (var entry in entries) Row(list.Rows, entry);

            // ⚠ Put back at its first row in BodySized, not here: the height it is built at is not
            // the one it ends up with, and a scroll area whose viewport changes size settles
            // wherever its offset happens to land.

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

            ShowLanguages(text, entry);

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
                        Intents.Toast(
                            Backups.WhyNoRoom(TranslationBackups.List())
                            ?? "This one could not be kept.", ToastTone.Off);
                    }

                    Redraw();
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
        /// <summary>
        /// What this copy translates, in two flags.
        ///
        /// 🔴 **Two flags and an arrow, never the names.** A row already carries a date, a count of
        /// lines, sometimes a name somebody wrote and a reason — adding "English → French" in words
        /// makes a list of ten copies a wall of text, and the question being asked here is only
        /// "which one of mine is this". A flag answers it at a glance and takes no width.
        ///
        /// ⚠ A source that was never settled — "auto", or nothing — gets the arrow with nothing
        /// before it, because that IS the fact: the copy was taken before anybody said what it
        /// translates from. Writing "Auto" there would dress an absence up as an answer.
        ///
        /// ⚠ Nothing at all when neither is known: an older copy whose file did not say is a row
        /// with one line less, not a row with two empty boxes.
        /// </summary>
        private static void ShowLanguages(Host text, BackupEntry entry)
        {
            bool source = Backups.IsSettledLanguage(entry.SourceLanguage);
            bool target = Backups.IsSettledLanguage(entry.TargetLanguage);

            if (!source && !target) return;

            var row = Stacks.Row(text, "Languages", spacing: 4,
                                 minHeight: UIStyles.RowHeightSmall);

            if (source) LanguageMark.Create(row, "From", entry.SourceLanguage);

            // ⚠ Excluded from the mod's own translation pass: an arrow is a sign, not a word, and
            // there is nothing to translate in it.
            Labels.Create(row, "To", "→", TextRole.Caption, tone: Tone.Secondary,
                          policy: TextPolicy.Excluded);

            if (target) LanguageMark.Create(row, "Into", entry.TargetLanguage);
        }

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
                Intents.Toast(
                    Backups.WhyCannotSave(TranslationBackups.List(), NowLines())
                    ?? "It could not be kept.", ToastTone.Off);
            }

            Redraw();
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

            Intents.Confirm(
                Backups.ConfirmRestoreTitle, body, Backups.ConfirmRestoreVerb,
                () =>
                {
                    if (!TranslationBackups.Restore(entry.Id))
                        Intents.Toast("It could not be put back.", ToastTone.Off);

                    Redraw();
                });
        }

        private void ConfirmDelete(BackupEntry entry)
        {
            var what = string.IsNullOrEmpty(entry.Label)
                ? $"from {entry.At:dd MMM HH:mm}"
                : $"\"{entry.Label}\"";

            Intents.Confirm(
                Backups.ConfirmDeleteTitle,
                Backups.ConfirmDeleteBody(what, entry.Lines),
                Backups.ConfirmDeleteVerb,
                () => { TranslationBackups.Delete(entry.Id); Redraw(); });
        }
    }
}
