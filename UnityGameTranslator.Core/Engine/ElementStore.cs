using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// What the mod has learnt about this game's text elements — <c>translations.json.elements</c>,
    /// a companion like the failed lines (<see cref="TranslationFiles.ElementsOf"/>) — so that a
    /// finding paid for once is not paid again at the next launch.
    ///
    /// ONE file, one section per kind of finding. Today: <c>heads</c>, where a text is the head of
    /// a reveal resumed part-way (TextRouter.Heads). A finding of another kind gets a section here,
    /// never a file of its own; and a section this build does not know is written back as it was
    /// read, so a newer build's findings survive an older one.
    ///
    /// ⚠ **Local, and holds no translation.** Each entry says what an element does, never what a
    /// text means: never uploaded, outside the hash, the merge and the backups. Lost, it is learnt
    /// again. One key still has one answer in the translation, whichever element shows it.
    ///
    /// Pure: no Unity, no clock — the Core.Checks replay a round trip on a real folder. Written
    /// whole or not at all (<see cref="AtomicFile"/>); deleted when nothing is left in it.
    /// </summary>
    public sealed class ElementStore
    {
        private const string HeadsSection = "heads";

        public string Path { get; }

        // The file as last read, for the sections this build does not own.
        private JObject _read = new JObject();

        public ElementStore(string translationPath)
        {
            Path = TranslationFiles.ElementsOf(translationPath);
        }

        /// <summary>
        /// The heads, as (place, normalised text). No file: none. A file that is not what this
        /// writes throws — the caller says so and goes on without it; it is not the translation.
        /// </summary>
        public List<KeyValuePair<string, string>> LoadHeads()
        {
            _read = new JObject();
            var heads = new List<KeyValuePair<string, string>>();
            if (!File.Exists(Path)) return heads;

            var doc = JObject.Parse(File.ReadAllText(Path));
            var section = doc[HeadsSection];
            if (section != null && !(section is JArray))
                throw new InvalidDataException("\"heads\" is not a list");
            foreach (var entry in section as JArray ?? new JArray())
            {
                string place = entry?["place"]?.Type == JTokenType.String ? (string)entry["place"] : null;
                string text = entry?["text"]?.Type == JTokenType.String ? (string)entry["text"] : null;
                if (string.IsNullOrEmpty(place) || string.IsNullOrEmpty(text))
                    throw new InvalidDataException("a head without its place or its text");
                heads.Add(new KeyValuePair<string, string>(place, text));
            }
            _read = doc;
            return heads;
        }

        /// <summary>
        /// Writes the heads beside the sections read that this build does not own; deletes the file
        /// when nothing is left in it.
        /// </summary>
        public void SaveHeads(IReadOnlyCollection<KeyValuePair<string, string>> heads)
        {
            if (heads == null) throw new ArgumentNullException(nameof(heads));

            var doc = (JObject)_read.DeepClone();
            doc.Remove(HeadsSection);
            if (heads.Count > 0)
            {
                var entries = new JArray();
                foreach (var head in heads)
                    entries.Add(new JObject { ["place"] = head.Key, ["text"] = head.Value });
                doc[HeadsSection] = entries;
            }

            if (!doc.HasValues)
            {
                if (File.Exists(Path)) File.Delete(Path);
                return;
            }
            AtomicFile.WriteAllText(Path, doc.ToString(Formatting.Indented));
        }
    }
}
