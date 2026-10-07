using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.Runtime;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Compatibility helpers for Unity API the running game may not carry. Two causes, same
    /// symptom (MissingMethodException at the call):
    ///   - IL2CPP strips constructors / methods the game's own code never calls, even though
    ///     they exist on every Unity version;
    ///   - the member did not exist yet in the game's Unity version (the mod compiles against a
    ///     newer UnityEngine than the oldest games it runs in).
    ///
    /// Confirmed cases:
    ///   - <c>RectOffset(int, int, int, int)</c> stripped on Heroes of Might and
    ///     Magic: Olden Era (Unity IL2CPP build, BepInEx 6).
    ///   - <c>ColorBlock.selectedColor</c> absent before Unity 2019.1 (see <see cref="SetSelectedColor"/>).
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
        ///
        /// 🔴 **Each constructor lives in a method of its own, and the try is HERE** (2026-09-30).
        /// The runtime resolves a missing member when it compiles the method that NAMES it
        /// (analyse/pieges-projet.md §9). Written inline, the 2-arg fallback made this whole
        /// method uncompilable on a game that stripped the 2-arg constructor — while it kept the
        /// 4-arg one this method tries first. The exception left before the try, through the first
        /// flag of the wizard, and CreatePanels stopped there: an empty wizard over the whole
        /// screen, on a game already set up. NoInlining keeps the JIT from folding the calls back.
        /// ⚠ No 2x2 placeholder after them: it was the same 2-arg constructor again, so a game
        /// without it failed twice and threw anyway. The caller gets the exception.
        /// </summary>
        public static Texture2D MakeTexture2D(int width, int height, TextureFormat format, bool mipmap)
        {
            try
            {
                return NewTexture(width, height, format, mipmap);
            }
            catch (MissingMethodException ex)
            {
                Faults.Say("Compat.MakeTexture2D", ex, "4-arg constructor missing, trying the 6-arg one");
            }

            // The 6-arg one next: it keeps the format and the mip choice the 2-arg one would lose.
            // IL2CPP's interop exposes it on the games seen (a 2020.3 game that stripped the 2-arg
            // one kept it), and UniverseLib's own texture helper has always built with it.
            // ⚠ Found by reflection, never named: in the Mono UnityEngine this Core compiles against
            // it is internal, so the compiler refuses it. On Mono it is never reached anyway — the
            // 4-arg one is always there.
            var sixArgs = SixArgConstructor;
            if (sixArgs != null)
                return (Texture2D)sixArgs.Invoke(new object[] { width, height, format, mipmap ? -1 : 1, false, IntPtr.Zero });

            // Last: the 2-arg one, used by the engine itself — RGBA32 with mipmaps.
            Faults.Say("Compat.MakeTexture2D 6-arg",
                new MissingMethodException("UnityEngine.Texture2D", ".ctor(Int32, Int32, TextureFormat, Int32, Boolean, IntPtr)"),
                "6-arg constructor missing, using the 2-arg one");
            return NewTexture(width, height);
        }

        /// <summary>Texture2D(int, int, TextureFormat, int mipCount, bool linear, IntPtr nativeTex), where public.</summary>
        private static System.Reflection.ConstructorInfo SixArgConstructor
            => _sixArgs ?? (_sixArgs = typeof(Texture2D).GetConstructor(new[]
                   { typeof(int), typeof(int), typeof(TextureFormat), typeof(int), typeof(bool), typeof(IntPtr) }));

        private static System.Reflection.ConstructorInfo _sixArgs;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Texture2D NewTexture(int width, int height, TextureFormat format, bool mipmap)
            => new Texture2D(width, height, format, mipmap);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Texture2D NewTexture(int width, int height)
            => new Texture2D(width, height);

        // Resolved once. Null member where the game's Unity predates it — SetValue is then a no-op.
        private static readonly AmbiguousMemberHandler<ColorBlock, Color> SelectedColorMember
            = new AmbiguousMemberHandler<ColorBlock, Color>(true, true, "selectedColor", "m_SelectedColor");

        /// <summary>
        /// Sets <c>ColorBlock.selectedColor</c> where the game's Unity has it (2019.1 and later).
        /// Before 2019.1 the "selected" state does not exist, so there is nothing to theme.
        ///
        /// 🔴 Never write <c>cb.selectedColor</c> directly: on an older game the call throws
        /// inside panel construction, <c>CreatePanels</c> aborts, and the whole interface is gone
        /// — an empty wizard filling the screen. This is the same tolerant access UniverseLib
        /// already uses for its own controls (<c>MonoProvider</c>, <c>Il2CppProvider</c>).
        /// </summary>
        public static void SetSelectedColor(ref ColorBlock colors, Color color)
        {
            object boxed = colors;
            SelectedColorMember.SetValue(boxed, color);
            colors = (ColorBlock)boxed;
        }
    }
}
