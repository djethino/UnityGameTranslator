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
        private Text _countLabel;
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

            var card = CreateAdaptiveCard(scrollContent, "BackupsCard", PanelWidth - 40);

            var title = CreateTitle(card, "Title", "Backups");
            RegisterUIText(title);

            // ⚠ Said once, at the top. Somebody looking at a list of their own work deserves to
            // know it goes nowhere before they wonder whether it does.
            var privacy = UIFactory.CreateLabel(card, "Privacy", Backups.PrivacyNote, TextAnchor.MiddleLeft);
            privacy.color = UIStyles.TextMuted;
            privacy.fontSize = UIStyles.FontSizeHint;
            UIFactory.SetLayoutElement(privacy.gameObject, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 9999);
            RegisterUIText(privacy);

            UIStyles.CreateSpacer(card, 8);

            // 🔴 The current state, first. Without it no row can be read: "3 210 lines" is neither
            // more nor less until you know where you stand today.
            _nowLabel = UIFactory.CreateLabel(card, "Now", "", TextAnchor.MiddleLeft);
            _nowLabel.fontStyle = FontStyle.Bold;
            _nowLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_nowLabel.gameObject, minHeight: UIStyles.RowHeightNormal,
                                       flexibleWidth: 9999);
            RegisterExcluded(_nowLabel);

            UIStyles.CreateSpacer(card, 8);

            var actionRow = UIStyles.CreateFormRow(card, "SaveRow", UIStyles.RowHeightNormal, 8);

            _saveBtn = CreatePrimaryButton(actionRow, "SaveBtn", "Save a copy");
            _saveBtn.OnClick += SaveCopy;
            RegisterUIText(_saveBtn.ButtonText);
            _helpZone?.Describe(_saveBtn.Component.gameObject,
                "Keeps the translation as it stands, with the fonts and images it uses. It stays "
                + "until you remove it.");

            _countLabel = UIFactory.CreateLabel(actionRow, "Count", "", TextAnchor.MiddleRight);
            _countLabel.color = UIStyles.TextSecondary;
            _countLabel.fontSize = UIStyles.FontSizeHint;
            UIFactory.SetLayoutElement(_countLabel.gameObject, flexibleWidth: 9999);
            RegisterExcluded(_countLabel);

            UIStyles.CreateSpacer(card, 10);

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

            Section(Backups.SavedHeading, null);

            if (saved.Count == 0)
            {
                Empty("Nothing kept yet. \"Save a copy\" puts one here before you try something.");
            }
            else
            {
                foreach (var entry in saved) Row(entry);
            }

            UIStyles.CreateSpacer(_listHost, 10);

            Section(Backups.AutomaticHeading, Backups.AutomaticNote);

            if (automatic.Count == 0)
            {
                Empty("Nothing yet. One is kept whenever something replaces your translation.");
            }
            else
            {
                foreach (var entry in automatic) Row(entry);
            }
        }

        private void RefreshHeader(int savedCount)
        {
            if (_nowLabel != null)
            {
                var lines = TranslatorCore.TranslationCache?.Count ?? 0;
                _nowLabel.text = $"Now: {lines} lines";
            }

            if (_countLabel != null)
                _countLabel.text = $"{savedCount} of {Backups.SavedKept} saved";

            if (_saveBtn?.Component != null)
            {
                var why = Backups.WhyCannotSave(TranslationBackups.List());
                var can = why == null;

                _saveBtn.Component.interactable = can;

                // ⚠ Never a control that cannot be pressed without words saying why — the rule
                // this product holds everywhere.
                _helpZone?.Describe(_saveBtn.Component.gameObject, can
                    ? "Keeps the translation as it stands, with the fonts and images it uses."
                    : why);
            }
        }

        private void Section(string heading, string note)
        {
            var row = UIStyles.CreateFormRow(_listHost, "Heading", UIStyles.RowHeightSmall, 8);

            var label = UIFactory.CreateLabel(row, "Text", heading, TextAnchor.MiddleLeft);
            label.fontStyle = FontStyle.Bold;
            label.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(label.gameObject, minWidth: 180);
            RegisterUIText(label);

            if (note == null) return;

            // ⚠ Beside the heading, not under a row. It is a property of the LIST — these age out
            // — and putting it on one row would read as being about that row.
            var hint = UIFactory.CreateLabel(row, "Note", note, TextAnchor.MiddleRight);
            hint.color = UIStyles.TextMuted;
            hint.fontSize = UIStyles.FontSizeHint;
            UIFactory.SetLayoutElement(hint.gameObject, flexibleWidth: 9999);
            RegisterUIText(hint);
        }

        private void Empty(string text)
        {
            var label = UIFactory.CreateLabel(_listHost, "Empty", text, TextAnchor.MiddleLeft);
            label.color = UIStyles.TextMuted;
            label.fontSize = UIStyles.FontSizeHint;
            UIFactory.SetLayoutElement(label.gameObject, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 9999);
            RegisterUIText(label);
        }

        private void Row(BackupEntry entry)
        {
            var box = UIFactory.CreateVerticalGroup(_listHost, "Entry", false, false, true, true, 2,
                                                    new Vector4(6, 6, 8, 8));
            UIStyles.SetBackground(box, UIStyles.ItemBackground);
            UIFactory.SetLayoutElement(box, flexibleWidth: 9999);

            // ── first line: when, and what is in it ──
            var facts = $"{entry.At:dd MMM HH:mm}   {entry.Lines} lines";
            if (entry.ByHand > 0) facts += $" · {entry.ByHand} by hand";
            if (entry.WithAssets) facts += " · with fonts and images";

            var factsLabel = UIFactory.CreateLabel(box, "Facts", facts, TextAnchor.MiddleLeft);
            factsLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(factsLabel.gameObject, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 9999);
            RegisterExcluded(factsLabel);

            // 🔴 The one restore that cannot be undone with another click, said where the counts
            // are and not in small print underneath.
            if (Backups.IsAnotherLineage(entry.Uuid, TranslatorCore.FileUuid))
            {
                var warn = UIFactory.CreateLabel(box, "Foreign", Backups.AnotherLineageNote,
                                                 TextAnchor.MiddleLeft);
                warn.color = UIStyles.StatusWarning;
                warn.fontSize = UIStyles.FontSizeHint;
                UIFactory.SetLayoutElement(warn.gameObject, minHeight: UIStyles.RowHeightSmall,
                                           flexibleWidth: 9999);
                RegisterUIText(warn);
            }

            // ── second line: why it exists, or what you called it ──
            var subtitle = string.IsNullOrEmpty(entry.Label)
                ? Backups.Describe(entry.Reason, entry.By)
                : "\"" + entry.Label + "\"";

            var subtitleLabel = UIFactory.CreateLabel(box, "Why", subtitle, TextAnchor.MiddleLeft);
            subtitleLabel.color = UIStyles.TextSecondary;
            subtitleLabel.fontSize = UIStyles.FontSizeHint;
            UIFactory.SetLayoutElement(subtitleLabel.gameObject, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 9999);

            if (string.IsNullOrEmpty(entry.Label)) RegisterUIText(subtitleLabel);
            else RegisterExcluded(subtitleLabel);

            if (_renaming == entry.Id)
            {
                RenameRow(box, entry);
                return;
            }

            // ── the verbs ──
            var buttons = UIStyles.CreateFormRow(box, "Verbs", UIStyles.RowHeightSmall, 6);

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
        /// </summary>
        private void ConfirmRestore(BackupEntry entry)
        {
            var now = TranslatorCore.TranslationCache?.Count ?? 0;

            var body = $"The game will use the {entry.Lines}-line version from "
                     + $"{entry.At:dd MMM HH:mm} instead of the {now} lines it has now.\n\n"
                     + "What you have now is kept as a backup, so you can come back to it.";

            if (Backups.IsAnotherLineage(entry.Uuid, TranslatorCore.FileUuid))
            {
                body += "\n\n⚠ This copy is " + Backups.AnotherLineageNote
                      + ". Its lines and its history are not yours.";
            }

            TranslatorUIManager.ConfirmationPanel?.Show(
                "Put this copy back?", body, "Put it back",
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
                "Delete this copy?",
                $"This removes {what} and its {entry.Lines} lines. Nothing else is touched, and "
                + "the translation you are playing with stays as it is.",
                "Delete",
                () => { TranslationBackups.Delete(entry.Id); Refresh(); });
        }
    }
}
