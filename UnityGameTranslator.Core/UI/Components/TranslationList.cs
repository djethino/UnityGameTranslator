using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Reusable translation list component.
    /// Displays a scrollable list of translations with selection.
    /// </summary>
    public class TranslationList
    {
        /// <summary>
        /// How many results the list renders. The rest is announced, never
        /// dropped in silence (see Populate).
        /// </summary>
        private const int MaxDisplayed = 5;

        /// <summary>Gap between the stacked lines of a row. Enters the row's height — see CreateListItem.</summary>
        private const int InfoColumnSpacing = 3;

        /// <summary>Breathing room above and below a row's content, top and bottom each.</summary>
        private const int RowVerticalPadding = 8;

        // UI elements
        private GameObject _root;
        private GameObject _listContent;
        private Text _statusLabel;

        // State
        private List<TranslationInfo> _translations = new List<TranslationInfo>();
        private TranslationInfo _selectedTranslation;
        private bool _isSearching;

        // Callbacks
        private Action<TranslationInfo> _onSelectionChanged;
        private Func<string> _getCurrentUser;

        /// <summary>
        /// Currently selected translation.
        /// </summary>
        public TranslationInfo SelectedTranslation => _selectedTranslation;

        /// <summary>
        /// Whether a search is in progress.
        /// </summary>
        public bool IsSearching => _isSearching;

        /// <summary>
        /// Number of translations in the list.
        /// </summary>
        public int Count => _translations.Count;

        /// <summary>
        /// The scroll view hosting the list. Use it to attach a help description covering
        /// the whole list area (individual rows are generated dynamically and not described).
        /// </summary>
        public GameObject Root => _root;

        /// <summary>
        /// Create a new translation list component.
        /// </summary>
        /// <param name="getCurrentUser">Function to get current logged-in username</param>
        public TranslationList(Func<string> getCurrentUser = null)
        {
            _getCurrentUser = getCurrentUser ?? (() => TranslatorCore.Config.api_user);
        }

        /// <summary>
        /// Create the UI elements in the given parent.
        /// </summary>
        /// <param name="parent">Parent GameObject to add UI to</param>
        /// <param name="listHeight">Height of the scrollable list</param>
        /// <param name="onSelectionChanged">Callback when selection changes</param>
        public void CreateUI(GameObject parent, int listHeight, Action<TranslationInfo> onSelectionChanged = null)
        {
            _onSelectionChanged = onSelectionChanged;

            // Status label
            _statusLabel = UIFactory.CreateLabel(parent, "TranslationStatus", "", TextAnchor.MiddleLeft);
            _statusLabel.fontSize = UIStyles.FontSizeSmall;
            _statusLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_statusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Scroll view for list
            var scrollObj = UIFactory.CreateScrollView(parent, "TranslationScroll", out _listContent, out _);
            _root = scrollObj;
            // Takes the space its card has left over, so the list is what grows when the window
            // does and everything around it stays where it was put.
            //
            // This only holds now that FillViewportHeight raises a floor instead of replacing
            // what the content asks for. Before, the scrolling area was told it was exactly
            // viewport-sized whatever it held, so a flexible child swallowed the viewport and
            // pushed the row beneath it somewhere unreachable.
            UIFactory.SetLayoutElement(scrollObj, minHeight: listHeight, preferredHeight: listHeight,
                flexibleHeight: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_listContent, false, false, true, true, 5, 5, 5, 5, 5);
            // ⚠ The trough, not a field. It was InputBackground — the same value as
            // ItemBackground — so every row was painted the exact colour of what it sits on and
            // the card disappeared into its own list.
            UIStyles.SetBackground(scrollObj, UIStyles.TroughBackground);
            UIStyles.ConfigureScrollViewNoScrollbar(scrollObj);
        }

        /// <summary>
        /// Set the status message.
        /// </summary>
        public void SetStatus(string message, Color color)
        {
            if (_statusLabel != null)
            {
                _statusLabel.text = message;
                _statusLabel.color = color;
            }
        }

        /// <summary>
        /// Set the translations to display.
        /// </summary>
        public void SetTranslations(List<TranslationInfo> translations)
        {
            _translations = translations ?? new List<TranslationInfo>();
            _selectedTranslation = null;

            if (_translations.Count > 0)
            {
                _selectedTranslation = _translations[0];
            }

            Populate();
        }

        /// <summary>
        /// Clear the translation list.
        /// </summary>
        public void Clear()
        {
            _translations.Clear();
            _selectedTranslation = null;
            ClearUI();
        }

        /// <summary>
        /// Refresh the list UI (e.g., after login status change).
        /// </summary>
        public void Refresh()
        {
            if (_translations.Count > 0)
            {
                Populate();
            }
        }

        /// <summary>
        /// Search for translations by steam ID or game name.
        /// </summary>
        public async System.Threading.Tasks.Task SearchAsync(string steamId, string gameName, string targetLanguage)
        {
            if (_isSearching) return;

            _isSearching = true;
            SetStatus("Searching online...", UIStyles.StatusWarning);
            Clear();

            try
            {
                TranslationSearchResult result = null;

                // Try Steam ID first
                if (!string.IsNullOrEmpty(steamId))
                {
                    result = await ApiClient.SearchBysteamId(steamId, targetLanguage);
                }

                // Fallback to game name
                if ((result == null || !result.Success || result.Count == 0) && !string.IsNullOrEmpty(gameName))
                {
                    result = await ApiClient.SearchByGameName(gameName, targetLanguage);
                }

                // After the awaits we may be on a background thread (IL2CPP). All UI access
                // (SetStatus = _statusLabel.text, SetTranslations -> Populate -> Destroy/Create
                // child GameObjects) must run on the main thread or the IL2CPP runtime faults
                // with AccessViolationException inside the Unity layout/UI code.
                var capturedResult = result;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (capturedResult != null && capturedResult.Success)
                    {
                        var translations = capturedResult.Translations ?? new List<TranslationInfo>();
                        if (translations.Count == 0)
                        {
                            SetStatus("No translations found for your language", UIStyles.TextMuted);
                        }
                        else
                        {
                            SetStatus($"Found {translations.Count} translation(s):", UIStyles.TextPrimary);
                            SetTranslations(translations);
                        }
                    }
                    else
                    {
                        SetStatus(capturedResult?.Error ?? "Search failed", UIStyles.StatusError);
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    SetStatus($"Error: {errorMsg}", UIStyles.StatusError);
                });
                TranslatorCore.LogWarning($"[TranslationList] Search error: {errorMsg}");
            }
            finally
            {
                // bool assignment is atomic in .NET; safe to set off the main thread.
                _isSearching = false;
            }
        }

        private void ClearUI()
        {
            UIHelpers.DestroyChildren(_listContent);
        }

        private void Populate()
        {
            ClearUI();

            // isLoggedIn must be based on api_token, not api_user (api_user persists after logout)
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            string currentUser = isLoggedIn ? _getCurrentUser?.Invoke() : null;

            int displayCount = Math.Min(MaxDisplayed, _translations.Count);
            for (int i = 0; i < displayCount; i++)
            {
                var t = _translations[i];
                CreateListItem(t, isLoggedIn, currentUser);
            }

            // Never cut the list in silence: the status line says how many were
            // found, so stopping at five without a word reads as "that's all".
            if (_translations.Count > displayCount)
            {
                var moreLabel = UIFactory.CreateLabel(_listContent, "MoreResults",
                    $"Showing the {displayCount} best of {_translations.Count} — refine the search to see others",
                    TextAnchor.MiddleCenter);
                moreLabel.fontSize = UIStyles.FontSizeHint;
                moreLabel.color = UIStyles.TextMuted;
                UIFactory.SetLayoutElement(moreLabel.gameObject, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

                ShowOwnTranslationBelowTheCut(displayCount, isLoggedIn, currentUser);
            }
        }

        /// <summary>
        /// The player's own translation, shown after the cut when it did not make the visible
        /// rows.
        ///
        /// The list is NOT reordered to float it to the top: doing so would lie about the order
        /// and quietly suggest theirs is the best. Its real position is the answer to the very
        /// question this screen exists for — is mine still the one to use. But an answer nobody
        /// can see is no answer, and only five rows are drawn, so it comes back here with its
        /// rank stated.
        /// </summary>
        private void ShowOwnTranslationBelowTheCut(int displayCount, bool isLoggedIn, string currentUser)
        {
            for (int i = displayCount; i < _translations.Count; i++)
            {
                if (!TranslatorCore.IsUuidMatch(_translations[i].FileUuid)) continue;

                var rankLabel = UIFactory.CreateLabel(_listContent, "YourRank",
                    TranslatorCore.TranslateOwnUIDynamic("Your current translation ranks") + $" #{i + 1}",
                    TextAnchor.MiddleLeft);
                rankLabel.fontSize = UIStyles.FontSizeHint;
                rankLabel.color = UIStyles.TextMuted;
                UIFactory.SetLayoutElement(rankLabel.gameObject, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

                CreateListItem(_translations[i], isLoggedIn, currentUser);
                return;
            }
        }

        private void CreateListItem(TranslationInfo translation, bool isLoggedIn, string currentUser)
        {
            // Check if this translation is from the same lineage (UUID match)
            bool isLineageMatch = TranslatorCore.IsUuidMatch(translation.FileUuid);

            // What the info column will hold, decided BEFORE the row exists:
            // its height was calibrated for two lines, and the extra ones would
            // simply have been cut off.
            var facts = BuildFactsLine(translation);
            string note = BuildNoteLine(translation);

            // The bar is only drawn when the server gave us something to draw; an empty container
            // under every row would read as "nothing translated" instead of "nothing known".
            bool hasComposition = translation.HumanCount + translation.ValidatedCount +
                translation.AiCount + translation.SkippedCount + translation.CaptureCount > 0;

            // Counted from what the row ACTUALLY holds, piece by piece.
            //
            // It used to start from CodeDisplayHeight — a constant that once covered two lines of
            // text with enough slack to swallow the padding — and add a line per optional block.
            // Every change to the row since has widened the gap: the author moved to its own
            // line, the spacing was loosened, and the count silently fell fifteen pixels short of
            // the content. A row that lies about its height makes the list lie about its own, and
            // the panel lies about how much space it needs — which is how a whole tab ends up
            // overflowing a window that had room for it.
            int textRows = 3 + (facts != null ? 1 : 0) + (note != null ? 1 : 0); // languages, author, details, [facts], [note]
            int blocks = textRows + (hasComposition ? 1 : 0);
            int rowHeight = textRows * UIStyles.RowHeightSmall
                + (hasComposition ? QualityBar.CompactHeight : 0)
                + (blocks - 1) * InfoColumnSpacing
                + RowVerticalPadding * 2;

            var itemRow = UIFactory.CreateHorizontalGroup(_listContent, $"Item_{translation.Id}", false, false, true, true, 8);
            UIFactory.SetLayoutElement(itemRow, minHeight: rowHeight, flexibleWidth: 9999);
            UIStyles.SetBackground(itemRow, UIStyles.ItemBackground);

            // No left padding: the accent stripe below is flush with the edge
            var layout = itemRow.GetComponent<HorizontalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = Compat.MakeRectOffset(0, 10, RowVerticalPadding, RowVerticalPadding);
                // Top, not middle: the tick box and the vote count belong to the row's subject,
                // which is its first line. Centred vertically they drifted to the middle of a
                // five-line block and looked unattached to anything.
                layout.childAlignment = TextAnchor.UpperLeft;
            }

            // The player's own translation is marked by a stripe down the left edge rather than
            // by flooding the row with colour. A full purple wash fought every text colour on
            // top of it and made the bar's track read as a black slab; a stripe says the same
            // thing at a glance and leaves the row legible.
            var stripe = UIFactory.CreateUIObject("Accent", itemRow);
            stripe.AddComponent<Image>().color = isLineageMatch ? UIStyles.ButtonPrimary : Color.clear;
            UIFactory.SetLayoutElement(stripe, minWidth: 3, flexibleWidth: 0,
                minHeight: rowHeight, flexibleHeight: 9999);

            // Selection toggle
            var toggleObj = UIFactory.CreateToggle(itemRow, "SelectToggle", out var toggle, out var _);
            toggle.isOn = _selectedTranslation == translation;
            UIHelpers.AddToggleListener(toggle, (val) =>
            {
                if (val)
                {
                    _selectedTranslation = translation;
                    RefreshSelection();
                    _onSelectionChanged?.Invoke(translation);
                }
            });
            UIFactory.SetLayoutElement(toggleObj, minWidth: UIStyles.ToggleControlWidth);

            // Info column. Transparent: CreateVerticalGroup fits its own background image, which
            // drew a dark rectangle inside the row's own colour — a box within a box, and it hid
            // the highlight that marks the player's own translation on three of its four sides.
            var infoCol = UIFactory.CreateVerticalGroup(itemRow, "InfoCol", false, false, true, true, InfoColumnSpacing);
            UIFactory.SetLayoutElement(infoCol, flexibleWidth: 9999);
            UIStyles.ClearRowBackground(infoCol);

            // Configure info column alignment
            var infoLayout = infoCol.GetComponent<VerticalLayoutGroup>();
            if (infoLayout != null)
            {
                infoLayout.childAlignment = TextAnchor.MiddleLeft;
            }

            // The SOURCE language leads because it decides whether this
            // translation can work at all: one made from Japanese is useless on
            // a game whose text is English, and showing only the target made
            // the two indistinguishable.
            string languages = string.IsNullOrEmpty(translation.SourceLanguage)
                ? translation.TargetLanguage
                : $"{translation.SourceLanguage} → {translation.TargetLanguage}";
            bool isOwnTranslation = isLoggedIn && !string.IsNullOrEmpty(currentUser) &&
                translation.Uploader.Equals(currentUser, StringComparison.OrdinalIgnoreCase);
            // Languages alone on the first line, author on the second. Together they ran past
            // the width and wrapped, which cost a line and broke the hierarchy: the pair of
            // languages is what a reader scans for, the author is context.
            // ⚠ The flags go on their own line ABOVE the words rather than beside them: this row
            // is already narrow enough that the pair of names wraps, and two pictures inserted into
            // it would push the target language onto a second line — costing the very thing they
            // are there to speed up.
            var flagRow = UIFactory.CreateUIObject("Flags", infoCol);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(flagRow, false, false, true, true,
                                                            6, 0, 0, 0, 0, TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(flagRow, minHeight: 13, flexibleWidth: 9999);

            var drawn = LanguageMark.Create(flagRow, "Source", translation.SourceLanguage) != null;
            if (drawn && !string.IsNullOrEmpty(translation.TargetLanguage))
            {
                var arrow = UIFactory.CreateLabel(flagRow, "Arrow", "→", TextAnchor.MiddleCenter);
                arrow.fontSize = UIStyles.FontSizeHint;
                arrow.color = UIStyles.TextMuted;
                UIFactory.SetLayoutElement(arrow.gameObject, minHeight: 13, flexibleWidth: 0);
            }

            LanguageMark.Create(flagRow, "Target", translation.TargetLanguage);

            // The row is removed entirely when neither language could be marked — an empty band
            // above the title would read as a rendering fault.
            if (flagRow.transform.childCount == 0) UnityEngine.Object.Destroy(flagRow);

            var titleLabel = UIFactory.CreateLabel(infoCol, "Title", languages, TextAnchor.MiddleLeft);
            titleLabel.fontStyle = FontStyle.Bold;
            titleLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(titleLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            string by = "by " + translation.Uploader;
            if (isOwnTranslation) by += " (you)";
            // The two things about a translation that catch the eye and are written nowhere else
            // on its row. Deliberately only two: a badge works by being rare, and the line count,
            // the review stage and the download count are already there in plain words.
            if (translation.IsNew) by += "  ·  " + TranslatorCore.TranslateOwnUIDynamic("new");
            if (IsFurthest(translation)) by += "  ·  " + TranslatorCore.TranslateOwnUIDynamic("goes furthest");
            // Says in words what the stripe says in colour — a mark nobody can name is a mark
            // nobody can act on.
            if (isLineageMatch) by += "  ·  " + TranslatorCore.TranslateOwnUIDynamic("installed");

            var byLabel = UIFactory.CreateLabel(infoCol, "Author", by, TextAnchor.MiddleLeft);
            byLabel.fontSize = UIStyles.FontSizeHint;
            byLabel.color = isLineageMatch ? UIStyles.ButtonPrimary : UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(byLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // The verdict leads, the size follows: "has anyone read this" decides between two
            // translations, the line count only qualifies it.
            string detailsText = Unbreakable(FormatQualityStats(translation))
                + "  ·  " + Unbreakable($"{translation.LineCount} lines")
                + FormatCoverage(translation);
            var detailsLabel = UIFactory.CreateLabel(infoCol, "Details", detailsText, TextAnchor.MiddleLeft);
            detailsLabel.fontSize = UIStyles.FontSizeHint;
            detailsLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(detailsLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Same component, same colours and same denominator as the card and the website.
            if (hasComposition)
            {
                var bar = new QualityBar();
                bar.CreateUI(infoCol, QualityBar.CompactHeight);
                bar.SetCounts(translation.HumanCount, translation.ValidatedCount,
                    translation.AiCount, translation.SkippedCount, translation.CaptureCount);
            }

            // Second details row: is it alive, is it finished, is it used, does
            // it need anything. All of it was already received and shown nowhere.
            if (facts != null)
            {
                var factsLabel = UIFactory.CreateLabel(infoCol, "Facts", facts, TextAnchor.MiddleLeft);
                factsLabel.fontSize = UIStyles.FontSizeHint;
                factsLabel.color = UIStyles.TextMuted;
                UIFactory.SetLayoutElement(factsLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            }

            if (note != null)
            {
                var notesLabel = UIFactory.CreateLabel(infoCol, "Notes", note, TextAnchor.MiddleLeft);
                notesLabel.fontSize = UIStyles.FontSizeHint;
                notesLabel.fontStyle = FontStyle.Italic;
                notesLabel.color = UIStyles.TextSecondary;
                UIFactory.SetLayoutElement(notesLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            }

            // Vote COUNT (right side), and no arrows.
            //
            // This is a list of candidates: one is choosing between translations one has never
            // run, and a vote cast here would rate a title card. Seeing how others rated it is
            // exactly what helps you choose — casting your own belongs on the current
            // translation, once you have played with it.
            new VoteButtons().Create(itemRow, translation.Id, translation.VoteCount, null,
                translation.UserVote, interactive: false);
        }

        /// <summary>
        /// "12 Mar 2026 · complete · 87 downloads · Kept as is: 312 · ◆ Resources",
        /// or null when the server told us none of it (older servers send no content date).
        /// </summary>
        private static string BuildFactsLine(TranslationInfo translation)
        {
            var facts = new List<string>();

            string dateLabel = translation.ContentDateLabel;
            if (!string.IsNullOrEmpty(dateLabel)) facts.Add(dateLabel);
            if (string.Equals(translation.Status, "complete", StringComparison.OrdinalIgnoreCase))
                facts.Add("complete");
            if (translation.DownloadCount > 0) facts.Add($"{translation.DownloadCount} downloads");


            // Names the purple segment, which has no colour key on these rows: an author who
            // kept what must stay untouched worked better than one who let the AI run over
            // everything, and a silent band of colour would not say so.
            string kept = QualityBar.KeptLabel(translation.SkippedCount);
            if (kept != null) facts.Add(kept);

            // U+25C6, same Geometric Shapes block as the ▲▼ already rendering everywhere — an
            // emoji would land as an empty square in games whose font has no colour glyphs.
            // "Resources" and not "Assets": one word for one thing, as on the website.
            if (!string.IsNullOrEmpty(translation.ResourcesUrl)) facts.Add("◆ Resources");

            if (facts.Count == 0) return null;

            // Each fact is welded together, and the separator is the only ordinary space in the
            // line — so Unity can only break BETWEEN facts. Left alone it breaks wherever the
            // width runs out, which put "103" at the end of one line and "downloads" at the start
            // of the next: two halves of a number that mean nothing apart. Same fix as the colour
            // key, and for the same reason.
            for (int i = 0; i < facts.Count; i++) facts[i] = Unbreakable(facts[i]);

            return string.Join("  ·  ", facts.ToArray());
        }

        /// <summary>
        /// Ties the words of one item together so a line break cannot land inside it. Escaped
        /// rather than a literal non-breaking space: that character is invisible in an editor and
        /// the first tidy-up would turn it back into an ordinary one, taking the fix with it.
        /// </summary>
        private static string Unbreakable(string text)
        {
            return text == null ? null : text.Replace(' ', ' ');
        }

        /// <summary>
        /// The author's own words, on one line. Shown on the website, invisible
        /// here until now. Null when there are none.
        /// </summary>
        private static string BuildNoteLine(TranslationInfo translation)
        {
            if (string.IsNullOrEmpty(translation.Notes)) return null;

            string note = translation.Notes.Replace("\r", " ").Replace("\n", " ").Trim();
            if (note.Length == 0) return null;
            if (note.Length > 90) note = note.Substring(0, 90) + "…";

            return $"“{note}”";
        }

        /// <summary>
        /// Nobody has gone further with this game — worth saying precisely because the coverage
        /// figure stays silent at 100%: the yardstick is the furthest translation, not the game's
        /// real size, so it cannot claim the game is covered but it can say this.
        ///
        /// Silent when it has no rival in the list: being furthest alone is a race of one, and
        /// saying so would dress a lack of competition up as an achievement.
        /// </summary>
        private bool IsFurthest(TranslationInfo translation)
        {
            if (!translation.GameCoverage.HasValue || translation.GameCoverage.Value < 0.999f)
                return false;

            // Compared by game NAME, because a search by name can return several games at once
            // and being furthest is only meaningful among translations of the same one.
            foreach (var other in _translations)
            {
                if (other.Id == translation.Id) continue;
                if (string.Equals(other.GameName, translation.GameName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// How much of the game the translation reaches, next to its line count — because a line
        /// count alone says nothing: three thousand lines is a lot or a little depending on the
        /// game, and only the game's other translations can tell.
        ///
        /// Empty when the server did not report it, and empty at 100% when this file IS the
        /// yardstick: "covers 100% of the game" would promise more than it knows, since the
        /// reference is the furthest anyone has got, not the game's real size.
        /// </summary>
        private static string FormatCoverage(TranslationInfo translation)
        {
            if (!translation.GameCoverage.HasValue) return string.Empty;

            int percent = Mathf.RoundToInt(translation.GameCoverage.Value * 100f);
            if (percent >= 100) return string.Empty;

            return "  ·  " + Unbreakable(percent + "% " + TranslatorCore.TranslateOwnUIDynamic("of the game"));
        }

        /// <summary>
        /// How far the translation has been reviewed, in a few words. The proportions are the
        /// bar's job; this says whether a human has been through it at all — the one thing that
        /// decides between two translations of the same game.
        ///
        /// A file that still has most of what it met in game untranslated gets its completeness
        /// instead: reading and translating are two different jobs, and "Fully reviewed" on two
        /// lines out of thirteen told the player the opposite of the truth.
        /// </summary>
        private static string FormatQualityStats(TranslationInfo translation)
        {
            string stage = TranslationQuality.ReviewStage(
                translation.HumanCount, translation.ValidatedCount, translation.SkippedCount,
                translation.AiCount, translation.CaptureCount);

            if (stage != null) return TranslatorCore.TranslateOwnUIDynamic(stage);

            float completeness = TranslationQuality.Completeness(
                translation.HumanCount, translation.ValidatedCount, translation.SkippedCount,
                translation.AiCount, translation.CaptureCount);

            // Something IS translated, just not enough for a review stage to mean anything
            if (completeness > 0f)
            {
                return Unbreakable(Mathf.RoundToInt(completeness * 100f) + "% "
                    + TranslatorCore.TranslateOwnUIDynamic("translated"));
            }

            // Text met in game, none of it translated: the file hands the game's own words back.
            // This row used to fall through to the legacy Type field and announce "human" — so a
            // player downloaded what looked like a hand-made translation and got the original
            // text, then built on top of it. Said before the download, not discovered after.
            if (TranslationQuality.IsCaptureOnly(translation.HumanCount, translation.ValidatedCount,
                    translation.SkippedCount, translation.AiCount, translation.CaptureCount))
            {
                return Unbreakable(TranslatorCore.TranslateOwnUIDynamic("captured only, nothing translated"));
            }

            // Nothing at all to go on: older servers send no H/V/A either, and the legacy Type
            // field is the only thing left to say
            return translation.Type ?? "unknown";
        }

        private void RefreshSelection()
        {
            if (_listContent == null) return;

            // Manual iteration for IL2CPP compatibility (foreach on Transform doesn't work)
            for (int i = 0; i < _listContent.transform.childCount; i++)
            {
                Transform child = _listContent.transform.GetChild(i);
                var toggle = child.GetComponentInChildren<Toggle>();
                if (toggle != null)
                {
                    string itemName = child.name;
                    if (itemName.StartsWith("Item_") && int.TryParse(itemName.Substring(5), out int id))
                    {
                        toggle.isOn = _selectedTranslation != null && _selectedTranslation.Id == id;
                    }
                }
            }
        }
    }
}
