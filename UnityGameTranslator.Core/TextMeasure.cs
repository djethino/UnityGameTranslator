using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The width of one line of text as a component draws it — the Unity side of
    /// <see cref="ITextRouterHost.MeasureLine"/>. Only ever compared with other widths of the
    /// same component (the lines the game kept and refused when it laid out its own text), so
    /// the unit does not matter, only that it follows what is drawn.
    ///
    /// ⚠ Each engine call sits in a method of its own, and the try is in the CALLER: IL2CPP
    /// resolves a stripped method when it compiles the method that NAMES it, so a guard written
    /// inside would never run (analyse/pieges-projet.md §9). A component whose measure fails is
    /// said once and measured no more — the router then leaves the game's layout as it is.
    /// </summary>
    internal static class TextMeasure
    {
        private static readonly HashSet<string> _failedKinds = new HashSet<string>();
        private static MethodInfo _tmpPreferred;

        /// <param name="atSize">
        /// The font size to measure at; null for the size the component draws at now. A bitmap
        /// font rasterised per size (TextMesh) is measured AT that size; the others scale with it.
        /// </param>
        public static float? Measure(object component, string line, float? atSize)
        {
            if (component == null || line == null) return null;
            string kind = component.GetType().Name;
            if (_failedKinds.Contains(kind)) return null;
            try
            {
                var textMesh = TypeHelper.Il2CppCast(component, typeof(TextMesh)) as TextMesh;
                if (textMesh != null) return MeasureTextMesh(textMesh, line, atSize);

                var uiText = TypeHelper.Il2CppCast(component, typeof(UnityEngine.UI.Text)) as UnityEngine.UI.Text;
                if (uiText != null) return MeasureUiText(uiText, line, atSize);

                float? now = null;
                if (TypeHelper.TMP_TextType != null && TypeHelper.TMP_TextType.IsInstanceOfType(component))
                    now = MeasureTmp(component, line);
                if (now == null || atSize == null) return now;
                float current = TypeHelper.GetFontSize(component);
                return current > 0f ? now.Value * atSize.Value / current : (float?)null;
            }
            catch (Exception ex)
            {
                _failedKinds.Add(kind);
                TranslatorCore.LogWarning($"[TextMeasure] {kind} cannot measure its text here ({ex.GetType().Name}: {ex.Message}) — a translation arriving after the game laid out its source stays unwrapped");
            }
            return null;
        }

        private static float? MeasureTextMesh(TextMesh textMesh, string line, float? atSize)
            => MeasureWithFont(textMesh.font, line, atSize.HasValue ? (int)Math.Round(atSize.Value) : textMesh.fontSize, textMesh.fontStyle);

        // ⚠ Not through the text generator (GetGenerationSettings + GetPreferredWidth): its
        // settings are a value type, which the Core — compiled once against Mono — cannot hand to
        // IL2CPP ("TypeLoadException … value type mismatch", measured 2026-09-27). The advances
        // are what the generator lays a dynamic font's line out with, on both runtimes.
        private static float? MeasureUiText(UnityEngine.UI.Text text, string line, float? atSize)
            => MeasureWithFont(text.font, line, atSize.HasValue ? (int)Math.Round(atSize.Value) : text.fontSize, text.fontStyle);

        // Sum of the advances, as the font draws each character at that size and style.
        private static float? MeasureWithFont(Font font, string line, int size, FontStyle style)
        {
            if (font == null) return null;
            font.RequestCharactersInTexture(line, size, style);
            float width = 0f;
            for (int i = 0; i < line.Length; i++)
            {
                if (!font.GetCharacterInfo(line[i], out CharacterInfo info, size, style)) return null;
                width += info.advance;
            }
            return width;
        }

        private static float? MeasureTmp(object tmp, string line)
        {
            if (_tmpPreferred == null)
                _tmpPreferred = TypeHelper.TMP_TextType.GetMethod("GetPreferredValues", new[] { typeof(string) });
            if (_tmpPreferred == null) return null;
            var size = _tmpPreferred.Invoke(tmp, new object[] { line });
            return size is Vector2 v ? v.x : (float?)null;
        }
    }
}
