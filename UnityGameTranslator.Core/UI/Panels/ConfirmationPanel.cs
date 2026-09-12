using System;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Reusable confirmation dialog for destructive actions.
    ///
    /// 🔴 **The first screen described in data** (2026-09-12): its shape is
    /// <c>common/spec/screens/confirm.json</c>, embedded in the assembly and built by
    /// <see cref="ScreenBuilder"/>. What is left here is what a document cannot say — three slots
    /// written at show time, two acts, and one decision: the verb's tone. A Core on another engine
    /// draws the same file with its own builder and keeps exactly this much code.
    /// </summary>
    public class ConfirmationPanel : TranslatorPanelBase
    {
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("confirm");

        public override string Name => Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

        protected override int MinPanelHeight => Doc.MinHeight;
        protected override bool PersistWindowPreferences => Doc.Persist;
        protected override bool UseBackdrop => Doc.Backdrop;

        private BuiltScreen _screen;
        private Action _onConfirm;
        private Action _onCancel;

        public ConfirmationPanel(UIBase owner) : base(owner)
        {
        }

        /// <summary>
        /// Shows the confirmation dialog with custom message and callbacks.
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="message">Message to display</param>
        /// <param name="confirmText">Text for confirm button (e.g., "Delete", "Logout")</param>
        /// <param name="onConfirm">Action to execute on confirm</param>
        /// <param name="onCancel">Optional action to execute on cancel</param>
        /// <param name="isDanger">If true, confirm button is styled as danger (red)</param>
        public void Show(
            string title,
            string message,
            string confirmText,
            Action onConfirm,
            Action onCancel = null,
            bool isDanger = true)
        {
            _screen.Say("title", title);
            _screen.Say("message", message);
            _screen.Say("verb", confirmText);
            _onConfirm = onConfirm;
            _onCancel = onCancel;

            // The one decision of this screen: a destructive verb reads as danger.
            _screen.Button("ConfirmBtn").Tone = isDanger ? ButtonTone.Danger : ButtonTone.Primary;

            SetActive(true);
        }

        protected override void ConstructPanelContent()
        {
            Layout(out var body, out var footer, Doc.CardWidth);
            _screen = ScreenBuilder.Build(Doc, body, footer, ActOf);
        }

        private Action ActOf(string act)
        {
            switch (act)
            {
                case "confirm": return OnConfirmClicked;
                case "cancel": return OnCancelClicked;
                default: return null;
            }
        }

        private void OnConfirmClicked()
        {
            SetActive(false);
            _onConfirm?.Invoke();
        }

        private void OnCancelClicked()
        {
            SetActive(false);
            _onCancel?.Invoke();
        }

        protected override void OnClosePanelClicked()
        {
            SetActive(false);
            _onCancel?.Invoke();
        }
    }
}
