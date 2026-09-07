using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>Buttons of which exactly one is chosen.</summary>
    public sealed class ChoiceHandle : Handle
    {
        private readonly GameObject _row;
        private readonly List<ButtonHandle> _buttons;
        private int _selected;
        private Action<int> _onChosen;

        internal ChoiceHandle(GameObject row, List<ButtonHandle> buttons, int initial, Action<int> onChosen)
        {
            _row = row;
            _buttons = buttons;
            _onChosen = onChosen;
            _selected = -1;
            Selected = initial;
        }

        internal override GameObject Object => _row;

        /// <summary>Which one is chosen. Setting it from code restyles and does NOT call back.</summary>
        public int Selected
        {
            get => _selected;
            set
            {
                if (value < 0 || value >= _buttons.Count || value == _selected) return;
                _selected = value;
                for (int i = 0; i < _buttons.Count; i++)
                {
                    _buttons[i].Tone = i == value ? ButtonTone.Primary : ButtonTone.Secondary;
                    var label = _buttons[i].Text;
                    if (label != null) label.Bold = i == value;
                }
            }
        }

        internal void Choose(int index)
        {
            if (index == _selected) return;
            Selected = index;
            _onChosen?.Invoke(index);
        }

        public bool Enabled
        {
            set { foreach (var b in _buttons) b.Enabled = value; }
        }

        /// <summary>
        /// One option's own button — to attach a help text distinct from its neighbour's, or
        /// describe one option on its own. Null outside the range.
        /// </summary>
        public ButtonHandle Option(int index) => index >= 0 && index < _buttons.Count ? _buttons[index] : null;
    }

    /// <summary>
    /// A choice between a few words — Use Local / Use Server, Online / Offline. The merge screen
    /// and the wizard each restyled two buttons by hand to say which one was chosen.
    /// </summary>
    public static class Choices
    {
        /// <param name="onChosen">The person chose. Not called when <see cref="ChoiceHandle.Selected"/> is set from code.</param>
        /// <param name="initial">The option chosen at creation; -1 for none yet.</param>
        /// <param name="spacing">Between two options.</param>
        /// <param name="minWidth">Of each option; null takes the size's own.</param>
        public static ChoiceHandle Create(Host parent, string name, string[] options, int initial = 0,
                                          Action<int> onChosen = null, ButtonSize size = ButtonSize.Compact,
                                          int spacing = 4, int? minWidth = null)
        {
            var row = Stacks.Horizontal(parent, name, spacing: spacing, placement: Placement.MiddleLeft,
                                        fill: Fill.Content);
            var buttons = new List<ButtonHandle>();
            ChoiceHandle handle = null;

            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var button = Buttons.Create(row, name + i, options[i], ButtonTone.Secondary, size, minWidth);
                button.Clicked += () => handle?.Choose(index);
                buttons.Add(button);
            }

            handle = new ChoiceHandle(row.Object, buttons, initial, onChosen);
            return handle;
        }
    }
}
