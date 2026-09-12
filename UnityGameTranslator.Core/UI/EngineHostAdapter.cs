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

        public void ShuttingDown()
        {
            // Streams first (background tasks holding HTTP connections), then the live edit
            // session server-side — bounded wait, and before the engine disposes its client.
            try { TranslatorUIManager.StopSyncWatch(); } catch { }
            try { TranslatorUIManager.StopMergeCompletionListener(); } catch { }
            try { TranslatorUIManager.EndEditSessionOnShutdown(); } catch { }
        }
    }
}
