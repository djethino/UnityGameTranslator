using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Detects the current game using various methods:
    /// 1. steam_appid.txt in game folder
    /// 2. Steam appmanifest files (scan steamapps folder)
    /// 3. Unity Application.productName
    /// 4. Folder name as fallback
    /// </summary>
    public static class GameDetector
    {
        private static GameInfo cachedGame = null;
        private static bool hasDetected = false;

        /// <summary>
        /// Detect the current game. Results are cached.
        /// </summary>
        public static GameInfo DetectGame()
        {
            if (hasDetected)
                return cachedGame;

            hasDetected = true;
            cachedGame = DetectGameInternal();

            if (cachedGame != null)
            {
                TranslatorCore.LogInfo($"[GameDetector] Detected: {cachedGame.name} (Steam ID: {cachedGame.steam_id ?? "none"})");
            }
            else
            {
                TranslatorCore.LogWarning("[GameDetector] Could not detect game");
            }

            return cachedGame;
        }

        private static GameInfo DetectGameInternal()
        {
            var info = new GameInfo();

            // Get game folder (data path parent)
            string dataPath = null;
            string gameFolderPath = null;

            try
            {
                dataPath = Application.dataPath;
                gameFolderPath = Path.GetDirectoryName(dataPath);
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[GameDetector] Could not get Application.dataPath: {e.Message}");
            }

            // Method 1: Try steam_appid.txt
            if (!string.IsNullOrEmpty(gameFolderPath))
            {
                string steamAppIdPath = Path.Combine(gameFolderPath, "steam_appid.txt");
                if (File.Exists(steamAppIdPath))
                {
                    try
                    {
                        string content = File.ReadAllText(steamAppIdPath).Trim();
                        // Validate it's a number
                        if (!string.IsNullOrEmpty(content) && long.TryParse(content, out _))
                        {
                            info.steam_id = content;
                            info.detection_method = "steam_appid.txt";
                            TranslatorCore.LogInfo($"[GameDetector] Found steam_appid.txt: {content}");
                        }
                    }
                    catch (Exception e)
                    {
                        TranslatorCore.LogWarning($"[GameDetector] Error reading steam_appid.txt: {e.Message}");
                    }
                }
            }

            // Method 2: Try Steam appmanifest files (if no steam_appid.txt found)
            if (string.IsNullOrEmpty(info.steam_id) && !string.IsNullOrEmpty(gameFolderPath))
            {
                string steamId = TryGetSteamIdFromAppManifest(gameFolderPath);
                if (!string.IsNullOrEmpty(steamId))
                {
                    info.steam_id = steamId;
                    info.detection_method = "appmanifest";
                    TranslatorCore.LogInfo($"[GameDetector] Found Steam ID from appmanifest: {steamId}");
                }
            }

            // Method 3: Application.productName
            try
            {
                string productName = Application.productName;
                if (!string.IsNullOrEmpty(productName) && productName != "DefaultCompany")
                {
                    info.name = productName;
                    TranslatorCore.LogInfo($"[GameDetector] Application.productName: {productName}");
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[GameDetector] Could not get Application.productName: {e.Message}");
            }

            // Method 3: Folder name as fallback
            if (!string.IsNullOrEmpty(gameFolderPath))
            {
                info.folder_name = Path.GetFileName(gameFolderPath);

                // Use folder name as name if productName wasn't available
                if (string.IsNullOrEmpty(info.name))
                {
                    info.name = info.folder_name;
                    TranslatorCore.LogInfo($"[GameDetector] Using folder name: {info.folder_name}");
                }
            }

            // Return null if we couldn't detect anything useful
            if (string.IsNullOrEmpty(info.name) && string.IsNullOrEmpty(info.steam_id))
            {
                return null;
            }

            return info;
        }

        /// <summary>
        /// Force re-detection (e.g., if game info changed)
        /// </summary>
        public static void Reset()
        {
            hasDetected = false;
            cachedGame = null;
        }

        /// <summary>
        /// Get Steam Store URL for the detected game
        /// </summary>
        public static string GetSteamStoreUrl()
        {
            var game = DetectGame();
            if (game?.steam_id != null)
            {
                return $"https://store.steampowered.com/app/{game.steam_id}";
            }
            return null;
        }

        /// <summary>
        /// Try to find Steam ID by scanning appmanifest files in the steamapps folder.
        /// Works by going up from the game folder to find steamapps, then scanning manifests.
        /// </summary>
        private static string TryGetSteamIdFromAppManifest(string gameFolderPath)
        {
            try
            {
                // Resolve any symlinks/junctions
                gameFolderPath = Path.GetFullPath(gameFolderPath);
                string gameFolderName = Path.GetFileName(gameFolderPath);

                // Check if parent folder is "common" (Steam structure: steamapps/common/GameFolder)
                string commonFolder = Path.GetDirectoryName(gameFolderPath);
                if (string.IsNullOrEmpty(commonFolder))
                    return null;

                string commonFolderName = Path.GetFileName(commonFolder);
                if (!string.Equals(commonFolderName, "common", StringComparison.OrdinalIgnoreCase))
                {
                    // Not in a Steam library structure
                    return null;
                }

                // Get steamapps folder (parent of common)
                string steamappsFolder = Path.GetDirectoryName(commonFolder);
                if (string.IsNullOrEmpty(steamappsFolder) || !Directory.Exists(steamappsFolder))
                    return null;

                TranslatorCore.LogInfo($"[GameDetector] Scanning appmanifest files in: {steamappsFolder}");

                // Scan appmanifest_*.acf files
                string[] manifestFiles;
                try
                {
                    manifestFiles = Directory.GetFiles(steamappsFolder, "appmanifest_*.acf");
                }
                catch (Exception e)
                {
                    TranslatorCore.LogWarning($"[GameDetector] Cannot access steamapps folder: {e.Message}");
                    return null;
                }

                // Regex to extract installdir and appid from Valve KeyValues format
                // Format: "key"		"value" (with tabs between)
                var installdirRegex = new Regex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                var appidRegex = new Regex("\"appid\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);

                foreach (string manifestPath in manifestFiles)
                {
                    try
                    {
                        string content = File.ReadAllText(manifestPath);

                        // Check if installdir matches our game folder name
                        var installdirMatch = installdirRegex.Match(content);
                        if (!installdirMatch.Success)
                            continue;

                        string installdir = installdirMatch.Groups[1].Value;
                        if (!string.Equals(installdir, gameFolderName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Found matching manifest - extract appid
                        var appidMatch = appidRegex.Match(content);
                        if (appidMatch.Success)
                        {
                            string appid = appidMatch.Groups[1].Value;
                            // Validate it's a number
                            if (long.TryParse(appid, out _))
                            {
                                TranslatorCore.LogInfo($"[GameDetector] Matched appmanifest: {Path.GetFileName(manifestPath)} -> appid {appid}");
                                return appid;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        // Skip unreadable manifest files
                        TranslatorCore.LogWarning($"[GameDetector] Error reading {Path.GetFileName(manifestPath)}: {e.Message}");
                    }
                }

                TranslatorCore.LogInfo($"[GameDetector] No matching appmanifest found for folder: {gameFolderName}");
                return null;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[GameDetector] Error scanning appmanifest files: {e.Message}");
                return null;
            }
        }
    }
}
