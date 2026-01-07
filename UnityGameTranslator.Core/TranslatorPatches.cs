using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Shared Harmony patch methods and application logic.
    /// Works with any mod loader that provides a Harmony instance.
    /// </summary>
    public static class TranslatorPatches
    {
        // Keywords to identify localization string types (case-insensitive)
        private static readonly string[] LocalizationPrefixes = { "locali", "l10n", "i18n", "translat" };
        private static readonly string[] LocalizationSuffixes = { "string", "text", "entry", "value" };

        // Cache for original font sizes (instance ID -> original fontSize)
        // Used to apply scale without cumulative errors
        private static readonly Dictionary<int, float> _originalFontSizes = new Dictionary<int, float>();

        // Types to exclude (known non-text types)
        private static readonly HashSet<string> ExcludedTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LocalizationSettings",
            "LocalizationManager",
            "LocalizationService",
            "LocalizationDatabase",
            "LocalizationTable",
            "LocalizationAsset",
            "StringLocalizer",
            "TranslationManager",
            "TranslationService",
            "TranslationDatabase"
        };
        /// <summary>
        /// Apply all Harmony patches using the provided patcher.
        /// </summary>
        /// <param name="patcher">Function that takes (MethodInfo target, MethodInfo prefix, MethodInfo postfix) and applies the patch</param>
        /// <returns>Number of patches applied</returns>
        public static int ApplyAll(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int patchCount = 0;

            try
            {
                // TMP_Text.text setter
                var tmpTextType = typeof(TMP_Text);
                var textProp = tmpTextType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (textProp?.SetMethod != null)
                {
                    var prefix = typeof(TranslatorPatches).GetMethod(nameof(TMPText_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);
                    patcher(textProp.SetMethod, prefix, null);
                    patchCount++;
                }

                // TMP_Text.SetText(string) methods
                var setTextMethods = tmpTextType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                foreach (var method in setTextMethods)
                {
                    if (method.Name == "SetText" && method.GetParameters().Length > 0
                        && method.GetParameters()[0].ParameterType == typeof(string))
                    {
                        var prefix = typeof(TranslatorPatches).GetMethod(nameof(TMPText_SetTextMethod_Prefix), BindingFlags.Static | BindingFlags.Public);
                        patcher(method, prefix, null);
                        patchCount++;
                    }
                }

                // UI.Text.text setter
                var uiTextType = typeof(Text);
                var uiTextProp = uiTextType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (uiTextProp?.SetMethod != null)
                {
                    var prefix = typeof(TranslatorPatches).GetMethod(nameof(UIText_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);
                    patcher(uiTextProp.SetMethod, prefix, null);
                    patchCount++;
                }

                // TextMesh.text setter (legacy 3D text)
                var textMeshType = typeof(TextMesh);
                var textMeshProp = textMeshType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (textMeshProp?.SetMethod != null)
                {
                    var prefix = typeof(TranslatorPatches).GetMethod(nameof(TextMesh_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);
                    patcher(textMeshProp.SetMethod, prefix, null);
                    patchCount++;
                }

                // Unity.Localization.StringTableEntry (optional)
                Type stringTableEntryType = FindStringTableEntryType();
                if (stringTableEntryType != null)
                {
                    patchCount += PatchStringTableEntry(stringTableEntryType, patcher);
                }

                // tk2dTextMesh (2D Toolkit - used by many 2D games)
                Type tk2dTextMeshType = FindTk2dTextMeshType();
                if (tk2dTextMeshType != null)
                {
                    patchCount += PatchTk2dTextMesh(tk2dTextMeshType, patcher);
                }

                // Generic localization system detection
                // Finds custom localization types like LocalisedString, LocalizedText, I18nString, etc.
                var customLocalizationTypes = FindCustomLocalizationTypes();
                foreach (var locType in customLocalizationTypes)
                {
                    patchCount += PatchCustomLocalizationType(locType, patcher);
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"Failed to apply patches: {e.Message}");
            }

            return patchCount;
        }

        private static Type FindStringTableEntryType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType("UnityEngine.Localization.Tables.StringTableEntry");
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static Type FindTk2dTextMeshType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Try common tk2d namespaces
                    var type = asm.GetType("tk2dTextMesh");
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static int PatchTk2dTextMesh(Type tk2dTextMeshType, Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int count = 0;
            var prefix = typeof(TranslatorPatches).GetMethod(nameof(Tk2dTextMesh_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);
            var getterPostfix = typeof(TranslatorPatches).GetMethod(nameof(Tk2dTextMesh_GetText_Postfix), BindingFlags.Static | BindingFlags.Public);

            var textProp = tk2dTextMeshType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);

            // Patch the text property setter
            if (textProp?.SetMethod != null)
            {
                try
                {
                    patcher(textProp.SetMethod, prefix, null);
                    count++;
                }
                catch { }
            }

            // Patch the text property getter (for pre-loaded/deserialized text)
            if (textProp?.GetMethod != null)
            {
                try
                {
                    patcher(textProp.GetMethod, null, getterPostfix);
                    count++;
                }
                catch { }
            }

            // Also patch FormattedText getter (used for display)
            var formattedTextProp = tk2dTextMeshType.GetProperty("FormattedText", BindingFlags.Public | BindingFlags.Instance);
            if (formattedTextProp?.GetMethod != null)
            {
                try
                {
                    patcher(formattedTextProp.GetMethod, null, getterPostfix);
                    count++;
                }
                catch { }
            }

            if (count > 0)
            {
                TranslatorCore.LogInfo($"[Patches] Patched {count} tk2dTextMesh methods");
            }

            return count;
        }

        /// <summary>
        /// Finds all custom localization types in loaded assemblies.
        /// Searches for types with names matching patterns like LocalisedString, LocalizedText, I18nString, etc.
        /// </summary>
        private static List<Type> FindCustomLocalizationTypes()
        {
            var results = new List<Type>();
            var foundTypeNames = new HashSet<string>(); // Avoid duplicates

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Skip system/Unity assemblies for performance
                    string asmName = asm.GetName().Name;
                    if (asmName.StartsWith("System") || asmName.StartsWith("mscorlib") ||
                        asmName.StartsWith("Unity.") || asmName.StartsWith("UnityEngine."))
                        continue;

                    foreach (var type in asm.GetTypes())
                    {
                        try
                        {
                            if (IsLocalizationStringType(type) && !foundTypeNames.Contains(type.FullName))
                            {
                                results.Add(type);
                                foundTypeNames.Add(type.FullName);
                            }
                        }
                        catch { } // Skip types that fail to load
                    }
                }
                catch { } // Skip assemblies that fail to enumerate
            }

            return results;
        }

        /// <summary>
        /// Checks if a type matches the pattern for a localization string type.
        /// </summary>
        private static bool IsLocalizationStringType(Type type)
        {
            if (type == null || type.IsInterface || type.IsAbstract)
                return false;

            string typeName = type.Name;

            // Check if excluded
            if (ExcludedTypeNames.Contains(typeName))
                return false;

            // Check if name matches pattern: (locali|l10n|i18n|translat) + (string|text|entry|value)
            string lowerName = typeName.ToLowerInvariant();

            bool hasPrefix = false;
            foreach (var prefix in LocalizationPrefixes)
            {
                if (lowerName.Contains(prefix))
                {
                    hasPrefix = true;
                    break;
                }
            }

            if (!hasPrefix) return false;

            bool hasSuffix = false;
            foreach (var suffix in LocalizationSuffixes)
            {
                if (lowerName.Contains(suffix))
                {
                    hasSuffix = true;
                    break;
                }
            }

            if (!hasSuffix) return false;

            // Must have ToString() returning string OR op_Implicit to string
            bool hasStringMethod = false;

            // Check for ToString() override (not just inherited from object)
            var toStringMethod = type.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (toStringMethod != null && toStringMethod.ReturnType == typeof(string))
                hasStringMethod = true;

            // Check for op_Implicit to string
            var implicitMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
            foreach (var method in implicitMethods)
            {
                if (method.Name == "op_Implicit" && method.ReturnType == typeof(string))
                {
                    hasStringMethod = true;
                    break;
                }
            }

            return hasStringMethod;
        }

        /// <summary>
        /// Patches a custom localization type's ToString() and op_Implicit methods.
        /// </summary>
        private static int PatchCustomLocalizationType(Type locType, Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int count = 0;
            var postfix = typeof(TranslatorPatches).GetMethod(nameof(CustomLocalization_ToString_Postfix), BindingFlags.Static | BindingFlags.Public);

            // Patch ToString() methods (declared in this type, not inherited)
            var toStringMethods = locType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in toStringMethods)
            {
                if (method.Name == "ToString" && method.ReturnType == typeof(string))
                {
                    try
                    {
                        patcher(method, null, postfix);
                        count++;
                    }
                    catch { }
                }
            }

            // Patch op_Implicit (string conversion)
            var implicitMethods = locType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            foreach (var method in implicitMethods)
            {
                if (method.Name == "op_Implicit" && method.ReturnType == typeof(string))
                {
                    try
                    {
                        patcher(method, null, postfix);
                        count++;
                    }
                    catch { }
                }
            }

            if (count > 0)
            {
                TranslatorCore.LogInfo($"[Patches] Found custom localization: {locType.FullName} ({count} methods patched)");
            }

            return count;
        }

        private static int PatchStringTableEntry(Type stringTableEntryType, Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int count = 0;
            var postfix = typeof(TranslatorPatches).GetMethod(nameof(StringTableEntry_Postfix), BindingFlags.Static | BindingFlags.Public);

            var allMethods = stringTableEntryType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            foreach (var method in allMethods)
            {
                if (method.Name == "GetLocalizedString" && method.ReturnType == typeof(string))
                {
                    try
                    {
                        patcher(method, null, postfix);
                        count++;
                    }
                    catch { }
                }
            }

            var valueProp = stringTableEntryType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (valueProp?.GetMethod != null)
            {
                try
                {
                    patcher(valueProp.GetMethod, null, postfix);
                    count++;
                }
                catch { }
            }

            var localizedValueProp = stringTableEntryType.GetProperty("LocalizedValue", BindingFlags.Public | BindingFlags.Instance);
            if (localizedValueProp?.GetMethod != null)
            {
                try
                {
                    patcher(localizedValueProp.GetMethod, null, postfix);
                    count++;
                }
                catch { }
            }

            return count;
        }

        #region Patch Methods

        // Cache for InputField textComponent exclusion (avoids repeated GetComponentInParent calls)
        // Key: instanceId, Value: true if this is an InputField's textComponent (should be excluded)
        private static readonly System.Collections.Generic.Dictionary<int, bool> inputFieldTextCache =
            new System.Collections.Generic.Dictionary<int, bool>();

        /// <summary>
        /// Check if a Text component is the textComponent of an InputField (should not be translated).
        /// Caches the result for performance.
        /// </summary>
        private static bool IsInputFieldTextComponent(Text text)
        {
            int id = text.GetInstanceID();
            if (inputFieldTextCache.TryGetValue(id, out bool isInputFieldText))
                return isInputFieldText;

            var inputField = text.GetComponentInParent<InputField>();
            bool result = inputField != null && inputField.textComponent == text;
            inputFieldTextCache[id] = result;
            return result;
        }

        /// <summary>
        /// Check if a TMP_Text component is the textComponent of a TMP_InputField (should not be translated).
        /// Caches the result for performance.
        /// </summary>
        private static bool IsTMPInputFieldTextComponent(TMP_Text text)
        {
            int id = text.GetInstanceID();
            if (inputFieldTextCache.TryGetValue(id, out bool isInputFieldText))
                return isInputFieldText;

            var inputField = text.GetComponentInParent<TMPro.TMP_InputField>();
            bool result = inputField != null && inputField.textComponent == text;
            inputFieldTextCache[id] = result;
            return result;
        }

        /// <summary>
        /// Clear the InputField cache (call on scene change).
        /// </summary>
        public static void ClearCache()
        {
            inputFieldTextCache.Clear();
        }

        public static void StringTableEntry_Postfix(object __instance, ref string __result)
        {
            // Disabled: sync translation here causes issues when the game builds strings
            // using translated parts. Let TMP_Text/UI.Text patches handle translation instead.
            // if (__instance == null || string.IsNullOrEmpty(__result)) return;
            // try { __result = TranslatorCore.TranslateText(__result); } catch { }
        }

        /// <summary>
        /// Postfix for custom localization types' ToString() and op_Implicit.
        /// Translates the localized string result before it's used.
        /// Works with any localization system (TeamCherry, custom studios, etc.)
        /// </summary>
        public static void CustomLocalization_ToString_Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            try
            {
                // Translate the localized string (no component context available)
                __result = TranslatorCore.TranslateText(__result);
            }
            catch { }
        }

        /// <summary>
        /// Apply font scale to a TMP_Text component.
        /// Stores original size on first call and applies scale relative to it.
        /// </summary>
        private static void ApplyFontScale(TMP_Text instance, string fontName)
        {
            if (instance == null || string.IsNullOrEmpty(fontName)) return;

            float scale = FontManager.GetFontScale(fontName);
            if (Math.Abs(scale - 1.0f) < 0.001f) return; // No scale needed

            int instanceId = instance.GetInstanceID();
            float originalSize;

            if (!_originalFontSizes.TryGetValue(instanceId, out originalSize))
            {
                // First time seeing this instance, store its current size as original
                originalSize = instance.fontSize;
                _originalFontSizes[instanceId] = originalSize;
            }

            float scaledSize = originalSize * scale;
            if (Math.Abs(instance.fontSize - scaledSize) > 0.1f)
            {
                instance.fontSize = scaledSize;
            }
        }

        /// <summary>
        /// Apply font scale to a UI.Text component.
        /// Stores original size on first call and applies scale relative to it.
        /// </summary>
        private static void ApplyFontScale(Text instance, string fontName)
        {
            if (instance == null || string.IsNullOrEmpty(fontName)) return;

            float scale = FontManager.GetFontScale(fontName);
            if (Math.Abs(scale - 1.0f) < 0.001f) return; // No scale needed

            int instanceId = instance.GetInstanceID();
            float originalSize;

            if (!_originalFontSizes.TryGetValue(instanceId, out originalSize))
            {
                // First time seeing this instance, store its current size as original
                originalSize = instance.fontSize;
                _originalFontSizes[instanceId] = originalSize;
            }

            int scaledSize = Mathf.RoundToInt(originalSize * scale);
            if (instance.fontSize != scaledSize)
            {
                instance.fontSize = scaledSize;
            }
        }

        public static void TMPText_SetText_Prefix(TMP_Text __instance, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                // Skip if part of our own UI and should not be translated (uses hierarchy check)
                // Check this FIRST to avoid registering mod UI fonts
                if (TranslatorCore.ShouldSkipTranslation(__instance)) return;

                string fontName = null;

                // Register font for fallback management (non-Latin script support)
                if (__instance.font != null)
                {
                    fontName = __instance.font.name;
                    FontManager.RegisterFont(__instance.font);

                    // Skip translation if disabled for this font
                    if (!FontManager.IsTranslationEnabled(__instance.font))
                        return;

                    // Apply replacement font if configured
                    var replacementFont = FontManager.GetTMPReplacementFont(__instance.font);
                    if (replacementFont != null)
                    {
                        __instance.font = replacementFont;
                    }
                }

                // Don't translate InputField textComponent (user's typed text)
                if (IsTMPInputFieldTextComponent(__instance)) return;

                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(__instance);
                value = TranslatorCore.TranslateTextWithTracking(value, __instance, isOwnUI);

                // Apply font scale if configured for this font
                ApplyFontScale(__instance, fontName);
            }
            catch { }
        }

        public static void TMPText_SetTextMethod_Prefix(TMP_Text __instance, ref string __0)
        {
            if (string.IsNullOrEmpty(__0)) return;
            try
            {
                // Skip if part of our own UI and should not be translated (uses hierarchy check)
                // Check this FIRST to avoid registering mod UI fonts
                if (TranslatorCore.ShouldSkipTranslation(__instance)) return;

                string fontName = null;

                // Register font for fallback management (non-Latin script support)
                if (__instance.font != null)
                {
                    fontName = __instance.font.name;
                    FontManager.RegisterFont(__instance.font);

                    // Skip translation if disabled for this font
                    if (!FontManager.IsTranslationEnabled(__instance.font))
                        return;

                    // Apply replacement font if configured
                    var replacementFont = FontManager.GetTMPReplacementFont(__instance.font);
                    if (replacementFont != null)
                    {
                        __instance.font = replacementFont;
                    }
                }

                // Don't translate InputField textComponent (user's typed text)
                if (IsTMPInputFieldTextComponent(__instance)) return;

                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(__instance);
                __0 = TranslatorCore.TranslateTextWithTracking(__0, __instance, isOwnUI);

                // Apply font scale if configured for this font
                ApplyFontScale(__instance, fontName);
            }
            catch { }
        }

        public static void UIText_SetText_Prefix(Text __instance, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                // Skip if part of our own UI and should not be translated (uses hierarchy check)
                // Check this FIRST to avoid registering mod UI fonts
                if (TranslatorCore.ShouldSkipTranslation(__instance)) return;

                string fontName = null;

                // Register font for detection (Unity UI fonts)
                if (__instance.font != null)
                {
                    fontName = __instance.font.name;
                    FontManager.RegisterFont(__instance.font);

                    // Skip translation if disabled for this font
                    if (!FontManager.IsTranslationEnabled(__instance.font))
                        return;

                    // Apply replacement font if configured (for non-Latin script support)
                    var replacementFont = FontManager.GetUnityReplacementFont(__instance.font);
                    if (replacementFont != null)
                    {
                        __instance.font = replacementFont;
                    }
                }

                // Don't translate InputField textComponent (user's typed text)
                if (IsInputFieldTextComponent(__instance)) return;

                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(__instance);
                value = TranslatorCore.TranslateTextWithTracking(value, __instance, isOwnUI);

                // Apply font scale if configured for this font
                ApplyFontScale(__instance, fontName);
            }
            catch { }
        }

        public static void TextMesh_SetText_Prefix(TextMesh __instance, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                // Skip if part of our own UI (uses hierarchy check)
                // Check this FIRST to avoid registering mod UI fonts
                if (TranslatorCore.ShouldSkipTranslation(__instance)) return;

                // Register font for detection (legacy 3D text)
                if (__instance.font != null)
                {
                    FontManager.RegisterFont(__instance.font);

                    // Skip translation if disabled for this font
                    if (!FontManager.IsTranslationEnabled(__instance.font))
                        return;
                }

                // TextMesh is legacy 3D text, typically not used for UI input fields
                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(__instance);
                value = TranslatorCore.TranslateTextWithTracking(value, __instance, isOwnUI);
            }
            catch { }
        }

        /// <summary>
        /// Prefix for tk2dTextMesh.text setter (2D Toolkit).
        /// Uses object type since tk2dTextMesh is not available at compile time.
        /// </summary>
        public static void Tk2dTextMesh_SetText_Prefix(object __instance, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                // tk2dTextMesh inherits from MonoBehaviour, so cast to Component for hierarchy checks
                var component = __instance as Component;
                if (component == null) return;

                // Skip if part of our own UI (uses hierarchy check)
                if (TranslatorCore.ShouldSkipTranslation(component)) return;

                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(component);
                value = TranslatorCore.TranslateTextWithTracking(value, component, isOwnUI);
            }
            catch { }
        }

        /// <summary>
        /// Postfix for tk2dTextMesh.text and FormattedText getters.
        /// Translates pre-loaded/deserialized text when it's read.
        /// </summary>
        public static void Tk2dTextMesh_GetText_Postfix(object __instance, ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            try
            {
                var component = __instance as Component;
                if (component == null) return;

                // Skip if part of our own UI
                if (TranslatorCore.ShouldSkipTranslation(component)) return;

                // Translate and track
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(component);
                __result = TranslatorCore.TranslateTextWithTracking(__result, component, isOwnUI);
            }
            catch { }
        }

        #endregion
    }
}
