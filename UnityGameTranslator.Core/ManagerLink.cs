using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Whether the Manager is on this machine, and how to reach it.
    ///
    /// Offered beside the mod's own update, never instead of it: the direct download stays exactly
    /// where it was, for whoever prefers to drop a zip in by hand. This adds the other way — the
    /// tool that installs and updates the mod without the player doing any of it — because from now
    /// on the two ship together and a new player has no way of learning that it exists.
    ///
    /// 🔴 **Nothing here reaches the network.** The address of the Manager's latest release is
    /// compiled in (PluginInfo.ManagerReleaseUrl) and GitHub resolves "latest" itself, so this never
    /// goes stale and never costs a request. A mod inside a game must not spend a call to decide
    /// what a button says.
    /// </summary>
    public static class ManagerLink
    {
        /// <summary>The Manager's executable, without the extension the platform decides.</summary>
        private const string ProcessName = "UnityGameTranslatorManager";

        /// <summary>
        /// Where the Manager records having installed itself: its own settings folder, which is a
        /// fixed path both programs compose the same way.
        ///
        /// ⚠ The record holds the ABSOLUTE path of the executable, so this never guesses at a
        /// "default directory" — the Manager can be installed anywhere and says where.
        /// </summary>
        private const string InstallationFile = "installation.json";

        private static bool _looked;
        private static string _executable;

        /// <summary>
        /// The Manager's executable when it can be reached, or null.
        ///
        /// ⚠ Looked for ONCE. The overlay that shows this refreshes on a timer, and enumerating
        /// processes several times a second to decide the wording of a button would be work nobody
        /// asked for, in a game, while somebody is playing.
        /// </summary>
        public static string Executable
        {
            get
            {
                if (!_looked)
                {
                    _looked = true;
                    _executable = Find();
                }

                return _executable;
            }
        }

        /// <summary>True when pressing the button opens the Manager rather than a web page.</summary>
        public static bool IsOnThisMachine => !string.IsNullOrEmpty(Executable);

        /// <summary>
        /// Forgets what was found, so the next read looks again.
        ///
        /// For the one moment it changes underneath us: somebody installs the Manager while the game
        /// is open, comes back, and the button should stop offering to fetch what they now have.
        /// </summary>
        public static void Forget()
        {
            _looked = false;
            _executable = null;
        }

        /// <summary>
        /// Opens the Manager, or the page to get it from.
        ///
        /// ⚠ **Launching it while it is already running is not a mistake, it is the point.** The
        /// Manager holds a single-instance lock: a second launch raises the window that is already
        /// open and then ends by itself. That is what makes this work for a portable copy, which
        /// registers nothing anywhere and can only be found by the process it is running as.
        /// </summary>
        public static void Open()
        {
            var executable = Executable;

            if (string.IsNullOrEmpty(executable))
            {
                TranslatorCore.OpenUrlSafe(PluginInfo.ManagerReleaseUrl);
                return;
            }

            if (!TranslatorCore.LaunchSafe(executable))
            {
                // It was there a moment ago and will not start now — removed since, or refused by
                // the system. The page is never wrong, so it is what somebody gets instead of a
                // button that did nothing.
                Forget();
                TranslatorCore.OpenUrlSafe(PluginInfo.ManagerReleaseUrl);
            }
        }

        /// <summary>
        /// The two ways the Manager can be found, in order of certainty.
        ///
        /// ⚠ Neither of them is a search of the disk. A path guessed by walking Program Files is a
        /// path that finds the wrong thing the day somebody has two copies.
        /// </summary>
        private static string Find()
        {
            var installed = FromInstallationRecord();
            if (!string.IsNullOrEmpty(installed)) return installed;

            return FromRunningProcess();
        }

        /// <summary>
        /// What the Manager wrote when it installed itself, if it ever did.
        ///
        /// ⚠ The file existing is not enough: it describes an installation, and somebody can delete
        /// the folder by hand. The Manager applies the same rule to its own record — "a receipt
        /// describing an executable that is no longer there describes nothing".
        /// </summary>
        private static string FromInstallationRecord()
        {
            var record = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnityGameTranslator", "Manager", InstallationFile);

            if (!File.Exists(record)) return null;

            try
            {
                var executable = (string)JObject.Parse(File.ReadAllText(record))["executable"];

                return !string.IsNullOrEmpty(executable) && File.Exists(executable)
                    ? executable
                    : null;
            }
            catch (Exception ex)
            {
                // A file we did not write correctly, or one somebody edited. Reported rather than
                // swallowed — this is the boundary with another program's data, and silence here
                // would turn a broken record into "the Manager is not installed", for ever.
                TranslatorCore.LogWarning(
                    "[Manager] Could not read the Manager's installation record: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// A Manager already running, which is the only way a PORTABLE copy can be found: it
        /// installs nothing and records nothing, by definition.
        ///
        /// Its own path is then enough — launching it raises the window it already has.
        /// </summary>
        private static string FromRunningProcess()
        {
            try
            {
                var running = System.Diagnostics.Process.GetProcessesByName(ProcessName);

                foreach (var process in running)
                {
                    using (process)
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                    }
                }
            }
            catch (Exception ex)
            {
                // Enumerating processes is a request to the system, and it is allowed to say no —
                // a stripped runtime, a sandbox, a platform without the notion. Not being able to
                // look is an ordinary answer here, and the button simply offers the page instead.
                TranslatorCore.LogInfo(
                    "[Manager] Could not look for a running Manager: " + ex.Message);
            }

            return null;
        }
    }
}
