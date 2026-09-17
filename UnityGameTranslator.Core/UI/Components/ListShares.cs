using System;
using System.Collections.Generic;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Several scrolling lists in one body, sharing its height by the socle's rule
    /// (<see cref="ListRooms"/>): each list states what it holds and the least it can be shown
    /// in, the chrome around them is MEASURED, and the layout divides what is left.
    ///
    /// 🔴 **Extracted from the Backups screen (2026-09-17) the day a second screen needed it** —
    /// the Failures tab, with a list of lines, the game text and the proposal each in a scroll
    /// area of its own. The rule was in the socle; the arbitration was in one panel, and copying
    /// it would have made two arithmetics to keep equal. What the Backups screen learnt stays
    /// written on its own methods; what it DOES is here.
    ///
    /// The contract, unchanged from there:
    ///  · a list is registered at a PROVISIONAL floor (<see cref="Add"/>) so the body never asks
    ///    for more than itself before the first measure;
    ///  · <see cref="Share"/> poses the real heights from a body that has been laid out — asked
    ///    on every resize and after every content change, never guessed at draw time;
    ///  · <see cref="Floor"/> and <see cref="Ceiling"/> are what the window would need with every
    ///    list at its least rows, and with every list showing everything — for a window that
    ///    sizes itself on its lists, as the Backups screen does. A tab in a fixed window ignores them.
    /// </summary>
    public sealed class ListShares
    {
        /// <summary>One list and what it was last given: the height is not settled when the list is built.</summary>
        private sealed class Slice
        {
            public ScrollList List;

            /// <summary>How many rows it holds NOW — a count, or lines of text measured at the current width.</summary>
            public Func<int> Rows;

            /// <summary>What it was last given — what the measured chrome is worked out against.</summary>
            public int Given;
        }

        private readonly List<Slice> _slices = new List<Slice>();

        /// <summary>Whether the lists have been given their height since they were registered.</summary>
        private bool _shared;

        /// <summary>The window's height with every list at its least rows, chrome included. Zero until measured.</summary>
        public int Floor { get; private set; }

        /// <summary>The window's height with every list showing everything, chrome included. Zero until measured.</summary>
        public int Ceiling { get; private set; }

        public bool Any => _slices.Count > 0;

        /// <summary>Before a redraw rebuilds the lists: what was registered is not the same list any more.</summary>
        public void Forget()
        {
            _slices.Clear();
            _shared = false;
        }

        /// <summary>
        /// Registers a list at a provisional floor: <see cref="ListRooms.LeastRows"/> rows of
        /// <paramref name="rowSpace"/> plus <paramref name="pad"/>, and no flexible share, so the
        /// sum of what the body asks for stays under the body itself until <see cref="Share"/>.
        /// </summary>
        public void Add(ScrollList list, Func<int> rows, int rowSpace, int pad)
        {
            if (list == null || rows == null) return;
            int floor = (int)ListRooms.For(Math.Max(1, rows()), rowSpace, pad).Least;
            list.SetHeight(floor, fill: false);
            _slices.Add(new Slice { List = list, Rows = rows, Given = floor });
        }

        /// <summary>
        /// What the body would ask for with every list showing everything it holds — what a
        /// window opens at when it sizes itself on its lists. Zero before there is a body.
        /// </summary>
        public float ContentWhole(float bodyContentHeight)
        {
            if (_slices.Count == 0 || bodyContentHeight <= 0f) return 0f;

            double given = 0, whole = 0;
            for (var i = 0; i < _slices.Count; i++)
            {
                given += _slices[i].Given;
                whole += _slices[i].List.ContentHeight;
            }
            return (float)(bodyContentHeight - given + whole);
        }

        /// <summary>
        /// Poses the heights from a MEASURED body.
        ///
        /// The chrome — every heading, sentence, field and button the body carries that is not a
        /// list — is the difference between what the content asks for and what the lists were
        /// last given; nothing about it is written down. <paramref name="around"/> is what the
        /// window carries around its body (title bar, header, footer), for the floor and ceiling
        /// only. False when there was nothing to measure: no list, or no body yet.
        /// </summary>
        public bool Share(float bodyHeight, float bodyContentHeight, float around)
        {
            if (_slices.Count == 0 || bodyHeight <= 1f) return false;

            double given = 0;
            for (var i = 0; i < _slices.Count; i++) given += _slices[i].Given;

            var chrome = bodyContentHeight - given;

            var rooms = new List<ListRoom>();
            double least = 0, whole = 0;
            for (var i = 0; i < _slices.Count; i++)
            {
                var content = _slices[i].List.ContentHeight;
                int rows = Math.Max(1, _slices[i].Rows());
                var room = ListRooms.Of(content, rows, content / rows);
                rooms.Add(room);
                least += room.Least;
                whole += room.Whole;
            }

            Floor = (int)Math.Ceiling(around + chrome + least);
            Ceiling = (int)Math.Ceiling(around + chrome + whole);

            var heights = ListRooms.Share(rooms, bodyHeight - chrome);

            for (var i = 0; i < _slices.Count; i++)
            {
                // 🔴 **A list nobody has scrolled stays at its first row; one somebody is reading
                // is left alone.** Read before the height is posed, and put back after: a rebuilt
                // list is put back whatever it showed, since it is not the same list.
                var atTop = _slices[i].List.AtTop;

                _slices[i].Given = (int)Math.Max(0, heights[i]);
                _slices[i].List.SetHeight(_slices[i].Given, fill: false);

                if (!_shared || atTop) _slices[i].List.ToTop();
            }

            _shared = true;
            return true;
        }
    }
}
