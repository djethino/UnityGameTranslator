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
    [BepInPlugin("com.community.unitygametranslator", "UnityGameTranslator", "0.9.0")]
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
        }

        void Awake()
        {
            TranslatorCore.Initialize(new BepInExAdapter(Logger));
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;

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
            int queueCount = TranslatorCore.QueueCount;
            bool isTranslating = TranslatorCore.IsTranslating;

            if (queueCount > 0 || isTranslating)
            {
                string status = isTranslating
                    ? $"Traduction... ({queueCount} en attente)"
                    : $"En attente: {queueCount}";

                float width = 250f;
                float height = 25f;
                float x = Screen.width - width - 10;
                float y = 10;

                GUI.Box(new Rect(x, y, width, height), status);
            }
        }
    }
}
