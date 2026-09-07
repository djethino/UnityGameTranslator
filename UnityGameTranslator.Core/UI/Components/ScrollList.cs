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
