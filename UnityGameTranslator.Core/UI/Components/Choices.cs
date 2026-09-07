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
    }

    /// <summary>
    /// A choice between a few words — Use Local / Use Server, Online / Offline. The merge screen
    /// and the wizard each restyled two buttons by hand to say which one was chosen.
    /// </summary>
    public static class Choices
    {
        /// <param name="onChosen">The person chose. Not called when <see cref="ChoiceHandle.Selected"/> is set from code.</param>
        public static ChoiceHandle Create(Host parent, string name, string[] options, int initial = 0,
                                          Action<int> onChosen = null, ButtonSize size = ButtonSize.Compact)
        {
            var row = Stacks.Horizontal(parent, name, spacing: 4, placement: Placement.MiddleLeft,
                                        fill: Fill.Content);
            var buttons = new List<ButtonHandle>();
            ChoiceHandle handle = null;

            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var button = Buttons.Create(row, name + i, options[i], ButtonTone.Secondary, size);
                button.Clicked += () => handle?.Choose(index);
                buttons.Add(button);
            }

            handle = new ChoiceHandle(row.Object, buttons, initial, onChosen);
            return handle;
        }
    }
}
