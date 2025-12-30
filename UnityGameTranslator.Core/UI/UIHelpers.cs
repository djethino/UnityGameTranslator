using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// Helper methods for UI operations that need to work on both Mono and IL2CPP.
    /// Provides abstractions for operations that behave differently between runtimes.
    /// </summary>
    public static class UIHelpers
    {
        /// <summary>
        /// Adds a value change listener to a Toggle.
        /// Works on both Mono and IL2CPP by using reflection when needed.
        /// </summary>
        public static void AddToggleListener(Toggle toggle, Action<bool> callback)
        {
            if (toggle == null) return;

            // IL2CPP requires reflection - field access fails at resolution time
            bool isIL2CPP = TranslatorCore.Adapter?.IsIL2CPP ?? false;

            if (isIL2CPP)
            {
                AddToggleListenerReflection(toggle, callback);
            }
            else
            {
                AddToggleListenerDirect(toggle, callback);
            }
        }

        /// <summary>
        /// Direct toggle listener for Mono builds.
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
                AddToggleListenerReflection(toggle, callback);
            }
        }

        /// <summary>
        /// Reflection-based toggle listener for IL2CPP builds.
        /// In IL2CPP, UnityAction types are IL2CPP wrappers, not real .NET delegates.
        /// We use Il2CppInterop's DelegateSupport to convert managed delegates.
        /// </summary>
        private static void AddToggleListenerReflection(Toggle toggle, Action<bool> callback)
        {
            try
            {
                // Get onValueChanged property via reflection
                var prop = typeof(Toggle).GetProperty("onValueChanged");
                if (prop == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] Toggle.onValueChanged property not found");
                    return;
                }

                var eventObj = prop.GetValue(toggle);
                if (eventObj == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] Toggle.onValueChanged is null");
                    return;
                }

                // Find AddListener method
                var methods = eventObj.GetType().GetMethods();
                MethodInfo addListenerMethod = null;
                foreach (var m in methods)
                {
                    if (m.Name == "AddListener" && m.GetParameters().Length == 1)
                    {
                        addListenerMethod = m;
                        break;
                    }
                }

                if (addListenerMethod == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] AddListener method not found");
                    return;
                }

                // Get the expected parameter type (IL2CPP delegate type)
                var paramType = addListenerMethod.GetParameters()[0].ParameterType;

                // Try to use Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate
                var delegateSupportType = Type.GetType("Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");
                if (delegateSupportType != null)
                {
                    var convertMethod = delegateSupportType.GetMethod("ConvertDelegate", BindingFlags.Public | BindingFlags.Static);
                    if (convertMethod != null)
                    {
                        // ConvertDelegate<T>(Delegate) - we need to make the generic version
                        var genericConvert = convertMethod.MakeGenericMethod(paramType);
                        var il2cppDelegate = genericConvert.Invoke(null, new object[] { callback });
                        addListenerMethod.Invoke(eventObj, new object[] { il2cppDelegate });
                        return;
                    }
                }

                // Fallback: try direct delegate creation (might work on some IL2CPP versions)
                try
                {
                    var unityAction = Delegate.CreateDelegate(paramType, callback.Target, callback.Method);
                    addListenerMethod.Invoke(eventObj, new object[] { unityAction });
                }
                catch
                {
                    TranslatorCore.Adapter?.LogWarning("[UIHelpers] Could not create IL2CPP delegate for toggle listener");
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.Adapter?.LogError($"[UIHelpers] Failed to add toggle listener via reflection: {ex.Message}");
            }
        }

        /// <summary>
        /// Adds a click listener to a Button.
        /// Works on both Mono and IL2CPP by using reflection when needed.
        /// </summary>
        public static void AddButtonListener(Button button, Action callback)
        {
            if (button == null) return;

            // IL2CPP requires reflection - field access fails at resolution time
            bool isIL2CPP = TranslatorCore.Adapter?.IsIL2CPP ?? false;

            if (isIL2CPP)
            {
                AddButtonListenerReflection(button, callback);
            }
            else
            {
                AddButtonListenerDirect(button, callback);
            }
        }

        /// <summary>
        /// Direct button listener for Mono builds.
        /// </summary>
        private static void AddButtonListenerDirect(Button button, Action callback)
        {
            try
            {
                button.onClick.AddListener(() => callback());
            }
            catch (Exception ex)
            {
                TranslatorCore.Adapter?.LogWarning($"[UIHelpers] Direct button listener failed, trying reflection: {ex.Message}");
                AddButtonListenerReflection(button, callback);
            }
        }

        /// <summary>
        /// Reflection-based button listener for IL2CPP builds.
        /// </summary>
        private static void AddButtonListenerReflection(Button button, Action callback)
        {
            try
            {
                // Get onClick property via reflection
                var prop = typeof(Button).GetProperty("onClick");
                if (prop == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] Button.onClick property not found");
                    return;
                }

                var eventObj = prop.GetValue(button);
                if (eventObj == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] Button.onClick is null");
                    return;
                }

                // Find AddListener method
                var methods = eventObj.GetType().GetMethods();
                MethodInfo addListenerMethod = null;
                foreach (var m in methods)
                {
                    if (m.Name == "AddListener" && m.GetParameters().Length == 1)
                    {
                        addListenerMethod = m;
                        break;
                    }
                }

                if (addListenerMethod == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] Button AddListener method not found");
                    return;
                }

                // Get the expected parameter type (IL2CPP delegate type)
                var paramType = addListenerMethod.GetParameters()[0].ParameterType;

                // Try to use Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate
                var delegateSupportType = Type.GetType("Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");
                if (delegateSupportType != null)
                {
                    var convertMethod = delegateSupportType.GetMethod("ConvertDelegate", BindingFlags.Public | BindingFlags.Static);
                    if (convertMethod != null)
                    {
                        var genericConvert = convertMethod.MakeGenericMethod(paramType);
                        var il2cppDelegate = genericConvert.Invoke(null, new object[] { callback });
                        addListenerMethod.Invoke(eventObj, new object[] { il2cppDelegate });
                        return;
                    }
                }

                // Fallback: try direct delegate creation
                try
                {
                    var unityAction = Delegate.CreateDelegate(paramType, callback.Target, callback.Method);
                    addListenerMethod.Invoke(eventObj, new object[] { unityAction });
                }
                catch
                {
                    TranslatorCore.Adapter?.LogWarning("[UIHelpers] Could not create IL2CPP delegate for button listener");
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.Adapter?.LogError($"[UIHelpers] Failed to add button listener via reflection: {ex.Message}");
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

        private static void AddEventTriggerCallbackReflection(EventTrigger.Entry entry, Action<BaseEventData> callback)
        {
            try
            {
                // Get callback property
                var prop = typeof(EventTrigger.Entry).GetField("callback");
                if (prop == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] EventTrigger.Entry.callback field not found");
                    return;
                }

                var eventObj = prop.GetValue(entry);
                if (eventObj == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] EventTrigger.Entry.callback is null");
                    return;
                }

                // Find AddListener method
                var methods = eventObj.GetType().GetMethods();
                MethodInfo addListenerMethod = null;
                foreach (var m in methods)
                {
                    if (m.Name == "AddListener" && m.GetParameters().Length == 1)
                    {
                        addListenerMethod = m;
                        break;
                    }
                }

                if (addListenerMethod == null)
                {
                    TranslatorCore.Adapter?.LogError("[UIHelpers] EventTrigger AddListener method not found");
                    return;
                }

                var paramType = addListenerMethod.GetParameters()[0].ParameterType;

                // Try DelegateSupport
                var delegateSupportType = Type.GetType("Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");
                if (delegateSupportType != null)
                {
                    var convertMethod = delegateSupportType.GetMethod("ConvertDelegate", BindingFlags.Public | BindingFlags.Static);
                    if (convertMethod != null)
                    {
                        var genericConvert = convertMethod.MakeGenericMethod(paramType);
                        var il2cppDelegate = genericConvert.Invoke(null, new object[] { callback });
                        addListenerMethod.Invoke(eventObj, new object[] { il2cppDelegate });
                        return;
                    }
                }

                // Fallback
                try
                {
                    var del = Delegate.CreateDelegate(paramType, callback.Target, callback.Method);
                    addListenerMethod.Invoke(eventObj, new object[] { del });
                }
                catch
                {
                    TranslatorCore.Adapter?.LogWarning("[UIHelpers] Could not create IL2CPP delegate for event trigger");
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
