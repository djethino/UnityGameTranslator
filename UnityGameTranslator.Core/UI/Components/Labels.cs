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
        /// <param name="autoHeight">
        /// Grows to the height its text draws — a paragraph that wraps, a URL shown in full. Labels
        /// render with vertical overflow, so a wrapped line is otherwise drawn past its row, over
        /// whatever follows. <paramref name="minHeight"/> stays the floor. Anchored at its TOP, since
        /// that is where it grows from; pair it with <see cref="Fill.Stretch"/> so it has a width to
        /// wrap to.
        /// </param>
        /// <param name="minWidth">The least width it keeps — a heading that the note beside it must not squeeze. Null leaves it to its words.</param>
        /// <param name="align">
        /// Where the words sit inside the label, when neither left nor centred will do — a note
        /// right-aligned beside a heading. Null takes <paramref name="centred"/> and the role.
        /// Anything but a left placement stretches the label, for the same reason a centred one does.
        /// </param>
        public static LabelHandle Create(Host parent, string name, string text,
                                         TextRole role = TextRole.Body, Tone? tone = null,
                                         bool? centred = null, TextPolicy policy = TextPolicy.UiText,
                                         bool wrap = true, Fill fill = Fill.Content, int? minHeight = null,
                                         bool richText = true, bool autoHeight = false,
                                         int? minWidth = null, Placement? align = null)
        {
            var spec = Spec(role);
            bool centre = centred ?? spec.Centred;

            TextAnchor anchor = align.HasValue ? Tones.Anchor(align.Value)
                              : autoHeight
                                  ? (centre ? TextAnchor.UpperCenter : TextAnchor.UpperLeft)
                                  : (centre ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft);
            var label = UIFactory.CreateLabel(parent.Object, name, text ?? "", anchor,
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
            // A label with a minimum width has room to align its words in; one without would
            // align them inside a box exactly as wide as they are, so it is stretched instead.
            bool stretch = centre || fill == Fill.Stretch
                           || (align.HasValue && !IsLeft(align.Value) && !minWidth.HasValue);
            UIFactory.SetLayoutElement(label.gameObject, minWidth: minWidth, minHeight: minHeight ?? spec.MinHeight,
                                       flexibleWidth: stretch ? (int?)9999 : null);

            // The fitter reads the minHeight just set as its floor, then keeps the label at what
            // its text needs for the current width, plus a small breath below the last line.
            if (autoHeight) UIFactory.ConfigureAutoHeight(label, UIStyles.SmallSpacing);

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

        /// <summary>Whether the words start at the left edge — the one placement a content-wide label can honour.</summary>
        private static bool IsLeft(Placement placement)
        {
            return placement == Placement.TopLeft || placement == Placement.MiddleLeft || placement == Placement.BottomLeft;
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
                case TextRole.Caption:
                    return new RoleSpec { Size = UIStyles.FontSizeHint, Style = FontStyle.Normal, Tone = Tone.Muted, Centred = false, MinHeight = UIStyles.RowHeightSmall };
                case TextRole.Status:
                    return new RoleSpec { Size = UIStyles.FontSizeNormal, Style = FontStyle.Normal, Tone = Tone.Plain, Centred = true, MinHeight = UIStyles.RowHeightMedium };
                case TextRole.Code:
                    // ⚠ MinHeight below what the glyphs need on purpose: the row holding a code
                    // already states its own height (CodeDisplayHeight), and a label asking for
                    // as much again on top of the row's padding would push that row taller.
                    return new RoleSpec { Size = UIStyles.CodeDisplayFontSize, Style = FontStyle.Bold, Tone = Tone.Accent, Centred = true, MinHeight = UIStyles.LabelHeight };
                default:
                    return new RoleSpec { Size = UIStyles.FontSizeNormal, Style = FontStyle.Normal, Tone = Tone.Plain, Centred = false, MinHeight = UIStyles.RowHeightNormal };
            }
        }
    }
}
