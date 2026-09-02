using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Runs the mod's pure rules against the answers they are supposed to give.
    ///
    /// Why this exists: everything that decides how a text is routed — is it a typewriter reveal,
    /// is the game assembling a tooltip — was welded to Component, Time.frameCount and
    /// Time.realtimeSinceStartup. Nothing about it could be checked without launching a game, so
    /// every change was validated by looking at ONE game and hoping. The rules that are genuinely
    /// pure move out into files this project links, and become answerable.
    ///
    /// Run with `dotnet run` from this folder; the exit code is what a script should read.
    ///
    /// ⚠ It links source FILES, not the Core assembly — see the csproj for why, and for the alarm
    /// that fires if a linked file stops being pure.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            HowTextChanges();
            WhenTextMayBeTyping();
            HowATargetIsNamed();
            HowAStringIsShaped();

            Console.WriteLine();
            if (_failures == 0)
            {
                Console.WriteLine("All checks passed.");
                return 0;
            }

            Console.WriteLine($"{_failures} check(s) FAILED.");
            return 1;
        }

        /// <summary>What a component's new text is, relative to the one it held a moment ago.</summary>
        private static void HowTextChanges()
        {
            Section("Text relations");
            TextRelationsChecks.Run(Check);
        }

        /// <summary>When a text on screen may be read as an echo of the keyboard.</summary>
        private static void WhenTextMayBeTyping()
        {
            Section("Input echo");
            InputEchoChecks.Run(Check);
        }

        /// <summary>How one step of a hierarchy path is named when the thing has no name.</summary>
        private static void HowATargetIsNamed()
        {
            Section("Target path");
            TargetPathChecks.Run(Check);
        }

        /// <summary>Which strings trigger the presentation pass, and what shaping makes of them.</summary>
        private static void HowAStringIsShaped()
        {
            Section("Text shaping");
            TextShapingChecks.Run(Check);

            Section("Rich text index map (UI.Text line slicing)");
            RichTextIndexMapChecks.Run(Check);

            Section("Indic reordering (pre-base vowel signs)");
            IndicReorderChecks.Run(Check);

            Section("Word breaking (Thai, Lao, Khmer, Myanmar)");
            WordBreakerChecks.Run(Check);

            Section("Bidi conformance (Unicode suite)");
            BidiConformanceChecks.Run(Check);
        }

        private static void Check(bool passed, string what, string why)
        {
            if (!passed) _failures++;
            Console.WriteLine($"  {(passed ? "ok  " : "FAIL")}  {what,-52}  {why}");
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            Console.WriteLine(new string('-', title.Length));
        }
    }
}
