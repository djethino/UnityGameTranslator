using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Everything a translation file tells the mod about ITSELF has to be re-derived from that
    /// file — never inherited from the one loaded before it.
    ///
    /// 🔴 **Four defects of one family, and the fourth was found on a real install.** Each metadata
    /// block in the file was read by a branch that only ran when its key was present, so a file
    /// that said nothing left the previous file's answer standing. It has cost, in order — the
    /// game settings (typewriting and concat stayed off from one translation to the next), then
    /// `_local_changes` and `_metadata_dirty` (the mod claimed local changes right after
    /// downloading the server's own copy), then the lineage identity itself.
    ///
    /// 🔴 **What the last one did**, observed on 2026-09-08: a published English→French translation,
    /// then a never-published English→Thai backup restored over it in the same session. The Thai
    /// file came out carrying `_source: { hash: 57881c8a…, site_id: 12 }` — the French
    /// translation's row and content hash — written into its own file.
    ///
    /// ✅ **Since 2026-09-11 the file is read into a RECORD** (<see cref="LoadedFile"/>) with a
    /// value for every field, and LoadCache assigns every field from it. "The previous value
    /// survives" is no longer expressible in the reading. What can still go wrong is around it,
    /// and that is what is checked here, lexically on purpose (LoadCache reads a real file, logs
    /// through the mod loader and talks to Unity, so it cannot be replayed):
    ///
    /// | what | why it can still bite |
    /// |---|---|
    /// | every field the applying assigns is cleared before the file is read | the two paths that never reach the record — no file, a file that could not be read — rely on the reset |
    /// | every field the record carries is applied | a field added to the record and not to LoadCache is read and thrown away |
    /// | every `_key` SaveCache writes is read by the record | a key written from memory and never read is the family coming back one level up |
    /// | the sections are walked from the socle's table on both sides, no key spelled out | a seventh section written by every product and read by nobody |
    /// </summary>
    internal static class LoadedIdentityChecks
    {
        /// <summary>
        /// A field of the mod's own, assigned: `SomeName = …` at the start of its line.
        ///
        /// ⚠ **Anchored to the line start on purpose.** Matching anywhere read three assignments
        /// out of one interpolated log line and failed the check on prose; it also would have read
        /// a commented-out assignment as a real one. Every assignment in the regions below begins
        /// its line; nothing inside a string or after `//` does.
        /// </summary>
        private static readonly Regex Assignment =
            new Regex(@"^[ \t]*(?<name>[A-Z][A-Za-z0-9]*)[ \t]*=[ \t]*(?!=)",
                      RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>A metadata key written as a literal: `"_something"`.</summary>
        private static readonly Regex MetadataKey = new Regex("\"(_[a-z_]+)\"", RegexOptions.Compiled);

        public static void Run(Action<bool, string, string> check)
        {
            string source = FindCore("TranslatorCore.cs");
            string recordSource = FindCore("Engine", "LoadedFile.cs");
            check(source != null && recordSource != null, "TranslatorCore's and LoadedFile's sources are found",
                "this check reads them; without them, it proves nothing");
            if (source == null || recordSource == null) return;

            string text = File.ReadAllText(source);
            string record = File.ReadAllText(recordSource);

            int loadCache = text.IndexOf("private static void LoadCache()", StringComparison.Ordinal);
            int reading = text.IndexOf("var file = LoadedFile.Read(parsed);", StringComparison.Ordinal);
            int entries = text.IndexOf("TranslationCache = entriesRead.Entries;", StringComparison.Ordinal);
            check(loadCache >= 0 && reading > loadCache && entries > reading,
                "and the three landmarks of LoadCache are where they are expected",
                "the check is anchored on them; moved or renamed, it must say so rather than pass quietly");
            if (loadCache < 0 || reading <= loadCache || entries <= reading) return;

            // From the start of LoadCache to the record: where a field is cleared.
            string clearedRegion = text.Substring(loadCache, reading - loadCache);
            // From the record to the lines: where the record is applied.
            string appliedRegion = text.Substring(reading, entries - reading);

            var cleared = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Assignment.Matches(clearedRegion)) cleared.Add(m.Groups["name"].Value);

            // Since 2026-09-12 the identity and the stamps live in TranslationStore, and LoadCache
            // clears them all at once by making the store afresh; the statics below are façades
            // over it. A fresh store IS the clear, for exactly these names.
            string[] storeOwned =
            {
                // the façades in TranslatorCore
                "FileUuid", "LastSyncedHash", "LastMergedMainHash", "SourceSiteId", "LocalChangesCount",
                "ForkedFromSiteId", "ForkedFromHash", "ForkedFromResolvedLines", "ForkedFromContentHash",
                // and the store's own fields, which TakeIdentity assigns
                "Uuid", "SourceHash", "MainHash", "SiteId", "LocalChanges",
            };
            if (clearedRegion.Contains("_store = new TranslationStore(", StringComparison.Ordinal))
                foreach (string name in storeOwned) cleared.Add(name);

            // And the record is applied to the store by TakeIdentity, field by field: that method
            // is part of the applied region for the "every field is taken" rule below.
            string storeSource = FindCore("Engine", "TranslationStore.cs");
            check(storeSource != null && appliedRegion.Contains("Store.TakeIdentity(file)", StringComparison.Ordinal),
                "the record is handed to the store", "TranslationStore.TakeIdentity is where the file's identity lands");
            if (storeSource != null)
            {
                string store = File.ReadAllText(storeSource);
                int take = store.IndexOf("public void TakeIdentity(LoadedFile file)", StringComparison.Ordinal);
                int takeEnd = take >= 0 ? store.IndexOf("        }", take, StringComparison.Ordinal) : -1;
                if (take >= 0 && takeEnd > take) appliedRegion += store.Substring(take, takeEnd - take);
            }

            var assignedByApplying = new List<string>();
            foreach (Match m in Assignment.Matches(appliedRegion))
            {
                string name = m.Groups["name"].Value;
                if (!assignedByApplying.Contains(name)) assignedByApplying.Add(name);
            }

            check(assignedByApplying.Count > 0,
                $"applying the record assigns {assignedByApplying.Count} field(s) of the mod's own",
                "finding none would mean the regions were mis-cut, and an empty comparison always passes");

            var unguarded = new List<string>();
            foreach (string name in assignedByApplying)
                if (!cleared.Contains(name)) unguarded.Add(name);
            check(unguarded.Count == 0,
                unguarded.Count == 0
                    ? "and every one of them is cleared before the file is read"
                    : "ASSIGNED FROM THE FILE, NOT CLEARED FIRST: " + string.Join(", ", unguarded),
                "the reset is what the no-file and the failed-read paths rely on; a field missing from it keeps the previous translation's answer on those two paths");

            // The four that were paid for, named so that dropping one is not a silent edit.
            foreach (string field in new[] { "FileUuid", "LastSyncedHash", "SourceSiteId", "ForkedFromSiteId" })
            {
                check(cleared.Contains(field),
                    $"{field} is cleared by name",
                    "each of these was, or would have been, one file's identity written into another's");
            }

            // 🔴 Every field the record carries is applied. A field read into the record and never
            // taken out of it is the family one level up: read, and thrown away.
            var unapplied = new List<string>();
            foreach (FieldInfo field in typeof(LoadedFile).GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (!appliedRegion.Contains("file." + field.Name, StringComparison.Ordinal)) unapplied.Add(field.Name);
            check(unapplied.Count == 0,
                unapplied.Count == 0
                    ? $"every one of the record's {typeof(LoadedFile).GetFields(BindingFlags.Public | BindingFlags.Instance).Length} fields is applied"
                    : "READ INTO THE RECORD, NEVER APPLIED: " + string.Join(", ", unapplied),
                "a value the file states and the mod does not take is a value the next save writes from memory");

            // 🔴 Every key SaveCache writes is read by the record — the strong one. It is how the
            // family comes back: a block written from memory that nothing reads from the file.
            string saving = BodyOf(text, "public static void SaveCache()");
            check(saving != null, "SaveCache is found", "without it the round trip cannot be checked");
            if (saving != null)
            {
                var written = new List<string>();
                foreach (Match m in MetadataKey.Matches(saving))
                    if (!written.Contains(m.Groups[1].Value)) written.Add(m.Groups[1].Value);
                var unread = new List<string>();
                foreach (string key in written)
                    if (!record.Contains("\"" + key + "\"", StringComparison.Ordinal)) unread.Add(key);
                check(written.Count > 0 && unread.Count == 0,
                    unread.Count == 0
                        ? $"every one of the {written.Count} metadata keys SaveCache writes is read back by the record"
                        : "WRITTEN BY SAVECACHE, READ BY NOBODY: " + string.Join(", ", unread),
                    "a key written and never read is one file's answer carried into the next, with nothing on disk to say so");
            }

            // 🔴 A SECTION is not read by assigning a field — it is handed to whoever owns it. So
            // the rules above cannot see them, and they carried the same defect: a Chinese→English
            // translation came back wearing a Chinese→French one's replacement image, its
            // exclusions and its variables, none of which its own backup held.
            //
            // 🔴 **This used to name the six emptying calls one by one, which made it blind to a
            // SEVENTH section.** Both halves walk the socle's table, so these cases walk it too: a
            // section added to SettingsSections is covered the day it is named.
            check(clearedRegion.Contains("foreach (string section in SettingsSections.All)", StringComparison.Ordinal),
                "every section the socle names is emptied before the file is read",
                "a section a file does not carry means this translation has none, never keep the last one's — and the next save writes it into the file that never had it");
            check(clearedRegion.Contains("ApplySectionAtLoad(section, null)", StringComparison.Ordinal),
                "and emptied through the same door that fills it",
                "two doors is how emptying and reading came to disagree about what a section even is");
            check(record.Contains("SettingsSections.SectionOf(prop.Name)", StringComparison.Ordinal),
                "and the file's keys are named back by that same table, in the record",
                "matching them by hand is a second copy of the list, which nothing compares to the first");
            check(appliedRegion.Contains("foreach (var section in file.Sections)", StringComparison.Ordinal)
                  && appliedRegion.Contains("ApplySectionAtLoad(section.Key, section.Value)", StringComparison.Ordinal),
                "and every section the record carries is applied through it",
                "the door is what makes the fonts' exception a named decision rather than two places that happen to differ");

            // 🔴 The strong one: no section key spelled out anywhere, on either side. It catches a
            // hand-written branch coming back, and it covers a seventh section without being told.
            var spelledOut = new List<string>();
            foreach (string section in SettingsSections.All)
            {
                string key = SettingsSections.JsonKey(section);
                if (key == null) continue;
                if (clearedRegion.Contains("\"" + key + "\"", StringComparison.Ordinal)
                    || appliedRegion.Contains("\"" + key + "\"", StringComparison.Ordinal)
                    || record.Contains("\"" + key + "\"", StringComparison.Ordinal))
                    spelledOut.Add(key);
            }
            check(spelledOut.Count == 0,
                spelledOut.Count == 0
                    ? $"and none of the {SettingsSections.All.Length} keys is written out on either side"
                    : "SPELLED OUT INSTEAD OF ASKED: " + string.Join(", ", spelledOut),
                "a key written here is a branch the socle's table does not know about, so adding a section leaves it unread");
        }

        private static string BodyOf(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return null;
            int open = text.IndexOf('{', start + signature.Length);
            if (open < 0) return null;
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(open, i - open + 1);
                }
            }
            return null;
        }

        private static string FindCore(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var segments = new List<string> { dir.FullName, "UnityGameTranslator.Core" };
                segments.AddRange(parts);
                string candidate = Path.Combine(segments.ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
