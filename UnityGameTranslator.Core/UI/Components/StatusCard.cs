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
        public int TotalLines { get; set; }

        public float QualityScore
        {
            get
            {
                int effective = HumanCount + ValidatedCount + AiCount;
                if (effective == 0) return 0f;
                float weighted = (HumanCount * 3) + (ValidatedCount * 2) + (AiCount * 1);
                return weighted / effective;
            }
        }

        public string QualityLabel
        {
            get
            {
                float score = QualityScore;
                if (score >= 2.5f) return "Excellent";
                if (score >= 2.0f) return "Good";
                if (score >= 1.5f) return "Fair";
                if (score >= 1.0f) return "Basic";
                return "Raw AI";
            }
        }
    }

    /// <summary>
    /// Reusable status card widget displaying sync status, role, and translation info.
    /// </summary>
    public class StatusCard
    {
        // UI elements
        private GameObject _root;
        private Text _statusIcon;
        private Text _statusLabel;
        private Text _roleLabel;
        private Text _detailsLabel;
        private GameObject _qualityRow;
        private Text _qualityLabel;
        private GameObject _qualityBarContainer;
        private Image _humanBar;
        private Image _validatedBar;
        private Image _aiBar;
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
                // Create a card that fills available width
                _root = UIFactory.CreateVerticalGroup(parent, "StatusCard", false, false, true, true, UIStyles.ElementSpacing);
                UIFactory.SetLayoutElement(_root, flexibleWidth: 9999);
                UIStyles.SetBackground(_root, UIStyles.CardBackground);
                var layout = _root.GetComponent<VerticalLayoutGroup>();
                if (layout != null)
                {
                    layout.padding = new RectOffset(UIStyles.CardPadding, UIStyles.CardPadding, UIStyles.CardPadding, UIStyles.CardPadding);
                    layout.childAlignment = TextAnchor.UpperLeft;
                }
            }

            // First row: Status icon + label + Role badge
            var statusRow = UIFactory.CreateHorizontalGroup(_root, "StatusRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(statusRow, minHeight: UIStyles.RowHeightMedium, flexibleWidth: 9999);

            // Status indicator (colored dot)
            _statusIcon = UIFactory.CreateLabel(statusRow, "StatusIcon", "●", TextAnchor.MiddleLeft);
            _statusIcon.fontSize = UIStyles.FontSizeNormal;
            UIFactory.SetLayoutElement(_statusIcon.gameObject, minWidth: 25);

            // Status text
            _statusLabel = UIFactory.CreateLabel(statusRow, "StatusLabel", "SYNCED", TextAnchor.MiddleLeft);
            _statusLabel.fontStyle = FontStyle.Bold;
            _statusLabel.fontSize = UIStyles.FontSizeNormal;
            UIFactory.SetLayoutElement(_statusLabel.gameObject, flexibleWidth: 9999);

            // Role badge
            _roleLabel = UIFactory.CreateLabel(statusRow, "RoleLabel", "[MAIN]", TextAnchor.MiddleRight);
            _roleLabel.fontStyle = FontStyle.Bold;
            _roleLabel.fontSize = UIStyles.FontSizeSmall;
            _roleLabel.color = UIStyles.TextAccent;
            UIFactory.SetLayoutElement(_roleLabel.gameObject, minWidth: 100);

            // Details row: entry count, language
            _detailsLabel = UIFactory.CreateLabel(_root, "DetailsLabel", "", TextAnchor.MiddleLeft);
            _detailsLabel.fontSize = UIStyles.FontSizeNormal;
            _detailsLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_detailsLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Quality row: H/V/A bar + score
            _qualityRow = UIFactory.CreateHorizontalGroup(_root, "QualityRow", false, false, true, true, UIStyles.SmallSpacing);
            UIFactory.SetLayoutElement(_qualityRow, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

            // Quality bar container (stacked colored segments)
            _qualityBarContainer = UIFactory.CreateHorizontalGroup(_qualityRow, "QualityBar", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_qualityBarContainer, minHeight: 8, preferredHeight: 8, minWidth: 100, flexibleWidth: 9999);

            // H segment (green)
            var humanObj = UIFactory.CreateUIObject("HumanBar", _qualityBarContainer);
            _humanBar = humanObj.AddComponent<Image>();
            _humanBar.color = UIStyles.StatusSuccess;
            UIFactory.SetLayoutElement(humanObj, minHeight: 8, flexibleWidth: 0);

            // V segment (blue)
            var validatedObj = UIFactory.CreateUIObject("ValidatedBar", _qualityBarContainer);
            _validatedBar = validatedObj.AddComponent<Image>();
            _validatedBar.color = UIStyles.StatusInfo;
            UIFactory.SetLayoutElement(validatedObj, minHeight: 8, flexibleWidth: 0);

            // A segment (orange)
            var aiObj = UIFactory.CreateUIObject("AiBar", _qualityBarContainer);
            _aiBar = aiObj.AddComponent<Image>();
            _aiBar.color = UIStyles.StatusWarning;
            UIFactory.SetLayoutElement(aiObj, minHeight: 8, flexibleWidth: 0);

            // Quality label
            _qualityLabel = UIFactory.CreateLabel(_qualityRow, "QualityLabel", "", TextAnchor.MiddleRight);
            _qualityLabel.fontSize = UIStyles.FontSizeSmall;
            _qualityLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_qualityLabel.gameObject, minWidth: 80);

            // Hide quality row by default
            _qualityRow.SetActive(false);

            // Secondary info row: branches count or main owner
            _secondaryLabel = UIFactory.CreateLabel(_root, "SecondaryLabel", "", TextAnchor.MiddleLeft);
            _secondaryLabel.fontSize = UIStyles.FontSizeSmall;
            _secondaryLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_secondaryLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
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
                    _statusLabel.text = "SYNCED";
                    _statusLabel.color = UIStyles.StatusSuccess;
                    break;
                case SyncStatusType.OutOfSync:
                    _statusIcon.color = UIStyles.StatusWarning;
                    _statusLabel.text = "OUT OF SYNC";
                    _statusLabel.color = UIStyles.StatusWarning;
                    break;
                case SyncStatusType.Conflict:
                    _statusIcon.color = UIStyles.StatusError;
                    _statusLabel.text = "CONFLICT";
                    _statusLabel.color = UIStyles.StatusError;
                    break;
                case SyncStatusType.LocalOnly:
                    _statusIcon.color = UIStyles.TextMuted;
                    _statusLabel.text = "LOCAL ONLY";
                    _statusLabel.color = UIStyles.TextMuted;
                    break;
                case SyncStatusType.NotLoggedIn:
                    _statusIcon.color = UIStyles.TextMuted;
                    _statusLabel.text = "NOT LOGGED IN";
                    _statusLabel.color = UIStyles.TextMuted;
                    break;
                case SyncStatusType.NoLocal:
                    _statusIcon.color = UIStyles.TextMuted;
                    _statusLabel.text = "NO TRANSLATION";
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

            switch (role)
            {
                case TranslationRoleType.Main:
                    _roleLabel.text = "[MAIN]";
                    _roleLabel.color = UIStyles.StatusSuccess;
                    _roleLabel.gameObject.SetActive(true);
                    break;
                case TranslationRoleType.Branch:
                    _roleLabel.text = "[BRANCH]";
                    _roleLabel.color = UIStyles.StatusInfo;
                    _roleLabel.gameObject.SetActive(true);
                    break;
                case TranslationRoleType.Contributor:
                    _roleLabel.text = "[CONTRIBUTOR]";
                    _roleLabel.color = UIStyles.TextAccent;
                    _roleLabel.gameObject.SetActive(true);
                    break;
                case TranslationRoleType.None:
                default:
                    _roleLabel.gameObject.SetActive(false);
                    break;
            }
        }

        /// <summary>
        /// Update the details display (entry count, language, game).
        /// </summary>
        public void SetDetails(int entryCount, string targetLanguage, string gameName = null)
        {
            if (_detailsLabel == null) return;

            string details = $"{entryCount} entries • {targetLanguage}";
            if (!string.IsNullOrEmpty(gameName))
            {
                details += $" • {gameName}";
            }
            _detailsLabel.text = details;
        }

        /// <summary>
        /// Update the secondary info (branches count for Main, owner name for Branch).
        /// </summary>
        public void SetSecondaryInfo(string info)
        {
            if (_secondaryLabel == null) return;

            if (string.IsNullOrEmpty(info))
            {
                _secondaryLabel.gameObject.SetActive(false);
            }
            else
            {
                _secondaryLabel.text = info;
                _secondaryLabel.gameObject.SetActive(true);
            }
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
                return;
            }

            int total = stats.HumanCount + stats.ValidatedCount + stats.AiCount;
            if (total == 0)
            {
                _qualityRow.SetActive(false);
                return;
            }

            // Update bar widths (flexible layout based on proportions)
            var humanLayout = _humanBar?.gameObject?.GetComponent<LayoutElement>();
            var validatedLayout = _validatedBar?.gameObject?.GetComponent<LayoutElement>();
            var aiLayout = _aiBar?.gameObject?.GetComponent<LayoutElement>();

            if (humanLayout != null) humanLayout.flexibleWidth = stats.HumanCount;
            if (validatedLayout != null) validatedLayout.flexibleWidth = stats.ValidatedCount;
            if (aiLayout != null) aiLayout.flexibleWidth = stats.AiCount;

            // Update label
            if (_qualityLabel != null)
            {
                _qualityLabel.text = $"{stats.QualityScore:F1}/3 ({stats.QualityLabel})";
            }

            _qualityRow.SetActive(true);
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
            SetSecondaryInfo(branchCount > 0 ? $"{branchCount} branch(es) contributing" : null);
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
            SetSecondaryInfo(!string.IsNullOrEmpty(mainOwner) ? $"Contributing to @{mainOwner}" : null);
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
                ? $"Based on @{mainOwner}'s translation • Choose: Branch or Fork"
                : "Choose: Branch or Fork");
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
