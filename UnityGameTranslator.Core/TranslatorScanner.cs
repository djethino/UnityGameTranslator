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
    /// Optimized for minimal per-frame overhead.
    /// </summary>
    public static class TranslatorScanner
    {
        #region IL2CPP Reflection Cache

        private static MethodInfo il2cppTypeOfMethod;
        private static MethodInfo resourcesFindAllMethod;
        private static MethodInfo tryCastMethod;
        private static bool il2cppMethodsInitialized = false;
        private static bool il2cppScanAvailable = false;

        // Cached generic methods (avoid MakeGenericMethod every call)
        private static MethodInfo tryCastTMPMethod;
        private static MethodInfo tryCastTextMethod;
        private static object il2cppTypeTMP;
        private static object il2cppTypeText;

        #endregion

        #region Component Cache

        // Cache found components to avoid FindObjectsOfTypeAll every frame
        private static UnityEngine.Object[] cachedTMPComponents;
        private static UnityEngine.Object[] cachedUIComponents;
        private static TMP_Text[] cachedTMPMono;
        private static Text[] cachedUIMono;
        private static float lastComponentCacheTime = 0f;
        private const float COMPONENT_CACHE_DURATION = 2f; // Refresh every 2 seconds

        // For Mono: cache direct references
        private static Dictionary<int, TMP_Text> tmpComponentCache = new Dictionary<int, TMP_Text>();
        private static Dictionary<int, Text> uiComponentCache = new Dictionary<int, Text>();

        #endregion

        #region Batch Processing

        private static int currentBatchIndexTMP = 0;
        private static int currentBatchIndexUI = 0;
        private const int BATCH_SIZE = 150; // Process 150 components per scan cycle

        #endregion

        #region Quick Skip Cache

        // Track objects that have been processed and haven't changed
        // Key: instanceId, Value: last processed text hash
        private static Dictionary<int, int> processedTextHashes = new Dictionary<int, int>();

        #endregion

        // Logging flags (one-time)
        private static bool scanLoggedTMP = false;
        private static bool scanLoggedUI = false;

        /// <summary>
        /// Reset caches on scene change.
        /// </summary>
        public static void OnSceneChange()
        {
            cachedTMPComponents = null;
            cachedUIComponents = null;
            cachedTMPMono = null;
            cachedUIMono = null;
            lastComponentCacheTime = 0f;
            currentBatchIndexTMP = 0;
            currentBatchIndexUI = 0;
            processedTextHashes.Clear();
            tmpComponentCache.Clear();
            uiComponentCache.Clear();
            scanLoggedTMP = false;
            scanLoggedUI = false;
        }

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

                // Pre-cache generic methods for TMP_Text and Text
                if (il2cppScanAvailable)
                {
                    try
                    {
                        il2cppTypeTMP = il2cppTypeOfMethod.MakeGenericMethod(typeof(TMP_Text)).Invoke(null, null);
                        il2cppTypeText = il2cppTypeOfMethod.MakeGenericMethod(typeof(Text)).Invoke(null, null);
                    }
                    catch { }

                    if (tryCastMethod != null)
                    {
                        tryCastTMPMethod = tryCastMethod.MakeGenericMethod(typeof(TMP_Text));
                        tryCastTextMethod = tryCastMethod.MakeGenericMethod(typeof(Text));
                    }

                    TranslatorCore.LogInfo($"IL2CPP scan initialized (TryCast cached: {tryCastTMPMethod != null})");
                }
                else
                {
                    TranslatorCore.LogWarning($"IL2CPP scan not available");
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

        #region Mono Scanning

        /// <summary>
        /// Scan and translate text components (Mono version) - batched for performance.
        /// </summary>
        public static void ScanMono()
        {
            float currentTime = Time.realtimeSinceStartup;

            // Refresh component cache periodically
            if (cachedTMPMono == null || currentTime - lastComponentCacheTime > COMPONENT_CACHE_DURATION)
            {
                RefreshMonoCache();
                lastComponentCacheTime = currentTime;
            }

            try
            {
                // Process TMP batch
                if (cachedTMPMono != null && cachedTMPMono.Length > 0)
                {
                    int endIndex = Math.Min(currentBatchIndexTMP + BATCH_SIZE, cachedTMPMono.Length);
                    for (int i = currentBatchIndexTMP; i < endIndex; i++)
                    {
                        var tmp = cachedTMPMono[i];
                        if (tmp == null) continue;
                        ProcessTMPComponent(tmp);
                    }
                    currentBatchIndexTMP = endIndex >= cachedTMPMono.Length ? 0 : endIndex;
                }

                // Process UI batch
                if (cachedUIMono != null && cachedUIMono.Length > 0)
                {
                    int endIndex = Math.Min(currentBatchIndexUI + BATCH_SIZE, cachedUIMono.Length);
                    for (int i = currentBatchIndexUI; i < endIndex; i++)
                    {
                        var ui = cachedUIMono[i];
                        if (ui == null) continue;
                        ProcessUITextComponent(ui);
                    }
                    currentBatchIndexUI = endIndex >= cachedUIMono.Length ? 0 : endIndex;
                }
            }
            catch { }
        }

        private static void RefreshMonoCache()
        {
            try
            {
                try { cachedTMPMono = UnityEngine.Object.FindObjectsOfType<TMP_Text>(true); }
                catch { cachedTMPMono = UnityEngine.Object.FindObjectsOfType<TMP_Text>(); }

                try { cachedUIMono = UnityEngine.Object.FindObjectsOfType<Text>(true); }
                catch { cachedUIMono = UnityEngine.Object.FindObjectsOfType<Text>(); }

                currentBatchIndexTMP = 0;
                currentBatchIndexUI = 0;
            }
            catch { }
        }

        #endregion

        #region IL2CPP Scanning

        /// <summary>
        /// Scan and translate text components (IL2CPP version) - batched for performance.
        /// </summary>
        public static void ScanIL2CPP()
        {
            if (!il2cppMethodsInitialized) InitializeIL2CPP();
            if (!il2cppScanAvailable) return;

            float currentTime = Time.realtimeSinceStartup;

            // Refresh component cache periodically
            if (cachedTMPComponents == null || currentTime - lastComponentCacheTime > COMPONENT_CACHE_DURATION)
            {
                RefreshIL2CPPCache();
                lastComponentCacheTime = currentTime;
            }

            try
            {
                // Process TMP batch
                if (cachedTMPComponents != null && cachedTMPComponents.Length > 0)
                {
                    if (!scanLoggedTMP)
                    {
                        TranslatorCore.LogInfo($"Scan: Found {cachedTMPComponents.Length} TMP_Text components");
                        scanLoggedTMP = true;
                    }

                    int endIndex = Math.Min(currentBatchIndexTMP + BATCH_SIZE, cachedTMPComponents.Length);
                    for (int i = currentBatchIndexTMP; i < endIndex; i++)
                    {
                        var obj = cachedTMPComponents[i];
                        if (obj == null) continue;

                        // Quick skip: check if we've already processed this object with same text
                        int objId = obj.GetInstanceID();

                        var tmp = TryCastTMP(obj);
                        if (tmp == null) continue;

                        ProcessTMPComponent(tmp);
                    }
                    currentBatchIndexTMP = endIndex >= cachedTMPComponents.Length ? 0 : endIndex;
                }

                // Process UI batch
                if (cachedUIComponents != null && cachedUIComponents.Length > 0)
                {
                    if (!scanLoggedUI)
                    {
                        TranslatorCore.LogInfo($"Scan: Found {cachedUIComponents.Length} UI.Text components");
                        scanLoggedUI = true;
                    }

                    int endIndex = Math.Min(currentBatchIndexUI + BATCH_SIZE, cachedUIComponents.Length);
                    for (int i = currentBatchIndexUI; i < endIndex; i++)
                    {
                        var obj = cachedUIComponents[i];
                        if (obj == null) continue;

                        var ui = TryCastText(obj);
                        if (ui == null) continue;

                        ProcessUITextComponent(ui);
                    }
                    currentBatchIndexUI = endIndex >= cachedUIComponents.Length ? 0 : endIndex;
                }
            }
            catch { }
        }

        private static void RefreshIL2CPPCache()
        {
            try
            {
                cachedTMPComponents = FindAllComponentsIL2CPPCached(il2cppTypeTMP);
                cachedUIComponents = FindAllComponentsIL2CPPCached(il2cppTypeText);
                currentBatchIndexTMP = 0;
                currentBatchIndexUI = 0;
            }
            catch { }
        }

        #endregion

        #region Component Processing

        private static void ProcessTMPComponent(TMP_Text tmp)
        {
            try
            {
                string currentText = tmp.text;
                if (string.IsNullOrEmpty(currentText) || currentText.Length < 2) return;

                int instanceId = tmp.GetInstanceID();
                int textHash = currentText.GetHashCode();

                // Quick skip: already processed with same text
                if (processedTextHashes.TryGetValue(instanceId, out int lastHash) && lastHash == textHash)
                    return;

                // Check if text changed since last seen
                if (TranslatorCore.HasSeenText(instanceId, currentText, out _))
                {
                    processedTextHashes[instanceId] = textHash;
                    return;
                }

                string translated = TranslatorCore.TranslateTextWithTracking(currentText, tmp);
                if (translated != currentText)
                {
                    tmp.text = translated;
                    TranslatorCore.UpdateSeenText(instanceId, translated);
                    processedTextHashes[instanceId] = translated.GetHashCode();
                }
                else
                {
                    TranslatorCore.UpdateSeenText(instanceId, currentText);
                    processedTextHashes[instanceId] = textHash;
                }
            }
            catch { }
        }

        private static void ProcessUITextComponent(Text ui)
        {
            try
            {
                string currentText = ui.text;
                if (string.IsNullOrEmpty(currentText) || currentText.Length < 2) return;

                int instanceId = ui.GetInstanceID();
                int textHash = currentText.GetHashCode();

                // Quick skip: already processed with same text
                if (processedTextHashes.TryGetValue(instanceId, out int lastHash) && lastHash == textHash)
                    return;

                // Check if text changed since last seen
                if (TranslatorCore.HasSeenText(instanceId, currentText, out _))
                {
                    processedTextHashes[instanceId] = textHash;
                    return;
                }

                string translated = TranslatorCore.TranslateTextWithTracking(currentText, ui);
                if (translated != currentText)
                {
                    ui.text = translated;
                    TranslatorCore.UpdateSeenText(instanceId, translated);
                    processedTextHashes[instanceId] = translated.GetHashCode();
                }
                else
                {
                    TranslatorCore.UpdateSeenText(instanceId, currentText);
                    processedTextHashes[instanceId] = textHash;
                }
            }
            catch { }
        }

        #endregion

        #region Translation Callback

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
                        int id = tmp.GetInstanceID();
                        TranslatorCore.UpdateSeenText(id, translation);
                        processedTextHashes[id] = translation.GetHashCode();
                    }
                    else if (comp is Text ui && ui != null && ui.text == originalText)
                    {
                        ui.text = translation;
                        int id = ui.GetInstanceID();
                        TranslatorCore.UpdateSeenText(id, translation);
                        processedTextHashes[id] = translation.GetHashCode();
                    }
                }
                catch { }
            }
        }

        #endregion

        #region IL2CPP Helpers (Optimized)

        private static TMP_Text TryCastTMP(object obj)
        {
            if (obj == null) return null;
            if (obj is TMP_Text direct) return direct;

            if (tryCastTMPMethod != null)
            {
                try
                {
                    if (tryCastMethod.IsStatic)
                        return tryCastTMPMethod.Invoke(null, new[] { obj }) as TMP_Text;
                    else
                        return tryCastTMPMethod.Invoke(obj, null) as TMP_Text;
                }
                catch { }
            }

            return null;
        }

        private static Text TryCastText(object obj)
        {
            if (obj == null) return null;
            if (obj is Text direct) return direct;

            if (tryCastTextMethod != null)
            {
                try
                {
                    if (tryCastMethod.IsStatic)
                        return tryCastTextMethod.Invoke(null, new[] { obj }) as Text;
                    else
                        return tryCastTextMethod.Invoke(obj, null) as Text;
                }
                catch { }
            }

            return null;
        }

        private static UnityEngine.Object[] FindAllComponentsIL2CPPCached(object il2cppType)
        {
            if (!il2cppScanAvailable || il2cppType == null) return null;

            try
            {
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
