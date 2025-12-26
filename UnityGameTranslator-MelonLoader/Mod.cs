using System;
using System.IO;
using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;
using HarmonyLib;
using UnityEngine;
using UnityGameTranslator.Core;

[assembly: MelonInfo(typeof(UnityGameTranslator.MelonLoader.TranslatorMod), "UnityGameTranslator", UnityGameTranslator.PluginInfo.Version, "Community")]
[assembly: MelonGame(null, null)]

namespace UnityGameTranslator.MelonLoader
{
    public class TranslatorMod : MelonMod
    {
        private float lastScanTime = 0f;
        private bool isIL2CPP = false;

        private class MelonLoaderAdapter : IModLoaderAdapter
        {
            private readonly bool isIL2CPP;
            private MethodInfo convertDelegateMethod;

            public MelonLoaderAdapter(bool isIL2CPP)
            {
                this.isIL2CPP = isIL2CPP;

                if (isIL2CPP)
                {
                    // Cache DelegateSupport.ConvertDelegate<T> method via reflection
                    var delegateSupportType = Type.GetType("Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");
                    if (delegateSupportType != null)
                    {
                        foreach (var method in delegateSupportType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (method.Name == "ConvertDelegate" && method.IsGenericMethod)
                            {
                                convertDelegateMethod = method;
                                break;
                            }
                        }
                    }
                }
            }

            public void LogInfo(string message) => MelonLogger.Msg(message);
            public void LogWarning(string message) => MelonLogger.Warning(message);
            public void LogError(string message) => MelonLogger.Error(message);
            public string GetPluginFolder() => Path.Combine(MelonEnvironment.UserDataDirectory, "UnityGameTranslator");
            public string ModLoaderType => "MelonLoader";

            public Rect DrawWindow(int id, Rect rect, Action<int> drawFunc, string title)
            {
                if (isIL2CPP && convertDelegateMethod != null)
                {
                    // IL2CPP: use DelegateSupport.ConvertDelegate<GUI.WindowFunction>
                    var genericMethod = convertDelegateMethod.MakeGenericMethod(typeof(GUI.WindowFunction));
                    var il2cppDelegate = (GUI.WindowFunction)genericMethod.Invoke(null, new object[] { drawFunc });
                    return GUI.Window(id, rect, il2cppDelegate, title);
                }
                else
                {
                    // Mono: direct delegate creation
                    return GUI.Window(id, rect, new GUI.WindowFunction(drawFunc), title);
                }
            }
        }

        public override void OnInitializeMelon()
        {
            isIL2CPP = MelonUtils.IsGameIl2Cpp();

            TranslatorCore.Initialize(new MelonLoaderAdapter(isIL2CPP));
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;
            TranslatorUI.Initialize();

            if (isIL2CPP)
            {
                TranslatorScanner.InitializeIL2CPP();
            }

            int patchCount = TranslatorPatches.ApplyAll((target, prefix, postfix) =>
            {
                HarmonyInstance.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null);
            });
            MelonLogger.Msg($"Applied {patchCount} Harmony patches");

            MelonLogger.Msg($"MelonLoader version loaded ({(isIL2CPP ? "IL2CPP" : "Mono")})");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            TranslatorCore.OnSceneChanged(sceneName);
            TranslatorScanner.OnSceneChange();
            lastScanTime = Time.realtimeSinceStartup - 0.04f;
        }

        public override void OnUpdate()
        {
            float currentTime = Time.realtimeSinceStartup;
            TranslatorCore.OnUpdate(currentTime);

            if (currentTime - lastScanTime > 0.2f)
            {
                lastScanTime = currentTime;

                if (isIL2CPP)
                    TranslatorScanner.ScanIL2CPP();
                else
                    TranslatorScanner.ScanMono();
            }
        }

        public override void OnApplicationQuit()
        {
            TranslatorCore.OnShutdown();
        }

        public override void OnGUI()
        {
            TranslatorUI.OnGUI();
        }
    }
}
