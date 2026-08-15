using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A language, shown as its flag and — when the flag cannot name it alone — its tag beside it.
    ///
    /// 🔴 **The same control as the manager's and the site's**, and the rule behind it is decided
    /// once in <see cref="Flags.Mark"/>: ten Indian languages share one flag because no Indian
    /// state has one of its own, and bokmål and nynorsk are two written standards of one country.
    /// Those show their tag; the eighty that a flag identifies on its own do not, because a chip
    /// beside every flag would be noise.
    ///
    /// ⚠ **The flag's Image is left WHITE.** Every other picture in this product is a white shape
    /// tinted by its Image; a flag carries its own colours, and tinting it would repaint it.
    /// </summary>
    public static class LanguageMark
    {
        /// <summary>Height of the flag, in pixels. Its width follows the catalogue's grid.</summary>
        private const int FlagHeight = 11;

        /// <summary>Between the flag and the tag beside it.</summary>
        private const int Gap = 4;

        /// <summary>
        /// Build the mark for a language into <paramref name="parent"/>, and return it.
        ///
        /// Returns null when the language is unknown — the caller then writes its name and nothing
        /// else, which is the truth rather than a placeholder.
        /// </summary>
        public static GameObject Create(GameObject parent, string name, string languageName)
        {
            var mark = Flags.Mark(languageName);
            if (mark.Flag == null && string.IsNullOrEmpty(mark.Tag)) return null;

            var row = UIFactory.CreateUIObject(name, parent);
            UIFactory.SetLayoutGroup<HorizontalLayoutGroup>(row, false, false, true, true,
                                                            Gap, 0, 0, 0, 0,
                                                            TextAnchor.MiddleLeft);

            var sprite = Icons.Flag(mark.Flag);
            if (sprite != null)
            {
                // The grid is wider than it is tall; the flag keeps that shape whatever the row's
                // height, so a stack of them lines up.
                int width = Mathf.RoundToInt(FlagHeight * (float)Flags.Width / Flags.Height);

                var holder = UIFactory.CreateUIObject(name + "Flag", row);
                var image = holder.AddComponent<Image>();
                image.sprite = sprite;
                image.preserveAspect = true;

                // ⚠ White, and it matters: uGUI multiplies a sprite by this colour. Any other value
                // and the flag comes out in that shade.
                image.color = Color.white;

                // A mark that swallows a click is a dead spot on whatever carries it.
                image.raycastTarget = false;

                UIFactory.SetLayoutElement(holder, minWidth: width, preferredWidth: width,
                                           minHeight: FlagHeight, preferredHeight: FlagHeight,
                                           flexibleWidth: 0, flexibleHeight: 0);
            }

            if (mark.ShowTag && !string.IsNullOrEmpty(mark.Tag))
            {
                var tag = UIFactory.CreateLabel(row, name + "Tag", mark.Tag, TextAnchor.MiddleLeft);
                tag.fontSize = UIStyles.FontSizeHint;
                tag.color = UIStyles.TextMuted;

                // Never wraps: two letters that fold onto a second line take the row's height with
                // them. Same rule as the scope strip's words.
                tag.horizontalOverflow = HorizontalWrapMode.Overflow;
                tag.verticalOverflow = VerticalWrapMode.Overflow;

                UIFactory.SetLayoutElement(tag.gameObject, minHeight: FlagHeight,
                                           flexibleWidth: 0, flexibleHeight: 0);
            }

            return row;
        }
    }
}
