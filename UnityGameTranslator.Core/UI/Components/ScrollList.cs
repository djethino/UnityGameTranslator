using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A list that scrolls, in its trough, with the sentence it shows when it holds nothing.
    ///
    /// Twelve lists were built by hand — eight in one panel — each with the scroll view, its
    /// sizing, its layout group, its trough colour, its scrollbar, a loop destroying its rows
    /// and a label for "nothing here". Here that is one object.
    /// </summary>
    public sealed class ScrollList
    {
        private readonly GameObject _scroll;
        private readonly GameObject _rows;
        private readonly LabelHandle _empty;

        private ScrollList(GameObject scroll, GameObject rows, LabelHandle empty)
        {
            _scroll = scroll;
            _rows = rows;
            _empty = empty;
        }

        /// <param name="minHeight">The least room it takes — its floor, never squeezed below it.</param>
        /// <param name="preferredHeight">
        /// What it asks for: everything it holds, and never more.
        ///
        /// 🔴 **A ceiling, not a wish.** A list handed room it has nothing to fill draws a gap under
        /// its last row, which is what "elle grandit en montrant du vide plutôt que de se bloquer"
        /// was. Defaults to the minimum, which is right for a list whose content is not known in
        /// rows.
        /// </param>
        /// <param name="fillHeight">
        /// Take the spare room as well. **False whenever this list shares a surface**: Unity divides
        /// the leftover height between flexible children, and two lists both asking for it get half
        /// each whatever they hold — which is how a list of three rows took as much room as the list
        /// of ten beside it. Spare room belongs to a spacer, not to a list.
        /// </param>
        /// <param name="emptyText">Shown alone while the list holds no row; null for no such sentence.</param>
        /// <param name="padding">Room between the trough's edge and its rows, on all four sides.</param>
        public static ScrollList Create(Host parent, string name, int minHeight, int? preferredHeight = null,
                                        bool fillHeight = true, string emptyText = null, int spacing = 5,
                                        int padding = 5)
        {
            var scroll = UIFactory.CreateScrollView(parent.Object, name, out GameObject rows, out _);
            UIFactory.SetLayoutElement(scroll, minHeight: minHeight, preferredHeight: preferredHeight ?? minHeight,
                                       flexibleHeight: fillHeight ? 9999 : 0, flexibleWidth: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(rows, false, false, true, true, spacing,
                                                          padding, padding, padding, padding);

            // The trough, not a field: rows painted the colour of what they sit on vanish into it.
            UIStyles.SetBackground(scroll, UIStyles.TroughBackground);
            UIStyles.ConfigureScrollViewNoScrollbar(scroll);

            LabelHandle empty = null;
            if (emptyText != null)
                empty = Labels.Create(new Host(rows), name + "Empty", emptyText, TextRole.Hint, centred: true);

            return new ScrollList(scroll, rows, empty);
        }

        /// <summary>Where rows go.</summary>
        public Host Rows => new Host(_rows);

        /// <summary>The list itself — to size or place it.</summary>
        public Host Handle => new Host(_scroll);

        /// <summary>
        /// Remove every row and show the empty sentence. Rows are deactivated before being
        /// destroyed: Destroy takes effect at the end of the frame, and a list refilled in the
        /// same breath would lay out the old rows beside the new ones for one frame.
        /// </summary>
        public void Clear()
        {
            if (_rows == null) return;
            var keep = _empty?.Object;
            for (int i = _rows.transform.childCount - 1; i >= 0; i--)
            {
                var child = _rows.transform.GetChild(i).gameObject;
                if (child == keep) continue;
                child.SetActive(false);
                Object.Destroy(child);
            }
            if (_empty != null) _empty.Visible = true;
        }

        /// <summary>Say that rows were added: the empty sentence goes.</summary>
        public void Filled()
        {
            if (_empty != null) _empty.Visible = false;
            ToTop();
        }

        /// <summary>
        /// Puts the list back at its first row.
        ///
        /// 🔴 **A rebuilt list is not the same list, and a ScrollRect does not know that.** It keeps
        /// the offset it had, so a panel redrawn — after a backup, a restore, a resize — opened
        /// already scrolled down, showing the middle of a list nobody had scrolled. Reported as
        /// "les 2 listes scrollées vers le bas".
        ///
        /// ⚠ Vertical only, and set rather than animated: this is not a movement somebody should
        /// see. What they should see is the top of the list they just asked for.
        /// </summary>
        public void ToTop()
        {
            if (_scroll == null) return;

            UniverseLib.RuntimeHelper.StartCoroutine(TopOnceLaidOut());
        }

        /// <summary>
        /// 🔴 **One frame later, or it does nothing at all.** A ScrollRect works its position out
        /// from the size of its content against the size of its viewport, and a list that has just
        /// been filled has neither until the layout has run: the value is written, then recomputed
        /// from what the scroller believes it holds — nothing — and the list stays exactly where it
        /// was. Written straight after the rows were added, this call had no effect for as long as
        /// it existed, and both lists went on opening part-way down.
        /// </summary>
        private System.Collections.IEnumerator TopOnceLaidOut()
        {
            yield return null;

            var rect = _scroll != null ? _scroll.GetComponent<ScrollRect>() : null;
            if (rect == null) yield break;

            // ⚠ Rebuilt first, and both halves of it: the position is worked out from the content's
            // size against the viewport's, and a list whose height was posed this frame has neither
            // until the layout has been asked for them. Without this the value is written against
            // sizes that no longer hold and the list settles wherever its offset lands.
            if (rect.content != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rect.content);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_scroll.GetComponent<RectTransform>());

            rect.verticalNormalizedPosition = 1f;

            // 🔴 **And again on the next frame.** uGUI settles a ScrollRect during its own late
            // pass, after this coroutine has run: the position written here is correct and is then
            // recomputed from the sizes it had before the rebuild. One more frame is what the
            // difference between "put back at the top" and "put back at the top, then dropped"
            // comes down to — reported three times as "tout apparaît scrollé en bas".
            yield return null;

            if (rect != null) rect.verticalNormalizedPosition = 1f;
        }

        /// <summary>The empty sentence, to reword it.</summary>
        public LabelHandle EmptyText => _empty;

        /// <summary>
        /// Revise the room this list claims by itself — for a list whose row count is only known
        /// once it is filled (InspectorPanel's text editor: one row needs a small box, a dozen need
        /// more). Added 2026-09-08; <c>fillHeight</c> from <see cref="Create"/> is left as it was,
        /// so the list still grows to fill whatever this leaves free.
        /// </summary>
        /// <param name="fill">
        /// Whether it still takes whatever room is left over. **False for a list that shares its
        /// surface**: the height it was just given IS its share, and a flexible share on top would
        /// take back the room that was worked out for its neighbour.
        /// </param>
        public void SetHeight(int height, bool fill = true)
        {
            if (_scroll == null) return;
            UIFactory.SetLayoutElement(_scroll, minHeight: height, preferredHeight: height,
                                       flexibleHeight: fill ? 9999 : 0, flexibleWidth: 9999);
        }

        public bool Visible
        {
            get => _scroll != null && _scroll.activeSelf;
            set { if (_scroll != null) _scroll.SetActive(value); }
        }
    }
}
