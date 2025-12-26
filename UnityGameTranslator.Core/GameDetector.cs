using System;
using System.IO;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Detects the current game using various methods:
    /// 1. steam_appid.txt in game folder
    /// 2. Unity Application.productName
    /// 3. Folder name as fallback
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
                            TranslatorCore.LogInfo($"[GameDetector] Found steam_appid.txt: {content}");
                        }
                    }
                    catch (Exception e)
                    {
                        TranslatorCore.LogWarning($"[GameDetector] Error reading steam_appid.txt: {e.Message}");
                    }
                }
            }

            // Method 2: Application.productName
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
    }
}
