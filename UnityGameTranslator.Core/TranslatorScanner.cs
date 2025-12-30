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
        private const float COMPONENT_CACHE_DURATION = 2f; // Minimum time between cache refreshes
        private static bool cacheRefreshPending = false; // Defer refresh until cycle completes

        #endregion

        #region Batch Processing

        private static int currentBatchIndexTMP = 0;
        private static int currentBatchIndexUI = 0;
        private const int BATCH_SIZE = 200; // Process 200 components per scan cycle
        private static bool scanCycleComplete = true; // True when we've scanned all components

        #endregion

        #region Quick Skip Cache

        // Track objects that have been processed and haven't changed
        // Key: instanceId, Value: last processed text hash
        private static Dictionary<int, int> processedTextHashes = new Dictionary<int, int>();

        // Track InputField text components (user input, not placeholder) - never translate these
        private static HashSet<int> inputFieldTextIds = new HashSet<int>();

        // Track components that are part of our own UI (UniverseLib/UnityGameTranslator) - never translate
        private static HashSet<int> ownUIComponentIds = new HashSet<int>();

        #endregion

        // Logging flags (one-time)
        private static bool scanLoggedTMP = false;
        private static bool scanLoggedUI = false;

        #region Pending Updates Queue (thread-safe)

        // Queue for translations completed by worker thread, to be applied on main thread
        private static readonly object pendingUpdatesLock = new object();
        private static Queue<PendingUpdate> pendingUpdates = new Queue<PendingUpdate>();

        private struct PendingUpdate
        {
            public string OriginalText;
            public string Translation;
            public List<object> Components;
        }

        #endregion

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
            scanCycleComplete = true;
            cacheRefreshPending = false;
            processedTextHashes.Clear();
            inputFieldTextIds.Clear();
            scanLoggedTMP = false;
            scanLoggedUI = false;
            inputFieldDebugLogCount = 0;

            // Clear pending updates (components from old scene are invalid)
            lock (pendingUpdatesLock)
            {
                pendingUpdates.Clear();
            }
        }

        /// <summary>
        /// Clear the processed text cache. Call when settings change to force re-evaluation.
        /// </summary>
        public static void ClearProcessedCache()
        {
            processedTextHashes.Clear();
            // Reset batch indices to start fresh
            currentBatchIndexTMP = 0;
            currentBatchIndexUI = 0;
            scanCycleComplete = true;
        }

        /// <summary>
        /// Check if scanning should be skipped (no useful work to do).
        /// Returns true if scanning can be skipped.
        /// </summary>
        private static bool ShouldSkipScanning()
        {
            // If translations are disabled, no point scanning
            if (!TranslatorCore.Config.enable_translations)
                return true;

            // If we have cached translations to serve, keep scanning
            if (TranslatorCore.TranslationCache.Count > 0)
                return false;

            // If Ollama is enabled, we need to scan to queue new translations
            if (TranslatorCore.Config.enable_ollama)
                return false;

            // If capture mode is enabled, we need to scan to capture keys
            if (TranslatorCore.Config.capture_keys_only)
                return false;

            // No cache, no Ollama, no capture mode - nothing useful to do
            return true;
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
            // Apply any pending translations from Ollama (main thread) - always do this
            ProcessPendingUpdates();

            // Skip scanning if there's no useful work to do
            if (ShouldSkipScanning())
                return;

            float currentTime = Time.realtimeSinceStartup;

            // Check if cache refresh is needed
            bool needsRefresh = cachedTMPMono == null ||
                               (currentTime - lastComponentCacheTime > COMPONENT_CACHE_DURATION);

            if (needsRefresh)
            {
                if (scanCycleComplete)
                {
                    RefreshMonoCache();
                    lastComponentCacheTime = currentTime;
                    cacheRefreshPending = false;
                }
                else
                {
                    cacheRefreshPending = true;
                }
            }

            if (cacheRefreshPending && scanCycleComplete)
            {
                RefreshMonoCache();
                lastComponentCacheTime = currentTime;
                cacheRefreshPending = false;
            }

            try
            {
                bool tmpDone = true;
                bool uiDone = true;

                // Process TMP batch
                if (cachedTMPMono != null && cachedTMPMono.Length > 0)
                {
                    if (currentBatchIndexTMP >= cachedTMPMono.Length)
                        currentBatchIndexTMP = 0;

                    int endIndex = Math.Min(currentBatchIndexTMP + BATCH_SIZE, cachedTMPMono.Length);
                    for (int i = currentBatchIndexTMP; i < endIndex; i++)
                    {
                        var tmp = cachedTMPMono[i];
                        if (tmp == null) continue;
                        ProcessTMPComponent(tmp);
                    }

                    if (endIndex >= cachedTMPMono.Length)
                        currentBatchIndexTMP = 0;
                    else
                    {
                        currentBatchIndexTMP = endIndex;
                        tmpDone = false;
                    }
                }

                // Process UI batch
                if (cachedUIMono != null && cachedUIMono.Length > 0)
                {
                    if (currentBatchIndexUI >= cachedUIMono.Length)
                        currentBatchIndexUI = 0;

                    int endIndex = Math.Min(currentBatchIndexUI + BATCH_SIZE, cachedUIMono.Length);
                    for (int i = currentBatchIndexUI; i < endIndex; i++)
                    {
                        var ui = cachedUIMono[i];
                        if (ui == null) continue;
                        ProcessUITextComponent(ui);
                    }

                    if (endIndex >= cachedUIMono.Length)
                        currentBatchIndexUI = 0;
                    else
                    {
                        currentBatchIndexUI = endIndex;
                        uiDone = false;
                    }
                }

                scanCycleComplete = tmpDone && uiDone;
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

                // Don't reset batch indices - continue from where we were
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
            // Apply any pending translations from Ollama (main thread) - always do this
            ProcessPendingUpdates();

            if (!il2cppMethodsInitialized) InitializeIL2CPP();
            if (!il2cppScanAvailable) return;

            // Skip scanning if there's no useful work to do
            if (ShouldSkipScanning())
                return;

            float currentTime = Time.realtimeSinceStartup;

            // Check if cache refresh is needed
            bool needsRefresh = cachedTMPComponents == null ||
                               (currentTime - lastComponentCacheTime > COMPONENT_CACHE_DURATION);

            if (needsRefresh)
            {
                if (scanCycleComplete)
                {
                    // Cycle is complete, safe to refresh now
                    RefreshIL2CPPCache();
                    lastComponentCacheTime = currentTime;
                    cacheRefreshPending = false;
                }
                else
                {
                    // Cycle in progress, defer refresh until complete
                    cacheRefreshPending = true;
                }
            }

            // Handle deferred refresh when cycle completes
            if (cacheRefreshPending && scanCycleComplete)
            {
                RefreshIL2CPPCache();
                lastComponentCacheTime = currentTime;
                cacheRefreshPending = false;
            }

            try
            {
                bool tmpDone = true;
                bool uiDone = true;

                // Process TMP batch
                if (cachedTMPComponents != null && cachedTMPComponents.Length > 0)
                {
                    if (!scanLoggedTMP)
                    {
                        TranslatorCore.LogInfo($"Scan: Found {cachedTMPComponents.Length} TMP_Text components");
                        scanLoggedTMP = true;
                    }

                    // Ensure index is valid after potential cache changes
                    if (currentBatchIndexTMP >= cachedTMPComponents.Length)
                        currentBatchIndexTMP = 0;

                    int endIndex = Math.Min(currentBatchIndexTMP + BATCH_SIZE, cachedTMPComponents.Length);
                    for (int i = currentBatchIndexTMP; i < endIndex; i++)
                    {
                        var obj = cachedTMPComponents[i];
                        if (obj == null) continue;

                        var tmp = TryCastTMP(obj);
                        if (tmp == null) continue;

                        ProcessTMPComponent(tmp);
                    }

                    if (endIndex >= cachedTMPComponents.Length)
                        currentBatchIndexTMP = 0;
                    else
                    {
                        currentBatchIndexTMP = endIndex;
                        tmpDone = false;
                    }
                }

                // Process UI batch
                if (cachedUIComponents != null && cachedUIComponents.Length > 0)
                {
                    if (!scanLoggedUI)
                    {
                        TranslatorCore.LogInfo($"Scan: Found {cachedUIComponents.Length} UI.Text components");
                        scanLoggedUI = true;
                    }

                    // Ensure index is valid after potential cache changes
                    if (currentBatchIndexUI >= cachedUIComponents.Length)
                        currentBatchIndexUI = 0;

                    int endIndex = Math.Min(currentBatchIndexUI + BATCH_SIZE, cachedUIComponents.Length);
                    for (int i = currentBatchIndexUI; i < endIndex; i++)
                    {
                        var obj = cachedUIComponents[i];
                        if (obj == null) continue;

                        var ui = TryCastText(obj);
                        if (ui == null) continue;

                        ProcessUITextComponent(ui);
                    }

                    if (endIndex >= cachedUIComponents.Length)
                        currentBatchIndexUI = 0;
                    else
                    {
                        currentBatchIndexUI = endIndex;
                        uiDone = false;
                    }
                }

                // Cycle is complete when both TMP and UI have wrapped around
                scanCycleComplete = tmpDone && uiDone;
            }
            catch { }
        }

        private static void RefreshIL2CPPCache()
        {
            try
            {
                cachedTMPComponents = FindAllComponentsIL2CPPCached(il2cppTypeTMP);
                cachedUIComponents = FindAllComponentsIL2CPPCached(il2cppTypeText);
                // Don't reset batch indices - continue from where we were
                // The Min() check in the scan loop handles size changes gracefully
            }
            catch { }
        }

        #endregion

        #region Component Processing

        private static void ProcessTMPComponent(TMP_Text tmp)
        {
            try
            {
                int instanceId = tmp.GetInstanceID();

                // Skip if own UI and should not be translated (uses hierarchy check)
                if (TranslatorCore.ShouldSkipTranslation(tmp))
                    return;

                // Skip if already identified as InputField user text (not placeholder)
                if (inputFieldTextIds.Contains(instanceId))
                    return;

                // First-time check: is this the textComponent of a TMP_InputField? (exclude user input, not placeholder)
                var tmpInputField = tmp.GetComponentInParent<TMPro.TMP_InputField>();
                if (tmpInputField != null && tmpInputField.textComponent == tmp)
                {
                    inputFieldTextIds.Add(instanceId);
                    TranslatorCore.LogInfo($"[Scanner] Excluded TMP_InputField textComponent: {tmp.gameObject.name} (parent: {tmpInputField.gameObject.name})");
                    return;
                }

                string currentText = tmp.text;
                if (string.IsNullOrEmpty(currentText) || currentText.Length < 2) return;

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

                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(tmp);
                string translated = TranslatorCore.TranslateTextWithTracking(currentText, tmp, isOwnUI);
                if (translated != currentText)
                {
                    tmp.text = translated;
                    tmp.ForceMeshUpdate();  // Force Unity to refresh
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

        // Debug: track how many times we've logged for InputField detection
        private static int inputFieldDebugLogCount = 0;
        private const int MAX_INPUTFIELD_DEBUG_LOGS = 50;

        private static void ProcessUITextComponent(Text ui)
        {
            try
            {
                int instanceId = ui.GetInstanceID();

                // Skip if own UI and should not be translated (uses hierarchy check)
                if (TranslatorCore.ShouldSkipTranslation(ui))
                    return;

                // Skip if already identified as InputField user text (not placeholder)
                if (inputFieldTextIds.Contains(instanceId))
                    return;

                // First-time check: is this the textComponent of an InputField? (exclude user input, not placeholder)
                var inputField = ui.GetComponentInParent<InputField>();
                if (inputField != null && inputField.textComponent == ui)
                {
                    inputFieldTextIds.Add(instanceId);
                    TranslatorCore.LogInfo($"[Scanner] Excluded InputField textComponent: {ui.gameObject.name} (parent: {inputField.gameObject.name})");
                    return;
                }

                // Debug: log first few Text components that are NOT detected as InputField children
                if (inputFieldDebugLogCount < MAX_INPUTFIELD_DEBUG_LOGS && !string.IsNullOrEmpty(ui.text) && ui.text.Length > 2)
                {
                    var parentNames = GetParentHierarchy(ui.gameObject, 5);
                    TranslatorCore.LogInfo($"[Scanner] Text NOT in InputField: '{ui.gameObject.name}' hierarchy: {parentNames}");
                    inputFieldDebugLogCount++;
                }

                string currentText = ui.text;
                if (string.IsNullOrEmpty(currentText) || currentText.Length < 2) return;

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

                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(ui);
                string translated = TranslatorCore.TranslateTextWithTracking(currentText, ui, isOwnUI);
                if (translated != currentText)
                {
                    ui.text = translated;
                    ui.SetAllDirty();  // Force Unity to refresh
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
        /// Callback for when async translation completes. Queues update for main thread.
        /// Called from worker thread - must NOT access Unity objects directly!
        /// </summary>
        public static void OnTranslationComplete(string originalText, string translation, List<object> components)
        {
            if (components == null || components.Count == 0) return;

            lock (pendingUpdatesLock)
            {
                pendingUpdates.Enqueue(new PendingUpdate
                {
                    OriginalText = originalText,
                    Translation = translation,
                    Components = components
                });
            }
        }

        /// <summary>
        /// Process pending translation updates on the main thread.
        /// Call this from Update() or scan methods.
        /// </summary>
        public static void ProcessPendingUpdates()
        {
            // Process all pending updates immediately
            while (true)
            {
                PendingUpdate update;
                lock (pendingUpdatesLock)
                {
                    if (pendingUpdates.Count == 0) break;
                    update = pendingUpdates.Dequeue();
                }

                ApplyTranslationToComponents(update.OriginalText, update.Translation, update.Components);
            }
        }

        private static void ApplyTranslationToComponents(string originalText, string translation, List<object> components)
        {
            foreach (var comp in components)
            {
                try
                {
                    if (comp is TMP_Text tmp && tmp != null)
                    {
                        string actualText = tmp.text;
                        string expectedPreview = originalText.Length > 40 ? originalText.Substring(0, 40) + "..." : originalText;
                        string actualPreview = actualText.Length > 40 ? actualText.Substring(0, 40) + "..." : actualText;

                        if (actualText == originalText)
                        {
                            tmp.text = translation;

                            // Force visual refresh by toggling enabled state
                            tmp.enabled = false;
                            tmp.enabled = true;

                            int id = tmp.GetInstanceID();
                            TranslatorCore.UpdateSeenText(id, translation);
                            processedTextHashes[id] = translation.GetHashCode();
                            TranslatorCore.LogInfo($"[Apply OK] {expectedPreview}");
                        }
                        else
                        {
                            TranslatorCore.LogWarning($"[Apply SKIP] expected='{expectedPreview}' actual='{actualPreview}'");
                        }
                    }
                    else if (comp is Text ui && ui != null)
                    {
                        string actualText = ui.text;
                        string expectedPreview = originalText.Length > 40 ? originalText.Substring(0, 40) + "..." : originalText;
                        string actualPreview = actualText.Length > 40 ? actualText.Substring(0, 40) + "..." : actualText;

                        if (actualText == originalText)
                        {
                            ui.text = translation;

                            // Force visual refresh by toggling enabled state
                            ui.enabled = false;
                            ui.enabled = true;

                            int id = ui.GetInstanceID();
                            TranslatorCore.UpdateSeenText(id, translation);
                            processedTextHashes[id] = translation.GetHashCode();
                            TranslatorCore.LogInfo($"[Apply OK] {expectedPreview}");
                        }
                        else
                        {
                            TranslatorCore.LogWarning($"[Apply SKIP] expected='{expectedPreview}' actual='{actualPreview}'");
                        }
                    }
                }
                catch { }
            }
        }

        #endregion

        #region Debug Helpers

        /// <summary>
        /// Get parent hierarchy as a string for debugging (child -> parent -> grandparent...)
        /// </summary>
        private static string GetParentHierarchy(GameObject obj, int maxDepth)
        {
            var names = new List<string>();
            var current = obj.transform;
            int depth = 0;

            while (current != null && depth < maxDepth)
            {
                // Also check if this object has InputField component
                var hasInputField = current.GetComponent<InputField>() != null;
                names.Add(hasInputField ? $"{current.name}[IF]" : current.name);
                current = current.parent;
                depth++;
            }

            return string.Join(" -> ", names);
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
