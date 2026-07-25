using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Contextual help bar pinned at the bottom of a panel (game-options pattern):
    /// hovering a described control shows its explanation in plain words, without
    /// popups or tooltips that could cover other controls.
    /// The label is a regular registered UI text, so its content follows the
    /// translate_mod_ui option like every other mod text.
    ///
    /// Hover detection is done by a per-frame geometric poll (RectangleContainsScreenPoint)
    /// driven from TranslatorUIManager.UpdateUI, NOT by pointer-enter events. The event
    /// route (injected IPointerEnterHandler) only fires on Mono: on IL2CPP the EventSystem
    /// never dispatches to an injected interface handler, so a single native poll is the
    /// only mechanism that behaves identically on both runtimes.
    /// </summary>
    public class HelpZone
    {
        /// <summary>A described control: its rect, owning canvas (for camera resolution) and help.</summary>
        private class Entry
        {
            public RectTransform Rect;
            public Canvas Canvas;
            public HelpZone Zone;
            public string Text;
        }

        // Static registry: panels live for the whole session (SetActive toggling, never destroyed),
        // so entries persist. Keyed by the control's instance ID.
        private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private static int _currentId;

        private GameObject _root;
        private Text _label;
        private string _defaultText;

        /// <summary>
        /// Create the help bar inside <paramref name="parent"/>. Returns the label so the
        /// panel can RegisterUIText it. Use SetSiblingIndex to pin it above a footer.
        /// </summary>
        public Text CreateUI(GameObject parent, string defaultText = "")
        {
            _defaultText = defaultText ?? "";

            _root = UIFactory.CreateVerticalGroup(parent, "HelpZone", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_root, minHeight: UIStyles.MultiLineSmall, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.SetBackground(_root, UIStyles.ItemBackground);
            var layout = _root.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = Compat.MakeRectOffset(8, 8, 4, 4);
                layout.childAlignment = TextAnchor.MiddleLeft;
            }

            _label = UIFactory.CreateLabel(_root, "HelpZoneLabel", _defaultText, TextAnchor.MiddleLeft);
            _label.fontSize = UIStyles.FontSizeSmall;
            _label.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_label.gameObject, flexibleWidth: 9999, minHeight: UIStyles.MultiLineSmall - 8);

            return _label;
        }

        /// <summary>The root GameObject of the bar (for sibling reordering / visibility).</summary>
        public GameObject Root => _root;

        /// <summary>
        /// Attach a help text to a control: hovering the control shows the text in this zone.
        /// The control needs a RectTransform (every uGUI element has one).
        /// </summary>
        public void Describe(GameObject control, string helpText)
        {
            if (control == null || string.IsNullOrEmpty(helpText)) return;

            var rect = control.GetComponent<RectTransform>();
            if (rect == null) return;

            Canvas canvas = rect.GetComponentInParent<Canvas>();
            if (canvas != null) canvas = canvas.rootCanvas;

            int id = control.GetInstanceID();
            entries[id] = new Entry
            {
                Rect = rect,
                Canvas = canvas,
                Zone = this,
                Text = helpText,
            };
        }

        /// <summary>
        /// Per-frame hover resolution. Finds the top-most (deepest in the hierarchy) described
        /// control under the pointer and shows its help in the owning zone; resets the previous
        /// zone to its default when the hovered control changes. Native APIs only → IL2CPP-safe.
        /// Called from TranslatorUIManager.UpdateUI while at least one panel is visible.
        /// </summary>
        public static void PollHover()
        {
            if (entries.Count == 0) return;

            Vector3 mp = UniverseLib.Input.InputManager.MousePosition;
            Vector2 screen = new Vector2(mp.x, mp.y);

            int bestId = 0;
            int bestDepth = -1;
            Entry best = null;

            foreach (var kv in entries)
            {
                Entry e = kv.Value;
                if (e.Rect == null || !e.Rect.gameObject.activeInHierarchy) continue;

                if (!ContainsScreenPoint(e.Rect, e.Canvas, screen)) continue;

                // Deepest match wins (a described child drawn on top of a described container).
                int depth = HierarchyDepth(e.Rect);
                if (depth > bestDepth)
                {
                    bestDepth = depth;
                    bestId = kv.Key;
                    best = e;
                }
            }

            if (bestId == _currentId) return;

            // Leaving the previous control: restore its zone's default text.
            if (_currentId != 0 && entries.TryGetValue(_currentId, out Entry prev) && prev.Zone != null)
                prev.Zone.SetText(prev.Zone._defaultText);

            _currentId = bestId;

            if (best != null && best.Zone != null)
                best.Zone.SetText(best.Text);
        }

        /// <summary>
        /// IL2CPP-safe screen-point containment: builds the rect's screen-space AABB from
        /// TransformPoint corners (never GetWorldCorners/RectTransformUtility — those take an
        /// array param that becomes an Il2CppStructArray and pulls Il2Cppmscorlib / crashes).
        /// Exact for unrotated uGUI controls, which is all of ours. Same pattern as
        /// InspectorPanel.GetScreenBounds.
        /// </summary>
        private static bool ContainsScreenPoint(RectTransform rect, Canvas canvas, Vector2 screen)
        {
            Rect lr = rect.rect;
            Vector3 c0 = rect.TransformPoint(new Vector3(lr.xMin, lr.yMin, 0f));
            Vector3 c1 = rect.TransformPoint(new Vector3(lr.xMin, lr.yMax, 0f));
            Vector3 c2 = rect.TransformPoint(new Vector3(lr.xMax, lr.yMax, 0f));
            Vector3 c3 = rect.TransformPoint(new Vector3(lr.xMax, lr.yMin, 0f));

            // Overlay canvases: world coords ARE screen pixels. Otherwise project via the camera.
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                Camera cam = canvas.worldCamera;
                if (cam != null)
                {
                    c0 = cam.WorldToScreenPoint(c0);
                    c1 = cam.WorldToScreenPoint(c1);
                    c2 = cam.WorldToScreenPoint(c2);
                    c3 = cam.WorldToScreenPoint(c3);
                }
            }

            float minX = Mathf.Min(Mathf.Min(c0.x, c1.x), Mathf.Min(c2.x, c3.x));
            float maxX = Mathf.Max(Mathf.Max(c0.x, c1.x), Mathf.Max(c2.x, c3.x));
            float minY = Mathf.Min(Mathf.Min(c0.y, c1.y), Mathf.Min(c2.y, c3.y));
            float maxY = Mathf.Max(Mathf.Max(c0.y, c1.y), Mathf.Max(c2.y, c3.y));

            return screen.x >= minX && screen.x <= maxX && screen.y >= minY && screen.y <= maxY;
        }

        private static int HierarchyDepth(Transform t)
        {
            int d = 0;
            Transform p = t.parent;
            while (p != null) { d++; p = p.parent; }
            return d;
        }

        private void SetText(string text)
        {
            if (_label != null)
                _label.text = text;
        }
    }
}
