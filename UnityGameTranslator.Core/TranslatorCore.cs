using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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

        private static float lastSaveTime = 0f;
        private static int translatedCount = 0;
        private static int ollamaCount = 0;
        private static int cacheHitCount = 0;
        private static HashSet<string> loggedTranslations = new HashSet<string>();
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

        // Callback for updating components when translation completes
        public static Action<string, string, List<object>> OnTranslationComplete;

        private static readonly Regex NumberPattern = new Regex(@"(?<!\[v)(-?\d+(?:[.,]\d+)?%?)", RegexOptions.Compiled);

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
            loggedTranslations.Clear();

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
                Adapter.LogInfo("Loaded config file");
            }
            catch (Exception e)
            {
                Adapter.LogError($"Failed to load config: {e.Message}");
            }
        }

        private static void LoadCache()
        {
            if (!File.Exists(CachePath))
            {
                Adapter.LogInfo("No cache file found, starting fresh");
                return;
            }

            try
            {
                string json = File.ReadAllText(CachePath);
                TranslationCache = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>();

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
                Adapter.LogInfo($"Loaded {TranslationCache.Count} cached translations, {translatedTexts.Count} reverse entries");
            }
            catch (Exception e)
            {
                Adapter.LogError($"Failed to load cache: {e.Message}");
                TranslationCache = new Dictionary<string, string>();
            }
        }

        public static void BuildPatternEntries()
        {
            PatternEntries.Clear();
            var placeholderRegex = new Regex(@"\[v(\d+)\]");

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
                        MatchRegex = new Regex("^" + pattern + "$", RegexOptions.Compiled),
                        PlaceholderIndices = placeholderIndices
                    });
                }
                catch { }
            }

            if (DebugMode)
                Adapter?.LogInfo($"Built {PatternEntries.Count} pattern entries");
        }

        private static void StartTranslationWorker()
        {
            if (!Config.enable_ollama) return;

            Thread workerThread = new Thread(TranslationWorkerLoop);
            workerThread.IsBackground = true;
            workerThread.Start();
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
                string textToTranslate = null;
                List<object> componentsToUpdate = null;

                lock (lockObj)
                {
                    if (translationQueue.Count > 0)
                    {
                        textToTranslate = translationQueue.Dequeue();
                        if (pendingComponents.TryGetValue(textToTranslate, out var comps))
                        {
                            componentsToUpdate = new List<object>(comps);
                        }
                        if (Config.debug_ollama)
                            Adapter?.LogInfo($"[Worker] Dequeued: {textToTranslate?.Substring(0, Math.Min(30, textToTranslate?.Length ?? 0))}...");
                    }
                }

                if (textToTranslate != null)
                {
                    string originalText = textToTranslate;
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

                        string translation = TranslateWithOllama(normalizedOriginal, extractedNumbers);
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

                    lock (lockObj)
                    {
                        pendingTranslations.Remove(originalText);
                        pendingComponents.Remove(originalText);
                    }
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
        }

        private static string TranslateWithOllama(string textWithPlaceholders, List<string> extractedNumbers)
        {
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
                    options = new { temperature = 0.0, num_predict = 100 }
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

            // Remove explanation blocks that come after double newline
            int doubleNewline = text.IndexOf("\n\n");
            if (doubleNewline > 0)
                text = text.Substring(0, doubleNewline);

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
        /// Main translation method - translate text from cache or queue for Ollama
        /// </summary>
        public static string TranslateText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            if (text.Contains("\n"))
            {
                string[] lines = text.Split('\n');
                bool anyTranslated = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    string translated = TranslateSingleText(trimmed);
                    if (translated != trimmed)
                    {
                        lines[i] = lines[i].Replace(trimmed, translated);
                        anyTranslated = true;
                    }
                }

                if (anyTranslated)
                {
                    translatedCount++;
                    return string.Join("\n", lines);
                }

                return text;
            }

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

            if (TranslationCache.TryGetValue(text, out string cached))
            {
                if (cached != text)
                {
                    cacheHitCount++;
                    translatedCount++;
                    return cached;
                }
            }

            string trimmed = text.Trim();
            if (trimmed != text && TranslationCache.TryGetValue(trimmed, out string cachedTrimmed))
            {
                if (cachedTrimmed != trimmed)
                {
                    cacheHitCount++;
                    return cachedTrimmed;
                }
            }

            string patternResult = TryPatternMatch(text);
            if (patternResult != null)
            {
                translatedCount++;
                return patternResult;
            }

            if (Config.enable_ollama && !string.IsNullOrEmpty(text) && text.Length >= 3)
            {
                if (translatedTexts.Contains(text))
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
        /// Translate with component tracking for async updates
        /// </summary>
        public static string TranslateTextWithTracking(string text, object component)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            if (text.Contains("\n"))
            {
                string[] lines = text.Split('\n');
                bool anyTranslated = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    string translated = TranslateSingleTextWithTracking(trimmed, component);
                    if (translated != trimmed)
                    {
                        lines[i] = lines[i].Replace(trimmed, translated);
                        anyTranslated = true;
                    }
                }

                if (anyTranslated)
                {
                    translatedCount++;
                    return string.Join("\n", lines);
                }

                return text;
            }

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

            if (TranslationCache.TryGetValue(text, out string cached))
            {
                if (cached != text)
                {
                    cacheHitCount++;
                    translatedCount++;
                    return cached;
                }
            }

            string trimmed = text.Trim();
            if (trimmed != text && TranslationCache.TryGetValue(trimmed, out string cachedTrimmed))
            {
                if (cachedTrimmed != trimmed)
                {
                    cacheHitCount++;
                    return cachedTrimmed;
                }
            }

            string patternResult = TryPatternMatch(text);
            if (patternResult != null)
            {
                translatedCount++;
                return patternResult;
            }

            if (Config.enable_ollama && !string.IsNullOrEmpty(text) && text.Length >= 3)
            {
                if (translatedTexts.Contains(text))
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
            if (string.IsNullOrEmpty(text) || text.Length < 8)
                return false;

            string targetLang = Config.GetTargetLanguage().ToLower();

            if (targetLang == "french" || targetLang == "français" || targetLang == "fr")
            {
                if (Regex.IsMatch(text, @"[éèêëàâùûôîç]"))
                    return true;
                if (Regex.IsMatch(text.ToLower(), @"\b(le|la|les|une|des|du|est|sont|vous|nous|cette|nouveau|nouvelle|chargement|sauvegarde|partie|niveau|terminé|joueur)\b"))
                {
                    if (!Regex.IsMatch(text.ToLower(), @"\b(the|this|that|is|are|was|has|have|player|game|level|save|load|health)\b"))
                        return true;
                }
            }
            else if (targetLang == "german" || targetLang == "deutsch" || targetLang == "de")
            {
                if (Regex.IsMatch(text, @"[äöüßÄÖÜ]"))
                    return true;
            }
            else if (targetLang == "spanish" || targetLang == "español" || targetLang == "es")
            {
                if (Regex.IsMatch(text, @"[áéíóúñ¿¡]"))
                    return true;
            }

            return false;
        }

        public static string TryPatternMatch(string text)
        {
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
                    var sorted = new SortedDictionary<string, string>(TranslationCache);
                    string json = JsonConvert.SerializeObject(sorted, Formatting.Indented);
                    File.WriteAllText(CachePath, json);
                    cacheModified = false;

                    if (DebugMode)
                        Adapter?.LogInfo($"Saved {sorted.Count} cache entries");
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

        public string GetTargetLanguage()
        {
            if (string.IsNullOrEmpty(target_language) || target_language.ToLower() == "auto")
            {
                var culture = System.Globalization.CultureInfo.CurrentUICulture;
                return culture.EnglishName.Split('(')[0].Trim();
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
}
