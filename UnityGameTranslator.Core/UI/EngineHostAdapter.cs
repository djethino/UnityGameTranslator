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

        public void TranslationReloaded(TranslationReload what) => TranslatorUIManager.NotifyTranslationReloaded(what);

        public void TranslationSettingsReplaced()
            => TranslatorUIManager.TranslationParamsPanel?.RefreshFromTranslation(TranslationReload.Replaced);

        public void LocalFileChanged() => TranslatorUIManager.NotifyLocalFileChanged();

        public void DerivedFontRewritten(string fontName)
        {
            // A font the mod's window draws with, rewritten (new glyphs named as the translation
            // grows): only what draws from it is given the new copy. The interface's font → the whole
            // window. A source/target text font → the game's text it shows only (measured: redoing the
            // whole window at each rewrite swapped its font 16 times in one session); where that font
            // has no object of its own (a runtime that cannot make fonts) it lives in the window's
            // chain, so the window is done again.
            bool Names(GameTextSide? side)
            {
                string font = TranslatorCore.WindowFontFor(side);
                return !string.IsNullOrEmpty(font)
                    && string.Equals(UnityGameTranslator.Common.FontReferences.Name(font), fontName, StringComparison.OrdinalIgnoreCase);
            }
            bool sideFont = Names(GameTextSide.Source) || Names(GameTextSide.Target) || Names(GameTextSide.ObjectNames);
            if (Names(null) || (sideFont && !GameTextFonts.Forget(fontName)))
                TranslatorUIManager.ApplyInterfaceFont();
            else if (sideFont)
                GameTextFonts.PutAll();
        }

        public void SceneChanged()
        {
            // Only when the window draws with a game font somewhere — its interface, or the game's
            // text it shows: those come and go with the scenes; the others do not.
            foreach (var side in ModWindowText.Parts)
                if (FontManager.IsGameFontRef(TranslatorCore.WindowFontFor(side)))
                {
                    TranslatorUIManager.ApplyInterfaceFont();
                    return;
                }
        }

        public void GameFontReplaced(string gameFontName)
        {
            if (GameTextFonts.WaitsForGameFonts) GameTextFonts.PutAll();
        }

        public void FontAtlasRebuilt(string fontName) => TranslatorUIManager.WindowFontRebuilt(fontName);

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
