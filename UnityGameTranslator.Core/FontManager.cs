using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Manages font detection and fallback font injection for proper Unicode support.
    /// Allows translation to languages with non-Latin scripts (Hindi, Arabic, Chinese, etc.)
    /// Settings are stored in translations.json (_fonts) for sharing with translations.
    /// All font references use object to avoid direct TMPro/UI type dependencies (IL2CPP compat).
    /// </summary>
    public static class FontManager
    {
        // Detected fonts from the game (runtime detection) - keyed by font name
        private static readonly HashSet<string> _detectedTMPFontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _detectedUnityFontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Keep font objects for fallback injection (object to avoid TMP_FontAsset dependency)
        private static readonly Dictionary<string, object> _detectedTMPFontObjects = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Created fallback assets per font (to avoid recreating)
        private static readonly Dictionary<string, object> _fallbackAssets = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Created Unity fonts from system fonts (for legacy UI.Text replacement)
        private static readonly Dictionary<string, Font> _unityFallbackFonts = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);

        // Track font names we created for fallback (to exclude from detection)
        private static readonly HashSet<string> _createdFallbackFontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track font names that failed to create (don't retry)
        private static readonly HashSet<string> _failedFallbackFontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Whether CreateDynamicFontFromOSFont is available on this runtime
        private static bool _dynamicFontCreationAvailable = true;

        // Game fonts (TMP_FontAsset objects already loaded in the game)
        // These work on IL2CPP without CreateDynamicFontFromOSFont
        private static readonly Dictionary<string, object> _gameTMPFonts = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static bool _gameFontsScanned = false;

        // Cache of system fonts
        private static string[] _systemFonts;

        /// <summary>
        /// Gets all detected TMP font names from the game.
        /// </summary>
        public static IReadOnlyCollection<string> DetectedTMPFontNames => _detectedTMPFontNames;

        /// <summary>
        /// Gets all detected Unity Font names from the game.
        /// </summary>
        public static IReadOnlyCollection<string> DetectedUnityFontNames => _detectedUnityFontNames;

        /// <summary>
        /// Gets the list of available system fonts.
        /// </summary>
        public static string[] SystemFonts
        {
            get
            {
                if (_systemFonts == null)
                {
                    _systemFonts = TryGetOSInstalledFontNames();
                }
                return _systemFonts;
            }
        }

        /// <summary>
        /// Safely get installed font names via reflection.
        /// Font.GetOSInstalledFontNames() doesn't exist in all Unity versions/runtimes.
        /// </summary>
        private static string[] TryGetOSInstalledFontNames()
        {
            try
            {
                var method = typeof(Font).GetMethod("GetOSInstalledFontNames",
                    BindingFlags.Public | BindingFlags.Static);
                if (method != null)
                {
                    var result = method.Invoke(null, null) as string[];
                    if (result != null && result.Length > 0)
                        return result;
                }
                TranslatorCore.LogInfo("[FontManager] GetOSInstalledFontNames returned empty, trying filesystem fallback");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] Failed to get system fonts: {ex.Message}");
            }

            // Filesystem fallback for IL2CPP or older Unity
            return TryGetFontNamesFromFilesystem();
        }

        /// <summary>
        /// Fallback: scan filesystem for font files when GetOSInstalledFontNames fails.
        /// </summary>
        private static string[] TryGetFontNamesFromFilesystem()
        {
            var fontNames = new List<string>();

            try
            {
                string[] fontDirs;
                if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
                {
                    string winDir = System.Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
                    fontDirs = new[] { System.IO.Path.Combine(winDir, "Fonts") };
                }
                else if (Application.platform == RuntimePlatform.LinuxPlayer || Application.platform == RuntimePlatform.LinuxEditor)
                {
                    fontDirs = new[] { "/usr/share/fonts", "/usr/local/share/fonts" };
                }
                else if (Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor)
                {
                    fontDirs = new[] { "/Library/Fonts", "/System/Library/Fonts" };
                }
                else
                {
                    return new string[0];
                }

                foreach (var dir in fontDirs)
                {
                    if (!System.IO.Directory.Exists(dir)) continue;
                    try
                    {
                        foreach (var file in System.IO.Directory.GetFiles(dir, "*.ttf", System.IO.SearchOption.AllDirectories))
                        {
                            string name = System.IO.Path.GetFileNameWithoutExtension(file);
                            if (!string.IsNullOrEmpty(name))
                                fontNames.Add(name);
                        }
                        foreach (var file in System.IO.Directory.GetFiles(dir, "*.otf", System.IO.SearchOption.AllDirectories))
                        {
                            string name = System.IO.Path.GetFileNameWithoutExtension(file);
                            if (!string.IsNullOrEmpty(name))
                                fontNames.Add(name);
                        }
                    }
                    catch { }
                }

                if (fontNames.Count > 0)
                    TranslatorCore.LogInfo($"[FontManager] Found {fontNames.Count} fonts via filesystem scan");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] Filesystem font scan failed: {ex.Message}");
            }

            return fontNames.ToArray();
        }

        /// <summary>
        /// Whether any fonts have been detected.
        /// </summary>
        public static bool HasDetectedFonts => _detectedTMPFontNames.Count > 0 || _detectedUnityFontNames.Count > 0;

        /// <summary>
        /// Check if translation is enabled for a specific font.
        /// Returns true by default if no settings exist.
        /// </summary>
        public static bool IsTranslationEnabled(string fontName)
        {
            if (string.IsNullOrEmpty(fontName))
                return true;

            if (TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var settings))
                return settings.enabled;

            return true; // Default: enabled
        }

        // Overloads removed — callers should use IsTranslationEnabled(string fontName) directly.

        /// <summary>
        /// Register a font detected from a text component.
        /// Called from TranslatorPatches/Scanner when intercepting text.
        /// fontType: "TMP", "Unity", "TextMesh", "TMP (alt)", etc.
        /// fontObj: optional font object (TMP_FontAsset or Font) for fallback injection.
        /// </summary>
        public static void RegisterFontObject(object fontObj, string fontType)
        {
            if (fontObj == null) return;

            string fontName = (fontObj is UnityEngine.Object uobj) ? uobj.name : null;
            if (string.IsNullOrEmpty(fontName)) return;

            // Don't register fonts we created for fallback
            if (_createdFallbackFontNames.Contains(fontName))
                return;

            bool isTMP = fontType == "TMP" || fontType == "TMP (alt)";
            bool isNew;

            if (isTMP)
            {
                isNew = _detectedTMPFontNames.Add(fontName);
                if (isNew)
                {
                    _detectedTMPFontObjects[fontName] = fontObj;
                    TranslatorCore.LogInfo($"[FontManager] Detected TMP font: {fontName}");
                    EnsureFontSettings(fontName, fontType);

                    // Auto-apply fallback if configured
                    var settings = GetFontSettings(fontName);
                    if (!string.IsNullOrEmpty(settings?.fallback))
                    {
                        ApplyFallbackToFont(fontObj, settings.fallback);
                    }
                }
            }
            else
            {
                isNew = _detectedUnityFontNames.Add(fontName);
                if (isNew)
                {
                    TranslatorCore.LogInfo($"[FontManager] Detected {fontType} font: {fontName}");
                    EnsureFontSettings(fontName, fontType);
                }
            }
        }

        /// <summary>
        /// Register a font by name only (when we don't have the actual Font object).
        /// Used for tk2d bitmap fonts and other non-standard font systems.
        /// </summary>
        public static void RegisterFontByName(string fontName, string fontType)
        {
            if (string.IsNullOrEmpty(fontName)) return;

            // Don't register fonts we created for fallback
            if (_createdFallbackFontNames.Contains(fontName))
                return;

            // Check if already in settings map
            if (TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var existing))
            {
                // Update type if the new type is more specific (e.g., "TMP (alt)" overrides "TMP")
                // This handles the case where fonts are first detected by the generic TMP scanner
                // and later identified as alternate TMP (TMProOld, etc.)
                // Also update if the existing type is null/empty (from old config)
                if (fontType == "TMP (alt)" && (existing.type == "TMP" || string.IsNullOrEmpty(existing.type)))
                {
                    string oldType = existing.type ?? "null";
                    existing.type = fontType;
                    TranslatorCore.LogInfo($"[FontManager] Updated {fontName} type: {oldType} -> {fontType}");
                }
                return;
            }

            TranslatorCore.LogInfo($"[FontManager] Detected {fontType} font: {fontName}");
            EnsureFontSettings(fontName, fontType);
        }

        /// <summary>
        /// Get settings for a font, or null if not configured.
        /// </summary>
        public static FontSettings GetFontSettings(string fontName)
        {
            if (string.IsNullOrEmpty(fontName))
                return null;

            TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var settings);
            return settings;
        }

        /// <summary>
        /// Get the font scale factor for a font.
        /// Returns 1.0 if no scale is set.
        /// </summary>
        public static float GetFontScale(string fontName)
        {
            if (string.IsNullOrEmpty(fontName))
                return 1.0f;

            if (TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var settings))
                return settings.scale;

            return 1.0f;
        }

        /// <summary>
        /// Update the scale factor for a font.
        /// </summary>
        public static void UpdateFontScale(string fontName, float scale)
        {
            if (string.IsNullOrEmpty(fontName))
                return;

            // Clamp scale to reasonable values
            scale = Math.Max(0.5f, Math.Min(3.0f, scale));

            if (!TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var settings))
            {
                settings = new FontSettings { type = "Unknown" };
                TranslatorCore.FontSettingsMap[fontName] = settings;
            }

            if (Math.Abs(settings.scale - scale) > 0.001f)
            {
                settings.scale = scale;
                TranslatorCore.LogInfo($"[FontManager] Updated scale for '{fontName}' to {scale:F2}");

                // Clear cached original sizes so ApplyFontScale re-reads current sizes
                TranslatorPatches.ClearFontSizeCache();

                // Refresh all text using this font to apply new scale
                if (settings.enabled)
                {
                    TranslatorScanner.RefreshForFont(fontName);
                }

                TranslatorCore.SaveCache();
            }
        }

        /// <summary>
        /// Increment the usage count for a font.
        /// Called when text using this font is translated.
        /// </summary>
        public static void IncrementUsageCount(string fontName)
        {
            if (string.IsNullOrEmpty(fontName))
                return;

            if (TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var settings))
            {
                settings.usageCount++;
            }
        }

        /// <summary>
        /// Ensure a font has an entry in the settings map.
        /// Creates default settings if not exists.
        /// </summary>
        private static void EnsureFontSettings(string fontName, string fontType)
        {
            if (string.IsNullOrEmpty(fontName))
                return;

            if (!TranslatorCore.FontSettingsMap.ContainsKey(fontName))
            {
                TranslatorCore.FontSettingsMap[fontName] = new FontSettings
                {
                    enabled = true,
                    fallback = null,
                    type = fontType
                };
            }
        }

        /// <summary>
        /// Update settings for a font.
        /// </summary>
        public static void UpdateFontSettings(string fontName, bool enabled, string fallbackFont)
        {
            if (string.IsNullOrEmpty(fontName))
                return;

            // Get or create settings
            if (!TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var settings))
            {
                settings = new FontSettings { type = "Unknown" };
                TranslatorCore.FontSettingsMap[fontName] = settings;
            }

            bool enabledChanged = settings.enabled != enabled;
            bool fallbackChanged = settings.fallback != fallbackFont;
            bool wasEnabled = settings.enabled;
            string oldFallback = settings.fallback; // Save BEFORE changing

            settings.enabled = enabled;
            settings.fallback = fallbackFont;

            // Handle fallback changes
            if (fallbackChanged || enabledChanged)
            {
                // Remove old fallback from the font
                RemoveFallbackApplied(fontName);

                // Apply new fallback immediately (don't wait for Harmony patch —
                // on IL2CPP, TypeHelper.SetText doesn't trigger Harmony patches)
                if (enabled && !string.IsNullOrEmpty(fallbackFont))
                {
                    if (_detectedTMPFontObjects.TryGetValue(fontName, out var fontObj))
                    {
                        EnsureFallbackApplied(fontObj, fontName);
                    }
                }
            }

            // Clear font size cache if scale might have changed
            bool scaleChanged = false;
            if (TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var currentSettings))
            {
                // Scale is set separately via UpdateFontScale, but clear cache on any change
                scaleChanged = true; // Always clear to be safe
            }
            if (scaleChanged)
                TranslatorPatches.ClearFontSizeCache();

            // Refresh all text to re-evaluate with new settings
            if (enabledChanged || (fallbackChanged && enabled))
            {
                TranslatorScanner.ClearProcessedCache();
                TranslatorScanner.ForceRefreshAllText();
            }

            // Save changes
            TranslatorCore.SaveCache();
        }

        /// <summary>
        /// Apply fallback font to a specific TMP font asset (via reflection).
        /// </summary>
        private static bool ApplyFallbackToFont(object font, string systemFontName)
        {
            if (font == null || string.IsNullOrEmpty(systemFontName))
                return false;

            try
            {
                string fontName = (font is UnityEngine.Object uobj) ? uobj.name : "unknown";

                // Get or create fallback asset for this system font
                if (!_fallbackAssets.TryGetValue(systemFontName, out var fallbackAsset))
                {
                    fallbackAsset = CreateFallbackAsset(systemFontName);
                    if (fallbackAsset == null)
                        return false;

                    _fallbackAssets[systemFontName] = fallbackAsset;
                    // Mark as created fallback so it won't be registered as game font
                    if (fallbackAsset is UnityEngine.Object fbObj)
                        _createdFallbackFontNames.Add(fbObj.name);
                }

                // Get the fallback list via reflection
                var fallbackList = GetFallbackListReflection(font);
                if (fallbackList == null)
                {
                    TranslatorCore.LogWarning($"[FontManager] Cannot access fallback list for: {fontName}");
                    return false;
                }

                // Check if already added via Contains
                var containsMethod = fallbackList.GetType().GetMethod("Contains");
                if (containsMethod != null)
                {
                    bool alreadyContains = (bool)containsMethod.Invoke(fallbackList, new[] { fallbackAsset });
                    if (alreadyContains) return true;
                }

                // Add our fallback
                var addMethod = fallbackList.GetType().GetMethod("Add");
                if (addMethod != null)
                {
                    addMethod.Invoke(fallbackList, new[] { fallbackAsset });
                    TranslatorCore.LogInfo($"[FontManager] Added fallback '{systemFontName}' to: {fontName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                string fontName = (font is UnityEngine.Object uobj2) ? uobj2.name : "unknown";
                TranslatorCore.LogWarning($"[FontManager] Failed to add fallback to {fontName}: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Remove fallback from a TMP font (via reflection).
        /// </summary>
        private static void RemoveFallbackFromFont(object font)
        {
            if (font == null) return;

            try
            {
                var fallbackList = GetFallbackListReflection(font);
                if (fallbackList == null) return;

                // Clear the entire fallback list — on IL2CPP, Remove() may not work
                // because object identity comparison fails across IL2CPP/managed boundary
                var clearMethod = fallbackList.GetType().GetMethod("Clear");
                if (clearMethod != null)
                {
                    clearMethod.Invoke(fallbackList, null);
                    TranslatorCore.LogInfo($"[FontManager] Cleared fallback list for font");
                }
            }
            catch { }
        }

        // Track which fonts already have our fallback added (by font name)
        private static readonly HashSet<string> _fallbackAppliedFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Ensure the configured fallback font is added to the original font's fallback list.
        /// Called from Harmony patches on every text set — must be fast (cached check).
        /// Does NOT replace the font on the component. TMP uses fallback fonts automatically
        /// for characters not found in the primary font.
        /// </summary>
        public static void EnsureFallbackApplied(object fontObj, string fontName)
        {
            if (fontObj == null || string.IsNullOrEmpty(fontName)) return;

            // Store font object reference for later use (RemoveFallbackApplied needs it)
            if (!_detectedTMPFontObjects.ContainsKey(fontName))
                _detectedTMPFontObjects[fontName] = fontObj;

            // Fast path: already applied
            if (_fallbackAppliedFonts.Contains(fontName)) return;

            // Check if fallback is configured
            if (!TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var settings))
                return;
            if (string.IsNullOrEmpty(settings.fallback))
                return;

            // Check if already tried and failed
            if (_failedFallbackFontNames.Contains(settings.fallback))
                return;

            // Get or create the fallback font asset
            if (!_fallbackAssets.TryGetValue(settings.fallback, out var fallbackAsset))
            {
                fallbackAsset = CreateFallbackAsset(settings.fallback);
                if (fallbackAsset != null)
                {
                    _fallbackAssets[settings.fallback] = fallbackAsset;
                    if (fallbackAsset is UnityEngine.Object uobj && !_gameTMPFonts.ContainsKey(uobj.name))
                        _createdFallbackFontNames.Add(uobj.name);
                }
                else
                {
                    _failedFallbackFontNames.Add(settings.fallback);
                    return;
                }
            }

            // Add to the original font's fallback list
            if (ApplyFallbackToFont(fontObj, settings.fallback))
            {
                _fallbackAppliedFonts.Add(fontName);
                TranslatorCore.LogInfo($"[FontManager] Applied fallback '{settings.fallback}' to font '{fontName}'");
            }
        }

        /// <summary>
        /// Remove fallback from a font and clear the applied cache.
        /// Call when fallback settings change.
        /// </summary>
        public static void RemoveFallbackApplied(string fontName)
        {
            _fallbackAppliedFonts.Remove(fontName);
            if (_detectedTMPFontObjects.TryGetValue(fontName, out var fontObj))
            {
                RemoveFallbackFromFont(fontObj);

                // Force TMP to rebuild its internal lookup caches
                var fontType = fontObj.GetType();
                try
                {
                    var dirtyProp = fontType.GetProperty("IsFontAssetLookupTablesDirty",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (dirtyProp != null && dirtyProp.CanWrite)
                        dirtyProp.SetValue(fontObj, true, null);

                    // Also try ReadFontAssetDefinition to force full rebuild
                    var readMethod = fontType.GetMethod("ReadFontAssetDefinition",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    readMethod?.Invoke(fontObj, null);
                }
                catch { }
            }
        }

        /// <summary>
        /// Get the replacement font for a Unity Font (UI.Text).
        /// Returns null if no fallback is configured.
        /// </summary>
        public static Font GetUnityReplacementFont(string originalFontName)
        {
            if (string.IsNullOrEmpty(originalFontName))
                return null;

            // Check if fallback is configured for this font
            if (!TranslatorCore.FontSettingsMap.TryGetValue(originalFontName, out var settings))
                return null;

            if (string.IsNullOrEmpty(settings.fallback))
                return null;

            // Check if already tried and failed
            if (_failedFallbackFontNames.Contains(settings.fallback))
                return null;

            // Get or create the replacement font
            if (!_unityFallbackFonts.TryGetValue(settings.fallback, out var replacementFont))
            {
                replacementFont = CreateUnityFontFromSystem(settings.fallback);
                if (replacementFont != null)
                {
                    _unityFallbackFonts[settings.fallback] = replacementFont;
                    _createdFallbackFontNames.Add(replacementFont.name);
                }
                else
                {
                    _failedFallbackFontNames.Add(settings.fallback);
                }
            }

            return replacementFont;
        }

        /// <summary>
        /// Get the replacement font for a Unity Font by object.
        /// </summary>
        public static Font GetUnityReplacementFont(object originalFont)
        {
            if (originalFont == null)
                return null;
            string name = (originalFont is UnityEngine.Object uobj) ? uobj.name : null;
            return GetUnityReplacementFont(name);
        }

        /// <summary>
        /// Get the replacement font object for a TMP font.
        /// Returns null if no fallback is configured.
        /// </summary>
        public static object GetTMPReplacementFont(string originalFontName)
        {
            if (string.IsNullOrEmpty(originalFontName))
                return null;

            // Check if fallback is configured for this font
            if (!TranslatorCore.FontSettingsMap.TryGetValue(originalFontName, out var settings))
                return null;

            if (string.IsNullOrEmpty(settings.fallback))
                return null;

            // Check if already tried and failed
            if (_failedFallbackFontNames.Contains(settings.fallback))
                return null;

            // Get or create the replacement font asset
            if (!_fallbackAssets.TryGetValue(settings.fallback, out var replacementFont))
            {
                replacementFont = CreateFallbackAsset(settings.fallback);
                if (replacementFont != null)
                {
                    _fallbackAssets[settings.fallback] = replacementFont;
                    // Only mark as "created" if it's NOT a game font (game fonts should stay detectable)
                    if (replacementFont is UnityEngine.Object uobj && !_gameTMPFonts.ContainsKey(uobj.name))
                        _createdFallbackFontNames.Add(uobj.name);
                }
                else
                {
                    _failedFallbackFontNames.Add(settings.fallback);
                }
            }

            return replacementFont;
        }

        /// <summary>
        /// Get the configured fallback font name for a font.
        /// Returns null if no fallback is configured.
        /// </summary>
        public static string GetConfiguredFallback(string fontName)
        {
            if (string.IsNullOrEmpty(fontName))
                return null;

            if (!TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var settings))
                return null;

            return settings.fallback;
        }

        /// <summary>
        /// Create a Unity Font from a system font name.
        /// </summary>
        private static Font CreateUnityFontFromSystem(string systemFontName)
        {
            try
            {
                if (!_dynamicFontCreationAvailable) return null;

                // Custom SDF fonts are not compatible with Unity Fonts (UI.Text)
                if (IsCustomFont(systemFontName)) return null;

                if (!SystemFonts.Contains(systemFontName))
                {
                    TranslatorCore.LogWarning($"[FontManager] System font not found: {systemFontName}");
                    return null;
                }

                var font = Font.CreateDynamicFontFromOSFont(systemFontName, 32);
                if (font != null)
                {
                    TranslatorCore.LogInfo($"[FontManager] Created Unity font from system: {systemFontName}");
                }
                return font;
            }
            catch (Exception ex)
            {
                // If the method is stripped/unavailable, disable for all future calls
                if (ex.Message.Contains("unstripping") || ex.Message.Contains("Method not found"))
                {
                    _dynamicFontCreationAvailable = false;
                    TranslatorCore.LogWarning($"[FontManager] CreateDynamicFontFromOSFont unavailable on this runtime, disabling system font creation");
                    return null;
                }
                TranslatorCore.LogError($"[FontManager] Failed to create Unity font: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create a TMP fallback asset from a system font or custom font (returns object).
        /// </summary>
        private static object CreateFallbackAsset(string fontName)
        {
            try
            {
                // Check if this is a custom font (from fonts/ folder)
                string cleanName = fontName;
                if (fontName.StartsWith("[Custom] "))
                    cleanName = fontName.Substring(9);

                if (IsCustomFont(cleanName))
                {
                    // Load custom font via CustomFontLoader
                    var customAsset = CustomFontLoader.LoadCustomFont(cleanName);
                    if (customAsset != null)
                    {
                        TranslatorCore.LogInfo($"[FontManager] Created fallback from custom font: {cleanName}");
                        return customAsset;
                    }
                    TranslatorCore.LogWarning($"[FontManager] Failed to load custom font: {cleanName}");
                    return null;
                }

                // Try game fonts first (works on IL2CPP — already valid IL2CPP objects)
                var gameFont = GetGameFont(cleanName);
                if (gameFont != null)
                {
                    TranslatorCore.LogInfo($"[FontManager] Using game font as fallback: {cleanName}");
                    return gameFont;
                }

                // Create a Unity Font from system font name
                Font unityFont = CreateUnityFont(cleanName);
                if (unityFont == null)
                {
                    TranslatorCore.LogWarning($"[FontManager] Cannot create Font for: {cleanName}");
                    return null;
                }

                // Use TMP_FontAsset.CreateFontAsset(Font) — TMP does ALL the work
                // (atlas SDF generation, glyph tables, character tables, material, metrics)
                var tmpAsset = CreateTMPFontAssetFromFont(unityFont);
                if (tmpAsset != null)
                {
                    TranslatorCore.LogInfo($"[FontManager] Created TMP_FontAsset from system font: {cleanName}");
                    return tmpAsset;
                }

                // Legacy fallback: manual creation
                var legacyAsset = CreateTMPFontAsset(unityFont);
                if (legacyAsset != null)
                {
                    TranslatorCore.LogInfo($"[FontManager] Created legacy TMP_FontAsset from: {cleanName}");
                    return legacyAsset;
                }

                TranslatorCore.LogWarning($"[FontManager] All font creation methods failed for: {cleanName}");
                return null;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[FontManager] Error creating fallback: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create a Unity Font from a system font name.
        /// Tries multiple approaches: CreateDynamicFontFromOSFont, new Font(name), reflection.
        /// </summary>
        private static Font CreateUnityFont(string fontName)
        {
            if (!SystemFonts.Contains(fontName))
            {
                TranslatorCore.LogWarning($"[FontManager] System font not found: {fontName}");
                return null;
            }

            // ALL Font creation is done via reflection to avoid JIT resolution crashes on IL2CPP
            // Direct calls like `new Font(name)` or `Font.CreateDynamicFontFromOSFont` cause
            // MissingMethodException at JIT compile time on IL2CPP when methods are stripped.

            var fontType = typeof(Font);

            // Try 1: CreateDynamicFontFromOSFont via reflection
            if (_dynamicFontCreationAvailable)
            {
                try
                {
                    var method = fontType.GetMethod("CreateDynamicFontFromOSFont",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        null, new Type[] { typeof(string), typeof(int) }, null);
                    if (method != null)
                    {
                        var font = method.Invoke(null, new object[] { fontName, 32 }) as Font;
                        if (font != null)
                        {
                            TranslatorCore.LogInfo($"[FontManager] Created Font via CreateDynamicFontFromOSFont: {fontName}");
                            return font;
                        }
                    }
                }
                catch (Exception ex)
                {
                    var msg = ex.InnerException?.Message ?? ex.Message;
                    if (msg.Contains("unstripping") || msg.Contains("Method not found"))
                    {
                        _dynamicFontCreationAvailable = false;
                        TranslatorCore.LogInfo($"[FontManager] CreateDynamicFontFromOSFont unavailable: {msg}");
                    }
                }
            }

            // Try 2: Font() + Internal_CreateFontFromPath — load TTF from file path
            try
            {
                var internalFromPath = fontType.GetMethod("Internal_CreateFontFromPath",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (internalFromPath != null)
                {
                    // Find the TTF file path
                    string fontPath = FindFontFilePath(fontName);
                    if (fontPath != null)
                    {
                        var emptyCtor = fontType.GetConstructor(Type.EmptyTypes);
                        if (emptyCtor != null)
                        {
                            var font = emptyCtor.Invoke(null) as Font;
                            if (font != null)
                            {
                                internalFromPath.Invoke(null, new object[] { font, fontPath });
                                TranslatorCore.LogInfo($"[FontManager] Created Font via Internal_CreateFontFromPath: {fontPath}");
                                return font;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] Internal_CreateFontFromPath failed: {ex.InnerException?.Message ?? ex.Message}");
            }

            // Try 3: Font() + Internal_CreateFont(Font, String) — by name
            try
            {
                var internalCreate = fontType.GetMethod("Internal_CreateFont",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (internalCreate != null)
                {
                    var emptyCtor = fontType.GetConstructor(Type.EmptyTypes);
                    if (emptyCtor != null)
                    {
                        var font = emptyCtor.Invoke(null) as Font;
                        if (font != null)
                        {
                            internalCreate.Invoke(null, new object[] { font, fontName });
                            TranslatorCore.LogInfo($"[FontManager] Created Font via Internal_CreateFont: {fontName}");
                            return font;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] Internal_CreateFont failed: {ex.InnerException?.Message ?? ex.Message}");
            }

            // Try 3: Font() + Internal_CreateDynamicFont(Font, string[], int) — dynamic font from names
            try
            {
                var internalDynamic = fontType.GetMethod("Internal_CreateDynamicFont",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (internalDynamic != null)
                {
                    var emptyCtor = fontType.GetConstructor(Type.EmptyTypes);
                    if (emptyCtor != null)
                    {
                        var font = emptyCtor.Invoke(null) as Font;
                        if (font != null)
                        {
                            // Need to create an Il2CppStringArray for the font names
                            var parameters = internalDynamic.GetParameters();
                            var arrayType = parameters[1].ParameterType;

                            // Try to create the array with the font name
                            object fontNamesArray = null;
                            try
                            {
                                var arrayCtor = arrayType.GetConstructor(new Type[] { typeof(string[]) });
                                if (arrayCtor != null)
                                    fontNamesArray = arrayCtor.Invoke(new object[] { new string[] { fontName } });
                            }
                            catch { }

                            if (fontNamesArray == null)
                            {
                                try
                                {
                                    var arrayCtor = arrayType.GetConstructor(new Type[] { typeof(int) });
                                    if (arrayCtor != null)
                                    {
                                        fontNamesArray = arrayCtor.Invoke(new object[] { 1 });
                                        var indexer = arrayType.GetProperty("Item");
                                        indexer?.SetValue(fontNamesArray, fontName, new object[] { 0 });
                                    }
                                }
                                catch { }
                            }

                            if (fontNamesArray != null)
                            {
                                internalDynamic.Invoke(null, new object[] { font, fontNamesArray, 32 });
                                TranslatorCore.LogInfo($"[FontManager] Created Font via Internal_CreateDynamicFont: {fontName}");
                                return font;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] Internal_CreateDynamicFont failed: {ex.InnerException?.Message ?? ex.Message}");
            }

            // Log available Font constructors/methods for diagnostics
            try
            {
                var ctors = fontType.GetConstructors();
                var methods = fontType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var ctorInfo = string.Join(", ", ctors.Select(c => $"({string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name))})"));
                var factoryMethods = methods.Where(m => m.Name.Contains("Font") || m.Name.Contains("Create"))
                    .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                TranslatorCore.LogWarning($"[FontManager] Font constructors: {ctorInfo}");
                TranslatorCore.LogWarning($"[FontManager] Font factory methods: {string.Join(", ", factoryMethods)}");
            }
            catch { }

            TranslatorCore.LogWarning($"[FontManager] Cannot create Font on this runtime for: {fontName}");
            return null;
        }

        /// <summary>
        /// Find the TTF/OTF file path for a font name.
        /// Scans Windows/Fonts, Linux/Mac font dirs.
        /// </summary>
        private static string FindFontFilePath(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return null;

            string[] fontDirs;
            if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
            {
                string winDir = System.Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
                fontDirs = new[] { System.IO.Path.Combine(winDir, "Fonts") };
            }
            else if (Application.platform == RuntimePlatform.LinuxPlayer || Application.platform == RuntimePlatform.LinuxEditor)
            {
                fontDirs = new[] { "/usr/share/fonts", "/usr/local/share/fonts" };
            }
            else if (Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor)
            {
                fontDirs = new[] { "/Library/Fonts", "/System/Library/Fonts" };
            }
            else
            {
                return null;
            }

            // Search for exact match first, then partial
            foreach (var dir in fontDirs)
            {
                if (!System.IO.Directory.Exists(dir)) continue;
                try
                {
                    // Exact filename match
                    foreach (var ext in new[] { ".ttf", ".otf", ".TTF", ".OTF" })
                    {
                        string path = System.IO.Path.Combine(dir, fontName + ext);
                        if (System.IO.File.Exists(path))
                        {
                            TranslatorCore.LogInfo($"[FontManager] Found font file: {path}");
                            return path;
                        }
                    }

                    // Search recursively
                    foreach (var file in System.IO.Directory.GetFiles(dir, "*.*", System.IO.SearchOption.AllDirectories))
                    {
                        string fileName = System.IO.Path.GetFileNameWithoutExtension(file);
                        string fileExt = System.IO.Path.GetExtension(file).ToLower();
                        if (fileExt != ".ttf" && fileExt != ".otf") continue;

                        if (string.Equals(fileName, fontName, StringComparison.OrdinalIgnoreCase))
                        {
                            TranslatorCore.LogInfo($"[FontManager] Found font file: {file}");
                            return file;
                        }
                    }
                }
                catch { }
            }

            TranslatorCore.LogWarning($"[FontManager] Font file not found for: {fontName}");
            return null;
        }

        /// <summary>
        /// Create a TMP_FontAsset using TMP's own CreateFontAsset method.
        /// This is the best approach — TMP generates the SDF atlas, glyph tables,
        /// character tables, material, and metrics automatically.
        /// </summary>
        private static object CreateTMPFontAssetFromFont(Font font)
        {
            if (font == null || TypeHelper.TMP_FontAssetType == null) return null;

            try
            {
                var tmpFontType = TypeHelper.TMP_FontAssetType;

                // Try ALL CreateFontAsset overloads, match Font by name (IL2CPP: Il2CppUnityEngine.Font)
                foreach (var method in tmpFontType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (method.Name != "CreateFontAsset") continue;
                    if (method.IsGenericMethod) continue;

                    var parameters = method.GetParameters();
                    if (parameters.Length == 0) continue;

                    // Check first param is Font-like (handles IL2CPP type name mismatch)
                    bool firstParamIsFont = typeof(Font).IsAssignableFrom(parameters[0].ParameterType)
                        || parameters[0].ParameterType.Name.Contains("Font");
                    if (!firstParamIsFont) continue;

                    TranslatorCore.LogInfo($"[FontManager] Trying CreateFontAsset overload: {parameters.Length} params ({string.Join(",", parameters.Select(p => p.ParameterType.Name))})");

                    // Simple version: CreateFontAsset(Font)
                    if (parameters.Length == 1)
                    {
                        try
                        {
                            var result = method.Invoke(null, new object[] { font });
                            if (result != null)
                            {
                                TranslatorCore.LogInfo($"[FontManager] TMP_FontAsset.CreateFontAsset(Font) succeeded!");
                                return result;
                            }
                            TranslatorCore.LogWarning($"[FontManager] CreateFontAsset(Font) returned null — font may not have valid data");
                        }
                        catch (Exception ex)
                        {
                            TranslatorCore.LogWarning($"[FontManager] CreateFontAsset(Font) failed: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }

                    // Advanced version with enums
                    if (parameters.Length >= 6)
                    {
                        try
                        {
                            // Build args array matching the exact parameter list
                            var args = new object[parameters.Length];
                            args[0] = font;

                            for (int i = 1; i < parameters.Length; i++)
                            {
                                var pType = parameters[i].ParameterType;
                                string pName = pType.Name;

                                if (pName.Contains("GlyphRenderMode"))
                                {
                                    // SDFAA_HINTED = 4166
                                    args[i] = pType.IsEnum ? Enum.ToObject(pType, 4166) : (object)4166;
                                }
                                else if (pName.Contains("AtlasPopulationMode"))
                                {
                                    // Dynamic = 1
                                    args[i] = pType.IsEnum ? Enum.ToObject(pType, 1) : (object)1;
                                }
                                else if (pType == typeof(int) || pType == typeof(System.Int32))
                                {
                                    // Int params: samplingPointSize, atlasPadding, atlasWidth, atlasHeight
                                    // For 9-param version: extra int is samplingPointSize (index 1)
                                    if (i <= 3) args[i] = 48; // sampling point size / padding
                                    else args[i] = 512; // atlas width/height
                                }
                                else if (pType == typeof(bool))
                                {
                                    args[i] = true; // enableMultiAtlasSupport
                                }
                                else
                                {
                                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                                }
                            }

                            TranslatorCore.LogInfo($"[FontManager] Calling CreateFontAsset with {args.Length} args");
                            var result = method.Invoke(null, args);
                            if (result != null)
                            {
                                TranslatorCore.LogInfo($"[FontManager] TMP_FontAsset.CreateFontAsset(Font, advanced) succeeded!");
                                return result;
                            }
                            TranslatorCore.LogWarning($"[FontManager] CreateFontAsset advanced returned null");
                        }
                        catch (Exception ex)
                        {
                            TranslatorCore.LogWarning($"[FontManager] CreateFontAsset(Font, advanced) failed: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                }

                TranslatorCore.LogWarning("[FontManager] No compatible CreateFontAsset method found");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[FontManager] CreateTMPFontAssetFromFont error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Get the fallback font list from a TMP_FontAsset via reflection.
        /// Handles different TMP versions (fallbackFontAssets vs fallbackFontAssetTable).
        /// Returns an IList (or similar) that supports Add/Remove/Contains.
        /// </summary>
        private static object GetFallbackListReflection(object font)
        {
            if (font == null) return null;
            var fontType = font.GetType();

            // Try fallbackFontAssetTable property first (newer TMP versions)
            try
            {
                var prop = fontType.GetProperty("fallbackFontAssetTable",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var list = prop.GetValue(font, null);
                    if (list != null) return list;
                }
            }
            catch { }

            // Try fallbackFontAssets field (older TMP versions)
            try
            {
                var field = fontType.GetField("fallbackFontAssets",
                    BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    var list = field.GetValue(font);
                    if (list != null) return list;

                    // If null, create and set a new list of the font type
                    Type listType = typeof(List<>).MakeGenericType(fontType);
                    list = Activator.CreateInstance(listType);
                    field.SetValue(font, list);
                    return list;
                }
            }
            catch { }

            return null;
        }

        // Common font style suffixes to try parsing
        private static readonly string[] FontStyleSuffixes = new[]
        {
            " Bold Italic", " BoldItalic", " Bold", " Italic",
            " Light Italic", " LightItalic", " Light",
            " Medium Italic", " MediumItalic", " Medium",
            " Thin Italic", " ThinItalic", " Thin",
            " Black Italic", " BlackItalic", " Black",
            " ExtraBold Italic", " ExtraBold", " SemiBold Italic", " SemiBold",
            " Condensed Bold", " Condensed Italic", " Condensed",
            " Extended Bold", " Extended Italic", " Extended",
            " Ex BT", " BT" // Bitstream fonts
        };

        /// <summary>
        /// Parse a font name into family name and style name.
        /// E.g., "Carlito Bold Italic" -> ("Carlito", "Bold Italic")
        /// </summary>
        private static (string familyName, string styleName) ParseFontName(string fontName)
        {
            if (string.IsNullOrEmpty(fontName))
                return (fontName, "Regular");

            // Try to find a known style suffix
            foreach (var suffix in FontStyleSuffixes)
            {
                if (fontName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    string family = fontName.Substring(0, fontName.Length - suffix.Length).Trim();
                    string style = suffix.Trim();
                    if (!string.IsNullOrEmpty(family))
                        return (family, style);
                }
            }

            // No known suffix found, use as-is with Regular style
            return (fontName, "Regular");
        }

        /// <summary>
        /// Create a TMP_FontAsset from a Unity Font.
        /// Uses reflection to handle different TMP versions.
        /// </summary>
        private static object CreateTMPFontAsset(Font font)
        {
            try
            {
                var tmpFontType = TypeHelper.TMP_FontAssetType;
                if (tmpFontType == null)
                {
                    TranslatorCore.LogWarning("[FontManager] TMP_FontAsset type not resolved");
                    return null;
                }

                // Try simple version first: CreateFontAsset(Font)
                var createMethod = tmpFontType.GetMethod("CreateFontAsset",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    new Type[] { typeof(Font) },
                    null);

                if (createMethod != null)
                {
                    var result = createMethod.Invoke(null, new object[] { font }) ;
                    if (result != null)
                    {
                        TranslatorCore.LogInfo($"[FontManager] Created TMP font via CreateFontAsset(Font)");
                        return result;
                    }
                }

                // Unity 6 / UGUI 2.0: try with string family name
                var createMethodByName = tmpFontType.GetMethod("CreateFontAsset",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    new Type[] { typeof(string), typeof(string), typeof(int) },
                    null);

                if (createMethodByName != null && font != null)
                {
                    // Parse font name to extract family and style
                    var (familyName, styleName) = ParseFontName(font.name);

                    // Try with parsed family/style first
                    TranslatorCore.LogInfo($"[FontManager] Trying CreateFontAsset(\"{familyName}\", \"{styleName}\", 90)");
                    try
                    {
                        var result = createMethodByName.Invoke(null, new object[] { familyName, styleName, 90 }) ;
                        if (result != null)
                            return result;
                    }
                    catch { }

                    // If that failed and we had a style, try with just family name + "Regular"
                    if (styleName != "Regular")
                    {
                        TranslatorCore.LogInfo($"[FontManager] Trying CreateFontAsset(\"{familyName}\", \"Regular\", 90)");
                        try
                        {
                            var result = createMethodByName.Invoke(null, new object[] { familyName, "Regular", 90 }) ;
                            if (result != null)
                                return result;
                        }
                        catch { }
                    }

                    // Last resort: try original name as family with Regular
                    if (familyName != font.name)
                    {
                        TranslatorCore.LogInfo($"[FontManager] Trying CreateFontAsset(\"{font.name}\", \"Regular\", 90)");
                        try
                        {
                            var result = createMethodByName.Invoke(null, new object[] { font.name, "Regular", 90 }) ;
                            if (result != null)
                                return result;
                        }
                        catch { }
                    }
                }

                TranslatorCore.LogWarning($"[FontManager] Failed to create TMP font for '{font?.name}'");
                return null;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[FontManager] CreateTMPFontAsset error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Initialize font manager.
        /// Called from TranslatorCore initialization.
        /// </summary>
        public static void Initialize()
        {
            TranslatorCore.LogInfo("[FontManager] Initialized");
            // Settings are loaded with translations.json, fallbacks applied when fonts are registered
        }

        /// <summary>
        /// Scan all TMP_FontAsset objects loaded in the game.
        /// These are valid IL2CPP objects that can be used as font replacements
        /// without needing CreateDynamicFontFromOSFont (which is stripped on IL2CPP).
        /// </summary>
        public static void ScanGameFonts()
        {
            if (_gameFontsScanned) return;
            _gameFontsScanned = true;

            if (TypeHelper.TMP_FontAssetType == null)
            {
                TranslatorCore.LogWarning("[FontManager] Cannot scan game fonts: TMP_FontAsset type not resolved");
                return;
            }

            try
            {
                var allFonts = TypeHelper.FindAllObjectsOfType(TypeHelper.TMP_FontAssetType);
                if (allFonts == null || allFonts.Length == 0)
                {
                    TranslatorCore.LogInfo("[FontManager] No game TMP fonts found");
                    return;
                }

                foreach (var font in allFonts)
                {
                    if (font == null) continue;
                    string name = font.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    // Skip fonts we created
                    if (_createdFallbackFontNames.Contains(name)) continue;

                    if (!_gameTMPFonts.ContainsKey(name))
                    {
                        _gameTMPFonts[name] = font;
                    }
                }

                TranslatorCore.LogInfo($"[FontManager] Found {_gameTMPFonts.Count} game TMP fonts: {string.Join(", ", GetGameFontNames())}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] Failed to scan game fonts: {ex.Message}");
            }
        }

        /// <summary>
        /// Get names of all TMP fonts loaded in the game.
        /// </summary>
        public static string[] GetGameFontNames()
        {
            if (!_gameFontsScanned) ScanGameFonts();

            var names = new string[_gameTMPFonts.Count];
            _gameTMPFonts.Keys.CopyTo(names, 0);
            System.Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// Get a game font object by name. Returns null if not found.
        /// </summary>
        public static object GetGameFont(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return null;
            if (!_gameFontsScanned) ScanGameFonts();

            _gameTMPFonts.TryGetValue(fontName, out object font);
            return font;
        }

        /// <summary>
        /// Whether dynamic font creation from system fonts is available.
        /// On MelonLoader IL2CPP, CreateDynamicFontFromOSFont is stripped.
        /// </summary>
        public static bool IsDynamicFontCreationAvailable => _dynamicFontCreationAvailable;

        /// <summary>
        /// Get font info for display in UI.
        /// Shows all fonts from saved settings + newly detected fonts.
        /// Deduplicates by font name (case-insensitive) to avoid showing duplicates.
        /// </summary>
        public static List<FontDisplayInfo> GetDetectedFontsInfo()
        {
            var result = new List<FontDisplayInfo>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First: add all fonts from saved settings (FontSettingsMap)
            // These are fonts that were previously detected and saved in translations.json
            foreach (var kvp in TranslatorCore.FontSettingsMap)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;

                seenNames.Add(kvp.Key);
                var settings = kvp.Value;

                // Determine type from saved settings or default to "Unknown"
                string fontType = settings?.type ?? "Unknown";

                result.Add(new FontDisplayInfo
                {
                    Name = kvp.Key,
                    Type = fontType,
                    SupportsFallback = true,
                    Enabled = settings?.enabled ?? true,
                    FallbackFont = settings?.fallback,
                    Scale = settings?.scale ?? 1.0f,
                    UsageCount = settings?.usageCount ?? 0
                });
            }

            // Then: add newly detected TMP fonts not already in settings
            foreach (var fontName in _detectedTMPFontNames)
            {
                if (string.IsNullOrEmpty(fontName)) continue;
                if (!seenNames.Add(fontName)) continue;

                var settings = GetFontSettings(fontName);
                result.Add(new FontDisplayInfo
                {
                    Name = fontName,
                    Type = "TextMeshPro",
                    SupportsFallback = true,
                    Enabled = settings?.enabled ?? true,
                    FallbackFont = settings?.fallback,
                    Scale = settings?.scale ?? 1.0f,
                    UsageCount = settings?.usageCount ?? 0
                });
            }

            // Then: add newly detected Unity fonts not already in settings
            foreach (var fontName in _detectedUnityFontNames)
            {
                if (string.IsNullOrEmpty(fontName)) continue;
                if (!seenNames.Add(fontName)) continue;

                var settings = GetFontSettings(fontName);
                result.Add(new FontDisplayInfo
                {
                    Name = fontName,
                    Type = "Unity Font",
                    SupportsFallback = true,
                    Enabled = settings?.enabled ?? true,
                    FallbackFont = settings?.fallback,
                    Scale = settings?.scale ?? 1.0f,
                    UsageCount = settings?.usageCount ?? 0
                });
            }

            // Sort by usage count (most used first), then by name
            result.Sort((a, b) =>
            {
                int usageCompare = b.UsageCount.CompareTo(a.UsageCount);
                if (usageCompare != 0) return usageCompare;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }

        /// <summary>
        /// Clear all detected fonts (for testing/reset).
        /// </summary>
        public static void Clear()
        {
            _detectedTMPFontNames.Clear();
            _detectedUnityFontNames.Clear();
            _detectedTMPFontObjects.Clear();
            _fallbackAssets.Clear();
            _unityFallbackFonts.Clear();
            _createdFallbackFontNames.Clear();
            _failedFallbackFontNames.Clear();
            _gameTMPFonts.Clear();
            _gameFontsScanned = false;
            _fallbackAppliedFonts.Clear();
        }

        /// <summary>
        /// Gets the names of all available custom fonts (from fonts/ folder).
        /// These are SDF fonts that can be used as fallback for any TMP font.
        /// </summary>
        public static string[] GetCustomFontNames()
        {
            return CustomFontLoader.GetCustomFontNames();
        }

        /// <summary>
        /// Checks if a font name is a custom font (from fonts/ folder).
        /// </summary>
        public static bool IsCustomFont(string fontName)
        {
            if (string.IsNullOrEmpty(fontName))
                return false;

            // Check if it has the [Custom] prefix
            if (fontName.StartsWith("[Custom] "))
                return true;

            // Check if it's in the custom fonts list
            var customFonts = CustomFontLoader.CustomFonts;
            return customFonts.ContainsKey(fontName);
        }

        /// <summary>
        /// Gets or loads a custom font asset by name.
        /// </summary>
        public static object GetCustomFontAsset(string fontName)
        {
            // Strip [Custom] prefix if present
            if (fontName.StartsWith("[Custom] "))
                fontName = fontName.Substring(9);

            return CustomFontLoader.LoadCustomFont(fontName);
        }
    }

    /// <summary>
    /// Font information for UI display.
    /// </summary>
    public class FontDisplayInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool SupportsFallback { get; set; }
        public bool Enabled { get; set; }
        public string FallbackFont { get; set; }
        public float Scale { get; set; } = 1.0f;
        public int UsageCount { get; set; } = 0;
    }
}
