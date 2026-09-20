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
        /// <param name="waiting">
        /// True while this line reports something IN FLIGHT — a code being asked for, a key being
        /// tested, a file coming down. The mark turns beside it until the next thing this line
        /// says, which is the answer.
        ///
        /// ⚠ The sentence still carries the meaning: the mark says "still going", never what is
        /// going on. A line that only turned would be a line that says nothing.
        /// </param>
        public void Say(string english, Tone tone = Tone.Plain, bool waiting = false)
        {
            _label.Say(english);
            _label.Tone = tone;
            _label.Visible = true;
            Waiting(waiting);
        }

        /// <summary>Show text that is already composed — a translated fragment plus data.</summary>
        /// <param name="waiting"><inheritdoc cref="Say" path="/param[@name='waiting']"/></param>
        public void Show(string text, Tone tone = Tone.Plain, bool waiting = false)
        {
            _label.Show(text);
            _label.Tone = tone;
            _label.Visible = true;
            Waiting(waiting);
        }

        /// <summary>Say nothing. The line keeps its room unless <paramref name="collapse"/> hides it.</summary>
        public void Clear(bool collapse = false)
        {
            _label.Show("");
            Waiting(false);
            if (collapse) _label.Visible = false;
        }

        /// <summary>
        /// Switches the turning mark on or off — the label's own, so a status line and a plain
        /// line that reports a scan show the same mark, made the same way.
        /// </summary>
        private void Waiting(bool on) => _label.Waiting = on;

        /// <summary>The label itself, for a hint or for placing.</summary>
        public LabelHandle Label => _label;

        public bool Visible
        {
            get => _label.Visible;
            set => _label.Visible = value;
        }
    }
}
