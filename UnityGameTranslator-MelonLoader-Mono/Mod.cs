using System.IO;
using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;
using HarmonyLib;
using UnityEngine;
using UnityGameTranslator.Core;

[assembly: MelonInfo(typeof(UnityGameTranslator.MelonLoaderMono.TranslatorMod), "UnityGameTranslator", UnityGameTranslator.PluginInfo.Version, "Community")]
[assembly: MelonGame(null, null)]

namespace UnityGameTranslator.MelonLoaderMono
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
            public string ModLoaderType => "MelonLoader-Mono";
            public bool IsIL2CPP => false;
        }

        public override void OnInitializeMelon()
        {
            // Register assembly resolver to find UniverseLib next to the mod DLL
            System.AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            // Pre-load UniverseLib from next to the mod DLL (Mods/ folder), then fallback to UserData/
            string modDllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string universeLibPath = Path.Combine(modDllDir, "UniverseLib.Mono.dll");
            if (!File.Exists(universeLibPath))
            {
                string pluginPath = Path.Combine(MelonEnvironment.UserDataDirectory, "UnityGameTranslator");
                universeLibPath = Path.Combine(pluginPath, "UniverseLib.Mono.dll");
            }
            if (File.Exists(universeLibPath))
            {
                _universeLibAssembly = Assembly.LoadFrom(universeLibPath);
                MelonLogger.Msg($"Pre-loaded UniverseLib from: {universeLibPath}");
            }
            else
            {
                MelonLogger.Warning($"UniverseLib not found at: {universeLibPath}");
            }

            TranslatorCore.Initialize(new MelonLoaderAdapter());
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;

            UnityGameTranslator.Core.UI.TranslatorUIManager.Initialize();

            int patchCount = TranslatorPatches.ApplyAll((target, prefix, postfix) =>
            {
                HarmonyInstance.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null);
            });
            MelonLogger.Msg($"Applied {patchCount} Harmony patches");

            MelonLogger.Msg("MelonLoader Mono version loaded");
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
                TranslatorScanner.ScanMono();
            }
        }

        public override void OnApplicationQuit()
        {
            TranslatorCore.OnShutdown();
        }

        /// <summary>
        /// Resolve UniverseLib if needed.
        /// </summary>
        private static Assembly OnAssemblyResolve(object sender, System.ResolveEventArgs args)
        {
            var assemblyName = new AssemblyName(args.Name);

            if (assemblyName.Name == "UniverseLib.Mono" && _universeLibAssembly != null)
            {
                return _universeLibAssembly;
            }

            return null;
        }
    }
}
