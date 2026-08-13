using System;
using System.Collections.Generic;
using UnityEngine;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// The rounded corners, drawn once and lent to everything.
    ///
    /// uGUI has no border-radius. An <see cref="UnityEngine.UI.Image"/> with no sprite draws a plain
    /// rectangle, which is why the mod's interface was square while the site and the Manager are
    /// round everywhere (`rounded-lg`, 367 times in the site's templates). The way round is a
    /// 9-slice sprite: four corners that keep their curve, four edges that stretch, a middle that
    /// fills. One WHITE sprite is enough for every surface — <c>Image.color</c> still does the
    /// tinting, so the palette is untouched by any of this.
    ///
    /// ⚠ ONE texture for every shape, on purpose. uGUI batches by texture: a sprite of its own for
    /// each radius would break the batch once per radius, for shapes that together weigh 64 KB.
    /// Everything is packed into one atlas and each shape is a rect inside it.
    ///
    /// ⚠ Nothing here may throw. It runs before the panels are built, and the whole interface is
    /// downstream: a shape that cannot be made must come back null, be logged, and leave the caller
    /// with the square corner it had yesterday. See the AddListener note in CLAUDE.md for what one
    /// exception in this part of the startup costs.
    /// </summary>
    /// <summary>
    /// Which corners of a shape are round. A title bar sits at the top of a rounded panel and must
    /// follow it there while staying square where it meets the content below — same for the active
    /// tab of a tab bar.
    /// </summary>
    [Flags]
    public enum Corners
    {
        None = 0,
        TopLeft = 1,
        TopRight = 2,
        BottomLeft = 4,
        BottomRight = 8,
        Top = TopLeft | TopRight,
        Bottom = BottomLeft | BottomRight,
        /// <summary>For the first segment of a bar made of several: round outside, flat inside.</summary>
        Left = TopLeft | BottomLeft,
        /// <summary>And the last one.</summary>
        Right = TopRight | BottomRight,
        All = Top | Bottom
    }

    public static class UIShapes
    {
        /// <summary>The radius the site uses on cards, fields and buttons alike (`rounded-lg`).</summary>
        public const int RadiusCard = 8;

        /// <summary>The site's small radius (`rounded`), for badges and other small marks.</summary>
        public const int RadiusSmall = 4;

        /// <summary>Thickness of the line <see cref="Ring"/> draws, in pixels.</summary>
        private const int BorderThickness = 1;

        /// <summary>
        /// Atlas geometry. A cell holds one shape plus a two-pixel transparent margin, so bilinear
        /// filtering at the edge of one shape can never reach into its neighbour.
        /// </summary>
        // 64 cells. Seven are taken by the shapes the factory needs; the rest are for the ones asked
        // for at runtime — a pill knows its radius only when it knows its height, and a segmented
        // bar wants one shape per end. 256 KB of texture against having to ration them.
        private const int AtlasSize = 256;
        private const int CellSize = 32;
        private const int CellMargin = 2;
        private const int CellsPerRow = AtlasSize / CellSize;

        /// <summary>Biggest radius a cell can hold: the shape is 2r+2 across, plus the margin.</summary>
        public const int MaxRadius = (CellSize - CellMargin * 2 - 2) / 2;

        private static Texture2D _atlas;
        private static Color32[] _pixels;
        private static int _nextCell;
        private static bool _failed;

        /// <summary>Shapes already drawn, by their key. Rebuilt from nothing on each game start.</summary>
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        /// <summary>
        /// Draw the shapes the whole interface needs and hand them to UniverseLib, so that every
        /// button, field and dropdown the factory builds comes out round without a single panel
        /// knowing about it.
        ///
        /// Called once, before any panel exists. Doing nothing (because a runtime refused) leaves
        /// UniverseLib exactly as it was: square, and working.
        /// </summary>
        public static void Initialize()
        {
            UIFactory.Shapes.Card = Rounded(RadiusCard);
            UIFactory.Shapes.CardTop = Rounded(RadiusCard, Corners.Top);
            UIFactory.Shapes.CardBottom = Rounded(RadiusCard, Corners.Bottom);
            UIFactory.Shapes.Control = Rounded(RadiusCard);
            UIFactory.Shapes.Small = Rounded(RadiusSmall);
            UIFactory.Shapes.Border = Ring(RadiusCard);
            UIFactory.Shapes.BorderSmall = Ring(RadiusSmall);
            UIFactory.Shapes.BorderColor = UIStyles.BorderSubtle;

            if (UIFactory.Shapes.Card == null)
                TranslatorCore.LogWarning("[UIShapes] No shapes could be drawn — the interface stays square. This is a fallback, not the intent.");
        }

        /// <summary>
        /// A filled rounded rectangle of the given corner radius, white, ready to be tinted.
        /// Returns null when it cannot be drawn — the caller keeps square corners.
        /// </summary>
        public static Sprite Rounded(int radius, Corners corners = Corners.All)
        {
            return Get("fill" + radius + "_" + (int)corners, radius, 0, corners);
        }

        /// <summary>
        /// A ring: one line around a rounded rectangle, nothing in the middle. Used to draw the
        /// card and field outlines the site has and the mod had not.
        /// </summary>
        public static Sprite Ring(int radius, Corners corners = Corners.All)
        {
            return Get("ring" + radius + "_" + (int)corners, radius, BorderThickness, corners);
        }

        /// <summary>
        /// A shape whose corners are as round as it is tall — the site's `rounded-full`, used on
        /// the quality bar and on pills.
        ///
        /// The radius follows the height, so it is asked for rather than fixed: a 6-pixel bar and a
        /// 24-pixel chip are both "fully round" and share no radius at all.
        /// </summary>
        public static Sprite Pill(int height)
        {
            return Rounded(Mathf.Max(1, height / 2));
        }

        private static Sprite Get(string key, int radius, int thickness, Corners corners)
        {
            if (_failed) return null;

            Sprite cached;
            if (_cache.TryGetValue(key, out cached))
                return cached;

            // A radius past what a cell holds is clamped rather than refused: a shape a little less
            // round is a detail, a missing background is a hole in the interface.
            int clamped = Mathf.Clamp(radius, 1, MaxRadius);
            if (clamped != radius)
                TranslatorCore.LogDebug($"[UIShapes] Radius {radius} clamped to {clamped} (a cell holds no more)");

            Sprite sprite = Draw(clamped, thickness, corners);
            _cache[key] = sprite;
            return sprite;
        }

        private static Sprite Draw(int radius, int thickness, Corners corners)
        {
            try
            {
                if (!EnsureAtlas()) return null;

                if (_nextCell >= CellsPerRow * CellsPerRow)
                {
                    TranslatorCore.LogWarning("[UIShapes] Shape atlas is full — this shape stays square");
                    return null;
                }

                // The shape itself: 2r for the corners, plus two pixels of straight edge in the
                // middle. That middle is what the 9-slice stretches, and it must exist.
                int size = radius * 2 + 2;
                int cell = _nextCell++;
                int originX = (cell % CellsPerRow) * CellSize + CellMargin;
                int originY = (cell / CellsPerRow) * CellSize + CellMargin;

                Paint(originX, originY, size, radius, thickness, corners);

                _atlas.Apply(false, false);

                // FullRect, never Tight: Tight trims the mesh to the opaque pixels, and the corners
                // of a rounded shape are transparent by design — the slicing would come out mangled.
                // CreateSpriteSafe asks for FullRect on every runtime that offers the overload.
                object spriteObj = TextureUtils.CreateSpriteSafe(
                    _atlas,
                    Compat.MakeRect(originX, originY, size, size),
                    new Vector2(0.5f, 0.5f),
                    // The canvas is set to 100 reference pixels per unit (UIBase) and its scaler is
                    // left at constant pixel size, so a sprite at 100 renders one texel per pixel:
                    // a radius of 8 texels is a radius of 8 pixels on screen, at any resolution.
                    100f,
                    new Vector4(radius, radius, radius, radius));

                Sprite sprite = spriteObj as Sprite;
                if (sprite == null)
                {
                    TranslatorCore.LogWarning($"[UIShapes] Sprite.Create returned nothing for radius {radius} — that shape stays square");
                    return null;
                }

                sprite.name = $"UGT_Shape_{radius}_{thickness}_{(int)corners}";
                return sprite;
            }
            catch (Exception ex)
            {
                // Every later shape would fail the same way; asking again each time would fill the
                // log with one line per element in the interface.
                _failed = true;
                TranslatorCore.LogWarning($"[UIShapes] Cannot draw shapes on this runtime — the interface stays square: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Write one shape into the atlas pixels.
        ///
        /// The distance field is the standard one for a rounded box: negative inside, zero on the
        /// edge, positive outside. Coverage comes straight out of it, which is what gives the curve
        /// a clean edge instead of a staircase — the shape is drawn at the size it is displayed, so
        /// there is nothing to smooth afterwards.
        ///
        /// White everywhere, including where it is fully transparent: a transparent BLACK pixel
        /// bleeds dark under bilinear filtering and outlines every card with a faint halo.
        /// </summary>
        private static void Paint(int originX, int originY, int size, int radius, int thickness, Corners corners)
        {
            float half = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Which corner this pixel belongs to decides its radius: the distance field is
                    // written in terms of |x| and |y| from the middle, so it treats the four
                    // quadrants alike and a per-quadrant radius is all it takes to leave two of
                    // them square. Unity's texture origin is bottom-left, so low y is the bottom.
                    bool left = x + 0.5f < half;
                    bool bottom = y + 0.5f < half;
                    Corners corner = bottom
                        ? (left ? Corners.BottomLeft : Corners.BottomRight)
                        : (left ? Corners.TopLeft : Corners.TopRight);

                    int cornerRadius = (corners & corner) != 0 ? radius : 0;
                    float inner = half - cornerRadius;

                    // Pixel centres, measured from the middle of the shape.
                    float px = Mathf.Abs(x + 0.5f - half) - inner;
                    float py = Mathf.Abs(y + 0.5f - half) - inner;

                    float outsideX = Mathf.Max(px, 0f);
                    float outsideY = Mathf.Max(py, 0f);
                    float distance = Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY)
                                   + Mathf.Min(Mathf.Max(px, py), 0f)
                                   - cornerRadius;

                    float alpha;
                    if (thickness <= 0)
                    {
                        // Filled: covered inside, half-covered exactly on the edge.
                        alpha = Mathf.Clamp01(0.5f - distance);
                    }
                    else
                    {
                        // A band of `thickness` drawn INWARDS from the edge, so the line stays
                        // inside the element's rect and no layout has to make room for it.
                        alpha = Mathf.Clamp01(0.5f - Mathf.Abs(distance + thickness * 0.5f) + thickness * 0.5f);
                    }

                    int index = (originY + y) * AtlasSize + (originX + x);
                    _pixels[index] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            TextureUtils.SetPixels32Safe(_atlas, _pixels);
        }

        private static bool EnsureAtlas()
        {
            if (_atlas != null) return true;

            _atlas = Compat.MakeTexture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false);
            if (_atlas == null)
            {
                TranslatorCore.LogWarning("[UIShapes] Could not create the shape atlas — the interface stays square");
                return false;
            }

            _atlas.name = "UGT_ShapeAtlas";
            _atlas.filterMode = FilterMode.Bilinear;
            // Clamp, so a shape at the very edge of the atlas cannot sample the opposite side.
            _atlas.wrapMode = TextureWrapMode.Clamp;
            _atlas.hideFlags = HideFlags.HideAndDontSave;

            // Transparent white, not transparent black: see Paint.
            _pixels = new Color32[AtlasSize * AtlasSize];
            for (int i = 0; i < _pixels.Length; i++)
                _pixels[i] = new Color32(255, 255, 255, 0);

            return true;
        }
    }
}
