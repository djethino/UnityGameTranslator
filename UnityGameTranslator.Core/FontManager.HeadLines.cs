using System;
using System.Reflection;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    public static partial class FontManager
    {
        /// <summary>
        /// 🔴 **A head measures its lines as the GAME's font does** (issue #33, follow-up of
        /// 2026-10-12). TMP takes a line's heights from the font that owns its letters; what no
        /// letter sets — an empty line, a line feed — it takes from the font the text wears, the
        /// head (BackupHead), which holds no letter. Left with the replacement's heights, a menu
        /// written as one text ("item, empty line, item"), every letter of it the game's, spread
        /// away from the pointer the game moves by a fixed step (bench `tmphide`: 14.54 → 19.43;
        /// the Laboratory terminal of Blue Prince).
        /// The game's ascent, descent, line height, underline and strikethrough are written into
        /// the head, in the head's units. A line that holds letters keeps the heights of the font
        /// that draws them, as before.
        /// Off: the replacement's own heights, as every head had.
        /// </summary>
        internal static bool HeadKeepsGameLines = true;

        private static readonly string[] GameLineLengths =
        {
            "ascentLine", "capLine", "meanLine", "baseline", "descentLine", "lineHeight",
            "underlineOffset", "underlineThickness", "strikethroughOffset", "strikethroughThickness",
            "superscriptOffset", "subscriptOffset", "tabWidth",
        };

        private static void GiveGameLines(object head, object gameFont, Type type, string name)
        {
            try
            {
                var game = TypeHelper.Il2CppCast(gameFont, type) ?? gameFont;
                if (!CustomFontLoader.TryGetModernFaceInfo(head, out float headPoint, out float headFace) || headPoint <= 0f) return;
                if (!CustomFontLoader.TryGetModernFaceInfo(game, out float gamePoint, out float gameFace) || gamePoint <= 0f) return;
                if (headFace <= 0f) headFace = 1f;
                if (gameFace <= 0f) gameFace = 1f;
                // A length is drawn at fontSize ÷ point size × face scale, in each asset's own terms:
                // equal on both sides once the game's is written in the head's units.
                double factor = (headPoint * gameFace) / (gamePoint * headFace);

                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var property = type.GetProperty("faceInfo", BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return;
                object own = property.GetValue(head, null), theirs = property.GetValue(game, null);
                if (own == null || theirs == null) return;
                var face = own.GetType();
                int written = 0;
                foreach (string length in GameLineLengths)
                {
                    // A property on Mono; on IL2CPP the struct may show its field alone (m_AscentLine).
                    string field = "m_" + char.ToUpperInvariant(length[0]) + length.Substring(1);
                    var p = face.GetProperty(length, any);
                    var f = p == null ? (face.GetField(field, any) ?? face.GetField(length, any)) : null;
                    object value = p != null ? p.GetValue(theirs, null) : f?.GetValue(theirs);
                    if (value == null) continue;
                    float scaled = (float)(Convert.ToDouble(value) * factor);
                    if (p != null && p.CanWrite) p.SetValue(own, scaled, null);
                    else if (f != null) f.SetValue(own, scaled);
                    else continue;
                    written++;
                }
                property.SetValue(head, own, null);
                TranslatorCore.LogDebug($"[FontManager] '{name}': lines laid out as the game font's ({written} length(s) taken)");
            }
            // The head keeps the replacement's heights: a menu's empty lines are the replacement's — said.
            catch (Exception ex) { Faults.Say("FontManager.GiveGameLines", ex.InnerException ?? ex, name); }
        }
    }
}
