using System;
using System.Collections.Generic;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// What the worker asks of its host for one item, in the order it asks. Everything that
    /// touches a backend, the file, a component or the clock goes through here; the decisions
    /// between the calls are the worker's own.
    /// </summary>
    public interface IWorkerHost
    {
        /// <summary>
        /// Put the text in front of the translation backend the host has chosen. Returns the raw
        /// answer, or null when there is none; <paramref name="rateLimited"/> is true when the
        /// backend refused for now (a 429), in which case the answer is null too.
        /// </summary>
        string Translate(string normalized, bool ownUi, out bool rateLimited);

        /// <summary>
        /// Whether the backend cannot be reached at all — the request never left the machine.
        /// Asked after a null answer: the line is then put back, since nothing was said about it.
        /// </summary>
        bool Unreachable { get; }

        /// <summary>Write one line into the cache of the side it belongs to (AddToCache).</summary>
        void Store(string key, string value, string tag);

        /// <summary>Tell whoever is waiting — the components, the screen — what this text now reads.</summary>
        void Notify(string original, string shown, List<object> targets);

        /// <summary>
        /// The backend gave this text up for the session (it is now on the queue's give-up list):
        /// the host may record which elements showed it, so the line can be excluded by hand.
        /// </summary>
        void Refused(string normalized, List<object> targets);

        /// <summary>Wait before asking again, in a way that can still be interrupted by a shutdown.</summary>
        void Backoff(float seconds);

        /// <summary>The verbose lines (debug_ai). Only called when <see cref="WorkerContext.Debug"/> is on.</summary>
        void Debug(string line);

        /// <summary>A line worth reading without debug on.</summary>
        void Info(string line);

        /// <summary>Something went wrong and was handled.</summary>
        void Warn(string line);
    }

    /// <summary>What the worker needs to know about the moment — settings and state, read once per item.</summary>
    public sealed class WorkerContext
    {
        /// <summary>Capture-only mode: collect the game's lines, call no backend.</summary>
        public bool CaptureOnly;
        /// <summary>Whether numbers are lifted into slots (the <c>normalize_numbers</c> setting).</summary>
        public bool NormalizeNumbers;
        /// <summary>Whether the verbose lines are wanted (debug_ai).</summary>
        public bool Debug;
        /// <summary>The backend's name, for the log only: the host chooses which one answers.</summary>
        public string Backend;
        /// <summary>How long to back off after a rate limit (the <c>rate_limit_retry_delay</c> setting).</summary>
        public float RateLimitRetryDelay;
        /// <summary>The game's variables; may be null.</summary>
        public IVariableSubstitution Variables;
        /// <summary>The GAME's cache, where a hit spares a call. ⚠ The game's even for an interface item: the worker looks up, it does not file — see <see cref="TranslationWorker.Process"/>.</summary>
        public IDictionary<string, TranslationEntry> Cache;
        /// <summary>The queue the item came from: asked whether the text was refused earlier and whether the item is still current; told to take one back on a rate limit.</summary>
        public TranslationQueue Queue;
    }

    /// <summary>What became of one item.</summary>
    public enum WorkerOutcome
    {
        /// <summary>The cache already had a usable translation: the waiting components were told, no backend was called.</summary>
        CacheHit,
        /// <summary>Capture-only, and the item is the mod's own interface: filed nowhere.</summary>
        NotCaptured,
        /// <summary>Capture-only: the key stored empty, for somebody to translate later.</summary>
        Captured,
        /// <summary>The text was refused earlier this session (invalid placeholders): not asked again.</summary>
        RefusedEarlier,
        /// <summary>The backend refused for now: the SAME item was put back and the worker backed off.</summary>
        RateLimited,
        /// <summary>The backend could not be reached: the SAME item was put back, the queue is held.</summary>
        Unreachable,
        /// <summary>The backend answered nothing, and it was not a rate limit.</summary>
        NoAnswer,
        /// <summary>The answer invented a placeholder the source does not have: discarded, nothing stored.</summary>
        Invented,
        /// <summary>The answer arrived after the translation it was asked for was replaced: dropped.</summary>
        Stale,
        /// <summary>The model declined an interface label: left as it is, nothing stored.</summary>
        Declined,
        /// <summary>The model said the text is not in the source language: stored as skipped, nothing shown.</summary>
        Skipped,
        /// <summary>The answer equals the source: stored as such, nothing to show.</summary>
        SameAsSource,
        /// <summary>A translation: stored, and the waiting components told.</summary>
        Translated,
    }

    /// <summary>
    /// What the worker does with one item taken from the queue, between the dequeue and the
    /// verdict — the sequence a second Core has to reproduce.
    ///
    /// 🔴 **The order is the semantic**, and several defects this project paid for were defects of
    /// order rather than of any single decision: the SAME item put back on a rate limit (a bare
    /// string lost its origin and came back as a game line); a text refused for its placeholders
    /// asked again every launch; an answer to a replaced translation written into the file that
    /// replaced it. The decisions themselves live in the socle (<see cref="Answers"/>,
    /// <see cref="Placeholders"/>) and in <see cref="TranslationQueue"/>; what is held here is
    /// when each one is asked.
    ///
    /// | rung | question | outcome |
    /// |---|---|---|
    /// | cache | is a usable translation already there? | the components are told now; then on to capture, or <see cref="WorkerOutcome.CacheHit"/> |
    /// | capture-only | are we collecting rather than translating? | <see cref="WorkerOutcome.Captured"/> / <see cref="WorkerOutcome.NotCaptured"/> — no backend, ever |
    /// | refused earlier | did this text fail its placeholders this session? | <see cref="WorkerOutcome.RefusedEarlier"/> |
    /// | backend | the one call | <see cref="WorkerOutcome.RateLimited"/> (same item back, back off) / <see cref="WorkerOutcome.Unreachable"/> (same item back, queue held) / <see cref="WorkerOutcome.NoAnswer"/> |
    /// | invented | did the answer make up a placeholder? | <see cref="WorkerOutcome.Invented"/> — nothing stored, nothing shown |
    /// | current | was the translation replaced while we waited? | <see cref="WorkerOutcome.Stale"/> — dropped |
    /// | filing | what becomes of the answer, from its origin (<see cref="Answers.Store"/>)? | stored under the key shape, or nowhere |
    /// | notify | is there something new to show? | <see cref="WorkerOutcome.Translated"/>, numbers and variables put back |
    ///
    /// ⚠ **Whose text this is was settled when it was queued, and nothing re-decides it here.**
    /// The item's identity IS (text, origin) — the game's "Options" and ours are two entries,
    /// asked with two prompts and filed in two files — so there is nothing left to infer, and
    /// inferring anyway is how a shared string used to end up in whichever file asked last. The
    /// components are deliberately not consulted: a text queued by our interface with no
    /// component at all is just as much ours as one that came with fifty.
    ///
    /// ⚠ **The cache rung looks in the GAME's cache even for an interface item** — as the loop
    /// always did. It is a lookup to spare a call, never a filing: what is stored goes through
    /// <see cref="IWorkerHost.Store"/>, which files by origin.
    ///
    /// ⚠ What is NOT here: a human asking for a line again (the retranslation has its own loop,
    /// with a previous value to put back), the thread, and the two counters the caller keeps.
    ///
    /// 🔴 **Pure by contract**, like its neighbours in Engine/: the item, the moment and a host
    /// in; a verdict out, and a recorded sequence of host calls that a check can replay with a
    /// backend of its own. Linked by tests/UnityGameTranslator.Core.Checks.
    ///
    /// Moved out of TranslatorCore.TranslationWorkerLoop on 2026-09-11 (step 6t of
    /// analyse/plan-prealables-couches.md), verbatim: same branches, same order, same log lines.
    /// </summary>
    public static class TranslationWorker
    {
        public static WorkerOutcome Process(QueuedText item, WorkerContext ctx, IWorkerHost host)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (ctx.Cache == null || ctx.Queue == null) throw new ArgumentException("a cache and a queue are needed", nameof(ctx));

            string originalText = item.Text;
            bool isOwnUI = item.FromOwnUI;
            List<object> componentsToUpdate = item.Targets.Count > 0 ? item.Targets : null;

            // Extract variables then numbers BEFORE sending to AI.
            // Never on our own GUI: variables hold GAME state (player name, seed…) and
            // substitution is a plain Replace of their current value. A value that happens
            // to equal one of our labels would swallow it whole ("Current Translation" →
            // "[!STR*0]") and poison that cache entry for good.
            string normalizedOriginal = TextGate.KeyShape(originalText, isOwnUI, ctx.Variables, ctx.NormalizeNumbers,
                out List<KeyValuePair<int, string>> workerExtractedVars, out List<string> extractedNumbers);

            // Check cache first (another request might have already translated this)
            string translation = null;
            if (ctx.Cache.TryGetValue(normalizedOriginal, out var cachedEntry))
            {
                if (cachedEntry.Value != normalizedOriginal && !cachedEntry.IsHumanEmpty && cachedEntry.Tag != "S")
                {
                    translation = cachedEntry.Value;
                    if (ctx.Debug)
                        host.Debug("[Worker] Cache hit for normalized text, skipping AI");

                    // Notify components even for cache hits — the text was re-queued
                    // with different components that didn't get the first Apply.
                    host.Notify(originalText, TextGate.RestoreSlots(translation, extractedNumbers, workerExtractedVars, ctx.Variables), componentsToUpdate);
                }
            }

            // Capture keys only mode: store H+empty without calling AI.
            //
            // 🔴 **The mod's own interface is not captured**, and this branch ignoring
            // that is what filed our menu labels in the GAME's file as empty human
            // captures. Capturing collects the game's strings for somebody to
            // translate later — on the site, or in the browser editor. Our interface
            // goes to neither, no backend is called in this mode, so an entry for it
            // would have no editor, no destination and nothing to become.
            if (ctx.CaptureOnly)
            {
                // The socle decides, here as below: Answers.Capture is the same rule a
                // Core in another language has to reach, and it is checked there.
                var captured = Answers.Capture(isOwnUI);
                if (captured == Filing.Nothing)
                {
                    if (ctx.Debug)
                        host.Debug("[Worker] Interface label not captured: capture mode collects the game's text.");
                    return WorkerOutcome.NotCaptured;
                }

                host.Store(normalizedOriginal, "", Answers.TagOf(captured));
                if (ctx.Debug)
                    host.Debug($"[Worker] Captured key (no translation): {normalizedOriginal.Substring(0, Math.Min(30, normalizedOriginal.Length))}...");
                return WorkerOutcome.Captured;
            }

            // Only call translation backend if not in cache
            if (translation != null)
                return WorkerOutcome.CacheHit;

            // Text already failed placeholder validation this session:
            // don't hammer the backend, it will be retried next launch
            if (ctx.Queue.WasRefused(normalizedOriginal))
            {
                if (ctx.Debug)
                    host.Debug($"[Worker] Skipping (failed placeholder validation earlier): {normalizedOriginal.Substring(0, Math.Min(40, normalizedOriginal.Length))}...");
                return WorkerOutcome.RefusedEarlier;
            }

            // Dispatch to the appropriate backend — the host's choice.
            translation = host.Translate(normalizedOriginal, isOwnUI, out bool rateLimited);

            if (ctx.Debug)
                host.Debug($"[Worker] {ctx.Backend} returned: {(translation == null ? "(null)" : translation.Substring(0, Math.Min(40, translation.Length)))}");

            // Handle rate limit: re-queue the text and backoff.
            //
            // ⚠ The SAME item goes back, not its text. Re-queuing a bare string
            // left the second attempt with neither the components to update nor
            // the origin — so a mod-interface label came back from the retry as a
            // GAME line, written into the game's file under a game tag.
            if (translation == null && rateLimited)
            {
                ctx.Queue.PutBack(item);
                float delaySec = Math.Max(0.1f, ctx.RateLimitRetryDelay);
                host.Warn($"[Worker] Rate limited — re-queued, backing off {delaySec:F1}s ({ctx.Queue.Count} pending)");
                host.Backoff(delaySec);
                return WorkerOutcome.RateLimited;
            }

            // Never reached: the line goes back whole, like a rate limit, and the host holds the
            // queue. Dropped here, it was lost for the scene — two thousand of them in seconds.
            if (translation == null && host.Unreachable)
            {
                ctx.Queue.PutBack(item);
                return WorkerOutcome.Unreachable;
            }

            // Given up this session, just now: the elements it came from are the one thing the
            // failure record cannot get from the backend.
            if (translation == null && ctx.Queue.WasRefused(normalizedOriginal))
                host.Refused(normalizedOriginal, item.Targets);

            if (string.IsNullOrEmpty(translation))
                return WorkerOutcome.NoAnswer;

            // Discard an answer that invented placeholders: treated as no answer at
            // all, so nothing is cached and nothing reaches the screen.
            var inventedTokens = Placeholders.Invented(normalizedOriginal, translation);
            if (inventedTokens.Count > 0)
            {
                string badPreview = normalizedOriginal.Length > 40
                    ? normalizedOriginal.Substring(0, 40) + "..."
                    : normalizedOriginal;
                host.Warn($"[Worker] Discarded answer inventing {string.Join(", ", inventedTokens)} (absent from source): '{badPreview}'");
                return WorkerOutcome.Invented;
            }

            // 🔴 **An answer asked for a translation that has since been replaced
            // is not an answer.** ReloadCache empties the queue, which settles
            // everything still waiting — but this item left before that and comes
            // back seconds later. Written, it adds to the restored file a line it
            // never had, in the language of the one before it, marks the file
            // changed, and paints it onto components whose own text was just put
            // back. Dropped the same way as an answer inventing a token above.
            if (!ctx.Queue.IsCurrent(item))
            {
                host.Info("[Worker] Dropped an answer asked before the translation was replaced");
                return WorkerOutcome.Stale;
            }

            // Check if AI returned the skip marker (text not in expected source language)
            // Note: Google/DeepL don't return skip markers, so this only applies to LLM
            // ⚠ Read ONCE: the kind decides what is filed just below, and the
            // same answer read twice is one call away from being read two ways.
            AnswerKind answerKind = Answers.Read(translation);
            bool isSkipped = answerKind == AnswerKind.Skip;

            // 🔴 **Where a line comes from outranks what happened to it, and the
            // socle is what says so.** The rule lived here as three conditions
            // and four spelled-out letters; it is Answers.Store now, where a
            // Core in another language reads the same one and where every case
            // — including the two this cost — can be replayed.
            var filed = Answers.Store(isOwnUI, answerKind);
            if (filed == Filing.Nothing)
            {
                if (ctx.Debug)
                    host.Debug("[Worker] The model declined an interface label; left in English, nothing stored.");
            }
            else
            {
                host.Store(normalizedOriginal,
                    Answers.StoresTheSource(filed) ? normalizedOriginal : translation,
                    Answers.TagOf(filed));
            }

            if (!isSkipped && translation != normalizedOriginal)
            {
                // For updating components, restore actual numbers then variables
                host.Notify(originalText, TextGate.RestoreSlots(translation, extractedNumbers, workerExtractedVars, ctx.Variables), componentsToUpdate);
                return WorkerOutcome.Translated;
            }

            if (isSkipped)
                return filed == Filing.Nothing ? WorkerOutcome.Declined : WorkerOutcome.Skipped;

            return WorkerOutcome.SameAsSource;
        }
    }
}
