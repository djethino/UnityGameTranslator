using System;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// IL2CPP compatibility helpers. Some Unity API constructors / methods are
    /// stripped by IL2CPP when the host game's own code never calls them — even
    /// though they exist on every Unity version. Hitting one of those at runtime
    /// crashes us with MissingMethodException.
    ///
    /// Confirmed cases:
    ///   - <c>RectOffset(int, int, int, int)</c> stripped on Heroes of Might and
    ///     Magic: Olden Era (Unity IL2CPP build, BepInEx 6).
    ///
    /// Strategy: route every fragile constructor through a helper that uses the
    /// most stable code path (default ctor + property setters when possible,
    /// try/catch fallback otherwise). The "happy path" — i.e. games where the
    /// stripped constructor IS available — runs the exact same code as before
    /// (no behavior change, no measurable overhead).
    /// </summary>
    public static class Compat
    {
        /// <summary>
        /// Build a <see cref="RectOffset"/> via the 0-arg ctor + setters instead
        /// of the 4-arg ctor. Strictly equivalent: the 4-arg ctor itself just
        /// assigns the same four properties internally.
        /// </summary>
        public static RectOffset MakeRectOffset(int left, int right, int top, int bottom)
        {
            var ro = new RectOffset();
            ro.left = left;
            ro.right = right;
            ro.top = top;
            ro.bottom = bottom;
            return ro;
        }

        /// <summary>
        /// Build a <see cref="Rect"/> via the default ctor + property setters.
        /// Rect is a struct so its 0-arg ctor is always present — IL2CPP cannot
        /// strip it.
        /// </summary>
        public static Rect MakeRect(float x, float y, float width, float height)
        {
            var r = new Rect();
            r.x = x;
            r.y = y;
            r.width = width;
            r.height = height;
            return r;
        }

        /// <summary>
        /// Build a <see cref="Texture2D"/> with optional fallbacks. Unity does
        /// NOT expose a 0-arg ctor (texture must have a width/height at
        /// creation), so we have to attempt the 4-arg ctor first. On games
        /// where it's available (the vast majority), this is identical to
        /// calling <c>new Texture2D(w, h, fmt, mipmap)</c> directly. The
        /// fallback paths only run on stripped builds.
        /// </summary>
        public static Texture2D MakeTexture2D(int width, int height, TextureFormat format, bool mipmap)
        {
            // 🔴 **Asked before it is called, because catching is not enough.** A stripped
            // constructor does NOT reliably raise MissingMethodException: on Unity 2022.3.62f2 and
            // 6000.0.77f1 the six-argument form threw, while the four-argument form below jumped to
            // a null pointer and ended the process — no exception, no log line, nothing to catch.
            // The `catch` under this call therefore protected nothing on exactly the builds it was
            // written for.
            //
            // Il2CppInterop names its pointer field after the member, with `.ctor` written `_ctor`,
            // and a stripped one is absent or zero. Reading that field is safe; calling what it
            // describes is not. On Mono there is nothing to ask and every constructor is real.
            if (HasConstructor("Int32_Int32_TextureFormat_Boolean"))
                return new Texture2D(width, height, format, mipmap);

            // 2-arg ctor is more universally preserved (used by the engine itself)
            if (HasConstructor("Int32_Int32"))
                return new Texture2D(width, height);

            throw new InvalidOperationException(
                "this build has no Texture2D constructor — textures cannot be created here");
        }

        /// <summary>
        /// Whether Texture2D really has the constructor taking these parameters.
        ///
        /// ⚠ A field READ and never an Invoke: reflection that ends in a call meets the same null
        /// pointer as the direct call. <paramref name="signature"/> is the parameter part of the
        /// generated field name, e.g. "Int32_Int32_TextureFormat_Boolean".
        /// </summary>
        private static bool HasConstructor(string signature)
        {
            // Mono: nothing is stripped and no such fields exist, so everything declared is real.
            if (TranslatorCore.Adapter?.IsIL2CPP != true) return true;

            try
            {
                var fields = typeof(Texture2D).GetFields(BindingFlags.Static
                                                         | BindingFlags.NonPublic
                                                         | BindingFlags.Public);

                foreach (var field in fields)
                {
                    if (field.Name.IndexOf("ctor", StringComparison.Ordinal) < 0) continue;
                    if (field.Name.IndexOf(signature, StringComparison.Ordinal) < 0) continue;

                    if (field.GetValue(null) is IntPtr pointer && pointer != IntPtr.Zero)
                        return true;
                }
            }
            catch { }

            return false;
        }
    }
}
