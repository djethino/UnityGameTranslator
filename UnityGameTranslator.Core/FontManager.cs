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

        // Track fonts we created for fallback (to exclude from detection)
        private static readonly HashSet<Font> _createdFallbackFonts = new HashSet<Font>();
        private static readonly HashSet<TMP_FontAsset> _createdFallbackTMPFonts = new HashSet<TMP_FontAsset>();

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

            // Don't register fonts we created for fallback
            if (_createdFallbackTMPFonts.Contains(font))
                return;

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

            // Don't register fonts we created for fallback
            if (_createdFallbackFonts.Contains(font))
                return;

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

                // Refresh all text using this font to apply new scale
                if (settings.enabled)
                {
                    TranslatorScanner.RefreshForFont(fontName);
                }

                TranslatorCore.SaveCache();
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
                    // Mark as created fallback so it won't be registered as game font
                    _createdFallbackTMPFonts.Add(fallbackAsset);
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
                    // Mark as created fallback so it won't be registered as game font
                    _createdFallbackFonts.Add(replacementFont);
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
        /// Get the replacement TMP_FontAsset for a TMP font.
        /// Returns null if no fallback is configured.
        /// </summary>
        public static TMP_FontAsset GetTMPReplacementFont(string originalFontName)
        {
            if (string.IsNullOrEmpty(originalFontName))
                return null;

            // Check if fallback is configured for this font
            if (!TranslatorCore.FontSettingsMap.TryGetValue(originalFontName, out var settings))
                return null;

            if (string.IsNullOrEmpty(settings.fallback))
                return null;

            // Get or create the replacement TMP_FontAsset
            if (!_fallbackAssets.TryGetValue(settings.fallback, out var replacementFont))
            {
                replacementFont = CreateFallbackAsset(settings.fallback);
                if (replacementFont != null)
                {
                    _fallbackAssets[settings.fallback] = replacementFont;
                    // Mark as created fallback so it won't be registered as game font
                    _createdFallbackTMPFonts.Add(replacementFont);
                }
            }

            return replacementFont;
        }

        /// <summary>
        /// Get the replacement TMP_FontAsset for a TMP font.
        /// </summary>
        public static TMP_FontAsset GetTMPReplacementFont(TMP_FontAsset originalFont)
        {
            if (originalFont == null)
                return null;
            return GetTMPReplacementFont(originalFont.name);
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
                        var result = createMethodByName.Invoke(null, new object[] { familyName, styleName, 90 }) as TMP_FontAsset;
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
                            var result = createMethodByName.Invoke(null, new object[] { familyName, "Regular", 90 }) as TMP_FontAsset;
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
                            var result = createMethodByName.Invoke(null, new object[] { font.name, "Regular", 90 }) as TMP_FontAsset;
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
                    Scale = settings?.scale ?? 1.0f
                });
            }

            // Then: add newly detected TMP fonts not already in settings
            foreach (var font in _detectedTMPFonts)
            {
                if (font == null) continue;

                // Skip if we've already seen this font name (case-insensitive)
                if (!seenNames.Add(font.name))
                    continue;

                var settings = GetFontSettings(font.name);
                result.Add(new FontDisplayInfo
                {
                    Name = font.name,
                    Type = "TextMeshPro",
                    SupportsFallback = true,
                    Enabled = settings?.enabled ?? true,
                    FallbackFont = settings?.fallback,
                    Scale = settings?.scale ?? 1.0f
                });
            }

            // Then: add newly detected Unity fonts not already in settings
            foreach (var font in _detectedUnityFonts)
            {
                if (font == null) continue;

                // Skip if we've already seen this font name (case-insensitive)
                if (!seenNames.Add(font.name))
                    continue;

                var settings = GetFontSettings(font.name);
                result.Add(new FontDisplayInfo
                {
                    Name = font.name,
                    Type = "Unity Font",
                    SupportsFallback = true,
                    Enabled = settings?.enabled ?? true,
                    FallbackFont = settings?.fallback,
                    Scale = settings?.scale ?? 1.0f
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
            _createdFallbackFonts.Clear();
            _createdFallbackTMPFonts.Clear();
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
    }
}
