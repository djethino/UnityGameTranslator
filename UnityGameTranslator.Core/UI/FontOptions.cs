using System;
using System.Collections.Generic;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>What a font is chosen to draw: legacy text (uGUI Text, the mod's own window), TextMesh Pro, an alternate TMP.</summary>
    internal enum FontOptionKind { Legacy, Tmp, AltTmp }

    /// <summary>
    /// The fonts offered wherever a font is chosen to draw text — a game font's fallback (Fonts tab)
    /// and the mod's own window (Options: interface, source text, target text fonts): Game, System and
    /// Custom fonts, by the same rules everywhere (user, 2026-10-01: « normalement ça devrait reprendre
    /// les mêmes règles »). Each entry carries its origin ([Game] / [Custom] / bare = installed):
    /// two fonts of the same name from two origins are two fonts.
    /// Moved here from TranslationParametersPanel.FillFallback — the TMP lists no longer carry their
    /// "System Fonts" heading twice.
    /// </summary>
    internal static class FontOptions
    {
        internal static List<string> For(FontOptionKind kind, string[] systemFonts)
        {
            var options = new List<string> { "(None)" };
            string[] availableFonts = null;
            bool isTMPFont = kind != FontOptionKind.Legacy;

            if (kind == FontOptionKind.AltTmp)
            {
                // For alternate TMP (TMProOld, etc.), show game fonts + system fonts
                var altFonts = TranslatorPatches.GetAlternateTMPFontNames();
                if (altFonts != null && altFonts.Length > 0)
                {
                    options.Add("--- Game Fonts ---");
                    foreach (var af in altFonts)
                        options.Add(AssetPacks.GameFontPrefix + af);
                }

                availableFonts = systemFonts;
            }
            else if (isTMPFont)
            {
                var gameFonts = FontManager.GetGameFontNames();
                var knownFonts = FontManager.GetKnownUnloadedFontNames(tmpFamily: true);
                if (gameFonts.Length > 0 || knownFonts.Length > 0)
                {
                    options.Add("--- Game Fonts ---");
                    foreach (var gf in gameFonts)
                        options.Add(AssetPacks.GameFontPrefix + gf);
                    // Known from the translation but not in memory right now — see
                    // FontManager.GetKnownUnloadedFontNames. Without them, a font used as a
                    // fallback in a past session could not be picked again.
                    foreach (var kf in knownFonts)
                        options.Add(AssetPacks.GameFontPrefix + kf + FontManager.UnloadedMarker);
                }

                availableFonts = systemFonts;
            }
            else
            {
                // Unity Font: game fonts first (with [Game] prefix), then system fonts
                var gameUnityFonts = FontManager.GetGameUnityFontNames();
                var knownFonts = FontManager.GetKnownUnloadedFontNames(tmpFamily: false);
                if (gameUnityFonts.Length > 0 || knownFonts.Length > 0)
                {
                    options.Add("--- Game Fonts ---");
                    foreach (var gf in gameUnityFonts)
                        options.Add(AssetPacks.GameFontPrefix + gf);
                    foreach (var kf in knownFonts)
                        options.Add(AssetPacks.GameFontPrefix + kf + FontManager.UnloadedMarker);
                }
                availableFonts = systemFonts;
            }

            if (availableFonts != null && availableFonts.Length > 0)
            {
                if (options.Count > 1)
                    options.Add("--- System Fonts ---");
                options.AddRange(availableFonts);
            }

            // Add custom fonts (user-provided fonts from fonts/ folder). TextMeshPro reads the file
            // itself; legacy text only through the engine's list of fonts, made once at start — a
            // file added since takes a name of the pool listed then (FontManager.LegacyReach), and
            // waits for the next launch only when the pool is spent; an atlas font never gets there.
            var customOptions = new List<string>();
            foreach (var customFont in FontManager.GetCustomFontNames())
            {
                var reach = isTMPFont ? FontFolderRedirect.Reach.Now : FontManager.LegacyReach(customFont);
                if (reach == FontFolderRedirect.Reach.Never) continue;
                customOptions.Add(AssetPacks.CustomFontPrefix + customFont
                    + (reach == FontFolderRedirect.Reach.NextLaunch ? FontManager.AfterRestartMarker : ""));
            }
            if (customOptions.Count > 0)
            {
                if (options.Count > 1)
                    options.Add("--- Custom Fonts ---");
                options.AddRange(customOptions);
            }

            return options;
        }

        /// <summary>
        /// The picker entry matching <paramref name="wanted"/>, ignoring display-only markers on
        /// either side, or null. Marker-insensitive because the same font is offered as
        /// "[Game] X" or "[Game] X (not loaded)" depending on what the game currently holds in
        /// memory — a stored fallback must select its entry in both cases.
        /// </summary>
        internal static string Find(List<string> options, string wanted)
        {
            if (options == null || string.IsNullOrEmpty(wanted)) return null;

            string target = FontManager.StripOptionMarker(wanted);
            foreach (var option in options)
            {
                if (string.Equals(FontManager.StripOptionMarker(option), target, StringComparison.Ordinal))
                    return option;
            }
            return null;
        }
    }
}
