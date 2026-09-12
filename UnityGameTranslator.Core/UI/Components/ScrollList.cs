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

        /// <param name="minHeight">The least room it takes.</param>
        /// <param name="fillHeight">Grow with the panel — the list is what should grow when the window does.</param>
        /// <param name="share">
        /// How much of the spare room this list takes when it shares a panel with others.
        ///
        /// 🔴 **Two lists that grow equally are two lists that ignore what is in them.** Unity
        /// divides the leftover height between flexible children in proportion to this number, and
        /// every list asking for the same 9999 gets the same half — so a list of one row was given
        /// as much room as a list of ten beside it, with the first mostly empty and the second
        /// scrolling. Passing the NUMBER OF ROWS makes the split say what the lists hold.
        ///
        /// ⚠ Null keeps the old behaviour — take what there is — which is right for a list that is
        /// alone in its panel and has nobody to share with.
        /// </param>
        /// <param name="emptyText">Shown alone while the list holds no row; null for no such sentence.</param>
        /// <param name="padding">Room between the trough's edge and its rows, on all four sides.</param>
        public static ScrollList Create(Host parent, string name, int minHeight, int? preferredHeight = null,
                                        bool fillHeight = true, string emptyText = null, int spacing = 5,
                                        int padding = 5, int? share = null)
        {
            var scroll = UIFactory.CreateScrollView(parent.Object, name, out GameObject rows, out _);
            UIFactory.SetLayoutElement(scroll, minHeight: minHeight, preferredHeight: preferredHeight ?? minHeight,
                                       flexibleHeight: fillHeight ? (share ?? 9999) : 0, flexibleWidth: 9999);
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
            var rect = _scroll != null ? _scroll.GetComponent<ScrollRect>() : null;
            if (rect == null) return;

            rect.verticalNormalizedPosition = 1f;
        }

        /// <summary>The empty sentence, to reword it.</summary>
        public LabelHandle EmptyText => _empty;

        /// <summary>
        /// Revise the room this list claims by itself — for a list whose row count is only known
        /// once it is filled (InspectorPanel's text editor: one row needs a small box, a dozen need
        /// more). Added 2026-09-08; <c>fillHeight</c> from <see cref="Create"/> is left as it was,
        /// so the list still grows to fill whatever this leaves free.
        /// </summary>
        public void SetHeight(int height)
        {
            if (_scroll == null) return;
            UIFactory.SetLayoutElement(_scroll, minHeight: height, preferredHeight: height,
                                       flexibleHeight: 9999, flexibleWidth: 9999);
        }

        public bool Visible
        {
            get => _scroll != null && _scroll.activeSelf;
            set { if (_scroll != null) _scroll.SetActive(value); }
        }
    }
}
