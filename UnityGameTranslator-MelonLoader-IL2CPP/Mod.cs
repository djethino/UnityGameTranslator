using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using MelonLoader;
using MelonLoader.Utils;
using HarmonyLib;
using UnityEngine;
using UnityGameTranslator.Core;
using UnityGameTranslator.Core.UI.Components;
using Il2CppInterop.Runtime.Injection;

[assembly: MelonInfo(typeof(UnityGameTranslator.MelonLoaderIL2CPP.TranslatorMod), "UnityGameTranslator", UnityGameTranslator.PluginInfo.Version, "Community")]
[assembly: MelonGame(null, null)]

namespace UnityGameTranslator.MelonLoaderIL2CPP
{
    public class TranslatorMod : MelonMod
    {
        private float lastScanTime = 0f;
        private static Assembly _universeLibAssembly;

        private class MelonLoaderAdapter : IModLoaderAdapter
        {
            public void LogInfo(string message) => MelonLogger.Msg(message);
            public void LogWarning(string message) => MelonLogger.Warning(message);
            public void LogError(string message) => MelonLogger.Error(message);
            public string GetPluginFolder() => Path.Combine(MelonEnvironment.UserDataDirectory, "UnityGameTranslator");
            public string ModLoaderType => "MelonLoader";
            public bool IsIL2CPP => true;
        }

        public override void OnInitializeMelon()
        {
            // Register assembly resolver BEFORE any UniverseLib types are accessed
            System.AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            // Pre-load the correct UniverseLib assembly
            string pluginPath = Path.Combine(MelonEnvironment.UserDataDirectory, "UnityGameTranslator");
            string universeLibPath = Path.Combine(pluginPath, "UniverseLib.ML.IL2CPP.Interop.dll");
            if (File.Exists(universeLibPath))
            {
                _universeLibAssembly = Assembly.LoadFrom(universeLibPath);
                MelonLogger.Msg($"Pre-loaded UniverseLib from: {universeLibPath}");
            }
            else
            {
                MelonLogger.Error($"UniverseLib not found at: {universeLibPath}");
            }

            TranslatorCore.Initialize(new MelonLoaderAdapter());
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;

            TranslatorScanner.InitializeIL2CPP();

            // Register Core's MonoBehaviour types for IL2CPP before UI initialization
            RegisterCoreTypes();

            // Initialize UI in a separate method to ensure AssemblyResolve is active
            InitializeUI();

            int patchCount = TranslatorPatches.ApplyAll((target, prefix, postfix) =>
            {
                HarmonyInstance.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null);
            });
            MelonLogger.Msg($"Applied {patchCount} Harmony patches");

            MelonLogger.Msg("MelonLoader IL2CPP version loaded");
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
                TranslatorScanner.ScanIL2CPP();
            }
        }

        public override void OnApplicationQuit()
        {
            TranslatorCore.OnShutdown();
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
            MelonLogger.Msg("Registered Core MonoBehaviour types for IL2CPP");
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

        /// <summary>
        /// Resolve UniverseLib.Mono requests to the IL2CPP variant.
        /// Core references UniverseLib.Mono at compile-time, but at runtime we use the IL2CPP variant.
        /// </summary>
        private static Assembly OnAssemblyResolve(object sender, System.ResolveEventArgs args)
        {
            var assemblyName = new System.Reflection.AssemblyName(args.Name);

            // Redirect UniverseLib.Mono to our pre-loaded IL2CPP variant
            if (assemblyName.Name == "UniverseLib.Mono" && _universeLibAssembly != null)
            {
                return _universeLibAssembly;
            }

            return null;
        }
    }
}
