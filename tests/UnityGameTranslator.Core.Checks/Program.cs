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

        private static int Main(string[] args)
        {
            // `dotnet run -- shape <font.ttf> <word>...` — print what the shapers make of
            // each word with that font, in the same notation as the HarfBuzz oracle used for
            // IndicShaperChecks (glyph@cluster(advance,xOffset,yOffset)), so the two can be
            // diffed over any word list and any font without editing a check.
            if (args.Length >= 2 && args[0] == "shape")
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                if (Environment.GetEnvironmentVariable("UGT_SHAPE_TRACE") == "1")
                    TextShaping.ShapingCommon.Trace = line => Console.Error.WriteLine(line);
                var font = new TextShaping.TtfShapingFont(new Rasterizer.TtfParser(System.IO.File.ReadAllBytes(args[1])));
                for (int i = 2; i < args.Length; i++)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var g in TextShaping.OpenTypeShaping.Shape(args[i], font))
                        parts.Add($"{g.Glyph}@{g.Cluster}({g.XAdvance},{g.XOffset},{g.YOffset})");
                    Console.WriteLine(args[i] + "\t" + string.Join(" ", parts));
                }
                return 0;
            }

            HowATextIsNormalized();
            HowTextChanges();
            WhenTextMayBeTyping();
            HowATargetIsNamed();
            WhereTheModsInterfaceGoes();
            WhatSitsBesideTheTranslation();
            WhereThePanelsStop();
            HowAStringIsShaped();
            WhatADownloadedFileMayAskFor();

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
        /// <summary>What a text looks like once its decoration is set aside — slots, tags, letters.</summary>
        private static void HowATextIsNormalized()
        {
            Section("Text normalization");
            TextNormalizationChecks.Run(Check);
        }

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

        /// <summary>
        /// What a translation downloaded from another player may make the mod do on this machine:
        /// which names it may turn into files, and how long one of its patterns may run.
        /// </summary>
        private static void WhatADownloadedFileMayAskFor()
        {
            Section("Plain file names");
            PlainFileNameChecks.Run(Check);

            Section("Text rules under a budget");
            TextRuleChecks.Run(Check);
        }

        /// <summary>Which interface lines leave a game translation, and which the hash still counts.</summary>
        private static void WhereTheModsInterfaceGoes()
        {
            // ⚠ Two rules left with their cases. "The language of a translation" went to the socle
            // on 2026-09-05 (TranslationLanguages); ModUiMigration followed on 2026-09-08, and its
            // cases are corpus/rules/mod_ui_migration.json. Both run in Common.Checks now.

            Section("The interface file, across a whole sequence");
            ModUiStoreChecks.Run(Check);
        }

        /// <summary>The ancestors a fork drops and the images a backup carries — on real files, by their real names.</summary>
        private static void WhatSitsBesideTheTranslation()
        {
            Section("Companion files (ancestors, images)");
            CompanionFilesChecks.Run(Check);
        }

        /// <summary>The frontier: a panel names nothing of the engine, a component lets no engine type through.</summary>
        private static void WhereThePanelsStop()
        {
            Section("UI frontier (panels hold handles only)");
            UiBoundaryChecks.Run(Check);
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

            Section("OpenType layout (GSUB/GPOS/GDEF on a real font)");
            OpenTypeLayoutChecks.Run(Check);

            Section("Indic shaper (against HarfBuzz, word by word)");
            IndicShaperChecks.Run(Check);

            Section("OpenType text (runs and glyph naming)");
            OpenTypeTextChecks.Run(Check);
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
