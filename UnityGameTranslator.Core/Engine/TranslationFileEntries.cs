using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The lines a translations.json yields, and what reading them says about the file itself.
    ///
    /// 🔴 **What is decided here is which translation a player ends up with**, and every branch can
    /// lose one silently. A key normalised differently on two machines becomes two keys and the
    /// game shows the untranslated one. A duplicate resolved the wrong way replaces somebody's own
    /// wording with a model's. An interface line left in the game's file is counted, hashed, merged
    /// and uploaded to everybody as if it were part of the game.
    ///
    /// 🔴 **The one that costs the most is the duplicate rule**, because it is invisible: two keys
    /// that differ only in their line endings collapse into one, and only the higher tag survives —
    /// **H over V over A**, a person over a review over a model. Backwards, a translation somebody
    /// typed is quietly replaced by one nobody read, in a file that is then shared.
    ///
    /// ⚠ <see cref="NeedsRewrite"/> is not cosmetic. Reading a file can change what it should
    /// contain — a legacy string turned into an entry, a key normalised, an interface line taken
    /// out — and if nobody writes it back the same repair happens at every launch, on a file that
    /// keeps travelling as it was.
    ///
    /// ⚠ **Pure by contract**: parsed JSON in, entries out. No disk, no statics, no logging. It is
    /// the first slice of separating READING the translation file from APPLYING it — see
    /// analyse/plan-prealables-couches.md, step 6.
    ///
    /// Cut out of TranslatorCore.LoadCache on 2026-09-08.
    /// </summary>
    public sealed class TranslationFileEntries
    {
        /// <summary>
        /// The largest index a capture order may carry: JavaScript's exact-integer ceiling.
        ///
        /// ⚠ Not arbitrary — the file is read by the website too, in a browser, where anything
        /// above this stops being the number it was written as.
        /// </summary>
        public const long MaxOrderIndex = 9007199254740991L;

        /// <summary>The game's lines, by normalised key.</summary>
        public Dictionary<string, TranslationEntry> Entries { get; }
            = new Dictionary<string, TranslationEntry>();

        /// <summary>
        /// Interface lines found where they no longer belong. Null while there are none.
        ///
        /// 🔴 Taken out BEFORE anything counts, hashes or merges them. What becomes of them is
        /// somebody else's decision — they may belong to this install's interface file, or they may
        /// have arrived inside somebody else's translation.
        /// </summary>
        public Dictionary<string, TranslationEntry> StrandedModUi { get; private set; }

        /// <summary>True when reading changed what the file should hold, so it has to be written back.</summary>
        public bool NeedsRewrite { get; private set; }

        /// <summary>
        /// Read every line of a parsed translation file. Metadata keys — anything starting with an
        /// underscore — are left to whoever asked; this only takes the lines.
        /// </summary>
        public static TranslationFileEntries ReadAll(JObject file)
        {
            var read = new TranslationFileEntries();
            if (file == null) return read;

            foreach (var property in file.Properties())
            {
                if (property.Name.StartsWith("_")) continue;
                read.Read(property.Name, property.Value);
            }

            return read;
        }

        /// <summary>
        /// Read one line: its key, its value, and where it belongs.
        /// </summary>
        public void Read(string key, JToken value)
        {
            if (key == null || value == null) return;

            // Normalize key line endings for cross-platform consistency
            string normalizedKey = TextNormalization.NormalizeLineEndings(key);

            TranslationEntry entry;
            if (value.Type == JTokenType.Object)
            {
                // New format: {"v": "value", "t": "A", "i": 123}
                var obj = value as JObject;
                entry = new TranslationEntry
                {
                    // Normalize value line endings too
                    Value = TextNormalization.NormalizeLineEndings(obj?["v"]?.ToString() ?? ""),
                    Tag = obj?["t"]?.ToString() ?? "A",
                    Index = ReadIndex(obj?["i"]),
                };
            }
            else if (value.Type == JTokenType.String)
            {
                // Legacy format: a bare string. Read, then written back in the current shape.
                entry = new TranslationEntry
                {
                    Value = TextNormalization.NormalizeLineEndings(value.ToString()),
                    Tag = "A",  // Default to AI for legacy data
                };
                NeedsRewrite = true;
            }
            else
            {
                return;
            }

            // 🔴 An interface line has no business in this file any more. It is taken out here —
            // before anything counts, hashes or merges it — and what becomes of it is settled by
            // the caller, once the ancestor is known.
            if (entry.Tag == ModUi.Tag)
            {
                if (StrandedModUi == null) StrandedModUi = new Dictionary<string, TranslationEntry>();
                StrandedModUi[normalizedKey] = entry;
                NeedsRewrite = true;   // the file will be rewritten without it
                return;
            }

            // Two keys that differed only in their line endings are now one.
            if (Entries.TryGetValue(normalizedKey, out var existing))
            {
                // 🔴 **The socle's ladder, because this is the socle's question.** A second one
                // lived here — H > V > anything else — and it disagreed with Merge.PriorityOf on
                // two tags, in both directions:
                //
                //  · a REFUSAL (S) ranked with the machine's work, so a model's output won over a
                //    line somebody had deliberately marked to be left alone. The socle ranks a
                //    refusal with a hand translation, because it is the same thing: a person
                //    deciding about that line;
                //  · a CAPTURE (H with nothing in it) ranked at the TOP, so an empty entry beat a
                //    real translation and the translation was lost. The socle ranks that pair
                //    below everything, which is why it takes the value and not the tag alone.
                //
                // ⚠ M never reaches here — an interface line is taken out above, before anything
                // counts, hashes or merges it.
                if (Merge.PriorityOf(entry.Tag, entry.Value) > Merge.PriorityOf(existing.Tag, existing.Value))
                {
                    Entries[normalizedKey] = entry;
                    NeedsRewrite = true;
                }
                // Otherwise keep existing (higher or same priority)
            }
            else
            {
                Entries[normalizedKey] = entry;
            }

            // The key on disk is not the key in memory, so the file is out of date.
            if (normalizedKey != key) NeedsRewrite = true;
        }

        /// <summary>
        /// Write the lines into a file being built, after whatever metadata it already carries.
        ///
        /// 🔴 **The mirror of <see cref="ReadAll"/>, and the pair is what has to hold.** Anything
        /// this writes that reading does not give back is work lost the next time the file is
        /// opened — silently, since nothing compares the two.
        ///
        /// ⚠ **Sorted by key**, so two saves of the same content produce the same bytes. The file
        /// is diffed by people, merged, and its hash is compared with the server's; a dictionary's
        /// order would make every save look like a change.
        ///
        /// ⚠ **An absent index is OMITTED, never written as null.** The website refuses `"i": null`
        /// outright, so a file carrying one cannot be published at all — and the person is told
        /// their upload is invalid, not that a line has no capture order.
        /// </summary>
        public static void WriteInto(JObject output, IDictionary<string, TranslationEntry> entries)
        {
            if (output == null || entries == null) return;

            var keys = new List<string>(entries.Keys);
            keys.Sort(System.StringComparer.Ordinal);

            foreach (string key in keys)
            {
                var entry = entries[key];
                var line = new JObject
                {
                    ["v"] = entry.Value,
                    // The cache always carries a tag; without one a line is machine output, which
                    // is what every line was before tags existed.
                    ["t"] = entry.Tag ?? "A",
                };

                if (entry.Index.HasValue) line["i"] = entry.Index.Value;

                output[key] = line;
            }
        }

        /// <summary>How many lines were given an index they did not have.</summary>
        public int Backfilled { get; private set; }

        /// <summary>
        /// Give a capture-order index to every line that has none, and say what the next one is.
        ///
        /// 🔴 **Deterministic, and that is the whole requirement.** Two machines reading the same
        /// file must produce the same indices, or the editors list the same translation in two
        /// different orders and a line moves whenever somebody else opens it. So the lines with no
        /// index are sorted by their KEY, ordinal — not by whatever order a dictionary happened to
        /// hand them over, which is not a promise any runtime makes.
        ///
        /// ⚠ **It costs nothing in sync**: `i` is excluded from the content hash, on the mod and on
        /// the site alike. Backfilling a thousand lines does not make a file look changed to the
        /// server — but it DOES have to be written, or the same thousand are backfilled again at
        /// the next launch.
        ///
        /// ⚠ New indices start above the highest one already there, never at one: an index is a
        /// position in the order lines were captured, and reusing a number would put a new line
        /// where an old one already sits.
        /// </summary>
        public long AssignMissingIndices()
        {
            long highest = 0;
            List<string> missing = null;

            foreach (var entry in Entries)
            {
                if (entry.Value.Index.HasValue)
                {
                    if (entry.Value.Index.Value > highest) highest = entry.Value.Index.Value;
                }
                else
                {
                    if (missing == null) missing = new List<string>();
                    missing.Add(entry.Key);
                }
            }

            long next = highest + 1;
            if (missing == null) return next;

            missing.Sort(System.StringComparer.Ordinal);
            foreach (string key in missing) Entries[key].Index = next++;

            Backfilled = missing.Count;
            NeedsRewrite = true;
            return next;
        }

        /// <summary>
        /// A capture-order index, or nothing when the file does not carry a usable one.
        ///
        /// ⚠ Anything outside 1..<see cref="MaxOrderIndex"/> is read as ABSENT rather than clamped:
        /// an index is a position in a sequence, and inventing one puts a line somewhere its author
        /// never put it. Absent is a state the rest of the mod already handles — it backfills.
        /// </summary>
        public static long? ReadIndex(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer) return null;

            try
            {
                long value = token.Value<long>();
                return (value >= 1 && value <= MaxOrderIndex) ? value : (long?)null;
            }
            catch
            {
                // Integer beyond long range (BigInteger) — treat as absent
                return null;
            }
        }
    }
}
