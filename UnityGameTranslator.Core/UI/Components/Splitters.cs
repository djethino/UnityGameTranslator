using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseLib.Input;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A bar between two scroll areas that share a body. Dragged, it moves height from one to
    /// the other (<see cref="ListShares.Attach"/>); the rule's own division comes back when the
    /// window closes.
    ///
    /// ⚠ Polled, never event-driven: injected pointer events are mute on IL2CPP and drag events
    /// go through the same UnityEvent path this project forbids from the Core. The bar watches
    /// the mouse itself each frame (<see cref="Splitters.Tick"/>, from the mod's single tick,
    /// like the help bar's hover): a press inside it starts a drag, the button held moves it,
    /// the release ends it. The screen-point test is the shared one
    /// (UIHelpers.ContainsScreenPoint), exact for unrotated controls.
    /// </summary>
    public sealed class SplitterHandle
    {
        private readonly GameObject _bar;
        private readonly RectTransform _rect;
        private Canvas _canvas;
        private bool _wasDown, _dragging;
        private float _lastY;

        /// <summary>The bar moved, in UI units — positive when dragged down, so the area above grows.</summary>
        public event Action<float> Dragged;

        public Host Handle => new Host(_bar);

        /// <summary>Gone with its screen: nothing left to watch.</summary>
        internal bool Dead => _bar == null;

        internal SplitterHandle(GameObject bar)
        {
            _bar = bar;
            _rect = bar.GetComponent<RectTransform>();
        }

        internal void Tick()
        {
            if (_bar == null || _rect == null || !_bar.activeInHierarchy)
            {
                _dragging = false;
                _wasDown = false;
                return;
            }

            bool down = InputManager.GetMouseButton(0);
            Vector3 mouse = InputManager.MousePosition;

            if (!_dragging)
            {
                // Only the press itself, inside the bar, starts a drag — a button held since
                // somewhere else and slid over the bar does not.
                bool pressed = down && !_wasDown;
                _wasDown = down;
                if (!pressed) return;
                if (_canvas == null) _canvas = _rect.GetComponentInParent<Canvas>();
                if (!UIHelpers.ContainsScreenPoint(_rect, _canvas, mouse)) return;
                _dragging = true;
                _lastY = mouse.y;
                return;
            }

            if (!down)
            {
                _dragging = false;
                _wasDown = false;
                return;
            }

            // Screen pixels grow upward; the bar's own units are the canvas's.
            float scale = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
            float moved = (mouse.y - _lastY) / scale;
            if (Mathf.Abs(moved) < 0.5f) return;
            _lastY = mouse.y;
            Dragged?.Invoke(-moved);
        }
    }

    public static class Splitters
    {
        /// <summary>The bar's thickness: enough to take hold of, thin enough to read as a seam.</summary>
        public const int Thickness = 8;

        private static readonly List<SplitterHandle> Live = new List<SplitterHandle>();

        public static SplitterHandle Create(Host parent, string name)
        {
            var bar = UIFactory.CreateUIObject(name, parent.Object);
            UIFactory.SetLayoutElement(bar, minHeight: Thickness, preferredHeight: Thickness, flexibleHeight: 0, flexibleWidth: 9999);
            UIStyles.SetBackground(bar, UIStyles.BorderStrong);
            var handle = new SplitterHandle(bar);
            Live.Add(handle);
            return handle;
        }

        /// <summary>Every bar watches the mouse; one whose screen was destroyed is dropped here.</summary>
        public static void Tick()
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (Live[i].Dead) { Live.RemoveAt(i); continue; }
                Live[i].Tick();
            }
        }
    }
}
