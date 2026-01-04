using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TMPro;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Manages font detection and fallback font injection for proper Unicode support.
    /// Allows translation to languages with non-Latin scripts (Hindi, Arabic, Chinese, etc.)
    /// Settings are stored in translations.json (_fonts) for sharing with translations.
    /// </summary>
    public static class FontManager
    {
        // Detected fonts from the game (runtime detection)
        private static readonly HashSet<TMP_FontAsset> _detectedTMPFonts = new HashSet<TMP_FontAsset>();
        private static readonly HashSet<Font> _detectedUnityFonts = new HashSet<Font>();

        // Created fallback assets per font (to avoid recreating)
        private static readonly Dictionary<string, TMP_FontAsset> _fallbackAssets = new Dictionary<string, TMP_FontAsset>();

        // Created Unity fonts from system fonts (for legacy UI.Text replacement)
        private static readonly Dictionary<string, Font> _unityFallbackFonts = new Dictionary<string, Font>();

        // Cache of system fonts
        private static string[] _systemFonts;

        /// <summary>
        /// Gets all detected TMP_FontAsset from the game.
        /// </summary>
        public static IReadOnlyCollection<TMP_FontAsset> DetectedTMPFonts => _detectedTMPFonts;

        /// <summary>
        /// Gets all detected Unity Font from the game.
        /// </summary>
        public static IReadOnlyCollection<Font> DetectedUnityFonts => _detectedUnityFonts;

        /// <summary>
        /// Gets the list of available system fonts.
        /// </summary>
        public static string[] SystemFonts
        {
            get
            {
                if (_systemFonts == null)
                {
                    try
                    {
                        _systemFonts = Font.GetOSInstalledFontNames();
                        if (_systemFonts == null)
                            _systemFonts = new string[0];
                    }
                    catch (Exception ex)
                    {
                        TranslatorCore.LogWarning($"[FontManager] Failed to get system fonts: {ex.Message}");
                        _systemFonts = new string[0];
                    }
                }
                return _systemFonts;
            }
        }

        /// <summary>
        /// Whether any fonts have been detected.
        /// </summary>
        public static bool HasDetectedFonts => _detectedTMPFonts.Count > 0 || _detectedUnityFonts.Count > 0;

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

        /// <summary>
        /// Check if translation is enabled for a TMP font.
        /// </summary>
        public static bool IsTranslationEnabled(TMP_FontAsset font)
        {
            return font == null || IsTranslationEnabled(font.name);
        }

        /// <summary>
        /// Check if translation is enabled for a Unity font.
        /// </summary>
        public static bool IsTranslationEnabled(Font font)
        {
            return font == null || IsTranslationEnabled(font.name);
        }

        /// <summary>
        /// Register a TMP_FontAsset detected from a text component.
        /// Called from TranslatorPatches when intercepting text.
        /// </summary>
        public static void RegisterFont(TMP_FontAsset font)
        {
            if (font == null) return;

            if (_detectedTMPFonts.Add(font))
            {
                TranslatorCore.LogInfo($"[FontManager] Detected TMP font: {font.name}");

                // Ensure font has entry in settings map
                EnsureFontSettings(font.name, "TMP");

                // Auto-apply fallback if configured
                var settings = GetFontSettings(font.name);
                if (!string.IsNullOrEmpty(settings?.fallback))
                {
                    ApplyFallbackToFont(font, settings.fallback);
                }
            }
        }

        /// <summary>
        /// Register a Unity Font detected from a text component.
        /// Called from TranslatorPatches when intercepting text.
        /// </summary>
        public static void RegisterFont(Font font)
        {
            if (font == null) return;

            if (_detectedUnityFonts.Add(font))
            {
                TranslatorCore.LogInfo($"[FontManager] Detected Unity font: {font.name}");

                // Ensure font has entry in settings map
                EnsureFontSettings(font.name, "Unity");
            }
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

            settings.enabled = enabled;
            settings.fallback = fallbackFont;

            // Apply or remove fallback for TMP fonts
            var tmpFont = _detectedTMPFonts.FirstOrDefault(f => f?.name == fontName);
            if (tmpFont != null)
            {
                if (!string.IsNullOrEmpty(fallbackFont) && fallbackChanged)
                {
                    ApplyFallbackToFont(tmpFont, fallbackFont);
                }
                else if (string.IsNullOrEmpty(fallbackFont))
                {
                    RemoveFallbackFromFont(tmpFont);
                }
            }

            // Handle translation toggle for this font's components
            if (enabledChanged)
            {
                if (wasEnabled && !enabled)
                {
                    // Translation disabled for this font: restore originals
                    TranslatorScanner.RestoreOriginalsForFont(fontName);
                }
                else if (!wasEnabled && enabled)
                {
                    // Translation enabled for this font: refresh to translate
                    TranslatorScanner.RefreshForFont(fontName);
                }
            }
            else if (fallbackChanged && enabled)
            {
                // Fallback font changed while translation enabled: refresh to apply new font
                // This is needed for Unity Fonts where GetUnityReplacementFont() is called on each text set
                TranslatorScanner.RefreshForFont(fontName);
            }

            // Save changes
            TranslatorCore.SaveCache();
        }

        /// <summary>
        /// Apply fallback font to a specific TMP font asset.
        /// </summary>
        private static bool ApplyFallbackToFont(TMP_FontAsset font, string systemFontName)
        {
            if (font == null || string.IsNullOrEmpty(systemFontName))
                return false;

            try
            {
                // Get or create fallback asset for this system font
                if (!_fallbackAssets.TryGetValue(systemFontName, out var fallbackAsset))
                {
                    fallbackAsset = CreateFallbackAsset(systemFontName);
                    if (fallbackAsset == null)
                        return false;

                    _fallbackAssets[systemFontName] = fallbackAsset;
                }

                // Get the fallback list
                var fallbackList = GetFallbackList(font);
                if (fallbackList == null)
                {
                    TranslatorCore.LogWarning($"[FontManager] Cannot access fallback list for: {font.name}");
                    return false;
                }

                // Check if already added
                if (fallbackList.Contains(fallbackAsset))
                    return true;

                // Add our fallback
                fallbackList.Add(fallbackAsset);
                TranslatorCore.LogInfo($"[FontManager] Added fallback '{systemFontName}' to: {font.name}");
                return true;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] Failed to add fallback to {font.name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Remove fallback from a TMP font.
        /// </summary>
        private static void RemoveFallbackFromFont(TMP_FontAsset font)
        {
            if (font == null) return;

            try
            {
                var fallbackList = GetFallbackList(font);
                if (fallbackList == null) return;

                // Remove any of our created fallback assets
                foreach (var fallback in _fallbackAssets.Values)
                {
                    fallbackList.Remove(fallback);
                }
            }
            catch { }
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

            // Get or create the replacement font
            if (!_unityFallbackFonts.TryGetValue(settings.fallback, out var replacementFont))
            {
                replacementFont = CreateUnityFontFromSystem(settings.fallback);
                if (replacementFont != null)
                {
                    _unityFallbackFonts[settings.fallback] = replacementFont;
                }
            }

            return replacementFont;
        }

        /// <summary>
        /// Get the replacement font for a Unity Font component.
        /// </summary>
        public static Font GetUnityReplacementFont(Font originalFont)
        {
            if (originalFont == null)
                return null;
            return GetUnityReplacementFont(originalFont.name);
        }

        /// <summary>
        /// Create a Unity Font from a system font name.
        /// </summary>
        private static Font CreateUnityFontFromSystem(string systemFontName)
        {
            try
            {
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
                TranslatorCore.LogError($"[FontManager] Failed to create Unity font: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create a TMP fallback asset from a system font.
        /// </summary>
        private static TMP_FontAsset CreateFallbackAsset(string systemFontName)
        {
            try
            {
                // Check if font exists in system
                if (!SystemFonts.Contains(systemFontName))
                {
                    TranslatorCore.LogWarning($"[FontManager] System font not found: {systemFontName}");
                    return null;
                }

                // Create Unity font from system font
                var unityFont = Font.CreateDynamicFontFromOSFont(systemFontName, 32);
                if (unityFont == null)
                {
                    TranslatorCore.LogError($"[FontManager] Failed to create font from: {systemFontName}");
                    return null;
                }

                // Create TMP_FontAsset from Unity font
                var tmpAsset = CreateTMPFontAsset(unityFont);
                if (tmpAsset == null)
                {
                    TranslatorCore.LogWarning($"[FontManager] Failed to create TMP_FontAsset from '{systemFontName}' - TMP version may not support dynamic font creation");
                    return null;
                }

                TranslatorCore.LogInfo($"[FontManager] Created fallback font asset from: {systemFontName}");
                return tmpAsset;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[FontManager] Error creating fallback: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get the fallback font list from a TMP_FontAsset.
        /// Handles different TMP versions (fallbackFontAssets vs fallbackFontAssetTable).
        /// </summary>
        private static List<TMP_FontAsset> GetFallbackList(TMP_FontAsset font)
        {
            // Try fallbackFontAssetTable first (newer TMP versions)
            try
            {
                var prop = font.GetType().GetProperty("fallbackFontAssetTable");
                if (prop != null)
                {
                    var list = prop.GetValue(font) as List<TMP_FontAsset>;
                    if (list != null)
                        return list;
                }
            }
            catch { }

            // Try fallbackFontAssets field (older TMP versions)
            try
            {
                var field = font.GetType().GetField("fallbackFontAssets",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    var list = field.GetValue(font) as List<TMP_FontAsset>;
                    if (list != null)
                        return list;

                    // If null, create and set a new list
                    list = new List<TMP_FontAsset>();
                    field.SetValue(font, list);
                    return list;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Create a TMP_FontAsset from a Unity Font.
        /// Uses reflection to handle different TMP versions.
        /// </summary>
        private static TMP_FontAsset CreateTMPFontAsset(Font font)
        {
            try
            {
                var tmpFontType = typeof(TMP_FontAsset);

                // Try simple version first: CreateFontAsset(Font)
                var createMethod = tmpFontType.GetMethod("CreateFontAsset",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    new Type[] { typeof(Font) },
                    null);

                if (createMethod != null)
                {
                    var result = createMethod.Invoke(null, new object[] { font }) as TMP_FontAsset;
                    if (result != null)
                        return result;
                }

                // Try version with more parameters for dynamic atlas
                var createMethodFull = tmpFontType.GetMethod("CreateFontAsset",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    new Type[] { typeof(Font), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) },
                    null);

                if (createMethodFull != null)
                {
                    // CreateFontAsset(font, samplingPointSize, atlasPadding, renderMode, atlasWidth, atlasHeight)
                    var result = createMethodFull.Invoke(null, new object[] { font, 32, 4, 4165, 1024, 1024 }) as TMP_FontAsset;
                    if (result != null)
                        return result;
                }

                TranslatorCore.LogWarning("[FontManager] CreateFontAsset method not found");
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
        /// Get font info for display in UI.
        /// </summary>
        public static List<FontDisplayInfo> GetDetectedFontsInfo()
        {
            var result = new List<FontDisplayInfo>();

            foreach (var font in _detectedTMPFonts)
            {
                if (font == null) continue;

                var settings = GetFontSettings(font.name);
                result.Add(new FontDisplayInfo
                {
                    Name = font.name,
                    Type = "TextMeshPro",
                    SupportsFallback = true,
                    Enabled = settings?.enabled ?? true,
                    FallbackFont = settings?.fallback
                });
            }

            foreach (var font in _detectedUnityFonts)
            {
                if (font == null) continue;

                var settings = GetFontSettings(font.name);
                result.Add(new FontDisplayInfo
                {
                    Name = font.name,
                    Type = "Unity Font",
                    SupportsFallback = true, // We can replace the font directly
                    Enabled = settings?.enabled ?? true,
                    FallbackFont = settings?.fallback
                });
            }

            return result;
        }

        /// <summary>
        /// Clear all detected fonts (for testing/reset).
        /// </summary>
        public static void Clear()
        {
            _detectedTMPFonts.Clear();
            _detectedUnityFonts.Clear();
            _fallbackAssets.Clear();
            _unityFallbackFonts.Clear();
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
    }
}
