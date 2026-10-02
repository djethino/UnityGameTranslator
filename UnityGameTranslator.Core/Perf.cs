using System.Diagnostics;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Where the mod's frame time goes, measured rather than guessed.
    ///
    /// 🔴 **Why it exists.** Two probes were already here — `[PERF]` around the TMP/UI.Text setter
    /// and `[SCAN-PERF]` around the component scan — and everything added since (UI Toolkit's own
    /// pass, the RTL presentation, the periodic font application) was invisible. Diagnosing a
    /// stutter then means proposing hypotheses one after another, which is exactly what happened
    /// on 2026-09-02. Same activation as those two (`debug` in config.json), same cadence (a line
    /// every 5 s), so a session that had one now has all of them.
    ///
    /// ⚠ **Costs nothing when off**: one static bool test per call site, no allocation ever —
    /// fixed arrays indexed by a constant, no dictionary, no string built until the report. The
    /// report itself is emitted from the scanner's single tick, not from a timer of its own.
    ///
    /// ⚠ Deliberately NOT a replacement for the two older probes: they measure the INSIDE of one
    /// call (which of the five phases of a setter cost what), this measures whole passes. Merging
    /// them would rewrite working code for tidiness — noted in TODO instead.
    /// </summary>
    internal static class Perf
    {
        // One slot per measured pass. Adding one means adding its name below, nothing else.
        internal const int UitkScan = 0;         // UIToolkitSupport.Scan — the whole pass
        internal const int UitkElement = 1;      // ...of which: routing + presenting one element
        internal const int RtlPresent = 2;       // RtlPresenter.Present, every engine
        internal const int RtlReflow = 3;        // RtlPresenter.ProcessPendingReflows
        internal const int FontScene = 4;        // FontManager.ApplyReplacementsToScene
        internal const int FontClones = 5;       // FontManager.ApplyUnityClonesToScene
        internal const int UitkFont = 6;         // ...of which: the per-element font question
        internal const int FontFind = 7;         // ...of which: the scene lookup those two make
        internal const int UitkChildren = 8;     // ...of which: reading an element's children (reflection)
        internal const int UitkImage = 9;        // ...of which: the per-element picture question
        internal const int ScanFind = 10;        // the scanner's per-type scene lookup (atomic engine call)
        internal const int UitkCycle = 11;       // UI Toolkit: sweep + document lookup opening a walk cycle
        internal const int UitkSetter = 12;      // the whole TextElement.set_text prefix (route + present)

        // 🔴 The batch phase is measured as a whole and NOTHING inside it is. Measured on a real
        // game (2026-09-09): 861 ms of batch per 5 s window on average, 3 959 ms at worst, with
        // single frames at 3.6 s — and no counter able to say whether that is one component or a
        // thousand. These two answer that first question; without it every explanation is a guess.
        internal const int ScanProcess = 13;     // one component, all of ProcessComponentForType
        internal const int ScanText = 14;        // ...of which: reading its text (interop on IL2CPP)

        // 🔴 ...and of which WHAT. Measured on a real game: one call at 473 ms out of 690 ms spent
        // across 109 126 calls — one component is seventy per cent of the whole phase, on texts as
        // plain as "+999" and "Clear Selection". So it is neither the text nor its reading; these
        // three cut the rest of the call in the three things it actually does.
        internal const int ScanGate = 15;        // ...of which: the questions asked before translating
        internal const int ScanTranslate = 16;   // ...of which: looking the text up and queueing it
        internal const int ScanApply = 17;       // ...of which: writing the answer onto the component

        // 🔴 The setters and the per-layout hook, measured (2026-09-25): two games dropped to 5 and
        // 16 frames a second after a day of additions that each run on EVERY text write or EVERY
        // mesh build, while every pass above stayed small — so the time was in code nothing timed.
        internal const int Setter = 18;          // the whole TMP/UI.Text/TextMesh setter prefix
        internal const int SetterNote = 19;      // ...of which: recording the text system (texts-seen)
        internal const int SetterRelease = 20;   // ...of which: handing a right-to-left state back
        internal const int TmpLayout = 21;       // the GenerateTextMesh postfix (input fields, probe)
        internal const int Reveal = 22;          // the maxVisibleCharacters setter prefix (RevealScale)
        internal const int RenderWatch = 23;     // TranslatorScanner.TickRenderWatch, every frame before the draw

        // Every engine lookup, whoever asks (scanner, fonts, images, variables, inspector) — the
        // scanner's own slot above covers one caller, and a lookup of every component or every
        // MonoBehaviour elsewhere is an atomic call no per-frame budget can split.
        internal const int FindAll = 24;         // TypeHelper.FindAllObjectsOfType, the whole call
        internal const int SceneRead = 25;       // reading which scene an object is in, once per object (TypeHelper.IsInScene)
        // Every uGUI graphic the game enables passes through the Graphic.OnEnable postfix (and every
        // TMP text through its own): a panel opening enables hundreds at once, in the same frame.
        internal const int TextEnable = 26;      // TranslatorPatches.Graphic_OnEnable_Postfix + TMPText_OnEnable_Postfix
        // The rest of a scanned component's call, which the four slots above left out: a single
        // call of 82 ms on a game, of which those four held 13 (2026-10-02).
        internal const int ScanInput = 27;       // ...of which: is it an input field's text, or an echo of typing
        internal const int ScanSeen = 28;        // ...of which: was this text seen on it already, is it the mod's own
        private const int SlotCount = 29;

        private static readonly string[] Names =
        {
            "UITK.Scan", "UITK.Element", "RTL.Present", "RTL.Reflow", "Font.Scene", "Font.Clones",
            "UITK.Font", "Font.Find", "UITK.Children", "UITK.Image",
            "Scan.Find", "UITK.Cycle", "UITK.Setter", "Scan.Process", "Scan.Text",
            "Scan.Gate", "Scan.Translate", "Scan.Apply",
            "Setter", "Setter.Note", "Setter.Release", "TMP.Layout", "Reveal", "RenderWatch",
            "Find.All", "Scene.Read", "Text.OnEnable", "Scan.Input", "Scan.Seen",
        };

        private static readonly long[] _ticks = new long[SlotCount];
        private static readonly int[] _calls = new int[SlotCount];
        private static readonly long[] _max = new long[SlotCount];   // the worst single call — a stutter is a peak, not an average
        private static float _lastReport;

        // The frame itself, so a stutter is SEEN rather than inferred: worst frame in the
        // window, and how many crossed 16 ms (one frame at 60 fps) and 33 ms (two).
        private static float _frameMax;
        private static int _framesOver16, _framesOver33;
        private static int _gcAtLastReport = -1;

        /// <summary>Called once per frame from the tick with the frame's delta time.</summary>
        internal static void Frame(float dt)
        {
            if (!TranslatorCore.DebugMode) return;
            HookAtlasRebuilds();
            if (_rebuiltThisFrame)
            {
                // The rebuild happened while the frame now measured was drawn.
                if (dt > 0.0333f) _rebuildsInSlowFrames++;
                _rebuiltThisFrame = false;
            }
            long heap = GameHeapUsed();
            if (heap >= 0)
            {
                // Between two collections the used size only grows: a drop is a collection, made
                // while the frame now measured ran.
                if (_gameHeapLast >= 0 && heap < _gameHeapLast)
                {
                    _gameGcs++;
                    if (dt > 0.0333f) _gameGcsInSlowFrames++;
                }
                _gameHeapLast = heap;
            }
            if (dt > _frameMax)
            {
                _frameMax = dt;
                _worstFrameModTicks = _frameModTicks;
            }
            if (dt > 0.0333f) _framesOver33++;
            else if (dt > 0.0167f) _framesOver16++;

            // A frame's calls all end within it: whatever depth is left is a Start with no Stop
            // (an early return), which must not stop the next frame from counting.
            _frameModTicks = 0;
            _depth = 0;
        }

        // 🔴 **Font atlas rebuilds — engine work no slot can time.** A dynamic font draws from a
        // texture holding only the glyphs asked so far; when a text needs ones that do not fit, the
        // engine rebuilds the whole texture while drawing — every glyph in it rasterized again,
        // thousands for a CJK font. It happens in the engine's render, after every hook of ours has
        // returned, so a frame frozen by it reads "timed mod work ~0 ms" (a game whose tooltips
        // froze the first time each was shown, translated, 2026-10-02). Counted here, with the
        // fonts, and how many fell in a frame over 33 ms. Debug only, like the rest.
        private static object _rebuiltHandler;
        private static bool _rebuiltHooked, _rebuiltThisFrame;
        private static int _rebuilds, _rebuildsInSlowFrames;
        private static readonly System.Collections.Generic.Dictionary<string, int> _rebuiltFonts =
            new System.Collections.Generic.Dictionary<string, int>();

        private static void HookAtlasRebuilds()
        {
            if (_rebuiltHooked) return;
            _rebuiltHooked = true;
            try
            {
                _rebuiltHandler = EngineEvents.Add(typeof(UnityEngine.Font), "textureRebuilt", (System.Action<UnityEngine.Font>)OnAtlasRebuilt);
                if (_rebuiltHandler == null) TranslatorCore.LogDebug("[PASS-PERF] Font.textureRebuilt not found — atlas rebuilds are not counted");
            }
            catch (System.Exception ex) { Faults.Say("Perf.HookAtlasRebuilds", ex); }
        }

        private static void OnAtlasRebuilt(UnityEngine.Font font)
        {
            _rebuilds++;
            _rebuiltThisFrame = true;
            string name = font != null ? font.name : "?";
            _rebuiltFonts.TryGetValue(name, out int n);
            _rebuiltFonts[name] = n + 1;
        }

        // 🔴 **The GAME's garbage collections (IL2CPP).** The "GC gen0" of the report counts the
        // runtime the mod runs in; under IL2CPP the game's objects live in another heap, collected
        // by its own collector, which stops everything and walks the whole heap — hundreds of
        // milliseconds on a game holding several hundred thousand objects, again in the middle of
        // a frame no hook of ours can time. Seen from the runtime's own export, the heap's used size
        // (il2cpp_gc_get_used_size, through Il2CppInterop): it only grows between two collections,
        // so a drop from one frame to the next is one. The game's GC.CollectionCount was the first
        // choice — and stripped from the bench's build. Absent on Mono, where the mod and the game
        // share one heap and "GC gen0" already is the game's.
        private static bool _gameHeapResolved;
        private static System.Reflection.MethodInfo _gameHeapUsed;
        private static long _gameHeapLast = -1;
        private static int _gameGcs, _gameGcsInSlowFrames;

        private static long GameHeapUsed()
        {
            if (!_gameHeapResolved)
            {
                _gameHeapResolved = true;
                if (TranslatorCore.Adapter == null || !TranslatorCore.Adapter.IsIL2CPP) return -1;
                var il2cpp = AssemblyTypes.Find("Il2CppInterop.Runtime.IL2CPP");
                _gameHeapUsed = il2cpp?.GetMethod("il2cpp_gc_get_used_size", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (_gameHeapUsed == null) TranslatorCore.LogDebug("[PASS-PERF] il2cpp_gc_get_used_size not found — the game's collections are not counted");
            }
            if (_gameHeapUsed == null) return -1;
            try { return System.Convert.ToInt64(_gameHeapUsed.Invoke(null, null)); }
            catch (System.Exception ex)
            {
                _gameHeapUsed = null;
                Faults.Say("Perf.GameHeapUsed", ex);
                return -1;
            }
        }

        /// <summary>Unsubscribes the atlas-rebuild count (shutdown).</summary>
        internal static void Shutdown()
        {
            if (_rebuiltHandler == null) return;
            EngineEvents.Remove(typeof(UnityEngine.Font), "textureRebuilt", _rebuiltHandler);
            _rebuiltHandler = null;
        }

        // 🔴 **The mod's own share of a slow frame.** Every slot says its longest single call, and
        // none says how much of the worst FRAME was ours: a 130 ms frame could be one 12 ms call and
        // 118 ms of the game, or a thousand small calls of ours — the one question that decides
        // whether there is anything to fix. Timed calls nest (a lookup inside a scan, a presenting
        // inside a setter), so only the outermost one of a nest is added.
        private static int _depth;
        private static long _frameModTicks, _worstFrameModTicks;

        private static void Leave(long spent)
        {
            if (--_depth > 0) return;
            _depth = 0;
            _frameModTicks += spent;
        }

        /// <summary>Timestamp to hand back to <see cref="Stop"/>, or 0 when profiling is off.</summary>
        internal static long Start()
        {
            if (!TranslatorCore.DebugMode) return 0L;
            _depth++;
            return Stopwatch.GetTimestamp();
        }

        internal static void Stop(int slot, long start)
        {
            if (start == 0L) return;
            long spent = Stopwatch.GetTimestamp() - start;
            Leave(spent);
            _ticks[slot] += spent;
            _calls[slot]++;
            if (spent > _max[slot]) _max[slot] = spent;
        }

        /// <summary>
        /// The subject of the worst <see cref="ScanProcess"/> call of the window, kept as a
        /// reference rather than described.
        ///
        /// 🔴 **A number alone cannot be acted on.** "max 3597 ms" says one component cost three
        /// and a half seconds and nothing about WHICH — so the next step is another session and
        /// another guess. This carries the object itself and describes it once, when the window is
        /// reported: no string is built on the path that is being measured, and nothing is
        /// allocated unless a call becomes the worst.
        ///
        /// ⚠ Only ever touched inside the report, in a try/catch: a component can be destroyed
        /// between the call and the report, and reading a destroyed one throws on IL2CPP.
        /// </summary>
        private static object _worstProcessed;

        // The slowest engine lookup of the window: who asked, for what, and how much came back.
        // A lookup is atomic — no frame budget can split it — so the only cures are asking for less
        // or asking less often, and both start from knowing which caller it is.
        private static string _worstFindBy, _worstFindType;
        private static int _worstFindCount;

        /// <summary>Stop the <see cref="FindAll"/> slot, remembering the slowest lookup's caller.</summary>
        internal static void StopFind(long start, string by, System.Type type, int found)
        {
            if (start == 0L) return;
            long spent = Stopwatch.GetTimestamp() - start;
            Leave(spent);
            _ticks[FindAll] += spent;
            _calls[FindAll]++;
            if (spent <= _max[FindAll]) return;

            _max[FindAll] = spent;
            _worstFindBy = by;
            _worstFindType = type?.Name;
            _worstFindCount = found;
        }

        /// <summary>
        /// Stop the per-component slot, remembering what the worst call was about.
        ///
        /// ⚠ Separate from <see cref="Stop"/> rather than an optional argument, so no other slot
        /// pays for a reference it never reads.
        /// </summary>
        internal static void StopProcessed(long start, object subject)
        {
            if (start == 0L) return;
            long spent = Stopwatch.GetTimestamp() - start;
            Leave(spent);
            _ticks[ScanProcess] += spent;
            _calls[ScanProcess]++;
            if (spent <= _max[ScanProcess]) return;

            _max[ScanProcess] = spent;
            _worstProcessed = subject;
        }

        /// <summary>
        /// Called from the scanner's tick. Prints the slots that saw work in the window and
        /// clears them — a silent slot is a pass that did not run, which is itself an answer.
        /// </summary>
        internal static void ReportIfDue(float now)
        {
            if (!TranslatorCore.DebugMode) return;
            if (_lastReport == 0f) { _lastReport = now; return; }
            if (now - _lastReport < 5f) return;
            float window = now - _lastReport;
            _lastReport = now;

            var sb = new System.Text.StringBuilder(240);
            double freq = Stopwatch.Frequency;
            for (int i = 0; i < SlotCount; i++)
            {
                if (_calls[i] == 0) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(Names[i]).Append('=').Append((_ticks[i] / freq * 1000).ToString("F1"))
                  .Append("ms/").Append(_calls[i]).Append(" calls max ")
                  .Append((_max[i] / freq * 1000).ToString("F1")).Append("ms");
                _ticks[i] = 0;
                _calls[i] = 0;
                _max[i] = 0;
            }

            int gcNow = System.GC.CollectionCount(0);
            int gcDelta = _gcAtLastReport < 0 ? 0 : gcNow - _gcAtLastReport;
            _gcAtLastReport = gcNow;
            string frames = $"frames: max {_frameMax * 1000:F1}ms (timed mod work in it {_worstFrameModTicks * 1000.0 / Stopwatch.Frequency:F1}ms), "
                            + $">33ms: {_framesOver33}, 16-33ms: {_framesOver16}, GC gen0: {gcDelta}";
            _frameMax = 0f; _framesOver16 = 0; _framesOver33 = 0; _worstFrameModTicks = 0;
            if (_rebuilds > 0)
            {
                var fonts = new System.Text.StringBuilder();
                foreach (var kv in _rebuiltFonts)
                    fonts.Append(fonts.Length == 0 ? "" : ", ").Append(kv.Key).Append(" x").Append(kv.Value);
                frames += $", font atlas rebuilds: {_rebuilds} ({fonts}), in frames over 33ms: {_rebuildsInSlowFrames}";
                _rebuilds = 0; _rebuildsInSlowFrames = 0; _rebuiltFonts.Clear();
            }
            if (_gameHeapUsed != null)
            {
                frames += $", game GC: {_gameGcs}, in frames over 33ms: {_gameGcsInSlowFrames}";
                _gameGcs = 0; _gameGcsInSlowFrames = 0;
            }

            if (sb.Length == 0) { TranslatorCore.LogDebug($"[PASS-PERF] over {window:F1}s | {frames}"); return; }
            TranslatorCore.LogDebug($"[PASS-PERF] over {window:F1}s | {frames} | {sb}");

            SayWhatTheWorstWasAbout();
            if (_worstFindBy != null)
            {
                TranslatorCore.LogDebug($"[PASS-PERF] the slowest lookup was {_worstFindType} for {_worstFindBy} ({_worstFindCount} found)");
                _worstFindBy = null;
            }
        }

        /// <summary>
        /// Name the component the worst per-component call of the window was spent on.
        ///
        /// ⚠ Its own line rather than inside the report: it is only there when there was a worst
        /// one to name, and a report that grows a field on some windows and not others is harder
        /// to read across a session than two lines.
        /// </summary>
        private static void SayWhatTheWorstWasAbout()
        {
            var subject = _worstProcessed;
            _worstProcessed = null;
            if (subject == null) return;

            try
            {
                var comp = subject as UnityEngine.Component;
                if (comp == null) return;

                string text = TypeHelper.GetText(subject) ?? "";
                if (text.Length > 40) text = text.Substring(0, 40) + "…";

                TranslatorCore.LogDebug($"[PASS-PERF] the worst one was '{comp.gameObject.name}' "
                                        + $"({subject.GetType().Name}) showing '{text}'");
            }
            catch (System.Exception e)
            {
                // Destroyed between the call and this line, which is ordinary — and saying so is
                // itself an answer: a component that dies mid-pass is worth knowing about.
                TranslatorCore.LogDebug($"[PASS-PERF] the worst one could not be named: {e.Message}");
            }
        }
    }
}
