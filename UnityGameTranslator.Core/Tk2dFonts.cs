using System;
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
    /// Font's atlas (Font.textureRebuilt, subscribed through EngineEvents), every text drawn with it
    /// built again then.
    /// Both runtimes. Under IL2CPP tk2d's types are interop proxies: their fields are PROPERTIES (a
    /// field lookup finds nothing and a write does nothing, silently — hence Member below), its
    /// dictionary and arrays are Il2Cpp collections made in the member's own type, and a wrapper read
    /// twice is two objects — every object here is known by its instance id, never by reference.
    /// Main thread.
    /// </summary>
    internal static class Tk2dFonts
    {
        private sealed class Replacement
        {
            public object Original;                 // the game's tk2dFontData
            public int OriginalId;
            public string OriginalName;
            public Font Font;                       // the dynamic font it is drawn from
            public int FontId;
            public Component Data;                  // our tk2dFontData
            public object CharDict;                 // Dictionary<int, tk2dFontChar> (Il2Cpp's under IL2CPP)
            public MethodInfo DictSet, DictRemove;
            public int PixelSize;
            public float Scale;                     // world units per pixel at PixelSize
            public float LineHeightPx, AscentPx;
            public readonly HashSet<int> Known = new HashSet<int>();
            public readonly Dictionary<int, object> Users = new Dictionary<int, object>();   // by instance id
        }

        private static Type _dataType, _charType;
        private static readonly Dictionary<int, Replacement> _byOriginal = new Dictionary<int, Replacement>();
        private static readonly Dictionary<int, Replacement> _byData = new Dictionary<int, Replacement>();
        private static bool _rebuiltHooked;
        private static object _rebuiltHandler;

        private static int IdOf(object unityObject) => unityObject == null ? -1 : TypeHelper.GetInstanceID(unityObject);

        private static bool Ours(object fontData, out Replacement replacement)
        {
            replacement = null;
            int id = IdOf(fontData);
            return id != -1 && _byData.TryGetValue(id, out replacement);
        }

        /// <summary>The game font a tk2d font stands for: itself, or the one a replacement of ours was made for.</summary>
        internal static object OriginalOf(object fontData)
            => Ours(fontData, out var r) ? r.Original : fontData;

        /// <summary>The name a tk2d font is known by in the font settings — the game font's, never ours.</summary>
        internal static string OriginalNameOf(object fontData)
            => Ours(fontData, out var r) ? r.OriginalName : (fontData as UnityEngine.Object)?.name;

        /// <summary>Whether this tk2d text draws from a replacement of ours — a dynamic font, by codepoint, like uGUI Text.</summary>
        internal static bool DrawsReplacement(object textMesh) => Ours(FontOf(textMesh), out _);

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
                if (IdOf(current) != IdOf(original)) SetFont(textMesh, original);   // the setting was left
                return;
            }
            if (FontManager.CloneCoverageProbeAvailable && !FontManager.CloneCoversText(settingsFontName, font, text)) return;

            var replacement = For(original, settingsFontName, font);
            if (replacement == null) return;
            Ensure(replacement, text);
            if (IdOf(current) != IdOf(replacement.Data)) SetFont(textMesh, replacement.Data);
            Track(replacement, textMesh);
        }

        /// <summary>The characters of a presented text (shaped glyphs included) added to the replacement it draws from, if it does.</summary>
        internal static void EnsureDrawn(object textMesh, string text)
        {
            if (textMesh == null || string.IsNullOrEmpty(text)) return;
            if (Ours(FontOf(textMesh), out var replacement)) Ensure(replacement, text);
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
            int originalId = IdOf(original);
            if (originalId == -1) return null;
            if (_byOriginal.TryGetValue(originalId, out var known) && known.FontId == IdOf(font)) return known;
            if (!Resolve()) return null;

            float lineHeight = Get(original, "lineHeight") is float h ? h : 0f;
            var texel = Get(original, "texelSize") is Vector2 v ? v : Vector2.zero;
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

            // The dictionary in the member's own type: Dictionary<int, tk2dFontChar> on Mono,
            // Il2CppSystem's under IL2CPP — both written through their indexer and Remove.
            var dictType = MemberType(_dataType, "charDict");
            if (dictType == null)
            {
                TranslatorCore.LogWarning($"[Tk2dFonts] {originalName}: this tk2d has no character dictionary — kept");
                return null;
            }

            var go = new GameObject((original as UnityEngine.Object)?.name ?? originalName);
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            var data = TypeHelper.AddComponentByType(go, _dataType);
            if (data == null)
            {
                TranslatorCore.LogWarning($"[Tk2dFonts] {originalName}: no tk2dFontData could be made — kept");
                UnityEngine.Object.Destroy(go);
                return null;
            }

            var dict = Activator.CreateInstance(dictType);
            var replacement = new Replacement
            {
                Original = original, OriginalId = originalId, OriginalName = originalName, Font = font, FontId = IdOf(font),
                Data = data, CharDict = dict,
                DictSet = dictType.GetMethod("set_Item"), DictRemove = OneArgument(dictType, "Remove"),
                PixelSize = pixelSize, Scale = lineHeight / lineHeightPx, LineHeightPx = lineHeightPx, AscentPx = ascentPx,
            };
            Set(data, "version", Get(original, "version") ?? 2);
            Set(data, "lineHeight", lineHeight);
            Set(data, "useDictionary", true);
            Set(data, "charDict", dict);
            Set(data, "chars", NewArray(MemberType(_dataType, "chars"), 0));
            Set(data, "kerning", NewArray(MemberType(_dataType, "kerning"), 0));
            Set(data, "material", font.material);
            Set(data, "needMaterialInstance", false);
            Set(data, "isPacked", false);
            Set(data, "textureGradients", false);
            Set(data, "premultipliedAlpha", false);
            Set(data, "texelSize", new Vector2(replacement.Scale, replacement.Scale));
            Put(replacement, 0, NewChar(Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, false, 0f));   // what a missing character draws
            replacement.Known.Add(0);

            HookRebuilt();
            _byOriginal[originalId] = replacement;
            _byData[IdOf(data)] = replacement;
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
            FontAtlas.Request(replacement.Font, text, replacement.PixelSize, FontStyle.Normal);
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
                r.DictRemove.Invoke(r.CharDict, new object[] { c });   // not in the font: entry 0 is drawn, as tk2d does
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
            Put(r, c, NewChar(p0, p1, uv0, uv1, flipped, ci.advance * s));
        }

        private static void Put(Replacement r, int c, object entry) => r.DictSet.Invoke(r.CharDict, new[] { (object)c, entry });

        private static object NewChar(Vector3 p0, Vector3 p1, Vector3 uv0, Vector3 uv1, bool flipped, float advance)
        {
            var ch = Activator.CreateInstance(_charType);
            Set(ch, "p0", p0); Set(ch, "p1", p1); Set(ch, "uv0", uv0); Set(ch, "uv1", uv1);
            Set(ch, "flipped", flipped); Set(ch, "advance", advance); Set(ch, "channel", 0);
            Set(ch, "gradientUv", NewArray(MemberType(_charType, "gradientUv"), 4));
            return ch;
        }

        /// <summary>
        /// The texts drawn with a replacement, built again when its atlas moves — held by instance id
        /// (a wrapper read twice is two objects under IL2CPP), the destroyed ones let go.
        /// </summary>
        private static void Track(Replacement replacement, object textMesh)
        {
            int id = IdOf(textMesh);
            if (id == -1 || replacement.Users.ContainsKey(id)) return;
            var gone = new List<int>();
            foreach (var user in replacement.Users) if (!TypeHelper.IsUnityObjectAlive(user.Value)) gone.Add(user.Key);
            foreach (int g in gone) replacement.Users.Remove(g);
            replacement.Users[id] = textMesh;
        }

        // ── the atlas moved: every entry read again, every text built again ──

        private static void HookRebuilt()
        {
            if (_rebuiltHooked) return;
            _rebuiltHooked = true;
            // Unfollowed, a replacement keeps the places its characters had in an atlas Unity has
            // since moved, and draws pieces of other characters: said.
            try
            {
                _rebuiltHandler = EngineEvents.Add(typeof(Font), "textureRebuilt", (Action<Font>)OnTextureRebuilt);
                if (_rebuiltHandler == null) TranslatorCore.LogWarning("[Tk2dFonts] Font.textureRebuilt not found — tk2d replacements are not read again when an atlas moves");
            }
            catch (Exception ex) { Faults.Say("Tk2dFonts.HookRebuilt", ex); }
        }

        /// <summary>The atlas event let go at shutdown: left live, Unity's teardown would call it against destroyed fonts.</summary>
        internal static void Shutdown()
        {
            if (_rebuiltHandler == null) return;
            EngineEvents.Remove(typeof(Font), "textureRebuilt", _rebuiltHandler);
            _rebuiltHandler = null;
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
                int fontId = IdOf(font);
                foreach (var r in _byData.Values)
                {
                    if (r.FontId != fontId) continue;
                    var known = new System.Text.StringBuilder(r.Known.Count);
                    foreach (int c in r.Known) if (c != 0) known.Append((char)c);
                    if (known.Length > 0) FontAtlas.Request(font, known.ToString(), r.PixelSize, FontStyle.Normal);
                }
                foreach (var r in _byData.Values)
                {
                    if (r.FontId != fontId) continue;
                    foreach (int c in r.Known) Fill(r, c);
                    foreach (var user in r.Users.Values)
                        if (TypeHelper.IsUnityObjectAlive(user)) TypeHelper.InvokeNoArg(user, "ForceBuild");
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

        /// <summary>
        /// A member of tk2d's types by name: the field on Mono, the property Il2CppInterop makes of it
        /// under IL2CPP.
        /// </summary>
        private static MemberInfo Member(Type type, string name)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return (MemberInfo)type.GetField(name, flags) ?? Members.Property(type, name, flags);
        }

        private static Type MemberType(Type type, string name)
        {
            var member = Member(type, name);
            return member is FieldInfo f ? f.FieldType : (member as PropertyInfo)?.PropertyType;
        }

        private static object Get(object target, string name)
        {
            var member = target == null ? null : Member(target.GetType(), name);
            if (member is FieldInfo f) return f.GetValue(target);
            return member is PropertyInfo p && p.CanRead ? p.GetValue(target, null) : null;
        }

        private static void Set(object target, string name, object value)
        {
            // A tk2d version without this member: the member it would set does not exist there either.
            var member = Member(target.GetType(), name);
            if (member is FieldInfo f) f.SetValue(target, value);
            else if (member is PropertyInfo p && p.CanWrite) p.SetValue(target, value, null);
        }

        /// <summary>An array of the member's own type: T[] on Mono, Il2CppReferenceArray/StructArray (made by their length) under IL2CPP.</summary>
        private static object NewArray(Type arrayType, int length)
        {
            if (arrayType == null) return null;
            if (arrayType.IsArray) return Array.CreateInstance(arrayType.GetElementType(), length);
            return arrayType.GetConstructor(new[] { typeof(long) })?.Invoke(new object[] { (long)length });
        }

        private static MethodInfo OneArgument(Type type, string name)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (method.Name == name && method.GetParameters().Length == 1) return method;
            return null;
        }
    }
}
