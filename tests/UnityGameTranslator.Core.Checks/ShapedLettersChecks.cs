using System;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Which translations lose a game's letter spacing (LetterSpacing): those holding a letter of a
    /// script CLDR says is shaped (the shapers' own table), whatever their language.
    /// </summary>
    internal static class ShapedLettersChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            void Expect(string text, bool shaped, string what) =>
                check(ShapingCommon.HoldsShapedLetters(text) == shaped, what, $"\"{text}\" → {(shaped ? "shaped" : "not shaped")}");
            Expect("सामान", true, "Hindi (Devanagari)");
            Expect("সামগ্রী", true, "Bengali");
            Expect("مرحبا", true, "Arabic, logical");
            Expect("\uFEE3\uFEAE\uFEA3\uFE92\uFE8E", true, "Arabic, presentation forms");
            Expect("ܫܠܡܐ", true, "Syriac");
            Expect("សួស្តី", true, "Khmer");
            Expect("OK", false, "Latin only, in any translation");
            Expect("Élément à l'œil", false, "Latin with accents");
            Expect("Привет", false, "Cyrillic");
            Expect("こんにちは", false, "Japanese");
            Expect("", false, "empty");
        }
    }
}
