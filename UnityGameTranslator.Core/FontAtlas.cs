using System;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Font.RequestCharactersInTexture — glyphs put into a dynamic font's atlas — on every runtime.
    ///
    /// 🔴 **The public method must never be called on Unity 2023.1+ under IL2CPP.** From 2023.1
    /// Unity hands strings to native code as a <c>ManagedSpanWrapper</c> (pointer + length), so the
    /// method has a managed body, and the interop rebuilds that body around an
    /// Il2CppSystem.ReadOnlySpan whose GetPinnableReference the game's runtime does not have: every
    /// call throws MissingMethodException — the atlas never filled, legacy text drew without its
    /// glyphs, and the fault repeated at every pass (a 2023 game, 2026-10-02). Same trap as
    /// LoadImage (TextureUtils.LoadImageThroughSpanWrapper), same way out: the native entry point,
    /// <c>RequestCharactersInTexture_Injected(IntPtr, ref ManagedSpanWrapper, int, FontStyle)</c>,
    /// handed the font's native pointer and the pinned characters. Native code reads them once,
    /// synchronously, and keeps nothing.
    ///
    /// Every caller of the mod goes through <see cref="Request"/>: one place to get right.
    /// </summary>
    internal static class FontAtlas
    {
        private static bool _resolved;
        private static MethodInfo _injected, _marshalFont;
        private static SpanWrappers.Shape _wrapper;

        /// <summary>Puts these characters, at the font's own size and normal style, into the font's atlas.</summary>
        // ⚠ An overload, not optional parameters: a default value of an engine enum (FontStyle) is a
        // constant the IL2CPP adapter's reference rewrite (Cecil) must resolve, and cannot.
        internal static void Request(Font font, string characters) => Request(font, characters, 0, FontStyle.Normal);

        /// <summary>Puts these characters, at this size and style (0 = the font's own size), into the font's atlas.</summary>
        internal static void Request(Font font, string characters, int size, FontStyle style)
        {
            if (font == null || string.IsNullOrEmpty(characters)) return;
            Resolve();
            if (_injected == null)
            {
                font.RequestCharactersInTexture(characters, size, style);
                return;
            }

            var nativeFont = (IntPtr)_marshalFont.Invoke(null, new object[] { font });
            if (nativeFont == IntPtr.Zero) return;   // a destroyed font: nothing to fill
            SpanWrappers.WithText(_wrapper, characters, wrapper => _injected.Invoke(null, new object[] { nativeFont, wrapper, size, style }));
        }

        /// <summary>
        /// The 2023.1+ IL2CPP shape, all or nothing: the injected entry made public by the interop,
        /// the wrapper (SpanWrappers), and the font's native pointer (what the rebuilt body starts
        /// with). On Mono the entry is private and never matches; below 2023.1 it does not exist —
        /// the public method is then the right one.
        /// </summary>
        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            MethodInfo injected = null;
            foreach (var method in typeof(Font).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "RequestCharactersInTexture_Injected") continue;
                var p = method.GetParameters();
                if (p.Length == 4 && p[0].ParameterType == typeof(IntPtr) && SpanWrappers.IsWrapperParameter(p[1])
                    && p[2].ParameterType == typeof(int) && p[3].ParameterType == typeof(FontStyle))
                {
                    injected = method;
                    break;
                }
            }
            if (injected == null) return;

            var wrapper = SpanWrappers.Of(injected, 1);
            var marshal = SpanWrappers.NativePointerOf(typeof(Font));
            if (wrapper == null || marshal == null)
            {
                // The public method would throw at every call here: said once, and kept as the path
                // so each caller's own handling says what it could not do.
                TranslatorCore.LogWarning($"[FontAtlas] RequestCharactersInTexture_Injected found without its parts (wrapper={wrapper != null}, marshal={marshal != null}) — dynamic font atlases are filled through the public method");
                return;
            }

            _injected = injected;
            _marshalFont = marshal;
            _wrapper = wrapper;
            TranslatorCore.LogInfo("[FontAtlas] Found Font.RequestCharactersInTexture_Injected (Unity 2023.1+ IL2CPP) — the public method is not used");
        }
    }
}
