using System.IO;
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
            // UniverseLib is now embedded in this assembly via ILRepack — no pre-load or
            // AssemblyResolve hook needed. The runtime resolves UniverseLib types directly
            // from the merged assembly.

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
    }
}
