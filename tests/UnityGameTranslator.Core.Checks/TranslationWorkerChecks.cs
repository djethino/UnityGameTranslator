using System;
using System.Collections.Generic;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// One item through the worker, with a host that records what it was asked and in which order.
    ///
    /// 🔴 **Several defects this project paid for were defects of ORDER**, not of any one decision:
    /// a bare string put back on a rate limit came back without its origin and was filed as a game
    /// line; a text refused for its placeholders was asked again at every launch; an answer to a
    /// translation that had been replaced was written into the file that replaced it. The decisions
    /// live in the socle and in the queue and are checked there; what is checked HERE is when each
    /// one is asked, and what the host is told between them.
    /// </summary>
    internal static class TranslationWorkerChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            FromTheCache(check);
            CaptureOnly(check);
            RefusedEarlier(check);
            Translated(check);
            WithSlots(check);
            RateLimited(check);
            NoAnswer(check);
            Invented(check);
            Stale(check);
            SkippedOrDeclined(check);
            SameAsSource(check);
            Logs(check);
            Refusals(check);
        }

        /// <summary>A backend and a file that only remember what they were told.</summary>
        private sealed class FakeHost : IWorkerHost
        {
            public string Answer;
            public bool RateLimited;
            public bool Unreachable { get; set; }
            public readonly List<string> Calls = new List<string>();
            public readonly List<string> Said = new List<string>();

            public string Translate(string normalized, bool ownUi, out bool rateLimited)
            {
                Calls.Add($"translate:{normalized}{(ownUi ? ":ui" : "")}");
                rateLimited = RateLimited;
                return Answer;
            }
            public void Store(string key, string value, string tag) => Calls.Add($"store:{key}={value}:{tag}");
            public void Notify(string original, string shown, List<object> targets) => Calls.Add($"notify:{original}→{shown}:{targets?.Count ?? 0}");
            public void Refused(string normalized, List<object> targets) => Calls.Add($"refused:{normalized}:{targets?.Count ?? 0}");
            public void Backoff(float seconds) => Calls.Add("backoff:" + seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            public void Debug(string line) => Said.Add("debug:" + line);
            public void Info(string line) => Said.Add("info:" + line);
            public void Warn(string line) => Said.Add("warn:" + line);
        }

        private sealed class Bench
        {
            public readonly TranslationQueue Queue = new TranslationQueue();
            public readonly Dictionary<string, TranslationEntry> Cache = new Dictionary<string, TranslationEntry>();
            public readonly FakeHost Host = new FakeHost();
            public bool CaptureOnly, Debug;
            public bool NormalizeNumbers = true;

            public WorkerContext Context() => new WorkerContext
            {
                CaptureOnly = CaptureOnly, NormalizeNumbers = NormalizeNumbers, Debug = Debug,
                Backend = "llm", RateLimitRetryDelay = 2.5f, Variables = null, Cache = Cache, Queue = Queue,
            };

            public QueuedText Item(string text, bool ownUi = false, object target = null)
            {
                Queue.Submit(text, target ?? "a component", ownUi, out _, out _);
                return Queue.Take();
            }

            public WorkerOutcome Run(QueuedText item) => TranslationWorker.Process(item, Context(), Host);
            public string Trace => string.Join(" | ", Host.Calls);
        }

        private static void FromTheCache(Action<bool, string, string> check)
        {
            var b = new Bench();
            b.Cache["Play"] = new TranslationEntry { Value = "Jouer", Tag = "A" };
            var outcome = b.Run(b.Item("Play"));
            check(outcome == WorkerOutcome.CacheHit && b.Trace == "notify:Play→Jouer:1",
                "a line the cache already holds is handed to its components, and no backend is called",
                "the text was re-queued with components that missed the first apply; asking the model again would be a call for nothing");

            var slots = new Bench();
            slots.Cache["Level [!v*0]"] = new TranslationEntry { Value = "Niveau [!v*0]", Tag = "A" };
            slots.Run(slots.Item("Level 5"));
            check(slots.Trace == "notify:Level 5→Niveau 5:1",
                "with the live numbers put back into what is shown",
                "the cache holds the slotted sentence; the component shows the value");

            foreach (var (entry, why) in new[]
            {
                (new TranslationEntry { Value = "", Tag = "H" }, "a capture waiting for a translation"),
                (new TranslationEntry { Value = "Play", Tag = "S" }, "a skipped line"),
                (new TranslationEntry { Value = "Play", Tag = "A" }, "a line equal to its key"),
            })
            {
                var miss = new Bench { Host = { Answer = "Jouer" } };
                miss.Cache["Play"] = entry;
                miss.Run(miss.Item("Play"));
                check(miss.Host.Calls.Count > 0 && miss.Host.Calls[0].StartsWith("translate:", StringComparison.Ordinal),
                    $"{why} is not a hit: the backend is asked",
                    "an entry with nothing to show is a line still waiting for its answer");
            }
        }

        private static void CaptureOnly(Action<bool, string, string> check)
        {
            var b = new Bench { CaptureOnly = true, Host = { Answer = "Jouer" } };
            var outcome = b.Run(b.Item("Play"));
            check(outcome == WorkerOutcome.Captured && b.Trace == "store:Play=:H",
                "capture-only stores the key empty, as a human placeholder, and calls no backend",
                "collecting the game's lines for somebody to translate later is the whole mode; a model answer here would be a translation nobody asked for");

            var ui = new Bench { CaptureOnly = true, Host = { Answer = "Jouer" } };
            var notCaptured = ui.Run(ui.Item("Apply", ownUi: true));
            check(notCaptured == WorkerOutcome.NotCaptured && ui.Host.Calls.Count == 0,
                "🔴 and the mod's own interface is not captured",
                "this branch ignoring the origin is what filed our menu labels in the GAME's file as empty human captures");

            var hit = new Bench { CaptureOnly = true };
            hit.Cache["Play"] = new TranslationEntry { Value = "Jouer", Tag = "A" };
            var both = hit.Run(hit.Item("Play"));
            check(both == WorkerOutcome.Captured && hit.Trace == "notify:Play→Jouer:1 | store:Play=:H",
                "a cache hit in capture mode still tells the components first, then captures",
                "the order the loop always had: what is known is shown, and the mode does what the mode does");
        }

        private static void RefusedEarlier(Action<bool, string, string> check)
        {
            var b = new Bench { Host = { Answer = "Jouer" } };
            b.Queue.NoteRefused("Play");
            var outcome = b.Run(b.Item("Play"));
            check(outcome == WorkerOutcome.RefusedEarlier && b.Host.Calls.Count == 0,
                "a text refused for its placeholders this session is not asked again",
                "the backend was hammered once per launch with a line it kept getting wrong; it is retried next launch, when the model may have changed");
        }

        private static void Translated(Action<bool, string, string> check)
        {
            var b = new Bench { Host = { Answer = "Jouer" } };
            var item = b.Item("Play");
            var outcome = b.Run(item);
            check(outcome == WorkerOutcome.Translated && b.Trace == "translate:Play | store:Play=Jouer:A | notify:Play→Jouer:1",
                "a translation is asked for, stored under the key shape as A, then shown — in that order",
                "stored before it is shown: a component told first and a crash in between would show a line the file never gets");

            var ui = new Bench { Host = { Answer = "Appliquer" } };
            ui.Run(ui.Item("Apply", ownUi: true));
            check(ui.Trace == "translate:Apply:ui | store:Apply=Appliquer:M | notify:Apply→Appliquer:1",
                "an interface label is asked with the interface prompt and filed as M",
                "whose text this is was settled when it was queued; the item carries its origin and nothing re-decides it here");

            var noTarget = new Bench { Host = { Answer = "Jouer" } };
            noTarget.Queue.Submit("Play", null, false, out _, out _);
            noTarget.Run(noTarget.Queue.Take());
            check(noTarget.Trace.EndsWith("notify:Play→Jouer:0", StringComparison.Ordinal),
                "a text with no component is still stored and still announced",
                "a help zone or a code-written label has no target; the file and the screen refresh are what it needs");
        }

        private static void WithSlots(Action<bool, string, string> check)
        {
            var b = new Bench { Host = { Answer = "Niveau [!v*0]" } };
            b.Run(b.Item("Level 5"));
            check(b.Trace == "translate:Level [!v*0] | store:Level [!v*0]=Niveau [!v*0]:A | notify:Level 5→Niveau 5:1",
                "the backend sees the slots, the file keeps the slots, the component gets the value",
                "one entry serves every value the game will put there; what is shown is the sentence with this one");

            var literal = new Bench { NormalizeNumbers = false, Host = { Answer = "Niveau 5" } };
            literal.Run(literal.Item("Level 5"));
            check(literal.Trace == "translate:Level 5 | store:Level 5=Niveau 5:A | notify:Level 5→Niveau 5:1",
                "with numbers left alone, the key is the text",
                "normalize_numbers is a setting; off, the file is written literally");
        }

        private static void RateLimited(Action<bool, string, string> check)
        {
            var b = new Bench { Host = { Answer = null, RateLimited = true } };
            var item = b.Item("Apply", ownUi: true, target: "the button");
            var outcome = b.Run(item);
            check(outcome == WorkerOutcome.RateLimited && b.Trace == "translate:Apply:ui | backoff:2.5",
                "a rate limit backs off for the configured delay, and stores nothing",
                "the backend said 'not now'; an empty answer stored would be a line lost");

            var back = b.Queue.Take();
            check(ReferenceEquals(back, item) && back.FromOwnUI && back.Targets.Count == 1 && back.Targets[0] == (object)"the button",
                "🔴 and the SAME item is put back — origin and components with it",
                "re-queuing a bare string left the retry with neither, so an interface label came back as a GAME line written under a game tag");

            var floor = new Bench { Host = { Answer = null, RateLimited = true } };
            var ctx = floor.Context(); ctx.RateLimitRetryDelay = 0f;
            TranslationWorker.Process(floor.Item("Play"), ctx, floor.Host);
            check(floor.Trace.EndsWith("backoff:0.1", StringComparison.Ordinal),
                "the back-off never drops below a tenth of a second",
                "a zero delay is a hot loop against a server that just said no");

            // 🔴 A server that cannot be reached: the line goes back whole and nothing waits here —
            // the host holds the queue until an event (2026-09-23). Dropped, it was lost for the
            // scene, and a blocked server emptied two thousand lines in seconds.
            var cut = new Bench { Host = { Answer = null, Unreachable = true } };
            var line = cut.Item("Apply", ownUi: true, target: "the button");
            var unreachable = cut.Run(line);
            var kept = cut.Queue.Take();
            check(unreachable == WorkerOutcome.Unreachable && cut.Trace == "translate:Apply:ui"
                  && ReferenceEquals(kept, line) && kept.FromOwnUI && kept.Targets.Count == 1,
                "an unreachable server puts the SAME item back, stores nothing and does not back off",
                "the queue is held by the host until a scene loads or settings are saved; a timer here would be a wait the project forbids");
        }

        private static void NoAnswer(Action<bool, string, string> check)
        {
            var b = new Bench { Host = { Answer = null } };
            var outcome = b.Run(b.Item("Play"));
            check(outcome == WorkerOutcome.NoAnswer && b.Trace == "translate:Play" && b.Queue.Count == 0,
                "no answer and no rate limit: nothing stored, nothing put back, no back-off",
                "the backend gave up on this line; it will be asked again when it comes by, not spun on");

            var empty = new Bench { Host = { Answer = "" } };
            check(empty.Run(empty.Item("Play")) == WorkerOutcome.NoAnswer,
                "an empty answer is no answer",
                "an empty string stored would show an empty component");
        }

        private static void Invented(Action<bool, string, string> check)
        {
            var b = new Bench { Host = { Answer = "Jouer [!v*0]" } };
            var outcome = b.Run(b.Item("Play"));
            check(outcome == WorkerOutcome.Invented && b.Trace == "translate:Play" && b.Host.Said.Exists(s => s.StartsWith("warn:", StringComparison.Ordinal)),
                "an answer that invented a placeholder is discarded, and said so",
                "stored, the game would show a slot nothing ever fills; nothing is cached and nothing reaches the screen");
        }

        private static void Stale(Action<bool, string, string> check)
        {
            var b = new Bench { Host = { Answer = "Jouer" } };
            var item = b.Item("Play");
            b.Queue.Clear();   // a reload settled everything still waiting — but this item had already left
            var outcome = b.Run(item);
            check(outcome == WorkerOutcome.Stale && b.Trace == "translate:Play",
                "🔴 an answer to a translation replaced while it was in flight is dropped",
                "written, it adds to the restored file a line it never had, in the language of the one before it, and paints it onto components whose text was just put back");
        }

        private static void SkippedOrDeclined(Action<bool, string, string> check)
        {
            var b = new Bench { Host = { Answer = Answers.SkipMarker } };
            var outcome = b.Run(b.Item("Play"));
            check(outcome == WorkerOutcome.Skipped && b.Trace == "translate:Play | store:Play=Play:S",
                "a game line the model says is not in the source language is stored as S with its source, and not shown",
                "S is never asked again and never translated; the component keeps what it had");

            var ui = new Bench { Host = { Answer = Answers.SkipMarker } };
            var declined = ui.Run(ui.Item("Apply", ownUi: true));
            check(declined == WorkerOutcome.Declined && ui.Trace == "translate:Apply:ui",
                "🔴 an interface label the model declined is stored NOWHERE",
                "classified S it landed in the GAME's file, counted and merged as a game line — one of the two defects Answers.Store exists for");
        }

        private static void SameAsSource(Action<bool, string, string> check)
        {
            var b = new Bench { Host = { Answer = "Play" } };
            var outcome = b.Run(b.Item("Play"));
            check(outcome == WorkerOutcome.SameAsSource && b.Trace == "translate:Play | store:Play=Play:A",
                "an answer equal to the source is stored as such, and nothing is shown",
                "'no translation needed' is remembered so the line is not asked again; there is nothing to paint");
        }

        private static void Logs(Action<bool, string, string> check)
        {
            var quiet = new Bench { Host = { Answer = "Jouer" } };
            quiet.Run(quiet.Item("Play"));
            check(!quiet.Host.Said.Exists(s => s.StartsWith("debug:", StringComparison.Ordinal)),
                "with debug off, no verbose line is even composed",
                "the worker runs on every text; a log line built and dropped is work on every text");

            var loud = new Bench { Debug = true, Host = { Answer = "Jouer" } };
            loud.Run(loud.Item("Play"));
            check(loud.Host.Said.Exists(s => s.Contains("returned: Jouer")),
                "with debug on, the backend's answer is named",
                "this is the line somebody reads when a translation is wrong");
        }

        private static void Refusals(Action<bool, string, string> check)
        {
            var b = new Bench();
            bool threw = false;
            try { TranslationWorker.Process(null, b.Context(), b.Host); } catch (ArgumentNullException) { threw = true; }
            check(threw, "no item is refused", "a null item would be a null text stored under a null key");

            threw = false;
            try { TranslationWorker.Process(b.Item("Play"), new WorkerContext(), b.Host); } catch (ArgumentException) { threw = true; }
            check(threw, "a context with no cache or no queue is refused", "without them every line is a miss and no item is ever current");
        }
    }
}
