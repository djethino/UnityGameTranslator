using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityGameTranslator.Common;
using UnityGameTranslator.Net.Http;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The mod's own HTTP client (Net/Http, issue #31): the Core names nothing of the runtime's
    /// System.Net.Http, and what our port puts on the wire — then makes of the answer — is what the
    /// mod relies on.
    ///
    /// 🔴 **Why the wire, and not the objects.** Upstream wrote headers through an internal
    /// WebHeaderCollection.AddInternal; ours goes through the public API, where HttpWebRequest
    /// refuses User-Agent, Accept or Content-Type in its Headers and wants its properties instead.
    /// A mapping that missed one throws on EVERY request, or silently drops the header — only the
    /// bytes a server receives tell. So a loopback server records them.
    ///
    /// ⚠ **What this does not prove.** It runs on CoreCLR's HttpWebRequest. Mono's — the one the
    /// mod meets in a game — is another implementation of the same public contract; this checks our
    /// side of it. A game launch is what checks Mono's.
    /// </summary>
    internal static class OwnHttpClientChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            TheCoreNamesNoRuntimeClient(check);

            // Before any transport is set: a request has nowhere to go, and says so.
            var early = Catch(() => new HttpClient().GetAsync("http://127.0.0.1:9/").GetAwaiter().GetResult());
            check(early is InvalidOperationException,
                "a request before the adapter named a transport is refused by name",
                "a startup-order defect is said, never carried by a transport that may be the wrong one");

            HttpTransport.Current = new WebRequestTransport();

            HeadersReachTheServer(check);
            ReservedHeadersHaveTheirProperty(check);
            AnErrorStatusIsAnAnswer(check);
            ABodyLeavesWhole(check);
            WhatComesBackIsRead(check);
            ARedirectIsNotFollowedWhenRefused(check);
            NoAnswerEndsCancelled(check);
            ARefusedConnectionIsNamed(check);
        }

        // ── The Core ───────────────────────────────────────────────────────────────────────────

        private static void TheCoreNamesNoRuntimeClient(Action<bool, string, string> check)
        {
            var pattern = new Regex(@"\bSystem\.Net\.Http\b");
            check(pattern.IsMatch("using System.Net.Http;") && pattern.IsMatch("new System.Net.Http.Headers.MediaTypeHeaderValue(x)")
                  && !pattern.IsMatch("using UnityGameTranslator.Net.Http;") && !pattern.IsMatch("System.Net.HttpStatusCode.OK"),
                "the rule tells the runtime's client from ours", "UnityGameTranslator.Net.Http and System.Net.HttpStatusCode are not it");

            string core = FindCoreFolder();
            check(core != null, "the Core's sources are found", "the check reads files; without them it proves nothing");
            if (core == null) return;

            int judged = 0;
            foreach (var file in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(core.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)) continue;
                judged++;

                if (pattern.IsMatch(StripComments(File.ReadAllText(file))))
                    check(false, $"{relative} does not name System.Net.Http",
                        "the runtime's client is the game's own library under Mono — the one issue #31 found broken; use UnityGameTranslator.Net.Http");
            }
            check(judged > 50, $"{judged} Core files judged, none names System.Net.Http",
                "every request goes through the mod's own client, whatever the game ships");
        }

        // ── On the wire ────────────────────────────────────────────────────────────────────────

        private static void HeadersReachTheServer(Action<bool, string, string> check)
        {
            using var server = new RawServer(_ => Reply(200, "OK", "{}", "Content-Type: application/json"));
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "UnityGameTranslator/0.0.0 (BepInEx5)");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("X-UGT-Device", "abc");
            client.DefaultRequestHeaders.Authorization = new UnityGameTranslator.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "ugt_token");

            var request = new HttpRequestMessage(HttpMethod.Get, server.Url("/check"));
            request.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
            var response = client.SendAsync(request).GetAwaiter().GetResult();
            string sent = server.Requests.SingleOrDefault() ?? "";

            check(response.IsSuccessStatusCode, "a plain GET is answered", $"status {(int)response.StatusCode}");
            check(Has(sent, "User-Agent", "UnityGameTranslator/0.0.0 (BepInEx5)"),
                "User-Agent reaches the server", "HttpWebRequest refuses it in Headers: it must go through UserAgent");
            check(Has(sent, "Accept", "application/json"), "Accept reaches the server", "same: through HttpWebRequest.Accept");
            check(Has(sent, "X-UGT-Device", "abc"), "a header of ours reaches the server", "anything not reserved goes through Headers.Add");
            check(Has(sent, "Authorization", "Bearer ugt_token"), "the token reaches the server", "every signed-in call depends on it");
            check(Has(sent, "If-None-Match", "\"v1\""), "If-None-Match reaches the server", "the update check answers 304 on it");
        }

        /// <summary>
        /// ⚠ The wire alone cannot catch a missed reserved header here: .NET Core's HttpWebRequest
        /// takes User-Agent in its Headers and sends it, where Mono's throws. Proved on 2026-10-05 —
        /// User-Agent sent through Headers.Add passed every wire check above. So the transport
        /// refuses a reserved header it has no property for, on every runtime, and this holds its
        /// list against the runtime's and sends each one.
        /// </summary>
        private static void ReservedHeadersHaveTheirProperty(Action<bool, string, string> check)
        {
            string[] candidates =
            {
                "Accept", "Accept-Charset", "Accept-Encoding", "Accept-Language", "Authorization", "Cache-Control",
                "Connection", "Content-Encoding", "Content-Length", "Content-Type", "Cookie", "Date", "ETag", "Expect",
                "From", "Host", "If-Match", "If-Modified-Since", "If-None-Match", "If-Range", "If-Unmodified-Since",
                "Keep-Alive", "Last-Event-ID", "Max-Forwards", "Pragma", "Proxy-Authorization", "Proxy-Connection",
                "Range", "Referer", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "User-Agent", "Via", "Warning",
                "X-Goog-Api-Key", "X-UGT-Device",
            };
            var differ = candidates.Where(n => WebHeaderCollection.IsRestricted(n) != WebRequestTransport.ReservedHeaders.Contains(n)).ToList();
            check(differ.Count == 0, "the transport's reserved headers are the runtime's",
                differ.Count == 0 ? "WebHeaderCollection.IsRestricted, name by name" : "differ: " + string.Join(", ", differ));

            using var server = new RawServer(_ => Reply(200, "OK", "{}"));
            var sent = new (string name, string value)[]
            {
                ("Referer", "http://example.invalid/from"),
                ("Date", "Mon, 05 Oct 2026 10:00:00 GMT"),
                ("If-Modified-Since", "Mon, 05 Oct 2026 09:00:00 GMT"),
                ("Range", "bytes=10-20"),
                ("Expect", "x-ugt"),
            };
            foreach (var (name, value) in sent)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, server.Url("/reserved"));
                request.Headers.TryAddWithoutValidation(name, value);
                var error = Catch(() => new HttpClient().SendAsync(request).GetAwaiter().GetResult());
                string arrived = server.Requests.LastOrDefault() ?? "";
                check(error == null && Regex.IsMatch(arrived, "^" + Regex.Escape(name) + ":", RegexOptions.Multiline | RegexOptions.IgnoreCase),
                    $"{name} goes through its property and arrives", error?.Message ?? "a reserved header the mod does not send today, kept working");
            }

            var proxy = new HttpRequestMessage(HttpMethod.Get, server.Url("/reserved"));
            proxy.Headers.TryAddWithoutValidation("Proxy-Connection", "keep-alive");
            var refused = Catch(() => new HttpClient().SendAsync(proxy).GetAwaiter().GetResult());
            check(refused is NotSupportedException, "a reserved header with no property is refused by name",
                refused?.GetType().Name ?? "sent — on Mono that is an exception on every request, outside a game nothing shows it");
        }

        private static void AnErrorStatusIsAnAnswer(Action<bool, string, string> check)
        {
            using var server = new RawServer(request => request.StartsWith("GET /missing", StringComparison.Ordinal)
                ? Reply(404, "Not Found", "{\"error\":\"gone\"}", "Content-Type: application/json")
                : request.StartsWith("GET /same", StringComparison.Ordinal)
                    ? Reply(304, "Not Modified", null)
                    : Reply(401, "Unauthorized", "{\"error\":\"revoked\"}", "Content-Type: application/json"));
            var client = new HttpClient();

            var missing = Catch(() => client.GetAsync(server.Url("/missing")).GetAwaiter().GetResult(), out HttpResponseMessage notFound);
            check(missing == null && notFound?.StatusCode == HttpStatusCode.NotFound,
                "a 404 is a response, not an exception", missing?.GetType().Name ?? "upstream's ThrowOnError = false, kept through the WebException");
            check(notFound != null && notFound.Content.ReadAsStringAsync().GetAwaiter().GetResult() == "{\"error\":\"gone\"}",
                "the body of an error is readable", "the mod reads the server's reason in it");
            check(notFound?.ReasonPhrase == "Not Found", "the server's reason phrase is kept", notFound?.ReasonPhrase ?? "(none)");

            var same = client.GetAsync(server.Url("/same")).GetAwaiter().GetResult();
            check(same.StatusCode == HttpStatusCode.NotModified, "a 304 is a response", "the translation download relies on it");

            var refused = client.GetAsync(server.Url("/me")).GetAwaiter().GetResult();
            check(refused.StatusCode == HttpStatusCode.Unauthorized, "a 401 is a response", "what signs a revoked token out");
        }

        private static void ABodyLeavesWhole(Action<bool, string, string> check)
        {
            using var server = new RawServer(_ => Reply(201, "Created", "{}"));
            var client = new HttpClient();

            string json = "{\"title\":\"Élan — 日本語\"}";
            client.PostAsync(server.Url("/translations"), new StringContent(json, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            string sent = server.Requests.LastOrDefault() ?? "";
            int bytes = Encoding.UTF8.GetByteCount(json);

            check(Has(sent, "Content-Type", "application/json; charset=utf-8"),
                "Content-Type reaches the server", "HttpWebRequest refuses it in Headers: it must go through ContentType");
            check(Has(sent, "Content-Length", bytes.ToString()), "Content-Length is the body's size in bytes", $"{bytes} expected");
            check(sent.EndsWith("\r\n\r\n" + json, StringComparison.Ordinal), "the body arrives as it was written", "UTF-8, nothing added");

            var gzip = new ByteArrayContent(new byte[] { 0x1F, 0x8B, 1, 2, 3 });
            gzip.Headers.Add("Content-Encoding", "gzip");
            gzip.Headers.ContentType = new UnityGameTranslator.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            client.PostAsync(server.Url("/translations"), gzip).GetAwaiter().GetResult();
            sent = server.Requests.LastOrDefault() ?? "";
            check(Has(sent, "Content-Encoding", "gzip") && Has(sent, "Content-Length", "5"),
                "a compressed upload says it is compressed", "the site inflates on Content-Encoding");

            client.PostAsync(server.Url("/read"), null).GetAwaiter().GetResult();
            check(Has(server.Requests.LastOrDefault() ?? "", "Content-Length", "0"),
                "a POST without a body says so", "some servers refuse a POST with no length (411)");
        }

        private static void WhatComesBackIsRead(Action<bool, string, string> check)
        {
            byte[] packed;
            using (var buffer = new MemoryStream())
            {
                using (var zip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
                    zip.Write(Encoding.UTF8.GetBytes("{\"ok\":true}"));
                packed = buffer.ToArray();
            }

            using var server = new RawServer(_ => Concat(
                Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nETag: \"abc\"\r\nRetry-After: 7\r\nContent-Length: {packed.Length}\r\nConnection: close\r\n\r\n"),
                packed));
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var client = new HttpClient(handler);

            var response = client.GetAsync(server.Url("/download")).GetAwaiter().GetResult();
            check(response.Content.ReadAsStringAsync().GetAwaiter().GetResult() == "{\"ok\":true}",
                "a gzip answer is inflated", "AutomaticDecompression, which the site's client relies on");
            check(response.Headers.ETag?.Tag == "\"abc\"", "the ETag is read typed", "the download's hash comes from it");
            check(response.Headers.TryGetValues("Retry-After", out var retry) && retry.FirstOrDefault() == "7",
                "Retry-After is read by name", "its value type is not ported; the text is enough for the mod");
            check(response.Content.Headers.ContentType?.MediaType == "application/json",
                "a content header is filed with the content", "Content-Type belongs to the body's headers");

            var streamed = client.GetAsync(server.Url("/stream"), HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            using (var reader = new StreamReader(streamed.Content.ReadAsStreamAsync().GetAwaiter().GetResult()))
                check(reader.ReadToEnd() == "{\"ok\":true}", "a body read as a stream arrives whole", "the event streams are read this way");
        }

        private static void ARedirectIsNotFollowedWhenRefused(Action<bool, string, string> check)
        {
            using var server = new RawServer(_ => Reply(302, "Found", null, "Location: http://example.invalid/steal"));
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

            var response = client.GetAsync(server.Url("/me")).GetAwaiter().GetResult();
            check(response.StatusCode == HttpStatusCode.Found && server.Requests.Count == 1,
                "a redirect is not followed when the handler refuses them",
                "the site's client carries the token: a redirect must never take it elsewhere");
        }

        private static void NoAnswerEndsCancelled(Action<bool, string, string> check)
        {
            using var server = new RawServer(_ => null, holdOpen: true);
            var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };

            var error = Catch(() => client.GetAsync(server.Url("/slow")).GetAwaiter().GetResult());
            check(error is TaskCanceledException, "no answer in time ends as a cancelled task",
                error?.GetType().Name ?? "no exception — TranslatorCore.SendForTranslation reads exactly this as a silent server");
        }

        private static void ARefusedConnectionIsNamed(Action<bool, string, string> check)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var error = Catch(() => new HttpClient().GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult());
            check(error is HttpRequestException, "a refused connection is the mod's HttpRequestException",
                error?.GetType().FullName ?? "no exception — the mod catches its own type");
            check(Connectivity.Classify(error) == ConnectionProblem.Refused, "the cause inside it is still named",
                Connectivity.Cause(error) + " — Connectivity reads the socket error under the wrapper");
        }

        // ── Tools ──────────────────────────────────────────────────────────────────────────────

        private static bool Has(string request, string name, string value) =>
            Regex.IsMatch(request, "^" + Regex.Escape(name) + @":\s*" + Regex.Escape(value) + "\r$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private static byte[] Reply(int status, string reason, string body, params string[] headers)
        {
            byte[] content = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\n");
            foreach (var h in headers) head.Append(h).Append("\r\n");
            head.Append($"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n");
            return Concat(Encoding.ASCII.GetBytes(head.ToString()), content);
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var all = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, all, 0, a.Length);
            Buffer.BlockCopy(b, 0, all, a.Length, b.Length);
            return all;
        }

        private static Exception Catch(Action act)
        {
            try { act(); return null; }
            catch (AggregateException e) { return e.GetBaseException(); }
            catch (Exception e) { return e; }
        }

        private static Exception Catch<T>(Func<T> act, out T result)
        {
            result = default;
            try { result = act(); return null; }
            catch (AggregateException e) { return e.GetBaseException(); }
            catch (Exception e) { return e; }
        }

        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", "");
        }

        private static string FindCoreFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "UnityGameTranslator.Core");
                if (Directory.Exists(Path.Combine(candidate, "Net", "Http"))) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>
        /// A loopback HTTP/1.1 server that records every request as it arrived — head and body —
        /// and answers with the bytes it is given. Null holds the connection open without a word.
        /// </summary>
        private sealed class RawServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Func<string, byte[]> _respond;
            private readonly bool _holdOpen;
            private readonly List<TcpClient> _held = new List<TcpClient>();
            private readonly object _lock = new object();
            private readonly List<string> _requests = new List<string>();

            public RawServer(Func<string, byte[]> respond, bool holdOpen = false)
            {
                _respond = respond;
                _holdOpen = holdOpen;
                _listener.Start();
                Task.Run(Serve);
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

            public List<string> Requests
            {
                get { lock (_lock) return new List<string>(_requests); }
            }

            private async Task Serve()
            {
                while (true)
                {
                    TcpClient connection;
                    try { connection = await _listener.AcceptTcpClientAsync(); }
                    catch (Exception) { return; } // stopped: Dispose
                    _ = Task.Run(() => Answer(connection));
                }
            }

            private void Answer(TcpClient connection)
            {
                var stream = connection.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (stream.Read(one, 0, 1) == 0) { connection.Close(); return; }
                    head.Append((char)one[0]);
                }

                var match = Regex.Match(head.ToString(), @"^Content-Length:\s*(\d+)\r$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                var body = new byte[match.Success ? int.Parse(match.Groups[1].Value) : 0];
                for (int read = 0; read < body.Length;)
                {
                    int n = stream.Read(body, read, body.Length - read);
                    if (n == 0) break;
                    read += n;
                }

                string request = head + Encoding.UTF8.GetString(body);
                lock (_lock) _requests.Add(request);

                byte[] reply = _respond(request);
                if (reply == null)
                {
                    if (_holdOpen) lock (_lock) _held.Add(connection);
                    else connection.Close();
                    return;
                }

                stream.Write(reply, 0, reply.Length);
                connection.Close();
            }

            public void Dispose()
            {
                _listener.Stop();
                lock (_lock)
                    foreach (var held in _held) held.Close();
            }
        }
    }
}
