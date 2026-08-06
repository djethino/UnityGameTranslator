using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
            public AuthRejectionHandler(HttpMessageHandler inner) : base(inner) { }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                // Read BEFORE sending: evaluating this after the round trip made it
                // race with SetAuthToken. A request that left before the saved token
                // was restored came back 401, by then the header existed, and the
                // handler concluded the token had been revoked — signing the player
                // out on startup because of a call that never carried a token.
                bool sentToken = request.Headers.Authorization != null
                    || client.DefaultRequestHeaders.Contains("Authorization");

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
                AllowAutoRedirect = false
            };
            client = new HttpClient(new AuthRejectionHandler(handler));
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "UnityGameTranslator/1.0");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");

            // Dedicated SSE client: no timeout (long-lived streams), no gzip (breaks streaming)
            var sseHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            sseClient = new HttpClient(sseHandler);
            sseClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            sseClient.DefaultRequestHeaders.Add("User-Agent", "UnityGameTranslator/1.0");
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
        /// </summary>
        public static string GetSyncSseUrl(string uuid, string localHash)
        {
            var url = $"{SseBaseUrl}/sync/stream?uuid={Uri.EscapeDataString(uuid)}";
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
        public static async Task<TranslationCheckResult> CheckPublicUpdate(int siteId, string localHash)
        {
            try
            {
                var url = $"{DefaultBaseUrl}/translations/{siteId}/check";
                if (!string.IsNullOrEmpty(localHash))
                {
                    url += $"?hash={Uri.EscapeDataString(localHash)}";
                }

                var response = await client.GetAsync(url);

                // Nothing changed since our hash — the server saved itself the body
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    return new TranslationCheckResult { Success = true, HasUpdate = false };
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
                return new TranslationCheckResult { Success = false, Error = e.Message };
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
                return new ModNotificationsResult { Success = false, Error = e.Message };
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
                return new TranslationSearchResult { Success = false, Error = e.Message };
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
                return new TranslationSearchResult { Success = false, Error = e.Message };
            }
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
                ContentUpdatedAt = t["content_updated_at"]?.Value<string>()
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

                // Handle gzip encoding
                byte[] rawContent = await response.Content.ReadAsByteArrayAsync();
                string jsonContent;

                if (response.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    // Bounded decompression: a gzip bomb (tiny compressed, huge decompressed)
                    // must be rejected during inflation, not after materializing the result
                    using (var stream = new MemoryStream(rawContent))
                    using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await gzip.ReadAsync(buffer, 0, buffer.Length)) > 0)
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
                }
                else
                {
                    jsonContent = Encoding.UTF8.GetString(rawContent);
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
                return new TranslationDownloadResult { Success = false, Error = e.Message };
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
                    // Use ToObject<int?>() to handle explicit JSON null values
                    BranchesCount = data["branches_count"]?.ToObject<int?>() ?? 0
                };

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
                        Notes = t["notes"]?.Value<string>(),
                        ResourcesUrl = t["resources_url"]?.Value<string>(),
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
                return new UuidCheckResult { Success = false, Error = $"Network error: {httpEx.Message}" };
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
                return new UuidCheckResult { Success = false, Error = e.Message };
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
                return new BranchListResult { Success = false, Error = e.Message };
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
                return new GameSearchResult { Success = false, Error = e.Message };
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
                return new GameSearchResult { Success = false, Error = e.Message };
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
        /// Initiate Device Flow authentication.
        /// Returns a device code and user code to display.
        /// </summary>
        public static async Task<DeviceFlowInitResult> InitiateDeviceFlow()
        {
            try
            {
                var response = await client.PostAsync($"{DefaultBaseUrl}/auth/device", null);

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
                return new DeviceFlowInitResult { Success = false, Error = e.Message };
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
                    resources_url = request.ResourcesUrl
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
                return new UploadResult { Success = false, Error = e.Message };
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
                return new MergePreviewInitResult { Success = false, Error = e.Message };
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
                return new TranslationDownloadResult { Success = false, Error = e.Message };
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
                return new EditSessionInitResult { Success = false, Error = e.Message };
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
                return new EditSessionUpdateResult { Success = false, Error = e.Message };
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
                return new EditSessionContentResult { Success = false, Error = e.Message };
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
                return new VoteResult { Success = false, Error = e.Message };
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
        public float QualityScore => TranslationQuality.ComputeScore(HumanCount, ValidatedCount, AiCount);

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
        /// <summary>Number of branches contributing to this UUID (if this is Main)</summary>
        public int BranchesCount { get; set; }
        public UuidCheckTranslationInfo ExistingTranslation { get; set; } // For UPDATE
        public UuidCheckTranslationInfo OriginalTranslation { get; set; } // For FORK
    }

    public class UuidCheckTranslationInfo
    {
        public int Id { get; set; }
        public string Uploader { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        public string Type { get; set; }
        public string Notes { get; set; }
        public string ResourcesUrl { get; set; }
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
