using System.IO;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Writes a file so that whoever reads it sees the old whole or the new whole, never a
    /// half. The text goes to a sibling <c>.tmp</c> first; only once it is entirely on disk does
    /// it take the file's place, in one rename.
    ///
    /// ⚠ <c>File.WriteAllText</c> straight onto the file truncates it first and fills it after:
    /// a game killed between the two — a crash, a forced quit, a power cut — leaves the
    /// translation as a zero-length or half-written file, and the next launch reads nothing. The
    /// bigger the file, the wider that window. Pure: no Unity, no state, no clock, so the
    /// Core.Checks replay it on a real folder.
    /// </summary>
    public static class AtomicFile
    {
        public const string TempSuffix = ".tmp";

        public static void WriteAllText(string path, string text)
        {
            string tmp = path + TempSuffix;
            File.WriteAllText(tmp, text);
            Commit(tmp, path);
        }

        /// <summary>Puts a fully written temp file in the place of <paramref name="path"/>.</summary>
        public static void Commit(string tmp, string path)
        {
            // Replace keeps the target's identity and is atomic where the platform can make it
            // so; it refuses when there is nothing to replace, hence the first-write branch.
            // Metadata errors are ignored on purpose: ACLs and ownership are not what is being
            // protected here, and a refusal over them would leave the new content in the temp.
            if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
    }
}
