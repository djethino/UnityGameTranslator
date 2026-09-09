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
            WhatAPatternCovers();
            HowANumberedSentenceIsRecognised();
            WhatSomebodyAskedToLeaveAlone();
            WhichFontARuleAsksFor();
            WhichLanguagesATranslationIsIn();
            WhatWaitsForABackend();
            WhatAConfigFileStillMeans();
            HowAnEventStreamIsRead();
            WhatABackendIsHandedAndGivesBack();
            WhatATranslationFileYields();
            WhatALoadedFileSaysAboutItself();
            HowContentIsFingerprinted();
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

        /// <summary>What a text looks like once its decoration is set aside — slots, tags, letters.</summary>
        private static void HowATextIsNormalized()
        {
            Section("Text normalization", TextNormalizationChecks.Run);
        }

        /// <summary>Which paths a written pattern covers — for an exclusion or for a font rule.</summary>
        private static void WhatAPatternCovers()
        {
            Section("Exclusion patterns", ExclusionPatternChecks.Run);
        }

        /// <summary>What somebody's exclusion patterns keep out, across a whole sequence.</summary>
        private static void WhatSomebodyAskedToLeaveAlone()
        {
            Section("Exclusion rules, across a whole sequence", ExclusionRulesChecks.Run);
        }

        /// <summary>Which font rule applies to a label, and what survives being decided.</summary>
        private static void WhichFontARuleAsksFor()
        {
            Section("Font rules, across a whole sequence", FontRulesChecks.Run);
        }

        /// <summary>Which languages a translation is in, across the launch that decides it.</summary>
        private static void WhichLanguagesATranslationIsIn()
        {
            Section("Languages, across a whole launch", LanguageStateChecks.Run);
        }

        /// <summary>The texts waiting for a backend, across the sequence that empties them.</summary>
        private static void WhatWaitsForABackend()
        {
            Section("The translation queue, across a whole sequence", TranslationQueueChecks.Run);
            Section("A thread the mod starts, on each runtime", WorkerThreadChecks.Run);
        }

        /// <summary>What a config.json written by an older build still means today.</summary>
        private static void WhatAConfigFileStillMeans()
        {
            Section("The config.json contract and its migrations", ModConfigChecks.Run);
        }

        /// <summary>Reading one server-sent event stream: the grammar, and the loop pulling the lines.</summary>
        private static void HowAnEventStreamIsRead()
        {
            Section("Server-sent events, across a whole stream", SseStreamChecks.Run);
        }

        /// <summary>What a backend is handed, and what is made of what comes back.</summary>
        private static void WhatABackendIsHandedAndGivesBack()
        {
            Section("Backends: taking a text apart and putting the answer back", BackendsChecks.Run);
        }

        /// <summary>What a translations.json yields, and what reading it says about the file.</summary>
        private static void WhatATranslationFileYields()
        {
            Section("Reading a translation file", TranslationFileEntriesChecks.Run);
        }

        /// <summary>Writing JSON the same way every time, so the same content fingerprints the same.</summary>
        private static void HowContentIsFingerprinted()
        {
            Section("Canonical JSON (the fork fingerprint)", CanonicalJsonChecks.Run);
        }

        /// <summary>What a file says about ITSELF is re-derived from it, never inherited.</summary>
        private static void WhatALoadedFileSaysAboutItself()
        {
            Section("A translation's own identity, on loading", LoadedIdentityChecks.Run);
            Section("Everything a reload has to re-run", ReloadChainChecks.Run);
            Section("Who the server was asked as", AccountVerdictChecks.Run);
            Section("Which publishing act a screen offers", UploadOfferChecks.Run);
            Section("One comparison, one way out, on every screen", ComparisonDoorChecks.Run);
            Section("A card that follows the translation growing under it", LiveCountChecks.Run);
        }

        /// <summary>How a sentence carrying live numbers is read as the pattern it was cached from.</summary>
        private static void HowANumberedSentenceIsRecognised()
        {
            Section("Number patterns", NumberPatternChecks.Run);
        }

        /// <summary>What a component's new text is, relative to the one it held a moment ago.</summary>
        private static void HowTextChanges()
        {
            Section("Text relations", TextRelationsChecks.Run);
        }

        /// <summary>When a text on screen may be read as an echo of the keyboard.</summary>
        private static void WhenTextMayBeTyping()
        {
            Section("Input echo", InputEchoChecks.Run);
        }

        /// <summary>How one step of a hierarchy path is named when the thing has no name.</summary>
        private static void HowATargetIsNamed()
        {
            Section("Target path", TargetPathChecks.Run);
        }

        /// <summary>
        /// What a translation downloaded from another player may make the mod do on this machine:
        /// which names it may turn into files, and how long one of its patterns may run.
        /// </summary>
        private static void WhatADownloadedFileMayAskFor()
        {
            Section("Plain file names", PlainFileNameChecks.Run);

            Section("Text rules under a budget", TextRuleChecks.Run);
        }

        /// <summary>Which interface lines leave a game translation, and which the hash still counts.</summary>
        private static void WhereTheModsInterfaceGoes()
        {
            // ⚠ Two rules left with their cases. "The language of a translation" went to the socle
            // on 2026-09-05 (TranslationLanguages); ModUiMigration followed on 2026-09-08, and its
            // cases are corpus/rules/mod_ui_migration.json. Both run in Common.Checks now.

            Section("The interface file, across a whole sequence", ModUiStoreChecks.Run);
        }

        /// <summary>The ancestors a fork drops and the images a backup carries — on real files, by their real names.</summary>
        private static void WhatSitsBesideTheTranslation()
        {
            Section("Companion files (ancestors, images)", CompanionFilesChecks.Run);
        }

        /// <summary>The frontier: a panel names nothing of the engine, a component lets no engine type through.</summary>
        private static void WhereThePanelsStop()
        {
            Section("UI frontier (panels hold handles only)", UiBoundaryChecks.Run);
        }

        /// <summary>Which strings trigger the presentation pass, and what shaping makes of them.</summary>
        private static void HowAStringIsShaped()
        {
            Section("Text shaping", TextShapingChecks.Run);

            Section("Rich text index map (UI.Text line slicing)", RichTextIndexMapChecks.Run);

            Section("Indic reordering (pre-base vowel signs)", IndicReorderChecks.Run);

            Section("Word breaking (Thai, Lao, Khmer, Myanmar)", WordBreakerChecks.Run);

            Section("Bidi conformance (Unicode suite)", BidiConformanceChecks.Run);

            Section("OpenType layout (GSUB/GPOS/GDEF on a real font)", OpenTypeLayoutChecks.Run);

            Section("Indic shaper (against HarfBuzz, word by word)", IndicShaperChecks.Run);

            Section("OpenType text (runs and glyph naming)", OpenTypeTextChecks.Run);
        }

        private static void Check(bool passed, string what, string why)
        {
            if (!passed) _failures++;
            Console.WriteLine($"  {(passed ? "ok  " : "FAIL")}  {what,-52}  {why}");
        }

        /// <summary>
        /// Print a section heading and run its cases.
        ///
        /// 🔴 **The catch is not defensive, it is the alarm working.** A case that throws ends the
        /// method it is in, so every case after it goes UNRUN — and the output looks merely
        /// shorter, which is indistinguishable from a section that was always that long. That cost
        /// a wrong conclusion twice on 2026-09-08: three deliberately broken rules showed one red,
        /// which reads exactly like two cases that prove nothing. They had simply never run.
        ///
        /// ⚠ So the throw is reported as a FAILED case, named, with what is lost said out loud.
        /// Nothing is swallowed and the exit code still turns non-zero — see
        /// analyse/pieges-projet.md §9.
        /// </summary>
        private static void Section(string title, Action<Action<bool, string, string>> run)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            Console.WriteLine(new string('-', title.Length));

            try
            {
                run(Check);
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"  FAIL  {"the section stopped here",-52}  "
                                  + $"{ex.GetType().Name}: {ex.Message} — every case below it went UNRUN");
            }
        }
    }
}
