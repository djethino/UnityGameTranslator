using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Result of a GitHub release check.
    /// </summary>
    public class ModUpdateInfo
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseUrl { get; set; }
        public string DownloadUrl { get; set; }
        public string ReleaseNotes { get; set; }
        public DateTime? PublishedAt { get; set; }
    }

    /// <summary>
    /// Checks GitHub releases for mod updates.
    /// </summary>
    public static class GitHubUpdateChecker
    {
        private const string GITHUB_API_URL = "https://api.github.com/repos/djethino/UnityGameTranslator/releases/latest";
        private static readonly HttpClient httpClient;

        static GitHubUpdateChecker()
        {
            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "UnityGameTranslator-Mod");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// Check for mod updates on GitHub.
        /// Only call this if online_mode is enabled.
        /// </summary>
        /// <param name="currentVersion">Current mod version (e.g., "0.9.3")</param>
        /// <param name="modLoaderType">Mod loader type for selecting the right asset</param>
        /// <returns>Update info with download URL if update available</returns>
        public static async Task<ModUpdateInfo> CheckForUpdatesAsync(string currentVersion, string modLoaderType)
        {
            try
            {
                TranslatorCore.LogInfo($"[GitHubUpdate] Checking for updates... Current: v{currentVersion}, Loader: {modLoaderType}");

                var response = await httpClient.GetAsync(GITHUB_API_URL);

                if (!response.IsSuccessStatusCode)
                {
                    return new ModUpdateInfo
                    {
                        Success = false,
                        Error = $"GitHub API returned {(int)response.StatusCode}: {response.ReasonPhrase}",
                        CurrentVersion = currentVersion
                    };
                }

                var json = await response.Content.ReadAsStringAsync();
                var release = JObject.Parse(json);

                var tagName = release["tag_name"]?.ToString();
                var htmlUrl = release["html_url"]?.ToString();
                var body = release["body"]?.ToString();
                var publishedAt = release["published_at"]?.ToObject<DateTime>();

                // Parse version from tag (remove 'v' prefix if present)
                var latestVersion = tagName?.TrimStart('v') ?? "";

                TranslatorCore.LogInfo($"[GitHubUpdate] Latest release: v{latestVersion}");

                // Compare versions
                bool hasUpdate = CompareVersions(currentVersion, latestVersion) < 0;

                // Find download URL for the specific mod loader
                string downloadUrl = null;
                var assets = release["assets"] as JArray;
                if (assets != null && hasUpdate)
                {
                    downloadUrl = FindAssetUrl(assets, modLoaderType);
                    TranslatorCore.LogInfo($"[GitHubUpdate] Download URL for {modLoaderType}: {downloadUrl ?? "not found"}");
                }

                return new ModUpdateInfo
                {
                    Success = true,
                    HasUpdate = hasUpdate,
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    ReleaseUrl = htmlUrl,
                    DownloadUrl = downloadUrl,
                    ReleaseNotes = body,
                    PublishedAt = publishedAt
                };
            }
            catch (HttpRequestException ex)
            {
                TranslatorCore.LogWarning($"[GitHubUpdate] Network error: {ex.Message}");
                return new ModUpdateInfo
                {
                    Success = false,
                    Error = $"Network error: {ex.Message}",
                    CurrentVersion = currentVersion
                };
            }
            catch (TaskCanceledException)
            {
                TranslatorCore.LogWarning("[GitHubUpdate] Request timed out");
                return new ModUpdateInfo
                {
                    Success = false,
                    Error = "Request timed out",
                    CurrentVersion = currentVersion
                };
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[GitHubUpdate] Error: {ex.Message}");
                return new ModUpdateInfo
                {
                    Success = false,
                    Error = ex.Message,
                    CurrentVersion = currentVersion
                };
            }
        }

        /// <summary>
        /// Find the download URL for a specific mod loader type.
        /// Asset naming convention: UnityGameTranslator-{ModLoaderType}-v{Version}.zip
        /// </summary>
        private static string FindAssetUrl(JArray assets, string modLoaderType)
        {
            // Map mod loader type to asset name pattern
            string assetPattern = GetAssetPattern(modLoaderType);

            foreach (var asset in assets)
            {
                var name = asset["name"]?.ToString() ?? "";
                if (name.Contains(assetPattern) && name.EndsWith(".zip"))
                {
                    return asset["browser_download_url"]?.ToString();
                }
            }

            // Fallback: try to find any matching zip
            foreach (var asset in assets)
            {
                var name = asset["name"]?.ToString() ?? "";
                if (name.ToLower().Contains(modLoaderType.ToLower().Replace("-", "")) && name.EndsWith(".zip"))
                {
                    return asset["browser_download_url"]?.ToString();
                }
            }

            return null;
        }

        /// <summary>
        /// Get the asset name pattern for a mod loader type.
        /// </summary>
        private static string GetAssetPattern(string modLoaderType)
        {
            switch (modLoaderType)
            {
                case "BepInEx5":
                    return "BepInEx5";
                case "BepInEx6-Mono":
                    return "BepInEx6-Mono";
                case "BepInEx6-IL2CPP":
                    return "BepInEx6-IL2CPP";
                case "MelonLoader":
                    return "MelonLoader";
                default:
                    return modLoaderType;
            }
        }

        /// <summary>
        /// Compare two semantic version strings.
        /// Returns: -1 if v1 < v2, 0 if equal, 1 if v1 > v2
        /// </summary>
        public static int CompareVersions(string v1, string v2)
        {
            if (string.IsNullOrEmpty(v1) && string.IsNullOrEmpty(v2)) return 0;
            if (string.IsNullOrEmpty(v1)) return -1;
            if (string.IsNullOrEmpty(v2)) return 1;

            // Remove 'v' prefix if present
            v1 = v1.TrimStart('v');
            v2 = v2.TrimStart('v');

            var parts1 = v1.Split('.');
            var parts2 = v2.Split('.');

            int maxLength = Math.Max(parts1.Length, parts2.Length);

            for (int i = 0; i < maxLength; i++)
            {
                int num1 = 0;
                int num2 = 0;

                if (i < parts1.Length)
                {
                    // Handle pre-release suffixes (e.g., "3-beta")
                    var part1 = parts1[i].Split('-')[0];
                    int.TryParse(part1, out num1);
                }

                if (i < parts2.Length)
                {
                    var part2 = parts2[i].Split('-')[0];
                    int.TryParse(part2, out num2);
                }

                if (num1 < num2) return -1;
                if (num1 > num2) return 1;
            }

            // If numeric parts are equal, check for pre-release (version without suffix > version with suffix)
            bool hasPrerelease1 = v1.Contains("-");
            bool hasPrerelease2 = v2.Contains("-");

            if (hasPrerelease1 && !hasPrerelease2) return -1;
            if (!hasPrerelease1 && hasPrerelease2) return 1;

            return 0;
        }
    }
}
