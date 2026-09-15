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
        /// 🔴 **Written as an OFFSET, not as a normalised position** (2026-09-15). The content of a
        /// scroll view is pinned to the top of its viewport (pivot and anchors at the top, see
        /// UIFactory.CreateScrollView), so an offset of zero IS the first row — whatever the content
        /// and the viewport measure, now or once the layout has run. A normalised position is
        /// worked out from those two sizes, and written before they were current it landed
        /// somewhere else: that is what three attempts with coroutines and forced rebuilds were
        /// chasing, and it is why the lists went on opening part-way down.
        ///
        /// ⚠ Vertical only, and set rather than animated: this is not a movement somebody should
        /// see. What they should see is the top of the list they just asked for.
        /// </summary>
        public void ToTop()
        {
            var content = ContentRect;
            if (content == null) return;

            content.anchoredPosition = new Vector2(content.anchoredPosition.x, 0f);
        }

        /// <summary>
        /// Whether the list shows its first row — the same offset <see cref="ToTop"/> writes, read
        /// back. A screen that poses this list's height asks it, so that a list nobody has scrolled
        /// stays at its first row through a resize while one somebody is reading is left alone.
        /// </summary>
        public bool AtTop
        {
            get
            {
                var content = ContentRect;
                return content == null || content.anchoredPosition.y <= 0.5f;
            }
        }

        /// <summary>
        /// What its rows come to, laid out at the current width, padding included — the height
        /// this list would need to show everything without scrolling.
        ///
        /// ⚠ **Measured, never added up from a row height.** A row here is one, two or four lines
        /// tall depending on what it has to say, so a declared figure per row either cut a list
        /// short (it scrolled with room to spare beside it) or handed it a band of empty trough.
        /// Asked once the layout has run — it forces the rows' own layout so the answer is current
        /// at whatever width the panel has this frame.
        /// </summary>
        public float ContentHeight
        {
            get
            {
                var content = ContentRect;
                if (content == null) return 0f;

                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
                return LayoutUtility.GetPreferredHeight(content);
            }
        }

        private RectTransform ContentRect
        {
            get
            {
                var rect = _scroll != null ? _scroll.GetComponent<ScrollRect>() : null;
                return rect != null ? rect.content : null;
            }
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
