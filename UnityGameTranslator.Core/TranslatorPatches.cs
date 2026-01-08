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

                // Alternate TMP implementations (TMProOld, etc. - used by some games with bundled/older TMP)
                // These are in different namespaces than the standard TMPro.TMP_Text we patch above
                var alternateTMPTypes = FindAlternateTMPTypes();
                foreach (var altTmpType in alternateTMPTypes)
                {
                    patchCount += PatchAlternateTMPType(altTmpType, patcher);
                }

                // Localization bridge components (MonoBehaviours that link LocalisedString to text components)
                // These have font context, so font-based enable/disable works correctly
                var bridgeComponents = FindLocalizationBridgeComponents();
                foreach (var bridgeType in bridgeComponents)
                {
                    patchCount += PatchLocalizationBridge(bridgeType, patcher);
                }

                // Generic localization system detection (FALLBACK - disabled by default)
                // Finds custom localization types like LocalisedString, LocalizedText, I18nString, etc.
                // Only patches ToString/op_Implicit - no font context available
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

        /// <summary>
        /// Finds alternate TMP implementations in different namespaces (TMProOld, etc.).
        /// Some games bundle older versions of TextMeshPro with different namespaces.
        /// </summary>
        private static List<Type> FindAlternateTMPTypes()
        {
            var results = new List<Type>();
            var standardTmpType = typeof(TMP_Text);
            string standardTmpAssembly = standardTmpType.Assembly.GetName().Name;

            // Type names to search for
            string[] tmpTypeNames = { "TextMeshPro", "TextMeshProUGUI", "TMP_Text" };
            // Namespaces that indicate alternate implementations
            string[] altNamespaces = { "TMProOld", "TextMeshPro", "TMPro.Old" };

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string asmName = asm.GetName().Name;
                    // Skip system assemblies
                    if (asmName.StartsWith("System") || asmName.StartsWith("mscorlib"))
                        continue;

                    foreach (var type in asm.GetTypes())
                    {
                        try
                        {
                            // Skip if it's the standard TMPro type we already patch
                            if (type == standardTmpType || type.IsSubclassOf(standardTmpType))
                                continue;

                            string typeName = type.Name;
                            string typeNamespace = type.Namespace ?? "";

                            // Check if this is a TMP-like type
                            bool isTmpType = false;
                            foreach (var name in tmpTypeNames)
                            {
                                if (typeName == name || typeName.EndsWith(name))
                                {
                                    isTmpType = true;
                                    break;
                                }
                            }

                            if (!isTmpType) continue;

                            // Check if it's in an alternate namespace (not standard TMPro)
                            bool isAltNamespace = typeNamespace != "TMPro";
                            if (!isAltNamespace)
                            {
                                foreach (var ns in altNamespaces)
                                {
                                    if (typeNamespace.Contains(ns))
                                    {
                                        isAltNamespace = true;
                                        break;
                                    }
                                }
                            }

                            if (!isAltNamespace) continue;

                            // Must have a "text" property with setter
                            var textProp = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                            if (textProp?.SetMethod == null) continue;

                            // Must inherit from Component (be a Unity component)
                            if (!typeof(Component).IsAssignableFrom(type)) continue;

                            results.Add(type);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return results;
        }

        /// <summary>
        /// Patches an alternate TMP type's text property setter and getter.
        /// Uses reflection-based patch method since we can't use generic TMP_Text.
        /// </summary>
        private static int PatchAlternateTMPType(Type altTmpType, Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int count = 0;
            var prefix = typeof(TranslatorPatches).GetMethod(nameof(AlternateTMP_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);
            var getterPostfix = typeof(TranslatorPatches).GetMethod(nameof(AlternateTMP_GetText_Postfix), BindingFlags.Static | BindingFlags.Public);

            var textProp = altTmpType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);

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

            // Patch the text property getter (for pre-loaded/deserialized text and late font initialization)
            if (textProp?.GetMethod != null)
            {
                try
                {
                    patcher(textProp.GetMethod, null, getterPostfix);
                    count++;
                }
                catch { }
            }

            // Also patch SetText(string) methods if present
            var methods = altTmpType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            foreach (var method in methods)
            {
                if (method.Name == "SetText" && method.GetParameters().Length > 0
                    && method.GetParameters()[0].ParameterType == typeof(string))
                {
                    try
                    {
                        patcher(method, prefix, null);
                        count++;
                    }
                    catch { }
                }
            }

            // NOTE: Font setter patch disabled - causes issues with text becoming empty
            // TODO: Investigate why and fix
            // Patch the font property setter (for late font initialization)
            // var fontPostfix = typeof(TranslatorPatches).GetMethod(nameof(AlternateTMP_SetFont_Postfix), BindingFlags.Static | BindingFlags.Public);
            // var fontProp = altTmpType.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
            // if (fontProp?.SetMethod != null)
            // {
            //     try
            //     {
            //         patcher(fontProp.SetMethod, null, fontPostfix);
            //         count++;
            //     }
            //     catch { }
            // }

            if (count > 0)
            {
                TranslatorCore.LogInfo($"[Patches] Patched alternate TMP: {altTmpType.FullName} ({count} methods)");
            }

            return count;
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

        #region Localization Bridge Components

        // Known text component type names (for bridge detection)
        private static readonly string[] TextComponentTypeNames = {
            "tk2dTextMesh", "TMP_Text", "TextMeshPro", "TextMeshProUGUI",
            "UnityEngine.UI.Text", "Text", "TextMesh"
        };

        // Method name patterns for localization update methods
        private static readonly string[] LocalizationMethodPatterns = {
            "Localize", "UpdateText", "RefreshText", "SetText", "ApplyText",
            "OnLanguageChanged", "OnLocaleChanged", "Refresh",
            "SetDisplay", "UpdateDisplay", "FormatText", "FormatDisplay",
            "FormatDescription", "FormatName", "ShowText", "DisplayText"
        };

        /// <summary>
        /// Finds MonoBehaviour components that bridge localization strings to text components.
        /// These have fields for both localization data AND text component references.
        /// </summary>
        private static List<Type> FindLocalizationBridgeComponents()
        {
            var results = new List<Type>();
            var foundTypeNames = new HashSet<string>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string asmName = asm.GetName().Name;
                    // Skip system/Unity core assemblies but NOT game assemblies
                    if (asmName.StartsWith("System") || asmName.StartsWith("mscorlib") ||
                        asmName == "UnityEngine" || asmName == "UnityEngine.CoreModule")
                        continue;

                    foreach (var type in asm.GetTypes())
                    {
                        try
                        {
                            if (IsLocalizationBridgeComponent(type) && !foundTypeNames.Contains(type.FullName))
                            {
                                results.Add(type);
                                foundTypeNames.Add(type.FullName);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return results;
        }

        /// <summary>
        /// Checks if a type is a localization bridge component.
        /// Must be a MonoBehaviour with both localization string field(s) AND text component field(s).
        /// </summary>
        private static bool IsLocalizationBridgeComponent(Type type)
        {
            if (type == null || type.IsInterface || type.IsAbstract)
                return false;

            // Must inherit from MonoBehaviour (Component)
            if (!typeof(Component).IsAssignableFrom(type))
                return false;

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            bool hasLocalizationField = false;
            bool hasTextComponentField = false;

            foreach (var field in fields)
            {
                var fieldType = field.FieldType;
                string fieldTypeName = fieldType.Name;
                string fieldTypeFullName = fieldType.FullName ?? "";

                // Check for localization string types
                string lowerName = fieldTypeName.ToLowerInvariant();
                foreach (var prefix in LocalizationPrefixes)
                {
                    if (lowerName.Contains(prefix))
                    {
                        foreach (var suffix in LocalizationSuffixes)
                        {
                            if (lowerName.Contains(suffix))
                            {
                                hasLocalizationField = true;
                                break;
                            }
                        }
                        if (hasLocalizationField) break;
                    }
                }

                // Check for text component types
                foreach (var textType in TextComponentTypeNames)
                {
                    if (fieldTypeName == textType || fieldTypeName.EndsWith(textType) ||
                        fieldTypeFullName.Contains(textType))
                    {
                        hasTextComponentField = true;
                        break;
                    }
                }

                if (hasLocalizationField && hasTextComponentField)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Patches a localization bridge component's update methods.
        /// </summary>
        private static int PatchLocalizationBridge(Type bridgeType, Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int count = 0;
            var postfix = typeof(TranslatorPatches).GetMethod(nameof(LocalizationBridge_Postfix), BindingFlags.Static | BindingFlags.Public);

            var methods = bridgeType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                // Skip property getters/setters and special methods
                if (method.IsSpecialName) continue;

                // Check if method name matches our patterns
                bool matches = false;
                foreach (var pattern in LocalizationMethodPatterns)
                {
                    if (method.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matches = true;
                        break;
                    }
                }

                if (!matches) continue;

                // Method should return void or have few parameters (likely an update method)
                if (method.GetParameters().Length > 2)
                    continue;

                try
                {
                    patcher(method, null, postfix);
                    count++;
                }
                catch { }
            }

            if (count > 0)
            {
                TranslatorCore.LogInfo($"[Patches] Found localization bridge: {bridgeType.FullName} ({count} methods patched)");
            }

            return count;
        }

        /// <summary>
        /// Postfix for localization bridge component methods.
        /// DISABLED: This causes double translation when text components have their own patches.
        /// The setter patches on TMP_Text, TMProOld, UI.Text etc. already handle translation.
        /// Keeping this postfix would: read already-translated text -> re-translate -> SetText -> trigger setter again.
        /// </summary>
        public static void LocalizationBridge_Postfix(object __instance)
        {
            // DISABLED to prevent double translation
            // The text component setter patches (TMP_Text, TMProOld, UI.Text, tk2d) already translate text.
            // This postfix would read the already-translated text and try to translate it again,
            // then call SetText which triggers the setter patch, causing an infinite loop of re-translation.
            return;
        }

        /// <summary>
        /// Helper class to abstract different text component types.
        /// </summary>
        private class TextComponentInfo
        {
            public object Component { get; set; }
            public string FontType { get; set; }
            private Func<string> _getText;
            private Action<string> _setText;
            private Func<string> _getFontName;

            public TextComponentInfo(object comp, string fontType, Func<string> getText, Action<string> setText, Func<string> getFontName)
            {
                Component = comp;
                FontType = fontType;
                _getText = getText;
                _setText = setText;
                _getFontName = getFontName;
            }

            public string GetText() => _getText?.Invoke();
            public void SetText(string text) => _setText?.Invoke(text);
            public string GetFontName() => _getFontName?.Invoke();
        }

        /// <summary>
        /// Finds the text component associated with a bridge component.
        /// Checks fields first, then GetComponent.
        /// </summary>
        private static TextComponentInfo FindTextComponentOnBridge(object bridge, Component bridgeComponent)
        {
            var bridgeType = bridge.GetType();
            var fields = bridgeType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            // Check fields for text components
            foreach (var field in fields)
            {
                var fieldValue = field.GetValue(bridge);
                if (fieldValue == null) continue;

                var info = CreateTextComponentInfo(fieldValue);
                if (info != null) return info;
            }

            // Try GetComponent on the same GameObject
            var go = bridgeComponent.gameObject;

            // Try TMP_Text
            var tmpText = go.GetComponent<TMP_Text>();
            if (tmpText != null)
            {
                return new TextComponentInfo(
                    tmpText, "TMP",
                    () => tmpText.text,
                    (s) => tmpText.text = s,
                    () => tmpText.font?.name
                );
            }

            // Try UI.Text
            var uiText = go.GetComponent<Text>();
            if (uiText != null)
            {
                return new TextComponentInfo(
                    uiText, "Unity",
                    () => uiText.text,
                    (s) => uiText.text = s,
                    () => uiText.font?.name
                );
            }

            // Try tk2dTextMesh via reflection
            var tk2dInfo = TryGetTk2dTextMeshInfo(go);
            if (tk2dInfo != null) return tk2dInfo;

            return null;
        }

        /// <summary>
        /// Creates a TextComponentInfo from a field value if it's a known text component type.
        /// </summary>
        private static TextComponentInfo CreateTextComponentInfo(object fieldValue)
        {
            if (fieldValue is TMP_Text tmpText)
            {
                return new TextComponentInfo(
                    tmpText, "TMP",
                    () => tmpText.text,
                    (s) => tmpText.text = s,
                    () => tmpText.font?.name
                );
            }

            if (fieldValue is Text uiText)
            {
                return new TextComponentInfo(
                    uiText, "Unity",
                    () => uiText.text,
                    (s) => uiText.text = s,
                    () => uiText.font?.name
                );
            }

            // Check for tk2dTextMesh via reflection
            var type = fieldValue.GetType();
            if (type.Name == "tk2dTextMesh")
            {
                return CreateTk2dTextComponentInfo(fieldValue, type);
            }

            return null;
        }

        /// <summary>
        /// Try to get tk2dTextMesh from a GameObject via reflection.
        /// </summary>
        private static TextComponentInfo TryGetTk2dTextMeshInfo(GameObject go)
        {
            try
            {
                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    var type = comp.GetType();
                    if (type.Name == "tk2dTextMesh")
                    {
                        return CreateTk2dTextComponentInfo(comp, type);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Creates TextComponentInfo for tk2dTextMesh using reflection.
        /// </summary>
        private static TextComponentInfo CreateTk2dTextComponentInfo(object tk2dComp, Type type)
        {
            var textProp = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
            if (textProp == null) return null;

            return new TextComponentInfo(
                tk2dComp, "tk2d",
                () => textProp.GetValue(tk2dComp, null) as string,
                (s) => textProp.SetValue(tk2dComp, s, null),
                () => TryGetTk2dFontName(tk2dComp)
            );
        }

        #endregion

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

        /// <summary>
        /// Schedule a delayed scan to apply font replacements to TMP components.
        /// Called after scene change to catch early-initialized text.
        /// </summary>
        public static void ScheduleDelayedFontScan(float delaySeconds = 0.5f)
        {
            try
            {
                UniverseLib.RuntimeHelper.StartCoroutine(DelayedFontScanCoroutine(delaySeconds));
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[FontScan] Failed to schedule: {ex.Message}");
            }
        }

        private static System.Collections.IEnumerator DelayedFontScanCoroutine(float delaySeconds)
        {
            yield return new WaitForSeconds(delaySeconds);
            ScanAndApplyFontReplacements();
        }

        /// <summary>
        /// Scan all alternate TMP components and apply font replacements where needed.
        /// Only applies if: translation enabled for the font AND fallback configured.
        /// </summary>
        private static void ScanAndApplyFontReplacements()
        {
            if (_alternateTMPFontAssetType == null)
            {
                TranslatorCore.LogInfo("[FontScan] No alternate TMP type found, skipping scan");
                return;
            }

            try
            {
                // Find the TMP_Text base type for this alternate TMP
                Type tmpTextType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    tmpTextType = asm.GetType("TMProOld.TMP_Text");
                    if (tmpTextType != null) break;
                }

                if (tmpTextType == null)
                {
                    TranslatorCore.LogWarning("[FontScan] TMP_Text type not found");
                    return;
                }

                // Find all TMP text components in the scene
                var allTmpComponents = UnityEngine.Object.FindObjectsOfType(tmpTextType);
                int appliedCount = 0;

                foreach (var component in allTmpComponents)
                {
                    if (component == null) continue;

                    try
                    {
                        // Skip our own UI
                        var unityComponent = component as Component;
                        if (unityComponent != null && TranslatorCore.ShouldSkipTranslation(unityComponent))
                            continue;

                        // Get font name
                        string fontName = TryGetAlternateTMPFontName(component);
                        if (string.IsNullOrEmpty(fontName)) continue;

                        // Skip if this is already a custom font (already replaced)
                        if (FontManager.IsCustomFont(fontName)) continue;

                        // Check if translation is enabled for this font
                        if (!FontManager.IsTranslationEnabled(fontName)) continue;

                        // Check if a fallback is configured
                        string fallbackName = FontManager.GetConfiguredFallback(fontName);
                        if (string.IsNullOrEmpty(fallbackName)) continue;

                        // Apply font replacement
                        TryApplyAlternateTMPReplacementFont(component, fontName);
                        appliedCount++;
                    }
                    catch { }
                }

                if (appliedCount > 0)
                {
                    TranslatorCore.LogInfo($"[FontScan] Applied font replacement to {appliedCount} components");
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[FontScan] Error: {ex.Message}");
            }
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
        /// ADVANCED FALLBACK: Only active when translate_localization_fallback is enabled.
        /// WARNING: Ignores font-based enable/disable settings.
        /// Prefer bridge component patches (LocalizedTextMesh, etc.) which have font context.
        /// </summary>
        public static void CustomLocalization_ToString_Postfix(ref string __result)
        {
            // This is a fallback - only active if explicitly enabled in config
            if (!TranslatorCore.Config.translate_localization_fallback)
                return;

            // Check global translation state
            if (!TranslatorCore.Config.enable_translations)
                return;

            if (string.IsNullOrEmpty(__result))
                return;

            try
            {
                // Translate without component tracking (we don't have a component reference)
                // WARNING: This bypasses font-based enable/disable!
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
                    FontManager.IncrementUsageCount(fontName);

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
                    FontManager.IncrementUsageCount(fontName);

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
                    FontManager.IncrementUsageCount(fontName);

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
                    FontManager.IncrementUsageCount(__instance.font.name);

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
        /// Try to get the font name from a tk2dTextMesh instance via reflection.
        /// tk2d uses bitmap fonts with font property holding the tk2dFont or tk2dFontData.
        /// </summary>
        private static string TryGetTk2dFontName(object instance)
        {
            if (instance == null) return null;

            try
            {
                var type = instance.GetType();

                // Try to get "font" property/field which is typically tk2dFont or tk2dFontData
                var fontProp = type.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                object fontObj = null;

                if (fontProp != null)
                {
                    fontObj = fontProp.GetValue(instance, null);
                }
                else
                {
                    // Try as field
                    var fontField = type.GetField("font", BindingFlags.Public | BindingFlags.Instance);
                    if (fontField != null)
                    {
                        fontObj = fontField.GetValue(instance);
                    }
                }

                if (fontObj == null)
                {
                    // Try "_font" (private backing field)
                    var privateFontField = type.GetField("_font", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (privateFontField != null)
                    {
                        fontObj = privateFontField.GetValue(instance);
                    }
                }

                if (fontObj != null)
                {
                    // Get the name from the font object
                    var fontType = fontObj.GetType();

                    // Try "name" property first
                    var nameProp = fontType.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null)
                    {
                        var name = nameProp.GetValue(fontObj, null) as string;
                        if (!string.IsNullOrEmpty(name))
                            return name;
                    }

                    // Try inherited UnityEngine.Object.name
                    if (fontObj is UnityEngine.Object unityObj)
                    {
                        return unityObj.name;
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Try to get font name from an alternate TMP component via reflection.
        /// </summary>
        private static string TryGetAlternateTMPFontName(object instance)
        {
            if (instance == null) return null;

            try
            {
                var type = instance.GetType();

                // Try "font" property (TMP_FontAsset)
                var fontProp = type.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                if (fontProp != null)
                {
                    var fontObj = fontProp.GetValue(instance, null);
                    if (fontObj is UnityEngine.Object unityObj)
                    {
                        return unityObj.name;
                    }
                }
            }
            catch { }

            return null;
        }

        // Cache for alternate TMP font assets found in the game
        private static Dictionary<string, object> _alternateTMPFontCache = new Dictionary<string, object>();
        private static Type _alternateTMPFontAssetType = null;
        private static bool _alternateTMPFontSearchDone = false;

        /// <summary>
        /// Try to find and apply a replacement font for an alternate TMP component.
        /// Since TMProOld.TMP_FontAsset != TMPro.TMP_FontAsset, we search for fonts already loaded in the game.
        /// Also applies font scale if configured.
        /// </summary>
        private static void TryApplyAlternateTMPReplacementFont(object instance, string originalFontName)
        {
            if (instance == null || string.IsNullOrEmpty(originalFontName)) return;

            TranslatorCore.LogInfo($"[AlternateTMP] TryApply called for font '{originalFontName}'");

            try
            {
                var type = instance.GetType();

                // Apply font scale if configured
                float scale = FontManager.GetFontScale(originalFontName);
                TranslatorCore.LogInfo($"[AlternateTMP] Scale for '{originalFontName}': {scale}");
                if (Math.Abs(scale - 1.0f) > 0.01f)
                {
                    // Try to apply scale via fontSize property
                    var fontSizeProp = type.GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance);
                    if (fontSizeProp != null && fontSizeProp.CanRead && fontSizeProp.CanWrite)
                    {
                        var currentSize = fontSizeProp.GetValue(instance, null);
                        if (currentSize is float floatSize)
                        {
                            // Store original size to avoid compounding scale
                            string instanceId = instance.GetHashCode().ToString();
                            if (!_alternateTMPOriginalSizes.TryGetValue(instanceId, out float originalSize))
                            {
                                originalSize = floatSize;
                                _alternateTMPOriginalSizes[instanceId] = originalSize;
                            }
                            float newSize = originalSize * scale;
                            fontSizeProp.SetValue(instance, newSize, null);
                            TranslatorCore.LogInfo($"[AlternateTMP] Applied scale {scale}: {originalSize} -> {newSize}");
                        }
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[AlternateTMP] No fontSize property found on {type.Name}");
                    }
                }

                // Get font property and search for available fonts (even if no fallback configured)
                // This populates the cache so dropdown can show available fonts
                var fontProp = type.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                if (fontProp != null)
                {
                    var currentFont = fontProp.GetValue(instance, null);
                    if (currentFont != null && !_alternateTMPFontSearchDone)
                    {
                        Type fontAssetType = currentFont.GetType();
                        _alternateTMPFontAssetType = fontAssetType;
                        SearchAlternateTMPFonts(fontAssetType);
                        _alternateTMPFontSearchDone = true;
                    }
                }

                // Get configured fallback font name
                string fallbackName = FontManager.GetConfiguredFallback(originalFontName);
                TranslatorCore.LogInfo($"[AlternateTMP] Fallback for '{originalFontName}': '{fallbackName ?? "(none)"}'");
                if (string.IsNullOrEmpty(fallbackName)) return;

                if (fontProp == null)
                {
                    TranslatorCore.LogWarning($"[AlternateTMP] No 'font' property found on {type.Name}");
                    return;
                }

                // Check if it's a custom font (from fonts/ folder)
                if (FontManager.IsCustomFont(fallbackName))
                {
                    // Strip [Custom] prefix if present
                    string customFontName = fallbackName;
                    if (fallbackName.StartsWith("[Custom] "))
                        customFontName = fallbackName.Substring(9);

                    // DEBUG TEST: Try using an existing game font instead of custom font
                    // This will tell us if the replacement mechanism works at all
                    bool testWithExistingFont = false; // Set to true to test with ARIAL SDF
                    if (testWithExistingFont && _alternateTMPFontCache.TryGetValue("ARIAL SDF", out object testFont))
                    {
                        fontProp.SetValue(instance, testFont, null);
                        TranslatorCore.LogInfo($"[AlternateTMP] DEBUG: Using existing ARIAL SDF for testing");
                        return;
                    }

                    var customFontAsset = CustomFontLoader.LoadCustomFont(customFontName);
                    if (customFontAsset != null)
                    {
                        // DEBUG TEST: Force direct replacement to test if our custom font works
                        // If this shows Hindi text, the problem is with the fallback system
                        // If still empty, the problem is with our custom font rendering
                        bool useDirectReplacement = true; // TODO: Change to false after testing

                        if (!useDirectReplacement)
                        {
                            // Get current font and add custom font as fallback instead of replacing
                            var currentFont = fontProp.GetValue(instance, null);
                            if (currentFont != null)
                            {
                                // Try to add as fallback font (TMP will auto-use for missing glyphs)
                                bool addedAsFallback = TryAddFallbackFont(currentFont, customFontAsset);
                                if (addedAsFallback)
                                {
                                    TranslatorCore.LogInfo($"[AlternateTMP] Added '{customFontName}' as fallback font");
                                    return;
                                }
                            }
                        }

                        // Replace the font directly
                        fontProp.SetValue(instance, customFontAsset, null);
                        TranslatorCore.LogInfo($"[AlternateTMP] Applied custom font '{customFontName}' (direct replacement)");

                        // Force TMP to regenerate mesh with new font
                        try
                        {
                            var forceMeshUpdate = instance.GetType().GetMethod("ForceMeshUpdate",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                                null, Type.EmptyTypes, null);
                            if (forceMeshUpdate != null)
                            {
                                forceMeshUpdate.Invoke(instance, null);
                                TranslatorCore.LogInfo($"[AlternateTMP] Called ForceMeshUpdate");
                            }
                        }
                        catch { }
                        TranslatorCore.LogInfo($"[AlternateTMP] Custom font type: {customFontAsset.GetType().FullName}");

                        // Debug: Check if font has glyphs for the text being set
                        try
                        {
                            var dictField = customFontAsset.GetType().GetField("m_characterDictionary", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (dictField != null)
                            {
                                var dict = dictField.GetValue(customFontAsset);
                                if (dict != null)
                                {
                                    var countProp = dict.GetType().GetProperty("Count");
                                    int count = (int)countProp.GetValue(dict);
                                    TranslatorCore.LogInfo($"[AlternateTMP] Font dictionary has {count} characters");

                                    // Check for specific Devanagari character (U+0928 = à¤¨)
                                    var containsKey = dict.GetType().GetMethod("ContainsKey");
                                    if (containsKey != null)
                                    {
                                        bool hasDevanagari = (bool)containsKey.Invoke(dict, new object[] { 0x0928 });
                                        bool hasLatinA = (bool)containsKey.Invoke(dict, new object[] { 0x0041 });
                                        TranslatorCore.LogInfo($"[AlternateTMP] Has 'à¤¨' (U+0928): {hasDevanagari}, Has 'A' (U+0041): {hasLatinA}");
                                    }
                                }
                            }

                            // Check glyph list
                            var glyphListField = customFontAsset.GetType().GetField("m_glyphInfoList", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (glyphListField != null)
                            {
                                var glyphList = glyphListField.GetValue(customFontAsset);
                                if (glyphList != null)
                                {
                                    var glyphCountProp = glyphList.GetType().GetProperty("Count");
                                    int glyphCount = (int)glyphCountProp.GetValue(glyphList);
                                    TranslatorCore.LogInfo($"[AlternateTMP] Font glyph list has {glyphCount} glyphs");
                                }
                            }
                        }
                        catch (Exception debugEx)
                        {
                            TranslatorCore.LogWarning($"[AlternateTMP] Debug check failed: {debugEx.Message}");
                        }
                        return;
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[AlternateTMP] Failed to load custom font '{customFontName}'");
                        return;
                    }
                }

                // Try to find the fallback font in our cache (exact match)
                if (_alternateTMPFontCache.TryGetValue(fallbackName, out object fallbackFont))
                {
                    fontProp.SetValue(instance, fallbackFont, null);
                    TranslatorCore.LogInfo($"[AlternateTMP] Applied fallback font '{fallbackName}' (exact match)");
                    return;
                }

                // Try partial match (font name contains fallback name or vice versa)
                foreach (var kvp in _alternateTMPFontCache)
                {
                    if (kvp.Key.IndexOf(fallbackName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        fallbackName.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fontProp.SetValue(instance, kvp.Value, null);
                        return;
                    }
                }

                // Log if fallback not found
                TranslatorCore.LogWarning($"[AlternateTMP] Fallback font '{fallbackName}' not found in game fonts");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[AlternateTMP] Error applying font: {ex.Message}");
            }
        }

        // Track fonts that already have our custom fallback added
        private static HashSet<int> _fontsWithFallbackAdded = new HashSet<int>();

        /// <summary>
        /// Try to add a custom font as a fallback font to the original font.
        /// This way TMP will automatically use glyphs from the fallback when missing from original.
        /// </summary>
        private static bool TryAddFallbackFont(object originalFont, object customFont)
        {
            try
            {
                if (originalFont == null || customFont == null) return false;

                // Check if we already added fallback to this font
                int fontId = originalFont.GetHashCode();
                if (_fontsWithFallbackAdded.Contains(fontId))
                {
                    TranslatorCore.LogInfo("[AlternateTMP] Fallback already added to this font");
                    return true; // Already done
                }

                Type fontType = originalFont.GetType();

                // Look for fallbackFontAssets field (List<TMP_FontAsset>)
                var fallbackField = fontType.GetField("fallbackFontAssets", BindingFlags.Public | BindingFlags.Instance);
                if (fallbackField == null)
                {
                    // Try m_fallbackFontAssets
                    fallbackField = fontType.GetField("m_fallbackFontAssets", BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (fallbackField != null)
                {
                    var fallbackList = fallbackField.GetValue(originalFont);
                    if (fallbackList == null)
                    {
                        // Create new list
                        Type listType = typeof(List<>).MakeGenericType(fontType);
                        fallbackList = Activator.CreateInstance(listType);
                        fallbackField.SetValue(originalFont, fallbackList);
                    }

                    // Check if already in list
                    var containsMethod = fallbackList.GetType().GetMethod("Contains");
                    if (containsMethod != null)
                    {
                        bool alreadyContains = (bool)containsMethod.Invoke(fallbackList, new[] { customFont });
                        if (alreadyContains)
                        {
                            _fontsWithFallbackAdded.Add(fontId);
                            return true;
                        }
                    }

                    // Add to list
                    var addMethod = fallbackList.GetType().GetMethod("Add");
                    if (addMethod != null)
                    {
                        addMethod.Invoke(fallbackList, new[] { customFont });
                        _fontsWithFallbackAdded.Add(fontId);
                        TranslatorCore.LogInfo($"[AlternateTMP] Added custom font to fallbackFontAssets list");
                        return true;
                    }
                }

                // Try fallbackFontAssetTable (newer TMP versions)
                var fallbackTableField = fontType.GetField("fallbackFontAssetTable", BindingFlags.Public | BindingFlags.Instance);
                if (fallbackTableField == null)
                {
                    fallbackTableField = fontType.GetField("m_FallbackFontAssetTable", BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (fallbackTableField != null)
                {
                    var fallbackTable = fallbackTableField.GetValue(originalFont);
                    if (fallbackTable == null)
                    {
                        Type listType = typeof(List<>).MakeGenericType(fontType);
                        fallbackTable = Activator.CreateInstance(listType);
                        fallbackTableField.SetValue(originalFont, fallbackTable);
                    }

                    var addMethod = fallbackTable.GetType().GetMethod("Add");
                    if (addMethod != null)
                    {
                        addMethod.Invoke(fallbackTable, new[] { customFont });
                        _fontsWithFallbackAdded.Add(fontId);
                        TranslatorCore.LogInfo($"[AlternateTMP] Added custom font to fallbackFontAssetTable");
                        return true;
                    }
                }

                TranslatorCore.LogWarning("[AlternateTMP] Could not find fallback font list field");
                return false;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[AlternateTMP] Error adding fallback font: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Force a TMP text component to update its mesh after font change.
        /// Uses reflection to call methods like SetAllDirty, ForceMeshUpdate, etc.
        /// </summary>
        private static void ForceTextMeshUpdate(object instance, Type type, int retryCount = 0)
        {
            try
            {
                bool meshUpdateCalled = false;

                // Try ForceMeshUpdate() first - most reliable
                var forceMeshUpdate = type.GetMethod("ForceMeshUpdate", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (forceMeshUpdate != null)
                {
                    try
                    {
                        forceMeshUpdate.Invoke(instance, null);
                        TranslatorCore.LogInfo("[AlternateTMP] Called ForceMeshUpdate()");
                        meshUpdateCalled = true;
                    }
                    catch (Exception fmuEx)
                    {
                        // Log inner exception for debugging
                        var inner = fmuEx.InnerException ?? fmuEx;
                        TranslatorCore.LogWarning($"[AlternateTMP] ForceMeshUpdate() exception: {inner.GetType().Name}: {inner.Message}");
                    }
                }

                // Also try ForceMeshUpdate(bool) overload
                if (!meshUpdateCalled)
                {
                    forceMeshUpdate = type.GetMethod("ForceMeshUpdate", BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(bool) }, null);
                    if (forceMeshUpdate != null)
                    {
                        try
                        {
                            forceMeshUpdate.Invoke(instance, new object[] { true });
                            TranslatorCore.LogInfo("[AlternateTMP] Called ForceMeshUpdate(true)");
                            meshUpdateCalled = true;
                        }
                        catch (Exception fmuEx)
                        {
                            var inner = fmuEx.InnerException ?? fmuEx;
                            TranslatorCore.LogWarning($"[AlternateTMP] ForceMeshUpdate(bool) exception: {inner.GetType().Name}: {inner.Message}");
                        }
                    }
                }

                if (meshUpdateCalled)
                {
                    TranslatorCore.LogInfo("[AlternateTMP] Mesh update successful");
                    return;
                }

                // ForceMeshUpdate failed - schedule a retry for later when component is ready
                // SetAllDirty alone doesn't regenerate the mesh, we need ForceMeshUpdate
                if (retryCount < 5)
                {
                    TranslatorCore.LogInfo($"[AlternateTMP] ForceMeshUpdate failed, scheduling retry {retryCount + 1}/5");
                    ScheduleDelayedMeshUpdate(instance, type, retryCount + 1);
                }
                else
                {
                    TranslatorCore.LogWarning("[AlternateTMP] ForceMeshUpdate failed after 5 retries");
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[AlternateTMP] Unexpected error in ForceTextMeshUpdate: {ex.Message}");
            }
        }

        /// <summary>
        /// Schedule a delayed mesh update retry using a coroutine.
        /// </summary>
        private static void ScheduleDelayedMeshUpdate(object instance, Type type, int retryCount)
        {
            try
            {
                UniverseLib.RuntimeHelper.StartCoroutine(DelayedMeshUpdateCoroutine(instance, type, retryCount));
            }
            catch { }
        }

        private static System.Collections.IEnumerator DelayedMeshUpdateCoroutine(object instance, Type type, int retryCount)
        {
            // Wait one frame
            yield return null;
            // Try again
            ForceTextMeshUpdate(instance, type, retryCount);
        }

        // Cache for original font sizes to avoid compounding scale
        private static Dictionary<string, float> _alternateTMPOriginalSizes = new Dictionary<string, float>();

        /// <summary>
        /// Search for all loaded TMP font assets of the alternate type.
        /// </summary>
        private static void SearchAlternateTMPFonts(Type fontAssetType)
        {
            try
            {
                // Find all loaded font assets of this type
                var allFonts = Resources.FindObjectsOfTypeAll(fontAssetType);
                foreach (var font in allFonts)
                {
                    if (font is UnityEngine.Object unityObj && !string.IsNullOrEmpty(unityObj.name))
                    {
                        if (!_alternateTMPFontCache.ContainsKey(unityObj.name))
                        {
                            _alternateTMPFontCache[unityObj.name] = font;
                            TranslatorCore.LogInfo($"[FontManager] Found alternate TMP font: {unityObj.name}");
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Get the list of alternate TMP font names available in the game.
        /// Used by UI to show only compatible fonts for TMP (alt) type fonts.
        /// </summary>
        public static string[] GetAlternateTMPFontNames()
        {
            if (_alternateTMPFontCache == null || _alternateTMPFontCache.Count == 0)
                return new string[0];

            var names = new string[_alternateTMPFontCache.Count];
            _alternateTMPFontCache.Keys.CopyTo(names, 0);
            return names;
        }

        /// <summary>
        /// Prefix for alternate TMP implementations (TMProOld, etc.).
        /// Uses __0 as generic first parameter name since actual name varies (text, value, etc.).
        /// </summary>
        public static void AlternateTMP_SetText_Prefix(object __instance, ref string __0)
        {
            if (string.IsNullOrEmpty(__0)) return;
            try
            {
                // Skip if this is a text re-set for glyph regeneration (avoid re-translation)
                int instanceId = __instance.GetHashCode();
                if (_skipTextResetInstances.Contains(instanceId)) return;

                var component = __instance as Component;
                if (component == null) return;

                // Skip if part of our own UI
                if (TranslatorCore.ShouldSkipTranslation(component)) return;

                // Check font-based enable/disable
                string fontName = TryGetAlternateTMPFontName(__instance);
                if (!string.IsNullOrEmpty(fontName))
                {
                    // Skip if already a custom font (avoid infinite loop)
                    if (!FontManager.IsCustomFont(fontName))
                    {
                        FontManager.RegisterFontByName(fontName, "TMP (alt)");
                        FontManager.IncrementUsageCount(fontName);
                        if (!FontManager.IsTranslationEnabled(fontName))
                            return;

                        // Try to apply replacement font for non-Latin scripts
                        TryApplyAlternateTMPReplacementFont(__instance, fontName);
                    }
                }

                // Check if own UI (use UI-specific prompt)
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(component);
                __0 = TranslatorCore.TranslateTextWithTracking(__0, component, isOwnUI);
            }
            catch { }
        }

        /// <summary>
        /// Postfix for alternate TMP text getter.
        /// Only applies font replacement when text is read (handles late font initialization).
        /// NOTE: Does NOT translate - translation happens in setter prefix only.
        /// Translating here would cause re-translation of already-translated text.
        /// </summary>
        public static void AlternateTMP_GetText_Postfix(object __instance, ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            try
            {
                var component = __instance as Component;
                if (component == null) return;

                // Skip if part of our own UI
                if (TranslatorCore.ShouldSkipTranslation(component)) return;

                // Check font-based enable/disable and apply font replacement only
                string fontName = TryGetAlternateTMPFontName(__instance);
                if (!string.IsNullOrEmpty(fontName))
                {
                    // Skip if already a custom font (avoid infinite loop)
                    if (!FontManager.IsCustomFont(fontName))
                    {
                        FontManager.RegisterFontByName(fontName, "TMP (alt)");
                        // Don't increment usage count here - it's already done in setter
                        if (!FontManager.IsTranslationEnabled(fontName))
                            return;

                        // Try to apply replacement font (font should be initialized now)
                        TryApplyAlternateTMPReplacementFont(__instance, fontName);
                    }
                }

                // NOTE: No translation here! Translation happens in setter prefix.
                // Translating in getter would re-translate already-translated text.
            }
            catch { }
        }

        // Track instances currently being processed to avoid recursion
        private static HashSet<int> _fontSetInProgress = new HashSet<int>();

        // Track instances being re-set for glyph regeneration (skip translation)
        private static HashSet<int> _skipTextResetInstances = new HashSet<int>();

        /// <summary>
        /// Postfix for alternate TMP font setter.
        /// Applies font replacement when a font is assigned (handles late font initialization).
        /// </summary>
        public static void AlternateTMP_SetFont_Postfix(object __instance)
        {
            if (__instance == null) return;

            // Avoid recursion when we set the replacement font
            int instanceId = __instance.GetHashCode();
            if (_fontSetInProgress.Contains(instanceId)) return;

            try
            {
                _fontSetInProgress.Add(instanceId);

                var component = __instance as Component;
                if (component == null) return;

                // Skip if part of our own UI
                if (TranslatorCore.ShouldSkipTranslation(component)) return;

                // Get the font that was just set
                string fontName = TryGetAlternateTMPFontName(__instance);
                if (!string.IsNullOrEmpty(fontName))
                {
                    FontManager.RegisterFontByName(fontName, "TMP (alt)");

                    // Try to apply replacement font
                    TryApplyAlternateTMPReplacementFont(__instance, fontName);
                }
            }
            catch { }
            finally
            {
                _fontSetInProgress.Remove(instanceId);
            }
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

                // Check font-based enable/disable
                string fontName = TryGetTk2dFontName(__instance);
                if (fontName != null)
                {
                    FontManager.RegisterFontByName(fontName, "tk2d");
                    FontManager.IncrementUsageCount(fontName);
                    if (!FontManager.IsTranslationEnabled(fontName))
                        return;
                }

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

                // Check font-based enable/disable
                string fontName = TryGetTk2dFontName(__instance);
                if (fontName != null)
                {
                    FontManager.RegisterFontByName(fontName, "tk2d");
                    FontManager.IncrementUsageCount(fontName);
                    if (!FontManager.IsTranslationEnabled(fontName))
                        return;
                }

                // Translate and track
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(component);
                __result = TranslatorCore.TranslateTextWithTracking(__result, component, isOwnUI);
            }
            catch { }
        }

        #endregion
    }
}
