using System;
using System.Collections.Generic;
using System.Reflection;
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

        private static void ResolveTypes()
        {
            // UI.Text and InputField - standard Unity types
            UI_TextType = FindType("UnityEngine.UI.Text");
            UI_InputFieldType = FindType("UnityEngine.UI.InputField");

            // TextMesh - legacy 3D text
            TextMeshType = typeof(TextMesh);

            // Font
            FontType = typeof(Font);

            // TMP types - prefer TMProOld (alternate TMP), fallback to TMPro
            // Same priority logic as CustomFontLoader.InitializeTypes()
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
            }
            else
            {
                TMP_TextType = FindType("TMPro.TMP_Text");
                TMP_FontAssetType = FindType("TMPro.TMP_FontAsset");
                TMP_InputFieldType = FindType("TMPro.TMP_InputField");
                UseAlternateTMP = false;
            }
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
        /// </summary>
        public static object GetFont(object component)
        {
            if (component == null) return null;

            try
            {
                var type = component.GetType();

                if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type) && TMP_FontProp != null)
                    return TMP_FontProp.GetValue(component, null);

                if (UI_TextType != null && UI_TextType.IsAssignableFrom(type) && UI_FontProp != null)
                    return UI_FontProp.GetValue(component, null);

                if (TextMeshType != null && TextMeshType.IsAssignableFrom(type) && TextMesh_FontProp != null)
                    return TextMesh_FontProp.GetValue(component, null);
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

                if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type) && TMP_FontProp != null && TMP_FontProp.CanWrite)
                {
                    TMP_FontProp.SetValue(component, font, null);
                    return;
                }

                if (UI_TextType != null && UI_TextType.IsAssignableFrom(type) && UI_FontProp != null && UI_FontProp.CanWrite)
                {
                    UI_FontProp.SetValue(component, font, null);
                    return;
                }

                if (TextMeshType != null && TextMeshType.IsAssignableFrom(type) && TextMesh_FontProp != null && TextMesh_FontProp.CanWrite)
                {
                    TextMesh_FontProp.SetValue(component, font, null);
                    return;
                }
            }
            catch { }
        }

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

                if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type) && TMP_FontSizeProp != null)
                {
                    var val = TMP_FontSizeProp.GetValue(component, null);
                    if (val is float f) return f;
                }

                if (UI_TextType != null && UI_TextType.IsAssignableFrom(type) && UI_FontSizeProp != null)
                {
                    var val = UI_FontSizeProp.GetValue(component, null);
                    if (val is int i) return i;
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

                if (TMP_TextType != null && TMP_TextType.IsAssignableFrom(type) && TMP_FontSizeProp != null && TMP_FontSizeProp.CanWrite)
                {
                    TMP_FontSizeProp.SetValue(component, size, null);
                    return;
                }

                if (UI_TextType != null && UI_TextType.IsAssignableFrom(type) && UI_FontSizeProp != null && UI_FontSizeProp.CanWrite)
                {
                    UI_FontSizeProp.SetValue(component, (int)Math.Round(size), null);
                    return;
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
                    TMP_TextProp.SetValue(component, text, null);
                    return;
                }

                if (UI_TextType != null && UI_TextType.IsAssignableFrom(type) && UI_TextProp != null && UI_TextProp.CanWrite)
                {
                    UI_TextProp.SetValue(component, text, null);
                    return;
                }

                if (TextMeshType != null && TextMeshType.IsAssignableFrom(type) && TextMesh_TextProp != null && TextMesh_TextProp.CanWrite)
                {
                    TextMesh_TextProp.SetValue(component, text, null);
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
    }
}
