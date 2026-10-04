using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Right-to-left editing in UI Toolkit's text fields on 2021.3 — the experience the fields of
    /// 2022.3 and Unity 6 give (RtlInputFields.UIToolkit), on another architecture
    /// (analyse/rtl-saisie-ngui-uitk.md § 2021.3): the field's input element draws the typed text
    /// itself and edits it with IMGUI's TextEditor, which measures with a GUIStyle of its own.
    ///
    /// Nothing here names a type of that generic input element: it is recognised by the USS class
    /// every single-line field gives it, and reached through what it calls —
    /// - its text: <c>MeshGenerationContextUtils.Text</c> draws the presented form
    ///   (<see cref="RtlFieldLayout.Display"/>) where the element drew the typed one;
    /// - its caret: <c>TextCore.Text.TextGenerator.GetCursorPosition(textInfo, rect, index)</c> on the
    ///   element's own text info gets the display gap of the typed caret;
    /// - its selection: the rectangle it draws in the selection colour becomes one per piece the typed
    ///   range shows;
    /// - clicks, drags and scrolling: the field's own GUIStyle measures the presented form
    ///   (<c>GetCursorStringIndex</c>, <c>GetCursorPixelPosition</c>);
    /// - the arrows: its TextEditor's MoveLeft/MoveRight/SelectLeft/SelectRight/MoveWord* step by
    ///   screen position (user's decision, 2026-09-25).
    /// The value stays as typed. Up/Down, Home/End and a multi-line field stay the engine's.
    /// </summary>
    internal static partial class RtlInputFields
    {
        private sealed class Uitk21State
        {
            // 🔴 No engine object held — neither strongly (on IL2CPP a wrapper keeps its native object
            // alive: a field removed from the screen would stay in memory) nor weakly (the interop may
            // let a wrapper go while the object lives: a weak reference to it dropped the state of a
            // field still being edited, half a case mapped and half not — bench, 2021.3 IL2CPP). The
            // field is found by the keys of its objects (ObjectKey), and they are read again on it.
            public RtlFieldLayout Layout;
            public string Logical;
            public string Shown;
        }

        // By the input element's id, and by the ids of its GUIStyle, TextEditor and text info.
        private static readonly Dictionary<long, Uitk21State> _uitk21 = new Dictionary<long, Uitk21State>();
        private static readonly Dictionary<long, long> _uitk21Related = new Dictionary<long, long>();
        [ThreadStatic] private static bool _uitk21Own;

        /// <summary>The hooks, when this engine has 2021's fields (an input element drawing its text itself).</summary>
        internal static int PatchUitk2021Fields(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            if (!Uitk21.Resolve()) return 0;
            var fields = typeof(RtlInputFields);
            MethodInfo Hook(string name) => fields.GetMethod(name, BindingFlags.Static | BindingFlags.Public);
            int count = 0;
            patcher(Uitk21.Text, Hook(nameof(Uitk21_Text_Prefix)), null); count++;
            patcher(Uitk21.CursorPosition, Hook(nameof(Uitk21_CursorPosition_Prefix)), null); count++;
            if (Uitk21.Rectangle != null) { patcher(Uitk21.Rectangle, Hook(nameof(Uitk21_Rectangle_Prefix)), null); count++; }
            if (Uitk21.Click != null) { patcher(Uitk21.Click, Hook(nameof(Uitk21_MoveCursorToPosition_Prefix)), null); count++; }
            if (Uitk21.Drag != null) { patcher(Uitk21.Drag, Hook(nameof(Uitk21_SelectToPosition_Prefix)), null); count++; }
            if (Uitk21.Scrolling != null) { patcher(Uitk21.Scrolling, Hook(nameof(Uitk21_UpdateScrollOffset_Prefix)), Hook(nameof(Uitk21_UpdateScrollOffset_Postfix))); count++; }
            if (Uitk21.PreDrawCursor != null) { patcher(Uitk21.PreDrawCursor, null, Hook(nameof(Uitk21_PreDrawCursor_Postfix))); count++; }
            if (Uitk21.SelectCurrentWord != null) { patcher(Uitk21.SelectCurrentWord, Hook(nameof(Uitk21_SelectCurrentWord_Prefix)), null); count++; }
            foreach (var move in Uitk21.Moves)
            {
                patcher(move.Value, Hook(move.Key), null);
                count++;
            }
            if (Uitk21.Rectangle == null || Uitk21.Click == null || Uitk21.Drag == null || Uitk21.Scrolling == null || Uitk21.Moves.Count < 6)
                TranslatorCore.LogWarning($"[Patches] UI Toolkit 2021 field right-to-left editing incomplete: selection={(Uitk21.Rectangle != null)} click={(Uitk21.Click != null)} drag={(Uitk21.Drag != null)} scroll={(Uitk21.Scrolling != null)} arrows={Uitk21.Moves.Count}/6");
            return count;
        }

        // ══ What the field draws ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// MeshGenerationContextUtils.Text(mgc, TextParams, handle, pixelsPerPoint): the element being
        /// drawn, when it is a single-line field's input showing right-to-left text (or text needing
        /// shaping), draws the presented form: the parameters' text is set to it for the original
        /// method, drawn by us, then put back. ⚠ Never a copy of the parameters: on IL2CPP a copy of
        /// the boxed struct is a second wrapper on the same memory, and the presented form stayed in
        /// the engine's own — the next draw laid it out as if typed (bench, 2021.3 IL2CPP).
        /// The field's value is not touched.
        /// </summary>
        public static bool Uitk21_Text_Prefix(object __0, object __1, object __2, float __3)
        {
            if (_uitk21Own) return true;
            try
            {
                if (!TranslatorCore.IsMainThread || __0 == null || __1 == null) return true;
                var element = Uitk21.InputOf(__0);
                if (element == null) return true;
                long id = ObjectKey(element);
                string text = Uitk21.ParamsText(__1);
                // Our own form coming round again is drawn as it is, never laid out as typed text.
                if (_uitk21.TryGetValue(id, out var held) && held.Shown == text) return true;
                var s = Uitk21Prepare(id, element, text);
                if (s == null || s.Shown == text) return true;

                _uitk21Own = true;
                try
                {
                    Uitk21.SetParamsText(__1, s.Shown);
                    Uitk21.Text.Invoke(null, new[] { __0, __1, __2, __3 });
                }
                finally
                {
                    Uitk21.SetParamsText(__1, text);
                    _uitk21Own = false;
                }
                return false;
            }
            catch (Exception ex)
            {
                Note("UI Toolkit 2021 field presentation failed, drawn as typed: " + (ex.InnerException ?? ex).Message);
                return true;
            }
        }

        /// <summary>The field's state for this text, laid out again when the text changed; null when there is nothing to present.</summary>
        private static Uitk21State Uitk21Prepare(long id, object element, string text)
        {
            if (string.IsNullOrEmpty(text) || Uitk21.IsPassword(element))
            {
                if (TranslatorCore.DebugMode && _uitk21.ContainsKey(id))
                    TranslatorCore.LogDebug($"[RtlInputFields] UI Toolkit 2021 field {id} let go: {(string.IsNullOrEmpty(text) ? "drawn empty" : "a password")}");
                Uitk21Forget(id);
                return null;
            }
            if (_uitk21.TryGetValue(id, out var held) && held.Logical == text) return held;
            if (held != null && TranslatorCore.DebugMode)
                TranslatorCore.LogDebug($"[RtlInputFields] UI Toolkit 2021 field {id} drawn with another text: was {RtlPresenter.Escape(held.Logical)} now {RtlPresenter.Escape(text)}");
            var prep = RtlFieldLayout.Prepare(text);
            if (prep == null) { Uitk21Forget(id); return null; }

            var s = new Uitk21State { Logical = text };
            s.Layout = prep.Lay(null);
            s.Shown = s.Layout.Display;
            var editor = Uitk21.EditorOf(element);
            var textInfo = Uitk21.TextInfoOf(element);
            Uitk21Forget(id);
            _uitk21[id] = s;
            if (editor != null)
            {
                _uitk21Related[ObjectKey(editor)] = id;
                var style = Uitk21.StyleOf(editor);
                if (style != null) _uitk21Related[ObjectKey(style)] = id;
            }
            if (textInfo != null) _uitk21Related[ObjectKey(textInfo)] = id;
            // What a field of this engine can be reached through — said once per answer: the first
            // thing to read when a 2021 field draws right but does not edit right.
            string reach = $"editor {(editor != null)}, its style {(editor != null && Uitk21.StyleOf(editor) != null)}, text info {(textInfo != null)}";
            if (DiagnosticOnce.First("RtlInputFields.Uitk21", reach))
                TranslatorCore.LogInfo($"[RtlInputFields] UI Toolkit 2021 field presented — reached through: {reach}");
            if (TranslatorCore.DebugMode)
                TranslatorCore.LogDebug($"[RtlInputFields] UI Toolkit 2021 field {id} laid out ({text.Length} chars): editor {(editor == null ? "-" : ObjectKey(editor).ToString())}, text info {(textInfo == null ? "-" : ObjectKey(textInfo).ToString())}");
            TranslatorCore.RegisterPresentedText(s.Shown, text);
            Describe("UI Toolkit (2021)", element, text, s.Shown);
            return s;
        }

        private static void Uitk21Forget(long id)
        {
            if (!_uitk21.Remove(id)) return;
            var stale = new List<long>();
            foreach (var kv in _uitk21Related) if (kv.Value == id) stale.Add(kv.Key);
            foreach (var key in stale) _uitk21Related.Remove(key);
        }

        /// <summary>The field state an object of it (its TextEditor, GUIStyle or text info) belongs to.</summary>
        private static Uitk21State Uitk21StateOf(object related)
        {
            if (related == null || _uitk21.Count == 0) return null;
            if (!_uitk21Related.TryGetValue(ObjectKey(related), out long id)) return null;
            if (!_uitk21.TryGetValue(id, out var s)) return null;
            return s;
        }

        /// <summary>
        /// The state for a TextEditor still editing the text it was laid out for — a keystroke since
        /// is laid out by the next draw, and until then the editor's own steps are used.
        /// </summary>
        private static Uitk21State Uitk21StateOfEditor(object editor)
        {
            var s = Uitk21StateOf(editor);
            if (s != null && Uitk21.EditorText(editor) == s.Logical) return s;
            if (TranslatorCore.DebugMode && editor != null
                && DiagnosticOnce.First("RtlInputFields.Uitk21.editor", ObjectKey(editor) + "/" + (s == null ? "none" : s.Logical.Length + "/" + (Uitk21.EditorText(editor)?.Length ?? -1))))
                TranslatorCore.LogDebug($"[RtlInputFields] UI Toolkit 2021: editor {ObjectKey(editor)} {(s == null ? "belongs to no presented field" : $"edits {Uitk21.EditorText(editor)?.Length ?? -1} chars, its field laid out {s.Logical.Length}")}");
            return null;
        }

        // ══ Caret ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// TextCore's TextGenerator.GetCursorPosition(textInfo, rect, index, inverseY): on a presented
        /// field's text info — the presented form's — the typed caret's display gap.
        /// </summary>
        public static void Uitk21_CursorPosition_Prefix(object __0, ref int __2)
        {
            if (_uitk21Own) return;
            try
            {
                var s = Uitk21StateOf(__0);
                if (s == null && TranslatorCore.DebugMode && _uitk21.Count > 0 && __0 != null
                    && DiagnosticOnce.First("RtlInputFields.Uitk21.caret", ObjectKey(__0).ToString()))
                    TranslatorCore.LogDebug($"[RtlInputFields] UI Toolkit 2021: caret asked on text info {ObjectKey(__0)}, no presented field holds it");
                if (s != null && __2 >= 0 && __2 <= s.Logical.Length)
                {
                    __2 = s.Layout.BoundaryOf(__2);
                    if (DiagnosticOnce.First("RtlInputFields.Uitk21", "caret")) TranslatorCore.LogInfo("[RtlInputFields] UI Toolkit 2021 field: caret mapped");
                }
            }
            catch (Exception ex) { Note("UI Toolkit 2021 caret mapping failed: " + ex.Message); }
        }

        // ══ Selection ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// MeshGenerationContextUtils.Rectangle(mgc, RectangleParams): the field's selection — the
        /// rectangle it draws in its selection colour — drawn as one rectangle per piece of the screen
        /// the typed range shows, at the height the field gave it (a mixed line selects in pieces).
        /// </summary>
        public static bool Uitk21_Rectangle_Prefix(object __0, object __1)
        {
            if (_uitk21Own) return true;
            try
            {
                if (__0 == null || __1 == null || _uitk21.Count == 0) return true;
                var element = Uitk21.InputOf(__0);
                if (element == null || !_uitk21.TryGetValue(ObjectKey(element), out var s)) return true;
                if (!Uitk21.IsSelectionRect(element, __1)) return true;
                var editor = Uitk21.EditorOf(element);
                var textInfo = Uitk21.TextInfoOf(element);
                if (editor == null || textInfo == null || Uitk21.EditorText(editor) != s.Logical) return true;

                int a = Uitk21.CursorIndex(editor), b = Uitk21.SelectIndex(editor);
                var pieces = DisplayPieces(s.Layout, s.Logical, Math.Min(a, b), Math.Max(a, b));
                Rect original = Uitk21.ParamsRect(__1);
                Rect content = Uitk21.ContentRect(element);
                float scroll = Uitk21.ScrollX(editor);
                _uitk21Own = true;
                try
                {
                    // The parameters written for each piece and put back (never copied: see the text's prefix).
                    foreach (var piece in pieces)
                    {
                        float from = Uitk21.PositionOf(textInfo, content, piece.Key).x - scroll;
                        float to = Uitk21.PositionOf(textInfo, content, piece.Value).x - scroll;
                        Uitk21.SetParamsRect(__1, new Rect(from, original.y, to - from, original.height));
                        Uitk21.Rectangle.Invoke(null, new[] { __0, __1 });
                    }
                }
                finally
                {
                    Uitk21.SetParamsRect(__1, original);
                    _uitk21Own = false;
                }
                return false;
            }
            catch (Exception ex)
            {
                Note("UI Toolkit 2021 selection drawing failed, the field's own used: " + (ex.InnerException ?? ex).Message);
                return true;
            }
        }

        // ══ Clicks, drags, scrolling — the field's TextEditor ════════════════════════════════════
        // The TextEditor measures with its GUIStyle (GetCursorStringIndex / GetCursorPixelPosition).
        // Those are hooked through their callers, not themselves: on IL2CPP the small GUIStyle methods
        // are compiled into the TextEditor's, and a hook on them never ran (bench, 2021.3).

        /// <summary>The caret standing under a point of the field (in its TextEditor's coordinates), measured on the presented form.</summary>
        private static int Uitk21CaretAt(TextEditor editor, Uitk21State s, Vector2 point)
        {
            var style = Uitk21.StyleOf(editor) as GUIStyle;
            int gap = style.GetCursorStringIndex(Uitk21.LocalPosition(editor), new GUIContent(s.Shown), point + Uitk21.Scroll(editor));
            if (DiagnosticOnce.First("RtlInputFields.Uitk21", "click")) TranslatorCore.LogInfo("[RtlInputFields] UI Toolkit 2021 field: click mapped");
            return s.Layout.CaretAtBoundary(gap);
        }

        /// <summary>Where the presented caret is drawn, in the TextEditor's terms (its graphicalCursorPos).</summary>
        private static Vector2 Uitk21CaretPixel(TextEditor editor, Uitk21State s, Rect within)
        {
            var style = Uitk21.StyleOf(editor) as GUIStyle;
            return style.GetCursorPixelPosition(within, new GUIContent(s.Shown), s.Layout.BoundaryOf(Math.Min(editor.cursorIndex, s.Logical.Length)));
        }

        /// <summary>TextEditor.MoveCursorToPosition_Internal(point, shift) — a click (and a shift-click).</summary>
        public static bool Uitk21_MoveCursorToPosition_Prefix(TextEditor __instance, Vector2 __0, bool __1)
        {
            try
            {
                var s = Uitk21StateOfEditor(__instance);
                if (s == null) return true;
                __instance.selectIndex = Uitk21CaretAt(__instance, s, __0);
                if (!__1) __instance.cursorIndex = __instance.selectIndex;
                __instance.DetectFocusChange();
                return false;
            }
            catch (Exception ex)
            {
                Note("UI Toolkit 2021 click mapping failed, the field's own used: " + (ex.InnerException ?? ex).Message);
                return true;
            }
        }

        /// <summary>
        /// TextEditor.SelectToPosition(point) — a drag, by the point measured on the presented form;
        /// after a double-click it snaps to the typed text's words, as the editor does
        /// (<see cref="RtlFieldLayout.WordEdge"/>). A drag snapping to paragraphs stays the editor's.
        /// </summary>
        public static bool Uitk21_SelectToPosition_Prefix(TextEditor __instance, Vector2 __0)
        {
            try
            {
                var s = Uitk21StateOfEditor(__instance);
                if (s == null) return true;
                int under = Uitk21CaretAt(__instance, s, __0);
                if (!Uitk21.DragSelectsWords(__instance))
                {
                    __instance.cursorIndex = under;
                    return false;
                }
                if (!Uitk21.SnapsToWords(__instance)) return true;
                int start = Math.Max(0, Math.Min(Uitk21.DoubleClickAt(__instance), s.Logical.Length));
                if (under < start)
                {
                    __instance.cursorIndex = RtlFieldLayout.WordEdge(s.Logical, under, false);
                    __instance.selectIndex = RtlFieldLayout.WordEdge(s.Logical, start, true);
                }
                else
                {
                    __instance.cursorIndex = RtlFieldLayout.WordEdge(s.Logical, under, true);
                    __instance.selectIndex = RtlFieldLayout.WordEdge(s.Logical, start, false);
                }
                return false;
            }
            catch (Exception ex)
            {
                Note("UI Toolkit 2021 drag mapping failed, the field's own used: " + (ex.InnerException ?? ex).Message);
                return true;
            }
        }

        /// <summary>
        /// TextEditor.UpdateScrollOffset() — what the field scrolls to show its caret: the editor
        /// measured the typed text; the horizontal offset is measured again on the presented caret,
        /// as the editor does (its padding, the caret revealed only when it asked to).
        /// </summary>
        public static void Uitk21_UpdateScrollOffset_Prefix(TextEditor __instance, out bool __state)
        {
            __state = Uitk21.RevealCursor(__instance);
        }

        public static void Uitk21_UpdateScrollOffset_Postfix(TextEditor __instance, bool __state)
        {
            try
            {
                var s = Uitk21StateOfEditor(__instance);
                if (s == null) return;
                var style = Uitk21.StyleOf(__instance) as GUIStyle;
                var position = __instance.position;
                var caret = Uitk21CaretPixel(__instance, s, new Rect(0f, 0f, position.width, position.height));
                Uitk21.SetGraphicalCursor(__instance, caret);
                var view = style.padding.Remove(position);
                float x = caret.x - style.padding.left;
                float width = style.CalcSize(new GUIContent(s.Shown)).x - style.padding.left - style.padding.right;
                var scroll = Uitk21.Scroll(__instance);
                if (width < view.width) scroll.x = 0f;
                else if (__state)
                {
                    if (x + 1f > scroll.x + view.width) scroll.x = x - view.width + 1f;
                    if (x < scroll.x) scroll.x = x;
                }
                Uitk21.SetScroll(__instance, scroll);
            }
            catch (Exception ex) { Note("UI Toolkit 2021 scrolling failed, the field's own used: " + (ex.InnerException ?? ex).Message); }
        }

        /// <summary>
        /// KeyboardTextEditorEventHandler.PreDrawCursor(text): the caret's graphical position — where
        /// an input method shows its candidates — measured again on the presented caret.
        /// </summary>
        public static void Uitk21_PreDrawCursor_Postfix(object __instance)
        {
            try
            {
                if (!(Uitk21.EditorOfHandler(__instance) is TextEditor editor)) return;
                var s = Uitk21StateOfEditor(editor);
                if (s == null) return;
                Uitk21.SetGraphicalCursor(editor, Uitk21CaretPixel(editor, s, Uitk21.LocalPosition(editor)));
            }
            catch (Exception ex) { Note("UI Toolkit 2021 caret position failed: " + (ex.InnerException ?? ex).Message); }
        }

        // ══ Words ════════════════════════════════════════════════════════════════════════════

        // IMGUI finds a word's edges on the typed text, rightly, but classes a vowel sign or a joiner
        // apart (char.IsLetterOrDigit) and stops the word at it — « كَتَبَ » selected as « ت ». The
        // rule every other field of the mod keeps (RtlFieldLayout.WordEdge), through the two callers of
        // FindEndOfClassification: on IL2CPP it is compiled into them, a hook on it never ran.

        /// <summary>TextEditor.SelectCurrentWord() — the double-click.</summary>
        public static bool Uitk21_SelectCurrentWord_Prefix(TextEditor __instance)
        {
            try
            {
                var s = Uitk21StateOfEditor(__instance);
                if (s == null) return true;
                int caret = Math.Max(0, Math.Min(__instance.cursorIndex, s.Logical.Length));
                int back = RtlFieldLayout.WordEdge(s.Logical, caret, false), forward = RtlFieldLayout.WordEdge(s.Logical, caret, true);
                bool before = __instance.cursorIndex < __instance.selectIndex;
                __instance.cursorIndex = before ? back : forward;
                __instance.selectIndex = before ? forward : back;
                Uitk21.WordSelected(__instance);
                return false;
            }
            catch (Exception ex)
            {
                Note("UI Toolkit 2021 word selection failed, the field's own used: " + (ex.InnerException ?? ex).Message);
                return true;
            }
        }

        // ══ Arrow keys ═══════════════════════════════════════════════════════════════════════

        public static bool Uitk21_MoveLeft_Prefix(TextEditor __instance) => !Uitk21Move(__instance, false, false, false);
        public static bool Uitk21_MoveRight_Prefix(TextEditor __instance) => !Uitk21Move(__instance, true, false, false);
        public static bool Uitk21_SelectLeft_Prefix(TextEditor __instance) => !Uitk21Move(__instance, false, true, false);
        public static bool Uitk21_SelectRight_Prefix(TextEditor __instance) => !Uitk21Move(__instance, true, true, false);
        public static bool Uitk21_MoveWordLeft_Prefix(TextEditor __instance) => !Uitk21Move(__instance, false, false, true);
        public static bool Uitk21_MoveWordRight_Prefix(TextEditor __instance) => !Uitk21Move(__instance, true, false, true);

        /// <summary>One arrow press on a presented field's TextEditor, by screen position. True when handled.</summary>
        private static bool Uitk21Move(TextEditor editor, bool toRight, bool shift, bool word)
        {
            try
            {
                var s = Uitk21StateOfEditor(editor);
                if (s == null) return false;
                int anchor = editor.selectIndex, focus = editor.cursorIndex;
                var layout = s.Layout;
                int next;
                if (!shift && anchor != focus)
                {
                    // A selection and no shift: collapse to its end on that side of the screen.
                    bool anchorFurther = toRight ? layout.BoundaryOf(anchor) > layout.BoundaryOf(focus)
                                                 : layout.BoundaryOf(anchor) < layout.BoundaryOf(focus);
                    next = anchorFurther ? anchor : focus;
                }
                // A single-line field scrolls its view, not its text: at the edge of the screen, the caret stays.
                else next = word ? WordStep(layout, s.Logical, focus, toRight) : layout.VisualStep(focus, toRight);

                editor.cursorIndex = next;
                if (!shift) editor.selectIndex = next;
                return true;
            }
            catch (Exception ex)
            {
                Note("UI Toolkit 2021 arrow move failed, the field's own used: " + (ex.InnerException ?? ex).Message);
                return false;
            }
        }

        // ══ UI Toolkit 2021 by reflection ═════════════════════════════════════════════════════

        internal static class Uitk21
        {
            private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            // The class every single-line field gives its input element (TextInputBaseField.singleLineInputUssClassName).
            private const string SingleLineInput = "unity-base-text-field__input--single-line";
            private static bool _resolved, _ok;
            internal static MethodInfo Text, CursorPosition, Rectangle, Click, Drag, Scrolling, PreDrawCursor, SelectCurrentWord;
            private static MethodInfo _clearCursorPos;
            private static MemberInfo _justSelected, _snap, _dblClickAt;
            private static PropertyInfo _handlerEditor, _localPosition;
            private static MemberInfo _revealCursor, _dragWords, _graphicalCursor;
            internal static readonly List<KeyValuePair<string, MethodInfo>> Moves = new List<KeyValuePair<string, MethodInfo>>();
            private static Type _inputType;
            private static MethodInfo _classListContains;
            private static PropertyInfo _visualElement, _contentRect, _currentElement;
            private static MemberInfo _painter;
            private static Type _stylePainterType;
            private static MemberInfo _editorEngine, _textHandle, _isPassword, _selectionColor;
            private static MemberInfo _paramsText, _rectRect, _rectColor;

            internal static bool Resolve()
            {
                if (_resolved) return _ok;
                _resolved = true;
                try
                {
                    // 2021's shape: a TextField whose input element is not a TextElement (2022.3 and
                    // Unity 6 put a TextElement inside, handled by RtlInputFields.UIToolkit).
                    _inputType = AssemblyTypes.Find("UnityEngine.UIElements.TextField+TextInput");
                    var element = AssemblyTypes.Find("UnityEngine.UIElements.TextElement");
                    var visualElement = AssemblyTypes.Find("UnityEngine.UIElements.VisualElement");
                    var utils = AssemblyTypes.Find("UnityEngine.UIElements.MeshGenerationContextUtils");
                    var mgc = AssemblyTypes.Find("UnityEngine.UIElements.MeshGenerationContext");
                    var generator = AssemblyTypes.Find("UnityEngine.TextCore.Text.TextGenerator");
                    var textInfo = AssemblyTypes.Find("UnityEngine.TextCore.Text.TextInfo");
                    if (_inputType == null || visualElement == null || utils == null || mgc == null || generator == null || textInfo == null
                        || (element != null && element.IsAssignableFrom(_inputType)))
                    {
                        TranslatorCore.LogDebug("[RtlInputFields] UI Toolkit fields not 2021's shape");
                        return _ok = false;
                    }
                    // Fields with a TextElement inside (2022.3, Unity 6) take the other path.
                    if (Members.FieldOrProperty(_inputType, "textElement", Inst) != null || InHierarchy(_inputType, "textElement") != null)
                        return _ok = false;

                    foreach (var m in utils.GetMethods(Stat))
                    {
                        var ps = m.GetParameters();
                        if (m.Name == "Text" && ps.Length == 4 && ps[1].ParameterType.Name == "TextParams" && ps[3].ParameterType == typeof(float)) Text = m;
                        if (m.Name == "Rectangle" && ps.Length == 2 && ps[1].ParameterType.Name == "RectangleParams") Rectangle = m;
                    }
                    CursorPosition = generator.GetMethod("GetCursorPosition", Stat, null, new[] { textInfo, typeof(Rect), typeof(int), typeof(bool) }, null);
                    Click = typeof(TextEditor).GetMethod("MoveCursorToPosition_Internal", Inst, null, new[] { typeof(Vector2), typeof(bool) }, null);
                    Drag = typeof(TextEditor).GetMethod("SelectToPosition", Inst, null, new[] { typeof(Vector2) }, null);
                    Scrolling = typeof(TextEditor).GetMethod("UpdateScrollOffset", Inst, null, Type.EmptyTypes, null);
                    PreDrawCursor = AssemblyTypes.Find("UnityEngine.UIElements.KeyboardTextEditorEventHandler")
                        ?.GetMethod("PreDrawCursor", Inst, null, new[] { typeof(string) }, null);
                    SelectCurrentWord = typeof(TextEditor).GetMethod("SelectCurrentWord", Inst, null, Type.EmptyTypes, null);
                    _clearCursorPos = typeof(TextEditor).GetMethod("ClearCursorPos", Inst, null, Type.EmptyTypes, null);
                    _justSelected = Members.FieldOrProperty(typeof(TextEditor), "m_bJustSelected", Inst);
                    _snap = Members.FieldOrProperty(typeof(TextEditor), "m_DblClickSnap", Inst);
                    _dblClickAt = Members.FieldOrProperty(typeof(TextEditor), "m_DblClickInitPos", Inst);
                    if (_clearCursorPos == null || _justSelected == null) SelectCurrentWord = null;
                    _handlerEditor = Members.Property(AssemblyTypes.Find("UnityEngine.UIElements.TextEditorEventHandler"), "editorEngine", Inst);
                    _localPosition = Members.Property(typeof(TextEditor), "localPosition", Inst);
                    _revealCursor = Members.FieldOrProperty(typeof(TextEditor), "m_RevealCursor", Inst);
                    _dragWords = Members.FieldOrProperty(typeof(TextEditor), "m_MouseDragSelectsWholeWords", Inst);
                    _graphicalCursor = Members.FieldOrProperty(typeof(TextEditor), "graphicalCursorPos", BindingFlags.Instance | BindingFlags.Public);
                    if (_localPosition == null) { Click = null; Drag = null; Scrolling = null; PreDrawCursor = null; }
                    if (_revealCursor == null || _graphicalCursor == null) Scrolling = null;
                    if (_dragWords == null || _snap == null || _dblClickAt == null) Drag = null;
                    if (_handlerEditor == null || _graphicalCursor == null) PreDrawCursor = null;
                    foreach (var move in new[] { "MoveLeft", "MoveRight", "SelectLeft", "SelectRight", "MoveWordLeft", "MoveWordRight" })
                    {
                        var m = typeof(TextEditor).GetMethod(move, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                        if (m != null) Moves.Add(new KeyValuePair<string, MethodInfo>("Uitk21_" + move + "_Prefix", m));
                    }

                    _classListContains = visualElement.GetMethod("ClassListContains", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                    _visualElement = Members.Property(mgc, "visualElement", Inst);
                    _painter = Members.FieldOrProperty(mgc, "painter", Inst);
                    _stylePainterType = AssemblyTypes.Find("UnityEngine.UIElements.UIR.Implementation.UIRStylePainter");
                    _currentElement = Members.Property(_stylePainterType, "currentElement", Inst);
                    _contentRect = Members.Property(visualElement, "contentRect", BindingFlags.Instance | BindingFlags.Public);
                    _editorEngine = InHierarchy(_inputType, "editorEngine");
                    _textHandle = InHierarchy(_inputType, "m_TextHandle");
                    _isPassword = InHierarchy(_inputType, "isPasswordField");
                    _selectionColor = InHierarchy(_inputType, "selectionColor");
                    if (Text != null) _paramsText = Members.FieldOrProperty(Text.GetParameters()[1].ParameterType, "text", Inst);
                    if (Rectangle != null)
                    {
                        var rectParams = Rectangle.GetParameters()[1].ParameterType;
                        _rectRect = Members.FieldOrProperty(rectParams, "rect", Inst);
                        _rectColor = Members.FieldOrProperty(rectParams, "color", Inst);
                    }

                    _ok = Text != null && CursorPosition != null && _classListContains != null && _visualElement != null
                          && _editorEngine != null && _textHandle != null && _paramsText != null;
                    if (!_ok) Note($"UI Toolkit 2021 fields: a member this needs is missing — left to the field's own drawing (text={Text != null} caret={CursorPosition != null} element={_visualElement != null && _classListContains != null} editor={_editorEngine != null} handle={_textHandle != null})");
                    if (Rectangle != null && (_rectRect == null || _rectColor == null || _selectionColor == null || _contentRect == null))
                    { Rectangle = null; Note("UI Toolkit 2021 fields: the selection's drawing members are missing — the selection is the field's"); }
                }
                catch (Exception ex) { _ok = false; Note("UI Toolkit 2021 fields unavailable: " + ex.Message); }
                return _ok;
            }

            /// <summary>
            /// A member declared anywhere above this type — private ones of a base included — that
            /// can be read: TextField.TextInput overrides isPasswordField with a setter alone.
            /// </summary>
            private static MemberInfo InHierarchy(Type type, string name)
            {
                for (var t = type; t != null; t = t.BaseType)
                {
                    var member = Members.FieldOrProperty(t, name, Inst | BindingFlags.DeclaredOnly);
                    if (member is PropertyInfo p && p.GetGetMethod(true) == null) continue;
                    if (member != null) return member;
                }
                return null;
            }

            /// <summary>
            /// The element a context draws. Through its painter's own member: the public
            /// MeshGenerationContext.visualElement goes through the IStylePainter interface, whose
            /// getter an IL2CPP build does not keep ("Method not found … get_visualElement").
            /// </summary>
            private static object DrawnElement(object mgc)
            {
                if (_painter != null && _currentElement != null)
                {
                    var painter = TypeHelper.Il2CppCast(Members.Get(_painter, mgc), _stylePainterType);
                    if (painter != null && _stylePainterType.IsInstanceOfType(painter)) return _currentElement.GetValue(painter, null);
                }
                return _visualElement.GetValue(mgc, null);
            }

            /// <summary>The element a context draws, when it is a single-line text field's input; null otherwise.</summary>
            internal static object InputOf(object mgc)
            {
                var element = DrawnElement(mgc);
                if (element == null || !(_classListContains.Invoke(element, new object[] { SingleLineInput }) is bool single) || !single) return null;
                var input = TypeHelper.Il2CppCast(element, _inputType);
                return _inputType.IsInstanceOfType(input) ? input : null;
            }

            internal static bool IsPassword(object element) =>
                _isPassword != null && Members.Get(_isPassword, element) is bool password && password;

            internal static object EditorOf(object element) => Members.Get(_editorEngine, element);
            // TextEditor.style and .scrollOffset are FIELDS on Mono and properties of the IL2CPP
            // interop: named in code, the method using them failed on IL2CPP ("Field not found:
            // 'UnityEngine.TextEditor.style'") — read by name, whichever they are.
            private static readonly MemberInfo _editorStyle = Members.FieldOrProperty(typeof(TextEditor), "style", BindingFlags.Instance | BindingFlags.Public);
            private static readonly MemberInfo _editorScroll = Members.FieldOrProperty(typeof(TextEditor), "scrollOffset", BindingFlags.Instance | BindingFlags.Public);

            internal static object StyleOf(object editor) => editor == null || _editorStyle == null ? null : Members.Get(_editorStyle, editor);
            internal static string EditorText(object editor) => (editor as TextEditor)?.text;
            internal static int CursorIndex(object editor) => ((TextEditor)editor).cursorIndex;
            internal static int SelectIndex(object editor) => ((TextEditor)editor).selectIndex;
            internal static Vector2 Scroll(object editor) => _editorScroll != null && Members.Get(_editorScroll, editor) is Vector2 scroll ? scroll : Vector2.zero;
            internal static float ScrollX(object editor) => Scroll(editor).x;
            internal static void SetScroll(object editor, Vector2 scroll) => Members.Set(_editorScroll, editor, scroll);
            internal static void SetGraphicalCursor(object editor, Vector2 at) => Members.Set(_graphicalCursor, editor, at);
            internal static Rect LocalPosition(object editor) => (Rect)_localPosition.GetValue(editor, null);
            internal static bool RevealCursor(object editor) => Members.Get(_revealCursor, editor) is bool reveal && reveal;
            internal static bool DragSelectsWords(object editor) => Members.Get(_dragWords, editor) is bool words && words;
            internal static bool SnapsToWords(object editor) => Members.Get(_snap, editor)?.ToString() == "WORDS";
            internal static int DoubleClickAt(object editor) => Convert.ToInt32(Members.Get(_dblClickAt, editor));

            /// <summary>What SelectCurrentWord does once the word is chosen.</summary>
            internal static void WordSelected(object editor)
            {
                _clearCursorPos.Invoke(editor, null);
                Members.Set(_justSelected, editor, true);
            }
            internal static object EditorOfHandler(object handler) => _handlerEditor.GetValue(handler, null);

            /// <summary>
            /// The text info the element's text handle draws with (TextCore's, a player's), or null.
            /// The handle is held as its ITextHandle interface: on IL2CPP it comes wrapped as that
            /// interface, without textInfoMesh, until cast to the TextCoreHandle it is (bench, 2021.3).
            /// </summary>
            internal static object TextInfoOf(object element)
            {
                var handle = Members.Get(_textHandle, element);
                if (handle == null) return null;
                if (_coreHandleType != null) handle = TypeHelper.Il2CppCast(handle, _coreHandleType);
                var info = Members.Property(handle.GetType(), "textInfoMesh", Inst);
                return info?.GetValue(handle, null);
            }

            private static readonly Type _coreHandleType = AssemblyTypes.Find("UnityEngine.UIElements.TextCoreHandle");

            internal static string ParamsText(object parameters) => Members.Get(_paramsText, parameters) as string;
            internal static void SetParamsText(object parameters, string text) => Members.Set(_paramsText, parameters, text);

            internal static Rect ParamsRect(object parameters) => (Rect)Members.Get(_rectRect, parameters);
            internal static void SetParamsRect(object parameters, Rect rect) => Members.Set(_rectRect, parameters, rect);

            /// <summary>The selection's rectangle: drawn in the field's selection colour (its caret is drawn in its caret colour).</summary>
            internal static bool IsSelectionRect(object element, object parameters) =>
                Members.Get(_rectColor, parameters) is Color color && Members.Get(_selectionColor, element) is Color selection && color == selection;

            internal static Rect ContentRect(object element) => (Rect)_contentRect.GetValue(element, null);

            internal static Vector2 PositionOf(object textInfo, Rect rect, int displayIndex) =>
                (Vector2)CursorPosition.Invoke(null, new object[] { textInfo, rect, displayIndex, true });
        }
    }
}
