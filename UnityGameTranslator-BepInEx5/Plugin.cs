using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.BepInEx5
{
    [BepInPlugin("com.community.unitygametranslator", "UnityGameTranslator", PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        private static Harmony harmony;
        private float lastScanTime = 0f;

        private class BepInExAdapter : IModLoaderAdapter
        {
            private readonly ManualLogSource logger;
            private readonly string pluginPath;

            public BepInExAdapter(ManualLogSource logger)
            {
                this.logger = logger;
                this.pluginPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }

            public void LogInfo(string message) => logger.LogInfo(message);
            public void LogWarning(string message) => logger.LogWarning(message);
            public void LogError(string message) => logger.LogError(message);
            public string GetPluginFolder() => pluginPath;
            public string ModLoaderType => "BepInEx5";

            public Rect DrawWindow(int id, Rect rect, Action<int> drawFunc, string title)
            {
                return GUI.Window(id, rect, new GUI.WindowFunction(drawFunc), title);
            }
        }

        void Awake()
        {
            TranslatorCore.Initialize(new BepInExAdapter(Logger));
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;
            TranslatorUI.Initialize();

            harmony = new Harmony("com.community.unitygametranslator");
            int patchCount = TranslatorPatches.ApplyAll((target, prefix, postfix) =>
            {
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null);
            });
            Logger.LogInfo($"Applied {patchCount} Harmony patches");

            SceneManager.sceneLoaded += (scene, mode) =>
            {
                TranslatorCore.OnSceneChanged(scene.name);
                TranslatorScanner.OnSceneChange();
                lastScanTime = Time.realtimeSinceStartup - 0.04f;
            };
        }

        void Update()
        {
            float currentTime = Time.realtimeSinceStartup;
            TranslatorCore.OnUpdate(currentTime);

            if (currentTime - lastScanTime > 0.2f)
            {
                lastScanTime = currentTime;
                TranslatorScanner.ScanMono();
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
}
