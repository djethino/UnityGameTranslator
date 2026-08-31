using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Stage D, the engine-facing end of the pipeline: called at the END of every text SETTER
    /// prefix — after routing, translation and all bookkeeping, which stay 100 % logical — to
    /// turn an outgoing logical string into what the component must display. Getters are
    /// deliberately not covered: they hand text back to the GAME'S code, not to a screen.
    ///
    /// Engine decision, probed per type and cached: a component exposing
    /// <c>isRightToLeftText</c> (TMP, TMProOld — bench-proven) gets the flagged form and the
    /// flag; anything else gets the visual-order form (correct single-line; multi-line is the
    /// generator-readback lot, not this one).
    ///
    /// 🔴 Every composed string is registered as our own output before it leaves
    /// (<see cref="TranslatorCore.RegisterPresentedText"/>): the scanner and every gate then
    /// refuse to learn from it — D8, nothing shaped ever reaches the AI queue, the cache, the
    /// file or the server.
    /// </summary>
    internal static class RtlPresenter
    {
        // isRightToLeftText per concrete component type — reflection once per type, not per call.
        private static readonly Dictionary<Type, PropertyInfo> _rtlProps = new Dictionary<Type, PropertyInfo>();

        // Components whose flag we set, with the value they had before: a reused component that
        // moves on to non-RTL text gets its own state back, not our leftovers.
        private static readonly Dictionary<long, bool> _flaggedOriginal = new Dictionary<long, bool>();

        private static int _logBudget = 5;

        /// <summary>
        /// Present one outgoing string in place. Cheap for the overwhelming majority of texts:
        /// one range scan says "nothing to do".
        /// </summary>
        internal static void Present(object instance, long compId, ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            // The composer and shaper sit on shared buffers — off the main thread, leave the
            // logical text alone rather than corrupt another call's.
            if (!TranslatorCore.IsMainThread) return;

            try
            {
                var prop = RtlProp(instance);

                if (!RtlText.NeedsPresentation(value))
                {
                    // Leaving RTL: a component we flagged earlier gets its original value back.
                    if (prop != null && compId != -1 && _flaggedOriginal.TryGetValue(compId, out bool original))
                    {
                        _flaggedOriginal.Remove(compId);
                        try { prop.SetValue(instance, original, null); } catch { }
                    }
                    return;
                }

                string composed = RtlComposer.Compose(value,
                    prop != null ? RtlOutput.RtlFlagged : RtlOutput.VisualOrder);

                if (prop != null && compId != -1)
                {
                    if (!_flaggedOriginal.ContainsKey(compId))
                    {
                        bool original = false;
                        try { original = prop.GetMethod != null && (bool)prop.GetValue(instance, null); } catch { }
                        _flaggedOriginal[compId] = original;
                    }
                    try { prop.SetValue(instance, true, null); } catch { }
                }

                TranslatorCore.RegisterPresentedText(composed, value);

                if (_logBudget > 0)
                {
                    _logBudget--;
                    TranslatorCore.LogDebug($"[RtlPresenter] comp={compId} mode={(prop != null ? "flagged" : "visual")} " +
                        $"'{(value.Length > 30 ? value.Substring(0, 30) + "…" : value)}' → shaped ({composed.Length} ch)");
                }

                value = composed;
            }
            catch (Exception ex)
            {
                // A failure here must never cost the translation itself: the logical text shows
                // broken (isolated letters) exactly as before this pipeline existed.
                TranslatorCore.LogWarning($"[RtlPresenter] compose failed, showing logical text: {ex.Message}");
            }
        }

        private static PropertyInfo RtlProp(object instance)
        {
            if (instance == null) return null;
            var type = instance.GetType();
            if (_rtlProps.TryGetValue(type, out var cached)) return cached;
            PropertyInfo prop = null;
            try { prop = type.GetProperty("isRightToLeftText", BindingFlags.Public | BindingFlags.Instance); }
            catch { }
            if (prop?.SetMethod == null) prop = null;
            _rtlProps[type] = prop;
            return prop;
        }
    }
}
