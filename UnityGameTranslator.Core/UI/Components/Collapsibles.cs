using System;
using UnityEngine;
using UnityEngine.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>A block that folds under its title.</summary>
    public sealed class Collapsible
    {
        private readonly GameObject _container;
        private readonly GameObject _content;
        private readonly Text _icon;
        private bool _expanded;
        private Action<bool> _onToggled;

        private Collapsible(GameObject container, GameObject content, Text icon, bool expanded, Action<bool> onToggled)
        {
            _container = container;
            _content = content;
            _icon = icon;
            _expanded = expanded;
            _onToggled = onToggled;
        }

        /// <summary>Where the folded content goes.</summary>
        public Host Body => new Host(_content);

        /// <summary>The block, title included.</summary>
        public Host Handle => new Host(_container);

        /// <summary>Open or folded. Setting it does not call back; the person's click does.</summary>
        public bool Expanded
        {
            get => _expanded;
            set
            {
                _expanded = value;
                UIStyles.SetCollapsibleState(_icon, _content, value);
            }
        }

        /// <summary>
        /// Create one. The header's click is wired here, not left to the caller: two panels
        /// wired it by hand, one of them after fetching the header's Button by component.
        /// </summary>
        /// <param name="onToggled">The person opened or folded it — a panel resizes itself then.</param>
        public static Collapsible Create(Host parent, string name, string title, bool expanded = true,
                                         Action<bool> onToggled = null)
        {
            var (container, header, icon, titleLabel, content) =
                UIStyles.CreateCollapsibleSection(parent.Object, name, title, expanded);
            TranslatorCore.RegisterUIText(titleLabel);
            TranslatorCore.RegisterExcluded(icon);

            var collapsible = new Collapsible(container, content, icon, expanded, onToggled);

            var button = header.GetComponent<Button>();
            UIHelpers.AddButtonListener(button, () =>
            {
                collapsible.Expanded = !collapsible.Expanded;
                collapsible._onToggled?.Invoke(collapsible.Expanded);
            });

            return collapsible;
        }
    }
}
