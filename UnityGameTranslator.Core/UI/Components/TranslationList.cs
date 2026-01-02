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
        // UI elements
        private GameObject _listContent;
        private Text _statusLabel;

        // State
        private List<TranslationInfo> _translations = new List<TranslationInfo>();
        private TranslationInfo _selectedTranslation;
        private bool _isSearching;

        // Vote buttons storage (keyed by translation ID)
        private Dictionary<int, VoteButtons> _voteButtons = new Dictionary<int, VoteButtons>();

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
            UIFactory.SetLayoutElement(scrollObj, minHeight: listHeight, flexibleHeight: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_listContent, false, false, true, true, 5, 5, 5, 5, 5);
            UIStyles.SetBackground(scrollObj, UIStyles.InputBackground);
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
            _voteButtons.Clear();
            ClearUI();
        }

        /// <summary>
        /// Refresh the list UI (e.g., after login status change).
        /// </summary>
        public void Refresh()
        {
            if (_translations.Count > 0)
            {
                _voteButtons.Clear();
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

                if (result != null && result.Success)
                {
                    var translations = result.Translations ?? new List<TranslationInfo>();
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
                    SetStatus(result?.Error ?? "Search failed", UIStyles.StatusError);
                }
            }
            catch (Exception e)
            {
                SetStatus($"Error: {e.Message}", UIStyles.StatusError);
                TranslatorCore.LogWarning($"[TranslationList] Search error: {e.Message}");
            }
            finally
            {
                _isSearching = false;
            }
        }

        private void ClearUI()
        {
            if (_listContent == null) return;

            // Manual iteration for IL2CPP compatibility (foreach on Transform doesn't work)
            for (int i = _listContent.transform.childCount - 1; i >= 0; i--)
            {
                GameObject.Destroy(_listContent.transform.GetChild(i).gameObject);
            }
        }

        private void Populate()
        {
            ClearUI();

            // isLoggedIn must be based on api_token, not api_user (api_user persists after logout)
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            string currentUser = isLoggedIn ? _getCurrentUser?.Invoke() : null;

            int displayCount = Math.Min(5, _translations.Count);
            for (int i = 0; i < displayCount; i++)
            {
                var t = _translations[i];
                CreateListItem(t, isLoggedIn, currentUser);
            }
        }

        private void CreateListItem(TranslationInfo translation, bool isLoggedIn, string currentUser)
        {
            // Check if this translation is from the same lineage (UUID match)
            bool isLineageMatch = TranslatorCore.IsUuidMatch(translation.FileUuid);

            var itemRow = UIFactory.CreateHorizontalGroup(_listContent, $"Item_{translation.Id}", false, false, true, true, 10);
            UIFactory.SetLayoutElement(itemRow, minHeight: UIStyles.CodeDisplayHeight, flexibleWidth: 9999);

            // Use highlight background for lineage match
            UIStyles.SetBackground(itemRow, isLineageMatch ? UIStyles.ItemBackgroundLineage : UIStyles.ItemBackground);

            // Configure layout with padding and alignment
            var layout = itemRow.GetComponent<HorizontalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = new RectOffset(10, 10, 8, 8); // Left, Right, Top, Bottom
                layout.childAlignment = TextAnchor.MiddleLeft;
            }

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

            // Info column
            var infoCol = UIFactory.CreateVerticalGroup(itemRow, "InfoCol", false, false, true, true, 2);
            UIFactory.SetLayoutElement(infoCol, flexibleWidth: 9999);

            // Configure info column alignment
            var infoLayout = infoCol.GetComponent<VerticalLayoutGroup>();
            if (infoLayout != null)
            {
                infoLayout.childAlignment = TextAnchor.MiddleLeft;
            }

            // Title row with badges
            string label = $"{translation.TargetLanguage} by {translation.Uploader}";
            bool isOwnTranslation = isLoggedIn && !string.IsNullOrEmpty(currentUser) &&
                translation.Uploader.Equals(currentUser, StringComparison.OrdinalIgnoreCase);
            if (isOwnTranslation) label += " (you)";
            if (isLineageMatch && !isOwnTranslation) label += " [YOUR]";  // Badge for same lineage (not your upload)

            var titleLabel = UIFactory.CreateLabel(infoCol, "Title", label, TextAnchor.MiddleLeft);
            titleLabel.fontStyle = FontStyle.Bold;
            if (isLineageMatch) titleLabel.color = UIStyles.StatusInfo;  // Highlight text color
            UIFactory.SetLayoutElement(titleLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Details row with quality stats (vote count is now in vote buttons)
            string qualityText = FormatQualityStats(translation);
            var detailsLabel = UIFactory.CreateLabel(infoCol, "Details",
                $"{translation.LineCount} lines | {qualityText}",
                TextAnchor.MiddleLeft);
            detailsLabel.fontSize = UIStyles.FontSizeHint;
            detailsLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(detailsLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Vote buttons (right side)
            var voteButtons = new VoteButtons();
            voteButtons.Create(itemRow, translation.Id, translation.VoteCount, OnVoteChanged);
            voteButtons.SetLoggedIn(isLoggedIn);
            _voteButtons[translation.Id] = voteButtons;
        }

        /// <summary>
        /// Callback when a vote changes - update the translation info.
        /// </summary>
        private void OnVoteChanged(int translationId, int newVoteCount)
        {
            // Find the translation and update its vote count
            for (int i = 0; i < _translations.Count; i++)
            {
                if (_translations[i].Id == translationId)
                {
                    _translations[i].VoteCount = newVoteCount;
                    break;
                }
            }
        }

        /// <summary>
        /// Format quality stats for display (H/V/A counts + score)
        /// </summary>
        private string FormatQualityStats(TranslationInfo translation)
        {
            int total = translation.HumanCount + translation.ValidatedCount + translation.AiCount;
            if (total == 0)
            {
                // Fallback to old Type field if no H/V/A data
                return translation.Type ?? "unknown";
            }

            // Show quality score with label
            float score = translation.QualityScore;
            string label;
            if (score >= 2.5f) label = "Excellent";
            else if (score >= 2.0f) label = "Good";
            else if (score >= 1.5f) label = "Fair";
            else if (score >= 1.0f) label = "Basic";
            else label = "Raw AI";

            return $"H:{translation.HumanCount} V:{translation.ValidatedCount} A:{translation.AiCount} ({label})";
        }

        private void RefreshSelection()
        {
            if (_listContent == null) return;

            // Manual iteration for IL2CPP compatibility (foreach on Transform doesn't work)
            for (int i = 0; i < _listContent.transform.childCount; i++)
            {
                Transform child = _listContent.transform.GetChild(i);
                // Use non-generic GetComponentInChildren for IL2CPP compatibility
                var toggle = child.GetComponentInChildren(typeof(Toggle)) as Toggle;
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
