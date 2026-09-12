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
        void TranslationReloaded();

        /// <summary>The local file was written: a browser editor, if one is open, is to be told.</summary>
        void LocalFileChanged();

        /// <summary>The game is closing: stop every stream and end every session, briefly.</summary>
        void ShuttingDown();
    }
}
