using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What a translation file states about itself, read into a record with a value for every
    /// field — the shape that ends the family of "the previous file's answer survives".
    ///
    /// ⚠ Read against <see cref="LoadedFile.Read"/> with documents built by hand. What is at stake
    /// is not one wrong value but a MISSING one: a key the file carries and the record does not
    /// read is a key the next save writes from memory — somebody else's, if a different file was
    /// loaded before. LoadedIdentityChecks holds the other half (every key SaveCache writes is read
    /// here); these cases hold what each key reads as.
    /// </summary>
    internal static class LoadedFileChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            Nothing(check);
            Everything(check);
            HalfSaid(check);
            Sections(check);
            Lines(check);
            Malformed(check);
        }

        private static void Nothing(Action<bool, string, string> check)
        {
            var file = LoadedFile.Read(new JObject());
            check(file.EngineVersion == 0 && file.Uuid == null && file.SourceLanguage == null && file.TargetLanguage == null
                  && file.LocalChanges == 0 && !file.MetadataDirty
                  && file.LastSyncedHash == null && file.LastMergedMainHash == null && file.SourceSiteId == null
                  && file.ForkedFromSiteId == null && file.ForkedFromHash == null && file.ForkedFromResolvedLines == null && file.ForkedFromContentHash == null
                  && file.SavedSteamId == null && file.Sections.Count == 0 && file.StrandedUiFont == null && file.Entries.Entries.Count == 0,
                "an empty file reads as every default",
                "absent means 'this translation has none' — never 'keep the last one's'");

            var none = LoadedFile.Read(null);
            check(none.Uuid == null && none.Sections.Count == 0 && none.Entries.Entries.Count == 0,
                "and so does no document at all",
                "a caller that could not parse still gets a record, not an exception from here");
        }

        private static void Everything(Action<bool, string, string> check)
        {
            var doc = JObject.Parse(@"{
                ""_engine_version"": 1,
                ""_uuid"": ""a6e615b8-0000-4000-8000-000000000001"",
                ""_source_language"": ""English"",
                ""_target_language"": ""Thai"",
                ""_local_changes"": 42,
                ""_metadata_dirty"": true,
                ""_source"": { ""hash"": ""57881c8a"", ""main_hash"": ""aaaa"", ""site_id"": 12 },
                ""_forked_from"": { ""site_id"": 7, ""hash"": ""bbbb"", ""resolved_lines"": 300, ""content_hash"": ""cccc"" },
                ""_game"": { ""name"": ""Some game"", ""steam_id"": ""367520"" },
                ""Play"": { ""v"": ""Jouer"", ""t"": ""A"" }
            }");
            var file = LoadedFile.Read(doc);
            check(file.EngineVersion == 1 && file.Uuid == "a6e615b8-0000-4000-8000-000000000001"
                  && file.SourceLanguage == "English" && file.TargetLanguage == "Thai"
                  && file.LocalChanges == 42 && file.MetadataDirty,
                "the identity fields read as written",
                "each one is what the next save writes back; a value read wrong is a value rewritten wrong");

            check(file.LastSyncedHash == "57881c8a" && file.LastMergedMainHash == "aaaa" && file.SourceSiteId == 12,
                "and the server's row and hashes",
                "this is the block a Thai backup came to carry from a French translation; it is read from THIS file or not at all");

            check(file.ForkedFromSiteId == 7 && file.ForkedFromHash == "bbbb" && file.ForkedFromResolvedLines == 300 && file.ForkedFromContentHash == "cccc",
                "and the fork's origin, all four",
                "an origin inherited from another file is a claim about somebody else's work, published at the next upload");

            check(file.SavedSteamId == "367520",
                "and the game the file remembers",
                "compared with the current detection; a mismatch rewrites the file");

            check(file.Entries.Entries.Count == 1 && file.Entries.Entries["Play"].Value == "Jouer",
                "and the lines",
                "the record carries the whole file, not the metadata beside it");
        }

        private static void HalfSaid(Action<bool, string, string> check)
        {
            var auto = LoadedFile.Read(JObject.Parse(@"{ ""_source_language"": ""auto"" }"));
            check(auto.SourceLanguage == "auto" && auto.TargetLanguage == null,
                "a language stated as 'auto' is handed over as 'auto', not as nothing",
                "the reader does not judge; the caller knows a mode must not outrank the server — and the absent one stays null");

            var notAnObject = LoadedFile.Read(JObject.Parse(@"{ ""_source"": ""57881c8a"", ""_forked_from"": 7, ""_game"": ""x"" }"));
            check(notAnObject.LastSyncedHash == null && notAnObject.SourceSiteId == null && notAnObject.ForkedFromSiteId == null && notAnObject.SavedSteamId == null,
                "a block that is not an object reads as nothing",
                "the branch always asked for an object; a hand edit that broke the shape is a block the file does not carry");

            var partial = LoadedFile.Read(JObject.Parse(@"{ ""_source"": { ""hash"": ""57881c8a"" }, ""_forked_from"": { ""site_id"": 7 } }"));
            check(partial.LastSyncedHash == "57881c8a" && partial.LastMergedMainHash == null && partial.SourceSiteId == null
                  && partial.ForkedFromSiteId == 7 && partial.ForkedFromContentHash == null,
                "a block missing a field reads that field as null",
                "a file forked before content_hash existed says 'we cannot tell', never a value from elsewhere");

            var unknown = LoadedFile.Read(JObject.Parse(@"{ ""_something_newer"": { ""x"": 1 }, ""Play"": ""Jouer"" }"));
            check(unknown.Sections.Count == 0 && unknown.Entries.Entries.Count == 1,
                "an underscore key this version does not know is neither a section nor a line",
                "a file written by a newer mod is read for what this one understands, and its lines are not lost");
        }

        private static void Sections(Action<bool, string, string> check)
        {
            var settings = SettingsSections.JsonKey(SettingsSections.GameSettings);
            string second = SettingsSections.All.First(s => s != SettingsSections.GameSettings);
            var secondKey = SettingsSections.JsonKey(second);

            var doc = new JObject
            {
                [secondKey] = new JArray(),
                [settings] = new JObject { ["ui_font"] = "Tahoma", ["typewriting"] = true },
                ["Play"] = "Jouer",
            };
            var file = LoadedFile.Read(doc);

            check(file.Sections.Count == 2 && file.Sections[0].Key == second && file.Sections[1].Key == SettingsSections.GameSettings,
                "the sections are named by the socle's table, in file order",
                "a key spelled out here would be a second copy of the list that nothing compares to the first");

            check(file.Sections[1].Value is JObject held && held["typewriting"]?.Value<bool>() == true,
                "each with its token as written",
                "the owner of the section parses it; the reader hands it over untouched");

            check(file.StrandedUiFont == "Tahoma",
                "ui_font inside the game settings is lifted out",
                "it describes the MOD's interface from inside the GAME's file, and must not be written back there");

            check(LoadedFile.Read(new JObject { [settings] = new JObject() }).StrandedUiFont == null
                  && LoadedFile.Read(new JObject { [settings] = "garbage" }).StrandedUiFont == null,
                "and is null when there is none, or when the section is not an object",
                "a stranded font that reads as an empty string would be adopted as a font called nothing");

            check(file.Entries.Entries.Count == 1,
                "and none of the sections is read as a line",
                "a section stored as a line would be published as one, and the file's next reader would not find it");

            var every = new JObject();
            foreach (string section in SettingsSections.All) every[SettingsSections.JsonKey(section)] = new JObject();
            var all = LoadedFile.Read(every);
            check(all.Sections.Count == SettingsSections.All.Length && all.Sections.Select(s => s.Key).SequenceEqual(SettingsSections.All),
                $"all {SettingsSections.All.Length} sections the socle names are read",
                "walked from the table, so a section added tomorrow is read the day it is named");
        }

        private static void Lines(Action<bool, string, string> check)
        {
            var file = LoadedFile.Read(JObject.Parse(@"{ ""_uuid"": ""x"", ""Play"": { ""v"": ""Jouer"", ""t"": ""H"" }, ""Quit"": ""Quitter"" }"));
            check(file.Entries.Entries.Count == 2 && file.Entries.Entries["Play"].Tag == "H" && file.Entries.Entries["Quit"].Value == "Quitter",
                "every key that is not metadata is a line, in either shape",
                "the bare form is still read; the rules for it live in TranslationFileEntries, which this reader hands the line to");
        }

        private static void Malformed(Action<bool, string, string> check)
        {
            bool threw = false;
            try { LoadedFile.Read(JObject.Parse(@"{ ""_engine_version"": ""one"" }")); } catch (Exception) { threw = true; }
            check(threw,
                "a version that is not a number throws",
                "as the inline branch did: the caller's catch turns a failed read into a fresh cache rather than keeping half a file");

            threw = false;
            try { LoadedFile.Read(JObject.Parse(@"{ ""_local_changes"": ""many"" }")); } catch (Exception) { threw = true; }
            check(threw,
                "and so does a count that is not one",
                "a value read as zero by mistake would be written back as zero, and the claim of unpublished work would vanish");
        }
    }
}
