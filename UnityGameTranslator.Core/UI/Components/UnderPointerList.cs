using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.Input;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Everything under the pointer, as a list opened where somebody clicked — hover a line to see
    /// that object, click it to take it (user, 2026-10-22: « une liste qui s'affiche sous la souris
    /// quand on click à un endroit et qui met les différents objets sous la souris »). The inspector
    /// keeps the nearest thing only, and a big object in front — an open book's box — hid the texts
    /// lying on it.
    ///
    /// ⚠ **Everything is polled, nothing is an event**: pointer events on our components are mute on
    /// IL2CPP (SearchableDropdown.PollOutsideClick, HelpZone.PollHover, Splitters.Tick), so the
    /// hover, the click, the wheel and Escape are read from the owner's tick. No Button, no
    /// ScrollRect: a row is a rectangle the pointer is tested against, and the wheel moves which
    /// entries the rows show.
    ///
    /// ⚠ Built on the mod's own root canvas (the one the inspector window sits on), so it is scaled
    /// as the window is and is recognised as the mod's interface, in a nested canvas drawn above
    /// the windows — the dropdown popup's order.
    /// </summary>
    internal sealed class UnderPointerList
    {
        internal struct Entry
        {
            /// <summary>The object's name, as the hierarchy has it.</summary>
            public string Name;
            /// <summary>What it says when it is a text, else what it is (Image, 3D object…).</summary>
            public string Detail;
        }

        private const float RowHeight = 34f;
        private const float HeaderHeight = 22f;
        private const float Pad = 4f;
        private const float Width = 380f;
        private const float EdgeMargin = 12f;

        private GameObject _root;
        private Canvas _rootCanvas;
        private Text _header;
        private readonly List<RectTransform> _rows = new List<RectTransform>();
        private readonly List<Image> _rowBacks = new List<Image>();
        private readonly List<EllipsisText> _names = new List<EllipsisText>();
        private readonly List<EllipsisText> _details = new List<EllipsisText>();

        private Entry[] _entries = new Entry[0];
        private int _first;
        private int _hovered = -1;
        private Action<int> _onHover;
        private Action<int> _onPick;
        private Action _onClose;

        internal bool IsOpen => _root != null;

        /// <summary>
        /// Open the list with its top-left corner at <paramref name="screen"/> — moved inside the
        /// screen when it would run off it. <paramref name="onHover"/> gets an entry's index, or -1
        /// when the pointer leaves the lines; <paramref name="onPick"/> the entry clicked, after
        /// which the list is closed; <paramref name="onClose"/> any close, picked or not.
        /// </summary>
        internal void Open(Canvas onCanvas, Vector2 screen, IList<Entry> entries,
                         Action<int> onHover, Action<int> onPick, Action onClose)
        {
            Close();
            if (onCanvas == null || entries == null || entries.Count == 0) return;

            _rootCanvas = onCanvas.rootCanvas;
            _entries = new Entry[entries.Count];
            entries.CopyTo(_entries, 0);
            _first = 0;
            _hovered = -1;
            _onHover = onHover;
            _onPick = onPick;
            _onClose = onClose;

            var canvasRect = _rootCanvas.GetComponent<RectTransform>();
            float scale = _rootCanvas.scaleFactor > 0f ? _rootCanvas.scaleFactor : 1f;
            float roomHeight = Screen.height / scale - 2f * EdgeMargin;
            int fit = Mathf.Max(1, Mathf.FloorToInt((roomHeight - HeaderHeight - 2f * Pad) / RowHeight));
            int shown = Mathf.Min(_entries.Length, fit);
            float height = HeaderHeight + shown * RowHeight + 2f * Pad;
            float width = Mathf.Min(Width, Screen.width / scale - 2f * EdgeMargin);

            _root = UIFactory.CreateUIObject("UGT_UnderPointerList", _rootCanvas.gameObject);
            var canvas = _root.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32000;   // above the windows, as the dropdown popup
            var back = _root.AddComponent<Image>();
            back.color = UIStyles.CardBackground;

            // Placed in the root canvas's own space: an overlay canvas's world space IS the screen.
            var rect = _root.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(width, height);
            Vector3 local = canvasRect.InverseTransformPoint(new Vector3(screen.x, screen.y, 0f));
            Rect area = canvasRect.rect;
            float x = Mathf.Clamp(local.x, area.xMin + EdgeMargin, area.xMax - EdgeMargin - width);
            float y = Mathf.Clamp(local.y, area.yMin + EdgeMargin + height, area.yMax - EdgeMargin);
            rect.anchoredPosition = new Vector2(x, y);

            _header = UIFactory.CreateLabel(_root, "Header", "", TextAnchor.MiddleLeft, UIStyles.TextMuted, supportRichText: false, fontSize: UIStyles.FontSizeHint);
            TranslatorCore.RegisterExcluded(_header);
            Place(_header.rectTransform, Pad + 4f, -Pad, -Pad - 4f, HeaderHeight);

            for (int i = 0; i < shown; i++)
            {
                var row = UIFactory.CreateUIObject("Row" + i, _root);
                var rowRect = row.GetComponent<RectTransform>();
                Place(rowRect, Pad, -(Pad + HeaderHeight + i * RowHeight), -Pad, RowHeight - 2f);
                var rowBack = row.AddComponent<Image>();
                rowBack.color = UIStyles.DropdownItemNormal;

                var name = UIFactory.CreateLabel(row, "Name", "", TextAnchor.LowerLeft, UIStyles.TextPrimary, supportRichText: false, fontSize: UIStyles.FontSizeSmall);
                TranslatorCore.RegisterExcluded(name);
                Place(name.rectTransform, 8f, -2f, -8f, (RowHeight - 2f) / 2f);
                var detail = UIFactory.CreateLabel(row, "Detail", "", TextAnchor.UpperLeft, UIStyles.TextSecondary, supportRichText: false, fontSize: UIStyles.FontSizeHint);
                TranslatorCore.RegisterExcluded(detail);
                Place(detail.rectTransform, 8f, -(RowHeight - 2f) / 2f, -8f, (RowHeight - 2f) / 2f - 2f);

                _rows.Add(rowRect);
                _rowBacks.Add(rowBack);
                _names.Add(new EllipsisText(name));
                _details.Add(new EllipsisText(detail));
            }
            Fill();
        }

        /// <summary>Close the list, if open. Says so to the owner once.</summary>
        internal void Close()
        {
            if (_root == null) return;
            UnityEngine.Object.Destroy(_root);
            _root = null;
            _rows.Clear();
            _rowBacks.Clear();
            _names.Clear();
            _details.Clear();
            _hovered = -1;
            var closed = _onClose;
            _onClose = null;
            closed?.Invoke();
        }

        /// <summary>
        /// One frame of the open list: Escape closes it, the wheel moves it, the pointer hovers a
        /// line, a press on a line takes it and a press anywhere else closes it without taking
        /// anything. True while the list was open this frame — the owner then leaves the frame's
        /// input to it.
        /// </summary>
        internal bool Tick()
        {
            if (_root == null) return false;
            if (!_root.activeInHierarchy) { Close(); return false; }

            if (InputManager.GetKeyDown(KeyCode.Escape)) { Close(); return true; }

            Vector3 mouse = InputManager.MousePosition;
            var point = new Vector2(mouse.x, mouse.y);
            bool over = Covers(point);

            // 🔴 FrameScrollDelta, never MouseScrollDelta — see Sliders.PollWheel.
            float turns = InputManager.FrameScrollDelta.y;
            if (over && turns != 0f && _entries.Length > _rows.Count)
            {
                int moved = Mathf.Clamp(_first - Math.Sign(turns), 0, _entries.Length - _rows.Count);
                if (moved != _first) { _first = moved; Fill(); }
            }

            int row = RowAt(point);
            int entry = row < 0 ? -1 : _first + row;
            if (entry != _hovered)
            {
                _hovered = entry;
                for (int i = 0; i < _rowBacks.Count; i++)
                    _rowBacks[i].color = i == row ? UIStyles.DropdownItemHighlight : UIStyles.DropdownItemNormal;
                _onHover?.Invoke(entry);
            }

            if (InputManager.GetMouseButtonDown(0) || InputManager.GetMouseButtonDown(1))
            {
                if (entry >= 0 && InputManager.GetMouseButtonDown(0))
                {
                    // Taken first, closed after: what closing clears is what the pick reads.
                    _onPick?.Invoke(entry);
                    Close();
                }
                else if (!over) Close();
            }
            return true;
        }

        /// <summary>Whether the open list lies under this screen point.</summary>
        internal bool Covers(Vector2 screen)
            => _root != null && UIHelpers.ContainsScreenPoint(_root.GetComponent<RectTransform>(), _rootCanvas, screen);

        private int RowAt(Vector2 screen)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (UIHelpers.ContainsScreenPoint(_rows[i], _rootCanvas, screen)) return i;
            return -1;
        }

        private void Fill()
        {
            int total = _entries.Length;
            _header.text = _rows.Count < total
                ? $"{_first + 1}–{_first + _rows.Count} of {total} objects under the pointer"
                : $"{total} objects under the pointer";
            for (int i = 0; i < _rows.Count; i++)
            {
                var e = _entries[_first + i];
                _names[i].Set(e.Name);
                _details[i].Set(e.Detail);
            }
        }

        /// <summary>A child stretched across its parent's width, at a top offset, with a height.</summary>
        private static void Place(RectTransform rect, float left, float top, float right, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(left, top - height);
            rect.offsetMax = new Vector2(right, top);
        }

        /// <summary>A label on one line, cut with an ellipsis when the line is too short (EllipsisLabel), or plainly set where it cannot be.</summary>
        private sealed class EllipsisText
        {
            private readonly Text _text;
            private readonly UniverseLib.UI.Widgets.EllipsisLabel _ellipsis;

            internal EllipsisText(Text text)
            {
                _text = text;
                _ellipsis = UIFactory.ConfigureEllipsis(text, "");
            }

            internal void Set(string value)
            {
                if (_ellipsis != null) _ellipsis.FullText = value ?? "";
                else _text.text = value ?? "";
            }
        }
    }
}
