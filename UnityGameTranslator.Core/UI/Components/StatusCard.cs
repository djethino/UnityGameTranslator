using System;
using System.Collections.Generic;
using UnityEngine;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    // 🔴 SyncStatusType and TranslationRoleType lived here and are gone. Between them they
    // answered three independent questions — up to date, published, under whose name — as one
    // list, so "LocalOnly" and "NotLoggedIn" sat beside "Synced" as if they were alternatives to
    // it. Replaced by Standing in the socle, which keeps the four apart and which the manager
    // reads too. See CLAUDE.md, "What each product is FOR".


    /// <summary>
    /// Local translation quality statistics.
    /// </summary>
    public class LocalQualityStats
    {
        public int HumanCount { get; set; }
        public int ValidatedCount { get; set; }
        public int AiCount { get; set; }
        public int CaptureCount { get; set; }
        /// <summary>Entries the author marked as not to translate (tag S).</summary>
        public int SkippedCount { get; set; }
        public int TotalLines { get; set; }

        // The measures live in TranslationQuality so the card, the community list and the
        // website all read a translation the same way.

        /// <summary>
        /// Where this translation stands, as a step. Null when nothing is translated, and null
        /// while most of what the mod has captured is still waiting to be translated — writing
        /// comes before reading, and the completeness below is what the author needs then.
        /// </summary>
        public string ReviewStage => TranslationQuality.ReviewStage(
            HumanCount, ValidatedCount, SkippedCount, AiCount, CaptureCount);

        /// <summary>How much of it a human has read, 0 to 1. Negative when nothing is translated.</summary>
        public float ReviewCoverage => TranslationQuality.ReviewCoverage(HumanCount, ValidatedCount, SkippedCount, AiCount);

        /// <summary>
        /// How much of what the mod has met in game is translated, 0 to 1. Negative when the file
        /// is empty. Captured lines are the work already identified — the honest denominator.
        /// </summary>
        public float Completeness => TranslationQuality.Completeness(
            HumanCount, ValidatedCount, SkippedCount, AiCount, CaptureCount);

        /// <summary>Translated lines nobody has read yet — what is left to do, not a mark.</summary>
        public int UnreviewedCount => AiCount;
    }

    /// <summary>
    /// The status card: what the translation this game holds IS — its languages, the chips of its
    /// standing, how much of it there is and how good, the one thing its mode has to say, and the
    /// vote row.
    ///
    /// ⚠ **Its shape is a document** (common/spec/screens/parts/status-card.json, a PART: templates
    /// alone, shared by whichever screen leaves a host for it). This builds the one template into
    /// that host and then decides, from the standing, what each line says and which rows show —
    /// the rules stay here and in the socle (StatusCards), never in the document.
    /// </summary>
    public class StatusCard
    {
        /// <summary>The part, read once: the card's shape, in the closed vocabulary.</summary>
        private static readonly ScreenDocument Part = ScreenDocument.FromEmbedded("parts/status-card");

        private BuiltScreen _card;
        private LabelHandle _identityLabel;

        /// <summary>Holds the two flags in front of the language pair. Rebuilt with the pair.</summary>
        private Host _identityMarks;
        private Host _badgeHost;

        /// <summary>
        /// How much room the chips have. The card sits in a 450-wide panel with padding either
        /// side; a generous under-estimate simply breaks a line early, where an over-estimate would
        /// push a chip off the edge.
        /// </summary>
        /// <summary>
        /// What the strip wraps in before the host has been laid out: the card's width at the
        /// window's default size. The measured width replaces it from the first layout on
        /// (<see cref="Reflow"/>), so it only ever decides the first frame.
        /// </summary>
        private const float _stripWidthBeforeLayout = 380f;

        /// <summary>Whether a standing has been shown yet — what <see cref="Reflow"/> has to deal again.</summary>
        private bool _standingSet;
        private LabelHandle _detailsLabel;
        private Host _qualityRow;
        private Host _stageRow;
        private Host _legendRow;
        private LabelHandle _qualityLabel;
        private QualityBar _qualityBar;
        private LabelHandle _qualityLegend;
        private Host _emptyRow;
        private LabelHandle _emptyLabel;
        private ButtonHandle _emptyBtn;
        private ButtonHandle _dismissBtn;
        private Host _modeRow;
        /// <summary>Where the tag chips of what a contribution holds are drawn.</summary>
        private Host _contributionRow;
        private Host _voteRow;
        private Host _voteHost;
        private LabelHandle _voteHint;
        private VoteButtons _voteButtons;
        private int _voteBuiltForId = -1;
        private bool _voteBuiltInteractive;
        private LabelHandle _secondaryLabel;

        /// <summary>The card, as a panel holds it.</summary>
        public Host Handle => _card?.Root;

        /// <summary>
        /// Build the card in a host — the one the screen's document leaves for it.
        ///
        /// ⚠ **A SECTION, not a card, because of where it sits.** This lands inside the "My
        /// translation" card, between boxes built as sections — and it was dressing itself as a
        /// top-level card: CardPadding against their SectionPadding, and CardBackground against
        /// their transparent one. Same outer width, so the frame lined up while its contents started
        /// eight pixels further in and on a different shade — which reads as a box of the wrong
        /// width stacked among the others. The document says so: SectionPadding, no surface.
        /// </summary>
        public void CreateUI(Host parent)
        {
            _card = ScreenBuilder.Part(Part, "Card", parent, ActOf);

            // Row 1 — WHAT this translation is, plus the role badge. Identity leads: you know what
            // you are looking at before you are told how it is doing.
            //
            // The flags lead, the names follow. ⚠ Both: a flag is found faster in a glance and
            // cannot always name a language on its own — ten Indian languages share one — so the
            // words stay and the pictures are added in front. Rebuilt by SetIdentity, since the
            // pair changes when a different translation is taken.
            _identityMarks = _card.Host("IdentityMarks");

            // ⚠ Kept for the states that have no pair to show — "Auto", or a language we do not
            // recognise. It is EMPTY whenever the marks carry the names, because "🇬🇧 English →
            // 🇫🇷 French  English → French" is the same sentence twice.
            _identityLabel = _card.Label("IdentityLabel");

            // ⚠ The role chip used to live here, alone and in its own colours. It moved into the
            // badge strip below, where it sits beside the other things it has to be read WITH —
            // being a Branch means something different depending on whether you are up to date.

            // Row 2 — what this translation IS, in chips, then how much of it there is.
            //
            // 🔴 **Replaces a coloured dot with one word beside it, and a role chip up in row 1.**
            // Those two answered three questions between them — up to date, published, whose — in a
            // vocabulary this product used nowhere else. The chips come from the socle, so a player
            // reads the same words here, in the manager and on the website.
            //
            // ⚠ The card keeps its quality bar and its vote row, so the chips ABOUT those are
            // dropped rather than shown twice: BadgeKind is what makes that a selection instead of
            // a second opinion.
            _badgeHost = _card.Host("BadgeHost");

            // Volume + game
            _detailsLabel = _card.Label("DetailsLabel");

            // Row 3 — quality bar, FULL WIDTH. It used to share a row with the score label, which
            // shortened it and made the proportions harder to read; the score moved to the legend.
            _qualityRow = _card.Host("QualityRow");

            // Shared with the community list and matching the website's bar — see QualityBar.
            _qualityBar = new QualityBar();
            _qualityBar.CreateUI(_qualityRow, QualityBar.DefaultHeight);

            // Row 4 — where the review stands: its own line, and RANGED RIGHT.
            //
            // It used to sit at the right end of the key, which worked while it read "2.5/3" and
            // fitted in 90px. As a sentence it needs 220, and on a card barely 340 wide that left
            // the key half a row — its two lines wrapped into four. A line of its own removes the
            // competition for width; kept to the right so the block does not stack up flush left,
            // and so the verdict still reads as the summing-up of the bar above it.
            _stageRow = _card.Host("StageRow");
            _qualityLabel = _card.Label("QualityLabel");

            // Row 5 — the colour key with the PERCENTAGES (asked for: the bar shows proportions,
            // the key says what they are worth). Full width, with nothing beside it.
            //
            // Top-aligned: the key takes as many lines as the current width leaves it (see
            // QualityBar.BuildLegend), and centring would float them inside the row. Wrapping, and
            // no minHeight of its own: the label announces the height its wrapped text needs at the
            // width it is given, the row inherits it, and a resize re-lays it out without anyone
            // recomputing anything.
            _legendRow = _card.Host("LegendRow");
            _qualityLegend = _card.Label("QualityLegend");

            // Row 5b — published, and translating nothing.
            //
            // The website says this on "my translations", where an author goes once. Here it is
            // in front of them while they play the very game concerned, which is the moment they
            // can do something about it — and the button leads straight to the row that carries
            // the delete, because uploading takes one click and unpublishing is a page nobody
            // thinks to look for.
            _emptyRow = _card.Host("EmptyRow");
            _emptyLabel = _card.Label("EmptyLabel");
            _emptyBtn = _card.Button("EmptyBtn");

            // Only on the notice that is a judgement about somebody else: an empty file of one's
            // own is a fact that comes back the moment a line is written, and hiding it would
            // only hide it from the person who can fix it.
            _dismissBtn = _card.Button("DismissBtn");

            // Row 5 — the ONE thing this mode has to tell you.
            //
            // ⚠ It carried a button too until 2026-08-19, and that was a mistake: each of the three
            // it could show (Review, Compare, Upload) already existed in "Actions" WITH the
            // conditions this row had no way to express — signed in, online, anything left to send.
            // A card that describes must not offer a second door to an action, least of all one
            // that skips the lock.
            _modeRow = _card.Host("ModeRow");
            _secondaryLabel = _card.Label("SecondaryLabel");

            // 🔴 What a contribution is HOLDING, in the marks the website uses for it.
            //
            // The socle composes "21 to review: 12 new (H 9, A 3)"; the letters arrived here as
            // grey prose while the same four letters are coloured squares on every table of the
            // site. They are what says whether the evening is worth it — nine lines written by
            // hand is not the proposition nine machine lines are — so they are drawn, not spelt.
            //
            // 🔴 **UNDER the sentence, and one line per kind — it was beside it, and unreadable.**
            // A horizontal group hands out its width to everything it holds, so a long sentence and
            // six chips shared one row of 350 pixels: every child was crushed to whatever was left,
            // words broke mid-syllable ("24 ne w", "35 diffe ring") and the counts stacked one digit
            // per line. What made it certain rather than unlucky is that this row grows with the
            // work — more qualities, more chips — so the one arrangement that cannot hold them is
            // the one that puts them all on a single line beside a sentence.
            //
            // ⚠ A kind and its letters still belong together: that is what the per-kind row keeps,
            // and it is why this is a COLUMN of rows rather than one row that wraps.
            _contributionRow = _card.Host("Contributions");

            // Row 6 — giving something back. Last, because it is not status: it is the one thing
            // the player can do FOR the translation rather than with it.
            //
            // 🔴 **The row says what it is.** It showed a bare "+1" beside a sentence, and a lone
            // signed number names nothing: it could be a score, a difference, lines added. Every
            // other row of this card carries the word for its subject, and this one did not.
            //
            // ⚠ "Votes", the website's word (games.sort.votes, admin.votes), not "rating": the
            // same fact must read the same way in the mod and on the site, and the site counts
            // votes. See CLAUDE.md — it is one ecosystem. Written once in the document and never
            // rewritten by the code, so it goes through the translation pipeline like the card's
            // other fixed words — unlike the labels above, whose text the code replaces on every
            // refresh and which are therefore excluded from it.
            _voteRow = _card.Host("VoteRow");

            // The widget is rebuilt into this host whenever the mode changes (signed in, seen
            // enough of it, someone else's work) — arrows exist or they don't, they are never
            // shown greyed out.
            _voteHost = _card.Host("VoteHost");
            _voteHint = _card.Label("VoteHint");
        }

        /// <summary>The two verbs the document asks for.</summary>
        private Action ActOf(string act)
        {
            switch (act)
            {
                case "manage":
                    return () =>
                    {
                        var state = TranslatorCore.ServerState;
                        TranslatorCore.OpenUrlSafe(ApiClient.GetMyTranslationsUrl(state?.SiteId));
                    };
                case "dismiss": return DismissCurrentNotice;
                default: return null;
            }
        }

        /// <summary>
        /// Show what the community made of this translation, and let the player have their say.
        ///
        /// This card is the ONE place in the mod where a vote is cast, and the reason is that it
        /// is the only place where the player has actually run the translation. The community
        /// list shows counts and no arrows: there one is picking between candidates never played,
        /// and a vote cast on a title card measures nothing.
        ///
        /// Hidden entirely when the server said nothing about votes — an older site, or a
        /// translation with nothing published. Absence is not "0 votes".
        /// </summary>
        public void SetVote(VoteState vote, LineageRole role)
        {
            if (_voteRow == null) return;

            if (vote == null)
            {
                _voteRow.Visible = false;
                return;
            }

            bool signedIn = !string.IsNullOrEmpty(TranslatorCore.Config?.api_token);
            bool playedEnough = TranslatorCore.HasUsedTranslationEnoughToRate;

            // 🔴 The socle decides who may vote and why not — the same four refusals, in the same
            // order, as the Manager. This card used to redo the rule with its own words; the one
            // thing it still adds is the server's own veto, kept as a last guard because the server
            // may know a reason this machine does not.
            var block = Voting.Rating(signedIn, published: true,
                                      isYourOwn: role == LineageRole.Main,
                                      hasUsedIt: playedEnough);
            bool interactive = block == RateBlock.None && vote.CanVote;

            // Rebuilt rather than toggled: a greyed-out arrow is a dead end, and the reason it
            // is dead belongs in words next to it.
            if (_voteButtons == null || _voteBuiltForId != vote.TargetId || _voteBuiltInteractive != interactive)
            {
                _voteHost.Clear();
                _voteButtons = new VoteButtons();
                _voteButtons.Create(_voteHost, vote.TargetId, vote.Count, OnVoteCast, vote.UserVote, interactive);
                _voteBuiltForId = vote.TargetId;
                _voteBuiltInteractive = interactive;
            }
            else
            {
                _voteButtons.UpdateVoteCount(vote.Count, vote.UserVote);
            }

            // The refusal in the socle's words ("vote", the website's verb, everywhere since
            // 2026-09-07); the invitation when there is none. Nothing when the server alone vetoed:
            // there is no sentence for a reason it did not give.
            string hint;
            if (interactive)
                hint = "Vote on this translation";
            else if (block != RateBlock.None)
                hint = Voting.Explain(block);
            else
                hint = null;

            if (hint == null) _voteHint.Show(string.Empty);
            else _voteHint.Say(hint);
            _voteHint.Visible = hint != null;

            _voteRow.Visible = true;
        }

        /// <summary>
        /// A vote was just cast from this card: write it back into the state the card is
        /// rebuilt from.
        ///
        /// Without this the next refresh — and the panel refreshes often — would hand the
        /// widget the server's answer from BEFORE the vote and visually undo it, until the
        /// next sync check happened to come round.
        /// </summary>
        private static void OnVoteCast(int translationId, int newCount, int? userVote)
        {
            var vote = TranslatorCore.ServerState?.Vote;
            if (vote == null || vote.TargetId != translationId) return;

            vote.Count = newCount;
            vote.UserVote = userVote;
        }

        /// <summary>
        /// What this translation IS, in the chips the socle decides.
        ///
        /// 🔴 **Replaces SetStatus and SetRole.** Those answered three questions between them — up
        /// to date, published, whose — in an enum that mixed them: "LocalOnly" was about publishing
        /// and "NotLoggedIn" about an account, neither about being in sync, and "OutOfSync" could
        /// not say WHICH side had moved. Standing keeps the four apart and Badges words them the
        /// same way in all three products.
        ///
        /// ⚠ The chips this card already answers another way are dropped, not repeated: the
        /// quality bar carries the review stage and the completeness, the vote row carries the
        /// votes. Showing them twice would spend attention on something already on screen.
        /// </summary>
        /// <summary>The standing last shown — what the notice under the lines is read from.</summary>
        private Standing _standing;

        public void SetStanding(Standing standing)
        {
            _standing = standing;
            _standingSet = true;
            if (_badgeHost == null) return;

            _badgeHost.Clear();

            // What the file is made of, measured here: the socle's chips for it are the
            // Manager's and the site's, and this card dropped them (see the filter below).
            var stats = CalculateLocalStats();
            bool captureOnly = TranslationQuality.IsCaptureOnly(stats.HumanCount, stats.ValidatedCount,
                                                                stats.SkippedCount, stats.AiCount, stats.CaptureCount);
            var stage = Quality.Stage(stats.HumanCount, stats.ValidatedCount, stats.SkippedCount,
                                      stats.AiCount, stats.CaptureCount);
            var completeness = Quality.Completeness(stats.HumanCount, stats.ValidatedCount, stats.SkippedCount,
                                                    stats.AiCount, stats.CaptureCount);

            var all = Badges.For(standing.Publication, standing.Role == LineageRole.Main ? true
                                     : standing.Role == LineageRole.Branch ? (bool?)false : null,
                                 standing.BranchesWaiting,

                                 // 🔴 **From the standing, which now carries it.** This used to read
                                 // ServerState directly because the Standing the main screen built
                                 // never filled MainMissing — the chip for a vanished Main never
                                 // once appeared in a game while the notice below, reading the
                                 // server state, did. Standings.From fills every field or none,
                                 // so the card reads one source, and the corpus holds the case.
                                 standing.MainMissing,

                                 standing.Sync,
                                 stage, completeness,
                                 // The votes have a row of their own on this card, so their chip
                                 // is filtered below whatever the count; the downloads have none.
                                 // Unknown (an older site) is passed as none, which is not shown.
                                 0, TranslatorCore.ServerState?.DownloadCount ?? 0,
                                 linesAvailable: standing.LinesAvailable,

                                 // The other way a lineage loses its head: the Main is still there
                                 // and its owner is not. Ignored by Badges when MainMissing is set
                                 // — a Main that is gone is the whole story.
                                 mainAbandoned: standing.MainAbandoned,

                                 // The third way this road ends, and the only one of the three that
                                 // had no chip. The notice below carries all three; the strip
                                 // carried two, so the same fact read differently depending on
                                 // where the eye landed — and differently again from the site and
                                 // the Manager, which both show it.
                                 branchFrozen: standing.BranchFrozen,

                                 // ⚠ The author's own word, which nothing else on this card says.
                                 // Without it somebody cannot tell whether they still have to open
                                 // Edit details and declare it — the measurements beside it answer
                                 // a different question.
                                 finished: standing.Finished,

                                 // The Main's other declaration, and the one a would-be
                                 // contributor needs before writing anything. Null on a server
                                 // that never sent it — unknown is not "solo work".
                                 acceptsContributions: TranslatorCore.ServerState?.AcceptsBranches,

                                 // Where this row came from, when it is a fork: the credit the
                                 // site's page and the community list already give, and this card
                                 // gave nowhere — the same file read "Forked from @x" in a browser
                                 // and a bare "Main" in the game (2026-09-17).
                                 //
                                 // ⚠ **The engine composes it, from the server's answer OR from the
                                 // file's own `_forked_from`** (2026-09-20). Reading only the server
                                 // meant the credit appeared at the moment the fork was published,
                                 // i.e. when the site was already showing it — and a fork living in
                                 // a game folder, which is every fork until somebody publishes it,
                                 // said nothing about where it came from at all.
                                 origin: TranslatorCore.ForkOrigin,

                                 // Nothing translated: the socle says it as one chip, in place of
                                 // a stage with nothing to judge and a "0% translated".
                                 captureOnly: captureOnly,
                                 // Named in the "Not yours" sentence, so somebody holding a
                                 // community translation is told WHOSE it is and that publishing
                                 // sends them a contribution rather than creating anything.
                                 mainOwner: standing.MainOwner);

            // 🔴 Only the votes are dropped: they have a row of their own on this card, and a
            // chip beside it would say one fact twice. The stage, the completeness and the
            // downloads used to be dropped too, on the reasoning that the quality bar answered
            // them — it answers the COUNTS; the stage and "Capture only" are the chips the site
            // and the Manager show, and the mod was the one product without them (2026-09-18).
            // The row under the bar keeps what the chips do not say: what is left to read.
            var shown = new List<Badge>();
            foreach (var badge in all)
            {
                if (badge.Kind == BadgeKind.Votes) continue;
                shown.Add(badge);
            }

            BadgeStrip.Create(_badgeHost, "Badges", shown, StripWidth());
        }

        /// <summary>
        /// The width the chips wrap in: the host's, once it has been laid out. Zero before the
        /// first layout, when the window's default stands in.
        /// </summary>
        private float StripWidth() => BadgeStrip.WidthOf(_badgeHost, _stripWidthBeforeLayout);

        /// <summary>
        /// The strip dealt again within the width the host has NOW — asked by the panel after
        /// every layout and every resize, since which chips fit on a row is a fact about the
        /// width and the chips are built long before the card is measured.
        /// </summary>
        public void Reflow()
        {
            if (!_standingSet) return;
            SetStanding(_standing);
        }

        /// <summary>
        /// Set the identity line: which languages this translation goes between. Language names
        /// are data — shown as-is, never translated.
        /// </summary>
        public void SetIdentity(string sourceLanguage, string targetLanguage)
        {
            if (_identityLabel == null) return;

            // An empty source means auto-detection is on, not a missing value — saying "?" reads as
            // data we failed to record, and sent the user hunting for an upload that skipped it.
            string source = string.IsNullOrEmpty(sourceLanguage)
                ? TranslatorCore.TranslateOwnUIDynamic("Auto")
                : sourceLanguage;
            string target = string.IsNullOrEmpty(targetLanguage)
                ? TranslatorCore.TranslateOwnUIDynamic("Auto")
                : targetLanguage;
            // The pair is drawn as marks, each side carrying its flag when it has one. The text
            // label is only the standby for when there is no row to draw them in at all.
            bool marked = RebuildIdentityMarks(sourceLanguage, targetLanguage);
            _identityLabel.Show(marked ? "" : $"{source} → {target}");
        }

        /// <summary>
        /// The two flags in front of the pair.
        ///
        /// ⚠ Torn down and rebuilt rather than recoloured: the pair changes when a different
        /// translation is taken, and a mark left over from the previous one would name a language
        /// this card is no longer about.
        /// </summary>
        /// <returns>True when the marks name both sides, so the text label has nothing to add.</returns>
        private bool RebuildIdentityMarks(string sourceLanguage, string targetLanguage)
        {
            if (_identityMarks == null) return false;

            _identityMarks.Clear();

            AddIdentitySide("IdSource", sourceLanguage);

            Labels.Create(_identityMarks, "IdArrow", "→", TextRole.Body, tone: Tone.Muted,
                          policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);

            AddIdentitySide("IdTarget", targetLanguage);
            return true;
        }

        /// <summary>
        /// One side of the pair: its flag and name when it has a language, the word "Auto" when it
        /// does not.
        ///
        /// 🔴 **Each side stands on its own.** This used to give up on the WHOLE pair as soon as one
        /// side could not be marked, which fell back to a plain "Auto → French" — so a target lost
        /// its flag because the SOURCE was auto-detected. A language does not stop having a flag
        /// because of what it is being translated from.
        /// </summary>
        private void AddIdentitySide(string name, string language)
        {
            if (LanguageMark.Create(_identityMarks, name, language, withName: true) != null) return;

            // Nothing to mark: no language was chosen, which is auto-detection rather than a value
            // we failed to record. The word takes the mark's place so the row still reads as a pair.
            Labels.Create(_identityMarks, name + "Auto", TranslatorCore.TranslateOwnUIDynamic("Auto"),
                          TextRole.Body, tone: Tone.Plain, policy: TextPolicy.Excluded, wrap: false,
                          minHeight: UIStyles.RowHeightSmall);
        }

        /// <summary>
        /// Update the details display (entry count, language, game).
        /// </summary>
        public void SetDetails(int entryCount, string targetLanguage, string gameName = null)
        {
            if (_detailsLabel == null) return;

            // Sits right after the status on the same line, so it reads as one sentence:
            // "● SYNCED · 1 248 entries". The language moved up to the identity row, so it is not
            // repeated here. Counts and game names are data — concatenated, never translated.
            // The kept-as-is count is NOT repeated here: the bar's legend already names it, with
            // its colour and its share.
            string details = "· " + TranslatorCore.TranslateOwnUIDynamic($"{entryCount} entries");
            if (!string.IsNullOrEmpty(gameName))
                details += $" · {gameName}";
            _detailsLabel.Show(details);
        }

        /// <summary>
        /// The one thing this mode has to say — branches waiting for a Main, whose lineage this is
        /// for a Branch, "not shared yet" for a local file. Information only: what to DO about it
        /// belongs to "Actions", which is the one place that knows whether it is possible.
        ///
        /// 🔴 **A username goes in <paramref name="mention"/>, never inside <paramref name="info"/>.**
        /// The pipeline turns NUMBERS into placeholders, so every count shares one cache entry — but
        /// nothing else. A name written into the sentence fills the cache with one entry per person
        /// and sends each of them off to be translated. Two of these calls did exactly that until
        /// 2026-08-20.
        ///
        /// ⚠ And when a mention is appended, the label is NOT handed to the translator: on a cache
        /// miss the worker writes its result straight into the component it was given, which would
        /// replace the whole line with the translated fragment alone — dropping the name.
        /// </summary>
        /// <param name="needsAttention">
        /// 🔴 **Whether this line is work waiting, or merely a fact.** It was always muted grey —
        /// the colour this card uses for "nothing to do here" — so "2 contributions you have not
        /// been through, holding 38 lines to take" read like a footnote and slid past the eye. It
        /// is the only sentence on the card asking for something.
        ///
        /// ⚠ Warning, not success: the Manager showed the same sentence in green, which reads as
        /// "all good, nothing to do" — the opposite of what it says. Green is where this ends up
        /// once the contributions have been gone through, not while they wait.
        /// </param>
        public void SetSecondaryInfo(string info, string mention = null, bool needsAttention = false)
        {
            if (_modeRow == null) return;

            bool hasInfo = !string.IsNullOrEmpty(info);
            if (hasInfo)
            {
                if (string.IsNullOrEmpty(mention)) _secondaryLabel.Say(info);
                else _secondaryLabel.Show(TranslatorCore.TranslateOwnUIDynamic(info) + " " + mention);

                _secondaryLabel.Tone = needsAttention ? Tone.Warning : Tone.Muted;
            }

            _secondaryLabel.Visible = hasInfo;
            _modeRow.Visible = hasInfo;

            // 🔴 **The kinds belong to THIS sentence, so they go when it is rewritten.** They used
            // to be a child of the row above and vanished with it; on their own they would outlive
            // the sentence that gives them their subject — a card switched from Main to Branch would
            // still show what somebody else's contributions were holding. Every caller that has
            // kinds to draw calls SetContributionKinds straight after this, so clearing here costs
            // nothing and makes the stale case impossible rather than unlikely.
            if (_contributionRow != null) _contributionRow.Visible = false;
        }

        /// <summary>
        /// Draw what the contributions are holding: the group, then a chip per quality.
        ///
        /// ⚠ The pieces come from the socle (<see cref="Contributions.KindsOfWork"/>), which also
        /// composes the printed sentence from them. Neither the order nor which zeros are left out
        /// is decided here — this only chooses how a piece looks.
        ///
        /// ⚠ Rebuilt whole on each call rather than patched: the set of qualities changes with the
        /// contributions, and a row that kept a chip nobody counted any more would report work
        /// that is no longer offered.
        /// </summary>
        public void SetContributionKinds(string head, WorkKind[] kinds)
        {
            if (_contributionRow == null) return;

            _contributionRow.Clear();

            bool any = kinds != null && kinds.Length > 0;
            _contributionRow.Visible = any;
            if (!any) return;

            // 🔴 **One line, opening with "N to review:" — the shape the Manager already has.** It
            // was appended to the sentence above with an em dash ("…lines to take. — 59 to review:")
            // and the dash joined two facts that answer different questions: how much work is
            // waiting, and what that work is made of. The second belongs with the pieces that detail
            // it, which is where the eye goes when deciding whether the evening is worth it.
            //
            // ⚠ Built here and not described: how many pieces the line holds is the work's, so the
            // row is the one thing of this card the document cannot draw ahead of time.
            var row = Stacks.Horizontal(_contributionRow, "Kinds", spacing: 4, pad: Pad.None,
                                        minHeight: UIStyles.RowHeightSmall);

            if (!string.IsNullOrEmpty(head)) Piece(row, "Head", head + ":");

            for (int k = 0; k < kinds.Length; k++)
            {
                // The separator the socle's sentence uses between groups, so the two read alike.
                Piece(row, "Kind" + k, (k > 0 ? "· " : "") + kinds[k].Total + " " + kinds[k].Label);

                foreach (TagCount piece in kinds[k].Tally.Counted())
                {
                    TagChips.Create(row, piece.Letter);
                    Piece(row, "Count" + piece.Letter, piece.Count.ToString());
                }
            }
        }

        /// <summary>
        /// One word of that line.
        ///
        /// ⚠ **White, not the muted grey and not the warning amber.** The sentence above is the one
        /// asking for something and keeps the amber; this line ANSWERS "what is in it", and a fact
        /// read next to a call to action must not compete with it — nor look like a footnote.
        ///
        /// ⚠ A minimum width from the label's own measurement: without one the group is free to
        /// crush a label to nothing when the row is tight, and a Text given no width does not clip,
        /// it wraps — one syllable per line, which is exactly what this row used to do.
        /// </summary>
        private static void Piece(Host row, string name, string text)
        {
            var label = Labels.Create(row, name, text, TextRole.Small, tone: Tone.Plain,
                                      policy: TextPolicy.Excluded, minHeight: UIStyles.RowHeightSmall);
            label.FitWords();
        }

        /// <summary>
        /// Update the quality stats display with H/V/A bar.
        /// </summary>
        public void SetQualityStats(LocalQualityStats stats)
        {
            if (_qualityRow == null) return;

            if (stats == null)
            {
                _qualityRow.Visible = false;
                _stageRow.Visible = false;
                _legendRow.Visible = false;
                _emptyRow.Visible = false;
                return;
            }

            RefreshEmptyWarning(stats);

            // Captures are part of the picture: a file made of 900 captured lines and 100
            // translated ones has to look like it. Hiding the grey flattered the result.
            bool hasData = _qualityBar != null &&
                _qualityBar.SetCounts(stats.HumanCount, stats.ValidatedCount, stats.AiCount,
                    stats.SkippedCount, stats.CaptureCount);

            if (!hasData)
            {
                _qualityRow.Visible = false;
                _stageRow.Visible = false;
                _legendRow.Visible = false;
                return;
            }

            // Percentages in the legend: the bar shows the proportions, the legend says what they
            // are worth. Rounded to whole percents — a decimal here is noise, not information.
            //
            // No height set from here. The row used to be measured for a PREDICTED number of
            // lines, which cannot survive a resizable panel: the same key needs one line wide and
            // three narrow. The layout already knows — a Text reports the height its wrapped
            // content needs at the width it has just been given, and the row takes it. The one row
            // of minHeight the document fixes stays as a floor.
            _qualityLegend.Show(QualityBar.BuildLegend(
                stats.HumanCount, stats.ValidatedCount, stats.AiCount,
                stats.SkippedCount, stats.CaptureCount));

            // The step, plus what is left to read. No mark: a score answers "where does each
            // line come from" when the question is "has anyone been through this", and its
            // top demanded retyping by hand what the AI already had right. The remaining
            // count is the part that moves as you work — that is what carries a translator
            // forward, not a grade.
            // ⚠ The stage itself and the share translated are CHIPS now (SetStanding), as on the
            // site and in the Manager; this row keeps only the part that moves as you work.
            string stage = stats.ReviewStage;
            string remainder;
            if (stage == null && stats.Completeness > 0f)
                remainder = $"{stats.CaptureCount} " + TranslatorCore.TranslateOwnUIDynamic("waiting");
            else if (stage != null && stats.UnreviewedCount > 0)
                remainder = $"{stats.UnreviewedCount} " + TranslatorCore.TranslateOwnUIDynamic("left to review");
            else
                remainder = null;
            _qualityLabel.Show(remainder ?? string.Empty);

            // Nothing left to say — nothing translated, or everything read — leaves no row: an
            // empty one would be a blank gap between the bar and its key.
            _stageRow.Visible = remainder != null;

            _qualityRow.Visible = true;
            _legendRow.Visible = true;
        }

        /// <summary>Notice key for "the Main is not taking the new work into account".</summary>
        private const string MainIgnoringNotice = "main-ignoring";

        /// <summary>
        /// Has this install already been shown, and put away, this notice for this translation?
        ///
        /// Keyed by lineage rather than by game: two translations of the same game are two
        /// different situations, and someone may contribute to one and own the other.
        /// </summary>
        private static bool IsNoticeDismissed(string notice)
        {
            string uuid = TranslatorCore.FileUuid;
            if (string.IsNullOrEmpty(uuid)) return false;

            var dismissed = TranslatorCore.Config?.sync?.dismissed_notices;

            return dismissed != null && dismissed.Contains(notice + ":" + uuid);
        }

        /// <summary>
        /// Put the notice away for good. Final for this translation: the line has to be removed
        /// from config.json to see it again, which is a deliberate act rather than an accident.
        /// </summary>
        private void DismissCurrentNotice()
        {
            string uuid = TranslatorCore.FileUuid;
            if (string.IsNullOrEmpty(uuid) || TranslatorCore.Config?.sync == null) return;

            string key = MainIgnoringNotice + ":" + uuid;
            if (!TranslatorCore.Config.sync.dismissed_notices.Contains(key))
            {
                TranslatorCore.Config.sync.dismissed_notices.Add(key);
                TranslatorCore.SaveConfig();
            }

            if (_emptyRow != null) _emptyRow.Visible = false;
        }

        /// <summary>
        /// The notice under the lines, for an author looking at their own published translation.
        ///
        /// 🔴 **Which notice, in which order, and whether it can be put away are the socle's**
        /// (StatusCards.Notice, corpus `status_card`), read off the standing this card shows. This
        /// used to be a chain of five reads of the server state, each with its own sentence — and
        /// the dismiss button of the one judgement among them was left as it was by the four facts
        /// above it.
        /// </summary>
        private void RefreshEmptyWarning(LocalQualityStats stats)
        {
            if (_emptyRow == null) return;

            bool captureOnly = stats != null && TranslationQuality.IsCaptureOnly(
                stats.HumanCount, stats.ValidatedCount, stats.SkippedCount, stats.AiCount, stats.CaptureCount);

            var notice = StatusCards.Notice(_standing, StandingFacts.Server(),
                                            IsNoticeDismissed(MainIgnoringNotice), captureOnly);

            if (notice == null)
            {
                _dismissBtn.Visible = false;
                _emptyRow.Visible = false;
                return;
            }

            var said = notice.Value;

            _emptyLabel.Tone = said.Tone == NoticeTone.Error ? Tone.Error : Tone.Warning;
            _emptyLabel.Show(TranslatorCore.TranslateOwnUIDynamic(said.Text));

            _emptyBtn.Label = said.Verb;

            // A judgement about somebody else can be put away; a fact about the file cannot. The
            // word on the button is the document's, written once by the pipeline.
            _dismissBtn.Visible = said.Dismissable;

            _emptyRow.Visible = true;
        }

        /// <summary>
        /// Calculate quality stats from the local translation cache.
        /// </summary>
        public static LocalQualityStats CalculateLocalStats()
        {
            var stats = new LocalQualityStats();

            if (TranslatorCore.TranslationCache == null)
                return stats;

            foreach (var kvp in TranslatorCore.TranslationCache)
            {
                // Skip metadata keys
                if (kvp.Key.StartsWith("_")) continue;

                stats.TotalLines++;
                var entry = kvp.Value;
                if (entry == null) continue;

                string tag = entry.Tag?.ToUpperInvariant();
                bool isEmpty = string.IsNullOrEmpty(entry.Value);

                switch (tag)
                {
                    case "H":
                        if (isEmpty)
                            stats.CaptureCount++;
                        else
                            stats.HumanCount++;
                        break;
                    case "V":
                        stats.ValidatedCount++;
                        break;
                    case "A":
                        stats.AiCount++;
                        break;
                    case "S":
                        // Counted, but never mixed with the translations — it is a decision
                        // about a line, not a translation of it — its own segment, never the grey.
                        stats.SkippedCount++;
                        break;
                    // "M" (mod UI) is deliberately absent: technical noise, of no use to anyone
                    // judging a translation.
                }
            }

            return stats;
        }

        /// <summary>
        /// Configure card for Main owner state.
        ///
        /// ⚠ <paramref name="waiting"/> is what is actually WAITING — contributions not been
        /// through that are holding something — never how many people contribute. Sending somebody
        /// to review emptiness is how a counter stops being read.
        /// </summary>
        public void ConfigureAsMainOwner(Standing standing, int entryCount, string language,
                                         int waiting, int? linesAvailable = null,
                                         int? linesToReview = null,
                                         TagTally linesNew = default(TagTally),
                                         TagTally linesDiffering = default(TagTally))
        {
            SetStanding(standing);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());

            // The line is the socle's (StatusCards.Secondary, corpus `status_card`): "you own
            // this", or what is waiting — in the words the Manager's signal row uses, and with the
            // weight of the one sentence on this card that asks the owner to do something.
            var line = StatusCards.Secondary(standing, StandingFacts.Server(), localChanges: 0);
            SetSecondaryInfo(line.Text, needsAttention: line.NeedsAttention);
            if (!line.NeedsAttention) return;

            // ⚠ The other axis, on its OWN line rather than appended with a dash. "41 lines to take"
            // and "59 to review" answer two questions and neither follows from the other; the tags
            // answer a third — 21 new lines written by hand is not the proposition 21 machine lines
            // are. The dash joined the first to the second and left the third orphaned underneath.
            // ⚠ The head is printed, the qualities are DRAWN. The socle still composes the whole
            // sentence for anything that can only print (a log, a tooltip); here the four letters
            // become the chips they are on the website — see SetContributionKinds.
            SetContributionKinds(Contributions.ToReview(linesToReview),
                                 Contributions.KindsOfWork(linesNew, linesDiffering));
        }

        /// <summary>
        /// Configure card for Branch owner state.
        ///
        /// ⚠ Says what is waiting before saying where it goes: a contributor opening this panel is
        /// answering "have I got work nobody has seen yet". The count is on the Compare button too,
        /// and that is deliberate — the card is what the eye reads first to know where things
        /// stand, the button is where the decision is taken. Repeating an INFORMATION where it is
        /// needed is not the same fault as offering an action twice.
        /// </summary>
        public void ConfigureAsBranchOwner(Standing standing, int entryCount, string language, int localChanges)
        {
            SetStanding(standing);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());

            // One translatable sentence per variant, ending just before the name — the socle's;
            // the name is appended here, as data, in the one form the ecosystem uses.
            var line = StatusCards.Secondary(standing, StandingFacts.Server(), localChanges);
            // ⚠ The flag travels, as it does for a Main's contributions waiting: lines nobody has
            // seen are the one thing on this card asking for a decision, and they were grey
            // between two other grey lines (2026-09-18).
            SetSecondaryInfo(line.Text,
                             line.Mention != null ? People.MentionOf(line.Mention, TranslatorCore.Config?.api_user) : null,
                             line.NeedsAttention);
        }

        /// <summary>
        /// Configure card for same lineage state (same UUID, not owner, not yet uploaded).
        /// User hasn't decided yet whether to contribute (branch) or fork.
        /// </summary>
        public void ConfigureAsHoldingAnothersLineage(Standing standing, int entryCount, string language, int localChanges)
        {
            SetStanding(standing);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());

            // Whose work this is — the socle's line, the Main named before the uploader. ⚠ It no
            // longer spells out "contribute (Branch) or go independent (Fork)": the three buttons
            // offering exactly that sit immediately below, each with its own label. What is
            // unpublished comes first, as on a branch: the count the Manager's card states.
            var line = StatusCards.Secondary(standing, StandingFacts.Server(), localChanges);
            // ⚠ The flag travels, as it does for a Main's contributions waiting: lines nobody has
            // seen are the one thing on this card asking for a decision, and they were grey
            // between two other grey lines (2026-09-18).
            SetSecondaryInfo(line.Text,
                             line.Mention != null ? People.MentionOf(line.Mention, TranslatorCore.Config?.api_user) : null,
                             line.NeedsAttention);
        }

        /// <summary>
        /// Configure card for local-only state (no server presence).
        /// </summary>
        public void ConfigureAsLocalOnly(Standing standing, int entryCount, string language)
        {
            SetStanding(standing);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());
            SetSecondaryInfo(StatusCards.Secondary(standing, StandingFacts.Server(), localChanges: 0).Text);
        }

        // ⚠ ConfigureAsNotLoggedIn and ConfigureAsNoLocal stood here and are gone with the states
        // that called them. "Not logged in" was never a description of a translation — it is an
        // account, and it now sits on its own axis; "no local" hides this card entirely, since a
        // card describing a file says nothing when there is no file.

        /// <summary>
        /// Show or hide the entire card.
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (_card?.Root != null) _card.Root.Visible = visible;
        }
    }
}
