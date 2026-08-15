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
        private GameObject _voteRow;
        private GameObject _voteHost;
        private Text _voteHint;
        private VoteButtons _voteButtons;
        private int _voteBuiltForId = -1;
        private bool _voteBuiltInteractive;
        private Text _secondaryLabel;
        private UniverseLib.UI.Models.ButtonRef _modeActionBtn;
        private Action _modeAction;

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

            // Row 5 — the ONE thing this mode wants to tell you, and the ONE action that answers
            // it. Reading them side by side is what makes the card self-sufficient per mode.
            _modeRow = UIFactory.CreateHorizontalGroup(_root, "ModeRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_modeRow, minHeight: UIStyles.RowHeightMedium, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(_modeRow);
            var modeLayout = _modeRow.GetComponent<HorizontalLayoutGroup>();
            if (modeLayout != null) modeLayout.childAlignment = TextAnchor.MiddleLeft;

            _secondaryLabel = UIFactory.CreateLabel(_modeRow, "SecondaryLabel", "", TextAnchor.MiddleLeft);
            _secondaryLabel.fontSize = UIStyles.FontSizeSmall;
            _secondaryLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_secondaryLabel.gameObject, flexibleWidth: 9999);
            TranslatorCore.RegisterExcluded(_secondaryLabel);

            _modeActionBtn = UIStyles.CreateSecondaryButton(_modeRow, "ModeActionBtn", "", 110);
            _modeActionBtn.Component.gameObject.SetActive(false);
            TranslatorCore.RegisterExcluded(_modeActionBtn.ButtonText);

            _modeRow.SetActive(false);

            // Row 6 — giving something back. Last, because it is not status: it is the one thing
            // the player can do FOR the translation rather than with it.
            _voteRow = UIFactory.CreateHorizontalGroup(_root, "VoteRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_voteRow, minHeight: UIStyles.RowHeightMedium, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(_voteRow);
            var voteLayout = _voteRow.GetComponent<HorizontalLayoutGroup>();
            if (voteLayout != null) voteLayout.childAlignment = TextAnchor.MiddleLeft;

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

            string hint;
            if (interactive)
                hint = "Rate this translation";
            else if (!signedIn)
                hint = "Sign in to rate this translation";
            else if (role == LineageRole.Main)
                hint = "You cannot rate your own translation";
            else if (!playedEnough)
                hint = "Play with it a little, then rate it";
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
                                 null, null, 0, 0);

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
            // The pair is drawn as "flag name → flag name". The text label only speaks when a side
            // has no language to mark — auto-detection, or a name the catalogue does not know.
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

            bool from = LanguageMark.Create(_identityMarks, "IdSource", sourceLanguage,
                                            withName: true) != null;
            if (!from) return false;

            var arrow = UIFactory.CreateLabel(_identityMarks, "IdArrow", "→", TextAnchor.MiddleCenter);
            arrow.fontSize = UIStyles.FontSizeNormal;
            arrow.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(arrow.gameObject, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 0);

            return LanguageMark.Create(_identityMarks, "IdTarget", targetLanguage,
                                       withName: true) != null;
        }

        /// <summary>
        /// The one thing this mode has to say, and the one action that answers it. Pass a null
        /// action label to show the information alone.
        /// </summary>
        public void SetModeAction(string info, string actionLabel = null, Action onClick = null)
        {
            if (_modeRow == null) return;

            bool hasInfo = !string.IsNullOrEmpty(info);
            bool hasAction = !string.IsNullOrEmpty(actionLabel) && onClick != null;

            if (hasInfo)
                _secondaryLabel.text = TranslatorCore.TranslateOwnUIDynamic(info, _secondaryLabel);
            _secondaryLabel.gameObject.SetActive(hasInfo);

            if (hasAction)
            {
                _modeAction = onClick;
                _modeActionBtn.ButtonText.text = TranslatorCore.TranslateOwnUIDynamic(actionLabel, _modeActionBtn.ButtonText);
                // Rebound every time: OnClick is a single delegate here, so assigning replaces the
                // previous mode's action instead of stacking them.
                _modeActionBtn.OnClick = () => _modeAction?.Invoke();
            }
            _modeActionBtn.Component.gameObject.SetActive(hasAction);

            _modeRow.SetActive(hasInfo || hasAction);
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
        /// Update the secondary info (branches count for Main, owner name for Branch).
        /// </summary>
        public void SetSecondaryInfo(string info)
        {
            // Same row as the mode action — information alone, no button.
            SetModeAction(info);
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
        /// </summary>
        public void ConfigureAsMainOwner(Standing standing, int entryCount, string language, int branchCount)
        {
            SetStanding(standing);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());
            SetSecondaryInfo(branchCount > 0
                ? $"{branchCount} contribution(s) from other players to review"
                : "You own this translation");
        }

        /// <summary>
        /// Configure card for Branch owner state.
        /// </summary>
        public void ConfigureAsBranchOwner(Standing standing, int entryCount, string language, string mainOwner)
        {
            SetStanding(standing);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());
            SetSecondaryInfo(!string.IsNullOrEmpty(mainOwner)
                ? $"Your changes are sent to @{mainOwner} for review"
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
            // Show whose translation this is based on, prompting user to make a choice
            SetSecondaryInfo(!string.IsNullOrEmpty(mainOwner)
                ? $"Based on @{mainOwner}'s translation — contribute your changes (Branch) or go independent (Fork)"
                : "Contribute your changes (Branch) or go independent (Fork)");
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
