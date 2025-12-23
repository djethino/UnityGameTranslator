using System;
using System.IO;
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

        private class MelonLoaderAdapter : IModLoaderAdapter
        {
            public void LogInfo(string message) => MelonLogger.Msg(message);
            public void LogWarning(string message) => MelonLogger.Warning(message);
            public void LogError(string message) => MelonLogger.Error(message);
            public string GetPluginFolder() => Path.Combine(MelonEnvironment.UserDataDirectory, "UnityGameTranslator");
        }

        public override void OnInitializeMelon()
        {
            isIL2CPP = MelonUtils.IsGameIl2Cpp();

            TranslatorCore.Initialize(new MelonLoaderAdapter());
            TranslatorCore.OnTranslationComplete = TranslatorScanner.OnTranslationComplete;

            if (isIL2CPP)
            {
                TranslatorScanner.InitializeIL2CPP();
            }

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

        public override void OnGUI()
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
