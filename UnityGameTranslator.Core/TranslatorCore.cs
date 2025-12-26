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
        /// Draw a GUI window. Required because IL2CPP needs special delegate handling.
        /// </summary>
        /// <param name="id">Window ID</param>
        /// <param name="rect">Window rectangle</param>
        /// <param name="drawFunc">Draw function (Action&lt;int&gt; where int is window ID)</param>
        /// <param name="title">Window title</param>
        /// <returns>Updated window rectangle (for dragging)</returns>
        UnityEngine.Rect DrawWindow(int id, UnityEngine.Rect rect, System.Action<int> drawFunc, string title);
    }

    /// <summary>
    /// Main translation engine - shared across all mod loaders
    /// </summary>
    public class TranslatorCore
    {
        public static TranslatorCore Instance { get; private set; }
        public static IModLoaderAdapter Adapter { get; private set; }
        public static ModConfig Config { get; private set; } = new ModConfig();
        public static Dictionary<string, string> TranslationCache { get; private set; } = new Dictionary<string, string>();
        public static List<PatternEntry> PatternEntries { get; private set; } = new List<PatternEntry>();
        public static string CachePath { get; private set; }
        public static string ConfigPath { get; private set; }
        public static string ModFolder { get; private set; }
        public static bool DebugMode { get; private set; } = false;
        public static string FileUuid { get; private set; }
        public static GameInfo CurrentGame { get; internal set; }

        /// <summary>
        /// Server state for current translation (populated via check-uuid, not persisted)
        /// </summary>
        public static ServerTranslationState ServerState { get; set; }

        public static int LocalChangesCount { get; private set; } = 0;
        public static Dictionary<string, string> AncestorCache { get; private set; } = new Dictionary<string, string>();

        private static float lastSaveTime = 0f;
        private static int translatedCount = 0;
        private static int ollamaCount = 0;
        private static int cacheHitCount = 0;
        private static Dictionary<int, string> lastSeenText = new Dictionary<int, string>();
        private static HashSet<string> pendingTranslations = new HashSet<string>();
        private static Queue<string> translationQueue = new Queue<string>();
        private static object lockObj = new object();
        private static bool cacheModified = false;
        private static HttpClient httpClient;
        private static int skippedTargetLang = 0;
        private static int skippedAlreadyTranslated = 0;

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

        // Security: Maximum text length for Ollama translation requests (prevents DoS)
        private const int MaxOllamaTextLength = 5000;

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

            Adapter.LogInfo("UnityGameTranslator-Ollama-Qwen3 v1.0 initialized!");
            Adapter.LogInfo($"Ollama: {(Config.enable_ollama ? "ENABLED" : "DISABLED")} - Model: {Config.model}");
            string srcLang = Config.GetSourceLanguage() ?? "auto-detect";
            string tgtLang = Config.GetTargetLanguage();
            Adapter.LogInfo($"Translation: {srcLang} -> {tgtLang}");
            Adapter.LogInfo($"Cache entries: {TranslationCache.Count}, Pattern entries: {PatternEntries.Count}");
        }

        public static void OnSceneChanged(string sceneName)
        {
            lastSeenText.Clear();

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
                    }
                    else
                    {
                        Adapter.LogWarning("Failed to decrypt API token - clearing it");
                        Config.api_token = null;
                    }
                }

                Adapter.LogInfo("Loaded config file");
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
                    ollama_url = Config.ollama_url,
                    model = Config.model,
                    target_language = Config.target_language,
                    source_language = Config.source_language,
                    game_context = Config.game_context,
                    timeout_ms = Config.timeout_ms,
                    enable_ollama = Config.enable_ollama,
                    cache_new_translations = Config.cache_new_translations,
                    normalize_numbers = Config.normalize_numbers,
                    debug_ollama = Config.debug_ollama,
                    preload_model = Config.preload_model,
                    first_run_completed = Config.first_run_completed,
                    online_mode = Config.online_mode,
                    enable_translations = Config.enable_translations,
                    settings_hotkey = Config.settings_hotkey,
                    api_user = Config.api_user,
                    sync = Config.sync,
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

                // Parse as JObject to handle metadata
                var parsed = JObject.Parse(json);
                TranslationCache = new Dictionary<string, string>();

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
                    else if (!prop.Name.StartsWith("_") && prop.Value.Type == JTokenType.String)
                    {
                        TranslationCache[prop.Name] = prop.Value.ToString();
                    }
                }

                // Generate UUID if not present
                if (string.IsNullOrEmpty(FileUuid))
                {
                    FileUuid = Guid.NewGuid().ToString();
                    cacheModified = true;
                    Adapter.LogInfo($"Legacy cache file, generated UUID: {FileUuid}");
                }

                // Load ancestor cache if exists (for 3-way merge support)
                string ancestorPath = CachePath + ".ancestor";
                if (File.Exists(ancestorPath))
                {
                    try
                    {
                        string ancestorJson = File.ReadAllText(ancestorPath);
                        var ancestorParsed = JObject.Parse(ancestorJson);
                        AncestorCache = new Dictionary<string, string>();

                        foreach (var prop in ancestorParsed.Properties())
                        {
                            if (!prop.Name.StartsWith("_") && prop.Value.Type == JTokenType.String)
                            {
                                AncestorCache[prop.Name] = prop.Value.ToString();
                            }
                        }

                        Adapter.LogInfo($"Loaded {AncestorCache.Count} ancestor entries for merge support");
                    }
                    catch (Exception ae)
                    {
                        Adapter.LogWarning($"Failed to load ancestor cache: {ae.Message}");
                        AncestorCache = new Dictionary<string, string>();
                    }
                }

                // Recalculate LocalChangesCount based on actual differences (always, even if no ancestor)
                RecalculateLocalChanges();

                // Build reverse cache: all translated values
                translatedTexts.Clear();
                foreach (var kv in TranslationCache)
                {
                    if (kv.Key != kv.Value && !string.IsNullOrEmpty(kv.Value))
                    {
                        translatedTexts.Add(kv.Value);
                    }
                }

                BuildPatternEntries();
                Adapter.LogInfo($"Loaded {TranslationCache.Count} cached translations, {translatedTexts.Count} reverse entries, UUID: {FileUuid}");
            }
            catch (Exception e)
            {
                Adapter.LogError($"Failed to load cache: {e.Message}");
                TranslationCache = new Dictionary<string, string>();
                FileUuid = Guid.NewGuid().ToString();
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
                var data = TranslationCache.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value);
                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(ancestorPath, json);
                AncestorCache = new Dictionary<string, string>(TranslationCache);
                LocalChangesCount = 0;
                Adapter.LogInfo($"Saved ancestor cache with {AncestorCache.Count} entries");
            }
            catch (Exception e)
            {
                Adapter.LogWarning($"Failed to save ancestor cache: {e.Message}");
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

                // New key or different value = local change
                if (!AncestorCache.TryGetValue(kvp.Key, out var ancestorValue) || ancestorValue != kvp.Value)
                {
                    changes++;
                }
            }

            LocalChangesCount = changes;
            Adapter?.LogInfo($"[LocalChanges] Recalculated: {changes} local changes");
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
                    // TranslationCache already contains only translation entries (no metadata)
                    sortedDict[kvp.Key] = kvp.Value;
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
                if (kv.Key == kv.Value) continue;

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
                        TranslatedPattern = kv.Value,
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
                    try
                    {
                        if (Config.debug_ollama)
                            Adapter?.LogInfo($"[Worker] Calling Ollama...");

                        // Extract numbers BEFORE sending to Ollama
                        string normalizedOriginal = textToTranslate;
                        List<string> extractedNumbers = null;
                        if (Config.normalize_numbers)
                        {
                            normalizedOriginal = ExtractNumbersToPlaceholders(textToTranslate, out extractedNumbers);
                        }

                        // Check cache first (another request might have already translated this)
                        string translation = null;
                        if (TranslationCache.TryGetValue(normalizedOriginal, out string alreadyCached))
                        {
                            if (alreadyCached != normalizedOriginal)
                            {
                                translation = alreadyCached;
                                if (Config.debug_ollama)
                                    Adapter?.LogInfo($"[Worker] Cache hit for normalized text, skipping Ollama");
                            }
                        }

                        // Only call Ollama if not in cache
                        if (translation == null)
                        {
                            translation = TranslateWithOllama(normalizedOriginal, extractedNumbers);
                        }
                        if (Config.debug_ollama)
                            Adapter?.LogInfo($"[Worker] Ollama returned: {translation?.Substring(0, Math.Min(30, translation?.Length ?? 0))}...");

                        if (!string.IsNullOrEmpty(translation))
                        {
                            // Always cache (even if unchanged) to avoid re-queuing the same text
                            AddToCache(normalizedOriginal, translation);

                            if (translation != normalizedOriginal)
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

        private static string TranslateWithOllama(string textWithPlaceholders, List<string> extractedNumbers)
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

                // Build system prompt
                var promptBuilder = new StringBuilder();
                string targetLang = Config.GetTargetLanguage();
                string sourceLang = Config.GetSourceLanguage();

                if (sourceLang != null)
                    promptBuilder.Append($"You are a video game UI translator from {sourceLang} to {targetLang}. ");
                else
                    promptBuilder.Append($"You are a video game UI translator to {targetLang}. ");

                promptBuilder.Append("Output ONLY the translation, nothing else. ");
                promptBuilder.Append("Keep it concise for UI. Preserve the original tone and style. ");
                promptBuilder.Append("Preserve formatting tags and special characters. ");
                if (extractedNumbers != null && extractedNumbers.Count > 0)
                {
                    promptBuilder.Append("IMPORTANT: Keep [v0], [v1], etc. placeholders exactly as-is (they represent numbers). ");
                }
                promptBuilder.Append("For single words: translate if it's game content (items, actions, stats). ");
                promptBuilder.Append("Keep unchanged: language names (English, French...), keyboard keys (Tab, Esc, Space...), technical settings (VSync, Auto as setting value). ");

                if (!string.IsNullOrEmpty(Config.game_context))
                {
                    promptBuilder.Append($"Game context: {Config.game_context}.");
                }

                var requestBody = new
                {
                    model = Config.model,
                    messages = new object[]
                    {
                        new { role = "system", content = promptBuilder.ToString() },
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

        public static void AddToCache(string original, string translated)
        {
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(translated))
                return;

            lock (lockObj)
            {
                string cacheKey = original;
                string cacheValue = translated;

                if (TranslationCache.ContainsKey(cacheKey))
                    return;

                TranslationCache[cacheKey] = cacheValue;
                cacheModified = true;

                // Track local changes (if different from ancestor or new)
                if (AncestorCache.Count > 0)
                {
                    if (!AncestorCache.TryGetValue(cacheKey, out var ancestorValue) || ancestorValue != cacheValue)
                    {
                        LocalChangesCount++;
                    }
                }
                else
                {
                    // No ancestor = all translations are local
                    LocalChangesCount++;
                }

                // Add to reverse cache
                if (cacheKey != cacheValue && !string.IsNullOrEmpty(cacheValue))
                {
                    translatedTexts.Add(cacheValue);
                }

                // Note: No longer clearing lastSeenText here.
                // OnTranslationComplete updates tracked components directly.
                // New components will be translated on their next scan cycle.

                if (cacheKey.Contains("[v"))
                {
                    BuildPatternEntries();
                }

                if (DebugMode)
                    Adapter?.LogInfo($"[Cache+] {cacheKey.Substring(0, Math.Min(40, cacheKey.Length))}...");
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

        public static void QueueForTranslation(string text, object component = null)
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

                if (pendingTranslations.Contains(text)) return;

                pendingTranslations.Add(text);
                translationQueue.Enqueue(text);

                if (Config.debug_ollama)
                {
                    string preview = text.Length > 40 ? text.Substring(0, 40) + "..." : text;
                    Adapter?.LogInfo($"[Queue] {preview}");
                }
            }
        }

        /// <summary>
        /// Main translation method - translate text from cache or queue for Ollama.
        /// Treats multiline text as a single unit to preserve context and ensure consistency.
        /// </summary>
        public static string TranslateText(string text)
        {
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

            // Normalize FIRST (extract numbers to placeholders)
            string normalizedText = text;
            List<string> extractedNumbers = null;
            if (Config.normalize_numbers)
            {
                normalizedText = ExtractNumbersToPlaceholders(text, out extractedNumbers);
            }

            // Check cache with NORMALIZED key
            bool foundInCache = false;
            if (TranslationCache.TryGetValue(normalizedText, out string cached))
            {
                foundInCache = true;
                if (cached != normalizedText)
                {
                    cacheHitCount++;
                    translatedCount++;
                    return (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cached, extractedNumbers)
                        : cached;
                }
                // If cached == normalizedText, it means "no translation needed", still a cache hit
            }

            // Try trimmed normalized
            string trimmed = normalizedText.Trim();
            if (trimmed != normalizedText && TranslationCache.TryGetValue(trimmed, out string cachedTrimmed))
            {
                foundInCache = true;
                if (cachedTrimmed != trimmed)
                {
                    cacheHitCount++;
                    return (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cachedTrimmed, extractedNumbers)
                        : cachedTrimmed;
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
        public static string TranslateTextWithTracking(string text, object component)
        {
            // Check if translations are disabled
            if (!Config.enable_translations)
                return text;

            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // Don't split multiline - treat as single unit for proper component tracking
            string result = TranslateSingleTextWithTracking(text, component);
            if (result != text)
                translatedCount++;
            return result;
        }

        private static string TranslateSingleTextWithTracking(string text, object component)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // Normalize FIRST (extract numbers to placeholders)
            string normalizedText = text;
            List<string> extractedNumbers = null;
            if (Config.normalize_numbers)
            {
                normalizedText = ExtractNumbersToPlaceholders(text, out extractedNumbers);
            }

            string translation = null;

            // Check cache with NORMALIZED key
            bool foundInCache = false;
            if (TranslationCache.TryGetValue(normalizedText, out string cached))
            {
                foundInCache = true;
                if (cached != normalizedText)
                {
                    cacheHitCount++;
                    translatedCount++;
                    // Restore numbers in the translation
                    translation = (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cached, extractedNumbers)
                        : cached;
                }
                // If cached == normalizedText, it means "no translation needed", still a cache hit
            }

            // Try trimmed normalized
            if (translation == null && !foundInCache)
            {
                string trimmed = normalizedText.Trim();
                if (trimmed != normalizedText && TranslationCache.TryGetValue(trimmed, out string cachedTrimmed))
                {
                    foundInCache = true;
                    if (cachedTrimmed != trimmed)
                    {
                        cacheHitCount++;
                        translation = (extractedNumbers != null && extractedNumbers.Count > 0)
                            ? RestoreNumbersFromPlaceholders(cachedTrimmed, extractedNumbers)
                            : cachedTrimmed;
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
                    QueueForTranslation(text, component);
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
                    var output = new Dictionary<string, object>();

                    // Metadata
                    output["_uuid"] = FileUuid;

                    if (CurrentGame != null)
                    {
                        output["_game"] = new Dictionary<string, string>
                        {
                            ["name"] = CurrentGame.name,
                            ["steam_id"] = CurrentGame.steam_id
                        };
                    }

                    // Note: _source is no longer persisted - server state is fetched via check-uuid at startup
                    // Hash is computed on-demand via ComputeContentHash()

                    if (LocalChangesCount > 0)
                    {
                        output["_local_changes"] = LocalChangesCount;
                    }

                    // Sorted translations
                    var sorted = new SortedDictionary<string, string>(TranslationCache);
                    foreach (var kv in sorted)
                    {
                        output[kv.Key] = kv.Value;
                    }

                    string json = JsonConvert.SerializeObject(output, Formatting.Indented);
                    File.WriteAllText(CachePath, json);
                    cacheModified = false;

                    if (DebugMode)
                        Adapter?.LogInfo($"Saved {sorted.Count} cache entries with UUID: {FileUuid}");
                }
                catch (Exception e)
                {
                    Adapter?.LogError($"Failed to save cache: {e.Message}");
                }
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
        public string game_context { get; set; } = "";
        public int timeout_ms { get; set; } = 30000;
        public bool enable_ollama { get; set; } = false;
        public bool cache_new_translations { get; set; } = true;
        public bool normalize_numbers { get; set; } = true;
        public bool debug_ollama { get; set; } = false;
        public bool preload_model { get; set; } = true;

        // Online mode and sync settings
        public bool first_run_completed { get; set; } = false;
        public bool online_mode { get; set; } = false;
        public bool enable_translations { get; set; } = true;
        public string settings_hotkey { get; set; } = "F10";
        public string api_token { get; set; } = null;
        public string api_user { get; set; } = null;
        public SyncConfig sync { get; set; } = new SyncConfig();

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
    }

    /// <summary>
    /// Game identification info
    /// </summary>
    public class GameInfo
    {
        public string steam_id { get; set; }
        public string name { get; set; }
        public string folder_name { get; set; }
    }
}
