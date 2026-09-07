using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>What a stack is painted on.</summary>
    public enum Surface
    {
        /// <summary>Nothing: the stack blends into whatever it sits on.</summary>
        None,
        /// <summary>A card: the main container, edged.</summary>
        Card,
        /// <summary>A raised block inside a card.</summary>
        Elevated,
        /// <summary>One row of a list.</summary>
        Item,
        /// <summary>The trough a list scrolls in.</summary>
        Trough,
        /// <summary>A field's fill.</summary>
        Input,
    }

    /// <summary>
    /// A container, vertical or horizontal — the one piece of vocabulary every panel used
    /// eighty-four times by hand: a layout group, its padding, its alignment, its fill, its
    /// background, spread over four to six UniverseLib calls each time.
    ///
    /// ⚠ Padding is set AFTER creation, through the layout group itself: the factory treats a
    /// zero colour as "no colour given" and puts its defaults back, and its Vector4 padding is
    /// easy to pass in the wrong order. Here the four numbers have names.
    /// </summary>
    public static class Stacks
    {
        /// <summary>Children one under the other.</summary>
        public static Host Vertical(Host parent, string name, int spacing = 0, Pad pad = default,
                                    Placement placement = Placement.TopLeft, Surface surface = Surface.None,
                                    Fill fill = Fill.Stretch, int? minHeight = null, bool fillHeight = false)
        {
            var obj = UIFactory.CreateVerticalGroup(parent.Object, name, false, false, true, true, spacing,
                                                    default, UIStyles.Transparent, Tones.Anchor(placement));
            Finish(obj, obj.GetComponent<VerticalLayoutGroup>(), pad, surface, fill, minHeight, fillHeight);
            return new Host(obj);
        }

        /// <summary>Children side by side.</summary>
        public static Host Horizontal(Host parent, string name, int spacing = 0, Pad pad = default,
                                      Placement placement = Placement.MiddleLeft, Surface surface = Surface.None,
                                      Fill fill = Fill.Stretch, int? minHeight = null, bool fillHeight = false)
        {
            var obj = UIFactory.CreateHorizontalGroup(parent.Object, name, false, false, true, true, spacing,
                                                      default, UIStyles.Transparent, Tones.Anchor(placement));
            Finish(obj, obj.GetComponent<HorizontalLayoutGroup>(), pad, surface, fill, minHeight, fillHeight);
            return new Host(obj);
        }

        /// <summary>
        /// A form row: the row every panel builds for a label, a field and a button — padded,
        /// middle-left, the standard height.
        /// </summary>
        public static Host Row(Host parent, string name, int spacing = 10, int? minHeight = null,
                               Placement placement = Placement.MiddleLeft)
        {
            return Horizontal(parent, name, spacing, Pad.Of(10, 5), placement,
                              minHeight: minHeight ?? UIStyles.RowHeightMedium);
        }

        /// <summary>A card, centred in its parent at the panel's card width, padded and edged.</summary>
        public static Host Card(Host parent, string name, int width = 420, bool stretchVertically = false)
        {
            return new Host(UIStyles.CreateAdaptiveCard(parent.Object, name, width, stretchVertically));
        }

        /// <summary>A section inside a card: no edge, its own padding.</summary>
        public static Host Section(Host parent, string name, int minHeight = 0)
        {
            return new Host(UIStyles.CreateSection(parent.Object, name, minHeight));
        }

        /// <summary>One row of a list: dense, on the item surface, edged in accent when selected.</summary>
        public static Host ListItem(Host parent, string name, bool selected = false, int? minHeight = null)
        {
            return new Host(UIStyles.CreateListItem(parent.Object, name, minHeight ?? 0, selected));
        }

        /// <summary>A fixed gap.</summary>
        public static Host Spacer(Host parent, int height, string name = "Spacer")
        {
            return new Host(UIStyles.CreateSpacer(parent.Object, height, name));
        }

        /// <summary>A gap that takes whatever is left — for centring what sits beside it.</summary>
        public static Host FlexSpacer(Host parent, string name = "Spacer")
        {
            return new Host(UIStyles.CreateFlexSpacer(parent.Object, name));
        }

        private static void Finish(GameObject obj, HorizontalOrVerticalLayoutGroup layout, Pad pad,
                                   Surface surface, Fill fill, int? minHeight, bool fillHeight)
        {
            UIFactory.SetLayoutElement(obj,
                minHeight: minHeight,
                flexibleWidth: fill == Fill.Stretch ? 9999 : (int?)0,
                flexibleHeight: fillHeight ? 9999 : (int?)0);

            if (layout != null)
            {
                layout.padding = Compat.MakeRectOffset(pad.Left, pad.Right, pad.Top, pad.Bottom);
                layout.childForceExpandWidth = false;
                layout.childForceExpandHeight = false;
            }

            switch (surface)
            {
                case Surface.Card:
                    UIStyles.SetBackground(obj, UIStyles.CardBackground);
                    UIFactory.AddBorder(obj, UIStyles.BorderSubtle);
                    break;
                case Surface.Elevated:
                    UIStyles.SetBackground(obj, UIStyles.CardElevated);
                    break;
                case Surface.Item:
                    UIStyles.SetBackground(obj, UIStyles.ItemBackground, UIFactory.Shapes.Small);
                    break;
                case Surface.Trough:
                    UIStyles.SetBackground(obj, UIStyles.TroughBackground);
                    break;
                case Surface.Input:
                    UIStyles.SetBackground(obj, UIStyles.InputBackground);
                    break;
                default:
                    // Transparent, and the padding we just set is kept: only the colour goes.
                    UIStyles.ClearRowBackground(obj, clearPadding: false);
                    break;
            }
        }
    }
}
