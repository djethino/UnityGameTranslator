using System;
using System.IO;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// A file of the mod's that is not what it should be any more — a translation file whose JSON is
    /// damaged — renamed beside itself, so the work in it is kept for repair and a new file can start.
    ///
    /// ⚠ Pure by contract (System.IO only): linked into Core.Checks, which replays it on real files.
    /// </summary>
    internal static class DamagedFiles
    {
        /// <summary>
        /// Renames <paramref name="path"/> to <c>&lt;path&gt;.damaged-&lt;yyyyMMdd-HHmmss&gt;</c>, taken a
        /// second further while that name is used. Returns the new full path, or null with
        /// <paramref name="error"/> when the file could not be moved (it is then left where it is).
        /// </summary>
        internal static string SetAside(string path, DateTime now, out string error)
        {
            error = null;
            for (int extra = 0; extra < 60; extra++)
            {
                string aside = path + ".damaged-" + now.AddSeconds(extra).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                if (File.Exists(aside)) continue;
                try
                {
                    File.Move(path, aside);
                    return aside;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    error = ex.Message;
                    return null;
                }
            }
            error = "every name for it within a minute is taken";
            return null;
        }
    }
}
