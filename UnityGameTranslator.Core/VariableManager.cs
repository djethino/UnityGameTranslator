using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UniverseLib;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Manages game variables (player name, clan name, etc.) that appear in translatable text.
    /// Variables are replaced with [!STR*N] placeholders for cache reuse, then restored after translation.
    /// Works on both Mono and IL2CPP via reflection.
    /// </summary>
    public static class VariableManager
    {
        #region Constants

        public const string Prefix = "[!STR*";
        public const string Suffix = "]";

        #endregion

        #region Data

        private static List<VariableDefinition> _definitions = new List<VariableDefinition>();
        private static Dictionary<int, string> _currentValues = new Dictionary<int, string>(); // Id → value
        private static int _nextId = 0; // Auto-increment ID, never reused
        private static bool _needsRefresh = true; // Flag: forces a resolve on the next OnUpdate tick

        /// <summary>True if any variables are defined.</summary>
        public static bool HasVariables => _definitions.Count > 0;

        /// <summary>Get all variable definitions (for UI display).</summary>
        public static IReadOnlyList<VariableDefinition> Definitions => _definitions;

        #endregion

        #region Types

        public class VariableDefinition
        {
            public int Id;             // Stable index for [!STR*N] — never reused after deletion
            public string Name;       // Display name: "PlayerName"
            public string ClassName;   // "GameManager"
            public string FieldPath;   // "Instance.playerName" or "playerName" (static)
        }

        public class VariableCandidate
        {
            public string ClassName;
            public string FieldPath;
            public string CurrentValue;
            public bool IsStatic;
            public int MatchRank;      // 3 = exact, 2 = value contains search, 1 = search contains value
        }

        #endregion

        #region Persistence (JSON)

        public static void LoadFromJson(JToken token)
        {
            // Copy-on-write: the translation worker iterates _definitions and reads
            // _currentValues concurrently — mutate copies, then swap the references
            // (reference assignment is atomic).
            var defs = new List<VariableDefinition>();

            if (token != null && token.Type == JTokenType.Array)
            {
                foreach (var item in token)
                {
                    if (item.Type != JTokenType.Object) continue;
                    var obj = (JObject)item;

                    int id = obj.Value<int>("id");
                    var def = new VariableDefinition
                    {
                        Id = id,
                        Name = obj.Value<string>("name") ?? "",
                        ClassName = obj.Value<string>("class") ?? "",
                        FieldPath = obj.Value<string>("path") ?? ""
                    };

                    if (!string.IsNullOrEmpty(def.ClassName) && !string.IsNullOrEmpty(def.FieldPath))
                    {
                        defs.Add(def);
                        if (id >= _nextId) _nextId = id + 1;
                    }
                }
            }

            _definitions = defs;
            _currentValues = new Dictionary<int, string>();

            if (defs.Count > 0)
                TranslatorCore.LogInfo($"[VariableManager] Loaded {defs.Count} variable definitions (nextId={_nextId})");
        }

        public static JToken SaveToJson()
        {
            if (_definitions.Count == 0) return null;

            var array = new JArray();
            foreach (var def in _definitions)
            {
                array.Add(new JObject
                {
                    ["id"] = def.Id,
                    ["name"] = def.Name,
                    ["class"] = def.ClassName,
                    ["path"] = def.FieldPath
                });
            }
            return array;
        }

        #endregion

        #region Variable Management

        public static void AddVariable(string name, string className, string fieldPath)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(fieldPath)) return;

            // Check for duplicates
            foreach (var def in _definitions)
            {
                if (def.ClassName == className && def.FieldPath == fieldPath)
                    return;
            }

            int id = _nextId++;
            var defs = new List<VariableDefinition>(_definitions)
            {
                new VariableDefinition
                {
                    Id = id,
                    Name = name,
                    ClassName = className,
                    FieldPath = fieldPath
                }
            };
            _definitions = defs; // copy-on-write: worker may be iterating the old list

            TranslatorCore.SetMetadataDirty();
            TranslatorCore.LogInfo($"[VariableManager] Added variable [!STR*{id}]: {name} ({className}.{fieldPath})");
        }

        /// <summary>Remove a variable by its stable Id (not list position).</summary>
        public static void RemoveVariable(int id)
        {
            var def = _definitions.Find(d => d.Id == id);
            if (def == null) return;

            // Copy-on-write: worker may be iterating/reading the old collections
            var defs = new List<VariableDefinition>(_definitions);
            defs.Remove(def);
            _definitions = defs;

            var values = new Dictionary<int, string>(_currentValues);
            values.Remove(id);
            _currentValues = values;

            TranslatorCore.SetMetadataDirty();
            TranslatorCore.LogInfo($"[VariableManager] Removed variable [!STR*{id}]: {def.Name}");
        }

        #endregion

        #region Value Resolution (Reflection)

        /// <summary>
        /// Force a value refresh on the next OnUpdate tick (bypasses the throttle).
        /// Called on scene change.
        /// </summary>
        public static void MarkNeedsRefresh()
        {
            _needsRefresh = true;
        }

        private static float _lastRefreshTime = -999f;
        private const float RefreshIntervalSeconds = 2f;

        // Own frame counter (Time.frameCount is unreliable off the main thread) and
        // main thread id, both maintained by OnUpdate. _mainThreadId stays -1 until
        // the first frame, so RefreshOnMiss safely no-ops before then.
        private static int _frameCounter;
        private static int _lastMissRefreshFrame = -1;
        private static int _mainThreadId = -1;

        /// <summary>
        /// Main-thread periodic refresh, called every frame from TranslatorCore.OnUpdate.
        /// Games (re)assign their state (seeds, player names...) long after the scene
        /// loads — new run, save load — so refreshing only on scene change serves stale
        /// values, extraction misses, and number extraction pollutes the cache with
        /// one key per seed. No-op without defined variables; throttled otherwise.
        /// </summary>
        public static void OnUpdate(float currentTime)
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            _frameCounter++;

            if (_definitions.Count == 0) return;
            if (!_needsRefresh && currentTime - _lastRefreshTime < RefreshIntervalSeconds) return;

            _needsRefresh = false;
            _lastRefreshTime = currentTime;
            RefreshValues();
        }

        /// <summary>
        /// Re-resolve values right before a never-seen text is queued for translation.
        /// The periodic tick cannot close the race where the game assigns a value and
        /// displays it immediately — the queued text's cache key is built by the worker
        /// from _currentValues, so stale values there mint a polluted key. The miss is
        /// the only moment a new cache entry can be born, which makes it the correct
        /// refresh trigger. Throttled to once per frame (scene-load floods queue
        /// hundreds of texts) and main-thread only (resolution may call Unity APIs).
        /// Returns true when a refresh actually ran, so the caller can retry its
        /// lookup once with fresh values — the throttle guarantees the retry cannot loop.
        /// </summary>
        public static bool RefreshOnMiss()
        {
            if (_definitions.Count == 0) return false;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId) return false;
            if (_lastMissRefreshFrame == _frameCounter) return false;

            _lastMissRefreshFrame = _frameCounter;
            RefreshValues();
            return true;
        }

        /// <summary>
        /// Resolve all variable values (main thread only: resolution may call Unity
        /// APIs). Keeps old values if re-resolution fails (instance temporarily
        /// destroyed). Builds a new dictionary and swaps the reference so the worker
        /// can read _currentValues concurrently without torn state.
        /// </summary>
        public static void RefreshValues()
        {
            var defs = _definitions;
            if (defs.Count == 0) return;

            var newValues = new Dictionary<int, string>(_currentValues);
            foreach (var def in defs)
            {
                string value = ResolveValue(def);
                if (value != null)
                    newValues[def.Id] = value;
                // If null, keep the old cached value (don't erase)
            }
            _currentValues = newValues;
        }

        /// <summary>
        /// Get the current value of a variable by index.
        /// </summary>
        public static string GetValue(int index)
        {
            var values = _currentValues; // snapshot: main thread swaps the reference
            if (values.TryGetValue(index, out string val))
                return val;
            return null;
        }

        /// <summary>
        /// Resolve a variable value. The historical pipeline (Unity object root +
        /// property traversal) runs first so existing definitions keep resolving
        /// through the exact same path; the static/singleton root pipeline only fires
        /// where the historical one returns null (plain C# game classes, static
        /// fields, Singleton&lt;T&gt; pattern — all previously unresolvable).
        /// </summary>
        private static string ResolveValue(VariableDefinition def)
        {
            if (def == null) return null;

            Type rootType = FindType(def.ClassName);
            if (rootType == null) return null;

            string[] parts = def.FieldPath.Split('.');

            string result = ResolveViaUnityInstance(rootType, parts);
            if (result != null) return result;

            return ResolveViaStaticRoot(rootType, parts);
        }

        /// <summary>
        /// Historical pipeline: find a live Unity object of the root type and traverse
        /// the path from its instance.
        /// </summary>
        private static string ResolveViaUnityInstance(Type rootType, string[] parts)
        {
            try
            {
                // Non-Unity roots can never be found by FindAllObjectsOfType — skip
                // (avoids pointless engine calls and IL2CPP warning spam on refresh)
                if (!typeof(UnityEngine.Object).IsAssignableFrom(rootType)) return null;

                var instances = TypeHelper.FindAllObjectsOfType(rootType);
                if (instances == null || instances.Length == 0) return null;

                var obj = instances[0];
                if (obj == null) return null;

                Type actualType;
                try { actualType = obj.GetActualType(); }
                catch { actualType = rootType; }

                object current;
                try { current = TypeHelper.Il2CppCast(obj, actualType) ?? obj; }
                catch { current = obj; }

                for (int i = 0; i < parts.Length; i++)
                {
                    current = GetMemberValue(current, parts[i]);
                    if (current == null) return null;
                }

                if (current is string str) return str;
                return current.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Fallback pipeline: resolve the first path segment as a static member
        /// (singleton Instance, static field...) on the root type or its base classes,
        /// then traverse the rest from that object.
        /// </summary>
        private static string ResolveViaStaticRoot(Type rootType, string[] parts)
        {
            try
            {
                object current = GetStaticMemberValue(rootType, parts[0]);
                if (current == null) return null;

                for (int i = 1; i < parts.Length; i++)
                {
                    current = GetMemberValue(current, parts[i]);
                    if (current == null) return null;
                }

                if (current is string str) return str;
                return current.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Read one instance member on an object. Lookup order preserves the historical
        /// behavior — public property (DeclaredOnly, then inherited) — then extends it:
        /// non-public property, field, "_"-prefixed field. Each later step only fires
        /// where the previous ones found nothing (previously a null result).
        /// </summary>
        private static object GetMemberValue(object instance, string memberName)
        {
            if (instance == null || string.IsNullOrEmpty(memberName)) return null;

            Type type;
            try { type = instance.GetActualType(); }
            catch { type = instance.GetType(); }

            var prop = FindProperty(type, memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?? FindProperty(type, memberName, BindingFlags.Public | BindingFlags.Instance)
                ?? FindProperty(type, memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
            {
                try { return prop.GetValue(instance, null); }
                catch { return null; }
            }

            var fieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var field = type.GetField(memberName, fieldFlags) ?? type.GetField("_" + memberName, fieldFlags);
            if (field != null)
            {
                try { return field.GetValue(instance); }
                catch { return null; }
            }

            return null;
        }

        /// <summary>GetProperty guarded against AmbiguousMatchException (properties re-declared with "new").</summary>
        private static PropertyInfo FindProperty(Type type, string name, BindingFlags flags)
        {
            try
            {
                return type.GetProperty(name, flags);
            }
            catch
            {
                try
                {
                    foreach (var p in type.GetProperties(flags))
                        if (p.Name == name) return p;
                }
                catch { }
                return null;
            }
        }

        /// <summary>
        /// Read a static member by name on a type or any of its base classes.
        /// Walking the hierarchy manually (DeclaredOnly per level) reaches private
        /// statics of generic bases like Singleton&lt;T&gt;.instance, which
        /// BindingFlags.FlattenHierarchy never returns.
        /// </summary>
        private static object GetStaticMemberValue(Type type, string memberName)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                try
                {
                    var field = t.GetField(memberName, flags);
                    if (field != null) return field.GetValue(null);

                    var prop = FindProperty(t, memberName, flags);
                    if (prop != null && prop.CanRead) return prop.GetValue(null, null);
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// The mod's own assembly. Variables read values out of the GAME, so nothing here is ever
        /// a legitimate target — while the mod does hold, in memory, things the game does not:
        /// the API token, in the config object and in the HTTP client's Authorization header.
        /// A translation is written by someone else, so the safe rule is that it may only reach
        /// the game it translates.
        /// </summary>
        private static readonly System.Reflection.Assembly ModAssembly = typeof(TranslatorCore).Assembly;

        private static Type FindType(string className)
        {
            // Try direct lookup first (fast, no GetTypes iteration)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == ModAssembly) continue;

                try
                {
                    var type = asm.GetType(className);
                    if (type != null) return type;
                }
                catch { }
            }

            // IL2CPP: try with Il2Cpp prefix
            string il2cppName = "Il2Cpp" + className;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == ModAssembly) continue;

                try
                {
                    var type = asm.GetType(il2cppName);
                    if (type != null) return type;
                }
                catch { }
            }

            // Last resort: name-only search (slower but handles partial names)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == ModAssembly) continue;

                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == className || type.Name == il2cppName)
                            return type;
                    }
                }
                catch { }
            }

            return null;
        }

        #endregion

        #region Extract / Restore Placeholders

        /// <summary>
        /// Replace known variable values with [!STR*N] placeholders.
        /// Must be called BEFORE ExtractNumbersToPlaceholders (variables may contain numbers).
        /// </summary>
        public static string ExtractVariables(string text, out List<KeyValuePair<int, string>> extracted)
        {
            extracted = null;
            // Snapshot references: this method also runs on the translation worker
            // while the main thread swaps them (copy-on-write). Resolution NEVER
            // happens here — it may call Unity APIs, main-thread only (OnUpdate tick
            // + RefreshOnMiss).
            var defs = _definitions;
            var values = _currentValues;
            if (string.IsNullOrEmpty(text) || defs.Count == 0) return text;

            // Collect active variables (non-null, non-empty values)
            // Key = stable Id (not list position), Value = current string value
            // Sort by value length descending to avoid partial matches
            var active = new List<KeyValuePair<int, string>>();
            foreach (var def in defs)
            {
                if (values.TryGetValue(def.Id, out string val) && !string.IsNullOrEmpty(val))
                    active.Add(new KeyValuePair<int, string>(def.Id, val));
            }

            if (active.Count == 0) return text;

            // Sort longest first to avoid sub-matches
            active.Sort((a, b) => b.Value.Length.CompareTo(a.Value.Length));

            string result = text;
            extracted = new List<KeyValuePair<int, string>>();

            foreach (var kvp in active)
            {
                int idx = kvp.Key;
                string value = kvp.Value;
                string placeholder = $"{Prefix}{idx}{Suffix}";

                if (result.Contains(value))
                {
                    result = result.Replace(value, placeholder);
                    extracted.Add(kvp);
                }
            }

            if (extracted.Count == 0)
                extracted = null;

            return result;
        }

        /// <summary>
        /// Restore [!STR*N] placeholders back to their current variable values.
        /// Must be called AFTER RestoreNumbersFromPlaceholders.
        /// </summary>
        public static string RestoreVariables(string text, List<KeyValuePair<int, string>> extracted)
        {
            if (string.IsNullOrEmpty(text) || extracted == null || extracted.Count == 0)
                return text;

            string result = text;
            foreach (var kvp in extracted)
            {
                string placeholder = $"{Prefix}{kvp.Key}{Suffix}";
                // Use current value (may differ from extraction time if variable changed)
                string currentVal = GetValue(kvp.Key) ?? kvp.Value;
                result = result.Replace(placeholder, currentVal);
            }
            return result;
        }

        #endregion

        #region Capture Mode (Scan)

        // Reverse-containment matches ("field value is a piece of the searched text")
        // below this length are noise — they match everywhere.
        private const int MinReverseMatchLength = 4;

        // How deep the scan follows object-typed members from a root (root.a.b.field).
        private const int MaxScanDepth = 2;

        /// <summary>
        /// Rank a candidate string value against the searched text.
        /// 3 = exact, 2 = value contains the search, 1 = search contains the value
        /// (displayed strings composed from several fields, e.g. "seedA-seedB"), 0 = no match.
        /// </summary>
        private static int GetMatchRank(string value, string searchValue)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            if (value == searchValue) return 3;
            if (value.Contains(searchValue)) return 2;
            if (value.Length >= MinReverseMatchLength && !string.IsNullOrWhiteSpace(value)
                && searchValue.Contains(value)) return 1;
            return 0;
        }

        private static void AddCandidate(List<VariableCandidate> results, HashSet<string> seen,
            string className, string fieldPath, string value, bool isStatic, int rank)
        {
            if (!seen.Add(className + "|" + fieldPath)) return;
            results.Add(new VariableCandidate
            {
                ClassName = className,
                FieldPath = fieldPath,
                CurrentValue = value,
                IsStatic = isStatic,
                MatchRank = rank
            });
        }

        /// <summary>
        /// Scan game memory for fields/properties matching the specified string value.
        /// This is a heavy operation — only called on user request (not every frame).
        /// Covers static members, singletons (including plain C# Singleton&lt;T&gt;
        /// classes, recursing into their object members) and live Unity instances.
        /// Returns candidates sorted by match strength then relevance.
        /// </summary>
        public static List<VariableCandidate> ScanForValue(string searchValue)
        {
            if (string.IsNullOrEmpty(searchValue)) return new List<VariableCandidate>();

            var results = new List<VariableCandidate>();
            var seen = new HashSet<string>(); // "ClassName|FieldPath" dedup across all scan passes
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            TranslatorCore.LogInfo($"[VariableManager] Scanning for value: \"{searchValue}\"...");

            // Skip system/engine assemblies that can crash on IL2CPP when accessing types
            var skipPrefixes = new[] { "mscorlib", "System", "Mono.", "UnityEngine.",
                "Unity.", "Il2CppInterop", "Il2CppMono", "Il2CppSystem",
                "MelonLoader", "Harmony", "0Harmony", "BepInEx",
                "Newtonsoft", "UniverseLib", "UnityGameTranslator" };

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string asmName = asm.GetName().Name;
                    bool skip = false;
                    foreach (var prefix in skipPrefixes)
                    {
                        if (asmName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            skip = true;
                            break;
                        }
                    }
                    if (skip) continue;

                    TranslatorCore.LogDebug($"[VariableManager] Scanning assembly: {asmName}");
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (var type in types)
                    {
                        try
                        {
                            ScanTypeStaticFields(type, searchValue, results, seen);
                            // Static classes (abstract+sealed) can't have singleton instances
                            if (!(type.IsAbstract && type.IsSealed))
                                ScanTypeSingleton(type, searchValue, results, seen);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            long staticsPassMs = stopwatch.ElapsedMilliseconds;
            TranslatorCore.LogInfo($"[VariableManager] Statics/singletons pass: {results.Count} candidate(s) in {staticsPassMs} ms");

            // Scan game instances using UniverseLib's GetActualType()
            // which resolves IL2CPP proxy types correctly on both Mono and IL2CPP
            ScanInstancesUniverseLib(searchValue, skipPrefixes, results, seen);

            foreach (var r in results)
                TranslatorCore.LogInfo($"[VariableManager] Candidate: {r.ClassName}.{r.FieldPath} = \"{r.CurrentValue}\" static={r.IsStatic} rank={r.MatchRank}");
            TranslatorCore.LogInfo($"[VariableManager] Scan complete: {results.Count} candidates found ({staticsPassMs} ms statics + {stopwatch.ElapsedMilliseconds - staticsPassMs} ms instances)");

            // Filter out noise: clipboard fields, m_Text (UI internals), our own mod
            results.RemoveAll(r =>
                r.FieldPath.Contains("clipboard") || r.FieldPath.Contains("Clipboard")
                || r.ClassName.Contains("NGUI")
                || (r.FieldPath == "m_Text" && r.ClassName.Contains("InputField"))
                || (r.FieldPath == "text" && r.ClassName.Contains("InputField"))
                || r.ClassName.StartsWith("UGT_")
                || r.ClassName.Contains("UnityGameTranslator")
                || r.ClassName.Contains("UniverseLib")
            );

            // Sort: strongest match first, then singleton fields, then static, then name
            results.Sort((a, b) =>
            {
                if (a.MatchRank != b.MatchRank) return b.MatchRank.CompareTo(a.MatchRank);
                bool aIsSingleton = a.FieldPath.StartsWith("Instance.") || a.FieldPath.StartsWith("instance.");
                bool bIsSingleton = b.FieldPath.StartsWith("Instance.") || b.FieldPath.StartsWith("instance.");
                if (aIsSingleton != bIsSingleton) return aIsSingleton ? -1 : 1;
                if (a.IsStatic != b.IsStatic) return a.IsStatic ? -1 : 1;
                return string.Compare(a.ClassName, b.ClassName, StringComparison.Ordinal);
            });

            return results;
        }

        private static void ScanTypeStaticFields(Type type, string searchValue, List<VariableCandidate> results, HashSet<string> seen)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                foreach (var field in type.GetFields(flags))
                {
                    try
                    {
                        if (field.FieldType != typeof(string) && !field.FieldType.Name.Contains("String"))
                            continue;
                        var val = field.GetValue(null);
                        string strVal = val as string ?? val?.ToString();
                        int rank = GetMatchRank(strVal, searchValue);
                        if (rank > 0)
                            AddCandidate(results, seen, type.Name, field.Name, strVal, true, rank);
                    }
                    catch { }
                }

                foreach (var prop in type.GetProperties(flags))
                {
                    try
                    {
                        if (prop.PropertyType != typeof(string) && !prop.PropertyType.Name.Contains("String"))
                            continue;
                        if (!prop.CanRead) continue;
                        // Same purity rule as the instance scan: on IL2CPP proxies,
                        // only read field-backed static wrappers, never real getters
                        var declaring = prop.DeclaringType ?? type;
                        if (IsIl2CppProxyType(declaring) && declaring.GetField("NativeFieldInfoPtr_" + prop.Name,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly) == null)
                            continue;
                        var val = prop.GetValue(null, null);
                        string strVal = val as string ?? val?.ToString();
                        int rank = GetMatchRank(strVal, searchValue);
                        if (rank > 0)
                            AddCandidate(results, seen, type.Name, prop.Name, strVal, true, rank);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void ScanTypeSingleton(Type type, string searchValue, List<VariableCandidate> results, HashSet<string> seen)
        {
            if (type.ContainsGenericParameters) return; // open generics have no readable statics

            object instance = GetSingletonInstanceForScan(type, out string memberName);
            if (instance == null) return;

            var visited = new HashSet<object>(ReferenceComparer.Comparer);
            ScanObjectRecursive(instance, type.Name, memberName, searchValue, results, seen, 0, visited);
        }

        // Singleton member names, checked as FIELDS first (pure memory reads,
        // no side effects) including common backing-field spellings.
        private static readonly string[] SingletonMemberNames =
            { "Instance", "instance", "_instance", "s_instance", "m_instance",
              "I", "Singleton", "singleton", "Current", "current" };

        /// <summary>
        /// Find a live singleton instance WITHOUT triggering side effects, walking
        /// base classes (Singleton&lt;T&gt; generic base pattern). Static property
        /// getters are only invoked when provably safe: on IL2CPP proxies, only
        /// field-backed wrappers (NativeFieldInfoPtr_ marker) — a real native getter
        /// may lazily CONSTRUCT the singleton, and the scan mass-invoking inherited
        /// Instance getters would instantiate every manager in the game. On Mono,
        /// getters are only invoked when the property returns the singleton's own
        /// type (classic pattern), limiting exposure to intended accessors.
        /// The permissive read stays in GetStaticMemberValue for runtime resolution
        /// of user-chosen variables, where hitting the intended getter is the point.
        /// </summary>
        private static object GetSingletonInstanceForScan(Type type, out string memberName)
        {
            memberName = null;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                bool isProxy = IsIl2CppProxyType(t);

                foreach (var name in SingletonMemberNames)
                {
                    try
                    {
                        var field = t.GetField(name, flags);
                        if (field != null)
                        {
                            var value = field.GetValue(null);
                            if (value != null)
                            {
                                memberName = name;
                                return value;
                            }
                            continue;
                        }

                        var prop = FindProperty(t, name, flags);
                        if (prop != null && prop.CanRead && IsSafeStaticPropertyRead(t, prop, type, isProxy))
                        {
                            var value = prop.GetValue(null, null);
                            if (value != null)
                            {
                                memberName = name;
                                return value;
                            }
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        /// <summary>See GetSingletonInstanceForScan — scan-time getter safety rules.</summary>
        private static bool IsSafeStaticPropertyRead(Type declaringType, PropertyInfo prop, Type scannedType, bool isProxy)
        {
            if (isProxy)
            {
                // Il2CppInterop emits NativeFieldInfoPtr_<name> for il2cpp FIELDS
                // exposed as proxy properties (side-effect-free reads) and
                // NativeMethodInfoPtr_get_<name> for real property getters.
                var marker = declaringType.GetField("NativeFieldInfoPtr_" + prop.Name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                return marker != null;
            }

            // Mono: classic singleton signature — the property returns the singleton's own type
            return prop.PropertyType.IsAssignableFrom(scannedType) || scannedType.IsAssignableFrom(prop.PropertyType);
        }

        // Cache: proxy detection enumerates static fields, and the scan asks for the
        // same types over and over (hierarchy levels, thousands of statics-pass types)
        private static readonly Dictionary<Type, bool> _proxyTypeCache = new Dictionary<Type, bool>();

        /// <summary>An Il2CppInterop-generated proxy type carries Native*InfoPtr marker fields.</summary>
        private static bool IsIl2CppProxyType(Type type)
        {
            if (_proxyTypeCache.TryGetValue(type, out bool cached)) return cached;

            bool isProxy = false;
            try
            {
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (f.Name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal)
                        || f.Name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal))
                    {
                        isProxy = true;
                        break;
                    }
                }
            }
            catch { }

            _proxyTypeCache[type] = isProxy;
            return isProxy;
        }

        /// <summary>
        /// Scan an object's fields AND properties for matching strings, following
        /// object-typed members up to MaxScanDepth levels (reaches nested plain C#
        /// state like Manager.Instance.subObject.seed). Fields matter on Mono where
        /// game data is not exposed as properties; IL2CPP proxies expose the same
        /// members as properties, deduplicated by the seen set. The type hierarchy is
        /// walked manually (DeclaredOnly per level) so inherited game members are
        /// covered without pulling in engine/system base-class noise.
        /// </summary>
        private static void ScanObjectRecursive(object instance, string rootClassName, string parentPath,
            string searchValue, List<VariableCandidate> results, HashSet<string> seen, int depth, HashSet<object> visited)
        {
            if (instance == null || depth > MaxScanDepth) return;
            if (!visited.Add(instance)) return;

            Type actualType;
            try { actualType = instance.GetActualType(); }
            catch { actualType = instance.GetType(); }

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            for (Type t = actualType; t != null && !IsEngineOrSystemType(t); t = t.BaseType)
            {
                bool proxyLevel = IsIl2CppProxyType(t);

                FieldInfo[] fields;
                try { fields = t.GetFields(flags); } catch { fields = null; }
                if (fields != null)
                {
                    foreach (var field in fields)
                    {
                        try
                        {
                            if (!ShouldFetchMember(field.FieldType, depth)) continue;
                            var val = field.GetValue(instance);
                            HandleScannedMember(val, field.FieldType, rootClassName, parentPath, field.Name,
                                searchValue, results, seen, depth, visited);
                        }
                        catch { }
                    }
                }

                PropertyInfo[] props;
                try { props = t.GetProperties(flags); } catch { props = null; }
                if (props != null)
                {
                    foreach (var prop in props)
                    {
                        try
                        {
                            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                            if (!ShouldFetchMember(prop.PropertyType, depth)) continue;
                            // On IL2CPP proxies, only read field-backed wrappers
                            // (NativeFieldInfoPtr marker = pure memory read). A real
                            // native getter runs arbitrary game code — side effects
                            // and hang risk when mass-invoked by a scan. Game DATA
                            // lives in il2cpp fields; computed strings are handled
                            // as multi-variable compositions instead.
                            if (proxyLevel && t.GetField("NativeFieldInfoPtr_" + prop.Name,
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly) == null)
                                continue;
                            var val = prop.GetValue(instance, null);
                            HandleScannedMember(val, prop.PropertyType, rootClassName, parentPath, prop.Name,
                                searchValue, results, seen, depth, visited);
                        }
                        catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Decide from the DECLARED member type whether reading the value is worth it.
        /// Critical on IL2CPP where every property read invokes a native getter:
        /// fetching blindly runs arbitrary game code (lazy initializers, expensive
        /// computed properties) for members the scan could never use. Runtime checks
        /// (CanRecurseInto) still run after the fetch — the declared type may be an
        /// interface or base whose runtime value is not followable.
        /// </summary>
        private static bool ShouldFetchMember(Type declaredType, int depth)
        {
            bool isStringLike = declaredType == typeof(string) || declaredType.Name.Contains("String")
                || declaredType.FullName == "System.String";
            if (isStringLike) return true;

            // Non-string member: only worth fetching to recurse into it
            if (depth >= MaxScanDepth) return false;
            if (declaredType.IsPrimitive || declaredType.IsEnum || declaredType.IsValueType) return false;
            if (declaredType == typeof(object)) return false;
            if (typeof(Delegate).IsAssignableFrom(declaredType)) return false;
            if (typeof(MemberInfo).IsAssignableFrom(declaredType)) return false;
            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(declaredType)) return false;
            if (typeof(UnityEngine.Object).IsAssignableFrom(declaredType)) return false;
            if (IsEngineOrSystemType(declaredType)) return false;
            return true;
        }

        /// <summary>Rank a scanned member value as candidate, or recurse into it.</summary>
        private static void HandleScannedMember(object value, Type declaredType, string rootClassName,
            string parentPath, string memberName, string searchValue,
            List<VariableCandidate> results, HashSet<string> seen, int depth, HashSet<object> visited)
        {
            if (value == null) return;

            string fullPath = string.IsNullOrEmpty(parentPath) ? memberName : parentPath + "." + memberName;

            bool isStringLike = declaredType == typeof(string) || declaredType.Name.Contains("String")
                || declaredType.FullName == "System.String";
            if (isStringLike)
            {
                string strVal = value as string ?? value.ToString();
                int rank = GetMatchRank(strVal, searchValue);
                if (rank > 0)
                    AddCandidate(results, seen, rootClassName, fullPath, strVal, false, rank);
                return;
            }

            if (depth >= MaxScanDepth) return;
            if (!CanRecurseInto(value)) return;
            ScanObjectRecursive(value, rootClassName, fullPath, searchValue, results, seen, depth + 1, visited);
        }

        /// <summary>
        /// Only follow plain data objects: no primitives/enums/structs, no strings,
        /// no delegates/reflection types, no collections, no Unity objects (already
        /// scanned as roots by the instance scan) and no engine/system classes.
        /// </summary>
        private static bool CanRecurseInto(object value)
        {
            Type type;
            try { type = value.GetActualType(); }
            catch { type = value.GetType(); }

            if (type.IsPrimitive || type.IsEnum || type.IsValueType) return false;
            if (type == typeof(string)) return false;
            if (typeof(Delegate).IsAssignableFrom(type)) return false;
            if (typeof(MemberInfo).IsAssignableFrom(type)) return false; // covers Type too
            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) return false;
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return false;
            if (IsEngineOrSystemType(type)) return false;
            return true;
        }

        /// <summary>Engine/framework types: never scanned as declaring levels nor recursed into.</summary>
        private static bool IsEngineOrSystemType(Type type)
        {
            if (type == typeof(object)) return true;
            string ns = type.Namespace ?? "";
            return ns.StartsWith("System") || ns.StartsWith("UnityEngine") || ns.StartsWith("Unity.")
                || ns.StartsWith("Il2CppSystem") || ns.StartsWith("Il2CppInterop")
                || ns.StartsWith("MelonLoader") || ns.StartsWith("BepInEx") || ns.StartsWith("HarmonyLib")
                || ns.StartsWith("Newtonsoft") || ns.StartsWith("UniverseLib")
                || ns.StartsWith("UnityGameTranslator") || ns.StartsWith("TMPro") || ns.StartsWith("TMProOld");
        }

        /// <summary>Reference-equality comparer for the visited set (cycle guard).</summary>
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Comparer = new ReferenceComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        #endregion

        #region Instance Scan (UniverseLib)

        /// <summary>
        /// Scan instances using UniverseLib's GetActualType() which correctly resolves
        /// IL2CPP proxy types. This gives us the real .NET proxy type with all properties,
        /// unlike raw reflection which only sees base class members.
        /// Works on both Mono and IL2CPP.
        /// </summary>
        // Soft cap on the instance pass. It cannot interrupt a single blocking native
        // call, but bounds the total when a game has thousands of live types.
        private const int InstanceScanBudgetMs = 15000;

        /// <summary>
        /// Scan live game instances. Enumerates ALL MonoBehaviours and ScriptableObjects
        /// in two bulk native calls, then groups by actual type and scans the first
        /// instance of each — the previous shape (one FindAllObjectsOfType native call
        /// PER candidate type) took minutes on large IL2CPP games, perceived as a
        /// freeze. Bulk enumeration also only ever visits types that actually have
        /// live instances.
        /// </summary>
        private static void ScanInstancesUniverseLib(string searchValue, string[] skipPrefixes,
            List<VariableCandidate> results, HashSet<string> seen)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var scannedTypes = new HashSet<Type>();
                int typesScanned = 0;
                bool truncated = false;

                // Scene objects (incl. inactive) + assets: SOs need the asset-scan
                // path on Mono (FindObjectsOfType never returns assets)
                var pools = new UnityEngine.Object[][]
                {
                    TypeHelper.FindAllObjectsOfType(typeof(MonoBehaviour)),
                    TypeHelper.FindAllAssetsOfType(typeof(ScriptableObject))
                };
                TranslatorCore.LogInfo($"[VariableManager] Instance pass: {pools[0]?.Length ?? 0} behaviours + {pools[1]?.Length ?? 0} scriptable objects enumerated in {sw.ElapsedMilliseconds} ms");

                foreach (var pool in pools)
                {
                    if (pool == null) continue;
                    if (truncated) break;

                    foreach (var obj in pool)
                    {
                        try
                        {
                            if (obj == null) continue;

                            Type actualType;
                            try { actualType = obj.GetActualType(); }
                            catch { continue; }

                            if (!scannedTypes.Add(actualType)) continue;

                            // Keep only game types (same assembly filter as the statics pass)
                            string asmName = actualType.Assembly.GetName().Name;
                            if (!asmName.Contains("Assembly-CSharp") && !asmName.StartsWith("Il2Cpp")) continue;
                            bool skipAsm = false;
                            foreach (var prefix in skipPrefixes)
                            {
                                if (asmName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                { skipAsm = true; break; }
                            }
                            if (skipAsm) continue;

                            if (sw.ElapsedMilliseconds > InstanceScanBudgetMs)
                            {
                                truncated = true;
                                TranslatorCore.LogWarning($"[VariableManager] Instance pass truncated after {typesScanned} types ({InstanceScanBudgetMs} ms budget) — some candidates may be missing");
                                break;
                            }

                            // Cast to actual type
                            object typed;
                            try { typed = TypeHelper.Il2CppCast(obj, actualType) ?? obj; }
                            catch { typed = obj; }

                            typesScanned++;
                            if (typesScanned % 100 == 0)
                                TranslatorCore.LogInfo($"[VariableManager] Instance pass: {typesScanned} types scanned, {sw.ElapsedMilliseconds} ms, last={actualType.Name}");

                            // Scan fields and properties, following sub-objects
                            var visited = new HashSet<object>(ReferenceComparer.Comparer);
                            ScanObjectRecursive(typed, actualType.Name, "", searchValue, results, seen, 0, visited);
                        }
                        catch { }
                    }
                }

                TranslatorCore.LogInfo($"[VariableManager] Instance pass: {typesScanned} types checked in {sw.ElapsedMilliseconds} ms{(truncated ? " (TRUNCATED)" : "")}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[VariableManager] UniverseLib scan error: {ex.Message}");
            }
        }

        #endregion
    }
}
