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

            long tPerf = Perf.Start();
            try
            {
                PresentSyllabic(instance, compId, ref value, settingsFontName);

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
                        // ⚠ Not for an echo of our own word-cut text: restoring the engine's wrap
                        // there let it re-cut our explicit lines by character in a box the
                        // layout had just shrunk to them (bench: a Thai label in three pieces).
                        if (TranslatorCore.TryGetPresentedLogical(value) == null)
                            RestoreIfFlagged(instance, compId, prop);
                        // A word reflow queued a moment ago by PresentSyllabic is this text's,
                        // not a leftover: only an RTL reflow is stale here.
                        if (_reflows.TryGetValue(compId, out var queued) && queued.Kind != ReflowKind.UGuiWords)
                            _reflows.Remove(compId);
                        return;
                    }

                    // Our own shaped output coming back: a scanner refresh, or Apply in the
                    // Fonts tab re-setting every text. The TEXT needs nothing — the ALIGNMENT
                    // choice may have changed since it was presented (per font, per rule), and
                    // this round-trip is the only way a new choice reaches a component the game
                    // never re-sets on its own. Without it, "Keep game's" chosen on a screen of
                    // static buttons changed nothing on that screen (user: "a dead option").
                    // ⚠ UI Toolkit reads its alignment from the resolved style, which an element
                    // showing our text has had for a while — safe here, unlike at first set.
                    bool mirrorNow = TranslatorCore.ShouldMirrorRtlAlignment(settingsFontName, overrideRule);
                    if (UIToolkitSupport.IsTextElementInstance(instance))
                        UIToolkitSupport.MirrorAlign(instance, mirrorNow);
                    else
                        MirrorAlignment(instance, compId, mirrorNow);

                    if (value.IndexOf("<u", StringComparison.OrdinalIgnoreCase) >= 0 && _underlineDropBudget > 0)
                    {
                        // ...and still carrying an underline the guard should have removed. Worth
                        // a line while this engine's underline defect is being characterised: it
                        // would mean a write reached the element without going through the guard.
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
                    // 🔴 Two passes, on purpose, and the engine's own lines. An immediate cut at
                    // the box's current width was tried and it is wrong by construction on uGUI:
                    // a box is routinely sized BY its text (ContentSizeFitter, layout groups on
                    // preferred width — a label 18 units wide showing "Vessel Amount:" in full),
                    // so the width seen before the text is laid out is the previous content's,
                    // the cut shrinks the box to its own lines, and every later look sees a
                    // width that is stable and false. Assigning the shaped LOGICAL string first
                    // lets the layout size the box for the whole text exactly as it does for
                    // the game's own; the reflow then reads the lines the engine produced there.
                    // ⚠ Measured with the component's OWN wrap mode: a reused box still wears
                    // the Overflow our previous lines needed, and measured under it the engine
                    // answers "one line" for any paragraph — the second text shown in a
                    // description box came out unwrapped. Restored here, taken again after the
                    // reflow (same remember-and-restore as the alignment).
                    RestoreRewrap(instance, compId);
                    // The alignment does not depend on the lines: mirrored with the text, not
                    // at the end of the reflow.
                    MirrorAlignment(instance, compId, mirror);
                    QueueReflow(instance, compId, ref value, ReflowKind.UGuiText, mirror, "logical+reflow");
                    return;
                }

                // NGUI (or a lookalike carrying processedText): same two-pass shape — the engine
                // wraps the assigned string itself, processedText hands back the result with the
                // '\n' it inserted.
                if (ProcessedTextProp(type) != null)
                {
                    MirrorAlignment(instance, compId, mirror);
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

                    // ⚠ The alignment is NOT mirrored here, unlike the other engines. UI Toolkit
                    // resolves styles at the panel update, so at set_text time — a fresh element,
                    // a first frame — resolvedStyle.unityTextAlign is the default (UpperLeft),
                    // not what the stylesheet says: mirrored from that, every centred button
                    // label landed top-right, outside its frame (bench, tim2). The mirror is
                    // taken with the finish, one frame later, when the resolved style is real.

                    // 🔴 Two passes here too, and for the same reason as UI.Text (§7.10): the
                    // width to cut at is the one the layout gives THIS text, which does not
                    // exist before the text is assigned — an element sized by its content still
                    // wears the previous text's width. An immediate cut at contentRect was
                    // right for a fixed box only, and nothing can tell the two apart from here.
                    // So: the VISUAL form goes on screen — right already for anything that fits
                    // on one line, which is most labels — with the element's own wrap mode put
                    // back (a reused element still wears our NoWrap; measured under it the
                    // engine says "one line" for any paragraph); the layout runs at the end of
                    // this frame; the next tick's fast lane, or the budgeted walk for anything
                    // not laid out by then, measures the shaped logical form against the width
                    // the element actually got and writes the per-line visual form.
                    UIToolkitSupport.RestoreWrap(instance);
                    string shapedNow = RtlComposer.ShapeLogicalOnly(value);
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
        /// The two presentation stages of the South and South-East Asian scripts, before the
        /// RTL work: word boundaries for the scripts written without spaces (WordBreaker), then
        /// the pre-base vowel signs put where they are drawn (IndicReorderer). Codepoint moves
        /// and zero-width marks only, so they touch no engine — except one that shapes natively
        /// (UI Toolkit's Advanced Text Generator), where a sign already moved would move twice.
        ///
        /// 🔴 Break BEFORE reorder: the dictionaries are spelt in storage order, a Myanmar text
        /// reordered first matches nothing. And one registration for the whole result, as
        /// presented text (D8): the gates then refuse to learn it, the in-game editor recovers
        /// the logical string behind it — and it is how our own output is told apart when it
        /// comes back through the setter (a scanner refresh, an Apply). The reorder is not
        /// idempotent: a moved sign sits after the previous syllable's consonant exactly like
        /// an unmoved one would, so an echo re-read would move it again. Asked once, first.
        /// </summary>
        private static void PresentSyllabic(object instance, long compId, ref string value, string settingsFontName)
        {
            bool needsBreak = WordBreaker.NeedsBreaking(value);
            bool needsShape = OpenTypeText.NeedsShaping(value);
            bool needsReorder = IndicReorderer.NeedsReordering(value);
            if (!needsBreak && !needsReorder && !needsShape) return;
            if (TranslatorCore.TryGetPresentedLogical(value) != null) return;
            if (UIToolkitSupport.IsTextElementInstance(instance) && UIToolkitSupport.IsAtgActive(instance)) return;

            string logical = value;
            string working = value;
            if (needsBreak)
            {
                working = WordBreaker.Break(working, out string whyNot);
                if (whyNot != null && _dictionaryLogBudget > 0)
                {
                    // Said, never hidden: a text stays unwrappable, and the reason must be readable.
                    _dictionaryLogBudget--;
                    TranslatorCore.LogWarning($"[RtlPresenter] word breaking skipped — {whyNot}");
                }
            }

            // Stage B2 — the font's own OpenType tables, for a TMP component drawn by a font
            // asset of ours (FontShaping): conjuncts, half forms, reph, positioned marks, as the
            // font designed them. 🔴 Never followed by the codepoint reorder: a shaped run
            // already carries its pre-base signs in visual order, and the reorder would move
            // one from the syllable it belongs to into the one before it (कि + क: the sign now
            // sits AFTER a consonant that is not its own). Every other engine — UI.Text on an
            // OS font, UI Toolkit, a game font we do not control — keeps stage C's reorder,
            // the most a font we cannot read can take.
            bool shaped = false;
            if (needsShape && TypeHelper.TMP_TextType != null && TypeHelper.TMP_TextType.IsInstanceOfType(instance))
            {
                var asset = ShapingFontAsset.ForSettings(settingsFontName);
                if (asset != null)
                {
                    string s = OpenTypeText.Shape(working, asset.Font, asset);
                    if (!ReferenceEquals(s, working)) { working = s; shaped = true; }
                }
            }
            if (needsReorder && !shaped)
                working = IndicReorderer.Reorder(working);

            if (ReferenceEquals(working, logical) || working == logical) return;
            TranslatorCore.RegisterPresentedText(working, logical);
            Log(compId, (needsBreak ? "words+" : "") + (shaped ? "opentype" : needsReorder ? "indic" : "none"), logical, working);
            value = working;

            // UI.Text does not break a line on U+200B (bench: a Thai paragraph cut inside its
            // words with the boundaries in place). Same two passes as its RTL text: the engine
            // lays the string out and sizes the box, then the reflow cuts the text on its
            // boundaries against that width with the engine's own character advances, the
            // engine's wrapping held off while those lines are displayed. TMP and UI Toolkit
            // break on the boundary themselves.
            if (needsBreak && compId != -1 && working.IndexOf(WordBreaker.ZeroWidthSpace) >= 0
                && TypeHelper.UI_TextType != null && TypeHelper.UI_TextType.IsAssignableFrom(instance.GetType()))
            {
                RestoreRewrap(instance, compId);
                _reflows[compId] = new Reflow
                {
                    Comp = new WeakReference(instance),
                    Logical = logical,
                    Assigned = working,
                    Measure = working,
                    Mirror = false,
                    Kind = ReflowKind.UGuiWords,
                };
            }
        }

        private static int _dictionaryLogBudget = 3;

        /// <summary>
        /// Finish a UI Toolkit element once its layout has run: cut its measuring form at the
        /// width it got, write the final per-line visual text, and hold the engine off from
        /// wrapping those lines again. False when it still has no layout — the element stays
        /// pending for a later pass. Called from UIToolkitSupport (the frame-after fast lane,
        /// then the budgeted walk).
        /// </summary>
        internal static bool FinishUiToolkitPending(object element, string logicalSource, string logical,
                                                    string measure, string assigned)
        {
            // The game moved on to another text — nothing left to finish.
            if (UIToolkitSupport.GetElementText(element) != assigned) return true;

            var lines = UIToolkitSupport.TryBreakLines(element, measure, out _);
            if (lines == null) return false;

            string final = ComposeLines(lines);
            TranslatorCore.RegisterPresentedText(final, logicalSource);
            UIToolkitSupport.SetElementTextSilently(element, final);
            UIToolkitSupport.DisableWrap(element);
            Log(-1, "walk/final/uitk", logical, final);
            return true;
        }

        #region Deferred reflow (pass 2) — UI.Text, NGUI

        // UGuiWords: a UI.Text holding word boundaries (U+200B) the engine does not break on —
        // Thai, Lao, Khmer, Myanmar. Cut on those boundaries by us, against the box width, with
        // the engine's own advance per character (BuildUGuiWordLines).
        private enum ReflowKind { UGuiText, UGuiWords, Ngui }

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
        private static PropertyInfo _generatorCharsProp;     // TextGenerator.characters -> IList<UICharInfo>
        private static FieldInfo _charWidthField;            // UICharInfo.charWidth
        private static PropertyInfo _supportRichTextProp;   // Text.supportRichText
        // The redraw gate (WillBeRedrawn): Graphic.canvas, Graphic.canvasRenderer, CanvasRenderer.cull.
        private static PropertyInfo _canvasProp;
        private static PropertyInfo _canvasRendererProp;
        private static PropertyInfo _cullProp;
        // The last-resort cut, for a drawn component whose generator never catches up:
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

                    // 🔴 A component the engine will not REDRAW has no fresh line data and never
                    // will until it shows — and "redraw" is wider than "active". Games preload
                    // hidden panes (a guide fills every page up front): inactive ones, but also
                    // pages under a disabled Canvas or clipped away by a RectMask2D, which stay
                    // active in the hierarchy while Graphic.Rebuild skips them outright
                    // (canvasRenderer.cull). Their generator kept describing the PREVIOUS text
                    // for as long as they stayed out of view; spending the attempts there turned
                    // every hidden page into the fallback's reversed line stack, or into a cut
                    // made at a box width the layout had not recomputed yet (bench: a 3-letter
                    // label on two rows, a section title under its own list). Wait, without
                    // spending attempts — staleness is already covered by the text check above.
                    if (!WillBeRedrawn(comp, entry.Kind))
                        continue;

                    string final = BuildLines(entry, comp, out string whyNot);
                    if (final == null)
                    {
                        // Line source not ready (or unreadable). The engine rebuilds a drawn
                        // component at the end of the frame its text changed, so two ticks is
                        // what a stale generator legitimately needs; a third strike means this
                        // component's rendering never feeds the generator we read (a Text
                        // subclass drawing its own way). Then, and only then, the engine is asked
                        // directly — a Populate of our own at the width the layout has by now
                        // settled for THIS text — and whole-string visual order stays the last
                        // resort. Either way SAY so: a silent fallback made the reversed line
                        // stack undiagnosable from a screenshot.
                        if (++entry.Attempts < 3) continue;
                        string whyOwn = null;
                        if (entry.Kind == ReflowKind.UGuiText)
                            final = BuildUGuiLinesNow(comp, entry.Measure, out whyOwn);
                        else if (entry.Kind == ReflowKind.UGuiWords)
                            final = BuildUGuiWordLines(comp, entry.Assigned, out whyOwn);
                        if (_fallbackLogBudget > 0)
                        {
                            _fallbackLogBudget--;
                            if (final != null)
                                TranslatorCore.LogWarning($"[RtlPresenter] engine lines never caught up ({whyNot}) — cut with our own generator at the box's settled width: comp={id}");
                            else
                                TranslatorCore.LogWarning($"[RtlPresenter] reflow gave up ({whyNot}{(whyOwn != null ? " | populate: " + whyOwn : "")}) — whole-string visual order, line stack may read bottom-up: comp={id}");
                        }
                        if (final == null) final = RtlComposer.Compose(entry.Logical, RtlOutput.VisualOrder);
                    }

                    TranslatorCore.RegisterPresentedText(final, entry.Logical);
                    Log(id, "reflow/final", entry.Logical, final);

                    {
                        // 🔴 WE computed the line breaks — the engine must not wrap again. A
                        // recomposed line is exactly as wide as the rect it was cut against, and
                        // the rendering rounding re-wrapped it: the overflowing visual chunk is
                        // the sentence's FIRST word, shoved onto its own row (bioc bench,
                        // «تحوّل»). Original wrap mode remembered and restored like the
                        // alignment. NGUI has no such knob; its wrap re-measures the same glyph
                        // advances deterministically (no rendering rounding), so a trimmed line
                        // that fitted keeps fitting — bench holds the proof burden there.
                        if (entry.Kind == ReflowKind.UGuiText || entry.Kind == ReflowKind.UGuiWords)
                            DisableRewrap(comp, id);

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

        // ⚠ No re-cut on box resize. It was tried (a Graphic.OnRectTransformDimensionsChange
        // hook) and it is circular by construction: a ContentSizeFitter sizes the box from the
        // text, the re-cut sizes the text from the box — the description of an organ shrank to
        // one character per line and locked there; a disclaimer grew to one line as wide as its
        // paragraph. A cut is made once, at the width the game gave the box for its own text,
        // and the engine's wrapping (kept on) folds what would not fit.
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
                    // The engine's own layout of the assigned text, and nothing else here: it was
                    // made in the box the layout gave THAT text. A generator still describing an
                    // older string is a reason to wait, never to cut at the box's current width —
                    // on uGUI a box is routinely sized by its text, so before the layout has run
                    // for the new string that width is the previous content's (bench: a 3-letter
                    // label on two rows, a paragraph re-cut at eight different widths). The
                    // caller's give-up path is where our own Populate comes in, once the layout
                    // has had its frames.
                    return BuildPerLineVisual(comp, entry.Measure, out whyNot);
                case ReflowKind.UGuiWords:
                    // The engine must have laid the assigned text out first — same reason as
                    // above, the box has its width for THIS text only then; its generator saying
                    // so is the proof. Then the text is cut on its word boundaries against that
                    // width, with the engine's own advance per character.
                    if (BuildPerLineVisual(comp, entry.Assigned, out whyNot) == null) return null;
                    return BuildUGuiWordLines(comp, entry.Assigned, out whyNot);
                default:
                    return BuildNguiLines(comp, entry.Measure, out whyNot);
            }
        }

        /// <summary>
        /// Will the engine rebuild this component's geometry at the end of the frame — and so
        /// refresh the line data the reflow reads? For UI.Text that is Graphic.Rebuild's own
        /// gate: a Behaviour that is active and enabled, under an active and enabled Canvas
        /// (Graphic.canvas is null otherwise), and not culled by a clipping mask
        /// (canvasRenderer.cull — Rebuild returns before UpdateGeometry on it). NGUI computes
        /// processedText from its own state, so only the hierarchy matters there. Anything not
        /// readable answers true: the attempt counter, not a silent wait, is the safety net.
        /// </summary>
        private static bool WillBeRedrawn(object comp, ReflowKind kind)
        {
            if (!(comp is UnityEngine.Component c) || c.gameObject == null) return true;
            if (!c.gameObject.activeInHierarchy) return false;
            if (kind == ReflowKind.Ngui) return true;
            if (comp is UnityEngine.Behaviour b && !b.isActiveAndEnabled) return false;
            EnsureGeneratorPlumbing();
            try
            {
                if (_canvasProp != null && _canvasProp.GetValue(comp, null) == null) return false;
                if (_cullProp != null && _canvasRendererProp != null)
                {
                    object renderer = _canvasRendererProp.GetValue(comp, null);
                    if (renderer != null && (bool)_cullProp.GetValue(renderer, null)) return false;
                }
            }
            catch { }
            return true;
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

        /// <summary>
        /// The generator API and the redraw gate, resolved once. Shared by the cached-generator
        /// read, our own Populate and <see cref="WillBeRedrawn"/>: it used to live inside the
        /// cached-generator path only, so any other caller saw "API not resolvable" until that
        /// path had run first.
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
                _canvasProp = TypeHelper.UI_TextType.GetProperty("canvas", BindingFlags.Public | BindingFlags.Instance);
                _canvasRendererProp = TypeHelper.UI_TextType.GetProperty("canvasRenderer", BindingFlags.Public | BindingFlags.Instance);
                _cullProp = _canvasRendererProp?.PropertyType.GetProperty("cull", BindingFlags.Public | BindingFlags.Instance);
                var genType = _cachedGeneratorProp?.PropertyType;
                _generatorLinesProp = genType?.GetProperty("lines", BindingFlags.Public | BindingFlags.Instance);
                _generatorCharCountProp = genType?.GetProperty("characterCount", BindingFlags.Public | BindingFlags.Instance);
                _generatorCharsProp = genType?.GetProperty("characters", BindingFlags.Public | BindingFlags.Instance);
                if (_generatorCharsProp != null)
                {
                    var charsType = _generatorCharsProp.PropertyType;
                    if (charsType.IsGenericType && charsType.GetGenericArguments().Length == 1)
                        _charWidthField = charsType.GetGenericArguments()[0].GetField("charWidth", BindingFlags.Public | BindingFlags.Instance);
                }
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


        // Everything a last-resort cut rests on, for the first few of a session: the box as the
        // engine sees it (rectTransform.rect — what Text.OnPopulateMesh cuts with) against the
        // pixel-adjusted rect, the component's own wrapping and best-fit settings, and how many
        // lines came out. Added when labels came out in three pieces with a "stable" width that
        // could not be the engine's — the log has to say which of these differs.
        private static int _cutDescribeBudget = 60;

        private static void DescribeCut(object comp, string assigned, UnityEngine.Rect pixelRect, object settings, string cut)
        {
            if (_cutDescribeBudget <= 0 || !TranslatorCore.DebugMode) return;
            _cutDescribeBudget--;
            try
            {
                string rectSize = "?";
                var rtProp = comp.GetType().GetProperty("rectTransform", BindingFlags.Public | BindingFlags.Instance);
                if (rtProp?.GetValue(comp, null) is UnityEngine.RectTransform rt) rectSize = $"{rt.rect.width:F1}x{rt.rect.height:F1}";
                var t = settings.GetType();
                object F(string name) { try { return t.GetField(name)?.GetValue(settings); } catch { return "?"; } }
                int lines = cut == null ? -1 : cut.Split('\n').Length;
                string preview = assigned.Length > 24 ? assigned.Substring(0, 24) + "…" : assigned;
                TranslatorCore.LogDebug($"[RtlPresenter] cut comp={TypeHelper.GetInstanceID(comp)} {(comp is UnityEngine.Component cc && cc.gameObject != null ? (cc.gameObject.activeInHierarchy ? "active" : "INACTIVE") : "?")} pixelRect={pixelRect.width:F1}x{pixelRect.height:F1} rect={rectSize} "
                    + $"hOverflow={F("horizontalOverflow")} vOverflow={F("verticalOverflow")} bestFit={F("resizeTextForBestFit")} "
                    + $"size={F("fontSize")} min={F("resizeTextMinSize")} max={F("resizeTextMaxSize")} scale={F("scaleFactor")} "
                    + $"→ {lines} line(s) for {assigned.Length} chars '{preview}'");
            }
            catch (Exception ex) { TranslatorCore.LogDebug("[RtlPresenter] cut describe failed: " + ex.Message); }
        }

        /// <summary>
        /// Ask the engine where it would cut this text in this component's box, with a generator
        /// of our own (the component's belongs to its rendering) — the UI.Text counterpart of
        /// UI Toolkit's MeasureTextSize. ⚠ Only right once the layout has run for THIS text: the
        /// width it cuts at is the box's current one, and a box sized by its content still wears
        /// the previous text's width until then. Hence its place: the give-up branch of the
        /// reflow, after a drawn component has had its frames. Null when the API or the layout
        /// is not there.
        /// </summary>
        /// <summary>
        /// A word-broken text cut on its boundaries (U+200B, space) against the box width, with
        /// the engine's own advance per character: our generator lays the text out on one line
        /// (both overflows on) and reports every character's width, in the same generation
        /// pixels as the box width times the scale factor. Greedy: a line ends at the last
        /// boundary before the width runs out; a stretch with no boundary is cut where the
        /// engine would have cut it. ⚠ Measured on the REAL string: a spaced copy was one
        /// space too wide per boundary, and a label that fitted its content-sized box exactly
        /// came out on two lines (bench: a Thai "Vessels" label in three pieces).
        /// The boundaries are dropped from the result — nothing to break on once the lines are
        /// explicit — and the engine's wrapping is held off while they show.
        /// </summary>
        private static string BuildUGuiWordLines(object comp, string assigned, out string whyNot)
        {
            whyNot = null;
            EnsureGeneratorPlumbing();
            if (_generatorPopulate == null || _getGenerationSettings == null || _getPixelAdjustedRect == null
                || _ownGenerator == null || _generatorCharsProp == null || _charWidthField == null)
            { whyNot = "generator character widths not readable on this runtime"; return null; }
            if (assigned.IndexOf('<') >= 0)
            { whyNot = "rich text tags in a word-broken text — not cut"; return null; }

            try
            {
                object rect = _getPixelAdjustedRect.Invoke(comp, null);
                if (!(rect is UnityEngine.Rect r) || r.width < 1f)
                { whyNot = "no layout yet (component has no width)"; return null; }

                object settings = _getGenerationSettings.Invoke(comp, new object[] { r.size });
                if (settings == null) { whyNot = "no generation settings"; return null; }
                var st = settings.GetType();
                // One line, every character: both overflows on. HorizontalWrapMode.Overflow = 1,
                // VerticalWrapMode.Overflow = 1.
                var hField = st.GetField("horizontalOverflow", BindingFlags.Public | BindingFlags.Instance);
                var vField = st.GetField("verticalOverflow", BindingFlags.Public | BindingFlags.Instance);
                if (hField != null) hField.SetValue(settings, Enum.ToObject(hField.FieldType, 1));
                if (vField != null) vField.SetValue(settings, Enum.ToObject(vField.FieldType, 1));
                float scale = 1f;
                var scaleField = st.GetField("scaleFactor", BindingFlags.Public | BindingFlags.Instance);
                if (scaleField != null) scale = Convert.ToSingle(scaleField.GetValue(settings));

                if (!(bool)_generatorPopulate.Invoke(_ownGenerator, new object[] { assigned, settings }))
                { whyNot = "generator refused to populate"; return null; }
                var chars = _generatorCharsProp.GetValue(_ownGenerator, null) as System.Collections.IList;
                if (chars == null || chars.Count < assigned.Length)
                { whyNot = $"generator reports {chars?.Count ?? 0} characters for {assigned.Length}"; return null; }

                float limit = r.width * scale;
                var sb = new System.Text.StringBuilder(assigned.Length + 8);
                int lineStart = 0;
                float lineWidth = 0f;
                int lastBoundary = -1;   // index of the boundary character on this line, or -1
                for (int i = 0; i < assigned.Length; i++)
                {
                    char c = assigned[i];
                    if (c == '\n')
                    {
                        AppendLine(sb, assigned, lineStart, i);
                        sb.Append('\n');
                        lineStart = i + 1; lineWidth = 0f; lastBoundary = -1;
                        continue;
                    }
                    float w = Convert.ToSingle(_charWidthField.GetValue(chars[i]));
                    bool boundary = c == WordBreaker.ZeroWidthSpace || c == ' ';
                    if (lineWidth + w > limit && i > lineStart)
                    {
                        bool cutOnBoundary = lastBoundary >= lineStart;
                        int cutAt = cutOnBoundary ? lastBoundary : i;
                        AppendLine(sb, assigned, lineStart, cutAt);
                        sb.Append('\n');
                        lineStart = cutOnBoundary ? cutAt + 1 : cutAt;
                        lastBoundary = -1;
                        // Width of what already sits on the new line, this character included.
                        lineWidth = 0f;
                        for (int k = lineStart; k <= i; k++) lineWidth += Convert.ToSingle(_charWidthField.GetValue(chars[k]));
                        if (boundary) lastBoundary = i;
                        continue;
                    }
                    lineWidth += w;
                    if (boundary) lastBoundary = i;
                }
                AppendLine(sb, assigned, lineStart, assigned.Length);
                return sb.ToString();
            }
            catch (Exception ex) { whyNot = "word cut failed: " + ex.Message; return null; }
        }

        /// <summary>One cut line into the result, its zero-width boundaries dropped and its trailing space too.</summary>
        private static void AppendLine(System.Text.StringBuilder sb, string text, int start, int end)
        {
            while (end > start && text[end - 1] == ' ') end--;
            for (int i = start; i < end; i++)
                if (text[i] != WordBreaker.ZeroWidthSpace) sb.Append(text[i]);
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

                string cut = BuildPerLineVisual(comp, assigned, out whyNot, _ownGenerator);
                DescribeCut(comp, assigned, r, settings, cut);
                return cut;
            }
            catch (Exception ex) { whyNot = "populate failed: " + ex.Message; return null; }
        }

        /// <summary>
        /// The assigned string re-cut at a generator's break points, each line converted to
        /// visual order — the component's own cachedTextGenerator unless <paramref name="populated"/>
        /// hands over ours. Null, with the reason, when the generator cannot be read or does not
        /// describe this text yet.
        /// </summary>
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
            // "Keep the game's": not merely nothing to do — a component mirrored under an
            // earlier choice gets its own alignment back, or the choice is dead on screen.
            if (!mirror) { RestoreAlignment(comp, compId); return; }
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
            if (prop != null && _flaggedOriginal.TryGetValue(compId, out bool original))
            {
                _flaggedOriginal.Remove(compId);
                try { prop.SetValue(instance, original, null); } catch { }
            }
            RestoreAlignment(instance, compId);
            RestoreRewrap(instance, compId);
        }

        /// <summary>The alignment a component had before <see cref="MirrorAlignment"/>, put back.</summary>
        private static void RestoreAlignment(object instance, long compId)
        {
            if (compId == -1 || !_alignedOriginal.TryGetValue(compId, out object anchor)) return;
            _alignedOriginal.Remove(compId);
            try
            {
                var alignProp = instance.GetType().GetProperty("alignment", BindingFlags.Public | BindingFlags.Instance);
                alignProp?.SetValue(instance, anchor, null);
            }
            catch { }
        }

        /// <summary>The wrap mode a component had before <see cref="DisableRewrap"/>, put back.</summary>
        private static void RestoreRewrap(object instance, long compId)
        {
            if (compId == -1 || !_wrapOriginal.TryGetValue(compId, out object wrap)) return;
            _wrapOriginal.Remove(compId);
            try
            {
                var wrapProp = instance.GetType().GetProperty("horizontalOverflow", BindingFlags.Public | BindingFlags.Instance);
                wrapProp?.SetValue(instance, wrap, null);
            }
            catch { }
        }

        private static void Log(long compId, string mode, string logical, string composed)
        {
            // ⚠ Bench diagnostic (to remove with RtlProbe): the exact code-point order of what
            // came in and what went out. A terminal renders Arabic with its own bidi, so the
            // summary line below cannot tell "the composer reordered this" from "my viewer did".
            if (TranslatorCore.DebugMode && _dumpBudget > 0)
            {
                _dumpBudget--;
                TranslatorCore.LogDebug($"[RtlPresenter] comp={compId} mode={mode} in : {Escape(logical)}");
                TranslatorCore.LogDebug($"[RtlPresenter] comp={compId} mode={mode} out: {Escape(composed)}");
            }
            if (_logBudget <= 0) return;
            _logBudget--;
            TranslatorCore.LogDebug($"[RtlPresenter] comp={compId} mode={mode} " +
                $"'{(logical.Length > 30 ? logical.Substring(0, 30) + "…" : logical)}' → shaped ({composed.Length} ch)");
        }

        private static int _dumpBudget = 300;

        private static string Escape(string s)
        {
            var b = new System.Text.StringBuilder(s.Length * 2);
            foreach (char c in s)
            {
                if (c < 128 && c != '\n') b.Append(c);
                else if (c == '\n') b.Append("\\n");
                else b.Append('<').Append(((int)c).ToString("x4")).Append('>');
            }
            return b.ToString();
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
