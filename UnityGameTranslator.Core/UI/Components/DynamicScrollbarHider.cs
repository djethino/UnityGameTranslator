using UnityEngine;
using UnityEngine.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Monitors scroll view content and dynamically hides/shows scrollbar.
    /// When content fits in viewport, hides scrollbar and expands viewport to full width.
    /// When content overflows, shows scrollbar and shrinks viewport by 28px.
    /// </summary>
    public class DynamicScrollbarHider : MonoBehaviour
    {
        private const float ScrollbarWidth = 28f;

        private ScrollRect _scrollRect;
        private RectTransform _viewport;
        private RectTransform _content;
        private GameObject _scrollbarObj;

        private bool _scrollbarVisible = true;
        private float _lastContentHeight;
        private float _lastViewportHeight;

        private void Awake()
        {
            _scrollRect = GetComponent<ScrollRect>();
            if (_scrollRect == null) return;

            _viewport = transform.Find("Viewport")?.GetComponent<RectTransform>();
            _content = _scrollRect.content;
            _scrollbarObj = transform.Find("AutoSliderScrollbar")?.gameObject;
        }

        private void LateUpdate()
        {
            if (_scrollRect == null || _viewport == null || _content == null) return;

            float contentHeight = _content.rect.height;
            float viewportHeight = _viewport.rect.height;

            // Only update if heights changed (optimization)
            if (Mathf.Approximately(contentHeight, _lastContentHeight) &&
                Mathf.Approximately(viewportHeight, _lastViewportHeight))
            {
                return;
            }

            _lastContentHeight = contentHeight;
            _lastViewportHeight = viewportHeight;

            // Determine if scrollbar is needed
            // Add small buffer to avoid flickering at boundary
            bool needsScrollbar = contentHeight > viewportHeight + 5f;

            if (needsScrollbar != _scrollbarVisible)
            {
                _scrollbarVisible = needsScrollbar;
                UpdateScrollbarVisibility();
            }
        }

        private void UpdateScrollbarVisibility()
        {
            if (_scrollbarObj != null)
            {
                _scrollbarObj.SetActive(_scrollbarVisible);
            }

            if (_viewport != null)
            {
                // Adjust viewport width based on scrollbar visibility
                // UniverseLib sets offsetMax.x = -28 by default
                _viewport.offsetMax = new Vector2(_scrollbarVisible ? -ScrollbarWidth : 0f, 0f);
            }
        }

        private void OnEnable()
        {
            // Force refresh on enable
            _lastContentHeight = -1;
            _lastViewportHeight = -1;
        }
    }
}
