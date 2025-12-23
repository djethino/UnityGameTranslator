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

        public static void StringTableEntry_Postfix(object __instance, ref string __result)
        {
            if (__instance == null || string.IsNullOrEmpty(__result)) return;
            try { __result = TranslatorCore.TranslateText(__result); } catch { }
        }

        public static void TMPText_SetText_Prefix(TMP_Text __instance, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            try { value = TranslatorCore.TranslateText(value); } catch { }
        }

        public static void TMPText_SetTextMethod_Prefix(ref string __0)
        {
            if (string.IsNullOrEmpty(__0)) return;
            try { __0 = TranslatorCore.TranslateText(__0); } catch { }
        }

        public static void UIText_SetText_Prefix(Text __instance, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            try { value = TranslatorCore.TranslateText(value); } catch { }
        }

        #endregion
    }
}
