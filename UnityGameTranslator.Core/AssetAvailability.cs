using System;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Answers one question in a single place: is an asset a translation refers to actually present
    /// on THIS machine?
    ///
    /// A translation can reference assets that do not travel with it — replacement fonts and images
    /// live in the user's fonts/ and images/ folders, or come from the author's external resources
    /// link. Several parts of the UI need to know what is missing (offer only usable fonts, tell the
    /// user what a link would bring, avoid applying something that will silently fall back).
    ///
    /// Rule of thumb: report "missing" only when we can actually tell. Where a runtime cannot answer
    /// (e.g. no OS font path map), treat the asset as available rather than hiding a working option.
    /// </summary>
    public static class AssetAvailability
    {
        /// <summary>
        /// Is a font reference usable here? Accepts picker/settings values with their origin prefix
        /// ("[Game] X", "[Custom] X") as well as a plain system font name.
        /// </summary>
        public static bool IsFontAvailable(string fontRef)
        {
            if (string.IsNullOrEmpty(fontRef)) return false;

            string name = FontManager.StripFontPrefix(fontRef);

            if (FontManager.IsGameFontRef(fontRef))
                return FontManager.IsGameFont(name);

            if (fontRef.StartsWith("[Custom] "))
                return Array.Exists(CustomFontLoader.GetCustomFontNames(),
                    f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));

            return IsSystemFontAvailable(name);
        }

        /// <summary>
        /// Is a system font installed? Resolved through FontManager.GetSystemFontPath (OS name→path
        /// map first, filesystem lookup as a fallback).
        /// </summary>
        public static bool IsSystemFontAvailable(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return false;

            // No map on this runtime: we cannot rule the font out, so accept it rather than hiding
            // an option that would have worked.
            if (!FontManager.HasSystemFontPathMap) return true;

            return FontManager.GetSystemFontPath(fontName) != null;
        }

        /// <summary>What the current translation refers to but cannot find on this machine.</summary>
        public struct MissingResources
        {
            public int Fonts;
            public int Images;

            public int Total => Fonts + Images;
            public bool Any => Total > 0;
        }

        /// <summary>
        /// Count the replacement assets the current translation asks for that are absent here.
        /// These files never travel inside the translation — the author ships them behind the
        /// resources link — so this is what tells the user the link still has something for them.
        /// </summary>
        public static MissingResources GetMissingResources()
        {
            var missing = new MissingResources();

            // Replacement fonts named by the translation's per-font settings
            foreach (var entry in TranslatorCore.FontSettingsMap)
            {
                var settings = entry.Value;
                if (settings == null || !settings.enabled || string.IsNullOrEmpty(settings.fallback))
                    continue;
                if (!IsFontAvailable(settings.fallback))
                    missing.Fonts++;
            }

            // The interface font, when the translation asks for one we do not have
            string uiFont = TranslatorCore.TranslationUIFont;
            if (!string.IsNullOrEmpty(uiFont) && !IsFontAvailable(uiFont))
                missing.Fonts++;

            // Replacement images declared by the translation, whose PNG is not in images/
            foreach (var entry in ImageReplacer.GetAll())
            {
                if (!ImageReplacer.HasReplacementFile(entry.Key))
                    missing.Images++;
            }

            return missing;
        }
    }
}
