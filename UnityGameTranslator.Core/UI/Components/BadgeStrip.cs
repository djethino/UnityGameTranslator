using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// The badge strip, drawn in uGUI.
    ///
    /// ⚠ **Everything decided is in <see cref="Badges"/>** — which chips, in which order, in what
    /// words, and how loudly. This turns those answers into labels, exactly as the manager turns the
    /// same ones into Avalonia borders. Anything decided here would be a decision the manager does
    /// not share, and one file described two ways depending on which program opened it is worse
    /// than describing it once.
    ///
    /// ⚠ **Rows are packed here because uGUI has nothing that wraps.** UniverseLib offers a
    /// horizontal group, which runs off the edge, and a grid, whose cells are all one size — useless
    /// for chips whose width is their text. So each chip is MEASURED and the chips are dealt into
    /// rows within the width the caller has.
    ///
    /// 🔴 Measured, not estimated (2026-09-18). The width used to be guessed from the letter
    /// count, "deliberately generous", and the strip wrapped at a constant no card was ever as
    /// narrow as: "Solo work" sat alone on a second line under four chips that filled half the
    /// card. A label reports its preferred width the moment it has its font — the scope switch
    /// and the title bar already size themselves by it — so nothing here needs guessing.
    ///
    /// ⚠ The chip is the Manager's, in uGUI: the input surface as background, a one-pixel
    /// subtle border, the small radius, 7×2 of padding, the hint size in the tone's colour.
    /// A bare word in a colour on the card's own surface was the mod's alone.
    /// </summary>
    public static class BadgeStrip
    {
        /// <summary>Padding inside a chip, on each side — the Manager's 7.</summary>
        private const float ChipSide = 7f;

        /// <summary>Space between two chips on a row, and between rows — the Manager's 5.</summary>
        private const int Gap = 5;

        /// <summary>
        /// A chip is one step lighter than what it sits on, edged one step further — the site's
        /// gray-700 pill on its gray-800 card. On a list row, which is ITSELF the card's raised
        /// step, the same surface vanished: the chips only showed on a highlighted row
        /// (2026-09-18). So the step is taken from the surface underneath, never fixed.
        /// </summary>
        private static Color SurfaceOn(Surface under)
            => under == Surface.Item || under == Surface.Elevated ? UIStyles.ItemBackgroundHover : UIStyles.InputBackground;

        private static Color EdgeOn(Surface under)
            => under == Surface.Item || under == Surface.Elevated ? UIStyles.BorderStrong : UIStyles.BorderSubtle;

        private static Color Colour(BadgeTone tone)
        {
            switch (tone)
            {
                case BadgeTone.Good: return UIStyles.StatusSuccess;
                case BadgeTone.Notice: return UIStyles.StatusInfo;
                case BadgeTone.Attention: return UIStyles.StatusWarning;
                case BadgeTone.Wrong: return UIStyles.StatusError;
                case BadgeTone.Quiet: return UIStyles.TextMuted;
                default: return UIStyles.TextSecondary;
            }
        }

        /// <summary>
        /// Builds the strip under <paramref name="parent"/>, wrapping within
        /// <paramref name="availableWidth"/>.
        ///
        /// ⚠ Returns the container so a caller can hide it: a strip with nothing in it must not
        /// leave an empty band behind, and the caller is the only one that knows whether its card
        /// has other rows to fall back on.
        /// </summary>
        /// <param name="under">The surface the strip sits on: a chip is one step lighter than it.</param>
        public static Host Create(Host parent, string name, List<Badge> badges, float availableWidth,
                                  Surface under = Surface.Card)
            => new Host(Create(parent.Object, name, badges, availableWidth, under));

        /// <summary>
        /// The width a host offers the strip: what it measures once it has been laid out, and
        /// <paramref name="fallback"/> before — a host built this frame has no width yet.
        /// </summary>
        public static float WidthOf(Host host, float fallback)
        {
            var rect = host?.Object != null ? host.Object.GetComponent<RectTransform>() : null;
            float width = rect != null ? rect.rect.width : 0f;
            return width > 1f ? width : fallback;
        }

        internal static GameObject Create(GameObject parent, string name, List<Badge> badges,
                                        float availableWidth, Surface under = Surface.Card)
        {
            var strip = UIFactory.CreateVerticalGroup(parent, name, false, false, true, true, Gap,
                                                      default, default, TextAnchor.UpperLeft);
            UIFactory.SetLayoutElement(strip, flexibleWidth: 9999, flexibleHeight: 0);
            UIStyles.ClearRowBackground(strip);

            if (badges == null || badges.Count == 0)
            {
                strip.SetActive(false);
                return strip;
            }

            GameObject row = null;
            float used = 0f;
            int rowIndex = 0;

            // The chips are built first and measured, then dealt: a chip has to exist to say how
            // wide its word is. Built under the strip and moved to their row afterwards.
            var chips = new List<GameObject>(badges.Count);
            var widths = new List<float>(badges.Count);
            for (int i = 0; i < badges.Count; i++)
            {
                chips.Add(Chip(strip, name + "Chip" + i, badges[i], under, out float width));
                widths.Add(width);
            }

            for (int i = 0; i < chips.Count; i++)
            {
                float width = widths[i];

                // A chip wider than the strip goes on a line of its own rather than being shrunk:
                // it is still readable, where a squeezed one is not.
                if (row == null || (used > 0f && used + Gap + width > availableWidth))
                {
                    row = UIFactory.CreateHorizontalGroup(strip, name + "Row" + rowIndex,
                                                          false, false, true, true, Gap,
                                                          default, default, TextAnchor.MiddleLeft);
                    UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightSmall,
                                               flexibleWidth: 9999, flexibleHeight: 0);
                    UIStyles.ClearRowBackground(row);

                    rowIndex++;
                    used = 0f;
                }

                chips[i].transform.SetParent(row.transform, false);
                used += (used > 0f ? Gap : 0f) + width;
            }

            return strip;
        }

        /// <summary>
        /// One chip, as the Manager draws it: the word in its tone on the input surface, edged
        /// and rounded. <paramref name="width"/> is what it measures, padding included.
        /// </summary>
        private static GameObject Chip(GameObject parent, string name, Badge badge, Surface under, out float width)
        {
            var chip = UIFactory.CreateUIObject(name, parent);

            // The surface has to EXIST before anything can paint it — SetBackground writes into
            // an Image and never adds one (the tag chips paid for this once).
            var surface = chip.AddComponent<Image>();
            surface.raycastTarget = false;
            UIStyles.SetBackground(chip, SurfaceOn(under), UIFactory.Shapes.Small);
            UIFactory.AddBorder(chip, EdgeOn(under), UIFactory.Shapes.BorderSmall);

            var label = UIFactory.CreateLabel(chip, "Word", badge.Text, TextAnchor.MiddleCenter,
                                              supportRichText: false);
            label.fontSize = UIStyles.FontSizeHint;
            label.color = Colour(badge.Tone);
            label.raycastTarget = false;

            var rect = label.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(ChipSide, 0f);
            rect.offsetMax = new Vector2(-ChipSide, 0f);

            // What the word measures at its font, plus the padding: the chip is exactly that wide,
            // and never stretches — three chips spread across the card would lose the fact that
            // they are separate things.
            width = Mathf.Ceil(label.preferredWidth) + 2f * ChipSide;
            UIFactory.SetLayoutElement(chip, minWidth: Mathf.CeilToInt(width), preferredWidth: Mathf.CeilToInt(width),
                                       minHeight: UIStyles.RowHeightSmall, preferredHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 0, flexibleHeight: 0);

            // ⚠ Never translated, like the scope switch's words: these are the product's own terms,
            // identical in three products, and translating the mod's interface must not make one of
            // the three drift.
            TranslatorCore.RegisterExcluded(label);
            return chip;
        }
    }
}
