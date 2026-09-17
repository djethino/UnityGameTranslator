using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The mod's half of the streams: <c>common/spec/sse-events/cases.json</c>, the `frame` and
    /// `read` cases.
    ///
    /// 🔴 **The relay has no executor of its own** — it is one file without exports — so its wire
    /// is proved from THIS end: the frames here are written in the relay's exact format (read
    /// from server.js), and the parser <see cref="SseStream"/> is held to yield from them what the
    /// case says. The `read` cases then hold <see cref="ApiReaders"/> to what each event's data
    /// must derive — including the sync `state` read OVER a previous state, which is the sequence
    /// rule the stream imposes and the one that wiped a card once.
    ///
    /// The site's half (`publish` cases) is tests/Unit/SsePublisherContractTest.php, on the same file.
    /// </summary>
    internal static class SseEventsChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string casesPath = Find("common", "spec", "sse-events", "cases.json");
            check(casesPath != null, "the streams' cases are found",
                "this check reads them; without them, it proves nothing");
            if (casesPath == null) return;

            var doc = JObject.Parse(File.ReadAllText(casesPath));
            int frames = 0, reads = 0;

            foreach (JObject c in (JArray)doc["cases"])
            {
                string id = (string)c["id"];
                string why = (string)c["why"];
                string kind = (string)c["kind"];

                try
                {
                    if (kind == "frame") { frames++; Frame(check, c, id, why); }
                    else if (kind == "read") { reads++; Read(check, c, id, why); }
                }
                catch (Exception e)
                {
                    check(false, id, $"threw {e.GetType().Name}: {e.Message} — {why}");
                }
            }

            check(frames >= 4, $"{frames} frame case(s) read", "fewer than the file holds: mis-read");
            check(reads >= 10, $"{reads} read case(s) read", "fewer than the file holds: mis-read");
        }

        // ── The wire ────────────────────────────────────────────────────────────────────────

        private static void Frame(Action<bool, string, string> check, JObject c, string id, string why)
        {
            var yielded = new List<SseEvent>();
            var stream = new SseStream(new StringReader((string)c["text"]))
            {
                Now = () => new DateTime(2026, 9, 11, 14, 0, 0, DateTimeKind.Utc),
                TickMs = 10,
                StopAfterFirstEvent = c["stop_after_first"]?.Value<bool>() ?? false,
                OnEvent = evt => yielded.Add(evt),
                Warning = _ => { },
            };

            var stopped = stream.Run(CancellationToken.None).GetAwaiter().GetResult();

            var failures = new List<string>();
            string expectedStop = (string)c["stop"];
            if (stopped.ToString() != expectedStop) failures.Add($"stopped {stopped}, expected {expectedStop}");

            if (c["retry_ms"] != null && stream.RetryDelayMs != (int)c["retry_ms"])
                failures.Add($"retry {stream.RetryDelayMs?.ToString() ?? "none"}, expected {c["retry_ms"]}");

            string expectedLast = c["last_event_id"]?.Type == JTokenType.Null ? null : (string)c["last_event_id"];
            if (c["last_event_id"] != null && stream.LastEventId != expectedLast)
                failures.Add($"last id {stream.LastEventId ?? "none"}, expected {expectedLast ?? "none"}");

            var expected = (JArray)c["events"];
            if (yielded.Count != expected.Count)
            {
                failures.Add($"{yielded.Count} event(s) yielded, expected {expected.Count}");
            }
            else
            {
                for (int i = 0; i < expected.Count; i++)
                {
                    var want = (JObject)expected[i];
                    var got = yielded[i];
                    string wantId = want["id"]?.Type == JTokenType.Null ? null : (string)want["id"];
                    if (got.Id != wantId) failures.Add($"event {i}: id {got.Id ?? "none"}, expected {wantId ?? "none"}");
                    if (got.EventType != (string)want["event"]) failures.Add($"event {i}: type {got.EventType}, expected {want["event"]}");
                    if (!JToken.DeepEquals(JToken.Parse(got.Data), want["data"])) failures.Add($"event {i}: data {got.Data}");
                }
            }

            check(failures.Count == 0, id, failures.Count == 0 ? why : string.Join("; ", failures) + " — " + why);
        }

        // ── What a reader derives ───────────────────────────────────────────────────────────

        private static void Read(Action<bool, string, string> check, JObject c, string id, string why)
        {
            string reader = (string)c["reader"];
            var payload = (JObject)c["payload"];
            var input = c["input"] as JObject;
            var derived = Derive(reader, payload, c["previous_payload"] as JObject, input);

            if (derived == null)
            {
                check(false, id, $"no reader named '{reader}' on this side — {why}");
                return;
            }

            var failures = new List<string>();
            foreach (var expectation in (JObject)c["expects"])
            {
                if (!derived.TryGetValue(expectation.Key, out var actual))
                {
                    failures.Add($"{expectation.Key}: not a value this reader derives");
                    continue;
                }
                string detail = Agrees(expectation.Value, actual);
                if (detail != null) failures.Add($"{expectation.Key}: {detail}");
            }

            check(failures.Count == 0, id, failures.Count == 0 ? why : string.Join("; ", failures) + " — " + why);
        }

        private static Dictionary<string, object> Derive(string reader, JObject payload, JObject previousPayload, JObject input)
        {
            var d = new Dictionary<string, object>();
            switch (reader)
            {
                case "device_authorized":
                {
                    var r = ApiReaders.ReadDeviceAuthorized(payload);
                    d["AccessToken"] = r.AccessToken; d["UserName"] = r.UserName; d["UserId"] = r.UserId;
                    return d;
                }
                case "stream_error":
                {
                    var r = ApiReaders.ReadStreamError(payload);
                    d["Error"] = r.Error; d["Code"] = r.Code; d["Revoked"] = r.Revoked;
                    return d;
                }
                case "translation_updated":
                {
                    var r = ApiReaders.ReadTranslationUpdated(payload);
                    d["FileHash"] = r.FileHash; d["LineCount"] = r.LineCount; d["VoteCount"] = r.VoteCount;
                    return d;
                }
                case "merge_completed":
                {
                    var r = ApiReaders.ReadMergeCompleted(payload);
                    d["TranslationId"] = r.TranslationId; d["FileHash"] = r.FileHash; d["LineCount"] = r.LineCount; d["ToLocal"] = r.ToLocal;
                    return d;
                }
                case "edit_saved":
                {
                    var r = ApiReaders.ReadEditSaved(payload);
                    d["ContentHash"] = r.ContentHash; d["LineCount"] = r.LineCount; d["SavedAt"] = r.SavedAt;
                    return d;
                }
                case "edit_retranslate":
                {
                    var r = ApiReaders.ReadEditRetranslate(payload);
                    d["Key"] = r.Key; d["RequestId"] = r.RequestId;
                    return d;
                }
                case "sync_state":
                {
                    string account = (string)input?["account"];
                    ServerTranslationState previous = previousPayload == null
                        ? null
                        : ApiReaders.ReadSyncState(previousPayload, null, account);
                    var r = ApiReaders.ReadSyncState(payload, previous, account);
                    d["Checked"] = r.Checked; d["AskedAsAccount"] = r.AskedAsAccount;
                    d["Exists"] = r.Exists; d["IsOwner"] = r.IsOwner; d["Role"] = r.Role.ToString();
                    d["SiteId"] = r.SiteId; d["Uploader"] = r.Uploader; d["Hash"] = r.Hash; d["Type"] = r.Type;
                    d["Status"] = r.Status; d["Notes"] = r.Notes; d["ResourcesUrl"] = r.ResourcesUrl;
                    d["Origin"] = r.Origin == null ? null : "present"; d["Origin.Author"] = r.Origin?.Author; d["Origin.Lines"] = r.Origin?.Lines;
                    d["AcceptsBranches"] = r.AcceptsBranches; d["BranchFrozen"] = r.BranchFrozen;
                    d["MainUsername"] = r.MainUsername; d["MainMissing"] = r.MainMissing;
                    d["MainAbandoned"] = r.MainAbandoned; d["MainIgnoring"] = r.MainIgnoring;
                    d["MergedLinesTotal"] = r.MergedLinesTotal; d["BranchesCount"] = r.BranchesCount;
                    d["BranchesWithWork"] = r.BranchesWithWork; d["LinesAvailable"] = r.LinesAvailable;
                    d["LinesToReview"] = r.LinesToReview; d["LinesNew.Human"] = r.LinesNew.Human;
                    d["LinesDiffering.Human"] = r.LinesDiffering.Human; d["LinesOffered"] = r.LinesOffered;
                    d["Vote"] = r.Vote == null ? null : "present"; d["Vote.CanVote"] = r.Vote?.CanVote;
                    d["BranchesPendingReview"] = r.BranchesPendingReview;
                    d["MainSiteId"] = r.MainSiteId; d["MainHash"] = r.MainHash; d["MainLineCount"] = r.MainLineCount;
                    d["SourceLanguage"] = r.SourceLanguage; d["TargetLanguage"] = r.TargetLanguage;
                    return d;
                }
                default:
                    return null;
            }
        }

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
            switch (expected.Type)
            {
                case JTokenType.Null: return actual == null ? null : $"expected null, got {Show(actual)}";
                case JTokenType.Boolean: return Equals(actual, (bool)expected) ? null : $"expected {expected}, got {Show(actual)}";
                case JTokenType.Integer: return actual != null && Convert.ToInt64(actual) == (long)expected ? null : $"expected {expected}, got {Show(actual)}";
                default: return string.Equals(actual as string, (string)expected, StringComparison.Ordinal) ? null : $"expected \"{expected}\", got {Show(actual)}";
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
