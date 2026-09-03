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

        private bool uiInitialized;

        void Awake()
        {
            TranslatorCore.Initialize(new BepInExAdapter(Logger));
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;
            // The UI (UniverseLib) is set up on the first scene load, not here — see below.

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
                // BepInEx 5 runs this Awake from Application's static constructor, before the
                // engine has run a frame. On Unity 6000.5 (tolerated by earlier versions), adding
                // a component that carries Start or Update to a game object at that moment is a
                // native crash — UniverseLib's behaviour from inside this Awake, and this very
                // plugin the moment it declared a Start of its own. So the UI is set up on the
                // first scene load, the first event the running engine hands us; this plugin
                // keeps to Awake and OnApplicationQuit. The other loaders start plugins after
                // the engine and initialise the UI at load, unchanged. Nothing of ours needs the
                // UI before then: the patches only cache and translate, and every coroutine of
                // the mod starts from the tick loop the UI owns.
                if (!uiInitialized)
                {
                    uiInitialized = true;
                    TranslatorUIManager.Initialize();
                }
                TranslatorCore.OnSceneChanged(scene.name);
            };
            SceneManager.sceneUnloaded += (scene) =>
            {
                TranslatorCore.OnSceneUnloaded(scene.name);
            };
        }

        void OnApplicationQuit()
        {
            TranslatorCore.OnShutdown();
        }
    }
}
