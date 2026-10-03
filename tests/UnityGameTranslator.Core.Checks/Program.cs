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

            // `dotnet run -- replay <text-trace.jsonl> [max-diffs] [--frame N]` — a trace recorded
            // in a game, played through the router: where it answers differently from the
            // recording; `--frame N` prints what the router logs during that frame.
            // `dotnet run -- tocase <text-trace.jsonl> <component> [first frame] [last frame]` —
            // one component's recorded sequence written out as a routing case (TraceToCase).
            // `dotnet run -- lineparts <file>` — where a text (the file's content) is cut into lines
            // by TextRouter.LineParts, to read a recorded text the way the router reads it.
            if (args.Length >= 2 && args[0] == "lineparts")
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                foreach (string part in TextRouter.LineParts(System.IO.File.ReadAllText(args[1])))
                    Console.WriteLine("[" + part.Replace("\r", "\\r").Replace("\n", "\\n") + "]");
                return 0;
            }

            // Rewrites silent-catches.json with what is left — after fixing some, never to raise it.
            if (args.Length >= 1 && args[0] == "silent-baseline")
                return SilentCatchChecks.WriteBaseline();
            if (args.Length >= 1 && args[0] == "silent-list")
                return SilentCatchChecks.List(args.Length >= 2 ? args[1] : null);

            if (args.Length >= 3 && args[0] == "tocase")
                return TraceToCase.Run(args[1], long.Parse(args[2]),
                    args.Length >= 4 ? int.Parse(args[3]) : int.MinValue,
                    args.Length >= 5 ? int.Parse(args[4]) : int.MaxValue);

            if (args.Length >= 2 && args[0] == "replay")
            {
                int frameAt = Array.IndexOf(args, "--frame");
                int debugFrame = frameAt > 0 && frameAt + 1 < args.Length ? int.Parse(args[frameAt + 1]) : int.MinValue;
                int sweepAt = Array.IndexOf(args, "--sweep");
                int sweepEvery = sweepAt > 0 && sweepAt + 1 < args.Length ? int.Parse(args[sweepAt + 1]) : 0;
                int maxDiffs = args.Length >= 3 && !args[2].StartsWith("--") ? int.Parse(args[2]) : 20;
                return TraceReplay.Run(args[1], maxDiffs, debugFrame, sweepEvery);
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
            WhenTheSweepHasBeenRound();
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
            Section("What a text is refused for, at the door and at the store", TextAdmissionChecks.Run);
            Section("The translation queue, across a whole sequence", TranslationQueueChecks.Run);
            Section("One item through the worker, in order", TranslationWorkerChecks.Run);
            Section("A text asked for and never answered", OwedTextChecks.Run);
            Section("A retry counter, and when it is not there", RetryCountChecks.Run);
            Section("A thread the mod starts, on each runtime", WorkerThreadChecks.Run);
        }

        /// <summary>What a config.json written by an older build still means today.</summary>
        private static void WhatAConfigFileStillMeans()
        {
            Section("The config.json contract and its migrations", ModConfigChecks.Run);
            Section("config.json against the spec's cases", ConfigSpecChecks.Run);
            Section("What an Apply has to redraw", ConfigEffectsChecks.Run);
        }

        /// <summary>Reading one server-sent event stream: the grammar, and the loop pulling the lines.</summary>
        private static void HowAnEventStreamIsRead()
        {
            Section("Server-sent events, across a whole stream", SseStreamChecks.Run);
        }

        /// <summary>What a backend is handed, and what is made of what comes back.</summary>
        private static void WhatABackendIsHandedAndGivesBack()
        {
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
            Section("What a translation file states about itself", LoadedFileChecks.Run);
            Section("The translation file against the spec's cases", TranslationFileSpecChecks.Run);
            Section("The site's answers against the API contract's cases", ApiContractChecks.Run);
            Section("The relay's streams against the spec's cases", SseEventsChecks.Run);
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
            Section("The text gate: exact, normalized, trimmed, pattern", TextGateChecks.Run);
            Section("Our own translations coming back, across a sequence", ReadbackIndexChecks.Run);
            Section("An old translation still on screen after a reload", StaleSnapshotChecks.Run);
            Section("JSON on a runtime that refuses to emit methods", JsonRuntimeChecks.Run);
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

        /// <summary>When the sweep has been all the way round every kind of text component.</summary>
        private static void WhenTheSweepHasBeenRound()
        {
            Section("One round of the sweep", ScanRoundChecks.Run);
            Section("A replaced font's shadow and outline, drawn like the game's", DrawnWidthsChecks.Run);
            Section("A typewriter reveal carried over to the translation", RevealScaleChecks.Run);
            Section("A window trembling between two layouts", OscillationChecks.Run);
            Section("What a reveal in flight is told", RevealDoorChecks.Run);
            Section("Texts a game writes, replayed in sequence (routing corpus)", RoutingCorpusChecks.Run);
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
            Section("Translation store (the moments of the file)", TranslationStoreChecks.Run);
            Section("Failed lines (kept to be settled by hand)", FailureLedgerChecks.Run);
            Section("A server error blames the line, or the server", ServerErrorBlameChecks.Run);
            Section("Failed lines beside the file, across a launch", FailureStoreChecks.Run);
            Section("What was learnt about the elements, beside the file, across a launch", ElementStoreChecks.Run);
            Section("Long text in pieces a label can draw", TextChunksChecks.Run);
            Section("Saving (whole or not at all, newest wins)", SavingChecks.Run);
            Section("Transfers (a limit per step, never on the whole)", StallGuardChecks.Run);
            Section("Picking (where a line of sight meets a box)", RayBoxChecks.Run);
        }

        /// <summary>The frontier: a panel names nothing of the engine, a component lets no engine type through.</summary>
        private static void WhereThePanelsStop()
        {
            Section("UI frontier (panels hold handles only)", UiBoundaryChecks.Run);
            Section("Screen router (which screen is up after which act)", ScreenRouterChecks.Run);
            Section("Engine frontier (the engine names nothing of the interface)", EngineFrontierChecks.Run);
            Section("Silent catches (a ratchet down to none)", SilentCatchChecks.Run);
            Section("Font atlases filled through FontAtlas only", FontAtlasChecks.Run);
            Section("No character decision written for one script", CharacterRangeChecks.Run);
            Section("Underlines and strikethroughs over their letters", DecorationSpansChecks.Run);
            Section("A font's own lines (cap, mean, strikeout, underline)", FontLinesChecks.Run);
            Section("The family and style a font is known by", FontNamesChecks.Run);
            Section("No diagnostic capped by a count", DiagnosticCapChecks.Run);
            Section("Reflection lookups that answer instead of throwing", MembersChecks.Run);
            Section("Screens in data (the documents and the vocabulary)", ScreenDocumentChecks.Run);
        }

        /// <summary>Which strings trigger the presentation pass, and what shaping makes of them.</summary>
        private static void HowAStringIsShaped()
        {
            Section("Text shaping", TextShapingChecks.Run);
            Section("Right-to-left text wrapped by TMP (runs of Latin words across a line end)", RtlWrapChecks.Run);

            Section("Right-to-left text in an input field (caret, clicks, arrows)", RtlFieldChecks.Run);
            Section("Shaped text in an input field (units, caret, clicks, arrows)", SyllabicFieldChecks.Run);

            Section("Rich text index map (UI.Text line slicing)", RichTextIndexMapChecks.Run);

            Section("Indic reordering (pre-base vowel signs)", IndicReorderChecks.Run);

            Section("Word breaking (Thai, Lao, Khmer, Myanmar)", WordBreakerChecks.Run);

            Section("Bidi conformance (Unicode suite)", BidiConformanceChecks.Run);
            Section("Line break conformance (Unicode suite)", LineBreakConformanceChecks.Run);
            Section("A late translation wrapped the way the game wraps", LineFitChecks.Run);

            Section("OpenType layout (GSUB/GPOS/GDEF on a real font)", OpenTypeLayoutChecks.Run);

            Section("Derived font (added glyphs vs the source font)", DerivedFontChecks.Run);

            Section("Font pool (names listed at start, filled during the session)", FontPoolChecks.Run);
            Section("Atlas sampling (the size Auto draws a font at)", AtlasSamplingChecks.Run);

            Section("Derived font pipeline (shape, name, write, draw — against HarfBuzz)", DerivedPipelineChecks.Run);

            Section("Shaping coverage (engine × font origin, from the mod's own decision)", ShapingCoverageChecks.Run);
            Section("Shaper stages (a GSUB feature runs once, at its earliest stage)", ShaperStagesChecks.Run);
            Section("Font coverage (what each font drew, what its font lacks)", FontCoverageChecks.Run);

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
