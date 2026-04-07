using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// Helper methods for UI operations that need to work on both Mono and IL2CPP.
    ///
    /// On IL2CPP, UnityAction types are IL2CPP proxy wrappers, not real .NET delegates.
    /// Calling AddListener directly from Core code (compiled against Mono UniverseLib)
    /// fails at runtime with MissingMethodException.
    ///
    /// Strategy:
    /// - For Buttons: use UniverseLib's ButtonRef (AddListener compiled inside UniverseLib)
    /// - For InputFields: use UniverseLib's InputFieldRef.OnValueChanged (same principle)
    /// - For Toggles/EventTriggers: use reflection with op_Implicit or DelegateSupport.ConvertDelegate
    /// </summary>
    public static class UIHelpers
    {
        // Cached DelegateSupport.ConvertDelegate method (found by scanning loaded assemblies)
        private static MethodInfo _convertDelegateMethod;
        private static bool _convertDelegateSearched;

        /// <summary>
        /// Finds Il2CppInterop's DelegateSupport.ConvertDelegate method by scanning loaded assemblies.
        /// Cached after first lookup.
        /// </summary>
        private static MethodInfo FindConvertDelegateMethod()
        {
            if (_convertDelegateSearched)
                return _convertDelegateMethod;

            _convertDelegateSearched = true;

            // Try exact Type.GetType first
            var delegateSupportType = Type.GetType("Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");

            // If not found, scan loaded assemblies (assembly name may differ)
            if (delegateSupportType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        delegateSupportType = asm.GetType("Il2CppInterop.Runtime.DelegateSupport");
                        if (delegateSupportType != null)
                        {
                            TranslatorCore.LogInfo($"[UIHelpers] Found DelegateSupport in assembly: {asm.GetName().Name}");
                            break;
                        }
                    }
                    catch { /* Skip assemblies that throw on GetType */ }
                }
            }

            if (delegateSupportType != null)
            {
                _convertDelegateMethod = delegateSupportType.GetMethod("ConvertDelegate", BindingFlags.Public | BindingFlags.Static);
                if (_convertDelegateMethod != null)
                    TranslatorCore.LogInfo("[UIHelpers] DelegateSupport.ConvertDelegate found and cached");
                else
                    TranslatorCore.Adapter?.LogWarning("[UIHelpers] DelegateSupport found but ConvertDelegate method not found");
            }
            else
            {
                // Not IL2CPP or Il2CppInterop not loaded - this is normal on Mono
                TranslatorCore.LogInfo("[UIHelpers] DelegateSupport not found (expected on Mono builds)");
            }

            return _convertDelegateMethod;
        }

        /// <summary>
        /// Converts a managed delegate to an IL2CPP delegate type using op_Implicit or DelegateSupport.
        /// </summary>
        /// <param name="targetType">The IL2CPP delegate type (e.g., UnityAction, UnityAction&lt;bool&gt;)</param>
        /// <param name="managedDelegate">The managed delegate to convert</param>
        /// <returns>The converted delegate, or null if conversion failed</returns>
        private static object ConvertToIl2CppDelegate(Type targetType, Delegate managedDelegate)
        {
            // Strategy 1: Try op_Implicit on the target type (e.g., UnityAction<bool>.op_Implicit(Action<bool>))
            // This is what UniverseLib's IL2CPP extensions rely on
            try
            {
                var opImplicit = targetType.GetMethod("op_Implicit",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new Type[] { managedDelegate.GetType() }, null);

                if (opImplicit != null)
                {
                    var result = opImplicit.Invoke(null, new object[] { managedDelegate });
                    if (result != null) return result;
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.Adapter?.LogWarning($"[UIHelpers] op_Implicit failed: {ex.Message}");
            }

            // Strategy 2: Try DelegateSupport.ConvertDelegate<T>(delegate)
            var convertMethod = FindConvertDelegateMethod();
            if (convertMethod != null)
            {
                try
                {
                    var genericConvert = convertMethod.MakeGenericMethod(targetType);
                    var result = genericConvert.Invoke(null, new object[] { managedDelegate });
                    if (result != null) return result;
                }
                catch (Exception ex)
                {
                    TranslatorCore.Adapter?.LogWarning($"[UIHelpers] DelegateSupport.ConvertDelegate failed: {ex.Message}");
                }
            }

            // Strategy 3: Try Delegate.CreateDelegate as last resort
            try
            {
                return Delegate.CreateDelegate(targetType, managedDelegate.Target, managedDelegate.Method);
            }
            catch (Exception ex)
            {
                TranslatorCore.Adapter?.LogWarning($"[UIHelpers] Delegate.CreateDelegate fallback failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Gets the AddListener method and parameter type from a UnityEvent object.
        /// </summary>
        private static (MethodInfo method, Type paramType) FindAddListenerMethod(object eventObj)
        {
            var methods = eventObj.GetType().GetMethods();
            foreach (var m in methods)
            {
                if (m.Name == "AddListener" && m.GetParameters().Length == 1)
                {
                    return (m, m.GetParameters()[0].ParameterType);
                }
            }
            return (null, null);
        }

        /// <summary>
        /// Adds a click listener to a Button.
        /// On IL2CPP, uses ButtonRef (compiled inside UniverseLib with correct platform defines).
        /// </summary>
        public static void AddButtonListener(Button button, Action callback)
        {
            if (button == null) return;

            // ButtonRef's constructor calls AddListener inside UniverseLib (compiled with
            // correct platform defines), so it works on both Mono and IL2CPP.
            var btnRef = new ButtonRef(button);
            btnRef.OnClick = callback;
        }

        /// <summary>
        /// Adds a value change listener to a Toggle.
        /// Works on both Mono and IL2CPP by using reflection with delegate conversion.
        /// </summary>
        public static void AddToggleListener(Toggle toggle, Action<bool> callback)
        {
            if (toggle == null) return;

            bool isIL2CPP = TranslatorCore.Adapter?.IsIL2CPP ?? false;

            if (isIL2CPP)
            {
                AddListenerViaReflection(toggle, "onValueChanged", callback, "Toggle");
            }
            else
            {
                // Must be in a separate method so IL2CPP JIT doesn't try to resolve
                // Toggle.onValueChanged field reference when this method body is compiled
                AddToggleListenerDirect(toggle, callback);
            }
        }

        /// <summary>
        /// Adds a value change listener to a Slider.
        /// Works on both Mono and IL2CPP by using reflection with delegate conversion.
        /// </summary>
        public static void AddSliderListener(UnityEngine.UI.Slider slider, Action<float> callback)
        {
            if (slider == null) return;

            bool isIL2CPP = TranslatorCore.Adapter?.IsIL2CPP ?? false;

            if (isIL2CPP)
            {
                AddListenerViaReflection(slider, "onValueChanged", callback, "Slider");
            }
            else
            {
                AddSliderListenerDirect(slider, callback);
            }
        }

        private static void AddSliderListenerDirect(UnityEngine.UI.Slider slider, Action<float> callback)
        {
            try
            {
                slider.onValueChanged.AddListener((val) => callback(val));
            }
            catch (Exception ex)
            {
                TranslatorCore.Adapter?.LogWarning($"[UIHelpers] Direct slider listener failed, trying reflection: {ex.Message}");
                AddListenerViaReflection(slider, "onValueChanged", callback, "Slider");
            }
        }

        /// <summary>
        /// Direct toggle listener for Mono builds. Isolated to prevent IL2CPP JIT field resolution.
        /// </summary>
        private static void AddToggleListenerDirect(Toggle toggle, Action<bool> callback)
        {
            try
            {
                toggle.onValueChanged.AddListener((val) => callback(val));
            }
            catch (Exception ex)
            {
                TranslatorCore.Adapter?.LogWarning($"[UIHelpers] Direct toggle listener failed, trying reflection: {ex.Message}");
                AddListenerViaReflection(toggle, "onValueChanged", callback, "Toggle");
            }
        }

        /// <summary>
        /// Adds a callback to an EventTrigger.Entry.
        /// Works on both Mono and IL2CPP.
        /// </summary>
        public static void AddEventTriggerCallback(EventTrigger.Entry entry, Action<BaseEventData> callback)
        {
            if (entry == null) return;

            bool isIL2CPP = TranslatorCore.Adapter?.IsIL2CPP ?? false;

            if (isIL2CPP)
            {
                AddEventTriggerCallbackReflection(entry, callback);
            }
            else
            {
                // Separate method to prevent IL2CPP JIT eager field resolution
                AddEventTriggerCallbackDirect(entry, callback);
            }
        }

        private static void AddEventTriggerCallbackDirect(EventTrigger.Entry entry, Action<BaseEventData> callback)
        {
            try
            {
                entry.callback.AddListener((data) => callback(data));
            }
            catch (Exception ex)
            {
                TranslatorCore.Adapter?.LogWarning($"[UIHelpers] Direct event trigger callback failed: {ex.Message}");
                AddEventTriggerCallbackReflection(entry, callback);
            }
        }

        /// <summary>
        /// Generic reflection-based AddListener for any UnityEvent property.
        /// Uses op_Implicit or DelegateSupport.ConvertDelegate for IL2CPP delegate conversion.
        /// </summary>
        private static void AddListenerViaReflection(object target, string eventPropertyName, Delegate callback, string debugName)
        {
            try
            {
                var prop = target.GetType().GetProperty(eventPropertyName);
                if (prop == null)
                {
                    TranslatorCore.Adapter?.LogError($"[UIHelpers] {debugName}.{eventPropertyName} property not found");
                    return;
                }

                var eventObj = prop.GetValue(target);
                if (eventObj == null)
                {
                    TranslatorCore.Adapter?.LogError($"[UIHelpers] {debugName}.{eventPropertyName} is null");
                    return;
                }

                var (addListenerMethod, paramType) = FindAddListenerMethod(eventObj);
                if (addListenerMethod == null)
                {
                    TranslatorCore.Adapter?.LogError($"[UIHelpers] {debugName} AddListener method not found");
                    return;
                }

                var il2cppDelegate = ConvertToIl2CppDelegate(paramType, callback);
                if (il2cppDelegate != null)
                {
                    addListenerMethod.Invoke(eventObj, new object[] { il2cppDelegate });
                }
                else
                {
                    TranslatorCore.Adapter?.LogError($"[UIHelpers] Failed to convert delegate for {debugName}.{eventPropertyName}");
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.Adapter?.LogError($"[UIHelpers] Failed to add {debugName} listener via reflection: {ex.Message}");
            }
        }

        private static void AddEventTriggerCallbackReflection(EventTrigger.Entry entry, Action<BaseEventData> callback)
        {
            try
            {
                var field = typeof(EventTrigger.Entry).GetField("callback");
                if (field == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] EventTrigger.Entry.callback field not found");
                    return;
                }

                var eventObj = field.GetValue(entry);
                if (eventObj == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] EventTrigger.Entry.callback is null");
                    return;
                }

                var (addListenerMethod, paramType) = FindAddListenerMethod(eventObj);
                if (addListenerMethod == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] EventTrigger AddListener method not found");
                    return;
                }

                var il2cppDelegate = ConvertToIl2CppDelegate(paramType, callback);
                if (il2cppDelegate != null)
                {
                    addListenerMethod.Invoke(eventObj, new object[] { il2cppDelegate });
                }
                else
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] Failed to convert delegate for EventTrigger callback");
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.Adapter?.LogError($"[UIHelpers] Failed to add event trigger callback: {ex.Message}");
            }
        }

        /// <summary>
        /// Iterates over children of a Transform.
        /// Works on both Mono and IL2CPP (foreach on Transform doesn't work in IL2CPP).
        /// </summary>
        public static void ForEachChild(Transform parent, Action<Transform> action)
        {
            if (parent == null) return;

            for (int i = 0; i < parent.childCount; i++)
            {
                action(parent.GetChild(i));
            }
        }

        /// <summary>
        /// Counts active children of a Transform.
        /// Works on both Mono and IL2CPP.
        /// </summary>
        public static int CountActiveChildren(Transform parent)
        {
            if (parent == null) return 0;

            int count = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                if (parent.GetChild(i).gameObject.activeSelf)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Gets a component using the non-generic method (IL2CPP compatible).
        /// </summary>
        public static T GetComponentSafe<T>(GameObject obj) where T : Component
        {
            if (obj == null) return null;
            return obj.GetComponent(typeof(T)) as T;
        }

        /// <summary>
        /// Gets a component using the non-generic method (IL2CPP compatible).
        /// </summary>
        public static T GetComponentSafe<T>(Transform transform) where T : Component
        {
            if (transform == null) return null;
            return transform.GetComponent(typeof(T)) as T;
        }

    }
}
