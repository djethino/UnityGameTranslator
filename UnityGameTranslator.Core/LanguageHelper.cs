using System.Globalization;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Language names, codes, and what the machine this game runs on is set to.
    ///
    /// ⚠ The tables live in UnityGameTranslator.Common.Languages now — read the note there before
    /// touching anything, in particular the fact that it holds TWO inventories of different sizes:
    /// codes for talking to the outside world, and the wider set of languages a model can actually
    /// translate into, five of which have no ISO 639-1 code at all.
    ///
    /// What stays here is what the shared library cannot hold: reading the system language, which
    /// needs UnityEngine and the mod loader's log, and the defaults this mod applies when it is
    /// handed nothing — a default is a decision about behaviour, not a fact about languages.
    /// </summary>
    public static class LanguageHelper
    {
        /// <summary>
        /// Get ISO 639-1 code from a full language name (e.g., "French" → "fr").
        /// Returns null if not found — including for the languages that have no code at all.
        /// </summary>
        public static string NameToIsoCode(string languageName) => Languages.CodeOf(languageName);

        /// <summary>
        /// Get the Google Translate API language code from a language name.
        /// </summary>
        public static string GetGoogleLanguageCode(string languageName) => Languages.GoogleCode(languageName);

        /// <summary>
        /// Get the DeepL API language code from a language name. Source and target differ for a
        /// handful of languages, which is why the side has to be said.
        /// </summary>
        public static string GetDeepLLanguageCode(string languageName, bool isTarget = true) =>
            Languages.DeepLCode(languageName, isTarget);

        /// <summary>
        /// Convert ISO 639-1 code to full language name.
        /// If already a full name or unknown, returns as-is.
        ///
        /// ⚠ Nothing at all gives "English", and that default belongs to the mod rather than to the
        /// shared table: with no language set the mod still has to translate into something, while
        /// a lookup asked about nothing should answer nothing. Callers have relied on this for a
        /// long time; it is stated here rather than hidden one layer down.
        /// </summary>
        public static string IsoCodeToName(string langCode)
        {
            if (string.IsNullOrEmpty(langCode))
                return "English";

            return Languages.NameOf(langCode);
        }

        /// <summary>
        /// Get the full language name from the system's current UI culture.
        /// Falls back to Unity's Application.systemLanguage if .NET culture is invariant (MelonLoader issue).
        /// </summary>
        public static string GetSystemLanguageName()
        {
            // First try .NET CultureInfo
            try
            {
                var culture = CultureInfo.CurrentUICulture;

                // Check if culture is valid (not invariant - MelonLoader sets it to invariant)
                if (culture != null && !string.IsNullOrEmpty(culture.Name) && culture.TwoLetterISOLanguageName != "iv")
                {
                    TranslatorCore.LogInfo($"[LanguageHelper] CurrentUICulture.Name='{culture.Name}' TwoLetter='{culture.TwoLetterISOLanguageName}'");

                    // Try with full code first (e.g., "zh-CN", "fr-FR"). Knows() rather than a
                    // lookup that hands the input back: "fr-FR" has to fall through to "fr" and
                    // not be taken for a language of its own.
                    string fullCode = culture.Name.ToLowerInvariant();
                    if (Languages.Knows(fullCode))
                    {
                        string fullName = Languages.NameOf(fullCode);
                        TranslatorCore.LogInfo($"[LanguageHelper] Matched full code '{fullCode}' -> {fullName}");
                        return fullName;
                    }

                    // Try with two-letter code
                    string twoLetter = culture.TwoLetterISOLanguageName.ToLowerInvariant();
                    if (Languages.Knows(twoLetter))
                    {
                        string twoLetterName = Languages.NameOf(twoLetter);
                        TranslatorCore.LogInfo($"[LanguageHelper] Matched two-letter '{twoLetter}' -> {twoLetterName}");
                        return twoLetterName;
                    }
                }
                else
                {
                    TranslatorCore.LogInfo($"[LanguageHelper] CultureInfo is invariant (MelonLoader?), trying Unity API");
                }
            }
            catch (System.Exception e)
            {
                TranslatorCore.LogWarning($"[LanguageHelper] CultureInfo exception: {e.Message}");
            }

            // Fallback: Use Unity's Application.systemLanguage (works even when .NET culture is invariant)
            try
            {
                var unityLang = UnityEngine.Application.systemLanguage;
                TranslatorCore.LogInfo($"[LanguageHelper] Unity.systemLanguage = {unityLang}");

                // Unity's SystemLanguage enum names match the shared list (e.g., "French", "German").
                // Chinese is the exception: Unity splits it three ways and spells it differently.
                string langName = unityLang.ToString();

                if (unityLang == UnityEngine.SystemLanguage.ChineseSimplified || unityLang == UnityEngine.SystemLanguage.Chinese)
                    langName = "Simplified Chinese";
                else if (unityLang == UnityEngine.SystemLanguage.ChineseTraditional)
                    langName = "Traditional Chinese";

                if (Languages.IsTranslatable(langName))
                {
                    TranslatorCore.LogInfo($"[LanguageHelper] Matched Unity language -> {langName}");
                    return langName;
                }
            }
            catch (System.Exception e)
            {
                TranslatorCore.LogWarning($"[LanguageHelper] Unity.systemLanguage exception: {e.Message}");
            }

            // No detection worked
            TranslatorCore.LogWarning("[LanguageHelper] Could not detect system language, defaulting to English");
            return "English";
        }

        /// <summary>
        /// Check if a language name is valid/supported.
        /// </summary>
        public static bool IsValidLanguage(string language) => Languages.IsTranslatable(language);

        /// <summary>
        /// Get all valid language names as a sorted array.
        /// </summary>
        public static string[] GetLanguageNames() => Languages.Names();
    }
}
