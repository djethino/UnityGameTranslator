namespace UnityGameTranslator.Core
{
    public sealed partial class TextRouter
    {
        // Bounded debug lines: each says something worth seeing once, never worth a flooded log.
        private int _dbgTwCacheHit;
        private int _dbgCacheHitNormLog;
        private int _dbgReverseMiss;

        /// <summary>
        /// Translate with component tracking for async updates: the answer to show now, the line
        /// queued when nobody has it. Treats multiline text as a single unit to ensure proper
        /// component tracking.
        /// </summary>
        /// <param name="isOwnUI">The mod's own labels: looked up in their own file, never queued from here.</param>
        /// <param name="skipTypewriting">Do not ask the reveal (a concat delta: immediate by design).</param>
        /// <param name="skipQueueing">Do not queue (a concat component: its deltas are queued one by one).</param>
        public string Translate(string text, object component, bool isOwnUI = false, bool skipTypewriting = false, bool skipQueueing = false)
        {
            // Switched off, or nobody has agreed to any of this yet. This is THE bottleneck every
            // translation path goes through — the setter patches included, which is what made a
            // cache full of translations show up on screen while the wizard was still open.
            if (!_host.TranslationsActive) return text;

            if (string.IsNullOrEmpty(text))
                return text;

            // Don't split multiline - treat as single unit for proper component tracking
            // (IsNumericOrSymbol check is in TranslateSingle — no need to call twice)
            string result = TranslateSingle(text, component, isOwnUI, skipTypewriting, skipQueueing);
            if (result != text)
            {
                _host.CountTranslated();
                _host.Showing(result, component);

                // Into the index of the side this text came from — see ReadbackIndex.
                // Index straight away: the read-back happens within the same session, often within
                // the same frame, so waiting for the next cache load would miss the whole point.
                _host.Readback.Index(text, result, isOwnUI, _host.NormalizeNumbers);
            }
            return result;
        }

        private string TranslateSingle(string text, object component, bool isOwnUI, bool skipTypewriting, bool skipQueueing)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (TextNormalization.IsNumericOrSymbol(text))
                return text;

            // 🔴 The game's own layout of a text already held or sent whole (TextRouter.Route,
            // "layout pass"). The setter never gets this far with it; the scene sweep does, reading
            // the component back — and took it for a new line: it replaced the whole the reveal was
            // holding (dropped, never sent), then went to the model with the game's line breaks in
            // it, a key no later write of that line would ever match (2026-09-26).
            if (_layoutResults.ContainsKey(text))
                return text;

            long compId = component != null ? _host.IdOf(component) : -1;

            // Read-back detection: if the game read translated text and appended
            // untranslated content, reconstruct the source-language text.
            if (compId != -1)
            {
                string reconstructed = DetectReadBack(compId, text);
                if (reconstructed != null)
                    text = reconstructed;
            }

            // 🔴 Tell a reveal in flight what this component now shows, BEFORE any lookup can
            // answer and return. Every exit below means "this text is known", and each one that
            // forgot to say so left the reveal holding a fragment it then sent to the model — see
            // NoteTextSeen for what that cost, measured.
            if (compId != -1)
                NoteTextSeen(compId, text);

            // 🔴 A template the game expands in place is never written back, whatever the cache
            // holds. This is what makes the rule deterministic — the withdrawal from the queue is
            // only the best case — and what keeps a file polluted before the rule from breaking the
            // game's own expansion. Nothing is deleted; the line simply stops reaching the screen.
            //
            // ⚠ Before every lookup, since it is a lookup ANSWERING that does the damage. The
            // test inside costs nothing on the games that never do this.
            if (_host.IsExpandedInPlace(text))
                return text;

            // Fast path: check concat assembled cache (runtime only, not JSON)
            // Catches full tooltip texts that were assembled from translated deltas.
            string concatResult = GetConcatCacheResult(text);
            if (concatResult != null)
            {
                _host.CountTranslated();
                return concatResult;
            }
            // Also skip if the text is a known concat translation result
            if (IsConcatTranslatedValue(text))
            {
                return text; // already translated, don't re-process
            }

            // 🔴 Which file this text is looked up in, decided once and used for every lookup
            // below. The mod's own labels and the game's text never see each other's entries —
            // that is the whole separation, and asking the same question three times is how a
            // branch ends up asking a fourth way.
            var store = isOwnUI ? _host.OwnUiStore : _host.GameStore;

            // 🔴 The ladder — exact, normalized, trimmed, pattern — lives in Engine/TextGate.cs,
            // where its order is held by cases. What follows is what a verdict DOES here: the
            // counters, the bounded debug lines, and the tracking a hit needs.
            var look = TextGate.Lookup(text, isOwnUI, store, _host.NormalizeNumbers, _host.Variables, _host.MatchPattern);

            if (look.Outcome == GateOutcome.Hit)
            {
                if (look.Stage != GateStage.Pattern) _host.CountCacheHit();
                _host.CountTranslated();

                if (look.Stage == GateStage.Exact)
                {
                    // ⚠ The reveal was told at the top of this method, for every exit at once — this
                    // used to be said HERE, on the exact-key hit alone, which is the defect.

                    // The canary for the defect NoteTextSeen was written for: a text recognised on
                    // a component whose reveal is still in flight. It is normal — recognition
                    // arrives before the last character, since the numbers are lifted out — and it
                    // is only harmless because the reveal was told at the top of this method.
                    if (_dbgTwCacheHit < 20 && compId != -1 && IsInTypewritingState(compId))
                    {
                        _dbgTwCacheHit++;
                        _host.LogDebug($"[TW-CACHEHIT] comp={compId} text='{Head40(text)}' → known while a reveal is in flight");
                    }
                    if (_host.DebugMode && text.Length > 100)
                        _host.LogDebug($"[CACHE-HIT-LONG] comp={compId}\n  key({text.Length}c)='{text}'\n  val({look.Value.Length}c)='{look.Value}'");
                }
                else if (look.Stage == GateStage.Normalized)
                {
                    // 🔴 **Said a few times, then not again.** This dumps the whole text TWICE —
                    // original and normalised, newlines and markup included — on every cache hit
                    // over a hundred characters. On a game whose long tooltips are on screen
                    // continuously that is 780 dumps in one session: a log nobody can read, in
                    // which a real warning is invisible, written by the thing being diagnosed.
                    //
                    // ⚠ Bounded rather than removed: what it shows — which text produced which
                    // key — is exactly what a normalisation defect looks like, and it is worth
                    // seeing once.
                    if (_host.DebugMode && text.Length > 100 && _dbgCacheHitNormLog < 10)
                    {
                        _dbgCacheHitNormLog++;
                        _host.LogDebug($"[CACHE-HIT-NORM] comp={compId} orig({text.Length}c) norm→key({look.NormalizedText.Length}c)\n  orig='{text}'\n  norm='{look.NormalizedText}'");
                    }
                }

                // Return it synchronously — this prevents the game from reading back translated
                // text and appending to it. Store the original for this component (enables the
                // runtime toggle restoration) and track the pair.
                if (component != null)
                {
                    _host.StoreOriginal(component, text);
                    TrackTranslation(compId, text, look.Value);
                }
                return look.Value;
            }

            if (look.Outcome == GateOutcome.Known)
            {
                // Nothing in it (a capture, or a key nobody filled in), S, or key == value:
                // the source is what to show, and nothing is queued.
                _host.CountCacheHit();
                if (look.Stage == GateStage.Exact && _host.DebugMode && text.Length > 100)
                    _host.LogDebug($"[CACHE-HIT-SAME] comp={compId} known as shown ({text.Length}c)='{text}'");
                return text;
            }

            // 🔴 The miss path — reverse index, own UI, stale snapshot, visibility, reveal, concat,
            // variables, queue — is TextGate.ResolveMiss, where its order is held by cases. What
            // follows is what each verdict DOES here.
            var miss = TextGate.ResolveMiss(text, isOwnUI, look.NormalizedText,
                gateOpen: _host.GateOpen,
                skipTypewriting: skipTypewriting, skipQueueing: skipQueueing,
                readback: _host.Readback, stale: _host.Stale, current: _host.GameStore, normalizeNumbers: _host.NormalizeNumbers,
                host: Gate, component: component);
            switch (miss.Kind)
            {
                case MissKind.AlreadyTarget:
                    _host.CountAlreadyTranslated();
                    // This component displays an ALREADY-translated string (e.g. a title's shadow/
                    // duplicate layer copied from the main layer) and so never had its source stored —
                    // without it, disabling translation can't revert it (issue #21). Back-fill the
                    // original from the reverse cache so restore works. Guard on "no original yet" to
                    // keep the scan over the whole file to once per such component; storing also
                    // no-ops if an original is already tracked.
                    if (component != null && _host.GetOriginal(component) == null)
                    {
                        string src = _host.SourceOf(text, isOwnUI);
                        if (!string.IsNullOrEmpty(src) && src != text)
                            _host.StoreOriginal(component, src);
                    }
                    return text;

                case MissKind.Refreshed:
                    if (component != null)
                    {
                        _host.StoreOriginal(component, miss.OriginalText);
                        TrackTranslation(compId, miss.OriginalText, miss.NewText);
                    }
                    _host.CountTranslated();
                    return miss.NewText;

                case MissKind.Gone:
                    // ⚠ The GAME's index: the stale snapshot is taken from the game's cache before a
                    // reload, and own-UI text returns before ever reaching this point.
                    _host.Readback.MarkTarget(miss.TrimmedNormalized, ownUi: false);
                    return text;

                case MissKind.Retry:
                    NoteReverseMiss(text, miss.TrimmedNormalized);
                    return TranslateSingle(text, component, isOwnUI, skipTypewriting, skipQueueing);

                case MissKind.Queue:
                    NoteReverseMiss(text, miss.TrimmedNormalized);
                    _host.Queue(text, component, isOwnUI);
                    return text;

                default:
                    // Closed, OwnUiUnknown, HeldHidden, HeldRevealing, NotQueued: the text as shown.
                    return text;
            }
        }

        /// <summary>
        /// DEBUG LOG: a text with Latin letters that reached the queue without the reverse index
        /// recognising it — after every skip check, so only texts actually queued are named.
        /// </summary>
        private void NoteReverseMiss(string text, string trimmedNormalized)
        {
            if (_dbgReverseMiss < 20 && text.Length > 5)
            {
                bool hasLatin = false;
                foreach (char c in text)
                {
                    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                    { hasLatin = true; break; }
                }
                if (hasLatin)
                {
                    _dbgReverseMiss++;
                    _host.LogDebug($"[REVERSE-MISS] orig({text.Length}c)='{text}'\n  norm({trimmedNormalized.Length}c)='{trimmedNormalized}'");
                }
            }
        }

        private static string Head40(string text) => text.Length > 40 ? text.Substring(0, 40) : text;

        private GateAnswers _gateHost;

        /// <summary>
        /// The miss path's questions as this router answers them — also for a door with no
        /// component (the localization fallback), where only the variables' refresh can say yes.
        /// </summary>
        public ITextGateHost Gate => _gateHost ?? (_gateHost = new GateAnswers(this));

        /// <summary>
        /// The three questions of the miss path, answered by this router and its host — see
        /// <see cref="ITextGateHost"/>. One instance per router: the component travels as the
        /// opaque object, so a miss allocates nothing here.
        /// </summary>
        private sealed class GateAnswers : ITextGateHost
        {
            private readonly TextRouter _router;
            public GateAnswers(TextRouter router) { _router = router; }

            public bool IsHiddenWhileRevealing(object component)
            {
                long id = component != null ? _router._host.IdOf(component) : -1;
                return id != -1 && _router._host.IsHidden(component) && _router.IsInTypewritingState(id);
            }

            public bool IsRevealInProgress(object component, string text)
            {
                long id = component != null ? _router._host.IdOf(component) : -1;
                return _router.IsTypewritingInProgress(id, text, component);
            }

            public bool RefreshVariables() => _router._host.RefreshVariables();
        }
    }
}
