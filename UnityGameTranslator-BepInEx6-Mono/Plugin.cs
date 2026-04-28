using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGameTranslator.Core;
using UnityGameTranslator.Core.UI;

namespace UnityGameTranslator.BepInEx6Mono
{
    [BepInPlugin("com.community.unitygametranslator", "UnityGameTranslator", PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        private static Harmony harmony;

        private class BepInEx6MonoAdapter : IModLoaderAdapter
        {
            private readonly ManualLogSource logger;
            private readonly string pluginPath;

            public BepInEx6MonoAdapter(ManualLogSource logger)
            {
                this.logger = logger;
                this.pluginPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }

            public void LogInfo(string message) => logger.LogInfo(message);
            public void LogWarning(string message) => logger.LogWarning(message);
            public void LogError(string message) => logger.LogError(message);
            public string GetPluginFolder() => pluginPath;
            public string ModLoaderType => "BepInEx6-Mono";
            public bool IsIL2CPP => false;
        }

        void Awake()
        {
            TranslatorCore.Initialize(new BepInEx6MonoAdapter(Logger));
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;
            TranslatorUIManager.Initialize();

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
            };
            SceneManager.sceneUnloaded += (scene) =>
            {
                TranslatorCore.OnSceneUnloaded(scene.name);
            };
        }

        void Update()
        {
            // Scanner runs every frame with an adaptive budget; the budget keeps work
            // under the natural frame-time noise so per-frame impact is imperceptible.
            TranslatorCore.OnUpdate(Time.realtimeSinceStartup);
            TranslatorScanner.Scan();
        }

        void OnApplicationQuit()
        {
            TranslatorCore.OnShutdown();
        }
    }
}
