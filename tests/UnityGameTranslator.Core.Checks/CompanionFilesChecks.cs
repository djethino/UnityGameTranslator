using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The files beside a translation, across the two sequences that once went wrong on real
    /// folders: a fork dropping its ancestors, a backup asking which images to carry.
    ///
    /// 🔴 Both defects were of the kind no pure rule can see — a right rule on the wrong NAME. The
    /// fork deleted <c>translations.ancestor.json</c>, a file nobody writes, so the real ancestor
    /// stayed and the memory clear inside the same <c>if</c> never ran; the backup read
    /// <c>_images</c>, a key nobody writes, so every saved copy carried no image. The cases below
    /// write the actual files under a temporary folder and watch what goes and what stays.
    /// </summary>
    internal static class CompanionFilesChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "ugt-companions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                AForkDropsBothAncestors(check, Fresh(root));
                TheWrongSpellingIsNotWhatGoes(check, Fresh(root));
                NothingToDropIsNotAnError(check, Fresh(root));
                NoPathIsRefused(check);
                ImagesAreReadWhereTheFileWritesThem(check);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        private static string Fresh(string root)
        {
            string folder = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string Translation(string folder) => Path.Combine(folder, TranslationFiles.Name);

        // ── 🔴 The one a fork got wrong ────────────────────────────────────────
        private static void AForkDropsBothAncestors(Action<bool, string, string> check, string folder)
        {
            string translation = Translation(folder);
            File.WriteAllText(translation, "{ \"_uuid\": \"new\", \"Hello\": { \"v\": \"Bonjour\", \"t\": \"H\" } }");
            File.WriteAllText(TranslationFiles.AncestorOf(translation), "{ \"Hello\": { \"v\": \"Salut\", \"t\": \"A\" } }");
            File.WriteAllText(TranslationFiles.MainAncestorOf(translation), "{ \"Hello\": { \"v\": \"Salut\", \"t\": \"A\" } }");
            string before = File.ReadAllText(translation);

            int gone = CompanionFiles.DeleteAncestors(translation);

            check(gone == 2,
                "a fork drops both ancestors",
                "the lineage it left is nobody's baseline any more — not its own last sync, not the Main it merged from");

            check(!File.Exists(TranslationFiles.AncestorOf(translation))
                  && !File.Exists(TranslationFiles.MainAncestorOf(translation)),
                "neither ancestor is on disk afterwards",
                "a file left behind is reloaded at the next launch and counts the fork's lines against a stranger");

            check(File.Exists(translation) && File.ReadAllText(translation) == before,
                "the translation itself is untouched",
                "dropping baselines must never cost a line of the work they were baselines for");
        }

        // ── The defect itself, pinned ──────────────────────────────────────────
        private static void TheWrongSpellingIsNotWhatGoes(Action<bool, string, string> check, string folder)
        {
            string translation = Translation(folder);
            File.WriteAllText(translation, "{}");
            File.WriteAllText(TranslationFiles.AncestorOf(translation), "{}");
            // What the fork used to look for: the name with the suffix substituted into it.
            string misspelt = translation.Replace(".json", ".ancestor.json");
            File.WriteAllText(misspelt, "{}");

            int gone = CompanionFiles.DeleteAncestors(translation);

            check(gone == 1 && !File.Exists(TranslationFiles.AncestorOf(translation)),
                "the file that goes is translations.json.ancestor",
                "the spelling every reader and writer uses — the one the fork's clean-up did not");

            check(File.Exists(misspelt),
                "translations.ancestor.json is not what is looked for",
                "a stray file by that name is somebody's, and was never ours to delete");
        }

        private static void NothingToDropIsNotAnError(Action<bool, string, string> check, string folder)
        {
            string translation = Translation(folder);
            File.WriteAllText(translation, "{}");

            int first = CompanionFiles.DeleteAncestors(translation);
            int second = CompanionFiles.DeleteAncestors(translation);

            check(first == 0 && second == 0 && File.Exists(translation),
                "a translation with no ancestors has nothing to drop, twice",
                "a fresh file and a file forked twice both arrive here; neither is a failure");
        }

        private static void NoPathIsRefused(Action<bool, string, string> check)
        {
            bool refused = false;
            try { CompanionFiles.DeleteAncestors(null); }
            catch (ArgumentException) { refused = true; }

            check(refused,
                "no path is refused, not silently ignored",
                "a null here means the cache path was never set — a state to see, not to paper over");
        }

        // ── 🔴 The one the backups got wrong ───────────────────────────────────
        private static void ImagesAreReadWhereTheFileWritesThem(Action<bool, string, string> check)
        {
            var written = JObject.Parse(
                "{ \"_uuid\": \"x\", \"_image_replacements\": [" +
                "  { \"sprite_name\": \"logo\", \"file\": \"logo.png\" }," +
                "  { \"sprite_name\": \"old\", \"replacement_file\": \"old.png\" }," +
                "  { \"sprite_name\": \"older\", \"original_file\": \"older.png\" }," +
                "  { \"sprite_name\": \"nameless\" }," +
                "  \"not an entry\" ]," +
                "  \"Hello\": { \"v\": \"Bonjour\", \"t\": \"H\" } }");

            var names = CompanionFiles.ImagesNamedBy(written);

            check(names.Count == 3 && names[0] == "logo.png" && names[1] == "old.png" && names[2] == "older.png",
                "images are read from _image_replacements, current field first, older ones after",
                "the key the file writes; the two older spellings are still lying in game folders");

            var misread = JObject.Parse("{ \"_images\": [ { \"file\": \"logo.png\" } ] }");
            check(CompanionFiles.ImagesNamedBy(misread).Count == 0,
                "_images names nothing",
                "the key both products read for weeks; nothing writes it, so a copy reading it carried no image");

            check(CompanionFiles.ImagesNamedBy(null).Count == 0
                  && CompanionFiles.ImagesNamedBy(JObject.Parse("{ \"_image_replacements\": \"nope\" }")).Count == 0,
                "no file, and a section that is not a list, name nothing",
                "a backup must still be taken of a translation whose images cannot be read");
        }
    }
}
