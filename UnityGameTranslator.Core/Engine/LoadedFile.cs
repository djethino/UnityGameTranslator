using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Everything a translation file states about ITSELF, read in one pass into a record that has
    /// a value for every field — absent meaning the default, never "whatever was there before".
    ///
    /// 🔴 **Why a record.** Four defects of one family were paid for before this existed, the last
    /// one observed on a real install: each metadata block was read by a branch that only ran when
    /// its key was present, so a file that said nothing left the PREVIOUS file's answer standing.
    /// A never-published Thai backup restored over a published French translation came out
    /// carrying the French one's server row and content hash. With a record, "the previous value
    /// survives" stops being expressible: the caller assigns every field from it, unconditionally.
    ///
    /// ⚠ **Reading, not applying.** This says what the file holds; what to do about it — which
    /// language wins, which section goes to which owner, whether a stranded font is adopted — is
    /// the caller's, in the order it chooses. Two things are read but not judged: a language
    /// stated as "auto" is handed over as "auto" (the caller knows a mode is not an answer), and
    /// a `_source` that is not an object reads as nothing, as the branch always did.
    ///
    /// ⚠ **A malformed value throws, as before**: `_engine_version` that is not a number,
    /// `_local_changes` that is not one. The caller's catch turns a failed read into a fresh
    /// cache, which is what it did when the branches lived inline. A read that swallowed the
    /// error would keep half a file.
    ///
    /// 🔴 **The sections are named by the socle's table, never by a key written here.** A key
    /// spelled out is a second copy of the list that nothing compares to the first — a seventh
    /// section would be written by every product and read back by nobody.
    ///
    /// 🔴 **Pure by contract**, like its neighbours in Engine/: a parsed document in, a record
    /// out. No Unity, no disk, no state. Linked by tests/UnityGameTranslator.Core.Checks, where
    /// LoadedIdentityChecks also holds the other half: every key SaveCache writes is read here.
    ///
    /// Moved out of TranslatorCore.LoadCache on 2026-09-11 (step 6t of
    /// analyse/plan-prealables-couches.md), verbatim: same branches, same answers.
    /// </summary>
    public sealed class LoadedFile
    {
        /// <summary>The placeholder format the file was written with; 0 when it does not say (an old file).</summary>
        public int EngineVersion;

        /// <summary>The lineage; null when absent — the caller gives the file a fresh one.</summary>
        public string Uuid;

        /// <summary>The source language as stated, "auto" included; null when the file does not state one.</summary>
        public string SourceLanguage;

        /// <summary>The target language as stated; null when the file does not state one.</summary>
        public string TargetLanguage;

        /// <summary>Entries modified since the last sync, as the file claims; 0 when absent (the caller recounts).</summary>
        public int LocalChanges;

        /// <summary>Whether settings changed since the last sync; false when absent.</summary>
        public bool MetadataDirty;

        /// <summary>`_source.hash`: the server version at the last sync; null when absent.</summary>
        public string LastSyncedHash;

        /// <summary>`_source.main_hash`: the Main at the last upstream merge; null when absent.</summary>
        public string LastMergedMainHash;

        /// <summary>`_source.site_id`: the server row; null when absent.</summary>
        public int? SourceSiteId;

        /// <summary>`_forked_from.site_id`; null when the file is not a fork, or was forked before the key existed.</summary>
        public int? ForkedFromSiteId;

        /// <summary>`_forked_from.hash`.</summary>
        public string ForkedFromHash;

        /// <summary>`_forked_from.resolved_lines`.</summary>
        public int? ForkedFromResolvedLines;

        /// <summary>`_forked_from.content_hash`. Absent from a file forked before this key existed: null reads as "we cannot tell".</summary>
        public string ForkedFromContentHash;

        /// <summary>`_game.steam_id` as the file remembers it; null when absent.</summary>
        public string SavedSteamId;

        /// <summary>
        /// The settings sections the file carries, named by the socle (<see cref="SettingsSections.All"/>),
        /// in file order, with the token as written. A section absent from the file is absent here:
        /// "this translation has none".
        /// </summary>
        public readonly List<KeyValuePair<string, JToken>> Sections = new List<KeyValuePair<string, JToken>>();

        /// <summary>
        /// `ui_font` found inside the game settings section: the MOD's interface font described
        /// from inside the GAME's file. Lifted out here because it is not part of the section; the
        /// caller carries it to the interface file and never writes it back.
        /// </summary>
        public string StrandedUiFont;

        /// <summary>The lines themselves, read by <see cref="TranslationFileEntries"/> (collisions, stranded interface lines, indices).</summary>
        public readonly TranslationFileEntries Entries = new TranslationFileEntries();

        /// <summary>
        /// Read a parsed translation file. Null reads as an empty file: every default, no line.
        /// </summary>
        public static LoadedFile Read(JObject parsed)
        {
            var file = new LoadedFile();
            if (parsed == null) return file;

            foreach (var prop in parsed.Properties())
            {
                if (prop.Name == "_engine_version")
                {
                    file.EngineVersion = prop.Value.Value<int>();
                }
                else if (prop.Name == "_uuid")
                {
                    file.Uuid = prop.Value.ToString();
                }
                else if (prop.Name == "_source_language")
                {
                    // Handed over as stated: a file written with "auto" in it — an older mod, or a
                    // hand edit — states nothing, and the caller knows a mode must not outrank the
                    // server.
                    file.SourceLanguage = prop.Value.ToString();
                }
                else if (prop.Name == "_target_language")
                {
                    file.TargetLanguage = prop.Value.ToString();
                }
                else if (prop.Name == "_local_changes")
                {
                    file.LocalChanges = prop.Value.Value<int>();
                }
                else if (prop.Name == "_metadata_dirty")
                {
                    file.MetadataDirty = prop.Value.Value<bool>();
                }
                else if (prop.Name == "_source" && prop.Value.Type == JTokenType.Object)
                {
                    // Source info for sync detection
                    var source = prop.Value as JObject;
                    file.LastSyncedHash = source?["hash"]?.Value<string>();
                    file.LastMergedMainHash = source?["main_hash"]?.Value<string>();
                    file.SourceSiteId = source?["site_id"]?.Value<int?>();
                }
                else if (prop.Name == "_forked_from" && prop.Value.Type == JTokenType.Object)
                {
                    var origin = prop.Value as JObject;
                    file.ForkedFromSiteId = origin?["site_id"]?.Value<int?>();
                    file.ForkedFromHash = origin?["hash"]?.Value<string>();
                    file.ForkedFromResolvedLines = origin?["resolved_lines"]?.Value<int?>();
                    // ⚠ Absent from a file forked before this key existed. Left null, which
                    // reads as "we cannot tell" — see ForkIsStillTheCopy.
                    file.ForkedFromContentHash = origin?["content_hash"]?.Value<string>();
                }
                else if (prop.Name == "_game" && prop.Value.Type == JTokenType.Object)
                {
                    // The saved steam_id, for comparison with the current detection
                    var game = prop.Value as JObject;
                    file.SavedSteamId = game?["steam_id"]?.Value<string>();
                }
                // 🔴 **One branch for the settings sections, and the socle says which they are.**
                // They were six branches naming six keys — the same list SaveCache walks from
                // SettingsSections.All, written out a second time where nothing compared the two.
                //
                // ⚠ No type guard, and it changes nothing: every parser is `as JObject` /
                // `as JArray` and yields nothing on anything else, so a malformed section leaves
                // that section empty — exactly what skipping the branch did, now that the caller
                // empties every section before applying the file's.
                else if (SettingsSections.SectionOf(prop.Name) != null)
                {
                    string section = SettingsSections.SectionOf(prop.Name);

                    // ⚠ Lifted out BEFORE the section is applied, and it is not part of it:
                    // ui_font described the MOD's interface from inside the GAME's file.
                    if (section == SettingsSections.GameSettings)
                        file.StrandedUiFont = (prop.Value as JObject)?["ui_font"]?.Value<string>();

                    file.Sections.Add(new KeyValuePair<string, JToken>(section, prop.Value));
                }
                else if (!prop.Name.StartsWith("_"))
                {
                    // Which line wins a collision, which one has no business in this file, and
                    // whether reading changed what the file should hold — see
                    // Engine/TranslationFileEntries, where it can be replayed without a game.
                    file.Entries.Read(prop.Name, prop.Value);
                }
                // Any other `_` key is unknown to this version and left alone.
            }

            return file;
        }
    }
}
