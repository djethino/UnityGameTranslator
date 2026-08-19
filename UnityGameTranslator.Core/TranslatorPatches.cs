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
        // Anti-cumulation reference: survives ClearFontSizeCache, which _originalFontSizes does not.
        // Dropped per dead id in CleanDeadRefs only — a destroyed component can no longer cumulate.
        // (This said "never cleared", which CleanDeadRefs has always contradicted.)
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

                    // TMP_Text.font setter — games re-assign fonts at any time (menu
                    // animations, presets, localization systems). The postfix re-applies
                    // our replacement when that happens: without it, the fast-path marker
                    // keeps a reverted component ignored forever (issue #21: menu items
                    // stayed in the original font after the intro animation), and
                    // components that get their font assigned late are covered the moment
                    // it happens instead of waiting for a refresh pass.
                    var fontAssetProp = TypeHelper.TMP_TextType.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                    if (fontAssetProp?.SetMethod != null)
                    {
                        var postfix = typeof(TranslatorPatches).GetMethod(nameof(TMPText_SetFont_Postfix), BindingFlags.Static | BindingFlags.Public);
                        patcher(fontAssetProp.SetMethod, null, postfix);
                        patchCount++;
                    }

                    TranslatorCore.LogDebug($"[Patches] TMP_Text patches applied ({TypeHelper.TMP_TextType.FullName})");
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

                // Image replacement patches — intercept sprite/texture assignments
                patchCount += PatchImageComponents(patcher);

                // UI Toolkit — a whole framework whose text is not a Component and which none of
                // the above can reach. One setter covers all of it; see UIToolkitSupport.
                UIToolkitSupport.Initialize();
                patchCount += UIToolkitSupport.ApplyPatches(patcher);
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"Failed to apply patches: {e.Message}");
            }

            return patchCount;
        }

        #region Image Replacement Patches

        private static int PatchImageComponents(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int count = 0;

            try
            {
                ImageReplacer.ResolveTypes();

                // Patch Image.sprite setter
                var imageType = ImageReplacer.ImageType;
                if (imageType != null)
                {
                    var spriteProp = imageType.GetProperty("sprite", BindingFlags.Public | BindingFlags.Instance);
                    if (spriteProp != null && spriteProp.SetMethod != null)
                    {
                        var prefix = typeof(TranslatorPatches).GetMethod(nameof(Image_SetSprite_Prefix),
                            BindingFlags.Static | BindingFlags.Public);
                        if (prefix != null)
                        {
                            patcher(spriteProp.SetMethod, prefix, null);
                            count++;
                            TranslatorCore.LogInfo("[Patches] Patched Image.sprite setter");
                        }
                    }
                }

                // Patch RawImage.texture setter
                var rawImageType = ImageReplacer.RawImageType;
                if (rawImageType != null)
                {
                    var textureProp = rawImageType.GetProperty("texture", BindingFlags.Public | BindingFlags.Instance);
                    if (textureProp != null && textureProp.SetMethod != null)
                    {
                        var prefix = typeof(TranslatorPatches).GetMethod(nameof(RawImage_SetTexture_Prefix),
                            BindingFlags.Static | BindingFlags.Public);
                        if (prefix != null)
                        {
                            patcher(textureProp.SetMethod, prefix, null);
                            count++;
                            TranslatorCore.LogInfo("[Patches] Patched RawImage.texture setter");
                        }
                    }
                }

                // Patch SpriteRenderer.sprite setter
                var spriteRendType = ImageReplacer.SpriteRendererType;
                if (spriteRendType != null)
                {
                    var spriteProp = spriteRendType.GetProperty("sprite", BindingFlags.Public | BindingFlags.Instance);
                    if (spriteProp != null && spriteProp.SetMethod != null)
                    {
                        var prefix = typeof(TranslatorPatches).GetMethod(nameof(SpriteRenderer_SetSprite_Prefix),
                            BindingFlags.Static | BindingFlags.Public);
                        if (prefix != null)
                        {
                            patcher(spriteProp.SetMethod, prefix, null);
                            count++;
                            TranslatorCore.LogInfo("[Patches] Patched SpriteRenderer.sprite setter");
                        }
                    }
                }

                if (count > 0)
                    TranslatorCore.LogInfo($"[Patches] Applied {count} image replacement patches");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Patches] Failed to apply image patches: {ex.Message}");
            }

            return count;
        }

        /// <summary>
        /// Prefix for Image.sprite setter. Replaces the sprite value if a replacement is loaded.
        /// Uses 'object' type for IL2CPP compatibility (avoids type mismatch with Il2CppSprite).
        /// </summary>
        public static void Image_SetSprite_Prefix(object __instance, ref object __0)
        {
            if (__0 == null) return;
            try
            {
                var name = ImageReplacer.GetSpriteName(__0);
                if (name == null) return;
                var replacement = ImageReplacer.GetReplacement(name);
                if (replacement != null)
                {
                    TranslatorCore.LogDebug($"[ImagePatch] Replacing sprite \"{name}\"");
                    __0 = replacement;
                }
            }
            catch { }
        }

        /// <summary>
        /// Prefix for RawImage.texture setter.
        /// </summary>
        public static void RawImage_SetTexture_Prefix(object __instance, ref object __0)
        {
            if (__0 == null) return;
            try
            {
                var name = ImageReplacer.GetSpriteName(__0);
                if (name == null) return;
                var replacement = ImageReplacer.GetReplacement(name);
                if (replacement != null && replacement.texture != null) __0 = replacement.texture;
            }
            catch { }
        }

        /// <summary>
        /// Prefix for SpriteRenderer.sprite setter.
        /// </summary>
        public static void SpriteRenderer_SetSprite_Prefix(object __instance, ref object __0)
        {
            if (__0 == null) return;
            try
            {
                var name = ImageReplacer.GetSpriteName(__0);
                if (name == null) return;
                var replacement = ImageReplacer.GetReplacement(name);
                if (replacement != null) __0 = replacement;
            }
            catch { }
        }

        #endregion

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
        };

        // Middleware namespaces whose text components never display game text.
        // Rewired: its internal GUIText backs a debug overlay, and its getter can
        // run on Rewired's input thread → native crash on IL2CPP (GitHub issue #15).
        private static readonly string[] GenericExcludedNamespaces = { "Rewired" };

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

                            // Skip middleware namespaces (strip interop prefix for matching)
                            string ns = type.Namespace ?? "";
                            if (ns.StartsWith("Il2Cpp")) ns = ns.Substring(6);
                            bool inExcludedNamespace = false;
                            foreach (var excludedNs in GenericExcludedNamespaces)
                            {
                                if (ns == excludedNs || ns.StartsWith(excludedNs + "."))
                                {
                                    inExcludedNamespace = true;
                                    break;
                                }
                            }
                            if (inExcludedNamespace) continue;

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
                            TranslatorCore.LogDebug($"[Patches] Detected generic text type: {type.FullName} ({framework})" +
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
                    TranslatorCore.LogDebug($"[Patches] Patched {typeInfo.Category}: {typeInfo.ComponentType.Name}.set_text");
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

        // Generic text types already reported as accessed off the main thread (log once per type)
        private static readonly HashSet<string> _offThreadLoggedTypes = new HashSet<string>();

        /// <summary>
        /// Returns true (and logs once per type) when called off the Unity main thread.
        /// Generic getters/setters can be invoked from middleware background threads
        /// (e.g. Rewired's input thread); the Unity APIs used below are main-thread
        /// only and crash natively on IL2CPP instead of throwing.
        /// </summary>
        private static bool SkipOffMainThread(object instance)
        {
            if (TranslatorCore.IsMainThread) return false;

            string typeName = instance?.GetType().FullName ?? "?";
            bool firstTime;
            lock (_offThreadLoggedTypes)
                firstTime = _offThreadLoggedTypes.Add(typeName);
            if (firstTime)
                TranslatorCore.LogDebug($"[Patches] {typeName}.text accessed off the main thread — translation skipped");
            return true;
        }

        /// <summary>
        /// Prefix for generically detected text components (NGUI UILabel, etc.)
        /// </summary>
        public static void GenericText_SetText_Prefix(object __instance, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (BypassTextPrefix) return;
            if (!TranslatorCore.TranslationsActive) return;
            if (SkipOffMainThread(__instance)) return;
            try
            {
                var component = __instance as Component;
                if (component == null) return;
                if (TranslatorCore.ShouldSkipTranslation(component)) return;

                // Find the matching type info for font handling
                var typeInfo = FindTypeInfoForInstance(__instance);

                string fontName = null;
                string settingsFontName = null;

                // Own UI reaches here only when translate_mod_ui is ON. Its font must never
                // enter the game font pipeline (managed separately as the interface font), so
                // skip font detection/registration for own UI while still translating it.
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(component);

                // Get font name if available
                if (typeInfo?.FontProp != null && !isOwnUI)
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
                string preVal = value;
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
            if (!TranslatorCore.TranslationsActive) return;
            if (SkipOffMainThread(__instance)) return;
            try
            {
                var component = __instance as Component;
                if (component == null) return;
                if (TranslatorCore.ShouldSkipTranslation(component)) return;

                var typeInfo = FindTypeInfoForInstance(__instance);

                // Own UI reaches here only when translate_mod_ui is ON. Keep its font out of
                // the game font pipeline (interface font is managed separately); still translate.
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(component);

                // Check font-based enable/disable
                if (typeInfo?.FontProp != null && !isOwnUI)
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
        /// <summary>
        /// The mod's own labels are never sized by a game font's settings.
        ///
        /// They can share a Unity font with the game — Arial is the usual one — and when the
        /// mod's interface is being translated they are tracked by the same caches as game text.
        /// Nothing else told the two apart on the SIZE path, so moving the slider for a game
        /// font resized the mod's own windows with it. The font-replacement path has guarded
        /// against exactly this from the start; this is the same guard, on the other path.
        ///
        /// The mod's interface has its own sizes (UIStyles) and its own scaling; a game's
        /// settings have no say over them.
        /// </summary>
        private static bool IsOwnUIText(object instance)
        {
            return instance is Component c && TranslatorCore.IsOwnUI(c);
        }

        private static void ApplyGenericFontScale(object instance, RegisteredTextType typeInfo, string fontName)
        {
            if (IsOwnUIText(instance)) return;
            if (typeInfo.FontSizeProp == null || string.IsNullOrEmpty(fontName)) return;

            int instanceId = TypeHelper.GetInstanceID(instance);
            float scale = FontManager.GetFontScale(fontName, instanceId);
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

                            // The "text" property MUST return string. Some custom game scripts reuse the
                            // name "text" with a non-string return type (e.g. a TMP_Text reference instead),
                            // which would cause Harmony to fail patching get_text with a string __result.
                            if (textProp.PropertyType != typeof(string)) continue;

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
                TranslatorCore.LogDebug($"[Patches] Patched alternate TMP: {altTmpType.FullName} ({count} methods)");
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
                TranslatorCore.LogDebug($"[Patches] Patched {count} tk2dTextMesh methods");
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
                TranslatorCore.LogDebug($"[Patches] Found localization bridge: {bridgeType.FullName} ({count} methods patched)");
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
                var tmpComp = TypeHelper.GetComponentByType(go, TypeHelper.TMP_TextType);
                if (tmpComp != null)
                {
                    return CreateReflectionTextComponentInfo(tmpComp, "TMP");
                }
            }

            // Try UI.Text
            if (TypeHelper.UI_TextType != null)
            {
                var uiComp = TypeHelper.GetComponentByType(go, TypeHelper.UI_TextType);
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
                TranslatorCore.LogDebug($"[Patches] Found custom localization: {locType.FullName} ({count} methods patched)");
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

        // Cache for InputField textComponent exclusion (avoids repeated parent walks)
        // Key: instanceId, Value: true if this is an InputField's textComponent (should be excluded)
        private static readonly System.Collections.Generic.Dictionary<int, bool> inputFieldTextCache =
            new System.Collections.Generic.Dictionary<int, bool>();

        // Parent InputField per text component (walk once per component).
        // Value null = no InputField ancestor (stable: hierarchies almost never reparent).
        private static readonly System.Collections.Generic.Dictionary<int, object> _parentInputFieldCache =
            new System.Collections.Generic.Dictionary<int, object>();

        private static object GetParentInputFieldCached(object textComponent)
        {
            int id = TypeHelper.GetInstanceID(textComponent);
            if (id == -1) return null;

            if (_parentInputFieldCache.TryGetValue(id, out object cached))
            {
                if (cached == null || TypeHelper.IsUnityObjectAlive(cached)) return cached;
                _parentInputFieldCache.Remove(id); // stale wrapper (scene unload)
            }

            object input = TypeHelper.FindParentInputField(textComponent as Component);
            _parentInputFieldCache[id] = input;
            return input;
        }

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

            object input = GetParentInputFieldCached(textComponent);
            if (input == null)
            {
                inputFieldTextCache[id] = false; // stable: no InputField ancestor
                return false;
            }

            bool result = TypeHelper.IsTextComponentOfInputField(input, textComponent);
            // Positives are stable; negatives under an InputField are NOT cached —
            // the game may wire textComponent after the first set_text
            if (result) inputFieldTextCache[id] = true;
            return result;
        }

        // === USER INPUT MIRROR PROTECTION ===
        // Games echo what the user types into display texts beyond the official
        // textComponent: a styled copy inside the input widget (e.g. seed shown in a
        // color-tagged TMP_Text next to the real one) or a live preview elsewhere
        // (character name in a header). Typed text must never be translated NOR
        // queued — and these checks must run BEFORE cache lookup: input matching an
        // existing cache key would otherwise be replaced by its translation while
        // typing.
        private const float InputBlurGraceSeconds = 2f;
        private const int MirrorCaseInsensitiveMinLength = 4;

        private static int _focusReadFrame = -1;
        private static string _focusedInputTextThisFrame;
        private static string _lastFocusedInputText;
        private static float _lastFocusedInputTime = -999f;

        /// <summary>
        /// True when a text is the user's own typed input echoed by the game:
        /// 1) internal mirror — component inside an InputField whose content equals
        ///    THAT input's text (structural, no dependency on focus or timing), or
        /// 2) external mirror — content equals the focused input field's text, with a
        ///    short grace period after the field loses focus (blur re-renders).
        /// Also used by the scanner (transient skip, never a permanent exclusion).
        /// </summary>
        public static bool IsUserInputMirror(object component, string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            string candidate = TranslatorCore.StripMarkupTags(text).Trim();
            if (candidate.Length == 0) return false;

            // Internal mirror
            if (component is Component)
            {
                object parentInput = GetParentInputFieldCached(component);
                if (parentInput != null && MatchesTypedText(candidate, TypeHelper.GetInputFieldText(parentInput)))
                    return true;
            }

            // External mirror (focused field, then blur grace)
            string focused = GetFocusedInputTextCached();
            if (MatchesTypedText(candidate, focused)) return true;
            if (_lastFocusedInputText != null
                && Time.realtimeSinceStartup - _lastFocusedInputTime <= InputBlurGraceSeconds
                && MatchesTypedText(candidate, _lastFocusedInputText)) return true;

            return false;
        }

        private static bool MatchesTypedText(string candidate, string inputText)
        {
            if (string.IsNullOrEmpty(inputText)) return false;
            inputText = inputText.Trim();
            if (inputText.Length == 0) return false;
            if (string.Equals(candidate, inputText, StringComparison.Ordinal)) return true;
            // Case-insensitive only for longer values (games display seeds uppercased);
            // short words stay strict so legit labels aren't skipped while typing them
            return inputText.Length >= MirrorCaseInsensitiveMinLength
                && string.Equals(candidate, inputText, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Text of the currently focused input field, read once per frame.</summary>
        private static string GetFocusedInputTextCached()
        {
            int frame = Time.frameCount;
            if (_focusReadFrame == frame) return _focusedInputTextThisFrame;
            _focusReadFrame = frame;
            _focusedInputTextThisFrame = null;

            try
            {
                var eventSystem = UnityEngine.EventSystems.EventSystem.current;
                var selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
                if (selected != null)
                {
                    object input = TypeHelper.FindParentInputField(selected.transform);
                    if (input != null)
                    {
                        string typed = TypeHelper.GetInputFieldText(input);
                        if (!string.IsNullOrEmpty(typed))
                        {
                            _focusedInputTextThisFrame = typed;
                            _lastFocusedInputText = typed;
                            _lastFocusedInputTime = Time.realtimeSinceStartup;
                        }
                    }
                }
            }
            catch { }

            return _focusedInputTextThisFrame;
        }

        // === CONCATENATION DETECTION ===
        // Games build text procedurally: text = "title"; then text = "title\nattr"; etc.
        // Each set_text contains the FULL text so far (pure source language, no FR/CN mix).
        // We detect this by tracking the raw (pre-translation) text per component.
        // When new text starts with the previous raw text → it's growing → extract delta.
        // Each delta is translated separately (cache-friendly: same parts across items).
        //
        // Concat vs Typewriting:
        // - Concat: 2+ set_text calls in the SAME frame → translate deltas immediately
        // - TW: 1 set_text per frame, text grows → defer for 500ms stabilization
        // Detection: track set_text count per frame per component.

        /// <summary>
        /// Which of the three ways a component's text is being followed.
        ///
        /// 🔴 **One field rather than two booleans**, because "the game assembles this" and "a
        /// reveal is in flight" are mutually exclusive — and that used to be kept true BY HAND at
        /// four separate places: the concat detector cancelled the reveal, the reveal detector
        /// refused to run on a concat component, the stabilizer dropped concat ids, and the
        /// input-mirror path cleared both. A state that cannot be spelled wrongly needs none of it.
        /// </summary>
        private enum TextMode
        {
            /// <summary>Nothing special: translate what arrives.</summary>
            Normal = 0,
            /// <summary>A reveal is in flight — hold the text back until it settles.</summary>
            Typewriter,
            /// <summary>The game builds this text in parts — translate each part.</summary>
            Concat,
        }

        /// <summary>
        /// Everything followed per text component, in one record.
        ///
        /// 🔴 **Nine dictionaries keyed by the same instance id used to hold this, under FIVE
        /// different cleanup policies.** Adding a tenth meant knowing which of three clear methods
        /// it belonged in, and getting it wrong was invisible: the read-back map ended up cleaned
        /// by nothing at all and kept two strings per component for the whole session, the
        /// typewriting work list kept ids whose state was gone, and the best-fit reference was
        /// never dropped. One record has ONE lifetime, so a field added later cannot be forgotten.
        ///
        /// ⚠ **Deliberately NOT in here**: `_concatAssembledCache` and `_concatTranslatedValues`,
        /// which are keyed by TEXT and not by component. They answer "have I already seen this
        /// string", a question that outlives any one component.
        /// </summary>
        private sealed class ComponentTextState
        {
            public TextMode Mode;

            // --- What the game last wrote, and what we last showed ---
            public string LastRaw;          // pre-translation, for delta computation
            public string LastTranslated;   // for display: translatedBase + translatedDelta

            // --- Procedural text (concat) ---
            public List<string> Deltas;     // ordered parts, for re-assembly when translations arrive

            // --- Frame tracking that feeds the concat decision ---
            // -1 and not 0: Time.frameCount really is 0 on the first frame, and a fresh record must
            // read as "not seen this frame" there too, exactly as an absent dictionary entry did.
            public int LastFrame = -1;
            public int FrameCallCount;

            // --- Read-back: what we translated, so an append can be reconstructed ---
            public string ReadBackSource;
            public string ReadBackTranslated;

            // --- Typewriting ---
            public string TypewritingText;
            public float TypewritingSince;
            public bool TypewritingQueued;  // already handed over; do not hand it over twice
        }

        private static readonly Dictionary<int, ComponentTextState> _componentState =
            new Dictionary<int, ComponentTextState>();

        /// <summary>
        /// Components with typewriting in flight — an INDEX over <see cref="_componentState"/>, not
        /// a second source of truth.
        ///
        /// ⚠ Kept as its own set on purpose: the stabilizer runs every frame, and walking every
        /// known component to find the two that are mid-reveal would turn a constant cost into one
        /// proportional to the whole scene. Any id in here whose mode is no longer Typewriter gets
        /// dropped by the stabilizer, so it cannot drift into a source of truth.
        /// </summary>
        private static readonly HashSet<int> _typewritingPending = new HashSet<int>();

        /// <summary>The record for this component, created on first need.</summary>
        private static ComponentTextState StateFor(int compId)
        {
            ComponentTextState state;
            if (!_componentState.TryGetValue(compId, out state))
            {
                state = new ComponentTextState();
                _componentState[compId] = state;
            }
            return state;
        }

        /// <summary>The record for this component, or null. For readers that must not create one.</summary>
        private static ComponentTextState PeekState(int compId)
        {
            if (compId == -1) return null;
            ComponentTextState state;
            return _componentState.TryGetValue(compId, out state) ? state : null;
        }

        // Runtime cache for concat-assembled texts (not saved to JSON).
        // Key = full raw source text, Value = full assembled translated text.
        // Prevents re-queuing of assembled texts on scanner refresh.
        private static readonly Dictionary<string, string> _concatAssembledCache = new Dictionary<string, string>();
        // Fast lookup for translated values (to skip target-language text that comes back)
        private static readonly HashSet<string> _concatTranslatedValues = new HashSet<string>();

        /// <summary>Check if a component is in concat mode.</summary>
        public static bool IsConcatComponent(int compId)
        {
            var state = PeekState(compId);
            return state != null && state.Mode == TextMode.Concat;
        }

        /// <summary>Look up a text in the concat assembled cache. Returns FR translation or null.</summary>
        public static string GetConcatCacheResult(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return null;
            string result;
            return _concatAssembledCache.TryGetValue(rawText, out result) ? result : null;
        }

        /// <summary>
        /// Re-assemble a concat component's text using stored deltas and current cache.
        /// Returns the assembled FR text, or null if no deltas stored.
        /// </summary>
        public static string ReassembleConcat(int compId, object component)
        {
            var state = PeekState(compId);
            List<string> deltas = state?.Deltas;
            if (deltas == null || deltas.Count == 0)
                return null;

            var result = new System.Text.StringBuilder();
            foreach (string part in deltas)
            {
                // Preserve newlines
                string leading = "", trailing = "";
                string core = part;
                while (core.Length > 0 && core[0] == '\n') { leading += "\n"; core = core.Substring(1); }
                while (core.Length > 0 && core[core.Length - 1] == '\n') { trailing = "\n" + trailing; core = core.Substring(0, core.Length - 1); }

                string translated = string.IsNullOrEmpty(core) ? "" :
                    TranslatorCore.TranslateTextWithTracking(core, component, false, skipTypewriting: true, skipQueueing: true);
                result.Append(leading);
                result.Append(translated);
                result.Append(trailing);
            }

            string assembled = result.ToString();
            // Update caches
            string rawKey = string.Join("", deltas);
            _concatAssembledCache[rawKey] = assembled;
            _concatTranslatedValues.Add(assembled);

            return assembled;
        }

        /// <summary>Invalidate a concat cache entry so it gets re-assembled with fresh translations.</summary>
        public static void InvalidateConcatCache(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string oldValue;
            if (_concatAssembledCache.TryGetValue(text, out oldValue))
            {
                _concatAssembledCache.Remove(text);
                _concatTranslatedValues.Remove(oldValue);
            }
        }

        /// <summary>Check if a text is a known concat translated value (FR result).</summary>
        public static bool IsConcatTranslatedValue(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return _concatTranslatedValues.Contains(text);
        }

        /// <summary>
        /// Getter postfix stub — kept for the Harmony patches already applied.
        /// </summary>
        public static void Text_GetText_Postfix(object __instance, ref string __result)
        {
            // No-op: getter returns the actual property value.
        }

        /// <summary>
        /// Clear the InputField cache (call on scene change).
        /// </summary>
        public static void ClearCache()
        {
            inputFieldTextCache.Clear();
            _parentInputFieldCache.Clear();
            _altTMPFontReplacedIds.Clear();
            _fontNameCache.Clear();
            _patchedComponentRefs.Clear();
            _componentState.Clear();
            _typewritingPending.Clear();
            _concatAssembledCache.Clear();
            _concatTranslatedValues.Clear();
        }

        /// <summary>
        /// Clean up dead component refs (destroyed by scene unload).
        /// Less aggressive than ClearCache — only removes dead entries.
        /// </summary>
        public static void CleanDeadRefs()
        {
            var deadIds = new List<int>();
            foreach (var kvp in _patchedComponentRefs)
            {
                if (kvp.Value == null || (kvp.Value is UnityEngine.Object uobj && uobj == null))
                    deadIds.Add(kvp.Key);
            }
            foreach (int id in deadIds)
            {
                _patchedComponentRefs.Remove(id);
                _fontNameCache.Remove(id);
                _originalFontSizes.Remove(id);
                _trueOriginalFontSizes.Remove(id);
                _originalAutoSizeMax.Remove(id);
                _originalAutoSizeMin.Remove(id);
                _inheritedCloneComponents.Remove(id);
                // Everything followed per component goes at once. ⚠ This drops a little more than
                // the separate maps used to: the frame counters, the concat deltas and the
                // typewriting state used to survive here. Dropping them is a no-op in fact — the
                // component is destroyed, so its id is never presented again and no reader can
                // reach those entries.
                _componentState.Remove(id);
                _typewritingPending.Remove(id);
                // Safe HERE and only here: the entry is the anti-cumulation reference, so it must
                // survive a live component (see its declaration).
                _originalMaxFontSizes.Remove(id);
            }
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
            _originalAutoSizeMax.Clear();
            _originalAutoSizeMin.Clear();
        }

        /// <summary>
        /// Clear the last-translated-text cache so ForceRefreshAllText re-processes
        /// all components fully (including ApplyFontScale). Call when font overrides change.
        /// </summary>
        public static void ClearLastTranslatedCache()
        {
            // Only that one field: the rest of a component's record (concat mode, deltas,
            // typewriting) is not what this method is about, and dropping it here would silently
            // reset the detection every time a font override changes.
            foreach (var state in _componentState.Values)
                state.LastTranslated = null;
        }

        /// <summary>
        /// Re-apply font sizes for all tracked components using their true original sizes.
        /// Call after font override rules change to force size recalculation.
        /// </summary>
        public static void ReapplyAllFontSizes()
        {
            // DON'T clear componentScaleOverrides here — they were already
            // cleared by SetFontOverrides() and re-populated by ForceRefreshAllText()

            // Clear the applied-size cache so fontSize setters re-apply from true originals
            _originalFontSizes.Clear();

            // Re-set fontSize from true originals to trigger the fontSize setter prefix
            var refs = new List<KeyValuePair<int, object>>(PatchedComponentRefs);
            int count = 0;

            foreach (var kvp in refs)
            {
                if (kvp.Value == null) continue;
                try
                {
                    // fontSize path — needs the tracked true original to avoid reading a
                    // scaled value back as "original"
                    if (_trueOriginalFontSizes.TryGetValue(kvp.Key, out float trueOriginal))
                    {
                        var fontSizeProp = kvp.Value.GetType().GetProperty("fontSize",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (fontSizeProp != null && fontSizeProp.CanWrite)
                        {
                            // Set the true original size — the fontSize prefix will re-apply the correct scale
                            if (fontSizeProp.PropertyType == typeof(float))
                                fontSizeProp.SetValue(kvp.Value, trueOriginal, null);
                            else if (fontSizeProp.PropertyType == typeof(int))
                                fontSizeProp.SetValue(kvp.Value, (int)trueOriginal, null);

                            count++;
                        }
                    }

                    // Auto-size bounds path — INDEPENDENT of the fontSize tracking:
                    // components never scaled before have no tracked original (scale 1.0
                    // fast-exits without storing), and auto-sized text ignores fontSize
                    // anyway. Without this, static auto-sized components (main-menu
                    // items…) only picked a runtime scale change up after a game restart
                    // (issue #21). ApplyTMPAutoSizeScale reads/tracks its own original
                    // bounds and no-ops on non-auto-sized components.
                    if (_fontNameCache.TryGetValue(kvp.Key, out string cachedFontName) &&
                        !string.IsNullOrEmpty(cachedFontName))
                    {
                        string settingsName = FontManager.GetSettingsFontName(kvp.Key, cachedFontName);
                        float scale = FontManager.GetFontScale(settingsName, kvp.Key);
                        ApplyTMPAutoSizeScale(kvp.Value, kvp.Key, scale);
                    }
                }
                catch { }
            }

            TranslatorCore.LogDebug($"[Patches] ReapplyAllFontSizes: re-applied {count} components");
        }

        /// <summary>
        /// Apply the current per-font scale to the auto-size bounds of every tracked
        /// component using <paramref name="fontName"/>. Static auto-sized components
        /// never re-fire set_text and may be absent from the scanner cache, so the
        /// RefreshForFont pass misses them — the patch refs have them (issue #21:
        /// main-menu items ignored runtime Size changes until a game restart).
        /// </summary>
        public static void ApplyAutoSizeScaleForFont(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return;

            var refs = new List<KeyValuePair<int, object>>(PatchedComponentRefs);
            foreach (var kvp in refs)
            {
                if (kvp.Value == null) continue;
                try
                {
                    if (!_fontNameCache.TryGetValue(kvp.Key, out string cachedFontName) ||
                        string.IsNullOrEmpty(cachedFontName))
                        continue;

                    string settingsName = FontManager.GetSettingsFontName(kvp.Key, cachedFontName);
                    if (!string.Equals(settingsName, fontName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    float scale = FontManager.GetFontScale(settingsName, kvp.Key);
                    ApplyTMPAutoSizeScale(kvp.Value, kvp.Key, scale);
                }
                catch { }
            }
        }

        /// <summary>
        /// Re-apply the font scale (fontSize + auto-size bounds) to EVERY tracked component, re-derived
        /// from each component's CURRENT settings scale. Unlike the text-set refresh path, this does not
        /// depend on a component's text changing or on the game re-triggering it — so it reliably re-fits
        /// static / game-managed / cache-hit components after a GLOBAL toggle. Issue #21: toggling font
        /// replacement (or global translation) off left un-retriggered components at their old scaled
        /// fontSize — the design-scale gate already made GetFontScale correct (1.0 when not replacing),
        /// only the APPLIED size lagged, so the original font rendered ~designScale× too big. This is the
        /// same reliable mechanism the per-font Auto toggle uses (ApplyAutoSizeScaleForFont), generalized
        /// to all fonts. Call ONLY from discrete user toggles, never the periodic refresh (a forced
        /// re-fit mid-reveal would freeze a typewriter).
        /// </summary>
        public static void ReapplyScaleToAllComponents()
        {
            var refs = new List<KeyValuePair<int, object>>(PatchedComponentRefs);
            foreach (var kvp in refs)
            {
                if (kvp.Value == null) continue;
                try
                {
                    if (!_fontNameCache.TryGetValue(kvp.Key, out string cachedFontName) ||
                        string.IsNullOrEmpty(cachedFontName))
                        continue;

                    string settingsName = FontManager.GetSettingsFontName(kvp.Key, cachedFontName);
                    // Only re-apply when the resolved name is a REAL settings font. When a component's
                    // per-component original tracking is absent, GetSettingsFontName falls back to the
                    // raw current font name; for a fallback/variant font (e.g. a "<family>-Latin" subset
                    // the game/TMP substitutes for Latin glyphs) that is NOT a settings key, GetFontScale
                    // returns a bogus 1.0 and would SHRINK the component (issue #21: the title, rendered
                    // through such a variant, collapsed to scale 1.0 on toggle). Leave those to the patch
                    // path, which re-tracks the true original before scaling. No suffix stripping — stays
                    // language/script agnostic.
                    if (!TranslatorCore.FontSettingsMap.ContainsKey(settingsName))
                        continue;
                    ApplyFontScale(kvp.Value, settingsName);
                }
                catch { }
            }
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
            // Realtime: a font scan must not be held hostage by a paused game.
            yield return new WaitForSecondsRealtime(delaySeconds);
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
                TranslatorCore.LogDebug("[FontScan] No alternate TMP type found, skipping scan");
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

                // Find all TMP text components in the scene. Same latent trap as
                // FontManager.ApplyReplacementsToScene: a direct
                // UnityEngine.Object.FindObjectsOfType(Type) call can bind against an
                // overload absent from stripped interop assemblies and throw
                // MissingMethodException at JIT time — use the IL2CPP-safe helper.
                var allTmpComponents = TypeHelper.FindAllObjectsOfType(tmpTextType);
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
                    TranslatorCore.LogDebug($"[FontScan] Applied font replacement to {appliedCount} components");
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
            if (!TranslatorCore.TranslationsActive)
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
                TranslatorCore.LogDebug("[Patches] Graphic.OnEnable postfix applied");
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
                if (!TranslatorCore.TranslationsActive) return;


                // Fast exit: only process types we know are text components
                if (__instance == null) return;
                var type = __instance.GetType();
                bool isText = (TypeHelper.UI_TextType != null && TypeHelper.UI_TextType.IsAssignableFrom(type));
                if (!isText) return;

                var comp = __instance as Component;
                if (comp == null) return;

                // Defense-in-depth: never re-apply game fonts to our own UI.
                if (TranslatorCore.IsOwnUI(comp)) return;

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
                    // Pass settingsFontName (original game font name) as cache key, not clone display name
                    FontManager.EnsureCharsInCloneAtlasDirect(text, replacementFont, settingsFontName);

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

            }
            catch { }
        }

        #endregion

        /// <summary>
        /// Apply font scale to a text component (TMP or UI.Text).
        /// Stores original size on first call and applies scale relative to it.
        /// </summary>
        /// <summary>
        /// Apply the per-font scale from inside the set_text prefix, honouring the clone-font gate:
        /// scaling before the Unity clone is actually on the component cumulates sizes when the clone
        /// lands later. Shared by the prefix's normal tail AND its early exits — a component that
        /// keeps our translated text still had its font replaced further up, so skipping the scale
        /// left it at the unscaled size while its neighbours were scaled (mixed sizes down a menu or
        /// a scoreboard).
        /// </summary>
        private static void ApplyFontScaleGated(object instance, Font unityCloneFont, string unityCloneName, string fontNameForScale)
        {
            if (unityCloneFont != null)
            {
                object curFont = TypeHelper.GetFont(instance);
                string curFontName = (curFont is UnityEngine.Object cfo) ? cfo.name : null;
                if (!string.Equals(curFontName, unityCloneName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            ApplyFontScale(instance, fontNameForScale);
        }

        private static void ApplyFontScale(object instance, string fontName)
        {
            if (IsOwnUIText(instance)) return;
            if (instance == null || string.IsNullOrEmpty(fontName)) return;

            int instanceId = TypeHelper.GetInstanceID(instance);
            float scale = FontManager.GetFontScale(fontName, instanceId);
            int bump = FontManager.GetFontSizeBump(fontName);
            // Fast exit: if scale is 1.0, no bump, and we've never tracked this component, nothing to do
            // But if we HAVE a true original stored, we must continue to potentially restore the original size
            // (e.g., component was previously scaled by global, now overridden to 1.0)
            if (Math.Abs(scale - 1.0f) < 0.001f && bump == 0)
            {
                if (instanceId == -1 ||
                    (!_originalFontSizes.ContainsKey(instanceId) && !_trueOriginalFontSizes.ContainsKey(instanceId)
                     && !_originalAutoSizeMax.ContainsKey(instanceId)))
                    return;
            }
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

            // Apply font size bump for runtime font changes.
            // Bumping fontSize by ±1 creates new atlas cache entries → forces re-rasterization
            // with updated fontNames. 1px difference during runtime testing, resets on restart.
            float targetSize = (originalSize + bump) * scale;

            float currentSize = TypeHelper.GetFontSize(instance);

            // TMP auto-sizing OWNS fontSize: it recomputes the fit on every layout. Setting fontSize
            // directly on an auto-sized component is useless AND harmful — each refresh pass re-inflates
            // it to the scaled value, then the auto-sizer re-fits, producing a visible "grow then settle"
            // flash (issue #21: the title auto-fits BELOW its max → flashed big and corrected on every
            // pass; menu items that fit AT the max saw no diff → stayed stable, hence the title-only,
            // two-step symptom). For auto-sized text only the BOUNDS are scaled (ApplyTMPAutoSizeScale).
            bool tmpAutoSizing = IsTMPAutoSizingEnabled(instance);
            if (!tmpAutoSizing && currentSize >= 0 && Math.Abs(currentSize - targetSize) > 0.1f)
            {
                _bypassFontSizePrefix = true;
                TypeHelper.SetFontSize(instance, targetSize);
                _bypassFontSizePrefix = false;
            }

            // Also scale bestFit maxSize (Mono only — IL2CPP causes atlas corruption)
            if (TranslatorCore.Adapter == null || !TranslatorCore.Adapter.IsIL2CPP)
                ApplyBestFitScale(instance, instanceId, scale);

            // TMP auto-sizing overwrites fontSize with the computed fit, so the scale
            // above has no visible effect on auto-sized components — scale their
            // auto-size BOUNDS instead (issue #21).
            ApplyTMPAutoSizeScale(instance, instanceId, scale);
        }

        // Cache original TMP auto-size bounds per component (for restore at scale 1.0)
        private static readonly Dictionary<int, float> _originalAutoSizeMax = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _originalAutoSizeMin = new Dictionary<int, float>();

        /// <summary>
        /// TMP counterpart of ApplyBestFitScale: on components with enableAutoSizing,
        /// the game recomputes fontSize to fit the container, erasing any direct
        /// fontSize scaling. Multiplying fontSizeMax/fontSizeMin by the user's per-font
        /// scale keeps the game's responsive fit while moving its ceiling/floor — the
        /// only lever that visibly changes auto-sized text (issue #21: text clamped at
        /// fontSizeMax rendered smaller than the original font's design scale).
        /// No-ops on non-TMP components (no enableAutoSizing property) and on
        /// components with auto-sizing off.
        /// </summary>
        /// <summary>
        /// True when the component is a TMP text with enableAutoSizing on. Used to skip the direct
        /// fontSize set (the auto-sizer owns fontSize; setting it re-inflates then re-fits — issue #21).
        /// Returns false for non-TMP components (no enableAutoSizing property).
        /// </summary>
        private static bool IsTMPAutoSizingEnabled(object instance)
        {
            try
            {
                var autoProp = instance.GetType().GetProperty("enableAutoSizing", BindingFlags.Public | BindingFlags.Instance);
                return autoProp != null && (bool)autoProp.GetValue(instance, null);
            }
            catch { return false; }
        }

        private static void ApplyTMPAutoSizeScale(object instance, int instanceId, float scale)
        {
            if (IsOwnUIText(instance)) return;
            try
            {
                var type = instance.GetType();
                var autoProp = type.GetProperty("enableAutoSizing", BindingFlags.Public | BindingFlags.Instance);
                if (autoProp == null) return;
                if (!(bool)autoProp.GetValue(instance, null)) return;

                var maxProp = type.GetProperty("fontSizeMax", BindingFlags.Public | BindingFlags.Instance);
                if (maxProp == null || !maxProp.CanWrite) return;

                if (!_originalAutoSizeMax.TryGetValue(instanceId, out float origMax))
                {
                    origMax = Convert.ToSingle(maxProp.GetValue(instance, null));
                    if (origMax <= 0) return;
                    _originalAutoSizeMax[instanceId] = origMax;
                }
                bool boundsChanged = false;
                float targetMax = origMax * scale;
                float currentMax = Convert.ToSingle(maxProp.GetValue(instance, null));
                if (Math.Abs(currentMax - targetMax) > 0.1f)
                {
                    maxProp.SetValue(instance, targetMax, null);
                    boundsChanged = true;
                }

                var minProp = type.GetProperty("fontSizeMin", BindingFlags.Public | BindingFlags.Instance);
                if (minProp != null && minProp.CanWrite)
                {
                    if (!_originalAutoSizeMin.TryGetValue(instanceId, out float origMin))
                    {
                        origMin = Convert.ToSingle(minProp.GetValue(instance, null));
                        _originalAutoSizeMin[instanceId] = origMin;
                    }
                    float targetMin = origMin * scale;
                    float currentMin = Convert.ToSingle(minProp.GetValue(instance, null));
                    if (Math.Abs(currentMin - targetMin) > 0.1f)
                    {
                        minProp.SetValue(instance, targetMin, null);
                        boundsChanged = true;
                    }
                }

                // Changing the auto-size bounds does NOT re-run TMP's fit — the component keeps its
                // previously settled fontSize (TMP caches the auto-size result; a plain ForceMeshUpdate
                // reuses it). At runtime toggle that left the OLD fontSize with the NEW font (issue #21:
                // disable grew / enable shrank auto-sized text — racy across components).
                if (boundsChanged)
                {
                    // Settle the container layout FIRST so the fit measures the final RectTransform, not a
                    // transient one — otherwise a component that fits BELOW its max briefly renders at the
                    // max ceiling and re-fits a frame later (the title's "grow then settle" flash; menu
                    // items that fit AT the max don't show it). Then a FULL reparse re-runs auto-sizing
                    // within the new bounds against the settled layout, in one synchronous pass.
                    TypeHelper.ForceUpdateCanvases();
                    TypeHelper.ForceMeshUpdateReparse(instance);
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[Patches] AutoSize scale failed: {ex.Message}");
            }
        }

        // Cache original resizeTextMaxSize per component
        // Original resizeTextMaxSize per component — the anti-cumulation reference for best-fit,
        // the exact counterpart of _trueOriginalFontSizes.
        // 🔴 Never Clear() this on a live component: the scaled max would then be read back as the
        // original and every pass would grow it again. It is deliberately absent from
        // ClearFontSizeCache for that reason; CleanDeadRefs drops it per dead id, which is safe.
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

        // === READ-BACK DETECTION ===
        /// <summary>
        /// Record the translation applied to a component (called after successful translation).
        /// Read back by <see cref="DetectReadBack"/> when the game appends to what we wrote.
        /// </summary>
        public static void TrackTranslation(int compId, string original, string translated)
        {
            if (compId == -1 || string.IsNullOrEmpty(original) || string.IsNullOrEmpty(translated)) return;
            var state = StateFor(compId);
            state.ReadBackSource = original;
            state.ReadBackTranslated = translated;
        }

        /// <summary>
        /// Detect if incoming text is a game read-back of translated text with appended content.
        /// If so, reconstruct the source-language equivalent and VERIFY it exists in cache.
        /// Returns null if not a read-back or if reconstructed text has no cache hit.
        /// </summary>
        public static string DetectReadBack(int compId, string incomingText)
        {
            if (compId == -1 || string.IsNullOrEmpty(incomingText)) return null;
            var state = PeekState(compId);
            if (state == null || state.ReadBackSource == null || state.ReadBackTranslated == null) return null;

            string original = state.ReadBackSource;
            string translated = state.ReadBackTranslated;

            // The incoming text must START WITH the translated text but be LONGER
            // (the game appended something to the read-back)
            if (incomingText.Length > translated.Length && incomingText.StartsWith(translated))
            {
                // Reconstruct: original source text + the appended suffix
                string suffix = incomingText.Substring(translated.Length);
                string reconstructed = original + suffix;

                // SAFETY: only accept the reconstruction if it produces a cache hit.
                // If the reconstructed text doesn't match any known key, this is NOT
                // a read-back — it's a legitimate new text that happens to start with
                // a previous translation. Return null to let normal flow handle it.
                string normalizedReconstructed = TranslatorCore.NormalizeForCacheLookup(reconstructed);
                if (!TranslatorCore.TranslationCache.ContainsKey(normalizedReconstructed))
                {
                    if (TranslatorCore.DebugMode)
                        TranslatorCore.LogDebug($"[READBACK-REJECT] comp={compId} reconstructed text has no cache hit, treating as new text\n  reconstructed({reconstructed.Length}c)='{reconstructed}'");
                    return null;
                }

                if (TranslatorCore.DebugMode)
                    TranslatorCore.LogDebug($"[READBACK] comp={compId} detected read-back+append → cache hit!\n  incoming({incomingText.Length}c)='{incomingText}'\n  reconstructed({reconstructed.Length}c)='{reconstructed}'");

                return reconstructed;
            }

            return null;
        }

        // === TYPEWRITING DETECTION ===
        //
        // Text growing a few characters at a time on one component. While a reveal is in progress
        // the text is held back, so a half-written line is never sent for translation: every
        // cache-miss text waits TYPEWRITING_STABILIZE_MS without changing before being handed over.
        // Cache hits bypass all of this.
        //
        // The state lives in ComponentTextState (the Typewriting* fields); _typewritingPending is
        // the index of the components that have one in flight.
        private const float TYPEWRITING_STABILIZE_MS = 500f; // ms without change = text is final

        /// <summary>Check if a component is currently being tracked for typewriting.</summary>
        public static bool IsInTypewritingState(int compId)
        {
            var state = PeekState(compId);
            return state != null && state.Mode == TextMode.Typewriter;
        }

        /// <summary>
        /// Touch the typewriting timestamp for a component. Called on cache hits
        /// to prevent the stabilizer from thinking the typewriting stopped.
        /// Also updates the stored text if it grew (StartsWith).
        /// </summary>
        public static void TouchTypewritingTimestamp(int compId, string currentText)
        {
            if (compId == -1 || string.IsNullOrEmpty(currentText)) return;
            var state = PeekState(compId);
            if (state == null || state.Mode != TextMode.Typewriter) return;

            bool isGrowing = TextRelations.Grows(state.TypewritingText, currentText);
            bool isSame = currentText == state.TypewritingText;

            if (state.TypewritingQueued)
            {
                if (_dbgTouchLog < 10 && (isGrowing || isSame))
                {
                    _dbgTouchLog++;
                    TranslatorCore.LogInfo($"[TW-TOUCH] comp={compId} BLOCKED by Queued=true, isGrowing={isGrowing} isSame={isSame} stateText='{Head(state.TypewritingText)}' curText='{Head(currentText)}'");
                }
                return;
            }

            if (!isGrowing && !isSame)
            {
                if (_dbgTouchLog < 10)
                {
                    _dbgTouchLog++;
                    TranslatorCore.LogInfo($"[TW-TOUCH] comp={compId} SKIP unrelated stateText='{Head(state.TypewritingText)}' curText='{Head(currentText)}'");
                }
                return;
            }

            if (_dbgTouchLog < 10)
            {
                _dbgTouchLog++;
                TranslatorCore.LogInfo($"[TW-TOUCH] comp={compId} OK isGrowing={isGrowing} text='{Head(currentText)}'");
            }

            HoldTypewriting(state, compId, isGrowing ? currentText : state.TypewritingText, Time.realtimeSinceStartup);
        }

        /// <summary>First 30 characters of a text, for a log line.</summary>
        private static string Head(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Length > 30 ? text.Substring(0, 30) : text;
        }

        /// <summary>
        /// Hold this component's text back: a reveal is in progress, or has just restarted.
        /// Puts it on the work list so the stabilizer will look at it.
        /// </summary>
        private static void HoldTypewriting(ComponentTextState state, int compId, string text, float now)
        {
            state.Mode = TextMode.Typewriter;
            state.TypewritingText = text;
            state.TypewritingSince = now;
            state.TypewritingQueued = false;
            _typewritingPending.Add(compId);
        }

        private static int _dbgTwProgress = 0;
        private static int _fontDebugOnce = 0;

        public static bool IsTypewritingInProgress(int compId, string newText)
        {
            if (compId == -1 || string.IsNullOrEmpty(newText)) return false;
            if (!TranslatorCore.TypewritingDetection) return false;

            var state = StateFor(compId);

            // Concat components are handled by the concat system, not TW
            if (state.Mode == TextMode.Concat) return false;

            float now = Time.realtimeSinceStartup;

            if (state.Mode == TextMode.Typewriter)
            {
                if (state.TypewritingText == newText)
                {
                    // Same text, still pending
                    return true;
                }

                float elapsed = (now - state.TypewritingSince) * 1000f;
                bool isGrowing = TextRelations.Grows(state.TypewritingText, newText);

                // Log every call for typewriting components
                if (TranslatorCore.DebugMode)
                {
                    TranslatorCore.LogDebug($"[TW-CHECK] comp={compId} prev={state.TypewritingText.Length}c new={newText.Length}c growing={isGrowing} elapsed={elapsed:F0}ms queued={state.TypewritingQueued}\n  prevText='{state.TypewritingText}'\n  newText='{newText}'");
                }

                if (isGrowing && elapsed < TYPEWRITING_STABILIZE_MS)
                {
                    HoldTypewriting(state, compId, newText, now);
                    return true;
                }

                // Detect TW overwrite: game is writing new text OVER our translation.
                // Pattern: text shrinks or changes while previous state was already queued/translated.
                // Each intermediate state mixes source and target → DO NOT finalize, keep deferring.
                bool isShrinkingOverwrite = !isGrowing && state.TypewritingQueued
                                            && newText.Length < state.TypewritingText.Length;
                if (isShrinkingOverwrite)
                {
                    // Don't finalize the mixed state. Just update tracking and keep deferring.
                    HoldTypewriting(state, compId, newText, now);
                    return true;
                }

                // Text changed completely (not StartsWith) or grew after long pause.
                if (!state.TypewritingQueued)
                {
                    TranslatorCore.LogDebug($"[TW-FINAL] comp={compId} isGrowing={isGrowing} elapsed={elapsed:F0}ms\n  prev({state.TypewritingText.Length}c)='{state.TypewritingText}'\n  new({newText.Length}c)='{newText}'");
                    ProcessFinalizedText(compId, state.TypewritingText);
                }

                // Store new text as new start, defer it
                HoldTypewriting(state, compId, newText, now);
                return true;
            }

            // First time — defer for stabilization
            if (TranslatorCore.DebugMode)
            {
                TranslatorCore.LogDebug($"[TW-NEW] comp={compId} FIRST text({newText.Length}c)='{newText}'");
            }
            HoldTypewriting(state, compId, newText, now);
            return true;
        }

        /// <summary>
        /// Process a finalized typewriting text: queue for AI if not in cache,
        /// or re-trigger SetText if already cached.
        /// </summary>
        private static void ProcessFinalizedText(int compId, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            string normalizedText = TranslatorCore.NormalizeForCacheLookup(text);
            bool inCache = TranslatorCore.TranslationCache.ContainsKey(normalizedText);
            // Also check reverse cache — text might already be translated (FR re-set by game).
            // The exact probe alone missed the re-decorated form, so the stabilizer queued our own
            // translation for a second pass: the entry was refused at storage, but the AI call had
            // already been paid for and the "translating" overlay shown.
            // Same question as every other gate, same answer: inCache already covered the key, so
            // what remains is "is this text itself target language?".
            bool alreadyTranslated = !inCache && TranslatorCore.IsAlreadyTargetText(text);

            if (TranslatorCore.DebugMode)
            {
                TranslatorCore.LogDebug($"[TW-FINALIZE] comp={compId} inCache={inCache} alreadyTranslated={alreadyTranslated} text({text.Length}c)='{text}'");
            }

            if (inCache)
            {
                if (_patchedComponentRefs.TryGetValue(compId, out var comp) && comp != null)
                {
                    try { TypeHelper.SetText(comp, text); }
                    catch { }
                }
            }
            else if (alreadyTranslated)
            {
                // Text is already in target language (reverse cache hit) — skip
                if (TranslatorCore.DebugMode)
                    TranslatorCore.LogDebug($"[TW-FINALIZE] SKIP already translated: '{(text.Length > 40 ? text.Substring(0,40) : text)}'");
            }
            else
            {
                object comp = null;
                _patchedComponentRefs.TryGetValue(compId, out comp);
                TranslatorCore.QueueForTranslation(text, comp);
            }
        }

        /// <summary>
        /// Check for stabilized typewriting texts and queue them for translation.
        /// Called from ProcessPendingUpdates (main thread, periodic).
        /// </summary>
        public static void ProcessStabilizedTypewriting()
        {
            if (_typewritingPending.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            var toDrop = new List<int>();

            foreach (int compId in _typewritingPending)
            {
                // 🔴 An id with no state left can never do anything again, so it is dropped rather
                // than skipped. Skipping it kept it in the set for good, and one such id is enough
                // to stop `Count == 0` from ever being true again — the early return above then
                // never fires and this allocates a List every frame for the rest of the session.
                // Self-healing on purpose: the state can vanish through ClearTypewritingState (scene
                // unload) or through the concat/mirror paths, and this covers all of them at once.
                var state = PeekState(compId);
                if (state == null || state.Mode != TextMode.Typewriter) { toDrop.Add(compId); continue; }
                float elapsed = (now - state.TypewritingSince) * 1000f;
                if (elapsed >= TYPEWRITING_STABILIZE_MS)
                    toDrop.Add(compId);
            }

            // Carries both the stabilized ids and the stateless ones; the latter fall out at the
            // mode test below, after having been removed from the set.
            foreach (int compId in toDrop)
            {
                _typewritingPending.Remove(compId);

                var state = PeekState(compId);
                // Concat components fall out here for free: the mode is exclusive, so one that
                // switched to assembling is no longer Typewriter. That used to need its own branch.
                if (state == null || state.Mode != TextMode.Typewriter) continue;
                if (state.TypewritingQueued) continue; // Already processed this stabilized text

                // Mark as queued but keep the state — if more chars are added,
                // IsTypewritingInProgress will detect it and reset.
                state.TypewritingQueued = true;

                TranslatorCore.LogDebug($"[TW-STAB] comp={compId} stabilized after {(now - state.TypewritingSince)*1000:F0}ms text='{(state.TypewritingText.Length > 40 ? state.TypewritingText.Substring(0,40) : state.TypewritingText)}'");
                ProcessFinalizedText(compId, state.TypewritingText);
            }
        }

        /// <summary>
        /// Drop the reveal being followed on this component, keeping everything else.
        /// ⚠ Only leaves Typewriter mode — it must not drag a component out of Concat, since
        /// entering Concat calls this to cancel whatever reveal was in flight.
        /// </summary>
        private static void ForgetTypewriting(ComponentTextState state)
        {
            if (state.Mode == TextMode.Typewriter) state.Mode = TextMode.Normal;
            state.TypewritingText = null;
            state.TypewritingSince = 0f;
            state.TypewritingQueued = false;
        }

        /// <summary>
        /// This component builds its text in parts. Cancels any reveal in flight — the two are
        /// exclusive, and this is now the ONE place that says so.
        /// </summary>
        private static void EnterConcat(ComponentTextState state, int compId)
        {
            ForgetTypewriting(state);
            _typewritingPending.Remove(compId);
            state.Mode = TextMode.Concat;
        }

        /// <summary>
        /// This component is no longer building its text in parts. The stored parts go with it:
        /// keeping them would let a later assembly resume from a text that is gone.
        /// </summary>
        private static void LeaveConcat(ComponentTextState state)
        {
            if (state.Mode == TextMode.Concat) state.Mode = TextMode.Normal;
            state.Deltas = null;
        }

        /// <summary>
        /// Clear typewriting state (on scene change, settings change, etc.)
        ///
        /// ⚠ Only the reveal, not the whole record: concat mode, the deltas and the read-back
        /// tracking are not what this method is about, and dropping them here would silently reset
        /// the detection on every scene unload.
        ///
        /// ⚠ Deliberately does NOT touch _typewritingPending: that set is the work list, and
        /// ProcessStabilizedTypewriting drops any id whose reveal is gone. Emptying it here would
        /// work too, but only for this one cause — the self-healing covers every cause.
        /// </summary>
        public static void ClearTypewritingState()
        {
            foreach (var state in _componentState.Values)
                ForgetTypewriting(state);
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

        // Set around our own internal SetText calls (e.g. the render-repair space nudge)
        // so the whole translation pipeline ignores that text — no target→target
        // re-translation, no reverse-cache pollution, no re-queue. The game's own
        // TMP text-changed event still fires (it is independent of this prefix), which is
        // exactly what the nudge needs to re-run the game's reveal (issue #21).
        //
        // ⚠ Checked by EVERY text setter prefix, not just the TMP/UI.Text/TextMesh one. It used to
        // guard that single path, and TMProOld carried an inert stand-in for the same idea
        // (a HashSet that was read and never filled). The guard is the cross-cutting kind: one
        // path having it is one path's worth of protection.
        //
        // ⚠ ThreadStatic, not a process-wide flag: it belongs to the thread doing the writing.
        // Every writer raises and lowers it around one synchronous SetText on the main thread, but
        // the generic text prefix can fire on a background thread (Rewired's input thread), and a
        // process-wide flag would silently skip that thread's own text while ours was in flight.
        // Same reasoning and same shape as UIToolkitSupport._writingBack, which reached it first.
        //
        // ⚠ Deliberately NOT checked in the getter postfixes: their job is to catch text the game
        // preloaded, and a read is not one of our writes. Suppressing them inside the window would
        // change what they return, for no demonstrated need.
        [ThreadStatic] internal static bool BypassTextPrefix;

        /// <summary>
        /// What the caller must do once the text has been routed.
        /// </summary>
        private enum RouteOutcome
        {
            /// <summary>Routed, possibly translated — carry on with the font work.</summary>
            Translated,
            /// <summary>Leave this setter alone entirely.</summary>
            Stop,
            /// <summary>Nothing to translate, but the component still needs its scale re-asserted.</summary>
            StopButRescale,
        }

        /// <summary>
        /// Decides what happens to one incoming text: is it the player's own typing, an assembly in
        /// parts, a reveal in flight, something already translated — or an ordinary line to send.
        ///
        /// 🔴 **Split out of ProcessTextPatchPrefix so it is not welded to the font work.** Those
        /// two were tressed together in one ~500-line method, so reusing the routing meant dragging
        /// the whole font pipeline with it — and every text framework added since simply copied the
        /// one line it could (the call to TranslateTextWithTracking) and inherited none of this.
        /// That is why procedural text, input mirrors and the already-written check existed for
        /// TMP, UI.Text and TextMesh alone.
        ///
        /// ⚠ Font work stays with the caller, in both directions: this never touches a font, and
        /// the one branch that needed a scale re-asserted says so through StopButRescale rather
        /// than doing it here.
        /// </summary>
        private static RouteOutcome RouteText(object instance, Component comp, int compId,
                                              bool isOwnUI, string componentType, ref string textValue)
        {
            // Don't translate InputField textComponent (user's typed text)
            if (componentType != "TextMesh" && IsInputFieldTextComponentCached(instance)) return RouteOutcome.Stop;

            // Don't translate mirrors of the user's typed input (styled copy in
            // the input widget, live preview elsewhere). Also purge TW/concat
            // state for the component: char-by-char typing has the exact
            // signature of a typewriting effect, and a one-frame-late mirror
            // must not leave a stale prefix behind for the stabilizer to queue.
            if (IsUserInputMirror(instance, textValue))
            {
                var typedState = PeekState(compId);
                if (typedState != null)
                {
                    ForgetTypewriting(typedState);
                    _typewritingPending.Remove(compId);
                    LeaveConcat(typedState);
                    typedState.Deltas = null;
                    typedState.LastRaw = null;
                }
                return RouteOutcome.Stop;
            }

            // Own UI (UI-specific translation prompt) — computed once near the top.
            string preTranslateText = textValue;

            // Check concat assembled cache: if this exact text was already assembled
            // by the concat system, apply the cached translation immediately.
            // This prevents scanner refresh from re-queuing assembled texts.
            bool concatCacheHit = false;
            string concatCached;
            if (_concatAssembledCache.TryGetValue(textValue, out concatCached))
            {
                // CN text matched → apply FR translation, skip all translate logic
                textValue = concatCached;
                concatCacheHit = true;
            }
            else if (_concatTranslatedValues.Contains(textValue))
            {
                // Text IS already a translated result → keep as-is
                concatCacheHit = true;
            }

          // ⚠ This block sits two columns left of where it belongs. Inherited, not accidental: the
          // concat cache was wrapped around code that already existed, and shifting it properly
          // would have buried the real change under a re-indent of two hundred lines. Same reason
          // it survived the move into this method.
          if (!concatCacheHit)
          {
            // Everything below follows this one component. Created here rather than looked up
            // five times: from this point on every branch either reads or writes it.
            ComponentTextState state = compId != -1 ? StateFor(compId) : null;

            // Skip if text is exactly our last translated output (scanner refresh, etc.)
            if (state != null)
            {
                if (state.LastTranslated != null && textValue == state.LastTranslated)
                {
                    // Nothing to translate, but the font work above already ran on this
                    // component — leaving without the scale left it at the unscaled size
                    // while neighbours that took the full path were scaled, so a menu or a
                    // scoreboard ended up with mismatched line sizes. The size is idempotent
                    // (derived from the cached true original), so re-asserting it is free.
                    return RouteOutcome.StopButRescale;
                }
            }

            // === Frame tracking for concat detection ===
            // Count set_text calls per component per frame.
            // 2+ calls in same frame → concat mode (procedural text building).
            if (state != null)
            {
                int currentFrame = Time.frameCount;
                if (state.LastFrame == currentFrame)
                {
                    state.FrameCallCount++;

                    // Flag as concat ONLY if the text is GROWING (prefix match).
                    // Without this, game init (default→real value = 2 set_text) false-positives.
                    if (state.FrameCallCount >= 2 && TranslatorCore.ConcatDetection && state.Mode != TextMode.Concat)
                    {
                        string prevRaw = state.LastRaw;
                        // The delta must carry more than layout whitespace: a lone appended
                        // "\n" at start-up is not procedural assembly (see LooksLikeConcatGrowth).
                        if (!string.IsNullOrEmpty(prevRaw)
                            && TextRelations.LooksLikeConcatGrowth(prevRaw, textValue))
                        {
                            EnterConcat(state, compId);
                            TranslatorCore.LogDebug($"[CONCAT-DETECT] comp={compId} flagged (text grew {prevRaw.Length}c→{textValue.Length}c in frame {currentFrame})");
                        }
                    }
                }
                else
                {
                    state.LastFrame = currentFrame;
                    state.FrameCallCount = 1;

                    // Detect TW pattern on a concat-flagged component:
                    // single set_text per frame with text growing by 1-3 chars = typewriting.
                    // Unflag concat and let TW handle it.
                    if (state.Mode == TextMode.Concat)
                    {
                        string prevRaw2 = state.LastRaw;
                        if (!string.IsNullOrEmpty(prevRaw2)
                            && TextRelations.LooksLikeTypewriterGrowth(prevRaw2, textValue))
                        {
                            LeaveConcat(state);
                            TranslatorCore.LogDebug($"[CONCAT-UNFLAG] comp={compId} reverted to TW (grew by {textValue.Length - prevRaw2.Length} chars in separate frame)");
                        }
                    }
                }

                // Raw text update happens AFTER the concat handling block below,
                // so the concat block can compare against the PREVIOUS raw text.
            }

            // === Concat handling ===
            // For concat components: track raw text, extract deltas, translate each separately.
            // For non-concat: use existing flow (TranslateTextWithTracking with TW detection).
            bool handledAsConcat = false;
            bool isConcatComp = state != null && state.Mode == TextMode.Concat;

            if (isConcatComp)
            {
                string lastRaw = state.LastRaw;

                if (!string.IsNullOrEmpty(lastRaw)
                    && TextRelations.Grows(lastRaw, textValue))
                {
                    // Text grew — extract delta (pure source language)
                    string delta = textValue.Substring(lastRaw.Length);

                    // Store delta for re-assembly later (when AI translations arrive)
                    List<string> deltas = state.Deltas;
                    if (deltas == null)
                    {
                        deltas = new List<string>();
                        // First delta: also store the base text
                        deltas.Add(lastRaw);
                        state.Deltas = deltas;
                    }
                    deltas.Add(delta);

                    // Preserve leading/trailing newlines: AI may strip them during translation.
                    // Extract \n before/after, translate the core, then re-add.
                    string leadingNL = "", trailingNL = "";
                    string deltaCore = delta;
                    while (deltaCore.Length > 0 && deltaCore[0] == '\n') { leadingNL += "\n"; deltaCore = deltaCore.Substring(1); }
                    while (deltaCore.Length > 0 && deltaCore[deltaCore.Length - 1] == '\n') { trailingNL = "\n" + trailingNL; deltaCore = deltaCore.Substring(0, deltaCore.Length - 1); }

                    // Translate core delta directly (skip TW — concat deltas are immediate)
                    string translatedCore = string.IsNullOrEmpty(deltaCore) ? "" : TranslatorCore.TranslateTextWithTracking(deltaCore, comp, isOwnUI, skipTypewriting: true);
                    string translatedDelta = leadingNL + translatedCore + trailingNL;

                    // Build display: previous translated + translated delta
                    string lastTrans = state.LastTranslated;
                    if (string.IsNullOrEmpty(lastTrans))
                    {
                        // First part wasn't translated yet (TW was capturing it before concat was detected).
                        // Translate it now.
                        lastTrans = TranslatorCore.TranslateTextWithTracking(lastRaw, comp, isOwnUI, skipTypewriting: true);
                        if (string.IsNullOrEmpty(lastTrans)) lastTrans = lastRaw;
                    }

                    textValue = lastTrans + translatedDelta;
                    state.LastRaw = preTranslateText; // full raw text so far
                    state.LastTranslated = textValue;
                    // Cache the assembled result: raw source → assembled target (runtime only)
                    _concatAssembledCache[preTranslateText] = textValue;
                    _concatTranslatedValues.Add(textValue);
                    handledAsConcat = true;

                    if (TranslatorCore.DebugMode)
                        TranslatorCore.LogDebug($"[CONCAT] comp={compId} delta({delta.Length}c)='{(delta.Length > 40 ? delta.Substring(0, 40) + "..." : delta)}'");
                }
                else if (!string.IsNullOrEmpty(lastRaw) && textValue.Length <= lastRaw.Length
                         && !textValue.StartsWith(lastRaw))
                {
                    // Text shrunk or changed completely — component likely reused for different content.
                    // Unflag concat so the new text is treated normally (queued for AI if cache miss).
                    // If the game does concat again (2+ set_text same frame), it'll be re-flagged.
                    state.LastRaw = null;
                    LeaveConcat(state);
                    isConcatComp = false; // update local flag for rest of this prefix call
                    TranslatorCore.LogDebug($"[CONCAT-RESET] comp={compId} unflagged, text changed from {lastRaw.Length}c to {textValue.Length}c");
                }

                // Raw text tracking is done in the frame tracking block above
            }

            // Also detect concat for non-flagged components (the game appending source text to
            // the translation we already wrote)
            string lastTranslatedTarget = state?.LastTranslated;
            if (!handledAsConcat
                && !string.IsNullOrEmpty(lastTranslatedTarget)
                && TextRelations.Grows(lastTranslatedTarget, textValue))
            {
                // Game appended untranslated text to our translation → extract that delta
                string delta = textValue.Substring(lastTranslatedTarget.Length);

                // Preserve leading/trailing newlines
                string leadNL = "", trailNL = "";
                string dCore = delta;
                while (dCore.Length > 0 && dCore[0] == '\n') { leadNL += "\n"; dCore = dCore.Substring(1); }
                while (dCore.Length > 0 && dCore[dCore.Length - 1] == '\n') { trailNL = "\n" + trailNL; dCore = dCore.Substring(0, dCore.Length - 1); }

                string transCore = string.IsNullOrEmpty(dCore) ? "" : TranslatorCore.TranslateTextWithTracking(dCore, comp, isOwnUI, skipTypewriting: true, skipQueueing: true);
                string translatedDelta = leadNL + transCore + trailNL;
                textValue = lastTranslatedTarget + translatedDelta;
                state.LastTranslated = textValue;
                // Also cache with the raw text as key (for scanner refresh lookups)
                _concatAssembledCache[preTranslateText] = textValue;
                _concatTranslatedValues.Add(textValue);
                handledAsConcat = true;

                if (TranslatorCore.DebugMode)
                    TranslatorCore.LogDebug($"[CONCAT-FR] comp={compId} delta({delta.Length}c)='{(delta.Length > 40 ? delta.Substring(0, 40) + "..." : delta)}'");
            }

            if (!handledAsConcat)
            {
                // For concat components: check cache but do NOT queue full text to AI.
                // Deltas are already queued individually by the concat handler above.
                // For non-concat: normal flow (cache check + queue if miss).
                textValue = TranslatorCore.TranslateTextWithTracking(textValue, comp, isOwnUI,
                    skipQueueing: isConcatComp);

                // Track translated text for concat detection (target-language prefix matching)
                if (state != null && textValue != preTranslateText)
                {
                    state.LastTranslated = textValue;
                    // For concat components: also remember the translated text so the
                    // different target versions (AI vs concat-assembled) are all recognized.
                    if (isConcatComp)
                        _concatTranslatedValues.Add(textValue);
                }
                else if (state != null && textValue == preTranslateText)
                {
                    state.LastTranslated = null;
                    // For concat components: the unchanged text might be an AI translation
                    // (from Apply OK) that we don't recognize. Remember it to prevent re-queue.
                    if (isConcatComp && textValue.Length > 20)
                        _concatTranslatedValues.Add(textValue);
                }
            }

            // Update raw text tracking AFTER concat/translate blocks
            // (so next set_text can compare against this value)
            if (state != null)
                state.LastRaw = preTranslateText;
          } // end if (!concatCacheHit)

            return RouteOutcome.Translated;
        }

        private static void ProcessTextPatchPrefix(object __instance, ref string textValue, string componentType)
        {
            if (string.IsNullOrEmpty(textValue)) return;

            // Early exit: our own internal set_text (nudge) — don't translate/track it.
            if (BypassTextPrefix) return;

            // Early exit: translations globally disabled → zero overhead
            if (!TranslatorCore.TranslationsActive) return;

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

                // Own UI reaches here only when translate_mod_ui is ON (otherwise
                // ShouldSkipTranslation returned above). Our own UI must NEVER enter the
                // game's font pipeline — its font is managed separately (interface font) and
                // must not pollute the game's _fonts map. Treat it like a no-font component:
                // skip all font detection/registration/replacement below, but still translate.
                // Cheap when OFF: IsOwnUITranslatable returns false immediately without a walk.
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(comp);

                int compId = TypeHelper.GetInstanceID(__instance);
                string fontName = null;
                string settingsFontName = null;
                object fontObj = null;
                Font unityCloneFont = null;  // Track the clone applied to this component
                string unityCloneName = null;      // Clone's display name (e.g., "calibri") — for font NAME comparisons
                string unityCloneFallback = null;   // Original game font name — for CACHE KEY lookups

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
                        string resolvedOriginal = FontManager.GetOriginalForClone(f);
                        if (resolvedOriginal != null)
                        {
                            // Clone resolved to original by instance ID
                            fontName = resolvedOriginal;
                            if (compId != -1)
                                _inheritedCloneComponents.Add(compId);
                        }
                        else
                        {
                            // Not a clone — check if we already tracked an original for this component.
                            // This handles external fonts (Unity built-in, system fontNames fallback)
                            // that replaced the original game font on the component.
                            string trackedOriginal = FontManager.GetOriginalFontName(compId);
                            if (trackedOriginal != null)
                                fontName = trackedOriginal;
                            // Otherwise keep fontName as-is — it's either a game font we haven't
                            // seen yet (first pass), or an external we can't resolve.
                            // RegisterUnityFontObject will categorize it.
                        }
                    }
                    if (compId != -1)
                    {
                        _fontNameCache[compId] = fontName;
                        _patchedComponentRefs[compId] = __instance;
                    }
                }

                if (profiling) { t2 = _profSw.ElapsedTicks; _profGetFont += t2 - t1; }

                if (!string.IsNullOrEmpty(fontName) && !isOwnUI)
                {
                    settingsFontName = FontManager.GetSettingsFontName(compId, fontName);

                    FontManager.RegisterFontByName(settingsFontName, componentType);
                    FontManager.IncrementUsageCount(settingsFontName);

                    // Skip translation if disabled for this font
                    if (!FontManager.IsTranslationEnabled(settingsFontName))
                        return;

                    // Check font override rules (pattern-based font/size overrides)
                    if (TranslatorCore.FontOverrides.Count > 0)
                    {
                        string goPath = comp != null ? TranslatorCore.GetGameObjectPath(comp.gameObject) : null;
                        var fontOverride = TranslatorCore.FindFontOverride(compId, goPath, settingsFontName, textValue);
                        if (fontOverride != null)
                        {
                            // Override font replacement if specified
                            if (!string.IsNullOrEmpty(fontOverride.replacement))
                            {
                                settingsFontName = fontOverride.replacement;
                            }
                            // Override scale if specified (> 0)
                            if (fontOverride.size_multiplier > 0.001f)
                            {
                                FontManager.ApplyTemporaryScale(compId, fontOverride.size_multiplier);
                            }
                        }
                    }

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

                        // Single implementation, shared with the direct UI.Text scene pass
                        // (FontManager.ApplyUnityClonesToScene) so the coverage rule and the
                        // CanvasRenderer rebind can't drift between the two entry points.
                        var replacementFont = FontManager.TryApplyUnityClone(__instance, fontObj, settingsFontName, textValue);
                        if (replacementFont != null)
                        {
                            unityCloneFont = replacementFont;
                            unityCloneName = replacementFont.name;  // Clone's display name for font comparisons
                            unityCloneFallback = settingsFontName;  // Original game font name for cache key lookups
                        }
                    }
                }

                if (profiling) { t3 = _profSw.ElapsedTicks; _profFontOps += t3 - t2; }

                // Kept here as well as inside RouteText: the font epilogue below compares against
                // what arrived, to tell a translated component from an untouched one.
                string preTranslateText = textValue;

                switch (RouteText(__instance, comp, compId, isOwnUI, componentType, ref textValue))
                {
                    case RouteOutcome.Stop:
                        return;
                    case RouteOutcome.StopButRescale:
                        // Nothing to translate, but the font work above already ran on this
                        // component — leaving without the scale left it at the unscaled size while
                        // neighbours that took the full path were scaled. The size is idempotent
                        // (derived from the cached true original), so re-asserting it is free.
                        ApplyFontScaleGated(__instance, unityCloneFont, unityCloneName, settingsFontName ?? fontName);
                        return;
                }

                // Detect missed SetFont: text was translated but HasCachedTranslation said no
                if (unityCloneFont != null && textValue != preTranslateText && componentType == "Unity")
                {
                    object curFont = TypeHelper.GetFont(__instance);
                    string curFontName = (curFont is UnityEngine.Object cfo) ? cfo.name : null;
                    if (!string.Equals(curFontName, unityCloneName, StringComparison.OrdinalIgnoreCase))
                    {
                        // SetFont was missed — apply it now
                        TypeHelper.SetFont(__instance, unityCloneFont);
                        if (_dbgMissedSetFont < 10)
                        {
                            _dbgMissedSetFont++;
                            TranslatorCore.LogInfo($"[MISSED-SETFONT] comp={compId} text='{(preTranslateText.Length > 30 ? preTranslateText.Substring(0,30) : preTranslateText)}' HasCached was false but translated!");
                        }
                    }
                }

                // Ensure chars are in the clone's atlas — always when a clone is active.
                if (unityCloneFont != null && !string.IsNullOrEmpty(textValue))
                {
                    FontManager.EnsureCharsInCloneAtlasDirect(textValue, unityCloneFont, unityCloneFallback);
                }

                if (profiling) { t4 = _profSw.ElapsedTicks; _profTranslate += t4 - t3; }

                // Apply font scale only if the component has the clone font.
                // Scaling on the original font before clone is applied causes size
                // cumulation when the clone is applied later.
                ApplyFontScaleGated(__instance, unityCloneFont, unityCloneName, settingsFontName ?? fontName);

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
                        TranslatorCore.LogDebug($"[PERF] {_profCallCount} calls in 5s | " +
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
        private static int _dbgMissedSetFont = 0;
        private static int _dbgTouchLog = 0;
        public static bool BypassFontSizePrefix { get => _bypassFontSizePrefix; set => _bypassFontSizePrefix = value; }

        /// <summary>
        /// Postfix on TMP_Text.font setter: the game assigned a font on a component.
        /// Delegates to FontManager which re-applies our replacement when relevant
        /// (reverted replaced component, or a component using a font that has a
        /// configured fallback). Our own SetFont calls echo here too and no-op fast.
        /// </summary>
        public static void TMPText_SetFont_Postfix(object __instance)
        {
            if (__instance == null) return;

            try
            {
                if (!TranslatorCore.FontReplacementActive) return;
                if (TypeHelper.UseAlternateTMP) return; // TMProOld uses the fallback-list path
                FontManager.OnGameAssignedFont(__instance);
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[Patches] SetFont postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Font name for the fontSize prefixes, memoized in _fontNameCache.
        /// These prefixes used to bail out on any component absent from that cache, i.e. any
        /// component the game sized BEFORE its text first went through set_text. Whether a row got
        /// its scale then depended on the order the game happened to assign text and size — inside
        /// one list some rows were scaled and others not. Resolving (and caching) the font here
        /// makes it order-independent; the scale==1 test below still shorts out every font the user
        /// never configured, so unrelated components cost one reflection call, once.
        /// </summary>
        private static string ResolveFontNameForSizePrefix(object instance, int instanceId)
        {
            if (_fontNameCache.TryGetValue(instanceId, out string cached))
                return cached;

            string resolved = null;
            var fontObj = TypeHelper.GetFont(instance);
            if (fontObj is UnityEngine.Object uobj)
                resolved = uobj.name;
            // Cached even when null: a component without a readable font must not pay the
            // reflection on every size assignment (animated sizes hit this per frame).
            _fontNameCache[instanceId] = resolved;
            return resolved;
        }

        public static void TMPText_SetFontSize_Prefix(object __instance, ref float value)
        {
            if (_bypassFontSizePrefix) return;
            if (__instance == null) return;

            try
            {
                int instanceId = TypeHelper.GetInstanceID(__instance);
                if (instanceId == -1) return;

                if (TranslatorCore.ShouldSkipTranslation(instanceId)) return;

                string fontName = ResolveFontNameForSizePrefix(__instance, instanceId);

                if (string.IsNullOrEmpty(fontName)) return;

                if (IsOwnUIText(__instance)) return;

                string settingsFontName = FontManager.GetSettingsFontName(instanceId, fontName);

                // Always record the game's fontSize as the TRUE original — even when no scale is
                // active yet — so a later runtime scale change rescales from the real base. Without
                // this, toggling font replacement ON at runtime (which caches the design-scale only
                // then) left the true base uncaptured and ApplyFontScale read back an already-replaced
                // value → the text shrank until a restart (issue #21). Our own sets bypass this prefix,
                // so `value` is always the game's intended size.
                _trueOriginalFontSizes[instanceId] = value;

                float scale = FontManager.GetFontScale(settingsFontName, instanceId);
                if (Math.Abs(scale - 1.0f) < 0.001f) return;

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
                if (TranslatorCore.ShouldSkipTranslation(instanceId)) return;
                string fontName = ResolveFontNameForSizePrefix(__instance, instanceId);
                if (string.IsNullOrEmpty(fontName)) return;
                string settingsFontName = FontManager.GetSettingsFontName(instanceId, fontName);
                // Always record the true original (see TMPText_SetFontSize_Prefix) so a runtime
                // scale change rescales from the real base, not an already-replaced value (issue #21).
                _trueOriginalFontSizes[instanceId] = value;
                float scale = FontManager.GetFontScale(settingsFontName, instanceId);
                if (Math.Abs(scale - 1.0f) < 0.001f) return;
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
        // Drained once and for all by OnUIInitialized (Clear right after collecting), so it holds
        // entries only during the window before the UI exists — nothing to clean elsewhere.
        private static Dictionary<int, (WeakReference instance, string fontName, string originalText)> _pendingFontReplacements = new Dictionary<int, (WeakReference, string, string)>();

        /// <summary>
        /// Register a callback for when UI is initialized.
        /// </summary>
        private static void RegisterInitCallback()
        {
            if (_initCallbackRegistered) return;
            _initCallbackRegistered = true;

            UI.TranslatorUIManager.OnInitialized += OnUIInitialized;
            TranslatorCore.LogDebug("[AlternateTMP] Registered init callback for pending font replacements");
        }

        /// <summary>
        /// Called when UniverseLib UI is fully initialized.
        /// Schedule delayed replay for pending components to ensure Unity state is stable.
        /// </summary>
        private static void OnUIInitialized()
        {
            TranslatorCore.LogDebug($"[AlternateTMP] UI initialized - scheduling {_pendingFontReplacements.Count} pending text operations");

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
            TranslatorCore.LogDebug($"[AlternateTMP] Processing {toProcess.Count} pending font replacements");

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

                    TranslatorCore.LogDebug($"[AlternateTMP] Processing: '{(originalText.Length > 40 ? originalText.Substring(0, 40) + "..." : originalText)}' with font '{fontName}'");

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
                        TranslatorCore.LogDebug($"[AlternateTMP] After processing, text is: '{(resultText.Length > 40 ? resultText.Substring(0, 40) + "..." : resultText)}'");
                    }
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogWarning($"[AlternateTMP] Failed to process pending: {ex.Message}");
                }
            }

            TranslatorCore.LogDebug($"[AlternateTMP] Completed processing {toProcess.Count} pending text operations");
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
                float scale = IsOwnUIText(instance) ? 1f : FontManager.GetFontScale(originalFontName, instId);
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
                    string customFontName = FontManager.StripFontPrefix(fallbackName);

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

        // Track fonts that already have our custom fallback added.
        // Keyed by FONT, not by component: bounded by how many fonts the game ships, so it is not
        // cleaned anywhere and does not need to be.
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
                    TranslatorCore.LogDebug("[AlternateTMP] Fallback already added to this font");
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
                        TranslatorCore.LogDebug($"[AlternateTMP] Added custom font to fallbackFontAssets list");
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
                        TranslatorCore.LogDebug($"[AlternateTMP] Added custom font to fallbackFontAssetTable");
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
                            TranslatorCore.LogDebug($"[FontManager] Found alternate TMP font: {unityObj.name}");
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
            // Our own write (render repair, mod UI) — never translate or track it. This replaces
            // _skipTextResetInstances, a HashSet that expressed the same intent here and was read
            // but never filled, so the protection it announced never once ran.
            if (BypassTextPrefix) return;
            if (!TranslatorCore.TranslationsActive) return;
            try
            {
                int instanceId = __instance.GetHashCode();

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
                                TranslatorCore.LogDebug($"[AlternateTMP] Queued for font+translation after init: '{fontName}'");
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

        // Track instances currently being processed to avoid recursion.
        // Scoped: added on entry and removed in the finally of AlternateTMP_SetFont_Postfix, so an
        // exception cannot leave an id behind. Nothing to clean elsewhere.
        private static HashSet<int> _fontSetInProgress = new HashSet<int>();

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
            if (BypassTextPrefix) return;
            if (!TranslatorCore.TranslationsActive) return;
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
            if (!TranslatorCore.TranslationsActive) return;
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
