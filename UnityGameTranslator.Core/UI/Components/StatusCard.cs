using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Status types for the sync indicator.
    /// </summary>
    public enum SyncStatusType
    {
        Synced,
        OutOfSync,
        Conflict,
        LocalOnly,
        NotLoggedIn,
        NoLocal
    }

    /// <summary>
    /// Role types for the translation.
    /// </summary>
    public enum TranslationRoleType
    {
        None,
        Main,
        Branch,
        Contributor
    }

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

        // Scoring lives in TranslationQuality so the card, the community list and the website
        // all rank a translation the same way.
        public float QualityScore => TranslationQuality.ComputeScore(HumanCount, ValidatedCount, AiCount);

        public string QualityLabel => TranslationQuality.LabelFor(QualityScore);
    }

    /// <summary>
    /// Reusable status card widget displaying sync status, role, and translation info.
    /// </summary>
    public class StatusCard
    {
        // UI elements
        private GameObject _root;
        private Text _identityLabel;
        private GameObject _roleBadge;
        private Image _roleBadgeImage;
        private Text _statusIcon;
        private Text _statusLabel;
        private Text _roleLabel;
        private Text _detailsLabel;
        private GameObject _qualityRow;
        private GameObject _legendRow;
        private Text _qualityLabel;
        private QualityBar _qualityBar;
        private Text _qualityLegend;
        private GameObject _modeRow;
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
                // Create a card that fills available width
                _root = UIFactory.CreateVerticalGroup(parent, "StatusCard", false, false, true, true, UIStyles.ElementSpacing);
                UIFactory.SetLayoutElement(_root, flexibleWidth: 9999);
                UIStyles.SetBackground(_root, UIStyles.CardBackground);
                var layout = _root.GetComponent<VerticalLayoutGroup>();
                if (layout != null)
                {
                    layout.padding = Compat.MakeRectOffset(UIStyles.CardPadding, UIStyles.CardPadding, UIStyles.CardPadding, UIStyles.CardPadding);
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

            // "English → Français" — language names are data, never translated
            _identityLabel = UIFactory.CreateLabel(identityRow, "IdentityLabel", "", TextAnchor.MiddleLeft);
            _identityLabel.fontStyle = FontStyle.Bold;
            _identityLabel.fontSize = UIStyles.FontSizeNormal;
            _identityLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_identityLabel.gameObject, flexibleWidth: 9999);
            TranslatorCore.RegisterExcluded(_identityLabel);

            // Role badge: a coloured chip rather than a raw "[MAIN]" tag, so the mode reads at a
            // glance and each role owns a colour.
            _roleBadge = UIFactory.CreateUIObject("RoleBadge", identityRow);
            _roleBadgeImage = _roleBadge.AddComponent<Image>();
            _roleBadgeImage.color = UIStyles.ButtonSecondary;
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(_roleBadge, true, true, true, true, 0, 8, 8, 3, 3);
            // A chip has the size of its text, never the size of what surrounds it
            UIFactory.SetLayoutElement(_roleBadge, minWidth: 62, minHeight: 20, preferredHeight: 20,
                flexibleWidth: 0, flexibleHeight: 0);

            _roleLabel = UIFactory.CreateLabel(_roleBadge, "RoleLabel", "MAIN", TextAnchor.MiddleCenter);
            _roleLabel.fontStyle = FontStyle.Bold;
            _roleLabel.fontSize = UIStyles.FontSizeHint;
            _roleLabel.color = Color.white;
            TranslatorCore.RegisterExcluded(_roleLabel);

            // Row 2 — how it is doing, and how much of it there is
            var statusRow = UIFactory.CreateHorizontalGroup(_root, "StatusRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(statusRow, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999, flexibleHeight: 0);

            UIStyles.ClearRowBackground(statusRow);

            // Status indicator (colored dot). Technical symbol/status/role tags — never translate.
            _statusIcon = UIFactory.CreateLabel(statusRow, "StatusIcon", "●", TextAnchor.MiddleLeft);
            _statusIcon.fontSize = UIStyles.FontSizeNormal;
            UIFactory.SetLayoutElement(_statusIcon.gameObject, minWidth: 16, flexibleWidth: 0);
            TranslatorCore.RegisterExcluded(_statusIcon);

            _statusLabel = UIFactory.CreateLabel(statusRow, "StatusLabel", "SYNCED", TextAnchor.MiddleLeft);
            _statusLabel.fontStyle = FontStyle.Bold;
            _statusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_statusLabel.gameObject, flexibleWidth: 0);
            TranslatorCore.RegisterExcluded(_statusLabel);

            // Volume + game, kept next to the status so the second line reads as one sentence
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

            // Row 4 — legend carrying the PERCENTAGES (asked for: the bar shows proportions, the
            // legend says what they are worth), with the overall score at the right end.
            var legendRow = UIFactory.CreateHorizontalGroup(_root, "LegendRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(legendRow, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999, flexibleHeight: 0);

            UIStyles.ClearRowBackground(legendRow);

            _qualityLegend = UIFactory.CreateLabel(legendRow, "QualityLegend", "", TextAnchor.MiddleLeft);
            _qualityLegend.fontSize = UIStyles.FontSizeHint;
            _qualityLegend.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_qualityLegend.gameObject, flexibleWidth: 9999);
            TranslatorCore.RegisterExcluded(_qualityLegend);

            _qualityLabel = UIFactory.CreateLabel(legendRow, "QualityLabel", "", TextAnchor.MiddleRight);
            _qualityLabel.fontSize = UIStyles.FontSizeHint;
            _qualityLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_qualityLabel.gameObject, minWidth: 90, flexibleWidth: 0);
            TranslatorCore.RegisterExcluded(_qualityLabel);

            _legendRow = legendRow;

            // Hide quality row by default
            _qualityRow.SetActive(false);
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
        }

        /// <summary>
        /// Update the sync status display.
        /// </summary>
        public void SetStatus(SyncStatusType status)
        {
            if (_statusIcon == null || _statusLabel == null) return;

            switch (status)
            {
                case SyncStatusType.Synced:
                    _statusIcon.color = UIStyles.StatusSuccess;
                    _statusLabel.text = TranslatorCore.TranslateOwnUIDynamic("SYNCED", _statusLabel);
                    _statusLabel.color = UIStyles.StatusSuccess;
                    break;
                case SyncStatusType.OutOfSync:
                    _statusIcon.color = UIStyles.StatusWarning;
                    _statusLabel.text = TranslatorCore.TranslateOwnUIDynamic("OUT OF SYNC", _statusLabel);
                    _statusLabel.color = UIStyles.StatusWarning;
                    break;
                case SyncStatusType.Conflict:
                    _statusIcon.color = UIStyles.StatusError;
                    _statusLabel.text = TranslatorCore.TranslateOwnUIDynamic("CONFLICT", _statusLabel);
                    _statusLabel.color = UIStyles.StatusError;
                    break;
                case SyncStatusType.LocalOnly:
                    _statusIcon.color = UIStyles.TextMuted;
                    _statusLabel.text = TranslatorCore.TranslateOwnUIDynamic("NOT SHARED", _statusLabel);
                    _statusLabel.color = UIStyles.TextMuted;
                    break;
                case SyncStatusType.NotLoggedIn:
                    _statusIcon.color = UIStyles.TextMuted;
                    _statusLabel.text = TranslatorCore.TranslateOwnUIDynamic("NOT LOGGED IN", _statusLabel);
                    _statusLabel.color = UIStyles.TextMuted;
                    break;
                case SyncStatusType.NoLocal:
                    _statusIcon.color = UIStyles.TextMuted;
                    _statusLabel.text = TranslatorCore.TranslateOwnUIDynamic("NO TRANSLATION", _statusLabel);
                    _statusLabel.color = UIStyles.TextMuted;
                    break;
            }
        }

        /// <summary>
        /// Update the role display.
        /// </summary>
        public void SetRole(TranslationRoleType role, string additionalInfo = null)
        {
            if (_roleLabel == null) return;

            // A coloured chip, one colour per role: the mode is recognised before being read.
            switch (role)
            {
                case TranslationRoleType.Main:
                    _roleLabel.text = "MAIN";
                    _roleBadgeImage.color = UIStyles.StatusSuccess;
                    _roleBadge.SetActive(true);
                    break;
                case TranslationRoleType.Branch:
                    _roleLabel.text = "BRANCH";
                    _roleBadgeImage.color = UIStyles.StatusInfo;
                    _roleBadge.SetActive(true);
                    break;
                case TranslationRoleType.Contributor:
                    _roleLabel.text = "CONTRIBUTOR";
                    _roleBadgeImage.color = UIStyles.ButtonPrimary;
                    _roleBadge.SetActive(true);
                    break;
                case TranslationRoleType.None:
                default:
                    _roleBadge.SetActive(false);
                    break;
            }
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
            _identityLabel.text = $"{source} → {target}";
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
                _legendRow?.SetActive(false);
                return;
            }

            // Captures are part of the picture: a file made of 900 captured lines and 100
            // translated ones has to look like it. Hiding the grey flattered the result.
            bool hasData = _qualityBar != null &&
                _qualityBar.SetCounts(stats.HumanCount, stats.ValidatedCount, stats.AiCount,
                    stats.SkippedCount, stats.CaptureCount);

            if (!hasData)
            {
                _qualityRow.SetActive(false);
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
            }

            if (_qualityLabel != null)
            {
                // A file with nothing translated has no quality to report. The formula would
                // return 0.0/3 and read as "terrible translation" for a file that simply has
                // no translation in it yet — a verdict on work that does not exist.
                bool hasTranslations = stats.HumanCount + stats.ValidatedCount + stats.AiCount > 0;
                _qualityLabel.text = hasTranslations ? $"{stats.QualityScore:F1}/3" : string.Empty;
            }

            _qualityRow.SetActive(true);
            _legendRow?.SetActive(true);
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
        public void ConfigureAsMainOwner(SyncStatusType syncStatus, int entryCount, string language, int branchCount)
        {
            SetStatus(syncStatus);
            SetRole(TranslationRoleType.Main);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());
            SetSecondaryInfo(branchCount > 0
                ? $"{branchCount} contribution(s) from other players to review"
                : "You own this translation");
        }

        /// <summary>
        /// Configure card for Branch owner state.
        /// </summary>
        public void ConfigureAsBranchOwner(SyncStatusType syncStatus, int entryCount, string language, string mainOwner)
        {
            SetStatus(syncStatus);
            SetRole(TranslationRoleType.Branch);
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
        public void ConfigureAsContributor(SyncStatusType syncStatus, int entryCount, string language, string mainOwner)
        {
            SetStatus(syncStatus);
            // Don't show a role badge - user hasn't decided yet (not a contributor until they upload as branch)
            SetRole(TranslationRoleType.None);
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
            SetStatus(SyncStatusType.LocalOnly);
            SetRole(TranslationRoleType.None);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());
            SetSecondaryInfo("Upload to share with others");
        }

        /// <summary>
        /// Configure card for not logged in state.
        /// </summary>
        public void ConfigureAsNotLoggedIn(int entryCount, string language)
        {
            SetStatus(SyncStatusType.NotLoggedIn);
            SetRole(TranslationRoleType.None);
            SetDetails(entryCount, language);
            SetQualityStats(CalculateLocalStats());
            SetSecondaryInfo("Login to sync your translation");
        }

        /// <summary>
        /// Configure card for no local translation state.
        /// </summary>
        public void ConfigureAsNoLocal()
        {
            SetStatus(SyncStatusType.NoLocal);
            SetRole(TranslationRoleType.None);
            SetDetails(0, "None");
            SetQualityStats(null);
            SetSecondaryInfo("Download from community or create new");
        }

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
