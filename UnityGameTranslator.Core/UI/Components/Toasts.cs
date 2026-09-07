using UnityEngine;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A short-lived, single-line banner: white bold text over one of three fixed colours — its
    /// own small palette, distinct from the four notification tones <see cref="Callout"/> reads,
    /// since a toast replaces every other box on screen rather than sitting beside them.
    /// </summary>
    public sealed class Toasts
    {
        private readonly Host _box;
        private readonly LabelHandle _label;

        private Toasts(Host box, LabelHandle label)
        {
            _box = box;
            _label = label;
        }

        /// <summary>The box itself.</summary>
        public Host Handle => _box;

        public bool Visible
        {
            get => _box.Visible;
            set => _box.Visible = value;
        }

        public static Toasts Create(Host parent, string name)
        {
            var box = Stacks.Vertical(parent, name, spacing: 0, pad: new Pad(12, 12, 8, 8),
                                      minHeight: UIStyles.RowHeightLarge);

            var label = Labels.Create(box, name + "Label", "", TextRole.SectionTitle,
                                      centred: true, policy: TextPolicy.Excluded,
                                      minHeight: UIStyles.RowHeightMedium);
            // Always white, whatever the tone: only the background carries the on/off/info read.
            label.Text.color = Color.white;

            box.Visible = false;
            return new Toasts(box, label);
        }

        /// <summary>Success and Error read as an ON/OFF flip; anything else as neutral info.</summary>
        public void Show(string message, Tone tone)
        {
            UIStyles.SetBackground(_box.Object, Fill(tone));
            _label.Show(message);
            _box.Visible = true;
        }

        private static Color Fill(Tone tone)
        {
            switch (tone)
            {
                case Tone.Success: return UIStyles.ToastSuccessBg;
                case Tone.Error: return UIStyles.ToastErrorBg;
                default: return UIStyles.ToastInfoBg;
            }
        }
    }
}
