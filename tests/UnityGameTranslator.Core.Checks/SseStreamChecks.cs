using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Reading one server-sent event stream — the grammar, and the loop that pulls the lines.
    ///
    /// 🔴 **These cases exist because of a defect that cost months and nothing could see.** Signing
    /// a game in never completed: the website said the game was linked, the game sat on "Waiting
    /// for authorization" for ever. A tick that won the race against a pending read abandoned it
    /// and started another, which .NET refuses — so the connection died, every reconnection met the
    /// same wall, and the reason was logged as debug and excused in a comment as normal.
    ///
    /// 🔴 **It was a MOMENT, not an answer**, and that is why it was invisible: every line the
    /// parser handled was handled correctly. What was wrong was when a read was started. So the
    /// reader here is not a network stream but one that answers slowly on purpose — and refuses a
    /// second overlapping read exactly as .NET does, so the defect reproduces rather than being
    /// described.
    /// </summary>
    internal static class SseStreamChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            TheGrammar(check);
            OneReadAtATime(check);
            WhenItStops(check);
        }

        /// <summary>
        /// A stream whose lines take a while to arrive — and which behaves like .NET when a second
        /// read is started over a pending one.
        ///
        /// ⚠ The refusal is the point. Without it a broken loop merely reads the same line twice
        /// and nothing looks wrong; with it, the check fails the way the field failed.
        /// </summary>
        private sealed class SlowReader : TextReader
        {
            private readonly Queue<string> _lines;
            private readonly int _delayMs;
            private int _inFlight;

            public SlowReader(int delayMs, params string[] lines)
            {
                _lines = new Queue<string>(lines);
                _delayMs = delayMs;
            }

            /// <summary>True once a second read was started over a pending one.</summary>
            public bool Overlapped { get; private set; }

            public override async Task<string> ReadLineAsync()
            {
                if (Interlocked.Increment(ref _inFlight) > 1)
                {
                    Overlapped = true;
                    Interlocked.Decrement(ref _inFlight);
                    throw new InvalidOperationException(
                        "The stream is currently in use by a previous operation on the stream.");
                }

                try
                {
                    await Task.Delay(_delayMs);
                    return _lines.Count > 0 ? _lines.Dequeue() : null;
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }

        private static List<SseEvent> ReadAll(string text, out SseStopReason stopped)
        {
            var events = new List<SseEvent>();
            var stream = new SseStream(new StringReader(text)) { OnEvent = events.Add };
            stopped = stream.Run(CancellationToken.None).GetAwaiter().GetResult();
            return events;
        }

        private static void TheGrammar(Action<bool, string, string> check)
        {
            var events = ReadAll("event: authorized\ndata: {\"token\":\"abc\"}\n\n", out _);
            check(events.Count == 1 && events[0].EventType == "authorized"
                  && events[0].Data == "{\"token\":\"abc\"}",
                "a named event with its data arrives whole",
                "this exact shape is what completes signing a game in; nothing else does");

            check(ReadAll("data: hello\n\n", out _)[0].EventType == "message",
                "an event with no name is a message",
                "the spec's default, and a relay is free to send bare data");

            check(ReadAll("data: one\ndata: two\n\n", out _)[0].Data == "one\ntwo",
                "several data lines are one payload, newline-joined",
                "a JSON body longer than a line arrives split, and rejoining it wrongly is a payload nobody can parse");

            // ⚠ Exactly ONE leading space is removed, per the spec. Removing more would corrupt a
            // payload that is meant to be indented; removing none would break every ordinary relay.
            check(ReadAll("data:  two spaces\n\n", out _)[0].Data == " two spaces",
                "one space after the colon is the separator, and only one",
                "the second space belongs to the payload — eating it silently changes what was sent");

            check(ReadAll("data:tight\n\n", out _)[0].Data == "tight",
                "and no space is fine too",
                "a relay that writes no space is still a conforming relay");

            check(ReadAll(":heartbeat\n:another\ndata: real\n\n", out _).Count == 1,
                "a comment line is not an event",
                "it is how a live stream proves it is alive; dispatching it would wake everything up for nothing");

            check(ReadAll("data: a\n\n\n\ndata: b\n\n", out _).Count == 2,
                "a blank line with nothing pending dispatches nothing",
                "otherwise a keep-alive newline produces an empty event on every beat");

            var withId = new SseStream(new StringReader("id: 42\ndata: x\n\n"));
            withId.Run(CancellationToken.None).GetAwaiter().GetResult();
            check(withId.LastEventId == "42",
                "the last id is kept for the reconnection",
                "it is what tells the server where to resume, and losing it replays or skips events");

            // ⚠ Per spec: an id containing NUL is ignored — it would go into a header.
            //
            // ⚠ Written as an escape rather than as a raw byte. It WAS a raw byte, which
            // compiles and passes and is invisible in every editor and every diff — so the day
            // a tool strips it, the case goes on passing while asking a different question under
            // the same name. The guard in the check is the other half: a case that can quietly
            // stop testing what it says is worse than no case at all.
            const string nul = "\u0000";
            string withNul = "id: 4" + nul + "2\ndata: x\n\n";
            var nulId = new SseStream(new StringReader(withNul));
            nulId.Run(CancellationToken.None).GetAwaiter().GetResult();
            check(withNul.Contains(nul) && nulId.LastEventId == null,
                "but an id carrying a NUL is refused",
                "it travels back as a Last-Event-ID header, and a NUL in a header is not something to pass on");

            var retry = new SseStream(new StringReader("retry: 15000\ndata: x\n\n"));
            retry.Run(CancellationToken.None).GetAwaiter().GetResult();
            check(retry.RetryDelayMs == 15000,
                "a server may say how long to wait before reconnecting",
                "it knows what it is doing to itself; ignoring it is how a client becomes a flood");

            var badRetry = new SseStream(new StringReader("retry: soon\nretry: -5\ndata: x\n\n"));
            badRetry.Run(CancellationToken.None).GetAwaiter().GetResult();
            check(badRetry.RetryDelayMs == null,
                "and one that says something else is not obeyed",
                "a negative or unreadable delay would make the client reconnect with no pause at all");

            check(ReadAll("unknown: value\ndata: x\n\n", out _).Count == 1,
                "a field nobody knows is skipped, not fatal",
                "the stream must survive a server that grows a field before this client learns it");
        }

        private static void OneReadAtATime(Action<bool, string, string> check)
        {
            // 🔴 The defect itself, replayed. The tick fires well before the line arrives, several
            // times over. A loop that abandoned its pending read would start a second one here and
            // the reader would refuse, exactly as .NET does.
            var reader = new SlowReader(delayMs: 300, "event: authorized", "data: ok", "");
            var events = new List<SseEvent>();
            var stream = new SseStream(reader)
            {
                TickMs = 20,
                OnEvent = events.Add,
                StopAfterFirstEvent = true,
            };

            // ⚠ Caught rather than allowed to escape: with the defect back in place this throws
            // the way it threw in the field, and an escaping exception ENDS the section — every
            // case after it goes unrun while the output looks merely shorter. A red must be a red.
            SseStopReason stopped = SseStopReason.Cancelled;
            Exception threw = null;
            try
            {
                stopped = stream.Run(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                threw = ex;
            }

            check(threw == null && !reader.Overlapped,
                "a tick that fires while a read is pending does NOT start another",
                "it did, and .NET refused it: the connection died, and signing a game in never completed");

            check(threw == null && stopped == SseStopReason.Delivered && events.Count == 1
                  && events[0].EventType == "authorized",
                "and the event still arrives across every tick that fired",
                "the tick exists so a silent stream can time out — it must cost nothing on a slow one");
        }

        private static void WhenItStops(Action<bool, string, string> check)
        {
            check(ReadAll("data: x\n\n", out SseStopReason ended) != null
                  && ended == SseStopReason.EndOfStream,
                "a stream that runs out has ended, and the client reconnects",
                "the ordinary case: a relay recycles its connections and the client is expected to come back");

            // 🔴 A deliberate single-shot close is NOT a loss. It was read as one and announced as
            // one, over the top of the success message the player had just been shown.
            var once = new SseStream(new StringReader("data: a\n\ndata: b\n\n"))
            {
                StopAfterFirstEvent = true,
            };
            var delivered = new List<SseEvent>();
            once.OnEvent = delivered.Add;
            var reason = once.Run(CancellationToken.None).GetAwaiter().GetResult();

            check(reason == SseStopReason.Delivered && delivered.Count == 1,
                "a stream opened for one event stops on it",
                "the device flow's stream carries exactly one 'authorized' and the server then closes it on purpose");

            // The clock is handed in, so a minute of silence costs nothing to check.
            var clock = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
            var silent = new SseStream(new SlowReader(delayMs: 100_000))
            {
                TickMs = 5,
                HeartbeatTimeoutMs = 60_000,
                Now = () => { clock = clock.AddSeconds(30); return clock; },
            };
            var said = new List<string>();
            silent.Warning = said.Add;
            var quiet = silent.Run(CancellationToken.None).GetAwaiter().GetResult();

            check(quiet == SseStopReason.HeartbeatTimeout && said.Count == 1,
                "a stream that goes quiet for too long is given up on, and says so",
                "a dead connection that nobody notices is a game waiting for an answer that will never come");

            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var stopped = new SseStream(new StringReader("data: x\n\n"))
                .Run(cancelled.Token).GetAwaiter().GetResult();
            check(stopped == SseStopReason.Cancelled,
                "and one told to stop stops without reconnecting",
                "shutting the game down or signing out must not look like a connection that failed");

            // A handler that throws is a bad handler, not a bad stream.
            var survived = new List<SseEvent>();
            var noisy = new SseStream(new StringReader("data: a\n\ndata: b\n\n"));
            bool first = true;
            noisy.OnEvent = evt =>
            {
                if (first) { first = false; throw new InvalidOperationException("boom"); }
                survived.Add(evt);
            };
            var complaints = new List<string>();
            noisy.Warning = complaints.Add;
            noisy.Run(CancellationToken.None).GetAwaiter().GetResult();

            check(survived.Count == 1 && complaints.Count == 1,
                "a handler that throws is said out loud and the stream reads on",
                "one bad event handler must not take down a connection the rest of the mod is waiting on");
        }
    }
}
