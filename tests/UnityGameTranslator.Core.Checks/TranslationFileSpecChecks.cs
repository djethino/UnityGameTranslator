using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The mod's reader against the contract: <c>common/spec/translation-file/cases.json</c>, the
    /// documents a Core may meet and what a reader must derive from each.
    ///
    /// 🔴 **The cases are the specification, not the C#.** They are written from the contract
    /// (analyse/inventaire-couches/inventaire-contrat.md §3.1: the line format, the tags, the
    /// CRLF rule, the metadata); the site's PHPUnit reads the same file for its half. A reader
    /// that derives something else from a document is wrong even if it has always done so — and
    /// then the case, or the reader, is changed on purpose.
    ///
    /// ⚠ Only cases carrying <c>read</c> concern this side; <c>written</c> is the schema's
    /// (check-spec.py) and <c>upload</c> the site's.
    /// </summary>
    internal static class TranslationFileSpecChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string casesPath = Find("common", "spec", "translation-file", "cases.json");
            check(casesPath != null, "the spec's cases are found",
                "this check reads them; without them, it proves nothing");
            if (casesPath == null) return;

            var doc = JObject.Parse(File.ReadAllText(casesPath));
            var cases = (JArray)doc["cases"];
            int withRead = 0;

            foreach (JObject c in cases)
            {
                string id = (string)c["id"];
                var read = c["read"];
                if (read == null) continue;
                withRead++;

                string why = (string)c["why"];
                var document = (JObject)c["document"];

                if (read.Type == JTokenType.String && (string)read == "throws")
                {
                    bool threw = false;
                    try { LoadedFile.Read(document); } catch (Exception) { threw = true; }
                    check(threw, id, why);
                    continue;
                }

                LoadedFile file;
                try { file = LoadedFile.Read(document); }
                catch (Exception e)
                {
                    check(false, id, $"the reader threw {e.GetType().Name}: {e.Message} — {why}");
                    continue;
                }

                var expected = (JObject)read;
                var wrong = new List<string>();

                void Expect<T>(string field, T actual)
                {
                    if (!expected.TryGetValue(field, out var want)) return;
                    var wantValue = want.Type == JTokenType.Null ? default : want.ToObject<T>();
                    if (!EqualityComparer<T>.Default.Equals(wantValue, actual))
                        wrong.Add($"{field}: expected {Show(want)}, got {Show(actual)}");
                }

                Expect("uuid", file.Uuid);
                Expect("engine_version", file.EngineVersion);
                Expect("source_language", file.SourceLanguage);
                Expect("target_language", file.TargetLanguage);
                Expect("local_changes", file.LocalChanges);
                Expect("metadata_dirty", file.MetadataDirty);
                Expect("source_hash", file.LastSyncedHash);
                Expect("main_hash", file.LastMergedMainHash);
                Expect("site_id", file.SourceSiteId);
                Expect("forked_from_site_id", file.ForkedFromSiteId);
                Expect("forked_from_resolved_lines", file.ForkedFromResolvedLines);
                Expect("forked_from_content_hash", file.ForkedFromContentHash);
                Expect("steam_id", file.SavedSteamId);
                Expect("rewrite", file.Entries.NeedsRewrite);
                Expect("stranded_interface", file.Entries.StrandedModUi?.Count ?? 0);

                if (expected.TryGetValue("sections", out var sections))
                {
                    var want = sections.Select(s => (string)s).ToList();
                    var got = file.Sections.Select(s => Common.SettingsSections.JsonKey(s.Key)).ToList();
                    if (!want.SequenceEqual(got))
                        wrong.Add($"sections: expected [{string.Join(", ", want)}], got [{string.Join(", ", got)}]");
                }

                if (expected.TryGetValue("lines", out var lines))
                {
                    var wantLines = (JObject)lines;
                    var got = file.Entries.Entries;
                    if (wantLines.Count != got.Count)
                        wrong.Add($"lines: expected {wantLines.Count}, got {got.Count} ({string.Join(", ", got.Keys.Select(Show))})");
                    foreach (var line in wantLines.Properties())
                    {
                        if (!got.TryGetValue(line.Name, out var entry))
                        {
                            wrong.Add($"line {Show(line.Name)}: absent");
                            continue;
                        }
                        var spec = (JObject)line.Value;
                        string v = (string)spec["v"] ?? "";
                        string t = (string)spec["t"];
                        if (entry.Value != v) wrong.Add($"line {Show(line.Name)}: v expected {Show(v)}, got {Show(entry.Value)}");
                        if (entry.Tag != t) wrong.Add($"line {Show(line.Name)}: t expected {t}, got {entry.Tag}");
                        if (spec.TryGetValue("i", out var i))
                        {
                            long? wantIndex = i.Type == JTokenType.Null ? (long?)null : (long)i;
                            if (entry.Index != wantIndex) wrong.Add($"line {Show(line.Name)}: i expected {wantIndex?.ToString() ?? "absent"}, got {entry.Index?.ToString() ?? "absent"}");
                        }
                    }
                }

                check(wrong.Count == 0, id, wrong.Count == 0 ? why : string.Join("; ", wrong));
            }

            check(withRead >= 10,
                $"{withRead} cases say what a reader derives",
                "fewer than that would mean the file was mis-read, and an empty comparison always passes");
        }

        private static string Show(object value)
        {
            if (value == null) return "null";
            if (value is JToken t) return t.Type == JTokenType.Null ? "null" : t.ToString(Newtonsoft.Json.Formatting.None);
            if (value is string s) return "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
            return value.ToString();
        }

        /// <summary>Walk up from the binary to the mod's checkout, where common/ is the submodule.</summary>
        private static string Find(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var segments = new List<string> { dir.FullName };
                segments.AddRange(parts);
                string candidate = Path.Combine(segments.ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
