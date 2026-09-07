using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A piece of text with a role — the seven `Create*Label` helpers of UIStyles, and the 171
    /// raw labels the panels built beside them with a size, a style, a colour and a height each.
    ///
    /// The role decides size, weight, default tone, default alignment and minimum height; the
    /// policy decides who may write it. A panel says what a label IS and never how it is drawn.
    /// </summary>
    public static class Labels
    {
        /// <summary>
        /// Create a label.
        /// </summary>
        /// <param name="tone">Colour role; null takes the role's own.</param>
        /// <param name="centred">Null takes the role's own: titles, descriptions and status lines are centred.</param>
        /// <param name="wrap">False keeps the text on one line, overflowing rather than folding — a name, a tag, a code.</param>
        /// <param name="fill">Stretch to the row, or as wide as the text. A centred label is always stretched: centring a label that is only as wide as its words changes nothing.</param>
        /// <param name="minHeight">Null takes the role's own.</param>
        /// <param name="richText">Whether &lt;color&gt; and friends are interpreted. Off for anything a player or a file could have written.</param>
        public static LabelHandle Create(Host parent, string name, string text,
                                         TextRole role = TextRole.Body, Tone? tone = null,
                                         bool? centred = null, TextPolicy policy = TextPolicy.UiText,
                                         bool wrap = true, Fill fill = Fill.Content, int? minHeight = null,
                                         bool richText = true)
        {
            var spec = Spec(role);
            bool centre = centred ?? spec.Centred;

            var label = UIFactory.CreateLabel(parent.Object, name, text ?? "",
                                              centre ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft,
                                              supportRichText: richText);
            label.fontSize = spec.Size;
            label.fontStyle = spec.Style;
            label.color = Tones.Colour(tone ?? spec.Tone);

            if (!wrap)
            {
                label.horizontalOverflow = HorizontalWrapMode.Overflow;
                label.verticalOverflow = VerticalWrapMode.Overflow;
            }

            // ⚠ Null, not 0, when it is not stretched: SetLayoutElement's parameters are nullable
            // and null means "leave it alone". Writing 0 would turn a field nobody had set into an
            // override on every label in the mod.
            bool stretch = centre || fill == Fill.Stretch;
            UIFactory.SetLayoutElement(label.gameObject, minHeight: minHeight ?? spec.MinHeight,
                                       flexibleWidth: stretch ? (int?)9999 : null);

            switch (policy)
            {
                case TextPolicy.UiText: TranslatorCore.RegisterUIText(label); break;
                default: TranslatorCore.RegisterExcluded(label); break;
            }

            return new LabelHandle(label, policy);
        }

        /// <summary>A status line the code writes: Dynamic, centred, the standard row height.</summary>
        public static LabelHandle Status(Host parent, string name, bool centred = true)
        {
            return Create(parent, name, "", TextRole.Status, policy: TextPolicy.Dynamic, centred: centred);
        }

        private struct RoleSpec
        {
            internal int Size;
            internal FontStyle Style;
            internal Tone Tone;
            internal bool Centred;
            internal int MinHeight;
        }

        private static RoleSpec Spec(TextRole role)
        {
            switch (role)
            {
                case TextRole.Title:
                    return new RoleSpec { Size = UIStyles.FontSizeTitle, Style = FontStyle.Bold, Tone = Tone.Plain, Centred = true, MinHeight = UIStyles.TitleHeight };
                case TextRole.SectionTitle:
                    return new RoleSpec { Size = UIStyles.FontSizeSectionTitle, Style = FontStyle.Bold, Tone = Tone.Plain, Centred = false, MinHeight = UIStyles.SectionTitleHeight };
                case TextRole.Description:
                    return new RoleSpec { Size = UIStyles.FontSizeNormal, Style = FontStyle.Normal, Tone = Tone.Secondary, Centred = true, MinHeight = UIStyles.LabelHeight };
                case TextRole.Info:
                    return new RoleSpec { Size = UIStyles.FontSizeNormal, Style = FontStyle.Normal, Tone = Tone.Secondary, Centred = false, MinHeight = UIStyles.RowHeightNormal };
                case TextRole.Small:
                    return new RoleSpec { Size = UIStyles.FontSizeSmall, Style = FontStyle.Normal, Tone = Tone.Muted, Centred = false, MinHeight = UIStyles.RowHeightSmall };
                case TextRole.Hint:
                    return new RoleSpec { Size = UIStyles.FontSizeHint, Style = FontStyle.Italic, Tone = Tone.Muted, Centred = false, MinHeight = UIStyles.RowHeightSmall };
                case TextRole.Status:
                    return new RoleSpec { Size = UIStyles.FontSizeNormal, Style = FontStyle.Normal, Tone = Tone.Plain, Centred = true, MinHeight = UIStyles.RowHeightMedium };
                default:
                    return new RoleSpec { Size = UIStyles.FontSizeNormal, Style = FontStyle.Normal, Tone = Tone.Plain, Centred = false, MinHeight = UIStyles.RowHeightNormal };
            }
        }
    }
}
