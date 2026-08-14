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

            // 🔴 **The separator bar, which the manager has and this did not.** It is what turns
            // "three pictures and a word" into one control with two parts, and it is the piece that
            // made the two products look like different things. One ecosystem: the same bar, in the
            // same place, in the same colour.
            var bar = UIFactory.CreateUIObject("ScopeMarkBar", buttonObj);
            var barImage = bar.AddComponent<Image>();
            barImage.color = UIStyles.BorderSubtle;
            barImage.raycastTarget = false;
            UIFactory.SetLayoutElement(bar, minWidth: 1, preferredWidth: 1,
                                       minHeight: MarkSize, preferredHeight: MarkSize,
                                       flexibleWidth: 0, flexibleHeight: 0);
            bar.transform.SetSiblingIndex(index);

            // ⚠ **flexibleWidth 0, minWidth 0** — and this is what makes the group CENTRE. Given
            // flexible width the label swells to fill the button, the group becomes the button, and
            // there is nothing left to centre; that is how everything ended up flush left. At its
            // natural width the group is smaller than a wide button and sits in the middle of it,
            // while minWidth 0 still lets it shrink on a narrow one instead of pushing the marks
            // out.
            UIFactory.SetLayoutElement(label.gameObject, minWidth: 0, minHeight: MarkSize,
                                       flexibleWidth: 0, flexibleHeight: 0);

            var text = label.GetComponent<Text>();
            if (text != null) text.alignment = TextAnchor.MiddleLeft;

            // (forceWidth, forceHeight, childControlWidth, childControlHeight, spacing,
            //  padTop, padBottom, padLeft, padRight, childAlignment)
            //
            // 🔴 **Centred, always.** There are two kinds of button — one sized by its content, one
            // stretched to the width of its card — and they take the SAME arrangement: the group is
            // centred, and the only difference is how much room is left around it. Aligning the
            // wide ones left was my reading of "a stack is a list", and it is not what this product
            // does: the manager centres, so this centres.
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(buttonObj, false, false, true, true,
                                                            MarkGap, 2, 2, EdgePad, EdgePad,
                                                            TextAnchor.MiddleCenter);

            // A wider gap on each side of the bar than between the marks, so the three marks read
            // as one object and the bar as what separates them from the word.
            UIFactory.SetLayoutElement(bar, minWidth: 1 + LabelGap, preferredWidth: 1 + LabelGap,
                                       minHeight: MarkSize, preferredHeight: MarkSize,
                                       flexibleWidth: 0, flexibleHeight: 0);
        }
    }
}
