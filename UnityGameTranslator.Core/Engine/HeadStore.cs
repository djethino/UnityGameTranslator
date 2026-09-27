using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Where, in this game, a text is the head of a reveal resumed part-way —
    /// <c>translations.json.heads</c>, a companion like the failed lines
    /// (<see cref="TranslationFiles.HeadsOf"/>) — so that a finding made once (a request paid,
    /// then taken back) is not paid again at the next launch. See TextRouter.Heads.
    ///
    /// ⚠ **Local, and holds no translation.** Each entry says only "this component does not send
    /// this text": never uploaded, outside the hash, the merge and the backups. Lost, it is learnt
    /// again, one request per finding. One key still has one answer in the translation, whichever
    /// component shows it.
    ///
    /// Pure: no Unity, no clock — the Core.Checks replay a round trip on a real folder. Written
    /// whole or not at all (<see cref="AtomicFile"/>); deleted when there is nothing left to keep.
    /// </summary>
    public sealed class HeadStore
    {
        public string Path { get; }

        public HeadStore(string translationPath)
        {
            Path = TranslationFiles.HeadsOf(translationPath);
        }

        /// <summary>
        /// The findings as (place, normalised text). No file: none. A file that is not what this
        /// writes throws — the caller says so and goes on without it; it is not the translation.
        /// </summary>
        public List<KeyValuePair<string, string>> Load()
        {
            var heads = new List<KeyValuePair<string, string>>();
            if (!File.Exists(Path)) return heads;

            var doc = JObject.Parse(File.ReadAllText(Path));
            if (!(doc["heads"] is JArray entries))
                throw new InvalidDataException("no \"heads\" list");
            foreach (var entry in entries)
            {
                string place = entry?["place"]?.Type == JTokenType.String ? (string)entry["place"] : null;
                string text = entry?["text"]?.Type == JTokenType.String ? (string)entry["text"] : null;
                if (string.IsNullOrEmpty(place) || string.IsNullOrEmpty(text))
                    throw new InvalidDataException("an entry without its place or its text");
                heads.Add(new KeyValuePair<string, string>(place, text));
            }
            return heads;
        }

        /// <summary>Writes the findings, or deletes the file when there are none.</summary>
        public void Save(IReadOnlyCollection<KeyValuePair<string, string>> heads)
        {
            if (heads == null) throw new ArgumentNullException(nameof(heads));
            if (heads.Count == 0)
            {
                if (File.Exists(Path)) File.Delete(Path);
                return;
            }

            var entries = new JArray();
            foreach (var head in heads)
                entries.Add(new JObject { ["place"] = head.Key, ["text"] = head.Value });
            AtomicFile.WriteAllText(Path, new JObject { ["heads"] = entries }.ToString(Formatting.Indented));
        }
    }
}
