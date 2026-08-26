using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
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
    /// Reusable status card widget displaying sync status, role, and translation info.
    /// </summary>
    public class StatusCard
    {
        // UI elements
        private GameObject _root;
        private Text _identityLabel;

        /// <summary>Holds the two flags in front of the language pair. Rebuilt with the pair.</summary>
        private GameObject _identityMarks;
        private GameObject _badgeHost;

        /// <summary>
        /// How much room the chips have. The card sits in a 450-wide panel with padding either
        /// side; a generous under-estimate simply breaks a line early, where an over-estimate would
        /// push a chip off the edge.
        /// </summary>
        private const float _stripWidth = 380f;
        private Text _detailsLabel;
        private GameObject _qualityRow;
        private GameObject _stageRow;
        private GameObject _legendRow;
        private Text _qualityLabel;
        private QualityBar _qualityBar;
        private Text _qualityLegend;
        private GameObject _emptyRow;
        private Text _emptyLabel;
        private UniverseLib.UI.Models.ButtonRef _emptyBtn;
        private UniverseLib.UI.Models.ButtonRef _dismissBtn;
        private GameObject _modeRow;
        /// <summary>Where the tag chips of what a contribution holds are drawn.</summary>
        private GameObject _contributionRow;
        private GameObject _voteRow;
        private GameObject _voteHost;
        private Text _voteHint;
        private VoteButtons _voteButtons;
        private int _voteBuiltForId = -1;
        private bool _voteBuiltInteractive;
        private Text _secondaryLabel;

        /// <summary>
        /// The root GameObject of the status card.
        /// </summary>
        public GameObject Root => _root;

        /// <summary>
        /// Create the status card UI in the given parent.
        /// </summary>
        /// <param name="parent">Parent container</param>
        /// <param name="width">Optional fixed width (0 = flexible width to fill parent)</param>
        public void CreateUI(GameObject parent, int width = 0)
        {
            // Main card container - use flexible width if not specified
            if (width > 0)
            {
                _root = UIStyles.CreateAdaptiveCard(parent, "StatusCard", width);
            }
            else
            {
                // ⚠ **A SECTION, not a card, because of where it sits.** This lands inside the
                // "My translation" card, between boxes built by UIStyles.CreateSection — and it was
                // dressing itself as a top-level card: CardPadding (20) against their SectionPadding
                // (12), and CardBackground against SectionBackground. Same outer width, so the frame
                // lined up while its contents started eight pixels further in and on a different
                // shade — which reads as a box of the wrong width stacked among the others.
                //
                // The width parameter above is the other case: used on its own, it IS a card.
                _root = UIFactory.CreateVerticalGroup(parent, "StatusCard", false, false, true, true, UIStyles.ElementSpacing);
                UIFactory.SetLayoutElement(_root, flexibleWidth: 9999);
                UIStyles.SetBackground(_root, UIStyles.SectionBackground);
                var layout = _root.GetComponent<VerticalLayoutGroup>();
                if (layout != null)
                {
                    layout.padding = Compat.MakeRectOffset(UIStyles.SectionPadding, UIStyles.SectionPadding,
                                                           UIStyles.SectionPadding, UIStyles.SectionPadding);
                    layout.childAlignment = TextAnchor.UpperLeft;
                }
            }

            // Row 1 — WHAT this translation is, plus the role badge. Identity leads: you know what
            // you are looking at before you are told how it is doing.
            var identityRow = UIFactory.CreateHorizontalGroup(_root, "IdentityRow", false, false, true, true, UIStyles.SmallSpacing);
            // flexibleHeight 0: this is a line, and it must stay one. Without it the
            // row absorbed the card's spare height — the badge stretched into a tall
            // green column and the language pair floated in the middle of the void.
            UIFactory.SetLayoutElement(identityRow, minHeight: UIStyles.RowHeightMedium, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(identityRow);
            var idLayout = identityRow.GetComponent<HorizontalLayoutGroup>();
            if (idLayout != null) idLayout.childAlignment = TextAnchor.MiddleLeft;

            // The flags lead, the names follow. ⚠ Both: a flag is found faster in a glance and
            // cannot always name a language on its own — ten Indian languages share one — so the
            // words stay and the pictures are added in front. Rebuilt by SetIdentity, since the
            // pair changes when a different translation is taken.
            _identityMarks = UIFactory.CreateUIObject("IdentityMarks", identityRow);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(_identityMarks, false, false, true, true,
                                                            4, 0, 0, 0, 0, TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(_identityMarks, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 0, flexibleHeight: 0);

            // ⚠ Kept for the states that have no pair to show — "Auto", or a language we do not
            // recognise. It is EMPTY whenever the marks carry the names, because "🇬🇧 English →
            // 🇫🇷 French  English → French" is the same sentence twice.
            _identityLabel = UIFactory.CreateLabel(identityRow, "IdentityLabel", "", TextAnchor.MiddleLeft);
            _identityLabel.fontStyle = FontStyle.Bold;
            _identityLabel.fontSize = UIStyles.FontSizeNormal;
            _identityLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_identityLabel.gameObject, flexibleWidth: 9999);
            TranslatorCore.RegisterExcluded(_identityLabel);

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
            _badgeHost = UIFactory.CreateVerticalGroup(_root, "BadgeHost", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_badgeHost, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(_badgeHost);

            var statusRow = UIFactory.CreateHorizontalGroup(_root, "StatusRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(statusRow, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999, flexibleHeight: 0);

            UIStyles.ClearRowBackground(statusRow);

            // Volume + game
            _detailsLabel = UIFactory.CreateLabel(statusRow, "DetailsLabel", "", TextAnchor.MiddleLeft);
            _detailsLabel.fontSize = UIStyles.FontSizeSmall;
            _detailsLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_detailsLabel.gameObject, flexibleWidth: 9999);
            TranslatorCore.RegisterExcluded(_detailsLabel);

            // Row 3 — quality bar, FULL WIDTH. It used to share a row with the score label, which
            // shortened it and made the proportions harder to read; the score moved to the legend.
            _qualityRow = UIFactory.CreateHorizontalGroup(_root, "QualityRow", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_qualityRow, minHeight: 14, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(_qualityRow);
            var qrLayout = _qualityRow.GetComponent<HorizontalLayoutGroup>();
            if (qrLayout != null) qrLayout.childAlignment = TextAnchor.MiddleLeft;

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
            var stageRow = UIFactory.CreateHorizontalGroup(_root, "StageRow", false, false, true, true, 0);
            UIFactory.SetLayoutElement(stageRow, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(stageRow);
            var stageLayout = stageRow.GetComponent<HorizontalLayoutGroup>();
            if (stageLayout != null) stageLayout.childAlignment = TextAnchor.MiddleRight;

            _qualityLabel = UIFactory.CreateLabel(stageRow, "QualityLabel", "", TextAnchor.MiddleRight);
            _qualityLabel.fontSize = UIStyles.FontSizeHint;
            _qualityLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_qualityLabel.gameObject, flexibleWidth: 9999);
            TranslatorCore.RegisterExcluded(_qualityLabel);

            _stageRow = stageRow;

            // Row 5 — the colour key with the PERCENTAGES (asked for: the bar shows proportions,
            // the key says what they are worth). Full width, with nothing beside it.
            var legendRow = UIFactory.CreateHorizontalGroup(_root, "LegendRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(legendRow, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999, flexibleHeight: 0);

            UIStyles.ClearRowBackground(legendRow);

            // Top-aligned: the key takes as many lines as the current width leaves it (see
            // QualityBar.BuildLegend), and centring would float them inside the row.
            _qualityLegend = UIFactory.CreateLabel(legendRow, "QualityLegend", "", TextAnchor.UpperLeft);
            _qualityLegend.fontSize = UIStyles.FontSizeHint;
            _qualityLegend.color = UIStyles.TextMuted;
            // Wrap, and no minHeight of its own: the label announces the height its wrapped text
            // needs at the width it is given, the row inherits it, and a resize re-lays it out
            // without anyone recomputing anything.
            _qualityLegend.horizontalOverflow = HorizontalWrapMode.Wrap;
            UIFactory.SetLayoutElement(_qualityLegend.gameObject, flexibleWidth: 9999);
            TranslatorCore.RegisterExcluded(_qualityLegend);

            _legendRow = legendRow;

            // Row 5b — published, and translating nothing.
            //
            // The website says this on "my translations", where an author goes once. Here it is
            // in front of them while they play the very game concerned, which is the moment they
            // can do something about it — and the button leads straight to the row that carries
            // the delete, because uploading takes one click and unpublishing is a page nobody
            // thinks to look for.
            _emptyRow = UIFactory.CreateHorizontalGroup(_root, "EmptyRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_emptyRow, minHeight: UIStyles.RowHeightMedium, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(_emptyRow);
            var emptyLayout = _emptyRow.GetComponent<HorizontalLayoutGroup>();
            if (emptyLayout != null) emptyLayout.childAlignment = TextAnchor.MiddleLeft;

            _emptyLabel = UIFactory.CreateLabel(_emptyRow, "EmptyLabel", "", TextAnchor.MiddleLeft);
            _emptyLabel.fontSize = UIStyles.FontSizeSmall;
            _emptyLabel.color = UIStyles.StatusWarning;
            _emptyLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            UIFactory.SetLayoutElement(_emptyLabel.gameObject, flexibleWidth: 9999);
            TranslatorCore.RegisterExcluded(_emptyLabel);

            _emptyBtn = UIStyles.CreateSecondaryButton(_emptyRow, "EmptyBtn", "", 120);
            _emptyBtn.OnClick += () =>
            {
                var state = TranslatorCore.ServerState;
                TranslatorCore.OpenUrlSafe(ApiClient.GetMyTranslationsUrl(state?.SiteId));
            };
            TranslatorCore.RegisterExcluded(_emptyBtn.ButtonText);

            // Only on the notice that is a judgement about somebody else: an empty file of one's
            // own is a fact that comes back the moment a line is written, and hiding it would
            // only hide it from the person who can fix it.
            _dismissBtn = UIStyles.CreateSecondaryButton(_emptyRow, "DismissBtn", "", 90);
            _dismissBtn.OnClick += DismissCurrentNotice;
            _dismissBtn.Component.gameObject.SetActive(false);
            TranslatorCore.RegisterExcluded(_dismissBtn.ButtonText);

            _emptyRow.SetActive(false);

            // Hide quality row by default
            _qualityRow.SetActive(false);
            _stageRow.SetActive(false);
            _legendRow.SetActive(false);

            // Row 5 — the ONE thing this mode has to tell you.
            //
            // ⚠ It carried a button too until 2026-08-19, and that was a mistake: each of the three
            // it could show (Review, Compare, Upload) already existed in "Actions" WITH the
            // conditions this row had no way to express — signed in, online, anything left to send.
            // A card that describes must not offer a second door to an action, least of all one
            // that skips the lock.
            _modeRow = UIFactory.CreateHorizontalGroup(_root, "ModeRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_modeRow, minHeight: UIStyles.RowHeightMedium, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(_modeRow);
            var modeLayout = _modeRow.GetComponent<HorizontalLayoutGroup>();
            if (modeLayout != null) modeLayout.childAlignment = TextAnchor.MiddleLeft;

            _secondaryLabel = UIFactory.CreateLabel(_modeRow, "SecondaryLabel", "", TextAnchor.MiddleLeft);
            _secondaryLabel.fontSize = UIStyles.FontSizeSmall;
            _secondaryLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_secondaryLabel.gameObject, flexibleWidth: 0);
            TranslatorCore.RegisterExcluded(_secondaryLabel);

            _modeRow.SetActive(false);

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
            _contributionRow = UIFactory.CreateVerticalGroup(_root, "Contributions", false, false, true, true, 2);
            UIFactory.SetLayoutElement(_contributionRow, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(_contributionRow);
            var contribLayout = _contributionRow.GetComponent<VerticalLayoutGroup>();
            if (contribLayout != null) contribLayout.childAlignment = TextAnchor.UpperLeft;
            _contributionRow.SetActive(false);

            // Row 6 — giving something back. Last, because it is not status: it is the one thing
            // the player can do FOR the translation rather than with it.
            _voteRow = UIFactory.CreateHorizontalGroup(_root, "VoteRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_voteRow, minHeight: UIStyles.RowHeightMedium, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(_voteRow);
            var voteLayout = _voteRow.GetComponent<HorizontalLayoutGroup>();
            if (voteLayout != null) voteLayout.childAlignment = TextAnchor.MiddleLeft;

            // 🔴 **The row says what it is.** It showed a bare "+1" beside a sentence, and a lone
            // signed number names nothing: it could be a score, a difference, lines added. Every
            // other row of this card carries the word for its subject, and this one did not.
            //
            // ⚠ "Votes", the website's word (games.sort.votes, admin.votes), not "rating": the
            // same fact must read the same way in the mod and on the site, and the site counts
            // votes. See CLAUDE.md — it is one ecosystem.
            var voteTitle = UIFactory.CreateLabel(_voteRow, "VoteTitle", "Votes", TextAnchor.MiddleLeft);
            voteTitle.fontSize = UIStyles.FontSizeHint;
            voteTitle.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(voteTitle.gameObject, minWidth: 40, flexibleWidth: 0);
            // Written once and never rewritten by the code, so it goes through the translation
            // pipeline like the card's other fixed words — unlike the labels above, whose text the
            // code replaces on every refresh and which are therefore excluded from it.
            TranslatorCore.RegisterUIText(voteTitle);

            // The widget is rebuilt into this host whenever the mode changes (signed in, seen
            // enough of it, someone else's work) — arrows exist or they don't, they are never
            // shown greyed out.
            _voteHost = UIFactory.CreateHorizontalGroup(_voteRow, "VoteHost", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_voteHost, minWidth: 90, flexibleWidth: 0);

            _voteHint = UIFactory.CreateLabel(_voteRow, "VoteHint", "", TextAnchor.MiddleLeft);
            _voteHint.fontSize = UIStyles.FontSizeHint;
            _voteHint.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_voteHint.gameObject, flexibleWidth: 9999);
            TranslatorCore.RegisterExcluded(_voteHint);

            _voteRow.SetActive(false);
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
                _voteRow.SetActive(false);
                return;
            }

            bool signedIn = !string.IsNullOrEmpty(TranslatorCore.Config?.api_token);
            bool playedEnough = TranslatorCore.HasUsedTranslationEnoughToRate;

            // The server decides who may vote (no self-votes, public only). The mod only adds
            // the one condition the server cannot see: has this player actually used it.
            bool interactive = vote.CanVote && playedEnough;

            // Rebuilt rather than toggled: a greyed-out arrow is a dead end, and the reason it
            // is dead belongs in words next to it.
            if (_voteButtons == null || _voteBuiltForId != vote.TargetId || _voteBuiltInteractive != interactive)
            {
                UIHelpers.DestroyChildren(_voteHost);
                _voteButtons = new VoteButtons();
                _voteButtons.Create(_voteHost, vote.TargetId, vote.Count, OnVoteCast, vote.UserVote, interactive);
                _voteBuiltForId = vote.TargetId;
                _voteBuiltInteractive = interactive;
            }
            else
            {
                _voteButtons.UpdateVoteCount(vote.Count, vote.UserVote);
            }

            // ⚠ **"Vote", not "rate" — the website's verb.** It counts votes, its buttons say
            // Upvote and Downvote, and it refuses with "You cannot vote on your own translation".
            // The mod said "rate" for the same act on the same object: one ecosystem, one word.
            string hint;
            if (interactive)
                hint = "Vote on this translation";
            else if (!signedIn)
                hint = "Sign in to vote on this translation";
            else if (role == LineageRole.Main)
                hint = "You cannot vote on your own translation";
            else if (!playedEnough)
                hint = "Play with it a little, then vote";
            else
                hint = null;

            _voteHint.text = hint == null ? string.Empty : TranslatorCore.TranslateOwnUIDynamic(hint, _voteHint);
            _voteHint.gameObject.SetActive(hint != null);

            _voteRow.SetActive(true);
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
        public void SetStanding(Standing standing)
        {
            if (_badgeHost == null) return;

            UIHelpers.DestroyChildren(_badgeHost);

            var all = Badges.For(standing.Publication, standing.Role == LineageRole.Main ? true
                                     : standing.Role == LineageRole.Branch ? (bool?)false : null,
                                 standing.BranchesWaiting, standing.MainMissing, standing.Sync,
                                 null, null, 0, 0,
                                 linesAvailable: standing.LinesAvailable,

                                 // The other way a lineage loses its head: the Main is still there
                                 // and its owner is not. Ignored by Badges when MainMissing is set
                                 // — a Main that is gone is the whole story.
                                 mainAbandoned: TranslatorCore.ServerState?.MainAbandoned == true,

                                 // ⚠ The author's own word, which nothing else on this card says.
                                 // Without it somebody cannot tell whether they still have to open
                                 // Edit details and declare it — the measurements beside it answer
                                 // a different question.
                                 finished: TranslatorCore.ServerState?.Status is string published
                                     ? string.Equals(published, "complete",
                                                     StringComparison.OrdinalIgnoreCase)
                                     : (bool?)null,

                                 // The Main's other declaration, and the one a would-be
                                 // contributor needs before writing anything. Null on a server
                                 // that never sent it — unknown is not "solo work".
                                 acceptsContributions: TranslatorCore.ServerState?.AcceptsBranches,

                                 // Named in the "Not yours" sentence, so somebody holding a
                                 // community translation is told WHOSE it is and that publishing
                                 // sends them a contribution rather than creating anything.
                                 mainOwner: standing.MainOwner);

            var shown = new List<Badge>();
            foreach (var badge in all)
            {
                if (badge.Kind == BadgeKind.ReviewStage || badge.Kind == BadgeKind.Completeness
                    || badge.Kind == BadgeKind.Votes || badge.Kind == BadgeKind.Downloads)
                {
                    continue;
                }

                shown.Add(badge);
            }

            BadgeStrip.Create(_badgeHost, "Badges", shown, _stripWidth);
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
            _identityLabel.text = marked ? "" : $"{source} → {target}";
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

            for (int i = _identityMarks.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_identityMarks.transform.GetChild(i).gameObject);

            AddIdentitySide("IdSource", sourceLanguage);

            var arrow = UIFactory.CreateLabel(_identityMarks, "IdArrow", "→", TextAnchor.MiddleCenter);
            arrow.fontSize = UIStyles.FontSizeNormal;
            arrow.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(arrow.gameObject, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 0);

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
            var word = UIFactory.CreateLabel(_identityMarks, name + "Auto",
                                             TranslatorCore.TranslateOwnUIDynamic("Auto"),
                                             TextAnchor.MiddleLeft);
            word.fontSize = UIStyles.FontSizeNormal;
            word.color = UIStyles.TextPrimary;
            word.horizontalOverflow = HorizontalWrapMode.Overflow;
            word.verticalOverflow = VerticalWrapMode.Overflow;
            UIFactory.SetLayoutElement(word.gameObject, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 0, flexibleHeight: 0);
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
            _detailsLabel.text = details;
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
                _secondaryLabel.text = string.IsNullOrEmpty(mention)
                    ? TranslatorCore.TranslateOwnUIDynamic(info, _secondaryLabel)
                    : TranslatorCore.TranslateOwnUIDynamic(info) + " " + mention;

                _secondaryLabel.color = needsAttention ? UIStyles.StatusWarning : UIStyles.TextMuted;
            }

            _secondaryLabel.gameObject.SetActive(hasInfo);
            _modeRow.SetActive(hasInfo);

            // 🔴 **The kinds belong to THIS sentence, so they go when it is rewritten.** They used
            // to be a child of the row above and vanished with it; on their own they would outlive
            // the sentence that gives them their subject — a card switched from Main to Branch would
            // still show what somebody else's contributions were holding. Every caller that has
            // kinds to draw calls SetContributionKinds straight after this, so clearing here costs
            // nothing and makes the stale case impossible rather than unlikely.
            if (_contributionRow != null) _contributionRow.SetActive(false);
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

            for (int i = _contributionRow.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_contributionRow.transform.GetChild(i).gameObject);

            bool any = kinds != null && kinds.Length > 0;
            _contributionRow.SetActive(any);
            if (!any) return;

            // 🔴 **One line, opening with "N to review:" — the shape the Manager already has.** It
            // was appended to the sentence above with an em dash ("…lines to take. — 59 to review:")
            // and the dash joined two facts that answer different questions: how much work is
            // waiting, and what that work is made of. The second belongs with the pieces that detail
            // it, which is where the eye goes when deciding whether the evening is worth it.
            var row = UIFactory.CreateHorizontalGroup(_contributionRow, "Kinds", false, false, true, true, 4);
            UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(row);
            var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            if (rowLayout != null) rowLayout.childAlignment = TextAnchor.MiddleLeft;

            if (!string.IsNullOrEmpty(head)) Piece(row, "Head", head + ":");

            for (int k = 0; k < kinds.Length; k++)
            {
                // The separator the socle's sentence uses between groups, so the two read alike.
                Piece(row, "Kind" + k, (k > 0 ? "· " : "") + kinds[k].Total + " " + kinds[k].Label);

                foreach (TagCount piece in kinds[k].Tally.Counted())
                {
                    UIStyles.CreateTagChip(row, piece.Letter, out _);
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
        private static void Piece(GameObject row, string name, string text)
        {
            var label = UIFactory.CreateLabel(row, name, text, TextAnchor.MiddleLeft);
            label.fontSize = UIStyles.FontSizeSmall;
            label.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(label.gameObject, minWidth: Mathf.CeilToInt(label.preferredWidth),
                                       minHeight: UIStyles.RowHeightSmall, flexibleWidth: 0);
            TranslatorCore.RegisterExcluded(label);
        }

        /// <summary>
        /// Update the quality stats display with H/V/A bar.
        /// </summary>
        public void SetQualityStats(LocalQualityStats stats)
        {
            if (_qualityRow == null) return;

            if (stats == null)
            {
                _qualityRow.SetActive(false);
                _stageRow?.SetActive(false);
                _legendRow?.SetActive(false);
                _emptyRow?.SetActive(false);
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
                _qualityRow.SetActive(false);
                _stageRow?.SetActive(false);
                _legendRow?.SetActive(false);
                return;
            }

            // Percentages in the legend: the bar shows the proportions, the legend says what they
            // are worth. Rounded to whole percents — a decimal here is noise, not information.
            if (_qualityLegend != null)
            {
                _qualityLegend.text = QualityBar.BuildLegend(
                    stats.HumanCount, stats.ValidatedCount, stats.AiCount,
                    stats.SkippedCount, stats.CaptureCount);

                // No height set from here any more. The row used to be measured for a PREDICTED
                // number of lines, which cannot survive a resizable panel: the same key needs one
                // line wide and three narrow. The layout already knows — a Text reports the
                // height its wrapped content needs at the width it has just been given, and the
                // row takes it. The one row of minHeight fixed in CreateUI stays as a floor.
            }

            if (_qualityLabel != null)
            {
                // The step, plus what is left to read. No mark: a score answers "where does each
                // line come from" when the question is "has anyone been through this", and its
                // top demanded retyping by hand what the AI already had right. The remaining
                // count is the part that moves as you work — that is what carries a translator
                // forward, not a grade.
                string stage = stats.ReviewStage;
                if (stage == null && stats.Completeness > 0f)
                {
                    // Still mostly untranslated: how much is done and how much is waiting says
                    // more than a review step that has nothing to judge yet.
                    _qualityLabel.text = Mathf.RoundToInt(stats.Completeness * 100f) + "% "
                        + TranslatorCore.TranslateOwnUIDynamic("translated")
                        + $" · {stats.CaptureCount} " + TranslatorCore.TranslateOwnUIDynamic("waiting");
                }
                else if (stage == null)
                {
                    _qualityLabel.text = string.Empty;
                }
                else if (stats.UnreviewedCount > 0)
                {
                    _qualityLabel.text = TranslatorCore.TranslateOwnUIDynamic(stage)
                        + $" · {stats.UnreviewedCount} " + TranslatorCore.TranslateOwnUIDynamic("left to review");
                }
                else
                {
                    _qualityLabel.text = TranslatorCore.TranslateOwnUIDynamic(stage);
                }

                // A file with nothing translated has no stage: an empty row would be a blank
                // gap between the bar and its key.
                _stageRow?.SetActive(stage != null);
            }

            _qualityRow.SetActive(true);
            _legendRow?.SetActive(true);
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

            _emptyRow?.SetActive(false);
        }

        /// <summary>
        /// The warning an author sees while playing the game their empty translation belongs to.
        ///
        /// Only once it is PUBLISHED and theirs: a file being built in capture mode is normal
        /// work, and warning about it would be noise. The website says the same thing on "my
        /// translations", but that is a page somebody visits on purpose — this is in front of
        /// them at the moment the file is actually in their hands.
        /// </summary>
        private void RefreshEmptyWarning(LocalQualityStats stats)
        {
            if (_emptyRow == null) return;

            var state = TranslatorCore.ServerState;
            bool published = state != null && state.Exists && state.IsOwner;

            if (!published)
            {
                _emptyRow.SetActive(false);
                return;
            }

            // Orphaned first: it is the one nobody else can fix. A branch whose Main is gone can
            // never be merged by anyone — the only way forward is to publish it as a translation
            // of its own, which is what Fork does. Said here rather than left to be discovered,
            // because from inside the game everything looks normal.
            if (state.MainMissing == true)
            {
                if (_emptyLabel != null)
                {
                    _emptyLabel.color = UIStyles.StatusError;
                    _emptyLabel.text = TranslatorCore.TranslateOwnUIDynamic(
                        "The translation you contribute to is gone: nobody can merge this work any more.");
                }

                if (_emptyBtn?.ButtonText != null)
                {
                    _emptyBtn.ButtonText.text = TranslatorCore.TranslateOwnUIDynamic("Manage online");
                }

                _emptyRow.SetActive(true);
                return;
            }

            // Then the Main whose owner is gone. Placed between the two on purpose: it ends like
            // the orphan above — nobody will ever merge this — and reads like the closure below,
            // since the translation is still there. What separates it from both is worth the
            // extra notice: nothing was withdrawn and nothing was refused, so the file in this
            // game stays perfectly good to play with.
            if (state.MainAbandoned == true)
            {
                if (_emptyLabel != null)
                {
                    _emptyLabel.color = UIStyles.StatusError;
                    _emptyLabel.text = TranslatorCore.TranslateOwnUIDynamic(
                        "The account behind the translation you contribute to is gone: nobody can "
                        + "merge this work any more. The translation itself still works.");
                }

                if (_emptyBtn?.ButtonText != null)
                {
                    _emptyBtn.ButtonText.text = TranslatorCore.TranslateOwnUIDynamic("Manage online");
                }

                _emptyRow.SetActive(true);
                return;
            }

            // Then the road that closed. Like the orphan above, nothing in the game shows it and
            // no amount of work will reopen it — the Main decided to work alone. Not dismissable
            // for the same reason: it is not an opinion about somebody, it is what may still be
            // done with this file.
            if (state.BranchFrozen == true)
            {
                if (_emptyLabel != null)
                {
                    _emptyLabel.color = UIStyles.StatusError;
                    _emptyLabel.text = TranslatorCore.TranslateOwnUIDynamic(
                        "The translation you contribute to no longer takes contributions: this can no longer be sent.");
                }

                if (_emptyBtn?.ButtonText != null)
                {
                    _emptyBtn.ButtonText.text = TranslatorCore.TranslateOwnUIDynamic("Manage online");
                }

                _emptyRow.SetActive(true);
                return;
            }

            // Told, came back, took nothing in. Not silence — that is dormancy, and it is said
            // elsewhere — but a judgement about somebody else, so it is said ONCE and carries a
            // way to put it away for good.
            if (state.MainIgnoring == true && !IsNoticeDismissed(MainIgnoringNotice))
            {
                if (_emptyLabel != null)
                {
                    _emptyLabel.color = UIStyles.StatusWarning;
                    _emptyLabel.text = TranslatorCore.TranslateOwnUIDynamic(
                        "The Main does not seem to be taking the new work into account. You can publish your own version whenever you like.");
                }

                if (_emptyBtn?.ButtonText != null)
                {
                    _emptyBtn.ButtonText.text = TranslatorCore.TranslateOwnUIDynamic("Manage online");
                }

                if (_dismissBtn?.ButtonText != null)
                {
                    _dismissBtn.ButtonText.text = TranslatorCore.TranslateOwnUIDynamic("Dismiss");
                    _dismissBtn.Component.gameObject.SetActive(true);
                }

                _emptyRow.SetActive(true);
                return;
            }

            _dismissBtn?.Component.gameObject.SetActive(false);

            bool captureOnly = TranslationQuality.IsCaptureOnly(
                stats.HumanCount, stats.ValidatedCount, stats.SkippedCount, stats.AiCount, stats.CaptureCount);

            if (!captureOnly)
            {
                _emptyRow.SetActive(false);
                return;
            }

            if (_emptyLabel != null)
            {
                _emptyLabel.color = UIStyles.StatusWarning;
                _emptyLabel.text = TranslatorCore.TranslateOwnUIDynamic(
                    "Published with no translated line: players who download it see nothing change.");
            }

            if (_emptyBtn?.ButtonText != null)
            {
                _emptyBtn.ButtonText.text = TranslatorCore.TranslateOwnUIDynamic("Manage online");
            }

            _emptyRow.SetActive(true);
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

            if (waiting <= 0)
            {
                SetSecondaryInfo("You own this translation", needsAttention: false);
                return;
            }

            // The socle's words, so this line and the Manager's signal row say one thing.
            // ⚠ And the same weight: contributions waiting is the one thing on this card that
            // asks the owner to do something, so it is not written in the colour of a footnote.
            var said = Contributions.WhatIsWaiting(waiting, linesAvailable);

            // ⚠ The other axis, on its OWN line rather than appended with a dash. "41 lines to take"
            // and "59 to review" answer two questions and neither follows from the other; the tags
            // answer a third — 21 new lines written by hand is not the proposition 21 machine lines
            // are. The dash joined the first to the second and left the third orphaned underneath.
            // ⚠ The head is printed, the qualities are DRAWN. The socle still composes the whole
            // sentence for anything that can only print (a log, a tooltip); here the four letters
            // become the chips they are on the website — see SetContributionKinds.
            SetSecondaryInfo(said, needsAttention: true);
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
        public void ConfigureAsBranchOwner(Standing standing, int entryCount, string language,
                                           string mainOwner, int localChanges)
        {
            SetStanding(standing);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());

            // One translatable sentence per variant, ending just before the name: the number becomes
            // a placeholder, so every count shares one cache entry, and the name is appended after.
            string state = localChanges > 0
                ? $"{localChanges} changes not sent yet"
                : "Everything sent";

            bool named = !string.IsNullOrEmpty(mainOwner);

            SetSecondaryInfo(named ? state + " · your branch of" : state,
                             named ? People.MentionOf(mainOwner, TranslatorCore.Config?.api_user)
                                   : null);
        }

        /// <summary>
        /// Configure card for same lineage state (same UUID, not owner, not yet uploaded).
        /// User hasn't decided yet whether to contribute (branch) or fork.
        /// </summary>
        public void ConfigureAsHoldingAnothersLineage(Standing standing, int entryCount, string language, string mainOwner)
        {
            SetStanding(standing);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());

            // Whose work this is. ⚠ It no longer spells out "contribute (Branch) or go independent
            // (Fork)": the three buttons offering exactly that sit immediately below, in the same
            // glance, each with its own label. Naming the ways out here made the sentence long
            // enough to be skipped, and said nothing the buttons were not already saying.
            SetSecondaryInfo(!string.IsNullOrEmpty(mainOwner) ? "Based on the translation of" : null,
                             !string.IsNullOrEmpty(mainOwner)
                                 ? People.MentionOf(mainOwner, TranslatorCore.Config?.api_user)
                                 : null);
        }

        /// <summary>
        /// Configure card for local-only state (no server presence).
        /// </summary>
        public void ConfigureAsLocalOnly(int entryCount, string language)
        {
            SetStanding(new Standing { Publication = Publication.NeverPublished, Role = LineageRole.None });
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());
            SetSecondaryInfo("Upload to share with others");
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
            if (_root != null)
            {
                _root.SetActive(visible);
            }
        }
    }
}
