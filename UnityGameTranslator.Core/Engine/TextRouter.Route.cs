using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    public sealed partial class TextRouter
    {
        /// <summary>
        /// Decides what happens to one incoming text: an assembly in parts, a reveal in flight,
        /// something already translated — or an ordinary line to send.
        ///
        /// 🔴 **Split out of the setter prefix so it is not welded to the font work.** Those two
        /// were tressed together in one ~500-line method, so reusing the routing meant dragging the
        /// whole font pipeline with it — and every text framework added since simply copied the
        /// one line it could (the call to the lookup) and inherited none of this. That is why
        /// procedural text, input mirrors and the already-written check existed for TMP, UI.Text
        /// and TextMesh alone.
        ///
        /// ⚠ The player's typing is the caller's to rule out first (it reads the engine's input
        /// fields); it hands the component over to <see cref="ForgetTyped"/> when it does.
        /// </summary>
        public RouteOutcome Route(object comp, long compId, bool isOwnUI, ref string textValue)
        {
            // Own UI (UI-specific translation prompt) — computed once near the top.
            string preTranslateText = textValue;

            // Check concat assembled cache: if this exact text was already assembled
            // by the concat system, apply the cached translation immediately.
            // This prevents scanner refresh from re-queuing assembled texts.
            bool concatCacheHit = false;
            string concatCached;
            if (_concatAssembledCache.TryGetValue(textValue, out concatCached))
            {
                // CN text matched → apply FR translation, skip all translate logic
                textValue = concatCached;
                concatCacheHit = true;
            }
            else if (_concatTranslatedValues.Contains(textValue)
                     || (_host.Readback.PresentedLogical(textValue) is string shownLogical
                         && _concatTranslatedValues.Contains(shownLogical)))
            {
                // Text IS already a translated result → keep as-is
                // ⚠ Its presented form too (shaped right-to-left, word breaks): that is what the
                // component holds, and what a scanner refresh sets again. Unrecognised, that echo
                // of our own assembly was taken for a new text, reset the component's parts, and a
                // translation arriving late had nothing left to be put back into.
                concatCacheHit = true;
            }

            if (concatCacheHit) return RouteOutcome.Translated;

            // Everything below follows this one component. Created here rather than looked up
            // five times: from this point on every branch either reads or writes it.
            ComponentTextState state = compId != -1 ? StateFor(compId) : null;

            // Skip if text is exactly our last translated output (scanner refresh, etc.)
            if (state != null)
            {
                if (state.LastTranslated != null && textValue == state.LastTranslated)
                {
                    // Nothing to translate, but the font work already ran on this component —
                    // leaving without the scale left it at the unscaled size while neighbours that
                    // took the full path were scaled, so a menu or a scoreboard ended up with
                    // mismatched line sizes. The size is idempotent (derived from the cached true
                    // original), so re-asserting it is free.
                    return RouteOutcome.StopButRescale;
                }
            }

            // === Frame tracking for concat detection ===
            // Count set_text calls per component per frame.
            // 2+ calls in same frame → concat mode (procedural text building).
            if (state != null)
            {
                int currentFrame = _host.Frame;
                if (state.LastFrame == currentFrame)
                {
                    state.FrameCallCount++;

                    // Flag as concat ONLY if the text is GROWING (prefix match).
                    // Without this, game init (default→real value = 2 set_text) false-positives.
                    if (state.FrameCallCount >= 2 && _host.ConcatDetection && state.Mode != TextMode.Concat)
                    {
                        string prevRaw = state.LastRaw;
                        // The delta must carry more than layout whitespace: a lone appended
                        // "\n" at start-up is not procedural assembly (see LooksLikeConcatGrowth).
                        if (!string.IsNullOrEmpty(prevRaw)
                            && TextRelations.LooksLikeConcatGrowth(prevRaw, textValue))
                        {
                            EnterConcat(state, compId);
                            _host.LogDebug($"[CONCAT-DETECT] comp={compId} flagged (text grew {prevRaw.Length}c→{textValue.Length}c in frame {currentFrame})");
                        }
                    }
                }
                else
                {
                    state.LastFrame = currentFrame;
                    state.FrameCallCount = 1;

                    // Detect TW pattern on a concat-flagged component:
                    // single set_text per frame with text growing by 1-3 chars = typewriting.
                    // Unflag concat and let TW handle it.
                    if (state.Mode == TextMode.Concat)
                    {
                        string prevRaw2 = state.LastRaw;
                        if (!string.IsNullOrEmpty(prevRaw2)
                            && TextRelations.LooksLikeTypewriterGrowth(prevRaw2, textValue))
                        {
                            LeaveConcat(state);
                            _host.LogDebug($"[CONCAT-UNFLAG] comp={compId} reverted to TW (grew by {textValue.Length - prevRaw2.Length} chars in separate frame)");
                        }
                    }
                }

                // Raw text update happens AFTER the concat handling block below,
                // so the concat block can compare against the PREVIOUS raw text.
            }

            // === Concat handling ===
            // For concat components: track raw text, extract deltas, translate each separately.
            // For non-concat: use existing flow (Translate with TW detection).
            bool handledAsConcat = false;
            bool isConcatComp = state != null && state.Mode == TextMode.Concat;

            if (isConcatComp)
            {
                string lastRaw = state.LastRaw;

                if (!string.IsNullOrEmpty(lastRaw)
                    && TextRelations.Grows(lastRaw, textValue))
                {
                    // Text grew — extract delta (pure source language)
                    string delta = textValue.Substring(lastRaw.Length);
                    string priorPairSource = state.ReadBackSource;
                    string priorPairTranslated = state.ReadBackTranslated;

                    // Store delta for re-assembly later (when AI translations arrive)
                    List<string> deltas = state.Deltas;
                    if (deltas == null)
                    {
                        deltas = new List<string>();
                        // First delta: also store the base text
                        deltas.Add(lastRaw);
                        state.Deltas = deltas;
                    }
                    deltas.Add(delta);

                    // Preserve leading/trailing newlines: AI may strip them during translation.
                    // Extract \n before/after, translate the core, then re-add.
                    SplitNewlines(delta, out string leadingNL, out string deltaCore, out string trailingNL);

                    // Build display: previous translated + translated delta
                    string lastTrans = state.LastTranslated;

                    // What the text on screen was made from — the tracked pair, when it IS what is
                    // on screen; the raw base when nothing was translated yet.
                    string lastSource = string.IsNullOrEmpty(lastTrans) ? lastRaw
                        : priorPairTranslated != null && priorPairTranslated == lastTrans ? priorPairSource
                        : null;
                    string whole = lastSource != null ? lastSource + delta : null;

                    // 🔴 The whole text first, as CONCAT-FR does: a file holding it as one entry
                    // (a page written sentence by sentence and captured whole) translates it as one.
                    // Neither part is queued then — the whole answered.
                    string wholeTranslated = whole != null
                        ? Translate(whole, comp, isOwnUI, skipTypewriting: true, skipQueueing: true)
                        : null;

                    if (whole != null && wholeTranslated != whole)
                    {
                        textValue = wholeTranslated;
                    }
                    else
                    {
                        // Translate core delta directly (skip TW — concat deltas are immediate)
                        string translatedCore = string.IsNullOrEmpty(deltaCore) ? "" : Translate(deltaCore, comp, isOwnUI, skipTypewriting: true);
                        string translatedDelta = leadingNL + translatedCore + trailingNL;

                        if (string.IsNullOrEmpty(lastTrans))
                        {
                            // First part wasn't translated yet (TW was capturing it before concat was detected).
                            // Translate it now.
                            lastTrans = Translate(lastRaw, comp, isOwnUI, skipTypewriting: true);
                            if (string.IsNullOrEmpty(lastTrans)) lastTrans = lastRaw;
                        }

                        textValue = lastTrans + translatedDelta;
                        // What this assembled text was made from, so the next append can try the
                        // whole again — the parts' own translations just replaced the tracked pair.
                        if (whole != null) TrackTranslation(compId, whole, textValue);
                    }
                    state.LastRaw = preTranslateText; // full raw text so far
                    state.LastTranslated = textValue;
                    // Cache the assembled result: raw source → assembled target (runtime only)
                    _concatAssembledCache[preTranslateText] = textValue;
                    _concatTranslatedValues.Add(textValue);
                    handledAsConcat = true;

                    if (_host.DebugMode)
                        _host.LogDebug($"[CONCAT] comp={compId} delta({delta.Length}c)='{Clip(delta, 40)}'");
                }
                // ⚠ Ordinal for the same reason as TextRelations.Grows, which this mirrors: it is
                // the negative half of the very same question, on the very same game text.
                else if (!string.IsNullOrEmpty(lastRaw) && textValue.Length <= lastRaw.Length
                         && !textValue.StartsWith(lastRaw, StringComparison.Ordinal))
                {
                    // Text shrunk or changed completely — component likely reused for different content.
                    // Unflag concat so the new text is treated normally (queued for AI if cache miss).
                    // If the game does concat again (2+ set_text same frame), it'll be re-flagged.
                    state.LastRaw = null;
                    LeaveConcat(state);
                    isConcatComp = false; // update local flag for rest of this call
                    _host.LogDebug($"[CONCAT-RESET] comp={compId} unflagged, text changed from {lastRaw.Length}c to {textValue.Length}c");
                }

                // Raw text tracking is done in the frame tracking block above
            }

            // Also detect concat for non-flagged components (the game appending source text to
            // the translation we already wrote)
            // 🔴 Against what the component HOLDS as well as against the translation: when
            // presenting changed it (shaped right-to-left, word breaks), the game reads that form
            // back and appends to it. Matched against the logical translation alone, such an append
            // was never seen — our shaped text plus the game's new sentence reached the screen as
            // one untranslatable string, the sentence reading backwards under the right-to-left flag.
            string lastTranslatedTarget = state?.LastTranslated;
            string lastShown = !string.IsNullOrEmpty(lastTranslatedTarget) ? ShownFormOf(state, lastTranslatedTarget) : null;
            string appendedTo = handledAsConcat || lastShown == null ? null
                              : TextRelations.Grows(lastTranslatedTarget, textValue) ? lastTranslatedTarget
                              : TextRelations.Grows(lastShown, textValue) ? lastShown
                              : null;
            if (appendedTo != null)
            {
                // Game appended untranslated text to our translation → extract that delta
                string delta = textValue.Substring(appendedTo.Length);

                // Preserve leading/trailing newlines
                SplitNewlines(delta, out string leadNL, out string dCore, out string trailNL);

                // 🔴 The WHOLE text first, when its source is known: what the translation on
                // screen was made from, plus what the game appended. A file holding the page as one
                // entry (a book written sentence by sentence and captured whole) had it, and the
                // sentence alone was not in it — the appended part stayed in the source language
                // beside a translated page until the game happened to rewrite the whole.
                // ⚠ The source is known only when the tracked pair IS what is on screen.
                string priorSource = state.ReadBackTranslated != null && state.ReadBackTranslated == lastTranslatedTarget
                    ? state.ReadBackSource : null;
                string whole = priorSource != null ? priorSource + delta : null;
                string wholeTranslated = whole != null
                    ? Translate(whole, comp, isOwnUI, skipTypewriting: true, skipQueueing: true)
                    : null;

                if (whole != null && wholeTranslated != whole)
                {
                    // A hit: tracked inside, so the next append starts from this page.
                    textValue = wholeTranslated;
                }
                else
                {
                    // 🔴 The appended part is QUEUED when its source is known, as the concat
                    // branch queues its parts. It was never sent: a sentence the file did not hold
                    // stayed in the source language on a translated page for good, since nothing
                    // would ever ask for it. With an unknown source the part may be a fragment of
                    // something else, so it is still only looked up.
                    string transCore = string.IsNullOrEmpty(dCore) ? "" : Translate(dCore, comp, isOwnUI, skipTypewriting: true, skipQueueing: whole == null);
                    string translatedDelta = leadNL + transCore + trailNL;
                    textValue = lastTranslatedTarget + translatedDelta;

                    if (whole != null)
                    {
                        // What this assembled text was made from, so the next append can try the
                        // whole again — the delta's own translation just replaced the tracked pair.
                        TrackTranslation(compId, whole, textValue);

                        // And its parts, so a translation arriving late for one of them is put
                        // back into the page (HasShownParts → ReassembleConcat) — the same record
                        // the concat branch keeps. Continued when the parts already make the
                        // source this append grows from; started over otherwise.
                        var parts = state.Deltas;
                        if (parts == null || string.Join("", parts) != priorSource)
                            parts = state.Deltas = new List<string> { priorSource };
                        parts.Add(delta);
                    }
                }
                state.LastTranslated = textValue;
                // Also cache with the raw text as key (for scanner refresh lookups)
                _concatAssembledCache[preTranslateText] = textValue;
                _concatTranslatedValues.Add(textValue);
                handledAsConcat = true;

                if (_host.DebugMode)
                    _host.LogDebug($"[CONCAT-FR] comp={compId} delta({delta.Length}c)='{Clip(delta, 40)}'");
            }

            if (!handledAsConcat)
            {
                // For concat components: check cache but do NOT queue full text to AI.
                // Deltas are already queued individually by the concat handler above.
                // For non-concat: normal flow (cache check + queue if miss).
                textValue = Translate(textValue, comp, isOwnUI, skipQueueing: isConcatComp);

                // Track translated text for concat detection (target-language prefix matching)
                if (state != null && textValue != preTranslateText)
                {
                    state.LastTranslated = textValue;
                    // For concat components: also remember the translated text so the
                    // different target versions (AI vs concat-assembled) are all recognized.
                    if (isConcatComp)
                        _concatTranslatedValues.Add(textValue);
                }
                else if (state != null && textValue == preTranslateText)
                {
                    state.LastTranslated = null;
                    // For concat components: the unchanged text might be an AI translation
                    // (from Apply OK) that we don't recognize. Remember it to prevent re-queue.
                    if (isConcatComp && textValue.Length > 20)
                        _concatTranslatedValues.Add(textValue);
                }
            }

            // Update raw text tracking AFTER concat/translate blocks
            // (so next set_text can compare against this value)
            if (state != null)
                state.LastRaw = preTranslateText;

            return RouteOutcome.Translated;
        }

        /// <summary>Leading and trailing newlines apart from the core: a model may strip them.</summary>
        private static void SplitNewlines(string text, out string leading, out string core, out string trailing)
        {
            leading = "";
            trailing = "";
            core = text;
            while (core.Length > 0 && core[0] == '\n') { leading += "\n"; core = core.Substring(1); }
            while (core.Length > 0 && core[core.Length - 1] == '\n') { trailing = "\n" + trailing; core = core.Substring(0, core.Length - 1); }
        }

        /// <summary>At most <paramref name="max"/> characters of a text, for a log line.</summary>
        private static string Clip(string text, int max)
            => text == null || text.Length <= max ? text : text.Substring(0, max) + "...";

        // === CONCAT CACHES AND REASSEMBLY ===

        /// <summary>
        /// Whether this component's text was built from parts it still shows — so a translation
        /// arriving late for one of them can be put back into the whole (<see cref="ReassembleConcat"/>).
        ///
        /// ⚠ Parts, not the concat MODE: a game appending to a text we translated (CONCAT-FR) builds
        /// a page in parts too without ever being flagged, and asking about the mode left its late
        /// translations with nowhere to go — the page stayed half in the source language.
        /// </summary>
        public bool HasShownParts(long compId, object component)
        {
            var state = PeekState(compId);
            if (state?.Deltas == null || state.Deltas.Count == 0 || state.LastTranslated == null) return false;

            // The parts must be what the text on screen was made from: a component in concat
            // mode, or one whose tracked source IS those parts joined. A text translated normally
            // since then tracks its own source, and its leftover parts are not its own.
            if (state.Mode != TextMode.Concat && state.ReadBackSource != string.Join("", state.Deltas)) return false;

            // ⚠ Only while the component still shows what the parts made: a page the game has
            // replaced since must never be overwritten by the rebuild of the one before it.
            string current = _host.GetText(component);
            return current == state.LastTranslated || current == ShownFormOf(state, state.LastTranslated);
        }

        /// <summary>Look up a text in the concat assembled cache. Returns the translation or null.</summary>
        public string GetConcatCacheResult(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return null;
            string result;
            return _concatAssembledCache.TryGetValue(rawText, out result) ? result : null;
        }

        /// <summary>
        /// Re-assemble a concat component's text using stored deltas and current cache.
        /// Returns the assembled text, or null if no deltas stored.
        /// </summary>
        public string ReassembleConcat(long compId, object component)
        {
            var state = PeekState(compId);
            List<string> deltas = state?.Deltas;
            if (deltas == null || deltas.Count == 0)
                return null;

            // The whole text first: a file holding it as one entry translates it as one, rather
            // than as parts glued together (or parts still in the source language).
            string rawKey = string.Join("", deltas);

            // 🔴 The previous assembly goes first: this is what it is rebuilt to replace, and the
            // lookup below answers from it before the file — the old mix of translated and source
            // parts came back as "the whole page's translation" and was written again, so a late
            // translation never reached the screen.
            InvalidateConcatCache(rawKey);
            string whole = Translate(rawKey, component, false, skipTypewriting: true, skipQueueing: true);
            if (whole != rawKey)
            {
                _concatAssembledCache[rawKey] = whole;
                _concatTranslatedValues.Add(whole);
                state.LastTranslated = whole;
                return whole;
            }

            var result = new System.Text.StringBuilder();
            foreach (string part in deltas)
            {
                // Preserve newlines
                SplitNewlines(part, out string leading, out string core, out string trailing);

                string translated = string.IsNullOrEmpty(core) ? "" :
                    Translate(core, component, false, skipTypewriting: true, skipQueueing: true);
                result.Append(leading);
                result.Append(translated);
                result.Append(trailing);
            }

            string assembled = result.ToString();

            // What the component now shows, and what it was made from: the next append builds on
            // THIS text, not on the one assembled before the late translation arrived.
            state.LastTranslated = assembled;
            TrackTranslation(compId, rawKey, assembled);

            // Update caches
            _concatAssembledCache[rawKey] = assembled;
            _concatTranslatedValues.Add(assembled);

            return assembled;
        }

        /// <summary>Invalidate a concat cache entry so it gets re-assembled with fresh translations.</summary>
        public void InvalidateConcatCache(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string oldValue;
            if (_concatAssembledCache.TryGetValue(text, out oldValue))
            {
                _concatAssembledCache.Remove(text);
                _concatTranslatedValues.Remove(oldValue);
            }
        }

        /// <summary>Check if a text is a known concat translated value.</summary>
        public bool IsConcatTranslatedValue(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return _concatTranslatedValues.Contains(text);
        }

        /// <summary>
        /// This component builds its text in parts. Cancels any reveal in flight — the two are
        /// exclusive, and this is now the ONE place that says so.
        /// </summary>
        private void EnterConcat(ComponentTextState state, long compId)
        {
            ForgetTypewriting(state);
            _typewritingPending.Remove(compId);
            state.Mode = TextMode.Concat;
        }

        /// <summary>
        /// This component is no longer building its text in parts. The stored parts go with it:
        /// keeping them would let a later assembly resume from a text that is gone.
        /// </summary>
        private static void LeaveConcat(ComponentTextState state)
        {
            if (state.Mode == TextMode.Concat) state.Mode = TextMode.Normal;
            state.Deltas = null;
        }

        // === READ-BACK DETECTION ===

        /// <summary>
        /// Record the translation applied to a component (called after successful translation).
        /// Read back by <see cref="DetectReadBack"/> when the game appends to what we wrote.
        /// </summary>
        public void TrackTranslation(long compId, string original, string translated)
        {
            if (compId == -1 || string.IsNullOrEmpty(original) || string.IsNullOrEmpty(translated)) return;
            var state = StateFor(compId);
            state.ReadBackSource = original;
            state.ReadBackTranslated = translated;
        }

        /// <summary>
        /// Record the form a component is displayed in when presenting changed our text. Called by
        /// the presenter for every form it writes. The logical text is found by walking back the
        /// stages (word breaks, then right-to-left, then a reflow's final lines): each one
        /// registered what it came from, so the chain is followed rather than guessed.
        /// </summary>
        public void NotePresented(long compId, string logical, string presented)
        {
            if (compId == -1 || string.IsNullOrEmpty(logical) || string.IsNullOrEmpty(presented)) return;
            // Bounded by the number of presentation stages there are (syllabic, right-to-left,
            // reflow) — each step back is one stage.
            string root = logical;
            for (int stage = 0; stage < 3; stage++)
            {
                string before = _host.Readback.PresentedLogical(root);
                if (before == null) break;
                root = before;
            }
            var state = StateFor(compId);
            state.Presented = presented;
            state.PresentedFrom = root;
        }

        /// <summary>
        /// The form this component shows for <paramref name="logical"/> when presenting changed it,
        /// or <paramref name="logical"/> itself. What a game that reads the component back holds.
        /// </summary>
        private static string ShownFormOf(ComponentTextState state, string logical) =>
            state.Presented != null && state.PresentedFrom == logical ? state.Presented : logical;

        /// <summary>
        /// Detect if incoming text is a game read-back of translated text with appended content.
        /// If so, reconstruct the source-language equivalent and VERIFY it exists in cache.
        /// Returns null if not a read-back or if reconstructed text has no cache hit.
        /// </summary>
        public string DetectReadBack(long compId, string incomingText)
        {
            if (compId == -1 || string.IsNullOrEmpty(incomingText)) return null;
            var state = PeekState(compId);
            if (state == null || state.ReadBackSource == null || state.ReadBackTranslated == null) return null;

            string original = state.ReadBackSource;
            string translated = state.ReadBackTranslated;

            // 🔴 What the component HOLDS, which is not the translation when presenting changed it
            // (shaped right-to-left, word breaks): that form is what the game reads back and
            // appends to. Matching the logical translation alone missed every such append.
            string shown = ShownFormOf(state, translated);

            // The incoming text must START WITH the translated text but be LONGER
            // (the game appended something to the read-back).
            // ⚠ The SAME question the typewriting and concat detectors ask, so it asks it with the
            // same words — it was written out separately here and kept the linguistic comparison
            // when the other six moved to ordinal, which would have cut the suffix in the wrong
            // place on any text carrying a soft hyphen or a joiner.
            string prefix = TextRelations.Grows(translated, incomingText) ? translated
                          : TextRelations.Grows(shown, incomingText) ? shown
                          : null;
            if (prefix != null)
            {
                // Reconstruct: original source text + the appended suffix
                string suffix = incomingText.Substring(prefix.Length);
                string reconstructed = original + suffix;

                // SAFETY: only accept the reconstruction if it produces a cache hit.
                // If the reconstructed text doesn't match any known key, this is NOT
                // a read-back — it's a legitimate new text that happens to start with
                // a previous translation. Return null to let normal flow handle it.
                //
                // 🔴 And not only against false positives: every caller takes what comes back as a
                // TRANSLATION (Translate indexes it as our own output). Returned without a key
                // behind it, the source text was learnt as "already translated" and the page
                // stayed in the source language for good. An append the file has no whole key for
                // is the concat path's job (CONCAT-FR in Route), which translates the appended
                // part on its own.
                string normalizedReconstructed = NormalizeForCacheLookup(reconstructed);
                if (!_host.GameStore.ContainsKey(normalizedReconstructed))
                {
                    if (_host.DebugMode)
                        _host.LogDebug($"[READBACK-REJECT] comp={compId} reconstructed text has no cache hit, treating as new text\n  reconstructed({reconstructed.Length}c)='{reconstructed}'");
                    return null;
                }

                if (_host.DebugMode)
                    _host.LogDebug($"[READBACK] comp={compId} detected read-back+append → cache hit!\n  incoming({incomingText.Length}c)='{incomingText}'\n  reconstructed({reconstructed.Length}c)='{reconstructed}'");

                return reconstructed;
            }

            return null;
        }

        /// <summary>
        /// The key shape of a text, as the file stores its keys.
        /// ⚠ isOwnUI is false on purpose: this probe has always lifted the variables out whichever
        /// side asked.
        /// </summary>
        public string NormalizeForCacheLookup(string text)
            => TextGate.KeyShape(text, isOwnUI: false, _host.Variables, _host.NormalizeNumbers, out _, out _);
    }
}
