using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

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
        private static UnityEngine.Object[] cachedTMPMono;
        private static UnityEngine.Object[] cachedUIMono;
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

        // Track original text per component (before translation was applied)
        // Key: component InstanceID, Value: original text before translation
        // Used to restore originals when translations are disabled at runtime
        private static Dictionary<int, string> componentOriginals = new Dictionary<int, string>();

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
            componentOriginals.Clear();  // Clear original text tracking (components are invalid after scene change)
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
        /// Force refresh of component caches.
        /// Use before RefreshForFont to ensure all components are captured.
        /// </summary>
        public static void ForceRefreshCache()
        {
            try
            {
                if (TranslatorCore.Adapter?.IsIL2CPP == true)
                {
                    RefreshIL2CPPCache();
                }
                else
                {
                    RefreshMonoCache();
                }
                lastComponentCacheTime = Time.time;
                TranslatorCore.LogInfo($"[Scanner] Force refreshed cache: {cachedTMPMono?.Length ?? 0} TMP, {cachedUIMono?.Length ?? 0} UI.Text");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] ForceRefreshCache error: {ex.Message}");
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
        /// Force refresh all text components by re-assigning their text.
        /// If translations are disabled, restores original texts from cache.
        /// This triggers Harmony patches to re-process and apply translations/fonts.
        /// Call after changing settings that affect text display (fonts, translations).
        /// </summary>
        public static void ForceRefreshAllText()
        {
            int refreshed = 0;
            int restored = 0;

            // Check if we should restore originals (translations disabled)
            bool shouldRestore = !TranslatorCore.Config.enable_translations;

            try
            {
                // Refresh cached Mono UI.Text components
                if (cachedUIMono != null)
                {
                    foreach (var obj in cachedUIMono)
                    {
                        if (obj == null) continue;
                        try
                        {
                            int instanceId = obj.GetInstanceID();

                            if (shouldRestore)
                            {
                                // Try to restore original if we have it
                                string original = GetOriginalText(instanceId);
                                if (original != null)
                                {
                                    TypeHelper.SetText(obj, original);
                                    ClearOriginalText(instanceId);
                                    processedTextHashes.Remove(instanceId);
                                    restored++;
                                    continue;
                                }
                            }

                            // Normal refresh path (trigger Harmony patch)
                            string currentText = TypeHelper.GetText(obj);
                            if (!string.IsNullOrEmpty(currentText))
                            {
                                TypeHelper.SetText(obj, currentText);
                                refreshed++;
                            }
                        }
                        catch { }
                    }
                }

                // Refresh cached Mono TMP components
                if (cachedTMPMono != null)
                {
                    foreach (var obj in cachedTMPMono)
                    {
                        if (obj == null) continue;
                        try
                        {
                            int instanceId = obj.GetInstanceID();

                            if (shouldRestore)
                            {
                                string original = GetOriginalText(instanceId);
                                if (original != null)
                                {
                                    TypeHelper.SetText(obj, original);
                                    ClearOriginalText(instanceId);
                                    processedTextHashes.Remove(instanceId);
                                    restored++;
                                    continue;
                                }
                            }

                            string currentText = TypeHelper.GetText(obj);
                            if (!string.IsNullOrEmpty(currentText))
                            {
                                TypeHelper.SetText(obj, currentText);
                                refreshed++;
                            }
                        }
                        catch { }
                    }
                }

                // Refresh cached IL2CPP components (if available)
                RefreshIL2CPPCachedComponents(cachedUIComponents, tryCastTextMethod, shouldRestore, ref refreshed, ref restored);
                RefreshIL2CPPCachedComponents(cachedTMPComponents, tryCastTMPMethod, shouldRestore, ref refreshed, ref restored);

                // Refresh alternate TMP components (TMProOld, etc.) - scan dynamically since they're not cached
                refreshed += RefreshAlternateTMPComponents(shouldRestore, ref restored);

                if (restored > 0)
                    TranslatorCore.LogInfo($"[Scanner] Restored {restored} original texts, refreshed {refreshed} components");
                else
                    TranslatorCore.LogInfo($"[Scanner] Force refreshed {refreshed} text components");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] ForceRefreshAllText error: {ex.Message}");
            }
        }

        /// <summary>
        /// Refresh alternate TMP components (TMProOld, etc.) by scanning the scene.
        /// These are not cached because they use reflection and different types per game.
        /// </summary>
        private static int RefreshAlternateTMPComponents(bool shouldRestore, ref int restored)
        {
            int refreshed = 0;

            try
            {
                // Find TMProOld.TMP_Text type
                Type tmpTextType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    tmpTextType = asm.GetType("TMProOld.TMP_Text");
                    if (tmpTextType != null) break;
                }

                if (tmpTextType == null) return 0;

                // Get text property for reflection
                var textProp = tmpTextType.GetProperty("text", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (textProp == null) return 0;

                // Find all components in scene
                var allComponents = FindAllObjectsOfType(tmpTextType);
                TranslatorCore.LogInfo($"[Scanner] Found {allComponents.Length} alternate TMP components");

                foreach (var component in allComponents)
                {
                    if (component == null) continue;

                    try
                    {
                        var unityComponent = component as Component;
                        if (unityComponent == null) continue;

                        // Skip our own UI
                        if (TranslatorCore.ShouldSkipTranslation(unityComponent)) continue;

                        int instanceId = unityComponent.GetInstanceID();

                        if (shouldRestore)
                        {
                            string original = GetOriginalText(instanceId);
                            if (original != null)
                            {
                                textProp.SetValue(component, original, null);
                                ClearOriginalText(instanceId);
                                processedTextHashes.Remove(instanceId);
                                restored++;
                                continue;
                            }
                        }

                        // Refresh by re-setting text (triggers Harmony patch)
                        string currentText = textProp.GetValue(component, null) as string;
                        if (!string.IsNullOrEmpty(currentText))
                        {
                            textProp.SetValue(component, currentText, null);
                            refreshed++;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] RefreshAlternateTMP error: {ex.Message}");
            }

            return refreshed;
        }

        /// <summary>
        /// Helper to refresh IL2CPP cached components (shared logic for TMP and UI).
        /// </summary>
        private static void RefreshIL2CPPCachedComponents(UnityEngine.Object[] cache, MethodInfo tryCastMethod,
            bool shouldRestore, ref int refreshed, ref int restored)
        {
            if (cache == null || tryCastMethod == null) return;

            foreach (var obj in cache)
            {
                if (obj == null) continue;
                try
                {
                    object component;
                    if (tryCastMethod.DeclaringType != null && !tryCastMethod.IsStatic)
                        component = tryCastMethod.Invoke(obj, null);
                    else
                        component = tryCastMethod.Invoke(null, new object[] { obj });
                    if (component == null) continue;

                    int instanceId = TypeHelper.GetInstanceID(component);
                    if (instanceId == -1) continue;

                    if (shouldRestore)
                    {
                        string original = GetOriginalText(instanceId);
                        if (original != null)
                        {
                            TypeHelper.SetText(component, original);
                            ClearOriginalText(instanceId);
                            processedTextHashes.Remove(instanceId);
                            restored++;
                            continue;
                        }
                    }

                    string currentText = TypeHelper.GetText(component);
                    if (!string.IsNullOrEmpty(currentText))
                    {
                        TypeHelper.SetText(component, currentText);
                        refreshed++;
                    }
                }
                catch { }
            }
        }

        #region Original Text Tracking

        /// <summary>
        /// Store the original (untranslated) text for a component.
        /// Called when translation is applied. Only stores on first translation (preserves true original).
        /// </summary>
        public static void StoreOriginalText(object component, string originalText)
        {
            if (component == null || string.IsNullOrEmpty(originalText)) return;

            int instanceId = GetComponentInstanceId(component);
            if (instanceId == -1) return;

            // Only store if not already stored (first translation wins - preserves true original)
            if (!componentOriginals.ContainsKey(instanceId))
            {
                componentOriginals[instanceId] = originalText;
            }
        }

        /// <summary>
        /// Get the original (untranslated) text for a component.
        /// Returns null if no original stored (component was never translated).
        /// </summary>
        public static string GetOriginalText(object component)
        {
            if (component == null) return null;

            int instanceId = GetComponentInstanceId(component);
            if (instanceId == -1) return null;

            if (componentOriginals.TryGetValue(instanceId, out string original))
                return original;

            return null;
        }

        /// <summary>
        /// Get the original text by instance ID directly.
        /// </summary>
        public static string GetOriginalText(int instanceId)
        {
            if (componentOriginals.TryGetValue(instanceId, out string original))
                return original;
            return null;
        }

        /// <summary>
        /// Clear the stored original for a component (after restoration).
        /// </summary>
        public static void ClearOriginalText(int instanceId)
        {
            componentOriginals.Remove(instanceId);
        }

        /// <summary>
        /// Helper to get InstanceID from various component types.
        /// </summary>
        private static int GetComponentInstanceId(object component)
        {
            return TypeHelper.GetInstanceID(component);
        }

        /// <summary>
        /// Restore originals for components using a specific font.
        /// Call when per-font translation is disabled.
        /// </summary>
        public static void RestoreOriginalsForFont(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return;
            int restored = 0;

            try
            {
                // Restore Mono components with this font (TMP + UI.Text)
                restored += RestoreOriginalsForFontInCache(cachedTMPMono, fontName);
                restored += RestoreOriginalsForFontInCache(cachedUIMono, fontName);

                // IL2CPP components
                restored += RestoreOriginalsForFontIL2CPP(cachedTMPComponents, tryCastTMPMethod, fontName);
                restored += RestoreOriginalsForFontIL2CPP(cachedUIComponents, tryCastTextMethod, fontName);

                if (restored > 0)
                    TranslatorCore.LogInfo($"[Scanner] Restored {restored} originals for font: {fontName}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] RestoreOriginalsForFont error: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-translate components for a specific font.
        /// Call when per-font translation is re-enabled.
        /// </summary>
        public static void RefreshForFont(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return;
            int refreshed = 0;

            try
            {
                // Force cache refresh to capture all current components
                ForceRefreshCache();

                // Refresh Mono components (TMP + UI.Text)
                refreshed += RefreshForFontInCache(cachedTMPMono, fontName);
                refreshed += RefreshForFontInCache(cachedUIMono, fontName);

                // IL2CPP components
                refreshed += RefreshForFontIL2CPP(cachedTMPComponents, tryCastTMPMethod, fontName);
                refreshed += RefreshForFontIL2CPP(cachedUIComponents, tryCastTextMethod, fontName);

                if (refreshed > 0)
                    TranslatorCore.LogInfo($"[Scanner] Refreshed {refreshed} components for font: {fontName}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] RefreshForFont error: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore originals for a given font in a Mono component cache.
        /// </summary>
        private static int RestoreOriginalsForFontInCache(UnityEngine.Object[] cache, string fontName)
        {
            if (cache == null) return 0;
            int restored = 0;

            foreach (var obj in cache)
            {
                if (obj == null) continue;
                try
                {
                    string compFont = TypeHelper.GetFontName(obj);
                    if (compFont != fontName) continue;

                    int id = obj.GetInstanceID();
                    string original = GetOriginalText(id);
                    if (original != null)
                    {
                        TypeHelper.SetText(obj, original);
                        ClearOriginalText(id);
                        processedTextHashes.Remove(id);
                        restored++;
                    }
                }
                catch { }
            }
            return restored;
        }

        /// <summary>
        /// Restore originals for a given font in an IL2CPP component cache.
        /// </summary>
        private static int RestoreOriginalsForFontIL2CPP(UnityEngine.Object[] cache, MethodInfo tryCastMethod, string fontName)
        {
            if (cache == null || tryCastMethod == null) return 0;
            int restored = 0;

            foreach (var obj in cache)
            {
                if (obj == null) continue;
                try
                {
                    object component = tryCastMethod.IsStatic
                        ? tryCastMethod.Invoke(null, new object[] { obj })
                        : tryCastMethod.Invoke(obj, null);
                    if (component == null) continue;

                    string compFont = TypeHelper.GetFontName(component);
                    if (compFont != fontName) continue;

                    int id = TypeHelper.GetInstanceID(component);
                    if (id == -1) continue;
                    string original = GetOriginalText(id);
                    if (original != null)
                    {
                        TypeHelper.SetText(component, original);
                        ClearOriginalText(id);
                        processedTextHashes.Remove(id);
                        restored++;
                    }
                }
                catch { }
            }
            return restored;
        }

        /// <summary>
        /// Refresh components for a given font in a Mono cache.
        /// </summary>
        private static int RefreshForFontInCache(UnityEngine.Object[] cache, string fontName)
        {
            if (cache == null) return 0;
            int refreshed = 0;

            foreach (var obj in cache)
            {
                if (obj == null) continue;
                try
                {
                    string compFont = TypeHelper.GetFontName(obj);
                    if (compFont != fontName) continue;

                    int id = obj.GetInstanceID();
                    processedTextHashes.Remove(id);

                    string currentText = TypeHelper.GetText(obj);
                    if (!string.IsNullOrEmpty(currentText))
                    {
                        TypeHelper.SetText(obj, currentText);
                        refreshed++;
                    }
                }
                catch { }
            }
            return refreshed;
        }

        /// <summary>
        /// Refresh components for a given font in an IL2CPP cache.
        /// </summary>
        private static int RefreshForFontIL2CPP(UnityEngine.Object[] cache, MethodInfo tryCastMethod, string fontName)
        {
            if (cache == null || tryCastMethod == null) return 0;
            int refreshed = 0;

            foreach (var obj in cache)
            {
                if (obj == null) continue;
                try
                {
                    object component = tryCastMethod.IsStatic
                        ? tryCastMethod.Invoke(null, new object[] { obj })
                        : tryCastMethod.Invoke(obj, null);
                    if (component == null) continue;

                    string compFont = TypeHelper.GetFontName(component);
                    if (compFont != fontName) continue;

                    int id = TypeHelper.GetInstanceID(component);
                    if (id == -1) continue;
                    processedTextHashes.Remove(id);

                    string currentText = TypeHelper.GetText(component);
                    if (!string.IsNullOrEmpty(currentText))
                    {
                        TypeHelper.SetText(component, currentText);
                        refreshed++;
                    }
                }
                catch { }
            }
            return refreshed;
        }

        #endregion

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

            // If AI is enabled, we need to scan to queue new translations
            if (TranslatorCore.Config.enable_ai)
                return false;

            // If capture mode is enabled, we need to scan to capture keys
            if (TranslatorCore.Config.capture_keys_only)
                return false;

            // No cache, no AI, no capture mode - nothing useful to do
            return true;
        }

        /// <summary>
        /// Initialize IL2CPP methods via reflection. Call once at startup for IL2CPP games.
        /// </summary>
        public static void InitializeIL2CPP()
        {
            if (il2cppMethodsInitialized) return;
            il2cppMethodsInitialized = true;

            // On IL2CPP, TMP assemblies may be loaded after initial TypeHelper.Initialize()
            TypeHelper.TryResolveIfNeeded();

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
                // Use TypeHelper resolved types instead of compile-time typeof()
                // This avoids TypeLoadException on IL2CPP where standard assemblies aren't compatible
                if (il2cppScanAvailable)
                {
                    try
                    {
                        if (TypeHelper.TMP_TextType != null)
                            il2cppTypeTMP = il2cppTypeOfMethod.MakeGenericMethod(TypeHelper.TMP_TextType).Invoke(null, null);
                        if (TypeHelper.UI_TextType != null)
                            il2cppTypeText = il2cppTypeOfMethod.MakeGenericMethod(TypeHelper.UI_TextType).Invoke(null, null);
                    }
                    catch (Exception ex)
                    {
                        TranslatorCore.LogWarning($"IL2CPP type resolution failed: {ex.Message}");
                    }

                    if (tryCastMethod != null)
                    {
                        if (TypeHelper.TMP_TextType != null)
                            tryCastTMPMethod = tryCastMethod.MakeGenericMethod(TypeHelper.TMP_TextType);
                        if (TypeHelper.UI_TextType != null)
                            tryCastTextMethod = tryCastMethod.MakeGenericMethod(TypeHelper.UI_TextType);
                    }

                    TranslatorCore.LogInfo($"IL2CPP scan initialized (TMP={il2cppTypeTMP != null}, Text={il2cppTypeText != null}, TryCast={tryCastTMPMethod != null})");
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
            // Apply any pending translations from AI (main thread) - always do this
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
                        var obj = cachedTMPMono[i];
                        if (obj == null) continue;
                        ProcessComponentReflection(obj, "TMP");
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
                        var obj = cachedUIMono[i];
                        if (obj == null) continue;
                        ProcessComponentReflection(obj, "Unity");
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
                cachedTMPMono = FindAllObjectsOfType(TypeHelper.TMP_TextType);
                cachedUIMono = FindAllObjectsOfType(TypeHelper.UI_TextType);
                // Don't reset batch indices - continue from where we were
            }
            catch { }
        }

        /// <summary>
        /// Find all objects of a given type, using reflection to handle both Mono and IL2CPP.
        /// On IL2CPP, FindObjectsOfType(Type) may not exist — uses Resources.FindObjectsOfTypeAll as fallback.
        /// </summary>
        private static UnityEngine.Object[] FindAllObjectsOfType(Type type)
        {
            if (type == null) return null;

            // Try FindObjectsOfType with includeInactive parameter (Unity 2020+, Mono)
            try
            {
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

            // Try FindObjectsOfType(Type) without includeInactive
            try
            {
                return UnityEngine.Object.FindObjectsOfType(type);
            }
            catch { }

            // IL2CPP fallback: use Resources.FindObjectsOfTypeAll(Type)
            // This finds ALL objects (including inactive/hidden) which is fine for scanning
            try
            {
                var result = Resources.FindObjectsOfTypeAll(type);
                if (result != null) return result;
            }
            catch { }

            // IL2CPP fallback 2: use the IL2CPP-specific method if available
            if (il2cppScanAvailable && il2cppTypeOfMethod != null && resourcesFindAllMethod != null)
            {
                try
                {
                    var il2cppType = il2cppTypeOfMethod.MakeGenericMethod(type).Invoke(null, null);
                    if (il2cppType != null)
                    {
                        return FindAllComponentsIL2CPPCached(il2cppType);
                    }
                }
                catch { }
            }

            return null;
        }

        #endregion

        #region IL2CPP Scanning

        /// <summary>
        /// Scan and translate text components (IL2CPP version) - batched for performance.
        /// </summary>
        public static void ScanIL2CPP()
        {
            // Apply any pending translations from AI (main thread) - always do this
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

                        var component = TryCastToType(obj, tryCastTMPMethod);
                        if (component == null) continue;

                        ProcessComponentReflection(component, "TMP");
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

                        var component = TryCastToType(obj, tryCastTextMethod);
                        if (component == null) continue;

                        ProcessComponentReflection(component, "Unity");
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

        // Debug: track how many times we've logged for InputField detection
        private static int inputFieldDebugLogCount = 0;
        private const int MAX_INPUTFIELD_DEBUG_LOGS = 50;

        /// <summary>
        /// Unified component processing via reflection.
        /// Works for TMP_Text, UI.Text, and any component type resolved by TypeHelper.
        /// </summary>
        private static void ProcessComponentReflection(object component, string componentType)
        {
            try
            {
                var comp = component as Component;
                if (comp == null) return;

                int instanceId = comp.GetInstanceID();

                // Skip if own UI and should not be translated (uses hierarchy check)
                if (TranslatorCore.ShouldSkipTranslation(comp))
                    return;

                // Skip if translation disabled for this font
                string fontName = TypeHelper.GetFontName(component);
                if (!string.IsNullOrEmpty(fontName) && !FontManager.IsTranslationEnabled(fontName))
                    return;

                // Skip if already identified as InputField user text (not placeholder)
                if (inputFieldTextIds.Contains(instanceId))
                    return;

                // First-time check: is this the textComponent of an InputField?
                if (TypeHelper.IsInputFieldTextComponent(component))
                {
                    inputFieldTextIds.Add(instanceId);
                    TranslatorCore.LogInfo($"[Scanner] Excluded InputField textComponent: {comp.gameObject.name}");
                    return;
                }

                string currentText = TypeHelper.GetText(component);
                if (string.IsNullOrEmpty(currentText) || currentText.Length < 2) return;

                // Debug log for UI.Text
                if (componentType == "Unity" && inputFieldDebugLogCount < MAX_INPUTFIELD_DEBUG_LOGS)
                {
                    var parentNames = GetParentHierarchy(comp.gameObject, 5);
                    TranslatorCore.LogInfo($"[Scanner] Text NOT in InputField: '{comp.gameObject.name}' hierarchy: {parentNames}");
                    inputFieldDebugLogCount++;
                }

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
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(comp);
                string translated = TranslatorCore.TranslateTextWithTracking(currentText, comp, isOwnUI);
                if (translated != currentText)
                {
                    TypeHelper.SetText(component, translated);

                    // Force refresh based on component type
                    if (componentType == "TMP")
                        TypeHelper.ForceMeshUpdate(component);
                    else
                        TypeHelper.SetAllDirty(component);

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
                    string actualText = TypeHelper.GetText(comp);
                    if (actualText == null) continue;

                    string expectedPreview = originalText.Length > 40 ? originalText.Substring(0, 40) + "..." : originalText;
                    string actualPreview = actualText.Length > 40 ? actualText.Substring(0, 40) + "..." : actualText;

                    if (actualText == originalText)
                    {
                        // Store original before applying translation (enables runtime toggle restoration)
                        StoreOriginalText(comp, originalText);

                        TypeHelper.SetText(comp, translation);

                        // Force visual refresh by toggling enabled state
                        TypeHelper.ToggleEnabled(comp);

                        int id = TypeHelper.GetInstanceID(comp);
                        if (id != -1)
                        {
                            TranslatorCore.UpdateSeenText(id, translation);
                            processedTextHashes[id] = translation.GetHashCode();
                        }
                        TranslatorCore.LogInfo($"[Apply OK] {expectedPreview}");
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[Apply SKIP] expected='{expectedPreview}' actual='{actualPreview}'");
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
                bool hasInputField = false;
                if (TypeHelper.UI_InputFieldType != null)
                    hasInputField = current.GetComponent(TypeHelper.UI_InputFieldType) != null;
                names.Add(hasInputField ? $"{current.name}[IF]" : current.name);
                current = current.parent;
                depth++;
            }

            return string.Join(" -> ", names);
        }

        #endregion

        #region IL2CPP Helpers (Optimized)

        /// <summary>
        /// Try to cast an IL2CPP object to a specific type using the cached TryCast method.
        /// </summary>
        private static object TryCastToType(object obj, MethodInfo typedTryCastMethod)
        {
            if (obj == null || typedTryCastMethod == null) return null;

            // Check if already the right type
            if (TypeHelper.TMP_TextType != null && TypeHelper.TMP_TextType.IsInstanceOfType(obj))
                return obj;
            if (TypeHelper.UI_TextType != null && TypeHelper.UI_TextType.IsInstanceOfType(obj))
                return obj;

            try
            {
                if (tryCastMethod != null && tryCastMethod.IsStatic)
                    return typedTryCastMethod.Invoke(null, new[] { obj });
                else
                    return typedTryCastMethod.Invoke(obj, null);
            }
            catch { }

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
