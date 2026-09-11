using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The moments of the translation file, replayed on a real folder: after a download, an edit,
    /// a merge, an upload, a fork, what do the stamps and the ancestors say. The sequences are
    /// <c>common/spec/translation-file/moments.json</c>, shared with the Manager's executor; a
    /// few mechanics the cases cannot name (settings beside the ancestor, refusals) are checked
    /// directly underneath.
    ///
    /// 🔴 A moment only exists in a sequence. "After a merge the ancestor is the published
    /// content" was a sentence in a doc and an order of statements in three callers; here it is
    /// a case, and a wrong MOMENT goes red.
    /// </summary>
    internal static class TranslationStoreChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string casesPath = Find("common", "spec", "translation-file", "moments.json");
            check(casesPath != null, "the moments' cases are found", "this check reads them; without them, it proves nothing");
            if (casesPath == null) return;

            var doc = JObject.Parse(File.ReadAllText(casesPath));
            int held = 0, skipped = 0;

            foreach (JObject c in (JArray)doc["cases"])
            {
                string id = (string)c["id"];
                string why = (string)c["why"];
                var heldBy = ((JArray)c["held_by"]).Select(t => (string)t).ToList();
                if (!heldBy.Contains("mod")) { skipped++; continue; }
                held++;

                string folder = Path.Combine(Path.GetTempPath(), "ugt-moments-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var failures = Replay(folder, (JArray)c["acts"], (JObject)c["expect"]);
                    check(failures.Count == 0, id, failures.Count == 0 ? why : string.Join("; ", failures) + " — " + why);
                }
                catch (Exception e)
                {
                    check(false, id, $"the replay threw {e.GetType().Name}: {e.Message} — {why}");
                }
                finally
                {
                    try { Directory.Delete(folder, true); } catch { /* a temp folder left behind proves nothing */ }
                }
            }

            check(held >= 10, $"{held} cases held here, {skipped} left to the Manager", "fewer than the contract holds: the file was mis-read");

            // ── What the cases cannot say ─────────────────────────────────────
            string extra = Path.Combine(Path.GetTempPath(), "ugt-moments-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extra);
            try
            {
                string path = Path.Combine(extra, "translations.json");
                var store = new TranslationStore(path);
                var lines = Lines(new JObject { ["Play"] = new JObject { ["v"] = "Jouer", ["t"] = "H" } });
                var settings = new JObject { ["_fonts"] = new JObject { ["Arial"] = "Tahoma" } };

                store.NoteSynced("h1", 12, lines, settings);
                var again = new TranslationStore(path);
                again.LoadAncestor();
                check(again.AncestorSettings != null && JToken.DeepEquals(again.AncestorSettings, settings),
                    "the settings beside the ancestor come back as written",
                    "without them there is no way to tell 'the other side changed this' from 'I changed this'");

                store.NoteMerged("h2", lines, null, lines);
                again = new TranslationStore(path);
                again.LoadAncestor();
                check(again.AncestorSettings == null,
                    "a merge whose settings were never seen leaves the baseline unknown",
                    "an invented baseline is worse than none: the next comparison would trust it");

                bool refused = false;
                try { store.Fork("", 1, "c", 1); } catch (ArgumentException) { refused = true; }
                check(refused, "a fork without a uuid is refused", "a lineage is its uuid");

                var output = new JObject();
                new TranslationStore(path).WriteStampsInto(output);
                check(output["_source"] == null && output["_local_changes"] == null && output["_forked_from"] == null,
                    "a store that knows nothing stamps nothing", "absent is the written form of 'nothing to say'");
            }
            finally
            {
                try { Directory.Delete(extra, true); } catch { }
            }
        }

        private static List<string> Replay(string folder, JArray acts, JObject expect)
        {
            string path = Path.Combine(folder, "translations.json");
            var store = new TranslationStore(path);
            var lines = new Dictionary<string, TranslationEntry>();
            JObject written = null;
            store.Uuid = "00000000-0000-4000-8000-000000000001";

            foreach (JObject act in acts)
            {
                var prop = act.Properties().First();
                var a = (JObject)prop.Value;
                switch (prop.Name)
                {
                    case "download":
                        lines = Lines((JObject)a["lines"]);
                        store.NoteSynced((string)a["hash"], (int?)a["site_id"], lines, null);
                        break;
                    case "edit":
                    {
                        var entry = new TranslationEntry { Value = (string)a["v"], Tag = (string)a["t"] ?? "A" };
                        lines[(string)a["key"]] = entry;
                        store.NoteLocalEdit((string)a["key"], entry);
                        break;
                    }
                    case "remove":
                        lines.Remove((string)a["key"]);
                        break;
                    case "write":
                        store.Recount(lines);
                        written = new JObject { ["_uuid"] = store.Uuid };
                        store.WriteStampsInto(written);
                        TranslationFileEntries.WriteInto(written, lines);
                        break;
                    case "merge":
                    {
                        var published = a["published"] is JObject p ? Lines(p) : null;
                        var merged = Lines((JObject)a["merged"]);
                        lines = merged;
                        store.NoteMerged((string)a["hash"], published, null, merged);
                        break;
                    }
                    case "upload":
                        store.NoteSynced((string)a["hash"], (int?)a["site_id"], lines, null);
                        break;
                    case "main_merge":
                        store.NoteMainMerged(Lines((JObject)a["lines"]), (string)a["hash"], null);
                        break;
                    case "fork":
                        store.Fork((string)a["uuid"], (int)a["resolved_lines"], (string)a["content_hash"], lines.Count);
                        break;
                    case "load":
                    {
                        if (written == null) throw new InvalidOperationException("load needs a write before it");
                        var file = LoadedFile.Read(written);
                        store = new TranslationStore(path);
                        store.TakeIdentity(file);
                        store.LoadAncestor();
                        lines = new Dictionary<string, TranslationEntry>(file.Entries.Entries);
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"unknown act '{prop.Name}'");
                }
            }

            var failures = new List<string>();
            foreach (var e in expect)
            {
                switch (e.Key)
                {
                    case "uuid": Same(failures, e.Key, (string)e.Value, store.Uuid); break;
                    case "source_hash": Same(failures, e.Key, (string)e.Value, store.SourceHash); break;
                    case "site_id": Same(failures, e.Key, (int?)e.Value, store.SiteId); break;
                    case "main_hash": Same(failures, e.Key, (string)e.Value, store.MainHash); break;
                    case "local_changes": Same(failures, e.Key, (int)e.Value, store.LocalChanges); break;
                    case "forked_from":
                    {
                        var actual = store.ForkedFromSiteId.HasValue
                            ? new JObject
                            {
                                ["site_id"] = store.ForkedFromSiteId.Value,
                                ["hash"] = store.ForkedFromHash,
                                ["resolved_lines"] = store.ForkedFromResolvedLines,
                                ["content_hash"] = store.ForkedFromContentHash,
                            }
                            : null;
                        if (e.Value.Type == JTokenType.Null ? actual != null : actual == null || !JToken.DeepEquals(Strip(actual), e.Value))
                            failures.Add($"forked_from: expected {e.Value.ToString(Newtonsoft.Json.Formatting.None)}, got {(actual == null ? "null" : actual.ToString(Newtonsoft.Json.Formatting.None))}");
                        break;
                    }
                    case "ancestor": SameFile(failures, e.Key, e.Value, store.AncestorPath); break;
                    case "main_ancestor": SameFile(failures, e.Key, e.Value, store.MainAncestorPath); break;
                    default: failures.Add($"{e.Key}: not a fact this side derives"); break;
                }
            }
            return failures;
        }

        private static void Same<T>(List<string> failures, string fact, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                failures.Add($"{fact}: expected {(expected == null ? "null" : expected.ToString())}, got {(actual == null ? "null" : actual.ToString())}");
        }

        private static void SameFile(List<string> failures, string fact, JToken expected, string path)
        {
            bool exists = File.Exists(path);
            if (expected.Type == JTokenType.String && (string)expected == "absent")
            {
                if (exists) failures.Add($"{fact}: expected no file, found one");
                return;
            }
            if (!exists) { failures.Add($"{fact}: expected lines, found no file"); return; }

            var parsed = JObject.Parse(File.ReadAllText(path));
            var actual = new JObject();
            foreach (var p in parsed.Properties())
                if (!p.Name.StartsWith("_")) actual[p.Name] = p.Value;
            if (!JToken.DeepEquals(actual, expected))
                failures.Add($"{fact}: expected {expected.ToString(Newtonsoft.Json.Formatting.None)}, got {actual.ToString(Newtonsoft.Json.Formatting.None)}");
        }

        private static JObject Strip(JObject o)
        {
            var copy = new JObject();
            foreach (var p in o.Properties())
                if (p.Value.Type != JTokenType.Null) copy[p.Name] = p.Value;
            return copy;
        }

        private static Dictionary<string, TranslationEntry> Lines(JObject o)
        {
            var d = new Dictionary<string, TranslationEntry>();
            foreach (var p in o.Properties())
                d[p.Name] = new TranslationEntry { Value = (string)p.Value["v"], Tag = (string)p.Value["t"] ?? "A" };
            return d;
        }

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
