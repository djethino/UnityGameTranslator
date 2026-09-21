using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.Runtime;

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
        /// </summary>
        public static Texture2D MakeTexture2D(int width, int height, TextureFormat format, bool mipmap)
        {
            try
            {
                return new Texture2D(width, height, format, mipmap);
            }
            catch (MissingMethodException)
            {
                try
                {
                    // 2-arg ctor is more universally preserved (used by the engine itself)
                    return new Texture2D(width, height);
                }
                catch
                {
                    // Last resort: tiny placeholder, caller will see a blank texture
                    return new Texture2D(2, 2);
                }
            }
        }

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
