using UnityEngine;
using UnityEngine.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// The two things a fixed-position overlay still needs from the engine: which corner it hangs
    /// from, and how big it is right now. Everything else about it is ordinary vocabulary — this
    /// exists so a panel that pins itself to a corner never has to write a Vector2 to do it.
    /// </summary>
    public static class Overlays
    {
        /// <summary>
        /// Anchors, pivot and offset for one of the four screen corners — the notification
        /// overlay's position, as chosen in Options. Silent if the host carries no RectTransform.
        /// </summary>
        public static void PinToCorner(Host panelRoot, string corner)
        {
            var rect = panelRoot?.Object?.GetComponent<RectTransform>();
            if (rect == null) return;

            float anchorX, anchorY, pivotX, pivotY, posX, posY;

            switch (corner)
            {
                case "top-left":
                    anchorX = 0f; anchorY = 1f;
                    pivotX = 0f; pivotY = 1f;
                    posX = 10f; posY = -10f;
                    break;
                case "bottom-right":
                    anchorX = 1f; anchorY = 0f;
                    pivotX = 1f; pivotY = 0f;
                    posX = -10f; posY = 10f;
                    break;
                case "bottom-left":
                    anchorX = 0f; anchorY = 0f;
                    pivotX = 0f; pivotY = 0f;
                    posX = 10f; posY = 10f;
                    break;
                default: // "top-right"
                    anchorX = 1f; anchorY = 1f;
                    pivotX = 1f; pivotY = 1f;
                    posX = -10f; posY = -10f;
                    break;
            }

            rect.anchorMin = new Vector2(anchorX, anchorY);
            rect.anchorMax = new Vector2(anchorX, anchorY);
            rect.pivot = new Vector2(pivotX, pivotY);
            rect.anchoredPosition = new Vector2(posX, posY);
        }

        /// <summary>Resize the panel itself to (width, height) pixels. Silent under the same condition.</summary>
        public static void SetSize(Host panelRoot, int width, int height)
        {
            var rect = panelRoot?.Object?.GetComponent<RectTransform>();
            if (rect == null) return;
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
