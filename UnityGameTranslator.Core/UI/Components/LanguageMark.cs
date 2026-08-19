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
        internal const int FlagHeight = 11;

        /// <summary>
        /// How wide a flag comes out at <see cref="FlagHeight"/>, for callers that have to reserve
        /// the space themselves — a row anchored by hand rather than laid out by a layout group.
        /// </summary>
        internal static int FlagWidth =>
            Mathf.RoundToInt(FlagHeight * (float)Flags.Width / Flags.Height);

        /// <summary>Between the flag and the tag beside it.</summary>
        private const int Gap = 4;

        /// <summary>
        /// Build the mark for a language into <paramref name="parent"/>, and return it.
        ///
        /// Returns null when the language is unknown — the caller then writes its name and nothing
        /// else, which is the truth rather than a placeholder.
        /// </summary>
        /// <param name="withName">
        /// Write the language's name after the flag.
        ///
        /// 🔴 **Then the tag chip disappears**, and the socle decides that rather than this file:
        /// the chip answers "which language is this flag", and the name answers it better. A row
        /// reading "🇬🇧 → 🇫🇷 English → French" said everything twice, which is what this replaces.
        /// </param>
        /// <param name="nameColour">
        /// The colour of the written name. Null takes the ordinary one — a list that dims its
        /// unselected rows passes its own, so the flag's row reads like every other row of that
        /// list rather than like a brighter exception.
        /// </param>
        /// <param name="nameElsewhere">
        /// The name is written by the CALLER, next to this mark rather than inside it.
        ///
        /// ⚠ Distinct from <paramref name="withName"/>, and both silence the tag chip: what the
        /// chip answers — which language is this flag — a name answers, wherever that name is. The
        /// selected line of a picker writes it in its own label, so the mark draws the flag alone
        /// and must still not add a chip.
        /// </param>
        public static GameObject Create(GameObject parent, string name, string languageName,
                                        bool withName = false, Color? nameColour = null,
                                        bool nameElsewhere = false)
        {
            var mark = Flags.Mark(languageName, nameIsWritten: withName || nameElsewhere);
            if (mark.Flag == null && string.IsNullOrEmpty(mark.Tag)
                && !(withName && !string.IsNullOrEmpty(languageName))) return null;

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

            if (withName && !string.IsNullOrEmpty(languageName))
            {
                var written = UIFactory.CreateLabel(row, name + "Name", languageName,
                                                    TextAnchor.MiddleLeft);
                written.fontSize = UIStyles.FontSizeNormal;
                written.color = nameColour ?? UIStyles.TextPrimary;

                // Never wraps: a name folded onto a second line takes the row's height with it.
                written.horizontalOverflow = HorizontalWrapMode.Overflow;
                written.verticalOverflow = VerticalWrapMode.Overflow;

                UIFactory.SetLayoutElement(written.gameObject, minHeight: FlagHeight,
                                           flexibleWidth: 0, flexibleHeight: 0);

                // ⚠ Language names are data, never translated — same rule as everywhere else.
                TranslatorCore.RegisterExcluded(written);
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
