using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Right-to-left text in Unity's input fields — the game's and this mod's own, which are the
    /// same uGUI component (UniverseLib builds InputField). See analyse/rtl-saisie-et-editeur-mod.md.
    ///
    /// What Unity does, and what this changes:
    /// - the field stores and edits the LOGICAL string and its label draws it as it is — Arabic
    ///   comes out unjoined and in typing order. The label is given the presented form instead
    ///   (<see cref="RtlFieldLayout"/>); the field's own text is never touched, so the caret, the
    ///   clipboard and what the game reads back all stay on what was typed;
    /// - the caret and the selection are drawn by the field from logical indices into the label,
    ///   which no longer line up. While the label is presented they are made transparent and drawn
    ///   here instead, from the layout's map, with plain Image quads (no class of ours to register
    ///   on IL2CPP);
    /// - a click is turned into a character index by walking the label's glyphs left to right:
    ///   the answer is recomputed from the map (GetCharacterIndexFromPosition postfix);
    /// - the arrow keys follow the screen (user's decision, 2026-09-25): MoveLeft/MoveRight go to
    ///   the visual neighbour.
    ///
    /// 🔴 A field showing no strong right-to-left character is left exactly as Unity has it, and a
    /// field that stops showing one gets its colours back the same instant.
    /// ⚠ Main thread only, like everything that touches the shaper.
    /// </summary>
    internal static class RtlInputFields
    {
        private sealed class FieldState
        {
            public WeakReference FieldRef;
            public InputField Field;
            public Text Label;
            public RtlFieldLayout Layout;
            public string Logical;          // the visible slice as the field holds it
            public string Shown;            // what the label was given
            public int DrawStart;

            public bool Hidden;
            public Color OriginalCaret;
            public bool OriginalCustomCaret;
            public Color OriginalSelection;

            public GameObject Overlay;
            public RectTransform OverlayRect;
            public readonly List<Image> Quads = new List<Image>();

            public int LastAnchor = -1, LastFocus = -1;
            public float BlinkStart;

            public readonly List<float> X = new List<float>();
            public readonly List<float> W = new List<float>();
            public readonly List<int> LineStart = new List<int>();
            public readonly List<float> LineTop = new List<float>();
            public readonly List<float> LineHeight = new List<float>();
        }

        private static readonly Dictionary<int, FieldState> _states = new Dictionary<int, FieldState>();
        private static readonly List<int> _scratch = new List<int>();
        private static int _logBudget = 5;

        // ── The label ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called from the text setter prefix when the component being written is an input field's
        /// label (the field is writing what it will draw). Replaces <paramref name="value"/> by
        /// its presented form when it carries right-to-left text, and releases the field otherwise.
        /// </summary>
        internal static void PresentLabel(object fieldObj, object labelObj, ref string value)
        {
            if (!TranslatorCore.IsMainThread) return;

            var field = TypeHelper.Il2CppCast(fieldObj, typeof(InputField)) as InputField;
            var label = TypeHelper.Il2CppCast(labelObj, typeof(Text)) as Text;
            if (field == null || label == null) return;   // TMP_InputField: see PresentTmpLabel

            int id = field.GetInstanceID();
            var prep = string.IsNullOrEmpty(value) ? null : RtlFieldLayout.Prepare(value);
            if (prep == null) { Release(id); return; }

            List<int> wraps = null;
            if (field.lineType != InputField.LineType.SingleLine)
            {
                wraps = RtlPresenter.UGuiLineStartsNow(label, prep.MeasureText, out string whyNot);
                if (wraps == null) Note($"multi-line field laid out on its hard breaks only ({whyNot})");
            }

            var layout = prep.Lay(wraps);

            if (!_states.TryGetValue(id, out var state))
            {
                state = new FieldState { FieldRef = new WeakReference(field) };
                _states[id] = state;
            }
            state.Field = field;
            state.Label = label;
            state.Layout = layout;
            state.Logical = value;
            state.Shown = layout.Display;
            state.DrawStart = ReadDrawStart(field);

            HideNative(state);

            // Our own output: nothing learns from it, and the in-game editor recovers the typed
            // text behind it (D8 — nothing shaped ever reaches the cache, the file or a server).
            TranslatorCore.RegisterPresentedText(layout.Display, value);
            value = layout.Display;
        }

        /// <summary>A field that stopped showing right-to-left text: its own caret and selection back.</summary>
        private static void Release(int id)
        {
            if (!_states.TryGetValue(id, out var state)) return;
            _states.Remove(id);
            RestoreNative(state);
            if (state.Overlay != null) UnityEngine.Object.Destroy(state.Overlay);
        }

        private static void HideNative(FieldState s)
        {
            if (s.Hidden) return;
            var f = s.Field;
            s.OriginalCustomCaret = f.customCaretColor;
            s.OriginalCaret = f.caretColor;
            s.OriginalSelection = f.selectionColor;

            var caret = f.caretColor;
            var selection = f.selectionColor;
            f.customCaretColor = true;
            f.caretColor = new Color(caret.r, caret.g, caret.b, 0f);
            f.selectionColor = new Color(selection.r, selection.g, selection.b, 0f);
            s.Hidden = true;
        }

        private static void RestoreNative(FieldState s)
        {
            if (!s.Hidden || s.Field == null) return;
            s.Field.caretColor = s.OriginalCaret;
            s.Field.customCaretColor = s.OriginalCustomCaret;
            s.Field.selectionColor = s.OriginalSelection;
            s.Hidden = false;
        }

        // m_DrawStart: where the visible slice starts in the field's text. Protected field on
        // Mono, a property of the same name in an IL2CPP interop assembly.
        private static bool _drawStartResolved;
        private static FieldInfo _drawStartField;
        private static PropertyInfo _drawStartProp;

        private static int ReadDrawStart(InputField field)
        {
            if (!_drawStartResolved)
            {
                _drawStartResolved = true;
                var t = typeof(InputField);
                _drawStartField = t.GetField("m_DrawStart", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (_drawStartField == null)
                    _drawStartProp = t.GetProperty("m_DrawStart", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (_drawStartField == null && _drawStartProp == null)
                    Note("InputField.m_DrawStart not found: a scrolled field places its caret from the start of its text");
            }
            try
            {
                if (_drawStartField != null) return Convert.ToInt32(_drawStartField.GetValue(field));
                if (_drawStartProp != null) return Convert.ToInt32(_drawStartProp.GetValue(field, null));
            }
            catch (Exception ex) { Note("m_DrawStart unreadable: " + ex.Message); }
            return 0;
        }

        // ── Every frame: the caret and the selection ──────────────────────────────────────

        /// <summary>Draw the caret or the selection of every presented field that has the focus.</summary>
        internal static void Tick()
        {
            if (_states.Count == 0) return;
            _scratch.Clear();
            _scratch.AddRange(_states.Keys);
            foreach (int id in _scratch)
            {
                var s = _states[id];
                if (s.Field == null || s.Label == null || !(s.FieldRef.Target is InputField alive) || alive == null)
                {
                    _states.Remove(id);
                    if (s.Overlay != null) UnityEngine.Object.Destroy(s.Overlay);
                    continue;
                }

                // The label carries something else now (the field wrote it without passing our
                // presentation — translations switched off, say): hand the field back.
                if (s.Label.text != s.Shown) { Release(id); continue; }

                bool focused = s.Field.isFocused && s.Field.gameObject.activeInHierarchy;
                if (!focused) { ShowQuads(s, 0); continue; }

                if (!RtlPresenter.ReadUGuiGlyphs(s.Label, s.Shown, s.X, s.W, s.LineStart, s.LineTop, s.LineHeight))
                {
                    ShowQuads(s, 0);
                    continue;
                }

                EnsureOverlay(s);
                int anchor = s.Field.selectionAnchorPosition - s.DrawStart;
                int focus = s.Field.selectionFocusPosition - s.DrawStart;
                if (anchor != s.LastAnchor || focus != s.LastFocus)
                {
                    s.LastAnchor = anchor;
                    s.LastFocus = focus;
                    s.BlinkStart = Time.unscaledTime;
                }

                if (anchor == focus) DrawCaret(s, focus);
                else DrawSelection(s, Math.Min(anchor, focus), Math.Max(anchor, focus));
            }
        }

        private static void DrawCaret(FieldState s, int caret)
        {
            float rate = s.Field.caretBlinkRate;
            bool on = rate <= 0f || ((Time.unscaledTime - s.BlinkStart) * rate) % 1f < 0.5f;
            if (!on) { ShowQuads(s, 0); return; }

            s.Layout.CaretAnchor(caret, out int d, out bool rightEdge, out int line);
            int gLine;
            float x;
            if (d < 0)
            {
                // An empty line: the side it starts on.
                gLine = GeneratorLineOfDisplay(s, s.Layout.LineDisplayStart(line));
                var rect = s.Label.rectTransform.rect;
                x = s.Layout.LineIsRtl(line) ? rect.xMax - s.Field.caretWidth : rect.xMin;
            }
            else
            {
                if (d >= s.X.Count) { ShowQuads(s, 0); return; }
                gLine = GeneratorLineOfDisplay(s, d);
                x = s.X[d] + (rightEdge ? s.W[d] : 0f);
            }
            if (gLine < 0) { ShowQuads(s, 0); return; }

            float top = s.LineTop[gLine];
            float height = s.LineHeight[gLine];
            // The colour the field would have drawn: InputField.caretColor answers the text's own
            // colour when no custom one is set, and that is what was read before hiding it.
            Place(s, 0, x, top - height, s.Field.caretWidth, height, s.OriginalCaret);
            ShowQuads(s, 1);
        }

        private static void DrawSelection(FieldState s, int from, int to)
        {
            // One interval per glyph, merged per generator line: a bidi selection can be several
            // disjoint pieces on one line, and each is drawn where it shows.
            var spans = new List<KeyValuePair<int, Vector2>>();
            int lastD = -1;
            for (int i = Math.Max(0, from); i < Math.Min(to, s.Layout.LogicalLength); i++)
            {
                if (s.Logical[i] == '\n') continue;
                int d = s.Layout.DisplayOf(i);
                if (d == lastD || d < 0 || d >= s.X.Count) continue;
                lastD = d;
                int g = GeneratorLineOfDisplay(s, d);
                if (g < 0) continue;
                spans.Add(new KeyValuePair<int, Vector2>(g, new Vector2(s.X[d], s.X[d] + s.W[d])));
            }
            spans.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.x.CompareTo(b.Value.x));

            int count = 0;
            for (int k = 0; k < spans.Count;)
            {
                int g = spans[k].Key;
                float x0 = spans[k].Value.x, x1 = spans[k].Value.y;
                k++;
                while (k < spans.Count && spans[k].Key == g && spans[k].Value.x <= x1 + 0.5f)
                {
                    x1 = Math.Max(x1, spans[k].Value.y);
                    k++;
                }
                float top = s.LineTop[g];
                float height = s.LineHeight[g];
                Place(s, count++, x0, top - height, x1 - x0, height, s.OriginalSelection);
            }
            ShowQuads(s, count);
        }

        /// <summary>The generator line holding display index <paramref name="d"/>.</summary>
        private static int GeneratorLineOfDisplay(FieldState s, int d)
        {
            int found = -1;
            for (int g = 0; g < s.LineStart.Count; g++)
                if (s.LineStart[g] <= d) found = g;
            return found;
        }

        // ── The overlay: a sibling of the label, BEHIND it like Unity's own caret ─────────

        private static void EnsureOverlay(FieldState s)
        {
            var labelRect = s.Label.rectTransform;
            if (s.Overlay == null)
            {
                s.Overlay = new GameObject("UGT RTL caret");
                s.OverlayRect = s.Overlay.AddComponent<RectTransform>();
                s.Overlay.transform.SetParent(labelRect.parent, false);
                s.Quads.Clear();
            }
            // Same rectangle as the label, drawn just before it (so under its glyphs), inside the
            // same mask — a field that scrolls or clips its text clips this too.
            var o = s.OverlayRect;
            o.anchorMin = labelRect.anchorMin;
            o.anchorMax = labelRect.anchorMax;
            o.pivot = labelRect.pivot;
            o.anchoredPosition = labelRect.anchoredPosition;
            o.sizeDelta = labelRect.sizeDelta;
            o.localRotation = labelRect.localRotation;
            o.localScale = labelRect.localScale;
            int li = labelRect.GetSiblingIndex();
            int oi = s.Overlay.transform.GetSiblingIndex();
            // Right before the label. Moving a child from before to after shifts the ones in
            // between down by one, hence the two targets.
            if (oi != li - 1) s.Overlay.transform.SetSiblingIndex(oi < li ? li - 1 : li);
        }

        private static void Place(FieldState s, int index, float x, float y, float w, float h, Color color)
        {
            while (s.Quads.Count <= index)
            {
                var go = new GameObject("q" + s.Quads.Count);
                go.transform.SetParent(s.Overlay.transform, false);
                var image = go.AddComponent<Image>();
                image.raycastTarget = false;
                var r = go.GetComponent<RectTransform>();
                var pivot = s.Label.rectTransform.pivot;
                r.anchorMin = pivot;
                r.anchorMax = pivot;
                r.pivot = Vector2.zero;
                s.Quads.Add(image);
            }
            var q = s.Quads[index];
            q.color = color;
            var rt = q.rectTransform;
            rt.anchorMin = s.Label.rectTransform.pivot;
            rt.anchorMax = s.Label.rectTransform.pivot;
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(Math.Max(1f, w), Math.Max(1f, h));
        }

        private static void ShowQuads(FieldState s, int count)
        {
            for (int i = 0; i < s.Quads.Count; i++)
            {
                bool on = i < count;
                if (s.Quads[i] != null && s.Quads[i].gameObject.activeSelf != on) s.Quads[i].gameObject.SetActive(on);
            }
        }

        // ── Clicks and arrow keys ──────────────────────────────────────────────────────────

        /// <summary>
        /// A click (or a drag) on a presented field: the character index under the pointer, from
        /// the map instead of Unity's left-to-right walk over glyphs it no longer lines up with.
        /// The answer is relative to the visible slice, as Unity's is (the caller adds the start).
        /// </summary>
        public static void UGui_GetCharacterIndexFromPosition_Postfix(object __instance, Vector2 __0, ref int __result)
        {
            try
            {
                var s = StateOf(__instance);
                if (s == null) return;
                if (!RtlPresenter.ReadUGuiGlyphs(s.Label, s.Shown, s.X, s.W, s.LineStart, s.LineTop, s.LineHeight)) return;

                Vector2 pos = __0;
                // The generator line under the pointer: above the first → first, below the last → last.
                int g = 0;
                for (int k = 0; k < s.LineStart.Count; k++)
                    if (pos.y <= s.LineTop[k]) g = k;

                int from = s.LineStart[g];
                int to = g + 1 < s.LineStart.Count ? s.LineStart[g + 1] : s.Shown.Length;
                int line = s.Layout.LineOfCaret(0);
                for (int L = 0; L < s.Layout.LineCount; L++)
                    if (s.Layout.LineDisplayStart(L) <= from) line = L;

                float minX = float.MaxValue, maxX = float.MinValue;
                for (int d = from; d < to && d < s.X.Count; d++)
                {
                    if (s.Shown[d] == '\n') continue;
                    float x0 = s.X[d], x1 = s.X[d] + s.W[d];
                    minX = Math.Min(minX, x0);
                    maxX = Math.Max(maxX, x1);
                    if (pos.x >= x0 && pos.x < x1)
                    {
                        __result = s.Layout.CaretFromHit(d, pos.x >= (x0 + x1) * 0.5f);
                        return;
                    }
                }
                __result = s.Layout.CaretAtLineSide(line, rightSide: minX == float.MaxValue || pos.x >= maxX);
            }
            catch (Exception ex) { Note("click mapping failed: " + ex.Message); }
        }

        public static bool UGui_MoveLeft_Prefix(object __instance, bool __0, bool __1) => !Move(__instance, false, __0, __1);
        public static bool UGui_MoveRight_Prefix(object __instance, bool __0, bool __1) => !Move(__instance, true, __0, __1);

        /// <summary>
        /// One arrow press on a presented field, by screen position. True when handled here — the
        /// original then does not run.
        /// </summary>
        private static bool Move(object instance, bool toRight, bool shift, bool ctrl)
        {
            try
            {
                var s = StateOf(instance);
                if (s == null) return false;
                var f = s.Field;
                int start = s.DrawStart;
                int anchor = f.selectionAnchorPosition - start;
                int focus = f.selectionFocusPosition - start;
                var layout = s.Layout;

                // A selection and no shift: collapse to its end on that side of the screen.
                if (!shift && anchor != focus)
                {
                    bool anchorFurther = toRight
                        ? layout.BoundaryOf(anchor) > layout.BoundaryOf(focus)
                        : layout.BoundaryOf(anchor) < layout.BoundaryOf(focus);
                    f.caretPosition = (anchorFurther ? anchor : focus) + start;
                    return true;
                }

                int next = ctrl ? WordStep(s, focus, toRight) : layout.VisualStep(focus, toRight);
                if (next == focus)
                {
                    // The visible slice's edge: onward in reading order, one character — the
                    // field then scrolls its window to keep the caret in view.
                    int line = layout.LineOfCaret(focus);
                    bool forward = toRight != layout.LineIsRtl(line);
                    int absolute = focus + start + (forward ? 1 : -1);
                    if (absolute < 0 || absolute > (f.text ?? "").Length) return true;
                    next = absolute - start;
                }

                if (shift) f.selectionFocusPosition = next + start;
                else f.caretPosition = next + start;
                return true;
            }
            catch (Exception ex)
            {
                Note("arrow move failed, Unity's own used: " + ex.Message);
                return false;
            }
        }

        /// <summary>Ctrl + arrow: visual steps until the caret stands at the start of a word.</summary>
        private static int WordStep(FieldState s, int caret, bool toRight)
        {
            int current = caret;
            for (int guard = 0; guard <= s.Logical.Length; guard++)
            {
                int next = s.Layout.VisualStep(current, toRight);
                if (next == current) return current;
                current = next;
                bool atWordStart = current < s.Logical.Length && !char.IsWhiteSpace(s.Logical[current])
                                   && (current == 0 || char.IsWhiteSpace(s.Logical[current - 1]));
                bool atEdge = current == 0 || current == s.Logical.Length;
                if (atWordStart || atEdge) return current;
            }
            return current;
        }

        private static FieldState StateOf(object instance)
        {
            if (_states.Count == 0 || instance == null) return null;
            var field = TypeHelper.Il2CppCast(instance, typeof(InputField)) as InputField;
            if (field == null) return null;
            return _states.TryGetValue(field.GetInstanceID(), out var s) && s.Label != null && s.Label.text == s.Shown ? s : null;
        }

        private static void Note(string message)
        {
            if (_logBudget <= 0) return;
            _logBudget--;
            TranslatorCore.LogWarning("[RtlInputFields] " + message);
        }
    }
}
