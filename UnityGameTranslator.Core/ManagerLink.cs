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
        /// Where the Manager records having installed itself: its own settings folder.
        ///
        /// ⚠ The record holds the ABSOLUTE path of the executable, so this never guesses at a
        /// "default directory" — the Manager can be installed anywhere and says where.
        ///
        /// ⚠ The folder is NOT one path but the few the Manager may use — see <see cref="DataFolders"/>,
        /// which exists because assuming one was wrong on Linux for months.
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
            string record = null;

            foreach (var folder in DataFolders())
            {
                var candidate = Path.Combine(folder, InstallationFile);
                if (File.Exists(candidate)) { record = candidate; break; }
            }

            if (record == null) return null;

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
        /// The number the Manager drew for this machine, or null when there is none to read.
        ///
        /// 🔴 **The mod READS this and never writes it**, and that is the whole arrangement. Sent to
        /// the site, it puts every game on one machine into one group — which is what the account's
        /// "Linked devices" page could not do: thirty-six accesses were measured there on
        /// 2026-08-27, thirty-five of them in one heap nobody could sort, because the only grouping
        /// key was a name somebody has to type.
        ///
        /// ⚠ Writing it here would take from the mod the one property worth keeping — that it
        /// touches nothing outside the game's own folder. "Several games on one machine" is the
        /// Manager's business; one game is the mod's.
        ///
        /// ⚠ No Manager, no value, no automatic grouping — and no hole either: the account row now
        /// shows the code naming this access, so a machine can be recognised and named once by hand.
        ///
        /// ⚠ Not looked up once and cached like the executable: the Manager may be installed while
        /// the game is open, and this is read from disk far too rarely to be worth remembering.
        /// </summary>
        public static string DeviceId()
        {
            foreach (var folder in DataFolders())
            {
                try
                {
                    var path = Path.Combine(folder, "device.id");
                    if (!File.Exists(path)) continue;

                    var value = File.ReadAllText(path).Trim();

                    // Shape-checked here as well as where it was written: a half-written file or one
                    // somebody edited must not become a group of its own on the account, for ever.
                    if (IsMachineId(value)) return value;
                }
                catch (Exception ex)
                {
                    // The boundary with another program's data. Reported rather than swallowed:
                    // silence here would show up as "my games are not grouped" with no cause.
                    TranslatorCore.LogInfo(
                        "[Manager] Could not read the machine identifier: " + ex.Message);
                }
            }

            return null;
        }

        private static bool IsMachineId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32) return false;

            foreach (var c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }

            return true;
        }

        /// <summary>
        /// Every folder the Manager may keep its data in, most likely first.
        ///
        /// 🔴 **The comment above used to claim both programs composed this path "the same way", and
        /// on Linux they did not.** The mod looked in `~/.local/share/UnityGameTranslator/Manager/`
        /// while `LinuxPlatform.UserDataDirectory` writes `~/.local/share/unitygametranslator-manager/`.
        /// So an installed Manager was NEVER found there: the button offered the download page to
        /// somebody who already had it, and the only thing that ever worked was the running-process
        /// fallback — which needs the Manager to be open.
        ///
        /// ⚠ Invisible for months because the fallback hides it the moment the tool is running, and
        /// because development happens on Windows, where the two names happen to agree. Found on
        /// 2026-08-27 while checking where a shared machine identifier could live.
        ///
        /// ⚠ Both spellings are tried rather than one being picked: this file cannot reference the
        /// Manager's `IPlatform`, so agreement here is a convention, not a compiler check. Trying
        /// both is what keeps a rename on either side from silently breaking the link again.
        /// </summary>
        private static string[] DataFolders()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // $XDG_DATA_HOME wins on Linux when it is set, and SpecialFolder does not read it.
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var shareRoot = !string.IsNullOrEmpty(xdg) ? xdg : local;

            return new[]
            {
                Path.Combine(local, "UnityGameTranslator", "Manager"),
                Path.Combine(shareRoot, "unitygametranslator-manager"),
            };
        }

        /// <summary>
        /// A Manager already running, which is the only way a PORTABLE copy can be found: it
        /// records no INSTALLATION, by definition — it still keeps its settings beside one.
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
