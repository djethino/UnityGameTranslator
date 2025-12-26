using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGameTranslator.Core;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;

namespace UnityGameTranslator.BepInEx6IL2CPP
{
    [BepInPlugin("com.community.unitygametranslator", "UnityGameTranslator", PluginInfo.Version)]
    public class Plugin : BasePlugin
    {
        private static Plugin Instance;
        private static Harmony harmony;
        private float lastScanTime = 0f;

        private class BepInEx6IL2CPPAdapter : IModLoaderAdapter
        {
            private readonly ManualLogSource logger;
            private readonly string pluginPath;
            private MethodInfo convertDelegateMethod;
            private Type windowFunctionType;

            public BepInEx6IL2CPPAdapter(ManualLogSource logger)
            {
                this.logger = logger;
                this.pluginPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                // Cache DelegateSupport.ConvertDelegate<T> method
                var delegateSupportType = typeof(DelegateSupport);
                foreach (var method in delegateSupportType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name == "ConvertDelegate" && method.IsGenericMethod)
                    {
                        convertDelegateMethod = method;
                        break;
                    }
                }

                // Get the IL2CPP WindowFunction type
                windowFunctionType = typeof(GUI).GetNestedType("WindowFunction");
            }

            public void LogInfo(string message) => logger.LogInfo(message);
            public void LogWarning(string message) => logger.LogWarning(message);
            public void LogError(string message) => logger.LogError(message);
            public string GetPluginFolder() => pluginPath;
            public string ModLoaderType => "BepInEx6-IL2CPP";

            public Rect DrawWindow(int id, Rect rect, Action<int> drawFunc, string title)
            {
                // IL2CPP requires delegate conversion via Il2CppInterop
                if (convertDelegateMethod != null && windowFunctionType != null)
                {
                    var genericMethod = convertDelegateMethod.MakeGenericMethod(windowFunctionType);
                    var il2cppDelegate = (GUI.WindowFunction)genericMethod.Invoke(null, new object[] { drawFunc });
                    return GUI.Window(id, rect, il2cppDelegate, title);
                }
                else
                {
                    // Fallback (shouldn't happen)
                    logger.LogError("[DrawWindow] Failed to initialize delegate conversion");
                    return rect;
                }
            }
        }

        public override void Load()
        {
            Instance = this;

            TranslatorCore.Initialize(new BepInEx6IL2CPPAdapter(Log));
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;
            TranslatorUI.Initialize();

            // Initialize IL2CPP scanning support
            TranslatorScanner.InitializeIL2CPP();

            harmony = new Harmony("com.community.unitygametranslator");
            int patchCount = TranslatorPatches.ApplyAll((target, prefix, postfix) =>
            {
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null);
            });
            Log.LogInfo($"Applied {patchCount} Harmony patches");

            // Register IL2CPP update component
            ClassInjector.RegisterTypeInIl2Cpp<TranslatorUpdateBehaviour>();
            var go = new GameObject("UnityGameTranslator_Updater");
            GameObject.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<TranslatorUpdateBehaviour>();

            Log.LogInfo("BepInEx 6 IL2CPP version loaded");
        }

        public class TranslatorUpdateBehaviour : MonoBehaviour
        {
            private string lastSceneName = "";

            void Update()
            {
                Instance?.OnUpdate();

                var activeScene = SceneManager.GetActiveScene();
                if (activeScene.name != lastSceneName)
                {
                    lastSceneName = activeScene.name;
                    TranslatorCore.OnSceneChanged(activeScene.name);
                    TranslatorScanner.OnSceneChange();
                    Instance.lastScanTime = Time.realtimeSinceStartup - 0.04f;
                }
            }

            void OnApplicationQuit()
            {
                TranslatorCore.OnShutdown();
            }

            void OnGUI()
            {
                TranslatorUI.OnGUI();
            }
        }

        private void OnUpdate()
        {
            float currentTime = Time.realtimeSinceStartup;
            TranslatorCore.OnUpdate(currentTime);

            if (currentTime - lastScanTime > 0.2f)
            {
                lastScanTime = currentTime;
                TranslatorScanner.ScanIL2CPP();
            }
        }
    }
}
