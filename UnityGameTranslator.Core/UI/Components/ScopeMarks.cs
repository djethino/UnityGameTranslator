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

        /// <summary>Names the empty child that remembers which side this button aims at.</summary>
        private const string SideRecordPrefix = "ScopeSide";

        /// <summary>
        /// Which side a button was adorned for, read from the button itself.
        ///
        /// False when it was never adorned — a caller tinting an ordinary button, which must do
        /// nothing rather than guess a side and light a mark that is not there.
        /// </summary>
        private static bool TryReadSide(GameObject buttonObj, out EditSide side)
        {
            foreach (EditSide candidate in System.Enum.GetValues(typeof(EditSide)))
            {
                if (buttonObj.transform.Find(SideRecordPrefix + candidate) != null)
                {
                    side = candidate;
                    return true;
                }
            }

            side = EditSide.Local;
            return false;
        }

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

            // 🔴 **The side is RECORDED on the button, not repeated by every caller.** It used to
            // be passed to Adorn and then again to Tint, at thirteen call sites — and when four
            // buttons were reclassified, the Adorn calls were fixed and the Tint calls were not.
            // Nothing would have said so: the button would simply light a different mark the next
            // time anything refreshed it.
            //
            // ⚠ An empty object, ignored by the layout. There is no MonoBehaviour to hang it on —
            // the Core is compiled once for Mono and IL2CPP, where a custom component would have
            // to be registered — so the hierarchy is the only place left that belongs to this
            // button and survives with it.
            var record = UIFactory.CreateUIObject(SideRecordPrefix + side, buttonObj);
            var ignored = record.AddComponent<LayoutElement>();
            ignored.ignoreLayout = true;

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
                image.color = lit ? UIStyles.MarkLit : UIStyles.TextMuted;
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
            // ⚠ **The line is a CHILD of a transparent slot, not the slot itself.** Put the Image on
            // the slot and it fills whatever width the slot was given — which is how a one-pixel
            // rule shipped as an eight-pixel grey rectangle. The slot carries the gaps; only its
            // child is painted.
            var bar = UIFactory.CreateUIObject("ScopeMarkBar", buttonObj);
            UIFactory.SetLayoutElement(bar, minWidth: 1 + 2 * LabelGap, preferredWidth: 1 + 2 * LabelGap,
                                       minHeight: MarkSize, preferredHeight: MarkSize,
                                       flexibleWidth: 0, flexibleHeight: 0);
            bar.transform.SetSiblingIndex(index);

            var rule = UIFactory.CreateUIObject("ScopeMarkRule", bar);
            var barImage = rule.AddComponent<Image>();
            barImage.color = UIStyles.BorderSubtle;
            barImage.raycastTarget = false;

            var ruleRect = rule.GetComponent<RectTransform>();
            ruleRect.anchorMin = new Vector2(0.5f, 0.5f);
            ruleRect.anchorMax = new Vector2(0.5f, 0.5f);
            ruleRect.pivot = new Vector2(0.5f, 0.5f);
            ruleRect.sizeDelta = new Vector2(1f, MarkSize);
            ruleRect.anchoredPosition = Vector2.zero;

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

            // The side is already recorded above, so this reads it back like every other caller.
            var selectable = buttonObj.GetComponent<Button>();
            Tint(buttonObj, selectable == null || selectable.interactable);
        }

        /// <summary>
        /// Recolours an adorned button for the state it is in — call it wherever `interactable`
        /// is set.
        ///
        /// 🔴 **The colours are NOT decided once at build time**, which is what shipped: the lit
        /// mark stayed accent-purple on a button nobody could press, so a dead control was the
        /// brightest thing on the card. Unity greys a Button's own Image through its ColorBlock and
        /// leaves every child alone — the marks and the label are children.
        ///
        /// ⚠ The label is dimmed too, and that was the other half of the report: a disabled button
        /// kept text as white as a live one, so it was told apart only by a slightly different grey
        /// behind it. Text carries far more weight than a background shade.
        /// </summary>
        public static void Tint(ButtonRef button, bool interactable)
        {
            if (button?.Component == null) return;
            Tint(button.Component.gameObject, interactable);
        }

        /// <summary>Same, for a button held as a GameObject.</summary>
        public static void Tint(GameObject buttonObj, bool interactable)
        {
            if (buttonObj == null) return;

            // ⚠ Read back from the button, never taken from the caller. See Adorn: the side used to
            // be repeated here and drifted from what was built the day four buttons changed side.
            EditSide side;
            if (!TryReadSide(buttonObj, out side)) return;

            int index = 0;
            foreach (var standing in EditScope.Sides(hasLocalFile: true, canReachMachine: true,
                                                     signedIn: true, publishedByThisAccount: true,
                                                     publishedBySomebodyElse: false))
            {
                var mark = buttonObj.transform.Find("ScopeMark" + index);
                index++;

                var image = mark == null ? null : mark.GetComponent<Image>();
                if (image == null) continue;

                bool lit = standing.Side == side;

                // Disabled, the lit one keeps its rank by being LIGHTER than the other two rather
                // than a different hue: which side the action aims at is still worth knowing while
                // you read why you cannot take it.
                image.color = lit
                    ? (interactable ? UIStyles.MarkLit : UIStyles.TextSecondary)
                    : UIStyles.TextMuted;
            }

            var rule = buttonObj.transform.Find("ScopeMarkBar/ScopeMarkRule");
            var ruleImage = rule == null ? null : rule.GetComponent<Image>();
            if (ruleImage != null) ruleImage.color = UIStyles.BorderSubtle;

            // ⚠ The label is NOT touched here any more. It used to be, back when a disabled button
            // kept white text and only the seven marked buttons had been fixed; that now happens
            // for every themed button, from UIStyles.SetBackground through ButtonStates. Two places
            // writing one colour is how they end up disagreeing.
        }
    }
}
