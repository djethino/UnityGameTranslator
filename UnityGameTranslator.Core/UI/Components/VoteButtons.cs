using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Reusable vote buttons component (upvote/downvote).
    /// </summary>
    public class VoteButtons
    {
        private GameObject _root;
        private Button _upButton;
        private Button _downButton;
        private Text _upText;
        private Text _downText;
        private Text _countText;

        private int _translationId;
        private int _currentVoteCount;
        private int? _userVote;
        private bool _isVoting;

        // Callback when vote changes (translationId, newVoteCount)
        private Action<int, int> _onVoteChanged;

        /// <summary>
        /// Create the vote buttons UI.
        /// </summary>
        public GameObject Create(GameObject parent, int translationId, int voteCount, Action<int, int> onVoteChanged = null)
        {
            _translationId = translationId;
            _currentVoteCount = voteCount;
            _userVote = null;
            _onVoteChanged = onVoteChanged;

            // Container
            _root = UIFactory.CreateHorizontalGroup(parent, "VoteButtons", false, false, true, true, 2);
            UIFactory.SetLayoutElement(_root, minWidth: 80, minHeight: 24);

            var layout = _root.GetComponent<HorizontalLayoutGroup>();
            if (layout != null)
            {
                layout.childAlignment = TextAnchor.MiddleCenter;
            }

            // Upvote button
            var upRef = UIFactory.CreateButton(_root, "UpButton", "▲");
            _upButton = upRef.Component;
            _upText = upRef.GameObject.GetComponentInChildren<Text>();
            if (_upText != null) _upText.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(upRef.GameObject, minWidth: 24, minHeight: 24);
            UIHelpers.AddButtonListener(_upButton, OnUpvoteClick);

            // Vote count
            _countText = UIFactory.CreateLabel(_root, "VoteCount", FormatVoteCount(voteCount), TextAnchor.MiddleCenter);
            _countText.fontSize = UIStyles.FontSizeSmall;
            _countText.fontStyle = FontStyle.Bold;
            UIFactory.SetLayoutElement(_countText.gameObject, minWidth: 30);
            UpdateCountColor();

            // Downvote button
            var downRef = UIFactory.CreateButton(_root, "DownButton", "▼");
            _downButton = downRef.Component;
            _downText = downRef.GameObject.GetComponentInChildren<Text>();
            if (_downText != null) _downText.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(downRef.GameObject, minWidth: 24, minHeight: 24);
            UIHelpers.AddButtonListener(_downButton, OnDownvoteClick);

            UpdateButtonStyles();

            return _root;
        }

        /// <summary>
        /// Update the vote count display.
        /// </summary>
        public void UpdateVoteCount(int newCount, int? userVote = null)
        {
            _currentVoteCount = newCount;
            if (userVote.HasValue)
            {
                _userVote = userVote;
            }

            if (_countText != null)
            {
                _countText.text = FormatVoteCount(newCount);
                UpdateCountColor();
            }

            UpdateButtonStyles();
        }

        /// <summary>
        /// Set whether the user is logged in (enables/disables buttons).
        /// </summary>
        public void SetLoggedIn(bool isLoggedIn)
        {
            if (_upButton != null) _upButton.interactable = isLoggedIn;
            if (_downButton != null) _downButton.interactable = isLoggedIn;
        }

        private void OnUpvoteClick()
        {
            if (_isVoting) return;
            _ = VoteAsync(1);
        }

        private void OnDownvoteClick()
        {
            if (_isVoting) return;
            _ = VoteAsync(-1);
        }

        private async Task VoteAsync(int value)
        {
            _isVoting = true;

            // Disable buttons during vote
            if (_upButton != null) _upButton.interactable = false;
            if (_downButton != null) _downButton.interactable = false;

            try
            {
                var result = await ApiClient.Vote(_translationId, value);

                if (result.Success)
                {
                    _currentVoteCount = result.VoteCount;
                    _userVote = result.UserVote;

                    if (_countText != null)
                    {
                        _countText.text = FormatVoteCount(_currentVoteCount);
                        UpdateCountColor();
                    }

                    UpdateButtonStyles();

                    // Notify parent
                    _onVoteChanged?.Invoke(_translationId, _currentVoteCount);

                    TranslatorCore.LogInfo($"[VoteButtons] Vote successful: {_translationId} -> {_currentVoteCount}");
                }
                else
                {
                    TranslatorCore.LogWarning($"[VoteButtons] Vote failed: {result.Error}");
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[VoteButtons] Vote error: {e.Message}");
            }
            finally
            {
                _isVoting = false;

                // Re-enable buttons
                bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config?.api_token);
                if (_upButton != null) _upButton.interactable = isLoggedIn;
                if (_downButton != null) _downButton.interactable = isLoggedIn;
            }
        }

        private void UpdateButtonStyles()
        {
            if (_upButton == null || _downButton == null) return;

            // Upvote button: green if voted up
            var upColors = _upButton.colors;
            if (_userVote == 1)
            {
                upColors.normalColor = UIStyles.StatusSuccess;
                upColors.highlightedColor = UIStyles.StatusSuccess;
                if (_upText != null) _upText.color = Color.white;
            }
            else
            {
                upColors.normalColor = UIStyles.ButtonSecondary;
                upColors.highlightedColor = UIStyles.ButtonHover;
                if (_upText != null) _upText.color = UIStyles.TextPrimary;
            }
            _upButton.colors = upColors;

            // Downvote button: red if voted down
            var downColors = _downButton.colors;
            if (_userVote == -1)
            {
                downColors.normalColor = UIStyles.StatusError;
                downColors.highlightedColor = UIStyles.StatusError;
                if (_downText != null) _downText.color = Color.white;
            }
            else
            {
                downColors.normalColor = UIStyles.ButtonSecondary;
                downColors.highlightedColor = UIStyles.ButtonHover;
                if (_downText != null) _downText.color = UIStyles.TextPrimary;
            }
            _downButton.colors = downColors;
        }

        private void UpdateCountColor()
        {
            if (_countText == null) return;

            if (_currentVoteCount > 0)
                _countText.color = UIStyles.StatusSuccess;
            else if (_currentVoteCount < 0)
                _countText.color = UIStyles.StatusError;
            else
                _countText.color = UIStyles.TextMuted;
        }

        private string FormatVoteCount(int count)
        {
            if (count > 0) return $"+{count}";
            return count.ToString();
        }

        /// <summary>
        /// Show or hide the vote buttons.
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
