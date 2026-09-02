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
        private const int SlotCount = 6;

        private static readonly string[] Names =
        {
            "UITK.Scan", "UITK.Element", "RTL.Present", "RTL.Reflow", "Font.Scene", "Font.Clones",
        };

        private static readonly long[] _ticks = new long[SlotCount];
        private static readonly int[] _calls = new int[SlotCount];
        private static float _lastReport;

        /// <summary>Timestamp to hand back to <see cref="Stop"/>, or 0 when profiling is off.</summary>
        internal static long Start()
        {
            return TranslatorCore.DebugMode ? Stopwatch.GetTimestamp() : 0L;
        }

        internal static void Stop(int slot, long start)
        {
            if (start == 0L) return;
            _ticks[slot] += Stopwatch.GetTimestamp() - start;
            _calls[slot]++;
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

            var sb = new System.Text.StringBuilder(160);
            double freq = Stopwatch.Frequency;
            for (int i = 0; i < SlotCount; i++)
            {
                if (_calls[i] == 0) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(Names[i]).Append('=').Append((_ticks[i] / freq * 1000).ToString("F1"))
                  .Append("ms/").Append(_calls[i]).Append(" calls");
                _ticks[i] = 0;
                _calls[i] = 0;
            }
            if (sb.Length == 0) return;
            TranslatorCore.LogDebug($"[PASS-PERF] over {window:F1}s | {sb}");
        }
    }
}
