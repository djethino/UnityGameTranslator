using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Handles component scanning for both Mono and IL2CPP runtimes.
    /// Uses a unified RegisteredTextType system to eliminate Mono/IL2CPP code duplication.
    /// </summary>
    public static class TranslatorScanner
    {
        #region IL2CPP Reflection Cache

        private static MethodInfo il2cppTypeOfMethod;
        private static MethodInfo resourcesFindAllMethod;
        private static MethodInfo tryCastMethod;
        private static bool il2cppMethodsInitialized = false;
        private static bool il2cppScanAvailable = false;

        #endregion

        #region Registered Types

        // Unified list of all text component types to scan
        private static readonly List<RegisteredTextType> _registeredTypes = new List<RegisteredTextType>();
        private static bool _typesRegistered = false;

        /// <summary>
        /// Find a component by instance ID across all registered type caches.
        /// Used by FontManager for font restore operations.
        /// </summary>
        public static object FindCachedComponentById(int instanceId)
        {
            foreach (var type in _registeredTypes)
            {
                if (type.CachedComponents == null) continue;
                foreach (var obj in type.CachedComponents)
                {
                    if (obj != null && obj.GetInstanceID() == instanceId)
                        return obj;
                }
            }
            return null;
        }

        /// <summary>
        /// Register a type for scanning. Called by RegisterBuiltInTypes and TranslatorPatches.
        /// </summary>
        public static void RegisterType(RegisteredTextType type)
        {
            if (type == null || type.ComponentType == null) return;

            // Avoid duplicate registration
            foreach (var existing in _registeredTypes)
            {
                if (existing.ComponentType == type.ComponentType)
                    return;
            }

            // Set up IL2CPP cache for this type if available
            if (il2cppScanAvailable && il2cppTypeOfMethod != null)
            {
                try
                {
                    type.IL2CPPType = il2cppTypeOfMethod.MakeGenericMethod(type.ComponentType).Invoke(null, null);
                }
                catch { }

                if (tryCastMethod != null)
                {
                    try
                    {
                        type.TryCastMethod = tryCastMethod.MakeGenericMethod(type.ComponentType);
                    }
                    catch { }
                }
            }

            _registeredTypes.Add(type);
            TranslatorCore.LogDebug($"[Scanner] Registered type: {type.Name} (Category={type.Category})");
        }

        #endregion

        #region Batch Processing

        private const int BATCH_SIZE = 200; // Process 200 components per scan cycle
        private static bool scanCycleComplete = true;

        #endregion

        #region Component Cache

        private static float lastComponentCacheTime = 0f;
        private const float COMPONENT_CACHE_DURATION = 2f;
        private static bool cacheRefreshPending = false;

        #endregion

        #region Quick Skip Cache

        // Track objects that have been processed and haven't changed
        // Key: instanceId, Value: last processed text hash
        private static Dictionary<int, int> processedTextHashes = new Dictionary<int, int>();

        // Per-cycle dedup: prevents processing same component through overlapping types
        // (e.g. TMP_Text scan returns TextMeshProUGUI components, which also have their own type entry)
        // Cleared at start of each Scan() call.
        private static readonly HashSet<int> _processedThisCycle = new HashSet<int>();

        // Track InputField text components (user input, not placeholder) - never translate these
        private static HashSet<int> inputFieldTextIds = new HashSet<int>();

        // Track original text per component (before translation was applied)
        // Key: component InstanceID, Value: original text before translation
        // Used to restore originals when translations are disabled at runtime
        private static Dictionary<int, string> componentOriginals = new Dictionary<int, string>();

        #endregion

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
            lastComponentCacheTime = 0f;
            scanCycleComplete = true;
            cacheRefreshPending = false;
            processedTextHashes.Clear();
            inputFieldTextIds.Clear();
            componentOriginals.Clear();

            // Clear all per-type caches and reset discovery strategies
            foreach (var type in _registeredTypes)
            {
                type.CachedComponents = null;
                type.BatchIndex = 0;
                type.LoggedOnce = false;
                type.ResetStrategyStates();
            }

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
                if (!_typesRegistered) RegisterBuiltInTypes();

                int total = 0;
                foreach (var type in _registeredTypes)
                {
                    type.CachedComponents = RefreshTypeCache(type);
                    total += type.CachedComponents?.Length ?? 0;
                }

                lastComponentCacheTime = Time.time;
                _lateUpdateCacheDirty = true;
                TranslatorCore.LogDebug($"[Scanner] Force refreshed cache: {total} total components across {_registeredTypes.Count} types");
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
            foreach (var type in _registeredTypes)
            {
                type.BatchIndex = 0;
            }
            scanCycleComplete = true;
        }

        #region Unified Scan

        /// <summary>
        /// Unified scan method - replaces ScanMono() and ScanIL2CPP().
        /// Works for both Mono and IL2CPP runtimes.
        /// </summary>
        // === PROFILING (activate via debug file in plugin folder) ===
        private static readonly System.Diagnostics.Stopwatch _scanProfSw = new System.Diagnostics.Stopwatch();
        private static long _scanProfRefreshTicks = 0;
        private static long _scanProfBatchTicks = 0;
        private static int _scanProfCount = 0;
        private static int _scanProfRefreshCount = 0;
        private static float _scanProfLastLog = 0f;

        public static void Scan()
        {
            // Apply any pending translations from AI (main thread) - always do this
            ProcessPendingUpdates();

            // Register types on first invocation (after TypeHelper.Initialize() + InitializeIL2CPP())
            if (!_typesRegistered) RegisterBuiltInTypes();

            // Skip scanning if there's no useful work to do
            if (ShouldSkipScanning())
                return;

            bool profiling = TranslatorCore.DebugMode;
            if (profiling) _scanProfSw.Restart();
            float currentTime = Time.realtimeSinceStartup;

            // Check if cache refresh is needed
            bool needsRefresh = (currentTime - lastComponentCacheTime > COMPONENT_CACHE_DURATION);

            // First run: force refresh if cache was never populated
            // (uses lastComponentCacheTime == 0 instead of checking CachedComponents == null
            // because null is a valid state for types with no components in the scene)
            if (!needsRefresh && lastComponentCacheTime == 0f)
            {
                needsRefresh = true;
            }

            if (needsRefresh)
            {
                if (scanCycleComplete)
                {
                    RefreshAllCaches();
                    lastComponentCacheTime = currentTime;
                    cacheRefreshPending = false;
                    _scanProfRefreshCount++;
                }
                else
                {
                    cacheRefreshPending = true;
                }
            }
            else if (cacheRefreshPending && scanCycleComplete)
            {
                RefreshAllCaches();
                lastComponentCacheTime = currentTime;
                cacheRefreshPending = false;
                _scanProfRefreshCount++;
            }

            long afterRefresh = profiling ? _scanProfSw.ElapsedTicks : 0;

            try
            {
                // Clear per-cycle dedup (each component processed at most once across all types)
                _processedThisCycle.Clear();

                bool allDone = true;
                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null || type.CachedComponents.Length == 0) continue;
                    bool typeDone = ProcessBatch(type);
                    if (!typeDone) allDone = false;
                }
                scanCycleComplete = allDone;
            }
            catch { }

            if (profiling)
            {
                long afterBatch = _scanProfSw.ElapsedTicks;
                _scanProfRefreshTicks += afterRefresh;
                _scanProfBatchTicks += afterBatch - afterRefresh;
                _scanProfCount++;

                // Log every 5 seconds
                if (currentTime - _scanProfLastLog > 5f)
                {
                    _scanProfLastLog = currentTime;
                    double freq = System.Diagnostics.Stopwatch.Frequency;
                    TranslatorCore.LogDebug($"[SCAN-PERF] {_scanProfCount} scans in 5s ({_scanProfRefreshCount} refreshes) | " +
                        $"Refresh={_scanProfRefreshTicks/freq*1000:F1}ms | " +
                        $"Batch={_scanProfBatchTicks/freq*1000:F1}ms");
                    _scanProfCount = 0;
                    _scanProfRefreshCount = 0;
                    _scanProfRefreshTicks = 0;
                    _scanProfBatchTicks = 0;
                }
            }
        }

        /// <summary>
        /// Refresh caches for all registered types using 2-phase approach:
        /// Phase 1: Direct scans per type (IL2CPP native, TypeHelper, Static lists) — cheap, targeted.
        /// Phase 2: Mutualized MonoBehaviourFilter — ONE FindAllObjectsOfType(Component) call
        ///          shared across all types that still need it. Avoids N expensive calls.
        /// </summary>
        private static int _lastTotalComponentCount = 0;
        private static float _lastForceRefreshTime = 0f;

        private static void RefreshAllCaches()
        {
            _lateUpdateCacheDirty = true;
            // Phase 1: Direct scans (strategies 1-3) for each type
            var needsFilterTypes = new List<RegisteredTextType>();
            int newTotal = 0;

            foreach (var type in _registeredTypes)
            {
                type.CachedComponents = RefreshTypeCacheDirect(type);
                newTotal += type.CachedComponents?.Length ?? 0;

                if (!type.LoggedOnce && type.CachedComponents != null && type.CachedComponents.Length > 0)
                {
                    TranslatorCore.LogDebug($"Scan: Found {type.CachedComponents.Length} {type.Name} components");
                    type.LoggedOnce = true;
                }

                // If direct strategies didn't find anything, check if MonoBehaviourFilter should be tried
                if ((type.CachedComponents == null || type.CachedComponents.Length == 0) && type.NeedsMonoBehaviourFilter)
                {
                    needsFilterTypes.Add(type);
                }
            }

            // Phase 2: Mutualized MonoBehaviourFilter (ONE call for ALL types that need it)
            if (needsFilterTypes.Count > 0)
            {
                RefreshViaMonoBehaviourFilterShared(needsFilterTypes);
            }

            // If new components appeared, invalidate font clones to prevent atlas corruption.
            // Cloned fonts inherit the original's atlas; when new glyphs are needed at scaled sizes,
            // the atlas overflows. Recreating the clone gives a fresh atlas.
            _lastTotalComponentCount = newTotal;
        }

        /// <summary>
        /// Process a batch of components for a registered type.
        /// Returns true when the full array has been processed (cycle complete for this type).
        /// </summary>
        private static bool ProcessBatch(RegisteredTextType type)
        {
            if (type.BatchIndex >= type.CachedComponents.Length)
                type.BatchIndex = 0;

            int endIndex = Math.Min(type.BatchIndex + BATCH_SIZE, type.CachedComponents.Length);

            for (int i = type.BatchIndex; i < endIndex; i++)
            {
                var obj = type.CachedComponents[i];
                if (obj == null) continue;

                // For IL2CPP native scan results, need TryCast
                if (type.TryCastMethod != null && TranslatorCore.Adapter?.IsIL2CPP == true)
                {
                    // Check if already the right type
                    if (!type.ComponentType.IsInstanceOfType(obj))
                    {
                        var cast = TryCastToType(obj, type.TryCastMethod);
                        if (cast != null)
                        {
                            ProcessComponentForType(cast, type);
                        }
                        continue;
                    }
                }

                ProcessComponentForType(obj, type);
            }

            if (endIndex >= type.CachedComponents.Length)
            {
                type.BatchIndex = 0;
                return true;
            }
            else
            {
                type.BatchIndex = endIndex;
                return false;
            }
        }

        #endregion

        #region Type Registration

        /// <summary>
        /// Register built-in text types (TMP, UI.Text, TextMesh) and generic types from TranslatorPatches.
        /// Called on first Scan() invocation, after TypeHelper.Initialize() and InitializeIL2CPP().
        /// </summary>
        private static void RegisterBuiltInTypes()
        {
            _typesRegistered = true;

            // Ensure IL2CPP is initialized for IL2CPP runtimes
            if (TranslatorCore.Adapter?.IsIL2CPP == true && !il2cppMethodsInitialized)
                InitializeIL2CPP();

            var pubInst = BindingFlags.Public | BindingFlags.Instance;

            // TMP_Text (base type) — FindAllObjectsOfType returns all subtypes
            // (TextMeshProUGUI, TextMeshPro) via polymorphism. No need to register them separately.
            // Subtypes are already covered by Harmony patches for real-time text interception.
            if (TypeHelper.TMP_TextType != null)
            {
                RegisterType(new RegisteredTextType
                {
                    Name = TypeHelper.TMP_TextType.Name,
                    Category = "TMP",
                    ComponentType = TypeHelper.TMP_TextType,
                    TextProp = TypeHelper.TMP_TextProp,
                    FontProp = TypeHelper.TMP_FontProp,
                    FontSizeProp = TypeHelper.TMP_FontSizeProp,
                    ColorProp = TypeHelper.TMP_TextType.GetProperty("color", pubInst),
                    FontTypeName = "TMP",
                    NeedsForceMeshUpdate = true,
                    NeedsSetAllDirty = false
                });
            }

            // UI.Text
            if (TypeHelper.UI_TextType != null)
            {
                RegisterType(new RegisteredTextType
                {
                    Name = "UI.Text",
                    Category = "Unity",
                    ComponentType = TypeHelper.UI_TextType,
                    TextProp = TypeHelper.UI_TextProp,
                    FontProp = TypeHelper.UI_FontProp,
                    FontSizeProp = TypeHelper.UI_FontSizeProp,
                    ColorProp = TypeHelper.UI_TextType.GetProperty("color", pubInst),
                    FontTypeName = "Unity",
                    NeedsForceMeshUpdate = false,
                    NeedsSetAllDirty = true
                });
            }

            // TextMesh and alternate TMP (TMProOld) are NOT registered for scanning.
            // They are fully covered by Harmony patches (TextMesh_SetText_Prefix,
            // AlternateTMP_SetText_Prefix) which handle real-time text interception.
            // The old code (pre-refactor) also did not scan them — only TMP_Text + UI.Text.

            // Register generic types already detected by TranslatorPatches (NGUI, etc.)
            // These have no Harmony coverage and need scanner discovery.
            foreach (var genericType in TranslatorPatches.GenericTextTypes)
            {
                RegisterType(genericType);
            }

            TranslatorCore.LogDebug($"[Scanner] Registered {_registeredTypes.Count} text component types");
        }

        /// <summary>
        /// Find a type by full name across all loaded assemblies.
        /// Handles IL2CPP prefixed names (e.g., Il2CppTMPro.TextMeshProUGUI).
        /// </summary>
        private static Type FindType(string fullName)
        {
            // Extract class name for IL2CPP prefix search
            string className = fullName.Contains(".") ? fullName.Substring(fullName.LastIndexOf('.') + 1) : fullName;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Try exact name first
                    var type = asm.GetType(fullName);
                    if (type != null) return type;

                    // On IL2CPP, types may have Il2Cpp prefix on namespace
                    // e.g., TMPro.TextMeshProUGUI -> Il2CppTMPro.TextMeshProUGUI
                    if (fullName.Contains("."))
                    {
                        string il2cppName = "Il2Cpp" + fullName;
                        type = asm.GetType(il2cppName);
                        if (type != null) return type;
                    }
                }
                catch { }
            }

            // Last resort: scan all types by class name (handles any namespace prefix)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == className && !type.IsAbstract)
                            return type;
                    }
                }
                catch { }
            }
            return null;
        }

        #endregion

        #region Cache Refresh

        /// <summary>
        /// Refresh the component cache for a registered type.
        /// Tries multiple strategies and merges results by instance ID to avoid duplicates.
        /// </summary>
        /// <summary>
        /// Full refresh including MonoBehaviourFilter. Used by ForceRefreshCache() only.
        /// Normal scan path uses RefreshAllCaches() which mutualizes MonoBehaviourFilter.
        /// </summary>
        private static UnityEngine.Object[] RefreshTypeCache(RegisteredTextType type)
        {
            var result = RefreshTypeCacheDirect(type);
            if (result != null && result.Length > 0)
                return result;

            // Fallback to MonoBehaviourFilter for this single type
            if (type.MonoBehaviourFilterState != StrategyState.Failed)
            {
                var seenIds = new HashSet<int>();
                var results = new List<UnityEngine.Object>();
                TryAddFromMonoBehaviourFilter(type, results, seenIds);
                type.MonoBehaviourFilterState = results.Count > 0 ? StrategyState.Works : StrategyState.Empty;
                if (results.Count > 0) return results.ToArray();
            }

            return null;
        }

        /// <summary>
        /// Refresh cache using direct strategies only (1: IL2CPP native, 2: TypeHelper, 3: Static lists).
        /// Does NOT use MonoBehaviourFilter — that's handled by RefreshViaMonoBehaviourFilterShared.
        /// Tracks per-strategy state: Works/Empty/Failed to skip known-bad strategies.
        /// PERF: When a strategy is known-Works, assigns the FindAllObjectsOfType result directly
        /// without wrapper allocations (no HashSet, no List, no ToArray).
        /// </summary>
        private static UnityEngine.Object[] RefreshTypeCacheDirect(RegisteredTextType type)
        {
            // FAST PATH: known-working strategy → direct assignment, zero allocations
            if (type.TypeHelperState == StrategyState.Works)
            {
                var found = TypeHelper.FindAllObjectsOfType(type.ComponentType);
                if (found != null && found.Length > 0) return found;
                type.TypeHelperState = StrategyState.Empty;
            }
            else if (type.IL2CPPNativeState == StrategyState.Works)
            {
                var found = FindAllComponentsIL2CPPCached(type.IL2CPPType);
                if (found != null && found.Length > 0) return found;
                type.IL2CPPNativeState = StrategyState.Empty;
            }
            else if (type.StaticListsState == StrategyState.Works)
            {
                var seenIds = new HashSet<int>();
                var results = new List<UnityEngine.Object>();
                TryAddFromStaticLists(type, results, seenIds);
                if (results.Count > 0) return results.ToArray();
                type.StaticListsState = StrategyState.Empty;
            }

            // DISCOVERY PATH: first run or winner stopped working
            // Try all non-Failed strategies in order

            // Strategy 1: IL2CPP native
            if (type.IL2CPPNativeState != StrategyState.Failed)
            {
                try
                {
                    if (il2cppScanAvailable && type.IL2CPPType != null)
                    {
                        var found = FindAllComponentsIL2CPPCached(type.IL2CPPType);
                        if (found != null && found.Length > 0)
                        {
                            type.IL2CPPNativeState = StrategyState.Works;
                            return found;
                        }
                    }
                    type.IL2CPPNativeState = StrategyState.Empty;
                }
                catch { type.IL2CPPNativeState = StrategyState.Failed; }
            }

            // Strategy 2: TypeHelper
            if (type.TypeHelperState != StrategyState.Failed)
            {
                try
                {
                    var found = TypeHelper.FindAllObjectsOfType(type.ComponentType);
                    if (found != null && found.Length > 0)
                    {
                        type.TypeHelperState = StrategyState.Works;
                        return found;
                    }
                    type.TypeHelperState = StrategyState.Empty;
                }
                catch { type.TypeHelperState = StrategyState.Failed; }
            }

            // Strategy 3: Static lists
            if (type.StaticListsState != StrategyState.Failed)
            {
                try
                {
                    var seenIds = new HashSet<int>();
                    var results = new List<UnityEngine.Object>();
                    TryAddFromStaticLists(type, results, seenIds);
                    if (results.Count > 0)
                    {
                        type.StaticListsState = StrategyState.Works;
                        return results.ToArray();
                    }
                    type.StaticListsState = StrategyState.Empty;
                }
                catch { type.StaticListsState = StrategyState.Failed; }
            }

            return null;
        }

        /// <summary>
        /// Mutualized MonoBehaviourFilter: ONE call to FindAllObjectsOfType(typeof(Component)),
        /// then distribute results to all types that need it.
        /// This avoids N expensive calls when N types all need the fallback.
        /// </summary>
        private static void RefreshViaMonoBehaviourFilterShared(List<RegisteredTextType> types)
        {
            UnityEngine.Object[] allComponents = null;

            try
            {
                allComponents = TypeHelper.FindAllObjectsOfType(typeof(Component));
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] MonoBehaviourFilter shared scan failed: {ex.Message}");
                // Mark all as Failed
                foreach (var type in types)
                    type.MonoBehaviourFilterState = StrategyState.Failed;
                return;
            }

            if (allComponents == null || allComponents.Length == 0)
            {
                foreach (var type in types)
                    type.MonoBehaviourFilterState = StrategyState.Empty;
                return;
            }

            // Build per-type result lists from the single shared scan
            var typeResults = new Dictionary<RegisteredTextType, List<UnityEngine.Object>>();
            var typeSeenIds = new Dictionary<RegisteredTextType, HashSet<int>>();
            foreach (var type in types)
            {
                typeResults[type] = new List<UnityEngine.Object>();
                typeSeenIds[type] = new HashSet<int>();

                // Pre-populate seenIds from existing cache (if any partial results from Phase 1)
                if (type.CachedComponents != null)
                {
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj != null) typeSeenIds[type].Add(obj.GetInstanceID());
                    }
                }
            }

            // Single pass over all components, distribute to matching types
            foreach (var obj in allComponents)
            {
                if (obj == null) continue;

                foreach (var type in types)
                {
                    bool matches = false;

                    // Try IsInstanceOfType first (works on Mono)
                    try { matches = type.ComponentType.IsInstanceOfType(obj); }
                    catch { }

                    // Fallback: match by type name (handles IL2CPP proxy types)
                    if (!matches)
                    {
                        var objType = obj.GetType();
                        string targetName = type.ComponentType.Name;
                        if (targetName.StartsWith("Il2Cpp")) targetName = targetName.Substring(6);

                        var current = objType;
                        while (current != null)
                        {
                            string name = current.Name;
                            if (name.StartsWith("Il2Cpp")) name = name.Substring(6);
                            if (name == targetName) { matches = true; break; }
                            current = current.BaseType;
                        }
                    }

                    if (matches)
                    {
                        int id = obj.GetInstanceID();
                        if (typeSeenIds[type].Add(id))
                            typeResults[type].Add(obj);
                    }
                }
            }

            // Apply results
            foreach (var type in types)
            {
                var results = typeResults[type];
                if (results.Count > 0)
                {
                    // Merge with any existing cache from Phase 1
                    if (type.CachedComponents != null && type.CachedComponents.Length > 0)
                    {
                        var merged = new List<UnityEngine.Object>(type.CachedComponents);
                        merged.AddRange(results);
                        type.CachedComponents = merged.ToArray();
                    }
                    else
                    {
                        type.CachedComponents = results.ToArray();
                    }

                    type.MonoBehaviourFilterState = StrategyState.Works;

                    if (!type.LoggedOnce)
                    {
                        TranslatorCore.LogDebug($"Scan: Found {results.Count} {type.Name} via MonoBehaviourFilter (shared scan)");
                        type.LoggedOnce = true;
                    }
                }
                else
                {
                    type.MonoBehaviourFilterState = StrategyState.Empty;
                }
            }
        }

        /// <summary>
        /// Strategy 1: IL2CPP native scan via cached Il2CppType.
        /// Throws on fundamental failure (marks strategy as Failed).
        /// </summary>
        private static void TryAddFromIL2CPPNative(RegisteredTextType type, List<UnityEngine.Object> results, HashSet<int> seenIds)
        {
            if (!il2cppScanAvailable || type.IL2CPPType == null) return;

            // Let exceptions propagate for strategy state tracking
            var found = FindAllComponentsIL2CPPCached(type.IL2CPPType);
            if (found == null) return;
            foreach (var obj in found)
            {
                if (obj == null) continue;
                int id = obj.GetInstanceID();
                if (seenIds.Add(id))
                    results.Add(obj);
            }
        }

        /// <summary>
        /// Strategy 2: TypeHelper (Resources.FindObjectsOfTypeAll).
        /// Throws on fundamental failure (marks strategy as Failed).
        /// </summary>
        private static void TryAddFromTypeHelper(RegisteredTextType type, List<UnityEngine.Object> results, HashSet<int> seenIds)
        {
            // Let exceptions propagate for strategy state tracking
            var found = TypeHelper.FindAllObjectsOfType(type.ComponentType);
            if (found == null) return;
            foreach (var obj in found)
            {
                if (obj == null) continue;
                int id = obj.GetInstanceID();
                if (seenIds.Add(id))
                    results.Add(obj);
            }
        }

        /// <summary>
        /// Strategy 3: Static list fields (NGUI mList, etc.).
        /// Throws on fundamental failure (marks strategy as Failed).
        /// </summary>
        private static void TryAddFromStaticLists(RegisteredTextType type, List<UnityEngine.Object> results, HashSet<int> seenIds)
        {
            var componentType = type.ComponentType;
            var staticFields = componentType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            string[] listFieldNames = { "mList", "sList", "s_Instances", "instances", "allInstances", "s_list" };

            foreach (var field in staticFields)
            {
                var fieldType = field.FieldType;
                if (fieldType == null) continue;

                bool nameMatch = false;
                foreach (var knownName in listFieldNames)
                {
                    if (string.Equals(field.Name, knownName, StringComparison.OrdinalIgnoreCase))
                    {
                        nameMatch = true;
                        break;
                    }
                }

                if (!nameMatch)
                {
                    string ftName = fieldType.Name ?? "";
                    if (!ftName.Contains("List") && !ftName.Contains("Array") &&
                        !ftName.Contains("Collection") && !ftName.Contains("HashSet"))
                        continue;
                }

                object listObj = null;
                try { listObj = field.GetValue(null); }
                catch { continue; }
                if (listObj == null) continue;

                var items = ExtractObjectsFromList(listObj, componentType);
                if (items == null) continue;

                foreach (var obj in items)
                {
                    if (obj == null) continue;
                    int id = obj.GetInstanceID();
                    if (seenIds.Add(id))
                        results.Add(obj);
                }
            }
        }

        private static void TryAddFromMonoBehaviourFilter(RegisteredTextType type, List<UnityEngine.Object> results, HashSet<int> seenIds)
        {
            try
            {
                // On IL2CPP, IsInstanceOfType may fail for proxy types.
                // Use type name matching as fallback: check if the runtime type name
                // matches or inherits from the target type name.
                string targetName = type.ComponentType.Name;
                // Strip Il2Cpp prefix for matching
                if (targetName.StartsWith("Il2Cpp")) targetName = targetName.Substring(6);

                var allComponents = TypeHelper.FindAllObjectsOfType(typeof(Component));
                if (allComponents == null) return;

                foreach (var obj in allComponents)
                {
                    if (obj == null) continue;

                    // Try IsInstanceOfType first (works on Mono)
                    if (type.ComponentType.IsInstanceOfType(obj))
                    {
                        int id = obj.GetInstanceID();
                        if (seenIds.Add(id))
                            results.Add(obj);
                        continue;
                    }

                    // Fallback: match by type name (handles IL2CPP proxy types)
                    var objType = obj.GetType();
                    var current = objType;
                    while (current != null)
                    {
                        string name = current.Name;
                        if (name.StartsWith("Il2Cpp")) name = name.Substring(6);
                        if (name == targetName)
                        {
                            int id = obj.GetInstanceID();
                            if (seenIds.Add(id))
                                results.Add(obj);
                            break;
                        }
                        current = current.BaseType;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Extract UnityEngine.Object instances from a list/collection object.
        /// Handles BetterList (NGUI), List, arrays, etc.
        /// </summary>
        private static UnityEngine.Object[] ExtractObjectsFromList(object listObj, Type expectedType)
        {
            try
            {
                var results = new List<UnityEngine.Object>();

                // Try 'buffer' field + 'size' field (BetterList pattern from NGUI)
                var bufferField = listObj.GetType().GetField("buffer", BindingFlags.Public | BindingFlags.Instance);
                var sizeField = listObj.GetType().GetField("size", BindingFlags.Public | BindingFlags.Instance);
                if (bufferField != null && sizeField != null)
                {
                    var buffer = bufferField.GetValue(listObj);
                    var size = sizeField.GetValue(listObj);
                    if (buffer is System.Collections.IEnumerable enumerable && size is int count)
                    {
                        int idx = 0;
                        foreach (var item in enumerable)
                        {
                            if (idx >= count) break;
                            if (item is UnityEngine.Object uobj)
                                results.Add(uobj);
                            idx++;
                        }
                        if (results.Count > 0) return results.ToArray();
                    }
                }

                // Try IEnumerable directly (List<T>, etc.)
                if (listObj is System.Collections.IEnumerable directEnum)
                {
                    foreach (var item in directEnum)
                    {
                        if (item is UnityEngine.Object uobj)
                            results.Add(uobj);
                    }
                    if (results.Count > 0) return results.ToArray();
                }

                // Try Count + Item indexer pattern
                var countProp = listObj.GetType().GetProperty("Count");
                var itemProp = listObj.GetType().GetProperty("Item");
                if (countProp != null && itemProp != null)
                {
                    int count2 = (int)countProp.GetValue(listObj, null);
                    for (int i = 0; i < count2; i++)
                    {
                        var item = itemProp.GetValue(listObj, new object[] { i });
                        if (item is UnityEngine.Object uobj)
                            results.Add(uobj);
                    }
                    if (results.Count > 0) return results.ToArray();
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region Component Processing

        /// <summary>
        /// Get text from a component using the appropriate method for its type.
        /// Built-in types use TypeHelper (handles IL2CPP casting); generic types use direct property access.
        /// </summary>
        private static string GetTextForType(object component, RegisteredTextType type)
        {
            if (type.Category == "TMP" || type.Category == "Unity" || type.Category == "TextMesh")
                return TypeHelper.GetText(component);
            // Generic types use direct property access
            return type.TextProp?.GetValue(component, null) as string;
        }

        /// <summary>
        /// Set text on a component using the appropriate method for its type.
        /// </summary>
        private static void SetTextForType(object component, RegisteredTextType type, string text)
        {
            if (type.Category == "TMP" || type.Category == "Unity" || type.Category == "TextMesh")
                TypeHelper.SetText(component, text);
            else
                type.TextProp?.SetValue(component, text, null);
        }

        /// <summary>
        /// Get font name from a component, using TypeHelper for built-in types.
        /// </summary>
        private static string GetFontNameForType(object component, RegisteredTextType type)
        {
            if (type.Category == "TMP" || type.Category == "Unity" || type.Category == "TextMesh")
                return TypeHelper.GetFontName(component);

            // Generic types: use font property directly
            if (type.FontProp == null) return null;
            try
            {
                var fontObj = type.FontProp.GetValue(component, null);
                if (fontObj is UnityEngine.Object uobj && !string.IsNullOrEmpty(uobj.name))
                    return uobj.name;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Process a single component for a registered text type.
        /// </summary>
        private static void ProcessComponentForType(object component, RegisteredTextType type)
        {
            try
            {
                var comp = component as Component;
                if (comp == null) return;

                int instanceId = comp.GetInstanceID();

                // Per-cycle dedup: skip if already processed through another overlapping type
                // (e.g. TMP_Text scan includes TextMeshProUGUI components)
                if (!_processedThisCycle.Add(instanceId))
                    return;

                // Skip if own UI and should not be translated
                if (TranslatorCore.ShouldSkipTranslation(comp))
                    return;

                // Skip if translation disabled for this font
                string fontName = GetFontNameForType(component, type);
                if (!string.IsNullOrEmpty(fontName) && !FontManager.IsTranslationEnabled(fontName))
                    return;

                // Skip if already identified as InputField user text
                if (inputFieldTextIds.Contains(instanceId))
                    return;

                // First-time check: is this the textComponent of an InputField?
                if (type.Category != "TextMesh" && TypeHelper.IsInputFieldTextComponent(component))
                {
                    inputFieldTextIds.Add(instanceId);
                    TranslatorCore.LogDebug($"[Scanner] Excluded InputField textComponent: {comp.gameObject.name}");
                    return;
                }

                string currentText = GetTextForType(component, type);

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

                // Check if own UI (use UI-specific prompt)
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(comp);
                string translated = TranslatorCore.TranslateTextWithTracking(currentText, comp, isOwnUI);
                if (translated != currentText)
                {
                    SetTextForType(component, type, translated);

                    // Force refresh based on type flags
                    if (type.NeedsForceMeshUpdate)
                        TypeHelper.ForceMeshUpdate(component);
                    else if (type.NeedsSetAllDirty)
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

        #region ForceRefreshAllText

        /// <summary>
        /// Force refresh all text components by re-assigning their text.
        /// If translations are disabled, restores original texts from cache.
        /// This triggers Harmony patches to re-process and apply translations/fonts.
        /// </summary>
        public static void ForceRefreshAllText()
        {
            int refreshed = 0;
            int restored = 0;

            bool globalRestore = !TranslatorCore.Config.enable_translations;

            try
            {
                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null) continue;
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj == null) continue;
                        RefreshComponent(obj, type, globalRestore, ref refreshed, ref restored);
                    }
                }

                if (restored > 0)
                    TranslatorCore.LogDebug($"[Scanner] Restored {restored} original texts, refreshed {refreshed} components");
                else
                    TranslatorCore.LogDebug($"[Scanner] Force refreshed {refreshed} text components");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] ForceRefreshAllText error: {ex.Message}");
            }
        }

        /// <summary>
        /// Refresh a single component: either restore original or trigger re-translation.
        /// </summary>
        private static void RefreshComponent(UnityEngine.Object obj, RegisteredTextType type, bool globalRestore, ref int refreshed, ref int restored)
        {
            try
            {
                // For IL2CPP native scan results, need TryCast to get the typed component
                object component = obj;
                if (type.TryCastMethod != null && TranslatorCore.Adapter?.IsIL2CPP == true)
                {
                    if (!type.ComponentType.IsInstanceOfType(obj))
                    {
                        component = TryCastToType(obj, type.TryCastMethod);
                        if (component == null) return;
                    }
                }

                int instanceId = TypeHelper.GetInstanceID(component);
                if (instanceId == -1) return;

                // Check per-font translation state (always uses original font name for settings)
                string compFontName = GetFontNameForType(component, type);
                bool fontDisabled = !FontManager.IsTranslationEnabledForComponent(instanceId, compFontName);
                bool shouldRestore = globalRestore || fontDisabled;

                if (shouldRestore)
                {
                    // Restore original font (for built-in types)
                    if (type.Category == "TMP" || type.Category == "Unity" || type.Category == "TextMesh")
                        FontManager.RestoreOriginalFont(component);

                    string original = GetOriginalText(instanceId);
                    if (original != null)
                    {
                        SetTextForType(component, type, original);
                        ClearOriginalText(instanceId);
                        processedTextHashes.Remove(instanceId);
                        restored++;
                        return;
                    }
                }

                // Normal refresh path (trigger Harmony patch)
                string currentText = GetTextForType(component, type);
                if (!string.IsNullOrEmpty(currentText))
                {
                    if (type.NeedsForceMeshUpdate)
                    {
                        // TMP needs empty-then-restore to force re-render
                        SetTextForType(component, type, "");
                        SetTextForType(component, type, currentText);
                        TypeHelper.ForceMeshUpdate(component);
                    }
                    else
                    {
                        SetTextForType(component, type, currentText);
                    }
                    refreshed++;
                }
            }
            catch { }
        }

        #endregion

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

            // Always update: the same component can show different texts over time
            // (e.g., tooltip reused for different items). The get_text patch needs
            // the CURRENT original, not the first one ever seen.
            componentOriginals[instanceId] = originalText;
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

        #endregion

        #region Font Operations (Restore/Refresh/Highlight)

        /// <summary>
        /// Restore originals for components using a specific font.
        /// Call when per-font translation is disabled.
        /// </summary>
        public static void RestoreOriginalsForFont(string fontName, string oldFallback = null)
        {
            if (string.IsNullOrEmpty(fontName)) return;
            int restored = 0;

            try
            {
                var fontNamesToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fontName };
                string newFallback = FontManager.GetConfiguredFallback(fontName);
                if (!string.IsNullOrEmpty(newFallback))
                    fontNamesToMatch.Add(newFallback);
                if (!string.IsNullOrEmpty(oldFallback))
                    fontNamesToMatch.Add(oldFallback);

                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null) continue;
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj == null) continue;
                        restored += RestoreComponentForFont(obj, type, fontNamesToMatch);
                    }
                }

                if (restored > 0)
                    TranslatorCore.LogDebug($"[Scanner] Restored {restored} originals for font: {fontName}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] RestoreOriginalsForFont error: {ex.Message}");
            }
        }

        private static int RestoreComponentForFont(UnityEngine.Object obj, RegisteredTextType type, HashSet<string> fontNames)
        {
            try
            {
                object component = ResolveComponent(obj, type);
                if (component == null) return 0;

                int id = TypeHelper.GetInstanceID(component);
                if (id == -1) return 0;

                // Match by both current and original font name
                string compFont = GetFontNameForType(component, type);
                if (!FontManager.ComponentMatchesFont(id, compFont, fontNames)) return 0;

                // Restore font before text
                if (type.Category == "TMP" || type.Category == "Unity" || type.Category == "TextMesh")
                    FontManager.RestoreOriginalFont(component);

                string original = GetOriginalText(id);
                if (original != null)
                {
                    SetTextForType(component, type, original);
                    ClearOriginalText(id);
                    processedTextHashes.Remove(id);
                    return 1;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Re-translate components for a specific font.
        /// Call when per-font translation is re-enabled.
        /// </summary>
        public static void RefreshForFont(string fontName, string oldFallback = null)
        {
            if (string.IsNullOrEmpty(fontName)) return;
            int refreshed = 0;

            try
            {
                ForceRefreshCache();

                var fontNamesToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fontName };
                string newFallback = FontManager.GetConfiguredFallback(fontName);
                if (!string.IsNullOrEmpty(newFallback))
                    fontNamesToMatch.Add(newFallback);
                if (!string.IsNullOrEmpty(oldFallback))
                    fontNamesToMatch.Add(oldFallback);

                TranslatorCore.LogDebug($"[Scanner] RefreshForFont: matching fonts [{string.Join(", ", fontNamesToMatch)}]");

                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null) continue;
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj == null) continue;
                        refreshed += RefreshComponentForFont(obj, type, fontNamesToMatch);
                    }
                }

                TranslatorCore.LogDebug($"[Scanner] Refreshed {refreshed} components for font: {fontName}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] RefreshForFont error: {ex.Message}");
            }
        }

        private static int RefreshComponentForFont(UnityEngine.Object obj, RegisteredTextType type, HashSet<string> fontNames)
        {
            try
            {
                object component = ResolveComponent(obj, type);
                if (component == null) return 0;

                int id = TypeHelper.GetInstanceID(component);
                if (id == -1) return 0;

                string compFont = GetFontNameForType(component, type);
                if (!FontManager.ComponentMatchesFont(id, compFont, fontNames)) return 0;

                processedTextHashes.Remove(id);

                string currentText = GetTextForType(component, type);
                if (!string.IsNullOrEmpty(currentText))
                {
                    // Set empty then back to force re-render with potentially new font
                    SetTextForType(component, type, "");
                    SetTextForType(component, type, currentText);
                    if (type.NeedsForceMeshUpdate)
                        TypeHelper.ForceMeshUpdate(component);
                    return 1;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Resolve a Unity Object to its typed component (handles IL2CPP TryCast).
        /// </summary>
        private static object ResolveComponent(UnityEngine.Object obj, RegisteredTextType type)
        {
            if (type.TryCastMethod != null && TranslatorCore.Adapter?.IsIL2CPP == true)
            {
                if (!type.ComponentType.IsInstanceOfType(obj))
                    return TryCastToType(obj, type.TryCastMethod);
            }
            return obj;
        }

        #endregion

        #region Font Highlight (in-game identification)

        // Stores original colors per instance ID for restore after highlight
        private static readonly Dictionary<int, Color> _highlightOriginalColors = new Dictionary<int, Color>();
        private static string _highlightedFontName = null;

        // Highlight color for matching font, dim color for non-matching
        private static readonly Color HighlightColor = new Color(1f, 0f, 0.8f, 1f); // Magenta
        private static readonly Color DimColor = new Color(1f, 1f, 1f, 0.15f); // Very transparent

        /// <summary>
        /// Highlight all text components using a specific font.
        /// Matching components get a bright color, others get dimmed.
        /// </summary>
        public static void HighlightFont(string fontName)
        {
            if (_highlightedFontName != null)
                ClearHighlight();

            _highlightedFontName = fontName;
            EnsureLateUpdateRunner();
            var processedIds = new HashSet<int>();

            try
            {
                ForceRefreshCache();

                // Pass 1: scanner cache (FindObjectsOfType snapshot)
                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null) continue;
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj == null) continue;
                        try
                        {
                            object component = ResolveComponent(obj, type);
                            if (component == null) continue;
                            int id = TypeHelper.GetInstanceID(component);
                            if (id == -1) continue;
                            processedIds.Add(id);
                            HighlightComponent(component, id, fontName);
                        }
                        catch { }
                    }
                }

                // Pass 2: components seen by the patch but not in scanner cache
                List<KeyValuePair<int, object>> patchRefsSnapshot;
                try { patchRefsSnapshot = new List<KeyValuePair<int, object>>(TranslatorPatches.PatchedComponentRefs); }
                catch { patchRefsSnapshot = new List<KeyValuePair<int, object>>(); }
                foreach (var kvp in patchRefsSnapshot)
                {
                    if (processedIds.Contains(kvp.Key)) continue;
                    if (kvp.Value == null) continue;
                    try
                    {
                        HighlightComponent(kvp.Value, kvp.Key, fontName);
                    }
                    catch { }
                }

                // Keep Animators disabled during highlight (diagnostic mode).
                // They override text color every frame when enabled.
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] HighlightFont error: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear all font highlights, restoring original colors.
        /// </summary>
        public static void ClearHighlight()
        {
            if (_highlightedFontName == null && _highlightOriginalColors.Count == 0) return;

            try
            {
                // Restore from scanner cache
                foreach (var type in _registeredTypes)
                {
                    if (type.CachedComponents == null) continue;
                    foreach (var obj in type.CachedComponents)
                    {
                        if (obj == null) continue;
                        try
                        {
                            object component = ResolveComponent(obj, type);
                            if (component == null) continue;
                            int id = TypeHelper.GetInstanceID(component);
                            if (id == -1) continue;
                            RestoreComponentColor(component, id);
                        }
                        catch { }
                    }
                }

                // Restore from patch-tracked components (not in scanner cache)
                List<KeyValuePair<int, object>> clearSnapshot;
                try { clearSnapshot = new List<KeyValuePair<int, object>>(TranslatorPatches.PatchedComponentRefs); }
                catch { clearSnapshot = new List<KeyValuePair<int, object>>(); }
                foreach (var kvp in clearSnapshot)
                {
                    if (kvp.Value == null) continue;
                    try { RestoreComponentColor(kvp.Value, kvp.Key); }
                    catch { }
                }
            }
            catch { }

            _highlightOriginalColors.Clear();
            _highlightedFontName = null;
        }

        private static void HighlightComponent(object component, int id, string targetFontName)
        {
            // Always skip our own UI components for highlight (regardless of translate_mod_ui)
            var comp = component as Component;
            if (comp != null && TranslatorCore.IsOwnUI(comp)) return;

            // Match by settings font name (always the original, not the replacement)
            string compFont = TypeHelper.GetFontName(component);
            string settingsFont = FontManager.GetSettingsFontName(id, compFont);
            bool matches = !string.IsNullOrEmpty(settingsFont) && string.Equals(settingsFont, targetFontName, StringComparison.OrdinalIgnoreCase);

            // Store original color
            Color originalColor = TypeHelper.GetTextColor(component);
            if (!_highlightOriginalColors.ContainsKey(id))
                _highlightOriginalColors[id] = originalColor;

            // Apply highlight or dim
            TypeHelper.SetTextColor(component, matches ? HighlightColor : DimColor);
        }

        private static void RestoreComponentColor(object component, int id)
        {
            if (_highlightOriginalColors.TryGetValue(id, out var originalColor))
            {
                TypeHelper.SetTextColor(component, originalColor);
            }
        }

        #endregion

        #region Scanning Control

        /// <summary>
        /// Check if scanning should be skipped (no useful work to do).
        /// </summary>
        private static bool ShouldSkipScanning()
        {
            if (!TranslatorCore.Config.enable_translations)
                return true;

            if (TranslatorCore.TranslationCache.Count > 0)
                return false;

            if (TranslatorCore.Config.enable_ai)
                return false;

            if (TranslatorCore.Config.capture_keys_only)
                return false;

            return true;
        }

        #endregion

        #region IL2CPP Initialization

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
                            TranslatorCore.LogDebug("Found Il2CppType.Of<T>() method");
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
                            TranslatorCore.LogDebug($"Found Resources.FindObjectsOfTypeAll({paramType.Name})");
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
                            TranslatorCore.LogDebug("Found IL2CPP.TryCast<T>() method");
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
                                TranslatorCore.LogDebug("Found Il2CppObjectBase.TryCast<T>() method");
                                break;
                            }
                        }
                    }
                }

                il2cppScanAvailable = il2cppTypeOfMethod != null && resourcesFindAllMethod != null;

                // Share IL2CPP methods with TypeHelper for use by other components
                if (il2cppScanAvailable)
                    TypeHelper.SetIL2CPPMethods(il2cppTypeOfMethod, resourcesFindAllMethod,
                        tryCastMethod, tryCastMethod != null && tryCastMethod.IsStatic);

                if (il2cppScanAvailable)
                    TranslatorCore.LogDebug($"IL2CPP scan initialized (TryCast={tryCastMethod != null})");
                else
                    TranslatorCore.LogWarning($"IL2CPP scan not available");
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
            // Protect clone atlases every frame — prevents character purging
            // when multiple cloned fonts share the native font rasterizer
            FontManager.ProtectCloneAtlases();

            // Check for stabilized typewriting texts and trigger their translation
            TranslatorPatches.ProcessStabilizedTypewriting();

            // After new chars were added to a clone atlas, force all components to re-render
            // so they pick up updated glyph positions. Debounced to max once per second.
            if (FontManager.ConsumePendingRefresh())
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastForceRefreshTime > 1f)
                {
                    _lastForceRefreshTime = now;
                    ForceRefreshAllText();
                }
            }

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
                    // Skip destroyed components (IL2CPP proxy still exists but Unity object is gone)
                    if (comp is UnityEngine.Object uobj && uobj == null) continue;

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
                        TranslatorCore.LogDebug($"[Apply OK] {expectedPreview}");
                    }
                    else
                    {
                        // Clear tracking so scanner retries this component on next cycle
                        int skipId = TypeHelper.GetInstanceID(comp);
                        if (skipId != -1)
                        {
                            processedTextHashes.Remove(skipId);
                            TranslatorCore.ClearSeenText(skipId);
                        }
                        TranslatorCore.LogDebug($"[Apply SKIP] expected='{expectedPreview}' actual='{actualPreview}'");
                    }
                }
                catch { }
            }
        }

        #endregion

        #region IL2CPP Helpers

        /// <summary>
        /// Try to cast an IL2CPP object to a specific type using a cached TryCast method.
        /// </summary>
        private static object TryCastToType(object obj, MethodInfo typedTryCastMethod)
        {
            if (obj == null || typedTryCastMethod == null) return null;

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

        #region Legacy API (backward compatibility)

        /// <summary>
        /// Legacy method - now delegates to Scan().
        /// Kept for backward compatibility during migration.
        /// </summary>
        public static void ScanMono()
        {
            Scan();
        }

        /// <summary>
        /// Legacy method - now delegates to Scan().
        /// Kept for backward compatibility during migration.
        /// </summary>
        public static void ScanIL2CPP()
        {
            if (!il2cppMethodsInitialized) InitializeIL2CPP();
            Scan();
        }

        #endregion

        #region LateUpdate Override (font scale + highlight after Animator)

        private static bool _lateUpdateRunning = false;
        private static Coroutine _lateUpdateCoroutine;
        // Cached component list for LateUpdate (rebuilt periodically, reused per frame)
        private static List<KeyValuePair<int, object>> _lateUpdateComponents;
        private static bool _lateUpdateCacheDirty = true;
        private static float _lateUpdateLastRebuild = 0f;
        private const float LATE_UPDATE_CACHE_INTERVAL = 1f;

        /// <summary>Mark the LateUpdate cache as dirty (call after scan refresh).</summary>
        public static void InvalidateLateUpdateCache() { _lateUpdateCacheDirty = true; }

        /// <summary>
        /// Ensures the per-frame coroutine is running.
        /// Uses a coroutine instead of MonoBehaviour to work on IL2CPP.
        /// </summary>
        public static void EnsureLateUpdateRunner()
        {
            if (_lateUpdateRunning) return;
            try
            {
                _lateUpdateCoroutine = UniverseLib.RuntimeHelper.StartCoroutine(LateUpdateCoroutine());
                _lateUpdateRunning = true;
                TranslatorCore.LogDebug("[Scanner] LateUpdate coroutine started");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[Scanner] Failed to start LateUpdate coroutine: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop the LateUpdate coroutine. Called during shutdown to prevent IL2CPP hangs.
        /// </summary>
        public static void StopLateUpdateRunner()
        {
            if (_lateUpdateCoroutine != null)
            {
                try { UniverseLib.RuntimeHelper.StopCoroutine(_lateUpdateCoroutine); } catch { }
                _lateUpdateCoroutine = null;
            }
            _lateUpdateRunning = false;
        }

        private static System.Collections.IEnumerator LateUpdateCoroutine()
        {
            var waitEndOfFrame = new WaitForEndOfFrame();
            while (!TranslatorCore.ShuttingDown)
            {
                yield return waitEndOfFrame;
                OnLateUpdate();
            }
            _lateUpdateRunning = false;
        }

        /// <summary>
        /// Called every LateUpdate (after Animator). Re-applies highlight color
        /// and font scale on components where the game overrides our values.
        /// </summary>
        internal static void OnLateUpdate()
        {
            try
            {
                if (_highlightedFontName == null) return;

                // Don't rebuild cache until types are registered and first scan has run
                if (_registeredTypes.Count == 0 || lastComponentCacheTime == 0f)
                {
                    _lateUpdateCacheDirty = true;
                    return; // wait for scanner to populate
                }
                // Periodically rebuild to catch dynamically created components
                float now = Time.realtimeSinceStartup;
                if (!_lateUpdateCacheDirty && _lateUpdateComponents != null
                    && now - _lateUpdateLastRebuild > LATE_UPDATE_CACHE_INTERVAL)
                    _lateUpdateCacheDirty = true;

                if (_lateUpdateCacheDirty || _lateUpdateComponents == null)
                {
                    _lateUpdateComponents = new List<KeyValuePair<int, object>>();
                    var processedIds = new HashSet<int>();

                    // Scanner cache components
                    foreach (var type in _registeredTypes)
                    {
                        if (type.CachedComponents == null) continue;
                        foreach (var obj in type.CachedComponents)
                        {
                            if (obj == null) continue;
                            try
                            {
                                object component = ResolveComponent(obj, type);
                                if (component == null) continue;
                                int id = TypeHelper.GetInstanceID(component);
                                if (id == -1 || !processedIds.Add(id)) continue;
                                _lateUpdateComponents.Add(new KeyValuePair<int, object>(id, component));
                            }
                            catch { }
                        }
                    }

                    // Patch-tracked components not in scanner cache
                    // Snapshot to avoid "Collection was modified" if prefix fires during iteration
                    List<KeyValuePair<int, object>> patchedSnapshot;
                    try { patchedSnapshot = new List<KeyValuePair<int, object>>(TranslatorPatches.PatchedComponentRefs); }
                    catch { patchedSnapshot = new List<KeyValuePair<int, object>>(); }

                    foreach (var kvp in patchedSnapshot)
                    {
                        if (kvp.Value == null || !processedIds.Add(kvp.Key)) continue;
                        _lateUpdateComponents.Add(kvp);
                    }

                    _lateUpdateCacheDirty = false;
                    _lateUpdateLastRebuild = now;
                }

                // Snapshot fontNameCache for thread safety
                Dictionary<int, string> fontNameSnapshot;
                try { fontNameSnapshot = new Dictionary<int, string>(TranslatorPatches.FontNameCache); }
                catch { fontNameSnapshot = new Dictionary<int, string>(); }

                foreach (var kvp in _lateUpdateComponents)
                {
                    int id = kvp.Key;
                    object component = kvp.Value;

                    // Get font name from cache or component
                    string fontName;
                    if (!fontNameSnapshot.TryGetValue(id, out fontName) || string.IsNullOrEmpty(fontName))
                    {
                        fontName = TypeHelper.GetFontName(component);
                        if (string.IsNullOrEmpty(fontName)) continue;
                    }

                    string settingsFontName = FontManager.GetSettingsFontName(id, fontName);

                    // Highlight color override (re-apply every frame to beat Animator)
                    if (_highlightedFontName != null)
                    {
                        // Skip own UI
                        var hlComp = component as Component;
                        if (hlComp != null && TranslatorCore.IsOwnUI(hlComp)) continue;

                        bool matches = string.Equals(settingsFontName, _highlightedFontName, StringComparison.OrdinalIgnoreCase);
                        Color target = matches ? HighlightColor : DimColor;
                        Color current = TypeHelper.GetTextColor(component);
                        if (current != target)
                            TypeHelper.SetTextColor(component, target);
                    }
                }
            }
            catch { }
        }

        #endregion
    }

}
