using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Everything a translation file tells the mod about ITSELF has to be re-derived from that
    /// file — never inherited from the one loaded before it.
    ///
    /// 🔴 **Four defects of one family, and the fourth was found on a real install.** Each metadata
    /// block in the file is written only when it has something to say, so a file that says nothing
    /// leaves the previous file's answer standing: the branch that reads it simply never runs. It
    /// has cost, in order — the game settings (typewriting and concat stayed off from one
    /// translation to the next), then `_local_changes` and `_metadata_dirty` (the mod claimed local
    /// changes right after downloading the server's own copy), then the lineage identity itself.
    ///
    /// 🔴 **What the last one did**, observed on 2026-09-08: a published English→French translation,
    /// then a never-published English→Thai backup restored over it in the same session. The Thai
    /// file came out carrying `_source: { hash: 57881c8a…, site_id: 12 }` — the French
    /// translation's row and content hash — written into its own file. From then on Thai content
    /// was compared against a French translation's server hash, so the sync verdict disagreed
    /// permanently and nothing could settle it.
    ///
    /// ⚠ **This is a LEXICAL check, and it is deliberate.** LoadCache reads a real file, logs
    /// through the mod loader and talks to Unity, so it cannot be replayed here — but the invariant
    /// is a property of the source: every field the reading assigns must also be cleared before the
    /// reading starts. That is checkable without running anything, and it fires on the one thing
    /// that brings the family back — somebody adding a `_new_key` branch and not the reset.
    ///
    /// ⚠ Until <see cref="TranslationFileEntries"/> grows into a record of the whole file, where
    /// "the previous value survives" stops being expressible, this is what stands in its place.
    /// See analyse/plan-prealables-couches.md, 6r.
    /// </summary>
    internal static class LoadedIdentityChecks
    {
        /// <summary>
        /// A field of the mod's own, assigned: `SomeName = …` at the start of its line.
        ///
        /// ⚠ **Anchored to the line start on purpose.** Matching anywhere read three assignments
        /// out of one interpolated log line — `$"… TW={TypewritingDetection}, Concat={…}"` — and
        /// failed the check on prose. It also would have read a commented-out assignment as a real
        /// one. Every assignment in the two regions below begins its line; nothing inside a string
        /// or after `//` does.
        /// </summary>
        private static readonly Regex Assignment =
            new Regex(@"^[ \t]*(?<name>[A-Z][A-Za-z0-9]*)[ \t]*=[ \t]*(?!=)",
                      RegexOptions.Compiled | RegexOptions.Multiline);

        public static void Run(Action<bool, string, string> check)
        {
            string source = FindCore();
            check(source != null, "TranslatorCore's source is found",
                "this check reads it; without it, it proves nothing");
            if (source == null) return;

            string text = File.ReadAllText(source);

            int loadCache = text.IndexOf("private static void LoadCache()", StringComparison.Ordinal);
            int reading = text.IndexOf("foreach (var prop in parsed.Properties())", StringComparison.Ordinal);
            int entries = text.IndexOf("else if (!prop.Name.StartsWith(\"_\"))", StringComparison.Ordinal);

            check(loadCache >= 0 && reading > loadCache && entries > reading,
                "and its three landmarks are where they are expected",
                "the check is anchored on them; moved or renamed, it must say so rather than pass quietly");
            if (loadCache < 0 || reading <= loadCache || entries <= reading) return;

            // From the start of LoadCache to the loop: where a field is cleared.
            string clearedRegion = text.Substring(loadCache, reading - loadCache);
            // From the loop to the entry branch: where the metadata is read.
            string readRegion = text.Substring(reading, entries - reading);

            var cleared = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Assignment.Matches(clearedRegion)) cleared.Add(m.Groups["name"].Value);

            var assignedByReading = new List<string>();
            foreach (Match m in Assignment.Matches(readRegion))
            {
                string name = m.Groups["name"].Value;
                if (!assignedByReading.Contains(name)) assignedByReading.Add(name);
            }

            check(assignedByReading.Count > 0,
                $"the reading assigns {assignedByReading.Count} field(s) of its own",
                "finding none would mean the regions were mis-cut, and an empty comparison always passes");

            var unguarded = new List<string>();
            foreach (string name in assignedByReading)
                if (!cleared.Contains(name)) unguarded.Add(name);

            check(unguarded.Count == 0,
                unguarded.Count == 0
                    ? "and every one of them is cleared before the file is read"
                    : "READ FROM THE FILE, SURVIVES THE PREVIOUS ONE: " + string.Join(", ", unguarded),
                "a block absent from the file leaves the previous translation's answer standing — that is how a never-published file came to carry another one's server row");

            // The four that were paid for, named so that dropping one is not a silent edit.
            foreach (string field in new[] { "FileUuid", "LastSyncedHash", "SourceSiteId", "ForkedFromSiteId" })
            {
                check(cleared.Contains(field),
                    $"{field} is cleared by name",
                    "each of these was, or would have been, one file's identity written into another's");
            }

            // 🔴 A SECTION is not read by assigning a field — it is handed to whoever owns it. So
            // the rule above could not see five of them, and they carried the same defect: a
            // Chinese→English translation came back wearing a Chinese→French one's replacement
            // image, its exclusions and its variables, none of which its own backup held.
            //
            // 🔴 **This used to name the six emptying calls one by one, which made it blind to a
            // SEVENTH section** — its own weakness, and the reason the reading is now driven by the
            // socle's table instead (2026-09-09). Both halves walk that table, so these cases walk
            // it too: a section added to SettingsSections is covered the day it is named, in the
            // one place where every product already learns about it.
            check(clearedRegion.Contains("foreach (string section in SettingsSections.All)", StringComparison.Ordinal),
                "every section the socle names is emptied before the file is read",
                "a section a file does not carry means this translation has none, never keep the last one's — and the next save writes it into the file that never had it");

            check(clearedRegion.Contains("ApplySectionAtLoad(section, null)", StringComparison.Ordinal),
                "and emptied through the same door that fills it",
                "two doors is how emptying and reading came to disagree about what a section even is");

            check(readRegion.Contains("SettingsSections.SectionOf(prop.Name)", StringComparison.Ordinal),
                "and the file's keys are named back by that same table",
                "matching them by hand is a second copy of the list, which nothing compares to the first");

            check(readRegion.Contains("ApplySectionAtLoad(section, prop.Value)", StringComparison.Ordinal),
                "and applied through it",
                "the door is what makes the fonts' exception a named decision rather than two places that happen to differ");

            // 🔴 The strong one: no section key spelled out anywhere in either region. It catches a
            // hand-written branch coming back, and it covers a seventh section without being told.
            var spelledOut = new List<string>();
            foreach (string section in SettingsSections.All)
            {
                string key = SettingsSections.JsonKey(section);
                if (key == null) continue;
                if (clearedRegion.Contains("\"" + key + "\"", StringComparison.Ordinal)
                    || readRegion.Contains("\"" + key + "\"", StringComparison.Ordinal))
                    spelledOut.Add(key);
            }

            check(spelledOut.Count == 0,
                spelledOut.Count == 0
                    ? $"and none of the {SettingsSections.All.Length} keys is written out here"
                    : "SPELLED OUT INSTEAD OF ASKED: " + string.Join(", ", spelledOut),
                "a key written here is a branch the socle's table does not know about, so adding a section leaves it unread");
        }

        private static string FindCore()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "UnityGameTranslator.Core", "TranslatorCore.cs");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
