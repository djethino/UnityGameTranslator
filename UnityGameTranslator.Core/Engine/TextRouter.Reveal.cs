using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    public sealed partial class TextRouter
    {
        // === TYPEWRITING DETECTION ===
        //
        // Text growing a few characters at a time on one component. While a reveal is in progress
        // the text is held back, so a half-written line is never sent for translation: every
        // cache-miss text waits TypewritingStabilizeMs without changing before being handed over.
        // Cache hits bypass all of this.
        //
        // The state lives in ComponentTextState (the Typewriting* fields); _typewritingPending is
        // the index of the components that have one in flight.
        //
        // ⚠ A wait of family ② (CLAUDE.md): the game never says "I have finished writing this
        // text"; nothing having moved for this long IS the signal.
        public const float TypewritingStabilizeMs = 500f; // ms without change = text is final

        /// <summary>Check if a component is currently being tracked for typewriting.</summary>
        public bool IsInTypewritingState(long compId)
        {
            var state = PeekState(compId);
            return state != null && state.Mode == TextMode.Typewriter;
        }

        /// <summary>
        /// This component's text has just been looked at. If a reveal is in flight and the text
        /// has grown past what is being held, hold the longer one and start the wait again.
        ///
        /// 🔴 **The one door, and it used to be one of eleven** (2026-09-10). A reveal is only
        /// followed through <see cref="IsTypewritingInProgress"/>, which a text already known never
        /// reaches: the lookup answers and returns. Eleven exits in that lookup say "this text is
        /// known" — the exact key, the normalised key, the trimmed key, a number pattern, a concat
        /// result — and exactly ONE of them told the reveal what it had seen. So the moment a
        /// growing sentence became recognisable (the numbers in it are lifted into placeholders, so
        /// recognition arrives BEFORE the last character), the reveal went blind: it kept holding
        /// the last unrecognised fragment, waited out its five hundred milliseconds on a text the
        /// game had already finished, and sent that fragment to the model.
        ///
        /// Measured on one dialogue of one game, visited three times:
        /// <code>
        /// 15:16:52  finalised 37 characters → model call → fragment cached → Apply SKIP
        /// 15:23:37  finalised 36 characters → model call → fragment cached → Apply SKIP
        /// 15:23:47  finalised 35 characters → model call → fragment cached → Apply SKIP
        /// </code>
        /// Three keys for one sentence, each a truncation of the next, each paid for, each landing
        /// after the screen already showed the translation. And a "translating" notice every time,
        /// on a line the player could see was already translated.
        ///
        /// ⚠ **An identical text is NOT a reason to wait longer.** It used to restart the wait, and
        /// that only stayed harmless because the caller was rare: called from the top of the lookup,
        /// where the sweep re-reads the same unchanged text several times a second, it would defer
        /// the line for as long as it stayed on screen — never translated, and nothing said.
        /// </summary>
        public void NoteTextSeen(long compId, string currentText)
        {
            if (compId == -1 || string.IsNullOrEmpty(currentText)) return;
            var state = PeekState(compId);
            if (state == null || state.Mode != TextMode.Typewriter) return;

            // Already handed over: IsTypewritingInProgress owns what happens next, and re-holding
            // here would cancel a finalisation that has already been decided.
            if (state.TypewritingQueued) return;

            // 🔴 **Growing, or the same content with its markup moved.** A reveal by markup never
            // grows — its tag walks — and a frame recognised as a known sentence (TextRouter.Dressed)
            // returns before the reveal sees it: told only of growth, the reveal kept an earlier
            // frame and sent it when the walk ended (measured: `power<color…>.</color>`, a fragment,
            // at every showing of a known sentence).
            bool grows = TextRelations.Grows(state.TypewritingText, currentText);
            bool redressed = !grows && state.TypewritingText != currentText
                             && TextRelations.SameContent(state.TypewritingText, currentText);
            if (!grows && !redressed) return;

            // The per-character trace of a reveal, which used to come from [TW-CHECK]: a text that
            // grows is now held here, so IsTypewritingInProgress sees it unchanged and says nothing.
            if (_host.DebugMode)
                _host.LogDebug($"[TW-GROW] comp={compId} {state.TypewritingText.Length}c → {currentText.Length}c{(redressed ? " (markup moved)" : "")} '{Head(currentText)}'");

            HoldTypewriting(state, compId, currentText, _host.Now, grew: true);
        }

        /// <summary>First 30 characters of a text, for a log line.</summary>
        private static string Head(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Length > 30 ? text.Substring(0, 30) : text;
        }

        /// <summary>
        /// Hold this component's text back: a reveal is in progress, or has just restarted.
        /// Puts it on the work list so the stabilizer will look at it.
        /// </summary>
        /// <param name="grew">It got here by growing (or by its markup walking): a reveal in progress,
        /// as opposed to a text the game wrote whole in one go.</param>
        private void HoldTypewriting(ComponentTextState state, long compId, string text, float now, bool grew)
        {
            // Handed over, grown from once, and now growing again before it settled: the component
            // is revealing from what it sent — that text was the head of a reveal (TextRouter.Heads).
            // Any other new start ends the question: one longer text that stays is a replacement.
            string resumedFrom = state.ResumedFrom;
            state.ResumedFrom = null;
            if (grew && resumedFrom != null) ProveHead(compId, resumedFrom);
            state.HeldAsHead = null;

            state.Mode = TextMode.Typewriter;
            state.TypewritingGrew = grew;
            state.TypewritingText = text;
            state.TypewritingSince = now;
            state.TypewritingQueued = false;
            _typewritingPending.Add(compId);
        }

        public bool IsTypewritingInProgress(long compId, string newText, object component = null)
        {
            if (compId == -1 || string.IsNullOrEmpty(newText)) return false;
            if (!_host.TypewritingDetection) return false;

            var state = StateFor(compId);
            if (component != null) state.Target = component;

            // Concat components are handled by the concat system, not TW
            if (state.Mode == TextMode.Concat) return false;

            float now = _host.Now;

            if (state.Mode == TextMode.Typewriter)
            {
                if (state.TypewritingText == newText)
                {
                    // Same text, still pending
                    return true;
                }

                // 🔴 **The same sentence, dressed differently, is the same sentence.** A game that
                // reveals by walking a colour tag along a finished line changes its raw text every
                // frame while saying exactly the same thing — and every frame was read as a new
                // line. One sentence cost 93 requests to the model and 91 cache entries, and a
                // quarter of that game's file was five sentences written out fifty-two times.
                //
                // ⚠ Held, not finalised: the reveal IS in progress, and the translation must not
                // reach the screen until it ends — the tag's position is the animation's own
                // state. The newest raw form is kept so that what is eventually finalised is the
                // line as the game leaves it.
                if (TextRelations.SameContent(state.TypewritingText, newText))
                {
                    HoldTypewriting(state, compId, newText, now, grew: true);
                    return true;
                }

                // 🔴 **The line that just arrived is the previous one EXPANDED**: the game wrote its
                // own template on the component (`*Overclock* ({0}): Add {1} Strength.`) and has now
                // resolved its tokens into markup and values. The template was never a line anybody
                // reads — and its translation, written back, stops the game finding `*Overclock*`
                // and `{0}` to expand at all.
                //
                // ⚠ Taken back rather than merely not sent: the template is stable long enough to
                // be queued (501 ms, measured), so by the time the expansion proves what it was, it
                // is already waiting. See TextRouter.Templates for what that does and, as
                // importantly, what it refuses to do.
                if (TextRelations.SameAfterExpansion(state.TypewritingText, newText))
                {
                    ForgetTemplate(state.TypewritingText);
                    HoldTypewriting(state, compId, newText, now, grew: false);
                    return true;
                }

                float elapsed = (now - state.TypewritingSince) * 1000f;
                bool isGrowing = TextRelations.Grows(state.TypewritingText, newText);

                // 🔴 **A text handed over, then another write: this is where a head is told from a
                // whole text** (TextRouter.Heads). Held as a head, it either goes on — the finding
                // holds — or is replaced by something else — it was whole this time, and is sent.
                // Handed over normally and now grown from, it MAY be a head: one more growth before
                // it settles proves it (HoldTypewriting); a longer text that stays proves nothing.
                //
                // ⚠ **Only a text WRITTEN at once.** One the component revealed itself and then
                // stopped on is a reading pause — a dialogue box waiting for a click before it adds
                // its next sentence — and is read for as long as the player likes: held, it would
                // stay in the source language for exactly that time. A resumed recording is the
                // other shape: its first part set in one write, then the reveal going on.
                string resumedFrom = null;
                if (state.TypewritingQueued)
                {
                    if (state.HeldAsHead != null)
                    {
                        if (isGrowing) state.HeldAsHead = null;
                        else RefuteHead(compId, state);
                    }
                    else if (isGrowing && !state.TypewritingGrew)
                    {
                        resumedFrom = state.TypewritingText;
                    }
                }

                // Log every call for typewriting components
                if (_host.DebugMode)
                {
                    _host.LogDebug($"[TW-CHECK] comp={compId} prev={state.TypewritingText.Length}c new={newText.Length}c growing={isGrowing} elapsed={elapsed:F0}ms queued={state.TypewritingQueued}\n  prevText='{state.TypewritingText}'\n  newText='{newText}'");
                }

                if (isGrowing && elapsed < TypewritingStabilizeMs)
                {
                    HoldTypewriting(state, compId, newText, now, grew: true);
                    return true;
                }

                // Detect TW overwrite: game is writing new text OVER our translation.
                // Pattern: text shrinks or changes while previous state was already queued/translated.
                // Each intermediate state mixes source and target → DO NOT finalize, keep deferring.
                bool isShrinkingOverwrite = !isGrowing && state.TypewritingQueued
                                            && newText.Length < state.TypewritingText.Length;
                if (isShrinkingOverwrite)
                {
                    // Don't finalize the mixed state. Just update tracking and keep deferring.
                    // ⚠ Not "grew": a shorter text replacing one already sent is as often a new
                    // tooltip as a game typing over our text, and only growth proves a reveal —
                    // the next write says so if it is one. Marked as grown, a tooltip written whole
                    // was taken for the head of a longer known line and never sent (2026-09-26).
                    HoldTypewriting(state, compId, newText, now, grew: false);
                    return true;
                }

                // Text changed completely (not StartsWith) or grew after long pause.
                //
                // 🔴 **Never the shorter one while it is still GROWING.** If Grows is true, a
                // longer version of this very text is in hand at this instant — so the one being
                // finalised is provably not final. A game that pauses mid-sentence (a comma, a
                // breath) waited past the stabiliser and had its half-written line finalised,
                // translated and cached: the fragment entered the file, the complete sentence
                // never did, and the line stayed in the game's own language. Observed with a
                // 620 ms pause on a reveal running at 40 ms a character.
                //
                // ⚠ Nothing is lost by waiting: the stabiliser finalises as soon as the text stops
                // moving, which is what ends every reveal.
                //
                // 🔴 **And never one whose own wait had not run out.** Two mechanisms answer "is
                // this final?" — the stabiliser, which waits, and this branch, which infers it from
                // a replacement. They contradicted each other: a text replaced while the stabiliser
                // was still holding it was declared final by this line, although nothing had
                // decided it was. Same sentence as the rule above, other side: a text that has been
                // REPLACED before it settled is not final either.
                //
                // ⚠ What that cost, measured on a game whose ability text is a template it expands
                // in place (2026-09-10): the component was set to
                // `*Overclock* ({0}): Add {1} Strength.` and 317 ms later to the expanded form —
                // and the TEMPLATE was queued, translated, and stored as
                // `*Surcadence* ({[!v*0]}): Ajoute {[!v*1]} de Force.` A translated template is not
                // merely wasted: written back, the game looks for `*Overclock*` and `{0}` to expand
                // and finds neither.
                //
                // ⚠ It uses the stabiliser's own delay and adds no number of its own. What it gives
                // up is a line the game replaced within half a second of showing it — which nobody
                // read, and which is not worth a call to a model. A dialogue line replaced after
                // three seconds settles exactly as before.
                bool settled = elapsed >= TypewritingStabilizeMs;
                if (!isGrowing && !state.TypewritingQueued)
                {
                    if (settled)
                    {
                        _host.LogDebug($"[TW-FINAL] comp={compId} isGrowing={isGrowing} elapsed={elapsed:F0}ms\n  prev({state.TypewritingText.Length}c)='{state.TypewritingText}'\n  new({newText.Length}c)='{newText}'");
                        ProcessFinalizedText(compId, state.TypewritingText, stillShown: false);
                    }
                    else if (WrittenWhole(state))
                    {
                        // 🔴 Written whole in one go, then replaced: not a reveal cut short but a
                        // text somebody saw — a tooltip passed over in 0.3 s is still read. Dropped,
                        // it was dropped at every pass and stayed in the source language for good
                        // (2026-09-26, a row of attribute tooltips swept by the pointer).
                        _host.LogDebug($"[TW-WHOLE] comp={compId} written whole, replaced after {elapsed:F0}ms — sent as it was\n  ({state.TypewritingText.Length}c)='{state.TypewritingText}'");
                        ProcessFinalizedText(compId, state.TypewritingText, stillShown: false);
                    }
                    else
                    {
                        _host.LogDebug($"[TW-DROP] comp={compId} replaced after {elapsed:F0}ms, before it had settled — dropped, not sent\n  prev({state.TypewritingText.Length}c)='{state.TypewritingText}'\n  new({newText.Length}c)='{newText}'");
                    }
                }

                // Store new text as new start, defer it
                HoldTypewriting(state, compId, newText, now, grew: false);
                state.ResumedFrom = resumedFrom;
                return true;
            }

            // First time — defer for stabilization
            if (_host.DebugMode)
            {
                // ⚠ The TYPE and the object's name, because on one game this is the only trace a
                // tooltip leaves: its text never comes through a patched setter, so nothing else
                // says what kind of component the mod is actually looking at.
                _host.LogDebug($"[TW-NEW] comp={compId} {_host.Describe(state.Target)} FIRST text({newText.Length}c)='{newText}'");
            }
            HoldTypewriting(state, compId, newText, now, grew: false);
            return true;
        }

        /// <summary>
        /// The text held was written whole — it never grew nor had its markup walk — and reads as
        /// a finished text: longer than one step of a reveal, and not a template waiting for its
        /// values (a `{0}` the game has not filled in yet is never a line anybody reads).
        /// </summary>
        private static bool WrittenWhole(ComponentTextState state)
        {
            string text = state.TypewritingText;
            return !state.TypewritingGrew
                   && text != null && text.Length > TextRelations.TypewriterMaxCharsPerStep
                   && !TemplateSlot.IsMatch(text);
        }

        private static readonly System.Text.RegularExpressions.Regex TemplateSlot =
            new System.Text.RegularExpressions.Regex(@"\{\d+(:[^{}]*)?\}", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Process a finalized typewriting text: queue for AI if not in cache,
        /// or re-trigger the write if already cached.
        /// </summary>
        /// <param name="stillShown">
        /// The text is still on the component (the stabiliser), as opposed to being replaced at this
        /// very write. Replaced without going on, a text is whole — so a head finding about it no
        /// longer holds (TextRouter.Heads).
        /// </param>
        private void ProcessFinalizedText(long compId, string text, bool stillShown)
        {
            if (string.IsNullOrEmpty(text)) return;

            string normalizedText = NormalizeForCacheLookup(text);
            bool inCache = _host.GameStore.ContainsKey(normalizedText);
            // Also check reverse cache — text might already be translated (re-set by game).
            // The exact probe alone missed the re-decorated form, so the stabilizer queued our own
            // translation for a second pass: the entry was refused at storage, but the AI call had
            // already been paid for and the "translating" overlay shown.
            // Same question as every other gate, same answer: inCache already covered the key, so
            // what remains is "is this text itself target language?".
            bool alreadyTranslated = !inCache && _host.Readback.IsAlreadyTarget(text, normalizedText.TrimEnd(), ownUi: false);
            // 🔴 **Held only where this place has resumed a reveal from it before** (TextRouter.Heads)
            // — never because it begins a line the file holds. That comparison needed an exception
            // per game and still left a character sheet's 性格 纯粹 in the source language for good.
            // Replaced at this write without going on, it was whole this time: the finding goes.
            bool headHere = !inCache && !alreadyTranslated && IsHeadHere(compId, normalizedText);
            if (headHere && !stillShown)
            {
                DropHead(compId, text);
                _host.Log($"[TW-HEAD] comp={compId} replaced without going on — it was whole this time: no longer held here, sent: '{Head40(text)}'");
                headHere = false;
            }

            if (_host.DebugMode)
            {
                _host.LogDebug($"[TW-FINALIZE] comp={compId} inCache={inCache} alreadyTranslated={alreadyTranslated} headHere={headHere} text({text.Length}c)='{text}'");
            }

            if (inCache)
            {
                var target = TargetOf(compId);
                if (target != null) _host.Write(target, text);
            }
            else if (alreadyTranslated)
            {
                // Text is already in target language (reverse cache hit) — skip
                if (_host.DebugMode)
                    _host.LogDebug($"[TW-FINALIZE] SKIP already translated: '{Head40(text)}'");
            }
            else if (PeekState(compId)?.ReadBackSource == text && KnownUnderSpan(text, false, _host.GameStore) != null)
            {
                // The last frame of a reveal by markup whose sentence is known: the component was
                // given its translation, uncovered, for this very frame (TextRouter.Dressed) —
                // ReadBackSource says so, it is what that answer recorded. Nothing to send. A text
                // that merely ends on a span was never given one, and goes on to the queue.
                _host.LogDebug($"[TW-FINALIZE] comp={compId} a known sentence uncovered by markup — shown, not sent: '{Head40(text)}'");
            }
            else if (headHere)
            {
                var state = PeekState(compId);
                if (state != null) state.HeldAsHead = text;
                _host.Log($"[TW-PARTIAL] comp={compId} this component went on revealing from this text before — held, not sent, until what follows says otherwise: '{Head40(text)}'");
            }
            else if (AssembleLines(compId, TargetOf(compId), text, false, skipQueueing: false) is string assembled)
            {
                // 🔴 It holds a line that is already a translation — ours, read back and built
                // into this text by the game (TextRouter.Lines). Sent whole, that line went to the
                // model again with the game's own lines around it, and the mix was stored. Only
                // the other lines were sent; what is known goes up now.
                var target = TargetOf(compId);
                if (target != null && assembled != text) _host.Write(target, assembled);
            }
            else
            {
                object target = TargetOf(compId);
                // A stabilized text queued without its component can never be delivered: the
                // answer would land in the cache and the screen would keep the source (bench
                // 2026-09-03: a menu stuck in English with every translation cached). Said.
                if (target == null)
                    _host.LogWarning($"[TW-FINALIZE] comp={compId} has no tracked target — queued without a component, the answer will not reach the screen: '{Head40(text)}'");
                _host.Queue(text, target, false);
            }
        }

        /// <summary>
        /// What an id points at: the target the state recorded, else whatever the host still
        /// knows under that id.
        /// </summary>
        private object TargetOf(long id)
        {
            // The state's own record first: it is set by every path that tracks the component.
            var state = PeekState(id);
            if (state?.Target != null)
            {
                if (_host.IsGone(state.Target)) state.Target = null;
                else return state.Target;
            }
            return _host.FindTarget(id);
        }

        /// <summary>
        /// Check for stabilized typewriting texts and queue them for translation.
        /// Called once per tick (main thread).
        /// </summary>
        public void ProcessStabilizedTypewriting()
        {
            if (_typewritingPending.Count == 0) return;

            float now = _host.Now;
            var toDrop = new List<long>();

            foreach (long compId in _typewritingPending)
            {
                // 🔴 An id with no state left can never do anything again, so it is dropped rather
                // than skipped. Skipping it kept it in the set for good, and one such id is enough
                // to stop `Count == 0` from ever being true again — the early return above then
                // never fires and this allocates a List every frame for the rest of the session.
                // Self-healing on purpose: the state can vanish through ClearTypewritingState (scene
                // unload) or through the concat/mirror paths, and this covers all of them at once.
                var state = PeekState(compId);
                if (state == null || state.Mode != TextMode.Typewriter) { toDrop.Add(compId); continue; }
                float elapsed = (now - state.TypewritingSince) * 1000f;
                if (elapsed >= TypewritingStabilizeMs)
                    toDrop.Add(compId);
            }

            // Carries both the stabilized ids and the stateless ones; the latter fall out at the
            // mode test below, after having been removed from the set.
            foreach (long compId in toDrop)
            {
                _typewritingPending.Remove(compId);

                var state = PeekState(compId);
                // Concat components fall out here for free: the mode is exclusive, so one that
                // switched to assembling is no longer Typewriter. That used to need its own branch.
                if (state == null || state.Mode != TextMode.Typewriter) continue;
                if (state.TypewritingQueued) continue; // Already processed this stabilized text

                // Mark as queued but keep the state — if more chars are added,
                // IsTypewritingInProgress will detect it and reset.
                state.TypewritingQueued = true;

                _host.LogDebug($"[TW-STAB] comp={compId} stabilized after {(now - state.TypewritingSince) * 1000:F0}ms text='{Head40(state.TypewritingText)}'");
                ProcessFinalizedText(compId, state.TypewritingText, stillShown: true);
            }
        }

        /// <summary>
        /// Drop the reveal being followed on this component, keeping everything else.
        /// ⚠ Only leaves Typewriter mode — it must not drag a component out of Concat, since
        /// entering Concat calls this to cancel whatever reveal was in flight.
        /// </summary>
        private static void ForgetTypewriting(ComponentTextState state)
        {
            if (state.Mode == TextMode.Typewriter) state.Mode = TextMode.Normal;
            state.TypewritingText = null;
            state.TypewritingSince = 0f;
            state.TypewritingQueued = false;
            state.HeldAsHead = null;
            state.ResumedFrom = null;
        }

        /// <summary>
        /// Clear typewriting state (on scene change, settings change, etc.)
        ///
        /// ⚠ Only the reveal, not the whole record: concat mode, the deltas and the read-back
        /// tracking are not what this method is about, and dropping them here would silently reset
        /// the detection on every scene unload.
        ///
        /// ⚠ Deliberately does NOT touch _typewritingPending: that set is the work list, and
        /// ProcessStabilizedTypewriting drops any id whose reveal is gone. Emptying it here would
        /// work too, but only for this one cause — the self-healing covers every cause.
        /// </summary>
        public void ClearTypewritingState()
        {
            foreach (var state in _componentState.Values)
                ForgetTypewriting(state);
        }

        // === READ BY THE REVEAL SCALER (maxVisibleCharacters) ===

        /// <summary>
        /// The pair this component shows — source and translation — when it has one: what a
        /// reveal counted on the original is carried over to (Engine/RevealScale).
        /// </summary>
        /// <param name="presented">
        /// The form the translation is displayed in — itself when presenting changed nothing. A
        /// component holding shaped right-to-left text holds THIS, and a reveal on it was never
        /// recognised as one on our translation (a recorded book, 2026-09-26: 572 reveal counts,
        /// none carried over).
        /// </param>
        public bool TryGetShownPair(long compId, out string source, out string translated, out string presented)
        {
            var state = PeekState(compId);
            source = state?.ReadBackSource;
            translated = state?.ReadBackTranslated;
            presented = state != null && translated != null ? ShownFormOf(state, translated) : null;
            return source != null && translated != null;
        }
    }
}
