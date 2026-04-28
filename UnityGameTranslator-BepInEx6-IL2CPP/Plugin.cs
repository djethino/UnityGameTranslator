using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGameTranslator.Core;
using UnityGameTranslator.Core.UI.Components;
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

            public BepInEx6IL2CPPAdapter(ManualLogSource logger)
            {
                this.logger = logger;
                this.pluginPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }

            public void LogInfo(string message) => logger.LogInfo(message);
            public void LogWarning(string message) => logger.LogWarning(message);
            public void LogError(string message) => logger.LogError(message);
            public string GetPluginFolder() => pluginPath;
            public string ModLoaderType => "BepInEx6-IL2CPP";
            public bool IsIL2CPP => true;
        }

        public override void Load()
        {
            Instance = this;

            // UniverseLib is now embedded in this assembly via ILRepack — no pre-load needed.
            // Core's UniverseLib.Mono compile-time reference was rewritten to UniverseLib.BIE.IL2CPP.Interop
            // by the Cecil pre-merge step, so the runtime resolves types directly to the merged assembly.

            TranslatorCore.Initialize(new BepInEx6IL2CPPAdapter(Log));
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;

            // Initialize IL2CPP scanning support
            TranslatorScanner.InitializeIL2CPP();

            // Register Core's MonoBehaviour types for IL2CPP before UI initialization
            RegisterCoreTypes();

            // Initialize UI in a separate method to ensure AssemblyResolve is active
            // before the JIT tries to resolve UniverseLib types
            InitializeUI();

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

        /// <summary>
        /// Registers Core's MonoBehaviour types with IL2CPP injector.
        /// Must be called before any of these types are used.
        /// NoInlining prevents JIT from loading these types before registration.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RegisterCoreTypes()
        {
            // Register UI component types from Core that are used with AddComponent
            ClassInjector.RegisterTypeInIl2Cpp<DynamicScrollbarHider>();
            Log.LogInfo("Registered Core MonoBehaviour types for IL2CPP");
        }

        /// <summary>
        /// Separate method to initialize UI after AssemblyResolve hook is active.
        /// NoInlining ensures JIT doesn't try to resolve UniverseLib types until this method is called.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void InitializeUI()
        {
            UnityGameTranslator.Core.UI.TranslatorUIManager.Initialize();
        }

        public class TranslatorUpdateBehaviour : MonoBehaviour
        {
            private string lastSceneName = "";
            private string previousSceneName = "";

            void Update()
            {
                Instance?.OnUpdate();

                var activeScene = SceneManager.GetActiveScene();
                if (activeScene.name != lastSceneName)
                {
                    // Scene changed — the previous scene was effectively "unloaded"
                    if (!string.IsNullOrEmpty(previousSceneName))
                    {
                        TranslatorCore.OnSceneUnloaded(previousSceneName);
                    }
                    previousSceneName = lastSceneName;
                    lastSceneName = activeScene.name;
                    TranslatorCore.OnSceneChanged(activeScene.name);
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
                TranslatorScanner.Scan();
            }
        }

    }
}
