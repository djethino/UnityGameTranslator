using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Who destroys the fonts the mod makes — debug mode only. A game destroyed the legacy
    /// replacement font in the middle of play although it carries DontUnloadUnusedAsset, and every
    /// text still wearing it showed filled squares until the game wrote it again (2026-10-03). The
    /// two ways an engine object dies on the game's request are watched: an explicit Destroy /
    /// DestroyImmediate given one of the mod's fonts, and an unload of unused assets. Each is said in
    /// the log with the frame, next to the line that notices a dead replacement.
    /// </summary>
    internal static class FontWitness
    {
        private static readonly HashSet<int> _ours = new HashSet<int>();

        /// <summary>A font the mod made, from now on watched.</summary>
        internal static void Watch(Font font)
        {
            if (font == null || !TranslatorCore.DebugMode) return;
            lock (_ours) _ours.Add(font.GetInstanceID());
        }

        /// <summary>A font the mod destroys itself (a probe): not the game's doing.</summary>
        internal static void Forget(Font font)
        {
            if (font == null) return;
            lock (_ours) _ours.Remove(font.GetInstanceID());
        }

        public static int Patch(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            if (!TranslatorCore.DebugMode) return 0;
            int placed = 0;
            const BindingFlags hook = BindingFlags.Static | BindingFlags.Public;
            var destroyed = typeof(FontWitness).GetMethod(nameof(Destroy_Prefix), hook);
            foreach (var m in typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Destroy" && m.Name != "DestroyImmediate") continue;
                var ps = m.GetParameters();
                if (ps.Length == 0 || ps[0].Name != "obj") continue;
                patcher(m, destroyed, null);
                placed++;
            }
            var unload = typeof(Resources).GetMethod("UnloadUnusedAssets", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (unload != null)
            {
                patcher(unload, null, typeof(FontWitness).GetMethod(nameof(UnloadUnusedAssets_Postfix), hook));
                placed++;
            }
            TranslatorCore.LogInfo($"[FontWitness] watching {placed} way(s) a mod font can be destroyed (debug)");
            return placed;
        }

        public static void Destroy_Prefix(object obj)
        {
            try
            {
                if (!(obj is Font font) || font == null) return;
                bool ours;
                lock (_ours) ours = _ours.Contains(font.GetInstanceID());
                if (!ours) return;
                TranslatorCore.LogWarning($"[FontWitness] frame {Time.frameCount}: the game destroys the mod's font '{font.name}'\n{Environment.StackTrace}");
            }
            catch (Exception ex) { Faults.Say("FontWitness.Destroy", ex); }
        }

        public static void UnloadUnusedAssets_Postfix()
        {
            TranslatorCore.LogInfo($"[FontWitness] frame {Time.frameCount}: the game unloads unused assets");
        }
    }
}
