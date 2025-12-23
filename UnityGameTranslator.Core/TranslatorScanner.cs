using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Handles component scanning for both Mono and IL2CPP runtimes.
    /// </summary>
    public static class TranslatorScanner
    {
        // IL2CPP support
        private static MethodInfo il2cppTypeOfMethod;
        private static MethodInfo resourcesFindAllMethod;
        private static MethodInfo tryCastMethod;
        private static bool il2cppMethodsInitialized = false;
        private static bool il2cppScanAvailable = false;

        // Logging flags (one-time)
        private static bool scanLoggedTMP = false;
        private static bool scanLoggedUI = false;

        /// <summary>
        /// Initialize IL2CPP methods via reflection. Call once at startup for IL2CPP games.
        /// </summary>
        public static void InitializeIL2CPP()
        {
            if (il2cppMethodsInitialized) return;
            il2cppMethodsInitialized = true;

            try
            {
                // Find Il2CppType.Of<T>()
                var il2cppTypeClass = Type.GetType("Il2CppInterop.Runtime.Il2CppType, Il2CppInterop.Runtime");
                if (il2cppTypeClass != null)
                {
                    foreach (var method in il2cppTypeClass.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (method.Name == "Of" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
                        {
                            il2cppTypeOfMethod = method;
                            TranslatorCore.LogInfo("Found Il2CppType.Of<T>() method");
                            break;
                        }
                    }
                }

                // Find Resources.FindObjectsOfTypeAll(Il2CppSystem.Type)
                var resourcesType = typeof(Resources);
                foreach (var method in resourcesType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name == "FindObjectsOfTypeAll" && method.GetParameters().Length == 1)
                    {
                        var paramType = method.GetParameters()[0].ParameterType;
                        if (paramType.FullName?.Contains("Il2Cpp") == true)
                        {
                            resourcesFindAllMethod = method;
                            TranslatorCore.LogInfo($"Found Resources.FindObjectsOfTypeAll({paramType.Name})");
                            break;
                        }
                    }
                }

                // Find TryCast - try static IL2CPP class first
                var il2cppClass = Type.GetType("Il2CppInterop.Runtime.IL2CPP, Il2CppInterop.Runtime");
                if (il2cppClass != null)
                {
                    foreach (var method in il2cppClass.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (method.Name == "TryCast" && method.IsGenericMethodDefinition)
                        {
                            tryCastMethod = method;
                            TranslatorCore.LogInfo("Found IL2CPP.TryCast<T>() method");
                            break;
                        }
                    }
                }

                // Fallback: Il2CppObjectBase instance method
                if (tryCastMethod == null)
                {
                    var il2cppObjectBase = Type.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase, Il2CppInterop.Runtime");
                    if (il2cppObjectBase != null)
                    {
                        foreach (var method in il2cppObjectBase.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (method.Name == "TryCast" && method.IsGenericMethodDefinition)
                            {
                                tryCastMethod = method;
                                TranslatorCore.LogInfo("Found Il2CppObjectBase.TryCast<T>() method");
                                break;
                            }
                        }
                    }
                }

                il2cppScanAvailable = il2cppTypeOfMethod != null && resourcesFindAllMethod != null;

                if (il2cppScanAvailable)
                {
                    TranslatorCore.LogInfo($"IL2CPP scan initialized (TryCast: {tryCastMethod != null})");
                }
                else
                {
                    TranslatorCore.LogWarning($"IL2CPP scan not available - Il2CppType.Of: {il2cppTypeOfMethod != null}, FindObjectsOfTypeAll: {resourcesFindAllMethod != null}");
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"Failed to initialize IL2CPP methods: {e.Message}");
            }
        }

        /// <summary>
        /// Check if IL2CPP scanning is available.
        /// </summary>
        public static bool IsIL2CPPScanAvailable => il2cppScanAvailable;

        /// <summary>
        /// Scan and translate all text components (Mono version).
        /// </summary>
        public static void ScanMono()
        {
            try
            {
                // TMP_Text
                TMP_Text[] allTMP;
                try { allTMP = UnityEngine.Object.FindObjectsOfType<TMP_Text>(true); }
                catch { allTMP = UnityEngine.Object.FindObjectsOfType<TMP_Text>(); }

                foreach (var tmp in allTMP)
                {
                    if (tmp == null) continue;
                    ProcessComponent(tmp, tmp.GetInstanceID(), tmp.text, text => tmp.text = text);
                }

                // UI.Text
                Text[] allUI;
                try { allUI = UnityEngine.Object.FindObjectsOfType<Text>(true); }
                catch { allUI = UnityEngine.Object.FindObjectsOfType<Text>(); }

                foreach (var ui in allUI)
                {
                    if (ui == null) continue;
                    ProcessComponent(ui, ui.GetInstanceID(), ui.text, text => ui.text = text);
                }
            }
            catch { }
        }

        /// <summary>
        /// Scan and translate all text components (IL2CPP version).
        /// </summary>
        public static void ScanIL2CPP()
        {
            if (!il2cppMethodsInitialized) InitializeIL2CPP();
            if (!il2cppScanAvailable) return;

            try
            {
                // TMP_Text
                var foundTMP = FindAllComponentsIL2CPP(typeof(TMP_Text));
                if (foundTMP != null)
                {
                    if (!scanLoggedTMP)
                    {
                        TranslatorCore.LogInfo($"Scan: Found {foundTMP.Length} TMP_Text components");
                        scanLoggedTMP = true;
                    }

                    foreach (var obj in foundTMP)
                    {
                        var tmp = TryCastIL2CPP<TMP_Text>(obj);
                        if (tmp == null) continue;
                        ProcessComponent(tmp, tmp.GetInstanceID(), tmp.text, text => tmp.text = text);
                    }
                }

                // UI.Text
                var foundUI = FindAllComponentsIL2CPP(typeof(Text));
                if (foundUI != null)
                {
                    if (!scanLoggedUI)
                    {
                        TranslatorCore.LogInfo($"Scan: Found {foundUI.Length} UI.Text components");
                        scanLoggedUI = true;
                    }

                    foreach (var obj in foundUI)
                    {
                        var ui = TryCastIL2CPP<Text>(obj);
                        if (ui == null) continue;
                        ProcessComponent(ui, ui.GetInstanceID(), ui.text, text => ui.text = text);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Process a single component for translation.
        /// </summary>
        private static void ProcessComponent(object component, int instanceId, string currentText, Action<string> setText)
        {
            try
            {
                if (string.IsNullOrEmpty(currentText) || currentText.Length < 2) return;
                if (TranslatorCore.HasSeenText(instanceId, currentText, out _)) return;

                string translated = TranslatorCore.TranslateTextWithTracking(currentText, component);
                if (translated != currentText)
                {
                    setText(translated);
                    TranslatorCore.UpdateSeenText(instanceId, translated);
                }
                else
                {
                    TranslatorCore.UpdateSeenText(instanceId, currentText);
                }
            }
            catch { }
        }

        /// <summary>
        /// Callback for when async translation completes. Updates tracked components.
        /// </summary>
        public static void OnTranslationComplete(string originalText, string translation, List<object> components)
        {
            if (components == null) return;

            foreach (var comp in components)
            {
                try
                {
                    if (comp is TMP_Text tmp && tmp != null && tmp.text == originalText)
                    {
                        tmp.text = translation;
                    }
                    else if (comp is Text ui && ui != null && ui.text == originalText)
                    {
                        ui.text = translation;
                    }
                }
                catch { }
            }
        }

        #region IL2CPP Helpers

        private static T TryCastIL2CPP<T>(object obj) where T : class
        {
            if (obj == null) return null;
            if (obj is T direct) return direct;

            if (tryCastMethod != null)
            {
                try
                {
                    var genericMethod = tryCastMethod.MakeGenericMethod(typeof(T));
                    if (tryCastMethod.IsStatic)
                        return genericMethod.Invoke(null, new[] { obj }) as T;
                    else
                        return genericMethod.Invoke(obj, null) as T;
                }
                catch { }
            }

            return null;
        }

        private static UnityEngine.Object[] FindAllComponentsIL2CPP(Type componentType)
        {
            if (!il2cppScanAvailable) return null;

            try
            {
                var genericMethod = il2cppTypeOfMethod.MakeGenericMethod(componentType);
                var il2cppType = genericMethod.Invoke(null, null);
                if (il2cppType == null) return null;

                var result = resourcesFindAllMethod.Invoke(null, new[] { il2cppType });
                if (result == null) return null;

                var asArray = result as UnityEngine.Object[];
                if (asArray == null)
                {
                    var enumerable = result as System.Collections.IEnumerable;
                    if (enumerable != null)
                    {
                        var list = new List<UnityEngine.Object>();
                        foreach (var item in enumerable)
                        {
                            if (item is UnityEngine.Object uobj)
                                list.Add(uobj);
                        }
                        return list.ToArray();
                    }
                    return null;
                }

                return asArray;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
