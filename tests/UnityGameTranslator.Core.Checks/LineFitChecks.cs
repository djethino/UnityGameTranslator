using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A late translation wrapped the way the game wraps (TextRouter.WrapLikeTheGame), at break
    /// opportunities Unicode's line breaking algorithm gives — for every script, not spaces plus
    /// Chinese and Japanese. The measure here is one unit per character.
    /// </summary>
    internal static class LineFitChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            Func<string, float?> length = s => s.Length;
            TextRouter.LineFit Fit(float kept) => new TextRouter.LineFit { Kept = kept, Refused = float.PositiveInfinity, Break = "\n" };

            string latin = TextRouter.WrapLikeTheGame("one two three", Fit(7), length);
            check(latin == "one two\nthree", "Latin text breaks at its spaces", Show(latin));

            string japanese = TextRouter.WrapLikeTheGame("日本語です。テスト", Fit(5), length);
            check(japanese != null && japanese.Contains("\n") && !japanese.Contains("\n。"),
                "Japanese breaks between characters, never before a full stop", Show(japanese));

            string kept = TextRouter.WrapLikeTheGame("ab\ncd ef", Fit(100), length);
            check(kept == "ab\ncd ef", "a newline the text holds is kept where it is", Show(kept));

            string thai = TextRouter.WrapLikeTheGame("สวัสดี\u200Bครับผม", Fit(7), length);
            check(thai != null && thai.Contains("\u200B\n"),
                "Thai breaks at the zero width space the word breaker put between its words", Show(thai));

            string word = TextRouter.WrapLikeTheGame("extraordinarily", Fit(5), length);
            check(word == "extraordinarily", "a word wider than the line stays whole", Show(word));
        }

        private static string Show(string s) => s == null ? "null" : s.Replace("\n", "\\n").Replace("\u200B", "<ZWSP>");
    }
}
