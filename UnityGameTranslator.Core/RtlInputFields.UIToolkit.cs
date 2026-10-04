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
            public RtlFieldLayout Layout;
            public string Logical;
            public string Shown;
            public string Suffix;   // what the element keeps after the text it draws (a zero-width space in 2022.3)
            public string Rendered => Shown + Suffix;
        }

        // 2022.3's TextElement keeps a zero-width space after the text it draws (renderedText's setter):
        // the typed text is laid out without it, and it is put back after the presented form.
        private static readonly string RenderedSuffix = ((char)0x200B).ToString();

        // By the element's key (ObjectKey): a text element is no UnityEngine.Object.
        private static readonly Dictionary<long, UitkState> _uitk = new Dictionary<long, UitkState>();

        private static readonly Type _il2cppObject = Type.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase, Il2CppInterop.Runtime");
        private static readonly PropertyInfo _il2cppPointer = _il2cppObject?.GetProperty("Pointer", BindingFlags.Instance | BindingFlags.Public);

        /// <summary>
        /// The same number for the same engine object, every time it is met. On IL2CPP an object
        /// reaches managed code through a wrapper the interop may let go and make again: keyed by
        /// the wrapper, a field's text info or editor met later no longer found its field (bench,
        /// 2021.3 IL2CPP: one case in eight left unmapped). The native pointer is the object; on
        /// Mono the object is itself (UIToolkitSupport.IdFor).
        /// </summary>
        private static long ObjectKey(object o)
        {
            if (o != null && _il2cppPointer != null && _il2cppObject.IsInstanceOfType(o))
                return ((IntPtr)_il2cppPointer.GetValue(o, null)).ToInt64();
            return UIToolkitSupport.IdFor(o);
        }
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
            if (Uitk.WordEdges != null) { patcher(Uitk.WordEdges, Hook(nameof(Uitk_FindEndOfClassification_Prefix)), null); count++; }
            if (Uitk.PositionByLine == null || Uitk.IndexFromPosition == null || Uitk.Moves.Count < 6 || Uitk.DrawHighlighting == null || Uitk.WordEdges == null)
                TranslatorCore.LogWarning($"[Patches] UI Toolkit field right-to-left editing incomplete: caret={(Uitk.PositionByLine != null)} click={(Uitk.IndexFromPosition != null)} arrows={Uitk.Moves.Count}/6 selection={(Uitk.DrawHighlighting != null)} words={(Uitk.WordEdges != null)}");
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
                long id = ObjectKey(__instance);
                var kind = Uitk.KindOf(__instance);
                if (kind != Uitk.FieldKind.Standard) _uitk.Remove(id);
                if (kind == Uitk.FieldKind.Advanced)
                {
                    // 🔴 The Advanced Text Generator does the bidi and the caret itself, but reads
                    // the paragraph's direction from the element (languageDirection, Inherit → left
                    // to right): a typed Arabic sentence came out with its words in order and its
                    // blocks left to right — and it is the DEFAULT generator from 6000.6 on
                    // (InitialStyle.unityTextGenerator = Advanced; Standard in 6000.3). The same
                    // answer the mod gives an ATG label (RtlPresenter): the direction of the typed
                    // text's paragraph (UAX #9 P2, a field draws its markup as text), put back when
                    // it reads left to right again.
                    string value = UIToolkitSupport.GetElementText(__instance);
                    if (RtlText.ParagraphDirection(value) < 0) UIToolkitSupport.SetRtlDirection(__instance);
                    else UIToolkitSupport.RestoreRtlAdjustments(__instance);
                    return;
                }
                if (kind == Uitk.FieldKind.None) return;
                string rendered = Uitk.RenderedText(__instance);
                if (_uitk.TryGetValue(id, out var held) && held.Rendered == rendered) return;
                string suffix = rendered != null && rendered.EndsWith(RenderedSuffix, StringComparison.Ordinal) ? RenderedSuffix : "";
                string typed = rendered?.Substring(0, rendered.Length - suffix.Length);
                var prep = string.IsNullOrEmpty(typed) ? null : RtlFieldLayout.Prepare(typed);
                if (prep == null) { _uitk.Remove(id); return; }

                var s = new UitkState { Logical = typed, Suffix = suffix };
                s.Layout = prep.Lay(null);
                s.Shown = s.Layout.Display;
                _uitk[id] = s;
                TranslatorCore.RegisterPresentedText(s.Shown, typed);
                Describe("UI Toolkit", __instance, typed, s.Shown);
                Uitk.SetRenderedTextField(__instance, s.Rendered);
            }
            catch (Exception ex) { Note("UI Toolkit field presentation failed, drawn as typed: " + ex.Message); }
        }

        /// <summary>The presented state of the element a text handle draws for, while it draws what was laid out.</summary>
        private static UitkState UitkStateOfHandle(object handle) =>
            handle == null || _uitk.Count == 0 ? null : UitkStateOf(Uitk.ElementOf(handle));

        private static UitkState UitkStateOf(object element)
        {
            if (element == null || _uitk.Count == 0) return null;
            if (!_uitk.TryGetValue(ObjectKey(element), out var s)) return null;
            return Uitk.RenderedText(element) == s.Rendered ? s : null;
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

        // A field the Advanced Text Generator draws: no presented form, only the typed text's layout,
        // for the arrows (the ATG draws, places the caret and answers clicks itself, bidi included).
        private static readonly Dictionary<long, UitkState> _uitkAtg = new Dictionary<long, UitkState>();

        /// <summary>
        /// The layout of an ATG field's typed text — the same Unicode bidi the ATG applies — when it
        /// reads right to left somewhere; null otherwise. The ATG's own arrows step through the TYPED
        /// order: on a mixed line they went the other way in the Latin runs (bench, 6000.6.3).
        /// </summary>
        private static UitkState UitkAtgStateOf(object handle)
        {
            var element = Uitk.ElementOf(handle);
            if (element == null || Uitk.KindOf(element) != Uitk.FieldKind.Advanced) return null;
            string value = UIToolkitSupport.GetElementText(element);
            long id = ObjectKey(element);
            if (_uitkAtg.TryGetValue(id, out var held) && held.Logical == value) return held;
            var prep = string.IsNullOrEmpty(value) ? null : RtlFieldLayout.Prepare(value);
            if (prep == null) { _uitkAtg.Remove(id); return null; }
            var s = new UitkState { Logical = value, Layout = prep.Lay(null) };
            _uitkAtg[id] = s;
            return s;
        }

        /// <summary>
        /// One arrow press on an ATG field, from the positions the ATG draws: the caret goes to the
        /// nearest place on screen in the arrow's direction. 🔴 Measured, not laid out by us: where
        /// two runs of opposite direction meet, one typed position has two places on screen, and the
        /// ATG (ICU) does not always take the one our layout takes — stepped by our layout, the drawn
        /// caret went back now and then (bench, 6000.6.3). Ctrl: on to the start of a word.
        /// </summary>
        private static int UitkAtgStep(object handle, string logical, int caret, bool toRight, bool word)
        {
            int n = logical.Length;
            var x = new float[n + 1];
            for (int i = 0; i <= n; i++) x[i] = Uitk.PositionOf(handle, i).x;
            int current = caret;
            for (int guard = 0; guard <= n; guard++)
            {
                int next = -1;
                for (int i = 0; i <= n; i++)
                {
                    bool ahead = toRight ? x[i] > x[current] + 0.01f : x[i] < x[current] - 0.01f;
                    if (ahead && (next < 0 || (toRight ? x[i] < x[next] : x[i] > x[next]))) next = i;
                }
                if (next < 0) return current;   // at the edge of the screen, the caret stays
                current = next;
                if (!word) return current;
                bool atWordStart = current < n && !UnicodeInfo.IsWhiteSpace(logical[current])
                                   && (current == 0 || UnicodeInfo.IsWhiteSpace(logical[current - 1]));
                if (atWordStart || current == 0 || current == n) return current;
            }
            return current;
        }

        /// <summary>One arrow press on a presented (or ATG) field's selecting utilities, by screen position. True when handled.</summary>
        private static bool UitkMove(object utilities, bool toRight, bool shift, bool word)
        {
            try
            {
                var handle = Uitk.HandleOf(utilities);
                var presented = UitkStateOfHandle(handle);
                if (presented == null)
                {
                    var atg = UitkAtgStateOf(handle);
                    if (atg == null) return false;
                    int anchor0 = Uitk.SelectIndex(utilities), focus0 = Uitk.CursorIndex(utilities);
                    int target;
                    if (!shift && anchor0 != focus0)
                    {
                        float xa = Uitk.PositionOf(handle, anchor0).x, xf = Uitk.PositionOf(handle, focus0).x;
                        target = (toRight ? xa > xf : xa < xf) ? anchor0 : focus0;
                    }
                    else target = UitkAtgStep(handle, atg.Logical, focus0, toRight, word);
                    Uitk.SetCursorIndex(utilities, target);
                    if (!shift) Uitk.SetSelectIndex(utilities, target);
                    return true;
                }
                var s = presented;
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

        // ══ Words ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// TextSelectingUtilities.FindEndOfClassification(position, direction) — the edges of the word
        /// a double-click selects, and of each word a drag by words takes. It reads the characters of
        /// the drawn text at typed positions: on a presented field, its presented form — measured on
        /// the typed text instead (<see cref="RtlFieldLayout.WordEdge"/>).
        /// </summary>
        public static bool Uitk_FindEndOfClassification_Prefix(object __instance, int __0, object __1, ref int __result)
        {
            try
            {
                var s = UitkStateOfHandle(Uitk.HandleOf(__instance));
                if (s == null) return true;
                __result = RtlFieldLayout.WordEdge(s.Logical, Math.Max(0, Math.Min(__0, s.Logical.Length)), __1 != null && __1.ToString() == "Forward");
                return false;
            }
            catch (Exception ex)
            {
                Note("UI Toolkit word selection failed, the field's own used: " + (ex.InnerException ?? ex).Message);
                return true;
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
            internal static MethodInfo SetValueWithoutNotify, PositionByLine, PositionByCharacter, IndexFromPosition, DrawHighlighting, WordEdges;
            internal static readonly List<KeyValuePair<string, MethodInfo>> Moves = new List<KeyValuePair<string, MethodInfo>>();
            private static MemberInfo _isInputField, _isPassword, _renderedText, _handleElement, _utilitiesHandle;
            private static MemberInfo _selectingManipulator, _manipulatorUtilities, _uitkTextHandle, _selectionColor;
            private static PropertyInfo _cursorIndex, _selectIndex, _contentRect;
            private static MethodInfo _isAdvanced, _lineNumber, _lineHeight, _meshGenerator, _rectangleExtension, _classListContains;
            private static string _innerFieldClass;
            private static Type _uitkHandleType;

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

                    // Not this shape (no rendered text apart from the value): another engine's fields —
                    // 2021's draw the typed text themselves. 🔴 Asked of the member this WRITES
                    // (m_RenderedText), not of a method beside it: SetRenderedText is private in Unity 6,
                    // explicit in 2022.3 and absent from 2022.2 — whose fields are the same — and
                    // asked for, it left 2022.2 drawn as typed (bench, 2022.2.11).
                    if (Members.FieldOrProperty(element, "m_RenderedText", Inst) == null)
                    {
                        TranslatorCore.LogDebug("[RtlInputFields] UI Toolkit fields not this shape: no TextElement.m_RenderedText");
                        return _ok = false;
                    }
                    // The explicit INotifyValueChanged<string> implementation: its name is the interface's
                    // on Mono and mangled by the IL2CPP interop — found by what it ends with.
                    SetValueWithoutNotify = EndingWith(element, "SetValueWithoutNotify", typeof(string));

                    // A field's own text element: Unity 6 says it (isInputField); 2022.3 does not, and
                    // marks it with the USS class every field gives its inner text element (Unity's
                    // public TextInputBase.innerTextElementUssClassName, the same in Unity 6).
                    _isInputField = Members.FieldOrProperty(element, "isInputField", Inst);
                    if (_isInputField == null)
                    {
                        var ussClass = Members.FieldOrProperty(element, "ussClassName", BindingFlags.Static | BindingFlags.Public);
                        if (ussClass != null && Members.Get(ussClass, null) is string elementClass) _innerFieldClass = elementClass + "--inner-input-field-component";
                        _classListContains = AssemblyTypes.Find("UnityEngine.UIElements.VisualElement")
                            ?.GetMethod("ClassListContains", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                    }
                    // Members of their own in Unity 6, explicit ITextEdition / ITextSelection ones in 2022.3.
                    _isPassword = Members.FieldOrProperty(element, "isPassword", Inst) ?? PropertyEndingWith(element, "isPassword");
                    _renderedText = Members.FieldOrProperty(element, "m_RenderedText", Inst);
                    _uitkTextHandle = Members.FieldOrProperty(element, "uitkTextHandle", Inst);
                    _selectingManipulator = Members.FieldOrProperty(element, "selectingManipulator", Inst);
                    _selectionColor = Members.FieldOrProperty(element, "selectionColor", Inst) ?? PropertyEndingWith(element, "selectionColor");
                    _contentRect = Members.Property(element, "contentRect", BindingFlags.Instance | BindingFlags.Public);
                    _manipulatorUtilities = Members.FieldOrProperty(Members.TypeOf(_selectingManipulator), "m_SelectingUtilities", Inst);
                    _uitkHandleType = uitkHandle;
                    _handleElement = Members.FieldOrProperty(uitkHandle, "m_TextElement", Inst);
                    // Public in Unity 6, private m_TextHandle in 2022.3.
                    _utilitiesHandle = Members.FieldOrProperty(utilities, "textHandle", Inst)
                                       ?? Members.FieldOrProperty(utilities, "m_TextHandle", Inst);
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
                    // A word's edges (double-click, drag by words): private, (int position, Direction).
                    foreach (var m in utilities.GetMethods(Inst))
                        if (m.Name == "FindEndOfClassification" && m.ReturnType == typeof(int) && m.GetParameters().Length == 2
                            && m.GetParameters()[0].ParameterType == typeof(int) && m.GetParameters()[1].ParameterType.IsEnum)
                            WordEdges = m;
                    var mgc = AssemblyTypes.Find("UnityEngine.UIElements.MeshGenerationContext");
                    // A rectangle: through the context's mesh generator in Unity 6, through the
                    // MeshGenerationContextUtils.Rectangle extension in 2022.3.
                    _meshGenerator = Members.Property(mgc, "meshGenerator", Inst)?.GetMethod;
                    if (_meshGenerator == null)
                        foreach (var m in AssemblyTypes.Find("UnityEngine.UIElements.MeshGenerationContextUtils")?.GetMethods(BindingFlags.Static | BindingFlags.Public) ?? new MethodInfo[0])
                            if (m.Name == "Rectangle" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.Name == "RectangleParams")
                            { _rectangleExtension = m; _rectParams = m.GetParameters()[1].ParameterType; }
                    DrawHighlighting = element.GetMethod("DrawHighlighting", Inst, null, mgc == null ? Type.EmptyTypes : new[] { mgc }, null);

                    // The Advanced Text Generator's question: absent where there is none (2022.3).
                    bool knowsFields = _isInputField != null || (_innerFieldClass != null && _classListContains != null);
                    _ok = SetValueWithoutNotify != null && knowsFields && _renderedText != null && _handleElement != null && _utilitiesHandle != null
                          && _cursorIndex != null && _selectIndex != null;
                    if (!_ok) Note($"UI Toolkit fields: a member this needs is missing — left to the field's own drawing (set={SetValueWithoutNotify != null} field={knowsFields} rendered={_renderedText != null} handle={_handleElement != null} utilities={_utilitiesHandle != null} indices={_cursorIndex != null && _selectIndex != null})");
                    if (DrawHighlighting != null && (_selectingManipulator == null || _manipulatorUtilities == null || _uitkTextHandle == null
                                                     || _selectionColor == null || _contentRect == null || (_meshGenerator == null && _rectangleExtension == null)
                                                     || _lineNumber == null || _lineHeight == null || PositionByLine == null))
                    { DrawHighlighting = null; Note("UI Toolkit fields: the selection's drawing members are missing — the selection is the field's"); }
                }
                catch (Exception ex) { _ok = false; Note("UI Toolkit fields unavailable: " + ex.Message); }
                return _ok;
            }

            internal enum FieldKind { None, Standard, Advanced }

            /// <summary>
            /// A field's text element (not a password), and the generator drawing it: the standard
            /// one gets the presented form, the Advanced Text Generator only its paragraph direction.
            /// </summary>
            internal static FieldKind KindOf(object element)
            {
                bool input = _isInputField != null
                    ? Members.Get(_isInputField, element) is bool isInput && isInput
                    : _classListContains.Invoke(element, new object[] { _innerFieldClass }) is bool hasClass && hasClass;
                if (!input) return FieldKind.None;
                if (_isPassword != null && Members.Get(_isPassword, element) is bool password && password) return FieldKind.None;
                return _isAdvanced != null && (bool)_isAdvanced.Invoke(null, new[] { element }) ? FieldKind.Advanced : FieldKind.Standard;
            }

            /// <summary>
            /// The method of that name declared by this type — its own, or an explicit interface
            /// implementation, whose name carries the interface's (and the IL2CPP interop's mangling)
            /// in front: found by what the name ends with.
            /// </summary>
            private static MethodInfo EndingWith(Type type, string name, Type parameter)
            {
                foreach (var m in type.GetMethods(Inst))
                    if (m.DeclaringType == type && m.Name.EndsWith(name, StringComparison.Ordinal)
                        && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == parameter)
                        return m;
                return null;
            }

            /// <summary>An explicit interface property of this type (ITextEdition.isPassword…), by what its name ends with.</summary>
            private static PropertyInfo PropertyEndingWith(Type type, string name)
            {
                foreach (var p in type.GetProperties(Inst))
                    if (p.DeclaringType == type && p.GetIndexParameters().Length == 0 && p.Name.EndsWith("." + name, StringComparison.Ordinal))
                        return p;
                // IL2CPP interop: the interface's name joined with underscores.
                foreach (var p in type.GetProperties(Inst))
                    if (p.DeclaringType == type && p.GetIndexParameters().Length == 0 && p.Name.EndsWith("_" + name, StringComparison.Ordinal))
                        return p;
                return null;
            }

            internal static string RenderedText(object element) => Members.Get(_renderedText, element) as string;
            internal static void SetRenderedTextField(object element, string value) => Members.Set(_renderedText, element, value);
            /// <summary>
            /// The element a text handle draws for. 🔴 Cast first: on IL2CPP a handle reaches a hook (or
            /// comes out of TextSelectingUtilities.textHandle) wrapped as its base TextHandle until
            /// something asked for it as a UITKTextHandle — the interop keeps one wrapper per object —
            /// and m_TextElement, declared on UITKTextHandle, then refused it ("Object does not match
            /// target type"): the field's first text was left unmapped (2026-10-03 bench, 7/8).
            /// </summary>
            internal static object ElementOf(object handle) =>
                handle == null ? null : Members.Get(_handleElement, TypeHelper.Il2CppCast(handle, _uitkHandleType));
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

            /// <summary>One rectangle, as the element draws its own highlight: the context's mesh generator (Unity 6), or its Rectangle extension (2022.3).</summary>
            internal static void DrawRectangle(object mgc, Rect rect, Color color)
            {
                if (_meshGenerator == null)
                {
                    _paramsRect = _paramsRect ?? Members.FieldOrProperty(_rectParams, "rect", Inst);
                    _paramsColor = _paramsColor ?? Members.FieldOrProperty(_rectParams, "color", Inst);
                    _paramsTint = _paramsTint ?? Members.FieldOrProperty(_rectParams, "playmodeTintColor", Inst);
                    _rectangleExtension.Invoke(null, new[] { mgc, RectangleParams(rect, color) });
                    return;
                }
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
                _drawRectangle.Invoke(generator, new[] { RectangleParams(rect, color) });
            }

            private static object RectangleParams(Rect rect, Color color)
            {
                // Boxed once, written in place: a struct copied at each write would lose the earlier ones.
                object parameters = Activator.CreateInstance(_rectParams);
                Members.Set(_paramsRect, parameters, rect);
                Members.Set(_paramsColor, parameters, color);
                if (_paramsTint != null) Members.Set(_paramsTint, parameters, Color.white);
                return parameters;
            }
        }
    }
}
