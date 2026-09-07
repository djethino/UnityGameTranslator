namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A line that says how something went — "Testing…", "Connected!", "Failed - check your key".
    ///
    /// The couple `SetDynamicText(label, "…"); label.color = UIStyles.StatusX;` appeared about a
    /// hundred and ten times across twelve panels. Here it is one call, and the tone is a role.
    /// </summary>
    public sealed class StatusLine
    {
        private readonly LabelHandle _label;

        private StatusLine(LabelHandle label) { _label = label; }

        /// <summary>Create the line. Empty until something is said.</summary>
        public static StatusLine Create(Host parent, string name, bool centred = true)
        {
            return new StatusLine(Labels.Status(parent, name, centred));
        }

        /// <summary>Say an English sentence in a tone, translated as it is written.</summary>
        public void Say(string english, Tone tone = Tone.Plain)
        {
            _label.Say(english);
            _label.Tone = tone;
            _label.Visible = true;
        }

        /// <summary>Show text that is already composed — a translated fragment plus data.</summary>
        public void Show(string text, Tone tone = Tone.Plain)
        {
            _label.Show(text);
            _label.Tone = tone;
            _label.Visible = true;
        }

        /// <summary>Say nothing. The line keeps its room unless <paramref name="collapse"/> hides it.</summary>
        public void Clear(bool collapse = false)
        {
            _label.Show("");
            if (collapse) _label.Visible = false;
        }

        /// <summary>The label itself, for a hint or for placing.</summary>
        public LabelHandle Label => _label;

        public bool Visible
        {
            get => _label.Visible;
            set => _label.Visible = value;
        }
    }
}
