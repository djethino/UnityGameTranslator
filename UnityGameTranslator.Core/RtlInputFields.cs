using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Right-to-left text, and text of a script that needs shaping, in Unity's input fields — the
    /// game's and this mod's own (UniverseLib builds uGUI InputField), uGUI and TextMesh Pro. See
    /// analyse/rtl-saisie-et-editeur-mod.md and analyse/ecritures-complexes-etat-reel.md (saisie).
    ///
    /// A shaped syllable is drawn by the field's font's own tables — the derived copy for uGUI
    /// (DerivedFonts), our font asset for TMP (ShapingFontAsset) — and stands as one unit for the
    /// caret (RtlFieldLayout). A TMP label may hold more characters than were typed (a split vowel:
    /// কো is drawn ে + ক + া): every label character is told the typed one it stands for.
    ///
    /// Unity's two fields store and edit the LOGICAL string and draw it as it is: Arabic comes out
    /// unjoined and in typing order, on every version. The field's text is never touched here —
    /// the caret, the clipboard and what the game reads back all stay on what was typed. What
    /// changes is what is DRAWN, and everything that reads positions back from the drawing:
    ///
    /// - uGUI: the label is given the presented form (<see cref="RtlFieldLayout.Display"/>) and
    ///   the map answers for it — the click (GetCharacterIndexFromPosition postfix) and the
    ///   arrows. The field's own editing works on the logical text and never reads the label;
    /// - TMP: its field DOES read positions back from the label's characterInfo (Backspace removes
    ///   <c>text.Remove(characterInfo[caret - 1].index, characterInfo[caret - 1].stringLength)</c>, TMP
    ///   1.4 to Unity 6), so its label keeps the typed order (<see cref="RtlFieldLayout.Prepared.LabelFor"/>)
    ///   and, after TMP lays the text out (GenerateTextMesh postfix), every characterInfo is given the
    ///   typed position and length it stands for, then the GLYPHS are moved into visual order. Every
    ///   index TMP reads is a typed one: Backspace after কো takes ো, as every editor does;
    /// - both: the caret and the selection the field draws are made transparent while the label
    ///   is presented and drawn here, from the map, with plain Image quads (no type of ours to
    ///   register on IL2CPP); the arrow keys follow the screen (user's decision, 2026-09-25).
    ///
    /// 🔴 A field showing no strong right-to-left character is left exactly as Unity has it, and
    /// a field that stops showing one gets its colours back the same instant.
    /// ⚠ Main thread only, like everything that touches the shaper.
    /// </summary>
    internal static partial class RtlInputFields
    {
        private enum Engine { UGui, Tmp }

        private sealed class FieldState
        {
            public Engine Kind;
            public WeakReference FieldRef;
            public object Field;             // the field as the runtime hands it
            public InputField UField;        // uGUI only
            public Graphic Label;            // Text, or TMP_Text as the Graphic it is
            public object LabelObj;          // TMP: read by reflection
            public RtlFieldLayout.Prepared Prep;
            public RtlFieldLayout Layout;
            public string Logical;           // the typed text the label shows (uGUI: the visible slice)
            public string Shown;             // what the label was given
            public int DrawStart;            // uGUI: where the visible slice starts; TMP: 0
            public int[] LabelIndex, LabelLength; // TMP: for each label character, the typed text it stands for

            public bool Hidden;
            public Color OriginalCaret;
            public bool OriginalCustomCaret;
            public Color OriginalSelection;

            public GameObject Overlay;
            public RectTransform OverlayRect;
            public readonly List<Image> Quads = new List<Image>();
            public int LastAnchor = -1, LastFocus = -1;
            public float BlinkStart;

            // The column Up/Down aim at, kept while the caret stays where the last vertical press put
            // it in the same text — any other move, a click or new text starts a new one.
            public int GoalLanded = -1;
            public float GoalX;
            public string GoalShown;

            // Where each typed character is drawn, in the label's local space, and the engine's lines.
            public float[] BoxL = new float[0], BoxR = new float[0];
            public int[] BoxLine = new int[0];
            public readonly List<float> LineTop = new List<float>();
            public readonly List<float> LineBottom = new List<float>();
            public int[] EngineLineOfLine = new int[0];
            public bool BoxesFromReorder;   // TMP: filled by the glyph move, not read back

            // uGUI scratch
            public readonly List<float> X = new List<float>(), W = new List<float>();
            public readonly List<int> GLineStart = new List<int>();
            public readonly List<float> GLineTop = new List<float>(), GLineHeight = new List<float>();
        }

        private static readonly Dictionary<int, FieldState> _states = new Dictionary<int, FieldState>();
        private static readonly Dictionary<int, FieldState> _byTmpLabel = new Dictionary<int, FieldState>();
        private static readonly List<int> _scratch = new List<int>();

        // ══ Presenting a label ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Called from the text setter prefix when the component being written is an input
        /// field's label. Replaces <paramref name="value"/> by what the label must draw when it
        /// carries right-to-left text, and releases the field otherwise.
        /// </summary>
        /// <param name="settingsFontName">the game font the label wears (its replacement shapes); null for the mod's own window</param>
        /// <param name="ownUi">the mod's own window: its interface font shapes</param>
        /// <summary>
        /// A field showing text that needs shaping with no font that shapes it: said as a text does
        /// (RtlPresenter's ReorderOnly) — the window notice for the mod's own fields (the in-game
        /// editor showed "संग्रह" unjoined and nothing said it, 2026-10-01), the font notice for a
        /// game field holding our translation. What the player types is not ours to report.
        /// </summary>
        private static void NoteIfUnshaped(bool canShape, string value, string settingsFontName, bool ownUi, GameTextSide? side)
        {
            if (canShape || string.IsNullOrEmpty(value) || !TextShaping.OpenTypeText.NeedsShaping(value)) return;
            if (ownUi) FontManager.NoteWindowUnshaped(side, value);
            else if (TranslatorCore.IsAlreadyTargetText(value)) FontManager.NoteUnshaped(settingsFontName, value);
        }

        /// <param name="overrideRule">the font rule matching the label, null when none</param>
        internal static void PresentLabel(object fieldObj, object labelObj, ref string value, string settingsFontName, FontOverrideRule overrideRule, bool ownUi)
        {
            if (!TranslatorCore.IsMainThread || fieldObj == null || labelObj == null) return;
            // Aligned as the game's texts of its font are (mirror RTL), from what was typed.
            RtlPresenter.AlignTypedLabel(labelObj, value, settingsFontName, overrideRule, ownUi);

            if (TypeHelper.TMP_InputFieldType != null && TypeHelper.TMP_InputFieldType.IsInstanceOfType(fieldObj))
            {
                var asset = ownUi ? null : ShapingFontAsset.ForSettings(settingsFontName);
                NoteIfUnshaped(asset != null, value, settingsFontName, ownUi, ModWindowText.SideOf(fieldObj) ?? ModWindowText.SideOf(labelObj));
                PresentTmpLabel(fieldObj, labelObj, ref value, asset);
                return;
            }

            var field = TypeHelper.Il2CppCast(fieldObj, typeof(InputField)) as InputField;
            var label = TypeHelper.Il2CppCast(labelObj, typeof(Text)) as Text;
            if (field == null || label == null) return;

            int id = field.GetInstanceID();
            // The mod's own field draws in the font of the part it shows: the game's source text or
            // its translation (ModWindowText), else the interface's.
            var side = ownUi ? ModWindowText.SideOf(field) ?? ModWindowText.SideOf(label) : null;
            var derived = ownUi ? FontManager.DerivedForWindow(side) : FontManager.DerivedForSettings(settingsFontName);
            // A copy shapes only the letters its font has (FontManager.Covers): the window's Arial
            // has a copy and no Hindi.
            NoteIfUnshaped(derived != null && (!ownUi || FontManager.Covers(TranslatorCore.WindowFontFor(side), value) != false),
                           value, settingsFontName, ownUi, side);
            var prep = string.IsNullOrEmpty(value) ? null : RtlFieldLayout.Prepare(value, UnitsOf(derived));
            if (prep == null) { Release(id); return; }

            List<int> wraps = null;
            long labelId = label.GetInstanceID();
            if (field.lineType != InputField.LineType.SingleLine)
            {
                // Measured under the label's OWN wrapping: the Overflow held for the previous text
                // answers "one line" for any paragraph.
                RtlPresenter.RestoreRewrap(label, labelId);
                wraps = RtlPresenter.UGuiLineStartsNow(label, prep.MeasureText, out string whyNot);
                if (wraps == null) Note($"multi-line field laid out on its hard breaks only ({whyNot})");
                // 🔴 OUR breaks are the lines: the engine must not wrap them again. A visual line as
                // wide as the box was folded by rendering rounding, and the sentence's FIRST word
                // (the right end of its visual line) landed alone on the next row — read out of
                // order (bench: a three-line Hebrew field drawn in four pieces). Same hold as a
                // reflowed text (RtlPresenter.DisableRewrap), put back when the field is released.
                else
                {
                    RtlPresenter.DisableRewrap(label, labelId);
                    // A tag wider than the line, cut by the engine: cut there too.
                    prep = prep.SplitTokensAt(prep.TokenSplitsAt(wraps));
                }
            }

            var s = StateFor(id, field, Engine.UGui);
            s.UField = field;
            s.Label = label;
            s.LabelObj = label;
            s.Prep = prep;
            s.Layout = prep.Lay(wraps);
            s.Logical = value;
            s.Shown = s.Layout.Display;
            s.DrawStart = ReadDrawStart(field);
            HideNative(s);

            // Our own output: nothing learns from it, and the in-game editor recovers the typed
            // text behind it (D8 — nothing shaped ever reaches the cache, the file or a server).
            TranslatorCore.RegisterPresentedText(s.Shown, value);
            Describe("uGUI", label, value, s.Shown);
            value = s.Shown;
        }

        // Every field text presented, code point by code point, once per text (debug only — what
        // somebody types is many texts): a screen cannot say whether a glyph drawn detached is a
        // letter we did not shape or a font that draws its shaped form that way — the log can
        // (first seen on a mod editor field, 2026-09-25). Never a count of the first few.
        private static void Describe(string kind, object label, string typed, string shown)
        {
            if (!TranslatorCore.DebugMode || !DiagnosticOnce.First("RtlInputFields.describe", kind + "\u0001" + typed)) return;
            string font = (label as Text)?.font != null ? (label as Text).font.name : "?";
            TranslatorCore.LogInfo($"[RtlInputFields] {kind} field (font {font}) typed: {RtlPresenter.Escape(typed)}");
            TranslatorCore.LogInfo($"[RtlInputFields] {kind} field shows: {RtlPresenter.Escape(shown)}");
        }

        /// <summary>The field's font's shaping as units, naming new glyphs in its derived copy (rewritten this tick).</summary>
        private static Func<string, List<OpenTypeText.ShapedUnit>> UnitsOf(DerivedFonts.Entry derived)
        {
            if (derived == null) return null;
            return text =>
            {
                var units = OpenTypeText.ShapeUnits(text, derived.Font, derived.Namer);
                DerivedFonts.NoteNamed(derived);
                return units;
            };
        }

        /// <summary>
        /// A derived copy was rewritten (new glyphs named for what was just typed): every uGUI field
        /// presented draws its label again — with the font object made from the new copy.
        /// </summary>
        internal static void OnDerivedFontRewritten()
        {
            foreach (var s in _states.Values)
                if (s.Kind == Engine.UGui && Alive(s) && s.UField != null) s.UField.ForceLabelUpdate();
        }

        private static void PresentTmpLabel(object fieldObj, object labelObj, ref string value, ShapingFontAsset asset)
        {
            if (!Tmp.Resolve()) return;

            int id = TypeHelper.GetInstanceID(fieldObj);
            // TMP_InputField appends a zero-width space (U+200B) "for caret tracking": kept as it
            // is, after the typed text.
            bool tracked = !string.IsNullOrEmpty(value) && value[value.Length - 1] == RtlFieldLayout.ZeroWidthSpace;
            string tail = tracked ? RtlFieldLayout.ZeroWidthSpace.ToString() : "";
            string logical = tail.Length > 0 ? value.Substring(0, value.Length - 1) : value;

            var prep = string.IsNullOrEmpty(logical) ? null
                : RtlFieldLayout.Prepare(logical, asset == null ? (Func<string, List<OpenTypeText.ShapedUnit>>)null : t => OpenTypeText.ShapeUnits(t, asset.Font, asset));
            if (prep == null) { Release(id); return; }

            var label = TypeHelper.Il2CppCast(labelObj, typeof(Graphic)) as Graphic;
            if (label == null) { Note("TMP label is not a Graphic on this runtime"); return; }

            // 🔴 The same text written again (TMP does it when the field takes the focus): TMP lays out
            // nothing for an unchanged text, so the glyph move — TMP's lines, the boxes the caret is
            // drawn from — would never come back. Kept as it stands: reset, the field had no caret
            // at all (its own is hidden), bench 2026-10-04.
            if (_states.TryGetValue(id, out var held) && held.Kind == Engine.Tmp && held.Label == label
                && held.Logical == logical && !string.IsNullOrEmpty(held.Shown)
                && (held.Shown[held.Shown.Length - 1] == RtlFieldLayout.ZeroWidthSpace) == tracked
                && LabelText(held) == held.Shown)
            {
                value = held.Shown;
                return;
            }

            var s = StateFor(id, fieldObj, Engine.Tmp);
            s.Label = label;
            s.LabelObj = labelObj;
            s.Prep = prep;
            s.Layout = prep.Lay(null);          // re-laid with TMP's own lines at the glyph move
            s.Logical = logical;
            string shown = s.Layout.MirroredLabel(prep.LabelFor(out var labelIndex, out var labelLength), labelIndex);
            if (tail.Length > 0)
            {
                // TMP's caret-tracking space stands for the end of the typed text.
                Array.Resize(ref labelIndex, labelIndex.Length + 1); labelIndex[labelIndex.Length - 1] = logical.Length;
                Array.Resize(ref labelLength, labelLength.Length + 1); labelLength[labelLength.Length - 1] = 1;
            }
            s.LabelIndex = labelIndex;
            s.LabelLength = labelLength;
            s.Shown = shown + tail;
            s.DrawStart = 0;
            s.BoxesFromReorder = false;
            _byTmpLabel[label.GetInstanceID()] = s;
            HideNative(s);

            TranslatorCore.RegisterPresentedText(s.Shown, value);
            Describe("TMP", labelObj, value, s.Shown);
            value = s.Shown;
        }

        private static FieldState StateFor(int id, object field, Engine kind)
        {
            if (!_states.TryGetValue(id, out var s))
            {
                s = new FieldState { FieldRef = new WeakReference(field), Kind = kind };
                _states[id] = s;
            }
            s.Field = field;
            s.Kind = kind;
            return s;
        }

        /// <summary>A field that stopped showing right-to-left text: its own caret and selection back.</summary>
        private static void Release(int id)
        {
            if (!_states.TryGetValue(id, out var s)) return;
            _states.Remove(id);
            if (s.Label != null) _byTmpLabel.Remove(s.Label.GetInstanceID());
            if (s.Label is Text uLabel && Alive(s)) RtlPresenter.RestoreRewrap(uLabel, uLabel.GetInstanceID());
            RestoreNative(s);
            if (s.Overlay != null) UnityEngine.Object.Destroy(s.Overlay);
        }

        // ══ The field's own caret and selection: transparent while presented ═══════════════

        private static void HideNative(FieldState s)
        {
            if (s.Hidden) return;
            s.OriginalCustomCaret = GetBool(s, "customCaretColor");
            s.OriginalCaret = GetColor(s, "caretColor");
            s.OriginalSelection = GetColor(s, "selectionColor");
            SetBool(s, "customCaretColor", true);
            SetColor(s, "caretColor", Transparent(s.OriginalCaret));
            SetColor(s, "selectionColor", Transparent(s.OriginalSelection));
            s.Hidden = true;
        }

        private static void RestoreNative(FieldState s)
        {
            if (!s.Hidden || !Alive(s)) return;
            SetColor(s, "caretColor", s.OriginalCaret);
            SetBool(s, "customCaretColor", s.OriginalCustomCaret);
            SetColor(s, "selectionColor", s.OriginalSelection);
            s.Hidden = false;
        }

        private static Color Transparent(Color c) => new Color(c.r, c.g, c.b, 0f);

        // ══ Every frame: caret and selection ═════════════════════════════════════════════════

        /// <summary>Draw the caret or the selection of every presented field that has the focus.</summary>
        internal static void Tick()
        {
            if (_states.Count == 0) return;
            _scratch.Clear();
            _scratch.AddRange(_states.Keys);
            foreach (int id in _scratch)
            {
                if (!_states.TryGetValue(id, out var s)) continue;
                if (!Alive(s)) { Forget(id, s); continue; }

                // The label carries something else now (written without our presentation —
                // translations switched off, say): hand the field back.
                if (LabelText(s) != s.Shown) { Release(id); continue; }

                if (!IsFocused(s)) { ShowQuads(s, 0); continue; }
                if (!ReadBoxes(s))
                {
                    // Focused and presented, yet nowhere to draw the caret: said once per field and cause.
                    if (TranslatorCore.DebugMode && DiagnosticOnce.First("RtlInputFields.noboxes", s.Kind + "\u0001" + id))
                        TranslatorCore.LogInfo($"[RtlInputFields] focused {s.Kind} field has no glyph boxes — "
                            + (s.Kind == Engine.Tmp ? "no glyph move since its text was presented (GenerateTextMesh not seen for its label)" : "its generator gave no glyphs"));
                    ShowQuads(s, 0);
                    continue;
                }

                EnsureOverlay(s);
                int anchor = Anchor(s), focus = Focus(s);
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

        private static void Forget(int id, FieldState s)
        {
            _states.Remove(id);
            if (s.Label != null) _byTmpLabel.Remove(s.Label.GetInstanceID());
            if (s.Overlay != null) UnityEngine.Object.Destroy(s.Overlay);
        }

        private static void DrawCaret(FieldState s, int caret)
        {
            float rate = BlinkRate(s);
            bool on = rate <= 0f || ((Time.unscaledTime - s.BlinkStart) * rate) % 1f < 0.5f;
            if (!on) { ShowQuads(s, 0); return; }

            float width = CaretWidth(s);
            if (!CaretPlace(s, caret, width, out float x, out int g)) { ShowQuads(s, 0); return; }

            float top = s.LineTop[g], bottom = s.LineBottom[g];
            // The colour the field would have drawn: caretColor answers the text's own colour when
            // no custom one is set, and that is what was read before hiding it.
            Place(s, 0, x, bottom, width, top - bottom, s.OriginalCaret);
            ShowQuads(s, 1);
        }

        /// <summary>
        /// Where the caret before typed character <paramref name="caret"/> is drawn: its x in the
        /// label's local space and the engine line it stands on. False when it stands on no line drawn.
        /// </summary>
        private static bool CaretPlace(FieldState s, int caret, float width, out float x, out int g)
        {
            s.Layout.CaretAnchor(caret, out int d, out bool rightEdge, out int line);
            int i = d < 0 ? -1 : BoxedNear(s, s.Layout.LogicalAtDisplay(d));
            if (i < 0)
            {
                // An empty line: the side the text sits on.
                g = line < s.EngineLineOfLine.Length ? s.EngineLineOfLine[line] : -1;
                var rect = s.Label.rectTransform.rect;
                x = s.Layout.LineIsRtl(line) ? rect.xMax - width : rect.xMin;
            }
            else
            {
                g = s.BoxLine[i];
                x = rightEdge ? s.BoxR[i] : s.BoxL[i];
            }
            return g >= 0 && g < s.LineTop.Count;
        }

        private static void DrawSelection(FieldState s, int from, int to)
        {
            // One interval per glyph, merged per engine line: a bidi selection can be several
            // disjoint pieces on one line, and each is drawn where it shows.
            var spans = new List<KeyValuePair<int, Vector2>>();
            for (int i = Math.Max(0, from); i < Math.Min(to, s.Logical.Length); i++)
            {
                if (s.Logical[i] == '\n' || float.IsNaN(s.BoxL[i])) continue;
                float a = Math.Min(s.BoxL[i], s.BoxR[i]), b = Math.Max(s.BoxL[i], s.BoxR[i]);
                if (b - a < 0.01f) continue;
                spans.Add(new KeyValuePair<int, Vector2>(s.BoxLine[i], new Vector2(a, b)));
            }
            spans.Sort((p, q) => p.Key != q.Key ? p.Key.CompareTo(q.Key) : p.Value.x.CompareTo(q.Value.x));

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
                if (g < 0 || g >= s.LineTop.Count) continue;
                Place(s, count++, x0, s.LineBottom[g], x1 - x0, s.LineTop[g] - s.LineBottom[g], s.OriginalSelection);
            }
            ShowQuads(s, count);
        }

        /// <summary>
        /// A typed character that has a box, starting at <paramref name="i"/>: a tag parsed away by
        /// TMP has none, and the caret then leans on its nearest neighbour.
        /// </summary>
        private static int BoxedNear(FieldState s, int i)
        {
            if (i < 0 || i >= s.BoxL.Length) return -1;
            for (int k = 0; k < s.BoxL.Length; k++)
            {
                if (i + k < s.BoxL.Length && !float.IsNaN(s.BoxL[i + k])) return i + k;
                if (i - k >= 0 && !float.IsNaN(s.BoxL[i - k])) return i - k;
            }
            return -1;
        }

        // ══ Glyph boxes ══════════════════════════════════════════════════════════════════════

        private static bool ReadBoxes(FieldState s)
        {
            int n = s.Logical.Length;
            if (s.BoxL.Length != n)
            {
                s.BoxL = new float[n];
                s.BoxR = new float[n];
                s.BoxLine = new int[n];
            }

            if (s.Kind == Engine.UGui)
            {
                if (!RtlPresenter.ReadUGuiGlyphs(s.Label, s.Shown, s.X, s.W, s.GLineStart, s.GLineTop, s.GLineHeight))
                    return false;
                for (int i = 0; i < n; i++)
                {
                    int d = s.Layout.DisplayOf(i);
                    if (d < 0 || d >= s.X.Count) { s.BoxL[i] = s.BoxR[i] = float.NaN; s.BoxLine[i] = -1; continue; }
                    // A shaped unit is all its glyphs: from the leftmost pen to the furthest advance.
                    float left = s.X[d], right = s.X[d] + s.W[d];
                    for (int u = 1; u < s.Layout.DisplayLengthOf(i) && d + u < s.X.Count; u++)
                    {
                        left = Math.Min(left, s.X[d + u]);
                        right = Math.Max(right, s.X[d + u] + s.W[d + u]);
                    }
                    s.BoxL[i] = left;
                    s.BoxR[i] = right;
                    s.BoxLine[i] = GeneratorLineOf(s, d);
                }
                s.LineTop.Clear(); s.LineBottom.Clear();
                for (int g = 0; g < s.GLineTop.Count; g++)
                {
                    s.LineTop.Add(s.GLineTop[g]);
                    s.LineBottom.Add(s.GLineTop[g] - s.GLineHeight[g]);
                }
                s.EngineLineOfLine = new int[s.Layout.LineCount];
                for (int L = 0; L < s.Layout.LineCount; L++)
                    s.EngineLineOfLine[L] = GeneratorLineOf(s, s.Layout.LineDisplayStart(L));
                return true;
            }

            // TMP: the boxes are the ones the glyph move computed, with the lines it read.
            return s.BoxesFromReorder;
        }

        private static int GeneratorLineOf(FieldState s, int d)
        {
            int found = -1;
            for (int g = 0; g < s.GLineStart.Count; g++)
                if (s.GLineStart[g] <= d) found = g;
            return found;
        }

        // ══ TMP: the glyphs moved into visual order after each layout ══════════════════════

        /// <summary>
        /// After TMP laid out a label (TmpMeshDone): when it is a presented field's label, move its
        /// glyphs into visual order, line by line. The lines are TMP's own — it wrapped the
        /// typed-order text, which is exactly where a paragraph is cut. Returns how far each
        /// character moved (null: nothing moved); the caller uploads the vertices.
        /// </summary>
        internal static float[] GenerateTextMeshDone(object __instance)
        {
            if (_byTmpLabel.Count == 0 || __instance == null) return null;
            try
            {
                int labelId = TypeHelper.GetInstanceID(__instance);
                if (!_byTmpLabel.TryGetValue(labelId, out var s)) return null;
                if (LabelText(s) != s.Shown)
                {
                    if (TranslatorCore.DebugMode && DiagnosticOnce.First("RtlInputFields.tmpother", labelId + "\u0001" + LabelText(s)))
                        TranslatorCore.LogInfo($"[RtlInputFields] TMP field label laid out with another text than presented: {RtlPresenter.Escape(LabelText(s) ?? "null")} (presented {RtlPresenter.Escape(s.Shown)})");
                    return null;
                }
                TellTypedPositions(s, __instance);
                var moved = MoveTmpGlyphs(s, __instance);
                if (TranslatorCore.DebugMode && DiagnosticOnce.First("RtlInputFields.tmpmoved", labelId + "\u0001" + s.Shown))
                    TranslatorCore.LogInfo($"[RtlInputFields] TMP field label laid out: boxes {(s.BoxesFromReorder ? "read" : "NOT read")}, {s.Layout.LineCount} line(s)");
                return moved;
            }
            catch (Exception ex) { Note("TMP glyph move failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Every characterInfo of the label TMP just laid out gets the typed position and length it
        /// stands for (RtlFieldLayout.Prepared.LabelFor) — what TMP_InputField reads to edit, select
        /// and place its caret. Before, its index is a position in the LABEL, which holds more
        /// characters than were typed when a unit draws more glyphs than it has characters.
        /// </summary>
        private static void TellTypedPositions(FieldState s, object label)
        {
            if (s.LabelIndex == null || TmpLayout.CiStringLength == null) return;
            var info = TmpLayout.TextInfo.GetValue(label, null);
            if (info == null) return;
            int count = Convert.ToInt32(TmpLayout.Get(TmpLayout.CharacterCount, info));
            var chars = TmpLayout.Get(TmpLayout.CharacterInfo, info);
            if (chars == null) return;
            for (int k = 0; k < count; k++)
            {
                var c = EngineCollections.Item(chars, k);
                int at = Convert.ToInt32(TmpLayout.Get(TmpLayout.CiIndex, c));
                if (at < 0 || at >= s.LabelIndex.Length) continue;
                // The label characters this one stands for: one, or a whole tag TMP draws as one
                // character (a <sprite=…>) — the typed span from the first's start to the last's end.
                // Told one character long, Backspace took the tag's "<" alone and left "sprite=2>",
                // where TMP's own field removes the sprite whole (bench 2026-10-04, Latin witness).
                int span = Math.Max(1, Convert.ToInt32(TmpLayout.Get(TmpLayout.CiStringLength, c)));
                int last = Math.Min(s.LabelIndex.Length - 1, at + span - 1);
                int typedStart = s.LabelIndex[at];
                int typedEnd = Math.Max(typedStart + s.LabelLength[at], s.LabelIndex[last] + s.LabelLength[last]);
                TmpLayout.Set(TmpLayout.CiIndex, c, typedStart);
                TmpLayout.Set(TmpLayout.CiStringLength, c, typedEnd - typedStart);
                EngineCollections.SetItem(chars, k, c);
            }
        }

        private static float[] MoveTmpGlyphs(FieldState s, object label)
        {
            var info = TmpLayout.TextInfo.GetValue(label, null);
            if (info == null) return null;
            int count = Convert.ToInt32(TmpLayout.Get(TmpLayout.CharacterCount, info));
            int lineCount = Convert.ToInt32(TmpLayout.Get(TmpLayout.LineCount, info));
            var chars = TmpLayout.Get(TmpLayout.CharacterInfo, info);
            var lines = TmpLayout.Get(TmpLayout.LineInfo, info);
            var meshes = TmpLayout.Get(TmpLayout.MeshInfo, info);
            if (chars == null || lines == null || count <= 0) return null;

            int n = s.Logical.Length;
            // The label characters drawing each typed one, in label order — several for a typed
            // character a unit drew more glyphs for (a split vowel), none for one merged away.
            var kOf = new List<int>[n];
            var origin = new float[count];
            var advance = new float[count];
            var lineOf = new int[count];
            var indexOf = new int[count];
            for (int k = 0; k < count; k++)
            {
                var c = EngineCollections.Item(chars, k);
                indexOf[k] = Convert.ToInt32(TmpLayout.Get(TmpLayout.CiIndex, c));
                origin[k] = Convert.ToSingle(TmpLayout.Get(TmpLayout.CiOrigin, c));
                advance[k] = Convert.ToSingle(TmpLayout.Get(TmpLayout.CiXAdvance, c));
                lineOf[k] = Convert.ToInt32(TmpLayout.Get(TmpLayout.CiLine, c));
                if (indexOf[k] >= 0 && indexOf[k] < n) (kOf[indexOf[k]] ?? (kOf[indexOf[k]] = new List<int>())).Add(k);
            }
            // How far the pen moves past each character as TMP laid the label out: to the next
            // character's origin on the same line — kerning included, which TMP adds to the NEXT
            // origin, never to xAdvance — else its own advance. Moved by these widths, a line that
            // needs no reordering does not move at all (a Latin word kept its kerning: 2026-10-01).
            var pen = new float[count];
            for (int k = 0; k < count; k++)
                pen[k] = k + 1 < count && lineOf[k + 1] == lineOf[k] ? origin[k + 1] - origin[k] : advance[k] - origin[k];

            // TMP's own line starts, as typed indices: the soft wraps to lay out against.
            var wraps = new List<int>();
            s.LineTop.Clear(); s.LineBottom.Clear();
            for (int g = 0; g < lineCount; g++)
            {
                var li = EngineCollections.Item(lines, g);
                s.LineTop.Add(Convert.ToSingle(TmpLayout.Get(TmpLayout.LiAscender, li)));
                s.LineBottom.Add(Convert.ToSingle(TmpLayout.Get(TmpLayout.LiDescender, li)));
                if (g == 0) continue;
                int first = Convert.ToInt32(TmpLayout.Get(TmpLayout.LiFirst, li));
                if (first < 0 || first >= count) continue;
                int idx = indexOf[first];
                if (idx > 0 && idx < n && s.Logical[idx - 1] != '\n') wraps.Add(idx);
            }
            // A tag wider than the line, cut by TMP: cut there too (the label is unchanged — each
            // character of a written-out token stands for itself).
            s.Prep = s.Prep.SplitTokensAt(s.Prep.TokenSplitsAtLogical(wraps));
            s.Layout = s.Prep.LayAtLogical(wraps);

            if (s.BoxL.Length != n)
            {
                s.BoxL = new float[n];
                s.BoxR = new float[n];
                s.BoxLine = new int[n];
            }
            for (int i = 0; i < n; i++) { s.BoxL[i] = s.BoxR[i] = float.NaN; s.BoxLine[i] = -1; }

            bool moved = false;
            var shift = new float[count];
            s.EngineLineOfLine = new int[s.Layout.LineCount];
            for (int L = 0; L < s.Layout.LineCount; L++)
            {
                var order = s.Layout.LogicalOnScreen(L);
                int engineLine = -1;
                float startX = float.MaxValue;
                foreach (int i in order)
                {
                    if (kOf[i] == null) continue;
                    foreach (int k in kOf[i])
                    {
                        if (engineLine < 0) engineLine = lineOf[k];
                        startX = Math.Min(startX, origin[k]);
                    }
                }
                if (engineLine < 0)
                {
                    int start = s.Layout.LineLogicalStart(L);
                    engineLine = start < n && kOf[start] != null ? lineOf[kOf[start][0]] : Math.Min(L, lineCount - 1);
                }
                s.EngineLineOfLine[L] = engineLine;
                if (startX == float.MaxValue) continue;

                float cursor = startX;
                foreach (int i in order)
                {
                    if (kOf[i] == null) continue;
                    s.BoxL[i] = cursor;
                    s.BoxLine[i] = lineOf[kOf[i][0]];
                    foreach (int k in kOf[i])
                    {
                        float w = pen[k];
                        float delta = cursor - origin[k];
                        if (Math.Abs(delta) > 0.001f)
                        {
                            MoveTmpQuad(chars, meshes, k, delta);
                            shift[k] += delta;
                            moved = true;
                        }
                        cursor += w;
                    }
                    s.BoxR[i] = cursor;
                }
            }

            // A character merged into the glyph before it (the alef of a lam-alef) is drawn by it.
            for (int i = 1; i < n; i++)
                if (float.IsNaN(s.BoxL[i]) && s.Layout.DisplayOf(i) == s.Layout.DisplayOf(i - 1))
                {
                    s.BoxL[i] = s.BoxL[i - 1];
                    s.BoxR[i] = s.BoxR[i - 1];
                    s.BoxLine[i] = s.BoxLine[i - 1];
                }

            s.BoxesFromReorder = true;
            return moved ? shift : null;
        }

        /// <summary>Shift one character's four vertices horizontally in the mesh TMP just built.</summary>
        private static void MoveTmpQuad(object chars, object meshes, int k, float delta)
        {
            var c = EngineCollections.Item(chars, k);
            if (!Convert.ToBoolean(TmpLayout.Get(TmpLayout.CiVisible, c))) return;
            int material = Convert.ToInt32(TmpLayout.Get(TmpLayout.CiMaterial, c));
            int vertex = Convert.ToInt32(TmpLayout.Get(TmpLayout.CiVertex, c));
            var mesh = EngineCollections.Item(meshes, material);
            var vertices = mesh == null ? null : TmpLayout.Get(TmpLayout.MiVertices, mesh);
            if (vertices == null) return;
            for (int v = 0; v < 4; v++)
            {
                var p = (Vector3)EngineCollections.Item(vertices, vertex + v);
                p.x += delta;
                EngineCollections.SetItem(vertices, vertex + v, p);
            }
        }

        // ══ Clicks ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// uGUI: a click (or a drag) → the character index under the pointer, from the map instead
        /// of Unity's left-to-right walk. Relative to the visible slice, as Unity's is.
        /// </summary>
        public static void UGui_GetCharacterIndexFromPosition_Postfix(object __instance, Vector2 __0, ref int __result)
        {
            try
            {
                var s = StateOf(__instance);
                if (s == null || !ReadBoxes(s)) return;
                s.GoalLanded = -1;   // a click starts a new column
                __result = CaretAt(s, __0);
            }
            catch (Exception ex) { Note("click mapping failed: " + ex.Message); }
        }

        private static int _tmpAnchorBefore = -1, _tmpFocusBefore = -1;

        public static void Tmp_OnPointerDown_Prefix(object __instance)
        {
            var s = StateOf(__instance);
            _tmpAnchorBefore = s == null ? -1 : Anchor(s);
            _tmpFocusBefore = s == null ? -1 : Focus(s);
        }

        /// <summary>
        /// TMP: the field placed the caret from its own reading of the glyphs; put it where the
        /// click was, from the map. A double click has selected a word by typed indices, which is
        /// right as it is and is left alone.
        /// </summary>
        public static void Tmp_OnPointerDown_Postfix(object __instance, PointerEventData __0)
        {
            try
            {
                var s = StateOf(__instance);
                if (s == null || __0 == null || !ReadBoxes(s)) return;
                s.GoalLanded = -1;   // a click starts a new column
                int anchor = Anchor(s), focus = Focus(s);
                bool wordSelected = anchor != focus && anchor != _tmpAnchorBefore;
                if (wordSelected) return;
                if (!ScreenToLocal(s.Label.rectTransform, __0.position, __0.pressEventCamera, out Vector2 local)) return;
                int caret = CaretAt(s, local);
                bool extending = anchor != focus && anchor == _tmpAnchorBefore;
                if (extending) SetFocus(s, caret);
                else SetCaret(s, caret);
            }
            catch (Exception ex) { Note("TMP click mapping failed: " + ex.Message); }
        }

        /// <summary>TMP: a drag extends the selection to where the pointer is, from the map.</summary>
        public static void Tmp_OnDrag_Postfix(object __instance, PointerEventData __0)
        {
            try
            {
                var s = StateOf(__instance);
                if (s == null || __0 == null || !ReadBoxes(s)) return;
                if (!ScreenToLocal(s.Label.rectTransform, __0.position, __0.pressEventCamera, out Vector2 local)) return;
                SetFocus(s, CaretAt(s, local));
            }
            catch (Exception ex) { Note("TMP drag mapping failed: " + ex.Message); }
        }

        /// <summary>The caret under a point of the label's local space.</summary>
        private static int CaretAt(FieldState s, Vector2 pos)
        {
            // The engine line under the point: nearest by its band.
            int g = 0;
            float best = float.MaxValue;
            for (int k = 0; k < s.LineTop.Count; k++)
            {
                float top = s.LineTop[k], bottom = s.LineBottom[k];
                float dist = pos.y > top ? pos.y - top : pos.y < bottom ? bottom - pos.y : 0f;
                if (dist < best) { best = dist; g = k; }
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            for (int i = 0; i < s.Logical.Length; i++)
            {
                if (s.BoxLine[i] != g || s.Logical[i] == '\n' || float.IsNaN(s.BoxL[i])) continue;
                float x0 = Math.Min(s.BoxL[i], s.BoxR[i]), x1 = Math.Max(s.BoxL[i], s.BoxR[i]);
                if (x1 - x0 < 0.01f) continue;
                minX = Math.Min(minX, x0);
                maxX = Math.Max(maxX, x1);
                if (pos.x >= x0 && pos.x < x1)
                    return s.Layout.CaretFromHit(s.Layout.DisplayOf(i), pos.x >= (x0 + x1) * 0.5f);
            }

            int line = 0;
            for (int L = 0; L < s.EngineLineOfLine.Length; L++)
                if (s.EngineLineOfLine[L] == g) { line = L; break; }
            return s.Layout.CaretAtLineSide(line, rightSide: minX == float.MaxValue || pos.x >= maxX);
        }

        /// <summary>
        /// A screen point in a RectTransform's local space, without RectTransformUtility (IL2CPP
        /// pitfall, analyse/pieges-projet.md §2): an overlay canvas is in screen units already, a
        /// camera canvas is a plane the pointer's ray meets.
        /// </summary>
        private static bool ScreenToLocal(RectTransform rect, Vector2 screen, Camera camera, out Vector2 local)
        {
            local = Vector2.zero;
            if (rect == null) return false;
            if (camera == null)
            {
                Vector3 p = rect.InverseTransformPoint(new Vector3(screen.x, screen.y, rect.position.z));
                local = new Vector2(p.x, p.y);
                return true;
            }
            var ray = camera.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
            var plane = new Plane(rect.forward, rect.position);
            if (!plane.Raycast(ray, out float distance)) return false;
            Vector3 q = rect.InverseTransformPoint(ray.GetPoint(distance));
            local = new Vector2(q.x, q.y);
            return true;
        }

        // ══ Arrow keys ═══════════════════════════════════════════════════════════════════════

        public static bool UGui_MoveLeft_Prefix(object __instance, bool __0, bool __1) => !Move(__instance, false, __0, __1);
        public static bool UGui_MoveRight_Prefix(object __instance, bool __0, bool __1) => !Move(__instance, true, __0, __1);
        public static bool Tmp_MoveLeft_Prefix(object __instance, bool __0, bool __1) => !Move(__instance, false, __0, __1);
        public static bool Tmp_MoveRight_Prefix(object __instance, bool __0, bool __1) => !Move(__instance, true, __0, __1);

        /// <summary>One arrow press on a presented field, by screen position. True when handled.</summary>
        private static bool Move(object instance, bool toRight, bool shift, bool ctrl)
        {
            try
            {
                var s = StateOf(instance);
                if (s == null) return false;
                s.GoalLanded = -1;   // a move along the line starts a new column
                int anchor = Anchor(s), focus = Focus(s);
                var layout = s.Layout;

                // A selection and no shift: collapse to its end on that side of the screen.
                if (!shift && anchor != focus)
                {
                    bool anchorFurther = toRight
                        ? layout.BoundaryOf(anchor) > layout.BoundaryOf(focus)
                        : layout.BoundaryOf(anchor) < layout.BoundaryOf(focus);
                    SetCaret(s, anchorFurther ? anchor : focus);
                    return true;
                }

                int next = ctrl ? WordStep(layout, s.Logical, focus, toRight) : layout.VisualStep(focus, toRight);
                if (next == focus && (s.DrawStart > 0 || s.Logical.Length < FieldText(s).Length))
                {
                    // The visible slice's edge (uGUI scrolls a long line): onward in reading order,
                    // one character — the field then brings the caret into view itself. Only a
                    // slice: with the whole text in view, the edge of the screen is the end — moving
                    // on there took the caret back the other way at the end of a mixed line
                    // (a number ending an Arabic line on its left; bench, 2026-10-03).
                    int line = layout.LineOfCaret(focus);
                    bool forward = toRight != layout.LineIsRtl(line);
                    int absolute = focus + s.DrawStart + (forward ? 1 : -1);
                    if (absolute < 0 || absolute > FieldText(s).Length) return true;
                    next = absolute - s.DrawStart;
                }

                if (shift) SetFocus(s, next);
                else SetCaret(s, next);
                return true;
            }
            catch (Exception ex)
            {
                Note("arrow move failed, Unity's own used: " + ex.Message);
                return false;
            }
        }

        // ══ Up / Down ════════════════════════════════════════════════════════════════════════

        public static bool UGui_MoveUp_Prefix(object __instance, bool __0, bool __1) => !Vertical(__instance, false, __0, __1);
        public static bool UGui_MoveDown_Prefix(object __instance, bool __0, bool __1) => !Vertical(__instance, true, __0, __1);
        public static bool Tmp_MoveUp_Prefix(object __instance, bool __0, bool __1) => !Vertical(__instance, false, __0, __1);
        public static bool Tmp_MoveDown_Prefix(object __instance, bool __0, bool __1) => !Vertical(__instance, true, __0, __1);

        /// <summary>
        /// MoveUp / MoveDown(shift, goToFirstChar / goToLastChar) on a presented field: the line drawn
        /// above or below, at the caret nearest on screen to the kept column — the standard of every
        /// editor (user's decision, 2026-10-04). Unity looks for that line in the label's characters
        /// with the TYPED index (uGUI's generator holds the presented form, TMP's characterInfo stays
        /// in typed order while its glyphs are moved). Without Shift a selection collapses as Unity
        /// collapses it — Up from its earlier end, Down from its later one; past the first or last line
        /// drawn, what Unity does: the start or end of the text when asked to, else nowhere. True when handled.
        /// </summary>
        private static bool Vertical(object instance, bool down, bool shift, bool toTextEdge)
        {
            try
            {
                var s = StateOf(instance);
                // A single-line field: Unity goes to the start or end of the text, reading no label.
                if (s == null || !IsMultiLine(s) || !ReadBoxes(s)) return false;
                int anchor = Anchor(s), focus = Focus(s);
                int from = shift || anchor == focus ? focus : down ? Math.Max(anchor, focus) : Math.Min(anchor, focus);
                float width = CaretWidth(s);
                Func<int, float> xOf = c => CaretPlace(s, c, width, out float x, out _) ? x : float.NaN;
                float goal = s.GoalLanded == from && ReferenceEquals(s.GoalShown, s.Shown) ? s.GoalX : xOf(from);
                if (float.IsNaN(goal)) return false;

                int next = s.Layout.VerticalStep(from, down, goal, xOf);
                if (next < 0)
                {
                    bool sliceGoesOn = down ? s.DrawStart + s.Logical.Length < FieldText(s).Length : s.DrawStart > 0;
                    if (sliceGoesOn)
                    {
                        // uGUI shows a slice of a long text: the next line is out of view. Onto the
                        // first character past the slice's edge — the field brings it into view itself.
                        next = down ? s.Logical.Length + 1 : -1;
                    }
                    else next = toTextEdge ? (down ? s.Logical.Length : 0) : from;
                }
                s.GoalLanded = next;
                s.GoalX = goal;
                s.GoalShown = s.Shown;
                if (shift) SetFocus(s, next);
                else SetCaret(s, next);
                return true;
            }
            catch (Exception ex)
            {
                Note("Up/Down failed, Unity's own used: " + ex.Message);
                return false;
            }
        }

        /// <summary>Ctrl + arrow: visual steps until the caret stands at the start of a word.</summary>
        private static int WordStep(RtlFieldLayout layout, string logical, int caret, bool toRight)
        {
            int current = caret;
            for (int guard = 0; guard <= logical.Length; guard++)
            {
                int next = layout.VisualStep(current, toRight);
                if (next == current) return current;
                current = next;
                bool atWordStart = current < logical.Length && !UnicodeInfo.IsWhiteSpace(logical[current])
                                   && (current == 0 || UnicodeInfo.IsWhiteSpace(logical[current - 1]));
                if (atWordStart || current == 0 || current == logical.Length) return current;
            }
            return current;
        }

        // ══ The overlay: a sibling of the label, BEHIND it like Unity's own caret ══════════

        private static void EnsureOverlay(FieldState s)
        {
            var labelRect = s.Label.rectTransform;
            if (s.Overlay == null)
            {
                s.Overlay = EngineStrings.NewGameObject("UGT RTL caret");
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
            var pivot = s.Label.rectTransform.pivot;
            while (s.Quads.Count <= index)
            {
                var go = EngineStrings.NewGameObject("q" + s.Quads.Count);
                go.transform.SetParent(s.Overlay.transform, false);
                var image = go.AddComponent<Image>();
                image.raycastTarget = false;
                var r = go.GetComponent<RectTransform>();
                r.pivot = Vector2.zero;
                s.Quads.Add(image);
            }
            var q = s.Quads[index];
            q.color = color;
            var rt = q.rectTransform;
            // Anchored at the label's pivot: that point is the origin of the label's local space,
            // where every position above is measured.
            rt.anchorMin = pivot;
            rt.anchorMax = pivot;
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

        // ══ The field, whichever it is ═══════════════════════════════════════════════════════

        private static FieldState StateOf(object instance)
        {
            if (_states.Count == 0 || instance == null) return null;
            int id = TypeHelper.GetInstanceID(instance);
            return _states.TryGetValue(id, out var s) && Alive(s) && LabelText(s) == s.Shown ? s : null;
        }

        private static bool Alive(FieldState s)
        {
            if (s.Label == null) return false;
            var f = s.FieldRef.Target;
            return f != null && !(f is UnityEngine.Object uo && uo == null);
        }

        private static string LabelText(FieldState s) =>
            s.Kind == Engine.UGui ? (s.Label as Text)?.text : Tmp.Resolve() ? Tmp.LabelText.GetValue(s.LabelObj, null) as string : null;

        private static string FieldText(FieldState s) =>
            (s.Kind == Engine.UGui ? s.UField.text : Tmp.FieldText.GetValue(s.Field, null) as string) ?? "";

        private static bool IsFocused(FieldState s) =>
            s.Label.gameObject.activeInHierarchy
            && (s.Kind == Engine.UGui ? s.UField.isFocused : (bool)Tmp.FieldIsFocused.GetValue(s.Field, null));

        private static bool IsMultiLine(FieldState s) =>
            s.Kind == Engine.UGui ? s.UField.multiLine : Tmp.FieldMultiLine != null && (bool)Tmp.FieldMultiLine.GetValue(s.Field, null);

        private static float BlinkRate(FieldState s) =>
            s.Kind == Engine.UGui ? s.UField.caretBlinkRate : Convert.ToSingle(Tmp.FieldBlinkRate.GetValue(s.Field, null));

        private static float CaretWidth(FieldState s) =>
            s.Kind == Engine.UGui ? s.UField.caretWidth : Convert.ToSingle(Tmp.FieldCaretWidth.GetValue(s.Field, null));

        /// <summary>The fixed end of the selection, relative to what the label shows.</summary>
        private static int Anchor(FieldState s) =>
            s.Kind == Engine.UGui ? s.UField.selectionAnchorPosition - s.DrawStart
                                  : Convert.ToInt32(Tmp.FieldStringAnchor.GetValue(s.Field, null));

        /// <summary>The moving end — where the caret is.</summary>
        private static int Focus(FieldState s) =>
            s.Kind == Engine.UGui ? s.UField.selectionFocusPosition - s.DrawStart
                                  : Convert.ToInt32(Tmp.FieldStringFocus.GetValue(s.Field, null));

        private static void SetCaret(FieldState s, int relative)
        {
            if (s.Kind == Engine.UGui) s.UField.caretPosition = relative + s.DrawStart;
            else Tmp.FieldStringPosition.SetValue(s.Field, relative, null);
        }

        private static void SetFocus(FieldState s, int relative)
        {
            if (s.Kind == Engine.UGui) s.UField.selectionFocusPosition = relative + s.DrawStart;
            else Tmp.FieldStringFocus.SetValue(s.Field, relative, null);
        }

        private static Color GetColor(FieldState s, string name) =>
            s.Kind == Engine.UGui
                ? (name == "caretColor" ? s.UField.caretColor : s.UField.selectionColor)
                : (Color)Tmp.FieldProp(name).GetValue(s.Field, null);

        private static void SetColor(FieldState s, string name, Color value)
        {
            if (s.Kind == Engine.UGui)
            {
                if (name == "caretColor") s.UField.caretColor = value;
                else s.UField.selectionColor = value;
            }
            else Tmp.FieldProp(name).SetValue(s.Field, value, null);
        }

        private static bool GetBool(FieldState s, string name) =>
            s.Kind == Engine.UGui ? s.UField.customCaretColor : (bool)Tmp.FieldProp(name).GetValue(s.Field, null);

        private static void SetBool(FieldState s, string name, bool value)
        {
            if (s.Kind == Engine.UGui) s.UField.customCaretColor = value;
            else Tmp.FieldProp(name).SetValue(s.Field, value, null);
        }

        // m_DrawStart: where uGUI's visible slice starts. Protected field on Mono, a property of
        // the same name in an IL2CPP interop assembly.
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

        private static void Note(string message)
        {
            if (!DiagnosticOnce.First("RtlInputFields.note", message)) return;
            TranslatorCore.LogWarning("[RtlInputFields] " + message);
        }

        // ══ TMP by reflection: the Core names no TMP type (it may not be in the game) ═══════
        // The text's layout is TmpLayout's; what is the input field's own is here.

        private static class Tmp
        {
            private static bool _resolved, _ok;
            internal static PropertyInfo FieldText, FieldIsFocused, FieldBlinkRate, FieldCaretWidth, FieldMultiLine;
            internal static PropertyInfo FieldStringPosition, FieldStringAnchor, FieldStringFocus;
            internal static PropertyInfo LabelText;
            private static readonly Dictionary<string, PropertyInfo> _fieldProps = new Dictionary<string, PropertyInfo>();

            internal static bool Resolve()
            {
                if (_resolved) return _ok;
                _resolved = true;
                try
                {
                    var field = TypeHelper.TMP_InputFieldType;
                    var text = TypeHelper.TMP_TextType;
                    if (field == null || text == null || !TmpLayout.Resolve()) return _ok = false;
                    const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;

                    FieldText = field.GetProperty("text", pub);
                    FieldIsFocused = field.GetProperty("isFocused", pub);
                    FieldBlinkRate = field.GetProperty("caretBlinkRate", pub);
                    FieldCaretWidth = field.GetProperty("caretWidth", pub);
                    FieldMultiLine = field.GetProperty("multiLine", pub);   // Up / Down only: absent, Unity's own
                    FieldStringPosition = field.GetProperty("stringPosition", pub);
                    FieldStringAnchor = field.GetProperty("selectionStringAnchorPosition", pub);
                    FieldStringFocus = field.GetProperty("selectionStringFocusPosition", pub);
                    LabelText = text.GetProperty("text", pub);

                    _ok = FieldText != null && FieldIsFocused != null && FieldStringPosition != null
                          && FieldStringAnchor != null && FieldStringFocus != null && LabelText != null
                          && TmpLayout.CiStringLength != null && TmpLayout.LiFirst != null
                          && TmpLayout.LiAscender != null && TmpLayout.LiDescender != null
                          && FieldProp("caretColor") != null && FieldProp("selectionColor") != null
                          && FieldProp("customCaretColor") != null;
                    if (!_ok) Note("TMP input fields: a member this needs is missing — left to TMP's own drawing");
                }
                catch (Exception ex)
                {
                    _ok = false;
                    Note("TMP input fields unavailable: " + ex.Message);
                }
                return _ok;
            }

            internal static PropertyInfo FieldProp(string name)
            {
                if (_fieldProps.TryGetValue(name, out var p)) return p;
                p = TypeHelper.TMP_InputFieldType?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                _fieldProps[name] = p;
                return p;
            }
        }
    }
}
