using System;
using System.IO;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>A damaged translation file set aside on disk (Engine/DamagedFiles), replayed on real files.</summary>
    internal static class DamagedFilesChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string folder = Path.Combine(Path.GetTempPath(), "ugt-damaged-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string file = Path.Combine(folder, "translations.json");
                File.WriteAllText(file, "{ \"_uuid\": \"x\", \"Hello\": { \"v\": \"Bonj");   // cut mid-write
                var now = new DateTime(2026, 10, 22, 14, 3, 9);

                string aside = DamagedFiles.SetAside(file, now, out string error);
                check(aside != null && !File.Exists(file) && File.Exists(aside)
                      && File.ReadAllText(aside).EndsWith("Bonj", StringComparison.Ordinal),
                    "a damaged translation file is moved aside whole, and its place is free",
                    $"aside {aside ?? "null"} ({error}): the work in it must survive the new file that starts in its place");
                check(aside != null && Path.GetFileName(aside) == "translations.json.damaged-20261022-140309",
                    "it keeps its name with the moment it was set aside", Path.GetFileName(aside ?? ""));

                // The same second again: the first copy set aside is never overwritten.
                File.WriteAllText(file, "{ broken again");
                string second = DamagedFiles.SetAside(file, now, out _);
                check(second != null && second != aside && File.Exists(aside) && File.Exists(second),
                    "a second damaged file in the same second takes the next name, the first one kept",
                    $"{Path.GetFileName(second ?? "null")} beside {Path.GetFileName(aside ?? "null")}");

                // Nothing to move: said, nothing invented.
                string none = DamagedFiles.SetAside(Path.Combine(folder, "missing.json"), now, out string missing);
                check(none == null && !string.IsNullOrEmpty(missing),
                    "a file that cannot be moved is left where it is, and the reason is given", missing ?? "no reason");
            }
            finally
            {
                try { Directory.Delete(folder, true); }
                catch (IOException ex) { Console.WriteLine($"  (temp folder left: {ex.Message})"); }
            }
        }
    }
}
