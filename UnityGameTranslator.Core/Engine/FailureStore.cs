using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The failed lines beside the translation — <c>translations.json.failures</c>, a companion
    /// like the ancestors (<see cref="TranslationFiles.FailuresOf"/>) — so that a line the AI
    /// gave up on is not asked again at the next launch, and the minutes it costs are paid once.
    ///
    /// 🔴 Why a file and not only the session: a text with hundreds of placeholders — release
    /// notes shown at every launch, a chat, a long history nobody excluded — fails in three
    /// attempts of a minute or more each, every time the game starts, while the lines that would
    /// translate wait behind it. Kept here, it is skipped at once, in sight on the Failures tab
    /// until somebody settles it. The record is reconciled at load: a key the file now holds a
    /// translation for is settled and dropped (<see cref="FailureLedger.Settle"/>).
    ///
    /// Pure: no Unity, no clock — the Core.Checks replay a round trip on a real folder. Written
    /// whole or not at all (<see cref="AtomicFile"/>); deleted when there is nothing left to keep.
    /// </summary>
    public sealed class FailureStore
    {
        public string Path { get; }

        public FailureStore(string translationPath)
        {
            Path = TranslationFiles.FailuresOf(translationPath);
        }

        /// <summary>
        /// Reads the file into the ledger, replacing what it held. No file: nothing read. A file
        /// that is not what this writes throws — the caller says so and goes on without it; a
        /// failures file is not the translation.
        /// </summary>
        public int Load(FailureLedger ledger)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            ledger.Clear();
            if (!File.Exists(Path)) return 0;

            var doc = JObject.Parse(File.ReadAllText(Path));
            int read = 0;
            foreach (var token in doc["lines"] as JArray ?? new JArray())
            {
                var line = Read(token as JObject);
                if (line == null) continue;
                ledger.Note(line);
                read++;
            }
            return read;
        }

        /// <summary>Writes the ledger, or deletes the file when the ledger is empty.</summary>
        public void Save(FailureLedger ledger)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            var lines = ledger.All;
            if (lines.Count == 0)
            {
                if (File.Exists(Path)) File.Delete(Path);
                return;
            }
            AtomicFile.WriteAllText(Path, Write(lines).ToString(Formatting.Indented));
        }

        public static JObject Write(IReadOnlyList<FailedLine> lines)
        {
            var array = new JArray();
            foreach (var line in lines)
            {
                var attempts = new JArray();
                foreach (var attempt in line.Attempts)
                {
                    attempts.Add(new JObject
                    {
                        ["value"] = attempt.Value ?? "",
                        ["errors"] = new JArray(attempt.Errors ?? new List<string>()),
                    });
                }
                array.Add(new JObject
                {
                    ["key"] = line.Key,
                    ["source"] = line.Source ?? line.Key,
                    ["elements"] = new JArray(line.Elements ?? new List<string>()),
                    ["attempts"] = attempts,
                });
            }
            return new JObject { ["lines"] = array };
        }

        private static FailedLine Read(JObject token)
        {
            string key = token?["key"]?.Type == JTokenType.String ? (string)token["key"] : null;
            if (string.IsNullOrEmpty(key)) return null;

            var line = new FailedLine { Key = key, Source = (string)token["source"] ?? key };
            foreach (var element in token["elements"] as JArray ?? new JArray())
                if (element.Type == JTokenType.String && !string.IsNullOrEmpty((string)element)) line.Elements.Add((string)element);
            foreach (var attempt in token["attempts"] as JArray ?? new JArray())
            {
                var errors = new List<string>();
                foreach (var error in attempt["errors"] as JArray ?? new JArray())
                    if (error.Type == JTokenType.String) errors.Add((string)error);
                line.Attempts.Add(new FailedAttempt { Value = (string)attempt["value"] ?? "", Errors = errors });
            }
            return line;
        }
    }
}
