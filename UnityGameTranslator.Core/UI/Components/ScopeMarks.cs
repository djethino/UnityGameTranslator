using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// The edit-scope switch shrunk to its three marks, laid inside a button.
    ///
    /// 🔴 **The same control as the manager's `ScopeMark` and the website's compact badge.** Three
    /// marks, in the switch's own order, the one this action aims at lit and the other two dimmed.
    /// A single icon would be a new label to learn; the three, with one lit, are the strip somebody
    /// has already seen beside a panel title read at a glance.
    ///
    /// ⚠ **Inside the button, before the label** — the label names the verb and never the
    /// destination, and Edit and Publish do not aim at the same place. A mark floating above a
    /// button only sits near one.
    ///
    /// ⚠ **No layout group is added to the button.** UniverseLib anchors a button's label to fill
    /// it with padding; dropping a HorizontalLayoutGroup on top would fight that anchoring and move
    /// every label in the product. The marks are placed by RectTransform and the label's left edge
    /// is pushed to make room, which touches this button and nothing else.
    /// </summary>
    public static class ScopeMarks
    {
        private const float MarkSize = 12f;
        private const float MarkGap = 3f;

        /// <summary>Between the last mark and the first letter.</summary>
        private const float LabelGap = 7f;

        /// <summary>The three marks and the gaps between them.</summary>
        private static float MarksBlock => 3f * MarkSize + 2f * MarkGap;

        /// <summary>
        /// Puts the three marks on a button that already exists.
        ///
        /// ⚠ Silent when a mark cannot be built. A game that refuses the texture still gets a
        /// working button with its words; the pictures make the control recognisable elsewhere,
        /// they are not what makes it legible here.
        /// </summary>
        public static void Adorn(ButtonRef button, EditSide side)
        {
            if (button?.Component == null) return;
            Adorn(button.Component.gameObject, side);
        }

        /// <summary>Same, for a button held as a GameObject.</summary>
        public static void Adorn(GameObject buttonObj, EditSide side)
        {
            if (buttonObj == null) return;

            // Built once. Adorning twice would stack a second set of marks over the first, and the
            // panels rebuild their cards on every refresh.
            if (buttonObj.transform.Find("ScopeMark0") != null) return;

            // 🔴 **The marks and the label are ONE centred group.** Pinned to the left edge with the
            // label still centring itself on the whole button, they read as debris stuck to the
            // side of a control whose text has drifted off centre — which is what shipped, and what
            // was reported. The manager centres its whole content by construction; this reproduces
            // that, which is the only reason the two look like the same control.
            var label = buttonObj.transform.Find("Text");
            var labelText = label == null ? null : label.GetComponent<Text>();
            if (labelText == null) return;

            // Measured from the font and the string, without waiting for a layout pass — which has
            // not happened when a panel builds its card.
            float labelWidth = labelText.preferredWidth;
            float total = MarksBlock + LabelGap + labelWidth;

            int index = 0;
            foreach (var standing in EditScope.Sides(hasLocalFile: true, canReachMachine: true,
                                                     signedIn: true, publishedByThisAccount: true,
                                                     publishedBySomebodyElse: false))
            {
                var sprite = Icons.Get(EditScope.Mark(standing.Side));
                if (sprite == null) return;

                bool lit = standing.Side == side;

                var holder = UIFactory.CreateUIObject("ScopeMark" + index, buttonObj);
                var image = holder.AddComponent<Image>();
                image.sprite = sprite;
                image.color = lit ? UIStyles.TextAccent : UIStyles.TextMuted;
                image.preserveAspect = true;

                // ⚠ Never a raycast target. A mark that swallows a click is a button with a dead
                // spot exactly where the eye is drawn.
                image.raycastTarget = false;

                // Anchored to the button's CENTRE, then walked left by half the group: the pair
                // stays centred whatever the button's width turns out to be, where anchoring to the
                // edge made the arrangement depend on it.
                var rect = holder.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.sizeDelta = new Vector2(MarkSize, MarkSize);
                rect.anchoredPosition =
                    new Vector2(-total / 2f + index * (MarkSize + MarkGap), 0f);

                index++;
            }

            // The label keeps filling the button and centring itself; shifting it right by half the
            // marks puts the group's centre back on the button's.
            var labelRect = label.GetComponent<RectTransform>();
            if (labelRect != null)
            {
                float shift = (MarksBlock + LabelGap) / 2f;
                labelRect.offsetMin = new Vector2(labelRect.offsetMin.x + shift, labelRect.offsetMin.y);
                labelRect.offsetMax = new Vector2(labelRect.offsetMax.x + shift, labelRect.offsetMax.y);
            }
        }
    }
}
