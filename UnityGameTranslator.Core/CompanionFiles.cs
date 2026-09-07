using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The mechanics on the files that sit beside <c>translations.json</c>: dropping the ancestors a
    /// fork leaves behind, reading which images a translation names. Pure by contract — paths and
    /// parsed JSON in, no Unity, no state, no clock — so <c>tests/UnityGameTranslator.Core.Checks</c>
    /// links this file and replays the sequences on real files in a real folder.
    ///
    /// 🔴 Why a file of its own. Both mechanics were written inline, once each, and both were wrong
    /// in a way nothing could see. The fork's clean-up spelled the ancestor
    /// <c>translations.ancestor.json</c> where every reader spells it <c>translations.json.ancestor</c>,
    /// so the file was never found, never deleted, and the in-memory clear inside the same <c>if</c>
    /// never ran either. The backups read the images from <c>_images</c> while the file writes
    /// <c>_image_replacements</c>, so a "saved" copy carried the fonts and never an image. Neither
    /// threw, neither logged. The names now live once, in <see cref="TranslationFiles"/>; the
    /// mechanics live here, where a check can break them on purpose.
    /// </summary>
    public static class CompanionFiles
    {
        /// <summary>
        /// The image files a translation names, in the order it names them. A section that is
        /// absent, not an array, or holding entries without a file name contributes nothing; the
        /// caller decides what an empty answer means.
        /// </summary>
        public static List<string> ImagesNamedBy(JObject root)
        {
            var names = new List<string>();
            if (root == null) return names;
            if (!(root[TranslationFiles.ImagesSection] is JArray images)) return names;

            foreach (var item in images)
            {
                if (!(item is JObject obj)) continue;

                string file = obj.Value<string>(TranslationFiles.ImageFileField);
                if (string.IsNullOrEmpty(file))
                {
                    foreach (var legacy in TranslationFiles.ImageFileLegacyFields)
                    {
                        file = obj.Value<string>(legacy);
                        if (!string.IsNullOrEmpty(file)) break;
                    }
                }

                if (!string.IsNullOrEmpty(file)) names.Add(file);
            }

            return names;
        }

        /// <summary>
        /// Removes both ancestors of a translation — its own last-synced state and the Main it last
        /// merged from — and says how many files actually went. The translation itself is never
        /// touched. A missing file is not an error: the answer is simply one fewer.
        ///
        /// ⚠ Throws when a file exists and cannot be deleted. The caller sits at the process boundary
        /// and is the one to log it; swallowing it here would recreate the silence this file exists
        /// to end.
        /// </summary>
        public static int DeleteAncestors(string translationPath)
        {
            if (string.IsNullOrEmpty(translationPath))
                throw new ArgumentException("A translation path is required.", nameof(translationPath));

            int deleted = 0;
            foreach (var path in new[]
                     {
                         TranslationFiles.AncestorOf(translationPath),
                         TranslationFiles.MainAncestorOf(translationPath)
                     })
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deleted++;
            }

            return deleted;
        }
    }
}
