using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGameTranslator.Core;
using UnityGameTranslator.Core.UI;

namespace UnityGameTranslator.BepInEx5
{
    [BepInPlugin("com.community.unitygametranslator", "UnityGameTranslator", PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        private static Harmony harmony;

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
            public bool IsIL2CPP => false;
        }

        void Awake()
        {
            TranslatorCore.Initialize(new BepInExAdapter(Logger));
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;
            // The UI (UniverseLib) is set up in Start, not here — see there.

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

        // BepInEx 5 runs Awake from Application's static constructor, before the engine has
        // run a single frame. Creating a component there (UniverseLib's behaviour, from inside
        // our own Awake) is a native crash on Unity 6000.5 — tolerated by earlier versions,
        // which is how it went unnoticed. Start is the first point the engine itself calls on
        // this plugin, once it is up. The other loaders (BepInEx 6, MelonLoader) start plugins
        // after the engine and keep initialising the UI at load; only this entry point differs.
        // Nothing of ours needs the UI before then: the Harmony patches above only cache and
        // translate, and every coroutine of the mod starts from the tick loop the UI owns.
        void Start()
        {
            TranslatorUIManager.Initialize();
        }

        void OnApplicationQuit()
        {
            TranslatorCore.OnShutdown();
        }
    }
}
