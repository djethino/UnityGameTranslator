using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Font replacement for 2D Toolkit's tk2dTextMesh — old games (user, 2026-10-01: « c'est pour le
    /// support des vieux jeux »). tk2d draws a text from a tk2dFontData: one entry per character (a quad
    /// and its place in the font's atlas, an advance), the atlas in the font's material. A bitmap font
    /// made for a game holds only that game's characters, so a translation into another script shows
    /// nothing. The replacement is such a font data made at run time from the dynamic Unity Font the mod
    /// makes for this game font (FontManager.GetUnityReplacementFont — the font chosen in the Fonts tab,
    /// its derived copy included): its atlas is that Font's texture, its entries are read from
    /// Font.GetCharacterInfo at the game font's own pixel size and scaled to its line height.
    ///
    /// Read in the real tk2d (decompiled from test games, 2026-10-01 — analyse/ecritures-complexes-etat-reel.md):
    /// a missing character draws entry 0, which must exist; p0/p1 are the quad's top-left and
    /// bottom-right, y up, measured from the BOTTOM of the line; uv0 goes with p0 unless the entry is
    /// flipped; the text mesh takes a font through its `font` property and draws again on ForceBuild.
    ///
    /// Entries are added as texts need them, and every entry is read again when Unity rebuilds the
    /// Font's atlas (Font.textureRebuilt), every text drawn with it built again then.
    /// ⚠ Mono only: the atlas event is a C# event IL2CPP would take as one of its own proxies.
    /// Main thread.
    /// </summary>
    internal static class Tk2dFonts
    {
        private sealed class Replacement
        {
            public object Original;                 // the game's tk2dFontData
            public string OriginalName;
            public Font Font;                       // the dynamic font it is drawn from
            public Component Data;                  // our tk2dFontData
            public IDictionary CharDict;            // Dictionary<int, tk2dFontChar>
            public int PixelSize;
            public float Scale;                     // world units per pixel at PixelSize
            public float LineHeightPx, AscentPx;
            public readonly HashSet<int> Known = new HashSet<int>();
            public readonly List<WeakReference> Users = new List<WeakReference>();
        }

        private static Type _dataType, _charType;
        private static readonly Dictionary<object, Replacement> _byOriginal = new Dictionary<object, Replacement>();
        private static readonly Dictionary<object, Replacement> _byData = new Dictionary<object, Replacement>();
        private static bool _rebuiltHooked, _il2cppSaid;

        /// <summary>The game font a tk2d font stands for: itself, or the one a replacement of ours was made for.</summary>
        internal static object OriginalOf(object fontData)
            => fontData != null && _byData.TryGetValue(fontData, out var r) ? r.Original : fontData;

        /// <summary>The name a tk2d font is known by in the font settings — the game font's, never ours.</summary>
        internal static string OriginalNameOf(object fontData)
            => fontData != null && _byData.TryGetValue(fontData, out var r) ? r.OriginalName : (fontData as UnityEngine.Object)?.name;

        /// <summary>Whether this tk2d text draws from a replacement of ours — a dynamic font, by codepoint, like uGUI Text.</summary>
        internal static bool DrawsReplacement(object textMesh)
        {
            var font = FontOf(textMesh);
            return font != null && _byData.ContainsKey(font);
        }

        /// <summary>
        /// Gives a tk2d text the replacement of its game font when one is set and it can draw the text —
        /// the rule TryApplyUnityClone applies to uGUI Text — and its game font back when none is set.
        /// </summary>
        internal static void Apply(object textMesh, string settingsFontName, string text)
        {
            if (textMesh == null || string.IsNullOrEmpty(settingsFontName) || !TypeHelper.IsInScene(textMesh)) return;
            var current = FontOf(textMesh);
            if (current == null) return;
            var original = OriginalOf(current);

            var font = FontManager.GetUnityReplacementFont(settingsFontName);
            if (font == null)
            {
                if (!ReferenceEquals(current, original)) SetFont(textMesh, original);   // the setting was left
                return;
            }
            if (TranslatorCore.Adapter?.IsIL2CPP ?? false)
            {
                if (!_il2cppSaid)
                {
                    _il2cppSaid = true;
                    TranslatorCore.LogInfo("[Tk2dFonts] tk2d text keeps its game font on IL2CPP: its atlas event cannot be followed there");
                }
                return;
            }
            if (FontManager.CloneCoverageProbeAvailable && !FontManager.CloneCoversText(settingsFontName, font, text)) return;

            var replacement = For(original, settingsFontName, font);
            if (replacement == null) return;
            Ensure(replacement, text);
            if (!ReferenceEquals(current, replacement.Data)) SetFont(textMesh, replacement.Data);
            Track(replacement, textMesh);
        }

        /// <summary>The characters of a presented text (shaped glyphs included) added to the replacement it draws from, if it does.</summary>
        internal static void EnsureDrawn(object textMesh, string text)
        {
            if (textMesh == null || string.IsNullOrEmpty(text)) return;
            var font = FontOf(textMesh);
            if (font != null && _byData.TryGetValue(font, out var replacement)) Ensure(replacement, text);
        }

        private static object FontOf(object textMesh)
        {
            var prop = textMesh.GetType().GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return null;
            try { return prop.GetValue(textMesh, null); }
            catch (Exception ex) { Faults.Say("Tk2dFonts.FontOf", ex); return null; }
        }

        private static void SetFont(object textMesh, object fontData)
        {
            var prop = textMesh.GetType().GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
            try { prop?.SetValue(textMesh, fontData, null); }
            catch (Exception ex) { Faults.Say("Tk2dFonts.SetFont", ex); }
        }

        /// <summary>The replacement of a game font drawn from this Font — made once, made again when the Font changed (a derived copy rewritten).</summary>
        private static Replacement For(object original, string originalName, Font font)
        {
            // A Font made again (a derived copy rewritten): a new replacement. The previous one stays —
            // texts still drawn with it keep their glyphs until they are written again.
            if (_byOriginal.TryGetValue(original, out var known) && known.Font == font) return known;
            if (!Resolve()) return null;

            float lineHeight = FloatField(original, "lineHeight");
            var texel = FieldValue(original, "texelSize") is Vector2 v ? v : Vector2.zero;
            if (lineHeight <= 0f)
            {
                TranslatorCore.LogWarning($"[Tk2dFonts] {originalName}: the game font has no line height — kept");
                return null;
            }
            // The game font's own pixel size: its line height in texels. Without texels (fonts older
            // than tk2d's version 2), the Font's own size.
            int pixelSize = texel.y > 0f ? Mathf.Max(1, Mathf.RoundToInt(lineHeight / texel.y)) : font.fontSize;
            float scale = font.fontSize > 0 ? (float)pixelSize / font.fontSize : 1f;
            float lineHeightPx = font.lineHeight * scale, ascentPx = font.ascent * scale;
            if (lineHeightPx <= 0f) lineHeightPx = pixelSize;

            var go = new GameObject((original as UnityEngine.Object)?.name ?? originalName);
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            var data = go.AddComponent(_dataType);

            var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(int), _charType));
            var replacement = new Replacement
            {
                Original = original, OriginalName = originalName, Font = font, Data = data, CharDict = dict,
                PixelSize = pixelSize, Scale = lineHeight / lineHeightPx, LineHeightPx = lineHeightPx, AscentPx = ascentPx,
            };
            Set(data, "version", FieldValue(original, "version") ?? 2);
            Set(data, "lineHeight", lineHeight);
            Set(data, "useDictionary", true);
            Set(data, "charDict", dict);
            Set(data, "chars", Array.CreateInstance(_charType, 0));
            Set(data, "kerning", Array.CreateInstance(_dataType.GetField("kerning")?.FieldType.GetElementType() ?? typeof(object), 0));
            Set(data, "material", font.material);
            Set(data, "needMaterialInstance", false);
            Set(data, "isPacked", false);
            Set(data, "textureGradients", false);
            Set(data, "premultipliedAlpha", false);
            Set(data, "texelSize", new Vector2(replacement.Scale, replacement.Scale));
            dict[0] = NewChar(Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, false, 0f);   // what a missing character draws
            replacement.Known.Add(0);

            HookRebuilt();
            _byOriginal[original] = replacement;
            _byData[data] = replacement;
            TranslatorCore.LogInfo($"[Tk2dFonts] {originalName}: replaced by '{font.name}' ({pixelSize} px, line height {lineHeight})");
            return replacement;
        }

        /// <summary>Entries for every character of the text not known yet, read from the Font's atlas.</summary>
        private static void Ensure(Replacement replacement, string text)
        {
            bool added = false;
            foreach (char c in text)
                if (!replacement.Known.Contains(c)) { added = true; break; }
            if (!added) return;
            replacement.Font.RequestCharactersInTexture(text, replacement.PixelSize, FontStyle.Normal);
            foreach (char c in text)
            {
                if (replacement.Known.Contains(c)) continue;
                replacement.Known.Add(c);
                Fill(replacement, c);
            }
        }

        /// <summary>One character's entry from the atlas as it is now.</summary>
        private static void Fill(Replacement r, int c)
        {
            if (c == 0) return;
            if (!r.Font.GetCharacterInfo((char)c, out var ci, r.PixelSize, FontStyle.Normal))
            {
                r.CharDict.Remove(c);   // not in the font: entry 0 is drawn, as tk2d does
                return;
            }
            float s = r.Scale, below = r.LineHeightPx - r.AscentPx;   // the baseline, from the line's bottom
            var p0 = new Vector3(ci.minX * s, (below + ci.maxY) * s, 0f);
            var p1 = new Vector3(ci.maxX * s, (below + ci.minY) * s, 0f);
            // tk2d puts uv0 at p0 (the top-left corner) unless the entry is flipped, then uv1 there.
            Vector2 topLeft = ci.uvTopLeft, bottomRight = ci.uvBottomRight, bottomLeft = ci.uvBottomLeft;
            bool flipped = !Mathf.Approximately(topLeft.x, bottomLeft.x);
            var uv0 = flipped ? (Vector3)bottomRight : (Vector3)topLeft;
            var uv1 = flipped ? (Vector3)topLeft : (Vector3)bottomRight;
            r.CharDict[c] = NewChar(p0, p1, uv0, uv1, flipped, ci.advance * s);
        }

        private static object NewChar(Vector3 p0, Vector3 p1, Vector3 uv0, Vector3 uv1, bool flipped, float advance)
        {
            var ch = Activator.CreateInstance(_charType);
            Set(ch, "p0", p0); Set(ch, "p1", p1); Set(ch, "uv0", uv0); Set(ch, "uv1", uv1);
            Set(ch, "flipped", flipped); Set(ch, "advance", advance); Set(ch, "channel", 0);
            Set(ch, "gradientUv", new Vector2[4]);
            return ch;
        }

        private static void Track(Replacement replacement, object textMesh)
        {
            foreach (var w in replacement.Users) if (ReferenceEquals(w.Target, textMesh)) return;
            replacement.Users.RemoveAll(w => !(w.Target is UnityEngine.Object o) || o == null);
            replacement.Users.Add(new WeakReference(textMesh));
        }

        // ── the atlas moved: every entry read again, every text built again ──

        private static void HookRebuilt()
        {
            if (_rebuiltHooked) return;
            _rebuiltHooked = true;
            Font.textureRebuilt += OnTextureRebuilt;
        }

        private static bool _inRebuilt;

        /// <summary>
        /// Unity rebuilt the Font's atlas: it keeps only what is asked while it rebuilds, so every
        /// character of ours is asked again — as uGUI Text does from the same event — then read again,
        /// and every text drawn with it built again. Asking may rebuild the atlas once more: that
        /// nested call is left to this one, which reads the atlas after.
        /// </summary>
        private static void OnTextureRebuilt(Font font)
        {
            if (_inRebuilt) return;
            _inRebuilt = true;
            try
            {
                foreach (var r in _byData.Values)
                {
                    if (r.Font != font) continue;
                    var known = new System.Text.StringBuilder(r.Known.Count);
                    foreach (int c in r.Known) if (c != 0) known.Append((char)c);
                    if (known.Length > 0) font.RequestCharactersInTexture(known.ToString(), r.PixelSize, FontStyle.Normal);
                }
                foreach (var r in _byData.Values)
                {
                    if (r.Font != font) continue;
                    foreach (int c in r.Known) Fill(r, c);
                    foreach (var w in r.Users)
                        if (w.Target is UnityEngine.Object o && o != null) TypeHelper.InvokeNoArg(o, "ForceBuild");
                }
            }
            // Called by the engine: a failure here is said, never thrown back into its atlas code.
            catch (Exception ex) { Faults.Say("Tk2dFonts.OnTextureRebuilt", ex, font != null ? font.name : null); }
            finally { _inRebuilt = false; }
        }

        // ── reflection over tk2d's own types ──

        private static bool Resolve()
        {
            if (_dataType != null && _charType != null) return true;
            _dataType = AssemblyTypes.Find("tk2dFontData");
            _charType = AssemblyTypes.Find("tk2dFontChar");
            if (_dataType == null || _charType == null)
            {
                TranslatorCore.LogWarning("[Tk2dFonts] tk2dFontData / tk2dFontChar not found in this game — tk2d text keeps its fonts");
                return false;
            }
            return true;
        }

        private static object FieldValue(object target, string name)
        {
            var f = target?.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(target);
        }

        private static float FloatField(object target, string name) => FieldValue(target, name) is float f ? f : 0f;

        private static void Set(object target, string name, object value)
        {
            var f = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return;   // a tk2d version without this field: the field it would set does not exist there either
            f.SetValue(target, value);
        }
    }
}
