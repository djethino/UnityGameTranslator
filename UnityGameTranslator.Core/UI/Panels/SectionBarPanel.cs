using System;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// The bar of sections of the one window (config `window_layout` = one): at the left edge of
    /// whichever section is up, as tall as it, so that Translation, Translation Tools and Mod Options
    /// read as one window with its sections on the left (analyse/fenetre-unique-sections.md).
    ///
    /// 🔴 **Not a screen.** It never takes the input on its own and the router does not know it: it is
    /// up exactly while a section is, in one window, and the manager places it there every frame
    /// (TranslatorUIManager.TickSectionBar). A press asks for a section (Intents.ShowSection); the
    /// router puts the section up in place of the other, its unapplied choices kept.
    /// </summary>
    public class SectionBarPanel : TranslatorPanelBase
    {
        /// <summary>The bar as a document — common/spec/screens/section-bar.json.</summary>
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("section-bar");

        private BuiltScreen _screen;
        private SectionBar _bar;
        private ButtonHandle _translationBtn, _toolsBtn, _optionsBtn;

        public override string Name => Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

        // Placed beside the section by the manager, never by hand, never remembered on its own.
        public override bool CanDragAndResize => false;
        protected override bool UseBackdrop => Doc.Backdrop;
        protected override bool PersistWindowPreferences => Doc.Persist;
        protected override bool UseDynamicSizing => false;
        // Its section's arrival is its own: the manager carries the section's scale and fade onto it.
        protected override bool ArrivesOnItsOwn => false;

        public SectionBarPanel(UIBase owner) : base(owner) { }

        protected override void ConstructPanelContent()
        {
            TitleBarHost.Visible = Doc.TitleBar;
            _screen = ScreenBuilder.Build(Doc, Content, Content, ActOf);

            _translationBtn = _screen.Button("TranslationBtn");
            _toolsBtn = _screen.Button("ToolsBtn");
            _optionsBtn = _screen.Button("OptionsBtn");
            _bar = SectionBar.Dress(_screen.Host("HeadRow"), _screen.Button("FoldBtn"), _screen.Host("BarStack"),
                new[] { _translationBtn, _toolsBtn, _optionsBtn },
                new[] { "section-translation", "section-tools", "section-options" });
            _bar.Folded = TranslatorCore.Config.window_preferences.sectionBarFolded;
        }

        /// <summary>What each verb the document asks for does.</summary>
        private Action ActOf(string act)
        {
            switch (act)
            {
                case "fold": return OnFoldClicked;
                case "showTranslation": return () => Intents.ShowSection(ScreenId.Main);
                case "showTools": return () => Intents.ShowSection(ScreenId.TranslationParameters);
                case "showOptions": return () => Intents.ShowSection(ScreenId.Options);
                default: return null;
            }
        }

        /// <summary>Folded or open: remembered for this person, like a window's place (window_preferences).</summary>
        private void OnFoldClicked()
        {
            _bar.Folded = !_bar.Folded;
            TranslatorCore.Config.window_preferences.sectionBarFolded = _bar.Folded;
            TranslatorCore.SaveConfig();
        }

        /// <summary>How wide the bar is right now: what the manager places it by.</summary>
        public float CurrentWidth => _bar != null ? _bar.Width : Doc.Width;

        /// <summary>
        /// The section up, and the unapplied choices each holds: the pressed button, and the count
        /// after a section's name (« Mod Options (3) », decided 2026-10-11).
        /// </summary>
        public void Show(ScreenId current, int toolsPending, int optionsPending)
        {
            if (_bar == null) return;
            _bar.Name(0, "This game");
            _bar.Name(1, toolsPending > 0 ? $"Translation Tools ({toolsPending})" : "Translation Tools");
            _bar.Name(2, optionsPending > 0 ? $"Mod Options ({optionsPending})" : "Mod Options");
            _bar.Current(current == ScreenId.Main ? 0 : current == ScreenId.TranslationParameters ? 1 : current == ScreenId.Options ? 2 : -1);
        }

        /// <summary>The head row as the section's title bar is: its height, its colour (SectionBar.MatchHead).</summary>
        public void MatchHead(Host titleBar, Host window) => _bar?.MatchHead(titleBar, window);

        public override void Update()
        {
            base.Update();
            if (Enabled) _bar?.PollName();
        }
    }
}
