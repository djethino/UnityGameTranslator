using System.Collections.Generic;
using System.Globalization;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Helper for language code conversion and validation.
    /// Maps ISO 639-1 codes to full language names used by Qwen3.
    /// </summary>
    public static class LanguageHelper
    {
        /// <summary>
        /// ISO 639-1 code to full language name mapping.
        /// Based on languages supported by Qwen3 for translation.
        /// </summary>
        private static readonly Dictionary<string, string> IsoToName = new Dictionary<string, string>
        {
            // Major languages (most common for game translations)
            { "en", "English" },
            { "fr", "French" },
            { "de", "German" },
            { "es", "Spanish" },
            { "it", "Italian" },
            { "pt", "Portuguese" },
            { "ru", "Russian" },
            { "pl", "Polish" },
            { "ja", "Japanese" },
            { "ko", "Korean" },
            { "zh", "Simplified Chinese" },
            { "zh-cn", "Simplified Chinese" },
            { "zh-hans", "Simplified Chinese" },
            { "zh-tw", "Traditional Chinese" },
            { "zh-hant", "Traditional Chinese" },
            { "ar", "Arabic" },
            { "tr", "Turkish" },
            { "nl", "Dutch" },
            { "sv", "Swedish" },
            { "da", "Danish" },
            { "nb", "Norwegian Bokmål" },
            { "nn", "Norwegian Nynorsk" },
            { "no", "Norwegian Bokmål" },
            { "fi", "Finnish" },
            { "cs", "Czech" },
            { "hu", "Hungarian" },
            { "ro", "Romanian" },
            { "el", "Greek" },
            { "th", "Thai" },
            { "vi", "Vietnamese" },
            { "id", "Indonesian" },
            { "ms", "Malay" },
            { "uk", "Ukrainian" },
            { "bg", "Bulgarian" },

            // Other Indo-European
            { "sk", "Slovak" },
            { "hr", "Croatian" },
            { "sr", "Serbian" },
            { "sl", "Slovenian" },
            { "lt", "Lithuanian" },
            { "lv", "Latvian" },
            { "et", "Estonian" },
            { "is", "Icelandic" },
            { "fo", "Faroese" },
            { "ga", "Irish" },
            { "cy", "Welsh" },
            { "ca", "Catalan" },
            { "gl", "Galician" },
            { "eu", "Basque" },
            { "lb", "Luxembourgish" },
            { "af", "Afrikaans" },
            { "mk", "Macedonian" },
            { "bs", "Bosnian" },
            { "sq", "Albanian" },
            { "hy", "Armenian" },
            { "be", "Belarusian" },
            { "fa", "Persian" },
            { "tg", "Tajik" },

            // South Asian
            { "hi", "Hindi" },
            { "bn", "Bengali" },
            { "ur", "Urdu" },
            { "pa", "Punjabi" },
            { "gu", "Gujarati" },
            { "mr", "Marathi" },
            { "ta", "Tamil" },
            { "te", "Telugu" },
            { "kn", "Kannada" },
            { "ml", "Malayalam" },
            { "or", "Oriya" },
            { "si", "Sinhala" },
            { "ne", "Nepali" },
            { "as", "Assamese" },
            { "sd", "Sindhi" },

            // East/Southeast Asian
            { "my", "Burmese" },
            { "km", "Khmer" },
            { "lo", "Lao" },
            { "tl", "Tagalog" },
            { "ceb", "Cebuano" },
            { "jv", "Javanese" },
            { "su", "Sundanese" },

            // Turkic
            { "az", "Azerbaijani" },
            { "uz", "Uzbek" },
            { "kk", "Kazakh" },
            { "ba", "Bashkir" },
            { "tt", "Tatar" },

            // Semitic/Afro-Asiatic
            { "he", "Hebrew" },
            { "iw", "Hebrew" }, // Old code
            { "mt", "Maltese" },

            // Other
            { "ka", "Georgian" },
            { "sw", "Swahili" },
            { "ht", "Haitian Creole" },
        };

        /// <summary>
        /// Full language names (for validation)
        /// </summary>
        private static readonly HashSet<string> ValidLanguageNames = new HashSet<string>
        {
            "English", "French", "German", "Spanish", "Italian", "Portuguese",
            "Russian", "Polish", "Japanese", "Korean", "Simplified Chinese",
            "Traditional Chinese", "Arabic", "Turkish", "Dutch", "Swedish",
            "Danish", "Norwegian Bokmål", "Norwegian Nynorsk", "Finnish",
            "Czech", "Hungarian", "Romanian", "Greek", "Thai", "Vietnamese",
            "Indonesian", "Malay", "Ukrainian", "Bulgarian", "Slovak",
            "Croatian", "Serbian", "Slovenian", "Lithuanian", "Latvian",
            "Estonian", "Icelandic", "Faroese", "Irish", "Welsh", "Catalan",
            "Galician", "Basque", "Luxembourgish", "Afrikaans", "Macedonian",
            "Bosnian", "Albanian", "Armenian", "Belarusian", "Persian", "Dari",
            "Tajik", "Hindi", "Bengali", "Urdu", "Punjabi", "Gujarati",
            "Marathi", "Tamil", "Telugu", "Kannada", "Malayalam", "Oriya",
            "Sinhala", "Nepali", "Assamese", "Sindhi", "Cantonese", "Burmese",
            "Khmer", "Lao", "Tagalog", "Cebuano", "Javanese", "Sundanese",
            "Azerbaijani", "Uzbek", "Kazakh", "Bashkir", "Tatar", "Hebrew",
            "Maltese", "Egyptian Arabic", "Levantine Arabic", "Moroccan Arabic",
            "Georgian", "Swahili", "Haitian Creole"
        };

        /// <summary>
        /// Convert ISO 639-1 code to full language name.
        /// If already a full name or unknown, returns as-is.
        /// </summary>
        public static string IsoCodeToName(string langCode)
        {
            if (string.IsNullOrEmpty(langCode))
                return "English"; // Default

            // Normalize to lowercase for lookup
            string lower = langCode.ToLowerInvariant();

            // Check if it's an ISO code
            if (IsoToName.TryGetValue(lower, out string fullName))
                return fullName;

            // Check if it's already a valid full name
            if (ValidLanguageNames.Contains(langCode))
                return langCode;

            // Unknown - return as-is (might be a less common language)
            return langCode;
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

                    // Try with full code first (e.g., "zh-CN", "fr-FR")
                    string fullCode = culture.Name.ToLowerInvariant();
                    if (IsoToName.TryGetValue(fullCode, out string fullName))
                    {
                        TranslatorCore.LogInfo($"[LanguageHelper] Matched full code '{fullCode}' -> {fullName}");
                        return fullName;
                    }

                    // Try with two-letter code
                    string twoLetter = culture.TwoLetterISOLanguageName.ToLowerInvariant();
                    if (IsoToName.TryGetValue(twoLetter, out fullName))
                    {
                        TranslatorCore.LogInfo($"[LanguageHelper] Matched two-letter '{twoLetter}' -> {fullName}");
                        return fullName;
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

                // Unity's SystemLanguage enum names match our ValidLanguageNames (e.g., "French", "German")
                // Exception: Chinese needs special handling
                string langName = unityLang.ToString();

                if (unityLang == UnityEngine.SystemLanguage.ChineseSimplified || unityLang == UnityEngine.SystemLanguage.Chinese)
                    langName = "Simplified Chinese";
                else if (unityLang == UnityEngine.SystemLanguage.ChineseTraditional)
                    langName = "Traditional Chinese";

                if (ValidLanguageNames.Contains(langName))
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
        public static bool IsValidLanguage(string language)
        {
            if (string.IsNullOrEmpty(language))
                return false;

            // Check if it's a known full name
            if (ValidLanguageNames.Contains(language))
                return true;

            // Check if it's a known ISO code
            string lower = language.ToLowerInvariant();
            return IsoToName.ContainsKey(lower);
        }

        /// <summary>
        /// Get all valid language names as a sorted array.
        /// </summary>
        public static string[] GetLanguageNames()
        {
            var list = new List<string>(ValidLanguageNames);
            list.Sort();
            return list.ToArray();
        }
    }
}
