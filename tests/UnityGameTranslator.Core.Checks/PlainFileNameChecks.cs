using System;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What a name taken from a downloaded translation may be used as, on disk.
    ///
    /// ⚠ Both halves matter. The refusals are the point of the rule; the acceptances are what
    /// keeps it from costing a player their fonts — a name with spaces, hyphens, dots and
    /// non-latin letters is an ordinary font name and must go through.
    /// </summary>
    internal static class PlainFileNameChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // Ordinary font names, exactly as fonts are named.
            Accepts(check, "Arial", "the plainest name there is");
            Accepts(check, "Noto Sans CJK SC", "spaces are part of a font name");
            Accepts(check, "AdobeDevanagari-Italic", "hyphens too");
            Accepts(check, "C64 Pro Mono", "digits too");
            Accepts(check, "Segoe UI Emoji", "a family with several words");
            Accepts(check, "font_v2.bold", "underscores and a single dot are not a path");
            Accepts(check, "源ノ角ゴシック", "a name in another script is a name");
            Accepts(check, "replacement.png", "an image file name, the rule's first user");

            // What leaves the folder, or ignores it.
            Refuses(check, "", "nothing names nothing");
            Refuses(check, null, "nor does an absent value");
            Refuses(check, "../x", "a parent step leaves the folder");
            Refuses(check, "..\\..\\Desktop\\x", "however it is spelt");
            Refuses(check, "fonts/arial", "a separator makes it a path");
            Refuses(check, "fonts\\arial", "either separator");
            Refuses(check, "/usr/share/fonts/x", "an absolute path ignores the folder entirely");
            Refuses(check, "C:\\Windows\\Fonts\\arial", "so does a drive");
            Refuses(check, "arial..ttf", "two dots anywhere are refused, not only as a step");
            Refuses(check, "ari\0al", "a NUL ends a path early on every platform");
        }

        private static void Accepts(Action<bool, string, string> check, string name, string why) =>
            check(PlainFileName.Accepts(name), $"accepts {Show(name)}", why);

        private static void Refuses(Action<bool, string, string> check, string name, string why) =>
            check(!PlainFileName.Accepts(name), $"refuses {Show(name)}", why);

        private static string Show(string value) =>
            value == null ? "(null)" : value.Length == 0 ? "(empty)" : "\"" + value.Replace("\0", "\\0") + "\"";
    }
}
