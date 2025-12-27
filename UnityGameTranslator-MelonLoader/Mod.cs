using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using MelonLoader;
using MelonLoader.Utils;
using HarmonyLib;
using UnityEngine;
using UnityGameTranslator.Core;

[assembly: MelonInfo(typeof(UnityGameTranslator.MelonLoader.TranslatorMod), "UnityGameTranslator", UnityGameTranslator.PluginInfo.Version, "Community")]
[assembly: MelonGame(null, null)]

namespace UnityGameTranslator.MelonLoader
{
    public class TranslatorMod : MelonMod
    {
        private float lastScanTime = 0f;
        private bool isIL2CPP = false;
        private static Assembly _universeLibAssembly;

        private class MelonLoaderAdapter : IModLoaderAdapter
        {
            private readonly bool _isIL2CPP;

            public MelonLoaderAdapter(bool isIL2CPP)
            {
                _isIL2CPP = isIL2CPP;
            }

            public void LogInfo(string message) => MelonLogger.Msg(message);
            public void LogWarning(string message) => MelonLogger.Warning(message);
            public void LogError(string message) => MelonLogger.Error(message);
            public string GetPluginFolder() => Path.Combine(MelonEnvironment.UserDataDirectory, "UnityGameTranslator");
            public string ModLoaderType => "MelonLoader";
            public bool IsIL2CPP => _isIL2CPP;
        }

        public override void OnInitializeMelon()
        {
            isIL2CPP = MelonUtils.IsGameIl2Cpp();

            // For IL2CPP: Register assembly resolver BEFORE any UniverseLib types are accessed
            if (isIL2CPP)
            {
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
            }

            TranslatorCore.Initialize(new MelonLoaderAdapter(isIL2CPP));
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;

            if (isIL2CPP)
            {
                TranslatorScanner.InitializeIL2CPP();
            }

            // Initialize UI in a separate method to ensure AssemblyResolve is active
            InitializeUI();

            int patchCount = TranslatorPatches.ApplyAll((target, prefix, postfix) =>
            {
                HarmonyInstance.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null);
            });
            MelonLogger.Msg($"Applied {patchCount} Harmony patches");

            MelonLogger.Msg($"MelonLoader version loaded ({(isIL2CPP ? "IL2CPP" : "Mono")})");
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

                if (isIL2CPP)
                    TranslatorScanner.ScanIL2CPP();
                else
                    TranslatorScanner.ScanMono();
            }
        }

        public override void OnApplicationQuit()
        {
            TranslatorCore.OnShutdown();
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
