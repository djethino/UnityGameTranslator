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

        private class MelonLoaderAdapter : IModLoaderAdapter
        {
            public void LogInfo(string message) => MelonLogger.Msg(message);
            public void LogWarning(string message) => MelonLogger.Warning(message);
            public void LogError(string message) => MelonLogger.Error(message);
            public string GetPluginFolder() => Path.Combine(MelonEnvironment.UserDataDirectory, "UnityGameTranslator");
            public string ModLoaderType => "MelonLoader-IL2CPP";
            public bool IsIL2CPP => true;
        }

        public override void OnInitializeMelon()
        {
            // AssemblyResolve hook: kept ONLY because MelonLoader does not auto-register
            // its Il2CppAssemblies/ folder with the CLR resolver. The merged Core references
            // dumped Il2Cpp proxy assemblies (Il2CppTMPro, UnityEngine.UI, etc.) that live there.
            //
            // BepInEx 6 IL2CPP wires this resolution at the loader level (BepInEx/interop/),
            // which is why the BepInEx6-IL2CPP adapter does not need this hook.
            //
            // UniverseLib redirection is no longer needed: types are embedded in this assembly
            // via ILRepack and Core's external reference was rewritten by Cecil at build time.
            System.AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

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
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            TranslatorCore.OnSceneUnloaded(sceneName);
        }

        public override void OnUpdate()
        {
            // Scanner runs every frame with an adaptive budget; the budget keeps work
            // under the natural frame-time noise so per-frame impact is imperceptible.
            TranslatorCore.OnUpdate(Time.realtimeSinceStartup);
            TranslatorScanner.Scan();
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
        /// Resolve Unity assemblies from MelonLoader's Il2CppAssemblies folder
        /// (TMPro, UnityEngine.UI, etc.) referenced by the merged Core.
        /// </summary>
        private static Assembly OnAssemblyResolve(object sender, System.ResolveEventArgs args)
        {
            var assemblyName = new System.Reflection.AssemblyName(args.Name);

            try
            {
                string il2cppAssembliesDir = Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Il2CppAssemblies");
                if (Directory.Exists(il2cppAssembliesDir))
                {
                    string dllPath = Path.Combine(il2cppAssembliesDir, assemblyName.Name + ".dll");
                    if (File.Exists(dllPath))
                    {
                        return Assembly.LoadFrom(dllPath);
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
