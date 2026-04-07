using System;
using System.Collections.Generic;
using System.Reflection;
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
        private static bool _needsRefresh = true; // Flag: resolve values on next ExtractVariables call

        // Cached resolved types/fields for performance
        private static Dictionary<int, ResolvedPath> _resolvedPaths = new Dictionary<int, ResolvedPath>();

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
        }

        private class ResolvedPath
        {
            public Type RootType;
            public List<MemberInfo> PathMembers; // chain of fields/properties to traverse
            public bool IsStatic;                // first member is static
            public bool ResolveFailed;
        }

        #endregion

        #region Persistence (JSON)

        public static void LoadFromJson(JToken token)
        {
            _definitions.Clear();
            _currentValues.Clear();
            _resolvedPaths.Clear();

            if (token == null || token.Type != JTokenType.Array) return;

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
                    _definitions.Add(def);
                    if (id >= _nextId) _nextId = id + 1;
                }
            }

            if (_definitions.Count > 0)
                TranslatorCore.LogInfo($"[VariableManager] Loaded {_definitions.Count} variable definitions (nextId={_nextId})");
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
            _definitions.Add(new VariableDefinition
            {
                Id = id,
                Name = name,
                ClassName = className,
                FieldPath = fieldPath
            });

            _resolvedPaths.Clear();
            TranslatorCore.SetMetadataDirty();
            TranslatorCore.LogInfo($"[VariableManager] Added variable [!STR*{id}]: {name} ({className}.{fieldPath})");
        }

        /// <summary>Remove a variable by its stable Id (not list position).</summary>
        public static void RemoveVariable(int id)
        {
            var def = _definitions.Find(d => d.Id == id);
            if (def == null) return;

            _definitions.Remove(def);
            _currentValues.Remove(id);
            _resolvedPaths.Clear();
            TranslatorCore.SetMetadataDirty();
            TranslatorCore.LogInfo($"[VariableManager] Removed variable [!STR*{id}]: {def.Name}");
        }

        #endregion

        #region Value Resolution (Reflection)

        /// <summary>
        /// Refresh all variable values from game memory via reflection.
        /// Called periodically (every ~2 seconds).
        /// </summary>
        /// <summary>
        /// Mark variables for refresh on next ExtractVariables call.
        /// Called on scene change.
        /// </summary>
        public static void MarkNeedsRefresh()
        {
            _needsRefresh = true;
        }

        /// <summary>
        /// Resolve all variable values. Called on scene change.
        /// Keeps old values if re-resolution fails (instance temporarily destroyed).
        /// Never called from the hot path (ExtractVariables just reads the cache).
        /// </summary>
        public static void RefreshValues()
        {
            if (_definitions.Count == 0) return;

            foreach (var def in _definitions)
            {
                string value = ResolveValue(def);
                if (value != null)
                    _currentValues[def.Id] = value;
                // If null, keep the old cached value (don't erase)
            }
        }

        /// <summary>
        /// Get the current value of a variable by index.
        /// </summary>
        public static string GetValue(int index)
        {
            if (_currentValues.TryGetValue(index, out string val))
                return val;
            return null;
        }

        /// <summary>
        /// Resolve a variable value by finding the instance via FindAllObjectsOfType
        /// then traversing properties using GetActualType() for IL2CPP compatibility.
        /// </summary>
        private static string ResolveValue(VariableDefinition def)
        {
            if (def == null) return null;

            try
            {
                // Find the root type
                Type rootType = FindType(def.ClassName);
                if (rootType == null) return null;

                // Find an instance
                var instances = TypeHelper.FindAllObjectsOfType(rootType);
                if (instances == null || instances.Length == 0) return null;

                var obj = instances[0];
                if (obj == null) return null;

                // Get actual type and cast
                Type actualType;
                try { actualType = obj.GetActualType(); }
                catch { actualType = rootType; }

                object current;
                try { current = TypeHelper.Il2CppCast(obj, actualType) ?? obj; }
                catch { current = obj; }

                // Traverse the property path
                string[] parts = def.FieldPath.Split('.');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (current == null) return null;

                    Type currentType;
                    try { currentType = current.GetActualType(); }
                    catch { currentType = current.GetType(); }

                    var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    var prop = currentType.GetProperty(parts[i], flags);
                    if (prop == null)
                    {
                        // Try without DeclaredOnly
                        prop = currentType.GetProperty(parts[i], BindingFlags.Public | BindingFlags.Instance);
                    }
                    if (prop == null) return null;

                    current = prop.GetValue(current, null);
                }

                if (current == null) return null;
                if (current is string str) return str;
                return current.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static ResolvedPath BuildResolvedPath(VariableDefinition def)
        {
            var result = new ResolvedPath
            {
                PathMembers = new List<MemberInfo>()
            };

            // Find the root type
            result.RootType = FindType(def.ClassName);
            if (result.RootType == null)
            {
                TranslatorCore.LogDebug($"[VariableManager] Type not found: {def.ClassName}");
                result.ResolveFailed = true;
                return result;
            }

            // Parse field path: "Instance.playerName" → ["Instance", "playerName"]
            string[] parts = def.FieldPath.Split('.');
            Type currentType = result.RootType;

            for (int i = 0; i < parts.Length; i++)
            {
                string memberName = parts[i];
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                // Try field first, then property
                var field = currentType.GetField(memberName, flags);
                if (field != null)
                {
                    result.PathMembers.Add(field);
                    if (i == 0) result.IsStatic = field.IsStatic;
                    currentType = field.FieldType;
                    continue;
                }

                var prop = currentType.GetProperty(memberName, flags);
                if (prop != null)
                {
                    result.PathMembers.Add(prop);
                    if (i == 0)
                    {
                        var getter = prop.GetGetMethod(true);
                        result.IsStatic = getter != null && getter.IsStatic;
                    }
                    currentType = prop.PropertyType;
                    continue;
                }

                // IL2CPP: try with different naming conventions
                field = currentType.GetField("_" + memberName, flags);
                if (field != null)
                {
                    result.PathMembers.Add(field);
                    if (i == 0) result.IsStatic = field.IsStatic;
                    currentType = field.FieldType;
                    continue;
                }

                TranslatorCore.LogDebug($"[VariableManager] Member not found: {def.ClassName}.{memberName} (in path {def.FieldPath})");
                result.ResolveFailed = true;
                return result;
            }

            return result;
        }

        private static string TraversePath(ResolvedPath resolved)
        {
            object current = null;

            for (int i = 0; i < resolved.PathMembers.Count; i++)
            {
                var member = resolved.PathMembers[i];
                bool isFirst = (i == 0);

                object target = isFirst && resolved.IsStatic ? null : current;

                if (member is FieldInfo fi)
                {
                    current = fi.GetValue(target);
                }
                else if (member is PropertyInfo pi)
                {
                    current = pi.GetValue(target, null);
                }

                if (current == null) return null;
            }

            // Final value should be a string
            if (current is string str)
                return str;

            // IL2CPP: might be an Il2CppString
            return current.ToString();
        }

        private static bool IsOwnUI(GameObject go)
        {
            if (go == null) return false;
            var current = go.transform;
            while (current != null)
            {
                string name = current.name;
                if (name.StartsWith("UGT_") || name.StartsWith("UniverseLibCanvas")
                    || name.StartsWith("UniverseLib_") || name == "UGT_InspectorHighlight")
                    return true;
                current = current.parent;
            }
            return false;
        }

        private static Type FindType(string className)
        {
            // Try direct lookup first (fast, no GetTypes iteration)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
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
            if (string.IsNullOrEmpty(text) || _definitions.Count == 0) return text;

            // Resolve values once after scene change (on first text request)
            if (_needsRefresh)
            {
                _needsRefresh = false;
                RefreshValues();
            }

            // Collect active variables (non-null, non-empty values)
            // Key = stable Id (not list position), Value = current string value
            // Sort by value length descending to avoid partial matches
            var active = new List<KeyValuePair<int, string>>();
            foreach (var def in _definitions)
            {
                if (_currentValues.TryGetValue(def.Id, out string val) && !string.IsNullOrEmpty(val))
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

        /// <summary>
        /// Scan game memory for fields/properties containing the specified string value.
        /// This is a heavy operation — only called on user request (not every frame).
        /// Returns candidates sorted by relevance (singleton fields first, then static, then instance).
        /// </summary>
        public static List<VariableCandidate> ScanForValue(string searchValue)
        {
            if (string.IsNullOrEmpty(searchValue)) return new List<VariableCandidate>();

            var results = new List<VariableCandidate>();
            TranslatorCore.LogInfo($"[VariableManager] Scanning for value: \"{searchValue}\"...");

            // Scan static fields across all types
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
                            if (type.IsAbstract && type.IsSealed)
                                ScanTypeStaticFields(type, searchValue, results);
                            else
                                ScanTypeSingletonAndStatic(type, searchValue, results);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Scan game instances using UniverseLib's GetActualType()
            // which resolves IL2CPP proxy types correctly on both Mono and IL2CPP
            ScanInstancesUniverseLib(searchValue, skipPrefixes, results);

            foreach (var r in results)
                TranslatorCore.LogInfo($"[VariableManager] Candidate: {r.ClassName}.{r.FieldPath} = \"{r.CurrentValue}\" static={r.IsStatic}");
            TranslatorCore.LogInfo($"[VariableManager] Scan complete: {results.Count} candidates found");

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

            // Sort: singleton fields first, then static, then instance
            results.Sort((a, b) =>
            {
                bool aIsSingleton = a.FieldPath.StartsWith("Instance.") || a.FieldPath.StartsWith("instance.");
                bool bIsSingleton = b.FieldPath.StartsWith("Instance.") || b.FieldPath.StartsWith("instance.");
                if (aIsSingleton != bIsSingleton) return aIsSingleton ? -1 : 1;
                if (a.IsStatic != b.IsStatic) return a.IsStatic ? -1 : 1;
                return string.Compare(a.ClassName, b.ClassName, StringComparison.Ordinal);
            });

            return results;
        }

        private static void ScanTypeStaticFields(Type type, string searchValue, List<VariableCandidate> results)
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
                        if (strVal == searchValue)
                        {
                            results.Add(new VariableCandidate
                            {
                                ClassName = type.Name,
                                FieldPath = field.Name,
                                CurrentValue = strVal,
                                IsStatic = true
                            });
                        }
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
                        var val = prop.GetValue(null, null);
                        string strVal = val as string ?? val?.ToString();
                        if (strVal == searchValue)
                        {
                            results.Add(new VariableCandidate
                            {
                                ClassName = type.Name,
                                FieldPath = prop.Name,
                                CurrentValue = strVal,
                                IsStatic = true
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void ScanTypeSingletonAndStatic(Type type, string searchValue, List<VariableCandidate> results)
        {
            // Check for singleton patterns: Instance, instance, I, Singleton
            string[] singletonNames = { "Instance", "instance", "I", "Singleton", "singleton", "Current", "current" };
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            foreach (var singletonName in singletonNames)
            {
                object instance = null;

                try
                {
                    var field = type.GetField(singletonName, flags);
                    if (field != null)
                    {
                        instance = field.GetValue(null);
                    }
                    else
                    {
                        var prop = type.GetProperty(singletonName, flags);
                        if (prop != null && prop.CanRead)
                            instance = prop.GetValue(null, null);
                    }
                }
                catch { continue; }

                if (instance == null) continue;

                // Scan instance fields of the singleton
                var instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                try
                {
                    foreach (var field in instance.GetType().GetFields(instanceFlags))
                    {
                        try
                        {
                            if (field.FieldType != typeof(string) && !field.FieldType.Name.Contains("String"))
                                continue;
                            var val = field.GetValue(instance);
                            string strVal = val as string ?? val?.ToString();
                            if (strVal == searchValue)
                            {
                                results.Add(new VariableCandidate
                                {
                                    ClassName = type.Name,
                                    FieldPath = $"{singletonName}.{field.Name}",
                                    CurrentValue = strVal,
                                    IsStatic = false
                                });
                            }
                        }
                        catch { }
                    }

                    foreach (var prop in instance.GetType().GetProperties(instanceFlags))
                    {
                        try
                        {
                            if (prop.PropertyType != typeof(string) && !prop.PropertyType.Name.Contains("String"))
                                continue;
                            if (!prop.CanRead) continue;
                            var val = prop.GetValue(instance, null);
                            string strVal = val as string ?? val?.ToString();
                            if (strVal == searchValue)
                            {
                                results.Add(new VariableCandidate
                                {
                                    ClassName = type.Name,
                                    FieldPath = $"{singletonName}.{prop.Name}",
                                    CurrentValue = strVal,
                                    IsStatic = false
                                });
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                break; // Found a singleton, don't check other names
            }

            // Also scan static string fields of this type
            ScanTypeStaticFields(type, searchValue, results);
        }

        /// <summary>
        /// Scan properties of an instance recursively (safe — uses .NET reflection on proxy types).
        /// Follows sub-objects up to maxDepth levels to find nested string values.
        /// </summary>
        private static void ScanInstanceFieldsRecursive(object instance, Type type, string className,
            string parentPath, string searchValue, List<VariableCandidate> results, int depth)
        {
            if (instance == null || type == null || depth > 3) return;

            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            try
            {
                foreach (var prop in type.GetProperties(flags))
                {
                    try
                    {
                        if (!prop.CanRead) continue;
                        if (prop.GetIndexParameters().Length > 0) continue;

                        string fullPath = string.IsNullOrEmpty(parentPath) ? prop.Name : $"{parentPath}.{prop.Name}";
                        var pt = prop.PropertyType;

                        // String property
                        bool isString = pt == typeof(string) || pt.Name.Contains("String")
                            || pt.FullName == "System.String";

                        if (isString)
                        {
                            var val = prop.GetValue(instance, null);
                            if (val == null) continue;
                            string strVal = val as string;
                            if (strVal == null) strVal = val.ToString();
                            if (strVal != null && strVal.Contains(searchValue))
                            {
                                results.Add(new VariableCandidate
                                {
                                    ClassName = className,
                                    FieldPath = fullPath,
                                    CurrentValue = strVal,
                                    IsStatic = false
                                });
                            }
                        }
                        // Non-primitive object property — recurse
                        else if (!pt.IsPrimitive && !pt.IsEnum && !pt.IsValueType
                            && pt != typeof(object) && !pt.Name.StartsWith("Il2CppArrayBase")
                            && !pt.Name.Contains("List") && !pt.Name.Contains("Dictionary")
                            && !pt.Name.Contains("Array") && !pt.Namespace?.StartsWith("UnityEngine") == true)
                        {
                            // Skip Unity types and collections — too deep and noisy
                            if (depth >= 2) continue;

                            try
                            {
                                var childVal = prop.GetValue(instance, null);
                                if (childVal == null) continue;

                                Type childType;
                                try { childType = childVal.GetActualType(); }
                                catch { childType = childVal.GetType(); }

                                ScanInstanceFieldsRecursive(childVal, childType, className, fullPath, searchValue, results, depth + 1);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void ScanInstanceFields(object instance, Type type, string searchValue, List<VariableCandidate> results)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                // Scan fields
                foreach (var field in type.GetFields(flags))
                {
                    try
                    {
                        var ft = field.FieldType;
                        bool isStringLike = ft == typeof(string) || ft.Name.Contains("String")
                            || ft.FullName == "System.String";
                        if (!isStringLike) continue;

                        var val = field.GetValue(instance);
                        if (val == null) continue;

                        string strVal = val as string;
                        if (strVal == null) strVal = val.ToString();
                        if (strVal == null) continue;

                        if (strVal.Contains(searchValue))
                        {
                            results.Add(new VariableCandidate
                            {
                                ClassName = type.Name,
                                FieldPath = field.Name,
                                CurrentValue = strVal,
                                IsStatic = false
                            });
                        }
                    }
                    catch { }
                }

                // Scan properties (IL2CPP proxy classes expose fields as properties)
                foreach (var prop in type.GetProperties(flags | BindingFlags.Public))
                {
                    try
                    {
                        var pt = prop.PropertyType;
                        bool isStringLike = pt == typeof(string) || pt.Name.Contains("String")
                            || pt.FullName == "System.String";
                        if (!isStringLike || !prop.CanRead) continue;
                        // Skip indexers
                        if (prop.GetIndexParameters().Length > 0) continue;

                        var val = prop.GetValue(instance, null);
                        if (val == null) continue;

                        string strVal = val as string;
                        if (strVal == null) strVal = val.ToString();
                        if (strVal == null) continue;

                        if (strVal.Contains(searchValue))
                        {
                            results.Add(new VariableCandidate
                            {
                                ClassName = type.Name,
                                FieldPath = prop.Name,
                                CurrentValue = strVal,
                                IsStatic = false
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        #endregion

        #region Instance Scan (UniverseLib)

        /// <summary>
        /// Scan instances using UniverseLib's GetActualType() which correctly resolves
        /// IL2CPP proxy types. This gives us the real .NET proxy type with all properties,
        /// unlike raw reflection which only sees base class members.
        /// Works on both Mono and IL2CPP.
        /// </summary>
        private static void ScanInstancesUniverseLib(string searchValue, string[] skipPrefixes, List<VariableCandidate> results)
        {
            try
            {
                // Iterate concrete types from game assemblies and FindAllObjectsOfType per type
                var gameAssemblies = new List<System.Reflection.Assembly>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName = asm.GetName().Name;
                    if (!asmName.Contains("Assembly-CSharp") && !asmName.StartsWith("Il2Cpp")) continue;
                    bool skipAsm = false;
                    foreach (var prefix in skipPrefixes)
                    {
                        if (asmName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        { skipAsm = true; break; }
                    }
                    if (!skipAsm) gameAssemblies.Add(asm);
                }

                int typesScanned = 0;

                foreach (var asm in gameAssemblies)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (var type in types)
                    {
                        try
                        {
                            if (!typeof(UnityEngine.Object).IsAssignableFrom(type)) continue;
                            if (type.IsAbstract || type.IsInterface) continue;

                            var instances = TypeHelper.FindAllObjectsOfType(type);
                            if (instances == null || instances.Length == 0) continue;

                            var firstObj = instances[0];
                            if (firstObj == null) continue;

                            // Get actual proxy type via UniverseLib
                            Type actualType;
                            try { actualType = firstObj.GetActualType(); }
                            catch { actualType = type; }

                            typesScanned++;

                            // Cast to actual type
                            object typed;
                            try { typed = TypeHelper.Il2CppCast(firstObj, actualType) ?? firstObj; }
                            catch { typed = firstObj; }

                            // Debug: dump members of GameDataController
                            if (actualType.Name.Contains("GameData"))
                            {
                                var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                                try
                                {
                                    var members = actualType.GetMembers(allFlags);
                                    TranslatorCore.LogInfo($"[VarDiag] {actualType.FullName}: {members.Length} members (DeclaredOnly)");
                                    foreach (var m in members)
                                    {
                                        if (m.MemberType == MemberTypes.Property || m.MemberType == MemberTypes.Field)
                                            TranslatorCore.LogInfo($"[VarDiag]   {m.MemberType}: {m.Name}");
                                    }
                                }
                                catch { }
                            }

                            // Scan fields and properties (with 1 level of depth for sub-objects)
                            ScanInstanceFieldsRecursive(typed, actualType, type.Name, "", searchValue, results, 0);
                        }
                        catch { }
                    }
                }

                TranslatorCore.LogInfo($"[VariableManager] UniverseLib scan: {typesScanned} types checked");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[VariableManager] UniverseLib scan error: {ex.Message}");
            }
        }

        #endregion
    }
}
