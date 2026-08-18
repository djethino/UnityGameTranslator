using System;
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
    }
}
