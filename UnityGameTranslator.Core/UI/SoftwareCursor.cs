using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.Input;
using UniverseLib.UI.Panels;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// A pointer of our own, drawn where the mouse really is, while the mod holds the cursor of a
    /// game that hides the system one (decided 2026-10-09, « ok pour 1 »).
    ///
    /// 🔴 **Why it is drawn rather than shown.** A game that draws its own cursor sets the system
    /// cursor invisible from native code, every frame; UniverseLib setting it visible at the end of
    /// the frame loses that race at the moment the frame is shown — measured: the system cursor
    /// reported hidden with a window of ours open. The game's own pointer was drawn away from the
    /// real position (its 16:9 picture inside a wider window), so every click in our windows landed
    /// beside what was aimed at. An image of ours cannot be taken away by the game, and sits exactly
    /// on the mouse.
    ///
    /// ⚠ **Only then.** A game that shows the system cursor keeps it, and gets nothing more: the arrow
    /// appears only when UniverseLib took a cursor the game had hidden
    /// (<see cref="CursorUnlocker.HoldingAHiddenCursor"/>), and it goes with the holding. While
    /// UniverseLib shows its resize mark (<see cref="PanelManager.ResizePrompting"/>) that mark is the
    /// pointer, and the arrow steps aside — one pointer on screen, as with the system cursor.
    ///
    /// ⚠ One arrow for everything, which is parity: Unity never changes the system cursor over a
    /// button or a field, so the system arrow is what every game showing it already gets.
    /// </summary>
    internal static class SoftwareCursor
    {
        // Above every canvas, UniverseLib's (30000) included.
        private const int SortingOrder = 32767;

        private static GameObject _root;
        private static RectTransform _pointer;
        private static bool _shown;

        /// <summary>Once per tick, after the input ownership was settled for this frame.</summary>
        internal static void Tick()
        {
            bool wanted = CursorUnlocker.HoldingAHiddenCursor && !PanelManager.ResizePrompting;
            if (wanted && _root == null && !Build()) return;

            if (wanted != _shown)
            {
                _shown = wanted;
                _root.SetActive(wanted);
            }
            if (_shown) _pointer.anchoredPosition = InputManager.MousePosition;
        }

        /// <summary>The canvas and its two layers — the dark rim under the white arrow.</summary>
        private static bool Build()
        {
            var body = Icons.Get("cursor");
            var rim = Icons.Get("cursor-rim");
            if (body == null || rim == null)
            {
                if (DiagnosticOnce.First("SoftwareCursor.build", "sprites"))
                    TranslatorCore.LogWarning("[SoftwareCursor] the pointer marks could not be drawn — no pointer of ours over a hidden system cursor");
                return false;
            }

            _root = EngineStrings.NewGameObject("UGT_SoftwareCursor");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            // Never picked by the scanner, the inspector or the capture: it is ours.
            TranslatorCore.RegisterPanelRoot(_root);

            // 🔴 The IL2CPP-proven path, as InspectorPicker's highlight overlay: the Image first (it
            // brings the RectTransform), the RectTransform then READ with GetComponent — never added
            // by hand, never reached by a cast (analyse/pieges-projet.md §2: a RectTransform reached
            // another way answers right at construction and wrong later, in silence).
            // The rim is the pointer itself; the white arrow, its child, draws over it.
            var pointer = Layer(_root, "Pointer", rim, Color.black);
            _pointer = pointer.GetComponent<RectTransform>();
            _pointer.anchorMin = Vector2.zero;
            _pointer.anchorMax = Vector2.zero;
            // Placed by its tip: the hotspot is where the mouse is.
            _pointer.pivot = new Vector2((float)(Icons.CursorTipX / 16.0), 1f - (float)(Icons.CursorTipY / 16.0));
            _pointer.sizeDelta = new Vector2(Icons.CursorTexels, Icons.CursorTexels);

            var arrow = Layer(pointer, "Arrow", body, Color.white).GetComponent<RectTransform>();
            arrow.anchorMin = Vector2.zero;
            arrow.anchorMax = Vector2.one;
            arrow.offsetMin = Vector2.zero;
            arrow.offsetMax = Vector2.zero;

            _root.SetActive(false);
            return true;
        }

        private static GameObject Layer(GameObject parent, string name, Sprite sprite, Color colour)
        {
            var go = EngineStrings.NewGameObject(name);
            go.transform.SetParent(parent.transform, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.color = colour;
            // A drawing, never a surface: the click goes to whatever is under the tip.
            image.raycastTarget = false;
            return go;
        }
    }
}
