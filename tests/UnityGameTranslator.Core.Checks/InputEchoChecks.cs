using System;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// When a text on screen may be read as an echo of the keyboard.
    ///
    /// ⚠ These cases exist because the rule is easy to "simplify" into something wrong. Either
    /// guard on its own looks sufficient and neither is: drop the cache guard and a real item
    /// stops being translated while its name is typed; drop the window and a search box left
    /// focused makes every matching label suspect for as long as it holds focus.
    /// </summary>
    internal static class InputEchoChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // The case this whole mechanism exists for: a name being entered, echoed in a header.
            Is(check, known: false, since: 0.1f, expected: true,
               "a string we have never seen, right after a keystroke");

            // 🔴 The user's case: "green apple" typed while the game shows an item of that name.
            Is(check, known: true, since: 0.1f, expected: false,
               "a string this game has already shown us is the game's, whoever is typing");

            // The window closes on its own, so a coincidence later is not mistaken for an echo.
            Is(check, known: false, since: InputEcho.WindowSeconds + 0.01f, expected: false,
               "past the window, a match is a coincidence");
            Is(check, known: false, since: InputEcho.WindowSeconds, expected: true,
               "the window is inclusive at its edge");

            // Holding focus without typing must not keep it armed — the sentinel says "no keystroke".
            Is(check, known: false, since: -1f, expected: false,
               "no keystroke to measure from is not the same as a recent one");

            // Both guards, so that removing either one is a visible failure rather than a silent
            // widening of what gets skipped.
            Is(check, known: true, since: -1f, expected: false, "neither guard passes");

            check(InputEcho.WindowSeconds > 0f && InputEcho.WindowSeconds <= 2f,
                  $"WindowSeconds = {InputEcho.WindowSeconds}",
                  "long enough for a burst of typing, short enough not to shadow the game");
        }

        private static void Is(Action<bool, string, string> check, bool known, float since,
                               bool expected, string why)
        {
            bool actual = InputEcho.CouldBeTyping(known, since);
            check(actual == expected,
                  $"CouldBeTyping(known: {known}, since: {since}) -> {actual}", why);
        }
    }
}
