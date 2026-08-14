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
    /// for chips whose width is their text. So the widths are estimated and the chips are dealt into
    /// rows. The estimate is deliberately GENEROUS: guessing too wide costs an early line break,
    /// guessing too narrow clips a word, and a clipped chip is a chip that lies.
    /// </summary>
    public static class BadgeStrip
    {
        /// <summary>
        /// Roughly how wide a character is at hint size. Measured against Unity's default font
        /// rather than derived: a real measurement needs a layout pass that has not happened yet
        /// when this builds.
        /// </summary>
        private const float CharWidth = 0.62f;

        /// <summary>Padding inside a chip, left and right together.</summary>
        private const float ChipPadding = 14f;

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
        public static GameObject Create(GameObject parent, string name, List<Badge> badges,
                                        float availableWidth)
        {
            var strip = UIFactory.CreateVerticalGroup(parent, name, false, false, true, true, 3,
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

            for (int i = 0; i < badges.Count; i++)
            {
                var badge = badges[i];
                float width = Width(badge.Text);

                // A chip wider than the strip goes on a line of its own rather than being shrunk:
                // it is still readable, where a squeezed one is not.
                if (row == null || (used > 0f && used + width > availableWidth))
                {
                    row = UIFactory.CreateHorizontalGroup(strip, name + "Row" + rowIndex,
                                                          false, false, true, true, 4,
                                                          default, default, TextAnchor.MiddleLeft);
                    UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightSmall,
                                               flexibleWidth: 9999, flexibleHeight: 0);
                    UIStyles.ClearRowBackground(row);

                    rowIndex++;
                    used = 0f;
                }

                Chip(row, name + "Chip" + i, badge, width);
                used += width + 4f;
            }

            return strip;
        }

        /// <summary>One chip: a word in its tone, on the panel's own item background.</summary>
        private static void Chip(GameObject row, string name, Badge badge, float width)
        {
            var chip = UIFactory.CreateLabel(row, name, badge.Text, TextAnchor.MiddleCenter,
                                             supportRichText: false);
            chip.fontSize = UIStyles.FontSizeHint;
            chip.color = Colour(badge.Tone);

            // flexibleWidth 0: a chip is the size of its text. Letting it stretch would spread three
            // chips across the whole card and lose the fact that they are separate things.
            UIFactory.SetLayoutElement(chip.gameObject, minWidth: Mathf.CeilToInt(width),
                                       minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 0, flexibleHeight: 0);
            UIStyles.SetBackground(chip.gameObject, UIStyles.ItemBackground);

            // ⚠ Never translated, like the scope switch's words: these are the product's own terms,
            // identical in three products, and translating the mod's interface must not make one of
            // the three drift.
            TranslatorCore.RegisterExcluded(chip);
        }

        private static float Width(string text)
        {
            return (text ?? "").Length * UIStyles.FontSizeHint * CharWidth + ChipPadding;
        }
    }
}
