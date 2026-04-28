using System;
using System.Collections.Concurrent;
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
using UnityGameTranslator.Core.UI;

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
        public static volatile bool ShuttingDown;
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

        /// <summary>
        /// True when metadata (fonts, images, exclusions) has been modified locally since last upload.
        /// Included in sync direction calculation so metadata changes trigger an upload prompt.
        /// </summary>
        public static bool MetadataDirty { get; private set; } = false;
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

        /// <summary>Detect typewriting effects (text appearing letter by letter). Stored in translations.json.</summary>
        public static bool TypewritingDetection { get; set; } = true;
        /// <summary>Detect procedural text building (tooltips, item descriptions). Stored in translations.json.</summary>
        public static bool ConcatDetection { get; set; } = true;

        /// <summary>
        /// Cached flag for whether the current AI provider supports the "think" parameter.
        /// null = unknown (will try), true = supported, false = not supported (got 400).
        /// Invalidated when ai_url or ai_model changes.
        /// </summary>
        private static bool? _providerSupportsThinkParam = null;
        private static string _thinkParamCacheKey = null;

        /// <summary>
        /// Build a cache key from current AI config to detect provider/model changes.
        /// </summary>
        private static string GetThinkParamCacheKey()
        {
            return $"{Config?.ai_url}|{Config?.ai_model}";
        }

        /// <summary>
        /// Check if the think parameter should be sent based on cached provider capability.
        /// Returns true if we should include "think": false in the request.
        /// </summary>
        private static bool ShouldSendThinkParam()
        {
            string currentKey = GetThinkParamCacheKey();
            if (_thinkParamCacheKey != currentKey)
            {
                // Provider or model changed, reset cache
                _providerSupportsThinkParam = null;
                _thinkParamCacheKey = currentKey;
            }
            // Send if supported or unknown (optimistic)
            return _providerSupportsThinkParam != false;
        }

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
        private static int aiTranslationCount = 0;
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
        private static ConcurrentDictionary<string, byte> translatedTexts = new ConcurrentDictionary<string, byte>();

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

        // Font overrides (per-pattern font/size rules) - stored in translations.json as _font_overrides
        private static List<FontOverrideRule> fontOverrides = new List<FontOverrideRule>();
        private static Dictionary<int, FontOverrideRule> fontOverrideCache = new Dictionary<int, FontOverrideRule>();

        /// <summary>
        /// Current font override rules. Read-only access for UI.
        /// </summary>
        public static IReadOnlyList<FontOverrideRule> FontOverrides => fontOverrides;

        /// <summary>
        /// Find the first matching font override rule for a component.
        /// Uses caching by component instance ID. Returns null if no override matches.
        /// </summary>
        /// <param name="componentId">Component instance ID for caching</param>
        /// <param name="gameObjectPath">Hierarchy path (e.g. "Canvas/Panel/Text")</param>
        /// <param name="fontName">Current font name</param>
        /// <param name="textContent">Current text content</param>
        public static FontOverrideRule FindFontOverride(int componentId, string gameObjectPath, string fontName, string textContent)
        {
            // Check cache first
            if (fontOverrideCache.TryGetValue(componentId, out var cached))
                return cached;

            FontOverrideRule matched = null;
            for (int i = 0; i < fontOverrides.Count; i++)
            {
                var rule = fontOverrides[i];
                if (!rule.enabled) continue;
                if (MatchesFontOverride(rule, gameObjectPath, fontName, textContent))
                {
                    matched = rule;
                    break; // First match wins
                }
            }

            fontOverrideCache[componentId] = matched;
            return matched;
        }

        /// <summary>
        /// Test if a font override rule matches the given context.
        /// </summary>
        private static bool MatchesFontOverride(FontOverrideRule rule, string path, string fontName, string text)
        {
            string match = rule.match;
            if (string.IsNullOrEmpty(match)) return false;

            // Prefix-based matching
            if (match.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = match.Substring(5);
                return !string.IsNullOrEmpty(path) && MatchesExclusionPattern(path, pattern);
            }
            if (match.StartsWith("font:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = match.Substring(5);
                return !string.IsNullOrEmpty(fontName) &&
                       string.Equals(fontName, pattern, StringComparison.OrdinalIgnoreCase);
            }
            if (match.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = match.Substring(5);
                if (string.IsNullOrEmpty(text)) return false;
                // Regex if wrapped in /.../
                if (pattern.StartsWith("/") && pattern.EndsWith("/") && pattern.Length > 2)
                {
                    string regex = pattern.Substring(1, pattern.Length - 2);
                    try { return System.Text.RegularExpressions.Regex.IsMatch(text, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                    catch { return false; }
                }
                return text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            // No prefix: try path first, then text substring
            if (!string.IsNullOrEmpty(path) && MatchesExclusionPattern(path, match))
                return true;
            if (!string.IsNullOrEmpty(text) && text.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        /// <summary>
        /// Replace all font override rules at once (called by Apply in UI).
        /// Marks metadata dirty but does NOT save — caller should save after all changes.
        /// </summary>
        public static void SetFontOverrides(List<FontOverrideRule> rules)
        {
            fontOverrides.Clear();
            fontOverrides.AddRange(rules);
            fontOverrideCache.Clear();
            FontManager.ClearComponentScaleOverrides();
            // Clear font size caches so ApplyFontScale re-reads from true originals
            TranslatorPatches.ClearFontSizeCache();
            // Clear last-translated cache so ForceRefreshAllText doesn't early-return
            // before reaching ApplyFontScale (the "already translated" check skips font scale)
            TranslatorPatches.ClearLastTranslatedCache();
            SetMetadataDirty();
        }

        /// <summary>
        /// Clear the font override cache (call on scene change).
        /// </summary>
        public static void ClearFontOverrideCache()
        {
            fontOverrideCache.Clear();
        }

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
            {
                ownUIPanelRoots.Add(panelRoot.GetInstanceID());
                // Clear hierarchy cache — components checked before this registration
                // may have been cached as "not own UI" and need re-evaluation
                _ownUIHierarchyCache.Clear();
            }
        }

        // Cache for IsOwnUIByHierarchy results (avoids repeated hierarchy traversal)
        // Key: component instanceId, Value: is own UI
        // Cleared on scene change (components become invalid)
        private static readonly Dictionary<int, bool> _ownUIHierarchyCache = new Dictionary<int, bool>();

        /// <summary>
        /// Check if a component is part of our UI by traversing up the hierarchy.
        /// Returns true if any parent is a registered panel root.
        /// Results are cached per instanceId to avoid repeated traversal.
        /// </summary>
        public static bool IsOwnUIByHierarchy(Component component)
        {
            if (component == null) return false;

            int id = component.GetInstanceID();
            if (_ownUIHierarchyCache.TryGetValue(id, out bool cached))
                return cached;

            bool result = false;
            Transform current = component.transform;
            while (current != null)
            {
                if (ownUIPanelRoots.Contains(current.gameObject.GetInstanceID()))
                {
                    result = true;
                    break;
                }
                current = current.parent;
            }

            // Only cache positive results — negative can change when new panel roots are registered
            if (result)
                _ownUIHierarchyCache[id] = true;
            return result;
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
                LogDebug($"[Exclusion] Matched: {path}");

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
        /// An exact path also matches all children (excluding "Canvas/Panel" excludes "Canvas/Panel/Text").
        /// </summary>
        /// <summary>
        /// Find the original key for a translated text value.
        /// Searches TranslationCache for an entry whose Value matches the given translated text.
        /// Returns null if not found (text might be the original itself).
        /// </summary>
        public static string FindOriginalKey(string translatedText)
        {
            if (string.IsNullOrEmpty(translatedText)) return null;

            foreach (var kvp in TranslationCache)
            {
                if (kvp.Value != null && kvp.Value.Value == translatedText)
                    return kvp.Key;
            }
            return null;
        }

        public static bool MatchesExclusionPattern(string path, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            // Exact path exclusions implicitly exclude all children:
            // Pattern "Canvas/Panel" should match "Canvas/Panel", "Canvas/Panel/Child", "Canvas/Panel/Child/Text"
            // Only apply this when pattern has no wildcards (pure path exclusion)
            if (!pattern.Contains("*"))
            {
                if (string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Check if path is a child of the excluded path
                if (path.Length > pattern.Length && path[pattern.Length] == '/' &&
                    path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }

            // Wildcard pattern matching
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
        /// Mark metadata as modified (fonts, images, exclusions).
        /// Triggers upload detection on next sync check.
        /// </summary>
        public static void SetMetadataDirty()
        {
            if (!MetadataDirty)
            {
                MetadataDirty = true;
                LogDebug("[Sync] Metadata marked dirty");
            }
        }

        /// <summary>
        /// Reset metadata dirty flag after successful upload.
        /// </summary>
        public static void ResetMetadataDirty()
        {
            MetadataDirty = false;
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
                SetMetadataDirty();
                SaveCache();
                LogDebug($"[Exclusion] Added: {pattern}");
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
                SetMetadataDirty();
                SaveCache();
                LogDebug($"[Exclusion] Removed: {pattern}");
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
            LogDebug("[Exclusion] Cleared all exclusions");
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

        // Security: Maximum text length for AI translation requests (prevents DoS)
        private const int MaxAITextLength = 15000;

        // Marker for skipped translations (text not in expected source language)
        private const string SkipTranslationMarker = "AxNoTranslateXa";

        // Placeholder format for extracted numbers: [!v*0], [!v*1], etc.
        // Exotic format to avoid collision with game text (e.g. [v0] used by some games).
        private const string PlaceholderPrefix = "[!v*";
        private const string PlaceholderSuffix = "]";
        // Current engine version for migration support
        private const int CurrentEngineVersion = 1;

        private static readonly Regex NumberPattern = new Regex(
            @"(?<!\[!v\*)(-?\d+(?:[.,]\d+)?%?)",
            RegexOptions.Compiled);

        // Matches any XML/HTML-like tag: <tag>, </tag>, <tag attr="val">, <tag/>, etc.
        private static readonly Regex MarkupTagPattern = new Regex(
            @"<[^>]+>",
            RegexOptions.Compiled);

        private const string TagPlaceholderPrefix = "[!t*";
        private const string TagPlaceholderSuffix = "]";

        /// <summary>
        /// Extract markup tags from text, replacing them with [!t*N] placeholders.
        /// Returns the processed text and the list of extracted tags.
        /// </summary>
        public static string ExtractMarkupTags(string text, out List<string> extractedTags)
        {
            extractedTags = new List<string>();
            if (string.IsNullOrEmpty(text))
                return text;

            var matches = MarkupTagPattern.Matches(text);
            if (matches.Count == 0)
                return text;

            var result = new StringBuilder(text.Length);
            int lastIndex = 0;

            foreach (Match match in matches)
            {
                // Append text before this tag
                result.Append(text, lastIndex, match.Index - lastIndex);
                // Replace tag with placeholder
                int tagIndex = extractedTags.Count;
                extractedTags.Add(match.Value);
                result.Append(TagPlaceholderPrefix).Append(tagIndex).Append(TagPlaceholderSuffix);
                lastIndex = match.Index + match.Length;
            }

            // Append remaining text after last tag
            result.Append(text, lastIndex, text.Length - lastIndex);
            return result.ToString();
        }

        /// <summary>
        /// Restore [!t*N] placeholders back to their original markup tags.
        /// </summary>
        public static string RestoreMarkupTags(string text, List<string> tags)
        {
            if (string.IsNullOrEmpty(text) || tags == null || tags.Count == 0)
                return text;

            string result = text;
            for (int i = 0; i < tags.Count; i++)
            {
                result = result.Replace($"{TagPlaceholderPrefix}{i}{TagPlaceholderSuffix}", tags[i]);
            }
            return result;
        }

        /// <summary>
        /// Validate that all [!t*N] placeholders are present in the AI response.
        /// Logs warnings for missing tags but does not reject the translation.
        /// </summary>
        private static void ValidateMarkupTags(string translation, int expectedCount, string originalText)
        {
            if (expectedCount == 0) return;

            int missing = 0;
            for (int i = 0; i < expectedCount; i++)
            {
                if (!translation.Contains($"{TagPlaceholderPrefix}{i}{TagPlaceholderSuffix}"))
                    missing++;
            }

            if (missing > 0)
            {
                Adapter?.LogWarning($"[AI] Translation missing {missing}/{expectedCount} tag placeholders for: {originalText.Substring(0, Math.Min(60, originalText.Length))}...");
            }
        }

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

            LoadConfig();
            DebugMode = Config.debug;

            // Initialize type resolution (must be before patches and scanning)
            TypeHelper.Initialize();

            // Initialize font manager for non-Latin script support
            FontManager.Initialize();

            // Initialize custom font loader (user-provided SDF fonts)
            CustomFontLoader.Initialize(ModFolder);

            // Initialize image replacer for bitmap text translation
            ImageReplacer.Initialize(ModFolder);

            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            // Detect game
            CurrentGame = GameDetector.DetectGame();
            if (CurrentGame != null)
            {
                Adapter.LogInfo($"Detected game: {CurrentGame.name} (Steam: {CurrentGame.steam_id ?? "N/A"})");
            }

            LoadCache();

            // Pre-load configured fallback fonts so they're ready for first use
            FontManager.PreloadConfiguredFallbacks();

            StartTranslationWorker();

            if (Config.preload_model && Config.enable_ai && Config.translation_backend == "llm")
            {
                PreloadModel();
            }

            Adapter.LogInfo($"UnityGameTranslator v{PluginInfo.Version} initialized!");
            if (Config.IsTranslationEnabled)
            {
                string backendName = Config.translation_backend == "llm"
                    ? $"LLM ({Config.ai_model} @ {Config.ai_url})"
                    : Config.translation_backend == "google"
                        ? "Google Translate"
                        : Config.translation_backend == "deepl"
                            ? $"DeepL ({(Config.deepl_use_free ? "Free" : "Pro")})"
                            : Config.translation_backend;
                Adapter.LogInfo($"Translation: ENABLED - Backend: {backendName}");
            }
            string srcLang = Config.GetSourceLanguage() ?? "auto-detect";
            string tgtLang = Config.GetTargetLanguage();
            Adapter.LogInfo($"Translation: {srcLang} -> {tgtLang}");
            Adapter.LogInfo($"Cache entries: {TranslationCache.Count}, Pattern entries: {PatternEntries.Count}");
        }

        public static void OnSceneChanged(string sceneName)
        {
            lastSeenText.Clear();
            _ownUIHierarchyCache.Clear();
            TranslatorScanner.OnSceneChange();
            TranslatorPatches.ClearCache();

            FontManager.OnSceneChanged();

            // Load/reload image replacements for the new scene
            ImageReplacer.OnSceneChange();

            // Flag variables for refresh on next text request
            // (instances don't exist yet at scene change time)
            VariableManager.MarkNeedsRefresh();

            if (DebugMode)
                Adapter?.LogInfo($"Scene: {sceneName}");
        }

        /// <summary>
        /// Called when a scene is unloaded. Cleans up stale references from the old scene.
        /// </summary>
        public static void OnSceneUnloaded(string sceneName)
        {
            // Clean dead refs immediately (don't wait for periodic cleanup)
            FontManager.CleanDeadComponentRefs();
            TranslatorPatches.CleanDeadRefs();
            TranslatorPatches.ClearTypewritingState();

            if (DebugMode)
                Adapter?.LogInfo($"Scene unloaded: {sceneName}");
        }

        public static void OnShutdown()
        {
            ShuttingDown = true;
            Adapter?.LogInfo("[Shutdown] Starting cleanup...");

            // Stop SSE streams (background tasks with HTTP connections)
            try { TranslatorUIManager.StopSyncStream(); } catch { }
            try { TranslatorUIManager.StopMergeCompletionListener(); } catch { }

            // Stop the LateUpdate coroutine
            try { TranslatorScanner.StopLateUpdateRunner(); } catch { }

            // Wait briefly for worker thread to notice ShuttingDown flag and exit
            if (workerRunning)
            {
                for (int i = 0; i < 20 && workerRunning; i++)
                    Thread.Sleep(10);
            }

            // Dispose HttpClient (cancels in-flight requests)
            try { httpClient?.Dispose(); } catch { }

            if (cacheModified)
            {
                try { SaveCache(); } catch { }
            }

            Adapter?.LogInfo($"Session: {translatedCount} translations, {cacheHitCount} cache hits, {aiTranslationCount} AI calls");
            Adapter?.LogInfo($"Skipped: {skippedTargetLang} (target lang heuristic), {skippedAlreadyTranslated} (reverse cache)");
            Adapter?.LogInfo("[Shutdown] Cleanup complete");
        }

        public static void OnUpdate(float currentTime)
        {
            // Feed the scanner's adaptive frame-time budget on every frame.
            // The scanner uses recent frame-time variance to size its per-frame work budget.
            TranslatorScanner.RecordFrameTime();

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
        /// <summary>
        /// Log only when debug mode is enabled (config.debug=true or debug.txt exists).
        /// Use for verbose/diagnostic messages that normal users don't need.
        /// </summary>
        public static void LogDebug(string message) { if (DebugMode) Adapter?.LogInfo(message); }

        #endregion

        #region Config persistence

        /// <summary>
        /// Names of token fields on ModConfig that are encrypted via [JsonConverter(typeof(EncryptedTokenConverter))].
        /// Used in LoadConfig to inspect raw JSON and detect decryption failures or legacy plaintext that needs re-encryption.
        /// Keep in sync with the [JsonConverter] annotations on ModConfig properties.
        /// </summary>
        private static readonly string[] EncryptedTokenFieldNames = new[]
        {
            "api_token",
            "ai_api_key",
            "google_api_key",
            "deepl_api_key",
        };

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

                // Parse raw JSON for inspection BEFORE deserialization. The deserialization
                // step decrypts encrypted fields via [JsonConverter(typeof(EncryptedTokenConverter))],
                // so we lose visibility into what was on disk. We need the raw values to detect:
                //   - decryption failures (raw had value, in-memory becomes null)
                //   - legacy plaintext / unprefixed tokens that need re-encryption on next save
                //   - missing newly-introduced fields that should be materialized on disk
                JObject rawJson;
                try
                {
                    rawJson = JObject.Parse(json);
                }
                catch (Exception parseEx)
                {
                    // Malformed JSON: fall through to deserialization which will raise a clearer error
                    Adapter.LogWarning($"Config JSON is malformed for inspection ({parseEx.Message}); proceeding without raw inspection");
                    rawJson = new JObject();
                }

                Config = JsonConvert.DeserializeObject<ModConfig>(json) ?? new ModConfig();

                bool needsResave = false;

                // Inspect each encrypted-token field. The converter has already populated Config
                // with plaintext (or null on failure); the raw JSON tells us what was on disk.
                foreach (var fieldName in EncryptedTokenFieldNames)
                {
                    string rawValue = rawJson[fieldName]?.Value<string>();
                    if (string.IsNullOrEmpty(rawValue))
                    {
                        continue;
                    }

                    string inMemoryValue = GetTokenFieldValue(Config, fieldName);

                    if (string.IsNullOrEmpty(inMemoryValue))
                    {
                        // Decryption failed (machine identity changed, corrupted ciphertext, key algo bumped).
                        // Converter has already returned null; we resave to clear the bad ciphertext from disk.
                        Adapter.LogWarning($"Failed to decrypt {fieldName} - clearing it");
                        needsResave = true;
                    }
                    else if (TokenProtection.NeedsReEncryption(rawValue))
                    {
                        // Legacy plaintext (ugt_ prefix) or unprefixed token: in-memory holds it as-is,
                        // next save will wrap it with the ENCRYPTED: prefix via the converter.
                        LogDebug($"Migrated legacy/unencrypted {fieldName} to encrypted storage");
                        needsResave = true;
                    }
                }

                // Security: Invalidate API token if the issuing server URL changed (replay attack prevention).
                // Runs after the converter has decrypted the token; we compare against the URL stored at issue time.
                if (!string.IsNullOrEmpty(Config.api_token))
                {
                    string currentApiUrl = Config.api_base_url ?? PluginInfo.ApiBaseUrl;
                    if (!string.IsNullOrEmpty(Config.api_token_server) &&
                        Config.api_token_server != currentApiUrl)
                    {
                        Adapter.LogWarning($"[Security] API URL changed from {Config.api_token_server} to {currentApiUrl} - invalidating token to prevent replay attacks");
                        Config.api_token = null;
                        Config.api_user = null;
                        Config.api_token_server = null;
                        needsResave = true;
                    }
                }

                if (Config._configMigrated)
                {
                    LogDebug($"[Config] Migrated old Ollama config -> AI config (enable_ai={Config.enable_ai}, ai_url={Config.ai_url}, ai_model={Config.ai_model})");
                    needsResave = true;
                }

                // Materialize newly-introduced fields so users can see them in config.json on first load.
                // When a new ModConfig field is added, deserialization fills it with its default value
                // but the existing file does not contain the JSON property; saving once writes it.
                if (rawJson["max_text_detection_latency_seconds"] == null)
                {
                    needsResave = true;
                }

                if (needsResave)
                {
                    SaveConfig();
                }

                LogDebug($"Loaded config (enable_translations={Config.enable_translations}, backend={Config.translation_backend}, ai_url={Config.ai_url}, ai_model={Config.ai_model})");
            }
            catch (Exception e)
            {
                Adapter.LogError($"Failed to load config: {e.Message}");
            }
        }

        /// <summary>
        /// Token-field accessor used by LoadConfig's raw-JSON inspection loop.
        /// Centralized to avoid drift if the field names change.
        /// </summary>
        private static string GetTokenFieldValue(ModConfig config, string fieldName)
        {
            switch (fieldName)
            {
                case "api_token":      return config.api_token;
                case "ai_api_key":     return config.ai_api_key;
                case "google_api_key": return config.google_api_key;
                case "deepl_api_key":  return config.deepl_api_key;
                default:
                    Adapter?.LogWarning($"[Config] Unknown encrypted token field: {fieldName}");
                    return null;
            }
        }

        public static void SaveConfig()
        {
            try
            {
                // Token fields with [JsonConverter(typeof(EncryptedTokenConverter))] are encrypted
                // on serialization automatically. No manual field-by-field copy is needed —
                // adding a new ModConfig field will be persisted on the next save without changing this method.
                string json = JsonConvert.SerializeObject(Config, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
                LogDebug("Config saved");
            }
            catch (Exception e)
            {
                Adapter?.LogError($"Failed to save config: {e.Message}");
            }
        }

        #endregion

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
                int engineVersion = 0;

                // Extract metadata and translations
                foreach (var prop in parsed.Properties())
                {
                    if (prop.Name == "_engine_version")
                    {
                        engineVersion = prop.Value.Value<int>();
                    }
                    else if (prop.Name == "_uuid")
                    {
                        FileUuid = prop.Value.ToString();
                    }
                    else if (prop.Name == "_local_changes")
                    {
                        LocalChangesCount = prop.Value.Value<int>();
                    }
                    else if (prop.Name == "_metadata_dirty")
                    {
                        MetadataDirty = prop.Value.Value<bool>();
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
                        LogDebug($"[LoadCache] Loaded {userExclusions.Count} user exclusions");
                    }
                    else if (prop.Name == "_image_replacements")
                    {
                        ImageReplacer.LoadFromJson(prop.Value);
                    }
                    else if (prop.Name == "_variables")
                    {
                        VariableManager.LoadFromJson(prop.Value);
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
                        LogDebug($"[LoadCache] Loaded {FontSettingsMap.Count} font settings");
                    }
                    else if (prop.Name == "_font_overrides" && prop.Value.Type == JTokenType.Array)
                    {
                        // Load font override rules
                        fontOverrides.Clear();
                        fontOverrideCache.Clear();
                        foreach (var item in prop.Value)
                        {
                            var ruleObj = item as JObject;
                            if (ruleObj == null) continue;
                            var rule = new FontOverrideRule
                            {
                                match = ruleObj["match"]?.Value<string>(),
                                replacement = ruleObj["replacement"]?.Value<string>(),
                                size_multiplier = ruleObj["size_multiplier"]?.Value<float>() ?? 0f,
                                enabled = ruleObj["enabled"]?.Value<bool>() ?? true,
                                comment = ruleObj["comment"]?.Value<string>()
                            };
                            if (!string.IsNullOrEmpty(rule.match))
                            {
                                fontOverrides.Add(rule);
                            }
                        }
                        LogDebug($"[LoadCache] Loaded {fontOverrides.Count} font override rules");
                    }
                    else if (prop.Name == "_settings" && prop.Value.Type == JTokenType.Object)
                    {
                        // Load game-specific settings
                        var settingsObj = prop.Value as JObject;
                        if (settingsObj != null)
                        {
                            DisableEventSystemOverride = settingsObj["disable_eventsystem_override"]?.Value<bool>() ?? false;
                            TypewritingDetection = settingsObj["typewriting_detection"]?.Value<bool>() ?? true;
                            ConcatDetection = settingsObj["concat_detection"]?.Value<bool>() ?? true;
                            LogDebug($"[LoadCache] Loaded settings: DisableEventSystemOverride={DisableEventSystemOverride}, TW={TypewritingDetection}, Concat={ConcatDetection}");
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
                    LogDebug($"Legacy cache file, generated UUID: {FileUuid}");
                }

                // Update _game.steam_id if we detected one but file didn't have it
                if (CurrentGame != null && !string.IsNullOrEmpty(CurrentGame.steam_id))
                {
                    if (string.IsNullOrEmpty(savedSteamId) || savedSteamId != CurrentGame.steam_id)
                    {
                        cacheModified = true;
                        LogDebug($"[LoadCache] Detected steam_id ({CurrentGame.steam_id}) differs from saved ({savedSteamId ?? "null"}), will update file");
                    }
                }

                // Load ancestor cache if exists (for 3-way merge support)
                LoadAncestorCache();

                // Migrate old placeholder format [vN] → [!v*N] if needed
                if (engineVersion < CurrentEngineVersion)
                {
                    int migrated = MigratePlaceholderFormat(TranslationCache);
                    if (migrated > 0)
                    {
                        LogDebug($"[LoadCache] Migrated {migrated} entries from [vN] to [!v*N] format (v{engineVersion} → v{CurrentEngineVersion})");
                        cacheModified = true; // Will trigger save
                    }
                }

                // Recalculate LocalChangesCount based on actual differences (always, even if no ancestor)
                RecalculateLocalChanges();

                // Build reverse cache: all translated values (NORMALIZED for comparison)
                // Values must be normalized the same way as incoming text in TranslateTextWithTracking
                // ALSO trim trailing whitespace/newlines because TMP often strips them when displaying
                translatedTexts.Clear();
                foreach (var kv in TranslationCache)
                {
                    if (kv.Key != kv.Value.Value && !string.IsNullOrEmpty(kv.Value.Value))
                    {
                        string normalizedValue = NormalizeLineEndings(kv.Value.Value);
                        if (Config.normalize_numbers)
                        {
                            normalizedValue = ExtractNumbersToPlaceholders(normalizedValue, out _);
                        }
                        normalizedValue = normalizedValue.TrimEnd();
                        translatedTexts.TryAdd(normalizedValue, 0);
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
            LogDebug("[TranslatorCore] Reloading cache from disk...");

            // Restore all displayed text to originals BEFORE loading new cache,
            // so the scanner doesn't see stale translated text from the old JSON
            // and try to re-translate it
            TranslatorScanner.RestoreAllOriginals();

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
                LogDebug($"Saved ancestor cache with {AncestorCache.Count} entries");
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

                LogDebug($"Saved ancestor from remote with {AncestorCache.Count} entries");
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

                LogDebug($"Saved ancestor from remote with {AncestorCache.Count} entries");
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
        /// <summary>
        /// Migrate old placeholder format [vN] to new format [!v*N] in all cache entries.
        /// Returns the number of entries migrated.
        /// </summary>
        private static int MigratePlaceholderFormat(Dictionary<string, TranslationEntry> cache)
        {
            var oldPattern = new Regex(@"\[v(\d+)\]");
            var toMigrate = new List<KeyValuePair<string, TranslationEntry>>();

            foreach (var kv in cache)
            {
                bool keyHasOld = oldPattern.IsMatch(kv.Key);
                bool valHasOld = kv.Value?.Value != null && oldPattern.IsMatch(kv.Value.Value);
                if (keyHasOld || valHasOld)
                    toMigrate.Add(kv);
            }

            foreach (var kv in toMigrate)
            {
                string newKey = oldPattern.Replace(kv.Key, "[!v*$1]");
                string newVal = kv.Value?.Value != null ? oldPattern.Replace(kv.Value.Value, "[!v*$1]") : kv.Value?.Value;

                // Remove old key if it changed
                if (newKey != kv.Key)
                    cache.Remove(kv.Key);

                cache[newKey] = new TranslationEntry
                {
                    Value = newVal,
                    Tag = kv.Value.Tag
                };
            }

            return toMigrate.Count;
        }

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
            LogDebug($"[LocalChanges] Recalculated: {changes} local changes");
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

                if (DebugMode)
                {
                    string preview = content.Length > 100 ? content.Substring(0, 100) + "..." : content;
                    LogDebug($"[HashDebug] Local JSON preview: {preview}");
                    LogDebug($"[HashDebug] Local entry count: {sortedDict.Count}, length: {content.Length}");
                }

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
            // Build into a NEW list, then swap atomically.
            // TryPatternMatch iterates PatternEntries on the main thread while
            // this can be called from the worker thread (via AddToCache).
            var newEntries = new List<PatternEntry>();
            var placeholderRegex = new Regex(@"\[!v\*(\d+)\]", RegexOptions.None);

            // Snapshot to avoid "Collection was modified" if AddToCache runs concurrently
            KeyValuePair<string, TranslationEntry>[] cacheSnapshot;
            try
            {
                var list = new List<KeyValuePair<string, TranslationEntry>>(TranslationCache);
                cacheSnapshot = list.ToArray();
            }
            catch { return; } // Collection changed during snapshot — next call will succeed

            foreach (var kv in cacheSnapshot)
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

                    newEntries.Add(new PatternEntry
                    {
                        OriginalPattern = kv.Key,
                        TranslatedPattern = kv.Value.Value,
                        MatchRegex = new Regex("^" + pattern + "$", RegexOptions.Compiled),
                        PlaceholderIndices = placeholderIndices
                    });
                }
                catch { }
            }

            // Atomic swap — main thread sees either the old or the new list, never a half-built one
            PatternEntries = newEntries;

            if (DebugMode)
                Adapter?.LogInfo($"Built {PatternEntries.Count} pattern entries");
        }

        private static bool workerRunning = false;
        /// <summary>
        /// Set to true by the worker thread when new translations are cached.
        /// Consumed by the scanner to trigger a visual refresh on the main thread.
        /// </summary>
        public static volatile bool PendingVisualRefresh = false;
        /// <summary>
        /// Set to true by API translation methods when a rate limit (429) is received.
        /// The worker checks this to re-queue the text and backoff.
        /// </summary>
        private static volatile bool _apiRateLimited = false;

        private static void StartTranslationWorker()
        {
            if (!Config.IsTranslationEnabled)
            {
                Adapter?.LogWarning("[Worker] Cannot start: no translation backend enabled");
                return;
            }
            if (workerRunning) return; // Already running

            workerRunning = true;
            LogDebug("[Worker] Starting translation worker thread");
            Thread workerThread = new Thread(TranslationWorkerLoop);
            workerThread.IsBackground = true;
            workerThread.Start();
        }

        /// <summary>
        /// Start the translation worker if AI is enabled and worker isn't running.
        /// Call this after enabling AI in settings.
        /// </summary>
        public static void EnsureWorkerRunning()
        {
            if (Config.IsTranslationEnabled && !workerRunning)
            {
                LogDebug("[TranslatorCore] Starting translation worker thread...");
                StartTranslationWorker();
            }
        }

        /// <summary>
        /// Clear the translation queue. Called when AI is disabled.
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
                    LogDebug($"[TranslatorCore] Cleared {count} items from translation queue");
                }
            }
        }

        private static void PreloadModel()
        {
            try
            {
                Adapter.LogInfo($"Preloading model {Config.ai_model}...");
                var requestBody = new
                {
                    model = Config.ai_model,
                    messages = new[] { new { role = "user", content = "Hi" } },
                    max_tokens = 1,
                    stream = false
                };
                string jsonRequest = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, ResolveAIEndpoint(Config.ai_url, "chat/completions"));
                request.Content = content;
                AddAIAuthHeader(request);

                var response = httpClient.SendAsync(request).Result;
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
        /// Add Authorization header for AI API requests if an API key is configured.
        /// </summary>
        private static void AddAIAuthHeader(HttpRequestMessage request, string apiKey = null)
        {
            string key = apiKey ?? Config?.ai_api_key;
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }
        }

        /// <summary>
        /// Resolve an OpenAI-compatible endpoint from the user's base URL.
        /// Handles various URL formats:
        ///   "http://localhost:11434"                                              → .../v1/chat/completions
        ///   "https://api.groq.com/openai/v1"                                     → .../openai/v1/chat/completions
        ///   "https://generativelanguage.googleapis.com/.../openai/chat/completions" → unchanged (full URL)
        ///   "https://example.com/v1/chat/completions"                            → unchanged (full URL)
        /// </summary>
        private static string ResolveAIEndpoint(string baseUrl, string path)
        {
            string url = baseUrl.TrimEnd('/');
            string trimmedPath = path.TrimStart('/');

            // Already contains the full path (user pasted the complete endpoint)
            if (url.EndsWith("/" + trimmedPath) || url.EndsWith(trimmedPath))
                return url;

            // URL contains /v1/ somewhere (e.g., .../openai/v1/something) — strip back to /v1 and append path
            int v1Index = url.LastIndexOf("/v1/");
            if (v1Index >= 0)
                return url.Substring(0, v1Index + 3) + "/" + trimmedPath;

            // URL ends with /v1 — just append the path (e.g., /chat/completions)
            if (url.EndsWith("/v1"))
                return url + "/" + trimmedPath;

            // Default — add /v1/ prefix to path (backwards compatible: Ollama, etc.)
            // Providers with non-standard paths (e.g., Gemini) should use the full URL
            // including /chat/completions, or ending with /v1
            return url + "/v1/" + trimmedPath;
        }

        /// <summary>
        /// Test connection to AI server via OpenAI-compatible /v1/models endpoint.
        /// </summary>
        /// <param name="url">The server URL to test</param>
        /// <param name="apiKey">Optional API key for authenticated servers</param>
        /// <returns>True if connection successful</returns>
        public static async System.Threading.Tasks.Task<bool> TestAIConnection(string url, string apiKey = null)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, ResolveAIEndpoint(url, "models"));
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }
                var response = await httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[AI] Connection test failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Test Google Translate API connection by translating a sample word.
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> TestGoogleConnection(string apiKey)
        {
            try
            {
                var requestObj = new JObject
                {
                    ["q"] = "Hello",
                    ["target"] = "fr",
                    ["format"] = "text"
                };

                string jsonRequest = requestObj.ToString(Newtonsoft.Json.Formatting.None);
                var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, "https://translation.googleapis.com/language/translate/v2");
                request.Content = httpContent;
                request.Headers.Add("X-Goog-Api-Key", apiKey);

                var response = await httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[Google] Connection test failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Test DeepL API connection by translating a sample word.
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> TestDeepLConnection(string apiKey, bool useFree)
        {
            try
            {
                var requestObj = new JObject
                {
                    ["text"] = new JArray { "Hello" },
                    ["target_lang"] = "FR"
                };

                string endpoint = useFree
                    ? "https://api-free.deepl.com/v2/translate"
                    : "https://api.deepl.com/v2/translate";

                string jsonRequest = requestObj.ToString(Newtonsoft.Json.Formatting.None);
                var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = httpContent;
                request.Headers.Add("Authorization", $"DeepL-Auth-Key {apiKey}");

                var response = await httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[DeepL] Connection test failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fetch available models from AI server via OpenAI-compatible /v1/models endpoint.
        /// </summary>
        /// <param name="url">The server URL</param>
        /// <param name="apiKey">Optional API key for authenticated servers</param>
        /// <returns>Sorted array of model names, or empty array on failure</returns>
        public static async System.Threading.Tasks.Task<string[]> FetchModels(string url, string apiKey = null)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, ResolveAIEndpoint(url, "models"));
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }
                var response = await httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    return new string[0];

                string json = await response.Content.ReadAsStringAsync();
                var obj = JObject.Parse(json);
                var data = obj["data"] as JArray;
                if (data == null)
                    return new string[0];

                var models = new List<string>();
                foreach (var item in data)
                {
                    string id = item["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                        models.Add(id);
                }
                models.Sort(StringComparer.OrdinalIgnoreCase);
                return models.ToArray();
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"Failed to fetch models: {e.Message}");
                return new string[0];
            }
        }

        private static void TranslationWorkerLoop()
        {
            // On IL2CPP, register this thread with the GC to prevent
            // "fatal error in GC: Collecting from unknown thread" crashes.
            // The Boehm GC used by IL2CPP doesn't know about .NET threads.
            if (Adapter?.IsIL2CPP == true)
            {
                try
                {
                    // Find IL2CPP class (Il2CppInterop.Runtime.IL2CPP)
                    Type il2cppType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        il2cppType = asm.GetType("Il2CppInterop.Runtime.IL2CPP");
                        if (il2cppType != null) break;
                    }

                    if (il2cppType != null)
                    {
                        // il2cpp_domain_get() returns the current domain pointer
                        var domainGet = il2cppType.GetMethod("il2cpp_domain_get",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        // il2cpp_thread_attach(domain) attaches current thread to GC
                        var threadAttach = il2cppType.GetMethod("il2cpp_thread_attach",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                        if (domainGet != null && threadAttach != null)
                        {
                            var domain = domainGet.Invoke(null, null);
                            threadAttach.Invoke(null, new object[] { domain });
                            LogDebug("[Worker] Thread attached to IL2CPP GC domain");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Adapter?.LogWarning($"[Worker] Failed to attach thread to IL2CPP GC: {ex.Message}");
                }
            }

            LogDebug("[Worker] Thread started, waiting for translations...");

            while (!ShuttingDown)
            {
                // Stop if AI was disabled
                if (!Config.IsTranslationEnabled)
                {
                    LogDebug("[Worker] Translation disabled, stopping worker thread");
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
                            if (Config.debug_ai)
                                Adapter?.LogInfo($"[Worker] Found {comps.Count} components for text");
                        }
                        else
                        {
                            if (Config.debug_ai)
                                Adapter?.LogWarning($"[Worker] NO components found for text!");
                        }

                        // Remove from pending so same text can be re-queued with new components
                        pendingTranslations.Remove(textToTranslate);

                        if (Config.debug_ai)
                            Adapter?.LogInfo($"[Worker] Dequeued: {textToTranslate?.Substring(0, Math.Min(30, textToTranslate?.Length ?? 0))}...");
                    }
                }

                if (textToTranslate != null)
                {
                    string originalText = textToTranslate;
                    if (Config.debug_ai)
                    {
                        string workerPreview = textToTranslate.Length > 40 ? textToTranslate.Substring(0, 40) + "..." : textToTranslate;
                        Adapter?.LogInfo($"[Worker] Processing: {workerPreview} (queue remaining: {translationQueue.Count})");
                    }
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
                        if (Config.debug_ai)
                            Adapter?.LogInfo($"[Worker] Calling AI...{(isOwnUI ? " (UI prompt)" : "")}");

                        // Extract variables then numbers BEFORE sending to AI
                        string normalizedOriginal = textToTranslate;
                        List<KeyValuePair<int, string>> workerExtractedVars = null;
                        if (VariableManager.HasVariables)
                        {
                            normalizedOriginal = VariableManager.ExtractVariables(normalizedOriginal, out workerExtractedVars);
                        }
                        List<string> extractedNumbers = null;
                        if (Config.normalize_numbers)
                        {
                            normalizedOriginal = ExtractNumbersToPlaceholders(normalizedOriginal, out extractedNumbers);
                        }

                        // Check cache first (another request might have already translated this)
                        string translation = null;
                        if (TranslationCache.TryGetValue(normalizedOriginal, out var cachedEntry))
                        {
                            if (cachedEntry.Value != normalizedOriginal && !cachedEntry.IsHumanEmpty && cachedEntry.Tag != "S")
                            {
                                translation = cachedEntry.Value;
                                if (Config.debug_ai)
                                    Adapter?.LogInfo($"[Worker] Cache hit for normalized text, skipping AI");

                                // Notify components even for cache hits — the text was re-queued
                                // with different components that didn't get the first Apply.
                                string cachedTranslation = (extractedNumbers != null && extractedNumbers.Count > 0)
                                    ? RestoreNumbersFromPlaceholders(translation, extractedNumbers)
                                    : translation;
                                cachedTranslation = VariableManager.RestoreVariables(cachedTranslation, workerExtractedVars);
                                OnTranslationComplete?.Invoke(originalText, cachedTranslation, componentsToUpdate);
                            }
                        }

                        // Capture keys only mode: store H+empty without calling AI
                        if (Config.capture_keys_only)
                        {
                            AddToCache(normalizedOriginal, "", "H");
                            if (Config.debug_ai)
                                Adapter?.LogInfo($"[Worker] Captured key (no translation): {normalizedOriginal.Substring(0, Math.Min(30, normalizedOriginal.Length))}...");
                        }
                        // Only call translation backend if not in cache
                        else if (translation == null)
                        {
                            // Dispatch to the appropriate backend
                            string backend = Config.translation_backend;
                            if (backend == "google" || backend == "deepl")
                            {
                                translation = TranslateWithAPI(normalizedOriginal, extractedNumbers);
                            }
                            else
                            {
                                // LLM backend (default)
                                translation = TranslateWithAI(normalizedOriginal, extractedNumbers, isOwnUI);
                            }

                            if (Config.debug_ai)
                                Adapter?.LogInfo($"[Worker] {backend} returned: {(translation == null ? "(null)" : translation.Substring(0, Math.Min(40, translation.Length)))}");

                            // Handle rate limit: re-queue the text and backoff
                            if (translation == null && _apiRateLimited)
                            {
                                _apiRateLimited = false;
                                lock (lockObj)
                                {
                                    if (!pendingTranslations.Contains(originalText))
                                    {
                                        pendingTranslations.Add(originalText);
                                        translationQueue.Enqueue(originalText);
                                    }
                                }
                                float delaySec = Math.Max(0.1f, Config.rate_limit_retry_delay);
                                Adapter?.LogWarning($"[Worker] Rate limited — re-queued, backing off {delaySec:F1}s ({translationQueue.Count} pending)");
                                // Backoff: wait before retrying (in small increments to respond to shutdown)
                                int delayMs = (int)(delaySec * 1000);
                                for (int i = 0; i < delayMs && !ShuttingDown; i += 100)
                                    Thread.Sleep(Math.Min(100, delayMs - i));
                            }

                            if (!string.IsNullOrEmpty(translation))
                            {
                                // Check if AI returned the skip marker (text not in expected source language)
                                // Note: Google/DeepL don't return skip markers, so this only applies to LLM
                                bool isSkipped = translation.Contains(SkipTranslationMarker);

                                // Cache with appropriate tag: S=Skipped, M=Mod UI, A=AI-translated
                                string tag = isSkipped ? "S" : (isOwnUI ? "M" : "A");
                                AddToCache(normalizedOriginal, isSkipped ? normalizedOriginal : translation, tag);

                                if (!isSkipped && translation != normalizedOriginal)
                                {
                                    aiTranslationCount++;

                                    // For updating components, restore actual numbers then variables
                                    string translationWithNumbers = translation;
                                    if (extractedNumbers != null)
                                    {
                                        translationWithNumbers = RestoreNumbersFromPlaceholders(translation, extractedNumbers);
                                    }
                                    translationWithNumbers = VariableManager.RestoreVariables(translationWithNumbers, workerExtractedVars);

                                    // Notify mod loader to update components
                                    OnTranslationComplete?.Invoke(originalText, translationWithNumbers, componentsToUpdate);

                                    // Request visual refresh so static text picks up the new translation
                                    PendingVisualRefresh = true;

                                    if (DebugMode || Config.debug_ai)
                                    {
                                        string preview = originalText.Length > 30 ? originalText.Substring(0, 30) + "..." : originalText;
                                        Adapter?.LogInfo($"[AI] {preview}");
                                    }
                                }
                                else if (isSkipped && Config.debug_ai)
                                {
                                    string preview = originalText.Length > 30 ? originalText.Substring(0, 30) + "..." : originalText;
                                    Adapter?.LogInfo($"[AI] Skipped (not in source language): {preview}");
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Adapter?.LogWarning($"[AI] Worker error: {e.Message}");
                    }
                    finally
                    {
                        isTranslating = false;
                        currentlyTranslating = null;
                    }

                    // Note: pendingTranslations and pendingComponents already cleaned at dequeue time

                    // Note: pendingTranslations and pendingComponents already cleaned at dequeue time
                }
                else
                {
                    // Sleep in small increments to respond quickly to shutdown
                    for (int i = 0; i < 10 && !ShuttingDown; i++)
                        Thread.Sleep(10);
                }
            }

            workerRunning = false;
            LogDebug("[Worker] Thread exiting (shutdown)");
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

        /// <summary>
        /// Detect if a model is a "thinking" model that needs special handling
        /// to disable the reasoning phase (e.g., /no_think, assistant prefill).
        /// </summary>
        private static bool IsThinkingModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            string lower = modelName.ToLowerInvariant();
            // Known thinking model families
            return lower.StartsWith("qwen3") || lower.Contains("deepseek-r1") || lower.Contains("deepseek-r2");
        }

        private static string TranslateWithAI(string textWithPlaceholders, List<string> extractedNumbers, bool isOwnUI = false)
        {
            // Security: Reject text that's too long (prevents DoS via large requests)
            if (textWithPlaceholders.Length > MaxAITextLength)
            {
                Adapter?.LogWarning($"[AI] Text too long ({textWithPlaceholders.Length} chars), skipping");
                // Cache as skipped so it's not re-queued endlessly
                AddToCache(textWithPlaceholders, textWithPlaceholders, "S");
                return null;
            }

            try
            {
                string textToTranslate = textWithPlaceholders;
                TextType textType = DetectTextType(textToTranslate);

                // === PRE-PROCESS text before prompt building ===
                // Replace structural elements with placeholders so the AI only sees translatable text.
                // 1. Line breaks → [!nl]
                string textForAI = textToTranslate.Replace("\n", "[!nl]");
                // 2. Markup tags (<color=...>, </b>, etc.) → [!t*N]
                List<string> extractedTags = null;
                textForAI = ExtractMarkupTags(textForAI, out extractedTags);
                // 3. Trim leading/trailing whitespace (visual padding confuses AI)
                string leadingWS = "";
                string trailingWS = "";
                string trimmed = textForAI.TrimStart();
                if (trimmed.Length < textForAI.Length)
                {
                    leadingWS = textForAI.Substring(0, textForAI.Length - trimmed.Length);
                    textForAI = trimmed;
                }
                trimmed = textForAI.TrimEnd();
                if (trimmed.Length < textForAI.Length)
                {
                    trailingWS = textForAI.Substring(trimmed.Length);
                    textForAI = trimmed;
                }

                if (Config.debug_ai && extractedTags != null && extractedTags.Count > 0)
                    Adapter?.LogInfo($"[AI] Extracted {extractedTags.Count} markup tags from text");

                // Detect which placeholder types are in the PROCESSED text
                bool hasNlPlaceholders = textForAI.Contains("[!nl]");
                bool hasTagPlaceholders = extractedTags != null && extractedTags.Count > 0;
                bool hasNumberPlaceholders = extractedNumbers != null && extractedNumbers.Count > 0;

                // === BUILD PROMPT based on processed text ===
                var promptBuilder = new StringBuilder();
                string targetLang = Config.GetTargetLanguage();
                string sourceLang = Config.GetSourceLanguage();

                if (isOwnUI)
                {
                    promptBuilder.AppendLine("=== CONTEXT ===");
                    promptBuilder.AppendLine($"Translating a game translation tool interface from English to {targetLang}.");
                    promptBuilder.AppendLine("Technical UI with terms: AI, cache, merge, sync, upload, download, API, hotkey, config, JSON.");
                    promptBuilder.AppendLine();
                    promptBuilder.AppendLine("=== TRANSLATION RULES ===");
                    promptBuilder.AppendLine("- Output the translation only, no explanation");
                    promptBuilder.AppendLine("- Translation must be understandable and correct in target language");
                    promptBuilder.AppendLine("- Keep it concise for UI");
                    promptBuilder.AppendLine("- Do not add punctuation if not in the source to translate");
                    promptBuilder.AppendLine("- Keep technical terms unchanged: API, URL, UUID, JSON, AI");
                    promptBuilder.AppendLine("- Keep keyboard shortcuts as-is: Ctrl, Alt, Shift, F1-F12, Tab, Esc");
                    if (hasNlPlaceholders)
                        promptBuilder.AppendLine("- IMPORTANT: Keep [!nl] placeholders exactly where they are, do not remove or move them");
                    if (hasTagPlaceholders)
                        promptBuilder.AppendLine("- IMPORTANT: Keep [!t*0], [!t*1], etc. tag placeholders exactly as-is, do not modify or remove them");
                    if (hasNumberPlaceholders)
                        promptBuilder.AppendLine("- IMPORTANT: Keep [!v*0], [!v*1], etc. placeholders exactly as-is, do not modify them");
                    if (VariableManager.HasVariables)
                        promptBuilder.AppendLine("- IMPORTANT: Keep [!STR*0], [!STR*1], etc. string variable placeholders exactly as-is, do not translate or modify them");

                    if (textType == TextType.SingleWord)
                    {
                        promptBuilder.AppendLine();
                        promptBuilder.Append("Now, translate this word:");
                    }
                }
                else
                {
                    string gameCtx = !string.IsNullOrEmpty(Config.game_context)
                        ? Config.game_context
                        : "video game UI, menus and dialogues";

                    if (Config.strict_source_language && sourceLang != null)
                    {
                        promptBuilder.AppendLine("=== CRITICAL RULE ===");
                        promptBuilder.AppendLine($"Source language: {sourceLang}");
                        promptBuilder.AppendLine($"- If text is NOT in {sourceLang}: reply ONLY with exactly: {SkipTranslationMarker}");
                        promptBuilder.AppendLine($"- If text IS in {sourceLang}: translate to {targetLang}");
                        promptBuilder.AppendLine();
                    }

                    promptBuilder.AppendLine("=== CONTEXT ===");
                    if (sourceLang != null)
                        promptBuilder.AppendLine($"Translating video game ({gameCtx}) from {sourceLang} to {targetLang}.");
                    else
                        promptBuilder.AppendLine($"Translating video game ({gameCtx}) to {targetLang}.");
                    promptBuilder.AppendLine();

                    promptBuilder.AppendLine("=== TRANSLATION RULES ===");
                    promptBuilder.AppendLine("- Output the translation only, no explanation");
                    promptBuilder.AppendLine("- Translation must be correct in target language");
                    promptBuilder.AppendLine("- Keep it concise for UI");
                    promptBuilder.AppendLine("- Do not add punctuation if not in the source to translate");
                    promptBuilder.AppendLine("- Keep unchanged: keyboard keys (Tab, Esc, Space...), technical settings (VSync, Auto)");
                    if (hasNlPlaceholders)
                        promptBuilder.AppendLine("- IMPORTANT: Keep [!nl] placeholders exactly where they are, do not remove or move them");
                    if (hasTagPlaceholders)
                        promptBuilder.AppendLine("- IMPORTANT: Keep [!t*0], [!t*1], etc. tag placeholders exactly as-is, do not modify or remove them");
                    if (hasNumberPlaceholders)
                        promptBuilder.AppendLine("- IMPORTANT: Keep [!v*0], [!v*1], etc. placeholders exactly as-is, do not modify them");
                    if (VariableManager.HasVariables)
                        promptBuilder.AppendLine("- IMPORTANT: Keep [!STR*0], [!STR*1], etc. string variable placeholders exactly as-is, do not translate or modify them");

                    if (textType == TextType.SingleWord)
                    {
                        promptBuilder.AppendLine();
                        promptBuilder.Append("Now, translate this word:");
                    }
                }

                string systemPrompt = promptBuilder.ToString();

                if (Config.debug_ai)
                {
                    Adapter?.LogInfo($"[AI] System prompt:\n{systemPrompt}");
                }

                // === BUILD REQUEST ===
                bool isThinkingModel = IsThinkingModel(Config.ai_model);
                string userContent = isThinkingModel ? textForAI + " /no_think" : textForAI;

                var messagesArray = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JObject { ["role"] = "user", ["content"] = userContent }
                };
                // Extra safety for known thinking models: assistant prefill to skip reasoning
                if (isThinkingModel)
                {
                    messagesArray.Add(new JObject { ["role"] = "assistant", ["content"] = "<think>\n\n</think>\n\n" });
                }

                var requestObj = new JObject
                {
                    ["model"] = Config.ai_model,
                    ["messages"] = messagesArray,
                    ["temperature"] = 0.0,
                    ["max_tokens"] = Math.Max(200, textToTranslate.Length * 2),
                    ["stream"] = false
                };

                // Send "think": false to disable reasoning on providers that support it (e.g. Ollama).
                // Some providers (OpenAI, Grok, etc.) reject unknown parameters with 400.
                // We use a cached flag to avoid retrying on every request.
                bool sendThink = ShouldSendThinkParam();
                if (sendThink)
                {
                    requestObj["think"] = false;
                }

                string aiEndpoint = ResolveAIEndpoint(Config.ai_url, "chat/completions");
                string jsonRequest = requestObj.ToString(Newtonsoft.Json.Formatting.None);
                var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, aiEndpoint);
                request.Content = httpContent;
                AddAIAuthHeader(request);

                var response = httpClient.SendAsync(request).Result;

                // Handle providers that reject the "think" parameter with 400
                if (!response.IsSuccessStatusCode && sendThink && (int)response.StatusCode == 400)
                {
                    string errorBody = "";
                    try { errorBody = response.Content.ReadAsStringAsync().Result; } catch { }

                    if (errorBody.Contains("think"))
                    {
                        // This provider doesn't support "think" param — cache and retry without it
                        _providerSupportsThinkParam = false;
                        LogDebug("[AI] Provider does not support 'think' parameter, retrying without it");

                        requestObj.Remove("think");
                        jsonRequest = requestObj.ToString(Newtonsoft.Json.Formatting.None);
                        httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                        request = new HttpRequestMessage(HttpMethod.Post, aiEndpoint);
                        request.Content = httpContent;
                        AddAIAuthHeader(request);

                        response = httpClient.SendAsync(request).Result;
                    }
                }

                // Mark provider as supporting think param on success (if we sent it)
                if (response.IsSuccessStatusCode && sendThink && _providerSupportsThinkParam == null)
                {
                    _providerSupportsThinkParam = true;
                }

                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode == 429)
                        _apiRateLimited = true;
                    string errorBody = "";
                    try { errorBody = response.Content.ReadAsStringAsync().Result; } catch { }
                    Adapter?.LogWarning($"[AI] HTTP {statusCode} {response.StatusCode}: {errorBody}");
                    return null;
                }

                string responseJson = response.Content.ReadAsStringAsync().Result;
                var responseObj = JObject.Parse(responseJson);
                string translation = responseObj["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim();

                if (Config.debug_ai)
                {
                    Adapter?.LogInfo($"[AI Raw] {translation?.Substring(0, Math.Min(80, translation?.Length ?? 0))}");
                }

                if (!string.IsNullOrEmpty(translation))
                {
                    // Validate tag placeholders are preserved
                    if (extractedTags != null && extractedTags.Count > 0)
                        ValidateMarkupTags(translation, extractedTags.Count, textToTranslate);

                    // Restore placeholders in reverse order of extraction:
                    // 1. Markup tags [!t*N] → original tags
                    translation = RestoreMarkupTags(translation, extractedTags);
                    // 2. Line breaks [!nl] → \n
                    translation = translation.Replace("[!nl]", "\n");
                    // 3. Clean AI artifacts (removes quotes, thinking blocks, etc.)
                    translation = CleanTranslation(translation);
                    // 4. Restore leading/trailing whitespace AFTER clean (clean does Trim)
                    if (leadingWS.Length > 0 || trailingWS.Length > 0)
                        translation = leadingWS + translation + trailingWS;
                    if (Config.debug_ai)
                    {
                        Adapter?.LogInfo($"[AI Clean] {translation?.Substring(0, Math.Min(80, translation?.Length ?? 0))}");
                    }
                }

                return translation;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[AI] Translation error: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Translate text using Google Translate API v2.
        /// Simpler than LLM: no prompt, no thinking, no artifacts.
        /// Pre-processing (placeholders, tags, whitespace) is done by the caller.
        /// </summary>
        private static string TranslateWithGoogle(string textToTranslate)
        {
            if (string.IsNullOrEmpty(Config.google_api_key))
            {
                Adapter?.LogWarning("[Google] No API key configured");
                return null;
            }

            try
            {
                string targetLang = Config.GetTargetLanguage();
                string targetCode = LanguageHelper.GetGoogleLanguageCode(targetLang);
                if (string.IsNullOrEmpty(targetCode))
                {
                    Adapter?.LogWarning($"[Google] Unsupported target language: {targetLang}");
                    return null;
                }

                var requestObj = new JObject
                {
                    ["q"] = textToTranslate,
                    ["target"] = targetCode,
                    ["format"] = "text"
                };

                // Add source language if specified
                string sourceLang = Config.GetSourceLanguage();
                if (!string.IsNullOrEmpty(sourceLang))
                {
                    string sourceCode = LanguageHelper.GetGoogleLanguageCode(sourceLang);
                    if (!string.IsNullOrEmpty(sourceCode))
                        requestObj["source"] = sourceCode;
                }

                string endpoint = "https://translation.googleapis.com/language/translate/v2";
                string jsonRequest = requestObj.ToString(Newtonsoft.Json.Formatting.None);
                var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = httpContent;
                request.Headers.Add("X-Goog-Api-Key", Config.google_api_key);

                var response = httpClient.SendAsync(request).Result;

                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode == 429)
                        _apiRateLimited = true;
                    string errorBody = "";
                    try { errorBody = response.Content.ReadAsStringAsync().Result; } catch { }
                    Adapter?.LogWarning($"[Google] HTTP {statusCode}: {errorBody}");
                    return null;
                }

                string responseJson = response.Content.ReadAsStringAsync().Result;
                var responseObj = JObject.Parse(responseJson);
                string translation = responseObj["data"]?["translations"]?[0]?["translatedText"]?.ToString();

                if (Config.debug_ai)
                    Adapter?.LogInfo($"[Google] '{textToTranslate}' -> '{translation}'");

                return translation;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[Google] Translation error: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Translate text using DeepL API v2.
        /// Simpler than LLM: no prompt, no thinking, no artifacts.
        /// Pre-processing (placeholders, tags, whitespace) is done by the caller.
        /// </summary>
        private static string TranslateWithDeepL(string textToTranslate)
        {
            if (string.IsNullOrEmpty(Config.deepl_api_key))
            {
                Adapter?.LogWarning("[DeepL] No API key configured");
                return null;
            }

            try
            {
                string targetLang = Config.GetTargetLanguage();
                string targetCode = LanguageHelper.GetDeepLLanguageCode(targetLang, isTarget: true);
                if (string.IsNullOrEmpty(targetCode))
                {
                    Adapter?.LogWarning($"[DeepL] Unsupported target language: {targetLang}");
                    return null;
                }

                var requestObj = new JObject
                {
                    ["text"] = new JArray { textToTranslate },
                    ["target_lang"] = targetCode
                };

                // Add source language if specified
                string sourceLang = Config.GetSourceLanguage();
                if (!string.IsNullOrEmpty(sourceLang))
                {
                    string sourceCode = LanguageHelper.GetDeepLLanguageCode(sourceLang, isTarget: false);
                    if (!string.IsNullOrEmpty(sourceCode))
                        requestObj["source_lang"] = sourceCode;
                }

                string endpoint = Config.deepl_use_free
                    ? "https://api-free.deepl.com/v2/translate"
                    : "https://api.deepl.com/v2/translate";

                string jsonRequest = requestObj.ToString(Newtonsoft.Json.Formatting.None);
                var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = httpContent;
                request.Headers.Add("Authorization", $"DeepL-Auth-Key {Config.deepl_api_key}");

                var response = httpClient.SendAsync(request).Result;

                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode == 429)
                        _apiRateLimited = true;
                    string errorBody = "";
                    try { errorBody = response.Content.ReadAsStringAsync().Result; } catch { }
                    Adapter?.LogWarning($"[DeepL] HTTP {statusCode}: {errorBody}");
                    return null;
                }

                string responseJson = response.Content.ReadAsStringAsync().Result;
                var responseObj = JObject.Parse(responseJson);
                string translation = responseObj["translations"]?[0]?["text"]?.ToString();

                if (Config.debug_ai)
                    Adapter?.LogInfo($"[DeepL] '{textToTranslate}' -> '{translation}'");

                return translation;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[DeepL] Translation error: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Translate text using a translation API (Google or DeepL).
        /// Handles pre/post-processing (placeholders, tags, whitespace) like TranslateWithAI but without prompts.
        /// </summary>
        private static string TranslateWithAPI(string textWithPlaceholders, List<string> extractedNumbers)
        {
            if (textWithPlaceholders.Length > MaxAITextLength)
            {
                Adapter?.LogWarning($"[API] Text too long ({textWithPlaceholders.Length} chars), skipping");
                AddToCache(textWithPlaceholders, textWithPlaceholders, "S");
                return null;
            }

            try
            {
                string textToTranslate = textWithPlaceholders;

                // === PRE-PROCESS: same placeholder extraction as LLM ===
                // 1. Line breaks → [!nl]
                string textForAPI = textToTranslate.Replace("\n", "[!nl]");
                // 2. Markup tags → [!t*N]
                List<string> extractedTags = null;
                textForAPI = ExtractMarkupTags(textForAPI, out extractedTags);
                // 3. Trim whitespace
                string leadingWS = "";
                string trailingWS = "";
                string trimmed = textForAPI.TrimStart();
                if (trimmed.Length < textForAPI.Length)
                {
                    leadingWS = textForAPI.Substring(0, textForAPI.Length - trimmed.Length);
                    textForAPI = trimmed;
                }
                trimmed = textForAPI.TrimEnd();
                if (trimmed.Length < textForAPI.Length)
                {
                    trailingWS = textForAPI.Substring(trimmed.Length);
                    textForAPI = trimmed;
                }

                // Skip empty text after pre-processing
                if (string.IsNullOrWhiteSpace(textForAPI))
                    return null;

                // === CALL THE API ===
                string translation = null;
                switch (Config.translation_backend)
                {
                    case "google":
                        translation = TranslateWithGoogle(textForAPI);
                        break;
                    case "deepl":
                        translation = TranslateWithDeepL(textForAPI);
                        break;
                }

                if (string.IsNullOrEmpty(translation))
                    return null;

                // === POST-PROCESS: restore placeholders ===
                // Validate and restore markup tags
                if (extractedTags != null && extractedTags.Count > 0)
                {
                    translation = RestoreMarkupTags(translation, extractedTags);
                }
                // Restore [!nl] → \n
                translation = translation.Replace("[!nl]", "\n");
                // Restore whitespace
                translation = leadingWS + translation + trailingWS;

                return translation;
            }
            catch (Exception e)
            {
                Adapter?.LogWarning($"[API] Translation error: {e.Message}");
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
                    string normalizedTranslation = NormalizeLineEndings(entry.Value);
                    if (Config.normalize_numbers)
                    {
                        normalizedTranslation = ExtractNumbersToPlaceholders(normalizedTranslation, out _);
                    }
                    normalizedTranslation = normalizedTranslation.TrimEnd();
                    translatedTexts.TryAdd(normalizedTranslation, 0);
                }

                // Note: No longer clearing lastSeenText here.
                // OnTranslationComplete updates tracked components directly.
                // New components will be translated on their next scan cycle.

                if (normalizedKey.Contains(PlaceholderPrefix))
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
                if (!IsPartOfHexColor(text, match.Index, match.Length)
                    && !IsInsidePlaceholder(text, match.Index))
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
                result.Insert(num.Item2, $"{PlaceholderPrefix}{i}{PlaceholderSuffix}");
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
                result = result.Replace($"{PlaceholderPrefix}{i}{PlaceholderSuffix}", numbers[i]);
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

        /// <summary>
        /// Check if a number at the given index is inside a [!v*N] placeholder.
        /// </summary>
        private static bool IsInsidePlaceholder(string text, int index)
        {
            // Look backwards for "[!v*" or "[!STR*" patterns
            // Protects numbers inside [!v*0], [!STR*0], [!t*0] from being extracted
            for (int i = index - 1; i >= Math.Max(0, index - 8); i--)
            {
                // Check for [!v* (4 chars)
                if (i >= 3 && text[i] == '*' && text[i - 1] == 'v' && text[i - 2] == '!' && text[i - 3] == '[')
                {
                    for (int j = index; j < Math.Min(text.Length, index + 4); j++)
                    {
                        if (text[j] == ']') return true;
                        if (!char.IsDigit(text[j])) break;
                    }
                }
                // Check for [!STR* (6 chars)
                if (i >= 5 && text[i] == '*' && text[i - 1] == 'R' && text[i - 2] == 'T' && text[i - 3] == 'S'
                    && text[i - 4] == '!' && text[i - 5] == '[')
                {
                    for (int j = index; j < Math.Min(text.Length, index + 4); j++)
                    {
                        if (text[j] == ']') return true;
                        if (!char.IsDigit(text[j])) break;
                    }
                }
                // Check for [!t* (4 chars)
                if (i >= 3 && text[i] == '*' && text[i - 1] == 't' && text[i - 2] == '!' && text[i - 3] == '[')
                {
                    for (int j = index; j < Math.Min(text.Length, index + 4); j++)
                    {
                        if (text[j] == ']') return true;
                        if (!char.IsDigit(text[j])) break;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Normalize text for cache lookup (line endings + number extraction).
        /// Used by typewriting stabilizer to check if text is already cached.
        /// </summary>
        /// <summary>
        /// Quick check if a text has a cached translation (without doing the full translation).
        /// Used to decide whether to apply the clone font before translation.
        /// </summary>
        private static int _dbgTwCacheHit = 0;
        private static int _dbgReverseMiss = 0;

        /// <summary>
        /// Check if a normalized text is a "natural identity" — contains only digits,
        /// punctuation, whitespace, placeholders and rich text tags. Such text is the
        /// same in any language and an identity translation (key==value) is expected,
        /// not an AI failure.
        /// </summary>
        private static bool IsNaturalIdentity(string normalizedText)
        {
            if (string.IsNullOrEmpty(normalizedText)) return true;

            // Strip placeholders [!v*N] [!STR*N] and rich text tags <...>
            // then check if remaining text has any letters
            var stripped = new System.Text.StringBuilder(normalizedText.Length);
            for (int i = 0; i < normalizedText.Length; i++)
            {
                char c = normalizedText[i];
                if (c == '[' && i + 1 < normalizedText.Length && normalizedText[i + 1] == '!')
                {
                    int end = normalizedText.IndexOf(']', i);
                    if (end > i) { i = end; continue; }
                }
                if (c == '<')
                {
                    int end = normalizedText.IndexOf('>', i);
                    if (end > i) { i = end; continue; }
                }
                stripped.Append(c);
            }

            return IsNumericOrSymbol(stripped.ToString());
        }

        public static bool HasCachedTranslation(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Exact match as key
            if (TranslationCache.TryGetValue(text, out var exact))
            {
                if (exact.IsHumanEmpty || exact.Tag == "S") return false;
                // key==value with tag "A" = AI couldn't translate (source language text).
                // Exception: natural identity (only digits/punctuation/placeholders) is expected
                // to be identical — not an AI failure.
                // key==value with tag "V"/"H" = human validated, intentionally same text.
                if (exact.Value == text && exact.Tag == "A" && !IsNaturalIdentity(text)) return false;
                return true;
            }

            // Normalized match as key
            string normalized = NormalizeForCacheLookup(text);
            if (TranslationCache.TryGetValue(normalized, out var norm))
            {
                if (norm.IsHumanEmpty || norm.Tag == "S") return false;
                if (norm.Value == normalized && norm.Tag == "A" && !IsNaturalIdentity(normalized)) return false;
                return true;
            }

            // Text is already a known translation (reverse cache) — the component
            // already shows translated text and should have the clone font.
            string trimmed = normalized.TrimEnd();
            if (translatedTexts.ContainsKey(trimmed))
                return true;

            return false;
        }

        public static string NormalizeForCacheLookup(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string normalized = NormalizeLineEndings(text);
            // Variables BEFORE numbers (variables may contain digits)
            if (VariableManager.HasVariables)
                normalized = VariableManager.ExtractVariables(normalized, out _);
            if (Config.normalize_numbers)
                normalized = ExtractNumbersToPlaceholders(normalized, out _);
            return normalized;
        }

        public static void QueueForTranslation(string text, object component = null, bool isOwnUI = false)
        {
            if (!Config.IsTranslationEnabled) return;
            // Google/DeepL require online mode
            if (Config.ActiveBackendRequiresOnline && !Config.online_mode) return;
            if (string.IsNullOrEmpty(text)) return;
            if (IsNumericOrSymbol(text)) return;

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

                // Log first queued item always, then every 10th
                int queueSize = translationQueue.Count;
                if (DebugMode || Config.debug_ai)
                {
                    string preview = text.Length > 40 ? text.Substring(0, 40) + "..." : text;
                    LogDebug($"[Queue] #{queueSize}: {preview}{(isOwnUI ? " (UI)" : "")}");
                }
            }
        }

        /// <summary>
        /// Main translation method - translate text from cache or queue for AI.
        /// Treats multiline text as a single unit to preserve context and ensure consistency.
        /// </summary>
        public static string TranslateText(string text)
        {
            // Check if translations are disabled
            if (!Config.enable_translations)
                return text;

            if (string.IsNullOrEmpty(text))
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // No line splitting - treat multiline as single unit for context preservation
            string result = TranslateSingleText(text);
            if (result != text)
            {
                translatedCount++;
                string normalizedResult = NormalizeLineEndings(result);
                if (Config.normalize_numbers)
                    normalizedResult = ExtractNumbersToPlaceholders(normalizedResult, out _);
                normalizedResult = normalizedResult.TrimEnd();
                if (!translatedTexts.ContainsKey(normalizedResult))
                    translatedTexts.TryAdd(normalizedResult, 0);
            }
            return result;
        }

        public static string TranslateSingleText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // Normalize line endings FIRST (for cross-platform consistency)
            // Cache keys are stored with normalized line endings (\n only)
            string lineNormalized = NormalizeLineEndings(text);

            // Extract string variables BEFORE numbers (variables may contain digits)
            string afterVars = lineNormalized;
            List<KeyValuePair<int, string>> extractedVars = null;
            if (VariableManager.HasVariables)
            {
                afterVars = VariableManager.ExtractVariables(lineNormalized, out extractedVars);
            }

            // Then extract numbers to placeholders (if enabled)
            string normalizedText = afterVars;
            List<string> extractedNumbers = null;
            if (Config.normalize_numbers)
            {
                normalizedText = ExtractNumbersToPlaceholders(afterVars, out extractedNumbers);
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
                    // Restore numbers first, then variables
                    string result = (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cachedEntry.Value, extractedNumbers)
                        : cachedEntry.Value;
                    return VariableManager.RestoreVariables(result, extractedVars);
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
                    string trimResult = (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cachedTrimmedEntry.Value, extractedNumbers)
                        : cachedTrimmedEntry.Value;
                    return VariableManager.RestoreVariables(trimResult, extractedVars);
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

            if (Config.IsTranslationEnabled && !string.IsNullOrEmpty(text))
            {
                // Check reverse cache with NORMALIZED text (translations are stored normalized + trimmed)
                // TrimEnd because TMP often strips trailing whitespace/newlines when displaying
                string trimmedNormalized = normalizedText.TrimEnd();
                if (translatedTexts.ContainsKey(trimmedNormalized))
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
        public static string TranslateTextWithTracking(string text, object component, bool isOwnUI = false, bool skipTypewriting = false, bool skipQueueing = false)
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

            if (string.IsNullOrEmpty(text))
                return text;

            // Don't split multiline - treat as single unit for proper component tracking
            // (IsNumericOrSymbol check is in TranslateSingleTextWithTracking — no need to call twice)
            string result = TranslateSingleTextWithTracking(text, component, isOwnUI, skipTypewriting, skipQueueing);
            if (result != text)
            {
                translatedCount++;
                FontManager.EnsureCharsInCloneAtlas(result, component);

                string normalizedResult = NormalizeLineEndings(result);
                if (Config.normalize_numbers)
                    normalizedResult = ExtractNumbersToPlaceholders(normalizedResult, out _);
                normalizedResult = normalizedResult.TrimEnd();
                if (!translatedTexts.ContainsKey(normalizedResult))
                    translatedTexts.TryAdd(normalizedResult, 0);
            }
            return result;
        }

        private static string TranslateSingleTextWithTracking(string text, object component, bool isOwnUI = false, bool skipTypewriting = false, bool skipQueueing = false)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (IsNumericOrSymbol(text))
                return text;

            // Read-back detection: if the game read translated text and appended
            // untranslated content, reconstruct the source-language text.
            if (component is Component rbComp)
            {
                int rbId = TypeHelper.GetInstanceID(rbComp);
                string reconstructed = TranslatorPatches.DetectReadBack(rbId, text);
                if (reconstructed != null)
                    text = reconstructed;
            }

            // Fast path: check concat assembled cache (runtime only, not JSON)
            // Catches full tooltip texts that were assembled from translated deltas.
            string concatResult = TranslatorPatches.GetConcatCacheResult(text);
            if (concatResult != null)
            {
                translatedCount++;
                return concatResult;
            }
            // Also skip if the text is a known concat translation result (FR text)
            if (TranslatorPatches.IsConcatTranslatedValue(text))
            {
                return text; // already translated, don't re-process
            }

            // Fast path: try exact text lookup BEFORE any normalization (avoids allocations for cache hits)
            if (TranslationCache.TryGetValue(text, out var exactEntry))
            {
                // If this component is in typewriting state, touch the timestamp
                // so the stabilizer doesn't think the typewriting stopped.
                // Cache hits bypass IsTypewritingInProgress, leaving the timestamp stale.
                if (component is Component twComp2)
                {
                    int twId2 = TypeHelper.GetInstanceID(twComp2);
                    TranslatorPatches.TouchTypewritingTimestamp(twId2, text);
                }

                if (exactEntry.IsHumanEmpty || exactEntry.Tag == "S")
                {
                    cacheHitCount++;
                    return text;
                }
                if (exactEntry.Value != text)
                {
                    cacheHitCount++;
                    translatedCount++;
                    // TEMP LOG: detect if typewriting text gets a cache hit before typewriting check
                    if (_dbgTwCacheHit < 20 && component is Component twComp)
                    {
                        int twId = TypeHelper.GetInstanceID(twComp);
                        if (TranslatorPatches.IsInTypewritingState(twId))
                        {
                            _dbgTwCacheHit++;
                            LogDebug($"[TW-CACHEHIT] comp={twId} text='{(text.Length > 40 ? text.Substring(0,40) : text)}' → cache hit BYPASSES typewriting check");
                        }
                    }
                    if (DebugMode && text.Length > 100)
                    {
                        int cId = (component is Component dc) ? TypeHelper.GetInstanceID(dc) : -1;
                        LogDebug($"[CACHE-HIT-LONG] comp={cId}\n  key({text.Length}c)='{text}'\n  val({exactEntry.Value.Length}c)='{exactEntry.Value}'");
                    }
                    if (component != null)
                    {
                        TranslatorScanner.StoreOriginalText(component, text);
                        int trackId = TypeHelper.GetInstanceID(component);
                        TranslatorPatches.TrackTranslation(trackId, text, exactEntry.Value);
                    }
                    return exactEntry.Value;
                }
                // key == value: no translation needed, return as-is
                cacheHitCount++;
                if (DebugMode && text.Length > 100)
                {
                    int cId = (component is Component dc2) ? TypeHelper.GetInstanceID(dc2) : -1;
                    LogDebug($"[CACHE-HIT-SAME] comp={cId} key==val({text.Length}c)='{text}'");
                }
                return text;
            }

            // Normalize line endings (for cross-platform consistency)
            // Cache keys are stored with normalized line endings (\n only)
            string lineNormalized = NormalizeLineEndings(text);

            // Extract string variables BEFORE numbers
            string afterVars = lineNormalized;
            List<KeyValuePair<int, string>> extractedVars = null;
            if (VariableManager.HasVariables)
            {
                afterVars = VariableManager.ExtractVariables(lineNormalized, out extractedVars);
            }

            // Then extract numbers to placeholders (if enabled)
            string normalizedText = afterVars;
            List<string> extractedNumbers = null;
            if (Config.normalize_numbers)
            {
                normalizedText = ExtractNumbersToPlaceholders(afterVars, out extractedNumbers);
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
                    if (DebugMode && text.Length > 100)
                    {
                        int cId = (component is Component dc3) ? TypeHelper.GetInstanceID(dc3) : -1;
                        LogDebug($"[CACHE-HIT-NORM] comp={cId} orig({text.Length}c) norm→key({normalizedText.Length}c)\n  orig='{text}'\n  norm='{normalizedText}'");
                    }
                    // Restore numbers then variables in the translation
                    string rawTranslation = (extractedNumbers != null && extractedNumbers.Count > 0)
                        ? RestoreNumbersFromPlaceholders(cachedEntry.Value, extractedNumbers)
                        : cachedEntry.Value;
                    translation = VariableManager.RestoreVariables(rawTranslation, extractedVars);
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
                        string rawTrimTranslation = (extractedNumbers != null && extractedNumbers.Count > 0)
                            ? RestoreNumbersFromPlaceholders(cachedTrimmedEntry.Value, extractedNumbers)
                            : cachedTrimmedEntry.Value;
                        translation = VariableManager.RestoreVariables(rawTrimTranslation, extractedVars);
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
                    int trackId = (component is Component tc) ? TypeHelper.GetInstanceID(tc) : -1;
                    TranslatorPatches.TrackTranslation(trackId, text, translation);
                }
                return translation;
            }

            // If found in cache with key == value, no translation needed, don't queue
            if (foundInCache)
            {
                return text;
            }

            // No cache hit - queue for AI if enabled
            if (Config.IsTranslationEnabled && !string.IsNullOrEmpty(text))
            {
                // Check reverse cache with NORMALIZED text (translations are stored normalized + trimmed)
                // TrimEnd because TMP often strips trailing whitespace/newlines when displaying
                string trimmedNormalized = normalizedText.TrimEnd();
                if (translatedTexts.ContainsKey(trimmedNormalized))
                {
                    skippedAlreadyTranslated++;
                    return text;
                }

                // Own UI text that's already translated (displayed result) — don't re-queue
                // The mod UI shows translated text; re-queueing it creates an infinite loop
                if (isOwnUI)
                {
                    return text;
                }

                // Skip invisible components ONLY if they're also in typewriting state
                // (likely an accumulator: hidden component with growing text).
                // Inactive components with STABLE text (tab panels, menus) are allowed
                // through so they get translated before the user opens them.
                if (component is Component visComp)
                {
                    try
                    {
                        if (visComp.gameObject != null && !visComp.gameObject.activeInHierarchy)
                        {
                            int visCompId = TypeHelper.GetInstanceID(visComp);
                            if (TranslatorPatches.IsInTypewritingState(visCompId))
                            {
                                return text;
                            }
                        }
                    }
                    catch { }
                }

                // Typewriting detection: skip queuing if text is growing char by char
                // on the same component. Only for cache MISSES — cache hits are returned above.
                // This prevents partial typewriting text from being sent to AI.
                // Skip for concat deltas (they should be queued immediately, not deferred).
                int compId = (component is Component comp2) ? TypeHelper.GetInstanceID(comp2) : -1;
                if (!skipTypewriting && TranslatorPatches.IsTypewritingInProgress(compId, text))
                {
                    return text;
                }

                if (!IsTargetLanguage(text))
                {
                    // DEBUG LOG: if text contains Latin chars and wasn't caught by reverse cache
                    // Log AFTER all skip checks so we only see texts actually queued
                    if (_dbgReverseMiss < 20 && text.Length > 5)
                    {
                        bool hasLatin = false;
                        foreach (char c in text)
                        {
                            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                            { hasLatin = true; break; }
                        }
                        if (hasLatin)
                        {
                            _dbgReverseMiss++;
                            LogDebug($"[REVERSE-MISS] orig({text.Length}c)='{text}'\n  norm({trimmedNormalized.Length}c)='{trimmedNormalized}'");
                        }
                    }

                    if (!skipQueueing)
                        QueueForTranslation(text, component, isOwnUI);
                    // else: concat component — deltas are queued individually, skip full text queue
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
            // AI can recognize already-translated text and return it unchanged
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
                            result = result.Replace($"{PlaceholderPrefix}{placeholderIndex}{PlaceholderSuffix}", capturedValues[i]);
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
                // char.IsLetter may fail for CJK characters on some IL2CPP runtimes.
                // Explicitly check Unicode ranges for letters and CJK ideographs.
                if (char.IsLetter(c))
                    return false;
                if (c >= 0x2E80 && c <= 0x9FFF)  // CJK radicals, kangxi, ideographs
                    return false;
                if (c >= 0xAC00 && c <= 0xD7AF)  // Korean Hangul syllables
                    return false;
                if (c >= 0x3040 && c <= 0x30FF)  // Japanese Hiragana + Katakana
                    return false;
                if (c >= 0x0400 && c <= 0x04FF)  // Cyrillic
                    return false;
                if (c >= 0x0600 && c <= 0x06FF)  // Arabic
                    return false;
                if (c >= 0x0900 && c <= 0x097F)  // Devanagari (Hindi)
                    return false;
                if (c >= 0x0E00 && c <= 0x0E7F)  // Thai
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
        /// Call this when settings change (enable_translations, enable_ai, etc.)
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

            LogDebug("[TranslatorCore] Processing caches cleared - text will be re-evaluated");
        }

        public static bool HasSeenText(int id, string text, out string lastText)
        {
            return lastSeenText.TryGetValue(id, out lastText) && lastText == text;
        }

        public static void UpdateSeenText(int id, string text)
        {
            lastSeenText[id] = text;
        }

        public static void ClearSeenText(int id)
        {
            lastSeenText.Remove(id);
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
                    output["_engine_version"] = CurrentEngineVersion;
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

                    if (MetadataDirty)
                    {
                        output["_metadata_dirty"] = true;
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

                    // Save image replacement definitions
                    var imgReplacements = ImageReplacer.SaveToJson();
                    if (imgReplacements != null)
                        output["_image_replacements"] = imgReplacements;

                    // Save variable definitions
                    var variables = VariableManager.SaveToJson();
                    if (variables != null)
                        output["_variables"] = variables;

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

                    // Save font override rules
                    if (fontOverrides.Count > 0)
                    {
                        var overridesArray = new JArray();
                        foreach (var rule in fontOverrides)
                        {
                            var ruleObj = new JObject { ["match"] = rule.match };
                            if (!string.IsNullOrEmpty(rule.replacement))
                                ruleObj["replacement"] = rule.replacement;
                            if (Math.Abs(rule.size_multiplier) > 0.001f)
                                ruleObj["size_multiplier"] = rule.size_multiplier;
                            if (!rule.enabled)
                                ruleObj["enabled"] = false;
                            if (!string.IsNullOrEmpty(rule.comment))
                                ruleObj["comment"] = rule.comment;
                            overridesArray.Add(ruleObj);
                        }
                        output["_font_overrides"] = overridesArray;
                    }

                    // Save game-specific settings (only if non-default values)
                    {
                        var settingsObj = new JObject();
                        if (DisableEventSystemOverride)
                            settingsObj["disable_eventsystem_override"] = true;
                        if (!TypewritingDetection)
                            settingsObj["typewriting_detection"] = false;
                        if (!ConcatDetection)
                            settingsObj["concat_detection"] = false;
                        if (settingsObj.Count > 0)
                            output["_settings"] = settingsObj;
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

            LogDebug($"[Fork] Context saved: {PendingFork.SourceLanguage} -> {PendingFork.TargetLanguage}, game={PendingFork.Game?.name}");

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
        // Translation backend: "llm", "google", "deepl" (or "none" / enable_ai=false for disabled)
        public string translation_backend { get; set; } = "none";

        // LLM Translation settings (universal OpenAI-compatible)
        public string ai_url { get; set; } = "http://localhost:11434";
        public string ai_model { get; set; } = "";
        public string target_language { get; set; } = "auto";
        public string source_language { get; set; } = "auto";
        public bool strict_source_language { get; set; } = false;
        public string game_context { get; set; } = "";
        public int timeout_ms { get; set; } = 30000;
        public bool enable_ai { get; set; } = false;
        public bool cache_new_translations { get; set; } = true;
        public bool normalize_numbers { get; set; } = true;
        public bool debug { get; set; } = false;
        public bool debug_ai { get; set; } = false;
        public bool preload_model { get; set; } = true;

        [JsonConverter(typeof(EncryptedTokenConverter))]
        public string ai_api_key { get; set; } = null;

        // Google Translate API settings
        [JsonConverter(typeof(EncryptedTokenConverter))]
        public string google_api_key { get; set; } = null;

        // DeepL API settings
        [JsonConverter(typeof(EncryptedTokenConverter))]
        public string deepl_api_key { get; set; } = null;
        public bool deepl_use_free { get; set; } = true;

        /// <summary>
        /// Delay in seconds before retrying after a rate limit (HTTP 429).
        /// Applies to all backends (LLM, Google, DeepL). Supports decimals (e.g., 0.5).
        /// </summary>
        public float rate_limit_retry_delay { get; set; } = 3f;

        /// <summary>
        /// Maximum time, in seconds, that a newly instantiated text component can stay
        /// untranslated before the periodic scanner picks it up. This is the worst-case
        /// detection latency for components that are not caught by the get_text/set_text
        /// Harmony hooks (i.e. instantiated and shown without their text being read or
        /// written immediately).
        ///
        /// Lower = more responsive but higher CPU usage when many text types are registered.
        /// The actual scan work is spread across frames using an adaptive frame-time budget,
        /// so the per-frame impact stays under the natural frame-time noise even at low values.
        /// </summary>
        public float max_text_detection_latency_seconds { get; set; } = 1f;

        /// <summary>
        /// Returns true if any translation backend is enabled (LLM, Google, or DeepL).
        /// This replaces the old enable_ai check for general "is translation active" guards.
        /// </summary>
        [JsonIgnore]
        public bool IsTranslationEnabled =>
            enable_ai || translation_backend == "google" || translation_backend == "deepl";

        /// <summary>
        /// Returns true if the active backend requires online mode.
        /// LLM can be local (Ollama), Google and DeepL always need internet.
        /// </summary>
        [JsonIgnore]
        public bool ActiveBackendRequiresOnline =>
            translation_backend == "google" || translation_backend == "deepl";

        // Backward-compatible migration from old config format
        [JsonExtensionData]
        private IDictionary<string, JToken> _extraData;

        [System.Runtime.Serialization.OnDeserialized]
        private void OnDeserialized(System.Runtime.Serialization.StreamingContext context)
        {
            // Migrate old Ollama config fields (if present as unknown keys)
            if (_extraData != null)
            {
                bool migrated = false;
                if (_extraData.TryGetValue("ollama_url", out var url))
                {
                    ai_url = url.ToString();
                    migrated = true;
                }
                if (_extraData.TryGetValue("enable_ollama", out var eo))
                {
                    enable_ai = eo.Value<bool>();
                    migrated = true;
                }
                if (_extraData.TryGetValue("debug_ollama", out var dbg))
                {
                    debug_ai = dbg.Value<bool>();
                    migrated = true;
                }
                if (_extraData.TryGetValue("model", out var m) && string.IsNullOrEmpty(ai_model))
                {
                    ai_model = m.ToString();
                    migrated = true;
                }
                if (migrated)
                {
                    _configMigrated = true;
                }
                _extraData = null;
            }

            // Migrate: if enable_ai is true but translation_backend is still "none",
            // the user had AI enabled before the backend system was added
            if (enable_ai && translation_backend == "none")
            {
                translation_backend = "llm";
                _configMigrated = true;
            }
        }

        [JsonIgnore]
        internal bool _configMigrated = false;

        // General settings
        public bool capture_keys_only { get; set; } = false;
        public bool translate_mod_ui { get; set; } = false; // Translate the mod's own interface

        /// <summary>
        /// Advanced fallback: Translate at localization string level (ToString/op_Implicit).
        /// WARNING: Ignores font-based enable/disable settings.
        /// Only enable if some text is not being captured by other methods.
        /// </summary>
        public bool translate_localization_fallback { get; set; } = false;

        // Online mode and sync settings
        public bool first_run_completed { get; set; } = false;
        public bool online_mode { get; set; } = false;
        public bool enable_translations { get; set; } = true;

        // Runtime debug toggles — persisted to config.json so developers/translators
        // can keep them off between sessions. End users should leave these at true.
        public bool enable_image_replacement { get; set; } = true;
        public bool enable_font_replacement { get; set; } = true;

        public string settings_hotkey { get; set; } = "F10";

        // Additional hotkeys (empty = disabled). Configured via Options panel only.
        // Each one maps to a toggle/action. Unused by the wizard to avoid conflicts.
        public string toggle_translations_hotkey { get; set; } = "";
        public string toggle_ai_hotkey { get; set; } = "";
        public string toggle_images_hotkey { get; set; } = "";
        public string toggle_fonts_hotkey { get; set; } = "";
        public string toggle_overlay_hotkey { get; set; } = "";
        public string open_inspector_hotkey { get; set; } = "";
        public string open_upload_hotkey { get; set; } = "";
        public string open_exclusion_mode_hotkey { get; set; } = "";
        public string open_text_editor_hotkey { get; set; } = "";
        public string force_scan_hotkey { get; set; } = "";

        [JsonConverter(typeof(EncryptedTokenConverter))]
        public string api_token { get; set; } = null;
        public string api_user { get; set; } = null;
        // Server URL where the token was issued (for security: invalidate if URL changes)
        public string api_token_server { get; set; } = null;

        // Advanced: Override API URLs (null = use compiled default from Directory.Build.props)
        // For self-hosting or testing. Edit config.json manually to use.
        public string api_base_url { get; set; } = null;
        public string website_base_url { get; set; } = null;
        public string sse_base_url { get; set; } = null;

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

        /// <summary>
        /// Enable the corner notification overlay (mod updates, sync, AI queue).
        /// When false, all overlay notifications are hidden.
        /// </summary>
        public bool notifications_enabled { get; set; } = true;

        /// <summary>
        /// Screen corner for notification overlay.
        /// Values: "top-right", "top-left", "bottom-right", "bottom-left"
        /// </summary>
        public string notification_position { get; set; } = "top-right";

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
        /// <summary>URL to external resources (fonts, images)</summary>
        public string ResourcesUrl { get; set; }

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
    /// Stored in translations.json as _font_overrides.
    /// Rules are evaluated in order — first match wins.
    /// </summary>
    public class FontOverrideRule
    {
        /// <summary>
        /// Pattern to match. Prefixes: "path:" (hierarchy glob), "font:" (font name), "text:" (content, regex if /.../).
        /// Without prefix: tries path first, then text substring.
        /// </summary>
        public string match { get; set; }

        /// <summary>
        /// Replacement font name. Null = keep current font (only override size).
        /// </summary>
        public string replacement { get; set; }

        /// <summary>
        /// Size multiplier override. 0 = don't override (use global setting).
        /// Example: 1.0 = original size, 1.5 = 150%, 0.7 = 70%.
        /// </summary>
        public float size_multiplier { get; set; } = 0f;

        /// <summary>
        /// Whether this rule is active.
        /// </summary>
        public bool enabled { get; set; } = true;

        /// <summary>
        /// User comment for identifying the rule purpose.
        /// </summary>
        public string comment { get; set; }
    }

    /// <summary>
    /// Per-font settings for translation.
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

        /// <summary>
        /// Number of times this font has been used for translation.
        /// Used to sort fonts by usage in the UI.
        /// </summary>
        public int usageCount { get; set; } = 0;

        /// <summary>
        /// Origin of this font: "game", "system", "custom", or null for legacy entries.
        /// Used to distinguish fonts with the same name from different sources.
        /// Null/missing in JSON is treated as legacy (unknown origin).
        /// </summary>
        public string origin { get; set; }
    }
}
