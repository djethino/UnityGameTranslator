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
        private const string DefaultBaseUrl = "https://unitygametranslator.asymptomatikgames.com/api/v1";

        static ApiClient()
        {
            client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "UnityGameTranslator/1.0");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");
        }

        /// <summary>
        /// Set the API token for authenticated requests
        /// </summary>
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
                var data = JObject.Parse(json);

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
                var data = JObject.Parse(json);

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
                VoteCount = t["vote_count"]?.Value<int>() ?? 0,
                DownloadCount = t["download_count"]?.Value<int>() ?? 0,
                FileHash = t["file_hash"]?.Value<string>(),
                UpdatedAt = t["updated_at"]?.Value<string>()
            };
        }

        #endregion

        #region Translation Check

        /// <summary>
        /// Check if a translation has been updated
        /// </summary>
        public static async Task<TranslationCheckResult> CheckUpdate(int translationId, string currentHash)
        {
            try
            {
                // Add debug=1 to get hash comparison info from server
                var request = new HttpRequestMessage(HttpMethod.Get, $"{DefaultBaseUrl}/translations/{translationId}/check?hash={Uri.EscapeDataString(currentHash ?? "")}&debug=1");

                if (!string.IsNullOrEmpty(currentHash))
                {
                    request.Headers.Add("If-None-Match", $"\"{currentHash}\"");
                }

                var response = await client.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    return new TranslationCheckResult { Success = true, HasUpdate = false };
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new TranslationCheckResult { Success = false, Error = $"HTTP {response.StatusCode}" };
                }

                string json = await response.Content.ReadAsStringAsync();
                var data = JObject.Parse(json);

                // Log debug info if present
                var debug = data["debug"];
                if (debug != null)
                {
                    TranslatorCore.LogInfo($"[HashDebug] Client hash:   {debug["client_hash"]}");
                    TranslatorCore.LogInfo($"[HashDebug] Stored hash:   {debug["stored_hash"]}");
                    TranslatorCore.LogInfo($"[HashDebug] Computed hash: {debug["computed_hash"]}");
                    TranslatorCore.LogInfo($"[HashDebug] Stored==Computed: {debug["stored_matches_computed"]}");
                    TranslatorCore.LogInfo($"[HashDebug] Server JSON preview: {debug["json_preview"]?.ToString()?.Substring(0, Math.Min(100, debug["json_preview"]?.ToString()?.Length ?? 0))}...");
                    TranslatorCore.LogInfo($"[HashDebug] Server entry count: {debug["entry_count"]}, length: {debug["json_length"]}");
                }

                return new TranslationCheckResult
                {
                    Success = true,
                    HasUpdate = data["has_update"]?.Value<bool>() ?? false,
                    FileHash = data["file_hash"]?.Value<string>(),
                    LineCount = data["line_count"]?.Value<int>() ?? 0,
                    VoteCount = data["vote_count"]?.Value<int>() ?? 0,
                    UpdatedAt = data["updated_at"]?.Value<string>()
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Check error: {e.Message}");
                return new TranslationCheckResult { Success = false, Error = e.Message };
            }
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
                    using (var stream = new MemoryStream(rawContent))
                    using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                    using (var reader = new StreamReader(gzip, Encoding.UTF8))
                    {
                        jsonContent = await reader.ReadToEndAsync();
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
                TranslatorCore.LogInfo($"[ApiClient] CheckUuid response: {json}");
                var data = JObject.Parse(json);

                var result = new UuidCheckResult
                {
                    Success = true,
                    Exists = data["exists"]?.Value<bool>() ?? false,
                    IsOwner = data["is_owner"]?.Value<bool>() ?? false,
                    MainUsername = data["main_user"]?.Value<string>(),
                    BranchesCount = data["branches_count"]?.Value<int>() ?? 0
                };

                // Parse role with .NET Standard 2.0 compatible switch
                string roleStr = data["role"]?.Value<string>();
                switch (roleStr)
                {
                    case "main":
                        result.Role = TranslationRole.Main;
                        break;
                    case "branch":
                        result.Role = TranslationRole.Branch;
                        break;
                    default:
                        result.Role = TranslationRole.None;
                        break;
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
                        Notes = t["notes"]?.Value<string>(),
                        LineCount = t["line_count"]?.Value<int>() ?? 0,
                        FileHash = t["file_hash"]?.Value<string>(),
                        UpdatedAt = t["updated_at"]?.Value<string>()
                    };
                }

                // Parse original info if FORK
                if (result.Exists && !result.IsOwner && data["original"] != null)
                {
                    var o = data["original"];
                    result.OriginalTranslation = new UuidCheckTranslationInfo
                    {
                        Id = o["id"]?.Value<int>() ?? 0,
                        Uploader = o["uploader"]?.Value<string>(),
                        SourceLanguage = o["source_language"]?.Value<string>(),
                        TargetLanguage = o["target_language"]?.Value<string>(),
                        Type = o["type"]?.Value<string>(),
                        LineCount = o["line_count"]?.Value<int>() ?? 0,
                        UpdatedAt = o["updated_at"]?.Value<string>()
                    };
                }

                return result;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] UUID check error: {e.Message}");
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
                var data = JObject.Parse(json);

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
                            Username = b["username"]?.Value<string>(),
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

            // Size limit (100MB should be more than enough for any translation file)
            const int maxSize = 100 * 1024 * 1024;
            if (json.Length > maxSize)
            {
                error = $"Content too large ({json.Length} bytes)";
                return false;
            }

            try
            {
                // Parse with depth limit to prevent stack overflow attacks
                var settings = new JsonSerializerSettings
                {
                    MaxDepth = 10 // Translation files should be flat key-value
                };

                var parsed = JObject.Parse(json);

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
                    // - An object (new format): "key": {"v": "value", "t": "A/H/V"}
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
                        // "v" must be string, "t" is optional but must be string if present
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
                var data = JObject.Parse(json);

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
                var data = JObject.Parse(json);

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
                var data = JObject.Parse(json);

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

        /// <summary>
        /// Poll for device flow authorization status.
        /// Returns authorization_pending until user authorizes.
        /// </summary>
        public static async Task<DeviceFlowPollResult> PollDeviceFlow(string deviceCode)
        {
            try
            {
                var content = new StringContent(
                    JsonConvert.SerializeObject(new { device_code = deviceCode }),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await client.PostAsync($"{DefaultBaseUrl}/auth/device/poll", content);

                string json = await response.Content.ReadAsStringAsync();
                var data = JObject.Parse(json);

                if (!response.IsSuccessStatusCode)
                {
                    string error = data["error"]?.Value<string>();
                    return new DeviceFlowPollResult
                    {
                        Success = false,
                        Pending = error == "authorization_pending",
                        Error = data["error_description"]?.Value<string>() ?? error
                    };
                }

                // Success - we got the token
                string token = data["access_token"]?.Value<string>();
                var user = data["user"];

                return new DeviceFlowPollResult
                {
                    Success = true,
                    AccessToken = token,
                    UserName = user?["name"]?.Value<string>()
                };
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ApiClient] Device flow poll error: {e.Message}");
                return new DeviceFlowPollResult { Success = false, Error = e.Message };
            }
        }

        #endregion

        #region Upload

        /// <summary>
        /// Upload a translation to the website.
        /// Requires authentication (SetAuthToken must be called first).
        /// </summary>
        public static async Task<UploadResult> UploadTranslation(UploadRequest request)
        {
            try
            {
                var payload = new
                {
                    steam_id = request.SteamId,
                    game_name = request.GameName,
                    source_language = request.SourceLanguage,
                    target_language = request.TargetLanguage,
                    type = request.Type,
                    status = request.Status,
                    content = request.Content,
                    notes = request.Notes
                };

                var content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await client.PostAsync($"{DefaultBaseUrl}/translations", content);

                string json = await response.Content.ReadAsStringAsync();
                var data = JObject.Parse(json);

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
                return new UploadResult
                {
                    Success = true,
                    TranslationId = translation?["id"]?.Value<int>() ?? 0,
                    FileHash = translation?["file_hash"]?.Value<string>(),
                    LineCount = translation?["line_count"]?.Value<int>() ?? 0,
                    IsFork = translation?["is_fork"]?.Value<bool>() ?? false,
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
    }

    #region Result Classes

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
        public int VoteCount { get; set; }
        public int DownloadCount { get; set; }
        public string FileHash { get; set; }
        public string UpdatedAt { get; set; }

        /// <summary>
        /// Get website URL for this translation
        /// </summary>
        public string GetWebUrl()
        {
            return $"https://unitygametranslator.asymptomatikgames.com/games/{GameSlug}";
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

    public class DeviceFlowPollResult
    {
        public bool Success { get; set; }
        public bool Pending { get; set; }
        public string Error { get; set; }
        public string AccessToken { get; set; }
        public string UserName { get; set; }
    }

    public class UploadRequest
    {
        public string SteamId { get; set; }
        public string GameName { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        public string Type { get; set; }
        public string Status { get; set; }
        public string Content { get; set; }
        public string Notes { get; set; }
    }

    public class UploadResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int TranslationId { get; set; }
        public string FileHash { get; set; }
        public int LineCount { get; set; }
        public bool IsFork { get; set; }
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

    #endregion
}
