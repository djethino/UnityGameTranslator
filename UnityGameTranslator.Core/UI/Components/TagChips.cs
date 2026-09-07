using UnityEngine;
using UnityEngine.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>The letter of a line's tag on its coloured square — H, V, A, S, or a dash.</summary>
    public sealed class TagChipHandle : Handle
    {
        private readonly GameObject _chip;
        private readonly Text _letter;

        internal TagChipHandle(GameObject chip, Text letter)
        {
            _chip = chip;
            _letter = letter;
        }

        internal override GameObject Object => _chip;

        /// <summary>Retag: colour and letter together, never one alone.</summary>
        public void Retag(string tag) { UIStyles.SetTagChip(_chip, _letter, tag); }
    }

    public static class TagChips
    {
        public static TagChipHandle Create(Host parent, string tag)
        {
            var chip = UIStyles.CreateTagChip(parent.Object, tag, out Text letter);
            TranslatorCore.RegisterExcluded(letter);
            return new TagChipHandle(chip, letter);
        }
    }
}
