using System;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// What the engine asks of whoever hosts it — the interface, on this engine — and the only
    /// road from the engine to a screen.
    ///
    /// 🔴 **The engine names no panel, no manager, no window.** Until 2026-09-12 TranslatorCore
    /// and TranslatorPatches reached the interface directly at seven places: the shutdown stopped
    /// the manager's watchers, a reload told the manager, a save told the browser editor, a
    /// backend timeout wrote a toast, and the patches waited on the manager's initialisation to
    /// replay fonts. Each was a line the second Core would have had to rewrite, and none was
    /// visible to a check. Now the engine says what happened to its host; what the host does with
    /// it is the host's.
    ///
    /// ⚠ Pure by contract: no Unity, no state. The host is attached by the interface at start
    /// (<see cref="TranslatorCore.AttachHost"/>) and may be absent — the engine translates a game
    /// with no window at all — so every call goes through <c>Host?.</c>. The host's readiness is
    /// a fact the engine holds itself (<see cref="TranslatorCore.HostReady"/>), because the patches
    /// can fire before the host exists.
    /// </summary>
    /// <summary>
    /// What a reload of the translation file brought in — the answer a screen holding unapplied
    /// choices needs (user, 2026-10-02: the font and rule settings travel with the translation
    /// they were made on; a change of translation not applied first is a change not wanted).
    /// </summary>
    public enum TranslationReload
    {
        /// <summary>Another file in place of this one — downloaded, installed, put back from a
        /// backup: everything it carries besides its lines may differ.</summary>
        Replaced,

        /// <summary>Lines merged into this same translation (a browser edit, the Main's lines):
        /// its settings are the ones it had, written back unchanged.</summary>
        LinesOnly,
    }

    public interface IEngineHost
    {
        /// <summary>The interface is built and can be drawn on.</summary>
        bool IsReady { get; }

        /// <summary>Run this on the game's main thread; at once when already on it.</summary>
        void RunOnMainThread(Action action);

        /// <summary>Run this after a delay, on the main thread.</summary>
        void RunLater(float seconds, Action action);

        /// <summary>Something failed that the player should know about, in one sentence.</summary>
        void Warn(string message);

        /// <summary>The translation on disk was reloaded: what is on screen describes the previous file.</summary>
        void TranslationReloaded(TranslationReload what);

        /// <summary>
        /// Settings sections the translation carries (fonts, rules, exclusions…) were replaced from
        /// another file's — after a download, the arbitration of whose settings to keep.
        /// </summary>
        void TranslationSettingsReplaced();

        /// <summary>The local file was written: a browser editor, if one is open, is to be told.</summary>
        void LocalFileChanged();

        /// <summary>The game is closing: stop every stream and end every session, briefly.</summary>
        void ShuttingDown();

        /// <summary>
        /// A font's derived copy was rewritten with new shaped glyphs (DerivedFonts): the window's
        /// interface font, if it is that font, is to be applied again to draw them.
        /// </summary>
        void DerivedFontRewritten(string fontName);

        /// <summary>
        /// The game changed scene: fonts it loads or unloads with its scenes may have come or gone —
        /// a game font the window was asked to draw with is looked for again.
        /// </summary>
        void SceneChanged();

        /// <summary>
        /// A game legacy font received its replacement object (FontManager): the mod's window may be
        /// waiting for it to draw the game's text with, where it cannot make fonts.
        /// </summary>
        void GameFontReplaced(string gameFontName);
    }
}
