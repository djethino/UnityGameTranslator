using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Widgets;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Contextual help bar pinned at the bottom of a panel (game-options pattern):
    /// hovering a described control shows its explanation in plain words, without
    /// popups or tooltips that could cover other controls.
    /// The label is a regular registered UI text, so its content follows the
    /// translate_mod_ui option like every other mod text.
    /// </summary>
    public class HelpZone
    {
        // Static routing: HoverCallback publishes instance IDs, help zones own the mapping.
        // Panels live for the whole session (SetActive toggling, never destroyed).
        private static readonly Dictionary<int, HelpZone> zonesByControl = new Dictionary<int, HelpZone>();
        private static readonly Dictionary<int, string> helpTexts = new Dictionary<int, string>();
        private static bool eventsHooked;

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

            HookEvents();
            return _label;
        }

        /// <summary>The root GameObject of the bar (for sibling reordering / visibility).</summary>
        public GameObject Root => _root;

        /// <summary>
        /// Attach a help text to a control: hovering the control shows the text in this zone.
        /// The control needs a raycastable Graphic (buttons, toggles and labels all have one).
        /// </summary>
        public void Describe(GameObject control, string helpText)
        {
            if (control == null || string.IsNullOrEmpty(helpText)) return;

            UIFactory.AddHoverCallback(control);
            int id = control.GetInstanceID();
            zonesByControl[id] = this;
            helpTexts[id] = helpText;
        }

        private static void HookEvents()
        {
            if (eventsHooked) return;
            eventsHooked = true;
            HoverCallback.PointerEntered += OnPointerEntered;
            HoverCallback.PointerExited += OnPointerExited;
        }

        private static void OnPointerEntered(int instanceId)
        {
            if (zonesByControl.TryGetValue(instanceId, out var zone) &&
                helpTexts.TryGetValue(instanceId, out var text))
            {
                zone.SetText(text);
            }
        }

        private static void OnPointerExited(int instanceId)
        {
            if (zonesByControl.TryGetValue(instanceId, out var zone))
            {
                zone.SetText(zone._defaultText);
            }
        }

        private void SetText(string text)
        {
            if (_label != null)
                _label.text = text;
        }
    }
}
