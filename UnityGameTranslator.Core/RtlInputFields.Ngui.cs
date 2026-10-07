using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Right-to-left editing in NGUI's input field (UIInput) — the same experience as uGUI's and
    /// TMP's (analyse/rtl-saisie-ngui-uitk.md). NGUI edits the LOGICAL string (mValue,
    /// mSelectionStart/End) and its label shows a slice of it from mDrawStart; the label is given
    /// the presented form (<see cref="RtlFieldLayout.Display"/>) and every place where NGUI reads a
    /// position in the label is told the typed one instead:
    ///
    /// - the caret and the selection: <c>UILabel.PrintOverlay(start, end, …)</c> takes indices in
    ///   the label's text — the caret goes to its display gap, a selection to every piece it shows
    ///   (a mixed text selects in several pieces), each drawn by NGUI's own call;
    /// - the click and the drag: <c>UILabel.GetCharacterIndexAtPosition(Vector2, bool)</c> answers
    ///   a gap of the label, turned into the caret standing there;
    /// - the arrows: <c>UIInput.ProcessEvent</c> moves ←/→ by one typed character; they follow the
    ///   screen instead (user's decision, 2026-09-25), Ctrl by word.
    ///
    /// - Up/Down: to the line drawn above or below, nearest on screen to the kept column.
    ///
    /// Home/End stay NGUI's (typed order). NGUI draws everything itself: nothing is
    /// hidden or drawn here. Every member is found by name — the Core names no NGUI type — and a
    /// missing one is said and leaves that gesture to NGUI.
    /// </summary>
    internal static partial class RtlInputFields
    {
        private sealed class NguiState
        {
            public WeakReference FieldRef;
            public object Label;
            public RtlFieldLayout Layout;
            public string Logical;
            public string Shown;

            // The column Up/Down aim at (as RtlInputFields.FieldState keeps it): a new state is a new text.
            public int GoalLanded = -1;
            public float GoalX;
        }

        // By the label's instance id: the overlay and the click are asked of the label.
        private static readonly Dictionary<int, NguiState> _ngui = new Dictionary<int, NguiState>();
        [ThreadStatic] private static bool _nguiOverlayInside;

        /// <summary>
        /// The label of an NGUI field showing what was typed: given the presented form, and the
        /// field remembered for the caret, the clicks and the arrows. A text needing nothing
        /// (Latin, say) leaves the field as NGUI has it.
        /// </summary>
        internal static void PresentNguiLabel(object field, object label, ref string value, string settingsFontName, FontOverrideRule overrideRule)
        {
            if (!TranslatorCore.IsMainThread || field == null || label == null || !Ngui.Resolve()) return;
            // Aligned as the game's texts of its font are (mirror RTL), from what was typed.
            RtlPresenter.AlignTypedLabel(label, value, settingsFontName, overrideRule, false);
            int id = TypeHelper.GetInstanceID(label);

            var derived = FontManager.DerivedForSettings(settingsFontName);
            NoteIfUnshaped(derived != null, value, settingsFontName, false, null);
            var prep = string.IsNullOrEmpty(value) ? null : RtlFieldLayout.Prepare(value, UnitsOf(derived));
            if (prep == null) { _ngui.Remove(id); return; }

            var s = new NguiState { FieldRef = new WeakReference(field), Label = label, Logical = value };
            // The label written again with the same text (every caret move goes through UpdateLabel):
            // the column Up/Down aim at stays.
            if (_ngui.TryGetValue(id, out var before) && before.Logical == value) { s.GoalLanded = before.GoalLanded; s.GoalX = before.GoalX; }
            var wraps = NguiWraps(label, prep.MeasureText);
            prep = prep.SplitTokensAt(prep.TokenSplitsAt(wraps));   // a tag wider than the line, cut by the label: cut there too
            s.Layout = prep.Lay(wraps);
            s.Shown = s.Layout.Display;
            _ngui[id] = s;

            TranslatorCore.RegisterPresentedText(s.Shown, value);
            Describe("NGUI", label, value, s.Shown);
            value = s.Shown;
        }

        /// <summary>
        /// Where the label itself would wrap the text (UILabel.Wrap, synchronous, the label's own
        /// width and font): the soft breaks to lay out against, as positions in it. Null — one
        /// line per hard break — when it has none, or when what it hands back is not the same text
        /// with breaks added (an ellipsis, a cut), which is said.
        /// </summary>
        private static List<int> NguiWraps(object label, string text)
        {
            if (Ngui.Wrap == null || string.IsNullOrEmpty(text)) return null;
            // A label sized by its text never wraps (UILabel.ProcessText gives it all the width it
            // wants); Wrap would measure it against the width the previous text left it.
            if (Ngui.OverflowOf(label) == "ResizeFreely") return null;
            string final;
            try
            {
                var args = new object[] { text, null };
                Ngui.Wrap.Invoke(label, args);
                final = args[1] as string;
            }
            catch (Exception ex) { Note("NGUI field: UILabel.Wrap failed, laid out on its hard breaks", ex); return null; }
            if (final == null || final == text) return null;

            var wraps = new List<int>();
            int i = 0;
            foreach (char c in final)
            {
                if (i < text.Length && text[i] == c) { i++; continue; }
                // A break the label added — before a character, or in place of the space it ate.
                if (c == '\n')
                {
                    if (i < text.Length && text[i] == ' ') i++;
                    if (i > 0 && i < text.Length) wraps.Add(i);
                    continue;
                }
                Note("NGUI field: the label's wrap changes the text (ellipsis or cut), laid out on its hard breaks");
                return null;
            }
            return i == text.Length ? wraps : null;
        }

        /// <summary>The presented field state of a label, while it still shows what was laid out.</summary>
        private static NguiState NguiStateOf(object label)
        {
            if (_ngui.Count == 0 || label == null) return null;
            if (!_ngui.TryGetValue(TypeHelper.GetInstanceID(label), out var s)) return null;
            var field = s.FieldRef.Target;
            if (field == null || field is UnityEngine.Object uo && uo == null) { _ngui.Remove(TypeHelper.GetInstanceID(label)); return null; }
            // NGUI reads its positions in processedText: they are ours only while it is the text laid out.
            string processed = null;
            try { processed = Ngui.ProcessedText.GetValue(label, null) as string; }
            catch (Exception ex) { Faults.Say("RtlInputFields.NguiStateOf", ex); }
            return processed == s.Shown ? s : null;
        }

        // ══ Where the label is written on IL2CPP ═══════════════════════════════════════════

        /// <summary>
        /// UIInput.UpdateLabel — after every keystroke, a caret move, a selection. On Mono its
        /// <c>label.text = …</c> reached the text setter's hook, which presented the label already;
        /// on IL2CPP that small setter is compiled INTO UpdateLabel (seen in the disassembly,
        /// 2026-10-01) and no hook of it runs: the label kept the typed order and NGUI drew its caret
        /// there. Presented here, then the caret and the selection drawn again on the presented text.
        /// </summary>
        public static void Ngui_UpdateLabel_Postfix(object __instance)
        {
            try
            {
                if (!TranslatorCore.TranslationsActive || !Ngui.ResolveField()) return;
                var label = TypeHelper.NguiInputLabel(__instance);
                if (label == null || !TypeHelper.IsTypedNguiLabel(__instance, label)) return;
                string written = TypeHelper.GetText(label);
                if (string.IsNullOrEmpty(written)) return;
                if (_ngui.TryGetValue(TypeHelper.GetInstanceID(label), out var held) && held.Shown == written) return;

                string value = written;
                string font = Ngui.SettingsFontOf(label);
                FontOverrideRule rule = TranslatorCore.FontOverrides.Count > 0 && label is Component labelComponent
                    ? TranslatorCore.FindFontOverride(TypeHelper.GetInstanceID(label), TranslatorCore.GetGameObjectPath(labelComponent.gameObject), font, null)
                    : null;
                PresentNguiLabel(__instance, label, ref value, font, rule);
                if (value == written) return;
                TranslatorPatches.BypassTextPrefix = true;
                try { TypeHelper.SetText(label, value); }
                finally { TranslatorPatches.BypassTextPrefix = false; }
                Ngui.RedrawOverlay(__instance, label);
            }
            catch (Exception ex) { Note("NGUI field presentation after UpdateLabel failed", ex); }
        }

        // ══ Caret and selection ══════════════════════════════════════════════════════════════

        /// <summary>
        /// UILabel.PrintOverlay(start, end, caret, highlight, caretColor, highlightColor): the typed
        /// positions turned into the label's. One piece: the original call with display gaps. Several
        /// pieces (a selection across a direction change): one original call per piece into a
        /// scratch geometry, gathered into NGUI's.
        /// </summary>
        public static bool Ngui_PrintOverlay_Prefix(object __instance, ref int __0, ref int __1, object __2, object __3, Color __4, Color __5)
        {
            if (_nguiOverlayInside) return true;
            try
            {
                var s = NguiStateOf(__instance);
                if (s == null) return true;
                int caret = s.Layout.BoundaryOf(__1);
                if (__0 == __1 || __3 == null) { __0 = __1 = caret; return true; }

                var pieces = DisplayPieces(s.Layout, s.Logical, Math.Min(__0, __1), Math.Max(__0, __1));
                if (pieces.Count == 1 && (pieces[0].Key == caret || pieces[0].Value == caret))
                {
                    __0 = pieces[0].Key == caret ? pieces[0].Value : pieces[0].Key;
                    __1 = caret;
                    return true;
                }

                Ngui.Clear(__3);
                _nguiOverlayInside = true;
                try
                {
                    var scratchCaret = Ngui.NewGeometry();
                    var scratchHighlight = Ngui.NewGeometry();
                    foreach (var piece in pieces)
                    {
                        Ngui.PrintOverlay.Invoke(__instance, new object[] { piece.Key, piece.Value, scratchCaret, scratchHighlight, __4, __5 });
                        Ngui.Append(scratchHighlight, __3);
                    }
                    // The caret last, alone: the call clears the caret geometry only (no highlight given).
                    Ngui.PrintOverlay.Invoke(__instance, new object[] { caret, caret, __2, null, __4, __5 });
                }
                finally { _nguiOverlayInside = false; }
                return false;
            }
            catch (Exception ex)
            {
                Note("NGUI caret mapping failed, NGUI's own used", ex);
                return true;
            }
        }

        /// <summary>The display gaps a typed range shows, merged where they touch: [start, end) pairs, left to right.</summary>
        private static List<KeyValuePair<int, int>> DisplayPieces(RtlFieldLayout layout, string logical, int from, int to)
        {
            var spans = new List<KeyValuePair<int, int>>();
            for (int i = Math.Max(0, from); i < Math.Min(to, logical.Length); i++)
            {
                if (logical[i] == '\n') continue;
                int d = layout.DisplayOf(i);
                spans.Add(new KeyValuePair<int, int>(d, d + layout.DisplayLengthOf(i)));
            }
            spans.Sort((a, b) => a.Key.CompareTo(b.Key));
            var merged = new List<KeyValuePair<int, int>>();
            foreach (var span in spans)
            {
                if (merged.Count > 0 && span.Key <= merged[merged.Count - 1].Value)
                {
                    var last = merged[merged.Count - 1];
                    merged[merged.Count - 1] = new KeyValuePair<int, int>(last.Key, Math.Max(last.Value, span.Value));
                }
                else merged.Add(span);
            }
            return merged;
        }

        // ══ Clicks ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// UILabel.GetCharacterIndexAtPosition(Vector2, bool) — the one every click and drag of the
        /// field ends in (the Vector3 overload calls it): the label's gap under the pointer, made
        /// the caret standing there.
        /// </summary>
        public static void Ngui_GetCharacterIndexAtPosition_Postfix(object __instance, ref int __result)
        {
            try
            {
                var s = NguiStateOf(__instance);
                if (s == null) return;
                __result = s.Layout.CaretAtBoundary(__result);
                s.GoalLanded = -1;   // a click starts a new column
            }
            catch (Exception ex) { Note("NGUI click mapping failed", ex); }
        }

        // ══ Arrow keys ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// UIInput.ProcessEvent(Event): ← and → on a presented field, by screen position; ↑ and ↓ to
        /// the line drawn above or below, at the caret nearest on screen to the kept column — the
        /// standard of every editor (user's decision, 2026-10-04). NGUI's own Up/Down asked the label
        /// (GetCharacterIndex) with the TYPED index, in the presented text. Past the first or last
        /// line: the start or end of the text.
        /// </summary>
        public static bool Ngui_ProcessEvent_Prefix(object __instance, object __0, ref bool __result)
        {
            try
            {
                if (__0 == null || !Ngui.ResolveField()) return true;
                var key = (KeyCode)Ngui.EventKeyCode.GetValue(__0, null);
                bool vertical = key == KeyCode.UpArrow || key == KeyCode.DownArrow;
                bool lineEdge = key == KeyCode.Home || key == KeyCode.End;
                if (key != KeyCode.LeftArrow && key != KeyCode.RightArrow && !vertical && !lineEdge) return true;
                var label = TypeHelper.NguiInputLabel(__instance);
                var s = NguiStateOf(label);
                if (s == null) return true;

                var modifiers = (EventModifiers)Ngui.EventModifiers.GetValue(__0, null);
                bool shift = (modifiers & EventModifiers.Shift) != 0;
                if (lineEdge)
                {
                    // Home / End on a multi-line label: NGUI asked the drawn label with the typed index
                    // (GetCharacterIndex); the line's start or end in reading order instead. On one line
                    // NGUI goes to the start or end of the text, which is right as it is.
                    if (!Ngui.IsMultiLine(label)) return true;
                    int start = Ngui.DrawStart(__instance);
                    int from = Ngui.SelectionEnd(__instance) - start;
                    int line = s.Layout.LineOfCaret(from);
                    int target = key == KeyCode.Home ? s.Layout.LineLogicalStart(line) : s.Layout.LineEndCaret(line);
                    s.GoalLanded = -1;
                    Ngui.SetSelectionEnd(__instance, target + start);
                    if (!shift) Ngui.SetSelectionStart(__instance, target + start);
                    Ngui.UpdateLabel.Invoke(__instance, null);
                    Ngui.EventUse.Invoke(__0, null);
                    __result = true;
                    return false;
                }
                if (vertical)
                {
                    if (Ngui.PrintOverlay == null) return true;   // no way to measure where a caret is drawn
                    // The game's own use of the key (onUpArrow / onDownArrow — a chat's history, say):
                    // NGUI calls it instead of moving the caret, and so does this.
                    if (Ngui.HasArrowCallback(__instance, key == KeyCode.UpArrow)) return true;
                    int start = Ngui.DrawStart(__instance);
                    int from = Ngui.SelectionEnd(__instance) - start;
                    Func<int, float> xOf = c => Ngui.CaretX(label, c);
                    float goal = s.GoalLanded == from ? s.GoalX : xOf(from);
                    if (float.IsNaN(goal)) return true;
                    bool down = key == KeyCode.DownArrow;
                    int target = s.Layout.VerticalStep(from, down, goal, xOf);
                    if (target < 0) target = down ? s.Logical.Length : 0;
                    s.GoalLanded = target;
                    s.GoalX = goal;
                    Ngui.SetSelectionEnd(__instance, target + start);
                    if (!shift) Ngui.SetSelectionStart(__instance, target + start);
                    Ngui.UpdateLabel.Invoke(__instance, null);
                    Ngui.EventUse.Invoke(__0, null);
                    __result = true;
                    return false;
                }
                s.GoalLanded = -1;   // a move along the line starts a new column
                bool mac = Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;
                // NGUI's own reading: Command on a Mac, Control elsewhere, never with Alt.
                bool ctrl = (modifiers & EventModifiers.Alt) == 0
                            && (modifiers & (mac ? EventModifiers.Command : EventModifiers.Control)) != 0;
                bool toRight = key == KeyCode.RightArrow;

                int drawStart = Ngui.DrawStart(__instance);
                int anchor = Ngui.SelectionStart(__instance) - drawStart;
                int focus = Ngui.SelectionEnd(__instance) - drawStart;
                var layout = s.Layout;
                int next;
                if (!shift && anchor != focus)
                {
                    // A selection and no shift: collapse to its end on that side of the screen.
                    bool anchorFurther = toRight ? layout.BoundaryOf(anchor) > layout.BoundaryOf(focus)
                                                 : layout.BoundaryOf(anchor) < layout.BoundaryOf(focus);
                    next = anchorFurther ? anchor : focus;
                }
                else
                {
                    next = ctrl ? WordStep(layout, s.Logical, focus, toRight) : layout.VisualStep(focus, toRight);
                    string typed = TypeHelper.GetInputFieldText(__instance) ?? "";
                    if (next == focus && (drawStart > 0 || s.Logical.Length < typed.Length))
                    {
                        // The visible slice's edge (NGUI scrolls a long line): onward in reading
                        // order, one character — the field brings the caret into view itself. Only
                        // a slice: with the whole text in view, the edge of the screen is the end.
                        bool forward = toRight != layout.LineIsRtl(layout.LineOfCaret(focus));
                        int absolute = focus + drawStart + (forward ? 1 : -1);
                        if (absolute < 0 || absolute > typed.Length) { Ngui.EventUse.Invoke(__0, null); __result = true; return false; }
                        next = absolute - drawStart;
                    }
                }

                Ngui.SetSelectionEnd(__instance, next + drawStart);
                if (!shift) Ngui.SetSelectionStart(__instance, next + drawStart);
                Ngui.UpdateLabel.Invoke(__instance, null);
                Ngui.EventUse.Invoke(__0, null);
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Note("NGUI arrow move failed, NGUI's own used", ex);
                return true;
            }
        }

        // ══ NGUI by reflection ══════════════════════════════════════════════════════════════

        internal static class Ngui
        {
            // 🔴 Every NGUI field is a FIELD on Mono and a PROPERTY on IL2CPP (the interop wrapper
            // exposes each field as one), and its lists are not .NET lists there: members through
            // Members.FieldOrProperty, lists through EngineCollections — asked for a field alone,
            // every one read as missing on IL2CPP and the caret and the arrows stayed NGUI's.
            private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private static bool _resolved, _ok, _fieldResolved, _fieldOk;
            internal static Type LabelType;
            internal static PropertyInfo ProcessedText;
            internal static MethodInfo Wrap, PrintOverlay, GetCharacterIndexAtPosition;
            private static Type _geometryType;
            private static MemberInfo _geoVerts, _geoUvs, _geoCols;
            private static MethodInfo _geoClear;

            internal static MethodInfo ProcessEvent, UpdateLabel;
            private static MemberInfo _selStart, _selEnd, _drawStart;
            internal static PropertyInfo EventKeyCode, EventModifiers;
            internal static MethodInfo EventUse;

            /// <summary>The label side: what presenting and the two label hooks need.</summary>
            internal static bool Resolve()
            {
                if (_resolved) return _ok;
                _resolved = true;
                try
                {
                    LabelType = TypeHelper.NGUI_LabelType;
                    if (TypeHelper.NGUI_InputType == null || LabelType == null) return _ok = false;

                    ProcessedText = Members.Property(LabelType, "processedText", BindingFlags.Instance | BindingFlags.Public);
                    Wrap = LabelType.GetMethod("Wrap", BindingFlags.Instance | BindingFlags.Public, null,
                                               new[] { typeof(string), typeof(string).MakeByRefType() }, null);
                    GetCharacterIndexAtPosition = LabelType.GetMethod("GetCharacterIndexAtPosition", BindingFlags.Instance | BindingFlags.Public, null,
                                                                      new[] { typeof(Vector2), typeof(bool) }, null);
                    foreach (var m in LabelType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (m.Name != "PrintOverlay") continue;
                        var p = m.GetParameters();
                        if (p.Length == 6 && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(int)
                            && p[4].ParameterType == typeof(Color) && p[5].ParameterType == typeof(Color))
                        { PrintOverlay = m; _geometryType = p[2].ParameterType; }
                    }
                    if (_geometryType != null)
                    {
                        _geoVerts = Members.FieldOrProperty(_geometryType, "verts", Inst);
                        _geoUvs = Members.FieldOrProperty(_geometryType, "uvs", Inst);
                        _geoCols = Members.FieldOrProperty(_geometryType, "cols", Inst);
                        _geoClear = _geometryType.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    }

                    _ok = ProcessedText != null;
                    if (!_ok) Note("NGUI fields: UILabel.processedText missing — left to NGUI's own drawing");
                    if (Wrap == null) Note("NGUI fields: UILabel.Wrap missing — a multi-line field is laid out on its hard breaks");
                    if (PrintOverlay == null || _geoVerts == null || _geoUvs == null || _geoCols == null || _geoClear == null)
                    { PrintOverlay = null; Note("NGUI fields: UILabel.PrintOverlay or UIGeometry not as expected — the caret is NGUI's"); }
                    if (GetCharacterIndexAtPosition == null) Note("NGUI fields: UILabel.GetCharacterIndexAtPosition missing — clicks are NGUI's");
                }
                catch (Exception ex) { _ok = false; Note("NGUI fields unavailable", ex); }
                return _ok;
            }

            /// <summary>The field side: what the arrows and the label's redraw need.</summary>
            internal static bool ResolveField()
            {
                if (_fieldResolved) return _fieldOk;
                _fieldResolved = true;
                if (!Resolve()) return _fieldOk = false;
                try
                {
                    var input = TypeHelper.NGUI_InputType;
                    UpdateLabel = input.GetMethod("UpdateLabel", Inst, null, Type.EmptyTypes, null);
                    _selStart = Members.FieldOrProperty(input, "mSelectionStart", Inst);
                    _selEnd = Members.FieldOrProperty(input, "mSelectionEnd", Inst);
                    // A static in recent NGUI, an instance field before: read whichever it is.
                    _drawStart = Members.FieldOrProperty(input, "mDrawStart", Inst | BindingFlags.Static);
                    var eventType = ProcessEvent?.GetParameters()[0].ParameterType;
                    EventKeyCode = eventType?.GetProperty("keyCode");
                    EventModifiers = eventType?.GetProperty("modifiers");
                    EventUse = eventType?.GetMethod("Use", Type.EmptyTypes);
                    _fieldOk = UpdateLabel != null && _selStart != null && _selEnd != null
                               && EventKeyCode != null && EventModifiers != null && EventUse != null;
                    if (!_fieldOk) Note("NGUI fields: a UIInput member the arrows need is missing — the arrows are NGUI's");
                    if (_drawStart == null) Note("NGUI fields: UIInput.mDrawStart not found — a scrolled field steps from the start of its text");
                }
                catch (Exception ex) { _fieldOk = false; Note("NGUI field arrows unavailable", ex); }
                return _fieldOk;
            }

            /// <summary>UIInput.ProcessEvent(Event), found before patching so the arrows' hook has its event type.</summary>
            internal static MethodInfo FindProcessEvent()
            {
                var input = TypeHelper.NGUI_InputType;
                if (input == null) return null;
                foreach (var m in input.GetMethods(Inst))
                    if (m.Name == "ProcessEvent" && m.ReturnType == typeof(bool) && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.Name == "Event")
                        return ProcessEvent = m;
                return null;
            }

            private static PropertyInfo _multiLine;
            private static bool _multiLineResolved;

            /// <summary>UILabel.multiLine — what NGUI's own Home / End ask. A label without it: one line.</summary>
            internal static bool IsMultiLine(object label)
            {
                if (!_multiLineResolved)
                {
                    _multiLineResolved = true;
                    _multiLine = Members.Property(LabelType, "multiLine", BindingFlags.Instance | BindingFlags.Public);
                    if (_multiLine == null) Note("NGUI fields: UILabel.multiLine not found — Home / End stay NGUI's");
                }
                try
                {
                    object value = _multiLine?.GetValue(label, null);
                    if (TranslatorCore.DebugMode && DiagnosticOnce.First("RtlInputFields.Ngui.multiLine", value?.GetType().Name + "\u0001" + value))
                        TranslatorCore.LogDebug($"[RtlInputFields] NGUI UILabel.multiLine reads {value ?? "null"} ({value?.GetType().FullName ?? "-"})");
                    return value is bool multi && multi;
                }
                catch (Exception ex) { Faults.Say("RtlInputFields.Ngui.IsMultiLine", ex); return false; }
            }

            private static bool _arrowCallbacksResolved;
            private static MemberInfo _onUpArrow, _onDownArrow;

            /// <summary>
            /// The game gave the field its own use of ↑ (or ↓) — UIInput.onUpArrow / onDownArrow, absent
            /// from older NGUI (then never). A member that cannot be read counts as set: the key stays NGUI's.
            /// </summary>
            internal static bool HasArrowCallback(object field, bool up)
            {
                if (!_arrowCallbacksResolved)
                {
                    _arrowCallbacksResolved = true;
                    _onUpArrow = Members.FieldOrProperty(TypeHelper.NGUI_InputType, "onUpArrow", Inst);
                    _onDownArrow = Members.FieldOrProperty(TypeHelper.NGUI_InputType, "onDownArrow", Inst);
                }
                var member = up ? _onUpArrow : _onDownArrow;
                if (member == null) return false;
                try { return Members.Get(member, field) != null; }
                catch (Exception ex) { Faults.Say("RtlInputFields.Ngui.HasArrowCallback", ex); return true; }
            }

            internal static int SelectionStart(object field) => Convert.ToInt32(Members.Get(_selStart, field));
            internal static int SelectionEnd(object field) => Convert.ToInt32(Members.Get(_selEnd, field));
            internal static void SetSelectionStart(object field, int value) => Members.Set(_selStart, field, value);
            internal static void SetSelectionEnd(object field, int value) => Members.Set(_selEnd, field, value);

            internal static int DrawStart(object field) =>
                _drawStart == null ? 0 : Convert.ToInt32(Members.Get(_drawStart, IsStatic(_drawStart) ? null : field));

            private static bool IsStatic(MemberInfo member) =>
                member is FieldInfo f ? f.IsStatic : member is PropertyInfo p && p.GetMethod != null && p.GetMethod.IsStatic;

            private static bool _redrawResolved, _redrawOk;
            private static PropertyInfo _isSelected, _trueTypeFont, _widgetEnabled;
            private static MemberInfo _caretWidget, _highlightWidget, _caretColor, _selectionColor, _widgetGeometry;
            private static MethodInfo _markAsChanged;

            /// <summary>The settings name of the font a label draws with (its dynamic trueTypeFont), null when none.</summary>
            internal static string SettingsFontOf(object label)
            {
                if (_trueTypeFont == null) _trueTypeFont = Members.Property(LabelType, "trueTypeFont", BindingFlags.Instance | BindingFlags.Public);
                try
                {
                    var font = _trueTypeFont?.GetValue(label, null) as UnityEngine.Object;
                    if (font == null || string.IsNullOrEmpty(font.name)) return null;
                    return FontManager.GetSettingsFontName(TypeHelper.GetInstanceID(label), font.name);
                }
                catch (Exception ex) { Faults.Say("RtlInputFields.Ngui.SettingsFontOf", ex); return null; }
            }

            /// <summary>
            /// The caret and the selection drawn again, as UpdateLabel draws them, on the label as it now
            /// stands — the call goes through the PrintOverlay hook, which maps them.
            /// </summary>
            internal static void RedrawOverlay(object field, object label)
            {
                if (!_redrawResolved)
                {
                    _redrawResolved = true;
                    var input = TypeHelper.NGUI_InputType;
                    _isSelected = Members.Property(input, "isSelected", Inst);
                    _caretWidget = Members.FieldOrProperty(input, "mCaret", Inst);
                    _highlightWidget = Members.FieldOrProperty(input, "mHighlight", Inst);
                    _caretColor = Members.FieldOrProperty(input, "caretColor", Inst);
                    _selectionColor = Members.FieldOrProperty(input, "selectionColor", Inst);
                    var widgetType = Members.TypeOf(_caretWidget);
                    _widgetGeometry = Members.FieldOrProperty(widgetType, "geometry", Inst);
                    _markAsChanged = widgetType?.GetMethod("MarkAsChanged", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    _widgetEnabled = Members.Property(widgetType, "enabled", BindingFlags.Instance | BindingFlags.Public);
                    _redrawOk = PrintOverlay != null && _isSelected != null && _caretWidget != null && _highlightWidget != null
                                && _caretColor != null && _selectionColor != null && _widgetGeometry != null && _markAsChanged != null;
                    if (!_redrawOk) Note("NGUI fields: a member the caret redraw needs is missing — the caret may stand in typing order");
                }
                if (!_redrawOk || !(bool)_isSelected.GetValue(field, null)) return;
                var caret = Members.Get(_caretWidget, field);
                if (caret == null) return;
                var highlight = Members.Get(_highlightWidget, field);
                int drawStart = DrawStart(field);
                int start = SelectionStart(field) - drawStart, end = SelectionEnd(field) - drawStart;
                bool selecting = start != end && highlight != null;
                PrintOverlay.Invoke(label, new object[] { start, end, Members.Get(_widgetGeometry, caret),
                                                          selecting ? Members.Get(_widgetGeometry, highlight) : null,
                                                          Members.Get(_caretColor, field), Members.Get(_selectionColor, field) });
                _markAsChanged.Invoke(caret, null);
                if (highlight != null)
                {
                    bool drawn = selecting && EngineCollections.Length(Members.Get(_geoVerts, Members.Get(_widgetGeometry, highlight))) > 0;
                    _widgetEnabled?.SetValue(highlight, drawn, null);
                    _markAsChanged.Invoke(highlight, null);
                }
            }

            private static PropertyInfo _overflow;
            private static bool _overflowResolved;

            /// <summary>The name of a label's overflowMethod (ResizeFreely, ShrinkContent…), null when it has none.</summary>
            internal static string OverflowOf(object label)
            {
                if (!_overflowResolved) { _overflowResolved = true; _overflow = Members.Property(LabelType, "overflowMethod", BindingFlags.Instance | BindingFlags.Public); }
                try { return _overflow?.GetValue(label, null)?.ToString(); }
                catch (Exception ex) { Faults.Say("RtlInputFields.Ngui.OverflowOf", ex); return null; }
            }

            internal static object NewGeometry() => Activator.CreateInstance(_geometryType);

            /// <summary>
            /// Where NGUI draws the caret before typed character <paramref name="caret"/> (relative to the
            /// label's slice): its own PrintOverlay into a scratch geometry — through the caret hook,
            /// which takes it to its display gap — the middle of the quad. NaN when nothing is drawn.
            /// </summary>
            internal static float CaretX(object label, int caret)
            {
                var geometry = NewGeometry();
                PrintOverlay.Invoke(label, new object[] { caret, caret, geometry, null, Color.white, Color.white });
                var verts = Members.Get(_geoVerts, geometry);
                int n = EngineCollections.Length(verts);
                if (n == 0) return float.NaN;
                float x = 0f;
                for (int i = 0; i < n; i++) x += ((Vector3)EngineCollections.Item(verts, i)).x;
                return x / n;
            }
            internal static void Clear(object geometry) => _geoClear.Invoke(geometry, null);

            /// <summary>Every vertex of <paramref name="from"/> added to <paramref name="to"/>, with its uv and colour.</summary>
            internal static void Append(object from, object to)
            {
                foreach (var member in new[] { _geoVerts, _geoUvs, _geoCols })
                {
                    var source = Members.Get(member, from);
                    var target = Members.Get(member, to);
                    for (int i = 0, n = EngineCollections.Length(source); i < n; i++)
                        EngineCollections.Add(target, EngineCollections.Item(source, i));
                }
            }
        }
    }
}
