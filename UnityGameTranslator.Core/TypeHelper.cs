using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Centralized type resolution for Unity/TMPro types via reflection.
    /// Avoids direct compile-time references to TMPro/UI types that crash on IL2CPP
    /// when the standard assemblies aren't compatible with IL2CPP proxy types.
    ///
    /// Pattern inspired by CustomFontLoader.InitializeTypes() — resolve once, cache forever.
    /// </summary>
    public static class TypeHelper
    {
        private static bool _initialized = false;

        #region Resolved Types

        /// <summary>TMPro.TMP_Text or TMProOld.TMP_Text</summary>
        public static Type TMP_TextType { get; private set; }

        /// <summary>UnityEngine.UI.Text</summary>
        public static Type UI_TextType { get; private set; }

        /// <summary>UnityEngine.TextMesh</summary>
        public static Type TextMeshType { get; private set; }

        /// <summary>TMPro.TMP_FontAsset or TMProOld.TMP_FontAsset</summary>
        public static Type TMP_FontAssetType { get; private set; }

        /// <summary>TMPro.TMP_InputField or TMProOld.TMP_InputField</summary>
        public static Type TMP_InputFieldType { get; private set; }

        /// <summary>UnityEngine.UI.InputField</summary>
        public static Type UI_InputFieldType { get; private set; }

        /// <summary>UnityEngine.Font</summary>
        public static Type FontType { get; private set; }

        /// <summary>Whether we're using TMProOld namespace instead of TMPro</summary>
        public static bool UseAlternateTMP { get; private set; }

        #endregion

        #region Cached PropertyInfo / MethodInfo

        // TMP_Text properties
        public static PropertyInfo TMP_TextProp { get; private set; }      // .text
        public static PropertyInfo TMP_FontProp { get; private set; }      // .font
        public static PropertyInfo TMP_FontSizeProp { get; private set; }  // .fontSize

        // UI.Text properties
        public static PropertyInfo UI_TextProp { get; private set; }       // .text
        public static PropertyInfo UI_FontProp { get; private set; }       // .font
        public static PropertyInfo UI_FontSizeProp { get; private set; }   // .fontSize

        // TextMesh properties
        public static PropertyInfo TextMesh_TextProp { get; private set; }  // .text
        public static PropertyInfo TextMesh_FontProp { get; private set; }  // .font

        // TMP_InputField.textComponent
        public static PropertyInfo TMP_InputField_TextComponentProp { get; private set; }

        // UI InputField.textComponent
        public static PropertyInfo UI_InputField_TextComponentProp { get; private set; }

        // TMP_InputField.text / UI InputField.text (current typed value)
        public static PropertyInfo TMP_InputField_TextProp { get; private set; }
        public static PropertyInfo UI_InputField_TextProp { get; private set; }

        // TMP_Text.ForceMeshUpdate()
        public static MethodInfo TMP_ForceMeshUpdateMethod { get; private set; }

        #endregion

        /// <summary>
        /// Initialize all type references via reflection.
        /// Must be called early in mod initialization (before patches or scanning).
        /// Safe to call multiple times — only initializes once.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                ResolveTypes();
                ResolveProperties();
                LogResults();
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[TypeHelper] Initialization error: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-attempt type resolution for types that weren't found on first pass.
        /// On IL2CPP, assemblies may be loaded lazily after initial init.
        /// Call this before patches or scanning if TMP types are still null.
        /// </summary>
        public static void TryResolveIfNeeded()
        {
            if (TMP_TextType != null) return; // Already resolved

            try
            {
                TranslatorCore.LogInfo("[TypeHelper] Re-attempting TMP type resolution (late-loaded assemblies)...");
                ResolveTypes();
                if (TMP_TextType != null)
                {
                    ResolveProperties();
                    LogResults();
                }
                else
                {
                    TranslatorCore.LogWarning("[TypeHelper] TMP types still not found after re-scan");
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[TypeHelper] Re-resolve error: {ex.Message}");
            }
        }

        private static void ResolveTypes()
        {
            // UI.Text and InputField - standard Unity types
            if (UI_TextType == null)
                UI_TextType = FindType("UnityEngine.UI.Text");
            if (UI_InputFieldType == null)
                UI_InputFieldType = FindType("UnityEngine.UI.InputField");

            // TextMesh - legacy 3D text
            if (TextMeshType == null)
                TextMeshType = typeof(TextMesh);

            // Font
            if (FontType == null)
                FontType = typeof(Font);

            // TMP types - already resolved?
            if (TMP_TextType != null) return;

            // Prefer TMProOld (alternate TMP), fallback to TMPro
            Type tmpOldText = FindType("TMProOld.TMP_Text");
            Type tmpOldFontAsset = FindType("TMProOld.TMP_FontAsset");
            Type tmpOldInputField = FindType("TMProOld.TMP_InputField");

            if (tmpOldText != null)
            {
                TMP_TextType = tmpOldText;
                TMP_FontAssetType = tmpOldFontAsset;
                TMP_InputFieldType = tmpOldInputField;
                UseAlternateTMP = true;
                TranslatorCore.LogInfo("[TypeHelper] Using TMProOld types");
                return;
            }

            // Standard TMPro namespace
            TMP_TextType = FindType("TMPro.TMP_Text");
            if (TMP_TextType != null)
            {
                TMP_FontAssetType = FindType("TMPro.TMP_FontAsset");
                TMP_InputFieldType = FindType("TMPro.TMP_InputField");
                UseAlternateTMP = false;
                return;
            }

            // IL2CPP: types may be in Il2Cpp-prefixed assemblies but keep their original namespace.
            // Or they may have Il2Cpp-prefixed type names. Try common IL2CPP patterns.
            // On MelonLoader IL2CPP, the type might be in assembly "Il2CppTMPro" but namespace is still "TMPro"
            // FindType already scans all assemblies, so if the namespace is "TMPro" it would have been found above.
            // The issue is the assemblies may not be loaded yet at init time.
            // Log all loaded assemblies containing "TMP" for diagnostics.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string asmName = asm.GetName().Name;
                    if (asmName.IndexOf("TMP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        asmName.IndexOf("TextMesh", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        TranslatorCore.LogInfo($"[TypeHelper] Found TMP-related assembly: {asmName}");
                        // Try to find TMP_Text in this assembly
                        foreach (var type in asm.GetTypes())
                        {
                            if (type.Name == "TMP_Text" && TMP_TextType == null)
                            {
                                TMP_TextType = type;
                                TranslatorCore.LogInfo($"[TypeHelper] Found TMP_Text: {type.FullName} in {asmName}");
                            }
                            else if (type.Name == "TMP_FontAsset" && TMP_FontAssetType == null)
                            {
                                TMP_FontAssetType = type;
                            }
                            else if (type.Name == "TMP_InputField" && TMP_InputFieldType == null)
                            {
                                TMP_InputFieldType = type;
                            }
                        }
                    }
                }
                catch { }
            }

            UseAlternateTMP = false;
        }

        private static void ResolveProperties()
        {
            var pubInst = BindingFlags.Public | BindingFlags.Instance;

            // TMP_Text
            if (TMP_TextType != null)
            {
                TMP_TextProp = TMP_TextType.GetProperty("text", pubInst);
                TMP_FontProp = TMP_TextType.GetProperty("font", pubInst);
                TMP_FontSizeProp = TMP_TextType.GetProperty("fontSize", pubInst);
                TMP_ForceMeshUpdateMethod = TMP_TextType.GetMethod("ForceMeshUpdate", pubInst, null, Type.EmptyTypes, null);
            }

            // UI.Text
            if (UI_TextType != null)
            {
                UI_TextProp = UI_TextType.GetProperty("text", pubInst);
                UI_FontProp = UI_TextType.GetProperty("font", pubInst);
                UI_FontSizeProp = UI_TextType.GetProperty("fontSize", pubInst);
            }

            // TextMesh
            if (TextMeshType != null)
            {
                TextMesh_TextProp = TextMeshType.GetProperty("text", pubInst);
                TextMesh_FontProp = TextMeshType.GetProperty("font", pubInst);
            }

            // TMP_InputField.textComponent + .text
            if (TMP_InputFieldType != null)
            {
                TMP_InputField_TextComponentProp = TMP_InputFieldType.GetProperty("textComponent", pubInst);
                TMP_InputField_TextProp = TMP_InputFieldType.GetProperty("text", pubInst);
            }

            // UI InputField.textComponent + .text
            if (UI_InputFieldType != null)
            {
                UI_InputField_TextComponentProp = UI_InputFieldType.GetProperty("textComponent", pubInst);
                UI_InputField_TextProp = UI_InputFieldType.GetProperty("text", pubInst);
            }
        }

        private static void LogResults()
        {
            TranslatorCore.LogInfo($"[TypeHelper] Types resolved: TMP_Text={TMP_TextType != null}, UI.Text={UI_TextType != null}, TextMesh={TextMeshType != null}");
            TranslatorCore.LogInfo($"[TypeHelper] TMP_FontAsset={TMP_FontAssetType != null}, TMP_InputField={TMP_InputFieldType != null}, UI.InputField={UI_InputFieldType != null}");
        }

        #region Type Helper Methods

        /// <summary>
        /// Find a type by full name across all loaded assemblies.
        /// </summary>
        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(fullName);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Returns the component type category: "TMP", "Unity", "TextMesh", or null.
        /// </summary>
        public static string GetComponentType(object component)
        {
            if (component == null) return null;
            var type = component.GetType();

            if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type))
                return "TMP";
            if (UI_TextType != null && UI_TextType.IsAssignableFrom(type))
                return "Unity";
            if (TextMeshType != null && TextMeshType.IsAssignableFrom(type))
                return "TextMesh";

            return null;
        }

        #endregion

        #region Property Accessors

        /// <summary>
        /// Get the font name from a text component (TMP, UI.Text, or TextMesh).
        /// Returns null if component is null or font is not accessible.
        /// </summary>
        public static string GetFontName(object component)
        {
            if (component == null) return null;

            try
            {
                object font = GetFont(component);
                if (font is UnityEngine.Object unityObj)
                    return unityObj.name;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Get the font object from a text component.
        /// Returns TMP_FontAsset for TMP, Font for UI.Text/TextMesh.
        /// Uses cached PropertyInfo first, falls back to instance type reflection.
        /// </summary>
        public static object GetFont(object component)
        {
            if (component == null) return null;

            try
            {
                // Try cached PropertyInfo first (fast path)
                var type = component.GetType();

                if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type) && TMP_FontProp != null)
                    return TMP_FontProp.GetValue(component, null);

                if (UI_TextType != null && UI_TextType.IsAssignableFrom(type) && UI_FontProp != null)
                    return UI_FontProp.GetValue(component, null);

                if (TextMeshType != null && TextMeshType.IsAssignableFrom(type) && TextMesh_FontProp != null)
                    return TextMesh_FontProp.GetValue(component, null);

                // Fallback: look up "font" property on the actual instance type
                // Handles cases where TypeHelper resolved a different type (e.g. TMProOld vs TMPro)
                var fontProp = type.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
                if (fontProp != null)
                    return fontProp.GetValue(component, null);
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Set the font on a text component.
        /// </summary>
        public static void SetFont(object component, object font)
        {
            if (component == null || font == null) return;

            try
            {
                var type = component.GetType();
                PropertyInfo prop = null;

                // Find the right font property
                if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type) && TMP_FontProp != null && TMP_FontProp.CanWrite)
                    prop = TMP_FontProp;
                else if (UI_TextType != null && UI_TextType.IsAssignableFrom(type) && UI_FontProp != null && UI_FontProp.CanWrite)
                    prop = UI_FontProp;
                else if (TextMeshType != null && TextMeshType.IsAssignableFrom(type) && TextMesh_FontProp != null && TextMesh_FontProp.CanWrite)
                    prop = TextMesh_FontProp;

                // Fallback: look up "font" property on actual instance type
                if (prop == null)
                    prop = type.GetProperty("font", BindingFlags.Public | BindingFlags.Instance);

                if (prop == null || !prop.CanWrite)
                {
                    TranslatorCore.LogWarning($"[TypeHelper] SetFont failed: no writable font property on {type.Name}");
                    return;
                }

                // On IL2CPP, the font object must be cast to the exact property type
                // using TryCast<T>(). Direct assignment of UnityEngine.Object to
                // Il2CppTMPro.TMP_FontAsset fails without proper IL2CPP casting.
                var expectedType = prop.PropertyType;
                var castedFont = Il2CppCast(font, expectedType);
                prop.SetValue(component, castedFont, null);
            }
            catch (Exception ex)
            {
                // Log only once per error type to avoid spam
                if (!_setFontErrorLogged)
                {
                    _setFontErrorLogged = true;
                    TranslatorCore.LogWarning($"[TypeHelper] SetFont error on {component.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static bool _setFontErrorLogged = false;

        /// <summary>
        /// Get fontSize from a text component (TMP_Text or UI.Text).
        /// Returns -1 if not accessible.
        /// </summary>
        public static float GetFontSize(object component)
        {
            if (component == null) return -1f;

            try
            {
                var type = component.GetType();
                PropertyInfo prop = null;

                if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type))
                    prop = TMP_FontSizeProp;
                else if (UI_TextType != null && UI_TextType.IsAssignableFrom(type))
                    prop = UI_FontSizeProp;

                // Fallback: look up on actual type
                if (prop == null)
                    prop = type.GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance);

                if (prop != null)
                {
                    var val = prop.GetValue(component, null);
                    return Convert.ToSingle(val);
                }
            }
            catch { }

            return -1f;
        }

        /// <summary>
        /// Set fontSize on a text component (TMP_Text or UI.Text).
        /// </summary>
        public static void SetFontSize(object component, float size)
        {
            if (component == null) return;

            try
            {
                var type = component.GetType();
                PropertyInfo prop = null;

                if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type))
                    prop = TMP_FontSizeProp;
                else if (UI_TextType != null && UI_TextType.IsAssignableFrom(type))
                    prop = UI_FontSizeProp;

                // Fallback
                if (prop == null)
                    prop = type.GetProperty("fontSize", BindingFlags.Public | BindingFlags.Instance);

                if (prop != null && prop.CanWrite)
                {
                    // Set with the correct type (float for TMP, int for UI.Text)
                    var propType = prop.PropertyType;
                    if (propType == typeof(int))
                        prop.SetValue(component, (int)Math.Round(size), null);
                    else
                        prop.SetValue(component, size, null);
                    return;
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TypeHelper] SetFontSize error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the text color from a TMP or UI.Text component.
        /// </summary>
        public static Color GetTextColor(object component)
        {
            if (component == null) return Color.white;
            try
            {
                var type = component.GetType();
                var colorProp = type.GetProperty("color", BindingFlags.Public | BindingFlags.Instance);
                if (colorProp != null && colorProp.CanRead)
                {
                    var val = colorProp.GetValue(component, null);
                    if (val is Color c) return c;
                }
            }
            catch { }
            return Color.white;
        }

        /// <summary>
        /// Set the text color on a TMP or UI.Text component.
        /// </summary>
        public static void SetTextColor(object component, Color color)
        {
            if (component == null) return;
            try
            {
                var type = component.GetType();
                var colorProp = type.GetProperty("color", BindingFlags.Public | BindingFlags.Instance);
                if (colorProp != null && colorProp.CanWrite)
                {
                    colorProp.SetValue(component, color, null);
                }
            }
            catch { }
        }

        /// <summary>
        /// Get the text value from a component.
        /// </summary>
        public static string GetText(object component)
        {
            if (component == null) return null;

            try
            {
                var type = component.GetType();

                if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type) && TMP_TextProp != null)
                    return TMP_TextProp.GetValue(component, null) as string;

                if (UI_TextType != null && UI_TextType.IsAssignableFrom(type) && UI_TextProp != null)
                    return UI_TextProp.GetValue(component, null) as string;

                if (TextMeshType != null && TextMeshType.IsAssignableFrom(type) && TextMesh_TextProp != null)
                    return TextMesh_TextProp.GetValue(component, null) as string;

                // Fallback: look up "text" property on actual instance type
                var textProp = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (textProp != null)
                    return textProp.GetValue(component, null) as string;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Set the text value on a component.
        /// </summary>
        public static void SetText(object component, string text)
        {
            if (component == null) return;

            try
            {
                var type = component.GetType();

                if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type) && TMP_TextProp != null && TMP_TextProp.CanWrite)
                {
                    var setter = TMP_TextProp.SetMethod ?? TMP_TextProp.GetSetMethod();
                    if (setter != null)
                        setter.Invoke(component, new object[] { text });
                    else
                        TMP_TextProp.SetValue(component, text, null);
                    return;
                }

                if (UI_TextType != null && UI_TextType.IsAssignableFrom(type) && UI_TextProp != null && UI_TextProp.CanWrite)
                {
                    // Use SetMethod.Invoke instead of SetValue to trigger Harmony patches on IL2CPP.
                    // PropertyInfo.SetValue can bypass IL2CPP managed wrappers → Harmony prefix not called.
                    var setter = UI_TextProp.SetMethod ?? UI_TextProp.GetSetMethod();
                    if (setter != null)
                        setter.Invoke(component, new object[] { text });
                    else
                        UI_TextProp.SetValue(component, text, null);
                    return;
                }

                if (TextMeshType != null && TextMeshType.IsAssignableFrom(type) && TextMesh_TextProp != null && TextMesh_TextProp.CanWrite)
                {
                    var setter = TextMesh_TextProp.SetMethod ?? TextMesh_TextProp.GetSetMethod();
                    if (setter != null)
                        setter.Invoke(component, new object[] { text });
                    else
                        TextMesh_TextProp.SetValue(component, text, null);
                    return;
                }

                // Fallback
                var textProp = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (textProp != null && textProp.CanWrite)
                {
                    textProp.SetValue(component, text, null);
                    return;
                }
            }
            catch { }
        }

        /// <summary>
        /// Check if a text component is the textComponent of an InputField (should not
        /// be translated). Works for both TMP_InputField and UI.InputField. The parent
        /// lookup walks the transform chain manually (GetComponentInParent skips
        /// inactive hierarchies — the first set_text often fires while a menu is still
        /// being built inactive, and a cached miss there was permanent).
        /// </summary>
        public static bool IsInputFieldTextComponent(object textComponent)
        {
            var comp = textComponent as Component;
            if (comp == null) return false;

            var input = FindParentInputField(comp);
            if (input == null) return false;

            return IsTextComponentOfInputField(input, textComponent);
        }

        /// <summary>
        /// Identity check "is this the input's wired textComponent", by InstanceID:
        /// on IL2CPP two interop wrappers for the same native object fail ReferenceEquals.
        /// </summary>
        public static bool IsTextComponentOfInputField(object inputField, object textComponent)
        {
            try
            {
                var wired = GetInputFieldTextComponent(inputField);
                if (wired == null) return false;
                int a = GetInstanceID(wired);
                int b = GetInstanceID(textComponent);
                return a != -1 && a == b;
            }
            catch { return false; }
        }

        /// <summary>
        /// Find an InputField or TMP_InputField on this component's GameObject or any
        /// parent. Manual transform walk, works on inactive hierarchies.
        /// </summary>
        public static object FindParentInputField(Component component)
        {
            if (component == null) return null;

            try
            {
                var t = component.transform;
                while (t != null)
                {
                    if (TMP_InputFieldType != null)
                    {
                        var field = GetComponentOfType(t, TMP_InputFieldType);
                        if (field != null && IsUnityObjectAlive(field)) return field;
                    }
                    if (UI_InputFieldType != null)
                    {
                        var field = GetComponentOfType(t, UI_InputFieldType);
                        if (field != null && IsUnityObjectAlive(field)) return field;
                    }
                    t = t.parent;
                }
            }
            catch { }

            return null;
        }

        /// <summary>Current typed value of an InputField / TMP_InputField (null if unavailable).</summary>
        public static string GetInputFieldText(object inputField)
        {
            if (inputField == null) return null;

            try
            {
                var type = inputField.GetType();
                PropertyInfo prop = null;
                if (TMP_InputFieldType != null && TMP_InputFieldType.IsAssignableFrom(type))
                    prop = TMP_InputField_TextProp;
                else if (UI_InputFieldType != null && UI_InputFieldType.IsAssignableFrom(type))
                    prop = UI_InputField_TextProp;
                if (prop == null)
                    prop = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);

                return prop != null ? prop.GetValue(inputField, null) as string : null;
            }
            catch { }

            return null;
        }

        /// <summary>The textComponent wired on an InputField / TMP_InputField.</summary>
        public static object GetInputFieldTextComponent(object inputField)
        {
            if (inputField == null) return null;

            try
            {
                var type = inputField.GetType();
                PropertyInfo prop = null;
                if (TMP_InputFieldType != null && TMP_InputFieldType.IsAssignableFrom(type))
                    prop = TMP_InputField_TextComponentProp;
                else if (UI_InputFieldType != null && UI_InputFieldType.IsAssignableFrom(type))
                    prop = UI_InputField_TextComponentProp;
                if (prop == null)
                    prop = type.GetProperty("textComponent", BindingFlags.Public | BindingFlags.Instance);

                return prop != null ? prop.GetValue(inputField, null) : null;
            }
            catch { }

            return null;
        }

        // Cached GetComponent lookups: Mono exposes GetComponent(Type); IL2CPP proxies
        // only expose the generic version, closed via reflection and cached per type.
        private static MethodInfo _getComponentTypeMethod;
        private static bool _getComponentTypeMethodResolved;
        private static MethodInfo _getComponentGenericDef;
        private static readonly Dictionary<Type, MethodInfo> _getComponentClosedCache = new Dictionary<Type, MethodInfo>();

        /// <summary>GetComponent(searchType) working on both Mono and IL2CPP.</summary>
        public static object GetComponentOfType(Component target, Type searchType)
        {
            if (target == null || searchType == null) return null;

            try
            {
                if (!_getComponentTypeMethodResolved)
                {
                    _getComponentTypeMethodResolved = true;
                    _getComponentTypeMethod = typeof(Component).GetMethod("GetComponent",
                        BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(Type) }, null);
                }
                if (_getComponentTypeMethod != null)
                    return _getComponentTypeMethod.Invoke(target, new object[] { searchType });

                if (!_getComponentClosedCache.TryGetValue(searchType, out var closed))
                {
                    if (_getComponentGenericDef == null)
                    {
                        foreach (var m in typeof(Component).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (m.Name == "GetComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                            {
                                _getComponentGenericDef = m;
                                break;
                            }
                        }
                    }
                    closed = _getComponentGenericDef != null ? _getComponentGenericDef.MakeGenericMethod(searchType) : null;
                    _getComponentClosedCache[searchType] = closed;
                }
                if (closed != null)
                    return closed.Invoke(target, null);
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Call ForceMeshUpdate() on a TMP component via reflection.
        /// </summary>
        public static void ForceMeshUpdate(object component)
        {
            if (component == null) return;

            try
            {
                if (TMP_ForceMeshUpdateMethod != null)
                {
                    TMP_ForceMeshUpdateMethod.Invoke(component, null);
                    return;
                }

                // Fallback: try by type
                var type = component.GetType();
                var method = type.GetMethod("ForceMeshUpdate",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                method?.Invoke(component, null);
            }
            catch { }
        }

        /// <summary>
        /// Force a FULL TMP re-generation with text reparsing via ForceMeshUpdate(ignoreActiveState,
        /// forceTextReparsing). Unlike the parameterless overload (which reuses the cached parse and
        /// auto-size result), forceTextReparsing=true makes TMP re-run auto-sizing — needed after we
        /// change fontSizeMax/Min so the component re-fits within the new bounds (issue #21: runtime
        /// font toggle left auto-sized text at its old settled size). Falls back to the plain call.
        /// </summary>
        public static void ForceMeshUpdateReparse(object component)
        {
            if (component == null) return;
            try
            {
                var type = component.GetType();
                var m2 = type.GetMethod("ForceMeshUpdate",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(bool), typeof(bool) }, null);
                if (m2 != null)
                {
                    // ignoreActiveState = false, forceTextReparsing = true
                    m2.Invoke(component, new object[] { false, true });
                    return;
                }
            }
            catch { }
            ForceMeshUpdate(component);
        }

        /// <summary>
        /// Get the InstanceID from a component (any Unity Object).
        /// Returns -1 if not a Unity Object.
        /// </summary>
        public static int GetInstanceID(object component)
        {
            if (component is UnityEngine.Object unityObj)
                return unityObj.GetInstanceID();
            return -1;
        }

        /// <summary>
        /// IL2CPP-safe liveness check for Unity objects. A managed wrapper can outlive
        /// its native object (destroyed or unloaded by the game): any member access on
        /// the dead wrapper (e.g. <c>.name</c>) then throws. Unity's overloaded
        /// <c>==</c> operator detects the destroyed native side on both Mono and
        /// IL2CPP interop, without touching the object's members.
        /// Non-Unity objects are considered alive if non-null.
        /// </summary>
        public static bool IsUnityObjectAlive(object obj)
        {
            if (obj == null) return false;
            if (obj is UnityEngine.Object unityObj)
            {
                try { return unityObj != null; }
                catch { return false; }
            }
            return true;
        }

        /// <summary>
        /// Check if an object is of a given type (null-safe).
        /// </summary>
        public static bool IsOfType(object obj, Type type)
        {
            if (obj == null || type == null) return false;
            return type.IsInstanceOfType(obj);
        }

        /// <summary>
        /// Toggle enabled state on a Component (for forcing visual refresh).
        /// </summary>
        public static void ToggleEnabled(object component)
        {
            if (component == null) return;

            try
            {
                var type = component.GetType();
                var enabledProp = type.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
                if (enabledProp != null && enabledProp.CanWrite && enabledProp.CanRead)
                {
                    bool current = (bool)enabledProp.GetValue(component, null);
                    enabledProp.SetValue(component, !current, null);
                    enabledProp.SetValue(component, current, null);
                }
            }
            catch { }
        }

        /// <summary>
        /// UNIVERSAL RENDER-HEALTH detector (issue #21). Reads per-glyph vertex alpha from
        /// textInfo.meshInfo (one quad = 4 verts per RENDERED glyph) and counts:
        ///   visible = rendered glyphs, hidden = glyphs whose alpha is ~0 (invisible).
        /// A game typewriter reveal that stalls on our async text change leaves glyphs
        /// stuck at alpha 0 → hidden &gt; 0 once the animation settles. Game- and
        /// language-agnostic: it only looks at what the mesh actually draws. (Quad HEIGHT
        /// varies naturally between glyphs — 'e' vs 'H' — so it is NOT used as a signal.)
        /// Returns false when meshInfo is unavailable.
        /// </summary>
        public static bool GetRenderHealth(object component, out int visible, out int hidden)
        {
            visible = 0; hidden = 0;
            if (component == null) return false;
            try
            {
                var ti = component.GetType().GetProperty("textInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(component, null);
                var mi = ti?.GetType().GetProperty("meshInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ti, null);
                if (mi == null) return false;
                var miLenP = mi.GetType().GetProperty("Length") ?? mi.GetType().GetProperty("Count");
                int miLen = miLenP != null ? Convert.ToInt32(miLenP.GetValue(mi, null)) : 0;
                var miItem = mi.GetType().GetProperty("Item");
                if (miItem == null || miLen <= 0) return false;

                for (int m = 0; m < miLen; m++)
                {
                    var info = miItem.GetValue(mi, new object[] { m });
                    if (info == null) continue;
                    var colors = info.GetType().GetProperty("colors32", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info, null);
                    if (colors == null) continue;
                    // vertexCount = USED verts (TMP over-allocates the buffer; the padding
                    // would otherwise be counted as hidden glyphs).
                    var vcP = info.GetType().GetProperty("vertexCount", BindingFlags.Public | BindingFlags.Instance);
                    int n = vcP != null ? Convert.ToInt32(vcP.GetValue(info, null)) : 0;
                    var cItem = colors.GetType().GetProperty("Item");
                    if (cItem == null || n <= 0) continue;

                    for (int q = 0; q + 3 < n; q += 4)
                    {
                        int aSum = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            var col = cItem.GetValue(colors, new object[] { q + k });
                            aSum += Convert.ToInt32(col.GetType().GetField("a").GetValue(col));
                        }
                        if (aSum / 4 < 51) hidden++;
                        visible++;
                    }
                }
                return visible > 0;
            }
            catch { return false; }
        }

        private static MethodInfo _updateVertexDataMethod;
        private static bool _updateVertexDataResolved;

        /// <summary>
        /// DIAGNOSTIC (issue #21): compare the max glyph quad HEIGHT as laid out in
        /// characterInfo (TMP's reference) vs as drawn in meshInfo. If characterInfo is a
        /// clean full-scale reference, layoutMaxH &gt; meshMaxH on a scale-broken text; if
        /// the game corrupts characterInfo too, they match (both small) → no usable
        /// reference to restore scale from.
        /// </summary>
        public static void GetLayoutMeshHeights(object component, out float layoutMaxH, out float meshMaxH)
        {
            layoutMaxH = -1f; meshMaxH = -1f;
            if (component == null) return;
            try
            {
                var ti = component.GetType().GetProperty("textInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(component, null);
                if (ti == null) return;
                var mi = ti.GetType().GetProperty("meshInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ti, null);
                var ci = ti.GetType().GetProperty("characterInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ti, null);
                int charCount = Convert.ToInt32(ti.GetType().GetProperty("characterCount", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ti, null) ?? 0);
                if (mi == null || ci == null || charCount <= 0) return;
                var ciItem = ci.GetType().GetProperty("Item");
                var miItem = mi.GetType().GetProperty("Item");
                if (ciItem == null || miItem == null) return;

                float lMax = 0f, mMax = 0f;
                for (int i = 0; i < charCount; i++)
                {
                    var cinfo = ciItem.GetValue(ci, new object[] { i });
                    if (cinfo == null) continue;
                    var cit = cinfo.GetType();
                    var visF = cit.GetField("isVisible");
                    if (visF != null && !Convert.ToBoolean(visF.GetValue(cinfo))) continue;

                    var bl = cit.GetField("vertex_BL")?.GetValue(cinfo);
                    var tl = cit.GetField("vertex_TL")?.GetValue(cinfo);
                    var blP = bl?.GetType().GetField("position")?.GetValue(bl);
                    var tlP = tl?.GetType().GetField("position")?.GetValue(tl);
                    if (blP != null && tlP != null)
                    {
                        float h = Convert.ToSingle(tlP.GetType().GetField("y").GetValue(tlP)) - Convert.ToSingle(blP.GetType().GetField("y").GetValue(blP));
                        if (h > lMax) lMax = h;
                    }

                    int matRef = Convert.ToInt32((cit.GetField("materialReferenceIndex")?.GetValue(cinfo)) ?? 0);
                    int vIndex = Convert.ToInt32((cit.GetField("vertexIndex")?.GetValue(cinfo)) ?? -1);
                    if (vIndex < 0) continue;
                    var info = miItem.GetValue(mi, new object[] { matRef });
                    var verts = info?.GetType().GetProperty("vertices", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info, null);
                    var vItem = verts?.GetType().GetProperty("Item");
                    if (vItem == null) continue;
                    var v0 = vItem.GetValue(verts, new object[] { vIndex });      // BL
                    var v1 = vItem.GetValue(verts, new object[] { vIndex + 1 });  // TL
                    float mh = Convert.ToSingle(v1.GetType().GetField("y").GetValue(v1)) - Convert.ToSingle(v0.GetType().GetField("y").GetValue(v0));
                    if (mh > mMax) mMax = mh;
                }
                layoutMaxH = lMax; meshMaxH = mMax;
            }
            catch { }
        }

        /// <summary>
        /// UNIVERSAL RENDER REPAIR (issue #21). Force every rendered glyph opaque by
        /// writing alpha=255 into textInfo.meshInfo colours and pushing them with
        /// UpdateVertexData() — which uploads the mesh WITHOUT regenerating it, so it does
        /// NOT fire TMP's text-changed event and therefore does NOT re-trigger a game
        /// typewriter that re-applies its stalled reveal (unlike ForceMeshUpdate, which
        /// does regenerate and loses the race). Use only when GetRenderHealth reports a
        /// settled hidden&gt;0. Returns true when it wrote and pushed.
        /// </summary>
        public static bool ForceVertexAlphaOpaque(object component)
        {
            if (component == null) return false;
            try
            {
                var ti = component.GetType().GetProperty("textInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(component, null);
                var mi = ti?.GetType().GetProperty("meshInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ti, null);
                if (mi == null) return false;
                var miLenP = mi.GetType().GetProperty("Length") ?? mi.GetType().GetProperty("Count");
                int miLen = miLenP != null ? Convert.ToInt32(miLenP.GetValue(mi, null)) : 0;
                var miItem = mi.GetType().GetProperty("Item");
                if (miItem == null || miLen <= 0) return false;

                bool wrote = false;
                for (int m = 0; m < miLen; m++)
                {
                    var info = miItem.GetValue(mi, new object[] { m });
                    if (info == null) continue;
                    var colors = info.GetType().GetProperty("colors32", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info, null);
                    if (colors == null) continue;
                    var vcP = info.GetType().GetProperty("vertexCount", BindingFlags.Public | BindingFlags.Instance);
                    int n = vcP != null ? Convert.ToInt32(vcP.GetValue(info, null)) : 0;
                    var cItem = colors.GetType().GetProperty("Item");
                    if (cItem == null || n <= 0) continue;
                    var aField = (FieldInfo)null;
                    for (int v = 0; v < n; v++)
                    {
                        var col = cItem.GetValue(colors, new object[] { v });
                        if (aField == null) aField = col.GetType().GetField("a");
                        if (aField == null) break;
                        if (Convert.ToInt32(aField.GetValue(col)) >= 255) continue;
                        aField.SetValue(col, (byte)255);         // mutate boxed Color32 copy
                        cItem.SetValue(colors, col, new object[] { v }); // write it back
                        wrote = true;
                    }
                }
                if (!wrote) return false;

                if (!_updateVertexDataResolved)
                {
                    _updateVertexDataResolved = true;
                    _updateVertexDataMethod = component.GetType().GetMethod("UpdateVertexData",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                }
                if (_updateVertexDataMethod != null)
                    _updateVertexDataMethod.Invoke(component, null);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// UNIVERSAL RENDER REPAIR — full version (issue #21). Restores BOTH alpha AND
        /// POSITION (scale) of every visible glyph from textInfo.characterInfo, which holds
        /// TMP's own full-scale layout (a game vertex effect modifies meshInfo, not this
        /// layout reference). Copies each character's 4 layout vertices into meshInfo and
        /// forces alpha opaque, then UpdateVertexData (no regeneration → no game re-trigger).
        /// Fixes the "garbled" case (some glyphs frozen at reduced scale) that the
        /// alpha-only repair leaves behind. Falls back to alpha-only when characterInfo
        /// can't be read on this runtime. Returns true when it wrote and pushed.
        /// </summary>
        public static bool ForceVertexFullRender(object component)
        {
            if (component == null) return false;
            try
            {
                var ti = component.GetType().GetProperty("textInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(component, null);
                if (ti == null) return ForceVertexAlphaOpaque(component);
                var mi = ti.GetType().GetProperty("meshInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ti, null);
                var ci = ti.GetType().GetProperty("characterInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ti, null);
                int charCount = Convert.ToInt32(ti.GetType().GetProperty("characterCount", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ti, null) ?? 0);
                if (mi == null || ci == null || charCount <= 0) return ForceVertexAlphaOpaque(component);

                var miItem = mi.GetType().GetProperty("Item");
                var ciItem = ci.GetType().GetProperty("Item");
                if (miItem == null || ciItem == null) return ForceVertexAlphaOpaque(component);

                bool wrote = false;
                for (int i = 0; i < charCount; i++)
                {
                    var cinfo = ciItem.GetValue(ci, new object[] { i });
                    if (cinfo == null) continue;
                    var cit = cinfo.GetType();

                    // isVisible: skip non-rendered (space/newline). If the field can't be
                    // read, assume visible (the vertex indices below still guard us).
                    var visF = cit.GetField("isVisible");
                    if (visF != null && !Convert.ToBoolean(visF.GetValue(cinfo))) continue;

                    int matRef = Convert.ToInt32((cit.GetField("materialReferenceIndex")?.GetValue(cinfo)) ?? 0);
                    int vIndex = Convert.ToInt32((cit.GetField("vertexIndex")?.GetValue(cinfo)) ?? -1);
                    if (vIndex < 0) return ForceVertexAlphaOpaque(component); // layout not readable → fallback

                    var info = miItem.GetValue(mi, new object[] { matRef });
                    var verts = info?.GetType().GetProperty("vertices", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info, null);
                    var colors = info?.GetType().GetProperty("colors32", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info, null);
                    if (verts == null || colors == null) continue;
                    var vItem = verts.GetType().GetProperty("Item");
                    var cItem = colors.GetType().GetProperty("Item");
                    var vLenP = verts.GetType().GetProperty("Length") ?? verts.GetType().GetProperty("Count");
                    int vLen = vLenP != null ? Convert.ToInt32(vLenP.GetValue(verts, null)) : 0;
                    if (vItem == null || cItem == null || vIndex + 3 >= vLen) continue;

                    // layout positions: vertex_BL/TL/TR/BR (TMP_Vertex.position), in the same
                    // BL,TL,TR,BR order TMP writes into the mesh quad.
                    string[] corners = { "vertex_BL", "vertex_TL", "vertex_TR", "vertex_BR" };
                    var posObjs = new object[4];
                    float qLo = float.MaxValue, qHi = float.MinValue;
                    bool posReadable = true;
                    for (int k = 0; k < 4; k++)
                    {
                        var vtx = cit.GetField(corners[k])?.GetValue(cinfo);
                        var pos = vtx?.GetType().GetField("position")?.GetValue(vtx);
                        posObjs[k] = pos;
                        if (pos == null) { posReadable = false; break; }
                        float y = Convert.ToSingle(pos.GetType().GetField("y").GetValue(pos));
                        if (y < qLo) qLo = y; if (y > qHi) qHi = y;
                    }

                    // SAFETY: only trust the layout positions when the reference quad has a
                    // sane height. A degenerate quad (~0) means the read failed or the
                    // layout itself is collapsed — writing it would make the glyph vanish
                    // (the earlier regression). In that case leave positions untouched and
                    // only fix alpha for this glyph.
                    bool writePos = posReadable && (qHi - qLo) > 1f;

                    for (int k = 0; k < 4; k++)
                    {
                        if (writePos) vItem.SetValue(verts, posObjs[k], new object[] { vIndex + k });
                        var col = cItem.GetValue(colors, new object[] { vIndex + k });
                        var aF = col.GetType().GetField("a");
                        if (aF != null) { aF.SetValue(col, (byte)255); cItem.SetValue(colors, col, new object[] { vIndex + k }); }
                    }
                    wrote = true;
                }
                if (!wrote) return false;

                if (!_updateVertexDataResolved)
                {
                    _updateVertexDataResolved = true;
                    _updateVertexDataMethod = component.GetType().GetMethod("UpdateVertexData",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                }
                if (_updateVertexDataMethod != null)
                    _updateVertexDataMethod.Invoke(component, null);
                return true;
            }
            catch { return ForceVertexAlphaOpaque(component); }
        }

        /// <summary>
        /// DIAGNOSTIC: vertical span (maxY - minY) of a TMP component's vertices. A
        /// per-character SCALE reveal shrinks glyph quads → span smaller than the
        /// fully-revealed baseline. Returns -1 when unavailable.
        /// </summary>
        public static float GetVertexYSpan(object component)
        {
            if (component == null) return -1f;
            try
            {
                var ti = component.GetType().GetProperty("textInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(component, null);
                var mi = ti?.GetType().GetProperty("meshInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ti, null);
                if (mi == null) return -1f;
                var lenP = mi.GetType().GetProperty("Length") ?? mi.GetType().GetProperty("Count");
                int len = lenP != null ? Convert.ToInt32(lenP.GetValue(mi, null)) : 0;
                var indexer = mi.GetType().GetProperty("Item");
                float lo = float.MaxValue, hi = float.MinValue;
                for (int m = 0; m < len; m++)
                {
                    var info = indexer.GetValue(mi, new object[] { m });
                    var verts = info?.GetType().GetProperty("vertices", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info, null);
                    if (verts == null) continue;
                    var vlp = verts.GetType().GetProperty("Length") ?? verts.GetType().GetProperty("Count");
                    int vlen = vlp != null ? Convert.ToInt32(vlp.GetValue(verts, null)) : 0;
                    var vItem = verts.GetType().GetProperty("Item");
                    for (int v = 0; v < vlen; v++)
                    {
                        var vec = vItem.GetValue(verts, new object[] { v });
                        float y = Convert.ToSingle(vec.GetType().GetField("y").GetValue(vec));
                        if (y < lo) lo = y;
                        if (y > hi) hi = y;
                    }
                }
                if (hi > float.MinValue) return hi - lo;
            }
            catch { }
            return -1f;
        }

        private static Type _canvasGroupType;
        private static MethodInfo _getComponentMethod;
        private static bool _getComponentResolved;

        /// <summary>
        /// DIAGNOSTIC: nearest CanvasGroup alpha up the parent hierarchy (the "transparent"
        /// hypothesis — a container fade the game freezes at 0 would render invisible while
        /// the TMP component's own alpha stays 1). Returns -1 when none found or unavailable.
        /// Uses reflection (never a direct GetComponent(Type) call, which is stripped on
        /// IL2CPP and would throw MissingMethodException at JIT time).
        /// </summary>
        public static float GetHierarchyCanvasGroupAlpha(object component)
        {
            try
            {
                if (_canvasGroupType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _canvasGroupType = asm.GetType("UnityEngine.CanvasGroup");
                        if (_canvasGroupType != null) break;
                    }
                }
                if (_canvasGroupType == null) return -1f;

                if (!_getComponentResolved)
                {
                    _getComponentResolved = true;
                    _getComponentMethod = typeof(GameObject).GetMethod("GetComponent",
                        BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);
                }
                if (_getComponentMethod == null) return -1f;

                var comp = component as Component;
                var t = comp?.transform;
                int guard = 0;
                while (t != null && guard++ < 64)
                {
                    object cg = null;
                    try { cg = _getComponentMethod.Invoke(t.gameObject, new object[] { _canvasGroupType }); }
                    catch { return -1f; }
                    var cgCast = Il2CppCast(cg, _canvasGroupType) ?? cg;
                    if (cgCast != null)
                    {
                        var aProp = _canvasGroupType.GetProperty("alpha", BindingFlags.Public | BindingFlags.Instance);
                        if (aProp != null) return Convert.ToSingle(aProp.GetValue(cgCast, null));
                    }
                    t = t.parent;
                }
            }
            catch { }
            return -1f;
        }

        /// <summary>
        /// Read TMP_Text.maxVisibleCharacters (int). Returns -1 when unavailable.
        /// A typewriter effect keeps this below the text length while revealing; a
        /// finished/idle typewriter leaves it at the last-revealed count. Default is a
        /// very large value (= "show everything").
        /// </summary>
        public static int GetMaxVisibleCharacters(object component)
        {
            if (component == null) return -1;
            try
            {
                var prop = component.GetType().GetProperty("maxVisibleCharacters", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanRead)
                    return Convert.ToInt32(prop.GetValue(component, null));
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// Set TMP_Text.maxVisibleCharacters (int). No-op when the property is absent.
        /// </summary>
        public static void SetMaxVisibleCharacters(object component, int value)
        {
            if (component == null) return;
            try
            {
                var prop = component.GetType().GetProperty("maxVisibleCharacters", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(component, value, null);
            }
            catch { }
        }

        /// <summary>
        /// Character count of a TMP_Text (textInfo.characterCount). Returns -1 if absent.
        /// This is the count TMP compares maxVisibleCharacters against — NOT string length
        /// (rich-text tags and composed glyphs make the two differ).
        /// </summary>
        public static int GetTMPCharacterCount(object component)
        {
            if (component == null) return -1;
            try
            {
                var tiProp = component.GetType().GetProperty("textInfo", BindingFlags.Public | BindingFlags.Instance);
                var ti = tiProp?.GetValue(component, null);
                if (ti == null) return -1;
                var ccProp = ti.GetType().GetProperty("characterCount", BindingFlags.Public | BindingFlags.Instance);
                if (ccProp != null && ccProp.CanRead)
                    return Convert.ToInt32(ccProp.GetValue(ti, null));
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// Call SetAllDirty() on a UI component for forcing visual refresh.
        /// </summary>
        public static void SetAllDirty(object component)
        {
            if (component == null) return;

            try
            {
                var type = component.GetType();
                var method = type.GetMethod("SetAllDirty", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                method?.Invoke(component, null);
            }
            catch { }
        }

        // Canvas.ForceUpdateCanvases() — static, resolved once.
        private static MethodInfo _forceUpdateCanvasesMethod;
        private static bool _forceUpdateCanvasesResolved;

        /// <summary>
        /// Synchronously flush all pending Canvas layout/rebuilds (UnityEngine.Canvas.ForceUpdateCanvases).
        /// Settles RectTransform sizes NOW so a subsequent TMP auto-size fit measures the final container
        /// instead of a transient one (issue #21: auto-sized text that fits BELOW its max briefly rendered
        /// at the max ceiling before re-fitting a frame later — the "grow then settle" flash). Global and
        /// not cheap; call only on discrete toggles, right before forcing the fit.
        /// </summary>
        public static void ForceUpdateCanvases()
        {
            if (!_forceUpdateCanvasesResolved)
            {
                _forceUpdateCanvasesResolved = true;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var canvasType = asm.GetType("UnityEngine.Canvas");
                        if (canvasType == null) continue;
                        _forceUpdateCanvasesMethod = canvasType.GetMethod("ForceUpdateCanvases",
                            BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                        if (_forceUpdateCanvasesMethod != null) break;
                    }
                }
                catch { }
            }
            try { _forceUpdateCanvasesMethod?.Invoke(null, null); }
            catch { }
        }

        #endregion

        #region IL2CPP Helpers

        // Cached IL2CPP methods (populated by TranslatorScanner.InitializeIL2CPP or on first use)
        private static MethodInfo _il2cppTypeOfMethod;
        private static MethodInfo _il2cppResourcesFindAllMethod;
        private static MethodInfo _il2cppTryCastMethod; // Il2CppObjectBase.TryCast<T>() or IL2CPP.TryCast<T>()
        private static bool _il2cppTryCastIsStatic;
        private static bool _il2cppHelpersInitialized;

        /// <summary>
        /// Initialize IL2CPP helper methods. Call from InitializeIL2CPP after methods are found.
        /// </summary>
        public static void SetIL2CPPMethods(MethodInfo il2cppTypeOfMethod, MethodInfo resourcesFindAllMethod,
            MethodInfo tryCastMethod = null, bool tryCastIsStatic = false)
        {
            _il2cppTypeOfMethod = il2cppTypeOfMethod;
            _il2cppResourcesFindAllMethod = resourcesFindAllMethod;
            _il2cppTryCastMethod = tryCastMethod;
            _il2cppTryCastIsStatic = tryCastIsStatic;
            _il2cppHelpersInitialized = true;
        }

        /// <summary>
        /// Cast an IL2CPP object to a specific type using TryCast&lt;T&gt;().
        /// Per MelonLoader docs, IL2CPP objects must be cast with TryCast/Cast, not C# casts.
        /// Returns the casted object, or the original if casting isn't needed/available.
        /// </summary>
        public static object Il2CppCast(object obj, Type targetType)
        {
            if (obj == null || targetType == null) return obj;

            // Already the right type?
            if (targetType.IsInstanceOfType(obj)) return obj;

            // Try IL2CPP TryCast<T>()
            if (_il2cppTryCastMethod != null)
            {
                try
                {
                    var typedMethod = _il2cppTryCastMethod.MakeGenericMethod(targetType);
                    object result;
                    if (_il2cppTryCastIsStatic)
                        result = typedMethod.Invoke(null, new[] { obj });
                    else
                        result = typedMethod.Invoke(obj, null);

                    if (result != null) return result;
                }
                catch { }
            }

            // Try instance Cast<T>() method on the object itself
            try
            {
                var objType = obj.GetType();
                foreach (var method in objType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if ((method.Name == "Cast" || method.Name == "TryCast") &&
                        method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
                    {
                        try
                        {
                            var typedMethod = method.MakeGenericMethod(targetType);
                            var result = typedMethod.Invoke(obj, null);
                            if (result != null) return result;
                        }
                        catch { continue; }
                    }
                }
            }
            catch { }

            return obj; // Return original if cast not possible
        }

        /// <summary>
        /// Find all objects of a type, compatible with both Mono and IL2CPP.
        /// On IL2CPP, uses Il2CppType.Of&lt;T&gt;() + Resources.FindObjectsOfTypeAll(Il2CppType)
        /// which is the correct pattern per MelonLoader documentation.
        /// </summary>
        public static UnityEngine.Object[] FindAllObjectsOfType(Type type)
        {
            if (type == null) return new UnityEngine.Object[0];

            // IL2CPP path: use Il2CppType.Of<T>() pattern
            if (_il2cppHelpersInitialized && _il2cppTypeOfMethod != null && _il2cppResourcesFindAllMethod != null)
            {
                try
                {
                    var il2cppType = _il2cppTypeOfMethod.MakeGenericMethod(type).Invoke(null, null);
                    if (il2cppType != null)
                    {
                        var result = _il2cppResourcesFindAllMethod.Invoke(null, new[] { il2cppType });
                        if (result is UnityEngine.Object[] array)
                            return array;

                        // IL2CPP may return Il2CppReferenceArray — convert
                        if (result is System.Collections.IEnumerable enumerable)
                        {
                            var list = new List<UnityEngine.Object>();
                            foreach (var item in enumerable)
                            {
                                if (item is UnityEngine.Object uobj)
                                    list.Add(uobj);
                            }
                            return list.ToArray();
                        }
                    }
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogWarning($"[TypeHelper] IL2CPP FindAllObjectsOfType failed for {type.Name}: {ex.Message}");
                }
            }

            // Mono path: use reflection ONLY to avoid MissingMethodException at JIT time on IL2CPP
            // Direct calls to UnityEngine.Object.FindObjectsOfType(Type) crash on IL2CPP
            // because the method doesn't exist and JIT resolves references before try/catch
            return FindAllObjectsOfTypeMono(type);
        }

        /// <summary>
        /// Find all loaded objects of an ASSET type (ScriptableObject-derived, e.g.
        /// TMP_FontAsset). Always uses Resources.FindObjectsOfTypeAll, the ONLY API that
        /// returns assets which are not live scene objects.
        ///
        /// Why this is separate from FindAllObjectsOfType: that method's Mono path tries
        /// Object.FindObjectsOfType first and returns its (empty but non-null) result for
        /// assets — ScriptableObjects are never returned by FindObjectsOfType (scene-only),
        /// so it short-circuits before ever reaching Resources.FindObjectsOfTypeAll. That
        /// left Mono + modern-TMP games (e.g. clone-source lookup for custom fonts) finding
        /// zero TMP_FontAssets. Routing asset scans here aligns Mono with the IL2CPP path,
        /// which already goes through Resources.FindObjectsOfTypeAll.
        ///
        /// Note: this is for ASSETS only. Do NOT use it for Component scans (scene text):
        /// FindObjectsOfTypeAll also returns prefabs, inactive objects and built-in assets,
        /// which those scans intentionally exclude.
        /// </summary>
        public static UnityEngine.Object[] FindAllAssetsOfType(Type type)
        {
            if (type == null) return new UnityEngine.Object[0];

            // IL2CPP: FindAllObjectsOfType already routes through Resources.FindObjectsOfTypeAll
            if (_il2cppHelpersInitialized && _il2cppTypeOfMethod != null && _il2cppResourcesFindAllMethod != null)
            {
                return FindAllObjectsOfType(type);
            }

            // Mono: call Resources.FindObjectsOfTypeAll directly via reflection
            return FindAllAssetsOfTypeMono(type);
        }

        /// <summary>
        /// Mono-only asset scan via Resources.FindObjectsOfTypeAll (pure reflection).
        /// NoInlining prevents JIT from resolving the method reference on IL2CPP.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static UnityEngine.Object[] FindAllAssetsOfTypeMono(Type type)
        {
            try
            {
                var method = typeof(Resources).GetMethod("FindObjectsOfTypeAll",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new Type[] { typeof(Type) }, null);
                if (method != null)
                {
                    var result = method.Invoke(null, new object[] { type }) as UnityEngine.Object[];
                    if (result != null) return result;
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[TypeHelper] FindAllAssetsOfType failed for {type.Name}: {ex.Message}");
            }

            return new UnityEngine.Object[0];
        }

        /// <summary>
        /// Mono-only fallback using pure reflection (no direct Unity method references).
        /// NoInlining prevents JIT from resolving these method references on IL2CPP.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static UnityEngine.Object[] FindAllObjectsOfTypeMono(Type type)
        {
            // Use reflection for ALL calls to avoid JIT resolution issues
            try
            {
                // Try FindObjectsOfType(Type, bool) via reflection
                var method = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new Type[] { typeof(Type), typeof(bool) }, null);
                if (method != null)
                {
                    var result = method.Invoke(null, new object[] { type, true }) as UnityEngine.Object[];
                    if (result != null) return result;
                }
            }
            catch { }

            try
            {
                // Try FindObjectsOfType(Type) via reflection
                var method = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new Type[] { typeof(Type) }, null);
                if (method != null)
                {
                    var result = method.Invoke(null, new object[] { type }) as UnityEngine.Object[];
                    if (result != null) return result;
                }
            }
            catch { }

            try
            {
                // Try Resources.FindObjectsOfTypeAll(Type) via reflection
                var method = typeof(Resources).GetMethod("FindObjectsOfTypeAll",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new Type[] { typeof(Type) }, null);
                if (method != null)
                {
                    var result = method.Invoke(null, new object[] { type }) as UnityEngine.Object[];
                    if (result != null) return result;
                }
            }
            catch { }

            return new UnityEngine.Object[0];
        }

        /// <summary>
        /// Create a ScriptableObject of a given type, compatible with IL2CPP.
        /// Uses generic CreateInstance&lt;T&gt;() which works on IL2CPP,
        /// unlike CreateInstance(Type) which has signature mismatch.
        /// </summary>
        public static UnityEngine.Object CreateScriptableObject(Type type)
        {
            if (type == null) return null;

            // Try generic version first: ScriptableObject.CreateInstance<T>()
            // This works on both Mono and IL2CPP
            try
            {
                var soType = typeof(ScriptableObject);
                MethodInfo genericMethod = null;
                foreach (var m in soType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "CreateInstance" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                    {
                        genericMethod = m;
                        break;
                    }
                }

                if (genericMethod != null)
                {
                    var specific = genericMethod.MakeGenericMethod(type);
                    var result = specific.Invoke(null, null);
                    if (result is UnityEngine.Object uobj)
                        return uobj;
                }
            }
            catch { }

            // Fallback: non-generic via reflection (avoids JIT issues)
            return CreateScriptableObjectMono(type);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static UnityEngine.Object CreateScriptableObjectMono(Type type)
        {
            try
            {
                // Try CreateInstance(Type) via reflection
                var method = typeof(ScriptableObject).GetMethod("CreateInstance",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new Type[] { typeof(Type) }, null);
                if (method != null)
                {
                    var result = method.Invoke(null, new object[] { type });
                    if (result is UnityEngine.Object uobj)
                        return uobj;
                }
            }
            catch { }

            try
            {
                var obj = Activator.CreateInstance(type);
                if (obj is UnityEngine.Object uobj)
                    return uobj;
            }
            catch { }

            TranslatorCore.LogWarning($"[TypeHelper] Cannot create ScriptableObject of type {type.Name}");
            return null;
        }

        #endregion
    }
}
