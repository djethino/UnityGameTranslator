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
        private Text _secondaryLabel;

        /// <summary>
        /// The root GameObject of the status card.
        /// </summary>
        public GameObject Root => _root;

        /// <summary>
        /// Create the status card UI in the given parent.
        /// </summary>
        public void CreateUI(GameObject parent)
        {
            // Main card container
            _root = UIStyles.CreateAdaptiveCard(parent, "StatusCard");

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
        /// Configure card for Main owner state.
        /// </summary>
        public void ConfigureAsMainOwner(SyncStatusType syncStatus, int entryCount, string language, int branchCount)
        {
            SetStatus(syncStatus);
            SetRole(TranslationRoleType.Main);
            SetDetails(entryCount, language);
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
            SetSecondaryInfo(!string.IsNullOrEmpty(mainOwner) ? $"Contributing to @{mainOwner}" : null);
        }

        /// <summary>
        /// Configure card for Contributor state (same UUID, not owner).
        /// </summary>
        public void ConfigureAsContributor(SyncStatusType syncStatus, int entryCount, string language, string mainOwner)
        {
            SetStatus(syncStatus);
            SetRole(TranslationRoleType.Contributor);
            SetDetails(entryCount, language);
            SetSecondaryInfo(!string.IsNullOrEmpty(mainOwner) ? $"Based on @{mainOwner}'s translation" : null);
        }

        /// <summary>
        /// Configure card for local-only state (no server presence).
        /// </summary>
        public void ConfigureAsLocalOnly(int entryCount, string language)
        {
            SetStatus(SyncStatusType.LocalOnly);
            SetRole(TranslationRoleType.None);
            SetDetails(entryCount, language);
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
