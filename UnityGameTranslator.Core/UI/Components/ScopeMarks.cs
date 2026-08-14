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

        /// <summary>Room between the button's edge and the first mark.</summary>
        private const float EdgePad = 7f;

        /// <summary>The three marks and the gaps between them.</summary>
        private static float MarksBlock => 3f * MarkSize + 2f * MarkGap;

        /// <summary>
        /// One side column. Held on BOTH sides — the left one carries the marks, the right one
        /// stays empty so the label centres on the button rather than on what is left of it.
        /// </summary>
        private static float SlotWidth => EdgePad + MarksBlock + LabelGap;

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

            // 🔴 **THREE SLOTS: marks · label · reserved.** The label keeps the middle and stays
            // centred on the BUTTON; the marks sit in a left slot, right-justified against it; and
            // a slot of the same width is held empty on the right.
            //
            // ⚠ **The empty right slot is the whole point, not padding.** These buttons have fixed
            // widths and are stacked — 250, 200, 80. Centre "marks + label" as one group and every
            // label lands at a different place depending on what is beside it, so a column of
            // buttons has a column of labels that will not line up. Reserving the mirror slot keeps
            // the label on the button's own centre line, so they align down the stack whatever each
            // one carries. It is also where a trailing state would go, without moving anything.
            //
            // ⚠ Deterministic: no text measurement, so it does not depend on the font being ready
            // or on a layout pass that has not happened when a panel builds its card.
            var label = buttonObj.transform.Find("Text");
            if (label == null) return;

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

                // In the left slot, right-justified against the label — the marks belong to the
                // word beside them, not to the button's edge.
                var rect = holder.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0f, 0.5f);
                rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.sizeDelta = new Vector2(MarkSize, MarkSize);
                rect.anchoredPosition =
                    new Vector2(SlotWidth - LabelGap - MarksBlock + index * (MarkSize + MarkGap), 0f);

                index++;
            }

            // The middle column. Inset by the SAME width on both sides, so what the label centres
            // itself on is the button's own centre.
            var labelRect = label.GetComponent<RectTransform>();
            if (labelRect != null)
            {
                labelRect.offsetMin = new Vector2(SlotWidth, labelRect.offsetMin.y);
                labelRect.offsetMax = new Vector2(-SlotWidth, labelRect.offsetMax.y);
            }
        }
    }
}
