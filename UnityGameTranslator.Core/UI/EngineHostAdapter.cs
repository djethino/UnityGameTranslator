using System;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// The interface as the engine's host: each thing the engine says lands on the manager's
    /// existing services. The one file that turns an engine fact into a screen.
    /// </summary>
    internal sealed class EngineHostAdapter : IEngineHost
    {
        public bool IsReady => TranslatorUIManager.IsInitialized;

        public void RunOnMainThread(Action action) => TranslatorUIManager.RunOnMainThread(action);

        public void RunLater(float seconds, Action action) => TranslatorUIManager.RunDelayed(seconds, action);

        public void Warn(string message)
            => TranslatorUIManager.RunOnMainThread(() => Intents.Toast(message, ToastTone.Off));

        public void TranslationReloaded() => TranslatorUIManager.NotifyTranslationReloaded();

        public void LocalFileChanged() => TranslatorUIManager.NotifyLocalFileChanged();

        public void DerivedFontRewritten(string fontName)
        {
            string interfaceFont = TranslatorCore.EffectiveInterfaceFont;
            if (!string.IsNullOrEmpty(interfaceFont)
                && string.Equals(UnityGameTranslator.Common.FontReferences.Name(interfaceFont), fontName, StringComparison.OrdinalIgnoreCase))
                TranslatorUIManager.ApplyInterfaceFont();
        }

        public void ShuttingDown()
        {
            // Streams first (background tasks holding HTTP connections), then the live edit
            // session server-side — bounded wait, and before the engine disposes its client.
            // Each step on its own, so one that fails does not keep the next from running — and each
            // failure said.
            try { TranslatorUIManager.StopSyncWatch(); }
            catch (Exception ex) { Faults.Say("EngineHostAdapter.ShuttingDown sync watch", ex); }
            try { TranslatorUIManager.StopMergeCompletionListener(); }
            catch (Exception ex) { Faults.Say("EngineHostAdapter.ShuttingDown merge listener", ex); }
            try { TranslatorUIManager.EndEditSessionOnShutdown(); }
            catch (Exception ex) { Faults.Say("EngineHostAdapter.ShuttingDown edit session", ex); }
        }
    }
}
