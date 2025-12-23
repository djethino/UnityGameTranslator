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
using Il2CppInterop.Runtime.Injection;

namespace UnityGameTranslator.BepInEx6IL2CPP
{
    [BepInPlugin("com.community.unitygametranslator", "UnityGameTranslator", "0.8.0")]
    public class Plugin : BasePlugin
    {
        private static Plugin Instance;
        private static Harmony harmony;
        private float lastScanTime = 0f;

        private class BepInEx6IL2CPPAdapter : IModLoaderAdapter
        {
            private readonly ManualLogSource logger;
            private readonly string pluginPath;

            public BepInEx6IL2CPPAdapter(ManualLogSource logger)
            {
                this.logger = logger;
                this.pluginPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }

            public void LogInfo(string message) => logger.LogInfo(message);
            public void LogWarning(string message) => logger.LogWarning(message);
            public void LogError(string message) => logger.LogError(message);
            public string GetPluginFolder() => pluginPath;
        }

        public override void Load()
        {
            Instance = this;

            TranslatorCore.Initialize(new BepInEx6IL2CPPAdapter(Log));
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;

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
