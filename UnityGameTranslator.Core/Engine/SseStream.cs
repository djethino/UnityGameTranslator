using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// One parsed event.
    ///
    /// ⚠ It lives beside the reader that produces it rather than beside the client that connects:
    /// the client can be checked by nobody (it holds an HttpClient and the mod's log), and a shape
    /// its reader hands out has to be nameable wherever that reader is.
    /// </summary>
    public class SseEvent
    {
        public string Id { get; set; }
        public string EventType { get; set; }
        public string Data { get; set; }
    }

    /// <summary>Why a stream stopped being read.</summary>
    public enum SseStopReason
    {
        /// <summary>Somebody asked it to stop.</summary>
        Cancelled,

        /// <summary>The other end closed it. The client reconnects.</summary>
        EndOfStream,

        /// <summary>The connection dropped under us. The client reconnects.</summary>
        Closed,

        /// <summary>Nothing arrived for longer than a live stream ever goes quiet.</summary>
        HeartbeatTimeout,

        /// <summary>It carried the one event it was opened for. NOT a loss — see the note below.</summary>
        Delivered,
    }

    /// <summary>
    /// Reading one server-sent event stream: the line grammar, and the loop that pulls the lines.
    ///
    /// 🔴 **This exists because of a defect that cost months and could not be checked anywhere.**
    /// Signing a game in never completed: the website said the game was linked, the game sat on
    /// "Waiting for authorization" for ever. The cause was in the read loop — a tick that won the
    /// race against a pending read ABANDONED it and started another, which .NET refuses ("the
    /// stream is currently in use by a previous operation"), killing the connection. And the tick
    /// was fifteen seconds while the relay's heartbeat is also fifteen: two numbers chosen
    /// independently, landing on the same one, so the race was a dead heat and the loser was always
    /// the connection.
    ///
    /// 🔴 **A moment, not an answer** — so only a replayed sequence can see it. Which is why the
    /// reader is a <see cref="TextReader"/> rather than a StreamReader, the clock is handed in, and
    /// the log is a sink: a check can hand this a reader whose lines arrive slowly, and one that
    /// refuses a second overlapping read exactly as .NET does.
    ///
    /// ⚠ **The tick is comfort, not the fix.** It is now well under the relay's heartbeat, so
    /// waking up finds data waiting rather than racing it. The fix is that a tick which wins keeps
    /// the pending read instead of replacing it.
    ///
    /// ⚠ **Pure by contract**: no Unity, no HttpClient, no wall clock of its own. What it does not
    /// do is connect or reconnect — that stays with <see cref="SseClient"/>, which owns the socket.
    ///
    /// Grammar per https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation
    ///
    /// Cut out of SseClient on 2026-09-08 (step 6 of analyse/plan-prealables-couches.md).
    /// </summary>
    public sealed class SseStream
    {
        private readonly TextReader _reader;

        public SseStream(TextReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        /// <summary>
        /// What time it is. Handed in so a check can reach the heartbeat timeout without waiting a
        /// minute for it — the timeout is a rule about elapsed time, not about a real clock.
        /// </summary>
        public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

        /// <summary>
        /// How often the reader wakes up on a silent stream, only to ask whether
        /// <see cref="HeartbeatTimeoutMs"/> has passed.
        ///
        /// ⚠ Deliberately well under the relay's own heartbeat rather than equal to it. It is a
        /// TICK, not a deadline: nothing is given up when it fires.
        /// </summary>
        public int TickMs { get; set; } = 5000;

        /// <summary>How long a live stream may stay silent before it is treated as gone.</summary>
        public int HeartbeatTimeoutMs { get; set; } = 60000;

        /// <summary>
        /// Stop once one event has been handed over.
        ///
        /// 🔴 Set for the device flow, whose stream carries exactly one `authorized` and is then
        /// closed by the server on purpose. Without this the deliberate close was read as a lost
        /// connection and announced as one — over the top of the success message.
        /// </summary>
        public bool StopAfterFirstEvent { get; set; }

        /// <summary>Where an event goes. Its exceptions are caught: a bad handler is not a bad stream.</summary>
        public Action<SseEvent> OnEvent { get; set; }

        /// <summary>What is worth saying out loud — a timeout, a handler that threw.</summary>
        public Action<string> Warning { get; set; }

        /// <summary>The last id seen, for the Last-Event-ID header on a reconnection.</summary>
        public string LastEventId { get; private set; }

        /// <summary>What the server asked the client to wait before reconnecting, when it said.</summary>
        public int? RetryDelayMs { get; private set; }

        /// <summary>
        /// Read until the stream ends, goes quiet for too long, or delivers what it was opened for.
        /// </summary>
        public async Task<SseStopReason> Run(CancellationToken ct)
        {
            string eventType = null;
            var dataLines = new List<string>();
            string eventId = null;
            DateTime lastData = Now();

            // 🔴 **ONE read at a time, and that is the whole reason this lives outside the loop.**
            // ReadLineAsync must not be called again while a previous call is still pending on the
            // same stream: .NET answers "The stream is currently in use by a previous operation on
            // the stream", the connection dies, and every reconnection meets the same wall.
            Task<string> readTask = null;

            while (!ct.IsCancellationRequested)
            {
                if ((Now() - lastData).TotalMilliseconds > HeartbeatTimeoutMs)
                {
                    Warning?.Invoke("[SSE] Heartbeat timeout, reconnecting...");
                    return SseStopReason.HeartbeatTimeout;
                }

                string line;
                try
                {
                    // Started only when there is no read in flight. ReadLineAsync waits for data,
                    // for the end of the stream, or for the connection to drop.
                    if (readTask == null) readTask = _reader.ReadLineAsync();

                    // A tick, so the heartbeat timeout above is reached even on a silent stream.
                    var completed = await Task.WhenAny(readTask, Task.Delay(TickMs, ct));
                    if (completed != readTask)
                    {
                        // Nothing yet. The read stays pending and is picked up again next time
                        // round — replacing it is what broke this.
                        continue;
                    }

                    // Awaited rather than read through .Result: a faulted read hands back its own
                    // exception here, where the catches below can recognise it, instead of an
                    // AggregateException that matches none of them.
                    line = await readTask;
                    readTask = null;
                }
                catch (OperationCanceledException)
                {
                    return SseStopReason.Cancelled;
                }
                catch (IOException)
                {
                    return SseStopReason.Closed;
                }

                if (line == null) return SseStopReason.EndOfStream;

                lastData = Now();

                // Empty line = dispatch event
                if (line.Length == 0)
                {
                    if (dataLines.Count > 0)
                    {
                        var evt = new SseEvent
                        {
                            Id = eventId,
                            EventType = eventType ?? "message",
                            Data = string.Join("\n", dataLines)
                        };

                        if (!string.IsNullOrEmpty(eventId)) LastEventId = eventId;

                        try
                        {
                            OnEvent?.Invoke(evt);
                        }
                        catch (Exception ex)
                        {
                            Warning?.Invoke($"[SSE] Event handler error: {ex.Message}");
                        }

                        // Handed over, and this stream was only ever going to carry the one.
                        if (StopAfterFirstEvent) return SseStopReason.Delivered;
                    }

                    // Reset for next event
                    eventType = null;
                    dataLines.Clear();
                    eventId = null;
                    continue;
                }

                // Comment line (heartbeat)
                if (line[0] == ':') continue;

                // Parse field:value
                int colonIndex = line.IndexOf(':');
                string field, value;
                if (colonIndex >= 0)
                {
                    field = line.Substring(0, colonIndex);
                    value = colonIndex + 1 < line.Length ? line.Substring(colonIndex + 1) : "";
                    // Remove single leading space after colon (per spec)
                    if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);
                }
                else
                {
                    field = line;
                    value = "";
                }

                switch (field)
                {
                    case "event":
                        eventType = value;
                        break;
                    case "data":
                        dataLines.Add(value);
                        break;
                    case "id":
                        // Ignore IDs containing null (per spec)
                        if (!value.Contains("\0")) eventId = value;
                        break;
                    case "retry":
                        if (int.TryParse(value, out int retryMs) && retryMs >= 0) RetryDelayMs = retryMs;
                        break;
                }
            }

            return SseStopReason.Cancelled;
        }
    }
}
