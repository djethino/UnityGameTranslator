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
    /// has already seen beside a panel title, read at a glance.
    ///
    /// ⚠ **A row that hugs its content — the ordinary button pattern.** Two earlier attempts placed
    /// the marks by hand: pinned to the left edge while the label went on centring itself on the
    /// whole button (two reference frames, so nothing lined up), then three equal columns with the
    /// side ones reserved. That second one is a TITLE BAR pattern, not a button one: it keeps a
    /// title optically centred whatever sits beside it, at the price of a short label floating far
    /// from its icons — which is exactly what it did. A button is an inline group of an icon and a
    /// word, sized by them, and the layout group does the arithmetic nobody should be writing.
    /// </summary>
    public static class ScopeMarks
    {
        private const int MarkSize = 12;
        private const int MarkGap = 3;

        /// <summary>Between the last mark and the first letter.</summary>
        private const int LabelGap = 7;

        /// <summary>Room between the button's edge and what it holds.</summary>
        private const int EdgePad = 8;

        /// <summary>
        /// Puts the three marks on a button that already exists.
        /// </summary>
        /// <param name="centred">
        /// ⚠ **Left by default, and that is the case that matters here.** These buttons are full
        /// width and STACKED — a column of them is a list, and a list aligns left, as every
        /// settings screen does. Centring is for a button standing alone or sharing a row, where
        /// there is nothing above and below for its label to line up with.
        /// </param>
        public static void Adorn(ButtonRef button, EditSide side, bool centred = false)
        {
            if (button?.Component == null) return;
            Adorn(button.Component.gameObject, side, centred);
        }

        /// <summary>Same, for a button held as a GameObject.</summary>
        public static void Adorn(GameObject buttonObj, EditSide side, bool centred = false)
        {
            if (buttonObj == null) return;

            // Built once. Adorning twice would stack a second set of marks over the first, and the
            // panels rebuild their cards on every refresh.
            if (buttonObj.transform.Find("ScopeMark0") != null) return;

            var label = buttonObj.transform.Find("Text");
            if (label == null) return;

            // ⚠ **The library anchors a button's label to FILL its button**, which is why the two
            // previous attempts worked around it with hand-placed rectangles instead of fixing the
            // structure. A layout group cannot drive a child that anchors itself, so the anchoring
            // is undone here — on this button only — and the group takes over.
            var labelRect = label.GetComponent<RectTransform>();
            if (labelRect != null)
            {
                labelRect.anchorMin = new Vector2(0.5f, 0.5f);
                labelRect.anchorMax = new Vector2(0.5f, 0.5f);
                labelRect.pivot = new Vector2(0.5f, 0.5f);
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
            }

            // The marks are built BEFORE the row is arranged, and inserted before the label: the
            // group lays its children out in sibling order, and the label already exists.
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

                // A fixed square that neither grows nor shrinks: the label is the only thing with
                // any give, so a long one shortens itself instead of pushing the marks out.
                UIFactory.SetLayoutElement(holder, minWidth: MarkSize, minHeight: MarkSize,
                                           preferredWidth: MarkSize, preferredHeight: MarkSize,
                                           flexibleWidth: 0, flexibleHeight: 0);

                holder.transform.SetSiblingIndex(index);
                index++;
            }

            // The last mark is followed by a wider gap than the marks have between them, so the
            // three read as one object rather than four things evenly spaced.
            UIFactory.SetLayoutElement(label.gameObject, minHeight: MarkSize,
                                       flexibleWidth: 9999, flexibleHeight: 0);

            var text = label.GetComponent<Text>();
            if (text != null) text.alignment = centred ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft;

            // (forceWidth, forceHeight, childControlWidth, childControlHeight, spacing,
            //  padTop, padBottom, padLeft, padRight, childAlignment)
            //
            // ⚠ forceExpandWidth FALSE: everything sits at its own width, and only the label was
            // given any give — so a long label is what shortens, never the marks that get pushed
            // out of the button.
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(buttonObj, false, false, true, true,
                                                            MarkGap, 2, 2, EdgePad, EdgePad,
                                                            centred ? TextAnchor.MiddleCenter
                                                                    : TextAnchor.MiddleLeft);

            // The gap before the word, added to the last mark rather than to the group's spacing so
            // the marks stay tight against each other.
            var lastMark = buttonObj.transform.Find("ScopeMark" + (index - 1));
            if (lastMark != null)
            {
                UIFactory.SetLayoutElement(lastMark.gameObject,
                                           minWidth: MarkSize + LabelGap - MarkGap,
                                           preferredWidth: MarkSize + LabelGap - MarkGap,
                                           flexibleWidth: 0);
            }
        }
    }
}
