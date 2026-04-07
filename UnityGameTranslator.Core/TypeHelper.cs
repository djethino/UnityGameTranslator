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

            // TMP_InputField.textComponent
            if (TMP_InputFieldType != null)
            {
                TMP_InputField_TextComponentProp = TMP_InputFieldType.GetProperty("textComponent", pubInst);
            }

            // UI InputField.textComponent
            if (UI_InputFieldType != null)
            {
                UI_InputField_TextComponentProp = UI_InputFieldType.GetProperty("textComponent", pubInst);
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
        /// Check if a text component is the textComponent of an InputField (should not be translated).
        /// Works for both TMP_InputField and UI.InputField.
        /// </summary>
        public static bool IsInputFieldTextComponent(object textComponent)
        {
            if (textComponent == null) return false;

            var comp = textComponent as Component;
            if (comp == null) return false;

            try
            {
                var type = textComponent.GetType();

                // Check TMP_InputField
                if (TMP_InputFieldType != null && TMP_TextType != null && TMP_TextType.IsAssignableFrom(type))
                {
                    // Use GetComponentInParent via reflection (generic method)
                    var getCompMethod = FindGetComponentInParent(comp, TMP_InputFieldType);
                    if (getCompMethod != null)
                    {
                        var inputField = getCompMethod;
                        if (inputField != null && TMP_InputField_TextComponentProp != null)
                        {
                            var textComp = TMP_InputField_TextComponentProp.GetValue(inputField, null);
                            return textComp != null && ReferenceEquals(textComp, textComponent);
                        }
                    }
                }

                // Check UI.InputField
                if (UI_InputFieldType != null && UI_TextType != null && UI_TextType.IsAssignableFrom(type))
                {
                    var getCompMethod = FindGetComponentInParent(comp, UI_InputFieldType);
                    if (getCompMethod != null)
                    {
                        var inputField = getCompMethod;
                        if (inputField != null && UI_InputField_TextComponentProp != null)
                        {
                            var textComp = UI_InputField_TextComponentProp.GetValue(inputField, null);
                            return textComp != null && ReferenceEquals(textComp, textComponent);
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Call GetComponentInParent for a given type via reflection.
        /// </summary>
        private static object FindGetComponentInParent(Component component, Type searchType)
        {
            try
            {
                // Use the non-generic GetComponentInParent(Type)
                var method = typeof(Component).GetMethod("GetComponentInParent",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new Type[] { typeof(Type) }, null);

                if (method != null)
                {
                    return method.Invoke(component, new object[] { searchType });
                }

                // Fallback: use the generic version
                var genericMethod = typeof(Component).GetMethod("GetComponentInParent",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (genericMethod != null && genericMethod.IsGenericMethodDefinition)
                {
                    var specific = genericMethod.MakeGenericMethod(searchType);
                    return specific.Invoke(component, null);
                }
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
