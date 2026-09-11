using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The mod's readers against the API contract: <c>common/spec/api-v1/cases.json</c>, the
    /// answers the site gives and what a client must derive from each.
    ///
    /// 🔴 **The cases are the specification, not the C#.** The site replays the same file with
    /// PHPUnit (its half: building the setup, sending the request, matching the answer); this
    /// side feeds each case's <c>response.body</c> to the reader the case names and compares what
    /// comes out with <c>read.expects</c>. A reader that derives something else from an answer
    /// is wrong even if it has always done so — and then the case, or the reader, is changed on
    /// purpose.
    ///
    /// ⚠ Only cases carrying <c>read</c> concern this side. The markers a case body carries
    /// (<c>$is</c>, <c>$ref</c>, <c>$contains</c>, <c>$absent</c>) are resolved here the same way
    /// check-spec.py resolves them: a sample of the right kind, a stable number for a setup id,
    /// the text itself, the key removed.
    /// </summary>
    internal static class ApiContractChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string casesPath = Find("common", "spec", "api-v1", "cases.json");
            check(casesPath != null, "the contract's cases are found",
                "this check reads them; without them, it proves nothing");
            if (casesPath == null) return;

            var doc = JObject.Parse(File.ReadAllText(casesPath));
            var cases = (JArray)doc["cases"];
            int withRead = 0;
            var readersSeen = new HashSet<string>();

            foreach (JObject c in cases)
            {
                string id = (string)c["id"];
                var read = c["read"] as JObject;
                if (read == null) continue;
                withRead++;

                string why = (string)c["why"];
                string reader = (string)read["reader"];
                readersSeen.Add(reader);
                var response = (JObject)c["response"];
                int status = (int)response["status"];
                var body = Resolve(response["body"]) as JObject;
                var input = read["input"] as JObject;

                Dictionary<string, object> derived;
                try
                {
                    derived = Derive(reader, status, body, input);
                }
                catch (Exception e)
                {
                    check(false, id, $"the reader '{reader}' threw {e.GetType().Name}: {e.Message} — {why}");
                    continue;
                }

                if (derived == null)
                {
                    check(false, id, $"no reader named '{reader}' on this side — {why}");
                    continue;
                }

                var failures = new List<string>();
                foreach (var expectation in (JObject)read["expects"])
                {
                    if (!derived.TryGetValue(expectation.Key, out var actual))
                    {
                        failures.Add($"{expectation.Key}: not a value this reader derives");
                        continue;
                    }

                    string detail = Agrees(expectation.Value, actual);
                    if (detail != null) failures.Add($"{expectation.Key}: {detail}");
                }

                check(failures.Count == 0, id,
                    failures.Count == 0 ? why : string.Join("; ", failures) + " — " + why);
            }

            check(withRead >= 30, $"{withRead} cases carry a reading for this side",
                "fewer than the contract holds: the file was mis-read");

            // Every reader this side has must be held by at least one case, or a reader can drift
            // with nothing to say so — the same coverage rule as the corpus's `sides`.
            foreach (string reader in Readers)
            {
                check(readersSeen.Contains(reader), $"reader '{reader}' has a case",
                    "a reader no case holds is a reader free to drift");
            }
        }

        private static readonly string[] Readers =
        {
            "search_by_steam_id", "search_by_name", "check", "check_uuid", "branches", "upload",
            "details", "vote", "games", "games_external", "access_code", "notifications",
            "device_flow", "merge_preview_init", "merge_preview_result", "edit_session_init",
            "edit_session_update", "edit_session_state", "error",
        };

        /// <summary>
        /// What the named reader derives from an answer, flattened to the dotted paths a case
        /// names. Null when no reader has that name.
        /// </summary>
        private static Dictionary<string, object> Derive(string reader, int status, JObject body, JObject input)
        {
            var d = new Dictionary<string, object>();

            switch (reader)
            {
                case "search_by_steam_id":
                {
                    var r = ApiReaders.ReadSearch(body);
                    d["Success"] = r.Success;
                    d["Count"] = r.Count;
                    Rows(d, r.Translations);
                    return d;
                }
                case "search_by_name":
                {
                    var r = ApiReaders.ReadSearchByName(body, (string)input?["game_name"] ?? "");
                    d["Success"] = r.Success;
                    d["Count"] = r.Count;
                    Rows(d, r.Translations);
                    return d;
                }
                case "check":
                {
                    var r = ApiReaders.ReadCheck(body, (string)input?["local_hash"], "\"etag\"");
                    d["Success"] = r.Success;
                    d["HasUpdate"] = r.HasUpdate;
                    d["NotModified"] = r.NotModified;
                    d["FileHash"] = r.FileHash;
                    d["LineCount"] = r.LineCount;
                    d["VoteCount"] = r.VoteCount;
                    d["Uploader"] = r.Uploader;
                    return d;
                }
                case "check_uuid":
                {
                    var r = ApiReaders.ReadUuidCheck(body);
                    d["Success"] = r.Success;
                    d["Exists"] = r.Exists;
                    d["IsOwner"] = r.IsOwner;
                    d["Role"] = r.Role.ToString();
                    d["MainUsername"] = r.MainUsername;
                    d["MainMissing"] = r.MainMissing;
                    d["MainAbandoned"] = r.MainAbandoned;
                    d["BranchesCount"] = r.BranchesCount;
                    d["AcceptsBranches"] = r.AcceptsBranches;
                    d["BranchFrozen"] = r.BranchFrozen;
                    d["BranchesWithWork"] = r.BranchesWithWork;
                    d["LinesAvailable"] = r.LinesAvailable;
                    d["LinesToReview"] = r.LinesToReview;
                    d["LinesNew.Human"] = r.LinesNew.Human;
                    d["LinesNew.Validated"] = r.LinesNew.Validated;
                    d["LinesNew.Machine"] = r.LinesNew.Machine;
                    d["LinesNew.Skipped"] = r.LinesNew.Skipped;
                    d["LinesDiffering.Human"] = r.LinesDiffering.Human;
                    d["LinesDiffering.Machine"] = r.LinesDiffering.Machine;
                    d["LinesOffered"] = r.LinesOffered;
                    d["Vote"] = r.Vote == null ? null : "present";
                    d["Vote.CanVote"] = r.Vote?.CanVote;
                    d["Vote.Count"] = r.Vote?.Count;
                    d["Vote.UserVote"] = r.Vote?.UserVote;
                    d["ExistingTranslation"] = r.ExistingTranslation == null ? null : "present";
                    d["ExistingTranslation.Id"] = r.ExistingTranslation?.Id;
                    d["ExistingTranslation.Status"] = r.ExistingTranslation?.Status;
                    d["ExistingTranslation.Notes"] = r.ExistingTranslation?.Notes;
                    d["ExistingTranslation.ResourcesUrl"] = r.ExistingTranslation?.ResourcesUrl;
                    d["ExistingTranslation.OwnResourcesUrl"] = r.ExistingTranslation?.OwnResourcesUrl;
                    d["ExistingTranslation.LineCount"] = r.ExistingTranslation?.LineCount;
                    d["OriginalTranslation"] = r.OriginalTranslation == null ? null : "present";
                    d["OriginalTranslation.Uploader"] = r.OriginalTranslation?.Uploader;
                    d["OriginalTranslation.LineCount"] = r.OriginalTranslation?.LineCount;
                    return d;
                }
                case "branches":
                {
                    var r = ApiReaders.ReadBranches(body);
                    d["Success"] = r.Success;
                    d["Branches.Count"] = r.Branches.Count;
                    for (int i = 0; i < r.Branches.Count; i++)
                    {
                        d[$"Branches.{i}.Username"] = r.Branches[i].Username;
                        d[$"Branches.{i}.LineCount"] = r.Branches[i].LineCount;
                        d[$"Branches.{i}.HumanCount"] = r.Branches[i].HumanCount;
                        d[$"Branches.{i}.AiCount"] = r.Branches[i].AiCount;
                    }
                    return d;
                }
                case "upload":
                {
                    var r = ApiReaders.ReadUploadAnswer(status, body);
                    d["Success"] = r.Success;
                    d["Error"] = r.Error;
                    d["Role"] = r.Role.ToString();
                    d["LineCount"] = r.LineCount;
                    d["TranslationId"] = r.TranslationId;
                    d["FileHash"] = r.FileHash;
                    d["WebUrl"] = r.WebUrl;
                    return d;
                }
                case "details":
                {
                    var r = ApiReaders.ReadDetails(status, body);
                    d["Success"] = r.Success;
                    d["Error"] = r.Error;
                    return d;
                }
                case "vote":
                {
                    var r = ApiReaders.ReadVote(status, body);
                    d["Success"] = r.Success;
                    d["Error"] = r.Error;
                    d["VoteCount"] = r.VoteCount;
                    d["UserVote"] = r.UserVote;
                    return d;
                }
                case "games":
                {
                    var r = ApiReaders.ReadGames(body);
                    d["Success"] = r.Success;
                    d["Count"] = r.Count;
                    for (int i = 0; i < r.Games.Count; i++)
                    {
                        d[$"Games.{i}.Name"] = r.Games[i].Name;
                        d[$"Games.{i}.Slug"] = r.Games[i].Slug;
                        d[$"Games.{i}.TranslationsCount"] = r.Games[i].TranslationsCount;
                    }
                    return d;
                }
                case "games_external":
                {
                    var r = ApiReaders.ReadExternalGames(body);
                    d["Success"] = r.Success;
                    d["Count"] = r.Count;
                    for (int i = 0; i < r.Games.Count; i++)
                    {
                        d[$"Games.{i}.Name"] = r.Games[i].Name;
                        d[$"Games.{i}.Source"] = r.Games[i].Source;
                        d[$"Games.{i}.SteamId"] = r.Games[i].SteamId;
                    }
                    return d;
                }
                case "access_code":
                    d["AccessCode"] = ApiReaders.ReadAccessCode(body);
                    return d;
                case "notifications":
                {
                    var r = ApiReaders.ReadNotifications(body);
                    d["Success"] = r.Success;
                    d["Unread"] = r.Unread;
                    for (int i = 0; i < r.Items.Count; i++)
                    {
                        d[$"Items.{i}.Type"] = r.Items[i].Type;
                        d[$"Items.{i}.Text"] = r.Items[i].Text;
                        d[$"Items.{i}.Url"] = r.Items[i].Url;
                    }
                    return d;
                }
                case "device_flow":
                {
                    var r = ApiReaders.ReadDeviceFlow(body);
                    d["Success"] = r.Success;
                    d["DeviceCode"] = r.DeviceCode;
                    d["UserCode"] = r.UserCode;
                    d["ExpiresIn"] = r.ExpiresIn;
                    d["Interval"] = r.Interval;
                    return d;
                }
                case "merge_preview_init":
                {
                    if (status < 200 || status >= 300)
                    {
                        d["Success"] = false;
                        d["Error"] = ApiReaders.DescribeError(status, body, 0);
                        return d;
                    }
                    var r = ApiReaders.ReadMergePreviewInit(body);
                    d["Success"] = r.Success;
                    d["Token"] = r.Token;
                    d["Url"] = r.Url;
                    return d;
                }
                case "merge_preview_result":
                {
                    var r = ApiReaders.ReadMergePreviewResult(status, body, 0);
                    d["Success"] = r.Success;
                    d["Error"] = r.Error;
                    d["Content"] = r.Content;
                    return d;
                }
                case "edit_session_init":
                {
                    var r = ApiReaders.ReadEditSessionInit(body);
                    d["Success"] = r.Success;
                    d["ModKey"] = r.ModKey;
                    d["Url"] = r.Url;
                    return d;
                }
                case "edit_session_update":
                {
                    var r = ApiReaders.ReadEditSessionUpdate(status, body);
                    d["Success"] = r.Success;
                    d["SessionGone"] = r.SessionGone;
                    d["ContentHash"] = r.ContentHash;
                    d["BrowserSeenSecondsAgo"] = r.BrowserSeenSecondsAgo;
                    d["BrowserLeft"] = r.BrowserLeft;
                    return d;
                }
                case "edit_session_state":
                {
                    var r = ApiReaders.ReadEditSessionState(status, body);
                    d["Exists"] = r.Exists;
                    d["PendingChanges"] = r.PendingChanges;
                    return d;
                }
                case "error":
                    d["Message"] = ApiReaders.DescribeError(status, body, 0);
                    return d;
                default:
                    return null;
            }
        }

        private static void Rows(Dictionary<string, object> d, List<TranslationInfo> rows)
        {
            d["Translations.Count"] = rows.Count;
            for (int i = 0; i < rows.Count; i++)
            {
                var t = rows[i];
                d[$"Translations.{i}.Id"] = t.Id;
                d[$"Translations.{i}.GameName"] = t.GameName;
                d[$"Translations.{i}.GameSlug"] = t.GameSlug;
                d[$"Translations.{i}.Uploader"] = t.Uploader;
                d[$"Translations.{i}.AcceptsBranches"] = t.AcceptsBranches;
                d[$"Translations.{i}.Origin"] = t.Origin.HasValue ? "present" : null;
                d[$"Translations.{i}.UserVote"] = t.UserVote;
                d[$"Translations.{i}.VoteCount"] = t.VoteCount;
                d[$"Translations.{i}.SkippedCount"] = t.SkippedCount;
                d[$"Translations.{i}.LineCount"] = t.LineCount;
                d[$"Translations.{i}.Status"] = t.Status;
                d[$"Translations.{i}.Type"] = t.Type;
                d[$"Translations.{i}.GameCoverage"] = t.GameCoverage;
            }
        }

        // ── The markers, resolved as check-spec.py resolves them ───────────────────────────

        private static JToken Resolve(JToken value)
        {
            if (value is JObject obj)
            {
                if (obj["$is"] != null) return SampleFor((string)obj["$is"]);
                if (obj["$ref"] != null) return new JValue(StableId((string)obj["$ref"]));
                if (obj["$contains"] != null) return new JValue((string)obj["$contains"]);
                if (obj["$any"] != null) return new JValue(1);

                var outObj = new JObject();
                foreach (var prop in obj.Properties())
                {
                    if (prop.Value is JObject inner && inner["$absent"] != null) continue;
                    outObj[prop.Name] = Resolve(prop.Value);
                }
                return outObj;
            }
            if (value is JArray arr)
            {
                var outArr = new JArray();
                foreach (var item in arr) outArr.Add(Resolve(item));
                return outArr;
            }
            return value?.DeepClone();
        }

        private static JToken SampleFor(string kind)
        {
            switch (kind)
            {
                case "integer": return new JValue(1);
                case "number": return new JValue(1.5);
                case "string": return new JValue("x");
                case "boolean": return new JValue(true);
                case "iso8601": return new JValue("2026-09-11T14:03:22+00:00");
                case "file_hash": return new JValue(new string('a', 64));
                case "session_key": return new JValue(new string('A', 64));
                case "url": return new JValue("https://example.test/x");
                default: return new JValue(1);
            }
        }

        /// <summary>A setup id this side never has: a stable positive number drawn from the name.</summary>
        private static int StableId(string reference)
        {
            int sum = 7;
            foreach (char ch in reference) sum = (sum * 31 + ch) % 100000;
            return sum + 1;
        }

        /// <summary>Null when the derived value is what the expectation says, otherwise what differs.</summary>
        private static string Agrees(JToken expected, object actual)
        {
            if (expected is JObject marker && marker["$is"] != null)
            {
                string kind = (string)marker["$is"];
                bool ok = kind == "string" ? actual is string s && s.Length > 0
                        : kind == "integer" ? actual is int
                        : kind == "boolean" ? actual is bool
                        : actual != null;
                return ok ? null : $"expected {kind}, got {Show(actual)}";
            }
            if (expected is JObject refMarker && refMarker["$ref"] != null)
            {
                int wanted = StableId((string)refMarker["$ref"]);
                return Equals(actual, wanted) ? null : $"expected {wanted}, got {Show(actual)}";
            }

            switch (expected.Type)
            {
                case JTokenType.Null:
                    return actual == null ? null : $"expected null, got {Show(actual)}";
                case JTokenType.Boolean:
                    return Equals(actual, (bool)expected) ? null : $"expected {expected}, got {Show(actual)}";
                case JTokenType.Integer:
                    return actual != null && Convert.ToInt64(actual) == (long)expected ? null : $"expected {expected}, got {Show(actual)}";
                case JTokenType.Float:
                    return actual != null && Math.Abs(Convert.ToDouble(actual) - (double)expected) < 1e-6 ? null : $"expected {expected}, got {Show(actual)}";
                default:
                    return string.Equals(actual as string, (string)expected, StringComparison.Ordinal) ? null : $"expected \"{expected}\", got {Show(actual)}";
            }
        }

        private static string Show(object value) => value == null ? "null" : $"\"{value}\"";

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
