using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Right-to-left editing in UI Toolkit's text fields on Unity 6 (6000.0 → 6000.6, one shape),
    /// standard text generator — the same experience as uGUI's, TMP's and NGUI's
    /// (analyse/rtl-saisie-ngui-uitk.md). The Advanced Text Generator does bidi and shaping itself and
    /// is left to it.
    ///
    /// A field's text element keeps the typed value (<c>m_Text</c>) apart from what it draws
    /// (<c>m_RenderedText</c>, set by <c>SetRenderedText</c> from the value): the drawn text gets the
    /// presented form (<see cref="RtlFieldLayout.Display"/>), the value stays as typed, and every
    /// place the field reads a position in the drawn text is told the typed one instead:
    ///
    /// - the caret: <c>TextHandle.GetCursorPositionFromStringIndexUsing{Line,Character}Height(index)</c>
    ///   gets the display gap of the typed caret;
    /// - the click and the drag: <c>TextHandle.GetCursorIndexFromPosition</c> answers a display gap,
    ///   turned into the caret standing there;
    /// - the arrows: <c>TextSelectingUtilities.MoveLeft/MoveRight/SelectLeft/SelectRight</c> and the
    ///   word moves step by screen position (user's decision, 2026-09-25);
    /// - the selection: <c>TextElement.DrawHighlighting</c> draws one rectangle per piece the typed
    ///   range shows (a mixed line selects in several pieces).
    ///
    /// Up/Down and Home/End stay the field's. Every member found by name — the Core names no UI
    /// Toolkit type — and a missing one said: that gesture stays the engine's.
    /// </summary>
    internal static partial class RtlInputFields
    {
        private sealed class UitkState
        {
            public WeakReference Element;
            public RtlFieldLayout Layout;
            public string Logical;
            public string Shown;
        }

        // By the element's id (UIToolkitSupport.IdFor): a text element is no UnityEngine.Object.
        private static readonly Dictionary<long, UitkState> _uitk = new Dictionary<long, UitkState>();
        [ThreadStatic] private static bool _uitkRawIndices;

        /// <summary>The hooks, when this engine has the shape they are written for (Unity 6).</summary>
        internal static int PatchUitkFields(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            if (!Uitk.Resolve()) return 0;
            var fields = typeof(RtlInputFields);
            MethodInfo Hook(string name) => fields.GetMethod(name, BindingFlags.Static | BindingFlags.Public);
            int count = 0;
            patcher(Uitk.SetValueWithoutNotify, null, Hook(nameof(Uitk_SetValueWithoutNotify_Postfix))); count++;
            if (Uitk.PositionByLine != null) { patcher(Uitk.PositionByLine, Hook(nameof(Uitk_CursorPosition_Prefix)), null); count++; }
            if (Uitk.PositionByCharacter != null) { patcher(Uitk.PositionByCharacter, Hook(nameof(Uitk_CursorPosition_Prefix)), null); count++; }
            if (Uitk.IndexFromPosition != null) { patcher(Uitk.IndexFromPosition, null, Hook(nameof(Uitk_CursorIndexFromPosition_Postfix))); count++; }
            foreach (var move in Uitk.Moves)
            {
                patcher(move.Value, Hook(move.Key), null);
                count++;
            }
            if (Uitk.DrawHighlighting != null) { patcher(Uitk.DrawHighlighting, Hook(nameof(Uitk_DrawHighlighting_Prefix)), null); count++; }
            if (Uitk.PositionByLine == null || Uitk.IndexFromPosition == null || Uitk.Moves.Count < 6 || Uitk.DrawHighlighting == null)
                TranslatorCore.LogWarning($"[Patches] UI Toolkit field right-to-left editing incomplete: caret={(Uitk.PositionByLine != null)} click={(Uitk.IndexFromPosition != null)} arrows={Uitk.Moves.Count}/6 selection={(Uitk.DrawHighlighting != null)}");
            return count;
        }

        // ══ What the field draws ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// After TextElement's SetValueWithoutNotify (every write of a field's value, typed or set):
        /// the text it draws (<c>m_RenderedText</c>, just set from the value) becomes the presented
        /// form when the field shows right-to-left text, or text needing shaping; the value stays as
        /// typed. Anything else — a label, a password, a field on the Advanced Text Generator — as is.
        /// ⚠ After the method, not inside SetRenderedText: that small private setter is compiled into
        /// this one on IL2CPP, and a hook on it never ran there.
        /// </summary>
        public static void Uitk_SetValueWithoutNotify_Postfix(object __instance)
        {
            try
            {
                if (!TranslatorCore.IsMainThread || __instance == null) return;
                long id = UIToolkitSupport.IdFor(__instance);
                if (!Uitk.IsPresentableField(__instance))
                {
                    _uitk.Remove(id);
                    return;
                }
                string rendered = Uitk.RenderedText(__instance);
                if (_uitk.TryGetValue(id, out var held) && held.Shown == rendered) return;
                var prep = string.IsNullOrEmpty(rendered) ? null : RtlFieldLayout.Prepare(rendered);
                if (prep == null) { _uitk.Remove(id); return; }

                var s = new UitkState { Element = new WeakReference(__instance), Logical = rendered };
                s.Layout = prep.Lay(null);
                s.Shown = s.Layout.Display;
                _uitk[id] = s;
                TranslatorCore.RegisterPresentedText(s.Shown, rendered);
                Describe("UI Toolkit", __instance, rendered, s.Shown);
                Uitk.SetRenderedTextField(__instance, s.Shown);
            }
            catch (Exception ex) { Note("UI Toolkit field presentation failed, drawn as typed: " + ex.Message); }
        }

        /// <summary>The presented state of the element a text handle draws for, while it draws what was laid out.</summary>
        private static UitkState UitkStateOfHandle(object handle) =>
            handle == null || _uitk.Count == 0 ? null : UitkStateOf(Uitk.ElementOf(handle));

        private static UitkState UitkStateOf(object element)
        {
            if (element == null || _uitk.Count == 0) return null;
            if (!_uitk.TryGetValue(UIToolkitSupport.IdFor(element), out var s)) return null;
            return Uitk.RenderedText(element) == s.Shown ? s : null;
        }

        // ══ Caret and click ═══════════════════════════════════════════════════════════════════

        /// <summary>TextHandle.GetCursorPositionFromStringIndexUsing{Line,Character}Height: the typed caret's display gap.</summary>
        public static void Uitk_CursorPosition_Prefix(object __instance, ref int __0)
        {
            if (_uitkRawIndices) return;
            try
            {
                var s = UitkStateOfHandle(__instance);
                if (s != null) __0 = s.Layout.BoundaryOf(__0);
            }
            catch (Exception ex) { Note("UI Toolkit caret mapping failed: " + ex.Message); }
        }

        /// <summary>TextHandle.GetCursorIndexFromPosition: the display gap under the pointer, made the caret standing there.</summary>
        public static void Uitk_CursorIndexFromPosition_Postfix(object __instance, ref int __result)
        {
            try
            {
                var s = UitkStateOfHandle(__instance);
                if (s != null) __result = s.Layout.CaretAtBoundary(__result);
            }
            catch (Exception ex) { Note("UI Toolkit click mapping failed: " + ex.Message); }
        }

        // ══ Arrow keys ═══════════════════════════════════════════════════════════════════════

        public static bool Uitk_MoveLeft_Prefix(object __instance) => !UitkMove(__instance, false, false, false);
        public static bool Uitk_MoveRight_Prefix(object __instance) => !UitkMove(__instance, true, false, false);
        public static bool Uitk_SelectLeft_Prefix(object __instance) => !UitkMove(__instance, false, true, false);
        public static bool Uitk_SelectRight_Prefix(object __instance) => !UitkMove(__instance, true, true, false);
        public static bool Uitk_MoveWordLeft_Prefix(object __instance) => !UitkMove(__instance, false, false, true);
        public static bool Uitk_MoveWordRight_Prefix(object __instance) => !UitkMove(__instance, true, false, true);

        /// <summary>One arrow press on a presented field's selecting utilities, by screen position. True when handled.</summary>
        private static bool UitkMove(object utilities, bool toRight, bool shift, bool word)
        {
            try
            {
                var s = UitkStateOfHandle(Uitk.HandleOf(utilities));
                if (s == null) return false;
                int anchor = Uitk.SelectIndex(utilities), focus = Uitk.CursorIndex(utilities);
                var layout = s.Layout;
                int next;
                if (!shift && anchor != focus)
                {
                    // A selection and no shift: collapse to its end on that side of the screen.
                    bool anchorFurther = toRight ? layout.BoundaryOf(anchor) > layout.BoundaryOf(focus)
                                                 : layout.BoundaryOf(anchor) < layout.BoundaryOf(focus);
                    next = anchorFurther ? anchor : focus;
                }
                // The whole text is drawn (a field scrolls its view, not its text): at the edge of
                // the screen, the caret stays.
                else next = word ? WordStep(layout, s.Logical, focus, toRight) : layout.VisualStep(focus, toRight);

                Uitk.SetCursorIndex(utilities, next);
                if (!shift) Uitk.SetSelectIndex(utilities, next);
                return true;
            }
            catch (Exception ex)
            {
                Note("UI Toolkit arrow move failed, the field's own used: " + (ex.InnerException ?? ex).Message);
                return false;
            }
        }

        // ══ Selection ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// TextElement.DrawHighlighting(mgc): one rectangle per piece of the screen the typed range
        /// shows, as the element draws its own — positions from its text handle, in display gaps.
        /// </summary>
        public static bool Uitk_DrawHighlighting_Prefix(object __instance, object __0)
        {
            try
            {
                var s = UitkStateOf(__instance);
                if (s == null || __0 == null) return true;
                var utilities = Uitk.UtilitiesOf(__instance);
                var handle = Uitk.HandleOfElement(__instance);
                if (utilities == null || handle == null) return true;
                int a = Uitk.CursorIndex(utilities), b = Uitk.SelectIndex(utilities);
                var pieces = DisplayPieces(s.Layout, s.Logical, Math.Min(a, b), Math.Max(a, b));
                Vector2 origin = Uitk.ContentMin(__instance);
                Color color = Uitk.SelectionColor(__instance);
                _uitkRawIndices = true;
                try
                {
                    foreach (var piece in pieces)
                    {
                        Vector2 from = Uitk.PositionOf(handle, piece.Key), to = Uitk.PositionOf(handle, piece.Value);
                        float height = Uitk.LineHeightAt(handle, piece.Key);
                        Uitk.DrawRectangle(__0, new Rect(origin.x + from.x, origin.y + from.y - height, to.x - from.x, height), color);
                    }
                }
                finally { _uitkRawIndices = false; }
                return false;
            }
            catch (Exception ex)
            {
                Note("UI Toolkit selection drawing failed, the field's own used: " + (ex.InnerException ?? ex).Message);
                return true;
            }
        }

        // ══ UI Toolkit by reflection ══════════════════════════════════════════════════════════

        internal static class Uitk
        {
            private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private static bool _resolved, _ok;
            internal static MethodInfo SetValueWithoutNotify, PositionByLine, PositionByCharacter, IndexFromPosition, DrawHighlighting;
            internal static readonly List<KeyValuePair<string, MethodInfo>> Moves = new List<KeyValuePair<string, MethodInfo>>();
            private static MemberInfo _isInputField, _isPassword, _renderedText, _handleElement, _utilitiesHandle;
            private static MemberInfo _selectingManipulator, _manipulatorUtilities, _uitkTextHandle, _selectionColor;
            private static PropertyInfo _cursorIndex, _selectIndex, _contentRect;
            private static MethodInfo _isAdvanced, _lineNumber, _lineHeight, _meshGenerator;

            internal static bool Resolve()
            {
                if (_resolved) return _ok;
                _resolved = true;
                try
                {
                    var element = AssemblyTypes.Find("UnityEngine.UIElements.TextElement");
                    var handle = AssemblyTypes.Find("UnityEngine.TextCore.Text.TextHandle");
                    var uitkHandle = AssemblyTypes.Find("UnityEngine.UIElements.UITKTextHandle");
                    var utilities = AssemblyTypes.Find("UnityEngine.TextSelectingUtilities");
                    var textUtilities = AssemblyTypes.Find("UnityEngine.UIElements.TextUtilities");
                    if (element == null || handle == null || uitkHandle == null || utilities == null)
                    {
                        TranslatorCore.LogDebug($"[RtlInputFields] UI Toolkit fields not Unity 6's shape: TextElement={element != null} TextHandle={handle != null} UITKTextHandle={uitkHandle != null} TextSelectingUtilities={utilities != null}");
                        return _ok = false;
                    }

                    // Not Unity 6's shape (no rendered text apart from the value): another engine's fields.
                    if (element.GetMethod("SetRenderedText", Inst, null, new[] { typeof(string) }, null) == null)
                    {
                        TranslatorCore.LogDebug("[RtlInputFields] UI Toolkit fields not Unity 6's shape: no TextElement.SetRenderedText(string)");
                        return _ok = false;
                    }
                    // The explicit INotifyValueChanged<string> implementation: its name is the interface's
                    // on Mono and mangled by the IL2CPP interop — found by what it ends with.
                    foreach (var m in element.GetMethods(Inst))
                        if (m.Name.EndsWith("SetValueWithoutNotify", StringComparison.Ordinal) && m.DeclaringType == element
                            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
                            SetValueWithoutNotify = m;

                    _isInputField = Members.FieldOrProperty(element, "isInputField", Inst);
                    _isPassword = Members.FieldOrProperty(element, "isPassword", Inst);
                    _renderedText = Members.FieldOrProperty(element, "m_RenderedText", Inst);
                    _uitkTextHandle = Members.FieldOrProperty(element, "uitkTextHandle", Inst);
                    _selectingManipulator = Members.FieldOrProperty(element, "selectingManipulator", Inst);
                    _selectionColor = Members.FieldOrProperty(element, "selectionColor", Inst);
                    _contentRect = Members.Property(element, "contentRect", BindingFlags.Instance | BindingFlags.Public);
                    _manipulatorUtilities = Members.FieldOrProperty(Members.TypeOf(_selectingManipulator), "m_SelectingUtilities", Inst);
                    _handleElement = Members.FieldOrProperty(uitkHandle, "m_TextElement", Inst);
                    _utilitiesHandle = Members.FieldOrProperty(utilities, "textHandle", Inst);
                    _cursorIndex = Members.Property(utilities, "cursorIndex", Inst);
                    _selectIndex = Members.Property(utilities, "selectIndex", Inst);
                    _isAdvanced = textUtilities?.GetMethod("IsAdvancedTextEnabledForElement", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    PositionByLine = handle.GetMethod("GetCursorPositionFromStringIndexUsingLineHeight", Inst, null, new[] { typeof(int), typeof(bool), typeof(bool) }, null);
                    PositionByCharacter = handle.GetMethod("GetCursorPositionFromStringIndexUsingCharacterHeight", Inst, null, new[] { typeof(int), typeof(bool) }, null);
                    IndexFromPosition = handle.GetMethod("GetCursorIndexFromPosition", Inst, null, new[] { typeof(Vector2), typeof(bool) }, null);
                    _lineNumber = handle.GetMethod("GetLineNumber", Inst, null, new[] { typeof(int) }, null);
                    _lineHeight = handle.GetMethod("GetLineHeight", Inst, null, new[] { typeof(int) }, null);
                    foreach (var move in new[] { "MoveLeft", "MoveRight", "SelectLeft", "SelectRight", "MoveWordLeft", "MoveWordRight" })
                    {
                        var m = utilities.GetMethod(move, Inst, null, Type.EmptyTypes, null);
                        if (m != null) Moves.Add(new KeyValuePair<string, MethodInfo>("Uitk_" + move + "_Prefix", m));
                    }
                    var mgc = AssemblyTypes.Find("UnityEngine.UIElements.MeshGenerationContext");
                    _meshGenerator = Members.Property(mgc, "meshGenerator", Inst)?.GetMethod;
                    DrawHighlighting = element.GetMethod("DrawHighlighting", Inst, null, mgc == null ? Type.EmptyTypes : new[] { mgc }, null);

                    _ok = SetValueWithoutNotify != null && _isInputField != null && _renderedText != null && _handleElement != null && _utilitiesHandle != null
                          && _cursorIndex != null && _selectIndex != null && _isAdvanced != null;
                    if (!_ok) Note("UI Toolkit fields: a member this needs is missing — left to the field's own drawing");
                    if (DrawHighlighting != null && (_selectingManipulator == null || _manipulatorUtilities == null || _uitkTextHandle == null
                                                     || _selectionColor == null || _contentRect == null || _meshGenerator == null
                                                     || _lineNumber == null || _lineHeight == null || PositionByLine == null))
                    { DrawHighlighting = null; Note("UI Toolkit fields: the selection's drawing members are missing — the selection is the field's"); }
                }
                catch (Exception ex) { _ok = false; Note("UI Toolkit fields unavailable: " + ex.Message); }
                return _ok;
            }

            /// <summary>A field's text element, drawing with the standard generator, not a password.</summary>
            internal static bool IsPresentableField(object element)
            {
                if (!(Members.Get(_isInputField, element) is bool input) || !input) return false;
                if (_isPassword != null && Members.Get(_isPassword, element) is bool password && password) return false;
                return !(bool)_isAdvanced.Invoke(null, new[] { element });
            }

            internal static string RenderedText(object element) => Members.Get(_renderedText, element) as string;
            internal static void SetRenderedTextField(object element, string value) => Members.Set(_renderedText, element, value);
            internal static object ElementOf(object handle) => Members.Get(_handleElement, handle);
            internal static object HandleOf(object utilities) => Members.Get(_utilitiesHandle, utilities);
            internal static object HandleOfElement(object element) => Members.Get(_uitkTextHandle, element);

            internal static object UtilitiesOf(object element)
            {
                var manipulator = Members.Get(_selectingManipulator, element);
                return manipulator == null ? null : Members.Get(_manipulatorUtilities, manipulator);
            }

            internal static int CursorIndex(object utilities) => Convert.ToInt32(_cursorIndex.GetValue(utilities, null));
            internal static int SelectIndex(object utilities) => Convert.ToInt32(_selectIndex.GetValue(utilities, null));
            internal static void SetCursorIndex(object utilities, int value) => _cursorIndex.SetValue(utilities, value, null);
            internal static void SetSelectIndex(object utilities, int value) => _selectIndex.SetValue(utilities, value, null);

            internal static Vector2 PositionOf(object handle, int displayIndex) =>
                (Vector2)PositionByLine.Invoke(handle, new object[] { displayIndex, false, true });

            internal static float LineHeightAt(object handle, int displayIndex)
            {
                int line = Convert.ToInt32(_lineNumber.Invoke(handle, new object[] { displayIndex }));
                return Convert.ToSingle(_lineHeight.Invoke(handle, new object[] { line }));
            }

            internal static Vector2 ContentMin(object element) => ((Rect)_contentRect.GetValue(element, null)).min;
            internal static Color SelectionColor(object element) => (Color)Members.Get(_selectionColor, element);

            private static MethodInfo _drawRectangle;
            private static Type _rectParams;
            private static MemberInfo _paramsRect, _paramsColor, _paramsTint;

            /// <summary>One rectangle through the context's mesh generator, as the element draws its own highlight.</summary>
            internal static void DrawRectangle(object mgc, Rect rect, Color color)
            {
                var generator = _meshGenerator.Invoke(mgc, null);
                if (_drawRectangle == null)
                {
                    foreach (var m in generator.GetType().GetMethods(Inst))
                        if (m.Name == "DrawRectangle" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "RectangleParams")
                        { _drawRectangle = m; _rectParams = m.GetParameters()[0].ParameterType; }
                    if (_drawRectangle == null) throw new MissingMethodException(generator.GetType().Name, "DrawRectangle");
                    _paramsRect = Members.FieldOrProperty(_rectParams, "rect", Inst);
                    _paramsColor = Members.FieldOrProperty(_rectParams, "color", Inst);
                    _paramsTint = Members.FieldOrProperty(_rectParams, "playmodeTintColor", Inst);
                }
                // Boxed once, written in place: a struct copied at each write would lose the earlier ones.
                object parameters = Activator.CreateInstance(_rectParams);
                Members.Set(_paramsRect, parameters, rect);
                Members.Set(_paramsColor, parameters, color);
                if (_paramsTint != null) Members.Set(_paramsTint, parameters, Color.white);
                _drawRectangle.Invoke(generator, new[] { parameters });
            }
        }
    }
}
