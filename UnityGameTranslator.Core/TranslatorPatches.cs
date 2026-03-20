using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Unified descriptor for any text component type the scanner should process.
    /// Covers built-in types (TMP, UI.Text, TextMesh) and generically detected types (NGUI, SuperTextMesh, etc.)
    /// </summary>
    public class RegisteredTextType
    {
        public string Name { get; set; }                    // "TMP_Text", "UI.Text", "UILabel"
        public string Category { get; set; }                // "TMP", "Unity", "TextMesh", "NGUI", "Custom"
        public Type ComponentType { get; set; }
        public PropertyInfo TextProp { get; set; }          // .text (string get/set)
        public PropertyInfo FontProp { get; set; }          // .font or .trueTypeFont (Font)
        public PropertyInfo FontSizeProp { get; set; }      // .fontSize (int or float)
        public PropertyInfo ColorProp { get; set; }         // .color (Color)
        public string FontTypeName { get; set; }            // For FontManager registration
        public bool NeedsForceMeshUpdate { get; set; }      // TMP types need ForceMeshUpdate
        public bool NeedsSetAllDirty { get; set; }          // UI.Text types need SetAllDirty

        // Per-type scan cache (managed by scanner)
        internal UnityEngine.Object[] CachedComponents;
        internal int BatchIndex;
        internal bool LoggedOnce;

        // IL2CPP specific cache (managed by scanner)
        internal object IL2CPPType;                          // Cached Il2CppType.Of<T>() result
        internal MethodInfo TryCastMethod;                   // Cached TryCast<T> generic method

        // Per-strategy state tracking (skip strategies known to fail, prefer strategies known to work)
        // Reset to Unknown on scene change.
        internal StrategyState IL2CPPNativeState = StrategyState.Unknown;
        internal StrategyState TypeHelperState = StrategyState.Unknown;
        internal StrategyState StaticListsState = StrategyState.Unknown;
        internal StrategyState MonoBehaviourFilterState = StrategyState.Unknown;

        /// <summary>
        /// Reset all strategy states to Unknown (call on scene change).
        /// </summary>
        internal void ResetStrategyStates()
        {
            IL2CPPNativeState = StrategyState.Unknown;
            TypeHelperState = StrategyState.Unknown;
            StaticListsState = StrategyState.Unknown;
            MonoBehaviourFilterState = StrategyState.Unknown;
        }

        /// <summary>
        /// Returns true if this type needs the mutualized MonoBehaviourFilter scan.
        /// True when: no direct strategy Works AND MonoBehaviourFilter is not Failed.
        /// </summary>
        internal bool NeedsMonoBehaviourFilter =>
            IL2CPPNativeState != StrategyState.Works &&
            TypeHelperState != StrategyState.Works &&
            StaticListsState != StrategyState.Works &&
            MonoBehaviourFilterState != StrategyState.Failed;
    }

    /// <summary>
    /// State of a discovery strategy for a specific type.
    /// </summary>
    public enum StrategyState
    {
        Unknown = 0,  // Not yet tried
        Works = 1,    // Returned results — use this strategy
        Empty = 2,    // Ran without error but found 0 components — retry (components may appear later)
        Failed = 3    // Exception thrown — never retry this session
    }

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
        // Permanent backup — never cleared. Prevents cumulative scaling.
        private static readonly Dictionary<int, float> _trueOriginalFontSizes = new Dictionary<int, float>();
        public static Dictionary<int, float> TrueOriginalFontSizes => _trueOriginalFontSizes;

        // Generically detected text component types (NGUI UILabel, SuperTextMesh, etc.)
        private static readonly List<RegisteredTextType> _genericTextTypes = new List<RegisteredTextType>();

        /// <summary>
        /// Get the list of generically detected text types (for scanner integration).
        /// </summary>
        public static IReadOnlyList<RegisteredTextType> GenericTextTypes => _genericTextTypes;

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

            // On IL2CPP, TMP assemblies may be loaded after initial TypeHelper.Initialize()
            TypeHelper.TryResolveIfNeeded();

            try
            {
                // TMP_Text.text setter (resolved via TypeHelper to avoid IL2CPP TypeLoadException)
                if (TypeHelper.TMP_TextType != null)
                {
                    var textProp = TypeHelper.TMP_TextType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    if (textProp?.SetMethod != null)
                    {
                        var prefix = typeof(TranslatorPatches).GetMethod(nameof(TMPText_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);
                        patcher(textProp.SetMethod, prefix, null);
                        patchCount++;
                    }

                    // TMP_Text.SetText(string) methods
                    var setTextMethods = TypeHelper.TMP_TextType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
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
                    // TMP_Text.fontSize setter — intercept to apply font scale
                    var fontSizeProp = TypeHelper.TMP_TextType.GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance);
                    if (fontSizeProp?.SetMethod != null)
                    {
                        var prefix = typeof(TranslatorPatches).GetMethod(nameof(TMPText_SetFontSize_Prefix), BindingFlags.Static | BindingFlags.Public);
                        patcher(fontSizeProp.SetMethod, prefix, null);
                        patchCount++;
                    }

                    TranslatorCore.LogInfo($"[Patches] TMP_Text patches applied ({TypeHelper.TMP_TextType.FullName})");
                }
                else
                {
                    TranslatorCore.LogWarning("[Patches] TMP_Text type not found, skipping TMP patches");
                }

                // UI.Text.text setter
                if (TypeHelper.UI_TextType != null)
                {
                    var uiTextProp = TypeHelper.UI_TextType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    if (uiTextProp?.SetMethod != null)
                    {
                        var prefix = typeof(TranslatorPatches).GetMethod(nameof(UIText_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);
                        patcher(uiTextProp.SetMethod, prefix, null);
                        patchCount++;
                    }

                    // UI.Text.fontSize setter — only on Mono (on IL2CPP, causes font atlas corruption)
                    if (TranslatorCore.Adapter != null && !TranslatorCore.Adapter.IsIL2CPP)
                    {
                        var uiFontSizeProp = TypeHelper.UI_TextType.GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance);
                        if (uiFontSizeProp?.SetMethod != null)
                        {
                            var fsPrefix = typeof(TranslatorPatches).GetMethod(nameof(UIText_SetFontSize_Prefix), BindingFlags.Static | BindingFlags.Public);
                            patcher(uiFontSizeProp.SetMethod, fsPrefix, null);
                            patchCount++;
                        }
                    }
                }
                else
                {
                    TranslatorCore.LogWarning("[Patches] UI.Text type not found, skipping UI patches");
                }

                // TextMesh.text setter (legacy 3D text)
                if (TypeHelper.TextMeshType != null)
                {
                    var textMeshProp = TypeHelper.TextMeshType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    if (textMeshProp?.SetMethod != null)
                    {
                        var prefix = typeof(TranslatorPatches).GetMethod(nameof(TextMesh_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);
                        patcher(textMeshProp.SetMethod, prefix, null);
                        patchCount++;
                    }
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

                // Generic text component detection (NGUI UILabel, SuperTextMesh, etc.)
                // Scans all loaded types for MonoBehaviours with a 'text' property
                var genericTextTypes = FindGenericTextTypes();
                foreach (var typeInfo in genericTextTypes)
                {
                    patchCount += PatchGenericTextType(typeInfo, patcher);
                }

                // Generic localization system detection (FALLBACK - disabled by default)
                // Finds custom localization types like LocalisedString, LocalizedText, I18nString, etc.
                // Only patches ToString/op_Implicit - no font context available
                var customLocalizationTypes = FindCustomLocalizationTypes();
                foreach (var locType in customLocalizationTypes)
                {
                    patchCount += PatchCustomLocalizationType(locType, patcher);
                }

                // Graphic.OnEnable postfix — detect when text components are activated
                // and re-apply clone font + warm atlas (fixes transparent text on inactive→active)
                patchCount += PatchGraphicOnEnable(patcher);
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

        #region Generic Text Type Detection

        // Known framework class names (explicit detection — Tier 1)
        private static readonly Dictionary<string, string> KnownTextTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "UILabel", "NGUI" },              // NGUI (very popular in Asian games)
            { "SuperTextMesh", "SuperTextMesh" }, // Super Text Mesh asset
            { "dfLabel", "DaikonForge" },        // Daikon Forge GUI (legacy)
            { "dfRichTextLabel", "DaikonForge" },
        };

        // Heuristic class name patterns for generic detection
        private static readonly string[] TextClassHints = { "Label", "TextField", "Caption", "TextUI", "UIText", "GameText" };

        // Types to skip in generic detection (already handled, or known non-text)
        private static readonly HashSet<string> GenericExcludedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TMP_Text", "TextMeshPro", "TextMeshProUGUI", "Text", "TextMesh",
            "InputField", "TMP_InputField", "tk2dTextMesh",
            "TMP_Dropdown", "Dropdown", "Toggle", "Button", "Slider", "Scrollbar",
            "ScrollRect", "LayoutGroup", "ContentSizeFitter", "CanvasScaler",
            "DynamicScrollbarHider", // Our own component
        };

        // Common font property names to check (in priority order)
        private static readonly string[] FontPropertyNames = { "font", "trueTypeFont", "fontAsset" };
        private static readonly string[] FontSizePropertyNames = { "fontSize", "size", "fontsize" };

        /// <summary>
        /// Scan all loaded assemblies for MonoBehaviour types with a 'text' property.
        /// Returns info about each detected type including font/size property access.
        /// </summary>
        private static List<RegisteredTextType> FindGenericTextTypes()
        {
            var results = new List<RegisteredTextType>();
            var pubInst = BindingFlags.Public | BindingFlags.Instance;

            // Collect types we already handle (to avoid double-patching)
            var handledTypes = new HashSet<Type>();
            if (TypeHelper.TMP_TextType != null) handledTypes.Add(TypeHelper.TMP_TextType);
            if (TypeHelper.UI_TextType != null) handledTypes.Add(TypeHelper.UI_TextType);
            if (TypeHelper.TextMeshType != null) handledTypes.Add(TypeHelper.TextMeshType);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string asmName = asm.GetName().Name;
                    // Skip Unity/System/Harmony/modloader assemblies
                    if (asmName.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) && !asmName.Contains("NGUI"))
                        continue;
                    if (asmName.StartsWith("System") || asmName.StartsWith("mscorlib") ||
                        asmName.StartsWith("Mono.") || asmName.StartsWith("0Harmony") ||
                        asmName.StartsWith("HarmonyLib") || asmName.StartsWith("MelonLoader") ||
                        asmName.StartsWith("BepInEx") || asmName.StartsWith("UniverseLib") ||
                        asmName.StartsWith("UnityGameTranslator") || asmName.StartsWith("Newtonsoft") ||
                        asmName.StartsWith("Il2CppInterop") || asmName.StartsWith("Il2CppSystem"))
                        continue;

                    foreach (var type in asm.GetTypes())
                    {
                        try
                        {
                            // Must be a class, not abstract, not generic
                            if (!type.IsClass || type.IsAbstract || type.IsGenericType) continue;

                            // Skip already handled types
                            string typeName = type.Name;
                            // Strip Il2Cpp prefix for name matching
                            string cleanName = typeName.StartsWith("Il2Cpp") ? typeName.Substring(6) : typeName;
                            if (GenericExcludedTypes.Contains(cleanName)) continue;
                            if (handledTypes.Contains(type)) continue;

                            // Check if it inherits from MonoBehaviour (Component chain)
                            if (!typeof(Component).IsAssignableFrom(type) && !InheritsFromComponent(type))
                                continue;

                            // Must have a 'text' property with string get + set
                            var textProp = type.GetProperty("text", pubInst);
                            if (textProp == null || !textProp.CanRead || !textProp.CanWrite) continue;
                            if (textProp.PropertyType != typeof(string)) continue;
                            if (textProp.SetMethod == null) continue;

                            // Check: known framework OR heuristic name match
                            string framework = null;
                            if (KnownTextTypes.TryGetValue(cleanName, out framework))
                            {
                                // Explicit match — always include
                            }
                            else
                            {
                                // Heuristic: class name must suggest it's a text component
                                bool nameMatch = false;
                                foreach (var hint in TextClassHints)
                                {
                                    if (cleanName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        nameMatch = true;
                                        break;
                                    }
                                }
                                if (!nameMatch) continue;
                                framework = "Custom";
                            }

                            // Detect font properties
                            PropertyInfo fontProp = null;
                            foreach (var fpName in FontPropertyNames)
                            {
                                var fp = type.GetProperty(fpName, pubInst);
                                if (fp != null && fp.CanRead)
                                {
                                    // Accept Font, Object, or any type with a .name property
                                    fontProp = fp;
                                    break;
                                }
                            }

                            // Detect fontSize property
                            PropertyInfo fontSizeProp = null;
                            foreach (var fsName in FontSizePropertyNames)
                            {
                                var fs = type.GetProperty(fsName, pubInst);
                                if (fs != null && fs.CanRead && fs.CanWrite &&
                                    (fs.PropertyType == typeof(float) || fs.PropertyType == typeof(int) || fs.PropertyType == typeof(System.Single)))
                                {
                                    fontSizeProp = fs;
                                    break;
                                }
                            }

                            // Detect color property
                            PropertyInfo colorProp = type.GetProperty("color", pubInst);

                            var info = new RegisteredTextType
                            {
                                Name = cleanName,
                                Category = framework,
                                ComponentType = type,
                                TextProp = textProp,
                                FontProp = fontProp,
                                FontSizeProp = fontSizeProp,
                                ColorProp = colorProp,
                                FontTypeName = framework == "NGUI" ? "NGUI" : $"Custom ({cleanName})",
                                NeedsForceMeshUpdate = false,
                                NeedsSetAllDirty = false
                            };

                            results.Add(info);
                            TranslatorCore.LogInfo($"[Patches] Detected generic text type: {type.FullName} ({framework})" +
                                $" font={fontProp?.Name ?? "none"}, fontSize={fontSizeProp?.Name ?? "none"}");
                        }
                        catch { }
                    }
                }
                catch { }
            }

            _genericTextTypes.AddRange(results);
            return results;
        }

        /// <summary>
        /// Check if a type inherits from Component (handles IL2CPP where IsAssignableFrom may fail).
        /// </summary>
        private static bool InheritsFromComponent(Type type)
        {
            var current = type.BaseType;
            while (current != null)
            {
                if (current == typeof(Component) || current.Name == "Component" || current.Name == "MonoBehaviour")
                    return true;
                current = current.BaseType;
            }
            return false;
        }

        /// <summary>
        /// Patch a generically detected text type's set_text with our prefix.
        /// </summary>
        private static int PatchGenericTextType(RegisteredTextType typeInfo, Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int patched = 0;
            try
            {
                var setMethod = typeInfo.TextProp.SetMethod;
                if (setMethod != null)
                {
                    var prefix = typeof(TranslatorPatches).GetMethod(nameof(GenericText_SetText_Prefix),
                        BindingFlags.Static | BindingFlags.Public);
                    patcher(setMethod, prefix, null);
                    patched++;
                    TranslatorCore.LogInfo($"[Patches] Patched {typeInfo.Category}: {typeInfo.ComponentType.Name}.set_text");
                }

                // Also patch get_text for scanner (catches pre-loaded text)
                var getMethod = typeInfo.TextProp.GetMethod;
                if (getMethod != null)
                {
                    var postfix = typeof(TranslatorPatches).GetMethod(nameof(GenericText_GetText_Postfix),
                        BindingFlags.Static | BindingFlags.Public);
                    patcher(getMethod, null, postfix);
                    patched++;
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Patches] Failed to patch {typeInfo.ComponentType.Name}: {ex.Message}");
            }
            return patched;
        }

        /// <summary>
        /// Prefix for generically detected text components (NGUI UILabel, etc.)
        /// </summary>
        public static void GenericText_SetText_Prefix(object __instance, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (!TranslatorCore.Config.enable_translations) return;
            try
            {
                var component = __instance as Component;
                if (component == null) return;
                if (TranslatorCore.ShouldSkipTranslation(component)) return;

                // Find the matching type info for font handling
                var typeInfo = FindTypeInfoForInstance(__instance);

                string fontName = null;
                string settingsFontName = null;

                // Get font name if available
                if (typeInfo?.FontProp != null)
                {
                    try
                    {
                        var fontObj = typeInfo.FontProp.GetValue(__instance, null);
                        if (fontObj is UnityEngine.Object uobj && !string.IsNullOrEmpty(uobj.name))
                        {
                            fontName = uobj.name;
                            int compId = component.GetInstanceID();
                            settingsFontName = FontManager.GetSettingsFontName(compId, fontName);

                            FontManager.RegisterFontByName(settingsFontName, typeInfo.FontTypeName);
                            FontManager.IncrementUsageCount(settingsFontName);

                            if (!FontManager.IsTranslationEnabled(settingsFontName))
                                return;
                        }
                    }
                    catch { }
                }

                // Translate
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(component);
                value = TranslatorCore.TranslateTextWithTracking(value, component, isOwnUI);

                // Apply font scale
                if (typeInfo?.FontSizeProp != null && !string.IsNullOrEmpty(settingsFontName ?? fontName))
                {
                    ApplyGenericFontScale(__instance, typeInfo, settingsFontName ?? fontName);
                }
            }
            catch { }
        }

        /// <summary>
        /// Postfix for generically detected text getters (catches pre-loaded text).
        /// </summary>
        public static void GenericText_GetText_Postfix(object __instance, ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            if (!TranslatorCore.Config.enable_translations) return;
            try
            {
                var component = __instance as Component;
                if (component == null) return;
                if (TranslatorCore.ShouldSkipTranslation(component)) return;

                var typeInfo = FindTypeInfoForInstance(__instance);

                // Check font-based enable/disable
                if (typeInfo?.FontProp != null)
                {
                    try
                    {
                        var fontObj = typeInfo.FontProp.GetValue(__instance, null);
                        if (fontObj is UnityEngine.Object uobj && !string.IsNullOrEmpty(uobj.name))
                        {
                            int compId = component.GetInstanceID();
                            string settingsFontName = FontManager.GetSettingsFontName(compId, uobj.name);
                            FontManager.RegisterFontByName(settingsFontName, typeInfo.FontTypeName);
                            if (!FontManager.IsTranslationEnabled(settingsFontName))
                                return;
                        }
                    }
                    catch { }
                }

                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(component);
                __result = TranslatorCore.TranslateTextWithTracking(__result, component, isOwnUI);
            }
            catch { }
        }

        /// <summary>
        /// Find the RegisteredTextType matching an instance's type.
        /// </summary>
        private static RegisteredTextType FindTypeInfoForInstance(object instance)
        {
            if (instance == null) return null;
            var type = instance.GetType();
            foreach (var info in _genericTextTypes)
            {
                if (info.ComponentType.IsAssignableFrom(type) || info.ComponentType == type)
                    return info;
            }
            return null;
        }

        /// <summary>
        /// Apply font scale for a generic text component using its detected fontSize property.
        /// </summary>
        private static void ApplyGenericFontScale(object instance, RegisteredTextType typeInfo, string fontName)
        {
            if (typeInfo.FontSizeProp == null || string.IsNullOrEmpty(fontName)) return;

            float scale = FontManager.GetFontScale(fontName);
            int instanceId = TypeHelper.GetInstanceID(instance);
            if (instanceId == -1) return;

            float originalSize;
            if (!_originalFontSizes.TryGetValue(instanceId, out originalSize))
            {
                try
                {
                    var val = typeInfo.FontSizeProp.GetValue(instance, null);
                    if (val is float f) originalSize = f;
                    else if (val is int i) originalSize = i;
                    else return;
                }
                catch { return; }
                if (originalSize <= 0) return;
                _originalFontSizes[instanceId] = originalSize;
            }

            if (Math.Abs(scale - 1.0f) < 0.001f)
            {
                // Restore original
                try
                {
                    float currentSize = Convert.ToSingle(typeInfo.FontSizeProp.GetValue(instance, null));
                    if (Math.Abs(currentSize - originalSize) > 0.1f)
                        SetGenericFontSize(typeInfo, instance, originalSize);
                }
                catch { }
                return;
            }

            float scaledSize = originalSize * scale;
            try
            {
                float currentSize = Convert.ToSingle(typeInfo.FontSizeProp.GetValue(instance, null));
                if (Math.Abs(currentSize - scaledSize) > 0.1f)
                    SetGenericFontSize(typeInfo, instance, scaledSize);
            }
            catch { }
        }

        private static void SetGenericFontSize(RegisteredTextType typeInfo, object instance, float size)
        {
            if (typeInfo.FontSizeProp.PropertyType == typeof(int))
                typeInfo.FontSizeProp.SetValue(instance, (int)Math.Round(size), null);
            else
                typeInfo.FontSizeProp.SetValue(instance, size, null);
        }

        #endregion

        /// <summary>
        /// Finds alternate TMP implementations in different namespaces (TMProOld, etc.).
        /// Some games bundle older versions of TextMeshPro with different namespaces.
        /// </summary>
        private static List<Type> FindAlternateTMPTypes()
        {
            var results = new List<Type>();
            var standardTmpType = TypeHelper.TMP_TextType;
            if (standardTmpType == null) return results; // No TMP at all
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
            if (TypeHelper.TMP_TextType != null)
            {
                var tmpComp = go.GetComponent(TypeHelper.TMP_TextType);
                if (tmpComp != null)
                {
                    return CreateReflectionTextComponentInfo(tmpComp, "TMP");
                }
            }

            // Try UI.Text
            if (TypeHelper.UI_TextType != null)
            {
                var uiComp = go.GetComponent(TypeHelper.UI_TextType);
                if (uiComp != null)
                {
                    return CreateReflectionTextComponentInfo(uiComp, "Unity");
                }
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
            if (fieldValue == null) return null;

            var type = fieldValue.GetType();

            // Check TMP
            if (TypeHelper.TMP_TextType != null && TypeHelper.TMP_TextType.IsAssignableFrom(type))
            {
                return CreateReflectionTextComponentInfo(fieldValue, "TMP");
            }

            // Check UI.Text
            if (TypeHelper.UI_TextType != null && TypeHelper.UI_TextType.IsAssignableFrom(type))
            {
                return CreateReflectionTextComponentInfo(fieldValue, "Unity");
            }

            // Check for tk2dTextMesh via reflection
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
        /// Creates TextComponentInfo for TMP or UI.Text using TypeHelper reflection.
        /// </summary>
        private static TextComponentInfo CreateReflectionTextComponentInfo(object component, string fontType)
        {
            return new TextComponentInfo(
                component, fontType,
                () => TypeHelper.GetText(component),
                (s) => TypeHelper.SetText(component, s),
                () => TypeHelper.GetFontName(component)
            );
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

            // Never patch our own types
            if (type.Namespace != null && type.Namespace.StartsWith("UnityGameTranslator"))
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
        /// Check if a text component is the textComponent of an InputField (should not be translated).
        /// Caches the result for performance. Works for both UI.InputField and TMP_InputField.
        /// </summary>
        private static bool IsInputFieldTextComponentCached(object textComponent)
        {
            int id = TypeHelper.GetInstanceID(textComponent);
            if (id == -1) return false;

            if (inputFieldTextCache.TryGetValue(id, out bool isInputFieldText))
                return isInputFieldText;

            bool result = TypeHelper.IsInputFieldTextComponent(textComponent);
            inputFieldTextCache[id] = result;
            return result;
        }

        /// <summary>
        /// Clear the InputField cache (call on scene change).
        /// </summary>
        public static void ClearCache()
        {
            inputFieldTextCache.Clear();
            _altTMPFontReplacedIds.Clear();
            _fontNameCache.Clear();
            _patchedComponentRefs.Clear();
            _typewritingState.Clear();
            _activeTypewriting.Clear();
        }

        /// <summary>
        /// Clear cached original font sizes.
        /// Only call on scene change — NOT on scale change, because
        /// clearing causes the scaled size to be read as "original".
        /// </summary>
        public static void ClearFontSizeCache()
        {
            _originalFontSizes.Clear();
            _alternateTMPOriginalSizes.Clear();
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
                UnityEngine.Object[] allTmpComponents;
                try { allTmpComponents = UnityEngine.Object.FindObjectsOfType(tmpTextType); }
                catch { allTmpComponents = Resources.FindObjectsOfTypeAll(tmpTextType); }
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

        #region OnEnable Hook

        /// <summary>
        /// Patch Graphic.OnEnable to detect when text components transition from inactive to active.
        /// When a UI.Text with a clone font is enabled, Unity needs a fresh font bind + atlas warm.
        /// </summary>
        private static int PatchGraphicOnEnable(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            try
            {
                // Find Graphic type (base class of UI.Text, MaskableGraphic)
                // OnEnable is defined on Graphic (protected virtual)
                Type graphicType = null;
                if (TypeHelper.UI_TextType != null)
                {
                    // Walk up: Text → MaskableGraphic → Graphic
                    graphicType = TypeHelper.UI_TextType.BaseType?.BaseType;
                }
                if (graphicType == null)
                {
                    // Fallback: find by name
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        graphicType = asm.GetType("UnityEngine.UI.Graphic");
                        if (graphicType != null) break;
                    }
                }

                if (graphicType == null)
                {
                    TranslatorCore.LogWarning("[Patches] Graphic type not found, OnEnable hook skipped");
                    return 0;
                }

                var onEnableMethod = graphicType.GetMethod("OnEnable",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (onEnableMethod == null)
                {
                    TranslatorCore.LogWarning("[Patches] Graphic.OnEnable not found, hook skipped");
                    return 0;
                }

                var postfix = typeof(TranslatorPatches).GetMethod(nameof(Graphic_OnEnable_Postfix),
                    BindingFlags.Static | BindingFlags.Public);
                patcher(onEnableMethod, null, postfix);
                TranslatorCore.LogInfo("[Patches] Graphic.OnEnable postfix applied");
                return 1;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Patches] Failed to patch Graphic.OnEnable: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Postfix for Graphic.OnEnable. Fires for ALL graphics (Image, RawImage, Text...).
        /// Must exit fast for non-text components.
        /// For text components with clone fonts: re-apply font + warm atlas.
        /// </summary>
        public static void Graphic_OnEnable_Postfix(object __instance)
        {
            try
            {
                // Skip during shutdown or if not initialized
                if (TranslatorCore.Adapter == null || TranslatorCore.Config == null) return;
                if (!TranslatorCore.Config.enable_translations) return;

                _onEnableFireCount++;
                if (_onEnableFireCount == 1 || _onEnableFireCount == 100 || _onEnableFireCount == 1000)
                    TranslatorCore.LogInfo($"[OnEnable] Fire count: {_onEnableFireCount}");

                // Fast exit: only process types we know are text components
                if (__instance == null) return;
                var type = __instance.GetType();
                bool isText = (TypeHelper.UI_TextType != null && TypeHelper.UI_TextType.IsAssignableFrom(type));
                if (!isText) return;

                var comp = __instance as Component;
                if (comp == null) return;

                int compId = TypeHelper.GetInstanceID(__instance);
                if (compId == -1) return;

                // Check if this component has a tracked original font (= we replaced its font before)
                string originalFontName = FontManager.GetOriginalFontName(compId);
                if (originalFontName == null) return;

                // Get the settings font name and check if a replacement is configured
                string settingsFontName = FontManager.GetSettingsFontName(compId, originalFontName);
                var replacementFont = FontManager.GetUnityReplacementFont(settingsFontName);
                if (replacementFont == null) return;

                // Re-apply the clone font — this is the moment Unity sets up the CanvasRenderer,
                // so the font binding will be complete (unlike when set on inactive components).
                TypeHelper.SetFont(__instance, replacementFont);

                // Ensure the clone's atlas has all chars for this component's text
                string text = TypeHelper.GetText(__instance);
                if (!string.IsNullOrEmpty(text))
                {
                    string replaceName = replacementFont.name;
                    FontManager.EnsureCharsInCloneAtlasDirect(text, replacementFont, replaceName);

                    // Force complete mesh regeneration — SetFont alone doesn't rebuild
                    // the vertex mesh on IL2CPP. We need to trigger the full dirty chain.
                    try
                    {
                        var compType = __instance.GetType();
                        var setVertsDirty = compType.GetMethod("SetVerticesDirty", BindingFlags.Public | BindingFlags.Instance);
                        var setLayoutDirty = compType.GetMethod("SetLayoutDirty", BindingFlags.Public | BindingFlags.Instance);
                        var setMatDirty = compType.GetMethod("SetMaterialDirty", BindingFlags.Public | BindingFlags.Instance);
                        setVertsDirty?.Invoke(__instance, null);
                        setLayoutDirty?.Invoke(__instance, null);
                        setMatDirty?.Invoke(__instance, null);
                    }
                    catch { }
                }

                if (_onEnableLogCount < 10)
                {
                    _onEnableLogCount++;
                    // After SetFont, immediately verify what font the component actually has
                    object verifyFont = TypeHelper.GetFont(__instance);
                    string verifyName = (verifyFont is UnityEngine.Object vfo) ? vfo.name : "null";
                    TranslatorCore.LogInfo($"[OnEnable] compId={compId} settings='{settingsFontName}' clone='{replacementFont.name}' actualAfter='{verifyName}' text='{(text != null && text.Length > 20 ? text.Substring(0,20) : text)}'");
                }
            }
            catch { }
        }

        #endregion

        /// <summary>
        /// Apply font scale to a text component (TMP or UI.Text).
        /// Stores original size on first call and applies scale relative to it.
        /// </summary>
        private static void ApplyFontScale(object instance, string fontName)
        {
            if (instance == null || string.IsNullOrEmpty(fontName)) return;

            float scale = FontManager.GetFontScale(fontName);
            // Fast exit: if scale is 1.0 and we haven't stored an original size, nothing to do
            if (Math.Abs(scale - 1.0f) < 0.001f)
            {
                int quickId = TypeHelper.GetInstanceID(instance);
                if (quickId == -1 || !_originalFontSizes.ContainsKey(quickId))
                    return;
            }
            int instanceId = TypeHelper.GetInstanceID(instance);
            if (instanceId == -1) return;

            float originalSize;
            if (!_originalFontSizes.TryGetValue(instanceId, out originalSize))
            {
                if (!_trueOriginalFontSizes.TryGetValue(instanceId, out originalSize))
                {
                    // Skip components that inherited the clone from template
                    // Their fontSize is already scaled — re-scaling would double it
                    if (_inheritedCloneComponents.Contains(instanceId))
                        return;

                    originalSize = TypeHelper.GetFontSize(instance);
                    if (originalSize < 0) return;
                    _trueOriginalFontSizes[instanceId] = originalSize;
                }
                _originalFontSizes[instanceId] = originalSize;
            }

            float targetSize = originalSize * scale;

            float currentSize = TypeHelper.GetFontSize(instance);
            if (currentSize >= 0 && Math.Abs(currentSize - targetSize) > 0.1f)
            {
                _bypassFontSizePrefix = true;
                TypeHelper.SetFontSize(instance, targetSize);
                _bypassFontSizePrefix = false;
            }

            // Also scale bestFit maxSize (Mono only — IL2CPP causes atlas corruption)
            if (TranslatorCore.Adapter == null || !TranslatorCore.Adapter.IsIL2CPP)
                ApplyBestFitScale(instance, instanceId, scale);
        }

        // Cache original resizeTextMaxSize per component
        private static readonly Dictionary<int, int> _originalMaxFontSizes = new Dictionary<int, int>();

        public static void ApplyBestFitScalePublic(object instance, int instanceId, float scale)
            => ApplyBestFitScale(instance, instanceId, scale);

        private static void ApplyBestFitScale(object instance, int instanceId, float scale)
        {
            try
            {
                var type = instance.GetType();
                var bestFitProp = type.GetProperty("resizeTextForBestFit", BindingFlags.Public | BindingFlags.Instance);
                if (bestFitProp == null) return;

                bool bestFit = (bool)bestFitProp.GetValue(instance, null);
                if (!bestFit) return;

                var maxSizeProp = type.GetProperty("resizeTextMaxSize", BindingFlags.Public | BindingFlags.Instance);
                if (maxSizeProp == null || !maxSizeProp.CanWrite) return;

                int currentMax = (int)maxSizeProp.GetValue(instance, null);

                // Store original maxSize on first encounter
                if (!_originalMaxFontSizes.ContainsKey(instanceId))
                    _originalMaxFontSizes[instanceId] = currentMax;

                int originalMax = _originalMaxFontSizes[instanceId];
                int targetMax = (int)(originalMax * scale);
                if (targetMax < 1) targetMax = 1;

                if (currentMax != targetMax)
                    maxSizeProp.SetValue(instance, targetMax, null);
            }
            catch { }
        }

        /// <summary>
        /// Shared prefix logic for TMP/UI/TextMesh text patches.
        /// Handles font registration, InputField exclusion, translation, and font scale.
        /// </summary>
        // Cache font name per component instanceId (avoids GetFont reflection on every set_text)
        // Key: instanceId, Value: font name (null if no font). Cleared on scene change.
        private static readonly Dictionary<int, string> _fontNameCache = new Dictionary<int, string>();
        // Component refs seen by the patch (for highlight — scanner cache misses some)
        private static readonly Dictionary<int, object> _patchedComponentRefs = new Dictionary<int, object>();
        /// <summary>
        /// Expose font name cache and component refs for highlight/size operations.
        /// The scanner cache may not contain all components reached by the patch.
        /// </summary>
        public static Dictionary<int, string> FontNameCache => _fontNameCache;
        public static Dictionary<int, object> PatchedComponentRefs => _patchedComponentRefs;

        // === TYPEWRITING DETECTION ===
        // Per-component tracking to detect typewriting effects (text growing char by char).
        // When detected, skip translation until the text stabilizes.
        private struct TypewritingState
        {
            public string Text;
            public float Timestamp;
            public bool Queued; // true = already queued for translation, don't re-queue
        }
        private static readonly Dictionary<int, TypewritingState> _typewritingState = new Dictionary<int, TypewritingState>();
        private const float TYPEWRITING_STABILIZE_MS = 500f; // ms without change = text is final

        /// <summary>
        /// Check if a set_text call looks like a typewriting effect (text growing on same component).
        /// Returns true if we should skip translation for this call.
        /// </summary>
        /// <summary>
        /// Debounce cache-miss translations. ALL new texts (cache miss) are held for
        /// TYPEWRITING_STABILIZE_MS before being queued for AI. This catches typewriting
        /// effects where the text grows char by char. Cache hits bypass this entirely.
        /// Returns true if the text should be skipped (still stabilizing).
        /// </summary>
        // If text grows AND is within this time window, it's typewriting (regardless of chars added)
        // The key indicator is RAPID growth on the same component, not the size of each addition
        private static int _twLogCount = 0;

        /// <summary>
        /// Detect typewriting effects: text growing by a few chars at a time on the same component.
        /// Only defers if the text grows by 1-5 chars (typewriting). Larger additions (dialogue
        /// accumulation, paragraph appends) are allowed through immediately.
        /// Returns true if the text should NOT be queued yet (typewriting in progress).
        /// </summary>
        public static bool IsTypewritingInProgress(int compId, string newText)
        {
            if (compId == -1 || string.IsNullOrEmpty(newText)) return false;

            float now = Time.realtimeSinceStartup;

            if (_typewritingState.TryGetValue(compId, out var state))
            {
                if (state.Text == newText)
                {
                    return true;
                }

                float elapsed = (now - state.Timestamp) * 1000f;
                bool isGrowing = newText.Length > state.Text.Length && newText.StartsWith(state.Text);
                int charsAdded = isGrowing ? newText.Length - state.Text.Length : -1;

                if (_twLogCount < 50)
                {
                    _twLogCount++;
                    string action;
                    if (isGrowing && elapsed < TYPEWRITING_STABILIZE_MS)
                        action = "SKIP(typewriting)";
                    else if (isGrowing)
                        action = $"ALLOW(elapsed={elapsed:F0}ms)";
                    else
                        action = $"ALLOW(notGrowing)";
                    TranslatorCore.LogInfo($"[TW] comp={compId} prev={state.Text.Length}c new={newText.Length}c added={charsAdded} elapsed={elapsed:F0}ms → {action}");
                }

                // Any text change on a known component (growing, shrinking, or replaced)
                // within the time window → defer. The game may be erasing and restarting
                // typewriting, or buffering multiple chars. Always wait for stabilization.
                _typewritingState[compId] = new TypewritingState { Text = newText, Timestamp = now, Queued = false };
                _activeTypewriting.Add(compId);
                return true;
            }

            // First time — defer for stabilization. We can't know yet if this is
            // typewriting or a normal text. ProcessStabilizedTypewriting will queue
            // for translation after 500ms if the text doesn't change.
            _typewritingState[compId] = new TypewritingState { Text = newText, Timestamp = now };
            _activeTypewriting.Add(compId);
            return true;
        }

        /// <summary>
        /// Check for stabilized typewriting texts and queue them for translation.
        /// Called from ProcessPendingUpdates (main thread, periodic).
        /// </summary>
        // Track which compIds are actually in a typewriting sequence (had a skip)
        private static readonly HashSet<int> _activeTypewriting = new HashSet<int>();

        public static void ProcessStabilizedTypewriting()
        {
            if (_activeTypewriting.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            var stabilized = new List<int>();

            foreach (int compId in _activeTypewriting)
            {
                if (!_typewritingState.TryGetValue(compId, out var state)) continue;
                float elapsed = (now - state.Timestamp) * 1000f;
                if (elapsed >= TYPEWRITING_STABILIZE_MS)
                    stabilized.Add(compId);
            }

            foreach (int compId in stabilized)
            {
                _activeTypewriting.Remove(compId);

                if (!_typewritingState.TryGetValue(compId, out var state)) continue;
                if (state.Queued) continue; // Already processed this stabilized text

                // Mark as queued but keep in state — if more chars are added,
                // IsTypewritingInProgress will detect it and reset.
                _typewritingState[compId] = new TypewritingState
                {
                    Text = state.Text,
                    Timestamp = state.Timestamp,
                    Queued = true
                };

                object comp = null;
                _patchedComponentRefs.TryGetValue(compId, out comp);

                string normalizedText = TranslatorCore.NormalizeForCacheLookup(state.Text);

                // Check if this text is already cached (exact match)
                if (TranslatorCore.TranslationCache.ContainsKey(normalizedText))
                {
                    // Already cached — re-trigger SetText to apply translation via prefix
                    if (comp != null)
                    {
                        try { TypeHelper.SetText(comp, state.Text); }
                        catch { }
                    }
                }
                // Check if this text is a PREFIX of an existing cache key
                // (= partial text from an accumulating dialogue already translated)
                else if (IsPrefixOfCachedKey(normalizedText))
                {
                    // Partial text of a known dialogue — don't queue, don't notify.
                    // The full text will arrive later and be translated from cache.
                }
                else
                {
                    // Not cached — queue for AI translation
                    TranslatorCore.QueueForTranslation(state.Text, comp);
                }
            }
        }

        /// <summary>
        /// Check if a normalized text is a prefix of any existing cache key.
        /// Used to detect partial dialogue text that will be completed later.
        /// </summary>
        private static bool IsPrefixOfCachedKey(string normalizedText)
        {
            if (string.IsNullOrEmpty(normalizedText) || normalizedText.Length < 3) return false;
            try
            {
                foreach (var key in TranslatorCore.TranslationCache.Keys)
                {
                    if (key.Length > normalizedText.Length && key.StartsWith(normalizedText))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Clear typewriting state (on scene change, settings change, etc.)
        /// </summary>
        public static void ClearTypewritingState()
        {
            _typewritingState.Clear();
        }

        // === PROFILING (activate via debug file in plugin folder) ===
        private static readonly System.Diagnostics.Stopwatch _profSw = new System.Diagnostics.Stopwatch();
        private static long _profCallCount = 0;
        private static long _profSkipTranslation = 0;
        private static long _profGetFont = 0;
        private static long _profFontOps = 0;
        private static long _profTranslate = 0;
        private static long _profFontScale = 0;
        private static long _profTotal = 0;
        private static float _profLastLog = 0f;

        private static void ProcessTextPatchPrefix(object __instance, ref string textValue, string componentType)
        {
            if (string.IsNullOrEmpty(textValue)) return;

            // Early exit: translations globally disabled → zero overhead
            if (!TranslatorCore.Config.enable_translations) return;

            bool profiling = TranslatorCore.DebugMode;
            long t0 = 0, t1 = 0, t2 = 0, t3 = 0, t4 = 0, t5 = 0;
            if (profiling) _profSw.Restart();

            try
            {
                var comp = __instance as Component;
                if (comp == null) return;

                if (profiling) t0 = _profSw.ElapsedTicks;

                // Skip if part of our own UI and should not be translated (uses hierarchy check)
                if (TranslatorCore.ShouldSkipTranslation(comp)) return;

                if (profiling) { t1 = _profSw.ElapsedTicks; _profSkipTranslation += t1 - t0; }

                int compId = TypeHelper.GetInstanceID(__instance);
                string fontName = null;
                string settingsFontName = null;
                object fontObj = null;
                Font unityCloneFont = null;  // Track the clone applied to this component
                string unityCloneFallback = null;

                // Get font name (cached to avoid GetFont reflection on every call)
                if (compId != -1 && _fontNameCache.TryGetValue(compId, out string cachedFontName))
                {
                    fontName = cachedFontName;
                }
                else
                {
                    fontObj = TypeHelper.GetFont(__instance);
                    if (fontObj != null)
                        fontName = (fontObj is UnityEngine.Object uobj) ? uobj.name : null;

                    // If the component already has a clone font (inherited from template/pool),
                    // resolve back to the ORIGINAL font name so tracking stays correct
                    Font f = fontObj as Font;
                    if (f == null) f = TypeHelper.Il2CppCast(fontObj, typeof(Font)) as Font;
                    if (f != null)
                    {
                        string resolvedOriginal = FontManager.GetOriginalFontNameForClone(f);
                        if (resolvedOriginal != null)
                        {
                            fontName = resolvedOriginal;
                            if (compId != -1)
                                _inheritedCloneComponents.Add(compId);
                        }
                    }
                    if (compId != -1)
                    {
                        _fontNameCache[compId] = fontName;
                        _patchedComponentRefs[compId] = __instance;
                    }
                }

                if (profiling) { t2 = _profSw.ElapsedTicks; _profGetFont += t2 - t1; }

                if (!string.IsNullOrEmpty(fontName))
                {
                    settingsFontName = FontManager.GetSettingsFontName(compId, fontName);

                    FontManager.RegisterFontByName(settingsFontName, componentType);
                    FontManager.IncrementUsageCount(settingsFontName);

                    // Skip translation if disabled for this font
                    if (!FontManager.IsTranslationEnabled(settingsFontName))
                        return;

                    // Font replacement operations
                    if (componentType == "TMP")
                    {
                        if (fontObj == null) fontObj = TypeHelper.GetFont(__instance);

                        // TMProOld: use fallback approach (add custom font to game font's fallback list)
                        // TMProOld can't render manually-created TMP_FontAssets via SetFont,
                        // but it CAN use them as fallback fonts for missing characters.
                        // Modern TMP: use direct replacement (SetFont) for full font swap.
                        if (TypeHelper.UseAlternateTMP)
                        {
                            FontManager.EnsureFallbackApplied(fontObj, settingsFontName);
                        }
                        else
                        {
                            FontManager.ApplyFontReplacement(__instance, fontObj, settingsFontName);
                        }
                    }
                    else if (componentType == "Unity")
                    {
                        if (fontObj == null) fontObj = TypeHelper.GetFont(__instance);
                        FontManager.RegisterUnityFontObject(settingsFontName, fontObj);

                        var replacementFont = FontManager.GetUnityReplacementFont(settingsFontName);
                        if (replacementFont != null)
                        {
                            FontManager.TrackOriginalFont(compId, fontObj, __instance);
                            string currentName = (fontObj is UnityEngine.Object co) ? co.name : null;
                            string replaceName = replacementFont.name;
                            if (currentName != replaceName)
                            {
                                TypeHelper.SetFont(__instance, replacementFont);
                                FontManager.PreWarmCloneAtlas(replaceName, replacementFont);
                            }
                            // Remember the clone for EnsureCharsInCloneAtlasDirect later
                            // (GetFont may not reflect SetFont yet on IL2CPP)
                            unityCloneFont = replacementFont;
                            unityCloneFallback = replaceName;
                        }
                    }
                }

                if (profiling) { t3 = _profSw.ElapsedTicks; _profFontOps += t3 - t2; }

                // Don't translate InputField textComponent (user's typed text)
                if (componentType != "TextMesh" && IsInputFieldTextComponentCached(__instance)) return;

                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(comp);
                string preTranslateText = textValue;
                textValue = TranslatorCore.TranslateTextWithTracking(textValue, comp, isOwnUI);

                // Ensure chars are in the clone's atlas — always when a clone is active.
                // Uses the known clone directly because on IL2CPP, GetFont() may still return
                // the OLD font after SetFont (deferred update), causing EnsureCharsInCloneAtlas to skip.
                // Must run even when text didn't change: the clone may be new with an empty atlas
                // but the text is already translated from cache.
                if (unityCloneFont != null && !string.IsNullOrEmpty(textValue))
                {
                    FontManager.EnsureCharsInCloneAtlasDirect(textValue, unityCloneFont, unityCloneFallback);
                }

                if (profiling) { t4 = _profSw.ElapsedTicks; _profTranslate += t4 - t3; }

                // Apply font scale if configured for this font
                ApplyFontScale(__instance, settingsFontName ?? fontName);

                if (profiling)
                {
                    t5 = _profSw.ElapsedTicks;
                    _profFontScale += t5 - t4;
                    _profTotal += t5;
                    _profCallCount++;

                    // Log profiling summary every 5 seconds
                    float now = Time.realtimeSinceStartup;
                    if (now - _profLastLog > 5f)
                    {
                        _profLastLog = now;
                        double freq = System.Diagnostics.Stopwatch.Frequency;
                        TranslatorCore.LogInfo($"[PERF] {_profCallCount} calls in 5s | " +
                            $"ShouldSkip={_profSkipTranslation/freq*1000:F1}ms | " +
                            $"GetFont={_profGetFont/freq*1000:F1}ms | " +
                            $"FontOps={_profFontOps/freq*1000:F1}ms | " +
                            $"Translate={_profTranslate/freq*1000:F1}ms | " +
                            $"Scale={_profFontScale/freq*1000:F1}ms | " +
                            $"TOTAL={_profTotal/freq*1000:F1}ms");
                        _profCallCount = 0;
                        _profSkipTranslation = 0;
                        _profGetFont = 0;
                        _profFontOps = 0;
                        _profTranslate = 0;
                        _profFontScale = 0;
                        _profTotal = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[Patches] ProcessTextPatchPrefix error ({componentType}): {ex.Message}\n{ex.StackTrace}");
            }
        }

        public static void TMPText_SetText_Prefix(object __instance, ref string value)
        {
            ProcessTextPatchPrefix(__instance, ref value, "TMP");
        }

        public static void TMPText_SetTextMethod_Prefix(object __instance, ref string __0)
        {
            ProcessTextPatchPrefix(__instance, ref __0, "TMP");
        }

        /// <summary>
        /// Prefix for TMP_Text.fontSize setter.
        /// Intercepts fontSize changes to apply font scale immediately.
        /// This ensures the scale is applied even when the game sets fontSize AFTER set_text.
        /// </summary>
        [ThreadStatic] private static bool _bypassFontSizePrefix;
        // Components that inherited a clone font from template — skip ApplyFontScale (already scaled)
        private static readonly HashSet<int> _inheritedCloneComponents = new HashSet<int>();
        private static int _onEnableLogCount = 0;
        private static int _onEnableFireCount = 0;
        public static bool BypassFontSizePrefix { get => _bypassFontSizePrefix; set => _bypassFontSizePrefix = value; }

        public static void TMPText_SetFontSize_Prefix(object __instance, ref float value)
        {
            if (_bypassFontSizePrefix) return;
            if (__instance == null) return;

            try
            {
                int instanceId = TypeHelper.GetInstanceID(__instance);
                if (instanceId == -1) return;

                // Skip components we've never seen (mod's own UI, etc.)
                if (!_fontNameCache.ContainsKey(instanceId)) return;

                // Get the font name for this component
                string fontName = null;
                if (_fontNameCache.TryGetValue(instanceId, out string cached))
                    fontName = cached;
                else
                {
                    var fontObj = TypeHelper.GetFont(__instance);
                    if (fontObj is UnityEngine.Object uobj)
                        fontName = uobj.name;
                }

                if (string.IsNullOrEmpty(fontName)) return;

                string settingsFontName = FontManager.GetSettingsFontName(instanceId, fontName);
                float scale = FontManager.GetFontScale(settingsFontName);
                if (Math.Abs(scale - 1.0f) < 0.001f) return;

                // Store the incoming value as the true original size
                _trueOriginalFontSizes[instanceId] = value;
                _originalFontSizes[instanceId] = value;

                // Apply scale to the incoming value
                value = value * scale;
            }
            catch { }
        }

        /// <summary>
        /// Prefix for UI.Text.fontSize setter (Mono only — IL2CPP causes atlas corruption).
        /// </summary>
        public static void UIText_SetFontSize_Prefix(object __instance, ref int value)
        {
            if (_bypassFontSizePrefix) return;
            if (__instance == null) return;
            try
            {
                int instanceId = TypeHelper.GetInstanceID(__instance);
                if (instanceId == -1) return;
                if (!_fontNameCache.ContainsKey(instanceId)) return;
                string fontName;
                if (!_fontNameCache.TryGetValue(instanceId, out fontName)) return;
                if (string.IsNullOrEmpty(fontName)) return;
                string settingsFontName = FontManager.GetSettingsFontName(instanceId, fontName);
                float scale = FontManager.GetFontScale(settingsFontName);
                if (Math.Abs(scale - 1.0f) < 0.001f) return;
                _trueOriginalFontSizes[instanceId] = value;
                _originalFontSizes[instanceId] = value;
                value = (int)(value * scale);
            }
            catch { }
        }

        public static void UIText_SetText_Prefix(object __instance, ref string value)
        {
            ProcessTextPatchPrefix(__instance, ref value, "Unity");
        }

        public static void TextMesh_SetText_Prefix(object __instance, ref string value)
        {
            ProcessTextPatchPrefix(__instance, ref value, "TextMesh");
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

        // Cached PropertyInfo for alternate TMP font access (avoids GetProperty reflection on every call)
        private static readonly Dictionary<Type, PropertyInfo> _altTMPFontPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _altTMPFontSizePropCache = new Dictionary<Type, PropertyInfo>();

        /// <summary>
        /// Try to get font name from an alternate TMP component via reflection.
        /// PropertyInfo is cached per type to avoid repeated reflection.
        /// </summary>
        private static string TryGetAlternateTMPFontName(object instance)
        {
            if (instance == null) return null;

            try
            {
                var type = instance.GetType();

                PropertyInfo fontProp;
                if (!_altTMPFontPropCache.TryGetValue(type, out fontProp))
                {
                    fontProp = type.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                    _altTMPFontPropCache[type] = fontProp;
                }

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

        // Flag to register callback only once
        private static bool _initCallbackRegistered = false;

        // Pending font replacements: components that need font change but were encountered before init
        // Key: instance hashcode, Value: (WeakReference to instance, original font name, original English text)
        // We store the English text so we can replay the full set_text flow after init
        private static Dictionary<int, (WeakReference instance, string fontName, string originalText)> _pendingFontReplacements = new Dictionary<int, (WeakReference, string, string)>();

        /// <summary>
        /// Register a callback for when UI is initialized.
        /// </summary>
        private static void RegisterInitCallback()
        {
            if (_initCallbackRegistered) return;
            _initCallbackRegistered = true;

            UI.TranslatorUIManager.OnInitialized += OnUIInitialized;
            TranslatorCore.LogInfo("[AlternateTMP] Registered init callback for pending font replacements");
        }

        /// <summary>
        /// Called when UniverseLib UI is fully initialized.
        /// Schedule delayed replay for pending components to ensure Unity state is stable.
        /// </summary>
        private static void OnUIInitialized()
        {
            TranslatorCore.LogInfo($"[AlternateTMP] UI initialized - scheduling {_pendingFontReplacements.Count} pending text operations");

            // Collect pending items
            var toProcess = new List<(object instance, string fontName, string originalText)>();
            foreach (var kvp in _pendingFontReplacements)
            {
                var weakRef = kvp.Value.instance;
                var fontName = kvp.Value.fontName;
                var originalText = kvp.Value.originalText;
                if (weakRef.IsAlive && weakRef.Target != null)
                {
                    toProcess.Add((weakRef.Target, fontName, originalText));
                }
            }
            _pendingFontReplacements.Clear();

            if (toProcess.Count == 0) return;

            // Use RunDelayed to wait a few frames for Unity to stabilize
            // This is critical: applying font immediately after init often fails
            UI.TranslatorUIManager.RunDelayed(0.1f, () => ProcessPendingFontReplacements(toProcess));
        }

        /// <summary>
        /// Process pending font replacements after a delay.
        /// Applies font and triggers translation for each queued component.
        /// </summary>
        private static void ProcessPendingFontReplacements(List<(object instance, string fontName, string originalText)> toProcess)
        {
            TranslatorCore.LogInfo($"[AlternateTMP] Processing {toProcess.Count} pending font replacements");

            foreach (var (instance, fontName, originalText) in toProcess)
            {
                try
                {
                    // Check if instance is still valid
                    var component = instance as Component;
                    if (component == null || component.gameObject == null)
                    {
                        TranslatorCore.LogWarning($"[AlternateTMP] Component no longer valid, skipping");
                        continue;
                    }

                    TranslatorCore.LogInfo($"[AlternateTMP] Processing: '{(originalText.Length > 40 ? originalText.Substring(0, 40) + "..." : originalText)}' with font '{fontName}'");

                    // Step 1: Apply font replacement
                    TryApplyAlternateTMPReplacementFont(instance, fontName);

                    // Step 2: Trigger set_text with original text
                    // Our prefix will now translate it (UI is ready, font was just applied)
                    var textProp = instance.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    if (textProp != null && !string.IsNullOrEmpty(originalText))
                    {
                        textProp.SetValue(instance, originalText, null);

                        // Check result
                        var resultText = textProp.GetValue(instance, null) as string ?? "(null)";
                        TranslatorCore.LogInfo($"[AlternateTMP] After processing, text is: '{(resultText.Length > 40 ? resultText.Substring(0, 40) + "..." : resultText)}'");
                    }
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogWarning($"[AlternateTMP] Failed to process pending: {ex.Message}");
                }
            }

            TranslatorCore.LogInfo($"[AlternateTMP] Completed processing {toProcess.Count} pending text operations");
        }

        /// <summary>
        /// Try to find and apply a replacement font for an alternate TMP component.
        /// Since TMProOld.TMP_FontAsset != TMPro.TMP_FontAsset, we search for fonts already loaded in the game.
        /// Also applies font scale if configured.
        /// NOTE: This should only be called when UI is initialized (caller must check).
        /// </summary>
        // Cache of components already font-replaced (avoids redundant reflection per set_text)
        private static readonly HashSet<int> _altTMPFontReplacedIds = new HashSet<int>();

        private static void TryApplyAlternateTMPReplacementFont(object instance, string originalFontName)
        {
            if (instance == null || string.IsNullOrEmpty(originalFontName)) return;

            // Early exit: check if fallback is even configured before any reflection
            string fallbackName = FontManager.GetConfiguredFallback(originalFontName);
            bool hasFallback = !string.IsNullOrEmpty(fallbackName);

            // Early exit: if no fallback and font search already done, nothing to do
            if (!hasFallback && _alternateTMPFontSearchDone)
                return;

            try
            {
                var type = instance.GetType();
                int instId = TypeHelper.GetInstanceID(instance);

                // Apply font scale if configured (use cached PropertyInfo)
                float scale = FontManager.GetFontScale(originalFontName);
                if (Math.Abs(scale - 1.0f) > 0.01f)
                {
                    PropertyInfo fontSizeProp;
                    if (!_altTMPFontSizePropCache.TryGetValue(type, out fontSizeProp))
                    {
                        fontSizeProp = type.GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance);
                        _altTMPFontSizePropCache[type] = fontSizeProp;
                    }

                    if (fontSizeProp != null && fontSizeProp.CanRead && fontSizeProp.CanWrite)
                    {
                        var currentSize = fontSizeProp.GetValue(instance, null);
                        if (currentSize is float floatSize)
                        {
                            string instanceKey = instId.ToString();
                            if (!_alternateTMPOriginalSizes.TryGetValue(instanceKey, out float originalSize))
                            {
                                originalSize = floatSize;
                                _alternateTMPOriginalSizes[instanceKey] = originalSize;
                            }
                            float newSize = originalSize * scale;
                            if (Math.Abs(floatSize - newSize) > 0.1f)
                                fontSizeProp.SetValue(instance, newSize, null);
                        }
                    }
                }

                // Get font property (cached) and do one-time font search
                PropertyInfo fontProp;
                if (!_altTMPFontPropCache.TryGetValue(type, out fontProp))
                {
                    fontProp = type.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                    _altTMPFontPropCache[type] = fontProp;
                }

                if (fontProp != null && !_alternateTMPFontSearchDone)
                {
                    var currentFont = fontProp.GetValue(instance, null);
                    if (currentFont != null)
                    {
                        Type fontAssetType = currentFont.GetType();
                        _alternateTMPFontAssetType = fontAssetType;
                        SearchAlternateTMPFonts(fontAssetType);
                        _alternateTMPFontSearchDone = true;
                    }
                }

                if (!hasFallback) return;

                // Skip if already replaced for this component (avoid redundant reflection every set_text)
                if (instId != -1 && _altTMPFontReplacedIds.Contains(instId))
                    return;

                if (fontProp == null)
                {
                    TranslatorCore.LogWarning($"[AlternateTMP] No 'font' property found on {type.Name}");
                    return;
                }

                // Get current (original) font before replacement
                var originalFont = fontProp.GetValue(instance, null);

                // Resolve the replacement font asset
                object replacementAsset = null;

                if (FontManager.IsCustomFont(fallbackName))
                {
                    string customFontName = fallbackName;
                    if (fallbackName.StartsWith("[Custom] "))
                        customFontName = fallbackName.Substring(9);

                    replacementAsset = CustomFontLoader.LoadCustomFont(customFontName);
                    if (replacementAsset == null)
                    {
                        TranslatorCore.LogWarning($"[AlternateTMP] Failed to load custom font '{customFontName}'");
                        return;
                    }
                }
                else
                {
                    // Try exact match in game font cache
                    if (_alternateTMPFontCache.TryGetValue(fallbackName, out object cachedFont))
                    {
                        replacementAsset = cachedFont;
                    }
                    else
                    {
                        // Try partial match
                        foreach (var kvp in _alternateTMPFontCache)
                        {
                            if (kvp.Key.IndexOf(fallbackName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                fallbackName.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                replacementAsset = kvp.Value;
                                break;
                            }
                        }
                    }

                    if (replacementAsset == null)
                    {
                        TranslatorCore.LogWarning($"[AlternateTMP] Fallback font '{fallbackName}' not found in game fonts");
                        return;
                    }
                }

                // Check if already replaced with the same font (avoid redundant work)
                string currentFontName = (originalFont is UnityEngine.Object curObj) ? curObj.name : null;
                string replacementName = (replacementAsset is UnityEngine.Object repObj) ? repObj.name : null;
                if (currentFontName == replacementName && !string.IsNullOrEmpty(currentFontName)) return;

                // Store original font and component ref for restore
                if (instId != -1 && originalFont != null)
                    FontManager.TrackOriginalFont(instId, originalFont, instance);

                // Replace the font: replacement becomes PRIMARY
                fontProp.SetValue(instance, replacementAsset, null);

                // Mark as replaced to skip redundant work on next set_text
                if (instId != -1)
                    _altTMPFontReplacedIds.Add(instId);

                // Set material to match the replacement font
                try
                {
                    var materialField = replacementAsset.GetType().GetField("material", BindingFlags.Public | BindingFlags.Instance);
                    if (materialField != null)
                    {
                        var fontMaterial = materialField.GetValue(replacementAsset) as Material;
                        if (fontMaterial != null)
                        {
                            var fontSharedMatProp = instance.GetType().GetProperty("fontSharedMaterial", BindingFlags.Public | BindingFlags.Instance);
                            if (fontSharedMatProp != null && fontSharedMatProp.CanWrite)
                                fontSharedMatProp.SetValue(instance, fontMaterial, null);
                        }
                    }
                }
                catch { }

                // Add original game font as FALLBACK on the replacement
                // (so missing chars in replacement fall back to original)
                if (originalFont != null)
                {
                    TryAddFallbackFont(replacementAsset, originalFont);
                }

                // Force mesh regeneration
                try
                {
                    var forceMeshUpdate = instance.GetType().GetMethod("ForceMeshUpdate",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    forceMeshUpdate?.Invoke(instance, null);
                }
                catch { }
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
                        meshUpdateCalled = true;
                    }
                    catch (Exception)
                    {
                        // Expected for components not fully initialized yet
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
                            meshUpdateCalled = true;
                        }
                        catch (Exception)
                        {
                            // Expected for components not fully initialized yet
                        }
                    }
                }

                if (meshUpdateCalled) return;

                // ForceMeshUpdate failed - schedule a retry for later when component is ready
                if (retryCount < 5)
                {
                    ScheduleDelayedMeshUpdate(instance, type, retryCount + 1);
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
            if (!TranslatorCore.Config.enable_translations) return;
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

                        // Check if this font needs replacement (has a fallback configured)
                        string fallback = FontManager.GetConfiguredFallback(fontName);
                        bool needsFontReplacement = !string.IsNullOrEmpty(fallback);

                        // If UI not ready yet, queue for later processing
                        // DON'T apply font here - it will be reset by the game before we can replay
                        // We queue the component and will apply font + translation together after init
                        if (needsFontReplacement && !UI.TranslatorUIManager.IsInitialized)
                        {
                            RegisterInitCallback();
                            if (!_pendingFontReplacements.ContainsKey(instanceId))
                            {
                                _pendingFontReplacements[instanceId] = (new WeakReference(__instance), fontName, __0);
                                TranslatorCore.LogInfo($"[AlternateTMP] Queued for font+translation after init: '{fontName}'");
                            }
                            // Skip font and translation - let original set_text run with English
                            // We'll do everything after UI init
                            return;
                        }

                        // UI is ready, apply font replacement now
                        if (needsFontReplacement)
                        {
                            TryApplyAlternateTMPReplacementFont(__instance, fontName);
                        }
                    }
                }

                // Check if own UI (use UI-specific prompt)
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(component);
                __0 = TranslatorCore.TranslateTextWithTracking(__0, component, isOwnUI);
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[AlternateTMP] Prefix exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix for alternate TMP text getter.
        /// Only applies font replacement when text is read (handles late font initialization).
        /// NOTE: Does NOT translate - translation happens in setter prefix only.
        /// Translating here would cause re-translation of already-translated text.
        /// </summary>
        public static void AlternateTMP_GetText_Postfix(object __instance, ref string __result)
        {
            // PERF: Getter postfix kept minimal — no font replacement here.
            // Font replacement is handled by the setter prefix (AlternateTMP_SetText_Prefix).
            // Getters fire very frequently (layout, localization systems reading values)
            // and the previous font replacement + reflection calls here were a major perf drain.
            // Translation also doesn't happen here (would re-translate already-translated text).
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
            if (!TranslatorCore.Config.enable_translations) return;
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
            if (!TranslatorCore.Config.enable_translations) return;
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
