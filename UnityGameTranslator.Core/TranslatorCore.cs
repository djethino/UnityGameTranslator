using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Interface for mod loader abstraction (logging, paths, etc.)
    /// </summary>
    public interface IModLoaderAdapter
    {
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
        string GetPluginFolder();

        /// <summary>
        /// Mod loader type identifier for GitHub release asset selection.
        /// Values: "BepInEx5", "BepInEx6-Mono", "BepInEx6-IL2CPP", "MelonLoader-Mono", "MelonLoader-IL2CPP"
        /// </summary>
        string ModLoaderType { get; }

        /// <summary>
        /// Whether this mod loader is running on IL2CPP (vs Mono).
        /// Used to determine which UniverseLib variant to use and which scanning method to apply.
        /// </summary>
        bool IsIL2CPP { get; }
    }

    /// <summary>
    /// Main translation engine - shared across all mod loaders
    /// </summary>
    public class TranslatorCore
    {
        public static TranslatorCore Instance { get; private set; }
        public static IModLoaderAdapter Adapter { get; private set; }
        public static ModConfig Config { get; private set; } = new ModConfig();
        public static Dictionary<string, TranslationEntry> TranslationCache { get; private set; } = new Dictionary<string, TranslationEntry>();
        public static List<PatternEntry> PatternEntries { get; private set; } = new List<PatternEntry>();
        public static string CachePath { get; private set; }
        public static string ConfigPath { get; private set; }
        public static string ModFolder { get; private set; }
        public static bool DebugMode { get; private set; } = false;
        public static string FileUuid { get; private set; }

        /// <summary>
        /// Per-font settings for translation and fallback.
        /// Stored in translations.json as _fonts for sharing.
        /// Key = font name (case-insensitive), Value = settings (enabled, fallback)
        /// </summary>
        public static Dictionary<string, FontSettings> FontSettingsMap { get; set; } = new Dictionary<string, FontSettings>(StringComparer.OrdinalIgnoreCase);

        public static GameInfo CurrentGame { get; internal set; }

        /// <summary>
        /// Server state for current translation (populated via check-uuid, not persisted)
        /// </summary>
        public static ServerTranslationState ServerState { get; set; }

        /// <summary>
        /// Context for pending fork operation. Set before CreateFork() with source translation info.
        /// Used by UploadPanel to skip UploadSetupPanel since languages/game are already known.
        /// Cleared after successful upload.
        /// </summary>
        public static ForkContext PendingFork { get; set; }

        public static int LocalChangesCount { get; private set; } = 0;
        public static Dictionary<string, TranslationEntry> AncestorCache { get; private set; } = new Dictionary<string, TranslationEntry>();

        /// <summary>
        /// Hash of the translation at last sync (download or upload).
        /// Used to detect if server has changed since our last sync.
        /// Stored in translations.json as _source.hash
        /// </summary>
        public static string LastSyncedHash { get; set; } = null;

        /// <summary>
        /// If true, UniverseLib won't override the game's EventSystem.
        /// Enable this if the game's UI animations or navigation don't work with the mod.
        /// Stored in translations.json as _settings.disable_eventsystem_override
        /// Requires game restart to take effect.
        /// </summary>
        public static bool DisableEventSystemOverride { get; set; } = false;

        /// <summary>
        /// Returns true if source/target languages are locked (translation exists on server).
        /// Once a translation is uploaded, languages cannot be changed to maintain consistency.
        /// </summary>
        public static bool AreLanguagesLocked => ServerState != null && ServerState.Exists;

        /// <summary>
        /// Returns true if a remote translation's UUID matches our local FileUuid.
        /// Used to highlight translations from the same lineage in the community list.
        /// </summary>
        public static bool IsUuidMatch(string remoteUuid)
        {
            return !string.IsNullOrEmpty(remoteUuid) &&
                   !string.IsNullOrEmpty(FileUuid) &&
                   remoteUuid == FileUuid;
        }

        private static float lastSaveTime = 0f;
        private static int translatedCount = 0;
        private static int ollamaCount = 0;
        private static int cacheHitCount = 0;
        private static Dictionary<int, string> lastSeenText = new Dictionary<int, string>();
        private static HashSet<string> pendingTranslations = new HashSet<string>();
        private static Queue<string> translationQueue = new Queue<string>();
        // Note: Own UI detection now happens at processing time using IsOwnUITranslatable(component)
        // instead of string-based tracking which caused false positives when game text matched mod UI text
        private static object lockObj = new object();
        private static bool cacheModified = false;
        private static HttpClient httpClient;
        private static int skippedTargetLang = 0;
        private static int skippedAlreadyTranslated = 0;
        private static bool _enableTranslationsLogOnce = true; // Log once when translations disabled

        // Reverse cache: all translated values (to detect already-translated text)
        private static HashSet<string> translatedTexts = new HashSet<string>();

        // Component tracking: components waiting for a translation (using object to avoid Unity dependencies)
        private static Dictionary<string, List<object>> pendingComponents = new Dictionary<string, List<object>>();

        // Pattern match failure cache (texts that don't match any pattern)
        private static HashSet<string> patternMatchFailures = new HashSet<string>();

        // Callback for updating components when translation completes
        public static Action<string, string, List<object>> OnTranslationComplete;

        // Queue status for UI overlay
        private static bool isTranslating = false;
        private static string currentlyTranslating = null;
        public static int QueueCount { get { lock (lockObj) { return translationQueue.Count; } } }
        public static bool IsTranslating => isTranslating;
        public static string CurrentText => currentlyTranslating;

        // Own UI component tracking (mod interface)
        private static HashSet<int> ownUIExcluded = new HashSet<int>();      // Never translate (title, lang codes, config values)
        private static HashSet<int> ownUITranslatable = new HashSet<int>();  // Translate with UI-specific prompt
        private static HashSet<int> ownUIPanelRoots = new HashSet<int>();    // Root GameObjects of our panels (for hierarchy check)

        // User exclusions (chat windows, player names, etc.) - stored in translations.json as _exclusions
        private static List<string> userExclusions = new List<string>();
        private static Dictionary<int, bool> userExclusionCache = new Dictionary<int, bool>();

        /// <summary>
        /// Current user exclusion patterns. Read-only access for UI.
        /// </summary>
        public static IReadOnlyList<string> UserExclusions => userExclusions;

        // Panel construction mode: when true, all translations are skipped
        // This prevents texts created during panel construction from being queued before we can register them
        private static int _constructionModeCount = 0;
        private static object _constructionModeLock = new object();

        /// <summary>
        /// Enter panel construction mode. While active, all translations are skipped.
        /// Call this before creating panel UI elements. Supports nested calls (reference counted).
        /// </summary>
        public static void EnterConstructionMode()
        {
            lock (_constructionModeLock)
            {
                _constructionModeCount++;
            }
        }

        /// <summary>
        /// Exit panel construction mode. Decrements the reference count.
        /// </summary>
        public static void ExitConstructionMode()
        {
            lock (_constructionModeLock)
            {
                if (_constructionModeCount > 0)
                    _constructionModeCount--;
            }
        }

        /// <summary>
        /// Returns true if we're currently in panel construction mode.
        /// </summary>
        public static bool IsInConstructionMode
        {
            get
            {
                lock (_constructionModeLock)
                {
                    return _constructionModeCount > 0;
                }
            }
        }

        /// <summary>
        /// Register a component to be excluded from translation (mod title, language codes, config values).
        /// </summary>
        public static void RegisterExcluded(UnityEngine.Object component)
        {
            if (component != null)
                ownUIExcluded.Add(component.GetInstanceID());
        }

        /// <summary>
        /// Register a component to be translated with UI-specific prompt (labels, buttons).
        /// </summary>
        public static void RegisterUIText(UnityEngine.Object component)
        {
            if (component != null)
                ownUITranslatable.Add(component.GetInstanceID());
        }

        /// <summary>
        /// Register a panel root GameObject. All children will be identified as own UI via hierarchy check.
        /// Call this BEFORE creating any child components.
        /// </summary>
        public static void RegisterPanelRoot(GameObject panelRoot)
        {
            if (panelRoot != null)
                ownUIPanelRoots.Add(panelRoot.GetInstanceID());
        }

        /// <summary>
        /// Check if a component is part of our UI by traversing up the hierarchy.
        /// Returns true if any parent is a registered panel root.
        /// </summary>
        public static bool IsOwnUIByHierarchy(Component component)
        {
            if (component == null) return false;
            Transform current = component.transform;
            while (current != null)
            {
                if (ownUIPanelRoots.Contains(current.gameObject.GetInstanceID()))
                    return true;
                current = current.parent;
            }
            return false;
        }

        #region User Exclusions

        /// <summary>
        /// Get the full hierarchy path of a GameObject (e.g., "Canvas/Panel/ChatWindow/MessageList").
        /// Used for exclusion pattern matching.
        /// </summary>
        public static string GetGameObjectPath(GameObject obj)
        {
            if (obj == null) return "";

            var parts = new List<string>();
            var current = obj.transform;
            while (current != null)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }

        /// <summary>
        /// Check if a component is excluded by user-defined patterns.
        /// Uses caching for performance.
        /// </summary>
        public static bool IsUserExcluded(Component component)
        {
            if (component == null || userExclusions.Count == 0) return false;

            int id = component.GetInstanceID();
            if (userExclusionCache.TryGetValue(id, out bool cached))
                return cached;

            string path = GetGameObjectPath(component.gameObject);
            bool excluded = MatchesAnyExclusionPattern(path);
            userExclusionCache[id] = excluded;

            if (excluded)
                Adapter?.LogInfo($"[Exclusion] Matched: {path}");

            return excluded;
        }

        /// <summary>
        /// Check if a path matches any exclusion pattern.
        /// Supports: ** (any depth), * (single level), exact match.
        /// </summary>
        private static bool MatchesAnyExclusionPattern(string path)
        {
            foreach (var pattern in userExclusions)
            {
                if (MatchesExclusionPattern(path, pattern))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Match a path against an exclusion pattern.
        /// Patterns: "Canvas/Chat/**" matches any child, "**/PlayerName" matches at any depth.
        /// </summary>
        private static bool MatchesExclusionPattern(string path, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            // Convert pattern to regex-like matching
            // ** = any number of path segments (including zero)
            // * = any single path segment name

            // Split both into segments
            var pathParts = path.Split('/');
            var patternParts = pattern.Split('/');

            return MatchPatternRecursive(pathParts, 0, patternParts, 0);
        }

        private static bool MatchPatternRecursive(string[] path, int pathIdx, string[] pattern, int patternIdx)
        {
            // Base cases
            if (patternIdx >= pattern.Length)
                return pathIdx >= path.Length;

            string patternPart = pattern[patternIdx];

            if (patternPart == "**")
            {
                // ** matches zero or more path segments
                // Try matching rest of pattern at every remaining position
                for (int i = pathIdx; i <= path.Length; i++)
                {
                    if (MatchPatternRecursive(path, i, pattern, patternIdx + 1))
                        return true;
                }
                return false;
            }

            if (pathIdx >= path.Length)
                return false;

            string pathPart = path[pathIdx];

            if (patternPart == "*")
            {
                // * matches exactly one segment (any name)
                return MatchPatternRecursive(path, pathIdx + 1, pattern, patternIdx + 1);
            }

            // Check if pattern part contains * as wildcard within the name
            if (patternPart.Contains("*"))
            {
                // Convert to simple wildcard matching (e.g., "Chat*" matches "ChatWindow")
                string regexPattern = "^" + Regex.Escape(patternPart).Replace("\\*", ".*") + "$";
                if (!Regex.IsMatch(pathPart, regexPattern, RegexOptions.IgnoreCase))
                    return false;
            }
            else
            {
                // Exact match (case-insensitive)
                if (!string.Equals(pathPart, patternPart, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return MatchPatternRecursive(path, pathIdx + 1, pattern, patternIdx + 1);
        }

        /// <summary>
        /// Add a new exclusion pattern. Clears the cache.
        /// </summary>
        public static void AddExclusion(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return;
            pattern = pattern.Trim();

            if (!userExclusions.Contains(pattern))
            {
                userExclusions.Add(pattern);
                userExclusionCache.Clear();
                SaveCache(); // Auto-save
                Adapter?.LogInfo($"[Exclusion] Added: {pattern}");
            }
        }

        /// <summary>
        /// Remove an exclusion pattern. Clears the cache.
        /// </summary>
        public static bool RemoveExclusion(string pattern)
        {
            if (userExclusions.Remove(pattern))
            {
                userExclusionCache.Clear();
                SaveCache(); // Auto-save
                Adapter?.LogInfo($"[Exclusion] Removed: {pattern}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Clear all exclusions.
        /// </summary>
        public static void ClearExclusions()
        {
            userExclusions.Clear();
            userExclusionCache.Clear();
            SaveCache();
            Adapter?.LogInfo("[Exclusion] Cleared all exclusions");
        }

        /// <summary>
        /// Clear the exclusion cache (call on scene change).
        /// </summary>
        public static void ClearUserExclusionCache()
        {
            userExclusionCache.Clear();
        }

        #endregion

        /// <summary>
        /// Check if a component is excluded from translation (mod title, language codes, config values).
        /// </summary>
        public static bool IsOwnUIExcluded(int instanceId) => ownUIExcluded.Contains(instanceId);

        /// <summary>
        /// Check if a component is part of our own UI (registered or in panel hierarchy).
        /// </summary>
        public static bool IsOwnUI(int instanceId) => ownUIExcluded.Contains(instanceId) || ownUITranslatable.Contains(instanceId);

        /// <summary>
        /// Check if a component is part of our own UI (by instance ID or hierarchy).
        /// </summary>
        public static bool IsOwnUI(Component component)
        {
            if (component == null) return false;
            int instanceId = component.GetInstanceID();
            return IsOwnUI(instanceId) || IsOwnUIByHierarchy(component);
        }

        /// <summary>
        /// Check if a component should use UI-specific prompt (own UI).
        /// Returns false if translate_mod_ui is disabled in config.
        /// Uses hierarchy check if not explicitly registered.
        /// </summary>
        public static bool IsOwnUITranslatable(int instanceId) => Config.translate_mod_ui && ownUITranslatable.Contains(instanceId);

        /// <summary>
        /// Check if a component should use UI-specific prompt (own UI).
        /// Uses hierarchy check to identify own UI even before individual registration.
        /// </summary>
        public static bool IsOwnUITranslatable(Component component)
        {
            if (!Config.translate_mod_ui) return false;
            if (component == null) return false;
            int instanceId = component.GetInstanceID();
            // Check explicit registration first, then hierarchy
            if (ownUITranslatable.Contains(instanceId)) return true;
            // If in hierarchy and NOT explicitly excluded, it's translatable
            if (IsOwnUIByHierarchy(component) && !ownUIExcluded.Contains(instanceId)) return true;
            return false;
        }

        /// <summary>
        /// Check if a component should be skipped for translation entirely.
        /// True if: (1) in construction mode, (2) explicitly excluded, OR (3) own UI but translate_mod_ui is disabled.
        /// Uses hierarchy check to identify own UI even before individual registration.
        /// </summary>
        public static bool ShouldSkipTranslation(int instanceId)
        {
            // Skip all translations during panel construction
            if (IsInConstructionMode)
                return true;
            if (ownUIExcluded.Contains(instanceId))
                return true;
            if (ownUITranslatable.Contains(instanceId) && !Config.translate_mod_ui)
                return true;
            return false;
        }

        /// <summary>
        /// Check if a component should be skipped for translation entirely.
        /// True if: (1) in construction mode, (2) explicitly excluded, OR (3) own UI but translate_mod_ui is disabled.
        /// Uses hierarchy check to identify own UI even before individual registration.
        /// </summary>
        public static bool ShouldSkipTranslation(Component component)
        {
            // Skip all translations during panel construction
            if (IsInConstructionMode)
                return true;
            if (component == null) return false;

            // Check user-defined exclusions (priority - shared via translations.json)
            if (IsUserExcluded(component))
                return true;

            int instanceId = component.GetInstanceID();
            // Explicitly excluded - always skip
            if (ownUIExcluded.Contains(instanceId))
                return true;
            // Explicitly translatable - skip only if translate_mod_ui is disabled
            if (ownUITranslatable.Contains(instanceId))
                return !Config.translate_mod_ui;
            // Check hierarchy - if part of our UI, skip if translate_mod_ui is disabled
            if (IsOwnUIByHierarchy(component))
                return !Config.translate_mod_ui;
            return false;
        }

        // Security: Maximum text length for Ollama translation requests (prevents DoS)
        private const int MaxOllamaTextLength = 5000;

        // Marker for skipped translations (text not in expected source language)
        private const string SkipTranslationMarker = "AxNoTranslateXa";

        // Security: Regex with timeout to prevent ReDoS attacks
        private static readonly Regex NumberPattern = new Regex(
            @"(?<!\[v)(-?\d+(?:[.,]\d+)?%?)",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        public class PatternEntry
        {
            public string OriginalPattern;
            public string TranslatedPattern;
            public Regex MatchRegex;
            public List<int> PlaceholderIndices;
        }

        /// <summary>
        /// Initialize the translation core
        /// </summary>
        public static void Initialize(IModLoaderAdapter adapter)
        {
            Instance = new TranslatorCore();
            Adapter = adapter;

            // Use the folder provided by the adapter directly (no subfolder)
            ModFolder = adapter.GetPluginFolder();

            if (!Directory.Exists(ModFolder))
                Directory.CreateDirectory(ModFolder);

            CachePath = Path.Combine(ModFolder, "translations.json");
            ConfigPath = Path.Combine(ModFolder, "config.json");

            string debugPath = Path.Combine(ModFolder, "debug.txt");
            DebugMode = File.Exists(debugPath);

            LoadConfig();

            // Initialize font manager for non-Latin script support
            FontManager.Initialize();

            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            // Detect game
            CurrentGame = GameDetector.DetectGame();
            if (CurrentGame != null)
            {
                Adapter.LogInfo($"Detected game: {CurrentGame.name} (Steam: {CurrentGame.steam_id ?? "N/A"})");
            }

            LoadCache();
            StartTranslationWorker();

            if (Config.preload_model && Config.enable_ollama)
            {
                PreloadModel();
            }

            Adapter.LogInfo($"UnityGameTranslator v{PluginInfo.Version} initialized!");
            if (Config.enable_ollama)
            {
                Adapter.LogInfo($"Ollama: ENABLED - Model: {Config.model}");
            }
            string srcLang = Config.GetSourceLanguage() ?? "auto-detect";
            string tgtLang = Config.GetTargetLanguage();
            Adapter.LogInfo($"Translation: {srcLang} -> {tgtLang}");
            Adapter.LogInfo($"Cache entries: {TranslationCache.Count}, Pattern entries: {PatternEntries.Count}");
        }

        public static void OnSceneChanged(string sceneName)
        {
            lastSeenText.Clear();
            TranslatorScanner.OnSceneChange();
            TranslatorPatches.ClearCache();

            if (DebugMode)
                Adapter?.LogInfo($"Scene: {sceneName}");
        }

        public static void OnShutdown()
        {
            if (cacheModified)
            {
                try { SaveCache(); } catch { }
            }

            Adapter?.LogInfo($"Session: {translatedCount} translations, {cacheHitCount} cache hits, {ollamaCount} Ollama calls");
            Adapter?.LogInfo($"Skipped: {skippedTargetLang} (target lang heuristic), {skippedAlreadyTranslated} (reverse cache)");
        }

        public static void OnUpdate(float currentTime)
        {
            if (cacheModified && currentTime - lastSaveTime > 30f)
            {
                lastSaveTime = currentTime;
                SaveCache();
            }
        }

        #region Public Logging (for use by TranslatorPatches/TranslatorScanner)

        public static void LogInfo(string message) => Adapter?.LogInfo(message);
        public static void LogWarning(string message) => Adapter?.LogWarning(message);
        public static void LogError(string message) => Adapter?.LogError(message);

        #endregion

        private static void LoadConfig()
        {
            if (!File.Exists(ConfigPath))
            {
                string defaultConfig = JsonConvert.SerializeObject(Config, Formatting.Indented);
                File.WriteAllText(ConfigPath, defaultConfig);
                Adapter.LogInfo("Created default config file");
                return;
            }

            try
            {
                string json = File.ReadAllText(ConfigPath);
                Config = JsonConvert.DeserializeObject<ModConfig>(json) ?? new ModConfig();

                // Decrypt API token if present
                if (!string.IsNullOrEmpty(Config.api_token))
                {
                    string decryptedToken = TokenProtection.DecryptToken(Config.api_token);
                    if (decryptedToken != null)
                    {
                        // Check if token needs re-encryption (legacy plaintext)
                        if (TokenProtection.NeedsReEncryption(Config.api_token))
                        {
                            Config.api_token = decryptedToken;
                            SaveConfig(); // Will encrypt on save
                            Adapter.LogInfo("Migrated legacy token to encrypted storage");
                        }
                        else
                        {
                            Config.api_token = decryptedToken;
                        }

                        // Security: Invalidate token if API URL changed (prevent token replay attacks)
                        string currentApiUrl = Config.api_base_url ?? PluginInfo.ApiBaseUrl;
                        if (!string.IsNullOrEmpty(Config.api_token_server) &&
                            Config.api_token_server != currentApiUrl)
                        {
                            Adapter.LogWarning($"[Security] API URL changed from {Config.api_token_server} to {currentApiUrl} - invalidating token to prevent replay attacks");
                            Config.api_token = null;
                            Config.api_user = null;
                            Config.api_token_server = null;
                            SaveConfig();
                        }
                    }
                    else
                    {
                        Adapter.LogWarning("Failed to decrypt API token - clearing it");
                        Config.api_token = null;
                    }
                }

                Adapter.LogInfo($"Loaded config file (enable_translations={Config.enable_translations})");
            }
            catch (Exception e)
            {
                Adapter.LogError($"Failed to load config: {e.Message}");
            }
        }

        public static void SaveConfig()
        {
            try
            {
                // Create a copy for serialization with encrypted token
                var configToSave = new ModConfig
                {
                    // Ollama settings
                    ollama_url = Config.ollama_url,
                    model = Config.model,
                    target_language = Config.target_language,
                    source_language = Config.source_language,
                    strict_source_language = Config.strict_source_language,
                    game_context = Config.game_context,
                    timeout_ms = Config.timeout_ms,
                    enable_ollama = Config.enable_ollama,
                    cache_new_translations = Config.cache_new_translations,
                    normalize_numbers = Config.normalize_numbers,
                    debug_ollama = Config.debug_ollama,
                    preload_model = Config.preload_model,

                    // General settings
                    capture_keys_only = Config.capture_keys_only,
                    translate_mod_ui = Config.translate_mod_ui,
                    first_run_completed = Config.first_run_completed,
                    online_mode = Config.online_mode,
                    enable_translations = Config.enable_translations,
                    settings_hotkey = Config.settings_hotkey,

                    // Auth & sync
                    api_user = Config.api_user,
                    api_token_server = Config.api_token_server,
                    api_base_url = Config.api_base_url,
                    website_base_url = Config.website_base_url,
                    sync = Config.sync,
                    window_preferences = Config.window_preferences,
                    // Encrypt token before saving
                    api_token = !string.IsNullOrEmpty(Config.api_token)
                        ? TokenProtection.EncryptToken(Config.api_token)
                        : null
                };

                string json = JsonConvert.SerializeObject(configToSave, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
                Adapter?.LogInfo("Config saved");
            }
            catch (Exception e)
            {
                Adapter?.LogError($"Failed to save config: {e.Message}");
            }
        }

        private static void LoadCache()
        {
            // Reset server state (will be populated by check-uuid if online)
            ServerState = null;

            if (!File.Exists(CachePath))
            {
                // Generate UUID for new translation file
                FileUuid = Guid.NewGuid().ToString();
                Adapter.LogInfo($"No cache file found, starting fresh with UUID: {FileUuid}");
                SaveCache(); // Save immediately to persist UUID
                return;
            }

            try
            {
                string json = File.ReadAllText(CachePath);
                // Normalize line endings to prevent key mismatches (Windows editors may add \r\n)
                json = json.Replace("\r\n", "\n");

                // Parse as JObject to handle metadata
                var parsed = JObject.Parse(json);
                TranslationCache = new Dictionary<string, TranslationEntry>();

                // Track saved _game.steam_id to compare with current detection
                string savedSteamId = null;

                // Extract metadata and translations
                foreach (var prop in parsed.Properties())
                {
                    if (prop.Name == "_uuid")
                    {
                        FileUuid = prop.Value.ToString();
                    }
                    else if (prop.Name == "_local_changes")
                    {
                        LocalChangesCount = prop.Value.Value<int>();
                    }
                    else if (prop.Name == "_source" && prop.Value.Type == JTokenType.Object)
                    {
                        // Load source info for sync detection
                        var source = prop.Value as JObject;
                        LastSyncedHash = source?["hash"]?.Value<string>();
                    }
                    else if (prop.Name == "_game" && prop.Value.Type == JTokenType.Object)
                    {
                        // Load saved steam_id for comparison with current detection
                        var game = prop.Value as JObject;
                        savedSteamId = game?["steam_id"]?.Value<string>();
                    }
                    else if (prop.Name == "_exclusions" && prop.Value.Type == JTokenType.Array)
                    {
                        // Load user exclusion patterns
                        userExclusions.Clear();
                        foreach (var item in prop.Value)
                        {
                            var pattern = item.ToString();
                            if (!string.IsNullOrEmpty(pattern))
                            {
                                userExclusions.Add(pattern);
                            }
                        }
                        Adapter?.LogInfo($"[LoadCache] Loaded {userExclusions.Count} user exclusions");
                    }
                    else if (prop.Name == "_fonts" && prop.Value.Type == JTokenType.Object)
                    {
                        // Load per-font settings
                        FontSettingsMap.Clear();
                        foreach (var fontProp in (prop.Value as JObject).Properties())
                        {
                            var settings = new FontSettings();
                            var fontObj = fontProp.Value as JObject;
                            if (fontObj != null)
                            {
                                settings.enabled = fontObj["enabled"]?.Value<bool>() ?? true;
                                settings.fallback = fontObj["fallback"]?.Value<string>();
                                settings.type = fontObj["type"]?.Value<string>();
                                settings.scale = fontObj["scale"]?.Value<float>() ?? 1.0f;
                            }
                            FontSettingsMap[fontProp.Name] = settings;
                        }
                        Adapter?.LogInfo($"[LoadCache] Loaded {FontSettingsMap.Count} font settings");
                    }
                    else if (prop.Name == "_settings" && prop.Value.Type == JTokenType.Object)
                    {
                        // Load game-specific settings
                        var settingsObj = prop.Value as JObject;
                        if (settingsObj != null)
                        {
                            DisableEventSystemOverride = settingsObj["disable_eventsystem_override"]?.Value<bool>() ?? false;
                            Adapter?.LogInfo($"[LoadCache] Loaded settings: DisableEventSystemOverride={DisableEventSystemOverride}");
                        }
                    }
                    else if (!prop.Name.StartsWith("_"))
                    {
                        // Normalize key line endings for cross-platform consistency
                        string normalizedKey = NormalizeLineEndings(prop.Name);

                        // Handle both new format (object with v/t) and legacy format (string)
                        TranslationEntry newEntry;
                        if (prop.Value.Type == JTokenType.Object)
                        {
                            // New format: {"v": "value", "t": "A"}
                            var obj = prop.Value as JObject;
                            newEntry = new TranslationEntry
                            {
                                // Normalize value line endings too
                                Value = NormalizeLineEndings(obj?["v"]?.ToString() ?? ""),
                                Tag = obj?["t"]?.ToString() ?? "A"
                            };
                        }
                        else if (prop.Value.Type == JTokenType.String)
                        {
                            // Legacy format: string value - convert to AI tag
                            newEntry = new TranslationEntry
                            {
                                Value = NormalizeLineEndings(prop.Value.ToString()),
                                Tag = "A"  // Default to AI for legacy data
                            };
                            cacheModified = true;  // Will save in new format
                        }
                        else
                        {
                            continue;
                        }

                        // Handle duplicates after normalization (e.g., "LB\r\n" and "LB\n" become same key)
                        if (TranslationCache.TryGetValue(normalizedKey, out var existingEntry))
                        {
                            // Tag priority: H > V > A (Human > Validated > AI)
                            int GetPriority(string tag) => tag == "H" ? 3 : tag == "V" ? 2 : 1;

                            if (GetPriority(newEntry.Tag) > GetPriority(existingEntry.Tag))
                            {
                                TranslationCache[normalizedKey] = newEntry;
                                cacheModified = true;
                            }
                            // Otherwise keep existing (higher or same priority)
                        }
                        else
                        {
                            TranslationCache[normalizedKey] = newEntry;
                        }

                        // Mark modified if key was normalized
                        if (normalizedKey != prop.Name)
                        {
                            cacheModified = true;
                        }
                    }
                }

                // Generate UUID if not present
                if (string.IsNullOrEmpty(FileUuid))
                {
                    FileUuid = Guid.NewGuid().ToString();
                    cacheModified = true;
                    Adapter.LogInfo($"Legacy cache file, generated UUID: {FileUuid}");
                }

                // Update _game.steam_id if we detected one but file didn't have it
                if (CurrentGame != null && !string.IsNullOrEmpty(CurrentGame.steam_id))
                {
                    if (string.IsNullOrEmpty(savedSteamId) || savedSteamId != CurrentGame.steam_id)
                    {
                        cacheModified = true;
                        Adapter.LogInfo($"[LoadCache] Detected steam_id ({CurrentGame.steam_id}) differs from saved ({savedSteamId ?? "null"}), will update file");
                    }
                }

                // Load ancestor cache if exists (for 3-way merge support)
                LoadAncestorCache();

                // Recalculate LocalChangesCount based on actual differences (always, even if no ancestor)
                RecalculateLocalChanges();

                // Build reverse cache: all translated values
                translatedTexts.Clear();
                foreach (var kv in TranslationCache)
                {
                    if (kv.Key != kv.Value.Value && !string.IsNullOrEmpty(kv.Value.Value))
                    {
                        translatedTexts.Add(kv.Value.Value);
                    }
                }

                BuildPatternEntries();
                Adapter.LogInfo($"Loaded {TranslationCache.Count} cached translations, {translatedTexts.Count} reverse entries, UUID: {FileUuid}");
            }
            catch (Exception e)
            {
                Adapter.LogError($"Failed to load cache: {e.Message}");
                TranslationCache = new Dictionary<string, TranslationEntry>();
                FileUuid = Guid.NewGuid().ToString();
            }
        }

        private static void LoadAncestorCache()
        {
            string ancestorPath = CachePath + ".ancestor";
            if (!File.Exists(ancestorPath))
            {
                AncestorCache = new Dictionary<string, TranslationEntry>();
                return;
            }

            try
            {
                string ancestorJson = File.ReadAllText(ancestorPath);
                // Normalize line endings (consistency with main cache)
                ancestorJson = ancestorJson.Replace("\r\n", "\n");
                var ancestorParsed = JObject.Parse(ancestorJson);
                AncestorCache = new Dictionary<string, TranslationEntry>();

                foreach (var prop in ancestorParsed.Properties())
                {
                    if (!prop.Name.StartsWith("_"))
                    {
                        // Normalize key line endings for cross-platform consistency
                        string normalizedKey = NormalizeLineEndings(prop.Name);

                        if (prop.Value.Type == JTokenType.Object)
                        {
                            // New format
                            var obj = prop.Value as JObject;
                            AncestorCache[normalizedKey] = new TranslationEntry
                            {
                                Value = NormalizeLineEndings(obj?["v"]?.ToString() ?? ""),
                                Tag = obj?["t"]?.ToString() ?? "A"
                            };
                        }
                        else if (prop.Value.Type == JTokenType.String)
                        {
                            // Legacy format
                            AncestorCache[normalizedKey] = new TranslationEntry
                            {
                                Value = NormalizeLineEndings(prop.Value.ToString()),
                                Tag = "A"
                            };
                        }
                    }
                }

                Adapter.LogInfo($"Loaded {AncestorCache.Count} ancestor entries for merge support");
            }
            catch (Exception ae)
            {
                Adapter.LogWarning($"Failed to load ancestor cache: {ae.Message}");
                AncestorCache = new Dictionary<string, TranslationEntry>();
            }
        }

        /// <summary>
        /// Reload the cache from disk. Call this after downloading a translation
        /// to apply it immediately without requiring a game restart.
        /// </summary>
        public static void ReloadCache()
        {
            Adapter?.LogInfo("[TranslatorCore] Reloading cache from disk...");
            LoadCache();

            // Clear processing caches so scanner re-evaluates all text with new translations
            ClearProcessingCaches();
        }

        /// <summary>
        /// Save the current cache as ancestor (for 3-way merge)
        /// Call this after downloading from website before any local changes
        /// </summary>
        public static void SaveAncestorCache()
        {
            try
            {
                string ancestorPath = CachePath + ".ancestor";
                var output = new JObject();

                foreach (var kvp in TranslationCache)
                {
                    output[kvp.Key] = new JObject
                    {
                        ["v"] = kvp.Value.Value,
                        ["t"] = kvp.Value.Tag ?? "A"
                    };
                }

                string json = output.ToString(Formatting.Indented);
                File.WriteAllText(ancestorPath, json);

                // Copy to AncestorCache
                AncestorCache = new Dictionary<string, TranslationEntry>();
                foreach (var kvp in TranslationCache)
                {
                    AncestorCache[kvp.Key] = new TranslationEntry
                    {
                        Value = kvp.Value.Value,
                        Tag = kvp.Value.Tag
                    };
                }

                LocalChangesCount = 0;
                Adapter.LogInfo($"Saved ancestor cache with {AncestorCache.Count} entries");
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save ancestor cache: {e.Message}");
            }
        }

        /// <summary>
        /// Save remote translations as ancestor (for use after merge).
        /// This sets the ancestor to the server version, so LocalChangesCount reflects local additions.
        /// </summary>
        /// <param name="remoteTranslations">Remote translations (legacy string format, will be converted to entries with AI tag)</param>
        public static void SaveAncestorFromRemote(Dictionary<string, string> remoteTranslations)
        {
            try
            {
                string ancestorPath = CachePath + ".ancestor";
                var output = new JObject();

                foreach (var kvp in remoteTranslations)
                {
                    if (kvp.Key.StartsWith("_")) continue;
                    output[kvp.Key] = new JObject
                    {
                        ["v"] = kvp.Value,
                        ["t"] = "A"  // Default to AI for legacy format
                    };
                }

                string json = output.ToString(Formatting.Indented);
                File.WriteAllText(ancestorPath, json);

                // Convert to AncestorCache
                AncestorCache = new Dictionary<string, TranslationEntry>();
                foreach (var kvp in remoteTranslations)
                {
                    if (kvp.Key.StartsWith("_")) continue;
                    AncestorCache[kvp.Key] = new TranslationEntry
                    {
                        Value = kvp.Value,
                        Tag = "A"
                    };
                }

                Adapter.LogInfo($"Saved ancestor from remote with {AncestorCache.Count} entries");
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save ancestor from remote: {e.Message}");
            }
        }

        /// <summary>
        /// Save remote translations as ancestor (new format with tags).
        /// </summary>
        public static void SaveAncestorFromRemote(Dictionary<string, TranslationEntry> remoteTranslations)
        {
            try
            {
                string ancestorPath = CachePath + ".ancestor";
                var output = new JObject();

                foreach (var kvp in remoteTranslations)
                {
                    if (kvp.Key.StartsWith("_")) continue;
                    output[kvp.Key] = new JObject
                    {
                        ["v"] = kvp.Value.Value,
                        ["t"] = kvp.Value.Tag ?? "A"
                    };
                }

                string json = output.ToString(Formatting.Indented);
                File.WriteAllText(ancestorPath, json);

                // Copy to AncestorCache
                AncestorCache = new Dictionary<string, TranslationEntry>();
                foreach (var kvp in remoteTranslations)
                {
                    if (kvp.Key.StartsWith("_")) continue;
                    AncestorCache[kvp.Key] = new TranslationEntry
                    {
                        Value = kvp.Value.Value,
                        Tag = kvp.Value.Tag
                    };
                }

                Adapter.LogInfo($"Saved ancestor from remote with {AncestorCache.Count} entries");
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save ancestor from remote: {e.Message}");
            }
        }

        /// <summary>
        /// Recalculate LocalChangesCount based on actual differences between TranslationCache and AncestorCache.
        /// Call this after loading caches or after a merge.
        /// </summary>
        public static void RecalculateLocalChanges()
        {
            if (AncestorCache.Count == 0)
            {
                // No ancestor = all entries are local changes
                LocalChangesCount = TranslationCache.Count;
                return;
            }

            int changes = 0;
            foreach (var kvp in TranslationCache)
            {
                // Skip metadata keys
                if (kvp.Key.StartsWith("_")) continue;

                // New key or different value/tag = local change
                if (!AncestorCache.TryGetValue(kvp.Key, out var ancestorEntry) ||
                    ancestorEntry.Value != kvp.Value.Value ||
                    ancestorEntry.Tag != kvp.Value.Tag)
                {
                    changes++;
                }
            }

            LocalChangesCount = changes;
            Adapter?.LogInfo($"[LocalChanges] Recalculated: {changes} local changes");
        }

        /// <summary>
        /// Convert TranslationCache to a simple string dictionary (for legacy merge support).
        /// Values are extracted without tags.
        /// </summary>
        public static Dictionary<string, string> GetCacheAsStrings()
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in TranslationCache)
            {
                result[kvp.Key] = kvp.Value.Value;
            }
            return result;
        }

        /// <summary>
        /// Convert AncestorCache to a simple string dictionary (for legacy merge support).
        /// </summary>
        public static Dictionary<string, string> GetAncestorAsStrings()
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in AncestorCache)
            {
                result[kvp.Key] = kvp.Value.Value;
            }
            return result;
        }

        /// <summary>
        /// Parse JSON content into Dictionary of TranslationEntry.
        /// Handles both new format ({"v": "value", "t": "tag"}) and legacy format (string).
        /// </summary>
        /// <param name="jsonContent">Raw JSON string from file or API</param>
        /// <returns>Dictionary with translation entries including tags</returns>
        public static Dictionary<string, TranslationEntry> ParseTranslationsFromJson(string jsonContent)
        {
            var result = new Dictionary<string, TranslationEntry>();

            try
            {
                // Normalize line endings for consistent key handling
                jsonContent = jsonContent.Replace("\r\n", "\n");
                var parsed = JObject.Parse(jsonContent);

                foreach (var prop in parsed.Properties())
                {
                    // Skip metadata keys
                    if (prop.Name.StartsWith("_")) continue;

                    if (prop.Value.Type == JTokenType.Object)
                    {
                        // New format: {"v": "value", "t": "A"}
                        var obj = prop.Value as JObject;
                        result[prop.Name] = new TranslationEntry
                        {
                            Value = obj?["v"]?.ToString() ?? "",
                            Tag = obj?["t"]?.ToString() ?? "A"
                        };
                    }
                    else if (prop.Value.Type == JTokenType.String)
                    {
                        // Legacy format: string value - default to AI tag
                        result[prop.Name] = new TranslationEntry
                        {
                            Value = prop.Value.ToString(),
                            Tag = "A"
                        };
                    }
                }
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"Failed to parse translations from JSON: {e.Message}");
            }

            return result;
        }

        /// <summary>
        /// Compute SHA256 hash of the translation content (same format as upload).
        /// Used to detect if local content differs from server version.
        /// IMPORTANT: Must match PHP Translation::computeHash() exactly.
        /// </summary>
        public static string ComputeContentHash()
        {
            try
            {
                // Build content with sorted keys for deterministic hash
                // Include only translations (non-underscore keys) + _uuid
                // This must match PHP computeHash() which filters the same way
                // Use Ordinal comparer to match PHP ksort() byte-by-byte sorting
                var sortedDict = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var kvp in TranslationCache)
                {
                    // TranslationCache now contains TranslationEntry objects
                    // Serialize with new format: {"v": "value", "t": "tag"}
                    sortedDict[kvp.Key] = new Dictionary<string, string>
                    {
                        ["v"] = kvp.Value.Value,
                        ["t"] = kvp.Value.Tag ?? "A"
                    };
                }
                sortedDict["_uuid"] = FileUuid;

                // Serialize with same settings as PHP json_encode(JSON_UNESCAPED_UNICODE)
                // Newtonsoft.Json by default doesn't escape unicode, same as PHP
                string content = JsonConvert.SerializeObject(sortedDict, Formatting.None);

                // Always log for debugging hash issues
                string preview = content.Length > 100 ? content.Substring(0, 100) + "..." : content;
                Adapter?.LogInfo($"[HashDebug] Local JSON preview: {preview}");
                Adapter?.LogInfo($"[HashDebug] Local entry count: {sortedDict.Count}, length: {content.Length}");

                using (var sha256 = SHA256.Create())
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(content);
                    byte[] hash = sha256.ComputeHash(bytes);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[Hash] Failed to compute content hash: {e.Message}");
                return null;
            }
        }

        public static void BuildPatternEntries()
        {
            PatternEntries.Clear();
            var placeholderRegex = new Regex(@"\[v(\d+)\]", RegexOptions.None, TimeSpan.FromMilliseconds(100));

            foreach (var kv in TranslationCache)
            {
                // Skip if key equals value (no translation)
                if (kv.Key == kv.Value.Value) continue;

                var matches = placeholderRegex.Matches(kv.Key);
                if (matches.Count == 0) continue;

                try
                {
                    var placeholderIndices = new List<int>();
                    string pattern = Regex.Escape(kv.Key);

                    foreach (Match match in matches)
                    {
                        int index = int.Parse(match.Groups[1].Value);
                        placeholderIndices.Add(index);
                        string placeholder = Regex.Escape(match.Value);
                        pattern = pattern.Replace(placeholder, @"(-?\d+(?:[.,]\d+)?%?)");
                    }

                    PatternEntries.Add(new PatternEntry
                    {
                        OriginalPattern = kv.Key,
                        TranslatedPattern = kv.Value.Value,
                        MatchRegex = new Regex("^" + pattern + "$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
                        PlaceholderIndices = placeholderIndices
                    });
                }
                catch { }
            }

            if (DebugMode)
                Adapter?.LogInfo($"Built {PatternEntries.Count} pattern entries");
        }

        private static bool workerRunning = false;

        private static void StartTranslationWorker()
        {
            if (!Config.enable_ollama) return;
            if (workerRunning) return; // Already running

            workerRunning = true;
            Thread workerThread = new Thread(TranslationWorkerLoop);
            workerThread.IsBackground = true;
            workerThread.Start();
        }

        /// <summary>
        /// Start the translation worker if Ollama is enabled and worker isn't running.
        /// Call this after enabling Ollama in settings.
        /// </summary>
        public static void EnsureWorkerRunning()
        {
            if (Config.enable_ollama && !workerRunning)
            {
                Adapter?.LogInfo("[TranslatorCore] Starting Ollama worker thread...");
                StartTranslationWorker();
            }
        }

        /// <summary>
        /// Clear the translation queue. Called when Ollama is disabled.
        /// </summary>
        public static void ClearQueue()
        {
            lock (lockObj)
            {
                int count = translationQueue.Count;
                translationQueue.Clear();
                pendingTranslations.Clear();
                pendingComponents.Clear();
                isTranslating = false;
                currentlyTranslating = null;
                if (count > 0)
                {
                    Adapter?.LogInfo($"[TranslatorCore] Cleared {count} items from translation queue");
                }
            }
        }

        private static void PreloadModel()
        {
            try
            {
                Adapter.LogInfo($"Preloading model {Config.model}...");
                var requestBody = new { model = Config.model, prompt = "Hi", stream = false, options = new { num_predict = 1 } };
                string jsonRequest = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
                var response = httpClient.PostAsync($"{Config.ollama_url}/api/generate", content).Result;
                if (response.IsSuccessStatusCode)
                {
                    Adapter.LogInfo("Model preloaded successfully");
                }
                else
                {
                    Adapter.LogWarning($"Failed to preload model: {response.StatusCode}");
                }
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Error preloading model: {e.Message}");
            }
        }

        /// <summary>
        /// Test connection to Ollama server.
        /// </summary>
        /// <param name="url">The Ollama URL to test</param>
        /// <returns>True if connection successful</returns>
        public static async System.Threading.Tasks.Task<bool> TestOllamaConnection(string url)
        {
            try
            {
                var testUrl = url.TrimEnd('/') + "/api/tags";
                var response = await httpClient.GetAsync(testUrl);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static void TranslationWorkerLoop()
        {
            if (Config.debug_ollama)
                Adapter?.LogInfo("[Worker] Thread started");

            while (true)
            {
                // Stop if Ollama was disabled
                if (!Config.enable_ollama)
                {
                    Adapter?.LogInfo("[Worker] Ollama disabled, stopping worker thread");
                    workerRunning = false;
                    return;
                }

                string textToTranslate = null;
                List<object> componentsToUpdate = null;

                lock (lockObj)
                {
                    if (translationQueue.Count > 0)
                    {
                        textToTranslate = translationQueue.Dequeue();

                        // TAKE components (remove from dict) so new queues create fresh entries
                        if (pendingComponents.TryGetValue(textToTranslate, out var comps))
                        {
                            componentsToUpdate = comps; // Take the list directly
                            pendingComponents.Remove(textToTranslate); // Remove NOW
                            if (Config.debug_ollama)
                                Adapter?.LogInfo($"[Worker] Found {comps.Count} components for text");
                        }
                        else
                        {
                            if (Config.debug_ollama)
                                Adapter?.LogWarning($"[Worker] NO components found for text!");
                        }

                        // Remove from pending so same text can be re-queued with new components
                        pendingTranslations.Remove(textToTranslate);

                        if (Config.debug_ollama)
                            Adapter?.LogInfo($"[Worker] Dequeued: {textToTranslate?.Substring(0, Math.Min(30, textToTranslate?.Length ?? 0))}...");
                    }
                }

                if (textToTranslate != null)
                {
                    string originalText = textToTranslate;
                    isTranslating = true;
                    currentlyTranslating = textToTranslate.Length > 50 ? textToTranslate.Substring(0, 50) + "..." : textToTranslate;

                    // Check if this text is from our own UI by examining the pending components
                    // Use IsOwnUI (not IsOwnUITranslatable) for tagging - it doesn't depend on translate_mod_ui config
                    // This is more accurate than string-based tracking which caused false positives
                    bool isOwnUI = false;
                    if (componentsToUpdate != null && componentsToUpdate.Count > 0)
                    {
                        foreach (var comp in componentsToUpdate)
                        {
                            if (comp is Component component && IsOwnUI(component))
                            {
                                isOwnUI = true;
                                break;
                            }
                        }
                    }

                    try
                    {
                        if (Config.debug_ollama)
                            Adapter?.LogInfo($"[Worker] Calling Ollama...{(isOwnUI ? " (UI prompt)" : "")}");

                        // Extract numbers BEFORE sending to Ollama
                        string normalizedOriginal = textToTranslate;
                        List<string> extractedNumbers = null;
                        if (Config.normalize_numbers)
                        {
                            normalizedOriginal = ExtractNumbersToPlaceholders(textToTranslate, out extractedNumbers);
                        }

                        // Check cache first (another request might have already translated this)
                        string translation = null;
                        if (TranslationCache.TryGetValue(normalizedOriginal, out var cachedEntry))
                        {
                            if (cachedEntry.Value != normalizedOriginal)
                            {
                                translation = cachedEntry.Value;
                                if (Config.debug_ollama)
                                    Adapter?.LogInfo($"[Worker] Cache hit for normalized text, skipping Ollama");
                            }
                        }

                        // Capture keys only mode: store H+empty without calling Ollama
                        if (Config.capture_keys_only)
                        {
                            AddToCache(normalizedOriginal, "", "H");
                            if (Config.debug_ollama)
                                Adapter?.LogInfo($"[Worker] Captured key (no translation): {normalizedOriginal.Substring(0, Math.Min(30, normalizedOriginal.Length))}...");
                        }
                        // Only call Ollama if not in cache
                        else if (translation == null)
                        {
                            translation = TranslateWithOllama(normalizedOriginal, extractedNumbers, isOwnUI);

                            if (Config.debug_ollama)
                                Adapter?.LogInfo($"[Worker] Ollama returned: {translation?.Substring(0, Math.Min(30, translation?.Length ?? 0))}...");

                            if (!string.IsNullOrEmpty(translation))
                            {
                                // Check if Ollama returned the skip marker (text not in expected source language)
                                bool isSkipped = translation.Contains(SkipTranslationMarker);

                                // Cache with appropriate tag: S=Skipped, M=Mod UI, A=AI-translated
                                string tag = isSkipped ? "S" : (isOwnUI ? "M" : "A");
                                AddToCache(normalizedOriginal, isSkipped ? normalizedOriginal : translation, tag);

                                if (!isSkipped && translation != normalizedOriginal)
                                {
                                    ollamaCount++;

                                    // For updating components, restore actual numbers
                                    string translationWithNumbers = translation;
                                    if (extractedNumbers != null)
                                    {
                                        translationWithNumbers = RestoreNumbersFromPlaceholders(translation, extractedNumbers);
                                    }

                                    // Notify mod loader to update components
                                    OnTranslationComplete?.Invoke(originalText, translationWithNumbers, componentsToUpdate);

                                    if (DebugMode || Config.debug_ollama)
                                    {
                                        string preview = originalText.Length > 30 ? originalText.Substring(0, 30) + "..." : originalText;
                                        Adapter?.LogInfo($"[Ollama] {preview}");
                                    }
                                }
                                else if (isSkipped && Config.debug_ollama)
                                {
                                    string preview = originalText.Length > 30 ? originalText.Substring(0, 30) + "..." : originalText;
                                    Adapter?.LogInfo($"[Ollama] Skipped (not in source language): {preview}");
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        if (Config.debug_ollama)
                            Adapter?.LogWarning($"Ollama error: {e.Message}");
                    }
                    finally
                    {
                        isTranslating = false;
                        currentlyTranslating = null;
                    }

                    // Note: pendingTranslations and pendingComponents already cleaned at dequeue time
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>
        /// Detects the type of text for prompt optimization.
        /// </summary>
        private static TextType DetectTextType(string text)
        {
            if (string.IsNullOrEmpty(text))
                return TextType.SingleWord;

            // Paragraph: has newlines
            if (text.Contains('\n'))
                return TextType.Paragraph;

            // Check if text uses a scriptio continua writing system (no spaces between words)
            bool isScriptioContinua = text.Any(c =>
                (c >= 0x4E00 && c <= 0x9FFF) ||   // Chinese (CJK Unified Ideographs)
                (c >= 0x3040 && c <= 0x30FF) ||   // Japanese Hiragana/Katakana
                (c >= 0xAC00 && c <= 0xD7AF) ||   // Korean Hangul
                (c >= 0x0E00 && c <= 0x0E7F) ||   // Thai
                (c >= 0x0E80 && c <= 0x0EFF) ||   // Lao
                (c >= 0x1780 && c <= 0x17FF) ||   // Khmer (Cambodian)
                (c >= 0x1000 && c <= 0x109F) ||   // Myanmar (Burmese)
                (c >= 0x0F00 && c <= 0x0FFF));    // Tibetan

            if (isScriptioContinua)
            {
                // No-space scripts: use character count as proxy
                if (text.Length <= 4) return TextType.SingleWord;
                return TextType.Phrase;
            }
            else
            {
                // Space-based scripts (Latin, Arabic, Hebrew, Devanagari, etc.)
                if (!text.Contains(' ')) return TextType.SingleWord;
                return TextType.Phrase;
            }
        }

        private static string TranslateWithOllama(string textWithPlaceholders, List<string> extractedNumbers, bool isOwnUI = false)
        {
            // Security: Reject text that's too long (prevents DoS via large requests)
            if (textWithPlaceholders.Length > MaxOllamaTextLength)
            {
                if (Config.debug_ollama)
                    Adapter?.LogWarning($"[Ollama] Text too long ({textWithPlaceholders.Length} chars), skipping translation");
                return null;
            }

            try
            {
                string textToTranslate = textWithPlaceholders;
                TextType textType = DetectTextType(textToTranslate);

                // Build system prompt based on text type
                var promptBuilder = new StringBuilder();
                string targetLang = Config.GetTargetLanguage();
                string sourceLang = Config.GetSourceLanguage();

                if (isOwnUI)
                {
                    // UI-specific prompt for mod interface (source is always English)
                    promptBuilder.AppendLine("=== CONTEXT ===");
                    promptBuilder.AppendLine($"Translating a game translation tool interface from English to {targetLang}.");
                    promptBuilder.AppendLine("Technical UI with terms: Ollama, cache, merge, sync, upload, download, API, hotkey, config, JSON.");
                    promptBuilder.AppendLine();
                    promptBuilder.AppendLine("=== TRANSLATION RULES ===");
                    promptBuilder.AppendLine("- Output the translation only, no explanation");
                    promptBuilder.AppendLine("- Translation must be understandable and correct in target language");
                    promptBuilder.AppendLine("- Keep it concise for UI");
                    promptBuilder.AppendLine("- Preserve formatting tags and special characters");
                    promptBuilder.AppendLine("- Keep technical terms unchanged: Ollama, API, URL, UUID, JSON, AI");
                    promptBuilder.AppendLine("- Keep keyboard shortcuts as-is: Ctrl, Alt, Shift, F1-F12, Tab, Esc");
                    if (extractedNumbers != null && extractedNumbers.Count > 0)
                    {
                        promptBuilder.AppendLine("- IMPORTANT: Keep [v0], [v1], etc. placeholders exactly as-is");
                    }

                    if (textType == TextType.SingleWord)
                    {
                        promptBuilder.AppendLine();
                        promptBuilder.Append("Now, translate this word:");
                    }
                }
                else
                {
                    // Game context prompt - default to generic game context if not specified
                    string gameCtx = !string.IsNullOrEmpty(Config.game_context)
                        ? Config.game_context
                        : "video game UI, menus and dialogues";

                    // Strict source language filter: add CRITICAL RULE section first
                    if (Config.strict_source_language && sourceLang != null)
                    {
                        promptBuilder.AppendLine("=== CRITICAL RULE ===");
                        promptBuilder.AppendLine($"Source language: {sourceLang}");
                        promptBuilder.AppendLine($"- If text is NOT in {sourceLang}: reply ONLY with exactly: {SkipTranslationMarker}");
                        promptBuilder.AppendLine($"- If text IS in {sourceLang}: translate to {targetLang}");
                        promptBuilder.AppendLine();
                    }

                    // Context section
                    promptBuilder.AppendLine("=== CONTEXT ===");
                    if (sourceLang != null)
                        promptBuilder.AppendLine($"Translating video game ({gameCtx}) from {sourceLang} to {targetLang}.");
                    else
                        promptBuilder.AppendLine($"Translating video game ({gameCtx}) to {targetLang}.");
                    promptBuilder.AppendLine();

                    // Translation rules section
                    promptBuilder.AppendLine("=== TRANSLATION RULES ===");
                    promptBuilder.AppendLine("- Output the translation only, no explanation");
                    promptBuilder.AppendLine("- Translation must be correct in target language");
                    promptBuilder.AppendLine("- Keep it concise for UI");
                    promptBuilder.AppendLine("- Preserve formatting tags and special characters");
                    promptBuilder.AppendLine("- Keep unchanged: keyboard keys (Tab, Esc, Space...), technical settings (VSync, Auto)");
                    if (extractedNumbers != null && extractedNumbers.Count > 0)
                    {
                        promptBuilder.AppendLine("- IMPORTANT: Keep [v0], [v1], etc. placeholders exactly as-is");
                    }

                    if (textType == TextType.SingleWord)
                    {
                        promptBuilder.AppendLine();
                        promptBuilder.Append("Now, translate this word:");
                    }
                }

                string systemPrompt = promptBuilder.ToString();

                // Debug: log the full system prompt being sent
                if (Config.debug_ollama)
                {
                    Adapter?.LogInfo($"[Ollama] System prompt:\n{systemPrompt}");
                }

                var requestBody = new
                {
                    model = Config.model,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = textToTranslate + " /no_think" },
                        new { role = "assistant", content = "<think>\n\n</think>\n\n" }
                    },
                    stream = false,
                    options = new { temperature = 0.0, num_predict = Math.Max(200, textToTranslate.Length * 2) }
                };

                string jsonRequest = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var response = httpClient.PostAsync($"{Config.ollama_url}/api/chat", content).Result;

                if (!response.IsSuccessStatusCode)
                {
                    if (Config.debug_ollama)
                        Adapter?.LogWarning($"Ollama HTTP {response.StatusCode}");
                    return null;
                }

                string responseJson = response.Content.ReadAsStringAsync().Result;
                var responseObj = JObject.Parse(responseJson);
                string translation = responseObj["message"]?["content"]?.ToString()?.Trim();

                if (Config.debug_ollama)
                {
                    Adapter?.LogInfo($"[Ollama Raw] {translation?.Substring(0, Math.Min(80, translation?.Length ?? 0))}");
                }

                if (!string.IsNullOrEmpty(translation))
                {
                    translation = CleanTranslation(translation);
                    if (Config.debug_ollama)
                    {
                        Adapter?.LogInfo($"[Ollama Clean] {translation?.Substring(0, Math.Min(50, translation?.Length ?? 0))}");
                    }
                }

                return translation;
            }
            catch (Exception e)
            {
                if (Config.debug_ollama)
                    Adapter?.LogWarning($"Ollama exception: {e.Message}");
                return null;
            }
        }

        public static string CleanTranslation(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Remove <think> blocks from thinking models (qwen3, etc.)
            text = Regex.Replace(text, @"<think>[\s\S]*?</think>\s*", "", RegexOptions.IgnoreCase);

            // Remove /no_think and /think artifacts from qwen3 models
            text = text.Replace(" /no_think", "").Replace("/no_think", "");
            text = text.Replace(" /think", "").Replace("/think", "");

            // Remove markdown bold **text**
            text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");

            // Remove common prefixes (only at start)
            text = Regex.Replace(text, @"^(Translation|Traduction|Here'?s?|The translation is)\s*[:\-]?\s*", "", RegexOptions.IgnoreCase);

            // Remove explanation blocks - only if they start with typical LLM explanation patterns
            // Don't cut legitimate double newlines in the source text
            var explanationMatch = Regex.Match(text, @"\n\n(Note:|I |This |Here |The above|Explanation:|Translation note:)", RegexOptions.IgnoreCase);
            if (explanationMatch.Success)
                text = text.Substring(0, explanationMatch.Index);

            // Remove quotes only if they wrap the entire text
            text = text.Trim();
            if ((text.StartsWith("\"") && text.EndsWith("\"")) || (text.StartsWith("'") && text.EndsWith("'")))
                text = text.Substring(1, text.Length - 2);

            return text.Trim();
        }

        /// <summary>
        /// Normalize line endings to Unix format (\n).
        /// Converts \r\n (Windows) and \r (old Mac) to \n.
        /// This ensures consistent keys across platforms.
        /// </summary>
        public static string NormalizeLineEndings(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Order is important: first \r\n, then \r
            // Otherwise \r\n would become \n\n
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>
        /// Add a translation to the cache with an optional tag.
        /// </summary>
        /// <param name="original">Original text (key)</param>
        /// <param name="translated">Translated text (value)</param>
        /// <param name="tag">Tag: A=AI, H=Human, V=Validated (default: A)</param>
        public static void AddToCache(string original, string translated, string tag = "A")
        {
            if (string.IsNullOrEmpty(original))
                return;

            // Allow empty translated value for capture-only mode (H tag with empty value)
            if (string.IsNullOrEmpty(translated) && tag != "H")
                return;

            // Normalize line endings for cross-platform consistency
            string normalizedKey = NormalizeLineEndings(original);
            string normalizedValue = NormalizeLineEndings(translated ?? "");

            lock (lockObj)
            {
                if (TranslationCache.ContainsKey(normalizedKey))
                    return;

                var entry = new TranslationEntry
                {
                    Value = normalizedValue,
                    Tag = tag ?? "A"
                };

                TranslationCache[normalizedKey] = entry;
                cacheModified = true;

                // Track local changes (if different from ancestor or new)
                if (AncestorCache.Count > 0)
                {
                    if (!AncestorCache.TryGetValue(normalizedKey, out var ancestorEntry) ||
                        ancestorEntry.Value != entry.Value ||
                        ancestorEntry.Tag != entry.Tag)
                    {
                        LocalChangesCount++;
                    }
                }
                else
                {
                    // No ancestor = all translations are local
                    LocalChangesCount++;
                }

                // Add to reverse cache (only if value is non-empty and different from key)
                if (normalizedKey != entry.Value && !string.IsNullOrEmpty(entry.Value))
                {
                    translatedTexts.Add(entry.Value);
                }

                // Note: No longer clearing lastSeenText here.
                // OnTranslationComplete updates tracked components directly.
                // New components will be translated on their next scan cycle.

                if (normalizedKey.Contains("[v"))
                {
                    BuildPatternEntries();
                }

                if (DebugMode)
                    Adapter?.LogInfo($"[Cache+] {normalizedKey.Substring(0, Math.Min(40, normalizedKey.Length))}... [{tag}]");
            }
        }

        public static string ExtractNumbersToPlaceholders(string text, out List<string> extractedNumbers)
        {
            extractedNumbers = new List<string>();

            if (string.IsNullOrEmpty(text))
                return text;

            var matches = NumberPattern.Matches(text);
            if (matches.Count == 0)
                return text;

            var numbersWithIndex = new List<Tuple<string, int, int>>();
            foreach (Match match in matches)
            {
                if (!IsPartOfHexColor(text, match.Index, match.Length))
                {
                    numbersWithIndex.Add(Tuple.Create(match.Value, match.Index, match.Length));
                }
            }

            if (numbersWithIndex.Count == 0)
                return text;

            foreach (var num in numbersWithIndex)
            {
                extractedNumbers.Add(num.Item1);
            }

            var result = new StringBuilder(text);
            for (int i = numbersWithIndex.Count - 1; i >= 0; i--)
            {
                var num = numbersWithIndex[i];
                result.Remove(num.Item2, num.Item3);
                result.Insert(num.Item2, $"[v{i}]");
            }

            return result.ToString();
        }

        public static string RestoreNumbersFromPlaceholders(string text, List<string> numbers)
        {
            if (string.IsNullOrEmpty(text) || numbers == null || numbers.Count == 0)
                return text;

            string result = text;
            for (int i = 0; i < numbers.Count; i++)
            {
                result = result.Replace($"[v{i}]", numbers[i]);
            }
            return result;
        }

        private static bool IsPartOfHexColor(string text, int index, int length)
        {
            for (int i = index - 1; i >= 0 && i >= index - 8; i--)
            {
                char c = text[i];
                if (c == '#')
                    return true;
                if (!IsHexChar(c))
                    break;
            }
            return false;
        }

        private static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        public static void QueueForTranslation(string text, object component = null, bool isOwnUI = false)
        {
            if (!Config.enable_ollama) return;
            if (string.IsNullOrEmpty(text) || text.Length < 3) return;

            lock (lockObj)
            {
                if (component != null)
                {
                    if (!pendingComponents.ContainsKey(text))
                        pendingComponents[text] = new List<object>();
                    pendingComponents[text].Add(component);
                }

                // Note: isOwnUI is determined at processing time by checking pendingComponents
                // This avoids false positives when game text matches mod UI text

                if (pendingTranslations.Contains(text)) return;

                pendingTranslations.Add(text);
                translationQueue.Enqueue(text);

                if (Config.debug_ollama)
                {
                    string preview = text.Length > 40 ? text.Substring(0, 40) + "..." : text;
                    Adapter?.LogInfo($"[Queue] {preview}{(isOwnUI ? " (UI)" : "")}");
                }
            }
        }

        /// <summary>
        /// Main translation method - translate text from cache or queue for Ollama.
        /// Treats multiline text as a single unit to preserve context and ensure consistency.
        /// </summary>
        public static string TranslateText(string text)
        {
            // Check if translations are disabled
            if (!Config.enable_translations)
                return text;

            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // No line splitting - treat multiline as single unit for context preservation
            string result = TranslateSingleText(text);
            if (result != text)
                translatedCount++;
            return result;
        }

        public static string TranslateSingleText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // Normalize line endings FIRST (for cross-platform consistency)
            // Cache keys are stored with normalized line endings (\n only)
            string lineNormalized = NormalizeLineEndings(text);

            // Then extract numbers to placeholders (if enabled)
            string normalizedText = lineNormalized;
            List<string> extractedNumbers = null;
            if (Config.normalize_numbers)
            {
                normalizedText = ExtractNumbersToPlaceholders(lineNormalized, out extractedNumbers);
            }

            // Check cache with NORMALIZED key
            bool foundInCache = false;
            if (TranslationCache.TryGetValue(normalizedText, out var cachedEntry))
            {
                foundInCache = true;
                // H+empty (capture-only) or S (skipped): return original text
                if (cachedEntry.IsHumanEmpty || cachedEntry.Tag == "S")
                {
                    cacheHitCount++;
                    return text;
                }
                if (cachedEntry.Value != normalizedText)
                {
                    cacheHitCount++;
                    translatedCount++;
                    return (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cachedEntry.Value, extractedNumbers)
                        : cachedEntry.Value;
                }
                // If cached == normalizedText, it means "no translation needed", still a cache hit
            }

            // Try trimmed normalized
            string trimmed = normalizedText.Trim();
            if (trimmed != normalizedText && TranslationCache.TryGetValue(trimmed, out var cachedTrimmedEntry))
            {
                foundInCache = true;
                // H+empty (capture-only) or S (skipped): return original text
                if (cachedTrimmedEntry.IsHumanEmpty || cachedTrimmedEntry.Tag == "S")
                {
                    cacheHitCount++;
                    return text;
                }
                if (cachedTrimmedEntry.Value != trimmed)
                {
                    cacheHitCount++;
                    return (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cachedTrimmedEntry.Value, extractedNumbers)
                        : cachedTrimmedEntry.Value;
                }
            }

            // If found in cache with key == value, no translation needed, don't queue
            if (foundInCache)
            {
                return text;
            }

            // Pattern matching (keep for non-number patterns)
            string patternResult = TryPatternMatch(text);
            if (patternResult != null)
            {
                translatedCount++;
                return patternResult;
            }

            if (Config.enable_ollama && !string.IsNullOrEmpty(text) && text.Length >= 3)
            {
                // Check reverse cache with NORMALIZED text (translations are stored normalized)
                if (translatedTexts.Contains(normalizedText))
                {
                    skippedAlreadyTranslated++;
                    return text;
                }

                if (!IsTargetLanguage(text))
                {
                    QueueForTranslation(text);
                }
                else
                {
                    skippedTargetLang++;
                }
            }

            return text;
        }

        /// <summary>
        /// Translate with component tracking for async updates.
        /// Treats multiline text as a single unit to ensure proper component tracking.
        /// </summary>
        /// <param name="isOwnUI">If true, use UI-specific prompt for mod interface translation.</param>
        public static string TranslateTextWithTracking(string text, object component, bool isOwnUI = false)
        {
            // Check if translations are disabled
            if (!Config.enable_translations)
            {
                // Debug: log first time to confirm this check works
                if (_enableTranslationsLogOnce)
                {
                    _enableTranslationsLogOnce = false;
                    LogInfo($"[TranslatorCore] enable_translations=false, skipping translation");
                }
                return text;
            }

            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // Don't split multiline - treat as single unit for proper component tracking
            string result = TranslateSingleTextWithTracking(text, component, isOwnUI);
            if (result != text)
                translatedCount++;
            return result;
        }

        private static string TranslateSingleTextWithTracking(string text, object component, bool isOwnUI = false)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // Normalize line endings FIRST (for cross-platform consistency)
            // Cache keys are stored with normalized line endings (\n only)
            string lineNormalized = NormalizeLineEndings(text);

            // Then extract numbers to placeholders (if enabled)
            string normalizedText = lineNormalized;
            List<string> extractedNumbers = null;
            if (Config.normalize_numbers)
            {
                normalizedText = ExtractNumbersToPlaceholders(lineNormalized, out extractedNumbers);
            }

            string translation = null;

            // Check cache with NORMALIZED key
            bool foundInCache = false;
            if (TranslationCache.TryGetValue(normalizedText, out var cachedEntry))
            {
                foundInCache = true;
                // H+empty (capture-only) or S (skipped): return original text
                if (cachedEntry.IsHumanEmpty || cachedEntry.Tag == "S")
                {
                    cacheHitCount++;
                    return text;
                }
                if (cachedEntry.Value != normalizedText)
                {
                    cacheHitCount++;
                    translatedCount++;
                    // Restore numbers in the translation
                    translation = (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cachedEntry.Value, extractedNumbers)
                        : cachedEntry.Value;
                }
                // If cached == normalizedText, it means "no translation needed", still a cache hit
            }

            // Try trimmed normalized
            if (translation == null && !foundInCache)
            {
                string trimmed = normalizedText.Trim();
                if (trimmed != normalizedText && TranslationCache.TryGetValue(trimmed, out var cachedTrimmedEntry))
                {
                    foundInCache = true;
                    // H+empty (capture-only) or S (skipped): return original text
                    if (cachedTrimmedEntry.IsHumanEmpty || cachedTrimmedEntry.Tag == "S")
                    {
                        cacheHitCount++;
                        return text;
                    }
                    if (cachedTrimmedEntry.Value != trimmed)
                    {
                        cacheHitCount++;
                        translation = (extractedNumbers != null && extractedNumbers.Count > 0)
                            ? RestoreNumbersFromPlaceholders(cachedTrimmedEntry.Value, extractedNumbers)
                            : cachedTrimmedEntry.Value;
                    }
                }
            }

            // Pattern matching no longer needed for numbers (normalized lookup handles it)
            // But keep for other patterns that might exist
            if (translation == null)
            {
                string patternResult = TryPatternMatch(text);
                if (patternResult != null)
                {
                    translatedCount++;
                    translation = patternResult;
                }
            }

            // If we found a translation in cache, return it synchronously
            // This prevents the game from reading back translated text and appending to it
            if (translation != null)
            {
                // Store original text for this component (enables runtime toggle restoration)
                if (component != null)
                {
                    TranslatorScanner.StoreOriginalText(component, text);
                }
                return translation;
            }

            // If found in cache with key == value, no translation needed, don't queue
            if (foundInCache)
            {
                return text;
            }

            // No cache hit - queue for Ollama if enabled
            if (Config.enable_ollama && !string.IsNullOrEmpty(text) && text.Length >= 3)
            {
                // Check reverse cache with NORMALIZED text (translations are stored normalized)
                if (translatedTexts.Contains(normalizedText))
                {
                    skippedAlreadyTranslated++;
                    return text;
                }

                if (!IsTargetLanguage(text))
                {
                    QueueForTranslation(text, component, isOwnUI);
                }
                else
                {
                    skippedTargetLang++;
                }
            }

            return text;
        }

        public static bool IsTargetLanguage(string text)
        {
            // Disabled: too many false positives with mixed-language content
            // The reverse cache (translatedTexts) handles exact matches
            // Ollama can recognize already-translated text and return it unchanged
            return false;
        }

        public static string TryPatternMatch(string text)
        {
            // Quick skip if we already know this text doesn't match any pattern
            if (patternMatchFailures.Contains(text))
                return null;

            foreach (var entry in PatternEntries)
            {
                try
                {
                    var match = entry.MatchRegex.Match(text);
                    if (match.Success)
                    {
                        var capturedValues = new List<string>();
                        for (int i = 1; i < match.Groups.Count; i++)
                        {
                            capturedValues.Add(match.Groups[i].Value);
                        }

                        string result = entry.TranslatedPattern;
                        for (int i = 0; i < entry.PlaceholderIndices.Count && i < capturedValues.Count; i++)
                        {
                            int placeholderIndex = entry.PlaceholderIndices[i];
                            result = result.Replace($"[v{placeholderIndex}]", capturedValues[i]);
                        }

                        return result;
                    }
                }
                catch { }
            }

            // Cache this failure to avoid re-checking all patterns next time
            patternMatchFailures.Add(text);
            return null;
        }

        public static bool IsNumericOrSymbol(string text)
        {
            foreach (char c in text.Trim())
            {
                if (char.IsLetter(c))
                    return false;
            }
            return true;
        }

        public static void ClearLastSeenText()
        {
            lastSeenText.Clear();
        }

        /// <summary>
        /// Clear all processing state caches to force re-evaluation of text.
        /// Call this when settings change (enable_translations, enable_ollama, etc.)
        /// Does NOT clear the translation cache itself.
        /// </summary>
        public static void ClearProcessingCaches()
        {
            // Clear text tracking
            lastSeenText.Clear();

            // Clear Harmony patch cache
            TranslatorPatches.ClearCache();

            // Clear scanner processed cache
            TranslatorScanner.ClearProcessedCache();

            // Clear pattern match failure cache (in case patterns changed)
            patternMatchFailures.Clear();

            // Clear user exclusion cache (instance IDs change between scenes)
            ClearUserExclusionCache();

            Adapter?.LogInfo("[TranslatorCore] Processing caches cleared - text will be re-evaluated");
        }

        public static bool HasSeenText(int id, string text, out string lastText)
        {
            return lastSeenText.TryGetValue(id, out lastText) && lastText == text;
        }

        public static void UpdateSeenText(int id, string text)
        {
            lastSeenText[id] = text;
        }

        public static void SaveCache()
        {
            lock (lockObj)
            {
                try
                {
                    // Create output with metadata first, then sorted translations
                    var output = new JObject();

                    // Metadata
                    output["_uuid"] = FileUuid;

                    if (CurrentGame != null)
                    {
                        output["_game"] = new JObject
                        {
                            ["name"] = CurrentGame.name,
                            ["steam_id"] = CurrentGame.steam_id
                        };
                    }

                    // Save _source with hash for multi-device sync detection
                    if (!string.IsNullOrEmpty(LastSyncedHash))
                    {
                        output["_source"] = new JObject
                        {
                            ["hash"] = LastSyncedHash
                        };
                    }

                    if (LocalChangesCount > 0)
                    {
                        output["_local_changes"] = LocalChangesCount;
                    }

                    // Save user exclusion patterns
                    if (userExclusions.Count > 0)
                    {
                        var exclusionsArray = new JArray();
                        foreach (var pattern in userExclusions)
                        {
                            exclusionsArray.Add(pattern);
                        }
                        output["_exclusions"] = exclusionsArray;
                    }

                    // Save per-font settings
                    if (FontSettingsMap.Count > 0)
                    {
                        var fontsObj = new JObject();
                        foreach (var kvp in FontSettingsMap)
                        {
                            var fontObj = new JObject
                            {
                                ["enabled"] = kvp.Value.enabled,
                                ["fallback"] = kvp.Value.fallback,
                                ["type"] = kvp.Value.type
                            };
                            // Only save scale if not default (1.0)
                            if (Math.Abs(kvp.Value.scale - 1.0f) > 0.001f)
                            {
                                fontObj["scale"] = kvp.Value.scale;
                            }
                            fontsObj[kvp.Key] = fontObj;
                        }
                        output["_fonts"] = fontsObj;
                    }

                    // Save game-specific settings (only if non-default values)
                    if (DisableEventSystemOverride)
                    {
                        output["_settings"] = new JObject
                        {
                            ["disable_eventsystem_override"] = DisableEventSystemOverride
                        };
                    }

                    // Sorted translations with new format {"v": "value", "t": "tag"}
                    var sortedKeys = TranslationCache.Keys.OrderBy(k => k).ToList();
                    foreach (var key in sortedKeys)
                    {
                        var entry = TranslationCache[key];
                        output[key] = new JObject
                        {
                            ["v"] = entry.Value,
                            ["t"] = entry.Tag ?? "A"
                        };
                    }

                    string json = output.ToString(Formatting.Indented);
                    File.WriteAllText(CachePath, json);
                    cacheModified = false;

                    if (DebugMode)
                        Adapter?.LogInfo($"Saved {sortedKeys.Count} cache entries with UUID: {FileUuid}");
                }
                catch (Exception e)
                {
                    Adapter?.LogError($"Failed to save cache: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Creates a new fork by generating a new UUID.
        /// This effectively starts a new lineage separate from any existing server translation.
        /// The current translations are preserved but will be treated as a new upload.
        /// Call with languages from ServerState before it's reset (from downloaded translation).
        /// </summary>
        /// <param name="sourceLanguage">Source language of the forked translation</param>
        /// <param name="targetLanguage">Target language of the forked translation</param>
        public static void CreateFork(string sourceLanguage = null, string targetLanguage = null)
        {
            string oldUuid = FileUuid;

            // Store fork context with languages/game BEFORE resetting ServerState
            // This allows UploadPanel to skip UploadSetupPanel since we already know the context
            PendingFork = new ForkContext
            {
                SourceLanguage = sourceLanguage ?? ServerState?.SourceLanguage,
                TargetLanguage = targetLanguage ?? ServerState?.TargetLanguage,
                Game = CurrentGame
            };

            Adapter?.LogInfo($"[Fork] Context saved: {PendingFork.SourceLanguage} -> {PendingFork.TargetLanguage}, game={PendingFork.Game?.name}");

            // Generate new UUID for the fork
            FileUuid = Guid.NewGuid().ToString();

            // Reset server state - we're starting fresh
            ServerState = new ServerTranslationState();

            // Reset sync tracking - local changes will be counted from this point
            LastSyncedHash = null;
            LocalChangesCount = TranslationCache.Count; // All entries are now "local changes"

            // Clear ancestor cache - no longer relevant for the new lineage
            ClearAncestorCache();

            // Save with new UUID
            SaveCache();

            Adapter?.LogInfo($"Created fork: old UUID {oldUuid} -> new UUID {FileUuid}");
        }

        /// <summary>
        /// Clears the ancestor cache file.
        /// </summary>
        private static void ClearAncestorCache()
        {
            try
            {
                string ancestorPath = CachePath.Replace(".json", ".ancestor.json");
                if (File.Exists(ancestorPath))
                {
                    File.Delete(ancestorPath);
                    AncestorCache.Clear();
                }
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"Failed to clear ancestor cache: {e.Message}");
            }
        }
    }

    public class ModConfig
    {
        // Ollama settings
        public string ollama_url { get; set; } = "http://localhost:11434";
        public string model { get; set; } = "qwen3:8b";
        public string target_language { get; set; } = "auto";
        public string source_language { get; set; } = "auto";
        public bool strict_source_language { get; set; } = false;
        public string game_context { get; set; } = "";
        public int timeout_ms { get; set; } = 30000;
        public bool enable_ollama { get; set; } = false;
        public bool cache_new_translations { get; set; } = true;
        public bool normalize_numbers { get; set; } = true;
        public bool debug_ollama { get; set; } = false;
        public bool preload_model { get; set; } = true;

        // General settings
        public bool capture_keys_only { get; set; } = false;
        public bool translate_mod_ui { get; set; } = false; // Translate the mod's own interface

        // Online mode and sync settings
        public bool first_run_completed { get; set; } = false;
        public bool online_mode { get; set; } = false;
        public bool enable_translations { get; set; } = true;
        public string settings_hotkey { get; set; } = "F10";
        public string api_token { get; set; } = null;
        public string api_user { get; set; } = null;
        // Server URL where the token was issued (for security: invalidate if URL changes)
        public string api_token_server { get; set; } = null;

        // Advanced: Override API URLs (null = use compiled default from Directory.Build.props)
        // For self-hosting or testing. Edit config.json manually to use.
        public string api_base_url { get; set; } = null;
        public string website_base_url { get; set; } = null;

        public SyncConfig sync { get; set; } = new SyncConfig();
        public WindowPreferences window_preferences { get; set; } = new WindowPreferences();

        public string GetTargetLanguage()
        {
            if (string.IsNullOrEmpty(target_language) || target_language.ToLower() == "auto")
            {
                return LanguageHelper.GetSystemLanguageName();
            }
            return target_language;
        }

        public string GetSourceLanguage()
        {
            if (string.IsNullOrEmpty(source_language) || source_language.ToLower() == "auto")
            {
                return null;
            }
            return source_language;
        }
    }

    public class SyncConfig
    {
        public bool check_update_on_start { get; set; } = true;
        public bool auto_download { get; set; } = false;
        public bool notify_updates { get; set; } = true;
        public string merge_strategy { get; set; } = "ask";
        public List<string> ignored_uuids { get; set; } = new List<string>();

        /// <summary>
        /// Check for mod updates on GitHub at startup.
        /// Only works when online_mode is enabled.
        /// </summary>
        public bool check_mod_updates { get; set; } = true;

        /// <summary>
        /// Last known mod version (to avoid notifying about same version again)
        /// </summary>
        public string last_seen_mod_version { get; set; } = null;

        /// <summary>
        /// The mod version we were running when we saw last_seen_mod_version.
        /// Used to re-notify if user upgrades from an old version.
        /// </summary>
        public string last_seen_from_version { get; set; } = null;

        /// <summary>
        /// The published_at timestamp of the last seen release.
        /// Used to detect re-releases with the same version number.
        /// </summary>
        public string last_seen_published_at { get; set; } = null;
    }

    /// <summary>
    /// Per-panel window preferences for persistence across sessions.
    /// Position and size are saved independently.
    /// </summary>
    public class WindowPreference
    {
        /// <summary>Panel X position (anchored position, center-relative)</summary>
        public float x { get; set; }
        /// <summary>Panel Y position (anchored position, center-relative)</summary>
        public float y { get; set; }
        /// <summary>Panel width in pixels</summary>
        public float width { get; set; }
        /// <summary>Panel height in pixels</summary>
        public float height { get; set; }
        /// <summary>True if user manually moved the panel (apply saved position)</summary>
        public bool hasPosition { get; set; }
        /// <summary>True if user manually resized (don't auto-adjust size)</summary>
        public bool userResized { get; set; }
    }

    /// <summary>
    /// Collection of window preferences keyed by panel name.
    /// Screen dimensions are stored globally since all panels share the same screen.
    /// </summary>
    public class WindowPreferences
    {
        /// <summary>Screen width when preferences were last saved</summary>
        public int screenWidth { get; set; }
        /// <summary>Screen height when preferences were last saved</summary>
        public int screenHeight { get; set; }
        /// <summary>Per-panel position and size preferences</summary>
        public Dictionary<string, WindowPreference> panels { get; set; } = new Dictionary<string, WindowPreference>();
    }

    /// <summary>
    /// Server state for current translation (from check-uuid, not persisted to disk)
    /// </summary>
    public class ServerTranslationState
    {
        /// <summary>True if we've checked with the server (even if translation doesn't exist)</summary>
        public bool Checked { get; set; } = false;
        /// <summary>True if translation exists on server</summary>
        public bool Exists { get; set; } = false;
        /// <summary>True if current user owns the translation</summary>
        public bool IsOwner { get; set; } = false;
        /// <summary>Translation ID on server</summary>
        public int? SiteId { get; set; }
        /// <summary>Username of uploader</summary>
        public string Uploader { get; set; }
        /// <summary>File hash on server</summary>
        public string Hash { get; set; }
        /// <summary>Translation type (ai, human, etc.)</summary>
        public string Type { get; set; }
        /// <summary>Translation notes</summary>
        public string Notes { get; set; }

        /// <summary>User's role for this translation</summary>
        public TranslationRole Role { get; set; } = TranslationRole.None;

        /// <summary>If Branch, the username of the Main owner</summary>
        public string MainUsername { get; set; }

        /// <summary>If Main, the number of branches</summary>
        public int BranchesCount { get; set; }

        /// <summary>Source language of the translation (original game language)</summary>
        public string SourceLanguage { get; set; }

        /// <summary>Target language of the translation (translated to)</summary>
        public string TargetLanguage { get; set; }
    }

    /// <summary>
    /// Context for a fork operation. Set before CreateFork() to preserve source translation info.
    /// Cleared after successful upload.
    /// </summary>
    public class ForkContext
    {
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        public GameInfo Game { get; set; }
    }

    /// <summary>
    /// User role relative to a translation on the server.
    /// Determined by comparing UUID and user identity.
    /// </summary>
    public enum TranslationRole
    {
        /// <summary>Not yet uploaded / UUID unknown on server</summary>
        None,
        /// <summary>Owner of this translation (same UUID + same user)</summary>
        Main,
        /// <summary>Contributor to someone else's translation (same UUID + different user)</summary>
        Branch
    }

    /// <summary>
    /// Type of text being translated, used to optimize prompts.
    /// </summary>
    public enum TextType
    {
        /// <summary>Single word (no spaces for Latin, ≤4 chars for CJK)</summary>
        SingleWord,
        /// <summary>Short phrase or sentence</summary>
        Phrase,
        /// <summary>Multiple lines or long text</summary>
        Paragraph
    }

    /// <summary>
    /// A translation entry with value and tag.
    /// JSON format: {"v": "value", "t": "A/H/V"}
    /// </summary>
    public class TranslationEntry
    {
        /// <summary>The translated value</summary>
        public string Value { get; set; } = "";

        /// <summary>
        /// Tag indicating the source of this translation.
        /// A = AI generated, H = Human, V = AI Validated by human,
        /// S = Skipped (wrong source language), M = Mod UI.
        /// Null defaults to A.
        /// </summary>
        public string Tag { get; set; } = "A";

        /// <summary>True if this is a Skipped or Mod UI entry (immutable tags)</summary>
        public bool IsImmutableTag => Tag == "S" || Tag == "M";

        /// <summary>True if Value is null or empty</summary>
        public bool IsEmpty => string.IsNullOrEmpty(Value);

        /// <summary>True if this is a Human-tagged empty entry (capture-only placeholder)</summary>
        public bool IsHumanEmpty => Tag == "H" && IsEmpty;

        /// <summary>
        /// Get the priority of this entry for merge conflict resolution.
        /// Higher priority wins: H empty (0) < A (1) < V (2) < H with value (3) < S/M (99)
        /// S and M are immutable and should never be replaced.
        /// </summary>
        public int Priority
        {
            get
            {
                // Immutable tags (S/M) have highest priority - never replace
                if (IsImmutableTag) return 99;
                if (IsHumanEmpty) return 0;  // H empty = lowest priority
                switch (Tag)
                {
                    case "A": return 1;  // AI
                    case "V": return 2;  // Validated
                    case "H": return 3;  // Human with value
                    default: return 1;   // Default = AI
                }
            }
        }

        /// <summary>
        /// Create a new TranslationEntry from a string value (defaults to AI tag).
        /// </summary>
        public static TranslationEntry FromValue(string value, string tag = "A")
        {
            return new TranslationEntry { Value = value ?? "", Tag = tag ?? "A" };
        }

        /// <summary>
        /// Check if this entry can replace another entry based on tag hierarchy.
        /// S and M tags are immutable and cannot be replaced.
        /// </summary>
        public bool CanReplace(TranslationEntry other)
        {
            if (other == null) return true;
            // Cannot replace immutable tags (S/M) regardless of priority
            if (other.IsImmutableTag) return false;
            return Priority > other.Priority;
        }

        public override string ToString() => $"{Value} [{Tag}]";
    }

    /// <summary>
    /// Game identification info
    /// </summary>
    public class GameInfo
    {
        public string steam_id { get; set; }
        public string name { get; set; }
        public string folder_name { get; set; }
        /// <summary>
        /// How the steam_id was detected: "steam_appid.txt", "appmanifest", or null if not detected
        /// </summary>
        public string detection_method { get; set; }
    }

    /// <summary>
    /// Per-font settings for translation control and fallback fonts.
    /// Stored in translations.json as _fonts for sharing with translations.
    /// </summary>
    public class FontSettings
    {
        /// <summary>
        /// Whether to translate text using this font.
        /// Set to false for bitmap fonts that can't display non-Latin characters.
        /// </summary>
        public bool enabled { get; set; } = true;

        /// <summary>
        /// System font name to use as fallback for missing glyphs.
        /// Only applies to TMP fonts that support fallback.
        /// </summary>
        public string fallback { get; set; }

        /// <summary>
        /// Font type detected: "TMP", "Unity", "TextMesh", "tk2d"
        /// </summary>
        public string type { get; set; }

        /// <summary>
        /// Font size scale factor. 1.0 = original size, 1.5 = 150%, 0.8 = 80%.
        /// Applied to translated text only.
        /// </summary>
        public float scale { get; set; } = 1.0f;
    }
}
