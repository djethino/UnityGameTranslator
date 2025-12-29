using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Shared Harmony patch methods and application logic.
    /// Works with any mod loader that provides a Harmony instance.
    /// </summary>
    public static class TranslatorPatches
    {
        /// <summary>
        /// Apply all Harmony patches using the provided patcher.
        /// </summary>
        /// <param name="patcher">Function that takes (MethodInfo target, MethodInfo prefix, MethodInfo postfix) and applies the patch</param>
        /// <returns>Number of patches applied</returns>
        public static int ApplyAll(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int patchCount = 0;

            try
            {
                // TMP_Text.text setter
                var tmpTextType = typeof(TMP_Text);
                var textProp = tmpTextType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (textProp?.SetMethod != null)
                {
                    var prefix = typeof(TranslatorPatches).GetMethod(nameof(TMPText_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);
                    patcher(textProp.SetMethod, prefix, null);
                    patchCount++;
                }

                // TMP_Text.SetText(string) methods
                var setTextMethods = tmpTextType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                foreach (var method in setTextMethods)
                {
                    if (method.Name == "SetText" && method.GetParameters().Length > 0
                        && method.GetParameters()[0].ParameterType == typeof(string))
                    {
                        var prefix = typeof(TranslatorPatches).GetMethod(nameof(TMPText_SetTextMethod_Prefix), BindingFlags.Static | BindingFlags.Public);
                        patcher(method, prefix, null);
                        patchCount++;
                    }
                }

                // UI.Text.text setter
                var uiTextType = typeof(Text);
                var uiTextProp = uiTextType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (uiTextProp?.SetMethod != null)
                {
                    var prefix = typeof(TranslatorPatches).GetMethod(nameof(UIText_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);
                    patcher(uiTextProp.SetMethod, prefix, null);
                    patchCount++;
                }

                // Unity.Localization.StringTableEntry (optional)
                Type stringTableEntryType = FindStringTableEntryType();
                if (stringTableEntryType != null)
                {
                    patchCount += PatchStringTableEntry(stringTableEntryType, patcher);
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"Failed to apply patches: {e.Message}");
            }

            return patchCount;
        }

        private static Type FindStringTableEntryType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType("UnityEngine.Localization.Tables.StringTableEntry");
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static int PatchStringTableEntry(Type stringTableEntryType, Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int count = 0;
            var postfix = typeof(TranslatorPatches).GetMethod(nameof(StringTableEntry_Postfix), BindingFlags.Static | BindingFlags.Public);

            var allMethods = stringTableEntryType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            foreach (var method in allMethods)
            {
                if (method.Name == "GetLocalizedString" && method.ReturnType == typeof(string))
                {
                    try
                    {
                        patcher(method, null, postfix);
                        count++;
                    }
                    catch { }
                }
            }

            var valueProp = stringTableEntryType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (valueProp?.GetMethod != null)
            {
                try
                {
                    patcher(valueProp.GetMethod, null, postfix);
                    count++;
                }
                catch { }
            }

            var localizedValueProp = stringTableEntryType.GetProperty("LocalizedValue", BindingFlags.Public | BindingFlags.Instance);
            if (localizedValueProp?.GetMethod != null)
            {
                try
                {
                    patcher(localizedValueProp.GetMethod, null, postfix);
                    count++;
                }
                catch { }
            }

            return count;
        }

        #region Patch Methods

        // Cache for InputField textComponent exclusion (avoids repeated GetComponentInParent calls)
        // Key: instanceId, Value: true if this is an InputField's textComponent (should be excluded)
        private static readonly System.Collections.Generic.Dictionary<int, bool> inputFieldTextCache =
            new System.Collections.Generic.Dictionary<int, bool>();

        /// <summary>
        /// Check if a Text component is the textComponent of an InputField (should not be translated).
        /// Caches the result for performance.
        /// </summary>
        private static bool IsInputFieldTextComponent(Text text)
        {
            int id = text.GetInstanceID();
            if (inputFieldTextCache.TryGetValue(id, out bool isInputFieldText))
                return isInputFieldText;

            var inputField = text.GetComponentInParent<InputField>();
            bool result = inputField != null && inputField.textComponent == text;
            inputFieldTextCache[id] = result;
            return result;
        }

        /// <summary>
        /// Check if a TMP_Text component is the textComponent of a TMP_InputField (should not be translated).
        /// Caches the result for performance.
        /// </summary>
        private static bool IsTMPInputFieldTextComponent(TMP_Text text)
        {
            int id = text.GetInstanceID();
            if (inputFieldTextCache.TryGetValue(id, out bool isInputFieldText))
                return isInputFieldText;

            var inputField = text.GetComponentInParent<TMPro.TMP_InputField>();
            bool result = inputField != null && inputField.textComponent == text;
            inputFieldTextCache[id] = result;
            return result;
        }

        /// <summary>
        /// Clear the InputField cache (call on scene change).
        /// </summary>
        public static void ClearCache()
        {
            inputFieldTextCache.Clear();
        }

        public static void StringTableEntry_Postfix(object __instance, ref string __result)
        {
            // Disabled: sync translation here causes issues when the game builds strings
            // using translated parts. Let TMP_Text/UI.Text patches handle translation instead.
            // if (__instance == null || string.IsNullOrEmpty(__result)) return;
            // try { __result = TranslatorCore.TranslateText(__result); } catch { }
        }

        public static void TMPText_SetText_Prefix(TMP_Text __instance, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                // Skip if part of our own UI and should not be translated (uses hierarchy check)
                if (TranslatorCore.ShouldSkipTranslation(__instance)) return;

                // Don't translate InputField textComponent (user's typed text)
                if (IsTMPInputFieldTextComponent(__instance)) return;

                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(__instance);
                value = TranslatorCore.TranslateTextWithTracking(value, __instance, isOwnUI);
            }
            catch { }
        }

        public static void TMPText_SetTextMethod_Prefix(TMP_Text __instance, ref string __0)
        {
            if (string.IsNullOrEmpty(__0)) return;
            try
            {
                // Skip if part of our own UI and should not be translated (uses hierarchy check)
                if (TranslatorCore.ShouldSkipTranslation(__instance)) return;

                // Don't translate InputField textComponent (user's typed text)
                if (IsTMPInputFieldTextComponent(__instance)) return;

                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(__instance);
                __0 = TranslatorCore.TranslateTextWithTracking(__0, __instance, isOwnUI);
            }
            catch { }
        }

        public static void UIText_SetText_Prefix(Text __instance, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                // Skip if part of our own UI and should not be translated (uses hierarchy check)
                if (TranslatorCore.ShouldSkipTranslation(__instance)) return;

                // Don't translate InputField textComponent (user's typed text)
                if (IsInputFieldTextComponent(__instance)) return;

                // Check if own UI (use UI-specific prompt) - uses hierarchy check
                bool isOwnUI = TranslatorCore.IsOwnUITranslatable(__instance);
                value = TranslatorCore.TranslateTextWithTracking(value, __instance, isOwnUI);
            }
            catch { }
        }

        #endregion
    }
}
