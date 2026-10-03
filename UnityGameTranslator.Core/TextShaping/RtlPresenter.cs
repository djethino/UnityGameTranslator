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

        // Components whose text went through Present, and those just enabled still wearing a
        // template's presented form without having done so (NoteEnabled → AdoptCopies).
        private static readonly HashSet<long> _presented = new HashSet<long>();
        private static readonly List<object> _appeared = new List<object>();
        private static readonly List<object> _adoptScratch = new List<object>();

        /// <summary>
        /// A text component enabled (Graphic.OnEnable, TMP's OnEnable). A COPY of a template is
        /// born with the template's text — our presented form — and nothing else: a template is no
        /// scene object, so it keeps the game's font, alignment and wrapping (TypeHelper.IsInScene),
        /// and a game that never writes the copy again left it that way (2026-10-02): a Hebrew
        /// row aligned the game's way, a Hindi row drawn with nothing at all — its shaped glyphs
        /// exist only in the replacement font the copy never got. Such a copy is noted here and
        /// presented on its own at the next pass (AdoptCopies) — not inside OnEnable, where the
        /// game is about to give it its own text, font and material.
        /// </summary>
        internal static void NoteEnabled(object instance)
        {
            if (instance == null || !TranslatorCore.IsMainThread) return;
            long compId = TypeHelper.GetInstanceID(instance);
            if (compId == -1 || _presented.Contains(compId)) return;
            string text = TypeHelper.GetText(instance);
            if (string.IsNullOrEmpty(text)) return;
            // Ours, and not its own logical text: a form only a presentation produces.
            string logical = TranslatorCore.TryGetPresentedLogical(text);
            if (logical == null || logical == text) return;
            _appeared.Add(instance);
        }

        /// <summary>
        /// The copies noted by <see cref="NoteEnabled"/> that still show a template's form: each
        /// written its logical text through the setter, as the game would have — presented,
        /// mirrored and laid out like any text. One the game wrote meanwhile is already done.
        /// </summary>
        private static void AdoptCopies()
        {
            if (_appeared.Count == 0) return;
            _adoptScratch.Clear();
            _adoptScratch.AddRange(_appeared);
            _appeared.Clear();
            foreach (var comp in _adoptScratch)
            {
                if (comp == null || (comp is UnityEngine.Object uo && uo == null)) continue;
                long compId = TypeHelper.GetInstanceID(comp);
                if (_presented.Contains(compId) || !TypeHelper.IsInScene(comp)) continue;
                string text = TypeHelper.GetText(comp);
                string logical = string.IsNullOrEmpty(text) ? null : TranslatorCore.TryGetPresentedLogical(text);
                if (logical == null) continue;
                TraceReflow(compId, "copy of a template adopted", comp);
                try { TypeHelper.SetText(comp, logical); }
                catch (Exception ex) { Faults.Say("RtlPresenter.AdoptCopies", ex, comp.GetType().Name); }
            }
        }

        // How many times Present has been entered — main thread only, like everything here. A
        // setter prefix compares it before and after its body to know whether the write it just
        // handled went through Present (see ReleaseIfNotPresented).
        internal static int PresentCount;

        /// <summary>
        /// One form written to a component, said twice: as our own output (the gates refuse to
        /// learn it — D8), and as what this component now holds for its translation, which is what
        /// a game reads back when it appends to a text (TranslatorPatches.DetectReadBack).
        /// </summary>
        private static void RegisterShown(long compId, string presented, string logical)
        {
            TranslatorCore.RegisterPresentedText(presented, logical);
            TranslatorCore.Router.NotePresented(compId, logical, presented);
            TextTrace.Shown(compId, logical, presented);
        }

        /// <summary>
        /// A write that left a setter prefix WITHOUT going through <see cref="Present"/>: the
        /// translations switched off, the font's translation off, a text skipped or not to be
        /// translated. Present is what gives a component its own right-to-left flag, alignment
        /// and wrap back when it moves on to a left-to-right text — so a component we had turned
        /// right-to-left kept that state under the game's own English, which then read backwards
        /// (switching translation off showed "KNUJ" for "JUNK").
        /// Cheap for every other write: three dictionary lookups on components we never touched.
        /// </summary>
        internal static void ReleaseIfNotPresented(object instance, string value, int presentCountBefore)
        {
            if (PresentCount != presentCountBefore || instance == null || string.IsNullOrEmpty(value)) return;
            if (!TranslatorCore.IsMainThread) return;
            try
            {
                long compId = TypeHelper.GetInstanceID(instance);
                if (compId == -1) return;
                if (!_flaggedOriginal.ContainsKey(compId) && !_alignedOriginal.ContainsKey(compId)
                    && !_wrapOriginal.ContainsKey(compId)) return;
                // Our own output coming back (an echo, a reflowed line) keeps the state it needs —
                // the same two tests Present applies before restoring.
                if (RtlText.ContainsPresentationForms(value) || TranslatorCore.TryGetPresentedLogical(value) != null)
                    return;
                RestoreIfFlagged(instance, compId, RtlProp(instance));
                if (_reflows.TryGetValue(compId, out var queued) && queued.Kind != ReflowKind.UGuiWords)
                {
                    TraceReflow(compId, "removed: a write not presented");
                    _reflows.Remove(compId);
                }
            }
            catch (Exception ex) { TranslatorCore.LogDebug($"[RtlPresenter] release failed: {ex.Message}"); }
        }

        /// <summary>
        /// Present one outgoing string in place. Cheap for the overwhelming majority of texts:
        /// one range scan says "nothing to do".
        /// </summary>
        internal static void Present(object instance, long compId, ref string value,
                                     string settingsFontName = null, FontOverrideRule overrideRule = null, bool ownUi = false)
        {
            if (string.IsNullOrEmpty(value)) return;

            // The composer and shaper sit on shared buffers — off the main thread, leave the
            // logical text alone rather than corrupt another call's.
            if (!TranslatorCore.IsMainThread) return;
            PresentCount++;
            if (compId != -1) _presented.Add(compId);

            // Every game text goes out through here, with its font and still logical: the account
            // of what each font must be able to draw (FontManager.Coverage).
            FontManager.NoteTextDrawn(compId, settingsFontName, value);

            long tPerf = Perf.Start();
            try
            {
                // Asked of the text AS IT ARRIVED: PresentSyllabic registers its own shaped form
                // just below, which is no echo — the right-to-left step still has to lay it out.
                string arrivedLogical = TranslatorCore.TryGetPresentedLogical(value);
                bool ownEcho = arrivedLogical != null && arrivedLogical != value;

                // Presented again from its logical text: what arrived was ours, but for another
                // font — no longer an echo to keep.
                if (PresentSyllabic(instance, compId, ref value, settingsFontName, ownUi)) ownEcho = false;

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
                            RestoreIfFlagged(instance, compId, prop,
                                             keepMirrored: !ownUi && MirrorsEveryText(settingsFontName, overrideRule));
                        // A word reflow queued a moment ago by PresentSyllabic is this text's,
                        // not a leftover: only an RTL reflow is stale here.
                        if (_reflows.TryGetValue(compId, out var queued) && queued.Kind != ReflowKind.UGuiWords)
                        {
                            TraceReflow(compId, "removed: left-to-right text");
                            _reflows.Remove(compId);
                        }
                        return;
                    }

                    KeepEcho(instance, compId, value, settingsFontName, overrideRule);
                    return;
                }

                // Our own composed output coming back when nothing in it says so: Hebrew, Adlam and
                // the other scripts with no presentation forms read as fresh logical text, and
                // composing it again reversed every left-to-right run inside it once more — a
                // Latin word or a number in a Hebrew line flipped at each refresh ("Unity" →
                // "ytinU", "2.5" → "5.2"; bench, mixed corpus, 2026-10-01). The index of what we
                // presented knows it; a text whose presented form IS its logical one composes to
                // itself, so it needs no exception.
                if (ownEcho)
                {
                    KeepEcho(instance, compId, value, settingsFontName, overrideRule);
                    return;
                }

                bool mirror = TranslatorCore.ShouldMirrorRtlAlignment(settingsFontName, overrideRule);

                DescribeFont(instance, compId, settingsFontName);

                if (prop != null)
                {
                    // 🔴 Never the flag on a TEMPLATE: every copy is born with it, and the game's own
                    // text written into a copy — a Latin word, a resolution, a count, set while the
                    // copy is still inactive, so before anything of ours sees it — read backwards:
                    // "VSync" as "cnySV", "3440x1440" as "0441x0443" (2026-10-02, a settings screen;
                    // proven by the log, then on the bench). This component's flag was never ours to
                    // record, so nothing could put it back. The template gets the visual form like
                    // any other engine's (no state, right for a one-line label from the copy's first
                    // frame), and each copy is presented on its own — by the game's write, or by
                    // AdoptCopies when it never writes it.
                    if (PresentTemplate(instance, compId, ref value)) return;
                    string flagged = RtlComposer.Compose(value, RtlOutput.RtlFlagged);
                    if (compId != -1 && !_flaggedOriginal.ContainsKey(compId))
                    {
                        bool original = false;
                        try { original = prop.GetMethod != null && (bool)prop.GetValue(instance, null); } catch (Exception ex) { Faults.Say("RtlPresenter.Present flag read", ex); }
                        _flaggedOriginal[compId] = original;
                    }
                    try { prop.SetValue(instance, true, null); } catch (Exception ex) { Faults.Say("RtlPresenter.Present flag write", ex); }
                    MirrorAlignment(instance, compId, mirror);
                    RegisterShown(compId, flagged, value);
                    Log(compId, "flagged", value, flagged);
                    // 🔴 The engine wraps the flagged form in ITS order, where a left-to-right run is
                    // written backwards: a run of several words crossing a line end had its words
                    // swapped between the lines ("…של Schedule I?" shown "…של I" / "?Schedule",
                    // 2026-10-01). Only that case: the lines are then cut by us, next frame, in
                    // logical order, against the width the layout gave this text and with TMP's own
                    // ruler (BuildTmpLines). A text with no such run is the engine's to wrap.
                    // The same text again (a game re-setting its labels every frame) in a box of the
                    // same width: the lines already cut for it, at once — re-queueing would flip the
                    // screen between the engine's wrap and ours each frame and measure again each time.
                    bool runAcrossSpace = compId != -1 && RtlComposer.HasLtrRunAcrossSpace(value);
                    if (runAcrossSpace && _tmpLines.TryGetValue(compId, out var known) && known.Logical == value
                        && TmpLayout(instance, out _, out float widthNow) && Math.Abs(widthNow - known.Width) < 0.5f)
                    {
                        if (_reflows.TryGetValue(compId, out var pending) && pending.Kind == ReflowKind.Tmp) _reflows.Remove(compId);
                        RegisterShown(compId, known.Final, value);
                        value = known.Final;
                        return;
                    }
                    if (runAcrossSpace && TypeHelper.IsInScene(instance))
                        _reflows[compId] = new Reflow
                        {
                            Comp = new WeakReference(instance),
                            Logical = value,
                            Assigned = flagged,
                            Measure = flagged,
                            Mirror = mirror,
                            Kind = ReflowKind.Tmp,
                        };
                    else if (_reflows.TryGetValue(compId, out var stale) && stale.Kind == ReflowKind.Tmp)
                        _reflows.Remove(compId);
                    value = flagged;
                    return;
                }

                var type = instance.GetType();

                // TextMesh never wraps by itself: every line break is already an explicit '\n',
                // so the per-line visual conversion happens right here, no second pass needed.
                if (TypeHelper.TextMeshType != null && TypeHelper.TextMeshType.IsAssignableFrom(type))
                {
                    string perLine = ComposeVisualPerLine(value);
                    RegisterShown(compId, perLine, value);
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
                    RegisterShown(compId, final, value);
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
                    if (PresentTemplate(instance, compId, ref value)) return;
                    QueueReflow(instance, compId, ref value, ReflowKind.UGuiText, mirror, "logical+reflow");
                    return;
                }

                // NGUI (or a lookalike carrying processedText): same two-pass shape — the engine
                // wraps the assigned string itself, processedText hands back the result with the
                // '\n' it inserted.
                if (ProcessedTextProp(type) != null)
                {
                    MirrorAlignment(instance, compId, mirror);
                    if (PresentTemplate(instance, compId, ref value)) return;
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
                    // 🔴 Crash guard. TextCore's DrawUnderlineMesh died on an underline (IndexOutOfRange,
                    // a material registered while drawing — TextCoreDecorations says how) until
                    // Unity fixed it; the mod carries that fix where it can. Without it, the tag
                    // comes off: underlined Arabic links are normal typography, but not at the
                    // price of the game's panel.
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
                        if (DiagnosticOnce.First("RtlPresenter.underline", instance.GetType().Name))
                        {
                            TranslatorCore.LogWarning("[RtlPresenter] underline/strikethrough tag dropped on RTL text: this engine's DrawUnderlineMesh can crash on an underline (a Unity defect, fixed in later engines) and the mod cannot carry the fix here. The text is unaffected, and translations.json keeps the tag.");
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
                    RegisterShown(compId, visualNow, logicalSource);
                    UIToolkitSupport.DeferUntilLaidOut(instance, logicalSource, value, shapedNow, visualNow, mirror);
                    Log(compId, "visual+walk/uitk", value, visualNow);
                    value = visualNow;
                    return;
                }

                // Everything else: visual order — correct single-line. This branch is now the
                // documented EXCEPTION (unknown frameworks), not the rule, and it says so.
                string composed = RtlComposer.Compose(value, RtlOutput.VisualOrder);
                RegisterShown(compId, composed, value);
                MirrorAlignment(instance, compId, mirror);
                if (DiagnosticOnce.First("RtlPresenter.noLineSource", type.Name))
                    TranslatorCore.LogWarning($"[RtlPresenter] no line source for {type.Name} — whole-string visual order, multi-line may stack bottom-up");
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
        /// <summary>
        /// A component the mod re-fonts with a UnityEngine.Font — uGUI Text, TextMesh, and a UI Toolkit
        /// text element on its standard generator (the ATG case never reaches here) — the ones that
        /// take a derived copy (FontManager.GetUnityReplacementFont) — an NGUI label drawing a dynamic
        /// font (trueTypeFont), and a tk2d text drawing a font the mod made from one (Tk2dFonts).
        /// NGUI's bitmap UIFont does not: the mod does not replace it.
        /// </summary>
        private static bool DrawsFromLegacyFont(object instance) =>
            instance != null
            && ((TypeHelper.UI_TextType != null && TypeHelper.UI_TextType.IsInstanceOfType(instance))
                || (TypeHelper.TextMeshType != null && TypeHelper.TextMeshType.IsInstanceOfType(instance))
                || UIToolkitSupport.IsTextElementInstance(instance)
                || DrawsDynamicFont(instance)
                || Tk2dFonts.DrawsReplacement(instance));

        private static readonly Dictionary<Type, System.Reflection.PropertyInfo> _trueTypeFontProps = new Dictionary<Type, System.Reflection.PropertyInfo>();

        /// <summary>A component drawing with a dynamic UnityEngine.Font of its own (NGUI's trueTypeFont) — re-fonted by the mod like uGUI Text.</summary>
        private static bool DrawsDynamicFont(object instance)
        {
            var type = instance.GetType();
            if (!_trueTypeFontProps.TryGetValue(type, out var prop))
            {
                prop = type.GetProperty("trueTypeFont", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null && !typeof(UnityEngine.Font).IsAssignableFrom(prop.PropertyType)) prop = null;
                _trueTypeFontProps[type] = prop;
            }
            if (prop == null) return false;
            try { return prop.GetValue(instance, null) is UnityEngine.Font f && f != null; }
            catch (Exception ex) { Faults.Say("RtlPresenter.DrawsDynamicFont", ex, type.Name); return false; }
        }

        /// <summary>
        /// Our own output coming back: a scanner refresh, or Apply in the Fonts tab re-setting every
        /// text. The TEXT needs nothing — the ALIGNMENT choice may have changed since it was
        /// presented (per font, per rule), and this round-trip is the only way a new choice reaches
        /// a component the game never re-sets on its own. Without it, "Keep game's" chosen on a
        /// screen of static buttons changed nothing on that screen (user: "a dead option").
        /// ⚠ UI Toolkit reads its alignment from the resolved style, which an element showing our
        /// text has had for a while — safe here, unlike at first set.
        /// </summary>
        private static void KeepEcho(object instance, long compId, string value, string settingsFontName, FontOverrideRule overrideRule)
        {
            bool mirrorNow = TranslatorCore.ShouldMirrorRtlAlignment(settingsFontName, overrideRule);
            if (UIToolkitSupport.IsTextElementInstance(instance))
                UIToolkitSupport.MirrorAlign(instance, mirrorNow);
            else
                MirrorAlignment(instance, compId, mirrorNow);

            if (value.IndexOf("<u", StringComparison.OrdinalIgnoreCase) >= 0 && DiagnosticOnce.First("RtlPresenter.echoUnderline", compId.ToString()))
            {
                // ...and still carrying an underline the guard should have removed. Worth a line
                // while this engine's underline defect is being characterised: it would mean a
                // write reached the element without going through the guard.
                TranslatorCore.LogWarning($"[RtlPresenter] echo still carries an underline tag on comp={compId} — a write bypassed the guard");
            }
        }

        // The font each component's shaped text was made for (DrawnFor), by component — what tells
        // an echo still right from one made for a font the component no longer draws with.
        private static readonly Dictionary<long, string> _shapedFor = new Dictionary<long, string>();

        /// <summary>
        /// The route a component's text is shaped by, and a key naming the font behind it: our TMP
        /// asset, a derived copy, or none (the codepoints only reordered). Decided in one place
        /// (ShapingRoute), the one the coverage checks walk.
        /// </summary>
        private static string DrawnFor(object instance, string settingsFontName, bool ownUi,
            out ShapingRoute.Route route, out ShapingFontAsset asset, out DerivedFonts.Entry derived)
        {
            bool isTmp = TypeHelper.TMP_TextType != null && TypeHelper.TMP_TextType.IsInstanceOfType(instance);
            bool legacy = !isTmp && DrawsFromLegacyFont(instance);
            asset = isTmp ? ShapingFontAsset.ForSettings(settingsFontName) : null;
            // The mod's own window draws its interface in the interface font and the game's text it
            // shows in the source or target text font (ModWindowText); the game's text on screen in
            // its replacement.
            derived = !legacy ? null : ownUi ? FontManager.DerivedForWindow(ModWindowText.SideOf(instance)) : FontManager.DerivedForSettings(settingsFontName);
            route = ShapingRoute.Decide(isTmp, asset != null, legacy, derived != null, engineShapes: false);
            switch (route)
            {
                case ShapingRoute.Route.OurTmpAsset: return "tmp:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(asset);
                case ShapingRoute.Route.DerivedFont: return "derived:" + derived.Key;
                default: return "reorder";
            }
        }

        /// <summary>
        /// Word breaking, OpenType shaping or the codepoint reorder for one outgoing text. True when
        /// it presented again, from its logical text, a text of ours shaped for another font.
        /// </summary>
        private static bool PresentSyllabic(object instance, long compId, ref string value, string settingsFontName, bool ownUi)
        {
            // Our own output coming back (a refresh re-sets what is on screen) is kept — unless it
            // was shaped for another font than the one the component draws with now: a fallback
            // set, changed or removed. A copy's private names are nothing in another font: the
            // buttons went blank when a fallback was set back to none (2026-10-01).
            bool again = false;
            string ownLogical = TranslatorCore.TryGetPresentedLogical(value);
            if (ownLogical != null)
            {
                if (compId == -1 || !_shapedFor.TryGetValue(compId, out var was)
                    || was == DrawnFor(instance, settingsFontName, ownUi, out _, out _, out _)) return false;
                value = ownLogical;
                again = true;
            }

            bool needsBreak = WordBreaker.NeedsBreaking(value);
            bool needsShape = OpenTypeText.NeedsShaping(value);
            bool needsReorder = IndicReorderer.NeedsReordering(value);
            if (!needsBreak && !needsReorder && !needsShape) return again;
            if (UIToolkitSupport.IsTextElementInstance(instance) && UIToolkitSupport.IsAtgActive(instance)) return again;

            string logical = value;
            string working = value;
            if (needsBreak)
            {
                working = WordBreaker.Break(working, out string whyNot);
                // Said, never hidden: a text stays unwrappable, and the reason must be readable —
                // once per reason.
                if (whyNot != null && DiagnosticOnce.First("RtlPresenter.wordBreak", whyNot))
                    TranslatorCore.LogWarning($"[RtlPresenter] word breaking skipped — {whyNot}");
            }

            // Stage B2 — the font's own OpenType tables, for a TMP component drawn by a font
            // asset of ours (FontShaping): conjuncts, half forms, reph, positioned marks, as the
            // font designed them. 🔴 Never followed by the codepoint reorder: a shaped run
            // already carries its pre-base signs in visual order, and the reorder would move
            // one from the syllable it belongs to into the one before it (कि + क: the sign now
            // sits AFTER a consonant that is not its own). UI.Text and UI Toolkit replaced by a
            // fonts/ or an installed font get the same, through that font's DERIVED copy
            // (DerivedFonts): every glyph named by a private codepoint the copy maps to a
            // composite placed as shaped. Every other case — a game font we have no file for, an
            // engine the mod does not re-font (TextMesh) — keeps stage C's reorder, the most a
            // font we cannot read can take (ShapingCoverageChecks lists them).
            bool shaped = false;
            string drawnFor = null;
            if (needsShape)
            {
                drawnFor = DrawnFor(instance, settingsFontName, ownUi, out var route, out var asset, out var derived);
                switch (route)
                {
                    case ShapingRoute.Route.OurTmpAsset:
                    {
                        string s = OpenTypeText.Shape(working, asset.Font, asset);
                        if (!ReferenceEquals(s, working)) { working = s; shaped = true; }
                        break;
                    }
                    case ShapingRoute.Route.DerivedFont:
                    {
                        string s = OpenTypeText.Shape(working, derived.Font, derived.Namer);
                        if (!ReferenceEquals(s, working)) { working = s; shaped = true; }
                        DerivedFonts.NoteNamed(derived);   // new names → the copy is rewritten this tick
                        // The mod's own window: a font with a copy shapes only the letters it has —
                        // the interface's Arial has none of Hindi. Said as unshaped (the window
                        // notice); a game font lacking them is the coverage notice's.
                        if (ownUi && FontManager.Covers(TranslatorCore.WindowFontFor(ModWindowText.SideOf(instance)), logical) == false)
                            FontManager.NoteWindowUnshaped(ModWindowText.SideOf(instance), logical);
                        break;
                    }
                    case ShapingRoute.Route.ReorderOnly:
                        // Shown without its shaping: said on the Fonts tab and in the corner, for a
                        // text of the translation (FontManager.Coverage).
                        // The mod's own window: said in the corner while it is open, for the part that
                        // shows it (FontManager.WindowCannotShape).
                        if (ownUi) FontManager.NoteWindowUnshaped(ModWindowText.SideOf(instance), logical);
                        else if (TranslatorCore.IsAlreadyTargetText(logical)) FontManager.NoteUnshaped(settingsFontName, logical);
                        break;
                }
            }
            if (needsReorder && !shaped)
                working = IndicReorderer.Reorder(working);

            if (compId != -1)
            {
                if (drawnFor != null) _shapedFor[compId] = drawnFor;
                else _shapedFor.Remove(compId);
            }
            if (ReferenceEquals(working, logical) || working == logical) return again;
            RegisterShown(compId, working, logical);
            Log(compId, (needsBreak ? "words+" : "") + (shaped ? "opentype" : needsReorder ? "indic" : "none"), logical, working);
            value = working;

            // UI.Text does not break a line on U+200B (bench: a Thai paragraph cut inside its
            // words with the boundaries in place). Same two passes as its RTL text: the engine
            // lays the string out and sizes the box, then the reflow cuts the text on its
            // boundaries against that width with the engine's own character advances, the
            // engine's wrapping held off while those lines are displayed. TMP and UI Toolkit
            // break on the boundary themselves.
            if (needsBreak && compId != -1 && working.IndexOf(WordBreaker.ZeroWidthSpace) >= 0 && TypeHelper.IsInScene(instance)
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
            return again;
        }


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
        private enum ReflowKind { UGuiText, UGuiWords, Ngui, Tmp }

        private sealed class Reflow
        {
            public WeakReference Comp;
            public string Logical;
            public string Assigned;   // what is on screen right now (freshness check)
            public string Measure;    // the shaped LOGICAL form the line source must cut
            public int Attempts;
            public bool Mirror;
            public ReflowKind Kind;
            public float Width;       // TMP: the width the lines were cut at
        }

        // TMP: the lines last cut per component — what it is given again when the game writes the same
        // text into a box of the same width (see Present).
        private sealed class TmpCut { public string Logical; public float Width; public string Final; }
        private static readonly Dictionary<long, TmpCut> _tmpLines = new Dictionary<long, TmpCut>();

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
        /// <summary>
        /// A TEMPLATE (a prefab the game copies from, never drawn itself): no second pass — it is
        /// never laid out, so its reflow waited forever, and every copy was born with the measuring
        /// form (logical order, direction marks drawn as boxes) that the game never wrote again on
        /// the copy (2026-10-02, a phone's titles; and hundreds of such waits walked every frame).
        /// The whole-string visual form instead: right for a one-line label, which a copy keeps
        /// until the game writes it, when the copy is presented on its own. Nothing else is set on
        /// a template — no flag, alignment or wrap: a copy inherits every one of them, and the
        /// game's own text written into the copy then wears state this component never recorded.
        /// </summary>
        private static bool PresentTemplate(object instance, long compId, ref string value)
        {
            if (TypeHelper.IsInScene(instance)) return false;
            string visual = RtlComposer.Compose(value, RtlOutput.VisualOrder);
            RegisterShown(compId, visual, value);
            if (_reflows.ContainsKey(compId)) _reflows.Remove(compId);
            Log(compId, "visual/template", value, visual);
            value = visual;
            return true;
        }

        private static void QueueReflow(object instance, long compId, ref string value,
                                        ReflowKind kind, bool mirror, string logMode,
                                        string logicalForRecord = null, string assignedForm = null)
        {
            string shapedLogical = RtlComposer.ShapeLogicalOnly(value);
            string assigned = assignedForm ?? shapedLogical;
            RegisterShown(compId, assigned, logicalForRecord ?? value);
            if (compId != -1)
            {
                _reflows[compId] = new Reflow
                {
                    Comp = new WeakReference(instance),
                    Logical = value,
                    Assigned = assigned,
                    Measure = shapedLogical,
                    Mirror = mirror,
                    Kind = kind,
                };
                TraceReflow(compId, "queued " + kind);
            }
            Log(compId, logMode, value, assigned);
            value = assigned;
        }

        // cachedTextGenerator plumbing, resolved once per process.
        private static bool _genResolved;
        private static PropertyInfo _cachedGeneratorProp;   // Text.cachedTextGenerator
        private static GeneratorList _generatorLines;       // TextGenerator.GetLines(List<UILineInfo>)
        private static PropertyInfo _generatorCharCountProp; // TextGenerator.characterCount
        private static GeneratorList _generatorChars;       // TextGenerator.GetCharacters(List<UICharInfo>)
        private static MemberInfo _charWidth;                // UICharInfo.charWidth
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
        private static MemberInfo _lineStartChar;           // UILineInfo.startCharIdx

        /// <summary>
        /// A text generator's lines or characters, read through GetLines / GetCharacters(List&lt;T&gt;)
        /// into one list of the engine's own type, reused (main thread only). Not through the
        /// lines / characters properties: they answer an IList&lt;T&gt;, which on IL2CPP is an interop
        /// interface wrapper — no System IList, no Count — so every read there came back empty and
        /// every UI.Text cut fell to its last resort: one line, held unwrapped, spilling out of its
        /// box. The list type is the one the method declares (System's on Mono, the interop's on
        /// IL2CPP), read through its own Count and indexer.
        /// </summary>
        private sealed class GeneratorList
        {
            private readonly MethodInfo _fill, _count, _item;
            private readonly object _list;
            internal readonly Type Element;

            private GeneratorList(MethodInfo fill, object list, MethodInfo count, MethodInfo item, Type element)
            {
                _fill = fill; _list = list; _count = count; _item = item; Element = element;
            }

            /// <summary>The generator's <paramref name="fillName"/>(List&lt;T&gt;), or null when this runtime lacks it.</summary>
            internal static GeneratorList Resolve(Type generatorType, string fillName)
            {
                const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;
                foreach (var m in generatorType.GetMethods(pubInst))
                {
                    if (m.Name != fillName) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    var listType = ps[0].ParameterType;
                    if (!listType.IsGenericType || listType.IsInterface || listType.IsAbstract
                        || listType.GetGenericArguments().Length != 1) continue;
                    var count = Members.Property(listType, "Count", pubInst)?.GetGetMethod();
                    var item = listType.GetMethod("get_Item", pubInst, null, new[] { typeof(int) }, null);
                    if (count == null || item == null) continue;
                    return new GeneratorList(m, Activator.CreateInstance(listType), count, item,
                                             listType.GetGenericArguments()[0]);
                }
                return null;
            }

            /// <summary>What the generator holds now, one boxed element per entry.</summary>
            internal List<object> Read(object generator)
            {
                _fill.Invoke(generator, new[] { _list });
                int n = Convert.ToInt32(_count.Invoke(_list, null));
                var items = new List<object>(n);
                for (int i = 0; i < n; i++) items.Add(_item.Invoke(_list, new object[] { i }));
                return items;
            }
        }

        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>A TextGenerationSettings member — a field on Mono, a property on IL2CPP. Null when absent.</summary>
        private static object SettingOf(object settings, string name)
        {
            var m = Members.FieldOrProperty(settings.GetType(), name, PublicInstance);
            return m == null ? null : Members.Get(m, settings);
        }

        /// <summary>
        /// Sets a wrap setting (horizontalOverflow / verticalOverflow) to Overflow (1). False when
        /// the member is absent — the caller says so rather than cut under the box's own wrapping.
        /// </summary>
        private static bool SetOverflowSetting(object settings, string name)
        {
            var m = Members.FieldOrProperty(settings.GetType(), name, PublicInstance);
            if (m == null) return false;
            Members.Set(m, settings, Enum.ToObject(Members.TypeOf(m), 1));
            return true;
        }

        private static int LineStartOf(object line) => Convert.ToInt32(Members.Get(_lineStartChar, line));

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
            // First: copies of templates enabled since the last pass, presented now — their own
            // reflow, if they need one, is queued for this same pass.
            AdoptCopies();
            if (_reflows.Count == 0) return;

            _reflowScratch.Clear();
            _reflowScratch.AddRange(_reflows.Keys);
            // A uGUI graphic enabled since the last pass may be one of the parked texts: all of them
            // are looked at again. Nothing enabled: a parked text is still inactive, and is skipped
            // without asking the engine anything (hundreds of them on a screen filled up front).
            if (TranslatorScanner.GraphicsEnabled != _parkedAt)
            {
                _parked.Clear();
                _parkedAt = TranslatorScanner.GraphicsEnabled;
            }
            foreach (long id in _reflowScratch)
            {
                if (_parked.Contains(id)) continue;
                var entry = _reflows[id];
                var comp = entry.Comp.Target;
                bool dead = comp == null || (comp is UnityEngine.Object uo && uo == null);
                if (dead) { _reflows.Remove(id); continue; }

                try
                {
                    // The game moved on to another text — this reflow is stale.
                    if (TypeHelper.GetText(comp) != entry.Assigned)
                    {
                        TraceReflow(id, "dropped (the text changed since) " + entry.Kind, comp);
                        _reflows.Remove(id);
                        continue;
                    }

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
                    string blocker = RedrawBlocker(comp, entry.Kind);
                    if (blocker != null)
                    {
                        // Inactive, on a uGUI text, where graphics announce their arrival: parked
                        // until one is enabled — the event that can change the answer (above).
                        if (blocker == "inactive" && TranslatorScanner.AppearanceHooked
                            && (entry.Kind == ReflowKind.UGuiText || entry.Kind == ReflowKind.UGuiWords))
                            _parked.Add(id);
                        // Debug: a reflow that waits says why, once per component — a text left in
                        // its measuring form (logical order) on screen is read backwards.
                        TraceReflow(id, "waits (" + blocker + ")", comp);
                        continue;
                    }

                    string final = BuildLines(entry, comp, out string whyNot);
                    // Nothing to change: the engine's own lines were right (one line, no wrap).
                    if (final != null && final == entry.Assigned)
                    {
                        if (entry.Kind == ReflowKind.Tmp) _tmpLines[id] = new TmpCut { Logical = entry.Logical, Width = entry.Width, Final = final };
                        _reflows.Remove(id);
                        continue;
                    }
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
                        TraceReflow(id, "not ready (" + whyNot + ") " + entry.Kind + " attempt " + (entry.Attempts + 1), comp);
                        if (++entry.Attempts < 3) continue;
                        // TMP: the flagged form stays — the engine's own wrap, as before this pass
                        // existed. A visual-order fallback would be read backwards under the flag.
                        if (entry.Kind == ReflowKind.Tmp)
                        {
                            if (DiagnosticOnce.First("RtlPresenter.tmpNotCut", id + "|" + whyNot))
                                TranslatorCore.LogWarning($"[RtlPresenter] TMP lines not cut ({whyNot}) — the engine's own wrap kept: comp={id}");
                            _reflows.Remove(id);
                            continue;
                        }
                        string whyOwn = null;
                        if (entry.Kind == ReflowKind.UGuiText)
                            final = BuildUGuiLinesNow(comp, entry.Measure, out whyOwn);
                        else if (entry.Kind == ReflowKind.UGuiWords)
                            final = BuildUGuiWordLines(comp, entry.Assigned, out whyOwn);
                        if (DiagnosticOnce.First("RtlPresenter.reflowFallback", id + "|" + whyNot + "|" + (final != null)))
                        {
                            if (final != null)
                                TranslatorCore.LogWarning($"[RtlPresenter] engine lines never caught up ({whyNot}) — cut with our own generator at the box's settled width: comp={id}");
                            else
                                TranslatorCore.LogWarning($"[RtlPresenter] reflow gave up ({whyNot}{(whyOwn != null ? " | populate: " + whyOwn : "")}) — whole-string visual order, line stack may read bottom-up: comp={id}");
                        }
                        if (final == null) final = RtlComposer.Compose(entry.Logical, RtlOutput.VisualOrder);
                    }

                    RegisterShown(id, final, entry.Logical);
                    Log(id, "reflow/final", entry.Logical, final);
                    TraceReflow(id, "written " + entry.Kind + ", " + final.Split('\n').Length + " line(s)", comp);

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
                    if (entry.Kind == ReflowKind.Tmp) _tmpLines[id] = new TmpCut { Logical = entry.Logical, Width = entry.Width, Final = final };
                    _reflows.Remove(id);
                }
                catch (Exception ex)
                {
                    _reflows.Remove(id);
                    TranslatorCore.LogWarning($"[RtlPresenter] reflow failed, leaving measuring form: {ex.Message}");
                }
            }
        }

        private static readonly HashSet<string> _waitReasons = new HashSet<string>();
        // Reflows waiting on an inactive uGUI text, skipped until a graphic is enabled (see
        // ProcessPendingReflows), and the count of enabled graphics they were parked at.
        private static readonly HashSet<long> _parked = new HashSet<long>();
        private static int _parkedAt;
        private static int _inactiveTraces;

        /// <summary>
        /// Debug: what happens to one component's pending reflow, once per state — queued, waiting
        /// (and why), removed (and by what). Waiting on an inactive component is the common case
        /// (a hidden screen filled up front): only the first ones are said, so the rest stays readable.
        /// </summary>
        private static void TraceReflow(long id, string state, object comp = null)
        {
            if (!TranslatorCore.DebugMode) return;
            // Waiting on an inactive component: neither remembered nor said past the first fifty —
            // a hidden screen holds hundreds, and they must not crowd out the states that matter.
            if (state == "waits (inactive)") { if (_inactiveTraces >= 50 || !_waitReasons.Add(id + "|" + state)) return; _inactiveTraces++; }
            else if (!_waitReasons.Add(id + "|" + state)) return;
            string where = comp is UnityEngine.Component wc && wc != null ? " @ " + TranslatorCore.GetGameObjectPath(wc.gameObject) : "";
            TranslatorCore.LogInfo($"[RtlPresenter] reflow {state}: comp={id}{where}");
        }

        // ⚠ No re-cut on box resize. It was tried (a Graphic.OnRectTransformDimensionsChange
        // hook) and it is circular by construction: a ContentSizeFitter sizes the box from the
        // text, the re-cut sizes the text from the box — the description of an organ shrank to
        // one character per line and locked there; a disclaimer grew to one line as wide as its
        // paragraph. A cut is made once, at the width the game gave the box for its own text,
        // and the engine's wrapping (kept on) folds what would not fit.

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
                case ReflowKind.Tmp:
                    return BuildTmpLines(comp, entry, out whyNot);
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

        // TMP's wrapping switch (enableWordWrapping, or textWrappingMode in newer TMP), its margins
        // and its rect — resolved once per type.
        private static readonly Dictionary<Type, PropertyInfo[]> _tmpLayoutProps = new Dictionary<Type, PropertyInfo[]>();

        /// <summary>
        /// A TMP component's wrapping switch and the width its text is laid out in: the rect less
        /// TMP's margins. False when it has no rect to read.
        /// </summary>
        private static bool TmpLayout(object comp, out bool wraps, out float width)
        {
            wraps = true;
            width = 0f;
            var type = comp.GetType();
            if (!_tmpLayoutProps.TryGetValue(type, out var props))
            {
                const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
                _tmpLayoutProps[type] = props = new[]
                {
                    type.GetProperty("enableWordWrapping", pub),
                    type.GetProperty("textWrappingMode", pub),
                    type.GetProperty("margin", pub),
                    type.GetProperty("rectTransform", pub),
                };
            }
            if (props[0] != null && props[0].GetValue(comp, null) is bool on && !on) wraps = false;
            if (props[1] != null && props[1].GetValue(comp, null)?.ToString().IndexOf("NoWrap", StringComparison.Ordinal) >= 0) wraps = false;
            var rect = props[3]?.GetValue(comp, null) as UnityEngine.RectTransform;
            if (rect == null) return false;
            width = rect.rect.width;
            if (props[2]?.GetValue(comp, null) is UnityEngine.Vector4 margin) width -= margin.x + margin.z;
            return true;
        }

        /// <summary>
        /// The lines of a right-to-left TMP text cut in LOGICAL order — the engine wrapping the
        /// flagged form cuts a left-to-right run of several words backwards (see Present). Cut
        /// at its spaces against the width the layout gave this text (the rect less TMP's
        /// margins, read now, a frame after the text was assigned — the reason it is a second
        /// pass), with TMP's own measure of each candidate line; then composed flagged as one
        /// string, each line ending with an explicit break the engine keeps. Returns the assigned
        /// form unchanged when the text is one line or the component does not wrap.
        /// </summary>
        private static string BuildTmpLines(object comp, Reflow entry, out string whyNot)
        {
            whyNot = null;
            if (!TmpLayout(comp, out bool wraps, out float width)) { whyNot = "no rectTransform"; return null; }
            if (!wraps) return entry.Assigned;
            if (width <= 1f) { whyNot = "no width yet"; return null; }
            entry.Width = width;

            // Half a unit of slack: a line measured exactly at the width must not be re-wrapped by
            // the engine's rounding (the same lesson as UI.Text's DisableRewrap, kept as a margin
            // here since TMP's own wrapping stays on for a word wider than the box).
            var lines = GreedyLines.Cut(entry.Logical, width - 0.5f,
                line => TextMeasure.Measure(comp, RtlComposer.ShapeLogicalOnly(line), null));
            if (lines == null) { whyNot = "TMP cannot measure here"; return null; }
            if (lines.Count <= 1) return entry.Assigned;
            return RtlComposer.Compose(string.Join("\n", lines.ToArray()), RtlOutput.RtlFlagged);
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
        /// <summary>
        /// Does Unity draw this text component right now? The same gate as <see cref="WillBeRedrawn"/>
        /// for a uGUI Graphic — active, enabled, under an enabled Canvas, not culled by a mask —
        /// shared with the inspector's picking, which asks the same question of a text the pointer
        /// is over: a hidden or clipped text must not be picked. Anything not readable answers true.
        /// </summary>
        internal static bool IsDrawn(object comp) => WillBeRedrawn(comp, ReflowKind.UGuiText);

        private static bool WillBeRedrawn(object comp, ReflowKind kind) => RedrawBlocker(comp, kind) == null;

        /// <summary>Why the engine will not redraw this component now (see WillBeRedrawn), or null.</summary>
        private static string RedrawBlocker(object comp, ReflowKind kind)
        {
            if (!(comp is UnityEngine.Component c) || c.gameObject == null) return null;
            if (!c.gameObject.activeInHierarchy) return "inactive";
            if (kind == ReflowKind.Ngui) return null;
            // TMP lays out what it draws; a 3D TextMeshPro is no Graphic, so the canvas gate below
            // does not apply to it — active and enabled is the question.
            if (kind == ReflowKind.Tmp) return comp is UnityEngine.Behaviour tb && !tb.isActiveAndEnabled ? "disabled" : null;
            if (comp is UnityEngine.Behaviour b && !b.isActiveAndEnabled) return "disabled";
            EnsureGeneratorPlumbing();
            try
            {
                if (_canvasProp != null && _canvasProp.GetValue(comp, null) == null) return "no canvas";
                if (_cullProp != null && _canvasRendererProp != null)
                {
                    object renderer = _canvasRendererProp.GetValue(comp, null);
                    if (renderer != null && (bool)_cullProp.GetValue(renderer, null)) return "culled";
                }
            }
            catch (Exception ex) { Faults.Say("RtlPresenter.WillBeRedrawn", ex); }
            return null;
        }

        /// <summary>horizontalOverflow = Overflow while OUR line breaks are displayed.</summary>
        internal static void DisableRewrap(object comp, long compId)
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
            catch (Exception ex) { Faults.Say("RtlPresenter.DisableRewrap", ex); }
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
                _generatorCharCountProp = genType?.GetProperty("characterCount", BindingFlags.Public | BindingFlags.Instance);

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
                    // A text generator of our own: the engine may refuse to make one — said.
                    try { _ownGenerator = Activator.CreateInstance(genType); }
                    catch (Exception ex) { Faults.Say("RtlPresenter.EnsureGeneratorPlumbing own generator", ex); }

                    // Lines and characters, and the members read on them. UILineInfo / UICharInfo
                    // live in the text-rendering assembly, not necessarily UI's: the element type
                    // of the list each method fills is the reliable way to them.
                    try
                    {
                        _generatorLines = GeneratorList.Resolve(genType, "GetLines");
                        _generatorChars = GeneratorList.Resolve(genType, "GetCharacters");
                        _lineStartChar = Members.FieldOrProperty(_generatorLines?.Element, "startCharIdx", PublicInstance);
                        _charWidth = Members.FieldOrProperty(_generatorChars?.Element, "charWidth", PublicInstance);
                    }
                    catch (Exception ex) { Faults.Say("RtlPresenter.EnsureGeneratorPlumbing lines", ex); }
                }
            }
            // The uGUI text generator's members, found by reflection on this engine: a refusal
            // leaves right-to-left lines unmeasured, and it is said.
            catch (Exception ex) { Faults.Say("RtlPresenter.EnsureGeneratorPlumbing", ex); }
        }
        }


        // Everything a last-resort cut rests on, once per text cut (never a count): the box as the
        // engine sees it (rectTransform.rect — what Text.OnPopulateMesh cuts with) against the
        // pixel-adjusted rect, the component's own wrapping and best-fit settings, and how many
        // lines came out. Added when labels came out in three pieces with a "stable" width that
        // could not be the engine's — the log has to say which of these differs.
        private static void DescribeCut(object comp, string assigned, UnityEngine.Rect pixelRect, object settings, string cut)
        {
            if (!TranslatorCore.DebugMode || !DiagnosticOnce.First("RtlPresenter.cut", assigned + "\u0001" + pixelRect.width.ToString("F1"))) return;
            try
            {
                string rectSize = "?";
                var rtProp = comp.GetType().GetProperty("rectTransform", BindingFlags.Public | BindingFlags.Instance);
                if (rtProp?.GetValue(comp, null) is UnityEngine.RectTransform rt) rectSize = $"{rt.rect.width:F1}x{rt.rect.height:F1}";
                object F(string name) { try { return SettingOf(settings, name) ?? "(absent)"; } catch (Exception ex) { return $"?({ex.GetType().Name})"; } }
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
                || _ownGenerator == null || _generatorChars == null || _charWidth == null)
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
                // One line, every character: both overflows on (HorizontalWrapMode.Overflow and
                // VerticalWrapMode.Overflow are both 1).
                if (!SetOverflowSetting(settings, "horizontalOverflow") || !SetOverflowSetting(settings, "verticalOverflow"))
                { whyNot = "generation settings not writable on this runtime"; return null; }
                object scaleSetting = SettingOf(settings, "scaleFactor");
                if (scaleSetting == null) { whyNot = "generation settings carry no scale factor"; return null; }
                float scale = Convert.ToSingle(scaleSetting);

                if (!(bool)_generatorPopulate.Invoke(_ownGenerator, new object[] { assigned, settings }))
                { whyNot = "generator refused to populate"; return null; }
                var chars = _generatorChars.Read(_ownGenerator);
                if (chars.Count < assigned.Length)
                { whyNot = $"generator reports {chars.Count} characters for {assigned.Length}"; return null; }

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
                    float w = Convert.ToSingle(Members.Get(_charWidth, chars[i]));
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
                        for (int k = lineStart; k <= i; k++) lineWidth += Convert.ToSingle(Members.Get(_charWidth, chars[k]));
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

        /// <summary>
        /// Where the engine would start each line of <paramref name="text"/> in this UI.Text's box,
        /// now, with a generator of our own — for an INPUT FIELD's label (RtlInputFields). Unlike a
        /// label sized by its content, a field's text box is fixed by the field, so the width seen
        /// before the text is assigned is the right one: no two-pass needed. Same one-pixel margin
        /// as <see cref="BuildUGuiLinesNow"/>, so a recomposed line is never folded again by rounding.
        /// Null, with the reason, when the generator API or the layout is not there.
        /// </summary>
        internal static List<int> UGuiLineStartsNow(object comp, string text, out string whyNot)
        {
            whyNot = null;
            EnsureGeneratorPlumbing();
            if (_generatorPopulate == null || _getGenerationSettings == null || _getPixelAdjustedRect == null
                || _ownGenerator == null || _generatorLines == null || _lineStartChar == null)
            { whyNot = "generator API not resolvable on this runtime"; return null; }

            try
            {
                object rect = _getPixelAdjustedRect.Invoke(comp, null);
                if (!(rect is UnityEngine.Rect r) || r.width < 1f) { whyNot = "no layout yet"; return null; }

                var extents = new UnityEngine.Vector2(Math.Max(1f, r.width - 1f), r.height);
                object settings = _getGenerationSettings.Invoke(comp, new object[] { extents });
                if (settings == null) { whyNot = "no generation settings"; return null; }
                if (!SetOverflowSetting(settings, "verticalOverflow"))
                { whyNot = "generation settings not writable on this runtime"; return null; }

                if (!(bool)_generatorPopulate.Invoke(_ownGenerator, new object[] { text, settings }))
                { whyNot = "generator refused to populate"; return null; }

                var lines = _generatorLines.Read(_ownGenerator);
                var starts = new List<int>(lines.Count);
                foreach (var line in lines) starts.Add(LineStartOf(line));
                return starts;
            }
            catch (Exception ex) { whyNot = "populate failed: " + ex.Message; return null; }
        }

        // UICharInfo.cursorPos, UILineInfo.topY/height — read for an input field's caret.
        private static MemberInfo _charCursorPos;
        private static MemberInfo _lineTopY, _lineHeight;
        private static PropertyInfo _pixelsPerUnitProp;

        /// <summary>
        /// What a UI.Text's own generator says it drew: for every character its left x and width,
        /// for every line its first character, top and height — in the component's local space
        /// (generator pixels divided by pixelsPerUnit, as InputField's own caret code does). False
        /// when the generator does not describe <paramref name="shown"/> (a frame behind) or cannot
        /// be read on this runtime.
        /// </summary>
        internal static bool ReadUGuiGlyphs(object comp, string shown,
                                            List<float> charX, List<float> charWidth,
                                            List<int> lineStart, List<float> lineTop, List<float> lineHeight)
        {
            EnsureGeneratorPlumbing();
            if (_cachedGeneratorProp == null || _generatorChars == null || _generatorLines == null
                || _charWidth == null || _lineStartChar == null) return false;
            try
            {
                if (_charCursorPos == null)
                {
                    _charCursorPos = Members.FieldOrProperty(_generatorChars.Element, "cursorPos", PublicInstance);
                    _lineTopY = Members.FieldOrProperty(_generatorLines.Element, "topY", PublicInstance);
                    _lineHeight = Members.FieldOrProperty(_generatorLines.Element, "height", PublicInstance);
                    _pixelsPerUnitProp = TypeHelper.UI_TextType.GetProperty("pixelsPerUnit", BindingFlags.Public | BindingFlags.Instance);
                }
                if (_charCursorPos == null || _lineTopY == null || _lineHeight == null || _pixelsPerUnitProp == null)
                    return false;

                object generator = _cachedGeneratorProp.GetValue(comp, null);
                if (generator == null) return false;
                var chars = _generatorChars.Read(generator);
                var lines = _generatorLines.Read(generator);
                if (lines.Count == 0) return false;
                // A generator a frame behind describes the previous text: refuse rather than draw
                // the caret against somebody else's glyphs. (Unity adds one terminator glyph.)
                if (chars.Count < shown.Length || chars.Count > shown.Length + 1) return false;

                float ppu = Convert.ToSingle(_pixelsPerUnitProp.GetValue(comp, null));
                if (ppu <= 0f) ppu = 1f;

                charX.Clear(); charWidth.Clear();
                foreach (var c in chars)
                {
                    var pos = (UnityEngine.Vector2)Members.Get(_charCursorPos, c);
                    charX.Add(pos.x / ppu);
                    charWidth.Add(Convert.ToSingle(Members.Get(_charWidth, c)) / ppu);
                }
                lineStart.Clear(); lineTop.Clear(); lineHeight.Clear();
                foreach (var line in lines)
                {
                    lineStart.Add(LineStartOf(line));
                    lineTop.Add(Convert.ToSingle(Members.Get(_lineTopY, line)) / ppu);
                    lineHeight.Add(Convert.ToSingle(Members.Get(_lineHeight, line)) / ppu);
                }
                return true;
            }
            catch (Exception ex) { Faults.Say("RtlPresenter.ReadUGuiGlyphs", ex); return false; }
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
                bool bestFit = false;
                var bestFitProp = comp.GetType().GetProperty("resizeTextForBestFit", BindingFlags.Public | BindingFlags.Instance);
                if (bestFitProp != null) bestFit = (bool)bestFitProp.GetValue(comp, null);
                if (!bestFit && !SetOverflowSetting(settings, "verticalOverflow"))
                { whyNot = "generation settings not writable on this runtime"; return null; }
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

            if (_cachedGeneratorProp == null || _generatorLines == null || _lineStartChar == null)
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
                catch (Exception ex) { Faults.Say("RtlPresenter.BuildPerLineVisual", ex); }
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

            var lines = _generatorLines.Read(generator);
            if (lines.Count == 0) { whyNot = "generator has no lines yet"; return null; }

            var starts = new List<int>(lines.Count);
            foreach (var line in lines) starts.Add(LineStartOf(line));
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
            try { processed = prop.GetValue(comp, null) as string; } catch (Exception ex) { Faults.Say("RtlPresenter.BuildNguiLines", ex); }
            if (string.IsNullOrEmpty(processed)) { whyNot = "processedText not ready"; return null; }

            if (!EqualsIgnoringWhitespace(processed, assigned))
            { whyNot = "processedText diverges from the assigned text (markup, ellipsis or shrink)"; return null; }

            return ComposeLines(processed.Split('\n'));
        }

        private static PropertyInfo ProcessedTextProp(Type type)
        {
            if (_processedTextProps.TryGetValue(type, out var cached)) return cached;
            // Members.Property: no AmbiguousMatchException to catch on a type that re-declares it.
            PropertyInfo prop = Members.Property(type, "processedText", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && (prop.PropertyType != typeof(string) || prop.GetMethod == null))
                prop = null;
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
                // A method named with its parameter types cannot be ambiguous, and the property goes
                // through Members: nothing here throws.
                _tk2dFormatText = TranslatorPatches.Tk2dType.GetMethod("FormatText",
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                _tk2dInlineStyling = Members.Property(TranslatorPatches.Tk2dType, "inlineStyling",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            string shaped = RtlComposer.ShapeLogicalOnly(logical);

            if (shaped.IndexOf('^') >= 0)
            {
                bool styling = false;
                try { styling = _tk2dInlineStyling != null && (bool)_tk2dInlineStyling.GetValue(instance, null); }
                catch (Exception ex) { Faults.Say("RtlPresenter.ComposeTk2dPerLine styling", ex); }
                if (styling) return ComposeVisualPerLine(logical);
            }

            string wrapped = shaped;
            if (_tk2dFormatText != null)
            {
                try { wrapped = (string)_tk2dFormatText.Invoke(instance, new object[] { shaped }) ?? shaped; }
                catch (Exception ex) { Faults.Say("RtlPresenter.ComposeTk2dPerLine format", ex); wrapped = shaped; }
            }
            else if (DiagnosticOnce.First("RtlPresenter.tk2dFormat", ""))
                TranslatorCore.LogWarning("[RtlPresenter] tk2dTextMesh.FormatText not resolvable — per-explicit-line only, engine wrap points unknown");

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
            // 🔴 Never a TEMPLATE's (a prefab the game copies its list entries from): mirrored there,
            // every copy was born mirrored, its mirrored side was then recorded as the game's, and
            // the copy's own mirror put it back — mirrored at launch read unmirrored, and "keep"
            // chosen later mirrored it (2026-10-02, a quest list). Same rule as the scanner, the
            // font replacement and the sizes (TypeHelper.IsInScene): a template is left alone, and
            // each copy is decided on its own.
            if (!TypeHelper.IsInScene(comp)) return;
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
            catch (Exception ex) { Faults.Say("RtlPresenter.MirrorAlignment", ex); }
        }

        /// <summary>The mirrored value of one alignment enum, or null when there is nothing to swap.</summary>
        internal static object MirroredAlignmentValue(Type enumType, object original)
        {
            // Enum.GetName throws on a value not of that enum, and Enum.Parse on a name it lacks:
            // both are recognised beforehand instead of caught.
            // (GetName takes the enum's own values and its underlying integer, nothing else.)
            string name = original != null
                          && (original.GetType() == enumType || original.GetType() == Enum.GetUnderlyingType(enumType))
                ? Enum.GetName(enumType, original) : null;
            if (name != null)
            {
                string swapped =
                    name.IndexOf("Left", StringComparison.Ordinal) >= 0 ? name.Replace("Left", "Right") :
                    name.IndexOf("Right", StringComparison.Ordinal) >= 0 ? name.Replace("Right", "Left") : null;
                if (swapped != null && Enum.IsDefined(enumType, swapped))
                    return Enum.Parse(enumType, swapped);
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

        /// <summary>
        /// Whether every text drawn with this font is aligned the reading way, not only the
        /// right-to-left ones: the translation's language is written right to left (the language
        /// catalogue, from CLDR) and the font's setting — or the override rule matching the
        /// component — says mirror. 🔴 The setting belongs to the FONT, not to the text (user,
        /// 2026-10-02: « le mirror rtl s'applique à une font, pas à la traduction »): a Latin
        /// "VSync" among Arabic labels of the same font stayed on the game's side. A place where
        /// that is wrong gets an override rule — what the rules are for.
        /// </summary>
        private static bool MirrorsEveryText(string settingsFontName, FontOverrideRule overrideRule)
            => TranslatorCore.TargetIsRightToLeft && TranslatorCore.ShouldMirrorRtlAlignment(settingsFontName, overrideRule);

        /// <param name="keepMirrored">The component leaves right-to-left text but stays aligned the
        /// reading way (<see cref="MirrorsEveryText"/>): its flag and wrap go back, its alignment
        /// stays mirrored.</param>
        private static void RestoreIfFlagged(object instance, long compId, PropertyInfo prop, bool keepMirrored = false)
        {
            if (UIToolkitSupport.IsTextElementInstance(instance))
            {
                UIToolkitSupport.RestoreRtlAdjustments(instance);
                UIToolkitSupport.ForgetPending(instance);
                if (keepMirrored) UIToolkitSupport.MirrorAlign(instance, true);
            }

            if (compId == -1) return;
            if (prop != null && _flaggedOriginal.TryGetValue(compId, out bool original))
            {
                _flaggedOriginal.Remove(compId);
                try { prop.SetValue(instance, original, null); } catch (Exception ex) { Faults.Say("RtlPresenter.RestoreIfFlagged", ex); }
            }
            if (keepMirrored) MirrorAlignment(instance, compId, true);
            else RestoreAlignment(instance, compId);
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
            catch (Exception ex) { Faults.Say("RtlPresenter.RestoreAlignment", ex); }
        }

        /// <summary>The wrap mode a component had before <see cref="DisableRewrap"/>, put back.</summary>
        internal static void RestoreRewrap(object instance, long compId)
        {
            if (compId == -1 || !_wrapOriginal.TryGetValue(compId, out object wrap)) return;
            _wrapOriginal.Remove(compId);
            try
            {
                var wrapProp = instance.GetType().GetProperty("horizontalOverflow", BindingFlags.Public | BindingFlags.Instance);
                wrapProp?.SetValue(instance, wrap, null);
            }
            catch (Exception ex) { Faults.Say("RtlPresenter.RestoreRewrap", ex); }
        }

        private static void Log(long compId, string mode, string logical, string composed)
        {
            // ⚠ **Diagnostic, behind DebugMode — silent unless somebody sets `"debug": true`.** The
            // exact code-point order of what came in and what went out: a terminal renders Arabic
            // with its own bidi, so the summary line below cannot tell "the composer reordered
            // this" from "my viewer did". It is what answers an issue saying "right-to-left is
            // broken in my game", which is why it stayed when the bench probes were removed.
            //
            // Once per distinct text and mode, never a count (DiagnosticOnce): a text re-set every
            // frame is written once, every new one is written — a 300-line budget fell silent before
            // the line somebody was looking at (2026-10-02).
            if (!TranslatorCore.DebugMode || !DiagnosticOnce.First("RtlPresenter.Log", mode + "\u0001" + logical)) return;
            TranslatorCore.LogDebug($"[RtlPresenter] comp={compId} mode={mode} in : {Escape(logical)}");
            TranslatorCore.LogDebug($"[RtlPresenter] comp={compId} mode={mode} out: {Escape(composed)}");
        }

        // Components already described, so a text refreshed every frame costs one line, once.
        private static readonly HashSet<long> _fontDescribed = new HashSet<long>();

        /// <summary>
        /// Debug only: which font draws a right-to-left text, and where the component sits — the
        /// question a text shown EMPTY raises, and the only one the rest of the log cannot answer
        /// for a component nobody can reach with the inspector (2026-09-25: a card and a menu on
        /// one game stayed blank while the rest of its Arabic rendered).
        /// </summary>
        private static void DescribeFont(object instance, long compId, string settingsFontName)
        {
            if (!TranslatorCore.DebugMode || compId == -1 || !_fontDescribed.Add(compId)) return;
            try
            {
                object font = TypeHelper.GetFont(instance);
                string current = font is UnityEngine.Object uo && uo != null ? uo.name : "(none)";
                string path = instance is UnityEngine.Component c && c != null
                    ? TranslatorCore.GetGameObjectPath(c.gameObject) : "?";
                string role = settingsFontName == null ? "not registered"
                    : FontManager.IsTranslationEnabled(settingsFontName) ? "registered" : "translation off for this font";

                // The material the component draws with, and the texture it samples: a component
                // wearing a material PRESET of the game (an outline, a glow) keeps the game's atlas
                // there while the glyph coordinates now point into ours — the text then draws
                // nothing at all, with the right font named above.
                string material = "?";
                try
                {
                    var matProp = instance.GetType().GetProperty("fontSharedMaterial", BindingFlags.Public | BindingFlags.Instance)
                                  ?? instance.GetType().GetProperty("material", BindingFlags.Public | BindingFlags.Instance);
                    if (matProp?.GetValue(instance, null) is UnityEngine.Material m && m != null)
                    {
                        string tex = m.mainTexture != null ? m.mainTexture.name : "(no texture)";
                        material = $"'{m.name}' shader '{(m.shader != null ? m.shader.name : "?")}' texture '{tex}'";
                    }
                }
                catch (Exception ex) { material = "unreadable: " + ex.Message; }

                // The component's own layout settings that act on every glyph: a game's spacing, its
                // kerning or font features, its own right-to-left flag can move letters a shaped text
                // placed (2026-10-02: words cut around some letters in one game only, the same text
                // and font drawing right everywhere else). Each said as the type has it, absent if not.
                var layout = new System.Text.StringBuilder();
                foreach (var name in new[] { "characterSpacing", "wordSpacing", "characterWidthAdjustment", "enableKerning",
                                             "fontFeatures", "isRightToLeftText", "richText", "parseCtrlCharacters", "enableCulling" })
                {
                    try
                    {
                        var prop = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                        if (prop == null) continue;
                        object value = prop.GetValue(instance, null);
                        string shown = value?.ToString() ?? "null";
                        if (value is System.Collections.IEnumerable list && !(value is string))
                        {
                            var items = new List<string>();
                            foreach (var item in list) items.Add(item?.ToString());
                            shown = "[" + string.Join(",", items.ToArray()) + "]";
                        }
                        layout.Append(' ').Append(name).Append('=').Append(shown);
                    }
                    catch (Exception ex) { layout.Append(' ').Append(name).Append("=unreadable(").Append(ex.GetType().Name).Append(')'); }
                }

                TranslatorCore.LogDebug($"[RtlPresenter] font comp={compId} {instance.GetType().Name} settings='{settingsFontName ?? "-"}' ({role}) drawn with '{current}', material {material}, layout{layout}, at {path}");
            }
            catch (Exception ex) { TranslatorCore.LogDebug($"[RtlPresenter] font comp={compId} unreadable: {ex.Message}"); }
        }

        internal static string Escape(string s)
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
            // Members.Property: no AmbiguousMatchException to catch on a type that re-declares it.
            PropertyInfo prop = Members.Property(type, "isRightToLeftText", BindingFlags.Public | BindingFlags.Instance);
            if (prop?.SetMethod == null) prop = null;
            _rtlProps[type] = prop;
            return prop;
        }
    }
}
