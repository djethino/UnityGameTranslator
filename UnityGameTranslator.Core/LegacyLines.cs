using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// 🔴 **The game's lines kept in the engine's two older texts** (UI.Text and TextMesh) — the
    /// counterpart of FontManager.HeadLines for TextMesh Pro (FontSettings.keep_lines).
    ///
    /// There a font's line heights are fields the mod writes in a copy of the font. Here they come
    /// from the font FILE and nothing can write them: a replacement font brings its own (a menu
    /// step of 75 became 123 with a tall one, bench tmphide 2026-10-13), and a size put on a font
    /// closes its lines up. So the lines are kept on the COMPONENT:
    ///   - the step, by its line spacing: the game's, times what the game font's line is to the
    ///     worn font's at the sizes they are drawn at;
    ///   - where the block sits, on UI.Text, by moving its letters when it builds its mesh
    ///     (OnPopulateMesh): the engine anchors a block by its first line (upper), its last
    ///     (lower) or its middle whatever the spacing, so one line of each font tells the move.
    ///     TextMesh builds its mesh in the engine: its block stays where the worn font puts it.
    /// Both are measured by the engine itself (RtlPresenter.MeasureLegacyLines), once per pair of
    /// fonts and sizes — a Font's own lineHeight is a whole number at its nominal size, 8 % off.
    ///
    /// ⚠ The line spacing is the game's to write too: what the component holds is compared with
    /// what the mod wrote, and anything else is the game's new value (the rule of the alignment,
    /// RtlPresenter.MirrorAlignment). A text sized to its box (Best Fit) is left alone: the size
    /// the engine picks there is not one the game would have picked.
    /// </summary>
    internal static class LegacyLines
    {
        private sealed class Kept
        {
            /// <summary>The game's line spacing, and what the mod put in its place.</summary>
            public float Original, Written;
            public bool Wrote;
            /// <summary>How far down (negative) or up the letters of a UI.Text are moved, in its own units.</summary>
            public float Shift;
        }

        private static readonly Dictionary<int, Kept> _kept = ForgetByNumber.Tracked(new Dictionary<int, Kept>());
        /// <summary>Per pair of fonts and sizes: the game's line over the worn one's, and the move of the block.</summary>
        private static readonly Dictionary<string, float[]> _measures = new Dictionary<string, float[]>(StringComparer.Ordinal);
        private const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>
        /// After the mod gave a component its font and its size: its lines put back where the
        /// game's font at the game's size has them, or given back to the game when the setting
        /// is off or nothing differs.
        /// </summary>
        internal static void Apply(object instance, string fontName)
        {
            if (instance == null || string.IsNullOrEmpty(fontName)) return;
            try
            {
                var type = instance.GetType();
                bool uiText = TypeHelper.UI_TextType != null && TypeHelper.UI_TextType.IsAssignableFrom(type);
                bool textMesh = !uiText && TypeHelper.TextMeshType != null && TypeHelper.TextMeshType.IsAssignableFrom(type);
                if (!uiText && !textMesh) return;
                int id = TypeHelper.GetInstanceID(instance);
                if (id == -1) return;
                var spacing = type.GetProperty("lineSpacing", Pub);
                if (spacing == null || !spacing.CanWrite) return;

                // A template's lines stay the game's: every copy starts from them (TypeHelper.IsInScene).
                bool keep = FontManager.KeepsLines(fontName) && TypeHelper.IsInScene(instance) && !BestFit(instance, type);
                object worn = keep ? TypeHelper.GetFont(instance) : null;
                object game = keep ? (FontManager.OriginalFontOf(id) ?? worn) : null;
                int wornSize = 0, gameSize = 0;
                if (keep && TypeHelper.IsUnityObjectAlive(worn) && TypeHelper.IsUnityObjectAlive(game))
                {
                    float held = TypeHelper.GetFontSize(instance);
                    wornSize = SizeOr(worn, held);
                    gameSize = SizeOr(game, TranslatorPatches.TrueOriginalFontSizes.TryGetValue(id, out float first) ? first : held);
                }
                bool same = keep && worn is UnityEngine.Object w && game is UnityEngine.Object g && w == g && wornSize == gameSize;
                if (!keep || wornSize < 1 || gameSize < 1 || same) { Restore(instance, id, spacing); return; }

                int row = uiText || textMesh ? AnchorRow(instance, type) : 0;
                float unit = uiText ? PixelsPerUnit(instance, type) : 1f;
                string key = ObjectNumbers.Of((UnityEngine.Object)game) + "|" + gameSize + "|" + ObjectNumbers.Of((UnityEngine.Object)worn) + "|" + wornSize
                             + "|" + (uiText ? "u" : "m") + row + "|" + unit.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                if (!_measures.TryGetValue(key, out var measure))
                {
                    measure = null;
                    object like = uiText ? instance : null;
                    if (RtlPresenter.MeasureLegacyLines(like, game, gameSize, out float gameFoot, out float gameStep)
                        && RtlPresenter.MeasureLegacyLines(like, worn, wornSize, out float wornFoot, out float wornStep))
                        measure = new[] { gameStep / wornStep, gameFoot - wornFoot };
                    else if (DiagnosticOnce.First("LegacyLines.unmeasured", TypeHelper.FontNameOf(game) + "|" + TypeHelper.FontNameOf(worn)))
                        TranslatorCore.LogWarning($"[LegacyLines] the lines of '{TypeHelper.FontNameOf(game)}' and '{TypeHelper.FontNameOf(worn)}' cannot be measured on this engine ({RtlPresenter.MeasureWhy}): texts of '{fontName}' keep the lines of the font that draws them");
                    _measures[key] = measure;
                }
                if (measure == null) { Restore(instance, id, spacing); return; }

                float current = Convert.ToSingle(spacing.GetValue(instance, null));
                if (!_kept.TryGetValue(id, out var kept)) _kept[id] = kept = new Kept { Original = current };
                else if (Math.Abs(current - (kept.Wrote ? kept.Written : kept.Original)) > 0.0005f)
                {
                    // Not what the mod left there: the game wrote it since, and it is the game's now.
                    kept.Original = current;
                    kept.Wrote = false;
                }
                float shift = uiText ? measure[1] : 0f;
                bool moved = Math.Abs(kept.Shift - shift) > 0.01f;
                kept.Shift = shift;
                float target = kept.Original * measure[0];
                if (Math.Abs(current - target) > 0.0005f)
                {
                    spacing.SetValue(instance, target, null);
                    kept.Written = target;
                    kept.Wrote = true;
                    if (DiagnosticOnce.First("LegacyLines.kept", fontName + "|" + type.Name))
                        TranslatorCore.LogDebug($"[LegacyLines] '{fontName}' ({type.Name}): the game's lines kept — line spacing {kept.Original:F3} -> {target:F3}, letters moved by {shift:F2}");
                }
                else if (moved && uiText) Redraw(instance, type);
            }
            // The component then keeps the lines of the font that draws it — said.
            catch (Exception ex) { Faults.Say("LegacyLines.Apply", ex.InnerException ?? ex, fontName); }
        }

        /// <summary>A component the mod no longer sizes or dresses: its line spacing given back, its letters where the engine puts them.</summary>
        internal static void Restore(object instance)
        {
            if (instance == null || _kept.Count == 0) return;
            int id = TypeHelper.GetInstanceID(instance);
            if (id == -1 || !_kept.ContainsKey(id)) return;
            try { Restore(instance, id, instance.GetType().GetProperty("lineSpacing", Pub)); }
            catch (Exception ex) { Faults.Say("LegacyLines.Restore", ex.InnerException ?? ex); }
        }

        private static void Restore(object instance, int id, PropertyInfo spacing)
        {
            if (!_kept.TryGetValue(id, out var kept)) return;
            _kept.Remove(id);
            if (spacing == null) return;
            // What the game wrote since the mod's own write is the game's: nothing to give back.
            bool ours = kept.Wrote && Math.Abs(Convert.ToSingle(spacing.GetValue(instance, null)) - kept.Written) <= 0.0005f;
            if (ours) spacing.SetValue(instance, kept.Original, null);
            else if (Math.Abs(kept.Shift) > 0.01f) Redraw(instance, instance.GetType());
        }

        /// <summary>UI.Text built its mesh (OnPopulateMesh): its letters moved to where the game's font has them.</summary>
        internal static void Shift(object instance, object helper)
        {
            if (_kept.Count == 0 || instance == null) return;
            if (!_kept.TryGetValue(TypeHelper.GetInstanceID(instance), out var kept) || Math.Abs(kept.Shift) < 0.01f) return;
            if (!(helper is VertexHelper vertices))
            {
                if (DiagnosticOnce.First("LegacyLines.helper", helper?.GetType().FullName ?? "null"))
                    TranslatorCore.LogWarning($"[LegacyLines] UI.Text hands its mesh over as {helper?.GetType().FullName ?? "nothing"}: its letters are not moved, the block sits where the font that draws it puts it");
                return;
            }
            var vertex = new UIVertex();
            int count = vertices.currentVertCount;
            for (int i = 0; i < count; i++)
            {
                vertices.PopulateUIVertex(ref vertex, i);
                var position = vertex.position;
                position.y += kept.Shift;
                vertex.position = position;
                vertices.SetUIVertex(vertex, i);
            }
        }

        /// <summary>The size a text is drawn at: its own, or — 0 on a TextMesh — its font's.</summary>
        private static int SizeOr(object font, float held)
        {
            if (held >= 1f) return (int)Math.Round(held);
            var own = font.GetType().GetProperty("fontSize", Pub)?.GetValue(font, null);
            return own != null ? Convert.ToInt32(own) : 0;
        }

        private static bool BestFit(object instance, Type type)
        {
            var bestFit = type.GetProperty("resizeTextForBestFit", Pub);
            return bestFit != null && (bool)bestFit.GetValue(instance, null);
        }

        /// <summary>0 upper, 1 middle, 2 lower (TextAnchor's rows, on UI.Text's alignment and TextMesh's anchor).</summary>
        private static int AnchorRow(object instance, Type type)
        {
            var anchor = type.GetProperty("alignment", Pub) ?? type.GetProperty("anchor", Pub);
            object value = anchor?.GetValue(instance, null);
            int index = value != null ? Convert.ToInt32(value) : 0;
            return index >= 0 && index <= 8 ? index / 3 : 0;
        }

        private static float PixelsPerUnit(object instance, Type type)
        {
            object value = type.GetProperty("pixelsPerUnit", Pub)?.GetValue(instance, null);
            float unit = value != null ? Convert.ToSingle(value) : 1f;
            return unit > 0f ? unit : 1f;
        }

        private static void Redraw(object instance, Type type) =>
            type.GetMethod("SetVerticesDirty", Pub, null, Type.EmptyTypes, null)?.Invoke(instance, null);
    }
}
