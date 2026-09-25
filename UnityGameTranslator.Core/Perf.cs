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
        private const int SlotCount = 24;

        private static readonly string[] Names =
        {
            "UITK.Scan", "UITK.Element", "RTL.Present", "RTL.Reflow", "Font.Scene", "Font.Clones",
            "UITK.Font", "Font.Find", "UITK.Children", "UITK.Image",
            "Scan.Find", "UITK.Cycle", "UITK.Setter", "Scan.Process", "Scan.Text",
            "Scan.Gate", "Scan.Translate", "Scan.Apply",
            "Setter", "Setter.Note", "Setter.Release", "TMP.Layout", "Reveal", "RenderWatch",
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
            if (dt > _frameMax) _frameMax = dt;
            if (dt > 0.0333f) _framesOver33++;
            else if (dt > 0.0167f) _framesOver16++;
        }

        /// <summary>Timestamp to hand back to <see cref="Stop"/>, or 0 when profiling is off.</summary>
        internal static long Start()
        {
            return TranslatorCore.DebugMode ? Stopwatch.GetTimestamp() : 0L;
        }

        internal static void Stop(int slot, long start)
        {
            if (start == 0L) return;
            long spent = Stopwatch.GetTimestamp() - start;
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
            string frames = $"frames: max {_frameMax * 1000:F1}ms, >33ms: {_framesOver33}, 16-33ms: {_framesOver16}, GC gen0: {gcDelta}";
            _frameMax = 0f; _framesOver16 = 0; _framesOver33 = 0;

            if (sb.Length == 0) { TranslatorCore.LogDebug($"[PASS-PERF] over {window:F1}s | {frames}"); return; }
            TranslatorCore.LogDebug($"[PASS-PERF] over {window:F1}s | {frames} | {sb}");

            SayWhatTheWorstWasAbout();
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
