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

            long tPerf = Perf.Start();
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
                    // One pass whenever the engine can answer now — same shape as UI Toolkit.
                    string shapedUGui = RtlComposer.ShapeLogicalOnly(value);
                    string cutNow = BuildUGuiLinesNow(instance, shapedUGui, out string whyNotNow);
                    if (cutNow == null && _immediateLogBudget > 0)
                    {
                        _immediateLogBudget--;
                        TranslatorCore.LogDebug($"[RtlPresenter] UI.Text immediate cut unavailable ({whyNotNow}) — deferred: comp={compId}");
                    }
                    if (cutNow != null)
                    {
                        TranslatorCore.RegisterPresentedText(cutNow, value);
                        MirrorAlignment(instance, compId, mirror);
                        _reflows.Remove(compId);
                        RecordCut(instance, compId, value, mirror);
                        Log(compId, "visual/ugui", value, cutNow);
                        value = cutNow;
                        return;
                    }
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
                    string logicalSource = value;
                    string stripped = RtlComposer.StripUnderlineTags(value);
                    // ⚠ The safety question is asked about the SHAPED form — the glyphs that will
                    // be drawn — never about the logical text: a font carrying base Arabic but no
                    // presentation forms answered "safe" and the game died anyway (3rd bench
                    // crash). See UnderlineIsSafe for what the bench then made of that answer.
                    if (!ReferenceEquals(stripped, value)
                        && !UIToolkitSupport.UnderlineIsSafe(instance, RtlComposer.ShapeLogicalOnly(value)))
                    {
                        value = stripped;
                        if (_underlineDropBudget > 0)
                        {
                            _underlineDropBudget--;
                            TranslatorCore.LogWarning("[RtlPresenter] underline/strikethrough tag dropped on RTL text: this engine's DrawUnderlineMesh crashes laying out an underline over right-to-left glyphs (Unity issue, fixed in 6000.5). The text is unaffected, and translations.json keeps the tag.");
                        }
                    }

                    // ONE pass whenever the element already has a width. UI Toolkit measures a
                    // string handed to it — MeasureTextSize takes the text as an argument — so
                    // unlike UI.Text there is nothing to assign first: the line breaks can be
                    // computed here and only the FINAL form ever reaches the screen. The two-pass
                    // path below stays for an element with no layout yet (a hidden pane, a first
                    // frame), where nothing can be measured at all. This is what removes the
                    // "text appears, then changes" flicker the user saw.
                    string shapedNow = RtlComposer.ShapeLogicalOnly(value);
                    var linesNow = UIToolkitSupport.TryBreakLines(instance, shapedNow, out _);
                    if (linesNow != null)
                    {
                        string finalNow = ComposeLines(linesNow);
                        TranslatorCore.RegisterPresentedText(finalNow, logicalSource);
                        UIToolkitSupport.MirrorAlign(instance, mirror);
                        UIToolkitSupport.ForgetPending(instance);
                        Log(compId, "visual/uitk", value, finalNow);
                        value = finalNow;
                        return;
                    }

                    // No layout to measure against (a pane opening, a first frame). Show the
                    // VISUAL form — right already for anything that fits on one line — and leave
                    // the element to the UI Toolkit walk: that budgeted pass visits every
                    // attached element on the detection cadence, so it reaches this one exactly
                    // when it is on screen with a width, and finishes it then. No per-frame
                    // queue polling a hidden pane sixty times a second (measured: RTL.Reflow
                    // 0.6-1.0 ms EVERY frame, entries never leaving).
                    string visualNow = RtlComposer.Compose(value, RtlOutput.VisualOrder);
                    TranslatorCore.RegisterPresentedText(visualNow, logicalSource);
                    UIToolkitSupport.DeferUntilLaidOut(instance, logicalSource, value, shapedNow, visualNow, mirror);
                    Log(compId, "visual+walk/uitk", value, visualNow);
                    value = visualNow;
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
            finally { Perf.Stop(Perf.RtlPresent, tPerf); }
        }

        /// <summary>
        /// Finish a UI Toolkit element the walk just reached: cut its measuring form now that
        /// it has a width, and write the final per-line visual text. False when it still has no
        /// layout — the element stays pending for a later pass. Called from
        /// UIToolkitSupport.ProcessElement, inside the budgeted walk.
        /// </summary>
        internal static bool FinishUiToolkitPending(object element, string logicalSource, string logical,
                                                    string measure, string assigned, bool mirror)
        {
            // The game moved on to another text — nothing left to finish.
            if (UIToolkitSupport.GetElementText(element) != assigned) return true;

            var lines = UIToolkitSupport.TryBreakLines(element, measure, out _);
            if (lines == null) return false;

            string final = ComposeLines(lines);
            TranslatorCore.RegisterPresentedText(final, logicalSource);
            UIToolkitSupport.MirrorAlign(element, mirror);
            UIToolkitSupport.SetElementTextSilently(element, final);
            return true;
        }

        #region Deferred reflow (pass 2) — UI.Text, NGUI

        private enum ReflowKind { UGuiText, Ngui }

        private sealed class Reflow
        {
            public WeakReference Comp;
            public string Logical;
            public string Assigned;   // what is on screen right now (freshness check)
            public string Measure;    // the shaped LOGICAL form the line source must cut
            public int Attempts;
            public bool Mirror;
            public ReflowKind Kind;
        }

        private static readonly Dictionary<long, Reflow> _reflows = new Dictionary<long, Reflow>();
        private static readonly List<long> _reflowScratch = new List<long>();

        /// <param name="logicalForRecord">
        /// What this display form CAME FROM, for the presented→logical map — the untouched
        /// translation, tags included. Differs from <paramref name="value"/> when stage D had to
        /// alter the text to render it at all (the underline guard): the screen loses the tag,
        /// the recovered source must not, or an edit made from the in-game editor would silently
        /// save the amputated version.
        /// </param>
        /// <param name="assignedForm">
        /// What to put on screen while the line source is out of reach, when it must NOT be the
        /// measuring form. UI.Text needs the shaped logical string assigned — that is how its
        /// generator computes the break points — but UI Toolkit measures a string handed to it,
        /// so assigning the logical order there only means one frame of text reading backwards.
        /// The visual form is given instead: right the first time for everything that fits on a
        /// line, which is most labels, and a paragraph is corrected on the next frame as before.
        /// </param>
        private static void QueueReflow(object instance, long compId, ref string value,
                                        ReflowKind kind, bool mirror, string logMode,
                                        string logicalForRecord = null, string assignedForm = null)
        {
            string shapedLogical = RtlComposer.ShapeLogicalOnly(value);
            string assigned = assignedForm ?? shapedLogical;
            TranslatorCore.RegisterPresentedText(assigned, logicalForRecord ?? value);
            if (compId != -1)
                _reflows[compId] = new Reflow
                {
                    Comp = new WeakReference(instance),
                    Logical = value,
                    Assigned = assigned,
                    Measure = shapedLogical,
                    Mirror = mirror,
                    Kind = kind,
                };
            Log(compId, logMode, value, assigned);
            value = assigned;
        }

        // cachedTextGenerator plumbing, resolved once per process.
        private static bool _genResolved;
        private static PropertyInfo _cachedGeneratorProp;   // Text.cachedTextGenerator
        private static PropertyInfo _generatorLinesProp;    // TextGenerator.lines -> IList<UILineInfo>
        private static PropertyInfo _generatorCharCountProp; // TextGenerator.characterCount
        private static PropertyInfo _supportRichTextProp;   // Text.supportRichText
        // Asking the engine for the line breaks NOW, instead of reading what it drew last frame:
        // Text.GetGenerationSettings(extents) + our own TextGenerator.Populate(text, settings).
        private static MethodInfo _getGenerationSettings;   // Text.GetGenerationSettings(Vector2)
        private static MethodInfo _getPixelAdjustedRect;    // Graphic.GetPixelAdjustedRect()
        private static MethodInfo _generatorPopulate;       // TextGenerator.Populate(string, settings)
        private static object _ownGenerator;                // ours, never the component's
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
                    if (TypeHelper.GetText(comp) != entry.Assigned) { _reflows.Remove(id); continue; }

                    // An INACTIVE component has no line data and never will until it shows: games
                    // preload hidden panes (a guide fills every page up front), and burning the
                    // attempts there left the fallback's reversed line stack as the final display
                    // once the pane opened (bios/biot bench). Wait, without spending attempts —
                    // staleness is already covered by the text check above.
                    if (comp is UnityEngine.Component c && c.gameObject != null && !c.gameObject.activeInHierarchy)
                        continue;

                    string final = BuildLines(entry, comp, out string whyNot);
                    if (final == null)
                    {
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

                    {
                        MirrorAlignment(comp, id, entry.Mirror);
                        // ⚠ The engine's own wrapping stays ON. It used to be switched off so a
                        // recomposed line exactly as wide as its box could not be re-wrapped by
                        // rounding (bioc bench) — but a cut made at a width that was not the box's
                        // final one then had NOTHING to fold it back: a disclaimer ended as one line
                        // as wide as its whole paragraph (bench, after the resize hook). The cut
                        // is now made one pixel narrower than the box instead, which removes the
                        // rounding case while the engine keeps the last word.

                        TranslatorPatches.BypassTextPrefix = true;
                        try { TypeHelper.SetText(comp, final); }
                        finally { TranslatorPatches.BypassTextPrefix = false; }

                        // 🔴 A write that bypasses the prefix bypasses the clone-atlas step too.
                        // UI.Text renders Arabic through a cloned font whose atlas is filled
                        // EXPLICITLY with the characters of each text the prefix writes — after
                        // Present, so it sees the presentation forms. This write is not a prefix
                        // write: without this call the final text's glyphs were never added and
                        // the label drew nothing (bench: "New game" empty at start-up, filled once
                        // the game re-set it through the prefix on the way back from a run).
                        FontManager.EnsureCharsInCloneAtlas(final, comp);
                        if (entry.Kind == ReflowKind.UGuiText) RecordCut(comp, id, entry.Logical, entry.Mirror);
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
        private static int _immediateLogBudget = 6;

        #region Re-cut on resize (UI.Text)

        // 🔴 A cut is right for the width it was made at — and the FIRST width a label has is
        // often not its last: a menu still laying out gave "New game" a sliver of a box, the cut
        // put its two words on two lines, and Best Fit then shrank the font to make two lines fit
        // a one-line box. Two specks in an empty button (bench, bio1 zoomed). The engine signals
        // exactly this moment — Graphic.OnRectTransformDimensionsChange — so a component that
        // wears one of our cuts is re-cut when its box changes width. A trigger, not a poll; the
        // width comparison keeps a fitter that resizes the box to OUR lines from looping.
        private sealed class Cut { public string Logical; public bool Mirror; public float Width; }
        private static readonly Dictionary<long, Cut> _cuts = new Dictionary<long, Cut>();

        private static void RecordCut(object comp, long compId, string logical, bool mirror)
        {
            if (compId == -1) return;
            _cuts[compId] = new Cut { Logical = logical, Mirror = mirror, Width = CurrentWidth(comp) };
        }

        private static float CurrentWidth(object comp)
        {
            EnsureGeneratorPlumbing();
            try
            {
                if (_getPixelAdjustedRect != null && _getPixelAdjustedRect.Invoke(comp, null) is UnityEngine.Rect r)
                    return r.width;
            }
            catch { }
            return float.NaN;
        }

        internal static int HookResize(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            try
            {
                Type graphic = null;
                for (var t = TypeHelper.UI_TextType; t != null && graphic == null; t = t.BaseType)
                    if (t.Name == "Graphic") graphic = t;
                var resized = graphic?.GetMethod("OnRectTransformDimensionsChange", BindingFlags.NonPublic | BindingFlags.Instance);
                if (resized == null) return 0;
                var postfix = typeof(RtlPresenter).GetMethod(nameof(Graphic_Resized_Postfix), BindingFlags.Static | BindingFlags.Public);
                patcher(resized, null, postfix);
                TranslatorCore.LogInfo("[RtlPresenter] Patched Graphic.OnRectTransformDimensionsChange — RTL cuts follow the box");
                return 1;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[RtlPresenter] resize hook not applied ({ex.Message}) — a cut made at a transient width stays");
                return 0;
            }
        }

        public static void Graphic_Resized_Postfix(object __instance)
        {
            if (_cuts.Count == 0 || __instance == null) return;
            try
            {
                if (TypeHelper.UI_TextType == null || !TypeHelper.UI_TextType.IsInstanceOfType(__instance)) return;
                long id = TypeHelper.GetInstanceID(__instance);
                if (!_cuts.TryGetValue(id, out var cut)) return;
                if (!TranslatorCore.IsMainThread) return;

                float width = CurrentWidth(__instance);
                if (float.IsNaN(width) || Math.Abs(width - cut.Width) < 0.5f) return;

                string current = TypeHelper.GetText(__instance);
                if (string.IsNullOrEmpty(current)) return;

                // Re-cut on the next tick, at the new width, through the ordinary deferred path.
                _reflows[id] = new Reflow
                {
                    Comp = new WeakReference(__instance),
                    Logical = cut.Logical,
                    Assigned = current,
                    Measure = RtlComposer.ShapeLogicalOnly(cut.Logical),
                    Mirror = cut.Mirror,
                    Kind = ReflowKind.UGuiText,
                };
            }
            catch { }
        }

        #endregion
        private static int _underlineDropBudget = 3;

        /// <summary>
        /// One line source per engine; everything after the cut is shared. Cuts
        /// <see cref="Reflow.Measure"/>, the shaped logical form — the only one whose character
        /// order matches what a generator reports. (UI Toolkit no longer queues here: its walk
        /// finishes its own elements, see FinishUiToolkitPending.)
        /// </summary>
        private static string BuildLines(Reflow entry, object comp, out string whyNot)
        {
            switch (entry.Kind)
            {
                case ReflowKind.UGuiText:
                {
                    string now = BuildUGuiLinesNow(comp, entry.Measure, out string whyNow);
                    if (now != null) { whyNot = null; return now; }
                    string later = BuildPerLineVisual(comp, entry.Measure, out whyNot);
                    if (later == null) whyNot = $"immediate: {whyNow} | cached: {whyNot}";
                    return later;
                }
                default:
                    return BuildNguiLines(comp, entry.Measure, out whyNot);
            }
        }

        /// <summary>
        /// The assigned string re-cut at the engine's own break points, each line converted to
        /// visual order. Null when the generator cannot be read (not populated yet, IL2CPP
        /// marshaling — see below).
        /// </summary>
        /// <summary>
        /// Ask the engine, RIGHT NOW, where it would cut this text in this component's box — the
        /// UI.Text counterpart of UI Toolkit's MeasureTextSize, and for the same reason.
        ///
        /// 🔴 Reading cachedTextGenerator instead means reading what the component DREW last
        /// frame: on a text swap it still describes the previous string, the identity guard
        /// rejects the cut ("generator describes another text (173 chars vs 126)" — bench, the
        /// guide panel), three frames later the reflow gives up and the whole-string fallback
        /// stacks the lines bottom-up. That is the section title showing UNDER its own list.
        /// Populating our own generator with our own text removes the wait, the guess and the
        /// fallback in one go. Null when the API or the layout is not there yet.
        /// </summary>
        /// <summary>
        /// The generator API, resolved once — and from BOTH paths. It used to live inside the
        /// cached-generator path only, so the immediate path saw "API not resolvable" until the
        /// slow path had run first; on the bench the guide page never got its immediate cut.
        /// </summary>
        private static void EnsureGeneratorPlumbing()
        {
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

                // The synchronous path. Both are public API on this engine (verified in the
                // bench game's own assemblies), and a generator of OUR OWN keeps the
                // component's untouched — that one belongs to its rendering.
                _getGenerationSettings = TypeHelper.UI_TextType.GetMethod("GetGenerationSettings",
                    BindingFlags.Public | BindingFlags.Instance);
                _getPixelAdjustedRect = TypeHelper.UI_TextType.GetMethod("GetPixelAdjustedRect",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (genType != null)
                {
                    foreach (var m in genType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "Populate") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(string)) { _generatorPopulate = m; break; }
                    }
                    try { _ownGenerator = Activator.CreateInstance(genType); } catch { }
                }
            }
            catch { }
        }
        }

        private static string BuildUGuiLinesNow(object comp, string assigned, out string whyNot)
        {
            whyNot = null;
            EnsureGeneratorPlumbing();
            if (_generatorPopulate == null || _getGenerationSettings == null
                || _getPixelAdjustedRect == null || _ownGenerator == null)
            { whyNot = "generator Populate API not resolvable on this runtime"; return null; }

            try
            {
                object rect = _getPixelAdjustedRect.Invoke(comp, null);
                if (!(rect is UnityEngine.Rect r) || r.width < 1f)
                { whyNot = "no layout yet (component has no width)"; return null; }

                // One pixel narrower than the box: a recomposed line then always has room, so
                // the engine's wrapping — kept on as the safety net — never folds it by rounding.
                var extents = new UnityEngine.Vector2(Math.Max(1f, r.width - 1f), r.height);
                object settings = _getGenerationSettings.Invoke(comp, new object[] { extents });
                if (settings == null) { whyNot = "no generation settings"; return null; }
                // Every line the paragraph has, wrapped at the box's width — never cut short by
                // the box's HEIGHT: with the component's own vertical mode the generator stops at
                // what fits ("0 chars vs 382", "13 vs 21" on the bench) and the tail is lost.
                // 🔴 EXCEPT under Best Fit. There the height is part of the question: the engine
                // searches the largest size at which the text fits width AND height, and ignoring
                // the height made it answer "two lines at full size" where the game shows one
                // line at a smaller size — the component then shrank our two lines into two
                // specks (bench: "New game" empty at start-up).
                try
                {
                    bool bestFit = false;
                    var bestFitProp = comp.GetType().GetProperty("resizeTextForBestFit", BindingFlags.Public | BindingFlags.Instance);
                    if (bestFitProp != null) bestFit = (bool)bestFitProp.GetValue(comp, null);
                    if (!bestFit)
                    {
                        var vertical = settings.GetType().GetField("verticalOverflow", BindingFlags.Public | BindingFlags.Instance);
                        if (vertical != null) vertical.SetValue(settings, Enum.ToObject(vertical.FieldType, 1));
                    }
                }
                catch { }
                if (!(bool)_generatorPopulate.Invoke(_ownGenerator, new object[] { assigned, settings }))
                { whyNot = "generator refused to populate"; return null; }

                return BuildPerLineVisual(comp, assigned, out whyNot, _ownGenerator);
            }
            catch (Exception ex) { whyNot = "populate failed: " + ex.Message; return null; }
        }

        private static string BuildPerLineVisual(object comp, string assigned, out string whyNot,
                                                 object populated = null)
        {
            whyNot = null;
            EnsureGeneratorPlumbing();

            if (_cachedGeneratorProp == null || _generatorLinesProp == null
                || (_lineStartCharField == null && _lineStartCharProp == null))
            { whyNot = "text generator API not resolvable on this runtime"; return null; }

            // ⚠ Rich text: which string do the generator's line indices count? The legacy
            // generator seen on the bench keeps every tag character in its stream as an
            // invisible glyph — it reported 173 characters for a 173-char tagged string — so
            // its indices address the RAW string. RichTextIndexMap stays for a runtime that
            // strips (indices then count the tagless text); the generator's own characterCount
            // decides between the two below, and a count matching neither is a stale generator.
            int rawLength = assigned.Length;
            int strippedLength = rawLength;
            int[] tagMap = null;
            if (assigned.IndexOf('<') >= 0)
            {
                bool richText = true;
                try
                {
                    if (_supportRichTextProp != null)
                        richText = (bool)_supportRichTextProp.GetValue(comp, null);
                }
                catch { }
                if (richText) tagMap = RichTextIndexMap.Build(assigned, out strippedLength);
            }
            int referenceLength = rawLength;

            object generator = populated ?? _cachedGeneratorProp.GetValue(comp, null);
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
                if (Math.Abs(genChars - rawLength) <= 1)
                    tagMap = null;                       // raw indices — the bench engine
                else if (tagMap != null && Math.Abs(genChars - strippedLength) <= 1)
                    referenceLength = strippedLength;    // tag-stripped indices — the map applies
                else
                { whyNot = $"generator describes another text ({genChars} chars vs {rawLength} raw / {strippedLength} stripped)"; return null; }
            }
            else
            {
                tagMap = null;                           // no count to prove the map — raw it is
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

        /// <summary>Each explicit line to visual order — the whole story for TextMesh.</summary>
        private static string ComposeVisualPerLine(string logical)
        {
            return ComposeLines(logical.Split('\n'));
        }

        private static void RestoreIfFlagged(object instance, long compId, PropertyInfo prop)
        {
            if (UIToolkitSupport.IsTextElementInstance(instance))
            {
                UIToolkitSupport.RestoreRtlAdjustments(instance);
                UIToolkitSupport.ForgetPending(instance);
            }

            if (compId == -1) return;
            _cuts.Remove(compId);
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
