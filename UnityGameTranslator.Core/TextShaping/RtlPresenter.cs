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
    /// Engine decision, probed per type and cached — ONE mechanism, one LINE SOURCE per engine
    /// (user-arbitrated 2026-08-31: every engine gets its multi-line answer, none is left as
    /// "the documented remainder"):
    /// - <c>isRightToLeftText</c> present (TMP, TMProOld — bench-proven): flagged form + the
    ///   flag, original value restored when the text leaves RTL. The engine owns wrapping AND
    ///   rich-text tags natively — nothing more to do;
    /// - TextMesh (never auto-wraps): every break is an explicit '\n' — per-line visual,
    ///   immediately;
    /// - tk2d: <c>FormatText(string)</c> is PUBLIC and synchronous (read in the bench game's own
    ///   assembly) — ask the engine where it would cut the shaped logical string, then emit each
    ///   cut line in visual order, immediately. Its overflow test is a running sum of glyph
    ///   advances, so a reordered line that fitted still fits: no re-wrap guard needed;
    /// - UI.Text: the two-pass emission — the SHAPED LOGICAL string is assigned so the engine
    ///   cuts the paragraph at the correct text-flow points (one frame in logical order), then
    ///   the generator's line breaks are read back and each line is converted to visual order.
    ///   With rich text, the generator's indices count the TAG-STRIPPED text: RichTextIndexMap
    ///   bridges them back to the raw string (the legacy tag set is closed, so the map can be
    ///   exact, and the characterCount cross-check proves it per call);
    /// - NGUI (<c>processedText</c> present): same two-pass shape — the engine wraps the assigned
    ///   string itself and processedText hands the result back with '\n' at its own break points;
    /// - UI Toolkit (standard generator): the engine exposes no line data, but it MEASURES on
    ///   demand — line breaks are recomputed by asking MeasureTextSize word by word (the
    ///   engine's own ruler, not ours), once the layout has given the element a width. An
    ///   element already rendering through ATG does bidi natively: presentation is skipped;
    /// - anything else: visual order — correct single-line, and now the loudly-logged exception
    ///   rather than the silent rule.
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
                    else if (value.IndexOf("<u", StringComparison.OrdinalIgnoreCase) >= 0 && _underlineDropBudget > 0)
                    {
                        // Our own shaped output coming back — and still carrying an underline the
                        // guard should have removed. Worth a line while this engine's underline
                        // defect is being characterised: it would mean a write reached the element
                        // without going through the guard at all.
                        _underlineDropBudget--;
                        TranslatorCore.LogWarning($"[RtlPresenter] shaped echo still carries an underline tag on comp={compId} — a write bypassed the guard");
                    }
                    return;
                }

                bool mirror = TranslatorCore.ShouldMirrorRtlAlignment(settingsFontName, overrideRule);

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
                    MirrorAlignment(instance, compId, mirror);
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
                    MirrorAlignment(instance, compId, mirror);
                    Log(compId, "visual/lines", value, perLine);
                    value = perLine;
                    return;
                }

                // tk2d: its own FormatText(string) says synchronously where it would cut — the
                // one engine that answers the wrapping question without waiting a frame.
                if (TranslatorPatches.Tk2dType != null && TranslatorPatches.Tk2dType.IsAssignableFrom(type))
                {
                    string final = ComposeTk2dPerLine(instance, value);
                    TranslatorCore.RegisterPresentedText(final, value);
                    MirrorAlignment(instance, compId, mirror);
                    Log(compId, "visual/tk2d", value, final);
                    value = final;
                    return;
                }

                // UI.Text: two-pass emission. Pass 1 assigns the shaped LOGICAL string — for one
                // frame it reads backwards, the price of letting the engine compute the correct
                // break points; ProcessPendingReflows converts each cut line next frame.
                if (TypeHelper.UI_TextType != null && TypeHelper.UI_TextType.IsAssignableFrom(type))
                {
                    QueueReflow(instance, compId, ref value, ReflowKind.UGuiText, mirror, "logical+reflow");
                    return;
                }

                // NGUI (or a lookalike carrying processedText): same two-pass shape — the engine
                // wraps the assigned string itself, processedText hands back the result with the
                // '\n' it inserted.
                if (ProcessedTextProp(type) != null)
                {
                    QueueReflow(instance, compId, ref value, ReflowKind.Ngui, mirror, "logical+reflow/ngui");
                    return;
                }

                // UI Toolkit: an ATG element does bidi natively — presenting on top of it would
                // double-process. The standard generator gets the measured two-pass.
                if (UIToolkitSupport.IsTextElementInstance(instance))
                {
                    if (UIToolkitSupport.IsAtgActive(instance))
                    {
                        UIToolkitSupport.RestoreRtlAdjustments(instance);
                        Log(compId, "native/atg", value, value);
                        return;
                    }
                    // 🔴 CONDITIONAL crash guard, not a ban (user-arbitrated 2026-09-02). Unity's
                    // tracked bug: an underline spanning glyphs served by a FALLBACK font asset
                    // dies in DrawUnderlineMesh (IndexOutOfRange, fixed only in 6000.5.0a5) — our
                    // bench hit it twice with Arabic exactly as their repro hits it with an
                    // emoji. Underlined Arabic links are perfectly normal typography, so the tag
                    // is kept whenever this element CAN render it safely — engine carrying the
                    // fix, or one font asset covering both the RTL text and the '_' glyph (a
                    // game with a real Arabic font, or the mod's own replacement font) — and
                    // dropped only in the configuration proven to kill the game.
                    string stripped = RtlComposer.StripUnderlineTags(value);
                    // ⚠ The safety question is asked about the SHAPED form — the glyphs that will
                    // be drawn — never about the logical text: a font carrying base Arabic but no
                    // presentation forms answered "safe" and the game died anyway (3rd bench
                    // crash). Shaping here is the same call QueueReflow makes just below.
                    if (!ReferenceEquals(stripped, value)
                        && !UIToolkitSupport.UnderlineIsSafe(instance, RtlComposer.ShapeLogicalOnly(value)))
                    {
                        value = stripped;
                        if (_underlineDropBudget > 0)
                        {
                            _underlineDropBudget--;
                            TranslatorCore.LogWarning("[RtlPresenter] underline/strikethrough tag dropped: this element's font cannot cover the RTL text itself, and this engine's DrawUnderlineMesh crashes on fallback-font underlines (Unity issue, fixed in 6000.5) — a replacement font covering the language brings the underline back");
                        }
                    }
                    QueueReflow(instance, compId, ref value, ReflowKind.UiToolkit, mirror, "logical+reflow/uitk");
                    return;
                }

                // Everything else: visual order — correct single-line. This branch is now the
                // documented EXCEPTION (unknown frameworks), not the rule, and it says so.
                string composed = RtlComposer.Compose(value, RtlOutput.VisualOrder);
                TranslatorCore.RegisterPresentedText(composed, value);
                MirrorAlignment(instance, compId, mirror);
                if (_fallbackLogBudget > 0)
                {
                    _fallbackLogBudget--;
                    TranslatorCore.LogWarning($"[RtlPresenter] no line source for {type.Name} — whole-string visual order, multi-line may stack bottom-up");
                }
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

        #region Deferred reflow (pass 2) — UI.Text, NGUI, UI Toolkit

        private enum ReflowKind { UGuiText, Ngui, UiToolkit }

        private sealed class Reflow
        {
            public WeakReference Comp;
            public string Logical;
            public string Assigned;
            public int Attempts;
            public bool Mirror;
            public ReflowKind Kind;
        }

        private static readonly Dictionary<long, Reflow> _reflows = new Dictionary<long, Reflow>();
        private static readonly List<long> _reflowScratch = new List<long>();

        private static void QueueReflow(object instance, long compId, ref string value,
                                        ReflowKind kind, bool mirror, string logMode)
        {
            string shapedLogical = RtlComposer.ShapeLogicalOnly(value);
            TranslatorCore.RegisterPresentedText(shapedLogical, value);
            if (compId != -1)
                _reflows[compId] = new Reflow
                {
                    Comp = new WeakReference(instance),
                    Logical = value,
                    Assigned = shapedLogical,
                    Mirror = mirror,
                    Kind = kind,
                };
            Log(compId, logMode, value, shapedLogical);
            value = shapedLogical;
        }

        // cachedTextGenerator plumbing, resolved once per process.
        private static bool _genResolved;
        private static PropertyInfo _cachedGeneratorProp;   // Text.cachedTextGenerator
        private static PropertyInfo _generatorLinesProp;    // TextGenerator.lines -> IList<UILineInfo>
        private static PropertyInfo _generatorCharCountProp; // TextGenerator.characterCount
        private static PropertyInfo _supportRichTextProp;   // Text.supportRichText
        private static FieldInfo _lineStartCharField;       // UILineInfo.startCharIdx
        private static PropertyInfo _lineStartCharProp;

        // processedText per concrete type (NGUI UILabel and lookalikes).
        private static readonly Dictionary<Type, PropertyInfo> _processedTextProps = new Dictionary<Type, PropertyInfo>();

        // tk2d FormatText(string), resolved once (one tk2d type per game).
        private static bool _tk2dResolved;
        private static MethodInfo _tk2dFormatText;
        private static PropertyInfo _tk2dInlineStyling;

        /// <summary>
        /// Convert every pending component from its measuring form (shaped logical) to the final
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
                    string currentText = entry.Kind == ReflowKind.UiToolkit
                        ? UIToolkitSupport.GetElementText(comp)
                        : TypeHelper.GetText(comp);
                    if (currentText != entry.Assigned) { _reflows.Remove(id); continue; }

                    // An INACTIVE component has no line data and never will until it shows: games
                    // preload hidden panes (a guide fills every page up front), and burning the
                    // attempts there left the fallback's reversed line stack as the final display
                    // once the pane opened (bios/biot bench). Wait, without spending attempts —
                    // staleness is already covered by the text check above. Same story for a
                    // UI Toolkit element not (yet) attached to a panel.
                    if (comp is UnityEngine.Component c && c.gameObject != null && !c.gameObject.activeInHierarchy)
                        continue;
                    if (entry.Kind == ReflowKind.UiToolkit && !UIToolkitSupport.IsElementAttached(comp))
                        continue;

                    string final = BuildLines(entry.Kind, comp, entry.Assigned, out string whyNot, out bool waitQuietly);
                    if (final == null)
                    {
                        // An element with NO LAYOUT yet (hidden pane, first frame) waits without
                        // spending attempts, exactly like an inactive uGUI component above —
                        // burning them left the fallback's reversed stack as the final display
                        // once the pane opened (bios/biot lesson, seen again as "no layout yet"
                        // fallbacks in the 2026-09-02 UITK bench log).
                        if (waitQuietly) continue;
                        // Line source not ready (or unreadable). A few frames, then fall back to
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

                    if (entry.Kind == ReflowKind.UiToolkit)
                    {
                        UIToolkitSupport.MirrorAlign(comp, entry.Mirror);
                        // 🔴 WE computed the line breaks — the engine must not wrap again (see
                        // DisableRewrap below for the uGUI account of why). UI Toolkit's knob is
                        // the whiteSpace style; explicit '\n' stay honored under NoWrap.
                        UIToolkitSupport.DisableWrap(comp);
                        UIToolkitSupport.SetElementTextSilently(comp, final);
                    }
                    else
                    {
                        MirrorAlignment(comp, id, entry.Mirror);
                        // 🔴 WE computed the line breaks — the engine must not wrap again. A
                        // recomposed line is exactly as wide as the rect it was cut against, and
                        // the rendering rounding re-wrapped it: the overflowing visual chunk is
                        // the sentence's FIRST word, shoved onto its own row (bioc bench,
                        // «تحوّل»). Original wrap mode remembered and restored like the
                        // alignment. NGUI has no such knob; its wrap re-measures the same glyph
                        // advances deterministically (no rendering rounding), so a trimmed line
                        // that fitted keeps fitting — bench holds the proof burden there.
                        if (entry.Kind == ReflowKind.UGuiText)
                            DisableRewrap(comp, id);

                        TranslatorPatches.BypassTextPrefix = true;
                        try { TypeHelper.SetText(comp, final); }
                        finally { TranslatorPatches.BypassTextPrefix = false; }
                    }
                    _reflows.Remove(id);
                }
                catch (Exception ex)
                {
                    _reflows.Remove(id);
                    TranslatorCore.LogWarning($"[RtlPresenter] reflow failed, leaving measuring form: {ex.Message}");
                }
            }
        }

        private static int _fallbackLogBudget = 5;
        private static int _underlineDropBudget = 3;

        /// <summary>One line source per engine; everything after the cut is shared.</summary>
        private static string BuildLines(ReflowKind kind, object comp, string assigned, out string whyNot, out bool waitQuietly)
        {
            waitQuietly = false;
            switch (kind)
            {
                case ReflowKind.UGuiText:
                    return BuildPerLineVisual(comp, assigned, out whyNot);
                case ReflowKind.Ngui:
                    return BuildNguiLines(comp, assigned, out whyNot);
                default:
                    var lines = UIToolkitSupport.TryBreakLines(comp, assigned, out whyNot, out waitQuietly);
                    return lines == null ? null : ComposeLines(lines);
            }
        }

        /// <summary>
        /// The assigned string re-cut at the engine's own break points, each line converted to
        /// visual order. Null when the generator cannot be read (not populated yet, IL2CPP
        /// marshaling — see below).
        /// </summary>
        private static string BuildPerLineVisual(object comp, string assigned, out string whyNot)
        {
            whyNot = null;

            if (!_genResolved)
            {
                _genResolved = true;
                try
                {
                    _cachedGeneratorProp = TypeHelper.UI_TextType.GetProperty("cachedTextGenerator", BindingFlags.Public | BindingFlags.Instance);
                    _supportRichTextProp = TypeHelper.UI_TextType.GetProperty("supportRichText", BindingFlags.Public | BindingFlags.Instance);
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

            // ⚠ Rich text: the generator's char indices count the TAG-STRIPPED text, our slices
            // cut the raw string — RichTextIndexMap bridges the two. Only when the component
            // actually parses tags: with supportRichText off, a '<' is an ordinary character.
            int[] tagMap = null;
            int referenceLength = assigned.Length;
            if (assigned.IndexOf('<') >= 0)
            {
                bool richText = true;
                try
                {
                    if (_supportRichTextProp != null)
                        richText = (bool)_supportRichTextProp.GetValue(comp, null);
                }
                catch { }
                if (richText)
                {
                    tagMap = RichTextIndexMap.Build(assigned, out int strippedLength);
                    if (tagMap != null) referenceLength = strippedLength;
                }
            }

            var generator = _cachedGeneratorProp.GetValue(comp, null);
            if (generator == null) { whyNot = "no cached generator"; return null; }

            // 🔴 IDENTITY, not just bounds: on a page switch the game refills the same component
            // and for a frame or two the generator still describes the PREVIOUS text — its line
            // starts fall inside our bounds and the slices cut words in half (biob bench: نفسك
            // sawn across two distant lines). The generated character count must match the
            // reference length (±1: Unity generates a trailing terminator glyph). With a tag map
            // in play this same check is also the PROOF of the map: if the native parser stripped
            // differently than RichTextIndexMap claims, the counts diverge and we fall back.
            if (_generatorCharCountProp != null)
            {
                int genChars = Convert.ToInt32(_generatorCharCountProp.GetValue(generator, null));
                if (Math.Abs(genChars - referenceLength) > 1)
                { whyNot = $"generator describes another text ({genChars} chars vs {referenceLength})"; return null; }
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
            foreach (int s in starts) if (s < 0 || s > referenceLength)
            { whyNot = "generator line data belongs to another text"; return null; }

            var slices = new List<string>(starts.Count);
            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i];
                int end = i + 1 < starts.Count ? starts[i + 1] : referenceLength;
                if (end <= start) continue;
                int rawStart = tagMap == null ? start : tagMap[start];
                int rawEnd = tagMap == null ? end : tagMap[end];
                slices.Add(assigned.Substring(rawStart, rawEnd - rawStart));
            }
            return ComposeLines(slices);
        }

        /// <summary>
        /// NGUI's line source: the label wrapped the assigned string itself and processedText
        /// hands it back with '\n' at its break points. The equality check (both strings modulo
        /// whitespace) is what keeps this honest: encoding markup, ellipsis or shrink produce a
        /// DIFFERENT text, and cutting the assigned string with someone else's breaks would saw
        /// words — divergence falls back to whole-string visual instead.
        /// </summary>
        private static string BuildNguiLines(object comp, string assigned, out string whyNot)
        {
            whyNot = null;
            var prop = ProcessedTextProp(comp.GetType());
            if (prop == null) { whyNot = "processedText disappeared"; return null; }

            string processed = null;
            try { processed = prop.GetValue(comp, null) as string; } catch { }
            if (string.IsNullOrEmpty(processed)) { whyNot = "processedText not ready"; return null; }

            if (!EqualsIgnoringWhitespace(processed, assigned))
            { whyNot = "processedText diverges from the assigned text (markup, ellipsis or shrink)"; return null; }

            return ComposeLines(processed.Split('\n'));
        }

        private static PropertyInfo ProcessedTextProp(Type type)
        {
            if (_processedTextProps.TryGetValue(type, out var cached)) return cached;
            PropertyInfo prop = null;
            try
            {
                prop = type.GetProperty("processedText", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && (prop.PropertyType != typeof(string) || prop.GetMethod == null))
                    prop = null;
            }
            catch { }
            _processedTextProps[type] = prop;
            return prop;
        }

        private static bool EqualsIgnoringWhitespace(string a, string b)
        {
            int i = 0, j = 0;
            while (true)
            {
                while (i < a.Length && (a[i] == ' ' || a[i] == '\n' || a[i] == '\r' || a[i] == '\t')) i++;
                while (j < b.Length && (b[j] == ' ' || b[j] == '\n' || b[j] == '\r' || b[j] == '\t')) j++;
                if (i >= a.Length || j >= b.Length) return i >= a.Length && j >= b.Length;
                if (a[i] != b[j]) return false;
                i++; j++;
            }
        }

        #endregion

        #region tk2d (synchronous line source)

        /// <summary>
        /// Ask tk2d where it would cut, then emit per cut line — all inside the setter prefix.
        /// FormatText keeps explicit '\n', replaces the wrap-point handling around spaces, and
        /// its inline styling commands use '^', which our composer does not protect: a text that
        /// both carries '^' and runs through a component with inlineStyling on falls back to the
        /// per-explicit-line form rather than risk tearing a command apart.
        /// </summary>
        private static string ComposeTk2dPerLine(object instance, string logical)
        {
            if (!_tk2dResolved)
            {
                _tk2dResolved = true;
                try
                {
                    _tk2dFormatText = TranslatorPatches.Tk2dType.GetMethod("FormatText",
                        BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                    _tk2dInlineStyling = TranslatorPatches.Tk2dType.GetProperty("inlineStyling",
                        BindingFlags.Public | BindingFlags.Instance);
                }
                catch { }
            }

            string shaped = RtlComposer.ShapeLogicalOnly(logical);

            if (shaped.IndexOf('^') >= 0)
            {
                bool styling = false;
                try { styling = _tk2dInlineStyling != null && (bool)_tk2dInlineStyling.GetValue(instance, null); }
                catch { }
                if (styling) return ComposeVisualPerLine(logical);
            }

            string wrapped = shaped;
            if (_tk2dFormatText != null)
            {
                try { wrapped = (string)_tk2dFormatText.Invoke(instance, new object[] { shaped }) ?? shaped; }
                catch { wrapped = shaped; }
            }
            else if (_fallbackLogBudget > 0)
            {
                _fallbackLogBudget--;
                TranslatorCore.LogWarning("[RtlPresenter] tk2dTextMesh.FormatText not resolvable — per-explicit-line only, engine wrap points unknown");
            }

            return ComposeLines(wrapped.Split('\n'));
        }

        #endregion

        /// <summary>
        /// The shared tail of every line source: trim each cut line (the wrap point's space rides
        /// at the slice end and pushes the recomposed line to the exact rect width — bioc bench),
        /// convert it to visual order, join with explicit newlines.
        /// </summary>
        private static string ComposeLines(IList<string> slices)
        {
            var outLines = new List<string>(slices.Count);
            for (int i = 0; i < slices.Count; i++)
            {
                string line = slices[i].TrimEnd('\n', '\r', ' ');
                outLines.Add(line.Length == 0 ? "" : RtlComposer.Compose(line, RtlOutput.VisualOrder));
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
        /// one side, so a global switch cannot be right).
        ///
        /// The swap works by NAME first — "Left"→"Right" inside the enum member's own name —
        /// which covers every alignment vocabulary met so far with one rule: TextAnchor
        /// (UpperLeft→UpperRight), NGUIText.Alignment (Left→Right, Automatic untouched), TMP's
        /// named combos (TopLeft→TopRight). Two arithmetic fallbacks remain for enums whose
        /// value has no name, GUARDED BY TYPE NAME: the old "any int ≤ 8 is a TextAnchor triple"
        /// guess would corrupt NGUI's enum (Right=3 → 5, undefined).
        /// </summary>
        private static void MirrorAlignment(object comp, long compId, bool mirror)
        {
            if (!mirror) return;
            try
            {
                var alignProp = comp.GetType().GetProperty("alignment", BindingFlags.Public | BindingFlags.Instance);
                if (alignProp?.SetMethod == null || !alignProp.PropertyType.IsEnum) return;
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

                object mirroredObj = MirroredAlignmentValue(alignProp.PropertyType, original);
                if (mirroredObj == null || Equals(mirroredObj, current)) return;
                alignProp.SetValue(comp, mirroredObj, null);
            }
            catch { }
        }

        /// <summary>The mirrored value of one alignment enum, or null when there is nothing to swap.</summary>
        internal static object MirroredAlignmentValue(Type enumType, object original)
        {
            string name = null;
            try { name = Enum.GetName(enumType, original); } catch { }
            if (name != null)
            {
                string swapped =
                    name.IndexOf("Left", StringComparison.Ordinal) >= 0 ? name.Replace("Left", "Right") :
                    name.IndexOf("Right", StringComparison.Ordinal) >= 0 ? name.Replace("Right", "Left") : null;
                if (swapped != null)
                {
                    try { return Enum.Parse(enumType, swapped); }
                    catch { }
                }
                return null;   // named value with no Left/Right — Center, Justified, Automatic…
            }

            int v = Convert.ToInt32(original);
            int mirrored = v;
            if (enumType.Name == "TextAnchor" && v <= 8)
            {
                // TextAnchor-style: 3 rows of Left/Center/Right.
                int column = v % 3;
                if (column == 0) mirrored = v + 2;
                else if (column == 2) mirrored = v - 2;
            }
            else if (enumType.Name == "TextAlignmentOptions")
            {
                // TMP bit field: horizontal flags in the low byte.
                if ((v & 0x1) != 0) mirrored = (v & ~0x1) | 0x4;
                else if ((v & 0x4) != 0) mirrored = (v & ~0x4) | 0x1;
            }
            return mirrored == v ? null : Enum.ToObject(enumType, mirrored);
        }

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
            return ComposeLines(logical.Split('\n'));
        }

        private static void RestoreIfFlagged(object instance, long compId, PropertyInfo prop)
        {
            if (UIToolkitSupport.IsTextElementInstance(instance))
                UIToolkitSupport.RestoreRtlAdjustments(instance);

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
