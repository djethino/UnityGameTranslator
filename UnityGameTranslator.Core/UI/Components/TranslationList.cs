using System;
using System.Collections.Generic;
using UnityEngine;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// The community list: the translations the website holds for this game, one row each, and
    /// the one the player picks.
    ///
    /// ⚠ **Its shape is a document** (common/spec/screens/parts/community-list.json, a PART:
    /// templates alone, shared by the two screens that hold this list). The status line and the
    /// scrolling list are built once into the host a screen leaves for it; a row is built per
    /// translation shown. What a row says, and which row is the player's own, is decided here from
    /// what the server sent — never in the document.
    /// </summary>
    public class TranslationList
    {
        /// <summary>The part, read once: the list's shape, in the closed vocabulary.</summary>
        private static readonly ScreenDocument Part = ScreenDocument.FromEmbedded("parts/community-list");

        /// <summary>The templates placed once or per row that ask for nothing: no verb to hand them.</summary>
        private static readonly Func<string, Action> NoActs = _ => null;

        /// <summary>
        /// How many results the list renders. The rest is announced, never
        /// dropped in silence (see Populate).
        /// </summary>
        private const int MaxDisplayed = 5;

        // UI elements
        private ScrollList _list;
        private LabelHandle _statusLabel;

        /// <summary>Each row shown: the translation's id, its tick box, its box — for RefreshSelection.</summary>
        private readonly List<ShownRow> _rows = new List<ShownRow>();

        private struct ShownRow
        {
            public int Id;
            public ToggleHandle Select;
            public Host Root;
        }

        /// <summary>
        /// The account's own library, by lineage: true where it leads, false where it contributes.
        /// Null until read for the current token — and null is "not said yet", never "nothing":
        /// a row shows no role chip at all rather than a guess. Read once per token, as the
        /// Manager does, since check-uuid per row would spend the account's budget on one question.
        /// </summary>
        private Dictionary<string, bool> _library;
        private string _libraryFor;
        private bool _refreshing;

        /// <summary>
        /// Set while the rows are being built or their boxes put right: writing a box's value from
        /// code fires its act exactly as a click would, and a row being filled is not a choice.
        /// </summary>
        private bool _fillingRows;

        // State
        private List<TranslationInfo> _translations = new List<TranslationInfo>();
        private TranslationInfo _selectedTranslation;
        private bool _isSearching;

        // Callbacks
        private Action<TranslationInfo> _onSelectionChanged;
        private Func<string> _getCurrentUser;

        /// <summary>
        /// The panel's help bar, where a row's composition bar spells its own figures out.
        ///
        /// ⚠ The bar itself carries no key on these rows on purpose — five rows each repeating
        /// "Human 12% Validated 3% AI 85%" would be three lines of text per candidate on the one
        /// screen where somebody is comparing candidates. The numbers are one hover away instead,
        /// which is the form this project settled on for the narrow case.
        /// </summary>
        private HelpZone _help;

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
        /// Create a new translation list component.
        /// </summary>
        /// <param name="getCurrentUser">Function to get current logged-in username</param>
        public TranslationList(Func<string> getCurrentUser = null)
        {
            _getCurrentUser = getCurrentUser ?? (() => TranslatorCore.Config.api_user);
        }

        /// <summary>
        /// Build the list in a host — the one the screen's document leaves for it: the status line,
        /// then the scrolling list under it.
        ///
        /// The list takes the space its card has left over, so it is what grows when the window
        /// does and everything around it stays where it was put. This only holds now that
        /// FillViewportHeight raises a floor instead of replacing what the content asks for. Before,
        /// the scrolling area was told it was exactly viewport-sized whatever it held, so a flexible
        /// child swallowed the viewport and pushed the row beneath it somewhere unreachable.
        ///
        /// ⚠ The trough, not a field, behind the rows (the document's `list`). It was
        /// InputBackground — the same value as ItemBackground — so every row was painted the exact
        /// colour of what it sits on and the card disappeared into its own list.
        /// </summary>
        /// <param name="onSelectionChanged">Callback when selection changes</param>
        /// <param name="help">
        /// The panel's help bar, so each row's composition bar can say its own figures on hover.
        /// Optional: without one the bar simply stays silent, as it did before.
        /// </param>
        public void CreateUI(Host parent, Action<TranslationInfo> onSelectionChanged = null, HelpZone help = null)
        {
            _onSelectionChanged = onSelectionChanged;
            _help = help;

            _statusLabel = ScreenBuilder.Part(Part, "Status", parent, NoActs, help).Label("Status");
            _list = ScreenBuilder.Part(Part, "Rows", parent, NoActs, help).List("Rows");
        }

        /// <summary>
        /// The scroll view hosting the list, as a panel holds it. Use it to attach a help
        /// description covering the whole list area (individual rows are generated dynamically and
        /// not described).
        /// </summary>
        public Host Handle => _list?.Handle;

        /// <summary>
        /// Set the status message, in a tone. Written as it is: what the panels say here is
        /// composed with counts and server messages.
        /// </summary>
        public void SetStatus(string message, Tone tone)
        {
            if (_statusLabel == null) return;
            _statusLabel.Show(message);
            _statusLabel.Tone = tone;
        }

        /// <summary>
        /// Set the translations to display.
        /// </summary>
        public void SetTranslations(List<TranslationInfo> translations)
        {
            _translations = translations ?? new List<TranslationInfo>();
            // ⚠ Nothing chosen until the person chooses. The first row used to come ticked, which
            // armed Download on a candidate nobody had looked at — the best-ranked one, which is
            // not the same thing as the one wanted.
            _selectedTranslation = null;

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
        /// Signed in or out: the roles on the rows are about a person, and the person changed. The
        /// library is read again for the new token, then the rows are redrawn with it. Cheap when
        /// nothing changed, which is how often it is called.
        /// </summary>
        public void Refresh()
        {
            string token = TranslatorCore.Config?.api_token;
            bool stale = string.IsNullOrEmpty(token) ? _library != null : (_library == null || _libraryFor != token);
            if (!stale || _refreshing) return;
            _refreshing = true;
            _ = RefreshAsync();
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            try
            {
                await EnsureLibraryAsync();
            }
            finally
            {
                _refreshing = false;
            }
            TranslatorUIManager.RunOnMainThread(() =>
            {
                if (_translations.Count > 0) Populate();
            });
        }

        /// <summary>
        /// The account's library for the current token, read once. Signed out, there is none.
        /// A failed read leaves it unknown: the rows then say nothing about the reader's role,
        /// which is right — "not yours" on the strength of a timeout would be a guess.
        /// </summary>
        private async System.Threading.Tasks.Task EnsureLibraryAsync()
        {
            string token = TranslatorCore.Config?.api_token;
            if (string.IsNullOrEmpty(token))
            {
                _library = null;
                _libraryFor = null;
                return;
            }
            if (_library != null && _libraryFor == token) return;

            var answer = await ApiClient.GetMyTranslations();
            if (!answer.Success)
            {
                TranslatorCore.LogWarning($"[TranslationList] Library not read: {answer.Error}");
                _library = null;
                _libraryFor = null;
                return;
            }

            var index = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var row in answer.Rows)
            {
                if (!string.IsNullOrEmpty(row.FileUuid)) index[row.FileUuid] = row.IsMain;
            }
            _library = index;
            _libraryFor = token;
        }

        /// <summary>What the reader is to a lineage: leads it, contributes to it, or nothing known.</summary>
        private bool? RoleIn(string uuid)
        {
            if (_library == null || string.IsNullOrEmpty(uuid)) return null;
            return _library.TryGetValue(uuid, out bool isMain) ? (bool?)isMain : null;
        }

        /// <summary>
        /// Search for translations by steam ID or game name.
        /// </summary>
        public async System.Threading.Tasks.Task SearchAsync(string steamId, string gameName, string targetLanguage)
        {
            if (_isSearching) return;

            _isSearching = true;
            SetStatus("Searching online...", Tone.Warning);
            Clear();

            try
            {
                // The reader's own library, alongside the search: the rows say what each lineage
                // is to them, and that answer must be there when the rows are drawn.
                var library = EnsureLibraryAsync();
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

                await library;

                // After the awaits we may be on a background thread (IL2CPP). All UI access
                // (SetStatus = the label's text, SetTranslations -> Populate -> Destroy/Create
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
                            SetStatus("No translations found for your language", Tone.Muted);
                        }
                        else
                        {
                            SetStatus($"Found {translations.Count} translation(s):", Tone.Plain);
                            SetTranslations(translations);
                        }
                    }
                    else
                    {
                        SetStatus(capturedResult?.Error ?? "Search failed", Tone.Error);
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    SetStatus($"Error: {errorMsg}", Tone.Error);
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
            _list?.Clear();
            _rows.Clear();
        }

        private void Populate()
        {
            ClearUI();

            // isLoggedIn must be based on api_token, not api_user (api_user persists after logout)
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            string currentUser = isLoggedIn ? _getCurrentUser?.Invoke() : null;

            _fillingRows = true;
            try
            {
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
                    ScreenBuilder.Part(Part, "More", _list.Rows, NoActs, _help)
                        .Say("more", $"Showing the {displayCount} best of {_translations.Count} — refine the search to see others");

                    ShowOwnTranslationBelowTheCut(displayCount, isLoggedIn, currentUser);
                }
            }
            finally
            {
                _fillingRows = false;
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

                ScreenBuilder.Part(Part, "Rank", _list.Rows, NoActs, _help)
                    .Say("rank", TranslatorCore.TranslateOwnUIDynamic("Your current translation ranks") + $" #{i + 1}");

                CreateListItem(_translations[i], isLoggedIn, currentUser);
                return;
            }
        }

        /// <summary>
        /// One row: the document's 'Row', filled from what the server sent about this translation.
        ///
        /// ⚠ Its height is no longer counted here, piece by piece. It used to start from
        /// CodeDisplayHeight — a constant that once covered two lines of text with enough slack to
        /// swallow the padding — and every change to the row since widened the gap between the
        /// count and the content. Each line now states its own floor and the row takes what its
        /// column adds up to, as every other list of the mod does since its rows became templates.
        /// </summary>
        private void CreateListItem(TranslationInfo translation, bool isLoggedIn, string currentUser)
        {
            // Check if this translation is from the same lineage (UUID match)
            bool isLineageMatch = TranslatorCore.IsUuidMatch(translation.FileUuid);

            // 🔴 **The one public way to learn where our own file lives on the site.** `site_id` is
            // written when a translation is downloaded and, until now, nowhere else — so a file
            // somebody wrote and published themselves carried none, and signed out the mod had no
            // id to ask about: check-uuid needs a token, and a published translation was reported
            // as never published to the very person who made it.
            //
            // This row IS that translation — same lineage, answered by a public search — so its id
            // is ours. Recorded once, then saved with the file, and everything downstream (the
            // public update check, the badge strip) works without an account.
            if (isLineageMatch && !TranslatorCore.SourceSiteId.HasValue && translation.Id > 0)
            {
                TranslatorCore.SourceSiteId = translation.Id;
                TranslatorCore.SaveCache();
                TranslatorCore.LogInfo($"[TranslationList] Learned this file is site #{translation.Id}");
            }

            var facts = BuildFactsLine(translation);
            string note = BuildNoteLine(translation);

            // The bar is only drawn when the server gave us something to draw; an empty container
            // under every row would read as "nothing translated" instead of "nothing known".
            bool hasComposition = translation.HumanCount + translation.ValidatedCount +
                translation.AiCount + translation.SkippedCount + translation.CaptureCount > 0;

            // Two doors to one choice: the row pressed anywhere, or its tick box. A box ticked by
            // the person is the choice; one written by code (Populate, RefreshSelection) is not.
            // ⚠ The box is reached after the row exists, so its act reads it through a local the
            // row fills in.
            ToggleHandle select = null;
            var row = ScreenBuilder.Part(Part, "Row", _list.Rows, act =>
            {
                switch (act)
                {
                    case "pick": return () => { if (!_fillingRows) Choose(translation); };
                    case "select": return () => { if (!_fillingRows && select != null && select.IsOn) Choose(translation); };
                    default: return null;
                }
            }, _help);
            select = row.Toggle("Select");
            _rows.Add(new ShownRow { Id = translation.Id, Select = select, Root = row.Root });

            // The player's own translation is marked by a stripe down the left edge rather than
            // by flooding the row with colour. A full purple wash fought every text colour on
            // top of it and made the bar's track read as a black slab; a stripe says the same
            // thing at a glance and leaves the row legible.
            Stacks.Retint(row.Host("Accent"), isLineageMatch ? Surface.Accent : Surface.None);

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
            // 🔴 **Each flag beside the language it names**, on one line: "🇬🇧 English → 🇫🇷 French".
            // The first attempt put the two flags on their own line above the two names, which said
            // everything twice and cost a row.
            var marked = LanguageMark.Create(row.Host("From"), "Source", translation.SourceLanguage,
                                             withName: true) != null;
            if (marked)
            {
                row.Label("Arrow").Visible = true;
                marked = LanguageMark.Create(row.Host("Into"), "Target", translation.TargetLanguage,
                                             withName: true) != null;
            }

            // Falls back to the plain sentence when a side could not be marked at all — a half-built
            // pair would name one language and leave the other missing.
            if (!marked)
            {
                row.Say("title", languages);
                row.Label("Title").Visible = true;
            }

            // What this row is to the reader, in the socle's chips: the one this game holds, and
            // whether they lead the lineage or contribute to it. Silent when neither is known.
            var marks = Badges.InListing(isLineageMatch, RoleIn(translation.FileUuid));
            if (marks.Count > 0) BadgeStrip.Create(row.Host("Marks"), "Marks", marks, 220f);

            // ⚠ One form for the whole ecosystem, composed in `common`: "@name", and "@name (you)"
            // on your own. The mark is a WORD and not a colour — this row already spends colour on
            // the lineage stripe, and a second meaning on the same channel reads as neither.
            string by = "by " + People.Mention(translation.Uploader, isOwnTranslation);
            // The two things about a translation that catch the eye and are written nowhere else
            // on its row. Deliberately only two: a badge works by being rare, and the line count,
            // the review stage and the download count are already there in plain words.
            if (translation.IsNewAt(DateTime.UtcNow)) by += "  ·  " + TranslatorCore.TranslateOwnUIDynamic("new");
            if (IsFurthest(translation)) by += "  ·  " + TranslatorCore.TranslateOwnUIDynamic("goes furthest");
            // "installed" used to be a third word here; it is the Installed chip on the first line
            // now, beside the reader's role, where the eye lands before it reads the author.

            // 🔴 **Never the accent on the author line.** It was ButtonPrimary — purple-600, a FILL
            // colour used as text — which scores 1.86 against this row and is simply unreadable.
            // The palette says as much: 600 fills, 400 writes. And even purple-400 only manages
            // 3.69 on a raised row, because a row is LIGHTER than the card it sits on: accent text
            // belongs on the card, not on the row.
            //
            // ⚠ Nothing is lost. The row already carries a purple stripe for the lineage it
            // matches, and the line spells "installed" out in words. The colour was a third way of
            // saying the same thing, and the only one that cost legibility.
            row.Say("author", by);

            // 🔴 What this translation IS, in the socle's chips — the strip the Manager's
            // community list draws (TranslationBadges.ForOnline) and the card above draws for
            // the one file held: the stage or "Capture only", the share translated, the author's
            // "finished", whether it takes contributions, its downloads, and whose work it started
            // from. These were three lines of prose here — a stage in the details, "finished ·
            // accepts contributions · N downloads" in the facts, a "Forked from" line of its own —
            // and read as nothing to find one's way by (2026-09-18).
            //
            // ⚠ Two of the socle's chips are left out, on purpose. The publication: every row of
            // this list is on the site, and which one is held is the Installed mark on the first
            // line — "Not downloaded" on eight rows out of nine would say nothing. The votes: the
            // column on the right is theirs.
            var chips = new List<Badge>();
            foreach (var badge in Badges.For(
                         Publications.Of(hereOnDisk: isLineageMatch, onTheSite: true),
                         isMain: null, branchesWaiting: null, mainMissing: false, sync: null,
                         stage: Quality.Stage(translation.HumanCount, translation.ValidatedCount,
                                              translation.SkippedCount, translation.AiCount, translation.CaptureCount),
                         completeness: Quality.Completeness(translation.HumanCount, translation.ValidatedCount,
                                                            translation.SkippedCount, translation.AiCount, translation.CaptureCount),
                         votes: translation.VoteCount,
                         downloads: translation.DownloadCount,
                         finished: string.IsNullOrEmpty(translation.Status)
                             ? (bool?)null
                             : string.Equals(translation.Status, "complete", StringComparison.OrdinalIgnoreCase),
                         acceptsContributions: translation.AcceptsBranches,
                         origin: translation.Origin,
                         captureOnly: Quality.IsCaptureOnly(translation.HumanCount, translation.ValidatedCount,
                                                            translation.SkippedCount, translation.AiCount, translation.CaptureCount)))
            {
                if (badge.Kind == BadgeKind.Publication || badge.Kind == BadgeKind.Votes) continue;
                chips.Add(badge);
            }
            if (chips.Count > 0)
            {
                var chipHost = row.Host("Badges");
                BadgeStrip.Create(chipHost, "Badges", chips, BadgeStrip.WidthOf(chipHost, 360f));
            }

            // The size, and how much of the game it reaches: what the chips above do not say.
            row.Say("details", Unbreakable($"{translation.LineCount} lines") + FormatCoverage(translation));

            // Same component, same colours and same denominator as the card and the website.
            if (hasComposition)
            {
                var composition = row.Host("Composition");
                var bar = new QualityBar();
                bar.CreateUI(composition, QualityBar.CompactHeight);
                bar.SetCounts(translation.HumanCount, translation.ValidatedCount,
                    translation.AiCount, translation.SkippedCount, translation.CaptureCount);
                composition.Visible = true;

                // The figures the bar draws, in words, on hover — the narrow form of the same
                // composition the card spells out under itself. Without this the coloured band is
                // a decoration: five bands side by side rank the candidates and name none of them.
                //
                // ⚠ composed: BuildLegend already translated each word and welded colour tags
                // around them. The card showing the identical string is RegisterExcluded for the
                // same reason — translating it again would hand markup to the AI.
                _help?.Describe(bar.Handle, QualityBar.BuildLegend(
                    translation.HumanCount, translation.ValidatedCount,
                    translation.AiCount, translation.SkippedCount, translation.CaptureCount),
                    composed: true);
            }

            // Second details row: is it alive, is it finished, is it used, does
            // it need anything. All of it was already received and shown nowhere.
            if (facts != null)
            {
                row.Say("facts", facts);
                row.Label("Facts").Visible = true;
            }

            if (note != null)
            {
                row.Say("note", note);
                row.Label("Note").Visible = true;
            }

            // Vote COUNT (right side), and no arrows.
            //
            // This is a list of candidates: one is choosing between translations one has never
            // run, and a vote cast here would rate a title card. Seeing how others rated it is
            // exactly what helps you choose — casting your own belongs on the current
            // translation, once you have played with it.
            new VoteButtons().Create(row.Host("Votes"), translation.Id, translation.VoteCount, null,
                translation.UserVote, interactive: false);
        }

        /// <summary>
        /// "12 Mar 2026 · complete · 87 downloads · Kept as is: 312 · ◆ Resources",
        /// or null when the server told us none of it (older servers send no content date).
        /// </summary>
        private static string BuildFactsLine(TranslationInfo translation)
        {
            var facts = new List<string>();

            string dateLabel = translation.ContentDateLabel(TimeZoneInfo.Local);
            if (!string.IsNullOrEmpty(dateLabel)) facts.Add(dateLabel);

            // ⚠ "finished", "accepts contributions" and the downloads are CHIPS now, on the row's
            // own strip, said as the card and the Manager say them; what stays here is what no
            // chip carries.
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

        /// <summary>The person chose a row: remember it, show it, tell the panel.</summary>
        private void Choose(TranslationInfo translation)
        {
            _selectedTranslation = translation;
            RefreshSelection();
            _onSelectionChanged?.Invoke(translation);
        }

        /// <summary>
        /// Put every row in step with the choice: one ticked and lit, the others not. Written by
        /// code, so the box's act stays quiet (see _fillingRows). The tint is the one every chosen
        /// row of this mod wears — a tick alone was too small a mark to find the chosen candidate.
        /// </summary>
        private void RefreshSelection()
        {
            _fillingRows = true;
            try
            {
                foreach (var row in _rows)
                {
                    bool chosen = _selectedTranslation != null && _selectedTranslation.Id == row.Id;
                    row.Select.IsOn = chosen;
                    Stacks.Highlight(row.Root, chosen);
                }
            }
            finally
            {
                _fillingRows = false;
            }
        }
    }
}
