using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

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
        /// <summary>GitHub "Pre-release" flag (beta builds).</summary>
        public bool IsPrerelease { get; set; }
        /// <summary>Major jump vs the current version (first component; second while still in 0.x).</summary>
        public bool IsMajorUpdate { get; set; }
    }

    /// <summary>
    /// Checks GitHub releases for mod updates.
    /// </summary>
    public static class GitHubUpdateChecker
    {
        // /releases/latest natively excludes pre-releases; the list endpoint includes them
        private const string GITHUB_API_URL = "https://api.github.com/repos/djethino/UnityGameTranslator/releases/latest";
        private const string GITHUB_RELEASES_URL = "https://api.github.com/repos/djethino/UnityGameTranslator/releases?per_page=10";
        private static readonly HttpClient httpClient;

        static GitHubUpdateChecker()
        {
            httpClient = new HttpClient();
            // Versioned like every other call this mod makes: a support report that quotes an
            // update failure should say which build was asking. (No loader here — this client is
            // built before the adapter exists, and GitHub is not where population is counted.)
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"UnityGameTranslator-Mod/{PluginInfo.Version}");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// Check for mod updates on GitHub.
        /// Only call this if online_mode is enabled.
        /// </summary>
        /// <param name="currentVersion">Current mod version (e.g., "0.9.3")</param>
        /// <param name="modLoaderType">Mod loader type for selecting the right asset</param>
        /// <param name="includePrereleases">Also consider GitHub pre-releases (beta builds)</param>
        /// <returns>Update info with download URL if update available</returns>
        public static async Task<ModUpdateInfo> CheckForUpdatesAsync(string currentVersion, string modLoaderType, bool includePrereleases = false)
        {
            try
            {
                TranslatorCore.LogInfo($"[GitHubUpdate] Checking for updates... Current: v{currentVersion}, Loader: {modLoaderType}, Prereleases: {includePrereleases}");

                var response = await httpClient.GetAsync(includePrereleases ? GITHUB_RELEASES_URL : GITHUB_API_URL);

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
                JToken release;
                if (includePrereleases)
                {
                    // Newest first; skip drafts, keep the most recent release or pre-release
                    var releases = JArray.Parse(json);
                    release = null;
                    foreach (var candidate in releases)
                    {
                        if (candidate["draft"]?.ToObject<bool>() == true) continue;
                        release = candidate;
                        break;
                    }
                    if (release == null)
                    {
                        return new ModUpdateInfo
                        {
                            Success = false,
                            Error = "No releases found",
                            CurrentVersion = currentVersion
                        };
                    }
                }
                else
                {
                    release = ApiClient.ParseJsonSafe(json);
                }

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
                    PublishedAt = publishedAt,
                    IsPrerelease = release["prerelease"]?.ToObject<bool>() ?? false,
                    IsMajorUpdate = hasUpdate && IsMajorJump(currentVersion, latestVersion)
                };
            }
            catch (HttpRequestException ex)
            {
                // ⚠ The raw sentence stays in the log — it names the mechanism, which is what a
                // maintainer needs. What goes on screen names the CAUSE, which is what the player
                // can act on. See Connectivity.
                TranslatorCore.LogWarning($"[GitHubUpdate] Network error: {ex.Message}");
                return new ModUpdateInfo
                {
                    Success = false,
                    Error = Connectivity.Describe(ex),
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
                    Error = Connectivity.Describe(ex),
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
                case "MelonLoader-Mono":
                    return "MelonLoader-Mono";
                case "MelonLoader-IL2CPP":
                    return "MelonLoader-IL2CPP";
                default:
                    return modLoaderType;
            }
        }

        /// <summary>
        /// True when the jump between two versions is "major" for notification purposes:
        /// the first component changed — or the second one while we are still in 0.x
        /// (SemVer treats 0.MINOR as the breaking-change slot).
        /// </summary>
        public static bool IsMajorJump(string current, string latest)
        {
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(latest)) return false;

            var p1 = current.TrimStart('v').Split('.');
            var p2 = latest.TrimStart('v').Split('.');

            int major1 = 0, major2 = 0, minor1 = 0, minor2 = 0;
            if (p1.Length > 0) int.TryParse(p1[0].Split('-')[0], out major1);
            if (p2.Length > 0) int.TryParse(p2[0].Split('-')[0], out major2);
            if (p1.Length > 1) int.TryParse(p1[1].Split('-')[0], out minor1);
            if (p2.Length > 1) int.TryParse(p2[1].Split('-')[0], out minor2);

            if (major1 != major2) return true;
            return major2 == 0 && minor1 != minor2;
        }

        /// <summary>
        /// Compare two semantic version strings.
        /// Returns: -1 if v1 &lt; v2, 0 if equal, 1 if v1 &gt; v2
        ///
        /// ⚠ The rules live in UnityGameTranslator.Common now, and this stays only as the name
        /// callers here already use. They were written twice — once here, once in the installer,
        /// the second copied from the first and marked as a mirror. Both programs read the same
        /// tags from the same publisher and have to reach the same verdict: one of them deciding
        /// that 0.9.66 comes before 0.9.9 while the other says the opposite shows up as "an update
        /// it keeps offering and never applies", with nothing on screen to say which is wrong.
        /// </summary>
        public static int CompareVersions(string v1, string v2) =>
            UnityGameTranslator.Common.Versions.Compare(v1, v2);
    }
}
