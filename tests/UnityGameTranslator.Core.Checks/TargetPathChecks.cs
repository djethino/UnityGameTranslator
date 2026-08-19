using System;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Which of the three candidates names one step of a path.
    ///
    /// ⚠ The order is the whole rule, and reversing two of them fails quietly rather than loudly:
    /// every element in a panel would produce the same path, and a pattern written against one of
    /// them would match all of them. Nothing on screen says so.
    /// </summary>
    internal static class TargetPathChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            Segment(check, "SaveButton", "unity-button", "Button", "SaveButton",
                    "a name is the most specific thing available");
            Segment(check, "", "unity-button", "Button", "unity-button",
                    "no name: the class groups, which is better than the type");
            Segment(check, null, null, "Button", "Button",
                    "no class either: the type is the last resort");
            Segment(check, "", "", "", "?",
                    "never an empty segment — a hole changes what a pattern matches");

            // 🔴 The same element must name itself the same way on both runtimes, or a pattern
            // written while playing on one silently stops matching on the other.
            Segment(check, null, null, "Il2CppLabel", "Label",
                    "the interop prefix is not part of the type's name");
            check(TargetPath.StripInteropPrefix("Il2CppTMPro.TMP_Text") == "TMPro.TMP_Text",
                  "StripInteropPrefix removes only the prefix", "the rest of the name is untouched");
            check(TargetPath.StripInteropPrefix("Illustration") == "Illustration",
                  "a name merely starting with Il is left alone",
                  "the prefix is six exact characters, not a resemblance");

            // Order matters more than presence: a class must never beat a name.
            Segment(check, "Row", "unity-list-item", "Label", "Row",
                    "the name wins even when a class looks more descriptive");
        }

        private static void Segment(Action<bool, string, string> check, string name, string css,
                                    string type, string expected, string why)
        {
            string actual = TargetPath.Segment(name, css, type);
            check(actual == expected,
                  $"Segment({Show(name)}, {Show(css)}, {Show(type)}) -> {Show(actual)}", why);
        }

        private static string Show(string value) =>
            value == null ? "(null)" : value.Length == 0 ? "(empty)" : "\"" + value + "\"";
    }
}
