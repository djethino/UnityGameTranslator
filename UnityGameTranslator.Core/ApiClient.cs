using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// HTTP client for communicating with the UnityGameTranslator website API.
    /// All methods are async and handle errors gracefully.
    /// </summary>
    public static class ApiClient
    {
        private static readonly HttpClient client;
        private static readonly HttpClient sseClient;
        private static bool _urlOverrideLogged = false;

        // URLs can be overridden in config.json (api_base_url, website_base_url)
        // Default values come from Directory.Build.props via PluginInfo.g.cs
        private static string DefaultBaseUrl
        {
            get
            {
                var config = TranslatorCore.Config;
                if (config != null && !string.IsNullOrEmpty(config.api_base_url))
                {
                    LogUrlOverrideOnce();
                    return config.api_base_url.TrimEnd('/');
                }
                return PluginInfo.ApiBaseUrl;
            }
        }

        public static string WebsiteBaseUrl
        {
            get
            {
                var config = TranslatorCore.Config;
                if (config != null && !string.IsNullOrEmpty(config.website_base_url))
                {
                    LogUrlOverrideOnce();
                    return config.website_base_url.TrimEnd('/');
                }
                return PluginInfo.WebsiteBaseUrl;
            }
        }

        /// <summary>
        /// Base URL for SSE streams (Node.js micro-server).
        /// Can be overridden in config.json with sse_base_url for self-hosting.
        /// </summary>
        public static string SseBaseUrl
        {
            get
            {
                var config = TranslatorCore.Config;
                if (config != null && !string.IsNullOrEmpty(config.sse_base_url))
                {
                    LogUrlOverrideOnce();
                    return config.sse_base_url.TrimEnd('/');
                }
                return PluginInfo.SseBaseUrl;
            }
        }

        private static void LogUrlOverrideOnce()
        {
            if (!_urlOverrideLogged)
            {
                _urlOverrideLogged = true;
                TranslatorCore.LogWarning("[ApiClient] Using custom API URLs from config.json - tokens will be sent to this server!");
            }
        }

        /// <summary>
        /// Get the merge review page URL for a translation UUID
        /// </summary>
        public static string GetMergeReviewUrl(string uuid)
        {
            return $"{WebsiteBaseUrl}/translations/{uuid}/merge";
        }

        /// <summary>
        /// Get the translation detail page URL
        /// </summary>
        public static string GetTranslationUrl(int translationId)
        {
            return $"{WebsiteBaseUrl}/translations/{translationId}";
        }

        /// <summary>
        /// The author's own translations, anchored on one of them.
        ///
        /// Where a file can be taken back off the server — the page carries the delete button and
        /// its confirmation. Uploading is one click and unpublishing is a page nobody thinks to
        /// look for, which is how "it's uploaded, not my problem any more" happens; the anchor
        /// lands on the right row instead of a list of twenty.
        /// </summary>
        public static string GetMyTranslationsUrl(int? translationId = null)
        {
            string url = $"{WebsiteBaseUrl}/my-translations";

            return translationId.HasValue ? $"{url}#translation-{translationId.Value}" : url;
        }

        /// <summary>
        /// Raised when the server refuses our token: it was revoked from the website, or the
        /// account was banned (banning deletes the account's API tokens). Carries the reason the
        /// server gave, when it gave one.
        /// </summary>
        public static event Action<string> OnAuthenticationRejected;

        /// <summary>
        /// Watches every response for "this token is no longer accepted" so the mod signs itself
        /// out instead of showing a signed-in account whose every action silently fails. A single
        /// handler covers all call sites, including ones added later.
        /// </summary>
        private class AuthRejectionHandler : DelegatingHandler
        {
            /// <summary>
            /// Whether the client this handler sits on carries a token by default, or null when it
            /// never does.
            /// </summary>
            /// <remarks>
            /// 🔴 Per client, not global. It used to read <c>client.DefaultRequestHeaders</c>
            /// outright, which was harmless while only that one client was watched — and became a
            /// trap the moment the streaming client was too: every stream would have counted as
            /// authenticated merely because the player was signed in somewhere else, and a 401 from
            /// the sign-in, merge-preview or editing streams — none of which carry the account's
            /// token — would have signed them out.
            /// </remarks>
            private readonly Func<bool> _carriesTokenByDefault;

            public AuthRejectionHandler(HttpMessageHandler inner, Func<bool> carriesTokenByDefault = null)
                : base(inner)
            {
                _carriesTokenByDefault = carriesTokenByDefault;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                // Read BEFORE sending: evaluating this after the round trip made it
                // race with SetAuthToken. A request that left before the saved token
                // was restored came back 401, by then the header existed, and the
                // handler concluded the token had been revoked — signing the player
                // out on startup because of a call that never carried a token.
                bool sentToken = request.Headers.Authorization != null
                    || request.Headers.Contains("Authorization")
                    || (_carriesTokenByDefault != null && _carriesTokenByDefault());

                var response = await base.SendAsync(request, cancellationToken);
                bool unauthorized = response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                bool forbidden = response.StatusCode == System.Net.HttpStatusCode.Forbidden;

                if (sentToken && (unauthorized || forbidden))
                {
                    string error = null, reason = null;
                    try
                    {
                        var body = ParseJsonSafe(await response.Content.ReadAsStringAsync());
                        error = body?["error"]?.Value<string>();
                        reason = body?["reason"]?.Value<string>() ?? body?["message"]?.Value<string>();
                    }
                    catch { /* body not JSON: fall back to the status code alone */ }

                    // 401 always means the credential itself was refused (revoked token, or one
                    // deleted along with a ban). 403 does NOT: it also covers ordinary refusals
                    // such as voting on a non-public translation, which must not sign anyone out
                    // — so only the server's explicit ban marker counts here.
                    bool credentialRefused = unauthorized
                        || string.Equals(error, "Account banned", StringComparison.OrdinalIgnoreCase);

                    if (credentialRefused)
                    {
                        try { OnAuthenticationRejected?.Invoke(reason); }
                        catch (Exception e) { TranslatorCore.LogWarning($"[ApiClient] Auth rejection handler failed: {e.Message}"); }
                    }
                }

                return response;
            }
        }

        static ApiClient()
        {
            // Disable automatic redirects to prevent token leakage via malicious redirects
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,

                // 🔴 **Asking for gzip and decompressing it are two different things, and only the
                // first was being done.** The header was set by hand while the handler was left on
                // its default (no decompression), so a server that took the offer sent 0x1F 0x8B
                // and every single call died in the JSON parser with "Unexpected character
                // encountered while parsing value" — the character being unprintable, the message
                // named nothing and read as a corrupt server.
                //
                // ⚠ It had never fired because the production site does not compress these
                // responses. Nothing in the mod protected it: the day that server turns
                // compression on — a config line, a CDN put in front — every installed copy would
                // have lost the site at once, with no release able to reach them any more.
                // Found on 2026-08-20 against a local site whose stack compresses by default.
                //
                // ⚠ AutomaticDecompression sends `Accept-Encoding` itself. Adding it by hand as
                // well is what created the gap, so it must NOT be set below.
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            // SetAuthToken puts the account's token in this client's default headers, so every call
            // it makes is authenticated whether or not the request says so itself.
            client = new HttpClient(new AuthRejectionHandler(
                handler, () => client.DefaultRequestHeaders.Contains("Authorization")));
            client.Timeout = TimeSpan.FromSeconds(30);

            // ⚠ Every other call in this file reads its body straight into a string. Now that the
            // handler inflates, a hostile server could answer a few kilobytes that become
            // gigabytes, so the buffer carries the same ceiling the download reads under.
            client.MaxResponseContentBufferSize = MaxTranslationJsonBytes;

            client.DefaultRequestHeaders.Add("User-Agent", UserAgent());
            client.DefaultRequestHeaders.Add("Accept", "application/json");

            // Dedicated SSE client: no timeout (long-lived streams), no gzip (breaks streaming)
            var sseHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            // 🔴 Wrapped like the other one, and it was not.
            //
            // The sync stream carries `Authorization: Bearer …` (TranslatorUIManager opens it that
            // way), so it is an authenticated call like any other — it simply happened to be made
            // through the one client nothing was watching. An access revoked from the account was
            // therefore refused here in silence, and the mod went on showing a signed-in account
            // until something else spoke to the site.
            //
            // ⚠ This catches the refusal when a stream is OPENED or reopened, which is what an
            // HTTP handler can see. A stream already open is not re-authenticated by anybody: to
            // cut one live the relay would have to be told, and that lives in the SSE server.
            //
            // ⚠ No default-token check passed: this client never holds one. Only the streams that
            // put the header on the request themselves count — the sync stream does, the sign-in,
            // merge-preview and editing streams do not, and a refusal from those says nothing
            // about the account.
            sseClient = new HttpClient(new AuthRejectionHandler(sseHandler));
            sseClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            sseClient.DefaultRequestHeaders.Add("User-Agent", UserAgent());
        }

        /// <summary>
        /// How this mod names itself to a server.
        /// </summary>
        /// <remarks>
        /// 🔴 **It used to be the literal "UnityGameTranslator/1.0", on every build ever shipped.**
        /// So a server could see that a mod was talking to it and nothing else: not which version,
        /// not which loader. That matters for two decisions nobody could otherwise make —
        /// whether an old release is still out there in numbers, and whether a loader adapter is
        /// still worth maintaining. The Manager has always sent its real version
        /// (<c>UnityGameTranslatorManager/{version}</c>); the mod simply never did.
        ///
        /// ⚠ **It is also what a server needs to protect the versions that cannot read gzip.**
        /// Excluding a broken client from compression means naming it, and every version named
        /// itself the same thing — so the exclusion could only ever be "all of them, forever".
        ///
        /// ⚠ Deliberately coarse: a version and a loader, both of which take a handful of values
        /// across the whole population. Nothing here distinguishes one installation from another.
        /// </remarks>
        private static string UserAgent()
        {
            string loader = TranslatorCore.Adapter?.ModLoaderType;

            return string.IsNullOrEmpty(loader)
                ? $"UnityGameTranslator/{PluginInfo.Version}"
                : $"UnityGameTranslator/{PluginInfo.Version} ({loader})";
        }

        /// <summary>
        /// Re-read the User-Agent once the adapter is known.
        /// </summary>
        /// <remarks>
        /// ⚠ The static constructor runs on first use, which may be before or after the adapter is
        /// set — an order nothing here controls. So the loader is filled in from
        /// <see cref="TranslatorCore.Initialize"/>, and until then the version alone is sent
        /// rather than an empty pair of brackets.
        /// </remarks>
        internal static void RefreshUserAgent()
        {
            string agent = UserAgent();

            foreach (var http in new[] { client, sseClient })
            {
                http.DefaultRequestHeaders.Remove("User-Agent");
                http.DefaultRequestHeaders.Add("User-Agent", agent);
            }
        }

        /// <summary>
        /// Maximum accepted size for a translation JSON payload (decompressed).
        /// 100MB is more than enough for any translation file.
        /// </summary>
        private const int MaxTranslationJsonBytes = 100 * 1024 * 1024;

        /// <summary>
        /// Parse a JSON object from a network response with an enforced depth limit.
        /// Newtonsoft's default MaxDepth (64) still lets a hostile or buggy server
        /// nest deep enough to exhaust the stack; 10 covers every legitimate API
        /// response with margin. Throws JsonReaderException on invalid or too-deep
        /// JSON — every caller already wraps parsing in try/catch.
        /// Public: also used for every other network-originated JSON in the mod
        /// (AI responses, GitHub API, SSE messages).
        /// </summary>
        public static JObject ParseJsonSafe(string json)
        {
            using (var stringReader = new StringReader(json))
            using (var jsonReader = new JsonTextReader(stringReader) { MaxDepth = 10 })
            {
                var parsed = JObject.Load(jsonReader);
                // Mirror JObject.Parse: reject trailing content after the object
                if (jsonReader.Read())
                {
                    throw new JsonReaderException("Additional text found after JSON content.");
                }
                return parsed;
            }
        }

        /// <summary>
        /// Human-readable reason for a failed response, meant to be shown as-is.
        /// Rate limits get their own sentence: the server answers "Too Many
        /// Requests", which reads as a breakage when in fact the only thing to
        /// do is wait — and the wait is short and known.
        /// Never throws: an error body can be anything, including an HTML page
        /// from a proxy the application never saw.
        /// </summary>
        public static string DescribeHttpError(HttpResponseMessage response, string body)
        {
            if ((int)response.StatusCode == 429)
            {
                int seconds = 60;
                if (response.Headers.TryGetValues("Retry-After", out var values))
                {
                    foreach (var value in values)
                    {
                        if (int.TryParse(value, out int parsed) && parsed > 0)
                        {
                            seconds = parsed;
                            break;
                        }
                    }
                }
                return $"too many attempts in a row, wait {seconds}s and try again";
            }

            try
            {
                var parsed = ParseJsonSafe(body);
                string message = parsed["error"]?.Value<string>()
                    ?? parsed["message"]?.Value<string>();
                if (!string.IsNullOrEmpty(message)) return message;
            }
            catch
            {
                // Not JSON (proxy error page): the status code is all we can say
            }

            return $"HTTP {(int)response.StatusCode} {response.StatusCode}";
        }

        /// <summary>
        /// Get the HttpClient configured for SSE streaming (infinite timeout, no gzip).
        /// </summary>
        public static HttpClient GetSseHttpClient() => sseClient;

        /// <summary>
        /// Build the SSE URL for Device Flow authentication stream.
        /// Points to the Node.js SSE micro-server.
        /// </summary>
        public static string GetDeviceFlowSseUrl(string deviceCode)
        {
            return $"{SseBaseUrl}/auth/device/{Uri.EscapeDataString(deviceCode)}/stream";
        }

        /// <summary>
        /// Build the SSE URL for translation sync stream.
        /// Points to the Node.js SSE micro-server.
        ///
        /// ⚠ **The stream carries one's OWN line and nothing else** (<c>lineage=0</c>). What other
        /// people do — a contribution arriving, a Main moving on — rides the periodic check
        /// instead: weighing contributions reads their files, and a lineage where somebody
        /// publishes every ten minutes would do that on every push, for every contributor
        /// connected. An older SSE server ignores the parameter and behaves as before.
        /// </summary>
        public static string GetSyncSseUrl(string uuid, string localHash)
        {
            var url = $"{SseBaseUrl}/sync/stream?uuid={Uri.EscapeDataString(uuid)}&lineage=0";
            if (!string.IsNullOrEmpty(localHash))
            {
                url += $"&hash={Uri.EscapeDataString(localHash)}";
            }
            return url;
        }

        /// <summary>
        /// One-shot sync state, straight from the website.
        ///
        /// Returns the RAW payload on purpose: it is byte-for-byte what the SSE
        /// 'state' event carries, so the caller feeds it to the very same handler
        /// and the two paths can never drift apart. This is what lets a player
        /// who only wants to hear about updates now and then stop holding a
        /// stream open for the whole session.
        ///
        /// Null on any failure — a missed check is not an error worth surfacing,
        /// the next one comes on its own.
        /// </summary>
        public static async Task<string> FetchSyncState(string uuid, string localHash)
        {
            if (string.IsNullOrEmpty(uuid)) return null;

            try
            {
                var url = $"{DefaultBaseUrl}/sync/state?uuid={Uri.EscapeDataString(uuid)}";
                if (!string.IsNullOrEmpty(localHash))
                {
                    url += $"&hash={Uri.EscapeDataString(localHash)}";
                }

                var response = await client.GetAsync(url);
                string json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    TranslatorCore.LogWarning($"[ApiClient] Sync state check failed: {DescribeHttpError(response, json)}");
                    return null;
                }

                return json;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Sync state check error: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Public update check for a translation, by site id. NO ACCOUNT NEEDED.
        ///
        /// The one path someone without an account has to hear that the
        /// translation they installed has moved: searching and downloading are
        /// public, but every update signal went through the authenticated sync
        /// state, leaving them with a file frozen at install time.
        ///
        /// Returns the server hash, or null when the answer is unusable. The
        /// endpoint refuses branches to anyone but their Main, so nothing
        /// private can be reached this way.
        /// </summary>
        public static async Task<TranslationCheckResult> CheckPublicUpdate(
            int siteId, string localHash, string knownETag = null)
        {
            try
            {
                var url = $"{DefaultBaseUrl}/translations/{siteId}/check";
                if (!string.IsNullOrEmpty(localHash))
                {
                    url += $"?hash={Uri.EscapeDataString(localHash)}";
                }

                var request = new HttpRequestMessage(HttpMethod.Get, url);

                // ⚠ The 304 this method has always handled could never happen: the endpoint keys
                // it on If-None-Match and nothing here ever sent one, so every timed check pulled
                // the whole answer back. Sent as received, never rebuilt from the hash — the
                // validator covers the vote count and the uploader too now.
                if (!string.IsNullOrEmpty(knownETag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", knownETag);
                }

                var response = await client.SendAsync(request);

                // Nothing changed — the server saved itself the body, so this result carries NO
                // values. Flagged rather than filled with zeroes: the caller must keep what it has.
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    return new TranslationCheckResult
                    {
                        Success = true,
                        NotModified = true,
                        HasUpdate = false,
                        ETag = knownETag,
                    };
                }

                string json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    TranslatorCore.LogWarning($"[ApiClient] Public update check failed: {DescribeHttpError(response, json)}");
                    return new TranslationCheckResult { Success = false, Error = DescribeHttpError(response, json) };
                }

                var data = ParseJsonSafe(json);
                string serverHash = data["file_hash"]?.Value<string>();

                return new TranslationCheckResult
                {
                    Success = true,
                    FileHash = serverHash,
                    LineCount = data["line_count"]?.Value<int>() ?? 0,
                    VoteCount = data["vote_count"]?.Value<int>() ?? 0,
                    // Absent on an older server: left null, which the caller reads as "unknown"
                    // and never as "published by nobody"
                    Uploader = data["uploader"]?.Value<string>(),
                    ETag = response.Headers.ETag?.ToString(),
                    // Trust our own comparison rather than the optional flag:
                    // the caller always knows the hash it is holding
                    HasUpdate = !string.IsNullOrEmpty(serverHash)
                                && !string.IsNullOrEmpty(localHash)
                                && serverHash != localHash,
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Public update check error: {e.Message}");
                return new TranslationCheckResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        /// <summary>
        /// Build the SSE URL for merge preview completion stream.
        /// Points to the Node.js SSE micro-server.
        /// </summary>
        public static string GetMergeStreamUrl(string token)
        {
            return $"{SseBaseUrl}/merge-preview/{Uri.EscapeDataString(token)}/stream";
        }

        /// <summary>
        /// Get the SSE stream URL for a live edit session (browser saves + end).
        /// </summary>
        public static string GetEditSessionStreamUrl(string modKey)
        {
            return $"{SseBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}/stream";
        }

        /// <summary>
        /// Set the API token for authenticated requests
        /// </summary>
        /// <summary>
        /// True once the token is actually on the client, which is NOT the same as
        /// having one in the config: the header is set during UI init, and anything
        /// firing before that would send an anonymous request to an authenticated
        /// endpoint — answered 401, and read as a revoked token.
        /// </summary>
        public static bool HasAuthToken => client.DefaultRequestHeaders.Contains("Authorization");

        public static void SetAuthToken(string token)
        {
            if (client.DefaultRequestHeaders.Contains("Authorization"))
            {
                client.DefaultRequestHeaders.Remove("Authorization");
            }

            if (!string.IsNullOrEmpty(token))
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            }

            DeclareGame();
        }

        /// <summary>The header carrying which game this mod speaks for.</summary>
        private const string GameHeader = "X-UGT-Game";

        /// <summary>
        /// The short code the site names THIS access by, or null until the site has said.
        ///
        /// 🔴 **Why it has to be shown here.** "Linked devices" names every line "#QKADJN" and offers
        /// to rename the machine it belongs to — while that code appeared in no program at all. So
        /// somebody was asked to name a machine nothing let them identify, and to cut accesses they
        /// could not tell apart. Reported from production on 2026-08-27.
        ///
        /// ⚠ Held in memory, never written to the config. It belongs to the token, the site is the
        /// one that knows it, and a copy on disk would be one more thing able to go stale — for a
        /// value worth one round trip.
        ///
        /// ⚠ Not a secret and not a credential: no endpoint accepts it, and whoever can read it
        /// already holds the token itself.
        /// </summary>
        public static string AccessCode { get; private set; }

        /// <summary>
        /// Ask the site which line this access is, so the mod can show it.
        ///
        /// Retroactive by construction: the code has been stored against this token since it was
        /// issued, so an access linked months ago becomes identifiable the moment this runs.
        /// </summary>
        public static async Task RefreshAccessCodeAsync()
        {
            if (!HasAuthToken)
            {
                AccessCode = null;
                return;
            }

            try
            {
                var response = await client.GetAsync($"{DefaultBaseUrl}/me");

                if (!response.IsSuccessStatusCode) return;

                var body = ParseJsonSafe(await response.Content.ReadAsStringAsync());
                AccessCode = body?["access_code"]?.Value<string>();
            }
            catch (Exception e)
            {
                // An unreachable site is an ordinary answer here — the label simply says nothing
                // rather than showing a code that may be wrong.
                TranslatorCore.LogInfo($"[ApiClient] Could not read this access's code: {e.Message}");
            }
        }

        /// <summary>
        /// Say which game this access belongs to, on every call rather than only when linking.
        ///
        /// 🔴 **The game used to be declared at the link and NOWHERE else.** So an access created
        /// before the mod declared anything stayed nameless for ever, while this same mod called the
        /// site several times an hour with the game right in front of it. Reported from production
        /// on 2026-08-27: every line of "Linked devices" read "Mod", with no way to tell one game
        /// from another. The link was the one moment we spoke up — which is the one moment we had
        /// nothing more to say than the site already knew.
        ///
        /// ⚠ **The same payload the device flow sends**, so one shape of declaration is decided in
        /// one place. Base64 because an HTTP header value is latin-1 by specification and a game is
        /// called 龙胤立志传 as readily as LoneStar — sent raw, the .NET client throws on the way out.
        ///
        /// ⚠ The site fills an EMPTY line and never corrects a filled one, so repeating this on
        /// every call costs nothing and cannot relabel an access. Nothing here is a proof either:
        /// a declaration only ever describes the caller's own line, under its own account.
        /// </summary>
        public static void DeclareGame()
        {
            if (client.DefaultRequestHeaders.Contains(GameHeader))
            {
                client.DefaultRequestHeaders.Remove(GameHeader);
            }

            var declaration = DeviceFlowPayload();

            if (declaration == null)
            {
                return;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(declaration.ToString(Newtonsoft.Json.Formatting.None));
                client.DefaultRequestHeaders.Add(GameHeader, Convert.ToBase64String(bytes));
            }
            catch (Exception e)
            {
                // A header we could not build is a line that stays unnamed — never a call that
                // fails. Logged rather than swallowed: silence here would hide the one reason the
                // screen keeps saying "Game not recorded" after an update meant to fix it.
                TranslatorCore.LogWarning($"[ApiClient] Could not declare the game: {e.Message}");
            }
        }

        #region Notifications

        /// <summary>
        /// Fetch the user's unread in-app notifications (compact summary for the
        /// status overlay). Requires the auth token to be set.
        /// </summary>
        public static async Task<ModNotificationsResult> GetNotificationsAsync()
        {
            try
            {
                var response = await client.GetAsync($"{DefaultBaseUrl}/me/notifications");
                if (!response.IsSuccessStatusCode)
                {
                    return new ModNotificationsResult { Success = false, Error = $"HTTP {response.StatusCode}" };
                }

                string json = await response.Content.ReadAsStringAsync();
                var data = ParseJsonSafe(json);

                var result = new ModNotificationsResult
                {
                    Success = true,
                    Unread = data["unread"]?.Value<int>() ?? 0,
                    Items = new List<ModNotificationItem>(),
                };

                if (data["items"] is JArray items)
                {
                    foreach (var item in items)
                    {
                        result.Items.Add(new ModNotificationItem
                        {
                            Id = item["id"]?.ToString(),
                            Type = item["type"]?.ToString(),
                            Text = item["text"]?.ToString(),
                            Url = item["url"]?.ToString(),
                        });
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Notifications error: {e.Message}");
                return new ModNotificationsResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        /// <summary>
        /// Mark notifications as read on the website (all of them when ids is null).
        /// </summary>
        public static async Task<bool> MarkNotificationsReadAsync(List<string> ids = null)
        {
            try
            {
                var payload = new JObject();
                if (ids != null && ids.Count > 0)
                {
                    payload["ids"] = new JArray(ids);
                }

                var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{DefaultBaseUrl}/me/notifications/read", content);
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Mark notifications read error: {e.Message}");
                return false;
            }
        }

        #endregion

        #region Translation Search

        /// <summary>
        /// Search for translations by Steam ID and language
        /// </summary>
        public static async Task<TranslationSearchResult> SearchBysteamId(string steamId, string targetLang)
        {
            try
            {
                string url = $"{DefaultBaseUrl}/translations?steam_id={Uri.EscapeDataString(steamId)}&lang={Uri.EscapeDataString(targetLang)}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    return new TranslationSearchResult { Success = false, Error = $"HTTP {response.StatusCode}" };
                }

                string json = await response.Content.ReadAsStringAsync();
                var data = ParseJsonSafe(json);

                var result = new TranslationSearchResult { Success = true };
                result.Count = data["count"]?.Value<int>() ?? 0;
                result.Translations = new List<TranslationInfo>();

                var translations = data["translations"] as JArray;
                if (translations != null)
                {
                    foreach (var t in translations)
                    {
                        result.Translations.Add(ParseTranslationInfo(t));
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Search error: {e.Message}");
                return new TranslationSearchResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        /// <summary>
        /// Search for translations by game name
        /// </summary>
        public static async Task<TranslationSearchResult> SearchByGameName(string gameName, string targetLang)
        {
            try
            {
                string url = $"{DefaultBaseUrl}/translations?q={Uri.EscapeDataString(gameName)}&lang={Uri.EscapeDataString(targetLang)}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    return new TranslationSearchResult { Success = false, Error = $"HTTP {response.StatusCode}" };
                }

                string json = await response.Content.ReadAsStringAsync();
                var data = ParseJsonSafe(json);

                var result = new TranslationSearchResult { Success = true };
                result.Count = data["count"]?.Value<int>() ?? 0;
                result.Translations = new List<TranslationInfo>();

                var translations = data["translations"] as JArray;
                if (translations != null)
                {
                    foreach (var t in translations)
                    {
                        result.Translations.Add(ParseTranslationInfo(t));
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Search error: {e.Message}");
                return new TranslationSearchResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        /// <summary>
        /// One side of a review — "new" or "differing" — read into the socle's own shape.
        ///
        /// ⚠ A missing letter is zero, unlike a missing figure elsewhere: the server sends only the
        /// tags it counted, so an absent "S" means no refusals rather than an unknown number. What
        /// stands for "we do not know" is the whole `lines_waiting` block being absent, which an
        /// older server does not send at all.
        /// </summary>
        internal static TagTally TallyOf(JToken waiting, string side)
        {
            var tags = waiting?[side];
            if (tags == null || tags.Type != JTokenType.Object) return default(TagTally);

            return new TagTally
            {
                Human = tags["H"]?.Value<int>() ?? 0,
                Validated = tags["V"]?.Value<int>() ?? 0,
                Machine = tags["A"]?.Value<int>() ?? 0,
                Skipped = tags["S"]?.Value<int>() ?? 0,
            };
        }

        /// <summary>
        /// Where a fork came from, or null when it came from nowhere.
        ///
        /// ⚠ An author of null inside a present block is NOT the same as an absent block: the
        /// first is a fork whose source account has gone, which is still a credit worth showing;
        /// the second is a translation somebody started themselves.
        /// </summary>
        private static Origin? ParseOrigin(JToken origin)
        {
            if (origin == null || origin.Type != JTokenType.Object) return null;

            return new Origin(origin["author"]?.Value<string>(),
                              origin["lines"]?.Value<int?>());
        }

        private static TranslationInfo ParseTranslationInfo(JToken t)
        {
            var game = t["game"];
            return new TranslationInfo
            {
                Id = t["id"]?.Value<int>() ?? 0,
                GameName = game?["name"]?.Value<string>(),
                GameSlug = game?["slug"]?.Value<string>(),
                GameSteamId = game?["steam_id"]?.Value<string>(),
                GameImageUrl = game?["image_url"]?.Value<string>(),
                Uploader = t["uploader"]?.Value<string>(),
                SourceLanguage = t["source_language"]?.Value<string>(),
                TargetLanguage = t["target_language"]?.Value<string>(),
                LineCount = t["line_count"]?.Value<int>() ?? 0,
                Status = t["status"]?.Value<string>(),
                Type = t["type"]?.Value<string>(),
                Notes = t["notes"]?.Value<string>(),
                ResourcesUrl = t["resources_url"]?.Value<string>(),
                // Whether its Main takes contributions. Null on a server that predates the
                // field, and null shows nothing — silence is not "solo work".
                AcceptsBranches = t["accepts_branches"]?.ToObject<bool?>(),
                // Where a fork came from. Absent on a server that predates the field and on
                // anything nobody forked — both read as "started from nothing", which is what the
                // row then says by saying nothing.
                Origin = ParseOrigin(t["origin"]),
                VoteCount = t["vote_count"]?.Value<int>() ?? 0,
                // Null for anonymous callers and for servers older than this field.
                UserVote = t["user_vote"]?.Value<int?>(),
                DownloadCount = t["download_count"]?.Value<int>() ?? 0,
                HumanCount = t["human_count"]?.Value<int>() ?? 0,
                ValidatedCount = t["validated_count"]?.Value<int>() ?? 0,
                AiCount = t["ai_count"]?.Value<int>() ?? 0,
                CaptureCount = t["capture_count"]?.Value<int>() ?? 0,
                SkippedCount = t["skipped_count"]?.Value<int>() ?? 0,
                FileHash = t["file_hash"]?.Value<string>(),
                FileUuid = t["file_uuid"]?.Value<string>(),
                UpdatedAt = t["updated_at"]?.Value<string>(),
                // Null on servers older than this field: the list then shows no
                // date rather than one that a vote could have moved
                ContentUpdatedAt = t["content_updated_at"]?.Value<string>(),
                // Same rule: absent means unknown, and the list says nothing rather than 0 %
                GameCoverage = t["game_coverage"]?.Value<float?>(),
                CreatedAt = t["created_at"]?.Value<string>()
            };
        }

        #endregion

        #region Translation Download

        /// <summary>
        /// Download a translation file
        /// </summary>
        public static async Task<TranslationDownloadResult> Download(int translationId, string currentHash = null)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{DefaultBaseUrl}/translations/{translationId}/download");

                if (!string.IsNullOrEmpty(currentHash))
                {
                    request.Headers.Add("If-None-Match", $"\"{currentHash}\"");
                }

                var response = await client.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    return new TranslationDownloadResult { Success = true, NotModified = true };
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new TranslationDownloadResult { Success = false, Error = $"HTTP {response.StatusCode}" };
                }

                // Read under a ceiling, whatever the transport did.
                //
                // 🔴 **The bound is what matters here, not the gzip.** This used to inflate the
                // body itself, because the handler did not — and it counted the bytes as they came
                // out, so a gzip bomb (a few KB claiming gigabytes) was refused DURING inflation
                // rather than after filling memory with it. The handler now decompresses, which
                // fixes every other call in this file, and would have quietly taken that ceiling
                // away with it: `Content-Encoding` is removed once decompressed, so the branch
                // holding the check could never run again.
                //
                // So the ceiling moves to where it no longer depends on an encoding at all — the
                // decompressed stream, which is the only thing whose size was ever the danger.
                string jsonContent;
                using (var body = await response.Content.ReadAsStreamAsync())
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await body.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > MaxTranslationJsonBytes)
                        {
                            return new TranslationDownloadResult
                            {
                                Success = false,
                                Error = $"Decompressed content exceeds {MaxTranslationJsonBytes} bytes"
                            };
                        }
                        output.Write(buffer, 0, read);
                    }
                    jsonContent = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
                }

                // Validate JSON structure before accepting
                if (!ValidateTranslationJson(jsonContent, out string validationError))
                {
                    TranslatorCore.LogWarning($"[ApiClient] Downloaded content failed validation: {validationError}");
                    return new TranslationDownloadResult
                    {
                        Success = false,
                        Error = $"Invalid translation file: {validationError}"
                    };
                }

                // Extract ETag for hash
                string etag = null;
                if (response.Headers.ETag != null)
                {
                    etag = response.Headers.ETag.Tag?.Trim('"');
                }

                return new TranslationDownloadResult
                {
                    Success = true,
                    Content = jsonContent,
                    FileHash = etag
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Download error: {e.Message}");
                return new TranslationDownloadResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        #endregion

        #region UUID Check

        /// <summary>
        /// Check if a UUID exists on the server before uploading.
        /// Determines if this is NEW, UPDATE, or FORK.
        /// Requires authentication.
        /// </summary>
        public static async Task<UuidCheckResult> CheckUuid(string uuid)
        {
            try
            {
                string url = $"{DefaultBaseUrl}/translations/check-uuid?uuid={Uri.EscapeDataString(uuid)}";
                var response = await client.GetAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return new UuidCheckResult { Success = false, Error = "Not authenticated" };
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new UuidCheckResult { Success = false, Error = $"HTTP {response.StatusCode}" };
                }

                string json = await response.Content.ReadAsStringAsync();
                TranslatorCore.LogDebug($"[ApiClient] CheckUuid response: {json}");
                var data = ParseJsonSafe(json);

                // Parse role first to derive IsOwner
                string roleStr = data["role"]?.Value<string>();
                TranslationRole role;
                switch (roleStr)
                {
                    case "main":
                        role = TranslationRole.Main;
                        break;
                    case "branch":
                        role = TranslationRole.Branch;
                        break;
                    default:
                        role = TranslationRole.None;
                        break;
                }

                var result = new UuidCheckResult
                {
                    Success = true,
                    Exists = data["exists"]?.Value<bool>() ?? false,
                    // IsOwner = user has a translation (role is main or branch)
                    IsOwner = role == TranslationRole.Main || role == TranslationRole.Branch,
                    Role = role,
                    // MainUsername is in main.uploader when role is none and main exists
                    MainUsername = data["main"]?["uploader"]?.Value<string>(),
                    // Null on older servers: "unknown", never "the Main is fine"
                    MainMissing = data["main_missing"]?.ToObject<bool?>(),
                    MainAbandoned = data["main_abandoned"]?.ToObject<bool?>(),
                    // Use ToObject<int?>() to handle explicit JSON null values
                    BranchesCount = data["branches_count"]?.ToObject<int?>() ?? 0,

                    // ⚠ ToObject<bool?> rather than Value<bool>: a missing field must stay null
                    // and not become false. See the properties.
                    AcceptsBranches = data["accepts_branches"]?.ToObject<bool?>(),
                    BranchFrozen = data["branch_frozen"]?.ToObject<bool?>(),

                    // ⚠ Null on an older site, and null is "unknown" — never "nothing is waiting".
                    BranchesWithWork = data["branches_with_work"]?.ToObject<int?>(),
                    LinesAvailable = data["lines_available"]?.ToObject<int?>(),

                    // The other axis: how many rows need a decision, and what they are made of.
                    // Absent on a server that predates it, and the card then shows the total
                    // alone, as before.
                    LinesToReview = data["lines_waiting"]?["review"]?.ToObject<int?>(),
                    LinesNew = TallyOf(data["lines_waiting"], "new"),
                    LinesDiffering = TallyOf(data["lines_waiting"], "differing"),
                    LinesOffered = data["lines_offered"]?.ToObject<int?>()
                };

                // Votes on the published translation of this lineage. Absent on older servers,
                // and null when nothing of this lineage is published — both mean "no vote to
                // show here", never "zero votes".
                var voteToken = data["vote"];
                if (voteToken != null && voteToken.Type == JTokenType.Object)
                {
                    result.Vote = new VoteState
                    {
                        TargetId = voteToken["target_id"]?.Value<int>() ?? 0,
                        Count = voteToken["count"]?.Value<int>() ?? 0,
                        UserVote = voteToken["user_vote"]?.Value<int?>(),
                        CanVote = voteToken["can_vote"]?.Value<bool>() ?? false,
                    };
                }

                TranslatorCore.LogInfo($"[ApiClient] Parsed: exists={result.Exists}, isOwner={result.IsOwner}, role={result.Role}");

                // Parse translation info if UPDATE
                if (result.Exists && result.IsOwner && data["translation"] != null)
                {
                    var t = data["translation"];
                    result.ExistingTranslation = new UuidCheckTranslationInfo
                    {
                        Id = t["id"]?.Value<int>() ?? 0,
                        SourceLanguage = t["source_language"]?.Value<string>(),
                        TargetLanguage = t["target_language"]?.Value<string>(),
                        Type = t["type"]?.Value<string>(),
                        // Null on a server that predates this field — the caller then leaves the
                        // status alone rather than guessing at one.
                        Status = t["status"]?.Value<string>(),
                        Notes = t["notes"]?.Value<string>(),
                        ResourcesUrl = t["resources_url"]?.Value<string>(),
                        // The row's own link, which is not the same question as the one above.
                        // Null on a server that predates the field — the edit field then falls
                        // back to the effective value, exactly as it behaved before.
                        OwnResourcesUrl = t["resources_url_own"]?.Value<string>(),
                        LineCount = t["line_count"]?.Value<int>() ?? 0,
                        FileHash = t["file_hash"]?.Value<string>(),
                        UpdatedAt = t["updated_at"]?.Value<string>()
                    };
                }

                // Parse main info if FORK (user doesn't own but main exists)
                // API returns "main" object, not "original"
                if (result.Exists && !result.IsOwner && data["main"] != null)
                {
                    var m = data["main"];
                    result.OriginalTranslation = new UuidCheckTranslationInfo
                    {
                        Id = m["id"]?.Value<int>() ?? 0,
                        Uploader = m["uploader"]?.Value<string>(),
                        SourceLanguage = m["source_language"]?.Value<string>(),
                        TargetLanguage = m["target_language"]?.Value<string>(),
                        Type = m["type"]?.Value<string>(),
                        LineCount = m["line_count"]?.Value<int>() ?? 0,
                        UpdatedAt = m["updated_at"]?.Value<string>()
                    };
                }

                return result;
            }
            catch (HttpRequestException httpEx)
            {
                TranslatorCore.LogWarning($"[ApiClient] UUID check HTTP error: {httpEx.Message}");
                if (httpEx.InnerException != null)
                {
                    TranslatorCore.LogWarning($"[ApiClient] Inner exception: {httpEx.InnerException.Message}");
                }
                return new UuidCheckResult { Success = false, Error = Connectivity.Describe(httpEx) };
            }
            catch (TaskCanceledException tcEx)
            {
                TranslatorCore.LogWarning($"[ApiClient] UUID check timeout: {tcEx.Message}");
                return new UuidCheckResult { Success = false, Error = "Request timeout" };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] UUID check error: {e.GetType().Name}: {e.Message}");
                if (e.InnerException != null)
                {
                    TranslatorCore.LogWarning($"[ApiClient] Inner exception: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                }
                return new UuidCheckResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        #endregion

        #region Branches

        /// <summary>
        /// Get list of branches contributing to a UUID.
        /// Requires authentication.
        /// </summary>
        public static async Task<BranchListResult> GetBranches(string uuid)
        {
            try
            {
                string url = $"{DefaultBaseUrl}/translations/{Uri.EscapeDataString(uuid)}/branches";
                var response = await client.GetAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return new BranchListResult { Success = false, Error = "Not authenticated" };
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new BranchListResult { Success = false, Error = $"HTTP {response.StatusCode}" };
                }

                string json = await response.Content.ReadAsStringAsync();
                var data = ParseJsonSafe(json);

                var result = new BranchListResult
                {
                    Success = true,
                    Branches = new List<BranchInfo>()
                };

                var branches = data["branches"] as JArray;
                if (branches != null)
                {
                    foreach (var b in branches)
                    {
                        result.Branches.Add(new BranchInfo
                        {
                            Id = b["id"]?.Value<int>() ?? 0,
                            // API returns user.name (nested object)
                            Username = b["user"]?["name"]?.Value<string>(),
                            LineCount = b["line_count"]?.Value<int>() ?? 0,
                            HumanCount = b["human_count"]?.Value<int>() ?? 0,
                            AiCount = b["ai_count"]?.Value<int>() ?? 0,
                            ValidatedCount = b["validated_count"]?.Value<int>() ?? 0,
                            UpdatedAt = b["updated_at"]?.Value<string>()
                        });
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] GetBranches error: {e.Message}");
                return new BranchListResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        #endregion

        #region Validation

        /// <summary>
        /// Validate downloaded translation JSON content.
        /// Ensures it's valid JSON with expected structure.
        /// </summary>
        private static bool ValidateTranslationJson(string json, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Empty content";
                return false;
            }

            // Size limit
            if (json.Length > MaxTranslationJsonBytes)
            {
                error = $"Content too large ({json.Length} bytes)";
                return false;
            }

            try
            {
                // ParseJsonSafe enforces MaxDepth=10 (translation files are flat key-value)
                var parsed = ParseJsonSafe(json);

                // Must be a JSON object (not array)
                if (parsed == null)
                {
                    error = "Invalid JSON structure";
                    return false;
                }

                // Check for required _uuid field
                if (!parsed.ContainsKey("_uuid"))
                {
                    error = "Missing _uuid field";
                    return false;
                }

                // Validate _uuid format (should be a valid GUID)
                string uuid = parsed["_uuid"]?.Value<string>();
                if (string.IsNullOrEmpty(uuid) || !Guid.TryParse(uuid, out _))
                {
                    error = "Invalid _uuid format";
                    return false;
                }

                // Validate all non-metadata entries are valid translation values
                foreach (var prop in parsed.Properties())
                {
                    // Skip metadata fields
                    if (prop.Name.StartsWith("_"))
                        continue;

                    // Each translation entry can be:
                    // - A string (old format): "key": "value"
                    // - An object (new format): "key": {"v": "value", "t": "A/H/V", "i": 123}
                    if (prop.Value.Type == JTokenType.String)
                    {
                        // Old format - valid
                        continue;
                    }
                    else if (prop.Value.Type == JTokenType.Object)
                    {
                        // New format - validate structure
                        var entry = prop.Value as JObject;
                        if (entry == null || !entry.ContainsKey("v"))
                        {
                            error = $"Invalid entry format for key '{prop.Name}' (missing 'v' field)";
                            return false;
                        }
                        // "v" must be string, "t" is optional but must be string if present,
                        // "i" (capture-order index) is optional but must be an integer if
                        // present — out-of-range values are handled at parse time
                        // (treated as absent), not rejected here
                        if (entry["v"]?.Type != JTokenType.String)
                        {
                            error = $"Invalid 'v' type for key '{prop.Name}' (expected string)";
                            return false;
                        }
                        if (entry.ContainsKey("t") && entry["t"]?.Type != JTokenType.String)
                        {
                            error = $"Invalid 't' type for key '{prop.Name}' (expected string)";
                            return false;
                        }
                        if (entry.ContainsKey("i") && entry["i"]?.Type != JTokenType.Integer)
                        {
                            error = $"Invalid 'i' type for key '{prop.Name}' (expected integer)";
                            return false;
                        }
                    }
                    else
                    {
                        error = $"Invalid value type for key '{prop.Name}' (expected string or object)";
                        return false;
                    }
                }

                return true;
            }
            catch (JsonReaderException ex)
            {
                error = $"JSON parse error: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Validation error: {ex.Message}";
                return false;
            }
        }

        #endregion

        #region Game Search

        /// <summary>
        /// Search for games by Steam ID
        /// </summary>
        public static async Task<GameSearchResult> SearchGameBySteamId(string steamId)
        {
            try
            {
                string url = $"{DefaultBaseUrl}/games?steam_id={Uri.EscapeDataString(steamId)}";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    return new GameSearchResult { Success = false, Error = $"HTTP {response.StatusCode}" };
                }

                string json = await response.Content.ReadAsStringAsync();
                var data = ParseJsonSafe(json);

                var result = new GameSearchResult { Success = true };
                result.Count = data["count"]?.Value<int>() ?? 0;
                result.Games = new List<GameApiInfo>();

                var games = data["games"] as JArray;
                if (games != null)
                {
                    foreach (var g in games)
                    {
                        result.Games.Add(new GameApiInfo
                        {
                            Id = g["id"]?.Value<int>() ?? 0,
                            Name = g["name"]?.Value<string>(),
                            Slug = g["slug"]?.Value<string>(),
                            SteamId = g["steam_id"]?.Value<string>(),
                            ImageUrl = g["image_url"]?.Value<string>(),
                            TranslationsCount = g["translations_count"]?.Value<int>() ?? 0
                        });
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Game search error: {e.Message}");
                return new GameSearchResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        /// <summary>
        /// Search for games using external APIs (Steam, IGDB, RAWG).
        /// Use this for finding games that may not be in the database yet.
        /// </summary>
        public static async Task<GameSearchResult> SearchGamesExternal(string query, string steamId = null)
        {
            try
            {
                var urlBuilder = new StringBuilder($"{DefaultBaseUrl}/games/search?");

                if (!string.IsNullOrEmpty(query))
                {
                    urlBuilder.Append($"q={Uri.EscapeDataString(query)}");
                }

                if (!string.IsNullOrEmpty(steamId))
                {
                    if (!string.IsNullOrEmpty(query)) urlBuilder.Append("&");
                    urlBuilder.Append($"steam_id={Uri.EscapeDataString(steamId)}");
                }

                var response = await client.GetAsync(urlBuilder.ToString());

                if (!response.IsSuccessStatusCode)
                {
                    return new GameSearchResult { Success = false, Error = $"HTTP {response.StatusCode}" };
                }

                string json = await response.Content.ReadAsStringAsync();
                var data = ParseJsonSafe(json);

                var result = new GameSearchResult { Success = true };
                result.Count = data["count"]?.Value<int>() ?? 0;
                result.Games = new List<GameApiInfo>();

                var games = data["games"] as JArray;
                if (games != null)
                {
                    foreach (var g in games)
                    {
                        result.Games.Add(new GameApiInfo
                        {
                            Id = g["id"]?.Value<int>() ?? 0,
                            Name = g["name"]?.Value<string>(),
                            SteamId = g["steam_id"]?.Value<string>(),
                            ImageUrl = g["image_url"]?.Value<string>(),
                            Source = g["source"]?.Value<string>()
                        });
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] External game search error: {e.Message}");
                return new GameSearchResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        #endregion

        #region Connection Test

        /// <summary>
        /// Test if the API is reachable
        /// </summary>
        public static async Task<bool> TestConnection()
        {
            try
            {
                var response = await client.GetAsync($"{DefaultBaseUrl}/games?limit=1");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Device Flow Authentication

        /// <summary>
        /// What this game says about itself when asking to be linked, or null when it has nothing
        /// to say. Read on the "Linked devices" screen, where a line otherwise reads "Mod, linked
        /// on 12 March" and looks exactly like the eleven above it.
        /// </summary>
        /// <remarks>
        /// ⚠ Optional at both ends. The site accepts a request with no body — every mod already
        /// installed sends one, and none of them will ever be updated — so nothing here may become
        /// a condition of signing in.
        ///
        /// ⚠ The Steam id is sent apart from the name because the two do different jobs: the id is
        /// what lets one game hold one access, and it only works because it identifies a game
        /// exactly. `product_name` does not — two different games can carry the same one — which is
        /// why it travels as a label and never as an identity.
        /// </remarks>
        private static HttpContent DeviceFlowDeclaration()
        {
            var payload = DeviceFlowPayload();

            return payload == null
                ? null
                : new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
        }

        /// <summary>
        /// What we say about this game, in one place.
        ///
        /// ⚠ Shared by the link and by the header on ordinary calls (see <see cref="DeclareGame"/>).
        /// Two builders would be two answers to one question, free to drift — and the drift would
        /// show up as a game named one way when linking and another way afterwards.
        /// </summary>
        private static JObject DeviceFlowPayload()
        {
            var game = TranslatorCore.CurrentGame;

            if (game == null)
            {
                return null;
            }

            var payload = new JObject();

            // Digits only: the site refuses anything else, and an id that is not one is not an id.
            if (!string.IsNullOrEmpty(game.steam_id) && IsAllDigits(game.steam_id))
            {
                payload["game_id"] = game.steam_id;
            }

            // What the game calls itself, never the folder it sits in — see GameInfo.product_name.
            string label = !string.IsNullOrEmpty(game.product_name) ? game.product_name : game.name;
            if (!string.IsNullOrEmpty(label))
            {
                payload["game_name"] = label.Length > 120 ? label.Substring(0, 120) : label;
            }

            if (!payload.HasValues)
            {
                return null;
            }

            return payload;
        }

        private static bool IsAllDigits(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                {
                    return false;
                }
            }

            return value.Length > 0;
        }

        /// <summary>
        /// Hand this access back to the server, so it stops existing rather than sitting in the
        /// account's list for anybody to wonder about. Returns false when the site could not be
        /// reached — the caller signs out locally either way and says so.
        /// </summary>
        /// <remarks>
        /// ⚠ The token is passed in rather than read from the client, because signing out clears
        /// the client's own credentials first: the local state must never depend on a network call
        /// succeeding.
        ///
        /// ⚠ Never called when the server has just refused the token (a 401): there is nothing left
        /// to revoke, and asking would only tell somebody who already knows.
        /// </remarks>
        public static async Task<bool> RevokeToken(string plainToken)
        {
            if (string.IsNullOrEmpty(plainToken))
            {
                return true;
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Delete, $"{DefaultBaseUrl}/auth/token");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + plainToken);

                var response = await client.SendAsync(request);

                // Already gone is the outcome we wanted, not a failure.
                return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Could not revoke the access: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Initiate Device Flow authentication.
        /// Returns a device code and user code to display.
        /// </summary>
        public static async Task<DeviceFlowInitResult> InitiateDeviceFlow()
        {
            try
            {
                var response = await client.PostAsync($"{DefaultBaseUrl}/auth/device", DeviceFlowDeclaration());

                if (!response.IsSuccessStatusCode)
                {
                    return new DeviceFlowInitResult { Success = false, Error = $"HTTP {response.StatusCode}" };
                }

                string json = await response.Content.ReadAsStringAsync();
                var data = ParseJsonSafe(json);

                return new DeviceFlowInitResult
                {
                    Success = true,
                    DeviceCode = data["device_code"]?.Value<string>(),
                    UserCode = data["user_code"]?.Value<string>(),
                    VerificationUri = data["verification_uri"]?.Value<string>(),
                    ExpiresIn = data["expires_in"]?.Value<int>() ?? 900,
                    Interval = data["interval"]?.Value<int>() ?? 5
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Device flow init error: {e.Message}");
                return new DeviceFlowInitResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        #endregion

        #region Upload

        /// <summary>
        /// Maximum upload size (100MB) - must match server limit.
        /// Even Baldur's Gate 3 (largest RPG ever) = ~80MB JSON with key+value.
        /// </summary>
        private const int MaxUploadSizeBytes = 100 * 1024 * 1024;

        /// <summary>
        /// Compress JSON string using gzip for upload bandwidth optimization.
        /// Reduces upload size by ~70% for typical translation files.
        /// </summary>
        private static ByteArrayContent CompressJson(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            using (var memoryStream = new MemoryStream())
            {
                using (var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal))
                {
                    gzipStream.Write(bytes, 0, bytes.Length);
                }
                var compressed = memoryStream.ToArray();
                var content = new ByteArrayContent(compressed);
                content.Headers.Add("Content-Encoding", "gzip");
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return content;
            }
        }

        /// <summary>
        /// Upload a translation to the website.
        /// Requires authentication (SetAuthToken must be called first).
        /// Uses gzip compression to reduce upload bandwidth (~70% reduction).
        /// </summary>
        public static async Task<UploadResult> UploadTranslation(UploadRequest request)
        {
            TranslatorCore.LogInfo($"[ApiClient] UploadTranslation called - game={request.GameName}, status={request.Status}");
            try
            {
                // Note: type is auto-calculated by server from HVASM tags in content
                var payload = new
                {
                    steam_id = request.SteamId,
                    game_name = request.GameName,
                    source_language = request.SourceLanguage,
                    target_language = request.TargetLanguage,
                    status = request.Status,
                    content = request.Content,
                    notes = request.Notes,
                    resources_url = request.ResourcesUrl,
                    accepts_branches = request.AcceptsBranches,
                    // Provenance of a fork, sent in the REQUEST rather than inside the file: an
                    // older version rebuilds translations.json from the metadata it knows and
                    // would drop an unknown block on its next save. Null on every upload that is
                    // not a fork, and a server that ignores these fields behaves exactly as
                    // before — nothing here is required.
                    forked_from_id = TranslatorCore.ForkedFromSiteId,
                    forked_from_hash = TranslatorCore.ForkedFromHash,
                    forked_from_lines = TranslatorCore.ForkedFromResolvedLines
                };

                var jsonPayload = JsonConvert.SerializeObject(payload);

                // Check size before sending to avoid wasting bandwidth
                if (jsonPayload.Length > MaxUploadSizeBytes)
                {
                    TranslatorCore.LogWarning($"[ApiClient] Upload rejected: file too large ({jsonPayload.Length / (1024 * 1024)}MB > {MaxUploadSizeBytes / (1024 * 1024)}MB limit)");
                    return new UploadResult { Success = false, Error = $"Translation file too large ({jsonPayload.Length / (1024 * 1024)}MB). Maximum is {MaxUploadSizeBytes / (1024 * 1024)}MB." };
                }

                var content = CompressJson(jsonPayload);

                TranslatorCore.LogInfo($"[ApiClient] POSTing to {DefaultBaseUrl}/translations (gzip: {jsonPayload.Length} -> {content.Headers.ContentLength ?? 0} bytes)...");
                var response = await client.PostAsync($"{DefaultBaseUrl}/translations", content);
                TranslatorCore.LogInfo($"[ApiClient] Response: {(int)response.StatusCode} {response.StatusCode}");

                string json = await response.Content.ReadAsStringAsync();
                var data = ParseJsonSafe(json);

                if (!response.IsSuccessStatusCode)
                {
                    // Handle different error formats (Laravel validation vs custom)
                    string errorMsg = data["error"]?.Value<string>()
                        ?? data["message"]?.Value<string>()
                        ?? $"HTTP {response.StatusCode}";

                    // Include validation errors if present
                    var errors = data["errors"];
                    if (errors != null)
                    {
                        var errorList = new List<string>();
                        foreach (var prop in errors.Children<JProperty>())
                        {
                            foreach (var e in prop.Value)
                            {
                                errorList.Add(e.Value<string>());
                            }
                        }
                        if (errorList.Count > 0)
                        {
                            errorMsg = string.Join(", ", errorList);
                        }
                    }

                    TranslatorCore.LogWarning($"[ApiClient] Upload failed: {errorMsg}");
                    return new UploadResult
                    {
                        Success = false,
                        Error = errorMsg
                    };
                }

                var translation = data["translation"];

                // Parse role from API response
                string roleStr = translation?["role"]?.Value<string>();
                TranslationRole role;
                switch (roleStr)
                {
                    case "main":
                        role = TranslationRole.Main;
                        break;
                    case "branch":
                        role = TranslationRole.Branch;
                        break;
                    default:
                        role = TranslationRole.None;
                        break;
                }

                return new UploadResult
                {
                    Success = true,
                    TranslationId = translation?["id"]?.Value<int>() ?? 0,
                    FileHash = translation?["file_hash"]?.Value<string>(),
                    LineCount = translation?["line_count"]?.Value<int>() ?? 0,
                    Role = role,
                    WebUrl = translation?["web_url"]?.Value<string>()
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Upload error: {e.Message}");
                return new UploadResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        #endregion

        #region Merge Preview

        /// <summary>
        /// Initialize a merge preview session.
        /// Sends local content to server and returns a URL to open in browser.
        /// Requires authentication.
        /// </summary>
        /// <param name="toLocal">
        /// True to compare WITHOUT publishing: the arbitrated result comes back to this machine
        /// and the online version is left alone. It is also the only mode allowed against a
        /// translation we do not own — a branch measuring itself against its Main — since
        /// publishing there is refused.
        /// </param>
        public static async Task<MergePreviewInitResult> InitMergePreview(int translationId, Dictionary<string, TranslationEntry> localContent, bool toLocal = false)
        {
            try
            {
                // Convert TranslationEntry to simple format for API.
                // "i" (capture-order index) is omitted when absent — an anonymous
                // type would serialize "i": null, which the server rejects
                var contentForApi = new Dictionary<string, object>();
                foreach (var kvp in localContent)
                {
                    if (kvp.Key.StartsWith("_")) continue; // Skip metadata

                    var entry = new Dictionary<string, object>
                    {
                        ["v"] = kvp.Value.Value,
                        ["t"] = kvp.Value.Tag
                    };
                    if (kvp.Value.Index.HasValue)
                    {
                        entry["i"] = kvp.Value.Index.Value;
                    }
                    contentForApi[kvp.Key] = entry;
                }

                var payload = new
                {
                    translation_id = translationId,
                    local_content = contentForApi,
                    destination = toLocal ? "local" : "server"
                };

                var jsonPayload = JsonConvert.SerializeObject(payload);
                var content = CompressJson(jsonPayload);

                TranslatorCore.LogInfo($"[ApiClient] Initiating merge preview for translation #{translationId} (gzip: {jsonPayload.Length} -> {content.Headers.ContentLength ?? 0} bytes)...");
                var response = await client.PostAsync($"{DefaultBaseUrl}/merge-preview/init", content);

                string json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    string errorMsg = DescribeHttpError(response, json);

                    TranslatorCore.LogWarning($"[ApiClient] Merge preview init failed: {errorMsg}");
                    return new MergePreviewInitResult { Success = false, Error = errorMsg };
                }

                var data = ParseJsonSafe(json);

                return new MergePreviewInitResult
                {
                    Success = true,
                    Token = data["token"]?.Value<string>(),
                    Url = data["url"]?.Value<string>(),
                    ExpiresAt = data["expires_at"]?.Value<string>()
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Merge preview init error: {e.Message}");
                return new MergePreviewInitResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        /// <summary>
        /// Collect the result of a comparison that ends here rather than on the server.
        ///
        /// Only exists for that mode: a published comparison BECAME the online version, so it is
        /// read back through the ordinary download. Here nothing was published, and the file the
        /// player arbitrated lives with the token.
        /// </summary>
        public static async Task<TranslationDownloadResult> GetMergePreviewResult(string token)
        {
            try
            {
                var response = await client.GetAsync($"{DefaultBaseUrl}/merge-preview/{Uri.EscapeDataString(token)}/result");
                string json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new TranslationDownloadResult { Success = false, Error = DescribeHttpError(response, json) };
                }

                var data = ParseJsonSafe(json);
                var content = data["content"];
                if (content == null || content.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                {
                    return new TranslationDownloadResult { Success = false, Error = "Merge result was empty" };
                }

                return new TranslationDownloadResult
                {
                    Success = true,
                    Content = content.ToString(Newtonsoft.Json.Formatting.None)
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Merge result fetch error: {e.Message}");
                return new TranslationDownloadResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        /// <summary>
        /// Get the full URL for a merge preview result
        /// </summary>
        public static string GetMergePreviewFullUrl(string relativeUrl)
        {
            if (string.IsNullOrEmpty(relativeUrl)) return null;
            // URL from API may be relative, make it absolute
            if (relativeUrl.StartsWith("/"))
            {
                return $"{WebsiteBaseUrl}{relativeUrl}";
            }
            return relativeUrl;
        }

        #endregion

        #region Edit Session (anonymous live edit in browser)

        /// <summary>
        /// Human-readable label of the active translation backend, advertised
        /// to the live edit session (tooltip of the browser's retranslate
        /// button). Null when AI translation is disabled.
        /// </summary>
        private static string GetAiBackendLabel()
        {
            var config = TranslatorCore.Config;
            if (config == null || !config.enable_ai) return null;
            switch (config.translation_backend)
            {
                case "llm": return string.IsNullOrEmpty(config.ai_model) ? "LLM" : config.ai_model;
                case "google": return "Google Translate";
                case "deepl": return "DeepL";
                default: return config.translation_backend;
            }
        }

        /// <summary>
        /// Hand the browser the answer to a retranslation it asked for.
        ///
        /// ⚠ Its own endpoint, NOT the content push: nothing was written, so the
        /// file is unchanged and the push would skip itself — and it carries the
        /// whole file, which for one proposed line would be absurd.
        ///
        /// The value travels as a PROPOSAL: the page stages it as a pending edit,
        /// under the same Save button as anything typed there. That is the whole
        /// point — a retranslation must not be the one gesture that writes without
        /// being validated.
        /// </summary>
        public static async Task SendRetranslationResult(string modKey, string requestId, string key,
            string value, string outcome)
        {
            if (string.IsNullOrEmpty(modKey) || string.IsNullOrEmpty(requestId)) return;

            try
            {
                var payload = new JObject
                {
                    ["id"] = requestId,
                    ["key"] = key,
                    ["value"] = value,
                    ["outcome"] = outcome
                };

                var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None),
                    Encoding.UTF8, "application/json");
                var response = await client.PostAsync(
                    $"{DefaultBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}/retranslation", content);

                if (!response.IsSuccessStatusCode)
                {
                    // Nothing is lost in the file — the proposal simply never reaches
                    // the page, which frees its waiting row on its own timer.
                    TranslatorCore.LogWarning(
                        $"[ApiClient] Retranslation result not delivered: HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Retranslation result error: {e.Message}");
            }
        }

        /// <summary>
        /// Initialize a live edit session: uploads the raw local translations
        /// file (metadata keys INCLUDED — the session file comes back verbatim
        /// to replace translations.json, so _uuid/_game/_source must survive
        /// the round trip). No authentication required.
        /// </summary>
        public static async Task<EditSessionInitResult> InitEditSession()
        {
            try
            {
                if (!System.IO.File.Exists(TranslatorCore.CachePath))
                {
                    return new EditSessionInitResult { Success = false, Error = "No local translation file to edit" };
                }

                string raw = System.IO.File.ReadAllText(TranslatorCore.CachePath);
                JObject contentObj;
                try { contentObj = JObject.Parse(raw); }
                catch
                {
                    return new EditSessionInitResult { Success = false, Error = "Local translation file is not valid JSON" };
                }

                var payload = new JObject
                {
                    ["content"] = contentObj,
                    ["game_name"] = TranslatorCore.CurrentGame?.name,
                    ["source_language"] = TranslatorCore.Config?.GetSourceLanguage(),
                    ["target_language"] = TranslatorCore.Config?.GetTargetLanguage(),
                    // Advertise OUR AI backend so the browser can offer per-line
                    // retranslation — no credential leaves this machine, the
                    // site only ever relays the request back over SSE
                    ["ai_available"] = TranslatorCore.Config?.enable_ai ?? false,
                    ["ai_model"] = GetAiBackendLabel()
                };

                var jsonPayload = payload.ToString(Newtonsoft.Json.Formatting.None);
                var content = CompressJson(jsonPayload);

                TranslatorCore.LogInfo($"[ApiClient] Initiating edit session (gzip: {jsonPayload.Length} -> {content.Headers.ContentLength ?? 0} bytes)...");
                var response = await client.PostAsync($"{DefaultBaseUrl}/edit-session/init", content);

                string json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    string errorMsg = DescribeHttpError(response, json);

                    TranslatorCore.LogWarning($"[ApiClient] Edit session init failed: {errorMsg}");
                    return new EditSessionInitResult { Success = false, Error = errorMsg };
                }

                var data = ParseJsonSafe(json);

                return new EditSessionInitResult
                {
                    Success = true,
                    ModKey = data["mod_key"]?.Value<string>(),
                    Url = data["url"]?.Value<string>(),
                    ExpiresAt = data["expires_at"]?.Value<string>()
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Edit session init error: {e.Message}");
                return new EditSessionInitResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        /// <summary>
        /// Push the current local file to the edit session (the file changed
        /// in-game while the browser session is open). The response carries
        /// browser presence so the caller can conclude the page was closed.
        /// </summary>
        public static async Task<EditSessionUpdateResult> UpdateEditSession(string modKey)
        {
            try
            {
                if (!System.IO.File.Exists(TranslatorCore.CachePath))
                {
                    return new EditSessionUpdateResult { Success = false, Error = "No local translation file" };
                }

                string raw = System.IO.File.ReadAllText(TranslatorCore.CachePath);
                JObject contentObj;
                try { contentObj = JObject.Parse(raw); }
                catch
                {
                    return new EditSessionUpdateResult { Success = false, Error = "Local translation file is not valid JSON" };
                }

                var payload = new JObject
                {
                    ["content"] = contentObj,
                    // Pushes refresh the AI flag: the player can toggle the
                    // backend mid-session and the browser buttons follow
                    ["ai_available"] = TranslatorCore.Config?.enable_ai ?? false,
                    ["ai_model"] = GetAiBackendLabel()
                };
                var content = CompressJson(payload.ToString(Newtonsoft.Json.Formatting.None));

                var response = await client.PostAsync($"{DefaultBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}/update", content);
                string json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var errorData = ParseJsonSafe(json);
                    return new EditSessionUpdateResult
                    {
                        Success = false,
                        SessionGone = response.StatusCode == System.Net.HttpStatusCode.NotFound,
                        Error = errorData["error"]?.Value<string>() ?? $"HTTP {response.StatusCode}"
                    };
                }

                var data = ParseJsonSafe(json);
                return new EditSessionUpdateResult
                {
                    Success = true,
                    ContentHash = data["content_hash"]?.Value<string>(),
                    BrowserSeenSecondsAgo = data["browser_seen_seconds_ago"]?.Value<int?>(),
                    BrowserLeft = data["browser_left"]?.Value<bool>() ?? false
                };
            }
            catch (Exception e)
            {
                return new EditSessionUpdateResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        /// <summary>
        /// Keep the edit session alive while the game runs: a session must
        /// only end on explicit browser close or game shutdown, never on a
        /// timer — the server TTL is just a backstop for orphaned sessions.
        /// Returns false only when the session no longer exists server-side
        /// (transient network failures keep the session).
        /// </summary>
        public static async Task<bool> KeepAliveEditSession(string modKey)
        {
            try
            {
                var response = await client.PostAsync(
                    $"{DefaultBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}/keepalive",
                    new StringContent("{}", Encoding.UTF8, "application/json"));
                return response.StatusCode != System.Net.HttpStatusCode.NotFound;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Ask whether a session still exists, WITHOUT claiming to be present.
        ///
        /// ⚠ Not <see cref="KeepAliveEditSession"/>, although it would answer the same question.
        /// Keepalive means "still here" and pushes the expiry back; used to inspect a session
        /// another program opened, it would hold that session alive on behalf of a window nobody
        /// has looked at since yesterday. The state route exists precisely because asking is not
        /// being present, and says so in its own comment.
        ///
        /// <see cref="EditSessionProbe.Exists"/> is null when the site could not be reached — never
        /// false. The caller must not read a network failure as "nobody is editing".
        /// </summary>
        public static async Task<EditSessionProbe> GetEditSessionState(string modKey)
        {
            try
            {
                var response = await client.GetAsync(
                    $"{DefaultBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}/state");

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new EditSessionProbe { Exists = false };

                if (!response.IsSuccessStatusCode)
                    return new EditSessionProbe { Exists = null };

                var data = ParseJsonSafe(await response.Content.ReadAsStringAsync());
                return new EditSessionProbe
                {
                    Exists = true,
                    PendingChanges = data["pending_changes"]?.Value<int>() ?? 0
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Edit session state error: {e.Message}");
                return new EditSessionProbe { Exists = null };
            }
        }

        /// <summary>What the site says about a session we are asking about, not living in.</summary>
        public class EditSessionProbe
        {
            /// <summary>True alive, false gone, null could not ask.</summary>
            public bool? Exists { get; set; }

            /// <summary>
            /// Saves the browser made that nobody has fetched. ⚠ Until somebody does, the session
            /// is the only place that work exists.
            /// </summary>
            public int PendingChanges { get; set; }
        }

        /// <summary>
        /// End the edit session server-side (user clicked Stop in the mod,
        /// the browser page was closed past the grace period, or the game is
        /// shutting down). Idempotent.
        /// </summary>
        public static async Task<bool> EndEditSession(string modKey)
        {
            try
            {
                var response = await client.DeleteAsync($"{DefaultBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Edit session end error: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Download the current edit session content (called after each
        /// browser save, signaled over SSE).
        /// </summary>
        public static async Task<EditSessionContentResult> GetEditSessionContent(string modKey)
        {
            try
            {
                var response = await client.GetAsync($"{DefaultBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}/content");
                string body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var errorData = ParseJsonSafe(body);
                    string errorMsg = errorData["error"]?.Value<string>() ?? $"HTTP {response.StatusCode}";
                    return new EditSessionContentResult
                    {
                        Success = false,
                        Error = errorMsg,
                        SessionGone = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    };
                }

                return new EditSessionContentResult { Success = true, Content = body };
            }
            catch (Exception e)
            {
                return new EditSessionContentResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        #endregion

        #region Voting

        /// <summary>
        /// Vote on a translation (upvote or downvote).
        /// Requires authentication.
        /// </summary>
        /// <param name="translationId">ID of the translation to vote on</param>
        /// <param name="value">1 for upvote, -1 for downvote</param>
        public static async Task<VoteResult> Vote(int translationId, int value)
        {
            try
            {
                if (value != 1 && value != -1)
                {
                    return new VoteResult { Success = false, Error = "Vote value must be 1 or -1" };
                }

                var token = TranslatorCore.Config?.api_token;
                if (string.IsNullOrEmpty(token))
                {
                    return new VoteResult { Success = false, Error = "Not authenticated" };
                }

                var payload = new { value };
                var content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                var request = new HttpRequestMessage(HttpMethod.Post, $"{DefaultBaseUrl}/translations/{translationId}/vote")
                {
                    Content = content
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var response = await client.SendAsync(request);
                string json = await response.Content.ReadAsStringAsync();
                var data = ParseJsonSafe(json);

                if (!response.IsSuccessStatusCode)
                {
                    var error = data["error"]?.Value<string>() ?? data["message"]?.Value<string>() ?? $"HTTP {response.StatusCode}";
                    return new VoteResult { Success = false, Error = error };
                }

                return new VoteResult
                {
                    Success = true,
                    VoteCount = data["vote_count"]?.Value<int>() ?? 0,
                    UserVote = data["user_vote"]?.Value<int?>()
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Vote error: {e.Message}");
                return new VoteResult { Success = false, Error = Connectivity.Describe(e) };
            }
        }

        #endregion
    }

    #region Result Classes

    public class ModNotificationsResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int Unread { get; set; }
        public List<ModNotificationItem> Items { get; set; } = new List<ModNotificationItem>();
    }

    public class ModNotificationItem
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Text { get; set; }
        public string Url { get; set; }
    }

    public class TranslationSearchResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int Count { get; set; }
        public List<TranslationInfo> Translations { get; set; }
    }

    public class TranslationInfo
    {
        public int Id { get; set; }
        public string GameName { get; set; }
        public string GameSlug { get; set; }
        public string GameSteamId { get; set; }
        public string GameImageUrl { get; set; }
        public string Uploader { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        public int LineCount { get; set; }
        public string Status { get; set; }
        public string Type { get; set; }
        public string Notes { get; set; }
        public string ResourcesUrl { get; set; }
        /// <summary>Whether this lineage takes contributions. Null on an older server, and null
        /// is not "no" — nothing is said rather than inventing somebody's decision.</summary>
        public bool? AcceptsBranches { get; set; }

        /// <summary>
        /// Which translation this one was forked from. Null when it was forked from none — and on
        /// a server that predates the field, where nothing is said rather than a claim made.
        ///
        /// ⚠ Not derivable from anything else here: a fork leads its own lineage and looks exactly
        /// like a translation somebody wrote from scratch. See <see cref="Origins"/>.
        /// </summary>
        public Origin? Origin { get; set; }

        public int VoteCount { get; set; }
        /// <summary>This user's own vote (+1 / -1), null when they haven't voted, aren't
        /// signed in, or the server predates the field.</summary>
        public int? UserVote { get; set; }
        public int DownloadCount { get; set; }
        public int HumanCount { get; set; }
        public int ValidatedCount { get; set; }
        public int AiCount { get; set; }
        public int CaptureCount { get; set; }
        /// <summary>
        /// Lines the author marked as not to translate (tag S). Outside the composition bar and
        /// the score; shown on its own. Zero on servers that predate the field.
        /// </summary>
        public int SkippedCount { get; set; }
        public string FileHash { get; set; }
        public string FileUuid { get; set; }
        public string UpdatedAt { get; set; }

        /// <summary>
        /// When the translation itself last changed. Distinct from UpdatedAt,
        /// which a vote or a download also moves. Null on older servers.
        /// </summary>
        public string ContentUpdatedAt { get; set; }

        /// <summary>
        /// How much of the game this translation reaches, 0 to 1, measured against the furthest
        /// translation of the same game whatever its language.
        ///
        /// Comes from the server because it cannot be computed here: it needs every other
        /// translation of the game. Null on servers that do not report it — and null must read
        /// as "unknown", never as "covers nothing".
        /// </summary>
        public float? GameCoverage { get; set; }

        /// <summary>
        /// When it was published. Null on servers that do not report it — and absence must read
        /// as "unknown", never as "old".
        /// </summary>
        public string CreatedAt { get; set; }

        /// <summary>Published within the last week, by the same reckoning as the website.</summary>
        public bool IsNew
        {
            get
            {
                if (string.IsNullOrEmpty(CreatedAt)) return false;
                DateTime published;
                if (!DateTime.TryParse(CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out published))
                    return false;
                return (DateTime.UtcNow - published.ToUniversalTime()).TotalDays <= 7;
            }
        }

        /// <summary>
        /// The content date as a short local string, or null when the server
        /// did not send one. Never falls back to UpdatedAt: showing a date that
        /// a vote moved would be worse than showing none.
        /// </summary>
        public string ContentDateLabel
        {
            get
            {
                if (string.IsNullOrEmpty(ContentUpdatedAt)) return null;
                DateTime parsed;
                if (!DateTime.TryParse(ContentUpdatedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out parsed))
                {
                    return null;
                }

                return parsed.ToLocalTime().ToString("d MMM yyyy");
            }
        }

        /// <summary>
        /// Quality score (0-3 scale): H=3pts, V=2pts, A=1pt. Shared formula — see
        /// <see cref="TranslationQuality"/>.
        /// </summary>

        /// <summary>
        /// Get website URL for this translation
        /// </summary>
        public string GetWebUrl()
        {
            return $"{ApiClient.WebsiteBaseUrl}/games/{GameSlug}";
        }
    }

    public class TranslationCheckResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public bool HasUpdate { get; set; }
        public string FileHash { get; set; }
        public int LineCount { get; set; }
        public int VoteCount { get; set; }
        public string UpdatedAt { get; set; }

        /// <summary>
        /// Who published it. The only way someone with no account can learn whose work they
        /// installed — every other source of that name is behind authentication.
        /// Null on a server too old to send it, which reads as "unknown", never as "nobody".
        /// </summary>
        public string Uploader { get; set; }

        /// <summary>
        /// The server answered "nothing changed". ⚠ Every other field is then EMPTY, not zero:
        /// a caller that writes them anyway blanks the very values it was trying to spare.
        /// </summary>
        public bool NotModified { get; set; }

        /// <summary>
        /// The validator to hand back on the next call. Opaque on purpose — it stopped being
        /// the file hash the day the answer started carrying the vote count and the uploader.
        /// </summary>
        public string ETag { get; set; }
    }

    public class TranslationDownloadResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public bool NotModified { get; set; }
        public string Content { get; set; }
        public string FileHash { get; set; }
    }

    public class GameSearchResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int Count { get; set; }
        public List<GameApiInfo> Games { get; set; }
    }

    public class GameApiInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Slug { get; set; }
        public string SteamId { get; set; }
        public string ImageUrl { get; set; }
        public int TranslationsCount { get; set; }
        public string Source { get; set; } // "local", "steam", "igdb", "rawg"
    }

    public class DeviceFlowInitResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string DeviceCode { get; set; }
        public string UserCode { get; set; }
        public string VerificationUri { get; set; }
        public int ExpiresIn { get; set; }
        public int Interval { get; set; }
    }

    public class VoteResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int VoteCount { get; set; }
        /// <summary>User's current vote: 1 (upvote), -1 (downvote), or null (no vote)</summary>
        public int? UserVote { get; set; }
    }

    public class UploadRequest
    {
        public string SteamId { get; set; }
        public string GameName { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        // Note: Type is now auto-calculated by server from HVASM tags
        public string Status { get; set; }
        public string Content { get; set; }
        public string Notes { get; set; }
        public string ResourcesUrl { get; set; }

        /// <summary>
        /// Whether this lineage takes contributions. Null on a branch — the decision belongs to
        /// the Main, and a contributor sending it would answer for somebody else's translation.
        /// </summary>
        public bool? AcceptsBranches { get; set; }
    }

    public class UploadResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int TranslationId { get; set; }
        public string FileHash { get; set; }
        public int LineCount { get; set; }
        /// <summary>Role assigned by the server (Main for public, Branch for contributor)</summary>
        public TranslationRole Role { get; set; } = TranslationRole.None;
        public string WebUrl { get; set; }
    }

    public class UuidCheckResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public bool Exists { get; set; }
        public bool IsOwner { get; set; }
        /// <summary>Detected role: Main (owner), Branch (contributor), or None (new)</summary>
        public TranslationRole Role { get; set; } = TranslationRole.None;
        /// <summary>Username of the Main translation owner (if this is a Branch)</summary>
        public string MainUsername { get; set; }

        /// <summary>Branch whose Main is gone. Null on servers that do not report it.</summary>
        public bool? MainMissing { get; set; }

        /// <summary>
        /// The Main is still there and the account behind it is not.
        ///
        /// Same consequence as MainMissing — nobody will ever merge this — reached another way, and
        /// the difference matters to whoever reads it: the translation is still published and still
        /// safe to keep using. Null on servers that do not report it.
        /// </summary>
        public bool? MainAbandoned { get; set; }
        /// <summary>Number of branches contributing to this UUID (if this is Main)</summary>
        public int BranchesCount { get; set; }

        /// <summary>
        /// Whether this lineage takes contributions at all — the Main's own decision.
        ///
        /// Null on a server that predates the field. Null is NOT "no": announcing that somebody
        /// works alone because a server said nothing would put words in their mouth, so an unknown
        /// answer behaves exactly as before and the refusal, if any, arrives from the upload.
        /// </summary>
        public bool? AcceptsBranches { get; set; }

        /// <summary>
        /// A branch whose Main has since closed: nothing can be done with it as a branch any more.
        /// The way on is to publish it as a translation of its own.
        /// </summary>
        public bool? BranchFrozen { get; set; }

        /// <summary>
        /// Of the branches above, how many are actually waiting: not been through in their current
        /// state, AND holding something. Null on a server too old to say — which is "unknown",
        /// never "none". See <see cref="ServerTranslationState.BranchesWithWork"/>.
        /// </summary>
        public int? BranchesWithWork { get; set; }

        /// <summary>How many lines those hold, counted once each. Null if unknown.</summary>
        public int? LinesAvailable { get; set; }

        /// <summary>
        /// How many rows need a decision — see <see cref="ServerTranslationState.LinesToReview"/>.
        /// Null on a server that predates it, which is "unknown", never zero.
        /// </summary>
        public int? LinesToReview { get; set; }

        /// <summary>Of those, the ones the Main does not hold, by the contribution's tag.</summary>
        public TagTally LinesNew { get; set; }

        /// <summary>Of those, the ones both sides hold differently, by the contribution's tag.</summary>
        public TagTally LinesDiffering { get; set; }

        /// <summary>On a branch: what this contribution still holds for its Main. Null if unknown.</summary>
        public int? LinesOffered { get; set; }

        public UuidCheckTranslationInfo ExistingTranslation { get; set; } // For UPDATE
        public UuidCheckTranslationInfo OriginalTranslation { get; set; } // For FORK

        /// <summary>
        /// Votes on the PUBLISHED translation of this lineage — the one being played, and the
        /// one the ranking ranks. Null when nothing of it is published, and on any server too
        /// old to report it: absence must read as "unknown", never as "no votes".
        /// </summary>
        public VoteState Vote { get; set; }
    }

    /// <summary>
    /// What the mod needs to show a vote without deciding anything itself. Whether the player
    /// MAY vote is a server rule (one owner, one translation, no self-votes) and stays there:
    /// the mod asks, it does not re-implement.
    /// </summary>
    public class VoteState
    {
        /// <summary>The translation a vote from here would land on.</summary>
        public int TargetId { get; set; }
        public int Count { get; set; }
        /// <summary>This player's own vote (+1 / -1), null when they have not voted.</summary>
        public int? UserVote { get; set; }
        public bool CanVote { get; set; }
    }

    public class UuidCheckTranslationInfo
    {
        public int Id { get; set; }
        public string Uploader { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        public string Type { get; set; }

        /// <summary>
        /// "in_progress" or "complete" — the author's own declaration.
        ///
        /// ⚠ Read so it can be SHOWN and sent back unchanged. Without it the upload posted
        /// "in_progress" every time, quietly undoing a translation its author had marked complete
        /// on the website.
        /// </summary>
        public string Status { get; set; }
        public string Notes { get; set; }

        /// <summary>
        /// The link to show: this translation's own, or the Main's when a branch has none.
        /// </summary>
        public string ResourcesUrl { get; set; }

        /// <summary>
        /// The link to EDIT: this row's own, never an inherited one.
        ///
        /// 🔴 Prefilling the edit field from <see cref="ResourcesUrl"/> and posting it back makes
        /// a branch adopt a copy of its Main's link and stop following it, over an edit its author
        /// never made. Null on servers older than the field, where the caller falls back.
        /// </summary>
        public string OwnResourcesUrl { get; set; }

        public int LineCount { get; set; }
        public string FileHash { get; set; }
        public string UpdatedAt { get; set; }
    }

    public class BranchListResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<BranchInfo> Branches { get; set; }
    }

    /// <summary>
    /// Information about a branch (contributor) to a translation
    /// </summary>
    public class BranchInfo
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public int LineCount { get; set; }
        /// <summary>Number of human-translated entries (tag H)</summary>
        public int HumanCount { get; set; }
        /// <summary>Number of AI-translated entries (tag A)</summary>
        public int AiCount { get; set; }
        /// <summary>Number of validated entries (tag V)</summary>
        public int ValidatedCount { get; set; }
        public string UpdatedAt { get; set; }
    }

    public class MergePreviewInitResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        /// <summary>Token for the merge preview session</summary>
        public string Token { get; set; }
        /// <summary>URL to open in browser (may be relative)</summary>
        public string Url { get; set; }
        /// <summary>ISO8601 expiration timestamp</summary>
        public string ExpiresAt { get; set; }
    }

    public class EditSessionInitResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        /// <summary>Mod-side key for content download and SSE stream (never shown to a browser)</summary>
        public string ModKey { get; set; }
        /// <summary>URL to open in browser (may be relative, contains the one-time browser token)</summary>
        public string Url { get; set; }
        /// <summary>ISO8601 expiration timestamp</summary>
        public string ExpiresAt { get; set; }
    }

    public class EditSessionContentResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        /// <summary>Raw JSON of the session translations file</summary>
        public string Content { get; set; }
        /// <summary>
        /// True when the server no longer knows the session (404). Distinguishes
        /// "the session is over" — forget it — from a transient network failure,
        /// which must keep a resumable session on disk.
        /// </summary>
        public bool SessionGone { get; set; }
    }

    public class EditSessionUpdateResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        /// <summary>True when the server no longer knows the session (404)</summary>
        public bool SessionGone { get; set; }
        /// <summary>sha256 of the session file after this push</summary>
        public string ContentHash { get; set; }
        /// <summary>Seconds since the browser last signaled presence (null: never opened)</summary>
        public int? BrowserSeenSecondsAgo { get; set; }
        /// <summary>True when the pagehide beacon fired without a rejoin since</summary>
        public bool BrowserLeft { get; set; }
    }

    #endregion
}
