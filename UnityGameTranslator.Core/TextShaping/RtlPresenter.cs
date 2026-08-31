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
    /// Engine decision, probed per type and cached:
    /// - <c>isRightToLeftText</c> present (TMP, TMProOld — bench-proven): flagged form + the
    ///   flag, original value restored when the text leaves RTL;
    /// - TextMesh (never auto-wraps): visual order per explicit line, immediately;
    /// - UI.Text: the two-pass emission — the SHAPED LOGICAL string is assigned so the engine
    ///   cuts the paragraph at the correct text-flow points (one frame in logical order), then
    ///   the generator's line breaks are read back and each line is converted to visual order
    ///   with explicit newlines and a right alignment (bench avia2/silk: the one-pass visual
    ///   string stacks its lines in reverse reading order);
    /// - anything else (UI Toolkit, tk2d, unknown): visual order — correct single-line, the
    ///   documented remainder.
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

        // Components whose flag/alignment we set, with the value they had before: a reused
        // component that moves on to non-RTL text gets its own state back, not our leftovers.
        private static readonly Dictionary<long, bool> _flaggedOriginal = new Dictionary<long, bool>();
        private static readonly Dictionary<long, object> _alignedOriginal = new Dictionary<long, object>();
        private static readonly Dictionary<long, object> _wrapOriginal = new Dictionary<long, object>();

        private static int _logBudget = 8;

        /// <summary>
        /// Present one outgoing string in place. Cheap for the overwhelming majority of texts:
        /// one range scan says "nothing to do".
        /// </summary>
        internal static void Present(object instance, long compId, ref string value,
                                     string settingsFontName = null, FontOverrideRule overrideRule = null)
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
                    // ⚠ "No presentation needed" covers TWO opposite cases. A text ALREADY in
                    // presentation forms is OUR OWN output echoing back (the game re-setting what
                    // it read from the component, a scanner refresh) — it still NEEDS the flag
                    // and the pending reflow it came with; restoring here flipped the flag off
                    // under a shaped string and the whole screen read backwards (found by the
                    // user's full Arabic playthrough, avia13). Only a genuinely LTR text is a
                    // transition worth restoring for.
                    if (!RtlText.ContainsPresentationForms(value))
                    {
                        RestoreIfFlagged(instance, compId, prop);
                        _reflows.Remove(compId);
                    }
                    return;
                }

                if (prop != null)
                {
                    string flagged = RtlComposer.Compose(value, RtlOutput.RtlFlagged);
                    if (compId != -1 && !_flaggedOriginal.ContainsKey(compId))
                    {
                        bool original = false;
                        try { original = prop.GetMethod != null && (bool)prop.GetValue(instance, null); } catch { }
                        _flaggedOriginal[compId] = original;
                    }
                    try { prop.SetValue(instance, true, null); } catch { }
                    MirrorAlignment(instance, compId,
                        TranslatorCore.ShouldMirrorRtlAlignment(settingsFontName, overrideRule));
                    TranslatorCore.RegisterPresentedText(flagged, value);
                    Log(compId, "flagged", value, flagged);
                    value = flagged;
                    return;
                }

                var type = instance.GetType();

                // TextMesh never wraps by itself: every line break is already an explicit '\n',
                // so the per-line visual conversion happens right here, no second pass needed.
                if (TypeHelper.TextMeshType != null && TypeHelper.TextMeshType.IsAssignableFrom(type))
                {
                    string perLine = ComposeVisualPerLine(value);
                    TranslatorCore.RegisterPresentedText(perLine, value);
                    Log(compId, "visual/lines", value, perLine);
                    value = perLine;
                    return;
                }

                // UI.Text: two-pass emission. Pass 1 assigns the shaped LOGICAL string — for one
                // frame it reads backwards, the price of letting the engine compute the correct
                // break points; ProcessPendingReflows converts each cut line next frame.
                if (TypeHelper.UI_TextType != null && TypeHelper.UI_TextType.IsAssignableFrom(type))
                {
                    string shapedLogical = RtlComposer.ShapeLogicalOnly(value);
                    TranslatorCore.RegisterPresentedText(shapedLogical, value);
                    if (compId != -1)
                        _reflows[compId] = new Reflow
                        {
                            Comp = new WeakReference(instance),
                            Logical = value,
                            Assigned = shapedLogical,
                            Mirror = TranslatorCore.ShouldMirrorRtlAlignment(settingsFontName, overrideRule),
                        };
                    Log(compId, "logical+reflow", value, shapedLogical);
                    value = shapedLogical;
                    return;
                }

                // Everything else: visual order, correct single-line (multi-line on these engines
                // is the documented remainder — no generator to read).
                string composed = RtlComposer.Compose(value, RtlOutput.VisualOrder);
                TranslatorCore.RegisterPresentedText(composed, value);
                Log(compId, "visual", value, composed);
                value = composed;
            }
            catch (Exception ex)
            {
                // A failure here must never cost the translation itself: the logical text shows
                // broken (isolated letters) exactly as before this pipeline existed.
                TranslatorCore.LogWarning($"[RtlPresenter] compose failed, showing logical text: {ex.Message}");
            }
        }

        #region UI.Text reflow (pass 2)

        private sealed class Reflow
        {
            public WeakReference Comp;
            public string Logical;
            public string Assigned;
            public int Attempts;
            public bool Mirror;
        }

        private static readonly Dictionary<long, Reflow> _reflows = new Dictionary<long, Reflow>();
        private static readonly List<long> _reflowScratch = new List<long>();

        // cachedTextGenerator plumbing, resolved once per process.
        private static bool _genResolved;
        private static PropertyInfo _cachedGeneratorProp;   // Text.cachedTextGenerator
        private static PropertyInfo _generatorLinesProp;    // TextGenerator.lines -> IList<UILineInfo>
        private static PropertyInfo _generatorCharCountProp; // TextGenerator.characterCount
        private static FieldInfo _lineStartCharField;       // UILineInfo.startCharIdx
        private static PropertyInfo _lineStartCharProp;

        /// <summary>
        /// Convert every pending UI.Text from its measuring form (shaped logical) to the final
        /// per-line visual form, using the break points the engine just computed. Called once per
        /// frame from the scanner's update pass, main thread.
        /// </summary>
        internal static void ProcessPendingReflows()
        {
            if (_reflows.Count == 0) return;

            _reflowScratch.Clear();
            _reflowScratch.AddRange(_reflows.Keys);
            foreach (long id in _reflowScratch)
            {
                var entry = _reflows[id];
                var comp = entry.Comp.Target;
                bool dead = comp == null || (comp is UnityEngine.Object uo && uo == null);
                if (dead) { _reflows.Remove(id); continue; }

                try
                {
                    // The game moved on to another text — this reflow is stale.
                    if (TypeHelper.GetText(comp) != entry.Assigned) { _reflows.Remove(id); continue; }

                    // An INACTIVE component has no line data and never will until it shows: games
                    // preload hidden panes (a guide fills every page up front), and burning the
                    // attempts there left the fallback's reversed line stack as the final display
                    // once the pane opened (bios/biot bench). Wait, without spending attempts —
                    // staleness is already covered by the text check above.
                    if (comp is UnityEngine.Component c && c.gameObject != null && !c.gameObject.activeInHierarchy)
                        continue;

                    string final = BuildPerLineVisual(comp, entry.Assigned, out string whyNot);
                    if (final == null)
                    {
                        // Generator not ready (or unreadable). A few frames, then fall back to
                        // whole-string visual — and SAY so: a silent fallback made the reversed
                        // line stack undiagnosable from a screenshot.
                        if (++entry.Attempts < 3) continue;
                        if (_fallbackLogBudget > 0)
                        {
                            _fallbackLogBudget--;
                            TranslatorCore.LogWarning($"[RtlPresenter] reflow gave up ({whyNot}) — whole-string visual order, line stack may read bottom-up: comp={id}");
                        }
                        final = RtlComposer.Compose(entry.Logical, RtlOutput.VisualOrder);
                    }

                    TranslatorCore.RegisterPresentedText(final, entry.Logical);
                    MirrorAlignment(comp, id, entry.Mirror);

                    // 🔴 WE computed the line breaks — the engine must not wrap again. A
                    // recomposed line is exactly as wide as the rect it was cut against, and the
                    // rendering rounding re-wrapped it: the overflowing visual chunk is the
                    // sentence's FIRST word, shoved onto its own row (bioc bench, «تحوّل»).
                    // Original wrap mode remembered and restored like the alignment.
                    DisableRewrap(comp, id);

                    TranslatorPatches.BypassTextPrefix = true;
                    try { TypeHelper.SetText(comp, final); }
                    finally { TranslatorPatches.BypassTextPrefix = false; }
                    _reflows.Remove(id);
                }
                catch (Exception ex)
                {
                    _reflows.Remove(id);
                    TranslatorCore.LogWarning($"[RtlPresenter] reflow failed, leaving measuring form: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// The assigned string re-cut at the engine's own break points, each line converted to
        /// visual order. Null when the generator cannot be read (not populated yet, IL2CPP
        /// marshaling, rich-text indices — see below).
        /// </summary>
        private static int _fallbackLogBudget = 5;

        private static string BuildPerLineVisual(object comp, string assigned, out string whyNot)
        {
            whyNot = null;
            // ⚠ Rich text: the generator's char indices refer to the TAG-STRIPPED text, ours to
            // the raw string — slicing would tear a tag apart. Whole-string visual instead.
            if (assigned.IndexOf('<') >= 0) { whyNot = "rich text tags present"; return null; }

            if (!_genResolved)
            {
                _genResolved = true;
                try
                {
                    _cachedGeneratorProp = TypeHelper.UI_TextType.GetProperty("cachedTextGenerator", BindingFlags.Public | BindingFlags.Instance);
                    var genType = _cachedGeneratorProp?.PropertyType;
                    _generatorLinesProp = genType?.GetProperty("lines", BindingFlags.Public | BindingFlags.Instance);
                    _generatorCharCountProp = genType?.GetProperty("characterCount", BindingFlags.Public | BindingFlags.Instance);
                    // UILineInfo lives in the text-rendering assembly, not necessarily UI's:
                    // the generic argument of TextGenerator.lines (IList<UILineInfo>) is the
                    // reliable way to it.
                    Type lineType = null;
                    if (_generatorLinesProp != null)
                    {
                        var listType = _generatorLinesProp.PropertyType;
                        if (listType.IsGenericType && listType.GetGenericArguments().Length == 1)
                            lineType = listType.GetGenericArguments()[0];
                    }
                    if (lineType != null)
                    {
                        _lineStartCharField = lineType.GetField("startCharIdx", BindingFlags.Public | BindingFlags.Instance);
                        _lineStartCharProp = lineType.GetProperty("startCharIdx", BindingFlags.Public | BindingFlags.Instance);
                    }
                }
                catch { }
            }
            if (_cachedGeneratorProp == null || _generatorLinesProp == null
                || (_lineStartCharField == null && _lineStartCharProp == null))
            { whyNot = "text generator API not resolvable on this runtime"; return null; }

            var generator = _cachedGeneratorProp.GetValue(comp, null);
            if (generator == null) { whyNot = "no cached generator"; return null; }

            // 🔴 IDENTITY, not just bounds: on a page switch the game refills the same component
            // and for a frame or two the generator still describes the PREVIOUS text — its line
            // starts fall inside our bounds and the slices cut words in half (biob bench: نفسك
            // sawn across two distant lines). The generated character count must match the
            // assigned string (±1: Unity generates a trailing terminator glyph).
            if (_generatorCharCountProp != null)
            {
                int genChars = Convert.ToInt32(_generatorCharCountProp.GetValue(generator, null));
                if (Math.Abs(genChars - assigned.Length) > 1)
                { whyNot = $"generator describes another text ({genChars} chars vs {assigned.Length})"; return null; }
            }

            var lines = _generatorLinesProp.GetValue(generator, null) as System.Collections.IList;
            if (lines == null || lines.Count == 0) { whyNot = "generator has no lines yet"; return null; }

            var starts = new List<int>(lines.Count);
            foreach (var line in lines)
            {
                object v = _lineStartCharField != null ? _lineStartCharField.GetValue(line)
                                                       : _lineStartCharProp.GetValue(line, null);
                starts.Add(Convert.ToInt32(v));
            }
            if (starts[0] != 0) { whyNot = "line data does not start at 0"; return null; }
            // The generator described a different (older) string — lengths must agree.
            foreach (int s in starts) if (s < 0 || s > assigned.Length)
            { whyNot = "generator line data belongs to another text"; return null; }

            var outLines = new List<string>(starts.Count);
            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i];
                int end = i + 1 < starts.Count ? starts[i + 1] : assigned.Length;
                if (end <= start) continue;
                // Trailing spaces too: the wrap point's space rides at the slice end and pushes
                // the recomposed line to the exact rect width.
                string slice = assigned.Substring(start, end - start).TrimEnd('\n', '\r', ' ');
                outLines.Add(slice.Length == 0 ? "" : RtlComposer.Compose(slice, RtlOutput.VisualOrder));
            }
            return string.Join("\n", outLines.ToArray());
        }

        /// <summary>
        /// MIRROR a component's horizontal alignment for RTL text: left becomes right and right
        /// becomes left — alignment follows the reading direction, so a "start-aligned" label
        /// stays start-aligned. Center, justified and the rest are untouched, and the original
        /// value is restored when the component goes back to LTR. The DECISION is per font and
        /// per override rule (<see cref="TranslatorCore.ShouldMirrorRtlAlignment"/> —
        /// user-arbitrated: one game mixes components that need the mirror with boxes built for
        /// one side, so a global switch cannot be right). Handles both alignment vocabularies:
        /// UI.Text/TextMesh TextAnchor (triples, column 0/1/2) and TMP's TextAlignmentOptions
        /// (bit field, horizontal Left=1 / Right=4 in the low byte).
        /// </summary>
        private static void MirrorAlignment(object comp, long compId, bool mirror)
        {
            if (!mirror) return;
            try
            {
                var alignProp = comp.GetType().GetProperty("alignment", BindingFlags.Public | BindingFlags.Instance);
                if (alignProp?.SetMethod == null) return;
                object current = alignProp.GetValue(comp, null);

                // 🔴 IDEMPOTENT, computed from the ORIGINAL — never from the current state. The
                // first version swapped whatever it found, so every re-presentation of the same
                // component (a guide page refilled) toggled the side: right, left, right — one
                // screen out of two aligned wrong (bioa/biob bench, found by the user).
                object original;
                if (compId == -1 || !_alignedOriginal.TryGetValue(compId, out original))
                {
                    original = current;
                    if (compId != -1) _alignedOriginal[compId] = original;
                }

                int v = Convert.ToInt32(original);
                int mirrored = v;

                if (Enum.GetUnderlyingType(alignProp.PropertyType) == typeof(int) && v <= 8)
                {
                    // TextAnchor-style: 3 rows of Left/Center/Right.
                    int column = v % 3;
                    if (column == 0) mirrored = v + 2;
                    else if (column == 2) mirrored = v - 2;
                }
                else
                {
                    // TMP TextAlignmentOptions-style bit field: horizontal flags in the low byte.
                    if ((v & 0x1) != 0) mirrored = (v & ~0x1) | 0x4;
                    else if ((v & 0x4) != 0) mirrored = (v & ~0x4) | 0x1;
                }

                if (mirrored == Convert.ToInt32(current)) return;
                alignProp.SetValue(comp, Enum.ToObject(alignProp.PropertyType, mirrored), null);
            }
            catch { }
        }

        #endregion

        /// <summary>horizontalOverflow = Overflow while OUR line breaks are displayed.</summary>
        private static void DisableRewrap(object comp, long compId)
        {
            try
            {
                var prop = comp.GetType().GetProperty("horizontalOverflow", BindingFlags.Public | BindingFlags.Instance);
                if (prop?.SetMethod == null) return;
                object current = prop.GetValue(comp, null);
                object overflow = Enum.ToObject(prop.PropertyType, 1);   // HorizontalWrapMode.Overflow
                if (Equals(current, overflow)) return;
                if (compId != -1 && !_wrapOriginal.ContainsKey(compId))
                    _wrapOriginal[compId] = current;
                prop.SetValue(comp, overflow, null);
            }
            catch { }
        }

        /// <summary>Each explicit line to visual order — the whole story for TextMesh.</summary>
        private static string ComposeVisualPerLine(string logical)
        {
            var lines = logical.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                lines[i] = line.Length == 0 ? line : RtlComposer.Compose(line, RtlOutput.VisualOrder);
            }
            return string.Join("\n", lines);
        }

        private static void RestoreIfFlagged(object instance, long compId, PropertyInfo prop)
        {
            if (compId == -1) return;
            if (prop != null && _flaggedOriginal.TryGetValue(compId, out bool original))
            {
                _flaggedOriginal.Remove(compId);
                try { prop.SetValue(instance, original, null); } catch { }
            }
            if (_alignedOriginal.TryGetValue(compId, out object anchor))
            {
                _alignedOriginal.Remove(compId);
                try
                {
                    var alignProp = instance.GetType().GetProperty("alignment", BindingFlags.Public | BindingFlags.Instance);
                    alignProp?.SetValue(instance, anchor, null);
                }
                catch { }
            }
            if (_wrapOriginal.TryGetValue(compId, out object wrap))
            {
                _wrapOriginal.Remove(compId);
                try
                {
                    var wrapProp = instance.GetType().GetProperty("horizontalOverflow", BindingFlags.Public | BindingFlags.Instance);
                    wrapProp?.SetValue(instance, wrap, null);
                }
                catch { }
            }
        }

        private static void Log(long compId, string mode, string logical, string composed)
        {
            if (_logBudget <= 0) return;
            _logBudget--;
            TranslatorCore.LogDebug($"[RtlPresenter] comp={compId} mode={mode} " +
                $"'{(logical.Length > 30 ? logical.Substring(0, 30) + "…" : logical)}' → shaped ({composed.Length} ch)");
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
