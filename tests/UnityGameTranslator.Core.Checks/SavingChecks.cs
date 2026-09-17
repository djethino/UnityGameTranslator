using System;
using System.IO;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// How the translation file reaches the disk: whole or not at all, and never an older
    /// content over a newer one. Replayed on a real folder, because the defect these protect
    /// against — a file truncated by a crash mid-write, an out-of-order save — only exists at a
    /// moment in a sequence.
    /// </summary>
    internal static class SavingChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string folder = Path.Combine(Path.GetTempPath(), "ugt-saving-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "translations.json");

                // ── AtomicFile ──────────────────────────────────────────────
                AtomicFile.WriteAllText(path, "first");
                check(File.Exists(path) && File.ReadAllText(path) == "first",
                      "a first write creates the file",
                      "there was nothing to replace: the temp is moved into place");
                check(!File.Exists(path + AtomicFile.TempSuffix),
                      "no temp file is left behind after a first write",
                      "the temp is the write in progress; once committed it must be gone");

                AtomicFile.WriteAllText(path, "second");
                check(File.ReadAllText(path) == "second",
                      "a later write replaces the content whole",
                      "the file is replaced in one rename, never truncated and refilled");
                check(!File.Exists(path + AtomicFile.TempSuffix),
                      "no temp file is left behind after a replace",
                      "same as above, on the replace branch");

                // A crash between the temp write and the commit leaves a temp beside a whole
                // older file: the next write must overwrite that temp, and the file must have
                // stayed whole the entire time.
                File.WriteAllText(path + AtomicFile.TempSuffix, "half-written by a crash");
                check(File.ReadAllText(path) == "second",
                      "a temp left by a crash does not touch the file",
                      "what a reader sees is the last commit, not the interrupted write");
                AtomicFile.WriteAllText(path, "third");
                check(File.ReadAllText(path) == "third" && !File.Exists(path + AtomicFile.TempSuffix),
                      "the next write overwrites a stale temp and commits",
                      "a stale temp is not a lock; it is replaced like any temp");

                // ── SaveOrder ───────────────────────────────────────────────
                var order = new SaveOrder();
                int a = order.Take(), b = order.Take();
                check(a < b, "serials grow with each save taken", "the serial is the order the snapshots were taken in");
                check(order.MayWrite(b), "the newer save may write", "nothing more recent is on disk");
                check(!order.MayWrite(a), "the older save is refused after the newer one wrote",
                      "🔴 the tick's save finishing after an act's save would put older content back on disk");
                check(!order.MayWrite(b), "a save writes once", "the same serial cannot land twice");
                int c = order.Take();
                check(order.MayWrite(c), "a save taken after the write may write", "it is newer than what is on disk");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { /* a temp folder left behind proves nothing */ }
            }
        }
    }
}
