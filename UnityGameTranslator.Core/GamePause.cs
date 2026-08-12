using System;
using System.IO;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Freezes the game while the mod's interface is open, without freezing the interface.
    /// </summary>
    /// <remarks>
    /// Distinct from the input capture: that one stops the game from RECEIVING, this one stops it
    /// from ADVANCING. Rendering and raycasts keep running at timeScale 0, so highlighting and
    /// picking still work — which is the whole point of the feature.
    ///
    /// ⚠ Time.set_timeScale is InternalCall, so unlike Cursor.lockState it cannot be patched: a
    /// game writing its own timeScale each frame simply wins. Hence rewriting it every frame
    /// rather than once, and hence an option that honestly says it may do nothing at all on some
    /// games. See analyse/pause-the-game-feasibility.md.
    /// </remarks>
    public static class GamePause
    {
        /// <summary>True while we are holding the game frozen.</summary>
        public static bool Active { get; private set; }

        // What the game's own timeScale was when we froze it. Restored verbatim — never 1, which
        // would break a game that was itself in slow motion or already paused in its own menu.
        private static float _restoreTo = 1f;

        // Set once the game has been seen fighting back, so the option can say so instead of
        // looking broken.
        private static bool _overridden;

        /// <summary>True when this game overwrites timeScale itself, so the pause cannot hold.</summary>
        public static bool GameFightsBack { get { return _overridden; } }

        /// <summary>
        /// Freeze, or keep freezing. Called every frame while the interface holds the game.
        /// </summary>
        public static void Engage()
        {
            if (!Active)
            {
                _restoreTo = Time.timeScale;
                // Already stopped — the game is in its own pause menu. Nothing to do, and nothing
                // to restore later either: leaving _restoreTo at 0 is correct.
                Active = true;
                _overridden = false;
                LogDebugState("engaged");
            }
            else if (Time.timeScale != 0f)
            {
                // The game put its own value back. Say it once: an option that appears to do
                // nothing is worse than one that explains why.
                if (!_overridden)
                {
                    _overridden = true;
                    TranslatorCore.LogInfo("[Pause] This game sets its own time scale — the pause cannot be held here.");
                }
            }

            // Every frame, not once: without a patchable setter this is the only way to hold it.
            if (Time.timeScale != 0f)
                Time.timeScale = 0f;
        }

        /// <summary>Give the game its own time back. Safe to call when not frozen.</summary>
        public static void Release()
        {
            if (!Active)
                return;

            Active = false;
            Time.timeScale = _restoreTo;
            LogDebugState("released");
        }

        private static void LogDebugState(string what)
        {
            TranslatorCore.LogDebug($"[Pause] {what} (restore to {_restoreTo:0.##})");
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Anti-cheat detection — for greying the option out, with the reason
        // ─────────────────────────────────────────────────────────────────────────────

        private static bool _antiCheatChecked;
        private static string _antiCheatName;

        /// <summary>
        /// The anti-cheat guarding this game, or null. Detected once, by looking for the files
        /// these systems install next to the executable.
        /// </summary>
        /// <remarks>
        /// ⚠ Altering timeScale is a textbook speedhack signature. The Manager already refuses to
        /// install on protected games; the mod cannot refuse to run, but it can refuse to offer
        /// this. Deliberately file-based and conservative: a false positive costs one greyed-out
        /// option, a false negative could cost somebody their account.
        /// </remarks>
        public static string AntiCheat
        {
            get
            {
                if (_antiCheatChecked)
                    return _antiCheatName;

                _antiCheatChecked = true;
                try { _antiCheatName = DetectAntiCheat(); }
                catch (Exception e) { TranslatorCore.LogDebug($"[Pause] anti-cheat probe failed: {e.Message}"); }
                return _antiCheatName;
            }
        }

        private static string DetectAntiCheat()
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return null;

            // Name → the marks it leaves in a game folder.
            var known = new[]
            {
                new[] { "Easy Anti-Cheat", "EasyAntiCheat", "EasyAntiCheat_EOS", "easyanticheat_x64.dll" },
                new[] { "BattlEye", "BattlEye", "BEService.exe", "BEClient_x64.dll" },
                new[] { "nProtect GameGuard", "GameGuard", "GameMon.des" },
                new[] { "Denuvo Anti-Cheat", "AntiCheat", "denuvo-anti-cheat.sys" },
            };

            foreach (var entry in known)
            {
                for (int i = 1; i < entry.Length; i++)
                {
                    string mark = Path.Combine(root, entry[i]);
                    if (Directory.Exists(mark) || File.Exists(mark))
                        return entry[0];
                }
            }

            return null;
        }
    }
}
