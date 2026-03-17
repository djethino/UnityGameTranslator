using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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

        /// <summary>
        /// Check if a Font object is one of our clones (for UI.Text replacement).
        /// Cloned fonts must NOT have their fontSize changed (causes atlas corruption).
        /// Use localScale on the component instead.
        /// </summary>
        public static bool IsClonedFont(Font font)
        {
            if (font == null) return false;
            foreach (var kvp in _unityFallbackFonts)
            {
                if (kvp.Value == font) return true;
            }
            return false;
        }

        /// <summary>
        /// Clear all UI.Text font clones from cache. They will be recreated on next use.
        /// Combined with ForceRefreshAllText, this is equivalent to a manual font change.
        /// </summary>
        private static bool _textureRebuiltSubscribed = false;
        private static bool _textureRebuiltHandling = false;
        private static string _subscribedOriginalFont = null;
        private static string _subscribedFallback = null;

        /// <summary>
        /// Subscribe to Font.textureRebuilt via add_textureRebuilt method (IL2CPP compatible).
        /// When the atlas rebuilds, recreate the clone to prevent corruption.
        /// </summary>
        private static void SubscribeTextureRebuilt(string originalFontName, string fallbackName)
        {
            if (_textureRebuiltSubscribed) return;

            _subscribedOriginalFont = originalFontName;
            _subscribedFallback = fallbackName;

            try
            {
                // Find add_textureRebuilt METHOD (not event — IL2CPP wrapping hides the event)
                var addMethod = typeof(Font).GetMethod("add_textureRebuilt",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (addMethod != null)
                {
                    Action<Font> handler = OnFontTextureRebuilt;
                    addMethod.Invoke(null, new object[] { handler });
                    _textureRebuiltSubscribed = true;
                    TranslatorCore.LogInfo("[FontManager] Subscribed to Font.textureRebuilt via add method");
                }
                else
                {
                    TranslatorCore.LogWarning("[FontManager] add_textureRebuilt method not found");
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] Failed to subscribe textureRebuilt: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private static void OnFontTextureRebuilt(Font rebuiltFont)
        {
            // Only act on our cloned fonts
            if (_textureRebuiltHandling) return;
            if (!IsClonedFont(rebuiltFont)) return;

            _textureRebuiltHandling = true;
            try
            {
                TranslatorCore.LogInfo($"[FontManager] Clone atlas rebuilt for '{rebuiltFont.name}', recreating");
                RemoveCloneAndRestore(_subscribedOriginalFont, _subscribedFallback);
                TranslatorScanner.ClearProcessedCache();
                TranslatorScanner.ForceRefreshAllText();
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] textureRebuilt handler error: {ex.Message}");
            }
            finally
            {
                _textureRebuiltHandling = false;
            }
        }

        /// <summary>
        /// Remove a clone from cache and restore all components to their original font.
        /// Exactly what manual font change does. Next GetUnityReplacementFont creates a fresh clone.
        /// </summary>
        public static void RemoveCloneAndRestore(string originalFontName, string fallbackName)
        {
            if (!string.IsNullOrEmpty(fallbackName))
            {
                _unityFallbackFonts.Remove(fallbackName);
                _failedFallbackFontNames.Remove(fallbackName);
            }

            RestoreOriginalFontNames(originalFontName);
            RestoreAllComponentsForFont(originalFontName);

            TranslatorCore.LogInfo($"[FontManager] Removed clone '{fallbackName}' and restored components for '{originalFontName}'");
        }

        // Track font names we created for fallback (to exclude from detection)
        private static readonly HashSet<string> _createdFallbackFontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track font names that failed to create (don't retry)
        private static readonly HashSet<string> _failedFallbackFontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Whether CreateDynamicFontFromOSFont is available on this runtime
        private static bool _dynamicFontCreationAvailable = true;

        // Delegate for ICall to CreateDynamicFontFromOSFont native function
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate IntPtr CreateDynamicFontDelegate(IntPtr fontName, int size);

        // Game fonts (TMP_FontAsset objects already loaded in the game)
        // These work on IL2CPP without CreateDynamicFontFromOSFont
        private static readonly Dictionary<string, object> _gameTMPFonts = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Font> _gameUnityFonts = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
        private static bool _gameFontsScanned = false;

        // Cache of system fonts
        private static string[] _systemFonts;

        // Mapping: font display name → TTF file path (built from GetOSInstalledFontNames + GetPathsToOSFonts)
        private static Dictionary<string, string> _systemFontPaths;

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
        /// Also builds a name→path mapping using GetPathsToOSFonts (Unity 2019.1+).
        /// Falls back to filesystem scan when APIs are unavailable (IL2CPP, old Unity).
        /// </summary>
        private static string[] TryGetOSInstalledFontNames()
        {
            _systemFontPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var namesMethod = typeof(Font).GetMethod("GetOSInstalledFontNames",
                    BindingFlags.Public | BindingFlags.Static);
                if (namesMethod != null)
                {
                    var names = namesMethod.Invoke(null, null) as string[];
                    if (names != null && names.Length > 0)
                    {
                        // Try GetPathsToOSFonts (Unity 2019.1+) — same order as names
                        try
                        {
                            var pathsMethod = typeof(Font).GetMethod("GetPathsToOSFonts",
                                BindingFlags.Public | BindingFlags.Static);
                            if (pathsMethod != null)
                            {
                                var paths = pathsMethod.Invoke(null, null) as string[];
                                if (paths != null && paths.Length == names.Length)
                                {
                                    for (int i = 0; i < names.Length; i++)
                                    {
                                        if (!string.IsNullOrEmpty(names[i]) && !string.IsNullOrEmpty(paths[i]))
                                        {
                                            if (!_systemFontPaths.ContainsKey(names[i]))
                                                _systemFontPaths[names[i]] = paths[i];
                                        }
                                    }
                                    TranslatorCore.LogInfo($"[FontManager] Built font path mapping: {_systemFontPaths.Count} entries");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            TranslatorCore.LogInfo($"[FontManager] GetPathsToOSFonts unavailable: {ex.Message}");
                        }

                        return names;
                    }
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
        /// Get the TTF file path for a system font name.
        /// Uses the name→path mapping from GetPathsToOSFonts, then filesystem scan fallback.
        /// </summary>
        public static string GetSystemFontPath(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return null;

            // Ensure system fonts are loaded (triggers TryGetOSInstalledFontNames)
            var _ = SystemFonts;

            // 1. Direct lookup from GetPathsToOSFonts mapping
            string path;
            if (_systemFontPaths != null && _systemFontPaths.TryGetValue(fontName, out path))
                return path;

            // 2. Fallback: search filesystem by normalized name
            return CustomFontLoader.FindSystemTtfPath(fontName);
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
        /// <summary>
        /// Register a Unity Font object discovered at runtime (from Harmony prefix).
        /// Used for UI.Text font replacement on IL2CPP where FindObjectsOfTypeAll fails.
        /// </summary>
        public static void RegisterUnityFontObject(string fontName, object fontObj)
        {
            if (string.IsNullOrEmpty(fontName) || fontObj == null) return;
            if (_createdFallbackFontNames.Contains(fontName)) return;
            var font = fontObj as Font;
            if (font != null && !_gameUnityFonts.ContainsKey(fontName))
                _gameUnityFonts[fontName] = font;
        }

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

                // DON'T clear font size cache — it stores TRUE originals
                // Clearing would cause the scaled size to be read as "original"

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
            string oldFallback = settings.fallback; // Save BEFORE changing

            settings.enabled = enabled;
            settings.fallback = fallbackFont;

            // Handle fallback or enabled changes
            if (fallbackChanged || enabledChanged)
            {
                // Remove old fallback tracking (both forward and reverse keys)
                _fallbackAppliedFonts.Remove(fontName);
                _fallbackAppliedFonts.Remove(fontName + "_reverse");

                // Remove old fallback from the game font's actual fallback list
                // Without this, TMProOld keeps using the old fallback (first in list wins)
                if (!string.IsNullOrEmpty(oldFallback))
                {
                    RemoveFallbackFromFont(fontName, oldFallback);
                }

                // Clear Unity font cache for this fallback so it's re-created
                if (!string.IsNullOrEmpty(oldFallback))
                {
                    _unityFallbackFonts.Remove(oldFallback);
                    _failedFallbackFontNames.Remove(oldFallback);
                }

                // Restore original fontNames if we modified the game font
                RestoreOriginalFontNames(fontName);

                // Restore original fonts on ALL components that had this font replaced.
                // Without this, components keep the old replacement font forever.
                RestoreAllComponentsForFont(fontName);

                // Clear ALL font-replaced cache so components get re-evaluated on rescan.
                // Some components may be in _fontReplacedComponentIds but NOT in
                // _originalFontsPerComponent (e.g. already-replaced on first encounter).
                _fontReplacedComponentIds.Clear();

                TranslatorCore.LogInfo($"[FontManager] Font settings changed for '{fontName}': " +
                    $"enabled={enabled}, fallback='{fallbackFont ?? "(none)"}' (was '{oldFallback ?? "(none)"}')");
            }

            // Refresh all text to re-evaluate with new settings
            if (enabledChanged || fallbackChanged)
            {
                // Each font switch creates a NEW clone with its own glyph cache.
                // No bump hack needed — the new Font object has no cached glyphs.
                TranslatorScanner.ClearProcessedCache();
                TranslatorScanner.ForceRefreshAllText();
            }

            // Save changes
            TranslatorCore.SaveCache();
        }

        /// <summary>
        /// Restore original fonts on all components tracked for a given font name.
        /// Called when font settings change so the new settings can be applied fresh.
        /// </summary>
        private static void RestoreAllComponentsForFont(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return;

            // Collect instance IDs to restore (can't modify dictionary during iteration)
            var toRestore = new List<int>();
            foreach (var kvp in _originalFontsPerComponent)
            {
                string origName = (kvp.Value is UnityEngine.Object uobj) ? uobj.name : null;
                if (origName == fontName)
                {
                    toRestore.Add(kvp.Key);
                }
            }

            if (toRestore.Count == 0) return;

            TranslatorCore.LogInfo($"[FontManager] Restoring {toRestore.Count} components to original font '{fontName}'");

            foreach (int instanceId in toRestore)
            {
                if (!_originalFontsPerComponent.TryGetValue(instanceId, out var originalFont))
                    continue;

                if (_replacedComponentRefs.TryGetValue(instanceId, out var component) && component != null)
                {
                    TypeHelper.SetFont(component, originalFont);
                    SetFontSharedMaterial(component, originalFont);
                    TypeHelper.ForceMeshUpdate(component);
                }

                _originalFontsPerComponent.Remove(instanceId);
                _replacedComponentRefs.Remove(instanceId);
                _fontReplacedComponentIds.Remove(instanceId);
            }
        }

        /// <summary>
        /// Set fontSharedMaterial on a TMP component from a font asset's material.
        /// Uses IL2CPP-safe casting for proxy objects.
        /// </summary>
        private static void SetFontSharedMaterial(object component, object fontAsset)
        {
            if (component == null || fontAsset == null) return;
            try
            {
                var materialField = fontAsset.GetType().GetField("material",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (materialField == null) return;

                var fontMaterial = materialField.GetValue(fontAsset);
                if (fontMaterial == null) return;

                var fontSharedMatProp = component.GetType().GetProperty("fontSharedMaterial",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (fontSharedMatProp == null || !fontSharedMatProp.CanWrite) return;

                // Cast for IL2CPP compatibility (proxy types need explicit casting)
                var castedMaterial = TypeHelper.Il2CppCast(fontMaterial, fontSharedMatProp.PropertyType);
                fontSharedMatProp.SetValue(component, castedMaterial, null);
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontReplace] SetFontSharedMaterial failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Searches through scanner caches to find the Unity Object.
        /// </summary>
        private static object FindComponentByInstanceId(int instanceId)
        {
            // Use the scanner's unified cache (covers all registered types, works on IL2CPP)
            return TranslatorScanner.FindCachedComponentById(instanceId);
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
        /// Remove a fallback font asset from a game font's fallback list.
        /// Called when switching to a different fallback font.
        /// </summary>
        private static void RemoveFallbackFromFont(string gameFontName, string oldFallbackName)
        {
            if (string.IsNullOrEmpty(gameFontName) || string.IsNullOrEmpty(oldFallbackName))
                return;

            // Find the game font object
            if (!_detectedTMPFontObjects.TryGetValue(gameFontName, out var gameFontObj))
                return;

            // Find the old fallback asset
            if (!_fallbackAssets.TryGetValue(oldFallbackName, out var oldFallbackAsset))
                return;

            try
            {
                var fallbackList = GetFallbackListReflection(gameFontObj);
                if (fallbackList == null) return;

                var removeMethod = fallbackList.GetType().GetMethod("Remove");
                if (removeMethod != null)
                {
                    bool removed = (bool)removeMethod.Invoke(fallbackList, new[] { oldFallbackAsset });
                    if (removed)
                        TranslatorCore.LogInfo($"[FontManager] Removed old fallback '{oldFallbackName}' from: {gameFontName}");
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] Failed to remove fallback from {gameFontName}: {ex.Message}");
            }
        }

        // Track which fonts already have our fallback added (by font name)
        private static readonly HashSet<string> _fallbackAppliedFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Flag: a new font was created, refresh needed on next scan cycle
        private static bool _pendingRefresh = false;

        /// <summary>
        /// Check and consume the pending refresh flag. Called from scan loop.
        /// </summary>
        public static bool ConsumePendingRefresh()
        {
            if (_pendingRefresh)
            {
                _pendingRefresh = false;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Ensure the configured fallback font is added to the original font's fallback list.
        /// Called from Harmony patches on every text set — must be fast (cached check).
        /// Does NOT replace the font on the component. TMP uses fallback fonts automatically
        /// for characters not found in the primary font.
        /// </summary>
        // Track original fonts per component instance ID (for restore on toggle)
        private static readonly Dictionary<int, object> _originalFontsPerComponent = new Dictionary<int, object>();
        private static readonly Dictionary<int, object> _replacedComponentRefs = new Dictionary<int, object>();

        /// <summary>
        /// Apply font replacement: SetFont to the replacement, add original as fallback.
        /// Stores original font for runtime restore.
        /// </summary>
        // Cache of components already font-replaced (avoids reflection GetFontName on every set_text)
        private static readonly HashSet<int> _fontReplacedComponentIds = new HashSet<int>();

        public static void ApplyFontReplacement(object component, object originalFontObj, string originalFontName)
        {
            if (component == null || string.IsNullOrEmpty(originalFontName)) return;

            // Fast check: already replaced this component (skip all reflection)
            int instanceId = TypeHelper.GetInstanceID(component);
            if (instanceId != -1 && _fontReplacedComponentIds.Contains(instanceId))
                return;

            var replacementFont = GetTMPReplacementFont(originalFontName);
            if (replacementFont == null) return;

            // Don't re-apply if already replaced with the right font
            string currentFontName = TypeHelper.GetFontName(component);
            string replacementName = (replacementFont is UnityEngine.Object rObj) ? rObj.name : null;
            if (currentFontName == replacementName)
            {
                if (instanceId != -1) _fontReplacedComponentIds.Add(instanceId);
                return;
            }

            // Store original font and component ref for this component (for restore)
            if (instanceId != -1 && !_originalFontsPerComponent.ContainsKey(instanceId))
            {
                _originalFontsPerComponent[instanceId] = originalFontObj;
                _replacedComponentRefs[instanceId] = component;
            }

            // SetFont to replacement
            TypeHelper.SetFont(component, replacementFont);

            // Mark as replaced (skip reflection on subsequent set_text calls)
            if (instanceId != -1)
                _fontReplacedComponentIds.Add(instanceId);

            // Set fontSharedMaterial to match the replacement font's material
            // Without this, TMP renders with the old font's atlas/shader → empty rectangles
            SetFontSharedMaterial(component, replacementFont);

            // Force mesh regeneration so the new font renders immediately
            TypeHelper.ForceMeshUpdate(component);

            // Add original game font as fallback on the replacement font
            // So missing chars in replacement → fall back to original
            if (originalFontObj != null && !_fallbackAppliedFonts.Contains(originalFontName + "_reverse"))
            {
                var fallbackList = GetFallbackListReflection(replacementFont);
                if (fallbackList != null)
                {
                    var addMethod = fallbackList.GetType().GetMethod("Add");
                    if (addMethod != null)
                    {
                        try
                        {
                            // Cast original font for IL2CPP compatibility
                            var castedOriginal = TypeHelper.Il2CppCast(originalFontObj,
                                TypeHelper.TMP_FontAssetType ?? originalFontObj.GetType());
                            addMethod.Invoke(fallbackList, new[] { castedOriginal });
                            _fallbackAppliedFonts.Add(originalFontName + "_reverse");
                        }
                        catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Restore original font on a component. Called when translation is disabled.
        /// </summary>
        public static void RestoreOriginalFont(object component)
        {
            if (component == null) return;
            int instanceId = TypeHelper.GetInstanceID(component);
            if (instanceId == -1) return;

            if (_originalFontsPerComponent.TryGetValue(instanceId, out var originalFont))
            {
                TypeHelper.SetFont(component, originalFont);
                SetFontSharedMaterial(component, originalFont);
                TypeHelper.ForceMeshUpdate(component);
                _originalFontsPerComponent.Remove(instanceId);
                _fontReplacedComponentIds.Remove(instanceId);
            }
        }

        /// <summary>
        /// Track original font for a component (used by AlternateTMP path).
        /// Only stores on first call per component (preserves true original).
        /// </summary>
        public static void TrackOriginalFont(int instanceId, object originalFont, object component)
        {
            if (instanceId == -1 || originalFont == null) return;
            if (!_originalFontsPerComponent.ContainsKey(instanceId))
            {
                _originalFontsPerComponent[instanceId] = originalFont;
                if (component != null)
                    _replacedComponentRefs[instanceId] = component;
            }
        }

        /// <summary>
        /// Get the original font name for a component (before replacement).
        /// Returns null if not tracked.
        /// </summary>
        public static string GetOriginalFontName(int instanceId)
        {
            if (_originalFontsPerComponent.TryGetValue(instanceId, out var fontObj))
            {
                return (fontObj is UnityEngine.Object uobj) ? uobj.name : null;
            }
            return null;
        }

        /// <summary>
        /// Get the SETTINGS font name for a component — always the ORIGINAL font name,
        /// never the replacement. This is THE method to use for all settings lookups
        /// (enable/disable, fallback, scale, font matching).
        ///
        /// Rule: the source font name is immutable for settings purposes.
        /// When a font is replaced, the replacement is visual only — all settings
        /// operations must reference the original game font.
        /// </summary>
        /// <param name="instanceId">Component instance ID</param>
        /// <param name="currentFontName">Current font name on the component (may be replacement)</param>
        /// <returns>Original font name if tracked, otherwise currentFontName</returns>
        public static string GetSettingsFontName(int instanceId, string currentFontName)
        {
            return GetOriginalFontName(instanceId) ?? currentFontName;
        }

        /// <summary>
        /// Check if a component's font matches a set of font names (for refresh/restore operations).
        /// Checks BOTH the current font name AND the tracked original font name.
        /// </summary>
        public static bool ComponentMatchesFont(int instanceId, string currentFontName, HashSet<string> fontNames)
        {
            return (!string.IsNullOrEmpty(currentFontName) && fontNames.Contains(currentFontName))
                || (!string.IsNullOrEmpty(GetOriginalFontName(instanceId)) && fontNames.Contains(GetOriginalFontName(instanceId)));
        }

        /// <summary>
        /// Check if translation is enabled for a component, using the correct (original) font name.
        /// </summary>
        public static bool IsTranslationEnabledForComponent(int instanceId, string currentFontName)
        {
            string settingsName = GetSettingsFontName(instanceId, currentFontName);
            return string.IsNullOrEmpty(settingsName) || IsTranslationEnabled(settingsName);
        }

        /// <summary>
        /// Check if fallback was successfully applied for a font.
        /// </summary>
        public static bool IsFallbackApplied(string fontName)
        {
            return _fallbackAppliedFonts.Contains(fontName);
        }

        public static void EnsureFallbackApplied(object fontObj, string fontName)
        {
            if (fontObj == null || string.IsNullOrEmpty(fontName)) return;

            // Store font object reference for font scanning
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

                // Refresh all text so components pick up the new fallback
                TranslatorScanner.ClearProcessedCache();
            }
        }

        /// <summary>
        /// Remove fallback from a font and clear the applied cache.
        /// Call when fallback settings change.
        /// </summary>
        // Track which GameObjects have been converted from UI.Text to TMP
        private static readonly HashSet<int> _convertedToTMP = new HashSet<int>();

        // Original fontNames saved before modification (for restore)
        private static readonly Dictionary<string, string[]> _originalFontNames = new Dictionary<string, string[]>();

        /// <summary>
        /// Check if a font has a fallback configured in settings.
        /// </summary>
        /// <summary>
        /// Force all UI.Text components using this font to re-render.
        /// Sets font to null then back to force Unity to drop cached glyphs.
        /// </summary>
        private static void ForceRefreshUITextFont(Font targetFont, string fontName)
        {
            try
            {
                // All access via reflection to avoid IL2CPP JIT crashes on stripped methods
                var textType = typeof(UnityEngine.UI.Text);
                var fontProp = textType.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                var setDirtyMethod = textType.GetMethod("SetVerticesDirty", BindingFlags.Public | BindingFlags.Instance);
                var setLayoutDirty = textType.GetMethod("SetLayoutDirty", BindingFlags.Public | BindingFlags.Instance);

                if (fontProp == null) return;

                var allTexts = TypeHelper.FindAllObjectsOfType(textType);
                int refreshed = 0;
                foreach (var textObj in allTexts)
                {
                    if (textObj == null) continue;
                    var currentFont = fontProp.GetValue(textObj, null);
                    if (currentFont == null) continue;

                    string currentName = (currentFont is UnityEngine.Object co) ? co.name : null;
                    if (currentFont != targetFont && currentName != fontName) continue;

                    // Bump fontSize by 1 to force Unity to re-rasterize with new fontNames.
                    // ApplyFontScale will correct it back on the next text processing cycle.
                    float origSize = TypeHelper.GetFontSize(textObj);
                    if (origSize > 0)
                    {
                        TranslatorPatches.BypassFontSizePrefix = true;
                        TypeHelper.SetFontSize(textObj, origSize + 1);
                        TranslatorPatches.BypassFontSizePrefix = false;
                    }

                    setDirtyMethod?.Invoke(textObj, null);
                    setLayoutDirty?.Invoke(textObj, null);
                    refreshed++;
                }

                // Clear font size cache so ApplyFontScale re-reads and corrects sizes
                TranslatorPatches.ClearFontSizeCache();
                TranslatorCore.LogInfo($"[FontManager] Force-refreshed {refreshed} UI.Text components for '{fontName}'");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[FontManager] ForceRefreshUITextFont failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore original fontNames on a game font (undo fontNames modification).
        /// </summary>
        public static void RestoreOriginalFontNames(string fontName)
        {
            string[] originalNames;
            if (!_originalFontNames.TryGetValue(fontName, out originalNames))
                return;

            // Find the font object
            Font gameFont = null;
            if (_gameUnityFonts.TryGetValue(fontName, out gameFont) || true)
            {
                if (gameFont == null)
                {
                    var allFonts = TypeHelper.FindAllObjectsOfType(typeof(Font));
                    foreach (var f in allFonts)
                    {
                        if (f != null && f.name == fontName)
                        {
                            gameFont = f as Font;
                            break;
                        }
                    }
                }

                if (gameFont != null)
                {
                    UniverseLib.Runtime.TextureHelper.SetFontNames(gameFont, originalNames);
                    ForceRefreshUITextFont(gameFont, fontName);
                    TranslatorCore.LogInfo($"[FontManager] Restored fontNames for '{fontName}': [{string.Join(", ", originalNames)}]");
                }
            }

            _originalFontNames.Remove(fontName);
        }

        public static bool IsCreatedFallbackFont(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return false;
            return _createdFallbackFontNames.Contains(fontName);
        }

        public static bool HasFallbackConfigured(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return false;
            if (!TranslatorCore.FontSettingsMap.TryGetValue(fontName, out var settings)) return false;
            return !string.IsNullOrEmpty(settings.fallback);
        }

        /// <summary>
        /// Convert a UI.Text component to TextMeshProUGUI with a custom TMP font.
        /// Used on IL2CPP where creating a Unity Font with CharacterInfo is impossible.
        /// Disables the original UI.Text and adds a TMP component with the rasterized font.
        /// </summary>
        public static void ConvertUITextToTMP(Component uiTextComponent, string originalFontName, string currentText)
        {
            if (uiTextComponent == null) return;

            var go = uiTextComponent.gameObject;
            int goId = go.GetInstanceID();

            // Don't convert twice
            if (_convertedToTMP.Contains(goId)) return;

            TranslatorCore.LogInfo($"[FontManager] ConvertUITextToTMP called: go='{go.name}', font='{originalFontName}'");

            // Check if a fallback is configured
            if (!TranslatorCore.FontSettingsMap.TryGetValue(originalFontName, out var settings))
                return;
            if (string.IsNullOrEmpty(settings.fallback))
                return;

            // Get the TMP font asset (via the existing TMP pipeline — works on IL2CPP)
            string cleanFallback = settings.fallback;
            if (cleanFallback.StartsWith("[Custom] "))
                cleanFallback = cleanFallback.Substring(9);

            object tmpFontAsset = null;

            // Try custom font first
            if (IsCustomFont(cleanFallback))
                tmpFontAsset = CustomFontLoader.LoadCustomFont(cleanFallback);

            // Try system font via TTF rasterizer
            if (tmpFontAsset == null)
                tmpFontAsset = CustomFontLoader.LoadSystemTtfFont(cleanFallback);

            if (tmpFontAsset == null)
            {
                // Try CreateFallbackAsset (covers all paths including game fonts)
                if (!_fallbackAssets.TryGetValue(settings.fallback, out tmpFontAsset))
                {
                    tmpFontAsset = CreateFallbackAsset(settings.fallback);
                    if (tmpFontAsset != null)
                        _fallbackAssets[settings.fallback] = tmpFontAsset;
                }
            }

            if (tmpFontAsset == null) return;

            try
            {
                // Get text properties from the UI.Text component before disabling
                var textComp = uiTextComponent;
                var textType = textComp.GetType();

                string text = currentText;
                Color color = Color.white;
                int fontSize = 14;
                int alignment = 0; // TextAnchor

                try
                {
                    var colorProp = textType.GetProperty("color", BindingFlags.Public | BindingFlags.Instance);
                    if (colorProp != null) color = (Color)colorProp.GetValue(textComp, null);
                }
                catch { }

                try
                {
                    var sizeProp = textType.GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance);
                    if (sizeProp != null) fontSize = (int)sizeProp.GetValue(textComp, null);
                }
                catch { }

                try
                {
                    var alignProp = textType.GetProperty("alignment", BindingFlags.Public | BindingFlags.Instance);
                    if (alignProp != null) alignment = (int)alignProp.GetValue(textComp, null);
                }
                catch { }

                // Disable the UI.Text component
                try
                {
                    var enabledProp = textType.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
                    if (enabledProp != null)
                        enabledProp.SetValue(textComp, false, null);
                }
                catch { }

                // Add TextMeshProUGUI component to the same GameObject
                var tmpTextType = TypeHelper.TMP_TextType;
                if (tmpTextType == null)
                {
                    TranslatorCore.LogWarning("[FontManager] TMP_Text type not found, cannot convert UI.Text to TMP");
                    return;
                }

                // Find TextMeshProUGUI type (subclass of TMP_Text for uGUI)
                Type tmproUGUIType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    tmproUGUIType = asm.GetType("TMPro.TextMeshProUGUI")
                        ?? asm.GetType("Il2CppTMPro.TextMeshProUGUI");
                    if (tmproUGUIType != null) break;
                }

                if (tmproUGUIType == null)
                {
                    TranslatorCore.LogWarning("[FontManager] TextMeshProUGUI type not found");
                    return;
                }

                // Clone an existing TMP GameObject from the scene and reconfigure it.
                // AddComponent<TextMeshProUGUI>() returns null on IL2CPP.
                object tmpComponent = null;

                try
                {
                    // Find any existing TextMeshProUGUI in the scene
                    var existingTmpComponents = TypeHelper.FindAllObjectsOfType(tmproUGUIType);
                    UnityEngine.Object templateObj = null;
                    foreach (var existing in existingTmpComponents)
                    {
                        if (existing == null) continue;
                        // Skip our own UI components
                        if (existing is Component ec && TranslatorCore.ShouldSkipTranslation(ec)) continue;
                        templateObj = existing;
                        break;
                    }

                    if (templateObj == null)
                    {
                        if (!_convertedToTMP.Contains(goId))
                        {
                            TranslatorCore.LogWarning("[FontManager] No existing TMP component found in scene to clone");
                            _convertedToTMP.Add(goId);
                        }
                        return;
                    }

                    // Clone the template's GameObject
                    var templateGO = (templateObj as Component)?.gameObject;
                    if (templateGO == null)
                    {
                        _convertedToTMP.Add(goId);
                        return;
                    }

                    var clonedGO = UnityEngine.Object.Instantiate(templateGO, go.transform);
                    clonedGO.name = "TMP_FontReplacement";

                    // Copy RectTransform to fill parent
                    var clonedRect = clonedGO.GetComponent<RectTransform>();
                    if (clonedRect != null)
                    {
                        clonedRect.anchorMin = Vector2.zero;
                        clonedRect.anchorMax = Vector2.one;
                        clonedRect.offsetMin = Vector2.zero;
                        clonedRect.offsetMax = Vector2.zero;
                    }

                    // Get the TMP component from the clone (via reflection — GetComponent(Type) is stripped on IL2CPP)
                    foreach (var m in typeof(GameObject).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "GetComponent") continue;
                        if (!m.IsGenericMethodDefinition) continue;
                        if (m.GetGenericArguments().Length != 1) continue;
                        if (m.GetParameters().Length != 0) continue;
                        tmpComponent = m.MakeGenericMethod(tmproUGUIType).Invoke(clonedGO, null);
                        break;
                    }
                    if (tmpComponent == null)
                    {
                        UnityEngine.Object.Destroy(clonedGO);
                        _convertedToTMP.Add(goId);
                        TranslatorCore.LogWarning("[FontManager] Cloned GO has no TMP component");
                        return;
                    }

                    TranslatorCore.LogInfo($"[FontManager] Cloned TMP component from '{templateGO.name}'");
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogWarning($"[FontManager] Clone TMP failed: {ex.Message}");
                    _convertedToTMP.Add(goId);
                    return;
                }

                // Set font
                TypeHelper.SetFont(tmpComponent, tmpFontAsset);
                SetFontSharedMaterial(tmpComponent, tmpFontAsset);

                // Set text
                var tmpCompType = tmpComponent.GetType();
                try
                {
                    var textProp = tmpCompType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    if (textProp != null) textProp.SetValue(tmpComponent, text, null);
                }
                catch { }

                // Set color
                try
                {
                    var colorProp = tmpCompType.GetProperty("color", BindingFlags.Public | BindingFlags.Instance);
                    if (colorProp != null) colorProp.SetValue(tmpComponent, color, null);
                }
                catch { }

                // Set font size
                try
                {
                    var sizeProp = tmpCompType.GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance);
                    if (sizeProp != null) sizeProp.SetValue(tmpComponent, (float)fontSize, null);
                }
                catch { }

                _convertedToTMP.Add(goId);
                _createdFallbackFontNames.Add(cleanFallback);

                TranslatorCore.LogInfo($"[FontManager] Converted UI.Text to TMP on '{go.name}': font='{cleanFallback}', size={fontSize}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[FontManager] ConvertUITextToTMP error: {ex.Message}");
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

                // IL2CPP fallback: modify the ORIGINAL font's fontNames to point to the system font
                // This avoids clone atlas sharing issues — Unity re-rasterizes using the new font
                Font originalGameFont = null;
                if (replacementFont == null)
                {
                    // Try to find the original game font by name
                    _gameUnityFonts.TryGetValue(originalFontName, out originalGameFont);

                    // Fallback: search all loaded fonts
                    if (originalGameFont == null)
                    {
                        var allFonts = TypeHelper.FindAllObjectsOfType(typeof(Font));
                        foreach (var f in allFonts)
                        {
                            if (f != null && f.name == originalFontName)
                            {
                                originalGameFont = f as Font;
                                break;
                            }
                        }
                    }
                }
                if (replacementFont == null && originalGameFont != null)
                {
                    string cleanFallback = settings.fallback;
                    if (cleanFallback.StartsWith("[Custom] "))
                        cleanFallback = cleanFallback.Substring(9);

                    // Get real font family name from TTF
                    string realFontName = cleanFallback;
                    string ttfPath = CustomFontLoader.FindSystemTtfPath(cleanFallback);
                    if (ttfPath != null)
                    {
                        try
                        {
                            var ttfBytes = System.IO.File.ReadAllBytes(ttfPath);
                            var parser = new Rasterizer.TtfParser(ttfBytes);
                            if (!string.IsNullOrEmpty(parser.Metrics.FontName))
                                realFontName = parser.Metrics.FontName;
                        }
                        catch { }
                    }

                    // Save original fontNames BEFORE modifying (only first time)
                    if (!_originalFontNames.ContainsKey(originalFontName))
                    {
                        try
                        {
                            var fnProp = typeof(Font).GetProperty("fontNames", BindingFlags.Public | BindingFlags.Instance);
                            if (fnProp != null)
                            {
                                var origNamesObj = fnProp.GetValue(originalGameFont, null);
                                if (origNamesObj != null)
                                {
                                    var lenProp = origNamesObj.GetType().GetProperty("Length") ?? origNamesObj.GetType().GetProperty("Count");
                                    int len = lenProp != null ? (int)lenProp.GetValue(origNamesObj, null) : 0;
                                    var saved = new string[len];
                                    var indexer = origNamesObj.GetType().GetProperty("Item");
                                    for (int fn = 0; fn < len; fn++)
                                    {
                                        if (indexer != null)
                                            saved[fn] = indexer.GetValue(origNamesObj, new object[] { fn })?.ToString();
                                        else if (origNamesObj is string[] strArr)
                                            saved[fn] = strArr[fn];
                                    }
                                    _originalFontNames[originalFontName] = saved;
                                    TranslatorCore.LogInfo($"[FontManager] Saved original fontNames for '{originalFontName}': [{string.Join(", ", saved)}]");
                                }
                            }
                        }
                        catch (Exception saveEx)
                        {
                            TranslatorCore.LogWarning($"[FontManager] Failed to save fontNames: {saveEx.Message}");
                        }
                    }

                    // Clone the original font — inherits dynamic=True needed for fontNames rasterization
                    var cloned = UnityEngine.Object.Instantiate(originalGameFont);
                    var clonedFont = TypeHelper.Il2CppCast(cloned, typeof(Font)) as Font;
                    if (clonedFont == null) clonedFont = cloned as Font;

                    if (clonedFont != null)
                    {
                        clonedFont.name = cleanFallback;

                        bool set = UniverseLib.Runtime.TextureHelper.SetFontNames(
                            clonedFont, new string[] { realFontName, cleanFallback });
                        TranslatorCore.LogInfo($"[FontManager] Cloned '{originalFontName}' -> '{cleanFallback}', fontNames set={set}");

                        if (set)
                        {
                            replacementFont = clonedFont;
                            SubscribeTextureRebuilt(originalFontName, settings.fallback);
                        }
                    }
                }

                if (replacementFont != null)
                {
                    _unityFallbackFonts[settings.fallback] = replacementFont;
                    // Don't add to _createdFallbackFontNames if it's the original game font
                    // (we modified its fontNames, not created a new font)
                    if (originalGameFont == null || replacementFont != originalGameFont)
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
                    if (replacementFont is UnityEngine.Object uobj && !_gameTMPFonts.ContainsKey(uobj.name))
                        _createdFallbackFontNames.Add(uobj.name);

                    // Font just created — schedule refresh on next scan cycle
                    _pendingRefresh = true;
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
            // Strip [Custom] prefix if present
            string cleanName = systemFontName;
            if (systemFontName.StartsWith("[Custom] "))
                cleanName = systemFontName.Substring(9);

            // Try game fonts first — already loaded, works on IL2CPP without CreateDynamicFontFromOSFont
            if (!_gameFontsScanned) ScanGameFonts();
            if (_gameUnityFonts.TryGetValue(cleanName, out var gameFont))
            {
                TranslatorCore.LogInfo($"[FontManager] Using game Unity font: {cleanName}");
                return gameFont;
            }

            // Partial match (font name contains search or vice versa)
            foreach (var kvp in _gameUnityFonts)
            {
                if (kvp.Key.IndexOf(cleanName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cleanName.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TranslatorCore.LogInfo($"[FontManager] Using game Unity font (partial match): {kvp.Key} for {cleanName}");
                    return kvp.Value;
                }
            }

            // On Mono, try CreateDynamicFontFromOSFont via reflection
            if (_dynamicFontCreationAvailable)
            {
                try
                {
                    var method = typeof(Font).GetMethod("CreateDynamicFontFromOSFont",
                        BindingFlags.Public | BindingFlags.Static,
                        null, new Type[] { typeof(string), typeof(int) }, null);
                    if (method != null)
                    {
                        var font = method.Invoke(null, new object[] { cleanName, 32 }) as Font;
                        if (font != null)
                        {
                            TranslatorCore.LogInfo($"[FontManager] Created dynamic Unity font: {cleanName}");
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
                        TranslatorCore.LogWarning($"[FontManager] CreateDynamicFontFromOSFont unavailable on this runtime");
                    }
                }
            }

            // On IL2CPP, CreateDynamicFontFromOSFont is stripped.
            // Return null here — GetUnityReplacementFont will modify the original font's fontNames instead.
            return null;
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
                        // Track the created asset name to exclude from game font detection
                        if (customAsset is UnityEngine.Object uobj2)
                            _createdFallbackFontNames.Add(uobj2.name);
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

                // Last resort: try TTF rasterizer (finds the .ttf on disk and rasterizes to SDF)
                var rasterizedAsset = CustomFontLoader.LoadSystemTtfFont(cleanName);
                if (rasterizedAsset != null)
                {
                    TranslatorCore.LogInfo($"[FontManager] Created TMP_FontAsset via TTF rasterizer: {cleanName}");
                    return rasterizedAsset;
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

            // ICall for CreateDynamicFontFromOSFont was tested but crashes the game
            // (the native function calls back into stripped managed code → crash loop)

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
                                font.name = fontName;
                                TranslatorCore.LogInfo($"[FontManager] Created Font via Internal_CreateFontFromPath: {fontPath}");

                                // Try to make the font dynamic by setting internal properties
                                // and calling RequestCharactersInTexture
                                try
                                {
                                    // Set m_FontSize (internal field) to enable dynamic rasterization
                                    var fontSizeProp = fontType.GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance);
                                    if (fontSizeProp != null && fontSizeProp.CanWrite)
                                        fontSizeProp.SetValue(font, 32, null);

                                    // Dump all writable properties to understand what we can set
                                    var allProps = fontType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                    var propInfo = new List<string>();
                                    foreach (var p in allProps)
                                    {
                                        try
                                        {
                                            var val = p.GetValue(font, null);
                                            propInfo.Add($"{p.Name}={val}({(p.CanWrite ? "rw" : "ro")})");
                                        }
                                        catch { propInfo.Add($"{p.Name}=(err)"); }
                                    }
                                    TranslatorCore.LogInfo($"[FontManager] Font props: {string.Join(", ", propInfo)}");

                                    // Try RequestCharactersInTexture
                                    string testChars = "ABCabc";
                                    var requestMethod = fontType.GetMethod("RequestCharactersInTexture",
                                        BindingFlags.Public | BindingFlags.Instance);
                                    if (requestMethod != null)
                                    {
                                        var reqParams = requestMethod.GetParameters();
                                        TranslatorCore.LogInfo($"[FontManager] RequestCharactersInTexture params: {string.Join(", ", reqParams.Select(p => p.ParameterType.Name))}");

                                        if (reqParams.Length == 3)
                                            requestMethod.Invoke(font, new object[] { testChars, 32, FontStyle.Normal });
                                        else if (reqParams.Length == 2)
                                            requestMethod.Invoke(font, new object[] { testChars, 32 });
                                        else if (reqParams.Length == 1)
                                            requestMethod.Invoke(font, new object[] { testChars });

                                        TranslatorCore.LogInfo($"[FontManager] After request: dynamic={font.dynamic}");

                                        // Check GetCharacterInfo for 'A'
                                        var getCharMethod = fontType.GetMethod("GetCharacterInfo",
                                            BindingFlags.Public | BindingFlags.Instance);
                                        if (getCharMethod != null)
                                        {
                                            TranslatorCore.LogInfo($"[FontManager] GetCharacterInfo params: {string.Join(", ", getCharMethod.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}");
                                        }
                                    }
                                }
                                catch (Exception reqEx)
                                {
                                    TranslatorCore.LogWarning($"[FontManager] Font setup failed: {reqEx.InnerException?.Message ?? reqEx.Message}");
                                }

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
                                if (result is UnityEngine.Object uobj && string.IsNullOrEmpty(uobj.name))
                                    uobj.name = font.name + " SDF";
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
                                if (result is UnityEngine.Object uobj && string.IsNullOrEmpty(uobj.name))
                                    uobj.name = font.name + " SDF";
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
                        // Ensure the asset has a proper name
                        if (result is UnityEngine.Object uobj && string.IsNullOrEmpty(uobj.name))
                            uobj.name = font.name + " SDF";
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
                        {
                            if (result is UnityEngine.Object uobj && string.IsNullOrEmpty(uobj.name))
                                uobj.name = font.name + " SDF";
                            return result;
                        }
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
                            {
                                if (result is UnityEngine.Object uobj && string.IsNullOrEmpty(uobj.name))
                                    uobj.name = font.name + " SDF";
                                return result;
                            }
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
                            {
                                if (result is UnityEngine.Object uobj && string.IsNullOrEmpty(uobj.name))
                                    uobj.name = font.name + " SDF";
                                return result;
                            }
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
        /// Pre-load fallback fonts that are already configured in settings.
        /// Call after LoadCache so FontSettingsMap is populated.
        /// This avoids the first-use delay where the font is created lazily
        /// during a Harmony prefix, causing the first text to render wrong.
        /// </summary>
        public static void PreloadConfiguredFallbacks()
        {
            int preloaded = 0;
            foreach (var kvp in TranslatorCore.FontSettingsMap)
            {
                if (!kvp.Value.enabled) continue;
                if (string.IsNullOrEmpty(kvp.Value.fallback)) continue;

                // Pre-create the fallback asset so it's cached for first use
                if (!_fallbackAssets.ContainsKey(kvp.Value.fallback))
                {
                    var asset = CreateFallbackAsset(kvp.Value.fallback);
                    if (asset != null)
                    {
                        _fallbackAssets[kvp.Value.fallback] = asset;
                        if (asset is UnityEngine.Object uobj && !_gameTMPFonts.ContainsKey(uobj.name))
                            _createdFallbackFontNames.Add(uobj.name);
                        preloaded++;
                    }
                }
            }

            if (preloaded > 0)
                TranslatorCore.LogInfo($"[FontManager] Pre-loaded {preloaded} fallback font(s)");
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

            // Also scan Unity Font objects (for UI.Text fallback on IL2CPP)
            // Use TypeHelper.FindAllObjectsOfType to handle IL2CPP (direct Resources.FindObjectsOfTypeAll
            // is stripped on some IL2CPP runtimes)
            try
            {
                var allUnityFonts = TypeHelper.FindAllObjectsOfType(typeof(Font));
                if (allUnityFonts != null)
                {
                    foreach (var fontObj in allUnityFonts)
                    {
                        var font = fontObj as Font;
                        if (font == null || string.IsNullOrEmpty(font.name)) continue;
                        if (_createdFallbackFontNames.Contains(font.name)) continue;
                        if (!_gameUnityFonts.ContainsKey(font.name))
                            _gameUnityFonts[font.name] = font;
                    }
                    if (_gameUnityFonts.Count > 0)
                        TranslatorCore.LogInfo($"[FontManager] Found {_gameUnityFonts.Count} game Unity fonts: {string.Join(", ", _gameUnityFonts.Keys)}");
                }
            }
            catch { }
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
        /// Get names of all Unity Font objects loaded in the game (for UI.Text fallback).
        /// </summary>
        public static string[] GetGameUnityFontNames()
        {
            if (!_gameFontsScanned) ScanGameFonts();

            var names = new string[_gameUnityFonts.Count];
            _gameUnityFonts.Keys.CopyTo(names, 0);
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
            _fontReplacedComponentIds.Clear();
            _originalFontsPerComponent.Clear();
            _replacedComponentRefs.Clear();
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

            if (fontName.StartsWith("[Custom] "))
                return true;

            var customFonts = CustomFontLoader.CustomFonts;
            return customFonts.ContainsKey(fontName);
        }

        /// <summary>
        /// Gets or loads a custom font asset by name.
        /// Handles both custom fonts and system TTF fonts.
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
