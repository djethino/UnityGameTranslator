using System;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Word boundaries for the scripts written without spaces, marked with U+200B. The expected
    /// segmentations are the ones a speaker would write with a space between words — never a
    /// read-back of the breaker. "|" stands for the zero-width space below.
    /// </summary>
    internal static class WordBreakerChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // Thai
            Expect(check, "สวัสดีครับ", "สวัสดี|ครับ", "Thai: สวัสดี ครับ", "two dictionary words");
            Expect(check, "ภาษาไทยเป็นภาษาที่ไม่มีช่องว่าง", "ภาษา|ไทย|เป็น|ภาษา|ที่|ไม่มี|ช่อง|ว่าง",
                "Thai sentence", "least-words path — ไม่มี is one entry in ICU's list, ช่องว่าง two");
            Expect(check, "กด A เพื่อเริ่ม", "กด A เพื่อ|เริ่ม", "Thai around a Latin key name", "the Latin run is left alone, spaces kept");
            check(IndicReorderer.NeedsReordering("เริ่ม") == false, "Thai is never reordered", "its left vowels are stored in visual order");

            // Lao, Khmer, Myanmar — one known pair each, from ICU's own lists.
            Expect(check, "ສະບາຍດີບໍ່", "ສະບາຍດີ|ບໍ່", "Lao: ສະບາຍດີ ບໍ່", "");
            Expect(check, "សួស្តីពិភពលោក", "សួស្តី|ពិភពលោក", "Khmer: សួស្តី ពិភពលោក", "");
            Expect(check, "မင်္ဂလာပါကမ္ဘာ", "မင်္ဂလာ|ပါ|ကမ္ဘာ", "Myanmar: မင်္ဂလာ ပါ ကမ္ဘာ", "ပါ is a word of its own in ICU's list");

            // What must not change.
            string latin = "Hello world";
            check(!WordBreaker.NeedsBreaking(latin) && ReferenceEquals(WordBreaker.Break(latin, out _), latin),
                "Latin untouched, same instance", "the range check answers before any dictionary is read");
            string hindi = "विकल्प";
            check(!WordBreaker.NeedsBreaking(hindi), "Devanagari is not a spaceless script here", "Hindi writes its spaces");
            string single = "ก";
            check(ReferenceEquals(WordBreaker.Break(single, out _), single), "a single character has no break", "");

            // Never inside a grapheme: the tone mark and the vowel below stay with their consonant.
            string got = WordBreaker.Break("ที่นี่", out _);
            check(got.IndexOf("​่") < 0 && got.IndexOf("​ี") < 0,
                "no break before a combining mark", got.Replace('​', '|'));
        }

        private static void Expect(Action<bool, string, string> check, string text, string expected, string what, string why)
        {
            string got = WordBreaker.Break(text, out string whyNot).Replace('​', '|');
            check(got == expected, what, got == expected ? why : $"got '{got}', expected '{expected}'{(whyNot != null ? " — " + whyNot : "")}");
        }
    }
}
